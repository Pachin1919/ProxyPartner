#pragma once

inline uint64_t exit_now_ms() {
    FILETIME time{};GetSystemTimeAsFileTime(&time);
    return ((static_cast<uint64_t>(time.dwHighDateTime)<<32)|time.dwLowDateTime)/10000-11644473600000ULL;
}
inline std::string exit_stamp(uint64_t millis) {
    uint64_t ticks=(millis+11644473600000ULL)*10000;
    FILETIME time{static_cast<DWORD>(ticks),static_cast<DWORD>(ticks>>32)};
    SYSTEMTIME utc{};if(!FileTimeToSystemTime(&time,&utc))return {};
    char stamp[32];
    std::snprintf(stamp,sizeof(stamp),"%04u-%02u-%02uT%02u:%02u:%02u.%03uZ",utc.wYear,utc.wMonth,utc.wDay,utc.wHour,utc.wMinute,utc.wSecond,utc.wMilliseconds);
    return stamp;
}
inline std::optional<std::map<int,ExitHistory>> load_exit_state(const std::filesystem::path& path,
    const std::string& node,const std::string& protocol,const std::string& revision,uint64_t now) {
    using J=nlohmann::json;
    std::ifstream file(path,std::ios::binary);
    if(!file)return {};
    char buffer[8193];file.read(buffer,sizeof(buffer));auto count=file.gcount();
    if(count<=0 || count>8192 || file.bad())return {};
    const auto root=J::parse(buffer,buffer+count,nullptr,false);
    if(!root.is_object() || !root.contains("version") || root["version"]!=1 ||
        !root.contains("node") || root["node"]!=node || !root.contains("protocol") || root["protocol"]!=protocol ||
        !root.contains("profileRevision") || root["profileRevision"]!=revision ||
        !root.contains("families") || !root["families"].is_array() || root["families"].size()>2)return {};
    std::map<int,ExitHistory> result;
    for(const auto& row:root["families"]) {
        if(!row.is_object())return {};
        for(auto key:{"family","changes","confirmedSamples","observedUnixMs","consistentSinceUnixMs"})
            if(!row.contains(key) || !row[key].is_number_unsigned())return {};
        if(!row.contains("address") || !row["address"].is_string() || !row.contains("lastVerifiedAddress") ||
            !(row["lastVerifiedAddress"].is_null() || row["lastVerifiedAddress"].is_string()))return {};
        auto address=parse_exit_address(row["address"].get<std::string>());
        uint64_t family=row["family"].get<uint64_t>(),changes=row["changes"].get<uint64_t>(),confirmed=row["confirmedSamples"].get<uint64_t>();
        uint64_t observed=row["observedUnixMs"].get<uint64_t>(),since=row["consistentSinceUnixMs"].get<uint64_t>();
        if(!address || family!=static_cast<unsigned>(address->family) || result.contains(address->family) ||
            changes>1000000 || confirmed>1000000 || !observed || observed>now || since>observed)return {};
        std::optional<ExitAddress> verified;
        if(row["lastVerifiedAddress"].is_string()) {
            verified=parse_exit_address(row["lastVerifiedAddress"].get<std::string>());
            if(!verified || verified->family!=address->family)return {};
        }
        if(confirmed ? !verified || verified->value!=address->value || !since : since!=0)return {};
        result.emplace(address->family,ExitHistory{address->value,verified?verified->value:"",since?exit_stamp(since):"",
            static_cast<unsigned>(changes),static_cast<unsigned>(confirmed),observed,since});
    }
    return result;
}
inline nlohmann::json serialize_exit_state(const std::map<int,ExitHistory>& history,const std::string& node,
    const std::string& protocol,const std::string& revision) {
    using J=nlohmann::json;
    J rows=J::array();
    for(const auto& [family,state]:history) rows.push_back({{"family",family},{"address",state.address},
        {"lastVerifiedAddress",state.lastVerified.empty()?J(nullptr):J(state.lastVerified)},
        {"changes",state.changes},{"confirmedSamples",state.confirmedSamples},{"observedUnixMs",state.observedTick},
        {"consistentSinceUnixMs",state.consistentSinceMillis}});
    return {{"event","exit_state"},{"version",1},{"node",node},{"protocol",protocol},{"profileRevision",revision},{"families",rows}};
}
