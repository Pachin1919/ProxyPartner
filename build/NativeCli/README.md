# Windows 原生测速 CLI

Windows x64 命令行使用 C++20、LibYAML 和现有原生 HTTPS 适配器。入口接受明文 Clash YAML 或标准 AGE v1 X25519 文件。AGE 走进程内 OpenSSL，解析结束清理明文输入，运行包不需要下载或登录订阅。

```powershell
.\ProxyPartner.Cli.exe inspect --profile $profile --age-identity $identity
.\ProxyPartner.Cli.exe probe --profile $profile --age-identity $identity --all --min-parallel 4 --max-parallel 8 --node-timeout-ms 6000 --kernel-latency
```

JSONL 支持目录、内核状态、节点进度、每轮结果和汇总。每个节点独立拨号，五站分别报告 HTTP 状态和延迟。命名管道核对普通用户进程身份或注册的 Clash LocalSystem 服务与子进程，只读取版本和节点现有数据。策略组 GLOBAL 的选择单独标为 `globalSelection`。

入口 DNS 使用 YAML 中匹配的 HTTPS DNS 策略，WinHTTP 核验证书并协商 HTTP/2。AnyTLS 和 Mieru 共用 OpenSSL 4。Mieru V2 支持本轮经验证的 TCP 流量模式，普通 TCP 走原有入口。带 TLS 混淆的 ChaCha20-Poly1305 Shadowsocks 可选。其他协议或选项返回不可测状态。

通过 `Build.ps1` 使用本机固定工具链、C++ DLL 和 PEM CA 包生成独立目录。准备依赖时锁定 MSVC 14.44.35207、Windows SDK 10.0.26100.0、[LibYAML 0.2.5](https://github.com/yaml/libyaml/releases/tag/0.2.5)、[nlohmann JSON v3.12.0](https://github.com/nlohmann/json/releases/tag/v3.12.0)、[OpenSSL 4.0.0](https://github.com/openssl/openssl/releases/tag/openssl-4.0.0)。原生 AGE 依照 [C2SP AGE 规范](https://c2sp.org/age@v1.0.0)使用 OpenSSL 的 X25519、HKDF 和 ChaCha20-Poly1305，不引入自定义密码算法。原生适配器的版权范围见 `experiments/cpp-probe` 的来源记录，打包保留各组件许可证。
