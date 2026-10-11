# M43 本地持久死信重放与条件完成

已有 `MoveExhaustedBatchToDeadLetterAsync` 在推进原订阅确认位点前保存完整原事件。本切片补充可完成的本地消费者重放流程：条件领取、业务幂等处理、条件删除完成，以及失败后重开继续领取。宿主须对这些入口实施管理员授权。

## 调用合同

`ListDeadLettersAsync` 和 `ReadDeadLetterAsync` 的摘要新增 `DeadLetterRevision`、`ReplayId`、`ReplayAttempt` 与 `LastReplayClaimedAtUtc`。空重放身份和零次数表示待领取；非空身份表示已领取且尚未完成。摘要中的目录修订号属于整个死信目录，与订阅 `StateRevision`、检查点 `Revision` 分别计数。其他批次的隔离、领取或删除也会使旧摘要条件失效。

`ReplayDeadLetterAsync(sequence, expectedDeliveryId, expectedRevision, cancellationToken)` 首次领取时持久分配 `ReplayId` 和 `ReplayAttempt = 1`，后续领取使用同一 `ReplayId` 并增加次数。返回前已提交源生成 JSON、SHA-256 校验、临时文件刷盘、原子替换和目录 fsync。事件副本保持原始稳定 ID、流序号、时间、载荷、大小写敏感事件头和迟到标记。修改副本不会改变死信数据。

成功处理全部事件后，调用 `DeleteDeadLetterAsync(sequence, expectedDeliveryId, expectedRevision, expectedReplayId, expectedReplayAttempt, cancellationToken)`，使用领取回执摘要中的全部条件。**该条件删除就是本地重放的完成步骤。** 身份、次数或目录修订号不匹配会拒绝，保留事件与领取状态。直接丢弃尚未领取批次需要传入空重放身份和零次数；已领取批次必须提供实际重放身份和次数。

```csharp
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
FileStreamingDeadLetterSummary summary =
    (await subscription.ListDeadLettersAsync(maxCount: 1, cancellationToken: deadline.Token)).Single();
FileStreamingDeadLetterReplayBatch replay = await subscription.ReplayDeadLetterAsync(
    summary.Sequence, summary.DeliveryId, summary.DeadLetterRevision, deadline.Token);

// 按原 EventId 执行业务幂等处理；业务失败时保留领取状态，供重开后重试。
await ProcessIdempotentlyAsync(replay.Events, deadline.Token);
FileStreamingDeadLetterSummary claimed = replay.Summary;
await subscription.DeleteDeadLetterAsync(
    claimed.Sequence, claimed.DeliveryId, claimed.DeadLetterRevision,
    claimed.ReplayId, claimed.ReplayAttempt, deadline.Token);
```

示例中的 `ProcessIdempotentlyAsync` 由调用方实现。领取后的业务异常不会撤销已经提交的领取；关闭并重开后列表会显示同一重放身份和已提交次数。再次领取增加次数，所有事件仍可能重复执行。业务副作用与条件删除之间没有事务；删除提交结果未知时，先重开读取批次是否仍存在，再决定后续操作。业务已成功但删除因其他目录操作而遇到陈旧修订号时，可重新读取摘要并核对重放身份/次数，使用新的目录修订号提交删除。

这套入口不会自动把旧序号重新发布到原订阅，不修改原订阅位点、暂停状态或正常未确认批次，也没有跨目录事务、分布式租约或 exactly-once 保证。

## 容量、分页与恢复

删除提交直接移除状态文件中的原记录，回收批次数和文件字节容量；死信序号采用单调高水位，全部记录被删除后重开也不会复用旧序号。分页使用 `afterSequence`，遇到删除空洞继续返回更大序号，每页最多 100 条；已删除的序号读取返回空值。保留记录最多 10,000 条、文件最多 128 MiB，具体容量沿用创建时配置。领取新增元数据若超过字节容量，会在写入前拒绝，保留原记录与修订号。

重放领取最多一百万次，达到边界时拒绝新的领取，保留最后身份、次数与事件；管理员仍可显式条件删除。每次操作和 gate 等待沿用订阅的 `OperationTimeoutMilliseconds`（默认 10 秒），遍历在已验证记录/事件数量边界内检查该操作令牌。取消发生在等待或写入前不改变状态；持久写入期间失败或取消会使当前实例停止后续操作，须关闭并重开核对结果。单次同步文件系统调用不能被取消令牌硬中断。

隔离意图尚未完成时不允许领取或删除。原隔离操作在检查点提交阶段失败后，当前实例已故障；重开先完成隔离意图、确认与 spool 回收，再允许重放。该顺序防止删除尚为原事件唯一持久副本的记录。

## 持久格式与验证边界

仅独立死信 `dead-letters/state.json` 从版本 1 升级到版本 2，增加目录 `NextSequence`、`Revision` 和逐记录的可空重放状态。原订阅状态格式、公开记录主构造函数/解构与 `OpenAsync` CLR 签名保持原合同。重开版本 1 时使用独立源生成旧 DTO 验证原规范化 SHA-256、必需字段、连续旧序号及订阅边界，再原子保存版本 2；不使用新默认字段重新解释旧哈希。

版本 2 恢复拒绝重复/未知 JSON 字段、缺少必需字段、空必需对象、错误版本或身份、无序/复用序号、低于序号高水位和保留重放次数的修订号、无效重放身份/次数、重放中的隔离意图，以及错误 SHA-256。JSON 全部使用 source-generated context，没有反射序列化入口。旧版本程序不能读取版本 2，升级后不支持直接降级打开该死信目录。

版本 2 的新增元数据需要额外字节容量。若旧文件已经贴近保存的 `MaxStoredBytes`，迁移会抛出 `FileStreamingDeadLetterCapacityException` 并保持原版本 1 文件不变。`OpenAsync` 传入更大配置仍会因已保存配置不匹配而拒绝；本切片没有扩容入口。此类目录应先用兼容版本 1 的工具读取并导出完整原事件，容量变更需另行提供显式迁移操作，不能通过自动重置目录处理。

独立回归覆盖：领取/失败重开后保留身份和事件；陈旧目录修订号、原批次身份、重放身份与次数拒绝；取消不提交；原子状态发布失败后重新领取；隔离意图恢复后领取；删除空洞分页、容量回收、全删重开序号不复用；版本 1 迁移以及合法 SHA 下的空字段、超量和版本 2 高水位/领取状态拒绝。它们是本地文件集成证据，不等同于机器掉电、网络文件系统或远程 Frame/REST parity 验收。
