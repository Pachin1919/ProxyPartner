// AnyTLS framing adapted from Mihomo v1.19.21 transport/anytls (GPL-3.0).
// Single fixed-destination stream per test; no listener, routing or pooling.
#include "shadowsocks.cpp"
extern "C" Byte* SHA256(const Byte*, unsigned __int64, Byte*);
extern "C" int BIO_free(Bio*);
extern "C" __declspec(dllexport) void ProbeShutdown();
#ifdef ANYTLS_ECH
extern "C" int SSL_set1_ech_config_list(Ssl*, const Byte*, unsigned __int64);
extern "C" int SSL_ech_get1_status(Ssl*, char**, char**);
extern "C" void CRYPTO_free(void*, const char*, int);
#endif

namespace {
SslContext* anyContext;
BioMethod* socketMethod;
BioMethod* streamMethod;

struct AnyChannel {
    Socket connection;
    Tick deadline;
    Handle stop;
    Ssl* outer = nullptr;
    int pending = 0;
    ~AnyChannel() { if (outer) SSL_free(outer); closesocket(connection); }
    int receive(char* data, int length) const {
        while (GetTickCount64() < deadline && !cancelled(stop)) {
            int n = recv(connection, data, length, 0);
            if (n >= 0) return n;
            if (WSAGetLastError() != wouldBlock || !ready(connection, false, deadline, stop)) break;
        }
        return -1;
    }
    bool transmit(const char* data, int length) const {
        while (length && GetTickCount64() < deadline && !cancelled(stop)) {
            int n = send(connection, data, length, 0);
            if (n > 0) { data += n; length -= n; }
            else if (n == 0 || WSAGetLastError() != wouldBlock || !ready(connection, true, deadline, stop)) return false;
        }
        return length == 0;
    }
    bool exact(Byte* data, int length) const {
        while (length > 0) {
            int n = SSL_read(outer, data, length);
            if (n <= 0) return false;
            data += n; length -= n;
        }
        return true;
    }
    bool frame(Byte cmd, unsigned sid, const void* data, int length) const {
        Byte header[7]{cmd, static_cast<Byte>(sid >> 24), static_cast<Byte>(sid >> 16),
            static_cast<Byte>(sid >> 8), static_cast<Byte>(sid), 0, 0};
        put16(header + 5, length);
        return SSL_write(outer, header, 7) == 7 && (!length || SSL_write(outer, data, length) == length);
    }
    int readStream(char* data, int length) {
        while (!pending) {
            Byte header[7];
            if (!exact(header, 7)) return -1;
            int count = (header[5] << 8) | header[6];
            unsigned sid = (unsigned(header[1]) << 24) | (unsigned(header[2]) << 16) |
                (unsigned(header[3]) << 8) | header[4];
            if (header[0] == 2 && sid == 1) { pending = count; continue; }
            if (header[0] == 5 || (header[0] == 3 && sid == 1)) return 0;
            // Discard server settings, padding updates and waste without retaining snapshots.
            Byte discard[1024];
            while (count) {
                int n = count < sizeof(discard) ? count : sizeof(discard);
                if (!exact(discard, n)) return -1;
                count -= n;
            }
        }
        int n = length < pending ? length : pending;
        if (!exact(reinterpret_cast<Byte*>(data), n)) return -1;
        pending -= n;
        return n;
    }
};
int socketRead(Bio* bio, char* data, int length) {
    return static_cast<AnyChannel*>(BIO_get_data(bio))->receive(data, length);
}
int socketWrite(Bio* bio, const char* data, int length) {
    return static_cast<AnyChannel*>(BIO_get_data(bio))->transmit(data, length) ? length : -1;
}
int streamRead(Bio* bio, char* data, int length) {
    return static_cast<AnyChannel*>(BIO_get_data(bio))->readStream(data, length);
}
int streamWrite(Bio* bio, const char* data, int length) {
    auto* channel = static_cast<AnyChannel*>(BIO_get_data(bio));
    int left = length;
    while (left) {
        int n = left < 16384 ? left : 16384;
        if (!channel->frame(2, 1, data, n)) return -1;
        data += n; left -= n;
    }
    return length;
}

Ssl* tlsOver(AnyChannel& channel, BioMethod* method, const char* host) {
    Bio* bio = BIO_new(method);
    Ssl* ssl = SSL_new(anyContext);
    if (!bio || !ssl) { if (bio) BIO_free(bio); if (ssl) SSL_free(ssl); return nullptr; }
    BIO_set_data(bio, &channel); BIO_set_init(bio, 1);
    SSL_set_bio(ssl, bio, bio);
    if (SSL_set1_host(ssl, host) != 1 || SSL_ctrl(ssl, 55, 0, const_cast<char*>(host)) != 1) {
        SSL_free(ssl); return nullptr;
    }
    return ssl;
}
}

extern "C" __declspec(dllexport) int ProbeInitialize(const char* caBundle) {
    alignas(8) Byte winsock[512];
    if (WSAStartup(0x0202, winsock)) return 0;
    anyContext = SSL_CTX_new(TLS_client_method());
    socketMethod = BIO_meth_new(0x0480, "AnyTLS socket");
    streamMethod = BIO_meth_new(0x0481, "AnyTLS stream");
    if (!anyContext || !socketMethod || !streamMethod ||
        !BIO_meth_set_read(socketMethod, socketRead) || !BIO_meth_set_write(socketMethod, socketWrite) ||
        !BIO_meth_set_ctrl(socketMethod, bioControl) || !BIO_meth_set_read(streamMethod, streamRead) ||
        !BIO_meth_set_write(streamMethod, streamWrite) || !BIO_meth_set_ctrl(streamMethod, bioControl) ||
        SSL_CTX_load_verify_locations(anyContext, caBundle, nullptr) != 1) {
        ProbeShutdown(); return 0;
    }
    SSL_CTX_set_verify(anyContext, 1, nullptr);
    return 1;
}
extern "C" __declspec(dllexport) void ProbeShutdown() {
    SSL_CTX_free(anyContext); BIO_meth_free(socketMethod); BIO_meth_free(streamMethod); WSACleanup();
    anyContext = nullptr; socketMethod = nullptr; streamMethod = nullptr;
}
static int probeAnyTLS(const char* address, const char* port, const char* password,
    const char* serverName, const char* echConfig, const char* host, const char* path, const char* headers,
    unsigned remaining, Handle stop, int* status, int* echStatus) {
    *status = 0;
    *echStatus = -103;
    Tick deadline = GetTickCount64() + remaining;
    Profile profile{address, port, serverName, {}, true};
    AnyChannel channel{dial(profile, deadline, stop), deadline, stop};
    if (channel.connection == invalidSocket) return 0;
    channel.outer = tlsOver(channel, socketMethod, serverName);
    if (!channel.outer) return 0;
#ifdef ANYTLS_ECH
    if (*echConfig && (SSL_ctrl(channel.outer, 123, 0x0304, nullptr) != 1 ||
        SSL_set1_ech_config_list(channel.outer, reinterpret_cast<const Byte*>(echConfig), strlen(echConfig)) != 1)) return 0;
#else
    if (*echConfig) return 0;
#endif
    if (SSL_connect(channel.outer) != 1) return 0;
#ifdef ANYTLS_ECH
    if (*echConfig) {
        char* innerName = nullptr;
        char* outerName = nullptr;
        *echStatus = SSL_ech_get1_status(channel.outer, &innerName, &outerName);
        CRYPTO_free(innerName, nullptr, 0); CRYPTO_free(outerName, nullptr, 0);
        if (*echStatus != 1) return 0;
    }
#endif
    Byte auth[34]{};
    SHA256(reinterpret_cast<const Byte*>(password), strlen(password), auth);
    int sent = SSL_write(channel.outer, auth, sizeof(auth));
    OPENSSL_cleanse(auth, sizeof(auth));
    if (sent != sizeof(auth)) return 0;
    constexpr char settings[] = "v=2\nclient=ProxyPartner-test\npadding-md5=none";
    if (!channel.frame(4, 0, settings, sizeof(settings)-1) || !channel.frame(1, 1, nullptr, 0)) return 0;
    Byte destination[259]{3};
    int hostLength = static_cast<int>(strlen(host));
    if (hostLength < 1 || hostLength > 255) return 0;
    destination[1] = static_cast<Byte>(hostLength);
    memcpy(destination + 2, host, hostLength); put16(destination + 2 + hostLength, 443);
    if (!channel.frame(2, 1, destination, hostLength + 4)) return 0;
    Ssl* inner = tlsOver(channel, streamMethod, host);
    if (!inner) return 0;
    int ok = 0;
    if (SSL_connect(inner) == 1) {
        char request[4096];
        const char* parts[]{"GET ", path, " HTTP/1.1\r\nHost: ", host, "\r\nConnection: close\r\n", headers, "\r\n"};
        int size = 0;
        for (auto part : parts) {
            int n = static_cast<int>(strlen(part));
            if (n > static_cast<int>(sizeof(request)) - size) { size = 0; break; }
            memcpy(request + size, part, n); size += n;
        }
        if (size && SSL_write(inner, request, size) == size) {
            char response[128]; int used = 0;
            while (used < 12) {
                int n = SSL_read(inner, response + used, sizeof(response) - used);
                if (n <= 0) break;
                used += n;
            }
            if (used >= 12 && memcmp(response, "HTTP/1.", 7) == 0 && response[8] == ' ' &&
                response[9] >= '1' && response[9] <= '5' && response[10] >= '0' && response[10] <= '9' &&
                response[11] >= '0' && response[11] <= '9') {
                *status = (response[9]-'0')*100 + (response[10]-'0')*10 + response[11]-'0'; ok = 1;
            }
        }
    }
    SSL_free(inner);
    return ok;
}
extern "C" __declspec(dllexport) int ProbeWebsite(const char* address, const char* port, const char* password,
    const char* serverName, const char* host, const char* path, const char* headers,
    unsigned remaining, Handle stop, int* status) {
    int echStatus;
    return probeAnyTLS(address, port, password, serverName, "", host, path, headers, remaining, stop, status, &echStatus);
}
extern "C" __declspec(dllexport) int ProbeAnyTlsWebsite(const char* address, const char* port, const char* password,
    const char* serverName, const char* echConfig, const char* host, const char* path, const char* headers,
    unsigned remaining, Handle stop, int* status, int* echStatus) {
    return probeAnyTLS(address, port, password, serverName, echConfig, host, path, headers, remaining, stop, status, echStatus);
}
