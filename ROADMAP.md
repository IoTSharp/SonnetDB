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
| 27 | AI / Agent 数据访问与治理 | 🚧 | 产品定位已校准；工具合同、运行时接线、双网客户端 Copilot、工业 Demo 和 eval 仍有实际缺口。 |
| 28 | 可靠性、并发与热路径加固 | ✅ | P0~P5 与 SDK 补口已收官。 |
| 29 | 多模型统一管理工作台 | 🚧 | Web/Studio/VS Code 功能与合同已落地；Studio 安装包和宿主生命周期仍需实机验收。 |
| 30 | Sparkplug B / CoAP / UDP 接入 | ✅ | 协议入口、生命周期、安全、parity 和基准已落地。 |
| 31 | 时序聚合类型语义 | ✅ | selector / categorical aggregates 已落地。 |
| 32 | Document MongoDB-like 易用性 | ✅ | SDK、查询/更新、multikey/wildcard 索引、aggregation、mixed Bulk、迁移 CLI、Workbench、Quickstart 与结构化 gap report 已闭环。 |
| 33 | 时序聚合执行与下推 | ✅ | Geo 正确性、多聚合复用、残差流式化、count(*)、LIMIT/latest-N 下推已落地。 |
| 34 | Modbus TCP 内建映射表 | 📋 | 尚未开始。 |
| 35 | 语义内容与多模态检索 | 📋 | 尚未开始。 |
| 36 | 八模型专用品类易用性对齐 | 📋 | 已完成参照分析；按真实缺口吸收高频工作流，不做协议或产品全集兼容。 |
| 37 | 视图与物化视图 | ✅ | #327 逻辑视图与 #328 显式全量刷新物化视图均已实现。 |
| 38 | SQL 存储过程与触发器 | ✅ | #329~#332 已完成 SQL 过程、关系表 AFTER ROW 触发器及治理收口；外部脚本运行时保持暂停。 |
| 39 | SQL 触发器第二版 | 🚧 | #333 证据 runner、三条关系表 journey 和真进程 crash 场景已接入；UPDATE/DELETE 同规模成本与固定目标硬件矩阵仍待归档，再决定高级语义与多模型范围。 |
| 40 | 原生属性图数据库 | 📋 | 路线已定稿；按公共地基、Native Graph Preview、SQL/PGQ Graph Beta、生产级单机图数据库四阶段推进，当前尚未实现。 |
| 41 | 关系查询规划与执行性能加固 | 📋 | 木垒生产证据已确认关系查询存在扫描、物化、锁等待和 GC 放大；按可观测性与快速路径、流式执行、统计成本模型、高级 JOIN/spill/并行和生产门禁推进。 |
| MM9 | 多模型备份恢复第一批 | ✅ | `BackupService` 与 `sndb backup` 已落地。 |

## 当前推进顺序

1. M41 P0 作为生产稳定性最高优先级：先完成 #368 基线与可观测性，再交付 #369~#371 的 `EXISTS/IN`、索引并集和倒序 Top-N 快速路径；不得用增加 SQL permit、内存或索引数量代替根因修复。
2. 恢复 M20 Parity nightly 的有效报告，并补齐 M19/M25 目标硬件容量证据。
3. 完成 M27 的真实 provider/Agent 接线、双网客户端 Copilot、工业 Demo 和 eval，消除历史虚标。
4. 收口 M29 Studio 安装包/宿主生命周期实机验收。
5. M34 先做合同、DDL 和安全边界；M35 在过滤 ANN 与内容生命周期地基完成后再做媒体场景。
6. M36 先完成八模型 golden journey 与 gap catalog；实现顺序为高频客户端工作流 -> 查询诊断 -> 高级治理，Document 复用已完成的 M32 结果，向量高级项复用 M35 地基。
7. M39 先执行 #333 触发器 V2 证据门禁；未证明 V1 在真实 journey 上存在缺口前，不直接扩展 BEFORE、statement-level 或多模型触发器。
8. M40 先完成 #341 的 workload/合同证据和 #342~#346 公共存储地基，再进入原生 Graph Preview；Phase 2 的关系映射规划和流式执行必须复用 M41 的公共计划/算子合同，不得另建一套关系优化器。正式产品定位在 M40 发布门禁通过前继续保持“八种数据模型，一套引擎”。

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
| #340 双网客户端 Copilot | 在数据库服务器不能访问公网、浏览器或 Studio 同时可访问内网和公网时，由访问端编排外部 AI 与本地授权工具；保留服务端中继模式，首版只读。 | 📋 |

### #340 — 双网客户端 Copilot

目标是支持典型的双网部署：SonnetDB Server 位于不能访问公网的内网，用户通过具备内外网连接的浏览器或 Studio 使用 Copilot。访问端是 Copilot host，分别连接外部 AI 服务与内网 SonnetDB；外部 AI 不直连内网，数据库服务器也不承担公网代理。

架构必须保持以下边界：

- 数据库凭据只发送到当前活动 SonnetDB 连接的受信 origin，外部 AI 的短期凭据只发送到批准的公网 origin；日志、错误和重试不得串用或泄露两类 token。
- 浏览器或 Studio 负责对话与 tool-call 状态机；数据库授权、SQL AST 分类、行数/字节/超时限制、结果脱敏和审计仍由 SonnetDB 服务端执行。
- 本地工具面优先复用 `/mcp/{db}` 的 typed、只读合同；不得让模型直接调用原始 `/v1/sql`，不得增加任意 URL 代理，也不得把本地 MCP 暴露到公网。
- 外部数据出域必须显式启用，并采用按需 schema、聚合优先、结果截断和敏感列脱敏；工具结果只能作为结构化、不可信数据返回模型，不能拼入 system prompt。
- 运行模式显式区分 `ServerRelay`、`BrowserDirect`、`StudioNative` 和 `Disabled`。不同模式代表不同数据路径，不得根据一次探活结果静默切换。
- 当前代码使用 `ai.sonnetdb.com`。若正式入口迁移到 `sonnet.vip`，必须先冻结版本化的认证、模型发现、流式事件、tool call/result、取消、续流和错误合同，不能只替换域名。

分层交付：

| 阶段 | 交付 |
|---|---|
| Client runtime 合同 | 抽取统一 Copilot transport 和事件状态机，保留现有 server relay；加入独立的本地/公网 readiness、run id、tool call id、sequence/cursor、幂等和取消合同。会话、消息和 usage 仍回写 SonnetDB 服务端持久化，客户端只同步状态，不回退 `localStorage` 作为权威来源。 |
| 本地工具会话 | 基于当前 HTTP 身份和数据库 grant 返回 permission-filtered capability/context；每次工具调用在服务端重新授权并强制限制行数、返回字节、执行时间、并发和总出域预算。 |
| Browser Direct | 外部服务支持 CORS preflight、Bearer public-client OAuth（Device Flow 或 PKCE）及 `fetch` POST 流式 NDJSON/SSE；access token 默认只驻留内存，页面使用 HTTPS 和受限 CSP `connect-src`。 |
| Studio Native | 由固定目标、非通用代理的 native broker 访问公网并使用系统凭据库保存 refresh token/BYOK；接入前收紧 Bridge origin、握手和 token 传递，不能把 bridge token 放入 URL 或浏览器存储。 |
| 治理与写入 | 第一阶段只开放只读 MCP。写工具后续消费 M29 的 staged approval，并增加由服务端签发、绑定 user/database/run/tool-call/规范化参数哈希/expiry 的一次性 confirmation challenge；模型或浏览器的 `confirmed=true` 不构成授权。 |
| 审计与评测 | 记录模式、provider/model、数据库、工具、规范化参数或 SQL hash、行数、字节数、脱敏策略、审批和失败原因，默认不记录原始结果；纳入 M27 eval 与成本报告。 |

#340 验收要求：

- 在 SonnetDB Server 的 DNS 与公网出口均被阻断时，具备双网连接的浏览器和 Studio 均能完成一条真实的只读 grounded Copilot 流程，网络证据确认服务器没有产生 AI 外连。
- 自动化测试证明 SonnetDB token 从不发送到公网、外部 AI token 从不发送到数据库服务器，未知工具、合同版本不匹配和未批准数据出域均 fail closed。
- 覆盖 CORS preflight、分片流解析、取消、断线续流、重复 tool call、客户端代理和四种本地/公网可用性组合；公网不可用时不得自动排队或重放包含数据库结果的请求。
- 本地工具调用保留现有认证与 database grant，并有 SQL AST、系统库、行数、字节、时间和并发门禁；第一阶段不存在可绕过人工确认的写路径。
- 页面刷新或 Studio 重连后可从 SonnetDB 服务端恢复会话；现有 `ServerRelay` 路径继续通过回归测试，客户端模式与服务端模式之间不得静默切换。

验收要求：AI / Agent 文案不得替代多模型引擎的核心产品定位；本地关闭云端外发时仍有一条可运行路径；高风险写入必须经权限和人工确认；外部 Agent 只通过授权 MCP/HTTP 合同访问，不直读目录或系统表。

## Milestone 32 — Document MongoDB-like 易用性

已完成：在既有 update、compound/unique/sparse/partial/TTL 索引、change feed 和管理面基础上，补齐类型化 SDK builder、AOT 友好值、分页 cursor 与稳定错误码；新增 `$mul/$pop`、原子 `findOneAndUpdate`、常用数组/类型/正则查询、递归 `$not` 和基础 ordinal collation。path index 支持 multikey/wildcard、parallel-array 拒绝、typed key、planner/EXPLAIN 与崩溃后派生索引修复；aggregation 支持 computed project/group expression、`push/addToSet` 和完整 unwind。

mixed Bulk 已统一 Core、HTTP、.NET SDK 与 Web/Studio 的 ordered/unordered、逐项结果、单 collection 原子边界、稳定批次上限错误和 24 小时 `requestId` 重放。`sndb document import` 可执行 JSON/NDJSON、常用 Extended JSON 和 mongodump BSON 子集导入，提供 dry-run、checkpoint/resume、确定性批次 ID、索引建议与机器报告；Document Quickstart、OpenAPI、迁移指南和 `docs/document-mongodb-gap.json` 提供可运行入口及 supported/partial/planned/not_planned 证据。

边界保持不变：不承诺 MongoDB wire protocol、BSON command、官方 Driver 直连、replica set、sharding、跨 collection 事务、locale collation、完整 aggregation/BSON 或 positional array update；对外只使用 “Document Store” 或 “MongoDB-like workloads”。

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
| #297 | Semantic Content 清单、object reference、chunk/segment、状态机和 Embedding Profile 合同。当前图片清单、对象引用、派生状态机和 profile 隔离已落地；通用 chunk/segment 合同仍未完成。 | 🚧 |
| #298 | metadata-filtered ANN、精确补偿/回退、similar-by-id 和可解释 EXPLAIN。当前已落地 metadata/tag/source 精确过滤扫描、similar-by-id、自身排除和按需候选解释；预过滤 ANN 优化仍未完成。 | 🚧 |
| #299 | 异步摄取、幂等 hash、重试/取消/背压/重启恢复，以及对象覆盖删除后的对账。已落地 KV 持久化任务、幂等对象版本、5 次退避重试、取消/替代、有界 Channel 背压补偿、重启恢复，以及普通删除、批量删除和生命周期过期后的语义索引/缩略图清理。 | ✅ |
| #300 | provider-neutral text/image/object embedding 能力发现、外发策略和调用审计。当前已落地 text/image provider 合同、SigLIP2 ONNX 与状态发现；object embedding、外发策略和调用审计仍未完成。 | 🚧 |
| #301 | 图片搜图片、文字搜图片、缩略图/来源/profile/分数展示和工业图片样例。已落地原图摄取/读取、文搜图、图搜图、WebP 缩略图、来源/profile/分数 REST 契约、managed/USearch 后端、对象桶管理面和可运行工业图片样例。 | ✅ |
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
| 关系 SQL | PostgreSQL：SQL 行为与错误诊断；SQLite：嵌入式零配置；EF Core / DBeaver：.NET 与开发工作流。 | ADO.NET、EF Core、参数绑定、轻事务、主外键/索引/CHECK/ROWVERSION、`INSERT ... RETURNING`、数据库生成整数键回填、EXPLAIN 和关系工作台已存在。 | `UPDATE/DELETE ... RETURNING`、`INSERT ... ON CONFLICT` 等高频 DML；可定位错误与稳定 code/hint；实际行数/耗时诊断；连接、取消、超时和 schema migration 的清晰入口。 | pgwire、完整 PostgreSQL 方言/extension、MVCC 全隔离级别、存储过程全集、HA 管理面。 |
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
| #312 | SQL 高频 DML：关系表 `INSERT ... RETURNING`、ADO.NET 语句级 last-insert-id 与 EF Core 数据库生成整数键回填已落地；仍需 `UPDATE/DELETE ... RETURNING`、SonnetDB-native `INSERT ... ON CONFLICT` 子集和稳定冲突结果。 | 🚧 |
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

## Milestone 37 — 视图与物化视图

目标是在不复制查询引擎的前提下提供可持久化的查询抽象，并把普通视图与具有物理存储、刷新生命周期的物化视图明确分开。

| PR | 交付 | 状态 |
|---|---|---|
| #327 | 逻辑视图：`CREATE VIEW [IF NOT EXISTS] ... AS SELECT ...`、`DROP VIEW [IF EXISTS]`、`SHOW VIEWS`、`DESCRIBE VIEW`、`information_schema.views`；定义以独立版本化目录持久化，查询通过派生表 AST 展开复用现有表/measurement/document/子查询/JOIN 执行器；拒绝参数化定义、未知依赖、循环/过深展开，并阻止删除或修改被引用基础对象。 | ✅ |
| #328 | 物化视图：定稿 `CREATE MATERIALIZED VIEW`、`REFRESH MATERIALIZED VIEW`、`DROP`、`SHOW/DESCRIBE`；首版仅显式全量刷新，结果写入独立派生存储并用临时代际 + 原子切换发布，刷新失败保留上一可读版本；记录定义版本、刷新时间、状态、错误和依赖。增量刷新、定时调度、后台自动刷新须经独立正确性和容量证据后再排期。 | ✅ |

验收要求：普通视图不保存数据，读取时使用当前基础对象数据；物化视图不得把刷新中间态暴露给读者。两者都不得绕过现有数据库权限、SQL 参数绑定和审计入口。首版不支持 `OR REPLACE`、`CASCADE` 或跨数据库依赖。

## Milestone 38 — SQL 存储过程与触发器

首版只实现 `LANGUAGE SQL`。Catalog 可为未来语言类型保留显式字段，但当前不得加载 C# 脚本、.NET DLL、JavaScript、Python、WASM、Shell 或任意外部进程；这些运行时继续暂停，恢复评估时必须单独完成隔离、权限、资源限额、AOT 和供应链威胁模型。

| PR | 交付 | 状态 |
|---|---|---|
| #329 | SQL 过程合同与目录：定稿 `CREATE PROCEDURE` / `DROP PROCEDURE` / `SHOW PROCEDURES` / `DESCRIBE PROCEDURE` / `CALL`，支持有序 IN 参数、SQL body、创建时间和 `LANGUAGE SQL`；使用独立版本化目录持久化，禁止重载、默认参数、OUT/INOUT 和动态 SQL。 | ✅ |
| #330 | SQL 过程执行：复用现有 parser、binder、executor 和轻事务；参数按 AST 绑定而非文本替换，定义时完成语法与对象依赖校验；明确多语句结果合同、调用方权限、失败回滚、取消、语句数/嵌套深度/结果行数上限与递归拒绝。 | ✅ |
| #331 | SQL 触发器第一版：定稿关系表 `AFTER INSERT/UPDATE/DELETE` 的 `FOR EACH ROW` 语义、`OLD`/`NEW` 只读行上下文、确定性触发顺序和 `WHEN` 条件；触发动作只允许受限 SQL body，与发起 DML 同一提交边界，任一失败回滚原操作。Document/measurement 触发器在写放大、批量语义和恢复证据完成前不宣称支持。 | ✅ |
| #332 | 过程/触发器治理收口：依赖图与 DROP/ALTER 阻断、调用链审计、稳定错误码、执行耗时和失败指标、备份恢复、崩溃恢复、权限越权测试、递归/语句炸弹/大结果防护，以及嵌入式与远程 parity。 | ✅ |

顺序固定为 #329 先冻结目录与公开合同，#330 完成可调用 SQL 过程，再以同一运行时实现 #331 触发器，最后 #332 做治理与恢复收口。不得在 #329/#330 中顺带引入外部语言宿主。

## Milestone 39 — SQL 触发器第二版

第二版从 V1 的可执行证据出发，不以 PostgreSQL/MySQL 语法全集为目标。每项高级语义必须先说明工业 journey、事务边界、批量复杂度和恢复行为；外部语言宿主仍不在本里程碑范围内。

| PR | 交付 | 状态 |
|---|---|---|
| #333 | V2 gap baseline：固定审计 outbox、派生汇总、状态流转保护三条关系表 golden journey；建立 1/100/10,000 行 DML 下无触发器、V1 row trigger 与候选 statement trigger 的吞吐、WAL、内存和回滚成本矩阵；加入触发动作中途失败、提交失败、进程终止、重启 replay 的 crash-injection 证据，并据此确认后续条目的优先级。已接入以批量 INSERT 为代表的 `tests/SonnetDB.Benchmarks --m39-trigger-evidence`、`m39-trigger-evidence.yml` 和 CrashTests；UPDATE/DELETE 同规模成本及固定目标硬件复测仍需归档，不作为本地语义准入结论。 | 🚧 |
| #334 | 生命周期与确定性顺序：设计 `ALTER TRIGGER ... ENABLE/DISABLE`、原子替换或重命名，以及显式 `FOLLOWS` / `PRECEDES` 顺序合同；目录更新必须保持落盘后发布、依赖安全和备份恢复兼容，禁用状态不能改变历史创建顺序。 | 📋 |
| #335 | 语句级触发器与 transition tables：在 #333 证明逐行写放大是主要瓶颈后，实现 `FOR EACH STATEMENT` 及只读 `OLD TABLE` / `NEW TABLE`；固定空影响集、批量 UPDATE/DELETE、同语句多行、触发器链和失败回滚语义，避免把 transition set 无界复制到内存。 | 📋 |
| #336 | 受控 BEFORE 语义：仅面向关系表 `BEFORE INSERT/UPDATE`，先冻结校验/改写顺序、生成列/ROWVERSION/主外键/CHECK 交互和只读 OLD 规则；若允许修改 NEW，必须使用受限赋值合同，不允许任意递归 DML 或绕过约束。`INSTEAD OF` 与可写视图另行评估。 | 📋 |
| #337 | 诊断与治理：提供按触发器过滤的执行/失败/回滚原因与延迟分布、最近调用链查询、定义级 `EXPLAIN`/dry-run，以及不记录参数值和行内容的可持久审计导出；指标标签必须有界，提交失败与已回滚动作不能被报告为已提交成功。 | 📋 |
| #338 | 高级事务语义准入：评估 deferred trigger、constraint trigger 和显式 order group 是否解决 #333 的真实场景；给出死锁、取消、保存点、调用深度和提交阶段错误合同。`AFTER COMMIT` 异步动作优先建模为 durable outbox worker，不伪装成与原 DML 原子的普通触发器。 | 📋 |
| #339 | Document / measurement 准入：分别量化批量摄取写放大、乱序/重放、幂等键、保留策略、compaction、备份恢复和高基数影响；只有模型原生事件合同和 crash/replay 对拍通过后才实现。不得把关系行 `OLD`/`NEW` 生硬套到 document patch 或 measurement batch。 | 📋 |

执行顺序为 #333 -> #334/#337；#335、#336 和 #338 由 baseline 证据决定是否进入实现，#339 始终独立过模型语义与容量门禁。V2 不默认包含多事件合并语法、异步网络调用、外部脚本、跨数据库触发器或分布式 exactly-once。

## Milestone 40 — 原生属性图数据库

目标不是把 `MATCH` 重写为关系 JOIN，而是在现有 KV/WAL/checkpoint/backup 地基上增加一级 Vertex、Edge、Label、Property 和双向 adjacency 存储，同时提供原生 Graph API 与可组合 SQL/PGQ。关系表映射图和原生图共享 Graph Logical Plan 与流式执行算子，但使用不同底层 accessor，`EXPLAIN` 必须如实显示 native adjacency、relation index seek 或 scan fallback。

详细的复用矩阵、key layout、事务不变量、SQL 双入口、逐 PR 验收和容量门禁见 [docs/native-graph-database-roadmap.md](docs/native-graph-database-roadmap.md)。

| 阶段 | PR 范围 | 交付边界 | 状态 |
|---|---|---|---|
| Phase 0：公共地基 | #341~#346 | ADR/golden journey、共享 sortable codec、KV snapshot cursor、Graph Catalog、单 graph 原子事务、backup/invariant/crash 骨架；无对外 Graph 能力宣称。 | 📋 |
| Phase 1：Native Graph Preview | #347~#352 | 原生 GraphStore、双向邻接、属性索引、流式 Expand/BFS/DFS/shortest path、Server/SDK/import 和首轮 Neo4j/容量证据。 | 📋 |
| Phase 2：SQL/PGQ Graph Beta | #353~#359 | 共享 Graph Logical Plan、原生 graph SQL DDL/DML、SQL/PGQ 关系映射、`GRAPH_TABLE MATCH`、planner/EXPLAIN 和跨模型 SQL 组合。 | 📋 |
| Phase 3：生产级单机图数据库 | #360~#367 | statement snapshot、supernode/维护、按证据准入的高级路径/算法、可选 GQL 风格入口、知识图谱组合、运维产品面和发布门禁。 | 📋 |

固定边界：一个 graph 一个 keyspace，第一阶段不支持跨 graph/跨模型原子事务；vertex 删除先用 `RESTRICT`，不以静默拆批伪装超大 `DETACH DELETE` 原子性；Graphify/实体抽取/LLM/GraphRAG job 留在 importer、Server 或 SDK；不引入第二套 WAL、SQL 表达式系统、向量/全文索引、权限和备份格式；不承诺 Bolt、完整 Cypher/GQL、RDF 推理或分布式图能力。

阶段名称也是产品宣称门禁：Phase 1 只能称 Native Graph Preview，Phase 2 只能称 SonnetDB Graph Beta。只有 #367 的 LDBC/Graphalytics 子集、7 天 mixed workload、crash/backup/Native AOT 和固定硬件报告通过后，才可称“生产可用的单机原生属性图数据库”，并统一评估把产品定位从八模型更新为九模型。

## Milestone 41 — 关系查询规划与执行性能加固

目标是在不减少 SQL 能力、不改变事务/持久化/恢复语义的前提下，消除关系查询中的扫描、物化、复制、长锁和 GC 放大，并从当前规则式访问路径选择演进到可解释、可回退的轻量成本优化器。本里程碑不追求 PostgreSQL/MySQL 方言或优化器全集，也不通过降低正确性、关闭审计、放松 fsync、限制既有查询能力或提高无界并发换取基准数字。

### 生产触发证据与目标

2026-08-05 木垒 ARM64 生产只读采样已经满足排期条件：主机 48 核、250 GiB 内存且约 190 GiB 可用，采样期 CPU idle 72%~89%、I/O wait 为 0；SonnetDB RSS 约 27~28 GiB，在约 72.33 SQL QPS、322.60 返回行/秒下产生约 282.39 MiB/s 逻辑读取而物理读取为 0。`GovernanceAudits` 幂等键/`EXISTS`、普通 `IN`、nullable `OR`、多表 JOIN 和倒序分页出现 12~61 秒延迟，简单点查和 `COMMIT` 也受排队、锁等待或 GC 连带影响。该采样是生产问题基线，不代替可重复基准和 profile。

阶段目标按以下顺序收敛：

1. P0 先让访问路径、examined/returned rows、队列/锁等待和分配可见，并消除已确认的 `EXISTS/IN/OR/倒序 Top-N` 扫描放大。
2. P1 将谓词、投影和 LIMIT 推入各关系输入，以流式 cursor、延迟物化和短锁快照替代完整行列表物化。
3. P2 建立统计信息、基数/行宽估算、轻量成本模型和可解释的逻辑/物理计划选择。
4. P3 在工作量已经缩减且内存有界后，再增加 JOIN 算法、spill、受控并行和运行时反馈。
5. 每一阶段都以差分正确性、故障恢复、固定硬件基准和木垒查询语料回归为准入门禁，不等到全部实现后才验证。

### 交付拆分

| 优先级 | PR | 交付 | 状态 |
|---|---|---|---|
| P0 | #368 | 性能合同与可观测性基线：固化木垒慢查询语料和合成数据集；按规范化 query fingerprint 记录访问路径、候选/检查/返回行数、SQL permit 队列等待、表/KV 锁等待、执行时间、分配量、GC、逻辑/物理读写及 fallback reason。指标标签必须有界且不得记录参数值或行内容；慢查询环满载不得丢失聚合计数。 | 📋 |
| P0 | #369 | `EXISTS`/`Any`、semijoin 与标量 `IN` 快速路径：唯一键/主键条件使用直接探测，普通主键或索引 `IN` 使用去重后的批量 MultiGet；保留残余谓词、NULL 三值逻辑、事务 overlay、稳定输出和参数绑定，无法证明等价时回退现有执行器。 | 📋 |
| P0 | #370 | `OR` 与多索引候选集合：为可索引分支实现主键集合 union，并按证据准入 intersection；支持 nullable 时间条件等常见形式，统一去重、残余过滤、排序/分页边界和内存阈值，超阈值或不可索引分支使用可解释 fallback。 | 📋 |
| P0 | #371 | 双向索引 cursor 与早停 Top-N：支持满足排序合同的升/降序索引遍历，将 `LIMIT/OFFSET` 安全下推到候选读取；多列方向、NULL 顺序、非覆盖谓词或事务 overlay 无法保证顺序时继续走现有排序路径。 | 📋 |
| P1 | #372 | 关系输入谓词与投影下推：在 JOIN 前按绑定列归属拆分并下推单表 WHERE、所需列和安全 LIMIT；顶层残余谓词始终保留，外连接、相关子查询、聚合和视图展开必须有独立等价性测试。 | 📋 |
| P1 | #373 | 流式关系算子与延迟物化：定义公共 row/candidate cursor，使 scan/filter/project/Top-N/JOIN 可逐批消费；增加 covering/index-only scan，仅在输出或残余谓词需要时读取并解码基表全行；所有阻塞算子必须声明内存行为。 | 📋 |
| P1 | #374 | KV/Table 快照读取与锁范围收缩：在短锁内取得不可变可见视图或版本化 cursor，在锁外枚举、复制和解码；保持同一 statement snapshot、事务内 read-your-writes、删除/更新 overlay、checkpoint/compaction/WAL replay 和异常释放语义。与 M40 #342~#346 共享 cursor/codec 地基，不重复实现。 | 📋 |
| P2 | #375 | 轻量统计信息：持久化表/索引行数与页数、平均行宽、NULL fraction、distinct、MCV 和等深直方图；支持显式 `ANALYZE` 与有预算的自动刷新，采样不得长时间阻塞业务，不保存原始敏感值，并记录 freshness/sample rate。 | 📋 |
| P2 | #376 | 逻辑/物理计划与成本选择：统一 point/range/full/index-union access path，基于基数、选择率、行宽、解码、排序、内存和逻辑 I/O 估算选择计划；首版保持小而确定，不引入无界搜索，统计缺失或估算不可信时使用稳定启发式回退。 | 📋 |
| P2 | #377 | 可解释计划与实际执行证据：默认 `EXPLAIN` 只读目录/统计元数据，不为估算候选数实际扫描业务数据；为 M36 #313 提供计划树、估算/实际行数、耗时、loops、rows removed、锁/队列等待、峰值内存、spill 和 fallback reason。M36 负责用户侧错误/取消/超时合同，本项只建设共享规划与算子证据源。 | 📋 |
| P3 | #378 | JOIN 优化：按估算行数和行宽选择 Hash build side，支持 semijoin/antijoin、index nested-loop，并在有序输入和收益证据成立时准入 merge join；建立有限 join-order 枚举与大连接图回退，外连接和 NULL 语义不得被重写破坏。 | 📋 |
| P3 | #379 | 阻塞算子内存预算与 spill：为 hash join、sort、Top-N、group、distinct 和索引候选集合设置按查询/全局预算、取消和临时文件生命周期；落盘结果必须与内存路径对拍，崩溃后可清理，禁止因预算不足静默截断结果。 | 📋 |
| P3 | #380 | 受控并行与运行时反馈：仅对估算收益成立的 scan/JOIN/aggregate 启用有界并行，服从 SQL permit、查询内存和取消；记录估算偏差供统计刷新或下一次规划使用，不在执行中改变可观察结果顺序。必须在 #369~#379 已减少扫描并使内存有界后准入。 | 📋 |
| 发布门禁 | #381 | 生产收口：运行语义差分、并发/事务、crash/replay、backup/restore、Native AOT、固定 ARM64/x64 硬件基准和 7 天 mixed workload；木垒语料分别报告 P50/P95/P99、examined/returned amplification、RSS/分配/GC、锁/队列等待和逻辑/物理 I/O。任一优化回归正确性或恢复保证时不得默认开启。 | 📋 |

### 参考边界、顺序与验收

参考 PostgreSQL 的统计信息、扩展统计、Bitmap Scan、有限 join-order 搜索、计划树和 `EXPLAIN ANALYZE`，参考 MySQL 的持久统计/直方图、range optimizer、Index Merge、semijoin/antijoin、Hash Join 内存界限与 spill；学习其机制和验证方法，不复制 wire protocol、完整 SQL 方言、系统目录或分布式能力。计划缓存只有在参数敏感选择和数据倾斜证据完成后另行准入，不能把单一计划盲目复用于所有参数。

固定执行顺序为 `#368 -> #369/#370/#371 -> #372/#374 -> #373 -> #375/#376/#377 -> #378/#379 -> #380 -> #381`。P0 完成后立即在木垒同语料只读复测；P1 完成后必须证明长扫描不再在表级锁内完成全行解码；P2 完成后必须报告 estimated/actual rows 偏差；P3 不以线程数或单条最佳数字验收，而以混合负载尾延迟、吞吐和内存上界验收。

所有快速路径必须满足以下不变量：索引 union/MultiGet 按主键去重；残余谓词不得丢失；NULL/三值逻辑、排序稳定性、LIMIT/OFFSET、相关子查询和事务可见性不变；WAL/checkpoint/compaction/backup/recovery 合同不变；公开 API 与 EXPLAIN schema 采用 extend-only 演进；Core 保持零第三方运行时依赖、Safe-only 和 Native AOT。每个新计划先与当前执行器做随机化及木垒固定语料差分测试，再按 feature gate/canary 放量；无法证明等价、统计过期或资源预算不足时必须回退到已验证路径并暴露原因。

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
| 37~38 | 持久化逻辑/物化视图，以及 SQL 存储过程、关系表 AFTER ROW 触发器与治理收口。 |
| MM9 | 多模型备份、检查、校验和恢复 CLI 第一批。 |

详细历史只用于追溯，不覆盖本文件的当前完成判定；若历史文档与当前实现冲突，以代码、可执行测试和本文件的审计结论为准。
