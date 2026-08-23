# M40 #341 原生图工作负载与证据合同

本文是 Milestone 40 Phase 0 的设计决策和验收输入。它冻结术语、原生图与关系映射图的边界、
五条 golden journey、性能与容量 SLO，以及 capability gap 的归责方式。Phase 1 Core/Server/SDK
实现可以引用本文，但本文仍不是 Native Graph Preview 的 correctness/performance PASS 报告。

| 属性 | 值 |
|---|---|
| 状态 | Accepted，作为 #342～#367 的前置合同 |
| 冻结日期 | 2026-08-10 |
| 适用里程碑 | M40 #341 |
| 后续门禁 | #352 Native Graph Preview、#359 Graph Beta、#367 Production |
| 上位路线 | [原生属性图数据库路线图](native-graph-database-roadmap.md) |
| 总路线 | [ROADMAP](../ROADMAP.md) |

## 1. 决策记录

### 1.1 背景

SonnetDB 需要同时支持以邻接为一级持久化结构的原生属性图，以及把现有关系表声明为图的
SQL/PGQ 迁移路径。如果两条路径各自实现 parser、计划、遍历和资源治理，语义和故障边界会快速
分叉；如果把关系 JOIN 包装成原生图，又无法满足单跳成本只随目标顶点度数增长的目标。

因此本决策先冻结工作负载和可证伪的门禁，再开始 record、KV cursor、catalog、事务和恢复实现。
任何实现结果都不能反向降低本文阈值。同一阈值的修改必须是独立 ADR，给出新业务证据和兼容
影响，并且只对修改后的候选提交生效。

### 1.2 决策

1. SonnetDB 采用“两种数据来源、一个 Graph Logical Plan、一套 pull operators”。
2. 原生图通过 graph keyspace 中的 vertex、edge、双向 adjacency、label/property index 访问；
   关系映射图通过 Table 主键和二级索引访问，不复制关系数据。
3. 两类图使用不同 accessor。诊断必须分别报告 `native_adjacency`、
   `relation_index_seek` 或 `relation_scan_fallback`，不得混称。
4. 第一版一个命名 graph 对应一个 `KvKeyspace`，只提供单 graph 原子事务。跨 graph、跨模型
   原子提交不在 M40 第一版。
5. 图查询必须受取消、超时、最大深度、frontier、expanded edge、path、row 和返回字节预算约束。
   超限必须返回稳定错误，不能截断后伪装成功。
6. 正确性/恢复 gate 与性能/容量 gate 独立出结论。只有两个 gate 都通过，阶段才可发布。
7. Phase 0 只建立公共地基和证据，不开放 vertex/edge CRUD、遍历、SQL/PGQ 或 Graph 产品文案。

### 1.3 后果

- #342～#346 可以围绕已经冻结的数据类型、读取、生命周期、事务和恢复边界实现，不需要猜测
  上层 workload。
- #352、#359 和 #367 必须直接引用本文的 journey ID、数据生成版本和 SLO ID；没有引用的报告
  不能作为发布证据。
- Couplet 可以并行开发 Git、语言解析、代码 schema、只读 MCP 和评测，但只能使用 SonnetDB
  公开合同。缺失 Graph 能力时必须 fail fast 或保持对应功能未发布。
- 性能优化如果改变结果、隔离、耐久性或错误语义，即使数字更好也判定失败。

## 2. 术语

| 术语 | 冻结定义 |
|---|---|
| Property graph | 由 vertex、edge、label 和 typed property 组成的属性图；edge 有固定 source/target，允许平行边和自环。 |
| Native graph | vertex、edge 和双向 adjacency 是 SonnetDB 一级持久化记录的图；逐跳扩展不经过 Table JOIN。 |
| Relational mapped graph | 由 `CREATE PROPERTY GRAPH` 声明的只读图视图；权威数据仍在关系表，遍历使用关系主键/索引。 |
| Graph catalog | 命名 graph、label、property、index、关系 mapping 和版本依赖的持久目录；不是普通 view 文本。 |
| Graph keyspace | 单个原生 graph 的 KV 持久化边界；复用现有 WAL、checkpoint 和 backup。 |
| Vertex | 具有稳定 `GraphElementId`、零个或多个 label 和 typed property 的图元素。 |
| Edge | 具有稳定 `GraphElementId`、一个 edge label、source、target 和 typed property 的有向图元素。 |
| Label | catalog 分配稳定 `LabelId` 的类型标记；持久 key 不重复保存长名称。 |
| Property | 由 property ID、类型标签和值组成的标量；首版范围为 null、Int64、Float64、Boolean、String、DateTime、Blob 和 Json。 |
| Adjacency | 按 vertex、方向、edge label、neighbor 和 edge ID 排序的独立 KV 记录；不能把 supernode 的全部边塞入单个 value。 |
| External key | 通过唯一 property index 映射到内部 element ID 的业务键；U key 的 value 是版本化 owner mapping，不直接进入每条 adjacency key。 |
| Snapshot lease | cursor 生命周期内固定可见视图及其资源所有权；释放后 borrowed memory 立即失效。 |
| Range cursor | 只向前推进、按页解码且不会从 range 起点重扫的 KV 读取器。 |
| Graph transaction | 缓冲 mutation、校验 element version，并通过一次 graph keyspace 原子 batch 发布的乐观事务。 |
| Graph Logical Plan | native API、SQL/PGQ 和未来可选语法绑定后的共享逻辑计划。 |
| Accessor | 执行算子访问底层数据的接口；首批为 native adjacency accessor 和 relational index accessor。 |
| Expansion | 从一个 vertex 读取指定方向和过滤条件下 adjacency 的操作；`expanded_edges` 是实际检查的邻接条目数。 |
| Frontier | 尚待展开的 vertex 集合；`frontier_peak` 是一次查询中的最大同时驻留数量。 |
| Scan fallback | 缺少可用索引时，在显式预算内扫描关系输入的降级路径；必须可取消并在 EXPLAIN/报告中可见。 |
| Golden journey | 固定数据生成器、更新方式、查询形状、结果 oracle、预算和 SLO 的端到端场景。 |
| Capability gap | 已由 journey、计划、计数器或故障证据复现，且当前公开合同无法满足的通用能力缺口。 |
| Gate | 阶段发布所需的机器可读 PASS/FAIL 结论；`not_run`、`partial` 和缺失证据均不等于 PASS。 |

## 3. 原生图与关系映射图边界

| 维度 | 原生属性图 | SQL/PGQ 关系映射图 |
|---|---|---|
| 权威数据 | graph keyspace 的 vertex/edge record | 现有 Table row |
| 邻接来源 | outgoing/incoming adjacency key | edge table 的 source/destination key 及其索引 |
| 数据复制 | 不复制 Table/Document/Vector/FullText 主数据 | mapping catalog 不复制 row 或暗建 adjacency |
| 写入 | Graph API/SQL 最终调用同一 graph transaction | 第一版只读；数据变化只通过既有 Table DML |
| 原子边界 | 单 graph、单 keyspace atomic batch | 沿用现有 Table 事务边界，不获得 Graph 跨表原子承诺 |
| 索引 | graph label/property/unique index | Table 主键/二级索引；无索引时仅允许显式 scan fallback |
| 扩展复杂度 | 随命中 adjacency 和过滤结果增长 | 随关系索引探测与命中 edge row 增长 |
| 共同上层 | Graph Logical Plan、path 语义、filter/project、预算、取消、错误 | 同左 |
| EXPLAIN | 必须标记 `native_adjacency` | 必须标记 `relation_index_seek` 或 `relation_scan_fallback` |
| 备份 | 扩展现有 manifest/checkpoint/verify/restore | 备份关系主数据和 mapping catalog，不生成第二份图包 |
| 首次产品阶段 | #352 通过后仅称 Native Graph Preview | #359 通过后才进入 SonnetDB Graph Beta |

共同规则如下：

- `GRAPH_TABLE` 只把图结果转成关系行集。外层 JOIN、GROUP BY、ORDER BY 和投影由共享 SQL
  执行层处理；逐跳扩展不能交给 `RelationalSelectExecutor` 循环 JOIN。
- 同一模式在 native API 与 SQL/PGQ 下必须共享计划和执行算子，不得各写一套 BFS/Expand。
- Graph property 只保存小型 typed scalar 或其他模型的稳定 ID/reference。大文档、媒体正文、
  embedding 和全文倒排仍由其权威模型管理。
- `relation_scan_fallback` 只适用于本文显式允许的负向/诊断用例。五条声明 journey 的性能样本
  都必须命中冻结 access path；出现隐藏 scan 即为 FAIL。

唯一 external-key 的 U value 冻结为 `GraphRecordKind.UniquePropertyOwner = 4` 的 V1 record envelope。
其 payload 依次为 `int32 little-endian version = 1`、`byte element kind`、3 个零保留字节和
`int64 little-endian owner ID > 0`，并受 envelope 长度及 CRC 保护。它是 owner 映射而不是空派生
索引值；读取时必须同时校验 owner record、完全一致的 P projection 和唯一键 collision。

## 4. 统一证据口径

### 4.1 数据档位

| 档位 | Vertex | Edge | 用途 | 发布证据 |
|---|---:|---:|---|---|
| `quick` | 10,000 | 100,000 | PR smoke、schema 和报告管线 | 否 |
| `preview-small` | 100,000 | 1,000,000 | #352 正确性、复杂度趋势和回归 | 仅辅助 |
| `gate` | 1,000,000 | 10,000,000 | #352/#359 固定硬件门禁 | 是 |
| `production-soak` | 1,000,000 | 10,000,000 | #367 7 天 mixed workload | 是 |

所有合成数据使用固定 64 位 seed `0x534F4E4E45544442`，并将 generator schema/version、输入摘要
和输出摘要写入报告。journey 可以使用少于 1,000 万条业务 edge，但必须追加不参与目标查询的
同分布数据达到 `gate` 总规模，证明访问成本不随无关全图数据增长。

### 4.2 固定目标硬件

发布门禁的首个固定目标为仓库现有基准机：

| 资源 | 冻结值 |
|---|---|
| OS | Windows 11 25H2 x64，构建号随报告记录 |
| CPU | Intel Core Ultra 9 185H，16 physical / 22 logical processors |
| Memory | 64 GiB physical memory |
| Disk | NVMe PC SN8000S WD 2048GB，本地 NTFS |
| Runtime | `global.json` 约束下的 .NET 10 x64 Release；SDK、runtime、GC mode 和 commit SHA 必须入报告 |
| Power | AC 供电、Windows Best performance；长测期间不运行其他数据库或 benchmark |

更快机器可以提供补充报告，但不能替代该固定目标。目标机不可用时门禁保持 `not_run`，不得用
hosted CI、容器限额或开发机结果换算。ARM64 是 #367 的额外发布证据，不改变上述 x64 SLO。

### 4.3 运行与统计

- #352 热查询：数据导入后执行 checkpoint，重开数据库，完成 1,000 次不计时 warmup；每个
  查询形状至少采集 10,000 个完整消费样本，独立运行 3 轮，以三轮中最差 P95/P99 判门。
- 延迟从 API/command 调用前开始，到结果 cursor 全部消费并释放为止；另报 time-to-first-row，
  但不能用它代替完整延迟。
- 分位数使用 nearest-rank。报告同时包含 P50/P95/P99/max、吞吐、managed allocation、
  working set、logical/physical read bytes、WAL bytes、candidates、examined、returned、
  expanded edges、frontier peak、实际 access path 和 fallback reason。
- 查询的“内存上限”是相对稳定 idle baseline 的 query-owned peak live bytes，包括 frontier、visited、
  path、页缓冲和结果缓冲，不含数据库常驻索引；仍需单独满足 `gate` 数据下进程 working set
  不超过 12 GiB。超过任一上限即 FAIL，不能依赖 64 GiB 物理内存掩盖无界增长。
- `preview-small` 与 `gate` 的目标查询应保持 `examined/returned` 和 `expanded_edges` 的同阶关系。
  无关全图扩大 10 倍时，point/adjacency 查询的 examined 数不得随之线性增长。
- #367 在同一数据和机器上运行 7 天：8 个 reader worker 加 1 个 journey update worker，使用下文
  冻结更新速率；每 30 分钟 checkpoint，每 24 小时执行 kill/reopen 和 invariant check。生产延迟
  阈值是对应 #352 阈值的 2 倍，内存上限不放宽。
- KV 使用耐久默认值：`SyncWalOnEveryWrite=true`、`AutoCheckpointEnabled=true`、
  `MaxWalBytes=256 MiB`、`MaxOverlayEntries=100,000`。不得通过关闭 fsync/checkpoint、提高预算、
  减少查询结果或关闭 invariant check 达标。
- 冷启动单独判门：`gate` 数据 checkpoint 后的 open P95/P99 不超过 2,000/5,000 ms；每个 journey
  的首个查询 P99 不超过 `max(4 x 热查询 P99, 2,000 ms)`，且 open/首查不能全量重建可持久索引。

记号：`d(v)` 是过滤前目标邻接度数，`X` 是实际 expanded edge 数，`R` 是 reached vertex 数，
`C` 是索引产生的 candidate 数，`K` 是返回数，`F` 是 frontier peak。复杂度是 access-path 合同，
不是仅供说明的估算；trace 违反合同即使延迟达标也判 FAIL。

## 5. Golden journeys 与冻结 SLO

下表中的 P95/P99 是 #352 或 #359 的热查询上限；#367 mixed workload 使用其 2 倍值。每个查询
都必须完整消费到冻结 row/path limit。`budget` 是硬上限，超限测试应返回预算错误，不进入延迟样本。

### 5.1 GJ-SOCIAL：社交多跳

**数据和分布**

- 1,000,000 `Person` vertex、9,500,000 `FOLLOWS`/`KNOWS` edge，加 500,000 条其他类型 edge。
- 度数使用确定性幂律分布；至少一个 100,000-degree supernode；包含自环和平行 edge。
- property 包含 tenant、country、active、createdAt；person business key 和 `(label, country)` 可索引。

**更新模式**

- 初始 bulk load 后，update worker 持续执行 500 edge mutation/s：45% create、45% delete、10%
  property update；每批不超过 100 mutation，保持默认 WAL 耐久配置。

**查询与 SLO**

| ID | 典型查询和预算 | 冻结复杂度 | Query memory | #352 P95/P99 |
|---|---|---|---:|---:|
| `SOC-1` | 从 person 扩展一跳 `KNOWS`，按 active/country 过滤，`LIMIT 100` | `O(d(v) + K)`；不得读取无关 vertex/edge | 32 MiB | 10/25 ms |
| `SOC-2` | 1～3 hop trail，`X<=250,000`、`F<=100,000`、`paths<=1,000`、`LIMIT 100` | `O(R + X)` | 128 MiB | 100/300 ms |
| `SOC-3` | 无权 shortest path，最大 6 hop，`X<=500,000`、`F<=200,000` | `O(R + X)`；tie-break 稳定 | 256 MiB | 300/900 ms |

正确性 oracle 必须覆盖方向、自环、平行 edge、cycle、walk/trail/simple 差异和不可达结果，并与
Neo4j 参考结果对拍。

**明确不做**：好友推荐模型、PageRank/社区算法、无限深度 all-path、完整 Cypher、跨租户遍历，
以及用关系 edge table/JOIN 代替 native adjacency。

### 5.2 GJ-TOPOLOGY：设备拓扑与影响范围

**数据和分布**

- 1,000,000 个 Site/Controller/Gateway/Device vertex、3,000,000 条 `CONTAINS`/`CONNECTS`/
  `DEPENDS_ON` edge；追加同分布非目标 edge 达到 10,000,000。
- 95% 层级深度不超过 8，加入 1% 环和断链；50 个 gateway 的 degree 在 10,000～50,000。
- measurement 只保存 `deviceId` reference；遥测值不复制进 graph property。

**更新模式**

- update worker 持续执行 100 mutation/s，包括设备上线/下线、链路切换和 parent 变更；每分钟执行
  一个 1,000-mutation 幂等配置批次。

**查询与 SLO**

| ID | 典型查询和预算 | 冻结复杂度 | Query memory | #352 P95/P99 |
|---|---|---|---:|---:|
| `TOP-1` | 从故障设备反向找 1～6 hop 上游根，`X<=100,000`、`F<=50,000`、`LIMIT 100` | `O(R + X)` | 96 MiB | 75/225 ms |
| `TOP-2` | 从 controller 找 1～6 hop 受影响设备，按 site/type 过滤，`X<=250,000`、`LIMIT 10,000` | `O(R + X + K)` | 192 MiB | 200/600 ms |
| `TOP-3` | 两设备间受限 shortest path，最大 8 hop、`X<=500,000` | `O(R + X)` | 256 MiB | 350/1,000 ms |

正确性 oracle 必须覆盖环、断链、方向、重复链路、并发 re-parent 的 statement snapshot 和预算取消。

**明确不做**：实时网络路由/控制平面、分布式拓扑共识、把 measurement 历史复制到 graph、
静默忽略环或断链，以及用提高 frontier 默认值处理 supernode。

### 5.3 GJ-EVIDENCE：知识证据链

**数据和分布**

- 400,000 Entity、300,000 Claim、200,000 Source/Chunk、100,000 Alias vertex，共 1,000,000；
  8,000,000 条 `ASSERTS`/`SUPPORTED_BY`/`CONTRADICTS`/`ALIAS_OF` edge，另加 2,000,000 条
  低置信或过期 edge。
- edge property 包含 confidence、validFrom/validTo 和 provenance reference；文档正文仍以
  Document/Object 中的稳定 ID 为权威来源。

**更新模式**

- update worker 持续执行 200 mutation/s，80% 新 claim/evidence，15% validity/confidence 更新，
  5% retract；每日批次在 gate 中缩短为每 30 分钟执行一次 10,000 mutation。

**查询与 SLO**

| ID | 典型查询和预算 | 冻结复杂度 | Query memory | #352 P95/P99 |
|---|---|---|---:|---:|
| `EVD-1` | entity 到 source/chunk 的 1～4 hop evidence chain，过滤 validity/confidence，`X<=100,000`、`LIMIT 100` | `O(C + R + X)` | 128 MiB | 100/300 ms |
| `EVD-2` | claim 的支持与反驳来源，2 hop、`X<=50,000`、`LIMIT 500` | `O(d(claim) + X + K)` | 64 MiB | 50/150 ms |
| `EVD-3` | alias 归一后返回确定性最短证据链，最大 6 hop、`X<=250,000` | `O(R + X)` | 192 MiB | 250/750 ms |

oracle 必须逐项验证 edge provenance、valid time、confidence、来源 ID 和路径顺序，不能只比较行数。

**明确不做**：实体抽取、消歧、LLM 推理、事实真伪裁决、RDF/SPARQL/本体推理、社区摘要，
以及在 graph property 内保存文档正文、媒体或第二份 embedding。

### 5.4 GJ-COUPLET：代码符号、引用、调用与测试影响

**数据和分布**

- 固定 Couplet fixture manifest 产生 1,000,000 个 Repository/Revision/File/Symbol/Test vertex 和
  10,000,000 条 `DECLARES`/`REFERENCES`/`CALLS`/`INHERITS`/`IMPORTS`/`TESTS` edge。
- 包含高扇出公共 symbol（100,000 references）、重载、partial type、生成文件、循环调用和跨项目依赖。
- symbol identity、revision 和 source span 是稳定 property/reference；源文件正文由 Document/文件系统管理。

**更新模式**

- update worker 每 60 秒提交一个确定性 revision delta：1,000 个变更文件、10,000 个受影响 symbol、
  100,000 条 edge add/remove；使用幂等 checkpoint 分批，查询只看到完整已发布 revision。

**查询与 SLO**

| ID | 典型查询和预算 | 冻结复杂度 | Query memory | #352 P95/P99 |
|---|---|---|---:|---:|
| `CPL-1` | 以稳定 symbol key 查定义及直接 references，`LIMIT 1,000` | `O(log V + d(symbol) + K)` | 32 MiB | 15/40 ms |
| `CPL-2` | callers/callees 1～3 hop，`X<=100,000`、`F<=50,000`、`LIMIT 1,000` | `O(R + X)` | 128 MiB | 100/300 ms |
| `CPL-3` | 变更影响与测试选择，最大 6 hop，`X<=500,000`、`F<=200,000`、`LIMIT 10,000` | `O(R + X + K)` | 256 MiB | 500/1,500 ms |
| `CPL-4` | 从 FullText/Vector 的至多 1,000 个候选做 1～2 hop 扩展，`X<=100,000` | `O(C + R + X)`，不得扫描全图或全向量集 | 128 MiB | 150/450 ms |

Phase 1 #352 必须执行 `CPL-1`～`CPL-3`；`CPL-4` 在 #359 且 M35/M36 候选合同可用后判门。
oracle 由 Couplet fixture 的 definition/reference/call/impact/test manifest 与 Graph 结果逐 ID 对拍。

**明确不做**：在 SonnetDB Core 内实现 Git/worktree、语言 parser、代码 schema、embedding、context
pack 或 MCP；Couplet 不读取内部 key layout，不实现应用层 BFS/DFS、关系边表或第二套图存储，
也不能以隐藏 FullText/Vector 全扫补缺。

### 5.5 GJ-PGQ：关系表 SQL/PGQ mapping

**数据和分布**

- `person` 表 1,000,000 行；`knows` 表 9,000,000 行；`works_at` 表 1,000,000 行；总 edge row
  10,000,000。edge table 有稳定主键，以及 source/destination 外键和二级索引。
- `CREATE PROPERTY GRAPH` 只持久化 mapping、label/property 和 key 定义，不生成 vertex/edge 副本。

**更新模式**

- 只通过 Table DML 持续执行 500 row mutation/s；mapping graph 本身只读。DDL 变更必须经过依赖
  检查，不能让已打开 mapping 看到半更新 schema。

**查询与 SLO**

| ID | 典型查询和预算 | 冻结复杂度 | Query memory | #359 P95/P99 |
|---|---|---|---:|---:|
| `PGQ-1` | `GRAPH_TABLE` 从 person 做一跳 `knows`，外层过滤并 `LIMIT 100` | 每个 frontier vertex 为 `O(log E + d(v))` | 64 MiB | 30/90 ms |
| `PGQ-2` | 固定 3 hop pattern 后与 department JOIN/GROUP BY，`X<=100,000`、`LIMIT 1,000` | `O(R log E + X)` 加共享关系算子成本 | 192 MiB | 250/750 ms |
| `PGQ-3` | 缺少 edge endpoint 索引的同形查询 | 显式 `relation_scan_fallback`，受 10,000 row/50 ms scan budget 限制并 fail bounded | 64 MiB | 仅验稳定预算错误，不设成功延迟 SLO |

oracle 使用等价 PostgreSQL SQL/PGQ 参考数据对拍 label、property、方向、NULL 和外层 SQL 结果。
`PGQ-1`/`PGQ-2` 的 EXPLAIN 必须为 `relation_index_seek`；任何 scan 都判性能 gate 失败。

**明确不做**：把 mapping 标记为 native、隐藏复制关系主数据、通过 mapping graph 写 Table、暗建
materialized graph、承诺 PostgreSQL wire/完整 SQL/PGQ，以及为通过基准在产品侧拼接结果。

## 6. 双 gate

### 6.1 Gate A：正确性与恢复

Gate A 不使用百分比或“允许少量误差”。以下项目全部 PASS 才能得到 `correctness_recovery=PASS`：

1. 五条 journey 的结果逐 ID/property/path 对拍；native 语义以独立 oracle 和 Neo4j 为参考，
   mapping 语义以 PostgreSQL SQL/PGQ 为参考。结果必须为零 missing、duplicate、unexpected 和
   value/path mismatch。
2. 覆盖空图、单 vertex、self-loop、parallel edge、cycle、零长度路径、方向、NULL、所有首版
   property 类型、稳定 tie-break、取消、超时和每种预算超限。
3. 每次 edge mutation 后，edge record、outgoing adjacency、incoming adjacency、label membership、
   property index 和统计 dirty marker 要么全部可见，要么全部不可见。
4. 并发 element version 冲突、重复 request、取消、超限和 commit-outcome-unknown 不得产生半条 edge、
   orphan adjacency、stale index 或 ID high-water 回退。
5. 对 WAL append 前后、fsync 前后、publish 前后、checkpoint 各阶段、backup/restore 和 index repair
   注入真子进程 kill。重开后只能是完整提交前或完整提交后状态。
6. invariant checker 必须发现故意构造的 missing endpoint、单边 adjacency、edge projection mismatch、
   stale/orphan label/property index 和 high-water 回退；不能把损坏自动忽略为成功。
7. record/key/catalog/manifest 的旧版本必须按冻结策略迁移或稳定拒绝；corrupt magic/version/length/CRC
   不得被接受。

报告必须列出每个测试/fixture、seed、commit、注入点、预期摘要、实际摘要和原始 artifact。任何
`not_run`、flaky retry 后才通过或未解释差异都使 Gate A 失败。

### 6.2 Gate B：性能与容量

只有以下项目全部满足，才得到 `performance_capacity=PASS`：

1. 在固定目标硬件运行 `preview-small` 和 `gate`；quick 仅验证管线，不能代替容量证据。
2. 对所有阶段应验 journey 达到本文 P95/P99、query memory、12 GiB process working set 和 cold-open
   阈值；#367 还必须完成 7 天 mixed workload。
3. 每个样本记录实际 access path。native journey 不得出现 Table JOIN；`PGQ-1`/`PGQ-2` 不得出现
   relation scan；`CPL-4` 不得隐藏全量 Document/FullText/Vector/Graph scan。
4. point/adjacency 查询复杂度不能随无关全图规模增长；路径查询的 CPU、I/O 和内存只能随实际
   candidates、reached vertex、expanded edge、frontier 和输出增长。
5. 不得出现无界物化、分页从头重扫、keyspace 锁内用户消费、重复遍历或通过提高默认资源上限
   掩盖的 OOM/timeout。
6. 报告保留三个原始测量轮次、环境清单、配置、generator digest 和计数器；只展示最佳数字或
   缺少失败样本都判 FAIL。

两个 gate 在报告中必须是独立字段：

```text
correctness_recovery: PASS | FAIL | NOT_RUN
performance_capacity: PASS | FAIL | NOT_RUN
release_decision: PASS only when both gates are PASS
```

Gate A 失败时，Gate B 数字仍可用于诊断，但不能形成容量声明。Gate B 失败时，也不能以 Gate A
正确为由发布。#352 失败阻断 Preview，#359 失败阻断 Beta，#367 任一 gate 失败阻断 Production 和
“九种数据模型”定位。

## 7. Capability gap catalog

### 7.1 登记模板

发现者必须复制下表登记；没有复现和关闭证据的口头判断不能改变 release gate。

| 字段 | 必填内容 |
|---|---|
| `gap_id` | 稳定 ID，格式 `M40-GAP-NNN` |
| `status` | `open`、`in_progress`、`closed`、`not_planned` |
| `journey/query` | 本文 journey/query ID；新场景需附 fixture |
| `first_seen` | commit SHA、日期、运行环境 |
| `reproduction` | generator version/seed、规模、更新模式、最小命令和 artifact |
| `expected_contract` | 正确性、不变量、复杂度、内存或延迟的具体合同 |
| `observed` | mismatch、实际 access path、计数器、P95/P99、内存和错误码 |
| `owner` | 仓库、里程碑、编号和责任模块 |
| `severity` | `correctness_recovery`、`bounded_execution`、`capacity_latency`、`api_product` |
| `blocks` | Phase 0、Preview、Beta、Production 或 Couplet C0～C4 |
| `temporary_behavior` | 只能是 fail fast、降低 capability 等级或保持未发布；不得记录旁路实现 |
| `close_evidence` | 修复 PR、回归测试、同 fixture before/after、固定硬件报告和 reviewer |

状态规则：

- `open`/`in_progress` 的 blocking gap 使对应 gate 失败。
- `closed` 必须同时具有自动回归和要求的容量/恢复证据；只有代码提交不能关闭。
- `not_planned` 只适用于明确不做且没有对外声明的能力；若 journey 依赖它，必须先降低产品能力
  等级，不能把 blocking gap 改名为 `not_planned`。
- 优先级固定为 correctness/recovery、bounded execution、capacity/latency、API/product。

### 7.2 当前 gap 与归责

冻结日尚无 M40 Graph 运行能力或发布报告。下表保留 #341 的初始缺口描述，并追加当前状态和关闭证据；
不删除历史项。Phase 0 的关闭证据是自动正确性/恢复回归，不等同于 #352/#359/#367 要求的固定硬件发布报告。

| Gap | 当前缺口 | Owner | Blocks | 状态/当前证据 |
|---|---|---|---|---|
| `M40-GAP-001` | 通用 sortable scalar codec、GraphElementId/LabelId/GraphPropertyValue、版本化 record/key 尚未交付 | SonnetDB M40 #342 | Phase 0、Preview | `closed`；`SortableScalarCodecTests`、`GraphPropertyValueTests`、`GraphStorageCodecTests`、`GraphElementRecordCodecTests` 与 `GraphFrozenV1FormatTests` 固定 round-trip、排序、严格 UTF-8、64 KiB key/16 MiB record 上限、损坏拒绝和完整 V1 bytes，`TableEncodingCompatibilityTests` 证明 Table V1 字节不变 |
| `M40-GAP-002` | KV snapshot lease、前向 range cursor、页缓冲所有权和取消合同尚未交付 | SonnetDB M40 #343；与 M41 #374 共享 | Phase 0、Preview、Couplet C1 | `closed`；`KvReadSnapshotTests` 覆盖 lease、三层惰性合并、二分 seek、固定 TTL 时刻、取消、pending 所有权、锁外消费，以及条目数/payload bytes 双预算下的 page-bounded 分配；`KvConditionalBatchTests` 固定条件提交原语 |
| `M40-GAP-003` | GraphCatalog、GraphManager、命名/依赖和 `Tsdb.Graphs` 生命周期尚未交付 | SonnetDB M40 #344 | Phase 0、Preview | `closed`；`GraphCatalogCodecTests`、`GraphManagerTests`、`GraphStoreMarkerTests` 与 `GraphManagedCatalogGuardTests` 覆盖目录 CRC/版本、create/open/reopen/drop、原子替换 outcome unknown、固定 marker 有界拒绝、跨模型名称与依赖竞态、异常释放和单 owner；`CrashReliabilityTests` 覆盖跨进程 lease 退出后重开 |
| `M40-GAP-004` | 单 graph transaction、element version、写预算、commit-unknown、vertex `RESTRICT` 尚未交付 | SonnetDB M40 #345 | Phase 0、Preview | `closed`；`GraphTransactionTests` 与 `GraphTransactionLimitTests` 覆盖版本冲突、幂等 request、取消、commit unknown、vertex `RESTRICT`、展开后预算、无限 enumerable 有界停止，以及拒绝时零 WAL/sequence/state 副作用 |
| `M40-GAP-005` | Graph backup manifest、checkpoint、verify/restore、invariant checker 和 CrashTests 骨架尚未交付 | SonnetDB M40 #346 | Phase 0、Preview、Couplet C1 | `closed`；`BackupServiceTests` 覆盖 manifest v2、v1 无 Graph 兼容、checkpoint/verify/restore 和发布前 reopen + invariant；`GraphInvariantCheckerTests` 检出 orphan/mismatch，`CrashReliabilityTests` 覆盖事务注入点与备份/提交一致性 |
| `M40-GAP-006` | Native GraphStore、双向 adjacency、索引、streaming API、路径、统计、Server/SDK/import 的完整验收证据尚未收口 | SonnetDB M40 #347～#351 | Preview、Couplet C2 | `in_progress`；Core/REST/Frame/SDK/import 和本地回归已实现，固定规模复杂度、完整 Graphify fixture 与恢复边界仍待 reviewer/门禁证据，禁止 Table edge workaround |
| `M40-GAP-007` | Preview correctness/recovery 和 performance/capacity 证据尚未交付 | SonnetDB M40 #352 | Preview、Couplet C2 | `open`；本文只冻结门禁，不是 PASS 报告 |
| `M40-GAP-008` | 共享 Graph plan、原生 SQL、SQL/PGQ mapping、GRAPH_TABLE、planner 和组合查询尚未交付 | SonnetDB M40 #353～#359 | Beta、Couplet C3 | `closed_functional`；#353~#359 已交付共享计划、原生 SQL、无副本 mapping、有界固定/可变长度/shortest GRAPH_TABLE、成本/实际指标、派生图行集与 Table/Document/Hybrid 的共享 SQL 组合、候选/fallback 诊断、权限、可验证备份、Studio 与 Parity capability；本地 Core、Frame、远程 ADO.NET REST 和 Web build 回归通过。外部 PostgreSQL/Neo4j、固定硬件和 Couplet C3 联合报告仍为 `NOT_RUN`，不构成 Beta 发布 PASS |
| `M40-GAP-009` | statement snapshot、supernode/维护、高级算法准入、运维面和生产证据尚未全部交付 | SonnetDB M40 #360～#367 | Production、Couplet C4 | `in_progress`；#360 statement snapshot、#361 supernode/可恢复维护、#362 加权路径与本地收益准入、#363 可恢复离线 degree/component/PageRank/community 及 Graph/Table 版本输出、#364 受限 GQL 入口、#365 知识图谱合同和 #366 运维产品面均已关闭功能切片。#352/#367 固定硬件、7 天、Graphalytics/LDBC、恢复/容量与 Couplet C4 联合证据仍 open，不能提升产品定位 |
| `M40-GAP-010` | 关系 mapping 所需共享 pull operator、索引访问和可解释 fallback 若缺失 | SonnetDB M41，重点 #374～#381 | Beta/Production | `open`；由 PGQ trace 触发和关闭，不在 M40 复制关系优化器 |
| `M40-GAP-011` | filtered ANN、FullText/Vector 候选与 hybrid lifecycle 若不能满足 `CPL-4` | SonnetDB M35/M36 或后续公共里程碑 | Beta、Couplet C3 | `open`；不得隐藏全量 scan |
| `M40-GAP-012` | Git/worktree、语言解析、代码 schema、增量协调、MCP 和 Agent eval 产品能力 | Couplet C0～C4 | 对应 Couplet 阶段 | `open`；不是 SonnetDB Core 能力，不得反向进入 Core |

归责规则：GraphStore、adjacency、事务、路径、统计、恢复和 Graph plan 归 M40；共享关系算子归 M41；
Document、FullText、Vector、filtered ANN 和 hybrid lifecycle 归 M32/M35/M36 或后续公共里程碑；
代码产品能力归 Couplet。上层复现通用 Core gap 时必须把证据回收到对应 owner，并阻断依赖阶段。

## 8. #341 完成与不完成

#341 只有在以下文档事实被 reviewer 确认后完成：

- 术语与 native/mapped 边界无歧义；
- 五条 journey 均有固定规模、更新、查询、复杂度、内存、P95/P99 和不做项；
- 固定硬件、seed、样本和报告计数器足以让 #352/#359/#367 实现 runner；
- 双 gate 零容忍正确性规则和性能容量规则可以分别判定；
- gap 模板、当前 gap、owner 和 blocking phase 已登记。

#341 完成仍不代表以下任何能力完成：Graph record/codec、snapshot cursor、catalog、事务、backup、
vertex/edge CRUD、Expand/BFS/DFS、SQL/PGQ、Server/SDK、UI、Neo4j/PostgreSQL parity、固定硬件 PASS
或生产发布。Phase 0 完成前不得进入 Preview；#367 通过前产品定位继续是“八种数据模型，一套引擎”。
