# 后端子对话交接

第一份正式 brief 交开发期 LangGraph bootstrap。输入包括可解析的 base SHA、独立工作树、任务图、流程策略、回执契约和模块分工。`tools/orchestrator/` 交付本地执行器、精确依赖锁、文件交接入口及行为测试。先跑假适配器的路由、修复上限、派工对账和重启恢复，再交测试子对话在实际候选 SHA 上独立运行启动关口和限定的 Sol 交接。执行器存储及依赖归开发工具。 [执行器设计与启动验收](../LANGGRAPH.zh-CN.md) [流程策略](../orchestrator.policy.json)

| 产品阶段 | 后端交付 | 主要路径 |
|---|---|---|
| T00 取证 | 把工具链、宿主版本、AOT 和许可证证据交统筹锁定 | T00 brief 指定的证据位置 |
| T02、T06 | 自动发现和白名单读取，串接采集调度、持久去重与展示快照 | `src/ProxyPartnerLite/Discovery/`、`Host/`、`Scheduler/` 及当前任务测试 |
| T04、T05 | 类型化节点与样本，SQLite 事务、汇总、水位与清理 | `src/ProxyPartnerLite/Domain/`、`Storage/`、`contracts/`、`storage/` 及当前任务测试 |
| T08、T09 | 单节点 Go 探测、取消、预算、路由证据与脱敏 JSONL，接收算法 T09 补丁 | `engine/`、`Scheduler/`、T09 的 `Domain/` 和探测测试 |
| T10 | 真实数据贯通、重启/休眠/清理及合计内存复验 | T10 brief 按实际影响收窄 |

在宿主/worker 边界验证原始值，把节点 revision、目标及方法、观察时刻、结果分类与路由状态投影为现有类型化样本。算法消费同口径事实，前端消费有限的展示数据。原始宿主配置和凭证留在授权访问边界，采集请求限于规格中的只读白名单。 [架构与接入](../../spec/ARCHITECTURE.md) [数据与保留](../../spec/DATA_AND_RETENTION.md) [Go IPC](../../spec/IPC.md)

T06 沿用前端 T03 交接的展示输入。涉及桌面路径、共享性能工具或跨模块契约的变更先由统筹调整 brief，再交对应写入方。T09 接收算法已释放的规则补丁后接入调度，T10 由统筹把整合范围收窄到实际影响。 [模块交接顺序](README.md) [任务图](../tasks.json)

回交 build/命令/退出码与日志 hash。宿主阶段附请求白名单、配置前后和授权证据，SQLite 阶段附去重与清理结果，Go 阶段附取消退出、路径和包含子进程的峰值。当前写入范围以任务 brief 为准。 [交接证据](../HANDOFF.zh-CN.md)
