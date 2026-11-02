# M50：时序时间戳精度与无损时间链路

规划日期：2026-11-02（Asia/Shanghai）。源码核查基线：`7255f0104a1176fd15b883814222dd0f23591d8d`；本地可见 `origin/main` 为 `3c40730a35140a24589e606310bcf1e2dae88ec3`，两者之间没有本专题路线文件变更。上述为规划阶段记录。提交准备阶段已读取最新主干，候选基于 `979a766b7a35594040cc58bff79911e45b79f76c`；研究基线不替代当前主干的实现验收。

状态：**📋 planned，尚未实施**。M50-T01～T30 为总路线必选，T31/T32 为条件项；这些是规划 ID，不代表已创建 GitHub Issue/PR。本文完成仅表示设计与任务拆解入库，不能将其写成存储格式、协议、性能、实机或发行已交付。

根入口：[ROADMAP M50](../../ROADMAP.md#milestone-50)。范围索引：[总里程碑](../roadmap-total-milestone.md)。任务的实际状态以根路线表为准；本文规定合同、依赖、交付物和验收方法。

## 1. 目标、规模与版本边界

目标是让时序 measurement 的时间轴真正支持 Unix 毫秒、微秒和纳秒，按数据库创建时选择并持久化；默认毫秒。输入单位与存储精度分开声明，存储、查询、传输、恢复和工具展示能够保留同一个时刻。

这是中大型跨层改造。整数容器仍可使用 8-byte Int64，难点是已有代码把整数默认解释成毫秒，以及升级期间新旧 reader/writer、协议与 SDK 对单位的共同约束。不能靠给 `TimePrecision` 增加一个值、全局替换 `1000`，或把输入先换算成毫秒完成。

本轮对 `src/`、`connectors/`、`embedded/`、`web/src/` 与 VS Code 扩展中的六类毫秒相关关键词做有界静态检索，共命中 69 个文件。其中包括认证时间、关系日期、文档 TTL 等范围外代码；这不是待改文件数量，也不是完整影响分析。T01 必须按调用链重新分类。

| 交付范围 | 初步工作量估计 | 估计口径 |
|---|---|---|
| 嵌入式最小闭环：元数据、WAL/Segment、范围查询、重开及旧库拒绝 | 10～15 工程人日 | 包含必要测试，不包含全部网络、语言、迁移与产品界面 |
| 可正式交付的完整首版：协议、承诺消费者、迁移/恢复、工具与门禁 | 40～70 工程人日 | 累计工作量，包含前项；不是日历工期，不是排期承诺 |
| 固定硬件、168 小时观察、外部工具链及发行等待 | 单独安排 | 可部分重叠；不能折算成实现已完成 |

估计置信度为低到中，T01 完成影响清单、负基线和首批矩阵后重估。30 个任务不等于 30 天，也不要求一次 PR 完成；共享存储和协议改动应逐包集成。

M50 是独立增量，不自动加入现有 4.5 正式版、M47 已发布预览或 M49 已冻结批次的必选集合。目标版本、兼容政策与首批语言/平台由 T01 冻结。允许分批发行：首批建议 Core/Server 与 .NET/C 的 Windows/Linux x64；其他语言、平台和宿主未交付时，不得宣称完整 M50 完成，也不得让旧客户端静默损失高精度数据。

## 2. 已有能力与本次新增边界

| 当前实现入口 | 已核对事实 | M50 要补的增量 |
|---|---|---|
| [Point](../../src/SonnetDB.Core/Model/Point.cs)、[DataPoint](../../src/SonnetDB.Core/Model/DataPoint.cs) | public 时间戳合同为非负 Unix 毫秒 | 明确内部库单位与公共接口单位，增加可无损携带高精度时刻的表示 |
| [TimePrecision](../../src/SonnetDB.Core/Ingest/BulkPayloadFormat.cs)、[LP reader](../../src/SonnetDB.Core/Ingest/LineProtocolReader.cs)、[JSON reader](../../src/SonnetDB.Core/Ingest/JsonPointsReader.cs) | 接受 s/ms/us/ns 输入，us/ns 最终整除为毫秒 | 解析原始整数及其单位，交给目标数据库的统一换算边界 |
| [类型化 writer](../../src/SonnetDB.Data/TimeSeries/TimeSeriesWriteModels.cs) | `Precision` 表示输入单位；`Timestamp(DateTimeOffset)` 先生成毫秒 | 消除 builder/writer 的双重缩放风险，独立保存原始时间单位 |
| [TsdbOptions](../../src/SonnetDB.Core/Engine/TsdbOptions.cs)、[Registry](../../src/SonnetDB/Hosting/TsdbRegistry.cs) | 没有数据库持久化的时序精度属性 | 创建、打开、挂载、能力发现及连接上下文的一致精度 |
| [通用 FileHeader](../../src/SonnetDB.Core/Storage/Format/FileHeader.cs) | 是当前目录持久化未使用的预留容器头 | 不能只修改此头；必须保护真实使用的 WAL、Segment、manifest 与 reader/writer |
| [WAL header](../../src/SonnetDB.Core/Storage/Format/WalFileHeader.cs)、[payload](../../src/SonnetDB.Core/Wal/WalPayloadCodec.cs)、[timestamp codec](../../src/SonnetDB.Core/Storage/Segments/TimestampCodec.cs) | 真实目录恢复与时间编码已有实现 | 单位绑定、版本拒绝、整数极值及精度一致的恢复 |
| [TimeRange](../../src/SonnetDB.Core/Query/TimeRange.cs)、[TimeBucket](../../src/SonnetDB.Core/Model/TimeBucket.cs)、[Retention](../../src/SonnetDB.Core/Engine/Retention/RetentionPolicy.cs) | 查询桶、时钟和 TTL 多处按毫秒约定 | 时间坐标与物理时长分离，正确计算范围、桶、速率及过期线 |
| [关系日期合同](../sql-reference.md)、[Frame SQL](../sql-execute-frame.md) | DATETIME 保留毫秒；DATETIME2/DATETIMEOFFSET 已有 100ns 路径 | 显式跨类型转换和精度元数据，不重做已交付的关系日期类型 |
| [M43 窗口](../m43-persistent-windows.md)、[M48 日志](m48-single-node-log-event-analytics.md)、[M49 连接器](m49-connector-analytics-bounded-batches.md) | 已有持久窗口/任务；日志合同与连接器拆分各有归属 | 时间状态和新 SDK 按现有架构接线，不另造任务系统、协议或日志平台 |

本次设置只定义时序 `measurement.time`。关系列日期、MQ 服务端时间、文档/KV TTL、认证有效期、进程超时及音视频相对位置分别沿用其模型语义；需要与时序时间互转时显式换算。既有 M46 压缩实现和 M49 原生 HTTP/2 Frame 路线继续复用。

## 3. 必须先冻结的设计决策

### 3.1 库单位、纪元和范围

- 存储精度为 `ms/us/ns`，默认 `ms`；`s` 作为输入、查询时长及降采样单位，不在本轮增加秒级物理存储档。
- 精度属于数据库时序域，数据库内所有 measurement 使用同一单位。内部可用 Int64 原值与不可变数据库时间上下文，避免给每个点重复存单位；公开接口和序列化边界必须明确单位。
- 纪元使用 `1970-01-01T00:00:00Z`，按 Unix/POSIX 时间处理，不逐项计入闰秒。PTP/TAI、设备启动计数、单调时钟等来源必须显式映射，不能直接当 Unix 纳秒写入。
- 继续保留现有非负写入范围。R1 的 ns 时间轴约为 1970～2262 年；有符号 Int64 的理论纳秒范围约 1677～2262 年，不代表 R1 已支持负时间戳。查询负下界、无界上界及最大值的处理单独定义，不用 `long.MaxValue + 1` 表示上界。
- 精度在创建时固定。`open` 参数与持久化精度冲突应拒绝；不能根据整数位数猜测已存数据单位，不能在有数据后原地切换精度。
- 没有新精度元数据时，仅允许完整识别为已知旧格式的库按 ms 打开；新格式缺元数据、未知版本或组件精度冲突必须失败，不能回退为 ms。

本文中的 `TimestampPrecision`、精确时间戳类型和错误语义名称都是拟议合同。T01 冻结实际 API、配置键、能力位与版本号，本文不提供已经可运行的建库语法。

### 3.2 输入换算与有损策略

```text
原始整数/ISO 时间 + 显式输入单位
                 ↓ 一次精确解析
统一时间换算器：目标库精度、范围、精度损失、策略
                 ↓ 库单位整数
WAL → MemTable → Segment/索引 → 查询与恢复
                 ↓ 带单位的结果合同
Frame/REST/SDK/CLI/Workbench
```

| 换算 | 默认合同 | 必须验证 |
|---|---|---|
| s/ms/us → 更细单位 | checked 乘法或有界宽整数中间值，无损转换 | 最大值、零、乘法溢出、批次失败边界 |
| ns/us → 更粗单位且整除 | 精确除法 | 同一时刻写入不同精度库后可对齐 |
| ns/us → 更粗单位且有余数 | 新精度合同默认拒绝 | 错误指出来源/目标单位，不写入该拒绝项 |
| 显式有损输入或迁移 | R1 只需支持明确的 `Reject` 与 `Truncate`；增加其他舍入模式另评审 | 记录策略、丢失/碰撞统计与原始来源；不靠先截断参数绕过检查 |
| 查询边界跨精度 | 按比较运算计算安全 floor/ceiling，或精确比较；不复用写入截断 | `>`/`>=`/`<`/`<=`、半开范围、空区间、边界不可整除和极值 |
| 混合精度 JOIN/跨库分析 | R1 未实现时明确拒绝；支持时按宽整数或精确商余比较 | 不强制将全部时间转 ns，避免日期范围缩小或溢出 |

旧 ms 库的既有入口继续保留其已发布的输入解释与默认值。旧 LP/JSON 的截断兼容行为不能未经版本说明直接改为报错；T01 冻结 legacy 与新严格入口的识别、迁移告知和弃用策略。新 us/ns 库要求明确的精度合同与能力协商，不能借 legacy 截断路径接收高精度输入。

整数时间不经 Double/Float64；先用整数得到时间差，再按函数需要转为秒等物理量。单位换算只能发生在明确边界一次，不能 writer 先除、Server 再除。

### 3.3 存储格式和旧引擎保护

精度元数据至少包含 schema/version、数据库身份、时序单位和必要 feature/min-reader 信息；精度本身参与校验。具体字段布局与版本由 T01/T03 核对现存版本后分配，不预占当前未知版本号。

只有新建高精度库写入对应新格式。现有 ms 库不因为读取或无关操作被自动改为 us/ns；若兼容工作确需升级 ms 格式，必须显式登记并提供旧版本拒绝与恢复方案。

仅增加旧引擎不会读取的新 JSON 配置不足以保护旧 writer。T03～T06 必须证明真实旧引擎在写 WAL、删段、恢复或 compaction 之前明确拒绝高精度库；对源目录逐文件哈希确认失败打开没有副作用。若现有打开顺序不能做到，先增加实际生效的版本入口，再开放创建高精度库。

数据库声明、WAL、Segment、tombstone、checkpoint、备份 manifest 和持久流状态之间的单位冲突都要报错。任何固定二进制结构布局变更遵守根 AGENTS：顺序布局/Pack=1/little-endian、升级对应格式与 `FileHeader.Version` 规则、旧格式读取/拒绝、CHANGELOG 和迁移说明；不能把未使用的通用头升级当成全部持久化已受保护。

### 3.4 查询、时长和排序

时间坐标属于库单位；`1s`、TTL、轮询、flush 窗口和网络超时是物理时长。现有以 `Ms` 命名的 public 参数不得悄悄改为“库单位”；使用显式新接口/转换或版本化拒绝。

SQL `time` 整数字面量、参数、`now()`、duration、GROUP BY time、窗口 gap-fill、latest-N、稀疏 field 合并、DELETE/tombstone 和 EXPLAIN 必须共享已绑定时间上下文。legacy 查询/返回合同保持 ms；新合同的整数按已声明库单位解释。ns/us 查询不得通过 legacy `DataPoint.Timestamp` 或旧结果编码静默变成 ms。

速率、导数、积分、PID、预测和异常函数逐项登记输入时间单位及物理输出单位。以 ms/us/ns 表示的同一物理过程应得到一致结果；需要除以秒时只能归一一次。桶边界必须精确；R1 对不能整除目标时间格的桶宽可明确拒绝，不能把 `500ns` 解析为 `0ms` 后继续执行。

精度不提供事件唯一性或跨设备全序。同一时间戳的现有合并/覆盖/多源读取行为先固定负基线，再按入口保留；新日志事件采用独立 event_id/source_sequence，不能把尾数加一伪造采样时间。乱序、重复和时钟回拨应可观察。

### 3.5 SDK、浏览器和固定单位协议

- .NET DateTime/DateTimeOffset 只有 100ns ticks；真实纳秒主路径保留整数或专门类型。不能表示的 DateTime 转换默认拒绝或显式有损；ISO 文本保留最多 9 位小数。
- JSON/NDJSON 新时间字段使用明确的整数文本或等价精确结构，并携带单位元数据；浏览器不得经 JavaScript Number、Date.parse 或 Date 的毫秒值完成高精度排序、游标和过滤。
- Frame/Native ABI 为结果及参数定义单位、版本和完整终态；旧客户端在不支持新语义时明确拒绝。新 SDK 遵循 M49 的原生 HTTP/2 路线，不另建旧 SDK 门面或用 REST 回落掩盖 Frame 缺口。
- Influx HTTP、UDP、MQTT/CoAP、JSON、SQL bulk 的现有输入默认值分入口保留。Sparkplug/Prometheus 等固定毫秒来源应精确升尺度；省略时间戳时记录真实时钟来源和分辨率，不伪称生成了任意纳秒准确时刻。
- C/Go/Rust/Java/Python/BASIC 各自提供精确读取与写入。Python datetime 的微秒、.NET ticks、VB6 Double、Pandas dtype/NaT/时区不能被当作任意 ns 的无损容器。
- 能力不足的流计算、旧客户端或工具在高精度库上明确拒绝相关操作；不能先创建可写库，再让后台 retention/流任务按 ms 误删或误算。高精度建库公开入口须在对应闭环通过后开放。

### 3.6 备份、迁移与回退

备份保留原单位和精度声明，restore 不负责偷偷变更单位。需要改变精度时使用一致源快照/停写窗口，创建目标库，有界复制与逐项对账，验证后切换连接。

迁移 checkpoint 绑定来源数据库/代际、快照身份、源/目标精度、策略、上次位置和校验摘要；设定每批点/字节、总预算、重试与墙钟界限并支持取消。降精度前检查余数及转换后相同键的碰撞；不能只比较总点数便宣布无损。迁移中断、重跑、目标已存在、满盘和发布前后崩溃均有确定处理。

数据库其他模型保留各自格式；组合迁移与恢复明确恢复域，不能把实例 `.system/mq` 计入单库备份。切换后如有新确认写入，回退必须先停止新写并对账/转回或恢复相应数据，不能直接指回旧副本丢弃新写后仍宣称安全回滚。

## 4. 与既有里程碑的依赖

| 既有归属 | M50 接线范围 | 边界 |
|---|---|---|
| M36 时序 write/query 与九模型旅程 | 扩展已有 writer、query、schema/preflight 和真实端到端 | 不重做其预算/背压/逐项回执基础 |
| M41/M42/V45-X01/X02 | 统一时间比较、范围裁剪、latest-N、批读取与资源计费 | 加精度不能回到全量物化或无界预取 |
| M43/M45 | 事件时间、窗口/水位、TTL、状态/checkpoint/CDC | T23 仅接单位；没有新增通用任务/流引擎 |
| M46 | 时间编码、restart/range decode、统计边界和完整成本 | 编码不能承担猜测时间单位的职责；压缩收益单独实测 |
| M47 | 共享 typed 时间组件、能力提示、Web/Studio/VS Code 精确输入/显示 | 三宿主分别验收，不能用 Web PASS 代表其它宿主 |
| M48 | L02 时间信封、L10 稳定排序、L21/L22 OTLP/Trace | 纳秒落库不代表 OTLP receiver 或 APM 已实现；由 T31 条件接线 |
| M49 | Protocol 独立程序集、原生 HTTP/2 Frame、语言/embedded 拆分和按批发行 | 先复用本批冻结的契约；不要求先完成整个 M49 再做 M50 |

## 5. 工作包总表

以下 32 项初始状态均为 **📋 planned**。T01～T30 是完整里程碑必选，T31/T32 为条件项。P0 表示关键路径，P1 表示必选深化，P2 为条件候选；P1 不等于可跳过。依赖表示可集成前置；测试夹具和故障样例可提前准备，不能把准备材料标为实现完成。

| ID | 优先级 | 任务 | 前置依赖 |
|---|---|---|---|
| M50-T01 | P0 | 现状复核、时间合同、负基线与首批范围冻结 | 无 |
| M50-T02 | P0 | 精确时间戳/时长表示及换算器 | T01 |
| M50-T03 | P0 | 数据库精度目录、打开顺序与旧引擎防护 | T01/T02 |
| M50-T04 | P0 | WAL 与 replay 的精度绑定 | T03 |
| M50-T05 | P0 | Segment、时间索引及 codec 的精度合同 | T03/T04 |
| M50-T06 | P0 | flush/compaction/tombstone/checkpoint | T04/T05 |
| M50-T07 | P0 | QueryEngine 范围、排序、裁剪与 latest-N | T02/T05/T06 |
| M50-T08 | P0 | SQL time 绑定、参数、分桶与日期互转 | T02/T07 |
| M50-T09 | P1 | 速率/积分/PID/预测等物理时间计算 | T08 |
| M50-T10 | P0 | retention、NowFn 与后台时长 | T02/T03/T06/T07 |
| M50-T11 | P0 | 嵌入式 ms/us/ns 最小完整旅程 | T03～T10 |
| M50-T12 | P0 | Server 建库/挂载/能力及精度元数据 | T03/T11 |
| M50-T13 | P0 | 二进制 Frame 时间参数/结果与协商 | T02/T12 |
| M50-T14 | P0 | REST/SQL/NDJSON 精确时间合同 | T02/T12 |
| M50-T15 | P0 | LP/JSON/BulkValues 统一解析与换算 | T02/T11 |
| M50-T16 | P1 | 设备协议、Influx 兼容、UDP 与固定单位来源 | T12/T13/T14/T15 |
| M50-T17 | P0 | .NET SDK/Data/ADO 的精确时间链路 | T11/T13/T14/T15 |
| M50-T18 | P0 | C/NativeAOT ABI 与原生消费者 | T13/T17 |
| M50-T19 | P1 | Go/Rust 原生远程与 embedded 映射 | T13/T18 |
| M50-T20 | P1 | Java/Python 与可选 DataFrame 映射 | T13/T18 |
| M50-T21 | P1 | VB6/PureBasic 精确时间远程接口 | T13 |
| M50-T22 | P1 | CLI/Web/Studio/VS Code 的精确时间体验 | T12/T13/T14/T17 |
| M50-T23 | P1 | 持久窗口、CDC/桥接与派生状态换代 | T08/T09/T10/T13 |
| M50-T24 | P0 | 备份/restore/inspect 的单位与版本 | T03/T06/T23 |
| M50-T25 | P0 | 精度迁移、续传、对账与回退工具 | T11/T24 |
| M50-T26 | P0 | 黄金语料、新旧/跨精度与 Parity 对拍 | T11/T13/T14/T15/T17 |
| M50-T27 | P0 | 强杀、损坏、满盘与取消故障矩阵 | T06/T23/T24/T25 |
| M50-T28 | P1 | 固定 x64/ARM64 性能、压缩与资源评估 | T26 |
| M50-T29 | P1 | 同候选混合精度长稳与资源回收 | T27/T28；本批适用 T16～T23 |
| M50-T30 | P0 | AOT/包消费者、文档/发行与最终验收 | T24～T29；本批声明矩阵 |
| M50-T31 | P2 条件 | OTLP/Arrow/分析生态的纳秒保真接入 | 对应基础与外部接收能力已冻结 |
| M50-T32 | P2 条件 | 真实硬件授时、时间来源及不确定度证据 | 有明确硬件/设备/现场授权与 profile |

### 5.1 源码与验证落点

下表用于派单时定位现有目录，不是整目录改写授权，也不是已确定的最终文件清单。T01 核对当时工作树后，为每个实际 PR 再固定精确文件/片段和测试过滤器；新增类型/测试文件的名称在对应任务中确定。

| 任务 | 主要源码或工具落点 | 验证落点 |
|---|---|---|
| T01 | `docs/design/`、本专题源码影响清单和版本/范围记录 | `tests/SonnetDB.Core.Tests/` 中既有时序用例、旧库/旧程序固定夹具 |
| T02 | `src/SonnetDB.Core/Model/`、`src/SonnetDB.Core/Query/`、`src/SonnetDB.Protocol/` | Core 的 Model/Query/Protocol 定向用例 |
| T03 | `src/SonnetDB.Core/Engine/`、目录创建/打开及持久化元数据入口 | Engine 创建/重开与旧引擎负向进程夹具 |
| T04 | `src/SonnetDB.Core/Wal/`、`src/SonnetDB.Core/Storage/Format/` | Core Wal、真实 `tests/SonnetDB.CrashTests/` |
| T05/T06 | `src/SonnetDB.Core/Storage/`、`Memory/`、`Engine/` 及真实 manifest/tombstone 路径 | Storage/Segments、Query/Engine、跨段删除/恢复用例 |
| T07/T08 | `src/SonnetDB.Core/Query/`、`Model/TimeBucket.cs`、`Sql/` | Query/Sql、范围/分桶/倒序与真实 ADO 对拍 |
| T09/T10 | `src/SonnetDB.Core/Query/Functions/`、`Engine/Retention/`、相关后台维护入口 | 物理时间函数与 retention 边界/回拨/重开 |
| T11 | `src/SonnetDB.Core/Engine/` 与上述组合入口 | Core 真实数据库旅程及独立重开进程 |
| T12/T14 | `src/SonnetDB/Hosting/`、`Configuration/`、`Contracts/`、`Endpoints/` 与 JSON contexts | `tests/SonnetDB.Tests/` 的真实 Server/REST/权限/metadata |
| T13 | `src/SonnetDB.Protocol/`、`connectors/dotnet/SonnetDB.Client/`、Server Frame handler | Client/Protocol 合同、真实 HTTP/2 端到端 |
| T15/T16 | `src/SonnetDB.Core/Ingest/`、Server 各设备 listener/ingestor/handler | Core Ingest、Server HTTP/UDP/MQTT/CoAP 与固定单位协议用例 |
| T17/T18 | `src/SonnetDB.Data/`、`connectors/dotnet/SonnetDB.Client/`、`embedded/native/SonnetDB.Native/`、C 头文件/连接器 | ADO/Client/Native、实际 .NET/C 包消费者和 AOT 产物 |
| T19/T20/T21 | `connectors/` 与 `embedded/` 中对应语言；继续遵守 M49 目录和产品线 | 各语言合同/真实消费者；缺工具链不生成替代 PASS |
| T22 | `src/SonnetDB.Cli/`、`web/src/`、`src/SonnetDB.Studio/`、`extensions/sonnetdb-vscode/` | CLI、web/e2e、Studio 实物与 VS Code Extension Host |
| T23 | `src/SonnetDB.Core/Streaming/`、`Cdc/`、`Views/` 与适用计算状态 | 持久窗口/桥接/检查点及状态升级/拒绝/重开 |
| T24/T25 | `src/SonnetDB.Core/Backup/`、CLI/管理迁移入口 | Backup/Migration、CrashTests 与目标目录无覆盖验证 |
| T26/T27/T28/T29 | `tests/SonnetDB.Parity/`、`SonnetDB.CrashTests/`、基准和受控长跑工程 | 版本化语料、TRX/原始结果/进程与资源回执 |
| T30/T31/T32 | 既有发布/打包工程、文档；外部生态和硬件仅按冻结范围新增 | 真包/安装/发布回读；真实 Collector 或已授权物理采集证据 |

## 6. 逐项实施说明与完成标准

### M50-T01：现状、合同和基线

- **交付物：** 调用链影响清单、legacy/新接口矩阵、单位/范围/有损策略 ADR、首批版本/语言/平台/负载、分阶段估算与性能退化预算。每个毫秒命中标明“时序时间、物理时长、其它模型、已版本化协议”。
- **实施：** 从 Point→WAL/Segment→Query→SQL→Server/Protocol→Data/Client/Native→消费者逐段追踪；冻结旧 ms 样本和实际旧引擎版本。已有 SQL 100ns、codec、Frame 和迁移基础只登记复用。
- **完成标准：** 明确复现相邻 us/ns 时刻落到相同 ms、writer DateTimeOffset 双缩放及旧结果能力缺口；冻结预期错误/精确值。未复现的候选保持 UNKNOWN；不修改生产数据或预填性能收益。

### M50-T02：精确时间和换算

- **交付物：** Core/Protocol 共享语义、精确时间戳与时长边界、checked 缩放/整除/商余/范围变换及 public 中文 XML 文档；遵守 Protocol 不依赖 Core 的 M49 约束。
- **实施：** 用整数处理单位倍率与 epoch；宽中间值使用框架已有安全 API。存储非负与查询哨兵分开；DateTime 100ns 转换显式检测余数，零时间是有效时刻而非统一 missing。
- **完成标准：** s/ms/us/ns 往返、1ns/100ns/1us、零/极值/溢出、查询 floor/ceiling、非法 enum、日期范围及有损策略通过；0 个相关 AOT/trim 警告，无第三方 Core 运行时依赖。

### M50-T03：持久目录与打开防护

- **交付物：** 版本化精度元数据、创建/打开/挂载状态与组件兼容矩阵；具体真实 header/feature 门禁和发布顺序。
- **实施：** 先校验目录/身份/格式再启动 replay、maintenance 和写入；配置冲突、缺失或损坏元数据、混合精度组件明确拒绝。仅已知旧库采用 ms。
- **完成标准：** ms/us/ns 创建重开一致；两个不同精度库可同时打开且不串缓存。真实旧引擎拒绝高精度库且目录文件哈希不变；若不能保证则保持高精度建库关闭。

### M50-T04：WAL 和恢复

- **交付物：** WAL header/payload/滚动/replay 的格式与单位绑定，旧 ms 读取路径及拒绝诊断。
- **实施：** 保留 LSN 的独立序号语义，时间字段按库精度编码；与现有 CRC、group commit、确认/fsync 语义衔接，未知提交不自动重发。
- **完成标准：** 先只写 WAL 后强杀重开保留相邻 1ns 点；重复 replay、尾部截断、未知版本、单位不符、CRC 错误和滚动恢复有确定结果。

### M50-T05：Segment、索引和编码

- **交付物：** Segment/Block 时间上下界、时间索引、TimestampCodec/raw 与适用 range decode 的统一合同；格式变更说明。
- **实施：** 编码层存库单位整数，codec 本身不猜测单位；有符号 delta/差分、极值与 restart checkpoint 计算防溢出，复用 M46。
- **完成标准：** raw/现有编码均能保留 1ns 差，块边界/稀疏/乱序/极值/窄范围读取准确；损坏精度或不支持版本明确拒绝，无 silent ms 回退。

### M50-T06：维护、删除和 checkpoint

- **交付物：** flush、compaction、manifest、tombstone、snapshot/checkpoint 精度接线。
- **实施：** 合并仅处理同数据库身份与精度；generation、段序号和写入顺序不解释为时间。精确删除范围贯穿内存、落盘和恢复。
- **完成标准：** 多段合并/删除/重开前后逐点值与时刻一致；相同时间戳沿用已冻结行为；错误精度段不能被合并或当过期段清除。

### M50-T07：查询内核

- **交付物：** 范围绑定、升降序归并、latest-N/limit、稀疏字段、索引裁剪、查询缓存/continuation 的单位合同。
- **实施：** 以统一上下文比较库单位整数；跨单位边界计算精确索引界限。新缓存键/游标绑定库身份、单位和代际；继续执行 M41/M42 预算及取消。
- **完成标准：** 全扫描参考与裁剪/索引/倒序结果逐点一致，覆盖不可整除边界、空/单点、最大值、乱序与多库；保持有界读取和旧 ms API 的单位含义。

### M50-T08：SQL time 与日期互转

- **交付物：** time 绑定、整数字面量/参数、duration、now、GROUP BY time、DELETE、排序/JOIN/分组/EXPLAIN 的类型与精度声明。
- **实施：** parser 的小单位解析贯穿绑定和执行；新结果显式声明单位，legacy 保持 ms。DATETIME/DATETIME2/DATETIMEOFFSET 转入/转出采用明确规则，不能经过毫秒中间值。
- **完成标准：** 表达相同时刻的 ms/us/ns 查询对齐，亚毫秒桶可计算或准确拒绝；1ns 值转 DateTime 的损失明确报告，空结果/参数/视图仍有精度元数据；不重做关系日期类型。

### M50-T09：物理时间计算

- **交付物：** derivative/rate/irate、integral、elapsed、PID、forecast/异常及 gap-fill 的单位清单和对照用例。
- **实施：** 先以整数计算差值，再转换一次物理时长；对尚未支持的函数在高精度库中明确拒绝，不返回按错单位计算的结果。
- **完成标准：** 同一物理语料按三档表示时结果符合冻结的数值误差；分桶/时间键仍逐值精确，零/重复/倒退时间和缺测有原合同结果；合成函数测试不计硬件控制验收。

### M50-T10：retention 与时钟

- **交付物：** NowFn/TTL/过期 cutoff 的精确上下文和兼容入口，以及后台 poll/flush/超时的物理时长清单。
- **实施：** 不按字段名批量乘倍率；明确旧 `TtlInTimestampUnits`、自定义 NowFn 与新库的组合，不能证明一致时拒绝启动相关维护。
- **完成标准：** cutoff 前/等于/后 1 单位、远未来、溢出、虚拟时钟回拨和重开不误删；ms/us/ns 库共存；ns 时钟若仅有 100ns 来源，能力报告真实来源。

### M50-T11：嵌入式最小闭环

- **交付物：** 单进程三库的 create→typed write→raw/SQL query→flush→close→reopen→delete 完整样例与清单。
- **实施：** 将 T03～T10 组合到真实 Tsdb 入口；未通过的函数或旧读取合同在执行前拒绝。此阶段保持内部候选，不宣称网络/消费者已交付。
- **完成标准：** 同一物理时刻、多点间隔与字段类型经 WAL/Segment 恢复后准确；旧 ms fixture 原时间/值合同保留，公开高精度入口的防护前置。

### M50-T12：Server 与管理合同

- **交付物：** 建库、注册/挂载、列表/详情、权限、能力发现、配置诊断的精度 DTO 与 source-generated context。
- **实施：** 请求创建精度与已存在库精度分开处理；查询能力绑定真实库而非服务端默认配置，授权与数据库身份贯穿连接上下文。
- **完成标准：** 真实双库/双身份创建、重开、挂载冲突、未授权和重建代际测试；服务器未具备本批完整能力时拒绝创建或明确禁用操作。

### M50-T13：Frame 协议

- **交付物：** 时间参数、时序列/SQL 结果、批数据、schema/能力、版本与终态的 wire 合同；黄金二进制样本。
- **实施：** 在独立 Protocol 中定义，不把 Native ABI 当远程协议；精确 Int64 和单位版本明确，保持预算、取消、错误终态与 unknown 写入语义。
- **完成标准：** 真实 HTTP/2 的相邻 1ns 往返、类型化 NULL/零行、截断/错误单位/未知版本拒绝；旧/新 client/server 矩阵明确，不自动回落 REST 后丢精度。

### M50-T14：REST 与 NDJSON

- **交付物：** 精确时间整数文本、单位元数据、SQL/NDJSON/导出响应和协商错误合同。
- **实施：** 服务端手写 reader/writer 或 source-generated JSON；不使用反射序列化。legacy ms 响应语义保留，us/ns 响应需可识别的新合同。
- **完成标准：** 超过 2^53 的纳秒值完整返回；类型/NULL/零行、分页/流式结束、取消和旧客户端拒绝逐项核对；不把 JSON Number 的文本输出直接当浏览器无损证明。

### M50-T15：统一批量解析

- **交付物：** LP、JSON points、BulkValues 的原始时间与单位模型、严格换算入口和逐项错误说明。
- **实施：** 解析器先保留原始值，目标库完成一次换算；正文/请求单位冲突采用明确规则。builder/DateTimeOffset 的具体 API 修复归 T17，本任务交付可复用换算边界和负向样本，避免两处重复实施。
- **完成标准：** ms/us/ns 各写入 s/ms/us/ns、无 timestamp、非法单位/溢出/余数、有损 opt-in 的矩阵；整批/逐项副作用按现有批次合同验证，不凭有界批次声称原子事务。

### M50-T16：设备和兼容协议

- **交付物：** Influx HTTP v1/v2、专用 LP、UDP、MQTT、CoAP、Sparkplug、Prometheus 与适用 Modbus 时间映射表。
- **实施：** 每个入口保留已发布默认输入单位；固定 ms 来源精确升尺度；UDP 监听输入精度与目标库存储精度分开，不能随库精度改变设备报文含义。
- **完成标准：** 经真实监听入口写入同一语料后对拍，错误单位有预期错误；UDP 无 ACK、设备实际采样、物理授时各自保留边界，不能把发送成功当耐久写入。

### M50-T17：.NET SDK、Data 与 ADO

- **交付物：** typed writer/query、Client/Data/ADO 参数和结果精度、DateTime 显式转换、schema 和时间范围 helper。
- **实施：** `.Timestamp(long)`、`.Timestamp(DateTimeOffset)` 与 writer 输入单位明确；精确纳秒类型贯穿 embedded/新 Frame/适用 REST，M49 新 API 不新增旧桥接层。
- **完成标准：** 编译后的独立 .NET 消费者实际写/读/重开 1ns 值；泛型 Reader、空结果及 old ms 语义准确；不支持 CLR 表示的转换明确拒绝；AOT/trim 零相关警告。

### M50-T18：C 与 NativeAOT

- **交付物：** 版本化 C/Native ABI 的时间类型、单位/能力、结果生命周期与头文件/共享库配套清单。
- **实施：** 遵守 M49 远程原生 Frame、embedded 共用 AOT 的两条产品线；little-endian、Safe-only、显式所有权与错误，不经 Double 或原有毫秒 DateTime 中转。
- **完成标准：** 真实 C 消费者两条适用路径读取纳秒、取消、释放、关闭和重开；实际 AOT 产物运行，头文件/库同源；仅构建成功不能标消费者通过。

### M50-T19：Go 与 Rust

- **交付物：** 精确 epoch/单位映射、惯用时间接口和溢出转换、原生 Frame 与 embedded FFI 适用实现。
- **实施：** 不假设语言日期范围等于 Int64 时间轴；继承 context/取消、RAII/所有权与有界批次；旧单位结果不得静默当新单位。
- **完成标准：** 每语言/平台独立真实消费者验证参数、结果、重开、取消和 >2^53/非 100ns 对齐值；工具链缺失保留 NOT_RUN。

### M50-T20：Java 与 Python

- **交付物：** Java 8 Instant/long、JNI 与原生远程，以及 Python 整数/精确时间模型；可选 Pandas dtype/NaT/时区映射清单。
- **实施：** Python datetime 的 us 上限与纳秒整数分开；Pandas 等可选依赖不进入 Core；Java8、缓冲与异常规则沿用 M49。
- **完成标准：** 真实 Java/Python 消费者逐项往返纳秒与单位；非整微秒转换明确处理；可选 DataFrame 未验不宣称分析生态已支持。

### M50-T21：BASIC 远程接口

- **交付物：** VB6/PureBasic 的精确 Int64 文本或高低位接口、单位、Frame 时间参数和结果说明。
- **实施：** VB6 Double 不能承担纳秒整数容器；保持仅远程定位，不增加 x86 AOT 桥，按实际工具链与 HTTP/2 能力验收。
- **完成标准：** 精确文本/整数与纳秒黄金样本一致；真实远程写/读、错误/取消各有回执；不能以 C 或 .NET 消费者代替 BASIC 验收。

### M50-T22：CLI 与三个管理宿主

- **交付物：** 建库精度选择、连接/库详情、时间输入/显示/排序、过滤、游标与导出的共享 typed 组件；分别接 CLI/Web/Studio/VS Code。
- **实施：** 保留整数文本/BigInt 或精确结构，显示最多 9 位小数；区分来源精度和显示格式，不经毫秒 Date 截断后再过滤。权限和资源错误走已有入口。
- **完成标准：** 两条仅差 1ns 的真实数据在各承诺宿主可区分、排序、精确筛选和导出/再导入；宿主安装、包版本与真实服务分别验收。

### M50-T23：持续计算与状态

- **交付物：** 窗口、水位/迟到、CDC/桥接、物化派生时间列、checkpoint/恢复的单位和状态版本。
- **实施：** 只调整既有 M43/M45 时间边界；源/目标精度不同先显式校验，不能静默截断；尚未支持的操作在 ns/us 库执行前拒绝。
- **完成标准：** 三精度的同物理窗口结果一致；迟到/重复/回拨、暂停/恢复、旧状态版本拒绝、跨精度桥接与断点重开对账；任务位点不能先于原耐久决定。

### M50-T24：备份与恢复

- **交付物：** manifest/inspect/verify/restore 的精度、数据库身份与组件版本清单。
- **实施：** snapshot 绑定一致恢复边界；restore 保留单位，精度变换只走 T25；明确实例 MQ/外部 spool 是否在恢复域中。
- **完成标准：** 已知旧 ms、新 ms/us/ns 库备份重开逐点一致；缺/冲突精度、错目标及旧恢复器明确拒绝，失败不会覆盖已有目标库。

### M50-T25：显式迁移与回退

- **交付物：** dry-run 影响报告、创建目标/有界搬运/checkpoint/对账/切换/回退操作及限制文档。
- **实施：** 使用一致源快照或停写窗口；升级先查溢出，降级先查余数/键碰撞；进度绑定源身份/代际和转换策略，保留源库。
- **完成标准：** ms→us/ns 无损，us/ns→ms 默认准确拒绝；显式截断可核对损失及冲突，取消/断点/满盘/切换崩溃可恢复；回退处理切换后的新确认写入。

### M50-T26：黄金语料与 Parity

- **交付物：** 版本化语料/预期整数、旧 ms 字节夹具、新旧 client/server/engine 兼容表和现有 Parity 适配增量。
- **实施：** 覆盖 1ns、100ns、1us、1ms、零/最大值、>2^53、不可整除边界、乱序/稀疏/重复；对外参考数据库固定版本和精度，在共同可表达域内对拍。
- **完成标准：** 时间戳及桶边界逐整数精确相等，不用浮点容差掩盖单位错误；light/full 和旧 ms 回归按同候选通过，不能合并或清理 `parity-results`。

### M50-T27：恢复与故障

- **交付物：** WAL/flush/compaction/checkpoint/迁移/restore 的强杀点、损坏/磁盘不足/取消故障矩阵及原始回执。
- **实施：** 记录 PID/开始时间/命令/父链，设置次数/超时并清理仅任务所属子进程；校验元数据损坏与旧引擎拒绝均无破坏性副作用。
- **完成标准：** 确认写在声明耐久级别内恢复，未确认/未知写可对账；无误删、单位漂移或失败后 silent ms fallback。进程强杀不替代掉电或物理介质验收。

### M50-T28：固定硬件成本

- **交付物：** 固定 x64/ARM64、候选、语料、确认/fsync、codec 与查询 profile 的对照报告。
- **实施：** 分两组测试“同物理时刻只变单位”和“来源真正增加亚毫秒/抖动信息”；测写入、范围/倒序/桶/函数、冷开、压缩、磁盘/WAL、CPU/分配/内存/I/O。
- **完成标准：** 按 T01 冻结门槛对 ms 回归和 us/ns 成本作结论；明确 8-byte 表示不等于相同压缩率/速度，不预填收益倍数；未测架构保持 NOT_RUN。

### M50-T29：长稳和资源

- **交付物：** 同候选多库混合精度、迟到/重复/回拨、retention/维护/恢复/消费者的 168 小时报告及停止条件。
- **实施：** 先通过短时受控样例，再按冻结机器/容量运行；观测单位错误、丢点/覆盖异常、资源增长与错误恢复，定时采样有最大次数和总期限。
- **完成标准：** T01 的正确性/资源门槛和所有清理要求满足；M20 七次 scheduled 状态独立读取，不以一次手动 full 或 168 小时进程存活替代七天与业务正确性。

### M50-T30：产品交付与完成判定

- **交付物：** 本批功能/限制/最低版本矩阵、升级回退文档、source-generated DTO 清单、AOT/包消费者/安装与发布回读证据。
- **实施：** 每个代码提交前在最终树运行完整 restore 和原 Format Check；发布候选跑适用 build/test/AOT、真实 Server/消费者与包安装，遵守现有 release policy。
- **完成标准：** 本批声明能力逐项有证据，未完成语言/宿主/外部条件不升级；全部 T01～T30 完成后才关闭整体 M50。包发布、远端 CI、部署、实机和现场授时各自记账。

### M50-T31：条件生态接入

- **触发条件：** M48-L21/L22 或某个明确 Arrow/DataFrame 消费者进入冻结范围。没有请求接收能力时，本任务保持 planned。
- **交付物：** OTLP 的 time_unix_nano/observed_time_unix_nano 或 Arrow timestamp 单位/时区/NULL 映射；OTLP 的 0 表示 missing 与 SonnetDB 的 epoch 0 分开处理。
- **完成标准：** 真实 Collector/分析消费者往返和数据对账；接收 Logs 不等于支持 Metrics/Traces，列出 unsigned 源整数超出本库范围时的明确错误。

### M50-T32：条件硬件证据

- **触发条件：** 有明确设备、授时网络、采样率、时标转换和现场操作授权。
- **交付物：** 设备/硬件打戳位置、NTP/PTP/GNSS/本地时钟域、校准方法、偏差/抖动/不确定度及事件/观察/摄取时间对账。
- **完成标准：** 用真实采集链路证明来源能力和保真；ns 整数 round-trip 不算 1ns 授时准确度，也不自动授权修改现场授时或部署。

## 7. 实施顺序与集成门禁

| 阶段 | 主任务 | 可交付结果 | 退出条件 |
|---|---|---|---|
| G0 合同与旧库防护 | T01～T03 | 可审查合同、黄金负基线、版本与建库开关 | 默认值/单位/范围/损失/旧引擎保护确定，公开 us/ns 建库仍关闭 |
| G1 嵌入式闭环 | T04～T11；T26/T27 相关用例同步累积 | 三档真实持久化、查询、维护与重开 | 相邻 1ns 不丢失，旧 ms 不误读、retention 不误删；只是内部候选 |
| G2 Server 与首批 SDK | T12～T18；T26 对应矩阵 | 真实 HTTP/2、适用 REST、.NET/C 首批候选 | 参数/结果/拒绝/能力/消费者完整，无 ms 中间截断 |
| G3 消费者与派生状态 | T19～T23 按冻结批次 | 语言、工具与既有流功能分别接入 | 承诺入口可精确消费，不支持的组合执行前拒绝；共享文件串行集成 |
| G4 迁移、恢复与成本 | T24～T29；T26/T27 完整收口 | 可恢复迁移/回退、固定报告与 168 小时证据 | 所有候选证据绑定相同最终实现，失败/未知保留 |
| G5 按批发行 | T30 | 明确矩阵的正式包与独立下载/安装回执 | 本批所有适用门禁通过；整体 M50 仍须 T01～T30 全部完成 |

首个实施任务是 T01；冻结后优先 T02/T03。存储/格式修改先于公开新精度，恢复门禁随实现累积，不推迟到最后一次性补。M43/M46/M49 同一文件的变更必须核对活动工作归属后串行集成，不能复制旧整文件覆盖对方切片。

每个任务用一个可审查 PR 或更小的单职责切片，关联 M50-Txx 与适用既有里程碑；附更改路径、实际旧/新格式、精确输入/输出或错误、验证候选/环境、未测矩阵、恢复/回退和下一依赖。不得把 T01 准备、helper 单测或单语言通过标成整个 G1/G2 完成。

## 8. 统一验收与证据矩阵

| 维度 | 最少覆盖 | 证据要求 |
|---|---|---|
| 精确值 | 三种库存储精度 × 四种输入单位，1ns/100ns/>2^53/极值 | 原始整数和预期结果/错误；禁止经 Double/毫秒中转 |
| legacy | 已知旧 ms 库、旧引擎/新库、旧/新 client/server、无/坏元数据 | 原文件哈希、实际最低版本和拒绝回执；不得猜测缺失单位 |
| 查询 | raw/SQL、桶/范围、DESC/latest-N、稀疏/重复、物理时间函数 | 与扫描/物理时间参考对拍；时间键不设置数值容差 |
| 耐久/恢复 | WAL/段/维护/删除/派生状态/备份/迁移 | 真实进程/故障回执、确认等级和逐项对账；不扩大为掉电 PASS |
| 入口/消费者 | embedded、HTTP/2 Frame、适用 REST、设备协议及本批语言/宿主 | 每项实际执行与产物版本/哈希；单端或 mock 不升级其它端 |
| 成本 | 固定机器、相同物理语料与更高信息量语料 | P50/P95/P99、CPU/分配、RSS、磁盘/WAL/I/O/压缩；冻结退化预算 |
| 长期/发行 | 168 小时、M20 适用 scheduled、AOT、真实包与安装 | 最终 commit/candidate、原始日志与回读；未执行保留 NOT_RUN |

源码要求继续为 Safe-only、Core 零第三方运行时依赖、严格编译、中文 public XML 文档、source-generated JSON 与相关 IL/AOT 零警告。提交前原格式门禁：

```text
dotnet restore SonnetDB.slnx
dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/
```

各产品测试、真实网络、固定硬件、长期、包/正式发行和现场验证分别记 PASS/FAIL/UNKNOWN/NOT_RUN。新增里程碑不重写既有失败，不自动发布、部署或启动远程任务。

## 9. 研究依据及结论使用边界

以下为本会话已读取的官方文档和 GitHub 活动分支，检索日期 2026-11-02；活动分支会变化。实施 T01 应冻结所用版本/commit，不把产品宣传、时钟 API 的数字位数或其它数据库默认值当 SonnetDB 实测。

- [Prometheus timestamp.go](https://github.com/prometheus/prometheus/blob/main/model/timestamp/timestamp.go)：毫秒样本时间。
- [InfluxDB 时间模型](https://github.com/influxdata/influxdb/blob/master/models/points.go)、[InfluxDB 3 写入](https://docs.influxdata.com/influxdb3/core/api/write-data/)：多输入单位与纳秒规范存储。
- [TDengine 数据库精度](https://docs.tdengine.com/tdengine-sql/ddl/database/)、[精度定义](https://github.com/taosdata/TDengine/blob/main/include/util/tdef.h)：按库 ms/us/ns、默认 ms。
- [IoTDB 配置](https://iotdb.apache.org/UserGuide/latest/Reference/System-Config-Manual_apache.html)：系统级 ms/us/ns、默认 ms 与首次启动限制。
- [QuestDB ColumnType](https://github.com/questdb/questdb/blob/master/core/src/main/java/io/questdb/cairo/ColumnType.java)：微秒/纳秒类型。
- [PostgreSQL 日期类型](https://www.postgresql.org/docs/current/datatype-datetime.html)、[ClickHouse DateTime64](https://clickhouse.com/docs/reference/data-types/datetime64)：列级精度与范围约束。
- [Apache Arrow Schema](https://github.com/apache/arrow/blob/main/format/Schema.fbs)、[OpenTelemetry Logs](https://github.com/open-telemetry/opentelemetry-proto/blob/main/opentelemetry/proto/logs/v1/logs.proto)：精确整数/单位及事件/观察时间的互操作依据。
- [chrony](https://chrony-project.org/)、[DateTime.Ticks](https://learn.microsoft.com/en-us/dotnet/api/system.datetime.ticks?view=net-10.0)：授时条件与表示分辨率分开，.NET tick 为 100ns。
