#include <winsock2.h>
#include <windows.h>
#include <winhttp.h>
#include <openssl/ssl.h>
#include <future>
#include <iostream>
#include <array>
#include "host.hpp"
#include "manual_proxy.hpp"

namespace {
using Json=nlohmann::json;
HANDLE stopEvent=nullptr;
BOOL WINAPI stop_probe(DWORD code){if(code==CTRL_C_EVENT||code==CTRL_BREAK_EVENT){SetEvent(stopEvent);return TRUE;}return FALSE;}
struct Wire {
    SOCKET socket=INVALID_SOCKET;
    ULONGLONG deadline=GetTickCount64()+7000;
    ~Wire(){if(socket!=INVALID_SOCKET) closesocket(socket);}
    bool ready(bool writing){
        while(GetTickCount64()<deadline && WaitForSingleObject(stopEvent,0)!=WAIT_OBJECT_0){fd_set set;FD_ZERO(&set);FD_SET(socket,&set);timeval t{0,100000};
            int n=select(0,writing?nullptr:&set,writing?&set:nullptr,nullptr,&t);if(n>0)return true;if(n<0)return false;}
        return false;
    }
    int read(char* p,int n){if(!ready(false))return -1;return recv(socket,p,n,0);}
    int write(const char* p,int n){int done=0;while(done<n){if(!ready(true))return -1;int k=send(socket,p+done,n-done,0);if(k<=0)return -1;done+=k;}return done;}
};
int bio_read(BIO* b,char* p,int n){return static_cast<Wire*>(BIO_get_data(b))->read(p,n);}
int bio_write(BIO* b,const char* p,int n){return static_cast<Wire*>(BIO_get_data(b))->write(p,n);}
long bio_ctrl(BIO*,int op,long,void*){return op==BIO_CTRL_FLUSH?1:0;}
struct Target{const char* id;const char* host;const char* path;};
constexpr Target targets[]={{"chatgpt_web","chatgpt.com","/"},{"openai_api","api.openai.com","/v1/models"},{"google","www.google.com","/"},{"youtube","www.youtube.com","/"},{"anthropic_api","api.anthropic.com","/v1/models"}};
Json probe(const Target& t,unsigned port,CurrentHostAccess& access,SSL_CTX* ctx,BIO_METHOD* method,bool bypassed){
    Wire wire;auto start=GetTickCount64();unsigned source=0;
    Json result={{"event","current_path_result"},{"target",t.id},{"attempted",false},{"httpStatus",nullptr},{"elapsedMs",nullptr},{"timedOut",false},{"routeStatus","unknown"},{"node",nullptr},{"direct",false},{"sourcePort",nullptr},{"destinationHost",t.host},{"destinationPort",443}};
    if(bypassed){result["routeStatus"]="bypassed";result["reason"]="system_proxy_bypass";return result;}
    if(!access.verifyTcp(port,0)){result["routeStatus"]="rejected";return result;}
    wire.socket=socket(AF_INET,SOCK_STREAM,IPPROTO_TCP);if(wire.socket==INVALID_SOCKET)return result;
    u_long nonblock=1;if(ioctlsocket(wire.socket,FIONBIO,&nonblock))return result;
    sockaddr_in endpoint{};endpoint.sin_family=AF_INET;endpoint.sin_addr.s_addr=htonl(INADDR_LOOPBACK);endpoint.sin_port=htons(static_cast<u_short>(port));
    int connected=connect(wire.socket,reinterpret_cast<sockaddr*>(&endpoint),sizeof(endpoint));
    if(connected && WSAGetLastError()!=WSAEWOULDBLOCK)return result;
    if(connected && !wire.ready(true))return result;
    int error=0,length=sizeof(error);if(getsockopt(wire.socket,SOL_SOCKET,SO_ERROR,reinterpret_cast<char*>(&error),&length)||error)return result;
    sockaddr_in local{};length=sizeof(local);if(getsockname(wire.socket,reinterpret_cast<sockaddr*>(&local),&length))return result;source=ntohs(local.sin_port);
    if(!access.verifyTcp(port,source)){result["routeStatus"]="rejected";return result;}
    result["attempted"]=true;result["sourcePort"]=source;
    std::string connectRequest="CONNECT "+std::string(t.host)+":443 HTTP/1.1\r\nHost: "+t.host+":443\r\n\r\n";
    if(wire.write(connectRequest.data(),static_cast<int>(connectRequest.size()))<0)return result;
    std::string header;char c;
    while(header.size()<16384 && !header.ends_with("\r\n\r\n")){if(wire.read(&c,1)!=1)return result;header+=c;}
    if(!header.starts_with("HTTP/1.1 200 ") && !header.starts_with("HTTP/1.0 200 "))return result;
    std::unique_ptr<SSL,decltype(&SSL_free)> ssl(SSL_new(ctx),SSL_free);
    BIO* bio=BIO_new(method);if(!ssl||!bio){BIO_free(bio);return result;}
    BIO_set_data(bio,&wire);BIO_set_init(bio,1);SSL_set_bio(ssl.get(),bio,bio);
    if(SSL_set_tlsext_host_name(ssl.get(),t.host)!=1 || SSL_set1_host(ssl.get(),t.host)!=1 || SSL_connect(ssl.get())!=1)return result;
    auto route=std::async(std::launch::async,[&]{return access.lookup(t.host,443,source,stopEvent,wire.deadline);});
    std::string request="GET "+std::string(t.path)+" HTTP/1.1\r\nHost: "+t.host+"\r\nConnection: close\r\n";
    if(std::string_view(t.id)=="anthropic_api")request+="anthropic-version: 2026-10-21\r\n";
    request+="\r\n";header.clear();
    if(SSL_write(ssl.get(),request.data(),static_cast<int>(request.size()))>0){
        while(header.size()<32768 && !header.ends_with("\r\n\r\n")){if(SSL_read(ssl.get(),&c,1)!=1)break;header+=c;}
        if(header.starts_with("HTTP/1.1 ")||header.starts_with("HTTP/1.0 ")){
            if(header.size()>12 && header[9]>='1' && header[9]<='5' && isdigit(static_cast<unsigned char>(header[10])) && isdigit(static_cast<unsigned char>(header[11]))){result["httpStatus"]=(header[9]-'0')*100+(header[10]-'0')*10+header[11]-'0';result["elapsedMs"]=GetTickCount64()-start;}
        }
    }
    auto matched=route.get();if(matched){result["routeStatus"]="verified";result["node"]=matched->direct?Json(nullptr):Json(matched->node);result["direct"]=matched->direct;}
    result["timedOut"]=GetTickCount64()>=wire.deadline;return result;
}
}
int run_current_path(int argc,wchar_t** argv){
    unsigned port=0;std::wstring pipe;
    for(int i=2;i<argc;++i){std::wstring_view key=argv[i];if(i+1>=argc)return 2;
        if(key==L"--proxy-port"){wchar_t* end=nullptr;auto n=wcstoul(argv[++i],&end,10);if(*end||n==0||n>65535)return 2;port=static_cast<unsigned>(n);}
        else if(key==L"--pipe")pipe=argv[++i];else return 2;}
    ManualProxyPolicy policy;
    const bool systemProxy = !port;
    if (systemProxy) {
        WINHTTP_CURRENT_USER_IE_PROXY_CONFIG config{};
        if (WinHttpGetIEProxyConfigForCurrentUser(&config)) {
            if (!config.fAutoDetect && (!config.lpszAutoConfigUrl || !*config.lpszAutoConfigUrl)) {
                auto parsed=parse_manual_proxy(config.lpszProxy ? config.lpszProxy : L"",
                    config.lpszProxyBypass ? config.lpszProxyBypass : L"");
                if (parsed) { policy=std::move(*parsed); port=policy.port; }
            }
            GlobalFree(config.lpszProxy);
            GlobalFree(config.lpszProxyBypass);
            GlobalFree(config.lpszAutoConfigUrl);
        }
    }
    if(!port){std::cout<<Json({{"event","current_path_error"},{"reason","explicit_verified_proxy_port_required"}}).dump()<<std::endl;return 1;}
    stopEvent=CreateEventW(nullptr,TRUE,FALSE,nullptr); if(!stopEvent)return 1; SetConsoleCtrlHandler(stop_probe,TRUE);
    WSADATA data{};if(WSAStartup(MAKEWORD(2,2),&data))return 1;
    CurrentHostAccess access;auto host=read_native_host(pipe,&access);
    if(!access.verifyTcp||!access.lookup){std::cout<<Json({{"event","current_path_error"},{"reason","verified_host_unavailable"}}).dump()<<std::endl;WSACleanup();return 1;}
    wchar_t exe[32768];DWORD n=GetModuleFileNameW(nullptr,exe,32768);std::wstring path(exe,n);path.resize(path.find_last_of(L"\\/")+1);path+=L"probe-ca-bundle.crt";
    FILE* file=_wfopen(path.c_str(),L"rb");
    std::unique_ptr<SSL_CTX,decltype(&SSL_CTX_free)> ctx(SSL_CTX_new(TLS_client_method()),SSL_CTX_free);
    bool ca=false;if(file&&ctx){std::string pem; char buffer[8192]; size_t count; while((count=fread(buffer,1,sizeof(buffer),file))) { pem.append(buffer,count); if(pem.size()>2*1024*1024)break; } BIO* input=BIO_new_mem_buf(pem.data(),static_cast<int>(pem.size()));X509* cert;while((cert=PEM_read_bio_X509(input,nullptr,nullptr,nullptr))){if(X509_STORE_add_cert(SSL_CTX_get_cert_store(ctx.get()),cert)==1)ca=true;X509_free(cert);}BIO_free(input);}if(file)fclose(file);
    if(!ca){std::cout<<Json({{"event","current_path_error"},{"reason","ca_bundle_unavailable"}}).dump()<<std::endl;WSACleanup();return 1;}
    SSL_CTX_set_verify(ctx.get(),SSL_VERIFY_PEER,nullptr);SSL_CTX_set_min_proto_version(ctx.get(),TLS1_2_VERSION);
    BIO_METHOD* method=BIO_meth_new(BIO_TYPE_SOURCE_SINK,"owned-verified-socket");BIO_meth_set_read(method,bio_read);BIO_meth_set_write(method,bio_write);BIO_meth_set_ctrl(method,bio_ctrl);
    std::cout<<Json({{"event","current_path_begin"},{"targets",5},{"method","owned-http-proxy-verified-socket"},{"timeoutMs",7000},{"proxySource",systemProxy?"system_manual":"explicit_port"}}).dump()<<std::endl;
    std::array<std::future<Json>,5> jobs;for(size_t i=0;i<5;++i)jobs[i]=std::async(std::launch::async,[&,i]{auto started=GetTickCount64(); std::string_view host=targets[i].host; bool bypassed=systemProxy && policy.bypasses(std::wstring(host.begin(),host.end())); auto row=probe(targets[i],port,access,ctx.get(),method,bypassed); row["timedOut"]=GetTickCount64()-started>=7000; return row;});
    unsigned verified=0,attempted=0;Json anchor=nullptr;
    for(auto& job:jobs){auto row=job.get();attempted+=row["attempted"].get<bool>();if(row["routeStatus"]=="verified"){++verified;if(anchor.is_null())anchor=row;}std::cout<<row.dump()<<std::endl;}
    Json summary={{"event","current_path_summary"},{"targets",5},{"attempted",attempted},{"verified",verified},{"anchorTarget",nullptr},{"node",nullptr},{"direct",false}};
    if(!anchor.is_null()){summary["anchorTarget"]=anchor["target"];summary["node"]=anchor["node"];summary["direct"]=anchor["direct"];}
    std::cout<<summary.dump()<<std::endl;BIO_meth_free(method);ctx.reset();WSACleanup();SetConsoleCtrlHandler(stop_probe,FALSE);CloseHandle(stopEvent);stopEvent=nullptr;return verified?0:1;
}
