# 原生属性图数据库路线图

> 本文定义 SonnetDB Milestone 40 的工程路线。Phase 0（#341～#346）公共地基 ✅ 已完成；Phase 1 的 Native Graph Core、Server/typed SDK、Frame/HTTP streaming、bounded import 和本地 smoke evidence ✅ 已接线，但固定硬件、Neo4j 对照与完整 correctness/performance gate 📋 尚未通过，因此仍不得宣称 Preview 发布。

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

- KV 稳定读快照和前向范围游标：游标生命周期内保持一致视图，按页解码，不在 keyspace 写锁内执行用户回调；每页同时受条目数和 key/value payload 字节预算约束，单条超限稳定失败。
- 有界 key/value 所有权：明确 borrowed memory、页缓冲、单条预取和复制边界；公共 API 不暴露失效 Span，游标驻留不随 keyspace 总量增长。
- 通用可排序标量 codec：支持 null、Int64、Float64、Boolean、String、DateTime、Blob/Json 的类型标签和确定性排序；复用现有表索引规则。
- `GraphPropertyValue` 可以作为图公共 API 的必要类型化包装，但 Phase 0 不迁移或替换现有 `FieldValue`、`TableColumnType` 和公开 Table API；只抽取双方真正共享的内部编码原语和测试向量。
- 流式执行结果合同：复用 ADO.NET `DbDataReader`、远程 Frame 流和取消链路，不再以 `SelectExecutionResult` 承载大路径集合。
- 执行预算：统一 timeout、cancellation、最大行数、最大返回字节，并增加最大深度、最大 frontier、最大路径数和单顶点 expansion 配额。

这些改造必须由实际 benchmark 和分配证据约束。Phase 0 不引入分片、MVCC page tree 或复杂缓存。

### 2.2 Couplet 上层工作负载与缺口回收门禁

[Couplet](https://github.com/IoTSharp/Couplet) 是面向 Codex、Claude Code 等编码 Agent 的独立本地代码知识产品。两个仓库保持同级独立，不互相作为 Git submodule；依赖方向固定为 `Couplet -> SonnetDB.Core`，正式构建固定已发布 package version，本地联调可用不提交的 composite solution 或 opt-in `ProjectReference`。Couplet 负责工作区/Git、解析器、代码领域 schema、增量协调、本地 embedding、上下文组装、首版只读 typed MCP 和 Agent 产品面；SonnetDB 负责通用多模型存储、查询、事务、恢复、资源治理与性能。Couplet 不读取内部 key layout，不复制 Core，也不能建立替代数据引擎。

代码知识、依赖分析和 Agent 上下文检索是原生图与多模型组合的正式 golden journey。该边界不允许上层绕过 Core：

- 一旦 golden journey、执行计划、分配/锁/I/O 计数或固定硬件报告稳定复现通用缺口，就必须登记到 capability gap catalog，标明复现语料、规模、复杂度、责任里程碑和关闭证据，并阻塞依赖该能力的预览、Beta 或正式发布。
- GraphStore、邻接、事务、路径算子、统计、恢复和图查询计划缺口归 M40；`KvKeyspace` 的 snapshot lease、range cursor、atomic batch、checkpoint/compaction、锁范围和分配缺口归 M40 公共地基或对应 Core 性能里程碑；共享关系算子缺口归 M41；Document、FullText、Vector、filtered ANN、混合召回和派生索引生命周期缺口分别回收到 M32、M35、M36 或其后续公共里程碑。不得在上层复制一套存储、索引、遍历或查询执行器。
- 修复优先级固定为：正确性、原子性与恢复不变量；随后是有界内存/锁/取消以及消除声明路径中的非预期全扫、全量物化和重复遍历；再处理固定硬件容量、P95/P99 延迟和资源放大；最后才开放新 API、算法、UI 和产品文案。
- 对尚未由证据触发的优化继续遵守“不预建复杂机制”；一旦受支持的工作负载触发缺口，就不再以“未来优化”延期，也不得以关系边表、应用层 BFS/DFS、第二套图数据库、隐藏全量 vector/document scan 或提高默认资源上限作为发布兜底。
- 能力暂缺时，上层只能 fail fast、降级产品能力等级或保持未发布；所有允许的 scan fallback 必须有显式预算、可取消、在 `EXPLAIN`/诊断中可见，并在目标规模报告中证明满足已冻结的 SLO。

Couplet 仓库/路线基线已经建立，但 C0-C4 产品实现仍按证据推进；详细产品待办以 Couplet 的 [ROADMAP.md](https://github.com/IoTSharp/Couplet/blob/main/ROADMAP.md) 为准：

| Couplet 阶段 | 产品交付 | 联调/开发开始条件 | 联合退出/发布门禁 | 状态边界 |
|---|---|---|---|---|
| 仓库/路线基线 | README/ADR、MCP 合同语义、golden journeys、质量/性能门禁和 gap catalog | 无 | 输入 #341 workload/SLO | ✅ 仅规划基线完成，不代表任何运行能力 |
| C0 基础与合同 | 可运行骨架、代码 schema、capability/version handshake、fixture/eval runner | 基线已建立 | 与 #341 同步冻结 | 📋 不宣称图检索可用 |
| C1 增量代码索引 | Git/worktree/revision、语言适配、Document/FullText 和基础 MCP | 与 #342~#346 public API 并行 | #343/#346 所需合同 + Couplet revision/crash/capacity gate | 📋 不用 Document/KV 边表旁路图 |
| C2 原生图代码智能 | 定义/引用/调用/继承/依赖路径/影响与测试选择 | #347~#351 目标 public API 可联调 | #352 + Couplet C2 correctness/performance 同时 PASS | 📋 才可发布 Native Graph Preview |
| C3 混合检索与 context pack | FullText + 本地 embedding/Vector + Native Graph、证据和 Agent eval | #353~#358 与相关 M35/M36 API 可联调 | #359 + Couplet C3 gate 同时 PASS | 📋 才可发布 Beta，不得产品侧 merge/遍历 |
| C4 生产与 Agent 体验 | 7 天长稳、恢复、安全、容量、分发和双客户端验收 | 与 #360~#366 并行取证 | #367 + Couplet C4 门禁同时 PASS | 📋 才可发布 Production/1.0 |

Couplet 可以提前开发不依赖缺失 Core 能力的 Git、解析、协议和评测代码；任何阶段不得以产品侧替代实现绕过未通过的 SonnetDB 门禁。

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
O | VertexId | EdgeTypeId | NeighborId | EdgeId   -> empty/reserved value (V1)
I | VertexId | EdgeTypeId | NeighborId | EdgeId   -> empty/reserved value (V1)
L | Kind | LabelId | ElementId                    -> empty/reserved value (V1)
P | Kind | LabelId | PropertyId | Value | Id      -> empty/reserved value (V1)
U | Kind | LabelId | PropertyId | Value           -> versioned owner element mapping
S | StatisticKind | ...                           -> cardinality/degree/selectivity statistics
M | MetadataKind | ...                            -> id high-water/generation/maintenance state
```

邻接必须按 key 分条保存，不能把超级节点的全部边列表编码成单个 value。V1 把 traversal 所需的 `EdgeTypeId`、`NeighborId` 和 `EdgeId` 紧凑投影在 O/I key 中，value 固定为空并保留给显式格式升级；普通扩展可直接解码 key，不必先读取完整 Edge record。L/P 同样把完整派生投影放在 key 中并使用空 value。U key 的 value 例外：它使用 `GraphRecordKind.UniquePropertyOwner = 4` 的带版本和 CRC 固定 owner envelope 保存 element kind 与内部 element ID，从而提供确定性的 external-key point lookup，并让 invariant checker 校验 owner record 与对应 P projection。owner payload 固定为 `int32 little-endian version = 1`、`byte element kind`、3 个零保留字节和 `int64 little-endian owner ID > 0`；任何 kind/version/reserved/长度/CRC 不匹配都必须稳定拒绝。

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

### Phase 0：基础改造与设计冻结（✅ 已完成）

此阶段没有对外 Graph 产品能力，目标是消除会导致后续返工的存储、读取和合同缺口。

| 编号 | 交付 | 验收门禁 |
|---|---|---|
| ✅ #341 | ADR、术语、目标 workload、原生/映射图边界、golden journey 和 capability gap catalog。 | 至少覆盖社交多跳、设备拓扑、知识证据链、Couplet 代码符号/引用/调用/测试影响分析、关系表 SQL/PGQ 映射；每条给出数据规模、更新模式、查询、冻结 SLO 和不做项。 |
| ✅ #342 | 抽取通用 sortable scalar codec，冻结 GraphElementId、LabelId、GraphPropertyValue 和版本化 record/key 格式。 | Table 编码回归不变；graph codec round-trip、排序、损坏拒绝、旧版拒绝/迁移策略齐全。 |
| ✅ #343 | KV snapshot lease、前向 range cursor、页缓冲所有权和取消合同。 | 遍历不在 keyspace 锁内执行消费逻辑；分页不从头重扫；页同时受条目数和 payload 字节数约束，内存随 page/frontier 上限而不是 keyspace 总量增长。 |
| ✅ #344 | `GraphCatalog`、`GraphManager`、目录、命名/依赖和 `Tsdb.Graphs` 生命周期。 | create/open/drop/reopen、同名对象阻断、目录损坏和版本不兼容测试通过；仍不暴露虚假查询能力。 |
| ✅ #345 | 单 graph transaction、element version、写预算、commit-unknown 和 vertex delete `RESTRICT` 合同。 | 并发冲突、取消、超限、重复请求和 WAL 故障不会产生半条边或孤立邻接。 |
| ✅ #346 | Graph backup manifest、checkpoint、verify/restore、invariant checker 和 CrashTests 骨架。 | 任意注入点重启后要么看到提交前、要么看到提交后状态；校验器能发现故意构造的 orphan/mismatch。 |

✅ Phase 0 已完成：frozen V1 vectors、Table V1 兼容、snapshot/cursor 有界读取、catalog/lifecycle、条件原子事务、manifest v1/v2、backup/restore/invariant 与跨进程 CrashTests 均有自动回归。✅ Phase 1 的 #347～#351 功能实现和本机 correctness smoke 已闭环；📋 #352 的固定硬件、Neo4j、完整恢复/容量 artifact 仍后置，因此不把本机结果提升为发布 gate。

### Phase 1：可用的原生图数据库第一阶段（✅ 功能切片完成；📋 #352 发布门禁待完成）

| 编号 | 交付 | 验收门禁 |
|---|---|---|
| ✅ #347 | `GraphStore` vertex/edge CRUD、多 label、typed property、双向邻接、label/property/unique index。 | 所有 mutation 都通过单 keyspace 原子 batch；重启、索引重建和 invariant check 对拍。当前已有 `RebuildIndexes` 有界修复和 Crash/backup 回归，唯一声明缺失边界仍需显式输入。 |
| ✅ #348 | 原生 Graph API 与 streaming cursor：seek、ExpandOut/In/Both、过滤和批量读取。 | 单跳访问不调用关系 JOIN；I/O/CPU 随命中度数增长；嵌入式遍历不全量复制图。当前 Core、REST、NDJSON、Frame 和 typed SDK 已接线，固定规模复杂度证据待跑。 |
| ✅ #349 | BFS/DFS、固定/受限可变长度路径、无权 shortest path、path uniqueness 和预算。 | cycle、self-loop、parallel edge、取消、超时、深度/frontier/path 上限测试完整。当前有分页/cancel/cycle/self-loop/parallel-edge 回归，完整超时和目标硬件矩阵待跑。 |
| ✅ #350 | label/property cardinality、degree histogram、index selectivity、统计刷新和基础 Graph EXPLAIN。 | 选择性 anchor 可验证；统计缺失/陈旧有稳定 fallback；统计是可重建派生数据。当前含 fingerprint cardinality、stale/missing explain 回归，容量校准待跑。 |
| ✅ #351 | Server/typed .NET SDK、Frame/HTTP 流式读取、幂等 bulk import，以及 CSV/JSON/Graphify `graph.json` importer。 | 嵌入式/远程同语义；导入可 checkpoint/resume；Graphify 只作为输入，不进入 Core。当前支持 native numeric profile 和 normalized string `nodes/relationships` profile；通过确定性 batch request ID 重放恢复，未引入 Graphify runtime。 |
| 📋 #352 | Phase 1 correctness/performance gate：CrashTests、BenchmarkDotNet、Neo4j 对照和固定硬件报告。 | 正确性/恢复与性能/容量是两个独立 gate：前者要求语义对拍零 mismatch、零 orphan/index drift，crash/replay/checkpoint/backup/repair 全 PASS；后者在 100k/1m vertex、1m/10m edge 下对 1~6 hop、supernode、代码知识影响分析、混合读写和冷/热重启达到 #341 预先冻结的复杂度、内存及 P95/P99 SLO。任一 gate 未达即阻断 Phase 1；不得遗留不可解释的全扫/全量物化，也不得事后按实现结果改低阈值。 |

✅ Phase 1 功能边界已闭环；📋 在 #352 外部证据通过前，对外名称仍只能是 **Native Graph Preview**。它已经是真正的邻接图存储，SQL 图模式和完整产品面由 Phase 2 继续建设。

### Phase 1 当前实现边界（✅ 功能切片完成；📋 发布证据待完成，2026-08-11）

- ✅ `GraphStore.RebuildIndexes` 在提交门内按稳定快照和 KV index-rebuild budget 分页补建/删除 adjacency、label/property、unique 派生键；冻结 V1 元素记录不保存 unique 声明，若声明的全部 key 已丢失，调用方必须通过 `GraphIndexRebuildOptions.UniqueIndexes` 重新提供声明。
- ✅ `SndbGraphImporter` 先把单个 JSON element 写入临时 NDJSON spool，再按确定性 request ID 以有界 batch 重放；CSV 同样按 batch 流式提交。`nodes/relationships` normalized profile 接受字符串 ID、label/type、对象属性和 provenance/confidence 元数据，映射函数为 `GetStableElementId`、`GetStableLabelId`、`GetStablePropertyId`。
- ✅ `tests/SonnetDB.Benchmarks --m40-graph-evidence --quick` 会真正 reopen/replay 并执行 path/invariant/index-repair smoke；本机报告输出 `correctness=PASS`、`correctness_recovery=LOCAL_PASS`；📋 `performance_capacity=NOT_RUN`、`release_decision=NOT_RUN`，不能代替 #352 的固定硬件、Neo4j 或完整 crash/replay/checkpoint/backup artifact。

### Phase 2：SQL 可组合与实用查询阶段（✅ 功能切片完成；📋 外部语义、硬件和 Couplet 发布证据待完成）

| 编号 | 交付 | 验收门禁 |
|---|---|---|
| ✅ #353 | Graph Logical Plan 与共享 pull operators；原生 API 改为消费相同计划。 | API 与计划执行结果对拍；不存在第二套 BFS/Expand 实现。 |
| ✅ #354 | 原生 graph SQL DDL/DML、`SHOW/DESCRIBE`、`graph_nodes/graph_edges` 和参数绑定。 | SQL mutation 与 Graph API 使用同一 GraphStore/transaction；权限、审计、错误位置和取消一致。 |
| ✅ #355 | SQL/PGQ `CREATE PROPERTY GRAPH` 关系映射 catalog 与 `RelationalGraphAccessor`。 | 映射不复制数据；主外键/显式 key 校验；索引 seek/scan fallback 在 EXPLAIN 可见。 |
| ✅ #356 | `GRAPH_TABLE MATCH COLUMNS` 固定模式、方向、label、property predicate 和变量投影。 | PostgreSQL SQL/PGQ 参考用例对拍；原生 graph 使用 adjacency，映射 graph 使用关系访问器。 |
| ✅ #357 | SQL 可变长度路径、path mode/uniqueness、shortest path、最大深度与结果预算。 | 路径爆炸 fail bounded；循环语义确定；结果通过 ADO.NET/远程流式读取。 |
| ✅ #358 | cost planner、join/expand 顺序、bidirectional BFS 准入、`EXPLAIN ANALYZE` 实际 rows/expansions/frontier/fallback。 | 计划估算和实际指标可解释；优化前后结果完全一致；无基准收益不引入复杂算法。 |
| ✅ #359 | SQL + Graph + Table/Document/Vector/FullText 组合、复用 M35/M36 的 Hybrid Search 候选合同，以及权限、备份、Studio 查询页和 Parity Graph capability。 | 同一 SQL/typed plan 可组合图行集与现有模型；实际 access path、候选规模和 fallback 可见，声明 journey 不在产品侧 merge、遍历或隐藏全扫；Neo4j 验原生语义、PostgreSQL 验 SQL/PGQ 语义，UI 不绕过 Server。 |

✅ Phase 2 #353~#359 功能已完成：`GraphLogicalPlan`、共享 pull cursor 和原生 API 计划执行已接入；原生 SQL 已提供 `CREATE/DROP/SHOW/DESCRIBE GRAPH`、参数化 `graph_nodes/graph_edges`、有界投影/过滤/排序/分页，以及受限的 `INSERT INTO GRAPH ... VERTEX|EDGE`；关系映射已提供持久化 `CREATE/DROP/SHOW/DESCRIBE PROPERTY GRAPH` catalog、`RelationalGraphAccessor`，`GRAPH_TABLE MATCH COLUMNS` 已覆盖固定一跳、受限可变长度和 shortest path，并具有端点成本规划、实际执行指标及共享 SQL 跨模型组合。📋 固定硬件、PostgreSQL/Neo4j 外部语义对拍和 Couplet C3 联合发布证据仍为 `NOT_RUN`，因此这里只关闭功能切片，不宣称 Beta 发布 gate 已通过。

#354 当前 DML 边界是：元素 ID 仅接受正整数；顶点 labels 接受单个正整数或逗号分隔的正整数文本；edge label 使用 Int32 范围内的正整数。此切片尚不承诺属性列写入，typed property mutation 继续通过 Graph API；property SQL、upsert/update/delete 在冻结完整 DML 合同后接入。

#355 当前映射边界是：`CREATE PROPERTY GRAPH` 按 SQL/PGQ 形状声明 `VERTEX TABLES`、`EDGE TABLES`、唯一 key、source/destination reference、label 和 property columns；独立 `SDBPGQ01` catalog 只保存 mapping 并随数据库备份文件复制，不生成 vertex/edge 副本。创建时校验表、列、主键或完整唯一索引、endpoint key 数量/类型；被映射表的破坏性 schema 变更受依赖守卫阻断。`RelationalGraphAccessor` 顶点 point lookup 与边 endpoint expand 显式报告 `relation_primary_key_seek`、`relation_index_seek` 或 `relation_scan_fallback`，fallback 默认受行数、时间与取消预算约束；`DESCRIBE`/`EXPLAIN DESCRIBE PROPERTY GRAPH` 展示实际路径。

#356 当前查询边界是：typed `GRAPH_TABLE(... MATCH (a IS label)-[e IS label]->(b IS label) [WHERE ...] COLUMNS (...))` 支持固定一跳的出、入和无向模式，变量属性谓词与参数绑定，显式变量投影，以及外层 WHERE/投影/DISTINCT/ORDER BY/分页。原生 graph 使用 label anchor 与 adjacency plan，映射 graph 使用关系主键/索引 seek 或 scan fallback；同 label 多 edge table 以 mapping branch union，无向自环每个 edge/anchor 只产生一次。整条关系映射查询共享 10,000 anchor、10,000 fallback scan 行、50 ms fallback 与 100,000 匹配行预算；Graph DDL/DML 不伪装进入轻事务，view/materialized view/procedure 会记录 graph 依赖。

#357 当前路径边界是：`MATCH p = WALK|TRAIL|SIMPLE|ACYCLIC (a IS label)-[e IS label]->{min,max}(b IS label)` 支持 1~64 hop 的出、入和无向路径；`ANY SHORTEST` 按 BFS 为每个终点选择一条满足深度下界的最短合法路径，普通变量路径按 DFS 枚举。`WALK` 允许重复元素，`TRAIL` 保证路径内 edge 唯一，`SIMPLE/ACYCLIC` 保证 vertex 唯一；路径变量公开 `length`、`vertex_ids`、`edge_ids`、`start_id` 和 `end_id`，edge group 不伪装成单 edge 标量。原生 graph 复用 `GraphPathPlan`/`GraphPlanExecutor`，映射 graph 复用 `RelationalGraphAccessor`，共享 10,000 anchor/frontier、100,000 path/result 和关系 fallback 预算；typed AST 的深度、enum 和变量冲突会在访问 graph 前拒绝。Core cycle/min-depth 回归、直接 Frame 流以及远程 ADO.NET REST 读取已通过；外部 PostgreSQL SQL/PGQ 对拍、固定硬件和容量报告仍为 `NOT_RUN`。

#358 当前规划边界是：`graph_cost_v1` 比较左右端点的完整 key predicate、关系表当前 `RowCount`、edge endpoint index/fallback 和路径最大深度，选择成本更低的 anchor 与 expand 方向；右端执行时顶点变量绑定保持不变，路径变量会反转回原 SQL 方向。原生图没有已刷新统计时使用公开预算上界并优先精确 ID，不在 `EXPLAIN` 中触发隐藏统计全扫。`EXPLAIN ANALYZE` 第一版只开放给 `GRAPH_TABLE`，返回估算计划以及实际 output/matched rows、anchor rows、expansions、generated paths、peak frontier、fallback rows/ms 和 elapsed ms；普通 `EXPLAIN` 不执行查询。native `ANY SHORTEST` 同时绑定两端时会进入 bidirectional admission 检查，但在没有可复现基准收益前明确保持 `bidirectional_bfs_admitted=false`、reason=`benchmark_evidence_missing`，不引入第二套 BFS。

#359 当前组合边界是：图查询先作为有界派生行集进入现有 `RelationalSelectExecutor`，可在同一 SQL 中与关系表、Document 投影，以及 FullText + Vector `hybrid_search(...)` 候选子查询做 hash join；不新增第二套 JOIN 或应用层 merge。组合 `EXPLAIN` 以 `cross_model_select` 展示每个 source/join 的 graph adjacency/关系索引/document/hybrid access path、候选上限和 fallback reason；Hybrid Search 明示全文候选上限及 document vector scan fallback。property-graph catalog 进入备份 manifest 的完整 mapping 摘要、独立文件类型与 restore 后逐字段复核；Graph SQL metadata/read 与 DDL/DML 分别沿数据库 read/write 权限，Studio Quick SQL 仍走 `/v1/db/{db}/sql` 和共享结果面板。Parity 增加 Graph/SQL-PGQ/native traversal/cross-model capability 与本地 correctness scenario，PostgreSQL/Neo4j outcome 明确保持 `not_run`。当前不支持把 `JOIN/GROUP BY` 直接写进 `GRAPH_TABLE` 外层执行器内部，调用方必须使用标准派生表组合；property alias 和多 pattern 仍不在此 Beta 子集。

✅ Phase 2 功能切片完成后具备原生存储、事务、遍历、SQL/PGQ 和跨模型组合；📋 长期并发、维护和容量证据仍未收口，外部 Beta 发布 gate 尚未通过。

### Phase 3：生产级单机图数据库（✅ #360~#365；📋 #366~#367）

| 编号 | 交付 | 验收门禁 |
|---|---|---|
| ✅ #360 | statement snapshot、长遍历读一致性、并发写冲突矩阵；按证据决定是否扩展 snapshot isolation。 | 遍历期间并发 mutation 的可见性确定；无死锁/锁饥饿；不做无证据 MVCC 重构。 |
| ✅ #361 | supernode 治理、邻接分页/压缩、索引 repair、统计维护、checkpoint/compaction 热点治理。 | 高度数节点内存有界；维护可暂停/续作；故障后不丢唯一修复来源。 |
| ✅ #362 | weighted shortest path（Dijkstra）、可选 A*、bidirectional search 和批量图算法执行框架。 | 只有真实 journey 和 benchmark 证明收益的算法进入 Core；权重负值、溢出、取消合同明确。 |
| ✅ #363 | 首批离线算法：connected components、PageRank、degree/community 基础结果，输出到 graph/table 而非常驻第二份状态。 | 算法可 checkpoint/cancel，结果版本可追溯；大图内存预算与 spill 策略明确。 |
| ✅ #364 | 可选 GQL 风格直接查询入口，只复用 Graph AST/Plan，不承诺完整 Cypher。 | 与等价 SQL/PGQ 计划和结果对拍；无新增执行器；语法能力矩阵公开。 |
| ✅ #365 | 知识图谱/GraphRAG 上层合同：provenance、confidence、source/chunk、valid time、alias/claim、community/summary 引用。 | Core 只存通用属性图；抽取/消歧/LLM job 在 Server/SDK；Document/Object/Vector 仍是权威内容存储。 |
| 📋 #366 | 运维产品面：schema/index/degree/slow traversal、可视化、受限编辑、import/export、repair/rebuild 和权限审计。 | Web/Studio/CLI/SDK 能力矩阵一致；危险 mutation 使用现有 staged approval。 |
| 📋 #367 | 发布门禁：LDBC SNB 子集、Graphalytics 子集、代码知识/Agent 组合语料、7 天 mixed workload、kill/reopen、backup/restore、Native AOT 和固定硬件容量报告。 | 报告可复现且包含 commit/硬件/数据规模/P50/P95/P99/内存/WAL/恢复/正确性、实际 access path/fallback 和 gap catalog 关闭状态；正确性/恢复 gate 必须全 PASS，性能/容量 gate 必须达到 #341 冻结的生产 SLO，任一失败或存在生产阻塞缺口都不得更改九模型定位。 |

✅ #360 已完成：`GraphStore.BeginRead` 明确冻结单一 KV sequence，同一 `GraphReadSession` 上的点读、在并发提交前后创建的游标和分页长遍历都复用该 snapshot；cursor lease 只保留不可变内存视图和 disk generation lease，不持有 Graph commit gate 或 store lock。`EXPLAIN ANALYZE GRAPH_TABLE` 对原生图新增 `read_consistency=statement_snapshot`、`actual_read_consistency` 和 `actual_snapshot_sequence`；关系映射如实返回 `relation_accessor_current` 与 null sequence，其 statement snapshot 仍归 M41 #374，不在本项虚构跨模型 MVCC。

✅ #360 并发矩阵覆盖：分页 BFS 期间原子 re-parent 只对下一 statement 可见且 writer 在旧 cursor 存活时可完成；同一 session 在并发提交后新建的 cursor 仍固定旧 sequence；不同 element 且不推进共享 metadata 的更新都提交；同 unique property claim 通过 unique key version 条件恰有一个提交；endpoint delete 与 edge insert 通过 endpoint version + adjacency `PrefixEmpty` 条件恰有一个提交。所有竞态设置超时并在完成后运行 `GraphInvariantChecker`。现有 workload 没有跨多个读会话/读写 statement 保持同一快照的需求，因此本项不扩展 snapshot-isolation transaction，也不进行无证据 MVCC 重构。详细合同见 [m40-graph-360-statement-snapshot.md](m40-graph-360-statement-snapshot.md)。

### #361 当前功能切片（✅ 已完成）

Graph V1 adjacency 继续保持每条边一个紧凑 key 和空 value；supernode 不会把全部边物化为一个 value。KV state v5 在 checkpoint/compaction 时对有序 key 做固定 restart 的前缀压缩，并保留 v1-v4 读取兼容。`GraphCursorOptions` 的 page size、page bytes 和 result limit 是硬预算。

`GraphStore.RunMaintenance` 按 work unit 扫描并修复一页，页间释放提交门；`maintenance.sdbgraph` 以 CRC、原子替换和 WAL sync 保存阶段、continuation key、计数和 unique 声明。取消、进程重开或 checkpoint 失败会从最后 durable 页重复执行，坏 sidecar 明确拒绝。最终 checkpoint 是必做的，compaction 由 `CompactOnCompletion` 显式选择；`GraphStore.Checkpoint/Compact` 也提供单独维护边界。统计刷新按 outgoing anchor 流式生成 degree histogram，并受扫描条目与统计分组预算约束。

✅ 功能和恢复回归见 [#361 contract](m40-graph-361-maintenance.md)；📋 这些证据不替代 #352/#367 固定硬件、7 天 mixed workload 或外部数据库对拍门禁。

### #362 当前功能切片（✅ 功能与本地收益证据已闭环）

`GraphReadSession` 现在提供 `WeightedShortestPath`、`Dijkstra`、`AStar`、`BidirectionalDijkstra` 和 `ShortestPathWeighted` 入口。权重可以来自边的 `Int64`/`Float64` 属性或嵌入式调用方 selector；结果包含总权重、实际算法、路径和扩展计数。Dijkstra、显式可选的 A*（非负启发式）与双向 Dijkstra 共享同一 statement snapshot 和有界邻接 cursor，不复制 GraphStore 或建立第二套执行器。

`GraphAlgorithmExecutor.ExecuteShortestPaths`/`RunShortestPaths` 在同一 snapshot 上按输入顺序执行批量加权路径查询。负权、缺失/错误类型、NaN/Infinity、累加溢出、最大深度、frontier、访问顶点数、扩展边数和取消都在实际工作前或工作中稳定拒绝/停止。HTTP/typed SDK 新增 source-generated `/weighted-shortest-path` 合同，嵌入式与远程响应携带相同路径与诊断字段。

✅ Core/HTTP correctness smoke 已覆盖总权重选路、A*/双向结果对拍、入向路径、深度状态、错误权重、溢出、取消、批量顺序，以及随机有向图与有界穷举 oracle 对拍。✅ 新增 `--m40-weighted-path-evidence` topology journey runner 和 BenchmarkDotNet 三算法基准；固定 seed 的 quick topology 上 A* expanded edges -91.6%、双向 Dijkstra -39.9%，P95 分别为 Dijkstra 的 0.465x/0.735x，三者均命中 `native_adjacency`。#362 的功能与本地算法准入证据据此闭环。📋 Couplet/1m-10m 真实语料、退化矩阵、固定目标硬件和发布决定仍统一归 #367，不据此宣称 Production。详细合同见 [#362 weighted path contract](m40-graph-362-weighted-path.md)。

### #363 当前功能切片（✅ 已完成）

`GraphStore.RunOfflineAlgorithms` 在一个固定 statement snapshot 上采集 vertex/edge，并共用可恢复 sidecar 与 spill workspace 计算 directed degree、精确 weakly connected components、PageRank 和确定性 label-propagation community。采集按页、PageRank/community 按完整迭代、Graph/Table 输出按批次 durable checkpoint；取消保留上一个边界，采集续作若 sequence 漂移则明确拒绝。

状态 vector 超过分配预算后切换固定 little-endian file-backed 访问；community vote 按预算生成排序 run 并最多 32 路多轮 merge。结果可写入带 `(operation_id, vertex_id)` 主键的标准 Table，或通过幂等 Graph transaction 写入显式 vertex property mapping；两者都携带 `operationId@sourceSequence` 版本。完成后删除输入和算法 spill，只保留 CRC manifest，不常驻第二份图状态。

✅ correctness/resume/reopen/source-drift/Graph+Table output 与真实 file-backed spill 已覆盖自动回归。📋 Graphalytics/LDBC、1m/10m、固定目标硬件、7 天 mixed workload 和 Couplet C4 联合门禁仍归 #367。详细边界见 [#363 offline algorithms contract](m40-graph-363-offline-algorithms.md)。

### #364 当前功能切片（✅ 已完成）

新增显式 opt-in 的 `GqlParser.Parse` 和 `SqlExecutor.ExecuteGql` 嵌入式只读入口，解析 `USE GRAPH ... MATCH ... RETURN`、固定/有界路径、变量谓词、参数、投影、去重、排序/分页与 `EXPLAIN [ANALYZE]`。GQL 与 SQL/PGQ `GRAPH_TABLE` 调用同一个 MATCH parser，直接生成既有 `GraphTableSource`/`SelectStatement` 并进入同一个 Graph planner/executor；没有 GQL 专用执行器、GraphStore、权限或 wire endpoint。

✅ typed AST、关系 mapping index plan、原生 shortest-path plan 和结果逐行对拍已覆盖自动回归；写语法、多语句、`RETURN *` 和 Cypher label 形式在解析阶段拒绝。公开语法与不支持项见 [#364 GQL 风格入口能力矩阵](m40-graph-364-gql-entry.md)。📋 完整 GQL/Cypher、远程专用入口和 #367 Production gate 不在本项范围。

### #365 当前功能切片（✅ 已完成）

新增 `SonnetDB.KnowledgeGraphs` 的 schema v1 合同和严格校验：Entity、Alias、Claim、Source、Chunk、Community、Summary 节点以及 `ASSERTS`、`SUPPORTED_BY`、`CONTRADICTS`、`ALIAS_OF`、`CHUNK_OF`、`MEMBER_OF`、`SUMMARIZED_BY` 关系统一携带可追溯 provenance，并为事实/证据显式要求 0~1 confidence 与半开 valid time。Document/Object 引用固定 container、ID、version 和可选 chunk/hash；Vector 只保存 index、record ID 与 embedding profile ID。Claim literal 限 4 KiB，合同没有正文、对象字节或 embedding 数组字段。

`KnowledgeGraphMapper` 以固定 `m40-kg-v1` 投影把合同编译为现有 `GraphImportRequest`：稳定外部 ID、label/property ID、unique external ID、expected element version 和 request ID 都进入同一个 Graph transaction，不修改 record/WAL 或新增 endpoint。`ImportKnowledgeGraphAsync` 在嵌入式与远程客户端复用相同 import API；单批节点与关系总数限制为 256，跨批次不宣称原子。source-generated `KnowledgeGraphJsonContext` 可用于 AOT job 边界。抽取、消歧、事实判断、embedding 和 LLM/community summary 生成仍明确留在 Server/SDK/上层产品；Core 只持久化通用属性图。

✅ 合同、非法 confidence/time/chunk/claim/relation shape、稳定投影、Document/Object/Vector 引用、嵌入式与远程写入读取、相同 request ID 重放均已覆盖自动回归。公开边界与示例见 [#365 知识图谱与 GraphRAG 合同](m40-graph-365-knowledge-contract.md)。📋 #366 运维产品面和 #367 固定硬件/恢复/长稳/联合发布 gate 不在本项范围。

📋 Phase 3 完成后，SonnetDB 才能对外称为**生产可用的单机原生属性图数据库**。这不包含分布式图数据库、完整 Cypher/GQL 或 RDF 推理能力。

## 7. 测试与验收矩阵

### 7.1 正确性

- CRUD、multi-label、parallel edge、self-loop、incoming/outgoing、属性类型和索引一致性。
- 事务提交前/中/后故障、torn WAL、checkpoint 交错、备份恢复和索引重建。
- path walk/trail/simple semantics、方向、cycle、零长度路径、重复边和确定性 tie-break。
- SQL 参数、NULL/类型转换、权限、取消、CommandTimeout、远程断线和结果续读。
- invariant checker 至少验证 edge/双邻接一一对应、端点存在、label/property index 无 stale/orphan、ID high-water 不回退。

### 7.2 性能与容量

- 数据形态：均匀度数、幂律分布、supernode、深链、宽 frontier、稠密子图、频繁属性更新，以及代码仓库常见的高扇出公共符号、海量引用边和 revision 切换/增量重建。
- 规模档：开发 quick、100k/1m、固定硬件 1m vertex/10m edge；更大档只在前档稳定后加入。
- 操作：批量导入、point lookup、1/2/3/6 hop、shortest path、属性 anchor、符号定义/引用/调用链、依赖路径、变更影响与测试选择、全文/向量候选后的原生图扩展、混合读写、checkpoint、reopen 和 repair。
- 记录：吞吐、P50/P95/P99、working set、托管分配、WAL bytes、read amplification、候选/检查/返回元素数、expanded edges、frontier peak、实际 access path/fallback 和恢复时间。
- 原生遍历必须通过 trace/plan 证明没有 Table JOIN；关系映射图必须如实报告 index seek 或 scan。
- 声明支持的 workload 若出现复杂度随全图规模而非命中邻接/候选集增长、无界物化、锁内消费、重复全扫或跨模型隐藏全量扫描，即判定门禁失败并回收到责任里程碑优先修复。

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
