#pragma once

#include <nlohmann/json.hpp>
#include <openssl/evp.h>
#include <map>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace share_links {
struct Row { std::string name, link; };

class Index : public nlohmann::json_sax<nlohmann::json> {
    int depth = 0;
    bool root = false;
    Row* current = nullptr;
    std::map<std::string, size_t> names;
public:
    std::vector<Row> rows;
    bool start_object(std::size_t) override {
        if (depth == 0) root = true;
        return ++depth <= 2;
    }
    bool end_object() override { --depth; return true; }
    bool start_array(std::size_t) override { return depth > 0 && ++depth <= 2; }
    bool end_array() override { --depth; return true; }
    bool key(string_t& name) override {
        if (depth != 1) return true;
        current = nullptr;
        if (name.empty() || name.size() > 1024 || name.find_first_of("\r\n\t") != name.npos || name.find('\0') != name.npos) return true;
        const auto previous = names.find(name);
        if (previous != names.end()) {
            auto& link = rows[previous->second].link;
            OPENSSL_cleanse(link.data(), link.size());
            link.clear();
        } else if (rows.size() < 1000) {
            names.emplace(name, rows.size());
            rows.push_back({std::move(name), {}});
            current = &rows.back();
        }
        return true;
    }
    bool string(string_t& value) override {
        if (root && depth == 1 && current && value.size() <= 16384) current->link = std::move(value);
        return root;
    }
    bool null() override { return root; }
    bool boolean(bool) override { return root; }
    bool number_integer(number_integer_t) override { return root; }
    bool number_unsigned(number_unsigned_t) override { return root; }
    bool number_float(number_float_t, const string_t&) override { return root; }
    bool binary(binary_t&) override { return false; }
    bool parse_error(std::size_t, const std::string&, const nlohmann::detail::exception&) override { return false; }
    ~Index() { for (auto& row : rows) OPENSSL_cleanse(row.link.data(), row.link.size()); }
};

inline std::optional<std::string> unescape(std::string_view input) {
    const auto hex = [](char ch) {
        if (ch >= '0' && ch <= '9') return ch - '0';
        if (ch >= 'a' && ch <= 'f') return ch - 'a' + 10;
        if (ch >= 'A' && ch <= 'F') return ch - 'A' + 10;
        return -1;
    };
    std::string value;
    value.reserve(input.size());
    for (size_t i = 0; i < input.size(); ++i) {
        char ch = input[i];
        if (ch == '%') {
            if (i + 2 >= input.size()) return {};
            const int high = hex(input[i+1]), low = hex(input[i+2]);
            if (high < 0 || low < 0) return {};
            ch = static_cast<char>(high * 16 + low);
            i += 2;
        }
        value.push_back(ch);
    }
    return value;
}

inline std::optional<std::string> base64(std::string_view text) {
    if (text.empty() || text.size() > 16384 || text.size() % 4 == 1) return {};
    const auto pad = text.find('=');
    if (pad != text.npos && (text.size() % 4 || text.size() - pad > 2 || text.find_first_not_of('=', pad) != text.npos)) return {};
    std::string encoded(text);
    for (char& ch : encoded) {
        if (ch == '-') ch = '+';
        if (ch == '_') ch = '/';
    }
    while (encoded.size() % 4) encoded.push_back('=');
    std::string binary(encoded.size() / 4 * 3, '\0');
    const int size = EVP_DecodeBlock(reinterpret_cast<unsigned char*>(binary.data()),
        reinterpret_cast<const unsigned char*>(encoded.data()), static_cast<int>(encoded.size()));
    const auto padding = encoded.ends_with("==") ? 2 : encoded.ends_with('=') ? 1 : 0;
    if (size <= padding) return {};
    binary.resize(static_cast<size_t>(size - padding));
    return binary;
}

struct Endpoint { std::string_view credentials, server, port, query; };
inline std::optional<Endpoint> endpoint(std::string_view body) {
    body = body.substr(0, body.find('#'));
    const auto query = body.find('?');
    const auto authority = body.substr(0, query);
    const auto user = authority.rfind('@');
    if (user == authority.npos || user == 0) return {};
    auto host = authority.substr(user + 1);
    if (host.ends_with('/')) host.remove_suffix(1);
    const auto colon = host.rfind(':');
    if (colon == host.npos || !colon) return {};
    auto server = host.substr(0, colon);
    if (server.starts_with('[') && server.ends_with(']')) {
        server.remove_prefix(1);
        server.remove_suffix(1);
    } else if (server.find(':') != server.npos) return {};
    if (server.empty() || server.size() > 253 || server.find_first_of("%@/[] \t\r\n") != server.npos || server.find('\0') != server.npos) return {};
    return Endpoint{authority.substr(0, user), server, host.substr(colon + 1),
        query == body.npos ? std::string_view{} : body.substr(query + 1)};
}

inline std::optional<std::map<std::string, std::string>> query(std::string_view text) {
    std::map<std::string, std::string> values;
    while (!text.empty()) {
        const auto amp = text.find('&');
        const auto item = text.substr(0, amp);
        const auto eq = item.find('=');
        if (eq == item.npos || !eq) return {};
        auto key = unescape(item.substr(0, eq)), value = unescape(item.substr(eq + 1));
        if (!key || !value || !values.emplace(std::move(*key), std::move(*value)).second) return {};
        if (amp == text.npos) break;
        text.remove_prefix(amp + 1);
    }
    return values;
}
}
