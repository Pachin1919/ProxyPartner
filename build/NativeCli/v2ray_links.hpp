#pragma once
#include "share_links.hpp"
#include "v2ray_fields.hpp"
namespace v2ray_links {
// No root container ownership. Caller provides one link and its external row name.
inline std::optional<v2ray_fields::Fields> parse(std::string_view link){
    using J=nlohmann::json;
    if(link.size()>4096)return {};
    std::string scheme(link.substr(0,8));std::transform(scheme.begin(),scheme.end(),scheme.begin(),[](char c){return c>='A'&&c<='Z'?static_cast<char>(c+'a'-'A'):c;});
    bool vmess=scheme=="vmess://";if(!vmess&&scheme!="vless://")return {};
    link.remove_prefix(8);v2ray_fields::Fields out;out.vmess=vmess;
    auto body=link.substr(0,link.find('#'));
    if(vmess&&body.find('@')==body.npos){
        auto decoded=share_links::base64(body);if(!decoded)return {};
        auto value=J::parse(*decoded,nullptr,false);OPENSSL_cleanse(decoded->data(),decoded->size());if(!value.is_object())return {};
        for(auto item=value.begin();item!=value.end();++item){static const std::vector<std::string> allowed={"v","ps","remarks","remark","name","add","port","id","aid","scy","security","net","type","host","path","tls","sni","alpn","fp","allowInsecure","skip-cert-verify"};if(std::find(allowed.begin(),allowed.end(),item.key())==allowed.end())return {};}
        auto scalar=[&](const char* key)->std::string{auto p=value.find(key);if(p==value.end())return {};if(p->is_string())return p->get<std::string>();if(p->is_number_integer()||p->is_boolean())return p->dump();return "\r";};
        out.server=scalar("add");out.port=scalar("port");out.uuid=scalar("id");out.network=scalar("net");out.cipher=scalar("scy");if(out.cipher.empty())out.cipher=scalar("security");out.sni=scalar("sni");out.path=scalar("path");out.host=scalar("host");out.alpn=scalar("alpn");
        if(auto alpn=value.find("alpn");alpn!=value.end()&&alpn->is_array()){out.alpn.clear();for(const auto& token:*alpn){if(!token.is_string()||token.get_ref<const std::string&>().empty())return {};if(!out.alpn.empty())out.alpn+=',';out.alpn+=token.get<std::string>();}}
        const auto aid=scalar("aid"),tls=scalar("tls"),type=scalar("type"),insecure=scalar("allowInsecure");
        if(!aid.empty()&&aid!="0" || !type.empty()&&type!="none"&&(out.network.empty()||out.network=="tcp"||out.network=="raw") || !insecure.empty()&&insecure!="false"&&insecure!="0")return {};
        if(!scalar("skip-cert-verify").empty()&&scalar("skip-cert-verify")!="false"&&scalar("skip-cert-verify")!="0")return {};
        if(tls=="tls"||tls=="true")out.security="tls";else if(!tls.empty()&&tls!="none"&&tls!="false")return {};
        // VMess ordinary TLS fp is not applied by the old VmessClient; reject instead of pretending fidelity.
        if(!scalar("fp").empty())return {};
    } else {
        auto ep=share_links::endpoint(link);if(!ep)return {};
        auto id=share_links::unescape(ep->credentials);auto query=share_links::query(ep->query);if(!id||!query)return {};
        out.server=ep->server;out.port=ep->port;out.uuid=*id;
        for(const auto& [key,value]:*query){
            if(key=="type"||key=="network")out.network=value;else if(key=="security")out.security=value;
            else if(key=="sni"||key=="serverName")out.sni=value;else if(key=="path")out.path=value;else if(key=="host")out.host=value;
            else if(key=="alpn")out.alpn=value;else if(key=="fp")out.fingerprint=value;else if(key=="flow")out.flow=value;
            else if(key=="pbk")out.publicKey=value;else if(key=="sid")out.shortId=value;
            else if(key=="encryption"){if(vmess)out.cipher=value;else if(!value.empty()&&value!="none")return {};}
            else if(key=="alterId"){if(value!="0"&&!value.empty())return {};}
            else if(key=="headerType"){if(value!="none"&&!value.empty())return {};}
            else if(key=="allowInsecure"||key=="skip-cert-verify"){if(value!="false"&&value!="0"&&!value.empty())return {};}
            else return {};
        }
    }
    if(!v2ray_fields::validate(out,true))return {};return out;
}
}
