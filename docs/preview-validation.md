# Windows preview validation

Source revision [`d3d0994`](https://github.com/Pachin1919/ProxyPartner/commit/d3d09945dcbccec2b84ad48e1b5d9643c559cf2f). The screenshots in the READMEs come from the built-in demo in the same Windows x64 EXE. The table below describes a separate test with a real subscription.

## Live Shadowsocks scan

| Check | Result |
| --- | --- |
| Host and workload | Clash Verge Rev with Mihomo v1.19.21 on Windows x64. 41 Shadowsocks nodes with TLS-mode `obfs`. Each eligible node was scheduled for five site probes and two exit-IP observations. |
| Scan coverage | 41 of 41 eligible nodes completed. All 33 nodes counted as non-current returned timings for all five sites. |
| Scan time | 11.46 seconds from launch to the observed completion of the scan. |
| Average private memory | 25.08 MiB. Time-weighted average over 13 samples spanning 10.02 seconds during the scan. |
| Host state | The host process, configuration hash, and policy-group selections matched before and after the scan. |

These figures describe one run. Network conditions and the mix of node protocols affect scan time and memory.

## Protocol tests

The loopback tests loaded local YAML through the same profile loader used by the desktop app. The same independent probe code received an HTTP response and two fixture exit-IP replies for each case.

| Node type | Verified transport |
| --- | --- |
| Trojan | TCP with TLS, WebSocket with TLS. |
| VMess AEAD | TCP, WebSocket, WebSocket with TLS. |

HTTP Upgrade passed configuration mapping for both protocols. A connection test for that transport remains pending. The mixed-profile test also verified that legacy VMess, unsupported transport options, and unknown protocols stay out of the independent scan queue.

The API probes time HTTPS responses from OpenAI and Anthropic. Model requests require their own credentials and tests.

## Build information

The preview used .NET SDK 10.0.403, Native AOT for Windows x64, and QuickProxyNet 5.0.0. The EXE SHA-256 is `64C95CE0883DC8414E3E12FFCA49FB3016B0788B472474409D3C662AAA2C7A45`. [Build procedure](../spec/BUILD_BASELINE.zh-CN.md) · [Dialer package](https://www.nuget.org/packages/QuickProxyNet/5.0.0)
