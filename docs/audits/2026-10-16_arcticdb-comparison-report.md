# ArcticDB 与 SonnetDB：差距、取舍与可采纳项

核查日期：2026-10-16（Asia/Shanghai）。SonnetDB 基线：本地 `main` / `dcadffc7137f5653ec46c55a4171cf5f2d83de03` 加当前未提交工作树。本报告是文章、官方资料及本地源码分析；未安装或运行 ArcticDB，未进行同条件性能对照。

**判断：ArcticDB 最值得学习的是面向分析人员的完整工作流：DataFrame 直接存取、可查询的数据版本、围绕时间和列裁剪组织存储。SonnetDB 在这些工作流上有差距，但已有 schema 演进、稀疏字段、压缩、范围查询、版本发布等基础。应扩展已有能力，避免重新造轮子或改变多模型定位。**

## 1. 文章说了什么，哪些需要限定

文章《英仕曼开源的时序数据库 —— ArcticDB》介绍的是面向 Python 数据分析的嵌入式 DataFrame 数据库。官方将其定位为 OLAP，使用 Python/C++ 绑定，并可直连对象存储；官方 FAQ 明确没有 SQL 接口。[A1][A2][A3]

| 文章说法 | 官方核查结果 | 对比较的影响 |
|---|---|---|
| Pandas 进、Pandas 出 | 与官方介绍一致；支持按时间范围和列读取，并有 LazyDataFrame / QueryBuilder 分析接口 | 这是实质性的用户体验优势，不只是多提供一种连接器。 |
| 无 schema，追加和更新自由变字段 | 默认是 static schema；动态演进需创建 library 时显式选择 `dynamic_schema`，索引及类型仍有约束 | 不能将其理解为任意列类型、任意更新都能无损接受。 |
| 时间旅行可以避免回测使用未来数据 | 支持按版本号、时间及快照读取历史数据；正确使用仍依赖当时已保存的数据和完整分析流程 | 2026 年才导入的一份修正后历史数据，无法凭空还原 2023 年真正已知的信息。事件时间与版本写入时间要分开。 |
| symbol 独立，因此机器越多越快 | 官方支持跨 symbol 并发；单 symbol 普通并发写不提供隔离，存在最后写入者覆盖可访问版本链的语义；并行摄取需 staging/finalize | 不能作为通用多写者数据库或自动分片集群的替代。 |
| 不可变版本意味着永不损坏、读取永远可用 | 不可变数据和无独立数据库服务减少特定更新及服务瓶颈；存储、网络、凭据、删除及硬件故障仍在系统边界内 | 本轮没有故障注入证据，不能从架构介绍推导端到端 HA/SLA。 |
| 40 万证券、20 年历史，可放一个 symbol | 官方 README 确有同类容量描述，但不是与 SonnetDB 同机、同语料、同耐久性条件的实测 | 可以作为待验证目标，不能据此给出我们慢多少倍或落后多少年。 |
| BSL，一上生产就必须付费 | 当前 README 和商业 FAQ 确实这样表述，FAQ 还包括商业研究/开发；但同次取得的 `LICENSE.txt` Additional Use Grant 只明确限制其定义的 Database Service | **官方材料存在口径冲突**。本报告不据其中一份材料下“内部商用免费”或“所有生产必付费”的最终结论；具体版本及分发物许可须单独厘清。 |

许可证还写有每个版本发布两年后转为 Apache 2.0 的安排；未逐个核实已转换版本及其功能范围。文章的许可提醒有官方 README 依据，不能简单认定文章错误；同样不能忽略仓库授权正文的差异。[A4][A5][A6][A7]

## 2. 实际差距矩阵

| 维度 | ArcticDB 已核查合同 | SonnetDB 本地证据 | 判断 |
|---|---|---|---|
| Python 分析入口 | DataFrame 写入、读回、时间/列切片与延迟分析 | Python 是 `ctypes` 封装 C ABI，返回游标/tuple；README 要求准备 Native 库；所查连接器未见 Pandas/Arrow 专用读写入口，参数绑定也有明确限制 | **明显差距：分析使用体验与分发。** 有 Python 连接器不等于已完成 DataFrame 工作流。 |
| 历史数据版本 | symbol 的不可变版本链，`as_of`，命名快照固定多个 symbol 的版本 | 已有备份、短期读快照、对象版本；`DatabaseGenerationManager` 已支持发布、指定 revision 租约、枚举及延迟清理，但资源枚举仅 KV/Document/FullText | **局部已有、主要差距明确。** 所查源码/API 未见 measurement/关系数据通用历史修订查询与 DataFrame 版本工作流；不能称为“完全没有版本”。 |
| 对象存储上的分析 | Python 进程内引擎直接访问 S3/Azure 等后端，存算分离 | 引擎以本地数据库目录为边界，segment 通过文件路径读写；`SndbObjectStore` 自己把对象内容放入数据库的 `objects` 目录 | **架构差异。** “对外提供对象存储”与“以外部对象存储作为分析引擎底座”方向不同。 |
| schema 演进 | static/dynamic 模式，有限类型提升 | `MeasurementSchemaPolicy` 有 Disabled/CreateOnly/CreateAndEvolve；默认允许创建/扩列/兼容的 INT64→FLOAT64 提升，并有增长额度 | **已有对应能力，不能列为从零缺口。** 需要改善类型合同、用户入口和可见性。 |
| 稀疏时间序列 | 针对稀疏/稠密时序设计列式布局 | measurement 按字段时间轴合并，缺失字段返回 NULL；已有稀疏分组、排序、COUNT 等测试源码 | **已有语义基础。** 存储成本、宽表往返和 Pandas nullable dtype 映射仍需量化。 |
| 范围查询、压缩 | 按行/列切块，索引定位，压缩后传输，客户端 C++ 执行 | 已有 block、时间索引、范围解码、聚合与 LIMIT 下推；delta-of-delta、Gorilla XOR、Boolean RLE、字符串字典已有 | **不是缺算法名称。** 差距重点是端到端批处理、复制成本、读取放大和可验证的默认策略。 |
| 大结果内存 | 分析接口贴近批量数据，并把部分计算留在原生引擎 | README、M42 文档明确区分响应分块、预览、物化准入与全链路流式；部分执行仍先生成完整结果 | **当前值得优先解决。** Python 外层 `fetchmany` 无法自动消除内部全量物化。也不假定 ArcticDB 所有操作都有内存硬上限。 |
| 规模及成熟度证据 | 官方展示金融分析定位及大规模数据描述 | M19/M25/M42 固定硬件和长期门禁仍待补；2026-09-01 系统性能报告已明确撤回 | **证据差距，性能倍数 UNKNOWN。** 本次未重新验证历史测试结果，不把源码存在写成实测 PASS。 |
| 产品范围 | Python 分析、版本化 DataFrame；不提供 SQL 事务隔离 | 九模型、SQL、.NET/ADO.NET/EF Core、多语言、嵌入式/Server、工业接入；各自仍有成熟度边界 | **取舍不同。** SonnetDB 适合连接设备数据、业务对象和分析；不能以模型数量证明性能或成熟度更高。 |

对应源码与文档：[S1]—[S14]。ArcticDB 官方材料：[A2]—[A10]。

## 3. 最值得学习的五件事

### 3.1 把分析入口做到一条闭环

用户任务可以是“取设备一段时间的温度与压力，转为 DataFrame，运行分析，再把结果与来源一起保存”。目前 Python 调用者仍需自行处理 SQL、结果转换和 Native 库准备。[S1]

建议先做有限的 Pandas 适配：读取指定 measurement/列/时间范围，批量写入，明确 index、TAG/FIELD 和类型映射；随后按测量结果决定是否增加 Arrow 批接口。Pandas/PyArrow 放在可选 Python 依赖中，保持 Core 的零第三方运行时依赖和 AOT 合同。

首片必须验证 NULL/NaN、带时区时间、时间精度转换、大整数、中文列名、重复时间、乱序、取消与超预算。不要通过手工拼接用户值到 SQL 弥补当前参数绑定限制；不要用无界 `fetchall` 再包装 DataFrame 作为大数据方案。这里描述的是建议接口能力，不是已存在 API。

### 3.2 让分析结果能够复现

典型工业需求：09:00 的读数后来被修正，旧告警使用的是修正前数据，新报表使用修正后数据。系统需要说明两次分析各自使用的数据、schema、规则和模型版本。该需求与金融回测类似，但不能只靠事件时间过滤解决。[A3][A8]

SonnetDB 已有 generation 的原子发布、revision 查询和租约回收，适合作为复用起点。[S2] 建议先选择一个有限分析数据集，冻结来源与版本 manifest，并证明重开后可复算；再决定是否扩展 measurement 的持久历史修订能力。不能只保存当前文件路径或最新数据库名称，因为其指向的数据仍可能变化。

“命名版本集合”不自动等于“跨模型同一事务时点”。对跨模型数据要声明采集窗口与一致性边界；备份也不能自动补齐从未保存的历史版本。正式历史保留还涉及 TTL、删除、compaction、活动读者和空间回收，不能仅增加一个 `AS OF` 解析分支。

### 3.3 让列裁剪、时间裁剪真正省 I/O

ArcticDB 将 DataFrame 按行/列切块，并通过版本与索引定位数据；同一布局也暴露小对象碎片、旧版本查找成本等取舍。[A9]

SonnetDB 已按字段和时间处理 block，并有 `DecodeBlockRange`；部分缓存路径会先解码完整 block，再返回所需范围。[S6][S7] 值得继续做的是贯穿存储→执行→传输→SDK 的批量读取：少读无关字段、少解码无关行、少构造 `object[]`，同时保持缓存和租约安全。是否受益应以真实读取字节、解码字节、分配、首批返回和 P99 判断，不能只看最终返回行数。

这直接对应既有 V45-X01、M46-S05/S07，无需另建一套查询引擎。先解决有界流式和类型合同，再评估零拷贝等优化；保持 Safe-only。

### 3.4 用工作负载决定压缩与数据块大小

SonnetDB 并非没有时序压缩：V2 已有浮点 XOR、Boolean RLE、字符串字典，时间戳也有 delta-of-delta。但 `SegmentWriterOptions` 默认编码仍为 `None`，V2 Int64 仍是原始 8B；“代码里有压缩”与“默认路径已取得收益”必须分开。[S5][S6]

应沿 M46-S01/S02/S04/S08/S09 比较 raw 和显式编码：稳定设备指标、高熵浮点、递增计数器、稀疏宽数据、字典高基数、迟到修订。衡量压缩率之外的写入 CPU、窄范围解码、恢复时间、临时空间和 compaction 尾延迟。ArcticDB 对小追加造成碎片的说明尤其值得借鉴；我们也不宜只通过做大块获得漂亮压缩率。[A9][S13]

### 3.5 把分发和示例当作产品功能

ArcticDB 将安装、创建库、DataFrame 写读放在一个短流程中，并公布平台二进制可用性。[A3][A6] SonnetDB Python 文档目前仍要求先构建/准备 Native 库，开发者入口存在明显摩擦。[S1]

值得交付一个干净环境可复现的完整示例和明确的平台包矩阵：安装→读写→分析→保存结果→关闭重开。优先展示 SonnetDB 的组合价值，例如设备时序与业务表关联，而不是仅复制金融行情演示。第一条真实旅程的稳定性，比继续增加连接器名字更有价值。

## 4. 建议先后顺序

以下是分析建议，不是新增版本承诺、GitHub issue 或已经执行的实现计划。

| 顺序 | 建议工作 | 复用/路线归属 | 完成证据 |
|---|---|---|---|
| P0 | 冻结一条 Python 工业分析旅程，做有界 DataFrame 读写与类型合同；基础包分发同步核验 | 现有 Python/C ABI、M36、M44-A07；需新增有限适配层 | 干净环境、真实库、类型精确往返、取消/失败/重开；不以 mock 代替。 |
| P0 | 打通该旅程所需的存储→执行→传输→Python 批路径 | V45-X01/X02、M42、M46-S05/S07 | 数据量上升时的峰值 RSS、首批延迟、实际 I/O、总耗时及取消释放；阻塞算子边界另列。 |
| P1 | 选定一个可复现分析数据集，复用 generation 和来源 manifest；评估 measurement 历史修订合同 | M44-A01/A06、已有 generation/CDC/恢复原语 | 更新前后版本正确、旧版本重开可读、schema/模型绑定、保留/清理、活动租约保护。范围不足时显式拒绝。 |
| P1 | 完成工作负载驱动的编码/范围读取对照 | 已有 M46 工作包 | 同机、同数据、同耐久性，raw/编码与宽/窄查询都测，报告收益与代价。 |
| P2，需求触发 | 外部对象存储上的只读冷数据/不可变数据集试验 | 需单独架构决策，不能由内置对象存储推出 | 固定数据集、版本和对象校验、缓存/请求费用、断网/权限/删除失败、局部恢复。通过后才考虑扩展写路径。 |

不建议因这篇文章立即重写为 C++、替换持久格式、建设完整分片集群，或降低现有事务和并发合同以换取表面一致。也不能直接把 SonnetDB S3 风格接口认定为 ArcticDB 已兼容的后端。[S9][S14]

## 5. 性能比较该如何做

本次同条件性能、ArcticDB 实跑、SonnetDB 回归测试均为 `NOT_RUN`。当前无法给出谁更快及快多少倍。

若后续安排对照，应先冻结两边版本、机器、内存上限、数据和耐久性；避免一边测客户端内存计算、另一边把远程网络与服务开销全算进去。原始 Parquet/Pandas 可作为简单分析基线，不能把它当作已提供数据库版本与并发语义。

最小对照集合：窄时间/窄列读取、全量批扫、按设备/时间聚合、顺序追加、迟到修订、历史版本读取、多读者加单写者。行数先以能完成正确性检查的小规模试跑，再按明确资源预算扩大；稀疏率、字段数、symbol/measurement 数及冷/热缓存分别报告。

记录 P50/P95/P99、首批时间、吞吐、峰值 RSS、分配/GC、磁盘/网络字节、存储量，以及恢复时间；对象存储额外记录 GET/LIST/PUT 数量和费用口径。跨版本删除、并发写和崩溃恢复的语义不同则拆表比较。不得引用已撤回的 SonnetDB 系统性能数字作为对照基线。[S12]

## 6. 证据和限制

本轮取得文章及 10 份官方文档/仓库文本，来源、文本哈希和核查边界见 [来源清单](../../artifacts/arcticdb-comparison-20261016/sources.json)。官方 `latest` / `master` 为可变入口；保存的是本次文本快照，不是锁定发行版。GitHub commit API 直接及代理请求均受限，本轮没有取得 ArcticDB 上游 commit，未猜测补齐。

没有修改生产代码、路线图或发布配置，没有构建、安装、提交、推送或部署。既有脏工作树保持；报告及 HANDOFF 本次追加留待用户后续决定是否纳入提交。源码/测试文件阅读仅证明实现与覆盖意图，不证明本轮执行通过。

### ArcticDB 官方来源与文章

| 编号 | 原始入口 | 本地核查文本 |
|---|---|---|
| A1 | `https://mp.weixin.qq.com/s/O9zVRKSywcHV2aAVNqMgbQ` | [文章](../../artifacts/arcticdb-comparison-20261016/wechat-article.txt) |
| A2 | `https://docs.arcticdb.io/latest/technical/architecture/` | [架构](../../artifacts/arcticdb-comparison-20261016/architecture.txt) |
| A3 | `https://docs.arcticdb.io/latest/` | [介绍与读写、schema、版本](../../artifacts/arcticdb-comparison-20261016/introduction.txt) |
| A4 | `https://docs.arcticdb.io/latest/faq/` | [FAQ：定位、并发、数据类型](../../artifacts/arcticdb-comparison-20261016/faqs.txt) |
| A5 | `https://raw.githubusercontent.com/man-group/ArcticDB/master/LICENSE.txt` | [许可正文](../../artifacts/arcticdb-comparison-20261016/license.txt) |
| A6 | `https://raw.githubusercontent.com/man-group/ArcticDB/master/README.md` | [README：规模、平台和商用表述](../../artifacts/arcticdb-comparison-20261016/arcticdb-readme.md) |
| A7 | `https://docs.arcticdb.io/latest/licensing/` | [商业 FAQ](../../artifacts/arcticdb-comparison-20261016/licensing-faq.txt) |
| A8 | `https://docs.arcticdb.io/latest/tutorials/snapshots/` | [命名快照](../../artifacts/arcticdb-comparison-20261016/snapshots.txt) |
| A9 | `https://docs.arcticdb.io/latest/technical/on_disk_storage/` | [四层存储与碎片取舍](../../artifacts/arcticdb-comparison-20261016/on-disk-storage.txt) |
| A10 | `https://docs.arcticdb.io/latest/tutorials/parallel_writes/` | [并行 staged 写](../../artifacts/arcticdb-comparison-20261016/parallel-writes.txt) |
| A11 | `https://docs.arcticdb.io/latest/lib_config/` | [library 配置](../../artifacts/arcticdb-comparison-20261016/library-config.txt) |

### SonnetDB 证据定位

- S1：[Python README](../../connectors/python/README.md)，[Python 实现](../../connectors/python/sonnetdb/__init__.py)：`Connection.execute`、`Result.fetchmany/fetchall`、`Cursor.execute`。
- S2：[generation 资源合同](../../src/SonnetDB.Core/Generations/DatabaseGenerationContracts.cs)，[管理器](../../src/SonnetDB.Core/Generations/DatabaseGenerationManager.cs)：`Publish`、`Acquire`、`List`、retired 清理。
- S3：[schema 策略](../../src/SonnetDB.Core/Engine/MeasurementSchemaPolicy.cs)，[Tsdb](../../src/SonnetDB.Core/Engine/Tsdb.cs)：策略接线、列增长及类型提升预检。
- S4：[查询执行器](../../src/SonnetDB.Core/Sql/Execution/SelectExecutor.cs)，[稀疏字段测试](../../tests/SonnetDB.Core.Tests/Sql/SqlExecutorSelectTests.cs)，[TAG 分组测试](../../tests/SonnetDB.Core.Tests/Sql/SqlExecutorMeasurementGroupByTests.cs)。
- S5：[编码默认值](../../src/SonnetDB.Core/Storage/Segments/SegmentWriterOptions.cs)。
- S6：[V2 编码](../../src/SonnetDB.Core/Storage/Segments/ValuePayloadCodecV2.cs)，[块解码](../../src/SonnetDB.Core/Storage/Segments/BlockDecoder.cs)。
- S7：[段读取](../../src/SonnetDB.Core/Storage/Segments/SegmentReader.cs)，[段写入](../../src/SonnetDB.Core/Storage/Segments/SegmentWriter.cs)。
- S8：[M42 SQL 结果边界](../benchmarks/m42-sql-result-bounds.md)，[M33 下推范围](../benchmarks/m33-aggregate-execution-pushdown.md)：本轮只引用已声明的路径及边界，不重新背书历史速度数字。
- S9：[对象存储](../../src/SonnetDB.Core/ObjectStorage/SndbObjectStore.cs)：本地 `objects` 内容根、版本化对象读写。
- S10：[关系读快照](../../src/SonnetDB.Core/Tables/TableReadSnapshot.cs)，[segment 租约快照](../../src/SonnetDB.Core/Engine/SegmentManagerSnapshot.cs)。
- S11：[备份说明](../backup-restore.md)，[备份实现](../../src/SonnetDB.Core/Backup/BackupService.cs)。
- S12：[系统性能报告撤回说明](../benchmarks/system-performance-20260901.md)。
- S13：[M46 存储性能设计](../design/sonnetdb-45-storage-performance.md)，[AI 应用设计](../design/sonnetdb-45-ai-applications.md)。
- S14：[ROADMAP](../../ROADMAP.md)，[README](../../README.md)，[MIT LICENSE](../../LICENSE)。

README 开头仍显示 Workbench 旧候选未发布文字，而当前 ROADMAP 已记录候选 12 发布；本报告不以旧 README 片段判断最新发布状态，也不扩展此次研究为发布状态修订任务。
