#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <winsock2.h>
#include <windows.h>
#include <iphlpapi.h>
#include <tlhelp32.h>
#include <winternl.h>
#include <nlohmann/json.hpp>

#include "host.hpp"
#include <algorithm>
#include <array>
#include <charconv>
#include <cstdio>
#include <filesystem>
#include <memory>
#include <mutex>
#include <chrono>
#include <set>
#include <string_view>
#include <vector>

namespace {
using Json = nlohmann::json;
struct Close { void operator()(void* h) const { CloseHandle(h); } };
using Handle = std::unique_ptr<void, Close>;
Handle valid(HANDLE h) { return Handle(h == INVALID_HANDLE_VALUE ? nullptr : h); }

struct Peer {
    DWORD pid{}, parent{};
    std::wstring image;
    uint64_t started{};
    bool service{};
};
using SidBytes = std::vector<unsigned char>;
SidBytes owner(HANDLE process) {
    HANDLE raw{};
    if (!OpenProcessToken(process, TOKEN_QUERY, &raw)) return {};
    Handle token(raw);
    alignas(TOKEN_USER) std::array<unsigned char, 512> buffer{};
    DWORD count{};
    if (!GetTokenInformation(token.get(), TokenUser, buffer.data(), static_cast<DWORD>(buffer.size()), &count)) return {};
    const auto sid = reinterpret_cast<const TOKEN_USER*>(buffer.data())->User.Sid;
    SidBytes user(GetLengthSid(sid));
    if (!CopySid(static_cast<DWORD>(user.size()), user.data(), sid)) return {};
    return user;
}
Peer identity(DWORD pid, DWORD parent, const SidBytes& user, DWORD session) {
    DWORD actual{};
    if (!ProcessIdToSessionId(pid, &actual) || actual != session) return {};
    Handle process(OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid));
    if (!process || owner(process.get()) != user) return {};
    std::wstring image(4096, L'\0');
    DWORD size = static_cast<DWORD>(image.size());
    FILETIME created{}, exit{}, kernel{}, active{};
    if (!QueryFullProcessImageNameW(process.get(), 0, image.data(), &size) ||
        !GetProcessTimes(process.get(), &created, &exit, &kernel, &active)) return {};
    image.resize(size);
    return {pid, parent, std::move(image), (static_cast<uint64_t>(created.dwHighDateTime) << 32) | created.dwLowDateTime};
}
std::wstring pipe_name(std::wstring_view configured) {
    constexpr std::wstring_view prefix = L"verge-mihomo-production-";
    constexpr std::wstring_view directory = L"\\\\.\\pipe\\";
    if (configured.starts_with(directory)) {
        const auto name = configured.substr(directory.size());
        if (name == L"verge-mihomo" ||
            (name.starts_with(prefix) && name.size() == prefix.size() + 64 &&
             name.substr(prefix.size()).find_first_not_of(L"0123456789abcdef") == std::wstring_view::npos))
            return std::wstring(configured);
    }
    WIN32_FIND_DATAW record{};
    HANDLE search = FindFirstFileW(L"\\\\.\\pipe\\*", &record);
    if (search == INVALID_HANDLE_VALUE) return L"\\\\.\\pipe\\verge-mihomo";
    std::wstring name;
    unsigned count = 0;
    do {
        const std::wstring_view candidate = record.cFileName;
        if (candidate.starts_with(prefix) && candidate.size() == prefix.size() + 64 &&
            candidate.substr(prefix.size()).find_first_not_of(L"0123456789abcdef") == std::wstring_view::npos) {
            name = candidate;
            ++count;
        }
    } while (count < 2 && FindNextFileW(search, &record));
    FindClose(search);
    if (count == 2) return {};
    return count == 1 ? L"\\\\.\\pipe\\" + name : L"\\\\.\\pipe\\verge-mihomo";
}

struct ServiceClose { void operator()(SC_HANDLE value) const { CloseServiceHandle(value); } };
using Service = std::unique_ptr<std::remove_pointer_t<SC_HANDLE>, ServiceClose>;

std::set<DWORD> session_zero() {
    using Query = NTSTATUS (NTAPI*)(SYSTEM_INFORMATION_CLASS, PVOID, ULONG, PULONG);
    const auto query = reinterpret_cast<Query>(GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "NtQuerySystemInformation"));
    if (!query) return {};
    ULONG size{};
    query(SystemProcessInformation, nullptr, 0, &size);
    if (!size || size > 8 * 1024 * 1024) return {};
    std::vector<unsigned char> data(static_cast<size_t>(size) + 65536u);
    if (query(SystemProcessInformation, data.data(), static_cast<ULONG>(data.size()), &size) < 0 || size > data.size()) return {};
    std::set<DWORD> result;
    for (size_t offset = 0; offset + sizeof(SYSTEM_PROCESS_INFORMATION) <= size;) {
        const auto* entry = reinterpret_cast<const SYSTEM_PROCESS_INFORMATION*>(data.data() + offset);
        if (entry->SessionId == 0) result.insert(static_cast<DWORD>(reinterpret_cast<ULONG_PTR>(entry->UniqueProcessId)));
        if (!entry->NextEntryOffset || entry->NextEntryOffset > size - offset) break;
        offset += entry->NextEntryOffset;
    }
    return result;
}

Peer service_core(const Peer& ui) {
    Service manager(OpenSCManagerW(nullptr, nullptr, SC_MANAGER_CONNECT));
    if (!manager) return {};
    Service service(OpenServiceW(manager.get(), L"clash_verge_service", SERVICE_QUERY_STATUS | SERVICE_QUERY_CONFIG));
    if (!service) return {};
    SERVICE_STATUS_PROCESS status{};
    DWORD count{};
    if (!QueryServiceStatusEx(service.get(), SC_STATUS_PROCESS_INFO, reinterpret_cast<BYTE*>(&status), sizeof(status), &count) ||
        status.dwCurrentState != SERVICE_RUNNING || status.dwServiceType != SERVICE_WIN32_OWN_PROCESS || !status.dwProcessId) return {};
    alignas(QUERY_SERVICE_CONFIGW) std::array<unsigned char, 8192> buffer{};
    const auto config = reinterpret_cast<QUERY_SERVICE_CONFIGW*>(buffer.data());
    if (!QueryServiceConfigW(service.get(), config, static_cast<DWORD>(buffer.size()), &count)) return {};
    auto image = std::wstring_view(config->lpBinaryPathName);
    if (image.starts_with(L'"') && image.ends_with(L'"') && image.size() > 1) image = image.substr(1, image.size() - 2);
    const auto expected = std::filesystem::path(ui.image).parent_path() / L"resources" / L"clash-verge-service.exe";
    if (_wcsicmp(std::wstring(image).c_str(), expected.c_str()) || _wcsicmp(config->lpServiceStartName, L"LocalSystem")) return {};
    const auto inServiceSession = session_zero();
    auto snapshot = valid(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
    if (!snapshot) return {};
    PROCESSENTRY32W process{}; process.dwSize = sizeof(process);
    DWORD child = 0;
    bool serviceFound = false;
    for (BOOL more = Process32FirstW(snapshot.get(), &process); more; more = Process32NextW(snapshot.get(), &process)) {
        if (process.th32ProcessID != status.dwProcessId && process.th32ParentProcessID != status.dwProcessId) continue;
        if (!inServiceSession.contains(process.th32ProcessID)) continue;
        if (process.th32ProcessID == status.dwProcessId && !_wcsicmp(process.szExeFile, L"clash-verge-service.exe"))
            serviceFound = true;
        else if (process.th32ParentProcessID == status.dwProcessId && !_wcsicmp(process.szExeFile, L"verge-mihomo.exe")) {
            if (child) return {};
            child = process.th32ProcessID;
        }
    }
    return serviceFound && child ? Peer{child, status.dwProcessId, {}, 0, true} : Peer{};
}

bool transfer(HANDLE pipe, bool writing, char* data, DWORD capacity, DWORD& bytes, uint64_t deadline, HANDLE stop = nullptr) {
    Handle event(CreateEventW(nullptr, TRUE, FALSE, nullptr));
    if (!event) return false;
    OVERLAPPED operation{};
    operation.hEvent = event.get();
    const BOOL complete = writing ? WriteFile(pipe, data, capacity, &bytes, &operation) :
        ReadFile(pipe, data, capacity, &bytes, &operation);
    if (complete) return true;
    const auto error = GetLastError();
    if (error != ERROR_IO_PENDING) return !writing && error == ERROR_BROKEN_PIPE;
    auto now = GetTickCount64();
    HANDLE waits[]{event.get(),stop};
    if (now < deadline && WaitForMultipleObjects(stop ? 2 : 1, waits, FALSE, static_cast<DWORD>(deadline - now)) == WAIT_OBJECT_0)
        return GetOverlappedResult(pipe, &operation, &bytes, FALSE) || (!writing && GetLastError() == ERROR_BROKEN_PIPE);
    CancelIoEx(pipe, &operation);
    GetOverlappedResult(pipe, &operation, &bytes, TRUE);
    return false;
}
std::string dechunk(std::string_view body, size_t limit) {
    std::string output;
    while (!body.empty()) {
        const auto split = body.find("\r\n");
        if (split == body.npos) return {};
        const auto line = body.substr(0, split);
        const auto number = line.substr(0, line.find(';'));
        size_t bytes{};
        const auto [end, result] = std::from_chars(number.data(), number.data() + number.size(), bytes, 16);
        if (result != std::errc{} || end != number.data() + number.size()) return {};
        if (!bytes) return output;
        body.remove_prefix(split + 2);
        if (bytes > limit - output.size() || bytes > body.size() || !body.substr(bytes).starts_with("\r\n")) return {};
        output.append(body.substr(0, bytes));
        body.remove_prefix(bytes + 2);
    }
    return {};
}
Json read_route(const std::wstring& name, const Peer& ui, const Peer& core, const SidBytes& user, DWORD session, bool version, bool connections = false, HANDLE stop = nullptr, uint64_t outerDeadline = UINT64_MAX) {
    auto pipe = valid(CreateFileW(name.c_str(), GENERIC_READ | GENERIC_WRITE, 0, nullptr,
        OPEN_EXISTING, FILE_FLAG_OVERLAPPED | SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION, nullptr));
    if (!pipe) return {};
    ULONG pid{};
    if (!GetNamedPipeServerProcessId(pipe.get(), &pid) || pid != core.pid) return {};
    if (core.service) {
        const auto currentUi = identity(ui.pid, ui.parent, user, session);
        if (currentUi.started != ui.started || currentUi.image != ui.image) return {};
        const auto registered = service_core(currentUi);
        if (core.pid != registered.pid || core.parent != registered.parent) return {};
    } else {
        const auto current = identity(pid, core.parent, user, session);
        if (current.started != core.started || current.image != core.image) return {};
    }
    std::string request = connections ? "GET /connections" : version ? "GET /version" : "GET /proxies";
    request += " HTTP/1.1\r\nHost: 127.0.0.1\r\nConnection: close\r\n\r\n";
    const auto deadline = (std::min)(GetTickCount64() + (connections ? 2000 : 5000), outerDeadline);
    DWORD sent{};
    if (!transfer(pipe.get(), true, request.data(), static_cast<DWORD>(request.size()), sent, deadline, stop) ||
        sent != request.size()) return {};
    const size_t limit = connections ? 2 * 1024 * 1024 : version ? 4096 : 8 * 1024 * 1024;
    std::string response;
    std::array<char, 8192> buffer{};
    for (;;) {
        DWORD count{};
        if (!transfer(pipe.get(), false, buffer.data(), static_cast<DWORD>(buffer.size()), count, deadline, stop)) return {};
        if (!count) break;
        if (response.size() + count > limit + 16384) return {};
        response.append(buffer.data(), count);
    }
    const auto end = response.find("\r\n\r\n");
    if (!response.starts_with("HTTP/1.1 200 ") || end == response.npos || end > 16384) return {};
    auto lower = response.substr(0, end);
    for (auto& c : lower) if (c >= 'A' && c <= 'Z') c += 'a' - 'A';
    if (lower.find("\r\ncontent-encoding:") != lower.npos) return {};
    auto body = std::string_view(response).substr(end + 4);
    if (lower.find("\r\ntransfer-encoding: chunked") != lower.npos) return Json::parse(dechunk(body, limit), nullptr, false);
    if (body.size() > limit) return {};
    return Json::parse(body, nullptr, false);
}
std::string text(const Json& object, const char* field) {
    if (!object.is_object()) return {};
    auto entry = object.find(field);
    return entry != object.end() && entry->is_string() ? entry->get<std::string>() : "";
}
bool valid_time(const std::string& stamp) {
    unsigned year{}, month{}, day{}, hour{}, minute{}, second{};
    if (std::sscanf(stamp.c_str(), "%4u-%2u-%2uT%2u:%2u:%2u", &year, &month, &day, &hour, &minute, &second) != 6 ||
        stamp.size() < 20 || year < 1970 || year > 9999) return false;
    const auto zone = stamp.find_first_of("Z+-", 19);
    if (zone == stamp.npos) return false;
    const SYSTEMTIME sample{static_cast<WORD>(year),static_cast<WORD>(month),0,static_cast<WORD>(day),
        static_cast<WORD>(hour),static_cast<WORD>(minute),static_cast<WORD>(second),0};
    FILETIME value{}, utc{};
    if (!SystemTimeToFileTime(&sample, &value)) return false;
    GetSystemTimeAsFileTime(&utc);
    auto ticks = [](FILETIME t) { return static_cast<int64_t>((static_cast<uint64_t>(t.dwHighDateTime) << 32) | t.dwLowDateTime); };
    int64_t offset = 0;
    if (stamp[zone] == 'Z') { if (zone + 1 != stamp.size()) return false; }
    else {
        unsigned hours{}, minutes{};
        if (std::sscanf(stamp.c_str() + zone + 1, "%2u:%2u", &hours, &minutes) != 2 || hours > 23 || minutes > 59 ||
            zone + 6 != stamp.size()) return false;
        offset = (static_cast<int64_t>(hours) * 60 + minutes) * 600000000 * (stamp[zone] == '+' ? 1 : -1);
    }
    return ticks(value) - offset <= ticks(utc) + 600000000;
}
}

NativeHost read_native_host(std::wstring_view configuredPipe, CurrentHostAccess* access) {
    NativeHost output;
    const auto user = owner(GetCurrentProcess());
    DWORD session{};
    if (user.empty() || !ProcessIdToSessionId(GetCurrentProcessId(), &session)) {
        output.state = "AccessDenied";
        return output;
    }
    auto snapshot = valid(CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0));
    if (!snapshot) return output;
    PROCESSENTRY32W process{};
    process.dwSize = sizeof(process);
    std::vector<Peer> uis, cores;
    for (BOOL more = Process32FirstW(snapshot.get(), &process); more; more = Process32NextW(snapshot.get(), &process)) {
        const bool ui = _wcsicmp(process.szExeFile, L"clash-verge.exe") == 0;
        if (!ui && _wcsicmp(process.szExeFile, L"verge-mihomo.exe") != 0) continue;
        auto peer = identity(process.th32ProcessID, process.th32ParentProcessID, user, session);
        if (peer.pid) (ui ? uis : cores).push_back(std::move(peer));
    }
    if (uis.empty()) return output;
    if (uis.size() != 1 || cores.size() > 1) { output.state = "Ambiguous"; return output; }
    const auto& ui = uis.front();
    const auto core = cores.empty() ? service_core(ui) : cores.front();
    if (!core.pid) return output;
    if (!core.service && (core.parent != ui.pid || core.started < ui.started ||
        _wcsicmp(std::filesystem::path(ui.image).parent_path().c_str(), std::filesystem::path(core.image).parent_path().c_str()))) {
        output.state = "IdentityRejected";
        return output;
    }
    const auto pipe = pipe_name(configuredPipe);
    if (pipe.empty()) { output.state = "Ambiguous"; return output; }
    const auto version = text(read_route(pipe, ui, core, user, session, true), "version");
    if (version.empty() || version.size() > 64 || version.find_first_not_of("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789.+-") != version.npos)
        return output;
    const auto payload = read_route(pipe, ui, core, user, session, false);
    if (!payload.is_object()) return output;
    const auto roster = payload.find("proxies");
    if (roster == payload.end() || !roster->is_object() || roster->size() > 2005) return output;
    if (access) {
        for (auto item = roster->begin(); item != roster->end(); ++item) {
            const auto type = text(item.value(),"type");
            if (!type.empty() && type != "Direct" && type != "Reject" && type != "RejectDrop" && type != "Pass" && type != "Compatible" &&
                !item.value().contains("all")) access->leaves.insert(item.key());
        }
        auto alive = [core,ui,user,session]() {
            if (core.service) { auto current=service_core(ui); return current.pid==core.pid && current.parent==core.parent; }
            auto current=identity(core.pid,core.parent,user,session); return current.started==core.started && current.image==core.image;
        };
        access->verifyTcp = [alive,core](unsigned proxyPort,unsigned localPort) {
            if (!alive()) return false;
            DWORD size{};
            const auto kind = localPort ? TCP_TABLE_OWNER_PID_CONNECTIONS : TCP_TABLE_OWNER_PID_LISTENER;
            if (GetExtendedTcpTable(nullptr,&size,FALSE,AF_INET,kind,0) != ERROR_INSUFFICIENT_BUFFER || size < 4 || size > 4*1024*1024-4096) return false;
            std::vector<unsigned char> bytes(size+4096); size=static_cast<DWORD>(bytes.size());
            if (GetExtendedTcpTable(bytes.data(),&size,FALSE,AF_INET,kind,0) != NO_ERROR) return false;
            const auto* table=reinterpret_cast<const MIB_TCPTABLE_OWNER_PID*>(bytes.data());
            if (table->dwNumEntries > (size-4)/sizeof(MIB_TCPROW_OWNER_PID)) return false;
            unsigned matches=0;
            for (DWORD i=0;i<table->dwNumEntries;++i) {
                const auto& row=table->table[i];
                if (row.dwLocalAddr != htonl(INADDR_LOOPBACK) || ntohs(static_cast<u_short>(row.dwLocalPort)) != proxyPort) continue;
                if (localPort && (row.dwState != MIB_TCP_STATE_ESTAB || row.dwRemoteAddr != htonl(INADDR_LOOPBACK) || ntohs(static_cast<u_short>(row.dwRemotePort)) != localPort)) continue;
                if (row.dwOwningPid != core.pid) return false;
                ++matches;
            }
            return matches==1 && alive();
        };
        const auto leaves=access->leaves;
        auto routeMutex=std::make_shared<std::timed_mutex>();
        access->lookup=[alive,pipe,ui,core,user,session,leaves,routeMutex](const std::string& host,unsigned destination,unsigned source,void* stop,uint64_t deadline) -> std::optional<MatchedCurrentRoute> {
            const auto now=GetTickCount64();
            const auto lookupDeadline=(std::min)(deadline,now+2000);
            if (now>=lookupDeadline || !source) return {};
            if (!routeMutex->try_lock_for(std::chrono::milliseconds(lookupDeadline-now))) return {};
            std::unique_lock<std::timed_mutex> lock(*routeMutex,std::adopt_lock);
            if (GetTickCount64()>=lookupDeadline || (stop && WaitForSingleObject(stop,0)==WAIT_OBJECT_0) || !alive()) return {};
            auto body=read_route(pipe,ui,core,user,session,false,true,stop,lookupDeadline);
            return alive() ? match_current_route(body,host,destination,source,leaves) : std::nullopt;
        };
    }
    for (auto item = roster->begin(); item != roster->end(); ++item) {
        const auto& entry = item.value();
        if (!entry.is_object()) continue;
        if (item.key() == "GLOBAL") output.current = text(entry, "now");
        const auto history = entry.find("history");
        if (history == entry.end() || !history->is_array() || history->empty()) continue;
        const auto& sample = history->back();
        if (!sample.is_object()) continue;
        const auto delay = sample.find("delay");
        auto observed = text(sample, "time");
        if (delay == sample.end() || !delay->is_number_integer() || !valid_time(observed)) continue;
        const auto value = delay->get<int64_t>();
        if (value <= 0 || value >= 65535) continue;
        const auto type = text(entry, "type");
        if (!type.empty()) output.samples.emplace(std::make_pair(item.key(), type), NativeSample{static_cast<int>(value),std::move(observed)});
    }
    output.version = version;
    output.state = "ConnectedPipe";
    return output;
}
