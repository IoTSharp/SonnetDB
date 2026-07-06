# 二进制帧协议（Frame Protocol）

> M28 P5b #235 引入的通用高吞吐接入通道。与既有 REST/JSON 端点**并列新增**，所有 REST 端点保持不变；
> 帧层只做传输编码，不改变任何引擎查询/写入语义。

## 设计动机

SonnetDB 全模型此前只有 HTTP+JSON 一条接入通道：MQ/对象 payload 走 Base64（+33% 体积 + 编解码 CPU）、
向量 `float[]` 走 JSON 数字文本、大结果集全量物化 JSON。帧协议消灭 JSON/Base64 税：
二进制 payload 原始字节直传，多帧一体、`stream-id` 关联，为 #236 推送订阅与 #238 流式结果集铺路。
自 #240 起七个 service（mq / tsdb / sql / vector / kv / object / doc）全部挂载，全模型二进制覆盖完成。

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
| `tsdb` | 2 | 1=write-columnar（列式批量写） | ✅ #237 |
| `sql` | 3 | 1=query（流式列式结果集） | ✅ #238 |
| `vector` | 4 | 1=search（KNN 检索，f32 二进制向量 + 流式列式结果集） | ✅ #239 |
| `kv` | 5 | 1=get, 2=put, 3=scan | ✅ #240 |
| `object` | 6 | 1=get（流式分块）, 2=put | ✅ #240 |
| `doc` | 7 | 1=find（ID/扫描）, 2=insert | ✅ #240 |

MQ 的 browse/stats 等管理面操作不进帧（走 REST 管理契约）。推送订阅（op 5/6）仅在双工流端点
`/v1/frame/stream` 上可用；一元端点 `/v1/frame` 只接受 op 1~4，收到 op 5/6 回 `unsupported_op`。
KV 的 ttl/incr/cas/remove、对象的 bucket 管理/版本/multipart/生命周期、文档的复杂查询（filter/
projection/sort/aggregate）/更新/删除均不进帧——高吞吐数据面走帧，管理面与复杂查询走 REST / SQL。

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

## tsdb 帧体编码（service=2，#237）

时序列式批量写：一个请求帧写一个 measurement 的一批点，帧体按**列式**布局
（对齐 IoTDB Tablet / PG COPY BINARY 的列式批思路），服务端流式列转行直通引擎
`WriteMany` 背压批量路径（复用 P0/P2 硬化成果），需 `Write` 权限。

### op=1 write-columnar

请求帧体：

| 字段 | 类型 |
|------|------|
| db | varstr |
| measurement | varstr |
| flushMode | u8（0=none / 1=async / 2=sync，对应 REST `?flush` 三档） |
| blockCount | varuint（≥1） |
| blocks[i] | 见下 |

每个 **block** = 一组 tag 取值下的行集（同一序列族），布局：

| 字段 | 类型 |
|------|------|
| tagCount | varuint（≤1024） |
| tags[i] | varstr key + varstr value |
| rowCount | varuint（≥1） |
| timestamps | i64 × rowCount（Unix epoch 毫秒，little-endian 定宽，可整段 `MemoryMarshal` 直传） |
| columnCount | varuint（≥1） |
| columns[i] | 见下 |

每个 **column** = 一个字段列：

| 字段 | 类型 |
|------|------|
| name | varstr |
| type | u8（1=Float64 / 2=Int64 / 3=Boolean / 4=String / 5=Vector / 6=GeoPoint，同引擎 `FieldType`） |
| sparse | u8（0=稠密：所有行都有值；1=稀疏：携带 presence 位图） |
| presence | `(rowCount+7)/8` 字节位图（仅 sparse=1；bit=1 表示该行有值，LSB-first） |
| dim | varuint（仅 type=Vector：向量维度 ≥1） |
| values | 紧凑值序列，仅 present 行按行序排列，见下 |

各类型 values 编码：

| type | 编码 |
|------|------|
| Float64 | f64 LE × present |
| Int64 | i64 LE × present |
| Boolean | u8（0/1）× present |
| String | (varstr) × present |
| Vector | f32 LE × dim × present（按行连续） |
| GeoPoint | (f64 lat + f64 lon) × present |

响应帧体：`written` varuint——成功写入的点数。

语义与防御：

- 名称（db / measurement / tag / 字段名）≤ 512 字节且不含保留字符（`,=\n\r\t"`），
  按块整体校验一次（块内行共享），行内零重复校验；
- 时间戳为负或某行所有列均缺值 → 该行 `bulk_ingest_error`（FailFast，整帧拒绝）；
- schema 冲突（字段类型与既有 measurement schema 不符）→ 引擎权威校验拒绝，回 `bulk_ingest_error` 错误帧；
- 块数 / 行数 / 列数 / 值长度先于分配按帧体剩余长度校验，畸形帧回 `bad_frame`；
- 写入路径与 REST `lp`/`json`/`bulk` 三端点完全一致（同一 `BulkIngestor` → `WriteMany`），
  仅传输编码不同——REST 端点全部保留。

## sql 帧体编码（service=3，#238）

SQL 只读查询的流式列式结果集：一个请求帧发起一条查询，响应为**同 `StreamId` 的帧序列**
meta → rows × N → end，服务端逐块编码逐块 flush——响应缓冲内存上界 = 单块（默认 256 KiB /
4096 行封顶），大结果集经 HTTP/2 流增量到达客户端，消灭全量 JSON 物化与数字文本税。
需 `Read` 权限。

**语句门禁**：帧通道只承载数据面只读语句（`SELECT` / `SHOW` / `DESCRIBE` / `EXPLAIN`）。
写语句（INSERT / UPDATE / DELETE / DDL）回 `bad_request`（写入走 REST SQL 端点或 tsdb
列式写帧）；控制面 SQL（CREATE USER / GRANT / SHOW DATABASES 等）回 `bad_request`
（走 REST `/v1/sql`）。事务（BEGIN/COMMIT）不进帧。

### op=1 query 请求

| 字段 | 类型 |
|------|------|
| db | varstr |
| sql | varstr（≤ 1 MiB，不可为空） |
| paramCount | varuint（≤ 256） |
| params[i] | varstr 参数名 + u8 类型标记 + 值 |

命名参数绑定 `@name` / `:name` 占位符（同 REST 参数化查询 #213），值类型标记：
`0`=null（无值字节）、`1`=int64（i64 LE）、`2`=float64（f64 LE）、`3`=bool（u8）、`4`=string（varstr）。

### 响应帧序列

每帧 Flags=`Response`、`StreamId` 回显，帧体首字节为**块类型**：

| chunkKind | 名称 | 布局 |
|-----------|------|------|
| 1 | meta | columnCount varuint（≤4096）+ 列名 varstr × columnCount |
| 2 | rows | rowCount varuint（1~65536）+ columnCount varuint + 列 × columnCount（见下） |
| 3 | end | rowCount varuint64（总行数）+ elapsedMs f64 LE |

**rows 帧按列存储**，每列 = u8 列类型标记 + 值序列：

- 列类型 `0`（全 null 列）：无后续字节；
- 列类型 `1`~`8`（单一类型列）：u8 hasNulls（0/1）+ 可选 null 位图
  （`(rowCount+7)/8` 字节，bit=1 表示该行有值，LSB-first）+ 紧凑值序列（仅有值行按行序）；
- 列类型 `255`（variant 混合列）：每行 u8 值标记 + 值（标记 `0`=null 无值字节）。

值类型标记与编码（列级与 variant 值级同一词汇表）：

| 标记 | 类型 | 编码 |
|------|------|------|
| 1 | Int64 | i64 LE |
| 2 | Float64 | f64 LE |
| 3 | Boolean | u8（0/1） |
| 4 | String | varstr |
| 5 | Bytes | varuint 长度 + 原始字节（零 Base64） |
| 6 | Timestamp | i64 LE（UTC ticks） |
| 7 | Vector | varuint 维度 + f32 LE × 维度 |
| 8 | GeoPoint | f64 lat + f64 lon |

类型按块内实际值推断：整型族归一 Int64、浮点族归一 Float64，**整型与浮点混列不合并**
（走 variant，避免大 long → double 精度损失，对齐 #219 Q15 语义）；`Guid` 与未识别类型按
String（ToString）回退——与 REST NDJSON 的 `NdjsonRowWriter` 覆盖面对齐（NDJSON 的 byte[]
走 Base64，帧走原始字节）。

语义与防御：

- 执行语义与 REST `/v1/db/{db}/sql` 完全一致（同一 `SqlExecutor`，含 #217 残差谓词、
  #219 DISTINCT/值删除、#220 流式合并等全部 SQL 能力）——仅结果集传输编码不同；
- 查询执行失败时若 meta/rows 帧已写出，错误帧以同 `StreamId` 追加：客户端在收到 end 帧前
  收到错误帧即终止该查询并丢弃已收行；
- 解码防御：sql ≤ 1 MiB、参数 ≤ 256、列 ≤ 4096、单 rows 帧 ≤ 65536 行且行×列 ≤ 2^24
  单元格（先于分配校验）；
- 慢查询事件与 `sonnetdb_sql_*` 指标与 REST 端点同源上报。

## vector 帧体编码（service=4，#239）

measurement 向量列的 KNN 检索：查询向量以 **f32 二进制**（little-endian，`MemoryMarshal`
整段直传）承载，消灭 JSON 数字文本编解码税（每个 float 4 字节 vs 文本约 9~12 字节）；
检索语义与 SQL `knn(measurement, column, query_vector, k[, metric]) [WHERE ...]` TVF
完全一致（服务端复用同一检索内核——列/维度校验、tag 过滤定位候选序列、单次读快照 KNN、
tag/field 回填），需 `Read` 权限。

向量**插入**不设独立 opcode：tsdb `write-columnar` 帧（#237）的 Vector 列已是
f32 二进制直传通道，向量批量写走 tsdb service。

### op=1 search 请求

| 字段 | 类型 |
|------|------|
| db | varstr |
| measurement | varstr（非空） |
| column | varstr（非空，须为 VECTOR 类型 FIELD 列） |
| k | varuint（≥1） |
| metric | u8（0=cosine / 1=l2 / 2=inner_product，同 SQL knn 的 metric 词汇） |
| tagCount | varuint（≤1024） |
| tags[i] | varstr key + varstr value（tag 等值过滤，key 须为 TAG 列，重复 key 拒绝） |
| timeFrom | i64 LE（闭区间起点，Unix 毫秒；全时间轴 = long.MinValue） |
| timeTo | i64 LE（闭区间终点；全时间轴 = long.MaxValue） |
| dim | varuint（≥1，须与列声明维度一致） |
| queryVector | f32 LE × dim |

### search 响应帧序列

响应为**同 `StreamId` 的帧序列** meta → rows × N → end，**块布局与 sql service 完全一致**
（见上节 chunkKind / 列式编码 / 值类型标记词汇表），仅帧头 service/op 为 vector/search——
客户端用同一套 sql 块解码器解析。结果集列固定为
`(time, distance, ...tag_columns, ...field_columns)`，按距离升序、行数 ≤ k；
`distance` 为 Float64，向量字段列以值类型标记 `7`（Vector，varuint 维度 + f32 LE）回传
——REST NDJSON 路径的向量列会降级 `ToString()`，帧通道是向量列语义正确回传的推荐通道。

语义与防御：

- 度量语义同 SQL knn：cosine=1−余弦相似度、l2=欧氏距离、inner_product=−内积，均越小越相似；
- 维度不匹配 / 非 VECTOR 列 / tag 过滤引用非 TAG 列 / measurement 不存在 → `vector_search_error`
  错误帧（引擎权威校验，同 REST `vector_search_error` 词汇）；
- 解码防御：名字 ≤ 512 字节、tag 过滤 ≤ 1024 条、维度声明先于分配按帧体剩余长度校验、
  时间窗起点 ≤ 终点、尾部多余字节拒绝；
- 大 k 结果集自动分块（同 sql 的 256 KiB / 4096 行封顶）逐块 flush。

## kv 帧体编码（service=5，#240）

内置 KV keyspace 的 get / put / scan。key / value 以**原始字节直传**（零 Base64）。
权限与 REST KV 端点对齐：get / scan 需 `Read`，put 需 `Write`；keyspace 名合法性同 REST。
ttl / incr / decr / cas / remove / expire / persist / stats 等操作不进帧（走 REST KV 端点）。

### op=1 get

请求：db varstr、keyspace varstr、key bytes。
响应：`found` u8；`found=1` 时附 `version` varuint64 + 过期时间 + `value` bytes。

过期时间编码：`hasExpiry` u8（0=永不过期，无后续字节；1=附 i64 LE `UtcTicks`）。

### op=2 put

请求：db varstr、keyspace varstr、key bytes、过期时间、value bytes。
响应：`version` varuint64——本次写入的单调版本号。

### op=3 scan

请求：db varstr、keyspace varstr、prefix bytes（空 = 全部）、afterKey bytes（空 = 从前缀起点）、
limit varuint（`0` → 服务端默认；封顶 10000）。
响应：count varuint，每条 (key bytes, version varuint64, 过期时间, value bytes)，按 key 字节序升序。

解码防御：名字（db/keyspace）≤ 512 字节、key/prefix/afterKey ≤ 64 KiB（对齐 `KvOptions.MaxKeyBytes`），
value 长度先于分配按帧体剩余长度校验，尾部多余字节拒绝。

## object 帧体编码（service=6，#240）

对象存储的 get / put。内容以**原始字节直传**（零 Base64）；get 需 `Read`、put 需 `Write`，
与 REST S3 兼容端点同一引擎入口与错误码词汇。bucket 管理 / 版本列表 / multipart / 生命周期 / tagging
不进帧（走 REST S3 兼容端点）。

### op=1 get

请求：db varstr、bucket varstr、key varstr、versionId varstr（空串 = 最新版本）。

响应为**同 `StreamId` 的帧序列** meta → data × N → end，服务端逐块 flush——响应缓冲内存上界 = 单块
（默认 256 KiB），大 blob 经 HTTP/2 流增量到达客户端。每帧 Flags=`Response`、`StreamId` 回显，
帧体首字节为**块类型**：

| chunkKind | 名称 | 布局 |
|-----------|------|------|
| 1 | meta | versionId varstr + contentType varstr + sizeBytes varuint64 + etag varstr + sha256 varstr + metadata map + tags map |
| 2 | data | 一段原始内容字节（chunkKind 字节之后到帧尾，帧长定界，无内嵌长度前缀） |
| 3 | end | totalBytes varuint64（内容总字节数，供客户端校验完整性） |

map 编码：count varuint + (key varstr, value varstr) × count。

### op=2 put

请求：db varstr、bucket varstr、key varstr、contentType varstr（空串 = 服务端默认）、
metadata map、tags map、content bytes。内容 ≤ 单帧 payload 上限 132 MiB；更大对象走 REST multipart 上传。
响应：versionId varstr + sizeBytes varuint64 + etag varstr + sha256 varstr。

解码防御：名字 ≤ 512 字节、对象 key ≤ 4096 字节、metadata/tags 各 ≤ 64 条且各 ≤ 16 KiB、
content 长度先于分配校验、尾部多余字节拒绝。对象存储引擎异常（bucket_not_found / object_not_found 等）
以其自带错误码回错误帧。

## doc 帧体编码（service=7，#240）

文档集合的 find / insert。JSON 文本以**原始 UTF-8 字节直传**（零 JSON 信封转义、零嵌套序列化）。
find 只承载 ID 点查 / ID 列表 / 扫描分页（高吞吐数据面）；filter / projection / sort / aggregate 等
复杂查询与 update / delete 不进帧（走 REST 文档端点或 SQL）。集合须已存在（同 REST mustExist 语义），
find 需 `Read`、insert 需 `Write`。

### op=1 find

请求：db varstr、collection varstr、idCount varuint + ids varstr × idCount（空 = 扫描分页）、
afterId varstr（仅扫描：空串 = 从头）、limit varuint（`0` → 默认 100；封顶 4096）。
ids 非空时按 ID 列表读取（afterId / limit 忽略）；ids 为空时按文档 ID 顺序扫描分页。
响应：count varuint，每条 (id varstr, version varuint64, JSON 原始 UTF-8 varstr)。

### op=2 insert

请求：db varstr、collection varstr、ordered u8（0/1）、count varuint（1~4096）+
每条 (id varstr, JSON 原始 UTF-8 varstr)。
响应：inserted / matched / modified / deleted varuint + errorCount varuint +
每条错误 (index varuint, id varstr, code varstr, message varstr, severity varstr)。
`ordered=1` 时任一错误阻止整批提交；`ordered=0` 时跳过失败项提交其余。

解码防御：名字 ≤ 512 字节、单帧文档 ≤ 4096 条、单条文档 JSON ≤ 16 MiB、id 非空、尾部多余字节拒绝。

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
| `bad_request` | 语义非法（非法 db/topic/keyspace 名、缺 consumerGroup、引擎 ArgumentException、重复订阅 `StreamId`、sql 帧携带写语句/控制面语句） |
| `db_not_found` | 数据库不存在 |
| `forbidden` | 权限不足（含订阅期间动态用户被撤销 `Read`） |
| `bad_frame` | 帧体结构畸形（截断 varint、长度越界、保留位、尾部残帧） |
| `bulk_ingest_error` | tsdb 列式写行级/schema 错误（负时间戳、整行无字段、字段类型与 schema 冲突） |
| `sql_error` | SQL 解析/执行失败（与 REST SQL 端点同一错误码；若 meta/rows 已写出则以同 `StreamId` 追加） |
| `vector_search_error` | vector 检索执行失败（维度不匹配、非 VECTOR 列、非 TAG 过滤列、measurement 不存在；与 REST `vector_search_error` 同码；若 meta/rows 已写出则以同 `StreamId` 追加） |
| `collection_not_found` | doc find/insert 引用的文档集合不存在（与 REST 同码） |
| `bucket_not_found` / `object_not_found` / `object_content_missing` | object get/put 的对象存储引擎错误（与 REST S3 端点同一错误码词汇） |
| `object_storage_io_error` | object get/put 底层 I/O 失败 |
| `too_many_subscriptions` | 单连接订阅数超过上限（仅流端点） |
| `unsupported_version` / `unsupported_service` / `unsupported_op` | 信封不支持 |
| `mq_io_error` / `mq_error` | 引擎 IOException / InvalidDataException |

> 请求帧的 Flags 必须为 0；`Response`/`Error`/`Push` 位由服务端设置，客户端设置任一位即 `bad_frame`。

与 REST 错误码同一词汇表，客户端两条传输统一处理。

## 客户端 SDK（`SonnetDB.Data`，#241）

`SonnetDB.Data` 的远程客户端在**远程模式**下按连接串 `Protocol` 选项与运行时探测决定走帧还是 REST；
嵌入式模式不受影响。数据面操作优先走帧、其余操作回落 REST/JSON。

**连接串 `Protocol` 选项**（仅远程模式生效）：

| 值 | 含义 |
|----|------|
| `auto`（默认） | 运行时惰性探测服务端是否支持帧；支持则对数据面操作走帧，不支持或传输级失败则回落 REST |
| `frame-http2` | 强制走帧；帧端点传输级失败直接抛 `frame_transport_error`，不静默回落（但帧不支持的 op 仍走 REST） |
| `rest` | 强制走 REST/JSON（#241 之前的行为） |

**探测语义**（`Remote/FrameChannel`）：`auto` 首次尝试 `POST /v1/frame`——

- 传输级失败（HTTP 非 2xx 如 415/404、连接异常、200 体解析不出合法帧）视为「服务端不懂帧」，
  缓存回落 REST，后续不再尝试帧端点。
- 200 + 可解析帧（哪怕是带内错误帧）视为「服务端懂帧」，缓存走帧；带内错误帧转 `SndbServerException`。
- 安全性：一元 POST 传输级失败意味着服务端处理前拒绝或从未收到请求，回落 REST 不会重复应用写入；
  200 带内错误帧意味着操作已执行且应用级失败，直接上抛、绝不重试。

**各客户端帧化范围**（其余操作恒走 REST）：

| 客户端 | 走帧 | 恒走 REST |
|--------|------|-----------|
| `SndbMqClient` | publish / publish-batch / pull / ack | stats |
| `SndbKvClient` | get / set / scan（命名空间限定 key 以字节发送、scan 返回剥前缀） | incr/decr/cas/getMany/setMany/remove/removePrefix/expire/persist/ttl/cleanExpired/stats |
| `SndbDocumentClient` | insertOne / insertMany / findOne / 单页非高级 find（id/ids/扫描，hasMore 即回落） | 创建/删除集合、validator、update/delete、count/distinct/aggregate、高级/分页 find |
| ADO SQL（`SndbConnection` 远程） | 只读语句（SELECT / SHOW-数据面 / DESCRIBE / EXPLAIN）走 sql service | 写/控制面/事务/bulk/schema/元命令 |
| 向量（`SonnetDBVectorStore`） | 经 ADO SQL `SELECT ... FROM vector_search(...)` 传递性走 sql service | — |

**⚠ ADO SQL 类型差异（以帧为准，记录在案）**：ADO SQL 帧路径与 REST NDJSON 路径对部分列返回不同 CLR 类型。
帧路径返回更正确/更富的类型，REST NDJSON 路径返回字符串：

| 列类型 | 帧路径 | REST NDJSON 路径 |
|--------|--------|------------------|
| 时间戳 | `DateTime`（UTC） | ISO 字符串 |
| blob | `byte[]` | base64 字符串 |
| 整数值 double（如 `3.0`） | `double`（不收敛） | `long` |
| 向量 | `float[]`（语义正确） | `"System.Single[]"`（`ToString()`） |
| `long` / `string` / `GeoPoint` | 一致 | 一致 |

`Protocol=auto` 下 ADO 走帧后上述列类型会变，消费 `SndbDataReader.GetValue`/`GetFieldType` 的既有代码需知悉。
MQ / KV / 文档三者两条传输字节一致，无此差异。需保持旧类型行为可显式设 `Protocol=rest`。

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
- tsdb 列式 codec 与列转行 reader（#237）：`src/SonnetDB.Core/Protocol/TsdbFrameCodec.cs`
  （`TsdbColumnarBlock` / `TsdbColumnarColumn` 编码模型）、`TsdbColumnarPointReader.cs`
  （`IPointReader` 实现，流式列转行直通 `BulkIngestor`）
- sql 流式结果集 codec（#238）：`src/SonnetDB.Core/Protocol/SqlFrameCodec.cs`
  （query 请求 + meta/rows/end 块编解码、块内列类型推断、`SelectChunkRowCount` 切块）、
  `SqlFrameOp.cs`；服务端分发在 `FrameEndpointHandler.ExecuteSqlQueryAsync`（逐块 flush）
- vector KNN 检索 codec（#239）：`src/SonnetDB.Core/Protocol/VectorFrameCodec.cs`
  （search 请求 f32 二进制编解码；响应帧复用 sql 块编码内核，仅换帧头 service/op）、
  `VectorFrameOp.cs`；服务端分发在 `FrameEndpointHandler.ExecuteVectorSearchAsync`，
  检索内核与 SQL knn TVF 共用 `TableValuedFunctionExecutor.ExecuteKnnSearch`
- kv / object / doc codec（#240）：`src/SonnetDB.Core/Protocol/KvFrameCodec.cs`
  （get/put/scan 原始字节直传）、`ObjectFrameCodec.cs`（get 流式 meta/data/end 分块 + put）、
  `DocFrameCodec.cs`（find/insert 原始 JSON 直传）、`KvFrameOp.cs` / `ObjectFrameOp.cs` / `DocFrameOp.cs`；
  服务端分发在 `FrameEndpointHandler`（kv/doc 同步 `ExecuteKvOp`/`ExecuteDocOp`、object 流式
  `ExecuteObjectOpAsync`），资源级鉴权复用 `SonnetDbEndpoints.EvaluateNamedResourceAccess`
  （kv keyspace / doc collection 名校验，与 REST 同语义）与 `EvaluateDatabaseAccess`（object）
- 服务端一元处理器：`src/SonnetDB/Endpoints/Handlers/FrameEndpointHandler.cs`（PipeReader 增量解析，
  内存上界 = 单帧，非全量缓冲；mq + tsdb + sql + vector 四 service 分发）
- 服务端双工流处理器（#236）：`src/SonnetDB/Endpoints/Handlers/FrameStreamEndpointHandler.cs`
  （reader 循环复用同一 `TryReadFrame`，响应侧走 `System.Threading.Channels` + 单写者 pump）
- Core 推送唤醒原语：`SonnetMqStore.WaitForMessagesAsync`（per-topic pulse `TaskCompletionSource`）
- 编码基准：`tests/SonnetDB.Benchmarks/Benchmarks/FrameEncodingBenchmark.cs`
  （`dotnet run -c Release -- --filter *FrameEncoding*`）、
  `ColumnarIngestBenchmark.cs`（#237 列式帧 vs JSON vs Line Protocol，`--filter *ColumnarIngest*`）、
  `VectorSearchEncodingBenchmark.cs`（#239 f32 二进制向量 vs JSON 数字数组，`--filter *VectorSearchEncoding*`）
