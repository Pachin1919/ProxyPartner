# 复制这段给新 Codex 会话

请以本目录为 ProxyPartner Lite 新项目的实施依据，先读 AGENTS.md、spec/PRODUCT.md、spec/PERFORMANCE.md 和 workflow/tasks.json。

Windows 便携版的启动流程、实施顺序和验收产物见 [ProxyPartner.exe 交付执行文档](workflow/EXE_DELIVERY.zh-CN.md)。用户日常只运行一个现有代理宿主，ProxyPartner 从这个宿主读取节点。交付验证从双击主程序开始。

只使用 gpt-6.1-sol 指挥及执行/验收角色，不自动切 Astra。先读 spec/DESIGN.zh-CN.md、WORKFLOW.zh-CN.md 和 workflow/LANGGRAPH.zh-CN.md。开发期 LangGraph 启动验收已通过，产品从 T00 开始。交接按 workflow/HANDOFF.zh-CN.md，退出/重新规划按 workflow/BREAKOUT.zh-CN.md，参数按 workflow/orchestrator.policy.json。Python/LangGraph 不进入产品。 [本机启动验收](runs/orchestrator/bootstrap/verifier-80a6330/report.md)

我要 Windows 轻量常驻“节点质量测试面板”，自动接入我已运行的 Clash Verge Rev 2.4.7 / Mihomo 1.19.21，不重新填订阅，不控制代理。重点是 OpenAI API/ChatGPT 网站延迟、历史、IP观察和自定义加权排名。不要做完整代理软件或网页壳。

先执行 T00→T01→T02→T03：锁定真实工具链和测试入口；建立进程树内存测量；验证本机 named pipe 或已授权本机 HTTP 只读接入；比较最小 C# Native AOT + Win32列表与 Avalonia AOT 的资源基线。首选原生虚拟列表，不为跨平台添加负担。目标托盘≤25MiB、窗口≤35MiB、主进程+Go等所有产品进程峰值≤40MiB。预算是验收要求，不允许在报告里伪称已经实现。

自动接入：优先 `\\.\pipe\verge-mihomo`，读取 /version、/proxies、/providers/proxies；按实际版本验证身份/权限。确实不可用时再从用户允许的宿主配置自动读本机控制地址与Secret。只读现有配置，不重新下载订阅、不暴露密码。发现多个实例才让用户选宿主，不问已知答案。

每项实现使用独立 worktree；实现者与验证者分离，回执绑定当前SHA，失败返回原任务修复。使用 tools/flow 验证依赖与证据。没有子代理能力就串行做第二轮独立验证，并说明限制；不要虚构工具或永久后台任务。

先完成真实自动接入+轻量表格+SQLite一条闭环。之后继续已就绪任务，不要停在架构图或空接口。指定节点Go探测必须有真实有效配置、取消、预算和路径验证，不能假装普通API能返回密码；不能偷偷改GLOBAL节点或用live healthcheck代替隔离测试。

请读 research/SOURCE_AUDIT.md 中与当前任务对应的源码条目；优先复用其成熟方法而不是从零设计协议。当前包只包含规格和验收工具，产品源码由你实现。不要自动创建GitHub仓库或发布。

每次报告：本轮真实完成的task/SHA、运行的检查、资源数据、剩余阻塞、下一ready任务。所有尚未测的Windows/内存/出口结果明确写未验证。
