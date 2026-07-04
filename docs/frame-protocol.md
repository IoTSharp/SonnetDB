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
| `tsdb` | 2 | 列式批量写 | 预留 #237 |
| `sql` | 3 | 流式结果集 | 预留 #238 |
| `vector` | 4 | search/insert | 预留 #239 |
| `kv` | 5 | get/put/scan | 预留 #240 |
| `object` | 6 | get/put | 预留 #240 |
| `doc` | 7 | find/insert | 预留 #240 |

MQ 的 browse/stats 等管理面操作不进帧（走 REST 管理契约）；推送订阅归 #236。

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
| `bad_request` | 语义非法（非法 db/topic 名、缺 consumerGroup、引擎 ArgumentException） |
| `db_not_found` | 数据库不存在 |
| `forbidden` | 权限不足 |
| `bad_frame` | 帧体结构畸形（截断 varint、长度越界、保留位、尾部残帧） |
| `unsupported_version` / `unsupported_service` / `unsupported_op` | 信封不支持 |
| `mq_io_error` / `mq_error` | 引擎 IOException / InvalidDataException |

与 REST 错误码同一词汇表，客户端两条传输统一处理。

## 限制与配额

| 项 | 值 |
|----|----|
| 单帧 payload 上限 | 132 MiB（先于分配校验） |
| `/v1/frame` 请求体大小 | 不限（已豁免 Kestrel 默认 30 MB 限制；单帧上限仍生效） |
| pull maxCount | 服务端封顶 1000 |
| MQ 单条消息 payload | 128 MiB（引擎权威上限） |

## 实现位置

- 帧信封与 MQ codec（纯 BCL，零第三方）：`src/SonnetDB.Core/Protocol/`
  （`FrameHeader` / `FrameCodec` / `MqFrameCodec` / `FrameService` / `FrameFlags` / `MqFrameOp`）
- 服务端处理器：`src/SonnetDB/Endpoints/Handlers/FrameEndpointHandler.cs`（PipeReader 增量解析，
  内存上界 = 单帧，非全量缓冲）
- 编码基准：`tests/SonnetDB.Benchmarks/Benchmarks/FrameEncodingBenchmark.cs`
  （`dotnet run -c Release -- --filter *FrameEncoding*`）
