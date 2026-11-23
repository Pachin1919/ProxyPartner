#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <windns.h>
#include <winhttp.h>
#include <openssl/crypto.h>
#include <openssl/evp.h>
#include <yaml.h>
#include <nlohmann/json.hpp>

#include "age.hpp"
#include "host.hpp"
#include <algorithm>
#include <array>
#include <atomic>
#include <charconv>
#include <chrono>
#include <cmath>
#include <filesystem>
#include <fstream>
#include <future>
#include <iostream>
#include <map>
#include <memory>
#include <mutex>
#include <numeric>
#include <optional>
#include <set>
#include <string>
#include <thread>
#include <vector>

namespace {
using Json = nlohmann::json;
using Clock = std::chrono::steady_clock;
using namespace std::chrono_literals;
namespace fs = std::filesystem;
constexpr size_t fileLimit = 2 * 1024 * 1024;
struct Target { const char* id; const char* host; const char* path; const char* headers; bool critical; };
constexpr std::array<Target, 5> targets{{
    {"chatgpt_web", "chatgpt.com", "/", "", true},
    {"openai_api", "api.openai.com", "/v1/models", "", true},
    {"google", "www.google.com", "/", "", false},
    {"youtube", "www.youtube.com", "/", "", false},
    {"anthropic_api", "api.anthropic.com", "/v1/models", "anthropic-version: 2026-10-21\r\n", true},
}};

std::mutex outputGate;
HANDLE cancelEvent;
BOOL WINAPI interrupt(DWORD type) {
    if (type != CTRL_C_EVENT && type != CTRL_BREAK_EVENT) return FALSE;
    return SetEvent(cancelEvent);
}
bool cancelled() { return WaitForSingleObject(cancelEvent, 0) == WAIT_OBJECT_0; }
void write_event(const Json& event) {
    std::lock_guard lock(outputGate);
    std::cout << event.dump(-1, ' ', false, Json::error_handler_t::replace) << '\n' << std::flush;
}
int fail(std::string_view state) { write_event({{"event", "error"}, {"state", state}}); return 2; }

std::wstring wide(std::string_view text) {
    if (text.empty()) return {};
    const auto size = static_cast<int>(text.size());
    const int count = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), size, nullptr, 0);
    std::wstring result(static_cast<size_t>(count), L'\0');
    if (count) MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text.data(), size, result.data(), count);
    return result;
}
std::string utf8(std::wstring_view text) {
    if (text.empty()) return {};
    const auto size = static_cast<int>(text.size());
    const int count = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), size, nullptr, 0, nullptr, nullptr);
    std::string result(static_cast<size_t>(count), '\0');
    if (count) WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), size, result.data(), count, nullptr, nullptr);
    return result;
}
std::optional<std::string> read_file(const fs::path& path) {
    std::ifstream file(path, std::ios::binary | std::ios::ate);
    const auto size = file.tellg();
    if (size <= 0 || size > static_cast<std::streamoff>(fileLimit)) return std::nullopt;
    std::string bytes(static_cast<size_t>(size), '\0');
    file.seekg(0);
    if (!file.read(bytes.data(), bytes.size())) return std::nullopt;
    return bytes;
}
std::optional<unsigned> integer(std::string_view value) {
    unsigned number{};
    auto [end, error] = std::from_chars(value.data(), value.data() + value.size(), number);
    if (error != std::errc{} || end != value.data() + value.size()) return std::nullopt;
    return number;
}

enum class Protocol { AnyTls, Mieru, Shadowsocks, Unsupported };
struct Node {
    std::string name, server, port, username, password, sni, ech, pattern, multiplexing, handshake, obfs;
    Protocol protocol = Protocol::Unsupported;
    Node() = default;
    Node(Node&&) noexcept = default;
    Node& operator=(Node&&) noexcept = default;
    ~Node() { if (!password.empty()) OPENSSL_cleanse(password.data(), password.size()); }
};
const char* protocol_name(Protocol protocol) {
    switch (protocol) {
    case Protocol::AnyTls: return "AnyTLS";
    case Protocol::Mieru: return "Mieru";
    case Protocol::Shadowsocks: return "Shadowsocks";
    default: return "Unsupported";
    }
}
struct DnsRule { std::string suffix; bool wildcard; std::vector<std::string> urls; };
struct Profile { std::vector<Node> nodes; std::vector<DnsRule> dns; std::string pipe; };

std::string scalar(const yaml_node_t* value) {
    if (!value || value->type != YAML_SCALAR_NODE) return {};
    return {reinterpret_cast<const char*>(value->data.scalar.value), value->data.scalar.length};
}
yaml_node_t* field(yaml_document_t& doc, const yaml_node_t* map, std::string_view key) {
    if (!map || map->type != YAML_MAPPING_NODE) return nullptr;
    for (auto entry = map->data.mapping.pairs.start; entry != map->data.mapping.pairs.top; ++entry)
        if (scalar(yaml_document_get_node(&doc, entry->key)) == key)
            return yaml_document_get_node(&doc, entry->value);
    return nullptr;
}
bool fields(yaml_document_t& doc, const yaml_node_t* map, std::initializer_list<std::string_view> allowed) {
    if (!map || map->type != YAML_MAPPING_NODE) return false;
    std::set<std::string> seen;
    for (auto entry = map->data.mapping.pairs.start; entry != map->data.mapping.pairs.top; ++entry) {
        auto key = scalar(yaml_document_get_node(&doc, entry->key));
        if (std::find(allowed.begin(), allowed.end(), key) == allowed.end() || !seen.insert(std::move(key)).second)
            return false;
    }
    return true;
}
bool port_ok(std::string_view value) {
    const auto port = integer(value);
    return port && *port >= 1 && *port <= 65535;
}
std::optional<Profile> parse_profile(std::string_view text) {
    yaml_parser_t parser{};
    yaml_document_t document{};
    if (!yaml_parser_initialize(&parser)) return std::nullopt;
    yaml_parser_set_input_string(&parser, reinterpret_cast<const unsigned char*>(text.data()), text.size());
    bool loaded = yaml_parser_load(&parser, &document) != 0;
    yaml_parser_delete(&parser);
    if (!loaded) return std::nullopt;
    const std::unique_ptr<yaml_document_t, decltype(&yaml_document_delete)> owner(&document, yaml_document_delete);
    const auto root = yaml_document_get_root_node(&document);
    const auto proxies = field(document, root, "proxies");
    if (!proxies || proxies->type != YAML_SEQUENCE_NODE) return std::nullopt;
    Profile profile;
    profile.pipe = scalar(field(document, root, "external-controller-pipe"));
    std::map<std::string, size_t> seen;
    for (auto item = proxies->data.sequence.items.start; item != proxies->data.sequence.items.top && profile.nodes.size() < 1000; ++item) {
        auto entry = yaml_document_get_node(&document, *item);
        auto value = [&](std::string_view key) { return scalar(field(document, entry, key)); };
        Node node;
        node.name = value("name");
        if (node.name.empty() || node.name.size() > 1024) continue;
        const auto [previous, inserted] = seen.emplace(node.name, profile.nodes.size());
        if (!inserted) { profile.nodes[previous->second].protocol = Protocol::Unsupported; continue; }
        node.server = value("server");
        node.port = value("port");
        node.password = value("password");
        const auto type = value("type");
        if (!node.server.empty() && node.server.size() <= 253 && !node.password.empty() &&
            node.password.size() <= 4096 && node.password.find('\0') == std::string::npos) {
            if (type == "anytls" && port_ok(node.port) &&
                fields(document, entry, {"name","type","server","port","password","udp","sni","alpn",
                    "client-fingerprint","fingerprint","skip-cert-verify","ech-opts"}) &&
                value("client-fingerprint").empty() && value("fingerprint").empty() &&
                (value("skip-cert-verify").empty() || value("skip-cert-verify") == "false")) {
                const auto alpn = field(document, entry, "alpn");
                auto ech = field(document, entry, "ech-opts");
                auto enable = scalar(field(document, ech, "enable"));
                node.ech = scalar(field(document, ech, "config"));
                node.sni = value("sni");
                if (node.sni.empty()) node.sni = node.server;
                if ((!alpn || (alpn->type == YAML_SEQUENCE_NODE && alpn->data.sequence.items.top == alpn->data.sequence.items.start)) &&
                    (!ech || fields(document, ech, {"enable","config"})) &&
                    ((enable == "true" && !node.ech.empty() && node.ech.size() <= 4096) ||
                        ((enable.empty() || enable == "false") && node.ech.empty())))
                    node.protocol = Protocol::AnyTls;
            }
            if (type == "mieru" && value("transport") == "TCP" &&
                fields(document, entry, {"name","type","server","port","port-range","password","udp","username",
                    "transport","traffic-pattern","multiplexing","handshake-mode"})) {
                node.username = value("username");
                node.pattern = value("traffic-pattern");
                node.multiplexing = value("multiplexing");
                node.handshake = value("handshake-mode");
                const auto range = value("port-range");
                const auto hyphen = range.find('-');
                bool endpoint = port_ok(node.port) && range.empty();
                if ((node.port.empty() || node.port == "0") && hyphen != range.npos) {
                    auto start = std::string_view(range).substr(0, hyphen), end = std::string_view(range).substr(hyphen + 1);
                    endpoint = port_ok(start) && port_ok(end) && *integer(start) <= *integer(end);
                    if (endpoint) node.port = range;
                }
                constexpr std::string_view supportedPattern = "CKPfirUDEAAaBAgAEAAiCAgCEAEYCSAJKgUIABD/ATICCAA=";
                if (endpoint && !node.username.empty() && node.username.size() <= 255 && node.password.size() <= 1024 &&
                    node.username.find('\0') == node.username.npos && node.handshake.empty() &&
                    ((node.pattern.empty() && node.multiplexing.empty()) ||
                        (node.pattern == supportedPattern && (node.multiplexing.empty() ||
                        node.multiplexing == "MULTIPLEXING_HIGH" || node.multiplexing == "MULTIPLEXING_LOW"))))
                    node.protocol = Protocol::Mieru;
            }
            if (type == "ss" && port_ok(node.port) && value("cipher") == "chacha20-ietf-poly1305" &&
                value("plugin") == "obfs" &&
                fields(document, entry, {"name","type","server","port","cipher","password","plugin","plugin-opts","udp","client-fingerprint"})) {
                auto opts = field(document, entry, "plugin-opts");
                node.obfs = scalar(field(document, opts, "host"));
                if (node.obfs.empty()) node.obfs = "bing.com";
                if (fields(document, opts, {"mode","host"}) && scalar(field(document, opts, "mode")) == "tls" &&
                    node.obfs.size() <= 253 && std::all_of(node.obfs.begin(), node.obfs.end(), [](char ch) { return ch >= '!' && ch <= '~'; }))
                    node.protocol = Protocol::Shadowsocks;
            }
        }
        profile.nodes.push_back(std::move(node));
    }
    auto policy = field(document, field(document, root, "dns"), "nameserver-policy");
    if (policy && policy->type == YAML_MAPPING_NODE) {
        for (auto item = policy->data.mapping.pairs.start; item != policy->data.mapping.pairs.top; ++item) {
            auto suffix = scalar(yaml_document_get_node(&document, item->key));
            auto value = yaml_document_get_node(&document, item->value);
            const bool wildcard = suffix.starts_with("+.");
            if (wildcard) suffix.erase(0, 2);
            if (suffix.empty()) continue;
            DnsRule rule{std::move(suffix), wildcard, {}};
            auto add = [&](std::string url) {
                if (!url.starts_with("https://") || url.find_first_of("?@") != url.npos || url.size() > 512 || rule.urls.size() == 8) return;
                if (const auto hash = url.find('#'); hash != url.npos) {
                    std::string_view flags(url.data() + hash + 1, url.size() - hash - 1);
                    while (!flags.empty()) {
                        const auto sep = flags.find('&');
                        auto flag = flags.substr(0, sep);
                        if (flag != "h3=true" && flag != "skip-cert-verify=true") return;
                        flags.remove_prefix(sep == flags.npos ? flags.size() : sep + 1);
                    }
                    url.resize(hash);
                }
                rule.urls.push_back(std::move(url));
            };
            if (value->type == YAML_SEQUENCE_NODE)
                for (auto url = value->data.sequence.items.start; url != value->data.sequence.items.top; ++url)
                    add(scalar(yaml_document_get_node(&document, *url)));
            else add(scalar(value));
            profile.dns.push_back(std::move(rule));
        }
    }
    return profile;
}

struct HttpClose { void operator()(void* handle) const { WinHttpCloseHandle(handle); } };
using Http = std::unique_ptr<void, HttpClose>;
std::string numeric_address(int family, const void* address) {
    char text[INET6_ADDRSTRLEN]{};
    return InetNtopA(family, address, text, sizeof(text)) ? text : "";
}
std::string dns_https(std::string_view url, const std::string& host, WORD type) {
    URL_COMPONENTS parts{};
    parts.dwStructSize = sizeof(parts);
    parts.dwHostNameLength = parts.dwUrlPathLength = static_cast<DWORD>(-1);
    const auto endpoint = wide(url);
    if (!WinHttpCrackUrl(endpoint.c_str(), static_cast<DWORD>(endpoint.size()), 0, &parts)) return {};
    Http session(WinHttpOpen(L"ProxyPartner", WINHTTP_ACCESS_TYPE_NO_PROXY, nullptr, nullptr, 0));
    if (!session || !WinHttpSetTimeouts(session.get(), 3000, 3000, 3000, 3000)) return {};
    DWORD protocols = WINHTTP_PROTOCOL_FLAG_HTTP2;
    if (!WinHttpSetOption(session.get(), WINHTTP_OPTION_ENABLE_HTTP_PROTOCOL, &protocols, sizeof(protocols)))
        return {};
    Http connection(WinHttpConnect(session.get(), std::wstring(parts.lpszHostName, parts.dwHostNameLength).c_str(), parts.nPort, 0));
    if (!connection) return {};
    alignas(DNS_MESSAGE_BUFFER) std::array<unsigned char, 512> question{};
    question[0] = 0x4e; question[1] = 0xf1; question[2] = 1; question[5] = 1;
    size_t position = 12;
    auto name = std::string_view(host);
    if (name.ends_with('.')) name.remove_suffix(1);
    while (!name.empty()) {
        const auto sep = name.find('.');
        const auto label = name.substr(0, sep);
        if (label.empty() || label.size() > 63 || position + label.size() + 6 > question.size() ||
            std::any_of(label.begin(), label.end(), [](char ch) { return ch <= ' ' || ch >= 127; })) return {};
        question[position++] = static_cast<unsigned char>(label.size());
        for (char ch : label) question[position++] = static_cast<unsigned char>(ch);
        name.remove_prefix(sep == name.npos ? name.size() : sep + 1);
    }
    ++position;
    question[position++] = static_cast<unsigned char>(type >> 8);
    question[position++] = static_cast<unsigned char>(type);
    ++position; question[position++] = 1;
    std::array<unsigned char, 700> encoded{};
    auto length = EVP_EncodeBlock(encoded.data(), question.data(), static_cast<int>(position));
    while (length > 0 && encoded[static_cast<size_t>(length - 1)] == '=') --length;
    std::wstring path(parts.lpszUrlPath, parts.dwUrlPathLength);
    if (path.empty()) path = L"/";
    path += L"?dns=";
    for (int i = 0; i < length; ++i) {
        const auto ch = encoded[static_cast<size_t>(i)];
        path += ch == '+' ? L'-' : ch == '/' ? L'_' : static_cast<wchar_t>(ch);
    }
    Http request(WinHttpOpenRequest(connection.get(), L"GET", path.c_str(),
        nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, WINHTTP_FLAG_SECURE));
    if (!request) return {};
    DWORD disable = WINHTTP_DISABLE_REDIRECTS;
    if (!WinHttpSetOption(request.get(), WINHTTP_OPTION_DISABLE_FEATURE, &disable, sizeof(disable))) return {};
    if (!WinHttpSendRequest(request.get(), L"Accept: application/dns-message\r\n", static_cast<DWORD>(-1),
        nullptr, 0, 0, 0) || !WinHttpReceiveResponse(request.get(), nullptr)) return {};
    DWORD status{}, statusSize = sizeof(status);
    if (!WinHttpQueryHeaders(request.get(), WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, nullptr, &status, &statusSize, nullptr) ||
        status != 200) return {};
    alignas(DNS_MESSAGE_BUFFER) std::array<unsigned char, 8192> body{};
    DWORD total = 0, count = 0;
    do {
        if (total == body.size() || !WinHttpReadData(request.get(), body.data() + total,
            static_cast<DWORD>(body.size()) - total, &count)) return {};
        total += count;
    } while (count);
    if (total < sizeof(DNS_HEADER) || body[0] != question[0] || body[1] != question[1] ||
        (body[2] & 0x82u) != 0x80u || (body[3] & 15u) != 0) return {};
    auto message = reinterpret_cast<PDNS_MESSAGE_BUFFER>(body.data());
    DNS_BYTE_FLIP_HEADER_COUNTS(&message->MessageHead);
    PDNS_RECORD records{};
    if (DnsExtractRecordsFromMessage_UTF8(message, static_cast<WORD>(total), &records) != ERROR_SUCCESS) return {};
    std::string address;
    std::string requested = host;
    for (unsigned depth = 0; depth < 8 && address.empty(); ++depth) {
        const auto original = requested;
        for (auto record = records; record; record = record->pNext) {
            if (record->Flags.S.Section != DnsSectionAnswer || _stricmp(record->pName, requested.c_str()) != 0) continue;
            if (record->wType == DNS_TYPE_A) address = numeric_address(AF_INET, &record->Data.A.IpAddress);
            if (record->wType == DNS_TYPE_AAAA) address = numeric_address(AF_INET6, &record->Data.AAAA.Ip6Address);
            if (record->wType == DNS_TYPE_CNAME) requested = record->Data.CNAME.pNameHost;
            if (!address.empty()) break;
        }
        if (requested == original) break;
    }
    DnsRecordListFree(records, DnsFreeRecordList);
    return address;
}

class Resolver {
    const std::vector<DnsRule>& rules_;
    std::mutex mutex_;
    std::map<std::string, std::shared_future<std::string>> results_;

    std::string query(const std::string& server) const {
        IN6_ADDR ipv6{};
        IN_ADDR ipv4{};
        if (InetPtonA(AF_INET, server.c_str(), &ipv4) == 1 || InetPtonA(AF_INET6, server.c_str(), &ipv6) == 1) return server;
        const DnsRule* match = nullptr;
        for (const auto& rule : rules_) {
            if (_stricmp(server.c_str(), rule.suffix.c_str()) == 0) { match = &rule; if (!rule.wildcard) break; }
            else if (rule.wildcard && server.size() > rule.suffix.size() &&
                _stricmp(server.c_str() + server.size() - rule.suffix.size(), rule.suffix.c_str()) == 0 &&
                server[server.size() - rule.suffix.size() - 1] == '.' && (!match || rule.suffix.size() > match->suffix.size())) match = &rule;
        }
        if (match) {
            for (const WORD type : std::array<WORD, 2>{DNS_TYPE_A, DNS_TYPE_AAAA}) {
                for (const auto& url : match->urls) {
                    if (cancelled()) return {};
                    if (auto address = dns_https(url, server, type); !address.empty()) return address;
                }
            }
            return {};
        }
        addrinfo hints{}, *found{};
        hints.ai_socktype = SOCK_STREAM;
        if (getaddrinfo(server.c_str(), nullptr, &hints, &found)) return {};
        std::string address;
        for (auto item = found; item && address.empty(); item = item->ai_next) {
            if (item->ai_family == AF_INET) address = numeric_address(AF_INET, &reinterpret_cast<sockaddr_in*>(item->ai_addr)->sin_addr);
            if (item->ai_family == AF_INET6) address = numeric_address(AF_INET6, &reinterpret_cast<sockaddr_in6*>(item->ai_addr)->sin6_addr);
        }
        freeaddrinfo(found);
        return address;
    }
public:
    explicit Resolver(const std::vector<DnsRule>& rules) : rules_(rules) {}
    std::string operator()(const std::string& server) {
        std::shared_future<std::string> future;
        std::promise<std::string> promise;
        bool owner = false;
        {
            std::lock_guard lock(mutex_);
            const auto [it, inserted] = results_.try_emplace(server);
            owner = inserted;
            if (owner) it->second = promise.get_future().share();
            future = it->second;
        }
        if (owner) promise.set_value(query(server));
        return future.get();
    }
};

struct Library {
    HMODULE handle{};
    using Init = int (*)(const char*);
    using Shutdown = void (*)();
    Shutdown shutdown{};
    bool load(const fs::path& module, const fs::path& ca) {
        handle = LoadLibraryExW(module.c_str(), nullptr, LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR | LOAD_LIBRARY_SEARCH_SYSTEM32);
        if (!handle) return false;
        const auto initialize = entry<Init>("ProbeInitialize");
        auto stop = entry<Shutdown>("ProbeShutdown");
        if (!initialize || !stop || !initialize(utf8(ca.native()).c_str())) return false;
        shutdown = stop;
        return true;
    }
    template<class Function> Function entry(const char* name) const {
        return reinterpret_cast<Function>(GetProcAddress(handle, name));
    }
    ~Library() { if (shutdown) shutdown(); if (handle) FreeLibrary(handle); }
};

struct Adapters {
    using AnyTls = int (*)(const char*, const char*, const char*, const char*, const char*, const char*,
        const char*, const char*, unsigned, HANDLE, int*, int*);
    using Mieru = int (*)(const char*, const char*, const char*, const char*, const char*,
        const char*, const char*, unsigned, HANDLE, int*);
    using MieruV2 = int (*)(const char*, const char*, const char*, const char*, const char*, const char*,
        const char*, const char*, const char*, const char*, unsigned, HANDLE, int*);
    using Shadowsocks = int (*)(const char*, const char*, const char*, const char*, const char*, const char*,
        const char*, unsigned, HANDLE, int*);
    Library anytls, mieru, shadowsocks;
    AnyTls any{};
    Mieru basic{};
    MieruV2 shaped{};
    Shadowsocks ss{};

    Adapters(const fs::path& root, const std::vector<Node>& nodes) {
        auto needed = [&nodes](Protocol type) { return std::any_of(nodes.begin(), nodes.end(),
            [type](const Node& n) { return n.protocol == type; }); };
        const auto ca = root / L"probe-ca-bundle.crt";
        if (needed(Protocol::AnyTls) && anytls.load(root / L"ProxyPartner.AnyTlsProbe.dll", ca))
            any = anytls.entry<AnyTls>("ProbeAnyTlsWebsite");
        if (needed(Protocol::Mieru) && mieru.load(root / L"ProxyPartner.MieruProbe.dll", ca)) {
            basic = mieru.entry<Mieru>("ProbeMieruWebsite");
            shaped = mieru.entry<MieruV2>("ProbeMieruWebsiteV2");
        }
        if (needed(Protocol::Shadowsocks) && shadowsocks.load(root / L"ProxyPartner.NativeProbe.dll", ca))
            ss = shadowsocks.entry<Shadowsocks>("ProbeWebsite");
    }
    bool ready(const Node& n) const {
        switch (n.protocol) {
        case Protocol::AnyTls: return any != nullptr;
        case Protocol::Mieru: return n.pattern.empty() ? basic != nullptr : shaped != nullptr;
        case Protocol::Shadowsocks: return ss != nullptr;
        default: return false;
        }
    }
    int measure(const Node& n, const std::string& address, const Target& t, unsigned remaining) const {
        int status = 0, result = 0, ech = 0;
        const char* ip = address.c_str();
        const char* port = n.port.c_str();
        const char* password = n.password.c_str();
        switch (n.protocol) {
        case Protocol::AnyTls:
            result = any(ip, port, password, n.sni.c_str(), n.ech.c_str(), t.host, t.path, t.headers,
                remaining, cancelEvent, &status, &ech);
            break;
        case Protocol::Mieru:
            if (n.pattern.empty())
                result = basic(ip, port, n.username.c_str(), password, t.host, t.path, t.headers, remaining, cancelEvent, &status);
            else
                result = shaped(ip, port, n.username.c_str(), password, n.pattern.c_str(), n.multiplexing.c_str(), n.handshake.c_str(),
                    t.host, t.path, t.headers, remaining, cancelEvent, &status);
            break;
        case Protocol::Shadowsocks:
            result = ss(ip, port, password, n.obfs.c_str(), t.host, t.path, t.headers, remaining, cancelEvent, &status);
            break;
        default: break;
        }
        return result && status >= 100 && status <= 599 ? status : 0;
    }
};

struct Options {
    fs::path profile, identity;
    std::string node;
    bool inspect{}, all{}, kernel{};
    unsigned minimum = 4, maximum = 8, rounds = 1, timeout = 0;
};
std::optional<Options> arguments(int argc, wchar_t** argv) {
    Options out;
    const std::wstring_view command = argv[1];
    if (command != L"probe" && command != L"inspect") return std::nullopt;
    out.inspect = command == L"inspect";
    std::set<std::wstring_view> seen;
    for (int index = 2; index < argc; ++index) {
        std::wstring_view option = argv[index];
        if (!seen.insert(option).second) return std::nullopt;
        if (option == L"--all") { out.all = true; continue; }
        if (option == L"--kernel-latency") { out.kernel = true; continue; }
        if (++index == argc) return std::nullopt;
        std::wstring_view value = argv[index];
        if (option == L"--profile") out.profile = value;
        else if (option == L"--age-identity") out.identity = value;
        else if (option == L"--node") out.node = utf8(value);
        else {
            const auto n = integer(utf8(value));
            if (!n || *n == 0) return std::nullopt;
            if (option == L"--rounds" && *n <= 6) out.rounds = *n;
            else if (option == L"--parallel" && *n <= 12) out.minimum = out.maximum = *n;
            else if (option == L"--min-parallel" && *n <= 12) out.minimum = *n;
            else if (option == L"--max-parallel" && *n <= 12) out.maximum = *n;
            else if (option == L"--node-timeout-ms" && *n <= 6000) out.timeout = *n;
            else return std::nullopt;
        }
    }
    if (out.profile.empty() || out.minimum > out.maximum ||
        (seen.contains(L"--parallel") && (seen.contains(L"--min-parallel") || seen.contains(L"--max-parallel"))) ||
        (out.inspect && seen.size() != (out.identity.empty() ? 1u : 2u)) ||
        (!out.inspect && out.all == !out.node.empty())) return std::nullopt;
    return out;
}

struct Sample { int status{}, elapsed{}; bool attempted{}, timedOut{}; };
struct Quality { std::optional<int> score; unsigned stable{}; };
Quality score(const std::array<std::vector<Sample>, 5>& history) {
    double total = 0;
    int cap = 100;
    unsigned stable = 0;
    for (size_t index = 0; index < targets.size(); ++index) {
        const auto& samples = history[index];
        if (samples.empty()) return {};
        double mean = 0, squares = 0;
        unsigned replies = 0;
        for (const auto& sample : samples) {
            if (!sample.status) continue;
            const auto delta = sample.elapsed - mean;
            mean += delta / ++replies;
            squares += delta * (sample.elapsed - mean);
        }
        if (replies) {
            const double availability = 50.0 * replies / samples.size();
            const double speed = 35.0 * std::clamp((3000 - mean) / 2800, 0.0, 1.0);
            if (replies == 1) total += (availability + speed) / 0.85;
            else {
                total += availability + speed + 15.0 * std::clamp(1 - std::sqrt((std::max)(0.0, squares / replies)) / 1000, 0.0, 1.0);
                ++stable;
            }
        }
        if (targets[index].critical) {
            const auto last = samples.back();
            if (last.timedOut) cap = (std::min)(cap, 20);
            else if (last.elapsed > 2500) cap = (std::min)(cap, 40);
        }
    }
    return { (std::min)(static_cast<int>(std::nearbyint(total / targets.size())), cap), stable };
}

class Budget {
    std::mutex gate_;
    unsigned total_ = 0, count_ = 0, fixed_;
public:
    explicit Budget(unsigned fixed) : fixed_(fixed) {}
    unsigned timeout() {
        std::lock_guard lock(gate_);
        return fixed_ ? fixed_ : count_ < 4 ? 6000 : (total_ + count_ - 1) / count_;
    }
    void observe(unsigned ms) {
        std::lock_guard lock(gate_);
        if (!fixed_ && count_ < 4) { total_ += std::clamp(ms, 1u, 6000u); ++count_; }
    }
};
struct Stats { std::atomic<unsigned> sites{}, replies{}, completed{}, scored{}; };

bool probe_node(const Node& node, const Options& options, Resolver& resolver, const Adapters& adapters,
    const NativeHost& host, Budget& budget, Stats& stats) {
    const auto type = protocol_name(node.protocol);
    write_event({{"event","node"},{"node",node.name},{"protocol",type},{"state","probing"}});
    const auto addr = resolver(node.server);
    if (addr.empty()) {
        write_event({{"event","node"},{"node",node.name},{"protocol",type},{"state","entry_dns_failed"}});
        return false;
    }
    std::array<std::vector<Sample>, 5> history;
    bool all = true;
    for (unsigned round = 1; round <= options.rounds && !cancelled(); ++round) {
        const auto limit = budget.timeout();
        const auto start = Clock::now();
        const auto end = start + std::chrono::milliseconds(limit);
        std::array<std::future<Sample>, 5> pending;
        for (size_t index = 0; index < targets.size(); ++index) {
            pending[index] = std::async(std::launch::async, [&, index] {
                const auto begin = Clock::now();
                const auto remaining = std::chrono::duration_cast<std::chrono::milliseconds>(end - begin).count();
                if (remaining <= 0 || cancelled()) return Sample{};
                const auto status = adapters.measure(node, addr, targets[index], static_cast<unsigned>(remaining));
                const auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now() - begin).count();
                return Sample{status, static_cast<int>(elapsed), true, !status && elapsed >= remaining};
            });
        }
        Json list = Json::array();
        bool answered = true, expired = false;
        for (size_t index = 0; index < targets.size(); ++index) {
            const auto result = pending[index].get();
            if (result.attempted) { ++stats.sites; history[index].push_back(result); }
            if (result.status) ++stats.replies;
            answered &= result.status != 0;
            expired |= result.timedOut;
            Json site{{"target",targets[index].id},{"attempted",result.attempted},{"timedOut",result.timedOut},
                {"nativeTransport",true},{"retried",false}};
            if (result.status) {
                site["httpStatus"] = result.status;
                site["latencyMs"] = result.elapsed;
            }
            double sum = 0, squareSum = 0;
            unsigned count = 0;
            for (const auto& sample : history[index]) if (sample.status) {
                ++count;
                sum += sample.elapsed;
                squareSum += static_cast<double>(sample.elapsed) * sample.elapsed;
            }
            site["replies"] = count;
            if (count) site["meanMs"] = sum / count;
            if (count > 1) site["standardDeviationMs"] = std::sqrt((std::max)(0.0, squareSum / count - sum / count * (sum / count)));
            list.push_back(std::move(site));
        }
        if (cancelled()) return false;
        const auto duration = static_cast<unsigned>(std::chrono::duration_cast<std::chrono::milliseconds>(Clock::now() - start).count());
        if (answered) budget.observe(duration);
        all &= answered;
        const auto quality = score(history);
        Json observation{{"event","round"},{"node",node.name},{"protocol",type},{"round",round},
            {"stableTargets",quality.stable},{"sites",std::move(list)},{"nodeElapsedMs",duration},
            {"nodeDeadlineMs",limit},{"deadlineReached",expired}};
        if (quality.score) observation["score"] = *quality.score;
        if (const auto sample = host.samples.find({node.name, type}); sample != host.samples.end()) {
            observation["kernelLatencyMs"] = sample->second.delay;
            observation["kernelObservedUtc"] = sample->second.observed;
        }
        write_event(observation);
        if (round == options.rounds) {
            ++stats.completed;
            if (quality.score) ++stats.scored;
        }
    }
    return all;
}
}

int wmain(int argc, wchar_t** argv) {
    if (argc == 1 || (argc == 2 && (std::wstring_view(argv[1]) == L"--help" || std::wstring_view(argv[1]) == L"-h"))) {
        std::cout << "ProxyPartner.Cli inspect --profile <file> [--age-identity <identity>]\n"
            "ProxyPartner.Cli probe --profile <file> [--age-identity <identity>] (--all | --node <name>) "
            "[--rounds 1-6] [--min-parallel 1-12 --max-parallel 1-12 | --parallel 1-12] "
            "[--node-timeout-ms 1-6000] [--kernel-latency]\n";
        return 0;
    }
    SetConsoleOutputCP(CP_UTF8);
    const auto options = arguments(argc, argv);
    if (!options) return fail("invalid_arguments");
    auto bytes = read_file(options->profile);
    if (!bytes) return fail("missing_or_invalid_yaml_proxies");
    std::string plaintext;
    bool encrypted = age_envelope(*bytes);
    if (encrypted && options->identity.empty()) return fail("age_identity_required");
    if (encrypted && !age_open(*bytes, options->identity, plaintext)) return fail("age_decrypt_failed");
    if (bytes->starts_with("#KJXENC1")) return fail("encrypted_profile");
    auto& input = encrypted ? plaintext : *bytes;
    auto profile = parse_profile(input);
    OPENSSL_cleanse(input.data(), input.size());
    if (!profile || profile->nodes.empty()) return fail("missing_or_invalid_yaml_proxies");

    std::wstring executable(32768, L'\0');
    const auto length = GetModuleFileNameW(nullptr, executable.data(), static_cast<DWORD>(executable.size()));
    if (!length || length >= executable.size()) return fail("package_path_unavailable");
    executable.resize(length);
    WSADATA winsock{};
    if (WSAStartup(MAKEWORD(2, 2), &winsock)) return fail("network_initialization_failed");
    struct Sockets { ~Sockets() { WSACleanup(); } } sockets;
    Adapters adapters(fs::path(executable).parent_path(), profile->nodes);
    size_t count = 0, eligible = 0;
    for (const auto& node : profile->nodes) if (options->node.empty() || options->node == node.name) {
        ++count;
        if (adapters.ready(node)) ++eligible;
    }
    if (!count) return fail("node_not_found");
    write_event({{"event","catalog"},{"nodes",count},{"eligible",eligible}});
    if (options->inspect) {
        for (const auto& node : profile->nodes) write_event({{"event","node"},{"node",node.name},
            {"protocol",protocol_name(node.protocol)},{"state",adapters.ready(node) ? "ready" : "unsupported_node"}});
        return 0;
    }
    cancelEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!cancelEvent) return fail("cancel_event_failed");
    SetConsoleCtrlHandler(interrupt, TRUE);
    const auto start = Clock::now();
    Resolver resolver(profile->dns);
    Stats stats;
    const auto host = options->kernel ? read_native_host(wide(profile->pipe)) : NativeHost{};
    if (options->kernel) write_event({{"event","kernel"},{"state",host.state},{"version",host.version},
        {"globalSelection",host.current}});
    std::array<Budget, 3> budgets{Budget(options->timeout), Budget(options->timeout), Budget(options->timeout)};
    std::vector<const Node*> queue;
    for (const auto& node : profile->nodes) {
        if (!options->node.empty() && options->node != node.name) continue;
        if (adapters.ready(node)) queue.push_back(&node);
        else write_event({{"event","node"},{"node",node.name},{"protocol",protocol_name(node.protocol)},{"state","unsupported_node"}});
    }
    unsigned limit = options->minimum, peak = 0;
    size_t next = 0;
    std::vector<std::future<bool>> active;
    while ((!cancelled() && next < queue.size()) || !active.empty()) {
        while (!cancelled() && next < queue.size() && active.size() < limit) {
            const auto node = queue[next++];
            active.push_back(std::async(std::launch::async, [&, node] {
                return probe_node(*node, *options, resolver, adapters, host, budgets[static_cast<size_t>(node->protocol)], stats);
            }));
        }
        peak = (std::max)(peak, static_cast<unsigned>(active.size()));
        const auto done = std::find_if(active.begin(), active.end(), [](auto& task) {
            return task.wait_for(0ms) == std::future_status::ready;
        });
        if (done == active.end()) { std::this_thread::sleep_for(8ms); continue; }
        const unsigned desired = done->get() ? limit + 1 : limit - 1;
        limit = std::clamp(desired, options->minimum, options->maximum);
        active.erase(done);
    }
    const bool stopped = cancelled();
    SetConsoleCtrlHandler(interrupt, FALSE);
    CloseHandle(cancelEvent);
    if (stopped) { write_event({{"event","error"},{"state","cancelled"}}); return 130; }
    write_event({{"event","summary"},{"nodes",count},{"completed",stats.completed.load()},{"scored",stats.scored.load()},
        {"replies",stats.replies.load()},{"attempts",stats.sites.load()},{"retries",0},{"dnsRetries",0},
        {"minParallel",options->minimum},{"maxParallel",options->maximum},{"parallel",peak},
        {"nativeDeadlineMs",budgets[0].timeout()},{"mieruDeadlineMs",budgets[1].timeout()},
        {"wallSeconds",std::chrono::duration<double>(Clock::now() - start).count()}});
    return stats.scored == count && stats.replies == count * targets.size() * options->rounds ? 0 : 1;
}
