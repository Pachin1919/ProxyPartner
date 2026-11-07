# Codex的工作图与验证循环
2026-11-06 更新：由 gpt-6.1-sol 主会话指挥，owner/verifier 同样使用 Sol，无 Astra 自动 fallback。按用户新决定引入开发期 Python LangGraph；产品运行时保持 C#/SQLite/按需 Go。workflow/tasks.json 是唯一产品依赖 DAG；loop 是真实执行、独立验证、有限修复与当前提交复验。

设计入口：spec/DESIGN.zh-CN.md；执行器设计及启动验收：workflow/LANGGRAPH.zh-CN.md；交接：workflow/HANDOFF.zh-CN.md；退出/重新规划：workflow/BREAKOUT.zh-CN.md；机器可读策略：workflow/orchestrator.policy.json。LangGraph 当前为设计阶段，尚无已运行执行器。

## 分工与节奏
统筹会话维护交接文档、模块契约与验收记录。前端、后端、算法和测试的接手包及单一候选 owner 按模块分工表派发。 [模块分工与统筹交接](workflow/handoff/README.md)

任务图的角色是技术职责。后端子对话按任务承担 collector、data 和 probe，前端承担 desktop，算法按专用 brief 接 data 的解释规则切片，测试承担 verifier。统筹分配范围、审阅证据并更新 state/checkpoint。
T01/T11中verifier角色可作为验收工具/发行证据任务的owner，但不得自验自批：另开独立verifier会话审核它的当前SHA。其他任务中verifier不改产品代码，发现问题交还owner。
T00~T07 按第一条完整闭环串行跑，每次仅一名 writer；T01/T02 本轮也不并行写入。T07 后只有依赖已满足、路径不重叠且有实际收益时才允许最多两名 writer+一名 verifier；当前 DAG 若仅一个任务 ready，就不为凑并发拆开写。无子代理时串行第二次审查，明确不是跨模型独立性。
先读当前 task/必要 spec/对应源码，不每次把全部报告塞给每个 agent。修复中的本地反馈可只跑相关回归；正式 candidate 和 integration 回执必须各自覆盖该任务全部 required_checks，性能/接口/存储改动再补对应受影响门禁。

## 实际loop
1. `git status/log`核对事实，读取AGENTS、tasks、state、最新checkpoint；运行flow validate/ready。
2. ready只由依赖done计算。给唯一owner独立worktree，写task brief：base SHA、允许文件、目标、禁止操作、验收命令、上游证据。
3. 实现最小可运行改动，相关测试和性能检查；未过不扩功能。owner提交候选SHA，列diff、证据、失败项。
4. verifier在干净worktree检出候选SHA，运行约定检查；不得以owner截图/“全绿”摘要代替验证。
5. FAIL→原owner修复→新SHA重验。初次失败后同根因最多两次修复；仍失败转 replan。每任务 cycle 最多6个候选验证；具体计数和出口见 workflow/BREAKOUT.zh-CN.md。
6. PASS→协调者读diff和回执→本地集成到项目分支→集成SHA回归→done→下一ready任务。没有远程授权不push/PR/merge GitHub。
7. 会话内可继续ready任务，不把“是否继续”交给用户；真正的产品决策/权限/不可逆操作/预算耗尽才停止。停止写checkpoint，不承诺离线自动继续。

## 状态与真实性
产品状态沿用 pending→running→implemented→verifying→verified→merge_ready→done；失败 needs_fix；环境/权限 blocked；会话暂停 paused。LangGraph 的 replan/reconcile/waiting_input/checkpointed 是调度阶段，不直接写成 state.json 的新 status；暂停时投影 paused，缺前提时投影 blocked，未验收仍保持 needs_fix。
一条验收只覆盖一个head。修改测试数据、权重或预算以让现有实现通过是违约。flow校验图、回执、SHA和文件哈希，**不能证明日志真实，也不是自动代理调度器**。
Linux单元/fixture测试不能代替Windows命名管道、真实UI、24h soak、真实出站IP。不同run id也不能单独证明独立性；需保存实际会话/worktree/命令证据。

## 性能hillclimb
冻结负载/预算→baseline→单一热点假设→最小改动→baseline/head交错至少3轮→正确性不退化+噪声外改善→保留。首次功能无旧基线则用绝对门槛，不编造百分比。每轮必须报告主进程与worker合计。

## 恢复
正常会话切换前 CHECKPOINT 记录 task、branch/worktree、head/base、最后失败命令、证据路径、下一动作、拥有的 PID。收到用户停止指令立即停止，不为补检查点继续工具操作。恢复先比对磁盘 Git 与进程，不能直接信旧报告。禁止 git reset --hard/clean -fd 破坏用户工作，禁止杀宿主 mihomo。

## Codex接法
项目自定义 agent TOML 固定 model=gpt-6.1-sol；协调者启动前也确认当前模型。模型不可用就报告阻塞，不自动切 Astra。模型字段依据 OpenAI Docs 的 subagents 文档核对；当前宿主实际加载和交接仍需实测。config.example.toml 仅供项目合并参考，不覆盖全局配置/权限。

先完成 workflow/LANGGRAPH.zh-CN.md 的启动验收，再启动产品任务循环。执行器通过不等于产品 T00 完成；GitHub 登录验证也不等于 Windows/AOT/宿主/内存通过。

## 本地工具
在tools/flow目录：`go run . validate --root ../..`；`go run . ready --root ../..`。
验收：`go run . check --root ../.. --task T03 --receipt runs/T03/receipt.json --head <git-rev-parse-HEAD>`。
该Go工具仅开发阶段运行，绝不随50MB以下产品常驻。

来源：research/SOURCE_AUDIT.md SRC11、PRIOR12，以及DEPENDENCIES.md中的Codex官方说明。
