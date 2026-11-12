#pragma once

// The pinned MSVC toolchain ships import libraries without SDK headers.
// These are the Windows x64 ABI declarations used by this experiment.
using Handle = void*;
using Dword = unsigned long;
using Bool = int;
using Port = unsigned short;
using WordPtr = unsigned __int64;

extern "C" {
__declspec(dllimport) Handle __stdcall WinHttpOpen(const wchar_t*, Dword, const wchar_t*, const wchar_t*, Dword);
__declspec(dllimport) Handle __stdcall WinHttpConnect(Handle, const wchar_t*, Port, Dword);
__declspec(dllimport) Handle __stdcall WinHttpOpenRequest(Handle, const wchar_t*, const wchar_t*, const wchar_t*,
    const wchar_t*, const wchar_t**, Dword);
__declspec(dllimport) Bool __stdcall WinHttpSetTimeouts(Handle, int, int, int, int);
__declspec(dllimport) Bool __stdcall WinHttpSetOption(Handle, Dword, void*, Dword);
__declspec(dllimport) Bool __stdcall WinHttpSendRequest(Handle, const wchar_t*, Dword, void*, Dword, Dword, WordPtr);
__declspec(dllimport) Bool __stdcall WinHttpReceiveResponse(Handle, void*);
__declspec(dllimport) Bool __stdcall WinHttpQueryHeaders(Handle, Dword, const wchar_t*, void*, Dword*, Dword*);
__declspec(dllimport) Bool __stdcall WinHttpCloseHandle(Handle);
__declspec(dllimport) Handle __stdcall CreateThread(void*, WordPtr, Dword (__stdcall*)(void*), void*, Dword, Dword*);
__declspec(dllimport) Dword __stdcall WaitForSingleObject(Handle, Dword);
__declspec(dllimport) Bool __stdcall CloseHandle(Handle);
__declspec(dllimport) unsigned __int64 __stdcall GetTickCount64();
__declspec(dllimport) Dword __stdcall GetLastError();
__declspec(dllimport) void __stdcall Sleep(Dword);
__declspec(dllimport) Handle __stdcall GetStdHandle(Dword);
__declspec(dllimport) Handle __stdcall CreateFileW(const wchar_t*, Dword, Dword, void*, Dword, Dword, Handle);
__declspec(dllimport) Bool __stdcall WriteFile(Handle, const void*, Dword, Dword*, void*);
}
