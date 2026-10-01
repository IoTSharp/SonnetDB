# M43 文件持久订阅的暂停、恢复与受控重试

本切片在已有文件事件 spool、至少一次投递、状态查询和投递上限之上提供可调用的本地运维入口。适用范围是 `FileStreamingSubscription` 的单目录、单执行者合同；宿主必须在调用运维 API 前完成管理员授权。本入口没有分布式租约、DLQ 或业务副作用与 ACK 的事务。

## 持久暂停和恢复

`GetStatusAsync()` 的新增 `ConsumptionPaused` 和 `StateRevision` 属性用于展示持久暂停状态与条件更新。既有状态记录的主构造和 `Deconstruct` 签名保留。`StateRevision` 与 `Checkpoint.Revision` 不同：发布事件、投递、确认、watermark、完成发布和实际运维状态变更都会递增状态修订号。暂停/恢复命令必须携带最近快照中的 `StateRevision`，陈旧值抛出 `InvalidOperationException` 并保持状态文件不变。对已处于目标状态的有效命令返回当前修订号，不重复提交。

```csharp
FileStreamingSubscriptionStatus status = await subscription.GetStatusAsync(cancellationToken);
long pausedRevision = await subscription.PauseConsumptionAsync(status.StateRevision, cancellationToken);

// 修复消费侧故障后重新查询；期间发布和 ACK 都可能推进状态修订号。
status = await subscription.GetStatusAsync(cancellationToken);
await subscription.ResumeConsumptionAsync(status.StateRevision, cancellationToken);
```

暂停成功返回后，新的 `ReadBatchAsync` 调用和已经等待事件的读取调用都不会开始新投递或重投。暂停前已经返回的批次继续有效。暂停标记关闭后重开仍保留；重开不会自动恢复消费。

| 操作 | 暂停期间的行为 |
| --- | --- |
| `PublishAsync` | 按原有序号、迟到和容量规则持久化；容量满时等待 ACK 回收 |
| `AdvanceWatermarkAsync` | 在发布未完成时仍可单调推进 |
| `ReadBatchAsync` | 有 backlog 时等待恢复，不增加 Attempt；发布未完成且为空时等待状态变化 |
| `AcknowledgeAsync` | 允许确认当前匹配的 in-flight 批次并回收空间 |
| `CompleteAsync` | 持久完成发布；有 backlog 时仍保持暂停 |
| 已完成且空的 `ReadBatchAsync` | 即使暂停也立即返回 `null`，终态优先 |
| `DisposeAsync` | 唤醒等待的读取和发布调用，调用者收到关闭异常，数据与暂停状态保留 |

读取和发布等待通过状态信号完成，不轮询磁盘或忙等待。信号捕获、替换和状态判断都在同一操作闸门内，避免错过恢复或 ACK 通知。每次读取/发布调用新增固定五分钟总等待边界，并最多重检 100,000 次信号；越界抛出 `TimeoutException`。调用者取消更早结束等待，抛出 `OperationCanceledException`；单次存储与闸门等待仍受 `OperationTimeoutMilliseconds` 限制。调用者可以在读取新状态后主动再次调用，不应将超时当成事件已丢弃或已确认。

## 耗尽批次的受控重试

`DeliveryAttemptsExhausted` 表示当前批次已经用尽 `MaxDeliveryAttempts`。`ReadBatchAsync` 的既有投递上限异常继续保留；没有自动跳过、自动确认或自动转移事件。

管理员排查消费失败原因后，使用 `ResetDeliveryAttemptsAsync(deliveryId, expectedRevision, expectedAttempt)` 明确重新授权该批次的一轮投递预算。命令必须同时匹配当前 `DeliveryId`、状态修订号和投递次数，而且当前批次必须已经耗尽。任何不匹配、陈旧命令或未耗尽批次都被拒绝，不修改磁盘状态。修订号还防止同一批次多次耗尽/重置后计数相同造成的陈旧命令误命中。

```csharp
FileStreamingSubscriptionStatus blocked = await subscription.GetStatusAsync(cancellationToken);
if (blocked.DeliveryAttemptsExhausted)
{
    await subscription.ResetDeliveryAttemptsAsync(
        blocked.InFlightDeliveryId!, blocked.StateRevision, blocked.InFlightAttempt, cancellationToken);
}

// 重置不会恢复暂停的消费。按最新快照显式恢复，再读取同一批次。
FileStreamingSubscriptionStatus latest = await subscription.GetStatusAsync(cancellationToken);
if (latest.ConsumptionPaused)
    await subscription.ResumeConsumptionAsync(latest.StateRevision, cancellationToken);
StreamingDeliveryBatch? retry = await subscription.ReadBatchAsync(cancellationToken);
```

重置保存 `Attempt = 0`，保留原有 `DeliveryId`、事件范围、事件 ID、事件载荷和候选检查点。该状态可关闭和重开恢复。后续第一次读取计数为 `1`，状态仍为 `Redelivered`，不会伪装成首次投递。重置不会改变确认位点、释放空间或恢复暂停状态。状态中的计数表示最近授权轮次的预算，不是生命周期累计投递次数；审计记录和授权主体由宿主保留。

稳定批次标识和事件 ID 要求消费者处理重复副作用。原有 `AcknowledgeAsync(deliveryId)` 按稳定批次标识确认；重置没有新增面向不同消费者的 ACK fencing，也没有 exactly once 保证。存储提交结果未知时仍须关闭、重开、查询状态后决定下一步。

## 状态文件 v3 与兼容迁移

`subscription.json` 状态格式由 v2 升为 v3，新增必需字段 `consumptionPaused` 和 `stateRevision`。事件 spool、确认检查点 DTO 和已发布数据库二进制格式保持各自版本。新状态采用现有严格 source-generated JSON、SHA-256 内容校验、有界读取、临时文件刷盘和原子替换路径。

v1 和 v2 均先按各自独立的 source-generated 模型严格解析并校验其原格式 SHA-256，再迁移。未知字段、缺失必需字段、损坏哈希、不受支持版本和无效投递状态均 fail closed，不以新模型的默认字段绕过旧哈希验证。v1 使用原有默认投递上限 100；v2 保留已保存的自定义上限。迁移初始为未暂停、状态修订号 0，并保留 watermark、完成标记、事件范围和未确认批次；后续状态提交开始递增。

迁移成功后写入 v3；旧实现不能重开 v3 状态，回退版本须先处理这一存储兼容边界。发生目录 fsync 或原子提交结果未知时，不能依据异常假设回滚。

## 验证范围

`FileStreamingSubscriptionOperationsTests` 覆盖暂停重开、暂停期间发布与 watermark、恢复唤醒、暂停期间 ACK、完成且为空的终态读取、取消和关闭等待、耗尽批次重置重开、稳定批次重投、不匹配/陈旧命令不改变状态、重复重置防陈旧命中，以及 v1/v2 的哈希迁移和损坏拒绝。测试属于本地文件合同证据，不代替远程用户旅程、实机生产门禁或完整 M43 验收。
