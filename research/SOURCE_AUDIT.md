# 竞品SRC核查与复用清单

**不是README功能表。** 本包记录11个本轮重新读取的源文件，以及13个前轮已读源码条目。后者明确标记 prior_turn_source_read，不冒充本轮全量重审。可变分支的内容用已返回Git blob SHA锁定；未有SHA的条目在复制/移植前必须重新读取。Git blob SHA不是commit SHA，不可把它直接拼成raw的commit地址。

## 功能边界结论

| 项目 | 已有能力 | 不得夸大的部分 | Lite采用 |
|---|---|---|---|
| MetaCubeXD | 指定URL测试、基础评分、短历史、实时监控 | 当前推荐历史未按target建模；网页/桌面版并不证明低内存 | 小算法和接口语义，不搬UI/自动切换 |
| Zashboard | 分组目标URL、内核目标历史、流量历史、后端smart排名 | flow IP不是出口变化；smart排名不等于本地网站权重 | 节流、有界缓存、读历史 |
| YACD-meta | 节点/组/provider读取测速，自定义URL | 未在已读实现确认长期IP/网站加权 | 简单API分层和批次更新 |
| Neko Master | Go采集、流量差值、持久化聚合 | 是监控服务架构，不是原生50MB工具 | baseline、增量、过期淘汰 |

没有证实某功能≠证明整个项目绝不存在该功能；引用限定到已读路径。不要声称市场空白。

## 逐源文件审查

### SRC01 · clash-verge-rev/clash-verge-rev

- 文件：`src-tauri/src/utils/dirs.rs`；ref：`v2.4.7`；核查层级：`this_delivery`。
- 已取得的Git blob SHA：`2e986e1a3cba37d378a60b5bd4ce189b01d3bbdb`。
- 本轮实际读取行范围：`full file`；不是全仓审计。
- 读取的符号：`ipc_path; app_home_dir; init_portable_flag`。
- 源码事实：Windows命名管道路径、标准/便携配置目录组成。
- 复用/排除决定：只读发现规则；不复制备份/密钥生成等无关功能。路径要核对实际安装。
- [源文件](https://github.com/clash-verge-rev/clash-verge-rev/blob/v2.4.7/src-tauri/src/utils/dirs.rs)

### SRC02 · MetaCubeX/mihomo

- 文件：`hub/route/server.go`；ref：`v1.19.21`；核查层级：`this_delivery`。
- 已取得的Git blob SHA：`4e0d0c93f5f371c6e915a71f82e56cfe111d8e41`。
- 本轮实际读取行范围：`1-360`；不是全仓审计。
- 读取的符号：`router; startPipe; start; authentication`。
- 源码事实：pipe路由传空Secret；TCP路由使用cfg.Secret；同一控制API包含读写操作。
- 复用/排除决定：验证服务器身份和Windows权限；本机pipe不是远控面板。实现白名单客户端而不是代理全部API。
- [源文件](https://github.com/MetaCubeX/mihomo/blob/v1.19.21/hub/route/server.go)

### SRC03 · MetaCubeX/mihomo

- 文件：`adapter/adapter.go`；ref：`v1.19.21`；核查层级：`this_delivery`。
- 已取得的Git blob SHA：`6f20c08abb43eb0ddef4f2ccf989ab0254e7f5ed`。
- 本轮实际读取行范围：`1-290`；不是全仓审计。
- 读取的符号：`MarshalJSON; URLTest; ExtraDelayHistories; defaultHistoriesNum`。
- 源码事实：history/extra/alive导出；历史队列10条；URLTest deferred更新健康状态；unifiedDelay第二次请求口径。
- 复用/排除决定：复用原生历史但去重；目标未知不瞎填；GET测速不是纯读，不能默认打live内核。
- [源文件](https://github.com/MetaCubeX/mihomo/blob/v1.19.21/adapter/adapter.go)

### SRC04 · MetaCubeX/metacubexd

- 文件：`packages/ui/stores/nodeRecommendation.ts`；ref：`v1.274.3`；核查层级：`this_delivery`。
- 已取得的Git blob SHA：`1caf324a47ac566945c6089cd9dbac7ac1740df4`。
- 本轮实际读取行范围：`full file`；不是全仓审计。
- 读取的符号：`NodePerformanceEntry; recordTestResult; saveToStorage`。
- 源码事实：history最多20项；按nodeName存timestamp/latency/success，没有target字段。
- 复用/排除决定：这是基础推荐，不等于跨网站权重；新表按node×target×method保存。不要移植localStorage整表序列化。
- [源文件](https://github.com/MetaCubeX/metacubexd/blob/v1.274.3/packages/ui/stores/nodeRecommendation.ts)

### SRC05 · MetaCubeX/metacubexd

- 文件：`packages/ui/stores/connections.ts`；ref：`v1.274.3`；核查层级：`this_delivery`。
- 已取得的Git blob SHA：`dbaa4f1963edee9d507fae230bf615f73b311dd6`。
- 本轮实际读取行范围：`1-315`；不是全仓审计。
- 读取的符号：`updateFromWsMsg; updateDataUsage; scheduleFlush; clearDataUsage`。
- 源码事实：连接全快照遍历、差值、历史合并与30秒写入；paused不阻止此前后台计算；计数下降进入重置路径。
- 复用/排除决定：不用全量连接统计作为常驻基础。借鉴baseline/delta；不要照搬源重启时clearAll历史。源码成本不等于用户机器已定位瓶颈。
- [源文件](https://github.com/MetaCubeX/metacubexd/blob/v1.274.3/packages/ui/stores/connections.ts)

### SRC06 · MetaCubeX/metacubexd

- 文件：`packages/ui/components/RealtimeLineChart.vue`；ref：`v1.274.3`；核查层级：`this_delivery`。
- 已取得的Git blob SHA：`0ac388171e5fe7e7a41943aa4da4813fbcfaa640`。
- 本轮实际读取行范围：`48-226`；不是全仓审计。
- 读取的符号：`createChartOptions; chartRef.addPoint; chartRef.addPoints`。
- 源码事实：Highcharts areaspline；新增点/重绘可带800ms动画。
- 复用/排除决定：Lite不移植图表；仅在可见时画有界历史，无连续动画。不能凭这里算出节省多少MB。
- [源文件](https://github.com/MetaCubeX/metacubexd/blob/v1.274.3/packages/ui/components/RealtimeLineChart.vue)

### SRC07 · Zephyruso/zashboard

- 文件：`src/store/conn-history.ts`；ref：`main`；核查层级：`this_delivery`。
- 已取得的Git blob SHA：`2ac30629f8b23af8478511d7840382c4e79c1c17`。
- 本轮实际读取行范围：`1-185`；不是全仓审计。
- 读取的符号：`refreshView; trimMap; flush; accumulate`。
- 源码事实：视图5秒、写入30秒、聚合map阈值2000裁到1500；类型为来源/目标/进程/出站/组。
- 复用/排除决定：借鉴采集与刷新分离、有界map；这是连接流量历史，不是每节点公网出口稳定性。
- [源文件](https://github.com/Zephyruso/zashboard/blob/main/src/store/conn-history.ts)

### SRC08 · MetaCubeX/Yacd-meta

- 文件：`src/api/proxies.ts`；ref：`master`；核查层级：`this_delivery`。
- 已取得的Git blob SHA：`4588d36cbbf52faaa83f37ff9ddd82e55a917e60`。
- 本轮实际读取行范围：`full file`；不是全仓审计。
- 读取的符号：`requestDelayForProxy; requestDelayForProxyGroup; fetchProviderProxies; requestToSwitchProxy`。
- 源码事实：指定URL节点测试、组测试、provider读取和切换API各自封装。
- 复用/排除决定：借鉴读取/探测区分；排除PUT/DELETE/healthcheck。fetch失败回空对象不适合Lite：应区分空节点和源失联。
- [源文件](https://github.com/MetaCubeX/Yacd-meta/blob/master/src/api/proxies.ts)

### SRC09 · faceair/clash-speedtest

- 文件：`speedtester/speedtester.go`；ref：`main`；核查层级：`this_delivery`。
- 已取得的Git blob SHA：`972dc9d2ef71056be719c2ccb849730e9b024b59`。
- 本轮实际读取行范围：`1-280;310-850`；不是全仓审计。
- 读取的符号：`LoadProxies; createClient; testLatency; calculateLatencyStats; testDownload`。
- 源码事实：自定义http.Transport.DialContext→指定Proxy；六次HEAD失败率叫packetLoss；io.Copy读体错误被忽略；读取配置可联网。
- 复用/排除决定：仅复用指定dialer结构。禁止复制订阅抓取、按server:port去重、明文ProxyConfig输出、无限读体、HTTP失败冒充丢包。
- [源文件](https://github.com/faceair/clash-speedtest/blob/main/speedtester/speedtester.go)

### SRC10 · foru17/neko-master

- 文件：`apps/agent/internal/agent/runner.go`；ref：`main`；核查层级：`this_delivery`。
- 已取得的Git blob SHA：`42494cca3ced23fd7153ebb778972e7a39f08c0a`。
- 本轮实际读取行范围：`390-575`；不是全仓审计。
- 读取的符号：`ingestSnapshots; runPolicyStateSyncLoop`。
- 源码事实：旧连接先建立baseline，后续差值；状态同步30秒；流过期淘汰。
- 复用/排除决定：借鉴baseline/计数回退/淘汰；不复制远程报告服务或常驻逐连接采集。IP字段属flow元数据，不能当节点exit IP。
- [源文件](https://github.com/foru17/neko-master/blob/main/apps/agent/internal/agent/runner.go)

### SRC11 · cursor/plugins

- 文件：`pstack/skills/poteto-mode/playbooks/orchestrate.md`；ref：`main`；核查层级：`this_delivery`。
- 已取得的Git blob SHA：`b6e5f5661a1e6b4778845de53f331cee3486905b`。
- 本轮实际读取行范围：`full file`；不是全仓审计。
- 读取的符号：`Topology; Supervision`。
- 源码事实：单分支owner、独立verifier、从一个单元起步、验收绑定head、按原因有限重试。
- 复用/排除决定：迁移方法而非Cursor Task/云VM/大型编排器。Lite首轮串行，后续最多两writer。
- [源文件](https://github.com/cursor/plugins/blob/main/pstack/skills/poteto-mode/playbooks/orchestrate.md)

### PRIOR01 · MetaCubeX/metacubexd

- 文件：`packages/ui/utils/nodeScoring.ts`；ref：`v1.274.3`；核查层级：`prior_turn_source_read`。
- 已取得的Git blob SHA：`未取得，实施前补核`。
- 读取的符号：`calculateNodeScore; calculateStabilityScore`。
- 源码事实：延迟/变异系数/成功率50/30/20加权。
- 复用/排除决定：可移植小型统计方法，但不宣称它已有OpenAI目标权重。引入代码前重取blob及LICENSE。
- [源文件](https://github.com/MetaCubeX/metacubexd/blob/v1.274.3/packages/ui/utils/nodeScoring.ts)

### PRIOR02 · MetaCubeX/metacubexd

- 文件：`packages/ui/composables/useWebSocket.ts`；ref：`v1.274.3`；核查层级：`prior_turn_source_read`。
- 已取得的Git blob SHA：`未取得，实施前补核`。
- 读取的符号：`connect; createWebSocket; createLogsWebSocket`。
- 源码事实：全局connections/traffic/memory/logs连接与JSON解析。
- 复用/排除决定：不把四路常驻通道原样搬来。
- [源文件](https://github.com/MetaCubeX/metacubexd/blob/v1.274.3/packages/ui/composables/useWebSocket.ts)

### PRIOR03 · MetaCubeX/metacubexd

- 文件：`packages/ui/pages/proxies.vue`；ref：`v1.274.3`；核查层级：`prior_turn_source_read`。
- 已取得的Git blob SHA：`未取得，实施前补核`。
- 读取的符号：`autoSwitchAfterTest; PROXIES_INITIAL_RENDER_COUNT`。
- 源码事实：自动切换确实会改策略组；每组渐进加载50行。
- 复用/排除决定：不移植自动切换；采用单张虚拟化节点表。
- [源文件](https://github.com/MetaCubeX/metacubexd/blob/v1.274.3/packages/ui/pages/proxies.vue)

### PRIOR04 · MetaCubeX/metacubexd

- 文件：`packages/ui/composables/useReachabilityBoard.ts`；ref：`v1.274.3`；核查层级：`prior_turn_source_read`。
- 已取得的Git blob SHA：`未取得，实施前补核`。
- 读取的符号：`probeTarget; testTargetsThroughNode`。
- 源码事实：按节点+URL调delay；预设多目标可达性，非完整解锁判断。
- 复用/排除决定：作为功能对照。不能把此功能等同长期多目标加权。
- [源文件](https://github.com/MetaCubeX/metacubexd/blob/v1.274.3/packages/ui/composables/useReachabilityBoard.ts)

### PRIOR05 · Zephyruso/zashboard

- 文件：`src/assembly/proxies/latency.ts`；ref：`main`；核查层级：`prior_turn_source_read`。
- 已取得的Git blob SHA：`未取得，实施前补核`。
- 读取的符号：`testNodeLatency; latencyTestForSingle`。
- 源码事实：节点与provider分别测速，支持URL，p-limit限制并发。
- 复用/排除决定：复用目标/节点能力划分；原测试更新内核状态，不默认采用。
- [源文件](https://github.com/Zephyruso/zashboard/blob/main/src/assembly/proxies/latency.ts)

### PRIOR06 · Zephyruso/zashboard

- 文件：`src/assembly/proxies/state.ts`；ref：`main`；核查层级：`prior_turn_source_read`。
- 已取得的Git blob SHA：`未取得，实施前补核`。
- 读取的符号：`getTestUrl; readHistory`。
- 源码事实：分组自定义URL与extra[testUrl].history。
- 复用/排除决定：解释为什么有按目标历史但不是网站权重算法。
- [源文件](https://github.com/Zephyruso/zashboard/blob/main/src/assembly/proxies/state.ts)

### PRIOR07 · Zephyruso/zashboard

- 文件：`src/assembly/proxies/smart.ts`；ref：`main`；核查层级：`prior_turn_source_read`。
- 已取得的Git blob SHA：`未取得，实施前补核`。
- 读取的符号：`initSmartWeights; restructWeights`。
- 源码事实：读取后端smart权重排名。
- 复用/排除决定：后端smart排名不等于面板本身多网站评分。
- [源文件](https://github.com/Zephyruso/zashboard/blob/main/src/assembly/proxies/smart.ts)

### PRIOR08 · Zephyruso/zashboard

- 文件：`src/api/latency.ts`；ref：`main`；核查层级：`prior_turn_source_read`。
- 已取得的Git blob SHA：`未取得，实施前补核`。
- 读取的符号：`getLatencyFromUrlAPI`。
- 源码事实：浏览器载入favicon计时。
- 复用/排除决定：只能反映当前浏览器路径，不能当所有节点独立探测。
- [源文件](https://github.com/Zephyruso/zashboard/blob/main/src/api/latency.ts)

### PRIOR09 · MetaCubeX/Yacd-meta

- 文件：`src/store/proxies.ts`；ref：`master`；核查层级：`prior_turn_source_read`。
- 已取得的Git blob SHA：`未取得，实施前补核`。
- 读取的符号：`setDelayPatch`。
- 源码事实：测速更新100ms合并。
- 复用/排除决定：可借鉴批次更新，不复制JS状态栈。
- [源文件](https://github.com/MetaCubeX/Yacd-meta/blob/master/src/store/proxies.ts)

### PRIOR10 · foru17/neko-master

- 文件：`apps/collector/src/database/repositories/traffic-writer.repository.ts`；ref：`main`；核查层级：`prior_turn_source_read`。
- 已取得的Git blob SHA：`未取得，实施前补核`。
- 读取的符号：`batchUpdateTrafficStats`。
- 源码事实：多维流量UPSERT、分钟/小时聚合。
- 复用/排除决定：不带入Node后端，参考增量落库但只保留Lite必要维度。
- [源文件](https://github.com/foru17/neko-master/blob/main/apps/collector/src/database/repositories/traffic-writer.repository.ts)

### PRIOR11 · clash-verge-rev/clash-verge-rev

- 文件：`src-tauri/src/config/clash.rs`；ref：`v2.4.7`；核查层级：`prior_turn_source_read`。
- 已取得的Git blob SHA：`未取得，实施前补核`。
- 读取的符号：`guard_external_controller_ipc; get_client_info`。
- 源码事实：生成pipe和TCP控制配置、取得Secret。
- 复用/排除决定：候选自动发现回退，绝不调用patch_config或save_config。
- [源文件](https://github.com/clash-verge-rev/clash-verge-rev/blob/v2.4.7/src-tauri/src/config/clash.rs)

### PRIOR12 · cursor/plugins

- 文件：`pstack/skills/poteto-mode/playbooks/multi-phase-plan.md`；ref：`main`；核查层级：`prior_turn_source_read`。
- 已取得的Git blob SHA：`7101aa71dbc08f2b84baa227a7bd589f800f13e8`。
- 读取的符号：`plan skeleton; verification`。
- 源码事实：上游计划引用node校验脚本和Cursor /loop。
- 复用/排除决定：只取按证据定义PR/任务，不照搬Node或十路swarm。
- [源文件](https://github.com/cursor/plugins/blob/main/pstack/skills/poteto-mode/playbooks/multi-phase-plan.md)

### PRIOR13 · 2dust/v2rayN

- 文件：`v2rayN/ServiceLib/Services/SpeedtestService.cs`；ref：`master`；核查层级：`prior_turn_source_read`。
- 已取得的Git blob SHA：`未取得，实施前补核`。
- 读取的符号：`SpeedtestService`。
- 源码事实：生命周期、取消与独立内核/SOCKS测速结构。
- 复用/排除决定：仅参考取消/清理；本版不走新代理端口，也不兼容Xray。
- [源文件](https://github.com/2dust/v2rayN/blob/master/v2rayN/ServiceLib/Services/SpeedtestService.cs)

## 性能研究边界
MetaCubeXD的全快照统计、全局数据通道、动画和卡片数量是可检查的成本来源；没有实际Windows profiler，不能写“已确认是某函数造成用户卡顿”。`shallowRef`、有界历史、批处理已说明上游也在优化。Lite是收缩工作量/数据范围/渲染成本，不是认为所有网页代码都必然差。

## 引入源码前
每项复用记录来源路径、ref、blob/commit、许可证、复制/改写范围、测试、维护状态。默认不新增零采用或停更核心依赖。上游整包不进入本ZIP；仅提供指向SRC的审查记录，不混入敏感用户订阅。
采用MIT片段应保留版权/许可；Mihomo/faceair等实际采用版本需核实GPL及对应源码义务。用户接受开源，不能因此省略许可证。不得读取Mihomo错误分支后把内核误标MIT。见DEPENDENCIES.md。
