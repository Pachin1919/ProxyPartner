# Windows x64 构建基线

T00 冻结开发机的可复验构建输入、许可边界和现有产品资源门槛。版本、SDK 下载校验和、NuGet 包及原生工具指纹见 [windows-toolchain.json](../contracts/windows-toolchain.json)。参考主机为 Windows x64 build 26200。T00 探针是一段 Win32/SQLite 验证程序，产品窗口、便携包和最低系统矩阵按后续关口验收。

| 构建输入 | 版本和范围 |
| --- | --- |
| .NET | Windows x64 SDK 10.0.403，.NET Runtime、Native AOT ILCompiler 和相关 NuGet 包 10.0.14 |
| MSVC | x64 链接工具 14.44.35229.0，Desktop 和 Store CRT 载荷 14.44.35226，Store 包提供 oldnames.lib |
| Windows SDK | Microsoft.Windows.SDK.CPP.x64 10.0.26100.9457，锁定官方包、UM 和 UCRT 库 |
| Go | Go 1.27.3 windows/amd64，独立标准库探针使用 CGO_ENABLED=0 |
| SQLite | 参考主机的 winsqlite3.dll 3.51.1，锁定运行时源 ID 并实测 STRICT 表。T05 锁定产品 SQLite 绑定、原生库和最低系统矩阵 |

## 复验

从候选工作树执行。PowerShell 7.3 及以上，使用 x64 进程。工具根指向统筹的 runs/toolchain，其同级 runs/orchestrator/go 包含 Go，runs/coordination 包含已哈希锁定的宿主报告。为每个执行会话分配新的绝对证据目录。同一会话的三项检查分别写入自己的子目录。

~~~powershell
$toolchain = 'F:\PachinStudio\ProxyPartner\runs\toolchain'
$evidence = 'F:\PachinStudio\ProxyPartner\runs\T00\owner-baseline\<run-id>'
pwsh -NoProfile -NonInteractive -File scripts/Test-WindowsBuildBaseline.ps1 -Check environment -ToolchainRoot $toolchain -EvidenceDirectory $evidence
pwsh -NoProfile -NonInteractive -File scripts/Test-WindowsBuildBaseline.ps1 -Check license-lock -ToolchainRoot $toolchain -EvidenceDirectory $evidence
pwsh -NoProfile -NonInteractive -File scripts/Test-WindowsBuildBaseline.ps1 -Check budget-contract -ToolchainRoot $toolchain -EvidenceDirectory $evidence
~~~

每项输出 result.json 和 run.log，包含实际 HEAD、命令、退出码和日志 SHA256。environment 从本地已锁定 NuGet 包还原，在证据目录生成限定 SDK 版本的 global.json 和探针工程。发布使用 Release、win-x64、PublishAot 和隔离链接工具。脚本检查 PE 为 x64 原生映像，再直接运行它，验证 Win32 进程 ID、WinSQLite 版本、源 ID 和 STRICT 表写入。Go 探针也实际编译并运行。LIB、PATH 前缀和缓存目录仅作用于此次 PowerShell 进程。Windows 沙箱若限制 MSBuild 任务间通信，按开发环境权限使用同一隔离命令复验。

宿主证据来自 2026-11-09 已匹配管道服务进程身份的只读检查，Mihomo API 报 v1.19.21。脚本重算归档证据哈希并核对版本，记录原观察时刻和提交。该证据证明当时已读到运行元数据。有效节点配置的当前合并文件、加载关联和读取授权仍待核实。T00 只记录来源边界，配置探查按 [宿主接入和配置规则](ARCHITECTURE.md) 另行验证。

## 打包与发布 Windows 便携版

在干净的 Windows x64 checkout 上运行下列命令。构建机需要上面锁定的工具链和 x64 Git for Windows。打包脚本从该 Git 安装取 OpenSSL 3 DLL 和 CA 证书，构建 Native AOT 桌面和 C++ 测速组件，输出 ZIP、SHA256 文件和构建记录。工具链位于别处时传入 `-ToolchainRoot`。

```powershell
pwsh -NoProfile -File .\build\New-PortablePackage.ps1
```

脚本最后一行是 ZIP 路径，默认写入 `runs/release/`。验收时在新目录解压，核对 ZIP 和各文件的 SHA256，再启动解压后的 EXE。确认验收通过后，从同一 checkout 发布到 GitHub。把 `$zip` 设为已验收的 ZIP 路径，把 `$tag` 设为新的预览版本号，并以已登录的 GitHub CLI 运行：

```powershell
$sha = git rev-parse HEAD
gh release create $tag $zip "$zip.sha256" --target $sha --title "ProxyPartner Lite $tag" --prerelease --draft --generate-notes
```

核对草稿附件后在 GitHub Releases 发布。预览版用户在 **Assets** 下载 `ProxyPartner-windows-x64.zip`，解压后运行 `ProxyPartner.exe`。 [GitHub CLI 发布参数](https://cli.github.com/manual/gh_release_create)

## 许可和来源

- 自研代码采用 [MIT](../LICENSE)。Win32 探针调用 kernel32.dll 的 GetCurrentProcessId，SQLite 探针调用 Windows 自带 winsqlite3.dll 的公开导出。使用系统库，SQLite 上游对应源 ID 的核心代码属于公有领域。 [Win32 API](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getcurrentprocessid) · [SQLite 3.51.1](https://www.sqlite.org/releaselog/3_51_1.html) · [SQLite 版权说明](https://sqlite.org/copyright.html)
- SDK ZIP 以 Microsoft 发布 SHA512 核对，并保留实际 LICENSE 和 ThirdPartyNotices。NuGet 10.0.14 包锁到 dotnet/dotnet 提交 0427b9abbead3fa64c6655eb4290c537397131fa。构建前提核对实际包内 Microsoft.NETCore.Native.Windows.targets 和 findvcvarsall.bat。许可检查读取包声明及通知并记录哈希。 [Microsoft SDK 发布](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) · [Native AOT 构建前提](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/#prerequisites)
- MSVC 载荷取自 Microsoft Visual Studio 发布目录，锁定四个载荷及其来源清单。Windows SDK 包的实际 nuspec 指向 SDK 条款。二者用于构建机，正式便携包按实际链接内容及运行依赖复核再分发条款和随附通知。Go 随包 LICENSE 为 BSD 3 条款，正式 Go worker 另锁标准库及第三方协议依赖。 [Visual Studio 条款](https://visualstudio.microsoft.com/license-terms/) · [Windows SDK 条款](https://aka.ms/WinSDKLicenseURL)
- Mihomo v1.19.21 的 hub/route/server.go 仅供理解命名管道和认证行为。Verge v2.4.7 的 src-tauri/src/utils/dirs.rs 仅供定位规则参考。Mihomo 与 faceair 的 GPL 源码限接口和可观察行为参考。产品依赖须在采用时记录固定版本、源码符号、许可证和范围。 [实际源码与固定 ref 清单](../research/SOURCE_AUDIT.md)

## 资源验收口径

budget-contract 检查 [机器合约](../contracts/performance-budget.json) 和 [资源规则](PERFORMANCE.md)。冻结托盘 p95 25 MiB、窗口 p95 35 MiB、全部产品进程硬峰值 40 MiB、空闲 CPU 单核等效均值 0.5%。首轮三次冷启动，100 节点、3 目标、7 天样本，再测 1000 节点压力。最终 24 小时观察按末小时相对预热首小时的中位数增长不超过 2 MiB 执行。正式峰值需 Job Object 或等价完整生命周期证据。T00 的合同检查不计作产品资源实测。
