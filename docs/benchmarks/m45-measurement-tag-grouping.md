# M45-C01：measurement TAG 分组合同切片

核查日期：2026-10-04。本文描述当前源码中的部分实现合同，不将 M45-C01 整包、通用聚合 state、批流等价、资源预算或发布验收标记为完成。本文核对源码及定向测试定义，没有执行构建、测试或性能测量；运行结果应绑定最终候选提交的独立验证报告。

## 入口与支持矩阵

入口沿用 `SqlExecutor`、统一名称绑定和 measurement `SelectExecutor`，没有新增查询语言或第二套执行器。只有包含 TAG 分组键的查询走本切片；原有仅 `GROUP BY time(...)` 和未分组聚合继续走既有路径，不因本切片自动获得相同的空值或类型行为。

| 项目 | 当前合同 |
|---|---|
| 分组键 | 一个或多个已声明 TAG 标识符，可组合一个 `time(正数 duration)`；TAG 重复、重复时间桶、FIELD、未知列或其它分组表达式拒绝。 |
| 聚合要求 | 分组查询必须包含裸聚合函数投影；裸列投影只允许已分组 TAG，以及有时间桶时的 `time`。未分组列与复合标量聚合表达式提前拒绝；旧 time-only 路径的复合表达式不变。 |
| 结果列 | TAG/FIELD 引用先绑定到 schema 原名；未显式改名的结果列保留已保存拼写，显式 SELECT alias 作为结果列名。 |
| WHERE | 复用现有时间范围及 TAG 下推；分解后仍含残差或 Geo 谓词时，TAG 分组提前抛出 `NotSupportedException`，避免过滤被忽略。 |
| HAVING | measurement HAVING 尚未接线，SELECT 和 EXPLAIN 均在扫描前拒绝；该限制也覆盖仅时间分组及未分组聚合。 |
| 自然行序 | 未写 ORDER BY 时，先按时间桶升序，再按 GROUP BY 中 TAG 的先后顺序逐项 `Ordinal` 比较；缺少 TAG 形成 NULL 键并先于已有 TAG 值。既有写入校验要求 TAG 值非空、非空白，本切片不放宽该约束。 |
| ORDER BY | TAG 分组仅允许单键 `ORDER BY time ASC/DESC`，且 SELECT 必须包含未改名的 `time` 投影；TAG 排序、结果别名排序及多键排序提前拒绝。显式时间排序不增加同桶 TAG 的次序承诺。 |
| LIMIT/OFFSET | 在分组结果生成后分页，不提前终止聚合输入，也不是分组状态准入额度。 |
| 显式 SQL 物化预算 | 现有 measurement 预算支持矩阵仍拒绝聚合及 GROUP BY；不能把未设置预算的分组执行表述为已接入该预算。 |
| EXPLAIN | 普通 TAG/time 键的支持校验及 HAVING、残差/Geo、复合投影、排序拒绝共用相同合同；直接 planner 入口同样经过名称绑定，并声明分组阻塞和最终结果物化。 |

基本查询示例：

```sql
SELECT Host, count(Usage), sum(Usage)
FROM Metrics
GROUP BY Host;

SELECT Host, time, count(*)
FROM Metrics
GROUP BY Host, time(1s);
```

## 行、稀疏字段与数值语义

每个 series 的行存在性取该 measurement 全部 FIELD 点流的时间戳并集；同一 series、同一时刻的多个 FIELD 只计一行。实现使用 k-way merge 保留各 FIELD 的流前沿，不为 COUNT(*) 构建全部时间戳集合。不同 series 在相同时间戳上的行分别参与统计；分组 TAG 值相同的 series 合并到同一个分组。

组存在性不依赖被聚合 FIELD 是否有值。例如 `marker` 在某时刻存在、`usage` 缺失时，该行仍建立分组并计入 `count(*)`；`count(usage)` 仅计实际存在的 Usage 点。组内某个 FIELD 完全没有值时，COUNT 返回 Int64 的 0，其它聚合槽返回 NULL。整个输入为空时没有分组，返回零行。

TAG 分组中的 Int64 `SUM` 使用 checked Int64 累加，返回 Int64，不经 double；溢出抛出 `OverflowException`，不返回失真结果。浮点 SUM 和其它已有聚合继续按原有函数/累加器合同执行，本切片不宣布所有整数聚合、NULL、NaN、空集或混合类型语义已经统一。聚合允许的输入类型仍由现有函数能力检查决定。FIRST/LAST 继续保留多 series 拒绝边界，即使这些 series 可按 TAG 拆到不同组，也不能据此宣称已支持。

名称遵守 GH-Issue #211：创建拼写保留；未加双引号使用 `OrdinalIgnoreCase` 解析，双引号使用 `Ordinal` 精确解析。创建 `Metrics(Host TAG, Usage FIELD FLOAT)` 后，`GROUP BY host` 和 `GROUP BY "Host"` 引用 Host，`GROUP BY "host"` 拒绝。TAG 数据值本身按 Ordinal 分组，不因 SQL 标识符大小写规则合并数据值。

## 执行成本与剩余边界

当前 TAG 分组逐点读取，复用既有聚合函数/累加器，保留全部分组状态并最终物化结果。分组状态的额外成本为 `O(groups + aggregate state)`，另有 FIELD 流前沿、series 候选及已有 reader/cache 成本；DISTINCT 和扩展累加器的 state 大小取决于其合同，不能概括为每组固定空间。GROUP BY 增加高基数时，默认路径没有本切片提供的分组数或字节准入，也没有 spill。

EXPLAIN 的 `memory_behavior` 输出为 `aggregate=tag_group_blocking;group_state=O(groups*aggregate_slots);result=materialized_public_boundary`。这描述槽位个数及阻塞边界，不给扩展累加器或 DISTINCT 内部 state 字节数设置上限。分组存在性需要扫描全部 FIELD 点流，即使 SELECT 只聚合其中一个 FIELD；扫描成本不能只按该投影字段估算。

取消检查贯穿 series、行并集和 FIELD 点读取，但该实现没有证明进程或 CLR heap 总内存上限，也没有首行流式输出合同。不能从 k-way merge、SQL 帧切分或 LIMIT 推断全链路有界。M45-C02 的版本化 state、C03 的 retract/更新删除、C08 的预算/落盘恢复和 C09 的批流/跨端对拍仍待各自切片。

## 源码与定向验证

源码依据：

- [SelectExecutor](../../src/SonnetDB.Core/Sql/Execution/SelectExecutor.cs)：`ValidateMeasurementAggregateClauses`、`ResolveGroupByTime`、`ResolveGroupByTags`、`ExecuteGroupedAggregate`、`EnumerateDistinctTimestamps`、`MeasurementGroupKeyComparer` 和分组 `AggSlot`。
- [MeasurementSqlNameBinder](../../src/SonnetDB.Core/Sql/Execution/MeasurementSqlNameBinder.cs)：SELECT/GROUP BY 名称绑定和 ORDER BY alias 解析。
- [SqlExplainPlanner](../../src/SonnetDB.Core/Sql/Execution/SqlExplainPlanner.cs)：直接 planner 绑定、共用子句拒绝及分组支持校验。
- [SqlExecutorMeasurementGroupByTests](../../tests/SonnetDB.Core.Tests/Sql/SqlExecutorMeasurementGroupByTests.cs)：TAG/复合键、flush/reopen、稀疏 FIELD/缺少 TAG、精确 Int64 SUM/溢出、空输入、名称和提前拒绝的测试定义。

建议最终候选在任务专属有界 runner 中执行：

```powershell
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release --filter 'FullyQualifiedName~SqlExecutorMeasurementGroupByTests|FullyQualifiedName~SqlExecutorSelectTests|FullyQualifiedName~SqlExplain'
dotnet restore SonnetDB.slnx
dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/
```

这些命令是复现入口，不是本文已经取得的 PASS、性能结果或生产门禁证据；适用 Release/AOT、真实 Server/SDK 和目标机证据应分开记录。
