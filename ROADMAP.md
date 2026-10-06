# ROADMAP — SonnetDB 4.5

本文件是 **4.5 版本的主执行路线**：在九种原生模型上强化 AI 应用、通用聚合与持续计算、存储编码和执行成本，并把 Web Admin、Studio 桌面和 VS Code 收敛到一套统一的数据库管理工作台核心，补齐现有能力的远程、恢复、容量、真实质量及三面发布边界。4.5 是规划目标，本文不宣布版本已发布，也不修改当前包版本。

规划基线：2026-10-04，本地提交 `4b004946`；M47 设计基线已于 2026-10-05 获用户确认，生产实现按 [Workbench 队列](docs/design/m47-unified-management-workbench/work-queue.md) 的有界切片推进。当前 WB-00～WB-31 已按各自本地范围验证，WB-31取得Measurement真实4/4、Document回归3/3与独立证据复核，最终门禁及本地提交以队列/证据和git log为准，提交与验证见队列，WB-12 Document 为 `54c78755`、WB-13 Studio合同为 `74835847`、WB-14 VS Code资源导航为 `e7cf2fe5`、WB-15 Studio客户端为 `e1f93a69`、WB-16导航/认证为 `0bb628ad`、WB-17 Relation为 `f1978263`；这些局部状态不等同 M47/U01~U09 全量完成。已完成范围归入 [CHANGELOG 本轮归档](CHANGELOG.md#roadmap-completed-archive-2026-10-03-45)，历史背景见[原归档](docs/roadmap-history.md)。本轮研究、证据复核与文档验证见[规划核查记录](docs/audits/sonnetdb-45-roadmap-planning-20261003.md)及 [M47 统一管理工作台专题](docs/design/m47-unified-management-workbench.md)。

现有能力事实继续沿用[综合审计](docs/audits/2026-09-05_project-SonnetDB-report.md)、[九模型证据](docs/audits/nine-model-capability-evidence-20260905.md)、[gap catalog](docs/audits/nine-model-gap-catalog-20260905.json)和[十四能力索引](docs/audits/fourteen-capability-evidence-index.json)，结合后续已核实切片判断。已撤回的系统性能原始报告不作为验收依据。

## 完成判定

1. 代码必须存在且由真实产品入口调用；原型、未调用类型和文档计划不算功能交付。
2. 实现合同、本地回归、真实服务、固定硬件、真实模型、长期运行及远程发布分别记录；一个层级的 PASS 不升级其它层级。
3. 新增工作统一为 `📋 planned`。`M44-Axx`、`M45-Cxx`、`M46-Sxx`、`V45-Xxx` 是规划 ID，不是已创建的 GitHub issue 或 PR。既有内部 `#N` 与外部 `GH-Issue #N` 保持区别。
4. P0 为基础合同与优先交付，P1 为本版深化与验收，P2 为条件候选；只有明确必选范围参与 4.5 完成判定，条件项未经独立评审不成为版本承诺。
5. 质量、性能与资源阈值在实现前绑定语料、版本、机器及基线冻结；未校准保持 `NOT_READY`，不以自造数据或缩规模 PASS 代替真实效果。
6. Core 保持 Safe-only、零第三方运行时依赖、source-generated JSON、Native AOT/trim 零相关警告及 public API 中文 XML 文档。API/帧保持兼容；新落盘格式必须版本化、拒绝不兼容旧 writer，提供迁移或明确拒绝及恢复方案。SQL 名称遵守 GH-Issue #211。

状态：`🟢` 当前范围已完成 / `📋` 规划或尚未启动 / `🚧` 有剩余实现或验收 / `🟡` 指定本地切片完成、外部证据待补 / `⏳` 未执行 / `❌` 已执行失败。历史完成范围在 CHANGELOG，主路线只列待办。

## 当前完成度一览（2026-10-06）

这张表放在路线图前部，直接区分“本地切片已经完成”和“整个里程碑仍未完成”。`🟢` 只表示表中列出的切片已通过其记录的本地验收；它不会把真实 Server、固定硬件、长期运行、三宿主安装或发布证据一并标记为完成。

| 范围 | 当前状态 | 已完成 / 当前证据 | 尚未完成 |
|---|---|---|---|
| M47 WB-00～WB-10 | 🟢 | 设计基线、原型交互/状态合同、资源身份、导航/Explorer 兼容、结果/审批工作流、外壳迁移与深链接切片均已提交；提交哈希和测试见 [Workbench 队列](docs/design/m47-unified-management-workbench/work-queue.md)。 | 全量九模型页面、真实 Server 旅程、Studio/VS Code/安装/发布证据仍待补。 |
| M47 WB-11/WB-30/WB-31 Measurement Workbench | 🟡 | 原迁移`390ff526`；WB-30补点/monitor/write精确权限锁存、readonly、连接/Schema代际与旧finally隔离、一次审批/原身份unknown，500行先截断、auto12轮/60秒和导入1000语句/10批/60秒。Node16/16、全Web311/311、Chrome fixture20/20、旧Measurement/Vector子页8/8、build与独立复核通过；既有Kestrel SQL预览端点兼容3/3单列；WB-31新UI真实4/4、当前100/500/61与导出、WRITE两点逐INSERT终态、撤权锁存，Document回归3/3、审计Node10/10/全Web321/321及完整成功证据独立复核通过；最终完整门禁为本地提交放行条件，实际结果见验证记录。 | 显式恢复、完整COMMIT/权威影响数/Int64、raw defaults无信号ABA、文件/Server预算、三宿主/安装/AOT/发行物。 |
| M47 WB-12/WB-18/WB-26 Document Workbench | 🟡 | WB-12身份/六态/隔离与WB-18显式Find100恢复/输出预算已有实现；WB-26补本机新Web→真实Kestrel权限/恢复及Aggregate1001、Distinct501/1000旅程3/3，修空IDs省略与满1000完整性unknown。Node21/21、全Web295/295、Chrome fixture11/11、TypeScript/Vite与独立复核通过。 | 登录UI/routed readonly props、扫描/中间物化/字节/总堆预算、Advanced完整读写、其它九模型真实旅程、三宿主与发行物。 |
| M47 WB-13 Studio宿主合同 | 🟡 | 真实bridge身份/URL与Managed Local生命周期合同已提交 `74835847`，Release定向34/34；客户端消费由WB-15另验。 | 干净Windows/WebView2、安装与发行物。 |
| M47 WB-14 VS Code资源导航 | 🟡 | 九模型/index/backup资源与Web导航入口已提交 `e7cf2fe5`，Node20/20与本机Extension Host注册检查；浏览器回选/认证夹具由WB-16补证据。 | 真实Server权限、外部OS浏览器交接、VSIX与发布。 |
| M47 WB-15 Studio客户端 | 🟡 | 宿主身份/lifecycle、保守操作门禁与迟返隔离已提交 `e1f93a69`；Node10/10、全Web137/137、Studio定向40/40、TypeScript/Vite与浏览器夹具8/8。 | 真实Server、WebView2/干净Windows、安装与三宿主全旅程；旧Header与完整Explorer异步组合另验。 |
| M47 WB-16 导航/认证与部署base | 🟡 | 已提交 `0bb628ad`；登录返回、base/SSE和index分组修复，Node9/9、全Web146/146、两部署浏览器各17/17、根/代理构建、本机真实Host13节点命令调用及独立复核/完整门禁通过。 | 真实Server权限/代理部署、远程SSE、同origin存储隔离、VSIX与三宿主全旅程。 |
| M47 WB-17/WB-27 Relation Workbench | 🟡 | WB-17迁移 `f1978263`；WB-27新Web→本机Kestrel分页50/200/尾51与当前导出、两insert四终态、COMMIT冲突及撤权403/regrant锁存3/3。返回编辑保留暂存，影响数取COMMIT避免重复；Node20/20、全Web300/300、Chrome fixture12/12、共享runner Document真实3/3、build与独立复核通过。 | 显式安全恢复、完整权限/SQL名称矩阵、物化/字节/总堆预算、完整九模型/三宿主、登录UI/readonly props、安装/AOT/发布。 |
| M47 WB-19/WB-28 FullText Workbench | 🟡 | WB-19迁移已有；WB-28新Web→本机Kestrel Top-K20/100/当前导出、数据库Admin同步重建151权威终态、撤权403/regrant READ锁存真实3/3，成功证据独立落盘及SHA256核验。Node15/15、全Web300/300、Chrome fixture8/8、TypeScript/Vite与独立复核通过；生产组件/Server不改。 | 显式读取恢复、完整权限矩阵/typed全文分页/facet/highlight、服务端扫描/物化/字节/堆预算、登录UI/readonly props、完整三宿主和发行物。 |
| M47 WB-20 Vector Workbench | 🟡 | 原名/六态、请求快照与迟返隔离、401/403及空Schema权限锁存、严格维度、Top-K100、Profile缺失不分派与Measurement子页门禁完成本地切片；Node17/17、全Web201/201、TypeScript/Vite、Chrome10/10、既有导入1/1、真实Kestrel兼容3/3与独立复核通过。 | 索引Profile、真实客户端权限/读取恢复、服务端预算、子页完整写终态、Recall/模型质量、三宿主与发行物。 |
| M47 WB-21/WB-29 KV Workbench | 🟡 | WB-21原名/六态/锁存/readonly与1000项/4096字节预览已有；WB-29新Web→本机Kestrel首轮3/3，真实Scan100/opaque cursor尾51、Get/当前JSONL，普通WRITE三审批NX成功/未应用影响0/交换版本history与管理员Get对拍，撤权403/regrant READ锁存。Node17/17、全Web300/300、Chrome fixture12/12与build通过，三成功JSON/manifest独立落盘，生产组件/Server不改。 | 显式恢复、完整权限/atomic/Int64/TTL/CAS、瞬时原子结果表展示、Base64解码/传输/字节/堆预算、三宿主和发行物。 |
| M47 WB-22 MQ Workbench | 🟡 | 原名/六态、401/403与空身份锁存、实际Topic/ABA隔离、一次审批和unknown、1000条/4096字节预览、Seek25窗/60秒与auto12轮/60秒完成本地切片；Node22/22、全Web240/240、Chrome16/16、既有MQ3/3、真实Kestrel兼容2/2及独立复核通过。 | 新UI真实权限/恢复、metadata/解码/传输/总堆预算、实例恢复、三宿主和发行物。 |
| M47 WB-23 Graph Workbench | 🟡 / Graph Beta | 权限锁存、只读浏览/元素读取/导出、请求代际隔离、客户端 10～1000 总元素画布预算、32项/4096字符 Inspector、safe-number ID 门禁及维护终态校验完成本地切片；Node27/27、Graph Chrome16/16、既有浏览器3/3、真实Kestrel兼容4/4及独立复核通过。 | 完整 Graph Int64 字符串身份、真实新 UI 权限/恢复、服务端长期预算、三宿主、AOT、固定硬件和发行物。 |
| M47 WB-24/WB-25 Object Workbench | 🟡 | WB-24读取权限锁存/六态、实际API与桶/prefix/token/key/version代际隔离、readonly、列表累计1000/Range4096及安全声明区间；WB-25补写终态、审批一次消费、1000项/60秒客户端启动窗口、响应目标与HTTP204核验、unknown/明确失败分离。Node32/32、全Web292/292、Chrome5/5、既有浏览器4/4、真实Kestrel兼容Object4/4+Multipart2/2、build及独立复核通过。 | 完整语义/Multipart/Server资源预算、新UI真实权限/恢复、真实OS对话框、三宿主与发行物。 |
| M47 U01～U05 | 🚧 | 设计、首批共享合同、结果/审批语义及 Web Admin 页面切片已有局部实现。 | 完整九模型适配器、分页/取消/离线组合、真实权限与全量生产旅程。 |
| M47 U06～U08、U10 | 📋 | 已记录规划边界和退出条件。 | Studio、VS Code、WorkBuddy/stdio bridge、manifest/签名/插件安全尚未启动完整验收。 |
| M45-C01 首批实现 | 🟡 | TAG/time 分组首批代码、SQL/EXPLAIN 合同和定向回归已完成。 | C02～C09、更新/删除修正、增量物化、恢复预算及真实性能证据。 |
| M44、M46 及其余 4.5 必选包 | 📋 | 已有设计专题、现存底座和验收边界记录。 | 计划中的 AI 应用、编码/成本优化及对应真实质量、容量、恢复和发布验收尚未启动或未闭环。 |

历史上已经完成且不再作为当前待办的范围，请看 [CHANGELOG 完成归档](CHANGELOG.md#roadmap-completed-archive-2026-10-03-45)；路线图下方的“里程碑总览”保留每个里程碑的剩余交付。

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
| 20 | 多模型 Parity | 🚧 / 历史窗口未过 | 最新候选 light/full 原始证据及七天 scheduled 观察，不由规划推断线上状态。 |
| 29 | Studio 安装与宿主 | 🟡 | 干净 Windows、升级/卸载、WebView2、端口冲突与生命周期。 |
| 35 | 语义内容与多模态检索 | 🚧 | 真实质量、模型换代/回滚、删除同步、容量与硬件；持久摄取/provider 基础不重做。 |
| 36 | 九模型易用性 | 🚧 | 完整真实旅程、远程 parity、取消/恢复、对象分页与 MQ 实例恢复。 |
| 39 | SQL 触发器生产观察 | ⏳ | 已归档研发之外的混合 DML、deferred/outbox 尾延迟及长期 SLO。 |
| 40 | 原生属性图 | 🟡 / Graph Beta | 外部对拍、容量、AOT、Couplet、hard-kill 及长期 gate；AI 图应用不替代底层门禁。 |
| 41 / 42 | 规划器与九模型性能 | 🚧 | 全链路有界读取、总资源预算、页/I/O 成本、冷启动及固定架构/168 小时。 |
| 43 | 十四能力与生态发布收口 | 🚧 | 远程 CDC/schema/冲突、恢复、业务副作用边界、完整旅程及原始报告。 |
| **44** | **AI 应用与可治理推理** | **📋** | 预测/异常、证据 RAG、模型治理、可恢复推理任务与真实效果门禁。 |
| **45** | **聚合与持续计算深化** | **🚧** | C01 首批 TAG/time 分组已完成局部实现；仍需通用 state、更新删除修正、增量物化/rollup 与批流等价。 |
| **46** | **存储编码与执行成本优化** | **📋** | 编码策略、整数/高熵回退、范围解码、统计精度及存储成本。 |
| **47** | **统一数据库管理工作台与三面发布** | **🚧** | WB-00～WB-16 本地切片已提交；Studio客户端、VS Code→Web回选/认证及显式部署base已有本地证据，真实三宿主旅程、九模型、AI/MCP与发布矩阵仍未闭环。 |

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

覆盖关系、measurement、文档可适配数据及增量，不限设备分析。复用 M31 类型、DISTINCT 子集、时间函数、M33 下推/sketch、M37 全量物化、M41 GROUP BY spill 和 M43 持久滚动/滑动/会话窗口。measurement 仍缺 TAG/普通列分组；有 `Merge` 不代表可撤回，持久数值窗口拒绝坏值/NULL 也不自动等价 SQL 忽略 NULL。

| ID | 优先级 / 范围 | 增量交付 | 依赖与退出条件 |
|---|---|---|---|
| M45-C01 | P0 必选 | 类型/NULL/空集/overflow/名称合同，measurement TAG＋时间分组 | 原名/引号及关系兼容；SELECT/HAVING/分组键支持矩阵在执行前校验。 |
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

M47 将 Web Admin、Studio 桌面和 VS Code 扩展规划为“一套核心、三个宿主、三个发布物”。共用资源上下文、九模型能力矩阵、设计令牌、Explorer、Workspace、结果平面、Inspector、History、Approval、MCP/AI onboarding 和验证夹具；宿主分别承载完整治理、原生桌面和开发者 Remote-first 子集。M29 的已有工作台与原型是实现基线，M47 不重新包装已完成的页面为新功能。

对照学习范围包括 dbx、Tabularis、DBeaver、DataGrip、pgAdmin、MongoDB Compass、RedisInsight、Kafka UI/RabbitMQ Management、Milvus Attu/Qdrant Console、Kibana/OpenSearch、MinIO Console 和 Neo4j Browser。逐模型、三面边界、发布形态、MCP/插件安全和原型目录见 [M47 统一管理工作台专题](docs/design/m47-unified-management-workbench.md)。

**M47 设计基线已确认，生产迁移按切片执行。** 外壳、七个一级/二级导航、九模型数据库逻辑资源树、共享对话框/状态和 MQ `database + Topic` 身份已确认；当前 [设计评审包](docs/design/m47-unified-management-workbench/README.md) 仍记录逐页像素与三宿主证据边界。规划覆盖七一级（概览/工作台/观测/数据流/AI 与 MCP/治理/设置）、30 个全局页面、九模型和 166 个任务页签（模型任务 60 个），不等同逐页生产完成。MQ 逻辑作用域在数据库资源树内，物理持久化为实例 `.system/mq`；单库备份不覆盖共享 MQ。

| ID | 优先级 / 范围 | 交付与退出条件 |
|---|---|---|
| M47-U01 | P0 | 竞品矩阵、完整菜单/导航、五区外壳、共享对话框/提示、九模型能力矩阵、三面边界和资源合同设计；先取得用户原型确认，再逐页细化与生产实现，不重复声明 M29 已实现内容。 |
| M47-U02 | P0 | `management-core` 规划与首批实现：typed API/MCP client、资源类型、能力/状态/权限/结果/审批合同、设计令牌；三个宿主各消费一个真实共享合同。 |
| M47-U03 | P0 | 统一 Explorer、Workspace、Result Plane、Inspector、History；九模型上下文一致，分页、取消、截断、离线和局部失败语义一致。 |
| M47-U04 | P0 | 九模型 adapter 和模型专用工作台：measurement、relational、document、KV、MQ、vector、full-text、object、Graph Beta；正常/空/错/只读/长内容状态可验证。 |
| M47-U05 | P0 | Web Admin 完整治理工作台：查询、编辑、导入导出、索引/策略维护、监控、审批、审计和九模型真实旅程闭环。 |
| M47-U06 | P0 | Studio 桌面宿主：同一 Web Admin 资源、native bridge、Managed Local、原生文件对话框以及干净 Windows/WebView2/升级卸载/端口/进程回收证据。 |
| M47-U07 | P1 | VS Code 开发者面：连接向导、可复用 Query/Notebook、分页结果、LSP/EXPLAIN、稳定宿主 Chat/MCP 能力或保留自有 Copilot WebView，并可深链接转交治理面。 |
| M47-U08 | P1 | WorkBuddy/Claude/Cursor/Codex AI Connect：现有 HTTP MCP、`sonnetdb mcp` stdio bridge、配置生成/自检、只读工具、凭据隔离、结果预算和数据外发说明。 |
| M47-U09 | P1 | M47 原型目录、完整页面/任务规范与三面发布矩阵：共享外壳、查询结果、审批/AI、九模型、Studio、VS Code 原型及正常/空/加载/错/只读/离线/长内容状态；HTML 评审稿、逐页最终视觉与真实宿主证据分别记录。 |
| M47-U10 | P2 条件 | manifest、签名/哈希、插件/连接器目录、权限撤销、升级/回滚和市场提交流程；先完成核心工作台和安全评审再启动。 |

### M47 实施状态索引（2026-10-06）

状态只反映当前本地证据：`🟢` 已完成本地切片，`🚧` 仍有实现或验收，`🟡` 指定切片完成但外部/宿主证据待补，`📋` 尚未启动。

| 范围 | 状态 | 当前证据与剩余边界 |
|---|---|---|
| WB-00～WB-03C | 🟢 | 设计基线、原型交互/页面合同、资源身份/能力合同与 Explorer 兼容证据已提交；静态/内存证据不等同真实 Server 或三宿主。 |
| WB-04～WB-04B | 🟢 | 导航与 Explorer→SQL 兼容预检已提交；迁移前证据不等同全量运行时验收。 |
| WB-05～WB-06 | 🟡 | 结果/草稿/历史/审批和生产外壳/七模块导航已完成本地切片；全量模型、真实宿主、安装、发布仍待补。 |
| WB-07～WB-10 | 🟢 | Explorer 资源身份、旧深链接回选、database 上下文及手动切库投影已提交并通过 Web 回归。 |
| WB-11 Measurement Workbench | 🟡 | 用户已确认页面基线；状态、动作、旧路由和 UI 证据已通过本地验证，提交为 `390ff526`。真实 Server、三宿主、安装、发布和全量九模型证据仍待补。 |
| WB-12/WB-18/WB-26 Web Admin Document Workbench | 🟡 | WB-12 `54c78755`、WB-18 `1d9465fb`为既有基线；WB-26取得本机新Web→真实Kestrel权限/Find100恢复、Aggregate1001→1000及Distinct501→500/1000unknown真实3/3，Node21/21、全Web295/295、fixture11/11与build通过。空IDs省略且显式IDs保留；登录UI/readonly props、Server预算、Advanced完整读写及三宿主另验，提交以最终完整门禁为前提，实际哈希见git log。 |
| WB-13 SonnetDB Studio 宿主合同 | 🟡 | 宿主身份/URL与Managed Local生命周期合同已提交 `74835847`，定向34/34、独立复核和完整提交门禁通过；Web消费由WB-15另验，干净Windows/WebView2、安装与发行物证据仍待补。 |
| WB-14 VS Code Workbench 合同 | 🟡 | 九模型资源/深链接入口已提交 `e7cf2fe5`，原名与旧key保留，Node20/20、本机Extension Host、独立复核与完整门禁通过；WB-16补浏览器回选/认证夹具，真实Server权限和VSIX仍另验。 |
| WB-15 Studio Web客户端合同消费 | 🟡 | 已提交 `e1f93a69`；宿主原名身份、完整生命周期、保守操作门禁、初始化/状态/保存迟返与目录ABA隔离；Node10/10、全Web137/137、Studio定向40/40、TypeScript/Vite、浏览器夹具8/8、独立复核和完整提交门禁通过；真实Server/桌面安装及三宿主整体仍另验。 |
| WB-16 VS Code→Web导航/认证 | 🟡 | 已提交 `0bb628ad`；九模型/index/backup最终回选、登录返回、显式base/SSE、同名MQ及index分组收口；Node9/9、全Web146/146、根/代理构建、浏览器各17/17、真实Host13节点命令调用、独立复核与完整门禁通过。真实Server/代理部署/远程SSE/同origin存储隔离/VSIX另验。 |
| WB-17 Relation Table Workbench | 🟡 | 已提交 `f1978263`；Node15/15、全Web161/161、TypeScript/Vite、Chrome12/12、既有设计器2/2、独立复核及完整代码门禁通过；readonly浏览/结果导出/纯DDL，HTTP403清载荷锁存，缺终态/断连/408/5xx为unknown。真实Server权限、预算、三宿主与发行物另验。 |
| WB-19 FullText Workbench | 🟡 | 已提交 `2f9a5477`；原名/六态/Top-K100、载荷清理与一次审批终态，Node15/15、全Web184/184、Chrome8/8和既有真实Kestrel兼容4/4。新客户端权限/写旅程、恢复、预算与三宿主另验。 |
| WB-20 Vector Workbench | 🟡 | 原名/六态、参数/身份快照、空Schema权限锁存、严格维度/Top-K100和子页门禁；Node17/17、全Web201/201、Chrome10/10、导入1/1和既有真实Kestrel兼容3/3。Profile、真实权限/恢复、服务端预算、子页写终态与三宿主另验；本轮提交哈希见git log。 |
| WB-21/WB-29 KV Workbench | 🟡 | 原名/权限锁存/readonly与有界预览已有；新Web→本机Kestrel首轮3/3补游标/Get/当前JSONL、NX成功/未应用影响0/交换实际版本+history、撤权403后READ/同token刷新锁存。Node17/17、全Web300/300、Chrome fixture12/12和build通过，成功JSON+manifest独立落盘；完整atomic/恢复/资源预算与三宿主另验，实际提交见git log。 |
| WB-22 MQ Workbench | 🟡 | 原名/六态、锁存/readonly、实际Topic同步代际、一次审批与unknown、Browse1000/Inspector4096、Seek25窗/60秒和自动采样12轮/60秒；Node22/22、全Web240/240、Chrome16/16+既有3/3、真实Kestrel兼容2/2。真实新UI权限/恢复、完整metadata/资源预算和三宿主另验；本轮提交哈希见git log。 |
| WB-23 Graph Workbench | 🟡 / Graph Beta | 原名/六态、401/403锁存、readonly与请求隔离、10～1000客户端总元素、32项/4096字符Inspector及safe-number门禁；Node27/27、全Web260/260、Chrome16/16+既有3/3、真实Kestrel兼容4/4与独立复核通过。完整Int64字符串、新UI真实权限/恢复、服务端资源预算和三宿主另验；提交说明见git log。 |
| M47-U01～U03 | 🟡 | 设计、首批合同和共享结果/审批语义已有局部实现；三个宿主真实消费、完整分页/离线/取消证据仍待补。 |
| M47-U04～U05 | 🚧 | 九模型专用工作台和 Web Admin 仍按页面切片迁移；WB-11 是其中一个页面样板，不代表整包完成。 |
| M47-U06～U08 | 📋 | Studio、VS Code、WorkBuddy/stdio bridge 与配置自检尚未形成完整真实宿主验收。 |
| M47-U09 | 🟡 | 原型目录、页面规范与发布矩阵已提交；逐页最终视觉、三面真实旅程和发布证据仍待补。 |
| M47-U10 | 📋 | 条件项，尚未启动。 |

逐个 WB 的文件归属、测试数量、提交哈希和剩余边界以 [Workbench 队列](docs/design/m47-unified-management-workbench/work-queue.md) 与 [HANDOFF](HANDOFF.md) 为准；本表用于路线图快速查看，不能替代两份交接记录。

M47 的代码边界是“共享合同和组件优先、宿主适配器隔离”：不把 VS Code 变成完整 Web Admin，不改变九模型存储语义、SQL 名称合同、MCP 只读边界或 M29 的写审批规则。三面分别产出 Web 静态资源、Studio 安装包和 VSIX，但使用同一版本、MCP contract version、能力清单和兼容矩阵。任何一个宿主未通过自己的安装、Electron/Extension Host 或真实 Server 旅程，不能把三面整体标为发布完成。

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
| M20 #136 | 最新候选 light/full 原始 backend/artifact、七天 scheduled | 保存的 9/25 三成功四失败，9/26 候选复验通过；本轮无新线上结果。 |
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

见 [2026-10-03 4.5 整理归档](CHANGELOG.md#roadmap-completed-archive-2026-10-03-45)及[原归档](CHANGELOG.md#roadmap-completed-archive-2026-09-21)，不关闭本文保留的远程/真实质量/容量/恢复/长期任务。

## 历史链接兼容锚点

旧链接保持可达，当前计划以本文工作包、真机待办和 CHANGELOG 证据范围为准。

<a id="milestone-12--函数与算子扩展pid--forecast--udf"></a>
<a id="milestone-17--可观测性与运行时可见性-observability--runtime-visibility"></a>
<a id="milestone-18--vs-code-数据库扩展sonnetdb-for-vs-code"></a>
<a id="milestone-19--生态适配底座能力关系--kv缓存--对象桶--大量-measurement"></a>
<a id="milestone-19--生态适配底座能力关系--kvcache--对象桶--大量-measurement"></a>
<a id="milestone-20--多模能力对齐与平移测试-parity"></a>
<a id="milestone-24--sonnetdb-studio-管理体验升级document-管理面"></a>
<a id="milestone-25--document-store-验收文档与发布治理"></a>
<a id="m40-修复与发布执行顺序2026-08-23-复盘"></a>
