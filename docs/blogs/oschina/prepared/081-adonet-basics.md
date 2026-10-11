## ADO.NET 基础：使用 SndbConnection、SndbCommand 和 SndbDataReader 连接本地 SonnetDB

SonnetDB 的 ADO.NET 驱动让 .NET 应用通过熟悉的连接、命令和 reader 操作数据库。本地模式打开数据库目录，引擎在当前进程执行 SQL；远程模式沿用相同的对象模型，连接方式和传输路径有所不同。

本文依据当前 `SonnetDB.Data` 合同整理。正式 4.0.0 与 main 的能力边界应分别核对，以下代码用于新的演示目录，不是生产迁移脚本。

### 安装与本地连接

```bash
dotnet add package SonnetDB
```

```csharp
using SonnetDB.Data;

using var connection = new SndbConnection("Data Source=./ado-sensor-demo");
connection.Open();

using var create = connection.CreateCommand();
create.CommandText = """
CREATE MEASUREMENT sensor_data (
    device_id TAG,
    temperature FIELD FLOAT,
    humidity FIELD FLOAT,
    voltage FIELD FLOAT
)
""";
create.ExecuteNonQuery();

using var insert = connection.CreateCommand();
insert.CommandText = """
INSERT INTO sensor_data (time, device_id, temperature, humidity, voltage)
VALUES (1713676800000, 'sensor-01', 23.5, 65.2, 3.3),
       (1713676801000, 'sensor-01', 23.7, 64.8, 3.4)
""";
Console.WriteLine($"写入行数：{insert.ExecuteNonQuery()}");
```

`Data Source` 指向目录。`sonnetdb://./ado-sensor-demo` 也是本地模式别名。不要把数据库目录理解成单文件，也不要把连接打开成功当作磁盘恢复测试已经通过。

### 参数绑定和结果读取

接着使用同一个连接：

```csharp
using var query = connection.CreateCommand();
query.CommandText = """
SELECT time, temperature, humidity FROM sensor_data
WHERE device_id = @deviceId
  AND time >= @startTime AND time < @endTime
ORDER BY time DESC LIMIT 100
""";
query.Parameters.AddWithValue("@deviceId", "sensor-01");
query.Parameters.AddWithValue("@startTime", 1713676800000L);
query.Parameters.AddWithValue("@endTime", 1713763200000L);

using var reader = query.ExecuteReader();
while (reader.Read())
{
    var timestamp = reader.GetInt64(0);
    var temperature = reader.IsDBNull(1) ? (double?)null : reader.GetDouble(1);
    var humidity = reader.IsDBNull(2) ? (double?)null : reader.GetDouble(2);
    Console.WriteLine($"{timestamp}: {temperature} °C, {humidity} %");
}
```

本地命名参数绑定进解析后的 AST，可复用同一查询形状的解析缓存。也支持 `:name` 与位置占位符 `?`。参数用于数据值，不能直接替代任意表名或列名。

Reader 提供逐行读取 API，但这一接口形态不保证所有执行路径都以常量内存处理结果。客户端仍应限定时间范围、列数和返回行数，并按列类型调用 getter。不要把 `FLOAT` 字段无条件当作 `Int32`。

### 选择正确执行方法

| 任务 | 方法 |
| --- | --- |
| 建表和写入 | `ExecuteNonQuery` |
| 第一行第一列的单值 | `ExecuteScalar` |
| 多行查询 | `ExecuteReader` |

```csharp
using var count = connection.CreateCommand();
count.CommandText = "SELECT count(*) FROM sensor_data";
Console.WriteLine(count.ExecuteScalar());
```

异步方法也可使用；调用方应传入有超时的取消令牌并及时释放 command、reader 和 connection，而不是在应用退出前一直保留结果对象。

### 事务适用范围

当前轻事务支持同一数据库内多个**关系表**的小批量 `INSERT`、`UPDATE`、`DELETE`，隔离级别为默认或 `ReadCommitted`。它不支持 measurement/document 写入、DDL、嵌套或跨数据库事务。因此上面的时序 measurement 插入不能通过套一层 `BeginTransaction` 获得整批原子提交。

关系表并发更新可以结合 `ROWVERSION` 和版本谓词；`SELECT ... FOR UPDATE`、`NOWAIT`、`SKIP LOCKED` 当前会被明确拒绝。迁移通用 ADO.NET 代码时应先核对这些合同。

参考：[ADO.NET 文档](https://github.com/IoTSharp/SonnetDB/blob/main/docs/ado-net.md)、[命令实现](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Data/SndbCommand.cs)、[正式 4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
