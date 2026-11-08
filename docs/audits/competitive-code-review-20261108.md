# SonnetDB 竞品对照代码审查（2026-11-08）

> 类型：静态源码审查（只读）。证据层级：**源代码 = PASS（已读）**；构建/测试/基准/固定硬件/生产 = **NOT_RUN**。
> 本报告中的性能判断来自算法复杂度与数据结构分析，以及仓库已有的 `docs/benchmarks/system-performance-20260901.md` 本机 smoke 数据，未在本轮重新实测。
> 竞品对照为机制对照（公开架构），不是同机基准对比。

## 0. 结论摘要

SonnetDB 的**功能广度**已接近“全能型单机多模型库”（九模型 + SQL/PGQ/GQL + CDC/流 + RAG），可靠性工程（WAL CRC、目录 fsync、有界预算、恢复测试）也很扎实。
但核心存储层存在几处**结构性瓶颈**，会在数据量从 10k 级 smoke 上升到 10M～1B 级时成为硬天花板：

| # | 问题 | 影响模型 | 严重度 |
|---|---|---|---|
| P0-1 | KV checkpoint 每次全量重写整个 keyspace（非 LSM 分层合并） | 关系表、文档、图、对象元数据、KV | 🔴 致命（容量上限） |
| P0-2 | KV 每次写后首次快照读 = 复制 + 排序整个 overlay（≤10 万/100 万项），且全局串行门闩 | 关系表事务、图读会话、文档 | 🔴 读写混合负载尾延迟 |
| P0-3 | 时序引擎全库单写锁 + 每字段一条 WAL 记录 | 时序 | 🔴 写入吞吐上限 |
| P0-4 | 时序段无时间分区，Size-Tiered 跨时间合并；保留策略只有全库单一 TTL | 时序 | 🟠 保留/压缩放大 |
| P0-5 | 同 series/field/timestamp 重复写入被全部保留（无 last-write-wins / dedup） | 时序 | 🟠 语义缺陷（重试即重复计数） |
| P1 | 查询结果全量物化为 `object?` 装箱行，无流式/向量化执行 | SQL 全部 | 🟠 大结果内存/延迟 |
| P1 | 文档以 JSON 文本存储，谓词/更新/聚合反复 `JsonDocument.Parse` | 文档 | 🟠 CPU/分配 |
| P1 | 全文检索穷举打分（`Dictionary<string,double>` + 全排序），无 WAND/Block-Max | 全文 | 🟠 Top-K 延迟 |
| P1 | Tag 倒排索引全内存三层 `ConcurrentDictionary`，catalog 整文件重写 | 时序高基数 | 🟠 内存 |
| P1 | 向量 ANN 为“每 block 一个 HNSW”，MemTable 与含墓碑 block 回退暴力扫描 | 向量 | 🟠 召回/延迟随段数劣化 |

---

## 1. 存储内核缺陷（跨模型共享，优先级最高）

### P0-1 KV checkpoint 是全量重写，不是 LSM

- 位置：[KvKeyspace.cs:3218](../../src/SonnetDB.Core/Kv/KvKeyspace.cs) `RunCheckpointCore` → `EnumerateVisibleEntries(..., readDiskValues: true)` → `KvStateFile.SaveSegment/SaveSnapshot`，随后 [KvKeyspace.cs:3277](../../src/SonnetDB.Core/Kv/KvKeyspace.cs) `openedState.ValidateAllEntries()` 再全量读回校验。
- 触发：overlay 达到 `MaxOverlayEntries = 100_000`（[KvOptions.cs:29](../../src/SonnetDB.Core/Kv/KvOptions.cs)）。
- 后果：keyspace 有 N 条记录时，每写入 10 万条就要 **读 N + 写 N + 再读 N**。写放大 ≈ N / 100k。1 亿行表每 10 万次写入重写 ~全表——这是 O(N²) 的总写入量。
- 受影响：关系表（`TableStore` 基于 `KvKeyspace`）、文档集合、原生图、对象存储元数据、Sequence、RAG 存储——即除时序/MQ/对象数据体外几乎全部模型。
- 竞品机制：RocksDB/LevelDB/Pebble（SQLite 是 B-Tree 原地页更新）采用分层 SST + 增量合并，写放大为 O(层数×扇出)，与总数据量无关。
- 建议：把 `KvStateFile` 演进为多层 SST（L0 追加 + Leveled/Universal compaction），每个 SST 带布隆过滤器和块索引；checkpoint 只产出新的 L0 文件。这是对齐“10M parity”（当前 NOT_RUN）的前提。

### P0-2 快照读的 overlay 复制排序

- 位置：[KvKeyspace.cs:2222](../../src/SonnetDB.Core/Kv/KvKeyspace.cs) `mutableValues = mutableSource.ToArray(); ... Array.Sort(...)`，在 `_snapshotOverlayBuildGate`（单许可信号量）内完成。
- 后果：任何一次写入都使缓存失效，下一次快照读要复制并排序整个 overlay（O(M log M)，M 最高 10 万，索引重建时 100 万）。读写交替的 OLTP 负载下，每次读都付这个成本，且所有读者在门闩上串行。
- 竞品机制：RocksDB memtable 是 SkipList，快照 = 一个序列号，O(1)；PostgreSQL/InnoDB MVCC 快照也为 O(活跃事务)。
- 建议：overlay 改为持久化/并发有序结构（并发 SkipList 或带版本号的 B+ 树），条目带 sequence，快照只记录 sequence。

### P1 KV 有序扫描每个元素都重新 seek

- 位置：[KvOrderedOverlay.cs:62](../../src/SonnetDB.Core/Kv/KvOrderedOverlay.cs) `Scan`：对每个候选键都执行 `keys.GetViewBetween(lower, maximum).GetEnumerator()`。
- 后果：每产出一个键都分配一个 `TreeSubSet` 与枚举器栈并做 O(log n) 定位；范围扫描 = O(k log n) + k 次堆分配。另外 overlay 同时维护 `Dictionary<byte[],…>` 与 `SortedSet<byte[]>` 两份结构，每键内存开销高（红黑树节点 + 字典桶）。
- 图遍历每一跳、表二级索引范围扫描、文档前缀扫描都会走到这里。

### P1 关系表打开时可能全量重建索引与计数

- 位置：[TableStore.cs:67](../../src/SonnetDB.Core/Tables/TableStore.cs) 非干净关闭时 `RebuildIndexesLocked()`；[TableStore.cs:69](../../src/SonnetDB.Core/Tables/TableStore.cs) 每次打开 `CountPrefix` 全量计数。
- 后果：崩溃后冷启动时间与表大小线性相关；大表重启 RTO 不可控。竞品（PostgreSQL/SQLite）索引本身持久且由 WAL 保护，不需要重建；行数由统计信息估算。
- 建议：二级索引写入纳入同一 KV 原子批次（看起来已是同 keyspace），靠 WAL 回放保证一致即可去掉重建；行数持久化到元数据键。

---

## 2. 时序模型（主场，对标 InfluxDB 3 / TDengine / IoTDB / QuestDB / TimescaleDB）

### P0-3 写入路径单锁串行 + WAL 记录粒度过细

- 位置：[Tsdb.cs:906](../../src/SonnetDB.Core/Engine/Tsdb.cs) `WriteCore` 与 [Tsdb.cs:1125](../../src/SonnetDB.Core/Engine/Tsdb.cs) `WriteManyChunk` 均在 `lock (_schemaSync) lock (_writeSync)` 内完成 schema 校验、catalog、WAL 追加、MemTable 追加。
- [Tsdb.cs:2288](../../src/SonnetDB.Core/Engine/Tsdb.cs) `WritePointLocked`：**每个字段一条 WAL 记录**（`AppendWritePoint(seriesId, ts, fieldName, value)`，字段名字符串每次重新编码），一个 10 字段的点 = 10 条记录 + 10 次 CRC + 10 次字段名编码。
- 每点 `SeriesKey.FromPoint` 重建规范字符串并哈希（[SeriesCatalog.cs:57](../../src/SonnetDB.Core/Catalog/SeriesCatalog.cs)），每字段 `SeriesFieldKey(seriesId, fieldName)` 字符串哈希查找 `ConcurrentDictionary`。
- MemTable 是按 (series, field) 的 `List<DataPoint>` 行存，`DataPoint` 内含 `FieldValue`（可能含引用）——GC 扫描压力大。
- 竞品机制：TDengine 按 vnode 分片并行写、行转列批量 WAL；IoTDB 每 DataRegion 独立写锁；QuestDB 每表单写者但列式追加；InfluxDB 3 每批一条 WAL。
- 建议：
  1. WAL 改为“每批（或每点）一条记录、字段名用 schema 列序号编码”；
  2. 写锁按 measurement 或 series 哈希分片；schema 锁只在 schema 变化时获取（读写锁/版本号）；
  3. 客户端批量协议直接带 seriesId 缓存（类似 TDengine 子表）跳过每点 tag 规范化；
  4. MemTable 用按列的 `long[]`/`double[]` 分块数组替代 `List<DataPoint>`。

### P0-4 无时间分区 + 单一全局 TTL

- [CompactionPlanner.cs](../../src/SonnetDB.Core/Engine/Compaction/CompactionPlanner.cs) 纯 Size-Tiered，按文件大小分 tier，不看时间范围；段里混合所有 measurement 与任意时间范围。
- [RetentionPolicy.cs:10](../../src/SonnetDB.Core/Engine/Retention/RetentionPolicy.cs) 只有一个全库 `Ttl`，**没有按 measurement/数据库的保留期**。
- 后果：大段跨越很宽的时间范围，几乎永远不会“整段过期”，只能注入墓碑再靠 compaction 重写（每轮最多 1024 个墓碑）；乱序/补录数据会把旧时间段拉进新段，导致反复重写。
- 竞品：InfluxDB shard group（按时间）、TDengine `DURATION`/`KEEP` 按库、IoTDB time partition + 路径 TTL、TimescaleDB chunk drop——过期都是 O(1) 删文件。
- 建议：段写入按 `(measurement 组, 时间窗)` 分区，compaction 只在同分区内进行；`CREATE MEASUREMENT ... WITH TTL` / `ALTER MEASUREMENT SET TTL`。

### P0-5 重复时间戳语义

- [MemTableSeries.cs:443](../../src/SonnetDB.Core/Memory/MemTableSeries.cs) `StableSorted` 保留同时间戳的所有点；测试 `SegmentCompactorTests.Execute_SameTimestamp_AllPointsPreserved`（tests/SonnetDB.Core.Tests/Engine/Compaction/SegmentCompactorTests.cs:153）把这当成合同。
- 后果：MQTT/Kafka/HTTP 的至少一次投递重试会让 `count/sum/mean` 变大；`LAST` 依赖追加顺序。
- 竞品：InfluxDB / TDengine / IoTDB 默认同键覆盖；QuestDB 提供 `DEDUP UPSERT KEYS`；TimescaleDB 可用唯一约束 + `ON CONFLICT`。
- 建议：增加 measurement 级 `DUPLICATE POLICY {KEEP_ALL | LAST | FIRST}`，默认值的变化需按 AGENTS 4.1 做兼容说明；flush/compaction/merge 时按策略折叠。

### P1 压缩率

- [ValuePayloadCodecV2.cs:33](../../src/SonnetDB.Core/Storage/Segments/ValuePayloadCodecV2.cs)：`Int64` 仍是原始 8 字节（只有可选 RLE），无 delta/zigzag/Simple8b；整个段无通用块压缩（ZSTD/LZ4）。
- 计数器、状态码、工业整数寄存器（Modbus）恰是 Int64 主力场景。竞品：TDengine 两级压缩（delta + LZ4/ZSTD）、IoTDB TS_2DIFF/Chimp + LZ4、InfluxDB 3 Parquet + ZSTD。
- 建议：Int64 采用 delta + zigzag + Simple8b/位打包；Float 对 Gorilla 输出再加可选 LZ4；String 字典之外加 ZSTD。

### P1 高基数

- [TagInvertedIndex.cs:32](../../src/SonnetDB.Core/Catalog/TagInvertedIndex.cs) 三层嵌套 `ConcurrentDictionary<string, ConcurrentDictionary<string, ConcurrentDictionary<ulong, byte>>>`，每个 (series, tag) 约 100+ 字节；[CatalogFileCodec.cs:50](../../src/SonnetDB.Core/Catalog/CatalogFileCodec.cs) catalog 脏时整文件重写。
- 1000 万 series × 5 tag ≈ 数 GB 常驻内存，且每次 flush 重写整个 catalog。
- 竞品：InfluxDB TSI 磁盘倒排 + 位图；IoTDB/TDengine 元数据分片。建议：tag 倒排改为 Roaring Bitmap（series 序号化），catalog 增量追加日志 + 定期快照。

### 时序已有优势（保持）
delta-of-delta 时间戳、Gorilla 浮点、字符串字典；惰性解码多路合并（[BlockSourceMerger.cs](../../src/SonnetDB.Core/Query/BlockSourceMerger.cs)）；聚合 sketch/下推；MemTable 增量聚合快路径；SIMD 聚合；PID/预测/异常检测函数——这些是 InfluxDB/TDengine 没有内置的差异化能力。

---

## 3. SQL / 查询执行（对标 PostgreSQL、DuckDB、DataFusion、ClickHouse）

- **结果全量物化**：[SelectExecutionResult.cs:18](../../src/SonnetDB.Core/Sql/Execution/SelectExecutionResult.cs) `IReadOnlyList<IReadOnlyList<object?>> Rows`；`RelationalSelectExecutor` 有 110 处 `object?[]`。每个数值装箱，大查询内存 ≈ 行数 × 列数 × 24B+。依赖 `Truncated` 预览而不是流式游标。建议引入 pull 式 `IAsyncEnumerable<RowBatch>`，列向量（`double[]`/`long[]` + null bitmap）表示批次，服务端分块写出。
- **执行器按模型分散**（TableSqlExecutor 7k 行、RelationalSelectExecutor 6.8k 行、SelectExecutor 3.6k 行、DocumentSqlExecutor、GraphTableSqlExecutor…），没有统一的逻辑计划→物理计划层；同一优化（谓词下推、Top-N、spill）需要在多处重复实现，跨模型 JOIN 的优化空间受限。
- **向量化/并行**：只有数值聚合与向量距离使用 SIMD；全库 `Parallel.` 只出现 1 处（TableManager）。DuckDB/ClickHouse/DataFusion 的 morsel 并行 + 列批次是分析查询 10～100× 的来源。
- **SQL 能力缺口**（解析器核验）：
  - 窗口函数只支持 `PARTITION BY`/`ORDER BY`，**不支持帧** `ROWS/RANGE BETWEEN`（[SqlParser.cs:4700](../../src/SonnetDB.Core/Sql/SqlParser.cs)）；
  - `INTERSECT/EXCEPT ALL` 不支持（[SqlParser.cs:3010](../../src/SonnetDB.Core/Sql/SqlParser.cs)）；
  - 无 `LATERAL`、无 `MERGE`；
  - 事务仅覆盖关系表，**measurement/document 写入在事务中被拒绝**（[SqlTransactionContext.cs](../../src/SonnetDB.Core/Sql/Execution/SqlTransactionContext.cs) 注释 #199），默认读已提交——作为“多模型”数据库，缺少跨模型原子事务（对比 SurrealDB、ArangoDB、Couchbase 均支持跨模型 ACID）。
- 已有亮点：成本规划（统计/直方图/MCV）、3～6 表 join 排序、hash/INL/merge join、spill、`EXPLAIN ANALYZE` 指标完整度高于多数嵌入式数据库。

---

## 4. 九种模型逐项对照

| 模型 | SonnetDB 现状（源码） | 主要竞品机制 | 缺陷 / 性能风险 | 缺失能力 |
|---|---|---|---|---|
| 时序 | MemTable + WAL + 段文件，DoD/Gorilla/字典，sketch 下推 | InfluxDB 3、TDengine、IoTDB、QuestDB、TimescaleDB | 见 §2：单写锁、逐字段 WAL、无时间分区、重复点、Int64 不压缩、高基数内存 | 按表 TTL、去重策略、降采样分层存储（冷热）、对象存储分层 |
| 关系表 | 行存于 KvKeyspace，二级/复合/覆盖索引，统计与成本规划，FK/CHECK/触发器/存储过程 | PostgreSQL、SQLite、DuckDB | 继承 P0-1/P0-2；打开时索引重建；结果装箱 | 窗口帧、MERGE、LATERAL、表分区、列存/分析副本、MVCC 快照隔离默认级 |
| KV | Dictionary+SortedSet overlay + 单一状态文件分页索引，TTL/CAS/计数器 | RocksDB、Redis、LMDB | P0-1 全量 checkpoint；P0-2 快照复制排序；扫描逐元素 seek；无布隆过滤器、无块缓存（仅弱引用页） | Redis 数据结构（List/ZSet/Stream）完整度、压缩、前缀布隆 |
| JSON 文档 | JSON 文本存 KV，路径索引/TTL 索引/校验器/聚合管线/变更流 | MongoDB、PostgreSQL JSONB、Couchbase | 每次谓词/更新/聚合 `JsonDocument.Parse`（如 [DocumentCollectionStore.cs:1663](../../src/SonnetDB.Core/Documents/DocumentCollectionStore.cs)）；smoke 中 32 谓词索引查询 1.4ms/895KB 分配 | 二进制文档格式（JSONB/BSON 式偏移跳转）、部分更新不重写全文、多键数组索引覆盖查询 |
| 全文 | BM25/BM25F、短语、模糊、jieba/CJK 分词、段合并 | Lucene/Elasticsearch、Tantivy、Meilisearch | 穷举打分：[InMemoryFullTextIndex.cs:147](../../src/SonnetDB.Core/FullText/Index/InMemoryFullTextIndex.cs)、[PersistentFullTextIndex.cs:194](../../src/SonnetDB.Core/FullText/Storage/PersistentFullTextIndex.cs) 用 `Dictionary<string,double>` 累加全部命中再排序；docId 为 string | WAND/Block-Max WAND 提前终止、posting 跳表、高亮片段、同义词/停用词管理、聚合 facet 的位图实现 |
| 向量 | Flat/量化 Flat/HNSW/IVF-Flat/IVF-PQ/Vamana(DiskANN)/OPQ/RQ，SIMD 距离 | Qdrant、Milvus、pgvector | measurement 侧每 block 一个 HNSW，多段时需遍历多图；MemTable 与含墓碑 block 回退暴力扫描（[KnnExecutor.cs](../../src/SonnetDB.Core/Query/KnnExecutor.cs)）；HNSW 单 `ReaderWriterLockSlim`，插入阻塞全部搜索（[HnswIndex.cs:47](../../src/SonnetDB.Core/Vector/Index/Hnsw/HnswIndex.cs)） | 全局（跨段）索引与后台合并、payload 过滤感知遍历（Qdrant 式）、稀疏向量/多向量（ColBERT）、Recall@K 基准（文档标注 NOT_RUN） |
| 对象 | S3 风格桶/版本/多段上传/条件请求/生命周期，元数据在 KV | MinIO、SeaweedFS | smoke：4KiB Range 读 230µs、64KiB 读 258µs（本地文件系统常见 <50µs）；元数据继承 P0-1 | 内容去重/纠删码（单节点可选）、S3 协议兼容范围、大对象并发分片读 |
| SonnetMQ | 每 topic 文件追加、消费组、ack/nack、保留、组提交 | Kafka、NATS JetStream、Redpanda | topic 内单 `SyncRoot` 串行；单文件模式所有 topic 共享一个 FileStream 与锁（[SonnetMqStore.cs:55](../../src/SonnetDB.Core/Mq/SonnetMqStore.cs)） | **无分区**（topic 内无法并行消费、无 key 有序分区）、无事务/幂等生产者、无压缩 topic（log compaction） |
| 原生属性图 | KV 键编码顶点/边/标签/属性索引，SQL/PGQ/GQL，加权路径、离线算法 | Neo4j、Memgraph、Kuzu | 每跳一次 KV 前缀范围扫描，继承 §1 逐元素 seek；邻接不是 index-free adjacency；容量门禁（1M 顶点/10M 边）NOT_RUN | 完整 GQL/openCypher 覆盖、图算法库广度（PageRank/社区/中心性已部分离线）、变长路径的工作集预算之外的剪枝 |

---

## 5. 横向对照：SonnetDB 的定位

| 维度 | SonnetDB | SurrealDB | ArangoDB | PostgreSQL + 扩展 | DuckDB | TDengine |
|---|---|---|---|---|---|---|
| 模型数 | 9 原生 | 文档/图/KV/时序/向量 | 文档/图/KV/搜索 | 关系为核心，扩展补齐 | 分析关系 | 时序 |
| 存储内核 | 自研（KV 全量 checkpoint + 时序段） | RocksDB/TiKV | RocksDB | 堆表 + WAL | 列存 | 列存 + 时间分区 |
| 跨模型事务 | ❌（仅关系表） | ✅ | ✅ | ✅ | ✅ | ❌ |
| 向量化执行 | 部分（聚合/距离） | ❌ | ❌ | ❌（JIT 部分） | ✅ | ✅ |
| 嵌入式 + Server 双形态 | ✅（.NET、NativeAOT） | ✅ | ❌ | ❌ | ✅ | ❌ |
| 工业/AI 内置（PID/预测/RAG/Modbus） | ✅ 独有 | ❌ | ❌ | 扩展 | ❌ | 部分 |

**差异化优势**：.NET 原生嵌入 + NativeAOT、工业 IoT 函数库、内置 RAG/Copilot/MCP、单一 SQL 横跨九模型、可观测性与有界预算工程。
**最大短板**：通用存储内核（KV）不具备大数据量伸缩性——这会同时拖累六种模型；时序引擎写入并行度与保留/去重语义落后于专业时序库。

---

## 6. 建议路线（按收益/风险排序）

1. **KV 内核 LSM 化**（P0-1/P0-2/扫描 seek）：并发 SkipList memtable + 多层 SST + 布隆 + 块缓存 + sequence 快照。一次投入解决关系/文档/图/对象元数据的容量问题。需按 AGENTS 4.1 升级格式版本并提供旧格式迁移。
2. **时序写路径**：批级 WAL 记录 + 字段序号编码 + 分片写锁；MemTable 列式化。
3. **时序分区与语义**：按时间窗分区 compaction、measurement 级 TTL、去重策略。
4. **Int64/通用块压缩**。
5. **SQL 流式结果 + 列批次执行**，先覆盖时序与关系表扫描/聚合。
6. **全文 Block-Max WAND**、**向量全局索引 + 过滤感知遍历**、**MQ 分区**。
7. 补齐容量证据：10M KV、百万文档、1M/10M 图、10k/100k 向量 Recall@K（当前均 NOT_RUN）。

## 7. 证据边界

| 层级 | 状态 |
|---|---|
| 源代码阅读 | PASS（本报告所列文件与行号） |
| 构建 / 单元测试 | NOT_RUN |
| 本轮基准 | NOT_RUN（引用 2026-09-01 本机 smoke 数据） |
| 竞品同机对比 | NOT_RUN（仅公开架构机制对照） |
| 固定硬件 / 生产 | NOT_RUN |
