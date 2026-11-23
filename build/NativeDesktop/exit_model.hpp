#pragma once

struct ExitSource {
    bool seen = false;
    Site response;
    int family = 0;
    std::wstring address, observed;
};
struct ExitObservation {
    bool valid = false;
    int observers = 0, changes = 0, confirmedSamples = 0;
    std::wstring address, otherAddress, observed, agreement, lastVerified, consistentSince;
};
struct ExitFacts {
    std::array<ExitSource,2> sources;
    std::array<ExitObservation,2> observations;
    std::wstring protocol, notice;
    Json state;
};
// The backend owns public-address classification. The UI accepts bounded literal facts,
// never endpoint/destination addresses or an echo response body.
bool ExitLiteral(const std::wstring& address,int family) {
    if(address.empty() || address.size()>45 || family!=4 && family!=6)return false;
    IN6_ADDR parsed{};
    return InetPtonW(family==4?AF_INET:AF_INET6,address.c_str(),&parsed)==1;
}
bool NormalizeExitState(const Json& value,const std::wstring& node,const std::wstring& protocol,Json& state) {
    if(!value.is_object() || !value.contains("version") || value["version"]!=1 ||
        !value.contains("node") || value["node"]!=Utf8(node) || !value.contains("protocol") || value["protocol"]!=Utf8(protocol) ||
        !value.contains("profileRevision") || !value["profileRevision"].is_string() || value["profileRevision"].get_ref<const std::string&>().size()>256 ||
        !value.contains("families") || !value["families"].is_array() || value["families"].size()>2)return false;
    Json families=Json::array();
    bool seen[2]{};
    for(const auto& row:value["families"]){
        if(!row.is_object())return false;
        for(const char* key:{"family","changes","confirmedSamples","observedUnixMs","consistentSinceUnixMs"})
            if(!row.contains(key) || !row[key].is_number_integer() || row[key]<0)return false;
        int family=row["family"]==4?4:row["family"]==6?6:0;
        if(!family || seen[family==4?0:1] || row["changes"]>1000000 || row["confirmedSamples"]>1000000 ||
            !row.contains("address") || !row["address"].is_string() || row["address"].get_ref<const std::string&>().size()>64 ||
            !ExitLiteral(Wide(row["address"].get<std::string>()),family) || !row.contains("lastVerifiedAddress"))return false;
        if(!row["lastVerifiedAddress"].is_null() && (!row["lastVerifiedAddress"].is_string() ||
            row["lastVerifiedAddress"].get_ref<const std::string&>().size()>64 || !ExitLiteral(Wide(row["lastVerifiedAddress"].get<std::string>()),family)))return false;
        seen[family==4?0:1]=true;
        families.push_back({{"family",family},{"address",row["address"]},{"lastVerifiedAddress",row["lastVerifiedAddress"]},
            {"changes",row["changes"]},{"confirmedSamples",row["confirmedSamples"]},
            {"observedUnixMs",row["observedUnixMs"]},{"consistentSinceUnixMs",row["consistentSinceUnixMs"]}});
    }
    state={{"version",1},{"node",Utf8(node)},{"protocol",Utf8(protocol)},
        {"profileRevision",value["profileRevision"]},{"families",families}};
    return state.dump().size()<=8192;
}
