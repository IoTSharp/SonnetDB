---
layout: default
title: "Graph GQL 风格查询入口"
description: "SonnetDB M40 #364 受限 GQL 风格只读查询的语法矩阵、.NET API、SQL/PGQ 等价形式与边界。"
permalink: /graph-gql/
---

# Graph GQL 风格查询入口

M40 #364 提供一个显式 opt-in 的嵌入式 .NET 查询入口。它把受限的
`USE GRAPH ... MATCH ... RETURN` 语法直接解析为现有 `SelectStatement` 和
`GraphTableSource`，随后调用与 SQL/PGQ `GRAPH_TABLE` 相同的成本规划、原生邻接或关系映射 accessor、预算和执行器。

这不是完整 GQL 或 Cypher 实现。原生属性图已作为第九种数据模型纳入产品定位，当前阶段仍是 SonnetDB Graph Beta；固定硬件、LDBC/Graphalytics、七天 mixed workload、恢复与发布门禁归 M40 #367，在该门禁通过前不宣称图能力生产就绪。

## .NET 入口

```csharp
var parameters = new SqlParameters().AddNamed("anchor", 42L);

SelectExecutionResult result = SqlExecutor.ExecuteGql(
    db,
    """
    USE GRAPH topology
    MATCH (a IS 1)-[e IS 2]->(b IS 1)
    WHERE a.id = @anchor
    RETURN a.id AS source_id, b.id AS target_id, e.id AS edge_id
    ORDER BY edge_id
    LIMIT 100
    """,
    parameters);
```

`GqlParser.Parse` 可单独取得现有 SQL typed AST。`EXPLAIN` 和 `EXPLAIN ANALYZE` 放在 `USE GRAPH` 前；两者返回的计划字段与等价 SQL/PGQ 查询相同，不增加 GQL 专用执行计划。

远程 HTTP、ADO.NET、Studio 和连接器当前继续使用等价的 SQL/PGQ `GRAPH_TABLE`。本项没有增加新的 wire endpoint、权限旁路或 mutation 通道。

## 语法

```text
[EXPLAIN [ANALYZE]]
USE GRAPH graph_name
MATCH [path =] [ANY SHORTEST] [WALK|TRAIL|SIMPLE|ACYCLIC]
      (left IS label)-[edge IS label]->{min,max}(right IS label)
[WHERE graph_expression]
RETURN [DISTINCT] expression [AS output_name] [, ...]
[ORDER BY output_name [ASC|DESC] [, ...]]
[LIMIT count [OFFSET offset] | OFFSET offset [ROWS] [FETCH NEXT count ROWS ONLY]]
```

方向也可写为 `<-[edge]-` 或 `-[edge]-`。固定一跳省略 `{min,max}`；可变路径满足 `1 <= min <= max <= 64`。原生图的 label 使用正整数 ID，关系映射图使用 catalog 中声明的 label 名称。

## 能力矩阵

| 能力 | 状态 | 边界 |
|---|---|---|
| 原生 graph | 支持 | 使用 `NativeGraphAccessor`、statement snapshot 和原生 adjacency/index |
| SQL/PGQ 关系映射 graph | 支持 | 使用相同 `RelationalGraphAccessor`；index seek 或有界 scan fallback 会如实进入 EXPLAIN |
| 固定一跳出向、入向、无向模式 | 支持 | 单个 pattern、两个 vertex 变量和一个 edge 变量 |
| `WALK` / `TRAIL` / `SIMPLE` / `ACYCLIC` | 支持 | 有界 1~64 hop，语义与 `GRAPH_TABLE` 相同 |
| `ANY SHORTEST` | 支持 | 复用现有 BFS/path plan；未凭证据启用第二套 bidirectional BFS |
| 变量属性谓词和参数 | 支持 | `?`、`@name`、`:name` 使用 `SqlParameters` 绑定，不做字符串拼接 |
| 显式 `RETURN`、`DISTINCT` | 支持 | 重名列必须用 `AS` 区分；`RETURN *` 不支持 |
| `ORDER BY`、`LIMIT/OFFSET`、`FETCH` | 支持 | `ORDER BY` 引用 `RETURN` 输出名 |
| `EXPLAIN` / `EXPLAIN ANALYZE` | 支持 | 与等价 SQL/PGQ 返回同一计划和运行证据 |
| GQL DDL/DML | 不支持 | graph 创建和 mutation 继续使用现有 Graph API 或 SQL，并服从原权限/事务合同 |
| 多 pattern、`OPTIONAL MATCH`、`UNION`、`GROUP BY` | 不支持 | 需要组合时使用现有 SQL 派生表和共享关系执行器 |
| Cypher `:Label`、`MERGE`、`WITH`、`UNWIND`、`CALL` | 不支持 | 不声明 Cypher 兼容，不做静默语法改写 |
| 完整 ISO GQL、Bolt、分布式图 | 不支持 | 不在 M40 #364 范围 |

## SQL/PGQ 等价形式

下面两条查询生成相同的 Graph AST、计划和结果：

```text
USE GRAPH social
MATCH (a IS person)-[e IS knows]->(b IS person)
WHERE a.id = @anchor
RETURN a.id AS source_id, b.id AS target_id, e.id AS edge_id
ORDER BY edge_id
LIMIT 10
```

```sql
SELECT source_id, target_id, edge_id
FROM GRAPH_TABLE (
    social
    MATCH (a IS person)-[e IS knows]->(b IS person)
    WHERE a.id = @anchor
    COLUMNS (a.id AS source_id, b.id AS target_id, e.id AS edge_id)
)
ORDER BY edge_id
LIMIT 10;
```

自动回归对拍原生 shortest path、关系映射 index access、命名参数、排序/分页、typed AST 和逐行 EXPLAIN。写语法、多语句输入、`RETURN *` 和未承诺的 Cypher label 形式会在解析阶段拒绝。
