# ProxyPartner Lite 开发执行器

本地 Python CLI 使用 LangGraph 和 SQLite 检查点编排开发任务。它读取产品任务图、流程策略和模块归属，输出文件 brief，由统筹在现有 Codex 会话中执行派工、验收和 Git 集成。开发依赖、检查点和日志与产品隔离。执行器不调用模型、启动代理、访问宿主代理、自动提交或发布。

## 安装与离线复验

在候选工作树运行以下 PowerShell 命令。将解释器与协调根目录换成实际位置。锁定的 wheel 适用于 Windows x64 CPython 3.12。初次验证使用 3.12.14。

~~~powershell
$root = 'F:\PachinStudio\ProxyPartner'
$basePython = 'C:\path\to\python.exe'
$venv = "$root\runs\orchestrator\verifier-venv"
$env:TEMP = "$root\runs\orchestrator"
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Force $env:TEMP | Out-Null
& $basePython -B -m venv $venv
$python = "$venv\Scripts\python.exe"
& $python -B -m pip --isolated install --index-url https://pypi.org/simple --only-binary=:all: --require-hashes -r tools/orchestrator/requirements.lock
& $python -B -m pip check
& $python -B -m unittest discover -v -s tools/orchestrator
~~~

如已备齐 wheel，将安装命令的索引选项换成 --no-index --find-links <wheel-directory>。测试会建临时 Git 仓库和工作树，默认放在当前候选工作树的 runs 下，可用 ORCHESTRATOR_TEST_TMP 指定临时目录。测试中的 Git、SQLite、子进程与证据检查真实执行，派工与回执数据由夹具模拟。

| 启动关口 | 复验入口 |
| --- | --- |
| runtime-lock | 新 venv 按锁安装，pip check，运行 suite |
| graph-routing | test_graph_routing_* |
| repair-breakout | test_repair_breakout_* |
| persistent-resume | test_persistent_resume_real_subprocess |
| dispatch-reconcile | test_dispatch_reconcile_owner_and_integration |
| sha-evidence-gate | test_sha_evidence_gate |
| state-projection | test_state_projection_rechecks_receipt_after_process_exit、test_fast_forward_dependency_requires_matching_integration_projection |
| sol-model-policy | test_sol_model_policy_*，另做一次有明确授权的真实 Sol 文件交接 |

真实交接由统筹在隔离的验证仓库中使用以下文件协议，保存本次派工的模型、会话 ID、简报、恢复前后输出和回执。可从本目录用上述 Python 执行以下命令建立隔离夹具，交给真实 Sol 会话的是一次有范围的夹具任务。

~~~powershell
& $python -B -i -c "from test_orchestrator import Bootstrap; f=Bootstrap(); f.setUp(); print(f.root); print(f.manifest); print(f.owner)"
~~~

保持该交互进程打开，在另一个终端把 --root 指向输出的夹具根目录，start 指向输出的清单。实际复验时把夹具清单中的 coordinator_session 改为实际统筹会话 ID。夹具中的 bootstrap 报告和 PASS 仅供此隔离试验。结束时在原交互进程执行 f.doCleanups() 后退出。产品 T00 的授权由统筹在所有启动关口通过后单独给出。

## 统筹文件协议

所有命令使用协调 Git 工作树绝对路径。执行器源码可以位于不同的候选工作树。

~~~powershell
$cli = 'F:\PachinStudio\ProxyPartner\runs\worktrees\langgraph-bootstrap\tools\orchestrator\cli.py'
& $python -B $cli --root $root start "$root\runs\orchestrator\authorization.json"
& $python -B $cli --root $root status T00
& $python -B $cli --root $root resume T00 "$root\runs\orchestrator\event.json"
& $python -B $cli --root $root project T00
~~~

协调者在 authorization.json 中提供以下字段。

- task、run、base_sha、owner_worktree 和 owner_branch。任务须符合现有 DAG 的依赖，工作树须为同一仓库内基于完整 base SHA 的独立工作树。
- allowed_paths 和 acceptance_commands。路径使用仓库相对斜杠路径，目录以 / 结尾。范围须包含在任务 scope 中。每个 required_checks ID 对应一个命令字符串。
- coordinator_session 和 bootstrap_report。报告使用根目录相对斜杠路径，包含模型 model、当前基线的已验收祖先提交 head_sha，以及八项检查 ID 到 PASS 的 checks 对象。该报告由统筹根据独立复验签收，执行器检查其结构、祖先关系及恢复时文件 hash。

每个命令输出当前检查点和交接意图到标准输出及 runs/orchestrator/T00.handoff.json。resume 事件都带 dispatch_id（取 dispatch.id）、revision（取顶层 revision）和 type。每次先读取最新 revision。

| type | 附加字段和操作 |
| --- | --- |
| claim | 无。先持久化声明，确认输出后再由统筹执行动作 |
| attach | session、model。绑定已存在或实际启动的会话，模型必须为 gpt-6.1-sol |
| not_started | confirmed_by。只有统筹实际对账确认从未派工后，才恢复同一派工意图为 ready |
| result，owner 或 integrate | session、sha。读取实际 Git，检查完整提交、基线、分配路径和集成树 |
| result，verifier | session、worktree、receipt、failure。检查独立会话、干净工作树与完整 SHA；receipt 使用协调根相对斜杠路径；failure 为 null 或 [失败检查 ID, 根因分类] |
| result，replan | session、plan、within_scope。plan 为可查证的协调根相对文件路径；within_scope 为布尔值 |
| unblock | confirmed_by。统筹确认前提已满足后申请下一轮验证 |

claim 后中断时，status 显示 reconcile。统筹先按原 dispatch.id 查实际会话或 Git 状态，再 attach 已有结果，或者在确认未启动后提交 not_started。集成要保留候选历史，允许快进或内容树与已验候选相同的本地合并提交。集成复验要求使用新的验收会话，以便即使快进 SHA 相同也能区分两份回执。候选或集成验证失败由原实现路径修复，限额按策略累计。replan 提升逻辑 cycle 并保留原根因和总轮次，在同一 SQLite thread 内恢复整条任务链。

project 由统筹显式执行，原子更新现有 state.json 契约。遇到中断先用 status 核对，随后可重新 project。start 派发依赖任务前，核对上游的 done 投影、SQLite 终态检查点及该检查点记录的集成复验回执。快进集成时也须核对独立的集成回执。当前每次只接受一项活动任务，符合首条闭环的串行约束。所有 CLI 进程共用一个系统锁；统筹操作期间须保持唯一状态写入者。仓库工作树、流程配置或证据不匹配时会停在核对，保留原 SQLite 检查点。创建 runs/orchestrator/STOP 可禁止后续 CLI 调用。上限退出或流程版本不匹配需要统筹裁决，保持原数据库与回执。

所用接口来自 LangGraph 官方的 [Graph API](https://docs.langchain.com/oss/python/langgraph/graph-api)、[持久化](https://docs.langchain.com/oss/python/langgraph/persistence) 和 [Interrupts](https://docs.langchain.com/oss/python/langgraph/interrupts)。应用代码无上游复制。
