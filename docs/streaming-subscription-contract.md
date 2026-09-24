# Streaming 订阅与事件窗口合同

本切片（M43 #391）定义可恢复订阅所需的最小本地合同。它补齐订阅定义、检查点、事件时间 watermark、迟到策略、批量边界和至少一次投递状态；`InMemoryStreamingSubscription` 只用于测试和进程内编排。

## 持久化 DTO

`StreamingSubscriptionDefinition` 与 `StreamingSubscriptionCheckpoint` 都带 `FormatVersion`，当前版本为 `1`。`StreamingSubscriptionJson` 通过 source-generated `StreamingJsonContext` 序列化，不依赖反射 JSON 元数据。检查点包含订阅 ID、最近确认的流序号、确认时 watermark 和单调 `Revision`，可由外部 WAL/KV 以条件更新方式保存。

`AllowedLateness` 以整数毫秒持久化；创建定义时若 `TimeSpan` 含有无法整除为毫秒的 tick 会直接拒绝，避免恢复后静默改变窗口边界。

重启时先从持久层读取定义和检查点，再使用构造函数创建新的内存订阅。`FileStreamingSubscriptionCheckpointStore` 提供一个可选的本地文件实现：按订阅 ID 派生稳定文件名，使用 source-generated JSON、临时文件 `Flush(true)`、原子替换和目录 fsync；每次写入都带 `expectedRevision` 条件，旧 revision、订阅 ID 不匹配、截断或损坏 JSON 会 fail closed。不同实例通过独占 `.lock` 文件串行化条件更新，锁等待和文件读取都受固定超时/取消边界约束。

该文件存储只保存已确认检查点，不保存 Channel 中的事件、in-flight 批次或订阅定义；原子替换成功但目录 fsync 失败时提交结果可能未知，调用方必须重新读取或重开后再决定是否重试。事件流的回放、跨进程租约和 exactly-once 仍不在本合同内。

## 事件时间与迟到

调用 `AdvanceWatermarkAsync` 单调推进 UTC watermark。事件时间早于 `watermark - AllowedLateness` 时视为迟到：

- `Deliver`：照常进入队列，并将 `StreamingEvent.IsLate` 置为 `true`；
- `Drop`：不进入队列，返回 `DroppedLate`；调用方应记录该决定；
- `Reject`：抛出 `StreamingLateEventException`。

watermark 不会因事件自动推进，也不提供窗口聚合结果；窗口关闭和迟到数据的业务语义由上层决定。

## 批量、背压与至少一次

`Capacity` 是有界 Channel 的最大缓冲事件数，`BatchSize` 是单次 `ReadBatchAsync` 最多取出的事件数，且 `BatchSize <= Capacity`。缓冲区满时 `PublishAsync` 等待空间，取消令牌会中止等待，不会静默丢弃已接受事件。

读取后批次处于 `InFlight`，其 `CandidateCheckpoint` 只在 `AcknowledgeAsync` 成功后成为当前检查点。未确认批次再次读取会使用相同 `DeliveryId`、递增 `Attempt` 并标记 `Redelivered`，因此合同是至少一次，不是 exactly-once。确认结果为 `Acknowledged`，调用方应随后持久化返回的检查点；进程退出前未写入持久层的内存状态不构成耐久性证明。

关闭发布端后，已入队事件仍可读完；取消只影响当前等待的发布或读取调用。该实现没有跨进程协调、租约、分布式顺序、DLQ 或真实 change-feed 存储集成，这些能力应在后续切片和真机验证计划中单独证明。
