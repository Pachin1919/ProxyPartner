# ProxyPartner Lite

**比较 Clash 节点在常用网站上的表现，再决定切哪条线路。**

同一个节点打开 Google 很快，访问 ChatGPT 也可能超时。ProxyPartner Lite 是一款 Windows 节点测速面板，接入本机 Clash Verge Rev 使用的 Mihomo 内核，把 Google、YouTube、OpenAI API、ChatGPT 和 Anthropic API 的测速结果放在同一个窗口。

![ProxyPartner Lite 深色卡片界面，展示演示节点的评分和各网站的测速结果](docs/images/proxypartner-dark-zh.png)

- **按网站选节点。** 用卡片查看各站延迟和超时，用表格按评分排序。OpenAI API 和 ChatGPT 分开测。
- **边用边测。** 兼容节点可以在后台独立测速，当前线路照常使用。选好节点后，双击卡片，或在表格选中一行后按 Enter 即可切换。
- **查看出口 IP。** 两个外部服务分别回报看到的地址，面板显示两处结果是否一致。

Windows 预览版支持中英文、深浅主题，以及 VLESS、Shadowsocks、Trojan 和 VMess 的常见配置。一轮 Shadowsocks 实测跑完 41 个节点，用时约 11 秒，平均私有内存约 25 MiB。

## C++ 独立网站测速

在 Windows x64 上构建便携版时，向 `build/Publish-DesktopBaseline.ps1` 传入 `-ToolchainRoot`、`-EvidenceDirectory`、`-OpenSslBin` 和 `-ProbeCaBundle`。OpenSSL 目录提供 x64 的 OpenSSL 3 DLL，CA 参数指向 PEM 证书包。构建产物在证据目录下的 `win32/publish`，运行其中的 `ProxyPartner.exe`。

便携版使用 C++ 测试带 TLS 混淆的 ChaCha20-Poly1305 Shadowsocks 节点的五个网站。现有 C# 拨号继续覆盖其他配置和出口 IP 探测。节点详情的评分来源会标明 C++ 测速。 [构建与测试说明](experiments/cpp-probe/README.md)
