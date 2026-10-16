# #255 无主键表独立消费者

这些是验收探针，不参与产品构建。使用 PowerShell 7、.NET 10 SDK；输出目录和运行目录必须全新，旧证据不会覆盖。大型缓存与输出放 D 盘，运行目录使用短且独占的路径。

固定输入：SonnetDB `b157dddc2886127e2deddb22a75555a3883e687d`；XCode PR #101 `757fa2a078906f87783e55fe0e9d5f54a9e916b9`。`Build-Source.ps1` 校验 SonnetDB HEAD、初始化两个锁定子模块、构建 Core/Data/Server 并冻结二进制与源码清单；这属于源码候选，不是正式发行。

```powershell
pwsh -NoProfile -File .\Build-Source.ps1 `
  -SourceRoot D:\source\SonnetDB-worktrees\issue255-keyless-xcode `
  -OutputDirectory D:\evidence\issue255-source `
  -RuntimeDirectory D:\sndb-t\255-source

pwsh -NoProfile -File .\Run.ps1 `
  -OutputDirectory D:\evidence\issue255-matrix `
  -RuntimeDirectory D:\sndb-t\255-matrix `
  -XCodeSource D:\source\xcode-fixed-757fa2a0 `
  -SourceBuild D:\evidence\issue255-source
```

`Run.ps1` 使用本地代理 `127.0.0.1:7890`、独立 NuGet.org 缓存；先运行公开 `4.0.0` 和 `4.5.0-preview.ef266.5` 的原生/XCode 拒绝基线，再运行源码候选。`-SkipPublic` 仅跳过已单独冻结的公开负基线；不得据此声称验证了公开包。

本地 XCode 候选顺序应用两份独立补丁：

- `XCodeMetadata.patch`：此前 #261 的元数据/XML 物理名补丁，SHA256 `555B3BF314C6558C0955ECEDAB96C712EA2952A529D6A773C60AE9489FC736AE`，不是 #255 新修复。
- `XCodeNulls.patch`：本项发现的 NULL 读取/迁移修复，只涉及外部 XCode `DbSession.cs`、`DAL_DbOperate.cs`、`DbPackage.cs`，没有 SonnetDB 生产改动。普通 SonnetDB 同步/异步 DAL 查询保留真实 NULL；涉及 SonnetDB 的默认分页迁移使用每次调用的内部查询入口，避免 NewLife `DbTable.ReadData` 将 NULL 转成 0、false 或默认日期。构造函数内建 `GetExtracter` 委托按委托等值识别；外部回调仅调用一次，非空结果原样执行自己的 `Fetch()`，空结果保持原有 fallback。默认查询使用抽取器自己的 `Dal`，`Row` 和 `TotalCount` 遵循原始分页合同。没有会话级可变开关或新公开 API；不含 SonnetDB 的迁移沿用原逻辑。

`-OriginalNullBehavior` 不应用第二份补丁，可复现加强断言后的迁移失败。补丁不对键索引抽取器、用户自定义抽取器、备份二进制序列化作新的能力承诺。

探针包含真实 `Entity.BatchInsert`、`DAL.Insert(IModel,IDataTable)`、参数化 DML、独立重开，以及 Microsoft.Data.Sqlite `10.0.0` 的实际 SQLite ↔ SonnetDB 多页 `DbPackage.Sync`。模型由实际 provider 反向工程并 XML 往返，断言零主键、零自增、精确物理名、类型与可空性。消费者不是模拟 ORM。真实 Server 分别监听 REST/h2c，写入后停止进程，再启动新宿主与新消费者验证持久化。

默认双向分页使用 `Db.BatchSize=2`，分别检查回调空结果 fallback、构造函数内建委托及每页 2/1 行的真实 NULL。独立控制让外部回调返回标准 `PagingExtracter`，指向另一 SQLite DAL；两库同名表使用不同标记值，验证实际读取指定数据源、一次回调、起始 Row=1、结束 Row=5、TotalCount=41 不变。旧 `matrix-09-final` 用回调设置分页，未验证真正默认路径，且漏掉了上述数据源覆盖错误；该轮和原交付文件保持冻结。`matrix-10-callback-baseline` 保存旧补丁在新控制上的失败，最终修正单独记录在 `matrix-11-callback-final`。

每个运行保存命令、PID、退出码、日志、实际 SQL/参数、运行 DLL 和 SHA；执行源文件副本在 `executed-probe`。XCode 上游原有警告不压制，不计作 SonnetDB 构建警告。进程数、断言数与仓库 xUnit 测试数必须分开。

正式 Core/ADO/Server 发布、XCode 上游合入和 #100/PR #101 更新仍需独立交付，不能据此关闭 #255。
