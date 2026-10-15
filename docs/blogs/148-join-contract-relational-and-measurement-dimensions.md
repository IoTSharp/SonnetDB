---
title: SonnetDB JOIN 使用合同：关系表与时序维表怎样关联
categories: SonnetDB
description: 通过独立小库理解关系外连接、NULL、同名列和 measurement-to-relation INNER JOIN 的真实边界。
draft: false
---

# SonnetDB JOIN 使用合同：关系表与时序维表怎样关联

SonnetDB 的 JOIN 可以关联业务关系表，也可以将一个 measurement 的观测与一个关系维表关联。本文分别给出关系外连接和时序 INNER JOIN 的小数据操作步骤，核对 NULL、列名与排序，帮助应用在生成 SQL 前判断当前入口能执行什么。

![SonnetDB JOIN：关系外连接与单个时序维表](https://img2024.cnblogs.com/blog/1725297/202610/1725297-20261011111326625-210140747.png)

图：AI 主题示意图，并非实测截图；关系外连接与 measurement INNER JOIN 的支持范围分别以正文为准。

## 1. 先选择数据模型和匹配版本

2026-10-11 事实核查源提交为 `d4e4b7fdb15dfcb47e81ac598778ad64aba25173`。本篇 JOIN 专题、实现与回归切片和公开提交 `5424b66672e37f49a2a5633fdde0b54a9b1f0290` 相同，参考链接固定到该公开快照。当天核实 GitHub 最新正式 Release 与 NuGet `SonnetDB.Core` 为 **4.0.0**；当前源码合同不自动成为该包的完整功能保证，应匹配 SDK、Server 与源码版本复现。

| 左侧 FROM | 右侧输入 | 本篇适用范围 |
|---|---|---|
| 关系表及合同允许的关系查询源 | 关系查询源 | INNER、LEFT、RIGHT、FULL、CROSS |
| 单个 measurement | 单个关系表 | 仅 INNER；ON 为 TAG 与关系列的单个等值条件 |
| measurement | 文档集合、多个维表或外连接 | 当前拒绝 |

这是 `partial` 的明确支持范围。关系表的 FULL JOIN 不能外推到时序或文档模型，也不赋予跨模型外键、级联删除或原子双写。

可以在引用对应 `SonnetDB.Core` 的 .NET 10 控制台打开独立实验库：

```csharp
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;

using var db = Tsdb.Open(new TsdbOptions
{
    RootDirectory = Path.Combine(Path.GetTempPath(), "sonnetdb-join-demo-" + Guid.NewGuid().ToString("N"))
});

void ExecuteOne(string sql)
{
    var result = SqlExecutor.Execute(db, sql);
    if (result is SelectExecutionResult rows)
    {
        Console.WriteLine(string.Join(" | ", rows.Columns));
        foreach (var row in rows.Rows)
            Console.WriteLine(string.Join(" | ", row.Select(v => v?.ToString() ?? "NULL")));
    }
}
```

将下面 SQL **逐条**交给 `ExecuteOne`，失败例单独捕获并打印异常。不要在业务数据库执行种子 DDL，也不要把整段多语句文本当成一个查询。

## 2. 两张关系表：先看缺失匹配

```sql
CREATE TABLE LeftItems (Id INT, JoinKey STRING NULL, PRIMARY KEY (Id));
CREATE TABLE RightItems (Id INT, JoinKey STRING NULL, PRIMARY KEY (Id));
INSERT INTO LeftItems (Id, JoinKey) VALUES (1, 'a'), (2, 'b');
INSERT INTO RightItems (Id, JoinKey) VALUES (20, 'b'), (30, 'c');

SELECT l.Id AS left_id, r.Id AS right_id
FROM LeftItems AS l
FULL OUTER JOIN RightItems AS r ON l.JoinKey = r.JoinKey
ORDER BY left_id, right_id;
```

按当前合同，结果包含三行：`(1,NULL)`、`(2,20)`、`(NULL,30)`。由于升序时 NULL 在非空值之前，上面的显式排序应让右侧未匹配行先出现。把 FULL 分别改为 INNER、LEFT、RIGHT，可对照下表核对行集合：

| 连接 | 保留的结果集合 |
|---|---|
| INNER | `(2,20)` |
| LEFT | `(1,NULL)`、`(2,20)` |
| RIGHT | `(2,20)`、`(NULL,30)` |
| FULL | 三行全部保留 |

没有 ORDER BY 时不保证顺序。外连接会让来源非空列在结果中成为 NULL；ADO.NET 应通过 `IsDBNull` 读取，不能因为原表主键非空就直接按整数取值。

`CROSS JOIN` 不带 ON，两端各两行时应产生四个组合；任一端为空则零行。当前 RIGHT、FULL、CROSS 使用嵌套循环，最坏工作量可达左右行数乘积，右端需要物化。小数据语义成功不证明大笛卡尔积可在固定内存内完成。

## 3. NULL、多重匹配和 ON/WHERE

分别给两表增加 `JoinKey = NULL` 的行，再执行同一 FULL 查询。等值条件中 NULL 不与另一个 NULL 匹配，它们应各保留为一条未匹配行，而不是合成一条“NULL 键匹配”结果。

若某个非空键左侧有两行、右侧有三行，JOIN 会产生六个匹配组合；它不会自动去重或选取一条维表记录。业务若需要一对一关系，应在建模阶段约束维表键，而不是在结果重复后假定数据库有问题。

```sql
SELECT l.Id AS left_id, r.Id AS right_id
FROM LeftItems AS l
LEFT JOIN RightItems AS r ON l.JoinKey = r.JoinKey
WHERE r.Id = 20
ORDER BY left_id;
```

在原始两行种子数据上，这条只保留 `(2,20)`。WHERE 在 NULL 扩展后执行，`r.Id = 20` 不会对未匹配行成立。若将该条件放进关系 LEFT JOIN 的 ON，未匹配左行仍会保留；两种写法有不同含义，ORM 不能随意移动谓词。

## 4. 同名列用限定符，结果列用唯一别名

两端都有 Id 时，未限定的 `SELECT Id` 会报来源歧义。使用 `l.Id`、`r.Id` 选择来源，再用 `AS left_id`、`AS right_id` 给调用方稳定的结果名称。

FULL JOIN 不合并同名列，也不自动 COALESCE。显式使用两个相同别名仍可得到两个独立结果列；ADO.NET 按序号能读出它们，`GetOrdinal` 对重复名称返回首个匹配。因此生成 SQL 的应用应使用唯一别名，尤其是后续按别名排序、导出或映射实体时。

嵌入式与 Frame 的非空 INT/FLOAT/BOOL/STRING 值保持相应 CLR 类型；空结果、全空列以及 REST 当前行中的 NULL 不提供完整 schema 推断。不要用一个 NULL 结果格判断来源列的声明类型。DATETIME、TIME 等其它类型的传输范围另查协议文档。

## 5. measurement 加一个关系维表

这部分可以在同一独立实验库运行，与上面的两个关系表无关：

```sql
CREATE TABLE DeviceDimension (
    Id INT, Name STRING NOT NULL, PRIMARY KEY (Id)
);
INSERT INTO DeviceDimension (Id, Name) VALUES (1, 'alpha'), (2, 'beta');

CREATE MEASUREMENT Metric (Host TAG, Value FIELD FLOAT);
INSERT INTO Metric (time, Host, Value)
VALUES (1000, 'alpha', 1.5), (2000, 'alpha', 2.5), (1500, 'beta', 9.0);

SELECT m.time AS observed_at, m.Host AS host, d.Id AS device_id, m.Value AS value
FROM Metric AS m
INNER JOIN DeviceDimension AS d ON m.Host = d.Name
WHERE m.time >= 1500 AND d.Id = 1
ORDER BY m.time;
```

按合同应只返回 `(2000,'alpha',1,2.5)`。这里 time 是时间戳伪列，Host 是字符串 TAG，Value 是 FIELD；ON 必须是一个 TAG 与一个关系列的单个等值条件。维表 Name 的唯一性和关联标识一致性由应用建模负责；本示例数据恰好唯一，不构成数据库自动外键。

将上面的 WHERE 常量改成 `@begin`、`@device_id` 时，嵌入式可通过 `SqlParameters` 绑定：

```csharp
var parameters = new SqlParameters()
    .AddNamed("begin", 1500L)
    .AddNamed("device_id", 1L);
var result = SqlExecutor.Execute(db, databaseName: null,
    sql: "SELECT m.time FROM Metric m INNER JOIN DeviceDimension d ON m.Host = d.Name "
       + "WHERE m.time >= @begin AND d.Id = @device_id ORDER BY m.time",
    parameters: parameters, controlPlane: null);
```

参数可用于 WHERE、标量投影和分页，不能参数化表名或列名。此时序合同不允许在 ON 追加 `AND d.Id = @device_id`；应将它保留在 WHERE。仅在这里的 INNER 合同和合法谓词下采用这个写法，不能因此给关系外连接做相同改写。

## 6. 主动核对拒绝和排序边界

分别测试以下变化，并保存每项诊断；异常之后再次执行合法查询，核对连接仍可使用：

- 把 measurement 的 INNER 改成 LEFT/RIGHT/FULL/CROSS：SELECT 与 EXPLAIN 都应拒绝。
- 添加第二个维表 JOIN，或将右侧改成文档集合：超出当前模型合同。
- 给 measurement JOIN 添加 COUNT、GROUP BY 或 HAVING：当前不支持，空输入也不能据此判断支持。
- 保留合法 INNER，增加 `ORDER BY d.Id ASC, m.Value DESC`，再加 `OFFSET 0 ROWS FETCH NEXT 1 ROWS ONLY`：分页发生在完整排序之后。

measurement JOIN 支持多个排序项、每项方向及未投影 FIELD 排序，但结果分页不证明有界扫描、低首行成本或固定硬件性能。关系 RIGHT/FULL/CROSS 的资源风险也需要另行测量。

SDK 的 `GetSchema("DataSourceInformation")` 提供关系 `SupportedJoinOperators`，并有 measurement 的 `MeasurementJoinKinds=INNER`、最大维表数 1、ON 形状与参数位置等字段。这些是 SDK 本地能力声明，读取不发请求，不是旧 Server 的能力协商。通用 ADO.NET 接收 SQL 也不代表自动做完整模型预检，Provider 应在生成 SQL 前检查映射和合同。

## 7. 传输与验证证据分别记录

非事务 JOIN SELECT 的 REST 入口为 `/v1/db/{database}/sql`，Frame 为真实 HTTP/2 `/v1/frame`。DDL/DML 仍使用 REST 写入入口；轻事务读取属于 REST 控制面，不因为连接配置为 Frame 就变成二进制事务查询。只读凭据可以查询，不能写入种子数据。

本次发稿只核对源码、专题与既有测试断言，**没有实跑本文示例或新增测试**。文中结果均为合同预期。读者可以在匹配源码执行这些已有回归并记录实际入口：

```text
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --filter FullyQualifiedName~RelationalStandardJoinTests
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --filter FullyQualifiedName~MeasurementJoinContractTests
dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj --filter FullyQualifiedName~RemoteStandardJoinTests
dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj --filter FullyQualifiedName~RemoteMeasurementJoinContractTests
```

功能或真实本机 Server 集成测试通过，与固定硬件、生产网络、运行中取消延迟和长期稳定性仍是不同证据。跨模型查询可以读取已提交数据，不等于新增跨模型原子写入或快照隔离保证。

参考：[标准关系 JOIN 合同](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/standard-join-contract.md)、[measurement JOIN 合同与历史验收](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/audits/measurement-join-196-closure-20260923.md)、[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/sql-reference.md)、[Frame 协议](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/frame-protocol.md)、[项目路线图](https://github.com/IoTSharp/SonnetDB/blob/main/ROADMAP.md)。
