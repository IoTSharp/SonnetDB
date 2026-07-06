# Changelog

本项目所有重要变更将记录在此文件中。
格式遵循 [Keep a Changelog 1.1.0](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [SemVer 2.0.0](https://semver.org/lang/zh-CN/)。

## [Unreleased]

### Added

- **M28 P5b #241 客户端 SDK 帧协议贯通**：`SonnetDB.Data`（ADO.NET 提供程序 + MQ / KV / 文档客户端）远程模式接入二进制帧协议——数据面操作优先经 `POST /v1/frame` 走帧、其余操作回落 REST/JSON，修复 N2（客户端仅走 REST，服务端帧能力无客户端出口）。新增连接串 `Protocol` 选项（`SndbTransportProtocol` 枚举）：`auto`（默认，运行时惰性探测）/ `frame-http2`（强制帧）/ `rest`（强制 REST，即 #241 之前行为），仅远程模式生效。新增共享传输助手 `Remote/FrameChannel`（每客户端一个实例，共享底层 `HttpClient`）：三态探测缓存（Unknown/Frames/Rest），首次尝试帧端点——传输级失败（HTTP 非 2xx 如 415/404、连接异常、200 体解析不出合法帧）视为「服务端不懂帧」缓存回落 REST 且返回 null，200 + 可解析帧（哪怕带内错误帧）视为「懂帧」缓存走帧并交由 `ThrowIfError` 把错误帧转 `SndbServerException`；`frame-http2` 强制模式下传输级失败直接抛 `frame_transport_error` 不静默回落。**回落安全性**：一元 POST 传输级失败意味着服务端处理前拒绝或从未收到，回落 REST 不会重复应用写入；200 带内错误帧意味着已执行且应用级失败，直接上抛绝不重试。各客户端帧化范围：
  - **MQ**（`SndbMqClient`）：`publish` / `publish-batch` / `pull` / `ack` 走帧；`stats` 无帧 op 恒走 REST。
  - **KV**（`SndbKvClient`）：`get` / `set(put)` / `scan` 走帧，命名空间限定 key（`ns:key`）以字节发送、scan 返回 key 剥前缀（复用既有 `Qualify`/`Unqualify`，与 REST 语义一致）；`incr`/`decr`/`cas`/`getMany`/`setMany`/`remove`/`removePrefix`/`expire`/`persist`/`ttl`/`cleanExpired`/`stats` 恒走 REST。
  - **文档**（`SndbDocumentClient`）：`insertOne` / `insertMany` / `findOne` 走帧，`find`/`findPage` 仅「非高级查询 + 无 continuation token + 无 skip」的单页 id/ids/扫描子情形走帧（多取一条判 hasMore，一旦溢出即回落 REST——帧 find 响应不携带 continuation token / snapshotVersion，避免篡改服务端分页契约）；创建/删除集合、validator、update/delete、count/distinct/aggregate、高级/分页 find 恒走 REST。
  - **向量**：SDK 向量存储（`SonnetDBVectorCollection`）无独立远程客户端，经 `SndbConnection` 发 `SELECT ... FROM vector_search(...)`，故只读 ADO SQL 走帧后向量检索**传递性**走 sql service（不引入独立 `VectorFrameCodec` 客户端）。
  - **ADO SQL**（`RemoteConnectionImpl`）：只读语句（SELECT / SHOW-数据面 / DESCRIBE / EXPLAIN，客户端以 `SqlParser.Parse` + 公开 AST 类型自行分类，与服务端 sql 帧门禁一致；解析失败/写/控制面/ShowDatabases → 回落 REST）走 sql service 流式帧（meta → rows × N → end 物化为 `MaterializedExecutionResult`）；写/控制面/事务/bulk/`SnapshotTables`/元命令（USE、current_database）恒走 REST。**⚠ 记录在案的行为差异**：ADO SQL 帧路径与 REST NDJSON 路径对部分列返回不同 CLR 类型——帧路径返回更正确/更富的类型（时间戳 `DateTime`、blob `byte[]`、整数值 `double` 不收敛为 `long`、向量 `float[]`），REST NDJSON 路径返回字符串（时间戳 ISO 字符串、blob base64、向量 `ToString()`）。**#241 以帧类型为准**：`auto` 下 ADO 走帧后这些列类型会变，消费 `SndbDataReader.GetValue`/`GetFieldType` 的既有代码需知悉。MQ / KV / 文档三者两条传输字节一致，无此差异。
  测试：`SonnetDB.Core.Tests` 新增 `FrameChannelTests`（5 项，stub `HttpMessageHandler` 驱动：auto 传输失败缓存回落且后续不再尝试、连接异常回落、rest 零帧 POST、frame-http2 传输失败抛错不回落、带内错误帧转 `SndbServerException`）；`SonnetDB.Tests` 新增 `FrameTransportParityTests`（12 项，真实 Kestrel + `Protocol=frame-http2` vs `rest` 各跑一遍断言等价：MQ 发布/批发布/拉取/ack + stats、KV 带命名空间 set/get/scan + 不支持 op 回落、文档 insert/findOne/findByIds + 高级 find 回落、ADO SELECT 列名行值一致 + 写语句回落 + DATETIME 列固化帧富类型 vs REST 字符串差异）。所有既有 REST 路径与嵌入式路径保留，帧层不改任何返回语义（ADO SQL 类型差异除外，已记录）。

- **M28 P5b #240 KV / 对象 / 文档接入帧协议**：帧协议挂载最后三个 service——`kv`（service=5）、`object`（service=6）、`doc`（service=7），至此七个 service（mq/tsdb/sql/vector/kv/object/doc）全部就位，全模型二进制覆盖完成，修复 N8（KV value / 对象 blob / 文档 JSON 经 JSON+Base64、无原始字节路径）。`SonnetDB.Core` 新增三个 codec（纯 BCL）+ `KvFrameOp`/`ObjectFrameOp`/`DocFrameOp`：
  - **`KvFrameCodec`**（get=1/put=2/scan=3）：key/value 原始字节直传零 Base64；get 响应 `found` u8 + version varuint64 + 过期时间（u8 标记 + 可选 i64 UtcTicks）+ value bytes；put 请求 key+过期时间+value、响应 version；scan 请求 prefix+afterKey+limit（复用 `KvKeyspace.ScanPrefixAfter` 分页）、响应 count + 每条 (key,version,过期时间,value) 按 key 序升序。解码防御：名字 ≤512B、key/prefix/afterKey ≤64KiB（对齐 `KvOptions.MaxKeyBytes`）、value 长度先于分配校验、尾部残留拒绝。
  - **`ObjectFrameCodec`**（get=1/put=2）：内容原始字节直传；get 响应为**同 streamId 的 meta → data × N → end 帧序列**（复用 #238 流式分块思路，默认 256 KiB/块，服务端逐块 flush、响应缓冲上界 = 单块，大 blob 增量到达客户端）——meta 携 versionId/contentType/sizeBytes/etag/sha256/metadata/tags，data 帧 chunkKind 字节后即原始内容（帧长定界），end 携 totalBytes；put 请求 contentType+metadata+tags+content（≤132MiB，更大走 REST multipart）、响应 versionId/sizeBytes/etag/sha256。
  - **`DocFrameCodec`**（find=1/insert=2）：JSON 文本原始 UTF-8 直传零信封转义；find 请求 ids（空=扫描）+afterId+limit、响应 count + 每条 (id,version,JSON)；insert 请求 ordered u8 + count + 每条 (id,JSON)、响应 inserted/matched/modified/deleted + 每条错误 (index,id,code,message,severity)。解码防御：单帧 ≤4096 文档、单条 JSON ≤16MiB、id 非空、尾部残留拒绝。
  Server `FrameEndpointHandler` 信封校验挂载三 service 的 opcode 分派（`unsupported_service` 消息更新为七 service 全列）：kv/doc 走同步 `ExecuteKvOp`/`ExecuteDocOp`（与 REST KV/文档端点同一引擎入口 `KvKeyspace`/`DocumentCollectionStore`），object 走流式 `ExecuteObjectOpAsync`（get 边读边推 data 帧，put 用零拷贝 `ReadOnlyMemoryStream` 包装帧体视图直喂 `SndbObjectStore.PutObjectAsync`）。资源级鉴权抽新的 `SonnetDbEndpoints.EvaluateNamedResourceAccess`（db 名→db 存在→keyspace/collection 名合法→权限，与 REST `TryResolveKvAsync`/`TryResolveDocumentCollectionAsync` 判定顺序一致，资源名非法复用 `BadTopic`）供 kv/doc 共用、object 复用 `EvaluateDatabaseAccess`；get/scan/find 需 `Read`，put/insert 需 `Write`。集合须已存在（同 REST mustExist，缺失回 `collection_not_found`）；对象存储引擎异常以自带错误码（`bucket_not_found`/`object_not_found` 等）回错误帧。**scope 边界**：KV 的 ttl/incr/cas/remove、对象 bucket 管理/版本/multipart/生命周期、文档复杂查询（filter/projection/sort/aggregate）/update/delete 不进帧——高吞吐数据面走帧，管理面与复杂查询走 REST/SQL。测试：Core codec 41 项（`KvFrameCodecTests` 16 + `ObjectFrameCodecTests` 12 + `DocFrameCodecTests` 13，覆盖各 op 往返、原始高位字节、过期时间/空值/多分块、编码/解码防御含长度炸弹先于分配、非法标记、尾部残留）+ Server 端到端 16 项（`KvObjectDocFrameEndpointTests`：put→get 原始字节往返、帧写→REST 读等价、scan 前缀、object 600KB 多分块流式 + REST 取回等价、doc insert→find + REST find-one 等价、扫描分页、duplicate_key 错误落结果、db/bucket/collection not-found 错误帧、只读 token forbidden、kv+doc+错误帧混合批隔离）。既有 `UnsupportedService_AfterValidFrame_ErrorFrame` 测试改用未定义 service 8（kv 已挂载）。`docs/frame-protocol.md` 补 kv/object/doc 三章节、Service/Op 矩阵、错误码（`collection_not_found`/`bucket_not_found`/`object_not_found`/`object_content_missing`/`object_storage_io_error`）与实现位置。所有既有 REST KV/S3/文档端点保留，帧层不改任何引擎语义。

- **M28 P5b #239 向量检索接入帧协议**：帧协议挂载第四个 service——`vector`（service=4）的 `search`（op=1）opcode，measurement 向量列 KNN 检索的查询向量以 **f32 二进制**（little-endian，`MemoryMarshal` 整段直传）承载，修复 N7（向量 `float[]` 经 JSON 数字文本编解码，比 Base64 更浪费）。`SonnetDB.Core` 新增 `SonnetDB.Protocol.VectorFrameCodec`（纯 BCL）：search 请求帧 = db + measurement + column + k(varuint) + metric(u8，cosine/l2/inner_product 同 SQL knn 词汇) + tag 等值过滤（≤1024 条，重复 key 拒绝）+ 闭区间时间窗（i64×2）+ 查询向量（varuint 维度 + f32×dim，维度声明先于分配校验）；响应为同 streamId 的 **meta → rows × N → end 帧序列，块布局与 sql service（#238）完全一致**——`SqlFrameCodec` 的三个响应帧编码抽出 service/op 参数化内核（`EncodeMetaFrameCore`/`EncodeRowsFrameCore`/`EncodeEndFrameCore`）供 vector 帧复用，客户端用同一套 sql 块解码器解析，KNN 结果集自动享受 #238 的切块与逐块 flush（响应缓冲上界 = 单块）；结果列固定 `(time, distance, ...tags, ...fields)` 距离升序，向量字段列以 `SqlValueKind.Vector`（f32）回传——REST NDJSON 路径向量列会降级 `ToString()`，帧通道是向量列语义正确回传的推荐通道。**检索语义与 SQL knn TVF 完全一致**：`TableValuedFunctionExecutor.ExecuteKnn` 的编排（列/维度校验 → tag 过滤定位候选序列 → 单次读快照 `KnnExecutor` → tag/field 批量回填）抽为共用内核 `ExecuteKnnSearch`，SQL 路径与帧路径同一入口，帧路径额外静态校验 tag 过滤键必须是 TAG 列。Server `FrameEndpointHandler.ExecuteVectorSearchAsync`：`EvaluateDatabaseAccess` 要求 `Read` 权限；引擎校验失败（维度不匹配/非 VECTOR 列/非 TAG 过滤列/measurement 不存在）回 `vector_search_error` 错误帧（与 REST 同码；meta/rows 已写出则同 streamId 追加）。**向量插入不设独立 opcode**——tsdb write-columnar 帧（#237）的 Vector 列已是 f32 二进制直传通道，避免重复写入路径。基准 `VectorSearchEncodingBenchmark`（`--filter *VectorSearchEncoding*`，dim=128/768/1536 × top-100 结果集）：bytes-on-wire 请求帧 vs JSON 数字数组 **2.6~2.8×**（dim=1536：6.2KB vs 17.2KB）、结果集 **2.8×**（617KB vs 1.72MB）；CPU dim=1536 请求编码 **91ns vs 102µs（~1100× 快、零分配）**、解码 346ns vs 85µs（~250×）；top-100 结果集编码 **20µs vs 11.9ms（~590× 快，分配 264B vs 1.7MB 零 LOH）**、解码 42µs vs 10.1ms（~240×）。测试：Core codec 15 项（最小/全选项/L2 往返、解码持有型、编码防御（空向量/k=0）、解码防御（非法 metric/k=0/零维度/维度炸弹先于分配/时间窗倒置/重复 tag key/尾部残留/空 measurement）、响应帧 sql 解码器互通）+ Server 端到端 11 项（cosine top-k 排序与列形状、**与 sql service knn TVF 帧结果逐行等价**、L2 度量、tag 过滤、时间窗、维度不匹配/非向量列/非 TAG 过滤列/库不存在错误帧、只读 token 放行、vector+畸形+sql 混合批隔离）。`docs/frame-protocol.md` 补 vector service 章节与 `vector_search_error` 错误码；既有向量 REST 端点（管理面 search-preview 等）全保留，帧层不改 KNN 引擎语义。

- **M28 P5b #238 SQL 流式列式结果集接入帧协议**：帧协议挂载第三个 service——`sql`（service=3）的 `query`（op=1）opcode，SQL 只读查询的大结果集以**流式列式二进制分块**回传（meta 帧 → rows 帧 × N → end 帧，同 streamId 关联；服务端逐块编码逐块 flush，响应缓冲内存上界 = 单块，默认 256 KiB / 4096 行封顶），修复 N6（SQL 结果集全量物化 JSON 回传、大结果集序列化瓶颈无流式）。`SonnetDB.Core` 新增 `SonnetDB.Protocol.SqlFrameCodec`（纯 BCL）：请求帧 = db + sql（≤1 MiB）+ 命名标量参数（null/i64/f64/bool/string，经 `SqlParameterBinder` 绑定 `@name`/`:name`，复用 #213 参数化机制）；rows 帧**按列存储 + 块内类型推断**——单一类型列走稠密定宽/紧凑编码（u8 类型标记 + 可选 null 位图 LSB-first + 仅有值行的紧凑值序列），混合类型列回退 variant（逐值带标记），值类型覆盖 Int64/Float64/Boolean/String/Bytes（原始字节零 Base64）/Timestamp（UTC ticks）/Vector（f32×dim）/GeoPoint，整型族归一 Int64、浮点族归一 Float64 但**整型与浮点混列不合并**（保 #219 Q15 的大 long 精度语义）；`SelectChunkRowCount` 按行字节估算切块；解码防御（sql/参数/列数/单帧行数/行×列单元格数全部先于分配校验）。Server `FrameEndpointHandler` 新增 `ExecuteSqlQueryAsync`：走 `EvaluateDatabaseAccess` 要求 `Read` 权限，**语句门禁**只放行数据面只读语句（SELECT/SHOW/DESCRIBE/EXPLAIN——`RequiresWritePermission`/`IsControlPlaneStatement` 与 REST 同一判定，写语句与控制面 SQL 回 `bad_request` 引导走 REST/tsdb 写帧），执行语义与 REST `/v1/db/{db}/sql` 完全一致（同一 `SqlExecutor`，含 #220 流式合并在内的全部 SQL 能力）；执行失败若 meta/rows 已写出则以同 streamId 追加 `sql_error` 错误帧（客户端 end 前收到错误帧即终止）；`RecordSqlRequest`/`AddReturnedRows` 指标与慢查询事件与 REST 端点同源。基准 `SqlResultEncodingBenchmark`（`--filter *SqlResultEncoding*`，time/host/value/cnt 四列 × 10k/100k 行）：bytes-on-wire 帧 3.40MB vs NDJSON 3.97MB（1.17×）；100k 行编码帧 9.7ms vs NDJSON 26.2ms（**2.7× 快**），分配 **2.2KB vs 24MB**（>10000× 差、编码零 GC 压力零 LOH）；解码帧 7.4ms vs NDJSON 逐行 JsonDocument 21.3ms（2.9× 快）。测试：Core codec 20 项（请求参数全类型往返/meta/rows 稠密+null 位图跨字节+全 null 列+variant 大 long 精度+扩展类型（Bytes/Timestamp/Vector/GeoPoint）/int 族归一/子范围编码/end/块切分/解码防御含单元格数炸弹、截断、越界列名、空 sql、尾部残留）+ Server 端到端 11 项（时序查询与 REST NDJSON 逐行等价、8192 行多 rows 帧流式、关系表 NULL 位图、空结果集 meta+end、命名参数绑定、只读 token SELECT 放行、写语句/控制面语句 `bad_request`、解析错误 `sql_error`、库不存在、MQ+SQL+错误帧混合批隔离）。`docs/frame-protocol.md` 补 sql service 章节与 `sql_error` 错误码。所有既有 REST SQL 端点保留，帧层不改任何 SQL 执行语义。

- **M28 P5b #237 时序列式批量写接入帧协议**：帧协议挂载第二个 service——`tsdb`（service=2）的 `write-columnar`（op=1）opcode，measurement 批量写以**列式紧凑二进制**直传（对齐 IoTDB Tablet / PG COPY BINARY 的列式批思路，非行式 JSON），修复 N5（时序批量写走 HTTP+JSON 行式）。`SonnetDB.Core` 新增 `SonnetDB.Protocol.TsdbFrameCodec`（纯 BCL）：帧体 = db + measurement + flushMode(u8，对应 REST `?flush` 三档) + 块序列；每块 = 一组 tag 取值（同一序列族）+ 时间戳列（i64 LE 定宽 × rowCount，编码侧整段 `MemoryMarshal.AsBytes` 直写）+ 若干字段列（类型 u8 + 稀疏标志 + 可选 presence 位图 + 紧凑值序列——Float64/Int64 列同样 `MemoryMarshal` 整段直传，全部六种 `FieldType` 支持含 Vector f32×dim 与 GeoPoint lat/lon）；`TsdbColumnarBlock`/`TsdbColumnarColumn` 编码模型（静态工厂按类型构造零装箱）。解码侧 `TsdbColumnarPointReader` 实现 `IPointReader` **流式列转行**：逐块解码列头后按行装配 `Point` 直通 `BulkIngestor` → `WriteMany`（复用 P0/P2 已硬化的分块背压批量路径，与 REST `lp`/`json`/`bulk` 三端点完全同一引擎入口）；名称/时间戳防御按块整体校验一次（块内行共享 tag 与字段名，绕开 `Point.Create` 的逐行重复校验），行数/列数/值长度先于分配按帧体剩余长度校验。Server `FrameEndpointHandler` 信封校验按 service 分派（`unsupported_service` 消息更新），tsdb 帧走 `EvaluateDatabaseAccess`（新抽的数据库级判定核心，语义与 REST `TryResolveDatabase`+权限检查一致）要求 `Write` 权限，行级/schema 错误映射 `bulk_ingest_error` 错误帧（与 REST bulk 端点同码），写入计数进 `ServerMetrics.AddInsertedRows`。基准 `ColumnarIngestBenchmark`（`--filter *ColumnarIngest*`，2 字段 × 10k/100k 行）：**bytes-on-wire 帧 240KB vs JSON 897KB（3.73×）vs LP 676KB（2.82×）**；100k 行编码帧 91µs vs JSON 29.8ms（**326× 快**）/ LP 28.3ms，且分配 368B vs 46MB（编码零 LOH）；解析→Point 流帧 11.9ms vs JSON 53.1ms（**4.5× 快**）/ LP 49.0ms，分配 40.8MB vs 97.6MB/101.5MB。测试：Core codec 13 项（稠密/全类型/稀疏 presence/多块/整行缺值行级异常/编码防御/解码防御含 flushMode 越界、行数超限、保留字符注入、负时间戳、尾部残留）+ Server 端到端 7 项（列式写→SQL 回查、稀疏+多块聚合 count 对齐、与 JSON bulk 端点数据等价、只读 token forbidden、库不存在、MQ+tsdb+畸形帧混合批隔离、schema 类型冲突 `bulk_ingest_error`）。`docs/frame-protocol.md` 补 tsdb service 章节与错误码。所有既有 REST 批量端点保留，帧层不改引擎写入语义。

- **M28 P5b #236 HTTP/2 流式推送订阅（MQ）**：MQ 消费从轮询升级为 HTTP/2 长生命周期双工流上的服务端推送——新消息到达即经二进制帧投递，修复 N3（消费纯轮询无推送）。`SonnetDB.Core`：`SonnetMqStore` 新增 `WaitForMessagesAsync(topic, fromOffset, ct)` 异步等待原语（per-topic 惰性 `TaskCompletionSource(RunContinuationsAsynchronously)` pulse，无订阅者时热路径仅一次 volatile null 检查、零分配；发布在刷盘后、`SyncRoot` 外 pulse；等待者在 `SyncRoot` 内「查条件 + 取 pulse」与发布/Dispose 串行化，杜绝丢唤醒；有效起点 `max(fromOffset, TrimmedBeforeOffset)` 穿越 retention gap 不空转；`Dispose` 令等待者抛 `ObjectDisposedException`）。`SonnetDB.Protocol`：`MqFrameOp` += `Subscribe=5`/`Unsubscribe=6`，`FrameFlags` += `Push=4`（bit2，仅服务端→客户端，独立于 Response），`MqFrameCodec` 新增 subscribe/unsubscribe 编解码 + `EncodePushFrame`（op=5、flags=Push，帧体布局与 pull 响应一致，客户端复用同一解码器）；`EncodePullResponse` 抽出参数化 `EncodeMessagesFrame` 核心供推送复用。Server 新增双工端点 `POST /v1/frame/stream`（仅 HTTP/2，h2c 或 TLS ALPN；HTTP/1.1 回 400）：reader 循环复用 #235 的 `TryReadFrame`（控制帧 op 1~4 语义与一元端点一致、响应交错回写），订阅帧注册 per-connection `Dictionary<streamId, Subscription>`（上限 64/连接、重复 streamId 拒绝），每订阅一个 pump task 经 `WaitForMessagesAsync`→`Pull(offset)`→推送帧；单写者 task 独占 `PipeWriter`，用有界 `System.Threading.Channels`（Wait 模式）解耦推送生产与写出，HTTP/2 流控经 `FlushAsync` 反压到 channel 天然背压不丢消息；清除 Kestrel 默认 `MinRequestBodyDataRate` 免空闲订阅流请求体被误杀；动态用户逐批复查 `Read` 权限（撤销即以 `forbidden` 帧终止该订阅、连接存活，与 SSE 一致）；请求体 EOF/断开触发有序 teardown（取消 pump → 排空 channel → 完成响应，无死锁）。消费组模式推送不推进组位点，客户端在同一条流上发 ack 帧显式确认、断线重连从已提交位点续传（至少一次）；startMode 支持组位点/显式 offset/最早/末尾。客户端 SDK 帧贯通归 #241，本项不动 `SndbMqClient`。测试：Core 5 项（数据已存在立即返回/publish 唤醒（内联刷盘+组提交两配置）/trim gap 前移/取消/Dispose 故障）+ codec 6 项（subscribe 四模式往返/非法 startMode/push 帧解码/unsubscribe 空体）+ Server 7 项双工 h2c 端到端（自写 `PushStreamContent` 保持请求体打开：订阅→REST 发布→收推送/一连接多订阅交错/流上 ack+重连续传/退订停推/订阅错误隔离/非 HTTP/2 拒绝/流上 publish 与推送交错）。`docs/frame-protocol.md` 补 op 5/6、Push flag、流端点章节。所有既有 REST/JSON 与一元 `/v1/frame` 端点保留，帧层不改引擎语义。

- **M28 P5b #235 通用二进制帧协议 + MQ service + 编码基准**：新增覆盖全模型的高吞吐二进制接入通道，消灭 JSON/Base64 税。`SonnetDB.Core` 新增 `SonnetDB.Protocol` 命名空间（纯 BCL 零第三方）：固定 12 字节 LE 帧头（u32 payloadLen ≤132 MiB 先于分配校验 | u8 version=1 | u8 service（1=mq..7=doc 七个编号全部现在保留，#237~#240 逐个挂载）| u8 op | u8 flags（bit0=Response/bit1=Error，保留位 MBZ）| u32 streamId 响应回显），基元 varuint(LEB128)/varstr/bytes（`SpanWriter`/`SpanReader` 新增 `WriteVarString`/`ReadVarString` + `Measure*`）；`FrameCodec.TryReadFrame` 基于 `ReadOnlySequence` 增量解析（#236 长流复用同一循环），`MqFrameCodec` 落首个 service 的 publish/publish-batch/pull/ack 四 opcode（payload 原始字节零 Base64、解码零拷贝视图直通 `SonnetMqStore.Publish(ReadOnlySpan)`、解码期防御上限名字 ≤512B/header 数 ≤1024/headers ≤64KiB）。Server 新增 `POST /v1/frame`（Content-Type `application/x-sonnetdb-frame`，请求体 1..N 帧逐帧处理逐帧流式回帧，`PipeReader` 增量解析内存上界=单帧，已豁免 30MB 请求体限制）；错误模型「未成帧走 HTTP 400/415/401，成帧后一切按帧回错误帧（HTTP 200，code 复用 REST 词汇 + bad_frame/unsupported_*），批内单帧失败不影响其余帧」；鉴权复用既有 Bearer + 三角色（`TryResolveMqAsync` 判定核心抽为 `EvaluateMqAccess` 供 REST/帧共用，REST 行为零变化）。**新增 h2c 监听口 5081**（`Kestrel:Endpoints:FrameH2`，`Protocols: Http2` 先验知识 h2c；明文端口无法同口协商 h1/h2 故单独开口），`/v1/frame` 同时在 5080 走 HTTP/1.1 可达，docker-compose 已补端口映射。编码基准 `FrameEncodingBenchmark`（`--filter *FrameEncoding*`）对比帧 vs JSON+Base64（体积/CPU/分配）：publish 16KiB 帧 16 459B vs JSON 21 921B（1.33× 体积税），pull 100 条 64B 消息帧 11.4KB vs JSON 24.3KB（2.13×，小消息元数据占比高）；CPU 上 publish 16KiB 编码帧约 5× 快（386ns vs 2 021ns，分配 160B vs 22KB）、解码约 60× 快（177ns vs 10 545ns），pull(100)×16KiB 编码约 12× 快（129µs vs 1 563µs，分配 16.8KB vs 2.2MB 且零 LOH）、解码约 2.8× 快。协议规格文档 `docs/frame-protocol.md`（帧头字节表/service-op 矩阵/MQ payload 编码/错误模型/限制/前向兼容规则）。所有既有 REST/JSON 端点保留，帧层不改任何引擎语义。

- **M17 #91 Prometheus 端点 + Web 内嵌监控面板**：`Observability:Prometheus:Enabled=true`（默认关闭）时 `/metrics` 由 `OpenTelemetry.Exporter.Prometheus.AspNetCore` 拉取端点接管，暴露 `sonnetdb_*`（#89 全部 Core 指标含 histogram bucket、per-db gauge）+ ASP.NET Core/Kestrel/HttpClient 指标与 `target_info` resource 元数据；关闭时保留原最小指标集文本端点（既有 scrape 配置零破坏）。Web Admin 新增「监控」侧边栏（`/admin/app/monitoring`，登录即见）：`web/src/api/metrics.ts` 纯前端解析 prom exposition 文本（样本/label/直方图聚合 + `histogram_quantile` 同款 bucket 线性插值差分还原），5s 轮询绘制写入吞吐（counter 差分）、查询 P95、WAL fsync P95、MemTable 占用四条内联 SVG 折线（零图表第三方依赖，与 SqlResultChart 同风格）+ 六个统计卡 + 按数据库 MemTable/Segment 表；Copilot 调用/token 卡片在 #92 指标落地后自动出现（缺失即隐藏）；未启用 Prometheus 端点时面板显示启用指引。测试覆盖：`PrometheusEndpointTests` 断言 OTel 接管后 `sonnetdb_write_points` 可抓取且旧文本渲染器指标消失。

- **M17 #90 Server OpenTelemetry 引导**：`src/SonnetDB`（Server 程序集）引入 `OpenTelemetry.Extensions.Hosting` 1.16.0 + AspNetCore/Http instrumentation + OTLP/Console exporter（**仅 Server；Core 保持零第三方**，只订阅 #89 的 BCL Meter/ActivitySource）。`Program.ConfigureOpenTelemetry`：`WithMetrics(AddMeter("SonnetDB.Core","SonnetDB.Server") + AddAspNetCoreInstrumentation + AddHttpClientInstrumentation)`、`WithTracing(AddSource("SonnetDB.Core","SonnetDB.Copilot") + AddAspNetCoreInstrumentation + AddHttpClientInstrumentation)`；Resource 自动携带 `service.name=sonnetdb`、`service.version`（程序集 InformationalVersion）、`service.instance.id`（机器名:PID）、`host.name`。OTLP 导出仅在设置了标准 `OTEL_EXPORTER_OTLP_ENDPOINT` 环境变量时挂接（默认不导出、指标/追踪管线仍常开供 Prometheus/诊断消费）；Console trace exporter 仅 `Development` 环境启用。新增 `ServerOptions.Observability`（`Observability:Prometheus:Enabled` 开关先行落位，端点在 #91 接管）。测试经 `OpenTelemetry.Exporter.InMemory` 断言 `sonnetdb.write.points`/`.duration` 真实流入 OTel SDK。

- **M17 #89 Core 端 Meter / ActivitySource 基线**：`SonnetDB.Core` 新增 `SonnetDB.Diagnostics` 命名空间——`SonnetDbMeter`（`Meter("SonnetDB.Core","1.0.0")`，纯 BCL `System.Diagnostics.Metrics`，零第三方依赖）与 `SonnetDbActivitySource`（`ActivitySource("SonnetDB.Core")`，span 携带 `db.system=sonnetdb`/`db.operation`/`sonnetdb.segment.id`）。插桩覆盖：写入路径（`sonnetdb.write.points` 计数 + `sonnetdb.write.duration` 端到端时延，`Write`/`WriteMany` chunk 双入口）、WAL fsync（`sonnetdb.wal.fsync.duration`，单点计量于 `WalSegmentSet.SyncActiveWriterLocked`，覆盖每写 fsync / 组提交 / flush checkpoint / Delete 强制同步全部路径）、Flush 泵（`sonnetdb.flush.duration{outcome}` / `.points` / `.bytes` + `sonnetdb.flush` span）、Compaction（`sonnetdb.compaction.duration{outcome}` + span）、Segment 物理读（`sonnetdb.segment.block.reads` / `.read.bytes`，解码缓存命中不计）、查询（`sonnetdb.query.duration{db.operation=points|aggregate}` 覆盖整个流式枚举周期含提前 break + `sonnetdb.query.points` span）。per-db 可见性由 4 个 ObservableGauge 提供（`sonnetdb.memtable.bytes` / `.points`、`sonnetdb.segments.count`、`sonnetdb.flush.pending`，tag `sonnetdb.database`，弱引用注册表随 `Tsdb.Open` / `Dispose` 自动登记/注销）。热路径全部经 `Instrument.Enabled` 短路——无监听者时连时间戳都不取，写入语义不变。`dotnet-counters monitor --counters SonnetDB.Core` 开箱可见。

- **M28 P5a #233 SonnetMQ 批量发布入口**：新增服务端 `POST /v1/db/{db}/mq/{topic}/publish-batch`（`MqPublishBatchRequest`/`MqPublishBatchResponse`，每条可带独立 headers，按序分配连续 offset）与客户端 `SndbMqClient.PublishManyAsync(topic, IReadOnlyList<SndbMqPublishEntry>)`（嵌入式直调 `PublishMany`、远程走 batch 端点）。此前 REST 只有单条 `Publish` 入口，N 条消息 = N 次刷盘系统调用；批量入口让一批消息复用 `PublishMany` 的「批末仅刷盘一次」路径（MQ4 的「HTTP 端点只调单条 Publish」半）。DTO 加在 `Contracts/Dtos.cs`「MQ」段，源生成注册在 `Json/ServerJsonContext.cs` 与客户端 `Remote/RemoteJsonContext.cs`；所有现有单条端点保留。

- **M29 A #245 多模型只读管理契约**：Server 层新增 `ManagementContractEndpoints`，为此前无管理端点或端点过薄的模型补齐最小只读 metadata + browse 契约，供 M29 后续 per-model 工作台（#251~#256）消费——KV `POST /v1/db/{db}/kv/keyspaces`（keyspace 枚举）与 `POST /v1/db/{db}/kv/{keyspace}/scan`（前缀 + base64 游标分页，复用 `ScanPrefixAfter`）；向量 `POST /v1/db/{db}/vector/indexes`（遍历 measurement 列回显 kind/维度/度量/HNSW m·ef 等图参数）与 `POST /v1/db/{db}/vector/search-preview`（复用既有 `knn(...)` data-plane，measurement/column 标识符校验 + 向量字面量拼接防注入）；全文 `POST /v1/db/{db}/fulltext/indexes`（fields/分词器/doc 数）、`POST /v1/db/{db}/fulltext/search-preview`（BM25 + exact/fuzzy）与 `POST /v1/db/{db}/fulltext/analyze`（unicode/cjk/jieba 分词预览）；MQ `POST /v1/db/{db}/mq/topics`（topic 枚举）、`POST /v1/db/{db}/mq/{topic}/offsets`（高水位 + 各消费者组已提交 offset 与 lag）与 `POST /v1/db/{db}/mq/{topic}/browse`（按 offset 只读浏览，不改消费者组状态）。全部走既有 Bearer + 三角色鉴权、`DatabasePermission.Read`；写操作复用既有 data-plane，不新增任何查询/写入/索引/存储语义。对象模型 list/metadata 已由既有 S3 端点覆盖，本 PR 不重复。`SonnetDB.Core` 仅为 MQ topic 枚举新增只读 `SonnetMqStore.ListTopicStats()`（不改任何队列语义）。
- **M28 P5a #230 SonnetMQ 吞吐/延迟基准基线**：`tests/SonnetDB.Benchmarks` 新增 `MqThroughputBenchmark`（BenchmarkDotNet `RunStrategy.Monitoring`，覆盖单/多 topic、`Publish` 单条 vs `PublishMany` 批量、pull+ack 回环、64B/1KB/16KB payload）与 `MqLatencyBenchmark`（独立 runner，`dotnet run -c Release -- --mq-latency`，采样 publish 尾延迟输出 P50/P90/P99/P99.9/max，对比 no-flush / os-flush / fsync-durable 三档持久性）。为 P5a #231~#234（去全局锁、零拷贝写、组提交、冷数据下沉）建立对照基线；基线数字已确认 `SyncOnPublish=true` 每写 fsync 相对默认 os-flush 有约两个数量级的 P50 延迟惩罚（MQ0）。
- **AI-ready 门面文档与 Industrial Data Agent 路线（PR #182）**：新增 `llms.txt` 与 `docs/industrial-ai-applications.md`，明确 SonnetDB 优先定位为“面向 .NET 工业边缘应用的本地优先数据引擎”，并把 Industrial Data Agent、MCP 工具契约、工业异常分析 Demo、provider-neutral、本地模型、写入审批二阶段和 IoTSharp 联合样例纳入新增 Milestone 27 规划。
- **连接器路线独立化 + C ABI 远程连接底座**：新增 Milestone 26 与 `connectors/README.md` 路线说明，明确 C ABI 当前继续 SQL-only，后续按 bulk / KV / Document / Object / MQ 分组扩展；`SonnetDB.Native` 改为引用 `SonnetDB.Data`，`sonnetdb_open` 可接受完整连接字符串或旧式本地目录，为 C/Go/Rust/Java/Python 等连接器同时支持嵌入式与远程 SQL 连接打底。
- **C ABI bulk ingest 分组（PR #176）**：`connectors/c` 新增 `sonnetdb_bulk_create` / `sonnetdb_bulk_execute` / `sonnetdb_bulk_free` 与 `measurement`、`onerror`、`flush` 配置函数，覆盖 Line Protocol、JSON points 与 Bulk VALUES payload；Native 层通过 `SonnetDB.Data` `CommandType.TableDirect` 统一走嵌入式和远程 bulk 路径，C quickstart 与连接器文档同步演示独立 bulk handle 用法。
- **C ABI KV 分组（PR #177）**：`connectors/c` 新增 `sonnetdb_kv_open` / `sonnetdb_kv_close` 以及 get/set/delete、scan prefix、ttl/expire/persist、incr、cas、entry/scan value-copy 函数组；Native 层复用 `SonnetDB.Data.Kv.SndbKvClient` 统一嵌入式与远程 KV 路径，并让嵌入式 KV 客户端复用共享 `Tsdb` 注册表，避免同进程 SQL connection 与 KV handle 重复打开同一目录。Go/Rust/Python/Java 连接器同步增加 idiomatic KV wrapper，quickstart 与连接器文档演示 KV handle、二进制 value 拷贝和 scan cursor 用法。
- **C ABI Document 分组（PR #178）**：`connectors/c` 新增 `sonnetdb_doc_open` / `sonnetdb_doc_close`、collection create/drop、insert/update/delete、find page、aggregate 与 `sonnetdb_doc_result_*` JSON 结果拷贝函数；Native 层复用 `SonnetDB.Data.Documents.SndbDocumentClient`，请求和响应保持 UTF-8 JSON 边界，不暴露内部 document 类型，并让嵌入式 Document 客户端复用共享 `Tsdb` 注册表，避免 SQL connection 与 document handle 重复打开同一目录。
- **C ABI Object Storage 分组（PR #179）**：`connectors/c` 新增 `sonnetdb_obj_open` / `sonnetdb_obj_close`、bucket create/list/delete、object put/head/get range/list/delete/delete-many、multipart initiate/upload-part/complete/abort 与 `sonnetdb_obj_result_*` JSON 结果拷贝函数；对象内容通过 `sonnetdb_obj_writer_*` / `sonnetdb_obj_reader_*` chunk handle 分块跨 ABI，避免大对象一次性内存复制。Native 层复用 `SonnetDB.Data.ObjectStorage.SndbObjectStorageClient`，嵌入式对象客户端改为共享 `Tsdb` 注册表，C quickstart 和连接器文档同步演示对象桶与 multipart 用法。
- **C ABI MQ 分组（PR #180）**：`connectors/c` 新增 `sonnetdb_mq_open` / `sonnetdb_mq_close`、topic publish、consumer group pull、ack、stats 以及 pull cursor payload/header 拷贝函数；Native 层复用 `SonnetDB.Data.Mq.SndbMqClient` 统一嵌入式与远程 MQ 路径，明确 offset 按 topic 单调递增、ack 返回 consumer group 下一条待消费 offset，C quickstart 和连接器文档同步演示 MQ handle 用法。
- **上层语言连接器同步包装（PR #181）**：Go / Rust / Java / Python 连接器同步补齐 bulk ingest 与 Document collection idiomatic wrapper，复用既有 KV wrapper；Java JNI 与 JDK 21 FFM 后端同时绑定 `sonnetdb_bulk_*` / `sonnetdb_doc_*`，四种语言 quickstart 与 README 同步演示 SQL、bulk、KV、Document 组合用法。
- **Data parity：embedded/remote ADO.NET schema 矩阵**：`SndbConnection.GetSchema("Tables"|"Columns"|"Indexes")` 在远程模式下改为通过 `/v1/db/{db}/schema` 读取真实关系表 schema，不再因缺少本地 `UnderlyingTsdb` 返回空集合；新增 embedded/remote 双模式矩阵测试覆盖表、列、唯一索引元数据一致性。
- **EF remote hardening**：`SonnetDbDatabaseCreator` 对远程连接明确实现 `Create` / `Delete` / `Exists` 语义，`Create` / `Delete` 走 `/v1/db` 控制面，`Exists` 通过远程 schema 探测真实数据库存在性；新增真实 Kestrel 远程 EF 生命周期测试。
- **缓存双模式测试**：新增 SonnetDB.Caching embedded/remote E2E 覆盖 EasyCaching provider、`IDistributedCache`、TTL 过期、janitor `CleanExpired` 与 prefix remove，固定缓存扩展在本地目录与远程 HTTP KV API 下的语义一致性。
- **PR #146 磁盘有序 KV / 文档容量底座**：KV state/segment 文件升级到 v3 并支持按 key/offset 打开磁盘有序段，冷启动只加载 key metadata 与 WAL overlay，不再把 compact 后的 document 主数据和 JSON path 索引 value 全量常驻内存；`ScanPrefix` / `ScanPrefixAfter` 通过磁盘段与 WAL overlay 有序合并，`Compact()` 会生成单个 SSTable-like 段并截断 WAL，删除 tombstone 可阻止崩溃恢复后旧磁盘 key 复活。Document backup checkpoint 改为对集合主数据执行 compact，backup/restore 覆盖 `.SDBKVSEG` 有序段恢复与索引一致性。
- **PR #145 文档校验执行能力**：Document Store 新增 collection validator 执行层与 schema 持久化（`documents.docschema` v4，兼容读取 v1~v3），支持 required/type/range/enum/pattern 校验与 validation action `error` / `warn`；`Insert`、`InsertMany`、整体替换、局部 update/upsert 统一在提交前执行 validator，error 拒绝写入并返回稳定 `validation_failed`，warn 允许写入并返回 `severity=warning`。SQL 新增 `ALTER DOCUMENT COLLECTION ... SET/DROP VALIDATOR`，HTTP 新增 `/documents/{collection}/validator`，`SndbDocumentClient` 新增 `SetValidatorAsync` / `DropValidatorAsync`，OpenAPI 与测试同步覆盖。
- **PR #144 单文档原子性与批量写轻事务**：Document Store 写路径新增 `DocumentWriteResult` / 稳定错误码与批量预检提交边界，`InsertMany`、整体替换型 `UpdateMany`、`DeleteMany` 支持 ordered/unordered 语义；ordered 批次在 duplicate key、validation failed、write conflict、document too large 时整体拒绝提交，unordered 批次提交有效项并返回 per-item `errors`。HTTP Document API 与 `SndbDocumentClient` 同步暴露 `ordered` 参数、错误数组与 207 partial success 响应，单文档 insert/update/delete 继续通过同一 mutation 路径同步维护 JSON path index 与 fulltext index。
- **PR #143 Aggregation Pipeline 子集**：Document Store 新增 `DocumentCollectionStore.Aggregate`、`SndbDocumentClient.AggregateAsync` 与 `/v1/db/{db}/documents/{collection}/aggregate`，支持 SonnetDB-native JSON 管线 `$match`、`$project`、`$group`、`$sort`、`$limit`、`$skip`、`$unwind`、`$count`、`$distinct`；`$group` 覆盖 count/sum/avg/min/max/first/last/distinct 聚合函数。SQL 文档集合查询同步支持 `GROUP BY json_value(...)` 与 count/sum/avg/min/max/first/last 聚合、HAVING 和 ORDER BY 结果列，复用现有 `json_value(document, '$.path')` 表达式求值。
- **PR #142 Document Query Planner 与代价模型**：Document Store 共享 planner 新增结构化 `DocumentQueryPlan` / 候选访问路径代价模型，按 `_id`、单字段索引、复合索引左前缀、partial index 与 full scan 估算候选行并选择成本最低路径；复合索引支持左前缀等值扫描，`EXPLAIN` 追加 `estimated_candidate_rows`、`estimated_output_rows`、`filter_pushdown(_fields)`、`residual_filter_fields`、`sort_uses_index`、`projection_covered_by_index`、`candidate_plans` 与 `gap_reason`，index intersection 第一版明确返回 `gap_reason=index_intersection_not_supported`。
- **PR #141 文档索引体系升级**：Document Store 二级索引从单 JSON path 扩展为单字段 / 复合索引，新增 `CREATE [UNIQUE] [SPARSE] [TTL] INDEX ... ON collection ('$.a', '$.b') [WHERE json_value(...)=...]`，支持 unique、sparse、partial、TTL schema 持久化、在线 rebuild 与写入/更新增量维护；`SHOW JSON INDEXES` 展示 paths/flags/partial/TTL，`EXPLAIN` 对文档索引统一返回 `access_path=document_index`，旧 `CREATE JSON INDEX` 继续作为单字段兼容语法。
- **PR #140 Document 局部更新操作符**：Document Store 新增共享 `DocumentUpdate` 执行层，支持 `$set`、`$unset`、`$inc`、`$min`、`$max`、`$rename`、`$push`、`$pull`、`$addToSet`、`$currentDate`、upsert 与 multi update；`SndbDocumentClient` 和 `/documents/{collection}/update-one|update-many` 统一支持 operator 请求，更新后复用主数据 mutation 路径同步维护 JSON path index 与 fulltext index，`hybrid_search` 基于更新后的 document/fulltext/vector 字段读取结果，并补齐冲突路径校验测试。
- **PR #139 Document cursor 分页与批量读取**：Document find 新增 `continuationToken` / `hasMore` / `batchSize` / `snapshotVersion` / `cursorExpiresAtUtc` 响应字段，服务器强制最大 batch size 1000；`SndbDocumentClient.FindPageAsync` 统一支持嵌入式与远程分页读取，token 绑定 collection、查询形状、只读快照版本和 15 分钟过期时间，普通 scan 走底层 key 续扫，高级 filter/projection/sort/id 查询走稳定偏移续页；Web Admin 新增 `api/documents.ts` 统一消费入口，OpenAPI、README 与 SQL 参考同步更新。
- **PR #137 Document API 契约与客户端第一版**：`SonnetDB.Data` 新增 `SndbDocumentClient`，统一支持嵌入式与远程 SonnetDB 文档集合访问，覆盖 `CreateCollection`、`DropCollection`、`InsertOne/Many`、`Find`、`FindOne`、`UpdateOne/Many`（整文档替换）、`DeleteOne/Many`、`Count`、`Distinct`。服务端新增私有 JSON API `/v1/db/{db}/documents/{collection}/...`，复用现有数据库权限模型；保留 SQL 路径不变，并把首版 find 高级语义、cursor、局部更新操作符与批量写事务边界交由 #138 ~ #144 分步补齐。
- **PR #138 Find 查询语义补齐**：`SonnetDB.Core` 新增共享 `DocumentQueryPlanner` 与 document filter AST，SQL `SELECT` 与 `SndbDocumentClient` / `/documents/{collection}/find` 复用同一套 `_id`、嵌套 JSON path、`eq/ne/gt/gte/lt/lte/in/nin/exists/contains`、`and/or/not`、数组 contains、null/missing 区分、projection、sort、limit/skip 与稳定排序语义；OpenAPI、README 与 SQL 参考同步更新，cursor 分页、局部更新操作符和索引体系升级仍留给 #139 ~ #142。
- **Jieba 默认中文词库与外部词库 API**：`SonnetDB.Core` 内置中文词库从最小示例词表升级为基于 `cppjieba` `dict/jieba.dict.utf8` 转换的中等规模词库，并记录 upstream commit 与 MIT third-party notice；新增 `ChineseDictionary.FromTextFiles(...)`、`ChineseDictionary.FromCompiledFile(...)` 和 `ChineseDictionaryCompiler.Compile(...)`，支持加载多个 Jieba/cppjieba 风格词库并编译为紧凑 `.dat` 缓存。`docs/fulltext-dictionaries.md` 说明词库格式、授权来源、Core/Server 分发边界和词库变更后需要重建全文索引。
- **搜索与向量引擎合并路线**：新增 `docs/search-vector-engine-consolidation-roadmap.md`，明确 DotSearch / DotVector 进入合并维护期。后续 BM25、分词、距离计算、HNSW / IVF / Vamana、量化和索引序列化逐步收编到 `SonnetDB.Core`，`Microsoft.Extensions.VectorData` adapter 迁移到 `SonnetDB.Data`；不再继续扩张独立 DotSearch / DotVector 产品线。
- **DotSearch 合并 Phase 1 完成**：DotSearch core、Unicode / CJK / Jieba 分词器源码已物理并入 `src/SonnetDB.Core/FullText`；`SonnetDB.Core.csproj` 不再引用 `modules/DotSearch`，并负责生成和内嵌 Jieba `dict.txt` / `dict.dat`（保留旧 logical name）；DotSearch core/tokenizer 测试迁入 `tests/SonnetDB.Core.Tests/FullText`。
- **DotVector 合并 Phase 2 源码第一刀**：DotVector primitives、SIMD 距离计算、量化、HNSW / IVF / IVF-PQ / Vamana indexing 和本地 index blob 编解码已并入 `src/SonnetDB.Core/Vector`；`SonnetDB.Core.csproj` 移除对 `modules/DotVector` 的 `ProjectReference` 并补充 `System.Numerics.Tensors` 包引用；关键向量算法测试迁入 `tests/SonnetDB.Core.Tests/Vector`。
- **DotVector 合并 Phase 2 命名空间清理**：`src/SonnetDB.Core/Vector`、`Query.VectorDistance`、Segment 向量索引 adapter 和对应向量测试已从 `DotVector.*` 缓冲命名空间收敛到 `SonnetDB.Vector.*` / `SonnetDB.Core.Tests.Vector.*`；注释和错误消息改为 SonnetDB 内置向量引擎表述，DotVector 独立 collection/query/filter/persistence 相关语义仍留 Phase 3 VectorData 或 Phase 4 归档处理。
- **VectorData 迁移 Phase 3**：`SonnetDB.Data` 新增 `SonnetDB.Data.VectorData` adapter、DI 扩展和 `Microsoft.Extensions.VectorData.Abstractions` 依赖；默认把 VectorData collection 映射为 SonnetDB `DOCUMENT COLLECTION`，使用 document `id` + JSON document 保存记录和 embedding，不把通用 VectorData collection 映射到时序 `measurement`。`SonnetDB.Core` 新增 `vector_search(...)` document collection 纯向量 TVF、EXPLAIN 支持和测试覆盖，`PackageReadme` / SQL 参考同步补充用法。
- **搜索与向量合并 Phase 4 完成**：移除 `modules/DotSearch` / `modules/DotVector` 子模块登记，CI / CodeQL / Publish / Docker / connectors release workflow 不再递归 checkout 旧模块；Dockerfile 删除旧模块复制步骤，release script 和发布文档不再生成或列出独立 DotSearch / DotVector NuGet 包。干净 checkout 可直接构建 SonnetDB 内置全文与向量引擎。
- **搜索与向量合并 Phase 5 完成**：`src/SonnetDB.Core/FullText` 内部命名空间、Jieba 资源 logical name、测试命名空间和当前文档叙事从 `DotSearch.*` 收敛到 `SonnetDB.FullText.*` / SonnetDB 内置全文引擎；当前源码和测试不再新增 `DotSearch.*` / `DotVector.*` 引用。

### Changed

- **M28 P4 #223 向量度量贯通 + efConstruction 独立（索引 I7 / I9）**：段级向量索引此前**一律按 cosine 建图、且 ANN gate 仅 cosine**——声明为 L2 / InnerProduct 的向量列即便建了索引也永远走不到 ANN，白占空间仍退化为暴力扫描（I7）；同时 HNSW 的 `efConstruction`（建图候选规模）被查询侧 `ef`（efSearch）绑死，一个小的检索 `ef` 会把一张低质量图永久烤进持久化 blob（I9）。现让**声明的度量贯通建图与检索**：向量索引成为带度量的对象（新增 `VectorIndexDefinition.Metric`，四种索引类型通用），建图按声明度量组织图 / 倒排结构（`VectorIndexAdapter` 去掉硬编码 `Cosine`，`KnnMetric` 直通 Core 向量层已支持的 L2 / InnerProduct），检索时 `KnnExecutor` 的 ANN gate 改判**查询度量 == 索引建图度量**才走 ANN、否则回退暴力扫描（保正确）；HNSW 新增独立的 `efConstruction`（`HnswVectorIndexOptions.EfConstruction`，缺省 `max(ef, 200)`，与 `ef` 解耦，建图质量不低于检索精度）。度量与 efConstruction 全程持久化：**measurement schema 文件升版 v4→v5**（非 None 索引在 kind 字节后追加 1 字节 metric，HNSW 追加 int32 efConstruction；读 v<5 旧文件时 metric 默认 cosine、efConstruction 取 `max(ef,200)`，语义与旧行为一致），**段内向量索引 section SDBVIDX 升版 v3→v4**（record 尾部追加 Metric + EfConstruction 两个 int32，读 v3 旧段默认 cosine——旧段本就一律 cosine 建，语义正确——efConstruction 回填 `max(Ef,200)`；HNSW 首选路径从持久化 blob 头读度量/efConstruction，IVF/PQ/Vamana 从 SDBVIDX 持久化度量重建 payload）。DDL 扩展：`WITH INDEX hnsw(m=…, ef=…[, ef_construction=…][, metric='cosine'|'l2'|'inner_product'])`，`ivf`/`ivf_pq`/`vamana` 均接受 `metric=`（省略即 cosine，向后兼容既有语句）。已知遗留 `HnswVectorBlockIndex`（cosine-only，非生产路径，待 #228 删除）保持不变，不在本项范围。测试：catalog 工厂默认值（度量 cosine、efConstruction=max(ef,200)）、schema codec v5 度量/efConstruction 往返（含 IVF/PQ/Vamana 非 cosine 度量）、parser（`metric=` 别名 cosine/l2/euclidean/inner_product/ip、`ef_construction=`、未知度量拒绝、IVF/Vamana 带度量）、段级 SDBVIDX 度量/efConstruction 持久化（HNSW blob + IVF rebuild）、reader adapter 报告声明度量、**L2 索引服务 L2 检索命中而 cosine 查询从 ANN 返回空（由上层回退）**、端到端 `metric='l2'` HNSW 段刷盘重开后 `knn(…, 'l2')` 走 ANN 且结果正确。Core 2621 + CrashTests 8 全绿。

- **M28 P4 #222 FTS 批量成段 + 增量语料统计（I3）**：`PersistentFullTextIndex` 此前每次 `Index` 写一个单文档 segment 并全量落盘一次 manifest（每次 tombstone 还把该段整个墓碑集合重排序物化进 manifest）——批量写 N 篇文档 = N 个段文件 + N 次 manifest fsync 落盘（O(N²) 尾部成本）；且 `ScoreTerm`/`ScorePositional` 每次查询都遍历全部 segment × 全部文档重算字段语料统计（docCount/totalLength）。现新增 `IndexMany(IReadOnlyList<Document>)`（整批构建为**单个不可变段**、manifest 只落盘一次；批内重复 ID last-write-wins，与逐条 `Index` 语义一致）与 `DeleteMany(IEnumerable<DocumentId>)`（整批 tombstone 一次落盘）；manifest 墓碑列表改为**按段惰性物化**（`SaveManifest` 前只对 dirty 段排序一次，不再每次 tombstone 全量重排）。字段语料统计改为**增量维护**（`_fieldStats` 随段加载/批量写入/tombstone/merge 同步增减），`GetFieldStats` 从 O(segments×docs) 遍历变 O(1) 查表——BM25 分数与重开索引后全量重建的统计逐位一致（测试断言 precision 12，覆盖混合单条/批量/删除/覆写与 merge 后）。上层贯通：`DocumentFullTextIndexStore` 新增 `UpsertMany`/`DeleteMany`，`Rebuild` 改走 `IndexMany`（全量重建从 N 段 N 次 manifest 变一段一次）；`DocumentCollectionStore` 批量路径（`InsertMany`/`UpdateMany`/`DeleteMany`/TTL 过期清扫）经新的 `ApplyPlannedMutationsLocked` 把全文索引维护整批成段（每索引一次 `DeleteMany` + 一次 `UpsertMany`），KV 主键/二级索引仍逐条应用语义不变。单条写入路径、段文件二进制格式、manifest 格式与崩溃恢复（#192 段文件重建）全部不变。

- **M28 P4 #221 文档查询惰性 scan（I2）**：`DocumentQueryPlanner` 此前在 `ChooseAccessPath` 里把每个候选访问路径的行集立即物化（全表 scan 候选 `store.Scan().ToArray()` 反序列化整个集合），即便最终选中 `_id`/二级索引路径，每次文档查询仍要付一次 O(collection) 的全集合反序列化。现访问路径候选改为惰性：候选携带行加载委托与**不物化文档的计数估算**——`KvKeyspace` 新增 `CountPrefix`（与 `ScanPrefix` 同一可见性/过期语义，但只走 key 索引不读 value），`DocumentCollectionStore` 新增 `Count()`（文档前缀计数）与 `CountByIndex`/`CountByIndexPrefix`（索引条目前缀计数），planner 用计数完成代价比较后只加载胜出候选的行。`EXPLAIN` 输出形状不变（`BuildPlan` 对选中候选仍物化以给出精确 `estimated_output_rows`；落选候选的 `rows=` 由计数估算给出，语义等价）。同型强制物化一并收口：`DocumentSqlExecutor.ExplainAccess`/`DescribeCollection` 的文档计数、`HybridSearchExecutor`/`DocumentVectorSearchExecutor` 的 `ExplainAccess`、Server 质量报告 `MaintenanceEndpointHandler` 的集合计数全部改走 `Count()`/`CountByIndex`，不再为拿一个行数反序列化整个集合。新增回归：索引/`_id`/复合索引前缀路径命中时全表 scan 零调用（内部 `FullScanCount` 观测）、无可用索引时回退 scan 结果等价、`Count*` 与物化行数一致（含删除后）、KV `CountPrefix` 的过期/删除/compact overlay 语义。

- **M28 P5a #234 SonnetMQ 冷数据下沉，修复无界内存（MQ3）**：`TopicState.Messages` 此前把所有未裁剪消息（含 payload）全量常驻内存，`Pull` 只读内存、段文件写完从不回读、`SegmentCacheSize` 文档写了却未实现——一个消费者跟不上（或无消费者）的 topic 内存随消息数无界增长，边缘节点长期高吞吐 OOM（🔴 数据可靠性级，同 P0 掉电丢数据同级）。存储读模型改为**有界热尾 + 冷数据按需读盘**（仅目录模式）：新增 `SonnetMqOptions.HotTailMaxBytes`（默认 64 MiB），追加使常驻 payload 超限时从头部驱逐最老消息；offset 稀疏索引升级为**位置索引**（offset → 段 baseOffset + 段内字节位置，publish 与 replay 均按 stride 采样、每段首条必采），Pull 命中热尾走原内存路径（keeping-up 消费者零回归），冷 offset 则二分位置索引取锚点、经 `RandomAccess` 从段文件顺序解码跳到目标 offset 连续读取，跨段自动续读、抵达热尾边界无缝转内存。冷读句柄走每 store 的只读 `SafeFileHandle` LRU，`SegmentCacheSize` 从「保留字段」变为真正生效（超容量关最久未用；活跃写入段不入只读缓存；段被 retention 删除时同步失效）。重启 replay 同样填充位置索引并施加热尾上限——大积压 topic 重启后内存亦有界。**唯一语义变更**：`RetentionMaxAge` 按时间裁剪从逐条改为**按段粒度**（整段最新记录超龄且非活跃段才裁，与 `RetentionMaxBytes` 的按段裁剪及 Kafka 时间保留一致），不再要求每条时间戳常驻内存；`GetStats().MessageCount` 语义明确为「未裁剪消息数」（`NextOffset - TrimmedBeforeOffset`，与是否驻留内存无关）。offset / replay / durability / 组提交 / per-topic 锁语义全部不变；on-disk 段格式不变（旧段可读）；单文件模式与 legacy 日志来源消息保持全量常驻（无段边界，不驱逐）。新增驱逐后冷读正确性、单次 Pull 跨冷/热边界连续、段数 ≫ `SegmentCacheSize` 压 LRU、驱逐后重启 replay 不丢、按段粒度 age 裁剪保活跃段五项测试。**至此 P5a（#230~#234）收官，SonnetMQ 不再携带任何 🔴 Critical 缺陷。**
- **M28 P5a #233 SonnetMQ 组提交 leader-flush 合并刷盘**：`FlushOnPublish=true`（默认）此前每条 publish 一次刷盘系统调用，抵消 128KB `BufferedStream` 的批量意义（MQ4）。新增 `SonnetMqOptions.GroupCommitPublish`（默认启用）：并发发布到同一 topic 的多个 publish 把各自刷盘合并——各 publish 先在 topic `SyncRoot` 内追加记录（延迟刷盘）并推进 `AppendedSeq`；随后在 `SyncRoot` 外经每 topic `FlushRoot` 选举一个 leader，leader 仅在真正 `Flush` 的瞬间借回 `SyncRoot`（`FileStream` 非线程安全，须与写入者序列化），一次刷盘覆盖此刻 `AppendedSeq` 之前的全部记录并写 `FlushedSeq`；字节已被覆盖（`FlushedSeq >= 自身 seq`）的并发发布者直接跳过自己的刷盘。**实现取舍**：ROADMAP 曾拟照搬 `WalGroupCommitCoordinator` 的「定时窗口」模型，但那是为 `SyncWalOnEveryWrite`（fsync ≈367µs）设计、2ms 窗口可摊薄；MQ 默认 os-flush 仅 ≈5.6µs（#230 基线），套用定时窗口会把默认路径拖慢约两个数量级。故改用**无定时**的 leader-flush：合并窗口 = 一次刷盘的在途时长本身，单发布者无争用时立即刷盘（延迟不变），仅并发争用下减少刷盘次数。持久性语义不变（每个 publish 仍在其字节刷到所配置持久层后才返回）；跨段滚动时旧段在 Dispose 前先 fsync（`SyncOnPublish` 下），避免 leader 误判旧段延迟记录已持久。单文件模式（共享一个流）与 `GroupCommitPublish=false` 回退逐条内联刷盘。新增并发 durable publish 重启不丢消息、跨段滚动重启不丢消息、`GroupCommitPublish=false` 仍持久三项测试。
- **M28 P5a #232 SonnetMQ 写路径零冗余拷贝 + 合并写**：消除单条 `Publish` 把 payload `ToArray()` 两次（先封 `SonnetMqPublishEntry` 再 `PublishMany` 二次拷贝）——`Publish`/`PublishMany` 现共用 `PublishPrepared`，payload 仅在入常驻消息时拷贝一次（MQ2）；空 header 复用 `EmptyHeaders.Instance` 单例，不再 `new Dictionary`（MQ2）。`WriteRecord` 由 4 次 `stream.Write`（头/topic/meta/payload）改为把定长头 + topic + meta 合并进一个 `ArrayPool` 租借前缀缓冲区一次写出、payload 单独直写（2 次写；大 payload 免二次拷贝）（MQ5）。`EncodeHeaders` 弃用 `StringBuilder` + LINQ `OrderBy` + 逐值 `Convert.ToBase64String`，改为 `Array.Sort(CompareOrdinal)` + `ArrayBufferWriter<byte>` + `Base64.EncodeToUtf8` 直接编码 UTF-8 header 帧（MQ6）。**实现取舍**：ROADMAP 曾拟用 `RandomAccess.Write` scatter/gather，但队列写入器是带 128KB 缓冲的 `FileStream`（与引擎 `WalWriter` 同款「组装进单缓冲区再写 BufferedStream」范式），且 #233 组提交将建立在该缓冲刷盘模型上——改走裸句柄 `RandomAccess` 会绕过并使 FileStream 缓冲失步、反而拖慢小消息热路径，故按 house 风格用合并缓冲写。写入的段帧二进制布局与 `Replay` 解码保持不变；新增多 header（含空值 / unicode / 乱序 key）经编码→落盘→重启 replay→解码的往返一致性测试。
- **M28 P5a #231 SonnetMQ 去全局锁：per-topic 锁分片**：`SonnetMqStore` 此前用单一 `_sync` 锁串行化所有 topic 的 Publish/Pull/Ack/Stats/Trim，零并发分片。改为顶层 `ConcurrentDictionary<string, TopicState>` 无锁查找 + 每个 `TopicState` 自持一把 `SyncRoot`（Kafka partition 思路），topic 间发布/拉取互不阻塞（MQ1）；`TrimRetention` / retention worker 只锁被裁剪的单个 topic，`File.Exists` / `FileInfo.Length` 等文件系统调用不再持锁阻塞其它 topic 主路径（MQ7）。单 topic 内 publish 顺序与 offset 单调性保持不变（同一 `SyncRoot` 串行）。**SingleFile 模式**下所有 topic 共享同一底层 `FileStream`，故其 `SyncRoot` 统一回退到全局锁以保证流写入串行；目录模式（服务端默认）享受真正的 per-topic 并发。新增同 topic 并发 publish 产出连续唯一 offset、跨 topic 并发 publish 各自 offset 独立两项并发测试（MQ1、MQ7）。
- **M28 P3 查询与 SQL 能力批次**：
  (#212) `SqlParser.Parse` 新增进程级有界 LRU 解析缓存（默认 512 条，按 SQL 文本 key）：解析纯语法、与 schema 无关且 AST 不可变，故按文本缓存并跨调用复用安全，命中直接返回已解析的不可变 AST。消除高频轮询同一 query 形状（仪表盘等）每次 `Execute` 重复 lex+parse 的分配与 CPU；超长（> 8 KB）单条 SQL 与语法错误不入缓存。所有走 `SqlParser.Parse` 的路径（嵌入式、ADO、HTTP、MCP、Copilot）透明受益（Q7）。
  (#213) 参数化查询 / 绑定变量：新增位置 `?` 与命名 `@name` / `:name` 占位符，贯穿 lexer（`TokenKind.Parameter`）→ AST（`ParameterExpression`）→ `SqlParameterBinder` 值绑定 → `SqlExecutor.Execute(..., SqlParameters)` 重载。带占位符的 AST 与参数值无关，可命中解析缓存并对不同参数值复用；执行前把 CLR 值绑定为字面量节点（`byte[]`→Base64、`DateTime`→Unix 毫秒、`GeoPoint`→`POINT(...)`、`null`→SQL NULL）。嵌入式 ADO 改走 Core AST 值绑定（防注入，不再字符串拼接）；远程因线协议仅接受 SQL 字符串，仍在客户端安全替换命名参数（Q10）。
  (#214) `ORDER BY … LIMIT k` 有界 Top-N：新增 `TopN` 工具，`ORDER BY` + `Fetch` 上限时用大小为 `offset+fetch` 的有界堆单遍选出前缀（O(N log K)），替代"全量 `OrderBy().ToArray()` 再 `Skip().Take()`"（O(N log N) + 大物化峰值）；无上限时回退全量排序。稳定排序语义保持（等序保留输入相对顺序）。measurement / 关系表 / 关系子查询三条 SELECT 路径统一走融合的 `ApplyOrderByAndPagination`（Q6）。
  (#215) 关系 JOIN 哈希连接：关系-关系 JOIN 从全物化嵌套循环笛卡尔积（两张 1 万行表 = 1 亿次谓词求值）改为等值键哈希连接（O(N+M)）。`TryPlanHashJoin` 拆 ON 顶层 AND 合取，识别 `left_col = right_col` 等值键（两侧均唯一裸列、分属左右关系），对右侧建哈希、左侧探测；非等值合取项作残差在候选对上再求值；含子查询的 ON 回退嵌套循环。NULL 键不匹配、LEFT JOIN 未命中行保留、多列键、数值跨类型（int/float）相等均与旧实现一致（Q9）。
  (#216) 非相关子查询记忆化：`IN(subquery)` / `EXISTS` / 标量子查询原先每外层行重执行整个内查询（O(n_outer × n_inner)）。新增运行时相关性探针 + per-查询记忆表：子查询首次执行时挂探针，若整段执行未通过外层作用域链读取任何列（非相关），其结果被缓存供本层后续外层行复用；一旦观测到读取外层列（相关）则标记不缓存、逐行照常执行。基于运行时观测而非静态分析，只在证实非相关后才缓存，杜绝误缓存相关子查询产生错误结果（Q8）。
  (#217) 时序 WHERE 字段谓词与 OR：`WhereClauseDecomposer` 原先只接受"AND 连接的 tag 等值 + time 比较 + geo"，其余（字段比较、顶层 OR、非等值 tag、`IS NULL`、`IN`、`NOT`、`LIKE`/`REGEX`）直接抛"不在 v1 支持范围"。现将不可下推的谓词收集为残差合取项，扫描路径按数据点逐点以三值 Kleene 逻辑求值（与 #197 关系路径语义一致，`NULL`→UNKNOWN→丢弃），只保留确定为 TRUE 的点。`WHERE temp > 30`、`WHERE tag='a' OR tag='b'`、`WHERE host != 'h1'`、混合 `tag AND time AND field` 均可用；tag/time 仍尽量下推为等值过滤与时间窗，仅残差逐点求值。有残差时禁用 latest（`ORDER BY time DESC LIMIT 1`）快路径、流式窗口路径与扩展聚合 sidecar 快路径（它们会绕过逐点过滤），改走物化路径保证正确性；残差引用的 field 列自动纳入查询列。`EXPLAIN` 的 WHERE 分解改为复用同一 `WhereClauseDecomposer`，与执行路径保持一致。DELETE 因按 (series, field, 时间窗) 墓碑删除无法表达字段级定向，遇残差显式拒绝（按点定向删除见 #219）（Q5）。
  (#218) 事务隔离 / read-your-writes：轻事务缓冲的关系表 insert/update/delete 原先对事务内 SELECT 不可见（SELECT 读已提交态、看不到自身缓冲写）。新增 `SqlTransactionContext` 的 ambient `AsyncLocal` 作用域，`ExecuteStatement` 执行期间把活动事务设为当前作用域；关系表 SELECT 读路径（`TableSqlExecutor.ExecuteSelect` 与 `RelationalSelectExecutor.LoadTable`）改为在已提交基线上叠加本事务对该表的缓冲变更（按主键合并、保序追加新插入、删除移除对应行），再由 WHERE 过滤。事务内 SELECT 因此能看到自身写入，覆盖直接查询、聚合与子查询；聚合/子查询走关系路径同样受益。隔离级别明确为**读已提交 + 本事务 read-your-writes**（不做快照、不加读锁，对其他并发写仍读已提交）；measurement/document 写入在事务内已被 #199 拒绝，故只覆盖关系表。ADO `BeginTransaction()` 透明获得该语义（事务态贯穿到执行层）。有本表缓冲写时该表 SELECT 退回全表 scan 后叠加（快路径可能漏掉未提交插入或返回旧值），无事务时不变（Q4）。
  (#219) 关系 SQL 语义补齐（Q11/Q12/Q13/Q15）：`SELECT DISTINCT` 原先无关键字、`DISTINCT` 被误当作首列的无 `AS` 别名静默丢弃。现新增 `distinct` 关键字与 `SelectStatement.Distinct` 标志，并在 `ExecuteSelect` 单一收敛点结构化去重——覆盖 measurement / 关系 / 文档等所有 SELECT 路径；求值顺序遵循标准 SELECT→DISTINCT→LIMIT（先剥离分页算全量投影，去重后再施加 OFFSET/FETCH）；去重比较器按"整型 vs 浮点"两个规范化命名空间比较（整型统一为 `long`、浮点为 `double`、`byte[]` 按内容），避免把大 `long` 折成 `double` 时误合并。**大小写策略**：`RelationalSelectExecutor` 的关系 / JOIN 列名比较原先用 `Ordinal`（大小写敏感）而限定符用 `OrdinalIgnoreCase`，与 measurement / 关系表投影的大小写不敏感不一致；现所有列名比较统一经 `NameEquals` / `QualifierEquals`（均 `OrdinalIgnoreCase`），`SELECT sum(AMOUNT) FROM t WHERE amount > 10`、`JOIN ON c.ID = o.customer_id` 等大小写混用可用。**DELETE 值定向删除**：时序 `DELETE` 遇字段谓词 / OR / IN 等残差时原先直接拒绝（#217 延后至此）；现复用 #217 的逐点三值 Kleene 残差求值（`SelectExecutor.CollectResidualMatchedTimestamps`），对命中时刻在该 series 的所有 field 列追加单点 `[ts,ts]` 墓碑（墓碑以 (series, field, 时间窗) 为粒度，故按"一行=一个时刻"删整行），未知列在无候选点时也做静态预校验硬报错；`WHERE usage > 2`、`WHERE count = 20`、`WHERE host='h1' OR host='h2'` 均可定向删除。**聚合返回类型**：`RelationalSelectExecutor` 关系聚合原先对每个 sum/min/max 做一次全量预扫判定输入是否整型、并统一 `Convert.ToDouble`，既多扫一遍又把大 `long` 折成 `double` 丢精度；现给 `RelColumn` 增加 schema 静态类型 `StaticType`，由列 / 字面量 / 算术表达式静态推断整型或浮点，命中即省预扫并对大 `long` 保持整型累加（`SumLongsWithOverflowPromotion` 仅溢出时升 `double`），仅表达式派生 / 子查询列静态未知时回退逐行预扫。
  (#220) QueryEngine 大范围点查询流式合并：`Execute(PointQuery)` 原先在租约内把**全部**候选 block 解码进 `List<DataPoint[]>` 再交给 N 路堆合并，大范围扫描的解码峰值正比于候选 block 总数（数万块时进 LOH、堆峰值高）。现 `BlockSourceMerger` 改为惰性流式合并：段 block 以 `(reader, descriptor)` 惰性源持有，仅当其 `MinTimestamp` 下界抵达合并前沿时才解码并入活跃堆，解码工作集被限制为「当前时刻相互重叠的 block 数」（overlap depth）而非候选总数；`Execute` 相应改为在整个流式枚举期间持有段快照租约的迭代器（枚举耗尽 / 提前 `break` 时经 `using` 释放租约，reader 存活覆盖惰性解码）。同时新增 `SegmentReader.DecodeBlockRangeView` 返回解码缓存数组的零拷贝 `ReadOnlyMemory<DataPoint>` 子区间视图——缓存命中不再每次 `CopyDecodedRange` 分配裁剪副本（缓存数组不可变，视图只读安全）；`TryGetLatestPoint` 段侧解码亦切换到该零拷贝视图。段内压缩产生的时间区间重叠 block、`ORDER BY time DESC LIMIT 1`、`LIMIT k` 提前终止、`Take(k)` 惰性截断均正确（并发 C9）。
- **M28 P2 写路径吞吐批次**：
  (#205) `EnsureMeasurementSchemaLocked` 稳态不再每点 `new List(schema.Columns)` 后丢弃，改为仅在真检测到新列 / int→float 提升时才 copy-on-write；`WritePointLocked` 对 `Dictionary` 支持的 `Point.Fields` 走 struct 枚举器，消除 `IReadOnlyDictionary` foreach 的枚举器装箱。缩短 `_writeSync` 临界区、降低写入热路径 GC 压力（C3）。
  (#206) 移除 `MemTable` 内部 `ReaderWriterLockSlim` 生命周期门：`Append` / `Reset` / `RemoveSeries` 三个写侧操作已由调用方 `_writeSync` 串行化（Reset 在 double-buffering 下已无生产调用方），该 RWLock 纯属冗余，`Append` 热路径每点省一对 enter/exit read-lock。读者无锁安全语义保留（`ConcurrentDictionary` + 每桶锁 + `Interlocked` 统计量不动），新增读者与 `Append` 并发压测回归（C10）。
  (#207) `SegmentManager` 段索引改增量维护：新增 `_indexById` 缓存，`AddSegment` / `SwapSegments` / `DropSegments` 只为新段调用一次 `SegmentIndex.Build`、复用未变段的索引、修剪已移除段，替换同 id 段时作废旧缓存。消除每次 flush 全量重建所有段索引（O(总 block 数)、段多时趋 O(N²)）的成本（C7）。
  (#208) `TombstoneTable` 查询免拷贝：维护 per-key 不可变 `Tombstone[]` 快照并 `Volatile` 发布，`IsCovered` / `GetForSeriesField` 改为无锁读取对应数组，消除每次查询锁内 `list.ToArray()` 的分配与锁竞争（C8）；写侧仍在 `_lock` 内重建 per-key 与全量两份快照。
  (#209) `SeriesCatalog` / `TagInvertedIndex` 高基数写入去 O(N²)：不再每新增一条 series 就全量 `ToFrozenDictionary()` / `ToFrozenSet()` 重建快照，改为多级 `ConcurrentDictionary`（`SeriesId` 集合用 `ConcurrentDictionary<ulong,byte>` 充当并发 set）原地增量插入，查询无锁读取、写者立即可见。单条插入从 O(N) 全量冻结降到 O(1) 摊还，消除高基数 ingest 的代数复杂度陷阱与大量瞬时分配（I5）。
  (#210) `SegmentReplacementManifest` 修剪与抑制快照化：每次 `Mutate` 后修剪 replacement 与全部 source 段都已不在盘上的 Committed 记录，杜绝 committed 记录无限累积致每次写 O(N)、会话内趋 O(N²)；`GetSegmentIdsToSuppress` 启动时一次性快照现存段 id，仅当 id 确在盘上才打开 `SegmentReader` 验证，避免对每条 committed replacement 都开 reader（S7）。修剪键于文件存在性而非可读性，且 commit 早于文件删除，崩溃安全不变。
  (#211) 孤儿段文件清理 + WAL footer 不变式收口：`SegmentManager.Open` 启动时清理被 manifest 抑制、上次删除失败残留在盘上的死段主文件及向量/聚合索引 sidecar（此前被跳过加载后永久泄漏磁盘），删除仍失败的文件下次启动重试（S12）；`WalWriter.WriteLastLsnFooterIfDirty` 写 footer 前自行 `_stream.Flush()` 排空 BufferedStream，使"footer 偏移 = `_bytesWritten`"不再依赖调用点先 flush 的隐式顺序，显式收口防未来改动破坏 WAL 帧（S13）。
- **写入持久性默认加固（M28 #196）**：新增 `TsdbOptions.FlushWalToOsOnWrite`（默认 `true`），每次写入后把 WAL 缓冲 flush 到 OS（不 fsync）。**行为变更**：此前默认下 WAL 记录仅停留在进程内 BufferedStream，直到 segment flush / roll / dispose 才交给 OS——普通进程崩溃（非掉电）也会丢失最近一个 flush 窗口内的已确认写；现在默认下进程崩溃不再丢已确认写（数据已在 OS page cache），仅掉电/内核崩溃可能丢。持久性分三级：`FlushWalToOsOnWrite=false`（极限吞吐，最弱）＜ 默认 `true`（进程崩溃安全）＜ `SyncWalOnEveryWrite=true`（每批 fsync，掉电安全）。开销为一次用户态→内核态拷贝，远低于 fsync。需要极限写吞吐可显式设为 `false`。
- CI workflows now use `actions/cache@v6` for NuGet package caching.
- README / README.en 第一屏介绍改为产品门面叙事，避免使用“收敛到一个……”这类内部路线图表达。
- **路线图状态校准**：`AGENTS.md` 当前推进口径从旧 Milestone 16 调整为 Milestone 27 / 17 / 18 并行推进，并明确 M27 当前滞后、需优先追赶 #183~#188；`ROADMAP.md` 总览同步将 Milestone 21 与 Milestone 26 标记为完成，将 Milestone 27 标记为滞后，将 Milestone 22 从 SonnetDB 内置路线降级为“基于 SonnetDB 的上层应用 / 示例方案候选”，暂停 #150~#159 内置派单，并在当前推进顺序中明确 M21、M26 已收口、M22 只有沉淀出通用数据库能力缺口时才拆 Core / Server / Studio PR，避免后续 AI 协作继续按旧目标派单。
- **ROADMAP 归档精简**：主 `ROADMAP.md` 移除 Milestone 0 ~ 16 的早期详细正文，仅保留摘要表和当前 / 未来路线；历史 PR 拆分、设计说明、示例与路线差异说明迁入 `docs/roadmap-history.md`。
- README / README.en 与 docs 首页第一屏从“多模型数据库”主叙事改为“.NET 工业边缘本地数据引擎”主叙事；多模型能力保留为能力矩阵，Copilot / MCP / Agent 描述收敛到工业数据查询、诊断、维修建议和写入审批场景。
- 统一 SonnetDB 当前文案边界：NuGet Description、PackageReadme、Copilot SQL prompt、协作规范与 IoTSharp 兼容矩阵不再把产品描述为“单文件数据库”，改为数据库目录持久化；同时明确 Core / ADO.NET / EF Core / CLI / Caching 的 Native AOT 声明边界，并修复帮助文档相关 XML 注释乱码。
- **Milestone 21 规划收敛**：ROADMAP 将 Document Store 单机能力升级收敛为纯能力 / 功能交付，Milestone 21 仅保留 #137~#146（Document API、find、cursor、局部更新、索引、planner、aggregation、原子性、validator 执行能力与文档容量底座）；原 #145 中的 Studio schema governance 与原 #148 Document Explorer / 导入导出迁入 Milestone 24，原 #147 MongoDB 参考 parity 与原 #149 长稳、容量、发布文档迁入 Milestone 25。
- **SonnetMQ 合并进 Core**：本地消息队列源码从独立 `src/SonnetMQ` 项目移动到 `src/SonnetDB.Core/Mq`，`SonnetDB` 服务端、`SonnetDB.Data` 与 Parity 测试统一通过 `SonnetDB.Core` 引用 MQ 能力；发布脚本、Dockerfile 和解决方案不再构建或打包独立 `SonnetMQ` 项目。本次仅调整项目边界，不修改 SonnetMQ 日志文件格式。

### Fixed

- **M28 P0 数据可靠性止血批次**：一轮跨子系统审计后修复一组崩溃/并发数据安全缺陷——
  (#189) `DirectoryFsync` 在 Windows 用 `CreateFileW(FILE_FLAG_BACKUP_SEMANTICS)`+`FlushFileBuffers` 实现真实目录 fsync（替换旧空操作），保证 segment 改名 + 目录项落盘早于 WAL 回收，并补 catalog / measurement schema 改名后的目录 flush；
  (#190/#204) MemTable 双缓冲统一快照（SegmentManager 作为 {active+sealing MemTable+segments} 单一原子发布者），消除 flush 期间"数据两处都无"的并发查询丢数据窗口，并把 flush 编码落盘移出全局写锁（FlushPump 单线程泵）；
  (#191) Compaction / Retention / DropMeasurement 走读租约 + maintenance 串行锁，消除后台 worker use-after-dispose 与"compaction 复活 retention 刚删数据"；并修复 DropMeasurement/backup 未排空 flush 泵导致在飞 flush 漏删的竞态；
  (#192) 全文索引 manifest / segment 写入改原子 `File.Move(overwrite)`+fsync，manifest 缺失时从段文件重建而非静默建空；
  (#193) HNSW 快照重载跳过 tombstone 行，修复"删除后重插同 key 的持久化向量索引无法加载"；
  (#194) Delete 无条件同步 WAL，消除"已落段数据被删后崩溃复活"；
  (#195) SegmentFooter 增加版本门控的自校验 CRC，检出满足布局等式的字段位翻转。
  新增 `SonnetDB.CrashTests` 真 kill-9 delete 场景与多项并发/崩溃回归测试。
- **M28 P1 正确性批次**：
  (#197) SQL 关系执行路径改用三值逻辑（Kleene）——任一操作数为 NULL 的比较（`=` / `!=` / `<` / `>` 等）判 UNKNOWN 而非 TRUE/FALSE，修复 `NULL != 5` 误判 TRUE、`NULL = NULL` 误判 TRUE 返回错误行；`AND`/`OR`/`NOT`/`IN` 按三值语义传播，`IS [NOT] NULL` 拆出独立 `IsNullExpression` AST 节点作为唯一的 NULL 检测入口（解析器不再把 `IS NULL` 退化成 `= NULL`）。统一应用到 WHERE / JOIN ON / HAVING 三条关系路径（TableSqlExecutor / RelationalSelectExecutor / JoinSqlExecutor）；文档集合仍保留 Mongo 风格 `field = NULL` 空值等值语义不变。
  (#198) measurement `count(*)` / `count(1)` 语义修正为计"行/时刻"数——按 series 取所有 field 列写过的时间戳并集去重，而非旧实现遍历每个 field 列逐点累加（M 个 field × N 时刻误返 M×N）；`GROUP BY time(...)` 下按桶分别取时间戳并集，多 series 分别计入。`count(field)` 仍只计该字段有值的时刻数。
  (#199) 轻事务上下文内的 measurement（时序）`INSERT` / `DELETE` 从"静默直写、ROLLBACK 无法撤销"改为显式抛 `NotSupportedException`（与文档集合写入一致），杜绝"`BEGIN` 内写 measurement、`ROLLBACK` 后数据仍在"的假回滚；被拒绝时同批已排队的关系表变更也不会提交。
  (#200) SQL 解析器新增表达式递归深度上限（200）：深层括号 `((((…))))`、`NOT NOT NOT…`、`------x` 等自递归输入超限时抛 `SqlParseException`，而非触发不可捕获的 `StackOverflowException` 直接终止宿主进程；扁平 `AND`/`OR` 长链走循环不受影响。
  (#201) `KvExpirerWorker` 后台过期清理失败时补发 `ReportBackgroundWorkerDiagnostic` 诊断事件（`KvExpirerWorker.CleanExpired` / Error），与 Flush / Compaction / Retention 三个 worker 对齐，杜绝反复失败静默不可见（C11）。（CompactionWorker 的 plan 步骤 try/catch 兜底 C6 已在 P0 #191 落地。）
  (#202) `WriteMany(ReadOnlySpan<Point>)` 超大批量改为按 8192 点分块：每块单独入锁、写入、检查硬上限并在锁外施加背压，块间释放 `_writeSync`。杜绝百万点单批在一次持锁内无界撑大 MemTable/WAL 致 OOM 且长时间阻塞所有写入者（C4）；中小批量仍是单次入锁，快路径开销不变。
  (#203) `SyncWalOnEveryWrite=true` 且 group-commit 禁用 / `FlushWindow=0` 时，把 WAL `Sync()` 从 `_writeSync` 锁内移到锁外执行（`Prepare` 返回携带 `WalSegmentSet` 的 ticket，`Wait()` 在释放写锁后 fsync），消除"所有写入者串行排在 fsync 后"的吞吐悬崖（S10/C5）；推迟 fsync 若与 `Dispose` 竞争抛 `ObjectDisposedException` 则静默跳过（`Dispose` 自身会 fsync active writer 保证持久性），不再把 ODE 泄漏给写入调用方（S11）。
- 持久化全文索引后台合并改用专用长运行任务启动，避免高并发 CI 测试下因线程池调度延迟导致等待 merge task 超时。
- 持久化全文索引后台合并测试改为等待实际 merge task 完成后再断言段文件数量，避免 CI 线程调度较慢时误判失败。
- Go connector quickstart now keeps the native library version string and KV CAS version number in separate variables, fixing the `go test ./...` compile failure in connector release builds.
- Accuracy Tests 在 Docker / InfluxDB 2.x 不可用时不再因 Testcontainers fixture 构造失败而整组失败，改为沿用既有跳过路径输出原因并让主 CI 继续执行。

## [2.5.0] - 2026-06-26

### Added

- **SonnetDB Studio 主线确立**：现有 Vue Workbench 正式升级为 SonnetDB Studio，新增 `/admin/app/studio` 语义入口并保留 `/admin/app/sql` 兼容；`src/SonnetDB.Studio` 新增基于 NativeWebHost / WebView2 的 Windows 桌面壳，默认打开同机 SonnetDB Server，也可通过 `--server-url` 指向远程实例。
- README 社群二维码改用 PNG 资源，提升 Gitee README 渲染兼容性。
- **Milestone 22 规划**：ROADMAP 新增 Agent Memory / Codebase Intelligence 路线，定位为 SonnetDB 对外提供的代码知识库与 MCP Memory 后端能力，覆盖 Code Memory 标准 schema、Git/files/chunks ingest、C# 符号与调用边索引、只读 MCP typed tools、Hybrid Search、Agent Memory 持久化 API、Web Admin Explorer、VS Code / Copilot 接入样例与规模报告，并明确语言解析器和 Git 扫描依赖不进入 `src/SonnetDB.Core`。
- **Milestone 21 规划**：ROADMAP 新增 Document Store 单机能力升级路线，目标达到 MongoDB 单机常用能力子集（CRUD、find/filter/projection/sort、cursor、局部更新、复合/unique/TTL 索引、aggregation、单文档原子性、MongoDB 参考 parity、Web Admin Document Explorer 与长稳报告），并明确不做 MongoDB wire protocol / BSON command / 官方 driver 直连协议兼容。
- **PR #136 CI gating + nightly parity 报告发布**：新增 `.github/workflows/parity.yml`，按每日 02:00 UTC 与手动触发运行 `{light, full} × ubuntu-latest` parity 矩阵，启动 `docker-compose.parity.yml` 后执行 `tests/SonnetDB.Parity` 与 `tests/SonnetDB.CrashTests`；新增 `tests/SonnetDB.Parity/reports/summarize-parity.ps1` 汇总 JSON / Markdown 报告，能力、可靠性与算法准确度失败作为红绿门槛，带 `performance_gating=warning_only` 的指标只进入 warning；nightly/手动结果会发布到 `parity-results` 孤立分支。README 新增 Parity workflow badge、开源栈对齐段落与最新结果入口，并提交 `tests/SonnetDB.Parity/reports/sample-run.md` 可读样例报告。
- **PR #135 可靠性套件（kill -9 / disk-full / oom / power-loss）**：新增独立 `tests/SonnetDB.CrashTests`（`<IsAotCompatible>false</IsAotCompatible>`，不进 `SonnetDB.slnx`），通过真子进程 + `Process.Kill(true)` 覆盖 `crash_kill9_during_fsync`、`crash_kill9_mid_compaction`、`disk_full_during_wal_append`、`oom_protection_memtable_backpressure`、`power_loss_torn_record`、`power_loss_half_renamed_segment` 六个恢复场景；连带新增 `MemTableFlushPolicy.HardCapBytes`（默认 `MaxBytes * 4`）同步背压 Flush、`SegmentCompactor.Execute` `CancellationToken` 检查，以及 BackgroundFlush / Compaction / Retention 后台 worker 异常路由到 `Tsdb.DiagnosticEvent`。
- **PR #134 分析 parity 场景套件（vs ClickHouse）+ 聚合精度对齐**：`tests/SonnetDB.Parity` 新增 `IAnalyticalOps`、`ClickHouseAdapter`（`ClickHouse.Client`）与分析场景 `groupby_time_1b_rows_wallclock`、`window_avg_7day`、`topn_per_device`、`columnar_compression_ratio`、`percentile_accuracy_p50_p95_p99`；`AnalyticsSuite_SonnetDbMatchesClickHouse` 会输出 JSON / Markdown 差异报告。性能与压缩率指标标记为 warning only，不作为红绿门槛；p50/p95/p99 等聚合数值按显式容差合同判定。
- **PR #133 全文 parity 场景套件（vs Meilisearch）+ BM25 排序对齐**：`tests/SonnetDB.Parity` 新增 `IFullTextOps`、`MeiliAdapter` 与全文场景 `index_1m_documents`、`bm25_ranking_top10_overlap`、`cjk_tokenize_correctness`、`facet_filter_query`、`incremental_update_during_query`、`typo_tolerant_query`；`FullTextSuite_SonnetDbMatchesMeilisearch` 会输出 JSON / Markdown 差异报告，并以 BM25 top-10 重合率 ≥ 0.8 作为排序准确度门槛。SonnetDB 当前未声明 CJK parity 与 typo-tolerant 能力，相关场景会结构化 SKIP 并在 capability gaps 中显式呈现。
- **PR #132 MQ parity 场景套件（vs NATS JetStream）+ SonnetMQ replay/retention 对齐**：`tests/SonnetDB.Parity` 新增 `IMqOps`、`NatsAdapter`（`NATS.Client.Core` + `NATS.Client.JetStream`）与 MQ 场景 `publish_consume_ack`、`consumer_group_offset`、`replay_after_restart`、`fan_out_10p_10c`、`backpressure_unbounded_producer`；`docker-compose.parity.yml` 补齐 NATS harness 环境变量。SonnetMQ 连带新增 `RecordTypeTombstone(3)`、目录模式 Topic 分段文件（默认 64MB）、time/size retention 手动与后台 worker、`FlushOnPublish=true` 默认值、重启后 tombstone / segment replay 语义与核心单元测试覆盖。
- **SQL `DROP MEASUREMENT IF EXISTS`**：新增 `DROP MEASUREMENT [IF EXISTS]`，删除 measurement schema、series catalog 与对应时序 Segment 数据；同名 measurement 重建后不会复活旧数据，Copilot draft/execute 白名单同步识别该写操作。
- **PR #131 对象桶 parity 场景套件（vs MinIO）**：`tests/SonnetDB.Parity` 新增 `IObjectOps`、`MinioAdapter`（`AWSSDK.S3` 指向 MinIO endpoint）与对象桶场景 `putget_1gb_object`、`multipart_upload_5gb`、`range_read_offsets`、`list_objects_v2_pagination`、`copy_delete_presigned_url_lifecycle`；SonnetDB 自检通过时会同时验证 put/get、multipart 合并、range read、ListObjectsV2 分页、copy、批量删除与 presigned 子能力。连带补齐 SonnetDB 对象桶 `ListObjectsV2 ContinuationToken` 返回/续页语义，以及 `POST /v1/db/{db}/s3/{bucket}?delete` 私有 JSON `DeleteObjects` 批量删除端点。
- **PR #130 KV parity 场景套件（vs Redis）+ 向量套件（vs Qdrant）**：`tests/SonnetDB.Parity` 新增 `IKvOps` / `IVectorOps`、`RedisAdapter`（`StackExchange.Redis`）与 `QdrantAdapter`（`Qdrant.Client`），覆盖 `set_get_scan_throughput`、`ttl_accuracy`、`incr_concurrency_16_clients`、`cas_optimistic_lock`、`scan_cursor_10m_keys`、`ann_recall_at_10`、`filtered_search`、`upsert_during_query`；`docker-compose.parity.yml` 补齐 Redis/Qdrant harness 环境变量与 Qdrant gRPC 端口。连带补齐 `KvKeyspace` 的 `INCR` / `DECR` / `CompareAndSet` / `EXPIRE` / `PERSIST` / `TTL`，并新增核心 KV 后台 expirer worker，语义通过新增单元测试与 Parity 自检覆盖。
- **SQL `DROP TABLE IF EXISTS`**：关系表 `DROP TABLE` 现支持可选 `IF EXISTS` 修饰；带 `IF EXISTS` 时表不存在视为成功（0 行受影响），不带时表不存在仍报错，与标准 SQL / Postgres 语义一致。
- **PR #129.2 TSDB parity：forecast TVF 列投影契约**：`forecast(...)` 表值函数继续暴露稳定完整列集合 `time, value, lower, upper, ...tags`，并支持 `SELECT time, value FROM forecast(...)`、列别名和未知列校验；SonnetDB parity 适配器的 Holt-Winters 召回查询可直接与 InfluxDB Flux `holtWinters` 的同构两列结果比较。
- **PR #129.1 TSDB parity：GROUP BY time bucket 投影对齐**：`SELECT time, avg(v) FROM m GROUP BY time(...)` 现允许在聚合桶查询中投影 `time`，返回 bucket 起始毫秒时间戳；`time AS bucket` 等别名会作为稳定等价列名保留。`ResultDiffer` 新增 `TimeBucketMs` 合同，只对 `time` / `bucket` 列把 InfluxDB `aggregateWindow` 与 PromQL `query_range` 常见的窗口 stop/evaluation timestamp 规范化为 bucket 起点后比较，数值列仍按原显式容差判定。
- **PR #129 TSDB parity 场景套件（vs InfluxDB / VictoriaMetrics）**：`tests/SonnetDB.Parity` 新增 `ITimeSeriesOps`、`InfluxAdapter`（`InfluxDB.Client`）与 `VictoriaMetricsAdapter`（Prometheus remote_write + PromQL HTTP），覆盖 `ingest_1m_points`、`groupby_time_window`、`derivative_accuracy`、`rate_irate_consistency`、`holt_winters_forecast_recall`、`percentile_p95_tdigest_vs_quantile`、`distinct_count_hll_2pct_error`；`ResultDiffer` 新增显式容差合同（HLL 2%、p95 2%、derivative/rate 严格容差）。该套件不绕开 SonnetDB 缺口，首轮暴露的问题按 ROADMAP #129.1 / #129.2 拆分修复。
- **PR #128 关系型场景套件（vs Postgres）**：`tests/SonnetDB.Parity/scenarios/relational/` 新增 `tpcc_lite`（5 仓库 / 30 分钟长跑 profile，默认结构化 SKIP）、`fk_cascade_constraint`、`isolation_read_committed`、`subquery_correlated`、`groupby_having`、`information_schema_introspection`、`update_returning_count`、`alter_table_evolution` 场景；关系型适配器扩展通用 SQL 执行 / 查询 / 独立会话事务接口，ResultDiffer 支持规范化 SQL 结果集比较；`ParityRunner` 现在一次运行完整关系型 suite，并在 JSON / Markdown 报告中输出能力差异表，明确展示 SonnetDB 对 `RelationalTpccLite`、`SqlCascadeDelete`、`SqlCorrelatedSubquery`、`SqlHaving` 等能力的 `SKIPPED` 缺口与已通过场景。
- **PR #127 Parity 骨架与第一对适配器**：新增独立测试项目 `tests/SonnetDB.Parity`（`<IsAotCompatible>false</IsAotCompatible>`，刻意不进 `SonnetDB.slnx`，避免污染主仓 AOT 流水线），含 `docker-compose.parity.yml` 12 服务栈（sonnetdb + postgres/redis/influxdb/victoriametrics/minio/nats/mosquitto/meilisearch/qdrant/clickhouse + harness，镜像标签全部 pin、healthcheck 齐备、named volumes、`light`/`full` profiles）+ `.env` + 本地 override 模板；落地 `IDataPlane` 契约 + `Capability` `[Flags]` 标志位 + `IScenario` / `ScenarioContext` / `ScenarioResult` + `ResultDiffer` 容差判定 + `BackendSelector` + 源生成 JSON / Markdown reporter + `ParityRunner` xUnit 驱动；首对适配器 `SonnetDbAdapter`（嵌入式 `SndbConnection`，临时目录，无需 docker）+ `PostgresAdapter`（`Npgsql`，不可达时记录 `gap_reason` 并 SKIP 而非 FAIL），跑通 1 个 `relational_hello_world` 关系型冒烟场景。遵循 Parity 路线图约定**不引入** Testcontainers / Verify.Xunit；竞品服务由 docker-compose 直接管理。
- **SonnetMQ 本地消息队列 MVP**：新增 `src/SonnetMQ` 零依赖核心库与 `docs/sonnetmq-roadmap.md`，采用 append-only log 提供 topic publish、consumer group pull/ack、单目录/单文件模式与重启 replay，为 IoTSharp 通过 SonnetDB 获得内置 MessageQueue 能力打基础。
- **Docker 发布补齐 SonnetMQ 项目引用**：`src/SonnetDB/Dockerfile` 在 restore 与 publish 阶段同步复制 `src/SonnetMQ`，避免服务端镜像构建时因缺少 `../SonnetMQ/SonnetMQ.csproj` 报 `CS0246`。
- **J10 SonnetDB Compaction 崩溃恢复与长稳加固**：新增 `segment-replacements.sdbmanifest` 段替换清单，Compaction 在写新段前记录 pending replacement、提交后标记旧段 superseded；启动加载 Segment 时按 manifest 跳过 pending 新段或已 superseded / retention dropped 旧段，避免崩溃后新旧重复段同时进入查询路径；Retention 整段 drop 也先提交清单再发布内存状态，补齐重启可恢复性测试。
- **J9 SonnetDB 大量物理分表与文件布局优化**：时序 Segment 新写入路径切换为 `segments/v2/{bucketGroup}/{bucket}/{segmentId}.SDBSEG` 分层目录，避免单个 `segments/` 目录长期堆积海量段文件；启动枚举、durable checkpoint 校验、备份扫描、Compaction / Retention 旧段清理均兼容旧版平铺 `segments/{segmentId}.SDBSEG` 与 legacy sidecar 文件，不修改 `.SDBSEG` 二进制格式。
- **J3 SonnetDB table MVP 前置能力补齐**：ADO.NET provider 新增 `Tables` / `Columns` / `Indexes` schema metadata 集合，可从嵌入式表 catalog 返回表、列、唯一索引和列序信息；SQL 新增 `REGEX` / `NOT REGEX` 字符串模式查询，关系表、表表 JOIN、文档和 hybrid 查询路径共享同一匹配语义，并补充 ADO.NET / EF Core 验收测试。
- **CI 依赖源码修复**：SonnetDB 仓库新增 `modules/DotVector` 与 `modules/DotSearch` 子模块，并让 CI / Publish / Docker / CodeQL checkout 递归拉取子模块；`SonnetDB.Core` 优先引用仓库内 `modules/` 下的 DotVector / DotSearch 项目，避免独立 OSS CI 缺少全文和向量引擎源码时报 `CS0246`。同时将 MCP 包版本统一到 1.3.0，并让 IoTSharp 兼容测试在外部 IoTSharp 源码不存在时只编译兼容矩阵基线。
- **PR #117 S3-compatible Bucket API 第一版**：SonnetDB 服务端新增数据库内置对象桶存储，metadata 写入 `__object_storage` KV keyspace，对象内容落盘到数据库目录 `objects/`；提供 bucket/object metadata、etag(MD5)、sha256、range read、copy object、delete marker、object tags、multipart upload、ListObjectsV2 风格列表和 presigned URL。`SonnetDB.Data` 新增 `SndbObjectStorageClient`，统一支持嵌入式与远程 SonnetDB 对象访问；IoTSharp 新增 `SonnetDbBlobStorage` 适配器，可通过 `sonnetdb://?connectionString=...&bucket=...` 让 BlobStorage 使用 SonnetDB 对象桶。
- **PR #116 KV TTL 与缓存 Provider**：`SonnetDB.Core` 的 KV keyspace 新增 expires-at metadata、惰性过期、`CleanExpired` 后台清理入口、逻辑命名空间、批量 get/set/remove、前缀删除与过期统计；KV WAL / snapshot / segment state 升级到 v2 并兼容 v1 读取。新增 `extensions/SonnetDB.Caching`，提供 SonnetDB KV-backed EasyCaching provider、`IEasyCachingProviderFactory` 适配、可选 `IDistributedCache` provider 和周期过期清理宿主；IoTSharp 可通过 `CachingUseIn=SonnetDB` 作为 Redis / LiteDB / InMemory 之外的缓存选择。
- **Milestone 19 规划更新**：在 IoTSharp 生态数据底座路线中，将“SonnetDB EF migrations history”明确前置到 PR #115，要求先补 `__EFMigrationsHistory` 或等价可配置历史表，并以 `Database.Migrate()`、迁移升级/回滚、重复执行幂等检查和空库初始化作为 `DataBase=SonnetDB` 接入 `ApplicationDbContext` 前的入口验收。
- **PR #114 SonnetDB.EntityFrameworkCore Provider MVP**：新增独立 `SonnetDB.EntityFrameworkCore` Provider 包，提供 `UseSonnetDB(...)`、关系型服务注册、SonnetDB ADO.NET 连接、SQL 标识符/参数生成、基础类型映射、DML SQL 生成、迁移 SQL 生成与基础查询翻译能力；新增 `tests/SonnetDB.EntityFrameworkCore.Tests`，覆盖 provider 服务注册、最小 `DbContext` CRUD、Identity 常用列子集、`ToQueryString()` SQL 生成以及迁移创建/回滚 DDL。
- **PR #113 跨表小事务与约束能力补齐**：关系表轻事务从单表扩展为同一数据库内多表 `INSERT` / `UPDATE` / `DELETE` 原子提交与回滚；`CREATE TABLE` 支持表级 `FOREIGN KEY (...) REFERENCES ... (...)` 第一版校验策略和列级 `ROWVERSION` 乐观并发列，DML 提交统一校验主键、唯一索引、外键和并发版本；新增稳定约束错误码 `table_unique_violation`、`table_foreign_key_violation`、`table_concurrency_conflict`，并在 SQL / ADO.NET 文档中明确当前仅支持默认 / `ReadCommitted` 边界，不提供 MVCC、可重复读、序列化隔离或跨数据库事务。
- **PR #112 表表 JOIN / 子查询 / 聚合能力补齐**：关系表 SELECT 新增多表 `INNER JOIN` 执行路径，支持连续表表 JOIN、`FROM (SELECT ...) alias` / `JOIN (SELECT ...) alias` 派生表、WHERE 中标量子查询，以及关系表 `GROUP BY` 搭配 `count` / `sum` / `min` / `max` / `avg` 聚合；保留既有 measurement JOIN 维表路径不变。
- **PR #109 IoTSharp 兼容矩阵与基线套件**：新增 `docs/iotsharp-compat-matrix.md`，梳理 IoTSharp 当前 PostgreSQL/MySQL/SQLServer/SQLite/Oracle/Cassandra/ClickHouse、InfluxDB/TimescaleDB/Taos/IoTDB/SonnetDB、Redis/LiteDB/InMemory、BlobStorage/S3 以及向量搜索、全文搜索的兼容状态；新增 `tests/SonnetDB.IoTSharpCompat.Tests` 占位基线套件，固定关系、时序、缓存、对象桶、向量搜索、全文搜索验收用例和迁移/双写/回滚清单。
- **MM8 Hybrid Search 第二批**：`hybrid_search(...)` 新增 measurement KNN 与 document collection 知识条目关联融合模式，支持 `source => measurement`、`documents => collection`、`measurement_join_tag`、`document_join_path`、可选 `document_join_index`、知识文档全文 BM25 和可选知识向量分数；TVF 后可继续 `JOIN` 关系维表，统一 planner 会把 measurement `time` / tag 谓词下推给 KNN、把 table 侧谓词下推给主键 / 二级索引候选行并用命中的 JOIN key 收窄 measurement join tag；剩余谓词可过滤知识文档字段或融合分数，`EXPLAIN` 返回 `access_path=hybrid_search_measurement_knn_documents`，带维表过滤时追加 `relation_filter:<table_access_path>`。
- **MM9.5 / C5.6 多模型管理面与索引生命周期第二批**：`GET /v1/db/{db}/schema` 在保持旧 `measurements` 字段兼容的同时新增 `tables`、`documentCollections`、`indexes` 和 `backupStatus`，可展示关系表、JSON 文档集合、JSON path / fulltext / vector 索引生命周期和当前备份状态；新增 `POST /v1/db/{db}/maintenance`，支持 `health_check`、`rebuild_index`、`quality_analysis`、`backup_verify`、`restore_dry_run`。其中 table secondary / table JSON path index 与 document JSON path index 会同步从主数据重建，document fulltext index 会触发同步补建 / touch，measurement vector index 返回 Segment 生命周期下的 planned rebuild 状态；`quality_analysis` 汇总索引生命周期、可重建性、planned vector 状态和空全文集合警告；`backup_verify` 和 `restore_dry_run` 仅 server admin 可调用，避免普通数据库 token 探测宿主文件系统路径。
- **MM9 统一备份、恢复和管理工具第一批**：`SonnetDB.Core` 新增 `BackupService` 与 `sonnetdb.backup.json` manifest，支持多模型一致目录备份、逐文件 SHA-256 校验、manifest inspect 和离线恢复到新目录；`sndb backup create/inspect/verify/dry-run/restore/rebuild-indexes` 提供本地管理入口，创建备份时使用维护模式打开数据库并禁用后台 worker。manifest 记录 measurement/table/keyspace/document 摘要，以及 table secondary / table JSON path、document fulltext/json path、measurement vector 索引的 included / rebuildable 生命周期；restore dry-run 统一校验 manifest、SHA-256 和目标目录策略，`--rebuild-indexes` 可在恢复后同步补建 table / document JSON path / document fulltext 索引，measurement vector 索引继续返回 Segment 生命周期 planned 状态；定时、增量、在线恢复、后台索引 rebuild 队列和 UI 编排留后续批次。
- **MM8 Hybrid Search 第一批**：SQL 新增 `hybrid_search(...)` document collection 表值函数，并支持函数命名参数语法 `source => docs`；第一批在文档集合内融合 DotSearch BM25 与 JSON embedding 数组向量距离，提供 `bm25_score()`、`vector_distance()`、`vector_score()`、`hybrid_score()` 伪列，支持 `text_index` / `text_field` / `vector_field` / `metric` / `text_weight` / `vector_weight` 参数、JSON path 过滤、按 alias 排序和 `EXPLAIN access_path=hybrid_search`。measurement `knn(...)` 与关系维表 JOIN 的跨模型融合留后续批次。
- **MM6 DotSearch 全文索引集成第一批**：`SonnetDB.Core` 新增 `FullText` adapter，条件引用根工作区 `modules/DotSearch` 的 Core、Unicode、CJK 和 Jieba 分词器项目；文档集合 schema / codec 新增全文索引声明，store 在 `documents/fulltext/` 下维护 DotSearch 派生索引并随 `INSERT` / `UPDATE` / `DELETE` 同步更新。SQL 新增 `CREATE FULLTEXT INDEX` / `DROP FULLTEXT INDEX` / `SHOW FULLTEXT INDEXES`，文档集合查询支持 `match(index, field, query[, topK])` 候选集、`bm25_score()` 投影 / 排序和 `EXPLAIN` 的 `fulltext_index` 访问路径；第一批限定为 document collection 全文搜索，后续已由 MM8 第一批接上文档集合 Hybrid Search。
- **MM5 JSON 文档能力第一批**：`SonnetDB.Core` 新增 `Documents` 模块与 `Tsdb.Documents` 入口，基于 KV-backed storage 在 `documents/` 目录实现 document collection 主数据、schema 持久化和可重建 JSON path 等值索引；SQL 新增 `CREATE DOCUMENT COLLECTION`、`DROP DOCUMENT COLLECTION`、`SHOW DOCUMENT COLLECTIONS`、`DESCRIBE DOCUMENT COLLECTION`、`CREATE JSON INDEX` / `DROP JSON INDEX` / `SHOW JSON INDEXES`，文档集合支持 `INSERT` / `SELECT` / `UPDATE` / `DELETE`、`id` 主键读取、`json_value(document, '$.path')` path 投影 / 过滤和 `EXPLAIN` 的 `document_id` / `json_path_index` / `document_scan` 访问路径。关系表 `JSON` 列同步支持 `json_value(json_col, '$.path')`，与 document collection 复用同一套 JSON path evaluator；第一批暂不提供 JSON 虚拟表、JSON 文件导入、跨文档复杂事务或关系表 JSON path 索引。
- **MM4 时序 JOIN 关系维表**：SQL parser / executor 新增 `measurement JOIN table ON measurement.tag = table.column` 第一版，支持 `JOIN` / `INNER JOIN`、双侧别名与限定列名、measurement tag/time 过滤下推、关系表主键 / 二级索引候选行下推、小维表 hash join、JOIN 结果 `ORDER BY` / `LIMIT`；当前限定为一个 measurement 加一个关系表的 inner 等值 JOIN，measurement 侧连接键必须是 TAG 列，暂不支持多表 JOIN、outer join、聚合 / GROUP BY / 窗口函数 JOIN。
- **MM1 内置 KV Keyspace**：`SonnetDB.Core` 新增独立 `Kv` 模块，`Tsdb.Keyspaces.Open(name)` 可打开轻量 keyspace，支持 `Put` / `Get` / `Delete` / `ScanPrefix`、独立 KV WAL、崩溃恢复、快照和 compaction 段文件；不修改现有时序 `.SDBWAL` / `.SDBSEG` 二进制格式。
- **MM2 关系表 MVP**：`SonnetDB.Core` 新增 `Tables` 模块与 `Tsdb.Tables` 入口，基于 MM1 `KvKeyspace` 在 `tables/` 目录实现关系表 rowstore；SQL 支持 `CREATE TABLE ... PRIMARY KEY (...)`、`DROP TABLE`、`INSERT`、`SELECT`、`UPDATE`、`DELETE`、`SHOW TABLES` 与 `DESCRIBE TABLE`，覆盖 `INT` / `FLOAT` / `BOOL` / `STRING` / `DATETIME` / `BLOB` / `JSON` 基础类型、主键查找、NOT NULL 校验和 ADO.NET BLOB 读取；不修改现有时序 `.SDBWAL` / `.SDBSEG` 二进制格式。
- **MM3 二级索引、约束和轻事务**：关系表新增 `CREATE [UNIQUE] INDEX`、`DROP INDEX ... ON`、`SHOW INDEXES ON`、索引 schema 持久化、普通 / 唯一索引 rowstore 维护和 rebuild；`SELECT` / `UPDATE` / `DELETE` 在索引列完整等值谓词下可走二级索引候选行，`EXPLAIN` 返回 `access_path` / `index_name`；`SqlExecutor.ExecuteScript` 和服务端 `/sql/batch` 支持 `BEGIN [TRANSACTION]`、`COMMIT`、`ROLLBACK` 单表小批量 DML 轻事务，提交失败时回滚已应用 rowstore / index 变更。
- **向量索引 adapter 层**：`src/SonnetDB.Core/Storage/Segments` 新增内部 `IVectorIndexBuilder` / `IVectorIndexReader` adapter，先通过 legacy HNSW wrapper 包住现有 `HnswVectorBlockIndex`；`SegmentWriter` 构建向量索引改走 builder，`KnnExecutor` 的 ANN 查询改走 reader，保持现有 `.SDBSEG` embedded extension / legacy `.SDBVIDX` 格式和查询行为不变，为后续接入 DotVector.Indexing 预留边界。
- **Prometheus Remote Write 兼容入站端点**：`src/SonnetDB` 新增 `POST /api/v1/prom/write?db=<name>`，请求体为 `snappy(block) + protobuf(prometheus.WriteRequest)`，让 Prometheus / VictoriaMetrics agent / Grafana Alloy / OpenTelemetry Collector 可以无需改 URL 直接把指标写入 SonnetDB。映射规则：`__name__` label → `measurement`，其余 label → `tags`，每条 `Sample(value:double, ts:int64 ms)` 展开为一个 `Point` 的 `value:double` field；NaN 样本（Prometheus stale marker）与名称含保留字符（`, = \n \r \t "`）的 series 静默跳过；snappy / protobuf 解码失败返回 `400`，否则返回 `204 No Content`（Prometheus 协议约定）。仅 server 层引入新依赖 `Snappier 1.3.1`（纯托管 Snappy 块格式解压，AOT 友好）；protobuf 解码采用手写最小 decoder（仅识别 WriteRequest/TimeSeries/Label/Sample，未知 wire 字段安全跳过），不引入 `Google.Protobuf` / `Grpc.Tools`，不改动 `src/SonnetDB.Core`。新增 13 个端到端测试覆盖 happy path + SQL 回查、多 series、NaN 跳过、缺失 `__name__`、保留字符、空 body、bad snappy / bad protobuf、缺/未知 db、ReadOnly 403、未认证 401、JSON 错误体。
- **InfluxDB 兼容写入端点**：`src/SonnetDB` 新增 `POST /write`（v1：`?db=&precision=`）与 `POST /api/v2/write`（v2：`?bucket=&org=&precision=`）两个 InfluxDB Line Protocol 兼容端点，复用现有 `LineProtocolReader` + `BulkIngestor`，让 Telegraf / EMQX / `influx` CLI / Prometheus `influxdb_v2` remote write 等生态工具可直接对接 SonnetDB；与既有 `/v1/db/{db}/measurements/{m}/lp` 的关键差别是 `measurement` 由每行 LP 自行解析（`measurementOverride: null`），符合 InfluxDB 协议语义。支持 `Content-Encoding: gzip` 请求体、`precision` 别名（`n`/`ns`、`u`/`us`/`µs`、`ms`、`s`，缺省 ns），成功返回 `204 No Content`，错误返回 `{ "error", "message" }` JSON；`BearerAuthMiddleware` 同步增加 `Authorization: Token <token>` 别名，与 `Authorization: Bearer <token>` 等价，以兼容 InfluxDB v2 客户端约定。新增 19 个端到端测试覆盖 v1/v2、precision 全别名、gzip、Token 头、measurement 解析、204、400/401/403/404 与 JSON 错误体。
- **地理坐标系转换与国内瓦片切换**：Web Admin 的轨迹地图和 SQL Console 地图视图现支持 OSM / 高德 / 腾讯 / 百度瓦片下拉切换；SQL 内置 `geo_transform` 以及 `geo_wgs84_to_gcj02`、`geo_gcj02_to_wgs84`、`geo_gcj02_to_bd09`、`geo_bd09_to_gcj02`、`geo_wgs84_to_bd09`、`geo_bd09_to_wgs84` 等坐标系转换函数，并在结果区按当前底图投影自动重投影 `GEOPOINT`。
- **数据库创建独立对话框**：`web/src/views/SqlConsoleView.vue` 的 Create Database 动作改为先弹出独立对话框，再输入名称并确认创建，避免在侧边栏中直接编辑数据库名。
- **SQL Console 结果区三视图升级**：`web/src/views/SqlConsoleView.vue` 现直接复用 `SqlResultPanel.vue` 作为结果展示卡片，结果区可在表格 / 图表 / 轨迹地图之间切换；`SqlResultChart.vue` 继续提供时间轴和值轴下拉选择，带明显时间列和数值列的结果会优先默认进入图表视图。
- **SonnetDB Workbench 首版**：Web Admin 的 `/admin/app/sql` 现已升级为 Workbench，按 Schema Explorer / SQL Editor / Staged Preview / Result Grid 组织布局；继续复用 `GET /v1/db`、`GET /v1/db/{db}/schema`、`POST /v1/db/{db}/sql` 与现有 Copilot stream 协议。写操作会先进入 staged preview，`DELETE` / `DROP` / `GRANT` / `REVOKE` / `USER` / `TOKEN` 类危险操作需要用户勾选确认；Copilot 仍保持右下角全局浮窗，不新增工作台内专栏。
- **SQL `EXPLAIN` 落地**：`POST /v1/db/{db}/sql` 现在支持 `EXPLAIN SELECT`、`EXPLAIN SHOW MEASUREMENTS` / `SHOW TABLES` 与 `EXPLAIN DESCRIBE [MEASUREMENT]`，返回 `key` / `value` 结果行而不是白页；同一套只读估算逻辑也复用到 MCP `explain_sql`。
- **WHERE time now()/duration 求值**：`WHERE time` 现在支持 Unix 毫秒整数字面量、duration 字面量以及 `now()` 参与的算术表达式，`SELECT`、`DELETE` 与 `explain_sql` 共享同一求值路径，因此 `time >= now() - 1d`、`time < now() + 1d` 这类查询可以直接执行。
- **云端 Copilot 直连桥接**：SonnetDB Web 端的 `/v1/copilot/chat` 与 `/v1/copilot/chat/stream` 现已改为只走 `https://ai.sonnetdb.com` 官方云端 Copilot Runtime，本地不再提供知识库 / 技能库 / 本地模型兜底；本地服务仅负责上下文摘要、数据库权限校验、受确认约束的工具执行与 tool result 回传。
- **sonnetdb.com 账号绑定与唯一 AI Gateway**：Web Admin「Copilot 设置」改为设备码绑定 sonnetdb.com 账号，绑定成功后本地 `.system/ai-config.json` 仅保存 Cloud Access Token / Refresh Token；OSS 端固定通过 `https://ai.sonnetdb.com` 调用平台 AI Gateway，不再支持国内 / 国外节点选择、手动 API Key、`SonnetDBServer_Ai_Example` 或本地模型选择。平台模型列表改为绑定后从 `ai.sonnetdb.com/v1/models` 读取，聊天请求自动使用平台返回的默认模型。
- **GitHub 协作模板与治理规范**：新增 `.github/ISSUE_TEMPLATE/`（bug/feature/task + config）、`.github/pull_request_template.md`，并新增 `.github/project-management.md` 统一 Milestone / Label 命名、颜色与 Issue 生命周期流转规则。
- **WAL LastLsn footer 元数据**：新 WAL segment 会在记录区后追加 32 字节 LastLsn footer，`WalWriter.Open` 优先通过 footer 快速恢复 `NextLsn`，旧 WAL、损坏 footer 与截断尾部会自动回退到顺序扫描并重写 footer；`WalRecordHeader` 与既有 WAL 记录格式保持不变，旧 WAL 继续可读。
- **WAL group-commit**：`SyncWalOnEveryWrite=true` 时新增可配置的 `WalGroupCommitOptions`（默认 2 ms 窗口），多个并发 `Write` / `WriteMany` / `Delete` 请求会在写入 WAL 后共享一次 `Flush(true)`，写请求仍会等待该批 fsync 完成后返回；WAL record/header 二进制格式不变，旧 WAL 继续可读。新增 WAL group-commit 崩溃恢复、`WriteMany` 批量写入、并发写入测试与 `WalGroupCommitBenchmark` 基准。
- **WAL 小记录写入优化**：`WalWriter.AppendRecord` 现在会将 `WalRecordHeader` 与 payload 合并到同一块 `stackalloc` / `ArrayPool` 缓冲后尽量单次 `Stream.Write`，保留原有 CRC32 payload 校验与 WAL 二进制布局不变；补充小/大 payload round-trip、CRC 损坏和截断容忍回归测试。
- **WAL 写缓冲策略评估**：新增 `WalBufferingBenchmark` 对比 `BufferedStream(FileStream)` 与 `FileStream + self buffer`；本机 microbenchmark 显示 BCL `BufferedStream` 在 200 万条 WAL-like 小记录写入中吞吐更优（约 91.15 ms vs 96.94 ms），因此生产路径继续保留 `BufferedStream`，`Sync()` 的 `Flush(true)` 持久化语义不变。
- 新增独立 `OPTIMIZATION_ROADMAP.md`，用于跟踪 `src/SonnetDB.Core` 核心库性能与可靠性优化路线，覆盖写入吞吐、MemTable 热路径、查询索引与缓存、窗口函数、崩溃恢复和现代 C# / Analyzer 六个阶段，并为每项任务提供状态标记、执行顺序、验收标准与可复用提示词。
- **Copilot Agent 提示词增强**：参考 VS Code Copilot 的行动型助手原则，强化 SonnetDB Copilot 的身份边界、工具优先、上下文事实校验、安全/版权边界、模型回答规则与 SQL 方言纠错约束，避免冒充外部产品或编造数据库结构。
- **SQL DDL 兼容修饰符（PR 4）**：lexer 新增 `DEFAULT` 关键字；`CREATE MEASUREMENT` parser 接受列级 `NULL` / `NOT NULL` 与 `DEFAULT <expr>` 并在 AST 中保留；执行层对 `DEFAULT` 返回明确 unsupported，`NULL` / `NOT NULL` 保持兼容性 no-op，并在 SQL 文档中说明 SonnetDB 的稀疏字段语义。
- **SQL 单表别名与限定列名（PR 3）**：SQL lexer 新增 `.` token；parser 支持 `FROM measurement [AS] alias` 与 `alias.column` / `alias."Column"` 列引用；执行器在查询执行前校验限定符必须匹配当前单表别名。
- **SQL `ORDER BY time`（PR 2）**：SQL lexer 新增 `ORDER` / `ASC` 关键字识别（`DESC` 复用已有 `DESC` token），`SELECT` AST 增加 `OrderBySpec`，parser 支持 `ORDER BY time [ASC|DESC]`，执行器会在 `LIMIT/OFFSET/FETCH` 前按结果集中的 `time` 列排序，并同步修正分页文档示例。
- **SQL 兼容性基础（PR 1）**：`SELECT` 现在支持常见探活写法 `SELECT 1 ... LIMIT 1` 的字面量投影；聚合函数 `count` 额外兼容 `count(1)`，语义等同于 `count(*)`，方便 Copilot / ORM 生成 SQL 直接执行。
- 新增 `connectors/` 连接器目录，预留 C / Go / Rust / Java / ODBC 连接器；首个 C 连接器通过 .NET Native AOT 将 `SonnetDB.Core` 发布为原生共享库，并导出 open / close / execute / result cursor / flush / last_error 等 C ABI 函数，附带 `sonnetdb.h`、C quickstart 示例与 CMake 构建入口（Windows x64/x86/ARM64、Linux x64）。
- 新增 Java 连接器第一版：提供 Java 8 兼容的默认 JNI 后端与 JDK 21+ 可选 FFM 后端，基于 C ABI 暴露 `SonnetDbConnection` / `SonnetDbResult` / `SonnetDbValueType` / `SonnetDbException`，支持打开嵌入式库、执行 SQL、读取 typed result cursor、Flush 与版本查询，并提供 CMake 构建入口和 quickstart 示例。
- 新增 Go 与 Rust 连接器第一版：Go 连接器基于 cgo 包装 SonnetDB C ABI，提供 `sonnetdb.Open` / typed result cursor / `database/sql` driver 与 quickstart；Rust 连接器提供手写 FFI、`Connection` / `ResultSet` 安全封装、typed getter、版本/错误读取与 quickstart，二者均复用既有 Native AOT `SonnetDB.Native` 共享库。
- 新增 Python 连接器第一版：基于标准库 `ctypes` 直接加载 SonnetDB C ABI，提供 `sonnetdb.connect`、`Connection.execute` / `execute_non_query`、typed result cursor、轻量 DB-API-style `cursor()`、quickstart 与 unittest smoke，运行时不引入第三方依赖。
- 新增 Visual Basic 6 与 PureBasic 连接器第一版：VB6 连接器提供 x86 stdcall 桥接 DLL 源码、`.bas` / `.cls` 封装与 quickstart，解决 VB6 stdcall 与 SonnetDB C ABI cdecl 的调用约定差异；PureBasic 连接器提供 `SonnetDB.pbi` 动态加载封装与 quickstart。二者因托管 CI 缺少授权语言工具链，不在 GitHub Actions 中构建各自语言产出的动态库。
- 新增连接器专用发布 CI：`.github/workflows/connectors-release.yml` 在非 tag 事件只编译连接器，在 `vX.Y.Z` tag 上使用 C# 工具 `eng/tools/connectors-release` 为各连接器按 RID 生成独立 zip 包，并把所有 zip 上传到对应 GitHub Release；每个包包含连接器产物、Native runtime、示例、启动脚本与说明文档。
- **写入路径支持受控 schema-on-write**：`Tsdb.Write/WriteMany` 现在会在写 WAL / MemTable 前自动创建或扩展 measurement schema，并先持久化 `measurements.tslschema`，覆盖 SQL `INSERT`、Line Protocol、JSON points 与 Bulk VALUES；缺失 TAG / FIELD 自动补齐，已有 `INT` 字段遇到 `FLOAT` 值会提升为 `FLOAT`，已有 `FLOAT` 字段接收整数时会转换为浮点保存，其它类型漂移仍拒绝。
- 新增 `eng/build-windows.ps1`，一键完成 Windows `win-x64` Release 构建、NuGet 打包、ZIP Bundle 与 MSI 输出，并把最终可发布文件汇总到 `artifacts/windows/final/` 后清理中间产物；同步修正发布脚本 NuGet 清单为 `SonnetDB.Core` / `SonnetDB` / `SonnetDB.Cli`，让服务端 publish 正确尊重 `BuildAdminUi` 开关，并让 Windows MSI 安装 `SonnetDB` 服务、通过 `DATAROOT` 指定数据目录、把 `sndb` 加入系统 `PATH`。
- **Apache IoTDB / PostgreSQL TimescaleDB 服务端基准**：`tests/SonnetDB.Benchmarks` 新增 IoTDB REST v2 `insertTablet` / SQL 查询 / `GROUP BY` 时间窗口基准，以及 TimescaleDB hypertable + binary COPY / range query / `time_bucket` 基准；Docker benchmark 环境、启动脚本、README 与 `docs/blogs/111-113` 对比通稿同步补齐实测数据，并统一 benchmark 文档中的时间单位为 ms、数据大小单位为 MB。
- **LiteDB 嵌入式基准**：`tests/SonnetDB.Benchmarks` 新增 LiteDB 5.0.21 对照，覆盖 100 万点 `InsertBulk` 写入、`Ts` 索引范围查询与 1 分钟桶文档顺扫聚合，并在 benchmark 文档与对比通稿中补充实测数据。
- **Benchmark 服务端端口可配置**：`tests/SonnetDB.Benchmarks/docker/docker-compose.yml` 新增 `SONNETDB_BENCH_PORT` 宿主机端口覆盖，`ServerBenchmark` 新增 `SONNETDB_BENCH_URL` 覆盖，并修复 SonnetDB / TDengine compose 健康检查，方便在本机已有 SonnetDB 开发容器占用 `5080` 时隔离运行基准。
- **PID 控制函数基准**：新增 `PidBenchmark`，覆盖 50k 阶跃响应数据上的 `pid_series`、`pid(...) GROUP BY time(1m)`、`pid_estimate(..., 'zn', ...)` 与 `pid_estimate(..., 'imc', ...)`，用于回归工业控制函数端到端 SQL 性能。
- **PR #70 — GEOPOINT 数据类型 + 编解码**：新增 `FieldType.GeoPoint = 6`、`GeoPoint` / `FieldValue.FromGeoPoint`，Segment 格式升级到 v4 并支持 `BlockEncoding.GeoPointRaw`（lat/lon 各 8 字节 little-endian），保留 v3 只读兼容；WAL、Segment、SQL `GEOPOINT` 列声明、`POINT(lat, lon)` 字面量、ADO.NET `GeoPoint` 参数化与 `lat(field)` / `lon(field)` 标量提取函数已接通。
- **CopilotDock 页面感知快捷能力**：全局 Copilot 仍保持伴随式聊天入口，但主界面收起模型选择与知识库状态到「选项」弹层；根据当前页面自动展示快捷动作，SQL Console 页面提供「生成 SQL / 修复 SQL / 解释 SQL / 优化 SQL」，其它页面提供结构梳理、事件排查、权限检查、配置检查等上下文能力。
- **PR #71 — 地理空间标量函数（Tier 1）**：新增 `geo_distance` / `geo_bearing` / `geo_within` / `geo_bbox` / `geo_speed`，基于 Haversine 计算距离、方位角、圆形围栏、矩形围栏与速度，并注册 `ST_Distance` / `ST_Within` / `ST_DWithin` PostGIS 兼容别名。
- **PR #72 — 轨迹聚合函数（Tier 2）**：新增 `trajectory_length` / `trajectory_centroid` / `trajectory_bbox` 与 `trajectory_speed_max` / `trajectory_speed_avg` / `trajectory_speed_p95`，支持 `GEOPOINT` 轨迹总路程、重心、外包矩形与基于相邻点时间差的速度统计，并接入 `GROUP BY time(...)`。
- **PR #73 — GeoJSON 序列化 + REST 端点扩展**：`GEOPOINT` 查询结果 ndjson 自动输出 GeoJSON Point（`[lon,lat]`），新增轨迹 REST 端点 `GET /v1/db/{db}/geo/{measurement}/trajectory`，支持 Point FeatureCollection 与 `format=linestring`；远程 ADO.NET `DbDataReader` 现在会把 GeoJSON Point 反序列化回 `GeoPoint` struct。
- **PR #74 — Web Admin 轨迹地图标签页**：新增 Vue3 TrajectoryMap.vue，引入 maplibre-gl 与 echarts，支持按数据库 / Measurement / GEOPOINT 字段 / TAG / 时间范围加载轨迹端点，展示 OSM 底图、LineString 轨迹、起终点标记、时间轴回放与速度折线图。
- **PR #75 — SQL 控制台地图渲染集成**：查询结果自动检测 GeoJSON Point / GeoPoint 列并显示地图视图，SqlResultPanel 支持文本 / 表格 / 图表 / 地图切换；ResultMapPreview.vue 支持散点、按时间排序轨迹连线与低基数列分组，多点结果可直接在 SQL Console 预览。
- **PR #76 — 地理空间索引（Geohash 段内过滤）**：Segment 格式升级到 v5，`BlockHeader` 新增 `GeoHashMin` / `GeoHashMax` 32-bit geohash 前缀；`SegmentWriter` 为 GEOPOINT Block 写入空间范围，`QueryEngine` 在 `geo_within` / `geo_bbox` WHERE 谓词下对落盘 block 做 geohash 剪枝，同时保留 v4 段文件只读兼容。
- **PR #77 — 地理空间基准 + 文档完善**：新增 `GeoQueryBenchmark`，覆盖 `100k` 默认轨迹点和可选 `1M` 档位下的 `geo_within`、`geo_bbox`、`trajectory_length` 与 `GEOPOINT` range scan；README 与 `docs/geo-spatial.md` 补齐地理空间功能矩阵、Web Admin / SQL Console 地图用法、基准运行方式和车辆追踪 / 户外运动 / IoT 地理围栏端到端示例。

### Changed
- **NuGet / Actions dependency refresh**：按 NuGet latest stable 与当前 GitHub dependency PR 批量升级中央包版本，覆盖 .NET 10.0.9 系列、`Microsoft.Extensions.AI 10.7.0`、`ModelContextProtocol 1.4.0`、`Microsoft.ML.OnnxRuntime 1.27.0`、`Microsoft.NET.Test.Sdk 18.7.0`、`Testcontainers 4.12.0`、`coverlet.collector 10.0.1`、`AWSSDK.S3 4.0.25.3`、`NATS.Client 2.8.2`、`StackExchange.Redis 3.0.7`、`MeiliSearch 0.20.0`、`InfluxDB.Client 5.1.0`、`Npgsql 10.0.3` 等；GitHub workflows 的 `actions/checkout` 同步升级到 `v7`。
- **门面文档叙事收敛**：根 README / README.en 改为“一句话定位 + 能力矩阵 + 使用方式 + 专题文档索引”的简洁结构，剔除长 SQL 函数表、架构细节和基准明细，突出时序、关系、KV、文档、全文、向量、对象存储、消息队列、Copilot、Workbench、MCP 和多语言连接器；文档首页、Workbench 文档和 Web 欢迎页同步从单一时序数据库定位升级为多模型数据底座定位。
- NuGet 发布链路新增 `SonnetDB.EntityFrameworkCore`：EF Core Provider 现在会随 `eng/release.ps1 -Tasks nuget`、GitHub Publish workflow、SDK Bundle 和发布文档一起输出。
- 文档统一修正 ADO.NET 提供程序的 NuGet 包名：`src/SonnetDB.Data` 发布为 `SonnetDB`，代码命名空间仍为 `SonnetDB.Data`；根 README、文档首页、综合指南、包说明和产品首页同步收敛当前能力说明。
- **Block 级跳跃索引（MaxTimestamp 前缀最大值）**：`SegmentIndex` 在 `Build` / `BuildFromBlocksForTesting` 时为每个 `(seriesId, fieldName)` 桶与每个 `seriesId` 桶预计算 `MaxTimestamp` 前缀最大值数组。`GetBlocks(seriesId, fieldName, from, toInclusive)` 中原来"二分上界 + 线性扫描下界 (`MaxTimestamp >= from`)"的实现改为"二分上界 + 在前缀最大值上二分下界 + 仅在 `[lower, upper)` 区间补一次微扫描"，渐近复杂度由 O(upper) 改善为 O(log n + 命中桶数)；新增 `GetBlocks(seriesId, from, toInclusive)` 多 field 单 series 时间窗剪枝重载，`MultiSegmentIndex.LookupCandidates(seriesId, from, to)` 改为转发该重载。压缩产生的重叠 block（`MaxTimestamp` 非单调）下结果集与朴素实现一致。新增 `SegmentIndexSkippingTests` 12 个场景：非重叠窄/宽窗、查询完全前/后于所有 block、边界等值（含单点查询）、压缩重叠场景两例、按 series 多 field 路径、未知 series/field 空返回、64 个随机 block × 300 次随机查询的 fuzz 与朴素 O(n) 实现对拍；`Storage/Segments/SegmentIndex.cs`、`Storage/Segments/MultiSegmentIndex.cs` 不修改文件二进制格式，无新增依赖。
- **Codec/BlockDecoder V2 专用化快路径**：评估 source generator、静态泛型与手写 fast path 后，选择零依赖、safe-only 的手写专用化；`TimestampCodec` 可直接把 delta-of-delta 时间戳写入 `DataPoint` 目标视图，`ValuePayloadCodecV2` 新增全量与范围 `DecodeInto` 路径，`BlockDecoder` 避免 V2 全量/范围解码中的中间 `long[]` / `FieldValue[]` 分配。新增 `CodecSpecializationBenchmark`，覆盖旧组合路径与生产快路径对比，并补充 V2 range 语义回归测试。
- **SonnetDB.Core 性能 analyzer 配置**：为核心库启用低噪声性能规则，覆盖热路径 LINQ、重复数组分配、Count/Any、Dictionary 查询、SearchValues 与字符串比较相关建议；高语义取舍规则先以 suggestion 暴露，并修复新增 warning 命中的保留字符搜索与 SQL 元数据列名数组分配点，保持 `TreatWarningsAsErrors` 通过且不新增 suppress。
- **Options/config 值对象化**：`TsdbOptions`、flush/WAL/segment/compaction/retention/background/pid 等配置类型改为 `sealed record`，继续保留 `new Options { ... }` 对象初始化器与 init-only 属性，同时支持 `with` 生成新配置快照，降低运行时共享配置被意外修改带来的并发不确定性。补充对象初始化器兼容、`with` 快照与值语义测试。
- **SQL Lexer 字符分类快路径**：`SqlLexer` 的空白、标识符、数字、duration 后缀与运算符起始字符判断改为基于 `SearchValues<char>` 的 ASCII 快路径，并保留 Unicode 标识符/空白 fallback；补充 `SqlLexerBenchmark` 对比旧分支分类 lexer 与新实现。
- **Catalog/schema/tag index 冻结快照**：`SeriesCatalog`、`MeasurementCatalog` 与 `TagInvertedIndex` 改为写入路径维护 mutable builder 并原子发布 `FrozenDictionary` / `FrozenSet` 快照，`MeasurementSchema` 的列名索引也改为构造后冻结；读路径无锁查询已发布快照，保持并发 `GetOrAdd` 幂等与 tag 查询语义不变。补充查找正确性、更新后可见性与并发读写回归测试。
- **Segment v6 主格式整合**：Segment 写入版本升级到 v6，新段把原先为保持 v5 而外置的 HNSW `.SDBVIDX` 与扩展聚合 `.SDBAIDX` 内容内嵌为 `.SDBSEG` 索引区之后、Footer 之前的 extension section；`SegmentHeader` 保留区新增 mini-footer 摘要副本（IndexCount / IndexOffset / FileLength / IndexCrc32），用于尾部损坏时的诊断与受控 fallback。读取层继续兼容 v4/v5，并保留旧 sidecar 按需读取回退。
- **WalSegmentSet checkpoint replay 单遍化**：`ReplayWithCheckpoint` 改为单次扫描 WAL records，并利用 segment `LastLsn` 元数据或相邻 segment 起始 LSN 推导结果跳过 durable checkpoint 之前的整段；旧 WAL、无 footer WAL 与 legacy `active.SDBWAL` 升级路径继续兼容，补充多段、checkpoint 命中和 legacy 升级回归测试。
- **Tombstone manifest 周期性 checkpoint**：新增 `TombstoneCheckpointOptions`，Delete 路径可按累计删除数或时间间隔把当前 tombstone 快照持久化到 manifest；恢复时以 manifest 最大 `CreatedLsn` 作为 Delete replay 下界并对 tombstone 去重，降低大量删除后崩溃恢复对 WAL Delete 全量重放的依赖，同时保持 Delete 过滤与 Compaction 消化语义不变。
- **Checkpoint LSN 持久化语义增强**：Flush 成功写出 Segment 并 Sync WAL checkpoint 后，会把 durable checkpoint LSN 以 tmp 写入、fsync、原子 rename、父目录 best-effort fsync 的方式保存到 WAL 元数据文件；恢复时仅当对应 Segment 文件存在且长度匹配才采用该 checkpoint，否则忽略并完整 replay WAL，避免坏中间状态错误跳过未 flush 数据。
- **WAL record torn-write 检测增强**：新写入的 WAL record 在不改变 32 字节 header 尺寸的前提下启用 header checksum，`WalReader.Replay` 与 `WalWriter.Open` 扫描旧 WAL 时会在第一条 header/payload/CRC 损坏记录处停止并忽略尾部；`Flags=0/Reserved=0` 的旧 WAL record 继续按原格式读取。
- **数值聚合 SIMD 快路径**：新增可选 `TsdbOptions.UseSimdNumericAggregates`（默认开启），`sum` / `min` / `max` / `count` 在适合的落盘数值 block 部分范围聚合上可使用 `System.Numerics.Vector<T>` 处理 Float64 / Int64 payload；不支持硬件加速、遇到 NaN、Boolean 或不适合的编码时自动回退标量路径，保持查询结果语义不变，并补充 scalar/SIMD 一致性测试与 `NumericAggregateSimdBenchmark` BenchmarkDotNet 对比。
- **扩展聚合 block sketch 快路径**：为数值 block 写入 TDigest 与 HyperLogLog sketch，`percentile` / `p50` / `p90` / `p95` / `p99` / `tdigest_agg` / `distinct_count` 在全局聚合且无 tombstone/geo 过滤时可按 block 合并 sketch；v6 新段将 sketch section 内嵌到 `.SDBSEG`，旧 `.SDBAIDX` sidecar 仍可按需读取回退，损坏或缺失时自动回退旧解码扫描路径，并补充内嵌读取、legacy sidecar 回退与 SQL 快路径测试。
- **窗口函数流式状态接口**：新增 `IWindowState` / `IWindowStreamingEvaluator`，流式窗口函数可通过 `Update(timestamp, value)` 按行推进；`DoubleWindowEvaluatorBase` 保留旧 `Compute` / `ComputeDouble` 适配层，`SelectExecutor` 在窗口 evaluator 全部支持流式状态时跳过 `long[]` / `FieldValue?[]` 对齐数组和预计算输出，仍对未迁移函数自动回退旧 materialized 路径。补充流式状态语义与大数据集内存占用回归测试。
- **窗口函数 Span 批量路径**：`moving_average`、`running_sum` 及新增 `running_min` / `running_max` 的 typed/boxed 批量计算改为基于 `ReadOnlySpan` / `Span` 单遍填充输出；`moving_average` 小窗口使用栈上环形缓冲，减少临时数组，同时保持前 `n-1` 行 NULL、缺失值跳过/延续和时间顺序语义不变。补充空输入、全缺失、窗口大小 1 与累计极值边界测试。
- **窗口函数 typed evaluator**：新增 `IWindowDoubleEvaluator` / `WindowDoubleOutput` 数值窗口函数输出接口，`SelectExecutor` 对 typed evaluator 优先复用 `double[] + bool[]`，避免 `Compute` 先生成 `object?[]` 导致每行提前装箱；`moving_average`、`ewma`、`holt_winters` 与累计求和路径已迁移，并新增 `running_sum` 作为 `cumulative_sum` 兼容别名。补充 typed 语义测试、SQL 兼容测试与 BenchmarkDotNet 分配基准。
- **SegmentReader 可选 memory-mapped 读取路径**：新增 `SegmentReaderOptions.UseMemoryMappedFileForLargeSegments` 与 `MemoryMappedFileThresholdBytes`，大段文件可通过 safe-only `MemoryMappedViewAccessor` 按需读取 header/index/block payload，避免默认 `File.ReadAllBytes` 把整段放入 LOH；默认仍保留 `byte[]` reader，mmap 打开失败会回退。补充默认回退、阈值回退、mmap 解码与 Dispose 释放文件句柄测试。
- **SegmentReader HNSW vector 索引懒加载**：`SegmentReader.Open` 不再 eager 反序列化 HNSW 图，改为 `TryGetVectorIndex` 首次命中 VECTOR block 时从 v6 内嵌 section 或旧 `.SDBVIDX` sidecar 按需读取，并通过进程内共享 LRU 预算 `SegmentReaderOptions.VectorIndexCacheMaxBytes` 控制常驻引用；冷段打开后不占用 HNSW 图内存，reader Dispose 会移除本段缓存。补充懒加载、预算淘汰、KNN 结果一致性与 compaction 后内嵌索引可加载测试。
- **QueryEngine tombstone 过滤热路径优化**：`Execute(PointQuery)` 不再通过 LINQ `Where` 过滤墓碑，改为手写迭代器循环，并在查询前预筛与时间窗不相交的 tombstone；保持结果顺序、Limit 过滤后计数和墓碑闭区间覆盖语义不变，补充边界/Limit 与分配回归测试。
- **SegmentReader block 解码缓存**：`SegmentReader` 新增按 `(SegmentId, BlockIndex, Crc32)` 标识的已解码 `DataPoint[]` LRU 缓存，默认单 reader 预算 16 MB，可通过 `SegmentReaderOptions.DecodeBlockCacheMaxBytes` 调整或禁用；`QueryEngine` 重复查询同一落盘 block 时复用解码结果，缓存受内存上限约束并在 reader Dispose 时清空引用。补充重复查询命中与预算淘汰测试。
- **QueryEngine reader map 缓存**：`QueryEngine` 现在通过 `SegmentManager` 的绑定快照获取 `Index + Readers`，并在快照未变化时复用 `SegmentId -> SegmentReader` 映射，避免每次查询重复构建字典；`AddSegment` / `SwapSegments` / `Dispose` 发布新快照后会自动失效旧缓存，compaction swap 会延迟释放仍被查询快照租约持有的旧 reader，避免并发查询使用已 Dispose reader。
- **SegmentReader 时间范围索引优化**：`SegmentReader.Open` 现在会构建仅驻留内存的 block 时间范围索引，`FindByTimeRange` 通过 MinTimestamp/MaxTimestamp 双排序数组二分并扫描较小候选集，避免按时间范围查找时线性遍历全部 block；保持 block 区间重叠、边界 inclusive 与段内 `BlockDescriptor` 顺序稳定，补充乱序 block、多重重叠边界和大 block 列表性能回归测试。
- **SegmentReader series 索引优化**：`SegmentReader.Open` 现在会构建仅驻留内存的只读 `SeriesId -> BlockDescriptor[]` 索引（不改变 `.SDBSEG` 文件格式），`FindBySeries` 直接按 series 命中，`FindBySeriesAndField` 仅扫描该 series 的 block，并保持 `BlockDescriptor` 返回顺序稳定；补充多 series、多 field 与空命中回归测试。
- **MemTable Flush 热路径统计增量化**：`MemTable.EstimatedBytes` / `MinTimestamp` / `MaxTimestamp` 现在由 `Append` / WAL replay / Flush reset 生命周期维护，`ShouldFlush` 不再遍历全部 series；`MemTableSeries` 的字符串字段在 Append 时增量累加 UTF-8 byte count，并通过 immutable snapshot swap 缓存无追加期间的只读 Snapshot，排序在锁外完成以避免查询长时间阻塞 Append；`SnapshotRange` 在有序数据上直接二分并只复制命中区间，避免小范围查询先分配全量数组。补充并发 append/read 压力、replay、string/null/非 string 混合统计、重复 Snapshot 分配、范围查询边界与 flush 后重置回归测试。
- **Measurement schema-on-write 批量持久化**：`Tsdb.WriteMany(ReadOnlySpan<Point>)` 现在会先合并整批新增 TAG/FIELD 与 INT→FLOAT 类型提升，单次原子写入 `measurements.tslschema` 后再写 WAL/MemTable，保持“schema 先于数据可恢复”的崩溃安全语义，同时避免同一批导入中每个新增列都触发一次 schema fsync。
- **WAL catalog checkpoint**：`Tsdb.FlushNowLocked` 不再在每次 Flush 后向新 WAL 重写全量 `CreateSeries` snapshot；当 catalog 出现新增 series 时，Flush 会先原子持久化 `catalog.SDBCAT`，再写 Segment / WAL Checkpoint / 回收旧 WAL segment。崩溃恢复现在由「已 checkpoint 的 series 来自 catalog 文件，checkpoint 之后的新 series 继续来自 WAL `CreateSeries`」共同保证，避免 catalog 大时每次 Flush 产生 O(series_count) WAL 放大。

### Docs
- README 新增企业微信群二维码入口，方便用户扫码加入社群交流。
- 重写 README 顶部“SonnetDB 是什么”简介，明确 SonnetDB 当前已集成时序数据库、关系型数据库、KV NoSQL、S3 兼容存储桶与本地消息队列能力。
- 补充 SonnetDB vs IoTDB 对比文档的两种口径说明：新增 2026-05-06 的 `--comparison-server` 同口径“Server vs Server”实测结果（1,000 设备 × 30 字段 × 12 时间点，AB BA AB BA 四轮，SonnetDB Server 平均 22,867 values/sec、IoTDB 11,541 values/sec、约 1.98x），并同步更新根 README、`tests/SonnetDB.Benchmarks/README.md`、`QUICK_START.md` 与专门对比说明，保留旧的嵌入式 vs REST 历史结果但明确标注为不同方法学。
- 完善 `.agents/skills/sonnetdb-docker-language-build`，将模板 TODO 替换为 Docker-backed Go / Rust / Python / Linux connector 构建与 smoke test 回退流程，并修正技能默认提示词中的技能名。
- 新增 `docs/blogs/129-132` 连接器系列文章，分别介绍 Go、Rust、Visual Basic 6 与 PureBasic 连接器的 C ABI 复用方式、API 形态、构建/部署要求、CI 策略与适用场景。
- 新增 `docs/blogs/117-schema-on-write.md`，介绍受控 schema-on-write 的使用场景、SQL / LP / JSON / Bulk VALUES 自动补列规则、`INT -> FLOAT` 类型提升与 schema 先持久化再写 WAL 的崩溃安全语义。
- 新增 `docs/sql-cookbook.md`，把 `demo.sql` 中高频、当前真实支持的 `CREATE MEASUREMENT`、`INSERT`、`SELECT`、`GROUP BY time(...)`、窗口函数、PID、预测、向量检索、元数据与 `DELETE` 场景整理成可直接复制的 cookbook，并在 `docs/index.md` 与 `docs/sql-reference.md` 中加入入口。

### Fixed
- **CI Format Check 行尾规范化**：新增 `.gitattributes` 固定 C# 文件使用 LF 行尾，并归一化 `src/SonnetDB/Endpoints` 相关文件，补齐 file-scoped namespace 后空行，避免 `dotnet format --verify-no-changes --severity warn` 在 GitHub Actions 中因 CRLF / whitespace 失败。
- **Parity workflow startup diagnostics**：修复 parity compose 中 SonnetDB 容器 healthcheck 依赖 runtime 镜像未安装的 `bash`，改为使用镜像内已有的 `curl /healthz`；GitHub Actions 的 `Start parity stack` 失败时现在会保存 `docker compose ps` 与容器日志到 `artifacts/parity/<profile>/stack-diagnostics`，避免后续只报 “No files were found” 而丢失根因。
- **CI restore vulnerability gate**：`tests/SonnetDB.Benchmarks` 显式引用 `SQLitePCLRaw.bundle_e_sqlite3 3.0.3`，让 `Microsoft.Data.Sqlite` 的 native SQLite provider 解析到 `SourceGear.sqlite3 3.50.4.5`，避免 GitHub Actions 在 `dotnet build SonnetDB.slnx --configuration Release` restore 阶段因 `SQLitePCLRaw.lib.e_sqlite3 2.1.11` 的 NU1903 高危漏洞告警失败。
- 修复 `QueryEngine` 与 Compaction / Retention 段替换并发时，连续快照复用的旧 `SegmentReader` 可能被后续 swap 提前释放，导致查询偶发 `ObjectDisposedException` 的 CI 回归；`SegmentManager` 现在按 reader 维护租约状态，等待所有持有该 reader 的查询快照释放后再 Dispose。
- **GitHub Actions 发布链路修复**：修复 Native AOT 下 `SndbDataReader.GetSchemaTable()` 的 `SchemaTableColumn.DataType` 反射裁剪告警；连接器发布 workflow 递归 checkout DotSearch / DotVector 子模块；Dockerfile 显式复制 DotSearch / DotVector 源码并让 Docker 发布路径过滤覆盖 `src/SonnetDB.Core/**` 与 `modules/**`；同步更新 DotSearch Jieba 词典生成 target 的跨平台路径与工具执行方式，避免 Linux CI、连接器发布和镜像构建因缺少依赖源码或 Windows 路径分隔符失败。
- 修复 Rust connector Linux x64 CI 构建失败：`build.rs` 现在通过 Rust 支持的 `+verbatim` 链接修饰符精确链接 `SonnetDB.Native.so`，避免 `-l:SonnetDB.Native.so` 被 Cargo 解析为 rename 语法并报出 library name must not be empty。
- **CI 测试稳定性**：为服务端与 Accuracy 测试项目补齐 `coverlet.collector`，修复 `--collect:"XPlat Code Coverage"` 找不到 collector；Accuracy Tests 在 Docker/InfluxDB 不可用时改为显式 no-op 通过并记录原因，避免可选外部依赖缺失导致 CI 失败；SSE db 事件断言改为等待当前测试触发的数据库事件，避免 `__copilot__` 系统库后台事件串扰。
- **CI Format Check 回归**：收窄 `.editorconfig` 私有字段命名规则，避免 `dotnet format` 把 `private const` 与 `private static` 字段误判为实例字段；同步修复既有 whitespace、import 顺序和 LF 行尾问题，使 `dotnet format --verify-no-changes --severity warn` 可通过。
- **Tsdb.Dispose final flush 诊断**：关闭路径中的 final flush 失败仍保持 `Dispose` 不抛异常，但会写入 `Tsdb.LastError` 并触发 `Tsdb.DiagnosticEvent`，避免异常被静默吞掉；诊断事件订阅者自身抛错不会影响关闭语义。
- 修复 Linux x64 C connector quickstart 运行时错误：CMake 现在在 Linux 下通过精确文件名链接 `SonnetDB.Native.so`，避免 Native AOT 共享库无 SONAME 时把构建目录写入 `DT_NEEDED`，并补充 WSL 开发环境与连接器验证文档。
- **普通用户登录不再显示控制面虚拟库 `__control_plane__`**：Web Admin 会在普通用户进入后台时清理 SQL Console 与 Copilot 会话历史中残留的控制面本地状态；SQL Console 的新建标签、刷新数据库、运行 SQL 与待执行 SQL 注入路径也增加二次防护，避免同一浏览器先用 admin 打开 system tab 后再切换普通账号时暴露 `__control_plane__`。
- **Copilot 会话历史补齐 assistant 回复与引用保存**：CopilotDock 现在按发起请求时的会话 ID 追加 user / assistant 消息，避免请求完成前切换或新建会话导致最终回复没有落盘；assistant 消息会连同 citations 一起写入本地历史，并在历史会话中渲染引用标题、来源与摘要。标题栏新增「+ 新会话」入口，历史弹层保留切换、重命名、删除与清空能力。
- **CopilotDock 回答改为 Markdown 渲染并隐藏裸 citation 标记**：聊天浮窗现在会把 Copilot 回复按 Markdown 渲染，代码块、列表、表格与行内代码可正常排版；渲染时转义模型返回的原生 HTML，并隐藏回答末尾类似 `[C11][C12]` 的内部引用编号，避免用户误以为是 SQL 或异常内容。
- **Copilot 错误提示不再暴露原始 JSON**：Web Admin 的 Copilot API 客户端现在会解析服务端 `{ error, message }` 与流式 provider 错误，把 `copilot_not_ready / chat.endpoint_invalid` 等内部代码映射成可操作的中文提示，引导用户检查「Copilot 设置」中的服务地址、API Key、模型或 embedding 配置，避免直接显示 `Copilot 请求失败 503: {"error":...}`。
- 统一 Copilot skills、Web starters 与 SQL 编辑器方言文案中的 SonnetDB SQL 示例口径：聚合示例统一回到当前真实支持的 `GROUP BY time(...)`，修正 `pid_tune` / `pid_compute`、`time_bucket(...)`、`LAG/LEAD OVER (...)` 等会误导当前版本能力边界的过时或未公开支持写法。
- **Token / API Key 管理现在支持带连字符的用户名**：控制面 SQL parser 为 `SHOW TOKENS FOR`、`ISSUE TOKEN FOR`、`SHOW GRANTS FOR` 等语句补齐 quoted username 语法，`TokensView.vue` 与 `UsersView.vue` 在回填已有用户名时统一走字符串 quoting，修复用户名如 `ops-admin` 时签发 token 报 `期望标识符（位置 16）`，并补 parser 与控制面集成回归测试。
- **Copilot 现在能直接理解“新建仓库并建表”并在无 db 场景继续工作**：新增 `CopilotProvisioning` 结构化意图抽取，把“建数据库 + 建 measurement + 从描述中抽字段”从 prompt 规则落到后端代码；`draft_sql` / `execute_sql` 现已支持 `CREATE DATABASE`，`/v1/copilot/chat` 对 provisioning 请求放开 `db` 必填限制，普通问题仍保持原有校验；Web 端 `CopilotDock` 同步支持在明显建库请求下绕过“先手工创建数据库”弹窗，并把工具产出的 SQL 自动绑定到目标库。
- **CopilotDock 在没有任何业务数据库时不再 400 `请求体需包含 db。`**：上一轮把 `__copilot__` / `_internal` 等系统库从 `dbs` 列表里过滤后，新装实例（或仅持有系统库的账号）`selectedDb` 永远为空，发送时直接被服务端 `CopilotChatEndpointHandler` 用 `400 bad_request` 拦下。现在 `CopilotDock.send()` 在 `targetDb` 为空时拦截发送，弹出 NDialog 引导用户输入数据库名（沿用 `isValidIdentifier` 校验、字母开头 + 字母数字下划线），点击「创建并继续」直接走 `execControlPlaneSql('CREATE DATABASE <name>')`，成功后 `reloadDbs()` 并把新库写回 `selectedDb` 再继续 `send` 流程；非超级用户提示「请联系管理员先创建一个」。配合上一轮服务端隐藏 `__copilot__` 的修复，确保新部署 / 空白账号也能从零开始让 Copilot 帮忙建第一个仓库。
- **Copilot 文档知识库索引不再因标题含保留字符而 500 `ingest_failed`**：`DocsIngestor` 新建 `__copilot__.docs` 时把 `section` / `title` 从 `TAG` 调整为 `FIELD STRING`，允许保留 Markdown 标题中的 ``=``, `,`, 引号和换行等字符；同时兼容旧库中遗留的 `TAG` 模式，在写入时仅对旧 schema 做最小归一化，避免 `bulk ingest / \`onerror=skip\`` 这类标题继续触发 `tag value contains reserved characters`。新增测试覆盖新 schema 精确保留标题，以及旧 schema 不抛异常回归。

### Fixed
- **SQL Console 的 GEOPOINT 结果现在可读且可上图**：`web/src/components/SqlResultPanel.vue` 现在会把 `GEOPOINT` / GeoJSON Point / `POINT(...)` 值格式化为可读文本，Markdown / 表格 / CSV 也会复用同一套值格式；`web/src/components/ResultMapPreview.vue` 与 `web/src/views/TrajectoryMap.vue` 则统一改成在 `style.load` 后先注入点线图层，不再等待底图瓦片完全加载，修复 `SELECT time, device, position, speed FROM vehicle LIMIT 100;` 这类结果里 `position` 看不到值、地图也不出点的问题。
- **Docker 镜像缺少 SPA 静态文件**：`src/SonnetDB/Dockerfile` 发布服务端时显式传入 `-p:BuildAdminUi=true`，让 `dotnet publish` 在镜像构建阶段执行 `web` 的 `npm ci && npm run build`，并把 `wwwroot/index.html` 与前端 assets 发布进最终镜像，避免容器访问 `/` 或 `/admin` 返回 `SonnetDB SPA static files are missing`。
- **隐藏 Copilot 系统库 `__copilot__` + 修复"创建数据库"被误解为列 measurement**：`DatabaseAccessEvaluator.GetVisibleDatabases` 现在统一过滤名字以双下划线开头并以双下划线结尾的系统库（含 `__copilot__`），管理员与普通用户均不再在 `GET /v1/db`、Web Admin Dashboard / Databases / SQL Console / Grants 下拉、Copilot Dock 数据库选择器、`/v1/copilot/chat` 的 `VisibleDatabases`、MCP `list_databases` 工具结果里看到它；`CopilotChatEndpointHandler` 同步对 `req.Db` 命中系统库时直接返回 `403 system_database`，防止旧客户端 localStorage 中残留的 `selectedDb = "__copilot__"` 触发 LLM 把系统知识库当成业务库去 `SHOW MEASUREMENTS`。`CopilotDock.vue` 客户端 `SYSTEM_DATABASES` 同步加入 `__copilot__` 并补充 `__xxx__` 通配过滤，作为防御性双重保险。`CopilotAgent` 的 Planner / Answer System Prompt 增加专门分支：用户说"建一个仓库 / 新建数据库 / create database"时，必须先 `draft_sql` 一条 `CREATE DATABASE <name>`（如未指定名字则建议合理名）+ 必要的 `CREATE MEASUREMENT`，禁止再去 `list_measurements` / `describe_measurement`，并显式告诉 LLM "不要把 `__copilot__` / `_internal` 当成业务库"。

### Added- **Copilot 内置零依赖 embedding（M1）**：新增 `BuiltinHashEmbeddingProvider`，基于 SHA-256 + 词袋哈希投影生成 384 维 L2 归一化向量，无需任何模型文件即可让 Copilot 子系统就绪；`CopilotEmbeddingOptions.Provider` 默认值由 `local` 改为 `builtin`，`CopilotReadiness` 接受 `builtin`，DI 工厂在 `local` 模型缺失时自动降级到 `builtin`，从根上消除"`503 copilot_not_ready: embedding.local_model_path_missing`"。同步保证 `CopilotDocsIngestionService` 在新部署即可自动把 `./docs`（含 `sql-reference.md` 等）/`./web/help`/`./src/SonnetDB/wwwroot/help` 全部 markdown/html 摄入系统库 `__copilot__.docs`（`embedding FIELD VECTOR(384)`）作为 Copilot 知识库。- **Copilot 知识库可视化端点（M1.5）**：新增 `GET /v1/copilot/knowledge/status`，返回当前 embedding provider（含是否处于 builtin 降级）、向量维度、扫描根目录绝对路径、已索引文件数 / 块数 / 最近一次摄入 UTC 时间、技能库条数，便于 Web Admin "Copilot 设置" 页面显示知识库实况和提供"立即重建索引"按钮。- **SQL 控制台生成走 SonnetDB 方言（M2）**：重写 `AiEndpointHandler.BuildSqlGenPrompt`，明确告诉模型 SonnetDB 使用 `time` 列（不是 `ts`）、`CREATE MEASUREMENT … (col TAG, col FIELD <FLOAT|INT|BOOL|STRING|VECTOR(N)>)`、`GROUP BY time(1m)`（不支持 `date_trunc` / 按 tag 分组）、`knn(measurement, vec_col, [向量], k [, 'cosine'])` 向量检索、`LIMIT n OFFSET m` 分页、`DELETE FROM ... WHERE ...` 删除，禁止生成 MySQL/PostgreSQL/InfluxQL 方言；未指定 `db` 时也提供通用 SonnetDB system prompt（原实现会跳过提示）；前端 `SqlConsoleView.generateSql` 新增 `stripCodeFence` 防御性剥离 ```sql ... ``` 代码围栏，避免编辑器里残留 Markdown 标记。配套把 prompt 模板抽到 `src/SonnetDB/Copilot/Prompts/sql-gen.md` / `sql-gen-no-db.md`，通过 `<EmbeddedResource>` 嵌入程序集，由 `PromptTemplates` 加载器（带缓存 + `{{key}}` 占位符替换）按需读取，便于非编程人员维护提示词与未来多语言/A-B 测试。- **Copilot Agent 支持 DDL/DML SQL 起草与执行**：在 `/v1/copilot/chat` 后台代理中新增 `draft_sql` 与 `execute_sql` 两个工具。`draft_sql` 会用 `SqlParser` 校验 `CREATE MEASUREMENT` / `INSERT` / `DELETE` / `SELECT` 语句并附带 measurement 是否已存在等说明，但不写入数据；`execute_sql` 在调用方对当前数据库具备 `Write` 权限时才会真正执行写入语句。Planner / Answer / 启发式回退、`CopilotAgentContext` 都已同步扩展，遇到“建表 / 写入 / 删除”意图时会先用 `list_measurements` / `describe_measurement` 收集上下文，再生成可直接复制执行的 SQL（放进 ```sql 代码块）。在 `tests/SonnetDB.Tests/Copilot/copilot-eval-scenarios.json` 新增 2 个 `write` 类场景覆盖回归。- **全局 CopilotDock 浮窗 + 知识库可视化卡片（M4）**：在 Web Admin `AppShell.vue` 右下角注入新组件 `web/src/components/CopilotDock.vue`（折叠态为 52px 圆形 FAB，展开态为 380×540 浮窗，支持顶部拖拽和「全屏 / 收起到角标」切换），任意页面均可呼出 Copilot 助手；浮窗内置数据库选择、最近 3 条进度状态、最终回答展示、停止按钮、3 个示例 quick-prompt，复用既有 `streamCopilotChat` SSE 端点；同时为 `Copilot 设置`（`AiSettingsView.vue`）新增「本地知识库」卡片，消费 `GET /v1/copilot/knowledge/status` 显示 embedding provider（含 builtin 降级提示）、向量维度、根目录、已索引文件 / 块 / 技能数量、最近摄入 UTC 时间，并提供「立即重建索引」按钮（POST `/v1/copilot/docs/ingest {force:true}`，仅 admin 可见）；`web/src/api/copilot.ts` 增加 `fetchCopilotKnowledgeStatus` / `triggerCopilotDocsIngest`。- **Copilot 会话历史 — 客户端持久化（M5 第一阶段）**：新增 Pinia store `web/src/stores/copilotSessions.ts`（`useCopilotSessionsStore`），把 Copilot Dock 内的会话以 `localStorage` key `sndb.copilot.sessions.v1` 持久化（最多 50 条，按 `updatedAt` 倒序，自动从首条用户消息派生 ≤ 32 字符标题，深度 `watch` 自动落盘）；`CopilotDock.vue` header 新增「会话历史」Popover 与 `+ 新会话` / `清空` 按钮，列表项支持点击切换、`✎` 重命名（`useDialog` + `NInput`）、`×` 删除；`send()` 改为写入 `sessions.current.messages`；切换会话时自动还原 `db` 选择。第二阶段（服务端 `__copilot__.conversations` 持久化 + `/v1/copilot/conversations` REST 端点）保留在 ROADMAP。- **Copilot 页面上下文感知（M6）**：CopilotDock 选择数据库下方新增 `📍 当前页面：Xxx [· SQL N 字符] [· db=xxx]` 状态标签，默认启用、可点“×”关闭；发送时（`buildContextMessage()`）根据当前路由（`dashboard` / `sql` / `databases` / `events` / `ai-settings` / `chat` / `home`）生成一条 `role: system` 上下文消息，在 `sql` 页面额外携带当前 SQL Console 选中的数据库与正在编辑的 SQL（超过 2000 字符自动截断）包裹在 ```sql ``` 中一同提供给 LLM。上下文消息仅在 `send()` 时临时拼到 `messages[]` 头部，不会写入 `useCopilotSessionsStore` 会话历史，避免污染本地持久化。同步扩展 `useSqlConsoleStore`：新增 `currentSql` / `currentDb` / `setCurrent(db, sql)`，`SqlConsoleView.vue` 通过 `watch([targetDb, sql], ...)` 实时同步。- **Copilot 权限选择器 + 写操作审批（M7、一阶段）**：CopilotDock 数据库选择下方新增权限模式指示器：`🔒 只读模式`（默认，绿色 NTag）点击后弹 NPopconfirm “切换为读写模式后，Copilot 可直接执行 INSERT / DELETE / CREATE MEASUREMENT 等写入语句。是否启用？” 二次确认，确认后变为 `⚠️ 读写模式`（黄色，可点 × 关闭回退）。偏好持久化在 localStorage `sndb.copilot.permission.v1`。请求载荷新增 `mode: "read-only" | "read-write"` 字段，服务端 `CopilotChatEndpointHandler` 仅当其严格等于 `read-write` 时才使 `effectiveCanWrite = canWrite && true`，其余取值（未提供 / `read-only` / 任意拼写）一律强制收紧为只读。该限制在服务端生效，即使客户端被绕过也无法越权调用 `execute_sql` 写入。服务端原有凭据权限仍是上限（读只凭据即使选 read-write 也仍然只读）。- **Copilot 模型选择器（M8）**：服务端新增 `GET /v1/copilot/models` 端点，返回 `{ default, candidates[] }`（候选来自新增的 `CopilotChatOptions.AvailableModels` 配置，默认模型会被自动插入候选首位）；`CopilotChatRequest` 增加 `Model` 字段，`IChatProvider.CompleteAsync` 签名增加 `string? modelOverride` 参数，`OpenAICompatibleChatProvider` 优先使用 override、仅 fallback 到 `CopilotChatOptions.Model`；`CopilotAgentContext` 增加 `ModelOverride`，Planner / Answer / SQL Repair 三处 `CompleteAsync` 都会透传该 override。CopilotDock 数据库下方新增模型 NSelect（`filterable + tag` 允许自由输入，服务端默认模型标为「默认」，其他候选按原文列出），选择持久化在 localStorage `sndb.copilot.model.v1`，发送时仅在用户显式选中某个模型时才携带 `model` 字段，保持「不选择 = 走服务端默认」语义。
- **SQL Console 多标签页持久化 + Copilot SQL 落盘展示**：SQL Console 状态升级为 Pinia + localStorage 持久化的多标签页模型，每个选项卡独立保存目标库、SQL 文本、执行结果与摘要，切换页面或刷新后不会丢失；页面使用 `KeepAlive` 固化 SQL Console 实例。Copilot 流式返回的 `draft_sql` / `query_sql` / `execute_sql` 会自动创建新的 SQL Console 选项卡，其中查询/执行工具结果直接转换为 Console 结果展示，避免写入类 SQL 被重复执行。
- **SQL Console 语法高亮回归（M9）**：新增 `web/src/components/sonnetdb-dialect.ts` 定义 `SonnetDbSQL = SQLDialect.define({ ...StandardSQL.spec, keywords + 'measurement measurements tag field bucket show describe explain knn', types + 'vector float int bool string', builtin + 'knn time time_bucket forecast pid_compute pid_tune' })`；`SqlEditor.vue` 改为 `dialect: SonnetDbSQL`，这样 `MEASUREMENT` / `TAG` / `FIELD` / `VECTOR` 等关键字与 `knn` / `time_bucket` / `forecast` / `pid_compute` / `pid_tune` 等内置函数同时获得语法高亮（keyword/type/builtinName 三种 token）与 lang-sql 内置的关键字自动补全。
- **新手引导 / 提示词模板（M10）**：新增 `web/src/copilot/starters.ts` 定义 `COPILOT_STARTERS` 集合（建表 / 写入 / 聚合 / 向量 / 预测 / PID / 排查共 7 大分类、指定路由 `routeKeys` 过滤）与 `pickStarters(routeKey, max)` 函数；CopilotDock 空白态从原来的 3 条硬编码 `<li>` 重构为 `grid-template-columns: repeat(auto-fill, minmax(140px, 1fr))` 的 starter 卡片（分类胶囊 + 标题 + tooltip 说明），点击后 `prompt` 填入输入框，路由感知下会优先提示当前页面（`sql` / `databases` / `dashboard`）独有的模板。

### Docs
- 新增 `extensions/sonnetdb-vscode/` 规划骨架：包含 VS Code 扩展 `package.json` / `tsconfig.json` / `src/` 占位代码、专属 `ROADMAP.md`、`docs/architecture.md` 与 `docs/api-contract.md`，用于承接官方 `SonnetDB for VS Code` 实现。
- 主 `ROADMAP.md` 同步新增 **Milestone 18 — VS Code 数据库扩展（SonnetDB for VS Code）**，把原 Milestone 10 中模糊的 `#40` 占位需求细化为 `#99 ~ #108` 的可执行 PR 路线；首批推荐从 `#99 ~ #103` 先闭环“远程连接 + Explorer + SQL + 结果三视图”。

### Fixed
- **Copilot 建表 SQL 兜底回复**：当用户请求“建温度/湿度监测表”而 planner 只返回 `list_measurements`、最终回答模型又失败或返回空时，Agent 现在会自动补 `draft_sql` 并在 deterministic fallback 中输出可复制的 `CREATE MEASUREMENT` 代码块，不再回复“请结合返回的结构化结果继续确认或缩小问题范围”。
- **ADO.NET 提供程序支持 `USE <db>` 切库与 `SELECT current_database()`（M16）**：与 SQL Console 同步，新增 `SonnetDB.Data.Internal.SqlMetaCommand` 在 `SndbCommand` 真正发送 SQL 之前拦截元命令——远程模式下 `USE foo` 直接修改连接当前 `Database`（后续命令路由到 `/v1/db/foo/sql`，不做服务端校验，下一条业务 SQL 自然返回 404 / 403），`SELECT current_database()` / `SELECT database()` / `SHOW CURRENT_DATABASE` / `SHOW CURRENT DATABASE` 合成单行 `current_database` 结果集；嵌入式模式下 `current_database()` 返回当前 Data Source 路径，但 `USE` 因数据源等价于物理路径而抛 `NotSupportedException`。`SndbConnection.ChangeDatabase(name)` 不再无条件抛错，改为内部执行一条 `USE \`name\`` 复用同一路径，让用户在 `CREATE DATABASE foo` 之后可直接 `conn.ChangeDatabase("foo")` 或 `cmd.CommandText = "USE foo"; cmd.ExecuteNonQuery();` 在同一连接上继续 `CREATE MEASUREMENT … / INSERT …`。同步新增 4 个 ADO 单元测试覆盖嵌入式行为。
- **SQL Console 支持 `USE <db>` 切库与 `SELECT current_database()`（M16）**：SonnetDB 服务端按 URL 路径 `/v1/db/{db}/sql` 强绑定目标库，没有连接级 "current database" 状态。新增客户端元命令解析 `web/src/api/sqlMeta.ts` `parseSqlMetaCommand()`，在 SQL Console `run()` 循环里优先识别 ① `USE <db>`（MySQL / SQL Server 风格，`USE system` 切控制面、需 superuser，未知库返回 `database_not_found` 并列出可用库）、② `SELECT current_database()` / `SELECT database()` / `SHOW CURRENT_DATABASE` / `SHOW CURRENT DATABASE`（PostgreSQL / MySQL 风格混合兼容），合成本地结果集（`build​Client​Result​Set`）展示并自动同步「目标」选择器，不发请求到服务端，避免触发服务端 SqlParser 的「未知关键字」错误。SqlEditor 上方新增提示行说明这两个命令。
- **Docker 镜像随构建打包 `docs/` 与 `copilot/` 知识库素材（M16 / 知识库零启动）**：原 `Dockerfile` 仅把 `dotnet publish` 输出（含 jekyllnet 渲染好的 `wwwroot/help`）拷进运行时镜像，源码目录 `docs/` 与 `copilot/skills/` 不在 `/app` 下，导致默认 `Copilot.Docs.Roots = ["./docs", "./web/help", "./src/SonnetDB/wwwroot/help"]` 与 `Copilot.Skills.Root = "./copilot/skills"` 在容器里只能命中 `wwwroot/help`，且技能数始终为 0。现在在最终镜像额外 `COPY docs/ /app/docs/` 与 `COPY copilot/ /app/copilot/`，让 `CopilotDocsIngestionService` / `CopilotSkillsIngestionService` 在容器首次启动即可把 docs（含 `sql-reference.md` 等）与 6 条内置技能向量化进 `__copilot__.docs` / `__copilot__.skills`，无需挂载额外卷。
- **SQL Console 支持多语句逐条执行（M9 配套）**：服务端 `SqlParser` 一次只接受一条语句（`ExpectEndOfFile` 见到第二条会抛 `语句末尾存在多余内容`）。新增客户端 `web/src/api/sqlSplit.ts` `splitSqlStatements()`，按顶层 `;` 切分并正确忽略 `'...'` 字符串、`-- ...` 行注释和 `/* ... */` 块注释；`SqlConsoleView.vue` 改为顺序执行每条语句、渐进展示结果、任一条失败即停止后续执行，并在底部汇总 `共 N · 成功 X · 失败 Y · 合计 ts.ms`，解决执行 `demo.sql` 时 `[http_400] 语句末尾存在多余内容` 的问题。
- **SQL Console 结果三视图：文本 / 表格 / 图表（M16）**：每条语句结果改为独立卡片 `web/src/components/SqlResultPanel.vue`，header 显示 `#N` 状态徽标 + 单行 SQL 预览 + 元信息（行数 / 受影响 / 耗时）+ 三段式 `n-tabs`：① **文本** — 用 `marked` 把结果集渲染成 Markdown 表格（>100 行截断并提示），允许列值内嵌 SVG/HTML 直接渲染；② **表格** — 仍用 `n-data-table`；③ **图表** — 新组件 `SqlResultChart.vue` 自动检测时间列（按列名 `time`/`ts`/`timestamp` 或可解析为时间的列）+ 数值字段 + 字符串低基数 tag 列，按 `tag` 维度 group，原生 SVG 折线图（4×4 网格 + 5 段时间刻度 + 8 色配色），无任何 chart 第三方依赖。新增依赖 `marked@14.1.4`。
- **Copilot 浮窗调用 503 `chat.endpoint_invalid`**：Web Admin 的「Copilot 设置」保存的 `AiOptions`（国际版 / 国内版 + ApiKey + Model）原本只持久化到 `<DataRoot>/.system/ai-config.json`，并未同步到 `CopilotChatOptions` / `CopilotEmbeddingOptions` 单例，导致 `/v1/copilot/chat` 走的 `CopilotReadiness.EvaluateChat` 因 `Endpoint` 为空一直返回 `copilot_not_ready: chat.endpoint_invalid`。新增 `AiCopilotBridge.Apply`，在 `AiConfigStore` 构造时（启动加载已有配置）和 `PUT /v1/admin/ai-config` 保存后，立即把 provider URL（`https://sonnet.vip/v1/` 或 `https://ai.sonnetdb.com/v1/`）、ApiKey、Model 写入 `CopilotChatOptions`，并对 `provider=openai` 的 embedding 选项做空值回填。
- **SQL Console 移除内嵌 Copilot 面板（M16）**：删除 `SqlConsoleView.vue` 内的 AI 助手卡片（`生成 SQL` / `分析结果`）及相关脚本和样式，统一由全局右下角 CopilotDock 浮窗提供问答能力，避免双入口造成的体验割裂。
- **下线 `Copilot Chat` 顶层菜单（M16）**：从 `AppShell.vue` 主导航与 `router/index.ts` 移除 `chat` 路由，删除 `views/CopilotChatView.vue`；Copilot 已通过全局 CopilotDock 浮窗在所有页面随时可呼出。
- **CopilotDock 浮窗按窗口高度自适应（M16）**：把固定 `380×540` 调整为 `width: 420px; height: 61.8vh`（黄金比例），新增 `min-height: 480px` 与 `max-height: calc(100vh - 48px)` 边界，避免在大屏上显得过小、在小屏上溢出。
- 重写首页欢迎页为产品介绍页：去掉安装/帮助导向叙述，改为展示数据库简介、核心功能、产品形态和路线图对应能力，并保留进入后台入口。
- 修复 Production 模式下 `GET /admin/` 自重定向死循环（`ERR_TOO_MANY_REDIRECTS`）：原 `UseDefaultFiles({ RequestPath = "/admin" })` 未提供针对 `wwwroot/admin` 的 `FileProvider`，与 `MapGet("/admin")` / `MapFallbackToFile` 共同作用导致 Vite 构建产物无法被正确解析；现改为使用专用 `PhysicalFileProvider(wwwroot/admin)` 的 `UseStaticFiles`，并显式 `MapGet("/admin/")` 直接返回 `index.html`。
- 修复访问根路径 `/`（产品宣传首页）被 Bearer 认证中间件拦截返回 `401`：将 `/`、`/favicon.ico`、`/robots.txt` 加入匿名白名单，使 `MapHomePage()` 渲染的官网首页可直接访问。
- **Milestone 14 核查与修复**：核查 PR #63 ~ #69 的实际落地情况，确认 `SonnetDB.Copilot` 命名空间骨架、文档/技能摄入管线、MCP 工具增强、单轮/多轮 Agent 编排、Web Admin Chat Tab 与 nightly eval 套件均已落库；修复 `tests/SonnetDB.Tests/Copilot/copilot-eval-scenarios.json` 中 5 个场景（`metadata_show_measurements_sql` / `query_cpu_time_filter` / `analytics_changepoint_cusum` / `troubleshoot_explain_slow_query_log` / `troubleshoot_multiturn_sample_memory`）的 `answerSummary` 与 `expectedAnswerContains` 不一致问题，使 nightly eval 准确率从 86.11% 恢复到 100%（≥ 95% 阈值），citation 命中率 100%，p95 延迟 < 20 ms。同步把 `ROADMAP.md` 中 PR #66 / #67 / #69 与 Milestone 14 总览状态由 📋 回填为 ✅。

### Changed (品牌重命名 / Breaking)
- **项目重命名 `TSLite` → `SonnetDB`**：因与其他品牌名称冲突，全仓代码、命名空间、包名、Docker 镜像、文档、CI 脚本统一改名为 `SonnetDB`。
  - **NuGet 包 ID**：
    - `TSLite` → `SonnetDB.Core`（核心嵌入式引擎库）
    - `TSLite.Server` → `SonnetDB`（HTTP 服务端 / Docker 镜像主品牌）
    - `TSLite.Cli` → `SonnetDB.Cli`（dotnet tool）
    - `TSLite.Data` → `SonnetDB.Data`（ADO.NET 提供程序）
  - **dotnet tool 命令名**：`tslite` → `sndb`。
  - **ADO.NET 连接字符串 scheme**：`tslite://` / `tslite+http://` / `tslite+https://` → `sonnetdb://` / `sonnetdb+http://` / `sonnetdb+https://`。
  - **Docker 镜像**：`iotsharp/tslite-server` → `iotsharp/sonnetdb`，`ghcr.io/<owner>/tslite-server` → `ghcr.io/<owner>/sonnetdb`。
  - **环境变量前缀**：`TSLITE_*` → `SONNETDB_*`。
  - **Prometheus 指标前缀**：`tslite_*` → `sonnetdb_*`。
  - **解决方案 / 目录**：`TSLite.slnx` → `SonnetDB.slnx`；`src/TSLite*` 与 `tests/TSLite*` 整体迁移至 `src/SonnetDB*` 与 `tests/SonnetDB*`。
  - **服务端 Bundle / 安装包**：`tslite-server-full-<ver>-<rid>` → `sonnetdb-full-<ver>-<rid>`；启动脚本 `start-tslite-server.{cmd,sh}` → `start-sonnetdb.{cmd,sh}`；Linux 包路径 `/opt/tslite-server` → `/opt/sonnetdb`。
  - **代码命名空间**：`TSLite.*` → `SonnetDB.*`，`TSLite.Server.*` → `SonnetDB.*`（服务端去掉 `.Server` 子命名空间，与对外品牌一致）。
  - **保留**：核心类型名 `Tsdb` / `TsdbOptions` / `SndbConnection` 等不变（`Tsdb` 是通用时序库缩写而非品牌词）。
- **版本升级**：`0.1.0` → `1.0.0`。
- Server Admin UI 从“嵌入式资源”切换为官方 SPA 模式：开发期由 `SpaProxy` 自动启动 `web` 的 `npm run dev`，发布期改为 Static Web Assets 输出到 `/admin/`，以便更贴近 ASP.NET Core 推荐做法并减少 AOT 发布链路的额外定制。

### Planned
- **Milestone 14 — SonnetDB Copilot：MCP 工具 + 知识库 + 智能体**：基于 Microsoft Agent Framework 新建独立项目 `src/SonnetDB.Copilot/`，复用现有 `/mcp/{db}` 工具集 + Milestone 13 的向量召回，把"用户文档 / 技能库 / 数据库 schema"统一存入 `__copilot__` 系统库（dogfooding）。Embedding/Chat 走统一 `IEmbeddingProvider` / `IChatProvider` 抽象，**本地 ONNX（bge-small-zh）** 与 **OpenAI 兼容端点（国际 / 国内任意 OpenAI-compat 网关）** 同时支持，可按部署场景切换。新增 HTTP 端点 `POST /v1/copilot/chat`（NDJSON / SSE 流式）+ Web Admin Chat Tab。详见 ROADMAP PR #63 ~ #69。

### Fixed
- 回填 `ROADMAP.md` 的 PR #60 状态与 Milestone 13 进度，统一为与现有代码、测试和 `docs/vector-search.md` 一致的已实现状态。
- 修正 `KnnExecutor` 在 HNSW + 时间范围过滤下“部分 ANN 命中后再精确补扫”时可能把同一点重复计入候选的问题，并补充 compaction 后 v6 内嵌索引 / legacy `.SDBVIDX` fallback 仍可加载使用的回归测试。
- 回填 `ROADMAP.md` 的 PR #62 状态与 Milestone 13 里程碑进度：默认 `10k / 100k` 向量基准与 README 实测结果已闭环，`1M` 长测与外部库同机对比保留为显式 / 环境可选的后续补数项，不再阻塞 Milestone 14。

### Added
- **PR #68 — Copilot 多轮对话 + SQL 自我纠错 + Web Admin Chat（Milestone 14 第六切片）**
  - `POST /v1/copilot/chat` / `/v1/copilot/chat/stream` 请求体升级为兼容 `message` 与 `messages[]` 两种模式；服务端会对最近对话按 token 预算裁剪，只保留最新且最相关的上下文进入 planner / answer prompt，支持多轮追问。
  - `CopilotAgent` 新增 `query_sql` 自我纠错回路：当模型规划出来的只读 SQL 在解析、校验或执行阶段失败时，会封装为 `SqlExecutionException` 回喂给模型改写，最多重试 3 轮，并通过 `tool_retry` 事件把错误与改写后的 SQL 流式回传前端。
  - Web Admin 新增 `Copilot Chat` 页面与路由，接入 `/v1/copilot/chat/stream` SSE 事件流，实时展示检索/工具执行过程，支持 skill/citation 折叠查看，以及将候选 SQL 一键发送到 `SQL Console` 并立即执行。
  - `SQL Console` 新增跨页面待执行 SQL 队列，允许 Chat 页把修正后的查询直接落到控制台执行；前端构建已随路由与页面一并通过。
  - 测试补齐：新增多轮 history 裁剪回归用例与 `query_sql` 自动重写回归用例，确认历史上下文按预算收敛、失败 SQL 会触发改写并闭环返回结果。

- **PR #67 — Copilot 单轮问答闭环（Milestone 14 第五切片）**
  - 新增内部 `CopilotAgent` 编排器：对单轮问题执行 docs/skills 召回、工具规划、只读工具执行与最终回答生成，串起 `IEmbeddingProvider`、`IChatProvider`、技能库、文档库与现有 MCP 只读工具语义。
  - 新增 HTTP 端点：`POST /v1/copilot/chat` 返回 `application/x-ndjson` 事件流，`POST /v1/copilot/chat/stream` 返回 `text/event-stream` SSE；统一输出 `start` / `retrieval` / `tool_call` / `tool_result` / `final` / `error` / `done` 事件，并在最终回答中附带 `citations`。
  - 新增 Bearer + 数据库级 `read` 权限校验：Copilot 聊天请求必须显式指定数据库，服务端会在进入编排前校验数据库名、数据库存在性以及当前凭据是否具备该库的 `read` 权限。
  - `Program.BuildApp(...)` 新增可选 `configureServices` 覆盖入口，便于在集成测试中注入 fake embedding/chat provider，对 Copilot 闭环做稳定的端到端验证。
  - 测试补齐：新增 Copilot chat 端到端用例，覆盖无 grant 返回 `403`、NDJSON 事件流返回、SSE 事件流返回三条关键链路。

- **PR #66 — MCP schema 工具增强 + 抽样 / explain（Milestone 14 第四切片）**
  - MCP 新增只读工具：`list_databases()`、`sample_rows(measurement, n=5)`、`explain_sql(sql)`；其中 `list_databases()` 会按 `GrantsStore` 与当前 Bearer 身份过滤为“当前可见数据库”集合。
  - 新增 `SonnetDbMcpSchemaCache`：对 `list_measurements` / `describe_measurement` 及对应 schema resources 统一提供 30 秒进程内缓存，降低 Copilot / MCP 高频探测时的重复 schema 开销。
  - 新增 `SonnetDbMcpExplainSqlService`：对只读 SQL 估算 `matchedSeriesCount`、`estimatedSegmentCount`、`estimatedBlockCount` 与 `estimatedScannedRows`，覆盖普通 `SELECT`、`SHOW MEASUREMENTS`、`DESCRIBE MEASUREMENT`，并支持 `forecast(...)` / `knn(...)` 表值函数的主扫描字段估算。
  - 端到端测试补齐：覆盖新工具返回结构、动态用户数据库可见性过滤，以及 schema 工具 30 秒缓存窗口内返回旧快照的行为。

- **PR #65 — Copilot 技能库 + 技能路由（Milestone 14 第三切片）**
  - 新增 `SkillSourceScanner` / `SkillFrontmatter` / `SkillRegistry` / `SkillSearchService`：扫描 `copilot/skills/*.md`（含 YAML frontmatter：`name`/`description`/`triggers`/`requires_tools`），把 `description + triggers` 嵌入到 `__copilot__.skills(name TAG, description, triggers, requires_tools, path, body, embedding VECTOR(384))`，并维护 `skills_state` 做 mtime/fingerprint 增量同步。
  - 新增 `CopilotSkillsIngestionService`（`BackgroundService`）：服务端启动时按 `Copilot.Skills.AutoIngestOnStartup` 自动执行一次技能库摄入，未就绪 / 未启用则安全跳过。
  - HTTP 端点：`POST /v1/copilot/skills/reload`（仅 server admin 触发增量摄入）；`POST /v1/copilot/skills/search` 走向量召回；`GET /v1/copilot/skills/list` 列出全部技能；`GET /v1/copilot/skills/{name}` 读取完整 markdown body；与 `/v1/copilot/docs/*` 一致地在 `Copilot.Embedding` 未就绪时返回 `503`。
  - MCP 工具：在 `/mcp/{db}` 上新增只读 `skill_search(query, k=5)` 与 `skill_load(name)`，结构化返回技能元数据与完整正文，方便 Agent 在对话开始时按问题召回少量技能并装配进上下文。
  - CLI：`sndb copilot skills [reload|list|show <name>]`，复用 `SONNETDB_COPILOT_URL` / `SONNETDB_COPILOT_TOKEN` 环境变量。
  - 首批入库 6 个技能：`query-aggregation` / `pid-control-tuning` / `forecast-howto` / `troubleshoot-slow-query` / `schema-design` / `bulk-ingest`，覆盖聚合、控制整定、预测、慢查询排查、Schema 设计与批量导入场景。

- **PR #64 — Copilot 文档摄入管线 + Knowledge 库（Milestone 14 第二切片）**
  - 新建系统级嵌入式 `Tsdb` 实例 `__copilot__`（按需创建），自动创建 `docs(time, source TAG, section TAG, title TAG, content STRING, embedding VECTOR(384))` measurement，dogfooding Milestone 13 的 `VECTOR(384)` + `knn(...)` 召回。
  - 新增 `DocsSourceScanner` / `DocsChunker` / `DocsIngestor` / `DocsSearchService`：扫描 `docs/*.md` 与 `web/admin/help/`，按 H2/H3 切片（≤ 800 字 / 100 字 overlap） → 嵌入 → 批量入库；`mtime` + 内容哈希做增量识别，避免重复嵌入。
  - 新增 `CopilotDocsIngestionService`（`BackgroundService`）：服务端启动时按 `Copilot.Docs.AutoIngestOnStartup` 自动执行一次摄入，未就绪 / 未启用则安全跳过。
  - HTTP 端点：`POST /v1/copilot/docs/ingest`（仅 server admin）触发增量摄入；`POST /v1/copilot/docs/search` 走向量召回返回命中片段；两者均在 `Copilot.Embedding` 未就绪时返回 `503`。
  - MCP 工具：在 `/mcp/{db}` 上新增只读 `docs_search(query, k=5)`，返回结构化的命中片段（source / section / title / content / score）。
  - CLI：`sndb copilot ingest [--root ./docs]... [--endpoint] [--token] [--force] [--dry-run]`，通过 `SONNETDB_COPILOT_URL` / `SONNETDB_COPILOT_TOKEN` 环境变量便捷接入远端服务端。

- **PR #63 — `SonnetDB.Copilot` 命名空间骨架 + Embedding/Chat Provider 抽象（Milestone 14 第一切片）**
  - 新增 `SonnetDB.Copilot` 命名空间（位于现有 `src/SonnetDB/` 项目内，不新建项目）：`IEmbeddingProvider` / `IChatProvider` 抽象、`CopilotOptions` 配置模型与 DI 装配。
  - 提供两类 provider 骨架：`LocalOnnxEmbeddingProvider`（默认 `bge-small-zh-v1.5`，模型缺失时返回未就绪而非抛异常）与 `OpenAICompatibleEmbeddingProvider` / `OpenAICompatibleChatProvider`（兼容 OpenAI / Azure OpenAI / DashScope / 智谱 / Moonshot / DeepSeek / SiliconFlow / 火山方舟等任意 OpenAI-compat 网关）。
  - 配置节 `SonnetDBServer__Copilot__*`：`Enabled` / `Embedding.Provider` / `Embedding.Endpoint` / `Embedding.ApiKey` / `Embedding.Model` / `Chat.*`，支持环境变量与 `appsettings.json` 同时配置。
  - `/healthz` 输出新增 `copilot` 子节，暴露 `enabled` / `embedding_ready` / `chat_ready` 与诊断原因，便于上层判定是否启用 Copilot 流程；不接入任何业务流程，纯骨架不破坏既有功能。

- **PR #62 — 向量召回基准骨架（Milestone 13 第七切片）**
  - `tests/SonnetDB.Benchmarks` 新增 `VectorRecallBenchmark`，覆盖 SonnetDB 自身 `384-dim` 向量的 brute-force Top10、HNSW Top10 与平均 `Recall@10`。
  - 默认档位为 `10k / 100k`；设置环境变量 `SONNETDB_VECTOR_BENCH_INCLUDE_1M=1` 后可额外启用 `1M` 数据集，避免日常基准意外占满内存。
  - `HnswVectorBlockIndex` 新增直接基于连续 `float32` 向量 payload 建图的重载，减少基准场景为构图额外复制 `DataPoint[]` 的内存开销。
  - `tests/SonnetDB.Benchmarks/README.md` 与根 `README.md` 已补回 `10k / 100k` 两档实测耗时；`1M` 档位保留为显式长测入口，`sqlite-vec` / `pgvector` 同机粗略对比在 README 中保留结果区，后续如具备环境可单独补数。

- **PR #61 — HNSW 段内 ANN sidecar 索引（Milestone 13 第六切片）**
  - `CREATE MEASUREMENT` 新增向量索引声明语法：`embedding FIELD VECTOR(384) WITH INDEX hnsw(m=16, ef=200)`；AST、`SqlParser`、`SqlExecutor`、`MeasurementSchema` 与 `MeasurementSchemaCodec` 已贯通，schema 文件格式升级到 v3 并兼容读取 v1/v2。
  - 新增 `VectorIndexDefinition` / `HnswVectorIndexOptions` 元数据模型，按列持久化 HNSW 参数 `m` / `ef`，并在 schema 校验阶段拒绝“非 VECTOR 列声明索引”或非法参数组合。
  - `SegmentWriter` 在 flush / compaction 写段时，按 schema 为声明了 HNSW 索引的 `VECTOR` block 生成 `.SDBVIDX` sidecar；`SegmentReader.Open` 会自动探测并加载 sidecar，段文件本体 `.SDBSEG` 保持不变。
  - 新增 `HnswVectorBlockIndex` 与 `SegmentVectorIndexFile`：实现段内 HNSW 图构建、sidecar 序列化/反序列化，以及 block 级 ANN 搜索入口。
  - `KnnExecutor` 现可在 `cosine` 度量下优先走 `.SDBVIDX` ANN；对部分时间窗会先做 ANN 候选过滤，不足以覆盖 Top-K 时再自动回退精确扫描，sidecar 缺失时仍保持 brute-force 行为。
  - 新增测试覆盖：`SqlParserVectorTests`、`MeasurementSchemaVectorTests`、`SqlExecutorVectorTests`、`VectorSegmentTests`、`SqlExecutorKnnTests`，覆盖索引语法解析、schema 持久化、sidecar 写读与 flush 后 KNN 查询闭环。

- **PR #60 — `knn(...)` 表值函数：brute-force KNN 向量检索（Milestone 13 第五切片）**
  - 新增内置表值函数 `knn(measurement, column, query_vector, k[, metric])`，支持 `SELECT * FROM knn(...)` SQL 语法，返回 `(time, distance, ...tags, ...fields)` 结果集。
  - 支持三种距离度量：`'cosine'`（余弦距离，默认）、`'l2'`（欧几里得距离）、`'inner_product'`（负内积），每种度量均支持多个别名（`cosine_distance` / `euclidean` / `dot` / `ip` 等）。
  - `KnnExecutor` 实现：段级时间窗剪枝 + `Parallel.ForEach` 多序列并行扫描（MemTable + 全量 Segment），扫描结果按距离升序排列后取前 k 条。
  - `WHERE` 子句同时支持 tag 等值过滤与 `time` 时间范围过滤，缩减召回范围。
  - 新增文档 `docs/vector-search.md`，包含 Schema 设计、写入、查询语法、度量说明与嵌入式 API C# 示例。
  - 新增测试 `SqlExecutorKnnTests`（14 个用例），覆盖余弦/L2/负内积排序、k 大于数据量、tag 过滤、时间过滤、多字段输出、错误边界等路径。

- **PR #59 — 向量距离函数与 `centroid` 聚合（Milestone 13 第四切片）**
  - `FunctionRegistry` 新增 4 个向量标量函数：`cosine_distance(a,b)`、`l2_distance(a,b)`、`inner_product(a,b)`、`vector_norm(a)`；均支持 `VECTOR(dim)` 列与 SQL 向量字面量 `[v0, v1, ...]` 直接混合计算，并在参数为 `NULL`、零向量或维度不一致时给出明确错误。
  - `SqlLexer` / `SqlParser` 新增 PostgreSQL/pgvector 兼容运算符 `<=>`、`<->`、`<#>`，语法层会分别重写为 `cosine_distance(...)`、`l2_distance(...)`、`inner_product(...)` 函数调用，因此现有 SELECT 标量函数执行路径与列依赖分析无需额外分支即可复用。
  - 扩展聚合新增 `centroid(vec)`：按维度累计向量和并在最终阶段输出 `float[]` 均值；`IAggregateAccumulator` / `SelectExecutor` 同步扩展了向量累加入口，使 `GROUP BY time(...)` 与跨桶/跨段合并场景都能复用同一套聚合实现。
  - 测试补齐：新增/扩展 `FunctionRegistryTests`、`ExtendedAggregateAccumulatorTests`、`SqlLexerTests`、`SqlParserVectorTests`、`SqlExecutorVectorTests`，覆盖函数注册、pgvector 运算符改写、标量执行、`centroid` 聚合与维度校验路径。

- **InfluxDB 2.x 数据准确性对照测试项目**
  - 新增独立测试项目 `tests/SonnetDB.Accuracy.Tests`，通过 Testcontainers 启动 InfluxDB 2.7 容器，同时在进程内启动 SonnetDB Server，向两侧写入同一批 Line Protocol 测试数据。
  - 新增准确性对照 fixture：自动创建 SonnetDB 数据库与 measurement schema，复用同一份 LP 数据分别写入 SonnetDB 批量入库端点与 InfluxDB `/api/v2/write`，用于验证服务端真实入库与查询链路，而不是仅验证进程内对象。
  - 新增结果归一化器与对照矩阵，覆盖 `SHOW MEASUREMENTS`、多 series 原始投影、稀疏字段查询、`LIMIT/OFFSET`、`sum/avg/min/max/count/first/last`、`GROUP BY time(...)` 桶聚合以及第二 measurement 的原始查询。
  - 对照测试会把 SonnetDB SQL NDJSON 结果与 InfluxDB Flux 结果统一归一化后逐行比较，减少时间格式、浮点表示或列序差异带来的噪音；当 Docker / InfluxDB 环境不可用时按 skip 处理，不阻塞普通无容器环境下的基础测试运行。

- **PR #58 c — `BlockEncoding.VectorRaw` + Segment Header v3 升级（Milestone 13 第三切片）**
  - `BlockEncoding` 新增 `VectorRaw = 4`：与 `DeltaValue` 互斥的值编码标志；payload 为 `count × dim × float32(LE)` 紧凑序列，dim 由列 schema 携带（不进 BlockHeader）。
  - `TsdbMagic.SegmentFormatVersion`：`2 → 3`（**写入版本**），新增 `TsdbMagic.SupportedSegmentFormatVersions = [2, 3]` 用于读时兼容。BlockHeader 大小保持 72B 不变，旧 v2 段（无 Vector 列）仍可被新 Reader 直接打开。
  - `SegmentHeader` / `SegmentFooter` 新增 `IsCompatibleForRead()`：magic 校验 + version ∈ `SupportedSegmentFormatVersions`；`IsValid()` 仍是严格相等（写时校验）。
  - `SegmentReader.Open` 改为按 `IsCompatibleForRead` 校验头/尾，错误信息升级为提示当前支持的版本列表；`ValueEncoding` 抽取支持 `VectorRaw` 标志位。
  - `SegmentWriter.WriteOneBlock`：`FieldType.Vector` 走 V1 raw 路径并在 `BlockHeader.Encoding` 上置 `VectorRaw`，禁止与 V2 `DeltaValue` 同时使用。
  - `ValuePayloadCodec`：新增 `MeasureVectorPayload` / `WriteVectorPayload`；要求同一 block 内所有点维度一致（不一致抛 `InvalidOperationException`）。
  - `BlockDecoder`：`ReadValues` / `ReadValuesRange` 新增 `FieldType.Vector` 分支；按 `bytesPerPoint = totalBytes/count`、`dim = bytesPerPoint/4` 反序列化，每点拷贝出独立 `float[]` 以 `FieldValue.FromVector` 包装。
  - 测试：新增 `VectorSegmentTests` × 8（payload 度量 / LE 字节序 / 维度不一致抛异常 / 段 round-trip / 版本常量 / `IsCompatibleForRead` v2+v3 接受、其它拒绝 / Reader 接受 v2 段）；`SonnetDB.Core.Tests` 1554 全绿，`SonnetDB.Tests` 116、`SonnetDB.Accuracy.Tests` 8 全绿。
  - 兼容性：v2 段仅可被 v3 Reader 读取（v2 Reader 无法读 v3）。Vector 列至此 WAL → MemTable → Segment 持久化链路打通；查询 / 索引 / KNN 相关能力将由 PR #59 起继续。

- **PR #58 b — Schema VECTOR(dim) 列声明 + SQL `[v0, v1, ...]` 字面量（Milestone 13 第二切片）**
  - `MeasurementColumn` 新增可选 `int? VectorDimension`；`MeasurementSchema.Create` 校验：Vector 列必须 `Field` 角色且 `dim > 0`，非 Vector 列禁止携带 dim。
  - SQL 解析层：新增 `KeywordVector` / `LeftBracket` / `RightBracket` token；`SqlLexer` 识别 `vector` 关键字与 `[` / `]` 标点；`SqlDataType.Vector` + `ColumnDefinition.VectorDimension` + `VectorLiteralExpression(IReadOnlyList<double>)` AST。
  - `SqlParser`：`ParseColumnDefinition` 支持 `<col> FIELD VECTOR(N)` 语法（N ∈ [1, int32]）；`ParsePrimary` 支持 `[a, b, c]` 字面量（拒绝空 `[]`，组件接受可选 `+/-` 前缀的整数 / 浮点）。
  - `SqlExecutor`：`CREATE MEASUREMENT` 透传 dim 至 schema；`INSERT` 在 Vector 列上要求 `[..]` 字面量并校验维度匹配（错误信息含 `维度不匹配`），非 Vector 列拒绝向量字面量；`DESCRIBE` 输出 `vector(N)`；`MapType` 新增 `SqlDataType.Vector → FieldType.Vector` 映射。
  - `MeasurementSchemaCodec` v1 → **v2**：在 `Vector` 列的类型字节后追加 4 字节 little-endian `dim`；读取兼容 v1（仅当文件中无 Vector 列时），v1 文件含 Vector 列则抛 `InvalidDataException`。`measurements.tslschema` 文件版本号字段同步升级为 2。
  - `MemTableSeries.ComputeEstimatedBytes`：新增 `FieldType.Vector` 分支，按 `16 + dim*4` 估算每点常驻字节。
  - `SelectExecutor.UnboxFieldValue`：Vector → `float[]`，便于 `SELECT embedding` 直接吐出数组。
  - 测试：新增 26 个测试（`SqlParserVectorTests` × 12、`SqlExecutorVectorTests` × 7、`MeasurementSchemaVectorTests` × 7）；总计 **1662 测试全部通过**。
  - 兼容性：仍未升级 `FileHeader.Version`（Segment 编码层尚未涉及）；Vector 数据落 WAL → MemTable → flush 走现有路径，落 segment 暂不支持，将随 PR #58 c 引入 `BlockEncoding.VectorRaw` + `FileHeader v3`。
- **PR #58 a — `FieldValue.Vector` 与 WAL `WritePoint` Vector 编解码（Milestone 13 第一切片）**
  - 新增 `FieldType.Vector = 5`：定长 32 位浮点向量，dim 由后续 schema 声明，WAL 内按 `dim(4) + dim×float32(LE)` 排布。
  - `FieldValue` 新增 `Vector` 分支：`FromVector(ReadOnlyMemory<float>)` / `FromVector(float[])` 工厂；`AsVector()` / `VectorDimension` 取值；`Equals` 全量序列比较，`GetHashCode` 采样首/中/末三分量；`ToString` 形如 `vector(N)[a,b,...]`，超过 8 维自动截断。
  - `WalPayloadCodec`：`MeasureWritePoint` / `WriteWritePointPayload` / `ReadWritePointPayload` 三处支持 `FieldType.Vector`；新增 `ReadVectorPayload`，对 `valueLen != 4 + dim*4`、`dim < 1` 等坏 payload 抛 `InvalidDataException`。
  - 兼容性：`FileHeader.Version` 暂未升级（本切片只动 WAL `WritePoint` 序列化层；Schema/Segment 层尚未声明 Vector 列，因此现有 segment 文件格式完全不变）。Schema VECTOR(dim) 列、`BlockEncoding.VectorRaw`、SQL 字面量与 `FileHeader` v3 升级将随 PR #58 后续切片合入。
  - 测试：`FieldValueTests` 新增 13 个用例（Vector round-trip / Equals / dim 不等 / `AsDouble` 抛异常 / `TryGetNumeric=false` / ToString 截断）；`WalPayloadCodecTests` 新增 4 个 dim variant（1 / 3 / 8 / 384）的 WritePoint round-trip。全量回归 1520 + 116 = 1636 通过。

- **服务端内建 MCP（Model Context Protocol）只读入口**
  - `src/SonnetDB` 新增基于官方 `ModelContextProtocol.AspNetCore` 1.2.0 的 Streamable HTTP MCP 端点：`/mcp/{db}`。启用 `Stateless=true`，关闭 legacy SSE，`ConfigureSessionOptions` 会把当前数据库名写入 `ServerInstructions`，明确这是绑定到单个数据库的只读 SonnetDB MCP 会话。
  - 新增 MCP 上下文解析与预校验：所有 `/mcp/{db}` 请求在进入 MCP SDK 前先复用现有 `TsdbRegistry` 校验数据库名与存在性；非法库名返回 `400 bad_request`，不存在数据库返回 `404 db_not_found`，并把当前 `db` 与 `Tsdb` 实例缓存到 `HttpContext.Items` 供 tools/resources 读取。
  - 新增只读 MCP tools：
    - `query_sql(sql, maxRows)`：仅允许 `SELECT` / `SHOW MEASUREMENTS` / `SHOW TABLES` / `DESCRIBE [MEASUREMENT]`；对 `SELECT` 在 AST 层自动补/收紧分页，并采用“多抓 1 行”检测 `truncated`。
    - `list_measurements(maxRows)`：返回当前数据库 measurement 名列表。
    - `describe_measurement(name)`：返回指定 measurement 的 tag/field schema。
  - 新增 MCP resources：
    - `sonnetdb://schema/measurements`
    - `sonnetdb://schema/measurement/{name}`
    - `sonnetdb://stats/database`
    三个资源统一返回 `application/json` 文本，分别暴露 measurement 列表、单 measurement schema 与当前数据库统计（measurement 数、segment 数、memtable 点数、checkpoint LSN 等）。
  - 新增 `src/SonnetDB/Mcp/` 实现层：结果 DTO、`JsonElementValue` 转换、只读 SQL 裁剪逻辑、`CallToolResult`/`TextResourceContents` 构造与基于 `IHttpContextAccessor` 的数据库绑定上下文解析。
  - 测试：新增 `tests/SonnetDB.Tests/McpEndToEndTests.cs`，通过真实 Kestrel + `McpClient` 覆盖 `list_tools`、`query_sql` 自动截断、`list_measurements`、`describe_measurement`、`list_resources` / `list_resource_templates` / `read_resource` 的端到端路径。
  - 权限模型补齐：`/mcp/{db}` 与其余数据库作用域 HTTP 入口现在会把“动态用户 token”映射到 `GrantsStore` 的数据库级 `Read/Write/Admin` 权限；静态 `ServerOptions.Tokens` 仍保持全局 role 语义。无 grant 的用户访问数据库作用域 MCP / SQL / schema / bulk 端点将返回 `403 forbidden`，superuser 保持全放行。
  - 数据库可见性补齐：`GET /v1/db` 与数据面 SQL 中的 `SHOW DATABASES` 现在会按当前请求可见范围过滤数据库列表。普通动态用户只会看到自己有 `Read/Write/Admin`（含 `*` 通配）权限的数据库；静态全局 token 与 superuser 继续看到全部数据库。
  - AI 权限补齐：`POST /v1/ai/chat` 在 `mode=sql_gen` 且携带 `db` 时，现在会先校验数据库名、数据库存在性与当前请求对该数据库的 `Read` 权限，再拼接 schema 系统提示词；未授权用户无法再借助 AI SQL 生成读取其他数据库的 measurement/column 元数据。
  - SSE 权限补齐：`GET /v1/events` 对动态用户 token 的数据库相关事件现在按 grant 实时过滤。`db` 与 `slow_query` 只会下发当前用户对该数据库具备 `Read` 以上权限的事件，控制面慢查询（`__control`）对普通动态用户隐藏；`metrics` 事件中的 `databases` 与 `perDatabaseSegments` 也会裁剪为当前用户可见数据库集合，避免从实时事件流泄露未授权数据库名。
  - 控制面自服务权限补齐：普通动态用户现在可以通过 `/v1/sql` 或有权访问的 `/v1/db/{db}/sql` 执行 `SHOW GRANTS` / `SHOW TOKENS` / `ISSUE TOKEN FOR <self>` / `REVOKE TOKEN '<self-token-id>'` 等“只操作自己”的控制面语句；对其他用户的授权或 token 执行查询、签发、吊销将返回 `403 forbidden`。`SHOW USERS`、用户管理、授权管理、数据库管理等仍保持 admin-only。

- **元数据 SQL：`SHOW MEASUREMENTS` / `SHOW TABLES` / `DESCRIBE [MEASUREMENT] <name>`**
  - 新增 AST 节点 `ShowMeasurementsStatement` 与 `DescribeMeasurementStatement`；`SqlLexer` 增加关键字 `MEASUREMENTS` / `TABLES` / `DESCRIBE` / `DESC`；`SqlParser` 在 `SHOW` 分支识别 `MEASUREMENTS` 和兼容别名 `TABLES`，并新增顶层 `DESCRIBE` / `DESC` 入口（关键字 `MEASUREMENT` 可省略）。
  - `SqlExecutor` 新增 `ShowMeasurements(Tsdb)` 与 `DescribeMeasurement(Tsdb, name)` 执行路径，统一返回 `SelectExecutionResult`：`SHOW MEASUREMENTS` / `SHOW TABLES` 输出单列 `name`（按字典序升序）；`DESCRIBE` 输出三列 `column_name` / `column_type`（`tag` / `field`）/ `data_type`（`float64` / `int64` / `boolean` / `string`），按 schema 声明顺序返回。
  - 引入 `SHOW TABLES` / `DESC` 兼容别名以适配 DBeaver / DataGrip / 通用 ADO.NET schema 浏览器。
  - 测试：新增 `tests/SonnetDB.Core.Tests/Sql/SqlExecutorMetadataTests.cs`，11 个用例覆盖空库 / 字典序排序 / `SHOW TABLES` 等价 / `DESCRIBE`+`DESC` 等价 / 关键字 `MEASUREMENT` 可省略 / 不存在 measurement 抛 `InvalidOperationException` / Parser AST 形状校验。

- **数据面分页：`OFFSET/FETCH` + `LIMIT` 兼容语法**
  - `SELECT` 新增可选分页子句：支持 SQL 标准风格 `OFFSET <n> [ROW|ROWS] FETCH FIRST|NEXT <m> ROW|ROWS ONLY`，以及兼容风格 `LIMIT <m> [OFFSET <n>]`。
  - AST `SelectStatement` 增加 `Pagination` 参数，执行层在最终结果集统一应用分页切片，覆盖 raw / aggregate / TVF 三条路径。
  - 为避免与聚合函数 `first(...)` 冲突，`FIRST/NEXT/ROW/ROWS/ONLY` 按普通标识符词法处理，仅在 `FETCH` 子句按上下文识别。
  - 测试：补充 parser / lexer / executor 用例，覆盖 `LIMIT`、`OFFSET`、`FETCH` 语义以及越界 offset 返回空集。

- **Milestone 12 — PR #57：函数族基准 + README 函数支持矩阵**
  - 新增 `tests/SonnetDB.Benchmarks/Benchmarks/FunctionBenchmark.cs`：以 50,000 个数据点为样本，对 PR #50 ~ #56 引入的窗口 / 聚合 / TVF 函数族走完整 SqlParser → SqlExecutor 流水线的端到端基准。覆盖 SonnetDB 自身的 `derivative` / `moving_average` / `ewma` / `holt_winters` / `anomaly(zscore)` / `p99` / `distinct_count` / `forecast(linear)` / `forecast(holt_winters)` 9 项基线，以及 InfluxDB Flux（`derivative` / `movingAverage` / `holtWinters` / `quantile(method:"estimate_tdigest")`）与 TDengine REST（`DERIVATIVE` / `MAVG` / `PERCENTILE`）的等价语义对照；外部数据库不可用时按 `[SKIP]` 提示，不阻塞 SonnetDB 基线运行。
  - `README.md` 新增「支持的 SQL 函数」矩阵章节：按 PR 引入顺序枚举 PR #50 ~ #56 全部内置函数（聚合 / 标量 / 窗口 / TVF）共 50+ 项，并列出 InfluxDB / Timescale / TDengine / Prometheus 的对标函数与备注；同步在 `README.en.md` 增加「Built-in SQL functions」英文版矩阵。
  - 矩阵章节同时指向 `docs/extending-functions.md`（UDF 注册）与 `tests/SonnetDB.Benchmarks/Benchmarks/FunctionBenchmark.cs`（性能对照），使函数体系的「能做什么 / 怎么扩展 / 性能如何」三条线索从 README 一处可达。

- **Milestone 12 — PR #56：Tier 5 用户自定义函数（UDF）注册 API**
  - 新增公开类型 `SonnetDB.Query.Functions.UserFunctionRegistry`：按 `Tsdb` 实例隔离的 UDF 注册表，挂在新增的 `Tsdb.Functions` 属性上。提供 `RegisterScalar(name, evaluator, min, max)` / `RegisterScalar(IScalarFunction)` / `RegisterAggregate(IAggregateFunction)` / `RegisterWindow(IWindowFunction)` / `RegisterTableValuedFunction(name, executor)` 五条注册路径，以及 `Unregister(name)` 与 `TryGet*` 查询。聚合 UDF 强制 `LegacyAggregator == null`（仅内置 7 个聚合可用 legacy fast-path）；TVF UDF 不允许使用保留名 `forecast`。
  - 通过 `AsyncLocal<UserFunctionRegistry?>` + `UserFunctionRegistry.AmbientScope` 提供查询作用域 ambient；`SqlExecutor.ExecuteSelect` 在执行前 `EnterScope(tsdb.Functions)`、退出时自动恢复，确保多 `Tsdb` 实例并发执行 SQL 时互不可见。
  - `FunctionRegistry.GetFunctionKind` / `TryGetAggregate` / `TryGetScalar` / `TryGetWindow` 全部改为「优先查 ambient UDF，未命中再回退内置」，保持 PR #50~#55 的所有内置函数行为零变化。`TableValuedFunctionExecutor.Execute` 同样优先匹配用户 TVF，再走内置 `forecast` 路由。
  - 新增 `TsdbOptions.AllowUserFunctions` 选项（默认 `true`，嵌入式启用）；`SonnetDB.Hosting.TsdbRegistry` 在两条 `Tsdb.Open` 调用上将其设为 `false`，从而 Server / HTTP 模式默认禁用 UDF 以保证 AOT 兼容（`Functions.IsEnabled == false`，所有 `Register*` 抛 `InvalidOperationException`）。
  - 新增 `docs/extending-functions.md`：覆盖标量 / 聚合 / 窗口 / TVF 四类 UDF 的注册示例（`Func` 委托形态 + `IScalarFunction` 等接口形态）、`Merge` 可结合性约束、跨实例隔离、`forecast` 保留名、Server 模式禁用策略与 UDF 不覆盖的功能边界。
  - 新增测试：`tests/SonnetDB.Core.Tests/Query/Functions/UserFunctionRegistryTests.cs`（9 项端到端）：委托标量 UDF 解析、UDF 覆盖同名内置（`abs` 路径）、聚合 UDF 通过 `IAggregateAccumulator` 接入 SELECT、TVF UDF 路由 + 行集构造、TVF 保留名 `forecast` 拒绝、`Unregister` 后查询失败、`AllowUserFunctions=false` 时 Register 全部抛、ambient 在两个 `Tsdb` 实例间隔离、聚合 UDF 设置 `LegacyAggregator` 时拒绝注册。

- **Milestone 12 — PR #55：Tier 4 Forecast TVF + 异常 / 变点检测**
  - 新增公开类型 `SonnetDB.Query.Functions.Forecasting.TimeSeriesForecaster`：纯 C#、零外部依赖的预测库 API，提供 `Forecast(long[] timestampsMs, double[] values, int horizon, ForecastAlgorithm algorithm, int season = 0)`，输出 `ForecastPoint[] (TimestampMs, Value, Lower, Upper)`；支持 **线性最小二乘外推** 与 **Holt / Holt-Winters 三次指数平滑（加性季节）**，置信区间按残差 RMSE × $z_{0.975}$ × $\sqrt{h+1}$ 给出。
  - 新增 SQL 表值函数 `forecast(measurement, field, horizon, 'algo'[, season])`：在 `FROM` 子句中作为数据源，按 measurement / FIELD 拉取历史数据并按 series 维度独立预测；输出列 `(time, value, lower, upper, ...tag_columns)`。Parser 在 `ParseSelect()` 中识别 `FROM <ident>(` 调用形态并填充 `SelectStatement.TableValuedFunction`；新增 `TableValuedFunctionExecutor` 路由器：校验参数（measurement / FIELD 标识符、`horizon` 正整数字面量、`'linear'` / `'holt_winters'` / `'hw'` 算法、可选 `season` 非负整数），复用 `WhereClauseDecomposer` 处理标签过滤，按 series 调用 `TimeSeriesForecaster.Forecast` 并按预测点落行。PR #129.2 起支持直接列投影。
  - 新增 SQL 窗口函数 `anomaly(field, 'zscore' | 'mad' | 'iqr', threshold)`：行流→行流，输出 `bool?`；`zscore` 用样本标准差（N−1），`mad` 用 1.4826 × MAD 鲁棒尺度，`iqr` 用 0.25 / 0.75 分位线性插值 + Tukey $k$ 倍 IQR 围栏。`null` 输入透传 `null`。
  - 新增 SQL 窗口函数 `changepoint(field, 'cusum', threshold[, drift])`：双边 CUSUM 累积和变点检测；用前 `max(5, n/4)` 个非空样本估计基线均值与样本标准差以避免变点本身污染参考；触发后累积器复位以探测下一个变点。`drift` 默认 `0.5`。
  - `FunctionRegistry` 注册 `anomaly` 与 `changepoint` 为 Tier 3 窗口函数，与 PR #53 框架共享 `IWindowFunction` / `IWindowEvaluator` 协议。
  - 新增 `docs/forecast.md`：完整覆盖 SQL 语法、输出列、算法公式、嵌入式库 API、局限与与 UDF 扩展的关系。
  - 新增测试：`tests/SonnetDB.Core.Tests/Query/Functions/TimeSeriesForecasterTests.cs`（7 项算法层单测：线性外推方向 / 截距、Holt-Winters 季节恢复、置信区间宽度、退化输入、空 / 单点输入边界）+ `AnomalyChangepointFunctionTests.cs`（8 项窗口函数单测：z-score / MAD / IQR 各方法、`null` 输入、常数序列、CUSUM 检测均值漂移并触发后复位）+ `tests/SonnetDB.Core.Tests/Sql/SqlExecutorForecastTests.cs`（SQL 端到端：线性 / HW 预测列形态、与 WHERE 标签过滤组合、参数校验、forecast 列投影、anomaly / changepoint 输出 bool 列）。

- **Milestone 12 — PR #54：Tier 4 PID 控制律内置函数 + 自动整定库 API**
  - 新增公开类型 `SonnetDB.Query.Functions.Control.PidController`：纯 C# 离散 PID 状态机，提供 `Update(timestampMs, processVariable, setpoint)`、`Snapshot()` / `Restore(...)` 与 `Reset()`；首行只输出比例项（无 dt 参考），$\Delta t \le 0$ 时跳过 I/D 更新避免发散；状态结构 `PidControllerSnapshot { Integral, PrevError, PrevTimeMs, HasHistory }`。
  - 新增 SQL 行级窗口函数 `pid_series(field, setpoint, kp, ki, kd)`（`IWindowFunction`）：与 PR #53 窗口算子框架共享 `Compute(long[] timestamps, FieldValue?[] values)` 协议，按 series 独立维护控制器实例；`null` 输入透传 `null` 输出且不推进状态。
  - 新增 SQL 聚合函数 `pid(field, setpoint, kp, ki, kd)`（`IAggregateFunction` + `PidAccumulator`）：与 `GROUP BY time(...)` 组合时桶内逐行推进、桶尾输出最终 $u(t)$；`Merge` 取 `PrevTimeMs` 更晚的段以支持跨段拼接；拒绝 `pid(*)`、错误参数个数、非 FIELD 列、字符串字段。
  - `IAggregateAccumulator` 增加默认接口方法 `Add(long timestampMs, double value) => Add(value);`；`SelectExecutor.AggSlot.Update` 改为通过该重载传递时间戳，对既有 Welford / TDigest / HLL / Histogram 累加器**零行为变化**。
  - `FunctionRegistry` 注册 `pid` 为 `Aggregate`、`pid_series` 为 `Window`，纳入既有 `GetFunctionKind` / `TryGetAggregate` / `TryGetWindow` 路由。
  - 既有 `PidParameterEstimator`（Sundaresan & Krishnaswamy 35%/85% 两点法识别 FOPDT 模型 + Ziegler-Nichols / Cohen-Coon / Skogestad IMC 三种整定规则）保持库级 API；同时新增 SQL 聚合函数 `pid_estimate(field, method, step_magnitude, initial_fraction, final_fraction, imc_lambda)`，对结果集中 (time, value) 样本调用辨识 + 整定，输出 JSON `{"kp":..,"ki":..,"kd":..}`。`method` 接受字符串字面量 `'zn'` / `'cc'` / `'imc'` 或 NULL（默认 ZN），数值参数允许 NULL 取默认值。`docs/pid-control.md` 提供端到端工作流（采集 → 离线整定 → SQL 回测 → 控制回写 → 监控）与 SQL/库 API 双形态示例。
  - 新增测试：`tests/SonnetDB.Core.Tests/Query/Functions/Control/PidControllerTests.cs`（14 项控制器与累加器/求值器单元测试）+ `tests/SonnetDB.Core.Tests/Sql/SqlExecutorPidFunctionTests.cs`（10 项 SQL 端到端：行级输出、桶级最终 u、与时间/字段投影混合、负增益字面量、参数校验、Tag/字符串列拒绝、控制回写两步流程）。

- **Milestone 12 — PR #53：Tier 3 窗口算子框架（17 个窗口函数）**
  - 新增公共契约 `IWindowFunction` / `IWindowEvaluator`：window 函数为「行流→行流」的逐序列算子，由 `CreateEvaluator(call, schema)` 工厂在查询计划阶段完成参数解析与字段绑定，运行阶段调用 `Compute(long[] timestamps, FieldValue?[] values) → object?[]` 输出与输入等长的列结果。
  - 落地 17 个窗口函数（位于 `src/SonnetDB/Query/Functions/Window/`），按语义分为 5 组：
    - **差分类**：`difference` / `delta`（当前 − 上一行）；`increase`（仅保留正差，counter 重置返回 `null`）；`derivative` / `non_negative_derivative` / `rate` / `irate`（按时间归一化的瞬时变化率，可指定单位 `1s` / `100ms` 等）。
    - **累计类**：`cumulative_sum`（运行总和，首个有效值前为 `null`）；`integral(field [, unit])`（梯形面积，默认按秒积分）。
    - **平滑类**：`moving_average(field, n)`（N 点滑动平均，前 N−1 行返回 `null`）；`ewma(field, alpha)`（指数加权移动平均，校验 `alpha ∈ (0, 1]`）；`holt_winters(field, alpha, beta)`（加性 Holt 双指数平滑，无季节性，校验 `alpha`、`beta` ∈ `(0, 1]`）。
    - **缺失值处理**：`fill(field, value)`（数值字面量填充 `null`，支持 `-1` 等带负号字面量）；`locf(field)`（last observation carried forward）；`interpolate(field)`（两遍扫描线性插值，前导/尾随 `null` 保持 `null`）。
    - **状态分析**：`state_changes(field)`（基于 `FieldValue.Equals` 的状态切换计数，支持 string/bool）；`state_duration(field)`（当前状态持续毫秒数，状态切换时归零）。
  - `FunctionRegistry` 新增 `WindowFunctions` 集合 + `TryGetWindow(name, out function)` API；`GetFunctionKind` 新增 `FunctionKind.Window` 分支。
  - `SelectExecutor` 新增 `ProjectionKind.Window` 投影类别：`ClassifyProjections` 将 `Window` 函数路由到该类别；`ExecuteRaw` 在每个 series 内构造 `long[] timestamps` + 与之对齐的 `FieldValue?[]`，预计算所有 window evaluator 输出后按行下标注入结果，与 `time` / `tag` / `field` / `scalar` 投影任意组合。窗口函数与聚合函数互斥（沿用既有 `_ → 内部错误` 拒绝路径）。
  - 新增 `tests/SonnetDB.Core.Tests/Query/Functions/WindowFunctionTests.cs`（35 项，覆盖各 evaluator 的纯算法语义、`Welford` / counter 重置 / 梯形积分 / 线性插值 / EWMA 递推 / 状态分析等基线，外加全部 17 个函数的 `FunctionRegistry` 注册校验）与 `tests/SonnetDB.Core.Tests/Sql/SqlExecutorWindowFunctionTests.cs`（21 项 SQL 端到端，覆盖各窗口函数典型用法、与 `time` / `field` / `tag` 投影混合、与聚合互斥、参数错误、字符串/Tag 字段拒绝等）。

- **Milestone 12 — PR #52：Tier 2 扩展聚合（可合并累加器）**
  - 新增 9 个扩展聚合函数：`stddev` / `variance` / `spread` / `mode` / `median` / `percentile(field, q)` / `p50` / `p90` / `p95` / `p99` / `tdigest_agg` / `distinct_count` / `histogram(field, bin_width)`，全部走新增的 `IAggregateAccumulator` 路径，与既有 7 个 legacy 聚合并存、零性能回归。
  - 新增公共契约 `IAggregateAccumulator`（`Count` / `Add` / `Merge` / `Finalize`）与 `IAggregateFunction.CreateAccumulator(call, schema)`（默认实现返回 `null`），用于让扩展聚合声明跨段、跨桶、跨序列可合并的中间状态。
  - 新增三类核心累加器算法（位于 `src/SonnetDB/Query/Functions/Aggregates/`）：
    - **Welford** 在线方差/标准差，附 Chan-Golub-LeVeque 并行合并公式；样本数 < 2 时 `stddev` 返回 `null`。
    - **TDigest** 简化的 Ben Haim merging digest（compression=200、k(q) ≈ 4q(1−q)/δ），支持 `Add` / `Merge` / `Quantile` / `Serialize` / `ToJson`，作为 `percentile` / `pXX` / `median` / `tdigest_agg` 的统一后端。
    - **HyperLogLog**（precision 14、16384 寄存器、AlphaMM 修正、小基数 linear-counting），作为 `distinct_count` 的统一后端，使用 `System.IO.Hashing.XxHash64` 哈希双精度浮点的 IEEE 字节序。
  - `SelectExecutor.ExecuteAggregate` 重构为 `AggSlot` 分发：legacy 聚合走原 `BucketState`，扩展聚合走 `IAggregateAccumulator`；`AggSpec` 新增 `IsExtended` / `IsCountStar` 与字段解析支持，`first` / `last` 多序列保护仅作用于 legacy 路径。
  - `histogram(field, bin_width)` 输出 `{"[lo,hi)":n,...}` 格式 JSON，跨段合并时校验 `bin_width` 一致性；`tdigest_agg` 输出可后续合并的 JSON 状态串。
  - 新增 `tests/SonnetDB.Core.Tests/Query/Functions/ExtendedAggregateAccumulatorTests.cs`（14 项单元测试，覆盖 Welford / TDigest / HLL 合并一致性、空集 / 单点边界、参数校验）与 `tests/SonnetDB.Core.Tests/Sql/SqlExecutorExtendedAggregateTests.cs`（13 项 SQL 端到端测试，覆盖单序列、多序列合并、`GROUP BY time(...)` 桶聚合、混合 legacy + 扩展聚合、参数错误）。

- **PR #39：Docker 镜像自动发布**
  - 新增 `.github/workflows/docker-publish.yml`：在 `main` 分支相关文件变更、`v*` 标签或手动触发时，自动构建 `src/SonnetDB/Dockerfile` 并推送镜像。
  - 目标镜像仓库：
    - Docker Hub：`iotsharp/sonnetdb`
    - GHCR：`ghcr.io/<owner>/sonnetdb`
  - 标签策略覆盖 `latest`、`edge`、`vX.Y.Z`、`X.Y` 与 `sha-<commit>`，并写入 OCI labels。
  - 工作流接入 `docker/setup-buildx-action`、`docker/metadata-action`、`docker/login-action`、`docker/build-push-action`，并启用 GitHub Actions Docker layer cache。
- 新增 `docs/releases/docker-image.md`，补齐 Docker 镜像的启动方式、标签策略、自动发布触发条件和仓库 Secrets 要求。

- **PR #37b：GitHub Pages 文档自动发布**
  - 新增 `.github/workflows/docs-pages.yml`：在 `main` 分支文档变更或手动触发时，自动执行 `dotnet tool restore` + `jekyllnet build`，并通过 GitHub Pages 官方 Actions 上传和部署静态文档站点。
  - Pages 构建阶段会基于仓库名动态注入文档基址（例如 `/SonnetDB`），因此无需维护第二套独立文档源码。

- **Milestone 12 — PR #51：Tier 1 标量函数 + SQL 函数调用扩展**
  - 新增公共类型 `ISqlFunction` / `IScalarFunction` / `FunctionKind`，并扩展 `FunctionRegistry` 的 `TryGetScalar(name, out function)` / `ScalarFunctions` / `GetFunctionKind(name)` API；`IAggregateFunction` 改为继承 `ISqlFunction` 共享 `Name` 契约。
  - 落地内置标量函数：`abs` / `round(value[, digits])` / `sqrt` / `log(value[, base])` / `coalesce(...)`；统一在 `BuiltInScalarFunction` 中做参数个数校验，并通过 `RequireDouble` 兼容 byte/int/long/float/double/decimal。
  - `SelectExecutor` 新增 `ProjectionKind.Scalar` 投影路径，支持在 SELECT 投影中嵌套调用、与算术表达式混用，并自动汇总标量函数引用的字段名加入 `QueryFieldValues` 的字段集；为 `Window` / `TableValued` 类别预留诊断分支，便于 PR #53 / #55 接入。
  - **AST 重构（破坏性内部 API）**：`SelectStatement.GroupByTime: TimeBucketSpec?` 替换为 `SelectStatement.GroupBy: IReadOnlyList<SqlExpression>`，由 `SelectExecutor.ResolveGroupByTime` 在执行阶段把 `time(duration)` 形式归约为 `TimeBucketSpec`；`SqlParser.ParseGroupByList` / `ParseGroupByExpression` 取代原 `ParseGroupByTime`，仍在 parser 阶段拒绝 `time(0)` 之类的非法 duration。无 GROUP BY 时 `GroupBy` 为空集合（不再为 `null`）。
  - 新增/扩展测试：`tests/SonnetDB.Core.Tests/Sql/SqlExecutorSelectTests.cs` 覆盖标量函数投影、`coalesce` 跨字段时间轴、别名、未知函数与参数个数错误；`SqlParserTests` 覆盖 `GROUP BY time(...)` 解析为 `FunctionCallExpression` 与标量函数调用解析；`FunctionRegistryTests` 增加 `TryGetScalar` / `GetFunctionKind` 与标量函数求值校验。
  - `docs/sql-reference.md` 补充 SELECT 投影中标量函数的支持范围、嵌套规则与 `coalesce` 时间轴说明。

- **Milestone 12 — 函数注册表基础设施（`FunctionRegistry`）**：新增 `src/SonnetDB/Query/Functions/` 目录，承载 Tier 1~3 函数扩展（PR #51~#57）所需的注册与解析骨架，零第三方依赖。
  - 新增公共类型 `FunctionRegistry`（静态注册表）+ `IAggregateFunction`（命名 / SQL 调用语法校验 / `LegacyAggregator` 桥接），通过 `TryGetAggregate(name, out function)` 与 `GetAggregate(Aggregator)` 双向查找内置 7 个聚合（count/sum/min/max/avg/first/last）。
  - `BuiltInAggregateFunction.ResolveFieldName` 集中实现 `*` 形式校验（仅 `count(*)` 允许）、参数个数校验、列存在性、Tag/Field 角色与 String 类型限制。
  - 重构 `SelectExecutor`：移除内部硬编码 `_aggregateFunctions` HashSet 与 `switch` 解析逻辑，改为统一走 `FunctionRegistry`；保留现有高性能 `Aggregator` 枚举执行路径，本 PR 不影响查询性能。
  - 新增 `tests/SonnetDB.Core.Tests/Query/Functions/FunctionRegistryTests.cs`：8 项单元测试覆盖大小写不敏感、未知函数、`count(*)` 接受 / 其他函数拒绝 `*`、Tag 列拒绝、String 字段拒绝、合法字段返回原列名等关键分支。
  - 新增 `tests/SonnetDB.Core.Tests/Sql/SqlExecutorSelectTests.cs::Select_SumStar_Throws` / `Select_AggregateLookup_IsCaseInsensitive`，验证 SelectExecutor 经注册表后的对外行为不变。

- **Milestone 12 — PID 参数估算（`PidParameterEstimator`）**：新增 `src/SonnetDB/Query/Functions/Control/` 目录，提供从历史阶跃响应时序数据自动推算 PID 控制器参数的纯 C# 实现，零第三方依赖。
  - `PidTuningMethod` 枚举：`ZieglerNichols`（Ziegler-Nichols 阶跃响应法）/ `CohenCoon`（Cohen-Coon 法）/ `Imc`（Skogestad SIMC/IMC 法）。
  - `PidParameters` 记录类型：封装 `Kp`（比例增益）、`Ki`（积分增益）、`Kd`（微分增益）。
  - `PidEstimationOptions`：可选配置项，包含整定方法、阶跃幅度 `StepMagnitude`、IMC 闭环时间常数 `ImcLambda`、初始/末尾稳态窗口比例。
  - `PidParameterEstimator.Estimate`：接受 `IReadOnlyList<(long TimestampMs, double Value)>` 或 `IReadOnlyList<DataPoint>`，采用 **Sundaresan & Krishnaswamy 35%/85% 两点法** 辨识一阶纯滞后（FOPDT）模型（K、τ、θ），再按所选整定规则输出 Kp/Ki/Kd。
  - 新增 `tests/SonnetDB.Core.Tests/Query/Functions/Control/PidParameterEstimatorTests.cs`：20 项单元测试，覆盖三种整定方法的数值精度验证、非零基线、DataPoint 重载、Int64 字段值、边界/错误校验、负向阶跃及三种方法正参数一致性。

### Changed
- `README.md` 与发布文档新增预编译 Docker 镜像入口，支持直接通过 `docker run iotsharp/sonnetdb:latest` 启动服务端。
- `ROADMAP.md` 中 `PR #39` 状态更新为已完成，Milestone 9 随之闭环。

- 文档站点模板与交叉链接支持双部署模式：
  - `docs/_config.yml` 新增 `docs_baseurl`、`app_link_url`、`app_link_text`、`home_primary_url`、`home_primary_text` 配置项。
  - `docs/_layouts/default.html` 与多篇文档内的站内链接改为基于配置拼接，默认继续服务于 `SonnetDB` 的 `/help/`，同时兼容 GitHub Pages 的仓库子路径。
- 将 `ROADMAP.md` 中的 `PR #37b` 标记为已完成，并更新 Milestone 9 的当前推进顺序。

- 核查并修正 `ROADMAP.md` 中的 Milestone 9 状态：
  - 补记已在仓库落地但路线图遗漏的 `PR #36`（`SonnetDB` Docker 基准环境与 `ServerBenchmark`）。
  - 将 `PR #38` 调整为已完成：仓库已具备 `eng/release.ps1`、`.github/workflows/publish.yml`、NuGet 包元数据、SDK / Server Bundle 与 `msi` / `deb` / `rpm` 打包能力。
  - 将原 `PR #37` 拆分为 `#37a`（已完成：文档重写、JekyllNet `/help` 站点、README 与发布文档核对）和 `#37b`（现已完成：GitHub Pages 自动发布流水线），使路线图状态与当前代码一致。

- **PR #50：查询元数据快路径 — Format v2 + 跨桶融合 + MemTable 增量聚合 + ExecuteMany 共享快照**（**Breaking 段文件格式变更**）
  - **Format v2**（破坏性，不兼容旧 segment 文件）：`BlockHeader` 由 64 B 扩展到 72 B，将 `AggregateMinBits` / `AggregateMaxBits` 由 `int`（4 B）升级为 `AggregateMin` / `AggregateMax`（`double`，8 B），覆盖 Float64 任意值与 ±2^53 内的 Int64。新增 `TsdbMagic.SegmentFormatVersion = 2` 常量与 `TsdbMagic.FormatVersion = 1`（用于 FileHeader/WAL/Catalog）解耦；`SegmentHeader.IsValid` 与 `SegmentReader` 拒绝读取 v1 段文件并给出明确升级提示。`SegmentWriter.TryBuildAggregateMetadata` 不再因窄类型截断而放弃 `HasMinMax`，对所有数值字段一律写入 `HasSumCount | HasMinMax`。`SegmentReader` 删除 `DecodeAggregateBoundary` 私有桥接，直接读取 8 B `double`。**升级路径：删除 `*.SDBSEG` 后启动可由 WAL 重放重新生成 v2 段；混用旧版本数据将抛 `SegmentCorruptedException`。**
  - **跨桶 block 元数据下推**：`QueryEngine` 新增 `CanFuseDeltaTimestampInline` + `FuseDeltaBlockToGlobal` / `FuseDeltaBlockToBuckets` 融合内联路径——对 `(delta-of-delta 时间戳 + 原始数值)` 的数值 block，仅租用一份 `ArrayPool<long>` 解码时间戳后内联走 `ReadRawNumericValue` + `AddValueToBucket`，避免 `BlockDecoder.DecodeRange` 物化 `DataPoint[]`。大 block 跨桶聚合分配显著下降。
  - **MemTable 运行期 sum/min/max**：`MemTableSeries` 在 `Append` 阶段对 Float64/Int64/Boolean 增量维护 `_numericSum / _numericMin / _numericMax`，新增 `HasNumericAggregates` / `NumericSum` / `NumericMin` / `NumericMax` / `TryGetNumericAggregateSnapshot` 公共 API；`QueryEngine.ExecuteAggregateFast` 在范围全覆盖且单桶或全局聚合时直接合并 MemTable 元数据，跳过对 `ReadOnlyMemory<DataPoint>` 切片的逐点扫描。`AggregateState` 新增 `AddMemTableAggregate` 合并入口。
  - **ExecuteMany 共享快照**：`QueryEngine.ExecuteMany` 不再每个 series 重建 `BuildReaderMap` 与重新读取 `_segments.Index`，改为外层一次构建并通过新的私有重载 `ExecuteAggregateFast(query, index, readers)` 透传；`ShouldUsePointAggregatePath` 仍按 series 分流到 `Execute` 慢路径以保证 First/Last 与墓碑场景行为不变。
  - **测试**：新增 `MemTableSeriesAggregateTests` 5 项（数值字段增量聚合、String 字段不维护、空序列）+ `QueryEngineV2OptimizationTests` 7 项（Int64 极值/高精度 Float64 round-trip、跨桶大 block 聚合一致性、MemTable 全/部分覆盖路径、`ExecuteMany` 与单次 `Execute` 一致性）；同步更新 `BlockHeaderTests` / `FormatSizesTests` / `SegmentHeaderTests` / `SegmentFooterTests` 以反映 72 B BlockHeader 与 `SegmentFormatVersion = 2`。`SonnetDB.Core.Tests` 1263/1263 + `SonnetDB.Tests` 97/97 通过。

### Changed
- 完善 Block 聚合元数据语义并修复 Min/Max 精度漏洞，新增桶聚合元数据快路径：
  - `BlockHeader.AggregateFlags` 由「0/1 单值」语义改为按位组合：`HasSumCount = 0x01`（sum/count 可信）、`HasMinMax = 0x02`（min/max 无损）。旧 v1 segment 写入的 `1` 自动等价于「仅 HasSumCount」，min/max 不会被误用，无需 Format 版本号变更（仍为 1）。
  - `SegmentWriter` 在 Float64 时只在 `(float)min == min && (float)max == max` 时才置 `HasMinMax`，避免向 `Min`/`Max` 查询返回 float 截断后的错误值；Int64 在 min/max 落入 int32 范围时置 `HasMinMax`；sum/count 仍始终写入。
  - `SegmentReader` 解析两个独立标志，`BlockDescriptor` 新增 `HasAggregateSumCount` / `HasAggregateMinMax`（旧 `HasAggregateMetadata` 改为派生属性）。
  - `QueryEngine.CanUseAggregateMetadata` 改为按聚合函数挑选元数据：`Count` 始终命中（用 `descriptor.Count`），`Sum/Avg` 需 `HasAggregateSumCount`，`Min/Max` 需 `HasAggregateMinMax`。
  - 桶聚合（`AddSegmentBlocksToBuckets`）新增元数据快路径：当 block 完整落入查询范围且 `[MinTimestamp, MaxTimestamp]` 整体落在同一个桶内时，直接合并元数据到桶状态，避免读取 payload 与逐点扫描，写入密集场景对 `SUM/COUNT/AVG/MIN/MAX` 的桶聚合直接获益。
  - 测试：新增 4 项 `QueryEngineAggregateTests`（Float64 非可表示数的 Min/Max 精度、Sum/Avg/Count 仍走快路径、桶 SUM/MIN/MAX 命中元数据、跨桶 block 回退扫描），1 项 `BlockHeaderTests` 校验旧 `flags == 1` 的兼容映射；`SonnetDB.Core.Tests` 1250/1250 + `SonnetDB.Tests` 97/97 通过。
  - 不修改文件二进制格式与 `FileHeader.Version`。

### Added
- 新增跨平台 C# 基准运行入口：`eng/benchmarks/start-benchmark-env/start-benchmark-env.csproj` 负责 Docker Compose 构建、启动、健康等待与停止，`eng/benchmarks/run-benchmarks/run-benchmarks.csproj` 负责调用环境入口并运行 BenchmarkDotNet；根 README 改为嵌入 `docs/assets/benchmark-summary.svg` 基准摘要图，后续刷新性能数字时无需反复改根 README 表格。
- **PR #49：基准刷新 + 对外对比（写入快路径专题收尾）**
  - 「写入：100 万点（单序列）」表新增 PR #47 三条服务端 LP/JSON/Bulk 端点的实测数据（1.10–1.20 s / 34–71 MB），把服务端 vs 嵌入式的写入差距从 ~33.8×（SQL Batch 路径）收敛展示到 **~1.77–1.93×**（绕开 SQL parser 路径）。
  - 「嵌入式 vs. SonnetDB 同机对比」表把写入行拆分为 SQL Batch + LP + JSON + Bulk 四行，标注各自的额外开销与主要来源。
  - 「批量入库快路径」段补充 PR #48 `?flush=` 三档位语义表（None / Async / Sync 与适用场景）。
  - 「小结」最后一行更新为反映 PR #47/#48 后的写入收敛事实。
  - **新增 `InsertBenchmark.TDengine_InsertSchemaless_1M`**：走 TDengine InfluxDB-compat 端点 `POST /influxdb/v1/write?precision=ms`，按 100,000 行/批切片避开 taosadapter 16 MB body 上限；`TDengineRestClient` 配套新增 `WriteLineProtocolAsync(db, lp, precision)`。配合 `bench_insert_schemaless` 隔离 DB，与已有显式 STable 路径互不干扰。
  - **全量重跑 24 个基准**（i9-13900HX / .NET 10.0.6 / Docker WSL2，~20 分钟）真实数字写入 `tests/SonnetDB.Benchmarks/README.md`：
    - InsertBenchmark（1M 点）：SonnetDB **544.9 ms / 530 MB**（1.00×）、SQLite 811.4 ms / 465 MB（1.49×）、InfluxDB 5,222 ms / 1,457 MB（**9.58×**）、TDengine REST INSERT 44,137 ms / 156 MB（81×）、**TDengine schemaless LP 996 ms / 61 MB（1.83×）**〔同库 schemaless 路径比 REST INSERT 子表路径快 44× / 分配缩到 39%〕
    - QueryBenchmark：SonnetDB **6.71 ms / 18.7 MB**，比 InfluxDB 快 61×、比 SQLite 快 6.6×
    - AggregateBenchmark：SonnetDB **42.3 ms / 39.4 MB**，比 SQLite 快 7.7×
    - CompactionBenchmark：SonnetDB **16.3 ms / 28.3 MB**
    - **ServerInsertBenchmark（重建镜像后首次全部跑通）**：SQL Batch `19.80 s / 655 MB`、LP `1.293 s / 52 MB`、JSON `1.352 s / 71 MB`、Bulk VALUES `1.120 s / 34 MB`——PR #47 三端点仍稳定在「秒级 1M 点 + ≤ 80 MB」区间，比 SQL Batch 快 **15–7×** / 分配缩到 **5–11%**、仅比嵌入式多 **~2.0–2.5×**额外开销
    - ServerQuery `88.4 ms / 16 MB`、ServerAggregate `88.8 ms / 2.5 MB`、BulkIngestBenchmark 四个路径均保持 PR #46/#47 后「百万点 / ~110–200 ms / 130–220 MB」区间。

### Changed
- 优化查询热路径：未压缩时间戳 block 的范围裁剪改为直接在 little-endian byte payload 上二分，避免为整块 block 分配 `long[]`；数值聚合在无墓碑且非 `First/Last` 时下推到 Segment block payload 扫描，使用 `ReadOnlySpan<byte>` + `CollectionsMarshal.GetValueRefOrAddDefault` 直接更新桶状态，减少 `DataPoint[]` 物化与托管堆分配。

### Added
- **PR #48：批量入库端点 Flush 三档位 `?flush=false|true|async`（写入快路径专题，第 3/4 步）**
  - `Tsdb` 新增 `public void SignalFlush()`：仅向 `BackgroundFlushWorker` 发信号后立即返回，不阻塞调用方；若未启用后台 Flush（`BackgroundFlush.Enabled = false`），降级为同步 `FlushNow()`，保证 `flush=async` 始终具备「最终一致」语义。
  - `BulkIngestor` 新增 `enum BulkFlushMode { None, Async, Sync }` 与新主重载 `Ingest(tsdb, reader, errorPolicy, BulkFlushMode flushMode)`；旧 `Ingest(..., bool flushOnComplete)` 重载保留，转发到新重载（`true → Sync` / `false → None`），向后兼容现有调用方。
  - `BulkIngestEndpointHandler.ParseFlush` 重写为 `BulkFlushMode` 解析：`async` → Async；`true|sync|yes|1` → Sync；其它（含缺省、`false`）→ None。三个端点 `/lp` / `/json` / `/bulk` 共享同一档位。
  - ADO 嵌入式 `EmbeddedConnectionImpl.ExecuteBulk` 新增 `internal static BulkFlushMode ParseFlushMode(string?)` 并改用 `BulkFlushMode` 透传到引擎；远程 `RemoteConnectionImpl.ExecuteBulk` 自然透传 `flush=` query string，无需改动。
  - 默认行为（缺省 `flush` 参数）维持 PR #45 / #47 一致：`BulkFlushMode.None`，仅入 MemTable + WAL，最快路径。
  - 测试：`SonnetDB.Core.Tests` 新增 3 项（Sync/None/Async 三档位的 BulkIngestor 直测，包括 async 不阻塞 < 1s 验证）；`SonnetDB.Tests` 新增 2 项（端点 `?flush=async` / `?flush=false`）。全量回归 `SonnetDB.Core.Tests` 1241/1241 + `SonnetDB.Tests` 97/97 通过。
  - 不修改文件二进制格式与 `FileHeader.Version`。

### Added
- **PR #37：JekyllNet 帮助文档站点 + 镜像内 `/help` 帮助中心**
  - 新增 `.config/dotnet-tools.json`，将 `JekyllNet 0.2.5` 固化为仓库本地工具，统一 `dotnet tool restore` 与 `dotnet tool run jekyllnet build` 的构建入口。
  - `docs/` 现在作为 JekyllNet 站点源目录，新增帮助中心布局、样式以及 `index / getting-started / data-model / sql-reference / file-format / releases` 页面。
  - `SonnetDB` 新增匿名可访问的 `/help/*` 静态文档端点，运行时从 `wwwroot/help` 提供帮助中心，并支持目录式 URL。
  - `src/SonnetDB/Dockerfile` 新增文档构建步骤，在镜像构建时执行 `jekyllnet build --source docs --destination src/SonnetDB/wwwroot/help`，再随 `dotnet publish` 一起打包进镜像。
  - `web/admin` 首页与管理后台头部新增“帮助”入口，直接打开 `/help/`。

### Changed
- 文档：重写仓库 `README.md`，新增 `README.en.md`，并把根 README 调整为当前项目真实形态说明，移除“单文件持久化”与过时目标 API 描述。
- 文档：扩展 `docs/` 帮助中心，新增嵌入式 API、ADO.NET、CLI、批量写入、架构总览页面，并重写首页、快速开始、数据模型、SQL 参考与文件布局说明。
- 文档：修正文档中的实际磁盘布局描述，补充 `measurements.tslschema`、`.system/` 首次安装文件和 `/help` 内置文档站点说明。
- **PR #47：服务端 + Reader 零拷贝（写入快路径专题，第 2/4 步）**
  - `BulkIngestEndpointHandler.ReadAllAsync` 改为返回 `(byte[] Buffer, int Length)`，统一走 `ArrayPool<byte>.Shared.Rent`：已知 `Content-Length` 时按精确长度租借，未知长度则 4KB 起步翻倍扩容；`finally` 必归还，避免大 payload 直入 LOH。
  - `JsonPointsReader` 字段重构为 `ReadOnlyMemory<byte> _utf8Memory + byte[]? _pooledBuffer`：`(ReadOnlyMemory<byte>)` ctor 直接零拷贝持有 caller buffer（原先需 `utf8Json.ToArray()` 全量复制）；`(string)` ctor 走 `ArrayPool<byte>.Shared.Rent` 转码后 Dispose 归还。
  - `BulkIngestEndpointHandler.HandleAsync` JSON 路径直接构造 `new ReadOnlyMemory<byte>(bodyBuffer, 0, bodyLength)` 喂 reader，杜绝 string 中转；LP 路径新增 `CreateLineProtocolReader`，从 `ArrayPool<char>.Shared.Rent` 借出精确长度 char buffer + `Encoding.UTF8.GetChars` 解码后包成 `ReadOnlyMemory<char>`；BulkValues 路径走 `Encoding.UTF8.GetString(buffer, 0, length)` 精确长度版本。`finally` 顺序：dispose reader → return char buffer → return byte buffer。
  - 服务端 `Program.cs` 三个批量端点（`/lp` / `/json` / `/bulk`）追加 `WithMetadata(new DisableRequestSizeLimitAttribute())`，移除 Kestrel 默认 30MB request body 上限，使 1M-row payload 真正可达。
  - 旁路修复：`HelpDocsEndpoints.cs` 集合表达式三元运算符歧义（CS0173）改写为 `new[] { ... }`。
  - **基准复测**（`ServerInsertBenchmark`，1 000 000 点 / i9-13900HX / .NET 10 / Release / `bench-admin-token` 本地 dotnet run）：

    | 路径 | Mean | Allocated | vs PR #45 baseline |
    |------|-----:|----------:|--------------------|
    | `POST /sql/batch` 单行（baseline，PR #45 = 21.36s） | **5.09 s** | 668 MB | **−76% Mean**（受益于 PR #46 真批量） |
    | `POST /v1/db/{db}/measurements/{m}/lp` 1M 点 | **1.20 s** | **52 MB** | **~17.8× faster** / **alloc −92%** |
    | `POST /v1/db/{db}/measurements/{m}/json` 1M 点 | **1.20 s** | **71 MB** | **~17.8× faster** / **alloc −89%** |
    | `POST /v1/db/{db}/measurements/{m}/bulk` 1M 点 | **1.10 s** | **34 MB** | **~19.4× faster** / **alloc −95%** |

    服务端三端点首次进入「秒级 1M 点 + ≤ 80 MB 分配」区间，远超 Milestone 11 既定目标（≥ 700k pts/s）。嵌入式 `BulkIngestBenchmark` 复测无显著变化（该基准不经服务端 handler，对 PR #47 不敏感，符合预期）。
  - 测试：`SonnetDB.Core.Tests` 1237/1238 通过（`BackgroundFlushIntegrationTests.ContinuousWrite_5000Points_AutoFlushesMultipleSegments` 1 处时序敏感 flake，独立跑 2/2 通过）；`SonnetDB.Tests` 95/95 通过。
  - 兼容性说明：`LineProtocolReader` / `BulkValuesReader` / `SchemaBoundBulkValuesReader` 接口保持 `ReadOnlyMemory<char>` / `string`（未做接口级 byte 化），如未来需要彻底 byte-path（避免 LP/Bulk UTF-8→char 一次解码），将作为独立小 PR 推进。

- **PR #46：`Tsdb.WriteMany` 真批量快路径（写入快路径专题，第 1/4 步）**
  - 新增 `Tsdb.WriteMany(ReadOnlySpan<Point>)` 重载：整批写入只获取 **一次** `_writeSync` 锁、批末仅调用 **一次** `BackgroundFlushWorker.Signal`，消除原 `foreach Write(point)` 退化批量在 N 次入锁/信号上的开销。
  - 旧 `Tsdb.WriteMany(IEnumerable<Point>)` 自动嗅探 `Point[]` / `List<Point>` / `ArraySegment<Point>` 并下沉到 span 重载（`CollectionsMarshal.AsSpan` 零拷贝），其它枚举回退到逐点写入；行为对调用方完全透明。
  - WAL 记录格式与 `_walSet` 锁结构 **保持不变**，新旧库双向兼容（`FileHeader.Version` / `TsdbMagic.FormatVersion` 不升）。
  - `BulkIngestor.FlushBatch` 改走 `buffer.AsSpan(0, count)`（替代 `new ArraySegment<Point>(buffer, 0, count)`），消除新重载导致的歧义并直达 span 快路径；`/v1/db/{db}/measurements/{m}/{lp|json|bulk}` 端点与 `RemoteConnectionImpl` 自动受益。
  - **基准复测**（`BulkIngestBenchmark`，100k 点 / i9-13900HX / .NET 10）：

    | 路径 | Mean | Allocated | vs SQL baseline |
    |------|-----:|----------:|-----------------|
    | SQL INSERT VALUES（baseline） | 170 ms | 224 MB | — |
    | TableDirect Line Protocol | 178 ms | 131 MB | **alloc −42%** |
    | TableDirect JSON | 176 ms | 167 MB | alloc −25% |
    | TableDirect Bulk VALUES | 159 ms | 130 MB | **alloc −42%** |

    Mean 与 PR #45 持平（瓶颈仍在 reader 解析 + Catalog/WAL field-level 层），但 **托管堆分配降低 42–58%**（少 N 次 lock entry + iterator boxing），降低 GC 压力。
  - 测试：`SonnetDB.Core.Tests` 1238/1238 通过，`SonnetDB.Tests` 89/90 通过（1 处 pre-existing `UserStore` 并发 IO race 与本 PR 无关）。

### Added
- **SonnetDB 首次安装向导 + 产品首页（未编号）**
  - 新增 `GET /v1/setup/status` 与 `POST /v1/setup/initialize`，当 `<DataRoot>/.system` 未完成初始化时返回首次安装状态，并支持一次性写入服务器 ID、组织、管理员用户名密码与初始 Bearer Token。
  - 新增 `installation.json` 持久化安装元数据；初始 Bearer Token 作为受管用户 token 持久化，后续与现有登录/鉴权体系保持一致。
  - `web/admin` 重构为“产品首页 / 首次安装向导 / 管理后台”三段式路由，新增品牌 logo、帮助导航和首次安装引导页，避免空 `.system` 时直接落到不可登录后台。
- **PR #45：批量入库基准（绕开 SQL 解析的快路径，第 4/4 步）**
  - 新增 `tests/SonnetDB.Benchmarks/Benchmarks/BulkIngestBenchmark.cs`：嵌入式 100 000 点 4 路对比。
    - **baseline**：`SQL INSERT VALUES`（100 行/条 × 1 000 条）走 `SqlParser` + `SqlExecutor.ExecuteInsert` 流水线。
    - **TableDirect Line Protocol**：`LineProtocolReader` + `BulkIngestor.Ingest`。
    - **TableDirect JSON**：`JsonPointsReader` + `BulkIngestor.Ingest`。
    - **TableDirect Bulk VALUES**：`SchemaBoundBulkValuesReader` + `BulkIngestor.Ingest`。
    - 初次运行结果：同量级吞吐，但 LP / Bulk VALUES 内存分配仅为 baseline 的 ~58%，JSON ~75%。
  - `tests/SonnetDB.Benchmarks/Benchmarks/ServerBenchmark.cs::ServerInsertBenchmark` 增加 3 个服务端基准方法：
    - `SonnetDBServer_BulkLp_1M` / `SonnetDBServer_BulkJson_1M` / `SonnetDBServer_BulkValues_1M`，均走 PR #44 新端点 `POST /v1/db/{db}/measurements/{m}/{lp\|json\|bulk}?flush=true`，与原有的 `SonnetDBServer_Insert_1M`（`/sql/batch` SQL 路径）同列对比。
    - LP / JSON / Bulk payload 均在 `[GlobalSetup]` 预生成，不计入迭代耗时；value 统一 `:F4` 格式化避免被 parser 当作 Int64、与 measurement schema (`Float64`) 类型冲突。
  - README 「性能基准」节新增「批量入库快路径（PR #45）」子节：收录 4 路嵌入式对比表与服务端复现命令。

- **PR #44：服务端三端点 + 远程 ADO 客户端打通批量入库（绕开 SQL 解析的快路径，第 3/4 步）**
  - 服务端新增三个端点：
    - `POST /v1/db/{db}/measurements/{m}/lp` —— Line Protocol（`text/plain`）。
    - `POST /v1/db/{db}/measurements/{m}/json` —— JSON points（`application/json`）。
    - `POST /v1/db/{db}/measurements/{m}/bulk` —— `INSERT INTO ... VALUES (...)` 快路径。
    - 三个端点共用 `BulkIngestEndpointHandler`：对 `BulkValues` 走 `SchemaBoundBulkValuesReader`（按 measurement schema 解析列角色），其余直接 new reader → `BulkIngestor.Ingest`。
    - 通过 query string 传参：`?onerror=skip` 切换到 `BulkErrorPolicy.Skip`，`?flush=true` 触发结尾 `Tsdb.FlushNow`。
    - 鉴权：`CanWrite` 才允许（readwrite / admin），与 SQL 端点保持一致；非法 db / measurement 名走 400/404。
    - 响应：`200 OK { "writtenRows": N, "skippedRows": K, "elapsedMilliseconds": ms }`；解析阶段失败 `400 { "error": "bulk_ingest_error", ... }`。
    - 写入行数同步进入 `ServerMetrics.AddInsertedRows`，与 SQL 路径的 `sonnetdb_rows_inserted_total` 计数对齐。
  - `SonnetDB.Data.Remote.RemoteConnectionImpl.ExecuteBulk` 完成实现：
    - 以 `BulkPayloadDetector.DetectWithPrefix` 嗅探协议、切首行 measurement 前缀。
    - measurement 优先级：`cmd.Parameters["measurement"]` > 首行前缀 > JSON `m` 字段 > `INSERT INTO <name>`。
    - 选择 `lp / json / bulk` 端点；`onerror`、`flush` 透传为 query string；payload 直接以 `text/plain`（LP / Bulk）或 `application/json`（JSON）POST。
    - 解析 `BulkIngestResponseBody.WrittenRows` 后包成 `MaterializedExecutionResult.NonQuery`。
  - 抽取共用 `SonnetDB.Ingest.SchemaBoundBulkValuesReader.Create(tsdb, sql, measurement)` 工厂：把 `tsdb.Measurements` 的 schema 桥接到 `BulkValuesReader` 的列角色 resolver。`EmbeddedConnectionImpl` 与服务端共用此工厂，避免逻辑重复。
  - 新增 `BulkIngestResponse`（服务端契约）、`BulkIngestResponseBody`（远程客户端 DTO）；两端均纳入 source-gen JSON context（AOT 友好）。
  - 测试：`tests/SonnetDB.Tests/BulkIngestEndpointTests.cs`（10 用例：LP/JSON/Bulk × {成功 / onerror=skip / FailFast 400 / flush=true / RBAC / 404 / 401 / 写后 SELECT 回查}）+ `RemoteAdoBulkIngestTests.cs`（7 用例：远程 ADO `CommandType.TableDirect` 三协议 + measurement 参数 / onerror / 403 / 缺 measurement 抛 InvalidOperationException）。

- **PR #43：`SonnetDB.Data` 接入批量入库快路径（绕开 SQL 解析的快路径，第 2/4 步）**
  - 扩展 `IConnectionImpl` 增加 `ExecuteBulk(commandText, parameters)`，与 `Execute(sql, …)` 并列。
  - `SndbCommand.CommandType` 由只读 `Text` 改为可读写字段，允许 `CommandType.Text`（默认）与 `CommandType.TableDirect`；其它值（如 `StoredProcedure`）仍抛 `NotSupportedException`。
  - `SndbCommand.ExecuteCore` 在 `TableDirect` 下跳过 `ParameterBinder`，把 `CommandText` 整段交给 `IConnectionImpl.ExecuteBulk`。
  - `EmbeddedConnectionImpl.ExecuteBulk` 桥接 `SonnetDB.Ingest`：用 `BulkPayloadDetector.DetectWithPrefix` 嗅探协议并切首行 measurement 前缀；按 `LineProtocol`/`Json`/`BulkValues` 路由到对应 reader；对 `BulkValuesReader` 通过闭包从 `Tsdb.Measurements.TryGet(measurement).TryGetColumn(col).Role` 解析列角色（Tag/Field/Time）；最终经 `BulkIngestor.Ingest` 写入并把写入行数包成 `MaterializedExecutionResult.NonQuery`。
  - 命令参数 hooks：`measurement`（覆盖 payload 内的 measurement）、`onerror=skip`（切换到 `BulkErrorPolicy.Skip`）、`flush=true`（写入完成后触发 `Tsdb.FlushNow`）；参数名兼容 `@`/`:` 前缀。
  - `RemoteConnectionImpl.ExecuteBulk` 占位实现：抛 `NotSupportedException`，明确指向 PR #44。
  - 新增 `tests/SonnetDB.Core.Tests/Ado/TsdbBulkIngestAdoTests.cs`：10 个 xUnit 用例覆盖 LP / JSON / BulkValues / 首行前缀 / 参数（onerror/flush/measurement）/ 未知列 / `StoredProcedure` 拒绝 / TableDirect 与普通 SELECT 在同一连接共存等场景。
  - 兼容性：现有 `CommandType.Text` 路径行为完全不变；不引入新依赖。

- **PR #A：批量入库核心 `SonnetDB.Ingest` 命名空间（绕开 SQL 解析的快路径，第 1/4 步）**
  - 新增 `src/SonnetDB/Ingest/BulkPayloadFormat.cs`：`BulkPayloadFormat` 枚举（`LineProtocol` / `Json` / `BulkValues`）+ `TimePrecision`（`Nanoseconds` / `Microseconds` / `Milliseconds` / `Seconds`）。
  - 新增 `src/SonnetDB/Ingest/BulkPayloadDetector.cs`：O(1) 前后字节嗅探协议；`DetectWithPrefix` 支持可选首行 measurement 前缀（首行不含空白/`=`/`,`/`{}`/`()`/`;` 时视为 measurement）。
  - 新增 `src/SonnetDB/Ingest/IPointReader.cs`（与 `LineProtocolReader.cs` 同文件）：流式 `TryRead(out Point)` 通用契约。
  - 新增 `src/SonnetDB/Ingest/LineProtocolReader.cs`：InfluxDB Line Protocol 子集（`double` / `42i` / `t`/`f`/`true`/`false` / `"…"` field；`\,` `\=` `\空格` `\\` 转义；ns/us/ms/s 精度换算；`measurementOverride`；空行与 `#` 注释跳过）。
  - 新增 `src/SonnetDB/Ingest/JsonPointsReader.cs`：基于 `Utf8JsonReader` 的流式 JSON reader，schema `{"m":"…","precision":"ms","points":[{"t":…,"tags":{…},"fields":{…}}]}`，避免一次性反序列化大 payload；支持 `measurementOverride` 与单点级 `measurement` 覆盖。
  - 新增 `src/SonnetDB/Ingest/BulkValuesReader.cs`：`INSERT INTO m(cols) VALUES (…),(…),…;` 形式的快路径 reader；表头按需解析一次后，VALUES 走自写扫描器（支持单引号字符串 + `''` 转义 / 整数 / 浮点 / `TRUE`/`FALSE`/`NULL` / 双引号或反引号包裹的标识符）；列角色由调用方 `Func<string, BulkValuesColumnRole>` resolver 提供，便于与 measurement schema 解耦。
  - 新增 `src/SonnetDB/Ingest/BulkIngestor.cs`：统一消费入口；`ArrayPool<Point>` 8192 批 → `Tsdb.WriteMany`；支持 `BulkErrorPolicy.FailFast` / `Skip` 与可选 `flushOnComplete`；返回 `BulkIngestResult(Written, Skipped)`。
  - 新增 `src/SonnetDB/Ingest/BulkIngestException.cs`：批量入库专用异常类型。
  - 新增 `tests/SonnetDB.Core.Tests/Ingest/` 下 5 个测试类（`BulkPayloadDetectorTests` / `LineProtocolReaderTests` / `JsonPointsReaderTests` / `BulkValuesReaderTests` / `BulkIngestorTests`），共 38 个 xUnit 用例覆盖：协议嗅探、首行前缀切分、LP 与 JSON 与 Bulk INSERT VALUES 解析与异常路径、`BulkIngestor` 在 batch 边界（>8192 行）下的正确性、`Skip` 策略与 `flushOnComplete` 路径。
  - 仍保持 `src/SonnetDB` 零第三方运行时依赖（仅 `System.IO.Hashing`），不引入新的 NuGet 包。

- **PR #38：发布 `SonnetDB 0.1.0` 的 NuGet、二进制包、完整服务端包与安装包**
  - 新增 `src/SonnetDB/PackageReadme.md`、`src/SonnetDB.Data/PackageReadme.md`、`src/SonnetDB.Cli/PackageReadme.md`，并在三个项目文件中补齐 `PackageId`、`PackageReadmeFile`、版本元数据；其中 `src/SonnetDB.Data` 项目发布为 `SonnetDB` NuGet 包，程序集和命名空间保持 `SonnetDB.Data`。
  - `SonnetDB.Cli` 从占位程序升级为可用命令行工具：支持 `sndb version`、`sndb sql --connection ... --command|--file ...` 和 `sndb repl --connection ...`，可直接连接本地嵌入式数据库或远程 `SonnetDB`。
  - 新增 `tests/SonnetDB.Core.Tests/Cli/CliApplicationTests.cs`，覆盖 CLI 帮助输出与本地 SQL 执行回归场景。
  - 新增 `src/SonnetDB.Data/Internal/ExecutionFieldTypeResolver.cs`，并为 `SndbDataReader` / `IExecutionResult` / `MaterializedExecutionResult` / `RemoteExecutionResult` 补齐 trim/AOT 注解与显式类型映射，保持 `SonnetDB.Cli` 接入 `SonnetDB.Data` 后仍可 `PublishAot=true` 通过发布。
  - 新增 `docs/releases/README.md`、`docs/releases/sdk-bundle.md`、`docs/releases/server-bundle.md`、`docs/releases/installers.md`，说明 NuGet 包、SDK Bundle、Server Full Bundle、MSI/DEB/RPM 安装包的用途、目录结构、默认启动方式与凭据。
  - 新增跨平台发布脚本 `eng/release.ps1`：
    - 生成 `SonnetDB.Core` / `SonnetDB` / `SonnetDB.Cli` NuGet 包；
    - 发布 Windows / Linux 原生 AOT CLI 与 Server；
    - 生成 `sndb-sdk-<version>-<rid>` 与 `sonnetdb-full-<version>-<rid>` 压缩包；
    - 自动写入默认本地启动配置、预置管理员 `admin / Admin123!` 与 Bearer Token `sonnetdb-admin-token`；
    - 生成 SHA256 校验文件；
    - 生成 Windows `msi` 与 Linux `deb` / `rpm` 安装包。
  - 新增 `.github/workflows/publish.yml`，在 `v*` tag 或手动触发时自动执行：
    - NuGet 打包与发布；
    - Windows / Linux Server + CLI + 前端完整打包；
    - MSI / DEB / RPM 安装包构建；
    - GitHub Release 附件上传。
- **PR #35：BenchmarkDotNet 五库性能基准全量收敛**
  - 所有基准代码与 docker compose 编排在前序 PR（#32 / #33 / #36 工作流）中已陆续落地，PR #35 收敛验收并将 ROADMAP 状态置 ✅。
  - 五个数据库的覆盖矩阵（详见 README「五库基准覆盖一览」）：
    - **SonnetDB（嵌入式）**：写入 + 范围查询 + 聚合 + Compaction（4→1 段合并）。
    - **SQLite**（`Microsoft.Data.Sqlite`，WAL）：写入 + 范围查询 + 聚合。
    - **InfluxDB 2.7**（HTTP Line Protocol + Flux）：写入 + 范围查询 + 聚合。
    - **TDengine 3.3**（REST + 超级表/子表）：写入 + 范围查询 + 聚合。
    - **SonnetDB**（HTTP Batch SQL + ndjson）：写入 + 范围查询 + 聚合。
  - 数据规模统一 100 万点、单序列 `host=server001`、`value DOUBLE`，外部数据库不可用时各基准独立 `[SKIP]`。
  - README 新增「五库基准覆盖一览」表，明确标注 Compaction 仅适用于 SonnetDB，并指向各详细结果章节；同时补 ROADMAP Milestone 9 推进顺序为 PR #37 → PR #38 → PR #39。
- **SonnetDB 实时事件流：SSE + 前端订阅自动刷新（PR #34c）**
  - 服务端新增 `src/SonnetDB/Hosting/EventBroadcaster.cs`：基于 `System.Threading.Channels` 的多路广播器（`BoundedChannel` + `FullMode.DropOldest`，容量 64），按通道 `metrics` / `slow_query` / `db` 过滤订阅，慢消费者自动丢最旧帧不阻塞 publish。
  - 新增 `src/SonnetDB/Endpoints/SseEndpointHandler.cs`：实现 `GET /v1/events`，响应 `text/event-stream`，禁用 buffering（`X-Accel-Buffering: no`），按 SSE 帧格式输出 `event:` + `id:` + `data:` 三行；30 秒空闲发 `: heartbeat` 注释行心跳；支持 `?stream=metrics,slow_query,db` 通道筛选；连接建立先发 `hello` 帧。
  - 新增 `src/SonnetDB/Hosting/MetricsTickService.cs`：`BackgroundService` + `PeriodicTimer`，按 `MetricsTickSeconds`（默认 5s）周期生成 `MetricsSnapshotEvent`（含数据库数 / 用户数 / 订阅者数 / 各库 segment 计数），仅在有订阅者时构造 + 推送，避免无人订阅时空转。
  - 新增 `src/SonnetDB/Contracts/Events.cs`：`ServerEvent` / `MetricsSnapshotEvent` / `SlowQueryEvent` / `DatabaseEvent` DTO（DTO 入 `ServerJsonContext` source-gen 满足 AOT），通道常量 `ChannelMetrics` / `ChannelSlowQuery` / `ChannelDatabase`。
  - `Configuration/ServerOptions.cs` 新增 `SlowQueryThresholdMs`（默认 500ms）/ `MetricsTickSeconds`（默认 5s）。
  - `Endpoints/SqlEndpointHandler.cs`：`HandleSingleAsync` / `HandleBatchAsync` 增加 `string databaseName` 参数；新增 `MaybePublishSlow` helper 在所有路径（成功 / 失败 / 控制面 / 单条 / 批量）统计耗时，超阈值即广播 `SlowQueryEvent`（控制面用 `__control` 标签，SQL 截断到 1024 字节）。
  - `Hosting/TsdbRegistry.cs`：构造器接受 `EventBroadcaster?`，`TryCreate` / `Drop` 成功后广播 `DatabaseEvent`（`created` / `dropped`）。
  - `Auth/BearerAuthMiddleware.cs`：当请求路径是 `/v1/events` 且无 `Authorization` 头时，从 `?access_token=<tok>` query 取 token，因为浏览器 `EventSource` API 无法发自定义 header。
  - 前端新增 `web/admin/src/api/events.ts`：`subscribeServerEvents(token, opts)` 封装 `EventSource`，挂载 `hello` / `metrics` / `slow_query` / `db` 监听并把 401 / `EventSource.CLOSED` 标记为 `unauthorized` 状态，返回关闭函数。
  - 前端新增 `web/admin/src/stores/events.ts`：Pinia store，缓冲最近 100 条慢查询 + 100 条 db 事件，维护 `dbEventBumper` 计数信号 + 当前 `metrics` 快照，监听 `auth.isAuthenticated` 自动 connect / disconnect。
  - 前端新增 `web/admin/src/views/EventsView.vue`：实时指标 grid（8 个 statistic）+ 慢查询表（带成功/失败 tag）+ 数据库事件表（带 created/dropped tag）。
  - `views/AppShell.vue` 顶栏增加 SSE 状态指示（n-tag + 状态点 CSS）+ 新增「事件流」菜单项；`router/index.ts` 注册 `/admin/events` 路由。
  - `views/DashboardView.vue` / `views/DatabasesView.vue` 各 `watch(() => events.dbEventBumper, reload)` + `watch(() => events.metrics, ...)`，CREATE/DROP DATABASE 在所有打开的客户端无需手动刷新即可即时同步；Dashboard 的 segment 计数也由 metrics 帧覆盖。
  - 测试：新增 `tests/SonnetDB.Tests/SseEndToEndTests.cs` 端到端覆盖：401 拒绝匿名访问、`?access_token=` query 鉴权通过、收到 `hello` → CREATE → `db.created` → 触发 SQL → `slow_query`（阈值压到 0）→ 周期性 `metrics`（tick 设 1s）→ DROP → `db.dropped` 完整链路。
  - `web/admin/tsconfig.json` 把 `ignoreDeprecations` 从 `"6.0"` 调整为 `"5.0"`，使本地 TypeScript 5.6.x 可继续构建；语义不变（仍是抑制 `baseUrl` 废弃告警）。
- **SonnetDB Admin Vue3 管理后台完成（PR #34b）**
  - `web/admin/`：Vite + Vue 3 + TypeScript + Naive UI + Pinia + Vue Router 单页应用，完整涵盖登录页、数据库列表/状态、SQL 控制台、用户/权限/Token 管理七个视图：
    - `LoginView.vue`：Bearer token 登录，结果存 localStorage，axios 拦截器自动注入 `Authorization: Bearer`。
    - `AppShell.vue`：响应式侧边栏 + 顶栏（用户名 / 角色标签 / 退出），超级用户额外显示用户 / 权限 / Token 菜单，路由 Guard 拦截未登录与越权访问。
    - `DashboardView.vue`：数据库数量 / 用户数量 / 授权条目三个统计卡，数据库状态表格（在线状态 + Segment 数），admin 额外展示用户列表。
    - `DatabasesView.vue`：`GET /v1/db` + `/metrics` 展示数据库列表/Segment 数，admin 可创建（`CREATE DATABASE`）与二次确认 DROP。
    - `SqlConsoleView.vue`：目标数据库选择器（admin 额外有控制面选项）+ 多行 SQL 编辑器 + 运行/清空按钮 + ndjson 结果表格 + 行数/耗时 meta 行。
    - `UsersView.vue`（admin only）：`SHOW USERS` 列表、`CREATE USER ... [SUPERUSER]` 表单、改密弹窗（`ALTER USER`）、二次确认 DROP。
    - `TokensView.vue`（admin only）：`SHOW TOKENS [FOR user]` 列表、`ISSUE TOKEN FOR <user>` 签发并弹窗展示明文、按用户筛选、行级 `REVOKE TOKEN`。
  - 服务端嵌入资源管线：`AdminUiAssets` / `AdminUiEndpoints` 把 `web/admin/dist/**` 以 `EmbeddedResource` 嵌入 dll，运行时按路径提供文件，SPA 路由 fallback 到 `index.html`，hash 化资产设 `immutable` 缓存，dist 未 build 时返回 503 提示。
  - 专用控制面 SQL 端点 `POST /v1/sql`（admin only）运行控制面语句（CREATE USER / GRANT / SHOW USERS …），数据面 SQL 走 `POST /v1/db/{db}/sql`；前端通过 `execControlPlaneSql` / `execDataSql` 统一调用。
  - `tsconfig.json` 添加 `"ignoreDeprecations": "6.0"` 抑制 TypeScript 对 `baseUrl` 的废弃警告（TS5101：`baseUrl` 将在 TypeScript 7.0 停止工作）。
- **SonnetDB Docker 性能测试（PR #36）**
  - 新增 `src/SonnetDB/Dockerfile`：基于 `mcr.microsoft.com/dotnet/sdk:10.0` 多阶段构建，最终镜像基于 `mcr.microsoft.com/dotnet/aspnet:10.0`，框架依赖发布（Framework-dependent）。
  - 更新 `tests/SonnetDB.Benchmarks/docker/docker-compose.yml`：新增 `sonnetdb` 服务，暴露端口 5080，默认 token `bench-admin-token`，含健康检查（wget `/healthz`）和 Docker Volume 持久化。
  - 新增 `tests/SonnetDB.Benchmarks/Benchmarks/ServerBenchmark.cs`：包含 `ServerInsertBenchmark`、`ServerQueryBenchmark`、`ServerAggregateBenchmark` 三个基准类，通过 `HttpClient` 直接调用 SonnetDB HTTP API（Batch SQL insert / SELECT / GROUP BY 聚合），服务不可用时自动 `[SKIP]`。
  - 更新 `README.md`：新增「SonnetDB 服务器模式性能基准」章节，记录 Docker 容器测试环境（AMD EPYC 9V74，Ubuntu 24.04，.NET 10.0.5）及实测结果：写入 13.16 s（HTTP Batch 2k/批）、范围查询 210.8 ms、1 分钟桶聚合 138.3 ms，并对比嵌入式模式额外开销。
- **SonnetDB Admin SPA：数据库状态 + Token 管理（PR #34b-4）**
  - 前端新增 `web/admin/src/views/TokensView.vue`，提供 admin-only 的 Token 管理页：`SHOW TOKENS [FOR user]` 列表、`ISSUE TOKEN FOR <user>` 一次性签发明文 token、`REVOKE TOKEN '<tokenId>'` 行级吊销，并在弹窗中提示“token 明文只展示一次”。
  - `web/admin/src/router/index.ts` / `views/AppShell.vue` 新增 `tokens` 路由与侧边栏菜单；用户 / 权限 / Token 三个控制面页面现在形成完整闭环。
  - `UserStore` / `ControlPlane` / `SqlExecutor` 的 token 查询与吊销链路补齐单元/集成/E2E 覆盖：新增 `ListTokensDetailed` + `RevokeTokenById` 验证、控制面 SQL 的 `ISSUE/SHOW/REVOKE TOKEN` round-trip，以及 token 吊销后旧 Bearer 立即失效的端到端断言。
- **SonnetDB 控制面：用户 / 权限 / 数据库 DDL（PR #34a）**
  - 新增持久化用户与权限存储（仅服务端）：`src/SonnetDB/Auth/UserStore.cs` + `GrantsStore.cs`，文件落 `<DataRoot>/.system/{users.json,grants.json}`，原子写入（temp + `Flush(true)` + `File.Move(overwrite=true)`）。
  - 密码：PBKDF2-HMAC-SHA256，100 000 轮、16 字节随机 salt、32 字节 hash；校验用 `CryptographicOperations.FixedTimeEquals` 防侧信道。
  - 动态 API token：32 字节随机（`RandomNumberGenerator`）→ Base64Url，仅存 SHA-256 hex 哈希；token id 形如 `tok_<8hex>`，便于审计与单条吊销。
  - 权限模型：`enum DatabasePermission { Read=1, Write=2, Admin=3 }`，按 `(database,user)` 单条记录；`*` 通配整个集群。
  - **SQL 控制面 DDL**：在 `src/SonnetDB/Sql/` 新增 7 条语句类型 + parser 分支（`ParseStatement` 添加 `Drop`/`Alter`/`Grant`/`Revoke`，新增 `ParseCreate` 二级分发）：
    - `CREATE USER <name> WITH PASSWORD '<pwd>' [SUPERUSER]`
    - `ALTER USER <name> WITH PASSWORD '<new>'`（成功后吊销该用户全部旧 token）
    - `DROP USER <name>`（级联删除其所有 grants）
    - `GRANT READ|WRITE|ADMIN ON DATABASE <db|*> TO <user>`
    - `REVOKE ON DATABASE <db|*> FROM <user>`
    - `CREATE DATABASE <name>` / `DROP DATABASE <name>`（通过 `IControlPlane` 触发 `TsdbRegistry.TryCreate/Drop`，并级联 grants）
  - **执行层 `IControlPlane`**：`src/SonnetDB/Sql/Execution/IControlPlane.cs` 在核心库声明（零依赖），`SqlExecutor.ExecuteStatement` 新增 `IControlPlane?` 参数；嵌入式连接传入 `null` → 控制面 DDL 抛 `NotSupportedException`（"控制面 DDL（CREATE USER / GRANT / CREATE DATABASE 等）仅在服务端模式可用。"）。服务端在 `src/SonnetDB/Auth/ControlPlane.cs` 提供桥接实现：用户/权限/数据库三向级联（DROP USER → DeleteUserGrants，DROP DATABASE → DeleteDatabaseGrants）。
  - **认证扩展（PR #34a-5）**：`BearerAuthMiddleware.Authenticate` 新增 `UserStore?` 入参，先匹配 `ServerOptions.Tokens` 静态映射，未命中再走 `UserStore.TryAuthenticate`（哈希查表）；命中动态 token 时把 `AuthenticatedUser` 写入 `HttpContext.Items["sndb.user"]`，超级用户映射 `admin` 角色，普通用户映射 `readwrite`。`/v1/auth/login` 路径始终匿名。
  - **新端点 `POST /v1/auth/login`**：接收 `{username,password}`，PBKDF2 校验通过后调用 `UserStore.IssueToken` 颁发新 token，返回 `{username,token,tokenId,isSuperuser}`。⚠️ 该端点用 `app.MapMethods(path, ["POST"], (RequestDelegate)(async ctx => ...))` 直接以 `RequestDelegate` 形式注册（详见 `Fixed`）。
  - **SQL 端点权限收紧**：`src/SonnetDB/Endpoints/SqlEndpointHandler.cs` 在执行前 parse 一次，识别为控制面 DDL 时要求 `isAdmin == true`，否则返回 `forbidden`；写操作（INSERT/DELETE）仍按 `canWrite` 判定。批处理同步收紧。
  - **测试**：5 个端到端用例 `tests/SonnetDB.Tests/AuthControlPlaneEndToEndTests.cs` 覆盖：登录字段缺失 → 400、未知用户 → 401、CREATE USER + GRANT + 登录拿 token + 用 token 调 `/healthz` 与 SQL 端点（普通用户控制面 DDL → forbidden）、动态非 admin token 控制面 DDL → forbidden、ALTER USER 改密后旧 token 立即失效 → 401。服务端测试总数：49 通过 / 0 失败。
- **SonnetDB 控制面查询 SQL：SHOW USERS / GRANTS / DATABASES（PR #34b-1）**
  - 新增 SQL 关键字 `SHOW / USERS / GRANTS / DATABASES / FOR`（`src/SonnetDB/Sql/TokenKind.cs` + `SqlLexer.cs`）。
  - 新增 AST：`ShowUsersStatement` / `ShowGrantsStatement(UserName)` / `ShowDatabasesStatement`，`SqlParser.ParseShow()` 分发；`SHOW GRANTS [FOR <user>]` 中 `FOR` 子句可选。
  - `IControlPlane` 扩展 3 个查询方法：`ListUsers()` → `IReadOnlyList<UserSummary>`、`ListGrants(string?)` → `IReadOnlyList<GrantSummary>`、`ListDatabases()` → `IReadOnlyList<string>`；`UserSummary(Name, IsSuperuser, CreatedUtc, TokenCount)` 与 `GrantSummary(UserName, Database, Permission)` 在核心库声明。
  - `SqlExecutor` 把 SHOW 语句包装成 `SelectExecutionResult`，复用现有 `/v1/db/{db}/sql` ndjson 渲染管线，无需新增 REST 端点。
  - 服务端 `UserStore.ListUsersDetailed()` 与 `GrantsStore.ListAll()` 提供枚举支撑；`ControlPlane` 用反向权限映射 (`MapPermissionBack`) 把 `DatabasePermission` 转回 `GrantPermission`。
  - 权限收紧：`SHOW USERS` / `SHOW GRANTS` 在 `SqlEndpointHandler.IsControlPlaneStatement` 中归为 admin-only；`SHOW DATABASES` 任何已认证用户均可执行。
  - 测试：parser 5 例（`Parse_ShowUsers/ShowDatabases/ShowGrants_NoFilter/WithFor` + 3 个 bad grammar Theory）+ ControlPlane 集成 3 例（`ListUsers_ReturnsCreatedUsersOrderedByName` / `ListGrants_NullFilter_ReturnsAll` / `ListDatabases_ReflectsRegistry`）+ E2E 3 例（`ShowUsers_AsAdmin_ReturnsRows` / `ShowUsers_AsRegularUser_Forbidden` / `ShowDatabases_AsAdmin_ReturnsRows`）。当前测试总数：1174 + 55 = 1229 全绿。
- **SonnetDB Admin SPA 脚手架：嵌入式静态资源管线（PR #34b-2）**
  - 新增 `web/admin/` 完整 Vite + Vue 3 + TypeScript + Naive UI + Pinia + Vue Router 单页应用脚手架，含 `LoginView`（PBKDF2 登录 → token 存 localStorage）+ `DashboardView`（数据库 / 用户 / grants 概览，**全部走 SHOW SQL**，零额外 REST 端点）。
  - 路由前缀固定为 `/admin/`；axios 拦截器自动注入 `Bearer <token>`；vite dev 反代 `/v1`、`/healthz`、`/metrics` → `:5000`。
  - 服务端 `src/SonnetDB/Hosting/AdminUiAssets.cs`：启动时一次性把 `web/admin/dist/**` 嵌入资源（`sndb.admin/...`）加载到 `FrozenDictionary`，AOT 友好的 MIME 类型 switch；`AdminUiEndpoints.MapAdminUi()` 注册 `GET /admin` 与 `GET /admin/{**path}`，命中具体文件返回原字节，未命中且无扩展名时回退 `index.html`（SPA 客户端路由），manifest 为空时返回 503 + 提示 `npm run build`。
  - `BearerAuthMiddleware.Authenticate` 豁免 `/admin/*` 路径匿名访问（仅静态资源；任何管理动作仍需登录后凭 token 调 `/v1/db/{db}/sql`）。
  - csproj 集成：`web/admin/dist/**` 通过 `<EmbeddedResource>` 自动嵌入，`LogicalName` 写为 `sndb.admin/%(RecursiveDir)%(Filename)%(Extension)`，C# 端把 `\` 规范化为 `/`；可选 target `BuildAdminUi=true` 自动跑 `npm install && npm run build`（默认 false，避免日常 `dotnet build` 被 npm 拖慢）。dist 目录通过 `web/admin/.gitignore` 排除，不入库。
  - 缓存策略：`/admin`、`/admin/index.html` → `no-cache`；其他 hash 化资产 → `public, max-age=31536000, immutable`（与 Vite 默认 contenthash 命名匹配）。
  - 测试：6 个端到端用例 `tests/SonnetDB.Tests/AdminUiEndToEndTests.cs` 覆盖 `GET /admin` 返回 HTML、SPA fallback (`/admin/login` → index.html)、带扩展名缺失 → 404、匿名可访问、favicon → image/svg+xml、`/v1/db` 仍要求 Bearer。当 dist 未 build 时所有用例自动跳过断言（CI 友好）。当前测试总数：1174 + 61 = **1235 全绿**。
- **SonnetDB Admin SPA：SQL Console + 数据库 / 用户 / 权限管理（PR #34b-3）**
  - 新增专用控制面 SQL 端点 `POST /v1/sql`（admin only，无 db 路径），仅接收控制面语句（CREATE USER / GRANT / CREATE DATABASE / SHOW USERS / SHOW DATABASES 等）；数据面语句 → 400。仍然以 `application/x-ndjson` 流式输出，行格式与 `/v1/db/{db}/sql` 完全一致，前端可共享解析器。复用 `app.MapMethods(path, ["POST"], (RequestDelegate)(...))` 模式绕过 AOT RequestDelegateGenerator 拦截。
  - 核心库新增 `SqlExecutor.ExecuteControlPlaneStatement(SqlStatement, IControlPlane)` 入口，独立于 `Tsdb` 实例运行；用于 `/v1/sql` 端点，使前端无需先选数据库就能跑控制面 SQL。
  - **SQL grammar 补全 `SUPERUSER` 关键字**：`CREATE USER <name> WITH PASSWORD '<pwd>' [SUPERUSER]` 末尾可选关键字现在被解析（此前 `IsSuperuser` 始终为 `false`）。新增 `TokenKind.KeywordSuperuser` + lexer 映射 + parser 可选消费 + 2 个 parser 单测。
  - **前端 SPA 重构**：抽出 `web/admin/src/api/sql.ts` 共享 ndjson 解析（`parseNdjson` / `execControlPlaneSql` / `execDataSql` / `rowsToObjects` / `quote` / `isValidIdentifier`），所有视图共用 `axios.validateStatus:()=>true` + `responseType:'text'` 模式正确处理 4xx / 5xx 响应。
  - 抽出 `views/AppShell.vue` 共享布局壳子（sider + header + `<router-view/>`），由 `App.vue` 顶层挂载 `NDialogProvider / NMessageProvider / NNotificationProvider`；普通用户菜单显示「概览 / SQL Console / 数据库」，超级用户额外显示「用户 / 权限」。
  - 4 个新视图：
    - `SqlConsoleView.vue`：目标选择器（控制面 / 任意数据库）+ 多行 SQL 编辑 + 运行按钮 + 结果表格 + meta（行数 / 受影响行数 / elapsedMs）+ error alert。
    - `DatabasesView.vue`：`SHOW DATABASES` 列表 + admin 创建（`CREATE DATABASE`，标识符校验）+ 二次确认 DROP。
    - `UsersView.vue`（admin only）：`SHOW USERS` 表格 + `CREATE USER ... [SUPERUSER]` 表单 + 改密弹窗（`ALTER USER ... WITH PASSWORD '...'`）+ 二次确认 DROP。
    - `GrantsView.vue`（admin only）：`SHOW GRANTS` 表格 + `GRANT READ|WRITE|ADMIN ON DATABASE <db> TO <user>` 表单 + 行级 `REVOKE ON DATABASE <db> FROM <user>` 二次确认。
  - 路由结构调整：`/admin/login` 匿名；其余路由全部嵌套在 `AppShell` 子路由下（`/dashboard` / `/sql` / `/databases` / `/users` / `/grants`），全局 guard 增加 `meta.admin` 判断（非 admin 访问 admin 路由 → 重定向 `/dashboard`）。
  - 测试：parser 新增 2 例（`Parse_CreateUser_WithSuperuserKeyword_SetsFlag` / `Parse_CreateUser_SuperuserKeyword_CaseInsensitive`）+ 服务端 E2E 新增 4 例（`ControlPlaneEndpoint_AsAdmin_RunsCreateUserAndShowUsers` / `ControlPlaneEndpoint_CreateSuperuser_FlagPersisted` / `ControlPlaneEndpoint_AsRegularUser_Forbidden` / `ControlPlaneEndpoint_RejectsDataPlaneStatement`）。当前测试总数：1176 + 65 = **1241 全绿**。

### Fixed
- **Admin SPA 普通用户数据库列表修正（PR #34b-4）**
  - `DashboardView` / `DatabasesView` / `SqlConsoleView` 不再错误地把 `SHOW DATABASES` 走到 admin-only 的 `POST /v1/sql`；数据库列表改走 `GET /v1/db`，数据库状态通过 `GET /metrics` 读取 `sonnetdb_segments{db="..."}`，因此普通已登录用户也能查看数据库概览与段数量。
  - `SqlConsoleView` 的“控制面”目标选择现在仅对 admin 显示；普通用户默认落到首个可访问数据库，避免一进控制台就遇到 `/v1/sql 仅 admin 可调用` 的无意义报错。
- **AOT RequestDelegateGenerator workaround**：`WebApplication.CreateSlimBuilder` + `EnableRequestDelegateGenerator=true` 下，对于
  `app.MapPost(path, async (HttpContext ctx) => Results.Json(value, typeInfo, statusCode: 4xx))` 形态的 lambda，生成的 interceptor 会
  错误地把响应吞成 `200 + 空 body`（lambda 实际执行，但 statusCode 与 body 全部丢失）。`/v1/auth/login` 改为
  `app.MapMethods(path, ["POST"], (RequestDelegate)(async ctx => { ctx.Response.StatusCode = ...; await JsonSerializer.SerializeAsync(...); }))`
  绕过 generator 拦截，行为稳定（PR #34a）。

- **SonnetDB.Data**：将 ADO.NET API 从 `SonnetDB` 核心库剥离为独立的 `src/SonnetDB.Data/`（PR #33）
  - 公共表面保持兼容：`SndbConnection` / `SndbCommand` / `SndbDataReader` / `SndbParameter` / `SndbParameterCollection` / `SndbConnectionStringBuilder` 命名空间从 `SonnetDB.Ado` 迁移到 `SonnetDB.Data`；`src/SonnetDB/Ado/` 目录整体删除。
  - **嵌入式 + 远程双模式**：通过连接字符串 scheme 自动分派，由内部接口 `IConnectionImpl` 统一抽象。
    - 嵌入式：`Data Source=<path>` 或 `sonnetdb://<path>` → `EmbeddedConnectionImpl` 复用 `SharedSndbRegistry` + 进程内 `Tsdb`，行为与原 `SonnetDB.Ado` 完全一致。
    - 远程：`Data Source=sonnetdb+http://host:port/<db>;Token=<bearer>` 或 `http(s)://...` → `RemoteConnectionImpl` 通过 `HttpClient` + ndjson 流式协议直连 `SonnetDB` 的 `POST /v1/db/{db}/sql` 端点；服务端错误抛 `SndbServerException`（含 `Error` / `ServerMessage` / `StatusCode`）。
    - `SndbConnectionStringBuilder.ResolveMode()` 支持显式 `Mode=Embedded|Remote` 覆盖；新增 `Token` / `Timeout`（默认 100s）键。
    - `SndbConnection.ProviderMode` 暴露当前模式；`UnderlyingTsdb` 仅在嵌入式模式返回非空。
  - 新增 `SndbProviderFactory : DbProviderFactory`（单例 `Instance`），可注册到 `DbProviderFactories` 供通用 ADO 工具使用。
  - `IsAotCompatible=false` 并附详细注释（理由：`DbConnection` / `DbCommand` 基类大量反射；主流 ADO 提供程序如 Npgsql、MySqlConnector 也未承诺 AOT；需要 AOT 的场景请直接使用 `Tsdb` API）。
  - 远程客户端 ndjson 解析使用 `System.Text.Json` 源生成器（`RemoteJsonContext`）+ `JsonDocument` 解析行级数组，`HttpCompletionOption.ResponseHeadersRead` 实现真流式读取。
  - 9 个端到端测试（`tests/SonnetDB.Tests/RemoteAdoEndToEndTests.cs`）覆盖：scheme 分派、嵌入式回退、CREATE→INSERT→SELECT 全链路、参数绑定与单引号转义、`ExecuteScalar`、只读令牌 INSERT 拒绝、SQL 错误、缺失令牌 401、未知数据库 404；既有 31 个 `TsdbAdoApiTests` 全部保持通过。

- **SonnetDB**：Native AOT 友好的 Minimal API HTTP 服务器（PR #32）
  - 新项目 `src/SonnetDB/`，基于 `Microsoft.NET.Sdk.Web` + `WebApplication.CreateSlimBuilder` + `EnableRequestDelegateGenerator=true`，全程零反射，可 `dotnet publish -p:PublishAot=true` 产出单文件可执行（win-x64 ~11.5MB），AOT 警告数为 0。
  - 多租户：进程内 `TsdbRegistry`（`ConcurrentDictionary<string, Tsdb>`）+ `DataRoot/<db>/` 子目录隔离，启动时按需加载已存在数据库；`POST /v1/db`（admin）创建、`DELETE /v1/db/{db}`（admin）销毁、`GET /v1/db` 列表；数据库名校验通过 `[GeneratedRegex]` 源生成器。
  - SQL 端点：`POST /v1/db/{db}/sql`（单条）+ `POST /v1/db/{db}/sql/batch`（多条），结果以 `application/x-ndjson` 流式返回（meta 行 + 每行 JSON 数组 + end 行），通过手写 `Utf8JsonWriter` 避免多态序列化；其余 DTO 全部走 `System.Text.Json` 源生成器（`ServerJsonContext`）。
  - 认证：`Authorization: Bearer <token>` 三角色（`admin` / `readwrite` / `readonly`），自定义中间件直接读 `ServerOptions.Tokens` 静态映射，非 `/healthz` `/metrics` 一律强制鉴权；写操作（INSERT/DELETE/DDL）需 `readwrite` 或 `admin`，建删数据库需 `admin`。
  - 可观测性：`GET /healthz` 返回 JSON 健康摘要；`GET /metrics` 输出 Prometheus 文本格式（`sonnetdb_uptime_seconds` / `sonnetdb_databases` / `sonnetdb_sql_requests_total` / `sonnetdb_sql_errors_total` / `sonnetdb_rows_inserted_total` / `sonnetdb_rows_returned_total` / per-db `sonnetdb_segments{db="..."}`）。
  - 6 个端到端集成测试（`tests/SonnetDB.Tests/ServerEndToEndTests.cs`）覆盖 Healthz / Metrics 匿名访问、SQL 鉴权、admin 角色限定、CREATE→INSERT→SELECT→DROP 全链路、ndjson 解析、未知数据库 404。
- **整库 Native AOT 兼容**：`Directory.Build.props` 默认开启 `IsAotCompatible=true`（测试与基准项目显式关闭）；`SonnetDB` / `SonnetDB.Cli` / `SonnetDB` 全部以零 IL/AOT 警告通过 `dotnet publish -p:PublishAot=true`。
  - `SndbDataReader.GetFieldType` 重构：内部 `Type[]` 改为 `enum ColumnTypeKind`，并添加 `[DynamicallyAccessedMembers]` 标注 + `typeof(...)` 常量 switch，消除 IL2063/IL2093 警告，对外 API 与运行时行为完全保持。
- **CI**：`.github/workflows/ci.yml` 新增 `aot-publish` job（Linux + Windows 矩阵），执行 `dotnet publish -p:PublishAot=true /warnaserror` 验证 `SonnetDB.Cli` 与 `SonnetDB`，并上传 publish 产物（PR #32）。

### Changed
- `InsertBenchmark`、`QueryBenchmark`、`AggregateBenchmark`：将内存占位实现替换为真实 `Tsdb` 引擎调用（PR #35）
- `README.md` 性能基准章节扩展为 **SonnetDB vs SQLite vs InfluxDB 2.7 vs TDengine 3.3.4** 四方对比（基于 1M 点数据集，单机容器）

### Fixed
- 基准测试 `GlobalCleanup` 中 SQLite 连接池文件锁问题（`SqliteConnection.ClearAllPools()`）（PR #35）
- `_influxAvailable` 现正确使用 `PingAsync()` 返回值而非无条件设为 `true`（PR #35）
- `InsertBenchmark.GlobalCleanup` 不再删除 InfluxDB bucket，避免后续 benchmark 进程的 `IterationSetup` 因 bucket 缺失而抛 `NotFoundException`
- `EnsureInfluxBucketAsync()`：三个 benchmark 在 `GlobalSetup` 中自动创建缺失的 `benchmarks` bucket
- TDengine SQL：`value` / `host` 列名加反引号绕开保留字解析错误，确保 4-DB 全部产出有效结果


### Added
- 新增段文件编码 / 字节统计快照 `SegmentReader.GetStats()`（PR #31）
  - 新增公开 record `SonnetDB.Storage.Segments.SegmentStats`（含 `BlockCount` / `TotalPointCount` / `TotalFieldNameBytes` / `TotalTimestampPayloadBytes` / `TotalValuePayloadBytes` / `RawTimestampBlocks` / `DeltaTimestampBlocks` / `RawValueBlocks` / `DeltaValueBlocks` / `ByFieldType` 以及计算型属性 `AverageTimestampBytesPerPoint` / `AverageValueBytesPerPoint`）与 `FieldTypeStats`（`BlockCount` / `PointCount` / `ValuePayloadBytes` / `DeltaValueBlocks`），为运维巡检、压缩率对比、基准测试提供结构化输出。
  - `SegmentReader.GetStats()`：按需遍历 `BlockDescriptor[]`，一次迭代同时计算总量 / 按 `BlockEncoding` 拆分 / 按 `FieldType` 分组三个维度；不缓存。可用于对同一 `MemTable` 分别以 V1 / V2 写入后对比 `Total*PayloadBytes` 验证压缩效果。
  - `SegmentStats.ByFieldType` 使用 `IReadOnlyDictionary<FieldType, FieldTypeStats>` 提供面向查询，默认为空字典以避免空段访问 NRE。
  - 6 个新测试（`SegmentReaderStatsTests`）覆盖：默认 V1 全部计入 raw 且字节数符合 8B/点；单独开启 V2 时间戳验证只选取时间戳压缩、值字节数不变；单独开启 V2 值（String 字典）验证值字节压缩、时间戳保持 V1；双 V2两个计数器都增加、平均字节/点均 < 8；多 `FieldType` 混合段按组计数与点数一致；空 `SegmentStats` 除零防护。

- 新增数值列 V2 编码：Float64 Gorilla XOR + Boolean RLE + String 字典（PR #30）
  - 新增内部位流工具 `SonnetDB.Storage.Segments.BitIo`：`BitWriter` / `BitReader` ref struct，高位优先按位写读，最大 64 位/调用。
  - 新增内部值列 V2 编解码器 `SonnetDB.Storage.Segments.ValuePayloadCodecV2`：
    - **Float64**：简化版 Gorilla XOR — 第一个值 64 位锚点，之后每点 1 位控制位；变化点再写 6 位 leadingZeros + 6 位 (meaningful-1) + meaningful 位有效位。常量序列压缩到 ≈1 位/点。
    - **Boolean**：游程长度编码（RLE）— 1 字节初值 + 交替 varint 段长。
    - **String**：按出现顺序构建字典 — `varint(dictSize)` + `dictSize × (varint(byteLen) + UTF-8)` + `count × varint(idx)`，重复值高度压缩。
    - **Int64**：本 PR 暂不压缩，仍为 8B LE 直存（与 V1 等价）。
  - `SegmentWriterOptions.ValueEncoding`：默认 `None`（V1）以保证已有段文件与测试行为不变；显式设为 `DeltaValue` 启用 V2 并在 `BlockHeader.Encoding` 与 `TimestampEncoding` 标志位独立组合。
  - `BlockDecoder.ReadValues` / `ReadValuesRange` 新增基于 `descriptor.ValueEncoding` 的 V1/V2 分发；V2 范围读取需先全量解码再切片（XOR/RLE/字典本质顺序）。
  - 19 个新测试（`ValuePayloadCodecV2Tests`）覆盖：Float64 空/单点/常量序列压缩/递增序列/特殊值（NaN/±Inf/±0）round-trip；Bool 全 true/交替/混合 run/损坏 run 越界；String 全相同/含 unicode/含空串/字典索引越界；Int64 V2 透传；SegmentWriter 默认无标志、单独 `DeltaValue`（Float64/Bool/String 均显著小于 V1）、`DeltaTimestamp | DeltaValue` 双标志组合及 `DecodeBlockRange` 与 V1 一致。

- 新增时间戳 Delta-of-Delta + ZigZag varint 编码（V2 block payload，向后兼容 V1）（PR #29）
  - 新增内部 `SonnetDB.Storage.Segments.TimestampCodec`：`MeasureDeltaOfDelta` / `WriteDeltaOfDelta` / `ReadDeltaOfDelta`。V2 格式：8 字节定点锐 + 1 个一阶差分 + 剩余二阶差分，常规采样间隔下压缩到 ≈1 字节/点。
  - `BlockEncoding` 改为 `[Flags]`：`DeltaTimestamp` (1) 与 `DeltaValue` (2) 可独立开关；`SegmentReader` 根据 bit 拆分到 `BlockDescriptor.{TimestampEncoding, ValueEncoding}`。
  - `SegmentWriterOptions.TimestampEncoding`：默认 `None`（V1）以保证已有文件与测试行为不变；显式设为 `DeltaTimestamp` 则启用 V2 并在 `BlockHeader.Encoding` 中置位。
  - `BlockDecoder` 联合读取路径（全量与范围）根据 `descriptor.TimestampEncoding` 分发；V2 路径需要完整重现时间戳后才能二分，已与现有范围查询逻辑保持一致。
  - 13 个新测试（`TimestampCodecTests`）覆盖：空序列、单点、规则间隔压缩占比、不规则间隔、负二阶差分、大锐点、buffer 长度不匹配、锐点截断、varint 越界、SegmentWriter 默认 V1、V1↔V2 跳点一致、`DecodeRange` 一致、`BlockDescriptor` 标志保留。

- 新增标准 ADO.NET API，提供 `SndbConnection` / `SndbCommand` / `SndbDataReader` / `SndbParameter` / `SndbParameterCollection` / `SndbConnectionStringBuilder`（PR #28）
  - `SonnetDB.Ado.SndbConnection : System.Data.Common.DbConnection`：连接字符串为 `Data Source=<根目录>`（大小写不敏感，由 `DbConnectionStringBuilder` 提供）；同进程同路径多次 `Open` 通过内部 `SharedSndbRegistry` 引用计数共享同一 `Tsdb`，避免 WAL 锁冲突；事务与 `ChangeDatabase` 抛 `NotSupportedException`
  - `SonnetDB.Ado.SndbCommand : DbCommand`：包装 `SqlExecutor`；`ExecuteNonQuery` 返回 INSERT 写入行数 / DELETE 増加的墓碑总数 / CREATE MEASUREMENT 0 / SELECT -1；`ExecuteScalar` 返回 SELECT 首行首列（空集返 null）；`ExecuteReader` 包装 `SelectExecutionResult`，非 SELECT 语句返回零行 reader 并携带 `RecordsAffected`
  - 参数绑定：支持 `@name` 与 `:name` 占位符，执行前以状态机扫描 SQL 文本并跳过字符串字面量 / 双引号标识符 / 行注释；支持类型包括 `string` / `bool` / 整型 / 浮点 / `decimal` / `DateTime` / `DateTimeOffset`（后两者转为 Unix 毫秒）/ `null` / `DBNull`；字符串值会被单引号包裹并把内部 `'` 转义为 `''`，避免 SQL 注入
  - `SndbDataReader : DbDataReader`：完整实现 `Read` / `GetXxx` / `IsDBNull` / `GetOrdinal` / `GetFieldType`（以首个非 null 行推断）/ `HasRows` / `RecordsAffected` / `CommandBehavior.CloseConnection`。`NextResult` 总为 `false`，`GetBytes` / `GetChars` 抛 `NotSupported`
  - 单元测试：31 个端到端测试（`TsdbAdoApiTests`）覆盖连接生命周期 / 共享 `Tsdb` / `BeginTransaction` 不支持 / `ConnectionStringBuilder` 大小写不敏感 / 三种 `ExecuteXxx` / 参数状态机（跳过字面量与标识符）/ 参数转义防注入（`O'Brien` 场景）/ 缺失参数报错 / `:name` 形式 / NULL 参数 / 多个 CommandText 错误路径 / `CloseConnection` 行为

- 新增 Tag 倒排索引以加速 `SELECT/DELETE` 的 `WHERE tag = '...'` 过滤（PR #27）
  - `SonnetDB.Catalog.TagInvertedIndex`（internal）：维护 `measurement → SeriesId 集合` 与 `measurement → tagKey → tagValue → SeriesId 集合` 两级映射；全部使用 `ConcurrentDictionary` 实现单写多读线程安全；集合本身用 `ConcurrentDictionary<ulong, byte>` 模拟并发集合
  - `SonnetDB.Catalog.SeriesCatalog.Find(measurement, tagFilter)`：从全表线性扫描改为基于倒排索引的候选集交集（基准选最小集合，规模上界为 `min(|S_i|)`）；返回前仍执行一次防御性 measurement+tag 重校验以容忍倒排索引与 `_byCanonical` 的瞬间不一致
  - 索引在 `GetOrAddInternal` 中仅由胜出的 `candidate` 线程写入（`ReferenceEquals(entry, candidate)`），并在 `LoadEntry`（`CatalogFileCodec` 重放路径）与 `Clear` 中维护——索引本身不进入持久化格式，启动时由现有持久化条目重建，因此**未变更磁盘 catalog 文件格式**
  - 单元测试：11 个新增测试（`TagInvertedIndexTests`）覆盖无 tag 过滤 / 单 tag / 多 tag 交集 / 未命中值 / 缺失 tagKey / 未知 measurement / measurement 隔离 / `Clear` 后清空 / 重复 `GetOrAdd` 索引不膨胀 / `LoadEntry` 重建 / 并发写读

- 新增 SQL `DELETE FROM ... WHERE ...` 执行支持（PR #26）
  - `SonnetDB.Sql.Execution.DeleteExecutionResult`（record，含 `Measurement` / `SeriesAffected` / `TombstonesAdded`）
  - `SonnetDB.Sql.Execution.DeleteExecutor`（internal）：复用 `WhereClauseDecomposer` 解析 tag 等值过滤 + 时间窗，对所有命中 tag 过滤的 series × schema 中所有 Field 列调用 `Tsdb.Delete(seriesId, fieldName, from, to)`，落到 PR #20 的 Tombstone 体系（WAL 追加 + 内存墓碑表 + 查询时过滤）
  - `SqlExecutor.ExecuteDelete(Tsdb, DeleteStatement)` 公共入口；`Execute` 派发新增 `DeleteStatement` 分支
  - 语义：`WHERE host = 'h1' AND time >= a AND time <= b` 等价于命中 series 的所有 field 列在 `[a, b]` 闭区间打墓碑；省略 time 比较则覆盖全时间轴；省略 tag 过滤则作用于该 measurement 下所有 series；命中 0 series 直接返回零计数（不抛错）
  - 校验规则：measurement 必须存在；WHERE 与 SELECT 共用同一套约束（仅 AND、tag 等值、time 比较、不支持 OR/NOT/field 过滤）；空时间窗抛 `InvalidOperationException`
  - 单元测试：13 个端到端测试覆盖时间窗 + tag 过滤 / 仅时间窗 / 仅 tag 过滤 / `time = X` 单点删除 / 命中 0 series / 跨重启持久化（WAL replay）/ 删除后聚合验证 / 各类错误场景（缺 measurement / OR / field 过滤 / 未知 tag 列 / 空时间窗 / null 参数）

- 新增 SQL `SELECT ... [WHERE ...] [GROUP BY time(...)]` 执行支持（PR #25）
  - `SonnetDB.Sql.Execution.SelectExecutionResult`（record，含 `Columns` / `Rows`；行内运行时类型：time→`long`、tag→`string?`、field→`double/long/bool/string?`、count→`long`、其他聚合→`double`）
  - `SonnetDB.Sql.Execution.WhereClauseDecomposer`（internal）：将 WHERE AST 拆分为 `(TagFilter, TimeRange)`；仅支持顶层 `AND` 合取、`tag = 'literal'` 等值过滤、`time {= != >= > <= <}` 时间窗（`time !=` 暂不支持）；OR / NOT / field 过滤 / 非字面量右值 / 同 tag 列冲突值均抛 `InvalidOperationException`；自动检测空时间窗
  - `SonnetDB.Sql.Execution.SelectExecutor`（internal）：投影分类（time/tag/field/aggregate）；原始模式按 series 做时间戳并集 outer-join，缺失字段输出 `null`；聚合模式以 `SortedDictionary<long, BucketState[]>` 按桶累积 count/sum/min/max/first/last，`GROUP BY time(d)` 由 `TimeBucket.Floor` 对齐，无 GROUP BY 则全局单桶；多 series 的 sum/avg/min/max/count 自动跨 series 合并；`count(*)` 跨 schema 全部数值 field 求总点数（跳过 String）；`count(field)` 计数任意类型；其他聚合拒绝 String field
  - `SqlExecutor.ExecuteSelect(Tsdb, SelectStatement)` 公共入口；`Execute` 派发新增 `SelectStatement` 分支
  - 校验规则：聚合不可与裸列混用；`GROUP BY time(...)` 仅在聚合中有效；`first`/`last` 多 series 暂不支持（v1）；未知函数 / 未知列 / 聚合函数作用于 Tag 列均抛错
  - 单元测试：25 个端到端测试覆盖 `SELECT *` / 列投影 / outer-join NULL / WHERE 时间窗 / WHERE tag 过滤 / 别名 / `count(*)` / `count(field)` / `sum/avg/min/max` / `first/last` / 多 series 聚合 / `GROUP BY time(1000ms)` / 空时间窗 / 各类错误场景（缺 measurement / 未知列 / OR / field 过滤 / 混合投影 / 缺聚合的 GROUP BY / first 多 series / tag 不等 / tag 冲突 / String 字段 sum）

- 新增 SQL `INSERT INTO ... VALUES (...)` 执行支持（PR #24）
  - `SonnetDB.Sql.Execution.InsertExecutionResult`（record，含 `Measurement` / `RowsInserted`）
  - `SqlExecutor.ExecuteInsert(Tsdb, InsertStatement)`：完整列绑定 + 类型校验 + 时间戳缺省 + 批量写入
  - 校验规则：measurement 必须已 CREATE；列名必须存在于 schema；同一 INSERT 列列表禁止重复；Tag 必须为字符串字面量且非 NULL；Field 类型必须匹配（INT 字面量可隐式提升为 FLOAT）；每行至少 1 个 Field 列值；`time` 列必须为非负整数字面量；缺省时使用 `DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()`
  - `SqlParser`：新增内部 `ExpectColumnName()`，允许 INSERT 列列表中将保留字 `time` 作为列名使用（与时间戳伪列对应；亦可继续用 `"time"` 引号转义）
  - `SqlExecutor.ExecuteStatement` 现支持 `InsertStatement` 派发
  - 单元测试：17 个端到端测试，覆盖单行 / 批量 / 时间缺省 / 时间大小写不敏感 / Int→Float 提升 / 全四种 FieldType round-trip / 仅 Field 无 Tag / measurement 缺失 / 未知列 / 重复列 / 类型不匹配 / Tag 非字符串 / NULL / 缺 Field / 负时间戳 / 批量部分失败前序已落地 / 参数 null

- 新增 `SonnetDB.Catalog` 命名空间下 measurement schema 体系，并接入 `Tsdb` 与 SQL 执行器（PR #23）
  - `MeasurementColumnRole`（Tag/Field 角色枚举）、`MeasurementColumn`（列定义 record）、`MeasurementSchema`（不可变值对象，工厂 `Create` 校验：列非空、≥1 个 Field、列名唯一、Tag 列必须 STRING、禁止 Unknown 类型）、`MeasurementCatalog`（基于 `ConcurrentDictionary` 的线程安全注册表）
  - `MeasurementSchemaCodec`：新增持久化文件 `measurements.tslschema`；二进制格式 `Magic(8) + FormatVersion(4) + HeaderSize(4) + Count(4) + Reserved(12)` 头 + 变长 measurement 记录 + `Crc32(4) + Magic(8) + Reserved(4)` 尾；`ArrayPool<byte>` + `SpanReader` / `SpanWriter` 实现，`Save` 走临时文件 + 原子 rename + fsync
  - `TsdbPaths.MeasurementSchemaFileName` / `MeasurementSchemaPath(root)` 路径常量
  - `Tsdb.Measurements` 属性 + `Tsdb.CreateMeasurement(MeasurementSchema)`：注册到 catalog 并立刻把全量 schema 集合原子持久化（崩溃安全）；`Open` 启动时加载、`Dispose` 关闭时再次保存
  - 新增 `SonnetDB.Sql.Execution.SqlExecutor`：`Execute(Tsdb, sql)` / `ExecuteStatement(Tsdb, SqlStatement)` / `ExecuteCreateMeasurement(Tsdb, CreateMeasurementStatement)`；把 AST `ColumnDefinition` 映射到 catalog `MeasurementColumn` 后调用 `Tsdb.CreateMeasurement`；其余语句类型暂抛 `NotSupportedException` 留待后续 PR
  - 单元测试：8 个 schema 校验 + 5 个 codec round-trip / 损坏检测 + 5 个执行器与持久化端到端测试

- 新增 `SonnetDB.Sql` 命名空间：纯 Safe-only、零第三方依赖的 SQL 词法 + 语法分析器（PR #22）
  - `TokenKind` / `Token` / `SqlLexer`：单遍词法分析；关键字大小写不敏感；标识符保留原始大小写；支持单引号字符串字面量（`''` 转义）、双引号引用标识符（`""` 转义）、整数/浮点字面量、duration 字面量（`ns / us / ms / s / m / h / d`，统一归一化为毫秒）、`-- 行注释`、`/* 块注释 */`、运算符 `= != <> < <= > >= + - * / %`
  - `Sql.Ast`：AST 节点（`SqlStatement` / `CreateMeasurementStatement` / `InsertStatement` / `SelectStatement` / `DeleteStatement` / `ColumnDefinition` / `SelectItem` / `TimeBucketSpec` / `SqlExpression` 派生：`LiteralExpression` / `DurationLiteralExpression` / `IdentifierExpression` / `StarExpression` / `FunctionCallExpression` / `BinaryExpression` / `UnaryExpression`），均为 `record` 值语义
  - `SqlParser`：递归下降解析器，覆盖 `CREATE MEASUREMENT` / `INSERT INTO ... VALUES (...) [, (...)]*` / `SELECT projections FROM measurement [WHERE ...] [GROUP BY time(duration)]` / `DELETE FROM measurement WHERE ...`；支持 `*` 通配、聚合函数（`count(*) / avg(x) / ...`）、`AS alias` 与裸 alias、`AND / OR / NOT` 短路逻辑、6 种比较与 5 种算术运算、括号显式优先级、`NULL / TRUE / FALSE` 字面量；新增 `SqlParser.Parse(string)` 解析单语句、`SqlParser.ParseScript(string)` 解析多语句脚本（分号分隔）
  - 关键字 `time` 在表达式中既可作为列名（`time >= 100`）也可作为函数（`time(1m)`），通过下一个 token 是否为 `(` 自动消歧
  - `SqlParseException`：携带源 SQL 字符位置的诊断异常
  - 单元测试：50 个 Lexer + Parser 测试，覆盖关键字大小写、字符串/标识符/duration 转义、运算符优先级、注释跳过、错误位置等

- 新增 `SonnetDB.Engine.Retention.RetentionPolicy`：数据保留策略；支持全局 TTL、轮询周期、限流（MaxTombstonesPerRound）及虚拟时钟注入（NowFn）（PR #21）
- 新增 `SonnetDB.Engine.Retention.RetentionPlan` / `TombstoneToInject`：单次 Retention 扫描的产物（纯计算，无副作用）
- 新增 `SonnetDB.Engine.Retention.RetentionPlanner`：从当前段集合产出 `RetentionPlan` 的纯函数；支持整段 drop、部分过期墓碑注入、已有等价墓碑去重及限流截断
- 新增 `SonnetDB.Engine.Retention.RetentionWorker`：后台 Retention 工作线程，双路径回收——整段直接 drop（MaxTimestamp < cutoff） + 墓碑注入（部分过期段，由 Compaction 在下一轮物理删除）
- 新增 `SonnetDB.Engine.Retention.RetentionExecutionStats`：单次 Retention 扫描统计（Cutoff / DroppedSegments / InjectedTombstones / ElapsedMicros）
- `SegmentManager.DropSegments(IReadOnlyList<long>)`：原子移除多个段，重建索引快照，Dispose 旧 reader，返回被移除列表（PR #21）
- `Tsdb.Retention`：暴露后台 Retention 工作线程（仅当 `TsdbOptions.Retention.Enabled=true` 时非 null）
- `TsdbOptions.Retention`：Retention TTL 策略入口（默认禁用，保持向后兼容）
- `RetentionPolicy.NowFn` 支持注入虚拟时钟（测试 + 自定义时间戳单位）

**Milestone 5 完成**（PR #17 后台 Flush + #18 Compaction + #19 多 WAL 滚动 + #20 DELETE-Tombstone + #21 Retention TTL）。

- 删除支持：`Tsdb.Delete(seriesId, field, from, to)` 和 `Tsdb.Delete(measurement, tags, field, from, to)`，返回操作是否成功（PR #20）
- WAL 新增 `RecordType.Delete = 5`（向后兼容），`WalWriter.AppendDelete` / `WalSegmentSet.AppendDelete` 追加删除记录
- 新增 `Tombstone`（readonly record struct）：墓碑数据结构，声明 (SeriesId, FieldName) 在时间窗 [From, To] 内的数据已被永久标记删除（v1 时间窗语义，无 perPoint LSN 比对）
- 新增 `TombstoneTable`：进程内墓碑集合，按 (SeriesId, FieldName) 索引；线程安全（lock 写 + Volatile 读快照）；提供 `IsCovered` / `GetForSeriesField` / `Add` / `LoadFrom` / `RemoveAll`
- 新增 `TombstoneManifestCodec`（`SonnetDB.Wal`）：墓碑清单文件 `<root>/tombstones.tslmanifest` 的序列化与反序列化；包含 Magic / FormatVersion / Crc32 校验；临时文件 + 原子 rename 写入
- 新增 `TsdbPaths.TombstoneManifestPath`：返回清单文件完整路径
- `Tsdb.Tombstones` 属性：暴露进程内墓碑集合
- 查询路径自动应用墓碑：`QueryEngine` 的 `Execute(PointQuery)` 和 `Execute(AggregateQuery)` 均一致过滤被墓碑覆盖的数据点（后者通过复用 PointQuery 路径自动获得）
- `SegmentCompactor.Execute` 新增可选 `TombstoneTable?` 参数：Compaction 时物理删除被墓碑覆盖的数据点；若某 (SeriesId, FieldName) 全部点被覆盖，则不生成对应 Block
- `CompactionWorker`：Swap 完成后自动回收"不再覆盖任何活段"的墓碑，更新 `TombstoneTable` 并重写 manifest
- 崩溃恢复：`Tsdb.Open` 启动时加载 manifest，再从 WAL replay 追加 CheckpointLsn 之后的 Delete 记录，最后重写 manifest（双路恢复）

### Changed
- `FlushCoordinator.Flush` 新增可选 `TombstoneTable?` 参数；Flush 序列在 WriteSegment 之前插入第 0 步：持久化 tombstone manifest（确保 WAL recycle 后墓碑不丢失）
- `WalReplayResult` 新增 `IReadOnlyList<DeleteRecord> DeleteRecords` 字段（含 CheckpointLsn 之后的删除记录）
- `WalReader.Replay()` 和 `WalSegmentSet.ReplayWithCheckpoint` 新增对 `WalRecordType.Delete` 的解析，产出 `DeleteRecord`
- `Tsdb.Dispose`：若 MemTable 为空（无需 Flush），仍会持久化 tombstone manifest


- 新增 `SonnetDB.Wal.WalSegmentSet`：多 WAL segment 管理器（Append / Sync / Roll / RecycleUpTo / ReplayWithCheckpoint），支持多 segment 滚动写入与按 CheckpointLsn 整段回收（PR #19）
- 新增 `WalSegmentLayout`（static）：WAL segment 文件命名约定（`{startLsn:X16}.SDBWAL`）、枚举、`TryParseStartLsn` 及 legacy `active.SDBWAL` 升级工具
- 新增 `WalSegmentInfo`（readonly record struct）：segment 元数据（StartLsn / Path / FileLength）
- 新增 `WalRollingPolicy`：WAL 滚动策略配置（Enabled / MaxBytesPerSegment=64MB / MaxRecordsPerSegment=1M 双阈值）
- 新增 `Tsdb` 启动时的 legacy `wal/active.SDBWAL` 自动升级路径（`UpgradeLegacyIfPresent`）

### Changed
- `FlushCoordinator.Flush` 改为通过 `WalSegmentSet` 工作；Flush 顺序升级为：WriteSegment → AppendCheckpoint+Sync → Roll → RecycleUpTo(checkpointRecordLsn) → MemTable.Reset（PR #19）
- `Tsdb` 内部 `_walWriter` 替换为 `WalSegmentSet _walSet`；`Tsdb.Open` 现在调用 `WalSegmentSet.Open`（自动升级 legacy WAL）和 `WalSegmentSet.ReplayWithCheckpoint`
- `TsdbOptions` 新增 `WalRollingPolicy WalRolling` 属性（默认 `WalRollingPolicy.Default`）
- `WalTruncator.SwapAndTruncate` 标记 `[Obsolete]`，内部保留以兼容外部使用；替代方案：`WalSegmentSet.Roll + RecycleUpTo`


- 新增 `SonnetDB.Engine.Compaction.CompactionPolicy`：Size-Tiered Compaction 触发策略（Enabled / MinTierSize / TierSizeRatio / FirstTierMaxBytes / PollInterval / ShutdownTimeout）
- 新增 `CompactionPlan` / `CompactionResult`：Compaction 计划与执行结果数据对象
- 新增 `CompactionPlanner`（static）：无副作用的 Size-Tiered 计划生成器；tier 划分公式 `tierIndex = max(0, floor(log_TierSizeRatio(fileLength / FirstTierMaxBytes)) + 1)`
- 新增 `SegmentCompactor`：N 路最小堆合并多个段、按 (SeriesId, FieldName) 写入新段；v1 同 timestamp 全部保留、FieldType 冲突抛 `InvalidOperationException`
- 新增 `CompactionWorker`（internal）：后台 Compaction 工作线程，轮询 Plan + Execute + SwapSegments + 删除旧段
- 新增 `SegmentManager.SwapSegments`：在单一锁内原子地移除旧段 + 打开新段 + 重建索引快照，避免中间状态可见
- `TsdbOptions.Compaction` 新增 `CompactionPolicy` 属性（默认 Default，Enabled=true）
- `Tsdb.Open` 末尾：若 `Compaction.Enabled` 启动 `CompactionWorker`
- `Tsdb.Dispose`：先关 CompactionWorker，再关 FlushWorker
- `Tsdb.AllocateSegmentId()`（internal）：线程安全 SegmentId 分配


- 新增 `SonnetDB.Engine.BackgroundFlushWorker`（internal）：后台 Flush 工作线程，含信号 + 周期轮询双触发，与同步 FlushNow 共享 `_writeSync` 锁保证互斥
- 新增 `BackgroundFlushOptions`（Enabled / PollInterval / ShutdownTimeout），`Dispose` 严格不泄漏后台线程
- 新增 `WalReplay.ReplayIntoWithCheckpoint`：基于 Checkpoint LSN 两遍扫描跳过冗余 WritePoint，消除崩溃恢复的冗余回放开销
- 新增 `WalReplayResult` record（CheckpointLsn / LastLsn / WritePoints）
- `TsdbOptions.BackgroundFlush` 暴露后台线程开关（默认 Enabled=true）
- `Tsdb.CheckpointLsn` 诊断属性：最近一次 Flush 的 WAL CheckpointLsn
- `Tsdb.Write` 在锁外向 worker 发送非阻塞信号；移除同步 Write 路径中的自动 Flush（由后台线程接管）
- `Tsdb.Open` 改用 `ReplayIntoWithCheckpoint` 替代 `ReplayInto`，支持 WAL 续写正确 LSN

### Added
- 新增 `SonnetDB.Query.QueryEngine`：合并 MemTable + 多 Segment 的查询执行器；支持原始点查询（`Execute(PointQuery)`）、聚合查询（`Execute(AggregateQuery)`）及批量聚合（`ExecuteMany`）（Milestone 4 完成）
- 新增 `PointQuery` / `AggregateQuery` / `AggregateBucket` / `Aggregator` / `TimeRange` 查询类型
- 支持 Count / Sum / Min / Max / Avg / First / Last 七种聚合函数（Float64 / Int64 / Boolean 字段）
- 支持 `GROUP BY time(...)` 桶聚合（基于 PR #7 的 `TimeBucket`）；空桶不输出
- 内部 N 路有序合并器 `BlockSourceMerger`：段按 SegmentId 升序排列后合并，MemTable 在最末，同 ts 全部 yield（不去重）
- `Tsdb.Query` 属性暴露查询入口（`QueryEngine` 无状态，每次查询时重建 SegmentId→Reader 映射）
- **Milestone 4 完成**：查询路径全面贯通（MemTable + 多段 + 时间过滤 + 7 种聚合 + GROUP BY time）

### Added
- 新增 `SonnetDB.Storage.Segments.SegmentBlockRef`（readonly struct）：跨段统一的 Block 引用（SegmentId + SegmentPath + BlockDescriptor）
- 新增 `SegmentIndex`（sealed class）：单段内 SeriesId / (SeriesId, FieldName) → BlockDescriptor 索引，含段级时间范围与时间窗二分剪枝
- 新增 `MultiSegmentIndex`（sealed class）：跨段只读联合索引快照；`LookupCandidates` 剪枝顺序：段级时间 → series → field → 段内时间窗二分
- 新增 `SonnetDB.Engine.SegmentManager`（sealed class）：已打开 SegmentReader 集合 + 索引快照管理器；lock 写 + Volatile 无锁读并发模型
- `Tsdb` 接入 `SegmentManager`：Open 时扫描段构建初始索引，FlushNow 时增量 AddSegment；Dispose 时关闭全部 SegmentReader
- `TsdbOptions` 新增 `SegmentReaderOptions` 属性

### Added
- 启动 Milestone 4：查询路径
- 新增 `SonnetDB.Storage.Segments.SegmentReader`：不可变段文件只读访问器
  - Open 时校验 Magic / Version / HeaderSize / FooterOffset / IndexCrc32
  - 按 SeriesId / (SeriesId, FieldName) / TimeRange 线性查找 BlockDescriptor
  - `ReadBlock` 返回零拷贝 ref struct `BlockData`
  - `DecodeBlock` / `DecodeBlockRange` 解码出 DataPoint[]
- 新增 `BlockDescriptor`（readonly struct）：描述 Block 元数据与物理位置
- 新增 `BlockData`（readonly ref struct）：零拷贝 Block payload 视图
- 新增 `SegmentReaderOptions`：VerifyIndexCrc / VerifyBlockCrc 选项（默认均启用）
- 新增 `SegmentCorruptedException`：段文件损坏或格式不一致时抛出（含 path + offset）
- 新增 `BlockDecoder`（internal static）：ValuePayloadCodec 的对偶，跨平台读用 BinaryPrimitives LE；支持 Float64 / Int64 / Boolean / String 四种类型及 DecodeRange 时间裁剪

### Added
- 启动 SonnetDB 引擎门面：`SonnetDB.Engine.Tsdb`（Open / Write / WriteMany / FlushNow / Dispose），完成 Milestone 3 写入路径闭环（PR #13）
- 新增 `TsdbOptions`：引擎全局配置（RootDirectory / FlushPolicy / SegmentWriterOptions / WalBufferSize / SyncWalOnEveryWrite）
- 新增 `TsdbPaths`：标准磁盘布局路径管理（catalog.SDBCAT + wal/active.SDBWAL + segments/{id:X16}.SDBSEG）
- 新增 `FlushCoordinator`：MemTable → Segment + WAL Checkpoint + WAL Truncate 三步原子可见
- 新增 `WalTruncator.SwapAndTruncate`：rename + 重建策略，避免就地截断的并发风险
- 新增 `SegmentWriterOptions.PostRenameAction`（internal）：原子 rename 完成后的测试钩子，用于模拟 rename 之后崩溃场景
- 完成 Milestone 3：写入路径闭环，三场景崩溃恢复测试矩阵齐全（未 Flush 崩溃 / Flush 后崩溃 / rename 后未 Checkpoint 崩溃）

### Added
- 初始化项目规划文档：`README.md`、`CHANGELOG.md`、`ROADMAP.md`、`AGENTS.md`
- 确定技术栈：C# / .NET 10 / xUnit / BenchmarkDotNet / GitHub Actions
- 确定核心设计原则：Safe-only、Span/MemoryMarshal、InlineArray、WAL+MemTable+Segment
- 解决方案与项目骨架（`SonnetDB.slnx`、`src/SonnetDB`、`src/SonnetDB.Cli`、`tests/SonnetDB.Core.Tests`、`tests/SonnetDB.Benchmarks`）（PR #2）
- `Directory.Build.props`（统一 `LangVersion` / `Nullable` / `ImplicitUsings` / `TreatWarningsAsErrors`）
- `Directory.Packages.props`（Central Package Management）
- `global.json`（固定 .NET 10 SDK）
- `.editorconfig`（统一代码风格）
- 新增 GitHub Actions CI 工作流（build + test，ubuntu / windows 矩阵）
- 新增 CodeQL 安全扫描工作流
- 新增 Dependabot 依赖更新配置
- 新增 dotnet format 校验
- 新增时序数据库性能对比基准（`tests/SonnetDB.Benchmarks/`）：使用 BenchmarkDotNet 0.15.8 对比 SonnetDB（内存占位）、SQLite、InfluxDB 2.x 和 TDengine 3.x 在相同 Docker 环境下的 100 万条数据**写入、时间范围查询、1 分钟桶聚合**的性能，含 Docker Compose 配置和 README 说明
- 新增 `SonnetDB.IO.SpanWriter`：基于 Span/MemoryMarshal/BinaryPrimitives 的 safe-only 顺序二进制写入器
- 新增 `SonnetDB.IO.SpanReader`：基于 Span/MemoryMarshal/BinaryPrimitives 的 safe-only 顺序二进制读取器
- 支持基础类型、unmanaged 结构体、结构体数组、VarInt(LEB128)、字符串的 round-trip 编解码
- 全程 little-endian，零 `unsafe`（PR #4）
- 新增 `SonnetDB.Buffers.InlineBytes4/8/16/32/64`：基于 `[InlineArray(N)]` 的固定长度内联缓冲区
- 新增 `InlineBytesExtensions`：通过 `MemoryMarshal.CreateSpan` 提供 Safe-only 的 `AsSpan` / `AsReadOnlySpan` 视图
- 新增 `InlineBytesHelpers`：泛型 `SequenceEqual` / `CopyFrom` 辅助方法
- 新增 `TsdbMagic`：定义 SonnetDB 文件 / 段 / WAL 的 magic 与格式版本常量（PR #5）
- 新增固定二进制结构体（namespace `SonnetDB.Storage.Format`）：
  - `FileHeader`（64B）/ `SegmentHeader`（64B）/ `BlockHeader`（64B）
  - `BlockIndexEntry`（48B）/ `SegmentFooter`（64B）/ `WalRecordHeader`（32B）
- 新增枚举：`BlockEncoding` / `FieldType` / `WalRecordType`
- 新增 `FormatSizes` 常量类，所有 header 尺寸由编译期 `Unsafe.SizeOf<T>` 测试守护
- 完成 Milestone 1：内存与二进制基础设施（Span/MemoryMarshal/InlineArray + 全部固定 header）（PR #6）
- 新增逻辑数据模型（namespace `SonnetDB.Model`）：
  - `FieldValue`（readonly struct，零装箱，支持 Float64/Int64/Boolean/String）
  - `Point`（用户层写入对象，含校验规则）
  - `DataPoint`（引擎内单 field 数据点，readonly record struct）
  - `SeriesFieldKey`（series + field 复合键，readonly record struct）
  - `AggregateResult`（Count/Sum/Min/Max/Avg 累加器）
  - `TimeBucket`（时间桶 Floor/Range/Enumerate 辅助）
- 启动 Milestone 2：逻辑模型与 Series Catalog（PR #7）
- 新增 `SonnetDB.Model.SeriesKey`（readonly struct）：规范化 `measurement + sorted(tags)` 为确定性字符串，格式 `measurement,k1=v1,k2=v2`，tags 按 key Ordinal 升序
- 新增 `SonnetDB.Model.SeriesId`（static class）：通过 `XxHash64` 将 `SeriesKey.Canonical` 的 UTF-8 编码折叠为 `ulong`，作为引擎主键（PR #8）
- 新增 `SonnetDB.Catalog.SeriesCatalog`：线程安全的 SeriesKey ↔ SeriesId ↔ SeriesEntry 中央目录（基于 ConcurrentDictionary，单写多读友好）
- 新增 `SonnetDB.Catalog.SeriesEntry`：序列目录条目（Id / Key / Measurement / Tags / CreatedAtUtcTicks），Tags 以 FrozenDictionary 保证不可变
- 新增 `SeriesCatalog.Find`：按 measurement + tag 子集线性查找
- 新增 `SonnetDB.Catalog.CatalogFileCodec`：`.SDBCAT` 目录文件序列化器（含临时文件原子替换写入与规范化校验加载）
- 新增 `SonnetDB.Storage.Format.CatalogFileHeader`（64B）：目录文件头，含 magic "SDBCATv1" / 版本 / 条目数
- 新增 `TsdbMagic.Catalog`（"SDBCATv1"）与 `TsdbMagic.CreateCatalogMagic()`
- 新增 `FormatSizes.CatalogFileHeaderSize = 64`
- 新增 `InlineBytes24` 内联缓冲区及其 `AsSpan`/`AsReadOnlySpan` 扩展
- 完成 Milestone 2：逻辑模型与 Series Catalog（PR #9）
- 启动 Milestone 3：写入路径（PR #10）
- 新增 `SonnetDB.Storage.Format.WalFileHeader`（64B）：WAL 文件头，含 magic "SDBWALv1" / 版本 / FirstLsn
- 新增 `FormatSizes.WalFileHeaderSize = 64`
- 更新 `WalRecordHeader`（32B）：新增 `Magic`（0x57414C52）/ `Flags` / `PayloadCrc32` / `Lsn` 字段，移除 `SeriesId` 至 payload
- 更新 `WalRecordType`：重命名 `Write→WritePoint`、`CatalogUpdate→CreateSeries`，新增 `Truncate=4`
- 新增 `SonnetDB.Wal` 命名空间：
  - `WalRecord` 抽象基类及派生：`WritePointRecord` / `CreateSeriesRecord` / `CheckpointRecord` / `TruncateRecord`
  - `WalWriter`：append-only WAL 写入器，含 CRC32（`System.IO.Hashing.Crc32`）+ fsync 支持
  - `WalReader`：迭代式回放，支持文件尾截断与 CRC 校验失败的优雅停止，暴露 `LastValidOffset`
  - `WalReplay`：将 WAL 回放到 `SeriesCatalog`，并 yield 出 `WritePointRecord` 流
  - `WalPayloadCodec`（internal）：4 种 RecordType × 4 种 FieldType 的 payload 编解码
- 新增 `SonnetDB.Memory.MemTableSeries`：单 (SeriesId, FieldName, FieldType) 桶，
  支持顺序与乱序追加，Snapshot 稳定排序（`_isSorted` 快速路径 + 索引辅助稳定排序）
- 新增 `SonnetDB.Memory.MemTable`：以 SeriesFieldKey 为主键的写入内存层，
  支持 WAL Replay 装载（`ReplayFrom`）、阈值触发 Flush（`ShouldFlush`）、Reset 与 SnapshotAll（PR #11）
- 新增 `SonnetDB.Memory.MemTableFlushPolicy`：MaxBytes / MaxPoints / MaxAge 三种阈值策略
- 新增 `SonnetDB.Storage.Segments.SegmentWriter`：把 MemTable 写为不可变 `.SDBSEG` 文件，使用临时文件 + 原子 rename 保证崩溃安全（PR #12）
- 新增 `SegmentWriterOptions`：BufferSize / FsyncOnCommit / TempFileSuffix 写入选项
- 新增 `SegmentBuildResult`：构建结果记录（路径、BlockCount、时间范围、各区偏移、耗时）
- 新增 `ValuePayloadCodec`（internal）：Float64 / Int64 / Boolean / String 的 Raw 编码
- 新增 `FieldNameHash`（internal）：基于 XxHash32 的字段名哈希，用于 BlockIndexEntry.FieldNameHash
- 启用 `BlockHeader.Crc32`（CRC32(FieldNameUtf8 ++ TsPayload ++ ValPayload)）与 `SegmentFooter.Crc32`（IndexCrc32）

---

## [0.1.0] — *Planned*

> 对应 ROADMAP Milestone 0 ～ Milestone 3

### Added
- 解决方案与项目骨架（`SonnetDB.sln`、`src/SonnetDB`、`src/SonnetDB.Cli`、`tests/SonnetDB.Core.Tests`、`tests/SonnetDB.Benchmarks`）
- `.editorconfig`、`Directory.Build.props`（统一 `LangVersion` / `Nullable` / `TreatWarningsAsErrors`）
- GitHub Actions CI（build + test，矩阵 ubuntu-latest / windows-latest）
- `SpanReader` / `SpanWriter`（`ref struct`，基于 `BinaryPrimitives` + `MemoryMarshal`）
- `[InlineArray]` 工具：`Magic8`、`Reserved16` 等固定缓冲
- 核心 `unmanaged struct`：`FileHeader`、`SegmentHeader`、`BlockHeader`、`BlockIndexEntry`、`SegmentFooter`
- 逻辑模型：`Point`、`DataPoint`、`SeriesFieldKey`、`AggregateResult`
- `SeriesKey` 规范化 + `SeriesId`（XxHash64）
- `SeriesCatalog`（内存 + 持久化）
- `WalWriter` / `WalReader`（append-only + replay）
- `MemTable`
- `SegmentWriter`（BlockHeader + payload + footer index）
- Flush 流程：MemTable → Segment，WAL truncate

---

## [0.2.0] — *Planned*

> 对应 ROADMAP Milestone 4 ～ Milestone 5

### Added
- `SegmentReader`（按 seriesId/time range 裁剪 block）
- `QueryEngine.QueryRaw`（合并 MemTable + 多 Segment）
- 聚合：`min/max/sum/avg/count` + 时间桶 `time(10s)` 分组
- SQL 词法与语法分析器（手写递归下降）
- `CREATE MEASUREMENT` / `INSERT INTO ... VALUES` / `SELECT ... WHERE ... GROUP BY time(...)` 语句支持
- ADO.NET 风格 API：`SndbConnection / SndbCommand / SndbDataReader`

---

## [0.3.0] — *Planned*

> 对应 ROADMAP Milestone 6 ～ Milestone 8

### Added
- 时间戳 delta 编码（block payload V2）
- 值列 delta 编码
- `CompactionEngine`（合并旧 segment）
- page manager + free list
- 将 manifest / wal / segments 合并为单一 `.tsl` 文件
- BenchmarkDotNet 基准（写入/查询/聚合）
- 发布 NuGet 包 `SonnetDB` 0.1.0

---

[Unreleased]: https://github.com/IoTSharp/SonnetDB/compare/v2.5.0...HEAD
[2.5.0]: https://github.com/IoTSharp/SonnetDB/releases/tag/v2.5.0
[0.1.0]: https://github.com/maikebing/SonnetDB/releases/tag/v0.1.0
[0.2.0]: https://github.com/maikebing/SonnetDB/releases/tag/v0.2.0
[0.3.0]: https://github.com/maikebing/SonnetDB/releases/tag/v0.3.0
