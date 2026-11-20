// Mieru v3.29.0 TCP framing/key derivation adapted from official GPL-3.0-or-later source.
#include "shadowsocks.cpp"
extern "C" {
Byte* SHA256(const Byte*, unsigned __int64, Byte*);
const Digest* EVP_sha256();
int PKCS5_PBKDF2_HMAC(const char*, int, const Byte*, int, int, const Digest*, int, Byte*);
int BIO_free(Bio*);
void crypto_aead_lock(Byte*, Byte*, const Byte*, const Byte*, const Byte*, unsigned __int64, const Byte*, unsigned __int64);
int crypto_aead_unlock(Byte*, const Byte*, const Byte*, const Byte*, const Byte*, unsigned __int64, const Byte*, unsigned __int64);
__declspec(dllexport) void ProbeShutdown();
}
namespace {
SslContext* mieruContext;
BioMethod* mieruMethod;
void put32(Byte* out, unsigned n) {
    for (int i = 3; i >= 0; --i) {
        out[i] = static_cast<Byte>(n);
        n >>= 8;
    }
}
unsigned get32(const Byte* in) {
    return (unsigned(in[0]) << 24) | (unsigned(in[1]) << 16) | (unsigned(in[2]) << 8) | in[3];
}
struct MieruChannel {
    Socket connection;
    Tick deadline;
    Handle stop;
    Byte key[32]{}, sendNonce[24]{}, recvNonce[24]{};
    unsigned session = 0, sequence = 0, receiveSequence = 0;
    bool firstSend = true, firstReceive = true;
    Byte plain[65535];
    int available = 0, offset = 0;
    ~MieruChannel() {
        closesocket(connection);
        OPENSSL_cleanse(key, sizeof(key));
        OPENSSL_cleanse(sendNonce, sizeof(sendNonce));
        OPENSSL_cleanse(recvNonce, sizeof(recvNonce));
    }
    bool transfer(Byte* data, int length, bool writing) const {
        while (length && GetTickCount64() < deadline && !cancelled(stop)) {
            int n = writing ? send(connection, reinterpret_cast<char*>(data), length, 0)
                : recv(connection, reinterpret_cast<char*>(data), length, 0);
            if (n > 0) {
                data += n;
                length -= n;
            } else if (n == 0 || WSAGetLastError() != wouldBlock || !ready(connection, writing, deadline, stop)) {
                return false;
            }
        }
        return length == 0;
    }
    static void increase(Byte* nonce) {
        for (int i = 23; i >= 0 && ++nonce[i] == 0; --i) {}
    }
    bool encrypt(const Byte* data, int length) {
        Byte encrypted[16384 + 16];
        if (length > 16384) return false;
        if (firstSend) {
            if (RAND_bytes(sendNonce, 24) != 1 || !transfer(sendNonce, 24, true)) return false;
            firstSend = false;
        } else increase(sendNonce);
        crypto_aead_lock(encrypted, encrypted + length, key, sendNonce, nullptr, 0, data, length);
        return transfer(encrypted, length + 16, true);
    }
    bool decrypt(Byte* data, int length) {
        Byte tag[16];
        if (firstReceive) {
            if (!transfer(recvNonce, 24, false)) return false;
            firstReceive = false;
        }
        else increase(recvNonce);
        if (!transfer(data, length, false) || !transfer(tag, 16, false)) return false;
        return crypto_aead_unlock(data, tag, key, recvNonce, nullptr, 0, data, length) == 0;
    }
    bool frame(Byte protocol, const Byte* data, int length) {
        Byte meta[32]{};
        meta[0] = protocol;
        put32(meta + 2, static_cast<unsigned>(_time64(nullptr) / 60));
        put32(meta + 6, session);
        put32(meta + 10, sequence++);
        if (protocol == 2) put16(meta + 15, length);
        else {
            put32(meta + 14, receiveSequence);
            put16(meta + 18, 256);
            put16(meta + 22, length);
        }
        return encrypt(meta, 32) && (!length || encrypt(data, length));
    }
    bool padding(int count) const {
        Byte discard[255];
        return !count || transfer(discard, count, false);
    }
    int read(char* data, int length) {
        while (!available) {
            Byte meta[32];
            if (!decrypt(meta, 32) || get32(meta + 6) != session) return -1;
            const auto now = static_cast<unsigned>(_time64(nullptr) / 60), stamp = get32(meta + 2);
            if (stamp + 1 < now || stamp > now + 1) return -1;
            int payload = 0, suffix = 0, prefix = 0;
            if (meta[0] == 3) {
                if (meta[14] || get32(meta + 10) != receiveSequence++) return -1;
                payload = (meta[15] << 8) | meta[16];
                suffix = meta[17];
                if (payload > 1024) return -1;
            } else if (meta[0] == 7 || meta[0] == 9) {
                if (meta[0] == 7 && get32(meta + 10) != receiveSequence++) return -1;
                prefix = meta[21];
                payload = (meta[22] << 8) | meta[23];
                suffix = meta[24];
            } else return 0;
            if (!padding(prefix) || (payload && !decrypt(plain, payload)) || !padding(suffix)) return -1;
            offset = 0;
            available = payload;
        }
        int count = length < available ? length : available;
        memcpy(data, plain + offset, count);
        offset += count;
        available -= count;
        return count;
    }
    bool exact(Byte* data, int length) {
        while (length) {
            int n = read(reinterpret_cast<char*>(data), length);
            if (n <= 0) return false;
            data += n;
            length -= n;
        }
        return true;
    }
};
int mieruRead(Bio* bio, char* data, int length) {
    return static_cast<MieruChannel*>(BIO_get_data(bio))->read(data, length);
}
int mieruWrite(Bio* bio, const char* data, int length) {
    auto* c = static_cast<MieruChannel*>(BIO_get_data(bio));
    int left = length;
    while (left) {
        int n = left < 16384 ? left : 16384;
        if (!c->frame(6, reinterpret_cast<const Byte*>(data), n)) return -1;
        left -= n;
        data += n;
    }
    return length;
}
}
extern "C" __declspec(dllexport) int ProbeInitialize(const char* caBundle) {
    alignas(8) Byte winsock[512];
    if (WSAStartup(0x0202, winsock)) return 0;
    mieruContext = SSL_CTX_new(TLS_client_method());
    mieruMethod = BIO_meth_new(0x0480, "Mieru TCP stream");
    if (!mieruContext || !mieruMethod || !BIO_meth_set_read(mieruMethod, mieruRead) ||
        !BIO_meth_set_write(mieruMethod, mieruWrite) || !BIO_meth_set_ctrl(mieruMethod, bioControl) ||
        SSL_CTX_load_verify_locations(mieruContext, caBundle, nullptr) != 1) {
        ProbeShutdown();
        return 0;
    }
    SSL_CTX_set_verify(mieruContext, 1, nullptr);
    return 1;
}
extern "C" __declspec(dllexport) void ProbeShutdown() {
    SSL_CTX_free(mieruContext);
    BIO_meth_free(mieruMethod);
    WSACleanup();
    mieruContext = nullptr;
    mieruMethod = nullptr;
}
extern "C" __declspec(dllexport) int ProbeMieruWebsite(const char* address, const char* port,
    const char* username, const char* password, const char* host, const char* path, const char* headers,
    unsigned remaining, Handle stop, int* status) {
    *status = 0;
    Tick deadline = GetTickCount64() + remaining;
    int userLen = static_cast<int>(strlen(username)), passLen = static_cast<int>(strlen(password)),
        hostLen = static_cast<int>(strlen(host));
    if (userLen < 1 || userLen > 255 || passLen < 1 || passLen > 1024 || hostLen < 1 || hostLen > 255) return 0;
    // One endpoint selection per probe, matching a TCP port binding/range. No hidden retry.
    unsigned firstPort = 0, lastPort = 0;
    const char* cursor = port;
    while (*cursor >= '0' && *cursor <= '9') {
        firstPort = firstPort * 10 + (*cursor++ - '0');
        if (firstPort > 65535) return 0;
    }
    if (!firstPort) return 0;
    lastPort = firstPort;
    if (*cursor == '-') {
        ++cursor;
        lastPort = 0;
        while (*cursor >= '0' && *cursor <= '9') {
            lastPort = lastPort * 10 + (*cursor++ - '0');
            if (lastPort > 65535) return 0;
        }
    }
    if (*cursor || lastPort < firstPort) return 0;
    unsigned choice = 0;
    if (lastPort != firstPort && RAND_bytes(reinterpret_cast<Byte*>(&choice), sizeof(choice)) != 1) return 0;
    unsigned selected = firstPort + choice % (lastPort - firstPort + 1);
    char selectedPort[6];
    int digits = 0;
    do {
        selectedPort[digits++] = static_cast<char>('0' + selected % 10);
        selected /= 10;
    } while (selected);
    for (int i = 0; i < digits / 2; ++i) {
        char digit = selectedPort[i];
        selectedPort[i] = selectedPort[digits - 1 - i];
        selectedPort[digits - 1 - i] = digit;
    }
    selectedPort[digits] = 0;
    Profile profile{address, selectedPort, nullptr, {}, true};
    MieruChannel channel{dial(profile, deadline, stop), deadline, stop};
    if (channel.connection == invalidSocket) return 0;
    Byte credentials[1280], passwordHash[32], salt[32], timeBytes[8]{};
    memcpy(credentials, password, passLen);
    credentials[passLen] = 0;
    memcpy(credentials + passLen + 1, username, userLen);
    SHA256(credentials, passLen + 1 + userLen, passwordHash);
    OPENSSL_cleanse(credentials, sizeof(credentials));
    auto rounded = static_cast<unsigned __int64>((_time64(nullptr) + 60) / 120 * 120);
    for (int i = 7; i >= 0; --i) {
        timeBytes[i] = static_cast<Byte>(rounded);
        rounded >>= 8;
    }
    SHA256(timeBytes, 8, salt);
    int derived = PKCS5_PBKDF2_HMAC(reinterpret_cast<char*>(passwordHash), 32, salt, 32, 64, EVP_sha256(), 32, channel.key);
    OPENSSL_cleanse(passwordHash, 32);
    OPENSSL_cleanse(salt, 32);
    if (derived != 1 || RAND_bytes(reinterpret_cast<Byte*>(&channel.session), 4) != 1) return 0;
    if (!channel.session) channel.session = 1;
    Byte requestBytes[262]{5, 1, 0, 3, static_cast<Byte>(hostLen)};
    memcpy(requestBytes + 5, host, hostLen);
    put16(requestBytes + 5 + hostLen, 443);
    if (!channel.frame(2, requestBytes, hostLen + 7)) return 0;
    Byte reply[5];
    if (!channel.exact(reply, 4) || reply[0] != 5 || reply[1] != 0 || reply[2] != 0) return 0;
    int addrLen = reply[3] == 1 ? 4 : reply[3] == 4 ? 16 : 0;
    if (reply[3] == 3) {
        if (!channel.exact(reply + 4, 1)) return 0;
        addrLen = reply[4];
    }
    if (!addrLen) return 0;
    Byte boundAddress[257];
    if (!channel.exact(boundAddress, addrLen + 2)) return 0;
    Bio* bio = BIO_new(mieruMethod);
    Ssl* ssl = SSL_new(mieruContext);
    if (!bio || !ssl) {
        if (bio) BIO_free(bio);
        if (ssl) SSL_free(ssl);
        return 0;
    }
    BIO_set_data(bio, &channel);
    BIO_set_init(bio, 1);
    SSL_set_bio(ssl, bio, bio);
    int ok = 0;
    if (SSL_set1_host(ssl, host) == 1 && SSL_ctrl(ssl, 55, 0, const_cast<char*>(host)) == 1 && SSL_connect(ssl) == 1) {
        // Same fixed-target HTTP request as the existing native probe, bounded at this ABI boundary.
        if (strlen(path) + strlen(host) + strlen(headers) < 400) *status = request(ssl, {nullptr, host, path, headers});
        ok = *status > 0 ? 1 : 0;
    }
    SSL_free(ssl);
    return ok;
}
