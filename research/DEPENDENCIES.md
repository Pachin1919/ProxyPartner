# 依赖与来源锁定
## 产品运行时（尽量小）
C#受支持稳定.NET SDK发布Native AOT；Windows SDK/编译工具只在构建机需要。具体SDK在T00核实并锁global.json，不预写未经构建的版本组合。
Win32/user32/comctl32/shell32为第一UI候选，必要PInvoke优先可裁剪/source-generated声明。不为几个API引入庞大第三方UI封装。
System.Text.Json使用源生成上下文/流读取；不要runtime反射序列化整棵DOM。
Microsoft.Data.Sqlite或同等成熟SQLite绑定必须实测AOT/打包内存，选一种；固定NuGet lock与native SQLite版本。无ORM、无数据库服务器。
Go worker 使用经审查的许可兼容协议依赖，固定上游版本、提交和 go.sum。先从 Shadowsocks 真实线路验证。Mihomo 与 faceair 源码作为接口和行为依据。
不默认加入图表、地理数据库、浏览器自动化、自动升级、插件引擎、LLM SDK、并行任务框架。

## 使用方法依据（官方）
- Native AOT：https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/ （减少运行时成本不等于特定MB保证）
- Win32虚拟列表：https://learn.microsoft.com/en-us/windows/win32/controls/list-view-controls-overview （LVS_OWNERDATA，应用管理数据/排序；不是每行一个控件）
- 命名管道：https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.namedpipeclientstream
- 自定义HTTP连接：https://learn.microsoft.com/en-us/dotnet/api/system.net.http.socketshttphandler.connectcallback
- SQLite绑定：https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/
- SQLite PRAGMA：https://www.sqlite.org/pragma.html
- WAL：https://www.sqlite.org/wal.html
- Codex agents：https://developers.openai.com/codex/multi-agent
- Codex skills：https://developers.openai.com/codex/skills

## 许可证处理
项目自研代码采用 [MIT](../LICENSE)。按实际采用的上游版本复核原始 LICENSE，保留所需版权和通知，记录引入范围及提交。
既有核查将 MetaCubeXD、Zashboard、Neko Master 作为 MIT 参考，Mihomo 与 faceair 为 GPLv3 接口和行为参考。YACD-meta 的包声明需要与实际版本的许可文件交叉确认。T00 及新增依赖时按来源清单做版本核对。

## 源码定位流程
优先按sources.json里的tag/ref取文件，核对blob（SHA1("blob "+bytes_length+NUL+raw_bytes)）。可变分支已变化则记录新commit并重新审查，不能把旧结论标成新版本已验证。Blob SHA不是commit；本包提供的抓取脚本会校验内容，不会把下载失败写成成功。
