# JSON与SQLite：只保存有意义的事实
## JSON的角色
宿主HTTP响应与Go的stdin/stdout传输；不是每次原样写进数据库。内部sample采用contracts/sample.schema.json；IP另有ip-observation.schema.json。未知内部字段拒绝，宿主新增字段可跳过；拒绝NaN/Infinity、重复键、超深嵌套、超长字符串、多个顶层JSON、破损UTF-8。
源timestamp与collected_at分开；时间UTC毫秒int64，耗时非负整数或null；status枚举；端口uint16校验；IP存SQLite BLOB4/16；原生零延迟按版本编码为unknown_native_failure/ambiguous，不能当最快成功。
JSONL单结果帧16KiB、控制/配置帧256KiB，上限在读取前执行；stdout只协议，stderr脱敏有界。配置帧含凭证仅内存传递，不能写正常日志。

## SQLite
C#单写入者，参数化SQL，schema迁移版本化。优先Microsoft.Data.Sqlite的AOT兼容锁定版本；运行依赖是否达内存目标实测，不先加ORM。schema.sql的STRICT表需要SQLite>=3.37；锁定并测试实际打包native库，不只看NuGet版本。
单连接/小缓存；新库先auto_vacuum=INCREMENTAL，WAL，cache_size=-2048（缓存建议值不是总内存硬上限）、mmap_size=0、busy_timeout=1000、foreign_keys=ON、synchronous=NORMAL（明确掉电可能丢最近事务）。不默认把所有读结果加载到内存。

## 保留与空间预算（设计参数，可在界面改）
原始测量7天；小时汇总90天；每日汇总365天；IP原始观察30天、变化事件180天；应用诊断日志轮转2MiB×2。总数据库文件+WAL目标≤128MiB。
每天空闲分批DELETE≤500行，事务时间≤50ms目标；先完成汇总/删除水位再删除。异常重试backoff，不每次启动全表VACUUM。WAL在空闲checkpoint；读事务占用时不无限等待；自动回收执行有限页incremental_vacuum。DELETE不等于物理文件立刻缩小，VACUUM也不能随手承诺无阻塞。
容量到阈值：先删过期，再删最老已汇总原始行；若仍不足，暂停主动探测并报storage_pressure，不吞错误、不无限长队列。不能删还在调查的未闭合事件或让清理破坏外键。

## 去重与历史汇总
dedup_key=源实例/epoch+稳定节点id/revision+目标/方法+源时间+值的规范摘要。同一历史经多轮轮询只能入库一次；/proxies与provider同一事件要映射再去重。保存持久watermark；清理后的过期历史不可再被轮询重新插回。
小时汇总保留n/成功数、耗时和、均值/M2等足够统计量与方法版本。**不能把小时均值再平均冒充所有样本均值，不能从小时p50/p95计算日p50/p95。** v1评分用可正确合并的计数和均值/方差；精确分位数只在原始样本还在时算，否则标不可用。
IP变化保留前后观察id与覆盖段；宿主关闭开新epoch，不清空全部历史库。主库不保存订阅URL、密码、完整原始JSON、连接域名明细、HTML正文。
