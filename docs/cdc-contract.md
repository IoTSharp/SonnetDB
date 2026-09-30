# CDC 版本化事件合同

M43 #385 的首批本地合同定义跨进程传输的单事件格式。实现位于 `SonnetDB.Cdc.CdcEventCodec`，使用手写 `Utf8JsonWriter`/`JsonDocument`，因此不依赖反射 JSON 元数据。M43 #386~#390 的首个本地存储切片位于 `SonnetDB.Cdc.CdcEventSpool`：它把事件写入带 magic/version/长度/分区位点/CRC32 的固定帧，并提供有界回放、确认和重开校验。

## 格式

事件使用固定字段和严格对象结构：

```json
{
  "eventId": "evt-1",
  "source": "db-a",
  "entity": "documents",
  "key": "doc-7",
  "sequence": 42,
  "occurredAtUtc": "2026-09-22T08:30:00+00:00",
  "metadata": {
    "contractVersion": 1,
    "schema": "documents",
    "schemaVersion": 1,
    "operation": "update",
    "checkpoint": { "partition": 2, "offset": 99 }
  },
  "before": { "value": 1 },
  "after": { "value": 2 }
}
```

`contractVersion` 和 `schemaVersion` 当前都为 `1`。未知字段、重复字段、未知 operation、未知版本和非 UTC 时间会 fail closed；接收方不能把未知版本降级为当前版本。

## 有界规则

- 单事件 UTF-8 总大小最多 1 MiB；`eventId`、`source`、`entity`、`key` 和 `schema` 各最多 4 KiB UTF-8 字节。
- `before`/`after` 各最多 256 KiB，必须是有效 JSON，最大嵌套深度为 32，不接受注释或尾逗号。
- `sequence`、`partition` 和 `offset` 必须非负；同一分区的 checkpoint 由上层保证单调。
- `insert` 不得带 `before`，`delete` 不得带 `after`；`update` 可同时带两者。
- `ReadAsync`、`WriteAsync` 使用取消令牌和有界缓冲，输入超限或流为空时返回合同错误。

## Append-only spool 首切片

`CdcEventSpool` 使用单个 `.lock` lease 约束同一路径的写者；实例内的 append、回放和确认由单写者闸门串行化。`MaxEvents`、`MaxBytes`、`MaxPartitions` 和每批回放上限在打开及运行时都受校验。metadata v2 会在写帧前以 CRC32 原子发布每个分区的 append high-watermark（未确认时 `acknowledged = -1`），再写入并 `Flush(true)` 固定帧；这样掉电、截断或数据文件丢失时，重开会因缺少未确认帧而 fail closed。确认位点同样先发布 metadata，再重写数据文件；重开时会重新扫描每个帧并校验正文 checkpoint、帧 CRC 和 high-water mark。现有 metadata v1 仍可读取。追加取消或 I/O 失败会尝试把 partial tail 截回，但由于 append intent 已持久化，实例会进入 faulted 状态，必须关闭并重开核对结果。

该 spool 只提供单进程编排可使用的 durable append/replay/ack 边界，不提供复制拓扑、冲突解决、schema migration 或 exactly-once。目录 fsync 失败发生在原子替换之后时，提交结果可能未知，调用方应停止当前实例并重开核对文件；不能把异常当作已回滚。

## 本地快照与增量接收端

M43 #386 新增 `CdcSnapshotReplica`，提供单源、单实体、单 schema、单分区的本地持久接收端。调用方必须先取得固定读视图及其准确总行数，并确保该读视图包含所有不大于 `CdcSnapshotDescriptor.Checkpoint` 的变更。文档集合写入路径已持久记录 change feed；调用方负责建立固定读视图、调度源捕获并把边界之后的事件保留在独立 `CdcEventSpool` 中，避免复制期间的写入丢失。

1. `CreateAsync(path, descriptor)` 原子创建初始状态，已有状态不覆盖；`Open(path, expectedDescriptor)` 重开时严格校验快照 ID、源、实体、schema、行数和 checkpoint。状态缺失不会被替换为空状态。
2. `WriteSnapshotPageAsync(expectedRowOrdinal, rows)` 要求非空页面、稳定键的序数顺序跨页严格递增，以及页起始序号等于持久进度。重开后从 `GetState().SnapshotRowsCopied` 和 `LastSnapshotKey` 继续固定读视图。重复页、键重复、页序号错配和超出声明行数都失败，进度不变。
3. `CompleteSnapshotAsync()` 仅在全部声明行数已经持久化后切换阶段；空快照可直接完成。该操作可重复。快照未完成时不能读取物化结果或应用增量。
4. `ApplyIncrementalAsync(events)` 原子保存整批物化行和应用位点。事件必须来自 descriptor 指定的源、实体、schema 和分区，offset 从固定边界开始连续加一。插入/更新必须提供完整 `after` JSON，覆盖对应业务键；删除移除该键。缺口、倒退、错配和容量超限不推进状态。
5. 只有最后一个已应用事件允许按完全相同的规范编码内容重复提交；内容摘要不一致会失败。历史重复事件应根据已应用位点在生产端过滤，不能据此宣称通用去重或 exactly-once。
6. `ReadRowsAsync(afterKey, maxRows, maxBytes)` 返回有界、按键排序的物化行和该批次的 `AppliedCheckpoint`。第一行无法容纳时抛出异常，不返回无法推进的空 continuation。并发增量期间的多个读取批次不是固定快照，调用方应核对各批位点一致或暂停增量应用。

本地衔接可调用 `CdcLocalReplicaPump.AdvanceAsync(view, spool, replica, maxRows, maxBytes)`，每次只执行以下一步：复制固定视图的一页、在行数完整后切换快照阶段，或在增量阶段补确认已物化前缀并应用/确认一批 spool 事件。同一接收端的调用在本进程内串行化；共享同一 spool 对象的源捕获与泵操作也在本进程内串行化，避免捕获器扫描未确认帧时与 ACK 交错。调用方需重复调度此方法直到达到所需位点，并持续调用源捕获器。本方法不创建后台任务，也不驱动 `CaptureAsync`。

进入增量阶段时，专用 spool 的源高水位必须至少覆盖固定视图的 descriptor checkpoint；位点 0 且空 spool 是合法空源。若源捕获尚未追到该边界，泵拒绝应用增量。接收端持久状态先于 spool ACK；ACK 中断后重试会先核对并补确认已物化前缀，再读取下一批。调用方设置的 `maxRows`/`maxBytes` 同时约束一次快照页或增量批次。等价的手动衔接顺序为：

```csharp
CdcSnapshotReplicaState state = replica.GetState();
long acknowledgedOffset = spool.AcknowledgedCheckpoints.GetValueOrDefault(
    state.AppliedCheckpoint.Partition, -1);
if (acknowledgedOffset > state.AppliedCheckpoint.Offset)
    throw new InvalidDataException("CDC spool 已确认位点超出接收端物化位点。");
if (acknowledgedOffset < state.AppliedCheckpoint.Offset)
{
    if (spool.EventCount > 0)
        await spool.AcknowledgeAsync(state.AppliedCheckpoint);
    else if (state.AppliedCheckpoint.Offset != state.Descriptor.Checkpoint.Offset)
        throw new InvalidDataException("CDC spool 缺少尚未确认的已物化事件。");
}
CdcEventSpoolBatch batch = await spool.ReplayBatchAsync(state.AppliedCheckpoint);
if (batch.Events.Count != 0)
{
    state = await replica.ApplyIncrementalAsync(batch.Events);
    await spool.AcknowledgeAsync(state.AppliedCheckpoint);
}
```

接收端状态提交必须先于 spool 确认。若提交成功后确认失败，重开后须先对照 spool ACK 补确认已物化前缀，再按持久 `AppliedCheckpoint` 回放后续事件；即使没有后续事件也必须补确认，否则已满的 spool 无法继续接收。空 spool 且接收端已经越过快照位点、却没有相应 ACK 时视为状态缺失并拒绝继续。不能在接收端应用前确认；spool 必须专用于该接收端，不能由其他消费者提前确认删除事件。

## 文档集合源捕获与固定读视图接线

本地首个真实源端切片由 `CdcDocumentSourceCapture` 和 `CdcSourceReadView.CaptureDocumentCollectionAsync` 提供，覆盖单源、单文档集合、单分区：

1. `DocumentCollectionStore` 在同一个 KV batch 中提交主文档、change feed 记录和集合序号。`AcquireCdcReadSnapshot()` 在集合写锁内取得 `KvReadSnapshot` 及该序号，固定视图只读取这份快照；快照建立后的写入不会进入视图。
2. `CaptureDocumentCollectionAsync` 对稳定快照计算准确行数，按文档业务键排序后原子发布 `CdcSourceReadView`。视图文件包含 source/entity/schema/row count/checkpoint，重开严格校验身份和 SHA-256；读取页的位点始终是创建时 checkpoint。
3. 调用方配置专用 `CdcEventSpool`，持续调用 `CdcDocumentSourceCapture.CaptureAsync`，并确保源 change feed 在捕获前仍保留从恢复游标起的完整连续记录。文档 change feed 的保留期为 7 天；过期缺口不能用当前位点跳过，必须重新建立快照。捕获器从 spool 的 ACK 和未确认帧恢复连续 source sequence，把 change feed 的 insert/update/delete 转换为版本化 `CdcEvent`；载荷截断、身份错配、序号缺口和未来 ACK 都 fail closed。事件帧成功 durable append 后才推进捕获游标。
4. 副本先写完视图并 `CompleteSnapshotAsync`，再按 descriptor checkpoint 回放 spool；`CdcSnapshotReplica` 提交物化状态后才确认 spool。调用方在快照复制期间持续捕获，确保边界之后的写入在源 change feed 到期前进入 spool。

本切片的重启回归关闭并重开 `Tsdb`、视图、spool 和副本对象，验证 spool high-watermark 后续捕获及源/副本结果对账。真实 Child `Process.Kill` 回归进一步覆盖源捕获、部分快照、增量物化后未 ACK 和容量回收；这些本机结果不代表真机掉电或远程拓扑。自动捕获调度、多分区、远程传输、冲突/schema 演进、离线同步和 exactly-once 仍未承诺。

固定视图创建在临时文件刷盘后原子发布，并对目录执行 fsync。若发布尝试后的目录同步报错，调用方不能把创建异常解释为视图不存在。`CaptureDocumentCollectionAsync` 可能尚未返回 descriptor，因此调用方应使用本次创建专用的路径，以 `CdcSourceReadView.OpenExisting(path, expectedSource, expectedEntity, expectedSchema, expectedSchemaVersion, expectedPartition, expectedSnapshotId)` 校验已有文件并读取其持久 `Descriptor`（包含准确行数和 checkpoint）。`expectedSnapshotId` 可省略；已知时传入可进一步约束身份。文件缺失、损坏或身份不匹配均不能被当作成功恢复；确认已有视图后可继续沿用严格的 `Open(path, expectedDescriptor)`。创建接口不覆盖既有视图。

### 容量与恢复边界

- 默认物化行数 10,000，状态文件 16 MiB，批次 1,000 条/4 MiB；绝对物化行上限 100,000，状态文件上限 256 MiB。单行 JSON 和键复用 CDC 的 256 KiB/4 KiB UTF-8 边界，JSON 嵌套深度最多 32。
- 状态文件使用独立的 `SDBCSR01`/version 1 格式，48 字节头部记录正文长度和 SHA-256；正文通过独立 source-generated `CdcSnapshotReplicaJsonContext` 序列化。没有修改既有 spool 或数据库文件格式。
- 每批替换整个有界状态文件。临时文件 `Flush(true)` 后原子发布，再要求目录 fsync；复制页、物化行、阶段与位点同时发布。发布前取消或容量失败保留旧状态；发布尝试之后的 I/O 错误可能结果未知，实例进入 faulted 状态，必须关闭并重开核对。
- 同一路径通过独占 `.lock` lease 保证一个接收端实例；实例内操作串行。重开校验完整状态长度、SHA-256、schema、阶段、行数、键和位点。
- 该接收端要求实体专属分区的连续 offset。不能把混合实体的分区过滤后直接应用，否则缺口会被拒绝。多分区切换、自动捕获调度、重新快照、远程传输、冲突解决、schema migration 和固定硬件容量仍属于后续切片。

## 尚未承诺的能力

已有快照/增量衔接仅覆盖上述本地文档源和接收端边界。离线队列、自动捕获调度、多分区/远程衔接、冲突解决、schema migration、客户端路由和复制拓扑仍归入 M43 #386~#390 的后续切片；远程 parity、断网现场恢复和固定硬件容量属于后续验证计划。

定向回归：`dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --filter FullyQualifiedName~CdcEventSpoolTests`（当前 19 项）及 `FullyQualifiedName~CdcEventCodecTests`。

快照接收端回归：`dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --filter FullyQualifiedName~CdcSnapshotReplicaTests`；包含快照中途重开、spool 增量/确认/重开衔接、空快照、取消、容量、身份/schema/checkpoint 错配、重复事件和损坏状态。

真实进程恢复场景：`dotnet test tests/SonnetDB.CrashTests/SonnetDB.CrashTests.csproj --filter FullyQualifiedName~M43_`，复用既有 Child Program / `Process.Kill` 框架，覆盖源捕获续传、部分快照和增量已物化但 spool 未确认时的强杀恢复。结果与边界见 [M43 本地接收端报告](audits/m43-cdc-snapshot-20260930.md)。
