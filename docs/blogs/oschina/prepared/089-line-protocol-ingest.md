## SonnetDB 行协议写入：兼容 InfluxDB Line Protocol 的大批量数据接入

SonnetDB 的 LP reader 解析 InfluxDB Line Protocol 子集，并通过统一时序写入内核处理数据。已有 Telegraf 等客户端可以接入兼容写端点，但 LP 格式兼容不等于兼容 InfluxDB 的全部查询/管理 API，也不等于 Prometheus remote write 协议。

本文依据当前 reader 和 HTTP handler 整理，正式 4.0.0 与 main 的接口应按实际版本核对。下面先明确两种 HTTP 入口的 measurement 和时间精度，再给出小批次示例。

### 行格式和类型

```text
<measurement>[,<tag>=<value>...] <field>=<value>[,<field>=<value>...] [<timestamp>]
```

```text
sensor,host=edge-01 temperature=23.5,count=42i,healthy=true,status="running" 1713676800000
sensor,host=edge-02 temperature=21.8,count=43i,healthy=false,status="idle" 1713676801000
```

裸数字表示浮点，`i` 后缀表示整数，布尔支持 true/false 等 reader 规定的形式，字符串用双引号。空行和 `#` 开头的注释可跳过；measurement/tag/key 中的空格、逗号和等号应按转义合同编码。未给时间戳时使用服务端默认 UTC 毫秒时间。

### 专用 LP 端点：URL 指定 measurement，时间戳毫秒

把上面的两行存入 `data.lp`：

```bash
curl --fail-with-body --max-time 30 \
  "http://127.0.0.1:5080/v1/db/metrics/measurements/sensor/lp?flush=async" \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: text/plain" --data-binary @data.lp
```

该 handler 使用 reader 的默认毫秒精度，URL 中的 `sensor` 覆盖行内 measurement。不要把纳秒整数直接写入这一入口；它不会自动根据位数猜单位。成功响应是 JSON 写入/跳过计数，`async` 只发 flush 信号，不等待完成。

### Influx 兼容入口：每行指定 measurement

```bash
curl --fail-with-body --max-time 30 \
  "http://127.0.0.1:5080/write?db=metrics&precision=ms" \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: text/plain" --data-binary @data.lp
```

v2 路由是 `/api/v2/write?bucket=metrics&precision=ms`。这两种兼容 handler 默认纳秒，支持 `n/ns`、`u/us/µs`、`ms`、`s`，内部转换到毫秒；纳秒输入不代表引擎保留纳秒级时间分辨率。`org` 不提供独立组织隔离语义。

兼容端点支持 gzip，请求成功返回 `204 No Content`；目标数据库必须已存在，凭据必须有写权限。

### ADO.NET TableDirect

```csharp
using System.Data;
using SonnetDB.Data;

using var connection = new SndbConnection("Data Source=./lp-demo");
connection.Open();
using var command = connection.CreateCommand();
command.CommandType = CommandType.TableDirect;
command.CommandText = """
sensor,host=edge-01 temperature=23.5 1713676800000
sensor,host=edge-02 temperature=21.8 1713676801000
""";
command.Parameters.AddWithValue("measurement", "sensor");
command.Parameters.AddWithValue("flush", "async");
Console.WriteLine(command.ExecuteNonQuery());
```

这一入口同样使用毫秒示例。远程模式推荐显式给 `measurement` 参数，避免目标推断歧义。

### Schema 与 Telegraf

LP/JSON 路径可按策略创建 measurement 或补齐 tag/field 列；生产部署仍应预先建表并核查摄入 schema 策略。已有 INT 接收 FLOAT 可以提升为 FLOAT，其它类型漂移可能失败或按 `onerror=skip` 跳过。新增 schema 不应因为客户端仅改变标识符大小写而制造另一套列名。

Telegraf v2 输出的示例配置如下，需先创建 `metrics` 并换成自己的 token：

```toml
[[outputs.influxdb_v2]]
  urls = ["http://127.0.0.1:5080"]
  token = "${SONNETDB_TOKEN}"
  organization = "example"
  bucket = "metrics"
```

先用小批次验证时间和字段，再启用持续采集。v1 客户端的自动建库调用不应假定被兼容，预建数据库并关闭自动创建更容易控制。

Prometheus remote write 使用另外的 `/api/v1/prom/write?db=...`，正文是 snappy 与 protobuf；不能把它当作 LP 文本发送。UDP、MQTT、CoAP 入口还有各自的鉴权和确认边界。吞吐量需要目标环境基准，不应直接引用没有配置和复现记录的百万点数字。

参考：[批量写入](https://github.com/IoTSharp/SonnetDB/blob/main/docs/bulk-ingest.md)、[协议接入](https://github.com/IoTSharp/SonnetDB/blob/main/docs/protocol-ingest.md)、[兼容 handler](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB/Endpoints/Handlers/InfluxLineProtocolEndpointHandler.cs)、[正式 4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
