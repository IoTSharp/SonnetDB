# ROADMAP — SonnetDB 4.5

更新日期：2026-10-10（Asia/Shanghai）。**当前优先交付 Workbench Preview 1；先发布 Web Admin 的已验收子集，Studio 与 VS Code 通过各自门禁后再纳入。** 首版预览不代表 SonnetDB 4.5 正式版或 M47 三面整体完成，当前发布状态为 `NOT_READY`。

根路线图只保留剩余交付、依赖和验收顺序。已完成实现、受限真实旅程以及旧窗口记录已归入 [CHANGELOG：2026-10-10 完成范围归档](CHANGELOG.md#roadmap-completed-archive-2026-10-10-workbench)；详细原始证据仍在 [M47 验证记录](docs/design/m47-unified-management-workbench/validation-report.md)。后续不按 WB 编号重新实施已完成功能。

## 当前执行顺序：Workbench Preview 1

| 顺序 | 交付 ID | 目标 | 依赖 / 当前状态 |
|---|---|---|---|
| 1 | M47-P01 | 冻结预览范围、已提交候选、版本与发布物白名单 | **🟢 范围冻结完成，产品仍 NOT_READY**。[范围/入口控制](docs/design/m47-unified-management-workbench/preview-1-scope.md)、[候选01](docs/design/m47-unified-management-workbench/preview-1-candidate-01.json)、[独立分发合同](docs/design/m47-unified-management-workbench/preview-1-distribution.md)已记录；入口与分发实现仍阻断。 |
| 2 | M47-P02 | 定位 Web smoke 失败并取得候选通过证据 | **下一项；🚧**。P01已冻结；已有失败证据，具体产品/夹具/环境原因待核。 |
| 3 | M47-P03 | 补齐首版真实登录与权限、安全结果和所承诺恢复旅程 | P01；🚧。复用已完成九模型旅程，只补未验承诺及受影响回归。 |
| 4 | M47-P04 | 生成来源明确的 Web/Server 预览发行物 | P02、P03；📋。版本、commit、合同、能力、依赖和 SHA256 一起冻结。 |
| 5 | M47-P05 | 用实际发行物验首次启动、部署与用户旅程 | P04；📋。验证安装后的资源和真实 Server，开发服务器运行不替代。 |
| 6 | M47-P06 | 完成预览说明、已知限制、升级/回退和反馈入口 | P01～P05；📋。文档描述实际发行范围；如需限定发布资产，补独立可审查的分发切片。 |
| 7 | M47-P07 | 按现有发布门禁核同一候选并作放行评审 | P02～P06；📋。保存真实 workflow/job/step/artifact 结果，不降低原 gate。 |
| 8 | M47-P08 | 发布、独立回读下载结果并归档首版 | P07 全部必选通过且取得实际发布授权；⏳。上传、公开可下载与运行验收分别记。 |

具体范围、每步退出条件、宿主后续与失败处置见下方 [M47 首个预览版路线](#workbench-preview-1)。P01候选01来源固定为 `c40fa670e3a170c5fac8cefa181d618631862270`，保留版本 `4.5.0-preview.1.1`；仅为已提交实现起点，未构建/验收/打标签。后续代码、配置或打包变更须新增候选与版本，不覆盖该清单、不用 dirty 工作树发包。首版限定 win-x64 同源环回 Web＋Server；旧 `workbench` 定时保持 `PAUSED`。

4.5 后续仍推进 AI 应用、聚合/持续计算、存储编码及跨模型成本、恢复、容量与真实质量，按本文件 M44～M46 和 V45 工作包执行；这些增量不自动成为单独 Workbench Web 预览的先决任务。已撤回的系统性能原始报告不作为验收依据。完整背景见 [路线历史](docs/roadmap-history.md)、[4.5 规划核查](docs/audits/sonnetdb-45-roadmap-planning-20261003.md)、[九模型证据](docs/audits/nine-model-capability-evidence-20260905.md)和[十四能力索引](docs/audits/fourteen-capability-evidence-index.json)。


## 完成判定

1. 代码必须存在且由真实产品入口调用；原型、未调用类型和文档计划不算功能交付。
2. 实现合同、本地回归、真实服务、固定硬件、真实模型、长期运行及远程发布分别记录；一个层级的 PASS 不升级其它层级。
3. 新增工作统一为 `📋 planned`。`M44-Axx`、`M45-Cxx`、`M46-Sxx`、`M47-Pxx`、`V45-Xxx` 是规划 ID，不是已创建的 GitHub issue 或 PR。既有内部 `#N` 与外部 `GH-Issue #N` 保持区别。
4. P0 为基础合同与优先交付，P1 为本版深化与验收，P2 为条件候选；只有明确必选范围参与 4.5 完成判定，条件项未经独立评审不成为版本承诺。
5. 质量、性能与资源阈值在实现前绑定语料、版本、机器及基线冻结；未校准保持 `NOT_READY`，不以自造数据或缩规模 PASS 代替真实效果。
6. Core 保持 Safe-only、零第三方运行时依赖、source-generated JSON、Native AOT/trim 零相关警告及 public API 中文 XML 文档。API/帧保持兼容；新落盘格式必须版本化、拒绝不兼容旧 writer，提供迁移或明确拒绝及恢复方案。SQL 名称遵守 GH-Issue #211。

状态：`🟢` 当前范围已完成 / `📋` 规划或尚未启动 / `🚧` 有剩余实现或验收 / `🟡` 指定本地切片完成、外部证据待补 / `⏳` 未执行 / `❌` 已执行失败。历史完成范围在 CHANGELOG，主路线只列待办。

## 4.5 目标与范围

**把九模型从可组合的数据能力推进到有真实效果、有资源边界、可恢复的 AI 与分析应用底座。** 应用覆盖工业诊断、业务经营分析、文档知识与资产检索；工业是重点场景，产品保持通用多模型引擎定位。

本版必选：M44-A01~A07、M45-C01~C09、M46-S01~S09、M47-U01~U09，以及 V45-X01~X06、X08 中注明的有界合同和适用验收。M44-A08/A09、M45-C10、M47-U10、V45-X07 是条件项；M44-A10 是后续候选。每包拆成单一职责 PR，不将完整研究集合塞入一个实现。

本版不纳入 TsFile 适配、导入导出或替换持久格式。参考其它产品机制后按 SonnetDB 当前存储、类型、事务和嵌入式边界独立改进，不以复制语法、算法数量或商业功能清单作为完成条件。完整分片集群、九模型分布式事务、任意外部副作用 exactly-once、完整 SQL/GQL/S3 兼容均不由本路线自动承诺。

### 代表产品研究与采纳原则

按产品家族覆盖主流关系、分析、时序、文档、缓存、搜索、向量、图和持续计算产品。官方来源、版本/商业边界及未核实项写在专题中；不声称穷尽所有产品或已完成同条件性能对照。

| 产品家族与代表 | 学习机制 | SonnetDB 采纳方向 |
|---|---|---|
| PostgreSQL / MySQL / SQLite | 聚合类型/NULL、事务/WAL、快照、统计及代价可见性 | 精确语义、资源指标与恢复；不复制全部方言，未取得正文的项保持待核。 |
| DuckDB / ClickHouse | 批执行、聚合 state、并行合并、统计跳读与列式成本 | 在 M33/M41 深化，兼顾嵌入式及多模型并发。 |
| TDengine / IoTDB / TimescaleDB | 设备/维度分析、时间函数、rollup、迟到修正与持续聚合 | TAG 分组扩展为通用聚合/增量计算，区分静态属性与观测。 |
| InfluxDB / QuestDB / VictoriaMetrics / DolphinDB | 窗口、ASOF、采样/填补与流聚合资源取舍 | 逐项核对已有函数；内存态流聚合不等同持久 event-time 计算。 |
| Flink / Materialize / RisingWave | merge/retract、event time、检查点与增量物化 | 复用 M43 任务/投递，首批限定可证明等价的 SQL 子集。 |
| MongoDB / Redis | 管道聚合、索引、持久性取舍与状态 | 文档/KV/MQ 参与应用，不伪称跨模型原子事务。 |
| Elasticsearch / Qdrant / Weaviate / pgvector | 混合召回、过滤、重排、相关性与向量工作集 | 延续 M35/M36 的索引与 RRF，强化真实语料、ACL、删除同步。 |
| Neo4j GraphRAG / TDgpt / IoTDB AINode / pgai | 图证据、时序推理、模型调用与版本 | 已有预测/RAG/GraphRAG/provider 推进到受治理应用。 |
| Snowflake Cortex / Databricks 等 AI 平台 | 质量/成本、批量推理、评测与应用闭环 | 学习治理与旅程；托管商业功能不算开源引擎现状。 |
| dbx / Tabularis / DBeaver / DataGrip / pgAdmin / Compass / RedisInsight / Kafka UI / MinIO Console / Neo4j Browser | 统一 Explorer、SQL/Notebook、结果平面、模型专用工作台、MCP/插件和发布分发 | 由 M47 统一三面工作台；复用交互语义与安全边界，不复制通用多库协议或商业功能清单。 |

来源与选择理由见 [AI 专题](docs/design/sonnetdb-45-ai-applications.md)、[聚合专题](docs/design/sonnetdb-45-aggregation-continuous-compute.md)、[存储专题](docs/design/sonnetdb-45-storage-performance.md) 和 [M47 统一管理工作台专题](docs/design/m47-unified-management-workbench.md)。采纳代码前逐组件核查许可：TDengine AGPL 核心不可直接搬入继续只标 MIT，其他许可也保留相应声明，不从 SDK 许可推断全产品许可。

## 里程碑总览

| Milestone | 主题 | 状态 | 4.5 保留工作 |
|---|---|---|---|
| 14 / 27 | Copilot、AI/Agent 数据访问与治理 | 🚧 | 真实 provider 质量/成本、双网与 Studio 实机；Agent Framework 迁移单独评估，自研 CopilotAgent 不改称已基于该框架。 |
| 19 / 25 | 时序与文档容量 | 🟡 | 四档时序、million/ten-million 文档固定硬件与恢复，复用 runner/verifier。 |
| 20 | 多模型 Parity | 🚧 / 候选待核 | 同一发行候选light/full原始证据与七天scheduled独立观察；历史失败不机械当最新结论。 |
| 29 | Studio 安装与宿主 | 🟡 | 干净 Windows、升级/卸载、WebView2、端口冲突与生命周期。 |
| 35 | 语义内容与多模态检索 | 🚧 | 真实质量、模型换代/回滚、删除同步、容量与硬件；持久摄取/provider 基础不重做。 |
| 36 | 九模型易用性 | 🚧 | 完整真实旅程、远程 parity、取消/恢复、对象分页与 MQ 实例恢复。 |
| 39 | SQL 触发器生产观察 | ⏳ | 已归档研发之外的混合 DML、deferred/outbox 尾延迟及长期 SLO。 |
| 40 | 原生属性图 | 🟡 / Graph Beta | 外部对拍、容量、AOT、Couplet、hard-kill 及长期 gate；AI 图应用不替代底层门禁。 |
| 41 / 42 | 规划器与九模型性能 | 🚧 | 全链路有界读取、总资源预算、页/I/O 成本、冷启动及固定架构/168 小时。 |
| 43 | 十四能力与生态发布收口 | 🚧 | 远程 CDC/schema/冲突、恢复、业务副作用边界、完整旅程及原始报告。 |
| **44** | **AI 应用与可治理推理** | **📋** | 预测/异常、证据 RAG、模型治理、可恢复推理任务与真实效果门禁。 |
| **45** | **聚合与持续计算深化** | **🚧** | 扩展分组支持矩阵，补通用state、更新删除修正、增量物化/rollup与批流等价。 |
| **46** | **存储编码与执行成本优化** | **📋** | 编码策略、整数/高熵回退、范围解码、统计精度及存储成本。 |
| **47** | **统一数据库管理工作台与三面发布** | **🚧 / Preview 1未放行** | 先按P01～P08收口Web预览，再补Studio/VS Code；正式版共享合同、九模型完整权限/预算/恢复、AI/MCP与逐页矩阵继续验收。已完成切片见CHANGELOG。 |

M22 保持上层应用候选；样例验证通用合同，行业规则不直接内置引擎。M0~M13、M15~M18、M21、M23/M24/M26/M28/M30~M34/M37/M38 及其它完成代码范围只在 CHANGELOG 追溯。

## Milestone 44 — AI 应用与可治理推理

复用已有 `forecast(linear/holt_winters)`、`anomaly(zscore/mad/iqr)`、`changepoint(cusum)`、在线 chat/embedding provider、显式 ONNX profile、持久摄取、Hybrid/RRF/重排、typed MCP，以及知识图谱/GraphRAG 上层合同与 typed SDK 投影。增量是应用效果与可治理推理，不把已有实现重新包装为新功能。

### 九模型如何支撑 AI

| 模型 | 应用角色 | 必须验证的边界 |
|---|---|---|
| 时序 | 趋势、预测、异常、变点与时间特征 | 时间切分防泄漏、缺失/乱序、基线、误报及区间校准。 |
| 关系 | 业务实体/指标、标签反馈与特征元数据 | 类型/SQL、权限、血缘；模型结果不隐式执行业务写操作。 |
| KV | 热状态、幂等键、缓存与 feature lookup | TTL/模型/ACL 进入缓存键，原子性范围和恢复明确。 |
| 文档 | 知识、标注、结构化输入与结果 | 更新/删除/TTL 同步、出处和 schema 可追踪。 |
| 全文 | 关键词与精确证据召回 | analyzer、语言、ACL 与相关性，不仅测返回命中。 |
| 向量 | 语义召回与相似样本 | profile/维度/版本、过滤 Recall、重建与回滚。 |
| 对象 | 原文、多模态内容及模型资产 | checksum、大文件/Range、出域与删除引用。 |
| MQ | 推理/重建触发、结果与告警 | 至少一次、稳定身份、背压、offset 与实例恢复。 |
| 图 | 关系、路径证据与影响传播 | 有界遍历、出处/权限/时间、无证据拒答，Graph Beta 继续公开。 |

### 工作包

| ID | 优先级 / 范围 | 增量交付 | 依赖与退出条件 |
|---|---|---|---|
| M44-A01 | P0 必选 | 模型/profile、版本和输入输出增量合同 | 延续 M27/M35；维度/版本/能力不匹配拒绝，切换可回读/回滚。 |
| M44-A02 | P0 必选 | 权限、出域、时限、成本/并发预算及追踪 | 接 V45-X02；未授权数据不入检索/prompt，缓存不跨 ACL/模型。 |
| M44-A03 | P0 必选 | 时序预测、异常及诊断应用 | 复用算法，接 M45 分组；独立测试集对照 naive/seasonal 基线，验证误报/不规则采样。 |
| M44-A04 | P0 必选 | 有出处的混合检索与证据回答 | 复用 RRF/重排；真实召回、引用支持率、无证据回答及删除同步。 |
| M44-A05 | P0 必选 | 可恢复的有界批量推理任务 | 复用摄取/M43；checkpoint、结果幂等、取消/恢复、积压及外部未知结果。 |
| M44-A06 | P0 必选 | 真实模型/应用评测与发布 profile | 质量/延迟/成本/版本可复算；hash fallback/tiny fixture 仅作合同证据。 |
| M44-A07 | P0 必选 | 工业诊断、业务分析、知识/资产助手三条旅程 | 嵌入式和真实 Server/SDK/MCP，覆盖九模型职责、权限/删除/失败/重开及批准写回。 |
| M44-A08 | P1 条件 | provider 原生流式及中断/预算 | 部分现有 provider 为完整响应；先对拍能力发现、首 token、取消、成本及续流。 |
| M44-A09 | P1 条件 | 有界 GraphRAG 证据扩展 | M40 适用 gate；真实对照收益，不足时降级、不编造路径。 |
| M44-A10 | P2 后续 | 高级离线/多模态/自适应模型候选 | 有任务、资产许可及质量/内存证明再单项纳入，不承诺训练平台。 |

逐项设计与评测见 [M44 专题](docs/design/sonnetdb-45-ai-applications.md)。应用评测不关闭 M27 双网或 M35/M40 底层容量门禁。

## Milestone 45 — 聚合与持续计算深化

覆盖关系、measurement、文档可适配数据及增量，不限设备分析。复用 M31 类型、DISTINCT 子集、时间函数、M33 下推/sketch、M37 全量物化、M41 GROUP BY spill 和 M43 持久滚动/滑动/会话窗口。首批TAG/time分组已归档，通用列/扩展表达式分组矩阵仍待补；有 `Merge` 不代表可撤回，持久数值窗口拒绝坏值/NULL 也不自动等价 SQL 忽略 NULL。

| ID | 优先级 / 范围 | 增量交付 | 依赖与退出条件 |
|---|---|---|---|
| M45-C01 | P0 必选 | 扩展已交付TAG/time分组的支持矩阵，统一类型/NULL/空集/overflow/名称边界 | 首批实现见CHANGELOG；扩展表达式、SELECT/HAVING/分组键在执行前校验，补原名/引号与关系兼容。 |
| M45-C02 | P0 必选 | 版本化聚合 state 与 Add/Merge 复用 | 分别声明结合/交换/顺序敏感性；COUNT/SUM merge 不冒称幂等，投递去重在输入身份层。 |
| M45-C03 | P0 必选 | 支持源的 before/after、retract、更新/删除修正 | 非可逆 MIN/MAX/sketch 有界重算或拒绝；缺 before-image 不静默按追加处理。 |
| M45-C04 | P0 必选 | 有限 SQL 的增量物化/rollup 与有界回填 | 复用 generation/位点/调度；快照增量无缺口、失效重算、原子发布与依赖变更。 |
| M45-C05 | P1 必选 | SQL 批流窗口及分区 event-time 推进 | 复用窗口；乱序/迟到/idle、水位回退/关闭及回填可对拍。 |
| M45-C06 | P1 必选 | 精确/近似分位、distinct/sketch 准确性和资源 | 复用 TDigest/HLL；误差/merge/偏态/种子公开，不冒称精确结果。 |
| M45-C07 | P1 必选 | 时间加权、率、积分、采样/填补边界和 state | 核对已有函数；边界点、重复时间、counter reset、缺失及最大跨度。 |
| M45-C08 | P1 必选 | 聚合 state 预算、落盘及恢复 | 复用 spill/V45-X02；高基数准入，取消/磁盘满/坏状态不损已提交结果。 |
| M45-C09 | P0 必选 | 批流、内存/落盘、跨端对拍和观测 | 同事件日志离线/增量等价；重投/更新/删除/重开，计数精确、浮点误差有声明。 |
| M45-C10 | P2 条件 | ROLLUP/GROUPING SETS、扩展窗口与 ASOF | 真实查询驱动单项选择；CUBE/count/state/change windows/ASOF 不全部默认纳入。 |

冻结首批源、AST、聚合及变更种类；不支持的 JOIN/递归/UDF/事务在计划阶段拒绝。M45 负责计算语义与状态，M43 负责运输、位点、订阅及任务生命周期。详见 [M45 专题](docs/design/sonnetdb-45-aggregation-continuous-compute.md)。

## Milestone 46 — 存储编码与执行成本优化

编码器已有，但 `SegmentWriterOptions` 时间戳/值编码默认均为 `None`，V2 Int64 仍 raw 8B。v6 已内嵌 HNSW/sketch。mmap 仍有复制；解码缓存按 reader 设置，共享 HNSW 缓存及 KV I/O 预算另有实现。不能把可选编码、mmap 或局部额度写成默认压缩、zero-copy 或进程总内存上限。

| ID | 优先级 / 范围 | 增量交付 | 依赖与退出条件 |
|---|---|---|---|
| M46-S01 | P0 必选 | 基线、版本、默认/显式编码策略及收益门槛 | raw/现有可选编码都测，默认是否切 auto 由语料与回归决定。 |
| M46-S02 | P0 必选 | 整数 delta/RLE/bit packing 与 raw 回退 | 全值域/溢出、稀疏/乱序 round-trip；新语义不偷复用 v6 标志。 |
| M46-S03 | P0 必选 | 无损浮点/Boolean 与高熵回退 | NaN 位模式、±0、Infinity；无默认量化/有损压缩。 |
| M46-S04 | P1 必选 | 字典高基数与稀疏 presence 成本 | 字典扩张、Unicode、大值及缺失/NULL 分开，超预算不截断。 |
| M46-S05 | P1 必选 | 分块自适应与范围解码 | 块/检查点结合窄读测 I/O/CPU/分配，防压缩导致读放大。 |
| M46-S06 | P0 必选 | 统计跳读精度 guard | Int64→double 等边界；不得依据不可靠元数据剪枝，精确差分及旧段回读。 |
| M46-S07 | P1 必选 | 解码分配、缓存与读成本接线 | 接 V45-X01/X02；租约/取消/淘汰/mmap 成本可观测，不新造资源管理器。 |
| M46-S08 | P1 必选 | small-segment/WAL/compaction 成本 | 复用 manifest/调度；写放大/fsync、合并读尾延迟与恢复共同验收。 |
| M46-S09 | P0 必选 | 统一语料、兼容/损坏及性能验收 | commit/硬件/配置/原始样本；旧格式可读、不兼容写拒绝，收益与代价分开。 |

指标包含磁盘字节、读写放大、解码 CPU、P95/P99、分配/GC 和恢复，不预设倍数。落盘改动先设计版本/CRC，遵守格式升级、CHANGELOG、迁移或显式拒绝规则。详见 [M46 专题](docs/design/sonnetdb-45-storage-performance.md)。

## Milestone 47 — 统一数据库管理工作台与三面发布（UI）

<a id="workbench-preview-1"></a>

### 首个预览版的范围

M47 继续采用一套共享核心和 Web Admin、Studio、VS Code 三个宿主。已交付的外壳、七模块导航、九模型资源身份、结果/草稿/历史/审批、模型页和宿主合同进入 CHANGELOG，不重新作为实施任务。设计与交互基线见 [M47 专题](docs/design/m47-unified-management-workbench.md)及[设计评审包](docs/design/m47-unified-management-workbench/README.md)；预览顺序以本根路线图为准，旧 [WB 队列](docs/design/m47-unified-management-workbench/work-queue.md)用于追溯证据。

| 发布面 | Preview 1 范围 | 放行条件 / 延期规则 |
|---|---|---|
| **Web Admin + 匹配的 Server 宿主** | **必选**：连接/真实登录、七模块导航和九模型已验证的有界浏览/查询、当前结果导出；只纳入终态与权限已验收的写审批动作 | P01～P07 全通过后独立发布；未验收动作必须明确限制、禁用或延期，不能仅靠 release notes 遮住产品入口。 |
| Studio Windows | 后续独立桌面预览候选；已有 bridge/Managed Local/本机恢复证据复用 | 当前包的干净 Windows/WebView2、安装/升级卸载、承诺的文件对话框及生命周期未通过前，不纳入公开安装包清单。 |
| VS Code | 后续独立扩展预览候选；已有资源树、深链接、基础 Host 和查询合同复用 | 完整 Query/history/退出与所承诺能力、新不可变版本 VSIX、安装/兼容/哈希通过后独立纳入。现有 Marketplace 0.4.1 不代表本次 M47 更新。 |
| AI/MCP 入驻、完整治理、全部模型写/恢复矩阵、Notebook/高级分页等 | 首版未验收的增量先延期；已有公开入口逐项评估并写入 P01 范围表 | 延期不删除既有能力，也不放宽已有权限、预算、未知写结果及安全合同；正式版按后续 U02～U09 验收。 |

Graph 继续声明 **Beta**。MQ 在数据库资源树中以 `database + Topic` 作逻辑身份，物理持久化仍为实例 `.system/mq`；单库备份不能称为覆盖实例 MQ。SQL 名称合同、只读边界、一次审批、取消不等于回滚、网络未知结果不重放均保持。

### Preview 1 按顺序收口

以下 `M47-Pxx` 是本地规划 ID，不是已创建 GitHub issue/PR。每项围绕一个实际缺口实施；已有功能只做与候选相关的必要回归，不重新补造实现。

| ID / 优先级 | 具体交付 | 完成与验收证据 |
|---|---|---|
| **M47-P01 / P0** | **🟢 范围冻结完成**：候选01完整SHA、版本/兼容、13组动作、入口限制及独立分发白名单 | [冻结合同](docs/design/m47-unified-management-workbench/preview-1-scope.md)与[机器清单](docs/design/m47-unified-management-workbench/preview-1-candidate-01.json)可供后续引用。九模型限定读取＋关系表单行审批插入；C01～C05/D01仍需P03～P06实施/验收，产品NOT_READY。Studio/VSIX延期，Publish默认含Studio而不含VSIX；独立渠道不得触发v*全套发布。 |
| **M47-P02 / P0** | 核远端 run37731752397 的 Web Admin / Studio bridge smoke 失败；按证据修复产品、夹具或环境缺口 | 在 P01 候选取得 Web build/smoke 和所影响回归通过，保留原失败。原 run 绑定 `5424b666`，其四个成功 job 不升级为全套 PASS；具体失败根因待日志核实。不能把 helper 审阅超时当产品根因或无界续接工具诊断。 |
| **M47-P03 / P0** | 补齐真实登录 UI、普通用户和只读入口、身份切换、撤权后的载荷清理，以及本次承诺的结果/恢复旅程 | 在真实 Server/正式路由验证授权与拒绝；取消/断连/截断和写 unknown 可见、不会重放；导出只覆盖标明的当前窗口；HTTP/字节/行/时间预算明确。复用 WB26～WB37 的九模型证据，补未验承诺及候选差异，不把 API 安装 token 算登录 UI。 |
| **M47-P04 / P0** | 构建来源可追踪的 Web/Server 预览包及 manifest/hash | 资源、Server、配置模板、许可证、安装说明与版本/commit 对应；按所含程序集取得适用 trim/AOT/构建证据。旧 Release DLL、web/dist 存在和 analyzer PASS 不能替代当前发行物。 |
| **M47-P05 / P0** | 用 P04 实物验证首次部署/启动、登录、导航、查询/导出和纳入的写审批 | 在公布的根路径/代理子路径及目标环境通过；资源/SSE/认证返回/深链接与真实 Server 一致。验证目录/权限、端口占用、停止重开与回退，所有文件/进程清理仅限核验归属。只测试实际承诺模式，不把 Vite 本地开发运行当发布部署通过。 |
| **M47-P06 / P0** | 编写首版预览说明、已知限制、升级/回退和反馈方式；必要时实现限定资产的分发切片 | README/CHANGELOG/版本兼容/使用说明与 manifest 一致，延期功能不会混入可用声明。现有 `publish.yml` 会构建并上传 Studio 等资产：Web-only 预览须有明确可审查的资产选择或独立分发路径，不能顺带公开未验收桌面包；不以此降低现有验证要求。 |
| **M47-P07 / P0** | 冻结最终候选并完成提交、CI、发行物与真实旅程放行评审 | 如需提交，最终树先完整 `dotnet restore SonnetDB.slnx`、原 CI `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`；同 commit/version/latest attempt 的真实工作流和有效 artifact 由既有 verifier 核验。必选失败/UNKNOWN/NOT_RUN 均不放行，延期项明确排除。 |
| **M47-P08 / 发布** | 经实际发布授权执行一次发布，核公开页/下载/版本/哈希与首跑，再归档完成条目 | P07 全通过后进行；保存已确认 ID/URL/上传结果及独立回读。未知远端效果先核实，不盲重试。仅核到公开文件不冒称全部宿主/生产验收；发布后把本轮完成 P 项移入 CHANGELOG。 |

现有发布验证读取 [release-readiness policy](eng/release-readiness-policy.ps1)：Workbench 五个 job 及要求的 artifact，CI/Format/AOT、适用 CodeQL、Parity light/full、soak/容量合同和发布/连接器等检查按原 policy 执行。排除首版 Studio 或 VS Code 下载物，不等于可以跳过其既有基础 CI job。七天 scheduled Parity 保持独立非阻断观察，不将其误设为首个 Web 预览必须等待七天，也不把缩规模验证当固定硬件/生产证据。

当前验收基线与剩余阻断见 [2026-10-10 发布审查](docs/design/m47-unified-management-workbench/release-readiness-2026-10-10.md)。最新 Studio WB80 已证明本机 A/B 选择、重启被动恢复 B/真实查询/正常关闭；WB61 旧失败只保留历史，不继续作为“恢复从未通过”的理由。VS Code WB83 未建立该次真实 Server/Code 旅程，完整发布验收仍需补证。WB85～WB98 pending 诊断集成不作为 Web 预览的默认先决任务。

### 首版之后：Studio、VS Code 与正式版

| 顺序 / 原始归属 | 剩余交付 | 退出条件 |
|---|---|---|
| **下一宿主：M47-U06 / M29** | Studio 当前候选包的原生文件对话框、Managed Local/Remote、干净 Windows/WebView2、安装/升级卸载与端口/进程生命周期 | 复用 WB13/15/39/40/80，补当前包与源码来源绑定、承诺文件操作和实机旅程；不推断 Server 优雅关闭、任意崩溃恢复或旧失败原因。独立通过后发布桌面预览。 |
| **下一宿主：M47-U07** | VS Code 向导、完整 Query/history/正常退出，所承诺分页/Notebook/LSP/EXPLAIN和治理深链接 | 复用 WB14/16/48/58/59，本地合同与真实 Host 分列；新 VSIX 用新不可变版本，核安装、组件版本映射、实际语言服务文件和包 SHA256。独立通过后发布扩展预览。 |
| **正式版：M47-U02 / U03** | 三宿主共享合同实际消费、完整分页/取消/离线、身份隔离、结果/草稿/历史和审批组合 | 共用 typed client/能力/权限/资源合同；按宿主与真实服务分别取得证据，未支持路径明确拒绝，局部切片不计整包完成。 |
| **正式版：M47-U04 / U05** | 九模型剩余写/维护/导入导出、显式恢复、完整权限/只读、全链路资源预算与 Web 治理 | 按模型单项收口；Graph 完整身份/edge/import、Object Multipart/大文件、KV 原子/TTL/Int64、MQ Nack/实例恢复、Vector Profile/完整写和 FullText 高级读取各自验收。当前窗口导出不代替全库快照。 |
| **正式版：M47-U08 / U09** | WorkBuddy/Claude/Cursor/Codex AI/MCP onboarding、自检、凭据隔离、逐页视觉/可访问性与三面版本兼容矩阵 | typed MCP/stdio 既有实现不重做；补真实配置/稳定宿主能力、只读工具与数据外发边界。七导航、30全局页/166任务页签的设计覆盖不计逐页生产完成。 |
| **条件项：M47-U10 / P2** | manifest/签名、插件/连接器目录、权限撤销、升级回滚与市场治理深化 | 核心工作台与安全评审完成后单项纳入；首版必需的版本/哈希/发布清单由 P01/P04/P06 完成，不以 U10 条件项延期这些基本交付。 |

M47-U01 的设计基线及既有页面/合同切片已归档，后续不重新派单。Studio/VS Code 延期只调整首版预览的分发范围，4.5 正式版原 M47-U01～U09 必选合同继续保留。任一宿主未通过自己的实物安装/真实服务/用户旅程，不能把三面整体标为发布完成。


## 4.5 跨模型能力与性能闭环

沿用既有归属，不重复建设核心服务。

| ID / 归属 | 优先级 / 范围 | 交付与验收 |
|---|---|---|
| V45-X01 / M41、M42 | P0 必选 | 执行器→REST/Frame→ADO/SDK 批/惰性读取、首行、取消/断连、行/字节准入及工作集；明确阻塞/spill，不把切帧/预览当全链路有界。 |
| V45-X02 / M42 | P0 必选 | 基于 SQL worker/内存、KV I/O 和缓存，增加数据库/进程总预算、分类归属及公平准入；治理 reader 缓存随段数放大、共享 HNSW 跨库配置、AI/后台与前台竞争，记录 CPU/I/O/heap/排队/释放。 |
| V45-X03 / M43 | P0 必选 | 已有本地 CDC/桥接/任务之外的有界远程合同：schema/version、历史增量、冲突/删除/TTL、续传/积压/offset；首批限定源和拓扑，不把副本称为 HA。 |
| V45-X04 / M36、M43 | P0 必选 | MQ 实例 snapshot/restore 与数据库恢复组合；对账 offset、任务/checkpoint、物化结果、派生 AI 索引/模型引用的一致性点与顺序，公开 RPO/RTO 和非原子边界，单库备份不含 `.system/mq`。 |
| V45-X05 / M35、M36、M29 | P1 必选 | 全文相关性/重建、filtered ANN/Recall、KV continuation/TTL、对象高变更率分页与大文件校验、图有界结果、SDK/CLI/Workbench 对拍；按明确缺口实施，UI 不先于入口。 |
| V45-X06 / 既有证据里程碑 | P0 必选 | 固定语料/机器/版本/持久性/客户端；分开功能、质量、容量、恢复及长期报告，九模型/AI/聚合/编码可复算，失败样本保留，冻结适用门槛。 |
| V45-X07 / M43 架构决策 | P2 条件 | 主备/复制先定义模型边界、fencing、单写者、日志缺口、未知提交、RPO/RTO 和 split-brain 矩阵；真实需求及一致性证据后单独实现，不承诺 4.5 自动 failover/完整分片。 |
| V45-X08 / M36、M44、M45 | P1 必选 | measurement/TAG/FIELD 上的设备/业务实体模板、静态属性映射、稀疏观测及跨实体分析；复用关系实体，不新增原生模型，名称/类型/模板演进与普通 SQL 一致。 |

### 九模型验收与参考负载

| 模型 | 验证重点 | 对照方式 |
|---|---|---|
| 时序 | 高基数、稀疏/乱序、TAG 分组、rollup、编码/恢复 | TDengine/IoTDB/TimescaleDB 等同语义子集，embedded/server 分开。 |
| 关系 | 分组/窗口、精确类型、增量修正、JOIN/排序及首行内存 | PostgreSQL/SQLite/DuckDB/ClickHouse 分 OLTP/OLAP 负载。 |
| KV | 大 keyspace/TTL、MultiGet、热点、游标/冷读 | 复用 Redis parity，恢复/缓存/网络分开。 |
| 文档 | 索引聚合、TTL/更新删除、million/ten-million 与恢复 | MongoDB 语义参考，不承诺 BSON/wire/官方 Driver。 |
| 全文 | 多语言、精确/模糊词、ACL、相关性/重建 | 复用 Meilisearch，Elasticsearch 作评测设计参考。 |
| 向量 | 过滤选择率、Recall@K/nDCG、换代、ANN/scan 内存 | Qdrant/pgvector 同维度/度量/召回档，合成不替代真实语义。 |
| 对象 | continuation、高变更率、大文件/Range/checksum/恢复 | MinIO 已支持合同，不外推完整 S3/SigV4。 |
| MQ | 至少一次、offset、重投、backlog/实例恢复 | NATS JetStream；TMQ/Kafka 概念参考不构成协议兼容。 |
| 图 | 路径/过滤、引用、算法、容量/恢复 | Neo4j/PostgreSQL、LDBC/Graphalytics，Graph gate 独立。 |



1. **基线与合同冻结：** M44-A01/A02/A06、M45-C01/C02/C09、M46-S01/S09、V45-X06；冻结支持矩阵、语料与门槛，不重做归档实现。
2. **基础实现与成本治理：** M45 分组/state、M46 编码/精度 guard、V45-X01/X02；每包有必要定向回归、Release 和适用 AOT。
3. **持续计算与恢复：** M45-C03/C04/C05/C08 接既有 M43，V45-X03/X04/X08；不支持源/操作及未知结果明确拒绝或进入恢复。
4. **AI 应用与九模型旅程：** M44-A03/A04/A05/A07、M45-C06/C07、M46 深化和 V45-X05；条件项独立证明收益后决定。
5. **性能、质量与发布论证：** 执行既有目标机队列和新应用/聚合/编码 gate，绑定同一候选；本机合成数字不升级生产结论。
6. **统一管理工作台与三面发布：** M47 先评审完整导航、五区壳、共享对话框/提示和一级/二级任务，再逐页细化；用户确认原型后推进共享合同、九模型工作台与生产迁移。Web Admin/Studio/VS Code 旅程、AI/MCP 入驻和版本兼容矩阵分别通过对应宿主证据；单端或原型 PASS 不升级为三面整体发布。

独立切片可并行；共享源码、构建输出和资源验证按所有权协调。单 PR 只做一个可审查切片。

## 真机验证待办

记录硬件/架构、commit、配置/版本/命令/时间、真实 P50/P95/P99、working set/heap/分配/GC、逻辑与物理 I/O 区别、正确性/恢复与原始样本。不可用项为 `NOT_RUN`/`NOT_READY`/`DEFERRED`。

| 来源 | 保留验收 | 当前边界 |
|---|---|---|
| M19 #125 | 百万 series、万 segment、20 次 kill/reopen、万 measurement | 固定 Linux x64 四档待证。 |
| M25 #174 | million/ten-million 文档写查/TTL/重建/备份恢复/内存 | 固定目标及 attestation 待证。 |
| M20 #136 | 同候选light/full真实backend/artifact、七天scheduled观察 | 2026-10-10审查核到远端5424b666的10月8/9日light/full成功，非本地预览候选证明；七天完整观察未核。 |
| M27 #184/#185/#187/#340 | 真实模型/成本、IdP/broker、双网/续流及 Studio | 功能/本地已归档，现场独立验收。 |
| M29 #258 | 干净 Windows、WebView2、升级/卸载、端口/生命周期 | 安装包/宿主已有，实机待验。 |
| M35 / M36 | 真实语料、模型换代、九模型跨端/权限/取消/删除/恢复 | 本地不替代完整旅程、远程或容量。 |
| M36 #323/#325/#326 | 对象大文件/分页/校验、MQ 实例/offset 恢复 | 不由单库备份推出实例一致性。 |
| M39 | 混合 DML、触发器/deferred/outbox 长期尾延迟 | 研发之外的生产观察。 |
| M40 #352/#367 / Couplet | 外部对拍、LDBC/Graphalytics、1m/10m、AOT、hard-kill/168 小时 | Beta 与联合环境 gate 独立。 |
| M41 / M42 | 固定 x64/ARM64、同语料、冷启动/首行/heap/I/O、恢复/168 小时 | 本地预算/快路径不代替目标机。 |
| M44 | 三条旅程真实质量/成本、防泄漏、删除/回滚/任务恢复 | 新规划，fixture/hash 不计效果。 |
| M45 | batch↔stream、merge/retract、late/update/delete、rollup/spill/reopen | 新规划，冻结首批子集/误差。 |
| M46 | 编码/高熵回退、旧新格式、损坏/极值/放大/冷读 | 新规划，不预填提升倍数。 |


## 待补验收证据

### M20 — Parity nightly

沿用 [9/25 回读](docs/audits/m20-nightly-readback-20260925.md)与[9/26 候选 light/full](https://github.com/IoTSharp/SonnetDB/actions/runs/36211622630)的范围。候选 verifier 核对同提交/版本十二类 workflow、最新 attempt、真实 backend 与原始 artifact；七天 scheduled 自 9/27 起为独立非阻断观察，不因手动复验或规划标记完成。其它适用质量/恢复/容量发布证据不能由此省略。

### M19 / M25 — 容量与发布

沿用[固定硬件容量合同](docs/benchmarks/m19-capacity-hardware.md)及 Document verifier；缩规模 hosted validation 仅证明对应行为。固定规模、硬件真实性/attestation、恢复及性能分别验收。

### M27 / M35 / M36 / M40 / M41 / M42

已有合同与 gate 不因新里程碑重编号、降门槛或重新声明完成。见[provider profile](docs/benchmarks/m27-provider-model-profile.md)、[M35 质量/预算](docs/m35-filtered-search-budgets.md)、[对象合同](docs/object-client-contract.md)、[Graph gate](docs/m40-graph-367-production-gate.md)、[SQL 内存边界](docs/benchmarks/m42-sql-result-bounds.md)。

## Milestone 43 — 十四套能力与生态发布总收口

#382~#384、本地 CDC/窗口/任务/DLQ/自动投递/桥接及汇总工具移至 CHANGELOG；这里只保留剩余。M44 应用、M45 计算语义、M46 编码各自归属新里程碑。

| 范围 | 状态 | 剩余交付 |
|---|---|---|
| #385~#390 | 🚧 | 远程同步、schema/冲突、离线续传、客户端及现场故障/容量。 |
| #391~#395 | 🚧 | 远程投递/接管、权限、业务副作用边界与现场恢复，已有本地底座不重做。 |
| #396 | 🚧 | 十四能力完整入口/权限/失败/取消/重启/对账及远程组合。 |
| #397 | ⏳ | 真实发布级原始报告，汇总工具不等于证据。 |
| #398~#402 | ⏳ / 草稿已有 | release 绑定、外部提交/反馈、公开案例、最终验收；提交/收录/排名分开。 |

既有细分与退出见[总索引](docs/roadmap-total-milestone.md)，外部 issue 见[路线快照](docs/github-issues-roadmap.md)。

## 性能观察项

| 编号 | 候选 | 进入条件 |
|---|---|---|
| PF1 | 级联删除按选择率切索引/哈希 | 1/10/50/100 父键矩阵收益及回滚等价。 |
| PF2 | fuzzy 高基数词典结构 | 100k/500k term 证实瓶颈及收益；原至少 2 倍候选门槛保留，不是当前性能声明。 |
| PF3 | ANN tombstone 区间/gate | 墓碑是主要成本且不降召回/精确语义。 |

候选不自动转为必选；有基准后并入 M46/V45-X05 单项评审。

## 4.5 完成与发布判定

1. 必选代码入口、矩阵、回归及适用 AOT/兼容完整；条件项明确交付或延期，不掩盖必选残余。
2. 三条 AI 旅程有真实模型/数据的可复算质量与成本；九模型职责、ACL/删除/取消/恢复和显式写回可追踪。
3. 支持子集的批流/增量/重算等价，迟到/更新/删除和不支持路径明确；至少一次与计算幂等分层，不承诺未知副作用 exactly-once。
4. 编码无损、统计剪枝正确、旧新格式恢复及高熵回退通过；默认由基准决定，优化不牺牲确认写持久性。
5. 适用固定硬件、远程、安装、容量、恢复、跨架构及长期证据绑定候选；保持 scheduled 独立观察与 Graph Beta gate。
6. 主路线、总索引、专题、能力索引及 CHANGELOG 状态一致；研究/规划只记录文档已变更，未来功能实际交付后才进 CHANGELOG。

## 已完成范围索引

见 [2026-10-10 Workbench及完成范围归档](CHANGELOG.md#roadmap-completed-archive-2026-10-10-workbench)、[2026-10-03 4.5归档](CHANGELOG.md#roadmap-completed-archive-2026-10-03-45)及[2026-09-21归档](CHANGELOG.md#roadmap-completed-archive-2026-09-21)。归档不关闭本路线保留的剩余验收，也不宣布预览或正式版发布。

## 历史链接兼容锚点

旧链接保持可达，当前计划以本文工作包、真机待办和 CHANGELOG 证据范围为准。旧完成度/实施状态入口改指向上方完成归档。

<a id="当前完成度一览2026-10-06"></a>
<a id="m47-实施状态索引2026-10-06"></a>

<a id="milestone-12--函数与算子扩展pid--forecast--udf"></a>
<a id="milestone-17--可观测性与运行时可见性-observability--runtime-visibility"></a>
<a id="milestone-18--vs-code-数据库扩展sonnetdb-for-vs-code"></a>
<a id="milestone-19--生态适配底座能力关系--kv缓存--对象桶--大量-measurement"></a>
<a id="milestone-19--生态适配底座能力关系--kvcache--对象桶--大量-measurement"></a>
<a id="milestone-20--多模能力对齐与平移测试-parity"></a>
<a id="milestone-24--sonnetdb-studio-管理体验升级document-管理面"></a>
<a id="milestone-25--document-store-验收文档与发布治理"></a>
<a id="m40-修复与发布执行顺序2026-08-23-复盘"></a>
