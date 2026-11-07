# 项目整理与目标

## 目标
ProxyPartner Lite 是 Windows 上与现有 Clash Verge Rev / Mihomo 配合的轻量节点质量测试面板。自动只读接入已有宿主，展示节点在 OpenAI API、ChatGPT 等目标上的历史表现、出口观察与可解释的自定义加权排名；不管理订阅、规则或切换节点。

正式 Lite 包含被动历史、指定节点网站补测、IP 观察、SQLite 保留和清理、评分，以及托盘和表格。observer-preview 仅代表被动接入与基础显示，不能冒充正式版。

## 工程边界
C# 主进程、SQLite；Go 按需执行隔离探测。先比较 Native AOT + Win32 虚拟列表与 Avalonia AOT。禁止网页壳和本地 UI 服务，不修改宿主配置或使用 live healthcheck。托盘 25 MiB、窗口 35 MiB 是目标；全部产品进程峰值 40 MiB 是门槛，均尚未实测。

## 文件分类
- `spec/`、`DECISIONS.zh-CN.md`：产品、架构、测量和决策依据。
- `contracts/`、`storage/`、`fixtures/`：数据契约、数据库设计和测试样例。
- `research/`：上游来源、许可和依赖核查；复用前仍须核验相关源码及许可。
- `workflow/`、`WORKFLOW.zh-CN.md`、`.agents/`、`.codex/`：任务依赖、执行流程和代理配置。
- `tools/`、`scripts/`：开发期流程、预算和校验工具，不代表产品实现。
- `diagrams/`：架构及流程图；`validation/`、`VALIDATION.md`：打包时的验证记录，不代表本机产品验收。

## 当前状态与下一步
此目录是实施资料包，尚无桌面产品实现。`workflow/state.json` 中 T00–T11 均为 pending。2026-11-06 已补开发设计和 Sol/LangGraph 交接与 breakout 方案；先完成执行器启动验收，再从 T00 环境与契约锁定开始。具体启动与约束见 `CODEX_START.md` 和 `AGENTS.md`。

2026-11-05 解压时曾核对原包内 SHA256 通过。项目已被用户移至工作区根目录；原包位置以实际磁盘为准。原清单不覆盖后续工作区修改。2026-11-06 检查时仓库尚无提交或远程地址；远程创建、推送与发布仍按用户授权执行。
