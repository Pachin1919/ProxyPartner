# 2026-11-06 开发准备记录

## 范围
用户要求 Sol 指挥、workflow loop、LangGraph 及预先准备设计/技术路径/交接/验收/breakout。本轮交付设计与项目配置；不把此轮计为任何产品 T00–T11 完成。

## 新增与更新
- 新增 spec/DESIGN.zh-CN.md、workflow/LANGGRAPH.zh-CN.md、HANDOFF.zh-CN.md、BREAKOUT.zh-CN.md 和 orchestrator.policy.json。
- 统一 README、AGENTS、CODEX_START、WORKFLOW、决策记录、概览、验收与交接模板的入口及边界。
- 新增项目 .codex/config.toml，更新示例和5个项目角色的 model=gpt-6.1-sol；loop 技能指向新规则。没有修改用户全局配置。
- 产品预算、评分夹具、任务依赖、required_checks 和 state 未修改。SHA256SUMS.txt、VALIDATION.md、validation/ 原打包记录保持历史含义，不作为新工作区的完整性证明。

## 本轮检查
PowerShell 中实际读取 JSON 并检查 model/fallback、policy 与 tasks 的并发/同因修复上限、8个 bootstrap ID、12项产品任务仍 pending；检查6个新增设计/配置入口存在；检查5个角色 model 及项目 default_subagent_model；退出码0。补查每 cycle 6候选、任务一次自主replan/12验证轮及8检查ID的场景映射，退出码0。输出保存 runs/preparation-2026-11-06/static-checks.txt。此为静态一致性检查，不是 LangGraph 运行测试或完整 TOML 解析。

git diff --check 退出0，但仓库尚无提交且文件未跟踪，因此该命令不覆盖未跟踪文件，不作为完整差异审查证据。文档独立审查另记在下节。

## 审查
独立 Sol verifier（review_workflow_design）已完成工作文件只读设计审查，复核后无剩余重大文档问题。根据审查修正了首轮并发例外、集成回执缺全部检查的歧义、跨cycle重置上限、集成BLOCKED与硬上限路由，以及最后允许轮次的PASS落地。verifier 未修改文件或访问宿主/网络。

当前仓库没有 candidate SHA/初始提交，不能称为 exact-SHA 独立验收。不会用虚拟 SHA 或同一会话的伪造 run ID 产生产品 PASS 回执。最终本轮文件快照 hashes 保存 runs/preparation-2026-11-06/files.sha256，供当前工作区对照，不替代提交身份或运行证据。

## NOT_RUN 与下一步
- LangGraph/Python 运行时安装与依赖锁：NOT_RUN。
- 图路由、持久恢复、dispatch adapter、实际 owner/verifier 交接闭环的8项 bootstrap：NOT_RUN，状态 WORKFLOW_NOT_READY。
- 完整 Codex 项目配置加载及运行策略约束：NOT_RUN；文件设置不代表不可被主会话错误调用绕过，实际模型须在派工时核对。
- 产品构建、真实宿主、UI/交互、内存、实网路径、24h 与作者验收：NOT_RUN。
- GitHub 验证按用户报告为已完成，本轮未重验；未创建提交/远程仓库或推送。

下一动作：核对 Git 提交身份并建立初始基线；按 workflow/LANGGRAPH.zh-CN.md 先实现/验证离线本地执行器，再接一个 Sol 交接实测。bootstrap 通过后才启动产品 T00，不提前更新产品完成状态。
