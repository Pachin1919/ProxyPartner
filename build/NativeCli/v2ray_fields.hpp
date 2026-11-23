#pragma once
#include <winsock2.h>
#include <ws2tcpip.h>
#include <windows.h>
#include <openssl/evp.h>
#include <openssl/crypto.h>
#include <array>
#include <charconv>
#include <string>
#include <algorithm>
#include "../../experiments/cpp-v2ray/v2ray.hpp"
namespace v2ray_fields {
struct Fields {
    std::string server,port,uuid,network="tcp",security="none",sni,path="/",host,alpn,cipher="auto",flow,fingerprint,publicKey,shortId;
    bool vmess=false;
    V2RayConfigV1 native() const {
        V2RayConfigV1 out;out.protocol=vmess?2:1;out.security=security=="tls"?1:security=="reality"?2:0;
        out.transport=network=="ws"?1:network=="httpupgrade"?2:0;out.cipher=cipher=="chacha20-poly1305"?4:3;
        out.uuid=uuid.c_str();out.serverName=sni.c_str();out.path=path.c_str();out.hostHeader=host.c_str();out.alpn=alpn.c_str();out.flow=flow.c_str();out.fingerprint=fingerprint.c_str();out.realityPublicKey=publicKey.c_str();out.realityShortId=shortId.c_str();return out;
    }
};
inline bool uuid(std::string& value,bool aliases){
    if(!aliases) {
        auto first=value.find_first_not_of(" \t\r\n"),last=value.find_last_not_of(" \t\r\n");if(first==value.npos)return false;value=value.substr(first,last-first+1);
        if(value.size()==38 && ((value.front()=='{' && value.back()=='}') || (value.front()=='(' && value.back()==')')))value=value.substr(1,36);
    }
    std::string hex;for(char c:value)if(c!='-')hex+=c;
    if((value.size()==32 || value.size()==36) && hex.size()==32 && std::all_of(hex.begin(),hex.end(),[](char c){return c>='0'&&c<='9'||c>='a'&&c<='f'||c>='A'&&c<='F';})){
        if(value.size()==36 && (value[8]!='-'||value[13]!='-'||value[18]!='-'||value[23]!='-'))return false;
        std::transform(hex.begin(),hex.end(),hex.begin(),[](char c){return c>='A'&&c<='F'?static_cast<char>(c+'a'-'A'):c;});
    } else {
        if(!aliases||value.empty()||value.size()>120)return false;
        int units=MultiByteToWideChar(CP_UTF8,MB_ERR_INVALID_CHARS,value.data(),static_cast<int>(value.size()),nullptr,0);if(units<1||units>30)return false;
        std::string input(16,0);input+=value;unsigned char hashed[20];unsigned count=0;
        bool ok=EVP_Digest(input.data(),input.size(),hashed,&count,EVP_sha1(),nullptr)==1&&count==20;OPENSSL_cleanse(input.data(),input.size());if(!ok)return false;
        hex.clear();
        hashed[6]=(hashed[6]&15)|0x50;hashed[8]=(hashed[8]&63)|0x80;constexpr char digits[]="0123456789abcdef";
        for(unsigned i=0;i<16;++i){hex+=digits[hashed[i]>>4];hex+=digits[hashed[i]&15];}OPENSSL_cleanse(hashed,sizeof(hashed));
    }
    OPENSSL_cleanse(value.data(),value.size());value=hex.substr(0,8)+"-"+hex.substr(8,4)+"-"+hex.substr(12,4)+"-"+hex.substr(16,4)+"-"+hex.substr(20);return true;
}
inline bool clean(const std::string& text,size_t max,bool spaces=false){return text.size()<=max && text.find('\0')==text.npos && std::none_of(text.begin(),text.end(),[=](unsigned char c){return c<32 || c==127 || (!spaces && c==' ');});}
inline bool validate(Fields& node,bool aliases=false){
    unsigned port=0;auto [end,error]=std::from_chars(node.port.data(),node.port.data()+node.port.size(),port);
    if(error!=std::errc{}||end!=node.port.data()+node.port.size()||!port||port>65535||node.server.empty()||!clean(node.server,253)||!uuid(node.uuid,aliases))return false;
    for(auto text:{&node.network,&node.security,&node.cipher})std::transform(text->begin(),text->end(),text->begin(),[](char c){return c>='A'&&c<='Z'?static_cast<char>(c+'a'-'A'):c;});
    if(node.network=="raw"||node.network.empty())node.network="tcp";if(node.network=="websocket")node.network="ws";
    if(node.network!="tcp"&&node.network!="ws"&&node.network!="httpupgrade")return false;
    if(node.security!="none"&&node.security!="tls")return false;
    if(!node.flow.empty()||!node.fingerprint.empty()||!node.publicKey.empty()||!node.shortId.empty())return false;
    if(node.path.empty())node.path="/";
    if(node.path.front()!='/'||!clean(node.path,4096)||!clean(node.host,253)||!clean(node.sni,253)||!clean(node.alpn,2048))return false;
    if(node.sni.empty())node.sni=node.host.empty()?node.server:node.host;
    if(node.host.empty())node.host=node.server;
    if(!node.vmess && node.security=="none"){
        unsigned char ip[16];if(!(InetPtonA(AF_INET,node.server.c_str(),ip)==1&&ip[0]==127 || InetPtonA(AF_INET6,node.server.c_str(),ip)==1&&IN6_IS_ADDR_LOOPBACK(reinterpret_cast<const IN6_ADDR*>(ip))))return false;
    }
    std::string_view protocols=node.alpn;while(!protocols.empty()){auto comma=protocols.find(',');auto token=protocols.substr(0,comma);if(token.empty()||token.size()>255)return false;if(comma==protocols.npos)break;protocols.remove_prefix(comma+1);if(protocols.empty())return false;}
    if(node.vmess && node.cipher!="auto"&&node.cipher!=""&&node.cipher!="aes-128-gcm"&&node.cipher!="chacha20-poly1305")return false;
    return true;
}
}
