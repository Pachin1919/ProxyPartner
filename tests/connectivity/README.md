# 五目标终端探测

使用 Windows、PowerShell 7.3+ 和系统目录中的 curl.exe 8.7.0+。后端验证版本为 PowerShell 7.6.9、Windows curl 8.21.0。入口固定从仓库内的 contracts/targets.terminal-smoke.json 读取五个目标，逐条发送 HTTPS GET。

~~~powershell
pwsh -NoProfile -NonInteractive -File scripts/Test-TargetConnectivity.ps1 -Proxy http://127.0.0.1:7897
~~~

- 代理参数接受带端口的 http://127.0.0.1 或 http://[::1]。curl 禁用用户配置和 netrc，显式覆盖 NO_PROXY。每个目标最多一次请求，禁止重定向，连接和总传输上限分别为 5 秒和 12 秒。响应体丢弃，最多 64 KiB。
- batch_exit_code 为 0 表示五条记录完整输出，2 表示启动配置或运行时不满足。单个连接失败、HTTP 错误或响应体超限仍会形成一条记录。
- http_status 是目标响应，proxy_connect_status 是代理 CONNECT 响应。各自缺测时为 null。loopback_proxy_peer_confirmed 基于 curl 的 proxy_used、remote_peer_ip 和 remote_peer_port。对端证据的范围仅限本地代理套接字，节点链和出口待另行实测。
- first_byte_ms 是从 curl 传输开始到目标响应首字节的时间，包含建连、TLS 和目标等待。curl_total_ms 是传输完成或停止所花的时间。体上限导致的 curl 63 记为 response_body_limit，同时保留已经收到的 HTTP 状态及首字节。API 401 表示收到鉴权响应，模型接口业务仍未验证。

## 离线复验

~~~powershell
pwsh -NoProfile -NonInteractive -File tests/connectivity/Test-TerminalSmoke.ps1
~~~

离线用例向同一脚本注入 curl 指标，覆盖参数、五项顺序、状态分类、失败记录及批次语义。CLI 用例检查无效或带凭证的代理参数。原生夹具在临时副本中使用 .invalid 域名，监听随机回环端口，对五条 CONNECT 请求各返回 407，检查请求头、NO_PROXY 覆盖，以及真实 curl 对代理状态和目标状态的区分。夹具不转发请求。真实代理及远端 HTTPS 服务的五目标批测单独验收。

## 实现来源

- 复用项目已有实测脚本 runs/connectivity/initial/reproduce.ps1 的代理调用、计时和状态分离方式。SHA256 为 20927e5b54508a24c1bf6fed471e11115ca7f27dbe9733d1501a3e51295cb228。对应 results.json 的 SHA256 为 3f52d150ddce227e61ab015286c354fa66ebbc9b333217b59bc36bbabfa8770d。
- 实测 Windows curl 对照上游 curl/curl 固定标签 curl-8_21_0。已查 src/tool_getparam.c 的 C_NOPROXY 分支和 src/tool_writeout.c 的 variables[]，用于确定空代理例外参数及独立的目标状态、CONNECT 状态、对端和计时字段。另查同标签 max-filesize 选项定义及 COPYING。仓库通过 Windows 自带二进制调用公开 CLI，应用脚本按项目 MIT 约束编写。 [参数源码](https://raw.githubusercontent.com/curl/curl/curl-8_21_0/src/tool_getparam.c) · [指标源码](https://raw.githubusercontent.com/curl/curl/curl-8_21_0/src/tool_writeout.c) · [体上限定义](https://raw.githubusercontent.com/curl/curl/curl-8_21_0/docs/cmdline-opts/max-filesize.md) · [上游许可](https://raw.githubusercontent.com/curl/curl/curl-8_21_0/COPYING)
