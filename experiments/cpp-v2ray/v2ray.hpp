#pragma once
// Additive x64 C ABI. All pointers nonnull UTF8; settings retained for the call only.
struct V2RayConfigV1 {
    unsigned size=sizeof(V2RayConfigV1);
    int protocol=1; // 1 VLESS, 2 VMess, 3 Trojan
    int security=1; //0none 1TLS 2REALITY
    int transport=0; //0TCP 1WebSocket 2HTTP Upgrade
    int cipher=3; //VMess body3AES128GCM 4ChaCha20Poly1305; auto resolved by importer
    const char* uuid="";
    const char* serverName="";
    const char* path="/";
    const char* hostHeader="";
    const char* alpn=""; //comma-separated, empty=default
    const char* flow="";
    const char* fingerprint="";
    const char* realityPublicKey="";
    const char* realityShortId="";
};
// ProbeInitialize(ca file) / ProbeShutdown() same lifecycle as existing adapters.
// int ProbeV2RayWebsite(address,port,config,host,path,headers,remaining,stop,status)
// int ProbeV2RayEcho(same arguments,body,capacity,used), body capacity>=65.
// int ProbeTrojan(address,port,config,password,passwordSize,host,path,headers,remaining,stop,status,body,capacity,used)
