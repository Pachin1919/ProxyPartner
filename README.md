# ProxyPartner Lite · Codex 实施包 v3
**2026-11-04｜Windows 优先｜节点质量测试面板，不是代理客户端。**

从 `CODEX_START.md` 开始。此包替代此前 ProxyBench/ProxyBoard 大型面板计划；旧包只保留历史价值，不再作为并行指令。

这是规格、源码核查记录、任务图、验收工具与测试夹具，不是已写好的软件，也不声称达到内存目标。

2026-11-06 开发规划更新：gpt-6.1-sol 指挥与分工验收，开发期引入 LangGraph；不自动使用 Astra。设计入口见 `spec/DESIGN.zh-CN.md`，循环/交接/breakout 见 `WORKFLOW.zh-CN.md` 和 `workflow/`。LangGraph 目前仅完成设计，执行器尚未实现和运行。原始 `SHA256SUMS.txt`、`VALIDATION.md` 和 `validation/` 是 11 月 4 日资料包快照，不覆盖之后修改的工作区；新检查单独记录。

## 本版决定
- 用户：已有 Clash Verge Rev 2.4.7 / Mihomo 1.19.21。宿主继续处理订阅、代理、路由。
- 自动本机接入；不再输入订阅 URL；不提供切节点、改规则、远控或代理服务。
- 首页面板是一张节点×网站表，重点看 OpenAI API、ChatGPT 的表现和透明评分；托盘驻留，窗口关闭不绘图。
- C# 主程序、按需 Go 探测、SQLite。为了低于 50MB，**先试 C# Native AOT + Windows 原生虚拟列表**；Avalonia 不再是强制依赖，只在同预算实测通过时才可选。
- 新的工程预算：托盘目标 25 MiB；窗口目标 35 MiB；产品全部进程总峰值门槛 40 MiB。均是待验证目标，非实测成绩。1 MiB = 1,048,576 bytes。
- 对比网页时要保持任务相同；不靠关掉核心采集、漏算 worker、清工作集制造“瘦身”。

## 读取顺序
1. `CODEX_START.md` / `AGENTS.md`：首轮任务和边界。
2. `DECISIONS.zh-CN.md`：几轮讨论形成的决定及纠错。
3. `spec/PRODUCT.md` / `spec/ARCHITECTURE.md` / `spec/PERFORMANCE.md`。
4. 分配到任务后按需读 `research/SOURCE_AUDIT.md`，不是让所有 agent 每次通读所有文档。
5. `WORKFLOW.zh-CN.md` / `workflow/tasks.json`：依赖与返工循环。

## 包内内容
- 来源可追溯到具体源文件、函数、版本/分支、已取得的 Git blob SHA；区分本轮复核与前轮源码阅读。
- `contracts/`：JSON Schema、目标配置、性能预算、回执格式。
- `storage/schema.sql`：强类型存储设计；`storage/retention.sql`：小批清理示例。
- `tools/flow/`：已有 Go 标准库工作流校验器；`tools/budget/`：性能 CSV 门槛检查器。开发工具，不随产品发行。
- `scripts/Measure-ProcessTree.ps1`：Windows 外部采样辅助，不能替代 Job Object 峰值/真实桌面验证。
- `diagrams/`：依赖图、开发循环、运行数据流，包含可编辑 DOT/Mermaid 与渲染图；不需要 Node。
- `VALIDATION.md`：本次真正运行了什么，以及还没有运行什么。
- `tools/kitchecks/verify.py`：可选的资料包/SQL/契约校验，使用Python与jsonschema，仅用于开发验证；不进入产品，也不增加最终用户安装依赖。

## 完成标准
被动读取预览版不等于完成品。正式 Lite 必须具备自动接入、历史与清理、指定节点网站补测、出口观察、目标权重及解释，并通过全程内存、真实 Windows 和不干扰宿主验证。任一失败都不能用“Lite 先删功能”冒充完整达成。
