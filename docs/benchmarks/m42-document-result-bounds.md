# M42 / SQL-002：直接 Document SELECT 物化预算

2026-10-01 的合同切片将 Core 显式 `SqlExecutionOptions.MaxMaterializedRows` / `MaxMaterializedBytes` 扩展到直接 Document 集合的 raw SELECT。未设置这两项时继续使用原规划器及执行路径。成功返回完整 SQL 结果或指定 SQL 页且 `Truncated=false`；超过累计预算抛出既有 `InvalidOperationException`，不返回成功前缀。

```csharp
var result = SqlExecutor.Execute(database, null,
    "SELECT id, json_value(document, '$.state') AS state FROM docs WHERE json_value(document, '$.active') = true LIMIT 100 OFFSET 5",
    null, null, new SqlExecutionOptions
    {
        MaxMaterializedRows = 100,
        MaxMaterializedBytes = 1024 * 1024,
        CancellationToken = cancellationToken,
    });
```

## 支持范围与执行顺序

支持直接集合 SELECT 的 `*`、id/document/json 伪列、常量、内置标量表达式、固定 JSON path 的 `json_value`、WHERE、LIMIT/OFFSET。JSON 缺失或 null 继续返回 NULL，属性名仍区分大小写；对象与数组的 JSON 投影仍是字符串。标量 CAST、CASE、字符串及算术语义沿用原实现。源与别名经现有名称绑定器处理；本切片没有修改 Document 已有伪列名称合同。

明确 ID 等值条件使用单文档读取。其余查询逐次使用 `ScanAfter(afterId, limit: 1)` 对应的锁内只读辅助，每次只生成一个候选文档并推进 ID 游标，不调用完整 `Scan()`、`GetByIndex()` 或全量 `DocumentQueryPlanner.Execute()`。预算路径当前采用 ID 扫描，不启用路径索引加速；无 ORDER BY 查询不承诺不同访问计划之间的结果次序。过滤及 OFFSET 在投影前执行，过滤掉与跳过的行不计入结果预算；LIMIT 达到指定 SQL 页后停止，后续行不执行投影。LIMIT 0 不启动文档读取。

完整输出行在加入结果列表之前使用既有 `SqlRowRetentionBudget.RetainForExecution` 计入根累计预算。EXPLAIN ANALYZE 的实际 Document 输出与解释结果行共用同一个根计数，正常返回、表达式失败、超限或取消均释放查询拥有的内存预留，下一次根调用建立独立预算。显式预算路径设置五分钟扫描/投影墙钟上限，并使用 Int32 候选读取上限；调用方取消及更短截止时间优先生效。取消与截止时间在每次单文档读取、每列投影和保留前检查；现有底层 KV 读取锁等待及单次存储/标量操作仍不是可抢占的 SQL 工作，因此五分钟是协作退出边界，不冒称可抢占硬超时。默认无预算路径没有增加该限制。

## 提前拒绝与兼容边界

排序（包括排序后 LIMIT）、聚合/GROUP BY/HAVING、窗口、JOIN、DISTINCT、集合运算、派生表/CTE/Document 视图、标量/EXISTS/IN 子查询、全文/向量函数和用户函数回调在读取候选前拒绝。Document 集合不能经关系查询树或关系 DML 的子查询进入该路径。默认无预算的调用继续保留原排序、聚合、索引、全文与向量能力；本切片没有扩展 Document DML 预算支持范围。

存在 TTL 索引的集合在预算调用中提前拒绝。既有 `Get`/`ScanAfter` 会先全量扫描并持久删除过期文档，不能将这种读取冒充为有界、失败不修改数据的 SELECT。预算只读辅助在集合锁内再次确认没有 TTL 索引并跳过回收，避免预检后并发添加 TTL 索引时绕过约束；默认无预算调用仍保留 TTL 回收行为。

字节估算复用既有合同：每行 `64 + 8 × 列数 + 值载荷`。NULL 计 1、固定 SQL 标量计 24、字符串计 `24 + 2 × UTF-16 长度`；全文 JSON、对象/数组 JSON 及标量 JSON 字符串均计费。例如投影 `id='d0000'` 和 `text='雪😀'` 的估算为 `64 + 16 + (24 + 10) + (24 + 6) = 144`，144 允许而 143 拒绝。共享查询/数据库内存预留继续约束结果保留。

## 验证与未完成门禁

定向过滤器为 `FullyQualifiedName~DocumentSelectMaterializationBudgetTests`，既有文档能力回归为 `FullyQualifiedName~SqlExecutorDocumentTests`；关系及 measurement 预算回归分别为 `FullyQualifiedName~SqlMaterializationBudgetTests` 和 `FullyQualifiedName~SqlMeasurementMaterializationBudgetTests`。新测试经过真实 Core SQL 入口验证 4096 文档的小预算读取前沿、字节等号、过滤/分页、全文 JSON 载荷、LIMIT 0、单 ID 读取、稀疏及大小写敏感 JSON 投影、标量表达式、路径索引存在时的过滤、LIMIT 后错误行、提前拒绝、TTL/用户回调拒绝、EXPLAIN ANALYZE 根计费、取消/截止时间、失败预留释放、持久重开和默认排序/聚合。最终测试结果见本轮统一验证记录；这里不将待运行测试写作通过证据。

该切片治理 SQL 输出行保留，**不保证 CLR heap 的精确硬上限、存储工作集硬上限、快照一致性或首行延迟**。底层 KV 扫描仍可能复制/排序覆盖层、持有段及索引工作集；当前文档 JSON 解码、JSON DOM 与标量求值可产生一次性分配。一文档本身可大于预算，只有投影完成后的保留准入有预算合同；不将结果预算冒称单文档输入上限。分页游标不在扫描期间锁定整个集合，保留原 API 的并发读取边界。固定目标硬件 heap/首字节、断连压力、端到端流式输出、TTL 有界回收及完整 SQL-002/M42 外部门禁仍未闭环。
