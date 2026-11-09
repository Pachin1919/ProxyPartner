# T03 Desktop baseline

Publish and run from a clean Windows x64 worktree at the candidate SHA. Use the
T00 SDK and native build tools and T01 Job/sampling entry points. Run each check
once per new evidence directory. Run the build first. The later checks verify
the publish manifest SHA and reuse its executables.

```powershell
pwsh -NoProfile -NonInteractive -File tests/ui/Test-DesktopBaseline.ps1 -Check aot-build -ToolchainRoot <absolute-runs-toolchain> -EvidenceDirectory <absolute-run-directory>
```

Repeat with `100-node-ui`, `dpi-tray`, and `memory-gate`. Use ordinary-user
execution in the interactive session. T00 documented that sandbox process
restrictions can block the MSBuild task host. The sampling and listener checks
require CIM access. The test entry saves JSON, command/exit records, window-only
PrintWindow PNGs and hashes. The 125% and 200% cases send the native DPI message
to the verified product PID and inspect the resized column, while 100% and 150%
use the physical displays. The DPI/tray check opens via the real Explorer
notification icon and dispatches the Exit menu command to the verified product
tray HWND. The resource stress loop sends the equivalent open callback to that
HWND. For a manual pass,
double-click the published EXE, use Tab and End,
close the window, open the ProxyPartner tray icon (check overflow), then
right-click that icon and choose 退出.

The fixture generates seven days of synthetic observations for 100 leaves and
three targets and projects them to display values. The fixture is shared by
Win32 and the Avalonia comparison. The Avalonia process runs a short startup
probe first. If that already exceeds 40 MiB, its measured Job peak is sufficient
to reject it. Its long-duration phases remain unmeasured in that case. The
Win32 gate runs all three cold starts and the full 10-minute tray and 5-minute
window phases.

T06 references `src/ProxyPartnerLite/Desktop/ProxyPartnerLite.Desktop.csproj`,
uses the same GUI manifest settings in its entry point, and provides an
`IDesktopSnapshotSource` on the same process. `DesktopApp.Run` owns the Windows
message loop and display resources. Its caller owns observation lifecycle.

## Sources and license

- MetaCubeXD `6b81581b80466b34b603f3882c0190449cd3d5a9`, MIT. Reviewed
  `packages/ui/pages/proxies.vue` (`ProxyNodes`, `revealProxy`) and
  `packages/ui/components/ProxyNodeTableRow.vue`. The grouped-list interactions
  informed stable row selection and bounded rendering. No source copied.
  [Source](https://github.com/MetaCubeX/metacubexd/blob/6b81581b80466b34b603f3882c0190449cd3d5a9/packages/ui/pages/proxies.vue)
  [License](https://github.com/MetaCubeX/metacubexd/blob/6b81581b80466b34b603f3882c0190449cd3d5a9/LICENSE)
- Zashboard `bf4f00bb8d905f8a78f627af7cf84457c4db53c4`, MIT. Reviewed
  `src/assembly/proxies/state.ts` (`getTestUrl`, `readHistory`) and
  `src/store/conn-history.ts` (`refreshView`, `trimMap`, `flush`). Target-specific
  observations and visible-only refresh informed the snapshot contract. No source copied.
  [State source](https://github.com/Zephyruso/zashboard/blob/bf4f00bb8d905f8a78f627af7cf84457c4db53c4/src/assembly/proxies/state.ts)
  [History source](https://github.com/Zephyruso/zashboard/blob/bf4f00bb8d905f8a78f627af7cf84457c4db53c4/src/store/conn-history.ts)
  [License](https://github.com/Zephyruso/zashboard/blob/bf4f00bb8d905f8a78f627af7cf84457c4db53c4/LICENSE)
- Avalonia `12.1.5` · `5825bf8c66fba0c11e67c980d8ece6444576bffa`, MIT.
  Reviewed `ListBox.cs` (virtualizing default panel), `TrayIcon.cs`
  (`SetIcons`, `Clicked`) and `Win32Platform.cs` (`UseWin32`). Referenced
  NuGet packages and transitives are pinned with content hashes in
  `AvaloniaBaseline/packages.lock.json`. This dependency is limited to the
  comparison project. No Avalonia source copied into the product.
  [ListBox source](https://github.com/AvaloniaUI/Avalonia/blob/5825bf8c66fba0c11e67c980d8ece6444576bffa/src/Avalonia.Controls/ListBox.cs)
  [License](https://github.com/AvaloniaUI/Avalonia/blob/5825bf8c66fba0c11e67c980d8ece6444576bffa/licence.md)
