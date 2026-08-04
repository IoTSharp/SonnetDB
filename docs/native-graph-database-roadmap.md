# 原生属性图数据库路线图

> 本文定义 SonnetDB Milestone 40 的工程路线。当前仅完成规划，不表示任何图能力已经实现。

## 1. 决策与目标

SonnetDB 的图能力采用两种数据来源、一个图计划和一套执行算子：

1. **原生属性图**：顶点、边、标签、属性和双向邻接作为一级持久化结构，遍历直接从顶点定位邻接，不把每一跳重写成关系 JOIN。
2. **SQL/PGQ 关系映射图**：通过 `CREATE PROPERTY GRAPH` 把现有关系表声明为只读属性图，保留 PostgreSQL SQL/PGQ 用户熟悉的迁移路径。
3. **统一图执行层**：原生图和关系映射图都绑定为同一种图逻辑计划；底层分别使用原生邻接访问器和关系索引访问器，不复制模式匹配、路径语义、过滤、投影和资源治理代码。
4. **SQL 可组合**：`GRAPH_TABLE` 把图匹配结果作为行集交给现有 SQL 上层完成 JOIN、GROUP BY、ORDER BY 和投影；图内扩展仍由图执行器完成。

完成 M40 前，SonnetDB 的正式定位仍是“八种数据模型，一套引擎”。只有生产验收门禁通过后，才统一修改为“九种数据模型，一套引擎”，不能以 Parser、原型或表映射图提前宣称原生图数据库已经可用。

### 1.1 必须达到的结果

- `VertexId -> adjacency -> Edge/Vertex` 的单跳成本与全图规模无关，只与目标顶点度数和过滤结果有关。
- 边记录、出邻接、入邻接、标签索引和属性索引在一次提交中保持一致。
- 固定长度、可变长度和最短路径查询使用流式/拉取式算子，不全量物化中间路径。
- 嵌入式 API、远程 SDK 和 SQL 对相同图语义、取消、超时、错误与权限保持一致。
- WAL 回放、检查点、备份恢复、索引重建和故障注入能够验证图不变量。
- 图查询可与 Table、Document、Vector、FullText 和时序结果组合，但不复制这些模型的主数据和索引。

### 1.2 明确不做

- 不把 SQL/PGQ 的关系 JOIN 重写包装成“原生图存储”。
- 不引入 Neo4j Bolt、PostgreSQL wire、完整 Cypher、完整 GQL 或竞品插件兼容承诺。
- 不在第一版实现分布式分片、复制、集群一致性或跨数据库图事务。
- 不把 Graphify、实体抽取、LLM 推理、社区摘要或 GraphRAG 工作流放入 Core。
- 不建立第二套 KV/WAL、第二套 SQL 表达式系统、第二套向量/全文索引或第二套权限体系。
- 不为尚未出现的性能问题预建复杂 page store、分布式 ID 或专用缓存层。

## 2. 现有基础与复用策略

| 现有能力 | 直接复用 | 需要改进 | 禁止的重复建设 |
|---|---|---|---|
| `KvKeyspace` | 有序 key、prefix/range scan、WAL、CRC、checkpoint、原子 mixed batch、版本号 | 增加稳定读快照/lease 和前向游标，避免图遍历逐页复制全部 key/value；形成通用 KV 能力 | 新建 Graph WAL、Graph LSM 或旁路文件数据库 |
| `TableKeyCodec` / `TableIndexCodec` | 大端可排序标量、复合键、前缀后继和范围编码方法 | 抽取不依赖 Table schema 的内部 sortable scalar codec，Table 与 Graph 共同消费 | 复制一份仅改命名的图属性索引编码器 |
| `TableStore` / `TableManager` | 行约束、乐观版本、索引维护和错误合同可作为设计参照 | 图事务必须在单 graph keyspace 内提交；不能依赖多表补偿回滚提供 crash atomicity | 用 Node/Edge/Adjacency 三张表和 JOIN 冒充原生图 |
| SQL Lexer/Parser/AST/Binder | 参数、标量表达式、函数、权限、取消、错误位置、外层关系组合 | 新增图 DDL/PGQ AST 与 Graph Binder；绑定后进入 Graph Logical Plan | 新建独立 SQL parser 或复制标量表达式 evaluator |
| View Catalog/依赖图 | 版本化定义、落盘后发布、依赖阻断 | Graph Catalog 复用相同生命周期模式，但使用独立格式版本 | 把图定义伪装为普通 view 文本 |
| `BackupService` | 一致性锁、各模型 checkpoint、manifest、checksum、verify/restore | manifest 增加 graph/catalog/index 摘要和可重建标志；备份前 checkpoint 已打开图 | 单独实现 graph backup 命令和包格式 |
| CrashTests/Benchmarks/Parity | 真子进程 kill、报告合同、能力标志、竞品 adapter 和固定目标硬件证据 | 增加 Graph capability、Neo4j 原生图对照和 PostgreSQL SQL/PGQ 语义对照 | 新建另一套测试 runner/report schema |
| Vector/FullText/Document/Object | 向量召回、证据文档、全文、原始对象和生命周期 | 通过稳定 ID/reference 与查询组合，不复制主数据 | 在 graph property 中内嵌大文档、媒体正文或第二份 embedding |
| Server/ADO.NET/SDK/Workbench | 连接、鉴权、审计、CommandTimeout、取消、流式 Frame/HTTP 和共享工作台 | 增加 Graph 专用 typed API、流式结果和管理页 | 另建 Graph Server、认证和传输协议 |

### 2.1 先改进、后复用的公共地基

以下改造不是“为图预写一个新引擎”，而是现有 KV/SQL 已经存在、图场景会放大的通用缺口：

- KV 稳定读快照和前向范围游标：游标生命周期内保持一致视图，按页解码，不在 keyspace 写锁内执行用户回调。
- 有界 key/value 所有权：明确 borrowed memory、页缓冲和复制边界；公共 API 不暴露失效 Span。
- 通用可排序标量 codec：支持 null、Int64、Float64、Boolean、String、DateTime、Blob/Json 的类型标签和确定性排序；复用现有表索引规则。
- `GraphPropertyValue` 可以作为图公共 API 的必要类型化包装，但 Phase 0 不迁移或替换现有 `FieldValue`、`TableColumnType` 和公开 Table API；只抽取双方真正共享的内部编码原语和测试向量。
- 流式执行结果合同：复用 ADO.NET `DbDataReader`、远程 Frame 流和取消链路，不再以 `SelectExecutionResult` 承载大路径集合。
- 执行预算：统一 timeout、cancellation、最大行数、最大返回字节，并增加最大深度、最大 frontier、最大路径数和单顶点 expansion 配额。

这些改造必须由实际 benchmark 和分配证据约束。Phase 0 不引入分片、MVCC page tree 或复杂缓存。

## 3. 原生图架构

```text
Native Graph API       SQL/PGQ + GRAPH_TABLE       后续可选 GQL 子集
        |                       |                         |
        +--------------- Graph Binder ------------------+
                                |
                       Graph Logical Plan
                                |
             NodeSeek / Expand / Filter / Path / Project
                     /                         \
          NativeGraphAccessor          RelationalGraphAccessor
                  |                              |
             GraphStore                    TableStore/index
                  |
      GraphCatalog + graph KvKeyspace
                  |
        现有 KV WAL/checkpoint/snapshot
```

关键边界：

- `RelationalSelectExecutor` 只消费 `GRAPH_TABLE` 最终产生的行，不执行原生图的逐跳扩展。
- 原生 API、SQL/PGQ 和未来可能的 GQL 子集只负责不同语法/调用面，绑定后必须共享 Graph Logical Plan。
- 原生图和关系映射图使用不同 accessor；关系映射图不获得“原生存储”标记，`EXPLAIN` 必须显示实际来源和访问路径。

## 4. 数据模型与持久化不变量

### 4.1 元素与标识

- 一个数据库可包含多个命名 graph；第一版一个 graph 对应一个 `KvKeyspace`，事务边界不跨 graph。
- 顶点和边使用紧凑、稳定的 64 位内部 ID；分配方式复用关系表 auto-increment 的持久高水位与 generation 经验。
- label 在 Graph Catalog 中分配稳定数值 ID，持久 key 不重复存储长 label 文本。
- 顶点支持多 label；边有一个主类型/label、固定 source/target，并允许平行边和自环。
- 可选外部业务 key 通过唯一属性索引映射到内部 ID；外部 key 不直接膨胀全部邻接 key。
- 属性第一阶段支持与关系标量重叠的 null、Int64、Float64、Boolean、String、DateTime、Blob 和 Json。GeoPoint/Vector 只有在复用现有类型与索引生命周期的方案通过独立设计后加入。

### 4.2 建议 key 布局

```text
N | VertexId                                      -> labels + properties + version
E | EdgeId                                        -> type + source + target + properties + version
O | VertexId | EdgeTypeId | NeighborId | EdgeId   -> compact edge projection
I | VertexId | EdgeTypeId | NeighborId | EdgeId   -> compact edge projection
L | Kind | LabelId | ElementId                    -> label membership
P | Kind | LabelId | PropertyId | Value | Id      -> property index entry
U | Kind | LabelId | PropertyId | Value           -> unique external-key mapping
S | StatisticKind | ...                           -> cardinality/degree/selectivity statistics
M | MetadataKind | ...                            -> id high-water/generation/maintenance state
```

邻接必须按 key 分条保存，不能把超级节点的全部边列表编码成单个 value。邻接 value 保存 traversal 所需的 `NeighborId`/edge projection，普通扩展不必先读取完整 Edge record。

### 4.3 每次写入必须保持的不变量

创建或更新边时，同一个 KV 原子 batch 至少覆盖：

1. Edge record。
2. source 的 outgoing adjacency。
3. target 的 incoming adjacency。
4. edge label membership。
5. 受影响的 edge property indexes。
6. 必需的统计增量或可恢复 dirty marker。

顶点属性更新必须原子替换 record 和对应属性索引。第一阶段顶点删除默认 `RESTRICT`：存在 incident edge 时拒绝删除。`DETACH DELETE` 只有在可恢复墓碑、分页清理、重启续作和读可见性合同完成后才加入，不能把百万边删除强塞进一个超限 WAL batch，也不能静默拆批伪装成原子事务。

### 4.4 事务与隔离

- 第一阶段提供单 graph 乐观事务：事务缓冲 mutation，提交时校验已读 element version，并用一次 `KvKeyspace.ApplyBatch` 发布。
- 单语句和显式事务超过 `MaxOverlayEntries`/`MaxWalBytes` 时在 WAL append 前整体拒绝；bulk importer 可以以幂等 checkpoint 分批，但不能宣称整个文件是一个事务。
- 第一阶段不承诺 graph 与 table/document/measurement 的跨模型原子提交；跨模型引用使用稳定 ID、outbox 或上层补偿。
- 第二阶段在稳定 KV snapshot lease 上提供 statement snapshot；最终阶段再根据冲突和长查询证据评估 snapshot isolation，不预先建设完整 MVCC。
- WAL append/fsync 结果不确定时沿用现有 commit-outcome-unknown 处理，调用方不得盲目重试非幂等写入。

## 5. 查询与 SQL 双入口

### 5.1 原生 Graph API

第一阶段先冻结不依赖查询语言的能力：

- vertex/edge CRUD、batch 和 transaction。
- `ExpandOut`、`ExpandIn`、`ExpandBoth`，支持 edge type、目标 label 和 property predicate。
- BFS/DFS、固定长度路径、受限可变长度路径、无权最短路径。
- 结果使用同步/异步 cursor；明确 path uniqueness、重复顶点/边、方向和最大深度。

这条 API 是存储和执行器的正确性入口，也是 Server/SDK、SQL 和测试的共同底座，不能成为与 SQL 平行的第二套实现。

### 5.2 原生图 SQL

第二阶段冻结 SonnetDB SQL 扩展，至少覆盖：

- graph、vertex label、edge label、property index 的 DDL 和 `SHOW`/`DESCRIBE`。
- 参数化 vertex/edge insert、upsert、property update 和受限 delete。
- `graph_nodes(...)` / `graph_edges(...)` 等关系化检查入口。
- 原生 graph 作为 `GRAPH_TABLE` 数据源，结果可继续参与现有 SQL JOIN、聚合、排序和分页。

具体 DML 拼写在对应设计 PR 中以 parser 冲突、参数绑定和标准演进证据冻结；本路线图不提前承诺一个难以兼容的临时语法。

### 5.3 SQL/PGQ 关系映射图

- 实现 ISO SQL/PGQ 核心子集：`CREATE PROPERTY GRAPH`、vertex/edge table mapping、labels、properties、source/destination key 和 `GRAPH_TABLE(... MATCH ... COLUMNS ...)`。
- 映射定义是只读 catalog，不复制关系主数据；边 key 必须有可用主键/索引/外键或显式 key 声明。
- Binder 把 mapping 转换为 Graph Logical Plan，`RelationalGraphAccessor` 使用 TableStore 的主键/二级索引定位顶点和边。
- 没有合适索引时允许受预算约束的 scan fallback，但 `EXPLAIN` 必须显示；不能标记为 native adjacency。
- 后续若有真实高频需求，可独立评估显式刷新的 materialized graph；不得在 SQL/PGQ 第一版中暗中维护第二份图数据。

### 5.4 图执行算子

按实际需求增量实现并复用：

```text
GraphNodeScan / GraphNodeIndexSeek
GraphExpand / GraphExpandInto
GraphFilter / GraphProject
GraphVarLengthExpand
GraphShortestPath
GraphDistinct / GraphLimit
```

执行模型采用 pull/cursor，禁止先生成全部路径再过滤。Planner 从 label/property 选择率最高的 anchor 开始，按方向和度分布选择扩展顺序；没有统计时使用保守规则并在 `EXPLAIN` 标明估算来源。

## 6. 分阶段交付

### Phase 0：基础改造与设计冻结

此阶段没有对外 Graph 产品能力，目标是消除会导致后续返工的存储、读取和合同缺口。

| 编号 | 交付 | 验收门禁 |
|---|---|---|
| #341 | ADR、术语、目标 workload、原生/映射图边界、golden journey 和 capability gap catalog。 | 至少覆盖社交多跳、设备拓扑、知识证据链、关系表 SQL/PGQ 映射；每条给出数据规模、更新模式、查询和不做项。 |
| #342 | 抽取通用 sortable scalar codec，冻结 GraphElementId、LabelId、GraphPropertyValue 和版本化 record/key 格式。 | Table 编码回归不变；graph codec round-trip、排序、损坏拒绝、旧版拒绝/迁移策略齐全。 |
| #343 | KV snapshot lease、前向 range cursor、页缓冲所有权和取消合同。 | 遍历不在 keyspace 锁内执行消费逻辑；分页不从头重扫；内存随 page/frontier 上限而不是 keyspace 总量增长。 |
| #344 | `GraphCatalog`、`GraphManager`、目录、命名/依赖和 `Tsdb.Graphs` 生命周期。 | create/open/drop/reopen、同名对象阻断、目录损坏和版本不兼容测试通过；仍不暴露虚假查询能力。 |
| #345 | 单 graph transaction、element version、写预算、commit-unknown 和 vertex delete `RESTRICT` 合同。 | 并发冲突、取消、超限、重复请求和 WAL 故障不会产生半条边或孤立邻接。 |
| #346 | Graph backup manifest、checkpoint、verify/restore、invariant checker 和 CrashTests 骨架。 | 任意注入点重启后要么看到提交前、要么看到提交后状态；校验器能发现故意构造的 orphan/mismatch。 |

Phase 0 完成标准：能够证明底层结构可安全提交和恢复，但仍不得宣称“图数据库已可用”。

### Phase 1：可用的原生图数据库第一阶段

| 编号 | 交付 | 验收门禁 |
|---|---|---|
| #347 | `GraphStore` vertex/edge CRUD、多 label、typed property、双向邻接、label/property/unique index。 | 所有 mutation 都通过单 keyspace 原子 batch；重启、索引重建和 invariant check 对拍。 |
| #348 | 原生 Graph API 与 streaming cursor：seek、ExpandOut/In/Both、过滤和批量读取。 | 单跳访问不调用关系 JOIN；I/O/CPU 随命中度数增长；嵌入式遍历不全量复制图。 |
| #349 | BFS/DFS、固定/受限可变长度路径、无权 shortest path、path uniqueness 和预算。 | cycle、self-loop、parallel edge、取消、超时、深度/frontier/path 上限测试完整。 |
| #350 | label/property cardinality、degree histogram、index selectivity、统计刷新和基础 Graph EXPLAIN。 | 选择性 anchor 可验证；统计缺失/陈旧有稳定 fallback；统计是可重建派生数据。 |
| #351 | Server/typed .NET SDK、Frame/HTTP 流式读取、幂等 bulk import，以及 CSV/JSON/Graphify `graph.json` importer。 | 嵌入式/远程同语义；导入可 checkpoint/resume；Graphify 只作为输入，不进入 Core。 |
| #352 | Phase 1 correctness/performance gate：CrashTests、BenchmarkDotNet、Neo4j 对照和固定硬件报告。 | 100k/1m vertex、1m/10m edge 分档报告；1~6 hop、supernode、混合读写、冷/热重启均有结果，不设无证据营销阈值。 |

Phase 1 对外名称只能是 **Native Graph Preview**。它已经是真正的邻接图存储，但 SQL 图模式和完整产品面尚未完成。

### Phase 2：SQL 可组合与实用查询阶段

| 编号 | 交付 | 验收门禁 |
|---|---|---|
| #353 | Graph Logical Plan 与共享 pull operators；原生 API 改为消费相同计划。 | API 与计划执行结果对拍；不存在第二套 BFS/Expand 实现。 |
| #354 | 原生 graph SQL DDL/DML、`SHOW/DESCRIBE`、`graph_nodes/graph_edges` 和参数绑定。 | SQL mutation 与 Graph API 使用同一 GraphStore/transaction；权限、审计、错误位置和取消一致。 |
| #355 | SQL/PGQ `CREATE PROPERTY GRAPH` 关系映射 catalog 与 `RelationalGraphAccessor`。 | 映射不复制数据；主外键/显式 key 校验；索引 seek/scan fallback 在 EXPLAIN 可见。 |
| #356 | `GRAPH_TABLE MATCH COLUMNS` 固定模式、方向、label、property predicate 和变量投影。 | PostgreSQL SQL/PGQ 参考用例对拍；原生 graph 使用 adjacency，映射 graph 使用关系访问器。 |
| #357 | SQL 可变长度路径、path mode/uniqueness、shortest path、最大深度与结果预算。 | 路径爆炸 fail bounded；循环语义确定；结果通过 ADO.NET/远程流式读取。 |
| #358 | cost planner、join/expand 顺序、bidirectional BFS 准入、`EXPLAIN ANALYZE` 实际 rows/expansions/frontier/fallback。 | 计划估算和实际指标可解释；优化前后结果完全一致；无基准收益不引入复杂算法。 |
| #359 | SQL + Graph + Table/Document/Vector 组合、权限、备份、Studio 查询页和 Parity Graph capability。 | 同一 SQL 可组合图行集与现有模型；Neo4j 验原生语义、PostgreSQL 验 SQL/PGQ 语义；UI 不绕过 Server。 |

Phase 2 完成后可称为 **SonnetDB Graph Beta**：具备原生存储、事务、遍历、SQL/PGQ 和跨模型组合，但长期并发、维护和容量证据仍未收口。

### Phase 3：生产级单机图数据库

| 编号 | 交付 | 验收门禁 |
|---|---|---|
| #360 | statement snapshot、长遍历读一致性、并发写冲突矩阵；按证据决定是否扩展 snapshot isolation。 | 遍历期间并发 mutation 的可见性确定；无死锁/锁饥饿；不做无证据 MVCC 重构。 |
| #361 | supernode 治理、邻接分页/压缩、索引 repair、统计维护、checkpoint/compaction 热点治理。 | 高度数节点内存有界；维护可暂停/续作；故障后不丢唯一修复来源。 |
| #362 | weighted shortest path（Dijkstra）、可选 A*、bidirectional search 和批量图算法执行框架。 | 只有真实 journey 和 benchmark 证明收益的算法进入 Core；权重负值、溢出、取消合同明确。 |
| #363 | 首批离线算法：connected components、PageRank、degree/community 基础结果，输出到 graph/table 而非常驻第二份状态。 | 算法可 checkpoint/cancel，结果版本可追溯；大图内存预算与 spill 策略明确。 |
| #364 | 可选 GQL 风格直接查询入口，只复用 Graph AST/Plan，不承诺完整 Cypher。 | 与等价 SQL/PGQ 计划和结果对拍；无新增执行器；语法能力矩阵公开。 |
| #365 | 知识图谱/GraphRAG 上层合同：provenance、confidence、source/chunk、valid time、alias/claim、community/summary 引用。 | Core 只存通用属性图；抽取/消歧/LLM job 在 Server/SDK；Document/Object/Vector 仍是权威内容存储。 |
| #366 | 运维产品面：schema/index/degree/slow traversal、可视化、受限编辑、import/export、repair/rebuild 和权限审计。 | Web/Studio/CLI/SDK 能力矩阵一致；危险 mutation 使用现有 staged approval。 |
| #367 | 发布门禁：LDBC SNB 子集、Graphalytics 子集、7 天 mixed workload、kill/reopen、backup/restore、Native AOT 和固定硬件容量报告。 | 报告可复现且包含 commit/硬件/数据规模/P50/P95/P99/内存/WAL/恢复/正确性；门禁通过后才更改九模型定位。 |

Phase 3 完成后，SonnetDB 才能对外称为**生产可用的单机原生属性图数据库**。这不包含分布式图数据库、完整 Cypher/GQL 或 RDF 推理能力。

## 7. 测试与验收矩阵

### 7.1 正确性

- CRUD、multi-label、parallel edge、self-loop、incoming/outgoing、属性类型和索引一致性。
- 事务提交前/中/后故障、torn WAL、checkpoint 交错、备份恢复和索引重建。
- path walk/trail/simple semantics、方向、cycle、零长度路径、重复边和确定性 tie-break。
- SQL 参数、NULL/类型转换、权限、取消、CommandTimeout、远程断线和结果续读。
- invariant checker 至少验证 edge/双邻接一一对应、端点存在、label/property index 无 stale/orphan、ID high-water 不回退。

### 7.2 性能与容量

- 数据形态：均匀度数、幂律分布、supernode、深链、宽 frontier、稠密子图和频繁属性更新。
- 规模档：开发 quick、100k/1m、固定硬件 1m vertex/10m edge；更大档只在前档稳定后加入。
- 操作：批量导入、point lookup、1/2/3/6 hop、shortest path、属性 anchor、混合读写、checkpoint、reopen 和 repair。
- 记录：吞吐、P50/P95/P99、working set、托管分配、WAL bytes、read amplification、expanded edges、frontier peak 和恢复时间。
- 原生遍历必须通过 trace/plan 证明没有 Table JOIN；关系映射图必须如实报告 index seek 或 scan。

### 7.3 竞品与标准对照

- Neo4j：属性图 CRUD、方向、多边、自环、路径和原生遍历结果基准。
- PostgreSQL SQL/PGQ：property graph mapping、`GRAPH_TABLE`、label/property/方向和 SQL 组合语义。
- Graphify：只验证 importer 对 `graph.json` 的节点、边、来源和置信度保真，不做数据库性能对比。
- RDF/SPARQL/本体推理不在 M40；若未来有真实需求，独立建模，不能把 RDF triple 语义硬套到属性图。

## 8. 与现有路线图的关系

- M20 Parity：Phase 2 增加 `Capability.Graph`，但不改写历史八模型完成结论。
- M27 Agent：只通过授权 Graph API/SQL 访问；写入继续使用现有 staged approval，不给模型旁路。
- M29 Workbench：Phase 1 先只读浏览/查询，mutation 等 Core/Server 合同稳定后开放。
- M35 多模态：内容、chunk、embedding 和对象生命周期继续由 M35 建设；M40 Phase 3 只增加图关系和 provenance 组合。
- M36 八模型易用性：保留原范围；Graph 的 golden journey、SDK 和诊断由 M40 自己验收，避免重开 #310~#326。
- M37 View：复用依赖/catalog 模式；SQL/PGQ mapping graph 是图 catalog，不冒充普通 view。
- M38/M39 过程触发器：Phase 1 不支持 graph trigger；只有事务写放大和恢复证据完成后另行准入。
- MM9 Backup：扩展现有 manifest 和 CLI，不新增 Graph 专用备份格式。

## 9. 实施纪律

- 每个编号一个可审查 PR；设计、格式、Core、Server/SDK、UI 和证据不能混成一个巨型 PR。
- 新格式必须带 magic/version/CRC、旧版读取或明确拒绝、迁移策略和 CHANGELOG。
- 所有 public API 使用中文 XML 注释；关键方法和不变量使用简洁中文注释；保持 Safe-only、Native AOT 和 Core 无新增第三方运行时依赖。
- 优化项必须同时给出基线、替代方案、正确性对拍和收益；没有证据的优化留在观察项。
- 每阶段先完成 Core/测试，再开放 Server/SDK，最后开放 UI 和产品文案。
- Graphify 等外部抽取器通过 importer/SDK 接入，不反向决定 Core 存储格式。
