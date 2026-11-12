#include "shadowsocks.hpp"

extern "C" long _InterlockedIncrement(volatile long*);
extern "C" unsigned long wcstoul(const wchar_t*, wchar_t**, int);
#pragma intrinsic(_InterlockedIncrement)
extern "C" __declspec(dllimport) int __stdcall WideCharToMultiByte(Dword, Dword,
    const wchar_t*, int, char*, int, const char*, Bool*);

namespace {
constexpr int maxNodes = 128;
constexpr int maxWorkers = 32;
constexpr int siteCount = 5;
constexpr Site sites[siteCount] = {
    {"GPT", "chatgpt.com", "/", ""},
    {"OPENAI", "api.openai.com", "/v1/models", ""},
    {"GO", "www.google.com", "/", ""},
    {"YT", "www.youtube.com", "/", ""},
    {"ANT", "api.anthropic.com", "/v1/models", "anthropic-version: 2026-10-21\r\n"}
};

Profile profiles[maxNodes];
Result results[maxNodes][siteCount];
char input[256 * 1024];
int count = 0;
volatile long nextNode;
Handle output;
SslContext* context;
BioMethod* method;

void report(const char* label, const Dword* values, int valueCount) {
    char line[96];
    char* end = line;
    while (*label) *end++ = *label++;
    for (int index = 0; index < valueCount; ++index) {
        *end++ = '\t';
        Dword value = values[index];
        char reversed[10];
        char* digit = reversed;
        do {
            *digit++ = static_cast<char>('0' + value % 10);
            value /= 10;
        } while (value);
        while (digit != reversed) *end++ = *--digit;
    }
    *end++ = '\n';
    Dword written;
    WriteFile(output, line, static_cast<Dword>(end - line), &written, nullptr);
}

bool readProfiles(const wchar_t* pipeName) {
    Handle source = CreateFileW(pipeName, 0x80000000, 0, nullptr, 3, 0x80, nullptr);
    if (source == reinterpret_cast<Handle>(-1)) return false;
    Dword size = 0, read = 0;
    while (size < sizeof(input) - 1 && ReadFile(source, input + size, sizeof(input) - size - 1, &read, nullptr) && read)
        size += read;
    CloseHandle(source);
    if (size == sizeof(input) - 1) return false;
    char* cursor = input;
    while (*cursor && count < maxNodes) {
        char* fields[4];
        for (int field = 0; field < 4; ++field) {
            fields[field] = cursor;
            const char separator = field == 3 ? '\n' : '\t';
            while (*cursor && *cursor != separator) ++cursor;
            if (!*cursor || cursor == fields[field]) return false;
            *cursor++ = 0;
        }
        const int passwordLength = static_cast<int>(strlen(fields[2]));
        if (strlen(fields[3]) > 253 || passwordLength > 4096) return false;
        auto& profile = profiles[count++];
        profile.server = fields[0];
        profile.port = fields[1];
        profile.obfsHost = fields[3];
        const int derived = EVP_BytesToKey(EVP_chacha20_poly1305(), EVP_md5(), nullptr,
            reinterpret_cast<const Byte*>(fields[2]), passwordLength, 1, profile.key, nullptr);
        OPENSSL_cleanse(fields[2], passwordLength);
        if (derived != keySize) return false;
    }
    return count && !*cursor;
}

Dword __stdcall worker(void*) {
    for (int node = _InterlockedIncrement(&nextNode) - 1; node < count; node = _InterlockedIncrement(&nextNode) - 1) {
        const Tick deadline = GetTickCount64() + 7000;
        for (int site = 0; site < siteCount; ++site)
            results[node][site] = measureSite(profiles[node], sites[site], deadline, context, method);
    }
    return 0;
}

bool runRound(Dword round, int concurrency, Dword idleMs) {
    nextNode = 0;
    report("sweep", &round, 1);
    Handle workers[maxWorkers]{};
    bool started = true;
    for (int index = 0; index < concurrency; ++index) {
        auto& thread = workers[index];
        thread = CreateThread(nullptr, 0, worker, nullptr, 0, nullptr);
        if (!thread) started = false;
    }
    for (auto thread : workers) {
        if (!thread) continue;
        WaitForSingleObject(thread, 0xffffffff);
        CloseHandle(thread);
    }
    if (!started) return false;
    for (int node = 0; node < count; ++node) {
        for (int site = 0; site < siteCount; ++site) {
            const auto& result = results[node][site];
            const Dword values[] = {round, static_cast<Dword>(node), static_cast<Dword>(result.status),
                result.elapsed, result.timeout ? 1ul : 0ul};
            report(sites[site].name, values, 5);
        }
    }
    report("idle", &round, 1);
    Sleep(idleMs);
    return true;
}

Dword positiveArgument(const wchar_t* argument, Dword maximum) {
    if (*argument < L'0' || *argument > L'9') return 0;
    wchar_t* end;
    const auto value = wcstoul(argument, &end, 10);
    return *end == 0 && value <= maximum ? value : 0;
}
}

int wmain(int argc, wchar_t** argv) {
    if (argc != 4 && argc != 7) return 2;
    const auto concurrency = argc == 7 ? positiveArgument(argv[4], maxWorkers) : 8;
    const auto rounds = argc == 7 ? positiveArgument(argv[5], 9) : 3;
    const auto idleSeconds = argc == 7 ? positiveArgument(argv[6], 60) : 30;
    if (!concurrency || !rounds || !idleSeconds) return 2;
    output = CreateFileW(argv[2], 0x40000000, 1, nullptr, 1, 0x80, nullptr);
    if (output == reinterpret_cast<Handle>(-1)) return 2;
    char certificateFile[512];
    alignas(8) Byte winsock[512];
    context = SSL_CTX_new(TLS_client_method());
    method = createTransportMethod();
    bool ok = context && method && WideCharToMultiByte(65001, 0, argv[3], -1, certificateFile, sizeof(certificateFile), nullptr, nullptr)
        && SSL_CTX_load_verify_locations(context, certificateFile, nullptr) == 1;
    if (ok && WSAStartup(0x0202, winsock) == 0) {
        SSL_CTX_set_verify(context, 1, nullptr);
        ok = readProfiles(argv[1]);
        for (Dword round = 1; ok && round <= rounds; ++round)
            ok = runRound(round, static_cast<int>(concurrency), idleSeconds * 1000);
        WSACleanup();
    } else ok = false;
    OPENSSL_cleanse(profiles, sizeof(profiles));
    OPENSSL_cleanse(input, sizeof(input));
    if (method) BIO_meth_free(method);
    if (context) SSL_CTX_free(context);
    CloseHandle(output);
    return ok ? 0 : 1;
}
