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

For the coordinator's real-network acceptance, the test executable also has an
explicit mode which calls the actual `App::Start` and JSONL receive path. It uses
hidden native controls and no desktop input. It inspects and then probes once
with a fixed 6000 ms deadline and adaptive concurrency 4–8. Supply an output file
which does not exist. The report contains aggregate counts, scores and Job Object
peak through report time, including the acceptance process and all CLI descendants.
No node names, addresses, configuration contents or identity material are written.
`COMPLETE` requires a nonzero eligible catalog, terminal summary, completed and
scored counts covering that catalog, and five attempted terminal sites plus a
numeric score for each eligible node. HTTP rejections and attempted timeouts may
still complete. Missing attempts or zero scored nodes produce `INCOMPLETE`.

```powershell
NativeDesktop.Tests.exe --acceptance --cli-dir F:/path/to/publish --profile F:/approved/profile.age --age-identity F:/approved/identity.txt --output F:/new-run/acceptance.json
```

The identity option is optional for plaintext profiles. This explicit mode makes
real network requests. It does not establish visual acceptance or whole-lifetime
peak after the report has been written. An external owning Job can measure the
process through exit for that final gate.

Reuse is from this repository at `af29444` under its existing license. The Win32
virtual list, card layout, colors, native icon loading, DPI handling and preference
behavior follow `src/ProxyPartnerLite/Desktop/`. The existing
`experiments/cpp-probe/desktop.cpp` is a transport DLL and provides no window code.

The restored settings match the actual `af29444` scope. Component weights default
to 50/35/15 and target weights to 60/30/10. Each group must total 100. They update
the fixed sample example only. Custom HTTP(S) targets can be added, edited and
removed, remain untested, and make no network requests. The same source JSON
boundary reads legacy C# names and native preferences, preserving the original
legacy file. Native preferences take precedence once saved. Missing or invalid
preferences fall back to defaults without overwriting either file.

The baseline has `--tray` and in-process history windows. It does not include
automatic logon installation or a SQLite-backed desktop history path. These are
not prerequisites for preserving that baseline. Current-route and exit/history
fields will be connected once the backend's actual JSONL contract is frozen.

Website cells show actual response timing when HTTP status and timing are present,
including 401 and 403. Details and native hover hints retain the status semantics.
Cards and the separate core-history table column display existing kernel latency,
with source/sample time in the hint and details. Missing samples display no data.
The default core sample option passes `--kernel-latency`, which reads existing
history through the CLI. Explicit saved preferences retain their selected option.

Active cards render an eight-dot rotating indicator beside the testing state.
A 200 ms native timer runs only while the card view is visible, the window is not
minimized, and a probing node intersects the viewport. Queue state does not count
as activity. Timer ticks invalidate only the active status strips and allocate no
bitmap. Hidden controls, viewport transitions and changing dot pixels are covered
by the offline regression harness. Real-screen motion acceptance remains separate.
