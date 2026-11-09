# T02 Windows 只读宿主

后续 Scheduler 可引用 src/ProxyPartnerLite/Host/ProxyPartnerLite.Host.csproj，使用正常入口读取瞬时类型化事实。

~~~csharp
var host = new ProxyPartnerLite.Host.ReadOnlyHostClient();
var result = await host.ReadAsync(cancellationToken: stoppingToken);
~~~

默认使用命名管道，依次读取 /version 和 /proxies。显式启用 HostReadOptions 的 IncludeProviders 时追加 /providers/proxies。发送边界只接受这三条精确 GET，拒绝 /configs 及其变体。Facts 包含观察时间、叶子节点、组及各组当前选项。未请求的 provider 保持 null。已请求的 provider 只保留 HTTP、File、Inline 的成员关系，Compatible 组桶省略。响应受 8 MiB、JSON 深度、字段和节点上限约束。失败状态携带脱敏的路由及 HTTP 状态，调用方只在成功时消费 Facts。

组的当前选项只表示宿主报告的组选择。展示方将全局路由模式标为未确认。Mihomo v1.19.21 的 getConfigs 返回完整通用配置，可能带入站认证和协议参数。拒绝此路由避免接收这些字段。 [getConfigs](https://github.com/MetaCubeX/mihomo/blob/3aa668c55704399ede35cd710a48c20111d23ea6/hub/route/configs.go) · [GetGeneral](https://github.com/MetaCubeX/mihomo/blob/3aa668c55704399ede35cd710a48c20111d23ea6/hub/executor/executor.go) · [General 与 Inbound](https://github.com/MetaCubeX/mihomo/blob/3aa668c55704399ede35cd710a48c20111d23ea6/config/config.go)

当前发现范围是同 Windows 用户、同会话、同安装目录的 Verge UI 和它直接启动的 Mihomo 子进程。身份依赖进程和已连接管道的 PID，不要求 Authenticode。服务启动拓扑、跨用户实例和多实例的选择 UI 留待相应工作。 [Windows 管道服务 PID](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeserverprocessid) · [Windows 进程快照](https://learn.microsoft.com/en-us/windows/win32/toolhelp/taking-a-snapshot-and-viewing-processes)

回退由调用方明确提供 LoopbackFallbackAuthorization，绑定核心 PID、启动时刻、批准的绝对文件路径和 SHA256。只有已验证核心的管道无法建立时才尝试回退。支持受限的 UTF-8 控制器 YAML，只接受无引号的 external-controller 和 secret 两个顶层标量、空行及独立注释。地址仅支持显式 IPv4 127.0.0.1 与端口。该范围可用虚构配置复验，完整 Verge YAML 及实际 -f 回退尚未验证。打开后校验文件最终路径及内容哈希。核对监听所有者和已连接四元组后才构造带 Bearer 的请求，配置值和 Secret 不进入结果。 [Windows TCP 所有者表](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedtcptable)

## 精确候选复验

从候选工作树使用 Windows x64、PowerShell 7.3+ 和 T00 锁定的隔离 SDK。每次验收指定新的绝对证据目录，四项在该目录下分别生成 run.log、result.json 和 Native AOT 测试程序。

~~~powershell
$toolchain = 'F:\PachinStudio\ProxyPartner\runs\toolchain'
$evidence = 'F:\PachinStudio\ProxyPartner\runs\T02\<unique-verifier-run>'
foreach ($check in 'pipe-handshake','fallback-auth','readonly-routes','no-secrets') {
    pwsh -NoProfile -NonInteractive -File tests/host/Test-HostAccess.ps1 -Check $check -ToolchainRoot $toolchain -EvidenceDirectory $evidence
    if ($LASTEXITCODE -ne 0) { throw "$check exited $LASTEXITCODE" }
}
~~~

pipe-handshake 使用当前真实宿主，只发命名管道白名单 GET，输出版本、PID、路由与数量。回退和错身份测试使用开发专用临时回环、假管道、进程与 YAML，正常结束时关闭夹具并删除合成凭证文件。退出 0 表示该项通过，2 表示真实宿主前提不足，1 表示失败。产品库没有监听入口。

## 源码适配和许可

生产库使用 BCL 和 Windows API。测试入口沿用 T00 的 SDK、Native AOT 和运行时包。实现按项目 MIT 编写，以下 GPL v3 上游只用于确认公开接口、数据形状和可观察行为，源码、配置生成器和宿主实现没有移植。

| 仓库与固定 ref | 文件、blob 和查阅符号 |
| --- | --- |
| Verge v2.4.7，520a7ed83fda06b6507c46e51e16f737c58c7ddc | src-tauri/src/utils/dirs.rs，2e986e1a3cba37d378a60b5bd4ce189b01d3bbdb，ipc_path、app_home_dir。src-tauri/src/config/clash.rs，1d2f53b99f9d2fcc43ab69142fb7bb9f2bba3217，guard_external_controller_ipc、get_client_info。 |
| Mihomo v1.19.21，3aa668c55704399ede35cd710a48c20111d23ea6 | hub/route/server.go，4e0d0c93f5f371c6e915a71f82e56cfe111d8e41，startPipe、router、authentication。hub/route/proxies.go，bc7eed44c30e7d193f0990a1b8904acd64417a7a，getProxies、getProxyDelay。 |
| Mihomo 同 ref 的配置拒绝依据 | hub/route/configs.go，7cf2c6cc87e7d20edd712f6c7ac26ec6cf362015，getConfigs。hub/executor/executor.go，5db0744a9e5d21fbadd63ac32221010be33fcfbb，GetGeneral。config/config.go，e02cfead9d265aecce77f387d55747e2be2ff978，General、Inbound。只用于核对该路由的响应边界。 |
| Mihomo 同 ref 的 provider 路径 | hub/route/provider.go，c060cc895dbf2e09b526b6711fa5e3c8a41e6ffc，getProviders、healthCheckProvider。adapter/provider/provider.go，1faa2147dd28fd02bca229d542dd242183aa2503，providerForApi、各 provider 的 MarshalJSON。 |

两份根 LICENSE blob 均为 f288702d2fa16d3cdf0035b15a9fcbc552cd88e7。 [Verge LICENSE](https://github.com/clash-verge-rev/clash-verge-rev/blob/520a7ed83fda06b6507c46e51e16f737c58c7ddc/LICENSE) · [Mihomo LICENSE](https://github.com/MetaCubeX/mihomo/blob/3aa668c55704399ede35cd710a48c20111d23ea6/LICENSE)
