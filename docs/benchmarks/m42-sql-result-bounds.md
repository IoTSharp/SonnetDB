# M42 / SQL-002：显式结果预览与关系 SELECT 物化预算

2026-09-24 本地合同切片完成：REST SQL 调用方可以显式要求有界结果预览；Web SQL 控制台使用该合同。该切片不代表 SQL-002 的执行阶段内存边界或整项验收完成。

## 请求和响应

`POST /v1/db/{db}/sql`、`/sql/batch` 中的单条语句和 `/v1/sql` 可传 `previewMaxRows`（正整数）。实际预算为请求值与 `SonnetDBServer:SqlExecution:MaxResultRows` 的较小值；配置默认 10000，绑定范围 1～1000000。无效请求值在该语句执行前拒绝，避免写入后才报告预算错误。

未提供 `previewMaxRows` 的 REST 调用、嵌入式调用和 Frame 调用保持完整结果语义。Frame 字节格式未扩展；已有客户端和公共记录类型的主构造/Deconstruct 签名保持兼容。

```json
{"sql":"SELECT id FROM orders ORDER BY id","previewMaxRows":1000}
```

查询和 DML RETURNING 的结果超过预算时只传前 N 行，NDJSON end 的 `rowCount` 为实际返回行数，`truncated=true` 明确表示数据不完整；恰好 N 行或更少为 `false`。`recordsAffected` 始终保留完整写入影响数。预览不会更改 SQL 的写入、COMMIT 或 ROLLBACK 语义，也不会中止批次后续语句。

Web 通用 SQL API 不自动启用预览；仅 SQL 控制台显式请求最多 10000 行，在历史摘要、执行摘要及警告中显示结果已截断。依赖完整查询结果的导出和其他工作台沿用原行为。缺少 end、非法行数或格式损坏不视为成功完整结果。`SndbDataReader.Truncated` 提供可检查的状态；REST 流读到 end 后确定该值。

## 验证

- `SqlResultBoundsTests`：预览前缀、精确边界、默认完整和 DML 影响行数。
- `SqlResultBoundsEndpointTests`：REST 默认完整/显式截断、请求与 Server 上限、RETURNING、批次提交/失败回滚、非法预算不执行写入，以及 Frame 字节格式未变化。
- `web/tests/sql-workflow.test.mjs`：实际 API/控制台模块的 opt-in 请求、截断状态、非法元数据、结果不完整提示，复用既有取消与事务批次测试。

最终本地验证：Core 预览 3 项、公开 ADO 13 项、真实 REST/Frame/ADO 端点 12 项通过；连同覆盖索引和超时回归，Core 为 87/87。Server 首轮 185 个既有/relay 用例通过，新预览 fixture 的 12 项因使用不支持的列内主键语法初始化失败；仅将 fixture 改为表级主键后，12/12 定向重跑通过。两轮共 197 个不同 Server 用例最终通过，首次失败记录仍保留。[统一记录](../audits/roadmap-closure-evidence-20260924/validation.json)、[Core TRX](../audits/roadmap-closure-evidence-20260924/core.trx)及[端点 TRX](../audits/roadmap-closure-evidence-20260924/sql-preview.trx)包含原始证据。

Web：`node --experimental-vm-modules --test web/tests/sql-workflow.test.mjs` 共 11 项通过、0 失败、0 跳过；`vue-tsc --noEmit` 通过，Vite production build 通过，仍有大于 500 kB chunk 的体积提示。[测试日志](../audits/roadmap-closure-evidence-20260924/sql-preview-web-tests-final.out.log)与[构建日志](../audits/roadmap-closure-evidence-20260924/sql-preview-web-build-final.out.log)已留存。全部构建产物输出到本轮专属 C 盘临时目录，不写入仓库；.NET Release 使用 `/warnaserror`，未压制 AOT/trim 警告。

## 2026-09-30 关系 SELECT 执行器预算

Core 的 `SqlExecutionOptions.MaxMaterializedRows` 和 `MaxMaterializedBytes` 是独立的 opt-in 执行合同，可用于 `SqlExecutor.Execute` / `ExecuteStatement` 的显式选项重载。两项默认均为 `null`；未设置时所有入口继续保留原有完整结果、写入和例程语义。启用后，成功返回的结果仍完整且不会设置 `Truncated`；超限抛出 `InvalidOperationException`，不会把不完整结果当成成功。

```csharp
var options = new SqlExecutionOptions
{
    MaxMaterializedRows = 100_000,
    MaxMaterializedBytes = 64L * 1024 * 1024,
};
var result = SqlExecutor.Execute(database, null,
    "SELECT id, payload FROM orders ORDER BY id", null, null, options);
```

支持范围为关系表、已展开的关系视图、常量 SELECT，以及它们的 JOIN、派生表、标量/IN/EXISTS 子查询、普通/递归 CTE、UNION/INTERSECT/EXCEPT、DISTINCT、GROUP BY、排序和窗口投影。`EXPLAIN SELECT` 的输出也计费，`EXPLAIN ANALYZE SELECT` 的实际执行与解释结果共用预算。查询树在扫描前预检；时序、文档、Graph、系统视图、物化视图、表值函数、用户函数回调和非标准标量值当前明确拒绝该模式。

**此切片不支持 DML 物化预算。** 启用任何一项时，INSERT/UPDATE/DELETE（包括 RETURNING）、CALL、DDL、事务控制等语句在目标分发、生成键、触发器或实际 mutation 之前抛出 `NotSupportedException`。已有显式事务可以执行受预算的只读 SELECT，失败不改变此前缓冲写入；BEGIN/COMMIT/ROLLBACK 继续使用未启用该预算的调用。0 或负预算在语句分发之前抛出 `ArgumentOutOfRangeException`。原有 trigger transition / INSERT SELECT 预算在默认模式下保持既有错误合同。

预算覆盖结果追加及阻塞阶段的物化保留，并跨整个根调用共享。子查询、CTE、集合运算、spill 输出和并行 worker 不重置计数；不同根调用各自计费。行数是**累计物化次数**，不是最终返回行数：排序输入、JOIN build/同键组、聚合/窗口输入、辅助键及后续投影可能重复计入，SQL LIMIT 不能跳过这些阶段的预算检查。已计费的物化在本条语句返回或失败前不退还累计配额，因此这是保守的准入合同，并非实际存活行数测量。为防止候选全集先于预算检查物化，此模式使用关系惰性扫描输入，事务叠加逐行计费并从小容量开始；会牺牲部分单表索引/IN 快路径性能，默认调用不受此影响。

每次物化行的估算为 `64 + 8 * 列数 + 值载荷` 字节。NULL 计 1，固定宽度 SQL 标量计 24，字符串计 `24 + 2 * UTF-16 长度`，二进制值计 `24 + byte[].Length`；窗口输出容器先计费，填入值时追加载荷差额。查询/数据库原有共享阻塞算子预留继续有效，启用的物化字节上限进一步收紧其准入，所有失败路径归还预留并清理查询 spill 工作区。成功返回后结果由调用方拥有，查询预留已经释放；该预算不治理调用方的后续保留或跨响应总内存。

`SqlMaterializationBudgetTests` 覆盖默认完整结果、精确行/字符串字节边界、大二进制值、0/负值、提前停止扫描、自动提交/显式事务 RETURNING 提前拒绝且数据不变、read-your-writes、嵌套/CTE/集合/递归预算共享、五类阻塞路径、spill 清理、EXPLAIN/ANALYZE、取消、并发 worker 计费，以及预检在扫描/用户回调前拒绝未支持路径。Release `/warnaserror` 定向回归最终 181/181，其中新增预算 36/36；合并 SQL/CDC/Streaming 回归 1746/1746。[并行实施记录](../audits/roadmap-parallel-implementation-20260930.md)保留原始结果与首轮夹具失败说明。

## 未覆盖的内存与性能边界

执行器仍先生成完整 `SelectExecutionResult`。REST 预览减少传输行数和 Web 留存行数；新 Core 预算提供受支持的关系 SELECT 物化准入与超限拒绝。两者都**不保证 CLR heap 精确硬上限或首行延迟**：解析/绑定、已有表 snapshot、存储页/索引底层缓存、单行 decode 和 scalar expression 求值的瞬时分配、spill 游标缓冲等不等同于物化估算。DML RETURNING 执行预算、其它数据模型、端到端流式执行、大结果 heap/首行延迟基准、断连压力与固定目标硬件证据仍未闭环。不得将此文或合同测试用作完整 SQL-002、M42 或生产门禁完成证据。
