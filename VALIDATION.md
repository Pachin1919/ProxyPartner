# 本次实际验证记录

**2026-11-04｜验证对象：ProxyPartner Lite Codex实施包v3及随包开发工具。不是已完成的桌面产品。**

## 已实际执行

| 项目 | 实际结果 | 证据 |
|---|---|---|
| Go工作流工具 `go test -count=1 -v ./...` | 17项测试通过 | `validation/flow-tests.txt` |
| Go采样预算工具同命令 | 13项测试通过 | `validation/budget-tests.txt` |
| 两个Go模块 `go vet ./...` | 均退出0 | `validation/flow-vet.txt`、`budget-vet.txt` |
| 资料包/契约/SQLite/评分夹具校验 | 21项测试通过 | `validation/kitchecks.txt` |
| `flow validate` | 12项任务图与初始状态通过 | `validation/flow-validate.txt` |
| `flow ready` | 初始只有T00可启动 | `validation/flow-ready.json` |
| 依赖图、开发循环、运行数据流、观察状态图 | DOT渲染PNG/SVG成功；任务图由同一tasks.json生成 | `diagrams/`、`validation/diagram-*.txt` |

合计 **51项自动化测试**，其中6个评分黄金案例包含在夹具校验测试中，不重复加到51项。测试通过只说明本包提供的工具与示例契约符合这些测试，不能替代将来的产品验收。

本环境：Linux x64，Go 1.23.2，Python 3.13.5，SQLite版本在 `validation/result.json` 中记录。Go版本是本次工具测试环境，不是建议最终产品锁定的版本。Python/jsonschema仅为可选开发校验工具，不进入产品发行依赖。

## 源码核查范围

11个源文件在本次重新读取并取得Git blob SHA；13个前轮源码条目保留并明确标记。部分文件只读与任务相关的行区间，范围写在 `research/sources.json`。来源清单不表示所有上游代码都得到运行或安全审计。
本次核实了Verge2.4.7命名管道路径、Mihomo1.19.21管道路由的认证处理、原生历史与URLTest副作用，以及竞品的推荐存储、连接统计、图表和批处理实现。

## 人工/静态检查

已查看任务图、开发循环与运行数据流的实际PNG，文字和箭头可读。PowerShell脚本做了静态检查，并修正了采样首尾跨度应覆盖要求时长的边界；**没有在PowerShell中执行**。所有测试数据为明确标记的合成夹具，没有用户订阅、Secret或API Key。

## 未执行，不得冒充通过

- 当前环境没有.NET SDK、PowerShell和Windows交互桌面；没有编译或运行C#窗口。
- 没有访问用户电脑上的管道、HTTP接口或订阅缓存，未确认实际ACL/宿主身份/有效配置位置。
- 没有编译独立Mihomo worker，未验证节点隔离、TUN路径、出口IP或真实目标网站速度。
- **25/35/40MiB只是工程预算，没有任何产品实测数据。** 未做24小时运行；没有Job Object/ETW全生命周期峰值证明。
- 自定义Codex agent TOML经过格式校验，但没有在用户安装的Codex宿主中加载运行；T00负责兼容性确认。
- 12项产品任务全部保持pending；未创建远程仓库、PR、自动化计划或付费模型调用。

## 工具能力边界

`flow`校验依赖、SHA和文件哈希，不会调用agent/执行任务，也不能证明日志真实。`budget`只检查外部采样CSV，成功状态刻意写成 `SAMPLED_PASS_REQUIRES_PEAK_PROOF`，不能漏算短命子进程或把采样峰值当终身峰值。Windows正式门禁仍需要实现和独立验证。

最终文件完整性与本地链接检查见 `validation/package-audit.json`；包内校验和见 `SHA256SUMS.txt`。
