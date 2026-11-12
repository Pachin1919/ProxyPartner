#include "winhttp-imports.hpp"

namespace {
constexpr Dword namedProxy = 3;
constexpr Dword secure = 0x00800000;
constexpr Dword disableFeature = 63;
constexpr Dword numericStatus = 19 | 0x20000000;
constexpr Dword waitForever = 0xffffffff;
constexpr int rounds = 3;
constexpr Dword idleMs = 15000;
Handle output;

struct InternetHandle {
    Handle value;
    explicit InternetHandle(Handle handle) : value(handle) {}
    InternetHandle(const InternetHandle&) = delete;
    ~InternetHandle() { if (value) WinHttpCloseHandle(value); }
};

struct Site {
    const char* id;
    const wchar_t* host;
    const wchar_t* path;
    const wchar_t* header = nullptr;
    Dword headerLength = 0;
};

constexpr wchar_t anthropicHeader[] = L"anthropic-version: 2023-06-01\r\n";
constexpr Site sites[] = {
    {"GO", L"www.google.com", L"/"},
    {"YT", L"www.youtube.com", L"/"},
    {"OPENAI", L"api.openai.com", L"/v1/models"},
    {"GPT", L"chatgpt.com", L"/"},
    {"ANT", L"api.anthropic.com", L"/v1/models", anthropicHeader, sizeof(anthropicHeader) / sizeof(wchar_t) - 1}
};
constexpr int siteCount = sizeof(sites) / sizeof(sites[0]);

struct Probe {
    Handle session;
    const Site* site;
    Dword status = 0;
    Dword error = 0;
    Dword milliseconds = 0;
};

Dword __stdcall measure(void* parameter) {
    auto& probe = *static_cast<Probe*>(parameter);
    const auto start = GetTickCount64();
    InternetHandle connection(WinHttpConnect(probe.session, probe.site->host, 443, 0));
    InternetHandle request(connection.value ? WinHttpOpenRequest(connection.value,
        L"GET", probe.site->path, nullptr, nullptr, nullptr, secure) : nullptr);
    Dword disabled = 1 | 2 | 4; // cookies, redirects, automatic authentication
    Dword size = sizeof(probe.status);
    if (!request.value ||
        !WinHttpSetOption(request.value, disableFeature, &disabled, sizeof(disabled)) ||
        !WinHttpSendRequest(request.value, probe.site->header, probe.site->headerLength, nullptr, 0, 0, 0) ||
        !WinHttpReceiveResponse(request.value, nullptr) ||
        !WinHttpQueryHeaders(request.value, numericStatus, nullptr, &probe.status, &size, nullptr))
        probe.error = GetLastError();
    probe.milliseconds = static_cast<Dword>(GetTickCount64() - start);
    return 0;
}

void report(const char* label, Dword first = 0, Dword second = 0, Dword third = 0) {
    char line[64];
    char* end = line;
    for (; *label; ++label) *end++ = *label;
    const Dword values[] = {first, second, third};
    for (Dword value : values) {
        *end++ = '\t';
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

bool loopbackProxy(const wchar_t* value) {
    constexpr wchar_t prefix[] = L"127.0.0.1:";
    for (int index = 0; prefix[index]; ++index)
        if (*value++ != prefix[index]) return false;
    unsigned port = 0;
    while (*value >= L'0' && *value <= L'9') {
        port = port * 10 + (*value++ - L'0');
        if (port > 65535) return false;
    }
    return *value == 0 && port != 0;
}

bool runRound(const wchar_t* proxy, Dword round) {
    InternetHandle session(WinHttpOpen(L"ProxyPartner Probe Experiment", namedProxy, proxy, nullptr, 0));
    if (!session.value || !WinHttpSetTimeouts(session.value, 7000, 7000, 7000, 7000)) {
        report("session_error", GetLastError());
        return false;
    }

    Probe probes[siteCount]{};
    Handle workers[siteCount]{};
    report("sweep", round);
    for (int index = 0; index < siteCount; ++index) {
        probes[index] = {session.value, &sites[index]};
        workers[index] = CreateThread(nullptr, 0, measure, &probes[index], 0, nullptr);
        if (!workers[index]) probes[index].error = GetLastError();
    }
    bool complete = true;
    for (int index = 0; index < siteCount; ++index) {
        if (workers[index]) {
            WaitForSingleObject(workers[index], waitForever);
            CloseHandle(workers[index]);
        }
        const auto& result = probes[index];
        report(result.site->id, result.status, result.milliseconds, result.error);
        complete &= result.status != 0;
    }
    return complete;
}
}

int wmain(int argc, wchar_t** argv) {
    output = GetStdHandle(static_cast<Dword>(-11));
    if (argc < 2 || argc > 3 || !loopbackProxy(argv[1])) {
        report("usage: CppProbe.exe 127.0.0.1:<port> [output.tsv]");
        return 2;
    }
    if (argc == 3) {
        output = CreateFileW(argv[2], 0x40000000, 1, nullptr, 1, 0x80, nullptr);
        if (output == reinterpret_cast<Handle>(-1)) return 2;
    }
    report("startup");
    Sleep(2000);
    bool complete = true;
    for (Dword round = 1; round <= rounds; ++round) {
        complete &= runRound(argv[1], round);
        report("idle", round);
        Sleep(idleMs);
    }
    if (argc == 3) CloseHandle(output);
    return complete ? 0 : 1;
}
