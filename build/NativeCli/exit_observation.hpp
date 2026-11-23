#pragma once

struct ExitAddress { std::string value; int family{}; };
inline std::optional<ExitAddress> parse_exit_address(std::string value) {
    const auto first=value.find_first_not_of(" \t\r\n\f\v");
    if(first==value.npos) return {};
    value=value.substr(first,value.find_last_not_of(" \t\r\n\f\v")-first+1);
    if(value.find('\0')!=value.npos || value.size()>64) return {};
    unsigned char bytes[16]{};int family=AF_INET;
    if(InetPtonA(AF_INET,value.c_str(),bytes)!=1) {
        family=AF_INET6;
        if(InetPtonA(AF_INET6,value.c_str(),bytes)!=1) return {};
        const unsigned char mapped[12]={0,0,0,0,0,0,0,0,0,0,255,255};
        if(!memcmp(bytes,mapped,12)) {memmove(bytes,bytes+12,4);family=AF_INET;}
    }
    const auto* b=bytes;
    const bool publicAddress=family==AF_INET ?
        b[0]>0 && b[0]<224 && b[0]!=10 && b[0]!=127 &&
        !(b[0]==169 && b[1]==254 || b[0]==172 && b[1]>=16 && b[1]<=31 ||
          b[0]==192 && (b[1]==168 || b[1]==0 && (b[2]==0 || b[2]==2)) ||
          b[0]==100 && b[1]>=64 && b[1]<=127 ||
          b[0]==198 && (b[1]==18 || b[1]==19 || b[1]==51 && b[2]==100) ||
          b[0]==203 && b[1]==0 && b[2]==113) :
        (b[0]&0xe0)==0x20 && !(b[0]==0x20 && b[1]==1 && b[2]==0x0d && b[3]==0xb8);
    if(!publicAddress) return {};
    char normalized[46];
    if(!InetNtopA(family,bytes,normalized,sizeof(normalized))) return {};
    return ExitAddress{normalized,family==AF_INET?4:6};
}

struct ExitHistory {
    std::string address,lastVerified,consistentSince;
    unsigned changes{},confirmedSamples{};
    uint64_t observedTick{},consistentSinceMillis{};
};
inline nlohmann::json update_exit_history(ExitHistory& history,const ExitAddress& address,
    const std::optional<ExitAddress>& corroboration,const std::string& now,uint64_t tick) {
    const bool confirmed=corroboration && corroboration->family==address.family && corroboration->value==address.value;
    if(!confirmed) {
        history.address=address.value;
        history.confirmedSamples=0;
        history.consistentSince.clear();
        history.consistentSinceMillis=0;
    } else if(history.confirmedSamples && history.address==address.value && tick>=history.observedTick && tick-history.observedTick<=3600000) {
        ++history.confirmedSamples;
    } else {
        if(!history.lastVerified.empty() && history.lastVerified!=address.value) ++history.changes;
        history.address=history.lastVerified=address.value;
        history.consistentSince=now;
        history.consistentSinceMillis=tick;
        history.confirmedSamples=1;
    }
    history.observedTick=tick;
    return {{"family",address.family},{"address",address.value},{"observedUtc",now},
        {"observers",corroboration?2:1},{"corroboratingAddress",corroboration?nlohmann::json(corroboration->value):nlohmann::json(nullptr)},
        {"agreement",confirmed?"confirmed":corroboration?"conflict":"single_source"},{"changes",history.changes},
        {"lastVerifiedAddress",history.lastVerified.empty()?nlohmann::json(nullptr):nlohmann::json(history.lastVerified)},
        {"consistentSince",history.consistentSince.empty()?nlohmann::json(nullptr):nlohmann::json(history.consistentSince)},
        {"confirmedSamples",history.confirmedSamples}};
}
