# #255 无主键 DML 与真实 XCode 消费者核对

日期：2026-10-16。结论：已有 #243 的原生无主键实现通过本次独立消费者验收；本项新增一个真实 XCode NULL 读取/跨库迁移缺口的三文件本地候选及可运行探针。正式 Core/ADO/Server 发布和 XCode 上游交付未完成，#255 保持 OPEN。

## 固定来源

- SonnetDB 源码：`b157dddc2886127e2deddb22a75555a3883e687d`。专属树重新构建 Core/Data/Server，两个 Release 构建均 0 warning / 0 error，源码与 Server/客户端快照已按 SHA 冻结。这不是公开安装包。
- 公开 NuGet.org：稳定版 `SonnetDB` / `SonnetDB.Core 4.0.0`，来源 `d49babae0e049ca4554d26772b92493220d42822`；最新预览 `4.5.0-preview.ef266.5`，来源 `1af23d8f299febfdf66b040ab3419980396acf46`。实时索引保存在 discovery。GitHub 最新非预览 Server 发布仍为 `v4.0.0`，2026-09-26 发布。
- 真实 XCode PR #101：`757fa2a078906f87783e55fe0e9d5f54a9e916b9`，仍 OPEN/Draft；本地源码目标 netstandard2.1，消费者 net10.0。未修改、推送该上游分支。
- `NewLife.Core 11.19.2026.901` 包声明源码 `2c5e6afdf0d4c64325d5eb40f699cd4a323206e3`；SQLite 使用公开 `Microsoft.Data.Sqlite 10.0.0` 与真实磁盘库。

## 公开失败与源码成功分别验证

| 检查 | 实际结果 | 证据 |
| --- | --- | --- |
| 公开 4.0.0 / `.5`，ADO / 原 XCode，各首次参数化 INSERT | 4 个独立进程复现拒绝，表仍为空 | `matrix-05` 四份 `*-negative` 日志；该轮后续错误预期失败不改写 |
| 原 XCode + 当前源码，简单无主键创建/写入/反向工程 | 成功；XML 往返把 `events/name` 改成 `Events/Name` | `matrix-11-callback-final/source-original-metadata.*`，1 个基线进程 |
| Core 隐藏身份，初次和独立重开 | 2/2 进程通过 | `matrix-11-callback-final/core-*` |
| 原生 ADO，嵌入式/REST/h2c × 初次/独立重开 | 6/6 进程通过 | `matrix-11-callback-final/Native-*` |
| 真实 XCode 本地候选，嵌入式/REST/h2c × 初次/独立重开 | 6/6 进程通过 | `matrix-11-callback-final/XCode-*` |

最终源码正向合计 **14 个独立消费者进程**，另有原 XML 基线 1 个；这些不是 14 个 xUnit 测试，不与旧 #243 或中间重跑累计。Native 探针构建 0 warning / 0 error；固定 XCode 自身有 105 条已有警告、0 error，未压制警告。

普通无主键 INSERT 每行分配独立内部身份，重复值及 NULL 不合并；UPDATE 后身份不变，独立进程重开保持同一集合。SQL/ADO/XCode 模型只包含声明的业务列，零主键、零自增，不添加业务键。

参数化单行/多行、重复行影响数、UPDATE/DELETE/LIMIT、NULL、CHECK/UNIQUE/FK 失败原子性、事务提交/回滚、普通索引和独立重开均有实际断言。真实 Server 首轮日志有 HTTP/1.1 108、HTTP/2 103 次请求，其中 HTTP/2 Frame POST 40 次；新 Server 进程重开轮分别为 20、19、15 次。计数只用于确认真实传输，不是测试数。两轮宿主均在自己的进程范围内停止；第二轮停止是清理，不是额外恢复验收。

## 本项发现并修复的 XCode 缺口

加强前的 `matrix-06` 进程全退出 0，但 SQL 审查发现迁移中的数值 NULL 已变成 0；该轮缺少逐列 NULL 断言，不能认定跨库完整通过。`matrix-07-strong-baseline` 保存了加强后的真实失败和分层证据：

1. SQLite 原生 reader：`Score.IsDBNull=True`。
2. 原 XCode DAL `DbTable`：`Score=Int64:0`。
3. `DbPackage.OnPage`：0；实际参数 `DbType=Int64`、`IsDBNull=False`。
4. SonnetDB 原生 reader：实际存储 0；`forward-preserves-numeric-null` 明确失败。

固定 NewLife `DbTable.ReadData` 把 `DBNull` 按类型转成默认值，丢失发生在源读取阶段。SonnetDB 引擎和 ADO 正确保留收到的值；此轮没有修改 SonnetDB 生产文件。

新 `XCodeNulls.patch` 仅涉及上游三文件：

- `Common/DbSession.cs`：SonnetDB 同步/异步 DAL 的 DbTable 读取逐列保留真实 NULL；内部迁移查询使用同一转换，不吞读取异常。
- `DAL_DbOperate.cs`：每次调用的内部保留 NULL 查询入口，并使用独立缓存操作名。
- `DbPackage.cs`：涉及 SonnetDB 的默认 `PagingExtracter` 迁移调用该入口。通过委托等值区分构造函数内建 `GetExtracter` 与外部回调；回调调用一次，非空外部结果执行自身 `Fetch()`，空结果保持原 fallback。默认查询使用 `paging.Dal`，不新增 `TotalCount` 累加。没有会话级可变模式，没有新公开 API；用户自定义抽取器和不含 SonnetDB 的迁移沿用原路径。键/时间抽取器与备份二进制序列化不在本次新增承诺内。

此前 #261 的 `XCodeMetadata.patch` 单独保留，SHA256 `555B3BF314C6558C0955ECEDAB96C712EA2952A529D6A773C60AE9489FC736AE`。该依赖解决物理名 XML 往返，不重复算作 #255 新修复。

最终真实 `Entity.BatchInsert`、`DAL.Insert(IModel,IDataTable)`、参数化更新/删除和事务均通过。SQLite ↔ SonnetDB 的实际 `DbPackage.Sync` 通过 `Db.BatchSize=2` 以每页 2 行处理 3 行，分别验证空回调结果 fallback 和构造函数内建委托的默认路径；断言重复行、整数/Decimal/Boolean/DateTime/string NULL、非空精确数值及日期 ticks、模型类型/可空性/原始名字。注入源查询错误后验证零写入并复用同一源正常完成迁移；SQLite→SQLite 对照及普通 SQLite 查询保持原有默认值行为。未把该对照的既有 NULL→0 行为称为正确的数据保真。


复审发现原 `matrix-09-final` 用 `CreateExtracterCallback` 返回标准 `PagingExtracter` 设置分页，因此没有验证真正默认路径。旧补丁只按返回类型识别默认抽取器，覆盖外部回调指定的 `Dal`，还无必要地累加了原始 `Fetch()` 不修改的 `TotalCount`。该轮不能继续称为默认迁移完整验收；旧运行、旧清单和原 18 个交付文件已冻结保留。

`matrix-10-callback-baseline` 用旧补丁及新真实控制复现错误：两个 SQLite 数据源有同名 `CallbackEvents` 表，分别存 `outer`/`alternate` 标记；回调指定 alternate，实际 SQL 却读取 outer，并触发 `custom-paging-uses-alternate-dal` 失败。新 `matrix-11-callback-final` 在嵌入式/REST/h2c 三种通路均读出 alternate 的三行，排除 outer 和被 Row=1 跳过的行，验证回调仅调用一次、页大小 2/1、最终 Row=5、初始 TotalCount=41 不变。默认双向分页同时验证 OnPage.Row 保持原始 0 和各类型 NULL；新一轮 14 进程独立计数，不与旧运行累计。

## 可复现与证据边界

执行入口及参数见 [探针说明](issue-255-keyless-probe/README.md)。本地证据根：`D:/source/SonnetDB/artifacts/issue-supervision-20261016/issue255-work`；源码构建 `source-01`、公开负基线 `matrix-05`、加强失败 `matrix-07-strong-baseline`、最终 `matrix-11-callback-final`。最终命令源副本在 `executed-probe`，模型 XML 及候选/运行 DLL 哈希在完整性收据。机器可读摘要为同名 JSON。

保留早期错误匹配、探针编译/API、源码 DLL 依赖装配、错误元数据预期及弱断言运行；没有覆盖失败或累计中间 PASS。完整性验证核对 2582 份源码、冻结二进制及运行证据。没有新增仓库 xUnit 用例，也未重跑完整 suite、NativeAOT 发布、性能或长稳；生产源码仅重新构建并跑上述独立消费者。

已有 v13 catalog 屏障及旧引擎拒绝边界沿用 #243，未新增格式变更。无主键 ON CONFLICT/按业务键 Upsert/JOIN-FROM UPDATE 及作为 FK 被引用主表的限制不改变。SQLite 实验不能推导 PostgreSQL 或所有自定义 XCode 抽取器已验收。

本轮不提交、不推送、不回复/关闭 Issue。根监督代理负责集成、HANDOFF/CHANGELOG、最终完整 restore/Format Check 和独立提交；正式发布及 XCode #100/PR #101 的正式依赖/上游验收仍未满足，不能关闭 #255。
