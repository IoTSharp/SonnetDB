## ADO.NET 远程连接：使用 sonnetdb+http:// 协议连接远程 SonnetDB 服务

同一套 `SndbConnection`、`SndbCommand` 和 `SndbDataReader` 可以连接远程 SonnetDB。应用主要调整连接字符串、认证和超时，不必把业务代码全部改成手写 HTTP 请求。

本文依据当前 ADO.NET 与连接字符串实现整理。正式 4.0.0 是独立发布标签，main 新增的传输或认证能力应与实际客户端、服务端版本配套核对。

### 连接字符串的完整写法

```text
Data Source=sonnetdb+http://127.0.0.1:5080/metrics;Token=<token>;Timeout=30
Data Source=sonnetdb+https://db.example.com/metrics;Token=<token>;Timeout=30
```

`sonnetdb+http` / `sonnetdb+https` 用来选择远程模式，URL 路径指定数据库。Token 是连接字符串中的独立键，不应写成 URL 的 `?token=`；服务端地址通常使用 5080，生产部署应改成自己的 HTTPS 入口。

还可以显式指定库名：

```text
Data Source=sonnetdb+https://db.example.com;Database=metrics;Token=<token>
```

如果 URL 路径和 `Database` 同时存在，以后者为准。

### 不把凭据硬编码进代码

```csharp
using SonnetDB.Data;

var token = Environment.GetEnvironmentVariable("APP_SONNETDB_TOKEN")
    ?? throw new InvalidOperationException("缺少 APP_SONNETDB_TOKEN");
var builder = new SndbConnectionStringBuilder
{
    DataSource = "sonnetdb+https://db.example.com/metrics",
    Token = token,
};
builder["Timeout"] = 30;
builder["Protocol"] = "rest";

using var connection = new SndbConnection(builder.ConnectionString);
connection.Open();
using var command = connection.CreateCommand();
command.CommandText = """
SELECT time, temperature FROM sensor_data
WHERE device_id = @device AND time >= @from
ORDER BY time DESC LIMIT 50
""";
command.Parameters.AddWithValue("@device", "sensor-01");
command.Parameters.AddWithValue("@from", 1713676800000L);
using var reader = command.ExecuteReader();
while (reader.Read())
    Console.WriteLine($"{reader.GetInt64(0)} {reader.GetDouble(1)}");
```

此示例假设目标数据库和 `sensor_data` 已存在，账户具有读取权限。Token 应由实际管理界面或认证 API 签发；不能把未经核实的 `sndb token create` 当作现有 CLI 命令。

当前也支持 PostgreSQL 风格连接键：`Host`、`Port`、`Database`、`Username`、`Password`、`Ssl`。提供 `Username` 时走 HTTP Basic；否则回退到 Bearer Token。密码和 token 都不应进入日志。

### REST 与帧协议

当前 `Protocol` 支持 `auto`、`frame-http2`、`rest`。`auto` 探测帧协议并按合同回落 REST；示例显式使用 `rest`，方便对应 `POST /v1/db/{db}/sql` 与 NDJSON 响应。强制帧协议需要服务端开放正确的 HTTP/2 监听与认证配置。

远程参数值由客户端按安全 SQL 字面量转义后发送；不要自己用字符串插值替代参数 API。HTTP 传输的连接复用，也不等于驱动提供了任意 `MaxPoolSize` / `ConnectTimeout` 的数据库连接池配置，当前合同使用的是 `Timeout`。

### 失败处理与写入结果未知

服务端错误映射成 `SonnetDB.Data.Remote.SndbServerException`，可查看 `StatusCode`、`Error` 和 `ServerMessage`。例如 `unauthorized`、`forbidden`、`db_not_found`、`sql_error` 应分别处理；换密码不能解决没有建库的问题。

读取请求可以按应用预算重试。写入超时或连接断开时，客户端可能无法判断服务端是否已经执行，必须结合业务键、查询对账或幂等设计确认，不能直接重复整个批次。measurement 写入也不属于关系表轻事务的支持范围。

参考：[ADO.NET](https://github.com/IoTSharp/SonnetDB/blob/main/docs/ado-net.md)、[连接字符串源码](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Data/SndbConnectionStringBuilder.cs)、[帧协议](https://github.com/IoTSharp/SonnetDB/blob/main/docs/frame-protocol.md)、[正式 4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
