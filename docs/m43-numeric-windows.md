# M43 持久固定 UTC 数值窗口

`FileStreamingWindowAggregator` 在既有 COUNT 聚合器上提供显式顶层 JSON 数值属性选择，同时维护 COUNT、SUM、MIN、MAX、AVG。时间对齐、watermark、迟到策略、批次 SHA-256 重投核对、原子状态提交与提交后 ACK 的顺序沿用[持久 COUNT 窗口](m43-persistent-windows.md)。

## 调用入口

```csharp
var subscriptionDefinition = StreamingSubscriptionDefinition.Create(
    "meter-reader", "meters", batchSize: 100, capacity: 1000);
await using var subscription = await FileStreamingSubscription.OpenAsync(
    subscriptionDirectory, subscriptionDefinition, cancellationToken: cancellationToken);
var definition = StreamingWindowDefinition.CreateNumeric(
    "meter-reader", "meters", TimeSpan.FromMinutes(1), numericField: "Reading",
    allowedLateness: TimeSpan.FromSeconds(5));
await using var windows = await FileStreamingWindowAggregator.CreateAsync(
    statePath, definition, initialCheckpoint: subscription.Checkpoint,
    cancellationToken: cancellationToken);
await subscription.PublishAsync(new StreamingEvent(
    "reading-1", 1, DateTimeOffset.UtcNow, "{\"Reading\":0.1}"u8.ToArray()),
    cancellationToken);
await windows.PumpOnceAsync(subscription, cancellationToken);
StreamingWindowNumericBatch results = await windows.ReadNumericWindowsAsync(
    maxWindows: 100, cancellationToken: cancellationToken);
```

`CreateNumeric` 是 opt-in。已有 `Create`、`ReadWindowsAsync` 以及 public record 主构造与解构签名保留。数值定义也可用 `ReadWindowsAsync` 读取 COUNT；COUNT 定义调用 `ReadNumericWindowsAsync` 会拒绝。

选择器是最长 256 字符的非空顶层属性原名，按 `Ordinal` 匹配。`"Reading"` 不匹配 `"reading"`，属性内的 JSON 转义会先解码。选择器没有路径、数组、分组或 SQL 表达式语义。

## 精度和坏数据合同

每个未被迟到策略丢弃的事件必须包含完整 JSON 对象，并且所选顶层属性必须恰好出现一次，其值必须为 JSON number。缺失、`null`、字符串、布尔、对象、数组、重复所选属性、无效 JSON 或多个根值都会以 `InvalidDataException` 拒绝整个批次，保留窗口文件、已应用位点和订阅未确认批次。无关属性可以是任意合法 JSON，最大 JSON 深度为 64。

数字 token 最长 128 UTF-8 字节，指数绝对值最大 1000。符号、指数和尾随零归一化后，必须能够精确写入 decimal 的 96 位系数和 0～28 位小数；通过整数系数检查再构造 decimal，不使用会静默舍入过长精度的 `GetDecimal` 转换。`1e-29`、超出 decimal 范围和有 29 位非零小数的值拒绝，`1.00000000000000000000000000000` 可精确归一化为 `1`。

SUM 按整数系数对齐小数位并精确累加，不经二进制浮点数。如果任一事件应用后的总和无法用 decimal 精确表示，以 `OverflowException` 拒绝整个批次。这也包括 decimal 常规加法可能舍入的情况，例如 `decimal.MaxValue + 0.1`。累加按批内事件顺序进行；即使后续数值理论上能够抵消溢出，当前批次仍拒绝。MIN、MAX 保留精确 decimal 值。

AVG 为精确 SUM 除以 COUNT 的 decimal 结果；不能有限表示时采用 .NET decimal 除法舍入，例如 `1 / 3` 返回 `0.3333333333333333333333333333`。AVG 不承诺有理数精确性。SUM 的拒绝策略不会因只查询 MIN/MAX 而改变。

迟到 `Drop` 在解析 payload 之前执行，因此已经关闭窗口中的坏 payload 仍可丢弃、累计 `DroppedLateEvents` 并确认。迟到 `Reject` 则保持整个批次未应用。最后已提交批次的重投先核对原批次哈希与位点，不重复累计数值，也不因 watermark 或结果删除而再执行迟到策略。

## 状态兼容和容量

数值定义与状态使用版本 2，新增持久 `numericField` 和与 COUNT 窗口一一对应的 `numericWindows`（SUM/MIN/MAX）。COUNT 仍保存版本 1 原字段形状；加载版本 1 时使用独立 legacy source-generated DTO，先按原字段顺序重新编码并校验原 SHA-256，然后恢复内存状态。不会补字段后再计算旧哈希，不会将旧 COUNT 状态自动改成数值定义，也不会重写为版本 2。版本 1 夹带新增数值字段、坏哈希或不匹配定义拒绝恢复。

所有生产 JSON DTO 使用 source-generated context。数值选择使用 `Utf8JsonReader`，未引入运行时依赖、反射序列化或 unsafe。此处版本升级是窗口 JSON 状态合同，没有修改引擎固定二进制结构。

应用入口先按事件数和原 payload 累计字节上限检查，再为每个事件复制独占 payload 和最多 64 个事件头。批次哈希与数值解析始终使用同一份副本，因此调用方继续修改数组或字典不会造成结果与重投哈希不一致。事件列表按预先校验的数量逐项获取，事件头枚举也有固定数量边界；复制和枚举受同一操作 deadline 与取消令牌约束。

窗口数量、批次数量、批次 JSON 字节、状态文件字节与操作超时沿用 `StreamingWindowOptions`；数值记录也计入同一个状态字节上限。结果按起点有界分页，`NextStartUtc` 为排他起点；`closedOnly` 过滤已关闭窗口。`RemoveClosedWindowsAsync` 在同一次原子提交中移除 COUNT 与数值记录，保持位点、重投身份与迟到计数。分页跨调用不冻结快照。

## 验证范围

测试文件为 `tests/SonnetDB.Core.Tests/Streaming/FileStreamingNumericWindowTests.cs`，过滤器为 `FullyQualifiedName~FileStreamingNumericWindowTests`。覆盖真实文件订阅 Pump/ACK、精确小数与指数、负 epoch、COUNT 结果兼容、状态重开、提交后未 ACK 重投、坏数据全批原子拒绝、SUM 范围溢出与精度丢失、AVG 舍入、分页、容量回收、DROP/REJECT、取消、独立原版本 1 哈希 fixture 恢复，以及事件头枚举修改原 payload 时的哈希与数值快照一致性和累计复制容量拒绝。

本片验证本地文件订阅与窗口结果的恢复边界，不宣称远程 Frame/REST parity、分组/滑动/会话窗口、真实掉电或固定目标硬件性能门禁完成。构建和测试结果由本轮统一验证记录补充。
