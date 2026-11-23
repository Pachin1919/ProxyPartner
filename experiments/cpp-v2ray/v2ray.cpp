#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <openssl/ssl.h>
#include <openssl/evp.h>
#include <openssl/hmac.h>
#include <openssl/rand.h>
#include <openssl/x509v3.h>
#include <array>
#include <vector>
#include <span>
#include <string>
#include <string_view>
#include <algorithm>
#include <memory>
#include <ctime>
#include <cstring>
#include "v2ray.hpp"
#include "../cpp-probe/echo_http.hpp"
namespace {
using Byte=unsigned char;
using Bytes=std::vector<Byte>;
using View=std::span<const Byte>;
using Hash=std::array<Byte,32>;
SSL_CTX* context=nullptr;
BIO_METHOD* bioMethod=nullptr;
struct IO {
    virtual int read(char*,int)=0;
    virtual int write(const char*,int)=0;
    virtual ~IO()=default;
    bool exact(void* data,int size) {auto p=static_cast<char*>(data);while(size>0){int n=read(p,size);if(n<=0)return false;p+=n;size-=n;}return true;}
    bool send(View bytes){return write(reinterpret_cast<const char*>(bytes.data()),static_cast<int>(bytes.size()))==static_cast<int>(bytes.size());}
};
void be16(Byte* p,unsigned n){p[0]=static_cast<Byte>(n>>8);p[1]=static_cast<Byte>(n);}
unsigned u16(const Byte* p){return unsigned(p[0])*256+p[1];}
Bytes digest(const EVP_MD* method,View data){Bytes out(EVP_MD_get_size(method));unsigned n=0;if(EVP_Digest(data.data(),data.size(),out.data(),&n,method,nullptr)!=1||n!=out.size())return {};return out;}
std::string base64(View data){std::string out(4*((data.size()+2)/3),0);EVP_EncodeBlock(reinterpret_cast<Byte*>(out.data()),data.data(),static_cast<int>(data.size()));return out;}
bool crypt(const EVP_CIPHER* cipher,View key,View nonce,View aad,View input,Bytes& out,bool encrypt){
    if(!encrypt && input.size()<16)return false;
    auto ctx=std::unique_ptr<EVP_CIPHER_CTX,decltype(&EVP_CIPHER_CTX_free)>(EVP_CIPHER_CTX_new(),EVP_CIPHER_CTX_free);
    if(!ctx)return false;
    int n=0,count=static_cast<int>(input.size())-(encrypt?0:16),last=0;
    out.resize(count+16);
    if(EVP_CipherInit_ex(ctx.get(),cipher,nullptr,key.data(),nonce.data(),encrypt?1:0)!=1 ||
        (!encrypt && EVP_CIPHER_CTX_ctrl(ctx.get(),EVP_CTRL_AEAD_SET_TAG,16,const_cast<Byte*>(input.data()+count))!=1) ||
        (!aad.empty() && EVP_CipherUpdate(ctx.get(),nullptr,&n,aad.data(),static_cast<int>(aad.size()))!=1) ||
        EVP_CipherUpdate(ctx.get(),out.data(),&n,input.data(),count)!=1 || n!=count ||
        EVP_CipherFinal_ex(ctx.get(),out.data()+n,&last)!=1 || last!=0 ||
        (encrypt && EVP_CIPHER_CTX_ctrl(ctx.get(),EVP_CTRL_AEAD_GET_TAG,16,out.data()+count)!=1)) {OPENSSL_cleanse(out.data(),out.size());out.clear();return false;}
    out.resize(count+(encrypt?16:0));return true;
}
struct SocketIO:IO {
    SOCKET socket=INVALID_SOCKET;ULONGLONG deadline;HANDLE stop;
    SocketIO(unsigned remaining,HANDLE cancel):deadline(GetTickCount64()+remaining),stop(cancel){}
    ~SocketIO(){if(socket!=INVALID_SOCKET)closesocket(socket);}
    bool active() const{return GetTickCount64()<deadline && (!stop||WaitForSingleObject(stop,0)!=WAIT_OBJECT_0);}
    bool ready(bool writing){while(active()){fd_set set;FD_ZERO(&set);FD_SET(socket,&set);timeval t{0,50000};int n=select(0,writing?nullptr:&set,writing?&set:nullptr,nullptr,&t);if(n>0)return true;if(n<0)return false;}return false;}
    bool connectTo(const char* address,const char* port){
        addrinfo hints{};hints.ai_flags=AI_NUMERICHOST|AI_NUMERICSERV;hints.ai_socktype=SOCK_STREAM;hints.ai_protocol=IPPROTO_TCP;
        addrinfo* raw=nullptr;if(getaddrinfo(address,port,&hints,&raw))return false;
        std::unique_ptr<addrinfo,decltype(&freeaddrinfo)> list(raw,freeaddrinfo);
        for(auto it=raw;it && active();it=it->ai_next){
            socket=::socket(it->ai_family,it->ai_socktype,it->ai_protocol);if(socket==INVALID_SOCKET)continue;
            u_long mode=1;int error=0,len=sizeof(error);BOOL yes=TRUE;
            if(ioctlsocket(socket,FIONBIO,&mode)==0 && setsockopt(socket,IPPROTO_TCP,TCP_NODELAY,reinterpret_cast<const char*>(&yes),sizeof(yes))==0 &&
                (::connect(socket,it->ai_addr,static_cast<int>(it->ai_addrlen))==0 ||
                 WSAGetLastError()==WSAEWOULDBLOCK && ready(true) && getsockopt(socket,SOL_SOCKET,SO_ERROR,reinterpret_cast<char*>(&error),&len)==0 && !error))return true;
            closesocket(socket);socket=INVALID_SOCKET;
        }return false;
    }
    int read(char* p,int n) override{while(active()){int count=recv(socket,p,n,0);if(count>=0)return count;if(WSAGetLastError()!=WSAEWOULDBLOCK||!ready(false))return -1;}return -1;}
    int write(const char* p,int n) override{int done=0;while(done<n && active()){int count=::send(socket,p+done,n-done,0);if(count>0)done+=count;else if(count==0 || WSAGetLastError()!=WSAEWOULDBLOCK || !ready(true))return -1;}return done==n?done:-1;}
};
int bioRead(BIO* b,char* p,int n){return static_cast<IO*>(BIO_get_data(b))->read(p,n);}
int bioWrite(BIO* b,const char* p,int n){return static_cast<IO*>(BIO_get_data(b))->write(p,n);}
long bioCtrl(BIO*,int op,long,void*){return op==BIO_CTRL_FLUSH?1:0;}
struct TlsIO:IO {
    SSL* ssl=nullptr;
    ~TlsIO(){SSL_free(ssl);}
    bool start(IO& lower,const char* name,const char* alpn){
        ssl=SSL_new(context);BIO* bio=BIO_new(bioMethod);if(!ssl||!bio){BIO_free(bio);return false;}
        BIO_set_data(bio,&lower);BIO_set_init(bio,1);SSL_set_bio(ssl,bio,bio);
        Byte ip[16];bool numeric=InetPtonA(AF_INET,name,ip)==1||InetPtonA(AF_INET6,name,ip)==1;
        if(numeric ? X509_VERIFY_PARAM_set1_ip_asc(SSL_get0_param(ssl),name)!=1 :
            X509_VERIFY_PARAM_set1_host(SSL_get0_param(ssl),name,0)!=1 || SSL_set_tlsext_host_name(ssl,name)!=1)return false;
        Bytes protocols;std::string_view list=alpn;

        while(!list.empty()){auto end=list.find(',');auto token=list.substr(0,end);if(token.empty()||token.size()>255)return false;protocols.push_back(static_cast<Byte>(token.size()));protocols.insert(protocols.end(),token.begin(),token.end());if(end==list.npos)break;list.remove_prefix(end+1);}
        if(!protocols.empty() && SSL_set_alpn_protos(ssl,protocols.data(),static_cast<unsigned>(protocols.size()))!=0)return false;
        return SSL_connect(ssl)==1;
    }
    int read(char* p,int n)override{return SSL_read(ssl,p,n);}
    int write(const char* p,int n)override{int done=0;while(done<n){int count=SSL_write(ssl,p+done,n-done);if(count<=0)return -1;done+=count;}return done;}
};
std::string lower(std::string text){for(auto& c:text)if(c>='A'&&c<='Z')c+='a'-'A';return text;}
struct WebIO:IO {
    IO& wire;bool frames;uint64_t left=0;bool fragmented=false;unsigned controls=0;
    WebIO(IO& io,bool ws):wire(io),frames(ws){}
    bool frame(unsigned op,const char* p,int n){
        if(n<0||n>65535)return false;
        Bytes packet;packet.reserve(static_cast<size_t>(n)+8);packet.push_back(static_cast<Byte>(0x80|op));
        if(n<126)packet.push_back(static_cast<Byte>(0x80|n));else{packet.push_back(0xfe);packet.push_back(static_cast<Byte>(n>>8));packet.push_back(static_cast<Byte>(n));}
        Byte mask[4];if(RAND_bytes(mask,4)!=1)return false;packet.insert(packet.end(),mask,mask+4);
        for(int i=0;i<n;++i)packet.push_back(static_cast<Byte>(p[i])^mask[i%4]);return wire.send(packet);
    }
    bool start(const char* path,const char* host){
        Byte keyBytes[16];if(RAND_bytes(keyBytes,16)!=1)return false;
        auto key=base64(keyBytes);
        std::string request="GET "+std::string(path)+" HTTP/1.1\r\nHost: "+host+"\r\nConnection: Upgrade\r\nUpgrade: websocket\r\n";
        if(frames)request+="Sec-WebSocket-Version: 13\r\nSec-WebSocket-Key: "+key+"\r\n";
        request+="\r\n";
        if(wire.write(request.data(),static_cast<int>(request.size()))!=static_cast<int>(request.size()))return false;
        std::string header;char c;
        while(header.size()<16384 && !header.ends_with("\r\n\r\n")){if(!wire.exact(&c,1))return false;header+=c;}
        if(!header.ends_with("\r\n\r\n") || !header.starts_with("HTTP/1.1 101 "))return false;
        std::string accept,upgrade,connection;size_t at=header.find("\r\n")+2;
        while(at<header.size()-2){auto end=header.find("\r\n",at);auto colon=header.find(':',at);if(colon==header.npos||colon>=end)return false;auto name=lower(header.substr(at,colon-at));auto value=header.substr(colon+1,end-colon-1);auto start=value.find_first_not_of(" \t");value=start==value.npos?"":value.substr(start,value.find_last_not_of(" \t")-start+1);if(name=="sec-websocket-accept"){if(!accept.empty())return false;accept=value;}if(name=="upgrade")upgrade=lower(value);if(name=="connection")connection=lower(value);at=end+2;}
        if(upgrade!="websocket" || connection.find("upgrade")==connection.npos)return false;
        if(frames){auto text=key+"258EAFA5-E914-47DA-95CA-C5AB0DC85B11";auto hash=digest(EVP_sha1(),View(reinterpret_cast<const Byte*>(text.data()),text.size()));if(hash.empty()||accept!=base64(hash))return false;}
        return true;
    }
    int read(char* p,int n)override{
        if(!frames)return wire.read(p,n);
        while(!left){
            Byte header[2];if(!wire.exact(header,2))return -1;unsigned op=header[0]&15;bool fin=header[0]&0x80;
            if((header[0]&0x70)||(header[1]&0x80))return -1;
            left=header[1]&127;
            if(left==126){Byte length[2];if(!wire.exact(length,2))return -1;left=u16(length);}else if(left==127){Byte length[8];if(!wire.exact(length,8))return -1;left=0;for(auto c:length)left=(left<<8)|c;}
            if(left>1024*1024)return -1;
            if(op>=8){if(!fin||(header[1]&127)>125||left>125||++controls>128)return -1;char payload[125];if(!wire.exact(payload,static_cast<int>(left)))return -1;left=0;if(op==8)return 0;if(op==9){if(!frame(10,payload,header[1]&127))return -1;}else if(op!=10)return -1;continue;}
            if(op==2){if(fragmented)return -1;fragmented=!fin;}else if(op==0){if(!fragmented)return -1;fragmented=!fin;}else return -1;
        }
        int count=static_cast<int>((std::min)(left,static_cast<uint64_t>(n)));if(!wire.exact(p,count))return -1;left-=count;return count;
    }
    int write(const char* p,int n)override{if(!frames)return wire.write(p,n);int done=0;while(done<n){int count=(std::min)(n-done,16384);if(!frame(2,p+done,count))return -1;done+=count;}return done;}
};
Hash vmHash(View input,const std::vector<View>& paths,size_t level,bool& ok){
    Hash out{};
    if(!level){unsigned n=0;constexpr char seed[]="VMess AEAD KDF";if(!HMAC(EVP_sha256(),seed,sizeof(seed)-1,input.data(),input.size(),out.data(),&n)||n!=32)ok=false;return out;}
    Byte innerPad[64],outerPad[64];std::fill_n(innerPad,64,Byte(0x36));std::fill_n(outerPad,64,Byte(0x5c));
    auto key=paths[level-1];for(size_t i=0;i<key.size();++i){innerPad[i]^=key[i];outerPad[i]^=key[i];}
    Bytes inner(innerPad,innerPad+64);inner.insert(inner.end(),input.begin(),input.end());
    auto hashed=vmHash(inner,paths,level-1,ok);Bytes outer(outerPad,outerPad+64);outer.insert(outer.end(),hashed.begin(),hashed.end());
    out=vmHash(outer,paths,level-1,ok);OPENSSL_cleanse(inner.data(),inner.size());OPENSSL_cleanse(outer.data(),outer.size());return out;
}
Hash kdf(View key,std::string_view label,bool& ok,View auth={},View nonce={}){
    std::vector<View> paths{View(reinterpret_cast<const Byte*>(label.data()),label.size())};if(!auth.empty()){paths.push_back(auth);paths.push_back(nonce);}return vmHash(key,paths,paths.size(),ok);
}
struct ProtocolIO:IO {
    IO& wire;const V2RayConfigV1& config;Byte uuid[16]{};
    Bytes txKey,rxKey,txIv,rxIv,plain;size_t offset=0;unsigned txCounter=0,rxCounter=0;Byte verifier=0;bool response=false;
    ProtocolIO(IO& lower,const V2RayConfigV1& cfg):wire(lower),config(cfg){}
    ~ProtocolIO(){for(auto p:{&txKey,&rxKey,&txIv,&rxIv,&plain})OPENSSL_cleanse(p->data(),p->size());OPENSSL_cleanse(uuid,sizeof(uuid));}
    bool start(const char* host){
        std::string text=config.uuid;
        if(text.size()==38 && ((text.front()=='{' && text.back()=='}') || (text.front()=='(' && text.back()==')')))text=text.substr(1,36);
        if(text.size()!=36 && text.size()!=32)return false;
        auto hex=[](char c){return c>='0'&&c<='9'?c-'0':c>='a'&&c<='f'?c-'a'+10:c>='A'&&c<='F'?c-'A'+10:-1;};
        size_t at=0;for(size_t i=0;i<text.size();++i){if(text.size()==36 && (i==8||i==13||i==18||i==23)){if(text[i]!='-')return false;continue;}if(i+1>=text.size()||at>=16)return false;int high=hex(text[i]),low=hex(text[++i]);if(high<0||low<0)return false;uuid[at++]=static_cast<Byte>((high<<4)|low);}if(at!=16)return false;
        const size_t hostLength=strlen(host);if(!hostLength || hostLength>255)return false;
        if(config.protocol==1){Bytes header{0};header.insert(header.end(),uuid,uuid+16);header.insert(header.end(),{0,1,1,187,2,static_cast<Byte>(hostLength)});header.insert(header.end(),host,host+hostLength);return wire.send(header);}
        txIv.resize(16);txKey.resize(16);if(RAND_bytes(txIv.data(),16)!=1||RAND_bytes(txKey.data(),16)!=1||RAND_bytes(&verifier,1)!=1)return false;
        auto hashedKey=digest(EVP_sha256(),txKey),hashedIv=digest(EVP_sha256(),txIv);if(hashedKey.size()!=32||hashedIv.size()!=32)return false;
        rxKey.assign(hashedKey.begin(),hashedKey.begin()+16);rxIv.assign(hashedIv.begin(),hashedIv.begin()+16);
        constexpr char magic[]="c48619fe-8f02-49e0-b9e9-edf763e17e21";Bytes input(uuid,uuid+16);input.insert(input.end(),magic,magic+36);auto cmd=digest(EVP_md5(),input);OPENSSL_cleanse(input.data(),input.size());if(cmd.size()!=16)return false;
        Byte auth[16];uint64_t now=static_cast<uint64_t>(time(nullptr));for(int i=7;i>=0;--i){auth[i]=static_cast<Byte>(now);now>>=8;}if(RAND_bytes(auth+8,4)!=1)return false;
        uint32_t crc=~uint32_t(0);for(int i=0;i<12;++i){crc^=auth[i];for(int bit=0;bit<8;++bit)crc=(crc>>1)^(0xedb88320U&static_cast<uint32_t>(-static_cast<int>(crc&1)));}crc=~crc;for(int i=15;i>=12;--i){auth[i]=static_cast<Byte>(crc);crc>>=8;}
        bool ok=true;auto authKey=kdf(cmd,"AES Auth ID Encryption",ok);if(!ok)return false;
        auto ctx=std::unique_ptr<EVP_CIPHER_CTX,decltype(&EVP_CIPHER_CTX_free)>(EVP_CIPHER_CTX_new(),EVP_CIPHER_CTX_free);int n=0,last=0;Byte authId[32];
        if(!ctx || EVP_EncryptInit_ex(ctx.get(),EVP_aes_128_ecb(),nullptr,authKey.data(),nullptr)!=1 || EVP_CIPHER_CTX_set_padding(ctx.get(),0)!=1 || EVP_EncryptUpdate(ctx.get(),authId,&n,auth,16)!=1 || n!=16 || EVP_EncryptFinal_ex(ctx.get(),authId+n,&last)!=1 || last)return false;
        Bytes command{1};command.insert(command.end(),txIv.begin(),txIv.end());command.insert(command.end(),txKey.begin(),txKey.end());command.insert(command.end(),{verifier,1,static_cast<Byte>(config.cipher),0,1,1,187,2,static_cast<Byte>(hostLength)});command.insert(command.end(),host,host+hostLength);
        uint32_t fnv=2166136261U;for(auto b:command)fnv=(fnv^b)*16777619U;for(int i=3;i>=0;--i)command.push_back(static_cast<Byte>(fnv>>(i*8)));
        Byte connectionNonce[8],size[2];if(RAND_bytes(connectionNonce,8)!=1)return false;be16(size,static_cast<unsigned>(command.size()));
        auto lengthKey=kdf(cmd,"VMess Header AEAD Key_Length",ok,View(authId,16),connectionNonce),lengthNonce=kdf(cmd,"VMess Header AEAD Nonce_Length",ok,View(authId,16),connectionNonce);
        auto payloadKey=kdf(cmd,"VMess Header AEAD Key",ok,View(authId,16),connectionNonce),payloadNonce=kdf(cmd,"VMess Header AEAD Nonce",ok,View(authId,16),connectionNonce);
        Bytes lengthBlock,payload;
        if(!ok || !crypt(EVP_aes_128_gcm(),lengthKey,View(lengthNonce.data(),12),View(authId,16),size,lengthBlock,true) || !crypt(EVP_aes_128_gcm(),payloadKey,View(payloadNonce.data(),12),View(authId,16),command,payload,true))return false;
        Bytes packet(authId,authId+16);packet.insert(packet.end(),lengthBlock.begin(),lengthBlock.end());packet.insert(packet.end(),connectionNonce,connectionNonce+8);packet.insert(packet.end(),payload.begin(),payload.end());OPENSSL_cleanse(command.data(),command.size());OPENSSL_cleanse(cmd.data(),cmd.size());return wire.send(packet);
    }
    bool responseHeader(){
        if(response)return true;
        if(config.protocol==1){Byte header[2];if(!wire.exact(header,2)||header[0]!=0)return false;Byte addons[255];if(!wire.exact(addons,header[1]))return false;}
        else {
            bool ok=true;auto lk=kdf(rxKey,"AEAD Resp Header Len Key",ok),ln=kdf(rxIv,"AEAD Resp Header Len IV",ok),hk=kdf(rxKey,"AEAD Resp Header Key",ok),hn=kdf(rxIv,"AEAD Resp Header IV",ok);
            Byte sealedLength[18];Bytes length,header;if(!ok||!wire.exact(sealedLength,18)||!crypt(EVP_aes_128_gcm(),lk,View(ln.data(),12),{},sealedLength,length,false))return false;
            unsigned size=u16(length.data());if(size<4||size>1024)return false;Bytes sealed(size+16);if(!wire.exact(sealed.data(),static_cast<int>(sealed.size()))||!crypt(EVP_aes_128_gcm(),hk,View(hn.data(),12),{},sealed,header,false)||header[0]!=verifier||header[2]!=0||header[3]!=0)return false;
        }response=true;return true;
    }
    bool bodyCrypt(bool writing,View input,Bytes& out){
        auto& key=writing?txKey:rxKey;auto& iv=writing?txIv:rxIv;auto& counter=writing?txCounter:rxCounter;
        if(counter>65535)return false;Byte nonce[12];memcpy(nonce,iv.data(),12);be16(nonce,counter++);Bytes expanded;
        if(config.cipher==4){auto first=digest(EVP_md5(),key);if(first.size()!=16)return false;auto second=digest(EVP_md5(),first);if(second.size()!=16)return false;expanded=first;expanded.insert(expanded.end(),second.begin(),second.end());}
        const auto cipher=config.cipher==4?EVP_chacha20_poly1305():EVP_aes_128_gcm();bool ok=crypt(cipher,expanded.empty()?View(key):View(expanded),nonce,{},input,out,writing);OPENSSL_cleanse(expanded.data(),expanded.size());return ok;
    }
    int read(char* p,int n)override{
        if(!responseHeader())return -1;
        if(config.protocol==1)return wire.read(p,n);
        if(offset==plain.size()){
            Byte length[2];if(!wire.exact(length,2))return -1;unsigned size=u16(length);if(size<16)return -1;Bytes sealed(size);if(!wire.exact(sealed.data(),static_cast<int>(size))||!bodyCrypt(false,sealed,plain))return -1;offset=0;if(plain.empty())return 0;
        }
        size_t count=(std::min)(plain.size()-offset,static_cast<size_t>(n));memcpy(p,plain.data()+offset,count);offset+=count;return static_cast<int>(count);
    }
    int write(const char* p,int n)override{
        if(config.protocol==1)return wire.write(p,n);
        int done=0;while(done<n){int count=(std::min)(n-done,16368);Bytes sealed;if(!bodyCrypt(true,View(reinterpret_cast<const Byte*>(p+done),count),sealed))return -1;Byte length[2];be16(length,static_cast<unsigned>(sealed.size()));if(!wire.send(length)||!wire.send(sealed))return -1;done+=count;}return done;
    }
};
struct TrojanIO : IO {
    IO& wire;
    std::string prefix;
    explicit TrojanIO(IO& lower) : wire(lower) {}
    ~TrojanIO() { OPENSSL_cleanse(prefix.data(), prefix.size()); }
    bool start(std::string_view password, std::string_view host) {
        Byte hash[28];
        if (EVP_Digest(password.data(), password.size(), hash, nullptr, EVP_sha224(), nullptr) != 1) return false;
        constexpr char hex[] = "0123456789abcdef";
        prefix.reserve(65 + host.size());
        for (const auto byte : hash) { prefix.push_back(hex[byte >> 4]); prefix.push_back(hex[byte & 15]); }
        OPENSSL_cleanse(hash, sizeof(hash));
        prefix.append("\r\n\x01\x03", 4);
        prefix.push_back(static_cast<char>(host.size()));
        prefix.append(host);
        prefix.append("\x01\xbb\r\n", 4);
        return true;
    }
    int read(char* data, int size) override { return wire.read(data, size); }
    int write(const char* data, int size) override {
        if (prefix.empty()) return wire.write(data, size);
        prefix.append(data, static_cast<size_t>(size));
        const bool sent = wire.write(prefix.data(), static_cast<int>(prefix.size())) == static_cast<int>(prefix.size());
        OPENSSL_cleanse(prefix.data(), prefix.size());
        prefix.clear();
        return sent ? size : -1;
    }
};
bool textSafe(const char* p,size_t max){size_t n=strlen(p);return n<=max && std::none_of(p,p+n,[](char c){return static_cast<unsigned char>(c)<32||c==127;});}
int run(const char* address,const char* port,const V2RayConfigV1* cfg,const char* host,const char* path,const char* headers,unsigned remaining,HANDLE stop,int* status,char* body,unsigned capacity,unsigned* used,std::string_view password={}){
    *status=0;if(used)*used=0;
    if(!context || !cfg || cfg->size!=sizeof(V2RayConfigV1)||cfg->protocol<1||cfg->protocol>3||cfg->security<0||cfg->security>1||cfg->transport<0||cfg->transport>2||
        (cfg->protocol==3 && (password.empty() || password.size()>12288 || cfg->security!=1)) ||
        (cfg->protocol==2&&cfg->cipher!=3&&cfg->cipher!=4)||*cfg->flow||*cfg->fingerprint||*cfg->realityPublicKey||*cfg->realityShortId||
        !textSafe(cfg->serverName,253)||!textSafe(cfg->hostHeader,cfg->protocol==3?512:253)||!textSafe(cfg->path,4096)||cfg->path[0]!='/'||!textSafe(cfg->alpn,2048)||strlen(cfg->uuid)>64||
        strlen(host)<1||!textSafe(host,255)||!textSafe(path,4096)||path[0]!='/'||strlen(path)+strlen(host)+strlen(headers)>4096 || (body && (capacity<65||*headers)))return 0;
    if(cfg->protocol==1 && cfg->security==0){Byte ip[16];bool local=InetPtonA(AF_INET,address,ip)==1 && ip[0]==127 || InetPtonA(AF_INET6,address,ip)==1 && IN6_IS_ADDR_LOOPBACK(reinterpret_cast<const IN6_ADDR*>(ip));if(!local)return 0;}
    SocketIO socket(remaining,stop);if(!socket.connectTo(address,port))return 0;
    TlsIO outer;IO* wire=&socket;
    if(cfg->security==1){if(!*cfg->serverName || !outer.start(socket,cfg->serverName,cfg->alpn))return 0;wire=&outer;}
    WebIO web(*wire,cfg->transport==1);if(cfg->transport){if(!web.start(cfg->path,*cfg->hostHeader?cfg->hostHeader:cfg->serverName))return 0;wire=&web;}
    ProtocolIO v2ray(*wire,*cfg);
    TrojanIO trojan(*wire);
    const bool isTrojan=cfg->protocol==3;
    if(isTrojan ? !trojan.start(password,host) : !v2ray.start(host))return 0;
    IO& tunnel=isTrojan?static_cast<IO&>(trojan):static_cast<IO&>(v2ray);
    TlsIO inner;if(!inner.start(tunnel,host,"http/1.1"))return 0;
    std::string request="GET "+std::string(path)+" HTTP/1.1\r\nHost: "+host+"\r\nConnection: close\r\n"+headers+"\r\n";
    if(inner.write(request.data(),static_cast<int>(request.size()))!=static_cast<int>(request.size()))return 0;
    if(body)return read_echo_response([&](char* p,int n){return inner.read(p,n);},body,capacity,used,status)?1:0;
    for(int attempt=0;attempt<8;++attempt){
        std::string header;char c;
        while(header.size()<32768 && !header.ends_with("\r\n\r\n")){if(inner.read(&c,1)!=1)return 0;header+=c;}
        if(!header.ends_with("\r\n\r\n")||header.size()<12||!header.starts_with("HTTP/1.")||header[8]!=' ')return 0;
        int code=0;for(int i=9;i<12;++i){if(header[i]<'0'||header[i]>'9')return 0;code=code*10+header[i]-'0';}if(code<100||code>599)return 0;
        if(code==101||code>=200){*status=code;return 1;}
    }
    return 0;
}
}
extern "C" __declspec(dllexport) int ProbeInitialize(const char* ca){
    WSADATA data{};if(WSAStartup(MAKEWORD(2,2),&data))return 0;context=SSL_CTX_new(TLS_client_method());bioMethod=BIO_meth_new(BIO_TYPE_SOURCE_SINK,"native-v2ray-stream");
    if(context && bioMethod && SSL_CTX_load_verify_locations(context,ca,nullptr)==1 &&
        SSL_CTX_set_min_proto_version(context,TLS1_2_VERSION)==1 && BIO_meth_set_read(bioMethod,bioRead) &&
        BIO_meth_set_write(bioMethod,bioWrite) && BIO_meth_set_ctrl(bioMethod,bioCtrl)) {
        SSL_CTX_set_verify(context,SSL_VERIFY_PEER,nullptr);return 1;
    }
    SSL_CTX_free(context);context=nullptr;BIO_meth_free(bioMethod);bioMethod=nullptr;WSACleanup();return 0;
}
extern "C" __declspec(dllexport) void ProbeShutdown(){SSL_CTX_free(context);context=nullptr;BIO_meth_free(bioMethod);bioMethod=nullptr;WSACleanup();}
extern "C" __declspec(dllexport) int ProbeV2RayWebsite(const char* address,const char* port,const V2RayConfigV1* cfg,const char* host,const char* path,const char* headers,unsigned remaining,void* stop,int* status){return run(address,port,cfg,host,path,headers,remaining,stop,status,nullptr,0,nullptr);}
extern "C" __declspec(dllexport) int ProbeV2RayEcho(const char* address,const char* port,const V2RayConfigV1* cfg,const char* host,const char* path,const char* headers,unsigned remaining,void* stop,int* status,char* body,unsigned capacity,unsigned* used){return run(address,port,cfg,host,path,headers,remaining,stop,status,body,capacity,used);}
extern "C" __declspec(dllexport) int ProbeTrojan(const char* address,const char* port,const V2RayConfigV1* cfg,const char* password,
    unsigned passwordSize,const char* host,const char* path,const char* headers,unsigned remaining,void* stop,int* status,char* body,unsigned capacity,unsigned* used){
    return run(address,port,cfg,host,path,headers,remaining,stop,status,body,capacity,used,{password,passwordSize});
}
