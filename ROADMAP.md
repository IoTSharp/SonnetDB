# ROADMAP

本文件只保留当前仍需交付、补证或继续深入的工作。已经完成的里程碑压缩为结果摘要；历史 PR 拆分与设计记录见 [docs/roadmap-history.md](docs/roadmap-history.md)。

图例：✅ 已完成 / 🚧 进行中或待补证 / 📋 计划中 / ⏸️ 暂停 / ➡️ 移交

## 完成判定

2026-07-14 起，里程碑只有同时满足以下条件才标记为完成：

1. 代码存在，且真实产品入口已经接到该实现；占位类型、未调用服务或仅有 UI 原型不算完成。
2. 自动化测试覆盖主要合同，并至少完成一次与声明相符的运行验证。
3. 涉及 CI、nightly、容量、发布或 Marketplace 的声明，必须有对应 workflow、报告或已发布产物证据。
4. 文档描述与实际依赖、调用链和限制一致；“计划采用”不能写成“已经基于”。

本轮核查基于合并提交 `59ecd3a`，并复核 Core、Server、EF Provider、IoTSharp compatibility、Web/Studio、VS Code、Parity、容量报告和发布流程。

## 里程碑总览

| Milestone | 主题 | 状态 | 核查结论 |
|---|---|---|---|
| 0~13 | 引擎、SQL、服务端、函数、向量底座 | ✅ | 实现与测试已落地，详情归档。 |
| 14 | SonnetDB Copilot | 🚧 | MCP、知识库、skills 和自研 `CopilotAgent` 已落地；Microsoft Agent Framework、本地 ONNX 执行和在线 provider-neutral 接线未完成，转入 M27。 |
| 15~17 | GEO/轨迹、Copilot UX、可观测性 | ✅ | 功能与测试已落地；会话以服务端持久化为准，不回退 `localStorage`。 |
| 18 | SonnetDB for VS Code | ✅ | `0.4.1` 已发布；smoke、隔离 VSIX 安装和本地/Marketplace SHA256 对拍通过。 |
| 19 | 生态适配底座 | 🚧 | #109~#124、#126/#126.1 实现完成；#125 四个默认容量档缺固定目标硬件报告。 |
| 20 | 多模型 Parity | 🚧 | 套件、宿主 readiness 与失败路径结构化 summary 已实现；仍需完整 scheduled run 和 7 天 nightly 连续证据。 |
| 21 | Document Store 单机能力 | ✅ | 常用单机 Document 子集已落地。 |
| 22 | 上层应用/示例候选 | ⏸️ | 不作为 SonnetDB 内置里程碑；通用能力缺口再回收。 |
| 23 | 搜索与向量引擎合并 | ✅ | DotSearch / DotVector 能力已收编。 |
| 24 | Document 管理面 | ✅ | Explorer、Validator、导入导出和维护入口已接入共享工作台。 |
| 25 | Document 验收与发布治理 | 🚧 | parity 与文档完成；#174 仅有 1 万文档 quick 证据，百万/千万档未验证。 |
| 26 | 连接器路线 | ✅ | C ABI 与多语言入口已交付，连接器 release workflow 通过。 |
| 27 | AI / Agent 数据访问与治理 | 🚧 | 产品定位已校准；工具合同、运行时接线、工业 Demo 和 eval 仍有实际缺口。 |
| 28 | 可靠性、并发与热路径加固 | ✅ | P0~P5 与 SDK 补口已收官。 |
| 29 | 多模型统一管理工作台 | 🚧 | Web/Studio/VS Code 功能与合同已落地；Studio 安装包和宿主生命周期仍需实机验收。 |
| 30 | Sparkplug B / CoAP / UDP 接入 | ✅ | 协议入口、生命周期、安全、parity 和基准已落地。 |
| 31 | 时序聚合类型语义 | ✅ | selector / categorical aggregates 已落地。 |
| 32 | Document MongoDB-like 易用性 | 🚧 | SDK builder、分页 cursor 与稳定游标错误码已落地；批量结果、缺失查询/更新/索引语义、迁移工具和 gap report 继续推进。 |
| 33 | 时序聚合执行与下推 | ✅ | Geo 正确性、多聚合复用、残差流式化、count(*)、LIMIT/latest-N 下推已落地。 |
| 34 | Modbus TCP 内建映射表 | 📋 | 尚未开始。 |
| 35 | 语义内容与多模态检索 | 📋 | 尚未开始。 |
| 36 | 八模型专用品类易用性对齐 | 📋 | 已完成参照分析；按真实缺口吸收高频工作流，不做协议或产品全集兼容。 |
| MM9 | 多模型备份恢复第一批 | ✅ | `BackupService` 与 `sndb backup` 已落地。 |

## 当前推进顺序

1. 恢复 M20 Parity nightly 的有效报告，并补齐 M19/M25 目标硬件容量证据。
2. 完成 M27 的真实 provider/Agent 接线、工业 Demo 和 eval，消除历史虚标。
3. 收口 M29 Studio 安装包/宿主生命周期实机验收。
4. 按真实差距推进 M32，不重复实现已有 update/index/change feed/UI 能力。
5. M34 先做合同、DDL 和安全边界；M35 在过滤 ANN 与内容生命周期地基完成后再做媒体场景。
6. M36 先完成八模型 golden journey 与 gap catalog；实现顺序为高频客户端工作流 -> 查询诊断 -> 高级治理，Document 继续归 M32，向量高级项复用 M35 地基。

## 待补验收证据

### M19 — 生态容量证据

#125 runner、workflow 和缩规模 quick 验证已经完成，但容量声明尚未闭环。固定规格目标硬件必须分别运行并归档：

- `high-cardinality`：默认 1,000,000 series。
- `small-segments`：默认 10,000 segment。
- `maintenance-chaos`：默认 20 次确定性 kill/reopen。
- `many-measurements`：默认 10,000 measurement。

每份报告必须记录 commit、机器/磁盘规格、持续时间、working set/托管内存峰值、查询与恢复 P50/P95/P99，以及 missing/duplicate/unexpected/value mismatch。缩规模 PASS 不能代替发布容量证据。

### M20 — Parity nightly 证据

Parity 场景、适配器和 compose 已存在，但“完成”还需要：

- ✅ workflow 已改为宿主 readiness 探测；restore、build、stack 或 test 失败仍生成带稳定 `gap_reason`、commit SHA 和门禁分类的 schema v2 summary，并保留容器诊断。
- 修复后的完整 compose 仍需在 CI 中健康启动并实际运行全部场景，而不是只通过 `docker compose config`。
- scheduled workflow 连续 7 天成功率不低于 95%，每次都生成非空 summary 和结构化 `gap_reason`。
- NATS、VictoriaMetrics 等第三方镜像的健康检查不得依赖镜像内不存在的 shell/wget；探活由宿主 workflow 或可用的原生命令完成。
- 失败 run 必须保留容器日志、测试报告和 commit SHA，不能发布 `No summary was produced for this run.` 作为完成证据。

### M25 — Document 容量证据

- ✅ MongoDB 参考 parity、Document 能力矩阵、迁移边界和 1 万文档 quick profile 已完成。
- 🚧 在固定目标硬件运行 `million` 与 `ten-million` profile，归档写入、查询、rebuild、TTL、热/冷启动、crash recovery、backup/restore 和内存曲线。
- 没有对应 PASS 报告前，对外只声明“profile 可执行，规模未在目标硬件验证”；当前发布证据仅支持 1 万文档级完整治理闭环。

### M29 — Studio 实机验收

Web/Bridge smoke、Server 管理合同、Web Admin、Studio Release build 和 VS Code consumer smoke 已覆盖实现。剩余验收只保留：

- 在干净 Windows 环境安装 Studio 安装包，验证首次启动、升级/卸载和数据目录保留策略。
- 验证托管 Server 的启动、停止、异常退出、宿主退出策略、端口冲突和日志/健康状态。

上述功能性实机验收完成后，M29 转为 ✅。

## Milestone 27 — AI / Agent 数据访问与治理

目标是在不改变 SonnetDB“八种数据模型，一套引擎”核心定位的前提下，为 Copilot、MCP 和外部 Agent 提供受权限、审计与人工确认约束的数据访问能力。工业数据诊断是验证该能力的示例之一，不是产品类别。当前实现不是 Microsoft Agent Framework：实际为 `Microsoft.Extensions.AI` 抽象加自研 `CopilotAgent`；`LocalOnnxEmbeddingProvider.EmbedAsync` 尚未执行模型；在线 `/v1/copilot/chat` 只走 `ICopilotCloudGatewayClient`。在接线完成前，文档必须如实描述这些边界。

| 项目 | 剩余交付 | 状态 |
|---|---|---|
| #182 产品定位校准 | README / README.en、文档首页、`llms.txt` 和产品欢迎页统一为“八种数据模型，一套引擎”；实现语言、部署方式、行业场景和 Agent 能力按层表达，不进入一级定位。 | ✅ |
| #183 MCP 合同 | 为现有 list/describe/sample/query/explain/docs 工具形成稳定 typed contract，写清参数、返回、权限、错误和版本兼容；不新增大工具面。 | 🚧 |
| #184 工业 Demo | 用 MQTT/HTTP 写入温度、电流、振动，演示异常设备查询、维修建议、引用和报告；数据模型、脚本、文档和视频口径一致。 | 📋 |
| #185 Provider 接线 | 配置样例和模型分组已完成；仍需让在线 Chat 按配置走 `IChatProvider` 或云 Gateway，并实现可运行的本地 embedding/provider 路径。 | 🚧 |
| M14 纠偏 | 接入最新 Microsoft Agent Framework 并以测试证明，或继续明确标注“自研 orchestrator”；实现本地 ONNX 前不得宣称 bge-small-zh 已可用。 | 🚧 |
| #186 写审批 | 已移交 M29，共享 staged preview/dry-run/confirm 已完成；M27 只消费。 | ➡️ |
| #187 Eval/成本 | 增加异常设备、慢查询、schema、维修建议和审批场景，记录 provider/model/tool/失败原因/token 成本，并给出可复现报告。 | 📋 |
| #188 上层边界 | IoTSharp 联合样例归 IoTSharp；SonnetDB 只提供授权 MCP、通用引擎和 Agent 素材。 | ✅ |

验收要求：AI / Agent 文案不得替代多模型引擎的核心产品定位；本地关闭云端外发时仍有一条可运行路径；高风险写入必须经权限和人工确认；外部 Agent 只通过授权 MCP/HTTP 合同访问，不直读目录或系统表。

## Milestone 32 — Document MongoDB-like 易用性

已实现基线：`$set/$unset/$inc/$min/$max/$rename/$push/$pull/$addToSet/$currentDate`、upsert、update preview、compound/unique/sparse/partial/TTL 索引、planner/validate、Document change feed，以及 Web/Studio 查询、更新、索引和 feed 界面。以下计划不得再次把这些能力列为待实现。

| 方向 | 真实剩余工作 | 状态 |
|---|---|---|
| SDK | ✅ 类型化 filter/projection/sort/update builder、AOT 类型值、分页 cursor 与 invalid/mismatch/expired/stale 标准错误码；🚧 统一批量结果模型仍与下方混合 Bulk 一并推进。 | 🚧 |
| 更新 | `$mul`、`$pop`、`findOneAndUpdate` 的 before/after 返回语义和完整单文档原子性。 | 📋 |
| 查询 | `$elemMatch`、`$regex`、`$type`、`$size`、`$all`、复杂 `$not`、嵌套 path 边界和基础 collation。 | 📋 |
| 索引 | multikey 与 wildcard 语义、恢复一致性、planner/EXPLAIN；现有 compound/unique/sparse/partial/TTL 不重做。 | 📋 |
| Aggregation | 深化 `$unwind/$project/$group` 表达式，评估 `$lookup/$facet/$bucket` 的 SonnetDB-native 子集，并保持流式/分页边界。 | 📋 |
| Bulk | 混合 insert/update/delete/upsert 的 ordered/unordered 结果、分项错误、重试安全和明确的批次事务边界。 | 📋 |
| 迁移 | 可执行的 MongoDB dump/NDJSON/Extended JSON 导入、索引建议、dry-run 和机器可读差异报告；现有说明文档不等于迁移工具。 | 📋 |
| 收口 | 用结构化 gap report 标记 supported/partial/planned/not_planned，并补示例应用；UI 只在引擎语义完成后开放对应控件。 | 📋 |

边界：不承诺 MongoDB wire protocol、BSON command、官方 Driver 直连、replica set、sharding 或分布式事务；对外只使用 “Document Store” 或 “MongoDB-like workloads”。

## Milestone 34 — Modbus TCP 内建映射表

SonnetDB 同时支持两个明确角色：主站/client 主动轮询外部 PLC/RTU 并写入表；从站/server 暴露受控寄存器映射供外部主站读取或 staged 写入。协议运行时默认关闭，普通 `SELECT` 只读已采集状态，不同步阻塞访问 PLC。

| PR | 交付 | 状态 |
|---|---|---|
| #288 | 定稿 `CREATE MODBUS SOURCE/ENDPOINT`、`FROM MODBUS`、`EXPOSE AS MODBUS` 的 DDL、方向、地址、类型、字节序、缩放、访问和错误策略。 | 📋 |
| #289 | Parser/AST/catalog、版本兼容和 `SHOW/DESCRIBE MODBUS` 元数据。 | 📋 |
| #290 | 地址冲突校验及 BIT、整数、浮点、BCD、STRING 的 Span/BinaryPrimitives 编解码。 | 📋 |
| #291 | 默认关闭的 TCP master runtime、批量读取、轮询、取消、退避、超时、重连和指标。 | 📋 |
| #292 | 受限 SQL 写寄存器、preview/dry-run、权限和审计；远端失败不得伪造本地成功。 | 📋 |
| #293 | 质量位、错误码、source health、latest/history 与 KEEP_LAST/NULL/SKIP/MARK_BAD 策略。 | 📋 |
| #294 | 默认关闭的 TCP slave endpoint、读请求、绑定/白名单/unit id/最大连接数。 | 📋 |
| #295 | 外部写入的 REJECT/STAGED/UPDATE_TABLE 策略、待确认队列和审计；默认 STAGED。 | 📋 |
| #296 | Web/Studio 管理面、模拟 PLC parity、文档，以及 IoTSharp Product/Collection Template/Gateway/EdgeNode 合同边界。 | 📋 |

验收要求：四类寄存器读写与类型转换可对拍；写入不绕过审批、权限和审计；IoTSharp 只通过稳定合同消费，不依赖 SonnetDB 内部 catalog。第一版只做 Modbus TCP，不扩张到 RTU/ASCII、OPC UA、S7 或完整 SCADA。

## Milestone 35 — 语义内容与多模态检索

复用 Object Bucket、Document、FullText、Vector 和 Hybrid Search，建立“原始内容 → 异步提取/embedding → 可重建派生索引 → 检索”的受治理链路。Core 只负责确定性存储和检索，不下载模型、解码媒体或同步调用外部推理。

| PR | 交付 | 状态 |
|---|---|---|
| #297 | Semantic Content 清单、object reference、chunk/segment、状态机和 Embedding Profile 合同。 | 📋 |
| #298 | metadata-filtered ANN、精确补偿/回退、similar-by-id 和可解释 EXPLAIN。 | 📋 |
| #299 | 异步摄取、幂等 hash、重试/取消/背压/重启恢复，以及对象覆盖删除后的对账。 | 📋 |
| #300 | provider-neutral text/image/object embedding 能力发现、外发策略和调用审计。 | 📋 |
| #301 | 图片搜图片、文字搜图片、缩略图/来源/profile/分数展示和工业图片样例。 | 📋 |
| #302 | 通用 RAG 摄取 SDK/CLI、稳定 chunk、增量更新、删除同步和 Copilot 可回滚迁移。 | 📋 |
| #303 | RRF/归一化/去重/rerank hook，以及 Recall@K、nDCG、P50/P95、体积和重建评测。 | 📋 |
| #304 | 音视频 transcript、关键帧和 timecode segment；媒体处理留在可选扩展或外部工具。 | 📋 |
| #305 | 管理面、安全、失败恢复、备份重建、模型换代和 10k/100k 容量基线。 | 📋 |
| #306 | 派生目标、区域、track 与 detector profile 模型，保持原对象为唯一主数据。 | 📋 |
| #307 | 默认关闭且受治理的人脸 1:1 验证/1:N 候选，独立权限、审计、删除和 FAR/FRR/TAR 评测。 | 📋 |
| #308 | ReID、步态、姿态/动作的独立 profile、查询和对应 mAP/CMC/precision/recall 评测。 | 📋 |
| #309 | 车辆外观向量与车牌 OCR；号码以标准化精确索引为主，向量不替代相等语义。 | 📋 |

顺序固定为 #297/#298 地基 → #299/#300 摄取/provider → #301/#302 首批场景 → #303 质量 → #304/#305 扩展收口 → #306~#309 专业视觉。完成 #301 前只宣称“具备多模态检索底座”。所有生物特征能力默认关闭，并要求用途、权限、访问/导出审计、保留期限和删除闭环。

## Milestone 36 — 八模型专用品类易用性对齐

目标是让每种数据模型都保留该品类用户熟悉的高频工作流，同时共享 SonnetDB 的连接、权限、审计、错误和运维边界。M20 回答“能力和结果是否对得上”，M29 回答“管理工具是否有入口”，M32 深化 Document MongoDB-like 易用性；本里程碑只处理从第一次成功调用到分页、批处理、失败恢复和诊断的**产品易用性**，不重复三者已经完成的工作。

参照产品是学习来源，不是兼容承诺。每项能力进入实现前都必须用代码、公开 API、真实产品入口、测试和文档建立 `supported / partial / planned / not_planned` 证据；已存在的能力只补入口或文档，不得重新实现。

### 逐模型分析与取舍

| 数据模型 | 主要参照与学习理由 | 已有基线，不重复建设 | 优先吸收的易用性 | 明确不吸收 |
|---|---|---|---|---|
| Document | MongoDB / MongoDB Compass：文档心智、builder、cursor、局部更新、索引与迁移诊断成熟。 | CRUD、常用 update、分页、索引、aggregation 子集、validator、change feed 和管理面已存在。 | 类型化 builder、常用缺失操作符、multikey/wildcard、混合 bulk、可执行迁移与结构化 gap report，全部归 M32。 | MongoDB wire/BSON command、官方 Driver 直连、replica set、sharding、分布式事务。 |
| 关系 SQL | PostgreSQL：SQL 行为与错误诊断；SQLite：嵌入式零配置；EF Core / DBeaver：.NET 与开发工作流。 | ADO.NET、EF Core、参数绑定、轻事务、主外键/索引/CHECK/ROWVERSION、EXPLAIN 和关系工作台已存在。 | `RETURNING`、`INSERT ... ON CONFLICT` 等高频 DML；可定位错误与稳定 code/hint；实际行数/耗时诊断；连接、取消、超时和 schema migration 的清晰入口。 | pgwire、完整 PostgreSQL 方言/extension、MVCC 全隔离级别、存储过程全集、HA 管理面。 |
| 时序 | InfluxDB：Point/Line Protocol 与批量 Write API；VictoriaMetrics / Grafana Explore：range query 与排障；TimescaleDB：SQL 连续性。 | measurement/tag/field/time、自动 schema 演进、LP/JSON/Bulk、窗口/填充/聚合、Retention、图表与多协议接入已存在。 | 类型化 Point writer、批量 flush/retry/backpressure 与逐项错误；range/aggregate/gap-fill 查询 builder 和流式结果；precision/schema/cardinality/retention 预检与摄取诊断。 | Flux/PromQL 全语言兼容、分布式集群、无限基数承诺、把采集 Agent 或长期任务调度器塞进 Core。 |
| KV / Cache | Redis：原子 key 操作、TTL 和条件写心智；RedisInsight：namespace、类型与过期诊断；.NET Cache：框架接入。 | bytes get/set、many、prefix scan/delete、TTL、INCR/DECR、CAS、`IDistributedCache`、EasyCaching 和 KV 工作台已存在。 | `NX/XX` 风格条件写、get-and-set/delete；AOT 友好的 UTF-8/JSON codec；异步 cursor/pipeline、批量分项结果、hot key/expiry/容量诊断。 | RESP/redis-cli 直连、List/Set/Hash/Stream 全数据结构、Lua、Pub/Sub、Redis Cluster 或跨 keyspace 事务。 |
| 全文检索 | Meilisearch：单一 Search 请求、typo tolerance、filter/facet 和 task 状态；OpenSearch/Kibana：analyzer 与 relevance explain。 | Document 全文索引、BM25、Unicode/CJK/Jieba、exact/fuzzy/phrase/boolean、Document filter、analyze、rebuild、客户端高亮和 playground 已存在。 | 用类型化 `SearchRequest/Result` 汇总现有 mode/filter/page；补 sort、facet distribution、服务端 matched offset/highlight；增加 searchable/filterable/sortable fields、synonym/stopword/typo 设置、analyzer diff、score explain 和 rebuild progress。 | Elasticsearch Query DSL 全集、聚合分析平台、分片副本集群、把全文索引变成第二份主数据。 |
| 向量检索 | Qdrant：point/payload/filter 与 collection UX；`Microsoft.Extensions.VectorData`：.NET 抽象；pgvector：SQL 组合能力。 | Measurement/Document 向量、HNSW/IVF/IVF-PQ/Vamana、精确回退、Hybrid Search、VectorData adapter 和 playground 已存在。 | 以 VectorData 为 .NET 默认入口，补齐 batch/filter/threshold/include 与 SonnetDB extension options；Measurement 仅补其不能表达的原生请求；增加 fast/balanced/accurate preset、维度/模型预检、index health、ANN/scan fallback explain 与质量报告。filtered ANN、similar-by-id 和 Embedding Profile 复用 M35 #297/#298。 | Qdrant/Milvus wire、第二套 collection/vector catalog、自动偷跑 embedding、分布式 shard/replica、用向量替代精确等值语义。 |
| 对象存储 | S3 SDK：stream、conditional request 与 Transfer Manager；MinIO Console：bucket/prefix、multipart 和治理渐进呈现。 | bucket/object、stream/range、continuation、multipart、version/lifecycle/retention/legal hold/audit/quota/presign 和工作台已存在。 | 自动 multipart 的传输管理器、并发/重试/校验和/断点续传/进度；`If-Match/If-None-Match`、metadata/content type；异步分页和带 dry-run 的 `cp/sync`。 | SigV4/S3 wire 全兼容、跨节点复制、纠删码集群、把 prefix 伪装成真正目录或承诺 POSIX 文件系统语义。 |
| 消息队列 | NATS JetStream：简洁 producer/consumer 与 drain；RabbitMQ：ack/nack/retry/DLQ；Kafka：offset/time/lag 与批处理运维。 | publish/batch、pull/ack、consumer offset、持久 replay、push stream、retention、MQTT 桥接和 MQ 工作台已存在。 | 高层 producer/consumer builder、`IAsyncEnumerable`、manual/auto ack、prefetch/backpressure 与 graceful drain；nack/redelivery/max-delivery/DLQ、message-id 去重、offset reset 和可解释 lag。 | Kafka/AMQP/NATS wire、partition rebalance、broker 集群、跨节点 consumer group、未经证明的 exactly-once。 |

### 交付拆分

| PR | 交付 | 状态 |
|---|---|---|
| #310 | 八模型 usability gap catalog 与可执行 golden journey：记录每个常用任务的当前入口、证据、手写样板量、失败恢复和 `supported/partial/planned/not_planned`；与 M20 capability report 分开。 | 📋 |
| #311 | 统一新客户端合同：连接/鉴权、取消/超时、分页、批量分项错误、correlation id、仅对可安全重试操作启用的 retry/idempotency 元数据；不强行抹平各模型概念。 | 📋 |
| #312 | SQL 高频 DML：`RETURNING`、SonnetDB-native `INSERT ... ON CONFLICT` 子集、ADO.NET/EF Core 映射和稳定冲突结果。 | 📋 |
| #313 | SQL 开发诊断：带位置/code/hint 的解析与执行错误、`EXPLAIN ANALYZE` 实际行数/耗时/回退原因，以及取消和超时闭环。 | 📋 |
| #314 | 时序类型化 Write API：Point builder、precision、batch/flush、限界背压、传输级重试、逐项错误和 dispose/drain；嵌入式与远程语义一致。 | 📋 |
| #315 | 时序 Query API 与建模诊断：range/aggregate/window/gap-fill builder、流式结果，以及 schema/cardinality/retention/坏点预检；不新增第二套查询引擎。 | 📋 |
| #316 | KV 条件与类型化 API：NX/XX、get-and-set/delete、UTF-8 与基于 `JsonTypeInfo<T>` 的 AOT JSON codec，保持 raw bytes 为底层权威语义。 | 📋 |
| #317 | KV 大 keyspace 工作流：异步 cursor、pipeline/batch 分项结果、取消/背压和 hot-key/expiry/容量诊断；现有 many/prefix/TTL 不重做。 | 📋 |
| #318 | FullText 高层 Search API：复用现有 query kind、Document filter 和分页，形成 query/filter/sort/facet/highlight/page typed contract；补服务端 matched offsets/terms 与稳定 score metadata。 | 📋 |
| #319 | FullText 设置与诊断：searchable/filterable/sortable fields、synonym/stopword/typo policy、analyzer diff、relevance explain 和可观察 rebuild task。 | 📋 |
| #320 | Vector 高层 Search API：以 VectorData adapter 为默认入口补 batch/filter/threshold/include/exact 与 fast/balanced/accurate preset；SonnetDB-specific 能力用 extension options 表达，不另建 collection API。 | 📋 |
| #321 | Vector 生命周期与解释：dimension/metric/Embedding Profile preflight、index health/rebuild progress、ANN/scan/补偿原因与 recall report；依赖 M35 #297/#298 的部分不得提前复制实现。 | 📋 |
| #322 | Object Transfer Manager：自动 multipart 阈值/part size/并发、checksum、retry、resume、progress、取消和资源释放，基于现有 `SndbObjectStorageClient`。 | 📋 |
| #323 | Object 日常文件流：conditional put/get、metadata/content type、异步 continuation，以及 CLI `cp/sync --dry-run`、冲突与删除保护。 | 📋 |
| #324 | SonnetMQ 高层 consumer：producer/consumer builder、push/pull `IAsyncEnumerable`、prefetch、manual/auto ack、限界背压、取消和 graceful drain。 | 📋 |
| #325 | SonnetMQ 投递失败治理：nack/redelivery/max-delivery/DLQ、message-id 去重窗口、offset earliest/latest/time/explicit reset、lag 与丢弃原因诊断。 | 📋 |
| #326 | 八模型收口：每模型一个嵌入式/远程同代码或最小差异样例，SDK/API/Workbench/CLI 能力矩阵、结构化 gap report 和用户任务 e2e；Document 结果汇总自 M32，不复制任务。 | 📋 |

### 顺序与验收

顺序固定为 #310 先建立证据，#311 固化共享合同；随后优先 #314/#316/#322/#324 四条高频客户端路径，再推进 SQL/全文/向量的查询与诊断，最后以 #326 收口。M32 可独立推进；#321 中的 filtered ANN、similar-by-id 与 Embedding Profile 必须等待 M35 #297/#298，不得另建旁路。

完成要求：每个模型至少有一个 20 行左右的最小成功样例和一个生产化样例；嵌入式/远程对同一合同做 parity；分页或流式读取内存有界；取消可停止真实工作；重试不会把非幂等写静默执行两次；错误包含稳定 code、操作与可行动建议且不泄露数据；对应产品入口和自动化测试真实接线。UI 控件只能在引擎/SDK 语义完成后开放。M36 不以“拥有与参照产品同名功能”判定完成，而以 golden journey 可运行、gap 可解释、失败可恢复来判定。

总边界：不新增任何竞品 wire protocol，不宣称完整替代专用数据库，不引入分布式复制/分片/集群，不为统一表面 API 混淆八种模型的原生语义，也不把外部采集、媒体推理或长运行工作流塞进 Core。

## 性能观察项

以下不是已完成里程碑的遗留验收，只有触发条件成立并取得独立基准后才排期：

| 编号 | 方向 | 进入条件 |
|---|---|---|
| PF1 | 级联删除按选择率切换二级索引或单次哈希扫描 | 在 1/10/50/100 父键矩阵中证明当前固定路径稳定劣于替代路径，并保持语义与事务回滚一致。 |
| PF2 | 高活跃词基数 fuzzy 词典结构 | 100k/500k 活跃 term 场景线性枚举成为主要 CPU 成本，且新结构至少有稳定 2 倍查询收益。 |
| PF3 | ANN tombstone gate/区间索引 | 高墓碑基数下区间扫描成为主要成本，且优化不降低 ANN/精确扫描召回对拍。 |

## 已完成里程碑摘要

| Milestone | 已交付结果 |
|---|---|
| 0~13 | Safe-only 存储引擎、WAL/Segment/Compaction、SQL/ADO.NET、Server/Web、函数、向量与知识库底座。 |
| 15~17 | GEOPOINT/轨迹、Copilot 产品 UX、OTel/结构化日志/诊断/health/慢查询/服务端会话。 |
| 18 | VS Code 扩展 `0.4.1` 发布、smoke、隔离 VSIX 安装和 Marketplace 产物校验。 |
| 21 | Document CRUD、查询/分页、局部更新第一批、索引、aggregation 子集、validator 和单机恢复。 |
| 23 | 全文与向量引擎收编、持久索引与 Hybrid Search。 |
| 24 | Document Explorer、Validator、导入导出、rebuild 与共享审批。 |
| 26 | C ABI 与 Go/Rust/Java/Python 等连接器入口和发布流程。 |
| 28 | 数据可靠性、并发正确性、写/查热路径、索引/向量、SonnetMQ 与全模型高吞吐接入加固。 |
| 30 | Sparkplug B、CoAP、Line Protocol UDP 的接入、生命周期、安全、parity 和基准。 |
| 31 | 字符串/布尔等 selector 与 categorical aggregate 类型语义。 |
| 33 | 聚合正确性、多聚合复用、残差流式化、count(*) 专路和 LIMIT/latest-N 下推。 |
| MM9 | 多模型备份、检查、校验和恢复 CLI 第一批。 |

详细历史只用于追溯，不覆盖本文件的当前完成判定；若历史文档与当前实现冲突，以代码、可执行测试和本文件的审计结论为准。
