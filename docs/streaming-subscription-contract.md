# Streaming 订阅与事件窗口合同

M43 #391 定义可恢复订阅所需的最小本地合同，包含订阅定义、检查点、事件时间 watermark、迟到策略、批量边界和至少一次投递状态。`InMemoryStreamingSubscription` 用于测试和进程内编排；M43 #392 的 `FileStreamingSubscription` 补充事件、未确认批次、watermark 和发布完成标记的文件持久化及重开恢复。

## 持久化 DTO

`StreamingSubscriptionDefinition` 与 `StreamingSubscriptionCheckpoint` 都带 `FormatVersion`，当前版本为 `1`。`StreamingSubscriptionJson` 通过 source-generated `StreamingJsonContext` 序列化，不依赖反射 JSON 元数据。检查点包含订阅 ID、最近确认的流序号、确认时 watermark 和单调 `Revision`，可由外部 WAL/KV 以条件更新方式保存。

`AllowedLateness` 以整数毫秒持久化；创建定义时若 `TimeSpan` 含有无法整除为毫秒的 tick 会直接拒绝，避免恢复后静默改变窗口边界。

重启时先从持久层读取定义和检查点，再使用构造函数创建新的内存订阅。`FileStreamingSubscriptionCheckpointStore` 提供一个可选的本地文件实现：按订阅 ID 派生稳定文件名，使用 source-generated JSON、临时文件 `Flush(true)`、原子替换和目录 fsync；每次写入都带 `expectedRevision` 条件，旧 revision、订阅 ID 不匹配、截断或损坏 JSON 会 fail closed。不同实例通过独占 `.lock` 文件串行化条件更新，锁等待和文件读取都受固定超时/取消边界约束。

该检查点存储单独使用时只保存已确认检查点，不保存 Channel 中的事件、in-flight 批次或订阅定义。原子替换成功但目录 fsync 失败时提交结果可能未知，调用方必须重新读取或重开后再决定是否重试。

## 文件持久订阅（#392）

`FileStreamingSubscription.OpenAsync(directoryPath, definition, options)` 是实际可调用的本地文件入口。每个订阅使用专属目录，重开时要求订阅定义与容量配置完全一致。目录中的状态文件为版本 `1`、source-generated JSON 与 SHA-256 内容校验；事件复用 `CdcEventSpool` 的版本化 JSON 包装、CRC32 帧、有界 append/replay/ack 和文件刷盘路径。事件包装使用一个订阅专属的单分区（`0`），包含完整 `StreamingEvent` 的原始字节载荷、事件头和迟到标记。

该入口保存以下状态：

- 订阅定义、文件边界、当前 watermark、最近已接受序号和待确认事件数；
- 未确认批次的 `DeliveryId`、`Attempt`、事件范围、事件数与候选检查点；
- 发布完成标记和最近已确认检查点。

`PublishAsync` 成功返回 `Accepted` 前，事件 spool 与订阅状态均已刷盘。事件序号必须严格递增；重复或倒退序号会拒绝。发布者和消费者获得的载荷不会共享可修改数组，后续重投从 spool 重读事件。`AdvanceWatermarkAsync` 成功后 watermark 可独立于事件确认恢复。

`ReadBatchAsync` 在返回批次前持久化未确认描述。未确认批次在 `DisposeAsync` / `OpenAsync` 后仍使用同一 `DeliveryId` 与同一事件范围，并增加 `Attempt`；消费者必须按稳定事件 ID 处理重复副作用。未返回到消费者的已持久投递尝试也可能使下一次成为 `Redelivered`。这里的合同是至少一次，不包含业务数据库副作用与确认之间的事务。

`AcknowledgeAsync` 先以 revision 条件提交检查点，再发布 spool ACK 并物理回收，最后更新订阅状态。重开时仅当检查点恰好等于未确认批次的候选提交结果，才会完成中断的确认；其他 revision、位点或订阅错配直接拒绝。确认可能已提交但调用未成功返回，调用方应关闭并重开后读取检查点，不能假设异常意味着回滚。append 已落盘但状态更新尚未发布时，重开保留该结果未知的事件，可能产生重复而不会静默删除。

`CompleteAsync` 持久化发布完成标记，允许读取和确认已接受事件，排空后读取返回 `null`。`DisposeAsync` 关闭文件句柄并唤醒等待调用；等待者收到关闭异常，文件数据保留用于重开。它与内存入口的关闭发布端语义不同。

```csharp
var definition = StreamingSubscriptionDefinition.Create(
    "order-consumer", "orders", batchSize: 100, capacity: 1000);
await using var subscription = await FileStreamingSubscription.OpenAsync(
    subscriptionDirectory, definition, cancellationToken: cancellationToken);
await subscription.PublishAsync(new StreamingEvent(
    "order-42-created", 42, DateTimeOffset.UtcNow, "{\"orderId\":42}"u8.ToArray()),
    cancellationToken);
StreamingDeliveryBatch? batch = await subscription.ReadBatchAsync(cancellationToken);
if (batch is not null)
{
    await ProcessIdempotentlyAsync(batch.Events, cancellationToken);
    await subscription.AcknowledgeAsync(batch.DeliveryId, cancellationToken);
}
```

示例中的稳定 ID、递增序号和业务幂等处理由调用方提供。重开后发布源读取 `LastAcceptedSequence` 并从更大序号继续发布，不能重新发布已接受的旧序号；消费端通过 `Checkpoint` 查看已确认位点，尚未确认的事件会由文件订阅回放。

### 容量与取消

文件入口的 `Capacity` 是全部未确认事件数上限，**包含 in-flight 批次**，最多配置 100,000 个事件；读取不会释放容量，确认才会回收。`BatchSize` 限制事件数，`MaxBatchBytes` 限制批次 CDC 包装正文大小；单个事件若超过批次或磁盘上限会立即拒绝，避免无法读取或永远无法满足的等待。`MaxStoredBytes` 限制 spool 帧文件，事件数或字节数满时发布等待确认释放空间，可使用取消令牌退出。

`MaxEventBytes` 默认 256 KiB，限制完整事件 JSON（包含 base64 载荷和事件头），而非仅原始载荷。单个标识最多 4 KiB UTF-8，订阅 ID 最多 256 字节，事件头最多 64 个且总字节数受事件上限约束。默认 spool 64 MiB、批次 4 MiB；状态 JSON 64 KiB、spool checkpoint 单分区、Streaming checkpoint 64 KiB 为独立有界元数据，临时原子替换期间可能短暂需要额外磁盘空间。

存储操作与闸门等待默认 10 秒，允许配置 50 毫秒至 5 分钟。重开时 spool 的 metadata 和帧恢复扫描会在读取、解析边界检查 `OperationTimeout` 和调用方取消；同步文件系统调用本身无法抢占，因此超时不是正在执行的单次同步 I/O 的硬中断。等待事件和等待容量由调用方取消控制；关闭会唤醒这些等待。取消或 I/O 失败若发生在持久化操作中，当前实例停止后续操作，须重开核对结果。取消在等待阶段发生不会接受新事件，也不会确认已有批次。

### 恢复范围

目录文件锁与 spool 写 lease 限制同一路径单执行者。该实现面向本机单进程活动执行者，支持释放文件句柄后由新的本地实例重开；没有跨机器、网络文件系统或分布式租约保证。锁文件保留于磁盘，进程退出释放句柄，后续实例可重新持有。

状态、检查点、spool 缺失、CRC/SHA-256 不匹配、截断、版本不支持或事件包装错配均 fail closed；不会回退为空订阅。不同文件的状态必须通过提交顺序与未确认批次相互验证。确定性测试模拟提交阶段中断与 dispose/reopen；本地真实子进程强杀已覆盖未确认批次重投、确认和再重开，仍不等同于机器掉电或文件系统故障验收。

2026-10-01 新增 [持久 UTC 滚动 COUNT](m43-persistent-windows.md)，通过 `FileStreamingWindowAggregator.PumpOnceAsync` 先提交窗口与已应用位点，再 ACK 订阅，重开时核对最后批次而不重复计数；[真实组合样例](../samples/SonnetDB.CdcStreamingJourney/README.md) 演示文档/CDC/订阅/窗口对账。其它聚合、远程 Frame/REST parity、更多 ACK 提交阶段的真实进程故障注入、任务列表/暂停/重试、DLQ 和持续事务化 change-feed 到流的桥接仍需后续切片。文件订阅入口保存发布者提供的事件，不自动捕获数据库变更，也不宣称 exactly-once。

## 事件时间与迟到

调用 `AdvanceWatermarkAsync` 单调推进 UTC watermark。事件时间早于 `watermark - AllowedLateness` 时视为迟到：

- `Deliver`：照常进入队列，并将 `StreamingEvent.IsLate` 置为 `true`；
- `Drop`：不进入队列，返回 `DroppedLate`；调用方应记录该决定；
- `Reject`：抛出 `StreamingLateEventException`。

watermark 不会因事件自动推进，也不提供窗口聚合结果；窗口关闭和迟到数据的业务语义由上层决定。

## 内存批量、背压与至少一次

`Capacity` 是有界 Channel 的最大缓冲事件数，`BatchSize` 是单次 `ReadBatchAsync` 最多取出的事件数，且 `BatchSize <= Capacity`。缓冲区满时 `PublishAsync` 等待空间，取消令牌会中止等待，不会静默丢弃已接受事件。

读取后批次处于 `InFlight`，其 `CandidateCheckpoint` 只在 `AcknowledgeAsync` 成功后成为当前检查点。未确认批次再次读取会使用相同 `DeliveryId`、递增 `Attempt` 并标记 `Redelivered`，因此合同是至少一次，不是 exactly-once。确认结果为 `Acknowledged`，调用方应随后持久化返回的检查点；进程退出前未写入持久层的内存状态不构成耐久性证明。

关闭内存发布端后，已入队事件仍可读完；取消只影响当前等待的发布或读取调用。内存实现没有跨进程协调、租约、分布式顺序、DLQ 或真实 change-feed 存储集成，这些能力应在后续切片和真机验证计划中单独证明。
