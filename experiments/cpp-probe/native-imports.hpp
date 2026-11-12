#pragma once
#include "winhttp-imports.hpp"

using Socket = unsigned __int64;
constexpr Socket invalidSocket = ~Socket{0};
struct Address;
struct AddressInfo {
    int flags, family, kind, protocol;
    unsigned __int64 length;
    char* name;
    Address* address;
    AddressInfo* next;
};
struct SocketSet { unsigned count; Socket socket[64]; };
struct TimeValue { long seconds, micros; };

struct CryptoContext;
struct Cipher;
struct Digest;
struct Ssl;
struct SslContext;
struct SslMethod;
struct Bio;
struct BioMethod;

extern "C" {
unsigned __int64 strlen(const char*);
void* memcpy(void*, const void*, unsigned __int64);
void* memset(void*, int, unsigned __int64);
int memcmp(const void*, const void*, unsigned __int64);

__declspec(dllimport) int __stdcall WSAStartup(unsigned short, void*);
__declspec(dllimport) int __stdcall WSACleanup();
__declspec(dllimport) int __stdcall getaddrinfo(const char*, const char*, const AddressInfo*, AddressInfo**);
__declspec(dllimport) void __stdcall freeaddrinfo(AddressInfo*);
__declspec(dllimport) Socket __stdcall socket(int, int, int);
__declspec(dllimport) int __stdcall closesocket(Socket);
__declspec(dllimport) int __stdcall ioctlsocket(Socket, long, Dword*);
__declspec(dllimport) int __stdcall connect(Socket, const Address*, int);
__declspec(dllimport) int __stdcall select(int, SocketSet*, SocketSet*, SocketSet*, const TimeValue*);
__declspec(dllimport) int __stdcall send(Socket, const char*, int, int);
__declspec(dllimport) int __stdcall recv(Socket, char*, int, int);
__declspec(dllimport) int __stdcall getsockopt(Socket, int, int, char*, int*);
__declspec(dllimport) int __stdcall WSAGetLastError();
__declspec(dllimport) Bool __stdcall ReadFile(Handle, void*, Dword, Dword*, void*);
__declspec(dllimport) long __stdcall InterlockedIncrement(volatile long*);

CryptoContext* EVP_CIPHER_CTX_new();
void EVP_CIPHER_CTX_free(CryptoContext*);
int EVP_CIPHER_CTX_reset(CryptoContext*);
const Cipher* EVP_chacha20_poly1305();
const Digest* EVP_sha1();
const Digest* EVP_md5();
int EVP_BytesToKey(const Cipher*, const Digest*, const unsigned char*, const unsigned char*, int, int,
    unsigned char*, unsigned char*);
unsigned char* HMAC(const Digest*, const void*, int, const unsigned char*, unsigned __int64, unsigned char*, unsigned*);
int EVP_CipherInit_ex(CryptoContext*, const Cipher*, void*, const unsigned char*, const unsigned char*, int);
int EVP_CIPHER_CTX_ctrl(CryptoContext*, int, int, void*);
int EVP_CipherUpdate(CryptoContext*, unsigned char*, int*, const unsigned char*, int);
int EVP_CipherFinal_ex(CryptoContext*, unsigned char*, int*);
int RAND_bytes(unsigned char*, int);
void OPENSSL_cleanse(void*, unsigned __int64);
BioMethod* BIO_meth_new(int, const char*);
void BIO_meth_free(BioMethod*);
int BIO_meth_set_read(BioMethod*, int (*)(Bio*, char*, int));
int BIO_meth_set_write(BioMethod*, int (*)(Bio*, const char*, int));
int BIO_meth_set_ctrl(BioMethod*, long (*)(Bio*, int, long, void*));
Bio* BIO_new(const BioMethod*);
void BIO_set_data(Bio*, void*);
void* BIO_get_data(Bio*);
void BIO_set_init(Bio*, int);
const SslMethod* TLS_client_method();
SslContext* SSL_CTX_new(const SslMethod*);
void SSL_CTX_free(SslContext*);
int SSL_CTX_load_verify_locations(SslContext*, const char*, const char*);
void SSL_CTX_set_verify(SslContext*, int, void*);
Ssl* SSL_new(SslContext*);
void SSL_free(Ssl*);
void SSL_set_bio(Ssl*, Bio*, Bio*);
int SSL_set1_host(Ssl*, const char*);
long SSL_ctrl(Ssl*, int, long, void*);
int SSL_connect(Ssl*);
int SSL_write(Ssl*, const void*, int);
int SSL_read(Ssl*, void*, int);
}
