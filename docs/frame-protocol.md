# 二进制帧协议（Frame Protocol）

> M28 P5b #235 引入的通用高吞吐接入通道。与既有 REST/JSON 端点**并列新增**，所有 REST 端点保持不变；
> 帧层只做传输编码，不改变任何引擎查询/写入语义。

## 设计动机

SonnetDB 全模型此前只有 HTTP+JSON 一条接入通道：MQ/对象 payload 走 Base64（+33% 体积 + 编解码 CPU）、
向量 `float[]` 走 JSON 数字文本、大结果集全量物化 JSON。帧协议消灭 JSON/Base64 税：
二进制 payload 原始字节直传，多帧一体、`stream-id` 关联，为 #236 推送订阅与 #238 流式结果集铺路。

## 传输承载

- **端点**：`POST /v1/frame`，Content-Type `application/x-sonnetdb-frame`。
- **HTTP/2 h2c 专用口**（推荐）：默认配置监听 `5081` 端口（`Protocols: Http2`，先验知识 h2c）。
  明文端点无法在同一端口协商 HTTP/1.1 与 HTTP/2，故单独开口；整个应用（含 REST）在两个口都可达。
- **HTTP/1.1 回退**：`/v1/frame` 在主端口 `5080` 也可用（HTTP/1.1）。注意服务端逐帧流式响应，
  响应可能在请求体读完之前开始——直连客户端无碍，严格代理可能不兼容，推荐走 h2c 口。
- **鉴权**：复用既有 Bearer token 与三角色权限模型；`/v1/frame` 无匿名豁免，缺 token 返回 401。

```jsonc
// appsettings.json
"Kestrel": {
  "Endpoints": {
    "Http":    { "Url": "http://0.0.0.0:5080" },
    "FrameH2": { "Url": "http://0.0.0.0:5081", "Protocols": "Http2" }
  }
}
```

```bash
curl --http2-prior-knowledge -X POST http://localhost:5081/v1/frame \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/x-sonnetdb-frame" \
  --data-binary @frames.bin
```

## 帧头（固定 12 字节，little-endian）

| 偏移 | 大小 | 字段 | 说明 |
|------|------|------|------|
| 0 | 4 | `PayloadLength` (u32) | 帧体字节数（不含帧头）。上限 132 MiB（138 412 032），超限拒绝 |
| 4 | 1 | `Version` (u8) | 当前 `1`；其他值 → `unsupported_version` |
| 5 | 1 | `Service` (u8) | 目标 service，见下表；`0` 保留 |
| 6 | 1 | `Op` (u8) | service 内 opcode |
| 7 | 1 | `Flags` (u8) | bit0=Response，bit1=Error（隐含 Response）；bit2~7 保留，v1 请求中必须为 0 |
| 8 | 4 | `StreamId` (u32) | 客户端选定的关联 id，响应帧原样回显 |

请求体 = 1..N 个请求帧连续排列；服务端按请求顺序逐帧处理、逐帧写响应帧。
帧间无对齐或分隔符，靠 `PayloadLength` 定界。

### 前向兼容规则

- `Version` 字节门控整体布局；解析方遇到未知版本必须拒绝（`unsupported_version`），不得猜测。
- Flags 保留位 **MBZ**（must be zero）：v1 请求含保留位 → `bad_frame`。后续版本经 Version 升级启用。
- Service/Op 编号只增不改；未挂载的 service/op 返回 `unsupported_service` / `unsupported_op` 错误帧。

## 基元编码约定

| 基元 | 编码 |
|------|------|
| `varuint` | LEB128（32 位最多 5 字节，64 位最多 10 字节） |
| `varstr` | `varuint` UTF-8 字节长度 + UTF-8 字节（无 null 表示） |
| `bytes` | `varuint` 长度 + 原始字节（零 Base64） |
| `i64` | 8 字节 little-endian（固定宽度，用于时间戳 ticks） |

解码期防御上限：名字（db/topic/consumerGroup/header key）≤ 512 字节、
单条消息 header 数 ≤ 1024、headers 总量 ≤ 64 KiB。引擎侧（`SonnetMqStore`）的权威校验不变。

## Service / Op 矩阵

| Service | 编号 | Op | 状态 |
|---------|------|-----|------|
| `mq` | 1 | 1=publish, 2=publish-batch, 3=pull, 4=ack | ✅ #235 |
| `mq` | 1 | 5=subscribe, 6=unsubscribe（仅 `/v1/frame/stream`） | ✅ #236 |
| `tsdb` | 2 | 列式批量写 | 预留 #237 |
| `sql` | 3 | 流式结果集 | 预留 #238 |
| `vector` | 4 | search/insert | 预留 #239 |
| `kv` | 5 | get/put/scan | 预留 #240 |
| `object` | 6 | get/put | 预留 #240 |
| `doc` | 7 | find/insert | 预留 #240 |

MQ 的 browse/stats 等管理面操作不进帧（走 REST 管理契约）。推送订阅（op 5/6）仅在双工流端点
`/v1/frame/stream` 上可用；一元端点 `/v1/frame` 只接受 op 1~4，收到 op 5/6 回 `unsupported_op`。

## MQ 帧体编码（service=1）

权限与 REST 完全对齐：publish / publish-batch / ack 需 `Write`，pull 需 `Read`。
topic 由服务端加 `db.` 前缀限定，限定名不出现在线上。

### op=1 publish

请求：

| 字段 | 类型 |
|------|------|
| db | varstr |
| topic | varstr |
| headerCount | varuint |
| headers[i] | varstr key + varstr value |
| payload | bytes |

响应：`offset` varuint64。

### op=2 publish-batch

请求：db varstr、topic varstr、count varuint（≥1），每条消息 = headerCount + headers + payload bytes。
响应：count varuint + offsets varuint64 × count（按输入顺序）。

### op=3 pull

请求：db varstr、topic varstr、consumerGroup varstr、maxCount varuint（`0` → 默认 100；服务端封顶 1000）。
响应：count varuint，每条消息：

| 字段 | 类型 |
|------|------|
| offset | varuint64 |
| timestampUtcTicks | i64（`DateTimeOffset.UtcTicks`） |
| headerCount + headers | varuint + (varstr, varstr)* |
| payload | bytes |

### op=4 ack

请求：db varstr、topic varstr、consumerGroup varstr、offset varuint64。
响应：`nextOffset` varuint64。

### op=5 subscribe（仅 `/v1/frame/stream`）

把消费从轮询升级为服务端推送。请求：

| 字段 | 类型 |
|------|------|
| db | varstr |
| topic | varstr |
| consumerGroup | varstr（`startMode=0` 必填，其余可空串） |
| startMode | u8 |
| startOffset | varuint64（仅 `startMode=1` 有效） |
| batchMax | varuint（`0` → 默认 100；服务端封顶 1000） |

`startMode`：`0`=消费组已提交位点、`1`=显式 `startOffset`、`2`=最早保留、`3`=末尾（仅新消息）。

响应帧（Flags=Response，同 `StreamId`）：`effectiveStartOffset` varuint64——服务端解析后的实际起点
（会被 retention 前移）。确认帧后，凡 offset ≥ 起点的消息到达即以**推送帧**投递：

- 推送帧 Flags=`Push`（bit2，**非** Response），op 回显 `5`，`StreamId` 为订阅时的 id；
- 帧体布局与 pull 响应**完全一致**（count + 每条 offset/timestampUtcTicks/headers/payload），故客户端可用
  同一解码器解析。

游标推进：消费组模式（`startMode=0`）下推送**不**推进组提交位点；客户端在同一条流上发 op=4 `ack` 帧显式确认，
断线重连从已提交位点续传（**至少一次**语义）。retention 裁掉订阅游标以下的消息时，游标静默前移到当前保留起点
（不重发已裁消息、也不报错）。单连接订阅数上限 64，超出回 `too_many_subscriptions`；重复 `StreamId` 回
`bad_request`。

### op=6 unsubscribe（仅 `/v1/frame/stream`）

请求：空体，按 `StreamId` 定位订阅。响应帧：空体（Flags=Response，回显 `StreamId`）确认。退订后该 `StreamId`
不再产生推送帧；`StreamId` 可复用于新订阅。

## 双工流端点 `/v1/frame/stream`（#236）

`POST /v1/frame/stream`，Content-Type 同 `application/x-sonnetdb-frame`，**仅 HTTP/2**（h2c 端点或 TLS ALPN；
HTTP/1.1 请求回 400）。请求体是长生命周期的帧流，响应体是长生命周期的帧流，二者在同一条 HTTP/2 流上双工。

- **控制帧**（op 1~4，publish/publish-batch/pull/ack）语义与一元端点一致，响应帧交错回写；
- **订阅帧**（op 5/6）注册/注销推送订阅，一条连接可多订阅按 `StreamId` 交错；
- **背压**：服务端用有界 `System.Threading.Channels`（Wait 模式）解耦推送生产者与响应写出者——慢客户端令
  HTTP/2 流控反压到 `PipeWriter.FlushAsync`，进而填满 channel、暂停各订阅 pump，不丢消息；
- **鉴权**：连接建立时鉴权一次；动态用户订阅推送每批复查 `Read` 权限（与 SSE 一致），撤销即以 `forbidden`
  错误帧终止该订阅、连接存活；
- **生命周期**：请求体 EOF 或客户端断开触发有序 teardown（取消所有 pump → 排空 channel → 完成响应）。
- Kestrel 默认 `MinRequestBodyDataRate`（240B/5s）会误杀空闲订阅流的请求体，服务端已在该端点清除该限速。

## 错误模型

**HTTP 状态码**只用于「根本不在说协议」且响应尚未开始：

| 状态码 | 场景 |
|--------|------|
| 415 | Content-Type 不是 `application/x-sonnetdb-frame` |
| 400 | 首帧即畸形（版本/保留位/超限长度）、空请求体、请求体只有残帧 |
| 401 | 缺失/无效 Bearer token（全局鉴权中间件） |

**错误帧**（HTTP 200，Flags=Response|Error，payload = varstr code + varstr message）用于成帧后的一切失败，
按 `StreamId` 关联；批内单帧失败不影响其余帧：

| code | 场景 |
|------|------|
| `bad_request` | 语义非法（非法 db/topic 名、缺 consumerGroup、引擎 ArgumentException、重复订阅 `StreamId`） |
| `db_not_found` | 数据库不存在 |
| `forbidden` | 权限不足（含订阅期间动态用户被撤销 `Read`） |
| `bad_frame` | 帧体结构畸形（截断 varint、长度越界、保留位、尾部残帧） |
| `too_many_subscriptions` | 单连接订阅数超过上限（仅流端点） |
| `unsupported_version` / `unsupported_service` / `unsupported_op` | 信封不支持 |
| `mq_io_error` / `mq_error` | 引擎 IOException / InvalidDataException |

> 请求帧的 Flags 必须为 0；`Response`/`Error`/`Push` 位由服务端设置，客户端设置任一位即 `bad_frame`。

与 REST 错误码同一词汇表，客户端两条传输统一处理。

## 限制与配额

| 项 | 值 |
|----|----|
| 单帧 payload 上限 | 132 MiB（先于分配校验） |
| `/v1/frame`、`/v1/frame/stream` 请求体大小 | 不限（已豁免 Kestrel 默认 30 MB 限制；单帧上限仍生效） |
| pull maxCount / subscribe batchMax | 服务端封顶 1000 |
| 单连接订阅数（流端点） | 64 |
| MQ 单条消息 payload | 128 MiB（引擎权威上限） |

## 实现位置

- 帧信封与 MQ codec（纯 BCL，零第三方）：`src/SonnetDB.Core/Protocol/`
  （`FrameHeader` / `FrameCodec` / `MqFrameCodec` / `FrameService` / `FrameFlags` / `MqFrameOp`）
- 服务端一元处理器：`src/SonnetDB/Endpoints/Handlers/FrameEndpointHandler.cs`（PipeReader 增量解析，
  内存上界 = 单帧，非全量缓冲）
- 服务端双工流处理器（#236）：`src/SonnetDB/Endpoints/Handlers/FrameStreamEndpointHandler.cs`
  （reader 循环复用同一 `TryReadFrame`，响应侧走 `System.Threading.Channels` + 单写者 pump）
- Core 推送唤醒原语：`SonnetMqStore.WaitForMessagesAsync`（per-topic pulse `TaskCompletionSource`）
- 编码基准：`tests/SonnetDB.Benchmarks/Benchmarks/FrameEncodingBenchmark.cs`
  （`dotnet run -c Release -- --filter *FrameEncoding*`）
