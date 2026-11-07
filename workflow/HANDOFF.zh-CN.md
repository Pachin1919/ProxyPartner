# 任务交接规则

角色统一使用 gpt-6.1-sol。协调者负责拆解、设计取舍和集成；owner 实现；verifier 复现与裁决。独立性来自会话、工作树和证据，不能称为跨模型审查。

## 派工入口
先从模块分工表定位接收方、唯一候选 owner 和跨模块补丁顺序。写入任务 brief 后再分配实际工作树和权限。 [模块分工与统筹交接](handoff/README.md)

协调者先确认任务依赖、授权范围及实际工作区状态，填写 BRIEF_TEMPLATE.md。没有真实 base SHA 时先完成初始基线提交，不编造 SHA；提交身份必须是用户确认姓名与 GitHub 已验证邮箱。用户报告 GitHub 已验证可作为已知前提，但实际 Git 配置仍在提交前核对。

每个任务分配一个 owner/worktree；首次闭环串行。允许的路径取 tasks.json 的 scope，必要范围扩展由协调者记录在 brief。协调者独占 tasks/state、策略和集成分支写入，owner 不自行改任务依赖或扩大范围。

## owner → verifier
交付一个可复验包，至少包含：
- task/run ID、真实 model ID、base/candidate SHA、分支和 worktree、实际修改路径。
- 改了什么、为何这样做、上游 ref/blob/许可证及复制适配范围。
- 检查 ID 对应的命令、工作目录、开始/结束 UTC、退出码、原始日志和 SHA256；NOT_RUN 原因。
- 二进制/发布参数、OS/SDK/负载；当前任务涉及的内存和无干扰证据。
- UI 任务的真实截图/操作步骤；已知限制、失败根因、下一步。

证据在协调根目录 runs/<task>/<run>/ 中按运行隔离。verifier 在自己的 worktree 执行，再导出脱敏的日志/产物并核对导出前后 hash；receipt 使用相对协调根目录的 portable slash 路径，与 tools/flow 现有路径规则一致。不得依赖另一个会话“应该知道”的上下文。

## verifier → owner / coordinator
verifier 检出 candidate SHA 并先确认工作树干净。按 required_checks 真实运行；审阅差异、范围和副作用。验收未实现的产品不允许用资料包测试顶替。

PASS：全部必需检查在当前 SHA 有证据，向协调者交 receipt。
FAIL：给出最小复现、失败检查、预期/实际、证据路径和 failure signature，退回原 owner；verifier 不偷偷改产品代码。
BLOCKED：明确缺的是权限、环境、真实线路还是产品决定；不把未运行填成通过。schema 的 passed 只有布尔值，NOT_RUN 的详细状态写在附属报告中；BLOCKED 回执不能通过 flow 的 PASS 门禁。

回执沿用 contracts/verification-receipt.schema.json，不为本次设计工作另造不兼容格式。实现者与验收者 run ID 必须不同，且确有独立会话/worktree。无子代理能力时串行第二轮审查并披露限制，不伪造独立身份来通过门禁。

## 集成与完成
协调者审 diff 和 receipt → 本地集成 → 记录 integration SHA → 在该 SHA 运行该任务全部 required_checks 并出集成回执 → 更新 state 为 done。受影响的相邻任务再做适当回归；仅相关子集不能替代本任务完整回执。candidate 回执与集成回执都保留；state 的 head_sha/receipt_path 指向集成结论。新 SHA 的旧回执只能作历史，不能继续作为当前 PASS。

证据不进被测提交以避免“把 SHA 写进自身”的循环；本机原始日志保留在 runs/，需要可移交时由协调者单独导出并保留 hash。远程 push、PR、release 仍按授权执行。

## 会话或人员交接
填写 CHECKPOINT_TEMPLATE.md，至少给出实际 HEAD、任务状态、最后成功/失败命令、artifact hash、未完成检查、拥有的 PID 和下一条动作。接手者先核对磁盘/进程/receipt 再恢复；禁止盲目重跑已执行的写操作或杀宿主进程。
