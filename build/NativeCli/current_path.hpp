#pragma once
#include <nlohmann/json.hpp>
#include <optional>
#include <set>
#include <string>

struct MatchedCurrentRoute { std::string node; bool direct{}; };

// Matches only the tuple captured from a live, owned and identity-verified probe.
inline std::optional<MatchedCurrentRoute> match_current_route(const nlohmann::json& body,
    const std::string& host, unsigned destinationPort, unsigned sourcePort,
    const std::set<std::string>& leaves) {
    if (!sourcePort || !body.is_object()) return {};
    const auto connections = body.find("connections");
    if (connections == body.end() || !connections->is_array()) return {};
    auto equals = [](const nlohmann::json& object, const char* key, const std::string& value) {
        auto field = object.find(key);
        return field != object.end() && field->is_string() && field->get_ref<const std::string&>() == value;
    };
    for (const auto& entry : *connections) {
        if (!entry.is_object()) continue;
        auto metadata = entry.find("metadata");
        if (metadata == entry.end() || !metadata->is_object() ||
            !equals(*metadata,"network","tcp") || !equals(*metadata,"sourceIP","127.0.0.1") ||
            !equals(*metadata,"sourcePort",std::to_string(sourcePort)) ||
            !equals(*metadata,"host",host) || !equals(*metadata,"destinationPort",std::to_string(destinationPort))) continue;
        auto chains = entry.find("chains");
        if (chains == entry.end() || !chains->is_array()) return {};
        MatchedCurrentRoute matched;
        for (const auto& chain : *chains) {
            if (!chain.is_string()) return {};
            const auto& name = chain.get_ref<const std::string&>();
            if (name == "DIRECT") matched.direct = true;
            if (!leaves.contains(name)) continue;
            if (!matched.node.empty() && matched.node != name) return {};
            matched.node = name;
        }
        if (!matched.node.empty()) { matched.direct = false; return matched; }
        if (matched.direct) return matched;
        return {};
    }
    return {};
}
