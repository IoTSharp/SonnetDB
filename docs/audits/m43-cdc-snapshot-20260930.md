# M43 #386 本地快照与增量接收端

## 交付范围

新增 `SonnetDB.Cdc.CdcSnapshotReplica` 及中文文档合同。该 API 是单源、单实体、单 schema、单分区的本地持久接收端，要求上层持有固定读视图及其准确总行数和 CDC 位点。源捕获和快照期间的增量保留复用已有 `CdcEventSpool`，没有重做 append/replay/ack。

新增 API：

- `CreateAsync` / `Open`：新建或按完全一致的 descriptor 重开；新建不覆盖，重开缺文件不自动初始化。
- `GetState`：读取已经提交的快照页序号、末尾键、阶段和应用位点。
- `WriteSnapshotPageAsync`：按键严格递增的有界页；行与复制进度同时持久化。
- `CompleteSnapshotAsync`：准确总行数完整后原子切换阶段，支持空快照和重复完成。
- `ApplyIncrementalAsync`：连续位点、身份校验、完整后像和删除；物化行与位点整批原子发布。
- `ReadRowsAsync`：有界物化读取；结果同时携带本次一致读取的位点。

状态使用独立版本 1 格式：`SDBCSR01`、48 字节头、正文长度、SHA-256 与 source-generated JSON。容量限制覆盖物化行数、状态文件字节数、批次条数/字节数、键和 JSON 单值。未修改数据库或既有 CDC spool 格式，未新增依赖或使用 `unsafe`。

## 回归证据

快照接收端最终新增 19 个测试方法、20 个测试用例：

- 快照中途关闭，按持久序号续传；完成后消费 spool，提交后确认，再次重开对账插入、更新和删除。
- 容量为 1 的最后事件已物化但尚未 ACK；重开后没有后续批次，先补确认释放容量，再继续接收下一条事件。
- 空快照、完整计数门禁、重复完成、错序/重复键、错误 ordinal 和非法 JSON。
- 创建、写入、完成、增量和读取的预取消，以及枚举中途取消的快照页/增量批次。
- 源/实体/schema/分区错配、schema 版本拒绝、checkpoint 缺口/倒退、最后事件相同重试及内容变化。
- 行数/文件字节/批次条数/批次字节容量；容量失败不推进持久位点，删除释放行数容量。
- 有界键分页、第一行字节不足、最大 Int64 checkpoint、身份错配、状态损坏/截断、缺文件和独占 lease。

验证命令：

```powershell
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~Cdc --logger 'trx;LogFileName=cdc.trx' --results-directory artifacts/m43-local-20260930
```

初轮结果：**44/44 通过，0 skipped**，包含当时新快照接收端 19 项及既有 CDC codec/spool 回归。TRX 位于 `artifacts/m43-local-20260930/cdc.trx`。随后补充尾批 ACK 和 spool 预取消、metadata/帧扫描取消 4 项回归，最终 CDC 与 Streaming 合并 **99/99 通过，0 失败、0 跳过**；其中 CDC 48 项（codec 9、spool 19、接收端 20），Streaming 51 项。最终 TRX 位于 `artifacts/m43-local-20260930/cdc-streaming-final.trx`。Release 构建保持 Core 的 `IsAotCompatible=true` 分析，执行输出未出现 IL/AOT 警告；未执行 NativeAOT 发布或跨架构运行。

首轮有 2 项失败，分别是 JSON 解析派生异常和 `SemaphoreSlim` 预取消派生异常的精确类型断言。修正 JSON 派生异常断言并统一公开异步 API 的预取消检查后，第二轮全部通过。

## 真实子进程强制终止

新增 `tests/SonnetDB.CrashTests/M43CrashReliabilityTests.cs`，复用既有 `RunKillScenario` / `Process.Kill(entireProcessTree: true)` 及 Child Program 就绪标记框架，未新增或复制进程启动/终止封装。子进程在发布持久边界标记后等待父进程强杀，30 秒取消边界避免父进程失败时无限等待；重开操作受 10 秒取消边界约束。

四个本地进程场景：

- `M43_CdcPartialSnapshot_HardKill_ReopensAndResumesAtCommittedOrdinal`：只提交第一快照页，进程被强杀后按 ordinal 1 续传，完成阶段转换，再次重开对账两行。
- `M43_CdcMaterializationBeforeAck_HardKill_ReopensFiltersAndConfirmsCommittedEvents`：增量 11/12 已随物化行提交，spool 11/12/13 都未确认时强杀；恢复位点 12 排除已物化前缀，确认该前缀后应用/确认 13，再次重开验证数据和空 spool。
- `M43_CdcSingleTailBeforeAck_HardKill_ReplayEmptyStillConfirmsAndReusesCapacity`：单个尾事件已物化但未确认、spool `MaxEvents=1` 时强杀；重开按已应用位点回放为空，仍按该位点补确认，回收容量并继续追加、物化、确认下一事件。
- `M43_StreamingInFlight_HardKill_RedeliversStableDeliveryIdAndPersistsAck`：订阅投递未确认时强杀，重开按同一 deliveryId 重投且 attempt 为 2；确认后再次重开验证位点与空 spool。

验证命令：

```powershell
dotnet test tests/SonnetDB.CrashTests/SonnetDB.CrashTests.csproj -c Release --filter FullyQualifiedName~M43_ --logger 'trx;LogFileName=crash.trx' --results-directory artifacts/m43-local-20260930
```

结果：**4/4 通过，0 skipped**。原始 TRX 位于 `artifacts/m43-local-20260930/crash.trx`。首次 `--no-restore` 命令因该 checkout 尚无 CrashTests 的 obj/bin 而零输出退出，且没有生成 TRX，不计为测试成功；完成已有依赖的正常 restore 后，上述命令构建真实 Child 程序，追加单尾场景后的 `--no-restore` 定向回归全部通过。真实 local process test 不等于远程拓扑、实际掉电或 168 小时 gate。

## 保留边界

- 固定读视图必须与 descriptor 位点一致，并在快照复制前启动/保留源 CDC 捕获；此切片没有接入引擎写入钩子或自动建立该视图。
- 仅支持一个实体专属分区，要求连续 offset。混合实体过滤、多分区快照、跨分区原子切换和远程拓扑尚未交付。
- 接收端提交先于 spool 确认。spool 必须专用，且已有确认不能提前删除接收端尚未应用的事件。
- 仅保留最后事件内容摘要供重试；历史重复事件依据持久位点在回放端过滤，不提供通用去重、冲突规则或 exactly-once。
- 每批原子替换整个有界状态文件，当前用途是本地恢复合同；吞吐、固定硬件容量和长稳尚未验证。
- 多批读取在并发增量期间不保证相同视图，调用方需核对各批位点或暂停应用增量。
- 发布尝试后的存储错误可能结果未知。当前实例 faulted 后必须重开校验，异常不能被理解为事务已经回滚。
- 无真实远程 parity、断网现场演练、固定硬件、生产安装或长期 gate 的 `PASS` 声明。

## CHANGELOG 建议

`Added`：M43 #386 新增有界本地 `CdcSnapshotReplica`，提供固定快照页恢复、完整计数切换、同源/schema/分区连续增量的原子物化、位点恢复与有界读取；复用已有 CDC spool 保留复制期间增量，明确保留远程拓扑与生产门禁边界。

`Added`：M43 #390/#395 新增 4 项真实本地子进程 hard-kill/reopen 验收，覆盖部分快照、接收端提交后 spool 未确认、单尾容量回收、持久订阅未确认投递的稳定 ID 重投与确认后再次重开。
