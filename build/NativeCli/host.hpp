#pragma once

#include <map>
#include <string>
#include <string_view>
#include <functional>
#include "current_path.hpp"

struct NativeSample { int delay; std::string observed; };
struct NativeHost {
    const char* state = "HostUnavailable";
    std::string version, current;
    std::map<std::pair<std::string, std::string>, NativeSample> samples;
};
struct CurrentHostAccess {
    std::set<std::string> leaves;
    std::function<bool(unsigned,unsigned)> verifyTcp;
    std::function<std::optional<MatchedCurrentRoute>(const std::string&,unsigned,unsigned,void*,uint64_t)> lookup;
};
NativeHost read_native_host(std::wstring_view configuredPipe, CurrentHostAccess* access = nullptr);
