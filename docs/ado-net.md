---
layout: default
title: "ADO.NET 参考"
description: "使用 SonnetDB.Data 连接本地嵌入式数据库或远程 SonnetDB。"
permalink: /ado-net/
---

## 安装

```bash
dotnet add package SonnetDB
```

## 连接字符串

| 模式 | 示例 |
| --- | --- |
| 嵌入式 | `Data Source=./demo-data` |
| 嵌入式别名 | `Data Source=sonnetdb://./demo-data` |
| 远程 | `Data Source=sonnetdb+http://127.0.0.1:5080/metrics;Token=<token>` |
| 远程显式库名 | `Data Source=sonnetdb+http://127.0.0.1:5080;Database=metrics;Token=<token>` |

常用键：

- `Data Source`
- `Database`
- `Token`
- `Timeout`

## 本地嵌入式示例

```csharp
using SonnetDB.Data;

using var connection = new SndbConnection("Data Source=./demo-data");
connection.Open();

using var create = connection.CreateCommand();
create.CommandText = "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)";
create.ExecuteNonQuery();

using var insert = connection.CreateCommand();
insert.CommandText = """
INSERT INTO cpu (time, host, usage)
VALUES (1713676800000, 'server-01', 0.71)
""";
insert.ExecuteNonQuery();

using var query = connection.CreateCommand();
query.CommandText = "SELECT time, host, usage FROM cpu";

using var reader = query.ExecuteReader();
while (reader.Read())
{
    Console.WriteLine($"{reader.GetInt64(0)} {reader.GetString(1)} {reader.GetDouble(2)}");
}
```

## 参数化查询

支持位置占位符 `?` 与命名占位符 `@name` / `:name`：

```csharp
using SonnetDB.Data;

using var connection = new SndbConnection("Data Source=./demo-data");
connection.Open();

// 命名参数
using var command = connection.CreateCommand();
command.CommandText = "SELECT host FROM cpu WHERE host = @host";
command.Parameters.AddWithValue("@host", "server-01");
using var reader = command.ExecuteReader();

// 位置参数（按添加顺序绑定）
using var q = connection.CreateCommand();
q.CommandText = "SELECT id FROM devices WHERE name = ? AND active = ?";
q.Parameters.AddWithValue("p0", "pump");
q.Parameters.AddWithValue("p1", true);
using var r = q.ExecuteReader();
```

说明：

- **嵌入式模式**：参数值直接绑定进已解析的 SQL AST（值绑定而非字符串拼接），从根上防注入，并可复用解析缓存（同一 query 形状不同参数值只解析一次）。
- **远程模式**：因线协议仅接受 SQL 字符串，命名参数仍在客户端安全转义为字面量后再发送；字符串中的单引号会正确转义。
- 参数类型映射：`byte[]` → BLOB（Base64）、`DateTime`/`DateTimeOffset` → Unix 毫秒、`GeoPoint` → `POINT(lat, lon)`、`null` → SQL `NULL`，数值/布尔/字符串各归对应字面量。
- `BeginTransaction()` / `BeginTransactionAsync()` 支持关系表轻事务；无参数或 `IsolationLevel.Unspecified` 默认映射为 `ReadCommitted`，也可显式请求 `ReadCommitted` 或关系基表专用的 `Serializable`。后者是表级首次读的乐观提交校验；冲突返回 `table_serialization_conflict` 并终止整笔事务，调用方必须从 BEGIN 重新读取并重试，不是 MVCC/可重复读快照或悲观行锁。
- SQL 文本 `BEGIN`（`SqlExecutor.ExecuteScript(...)` 或服务端 `/v1/db/{db}/sql/batch`）没有隔离级别语法，始终使用默认 `ReadCommitted`，不能从该入口请求 `Serializable`；嵌入式 ADO 命令中的 `BEGIN`/`COMMIT`/`ROLLBACK` 也被拒绝，应使用 `SndbConnection.BeginTransaction` API。
- 远程 ADO.NET 的显式 `Serializable` 会创建事务会话并发送 `X-SonnetDB-Isolation-Level: serializable`，只有 Server 回执 `isolationLevel: serializable` 时才继续，旧 Server 不会静默降级；嵌入式模式在本地创建相同隔离级别上下文。
- 轻事务可在同一数据库内提交多个关系表的 `INSERT` / `UPDATE` / `DELETE`，不支持 DDL、measurement / document 写入、嵌套事务或跨数据库事务

## 远程模式示例

```csharp
using SonnetDB.Data;

using var connection = new SndbConnection(
    "Data Source=sonnetdb+http://127.0.0.1:5080/metrics;Token=your-token;Timeout=30");
connection.Open();

using var command = connection.CreateCommand();
command.CommandText = "SHOW DATABASES";

using var reader = command.ExecuteReader();
while (reader.Read())
{
    Console.WriteLine(reader.GetString(0));
}
```

远程模式下：

- 数据面 SQL 通过 `POST /v1/db/{db}/sql` 调用
- 结果以 ndjson 流式返回，再转成 `DbDataReader`
- 服务端错误会映射成 `SndbServerException`

## DbFirst 元数据

`SndbConnection.GetSchema` 为关系表 DbFirst 提供结构化目录。`Tables`、`Columns`、`Indexes`、
`Views` 和 `ForeignKeys` 均返回 `TABLE_CATALOG`、`TABLE_SCHEMA` 与 `TABLE_NAME`，其中
`TABLE_SCHEMA` 是目录中保存的原始 schema 名称，不会因为 ADO、REST 或 frame/http2 传输而丢失。
`ForeignKeys` 另外返回 `PRINCIPAL_TABLE_SCHEMA`。`GetSchema` 的限制参数按 ADO.NET 约定排列：
`[catalog, schema, table, column-or-index-or-constraint]`；空值表示不限制，schema 和对象名按
保存的大小写精确匹配。

未限定的 `CREATE TABLE` 使用默认 schema `main`；两段限定名（例如 `sales.devices`）和双引号
标识符会保留各自的 schema、表名和大小写，因此 `main.devices` 与 `sales.devices` 可以同时被
FreeSql `DbFirst` 发现。关闭并重开连接或通过远程 `/v1/db/{db}/schema` 读取不会改变这些身份。
视图和文档集合的当前目录仍是单 schema 目录，`Views`、`DocumentCollections` 及其在 `Tables`
中的行固定报告 `main`；在目录支持视图 schema 前，provider 不应为它们猜测其它 schema。

## 远程异常处理

```csharp
using SonnetDB.Data;
using SonnetDB.Data.Remote;

try
{
    using var connection = new SndbConnection(
        "Data Source=sonnetdb+http://127.0.0.1:5080/metrics;Token=bad-token");
    connection.Open();

    using var command = connection.CreateCommand();
    command.CommandText = "SELECT * FROM cpu";
    command.ExecuteNonQuery();
}
catch (SndbServerException ex)
{
    Console.WriteLine($"{ex.StatusCode} {ex.Error} {ex.ServerMessage}");
}
```

常见错误标识包括：

- `unauthorized`
- `forbidden`
- `db_not_found`
- `sql_error`
- `table_unique_violation`
- `table_foreign_key_violation`
- `table_concurrency_conflict`
- `bulk_ingest_error`

## `ExecuteScalar` 与 `ExecuteReader`

当前推荐：

- 聚合或单值查询用 `ExecuteScalar`
- 普通查询用 `ExecuteReader`
- DDL 和写入用 `ExecuteNonQuery`

示例：

```csharp
using SonnetDB.Data;

using var connection = new SndbConnection("Data Source=./demo-data");
connection.Open();

using var command = connection.CreateCommand();
command.CommandText = "SELECT count(*) FROM cpu";

var count = (long)(command.ExecuteScalar() ?? 0L);
```

## 批量写入

ADO.NET 还支持 `CommandType.TableDirect` 快路径，适合：

- Line Protocol
- JSON points
- `INSERT INTO ... VALUES (...)` 快路径

详细示例见 [批量写入]({{ site.docs_baseurl | default: '/help' }}/bulk-ingest/)。

## 锁定读与乐观并发

`SndbCommand` 在显式 `SndbTransaction` 内支持单个关系基表的 `SELECT ... FOR UPDATE`、`FOR UPDATE NOWAIT` 和 `FOR UPDATE SKIP LOCKED`。同步 `ExecuteReader()`、异步 `ExecuteReaderAsync()`、嵌入式连接和远程 REST/HTTP2 连接使用相同的锁定结果和错误码：普通 `FOR UPDATE` 等待竞争行，`NOWAIT` 立即返回 `sql_lock_not_available`，`SKIP LOCKED` 不等待并跳过已被其它事务持有的行，再对剩余行应用 `ORDER BY`/`OFFSET`/`LIMIT`。成功返回的行锁持有到提交、回滚、连接关闭或远程会话过期；空结果和全部行被跳过时返回空 reader。`FOR SHARE` 以及 JOIN、聚合、CTE、派生表和子查询中的锁定读仍抛 `SqlParseException`，`Code=sql_locking_read_unsupported`，不会静默改写为普通 SELECT。

FreeSql 等 Provider 可以保留 `ForUpdate()` 的 `NOWAIT` 与 `SKIP LOCKED` 后缀；`FOR SHARE` 仍应在 Provider 侧明确拒绝。业务更新若不需要悲观锁，可使用 `ROWVERSION` 列读取版本、带版本谓词写入，并在 `table_concurrency_conflict` 或目标行消失时重试。
