# M43 本地持久 UTC 滑动窗口

`FileStreamingWindowAggregator` 增加显式版本 4 的滑动窗口，支持 COUNT、精确 decimal SUM/MIN/MAX 与 decimal AVG，以及已有的字符串分组。这一切片复用[持久滚动窗口](m43-persistent-windows.md)的文件 lease、状态 SHA-256、完整批次内容哈希、原子提交及提交后订阅 ACK。

```csharp
StreamingWindowDefinition definition = StreamingWindowDefinition.CreateSlidingNumeric(
    "consumer", "orders", windowSize: TimeSpan.FromMinutes(1),
    slide: TimeSpan.FromSeconds(10), numericField: "Value",
    allowedLateness: TimeSpan.FromSeconds(5));
await using var subscription = await FileStreamingSubscription.OpenAsync(
    subscriptionDirectory,
    StreamingSubscriptionDefinition.Create("consumer", "orders", batchSize: 100, capacity: 1000),
    cancellationToken: cancellationToken);
await using var windows = await FileStreamingWindowAggregator.CreateAsync(
    statePath, definition, initialCheckpoint: subscription.Checkpoint,
    cancellationToken: cancellationToken);
await windows.PumpOnceAsync(subscription, cancellationToken);
StreamingWindowNumericBatch page = await windows.ReadNumericWindowsAsync(
    maxWindows: 100, cancellationToken: cancellationToken);
```

首次创建使用 `CreateAsync`，恢复使用 `OpenAsync` 并传入相同定义和 options。仅计数使用 `CreateSliding`；字符串分组分别使用 `CreateSlidingGrouped` 与 `CreateSlidingGroupedNumeric`，通过 `ReadGroupedWindowsAsync` 读取。原来的四种滚动工厂及读取方法保留。

## 时间与重叠边界

窗口起点为 `UnixEpoch + k × slide`，成员条件为 `start <= eventTime < start + size`。size 和 slide 都必须是整数毫秒，`1 ms <= slide <= size <= 1 day`，`ceil(size / slide) <= 128`。size 不必是 slide 的整倍数，size 等于 slide 时具有滚动成员语义，仍保存显式版本 4 的定义。

例如 size 为十秒、slide 为三秒时，九秒事件属于起点 `0、3、6、9` 的四个窗口，十秒事件属于 `3、6、9` 三个窗口，零点窗口已到排他终点。负 epoch 采用向下取整；`-1 ms` 事件属于起点 `-9、-6、-3` 的三个窗口。事件时间保留原 tick 精度，不会先截断到毫秒再判断成员。

只要任一完整成员的起点或终点超出 UTC 可表示范围，整个批次就会被拒绝，不截断窗口或静默忽略边界成员。窗口成员最多枚举一百二十八项，并在每一步观察整次操作 deadline 与调用方取消。

## Watermark 与迟到

只有 `AdvanceWatermarkAsync` 才单调、持久地推进聚合器 watermark，不自动复制订阅 watermark，也不根据事件时间自行推进。关闭条件仍为 `windowEnd + AllowedLateness <= watermark`。调用方推进之前须协调事件源并处理已接受 backlog。

同一个事件可能同时属于已关闭和未关闭的成员：

- `Drop` 跳过已关闭成员，继续应用所有未关闭成员；仅当该事件的全部成员都关闭时，`DroppedLateEvents` 才增加一。该指标计完全丢弃的事件，不计跳过的窗口成员。完全丢弃时不会解析数值或分组 payload。
- `Reject` 在任何成员已经关闭时拒绝整批，之前暂存的事件和成员更新均不提交，订阅保持未确认。

例如 size 十秒、slide 五秒、允许迟到两秒，事件时间一秒属于 `[-5, 5)` 与 `[0, 10)`。watermark 七秒时只更新后者；watermark 十二秒时两个成员都关闭，Drop 记录一次完整事件丢弃，Reject 拒绝整批。

已关闭结果不会被修改或因清理后迟到事件重新创建。最近已提交批次重投先核对 delivery ID、完整内容哈希和位点，再直接返回；watermark 推进、结果清理或重开不会重复计算成员或迟到计数。

## 原子应用、容量和读取

单个事件的数值与分组键从既有的独占 payload 副本解析一次，应用到所有未关闭成员。任一成员容量不足、COUNT/SUM 溢出、精度丢失或坏 payload 都拒绝整批，窗口文件和应用位点不变。精确 decimal 输入与 SUM 的拒绝规则、AVG 除法舍入、Ordinal 分组键规则沿用[数值窗口](m43-numeric-windows.md)和[分组窗口](m43-grouped-windows.md)。

`MaxWindows` 限制保留的结果行数，包含已关闭但尚未清理的成员；分组时每个 `(start, key)` 算一行。它不限制输入事件数，也不允许超过容量时仅提交部分成员。`MaxGroups` 限制保留结果中的不同字符串键，只有某个键的全部成员被清理后才释放其容量。原有单批事件数、payload/JSON 字节数、状态 envelope 字节数与操作超时限制继续生效。

每批最多处理 `MaxBatchEvents × 128` 个成员，受同一操作 deadline 和取消令牌限制。状态扫描另受最多一万条结果限制；同步文件 I/O 不可抢占，操作 deadline 不是单次系统调用的硬中断。结果仍按起点或 `(start, Ordinal key)` 有界分页；cursor 不固定跨调用快照。

`RemoveClosedWindowsAsync` 仅删除指定起点及之前已关闭的结果，保留位点、最后批次身份和迟到计数。容量拒绝发生在提交前，实例仍可推进 watermark、清理关闭结果，再重试原 delivery ID 的未确认批次。`PumpOnceAsync` 将全部结果和位点共同提交后才 ACK 订阅；提交成功而 ACK 尚未成功时，重开后核对重投并继续确认。

## 持久兼容与验证范围

版本 4 使用现有 source-generated `StreamingWindowJsonContext`，在定义中显式保存 `slideMilliseconds`，复用 COUNT、numeric 或 grouped 状态形状。恢复按 slide 校验起点对齐，按 size 校验终点边界，核对定义、options、排序、数值状态和 SHA-256。

版本 4 恢复有界扫描所有 JSON 对象，拒绝重复属性，包括转义后相同的名称；要求基础定义、基础 options 及 slide 字段实际存在，不能省略后按默认值回填。分组模式同时要求已有分组必需字段；未分组模式拒绝夹带分组字段，即使值为 null。

版本 1～3 不增加 slide 字段，原始字段顺序和哈希保持。已有 record 主构造与 Deconstruct 签名保留，新字段为 optional init。版本 1 继续先用 legacy DTO 验证原哈希；旧版本夹带 slide 字段时明确拒绝，即使值为 null。不会将旧状态自动改成滑动窗口，也不会修改引擎的固定二进制格式。旧客户端不支持版本 4，必须保持旧定义或升级后显式创建新的状态文件。

测试文件为 `tests/SonnetDB.Core.Tests/Streaming/FileStreamingSlidingWindowTests.cs`，完整过滤器为 `FullyQualifiedName~FileStreamingSlidingWindowTests`。覆盖非整倍数与负 epoch、tick 级排他终点、COUNT/数值/字符串分组、提交前 ACK 缺口的 dispose/reopen 去重与续消费、部分关闭 Drop、任一关闭 Reject 的全批原子性、窗口/分组/状态字节容量、清理重试、decimal 溢出、UTC 极值、128 重叠边界、取消、版本 4 严格恢复与版本 1～3 的独立原始哈希 fixture。

此切片提供本地文件订阅和聚合结果的实现及恢复回归；统一构建和测试结果由本轮验证记录补充。会话窗口、远程 Frame/REST parity、跨机器租约、外部副作用事务、真实掉电、固定硬件性能和 nightly 仍需独立交付与证据。
