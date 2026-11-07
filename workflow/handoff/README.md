# 模块分工与统筹交接

本对话负责思考统筹和交接文档。统筹持有任务依赖、写入范围、模块契约、候选集成与验收记录。前端、后端、算法、测试各由一个 Sol 子对话负责，按 brief 接手具体工作。角色、任务主责和跨模块交付顺序见 [模块归属表](../module-ownership.json)。

| 子对话 | 负责的工作 | 第一个正式接手点 | 交接入口 |
|---|---|---|---|
| 统筹（本对话） | 设计取舍、任务切分、契约与交接文件、证据审阅、集成裁决 | 开发期执行器 brief，随后锁定 T00 文档 | 本页 |
| 前端 | C# Win32 虚拟表格与托盘，展示状态、覆盖和评分解释 | T03 桌面基线，T07 接评分结果 | [前端交接](FRONTEND.zh-CN.md) |
| 后端 | 开发期 LangGraph 执行器。本地采集、SQLite、调度和按需 Go 探测 | LangGraph bootstrap，产品接入从 T02 开始 | [后端交接](BACKEND.zh-CN.md) |
| 算法 | 评分、分类、覆盖和异常区间，交付纯规则与可复验用例 | T07 评分，再到 T09 观测解释 | [算法交接](ALGORITHM.zh-CN.md) |
| 测试 | 开发期与产品的独立验证，T01 测量工具与 T11 发行验收 | LangGraph bootstrap 复验，随后 T01 | [测试交接](TESTING.zh-CN.md) |

## 模块之间交什么

后端把带来源、方法、路由和时间的类型化事实交给算法。算法按当前规格产生可空的评分、覆盖、解释和异常区间。前端消费节点列表和轻量展示快照，保留缺测、过期、采集停顿、路径待证和实测失败的区别。测试接收候选 SHA、执行方法与证据，独立复现各模块及组合行为。字段以现有契约为准。 [样本契约](../../contracts/sample.schema.json) [IP 观察契约](../../contracts/ip-observation.schema.json) [测量和评分](../../spec/MEASUREMENTS_AND_SCORING.md)

## 统筹如何发起和接回一次工作

1. 先核对实际 HEAD、任务状态、bootstrap 结论与依赖。产品顺序及必验 ID 取自任务图。模块归属表只负责选定主责和接收方。 [产品任务图](../tasks.json) [流程策略](../orchestrator.policy.json)
2. 用 brief 给唯一候选 owner 写明模块、子任务范围、输入与接收方、base SHA、工作树、可写路径、验收命令和产物。T00 的环境与许可证据由后端回交，测试复核，统筹负责锁定文档。 [派工模板](../BRIEF_TEMPLATE.md)
3. T07 和 T09 的算法部分先交独立工作树中的限范围补丁和复验步骤。算法交回并释放写入后，主责在自己的工作树接收补丁，完成 UI 或探测集成，并提交一个完整候选。T00–T07 串行派写入，T09 的这两次交接也按顺序执行。单个模块补丁作为候选的输入，完成结论绑定组合后的 SHA。 [模块归属表](../module-ownership.json)
4. 测试对候选和集成 SHA 分别核验当前任务全部必需检查。测试角色自己实现 T01/T11 时，另派独立 Sol 验收会话和工作树。FAIL 退回当前候选 owner，由统筹按影响定位到模块。跨模块返工沿用原任务的 cycle 和累计计数。超出范围、阻塞或达到修复上限时，把证据和下一动作写入检查点。 [回执交接](../HANDOFF.zh-CN.md) [Breakout 规则](../BREAKOUT.zh-CN.md) [检查点模板](../CHECKPOINT_TEMPLATE.md)

## 2026-11-07 交接记录

四个 Sol 子对话已完成首轮只读分工梳理。`/root/frontend` 提供 T03 展示输入和 UI 证据清单。`/root/backend` 提供 bootstrap 与本机采集的范围、输入和产物。`/root/algorithm` 提出样本轮次和地址族的契约交接项。`/root/testing` 核对独立复验、T01/T11 隔离以及 bootstrap 报告路径。后续接手以各角色 brief 和实际派工为准。

当前仓库无初始 Git SHA，产品任务均为 pending。下一次正式派工先取得真实 Git 基线，再由后端接手 LangGraph bootstrap，测试独立验收。产品 T00 从 bootstrap 通过后启动。 [执行器启动关口](../LANGGRAPH.zh-CN.md)
