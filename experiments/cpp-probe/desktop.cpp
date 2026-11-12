#include "shadowsocks.hpp"

namespace {
SslContext* context;
BioMethod* method;
}

extern "C" __declspec(dllexport) int ProbeInitialize(const char* caBundle) {
    alignas(8) Byte winsock[512];
    if (WSAStartup(0x0202, winsock) != 0) return 0;
    context = SSL_CTX_new(TLS_client_method());
    method = createTransportMethod();
    if (context && method && SSL_CTX_load_verify_locations(context, caBundle, nullptr) == 1) {
        SSL_CTX_set_verify(context, 1, nullptr);
        return 1;
    }
    if (context) SSL_CTX_free(context);
    if (method) BIO_meth_free(method);
    WSACleanup();
    return 0;
}

extern "C" __declspec(dllexport) void ProbeShutdown() {
    SSL_CTX_free(context);
    BIO_meth_free(method);
    WSACleanup();
}

extern "C" __declspec(dllexport) int ProbeWebsite(const char* address, const char* port, const char* password,
    const char* obfsHost, const char* host, const char* path, const char* headers,
    unsigned remaining, Handle stop, int* status) {
    *status = 0;
    const Tick deadline = GetTickCount64() + remaining;
    Profile profile{address, port, obfsHost, {}, true};
    if (EVP_BytesToKey(EVP_chacha20_poly1305(), EVP_md5(), nullptr, reinterpret_cast<const Byte*>(password),
        static_cast<int>(strlen(password)), 1, profile.key, nullptr) != keySize) {
        OPENSSL_cleanse(profile.key, keySize);
        return 0;
    }
    const auto result = measureSite(profile, {nullptr, host, path, headers}, deadline, context, method, stop);
    OPENSSL_cleanse(profile.key, keySize);
    *status = result.status;
    return result.connected ? 1 : 0;
}
