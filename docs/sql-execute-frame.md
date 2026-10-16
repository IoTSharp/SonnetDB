# SQL Execute Frame v1（M49-F01 首片）

此入口用于 `SonnetDB.Client` 的独立远程产品线，不依赖 Core/Data。它完成一个可独立验证的关系表写入切片；完整 F01、旧 ADO 迁移、事务、多结果和 Core pull 执行仍待实现。

## 调用与范围

```csharp
using SonnetDB.Client;
using SonnetDB.Protocol;

using var client = new SndbFrameClient(new Uri("http://127.0.0.1:5080/"), "configured-token");
var result = await client.ExecuteSqlAsync("demo",
    "INSERT INTO readings (id, amount) VALUES (@id, @amount)",
    [SqlExecuteFrameCodec.FromClr("id", long.MaxValue),
     SqlExecuteFrameCodec.FromClr("amount", 9007199254740993.123456m)]);
Console.WriteLine(result.RecordsAffected);
```

Server 必须为该地址配置真正的 HTTP/2（h2c 或 TLS/ALPN）。客户端每次只发送一个 `POST /v1/frame`，采用 `RequestVersionExact=HTTP/2` 并校验实际响应协议，没有 REST/HTTP1、重定向或业务重试。

首片允许关系表 `CREATE TABLE`（无 Modbus 绑定）、`DROP TABLE`、无 `RETURNING` 的 `INSERT/UPDATE/DELETE`。名称解析、主键/外键/触发器及语句原子性复用现有引擎。Write 权限、SQL admission、例程/触发器预算与取消继续生效；并不新增跨模型事务或 fsync 保证，确认的持久性仍取决于引擎配置。

事务控制、`RETURNING`、过程/多结果、只读查询、其它 DDL/模型写入和控制面语句执行前返回 `unsupported_capability`。Workbench Preview 的此入口返回 `preview_action_denied`。旧 `Sql/Query=1` 的只读及结果格式合同保持原状；旧混合 `SonnetDB.Data` 的 ADO/REST/session 行为也保持原状，后续需要在 Frame 多结果/事务齐备后迁移。

## 字节合同

Frame 信封 v1、service=3（SQL）、op=2（Execute），请求 flags=0；响应必须匹配版本、service/op、streamId。不得把 op=1 改为写入。多字节数均为 little-endian；varuint 为 LEB128；varstr 为 varuint UTF-8 字节长度加原始字节，严格拒绝非法 UTF-8。

请求 payload 顺序：`u8 executeVersion=1`、`varstr db`、`varstr sql`、`varuint parameterCount`，随后每个参数为 `varstr name`、`u8 kind`、`u8 hasValue`、可选值。空名称表示按列表顺序绑定位置参数 `?`；非空名称不含 `@/:`，按 OrdinalIgnoreCase 唯一匹配占位符，不允许混合命名和位置参数。`hasValue=0` 保留 kind 声明但无值；`hasValue=1` 必须携带该类型的值。Kind=0 只能没有值。

| kind | 类型 | 非 NULL 值字节 |
|---|---|---|
| 0 | NULL | 无 |
| 1 | Int64 | i64 |
| 2 | Float64 | f64，必须有限 |
| 3 | Boolean | u8 0/1 |
| 4 | String | varstr |
| 5 | Blob | varuint 长度 + 原始字节 |
| 6 | Timestamp | i64 Unix 毫秒整数，与现有 SQL 数值输入语义相同 |
| 7 | Decimal | 4 × i32，按 CLR decimal.GetBits 的 lo/mid/hi/flags，保留 scale/符号，拒绝保留位/非法 scale |
| 8 | DateTimeOffset | i64 UTC ticks，解码为零偏移 |
| 9 | Duration | i64 TimeSpan ticks |
| 10 | Time | i64 一天内 TimeOnly ticks |
| 11 | Vector | varuint 维度 + f32 × 维度，必须非空且全部有限 |
| 12 | DateTime | i64 UTC ticks；FromClr 将 Local 转 UTC，将 Unspecified 按 UTC 处理 |

Server 通过 `SqlParameters`/`SqlParameterBinder` 绑定 AST，不改写 SQL 字符串。既有 DATETIME 存储精度为毫秒：DateTime 参数必须毫秒对齐，否则执行前返回 `unsupported_parameter_precision`，不静默截断；完整 100ns 时刻使用 DATETIMEOFFSET。DateTimeOffset 与 Duration 保留完整 ticks，使用其既有原生 CAST 合同。传输的 Vector 类型不意味着当前关系表 SQL 已接受向量列；模型能力按实际 schema 单列判断。

成功响应 payload 固定为 `u8 executeVersion=1`、`i64 recordsAffected`（非负）、`u8 outcome=1`。Outcome=1 表示现有引擎在请求作用域内完成自动提交；DDL 的影响行数为零。它是此单响应操作的完整终态，没有另一个 end 帧。未知版本、非法终态、负数、截断、额外字节或额外帧都不算成功。

错误响应沿用 Frame 的 `Response|Error`，payload 为 varstr code/message。Execute payload 版本不支持时为 `unsupported_execute_version`，非法输入为 `bad_frame`，缺能力为 `unsupported_capability`，权限为 `forbidden`，约束保留原 `table_*` 错误码。旧 Server 不认识 op=2 时拒绝 `unsupported_op`，客户端不回落、不重发。

## 预算、失败与验收边界

单个请求累计 payload ≤4 MiB，db/参数名各≤512 UTF-8 字节，SQL≤1 MiB，参数≤256，向量维度≤1,000,000，单值还受累计预算限制。编码前验证上限，解码前校验长度和版本；这只约束编码输入，并不等于数据库执行堆、RSS 或九模型总资源预算。

客户端的整请求期限包含响应读取；已发送后的取消、超时、断连、非法响应及服务端错误都保守保留 `OutcomeUnknown`。即使请求返回错误，也必须按操作核对，不能据此推断完全没有提交。真实断连回归在引擎提交后中断响应，验证一条写入仍在、客户端报告未知且没有自动重试。

测试/源码绑定及未验项见 [F01 审计](audits/m49/f01-20261017/README.md)。本片未运行 TLS/ALPN、SDK NativeAOT 发布、其它平台/语言或正式包消费者；不以 h2c 或本机重开代替这些门禁。
