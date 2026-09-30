---
layout: default
title: "批量写入"
description: "使用 TableDirect、Line Protocol、JSON points 和 Bulk VALUES 快路径批量写入数据。"
permalink: /bulk-ingest/
---

## 为什么有批量快路径

除了普通 SQL `INSERT`，SonnetDB 还支持绕开 SQL 解析器的批量写入路径，适合：

- 更高吞吐量
- 更低分配
- 直接消费已经序列化好的 payload

当前支持三种格式：

- Line Protocol
- JSON points
- `INSERT INTO ... VALUES (...)` 快路径

## ADO.NET `CommandType.TableDirect`

```csharp
using System.Data;
using SonnetDB.Data;

using var connection = new SndbConnection("Data Source=./demo-data");
connection.Open();

using var command = connection.CreateCommand();
command.CommandType = CommandType.TableDirect;
command.CommandText = "cpu,host=server-01 value=1.0 1\ncpu,host=server-01 value=2.0 2";

var written = command.ExecuteNonQuery();
Console.WriteLine(written);
```

## 格式 1：Line Protocol

标准 payload：

```text
cpu,host=server-01 value=1.0 1
cpu,host=server-01 value=2.0 2
cpu,host=server-02 value=3.0 3
```

嵌入式模式下，上面这种 payload 可以直接写入。

远程模式下，为了让客户端在发送前确定目标 measurement，推荐两种做法：

### 做法 A：首行 measurement 前缀

```text
cpu
ignored,host=server-01 value=1.0 1
ignored,host=server-02 value=2.0 2
```

### 做法 B：通过参数显式指定

```csharp
using System.Data;
using SonnetDB.Data;

using var connection = new SndbConnection(
    "Data Source=sonnetdb+http://127.0.0.1:5080/metrics;Token=your-token");
connection.Open();

using var command = connection.CreateCommand();
command.CommandType = CommandType.TableDirect;
command.CommandText = "ignored,host=server-01 value=1.0 1\nignored,host=server-02 value=2.0 2";
command.Parameters.AddWithValue("measurement", "cpu");

command.ExecuteNonQuery();
```

## 格式 2：JSON points

```json
{
  "m": "cpu",
  "points": [
    {
      "t": 1,
      "tags": { "host": "server-01" },
      "fields": { "value": 1.5 }
    },
    {
      "t": 2,
      "tags": { "host": "server-02" },
      "fields": { "value": 2.5 }
    }
  ]
}
```

ADO.NET 示例：

```csharp
using System.Data;
using SonnetDB.Data;

using var connection = new SndbConnection("Data Source=./demo-data");
connection.Open();

using var command = connection.CreateCommand();
command.CommandType = CommandType.TableDirect;
command.CommandText = """
{
  "m": "cpu",
  "points": [
    {"t": 1, "tags": {"host": "server-01"}, "fields": {"value": 1.5}},
    {"t": 2, "tags": {"host": "server-02"}, "fields": {"value": 2.5}}
  ]
}
""";

command.ExecuteNonQuery();
```

## 格式 3：Bulk VALUES 快路径

```sql
INSERT INTO cpu(host TAG, value, time) VALUES
('server-01', 1.0, 1),
('server-02', 2.0, 2),
('server-03', 3.0, 3)
```

ADO.NET 示例：

```csharp
using System.Data;
using SonnetDB.Data;

using var connection = new SndbConnection("Data Source=./demo-data");
connection.Open();

using var command = connection.CreateCommand();
command.CommandType = CommandType.TableDirect;
command.CommandText = """
INSERT INTO cpu(host TAG, value, time) VALUES
('server-01', 1.0, 1),
('server-02', 2.0, 2)
""";

command.ExecuteNonQuery();
```

## 批量参数

`TableDirect` 当前支持以下参数名：

- `measurement`
- `onerror`
- `flush`

参数名大小写不敏感，也接受 `@measurement`、`:measurement` 这样的形式。

### `onerror=skip`

遇到坏行时跳过，而不是整批失败：

```csharp
command.Parameters.AddWithValue("onerror", "skip");
```

### `flush`

当前支持三档：

- `false` 或缺省：只写入 MemTable + WAL，最快
- `async`：只发出后台 flush 信号，不等待落盘完成
- `true`、`sync`、`1`、`yes`：同步 flush，等待落盘

例如：

```csharp
command.Parameters.AddWithValue("flush", "async");
```

## 直接调用 HTTP 端点

服务端提供三个专用批量入口：

| 端点 | 格式 |
| --- | --- |
| `POST /v1/db/{db}/measurements/{m}/lp` | Line Protocol |
| `POST /v1/db/{db}/measurements/{m}/json` | JSON points |
| `POST /v1/db/{db}/measurements/{m}/bulk` | Bulk VALUES |

### Line Protocol

```bash
curl -X POST "http://127.0.0.1:5080/v1/db/metrics/measurements/cpu/lp?flush=async" \
  -H "Authorization: Bearer <your-token>" \
  -H "Content-Type: text/plain" \
  --data-binary $'cpu,host=server-01 value=1.0 1\ncpu,host=server-02 value=2.0 2'
```

### JSON points

```bash
curl -X POST "http://127.0.0.1:5080/v1/db/metrics/measurements/cpu/json" \
  -H "Authorization: Bearer <your-token>" \
  -H "Content-Type: application/json" \
  -d '{"m":"cpu","points":[{"t":1,"tags":{"host":"server-01"},"fields":{"value":1.5}}]}'
```

### Bulk VALUES

```bash
curl -X POST "http://127.0.0.1:5080/v1/db/metrics/measurements/cpu/bulk?onerror=skip" \
  -H "Authorization: Bearer <your-token>" \
  -H "Content-Type: text/plain" \
  --data-binary "INSERT INTO cpu(host, value, time) VALUES ('server-01', 1.0, 1),('server-02', 2.0, 2)"
```

## 响应体

三个端点统一返回：

```json
{
  "writtenRows": 1024,
  "skippedRows": 0,
  "elapsedMilliseconds": 12.3
}
```

## 注意事项

- 目标 measurement 可以预先通过 `CREATE MEASUREMENT` 定义，也可以由首次写入自动推断创建。
- Line Protocol / JSON points 会根据 payload 中的 `tags` / `fields` 自动补齐缺失列。
- `Bulk VALUES` 会按已有 measurement schema 校验列角色和类型；未知列默认按 `FIELD` 推断，包括字符串列。要新增 TAG，在列列表中显式写 `host TAG`；`name FIELD` 可显式标注 FIELD。提示与已有列角色冲突时拒绝写入。
- 已有 `INT` 字段遇到 `FLOAT` 写入会提升为 `FLOAT`；已有 `FLOAT` 字段接收整数时会转换为浮点保存；其它类型漂移会失败或在 `onerror=skip` 下跳过。
- 远程 `TableDirect` 的 Line Protocol 推荐显式给出 measurement 前缀或参数。
- 写入权限至少需要 `readwrite` 角色。

## 相关页面

- [SQL 参考]({{ site.docs_baseurl | default: '/help' }}/sql-reference/)
- [ADO.NET 参考]({{ site.docs_baseurl | default: '/help' }}/ado-net/)
- [开始使用]({{ site.docs_baseurl | default: '/help' }}/getting-started/)
