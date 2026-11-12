# C++ probe experiment

Build this standalone WinHTTP probe with the workspace's pinned MSVC compiler and import libraries. Its small Windows x64 ABI declaration file allows it to compile with the existing toolchain bundle.

```powershell
pwsh -File ./experiments/cpp-probe/Build.ps1 -ToolchainRoot <toolchain-root> -OutputDirectory <output-directory>
& <output-directory>/CppProbe.exe '127.0.0.1:<verified HTTP proxy port>'
```

The probe runs three rounds of five concurrent HTTPS GET requests through the supplied local HTTP proxy. Each result row gives the site, HTTP status, milliseconds to response headers, and WinHTTP error code. An optional second argument writes the rows to a new TSV file. Startup and idle markers allow the caller to sample private memory before and after each round. HTTP 401 and other nonzero statuses count as received HTTP responses.

The experiment uses the active local route and Windows TLS. The product sweep exercises independent node dialing, a scheduler and a desktop UI. A language comparison requires matching those workloads.

[Microsoft WinHTTP reference](https://learn.microsoft.com/en-us/windows/win32/winhttp/winhttp-functions)
