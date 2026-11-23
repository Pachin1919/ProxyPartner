#define WIN32_LEAN_AND_MEAN
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <openssl/ssl.h>
#include <openssl/rand.h>
#include <openssl/x509v3.h>
#include <algorithm>
#include <memory>
#include <utility>

#include "trojan.hpp"
#include "../../experiments/cpp-probe/echo_http.hpp"

namespace {
struct Tunnel {
    const ULONGLONG deadline;
    HANDLE stop;
    SOCKET socket = INVALID_SOCKET;
    SSL* outer{};
    std::string prefix;
    unsigned long long frameLeft = 0;
    bool websocket = false, fragmented = false;
    ~Tunnel() {
        SSL_free(outer);
        if (socket != INVALID_SOCKET) closesocket(socket);
        OPENSSL_cleanse(prefix.data(), prefix.size());
    }
    bool open() const { return GetTickCount64() < deadline && (!stop || WaitForSingleObject(stop, 0) != WAIT_OBJECT_0); }
    bool wait(bool writing) const {
        while (open()) {
            fd_set ready;
            FD_ZERO(&ready);
            FD_SET(socket, &ready);
            timeval interval{0, 20000};
            int selected = select(0, writing ? nullptr : &ready, writing ? &ready : nullptr, nullptr, &interval);
            if (selected) return selected > 0 && open();
        }
        return false;
    }
    bool connect_to(const char* address, const char* port) {
        addrinfo hints{}, *result = nullptr;
        hints.ai_flags = AI_NUMERICHOST | AI_NUMERICSERV;
        hints.ai_socktype = SOCK_STREAM;
        if (getaddrinfo(address, port, &hints, &result)) return false;
        const std::unique_ptr<addrinfo, decltype(&freeaddrinfo)> owner(result, freeaddrinfo);
        socket = ::socket(result->ai_family, SOCK_STREAM, IPPROTO_TCP);
        u_long nonblocking = 1;
        if (socket == INVALID_SOCKET || ioctlsocket(socket, FIONBIO, &nonblocking)) return false;
        if (::connect(socket, result->ai_addr, static_cast<int>(result->ai_addrlen)) == 0) return true;
        if (WSAGetLastError() != WSAEWOULDBLOCK || !wait(true)) return false;
        int error = 0, size = sizeof(error);
        return !getsockopt(socket, SOL_SOCKET, SO_ERROR, reinterpret_cast<char*>(&error), &size) && !error;
    }
    int read_wire(char* output, int length) {
        while (open()) {
            int size = recv(socket, output, length, 0);
            if (size >= 0) return size;
            if (WSAGetLastError() != WSAEWOULDBLOCK || !wait(false)) break;
        }
        return -1;
    }
    int write_wire(const char* data, int length) {
        int done = 0;
        while (done < length && open()) {
            int size = send(socket, data + done, length - done, 0);
            if (size > 0) done += size;
            else if (!size || WSAGetLastError() != WSAEWOULDBLOCK || !wait(true)) break;
        }
        return done == length ? done : -1;
    }
    bool read_exact(unsigned char* output, int length) const {
        while (length) {
            int size = SSL_read(outer, output, length);
            if (size <= 0) return false;
            output += size;
            length -= size;
        }
        return true;
    }
    bool write_frame(const char* data, unsigned size, unsigned char opcode) const {
        unsigned char frame[16384 + 8];
        unsigned head = 2;
        frame[0] = static_cast<unsigned char>(0x80 | opcode);
        frame[1] = static_cast<unsigned char>(0x80 | (size < 126 ? size : 126));
        if (size >= 126) {
            frame[head++] = static_cast<unsigned char>(size >> 8);
            frame[head++] = static_cast<unsigned char>(size);
        }
        unsigned char* mask = frame + head;
        if (RAND_bytes(mask, 4) != 1) return false;
        head += 4;
        for (unsigned i = 0; i < size; ++i) frame[head + i] = static_cast<unsigned char>(data[i]) ^ mask[i % 4];
        return SSL_write(outer, frame, static_cast<int>(head + size)) == static_cast<int>(head + size);
    }
    int read_stream(char* output, int length) {
        if (!websocket) return SSL_read(outer, output, length);
        while (!frameLeft) {
            unsigned char header[8];
            if (!read_exact(header, 2) || (header[0] & 0x70) || (header[1] & 0x80)) return -1;
            const unsigned opcode = header[0] & 0xf;
            const bool fin = (header[0] & 0x80) != 0;
            unsigned long long size = header[1];
            if (opcode >= 8) {
                if (!fin || size > 125 || opcode > 10) return -1;
                unsigned char control[125];
                if (!read_exact(control, static_cast<int>(size))) return -1;
                if (opcode == 8) return 0;
                if (opcode == 9 && !write_frame(reinterpret_cast<const char*>(control), static_cast<unsigned>(size), 10)) return -1;
                continue;
            }
            if (opcode != (fragmented ? 0u : 2u)) return -1;
            fragmented = !fin;
            if (size >= 126) {
                const int bytes = size == 126 ? 2 : 8;
                if (!read_exact(header, bytes) || (bytes == 8 && (header[0] & 0x80))) return -1;
                size = 0;
                for (int i = 0; i < bytes; ++i) size = (size << 8) | header[i];
            }
            frameLeft = size;
        }
        int size = static_cast<int>((std::min)(frameLeft, static_cast<unsigned long long>(length)));
        int count = SSL_read(outer, output, size);
        if (count > 0) frameLeft -= static_cast<unsigned>(count);
        return count;
    }
    int write_stream(const char* data, int length) {
        std::string first;
        if (!prefix.empty()) {
            first.swap(prefix);
            first.append(data, static_cast<size_t>(length));
            data = first.data();
        }
        const int size = first.empty() ? length : static_cast<int>(first.size());
        bool ok = true;
        if (!websocket) ok = SSL_write(outer, data, size) == size;
        else for (int i = 0; i < size && ok; i += 16384)
            ok = write_frame(data + i, static_cast<unsigned>((std::min)(size - i, 16384)), 2);
        OPENSSL_cleanse(first.data(), first.size());
        return ok ? length : -1;
    }
};

template<int (Tunnel::*Read)(char*, int)> int bio_read(BIO* bio, char* out, int count) {
    return (static_cast<Tunnel*>(BIO_get_data(bio))->*Read)(out, count);
}
template<int (Tunnel::*Write)(const char*, int)> int bio_write(BIO* bio, const char* in, int count) {
    return (static_cast<Tunnel*>(BIO_get_data(bio))->*Write)(in, count);
}
long flush(BIO*, int op, long, void*) { return op == BIO_CTRL_FLUSH; }

BIO_METHOD* transport(int (*reader)(BIO*, char*, int), int (*writer)(BIO*, const char*, int)) {
    auto* result = BIO_meth_new(BIO_get_new_index() | BIO_TYPE_SOURCE_SINK, "ProxyPartner Trojan transport");
    if (!result) return nullptr;
    if (BIO_meth_set_read(result, reader) && BIO_meth_set_write(result, writer) && BIO_meth_set_ctrl(result, flush)) return result;
    BIO_meth_free(result);
    return nullptr;
}
SSL* connect_tls(SSL_CTX* context, BIO_METHOD* method, Tunnel& channel, const char* host, std::string_view alpn = {}) {
    std::unique_ptr<SSL, decltype(&SSL_free)> tls(SSL_new(context), SSL_free);
    if (!tls) return nullptr;
    auto* bio = BIO_new(method);
    if (!bio) return nullptr;
    BIO_set_data(bio, &channel);
    BIO_set_init(bio, 1);
    SSL_set_bio(tls.get(), bio, bio);
    unsigned char address[16];
    const bool ip = InetPtonA(AF_INET, host, address) == 1 || InetPtonA(AF_INET6, host, address) == 1;
    auto* verification = SSL_get0_param(tls.get());
    if (ip ? X509_VERIFY_PARAM_set1_ip_asc(verification, host) != 1 :
        X509_VERIFY_PARAM_set1_host(verification, host, 0) != 1 || SSL_set_tlsext_host_name(tls.get(), host) != 1) return nullptr;
    if (!alpn.empty() && SSL_set_alpn_protos(tls.get(), reinterpret_cast<const unsigned char*>(alpn.data()), static_cast<unsigned>(alpn.size())) != 0) return nullptr;
    return SSL_connect(tls.get()) == 1 ? tls.release() : nullptr;
}

std::string http_header(SSL* ssl) {
    std::string header;
    char ch;
    while (header.size() < 32768 && SSL_read(ssl, &ch, 1) == 1) {
        header.push_back(ch);
        if (header.ends_with("\r\n\r\n")) return header;
    }
    return {};
}
int http_status(std::string_view header) {
    if ((!header.starts_with("HTTP/1.1 ") && !header.starts_with("HTTP/1.0 ")) || header.size() < 13) return 0;
    const auto digits = header.substr(9, 3);
    if (digits[0] < '1' || digits[0] > '5' || digits[1] < '0' || digits[1] > '9' || digits[2] < '0' || digits[2] > '9') return 0;
    return (digits[0] - '0') * 100 + (digits[1] - '0') * 10 + digits[2] - '0';
}
bool upgrade(Tunnel& channel, const TrojanSettings& settings) {
    unsigned char nonce[16], key[25];
    if (RAND_bytes(nonce, sizeof(nonce)) != 1) return false;
    EVP_EncodeBlock(key, nonce, sizeof(nonce));
    std::string request = "GET " + settings.path + " HTTP/1.1\r\nHost: " + settings.host +
        "\r\nConnection: Upgrade\r\nUpgrade: websocket\r\nSec-WebSocket-Version: 13\r\nSec-WebSocket-Key: " +
        reinterpret_cast<const char*>(key) + "\r\n\r\n";
    if (SSL_write(channel.outer, request.data(), static_cast<int>(request.size())) != static_cast<int>(request.size())) return false;
    auto response = http_header(channel.outer);
    if (http_status(response) != 101) return false;
    if (settings.transport == TrojanTransport::HttpUpgrade) return true;
    std::string challenge = reinterpret_cast<const char*>(key);
    challenge += "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    unsigned char digest[20], expected[29];
    if (!EVP_Digest(challenge.data(), challenge.size(), digest, nullptr, EVP_sha1(), nullptr)) return false;
    EVP_EncodeBlock(expected, digest, sizeof(digest));
    std::string_view fields(response);
    fields.remove_prefix(fields.find("\r\n") + 2);
    while (!fields.empty()) {
        const auto end = fields.find("\r\n");
        if (!end || end == fields.npos) return false;
        auto line = fields.substr(0, end);
        const auto split = line.find(':');
        if (split == 20 && _strnicmp(line.data(), "Sec-WebSocket-Accept", 20) == 0) {
            line.remove_prefix(split + 1);
            while (!line.empty() && (line.front() == ' ' || line.front() == '\t')) line.remove_prefix(1);
            while (!line.empty() && (line.back() == ' ' || line.back() == '\t')) line.remove_suffix(1);
            return line == reinterpret_cast<const char*>(expected);
        }
        fields.remove_prefix(end + 2);
    }
    return false;
}
}

TrojanProbe::TrojanProbe(const std::filesystem::path& ca) {
    context = SSL_CTX_new(TLS_client_method());
    if (!context) return;
    const auto file = ca.u8string();
    if (SSL_CTX_load_verify_locations(context, reinterpret_cast<const char*>(file.c_str()), nullptr) != 1) return;
    SSL_CTX_set_verify(context, SSL_VERIFY_PEER, nullptr);
    SSL_CTX_set_min_proto_version(context, TLS1_2_VERSION);
    wire = transport(bio_read<&Tunnel::read_wire>, bio_write<&Tunnel::write_wire>);
    stream = transport(bio_read<&Tunnel::read_stream>, bio_write<&Tunnel::write_stream>);
}
TrojanProbe::~TrojanProbe() {
    BIO_meth_free(stream);
    BIO_meth_free(wire);
    SSL_CTX_free(context);
}
int TrojanProbe::request(const TrojanSettings& profile, const char* address, const char* port, std::string_view password,
    const char* target, const char* path, const char* headers, unsigned remaining, void* stop, char* echo, unsigned* used) const {
    const auto host = std::string_view(target);
    if (host.empty() || host.size() > 255) return 0;
    Tunnel channel{GetTickCount64() + remaining, static_cast<HANDLE>(stop)};
    if (!channel.connect_to(address, port)) return 0;
    channel.outer = connect_tls(context, wire, channel, profile.peerName.c_str(), profile.alpn);
    if (!channel.outer || (profile.transport != TrojanTransport::Tcp && !upgrade(channel, profile))) return 0;
    channel.websocket = profile.transport == TrojanTransport::WebSocket;
    unsigned char hash[28];
    if (!EVP_Digest(password.data(), password.size(), hash, nullptr, EVP_sha224(), nullptr)) return 0;
    constexpr char hex[] = "0123456789abcdef";
    for (const auto byte : hash) { channel.prefix.push_back(hex[byte >> 4]); channel.prefix.push_back(hex[byte & 15]); }
    OPENSSL_cleanse(hash, sizeof(hash));
    channel.prefix.append("\r\n\x01\x03", 4);
    channel.prefix.push_back(static_cast<char>(host.size()));
    channel.prefix.append(host);
    channel.prefix.append("\x01\xbb\r\n", 4);
    std::unique_ptr<SSL, decltype(&SSL_free)> tls(connect_tls(context, stream, channel, target), SSL_free);
    if (!tls) return 0;
    std::string request = std::string("GET ") + path + " HTTP/1.1\r\nHost: " + target + "\r\nConnection: close\r\n" + headers + "\r\n";
    if (SSL_write(tls.get(), request.data(), static_cast<int>(request.size())) != static_cast<int>(request.size())) return 0;
    if (echo) {
        int status = 0;
        if (!read_echo_response([&](char* input, int size) { return SSL_read(tls.get(), input, size); }, echo, 65, used, &status)) *used = 0;
        return status;
    }
    for (int attempt = 0; attempt < 8; ++attempt) {
        const auto status = http_status(http_header(tls.get()));
        if (!status || status >= 200 || status == 101) return status;
    }
    return 0;
}
