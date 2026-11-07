# 验收清单
- AUTO：普通用户自动读Verge2.4.7管道，身份确认；回退只读已授权本机配置；多实例选择正确。无订阅输入、无自动订阅下载。
- READ：请求日志仅白名单，无PUT/PATCH/DELETE，无live delay/healthcheck、restart、close connection；宿主配置前后hash不变。用户/内核自己的自动变动不能误算成我们的写操作。
- DATA：重复源历史不重复计数；同名不同provider不合并；native未知目标不伪装OpenAI；null/zero/status和schema非法输入正确处理。
- STORE：重启保留数据；迁移/清理/磁盘满/重复插入测试；小时汇总正确合并；源重启不删整个历史；不落盘凭证。
- PROBE：指定节点确实被传入dialer；至少两条真实授权线路，服务端或可验证日志证明路径；同出口节点也允许。无新listener，无选路写入；取消后3秒内退出；全部过程计入40MiB。
- IP：入口/目标/出口不混淆；两echo/两地址族隔离；长空档不当连续稳定；跳IP频率用覆盖时间；无数据不满分。
- SCORE：按target独立样本和可调权重；关键服务失败cap生效；403不是“解锁”，401不是推理已验证；无自动切换。
- UI：100/1000节点滚动筛选，中文、DPI125/150/200%、键盘、托盘重开、无持续动画；红绿之外有文字。
- PERF：PERFORMANCE.md所有状态，PrivateBytes与子进程、CPU、24h soak；采样日志+生命周期峰值。低于目标前不追加功能。
- SHIP：干净Windows无Node/Go/.NET SDK运行；无管理员、服务、驱动、开机启动默认写入；zip路径可含中文；有来源、许可证、对应源码、SHA256。

预览版只通过AUTO/READ/DATA/UI部分不得标正式完成；无法运行Windows/实网则BLOCKED，不以Linuxfixture替代。

## 开发流程与验收证据（2026-11-06）
LangGraph 执行器须另过 workflow/LANGGRAPH.zh-CN.md 的8项启动检查，不能用产品 fixture 或 flow validate 顶替。模型只用 gpt-6.1-sol，交接及退出按 workflow/HANDOFF.zh-CN.md 和 workflow/BREAKOUT.zh-CN.md。

每项结论绑定 task、candidate/integration SHA、实际会话/worktree、命令/退出码、日志 hash 和固定负载。各阶段分别报告：技术行为、真实 Windows/网络、资源、视觉/交互、作者审阅；未运行写 NOT_RUN，不能互相代替。必需检查 NOT_RUN 时不出 PASS。

产品初期不存在的行为不得提前要求“已通过”：T03 的 UI 基线用真实虚拟列表+合成负载，T06 才验真实历史闭环；T08 起验 worker 全进程峰值，T11 才验24h。各阶段仍按自身 required_checks 复验，不能把晚期要求无限推迟或冒认已通过。
