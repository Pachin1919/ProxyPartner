#pragma once
#include <optional>
#include <string>
#include <string_view>
#include <vector>

// Restricted manual WinINet policy for HTTPS targets. Unsupported syntax fails closed.
struct ManualProxyPolicy {
    unsigned port{};
    std::vector<std::wstring> bypass;

    bool bypasses(std::wstring_view host) const {
        std::wstring name(host);
        for (auto& c : name) if (c >= L'A' && c <= L'Z') c += L'a' - L'A';
        for (const auto& rule : bypass) {
            if (rule == L"<local>") {
                if (name.find(L'.') == name.npos) return true;
                continue;
            }
            size_t at=0, pattern=0, star=std::wstring::npos, resume=0;
            while (at < name.size()) {
                if (pattern < rule.size() && rule[pattern] == name[at]) { ++pattern; ++at; }
                else if (pattern < rule.size() && rule[pattern] == L'*') { star=pattern++; resume=at; }
                else if (star != std::wstring::npos) { pattern=star+1; at=++resume; }
                else break;
            }
            while (pattern < rule.size() && rule[pattern] == L'*') ++pattern;
            if (at == name.size() && pattern == rule.size()) return true;
        }
        return false;
    }
};

inline std::optional<ManualProxyPolicy> parse_manual_proxy(std::wstring_view proxy, std::wstring_view bypass) {
    auto split=[](std::wstring_view value) {
        std::vector<std::wstring> tokens;
        size_t start=value.find_first_not_of(L"; \t\r\n");
        while (start != value.npos) {
            auto end=value.find_first_of(L"; \t\r\n",start);
            tokens.emplace_back(value.substr(start,end == value.npos ? value.size()-start : end-start));
            start=end == value.npos ? end : value.find_first_not_of(L"; \t\r\n",end);
        }
        return tokens;
    };
    ManualProxyPolicy policy;
    std::wstring endpoint;
    bool mapped=false;
    for (auto item : split(proxy)) {
        const auto equal=item.find(L'=');
        if (equal == item.npos) {
            if (!endpoint.empty() || mapped) return {};
            endpoint=std::move(item);
        } else {
            if (!endpoint.empty() && !mapped) return {};
            mapped=true;
            auto scheme=item.substr(0,equal);
            if (scheme != L"http" && scheme != L"https" && scheme != L"ftp" && scheme != L"socks") return {};
            if (scheme == L"https") {
                if (!endpoint.empty()) return {};
                endpoint=item.substr(equal+1);
            }
        }
    }
    if (endpoint.starts_with(L"http://")) endpoint.erase(0,7);
    if (!endpoint.starts_with(L"127.0.0.1:")) return {};
    const auto number=std::wstring_view(endpoint).substr(10);
    if (number.empty() || number.size()>5) return {};
    for (auto c : number) {
        if (c<L'0' || c>L'9') return {};
        policy.port=policy.port*10+static_cast<unsigned>(c-L'0');
    }
    if (!policy.port || policy.port>65535) return {};
    for (auto rule : split(bypass)) {
        for (auto& c : rule) if (c>=L'A' && c<=L'Z') c+=L'a'-L'A';
        if (rule != L"<local>" && (rule.front()==L'.' || rule.find_first_not_of(L"abcdefghijklmnopqrstuvwxyz0123456789-.*")!=rule.npos)) return {};
        policy.bypass.push_back(std::move(rule));
    }
    return policy;
}
