# M43 #391~#395 持久固定 UTC 滚动 COUNT 窗口

`FileStreamingWindowAggregator` 补充本地持久订阅的窗口聚合入口。它按事件 UTC 时间计算以 Unix epoch 对齐的固定滚动窗口，原子保存 COUNT、已应用订阅序号和 revision，再由 `PumpOnceAsync` 确认订阅。本片描述原有 COUNT 合同；2026-10-01 第三批新增显式 opt-in 的[精确 decimal SUM/MIN/MAX/AVG](m43-numeric-windows.md)，原有 COUNT 定义和状态仍可读取。分组、滑动窗口、会话窗口和远程 Frame/REST 继续待补。

## 实际调用

```csharp
var subscriptionDefinition = StreamingSubscriptionDefinition.Create(
    "order-counter", "orders", batchSize: 100, capacity: 1000);
await using var subscription = await FileStreamingSubscription.OpenAsync(
    subscriptionDirectory, subscriptionDefinition, cancellationToken: cancellationToken);
var definition = StreamingWindowDefinition.Create(
    "order-counter", "orders", windowSize: TimeSpan.FromMinutes(1),
    allowedLateness: TimeSpan.FromSeconds(5));
await using var windows = await FileStreamingWindowAggregator.CreateAsync(
    windowStatePath, definition, initialCheckpoint: subscription.Checkpoint,
    cancellationToken: cancellationToken);

await subscription.PublishAsync(new StreamingEvent(
    "order-42", 42, DateTimeOffset.UtcNow, "{\"orderId\":42}"u8.ToArray()),
    cancellationToken);
await windows.PumpOnceAsync(subscription, cancellationToken);
StreamingWindowBatch result = await windows.ReadWindowsAsync(
    maxWindows: 100, cancellationToken: cancellationToken);
```

初次创建调用 `CreateAsync`，恢复调用 `OpenAsync`，并传入完全相同的定义与容量配置。初始 checkpoint 指定只从该已确认位点之后计数，不重建该位点以前的历史结果。`CreateAsync` 不覆盖已有状态；`OpenAsync` 在状态缺失、截断、SHA-256 校验不匹配、版本不支持、位点或窗口边界无效时拒绝恢复，不重置为空。

`PumpOnceAsync` 每次最多读取一个订阅批次。批次为空且订阅已结束时返回 `false`；订阅尚未结束且暂时没有事件时可取消，并受聚合器操作超时约束。订阅与聚合器都只允许一个本地活动执行者，调用方不得另外确认该订阅中的窗口消费批次。发现订阅已确认聚合器尚未应用的事件时会拒绝继续，以免静默跳过数据。

## 提交和恢复

窗口结果与已应用位点、最近 delivery ID 和批次 SHA-256 一起保存为版本 `1` 的 source-generated JSON。写入使用固定同目录 `.pending` 侧文件、`Flush(true)`、原子 rename 和已有目录 fsync 路径。状态文件有 SHA-256 内容校验；独占 `.lock` 文件约束单机执行者。

状态路径及其 `.lock`、`.pending`、`.pending.owner` 是本 store 的保留文件合同。触碰 `.pending` 前，先刷盘包含产品标识和规范化绝对路径 SHA-256 的 `.pending.owner`，从而使终止过程中截断的待提交文件仍有明确归属。重开在独占 lease 下先校验正式状态，再仅凭有效 owner 删除这个固定待提交侧文件；后续提交复用同一路径，不按随机名字积累。owner 缺失、截断或不匹配时保留未知文件并拒绝恢复。正式状态缺失而 `.pending` 已存在时，`CreateAsync` 明确拒绝覆盖结果未知的初始提交，`OpenAsync` 仍拒绝缺失状态；不能自行删除该文件来假装恢复成功。

顺序为 **读取订阅 → 保存窗口和位点 → ACK 订阅**。窗口提交成功但 ACK 前退出时，重开聚合器和订阅，订阅重投同一 delivery ID 的批次。聚合器核对完整事件内容与最后提交位点后不再增加 COUNT，随后 ACK 回收订阅容量。相同位点的不同 ID 或内容、跳过检查点 revision、已应用前缀重复均拒绝处理。该保证只覆盖窗口文件与本地订阅之间的确定性重投，不扩展到调用方业务副作用或分布式 exactly-once。

持久化取消或 I/O 失败可能发生在 rename 后，当前聚合器会停止新操作，必须关闭并重开核对结果。容量、参数或迟到策略拒绝在写入前发生，保留聚合器原状态和订阅未确认批次，可以修正后重试。目录 fsync 的文件系统退化边界与现有 `DirectoryFsync` 保持一致；本机重开测试不替代机器掉电或网络文件系统验证。

## 时间与迟到

窗口是 `[StartUtc, EndUtc)`，以 Unix epoch 对齐；epoch 以前的事件采用向下取整，`1969-12-31 23:59:59.999Z` 在十秒窗口中属于 `23:59:50Z~00:00:00Z`。输入事件时间可乱序，流序号仍须单调递增。窗口长度为一毫秒至一天的整数毫秒，允许迟到为零至三十天的整数毫秒；完整窗口超出 UTC 可表示范围时拒绝该批次。

`AdvanceWatermarkAsync` 显式、持久、单调推进 UTC watermark。`EndUtc + AllowedLateness <= watermark` 时窗口关闭。聚合器不会自动复制订阅的当前 watermark：推进之前，调用方应先处理已经接受且可能具有旧事件时间的 backlog，并协调发布源对 watermark 的定义。订阅中的 `IsLate` 标记不单独决定聚合器处理结果，窗口是否关闭由聚合器自身 watermark 决定。

关闭窗口收到新序号事件时，`Drop` 记录累计 `DroppedLateEvents` 并提交序号，`Reject` 拒绝整个批次。已关闭结果不会被新事件修订；删除已关闭结果后，晚到事件仍按同一 watermark 拒绝或丢弃，不重新创建旧窗口。重投最后已应用批次始终先核对位点与内容，不因 watermark 变化再次执行迟到决定。

## 有界资源和消费结果

`StreamingWindowOptions` 限制保留窗口数（最多 10,000）、单批事件数（最多 10,000）、完整事件 JSON 字节数（最多 16 MiB）、状态文件字节数（最多 16 MiB）和每次操作的总超时（50 毫秒至 5 分钟）。所有状态扫描与事件处理同时受这些数量边界、操作 deadline 和调用方取消约束。同步文件系统调用本身不可抢占，deadline 不构成单次同步 I/O 的硬中断。

`ReadWindowsAsync` 按起点有界分页，`NextStartUtc` 是下次读取的排他起点，可用 `closedOnly: true` 只读取关闭结果。分页不固定跨调用快照，期间的新窗口可能影响后续页；每次返回带有对应的状态。结果保留至 `RemoveClosedWindowsAsync(throughStartUtc)` 显式持久删除，未关闭窗口不会被该操作移除。窗口数或状态字节容量已满时拒绝新的批次并保留未确认事件；消费并移除关闭结果后可以按原 delivery ID 重试。

## 本地验收

测试入口：`tests/SonnetDB.Core.Tests/Streaming/FileStreamingWindowAggregatorTests.cs`，过滤器 `FullyQualifiedName~FileStreamingWindowAggregatorTests`。覆盖乱序事件时间与精确分界、epoch 前向下取整、Pump 提交后 ACK 与排空、窗口提交后 ACK 前重开去重及续消费、变造重投拒绝、迟到关闭边界和 DROP/REJECT、窗口容量回收与原批重试、分页、调用方取消和操作超时、lease 排他、初始 checkpoint、损坏及缺失 fail closed。

2026-10-01 窗口专项 21/21、组合旅程 3/3 与全方案 Release 构建通过，见[第二批闭环记录](audits/roadmap-four-task-closure-20261001.md)。本片不宣称真机掉电、真实进程硬杀、跨机器租约、远程 parity、容量或长期运行门禁完成；对应证据仍须独立执行并记录。
