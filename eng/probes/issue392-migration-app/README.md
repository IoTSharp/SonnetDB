# 库存应用迁移验收（#392）

这是一个独立可运行的 EF Core 库存应用。它只有 `PackageReference`，没有 SonnetDB 源码项目引用；`Run-Isolated.ps1` 把五个固定输入复制到仓库外的全新目录，以独占 NuGet 包缓存及 HTTP 缓存还原和运行。默认项目版本 5.0.0 用于历史公开包负控；新事务修复须使用发行协调者冻结的新版本，不能把本地候选写成公开包。

## 运行

使用 PowerShell 7、.NET 10；每次输出目录必须不存在。将 `ProviderVersion` 设置为要验证的精确版本：

```powershell
$providerVersion = Read-Host '精确 Provider 版本'
$outputDirectory = Join-Path $env:TEMP ('sonnetdb-inventory-' + [Guid]::NewGuid().ToString('N'))
./eng/probes/issue392-migration-app/Run-Isolated.ps1 -ProviderVersion $providerVersion -OutputDirectory $outputDirectory
```

不传 `CandidateFeed` 时仅使用 NuGet.org。验证本地候选时显式传入候选包目录；结果标记 `LOCAL_CANDIDATE_NOT_PUBLIC`。远程入口用 `ConnectionString` 指定匹配版本的测试 Server，支持已有 `Protocol=rest` 或 `Protocol=frame-http2` 连接合同；不能把嵌入式结果转记为远程 Server 结果。

执行器应对整个脚本设置超时并回收所属子进程树；共享开发机需遵循既有重负载互斥。应用每次运行有 120 秒取消期限；三次运行分别启动新进程。日志、实际依赖及结果在输出目录保留，脚本不删除数据库或全局 NuGet 缓存。

## 固定业务旅程

初始迁移建立 `Categories` 的自引用父分类及 `Products.CategoryId` 外键，写入两个分类和一个温度传感器，库存为 7。发货迁移在一个事务中增加分类备注、产品名索引、库存非负 CHECK 和有产品外键的发货表，写入数量为 2 的发货种子并把库存改为 5。两个历史行分别绑定 `20261106000100_InventoryInitial` 和 `20261106000200_InventoryShipping`。

`roundtrip` 在最后的迁移历史行加入真实 CHECK 拒绝条件，确认本次全部 DDL/DML 回滚，随后在同一连接移除该条件、重试并重复迁移。应用通过实际 EF 查询和 `SaveChanges` 把库存改成 4，再恢复为 5；然后执行真实 Down、重复 Down、Up。两次后续 `verify-new` 是独立进程重开，检查数据、列、表、索引、外键、自引用、CHECK、历史及失败后连接可用性。

`roundtrip` 显式持有已打开的连接，通过 EF `DbConnectionInterceptor` 同时计数同步/异步打开和关闭；历史拒绝后和重试成功后均断言连接仍为 `Open`、对象相同、打开一次且从未关闭，输出 `SAME_CONNECTION`。当前 `SndbConnection` 不触发标准 `StateChange`，不能用该事件的零计数证明连接连续性；仅复用 `DbContext` 也不能代替此断言。应用输出 `REQUESTED_PROTOCOL`；远程验收必须显式选择 `rest` 或 `frame-http2`，自动协议探测不计入指定协议验收。该标记描述请求配置，实际线协议还须由测试 Server 的请求记录独立证明。

Down 删除发货表、恢复固定种子的库存、移除 CHECK/索引/备注列，并由 EF 删除对应历史行。这里的固定库存回补只适用于此受控验收数据；真实应用须为升级后的新增业务写入设计回补方案，不能把这个数值直接用于现存业务库。

## 升级、恢复与回退边界

只含受支持关系 DDL/DML 且没有 transaction-suppressed 命令的迁移组共享原子边界。独立序列、schema、显式事务外 SQL 仍会分组提交，详见 [能力矩阵](../../../docs/audits/issue-392-20261105.md)。`Migrate` 的历史检查可用于重复调用；本 Provider 仍明确拒绝 EF 的通用 idempotent SQL script，不应把两者混称。

升级前停止写入并保留完整数据库备份、应用和 Server 的准确版本。进程中断发生于 durable committed 决策前时恢复旧状态，决策后恢复新状态；调用端未收到确认时，应重新连接并读取历史与业务状态后调用 `Migrate`，不能只凭调用异常推断已回滚。

应用 Down 不等于引擎降级。关系 DDL 的目录 floor 与已有段格式仍适用；旧引擎不支持新目录/段格式时，必须恢复升级前备份并处理升级后的业务写入，不允许用删除 journal 或手改版本号强行打开。真实强杀测试证明进程崩溃恢复，物理掉电与生产环境另行验收。

源码强杀用例位于 `tests/SonnetDB.CrashTests/EfMigrationCrashTests.cs`。子进程链接本应用的业务模型和迁移，但引用源码 Provider 以连接既有 internal 提交阶段钩子；它与上述包消费应用是不同证据门。

包消费者另提供 `upgrade-await-kill <connection-string> <new-ready-file>`。先在新目录执行 `initialize`，再由监督器启动该动作；应用在升级确认及完整新状态核对后，以 `CreateNew` 写入含 PID/开始时间的 `after-ack` 文件，在总共 120 秒期限内等待监督器强制终止。父进程核对 PID、创建时间、完整命令和所属父链后执行强杀，保留退出码与原始日志，再运行两个独立 `verify-new`，以及 `downgrade`、`upgrade`、`verify-new`。未按期被终止时该动作失败，不自行打印 PASS；既有 `Run-Isolated.ps1` 不自动执行这条强杀路径。

这条包级路径仅证明已经收到提交确认后的应用进程强杀与恢复；不能代替 prepared/committed 等精确内部阶段的源码强杀。嵌入式独立进程验证会重新打开引擎；远程应用进程只重新建立会话，完整远程引擎恢复还须由唯一 Server 所有者停止并重启同一数据目录的匹配测试 Server 后复验。两者均不代表物理掉电或生产恢复。
