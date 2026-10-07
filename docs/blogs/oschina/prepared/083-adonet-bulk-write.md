## ADO.NET 批量写入：使用 CommandType.TableDirect 实现高速数据摄入

SonnetDB 将 `CommandType.TableDirect` 用作批量摄入快路径。这里的 `CommandText` 是一批 Line Protocol、JSON points 或 Bulk VALUES **正文**，不是仅填目标 measurement 名，再通过 `@time`、`@price` 逐行喂数据。

本文依据当前批量文档整理，正式 4.0.0 与 main 分开核对。性能应在目标硬件、实际批大小与 flush 策略下测量，不能用没有可复现证据的倍数承诺生产吞吐量。

### 一次提交一个有界批次

```csharp
using System.Data;
using SonnetDB.Data;

using var connection = new SndbConnection("Data Source=./bulk-stock-demo");
connection.Open();
using var create = connection.CreateCommand();
create.CommandText = """
CREATE MEASUREMENT stock_ticks (
    symbol TAG, price FIELD FLOAT, volume FIELD INT,
    bid FIELD FLOAT, ask FIELD FLOAT
)
""";
create.ExecuteNonQuery();

using var bulk = connection.CreateCommand();
bulk.CommandType = CommandType.TableDirect;
bulk.CommandText = """
stock_ticks,symbol=AAPL price=182.5,volume=100i,bid=182.4,ask=182.6 1713676800000
stock_ticks,symbol=MSFT price=415.2,volume=200i,bid=415.1,ask=415.3 1713676801000
""";
bulk.Parameters.AddWithValue("measurement", "stock_ticks");
bulk.Parameters.AddWithValue("flush", "async");
Console.WriteLine($"写入行数：{bulk.ExecuteNonQuery()}");
```

这一批只有两行，适合验证格式和 schema。实际应用逐步增大批次，并同时限制行数、编码后的字节数、待发送队列和总运行时间。不要默认创建百万个对象后一次提交。

### 三种载荷对应同一个入口

Line Protocol 每行一个点，整数用 `i` 后缀。JSON points 使用紧凑键 `m`、`t`、`tags`、`fields`：

```json
{
  "m": "stock_ticks",
  "points": [
    {"t":1713676802000,"tags":{"symbol":"AAPL"},"fields":{"price":182.7,"volume":120}}
  ]
}
```

将上面的 JSON 放入 `bulk.CommandText` 即可。Bulk VALUES 载荷则是完整语句：

```sql
INSERT INTO stock_ticks(symbol TAG, price, volume, bid, ask, time) VALUES
('AAPL', 182.8, 130, 182.7, 182.9, 1713676803000),
('MSFT', 415.5, 210, 415.4, 415.6, 1713676804000)
```

此模式由批量 reader 处理；普通 `CommandType.Text` 仍走 SQL 执行路径。已有 schema 会校验列角色和类型；Bulk VALUES 新字符串列不会自动成为 TAG，要显式写 `symbol TAG` 或预先建表。

### 控制参数和错误

| 参数 | 含义 |
| --- | --- |
| `measurement` | 显式目标 measurement，远程 LP 推荐给出 |
| `onerror=skip` | 对可跳过的坏行继续处理，需要核对跳过计数 |
| `flush=false` 或省略 | 写入 MemTable 与 WAL，不强制本批同步 flush |
| `flush=async` | 发出后台 flush 信号，不等待其完成 |
| `flush=true` / `sync` | 等待同步 flush |

参数名大小写不敏感，也接受 `@measurement`、`:measurement` 的形式。`onerror=skip` 不能代替业务校验，严格导入应保留输入批次与错误记录，对账写入数和查询结果。

### 本地与远程

远程连接可以换成 `Data Source=sonnetdb+https://db.example.com/metrics;Token=<token>`，程序结构相同。直接 HTTP 的对应端点为 `/v1/db/{db}/measurements/{m}/lp`、`/json`、`/bulk`，响应提供 `writtenRows`、`skippedRows` 和 `elapsedMilliseconds`。

measurement 批量写入不属于关系表轻事务的支持范围，不能用 `BeginTransaction` 承诺所有点原子回滚。发生超时的批次先对账再重试，逐条调用异步方法也不会自动变成一个服务端批次。

应用已有 `Point` 对象时，可使用核心引擎 `Tsdb.WriteMany`；它与 `TableDirect` 是两种输入方式，不能使用没有定义的 `SndbPoint` 或空缺的“自动并行 API”代替实际代码。

参考：[批量写入](https://github.com/IoTSharp/SonnetDB/blob/main/docs/bulk-ingest.md)、[嵌入式 API](https://github.com/IoTSharp/SonnetDB/blob/main/docs/embedded-api.md)、[正式 4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
