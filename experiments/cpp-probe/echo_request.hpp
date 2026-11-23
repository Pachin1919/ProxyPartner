#pragma once
#include "echo_http.hpp"
static int request_echo(Ssl* ssl,const char* host,const char* path,char* body,unsigned capacity,unsigned* used,int* status) {
    *used=0; *status=0;
    if(capacity<65 || strlen(host)+strlen(path)>350) return 0;
    char message[512]; unsigned count=0;
    const char* pieces[]={"GET ",path," HTTP/1.1\r\nHost: ",host,"\r\nConnection: close\r\n\r\n"};
    for(const auto* piece:pieces) { unsigned n=static_cast<unsigned>(strlen(piece));memcpy(message+count,piece,n);count+=n; }
    if(SSL_write(ssl,message,static_cast<int>(count))!=static_cast<int>(count)) return 0;
    return read_echo_response([&](char* out,int n){return SSL_read(ssl,out,n);},body,capacity,used,status)?1:0;
}
