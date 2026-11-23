# Windows 原生测速 CLI

Windows x64 命令行使用 C++20、LibYAML 和现有原生 HTTPS 适配器。入口接受明文 Clash YAML 或标准 AGE v1 X25519 文件。AGE 走进程内 OpenSSL，解析结束清理明文输入，运行包不需要下载或登录订阅。

```powershell
.\ProxyPartner.Cli.exe inspect --profile $profile --age-identity $identity
.\ProxyPartner.Cli.exe probe --profile $profile --age-identity $identity --all --min-parallel 4 --max-parallel 8 --node-timeout-ms 6000 --kernel-latency
.\ProxyPartner.Cli.exe current-path
.\ProxyPartner.Cli.exe exit-ip --profile $profile --age-identity $identity --node $node
```

JSONL 支持目录、内核状态、节点进度、每轮结果和汇总。每个节点独立拨号，五站分别报告 HTTP 状态和延迟。命名管道核对普通用户进程身份或注册的 Clash LocalSystem 服务与子进程，只读取版本和节点现有数据。策略组 GLOBAL 的选择单独标为 `globalSelection`。

本机 `.json` 可使用旧版节点名到分享链接的对象格式。无效链接、重复名称及不支持的参数显示为不可测。Trojan 使用 [Trojan 协议](https://trojan-gfw.github.io/trojan/protocol) 的 CONNECT 帧，支持 TLS 上的 TCP、WebSocket 和 HTTP Upgrade，外层代理与内层网站分别验证证书。受控测试确认了这三种传输的五站请求及出口观察。

入口 DNS 使用 YAML 中匹配的 HTTPS DNS 策略，WinHTTP 核验证书并协商 HTTP/2。Mieru V2 支持经验证的 TCP 流量模式。Shadowsocks 支持标准 AEAD 和 TLS-obfs，共四种加密方式。Trojan、VLESS 和 VMess 共用原生 TCP、TLS 和 WebSocket 传输层。VLESS 支持标准 TLS 和受控回环明文，VMess 支持 alterId=0 的 AEAD。VLESS REALITY、XTLS Vision 和客户端指纹尚未迁移。JSON 支持普通 Shadowsocks、Trojan、VLESS 和 VMess 链接，包括 VMess 的 base64 JSON 形式。

通过 `Build.ps1` 使用本机固定工具链、C++ DLL 和 PEM CA 包生成独立目录。准备依赖时锁定 MSVC 14.44.35207、Windows SDK 10.0.26100.0、[LibYAML 0.2.5](https://github.com/yaml/libyaml/releases/tag/0.2.5)、[nlohmann JSON v3.12.0](https://github.com/nlohmann/json/releases/tag/v3.12.0)、[OpenSSL 4.0.0](https://github.com/openssl/openssl/releases/tag/openssl-4.0.0)。原生 AGE 依照 [C2SP AGE 规范](https://c2sp.org/age@v1.0.0)使用 OpenSSL 的 X25519、HKDF 和 ChaCha20-Poly1305，不引入自定义密码算法。原生适配器的版权范围见 `experiments/cpp-probe` 的来源记录，打包保留各组件许可证。
