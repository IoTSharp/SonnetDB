---
layout: default
title: "标准关系 JOIN 合同"
description: "RIGHT、FULL、CROSS JOIN 的值语义、ADO.NET 能力与 REST/Frame 验收。"
permalink: /standard-join-contract/
---

# 标准关系 JOIN 合同

本合同对应 GH-Issue #177。关系查询支持 `INNER JOIN`、`LEFT [OUTER] JOIN`、`RIGHT [OUTER] JOIN`、`FULL [OUTER] JOIN` 和无 `ON` 子句的 `CROSS JOIN`。嵌入式 ADO.NET、Server REST/NDJSON 与 HTTP/2 Frame 使用同一解析器和关系执行器。

```sql
SELECT l.id AS left_id, r.id AS right_id
FROM left_items AS l
FULL OUTER JOIN right_items AS r ON l.join_key = r.join_key
ORDER BY left_id, right_id;
```

## 数据模型和匹配规则

`RIGHT`、`FULL` 和 `CROSS` 的合同属于关系行查询。输入可以是关系表，或由现有关系查询入口解析的关系视图、物化视图及派生表。measurement 作为 `FROM` 输入的混合 JOIN 只支持单个关系维表的 `INNER JOIN`；`LEFT`、`RIGHT`、`FULL` 和 `CROSS` 在 SELECT 和 EXPLAIN 均返回明确的“当前仅支持关系表 FROM”错误。该限制与 [SQL 参考](sql-reference.md) 的 measurement JOIN 范围一致，避免没有时序 NULL 扩展能力时返回错误的外连接结果。不得在 Provider 中把未支持的数据模型连接改写成其他 JoinKind。

| 类型 | 匹配及保留规则 |
|---|---|
| INNER | `ON` 为 TRUE 时输出；没有匹配的输入行不输出。 |
| LEFT | 输出所有匹配组合；无匹配的左行保留一次，右侧投影为 SQL NULL。 |
| RIGHT | 输出所有匹配组合；无匹配的右行保留一次，左侧投影为 SQL NULL。 |
| FULL | 输出所有匹配组合；两端各自的无匹配行分别保留一次，另一侧投影为 SQL NULL。 |
| CROSS | 输出两端全部行的笛卡尔积；任一端为空时结果为空。 |

等值条件中的 NULL 不与另一个 NULL 匹配。重复键保留多重性：某键左端有两行、右端有三行时产生六行。`ON` 参与匹配判断，`WHERE` 在完成 NULL 扩展后过滤；把外连接的 `WHERE` 条件挪到 `ON` 会改变结果，Provider 不应擅自转换。

输入为空时仍按上表执行；两端均空时所有连接都返回零行。多 JOIN 先组合前面的结果，再应用后面的连接。包含外连接或 CROSS 的链不会被仅适用于 INNER 的连接重排改变声明顺序。RIGHT/FULL/CROSS 当前使用嵌套循环：时间复杂度可达左右输入行数乘积，右端需要物化。该语义合同不承诺大规模笛卡尔积的性能或有界内存。

## 列冲突、类型和排序

- 投影顺序决定结果列顺序。`SELECT l.id, r.id` 的列名为 `l.id`、`r.id`；使用 `AS left_id`、`AS right_id` 可以给 Provider 稳定的结果名称。两端都有 `id` 时，未限定的来源引用 `SELECT id` 明确报歧义错误。
- FULL JOIN 不合并同名列，也不自动对两端执行 COALESCE 或统一类型。每个投影保留其来源值；未匹配端只补 SQL NULL。若业务需要单列结果，应显式编写受支持的表达式和唯一别名。
- 显式重复结果别名仍产生多个独立列，例如 `l.id AS same, r.id AS same`。ADO.NET 序号访问能取回两列，`GetOrdinal("same")` 按大小写不敏感匹配返回第一个结果列。Provider 应生成唯一别名，特别是随后需要按结果别名排序时；现有结果名查找同样采用首次匹配规则。
- 非空 `INT`、`FLOAT`、`BOOL`、`STRING` 值分别保持 CLR `Int64`、`Double`、`Boolean`、`String`；三入口回归逐值比较类型和值，并验证 `GetFieldType`。SQL NULL 由 ADO.NET `IsDBNull`/`DBNull` 表示。NULL 扩展可以使来源 `NOT NULL` 列在结果中为空。
- JOIN 沿用现有结果编解码，不新增跨协议类型转换。其他列类型仍受 [Frame 协议](frame-protocol.md) 和 [SQL 类型参考](sql-reference.md) 的既有合同约束，例如 REST 的 DATETIME 字符串与 Frame 的 DateTime 差异。ADO.NET 类型推断来自结果值：嵌入式/Frame 物化结果查找首个非空值，REST 由当前行取值；空结果、全空列或 REST 当前空值不能据此推断来源的完整 schema。
- 没有 `ORDER BY` 时不承诺结果行顺序。需要稳定分页时应显式列出足以消除并列的排序键。支持按限定来源列或唯一结果别名排序；未知排序列明确报错。
- `ASC` 将 NULL 排在非空值前，`DESC` 将 NULL 排在后。数值按数值比较，字符串沿用 ordinal 比较；连接不会引入新的 collation。多列排序依次比较，完全相同的排序键不承诺相互次序。`LIMIT`/`OFFSET` 在排序后应用，支持绑定整数参数。这里不扩展 `NULLS FIRST/LAST` 或新的排序方言。

## ADO.NET 能力发现和传输

`GetSchema("DataSourceInformation")` 的 `SupportedJoinOperators` 返回 `Inner | LeftOuter | RightOuter | FullOuter`，供 ORM 发现已实现连接。该字段是当前 SDK 的静态能力声明；远程连接不会用它协商旧 Server 的版本能力。枚举没有 Cross 成员，CROSS 支持由本 SQL 合同说明。

`Protocol=rest` 通过 `/v1/db/{database}/sql` 返回 NDJSON。`Protocol=frame-http2` 的 JOIN SELECT 通过真正 HTTP/2 的 `/v1/frame` 返回二进制结果；验收逐查询检查实际 endpoint 和 HTTP 版本，禁止把 REST 回落当成 Frame 通过。DDL/DML 仍使用已公开的 REST 写入入口。

只读凭据可以执行 JOIN SELECT，但不能写表。预取消的 ADO 请求在发送前抛出取消异常，同一连接随后可继续查询。此项验证的是预取消及连接复用，不是对运行中大规模 JOIN 的负载/取消延迟保证。完整停止并重启 Server 后，从持久化关系表重新执行 JOIN 的结果须与重启前及独立嵌入式库一致。

## 可复现验收

实现使用现有 `SqlParser`、`RelationalSelectExecutor` 和 measurement 边界 `JoinSqlExecutor`。本地语法/算子基线为 `RelationalStandardJoinTests`。三入口验收位于 [`RemoteStandardJoinTests.cs`](../tests/SonnetDB.Tests/RemoteStandardJoinTests.cs)，使用真实 Kestrel 和独立数据库，覆盖：

- RIGHT/FULL/CROSS 的重复键、NULL 键、左右未匹配行和空输入；
- 多连接链、绑定参数、ON/WHERE 的差别、排序和分页；
- LEFT 原有语义、列冲突、重复结果别名、歧义及未知排序列错误；
- 结果 CLR 类型、NULL 排序和 SDK 能力元数据；
- measurement 拒绝、只读权限、预取消后复用、完整宿主重启后结果对账；
- 每次 SELECT 的 REST 或 HTTP/2 Frame 实际传输断言。

```text
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --filter FullyQualifiedName~RelationalStandardJoinTests
dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj --filter FullyQualifiedName~RemoteStandardJoinTests
```

测试使用小型确定性数据，fixture 总超时 45 秒，每次读结果最多 63 行，HTTP 和关闭宿主均设置超时。该验收证明本合同的功能和客户端闭环；不会把它写成固定硬件性能、生产规模或长稳报告。
