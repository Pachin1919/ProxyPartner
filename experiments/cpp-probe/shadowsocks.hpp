#pragma once
#include "native-imports.hpp"

using Byte = unsigned char;
using Tick = unsigned __int64;
constexpr int maxChunk = 0x3fff;
constexpr int tagSize = 16;
constexpr int keySize = 32;

struct Profile {
    const char* server;
    const char* port;
    const char* obfsHost;
    Byte key[keySize];
    bool numericAddress = false;
};

struct Site { const char* name; const char* host; const char* path; const char* headers; };
struct Result { int status = 0; Dword elapsed = 0; bool timeout = false; bool connected = false; };

void put16(Byte* target, int value);
BioMethod* createTransportMethod();
Result measureSite(const Profile& profile, const Site& site, Tick deadline,
    SslContext* sslContext, const BioMethod* method, Handle stop = nullptr);
