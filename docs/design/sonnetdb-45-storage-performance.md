# SonnetDB 4.5 — 存储编码与执行成本优化（M46）

规划基线：2026-10-03；对应根 [ROADMAP](../../ROADMAP.md) 的 M46-S01~S09，状态均为 `📋 planned`。这些 ID 是规划工作包，不是已创建的 issue 或已完成的 PR。本文件是设计与验收计划，不包含本轮运行的性能结果，也不宣布格式、默认配置或包版本已变更。

目标是改善 SonnetDB 自身的磁盘成本、CPU、读写放大、分配与尾延迟，同时保留九模型、嵌入式部署、Safe-only、Core 零第三方运行时依赖和 Native AOT 合同。参考其它产品的机制之后独立设计，交付以现有入口上的真实收益与正确性为准，不以编码算法数量为准。

## 1. 已有能力与必须保留的边界

| 本地事实 | 证据入口 | 对 4.5 规划的影响 |
|---|---|---|
| `SegmentWriterOptions.TimestampEncoding`、`ValueEncoding` 默认均为 `None` | [SegmentWriterOptions](../../src/SonnetDB.Core/Storage/Segments/SegmentWriterOptions.cs) | 现有压缩实现不能写成默认已开启；首先同时测默认 raw 和显式编码，再决定是否推出或默认启用 auto。 |
| 时间戳已有 delta-of-delta + zigzag varint | [TimestampCodec](../../src/SonnetDB.Core/Storage/Segments/TimestampCodec.cs) | 深化极值合同、适用性选择及窄范围读取，不重做已有算法。 |
| V2 值编码已有简化 Gorilla XOR、Boolean RLE、String 字典；Int64 仍为 raw 8B LE | [ValuePayloadCodecV2](../../src/SonnetDB.Core/Storage/Segments/ValuePayloadCodecV2.cs) | Int64 是首个清晰增量；其余类型先处理高熵回退和成本，再选择必要改进。 |
| writer 按选项及类型选择 V1/V2，复合 VECTOR/GEOPOINT 保持相应 raw 路径 | [SegmentWriter](../../src/SonnetDB.Core/Storage/Segments/SegmentWriter.cs) | 不能把所有字段强制塞入同一编码；复合类型与索引有独立正确性、召回和空间语义。 |
| v6 段已有内嵌 HNSW/sketch、mini-footer 和兼容读取逻辑 | [SegmentHeader](../../src/SonnetDB.Core/Storage/Format/SegmentHeader.cs)、[SegmentReader](../../src/SonnetDB.Core/Storage/Segments/SegmentReader.cs) | 新编码与块目录不得破坏已有扩展区、CRC、恢复和旧段读取；不能偷偷改变旧 `DeltaValue` 的 Int64 含义。 |
| 解码缓存按 reader 分配，默认上限 16 MiB | [SegmentReaderOptions](../../src/SonnetDB.Core/Storage/Segments/SegmentReaderOptions.cs)、[SegmentReader](../../src/SonnetDB.Core/Storage/Segments/SegmentReader.cs) | 单 reader 上限不是数据库或进程总预算；reader 数量放大需要接 V45-X02。 |
| HNSW 缓存为静态共享 `SharedVectorIndexCache`，Open 按读选项调用 `TrimToBudget` | [SegmentReader](../../src/SonnetDB.Core/Storage/Segments/SegmentReader.cs) | 它不是每 reader 独占 16 MiB；需明确跨库配置、缓存归属与预算竞争。 |
| mmap 避免 Open 整段进入托管堆，但 `ReadSpan` 仍创建 `byte[]` 并复制 | [SegmentByteSource](../../src/SonnetDB.Core/Storage/Segments/SegmentByteSource.cs) | 不称 zero-copy；按需复制、复用缓冲、范围解码与生命周期值得测量。 |
| KV 已有独立磁盘读取准入，默认并发额度 8 | [KvDiskReadBudget](../../src/SonnetDB.Core/Kv/KvDiskReadBudget.cs)、[KvOptions](../../src/SonnetDB.Core/Kv/KvOptions.cs) | 复用既有租约与取消，统一治理归 V45-X02；不另造一个存储资源管理器。 |
| Int64 的块 min/max 转为 double，超过 ±2^53 存在表示精度风险 | [SegmentWriter](../../src/SonnetDB.Core/Storage/Segments/SegmentWriter.cs) | 必须核对查询比较、剪枝与下推的一致性；本轮未运行错误结果复现，不将其认定为已证实 bug。 |
| WAL 已有 OS flush/fsync 与 group-commit 的显式配置 | [TsdbOptions](../../src/SonnetDB.Core/Engine/TsdbOptions.cs) | 存储比较必须固定相同持久性，不以少做 fsync 获得虚假的编码或吞吐提升。 |

九模型并非共享同一种段载荷格式。本里程碑首先改善时序/段读取与共用执行成本，其它模型仅在适用入口接入指标和资源合同；不能以段编码 benchmark 代替 KV、文档、对象、MQ、图的完整容量或恢复验收。备份及实例级 MQ 的组合恢复继续归 V45-X04；AI 索引质量归 M35/M44，Graph Beta 的生产证据归 M40。

## 2. 参考机制与取舍

下表区分已经核对的官方机制与需要继续核对的候选。官方产品功能不证明 SonnetDB 同条件性能，也不证明这些机制可在不改格式、类型和依赖边界的情况下直接采用。

| 产品或相关格式 | 学习点 | SonnetDB 的独立改进 | 核查边界 |
|---|---|---|---|
| TDengine | 按列选择编码/压缩，类型和数据形态影响收益 | 显式策略、逐块成本与 raw 回退，记录被选择的编码及实际收益 | 使用主仓官方压缩文档；不移植 AGPL 核心，不把企业功能列为开源现状。 |
| IoTDB | 不同类型/序列适用不同编码，部分编码有精度或整数极值限制 | 完整 Int64 值域、无损浮点、极值失败或 raw 回退；不默认量化 | 使用官方 encoding/compression 文档；不采纳该文档的相关文件格式或交换方向。 |
| ClickHouse | 排序键、类型与 codec 联合决定压缩；同时观察压缩和解压后大小 | 利用现有序列/时间排序，核对每字段磁盘、解码 CPU 和工作集；不因压缩改变用户查询语义 | 本轮取得官方压缩正文；其示例数字仅属于其示例，不能转为本项目指标。 |
| Parquet 编码规范 | 有界字典、高基数回退、RLE/bit-packing、整数 delta，以及编码长度合同 | 只学习编码结构与防膨胀思想，按 SonnetDB block/CRC 独立实现 | 本轮取得规范正文；不引入新持久格式、reader 依赖或全格式兼容承诺。 |
| DuckDB | 嵌入式分析中的列段编码、批量执行和存储版本演进 | 将编码、范围解码与批执行一起评估，避免只改善文件大小 | 本轮 storage 地址返回重定向页；具体 codec/版本细节保持待核，不据此认定可移植实现。 |
| PostgreSQL / SQLite | I/O/WAL/checkpoint 活动与观测开销、WAL 快照及 checkpoint 对前台的影响 | I/O/durability 对齐、checkpoint/WAL 回放成本和观测开销；统计剪枝正确性按本地精确路径独立验证 | 复用总体研究已取得的 PG18 statistics collection/activity 与 SQLite WAL 正文；不从监控统计推断 planner 精度，关系页结构不直接替换段模型。 |
| WiredTiger / MongoDB | compressor 可配置，压缩与运行环境/外部依赖有关 | 分开字段预编码与通用压缩；先优化已有 safe codec，通用 compressor 不作为本版默认依赖 | 本轮取得 WiredTiger compressors 正文；第三方 native 库不进入 Core。 |
| TimescaleDB / QuestDB / InfluxDB | 时序排序、冷热读取、窄范围扫描与列式成本 | 测乱序/迟到、冷热与设备高基数；压缩不能掩盖读放大 | 机制候选，具体版本和实现需在对应实现包再绑定官方正文；不声称已做性能对拍。 |
| Redis | WAL/AOF 类日志重写和持久性成本可观察 | 观察重写临时空间、写暂停、fsync 和故障恢复，不降低已确认写的合同 | 复用官方 persistence 资料；不同持久化模型只作成本参考，不宣称等价。 |
| Qdrant / Neo4j | 向量或图工作集与后台优化影响前台延迟 | 把 ANN、图、全文、AI 和存储后台的竞争纳入 V45-X02/V45-X06 | 资源测量维度候选；不从磁盘压缩率推断召回、图吞吐或它们的缓存实现。 |

存储算法可优先参考公开规范与独立实现。采用任何外部代码前，逐组件核查许可证与 NOTICE；公开描述不等于代码可直接并入 MIT 仓库。

## 3. M46 工作包与依赖

九包均为 4.5 必选。P0 是基础合同与先行工作；P1 是本版深化，不表示可以在不说明残余的情况下省略。每包按单一职责细拆 PR，完成状态分别记实现、回归、兼容与目标机证据。

| ID | 范围 | 首要依赖 | 必须交付 |
|---|---|---|---|
| M46-S01 | P0 | V45-X06 | 语料/策略/版本基线、成本模型、收益与回归门槛；默认变更决策。 |
| M46-S02 | P0 | S01/S09 | 无损 Int64 候选编码、溢出合同、raw 回退与版本化格式。 |
| M46-S03 | P0 | S01/S09 | 已有浮点/Boolean 编码的无损合同、高熵回退及必要优化。 |
| M46-S04 | P1 | S01/S09、V45-X02 | 有界字典与稀疏表示，区分缺失/NULL/默认值；不截断数据。 |
| M46-S05 | P1 | S01~S04 的适用编码、S09、V45-X01 | 分块/检查点与范围解码，端到端读放大成本。 |
| M46-S06 | P0 | S01/S09、M45 类型合同 | 统计跳读的精度 guard 与关闭快路径的差分验证。 |
| M46-S07 | P1 | S01/S05、V45-X01/X02 | 解码与 mmap 分配优化、已有缓存/租约接线和取消释放。 |
| M46-S08 | P1 | S01/S09、V45-X02/X04 | small-segment/WAL/compaction 成本和故障恢复。 |
| M46-S09 | P0 | V45-X06；贯穿其余八包 | 统一语料、格式矩阵、损坏/恢复与完整性能证据。 |

### S01 — 基线、格式版本与编码策略

冻结默认 raw、显式现有编码和新候选三条基线。记录每字段类型、采样间隔、乱序率、重复率、基数、块大小、段数、持久性和索引配置；测量 payload 之外的 header、字典、索引、sketch、HNSW 和临时文件成本。

选择器先评估候选编码的完整成本，不能只看少量样本的压缩比。抽样仅用于淘汰明显不适合的候选；写出前用有界测量或精确尺寸验证其 header/字典/检查点等总成本，避免高熵膨胀。选择耗时、扫描次数和临时分配必须计入写成本。

显式编码需说明是“要求该编码”还是“优先该编码、允许 raw 回退”，并保持已有选项的含义；引入 auto 应是明确的新策略。新 reader 从文件元数据解码，不能根据当前选项猜测旧块编码。默认 `None` 只在 S09 的代表语料、故障及回归通过后独立决定是否改为 auto；证据不足则保持原默认，同时交付可解释的显式策略及报告。

**兼容与验收：** 不在 S01 偷改 V2 标志含义；配置与序列化保持 AOT。收益门槛是选择/写入额外 CPU 与分配被计入后仍满足冻结预算，最差语料的编码体积膨胀受控，默认决策可回溯到原始样本，而不是依赖平均压缩比。

### S02 — Int64 编码：完整值域优先

在 raw 8B 基础上比较常量/RLE、zigzag varint、delta、frame-of-reference/bit packing 等候选。固定值、平稳递增、计数器重置、小振荡、随机 64 位及离群点分别评估；不要求所有候选最终上线。先交付最小、易证明且在目标负载有收益的组合，选择逻辑与 format ID 保持可扩展。

delta 和 frame-of-reference 的差值可能超出 Int64 范围。设计必须明确使用可证明可逆的位模式算法或检测溢出后回退 raw；不能依赖未声明的 unchecked 行为、绝对值 `long.MinValue` 或 zigzag 结果仍为正 `long` 的假设。时间戳 DoD 的相邻差值和二阶差值也列入同一极值差分，但不无版本修改旧编码合同。

**兼容与验收：** 新 Int64 载荷有明确格式版本/codec ID/长度合同，旧 `DeltaValue` 对 Int64 的 raw 含义保持。测试空、单值、`long.MinValue/MaxValue`、跨极值跃迁、重复/乱序、counter reset、分块边界，逐值与 raw 精确相等。收益门槛冻结每类代表语料的字节节约及写/读 CPU 预算；随机语料须稳定回退，不以低幅值合成序列代替结论。

### S03 — Float64 / Boolean 无损与高熵回退

复用现有 Gorilla XOR 和 Boolean RLE。浮点比较以落盘边界的 IEEE 位模式为准：保留正负零、Infinity、不同 NaN payload；若 API 先行规范化了表示，先公开其合同，编码器不得再隐式增加规范化。相近值的数值距离小不保证 XOR 位差小，测恒定、缓慢漂移、指数变化、随机位模式和混合异常值。

Boolean 对长 run、交替位和随机位分别比较 RLE、raw 与可版本化的 bit-packed 候选。高熵回退需计算头部、长度与校验成本，极小块不因选择器和封装而放大。可重复的浮点解码状态与范围 checkpoint 必须采用原始位表示。

**兼容与验收：** 无默认有损量化/精度截断；旧 Gorilla/RLE 读取不变。按位 round-trip、非法 run/varint/count、截断、范围越界和长度溢出均需覆盖。收益门槛同时约束磁盘、decode CPU、分配、冷读及 P99；现有算法已适合时，交付安全回退与证据即可，不为算法数量替换它。

### S04 — 有界字典、稀疏和大值

字典设条目数、UTF-8 总字节和峰值构建内存上限；上限接 V45-X02 的准入/归属。比较总字典与索引开销，达到高基数或大值阈值时退出到 raw 或另一有界表示。过大值走已定义的数据合同，拒绝或流式处理均须显式；不能以截断、漏值或把不同 Unicode 表示视为相同字符串降低成本。

稀疏优化先说明缺失、SQL NULL、空字符串、零值及 false 的区别，再测 bitmap、稀疏位置列表和原路径。仅在现有类型/块布局真正表示这些状态的入口应用；不额外捏造跨模型 NULL 合同。判断 presence 的元数据与载荷独立校验，不能从压缩后的 run 推断字段不存在。

**兼容与验收：** 新表示版本化，旧字符串字典及未修改模型读取保持。包含低/高基数、多语言、emoji、组合字符、大值、缺失/NULL 混合及预算耗尽。收益门槛为全部编码字节与构建工作集下降且语义不变；高基数不会无界膨胀，错误/取消后租约归还。

### S05 — 分块自适应与范围解码

现有 range decode 是起点，不把 API 名称作为只解码请求区间的证据。测量从块首重放的前缀、实际读取字节与输出值数；对 delta/Gorilla/RLE/字典逐一说明重建状态成本。候选包括更小独立块、块内 restart checkpoint 和可定位子块目录，按窄读、整段扫描与冷热读的收益择优。

块大小与 checkpoint 间距有明确候选集合；不能为每次查询无界搜索最佳值。加入 count、offset、编码、统计精度标识与必要 CRC，验证目录长度、位置和父块范围。索引/检查点新增字节、缓存 footprint、全扫描 CPU 及 compaction 成本全部进入成本模型。

**兼容与验收：** 布局改变先冻结版本升级与旧读方案；新读取不能绕过 Block CRC 或降低索引校验。S09 分别测 1 个值、窄时间范围、跨 checkpoint/块边界、空结果与全扫描。收益门槛是实际 I/O、decode CPU 或分配至少一项达到冻结改善目标，同时全扫描与格式开销不超过预设回归预算；仅切小响应帧不算范围解码完成。

### S06 — 统计精度 guard 与差分

逐项记录统计的类型、精确/近似标识、有效值数、缺失/NULL/NaN 处理以及作用范围。Int64 转 double 的 ±2^53 边界是风险点；先以绕过剪枝和统计下推的逐值参考执行对拍 SQL/API 查询，覆盖比较符、开闭区间、聚合、排序与参数类型。若一条路径本身采用 double 语义，不能把它当作精确 Int64 对拍的唯一基准。

旧元数据若不足以安全排除一个块，优先关闭该精度敏感剪枝并读取验证。新格式可评估 typed min/max 或保守边界，但需在版本和校验中明确。sketch/近似统计只按声明误差用于适用聚合，不假装精确值；索引、数据统计和 MV/rollup 的语义同 M45 保持一致。

**兼容与验收：** 不宣称本轮已复现错结果。覆盖 `2^53±1`、对应负数、Int64 极值、混合整数/浮点、NaN/Infinity/±0、全空块、旧段和禁用快路径。收益门槛首先是结果合同精确成立；必要关闭优化允许付出公开的性能代价，不通过放宽比较语义维持速度。新统计只有在无假阴性且目标剪枝比例/查询成本可复算后启用。

### S07 — 解码分配、缓存和读取接线

首先用分配/heap/I/O 证据定位重复数组、缓存 miss、读取复制及 materialization；然后评估 `ReadInto`、调用方 span、池化有界缓冲、解码至目标批和生命周期租约。Safe-only 保持，不以 unsafe mmap 指针达成所谓 zero-copy。池化内存不得在归还后继续被 cache、异步帧或查询结果持有。

沿用既有 decode cache、共享 HNSW cache 和 KV read budget，接 V45-X02 的数据库/进程总预算、分类归属与公平准入。处理多个 reader 的额度放大、不同库打开时的共享 HNSW 配置竞争、索引/解码缓存占用以及前台/AI/compaction 竞争；该工作包不创建平行全局管理器。

**兼容与验收：** 实际产品 Open→查询→帧/SDK 路径使用新合同；失败/取消/关闭/淘汰后无悬空引用或残留租约。测多段、多库、热冷切换、缓存禁用、长游标、预算耗尽及取消。收益门槛为 alloc/GC、峰值工作集或 P99 的冻结改善；缓存 hit 提升不能掩盖总体 heap 或队列增长。全链路有界与首行继续由 V45-X01 验收。

### S08 — small-segment、WAL 和 compaction

先剖分 flush 频率、小段元数据/index/缓存成本、WAL 字节、manifest 更新、fsync、临时空间和 compaction 读写，再调整现有 flush/合并/调度。考虑段数、写入批大小、迟到写和字段高基数，防止“大段节省元数据”换来更高恢复时间、范围解码或前台尾延迟。

固定同等 durability、数据可见性与已确认写合同。group-commit、checkpoint 或后台合并可改变调度，不能未经合同变更延迟确认所需持久化。compaction 拷贝/重建新的编码、HNSW/sketch 时公开对应成本；复用 manifest/generation 的发布和失败恢复，不发明跨模型原子提交。

**兼容与验收：** 故障点包括临时段写中、fsync、rename、manifest/checkpoint、compaction 发布、磁盘满和取消；保留未损坏原段并验证 WAL 重放、不丢已确认数据。收益门槛为持续混合负载的写放大、临时空间、fsync/排队、P95/P99 与恢复目标共同通过；只测批写吞吐不能关闭此包。复制/主备归 V45-X07 的条件架构项，不扩大为本版必选 HA。

### S09 — 统一语料、兼容和收益证据

与 V45-X06 使用同一个候选 commit、机器、软件版本及语料清单。报告提供配置、原始样本、失败样本、查询计划/编码分布、文件尺寸分解、校验与数据对账。fixture/合成数据用于边界合同；最终改善至少覆盖真实或明确可代表目标应用的数据，不由合成模式压缩率外推生产收益。

**兼容与验收：** 维护旧 writer→新 reader、旧段→新 compaction、新 writer→新 reader、旧进程→新格式的拒绝、回退/回滚与混合段 reopen 矩阵。最后一项默认应在写入前拒绝不支持的格式，不能让旧 writer 擅自覆盖/追加未知语义；如果提供受控兼容模式，需单独证实其支持范围。迁移要说明峰值磁盘、取消、故障与数据回读；没有迁移时明确拒绝和备份恢复方案。

## 4. 成本指标与门槛冻结

不预填提升倍数或百分比。实现前在 S01/V45-X06 为每类负载填写如下门槛，记录统计方法、重复次数、波动、基线与允许误差；未校准保持 `NOT_READY`。正确性/无损/兼容门槛不因性能收益放宽。

| 门槛参数 | 定义与判定 |
|---|---|
| `MinStorageSaving` | 对同样逻辑数据、同索引及持久性，总持久字节的最低节约，包含 header/字典/checkpoint/扩展区，不只 payload。 |
| `MaxEncodedExpansion` | 高熵与极小块相对 raw 的最大膨胀；超阈值回退，选择器本身的 CPU/分配同测。 |
| `MaxEncodeCpuRegression` / `MaxDecodeCpuRegression` | CPU 时间与服务吞吐的允许回归，区分冷 I/O 与热 CPU 场景。 |
| `MaxP95Regression` / `MaxP99Regression` | 混合前后台负载的尾延迟回归；延迟需含排队、准入和 fsync，不只执行器内部计时。 |
| `MaxReadAmplification` | 实际读取字节与查询必要逻辑载荷之比，另记解码值数/输出值数；空结果采用绝对字节/工作量。 |
| `MaxWriteAmplification` | WAL、段、manifest、索引和 compaction 的总落盘字节/逻辑写入字节；明确临时文件与文件系统测量口径。 |
| `MaxPeakWorkingSet` / `MaxAlloc` | 数据库/进程预算及分配目标，分开托管 heap、租用数组、mmap/OS cache、共享索引与临时目录。 |
| `MaxRecoveryTime` / `MaxTemporaryDisk` | 冷 reopen、WAL replay、故障恢复耗时与迁移/compaction 临时空间上限；与磁盘满策略一起冻结。 |

默认策略升级必须在代表负载权重被冻结后评估整体收益和每类最差回归。符合空间目标但 CPU/尾延迟失败的编码，可以保留为明确限定的显式策略；不能据此默认开启。无法证明收益的候选可删除，其失败样本和理由保留，不影响必选的无损、回退与证据合同。

## 5. 统一测试和真实负载矩阵

| 层级 | 必测集合 | 证据意义 |
|---|---|---|
| 值与编码 | 空/单值、完整整数值域、溢出差分、随机/重复/有序/乱序、浮点位模式、Boolean 长 run/交替、Unicode、高基数、大值 | 逐值/逐位无损，不等于目标机性能。 |
| 范围读取 | 单点、空范围、首尾值、跨块/检查点、稀疏字段、多字段投影、冷读/热读 | 证明输入工作量，而非仅输出分页。 |
| 文件与损坏 | CRC 错误、断尾、截断 varint/run、非法 codec、count/offset/length 溢出、目录越界、损坏扩展区 | 显式拒绝损坏，不读出伪数据、不无界分配。 |
| 格式与恢复 | 所支持旧格式、混合段、v6 HNSW/sketch、未完成迁移、WAL/flush/compaction 故障、磁盘满 | 证明兼容和已确认写的恢复范围，不宣称实例级 MQ 被单库备份覆盖。 |
| 工业/通用时序 | 稳定周期传感器、不规则/迟到观测、多设备高基数、状态跳变、计数器与故障样本 | 同时测 TAG+时间聚合、点查、窄读、全扫描、rollup 和持续写。 |
| 关系/业务分析 | 整数金额/计数、大键、NULL、文本维度、排序/聚合/批扫描 | 只在适用存储路径测编码，类型与 SQL 语义由 M45 保证。 |
| 九模型/AI 竞争 | KV 热状态、文档/全文摄取、ANN 过滤查询、对象读取、MQ backlog、图有界遍历、AI 检索/推理与 compaction 并发 | 观察总资源、公平性与尾延迟；各模型质量和恢复沿用对应门禁。 |
| 发布/平台 | 目标 x64/ARM64、Release、适用 Native AOT/trim、真实 Server/SDK、长期混合负载 | 同候选原始报告；本机合成结果不升级为容量或生产结论。 |

序列排序不得改变相同时间戳冲突处理、字段拼写或可见性规则。测试分别保存 raw、显式旧编码、新候选和禁用快路径结果；在查询侧同时比较类型、列元数据、顺序及数据值。

## 6. 实施顺序与完成判定

1. S01/S09 与 V45-X06 冻结基线、阈值、支持矩阵及版本设计；优先补 S06 的精度差分与保守 guard。
2. S02/S03 先在 codec 层证明无损和高熵回退，再接真实 writer/reader；S04 的预算与 presence 语义同时冻结。
3. S05/S07 接 V45-X01/X02，测读取字节、decode 工作量、分配和跨库缓存竞争。
4. S08 在同等 durability 下跑混合写/读/AI 与 compaction 故障，接 V45-X04 对账恢复。
5. S09 汇总同一候选的正确性、兼容、冷读、CPU、P95/P99、alloc/GC、放大和恢复，才决定默认 auto 及版本发布。

完成要求为九包必选合同和入口均交付，兼容/损坏矩阵通过，适用 AOT 无相关警告，收益及代价公开可复算。只完成编码函数、单次 benchmark、可选策略或缓存 hit 都不能将 M46 标为完成。未取得目标机或真实语料结果保留对应待验收状态；本轮只有规划文档，不写入功能完成条目。

## 7. 官方参考与证据边界

资料查阅日期为 2026-10-03。实施时再绑定具体版本；主分支/latest 文档可能变化。本轮没有安装依赖、改源码、执行编码回归或数据库性能对拍。

- [TDengine 官方压缩文档（主仓）](https://github.com/taosdata/TDengine/blob/main/docs/en/05-tdengine-sql/03-data-write/03-compress.md)：复用总体研究已抓取正文。
- [IoTDB 编码与压缩](https://iotdb.apache.org/UserGuide/latest-Table/Technical-Insider/Encoding-and-Compression.html)：复用总体研究已抓取正文；只取类型/极值/精度机制。
- [ClickHouse Compression in ClickHouse](https://clickhouse.com/docs/data-compression/compression-in-clickhouse)：本专题抓取正文，涉及排序、类型与压缩/解压字节。
- [Parquet Encodings](https://parquet.apache.org/docs/file-format/data-pages/encodings/)：本专题抓取正文，涉及字典回退与 RLE/bit packing；仅参考编码规范。
- [WiredTiger Compressors](https://source.wiredtiger.com/develop/compression.html)：本专题抓取正文，页面为 12.0.0，涉及 compressor 配置和依赖。
- [PostgreSQL Monitoring Database Activity](https://www.postgresql.org/docs/current/monitoring-stats.html)、[SQLite Write-Ahead Logging](https://www.sqlite.org/wal.html)、[Redis Persistence](https://redis.io/docs/latest/operate/oss_and_stack/management/persistence/)：复用总体研究取得的官方正文。PG18 正文支持 I/O/WAL/checkpoint 观测与开销；SQLite 正文支持快照/checkpoint、单主机边界及 ATTACH 跨数据库事务的非原子边界；Redis 正文支持 RDB/AOF/fsync 取舍。均不作为本项目性能结论。
- [DuckDB Storage](https://duckdb.org/docs/stable/internals/storage.html)：本专题仅取得重定向页，存储细节待核；不作为已确认功能或性能断言。

其它产品在机制候选层使用；对应工作包实现前补官方来源、版本与许可。竞争产品的 benchmark、商业能力与默认配置必须分开，不能将其它版本的数字抄为 SonnetDB 的门槛或效果。
