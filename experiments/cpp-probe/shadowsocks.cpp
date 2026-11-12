#include "shadowsocks.hpp"

extern "C" __int64 _time64(__int64*);

void put16(Byte* target, int value) {
    target[0] = static_cast<Byte>(value >> 8);
    target[1] = static_cast<Byte>(value);
}

namespace {
constexpr int wouldBlock = 10035;
constexpr int socketLevel = 0xffff;
constexpr int socketError = 0x1007;
constexpr int chachaGetTag = 0x10;
constexpr int chachaSetTag = 0x11;
constexpr long nonblocking = static_cast<long>(0x8004667e);

bool ready(Socket connection, bool writing, Tick deadline) {
    const Tick now = GetTickCount64();
    if (now >= deadline) return false;
    const Tick remaining = deadline - now;
    TimeValue time{static_cast<long>(remaining / 1000), static_cast<long>(remaining % 1000 * 1000)};
    SocketSet sockets{1, {connection}};
    return select(0, writing ? nullptr : &sockets, writing ? &sockets : nullptr, nullptr, &time) == 1;
}

Socket dial(const Profile& profile, Tick deadline) {
    AddressInfo hints{0, 0, 1, 6, 0, nullptr, nullptr, nullptr};
    AddressInfo* addresses = nullptr;
    if (getaddrinfo(profile.server, profile.port, &hints, &addresses)) return invalidSocket;
    Socket connected = invalidSocket;
    for (auto* address = addresses; address && GetTickCount64() < deadline; address = address->next) {
        Socket candidate = socket(address->family, address->kind, address->protocol);
        if (candidate == invalidSocket) continue;
        Dword mode = 1;
        if (ioctlsocket(candidate, nonblocking, &mode) == 0) {
            int error = 0, size = sizeof(error);
            if (connect(candidate, address->address, static_cast<int>(address->length)) == 0 ||
                (WSAGetLastError() == wouldBlock && ready(candidate, true, deadline) &&
                 getsockopt(candidate, socketLevel, socketError, reinterpret_cast<char*>(&error), &size) == 0 && !error)) {
                connected = candidate;
                break;
            }
        }
        closesocket(candidate);
    }
    freeaddrinfo(addresses);
    return connected;
}

// Client handshake from the MIT-licensed gost v2.11.5 obfs.go, via ShadowsocksTlsObfs.cs.
constexpr Byte clientOptions[] = {
    0x00, 0x38,
    0xc0, 0x2c, 0xc0, 0x30, 0x00, 0x9f, 0xcc, 0xa9, 0xcc, 0xa8, 0xcc, 0xaa, 0xc0, 0x2b, 0xc0, 0x2f,
    0x00, 0x9e, 0xc0, 0x24, 0xc0, 0x28, 0x00, 0x6b, 0xc0, 0x23, 0xc0, 0x27, 0x00, 0x67, 0xc0, 0x0a,
    0xc0, 0x14, 0x00, 0x39, 0xc0, 0x09, 0xc0, 0x13, 0x00, 0x33, 0x00, 0x9d, 0x00, 0x9c, 0x00, 0x3d,
    0x00, 0x3c, 0x00, 0x35, 0x00, 0x2f, 0x00, 0xff, 0x01, 0x00
};
constexpr Byte extensions[] = {
    0x00, 0x0b, 0x00, 0x04, 0x03, 0x01, 0x00, 0x02,
    0x00, 0x0a, 0x00, 0x0a, 0x00, 0x08, 0x00, 0x1d, 0x00, 0x17, 0x00, 0x19, 0x00, 0x18,
    0x00, 0x0d, 0x00, 0x20, 0x00, 0x1e, 0x06, 0x01, 0x06, 0x02, 0x06, 0x03, 0x05, 0x01, 0x05, 0x02,
    0x05, 0x03, 0x04, 0x01, 0x04, 0x02, 0x04, 0x03, 0x03, 0x01, 0x03, 0x02, 0x03, 0x03, 0x02, 0x01,
    0x02, 0x02, 0x02, 0x03, 0x00, 0x16, 0x00, 0x00, 0x00, 0x17, 0x00, 0x00
};

bool derive(Byte* output, const Byte* salt, const Byte* master) {
    constexpr Byte info[] = {'s','s','-','s','u','b','k','e','y',1};
    Byte prk[20], blocks[30], tail[20];
    unsigned n;
    bool ok = HMAC(EVP_sha1(), salt, keySize, master, keySize, prk, &n) &&
        HMAC(EVP_sha1(), prk, sizeof(prk), info, sizeof(info), blocks, &n);
    if (ok) {
        memcpy(blocks + 20, info, sizeof(info));
        blocks[29] = 2;
        ok = HMAC(EVP_sha1(), prk, sizeof(prk), blocks, sizeof(blocks), tail, &n) != nullptr;
    }
    if (ok) {
        memcpy(output, blocks, 20);
        memcpy(output + 20, tail, 12);
    }
    OPENSSL_cleanse(prk, sizeof(prk));
    OPENSSL_cleanse(blocks, sizeof(blocks));
    OPENSSL_cleanse(tail, sizeof(tail));
    return ok;
}

struct Channel {
    Socket socket;
    const Profile& profile;
    const Tick deadline;
    CryptoContext* crypto = EVP_CIPHER_CTX_new();
    Byte encryptKey[keySize]{}, decryptKey[keySize]{};
    Byte encryptNonce[12]{}, decryptNonce[12]{};
    Byte plain[maxChunk + tagSize];
    int record = 0, recordLeft = 0, available = 0, offset = 0;
    bool hasReadSalt = false;

    ~Channel() {
        EVP_CIPHER_CTX_free(crypto);
        closesocket(socket);
        OPENSSL_cleanse(encryptKey, sizeof(encryptKey));
        OPENSSL_cleanse(decryptKey, sizeof(decryptKey));
    }

    bool transfer(Byte* data, int length, bool writing) const {
        while (length > 0 && GetTickCount64() < deadline) {
            const int n = writing
                ? send(socket, reinterpret_cast<const char*>(data), length, 0)
                : recv(socket, reinterpret_cast<char*>(data), length, 0);
            if (n > 0) {
                data += n;
                length -= n;
            } else if (n == 0 || WSAGetLastError() != wouldBlock || !ready(socket, writing, deadline)) {
                return false;
            }
        }
        return length == 0;
    }

    bool read(Byte* data, int length) {
        while (length) {
            if (!recordLeft) {
                Byte header[5];
                if (!transfer(header, 5, false)) return false;
                const Byte expected = record == 1 ? 0x14 : record < 3 ? 0x16 : 0x17;
                if (header[0] != expected || header[1] != 3 ||
                    (header[2] != 3 && (record != 0 || header[2] != 1))) return false;
                if (record < 2) {
                    Byte preamble[91];
                    if (!transfer(preamble, record++ == 0 ? 91 : 1, false)) return false;
                    continue;
                }
                record = 3;
                recordLeft = (header[3] << 8) | header[4];
                if (recordLeft > 16384) return false;
                if (!recordLeft) continue;
            }
            const int count = length < recordLeft ? length : recordLeft;
            if (!transfer(data, count, false)) return false;
            data += count;
            length -= count;
            recordLeft -= count;
        }
        return true;
    }

    bool crypt(Byte* bytes, int size, bool encrypt) {
        Byte* key = encrypt ? encryptKey : decryptKey;
        Byte* nonce = encrypt ? encryptNonce : decryptNonce;
        int out = 0, last = 0;
        if (!EVP_CIPHER_CTX_reset(crypto) ||
            !EVP_CipherInit_ex(crypto, EVP_chacha20_poly1305(), nullptr, key, nonce, encrypt ? 1 : 0) ||
            (!encrypt && !EVP_CIPHER_CTX_ctrl(crypto, chachaSetTag, tagSize, bytes + size)) ||
            !EVP_CipherUpdate(crypto, bytes, &out, bytes, size) ||
            !EVP_CipherFinal_ex(crypto, bytes + out, &last) || out + last != size ||
            (encrypt && !EVP_CIPHER_CTX_ctrl(crypto, chachaGetTag, tagSize, bytes + size))) return false;
        for (int index = 0; index < 12 && ++nonce[index] == 0; ++index) {}
        return true;
    }

    bool frame(Byte* output, const Byte* data, int size) {
        put16(output, size);
        memcpy(output + 2 + tagSize, data, size);
        return crypt(output, 2, true) && crypt(output + 2 + tagSize, size, true);
    }

    bool start(const char* destination) {
        const int nameLength = static_cast<int>(strlen(destination));
        Byte target[259] = {3, static_cast<Byte>(nameLength)};
        memcpy(target + 2, destination, nameLength);
        put16(target + 2 + nameLength, 443);
        const int size = nameLength + 4;
        const int payload = keySize + size + 2 + 2 * tagSize;
        const int host = static_cast<int>(strlen(profile.obfsHost));
        const int variable = payload + host;
        Byte hello[217 + keySize + sizeof(target) + 2 + 2 * tagSize + 253]{};
        hello[0] = 0x16; hello[1] = 3; hello[2] = 1;
        put16(hello + 3, 212 + variable);
        hello[5] = 1; put16(hello + 7, 208 + variable);
        hello[9] = 3; hello[10] = 3; hello[43] = 32;
        const auto timestamp = static_cast<Dword>(_time64(nullptr));
        put16(hello + 11, timestamp >> 16);
        put16(hello + 13, timestamp & 0xffff);
        if (!RAND_bytes(hello + 15, 28) || !RAND_bytes(hello + 44, 32)) return false;
        memcpy(hello + 76, clientOptions, sizeof(clientOptions));
        put16(hello + 136, 79 + variable);
        hello[139] = 0x23; put16(hello + 140, payload);
        Byte* salt = hello + 142;
        if (!RAND_bytes(salt, keySize) || !derive(encryptKey, salt, profile.key) ||
            !frame(salt + keySize, target, size)) return false;
        Byte* sni = salt + payload;
        put16(sni + 2, host + 5); put16(sni + 4, host + 3); put16(sni + 7, host);
        memcpy(sni + 9, profile.obfsHost, host);
        memcpy(hello + 151 + variable, extensions, sizeof(extensions));
        return transfer(hello, 217 + variable, true);
    }

    int writeTls(const char* input, int length) {
        Byte encoded[5 + maxChunk + 2 + 2 * tagSize];
        // The TLS-obfs record payload is capped at 16 KiB.
        constexpr int writeLimit = 16384 - 2 - 2 * tagSize;
        int written = 0;
        while (written < length) {
            const int size = length - written < writeLimit ? length - written : writeLimit;
            const int payload = size + 2 + 2 * tagSize;
            encoded[0] = 0x17; encoded[1] = 3; encoded[2] = 3;
            put16(encoded + 3, payload);
            if (!frame(encoded + 5, reinterpret_cast<const Byte*>(input + written), size) ||
                !transfer(encoded, 5 + payload, true)) return -1;
            written += size;
        }
        return written;
    }

    int readTls(char* output, int length) {
        if (!hasReadSalt) {
            Byte salt[keySize];
            if (!read(salt, sizeof(salt)) || !derive(decryptKey, salt, profile.key)) return -1;
            hasReadSalt = true;
        }
        if (!available) {
            Byte size[2 + tagSize];
            if (!read(size, sizeof(size)) || !crypt(size, 2, false)) return -1;
            const int count = (size[0] << 8) | size[1];
            if (!count || count > maxChunk || !read(plain, count + tagSize) || !crypt(plain, count, false)) return -1;
            available = count;
            offset = 0;
        }
        const int count = length < available ? length : available;
        memcpy(output, plain + offset, count);
        available -= count;
        offset += count;
        return count;
    }
};

int bioRead(Bio* bio, char* output, int length) { return static_cast<Channel*>(BIO_get_data(bio))->readTls(output, length); }
int bioWrite(Bio* bio, const char* input, int length) { return static_cast<Channel*>(BIO_get_data(bio))->writeTls(input, length); }
long bioControl(Bio*, int command, long, void*) { return command == 11; } // BIO_CTRL_FLUSH

int request(Ssl* ssl, const Site& site) {
    char message[512];
    char* cursor = message;
    const char* pieces[] = {"GET ", site.path, " HTTP/1.1\r\nHost: ", site.host,
        "\r\nConnection: close\r\n", site.headers, "\r\n"};
    for (auto* part : pieces) {
        const auto size = strlen(part);
        memcpy(cursor, part, size);
        cursor += size;
    }
    const int length = static_cast<int>(cursor - message);
    if (SSL_write(ssl, message, length) != length) return 0;
    char headers[32 * 1024];
    int used = 0;
    while (used < sizeof(headers)) {
        const int count = SSL_read(ssl, headers + used, static_cast<int>(sizeof(headers)) - used);
        if (count <= 0) break;
        const int previous = used;
        used += count;
        for (int end = previous > 3 ? previous : 3; end < used; ++end) {
            if (memcmp(headers + end - 3, "\r\n\r\n", 4) != 0) continue;
            if (used < 12 || memcmp(headers, "HTTP/1.", 7) != 0 || headers[8] != ' ') return 0;
            for (int i = 9; i < 12; ++i) if (headers[i] < '0' || headers[i] > '9') return 0;
            return (headers[9] - '0') * 100 + (headers[10] - '0') * 10 + headers[11] - '0';
        }
    }
    return 0;
}
}

BioMethod* createTransportMethod() {
    auto* method = BIO_meth_new(0x0400 | 128, "Shadowsocks TCP");
    if (!method) return nullptr;
    if (BIO_meth_set_read(method, bioRead) && BIO_meth_set_write(method, bioWrite) &&
        BIO_meth_set_ctrl(method, bioControl)) return method;
    BIO_meth_free(method);
    return nullptr;
}

Result measureSite(const Profile& profile, const Site& site, Tick deadline, SslContext* context, const BioMethod* method) {
    const Tick start = GetTickCount64();
    Channel channel{dial(profile, deadline), profile, deadline};
    int status = 0;
    if (channel.socket != invalidSocket && channel.crypto && channel.start(site.host)) {
        if (auto* ssl = SSL_new(context)) {
            if (auto* bio = BIO_new(method)) {
                BIO_set_data(bio, &channel);
                BIO_set_init(bio, 1);
                SSL_set_bio(ssl, bio, bio);
                if (SSL_set1_host(ssl, site.host) && SSL_ctrl(ssl, 55, 0, const_cast<char*>(site.host)) &&
                    SSL_connect(ssl) == 1) status = request(ssl, site);
            }
            SSL_free(ssl);
        }
    }
    const Tick end = GetTickCount64();
    return {status, static_cast<Dword>(end - start), !status && end >= deadline};
}
