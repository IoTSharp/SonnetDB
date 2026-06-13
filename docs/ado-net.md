---
layout: default
title: "ADO.NET 参考"
description: "使用 SonnetDB.Data 连接本地嵌入式数据库或远程 SonnetDB。"
permalink: /ado-net/
---

## 安装

```bash
dotnet add package SonnetDB --version 0.1.0
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

参数名支持 `@name` 和 `:name`：

```csharp
using SonnetDB.Data;

using var connection = new SndbConnection("Data Source=./demo-data");
connection.Open();

using var command = connection.CreateCommand();
command.CommandText = "SELECT host FROM cpu WHERE host = @host";
command.Parameters.AddWithValue("@host", "server-01");

using var reader = command.ExecuteReader();
```

说明：

- 参数会在客户端安全转义后再发送
- 字符串中的单引号会正确转义
- `BeginTransaction()` / `BeginTransactionAsync()` 支持关系表轻事务；隔离级别仅支持默认值或 `ReadCommitted`
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
