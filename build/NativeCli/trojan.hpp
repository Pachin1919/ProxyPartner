#pragma once

#include <filesystem>
#include <string>
#include <string_view>

enum class TrojanTransport { Tcp, WebSocket, HttpUpgrade };
struct TrojanSettings {
    std::string peerName, path = "/", host, alpn;
    TrojanTransport transport = TrojanTransport::Tcp;
};

struct ssl_ctx_st;
struct bio_method_st;
class TrojanProbe {
    ssl_ctx_st* context{};
    bio_method_st* wire{};
    bio_method_st* stream{};
public:
    explicit TrojanProbe(const std::filesystem::path& ca);
    ~TrojanProbe();
    TrojanProbe(const TrojanProbe&) = delete;
    TrojanProbe& operator=(const TrojanProbe&) = delete;
    bool ready() const { return context && wire && stream; }
    int request(const TrojanSettings& profile, const char* address, const char* port, std::string_view password,
        const char* target, const char* path, const char* headers, unsigned remaining, void* stop,
        char* echo = nullptr, unsigned* used = nullptr) const;
};
