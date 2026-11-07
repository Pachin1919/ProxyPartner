# 任务依赖与验收

机器可读唯一主表：tasks.json。初始全部pending；只有T00可执行。

## T00 环境与契约锁定
依赖：无；owner：coordinator

核对Windows/SDK/AOT/宿主版本、许可证和性能口径，冻结资源预算。

检查ID：environment, license-lock, budget-contract。
写入范围：spec/, workflow/, contracts/, scripts/。

## T01 进程树性能基准与采样
依赖：T00；owner：verifier

建立PrivateBytes/CPU与Job Object峰值验证，先用有子进程夹具证明不漏算。

检查ID：process-tree, short-child-peak, baseline-format。
写入范围：tests/perf/, scripts/, tools/budget/。

## T02 自动只读接入宿主
依赖：T00；owner：collector

优先named pipe；失败才读授权loopback配置；不输入订阅URL。

检查ID：pipe-handshake, fallback-auth, readonly-routes, no-secrets。
写入范围：src/ProxyPartnerLite/Discovery/, src/ProxyPartnerLite/Host/, tests/host/。

## T03 小窗口与内存一票否决
依赖：T01, T02；owner：desktop

C#AOT+Win32虚拟表格与最小Avalonia候选公平比较；只留符合25/35/40预算的一套。

检查ID：aot-build, 100-node-ui, dpi-tray, memory-gate。
写入范围：src/ProxyPartnerLite/Desktop/, tests/ui/, tests/perf/, build/。

## T04 类型与节点身份
依赖：T03；owner：data

强类型JSON、重复源历史、provider节点身份、目标与方法分开。

检查ID：schema-roundtrip, dedup, node-revision, unknown-not-zero。
写入范围：src/ProxyPartnerLite/Domain/, contracts/, tests/domain/, fixtures/。

## T05 SQLite与限额清理
依赖：T04；owner：data

单写入与有界队列、持久去重、事务批次、汇总/保留/空间回收。

检查ID：migration, retention-watermark, disk-pressure, aggregate-merge, memory-gate。
写入范围：src/ProxyPartnerLite/Storage/, storage/, tests/storage/。

## T06 被动采集完整闭环
依赖：T05；owner：collector

真实宿主→去重→SQLite→表格；无活跃连接/睡眠/源断开区别显示。

检查ID：live-passive, sleep-gap, no-live-healthcheck, restart-persistence, memory-gate。
写入范围：src/ProxyPartnerLite/Host/, src/ProxyPartnerLite/Scheduler/, tests/host/, tests/integration/。

## T07 网站权重和解释UI
依赖：T06；owner：desktop

依golden fixtures实现独立target加权/cap/覆盖不足；不自动切代理。

检查ID：score-golden, critical-cap, missing-target, no-autoswitch, memory-gate。
写入范围：src/ProxyPartnerLite/Scoring/, src/ProxyPartnerLite/Desktop/, tests/scoring/, fixtures/。

## T08 Go独立拨号资源验证
依赖：T07；owner：probe

只读宿主最终单节点配置；不重拉订阅；指定节点dialer无监听器，全进程40MiB。

检查ID：no-resubscribe, specified-node, no-listeners, cancel-exit, combined-memory。
写入范围：engine/, tests/probe/, tests/perf/。

## T09 网站探测和IP异常观察
依赖：T08；owner：probe

预算轮转补测、HTTP分类、IP与空档状态机；未验证路径不得混排。

检查ID：http-classification, ip-pairs, incident-bounds, request-budget, tun-path。
写入范围：engine/, src/ProxyPartnerLite/Domain/, src/ProxyPartnerLite/Scheduler/, tests/probe/, tests/domain/。

## T10 整合真实评分与收缩复核
依赖：T09；owner：collector

真实测量进入用途排名，清理/重启/休眠后结果一致，无新增管理功能。

检查ID：end-to-end, readonly-regression, 1000-nodes, combined-memory。
写入范围：src/ProxyPartnerLite/, tests/integration/, tests/perf/。

## T11 24小时与便携发行
依赖：T10；owner：verifier

原生Release24h、干净Windows启动、出处许可/源码/校验和，拒假完成。

检查ID：soak24h, clean-windows, no-runtime-node, release-notices, no-secret-export。
写入范围：tests/perf/, build/, docs/release/, third_party/。
