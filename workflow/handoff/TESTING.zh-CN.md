# 测试子对话交接

第一项接手是独立复验开发期 LangGraph bootstrap。使用后端交来的依赖锁、候选 SHA、文件交接入口和假适配器，用实际命令覆盖流程策略的 8 项关口。保留进程重启前后的证据，并核对真实 Sol 交接和运行记录。 [启动验收](../LANGGRAPH.zh-CN.md) [流程策略](../orchestrator.policy.json)

Bootstrap 独立报告放在协调根目录的 `runs/orchestrator/bootstrap/<verifier-run>/report.md`。按八个检查 ID 逐项列出候选 SHA、命令、退出码、证据路径及哈希，给出启动结论。产品回执继续按 Txx 任务和现有 schema 提交。 [回执契约](../../contracts/verification-receipt.schema.json)

产品阶段对每个候选及集成 SHA 分开复验。以 `tasks.json` 的 `required_checks` 取得必验项，用干净工作树执行。记录命令、退出码、时间、负载、日志路径及 SHA256，按回执契约提交 PASS、FAIL 或 BLOCKED。必需检查缺证据时给出缺失前提与 NOT_RUN。行为、Windows/实网、资源、视觉交互、作者审阅各有结论。 [任务图](../tasks.json) [回执契约](../../contracts/verification-receipt.schema.json) [验收清单](../../spec/ACCEPTANCE.md)

T01 作为 owner 交进程树采样与短命子进程峰值的测量工具，T11 交 24h、干净 Windows 与便携发行证据。两项各由另一个 Sol 会话在独立工作树核验。常规独立验收发现失败时，回传最小复现和 failure signature 给主责子对话。 [性能规则](../../spec/PERFORMANCE.md) [交接与集成](../HANDOFF.zh-CN.md)
