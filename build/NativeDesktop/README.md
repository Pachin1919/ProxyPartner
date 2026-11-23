# Native Win32 desktop candidate

`desktop.cpp` replaces the desktop executable with C++17 and Win32. It consumes the
same-directory `ProxyPartner.Cli.exe` using the `af29444` `inspect` / `probe` JSONL
contract. `node` events may report `probing`, and completed `round` events contain
the five website results. A Windows Job Object owns the CLI tree before it runs.

Build with the existing local toolchain and headers, supplying a fresh absolute output directory.

```powershell
pwsh -File build/NativeDesktop/Build.ps1 -OutputDirectory F:/path/to/new-run -Tests
pwsh -File build/NativeDesktop/Package.ps1 -CliDirectory F:/path/to/native-cli -OutputDirectory F:/path/to/new-package
```

The build script records compiler arguments, exit code, source SHA and published
file hashes. It reads the existing application ICO, manifest and five local site
icons. It packages the cached nlohmann JSON v3.12.0 MIT license. No SDK download is
performed. `HeaderRoot` and `ToolchainRoot` can override the known local cache paths.

The GUI supports Unicode node names, card and virtual table views, filtering,
sorting, node selection, full website details, refresh, all/selected node tests,
live JSONL progress, cancellation, tray hide/reopen/exit, bilingual labels, light
and dark themes, and persisted paths/probe parameters. Only known event fields
are displayed, and CLI stderr is discarded to avoid exposing configuration errors
that may contain secrets. Malformed or over-limit output stops the owned tree.

`tests.cpp` creates hidden test controls and uses an explicitly synthetic CLI
fixture. It verifies protocol handling and cancellation without accessing a user
profile or making requests. These tests do not establish visual acceptance or
resource compliance. `Portable.README.txt` lists the remaining application scope.

Reuse is from this repository at `af29444` under its existing license. The Win32
virtual list, card layout, colors, native icon loading, DPI handling and preference
behavior follow `src/ProxyPartnerLite/Desktop/`. The existing
`experiments/cpp-probe/desktop.cpp` is a transport DLL and provides no window code.
