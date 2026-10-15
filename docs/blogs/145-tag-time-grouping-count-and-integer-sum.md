---
title: SonnetDB TAG 加时间桶分组：COUNT、稀疏字段与精确整数 SUM
categories: SonnetDB
draft: false
description: 用可核查小数据解释 TAG 与时间桶分组、稀疏 COUNT 和 Int64 SUM，明确 M45 首批切片与阻塞聚合边界。
---

# SonnetDB TAG 加时间桶分组：COUNT、稀疏字段与精确整数 SUM

SonnetDB 当前的 measurement SQL 已有 TAG 分组首批实现，可以把设备标签与时间桶组合起来统计。本文用小数据解释 COUNT(*)、COUNT(field) 和整数 SUM 的区别，并说明哪些查询会被明确拒绝，以及为什么 LIMIT 不能限制聚合输入成本。

![SonnetDB TAG 与时间桶分组：COUNT、稀疏字段和整数 SUM](https://img2024.cnblogs.com/blog/1725297/202610/1725297-20261010111555357-91509806.png)

图：AI 主题示意图，并非实测截图；图形用于说明分组概念，实际接口和支持边界以正文为准。

## 先固定使用的代码版本

核查时间为 2026-10-10，源码基线为 `78045e8b407e9caceae2548116390ffb985fc959`。当日独立读取的 GitHub 最新正式 Release 与 NuGet SonnetDB.Core 页面均显示 4.0.0；ROADMAP 中 4.5 是规划目标。**本文解释当前源码中的 M45-C01 部分切片，不保证正式 4.0.0 包包含该切片，也不宣布 4.5 已发行。**

以下示例按当前源码与定向测试定义核对，本次写稿没有执行数据库示例、产品构建或性能测量。复现时请使用对应源码与 .NET 10，保存实际提交、SDK、结果和异常记录。已有数据库不要直接用于演示。

在单独的示例目录创建控制台项目，并引用这个源码版本的 Core 项目：

```powershell
dotnet new console --framework net10.0
dotnet add reference D:/source/SonnetDB/src/SonnetDB.Core/SonnetDB.Core.csproj
dotnet run
```

最后一步应在写入下文 Program.cs 后执行。源码目录可替换为读者自己的检出路径，数据库目录应是新的空目录。

公开参考和复现源码固定到提交 5424b66672e37f49a2a5633fdde0b54a9b1f0290；相关专题及本文使用的 Core/测试文件与本地复核基线逐文件比较无差异。后续工作台交接变动不作为本文能力证据。

## 用稀疏值建立一个可核查例子

将以下代码放入 Program.cs。SQL 创建名保留原始拼写；TAG 是 series 身份的一部分，FIELD 是数据值。这里特意让一条记录只有 Marker，没有 Usage。

```csharp
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;

using var db = Tsdb.Open(new TsdbOptions
{
    RootDirectory = Path.GetFullPath("./demo-145")
});

SqlExecutor.Execute(db,
    "CREATE MEASUREMENT Metrics (Host TAG, Usage FIELD FLOAT, Marker FIELD INT)");
SqlExecutor.Execute(db,
    "INSERT INTO Metrics (time, Host, Usage, Marker) VALUES " +
    "(1000, 'h1', 2.0, 1), (1500, 'h1', 3.0, 2), " +
    "(2000, 'h1', 4.0, 3)");
SqlExecutor.Execute(db,
    "INSERT INTO Metrics (time, Host, Marker) VALUES (1000, 'h0', 9)");

var result = (SelectExecutionResult)SqlExecutor.Execute(db,
    "SELECT Host, time, count(*), count(Usage), sum(Usage) " +
    "FROM Metrics GROUP BY Host, time(1s)");

Console.WriteLine(string.Join(" | ", result.Columns));
foreach (var row in result.Rows)
{
    Console.WriteLine(string.Join(" | ",
        row.Select(value => value?.ToString() ?? "NULL")));
}
```

本例时间值按毫秒解释，1 秒桶对应 1000 个单位。时序时间单位须由应用与配置保持一致，不能把任意整数当成 Unix 秒。

根据当前实现，预期分组如下。这是代码推导的可核查预期，不是本轮执行截图：

| Host | time | count(*) | count(Usage) | sum(Usage) |
|---|---|---|---|---|
| h0 | 1000 | 1 | 0 | NULL |
| h1 | 1000 | 2 | 2 | 5.0 |
| h1 | 2000 | 1 | 1 | 4.0 |

h0 的 Marker 建立了这一行和分组，但没有 Usage，所以 COUNT(*) 为 1、COUNT(Usage) 为 0、SUM(Usage) 为 NULL。h1 在 1000 与 1500 的两个时刻合进同一时间桶。同一 series 同一时刻同时有 Usage 和 Marker，仍只计一行。

这条路径以 measurement 全部 FIELD 点流的时间戳并集定义行存在性。不同 series 同时刻的行分别计数；若它们选定的分组 TAG 值相同，会合并进同一组。增加或减少分组键会改变合并范围，不能把一次分组误当成 series 数统计。

## 区分名称规则和 TAG 数据值

以下查询中的未加引号名称仍能绑定到 Metrics、Host 和 Usage 的原拼写：

```sql
SELECT host, count(usage)
FROM metrics
GROUP BY HOST;
```

`GROUP BY "Host"` 是精确引用；`GROUP BY "host"` 与创建的 Host 不匹配，应拒绝。该合同解决 SQL 标识符解析，不改变 TAG 值：数据值 h1 与 H1 按 Ordinal 比较，是不同分组。

缺少 TAG 的 series 可以形成 NULL 分组键；这不等于允许写入空白 TAG 值。组内 FIELD 没有值时 COUNT 为 Int64 的 0，其它空聚合槽为 NULL；整个输入为空时返回零个分组。不要把这些结论外推到原有 time-only 聚合的所有函数。

## 验证整数精度和溢出

整数 SUM 的关键是返回值仍是 Int64，不先转成 double。先在同一示例数据库执行：

```sql
CREATE MEASUREMENT integers (host TAG, value FIELD INT);
INSERT INTO integers (time, host, value) VALUES
(1000, 'precise', 9007199254740993),
(2000, 'precise', 2),
(1000, 'overflow', 9223372036854775807),
(2000, 'overflow', 1);

SELECT host, sum(value)
FROM integers
WHERE host = 'precise'
GROUP BY host;
```

按当前合同，SUM 为 Int64 的 9007199254740995。不要为了显示方便先强制转换成 double；记录返回类型和值才足以核对精度。然后执行：

```sql
SELECT host, sum(value)
FROM integers
WHERE host = 'overflow'
GROUP BY host;
```

预期抛出 OverflowException，不能把异常转成空结果或截断整数。当应用确实需要更大累加范围，应在应用模型中明确设计，不能假定当前 SQL 自动提升为任意精度整数。上述数据和断言来源于当前定向测试，本文没有取得本轮运行 PASS。

## 先用 EXPLAIN 核支持形状

```sql
EXPLAIN SELECT Host, time, count(*)
FROM Metrics
GROUP BY Host, time(1s);
```

当前 memory_behavior 会描述 `aggregate=tag_group_blocking`、分组槽位规模和 `result=materialized_public_boundary`。这告诉调用方需要聚合完成后再物化结果，不提供扩展累加器内部字节数的硬上限。

支持一个或多个已声明 TAG，组合至多一个正数 duration 的 time 桶；投影使用裸聚合函数和已分组键。以下边界应在接入时主动检查：

| 查询形状 | 当前 TAG 分组合同 |
|---|---|
| WHERE 中的时间范围与可下推 TAG 条件 | 沿用已有路径 |
| 残差 FIELD 条件、Geo 条件 | 提前拒绝 |
| measurement HAVING | SELECT 与 EXPLAIN 提前拒绝 |
| sum(Usage) + 1 等复合聚合投影 | 提前拒绝 |
| ORDER BY time ASC/DESC | 单键，必须投影未改名的 time |
| TAG、结果别名或多键排序 | 提前拒绝 |
| 多 series 的 FIRST/LAST | 保留拒绝边界 |
| 显式 SQL 物化预算加聚合/GROUP BY | 不在该预算支持范围内 |

例如 `WHERE Usage > 1 GROUP BY Host` 不能自动变成可靠的“过滤后分组”。当前执行器会拒绝残差条件，应用应处理 NotSupportedException，不能删掉过滤条件悄悄继续执行。

## 数据量增大时怎样控制任务

当前实现保留所有分组状态，最终物化结果，没有本切片提供的分组数或字节准入，也没有 spill。LIMIT/OFFSET 是聚合结果生成后的分页，不能提前终止输入扫描。COUNT(*) 为确定行存在性还需要看全部 FIELD 点流，不能只按 SELECT 里出现的字段估算扫描工作。

可先缩小明确的时间范围和 TAG 范围，再核对分组基数；保留取消令牌和失败记录。取消检查覆盖 series、行并集与 FIELD 点读取，但没有证明全进程 heap 上限或首行流式输出。高基数、DISTINCT 和扩展聚合状态需要独立测量。

2026-10-04 的专项审计记录了该工作树定向 Release 回归 181/181，以及 Core build 和格式检查通过；它是历史本机切片证据，不是当前提交的新回归、完整 Core 测试、真实 Server、固定硬件或长期生产验收。M45 后续的版本化 state、retract、预算与落盘恢复、批流对拍仍需各自推进。

## 事实依据与下一步阅读

- [TAG 分组支持矩阵与成本合同](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/benchmarks/m45-measurement-tag-grouping.md)
- [2026-10-04 首批验证及证据边界](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/audits/m45-measurement-grouping-20261004.md)
- [SelectExecutor 当前实现](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Core/Sql/Execution/SelectExecutor.cs)
- [稀疏、精度、溢出与拒绝形状的测试定义](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/tests/SonnetDB.Core.Tests/Sql/SqlExecutorMeasurementGroupByTests.cs)
- [Measurement 建模前篇](https://www.cnblogs.com/IoTSharp/p/23235723)
- [当前 ROADMAP；4.5 为目标](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/ROADMAP.md)

先用小数据核对 COUNT、NULL、返回类型和拒绝行为，再决定是否适合自己的设备统计任务。性能数字应在固定提交、数据分布、机器和完整原始报告下另行取得。
