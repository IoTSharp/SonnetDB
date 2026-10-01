# M43 本地持久分组窗口

`FileStreamingWindowAggregator` 的显式版本 3 支持固定 UTC tumbling 窗口内的字符串分组 COUNT，以及可选的精确 decimal SUM/MIN/MAX 和 decimal AVG。此切片复用既有本地订阅、重放内容哈希、原子状态提交与提交后 ACK 合同。

```csharp
StreamingWindowDefinition definition = StreamingWindowDefinition.CreateGroupedNumeric(
    "consumer", "orders", TimeSpan.FromSeconds(10), "Device", "Value");
var options = new StreamingWindowOptions
{
    MaxWindows = 2_000,
    MaxGroups = 100,
    MaxStateBytes = 2 * 1024 * 1024,
};
await using var windows = await FileStreamingWindowAggregator.CreateAsync(
    "device-windows.json", definition, options: options);
await windows.ApplyBatchAsync(delivery); // 先将结果和位点共同刷盘；订阅 ACK 由调用方负责。
StreamingGroupedWindowBatch page = await windows.ReadGroupedWindowsAsync(maxWindows: 50);
StreamingGroupedWindowBatch next = await windows.ReadGroupedWindowsAsync(
    maxWindows: 50, after: page.NextCursor);
```

仅计数时使用 `CreateGrouped`。与持久文件订阅组合时使用 `PumpOnceAsync`，它在状态提交后确认订阅；提交成功而 ACK 尚未成功的最近批次，重开后按投递标识、位点与完整事件 SHA-256 核对，结果不重复累加。重投内容改变或订阅已确认超过窗口位点时会拒绝继续。

## 键与精度

- 分组属性必须是显式选择的顶层 JSON string，属性名按 Ordinal 匹配；JSON 转义后的等价属性名视为同一个选择，因此重复选择属性会被拒绝。
- 键原值按 Ordinal 比较，保留大小写、空白和 Unicode 序列，不做语言区域比较、大小写折叠或 Unicode 归一化。空字符串有效；键最多 256 个 UTF-16 字符。
- 属性缺失、null、非 string、重复或键超长会拒绝整个批次，之前已应用状态和订阅 ACK 均保持原位点。数值属性必须与分组属性不同。
- 显式数值模式保留已有 decimal 精度合同：输入数字和 SUM 必须可以精确表示，SUM 溢出或需要舍入会拒绝整批；AVG 使用 decimal 除法，除不尽时会舍入。不同键的 SUM 分别计算。

## 容量、分页与清理

`MaxWindows` 限制保留的 `(windowStart, groupKey)` 结果行总量，包含未清理的已关闭结果；`MaxGroups` 限制这些行中不同键的数量。两者各自最多为 10,000。仅当某个键的全部保留结果都已清理时，该键才释放分组容量。容量判断覆盖整个批次，不会仅提交一部分事件。

显式提供 options 时必须为分组窗口设置 `MaxGroups`；未提供 options 时默认分组上限为 1,000。`MaxBatchEvents`、`MaxBatchBytes`、`MaxStateBytes` 和操作取消/超时边界继续生效，状态字节限制包括最终带校验和的 UTF-8 envelope。字节超限发生在磁盘提交前，当前实例仍可用于重试。

`ReadGroupedWindowsAsync` 使用 `(StartUtc, GroupKey)` 排他 cursor，先按起点再按 Ordinal 键排序。即使同一时间窗口有多个组，分页也不会遗漏同起点的后续组。`StreamingWindowState.RetainedWindowCount` 在分组模式表示保留结果行数。数值字段在 COUNT 模式均为空。原未分组读取方法拒绝读取分组状态。

每页取得该次读取的一致状态；cursor 不固定跨页快照。调用方需要跨页一致结果时，应暂停应用、watermark 推进和清理。`closedOnly` 的继续读取必须保持相同筛选条件。

显式单调推进 watermark 才会关闭窗口，关闭边界为 `windowEnd + allowedLateness <= watermark`。`RemoveClosedWindowsAsync` 删除指定起点及以前的全部已关闭分组结果。它保留位点、最后批次去重身份和迟到丢弃计数；已清理的关闭窗口不会被重投或新的迟到事件重新创建。Drop 策略在判定迟到后直接记录丢弃数量，不再解析已丢弃事件的键或数值；Reject 策略拒绝整个批次。

## 状态兼容及证据范围

分组状态必须通过 `CreateGrouped` / `CreateGroupedNumeric` 显式创建，保存定义与 options，并在版本 3 的 `groupedWindows` 数组中保存已排序结果。它不能将版本 1 COUNT 或版本 2 numeric 状态自动迁移为分组状态，也不能以旧读取接口隐藏分组结果。

未分组版本的新增分组字段均省略，原字段顺序和哈希合同保持不变；版本 1 仍先按原始 DTO 验证旧 SHA-256。版本 1/2 状态若夹带 `groupField`、`groupedWindows` 或 `options.maxGroups`，即使值为 null 也明确拒绝。版本 3 在反序列化前有界扫描全部 JSON 对象，拒绝重复属性，包括转义后相同的名称，不能以最后属性覆盖加合法哈希绕过校验。版本 3 同时要求持久 definition 的六个基础字段与 `groupField`、options 的五个基础字段与 `maxGroups` 实际存在，不能在字段省略后按默认值回填恢复；`numericField` 保持可选。恢复继续核对定义、容量、文件哈希、排序唯一性、时间边界、COUNT 和数值状态。

验证范围是本地真实文件订阅与窗口状态：分组与 Ordinal 顺序、同起点分页、全批拒绝、数量/字节容量、迟到、清理后重投、提交后未 ACK 重开、精确 decimal，以及旧版本原始哈希恢复。滑动窗口、跨节点执行与租约、远程订阅/CDC、外部副作用事务、固定硬件性能报告和 nightly 证据仍未完成，不能由这些本地测试替代。
