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

extern "C" int strcmp(const char*,const char*);

static int probeShadowsocks(const char* address,const char* port,const char* password,unsigned passwordBytes,const char* cipher,
    const char* obfsHost,const char* host,const char* path,const char* headers,unsigned remaining,
    Handle stop,int* status,bool legacyObfs,char* body=nullptr,unsigned capacity=0,unsigned* used=nullptr) {
    *status=0;
    if(body) { *used=0;if(capacity<65 || *headers)return 0; }
    if(passwordBytes<1 || passwordBytes>4096)return 0;
    if(strlen(host)<1 || strlen(host)>255 || strlen(obfsHost)>253 || strlen(host)+strlen(path)+strlen(headers)>=400)return 0;
    Profile profile{address,port,obfsHost,{},true};
    profile.tlsObfs=legacyObfs || *obfsHost;
    if(strcmp(cipher,"aes-128-gcm")==0) {profile.cipher=EVP_aes_128_gcm();profile.keyLength=16;}
    else if(strcmp(cipher,"aes-192-gcm")==0) {profile.cipher=EVP_aes_192_gcm();profile.keyLength=24;}
    else if(strcmp(cipher,"aes-256-gcm")==0) profile.cipher=EVP_aes_256_gcm();
    else if(strcmp(cipher,"chacha20-ietf-poly1305")==0) profile.cipher=EVP_chacha20_poly1305();
    else return 0;
    if(EVP_BytesToKey(profile.cipher,EVP_md5(),nullptr,reinterpret_cast<const Byte*>(password),
        static_cast<int>(passwordBytes),1,profile.key,nullptr)!=profile.keyLength) {
        OPENSSL_cleanse(profile.key,keySize);return 0;
    }
    bool ok=false;
    const auto result=measureSite(profile,{nullptr,host,path,headers},GetTickCount64()+remaining,context,method,
        stop,body,capacity,used,&ok);
    OPENSSL_cleanse(profile.key,keySize);*status=result.status;
    return body?(ok?1:0):(result.connected?1:0);
}
extern "C" __declspec(dllexport) int ProbeWebsite(const char* address,const char* port,const char* password,
    const char* obfsHost,const char* host,const char* path,const char* headers,unsigned remaining,Handle stop,int* status) {
    return probeShadowsocks(address,port,password,static_cast<unsigned>(strlen(password)),"chacha20-ietf-poly1305",obfsHost,host,path,headers,remaining,stop,status,true);
}
extern "C" __declspec(dllexport) int ProbeEcho(const char* address,const char* port,const char* password,
    const char* obfsHost,const char* host,const char* path,const char* headers,unsigned remaining,Handle stop,
    int* status,char* body,unsigned capacity,unsigned* used) {
    return probeShadowsocks(address,port,password,static_cast<unsigned>(strlen(password)),"chacha20-ietf-poly1305",obfsHost,host,path,headers,remaining,stop,status,true,body,capacity,used);
}
extern "C" __declspec(dllexport) int ProbeShadowsocksWebsiteV2(const char* address,const char* port,const char* password,
    unsigned passwordBytes,const char* cipher,const char* obfsHost,const char* host,const char* path,const char* headers,
    unsigned remaining,Handle stop,int* status) {
    return probeShadowsocks(address,port,password,passwordBytes,cipher,obfsHost,host,path,headers,remaining,stop,status,false);
}
extern "C" __declspec(dllexport) int ProbeShadowsocksEchoV2(const char* address,const char* port,const char* password,
    unsigned passwordBytes,const char* cipher,const char* obfsHost,const char* host,const char* path,const char* headers,
    unsigned remaining,Handle stop,int* status,char* body,unsigned capacity,unsigned* used) {
    return probeShadowsocks(address,port,password,passwordBytes,cipher,obfsHost,host,path,headers,remaining,stop,status,false,body,capacity,used);
}