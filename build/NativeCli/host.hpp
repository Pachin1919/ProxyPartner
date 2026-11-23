#pragma once

#include <map>
#include <string>
#include <string_view>

struct NativeSample { int delay; std::string observed; };
struct NativeHost {
    const char* state = "HostUnavailable";
    std::string version, current;
    std::map<std::pair<std::string, std::string>, NativeSample> samples;
};
NativeHost read_native_host(std::wstring_view configuredPipe);
