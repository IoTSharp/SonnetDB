# 九模型能力交叉核查 (2026-09-05)

## 证据范围与结论

本报告核查基线为 `3b5ff768adf946b09a3be11ed58a83cacb3c1696` 与当前工作区。核查范围包括核心实现、公开 API、Server 路由、相关测试源码及模型专题文档；这是有界源码审计，不是九域全量测试、现场容量或发布认证。测试文件及测试名称表示已有自动化覆盖，除文末明确记录的新运行外，不表示本次已经运行通过。旧 CHANGELOG 的测试数量不作为当前通过数。

九种模型都能找到实际读写、查询或索引执行路径，并非九个空壳类型。它们共享宿主和部分基础设施，但并非九个相同成熟度、相同事务边界、相同耐久配置的专用数据库。尤其 Graph 仍为 Beta 范围，Document 容量承诺只到已取得证据的档位；统一管理入口也不能证明统一恢复闭环。

这里的“实现”只指代码与真实入口相连；“缺口”区分实现缺口、证据缺口与明确排除项。文中的路径相对仓库根，行号取本轮读取时位置，后续修复可能移动行号。

## 九模型矩阵

| 模型 | 已实现的原生语义和真实入口 | 尚缺或有限范围 | 当前可声明 |
|---|---|---|---|
| 时序 | measurement schema、带 TAG/FIELD 的点写入、WAL/replay、segment、范围查询、窗口/多聚合、tombstone、retention；SQL/HTTP/Frame | 固定高基数/多 measurement/碎片段档位与长稳未取齐；同 time/TAG 身份删除后重写仍受 tombstone 规则限制，UI 校正要求改变身份 | 真正时序模型；容量和覆盖式更新语义需按合同声明 |
| 关系表 | KV-backed 独立 rowstore、主键/二级索引、约束、SQL CRUD/JOIN、读快照；SQL HTTP 入口 | 多表轻事务只有进程内补偿回滚，没有跨 keyspace 掉电原子性；SQL 是实用子集；自动统计、规划/物化/锁/GC 等仍需 M41/M42 实测 | 真正关系模型；不能宣称完整 SQL、跨表崩溃原子事务或 PostgreSQL 性能等价 |
| KV | raw bytes、TTL、版本 CAS、递增、批次、prefix/range 与 snapshot；嵌入式 NX/XX、GetAndSet/GetAndDelete；HTTP/Frame 旧接口 | NX/XX/原子交换的新合同尚未远程接线；分页/批次分项结果与诊断未形成 M36 闭环；无跨 keyspace 原子事务 | 真正持久 KV；不是 Redis 协议或 Redis 数据结构全集 |
| Document | 集合、JSON path、CRUD/partial update、mixed Bulk、validator、path/compound/multikey/wildcard 索引、change feed、派生搜索；SDK/SQL/HTTP/工作台 | MongoDB-native pipeline/array/collation 仅明确子集；百万/千万报告未取得；通用过滤向量路径仍全扫 | 真正 JSON 文档模型与 MongoDB-like 单机子集 |
| 全文 | 倒排 segment、BM25/BM25F、位置短语/布尔、模糊词扩展、Unicode/CJK/中文分词、重建；Document/SQL/管理 HTTP | 吞吐、词项膨胀、merge、中文相关性与长稳无本轮新证据；不是 Elasticsearch/OpenSearch 协议全集 | 真正全文检索模型；派生索引依附 Document 主数据是有效设计 |
| 向量 | Measurement 的 HNSW/IVF/IVF-PQ/Vamana 派生索引、Document HNSW、精确距离回退、SQL KNN/vector/hybrid、Frame；本轮新增纯 metadata WHERE 的距离计算前过滤 | 通用 Document WHERE 不使用 filtered ANN；距离/评分/通用函数谓词仍走全量残差路径，存储 Scan 仍全量物化；目标模型/质量/召回/规模仍需分别证明 | 真正向量检索能力；不能把多种索引名称等同生产容量或自动多模态理解 |
| 对象 | blob 文件与 KV 元数据、bucket、版本/删除标记、range、multipart、copy、policy/lifecycle/hold/quota/presign；S3 风格路由 | 当前 ListObjects 每页全桶扫描/解码/排序；完整 AWS S3 身份/协议、分布式对象存储等价不能由这些入口推导；一致备份仍需对象并发写 journey | 真正对象存储；兼容性必须列出操作、鉴权和边界 |
| MQ | append-only 日志、topic offset、consumer group cumulative ack、批量/组提交、分段/稀疏索引/热尾/冷读/retention、replay；HTTP/Frame/SDK | 本轮发现 ack 返回值不一致和 long.MaxValue 溢出；默认仅 OS flush；服务端 MQ 位于全局 `.system/mq`，没有接入单数据库备份合同；无证据支持 exactly-once 事务或分布式队列等价 | 真正本地持久消息队列；必须单列耐久与备份边界 |
| 原生属性图 | 原生顶点/边/标签/属性/双向邻接，GraphTransaction，snapshot/cursor/index seek/expand/traversal，SQL/PGQ、远程 CRUD/NDJSON、工作台 | 正式外部 Neo4j/PostgreSQL 对拍、LDBC/Graphalytics、固定容量、Couplet 联合发布、168 小时未通过；受限 GQL 不等同完整 GQL/Cypher | 真正原生属性图实现，当前只按 Graph Beta 范围声明 |

## 逐模型证据

### 1. 时序

- 实现与接线：`src/SonnetDB.Core/Engine/Tsdb.cs:684` / `:731` / `:767` 提供单点和批量写；`:499` 读取 tombstone manifest 并进行 WAL recovery；`:864` 追加删除记录；`src/SonnetDB.Core/Query/QueryEngine.cs:118` 范围查询、`:437` 聚合、`:707` 多聚合；`src/SonnetDB/Endpoints/Routes/SqlEndpoints.cs:34` 提供实际 SQL 入口。
- 原生性：`QueryEngine.cs:204` 与 `:2134` 按时间墓碑过滤，`:2030` 从 block descriptor 聚合；不是把时间字段塞入通用字典后全由应用聚合。
- 缺口：`docs/management-tools.md` 明确“点校正需改变 time/TAG 身份”，应向用户解释为版本/删除覆盖语义限制。M19 的高基数、多小段和大量 measurement runner 已存在，但未取得目标硬件报告不能推导容量。
- 下一切片：建立写入、查询、删除、同身份重写、flush/reopen 的 golden journey，冻结是否支持同身份覆盖；如要支持，先设计 LSN/版本可见性而不是仅修改 UI 放开编辑。验收必须含跨 segment、retention、backup/reopen 的值一致性。

### 2. 关系表

- 实现与接线：`src/SonnetDB.Core/Tables/TableStore.cs:9` 为独立行存储；`:43` 起迁移标记与索引恢复，`:73` 载入行数/统计；`Tsdb.cs:149` 公开 Tables；`SqlEndpoints.cs:34` / `:63` 提供单条和批次 SQL。
- 测试源码证据：`tests/SonnetDB.Core.Tests/Sql/SqlExecutorTableTests.cs:104` schema reopen、`:132` CRUD、`:214` 重复主键、`:252` LIMIT 早停、`:276` streaming Top-N、`:509` 三表 JOIN。主线程另行核查 M41/M42 统计/规划器，本报告不把这些历史测试计为本次通过。
- 事务关键边界：`src/SonnetDB.Core/Tables/TableManager.cs:689` 的公开文档明确，多表 DML 轻事务使用进程内反向补偿；各表有独立 WAL，因此不提供跨 keyspace 的掉电原子性。单表 batch 原子性不能外推为完整跨表 ACID，更不能外推到九模型事务。
- 缺口：ROADMAP 的“未来任务”与已存在实现必须分别核实，特别是显式 ANALYZE、成本规划、spill 等已实现内容不能重复派单；固定硬件、生产相同语料和 mixed workload 仍独立取证。
- 下一切片：优先修复经慢查询/计数器确认的路径，报告 examined/returned、估算偏差、锁/permit 等待、分配/GC、spill 与 P95/P99，同时对照内存路径、事务和 NULL 语义。

### 3. KV

- 实现：`src/SonnetDB.Core/Kv/KvKeyspace.cs:614` 条件 Set、`:708` GetAndSet、`:837` CAS、`:973` TTL、`:1046` GetAndDelete、`:1112` 单 CRC WAL batch；`Tsdb.cs:144` 为公开入口。
- 远程实际边界：`src/SonnetDB/Endpoints/Routes/KeyValueEndpoints.cs:27` Get、`:72` Set、`:84` 调用 `Put`、`:138` CAS、`:188` Expire、`:236` ScanPrefix。该 Set 没有 NX/XX；不能用嵌入式新方法推导 HTTP/Frame 已等价。
- 测试源码：`tests/SonnetDB.Core.Tests/Kv/KvConditionalOperationsTests.cs:17` NX/XX、`:47` 过期视为缺失、`:72` 交换/TTL、`:133` reopen、`:221` sync failure unknown outcome。
- 文档漂移：`docs/kv-keyspace.md:10` 及“当前不做”仍称不提供 HTTP KV 服务，和已存在路由不符。正确区分是已有远程基础 CRUD 与本次新增操作仍仅嵌入式。
- 下一切片：M36 #316 接线 NX/XX、原子交换到 DTO/SDK/REST/Frame，补默认参数兼容、TTL、CAS/冲突、AOT、重开、错误 parity 和 UI 审批；再做有界 cursor/分项批次。不要以网络层 Get+Put 拼接原子操作。

### 4. Document

- 实现与入口：`src/SonnetDB.Core/Documents/DocumentCollectionStore.cs` 为真实主文档与索引执行；`src/SonnetDB/Endpoints/Routes/DocumentEndpoints.cs:135` / `:173` / `:214` / `:325` 分别调用 insert、BulkWrite、find 与 findOneAndUpdate；`docs/document-mongodb-gap.json` 已提供逐项四态声明。
- 原子恢复证据源码：`tests/SonnetDB.Core.Tests/Documents/DocumentAtomicMultikeyWildcardTests.cs:16` 主文档/path index/feed 同一 KV version；`:73` 拒绝 compound parallel arrays；`:92` wildcard planner/reopen；`:147` 派生 fulltext repair marker 在 reopen 修复。
- 明确排除：MongoDB wire/driver、replica set/sharding、完整 BSON/MQL、positional array update、hashed/geospatial Document index，以及原生 `$lookup/$facet/$bucket` 已列为排除项；它们不应当被标为“承诺了但漏做”。locale collation 是无日期后续候选。
- 缺口与下一切片：固定 million/ten-million 档位未验收；先跑迁移 -> query/update -> change feed resume -> 索引损坏恢复 -> backup/reopen 的完整小规模 journey，再在固定档位扩大；不要重复实现已落地 M32 update/index/feed。

### 5. 全文

- 实现：`src/SonnetDB.Core/FullText/DocumentFullTextIndexStore.cs:73` 接入 PersistentFullTextIndex；`:162` search、`:295` phrase、`:322` fuzzy term expansion、`:410` tokenizer；`src/SonnetDB.Core/FullText/Storage/PersistentFullTextIndex.cs:662` 使用 BM25 打分，`:425` 从持久 segment manifest 重建视图。
- 入口：`src/SonnetDB/Endpoints/Routes/ManagementContractEndpoints.cs:613` 索引、`:641` 查询、`:677` 调用 Store.SearchFullText、`:693` analyze；DocumentCollectionStore 对 CRUD 维护派生全文索引。
- 测试源码：`tests/SonnetDB.Core.Tests/FullText/Core/PersistentFullTextIndexTests.cs:15` 持久重开、`:47` 删除墓碑、`:64` merge、`:130` phrase/near reopen、`:152` manifest 缺失恢复、`:355` 活跃词项缓存失效。
- 缺口与下一切片：把固定中英文工业语料、更新 churn、词典变更、phrase/fuzzy、权限过滤与重建取消加入 golden journey；统一统计索引/候选/实际评分数及 merge I/O。没有实验不得承诺 Lucene/Elasticsearch 等价性能或相关性。

### 6. 向量

- 实现：`src/SonnetDB.Core/Vector/Indexing/LocalVectorIndexBuilder.cs:27` / `:29` / `:30` / `:31` 分发 HNSW/IVF/IVF-PQ/Vamana 的实际 builder；`src/SonnetDB.Core/Documents/Vector/DocumentVectorIndexStore.cs:49` 持久 KV-backed HNSW、`:179` filtered traversal、`:250` 从权威向量重建 HNSW。
- 测试源码：`tests/SonnetDB.Core.Tests/Sql/SqlExecutorVectorTests.cs:44` schema index reopen、`:63` 向量 round-trip、`:85` 维度拒绝；`tests/SonnetDB.Tests/VectorFrameEndpointTests.cs:188` SQL/Frame 对拍、`:231` tag filter、`:242` time range、`:256` 错误帧。
- 基线性能缺口：`src/SonnetDB.Core/Sql/Execution/DocumentVectorSearchExecutor.cs` 对任何 WHERE 直接放弃 ANN；原先先 `ScoreRows` 再 `ApplyWhere`，`store.Scan()` 读取全部文档并计算距离、分配行列表。本轮已针对整条纯 metadata 谓词先过滤再算距离，含距离/评分/通用函数的复杂谓词仍保留原残差路径，过滤 ANN 推广与存储扫描物化仍未解决。
- 谓词边界：当前 vector executor 的 `ValuesEqual(null, null)` 返回 true，`NOT` 使用布尔取反；这不是完整 SQL 三值 NULL 逻辑。本次优化使用同一求值器并对拍现有行为，未借性能修改改变合同。是否统一 Document/Hybrid/Vector 的 SQL NULL 语义需要单独审查和迁移验收。
- 下一切片：把已存在的 indexed prefilter/filtered HNSW/exact fallback 合同推广至通用 SQL；先保证 filter-before-distance 与 bounded Top-K，声明近似/精确模式和 fallback reason，再验证召回。验收需含高/低选择率、index 漂移、取消、OOM 边界、随机差分、相同向量模型与维度校验。

### 7. 对象

- 实现：`src/SonnetDB.Core/ObjectStorage/SndbObjectStore.cs:146` 内容写入、`:200` 目录 fsync、`:326` Range 读取、`:391` delete marker、`:582` lifecycle；`src/SonnetDB/Endpoints/Routes/ObjectStorageEndpoints.cs:26` / `:34` / `:43` / `:55` 接入 bucket/object/presign。
- 测试源码：`tests/SonnetDB.Core.Tests/ObjectStorage/SndbObjectStoreTests.cs:44` 完整文件发布、`:73` Range、`:210` 元数据 sync failure 保留可恢复内容、`:418` multipart 原子恢复、`:688` bucket 删除竞争、`:911` 路径逃逸拒绝。
- 明确性能缺口：`SndbObjectStore.cs:258` 用 `ScanPrefix(..., limit: int.MaxValue)` 遍历全部 latest pointers；`:265` 逐条读元数据；`:272` 排序全部对象，最后才处理 cursor 和 `maxKeys`。一页 10 条也会付出全桶成本，多页会重复扫描。
- 下一切片：冻结对象 key 的排序编码/分页合同，使用有界 KV range cursor 推进到 prefix/afterKey，避免编码次序与用户可见次序不一致；不能仅把 int.MaxValue 改成 maxKeys。验收含 UTF-8/转义字符、删除标记、重复翻页、prefix、并发 mutation、十万对象候选读取计数和 maxKeys+probe 上界。

### 8. MQ

- 实现：`src/SonnetDB.Core/Mq/SonnetMqStore.cs:105` publish、`:124` batch、`:194` group commit、`:227` pull、`:312` ack、`:483` replay；`:1559` 内部 consumer offset 采用 max 保持单调。
- 本轮 bug：Ack 原先用 `Math.Min(offset + 1, state.NextOffset)` 写日志并返回 next；旧 ack 的返回值可能小于实际持久 consumer offset；`long.MaxValue + 1` 溢出为负数，既返回错误值又写入错误 ACK 值。修复与运行结果见文末。
- 耐久边界：`src/SonnetDB.Core/Mq/SonnetMqOptions.cs:31` 的 `SyncOnPublish` 默认为 false；`src/SonnetDB/Hosting/SonnetDbServiceRegistration.cs:92` 只启用 `FlushOnPublish=true`。因此默认是写到 OS 页缓存后返回；reopen replay 不等于每条消息可承受断电。
- 备份缺口：`SonnetDbServiceRegistration.cs:88` / `:277` 把 Server 队列放到全局 `DataRoot/.system/mq`，逻辑 DB 通过 topic 前缀隔离。`Tsdb.cs:1076` 一致备份仅 checkpoint TS/table/doc/KV/Graph，`src/SonnetDB.Core/Backup/BackupService.cs:379` 只复制 `tsdb.RootDirectory`，没有服务端 MQ 数据/消费位点 checkpoint 合同。嵌入式 `SndbMqClient.cs:276` 在连接目录下创建 `.system/mq`，即使文件被复制也不能据此证明 MQ writer 与备份一致性已协调。
- 下一切片：优先修复 Ack 边界；再明确定义 instance backup 与 database backup 的 MQ/消费者位点/权限范围，提供 stop-publish/drain/flush/checkpoint 或稳定日志快照合同及 manifest 摘要。跨 DB 数据不可错误拷入单 DB 包；恢复后要对拍消息、header、offset、consumer group、retention 与后续 publish。

### 9. 原生属性图

- 实现：`src/SonnetDB.Core/Graphs/GraphStore.cs:192` GraphTransaction、`:209` snapshot read、`:258` rebuild、`:315` offline algorithms；`src/SonnetDB.Core/Graphs/GraphReadSession.cs:143` property index seek、`:249` native expand、`:343` explain；`src/SonnetDB/Endpoints/Routes/GraphEndpoints.cs:130` / `:145` 真实事务 vertex write，`:164` / `:179` 真实 edge write。
- 测试源码：`tests/SonnetDB.Core.Tests/Graphs/GraphPreviewPhase1Tests.cs:31` CRUD/index/reopen、`:137` supernode 有界分页、`:197` max-result 后停止解码、`:226` BFS/DFS/shortest path、`:510` unique 索引恢复、`:646` 缺失派生索引重建、`:731` typed client/NDJSON。
- 成熟度：`docs/native-graph-database-roadmap.md:285` 明确 SQL 图派生行集复用关系执行器组合 Document/Hybrid；`:300` 及 ROADMAP 保持 Production evidence NOT_RUN。存在措辞冲突：专题文档 `:287` 仍称不得宣称 Beta 已发布，而 README 已按 Graph Beta 定位。应统一“Beta 范围功能可用”和“正式 Beta/Production 发布 gate”的含义。
- 下一切片：先完成已规划的固定 workload 性能/恢复/SDK-Server-CLI-管理面 parity，再按依赖顺序运行正式对拍和 168 小时 gate；不得用 quick 或增加资源限额代替生产证据。

## 开源竞品做法：已采用与待验证

| 参照机制 | 本仓库实际证据 | 不能据此声称 |
|---|---|---|
| Redis 风格 TTL、NX/XX、原子交换、CAS | KvKeyspace 的锁/WAL/版本实现和过期/失败恢复测试 | 已实现 Redis wire protocol、Lua、所有集合结构或远程新合同完整 parity |
| MongoDB 常用文档查询/更新与 multikey/wildcard | DocumentCollectionStore、Endpoint、M32 四态 gap 与原子/恢复测试 | 官方 MongoDB Driver 即插即用或完整 MQL/BSON/分布式语义 |
| Lucene 类倒排、segment/merge、BM25、位置检索 | PersistentFullTextIndex、Bm25、phrase/near/reopen 测试 | 直接基于 Lucene 源码，或已具备相同相关性/性能/插件生态 |
| HNSW、IVF/PQ、Vamana 算法路线 | LocalVectorIndexBuilder 的实际分发与 Document HNSW | 已使用 Qdrant/Milvus 服务、完整 DiskANN 的 SSD 优化或百万级召回达标 |
| Kafka 顺序日志/分段/offset/组提交，RabbitMQ 类确认概念 | SonnetMqStore 的日志、稀疏索引、group commit 与 ack/replay | Kafka partition rebalance、复制/事务，RabbitMQ exchange/routing，或 exactly-once |
| S3 对象/版本/multipart/range/presign 工作流 | SndbObjectStore 与 S3 风格 endpoint | 全量 AWS SDK/SigV4/IAM/S3 行为兼容或分布式存储耐久性 |
| 属性图原生邻接、索引 anchor、可解释受限遍历 | GraphReadSession/GraphStore/SQL-PGQ 与有界测试 | Neo4j/Cypher 完整兼容；外部对拍尚待执行 |
| PostgreSQL/MySQL 统计、成本选择、JOIN、spill 思路 | M41/M42 的表统计/SQL 执行器与现有测试，主线程专项核查 | 已达到竞品优化器成熟度；固定语料性能报告不可由“参考了机制”替代 |

本轮未下载竞品源码，也没有做许可证/代码来源追溯，因此“采用”只指本仓库可证实的算法或工程机制。需要新学习的重点是语义/失败/性能验证方法，以及明确访问路径和资源上界，不是继续增加同名功能。

## 建议纳入路线图的闭环优先级

| 优先级 | 最小可实现切片 | 验收退出条件 |
|---|---|---|
| P0 | MQ Ack 返回值与日志 offset 修复 | 旧/重复/越界 ack、retention cutoff、long.MaxValue、单文件/目录及 reopen 一致；日志里不出现负 ACK |
| P0 | 备份声明与 MQ 归属范围校准 | README/备份文档明确 database/instance 范围；有真实恢复 journey 前不能标九模型完整备份 |
| P1 | 通用 Document vector filter-before-distance / bounded Top-K | 过滤不会先计算全量无关向量；计数器与差分证明，取消/内存有界；filtered ANN 继续单独校准 recall |
| P1 | 对象 list 的有界稳定分页 | 页成本与候选数量有关而非整个 bucket；字符排序与所有 continuation 边界通过 |
| P1 | KV 新原子合同远程 parity | Embedded/REST/Frame/SDK 同一语义和错误，0 AOT/trim 警告 |
| P1 | 每模型最少一个端到端恢复 journey | create/import -> query/update -> paginate -> diagnose -> backup/reopen -> verify；同一 fixture 贯穿 Server/SDK/UI |
| P2 | 正式九域性能和容量证据 | 默认耐久、固定数据/硬件/commit；P50/P95/P99、吞吐、分配/GC、逻辑/物理 I/O、access path、恢复一致性均归档 |
| 发布 | Graph 外部语义/容量/Couplet/168 小时门禁 | 严格 evaluator 可从原始样本复算且所有依赖闭合；未跑保持 NOT_RUN，现场后置保持 DEFERRED |

M36 的原八模型范围可扩展引用 Graph 作为第九模型的验收行，但 Graph 引擎建设仍归 M40，避免重复里程碑。M32 明确排除项应保留；不能为了“闭环”自动承诺完整竞品能力。

## 本轮修复与验证记录

- 审计阶段仅进行了只读源码/文档检查。已有测试源码不作为本次 PASS 数量。
- MQ Ack 已修复，修改限于 `SonnetMqStore.cs` 与 `SonnetMqAckBoundaryTests.cs`。新增 6 场景乘以目录/单文件两种模式，共 12 个测试实例，覆盖旧 ack、long.MaxValue、空 topic、retention cutoff、重复 ack/消费者隔离和负数拒绝，并校验实际 v1 ACK 日志与 reopen。主任务最终 Core 全量 4037/4037 PASS、0 skip，包含全部 12 个实例；`git diff --check` 通过。
- 通用 Document vector 首个性能切片修改 `DocumentVectorSearchExecutor.cs`，使用 128 AST 节点预算的纯 metadata 白名单，保持完整向量解析/维度校验并在距离计算前筛除不匹配行；不拆分混合 AND/OR，不提前执行通用标量函数。19 个新增 `DocumentVectorPrefilterTests` 实例已在最终 Core 全量回归通过，使用固定随机种子、原残差路径 oracle 与执行上下文隔离的实际距离 hook，覆盖 NULL/missing、NOT/AND/OR、JSON path、K/排序/offset、距离与评分残差、非法维度及取消。128 文档中 32 条匹配的 fixture 证实距离计算从 128 次降到 32 次；不据此宣称整体速度倍数、filtered ANN 或扫描内存有界。
- 未启动下载、服务、编译器扫描、竞品容器或后台进程；未创建待回收的临时文件。测试的临时数据库采用独占 GUID 路径并由测试清理，执行由主线程有界 runner 统一管理。
