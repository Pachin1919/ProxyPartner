# 技术路径与自动接入
设计总览、界面交互和实施关口见 DESIGN.zh-CN.md。开发期 LangGraph 与产品进程分开，接法和待实现状态见 ../workflow/LANGGRAPH.zh-CN.md。
## 主架构
C# AOT主进程 = 本机发现 + typed JSON流读取 + 采样调度 + SQLite单写入 + 算法 + 托盘/轻量表格。
Go子进程 = 只有独立节点拨号/HTTP阶段测量/IP echo；一次一个节点，一批结束退出；stdin/stdout JSONL，stderr有界脱敏日志。
生产版不开任何TCP/UDP监听器，不包含代理客户端或宿主内核管理。独立dialer需要协议实现，但不是第二套常驻代理服务。

## UI选型随新预算收缩
默认低内存候选：C# Native AOT调用Win32窗口/托盘与 `LVS_REPORT | LVS_OWNERDATA` ListView。只为可见行提供数据，排序维护索引；自绘颜色可通过NM_CUSTOMDRAW；不写自己的布局引擎。处理中文、DPI、键盘、焦点、关闭/托盘恢复和可访问性。
小范围比较Avalonia Native AOT基线；只有相同负载下满足预算且节省开发成本时才保留。不要两套UI同时发版，不引入WinForms/WPF后假设AOT必然支持。窗口技术切换是工程候选，不改变C#业务层和Go的职责。
Native AOT仍有runtime/GC/本机库内存；不是“编译成exe就零运行时”。UI隐藏停止绘制与重计算，不暂停必须的采样。

## 已核实的宿主接口 [SRC01,SRC02]
Clash Verge Rev 2.4.7 `utils/dirs.rs:ipc_path()` 在Windows返回 `\\.\pipe\verge-mihomo`。Mihomo1.19.21 `hub/route/server.go:startPipe()` 调用 `router(..., "", ...)`，在该接口不启用HTTP Secret校验；TCP/TLS接口使用Secret。
这不是第三方稳定契约。先验证管道存在、普通用户权限、server PID/进程路径/版本和实例关联；pipe不可用不得自动提权或改ACL。不要向未验证的管道发送节点配置或Secret。
C#用NamedPipeClientStream + SocketsHttpHandler.ConnectCallback，HTTP/1.1，UseProxy=false、AllowAutoRedirect=false、有限timeout；是调用宿主，不是本程序开HTTP服务器。

## 自动接入回退顺序
1. 明确用户授权只读本机Verge后，发现已知管道，GET /version握手；再获取节点列表。
2. 管道不可用，读取当前用户已知Verge目录里的控制配置，采用已开启的loopback接口和Secret。不得假设默认Secret；不得发送到远程地址/不确定进程；禁重定向。
3. 标准版路径通过用户目录/APP_ID解析；便携版通过已发现的可执行文件旁 `.config/PORTABLE` 与 `.config/<APP_ID>`。路径来源见SRC01。只监视候选文件变化，去抖；不递归扫描全盘。
4. 无法解析或有多个实例时，仅请用户选择现有配置目录/宿主；自动填接口。最后才一次性手填本机地址/Secret，不再问订阅。
5. 本机HTTP调用不受浏览器CORS限制，但仍要认证、正确路由与操作系统权限。不要在宿主关闭安全措施。

## 节点列表和完整拨号配置不是一回事
`/proxies`和`/providers/proxies`主要提供公开元数据与有限历史。被动模式不需要密码。
隔离Go补测需要宿主已解析的当前运行配置/订阅缓存。只读**最终配置**优先于原订阅，因为合并、覆写、脚本可能改变配置。执行脚本由宿主负责，本软件绝不执行订阅脚本。依据宿主版本核实运行文件路径；`clash-verge.yaml`仅为候选，不凭名字就确认它已生效。
只把所测节点和必要依赖通过stdin传给worker；不保留全量敏感配置，不自动访问订阅URL。完整配置不可得或链式依赖无法复现时返回unsupported/unverified，保留被动结果。
节点身份：source_instance+provider+native_id映射为内部ID；拿到配置后用本机盐HMAC生成revision。不能只按server:port去重，也不能仅以显示名合并。

## live只读白名单
常规读取限于 GET /version 和 /proxies。需要核对节点归属时，显式读取 GET /providers/proxies。界面按组展示当前选择。全局路由或实际连接缺少证据时显示未知。
Mihomo v1.19.21 的 GET /configs 返回整份通用配置，包含可能携带入站认证的字段。本版拒绝该路由。 [配置接口](https://github.com/MetaCubeX/mihomo/blob/3aa668c55704399ede35cd710a48c20111d23ea6/hub/route/configs.go) · [通用配置实现](https://github.com/MetaCubeX/mihomo/blob/3aa668c55704399ede35cd710a48c20111d23ea6/hub/executor/executor.go)
发送边界只接受三个精确 GET 路径。配置、测速、健康检查、连接、日志与写操作均不在白名单。回退凭证仅用于已授权且确认归属的回环连接。

## Go补测与副作用
以 Mihomo 与 faceair 的源码核对宿主接口、指定拨号和测试口径。当前宿主现场为 VLESS 与 Mieru，首轮优先从获准的真实 VLESS 线路验证适用传输、许可与资源成本。自研代码使用 MIT，GPL 参考代码不进入实现。
T08 在选型前核对拨号库的实际依赖、许可证、路径证据和资源成本。完整 Lite 仍须通过 40 MiB 进程树门槛，探测保持宿主白名单读取边界。
全局并发1、节点级生命周期、无geodata/TUN/listener初始化、限制连接池与缓存。Go GOMEMLIMIT只是软runtime限制，不是Windows进程硬上限，不能当达标证据。
TUN或系统过滤器可能串联已有代理；独立拨号≠路径天然独立。路径无法验证就标route_unverified，不发独立节点榜单。测试仍消耗网络带宽，不声称完全零影响。
