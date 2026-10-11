# M42 / SQL-002：TTL Document raw SELECT 物化预算

2026-10-02 的合同切片将[直接 Document raw SELECT 预算](m42-document-result-bounds.md)扩展到存在 TTL 索引的集合。真实 Core `SqlExecutor.Execute` / `ExecuteStatement` 入口复用 `SqlExecutionOptions.MaxMaterializedRows` / `MaxMaterializedBytes` 的 opt-in 累计预算；成功返回完整 SQL 页且 `Truncated=false`，超限抛出 `InvalidOperationException`。未启用预算的读取继续使用已有 planner 和 TTL 持久回收语义。

```csharp
var result = SqlExecutor.Execute(database, null,
    "SELECT id, json_value(document, '$.state') AS state FROM events WHERE json_value(document, '$.active') = true LIMIT 100 OFFSET 5",
    null, null, new SqlExecutionOptions
    {
        MaxMaterializedRows = 100,
        MaxMaterializedBytes = 1024 * 1024,
        CancellationToken = cancellationToken,
    });
```

## TTL 可见性与读取顺序

预算 raw SELECT 在开始执行时取得一个 UTC Unix 毫秒时刻，逐候选读取复用既有 TTL 时间解析和过期判断。在集合锁内使用最新 schema，预检之后并发添加的 TTL 索引也会参与判断。任一 TTL 索引的时间加保留窗口小于或等于该时刻时，文档不可见；时间可以是 Int64 数字、整数文本或可解析日期文本。缺失、null、非时间文本、布尔值或无法解析为 Int64 的数字保持既有不参与过期判断的语义。普通非 TTL 索引不参与判断。本切片将时间相加改为 Int128 比较，避免 `long.MaxValue` 等未来时间因 Int64 加法溢出误判过期；TTL 秒转毫秒仍保留原有 checked 溢出检查。

扫描每次最多解码一个文档，按 ID 推进游标。过期候选仍推进游标，不能把过期页误判为扫描结束；全部过期的前缀或尾部会持续推进直到下一条可见文档或集合末尾。ID 等值查询只读取目标文档。过期文档在执行 WHERE、OFFSET 或投影前隐藏，不占输出物化配额。过滤及 OFFSET 之后保留的完整行进入根累计预算，LIMIT 达到 SQL 页后停止；LIMIT 0 不打开集合或读取 TTL 候选。

`CandidateRows` / `LogicalReads` 计入全部实际读取的候选，包括过期文档；`ExaminedRows` 只计 TTL 可见且进入 SQL WHERE 求值的候选。因此 TTL 过滤后的统计可以区分底层读取与残余谓词工作量，不能用保留行预算推断扫描候选总量。

固定的是 TTL 判断时刻，**不是集合快照**。分页读取只在单次候选读取时持有集合锁；并发写入、删除和 schema 变更仍沿用原逐页访问边界。执行过程中刚到期的文档按本次查询的开始时刻判断，后续根调用重新取得时刻。

## 回收、解释与支持边界

预算读取辅助不调用全量 `PurgeExpiredDocumentsLocked`，不持久删除 TTL 过期主文档，也不产生 TTL delete change-feed 事件。首次打开持久集合时，只有当前存在显式执行预算才跳过构造器的 TTL 回收；随后普通 `Get` / `Scan` / `Count` 及默认 SQL 查询仍按原语义回收文档与索引，并产生既有 TTL 删除事件。预算成功、物化失败、表达式失败或取消都不通过 TTL 回收改变文档集合。

首次打开集合仍执行既有 KV 恢复、二级索引重建和派生索引修复。其初始化可能全量读取数据，也可能修复索引条目或清理持久 repair marker；本切片没有把这些操作纳入物化估算，也不宣称 cold-open 完全只读。跳过的仅是预算 SELECT 的全量 TTL 回收和过期主文档删除。

预算 `EXPLAIN` 不调用默认 planner 的 `Count` / `Get` 或索引计数，避免为基数估算启动扫描和回收。其计划报告 `document_budgeted_scan`，估算行数字段为 0，并以 `gap_reason=materialization_budget_cardinality_unknown` 明确表示未取得基数，不能把该 0 当成已确认空集合。`EXPLAIN ANALYZE` 实际执行同一逐候选 raw SELECT，文档输出和解释输出共享根累计预算；解释后的真实候选统计按上述 TTL 合同报告。

支持已有直接 raw SELECT 的伪列、固定 JSON path、标量表达式、WHERE、LIMIT/OFFSET。排序及排序后 LIMIT、聚合/GROUP BY/HAVING、窗口、JOIN、DISTINCT、集合运算、派生表/CTE/Document 视图、子查询、全文/向量和用户函数回调继续在候选读取前拒绝。默认查询的排序、聚合和完整结果能力继续使用原实现。Document DML、关系树中的 Document 输入、REST preview 和 Frame 请求合同没有因本切片扩展。

## 验证与未完成门禁

新增 `DocumentTtlSelectMaterializationBudgetTests` 通过真实 SQL 入口覆盖 TTL 时间格式、等号与极值时间、多个 TTL 索引、4096 文档的预算失败前沿、UTF-16 字节等号、WHERE/OFFSET/LIMIT、单 ID、全部过期尾部、LIMIT 0、过期及页后错误投影、取消/截止时间、计划提前拒绝、预检后 TTL 新增、EXPLAIN/ANALYZE 根预算共享、持久重开，以及普通读取的 TTL delete 事件。原有两项 TTL 拒绝测试改为验证现有 raw SELECT 支持，并保留其他未支持计划回归。

建议定向过滤器：`FullyQualifiedName~DocumentTtlSelectMaterializationBudgetTests|FullyQualifiedName~DocumentSelectMaterializationBudgetTests|FullyQualifiedName~SqlExecutorDocumentTests|FullyQualifiedName~DocumentIndexConsistencyTests`；累计预算兼容回归使用 `FullyQualifiedName~SqlMaterializationBudgetTests|FullyQualifiedName~SqlMeasurementMaterializationBudgetTests`。测试及构建由本轮主智能体集中运行，实际结果记入统一验证证据；这里不将待运行测试记为通过。

扫描沿用 Int32 候选读取上限与五分钟协作墙钟上限，每次读取、每列投影及保留前检查取消/截止时间；根调用释放所有查询物化预留。单次锁等待、JSON 解码、TTL 判断、标量求值及首次打开集合的存储初始化仍不能被 SQL 工作抢占。该切片治理保留输出的累计行数和估算字节数，不保证单文档输入上限、精确 CLR heap 硬上限、存储工作集上限、首行延迟或完整快照一致性。固定目标硬件 heap/首字节、断连压力、端到端流式结果和 TTL 后台有界回收仍未闭环，不作为完整 SQL-002 / M42 生产门禁完成证据。
