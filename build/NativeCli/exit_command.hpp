#pragma once
#include "exit_observation.hpp"
#include "exit_state.hpp"

int run_exit_ip(const Options& options,const std::vector<Node>& nodes,Resolver& resolver,const Adapters& adapters,const std::string& revision) {
    const auto found=std::find_if(nodes.begin(),nodes.end(),[&](const Node& node){return node.name==options.node;});
    if(found==nodes.end()) return fail("node_not_found");
    const Node& node=*found;
    if(!((node.protocol==Protocol::Vless||node.protocol==Protocol::Vmess) && adapters.v2rayEcho || node.protocol==Protocol::AnyTls && adapters.anyEcho || node.protocol==Protocol::Mieru && adapters.mieruEcho || node.protocol==Protocol::Shadowsocks && (adapters.ssEchoV2 || adapters.ssEcho && Adapters::legacy_ss(node))))
        return fail("exit_transport_unavailable");
    const auto address=resolver(node.server);
    if(address.empty()) return fail("entry_dns_failed");
    const unsigned timeout=options.timeout?options.timeout:6000;
    write_event({{"event","exit_begin"},{"node",node.name},{"protocol",protocol_name(node.protocol)},
        {"rounds",options.rounds},{"sources",2},{"perSourceTimeoutMs",timeout},{"independent",true}});
    constexpr const char* sources[]={"ipv4.icanhazip.com","api.ipify.org"};
    std::map<int,ExitHistory> history;
    if(!options.exitState.empty()) {
        auto state=load_exit_state(options.exitState,node.name,protocol_name(node.protocol),revision,exit_now_ms());
        if(state) history=std::move(*state);
        write_event({{"event","exit_state_input"},{"state",state?"accepted":"ignored"},{"reason",state?Json(nullptr):Json("invalid_or_different_profile_node")}});
    }
    std::set<int> confirmedFamilies;
    unsigned observations=0,confirmed=0,completed=0;
    for(unsigned round=1;round<=options.rounds && !cancelled();++round) {
        std::array<std::optional<ExitAddress>,2> results;
        std::string observed;
        for(unsigned source=0;source<2 && !cancelled();++source) {
            char body[65]{}; unsigned used=0;int status=0,ech=0,ok=0;
            const auto begin=GetTickCount64();
            if(node.protocol==Protocol::Vless||node.protocol==Protocol::Vmess) {
                auto config=node.v2ray.native();ok=adapters.v2rayEcho(address.c_str(),node.port.c_str(),&config,sources[source],"/","",timeout,cancelEvent,&status,body,sizeof(body),&used);
            } else if(node.protocol==Protocol::AnyTls)
                ok=adapters.anyEcho(address.c_str(),node.port.c_str(),node.password.c_str(),node.sni.c_str(),node.ech.c_str(),
                    sources[source],"/","",timeout,cancelEvent,&status,&ech,body,sizeof(body),&used);
            else if(node.protocol==Protocol::Mieru)
                ok=adapters.mieruEcho(address.c_str(),node.port.c_str(),node.username.c_str(),node.password.c_str(),
                    node.pattern.c_str(),node.multiplexing.c_str(),node.handshake.c_str(),sources[source],"/","",timeout,cancelEvent,
                    &status,body,sizeof(body),&used);
            else if(adapters.ssEchoV2)
                ok=adapters.ssEchoV2(address.c_str(),node.port.c_str(),node.password.c_str(),static_cast<unsigned>(node.password.size()),node.cipher.c_str(),node.obfs.c_str(),
                    sources[source],"/","",timeout,cancelEvent,&status,body,sizeof(body),&used);
            else
                ok=adapters.ssEcho(address.c_str(),node.port.c_str(),node.password.c_str(),node.obfs.c_str(),
                    sources[source],"/","",timeout,cancelEvent,&status,body,sizeof(body),&used);
            auto elapsed=GetTickCount64()-begin;
            SYSTEMTIME utc{};GetSystemTime(&utc);char stamp[32];
            std::snprintf(stamp,sizeof(stamp),"%04u-%02u-%02uT%02u:%02u:%02u.%03uZ",utc.wYear,utc.wMonth,utc.wDay,utc.wHour,utc.wMinute,utc.wSecond,utc.wMilliseconds);
            observed=stamp;
            if(ok && used<=64) results[source]=parse_exit_address(std::string(body,used));
            OPENSSL_cleanse(body,sizeof(body));
            const auto& ip=results[source];
            write_event({{"event","exit_source"},{"node",node.name},{"round",round},{"source",source+1},{"host",sources[source]},
                {"observedUtc",observed},{"httpStatus",status?Json(status):Json(nullptr)},{"elapsedMs",status?Json(elapsed):Json(nullptr)},
                {"timedOut",!ok && !cancelled() && elapsed>=timeout},{"address",ip?Json(ip->value):Json(nullptr)},
                {"family",ip?Json(ip->family):Json(nullptr)},{"state",ip?"public":ok?"invalid_address":status==200?"response_incomplete_or_rejected":status?"http_error":"transport_failed"}});
        }
        for(unsigned i=0;i<2;++i) {
            if(!results[i]) continue;
            const auto& ip=*results[i];
            if(i && results[0] && results[0]->family==ip.family) continue;
            auto corroboration=results[1-i];
            if(corroboration && corroboration->family!=ip.family) corroboration.reset();
            auto row=update_exit_history(history[ip.family],ip,corroboration,observed,exit_now_ms());
            row["event"]="exit_observation";row["node"]=node.name;row["round"]=round;
            ++observations;
            if(row["agreement"]=="confirmed") {++confirmed;confirmedFamilies.insert(ip.family);}
            else confirmedFamilies.erase(ip.family);
            write_event(row);
        }
        ++completed;
    }
    write_event(serialize_exit_state(history,node.name,protocol_name(node.protocol),revision));
    write_event({{"event","exit_summary"},{"node",node.name},{"rounds",completed},{"publicObservations",observations},
        {"confirmedFamilies",confirmedFamilies.size()},{"confirmedObservations",confirmed},{"cancelled",cancelled()}});
    return observations?0:1;
}
