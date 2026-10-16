# #248 锁定读验证

这组文件同时保留实现前的负基线和实现后的原版 SqlSugar 正向消费者。负基线对应历史源码 `aa4fbffb`；当前候选在 `edd23c1d` 上实现单个关系基表、显式事务内的 `FOR UPDATE [NOWAIT]`。正式验收以 `docs/audits/issue-248-implementation-20261016.md` 的最终结果和冻结清单为准。

- `Program.cs.txt`：以两个真实 SndbConnection 和两个原版 SqlSugarClient 执行负矩阵。返回0要求10个控制检查通过和28次明确 unsupported；不会把异常吞成锁功能成功。
- `LockConsumer.csproj`：显式绑定给定 Core/Data 与 SqlSugar DLL，调用者提供 `EngineBin`、`SugarBin`、`SugarPackages`。外部 SqlSugar 的 Newtonsoft.Json 仅用于消费者，不加入 Sonnet runtime。
- `Remote-Negative.ps1`：历史文件名保留；复制并逐文件核 SHA 的已有 Server，启动独占 REST / HTTP2 Server、创建数据库，执行传入的 `ProbeAssembly`，记录实际请求协议并清理自己的进程。可传入负消费者或正消费者；`ProbeMode` 是未使用参数，不能据其值判断结果。脚本没有 reopen/recovery 验证。
- `AcceptanceTests.cs.txt`：保留实现前12个正向双连接验收草稿和原12失败记录。实现后的正式验收位于 Core 的 `SqlRowLockAcceptanceTests` / `SqlRowLockConcurrencyTests` / `TableRowLockManagerTests` 和 Server 的 `RemoteAdoHttp2TransportTests.RowLocks.cs`。
- `PositiveConsumer.csproj` / `PositiveProgram.cs.txt`：固定原版 SqlSugar PR1457 源码，验证 `TranLock(DbLockType.Wait/Error)` 生成的真实 SQL、活动持有者冲突、不同键、缺失键、提交/回滚释放、普通等待完成，以及 `Storageable.TranLock` 的同步/异步更新。每种协议要求34个检查通过；没有修改上游源或添加静默降级。
- `Common.ps1`：进程、退出码、证据和 SHA 记录工具，不记录 token。

本机可复现的准确项目、运行 spec 和输出位于 `D:/source/SonnetDB/artifacts/issue-supervision-20261016/issue248-work`。`preparation-checkpoint01.json` 索引源代码、日志、TRX、上游验证与已运行 DLL 的 SHA。所有重跑应使用新的输出/数据库/证据目录，不能覆盖该检查点的原始结果。

消费者构建示例（先将三个目录变量设置为有版本归属和 SHA 证据的绝对路径）：

```powershell
dotnet build scripts/validation/issue248-nowait/LockConsumer.csproj -c Release `
  --artifacts-path $freshBuildDirectory `
  "-p:EngineBin=$engineDirectory" "-p:SugarBin=$sugarDirectory" `
  "-p:SugarPackages=$externalPackageDirectory" `
  -m:2 -p:UseSharedCompilation=false -nodeReuse:false
dotnet $consumerAssembly $freshDatabaseDirectory current-negative
```

正向验证把项目改为 `PositiveConsumer.csproj`，运行新生成的 `PositiveConsumer.dll`，第二个运行参数仅为证据标签。远程验证把该 DLL 传给 `Remote-Negative.ps1 -ProbeAssembly`，由它依次启动 REST 和 h2c；以输出 `POSITIVE_CONTRACT_CONFIRMED` 和 exit0 为准。固定上游源是 `IoTSharp/SqlSugar@65e8226ce25a8e1cb9cca2590683f1fd6778cab3`；603个原始源码文件已逐一核 Git blob。其自身构建13条 nullable 警告独立记录，不等同于 SonnetDB 或正消费者构建告警。

远程脚本参数为 `EvidenceDirectory`、`ServerDirectory`、`ServerSourceCommit`、`ProbeAssembly` 和可选 `RuntimeDirectory`。必须给新目录；临时目录应在 D 盘，并使用本任务独占路径。正式调用前设置独立的 NuGet缓存、DOTNET_CLI_HOME、TEMP/TMP；现有 `Invoke.ps1` 和各 JSON spec 保存了实际值。

历史远程负矩阵中建库/控制查询/事务生命周期确实访问 Server，但锁语句由客户端 `SndbCommand` 解析守卫提前拒绝。实现后的正向矩阵实际访问 Server 锁定读。HTTP2 配置中的显式事务仍走 HTTP JSON transaction endpoint，普通查询控制出现 Frame 请求；不得把它写成 Frame 二进制锁事务。

`docs/audits/issue-248-preparation-20261016.md` 是准备时历史记录；旧失败和检查点保持原样。源码通过不表示已经发布正式 NuGet/NativeAOT 包或完成其它 ORM 的上游交付。
