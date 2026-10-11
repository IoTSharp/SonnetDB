# SonnetDB 4.5 — AI 应用与可治理推理（M44）

规划日期：2026-10-03；基线：`4b004946`。本文细化根 [ROADMAP](../../ROADMAP.md) 的 M44-A01~A10，所有工作包均为 `planned`，不是已实现能力、已创建 issue 或发布承诺。M44-A01~A07 为 4.5 必选；A08/A09 为条件项；A10 为后续候选。

目标是让已有九模型、时序算法、检索与 Copilot 组成有真实效果、可审计成本、可恢复的应用。先完成工业诊断、业务分析、知识与资产助手三个旅程，再按证据引入新的模型和算法。参考外部产品的机制与取舍，按 SonnetDB 的嵌入式、Server、类型、事务、资源和 AOT 合同独立实现。

## 1. 已有能力与本次增量

| 当前代码/入口 | 已有范围 | M44 增量与证据边界 |
|---|---|---|
| [TimeSeriesForecaster](../../src/SonnetDB.Core/Query/Functions/Forecasting/TimeSeriesForecaster.cs)、[AnomalyFunctions](../../src/SonnetDB.Core/Query/Functions/Window/AnomalyFunctions.cs)、SQL 执行器 | `forecast(linear/holt_winters)`、`anomaly(zscore/mad/iqr)`、`changepoint(cusum)` 等算法及 SQL 接线；预测提供区间字段 | 真实任务的分组、时间切分、缺失/乱序处理、基线与误报、区间覆盖率；已有区间公式不等于真实数据已经校准。 |
| [IChatProvider](../../src/SonnetDB/Copilot/IChatProvider.cs)、[OpenAICompatibleChatProvider](../../src/SonnetDB/Copilot/OpenAICompatibleChatProvider.cs)、[OpenAICompatibleEmbeddingProvider](../../src/SonnetDB/Copilot/OpenAICompatibleEmbeddingProvider.cs) | 在线 chat/embedding 接线，现有 chat 接口返回字符串；该 OpenAI Compatible chat 实现请求 `Stream: false` | 增量结果元数据、usage/实际模型版本、预算与错误分类；provider 原生 token 流是 A08 条件项。不能据此称整个 Copilot 无流式输出。 |
| [CopilotAgent](../../src/SonnetDB/Copilot/CopilotAgent.cs)、[SonnetDbMcpTools](../../src/SonnetDB/Mcp/SonnetDbMcpTools.cs) | 有界 ReAct、schema/SQL 工具、引用及 `IAsyncEnumerable<CopilotChatEvent>` 事件；typed MCP 与写入审批路径已有 | 三旅程的真实结果、权限/删除/取消/恢复对拍；不重建 MCP，也不把事件传输或 broker 续流当作 provider token 流。自研 CopilotAgent 不改称已基于 Microsoft Agent Framework。 |
| [LocalOnnxEmbeddingProvider](../../src/SonnetDB/Copilot/LocalOnnxEmbeddingProvider.cs)、[SigLip2OnnxEmbeddingProvider](../../src/SonnetDB/SemanticSearch/SigLip2OnnxEmbeddingProvider.cs) | 显式 tokenizer/tensor/pooling/normalization profile；文本 ONNX 与图片/文字模型路径分别存在 | 真实模型来源/许可、质量、容量与目标环境；lazy readiness、tiny ONNX 与 hash fallback 只证明对应合同。两类模型的证据不能互相替代。 |
| [SemanticContentContracts](../../src/SonnetDB.Core/SemanticContent/SemanticContentContracts.cs)、[CopilotRagProfileOptions](../../src/SonnetDB/Configuration/CopilotRagProfileOptions.cs) | `EmbeddingProfile`、模态、维度、版本、归一化、出域策略已有；新 RAG profile 支持配置维度 | 补充 chat/时序/批任务的能力与结果合同及跨 profile 一致性。legacy docs/skills 固定 384 维，不外推为整个产品都固定维度；已有可配置 RAG profile 不重新建设。 |
| [RagIngestionWriter](../../src/SonnetDB.Core/SemanticContent/RagIngestionWriter.cs)、[RagIngestionManager](../../src/SonnetDB.Core/SemanticContent/RagIngestionManager.cs)、[CopilotRagKnowledgeStore](../../src/SonnetDB/Copilot/CopilotRagKnowledgeStore.cs) | 持久摄取、checkpoint/resume、generation 发布、重建/模型换代和管理合同已有 | 应用删除同步、治理与真实语料；A05 复用这些基础，并扩展到结构化批量推理结果，不另建一套 RAG 摄取系统。 |
| [SemanticSearchFusion](../../src/SonnetDB.Core/SemanticContent/SemanticSearchFusion.cs)、[HybridSearchExecutor](../../src/SonnetDB.Core/Sql/Execution/HybridSearchExecutor.cs) | 混合召回、RRF/加权融合、去重、候选预算及可选重排已有；融合输入要求已经授权 | ACL 必须在召回前生效，并检查最终版本/权限；证明真实召回与重排收益，不把 RRF 重新包装为新增功能。 |
| [KnowledgeGraphContracts](../../src/SonnetDB.Core/KnowledgeGraphs/KnowledgeGraphContracts.cs)、[KnowledgeGraphValidator](../../src/SonnetDB.Core/KnowledgeGraphs/KnowledgeGraphValidator.cs) 与 [M40 #365](../m40-graph-365-knowledge-contract.md) | 知识图谱/GraphRAG 的 provenance、claim、valid time、source/chunk、community/summary 引用及 typed SDK 投影已有 | 实体抽取/消歧/摘要仍由上层 job 完成；A09 深化有界证据检索与真实任务收益，不宣称完整自动 GraphRAG 已生产就绪。Graph Beta 与 M40 gate 保留。 |
| [FileStreamingTaskCatalog](../../src/SonnetDB.Core/Streaming/FileStreamingTaskCatalog.cs)、[FileStreamingTaskRunner](../../src/SonnetDB.Core/Streaming/FileStreamingTaskRunner.cs)、[FileStreamingDeadLetterOperations](../../src/SonnetDB.Core/Streaming/FileStreamingDeadLetterOperations.cs) | M43 已有持久任务、订阅、位点、窗口、DLQ/恢复底座 | A05 负责模型调用/结果提交合同；M43 继续负责运输与任务生命周期。至少一次投递不等于外部模型调用恰好一次。 |
| [M27 模型证据](../benchmarks/m27-provider-model-profile.md)、[M27 eval/cost](../benchmarks/m27-copilot-eval-cost.md)、[M35 预算](../m35-filtered-search-budgets.md) | runner、verifier、fixture 回归和报告合同已有 | 复用报告结构，增加应用真实质量/成本。结构合法与内部汇总一致不能证明模型真实运行、硬件真实或效果通过。 |

M44 不关闭 M27 的真实 provider、IdP/broker、双网与 Studio 门禁，不替代 M35 的真实模型/容量，不替代 M40 图生产 gate；M43 远程 CDC/接管与恢复仍按原归属推进。本轮仅规划文档，未执行上述功能或模型测试。

## 2. 代表产品机制与选择理由

按时序 AI、SQL/模型集成、搜索/向量、图证据和 AI 平台覆盖相关产品，不声称穷尽数据库市场。下列官方 `main`、`latest` 与在线页面会变化：实现前冻结具体版本/部署方式/文档快照；未锁定版本或功能范围保持待核，不能作为同条件性能结论。

| 产品与官方来源 | 值得学习的机制 | 结合 SonnetDB 采纳 | 版本、商业与许可边界 |
|---|---|---|---|
| TDengine [README / TDgpt](https://github.com/taosdata/TDengine/blob/main/README.md)、[产品介绍](https://github.com/taosdata/TDengine/blob/main/docs/en/02-product-intro/index.md) | 时序预测、异常检测与数据库数据路径结合；围绕设备指标形成分析流程 | 先把已有算法与分组/窗口、诊断报告、反馈结合；新模型通过显式 provider/profile 接入 | `main` 非固定发布版；TSDB 核心、TDgpt 与企业 IDMP 的交付/模型范围分别核查。TDengine 核心 AGPL v3，不能复制后继续仅标 MIT。 |
| IoTDB [Common Concepts / AINode](https://iotdb.apache.org/UserGuide/latest-Table/Background-knowledge/Common-Concepts_apache.html) | 注册已训练 ML 模型，通过 SQL 执行推理；计算节点与数据库职责区分 | 模型注册/输入 schema/版本、批推理和结果查询分层；统计预测与 LLM 调用不混成一个函数 | `latest-Table` 为可变文档，具体 AINode 版本/资源/接口待部署锁定；Apache 项目与每个模型资产许可分别核查，不由 Apache 2.0 推断模型权利。 |
| Timescale [pgai](https://github.com/timescale/pgai)、[pgvector](https://github.com/pgvector/pgvector) | 数据附近的模型调用与 embedding 工作流；向量距离/索引与普通过滤相结合 | generation、来源版本、删除/更新同步及模型异步任务；SQL 只公开可有界验证的能力 | 两个项目与托管 Timescale 产品分别看待；pgai、pgvector 的仓库许可为 PostgreSQL License，不称 pgai 为 Apache 2.0；具体版本/API 待冻结。 |
| MongoDB [Vector Search overview](https://www.mongodb.com/docs/atlas/atlas-vector-search/vector-search-overview/) | 文档属性、向量召回/预过滤与聚合流程结合 | 原始 Document 作为权威记录，派生向量按 profile/版本同步；把过滤正确性与 filtered Recall 同时验收 | Vector Search 不能笼统称为仅 Atlas 云服务，self-managed/local 支持要按版本核查；automatic embedding 的支持范围另核。Server、Atlas、搜索组件和模型服务条款分别核查。 |
| Elasticsearch [检索文档](https://www.elastic.co/docs/solutions/search)、[LICENSE](https://github.com/elastic/elasticsearch/blob/main/LICENSE.txt) | 关键词与语义召回、融合/重排及 relevance 评测 | 接已有 FullText/Vector/RRF，冻结 analyzer、候选与重排预算；用真实 query labels 验证收益 | 不把全文引擎、ML/inference、`x-pack` 与商业订阅混为同一授权范围；`main` 源许可存在 Elastic License 2.0/AGPL v3/SSPL 选择及组件范围，交付包/功能须逐项核查。 |
| Qdrant [Hybrid Queries](https://qdrant.tech/documentation/concepts/hybrid-queries/) | prefetch 多路候选、RRF/DBSF、阶段式检索；候选数影响最终结果 | 为已实现融合补分阶段指标、过滤召回和重排对照，依据语料选择策略；无证据不增加算法清单 | 本轮取得官方正文，说明 Query API 与嵌套 prefetch；可变在线页不能代替目标版本，具体高级策略版本待冻结。开源引擎与 Cloud 服务分别核查。 |
| Weaviate [hybrid search](https://docs.weaviate.io/weaviate/search/hybrid)、[LICENSE](https://github.com/weaviate/weaviate/blob/main/LICENSE) | BM25/向量混合、模块化 vectorizer/reranker 与检索配置 | 模型能力发现、可替换重排接口、中文/多语言基准，保留 local-only | 不把托管服务/企业能力算作所有开源版本现状。仓库 `wl` 企业目录与其余 BSD 3-Clause 范围分开，模块与模型许可另核。 |
| Neo4j [GraphRAG Python](https://github.com/neo4j/neo4j-graphrag-python) | 向量/图联合检索与有出处的上下文，知识构建管线 | 复用现有 claim/provenance/valid-time/内容引用；有界图扩展与纯混合检索做同任务消融对照 | Python 包默认 Apache 2.0、少量文件有 PSF 声明，不能推断 Neo4j 数据库版本/企业许可；不复制其全套生态作为九模型必选。 |
| Snowflake [Cortex AI Functions](https://docs.snowflake.com/en/user-guide/snowflake-cortex/aisql) | SQL 调用完成、分类、抽取等任务；模型权限、区域、成本和 evaluation 分开 | 任务化结构化输入/输出、模型准入、权限与成本账本；直接查询与批任务区分，避免不透明费用 | 本轮取得官方正文；托管商业平台能力，区域/模型/权限与计费随服务变动，不算开源数据库可内嵌能力。 |
| Databricks [AI Functions](https://docs.databricks.com/aws/en/large-language-models/ai-functions) | `ai_query` 与专门任务函数，SQL/工作流组成批量推理管线 | 可恢复批推理、质量回归、输入快照与结果版本，优先使用现有调度底座 | 本轮取得官方正文；计算和模型推理可能分别计费，Classic SQL warehouse 支持有限制；托管商业产品，云/版本/地区分别核查。 |

采纳共同原则：模型输入保持类型和来源，provider 能力显式发现；模型调用受预算治理；原文/结果与派生索引分开；失败、更新、删除和换代可恢复；每个应用用真实任务评价。先进算法数量、网页展示和商业产品功能清单都不作为完成标准。

## 3. 九模型的应用职责与约束

| 原生模型 | AI 应用职责 | 4.5 应验证的合同 |
|---|---|---|
| 时序 | 时间特征、趋势/异常/变点、预测及预测误差反馈 | 观察时间/有效时间、时间切分、分组、缺失/乱序与 counter reset；不得读取未来数据参与预测。 |
| 关系 | 设备/业务实体、指标维度、标注、工单、特征和模型运行元数据 | 类型/NULL/名称合同；SQL 与指标可重算；模型建议写回必须走现有审批及事务范围。 |
| KV | 当前状态、feature lookup、缓存、幂等身份与短期会话 | 缓存键含源版本、profile、ACL/租户和有效期；TTL/恢复与允许的原子性范围公开。 |
| 文档 | 知识、标注、结构化输入、诊断/抽取结果 | 内容/版本为权威，更新/删除/TTL 使派生失效；模型返回字段验证后才能存储。 |
| 全文 | 关键词、编号、精确术语、中文/多语言证据 | analyzer 固定；ACL 在召回前应用；返回命中数量不替代真实相关性。 |
| 向量 | 语义召回、相似案例、模型特征 | profile/维度/度量/归一化一致；filtered Recall 与 ANN/精确回退成本、重建/回滚可测。 |
| 对象 | 原文、图片/音视频、模型资产及报告附件 | checksum/ETag/不可变版本、Range/大小预算、出域与引用删除；大字节不塞入 Graph property。 |
| MQ | 摄取/推理/重建触发、告警与结果通知 | 至少一次、稳定事件身份、backlog/背压；offset/任务恢复按实例合同，不声称单库备份包含 MQ。 |
| 图 | 实体关系、路径证据、时间有效性与影响传播 | 深度/节点/边/时间预算，出处/权限/版本；不支持的或缺证据的诊断明确拒答，保持 Beta 边界。 |

九模型协作不自动产生跨模型原子事务。应用保留源版本/提交身份/派生 generation，定义发布顺序、可见性、对账与补偿；恢复时按 V45-X04 验证一致性点。A07 的三个旅程共同覆盖这些职责，不要求每次提问强行触发九个模型。

## 4. 十个工作包

接口名称与字段在实现 PR 中冻结；以下为语义合同，不声明新增 public API 已存在。增量 API 采用兼容扩展，不破坏已有 `IChatProvider`、embedding、SDK/MCP 与帧合同。

### M44-A01 — profile、能力与结果合同（P0 必选）

依赖 M27/M35 的 model catalog、ONNX profile、`EmbeddingProfile`、RAG generation。已有 profile/维度换代与发布复用；补充 chat、时序推理和结构化任务统一需要的版本/能力信息，不重写现有注册。

输入合同冻结任务种类、schema、时间单位/时区、模态/尺寸、provider/model/revision、预处理版本、资源/出域策略。能力声明区分完整回复、原生流式、embedding、预测、分类、抽取和 batch，不凭 endpoint 名称猜测。结果记录实际 model/profile、输入快照、usage 是否由 provider 报告、finish reason、失败分类、出处和结果 schema；不把字符数换算为已报告 token。

验收：错误维度/模型/version/schema 在调用或发布前稳定拒绝；legacy 384 维与新 RAG profile 路径分别对拍；升级回读/回滚不混合向量空间。模型名称可用而资产未加载时，readiness 仍是配置检查；真实调用证据归 A06。

### M44-A02 — 应用治理与成本边界（P0 必选）

依赖现有访问控制、写入审批、RAG 出域/审计、MCP、V45-X02 总资源预算。现有 `SemanticDataEgressPolicy` 及调用前审计复用；增量是跨检索、推理、缓存和任务的治理接线与可验收成本。

接口包含被授权的数据范围、批准 provider、总 deadline、请求/输出字节、token（可准确计数时）、并发/排队、重试次数、硬成本准入或保守预授权额度，以及稳定 run/task ID。未报告 usage 为 unknown；无准确计数/价格证据不得假称账单硬上限。每次重试扣减相同任务预算，provider 不支持取消时记录调用仍可能完成/计费。

验收：未授权内容不进入召回/prompt/reranker；最终结果复核权限/版本；缓存不跨 ACL/模型复用；local-only 不外发；敏感凭据不入 trace；超预算/取消后不发布伪成功。排队和推理释放与前台 SQL/检索公平性在 V45-X02 对拍，未知计费不记录为零。

### M44-A03 — 时序 AI 与诊断应用（P0 必选）

依赖已有 forecast/anomaly/changepoint、M45-C01/C05/C07 的分组、窗口及时间函数，V45-X08 的实体/静态属性映射。先定义可复算的特征流水线和输出语义，再依据残差/任务收益选择新算法。

输入明确实体/分组、时间范围、采样/聚合、缺失/重复/乱序、最小样本、预测 horizon 与反馈标签；短期输出是预测/异常分数、检测事件和基于证据的诊断。区分统计关联、运维假设和已证实根因，不把 LLM 文本当成故障事实或自动控制指令。

验收：冻结 train/validation/test 时间切分，按实体/场景防泄漏；对照 last-value、seasonal-naive 等同任务基线；报告 MAE/MASE（适用时）及区间实际 coverage，异常报告 precision/recall、误报/实体日和检测延迟；数据缺失/不规则采样/漂移/季节变化与失败样本保留。模型不能战胜适用简单基线时维持基线或 `NOT_READY`，不借合成信号通过效果门禁。

### M44-A04 — 有出处的混合检索与证据回答（P0 必选）

依赖 M35 持久摄取/profile/generation、全文/向量、已有 Hybrid/RRF/重排与 A01/A02。实现重点是多来源证据完整性与真实检索效果；不新增第二套原文、向量或融合存储。

输入包含 query、授权范围、source version、candidate/rerank/top-k/text budget 和 profile；证据包含稳定 content/chunk ID、出处、版本、时间、授权范围和检索过程。答案能追到具体证据；回答前校验引用仍有效。无证据、矛盾、过期及预算失败各有明确结果，不返回未标识的部分成功。

验收：真实中文/多语言/专业术语/编号 queries；分别比较关键词、向量、hybrid、hybrid+rerank 的 Recall@K/nDCG/MRR（按任务选用）；报告 citation 支持率、无证据拒答率与人工判分。更新/删除/TTL/ACL 变更后答案和缓存不能继续引用失效证据；模型评审与独立人工抽样分开，不把另一 LLM 的分数视为唯一事实。

### M44-A05 — 可恢复批量推理（P0 必选）

依赖 M43 task/subscription/checkpoint/DLQ、M35 writer/generation、A01/A02 和 V45-X03/X04。新增分类/抽取/摘要/时序推理结果的批任务合同；embedding 摄取与重建继续复用现有入口。

任务冻结输入快照/版本、profile、output schema、row/byte/token/time budget、批次/并发、稳定输入/任务身份及重试策略。模型调用与本地结果提交分离；只在校验并持久提交结果之后确认已处理输入。幂等身份至少包含源对象版本、任务版本/profile 和操作种类；长输入按有界批次处理，pending 与 active 发布状态可区分。

验收：故障矩阵包含调用前、调用后提交前、结果提交后 ack 前、取消、重投、provider 429/5xx、永久坏输入、磁盘满、进程 kill/reopen 与模型换代。结果不因重投重复发布，积压/DLQ/恢复可观测。provider 没有幂等调用/结果查询时，调用后断线归 `unknown_external_result`，不能承诺调用或费用 exactly-once，也不能静默重试收费操作。

### M44-A06 — 真实模型与应用评测（P0 必选）

复用现有 M27/M35 runner/verifier，评测语料、阈值与报告合同随 A01/A02、V45-X06 先行冻结；最终真实评测依赖 A01~A05 和 A07 的相应实现。增量为三旅程评测集、版本/成本账本、错误切片和候选门禁，复用已有报告而非另造只统计 PASS 的 runner。

接口绑定 commit、OS/RID/硬件、模型/provider 实际版本、资产/tokenizer/profile SHA、来源/许可、输入 corpus/query/label SHA、价格生效时间、命令与原始逐样本输出。记录 P50/P95/P99、首 token（支持时）、失败/取消/超时、peak working set/heap、模型调用次数、重试与 usage/cost。在线无法锁定模型 revision 时记录 provider 返回信息及时间，不伪造固定模型证据。

验收：下面的质量/性能门槛在实现前按任务校准并冻结；未校准/缺真实资产为 `NOT_READY`，未执行为 `NOT_RUN`，格式或重算不一致为 `INVALID`。hash fallback、tiny fixture、mock/ScriptedChatProvider 仅合同证据。复算器验证内部一致性，不自称签名验证、硬件 attestation 或重新执行真实模型。漂移监控与模型回滚须有可操作报告。

### M44-A07 — 三条可交付应用旅程（P0 必选）

依赖 A01~A06、M36 九模型入口、M45 聚合、V45-X08 实体映射及适用 M35/M40/M43 gate。复用 [工业诊断样例](../../samples/SonnetDB.IndustrialDiagnostics/README.md)，行业规则留在样例/上层应用；追加业务与知识资产完整旅程。

| 旅程 | 输入与输出 | 必须走通的验证 |
|---|---|---|
| 工业诊断 | 设备/站点指标、台账、维修文档、报警与关联；异常排序、预测风险及带出处巡检建议 | 真实时间切分、误报/诊断证据、缺失/乱序与断网；建议工单写回需审批，不能自动操作设备。 |
| 业务分析 | 业务事实/维度、指标时间序列、规则文档；可复算指标、趋势解释与运营建议 | SQL/聚合正确性、实体/业务权限、数字与出处逐项一致、源修正后结果刷新、批准的标注/报告写回。 |
| 知识与资产助手 | 文档/对象及图片元数据、全文/语义案例、可选图关系；有证据的查找/问答 | 真实相关性、版本/ACL/删除、模型换代回滚、资产 Range/checksum、无证据拒答与任务 kill/reopen。 |

三者共同覆盖九模型角色，并分别通过嵌入式 API 与真实 Server/SDK/MCP。记录真实 provider 和数据链路状态；脚本报告或 mock 不升级为模型效果。UI 展示来源、版本、失败/未知状态及实际可用操作，用户无需了解存储实现即可判断证据可信度。

### M44-A08 — provider 原生流式（P1 条件）

依赖 A01/A02/A06 与现有 Copilot event/broker relay；在不破坏 `CompleteAsync` 的前提下扩展能力明确的原生流式入口。仅在目标 provider 支持且真实旅程需要首 token 改善时纳入，优先验证取消、usage、错误和背压。

验收：完整响应/provider token stream/Copilot events/relay replay 四层分别对拍；首 token、结束原因、最终 usage、慢消费者、中断/重复事件和总预算可测。relay 续流不等于 provider 可以从任意 token 恢复；无法继续时返回明确状态，不拼接两次不同 generation 为一次回答。

### M44-A09 — GraphRAG 证据深化（P1 条件）

依赖 A04/A06、现有 M40 #365 合同/SDK 与适用 M40 gate。使用已有源/chunk/version、claim、confidence、valid time 和社区摘要引用；增量是有界路径/关系证据、抽取任务回滚与权限/时间过滤。

验收：纯关键词/向量/hybrid 对照 graph 扩展的真实多跳任务；路径深度、节点/边/文本与 deadline 可控；保留矛盾事实及来源，不以 confidence 数字冒称校准概率；更新/撤回/删除传播与坏引用拒绝。无法证明质量收益或底层 gate 不满足时延期或降级到 A04，不关闭 Graph Beta。

### M44-A10 — 高级离线、多模态与自适应候选（P2 后续）

依赖 A06 已证明瓶颈/场景与真实模型资产许可。现有文本/图片 provider 和模态合同复用；候选包括任务相关的更高级预测模型、OCR/音视频理解、轻量离线生成及 drift-triggered profile 切换。

进入条件：单项任务、确定输入/输出、模型 provenance/许可、runtime/AOT 隔离、CPU/GPU/内存/冷启动预算、质量与简单基线对照、恢复/回滚。具备模态 enum 不意味着已有音频/视频模型；不默认承诺通用训练、foundation model、自动在线自学习或 GPU 集群。

## 5. 质量、性能与成本门禁

冻结门槛时必须填写真实语料、独立 test split、版本、硬件、基线值、允许退化/改善、样本量和统计方法；下表是需要测量的维度，不预填未经校准的数值或提升倍数。

| 对象 | 最低评测维度 | 完成判定 |
|---|---|---|
| 时序预测 | naive/seasonal-naive 对照，MAE/MASE/适用误差，按 horizon/实体切片，区间 coverage | 真实独立时间测试；目标切片的阈值冻结并通过；采样/缺失处理写入报告。 |
| 异常/诊断 | precision/recall、误报/实体日、检测延迟；弱标签与人工确认区分 | 适用任务达冻结阈值；相关性假设与已证实原因分开，失败案例可复核。 |
| 检索 | Recall@K/nDCG/MRR、过滤选择率、多语言/精确编号；ANN 与精确搜索对照 | 真实标注/独立 queries；hybrid/rerank 相对基线收益与 CPU/延迟代价分别报告。 |
| 证据回答 | 数值/SQL正确性、出处支持、矛盾/无证据拒答、ACL/删除/更新与引用有效性 | 禁止读取未授权内容等确定性合同必须通过；质量阈值与人工抽样方法先冻结。 |
| 批量推理 | checkpoint、幂等提交、kill/reopen、取消、重投、DLQ/积压和未知外部结果 | 适用故障矩阵通过；源版本/profile 绑定，费用与结果未知明确暴露。 |
| 资源/成本 | P50/P95/P99、首 token（适用时）、吞吐、峰值内存/GC、网络/I/O、usage/重试/费用 | 固定机器/同输入/同持久性与客户端；未知 usage/cost 保持 unknown，不零填、不估算伪报。 |
| 本地模型 | 模型/tokenizer/profile 哈希、实际 session/runtime、cold start 与峰值内存 | 原始运行证据证明不是 fallback，真实质量与合同/AOT证据分开。 |
| 长期/跨端 | 双网、provider错误、恢复、权限、删除、profile rollback 与 Server/SDK/MCP | M27/M35/M36/M40/M43 原 gate 独立；开发机、mock 或缩规模不代替目标环境。 |

算法优化以实测定位：先看预处理/批大小、过滤选择率、索引候选/重排窗口、重复模型调用、缓存命中和峰值内存，再考虑新模型。优化不能降低 ACL、删除一致性、无损原文或确认写持久性；同一真实 query set 比较质量、延迟和费用，保留回滚路径。

## 6. 执行顺序与范围归属

1. **冻结基线和合同：** A01/A02/A06；用已有 provider、profile 和 runner 填写支持矩阵、真实数据/标签及门槛。资产或价格未就绪保持 `NOT_READY`。
2. **推进三条应用：** A03/A04 与 M45 分组/窗口并行；修复真实任务中的入口/语义缺口，复用现有函数和检索。
3. **完成任务与恢复：** A05 接 M43 与 V45-X03/X04；对账 source/checkpoint/result/generation，复核取消/未知费用。
4. **走通旅程并评测：** A07 + A06；嵌入式、真实 Server/SDK/MCP 与权限/删除/失败/重开，固定候选后执行适用外部门禁。
5. **决定条件项：** A08/A09 经真实收益/基础 gate 评审后交付或明确延期；A10 只按单项任务评估。

M44 负责应用语义、模型治理和真实效果；M45 负责聚合/持续计算的类型、state、merge/retract 和批流等价；M46 负责编码/读成本；M43 负责运输/任务/远程合同；V45-X01/X02 负责全链路读取和总资源治理。一个实现 PR 只做一个可审查切片，新增工作不能重复关闭 M27/M35/M40/M43。

不纳入 TsFile 适配、导入导出或持久格式替换，不因研究外部产品扩大本版范围。Core 保持 Safe-only、零第三方运行时依赖；模型 runtime/SDK 在原有非 Core 边界按需使用。JSON 使用 source-generated context、public API 中文 XML 文档，适用 AOT/trim 不增加相关警告；外部组件和模型资产逐项核查许可。

## 7. 证据与维护

交付证据至少按合同回归、真实服务、真实模型质量/成本、目标硬件容量、恢复与长期运行分别登记。专题新增的是规划；未来实现通过代码入口及适用门禁后，才在 CHANGELOG 记录实际完成范围，不把本页计划转写为已实现功能。

外部来源核查包括父任务已读的 TDengine/IoTDB 和组件许可证，以及本专题新增取得正文的 Qdrant Hybrid Queries、Snowflake Cortex AI Functions、Databricks AI Functions（2026-10-03）。其它链接用于追踪官方机制与版本边界；未冻结版本/商业范围的细节保留待核。外部来源不提供 SonnetDB 相对性能胜负证据。
