# M43 #386 本地快照与增量接收端

## 交付范围

新增 `SonnetDB.Cdc.CdcSnapshotReplica`、`CdcSourceReadView`、`CdcDocumentSourceCapture` 和 `CdcLocalReplicaPump` 及中文文档合同。该切片是单源、单实体、单 schema、单分区的本地持久旅程：文档集合在写锁内取得稳定 KV 读快照和 change-feed 位点，固定视图按准确行数原子发布；源捕获把持久 change feed 接入专用 `CdcEventSpool`，快照期间的增量保留复用已有 append/replay/ack；本地泵有界推进接收端并在物化提交后确认 spool。

新增 API：

- `CreateAsync` / `Open`：新建或按完全一致的 descriptor 重开；新建不覆盖，重开缺文件不自动初始化。
- `GetState`：读取已经提交的快照页序号、末尾键、阶段和应用位点。
- `WriteSnapshotPageAsync`：按键严格递增的有界页；行与复制进度同时持久化。
- `CompleteSnapshotAsync`：准确总行数完整后原子切换阶段，支持空快照和重复完成。
- `ApplyIncrementalAsync`：连续位点、身份校验、完整后像和删除；物化行与位点整批原子发布。
- `ReadRowsAsync`：有界物化读取；结果同时携带本次一致读取的位点。
- `CdcSourceReadView.CaptureDocumentCollectionAsync`：从 `DocumentCollectionStore` 的稳定 KV 快照生成固定行集和 descriptor checkpoint；视图文件可独立重开。
- `CdcSourceReadView.OpenExisting`：创建结果未知时按源、实体、schema、分区和可选快照 ID 校验既有视图，取回持久行数与 checkpoint；严格 `Open` 仍要求完整 descriptor。
- `CdcDocumentSourceCapture.CaptureAsync`：从 source change feed 按 spool ACK/未确认帧恢复连续序号，转换 insert/update/delete 并 durable append；载荷截断、缺口和身份错配拒绝推进。
- `CdcLocalReplicaPump.AdvanceAsync`：每次复制一页固定视图、完成已复制快照，或核对高水位、补确认已物化前缀并应用/确认一批增量；调用方负责捕获调度和重复推进。

状态使用独立版本 1 格式：`SDBCSR01`、48 字节头、正文长度、SHA-256 与 source-generated JSON。容量限制覆盖物化行数、状态文件字节数、批次条数/字节数、键和 JSON 单值。未修改数据库或既有 CDC spool 格式，未新增依赖或使用 `unsafe`。

## 回归证据

快照接收端最终新增 19 个测试方法、20 个测试用例：

- 快照中途关闭，按持久序号续传；完成后消费 spool，提交后确认，再次重开对账插入、更新和删除。
- 容量为 1 的最后事件已物化但尚未 ACK；重开后没有后续批次，先补确认释放容量，再继续接收下一条事件。
- 固定视图建立后写入的文档通过真实 change feed 捕获到 spool；关闭并重开数据库、捕获器和 spool 后按未确认 high-watermark 续捕获，副本结果与源端最终状态对账。
- 空快照、完整计数门禁、重复完成、错序/重复键、错误 ordinal 和非法 JSON。
- 创建、写入、完成、增量和读取的预取消，以及枚举中途取消的快照页/增量批次。
- 源/实体/schema/分区错配、schema 版本拒绝、checkpoint 缺口/倒退、最后事件相同重试及内容变化。
- 行数/文件字节/批次条数/批次字节容量；容量失败不推进持久位点，删除释放行数容量。
- 有界键分页、第一行字节不足、最大 Int64 checkpoint、身份错配、状态损坏/截断、缺文件和独占 lease。

本轮新增 `CdcSourcePipelineTests` **17 项**：固定视图与边界后写入对账、分页/容量/身份/损坏恢复、创建结果未知时的 `OpenExisting` descriptor 取回与错配拒绝、源捕获高水位跨批重开、错分区与容量回收，以及有界泵的提交后未 ACK 补确认、并发推进和捕获/ACK 交错。CDC 定向 Release `/warnaserror` 回归为 **65/65 通过，0 失败、0 跳过**；其中既有 CDC 回归 48 项，本轮源管线 17 项。原始结果为 `artifacts/m43-local-20260930/cdc-source-final.trx`。

验证命令：

```powershell
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release --no-restore /warnaserror --filter FullyQualifiedName~.Cdc. --logger 'trx;LogFileName=cdc-source-final.trx' --results-directory artifacts/m43-local-20260930
```

先前 CDC 回归为 **44/44**，补充尾批 ACK 和 spool 预取消、metadata/帧扫描取消后，与 Streaming 合并回归为 **99/99**（CDC 48、Streaming 51）；对应旧 TRX 分别为 `cdc.trx`、`cdc-streaming-final.trx`。新增真实文档源切片后的当前 CDC 结果以上述 65/65 为准；不同过滤范围的结果不能相加为独立测试数。完整 `dotnet build SonnetDB.slnx -c Release --no-restore /warnaserror` 为 **0 警告、0 错误**；Core 的 `IsAotCompatible=true` 分析开启，未执行 NativeAOT 发布或跨架构运行。

首轮有 2 项失败，分别是 JSON 解析派生异常和 `SemaphoreSlim` 预取消派生异常的精确类型断言。修正 JSON 派生异常断言并统一公开异步 API 的预取消检查后，第二轮全部通过。

## 真实子进程强制终止

新增 `tests/SonnetDB.CrashTests/M43CrashReliabilityTests.cs`，复用既有 `RunKillScenario` / `Process.Kill(entireProcessTree: true)` 及 Child Program 就绪标记框架，未新增或复制进程启动/终止封装。子进程在发布持久边界标记后等待父进程强杀，30 秒取消边界避免父进程失败时无限等待；重开操作受 10 秒取消边界约束。

五个本地进程场景：

- `M43_CdcDocumentSourceCapture_HardKill_ReopensContinuesAndReconciles`：源集合、固定视图和部分快照已持久化，捕获部分增量后强杀；重开源库、视图、spool 和副本，继续写入并从未确认高水位续捕获，按位点应用/确认后对账最终源状态。
- `M43_CdcPartialSnapshot_HardKill_ReopensAndResumesAtCommittedOrdinal`：只提交第一快照页，进程被强杀后按 ordinal 1 续传，完成阶段转换，再次重开对账两行。
- `M43_CdcMaterializationBeforeAck_HardKill_ReopensFiltersAndConfirmsCommittedEvents`：增量 11/12 已随物化行提交，spool 11/12/13 都未确认时强杀；恢复位点 12 排除已物化前缀，确认该前缀后应用/确认 13，再次重开验证数据和空 spool。
- `M43_CdcSingleTailBeforeAck_HardKill_ReplayEmptyStillConfirmsAndReusesCapacity`：单个尾事件已物化但未确认、spool `MaxEvents=1` 时强杀；重开按已应用位点回放为空，仍按该位点补确认，回收容量并继续追加、物化、确认下一事件。
- `M43_StreamingInFlight_HardKill_RedeliversStableDeliveryIdAndPersistsAck`：订阅投递未确认时强杀，重开按同一 deliveryId 重投且 attempt 为 2；确认后再次重开验证位点与空 spool。

验证命令：

```powershell
dotnet test tests/SonnetDB.CrashTests/SonnetDB.CrashTests.csproj -c Release --no-restore /warnaserror --filter FullyQualifiedName~M43_ --logger 'trx;LogFileName=crash-source-final.trx' --results-directory artifacts/m43-local-20260930
```

结果：**5/5 通过，0 失败、0 跳过**。原始 TRX 位于 `artifacts/m43-local-20260930/crash-source-final.trx`；先前 4/4 的 `crash.trx` 为较早本地证据。真实 local process test 不等于远程拓扑、实际掉电或 168 小时 gate。

## 保留边界

- 固定读视图必须与 descriptor 位点一致；调用方须调度源捕获，在 change feed 保留期内将边界之后的连续事件写入专用 spool。增量推进前 spool 源高水位须覆盖视图位点（空源位点 0 例外）；固定视图不会自动建立，也没有后台捕获任务。
- `DocumentCollectionStore` 在文档写入的同一 KV batch 中记录 change feed 与序号；捕获器只覆盖一个实体和一个分区，不参与文档写事务，也不提供跨分区路由或远程传输。
- 仅支持一个实体专属分区，要求连续 offset。混合实体过滤、多分区快照、跨分区原子切换和远程拓扑尚未交付。
- 接收端提交先于 spool 确认。spool 必须专用，且已有确认不能提前删除接收端尚未应用的事件。
- 仅保留最后事件内容摘要供重试；历史重复事件依据持久位点在回放端过滤，不提供通用去重、冲突规则或 exactly-once。
- 每批原子替换整个有界状态文件，当前用途是本地恢复合同；吞吐、固定硬件容量和长稳尚未验证。
- 多批读取在并发增量期间不保证相同视图，调用方需核对各批位点或暂停应用增量。
- 发布尝试后的存储错误可能结果未知。当前实例 faulted 后必须重开校验，异常不能被理解为事务已经回滚。
- 固定视图创建若在原子发布后的目录 fsync 阶段失败，也可能已经留下完整视图文件且调用方尚无 descriptor；应在本次创建专用路径上调用 `OpenExisting` 校验身份、内容并取回持久 descriptor，不得直接覆盖或视为未创建。
- 无真实远程 parity、断网现场演练、固定硬件、生产安装或长期 gate 的 `PASS` 声明。

## CHANGELOG 建议

`Added`：M43 #386/#390 新增 `CdcSourceReadView` 固定文档快照、`CdcDocumentSourceCapture` 真实 change-feed 源捕获和每次有界推进的 `CdcLocalReplicaPump`；与本地 `CdcSnapshotReplica`、CDC spool 串接完成单源/单实体/单分区的快照期间增量保留、关闭重开续捕获和源/副本对账，明确保留调用方捕获调度、多分区、远程拓扑与生产门禁边界。

`Added`：M43 #390/#395 新增 5 项真实本地子进程 hard-kill/reopen 验收，覆盖文档源捕获续传与源/副本对账、部分快照、接收端提交后 spool 未确认、单尾容量回收、持久订阅未确认投递的稳定 ID 重投与确认后再次重开。
