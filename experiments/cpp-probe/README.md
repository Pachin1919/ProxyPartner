# C++ probe experiment

Build this standalone WinHTTP probe with the workspace's pinned MSVC compiler and import libraries. Its small Windows x64 ABI declaration file allows it to compile with the existing toolchain bundle.

```powershell
pwsh -File ./experiments/cpp-probe/Build.ps1 -ToolchainRoot <toolchain-root> -OutputDirectory <output-directory>
& <output-directory>/CppProbe.exe '127.0.0.1:<verified HTTP proxy port>'
```

The probe runs three rounds of five concurrent HTTPS GET requests through the supplied local HTTP proxy. Each result row gives the site, HTTP status, milliseconds to response headers, and WinHTTP error code. An optional second argument writes the rows to a new TSV file. Startup and idle markers allow the caller to sample private memory before and after each round. HTTP 401 and other nonzero statuses count as received HTTP responses.

The experiment uses the active local route and Windows TLS. The product sweep exercises independent node dialing, a scheduler and a desktop UI. A language comparison requires matching those workloads.

[Microsoft WinHTTP reference](https://learn.microsoft.com/en-us/windows/win32/winhttp/winhttp-functions)

## Paired independent-node sweep

`Build-Sweep.ps1` builds the C++ client with MSVC and the installed OpenSSL 3 libraries. The C# client in `csharp/` publishes with the workspace's pinned .NET Native AOT toolchain. `Measure-SweepPair.ps1` gives both clients the same ordered set of verified local profiles over a current-user pipe, then records Job Object peak, sampled private memory, timings, and per-site outcomes.

Each process runs three rounds with eight concurrent nodes, five sequential HTTPS destinations per node, a shared seven-second node deadline, and 30 seconds idle after each round. The test covers website probes over Shadowsocks ChaCha20-Poly1305 with TLS obfuscation. Both versions verify the HTTPS destination certificate. The C++ client uses the supplied OpenSSL CA bundle and the C# client uses Windows trust.

The TLS obfuscation framing follows the repository's `ShadowsocksTlsObfs.cs` port of [gost v2.11.5 obfs.go](https://github.com/ginuerzh/gost/blob/v2.11.5/obfs.go), with its notice in `build/DesktopBaseline/Gost.LICENSE.txt`. The paired test omits the desktop UI, ICMP and exit-IP requests.
