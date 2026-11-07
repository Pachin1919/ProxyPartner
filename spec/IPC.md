# Go worker边界
UTF-8 JSONL / stdio；首帧hello含protocol_version、engine_version、capabilities、will_open_listener=false。主程序验证协议再发送单节点配置与目标。id/run_id/sample_id贯穿；消息严格类型，拒未知必需版本。
最小方法：hello、probe.run、probe.cancel、shutdown。结果类型measurement/ip_observation/complete/error；完整参数由T08按contracts实现，不新造通用RPC。
stdout禁止log；stderr每秒限流、总环形日志≤64KiB，无URL token/节点密码；帧上限256KiB配置/16KiB结果。终态按sample_id幂等，主程序落库后ack；UI进度可合并但终态不能无声丢弃。
父进程EOF/Job终止立即取消自己出站；无孤儿；启动、取消、退出回执计入资源验收。错误分transport/config/protocol/resource_budget，不把资源限制当节点不可用。
Go低内存探测未达标时不得由主进程常驻完整内核弥补，也不准切宿主节点再测。保留阻塞报告，缩减实现依赖而不是删功能定义。
