#pragma once

#include <string>
#include "../../experiments/cpp-v2ray/v2ray.hpp"

enum class TrojanTransport { Tcp, WebSocket, HttpUpgrade };
struct TrojanSettings {
    std::string peerName, path = "/", host, alpn;
    TrojanTransport transport = TrojanTransport::Tcp;
    V2RayConfigV1 native() const {
        V2RayConfigV1 cfg;
        cfg.protocol = 3;
        cfg.transport = static_cast<int>(transport);
        cfg.serverName = peerName.c_str();
        cfg.path = path.c_str();
        cfg.hostHeader = host.c_str();
        cfg.alpn = alpn.c_str();
        return cfg;
    }
};
