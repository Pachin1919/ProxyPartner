# 依赖与来源锁定
## 产品运行时（尽量小）
C#受支持稳定.NET SDK发布Native AOT；Windows SDK/编译工具只在构建机需要。具体SDK在T00核实并锁global.json，不预写未经构建的版本组合。
Win32/user32/comctl32/shell32为第一UI候选，必要PInvoke优先可裁剪/source-generated声明。不为几个API引入庞大第三方UI封装。
System.Text.Json使用源生成上下文/流读取；不要runtime反射序列化整棵DOM。
Microsoft.Data.Sqlite或同等成熟SQLite绑定必须实测AOT/打包内存，选一种；固定NuGet lock与native SQLite版本。无ORM、无数据库服务器。
Go worker只用必要成熟代理代码/协议依赖；固定Mihomo tag+commit/go.sum；从SS真实需求验证。不复制faceair的整套导入和服务。
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
本包是自己的实施规格和验证工具，不包含上游完整源文件。产品建议走GPL-3.0兼容开源路线，但最终license/第三方notice以实际引入版本为准。
之前核查：MetaCubeXD、Zashboard、Neko Master为MIT；Mihomo Meta分支、faceair为GPL3；YACD-meta仅取得package许可声明时，不能当作完整license审核。实施T00及每次新依赖引入必须复核原始LICENSE，保留版权、修改说明和需要提供的对应源码。
分进程不能自动免除copyleft义务。开源意愿不是许可证兼容性结论。不开无必要法律讨论，但发行资料须完整。

## 源码定位流程
优先按sources.json里的tag/ref取文件，核对blob（SHA1("blob "+bytes_length+NUL+raw_bytes)）。可变分支已变化则记录新commit并重新审查，不能把旧结论标成新版本已验证。Blob SHA不是commit；本包提供的抓取脚本会校验内容，不会把下载失败写成成功。
