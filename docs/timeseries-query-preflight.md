# 时序 Query API 与建模预检（M36 #315）

`TimeSeriesQueryBuilder` 已有的 range、aggregate、window、gap-fill 和 TSQ001～004 保持原合同。新增的嵌入式 `Preflight` 读取真实 schema、series/tag 目录、实际数据库 retention 配置，以及目标序列/字段/时间范围内的有界原始点样本。所有原始点仍通过现有 `QueryEngine` 的快照、合并、墓碑和排序路径取得，不创建第二套查询引擎。

```csharp
var report = database.ForSeries(seriesId, "temperature")
    .Between(fromTimestamp, toTimestamp)
    .Window(60_000, Aggregator.Avg)
    .Preflight(new TimeSeriesPreflightOptions
    {
        MaxSamplePoints = 256,
        MaxSourcePoints = 8192,
        MaxSources = 256,
        MaxSourceBytes = 8 * 1024 * 1024,
        SeriesWarningThreshold = 100_000,
        TagValueWarningThreshold = 10_000,
    }, cancellationToken);

string json = JsonSerializer.Serialize(
    report, TimeSeriesQueryJsonContext.Default.TimeSeriesPreflightReport);
```

预检不会执行聚合、补桶、flush、retention、修复或 schema 修改。原始点 `Limit` 不影响预检的样本预算；按配置方向检查最多 `MaxSamplePoints` 个点，并最多额外读取一个点来判断截断。聚合与 gap-fill 的范围仍是原始点预检的范围。

## 证据与边界

| 报告 | 证据来源 | 解释边界 |
|---|---|---|
| `HasSchema`、`DeclaredFieldType` | 当前 measurement 的真实 `MeasurementCatalog` | Schema 可以来自显式创建或写入推导，不能从存在性推断它由用户显式创建。空数据不能证明字段不存在。数值聚合允许 Float64/Int64/Boolean，Count 支持其他类型；Float64 schema 允许历史 Int64 点。 |
| `MeasurementSeriesCount`、`Tags` | 真实 `TagInvertedIndex` 中现成集合的 Count | 测量级全部登记序列和 tag 不同值数量，包含多个 series；不是 retention 内活跃基数或生产容量结论。只返回有限 tag 项，超过时 `TagsTruncated=true`。并发写入/删除期间各计数分别观察，不是跨组件事务快照。 |
| `RetentionEnabled`、`RetentionTtl/Now/Cutoff` | 当前 `TsdbOptions.Retention` 与本次真实 `NowFn()` | 优先使用 `TtlInTimestampUnits`，否则按毫秒换算实际 TTL。时间戳严格小于 cutoff 才过期。禁用时不调用时钟；无效 TTL 或减法下溢不伪造 cutoff。时间单位仍由调用方与数据库配置负责一致。 |
| `SampledPoints`、`ObservedFieldTypes` | 现有查询引擎输出的可见原始点 | 仅覆盖单个目标 series/field/range；不会借一个 series 推断整个 measurement 的所有实际类型。合法同时间戳覆盖和乱序写入经原引擎处理，不视为坏点。 |
| `NonFinitePoints`、`SchemaMismatchPoints`、`ExpiredPoints` | 实际样本中 Float64 非有限值、类型不符、严格早于 cutoff 的点数 | 不返回业务原始值，不检查所有向量分量或推测传感器值域；超过 TTL 的可见点表示异步清理尚未使其不可见。 |

`DataChecked=false` 表示没有完成数据检查；`SampleComplete=false` 表示不能断言已检查完整目标范围。即使 `SampleComplete=true`，结论也只对应本次目标范围和引擎快照。`HasErrors=false` 不能代替“全量健康”；调用者必须同时查看证据数量、完整性和提示。

直接通过 `QueryEngine.ForSeries` 创建的构建器没有数据库 schema/retention 上下文，相关字段明确为 null 并返回 TSP001。它仍可检查真实原始点；不会套用默认数据库配置。已知数据库内不存在的 series 保留 TSQ001，且不读取数据。

## 有界工作

仅设置输出 `LIMIT` 不足以限制诊断工作，因此采样在同一查询执行路径添加独立的预算准入：

- 在逐个 reader 获取租约之前，检查当前已加载段数；段快照变化重试时重新检查且传播取消。
- MemTable 的点数/估算字节预算检查与快照在同一个桶锁内完成。乱序桶可能全桶排序，因此按全桶计费，即使查询范围很窄。
- 从现成 `(series, field)` 段索引直接获取无复制列表；先检查索引项数量，再遍历范围候选。不调用会全量分配候选列表的诊断捷径。
- 候选块按完整块点数和压缩块字节计费，因为部分范围也可能解码整个块。MemTable 与候选块共用累计点数/字节预算。
- 墓碑列表不复制，过滤前检查数量；段、内存表、块索引项和墓碑的各自上限由 `MaxSources` 控制。
- 目录基数不调用 `Catalog.Snapshot()`、`Find()`、并发字典 `Keys/Values` 或全量 `ToArray()`；只读取已维护的集合计数并枚举有限 tag 项。

硬上限：样本 4096 点、源数据 65536 点、各类来源/索引项 4096、源字节 64 MiB、tag 项 256。所有参数都有下限与上限校验。超过预算返回 TSP010，不自动加预算，不悄悄取一个未经完整合并的子源样本。保守准入可能拒绝一个最终输出很小的查询，调用者可缩小时间范围或转入专门的离线验证。

`MaxSourceBytes` 计量段的压缩块长度和 MemTable 的现有字节估算，不承诺总解码堆内存硬上限。Tag 预算保证有限枚举并对已选项排序，不承诺截断时选出全体 tag 中字典序最小的 N 项。

取消在访问前、实际时钟调用前后、快照重试、每个有界来源与结果点之间检查；同步桶锁、一次块解码和外部 `NowFn` 本身不能被强制中断。存储 I/O、校验和、数据格式和引擎类型冲突异常正常传播，不变成空样本或“健康”。

## 新增诊断代码

| 代码 | 含义 |
|---|---|
| TSP001 | 缺少数据库元数据上下文 |
| TSP002 | measurement 目录没有 schema |
| TSP003 | 目标字段未声明或实际是 Tag |
| TSP004 | 声明类型或真实样本不兼容数值聚合 |
| TSP005 / TSP006 | 实际 series / tag 值基数达到调用方阈值 |
| TSP007 | tag 项达到上限，部分基数未检查 |
| TSP008 | TTL 配置无效或截止时间溢出 |
| TSP009 | 查询范围包含 cutoff 之前的数据 |
| TSP010 | 源工作量预算不足，数据质量未检查 |
| TSP011 | 没有可见点，未取得质量/类型样本 |
| TSP012 | 样本截断，只检查了查询方向上的前缀 |
| TSP013 | 样本有 NaN/正负无穷 |
| TSP014 | 样本类型与目录 schema 不兼容 |
| TSP015 | 样本中有过期但仍可见的点 |

原有 `Info=0`、`Error=1` 数值保持不变，新增 `Warning=2`。预检 DTO、选项和静态查询诊断注册到公开 `TimeSeriesQueryJsonContext`；`TimeRange` 明确 JSON 构造函数，支持非零范围的 source-generated round-trip。

## 验收状态

本地实现覆盖 schema、cardinality、retention、坏点检查及真实引擎预算合同。回归覆盖真实目录的多 series、字段/Tag 区分、数值聚合类型、声明与历史数据冲突、TTL 单位与边界、非有限值、有界方向采样、源点/字节/段/墓碑预算、预取消与中途取消、source-generated JSON 和持久化重开后的 schema/catalog/segment/tombstone 读取。

本次本地验证新增 29 个预检测试用例，与既有 builder、QueryEngine、MemTableSeries 和 SegmentManager 定向回归合计 168 个通过。Core Release 构建通过；Core/Data 显式开启 `IsAotCompatible`、`EnableAotAnalyzer`、`EnableTrimAnalyzer` 的 Release Rebuild 为 0 警告、0 错误。该结果属于构建与真实本地引擎 fixture 证据，不是远程服务或现场报告。

这些合同测试使用真实本地引擎，测试数据仍是明确构造的 fixture。远程入口与真实服务 parity、现场恢复、长期容量和性能证据仍需独立验收；本项不声称已完成九模型 #310/#311 或 M20 nightly 门禁。
