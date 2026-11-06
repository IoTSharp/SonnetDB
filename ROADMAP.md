# ROADMAP — SonnetDB 4.5 与后续里程碑

## 2026-11-07 分支整合后的执行顺序

PR #413 已修复审查发现的超时后提交、Windows 挂载卷及只读容量查询重建目录问题，并在最终精确源码验证后合入 main；已合入切片不重新立项。下一阶段由新监督会话按[共同 Provider 三线并行合同](docs/handoffs/provider-next-phase-20261107.md)接续第一顺序，逐项状态及后续见 [PR #413 审查与 issue 矩阵](docs/audits/pr413-integration-20261107.md)。

2026-11-07：三位独立worker完成共同候选`5.1.0-rc.20261107.1`/source`eb5c0e5f`的消费者增强和独立复核；本地四包/JIT与实际CI四包/NativeAOT Server各69步骤PASS，三协议、after-ACK强杀/Down/Up及Server重开齐全，12类发行readiness READY。证据见[发行评审/机器清单](docs/releases/provider-candidate-20261107.md)及[迁移](docs/audits/provider-candidate-20261107-migrations.md)、[JSON](docs/audits/provider-candidate-20261107-json-update.md)、[集合](docs/audits/provider-candidate-20261107-ownsmany.md)专题；正式发行延期及公开门保持，母issue不提前关闭。下一独立SDK切片冻结为typed GetAndDelete。

| 顺序 | 接续范围 | 下一份可验收交付 |
| --- | --- | --- |
| 1 | #392 / #395 / #394：共同 Provider 候选 | 从全部整合后的最终 source 冻结包与匹配 Server，重验库存迁移/回退、补货 JSON 更新及 OwnsMany 生命周期。正式发布已延期；恢复发行后以公开新包及独占空缓存复验，满足后关闭。 |
| 2 | #397：M49 SDK | 在 typed KV Get/Put/Scan 基础上冻结下一操作/模型/语言包，补真实原生 HTTP/2、TLS/ALPN、取消/未知写入、ABI、重开及公开消费者。 |
| 3 | #230 / #404：上游 Provider | 接续 FreeSql #2291、XCode #104→#105 的实际审查/合入，公开 Core/Provider 后验证全公开组合；候选与上游发行分别记账。 |
| 4 | #396 / #402：AOT 与发行恢复 | EF 固定静态查询应用取得预编译/NativeAOT 的首个具体缺口；恢复流程先补可信 attempt 来源与双 verifier，再做受控预发布失败恢复。 |
| 5 | #398 / #399 / #403：独立环境验收 | 先落实干净 OS、固定硬件/阈值、正式 HTTPS/WorkBuddy 宿主与平台入口，再分别执行安装生命周期、原规模容量/长稳、九模型及送审公开闭环。 |
| 6 | #401：M50 高精度时序 | T01 冻结独立版本/API/矩阵→T02 精确换算→T03–T06 持久元数据/格式/WAL/恢复→T07–T11 查询/分桶/retention，再接协议、语言、迁移与固定硬件。 |

2026-11-07下一独立切片：typed GetAndDeleteKvAsync复用既有op6并保留旧记录及变更版本，新增codec44例、完整Client272例及真实产品Server7例本地通过；独立Client/Protocol候选与包应用继续验证，TLS/ALPN和公开消费不外推。详见[#397专题](docs/audits/issue397-get-and-delete-20261107.md)，不修改已冻结的Provider候选。

这 12 个母 issue 仍有未完成的公开、上游或实机门禁；#367/#393/#400/#407 已关闭。原 `0d687bd9` 5.1 候选的 PASS 不外推到新源码。Mulei 后续仍保留 FIFO 长尾、中心超时/积压、统计刷新取消、历史缺图及完整长稳；本次不执行生产操作。

2026-11-04：**[M51：WorkBuddy生态集成](#milestone-51)**当前连接器/Skill合同更新为0.2.0，面向SonnetDB 5.0的43工具发现、默认Read和用户明确授权的Write。生产切片49731955保持冻结，不重复开发；历史Docker/r2九工具证据保留source。平台解析、HTTPS、宿主验收与正式送审仍未完成，由统一发布流程逐项整合。

2026-11-02：新增 **[M50：时序时间戳精度与无损时间链路](#milestone-50)**，按库真正保存 `ms/us/ns`、默认 `ms`，规划 30 项必选与 2 项条件任务；全部 `📋 planned`。完整实现思路、源码归属、依赖/验收、迁移/旧端拒绝与初步工作量见 [M50 专题](docs/design/m50-timeseries-timestamp-precision.md)。M50 独立冻结版本与批次，本次仅更新规划，不启动源码改造或变更现有 4.5/预览/连接器批次范围。

2026-10-16：按用户确认修订 **[M49：连接器/嵌入式拆分、九模型访问与有界批处理](#milestone-49)**。远程各语言原生 HTTP/2 二进制 Frame，嵌入式 .NET 托管与非 .NET 共用 AOT；VB6/PureBasic 仅远程。首批只做九模型数据访问、先64位、Java8兼容、直接重构不留旧兼容层，允许分批正式发行。D01/F01/C01～C08/R01～R05 共15包，首片已实施、均未整体完成；详见 [M49 专题](docs/design/m49-connector-analytics-bounded-batches.md)与[确认需求](docs/design/connector-embedded-split-requirements-20261016.md)。

2026-10-17：F01 关系 SQL 写入首片已接通新 Client→真实 HTTP/2→Server：Execute=2/payload v1、精确参数、类型化 NULL、单条自动提交、完整终态与未知提交无重试。旧 ADO 迁移等待多结果/事务合同；九模型 F01 与 R01～R03 仍未关闭。见[协议](docs/sql-execute-frame.md)和[换机入口](docs/handoffs/m49-next-computer.md)。

M49-D01 共享协议与原生 .NET Frame Client 首片已完成：Protocol 程序集脱离 Core，Client 只依赖 Protocol；真实 h2c、取消、关闭、预算和干净包消费者证据见 [D01 审计](docs/audits/m49/d01-20261016/README.md)。D01/F01/R01～R03 仍未整体完成，换机接续见 [M49 交接](docs/handoffs/m49-next-computer.md)。

2026-10-16：新增 **[M48：单机日志与事件分析](#milestone-48)**，将 ELK 核心日志场景落实为可靠摄取、联合检索/统计、磁盘访问、生命周期、日志探索和规则告警的增量规划。`M48-L01`～`L20` 为 R1 必选，`L21`～`L24` 为条件候选，当前全部 `📋 planned`；完整字段/确认/查询/恢复合同、24 个工作包、阶段退出与验收矩阵见 [M48 详细设计](docs/design/m48-single-node-log-event-analytics.md)。R1 的目标版本与日期在 L01 冻结，不将全部 M48 自动追加为既有 4.5 正式版或已发布 Workbench 预览的门槛。

2026-10-16：第二批 [十项产品切片](docs/audits/implementation-batch-02-20261016.md)已完成本地实现：GH-Issue #226 存量主键列重命名，#231 持久序列/原子取号/逐行插入，#237 表列注释，#240 字符串长度声明/演进/元数据，以及 #243 有主键和复合主键有界删除。扩大 Core 3157 项、Server SQL/ADO/Frame 412 项（含真实 REST/h2c，本批新增 18 项中 17 项为真实网络）及实际强制终止恢复 2 项回归通过，最终冻结复核与本地提交见根 HANDOFF。表目录含新属性时写 v12，旧引擎拒绝；未改行或键编码。#226 主键类型/定义、#227 DDL 事务、#232/#233/#236 及无主键依赖继续排队；正式发布及 ORM 消费者验收另计，不能由这些切片关闭整个 Issue。

2026-10-15：接收最新 main `c7b307f6`，首次核对 46 条开放 Issue，最终快照 48 条（新增 #312/#313/#314，关闭 #273），清理两个已合并本地分支。本批 [十项实现任务](docs/audits/implementation-batch-01-20261015.md)已完成本地实现：GH-Issue #224 的 HAVING/复合投影/排序分页，#229 多结果 ADO，#257 全表关系 UPDATE，以及 M45/M46 的 Int64 众数/近似去重和三项聚合状态校验。最终 Core 2938 项、真实 REST/h2c 167 项回归通过；实际提交和远端 Issue 闭环见根 HANDOFF，不重做已合入的 #219～#223/#225/#228，不把局部任务提升为整个里程碑、正式发布或下游 ORM 完成。

更新日期：2026-11-03（Asia/Shanghai）。**Workbench 继续按 Web 预览后续、Studio 与 VS Code 各自门禁推进；M48 单独冻结日志 R1 范围。** 首版预览不代表 SonnetDB 4.5 正式版或 M47 三面整体完成，候选12的 P07/P08 已完成；[4.5.0-preview.1.12](https://github.com/IoTSharp/SonnetDB/releases/tag/workbench-preview-1.12) 已公开，匿名下载与Chrome/Edge本机首跑通过。

根路线图只保留剩余交付、依赖和验收顺序。已完成实现、受限真实旅程以及旧窗口记录已归入 [CHANGELOG：2026-10-10 完成范围归档](CHANGELOG.md#roadmap-completed-archive-2026-10-10-workbench)；详细原始证据仍在 [M47 验证记录](docs/design/m47-unified-management-workbench/validation-report.md)。后续不按 WB 编号重新实施已完成功能。

## 当前执行顺序：Workbench Preview 1

| 顺序 | 交付 ID | 目标 | 依赖 / 当前状态 |
|---|---|---|---|
| 1 | M47-P01 | 冻结预览范围、已提交候选、版本与发布物白名单 | **🟢 范围冻结完成，产品仍 NOT_READY**。[范围/入口控制](docs/design/m47-unified-management-workbench/preview-1-scope.md)、[候选01](docs/design/m47-unified-management-workbench/preview-1-candidate-01.json)、[独立分发合同](docs/design/m47-unified-management-workbench/preview-1-distribution.md)已记录；入口与分发实现仍阻断。 |
| 2 | M47-P02 | 定位 Web smoke 失败并取得候选通过证据 | **本地验收完成；远端门禁 NOT_RUN**。[P02报告](docs/design/m47-unified-management-workbench/preview-1-p02-smoke.md)、[候选02](docs/design/m47-unified-management-workbench/preview-1-candidate-02.json)：build、448合同、307fixture与12宿主回归通过，25默认skip分列；产品NOT_READY。 |
| 3 | M47-P03 | 首版真实登录、授权、预算和终态 | **R1本地完成；完整模式四片307通过/25原skip**：[P03报告](docs/design/m47-unified-management-workbench/preview-1-p03-controls.md)、[范围修订](docs/design/m47-unified-management-workbench/preview-1-p03-scope-revision.md)。七模型专用读取延期，原九模型承诺未全部验收；KV/measurement视口渲染与完整/选中导出回归通过；[候选04](docs/design/m47-unified-management-workbench/preview-1-candidate-04.json)固定`57222f57e4cc` / 4.5.0-preview.1.4，产品NOT_READY。 |
| 4 | M47-P04 | 生成来源明确的 Web/Server 预览发行物 | **候选12 P04通过**：4.5.0-preview.1.12/source5bcd3b81、38145580702 attempt1 success，290项库存/hash/NativeAOT身份独立通过；旧失败保留。 |
| 5 | M47-P05 | 用实际发行物验首次启动、部署与用户旅程 | **候选12本机R1实物通过**：四阶段37记录、五类拒绝、快照恢复/导出和17回执244身份非空审计通过；[冻结记录](docs/design/m47-unified-management-workbench/preview-1-candidate-12.json)。 |
| 6 | M47-P06 | 完成预览说明、已知限制、升级/回退和反馈入口 | **候选12真实四附件prepare通过**：独立原archive digest、附件/ZIP核验PASS；[分发冻结](docs/design/m47-unified-management-workbench/preview-1-distribution-12.json)保持原P04包字节。 |
| 7 | M47-P07 | 按现有发布门禁核同一候选并作放行评审 | **✅ 完成：更新分发工具后P07 READY，12/12门禁、8/8验收**：[候选12评审](docs/design/m47-unified-management-workbench/preview-1-p07-12.json)，原工具/审批策略历史保持。 |
| 8 | M47-P08 | 发布、独立回读下载结果并归档首版 | **✅ 已发布，匿名四附件/290项库存及Chrome/Edge本机首跑通过**：[4.5.0-preview.1.12](https://github.com/IoTSharp/SonnetDB/releases/tag/workbench-preview-1.12)、[P08记录](docs/design/m47-unified-management-workbench/preview-1-p08-12.json)。maikebing已实际自批，正常停止/独立进程审计通过；只限Windows R1 Web预览。 |

具体范围、每步退出条件、宿主后续与失败处置见下方 [M47 首个预览版路线](#workbench-preview-1)。P01候选01来源固定为 `c40fa670e3a170c5fac8cefa181d618631862270`，保留版本 `4.5.0-preview.1.1`；仅为已提交实现起点，未构建/验收/打标签。后续代码、配置或打包变更须新增候选与版本，不覆盖该清单、不用 dirty 工作树发包。首版限定 win-x64 同源环回 Web＋Server；旧 `workbench` 定时保持 `PAUSED`。 后继候选02已绑定修复提交 `ed4f48242c5d5b4e7e16861504d03cc845146402` 与保留版本 4.5.0-preview.1.2；本地验收通过，未打包/推送/发布，P07须重核最终候选的全部门禁。

4.5 后续仍推进 AI 应用、聚合/持续计算、存储编码及跨模型成本、恢复、容量与真实质量，按本文件 M44～M46 和 V45 工作包执行；这些增量不自动成为单独 Workbench Web 预览的先决任务。已撤回的系统性能原始报告不作为验收依据。完整背景见 [路线历史](docs/roadmap-history.md)、[4.5 规划核查](docs/audits/sonnetdb-45-roadmap-planning-20261003.md)、[九模型证据](docs/audits/nine-model-capability-evidence-20260905.md)和[十四能力索引](docs/audits/fourteen-capability-evidence-index.json)。


## 完成判定

1. 代码必须存在且由真实产品入口调用；原型、未调用类型和文档计划不算功能交付。
2. 实现合同、本地回归、真实服务、固定硬件、真实模型、长期运行及远程发布分别记录；一个层级的 PASS 不升级其它层级。
3. 新增工作统一为 `📋 planned`。`M44-Axx`、`M45-Cxx`、`M46-Sxx`、`M47-Pxx`、`M48-Lxx`、`M49-Cxx/Rxx/D01/F01`、`M50-Txx`、`M51-Wxx`、`V45-Xxx` 是规划 ID，不是已创建的 GitHub issue 或 PR。既有内部 `#N` 与外部 `GH-Issue #N` 保持区别。
4. P0 为基础合同与优先交付，P1 为本版深化与验收，P2 为条件候选；只有明确必选范围参与 4.5 完成判定，条件项未经独立评审不成为版本承诺。
5. 质量、性能与资源阈值在实现前绑定语料、版本、机器及基线冻结；未校准保持 `NOT_READY`，不以自造数据或缩规模 PASS 代替真实效果。
6. Core 保持 Safe-only、零第三方运行时依赖、source-generated JSON、Native AOT/trim 零相关警告及 public API 中文 XML 文档。API/帧保持兼容；新落盘格式必须版本化、拒绝不兼容旧 writer，提供迁移或明确拒绝及恢复方案。SQL 名称遵守 GH-Issue #211。

状态：`🟢` 当前范围已完成 / `📋` 规划或尚未启动 / `🚧` 有剩余实现或验收 / `🟡` 指定本地切片完成、外部证据待补 / `⏳` 未执行 / `❌` 已执行失败。历史完成范围在 CHANGELOG，主路线只列待办。

## 4.5 目标与范围

**把九模型从可组合的数据能力推进到有真实效果、有资源边界、可恢复的 AI 与分析应用底座。** 应用覆盖工业诊断、业务经营分析、文档知识与资产检索；工业是重点场景，产品保持通用多模型引擎定位。

本版必选：M44-A01~A07、M45-C01~C09、M46-S01~S09、M47-U01~U09，以及 V45-X01~X06、X08 中注明的有界合同和适用验收。M44-A08/A09、M45-C10、M47-U10、V45-X07 是条件项；M44-A10 是后续候选。每包拆成单一职责 PR，不将完整研究集合塞入一个实现。

M48 已纳入后续增量路线，R1 必选 L01～L20 与条件 L21～L24 分开验收；它复用本版底层合同，但不改变上述 4.5 必选集合。M48 的版本/平台/负载与发布时间须在其 L01/L20 独立冻结，不以“单机”降低可靠性、权限、预算或恢复要求。

本版不纳入 TsFile 适配、导入导出或替换持久格式。参考其它产品机制后按 SonnetDB 当前存储、类型、事务和嵌入式边界独立改进，不以复制语法、算法数量或商业功能清单作为完成条件。完整分片集群、九模型分布式事务、任意外部副作用 exactly-once、完整 SQL/GQL/S3 兼容均不由本路线自动承诺。

M49 按用户确认拆为原生 Frame 远程与托管/AOT 嵌入式两条线；首批只做九模型数据访问，其它平台能力后续，允许分批正式发行。C01 冻结每批语言/64位平台/操作和适用门槛，不等待整个目标矩阵；复用 V45-X01/X02、M46 的预算与有界合同，不降低既有4.5引擎门槛或重开已发布预览。VB6/PureBasic 仅远程，Java8兼容，不保留旧 SDK/API/ABI 兼容层；外部对象存储后端仍暂缓。

### 代表产品研究与采纳原则

按产品家族覆盖主流关系、分析、时序、文档、缓存、搜索、向量、图和持续计算产品。官方来源、版本/商业边界及未核实项写在专题中；不声称穷尽所有产品或已完成同条件性能对照。

| 产品家族与代表 | 学习机制 | SonnetDB 采纳方向 |
|---|---|---|
| PostgreSQL / MySQL / SQLite | 聚合类型/NULL、事务/WAL、快照、统计及代价可见性 | 精确语义、资源指标与恢复；不复制全部方言，未取得正文的项保持待核。 |
| DuckDB / ClickHouse | 批执行、聚合 state、并行合并、统计跳读与列式成本 | 在 M33/M41 深化，兼顾嵌入式及多模型并发。 |
| TDengine / IoTDB / TimescaleDB | 设备/维度分析、时间函数、rollup、迟到修正与持续聚合 | TAG 分组扩展为通用聚合/增量计算，区分静态属性与观测。 |
| InfluxDB / QuestDB / VictoriaMetrics / DolphinDB | 窗口、ASOF、采样/填补与流聚合资源取舍 | 逐项核对已有函数；内存态流聚合不等同持久 event-time 计算。 |
| ArcticDB | 面向分析用户的数据批读写、类型/版本可见性、时间/列裁剪及原生处理 | M49 将体验推广到全部已实现连接器，复用 M41/M42/M46；现有 schema/稀疏/编码不重做，外部对象存储暂缓。 |
| Flink / Materialize / RisingWave | merge/retract、event time、检查点与增量物化 | 复用 M43 任务/投递，首批限定可证明等价的 SQL 子集。 |
| MongoDB / Redis | 管道聚合、索引、持久性取舍与状态 | 文档/KV/MQ 参与应用，不伪称跨模型原子事务。 |
| Elasticsearch / Qdrant / Weaviate / pgvector | 混合召回、过滤、重排、相关性与向量工作集 | 延续 M35/M36 的索引与 RRF，强化真实语料、ACL、删除同步。 |
| Elasticsearch / Logstash / Kibana / OpenTelemetry Collector | 可靠采集与处理、倒排/统计列分工、数据流滚动、固定视图分页、日志探索与规则状态 | M48 复用九模型和 M43/M45/M47 形成单机日志旅程；先验证通用 HTTP 接入，不承诺完整 Elasticsearch/Kibana/OTLP 兼容。 |
| Neo4j GraphRAG / TDgpt / IoTDB AINode / pgai | 图证据、时序推理、模型调用与版本 | 已有预测/RAG/GraphRAG/provider 推进到受治理应用。 |
| Snowflake Cortex / Databricks 等 AI 平台 | 质量/成本、批量推理、评测与应用闭环 | 学习治理与旅程；托管商业功能不算开源引擎现状。 |
| dbx / Tabularis / DBeaver / DataGrip / pgAdmin / Compass / RedisInsight / Kafka UI / MinIO Console / Neo4j Browser | 统一 Explorer、SQL/Notebook、结果平面、模型专用工作台、MCP/插件和发布分发 | 由 M47 统一三面工作台；复用交互语义与安全边界，不复制通用多库协议或商业功能清单。 |

来源与选择理由见 [AI 专题](docs/design/sonnetdb-45-ai-applications.md)、[聚合专题](docs/design/sonnetdb-45-aggregation-continuous-compute.md)、[存储专题](docs/design/sonnetdb-45-storage-performance.md)、[M47 统一管理工作台专题](docs/design/m47-unified-management-workbench.md)、[ELK 分析](docs/design/elk-single-node-capability-analysis-20261016.md)、[M48 详细设计](docs/design/m48-single-node-log-event-analytics.md)和 [M49 连接器专题](docs/design/m49-connector-analytics-bounded-batches.md)。采纳代码前逐组件核查许可：TDengine AGPL 核心不可直接搬入继续只标 MIT，其他许可也保留相应声明，不从 SDK 许可推断全产品许可。

## 里程碑总览

| Milestone | 主题 | 状态 | 剩余工作 / 版本归属 |
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
| **47** | **统一数据库管理工作台与三面发布** | **🚧 / Web Preview 1已发布** | P01～P08 Web预览已收尾，再补Studio/VS Code；正式版共享合同、九模型完整权限/预算/恢复、AI/MCP与逐页矩阵继续验收。已完成切片见CHANGELOG。 |
| **48** | **单机日志与事件分析** | **📋 planned** | 独立增量 R1：L01～L20 覆盖接入、检索/统计、容量、生命周期、Web 排障与告警；L21～L24 为 OTLP/Trace/语义/生态条件项。版本与时间待冻结，功能未实施。 |
| **51** | **WorkBuddy 生态集成与 SonnetDB 全生命周期闭环** | **🚧 基线已有 / 后续 📋 planned** | 已有只读连接器/内置 Skill、三份公开资料及 Docker 分层证据；0.1.1 平台解析、真实宿主、九模型 MCP 缺口、可执行运维、独立 Skill/专家/Buddy 应用与分阶段审核发布待做，详见 [M51](#milestone-51)。 |

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
| M45-C01 | P0 必选 | 扩展已交付TAG/time分组的支持矩阵，统一类型/NULL/空集/overflow/名称边界 | 2026-10-15 续作 HAVING/复合投影/排序分页与 Int64 mode，见[十任务验收](docs/audits/implementation-batch-01-20261015.md)；剩余预算、完整类型/空集统一仍独立推进。 |
| M45-C02 | P0 必选 | 版本化聚合 state 与 Add/Merge 复用 | 分别声明结合/交换/顺序敏感性；COUNT/SUM merge 不冒称幂等，投递去重在输入身份层。 |
| M45-C03 | P0 必选 | 支持源的 before/after、retract、更新/删除修正 | 非可逆 MIN/MAX/sketch 有界重算或拒绝；缺 before-image 不静默按追加处理。 |
| M45-C04 | P0 必选 | 有限 SQL 的增量物化/rollup 与有界回填 | 复用 generation/位点/调度；快照增量无缺口、失效重算、原子发布与依赖变更。 |
| M45-C05 | P1 必选 | SQL 批流窗口及分区 event-time 推进 | 复用窗口；乱序/迟到/idle、水位回退/关闭及回填可对拍。 |
| M45-C06 | P1 必选 | 精确/近似分位、distinct/sketch 准确性和资源 | 2026-10-15 修正 Int64 distinct_count 输入身份及旧 HLL 回退；复用 TDigest/HLL，完整误差/merge/偏态/种子与资源验收仍待补齐，不冒称精确结果。 |
| M45-C07 | P1 必选 | 时间加权、率、积分、采样/填补边界和 state | 核对已有函数；边界点、重复时间、counter reset、缺失及最大跨度。 |
| M45-C08 | P1 必选 | 聚合 state 预算、落盘及恢复 | 2026-10-15 增加 TDigest 权重/排序/计数、HLL rank 与 Histogram 极值状态守卫；高基数准入、spill/V45-X02、取消/磁盘满及完整恢复仍独立验收。 |
| M45-C09 | P0 必选 | 批流、内存/落盘、跨端对拍和观测 | 同事件日志离线/增量等价；重投/更新/删除/重开，计数精确、浮点误差有声明。 |
| M45-C10 | P2 条件 | ROLLUP/GROUPING SETS、扩展窗口与 ASOF | 真实查询驱动单项选择；CUBE/count/state/change windows/ASOF 不全部默认纳入。 |

冻结首批源、AST、聚合及变更种类；不支持的 JOIN/递归/UDF/事务在计划阶段拒绝。M45 负责计算语义与状态，M43 负责运输、位点、订阅及任务生命周期。详见 [M45 专题](docs/design/sonnetdb-45-aggregation-continuous-compute.md)。

## Milestone 46 — 存储编码与执行成本优化（压缩深化）

M46 把现有可选 codec 收敛为可验证的自适应压缩路线：一级按类型做无损编码，二级按 Block 选择可选的通用压缩，编码膨胀时回退 raw，并同时测量完整物理文件成本、解码工作量、读写放大和恢复代价。当前 `SegmentWriterOptions` 的时间戳和值编码默认均为 `None`；现有 V2 为 Timestamp delta-of-delta + ZigZag varint、Float64 简化 Gorilla XOR、Boolean RLE、String 字典；2026-11-05 GH-Issue #367 增加显式 Int64 的独立v7 RLE及raw回退，旧V2 Int64仍为raw 8B，详见[实际文件与恢复审计](docs/audits/issue-367-20261105.md)。Vector/GeoPoint仍raw，WAL仍是逐条原始payload + CRC。v6起已内嵌HNSW/sketch；mmap仍有复制，解码缓存按reader设置，共享HNSW缓存及KV I/O预算另有实现。不能把此切片当作S02/S09完整交付，或把可选编码、局部压缩率、mmap与局部额度写成默认压缩、zero-copy或进程总内存上限。

| ID | 优先级 / 范围 | 增量交付 | 依赖与退出条件 |
|---|---|---|---|
| M46-S01 | P0 必选 | 冻结真实语料、Block 大小、raw/legacy V2/候选 Auto、持久性与收益门槛 | 同一 commit、硬件、配置下记录 payload、header、字典、checkpoint、index、CRC、HNSW/sketch、WAL 和临时文件；默认是否切 Auto 由完整证据决定。 |
| M46-S02 | P0 必选 | 完整 Int64 自适应编码：RLE、ZigZag/delta、delta-binary-packed/FOR、bit-pack 与 raw fallback | 覆盖完整 Int64 值域、差分溢出、counter reset、重复、乱序、随机高熵和 Block 边界；保留 v6 `DeltaValue` 的 raw Int64 含义，不偷改旧标志。 |
| M46-S03 | P0 必选 | Float64 canonical Gorilla window reuse 与 Boolean RLE/bit-packing hybrid | 保留 NaN 位模式、±0、Infinity；高熵浮点和交替/随机 Boolean 必须可回退；CHIMP/ALP 只作为有同语料证据的候选，不默认量化或有损压缩。 |
| M46-S04 | P1 必选 | 有界 String dictionary、bit-packed index、稀疏 presence 与大值回退 | 字典条目数、总字节和构建工作集有上限；Unicode、空串、NULL、缺失、false/零值分开；超预算回退或拒绝，不截断、不合并语义。 |
| M46-S05 | P1 必选 | Timestamp 自适应编码、restart checkpoint、分块与范围解码 | 在 delta-i、delta-d、RLE、Simple8b/bit-pack、varint、raw 之间按完整成本选择；窄读、跨 checkpoint、全扫描分别测实际 I/O、CPU、分配和读放大。 |
| M46-S06 | P0 必选 | 统计跳读精度 guard | 2026-10-15 对 Int64 distinct_count 拒用旧 double 来源 HLL 并扫描原块；其它统计精度、剪枝差分及旧段回读继续逐项验收，必要时关闭不安全快路径。 |
| M46-S07 | P1 必选 | 解码分配、缓存、预算与读成本接线 | 接 V45-X01/X02；租约、取消、淘汰、mmap 复制、解码缓存和共享 HNSW 竞争可观测，不新造平行资源管理器。 |
| M46-S08 | P1 必选 | 二级 LZ4/Zstd 边界、WAL 批压缩、small-segment 与 compaction 成本 | 热路径与冷 compaction 分开测；Core 零第三方运行时依赖、AOT、许可和平台边界单列；写放大、fsync、临时空间、合并读尾延迟、WAL replay 和故障恢复共同验收。 |
| M46-S09 | P0 必选 | 统一语料、codec ID/版本、兼容/损坏/迁移及性能验收 | commit/硬件/配置/原始样本可复算；旧格式可读，新 codec 从持久 metadata 解码，不兼容旧 writer 明确拒绝；CRC、长度、截断、非法 run/varint、混合段、升级/回退、收益与代价分别留证。 |

指标包含磁盘字节、读写放大、解码 CPU、P95/P99、分配/GC 和恢复，不预设倍数。落盘改动先设计版本/CRC，遵守格式升级、CHANGELOG、迁移或显式拒绝规则。详见 [M46 专题](docs/design/sonnetdb-45-storage-performance.md)。

M46 的编码选型采用“一级类型编码 + 二级可选压缩 + raw fallback”。选择器必须把完整载荷、字典、checkpoint、索引、CRC、解码工作量和临时分配计入成本，不能只比较小样本 payload。热写/窄读优先低 CPU 和可定位解码，冷 compaction 才评估更高压缩等级；Zstd/LZ4 适配器在 Core 之外单独核对许可、NativeAOT 和平台支持，不能用未验证的第三方运行时依赖改变 Core 合同。

首批顺序为 S01/S09 基线与格式合同、S02 Int64、S03 Float64/Boolean、S04 String、S05 Timestamp/范围解码，再接 S07/S08 的缓存、二级压缩、WAL 和 compaction。Auto 先作为显式策略；只有真实语料、CPU、尾延迟、恢复和兼容证据全部达到冻结门槛，才评估新数据库默认启用。Vector、GeoPoint、关系行存储、KV、文档、对象、MQ、图和向量索引继续按各自模型合同接入，不能用时序 Segment 压缩率代表九模型整体收益。

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
| **M47-P02 / P0** | **本地完成**：修复 Web smoke 夹具/环境/收集边界，冻结候选02 | 修复来源 `ed4f48242c5d5b4e7e16861504d03cc845146402`，保留版本 4.5.0-preview.1.2；build、448 Node合同、307 fixture与12 StudioHost通过，25默认skip和30 real suite分列，所有远端门禁NOT_RUN。原两次CI失败、旧Object flaky和执行器越界事件保留；详见[P02报告](docs/design/m47-unified-management-workbench/preview-1-p02-smoke.md)。 |
| **M47-P03 / P0** | **R1本地完成；完整模式四片307通过/25原skip**：入口/动作拒绝、登录授权、SQL预算、一次单行审批及终态 | [P03报告](docs/design/m47-unified-management-workbench/preview-1-p03-controls.md)分列真实Server、浏览器故障注入、正常重开与完整模式回归。七个模型专用读取因服务端预算未证延期；KV及时序表格超时已关闭，[候选04](docs/design/m47-unified-management-workbench/preview-1-candidate-04.json)固定`57222f57e4cc` / 4.5.0-preview.1.4。P04～P08未执行，NOT_READY。 |
| **M47-P04 / P0** | 构建来源可追踪的 Web/Server 预览包及 manifest/hash | 资源、Server、配置模板、许可证、安装说明与版本/commit 对应；按所含程序集取得适用 trim/AOT/构建证据。旧 Release DLL、web/dist 存在和 analyzer PASS 不能替代当前发行物。 |
| **M47-P05 / P0** | 用 P04 实物验证首次部署/启动、登录、导航、查询/导出和纳入的写审批 | 在公布的根路径/代理子路径及目标环境通过；资源/SSE/认证返回/深链接与真实 Server 一致。验证目录/权限、端口占用、停止重开与回退，所有文件/进程清理仅限核验归属。只测试实际承诺模式，不把 Vite 本地开发运行当发布部署通过。 |
| **M47-P06 / P0** | **本地说明/四附件/限定手动分发合同完成**：[报告](docs/design/m47-unified-management-workbench/preview-1-p06-distribution.md) | 候选10原ZIP/manifest/P04 checksum保持；外部notes与新checksum精确四附件，默认prepare只读。P07原完整policy及实际P08授权/环境审批仍必需；未公开发布，NOT_READY。 |
| **M47-P07 / P0** | **✅ 完成：更新分发工具后P07 READY，12/12门禁、8/8验收**：[候选12评审](docs/design/m47-unified-management-workbench/preview-1-p07-12.json)，原工具/审批策略历史保持。 | 原完整同source门禁和验收已通过，公开交付见P08。 |
| **M47-P08 / 发布** | **✅ 已发布，匿名四附件/290项库存及Chrome/Edge本机首跑通过**：[4.5.0-preview.1.12](https://github.com/IoTSharp/SonnetDB/releases/tag/workbench-preview-1.12)、[P08记录](docs/design/m47-unified-management-workbench/preview-1-p08-12.json)。maikebing已实际自批，正常停止/独立进程审计通过；只限Windows R1 Web预览。 | 预览、源码、四附件及原审批/回读/首跑无损证据全部归档，M47三面正式验收继续。 |

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


<a id="milestone-48"></a>

## Milestone 48 — 单机日志与事件分析

目标是把日志从可靠接入推进到检索、统计、排障、告警、保留与恢复，复用已有文档/全文/时序、M43 订阅与 DLQ、M45 聚合和 M47 Workbench。LogStream 是已有模型的逻辑资源，不新增第十模型。**当前只完成规划；以下 24 项全部 `📋 planned`，不是已创建的 Issue 或已交付功能。** 详细字段模型、状态机、实现边界与逐项验收见 [M48 专题](docs/design/m48-single-node-log-event-analytics.md)。

### 首版范围与工作包

R1 必选 **L01～L20**：一个节点可以接收多主机日志；先支持 .NET 结构化日志与 Web 访问日志、真实 Logstash HTTP 批量适配、Web 日志探索及一个受控 Webhook 通道。L01 冻结目标版本、平台、硬件、真实语料、负载和门槛；不自动扩大既有 4.5 发布范围。P0/P1 均为 R1 必选，P2 为后续条件。

| ID / 优先级 | 具体交付与主要依赖 | 完成与验收条件 |
|---|---|---|
| M48-L01 / P0 | 源码缺口复核、真实日志语料、范围/版本/负载 profile；复用 V45-X06 | 固定预期结果、来源/哈希、硬件/持久性/内存/SLO；复现候选窗口与 10000 条边界，未校准不称达标。 |
| M48-L02 / P0 | 事件信封、时间/精度/字段/来源身份；依赖 L01 | 缺失/NULL/整数、时区/源精度、数组/点号/大小写、重复身份冲突均明确；DTO/source generation 与拒绝矩阵齐全。 |
| M48-L03 / P0 | LogStream 目录、模板、分区与滚动；依赖 L02 | 默认按摄取时间/容量滚动，event_time 用于检索；并发写/崩溃后单一写目标，schema/流重建代际不串用。 |
| M48-L04 / P0 | 批量接入、逐项/通用批次确认、幂等/未知提交；依赖 L02/L03 | 区分已接收/耐久/可搜索；混合有效无效、ACK 丢失、重复冲突、超限/满盘可对账，不把内存入队当持久。 |
| M48-L05 / P0 | 版本化处理管线、模拟、转换/脱敏/失败分流；依赖 L02/L04 | 两类真实模板、日期与类型错误、脱敏失败、配置换代和原版本重放；禁止无界匹配/任意脚本。 |
| M48-L06 / P0 | .NET 示例和真实 Logstash HTTP/json_batch；依赖 L04/L05 | 固定消费者版本，实际 JSON 数组/鉴权/重试/429/503/断网；HTTP 成功不可掩盖未处理的批内失败。 |
| M48-L07 / P0 | M43 spool/检查点/重投/背压/DLQ 日志集成；依赖 L03/L04 | 来源位点不先于持久决定；提交前后/ACK 丢失/暂停/隔离/重放/满队列恢复，未知状态明确交接。 |
| M48-L08 / P0 | 文本+时间+精确字段+权限统一过滤；依赖 L02/L03 | 纯过滤、窗口外命中、超过 10000 条与全扫描参考对拍；总数/分面不再冒称候选窗口为完整集合。 |
| M48-L09 / P0 | 磁盘全文块访问、缓存、批量段/Top-K/合并；复用 M35/M42/M46 | 大于内存工作集、冷读/损坏/取消/满盘和前后台竞争；不全量加载或无界合并后宣称预算成立，格式升级可回退。 |
| M48-L10 / P0 | 固定读取视图、稳定时间排序/游标、上下文/有界导出；依赖 L03/L08/L09 | 写入/滚动/删除/同时间戳/撤权/过期不静默漏读重复；资源租约与游标绑定明确，当前窗口导出与完整导出区分。 |
| M48-L11 / P1 | 全过滤集合 facet/时间桶/统计列/精确和近似聚合；依赖 L08～L10/M45 | UTC 半开桶、missing/NULL/多值/Top-N、Int64/误差与高基数验证；派生水位/代际可查，批流同一致性点对拍。 |
| M48-L12 / P1 | 保留、封存、回收和归档；依赖 L03/L07/L09/L10 | 迟到、未来时间、保留缩短、活跃视图与删除中崩溃；原事件/索引/派生引用一致，归档不冒称可直接查询。 |
| M48-L13 / P0 | 采集/读取/管理权限、隔离/审计/原文与 DLQ 脱敏；贯穿 | 两库及多身份真实拒绝，hits/facet/总数/上下文/导出/规则一致；现有权限不足时用数据库隔离，不靠 UI 隐藏。 |
| M48-L14 / P0 | 全链路预算、公平性、取消与观测；复用 M42/V45-X01/X02 | 解析/候选/posting/排序/聚合/响应/spool/合并费用归属，临时峰值计费；超限明确拒绝，取消释放，有限指标标签。 |
| M48-L15 / P1 | Web 日志探索/时间轴/筛选/详情/上下文；依赖 L08/L10/L11/L13/L14 | 真实 Server/浏览器完整旅程，迟返/撤权/资源错误/视图过期可理解；消费实际 typed 入口。 |
| M48-L16 / P1 | 保存查询、轻量看板、联动/共享/导出；依赖 L10/L11/L13/L15 | 定义版本/权限/恢复、流删除/schema 改变失效、多面板预算与下载；不保存 token 或静默跨库。 |
| M48-L17 / P1 | 阈值规则、持久状态/静默/恢复通知/Webhook outbox；依赖 L07/L11/L13/L14/M45 | 迟到/重复/零分母/规则换代/数据中断/发送回执丢失；Unknown 不当作零或恢复，外部通知至少一次。 |
| M48-L18 / P0 | 备份恢复、强杀/磁盘故障/版本迁移与回退；依赖已交付核心切片 | 目录/事件/源位点/幂等/派生/定义/规则逐项对账；外部 spool 或 `.system/mq` 单列，kill 不代替掉电。 |
| M48-L19 / P1 | typed SDK/CLI/能力发现、真实示例与迁移运维说明；依赖实际入口 | embedded/REST/适用 Frame 独立矩阵，字段转换与失败回执可重现；不承诺原 ES 客户端/Kibana 即插即用。 |
| M48-L20 / P0 | 固定 profile/168 小时/原生候选/发行收口；依赖 L01～L19 | 同候选真实采集器、权限、容量、恢复、AOT/原发布 policy、安装首跑齐全；未验项保持 NOT_READY。 |
| M48-L21 / P2 | OTLP Logs 接收与真实 Collector 适配；R1 后条件项 | 独立冻结 transport/encoding/partial success/字段/精度；Logs 不自动涵盖 Metrics/Traces。 |
| M48-L22 / P2 | Trace 接收/查询与日志关联视图；依赖 L21/独立 span 合同 | 真实 span/父子/采样/迟到/重复验证；相同 trace_id 日志聚合不称完整 APM。 |
| M48-L23 / P2 | 语义日志与设备/业务关联；复用 M27/M35/M44 | 真实相关性/误报/成本、ACL/删除/模型换代及有证据解释；hash/合成不计语义质量。 |
| M48-L24 / P2 | 扩展采集器/字段包和必要的协议迁移子集 | 具体消费者、版本/请求/错误矩阵与许可评审；不默认重做完整 ELK/Fleet/插件生态。 |

### 依赖与阶段退出

1. **G0 合同与基线：** L01/L02 及 L13/L14 合同；先复现并冻结语料/范围/门槛，不把已有分词、BM25、窗口或页面重新派单。
2. **G1 可靠接入与基础查询：** L03～L08 + 适用权限/预算；真实采集器、持久确认和过滤通过后只标受限 pilot。
3. **G2 容量与生命周期：** L09～L12、完善 L13/L14 与 L18 基础恢复；大于内存工作集、固定视图、统计/保留及故障可对账。
4. **G3 排障闭环：** L15～L19；两条真实日志旅程、保存查询/看板、规则通知、运维与完整恢复，按实际宿主分别验收。
5. **G4 R1 发行：** L20 汇总 L01～L20；同候选适用发布门禁、固定 profile 168 小时、实际包/首跑核验；L21～L24 独立评审。

L13/L14 从 G0 贯穿，L18 的故障验证从 G1 累积。R1 只在全部必选范围完成后收口；功能实现、本地测试、真实服务、固定硬件、恢复、长稳与发行分列。R1 不提供分片/复制/自动接管、任意外部副作用 exactly-once、完整 Elasticsearch API/Kibana 兼容或完整 SIEM/APM。近期首个实施切片为 L01，不因本规划提交自动启动采集或功能开发。

## 4.5 跨模型能力与性能闭环

沿用既有归属，不重复建设核心服务。
<a id="milestone-49"></a>
## Milestone 49 — 连接器/嵌入式拆分、九模型访问与有界批处理

2026-10-16 按用户确认修订，实施已开始：C01/C02/D01/R02/R03 首片进行中，其余 planned，15 包均未整体完成。[实施矩阵](docs/design/m49-implementation-matrix.md)跟踪实际边界。`connectors/` 为各语言原生 HTTP/2 二进制 Frame，平行 `embedded/` 为托管 .NET 与统一 AOT C ABI 绑定。远程八语言，嵌入式六语言；VB6/PureBasic 仅远程，VB6 固有 x86 不需要 32位 AOT 桥接。Linux/Windows/macOS、适用 x64/ARM64/glibc/musl 按每批矩阵验收，普通32位后续；Java8兼容，FFM按需。用户明确直接重构，不保留旧兼容层。

**首批只做九模型数据访问，允许分批正式发行，剩余语言/平台/能力后续完成。** AI/RAG、通用流处理/CDC/备份治理API后续；现有鉴权授权、资源预算、持久性和恢复验证继续执行。当前Frame操作不完整，F01必须补齐，不能经REST绕过；仅确有语言限制才讨论有证据的例外。已存在的批量/参数/类型/多结果和 M41/M42/M46 实现继续复用，物化缺口仍需端到端修复。

| ID | 优先级 / 依赖 | 工作与退出条件 |
|---|---|---|
| M49-C01 | P0；先行 | 冻结首批九模型操作、两条产品线、每批语言/64位平台与证据基线；复现物化，核 Java 8/BASIC HTTP/2 候选，分批正式发行范围可审查。 |
| M49-C02 | P0；C01 | 冻结精确类型/参数、批 schema、预算/终态/错误/未知提交与 ABI/Frame 版本；共享语义和黄金语料，不强制远程复用 C ABI，不维护旧 API 兼容层。 |
| M49-D01 | P0；C02 | 直接拆分 connectors/embedded 的目录、包、依赖与构建入口；.NET 嵌入式托管，非 .NET 共用 AOT；BASIC 仅远程，去除旧门面/桥接依赖；不改磁盘格式。 |
| M49-F01 | P0；C02；与 D01 按切片衔接 | 逐模型补齐 HTTP/2 Frame 服务端操作、参数/适用事务、完整错误/终态/取消与必要认证路径；远程不得以 REST/HTTP1 回落代替缺口，例外逐项留证。 |
| M49-C03 | P0；D01/F01、R01/R02/R03 对应切片 | 打通 .NET 托管和 C→AOT 嵌入式，以及 .NET/C 原生 Frame 远程路径；精确类型、九模型、批次/取消/重开与适用 AOT 分别验收；生态接口按新边界调整。 |
| M49-C04 | P1；C03 对应切片 | Go/Rust 各实现原生 Frame 远程 SDK 与独立 embedded FFI；惯用迭代/批写、context/取消、RAII/所有权和 database/sql 适用语义逐端实跑。 |
| M49-C05 | P1；C03 对应切片 | Java 8 兼容原生 Frame 远程库与 JNI 嵌入式绑定；AutoCloseable/精确类型/取消/异常和缓冲生命周期实跑；必要时拆 Java8 包，FFM 可选。 |
| M49-C06 | P1；C03 对应切片 | Python 原生 Frame 远程 SDK 与 embedded ctypes 分开交付；DB-API/有界批与可选 Pandas 映射，dtype/NULL/时间/大整数和整结果容量明确。 |
| M49-C07 | P1；F01/C02 对应切片 | VB6/PureBasic 仅实现原生远程 Frame；允许系统网络库，验证实际 HTTP/2、二进制/Int64/取消与适用订阅；不建 AOT/x86 引擎桥接，缺授权工具链保留 NOT_RUN。 |
| M49-C08 | P1；本批适用 C03～C07/R05 | 按本批语言/平台/操作交付正式候选包、匹配共享库/头文件、模型 demo、文档与干净消费者；批次可独立完成，构建/发布/下载回读分列，不等全矩阵。 |
| M49-R01 | P0；C01/C02 | 复用 M41/M42 为有限关系/measurement 扫描建立 pull/批执行；不先生成全量 Rows，排序/JOIN/聚合等阻塞路径明确预算/spill/拒绝。 |
| M49-R02 | P0；R01/C02、D01/F01 对应切片 | 托管/AOT 嵌入式与 Server→Frame→各语言原生远程路径贯通批大小、背压、能力/版本与完整终态；不经 C ABI 中转远程，缺 end 不算成功 EOF。 |
| M49-R03 | P0；C02，贯穿 R01/R02/C03/F01 | 接 V45-X02，对执行/编码/解码/预取/缓存租约及累计请求计费，验证公平准入与取消/关闭释放；估算预算与实测 RSS 分列。 |
| M49-R04 | P1；R01～R03、M46 适用切片 | 列/时间裁剪、范围解码、批传递和复制成本优化；宽/窄、冷/热、稀疏/高熵对拍，不牺牲精确类型和取消安全。 |
| M49-R05 | P1；本批适用 C03～C07/R01～R04/F01 | 每批分别验收目标语言的 embedded/真实 HTTP2 Frame、失败/取消/断连/重开、资源/性能和新版本协商；公开限制，单端 PASS 不升级未交付矩阵。 |

推进顺序：**C01 → C02 → D01/F01 + R01/R02/R03/C03 → C04～C07 → R04/R05 → 按批 C08**，依赖按可验收切片处理。建议先 .NET/C 的 Windows/Linux x64，再扩语言与平台；正式批次只声明实际通过的矩阵，不等待整个M49完成，也不把局部PASS升级为九模型全部支持。详细边界见 [M49 专题](docs/design/m49-connector-analytics-bounded-batches.md)与[确认需求](docs/design/connector-embedded-split-requirements-20261016.md)。

不改变磁盘格式规则，不盲重放未知写，不隐藏全量物化/无界预取。外部对象存储、普通32位库、平台能力API与通用历史修订保持后续边界；Core Safe-only、零第三方运行时依赖、source-generated JSON 与适用 AOT 零警告保持。已开始 Native SQL reader 生命周期与唯一 AOT 工程迁移；Core 物化、远程新 SDK、性能和包验收不能从本片推断通过。


| ID / 归属 | 优先级 / 范围 | 交付与验收 |
|---|---|---|
| V45-X01 / M41、M42 | P0 必选 | 执行器→REST/Frame→ADO/SDK 批/惰性读取、首行、取消/断连、行/字节准入及工作集；以 M49-R01/R02 接通托管/AOT嵌入式与各语言原生Frame连接器，明确阻塞/spill，不把切帧/预览当全链路有界。 |
| V45-X02 / M42 | P0 必选 | 基于 SQL worker/内存、KV I/O 和缓存，增加数据库/进程总预算、分类归属及公平准入；接 M49-R03 的批次/编码/预取/客户端消费，治理 reader 缓存随段数放大、共享 HNSW 跨库配置、AI/后台与前台竞争，记录 CPU/I/O/heap/排队/释放。 |
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



1. **基线与合同冻结：** M44-A01/A02/A06、M45-C01/C02/C09、M46-S01/S09、M49-C01/C02、V45-X06；冻结支持矩阵、语料与门槛，不重做归档实现。
2. **基础实现与成本治理：** M45 分组/state、M46 编码/精度 guard、V45-X01/X02；读取按 M49-R01/R02/R03+C03 接通公共链路，再逐语言 C04～C07；每包有必要定向回归、Release 和适用 AOT。
3. **持续计算与恢复：** M45-C03/C04/C05/C08 接既有 M43，V45-X03/X04/X08；不支持源/操作及未知结果明确拒绝或进入恢复。
4. **AI 应用与九模型旅程：** M44-A03/A04/A05/A07、M45-C06/C07、M46 深化和 V45-X05；条件项独立证明收益后决定。
5. **性能、质量与发布论证：** 执行既有目标机队列和新应用/聚合/编码 gate，绑定同一候选；本机合成数字不升级生产结论。
6. **统一管理工作台与三面发布：** M47 先评审完整导航、五区壳、共享对话框/提示和一级/二级任务，再逐页细化；用户确认原型后推进共享合同、九模型工作台与生产迁移。Web Admin/Studio/VS Code 旅程、AI/MCP 入驻和版本兼容矩阵分别通过对应宿主证据；单端或原型 PASS 不升级为三面整体发布。

独立切片可并行；共享源码、构建输出和资源验证按所有权协调。单 PR 只做一个可审查切片。

<a id="milestone-50"></a>

## Milestone 50 — 时序时间戳精度与无损时间链路

2026-11-02 新增独立规划：**真正保存 ms/us/ns，默认 ms，创建数据库时固定并持久化时序精度。** 输入单位、存储单位与来源授时准确度分别定义；现有 ms 库不自动改精度，迁移采用新库和显式对账。当前只完成规划，以下 **30 项必选、2 项条件任务全部为 `📋 planned`**，不是已创建的 GitHub Issue/PR，也不是新精度已经实现。

这是存储、查询/物理时长、协议/SDK、工具和迁移/恢复的中大型改造。初估嵌入式闭环 10～15 工程人日，完整首版累计 40～70 工程人日；硬件、长稳和发行等待另计，T01 复核后重估，不能当作日历承诺。详细源码归属、兼容决策、逐项交付物/验收、阶段退出与来源见 [M50 详细设计](docs/design/m50-timeseries-timestamp-precision.md)。

| ID | 状态 | 优先级 / 依赖 | 工作与退出条件 |
|---|---|---|---|
| M50-T01 | 📋 | P0；无 | 逐链路登记毫秒假设，冻结 legacy/新合同、首批矩阵、负基线与估算；未校准不预填收益。 |
| M50-T02 | 📋 | P0；T01 | 精确整数/单位/时长、checked 缩放/商余、DateTime 100ns 与查询界限；不经 Double。 |
| M50-T03 | 📋 | P0；T01/T02 | 创建时固定单位，打开/挂载校验身份/版本；真实旧引擎须在修改前拒绝高精度库。 |
| M50-T04 | 📋 | P0；T03 | WAL 编码/滚动/replay 绑定精度，保留确认/fsync；相邻 1ns 经强杀重开不丢失。 |
| M50-T05 | 📋 | P0；T03/T04 | Segment/Block 上下界、时间索引、raw/codec/range decode 保真，格式/极值/损坏明确拒绝。 |
| M50-T06 | 📋 | P0；T04/T05 | flush/compaction/墓碑/checkpoint 保持单位，合并/删除/恢复不误读、不误删。 |
| M50-T07 | 📋 | P0；T02/T05/T06 | 精确范围、DESC/latest-N/稀疏合并与裁剪，游标/缓存绑定单位及代际，与扫描参考对拍。 |
| M50-T08 | 📋 | P0；T02/T07 | SQL time/参数/now/duration/分桶/日期互转统一绑定；空结果保留精度，亚毫秒不先转 ms。 |
| M50-T09 | 📋 | P1；T08 | 速率/积分/PID/预测归一物理时长一次；同一过程三精度结果对齐，未知函数提前拒绝。 |
| M50-T10 | 📋 | P0；T02/T03/T06/T07 | NowFn/TTL/cutoff 与后台物理时长分开，边界/回拨/溢出不误删；不是全局乘倍率。 |
| M50-T11 | 📋 | P0；T03～T10 | 三精度真实 create→write→query→flush→reopen→delete；旧 ms 语义与不支持路径明确。 |
| M50-T12 | 📋 | P0；T03/T11 | Server 建库/注册/挂载/列表/权限/能力、源生成 DTO；多库精度上下文不串用。 |
| M50-T13 | 📋 | P0；T02/T12 | 独立 Protocol 的参数/结果/批次/终态/版本协商，真实 HTTP/2 纳秒往返，旧端明确拒绝。 |
| M50-T14 | 📋 | P0；T02/T12 | REST/SQL/NDJSON 使用精确整数文本及单位元数据；>2^53、零行/NULL 与取消不丢精度。 |
| M50-T15 | 📋 | P0；T02/T11 | LP/JSON/BulkValues 原始时间先保留，在库边界只换算一次；余数/溢出/批次错误可查。 |
| M50-T16 | 📋 | P1；T12/T13/T14/T15 | Influx HTTP/UDP/MQTT/CoAP 与固定 ms 来源保留输入默认值；真实监听对拍，发送不等耐久。 |
| M50-T17 | 📋 | P0；T11/T13/T14/T15 | .NET SDK/Data/ADO 精确类型、builder/writer、schema 和日期转换；实际包消费者/AOT 分列。 |
| M50-T18 | 📋 | P0；T13/T17 | C/NativeAOT 版本化 ABI/头文件/所有权，原生远程与 embedded 两路精确消费者各自验证。 |
| M50-T19 | 📋 | P1；T13/T18 | Go/Rust 原生 Frame 与 FFI 精确时间映射、取消/重开；不以别的语言 PASS 代验。 |
| M50-T20 | 📋 | P1；T13/T18 | Java8 Instant/long 与 Python 整数/可选 DataFrame，时区/NaT/非整 us 值有明确合同。 |
| M50-T21 | 📋 | P1；T13 | VB6/PureBasic 仅远程，精确文本/高低位不经 Double；缺实际工具链保持 NOT_RUN。 |
| M50-T22 | 📋 | P1；T12/T13/T14/T17 | CLI/Web/Studio/VS Code 精确输入/排序/游标/导出及能力提示，三个宿主分别真实验收。 |
| M50-T23 | 📋 | P1；T08/T09/T10/T13 | 持久窗口/水位、CDC/桥接与派生 checkpoint 版本；不同单位不静默混算，不另造流引擎。 |
| M50-T24 | 📋 | P0；T03/T06/T23 | 备份 manifest/inspect/restore 保留单位和恢复域，错目标/旧恢复器拒绝，不暗中变精度。 |
| M50-T25 | 📋 | P0；T11/T24 | dry-run、目标库有界迁移/续传/余数与碰撞对账、切换/回退；保留源并处理切换后新写。 |
| M50-T26 | 📋 | P0；T11/T13/T14/T15/T17 | 黄金整数/旧 ms 夹具、新旧矩阵及固定外部版本对拍；时间戳严格相等，light/full 分列。 |
| M50-T27 | 📋 | P0；T06/T23/T24/T25 | WAL/维护/迁移/恢复的强杀/损坏/满盘/取消；确认等级可对账，拒绝打开无副作用。 |
| M50-T28 | 📋 | P1；T26 | 固定 x64/ARM64、两类同物理/更高信息量语料，测尾延迟/CPU/RSS/I/O/压缩，不预填倍数。 |
| M50-T29 | 📋 | P1；T27/T28；本批适用 T16～T23 | 同候选多库混合精度与维护/消费者 168 小时；资源/停止条件有界，M20 七天独立回读。 |
| M50-T30 | 📋 | P0；T24～T29；本批声明矩阵 | 本批 AOT/原格式门禁、真实包/安装/版本/限制/回退及发行回读；整体完成须全部必选。 |
| M50-T31 | 📋 | P2 条件；对应基础与外部接收能力已冻结 | 按 M48/实际消费者冻结 OTLP/Arrow 单位/NULL/时区和纳秒往返；不自动建设 APM/全部接收器。 |
| M50-T32 | 📋 | P2 条件；有明确硬件/设备/现场授权与 profile | 获明确硬件/现场授权后验证打戳位置、时标/偏差/抖动；ns 存储不等于 1ns 授时准确度。 |

推进顺序：**G0 T01～T03 → G1 T04～T11 → G2 T12～T18 → G3 T19～T23 按批 → G4 T24～T29 → G5 T30**；T26/T27 的正确性与恢复用例从对应切片累积。首个实施任务为 T01，先冻结 ms 兼容、精确换算、真实持久格式防护与首批 .NET/C 矩阵；存储/恢复和维护未闭环前不开公开高精度建库。

M50 复用 M36、M41/M42、M43/M45、M46、M47/M48 与 M49 的实际切片，不要求等待这些里程碑整体完成。目标版本与首批范围由 T01 独立冻结，不自动改变现有 4.5、已发布 Workbench 预览或 M49 批次门槛；分批正式发行只声明实际通过的矩阵。设置只影响时序 measurement time，不把关系日期、MQ、文档 TTL 或网络超时统一改成库单位。

<a id="milestone-51"></a>
## Milestone 51 — WorkBuddy 生态集成与 SonnetDB 全生命周期闭环

目标：让 SonnetDB 用户通过 WorkBuddy 完成部署、安全连接、九模型访问与使用，以及升级、备份、恢复、维护和排障。连接器负责 MCP 接入/授权，Skill 负责操作流程，资料库负责可追溯公开知识，专家负责统一助手入口，Buddy 应用负责已验收的完整场景封装；不要求五类资产同时发布。M51 独立冻结版本和每批范围，不改变既有 4.5、M47 预览或 M49/M50 门槛，也不将 Graph Beta 升格为正式稳定能力。

### 已完成基线与当前接入合同（2026-11-04）

以下仅说明实际完成的切片，不代表 M51 整体完成或平台已批准。

| 证据 ID | 状态 | 本次成果与验收边界 |
|---|---|---|
| M51-B01 | 🟢 本地草案完成 | 当前连接器/Skill为0.2.0，目标SonnetDB 5.0的43工具以tools/list和schema发现；默认目标库Read，用户已明确授权且有Write grant时使用独立写工具，query_sql仍只读。43生产切片49731955/PR391待统一整合；部署、升级和服务重启需另有授权，不能由工具存在推断授权。 |
| M51-B02 | 🟢 公开资料初次归档 | 闭环指南、九模型手册、提交清单已建于 `docs/workbuddy-sonnetdb-*.md`，并归档至 [IoTSharp 公开空间](https://www.workbuddy.cn/space/s/dQLhaiG6IukkkFwHCd8Rzj)。后续本地纠偏与最新报告尚未同步在线，不能把初次上传视为内容持续一致。 |
| M51-B03 | 🟢 历史 Docker 基线 | 2026-11-03官方 `iotsharp/sonnetdb` 5.0.0，revision `455bf3b53b1e56df0064d8f574b0595cbb0b609e`，digest `sha256:6c5974f4ba4e81fd12a82cb08a6265c959d3c63f589f325df7d02ff9a1d81597`；该镜像只公布九工具。两轮回环容器/独立卷正常停止并保留，未接生产库；不作为当前43工具的Docker/宿主验收。 |
| M51-B04 | 🟢 历史协议分层通过 | 2026-11-03 r2报告仍为19通用检查、原生九模型、重启读回九模型、MCP六模型，KV/对象/MQ的MCP三项not_available；日期/source不改写。当前脚本改为43工具发现与Read凭据九模型读取/写拒绝，不把本次离线校验写成新版实调PASS。正常重启不等于升级、备份恢复或掉电恢复。 |
| M51-B05 | 🟢 测试端纠错可追溯 | 首轮 MCP 六项因脚本未解码 `JsonElementValue` 比较失败，修复 typed cell 解码后用独立第二轮容器复验通过；保留两轮报告及实际 `tools/list` schema，不覆盖失败。报告为本地 HTTP 客户端证据，不是 WorkBuddy 宿主证据。 |
| M51-B06 | 🟡 0.1.1 本地修复完成 | 0.1.0 在真实平台因 URL 模板无显式 scheme 解析失败；已改为 `https://${SONNETDB_HOST}/mcp/${SONNETDB_DATABASE}`，host/database/token 三字段，版本 0.1.1，六组静态检查与五文件清洁包检查通过。0.1.1 重新上传解析尚无回执；0.1.0 不再使用。 |
| M51-B07 | 🟡 控制台入口已打开 | 已登录并进入连接器发布流程；本会话浏览器无文件上传接口，需用户手动选择新 ZIP。**NOT_SUBMITTED：尚无正式送审、审核通过或发布回执**。资料库公开不等于连接器送审。 |

证据入口：[验收脚本](eng/workbuddy-sonnetdb-verify.py)、[连接器说明](connectors/workbuddy-sonnetdb/README.md)、[闭环指南](docs/workbuddy-sonnetdb-closed-loop-guide.md)、[九模型手册](docs/workbuddy-sonnetdb-model-playbook.md)、[提交清单](docs/workbuddy-sonnetdb-submission-checklist.md)。历史脱敏报告/工具schema保存在outputs并保留日期/source；ZIP仅本地重建，W16绑定最终候选SHA256，不能沿用0.1.0/0.1.1或r2九工具报告代表0.2.0/43工具。后续公开空间同步和宿主/HTTPS验收仍待完成；历史随机凭据未落盘，不假定旧环境仍可登录或Token已全部撤销。

### 后续工作包

状态按各行实际切片记录；规划编号本身不表示已创建Issue/PR。43工具生产切片已验，不重复实施，集成与平台门禁继续独立验收。

| ID | 状态 / 范围 | 待交付工作 | 依赖与退出条件 |
|---|---|---|---|
| M51-W01 | 📋 P0 必选 | 冻结0.2.0的43工具、默认Read/授权Write和发布矩阵 | 当前用户已明确授权补齐写工具；按tools/list、schema与实际grant绑定能力，不能把发现等同授权或把数据面写权限当运维权限。 |
| M51-W02 | 📋 P0 必选 | 0.2.0 平台解析与上传自测 | 使用新五文件包，保留0.1.0/0.1.1历史失败/未知状态；检查HTTPS模板和三字段映射，取得真实平台解析回执，解析成功不等于送审。 |
| M51-W03 | 📋 P0 必选 | WorkBuddy 真实宿主安装、表单、连接、重连与卸载验收 | W02；有效 HTTPS/证书和审核网络可达；现场调用工具，验证 401/403、只读、截断及新旧 Token 轮换交互，不能复用脚本 PASS 代验。 |
| M51-W04 | 📋 P0 必选 | Token 生命周期、隐私/外发与白名单安全合同 | 最小 Read、密钥脱敏、撤销/到期/轮换、重定向与日志边界；先明确请求白名单与回调白名单的配置对象、实际认证方案和通配支持。用户提出的 `https://*.tencent.com`、`https://*.workbuddy.cn`、`https://*.workbuddy.ai`、`https://*.codebuddy.cn` 仅记为待核需求；当前 Token 模式无 OAuth 回调，不盲目开放 CORS/回调。 |
| M51-W05 | 📋 P0 必选 | 证据/文档收口与公开空间同步 | 将本地纠偏、分层矩阵和审核清单更新至指定 IoTSharp 空间；固定版本/来源及脱敏报告索引，检查框据实更新。仅上传公开内容，不上传 Token、客户数据和内部敏感日志。 |
| M51-W06 | 🟡 源码切片已验 / 集成待验 | 九模型MCP43工具的统一整合与客户端验收 | 生产实现冻结49731955/PR391，权限/预算与NativeAOT43/36、118/89已验；不重复开发。当前0.2.0更新能力声明/规则，宿主/HTTPS/平台/发布门禁仍按实际渠道验，不把带副作用消费称为只读。 |
| M51-W07 | 📋 P1 全量模型批次 | 九模型 MCP 与真实宿主联合回归 | W06 及对应模型合同；原生 API、MCP、宿主分层测试，覆盖权限、预算、分页、取消/超时、删除/恢复和不支持路径。Graph Beta 及模型能力限制继续公开。 |
| M51-W08 | 📋 P0 按批适用 | 知识 provider 与工具/schema 合同回归 | docs_search/skill_search/skill_load 的成功、不可用及不可信内容；错误码、超时/取消、契约版本与安全工具发现；CI 使用合成数据，输出脱敏可复算证据。 |
| M51-W09 | 📋 P1 运维批次 | 管理执行能力与人工审批模型设计 | 管理面与只读面隔离；显式计划、作用域/目标、dry-run、二次确认、审计、取消和失败终态。禁止凭只读 Token 提权或开放任意 shell。 |
| M51-W10 | 📋 P1 运维批次 | 部署、健康检查、升级/回退闭环 | W09；冻结目标平台/发行物/镜像 digest，验证真实首次部署、版本兼容、维护窗口、升级失败和回退；本次正常重启不能代替。 |
| M51-W11 | 📋 P1 运维批次 | 备份、恢复、对账与故障排查闭环 | W09；区分数据库与实例恢复域，真实备份到隔离目标恢复、校验数据/权限及适用 MQ 位点，验证错版本/损坏/取消/满盘；不覆盖生产源，明确 RPO/RTO。 |
| M51-W12 | 📋 P1 必选 | 独立 Skill 包、共享来源与独立审核 | 先核实实际平台提交格式；复用连接器内置 Skill 的单一来源/版本，避免规则分叉。验收无连接器、无权限、不支持模型和危险操作时的正确引导。 |
| M51-W13 | 📋 P1 必选 | SonnetDB 专家统一入口与发布 | W03/W05，按批引用 W12；组合连接器、Skill、公开资料，继承用户授权，不夹带 Token、不绕过审批；部署/查询/运维旅程及拒绝路径验收后独立送审。 |
| M51-W14 | 📋 P2 条件 | Buddy 应用可行性及范围冻结 | 先核实平台应用能力、审核形式、用户价值和维护成本，决定是否独立发布；不是仅为凑齐资产而重复封装连接器。 |
| M51-W15 | 📋 P2 条件 | Buddy 应用建设与完整旅程验收 | W14 放行且所承诺模型/运维切片已通过；验证登录/数据隔离/出域/人工审批/恢复，未实现能力隐藏或明确降级，再按实际入口独立审核。 |
| M51-W16 | 📋 P0 按批必选 | 清洁发行包、审核材料与正式提交 | W01～W05/W08 及本批适用项；复核版本派生文件名/README和报告指纹，白名单打包，核对实际 ZIP/CRC/SHA256/来源与许可；记录提交版本/时间/回执、反馈修订及审核结果。 |
| M51-W17 | 📋 P0 按批必选 | 审核通过后的发布、回读与维护 | W16 取得真实批准后按用户授权发布；独立安装/公开入口回读、版本兼容、反馈渠道、回滚/下线与定期回归。连接器/Skill/专家/应用各自保留状态，不以任一已发布代替其它资产。 |

### 阶段与接续入口

1. **G0：W01 冻结范围 → W02 新包解析 → W03/W04 宿主与安全 → W05/W08 资料及合同收口。** 首先处理 0.1.1 实际解析；若平台仍拒绝，保留错误并出新候选，不继续声称可安装。
2. **G1：限定只读批次按 W16 送审 → W17 审核后发布。** 发布只声明已验证模型/工具；无需等待全部运维、专家或 Buddy 应用完成。
3. **G2：W06/W07 九模型补齐与 W09～W11 管理运维分批实现。** 每批都重复适用宿主、安全、清洁包及审核门禁，不自动扩大旧包权限。
4. **G3：W12 独立 Skill 与 W13 专家统一入口分别验收/送审；G4：W14 决策后才做 W15 Buddy 应用。** 应用不先于所承诺完整场景闭环。

完成判定：全部本里程碑必选工作及各批适用矩阵取得真实证据；条件项明确实施或延期。部署指导不算自动部署，原生九模型不算九模型 MCP，MCP 客户端成功不算宿主成功，静态包通过不算平台解析成功，上传不算送审，送审不算批准，批准不算已发布。仅在实际完成后更新状态，不重做 B01～B07 已保留的有效切片。

## 真机验证待办

记录硬件/架构、commit、配置/版本/命令/时间、真实 P50/P95/P99、working set/heap/分配/GC、逻辑与物理 I/O 区别、正确性/恢复与原始样本。不可用项为 `NOT_RUN`/`NOT_READY`/`DEFERRED`。

| 来源 | 保留验收 | 当前边界 |
|---|---|---|
| M19 #125 | 百万 series、万 segment、20 次 kill/reopen、万 measurement | 固定 Linux x64 四档待证。 |
| M25 #174 | million/ten-million 文档写查/TTL/重建/备份恢复/内存 | 固定目标及 attestation 待证。 |
| M20 #136 | 同候选light/full真实backend/artifact、七天scheduled观察 | 2026-10-10审查核到远端5424b666的10月8/9日light/full成功，非本地预览候选证明；七天完整观察未核。 |
| M27 #184/#185/#187/#340 | 真实模型/成本、IdP/broker、双网/续流及 Studio | 功能/本地已归档，现场独立验收。 |
| M29 #258 | 干净 Windows、WebView2、升级/卸载、端口/生命周期 | 安装包/宿主已有，实机待验。 |
| M35 / M36 | 真实语料、模型换代、九模型跨端/权限/取消/删除/恢复 | 2026-11-04 ARM64关系/时序真实REST与HTTP2 Frame读取、ADO预取消及后续查询已过；完整九模型旅程、写删和容量仍待验。 |
| M36 #323/#325/#326 | 对象大文件/分页/校验、MQ 实例/offset 恢复 | 真实13.25MB读取原证据保留；旧桶分页已修复，ARM64真实元数据隔离副本首查2.339秒、三页9对象与317万latest对拍通过。真实Server/SDK取消、断连、换客户端续传及本地单库正文+独立MQ恢复通过；完整高变更率、跨进程/现场恢复和发布仍待验。[本批证据](docs/audits/object-pagination-20261104/README.md)。 |
| M39 | 混合 DML、触发器/deferred/outbox 长期尾延迟 | 研发之外的生产观察。 |
| M40 #352/#367 / Couplet | 外部对拍、LDBC/Graphalytics、1m/10m、AOT、hard-kill/168 小时 | Beta 与联合环境 gate 独立。 |
| M41 / M42 | 目标硬件、同语料、冷启动/首行/heap/I/O、恢复/168 小时 | 2026-11-04 ARM64覆盖索引/回表对照、REST完整/截断、固定身份DESC/OFFSET并列时间排序及ADO两协议读取已过；完整性能分布、恢复和168小时继续待验。 |
| M44 | 三条旅程真实质量/成本、防泄漏、删除/回滚/任务恢复 | 新规划，fixture/hash 不计效果。 |
| M45 | batch↔stream、merge/retract、late/update/delete、rollup/spill/reopen | 新规划，冻结首批子集/误差。 |
| M46 | 编码/高熵回退、旧新格式、损坏/极值/放大/冷读 | 新规划，不预填提升倍数。 |
| M48（独立 R1） | 固定日志 profile、真实 Logstash、窗口外检索/全量统计、磁盘容量、保留/恢复、规则通知及 168 小时 | 全部 planned；版本/阈值待 L01 冻结，不由研究或已有模型局部 PASS 宣布日志平台完成。 |
| M50（独立增量） 📋 | 三精度同候选、真实协议/消费者、旧引擎拒绝、迁移/恢复、固定 x64/ARM64 与 168 小时 | T01 冻结范围/阈值，T26～T30 分别留证；T32 硬件授时条件项不由软件纳秒往返替代。 |


2026-11-04按用户确认，本轮托管功能正确性以ARM64真机通过作为验收依据，不以同功能尚未重复执行x64阻塞收口；未执行的指定平台容量、NativeAOT或原生依赖专项不列为实测PASS。本轮6组功能子项、32条通过断言及1项对象分页超时见[木垒真机收口](docs/audits/mulei-field-gates-20261104/README.md)和[机器证据](docs/audits/mulei-field-gates-20261104/verification.json)。这些子项完成不扩大为整个M36/M41/M42或最新独立Client已发布。

同日后续完成旧桶前缀索引回填修复、Core 566项和真实Server/SDK 43项回归；真实ARM64隔离元数据副本通过前三页对拍，生产仍运行原R1。旧FAIL_TIMEOUT保留为历史基线，冷打开约237秒、完整容量/高变更率/NativeAOT/168小时继续单独验收，详见[对象分页后续收口](docs/audits/object-pagination-20261104/README.md)。

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
| M49（两条产品线） | 首批九模型、真实托管/AOT嵌入式和原生HTTP2 Frame、精确类型/预算/取消/恢复及独立包消费者 | 首片进行中，15包均未整体完成；先64位，Java8兼容，BASIC仅远程，按实际矩阵分批正式发行；平台API后续。 |
|---|---|---|
| #385~#390 | 🚧 | 远程同步、schema/冲突、离线续传、客户端及现场故障/容量。 |
| #391~#395 | 🚧 | 远程投递/接管、权限、业务副作用边界与现场恢复，已有本地底座不重做。 |
| #396 | 🚧 | 十四能力完整入口/权限/失败/取消/重启/对账及远程组合。 |
| #397 | ⏳ | 真实发布级原始报告，汇总工具不等于证据。 |
| #398~#402 | ⏳ / 草稿已有 | release 绑定、外部提交/反馈、公开案例、最终验收；提交/收录/排名分开。 |

既有细分与退出见[总索引](docs/roadmap-total-milestone.md)，外部 issue 见[路线快照](docs/github-issues-roadmap.md)。

CAP 生态适配新增统一 `SonnetDB.CAP` 项目（2026-10-09），按用户最新要求合并存储与传输，并通过 Data SDK 覆盖嵌入式与远程；Document 存储、MQ 传输、单 collection 业务+Outbox 事务、幂等索引和持久消费组登记已实现。真实本地/Server/CAP 定向验证及 NuGet 候选包见[集成合同](docs/design/cap-integration.md)；正式发布、Linux/强杀/掉电/固定硬件与长稳仍独立待验。EF Core/ADO SQL 存储、MongoDB wire、跨 collection 事务和跨进程消费者租约不在本片交付中。

## 性能观察项

| 编号 | 候选 | 进入条件 |
|---|---|---|
| PF1 | 级联删除按选择率切索引/哈希 | 1/10/50/100 父键矩阵收益及回滚等价。 |
| PF2 | fuzzy 高基数词典结构 | 100k/500k term 证实瓶颈及收益；原至少 2 倍候选门槛保留，不是当前性能声明。 |
| PF3 | ANN tombstone 区间/gate | 墓碑是主要成本且不降召回/精确语义。 |
| PF4 | `pg_vectorsearch` / PRISM 分区式 ANN | 作为 2026-10-13 Tiger Data alpha 的外部候选，对比 HNSW、IVF-Flat、IVF-PQ 与精确扫描在长尾/分区倾斜/高频更新下的 Recall@K、尾延迟、建索引时间、磁盘/内存和重建成本；厂商自测与 alpha 结果不计入门禁。 |

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
