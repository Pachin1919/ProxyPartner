#include "age.hpp"

#include <openssl/core_names.h>
#include <openssl/crypto.h>
#include <openssl/evp.h>
#include <openssl/hmac.h>
#include <openssl/kdf.h>
#include <openssl/params.h>

#include <algorithm>
#include <array>
#include <cstdint>
#include <fstream>
#include <memory>
#include <optional>
#include <span>
#include <vector>

namespace {
using Bytes = std::span<const unsigned char>;
template<class T, void (*Destroy)(T*)> using SslPtr = std::unique_ptr<T, decltype(Destroy)>;
template<size_t N> struct Secret : std::array<unsigned char, N> {
    ~Secret() { OPENSSL_cleanse(this->data(), N); }
};
constexpr std::string_view version = "age-encryption.org/v1\n";
constexpr std::string_view armor = "-----BEGIN AGE ENCRYPTED FILE-----";
constexpr std::string_view armorEnd = "-----END AGE ENCRYPTED FILE-----";
constexpr std::string_view alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
constexpr size_t plainLimit = 1024 * 1024;

Bytes bytes(std::string_view value) {
    return {reinterpret_cast<const unsigned char*>(value.data()), value.size()};
}

std::string_view line(std::string_view& text) {
    const auto end = text.find('\n');
    const auto value = text.substr(0, end);
    text.remove_prefix(end == text.npos ? text.size() : end + 1);
    return value;
}

std::optional<std::vector<unsigned char>> decode(std::string_view text) {
    if (text.empty() || text.size() % 4 == 1 || text.find_first_not_of(alphabet) != text.npos)
        return std::nullopt;
    const size_t size = (text.size() + 3) & ~size_t{3};
    std::string padded(text);
    padded.resize(size, '=');
    std::vector<unsigned char> result(size / 4 * 3);
    const auto count = EVP_DecodeBlock(result.data(), bytes(padded).data(), static_cast<int>(size));
    if (count < 0) return std::nullopt;
    if (padded.size() != text.size()) {
        const auto value = alphabet.find(text.back());
        if ((value & (text.size() % 4 == 2 ? 15u : 3u)) != 0) return std::nullopt;
    }
    result.resize(static_cast<size_t>(count) - (size - text.size()));
    return result;
}

bool identity_key(const std::filesystem::path& path, Secret<32>& key) {
    std::ifstream stream(path, std::ios::binary);
    struct RawIdentity {
        std::array<char, 4097> buffer{};
        ~RawIdentity() { OPENSSL_cleanse(buffer.data(), buffer.size()); }
    } raw;
    stream.read(raw.buffer.data(), raw.buffer.size());
    if (!stream.eof() || stream.gcount() == 0) return false;
    std::string_view input(raw.buffer.data(), static_cast<size_t>(stream.gcount()));
    constexpr std::string_view prefix = "AGE-SECRET-KEY-1";
    constexpr std::string_view symbols = "QPZRY9X8GF2TVDW0S3JN54KHCE6MUA7L";
    while (!input.empty()) {
        auto row = line(input);
        if (row.ends_with('\r')) row.remove_suffix(1);
        if (!row.starts_with(prefix)) continue;
        if (row.size() != prefix.size() + 58) return false;
        uint32_t checksum = 1, accumulator = 0;
        auto feed = [&checksum](uint32_t value) {
            constexpr uint32_t generators[]{0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3};
            const uint32_t high = checksum >> 25;
            checksum = (checksum & 0x1ffffff) << 5 ^ value;
            for (unsigned i = 0; i < 5; ++i) if ((high >> i) & 1) checksum ^= generators[i];
        };
        auto hrp = prefix.substr(0, prefix.size() - 1);
        for (const char ch : hrp) feed((ch >= 'A' && ch <= 'Z' ? ch + 32u : static_cast<unsigned>(ch)) >> 5);
        feed(0);
        for (const char ch : hrp) feed((ch >= 'A' && ch <= 'Z' ? ch + 32u : static_cast<unsigned>(ch)) & 31u);
        size_t written = 0;
        int bits = 0;
        const auto data = row.substr(prefix.size());
        for (size_t index = 0; index < data.size(); ++index) {
            const auto value = symbols.find(data[index]);
            if (value == symbols.npos) return false;
            feed(static_cast<uint32_t>(value));
            if (index >= 52) continue;
            accumulator = (accumulator << 5) | static_cast<uint32_t>(value);
            bits += 5;
            if (bits >= 8) key[written++] = static_cast<unsigned char>(accumulator >> (bits -= 8));
        }
        return checksum == 1 && written == key.size() && (accumulator & 15u) == 0;
    }
    return false;
}

bool derive(Bytes secret, Bytes salt, std::string_view info, Secret<32>& key) {
    SslPtr<EVP_KDF, EVP_KDF_free> algorithm(EVP_KDF_fetch(nullptr, "HKDF", nullptr), EVP_KDF_free);
    if (!algorithm) return false;
    SslPtr<EVP_KDF_CTX, EVP_KDF_CTX_free> context(EVP_KDF_CTX_new(algorithm.get()), EVP_KDF_CTX_free);
    if (!context) return false;
    char digest[] = "SHA256";
    std::array params{
        OSSL_PARAM_construct_utf8_string(OSSL_KDF_PARAM_DIGEST, digest, 0),
        OSSL_PARAM_construct_octet_string(OSSL_KDF_PARAM_KEY, const_cast<unsigned char*>(secret.data()), secret.size()),
        OSSL_PARAM_construct_octet_string(OSSL_KDF_PARAM_SALT, const_cast<unsigned char*>(salt.data()), salt.size()),
        OSSL_PARAM_construct_octet_string(OSSL_KDF_PARAM_INFO, const_cast<char*>(info.data()), info.size()),
        OSSL_PARAM_construct_end()
    };
    return EVP_KDF_derive(context.get(), key.data(), key.size(), params.data()) == 1;
}

bool open_aead(const Secret<32>& key, std::span<const unsigned char, 12> nonce, Bytes packet, unsigned char* out) {
    if (packet.size() < 16) return false;
    const auto size = packet.size() - 16;
    SslPtr<EVP_CIPHER_CTX, EVP_CIPHER_CTX_free> context(EVP_CIPHER_CTX_new(), EVP_CIPHER_CTX_free);
    if (!context) return false;
    int written = 0, tail = 0;
    return EVP_DecryptInit_ex2(context.get(), EVP_chacha20_poly1305(), key.data(), nonce.data(), nullptr) == 1 &&
        EVP_DecryptUpdate(context.get(), out, &written, packet.data(), static_cast<int>(size)) == 1 &&
        EVP_CIPHER_CTX_ctrl(context.get(), EVP_CTRL_AEAD_SET_TAG, 16, const_cast<unsigned char*>(packet.data() + size)) == 1 &&
        EVP_DecryptFinal_ex(context.get(), out + written, &tail) == 1 && static_cast<size_t>(written + tail) == size;
}
}

bool age_envelope(std::string_view input) {
    return input.starts_with(version) || input.starts_with(armor);
}

bool age_open(std::string_view input, const std::filesystem::path& identity, std::string& plaintext) {
    std::optional<std::vector<unsigned char>> unarmored;
    if (input.starts_with(armor)) {
        auto begin = line(input);
        if (begin.ends_with('\r')) begin.remove_suffix(1);
        if (begin != armor) return false;
        std::string base64;
        bool ended = false;
        while (!input.empty()) {
            auto row = line(input);
            if (row.ends_with('\r')) row.remove_suffix(1);
            if (row == armorEnd) { ended = true; break; }
            base64.append(row);
        }
        if (!ended) return false;
        while (base64.ends_with('=')) base64.pop_back();
        unarmored = decode(base64);
        if (!unarmored) return false;
        input = {reinterpret_cast<const char*>(unarmored->data()), unarmored->size()};
    }
    if (!input.starts_with(version)) return false;
    auto remaining = input.substr(version.size());
    Secret<32> privateKey{}, shared{}, key{};
    Secret<16> fileKey{};
    if (!identity_key(identity, privateKey)) return false;
    SslPtr<EVP_PKEY, EVP_PKEY_free> local(
        EVP_PKEY_new_raw_private_key_ex(nullptr, "X25519", nullptr, privateKey.data(), privateKey.size()), EVP_PKEY_free);
    std::array<unsigned char, 64> salt{};
    size_t publicSize = 32;
    if (!local || EVP_PKEY_get_raw_public_key(local.get(), salt.data() + 32, &publicSize) != 1 || publicSize != 32)
        return false;

    bool matched = false;
    while (remaining.starts_with("-> ")) {
        const auto recipient = line(remaining);
        const auto body = line(remaining);
        if (!recipient.starts_with("-> X25519 ")) {
            auto width = body.size();
            while (width == 64 && !remaining.empty()) width = line(remaining).size();
            continue;
        }
        auto peerKey = decode(recipient.substr(10));
        auto wrapped = decode(body);
        if (!peerKey || peerKey->size() != 32 || !wrapped || wrapped->size() != 32) return false;
        if (matched) continue;
        std::copy(peerKey->begin(), peerKey->end(), salt.begin());
        SslPtr<EVP_PKEY, EVP_PKEY_free> peer(
            EVP_PKEY_new_raw_public_key_ex(nullptr, "X25519", nullptr, peerKey->data(), peerKey->size()), EVP_PKEY_free);
        SslPtr<EVP_PKEY_CTX, EVP_PKEY_CTX_free> agreement(EVP_PKEY_CTX_new(local.get(), nullptr), EVP_PKEY_CTX_free);
        size_t size = shared.size();
        if (!peer || !agreement || EVP_PKEY_derive_init(agreement.get()) != 1 ||
            EVP_PKEY_derive_set_peer(agreement.get(), peer.get()) != 1 ||
            EVP_PKEY_derive(agreement.get(), shared.data(), &size) != 1 || size != shared.size() ||
            !derive(shared, salt, "age-encryption.org/v1/X25519", key)) return false;
        const std::array<unsigned char, 12> zero{};
        matched = open_aead(key, zero, *wrapped, fileKey.data());
    }
    if (!matched || !remaining.starts_with("--- ")) return false;
    const auto authenticated = bytes(input.substr(0, input.size() - remaining.size() + 3));
    const auto headerMac = decode(line(remaining).substr(4));
    std::array<unsigned char, 32> expected{};
    const std::array<unsigned char, 32> emptySalt{};
    unsigned count = 0;
    if (!headerMac || headerMac->size() != expected.size() || !derive(fileKey, emptySalt, "header", key) ||
        !HMAC(EVP_sha256(), key.data(), static_cast<int>(key.size()), authenticated.data(), authenticated.size(), expected.data(), &count) ||
        count != expected.size() || CRYPTO_memcmp(expected.data(), headerMac->data(), count) != 0 ||
        remaining.size() < 32 || !derive(fileKey, bytes(remaining.substr(0, 16)), "payload", key)) return false;
    remaining.remove_prefix(16);
    if (remaining.size() > plainLimit + 16 * (plainLimit / 65536 + 1)) return false;
    plaintext.resize(remaining.size());
    size_t offset = 0;
    std::array<unsigned char, 12> nonce{};
    constexpr size_t chunk = 65536 + 16;
    while (!remaining.empty()) {
        const size_t size = (std::min)(remaining.size(), chunk);
        nonce.back() = remaining.size() == size ? 1 : 0;
        if (!open_aead(key, nonce, bytes(remaining.substr(0, size)),
            reinterpret_cast<unsigned char*>(plaintext.data() + offset))) {
            OPENSSL_cleanse(plaintext.data(), plaintext.size());
            plaintext.clear();
            return false;
        }
        offset += size - 16;
        remaining.remove_prefix(size);
        for (size_t index = 11; index > 0 && ++nonce[index - 1] == 0; --index) {}
    }
    if (offset > plainLimit) {
        OPENSSL_cleanse(plaintext.data(), plaintext.size());
        plaintext.clear();
        return false;
    }
    plaintext.resize(offset);
    return true;
}
