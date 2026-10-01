# M42 / SQL-002：直接 measurement SELECT 物化预算

2026-10-01 的合同切片将 Core 的显式 `SqlExecutionOptions.MaxMaterializedRows` / `MaxMaterializedBytes` 扩展到直接 measurement 的 raw SELECT。未设置这两项时继续使用原执行路径。它与 REST `previewMaxRows` 的成功截断合同不同：成功结果是完整的 SQL 结果或 SQL 页，`Truncated=false`；超限抛出既有 `InvalidOperationException`，不把前缀作为成功结果返回。

```csharp
var result = SqlExecutor.Execute(database, null,
    "SELECT time, host, value FROM readings WHERE value >= 10 LIMIT 100 OFFSET 5",
    null, null, new SqlExecutionOptions
    {
        MaxMaterializedRows = 100,
        MaxMaterializedBytes = 1024 * 1024,
        CancellationToken = cancellationToken,
    });
```

## 支持范围与执行顺序

支持顶层直接 measurement raw SELECT 的 `*`、time/TAG/FIELD、常量与内置标量投影、TAG/time 下推过滤及逐点残差 WHERE、LIMIT/OFFSET。FIELD 依赖限于 INT/FLOAT/BOOL/STRING；稀疏字段仍按所需字段时间戳并集合并，缺值输出 NULL。名称继续由现有绑定器处理，保留创建时拼写及双引号精确匹配合同。

执行器逐 series 消费现有 QueryEngine 点流，使用字段前沿队列按时间戳合并。每个字段仅在 SQL 层保留一个前沿点与当前时间戳值，不建立原 raw 路径的全字段点列表、全时间戳集合或完整 ts→value 查表。多个 series 沿用原无 ORDER BY 查询的 series 顺序，各 series 内按时间升序；不承诺无 ORDER BY 查询的全局时间排序。

过滤在投影和保留之前执行，OFFSET 跳过过滤后的行，LIMIT 达到完整 SQL 页后立即停止。只有实际保留的输出行在加入结果列表**之前**计入累计预算，过滤掉及 OFFSET 跳过的行不计入结果预算；扫描点数仍可能远大于结果行数。跨 series 不重置根预算，EXPLAIN ANALYZE 的实际执行与已有解释输出共用同一根计费。新的 measurement 路径以串行逐点合并实现；不复制 worker 的预算或先并行生成 per-series 全结果列表。

LIMIT/OFFSET 外的行不执行投影，因此 SQL 页成功并不验证未选中行的 CAST 等表达式。完整查询仍能暴露这些后段错误。默认无预算路径保留原有求值与错误行为，不因本合同改变。每字段的一个前沿点可能已被读取；LIMIT 0 不启动点流。

## 提前拒绝与字节估算

measurement 的 ORDER BY（包括排序后 LIMIT）、聚合/GROUP BY、窗口、JOIN、DISTINCT、集合运算、派生表、CTE、标量/EXISTS/IN 子查询、Geo 谓词及 VECTOR/GEOPOINT FIELD 依赖当前在扫描前抛出 `NotSupportedException`。关系查询树内的 measurement 源同样拒绝，避免经子查询或集合分支进入未治理的路径。用户函数回调仍按原物化预算合同提前拒绝。默认调用仍可使用已有排序、聚合等能力；本切片没有扩大 measurement DML 预算支持范围。

复用既有 `SqlRowRetentionBudget` 的根作用域与估算：每次保留为 `64 + 8 × 列数 + 值载荷` 字节。NULL 计 1，固定宽度 SQL 标量计 24，字符串计 `24 + 2 × UTF-16 长度`。例如单行 `time, value, label` 且 label 为 100 字符，估算为 `64 + 24 + 24 + 24 + 224 = 360` 字节；360 允许，359 拒绝。共享查询/数据库内存预留继续生效，超限、取消、表达式失败和正常返回均释放查询拥有的预留及点流租约。下一次根调用具有独立累计预算。

## 验证与边界

定向过滤器为 `FullyQualifiedName~SqlMeasurementMaterializationBudgetTests`；关系预算回归为 `FullyQualifiedName~SqlMaterializationBudgetTests`。新测试通过真实 Core SQL 入口覆盖 4096 点/小预算的点流消费上界、过滤后计数、字符串与多列精确估算、WHERE/OFFSET/LIMIT、LIMIT 0、跨 series 根计费、稀疏字段 NULL、标量/字符串表达式、大小写/双引号绑定、LIMIT 后错误行、预检拒绝、用户回调拒绝、取消/截止时间、失败后预留回收与重新执行、持久数据重开以及默认完整排序/聚合能力。最终构建与测试结果由本轮统一验证记录提供；本文件不将待验证测试表述为已通过。

这提供 SQL 输出物化的准入合同，**不保证 CLR heap 精确硬上限、存储扫描工作集硬上限或首行延迟**。现有 QueryEngine 仍可为 MemTable 范围复制快照、为乱序桶排序，取得索引候选列表/段租约并解码底层 block；series catalog、解析/绑定、当前行 FIELD decode 和标量求值的瞬时分配也不等于 SQL 保留行估算。投影前可存在一行及每字段一个前沿点，调用方在成功返回后拥有结果，后续保留不再受查询预留治理。固定目标硬件的 heap/首字节、断连压力、长期运行、端到端流式输出及完整 SQL-002/M42 门禁仍未闭环。
