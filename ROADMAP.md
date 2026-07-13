# ROADMAP

本文件描述 SonnetDB 的分批 PR 开发计划，按 Milestone 组织。每个 PR 均包含：变更点、新增文件、测试覆盖与验收标准。

> **维护方式（本文件按状态分区）**：**前半 = 当前 / 未来（进行中 / 计划中）里程碑的详细正文**，按推进优先级排列，方便直接找到「要开始做的」；**后半 = 已完成里程碑的摘要 + 归档指针**，详细 PR 拆分与缺陷附录移至 [docs/roadmap-history.md](docs/roadmap-history.md)，避免当前规划被历史实现细节淹没。

图例：✅ 已完成 / 🚧 进行中 / 📋 计划中 / ⏸️ 暂停 / ➡️ 移交

---

## 里程碑总览

> 导航索引。**进行中 / 计划中**里程碑的详细正文在本文件前半（下方按推进优先级排列）；**已完成**里程碑仅保留摘要，详细正文归档在 [docs/roadmap-history.md](docs/roadmap-history.md)。

| Milestone | 主题 | PR 范围 | 状态 |
|-----------|------|---------|------|
| 0~16 | 早期路线（脚手架 → Copilot 产品化） | #1 ~ #88 | ✅（摘要见下「已完成里程碑」，详情见归档） |
| 17 | 可观测性与运行时可见性（OTel + 结构化日志 + 诊断端点） | #89 ~ #98 | ✅ |
| 18 | VS Code 数据库扩展（SonnetDB for VS Code） | #99 ~ #108 | 🚧（#99~#106 ✅；#107 C# parser diagnostics 第一批 ✅、signature/repair 待续；#108 Marketplace/实机验收待续） |
| 19 | 生态适配底座能力（关系 + KV/缓存 + 对象桶 + 大量 measurement） | #109 ~ #126.1 | ✅ |
| 20 | 多模能力对齐与平移测试（Parity） | #127 ~ #136 | ✅ |
| 21 | Document Store 单机能力升级（MongoDB-like） | #137 ~ #146 | ✅ |
| 23 | 搜索与向量引擎合并（DotSearch / DotVector 收编） | #160 ~ #169 | ✅ |
| 24 | SonnetDB Studio 管理体验升级（Document 管理面） | #170 ~ #172 | ✅（核心工作台由 M29 #257 落地；高级查询、更新预览、索引设计与 change feed 由 M32 #279/#281 界面交付收口） |
| 25 | Document Store 验收、文档与发布治理 | #173 ~ #174 | ✅ |
| 26 | 连接器路线独立化（C ABI + 多模型 API） | #175 ~ #181 | ✅ |
| 27 | Industrial Data Agent 与 AI-ready 产品化路线 | #182 ~ #188 | 🚧（#182 文档已落；M28 收官后 #184 Demo 可启动；#183/#185 纯文档） |
| 28 | 可靠性、并发正确性与热路径加固（P0~P5 分阶段） | #189 ~ #244、#261 ~ #262 | ✅（全部收官，详情见归档） |
| 29 | 多模型统一管理工作台（Multi-Model Management Workbench） | #245 ~ #260 | ✅（能力矩阵、三面 parity、截图与 smoke 已收口） |
| 30 | 多协议设备接入扩展（Sparkplug B / CoAP / Line Protocol UDP） | #263 ~ #268 | ✅（协议入口、生命周期/安全、接入矩阵、跨协议 parity 与基准已收口） |
| 31 | 时序聚合类型语义增强（selector / categorical aggregates） | #269 ~ #271 | ✅ |
| 32 | Document Store MongoDB-like 易用性增强 | #272 ~ #281 | 📋（后续池；承接 M21/M24/M25） |
| 33 | 时序聚合执行与下推优化（aggregate execution & pushdown） | #282 ~ #287 | ✅（Geo 正确性、多聚合复用、残差流式化、count(*) 专路、LIMIT 与 latest-N 下推全部收口） |
| 34 | Modbus TCP 内建映射表（主站采集 + 从站暴露） | #288 ~ #296 | 📋（新增路线；以 SQL DDL 定义寄存器与表字段映射） |
| 35 | 语义内容与多模态检索（Semantic Content & Multimodal Retrieval） | #297 ~ #309 | 📋（后续路线；先补过滤 ANN 与内容索引生命周期，再交付图文检索、通用 RAG 和受治理的专业视觉识别） |
| MM9 | 多模型统一备份、恢复和管理工具第一批 | BackupService + sndb backup | ✅ |

---

## 当前推进重点

> **旗舰（要开始做的）**：
> - **Milestone 29 — 多模型统一管理工作台**：#245~#260 已完成。管理工具已收敛为「一张能力矩阵 × 三个交付面（Web Admin 旗舰 / Studio 桌面 / VS Code）」；能力矩阵、截图、三面 parity 和 smoke 见 [管理工具与三面能力矩阵](docs/management-tools.md)。后续功能缺口回到各模型里程碑，Studio 原生菜单映射与 VS Code 完整打包仍分别归桌面后续项和 M18 #108。
> - **Milestone 30 — 多协议设备接入扩展**：已在 M28 的 MQTT 双形态之上完成 Sparkplug B、CoAP、Line Protocol UDP 三条被动接收通道，并以协议矩阵、跨协议落库 parity 与 Sparkplug/CoAP 基准收口。
>
> **进行中（按带宽穿插）**：
> - **M17 可观测性**：#89~#98 已完成，已覆盖结构化日志、Diagnostic Dump、Copilot 指标与细粒度 Agent span、观测文档、可选本地观测栈和完整 trace 端到端验证。
> - **M27 Industrial Data Agent**：M28 收官后 #184 端到端工业异常 Demo 阻塞解除、可启动；#183/#185 纯文档随时可做。
> - **M18 VS Code**：#99~#106 首个可用闭环与 #107 C# Parser diagnostics sidecar 第一批已完成；下一步补 signature help / repair suggestion，再进入 #108 Marketplace 正式发布与 Electron 实机验收。**M19 生态底座** #109~#126.1 已全部收官，正则治理与 generation/批量 tombstone 删除闭环见 [M19 #126/#126.1 契约](docs/m19-regex-bulk-delete.md)。
>
> **后续池**：
> - **M32 Document Store MongoDB-like 易用性增强**：在 M21 单机能力、M24 管理面、M25 parity/长稳之后，集中补齐 MongoDB 日常开发体验缺口。第一目标是让 SonnetDB Document Store 对应用开发者“像 MongoDB 一样顺手”，不是立即承诺 MongoDB wire protocol / BSON command / 官方 Driver 直连。
> - **M34 Modbus TCP 内建映射表**：把“建表时定义寄存器 ↔ 表字段 ↔ 类型转换”的能力作为独立路线推进，明确区分 **主站/client**（SonnetDB 主动连接外部从站/server 采集和写寄存器）与 **从站/server**（SonnetDB 暴露 Modbus TCP 端口，外部主站读写映射字段）。该路线不再藏在 M30 的被动接入边界内。
> - **M35 语义内容与多模态检索**：复用对象桶、Document、全文、向量与 Hybrid Search，补齐原始内容到 embedding 的异步索引链路。第一批先做 metadata-filtered ANN、Embedding Profile、内容清单与任务生命周期，再交付图片搜图片、文字搜图片和通用 RAG；其后扩展音视频分段、人脸相似检索、Person ReID、步态 / 姿态动作、车辆与车牌检索。专业视觉能力使用独立模型 Profile 和敏感数据治理，推荐系统和 Agent Memory 保持上层适配器 / 示例边界。
>
> **已收官**：M28（可靠性 / 并发正确性 / 热路径加固，P0~P5 + SDK 补口全部完成）、M20 Parity、M21 Document Store、M23 搜索/向量合并、M24 Document 管理面、M25 Document 发布治理、M26 连接器。详细正文见 [docs/roadmap-history.md](docs/roadmap-history.md)。

---

## 管理界面完成度（2026-07-11，更新至 2026-07-12）

> 状态图例：✅ 已完成 / 🟡 部分完成 / ❌ 未完成 / ➖ 当前交付面明确不承担完整能力。
> 本表按实际代码、`docs/management-tools.md`、Web Chromium smoke 和 VS Code consumer smoke 汇总；静态设计原型不自动计为产品实现。

### Web Admin

| 界面 / 能力 | 状态 | 当前结论 / 后续归口 |
|---|---|---|
| 统一多模型 Explorer | ✅ | 已覆盖时序、关系、Document、KV、MQ、向量、全文和对象桶。 |
| SQL 工作台与共享结果面板 | ✅ | SQL 编辑执行、Table / Raw / JSON / Chart / Map、历史和 CSV/JSON 导出已接线。 |
| 共享写审批 | ✅ | staged preview、危险确认、dry-run/confirm 和操作历史已被各写工作台复用。 |
| 关系表工作台 | ✅ | 数据网格、行编辑、EXPLAIN、表设计、索引、ER、CSV/JSON/JSONL 与 DDL 已完成。 |
| Document 工作台基础闭环 | ✅ | find/count/distinct/aggregate、CRUD、Validator、JSON/JSONL 和 rebuild 已完成。 |
| KV 工作台 | ✅ | 前缀扫描、TTL、类型化值、set/remove/expire/persist 和批量操作已完成。 |
| SonnetMQ 工作台 | ✅ | 消息浏览、publish/ack、consumer lag、吞吐、backlog、retention 和 DLQ 提示已完成。 |
| 向量检索工作台 | ✅ | Raw/Text Embed、Top-K、metadata filter 和 HNSW 参数查看已完成。 |
| 全文检索工作台 | ✅ | BM25、All/Any/Phrase/Fuzzy、Analyzer、高亮、分页和 staged rebuild 已完成。 |
| 对象桶工作台 | ✅ | 上传下载、预览、版本、Multipart、policy/lifecycle/retention/quota/hold 和审计已完成。 |
| 轨迹地图 | ✅ | Trajectory 与 SQL GEOPOINT Map 视图已完成。 |
| 基础监控 | ✅ | 写入、查询 P95、WAL、MemTable、Segment 等 Prometheus 指标页面已完成（M17 #91）。 |
| Copilot 基础交互 | ✅ | 流式聊天、模型选择、只读/读写模式、引用和 SQL 工作台联动已完成。 |
| Copilot 调用量 / token 摘要 | ✅ | `/v1/copilot/metrics` 从 `__copilot__.usage_events` 聚合当前 owner 最近一小时调用、input/output/total token、工具调用、成功失败与模型摘要；CopilotDock / AiSettingsView 已接入。 |
| 详细 Health / Readiness 状态条 | ✅ | M17 #94；`/healthz/live` 与 `/healthz/ready` 已拆分，顶部状态条独立显示存储、WAL、Chat provider、Embedding provider 四项检查。 |
| 慢查询 / Top-N 查询抽屉 | ✅ | M17 #95；SQL 工作台已接入服务端慢查询环形缓冲、SQL 指纹聚合与权限过滤诊断抽屉。 |
| Copilot 服务端会话与跨设备同步 | ✅ | M17 #97；`__copilot__` 系统表持久化会话、消息和引用，按 owner 隔离并跨设备同步，不再回退浏览器 `localStorage`。 |
| Provider-neutral 模型分组 | ✅ | M27 #185；模型目录兼容 `default/candidates` 并新增平台默认、自定义、本地三组，CopilotDock 与设置页已接入。 |
| 时序数据点专用编辑器 | ✅ | Measurement 工作台已提供时间窗/TAG 过滤、点级表单与网格、新增/校正/删除、CSV/JSON 导出、共享写审批和操作历史；校正必须改变 time/TAG 身份，避免 tombstone 屏蔽同身份重写。 |
| Measurement 文件导入 | ✅ | CSV、JSON、JSONL 已支持自动列映射、time/TAG/FIELD 类型校验、预览、共享审批、100 点分批提交、进度与停止后续批次。 |
| 单表 / 单 Measurement 实时监控 | ✅ | Measurement 工作台已提供目标类型/对象/频率/窗口选择、暂停/继续、趋势图、最近结果和查询耗时，复用当前 token 与数据面 SQL 权限。 |
| Document Change Feed Viewer | ✅ | M32 #279/#281 界面交付；collection 级持久化 feed、7 天保留、过滤、过期检测、resume token 与 Web 实时查看器已完成。 |
| Document 高级查询 / 更新 / 索引设计 | ✅ | 可视化 filter、服务端 update preview、共享写审批，以及 compound/unique/sparse/partial/TTL 创建、删除、重建与一致性校验已完成；尚未实现的 multikey/wildcard 引擎语义继续归 #276，不在 UI 中虚假承诺。 |
| KV 文件 round-trip | ✅ | 已完成 `sonnetdb-kv-v1` JSONL 导出与回导，保留 Base64 原值和逐 key expiry；导入按 expiry 分组并进入共享写审批。 |
| MQ 消息文件导入 | ✅ | 已完成 JSON/JSONL/NDJSON 导入，支持逐条 topic、headers、Base64 或 JSON payload，并按文件顺序进入共享写审批。 |
| 向量数据编辑 / 导入 | ✅ | 已接入对应 Measurement schema，支持点级新增、校正、删除以及 CSV/JSON/JSONL 导入；VECTOR 校验有限数值和 schema 维度，批处理保留停止/续传。 |
| 全文数据独立导入 | ✅ | 全文工作台已提供当前索引集合的 JSON/JSONL/NDJSON 独立导入，继续通过 Document API 维护主数据与派生索引，并统一进入 staged preview。 |
| 小于 800px 的完整治理 | ➖ | **本轮暂缓。** 小屏继续定位为巡检和只读浏览，复杂编辑/批量治理引导到桌面；当前只保证核心导航和现有工作台不横向溢出。 |
| Modbus 管理界面 | ❌ | **本轮未实现，后期继续。** 归 M34 #296；等待 source、endpoint、寄存器映射、轮询健康和最近错误运行时合同落地后再实现。 |

### Studio 桌面

| 界面 / 能力 | 状态 | 当前结论 / 后续归口 |
|---|---|---|
| 复用 Web Admin 八模型工作台 | ✅ | 模型级能力与 Web Admin 一致。 |
| 原生文本/二进制打开保存 | ✅ | 关系导入导出、对象上传下载和结果保存均已接线。 |
| 原生目录选择 | ✅ | data root、备份和恢复目录已接线，并保留浏览器降级。 |
| 磁盘连接库 | ✅ | profile 持久化到磁盘，Bearer token 不进入连接库。 |
| 托管本地 Server | ✅ | data root、Start/Stop、健康轮询和退出策略已完成。 |
| Win32 原生菜单 | ✅ | File / View / Local Server 菜单已映射到真实宿主窗口，并通过 JS bridge 复用共享工作台动作。 |
| 真实桌面宿主全流程自动化 | 🟡 | Web/Bridge smoke 已覆盖菜单动作、文件和 Server 控制；仍需补安装包与宿主生命周期实机验收。 |

### VS Code 扩展

| 界面 / 能力 | 状态 | 当前结论 / 后续归口 |
|---|---|---|
| 远程连接、SecretStorage 与状态栏 | ✅ | profile、token、探活、首次安装探测和活动连接已完成。 |
| Database / Schema Explorer | ✅ | Measurement、Table、Collection、KV、MQ、向量和全文节点已接线。 |
| SQL 执行与结果三视图 | ✅ | 当前语句/选区、Table / Raw / Chart、历史和 CSV/JSON 导出已完成。 |
| Copilot 面板 | ✅ | 流式聊天、模型/知识库状态、引用、当前 SQL 和读写确认已完成。 |
| 托管本地 Server | ✅ | data root、端口检测、进程启停、日志和健康检查已完成。 |
| Measurement 草稿与 Bulk Import | ✅ | Create Measurement、LP/JSON/Bulk VALUES 导入和 snippets 已完成。 |
| KV / MQ / 向量 / 全文只读预览 | ✅ | 复用 M29 管理契约和 Query Result Webview。 |
| C# `SqlParser` language sidecar | 🟡 | M18 #107；C# parser diagnostics、VSIX 打包与 TypeScript fallback 已完成，标准 LSP framing 待续。 |
| Signature Help / Repair Suggestion | ❌ | M18 #107；随 LSP sidecar 一并实现。 |
| Document 专用查询面板 | ✅ | Collection 节点可打开只读 Document find 面板，支持 filter/projection/sort、游标分页、错误反馈和 JSON/JSONL 导出。 |
| 对象桶浏览 | ❌ | 当前 Explorer 和结果面板均未接入对象桶。 |
| KV / MQ / 向量 / 全文完整编辑治理 | ➖ | VS Code 定位为开发者只读子集，完整治理继续由 Web Admin / Studio 承担。 |
| 实例 / MQ 监控交付面 | ❌ | client 有部分 monitor 契约，当前无可见监控页面。 |
| VS Code Extension Host UI 自动化 | ❌ | M18 #108；现有 smoke 只验证 HTTP consumer，不等同于真实 VS Code UI e2e。 |
| Electron 实机截图与 Marketplace 发布 | ❌ | M18 #108；VSIX/CI/metadata 已有，正式发布与实机验收未完成。 |

### 界面收口顺序

1. **P0 运维可见性**：M17 #89~#98 已完成，包括仅管理员可采集的 Diagnostic Dump、可选本地观测栈以及 HTTP → Copilot → 工具 → Core 查询 → Segment 读取的端到端追踪验证。
2. **P1 VS Code 产品化**：M18 #107 → #108，完成 LSP sidecar、真实 Extension Host UI e2e、截图和 Marketplace 发布。
3. **P1 Studio 收口**：补安装包与桌面宿主生命周期实机自动化验收。
4. **P2 模型体验补口**：Document update/index/change feed、KV 文件 round-trip、MQ 消息文件导入、向量编辑/导入和全文独立导入均已完成；后续按 M32 继续 Document 引擎与迁移工具，小于 800px 完整治理暂缓。
5. **P3 新协议管理面**：M34 Runtime 与合同落地后再做 #296 Modbus 管理界面，禁止 UI 先承诺未实现的运行时语义。

---

## Milestone 29 — 多模型统一管理工作台（Multi-Model Management Workbench）

> **背景**：SonnetDB 已是覆盖 8 种数据模型的多模型数据库（时序 / 关系 SQL / 文档 / KV / 全文 / 向量 / 对象存储 / 消息队列 SonnetMQ），但管理工具只覆盖了「时序 + SQL」一条线。这里的“多模型”指数据库数据模型，不等同于图片、文本、音频、视频语义互通的“多模态”；后者归 M35 语义内容与多模态检索路线。当前唯一成型的 UI 是 `web/`（Vue3 + Naive UI + ECharts + CodeMirror）Web Admin：有 Dashboard、SQL Console（即 Studio 工作台）、schema 树、结果表/图、Trajectory 地图、Events 监控（SSE）、Users/Grants/Tokens、Copilot；`src/SonnetDB.Studio` 只是把 `web/dist` 打包进 WebView2 的桌面壳，**无任何独立能力**；VS Code 扩展（M18）大部分仍是脚手架（只有 Explorer 树 + SQL 执行客户端能跑）。对照 pgAdmin / SSMS / Navicat / DBeaver（关系）、RedisInsight（KV）、Kafka UI / RabbitMQ Management / EMQX Dashboard（MQ）、Milvus Attu / Qdrant / Weaviate Console（向量）、Kibana / OpenSearch Dashboards（全文）、MinIO Console（对象）、MongoDB Compass（文档），SonnetDB 缺一整批 per-model 管理工作台。
>
> **核心策略**：把「管理工具」从三个孤立工程重构为「一张能力矩阵 × 三个交付面」——(1) **Web Admin 旗舰**，逐模型做到对标单品级别（**本里程碑推进优先级最高的交付面**）；(2) **Studio 桌面** = 打包的 Web Admin + 桌面原生桥（原生文件对话框、磁盘连接库、本地 data-root 托管 server）；(3) **VS Code** = 开发者子集，复用同一批 HTTP 契约。世界级多模型管理工具 = 统一 Explorer + 外壳 + 每模型一个专用工作台，各自向该模型最好的单品看齐；三面共享同一套 server contract、权限模型与写审批框架，不各写各的。
>
> **边界**（与 M24 / M28 一致）：本里程碑只做**管理面 + 最小只读 metadata / browse 契约**。UI 消费 M19 / M21 / M23 / M28 已交付的引擎能力与 HTTP API；发现后端缺必要只读 metadata 时可补最小 server contract，但**不新增任何查询语义、索引语义、存储格式或写入语义**——所有写操作复用既有 data-plane API（SQL / Document / KV / Object / MQ 端点）。**文档模型管理面仍归 M24（#170~#172）**，**对象存储后端治理仍归 M19 #118**；本里程碑只把它们接入统一外壳并补齐对象浏览体验，不重复造引擎能力。`SonnetDB.Core` 零第三方依赖不变；契约新增走 Server 层。

### 能力矩阵（现状 → 目标工作台 → 对标单品）

| 模型 | 现有管理 UI | 目标工作台 | 对标单品 | 归属 PR |
|---|---|---|---|---|
| 时序 measurement | ✅ schema 树 + SQL Console + Trajectory 地图/图 | 保持并接入统一外壳 | InfluxDB UI / Grafana | #246（并入外壳） |
| 关系 SQL 表 | ⚠️ 仅能写 SQL，无数据网格 / 行内编辑 | 数据网格 + 行内编辑 + 可视化 EXPLAIN + 表设计器 + ER + 导入导出 | pgAdmin / SSMS / Navicat / DBeaver | #248~#250 |
| 文档集合 | ✅ Web Admin 文档工作台（#257） | Document Explorer（M24 专属管理语义继续承接，M29 已接入统一外壳） | MongoDB Compass | M24 #170~#172 + #257 |
| KV keyspace | ❌ 无 | keyspace 前缀树 + TTL 查看/编辑 + 类型化值查看 + 批量 / 前缀删 + 过期统计 | RedisInsight / AnotherRedisDesktopManager | #245 契约 + #251 |
| 全文索引 | ⚠️ 索引可见可 rebuild，无检索 UI | BM25 检索 + 高亮 + 分词器（Jieba/CJK）预览 + 模糊 / 短语构建器 | Kibana / OpenSearch Dashboards | #245 契约 + #255 |
| 向量索引 | ❌ 无（仅 schema 类型可见） | ANN 检索 playground（文本→embed / 原始向量→Top-K + score + 过滤）+ 索引统计 + HNSW 参数 | Milvus Attu / Qdrant / Weaviate Console | #245 契约 + #254 |
| 对象桶 | ✅ Web Admin 对象桶工作台（#256） | 桶浏览 + 对象上传 / 下载 / 预览 + 前缀导航 + 版本 / 生命周期 / 保留 + presigned URL + 审计 | MinIO Console / S3 Browser | M19 #118 后端 + #256 |
| 消息队列 SonnetMQ | ❌ 无 | topic / 消息浏览（按 offset / 时间 seek + header）+ 发布测试 + 消费 / 订阅 lag + ack + 吞吐 + DLQ / retention | Kafka UI / RabbitMQ Management / EMQX Dashboard | #245 契约 + #252~#253 |

### 管理界面归口（跨里程碑，单一出处）

> SonnetDB 的管理界面此前散落在多个里程碑（M18 VS Code、M19 #118 对象、M24 文档、M29 多模型），东一块西一块。下表把所有管理 UI 一次列清、给出**唯一归口**；后端治理能力（非 UI）仍归其原里程碑，本表只统一 UI 交付出处。

| 管理界面 | 交付面 | 归属 PR | 状态 |
|---|---|---|---|
| 统一 Explorer + 连接库 + 结果面板 + 写审批框架 | Web Admin | M29 #245~#247 | #245/#246/#247 ✅ |
| 关系数据网格 / 可视化 EXPLAIN / 表设计器 / ER / 导入导出 | Web Admin | M29 #248~#250 | #248/#249/#250 ✅ |
| KV / MQ / 向量 / 全文 专用工作台 | Web Admin | M29 #251~#255 | #251/#252/#253/#254/#255 ✅ |
| 对象桶浏览器 | Web Admin | M29 #256（收编 M19 #118 的 Buckets / Objects / Multipart / Audit 页面） | ✅ |
| 文档 Explorer / Validator / 导入导出 | Web Admin / Studio | M24 #170~#172（M29 #257 接入统一外壳） | Web Admin #257 ✅ |
| Studio 桌面原生桥（文件对话框 / 连接库 / 本地托管 server） | Studio | M29 #258 | ✅ |
| VS Code 结果三视图 + Copilot 面板 | VS Code | M18 #103/#104（M29 #259 补完接线） | ✅ |
| VS Code 多模型只读浏览（KV / 向量 / 全文 / MQ） | VS Code | M29 #259 | ✅ |
| 对象后端治理（policy / lifecycle / version / audit / quota，**非 UI**） | Server 后端 | M19 #118（UI 归 M29 #256） | 🚧 |

### 阶段总览

| 阶段 | 主题 | PR 范围 | 目标 |
|------|------|---------|------|
| **A** | 管理契约与统一外壳 | #245 ~ #247 | 补齐 KV / 向量 / 全文 / MQ / 对象只读 metadata + browse 契约；Web Admin 左侧改统一多模型 Explorer；连接库 + 统一结果面板 / 写审批框架 |
| **B** | 关系工作台（对标 pgAdmin/SSMS/Navicat） | #248 ~ #250 | 数据网格 + 行内编辑 + 可视化 EXPLAIN + 表设计器 + ER + 导入导出 |
| **C** | KV / MQ / 向量 / 全文 工作台 | #251 ~ #255 | 四个缺失模型的专用管理工作台，各自对标其最佳单品 |
| **D** | 对象桶与文档收口 | #256 ~ #257 | 对象桶浏览器（收编 M19 #118 UI）；文档浏览器（M24）接入统一外壳与共享框架 |
| **E** | Studio 桌面原生桥 + VS Code 消费 + 收口 | #258 ~ #260 | 桌面原生能力；VS Code 复用同契约做多模型只读浏览；能力矩阵文档 + 三面 parity |

### A — 管理契约与统一外壳

| PR | 标题与范围 | 状态 |
|----|------------|------|
| #245 | **管理契约补齐（只读 metadata + browse endpoints）**：为当前无管理端点或端点过薄的模型补最小只读契约——KV keyspace `scan`（前缀/分隔符 + TTL + 类型 + 游标分页）与 keyspace 统计；向量索引 `stat`（度量/维度/图参数 ef/M/efConstruction）与 `search-preview`；全文索引 `stat`（doc/term 数、分词器）与 `search-preview`（BM25 + 高亮 + 分词器 analyze）；MQ topic `list` / `offsets` / `browse`（按 offset/时间 seek，含 header）/ `lag`；对象 bucket / object `list` 与 metadata。全部**读优先**、游标分页、走既有 Bearer + 三角色鉴权；写操作复用既有 data-plane API 不新增。`SonnetDB.Core` 不动，Server 层落地。 | ✅ |
| #246 | **统一多模型 Explorer + 连接库**：把 Web Admin 左侧导航从「时序/表/文档/索引/备份」扩展为覆盖 8 模型的统一树（Connection → Database → {Measurements / Tables / Collections / KV Keyspaces / Vector Indexes / FullText Indexes / MQ Topics / Buckets}）；每类节点的右键菜单路由到对应工作台；新增可持久化的连接库（Remote / Managed-local，token 走既有安全存储），活动连接与数据库选择全局一致，复用 SQL Console / CopilotDock 的 db 选择与权限状态。 | ✅ |
| #247 | **统一结果面板 + 写审批 / 历史 / 导出框架**：抽出跨模型共享的结果面板（Table / Raw / JSON / Chart 四视图，复用 `SqlResultPanel` / `SqlResultChart`）与**写审批框架**（staged preview → danger confirm → dry-run，比照 SQL Console 既有危险确认与 M24 写审批），供 B~D 各工作台统一挂载；统一 query/操作历史与 CSV/JSON 导出钩子；所有写、导入、rebuild、删除动作至少有 preview / dry-run / confirm 之一。 | ✅ |

> **#247 落地说明**：Web Admin 新增共享 `WorkbenchResultPanel`、`WriteApprovalPanel`、`WorkbenchHistoryDrawer`、`workbenchHistory` store 与 CSV/JSON 导出工具；`SqlResultPanel` 扩展为 Table / Raw / JSON / Chart 四视图并继续保留 GEOPOINT 地图视图。SQL Console 改为消费共享框架，查询与维护操作统一写入历史，CSV/JSON 导出集中在 `resultExport`。同时把原大文件按功能拆分为 Header、Explorer Sidebar、连接/建库弹窗、SQL Query Workspace、Explorer/SQL 工具模块和 `useSql*` composables，避免后续 B~D 工作台继续向单个 view 堆代码。

> **#245 落地说明**：Server 层新增 `ManagementContractEndpoints`，已交付 KV `keyspaces`/`scan`（base64 游标分页）、向量 `indexes`/`search-preview`（复用既有 `knn(...)` data-plane）、全文 `indexes`/`search-preview`/`analyze`、MQ `topics`/`offsets`（含 lag）/`browse`（按 offset 只读）。**对象** bucket/object list 与 metadata **已由既有 S3 端点覆盖**，本 PR 不重复实现。相对"`SonnetDB.Core` 不动"的初始约束，仅新增一个只读枚举方法 `SonnetMqStore.ListTopicStats()`（MQ topic 私有集合无其他公开枚举入口，纯读、不改任何队列语义）。**本 PR 范围外、留待后续里程碑**（Core 无公开 API）：全文 term 数与 BM25 高亮、MQ 按时间 seek、向量索引 live count 与 per-index 有效度量（当前引擎构建固定 cosine，已如实回显）。写/删/rebuild 一律不在本 PR，留给 #247 写审批框架 + 既有 data-plane。

### B — 关系工作台（对标 pgAdmin / SSMS / Navicat / DBeaver）

| PR | 标题与范围 | 状态 |
|----|------------|------|
| #248 | **关系数据网格 + 行内编辑**：表数据网格，游标分页、列排序 / 过滤、单元格类型化渲染；行内 INSERT / UPDATE / DELETE 经生成的**参数化 SQL**（复用 M28 #213）提交，编辑批次走 #247 staged preview + 事务确认（复用 M19 #110/#113 事务）；主键/唯一约束冲突走既有错误码回显。只调既有 SQL 端点，不新增查询语义。 | ✅ |
| #249 | **可视化 EXPLAIN + 表设计器 + 索引管理**：把既有 SQL `EXPLAIN` 计划渲染为可视化计划树（scan / filter / join / topN / 下推标注，复用 M28 #214~#217/#220 的 EXPLAIN 输出）；表设计器以可视化编辑生成 `CREATE TABLE` / `ALTER TABLE ADD/DROP/RENAME COLUMN` / `RENAME TABLE` DDL（复用 M19 #111 能力与其明确拒绝项），DDL 保存前 preview + confirm；索引查看 / 创建 / rebuild。 | ✅ |
| #250 | **关系导入导出 + ER 图**：CSV / JSON 导入导出（列映射、dry-run、批量错误报告、进度、取消）；基于 `INFORMATION_SCHEMA`（M19 #111）绘制 ER 图（表 / 列 / 主外键关系）；DDL 脚本导出。导入写入走 #247 写审批。 | ✅ |

> **#248 落地说明**：Web Admin 新增关系表工作台，表节点双击或右键 Open workbench 进入数据网格；数据浏览走既有 `/v1/db/{db}/sql`，支持 LIMIT/OFFSET 分页、ORDER BY 排序、按列/全列过滤与类型化渲染。INSERT / UPDATE / DELETE 先在网格中形成草稿，再生成参数化 SQL，统一进入 #247 `WriteApprovalPanel` staged preview；确认时通过 `/v1/db/{db}/sql/batch` 包裹 `BEGIN` / `COMMIT` 执行，结果进入 `WorkbenchResultPanel` 与历史记录。REST SQL 端点同步补齐 `SqlRequest.Parameters` 绑定，保持与 M28 #213 参数化能力一致；未新增查询语义、表结构能力或存储格式。

### C — KV / MQ / 向量 / 全文 工作台

| PR | 标题与范围 | 状态 |
|----|------------|------|
| #251 | **KV 浏览器（对标 RedisInsight）**：消费 #245 `scan` 契约做按前缀 / 分隔符的 keyspace 树扫描（游标分页，避免全量拉取）；TTL 显示与编辑（复用 M19 #116 TTL）；按类型的值查看 / 编辑；批量 get/set/remove、前缀删除、命名空间切换、过期统计。写与前缀删走 #247 写审批。 | ✅ |
| #252 | **SonnetMQ 控制台一（topic + 消息浏览 + 发布）**：topic 列表 + offset / 分区 / retention 概览；消息浏览器支持按 offset / 时间 seek、查看 header 与 payload（消费 #245 `browse`）；发布测试消息（复用既有 MQ 发布端点）；依赖 **M28 P5a（#231~#234）** 提供的 per-topic 统计与冷数据可读性。 | ✅ |
| #253 | **SonnetMQ 控制台二（消费 / 订阅监控 + 吞吐 + DLQ）**：消费者 / 订阅 lag 与 ack 监控、消费进度可视化；吞吐 / 积压曲线（复用 M17 metrics + Events SSE）；DLQ 查看与 retention 策略展示。依赖 #245 `lag` 契约与 M28 P5a MQ 统计，随 P5b #236 推送订阅落地可展示实时推送状态。 | ✅ |
| #254 | **向量检索 playground（对标 Milvus Attu / Qdrant）**：向量索引 / 集合统计（维度、行数、度量 L2/IP/cosine、HNSW ef/M/efConstruction，复用 M28 #223/#226 参数暴露）；ANN 检索 playground——文本经 Copilot embed 或直接粘原始 `float[]`，返回 Top-K + score + 元数据过滤（消费 #245 `search-preview` + 既有向量检索端点）；度量方式与图参数只读展示，不改索引语义。 | ✅ |
| #255 | **全文检索 playground（对标 Kibana / OpenSearch Dashboards）**：全文索引列表 + 统计（doc/term 数、分词器）；BM25 检索 UI 带高亮、评分与分页；分词器 / analyzer 预览（Jieba/CJK，展示切词结果）；模糊 / 短语 / 布尔查询构建器（消费 #245 `search-preview` + 既有全文检索端点）；索引 rebuild 走 #247 写审批。 | ✅ |

> **#254 落地说明**：Server 管理契约补齐向量索引 row count、schema 声明 metric、HNSW `efConstruction` 回显，并扩展 `search-preview` 支持只读 metric override、受限 metadata filter（TAG 等值与 time 比较的 `AND` 组合）和命中 tags/fields 明细；新增 `POST /v1/db/{db}/vector/embed-preview`，复用既有 Copilot embedding provider 生成查询向量，不写入数据。Web Admin 新增 `VectorSearchWorkbench` 并接入统一 Explorer / Header / 历史 / 结果面板：Vector Indexes 节点可直接进入 playground，支持 raw `float[]`、文本 embedding、Top-K、过滤条件、hit inspector 与索引参数只读展示；不新增索引语义、存储格式或写路径。

> **#255 落地说明**：Server 管理契约扩展全文索引统计，`POST /v1/db/{db}/fulltext/indexes` 回显 term count；`search-preview` 新增 `queryKind=all|any|phrase`，支持 AND / OR / 短语查询构造，并保留 exact / fuzzy 模式边界（fuzzy phrase 明确拒绝）。Web Admin 新增 `FullTextSearchWorkbench` 并接入统一 Explorer / Header / 历史 / 结果面板：FullText Indexes 节点可直接进入 playground，支持索引列表、doc/term/tokenizer 统计、BM25 Top-K 检索、命中文档加载、客户端高亮、评分分页、analyzer token 预览、All/Any/Phrase/Fuzzy 构建器；索引 rebuild 通过 #247 `WriteApprovalPanel` staged preview 后调用既有 `rebuild_index document_fulltext` 维护入口，不新增索引语义、存储格式或写路径。

### D — 对象桶与文档收口

| PR | 标题与范围 | 状态 |
|----|------------|------|
| #256 | **对象桶浏览器（对标 MinIO Console，收编 M19 #118 UI）**：桶列表 / 创建 / 删除；对象浏览（前缀导航、上传 / 下载 / 预览、range read）；multipart 会话查看；版本 / 生命周期 / 保留 / legal hold 展示与编辑；presigned URL 生成；访问审计与容量 / quota 统计。**后端能力复用 M19 #118**（bucket policy / lifecycle / versioning / audit / quota）；本 PR 把 #118 规划的 Buckets / Objects / Multipart / Audit 页面**收编进统一外壳的对象工作台**，#118 只保留后端治理能力交付。 | ✅ |
| #257 | **文档浏览器接入统一外壳**：把 **M24（#170~#172）** 的 Document Explorer / Validator Governance / 导入导出接入 #246 统一 Explorer 与 #247 共享结果 / 写审批框架，确保文档模型与其余模型的连接选择、权限状态、结果面板、写审批一致；**不新增文档引擎能力**（引擎与专属管理语义仍归 M24 / M21）。 | ✅ |

> **#257 落地说明**：Web Admin 新增 `DocumentCollectionWorkbench`，Collections 节点可进入文档专用工作台；支持 collection 列表、创建 / 删除、find（ID / filter / projection / sort / cursor 分页）、count / distinct / aggregate、文档详情、JSONL 导入导出、单文档 insert / replace / delete、批量删除、validator 查看 / 编辑 / 删除 / 样本预检，以及 JSON path / FullText 索引 rebuild。所有写、删、导入、validator 保存与 rebuild 均复用既有 Document / maintenance API 并统一进入 #247 `WriteApprovalPanel`、共享结果面板与工作台历史。Server 仅在 schema 响应中补充 document collection validator 只读 metadata，不新增查询语义、写入语义、索引语义或存储格式。

### E — Studio 桌面原生桥 + VS Code 消费 + 收口

| PR | 标题与范围 | 状态 |
|----|------------|------|
| #258 | **Studio 桌面原生桥**：`SonnetDB.Studio`（`NativeWebHost` WebView2 壳）从纯 WebView 升级为带原生桥——原生文件打开 / 保存对话框（供导入导出、对象上传下载、备份恢复）、磁盘持久化连接库、本地 `data root` 托管 SonnetDB Server 启动 / 停止 / 健康检查（对齐 M18 #105 托管本地模式思路）、原生菜单。Web Admin 检测到运行在 Studio 壳内时启用原生能力，浏览器内优雅降级。 | ✅ |
| #259 | **VS Code 多模型消费（复用 M29 契约）**：VS Code 扩展先补完 **M18 #103（结果 Table/Raw/Chart 三视图）+ #104（Copilot 面板，客户端 `streamCopilot` 已写好只差接线）**，再把 Explorer 与结果面板扩展为消费 #245 契约做 **KV / 向量 / 全文 / MQ 只读浏览**；写操作与完整工作台仍以 Web Admin 为主，VS Code 定位开发者只读 + SQL 执行子集。与 **M18 交叉引用**：M18 保留 VS Code 交付主线，多模型浏览契约由本 PR 落地。 | ✅ |
| #260 | **管理工作台收口 + 文档 + 三面 parity**：汇总能力矩阵文档（模型 → 工作台 → 对标单品 → 交付面覆盖度）；`docs/` 增管理工具章节与截图；Web Admin / Studio 桌面 / VS Code 三面能力 parity 表（谁支持哪些模型的浏览 / 查询 / 编辑 / 导入导出 / 监控）；各工作台 e2e smoke。 | ✅ |

> **#259 落地说明**：VS Code 扩展保留 Remote-first 与开发者子集定位。当前实现已将连接 profile 持久化到 VS Code `globalState`、token 存入 `SecretStorage`；Explorer 在数据库下合并 schema 与 #245 管理契约，展示 KV Keyspaces、Vector Indexes、FullText Indexes、MQ Topics；结果面板支持 Table / Raw / Chart 三视图，并复用于 KV/MQ/向量/全文只读预览；Copilot 面板接入流式 `/v1/copilot/chat/stream`，read-write 模式前置用户确认。完整编辑、导入导出和治理工作台仍以 Web Admin / Studio 为主。

> **#260 落地说明**：新增 `docs/management-tools.md` 作为八模型管理能力与三面 parity 的唯一收口入口，纳入 Web/MQ 与 Studio bridge 截图；Studio 后续已将共享 action manifest 映射为 Win32 原生菜单，VS Code 继续不承担完整编辑治理。Web Admin Playwright smoke 覆盖 SQL/时序、关系、文档、KV、MQ、向量、全文、对象桶八个工作台和 Studio bridge；VS Code 以 loopback server 验证同一批 schema、SQL NDJSON、KV、向量、全文、MQ HTTP 契约消费。未新增引擎语义或绕过权限的写路径。

### 推进顺序

```text
Web Admin 旗舰优先（用户决策 2026-07-04）：
A 外壳：#245（管理契约补齐）→ #246（统一多模型 Explorer + 连接库）→ #247（统一结果 + 写审批框架）
B 关系：#248（数据网格 + 行内编辑）→ #249（可视化 EXPLAIN + 表设计器）→ #250（导入导出 + ER）
C 四模型：#251（KV 浏览器）→ #252（MQ 控制台一）→ #253（MQ 控制台二）→ #254（向量 playground）→ #255（全文 playground）
D 收口：#256（对象桶浏览器，收编 M19 #118 UI ✅）→ #257（文档浏览器接入外壳 ✅）
E 三面：#258（Studio 桌面原生桥 ✅）∥ #259（VS Code 多模型消费 ✅）→ #260（收口 + 文档 + parity ✅）
```

> **阶段间依赖与并行度**：**A（#245~#247）是所有 per-model 工作台的地基，必须最先**——#245 契约是 #251~#257 的前置，#246/#247 外壳与框架是 B~D 所有工作台的挂载点。B / C / D 各工作台在 A 落地后**相互独立可并行 / 穿插**（各消费自己的 #245 契约 + 挂 #247 框架）。跨里程碑依赖：**#252/#253 MQ 控制台依赖 M28 P5a（#231~#234）** 的 per-topic 统计与冷数据可读性、`#253` 实时推送状态随 P5b `#236` 落地增强；**#254 向量 playground** 依赖 M28 #223/#226 的 HNSW 参数与度量暴露；**#256 对象桶** 依赖 M19 #118 后端治理能力；**#257 文档** 按 M24 边界复用既有 Document API 与最小只读 metadata，不新增文档引擎语义。E 的 #258 桌面桥可在任一模型工作台落地后并行；#259 VS Code 需先补完 M18 #103/#104；#260 收口最后。

### 验收标准

- **A**：KV / 向量 / 全文 / MQ / 对象都有可用的只读 metadata + browse 契约（游标分页、走既有鉴权）；Web Admin 左侧统一 Explorer 能展开全部 8 模型对象并路由到对应工作台；连接库可持久化多连接、token 不落明文；统一结果面板与写审批框架被至少一个工作台复用。
- **B**：关系表可在数据网格中浏览、排序、过滤、分页，并完成行内 INSERT/UPDATE/DELETE（参数化 SQL + 事务确认）；可视化 EXPLAIN 展示计划树；表设计器生成的 DDL 与 M19 #111 能力一致且保存前 preview；CSV/JSON 导入导出与 ER 图可用。
- **C**：KV 浏览器能按前缀树扫描、看 / 改 TTL、批量与前缀删除；MQ 控制台能列 topic、按 offset/时间浏览消息含 header、发布测试消息、观测消费 / 订阅 lag 与吞吐；向量 playground 能对索引做 ANN 检索返回 Top-K + score 并展示 HNSW 参数；全文 playground 能做 BM25 检索带高亮并预览分词结果。
- **D**：对象桶浏览器能浏览桶 / 对象、上传下载预览、看版本 / 生命周期 / 审计、生成 presigned URL；文档浏览器（M24）与其余模型共享同一连接选择、权限状态、结果面板与写审批框架。
- **E**：Studio 桌面壳提供原生文件对话框、磁盘连接库与本地托管 server；VS Code 补完 #103/#104 并能只读浏览 KV / 向量 / 全文 / MQ；能力矩阵文档与三面 parity 表齐备。
- **全局**：所有写 / 导入 / rebuild / 删除动作至少有 preview / dry-run / confirm 之一；本里程碑未新增任何引擎查询 / 写入 / 索引 / 存储语义，所有写走既有 data-plane API。

### 不做的事

- **不**新增任何模型的引擎查询 / 写入 / 索引 / 存储语义——本里程碑是管理面 + 只读 metadata / browse 契约，写复用既有 data-plane API（与 M24 边界一致）。
- **不**把文档引擎能力塞回本里程碑——文档管理面（Explorer / Validator / 导入导出）仍归 **M24 #170~#172**，本里程碑（#257）只做接入。
- **不**把对象存储后端治理（bucket policy / lifecycle / versioning / audit / quota）塞进本里程碑——仍归 **M19 #118**，本里程碑（#256）只收编其 UI 页面进统一对象工作台。
- **不**在 `SonnetDB.Core` 引入第三方依赖；管理契约与 UI 均走 Server / 前端层。
- **不**替换任何现有 Web Admin 页面或 REST 端点——统一 Explorer / 工作台是**扩展与整合**，SQL Console / Dashboard / Events / Users / Copilot 保留。
- **不**在 VS Code 做完整 per-model 编辑工作台——VS Code 定位开发者只读浏览 + SQL 执行子集，完整编辑体验以 Web Admin 旗舰为准。
- **不**做多节点 / 集群管理面（监控与备份编排的分布式形态由 SonnetDBEE 承接，本里程碑限单机 / 单连接管理）。

---

## Milestone 30 — 多协议设备接入扩展（Multi-Protocol Device Ingestion）

> **背景**：M28 P5b（#242/#243）已让 SonnetDB 具备 MQTT 双形态设备接入——内建 broker 供设备直连、client 订阅外部 EMQX/Mosquitto，两者共享 `db/{db}/m/{measurement}` 路由并复用 `BulkIngestEndpointHandler` 三格式（Line Protocol / JSON points / BulkValues）零重复落库。但工业与受约束设备现场并非只说裸 MQTT：**(1) 工业 SCADA 事实标准是 Sparkplug B**——骑在 MQTT 之上的 Protobuf payload 规范，带统一的 metric 语义、设备发现（birth/death）、别名压缩与死活检测，Ignition / Inductive Automation / Eclipse Tahu / HiveMQ / AWS IoT SiteWise 生态通用；裸 MQTT 只有 topic 约定、无 payload 语义。**(2) 受约束设备（MCU / 低功耗 / 无 TCP 栈）走 CoAP**——RFC 7252，UDP、REST-like、4 字节头，是 OMA LwM2M 的承载层。**(3) 遥测低开销入口**——InfluxDB Line Protocol 除 HTTP `/write` 外还有经典的 UDP 监听形态（fire-and-forget，Telegraf `influxdb` output 可直连）。
>
> **核心判断**：M30 只处理**「设备 push / 从消息总线被动接收」**这一类协议——即 SonnetDB 对 MQTT 已做的模式。本里程碑把 Sparkplug B、CoAP、Line Protocol UDP 三条「被动接收 / 直写」通道补齐，全部**收敛到既有 `BulkIngestEndpointHandler` 三格式落库路径**，不新增任何引擎写入 / 查询 / 索引 / 存储语义。**Modbus TCP 已从 M30 的“不做现场总线轮询”边界中拆出，转入独立的 M34：它不是普通被动写入入口，而是以 SQL DDL 声明寄存器映射、采集表、主站/client 与从站/server 双角色的工业数据映射能力。**其它主动轮询协议（OPC UA client / 西门子 S7 / 三菱 / FINS / AB / MTConnect）仍归边缘采集网关或后续独立评估，不混入 M30。
>
> **不变约束**（与 M28 P5b / #242 一致）：`SonnetDB.Core` 零第三方依赖不变——**所有协议栈限于 Server 层**。**依赖策略（本轮定档，倾向纯托管、避免 native 与重型第三方）**：**(1) Sparkplug 手写 protobuf 解码，零新依赖**——复用本仓已有的手写 protobuf wire-format 解码范式（`PrometheusRemoteWriteReader`：`ReadVarint` / `SkipField` / LEN 切片），Sparkplug `Payload` 与 Prometheus `WriteRequest` 同量级同手法，**不引 `Google.Protobuf`**（其 codegen 走 `Grpc.Tools` 会在 build 时拉 native `protoc`）；proto 字段号手写成常量，proto 文件都不带。**(2) CoAP 明文栈 vendor 自维护**——使用纯托管 CoAP.NET server 子集，现代化到 net10，**不引 CoAPnet**。**(3) 唯一允许的新第三方 = DTLS 用 `BouncyCastle.Cryptography` 2.6.2**——.NET BCL 无 DTLS，纯托管 DTLS 现实上只有 BouncyCastle；它是单个纯托管程序集、零 native（与 build 时拉 native 的 Google.Protobuf 性质不同），仅 #266 的 `coaps` 传输层用，且默认关闭。三条协议都是**并列新增**，现有 REST / MQTT / 帧协议全部保留；单机形态，不做 broker 集群 / 桥接 / 跨节点 session。

### 行业对标依据（2026-07 走查工业与 IoT 接入协议）

> - **Sparkplug B = 工业 MQTT 事实标准**：Ignition / Inductive Automation SCADA、Eclipse Tahu 参考实现、HiveMQ / EMQX 原生支持、AWS IoT SiteWise 摄取均以其为准。它**不是新传输层，而是 MQTT 之上的 Protobuf payload + topic namespace（`spBv1.0/{group}/{msgtype}/{node}/[{device}]`）规范**——解决裸 MQTT「无统一 payload 语义 / 无设备发现 / 无死活检测 / 无带宽压缩」四大缺口。TDengine / IoTDB 内建 MQTT broker 但**不原生解 Sparkplug** → 这是 SonnetDB「工业采集平台」定位的差异化高杠杆项，且**纯 payload codec，复用 #242 broker 接入与落库路径，成本最低**。
> - **CoAP = 受约束设备承载协议**：RFC 7252，UDP:5683 / DTLS:5684，REST-like（GET/POST/PUT/DELETE + Observe），4 字节头，为 MCU / 低功耗 / 有损网络设计，是 OMA LwM2M 的承载层。IoTSharp 平台侧已内建 CoAP；DB 侧补 CoAP 直写，让**无 MQTT 栈的约束设备也能直连落库**，映射规则对齐 #242 的 MQTT topic → 资源路径。
> - **Line Protocol UDP = 低开销遥测入口**：InfluxDB 除 HTTP `/write` 外提供 UDP 行协议监听（无 ack、无背压、fire-and-forget），Telegraf `influxdb` output 可直连。本仓 **HTTP `/write` / `/api/v2/write` / Prometheus remote-write 已由 M8 交付**（`InfluxLineProtocolEndpointHandler`，Telegraf / EMQX 生态可直接对接），本里程碑只补**唯一缺失的 UDP 数据报入口**，复用既有 `LineProtocolReader`，零新依赖（`System.Net.Sockets`）。

### 能力矩阵（现状 → 目标接入 → 对标）

| 协议 | 现状 | 目标接入 | 对标 | 归属 PR |
|---|---|---|---|---|
| MQTT（裸）| ✅ 内建 broker + client 订阅外部 broker | 保持 | IoTDB / TDengine 内建 broker、InfluxDB+Telegraf | M28 #242 / #243 ✅ |
| **Sparkplug B** | ✅ #263/#264 已完成 | 骑 #242 broker 解码 Protobuf payload + 别名解析 + birth/death 生命周期状态机 → BulkIngest 落库 | Ignition / Eclipse Tahu / HiveMQ / AWS IoT SiteWise | #263 ✅ / #264 ✅ |
| **CoAP** | ✅ #265/#266 已完成 | UDP CoAP route `db/{db}/m/{measurement}` → BulkIngest 三格式；DTLS + Observe | OMA LwM2M | #265 ✅ / #266 ✅ |
| Line Protocol（HTTP）| ✅ `/write`、`/api/v2/write`、Prometheus remote-write | 保持 | InfluxDB / Telegraf | M8 ✅ |
| **Line Protocol（UDP）** | ✅ #267 已完成 | UDP 数据报监听复用 `LineProtocolReader` → BulkIngest | InfluxDB UDP listener / Telegraf | #267 ✅ |
| Modbus TCP（主站/client + 从站/server）| 📋 新增路线 | 以 SQL DDL 定义寄存器、表字段、类型转换、采集 / 暴露方向与写入策略 | PLC / RTU / SCADA 现场集成 | M34 #288~#296 |
| 其它现场总线主动接入（OPC UA client / S7 / 三菱 / FINS / AB / MTConnect）| ❌（M30 不做）| 归边缘采集网关或后续独立里程碑评估 | — | 不做（见「不做的事」） |

### 阶段总览

| 阶段 | 主题 | PR 范围 | 目标 |
|------|------|---------|------|
| **A** | Sparkplug B（工业 SCADA 事实标准，骑 #242 broker） | #263 ~ #264 | ✅ 解码/alias 落库、birth/death、seq 缺口、Rebirth、Primary Host STATE 与审批命令闭环 |
| **B** | CoAP 设备写入（受约束设备 UDP 直连） | #265 ~ #266 | ✅ 服务端写入落库、DTLS PSK 与 Observe 订阅已完成 |
| **C** | Line Protocol UDP 监听 + 收口 | #267 ~ #268 | ✅ UDP 遥测入口、协议接入矩阵、落库 parity 与基准均已完成 |

### A — Sparkplug B（工业 SCADA 事实标准）

> 骑在 **M28 #242 内建 MQTT broker** 之上：SonnetDB broker 收到 `spBv1.0/...` 的 PUBLISH，由 Sparkplug 解码器解 Protobuf、解析别名、映射为 measurement 点后落库。**不新增 broker，不新增落库路径**——Sparkplug 是 #242 之上的一层 payload 编解码 + host application 状态机。**Protobuf 解码手写、零新依赖**：新增 `SparkplugPayloadReader : IPointReader`，抄本仓已有的 `PrometheusRemoteWriteReader` 手写 wire-format 骨架（`ReadVarint` / `SkipField` / LEN 切片全可复用，仅需补 float=field 12 / wire type 5 的 `ReadSingleLittleEndian` 一处），`Payload` / `Metric` 字段号手写成常量，**不引 `Google.Protobuf`**（其 codegen 会在 build 时拉 native `protoc`）。**落库直产 `Point` → `BulkIngestor.Ingest`**（与 `PrometheusRemoteWriteReader` 同一引擎入口），**不走 `IngestPayload` 的 LP/JSON/BulkValues 字节路径**——Sparkplug metric 已是强类型值，回序列化成 LP 文本再解析既浪费又丢类型；data-plane parity 仍成立（#268 parity 测试照旧）。**状态机自写**（broker 侧 host application 角色是 SonnetDB 特有，SparkplugNet 等库偏 client 侧）。

| PR | 标题与范围 | 状态 |
|----|------------|------|
| #263 | **Sparkplug B payload 解码 + 数据落库**：在 #242 broker 上挂 Sparkplug topic namespace（`spBv1.0/{group_id}/{message_type}/{edge_node_id}/[{device_id}]`）路由（新增并列 `[MqttController]`，不碰现有 `db/{db}` 控制器）；`NBIRTH`/`DBIRTH` 建立 `name↔alias` 映射表（进程内内存态）并注册设备，`NDATA`/`DDATA` 按 alias 解析 metric（Sparkplug 带宽压缩：DATA 只携 alias 不重发 name）；**metric 映射为点约定**——`measurement = edge_node_id`（带 device 时 = `device_id`），`group_id` / `edge_node_id` / `device_id` 为 tag，metric name 为 field key，per-metric timestamp（缺失回退 payload-level）为点时间戳；**类型映射**：整数族→Int64、Float(field 12,wire5)/Double→Float64、Bool→Boolean、String/Text/UUID→String、DateTime→Int64(ms)，**Bytes/DataSet/Template/File 等非标量本 PR 跳过并计数**（`FieldValue` 仅 Float64/Int64/Boolean/String）；metric name 含 `/` 的按名称合法性规则保留或 `.` 替换。**手写 `SparkplugPayloadReader : IPointReader` 解码，直产 `Point` → `BulkIngestor.Ingest`**（与 `PrometheusRemoteWriteReader` 同一入口，零新依赖，**不走 `IngestPayload` 字节路径**）。BIRTH 缺失时的孤儿 DATA（无 alias 映射）本 PR 丢弃并计数、不触发 rebirth。**本 PR 只做解码 + 落库**，生命周期 / seq 缺口 / rebirth 归 #264。 | ✅ |
| #264 | **Sparkplug B 生命周期与命令**：`bdSeq`（birth-death 序列）+ `seq`（每消息 0–255 滚动）校验与**缺口检测** → 经 `NCMD` 的「Node Control/Rebirth」请求边缘节点重生，补齐丢失的 birth 上下文；`NDEATH`/`DDEATH`（LWT）标记节点 / 设备离线状态；`alias` 表按 edge node 持久化 / 重建（断连重连不丢映射）；`STATE`/primary host application 语义（broker 侧宣告在线以触发边缘节点数据流）；下行命令 `NCMD`/`DCMD` 写入（可选，走写审批）。 | ✅ |

### B — CoAP 设备写入（受约束设备直连）

> CoAP 明文栈 **vendor 自维护**：使用纯托管 CoAP.NET server 子集并现代化到 net10（server + option 解析 + blockwise + observe），**不引 CoAPnet**。route 映射对齐 #242 的数据入口命名：`db/{db}/m/{measurement}`，payload = measurement 内容，`Content-Format` option 选择 Line Protocol / JSON points / BulkValues 三格式，落库复用 `BulkIngestEndpointHandler`。**DTLS（#266）用 `BouncyCastle.Cryptography` 2.6.2**——.NET BCL 无 DTLS，纯托管 DTLS 现实上只有 BouncyCastle（单个纯托管程序集、零 native），版本对齐宿主已用的 2.6.2，仅 Server 层 `coaps` 传输层用。

| PR | 标题与范围 | 状态 |
|----|------------|------|
| #265 | **CoAP 服务端 + 写入落库**：UDP:5683 CoAP 服务端（RFC 7252），`POST`/`PUT` 到 route `db/{db}/m/{measurement}` → `BulkIngestEndpointHandler` 三格式落库（格式由 `Content-Format` option 选择，回退首字节嗅探，与 #242 一致）；对外使用 route / endpoint 命名，CoAP.NET `Resource` 只作为内部 adapter；鉴权复用 Bearer/token（经 CoAP option 携带，映射三角色权限）；支持确认型（CON）/ 非确认型（NON）消息与块传输（RFC 7959，大 payload 分块）；错误以 CoAP response code 回（4.00/4.01/4.03/4.04 对齐 REST 语义）。 | ✅ |
| #266 | **CoAP 安全 + Observe 订阅**：DTLS（`coaps`:5684）经 **`BouncyCastle.Cryptography` 2.6.2**（`DtlsServerProtocol` + `DtlsServerTransport`，.NET BCL 无 DTLS，纯托管零 native，仅 Server 层）——**PSK 优先**（受约束设备最常用，`TlsPskIdentityManager`），RPK / 证书作后续增量；握手后解密 datagram 喂回 #265 vendored CoAP 解析 → 落库路径不变，默认关闭需显式启用（同 #242 / #267 安全姿态）。`Observe`（RFC 7641）资源订阅——设备 GET+Observe 一个 `db/{db}/mq/{topic}` 资源，服务端在新消息到达时推送（桥接 SonnetMQ，复用 #236 推送管线，对齐 #242 的 `mq/` 订阅），用 vendored CoAP 的 observe 关系、与 DTLS 正交不依赖 BouncyCastle。安全与 Observe 均为 #265 之上的增量。 | ✅ |

### C — Line Protocol UDP 监听 + 收口

| PR | 标题与范围 | 状态 |
|----|------------|------|
| #267 | **Line Protocol UDP 监听端点**：`System.Net.Sockets.UdpClient`（纯 BCL，零新依赖）监听 UDP 端口，每个数据报 = 一批 Line Protocol 行，复用既有 `LineProtocolReader` + `BulkIngestor`（与 HTTP `/write` 同一解析与落库路径）；目标数据库按监听端口绑定或配置项指定（UDP 无 query 参数）；对标 InfluxDB UDP listener / Telegraf `influxdb` UDP output。**安全边界文档化**：UDP fire-and-forget——无鉴权、无 ack、无背压、受数据报尺寸限制，**仅限可信内网**，默认关闭、需显式启用（与 #242 broker 默认关闭一致）。 | ✅ |
| #268 | **多协议接入收口 + 文档 + parity**：`docs/` 增协议接入矩阵（MQTT / Sparkplug B / CoAP / Line Protocol-HTTP / Line Protocol-UDP → 落库路径映射 + 安全 / QoS / 可靠性边界表 + 选型指引）；各协议落库与既有 `BulkIngestEndpointHandler` 路径的 **parity 平移测试**（同一 payload 经不同协议入口落库结果一致，复用 `tests/SonnetDB.Parity` 骨架）；Sparkplug 解码 / CoAP 吞吐基准进报告。 | ✅ |

> **#268 落地说明**：新增 `docs/protocol-ingest.md` 作为五类入口的统一选型与运维边界，`ProtocolIngestParitySuite` 在同一真实 Server 上把同一 LP payload 经 HTTP、UDP、MQTT、CoAP 写入独立数据库并对拍 SQL 结果；Sparkplug protobuf 因字节格式不同，由专用 codec/lifecycle 测试保证映射正确。`SparkplugDecodeBenchmark` 与 vendored `CoapRouteMatcherBenchmark` 的 ShortRun 基线记录在 `docs/benchmarks/m30-protocol-ingest.md`。UDP listener 改为严格 UTF-8，超限、非法编码和坏 LP 数据报被隔离且不终止后续接收。

### 推进顺序

```text
前置：M28 #242 内建 MQTT broker ✅（Sparkplug 骑其上）
A Sparkplug：#263（payload 解码 + 落库 ✅）→ #264（生命周期 + seq 缺口 + rebirth 命令 ✅）
B CoAP：#265（服务端 + 写入落库 ✅）→ #266（DTLS 安全 + Observe 订阅 ✅）
C LP-UDP + 收口：#267（Line Protocol UDP 监听 ✅）→ #268（协议矩阵文档 + parity + 基准 ✅）
```

> **阶段间依赖与并行度**：**A / B / C 相互独立，可按带宽并行 / 穿插**——各自挂在既有落库路径上。段内有序：**#263 是 #264 的前置**（先能解码落库，再补生命周期）；**#265 是 #266 的前置**（先能写入，再加 DTLS/Observe）；#267 独立，#268 收口最后。跨里程碑依赖：**A 段依赖 M28 #242 内建 broker（已 ✅）**——Sparkplug 是其上的 payload 层；#266 CoAP Observe 复用 M28 #236 推送管线（已 ✅）。MQTT 与 CoAP 复用 `BulkIngestEndpointHandler.IngestPayload`，HTTP/UDP 复用 `LineProtocolReader`，Sparkplug 强类型 metric 直接产出 `Point`；五类入口最终统一进入 `BulkIngestor`，无新引擎语义。

### 验收标准

- **A（Sparkplug B）**：真实 Sparkplug 边缘节点（或 Eclipse Tahu 测试工具）连上 SonnetDB #242 broker，`NBIRTH`/`DBIRTH` 后 `NDATA`/`DDATA` 的按-alias metric 能正确解析并经 SQL 回查落库；`seq` 缺口触发 rebirth 请求；`NDEATH` 反映节点离线状态；metric→点映射约定文档化且可回查。
- **B（CoAP）**：CoAP 客户端 `POST` 到 `db/{db}/m/{measurement}` 三格式 payload 能落库并经 SQL 回查；readonly token 被拒；块传输大 payload 完整落库；DTLS(PSK) 加密连接可用；Observe 订阅 `mq/{topic}` 能收到服务端推送。
- **C（LP-UDP + 收口）**：UDP 数据报的 Line Protocol 行与 HTTP `/write` 落库结果逐点等价；UDP 监听默认关闭、启用后限可信网络的安全边界在 `docs/` 明确；协议接入矩阵 + 落库 parity 平移测试齐备。
- **全局**：三条协议均复用 `BulkIngestEndpointHandler` 落库、未新增任何引擎写入 / 查询 / 索引 / 存储语义；`SonnetDB.Core` 零第三方依赖不变，协议栈限 Server 层；REST / MQTT / 帧协议全部保留向后兼容。

### 不做的事

- **M30 不做**现场总线轮询类协议；其中 **Modbus TCP 已改为 M34 独立路线**，用 SQL DDL 明确主站/client 与从站/server 两种角色、寄存器映射、类型转换和写入策略，不混在本里程碑的被动写入入口里。OPC UA client / 西门子 S7 / 三菱 / FINS / AB / MTConnect 仍归边缘采集网关或后续独立评估。
- **不**做设备管理导向协议的完整栈——**LwM2M**（设备管理 / 固件下发导向，非数据洪流）只在 CoAP 承载层落数据入口、不实现其对象模型 / DM 语义；**MQTT-SN**（传感网 UDP，一般由网关转 MQTT）不做，交给网关。
- **不**做 **DDS**（机器人 / 国防实时总线，重且小众）、**AMQP 1.0**（企业消息，本轮未选，如需再评估作 consumer）、**Kafka consumer**（本轮评估后未选，librdkafka native 依赖较重，留后续按现场需求再定）。
- **不**新增引擎语义——所有协议落库复用既有 `BulkIngestEndpointHandler` 三格式与 data-plane，与 #242/#243 边界一致。
- **不**在 `SonnetDB.Core` 引入第三方依赖——Sparkplug 手写解码 / vendored CoAP / DTLS 的 BouncyCastle 均限 Server 层；**不引 `Google.Protobuf`**（Sparkplug 手写 wire-format 解码，复用 `PrometheusRemoteWriteReader` 范式，零新依赖）、**不引 CoAPnet**（vendor `IoTSharp.CoAP.NET` server 子集自维护）；唯一新第三方 = #266 DTLS 的 `BouncyCastle.Cryptography` 2.6.2（纯托管零 native，BCL 无 DTLS 的唯一现实选项）。
- **不**选 **CoAPnet（chkr1011）作 CoAP 基底**（已评估）——CoAPnet 与 IoTSharp.CoAP.NET 两个 fork **均多年不维护**，但既已决定 **vendor 自维护**（而非引 NuGet 依赖），「谁在维护」不再是评判项，只比「哪份源码作 vendor 基底更省事」：选 **`IoTSharp.CoAP.NET`**（SmeshLink→Eclipse Californium 血统）而非 CoAPnet，因为 **(1) 它已是本 org 的 fork**（license 署名含 maikebing，谱系 / 授权 / 控制权零障碍）；**(2) server 子集更全**（observe / blockwise / 资源树是 Californium 血统强项，CoAPnet 偏 client）；**(3) 本地 NuGet 缓存可一手核实**（CoAPnet 从未引入，纯纸面）。chkr1011「与 MQTTnet 同作者、同 policy」的优点仅在**引依赖**语境成立，vendor 后失效。
- **不**引 **`protobuf-net`（Marc Gravell）解 Sparkplug**（已评估）——它确是**纯 C# protobuf**（Apache-2.0，依赖全托管、零 native，靠 `[ProtoContract]` 运行时特性映射、连 codegen 都不需要，比 `Google.Protobuf` 的 `Grpc.Tools`+native `protoc` 干净），是"纯 C# protobuf"问题的合法答案；但仍不选，因 **(1) 它靠 `System.Reflection.Emit` 运行时 IL 生成 → AOT / trim 不友好**（与本仓 NativeAOT 目标冲突，见 MQTTnet.Routing vendor 注释）；**(2) Sparkplug `Payload` 仅 ~10 字段，手写解码约 200 行、与已有 `PrometheusRemoteWriteReader` 同量级**，引库解一个小 message 不划算且 AOT 零摩擦。若未来某协议 message 复杂到手写不划算，protobuf-net 是纯托管回退项（代价 = AOT 友好性）。
- **不**做 broker 集群 / 桥接 / 跨节点 session——单机形态，与 P5「不做分布式」边界一致。
- **不**新造已存在的能力——**InfluxDB Line Protocol over HTTP（`/write`、`/api/v2/write`、Prometheus remote-write）已由 M8 交付**，本里程碑只补 UDP 入口，不重复 HTTP 形态。

---

## Milestone 31 — 时序聚合类型语义增强（Selector / Categorical Aggregates）

> **背景**：当前 SonnetDB 的内置 `count / sum / min / max / avg / first / last` 通过 legacy `Aggregator` 快路径执行，`BucketState` 只保存 `double` 聚合状态，`FunctionRegistry` 对除 `count` 外的聚合统一拒绝 `String / Vector / GeoPoint`。这让 `first(str_val) GROUP BY time(60s)` 这类 IoTSharp 字符串遥测分桶查询失败，错误表现为“仅支持数值字段”。但从语义上看，部分聚合并不是数值统计，而是**选择器**或**离散值统计**，应支持字符串、布尔和其他适用类型。
>
> **目标**：按聚合函数语义声明可接受字段类型，而不是按“是否是聚合函数”一刀切限制为数值字段。第一波优先解决 IoTSharp 遥测查询中最常见的状态字符串、枚举字符串和布尔状态分桶场景，同时保持数值聚合快路径性能与历史行为。

### 类型语义矩阵

| 聚合 | 目标支持类型 | 说明 |
|---|---|---|
| `count(*)` / `count(field)` | 所有 FIELD 类型 | 统计行或字段存在性，不读取值的数学含义。 |
| `first(field)` / `last(field)` | 所有 FIELD 类型 | 选择器聚合，按时间戳返回桶内首值 / 末值；字符串、布尔、数值、Vector、GeoPoint 均有明确语义。 |
| `min(field)` / `max(field)` | 数值、布尔、字符串 | 字符串使用 `StringComparison.Ordinal` 固定字典序；布尔按 `false < true`；Vector / GeoPoint 暂不支持。 |
| `mode(field)` | 数值、布尔、字符串 | 众数按值相等性统计，适合设备状态、工况、告警等级等离散值。 |
| `distinct_count(field)` | 数值、布尔、字符串 | 去重计数适合状态码、枚举值和分类标签；Vector / GeoPoint 留后续评估。 |
| `sum` / `avg` / `stddev` / `variance` / `percentile` / `histogram` / `tdigest_agg` / `pid*` | 数值 | 保持数学统计语义，不接受字符串。 |
| `centroid` | Vector | 保持现有向量中心语义。 |
| `trajectory_*` | GeoPoint | 保持现有轨迹语义。 |

### 阶段总览

| PR | 标题与范围 | 状态 |
|----|------------|------|
| #269 | **聚合函数类型能力声明**：为 `IAggregateFunction` / 内置函数注册表补充字段类型能力声明，区分 numeric aggregate、selector aggregate、categorical aggregate、vector aggregate、geo aggregate；移除 `FunctionRegistry` 中对所有非 `count` legacy 聚合统一拒绝 `String` 的硬编码，改为函数级校验。文档同步说明每个聚合的可接受类型和比较规则。 | ✅ |
| #270 | **selector 聚合支持任意 FieldValue**：将 `first/last` 从纯 `double` legacy 桶状态中拆出，桶内保存原始 `FieldValue` 与时间戳，最终按字段类型 unbox 返回；`GROUP BY time(...)` 与非分桶聚合都覆盖。数值 `first/last` 继续保持现有结果；空桶行为不变。重点验收 `first(str_val)` / `last(str_val)` / `first(bool_val)`。 | ✅ |
| #271 | **categorical 聚合扩展与 IoTSharp 兼容验收**：扩展 `mode` / `distinct_count` 支持 `String` 与 `Boolean`；评估并实现 `min/max(String|Boolean)` 的稳定比较规则（字符串固定 Ordinal）；补齐 SQL reference、Copilot skills 与 IoTSharp compat 测试。显式加入 IoTSharp 查询：`SELECT first(str_val) FROM ST_DeviceVal WHERE deviceId='xx' AND time >= ... AND time <= ... GROUP BY time(60s)`。 | ✅ |

### 验收标准

- `SELECT first(str_val) ... GROUP BY time(60s)` 能返回每个时间桶内按时间排序的第一个字符串值，不再报“仅支持数值字段”。
- `first/last` 对 Float64、Int64、Boolean、String、Vector、GeoPoint 均能保留原始类型返回；数值字段历史结果保持兼容。
- `mode` / `distinct_count` 支持字符串和布尔字段，结果在分桶与非分桶聚合中一致。
- `min/max` 如支持字符串，必须使用 Ordinal 比较并在 SQL 文档中明确；不受系统区域性影响。
- 数值统计类聚合仍拒绝字符串，错误信息说明“该函数需要数值字段”，不再泛化为所有聚合都只支持数值。
- IoTSharp 兼容矩阵增加字符串遥测分桶聚合场景，覆盖 SonnetDB storage profile 下的状态字段查询。

### 不做的事

- **不**把 `sum/avg/stddev/percentile/histogram/pid` 等数学聚合扩展到字符串。
- **不**把 Vector / GeoPoint 的 `mode/distinct_count/min/max` 纳入第一波；除 `first/last/count` 外，复杂类型仍按专用函数处理。
- **不**改变现有数值聚合热路径的性能目标；selector / categorical 路径按需拆分，不牺牲 `avg/sum/min/max` 的数值快路径。
- **不**引入区域性字符串排序；所有字符串比较必须稳定、跨平台一致。

---

## Milestone 32 — Document Store MongoDB-like 易用性增强

> **背景**：M21 已把 Document Store 从 JSON 文档集合 MVP 推进到单机常用能力子集；M24 负责 Studio / Web Admin 文档管理面；M25 负责 MongoDB 参考 parity、长稳、容量报告和发布文档。下一阶段如果希望用户从 MongoDB 迁移或用 MongoDB 心智开发 SonnetDB，缺口不只是“能存 JSON”，而是查询、更新、索引、批量写、变更订阅、工具链和错误语义都要顺手。
>
> **目标**：在不改变 SonnetDB 多模型统一底座定位的前提下，补齐 Document Store 的 MongoDB-like 日常开发体验：常用查询操作符、局部更新、数组语义、复合/多键/TTL 等索引、聚合 pipeline 扩展、bulk/upsert/findOneAndUpdate、变更订阅、SDK/迁移工具和 Studio 工作流。对外口径仍是 **MongoDB-like document workloads**，不是 MongoDB-compatible database。
>
> **依赖关系**：本里程碑排在 M25 之后。M25 #173 的 parity 报告必须把未覆盖项输出为结构化 `gap_reason`，M32 按 gap 优先级分批关闭；M24/M29 的管理面和写审批框架作为 UI/操作入口，不在 M32 重复造外壳。

### 能力差距矩阵

| 领域 | 目标能力 | 说明 |
|---|---|---|
| CRUD 与局部更新 | `$set` / `$unset` / `$inc` / `$mul` / `$rename` / `$currentDate` / `$min` / `$max`，数组 `$push` / `$pull` / `$addToSet` / `$pop`，upsert，findOneAndUpdate | 让高频业务代码不必整文档替换；更新语义必须保持单文档原子性。 |
| 查询操作符 | 数组、嵌套 path、`$elemMatch`、`$regex`、`$type`、`$size`、`$all`、`$not`、collation 基础规则 | 先覆盖 MongoDB 应用最常见 filter 组合，复杂 collation / 地理空间另行评估。 |
| 索引 | compound、unique、multikey、TTL、partial、sparse、wildcard、索引选择与 `EXPLAIN` | 先做单机确定性和恢复一致性，再做优化器选择。 |
| Aggregation | `$match` / `$project` / `$group` / `$sort` / `$limit` / `$skip` / `$unwind` 深语义，表达式系统，`$lookup` / `$facet` / `$bucket` 评估 | 保持 SonnetDB-native pipeline，不追求语法 100% 复刻。 |
| 批量写与事务边界 | `bulkWrite`、ordered/unordered、upsert 结果、错误分项、轻事务语义、重试安全结果 | 明确哪些是单文档原子，哪些是批次级 best-effort 或轻事务。 |
| 变更订阅 | SonnetDB-native change feed / resumable cursor | 对标 Change Streams 的使用体验，但不承诺 MongoDB wire 协议。 |
| SDK 与迁移 | .NET DocumentClient fluent builder、多语言连接器文档 API、MongoDB collection 导入、差异报告 | 让用户少拼 JSON filter，能从 MongoDB 导出/导入并看到不兼容项。 |
| Studio 体验 | 查询构建器、索引设计器、update preview、bulk import/export、变更流查看 | UI 复用 M24/M29 外壳与写审批。 |

### PR 拆分

| PR | 主题 | 状态 |
|----|------|------|
| #272 | **MongoDB-like gap report 与采纳门槛**：基于 M25 #173 parity 结果生成能力差距清单，按 CRUD / query / update / index / aggregation / bulk / change feed / SDK 分类，给每项定义 `supported` / `partial` / `planned` / `not_planned`、风险、兼容差异和验收样例。README / docs 只声明已通过门槛的能力。 | 📋 |
| #273 | **DocumentClient fluent API 与错误语义**：在 `SonnetDB.Data` 或专用 Document SDK 中提供类型化 filter / projection / sort / update builder、分页 cursor、标准化错误码和批量结果模型；多语言连接器先对齐 REST/Frame 文档 API，不引入 MongoDB Driver 适配。 | 📋 |
| #274 | **局部更新与数组更新第一批**：补齐 `$set` / `$unset` / `$inc` / `$mul` / `$rename` / `$currentDate` / `$min` / `$max`，以及 `$push` / `$pull` / `$addToSet` / `$pop`；支持 upsert 和 findOneAndUpdate 的返回前/返回后选项；所有更新保持单文档原子性，写 WAL 前形成可审计 patch plan。 | 📋 |
| #275 | **查询操作符与数组语义补齐**：补 `elemMatch`、数组包含/全包含、`regex`、`type`、`size`、复杂 `not`、嵌套 path 边界与基础 collation；优化 filter AST 到 SQL / Document executor 的映射，错误信息必须指出不支持的 operator/path。 | 📋 |
| #276 | **文档索引第二波**：实现 compound、unique、multikey、TTL、partial、sparse、wildcard 索引及 rebuild/validate；补索引一致性 crash test、TTL 后台清理、索引选择 `EXPLAIN` 和 Studio 索引设计器契约。 | 📋 |
| #277 | **Aggregation pipeline 深化**：扩展 SonnetDB-native pipeline 表达式系统，补 `$unwind` 深语义、`$project` 表达式、`$group` accumulator、`$lookup` / `$facet` / `$bucket` 的可行子集；大结果集必须支持分页/流式输出，不把 pipeline 变成长事务工作流。 | 📋 |
| #278 | **Bulk write、轻事务与并发语义**：提供 `bulkWrite` ordered/unordered、insert/update/delete/upsert 混合批次、分项错误、幂等重试结果；明确单文档原子、多文档轻事务和回滚边界，补并发写一致性测试。 | 📋 |
| #279 | **Document change feed**：基于 Document WAL / 版本序列提供 collection 级 change feed、resume token、过滤、过期和权限边界；Web Admin 可查看实时变更。该能力对标 Change Streams 使用体验，但命名和协议保持 SonnetDB-native。 | ✅ |
| #280 | **迁移工具与兼容报告**：新增 MongoDB dump / NDJSON / Extended JSON 导入路径、字段类型映射、索引定义迁移建议、dry-run 差异报告；输出“不支持操作符 / 不支持索引 / 需要应用改造”的机器可读报告。 | 📋 |
| #281 | **Studio 与文档收口**：Document 查询构建器、update preview、bulk import/export、索引设计器、change feed viewer 接入 M24/M29 外壳；补 MongoDB-like 使用指南、迁移手册、能力矩阵和示例应用。 | 🚧（Web Admin / Studio 共享界面与 API 文档已完成；MongoDB 迁移手册、能力矩阵和示例应用随 #272/#280 继续） |

### 验收标准

- 典型 MongoDB 风格应用代码可用 SonnetDB DocumentClient 完成 CRUD、局部更新、upsert、分页查询、索引创建和聚合，不需要手写大段 JSON 字符串。
- M25 parity 中所有规划内 `gap_reason` 被关闭或降为明确 `not_planned`，每个 `not_planned` 都有文档化替代方案。
- 索引 rebuild、TTL 清理、crash recovery、并发写、bulk 部分失败都进入自动化测试或长稳报告。
- Studio 能完成文档查询、局部更新预览、索引管理、导入导出和 change feed 查看，危险写操作走 M29 写审批。
- README / docs 对外只使用“MongoDB-like”或“Document Store”表述，除非未来单独里程碑完成 wire protocol 兼容验证。

### 不做的事

- **不**在本里程碑承诺 MongoDB wire protocol、BSON command、官方 MongoDB Driver 直连或 replica set / sharding 兼容；若要做，必须另起兼容层里程碑，并先证明协议、认证、游标、错误码和回滚路径。
- **不**把 SonnetDB 改成单一文档数据库；时序、关系、KV、全文、向量、对象和 MQ 的多模型统一底座仍是主定位。
- **不**为了追 MongoDB 语法牺牲 SonnetDB 现有 SQL / HTTP / Frame API 的稳定性；MongoDB-like 能力通过自有 API 暴露。
- **不**把跨集合长事务、分布式事务、分片和副本集作为本阶段目标。

---

## Milestone 33 — 时序聚合执行与下推优化（Aggregate Execution & Pushdown Optimization）

> **背景**：M28 已把 SQL legacy 聚合（count/sum/min/max/avg(field)）接到底层 `QueryEngine.ExecuteAggregateFast`（block metadata / SIMD / range aggregate 快路径），全量数值聚合从「物化数千万点再聚合」降到内存百 MiB 级。但对着实际聚合执行路径（`SelectExecutor.ExecuteAggregate` 与 `QueryEngine`）走查后，还剩一处**正确性缺陷**和一批**执行/下推**优化，均已定位到具体行：
> - **跨字段 Geo 聚合静默丢过滤（correctness）**：`SelectExecutor` 在 1318 行按「当前聚合字段」过滤 `where.GeoFilters`，而 Geo 谓词永远挂在 GeoPoint 字段（如 `position`）上、聚合字段是数值（如 `speed`），二者字段名不同 → per-field 子集恒为空 → 快路径判定（1501 行 `CanUseLegacyAggregateFastPath` 的 `geoFilters.Count != 0`）不触发、慢路径（1357 行 `QueryPoints(..., geoFilters=空)`）也不施加 → `SELECT avg(speed) WHERE geo_bbox(position,...)` 的 Geo 约束在**快慢两条路上都被丢掉**。这不是边缘情况，是「只要聚合带跨字段 Geo 就必错」。
> - **同字段多聚合重复扫描**：`count(v),avg(v),min(v),max(v)` 现在按聚合函数分别构造 `AggregateQuery`、对同一字段扫 4 遍元数据/块；而底层 `AggregateState`（QueryEngine.cs:1525）本就同时累加 Count/Sum/Min/Max，`ToBucket(aggregator)` 只投影其一、丢弃其三 → 引擎侧支持「一次扫描返回全量统计」几乎零成本。
> - **残差聚合强制物化**：`avg(v) WHERE v>10` 不能走底层快路径（合理），但 1357 行 `QueryPoints(...).ToList()` 把点全物化；而 `Query.Execute(PointQuery)` 自 M28 #220 起已是惰性流式 k-way merge，聚合循环会完整枚举 → 可边枚举边判残差边累加，内存 O(N)→O(1)。
> - **count(\*) 巨型 HashSet**：`AccumulateCountStar` 为「按时间戳行去重」建 `Dictionary<long,HashSet<long>>`，超大数据下内存重；单 field schema 无需去重（== block metadata count），多 field 可 k-way merge 计不同时间戳。
> - **LIMIT / ORDER BY DESC 未下推**：`SELECT * ... LIMIT N` 结果层才 `Skip().Take()`（先扫完整 measurement）；`ORDER BY time DESC LIMIT N` 的 Top-N 也是结果层，raw rows 仍先构建。可分别做 limit 提前中止与 latest-N 反向扫描下推。
>
> **目标**：先修正跨字段 Geo 聚合的正确性缺陷，再按「收益/成本比」推进执行与下推优化——引擎侧多聚合状态复用、残差流式化、count(\*) 专路、LIMIT 下推、latest-N。全程**不改聚合结果语义**（除 Geo 从「错」变「对」），数值快路径性能不回退。
>
> **与 M31 的关系**：M31 做聚合**类型语义**（把 `first/last/min/max/mode` 拓宽到字符串/布尔），M33 做聚合**执行性能**；两者都改 `AggSlot` 与 `CanUseLegacyAggregateFastPath` 这块共享面。M33 的快路径门控收紧必须与 M31 #269「按函数声明可接受类型」保持一致；建议 M31 #269（能力声明）先落或与 M33 #282 协同，避免两边各改快路径门控产生冲突。#283 的多聚合单次扫描仅覆盖**数值** count/sum/min/max；`first/last` 与 M31 拓宽的 selector / categorical 聚合走各自路径，两者正交。

### 阶段总览

| PR | 标题与范围 | 状态 |
|----|------------|------|
| #282 | **跨字段 Geo 聚合正确性修复**：把作用在非聚合字段上的 Geo 谓词当成**时间戳级约束**——先算出该 GeoPoint 字段命中 box 的时间戳集，再让数值字段的聚合只纳入这些时刻；快慢两路一致。立即止血：`where.GeoFilters.Count != 0`（整句而非本字段）时禁用 legacy 快路径，避免快路径给错答案。验收 `avg/count/min/max(num) WHERE geo_bbox(pos,...)` 结果与逐点参考实现一致。 | ✅ |
| #283 | **同字段多聚合单次扫描（MultiAggregateQuery）**：引擎侧新增批量聚合接口，一次扫描返回 Count/Sum/Min/Max（`AggregateState` 已是多聚合状态）；SQL 层 `ExecuteAggregate` 按 (series,field) 分组，把同字段的多个 legacy 聚合槽合并到一次 `Query` 调用再分发。`avg=sum/count` 天然包含。4×→1× 元数据/块扫描。对拍验证结果不变。 | ✅ |
| #284 | **残差聚合流式化 + 空 lookup 短路**：新增 `QueryPointsStream(...)` helper，残差路径与跨字段 Geo 的 `.Where().ToList()` 改惰性枚举，边判残差/Geo 边更新 `AggSlot`，内存 O(N)→O(1)、CPU 不变；`BuildResidualLookups` 在无残差谓词时短路返回共享只读空表，消除 per-(series,spec) 空字典分配。纯流式 `count(<string>)` 路径实测 10k↔40k 点查询期净分配恒为约 5KB（不随 N 线性增长）；残差 / 跨字段 Geo 大数据集聚合结果与逐点物化参考逐值一致。 | ✅ |
| #285 | **count(\*) 专门路径**：单 field schema 走 block metadata count（一个时间戳一个值，无需去重）；多 field 用各字段有序时间戳的 k-way merge 计不同时间戳，替掉 `Dictionary<long,HashSet<long>>`（O(N) 时间 / O(k) 内存）。有 residual / geo 时回退现路径。 | ✅ |
| #286 | **SELECT \* LIMIT/OFFSET 下推**：无 residual / window / 复杂 scalar 时，把 LIMIT/OFFSET 下推到时间轴合并器——凑够 N 个不同时间戳即提前中止，避免扫完整 measurement。注意 raw 路径按跨字段时间戳并集，「N 行」= 前 N 个不同时间戳。 | ✅ |
| #287 | **ORDER BY time DESC LIMIT N latest-N**：在 `PointQuery` / SQL planner 增加 `Direction.Desc + Limit` 下推，段从后往前扫、拿够 N 即停（引擎 `TryGetLatestPoint` 已有按 MaxTimestamp 反向扫先例，QueryEngine.cs:261）。本批工程量最大，排最后。 | ✅ |

### 验收标准

- 跨字段 Geo 聚合（#282）结果与「逐点施加 Geo 后聚合」的参考实现逐桶一致；快慢两路、`GROUP BY time` 与非分桶均覆盖。
- 多聚合单次扫描（#283）与逐个聚合的旧结果对拍逐值相等（含 Boolean 字段 sum/avg 的新旧一致性）；`SegmentBlockReads` 计量显示同字段多聚合的块读次数从 N× 降到 1×。
- 残差流式化（#284）在大数据集上峰值内存不随点数线性增长；聚合结果与物化路径一致。
- count(\*) 专路（#285）单/多 field 结果与现 HashSet 实现一致；大数据集峰值内存显著下降。
- LIMIT 下推（#286）/ latest-N（#287）返回行与结果层分页/排序完全一致，且读取的点数/块数随 N 有界，不随 measurement 总量增长。
- **基准报告规范**：每项性能 PR 的 benchmark 必须写清冷/热、数据落盘状态、是否 flush、CPU/磁盘、命令行参数与 before/after 同口径数字（≥5 次取 median + p90），并在大数据上做新旧结果对拍——不接受单次采样或只报新值。

本里程碑 #285~#287 的同口径结果、环境与复现命令见 [M33 时序聚合执行与下推基准](docs/benchmarks/m33-aggregate-execution-pushdown.md)。

### 不做的事

- **不**改变除跨字段 Geo（从错到对）以外的任何聚合结果语义。
- **不**在数值快路径上引入回退；selector / categorical（M31）与数值多聚合（#283）走各自路径，互不牺牲。
- **不**把 LIMIT / latest-N 下推扩展到带 residual / window / 复杂 scalar 的查询（这些仍走物化路径）。
- **不**在本里程碑做 mmap block 读的池化 / 更细 block cache（correctness-sensitive，涉及 buffer 生命周期，优先级低于 SQL 物化路径，留作独立性能小项）。

---

## Milestone 34 — Modbus TCP 内建映射表（Master Collection / Slave Exposure）

> **背景**：工业现场大量 PLC / RTU / 采集仪表仍以 Modbus TCP 暴露数据。上层平台希望在 SonnetDB 中通过一段 SQL DDL 同时定义：Modbus 连接、寄存器区域、寄存器地址、字节序 / 字序、缩放转换、读写权限，以及这些寄存器和表字段之间的映射。该能力让 SonnetDB 在边缘侧既能作为**主站/client**主动连接外部从站/server 采集数据，也能作为**从站/server**暴露 Modbus TCP 端口，允许外部主站读取或受控写入 SonnetDB 映射字段。
>
> **定位修正**：M30 只做 MQTT / Sparkplug / CoAP / UDP line protocol 这类被动接收入口；Modbus TCP 不是普通 payload ingest，而是**工业寄存器映射 + SQL DDL + 运行时采集 / 暴露**能力，因此独立成 M34。该路线仍保持 SonnetDB 的数据库边界：默认面向本地 / 边缘部署，协议栈默认关闭，所有写寄存器或外部主站写入都必须经过权限、审计和可配置策略。
>
> **术语约定**：文档同时使用 Modbus 传统术语和现代 client/server 术语。**主站/master = client**，由 SonnetDB 主动连接外部 Modbus TCP 从站/server；**从站/slave = server**，由 SonnetDB 监听 Modbus TCP 端口，外部主站/client 来读写寄存器。

### 角色边界

| 角色 | 网络方向 | SQL 映射方向 | 数据语义 | 典型用途 |
|---|---|---|---|---|
| 主站 / client | SonnetDB → 外部 PLC/RTU 从站/server | `FROM MODBUS ...` | 轮询寄存器后写入表；可对 `ACCESS WRITE` 字段执行受控写寄存器 | 边缘采集、PLC 数据归档、控制设定值写回 |
| 从站 / server | 外部 SCADA/PLC 主站/client → SonnetDB | `EXPOSE AS MODBUS ...` | 把表字段暴露为本地寄存器；外部写寄存器按策略更新表或进入待确认区 | 兼容旧 SCADA、把 SonnetDB 数据镜像给现有上位机 |

### SQL 草案：主站 / client 采集表

第一版建议使用两层 DDL：`CREATE MODBUS SOURCE` 定义远端连接，`CREATE TABLE ... USING MODBUS SOURCE` 定义寄存器到字段的映射。这样同一个 PLC 连接可以被多张采集表复用，也可以在 Studio / Web Admin 中独立显示连接健康状态。

```sql
CREATE MODBUS SOURCE line1_plc
WITH (
    ROLE MASTER,
    TRANSPORT TCP,
    ENDPOINT '192.168.1.50:502',
    UNIT_ID 1,

    POLL_INTERVAL '1s',
    TIMEOUT '800ms',
    RETRY 3,

    ADDRESSING MODICON,
    BYTE_ORDER BIG_ENDIAN,
    WORD_ORDER BIG_ENDIAN
);

CREATE TABLE pump_runtime (
    sample_time DATETIME SAMPLE_TIME,

    running BOOL
        FROM MODBUS COIL(1)
        ACCESS READ_WRITE,

    fault BOOL
        FROM MODBUS DISCRETE_INPUT(2),

    speed_rpm INT
        FROM MODBUS HOLDING_REGISTER(40001)
        AS UINT16
        SCALE 1,

    temperature FLOAT
        FROM MODBUS INPUT_REGISTER(30001)
        AS INT16
        SCALE 0.1
        OFFSET 0,

    flow_rate FLOAT
        FROM MODBUS HOLDING_REGISTER(40010, 2)
        AS FLOAT32
        BYTE_ORDER BIG_ENDIAN
        WORD_ORDER BIG_ENDIAN,

    alarm_bit BOOL
        FROM MODBUS HOLDING_REGISTER(40020).BIT(3)
        AS BIT,

    PRIMARY KEY (sample_time)
)
USING MODBUS SOURCE line1_plc
WITH (
    ROLE MASTER,
    TABLE_MODE HISTORY,
    ON_ERROR KEEP_LAST,
    STORE HISTORY
);
```

主站表的执行语义：

- `SELECT` 默认查询 SonnetDB 已采集 / 已持久化的数据，不在查询线程里同步阻塞访问 PLC。
- `TABLE_MODE HISTORY` 表示每次轮询追加一行；`TABLE_MODE LATEST` 表示只保留最新快照，历史可选落到 companion measurement。
- `SAMPLE_TIME` 是采集成功或采集批次闭合时间；不由外部寄存器提供。
- `UPDATE pump_runtime SET speed_rpm = 1200 ...` 只允许作用于 `ACCESS WRITE` 或 `ACCESS READ_WRITE` 字段，执行 Modbus 写线圈 / 写寄存器，并记录审计事件；第一版必须限制 WHERE 形态，避免把普通关系表批量更新误解释为大量寄存器写入。
- 读失败按 `ON_ERROR` 策略处理：`KEEP_LAST`、`NULL_VALUE`、`SKIP_SAMPLE`、`MARK_BAD_QUALITY` 作为候选；最终落地前需定义 bad quality 是否存入隐藏列、JSON metadata 或 companion diagnostics 表。

### SQL 草案：从站 / server 暴露表

从站模式下，SonnetDB 监听一个本地 Modbus TCP 端口，外部主站读取或写入寄存器。语法上使用 `CREATE MODBUS ENDPOINT` 定义本地端口，表字段使用 `EXPOSE AS MODBUS ...` 声明本地寄存器映射。

```sql
CREATE MODBUS ENDPOINT local_line_shadow
WITH (
    ROLE SLAVE,
    TRANSPORT TCP,
    BIND '0.0.0.0:1502',
    UNIT_ID 1,

    ADDRESSING MODICON,
    BYTE_ORDER BIG_ENDIAN,
    WORD_ORDER BIG_ENDIAN,

    WRITE_POLICY STAGED,
    AUDIT TRUE
);

CREATE TABLE line_shadow (
    id INT NOT NULL,

    running BOOL
        EXPOSE AS MODBUS COIL(1)
        ACCESS READ_WRITE,

    speed_rpm INT
        EXPOSE AS MODBUS HOLDING_REGISTER(40001)
        AS UINT16
        ACCESS READ_WRITE,

    temperature FLOAT
        EXPOSE AS MODBUS INPUT_REGISTER(30001)
        AS INT16
        SCALE 0.1
        ACCESS READ,

    PRIMARY KEY (id)
)
USING MODBUS ENDPOINT local_line_shadow
WITH (
    ROLE SLAVE,
    ROW KEY 1,
    ON_EXTERNAL_WRITE UPDATE_TABLE
);
```

从站表的执行语义：

- 外部主站读 `COIL` / `DISCRETE_INPUT` / `HOLDING_REGISTER` / `INPUT_REGISTER` 时，SonnetDB 从映射表的当前值编码出寄存器响应。
- 外部主站写 `COIL` 或 `HOLDING_REGISTER` 时，只能命中 `ACCESS WRITE` / `ACCESS READ_WRITE` 字段；写入先按 `WRITE_POLICY` 处理：`REJECT`、`STAGED`、`UPDATE_TABLE`、`CALL_APPROVED_HANDLER` 作为候选。
- `WRITE_POLICY STAGED` 是生产默认建议：外部写入先进入待确认 / 审计队列，由 Web Admin、IoTSharp 或授权 Agent 确认后再更新业务表，避免现场误写直接改变平台状态。
- `ROW KEY 1` 表示该端点暴露固定一行的寄存器影子表；后续可扩展为 `ROW KEY FROM UNIT_ID` 或 `ROW KEY FROM REGISTER_BLOCK`，但第一版不把 Modbus 地址空间滥用成无限关系表游标。

### 寄存器和类型映射

| Modbus 区域 | 功能码 | SQL 映射 | 默认访问 |
|---|---|---|---|
| `COIL(n)` | 01 / 05 / 15 | `BOOL` 或 `BIT` | 读写 |
| `DISCRETE_INPUT(n)` | 02 | `BOOL` 或 `BIT` | 只读 |
| `HOLDING_REGISTER(n[, count])` | 03 / 06 / 16 | `INT` / `FLOAT` / `STRING` / `BOOL` bit | 读写 |
| `INPUT_REGISTER(n[, count])` | 04 | `INT` / `FLOAT` / `STRING` / `BOOL` bit | 只读 |

首批类型候选：`BIT`、`INT16`、`UINT16`、`INT32`、`UINT32`、`FLOAT32`、`FLOAT64`、`BCD16`、`BCD32`、`STRING(n)`。所有多寄存器类型都必须显式定义 `BYTE_ORDER` / `WORD_ORDER` 的默认继承和列级覆盖规则；所有数值类型都支持 `SCALE` / `OFFSET`，语义固定为 `sql_value = raw_value * scale + offset`。

### 阶段总览

| 阶段 | 主题 | PR 范围 | 目标 |
|------|------|---------|------|
| **A** | SQL DDL 与 catalog 基线 | #288 ~ #290 | 定义 source / endpoint / table mapping 语法、AST、catalog 持久化、SHOW/DESCRIBE 元数据 |
| **B** | 主站 / client 采集闭环 | #291 ~ #293 | Modbus TCP client、轮询调度、寄存器解码、历史 / latest 表写入、受控写寄存器 |
| **C** | 从站 / server 暴露闭环 | #294 ~ #295 | Modbus TCP server、寄存器编码、外部写入策略、审计与待确认 |
| **D** | 管理面、文档、parity 与 IoTSharp 边界 | #296 | Studio / Web Admin 映射查看、SQL 文档、模拟 PLC parity、IoTSharp / EdgeNode 合同边界 |

### PR 拆分

| PR | 标题与范围 | 状态 |
|----|------------|------|
| #288 | **Modbus SQL DDL 设计与方言文档**：新增 `CREATE MODBUS SOURCE`、`CREATE MODBUS ENDPOINT`、`CREATE TABLE ... USING MODBUS SOURCE/ENDPOINT` 的语法草案；明确 `FROM MODBUS`（主站采集）与 `EXPOSE AS MODBUS`（从站暴露）的方向语义、寄存器区域、类型、字节序、缩放、访问权限和错误策略。只落文档 / parser 设计，不实现运行时。 | 📋 |
| #289 | **Parser / AST / catalog 元数据**：扩展 SQL parser 和 AST，持久化 Modbus source、endpoint、table mapping、column mapping、quality/error policy；新增 `SHOW MODBUS SOURCES`、`SHOW MODBUS ENDPOINTS`、`DESCRIBE MODBUS TABLE <name>`。Core 仍零第三方依赖，catalog 格式变更必须有版本与兼容读。 | 📋 |
| #290 | **映射校验与类型编解码库**：实现寄存器地址冲突检测、读写区域约束、register count 推导、`BIT` / `INT16` / `UINT16` / `INT32` / `UINT32` / `FLOAT32` / `FLOAT64` / `BCD` / `STRING(n)` 编解码、`SCALE` / `OFFSET` 和 endian / word-order 覆盖。用纯 BCL + Span/BinaryPrimitives，不引重型协议依赖。 | 📋 |
| #291 | **Modbus TCP 主站 client 与轮询调度**：Server 层新增默认关闭的 Modbus master runtime，按 source 建立 TCP 连接、执行功能码 01/02/03/04 批量读取、按 mapping 解码后写入 `TABLE_MODE HISTORY` 或 `LATEST` 表；轮询任务有取消、退避、超时、重连和指标。 | 📋 |
| #292 | **主站写寄存器与 SQL 写入语义**：支持对 `ACCESS WRITE` / `READ_WRITE` 字段执行功能码 05/06/15/16 写入；限制可写 SQL 形态，写前 preview / dry-run，写后审计；写失败不得伪造表成功更新。与 M29 写审批框架和服务端权限模型对齐。 | 📋 |
| #293 | **主站质量、诊断与 latest/history 双写策略**：补采集质量位、错误码、最后成功时间、连续失败计数、source health；明确 `KEEP_LAST` / `NULL_VALUE` / `SKIP_SAMPLE` / `MARK_BAD_QUALITY` 的落库差异；提供 latest shadow + history archive 的可选组合。 | 📋 |
| #294 | **Modbus TCP 从站 server MVP**：Server 层新增默认关闭的 Modbus slave endpoint，监听 TCP 端口，按 `EXPOSE AS MODBUS` 映射响应外部主站的 01/02/03/04 读请求；编码当前表字段为 coil / register。端口绑定、unit id、访问白名单和最大连接数均显式配置。 | 📋 |
| #295 | **从站外部写入策略与审计**：支持外部主站功能码 05/06/15/16 写入本地映射字段；实现 `REJECT` / `STAGED` / `UPDATE_TABLE` 策略、审计事件、待确认队列和权限边界。默认推荐 `STAGED`，不允许匿名外部写入静默修改业务数据。 | 📋 |
| #296 | **管理面、文档、模拟器与 IoTSharp 边界收口**：Web Admin / Studio 展示 source、endpoint、寄存器映射、轮询状态和最近错误；新增 Modbus SQL 文档、示例、模拟 PLC / 外部主站 parity 测试；明确 IoTSharp Product / Collection Template / Gateway / EdgeNode 如何消费这些合同，禁止通过隐藏内部表耦合。 | 📋 |

### 推进顺序

```text
A DDL/catalog：
#288（SQL 设计文档）
  → #289（Parser / AST / catalog）
  → #290（类型编解码与映射校验）

B 主站/client：
#291（TCP client + 轮询读）
  → #292（写寄存器 + 写审批）
  → #293（质量 / 诊断 / latest-history 策略）

C 从站/server：
#294（TCP server + 读寄存器）
  → #295（外部写入策略 + 审计）

D 收口：
#296（管理面 + 文档 + parity + IoTSharp 边界）
```

> **并行度**：#291 主站 runtime 与 #294 从站 runtime 在 #289/#290 后可并行；#292 与 #295 都涉及高风险写入策略，必须复用同一套权限、审计、preview / staged confirmation 思路。#296 最后收口，避免 UI / docs 先承诺未实现的写入语义。

### 验收标准

- 建表 DDL 可以完整表达 Modbus TCP 主站连接、寄存器区域、地址、类型、字节序、缩放、访问权限和表模式。
- 主站模式下，SonnetDB 能连接模拟或真实 Modbus TCP 从站，轮询 `COIL` / `DISCRETE_INPUT` / `HOLDING_REGISTER` / `INPUT_REGISTER` 并写入映射表；SQL 回查结果与寄存器原始值转换一致。
- 主站写寄存器必须只允许 `ACCESS WRITE` / `READ_WRITE` 字段，失败时不更新本地表为成功状态，并产生审计事件。
- 从站模式下，外部 Modbus TCP 主站能读取 SonnetDB 暴露的寄存器；外部写入按 `WRITE_POLICY` 生效，默认不绕过审批直接修改业务数据。
- 所有协议服务默认关闭；启用时必须显式配置端口、绑定地址、unit id、访问白名单 / token 映射和最大连接数。
- 可观测性覆盖轮询成功/失败、响应时延、重连次数、连续失败、外部写入次数、拒绝次数和待确认数量。
- 文档必须明确主站/client 与从站/server 的方向差异，避免用户把“我们去连别人”和“别人来改我们”混成一种表配置。
- IoTSharp 集成只能通过 Product / Collection Template / Gateway / EdgeNode / ReleaseTask 等稳定合同消费该能力，不直接依赖 SonnetDB 内部 catalog 表。

### 不做的事

- **不**在第一版支持 Modbus RTU / ASCII / 串口网关；仅做 Modbus TCP。
- **不**把 SonnetDB 变成完整 PLC 工程站、SCADA 或边缘工作流引擎；长运行任务、发布和回滚仍归 IoTSharp Release Center / EdgeNode。
- **不**把 OPC UA client、S7、三菱、FINS、AB、MTConnect 一并塞进本里程碑；这些需要独立协议评估。
- **不**默认开放 502 端口或匿名写入；所有 Modbus runtime 默认关闭，生产启用必须显式配置。
- **不**用普通 `SELECT` 做实时阻塞式 PLC 读取；查询读取 SonnetDB 已采集状态，实时刷新通过轮询 runtime 或显式维护命令处理。
- **不**允许外部主站写入绕过审计、权限和 staging policy 直接修改关键业务表。

---

## Milestone 35 — 语义内容与多模态检索（Semantic Content & Multimodal Retrieval）

> **背景**：SonnetDB 已具备对象桶、Document Collection、全文 BM25、Document Vector、Measurement `VECTOR(N)`、HNSW / IVF / IVF-PQ / Vamana、Hybrid Search、Embedding Provider 和 Vector Playground。这些能力说明 SonnetDB 已有多模态检索的数据库底座，但还没有把“原始图片 / 文档 / 音视频 → 内容提取 → embedding → 索引 → 跨模态查询 → 更新删除闭环”串成可直接使用的产品能力。
>
> **定位**：M35 不新增第九种数据模型，也不把模型推理塞进数据库内核。它在现有五类能力上增加一层可治理的语义内容索引：
>
> ```text
> Object Bucket（原始内容）
>        ↓
> Server / 可选扩展（提取、分块、Embedding、异步任务）
>        ↓
> Document Collection（内容清单、来源、模型版本、chunk / timecode、状态）
>        ↓
> FullText / Vector 派生索引（可重建）
>        ↓
> SQL / HTTP / SDK / Studio（向量、相似对象、以图搜图、RAG）
> ```
>
> **术语边界**：
> - **多模型数据库**表示时序、关系、KV、Document、全文、向量、对象、消息队列共享一个引擎。
> - **多模态检索**表示文字、图片、音频、视频片段通过兼容的 embedding 空间或文本派生内容进行语义互查。
> - 在 #301 以图搜图闭环完成前，对外只宣称“具备多模态检索底座”，不宣称已经提供完整多模态数据库体验。

### 领域与组件边界

| 组件 | 负责 | 不负责 |
|---|---|---|
| `SonnetDB.Core` | 确定性的向量存储、索引、过滤、相似度计算、全文与融合查询、索引重建 | 下载模型、解析媒体、调用云端 AI、同步执行不可控推理 |
| SonnetDB Server | Provider 编排、异步摄取任务、权限、审计、限流、状态与查询 API | 绕过数据库主数据直接维护隐藏向量副本 |
| Object Bucket | 原始图片、音频、视频、PDF 及版本 / ETag / hash | 把大对象字节复制进向量索引或 JSON 文档 |
| Document Collection | 内容清单、object reference、MIME、hash、来源、chunk / timecode、embedding profile、索引状态 | 代替对象桶保存大文件 |
| FullText / Vector Index | 可重建的派生检索结构 | 成为第二套权威主数据或隐藏 catalog |
| Web Admin / Studio / SDK | 摄取、状态、查询、命中解释、人工重试与评测 | 在浏览器内静默上传敏感内容到外部 Provider |

### 第一版内容清单

M35 第一版不新增 `IMAGE` / `AUDIO` / `VIDEO` SQL 类型，而是在受治理的 Document Collection 中保存稳定清单。具体 JSON 字段名由 #297 设计定稿，但至少覆盖：

- `objectRef`：bucket、key、version / etag；原始字节唯一来源。
- `contentHash`、`mimeType`、`modality`、`size`、`source`：幂等和过滤基础。
- `embeddingProfileId`：生成向量的 provider、model、revision、dimension、metric、normalization 与支持模态。
- `indexState`：pending / running / ready / stale / failed、attempt、lastError、updatedAt。
- `text` / `chunks` / `segments`：OCR、文档切片、音视频转写、关键帧和 `startMs` / `endMs`。
- 一个内容项可有多个命名向量字段，例如视觉向量、文本向量、OCR 向量；查询必须选择明确 profile，不把同维但不同模型的向量混在一起比较。

### 专业视觉识别场景

专业视觉能力复用 M35 的内容清单、异步任务、过滤 ANN 和 Provider 边界，但每个场景必须使用独立模型、向量字段和 `EmbeddingProfile`。通用图文模型只能承担语义检索，不能替代人脸、ReID、步态或车牌识别模型。

| 场景 | 图搜图 / 视频搜视频 | 文搜图 / 文搜视频 | 数据与索引路径 |
|---|---|---|---|
| 通用图片 | 图片检索视觉相似图片 | 用自然语言检索图文对齐内容 | `semanticEmbedding` + OCR / caption FullText |
| 人脸 | 人脸图片做 1:1 验证或 1:N 相似候选检索 | 仅按已授权人员 ID、姓名或业务标签查找，不用自然语言模型猜测身份 | 人脸检测 / 对齐 → `faceEmbedding`；身份元数据走精确索引 |
| Person ReID | 人物截图检索跨摄像头相似 track | 按服饰、时间、地点、摄像头和受控描述过滤 / 检索 | 人体检测 / 跟踪 → `reidEmbedding` + track metadata |
| 步态识别 | 行走视频片段检索相似步态序列 | 按行走、徘徊等标签检索片段；身份检索不依赖通用文本向量 | 人体轮廓 / 关键点时序 → `gaitEmbedding` + timecode |
| 姿态 / 动作 | 视频片段检索相似姿态或动作 | 按站立、行走、跌倒等动作标签 / 描述检索 | pose keypoints / action embedding + segment metadata |
| 车辆外观 | 车辆截图检索相似车辆与跨摄像头 track | 按车型、颜色、时间、地点等属性检索 | `vehicleEmbedding` + vehicle / track metadata |
| 车牌 | 车牌 / 车辆截图定位同号或相似候选 | 输入标准化车牌号精确查图；OCR 低置信结果可做受限候选召回 | 车牌检测 / 矫正 / OCR → normalized plate 精确索引；车辆图像另走向量 |

> **识别术语**：人脸“验证”是 1:1 比对，“识别”是 1:N 候选检索；Person ReID 依据人体外观，步态识别依据连续行走序列，姿态 / 动作识别判断骨架或行为。这四类问题不得在 API、评测和产品文案中混成一个“人体识别”。

### 阶段总览

| 阶段 | 主题 | PR 范围 | 目标 |
|---|---|---|---|
| **A** | 检索正确性与语义合同 | #297 ~ #298 | 定义内容 / Profile 合同，补 metadata-filtered ANN 与相似对象查询原语 |
| **B** | 摄取与 Provider | #299 ~ #300 | 建立对象到 Document / Vector / FullText 的异步、幂等、可恢复索引链路 |
| **C** | 首批产品场景 | #301 ~ #302 | 交付以图搜图 / 文搜图和可复用 RAG 摄取检索 |
| **D** | 检索质量 | #303 | 融合、去重、重排钩子、离线评测与可解释性 |
| **E** | 扩展模态与通用产品基线 | #304 ~ #305 | 音视频分段模型、管理面、恢复 / 备份 / 安全 / 容量基线 |
| **F** | 专业视觉识别扩展 | #306 ~ #309 | 派生目标 / 轨迹、人脸、ReID / 步态 / 姿态动作、车辆 / 车牌检索 |

### PR 拆分

| PR | 标题与范围 | 状态 |
|---|---|---|
| #297 | **Semantic Content 合同与 Embedding Profile**：定义内容清单、object reference、chunk / segment、索引状态和 `EmbeddingProfile` 合同；profile 明确 provider、model、revision、dimension、metric、normalization、supported modalities 和数据外发策略。首版优先用系统 Document / Table 主数据承载，不为保存模型元数据直接修改既有向量索引二进制格式；补 schema validator、API DTO、AOT JSON context、迁移与不支持项文档。 | 📋 |
| #298 | **Metadata-filtered ANN + similar-by-id**：为 Document Vector 增加可解释的过滤 ANN 计划，支持基于 `_id` / JSON path 索引候选集的 pre-filter，或 oversampling + 精确补偿；候选不足时必须回退精确扫描，不能静默返回错误 Top-K。新增按文档 / 内容 ID 读取既有向量后搜索相似项的 SQL / API / SDK 原语；`EXPLAIN` 显示 prefilter、ANN、补偿与回退原因。 | 📋 |
| #299 | **语义内容异步摄取与索引生命周期**：Server 新增默认关闭的摄取 worker，把对象版本 / ETag 与内容清单绑定，支持 pending/running/ready/stale/failed、幂等 hash、重试、取消、限流、背压和重启恢复；对象覆盖 / 删除后标记派生内容失效并对账清理。对象与 Document 跨模型更新采用显式状态机和 reconciliation，不伪装成当前不存在的跨模型原子事务。 | 📋 |
| #300 | **多模态 Embedding Provider 边界**：保留现有文本 `IEmbeddingProvider` 兼容入口，新增可表达 text / object reference / stream 与 MIME 的内容 embedding 抽象及 capability discovery；Server / 可选扩展支持 provider-neutral 的本地或远程实现。云端 Provider 必须显式配置、记录目标 / 模型 / 内容类型与调用审计，默认不允许把对象桶内容静默外发；`SonnetDB.Core` 不引入模型运行时或媒体解码依赖。 | 📋 |
| #301 | **以图搜图 MVP**：以图片作为第一种正式多模态内容，支持图片搜图片、文字搜图片、图片搜相关文字 / 文档；复用对象桶保存原图与缩略图，Document 保存 OCR / 描述 / profile / 向量，全文 + 向量做融合召回。提供 HTTP / SDK 和 Studio playground，结果展示缩略图、距离 / 融合分数、来源、模型 profile 与命中过滤条件；首批工业样例覆盖缺陷图片、设备铭牌和现场设备照片。 | 📋 |
| #302 | **通用 RAG 摄取与检索**：把 Copilot 私有 docs 管线中固定 measurement、固定 384 维和手写词法扫描收敛到 Document Collection + 原生 FullText / Vector / Hybrid Search；开放可复用 ingest SDK / CLI，支持 Markdown / text 第一批、稳定 chunk ID、overlap、来源引用、增量更新、删除同步和 profile 变更重建。Copilot 迁移需保留现有知识库回滚路径与引用语义。 | 📋 |
| #303 | **Hybrid 质量、重排与评测框架**：在现有加权融合之外评估并实现 RRF / score normalization、重复 chunk 抑制、同源多命中合并和可选 rerank hook；查询结果返回各分量分数与最终排序原因。新增 image→image、text→image、RAG 检索集，报告 Recall@K / nDCG、过滤命中正确性、P50/P95、索引体积和重建时间；ANN 门禁不得低于现有 parity 的 Recall@10 基线。 | 📋 |
| #304 | **音频 / 视频分段检索模型**：在 #301 稳定后扩展 transcript、关键帧和 `startMs` / `endMs` segment；查询返回具体片段而不是整文件单向量。解码、ASR、OCR、抽帧由 Server 可选扩展或外部工具完成，Core 只保存清单、文本、向量和时间范围；第一版不内置完整媒体处理平台。 | 📋 |
| #305 | **通用管理面、安全、恢复与容量基线**：Web Admin / Studio 新增 Semantic Content 工作台，覆盖对象选择、摄取状态、失败重试、profile、以图 / 文字查询、命中详情与索引健康；补对象覆盖 / 删除、Provider 失败、进程重启、索引重建、备份恢复、模型换代、敏感内容外发审计和 10k / 100k 内容档容量报告。通用能力达到验收后，README 才可从“多模态检索底座”升级为“多模态检索能力”；不同时宣称尚未验收的生物识别能力。 | 📋 |
| #306 | **派生目标、区域与轨迹内容模型**：在原始对象 / 视频 segment 之上定义 derived object，保存 `sourceObjectRef`、frame / `startMs` / `endMs`、bounding box / polygon、trackId、detector profile、质量与置信度；支持一个原图 / 片段派生多个人脸、人体、车辆、车牌区域，并把各区域绑定到独立 embedding / OCR / metadata。对象覆盖 / 删除必须级联标记派生记录 stale 并可对账重建，不复制原始大文件为第二份主数据。 | 📋 |
| #307 | **人脸验证与受治理的相似检索**：可选 Provider 执行人脸检测、质量检查、对齐和 face embedding；支持 1:1 verification 与 1:N candidate search，结果返回阈值、相似分数、模型 profile、来源区域和质量，不直接把 Top-1 当作确定身份。人员 ID / 姓名仅作为经过授权的精确 metadata 绑定与查询；增加显式启用、用途声明、独立权限、模板保护、访问审计、保留 / 删除策略和批量导出限制。评测报告 FAR / FRR / TAR、不同阈值、遮挡 / 角度 / 低照度与跨摄像头结果。 | 📋 |
| #308 | **Person ReID、步态与姿态 / 动作片段检索**：复用 #304 / #306 的视频 segment 与 track，分别接入人体外观 `reidEmbedding`、连续序列 `gaitEmbedding`、pose keypoints 和 action labels / embedding；支持截图→track、视频片段→步态片段，以及按时间 / 地点 / 摄像头 / 动作文字过滤检索。静态单图不得宣称完成步态身份识别。评测分别报告 ReID mAP / CMC、步态跨视角指标、姿态 / 动作 precision / recall，并覆盖服装变化、遮挡、视角和短序列降级。 | 📋 |
| #309 | **车辆外观与车牌 OCR 检索**：可选 Provider 完成车辆 / 车牌检测、透视矫正、OCR、地区规则标准化和置信度；车牌号码用 normalized exact index 做主查询，OCR 候选与编辑距离只用于显式模糊模式，不能用通用向量距离替代号码相等语义。车辆外观另存 `vehicleEmbedding`，支持车辆图片→相似车辆 / track 和车型、颜色、摄像头、时间等文搜图。评测报告字符准确率、整牌准确率、误报 / 漏报、低照度 / 模糊 / 遮挡和重复号码候选解释。 | 📋 |

### 推进顺序

```text
A 检索地基：
#297（内容与 Profile 合同）
  → #298（Filtered ANN + similar-by-id）

B 摄取链路：
#297 → #299（任务 / 对账 / 生命周期）
          → #300（多模态 Provider）

C 首批场景：
#298 + #299 + #300
  ├→ #301（图片搜图片 / 文字搜图片）
  └→ #302（通用 RAG）

D 质量：
#301 + #302 → #303（融合与 eval）

E 扩展和收口：
#301 → #304（音视频片段）
#303 + #304 → #305（管理面 / 安全 / 恢复 / 容量）

F 专业视觉识别：
#301 + #304 → #306（区域 / 目标 / 轨迹派生模型）
#305 + #306
  ├→ #307（人脸验证 / 相似检索与治理）
  ├→ #308（ReID / 步态 / 姿态动作）
  └→ #309（车辆 / 车牌 OCR 与检索）
```

> **优先级**：#297 / #298 是数据库通用能力，优先于具体媒体 Demo；#301 图片检索与 #302 通用 RAG 是第一批可交付产品场景，可在地基完成后并行。#304 音视频后置，避免第一版同时承担解码、ASR、抽帧和大文件资源治理。#306~#309 是建立在通用闭环之上的可选专业视觉扩展，不得为了展示“识别”而越过 Profile、权限、审计和评测基线。M35 不替代当前 M27 Industrial Data Agent、M32 Document 易用性或 M34 Modbus 路线；开始派单时应先确认这三条当前路线的带宽与依赖。

### 验收标准

- 同一内容版本只生成一组指定 profile 的有效派生数据；重复摄取幂等，对象覆盖 / 删除后旧 chunk、向量和全文命中最终可对账清除。
- 查询向量与目标索引的 profile / dimension / metric 不兼容时明确拒绝，不仅按维度碰巧相同就允许混查。
- Document Vector 带 metadata filter 时可以命中可解释的 ANN / pre-filter 路径；不满足正确性条件时自动精确补偿或回退，并在 `EXPLAIN` / 指标中可见。
- 图片搜索同时通过 image→image 与 text→image 固定数据集；结果可定位原对象版本、缩略图、profile、距离和过滤条件。
- 通用 RAG 支持增量摄取、稳定引用、删除同步、profile 换代重建；Copilot 不再维护一套绕开 Document FullText / Vector 的私有检索主路径。
- Provider 调用满足权限、超时、取消、限流、审计和数据外发策略；本地部署可以完全关闭云端外发。
- 备份恢复保存对象与内容主数据，派生全文 / 向量索引可校验和重建；重启和中途失败不会把 `running` 永久伪装成完成。
- 人脸、ReID、步态、车辆向量和通用图文向量分别绑定独立 profile / 字段；服务端拒绝跨 profile 比较，查询结果显示模型版本、阈值、质量与来源区域 / track。
- 人脸验收使用 FAR / FRR / TAR 与阈值曲线，ReID 使用 mAP / CMC，步态使用跨视角序列指标，姿态 / 动作使用 precision / recall；不得只用少量演示图片或 Top-1 命中作为“识别准确”证据。
- 车牌号码精确查询命中标准化文本索引，OCR 返回原始文本、标准化结果、候选、置信度和图像区域；图片向量只能辅助车辆外观和低置信候选，不能替代车牌号码相等判断。
- 所有生物特征功能默认关闭，启用时需要用途说明、独立权限、访问 / 导出审计、保留期限和按人员 / 来源删除闭环；删除后主数据、派生向量和索引最终一致清除。

### 不做的事

- **不**新增 `IMAGE` / `AUDIO` / `VIDEO` Core 数据类型；原始媒体保留在对象桶。
- **不**允许普通 `INSERT` / `SELECT` 在 Core 查询线程中同步调用外部模型、下载模型或解码大文件。
- **不**把 embedding 向量、全文索引或缩略图变成第二套权威主数据；派生数据必须可重建。
- **不**承诺不同 embedding 模型、不同 revision 或不同归一化方式的向量可以直接比较。
- **不**在 M35 内实现完整推荐系统；Core 只提供 similar-by-id、过滤、融合和重排原语，业务特征、反馈闭环与实验平台归上层应用。
- **不**把 Agent Memory 做成 Core 专用领域。遵守当前 M22 边界，优先以独立 adapter / 示例消费 Document、Vector、TTL 和时间排序能力；只有沉淀出数据库通用缺口才回到 Core / Server。
- **不**把通用 CLIP / caption 模型的相似结果当成人脸身份、步态身份或车牌号码识别；专业任务必须使用相应 Provider、Profile、阈值和独立评测。
- **不**默认开启人脸或步态身份检索，不建立无授权的全局人员库，不提供绕过权限和审计的批量生物特征导出；人脸 Top-1 只表示候选，不自动触发高风险身份处置。
- **不**把 Person ReID、步态识别、姿态估计和动作识别混成同一向量或同一准确率指标，也不声称单张静态图片可以可靠完成步态身份识别。
- **不**用向量近似查询替代标准化车牌号的精确匹配；模糊车牌查询必须显式启用并返回候选解释。
- **不**借“多模态”扩张为分布式向量集群、媒体资产管理平台或视频处理平台；SonnetDB 仍是单机、本地优先的数据引擎。

---

## Milestone 17 — 可观测性与运行时可见性（Observability & Runtime Visibility）

> **目标**：把 SonnetDB 从「能跑起来」推进到「生产可运维」。统一指标 / 追踪 / 日志三大支柱，把当前散落在写入路径、Compaction、查询引擎、Copilot Agent 内部的状态以**标准化**形式暴露给运维与用户。
>
> **不变约束**：
> - **零运行时第三方依赖原则不变**：`SonnetDB.Core` 仅依赖 `System.Diagnostics.DiagnosticSource`（BCL 内置 Activity / Meter API），不引入 OpenTelemetry SDK。
> - `SonnetDB.Server`（HTTP / Web Admin / Copilot 宿主）允许引入 `OpenTelemetry`、`OpenTelemetry.Extensions.Hosting`、`OpenTelemetry.Exporter.Prometheus.AspNetCore`、`OpenTelemetry.Instrumentation.AspNetCore`、`OpenTelemetry.Instrumentation.Http`，因为该程序集本身已经依赖 ASP.NET Core。
> - 不破坏二进制格式（`FileHeader.Version` 不变）。
> - 默认开启基本指标 / 追踪；Prometheus 端点、Slow Query Log、Diagnostic Dump 默认关闭，需在 `appsettings.json` 显式开启。
> - 所有新端点遵守现有 Bearer + 三角色权限模型。
>
> **优先级调整（2026-07-04）**：**#89 ~ #91 前置于 M28 P5b #235 之前落地**——#89（Core Meter / ActivitySource 基线，纯 BCL 零依赖）插桩的正是 P5b 帧接入层将要施压的路径（`Tsdb.Insert` / `WriteMany` / WAL fsync / `QueryEngine.Execute`），#90/#91（Server OTel 引导 + Prometheus 端点/监控面板）紧随其后，让全模型高吞吐接入层**从第一天起就有生产级指标可观测**（#230 基准只覆盖 benchmark 环境，不解决线上可见性）；M29 #253 的 MQ 吞吐/积压曲线也显式依赖 M17 metrics。**#92 ~ #98 不阻塞 P5b**，按原顺序随后推进。

### PR 拆分

| PR | 标题与范围 | 状态 |
|----|------------|------|
| #89 | **M17.1：Core 端 Meter / ActivitySource 基线**：在 `SonnetDB.Core` 新增 `SonnetDB.Diagnostics` 命名空间，引入静态 `SonnetDbMeter`（`Meter("SonnetDB.Core", "1.0.0")`）与 `SonnetDbActivitySource`（`ActivitySource("SonnetDB.Core")`）。在写入路径（`Tsdb.Insert` / `BulkValuesParser` / `MemTable.Append`）、Flush / Compaction、Segment 读取、`QueryEngine.Execute`、WAL fsync 处插入 `Counter<long>` / `Histogram<double>` / `Activity?.Start()`，遵守 OTel 语义约定（`db.system=sonnetdb`、`db.operation`、`db.statement.kind`、`sonnetdb.segment.id`、`sonnetdb.measurement.name`）。**禁止引入 OpenTelemetry NuGet**，仅用 BCL `System.Diagnostics.Metrics`。 | ✅ |
| #90 | **M17.2：Server OpenTelemetry 引导**：在 `src/SonnetDB`（Server 入口）引入 `OpenTelemetry.Extensions.Hosting`，按官方推荐结构注册 `WithMetrics(b => b.AddMeter("SonnetDB.Core", "SonnetDB.Server").AddAspNetCoreInstrumentation().AddHttpClientInstrumentation())` 与 `WithTracing(b => b.AddSource("SonnetDB.Core", "SonnetDB.Copilot").AddAspNetCoreInstrumentation())`。Resource attributes 自动包含 `service.name=sonnetdb`、`service.version`、`service.instance.id`、`host.name`。OTLP Exporter 走 `OTEL_EXPORTER_OTLP_ENDPOINT` 环境变量，默认不导出（Console exporter 仅在 `Development` 启用）。 | ✅ |
| #91 | **M17.3：Prometheus 端点 + Web 内嵌指标面板**：可选启用 `/metrics`（`OpenTelemetry.Exporter.Prometheus.AspNetCore`），用 `Observability:Prometheus:Enabled=true` 开关。Web Admin 新增「监控」侧边栏，使用 `fetch('/metrics')` 客户端解析 prom 文本，实时绘制：写入吞吐（`sonnetdb.write.points`）、查询 P95（histogram bucket 还原）、MemTable 大小、Segment 数、WAL 落盘延迟、Copilot 调用数 / token 总量。零图表第三方依赖，使用既有 `naive-ui` + 简易 SVG 折线（与现有 dashboard 风格一致）。 | ✅ |
| #92 | **M17.4：Copilot 指标与追踪**：`SonnetDB.Copilot` 命名空间下新增 `CopilotMeter`（`Meter("SonnetDB.Copilot")`）记录 `copilot.chat.requests`（按 model / mode tag）、`copilot.chat.duration`、`copilot.chat.tokens`（in/out）、`copilot.tool.calls`（按 tool name tag）、`copilot.knowledge.recall.hits` / `.misses`；Agent 每次 `PlanToolsAsync` / `RunToolAsync` / `GenerateAnswerAsync` 都开 `Activity` span，把 `tool.name`、`tool.arguments.length`、`tool.result.rows` 写到 tags。CopilotDock 与 AiSettingsView 增加「最近 1 小时调用 / token 用量」摘要卡片（消费 `/v1/copilot/metrics` 简化端点）。 | ✅ |
| #93 | **M17.5：结构化日志统一**：所有 `ILogger` 调用改用源生成日志（`[LoggerMessage]`），消除运行时 string interpolation 装箱。统一日志事件分类（Write / Query / Flush / Compaction / Wal / Copilot / Auth / Http）与 EventId 区段（1000~1999 写入；2000~2999 查询；…）。在 `Program.cs` 引入 `JsonConsoleFormatter`，生产模式默认输出 JSON 行（`logging.json`），开发模式保持单行简化格式。 | ✅ |
| #94 | **M17.6：Health / Readiness 端点扩展**：把现有 `/healthz` 拆为 `/healthz/live`（进程存活）与 `/healthz/ready`（细分 checks：`segment_store_writable`、`wal_writable`、`copilot_provider_reachable`、`copilot_embedding_provider_reachable`）。引入 `IHealthCheck` 接口的 SonnetDB 实现（无第三方依赖），结果以 ASP.NET Core HealthChecks 标准 JSON 输出。Web Admin 顶部状态条改为消费 `/healthz/ready`，单独显示 4 个 check 的颜色点。 | ✅ |
| #95 | **M17.7：Slow Query Log + Top-N 查询统计**：可选开关 `Observability:SlowQueryLog:Enabled=true` + `ThresholdMs=10000`，并支持 30s / 60s 分级。`QueryEngine.Execute` 完成后若超过阈值则发 `Activity.RecordException`-风格的结构化日志事件，并写入内存环形缓冲（`SonnetDB.Diagnostics.SlowQueryRing` 默认 256 条）。新增 `GET /v1/diagnostics/slow-queries` 与 `GET /v1/diagnostics/top-queries`（按归一化 SQL 指纹聚合 count / p50 / p95 / max）。Web Admin SQL Console 旁边新增「慢查询」抽屉。 | ✅ |
| #96 | **M17.8：Diagnostic Dump 端点**：新增 `GET /v1/diagnostics/dump`（仅 admin token）返回 JSON 快照：进程 GC（`GC.GetGCMemoryInfo()` / `GC.GetTotalMemory(false)`）、ThreadPool（`ThreadPool.GetAvailableThreads`）、SonnetDB 内部计数（每 db 的 MemTable 大小 / Segment 数 / 待 Compaction 任务 / WAL 文件列表 / Copilot 在飞会话数）。**禁止 dump 用户数据点本身**，仅 metadata。CLI 新增 `sndb diag dump` 命令直接调该端点，便于复现性能问题时一键采集。 | ✅ |
| #97 | **M17.9：Copilot 服务端会话持久化（M16 M5 二阶段）**：在 `__copilot__` 系统库新增 `conversations`、`messages` 与 `usage_events` 三张关系系统表；新增 `GET/POST/DELETE /v1/copilot/conversations[/{id}]`、`GET /v1/copilot/conversations/{id}/messages` 与 `GET /v1/copilot/metrics`。CopilotDock 从服务端加载历史，认证用户按用户名隔离，静态 Token 按不可逆哈希隔离；不再使用浏览器 `localStorage` 回退。会话、消息、引用与用量支持重启恢复和跨设备同步。 | ✅ |
| #98 | **M17.10：CHANGELOG / docs / OTel 端到端验证**：补 `docs/observability.md`（指标列表、追踪 span 树、health checks 含义、prom scrape 配置示例、`OTEL_EXPORTER_OTLP_ENDPOINT` 与本地 Aspire Dashboard 联调）；补 `docs/troubleshooting.md`（常见慢查询模式 + diagnostic dump 解读）；补 docker-compose 示例追加可选 `otel-collector` + `prometheus` + `grafana` 三服务（`profile: observability`，默认不启动）；端到端验证：嵌入式启动 → 触发写入 / 查询 / Copilot 调用 → 在 Aspire Dashboard 看到完整 trace（HTTP → SQL → Segment 读取 → Copilot Agent → tool 调用）。 | ✅ |

### 推进顺序

```
第一波（前置于 M28 P5b #235，见上方优先级调整）：
PR #89（Core Meter / Activity 基线）
  → #90（Server OTel 引导）
  → #91（Prometheus + Web 监控面板）

第二波（不阻塞 P5b，按带宽推进）：
#92（Copilot 指标 / 追踪）
  → #93（结构化日志）
  → #94（Health 拆分）
  → #95（Slow Query Log / Top-N）
  → #96（Diagnostic Dump）
  → #97（Copilot 会话服务端持久化）
  → #98（文档 / docker-compose / 端到端联调）
```

### 可观测性与 Copilot 下一步规划（整合）

> 第一波（#89~#91，Core Meter/Activity 基线 + Server OTel 引导 + Prometheus 端点/监控面板）已 ✅ 落地。下面把「可观测性第二波」与散落在 M17/M27 的 Copilot 线索整合成一条推进路径。

**可观测性第二波顺序（#92 → #98，各项解锁什么）**：

- **#92 Copilot 指标 / 追踪** —— `copilot.chat.*` / `copilot.tool.*` / `copilot.knowledge.recall.*` 指标 + Agent span。**优先级最高**：M29 #253 的 MQ 吞吐 / 积压曲线与 Copilot 用量卡片都显式依赖它。
- **#93 结构化日志（已完成）** —— `[LoggerMessage]` 源生成 + JSON 行 + EventId 分区，消除装箱、便于集中式日志检索。
- **#94 Health Live/Ready 拆分** —— `/healthz/live` vs `/healthz/ready`（segment / wal / copilot provider 细分 check），供编排探活。
- **#95 慢查询 Log + Top-N 统计** —— 归一化 SQL 指纹聚合 p50/p95/max，Web Admin 慢查询抽屉。
- **#96 诊断 Dump 端点（已完成）** —— GC / ThreadPool / 每 db 内部计数快照（仅 metadata，不含用户数据点），CLI 一键采集。
- **#97 Copilot 服务端会话持久化（已完成）** —— `__copilot__` 系统库存会话 / 消息 / 用量事实，按认证 owner 跨设备同步，不再回落 localStorage。
- **#98 文档 + docker-compose + 端到端联调** —— `docs/observability.md`、Aspire Dashboard / OTLP Collector 全链路 trace 验证。

**Copilot 线索整合（M17 + M27，M28 收官后阻塞解除）**：

- **M17 侧**：#92（指标）+ #97（会话服务端持久化、跨设备同步）。
- **M27 侧（M28 已收官、依赖解除）**：#184 端到端工业异常 Demo（P5 MQTT 内建 broker + P0/P2 可靠写入已就绪，**现可启动**）、#187 eval 与成本指标（provider / model / tool 调用数 / 失败原因 / 近似 token 成本；仍建议推迟到有真实采纳之后）；#183（MCP 工具契约文档化）/ #185（provider-neutral 配置样例）纯文档，随时可做。

**完成状态**：#89~#98 已全部落地；后续观测能力按实际生产反馈拆分新条目，不再扩展 M17 范围。

**前置依赖**：Milestone 16 已合并。本 Milestone 不破坏 SonnetDB Core 二进制格式，对 `__copilot__` 系统库新增 measurement 走现有 schema 升级路径（`SeriesCatalog` 自动 upsert）。**Core 仍坚持零第三方运行时依赖**，OpenTelemetry SDK 只允许出现在 `src/SonnetDB`（Server 程序集）的 `csproj`。

**验收标准**：
- 嵌入式 + 服务器两种启动方式下 `dotnet-counters monitor SonnetDB.Core` 可立即看到核心指标；
- 启用 Prometheus 端点后 `curl /metrics` 可被标准 prom scraper 采集，关键 metric 含语义化 tag；
- Web Admin 监控面板在不依赖外部图表库的情况下展示写入吞吐 / 查询 P95 / Copilot token；
- 慢查询日志可在 `/v1/diagnostics/slow-queries` 看到归一化 SQL 指纹与时延分布；
- Diagnostic Dump 在 admin token 下返回完整 JSON，匿名访问 401；
- Copilot 会话历史登录态走服务端，匿名态回落 `localStorage`，切换设备能拉到自己的历史；
- 端到端：通过 Aspire Dashboard 或 OTLP Collector 能看到一次 HTTP → Tsdb 写入 → WAL fsync 的完整 span 树。

---

## Milestone 27 — Industrial Data Agent 与 AI-ready 产品化路线

> **当前状态**：✅ M28 已收官（P0~P5 全部完成），M27 两拨阻塞解除、可全面推进。#182 门面文档已基本落地（`llms.txt`、`docs/industrial-ai-applications.md`、README 第一屏定位均已就位），且 #183 想要的 MCP 工具契约（`list_databases` / `list_measurements` / `describe_measurement` / `sample_rows` / `query_sql` / `explain_sql` / `docs_search`）与 #185 想要的 provider 抽象（`OpenAICompatibleChatProvider` + `IChatProvider` / `IEmbeddingProvider`）**代码已实现**。因此 M27 剩余工作**不是「建 AI 功能」，而是「打包、定位、证明、去重」**。
>
> **排序原则（关键纠偏）**：M27 的产品主张是「可靠的工业本地引擎 + 可信 Agent」，而该主张的「可靠」部分要靠 **Milestone 28** 为真——M28 审计发现 Windows 默认配置下的真实丢数据缺陷，且 P4（索引/向量）、P5（MQ/接入）尚未完成。**在引擎可靠性做真之前做 Demo / eval，等于给一个仍会丢数据的引擎拍宣传片。** 故 M27 拆成两拨：
>
> - **可与 M28 并行的纯文档条**（零引擎风险、是采纳门槛）：#182 收尾、#185 provider-neutral 配置文档、#183 降级为「稳定 + 文档化现有 MCP 工具」（**不新增 Agent 工具**）、#188 边界声明。
> - **M28 已收官、阻塞解除**：#184 端到端工业异常 Demo（P5 MQTT 接入 + 可靠写入已就绪，**可启动**）；#187 eval 与成本指标仍建议推迟到有真实采纳之后。
> - **移交去重**：#186 写入审批二阶段与 **Milestone 29** 的「共享写审批框架」重叠，归属 M29（审批是管理面能力），M27 只消费不重复实现。

> **目标**：把 SonnetDB 的对外门面从“多模型数据库”收敛为“面向 .NET 工业边缘应用的本地优先数据引擎”，并把 Copilot 从通用 SQL 助手推进到可被生产场景理解、演示和集成的 **Industrial Data Agent**。本里程碑优先做产品定位、AI-ready 文档、工业 Demo 和 provider-neutral 能力，**不新增引擎语义、不扩张 Agent 工具面、不改动核心二进制格式**。

> **边界**：
> 1. 多模型能力仍然保留，但作为能力矩阵描述，不再作为 README 第一屏的唯一定位。
> 2. Copilot / Agent 的第一责任是读取 schema、生成 SonnetDB 方言 SQL、执行只读分析、解释结果和请求写入审批；不绕过现有权限模型。
> 3. AI provider 必须走抽象层，不把 SonnetDB 绑定到 GPT、Claude、Gemini、DeepSeek、Qwen、Ollama 或任一单一供应商。
> 4. 工业 Demo 以 MQTT / HTTP ingest、设备异常、维修建议和上层平台集成为主，不把 SonnetDB 宣传为分布式云 TSDB 或大型集群平台；IoTSharp 联合样例归 IoTSharp 仓库维护。
> 5. **不新增 Agent 工具即可满足 #183**：现有 MCP 工具面已覆盖 list/describe/sample/query/explain/docs_search，M27 只稳定命名与参数并文档化，不铺大 Agent 表面。写入审批走 M29 框架，不在 M27 重复。

### PR 拆分

| PR | 主题 | 状态 |
|----|------|------|
| #182 | **AI-ready 门面文档第一批**：README / README.en 第一屏改为 `.NET industrial edge local-first data engine`；新增 `llms.txt`、`docs/industrial-ai-applications.md`，让开发者和 AI Agent 明确 SonnetDB 适合工业边缘、IoT telemetry、本地数据引擎、Copilot / MCP 场景。 | 🚧（可与 M28 并行收尾） |
| #183 | **稳定并文档化现有 MCP / Copilot 工具契约（降级：不新增工具）**：`list_databases` / `list_measurements` / `describe_measurement` / `sample_rows` / `query_sql` / `explain_sql` / `docs_search` **已在 `src/SonnetDB/Mcp/SonnetDbMcpTools.cs` 实现**；本 PR 只稳定命名、参数与权限边界并形成 typed contract 文档，**不铺大 Agent 表面、不新增专用端点**。异常分析优先复用已有 Core 算子 `anomaly(field,'zscore'/'mad'/'iqr',threshold)`（`AnomalyFunctions.cs`），仅在文档中给出「异常设备」查询范式，`analyze_measurement_anomaly` 单独工具**暂不新增**。 | 📋（可与 M28 并行，纯文档） |
| #184 | **工业异常分析 Demo（等 M28 收口后启动）**：新增 MQTT / HTTP ingest 示例，演示设备温度 / 电流 / 振动写入 SonnetDB，再通过 Copilot / MCP 提问“哪台设备今天最异常？”并生成报告；README、docs 和视频脚本统一使用同一数据模型。**依赖**：M28 P5b #242 MQTT 内建 broker + P0/P2 可靠写入（引擎主张为真后再拍 Demo）。 | 📋（M28 已收官，阻塞解除，可启动） |
| #185 | **Provider-neutral Copilot 配置回归**：`OpenAICompatibleChatProvider` + `IChatProvider` / `IEmbeddingProvider` 抽象**已实现**；本 PR 把 Chat / Embedding provider 抽象文档化并补齐 OpenAI-compatible、Azure OpenAI、国内兼容网关、本地 Ollama / vLLM 的配置样例；Web Admin 模型选择器明确区分“平台默认模型”“自定义模型”“本地模型”。 | ✅ |
| #186 | **写入审批二阶段 → 移交 Milestone 29**：与 M29「共享写审批框架」重叠，归属 M29（审批是管理面能力）。M27 只消费该框架、不重复实现。原范围（Copilot 写 SQL 进入 staged preview、`CREATE / INSERT / UPDATE / DELETE / DROP / GRANT / REVOKE` 展示 SQL diff / 影响范围 / 二次确认、服务端以权限和 `mode=read-write` 为上限）在 M29 交付。 | ➡️ 移交 M29 |
| #187 | **Agent eval 与成本指标（推迟到有真实采纳之后）**：新增 Industrial Data Agent eval 场景（异常设备、慢查询、schema 建模、维修建议、写入审批），并在 Copilot 指标中记录 provider、model、tool 调用数、失败原因和近似 token 成本，便于企业按成本选择模型。**排序**：无真实用户前做 eval 收益低，排在 #184 Demo 与首批采纳之后。 | 📋（M28 已收官；仍排在 #184 Demo 与首批采纳之后） |
| #188 | **上层平台联合样例边界**：SonnetDB 侧只提供工业边缘数据引擎、Studio、Copilot/Agent 和备份恢复的通用样例素材；具体 IoTSharp + SonnetDB 边缘节点样例迁入 IoTSharp 仓库 RD-10 维护。 | 📋（纯边界声明，可随时做） |

### 验收标准

- README 第一屏、docs 首页、`llms.txt` 和工业 AI 文档对 SonnetDB 的第一定位保持一致。
- AI / Agent 能从 `llms.txt` 找到 SQL 参考、工业应用文档、Studio / Copilot 文档和 Roadmap。
- 现有 MCP 工具（list/describe/sample/query/explain/docs_search）命名、参数、权限边界已文档化为稳定 typed contract，**未新增 Agent 工具面**。
- Provider 文档必须说明 OpenAI-compatible 抽象、本地模型路线和不绑定单一供应商的原则。
- Industrial Data Agent Demo 可以从样例数据跑到自然语言分析结果，且所有写操作都走 **M29 写审批框架**；**该验收项在 M28 P5（MQTT 接入）与 P0（可靠写入）收口后才启动**。
- 本里程碑不修改 `.SDBSEG` / `.SDBWAL` / KV / Document 等二进制格式，**不新增引擎语义、不扩张 Agent 工具面**。

---

## Milestone 18 — VS Code 数据库扩展（SonnetDB for VS Code）

> **背景**：当前 SonnetDB 已经具备 VS Code 扩展所需的大部分服务端能力：`GET /v1/db` 数据库列表、`GET /v1/db/{db}/schema` schema 快照、`POST /v1/db/{db}/sql` ndjson 查询、三条 bulk ingest 端点、`POST /v1/copilot/chat/stream` 流式 Copilot，以及 `/mcp/{db}` 只读 MCP 工具集。与其再发明一套编辑器协议，不如直接把这些现成 contract 包装成 VS Code 原生体验。
>
> **核心策略**：
> 1. **Remote-first**：第一版优先连接远程 SonnetDB Server，复用现有 HTTP contract；不在首版把 `SonnetDB.Data` / `Tsdb` 直接嵌入 Node 扩展宿主。
> 2. **托管本地模式**：后续本地目录支持走“扩展帮用户启动一个指向指定 `data root` 的 SonnetDB Server”方案，再通过同一套 HTTP client 连接，避免 Node ↔ .NET 直连复杂度。
> 3. **TypeScript-first**：扩展主体用 TypeScript 实现，目录位于 `extensions/sonnetdb-vscode/`；后续若要复用 C# `SqlParser` 做 diagnostics，再以 sidecar / LSP 形式接入。
> 4. **安全默认值**：token 存放在 VS Code `SecretStorage`；Copilot 默认 `read-only`，切换到 `read-write` 需要显式确认。
> 5. **复用现有前端经验**：直接吸收 `web/` 中现有的 ndjson 解析、schema 自动补全、SonnetDB SQL 方言、结果图表和 Copilot 请求模型，避免重复造轮子。

### PR 拆分

| PR | 主题 | 状态 |
|----|------|------|
| #99 | **扩展骨架 + Manifest + Activity Bar 容器**：在 `extensions/sonnetdb-vscode/` 建立 `package.json` / `tsconfig.json` / `src/` / `media/` 结构；注册 `SonnetDB` Activity Bar、基础命令（Add Connection / Refresh / Run Query / Open Copilot / Start Managed Local Server）与 TreeView 骨架。 | ✅ |
| #100 | **远程连接模型 + SecretStorage**：实现连接配置模型（`remote` / `managed-local`）、`SecretStorage` token 持久化、活动连接选择、`/healthz` 探活、`/v1/setup/status` 首次安装探测；连接面板支持测试连通性与提示未初始化状态。 | ✅ |
| #101 | **Explorer 树：Connections → Databases → Measurements → Columns**：消费 `GET /v1/db` 与 `GET /v1/db/{db}/schema`，展示数据库 / measurement / 列结构；支持刷新 schema、复制 measurement 名与 open in query runner。 | ✅ |
| #102 | **SQL 执行链路 + SonnetDB 方言补全**：实现 `POST /v1/db/{db}/sql` ndjson 解析、Run Current Statement / Run Selection 命令，并复用 Web Admin 方言思路提供关键词与 schema 补全。 | ✅ |
| #103 | **结果面板：Table / Raw / Chart 三视图**：Query Result Webview 支持表格、原始 JSON、多数值时间序列图表、query history 与 CSV/JSON 导出。 | ✅ |
| #104 | **VS Code 内置 Copilot 面板**：接入流式聊天、模型与知识库状态；支持默认只读、显式读写确认、引用显示和当前 SQL 发送。 | ✅ |
| #105 | **托管本地 SonnetDB Server 模式**：选择 `data root` 后启动 / 关闭本地 Server，处理端口占用、日志输出与健康检查；本地与远程共用 HTTP client 与 Explorer。 | ✅ |
| #106 | **生产力增强**：Create Measurement 草稿、带确认的 bulk import（LP / JSON / Bulk VALUES）、starter snippets、help / docs / explain 入口。 | ✅ |
| #107 | **Language Service / LSP Sidecar**：已提供 TypeScript 轻量 diagnostics、hover、schema completion 与 explain；复用 C# `SqlParser` 的打包 sidecar 与自动降级已实现，signature help、repair suggestion 和标准 LSP framing 尚未实现。 | 🚧 |
| #108 | **打包发布 + CI + 文档**：已补测试、VSIX 打包、CI artifact、Marketplace metadata 与安装/权限/本地模式文档；Marketplace 正式发布与 Electron 实机截图尚未完成。 | 🚧 |

### 首批实现建议

第一批建议先做 `#99 ~ #103`，把“能连、能看、能查、能画”闭环跑通：

```
#99（骨架）
  → #100（连接 + SecretStorage）
    → #101（Explorer）
      → #102（执行 SQL）
        → #103（结果三视图）
```

`#104`（Copilot 面板）可以在查询闭环后立即接入；`#105`（托管本地模式）可与 `#104` 并行，但不应阻塞首个可用版本。

> **与 Milestone 29 的关系**：本里程碑保留 VS Code 扩展交付主线（#99~#108）。Milestone 29 的 #259 已在 A/B/C 工作台契约（#245）落地后，补完本里程碑 #103 结果三视图 + #104 Copilot 面板的核心接线，并把 Explorer 扩展为消费 M29 契约做 KV / 向量 / 全文 / MQ **只读浏览**；VS Code 定位开发者只读 + SQL 执行子集，完整 per-model 编辑体验以 Web Admin 旗舰为准。管理界面的跨里程碑归口见 Milestone 29「管理界面归口」表（VS Code 管理 UI 由 M29 #259 补完）。

### 目录约定

```text
extensions/
  sonnetdb-vscode/
    README.md
    ROADMAP.md
    package.json
    docs/
      architecture.md
      api-contract.md
    src/
      extension.ts
      commands/
      core/
      tree/
      panels/
      lsp/
```

### 验收标准

- 用户可在 VS Code 中保存至少一个 SonnetDB 连接，token 不落到明文 `settings.json`；
- Explorer 能展示数据库、measurement 与列信息，并可手动刷新；
- 编辑器可执行当前 SQL，结果在独立面板中查看；
- 结果面板至少支持 Table / Raw / Chart 三视图；
- Copilot 面板默认只读，切换读写前有显式确认；
- 本地模式不要求首版完成，但架构上已经明确走“托管本地 Server”路线，而非 Node 直嵌引擎。

**前置依赖**：无新的 Core 二进制格式变更；Milestone 18 第一阶段主要依赖现有 `src/SonnetDB` HTTP API 与 `web/` 中可复用的客户端逻辑。当前仓库已新增 `extensions/sonnetdb-vscode/` 目录，用于承载扩展骨架与后续实现。

---

## Milestone 19 — 生态适配底座能力（关系 + KV/缓存 + 对象桶 + 大量 measurement）

> **目标**：为上层平台和应用提供通用数据库能力，而不是在 SonnetDB 仓库规划某个上层项目的迁移、灰度、双写或回滚流程。IoTSharp 如何使用 SonnetDB 已迁入 IoTSharp 仓库 `ROADMAP.md` 的 RD-10；本仓库仅保留 SonnetDB 自身需要交付的通用能力。
>
> **推进原则**：
> - 不把 SonnetDB 当前 table MVP 直接包装成“完整关系库”；先补 ADO.NET、SQL、事务、迁移和查询翻译硬能力。
> - 不把普通 KV keyspace 直接冒充 Redis；先补 TTL、过期清理、并发语义和缓存 Provider。
> - 对象桶能力以 SonnetDB 通用 object storage API 为边界；上层项目的 BlobStorage/S3 接入和回滚策略由上层项目维护。
> - 大量 measurement、文件布局、compaction 恢复、增量索引和长稳专项属于 SonnetDB 通用能力，继续保留在本仓库。

### PR 拆分

| PR | 主题 | 状态 |
|----|------|------|
| #109 | **生态兼容边界与能力基线**：梳理 SonnetDB 作为关系、时序、KV/缓存、对象桶、向量搜索与全文搜索能力底座时需要承诺的通用 API、测试域和不支持清单；具体上层项目的兼容矩阵迁出到对应项目仓库维护。 | ✅ |
| #110 | **ADO.NET 事务与异步 API**：实现 `SndbTransaction`，把 SQL 层 `BEGIN/COMMIT/ROLLBACK` 接入 `DbConnection.BeginDbTransaction` / `DbCommand.Transaction`；补 `OpenAsync`、`ExecuteReaderAsync`、`ExecuteNonQueryAsync`、`ExecuteScalarAsync`、取消令牌和远程 `/sql/batch` 事务语义。第一阶段允许单表轻事务，测试明确拒绝跨表事务。 | ✅ |
| #111 | **关系表 DDL 与 schema metadata 扩展**：补 `ALTER TABLE ADD/DROP/RENAME COLUMN`、`RENAME TABLE`、默认值、nullable 变更、索引重命名、`INFORMATION_SCHEMA` / `GetSchemaTable` / provider manifest metadata；为 EF Core migrations 生成器提供稳定数据库能力描述。当前已落 `ALTER TABLE ADD/DROP/RENAME COLUMN`、`ALTER TABLE RENAME TO`、`INFORMATION_SCHEMA.tables/columns/indexes`、`DbDataReader.GetSchemaTable()` 与 `DbConnection.GetSchema()` provider metadata 基线；首版明确拒绝主键列变更、被索引列删除和缺省值不足的 NOT NULL 新列。 | ✅ |
| #112 | **关系查询能力补齐一：表表 JOIN / 子查询 / 聚合**：在 table executor 增加 table-table inner join、基础 left join、`COUNT/SUM/MIN/MAX/AVG`、`GROUP BY column`、`HAVING`、`IN`、`EXISTS`、简单子查询；覆盖 ORM 常见 `Include`、权限过滤、分页统计可翻译的通用查询形态。当前已落表表连续 `INNER JOIN`、派生表、WHERE 标量子查询和基础 GROUP BY 聚合；outer join / HAVING / IN / EXISTS 留后续 provider 兼容压测补齐。 | ✅ |
| #113 | **关系事务能力补齐二：跨表小事务与约束**：实现同一数据库内多表 DML 的原子提交与回滚；补唯一约束、外键约束的第一版校验策略、乐观并发列、并发冲突错误码；明确隔离级别边界。 | ✅ |
| #114 | **SonnetDB.EntityFrameworkCore Provider MVP**：新增 EF Core provider 包，包含 `UseSonnetDB(...)`、SQL generator、type mapping、migrations SQL generator、query translation 基础能力；先通过 provider 自测与最小 `DbContext` CRUD、Identity 子集、迁移创建/回滚测试。 | ✅ |
| #115 | **EF migrations history 与典型 ApplicationDbContext 兼容基线**：补齐 SonnetDB EF provider 的 migrations history 支持（`__EFMigrationsHistory` 或等价可配置历史表），让 `Database.Migrate()`、迁移升级、回滚、重复执行幂等检查和空库初始化成为 provider 入口验收；典型 ASP.NET Core Identity / ApplicationDbContext 兼容样例只作为 provider 通用测试，不承载上层项目路线图。 | ✅ |
| #116 | **KV TTL 与缓存 Provider**：在 KV keyspace 增加 expires-at metadata、惰性过期 + 后台清理、命名空间、批量 get/set/remove、前缀删除和过期统计；新增 EasyCaching provider 与可选 `IDistributedCache` provider。 | ✅ |
| #117 | **对象桶 API 第一版**：新增 bucket/object metadata 表、multipart upload 会话、etag/sha256、range read、copy object、delete marker、object tags、presigned URL；HTTP API 覆盖通用对象存储常用子集。 | ✅ |
| #118 | **对象生命周期、版本、审计与配额**：已落 bucket policy 持久化占位、retention/lifecycle 执行、object versioning、legal hold、访问审计、容量统计和 quota 强制；远程/嵌入式客户端与 Server 回归覆盖配额超限、保留阻断、legal hold、生命周期和审计。**UI 已收编进 M29 #256 统一对象工作台。** | ✅ |
| #119 | **生态接入样例与 Profile 文档边界**：新增可编译运行的 `samples/SonnetDB.EcosystemSample`，同一连接字符串覆盖嵌入式/远程、ADO.NET、EF Core、`IDistributedCache` 和对象桶；`docs/ecosystem-integration.md` 明确具体 Profile、灰度、双写、回滚和生产验收由上层仓库维护。 | ✅ |
| #120 | **通用迁移与校验原语**：新增 `MigrationService` 的 export、scan、checksum、import dry-run/import 组合门面，复用一致性 checkpoint、manifest、逐文件 SHA-256、包级稳定摘要和恢复目录安全检查；多模型测试覆盖关系、时序、KV、对象桶恢复及篡改拒绝，不引入上层产品专用命令。 | ✅ |
| #121 | **通用长稳、压测和故障恢复报告**：新增 `tests/SonnetDB.EcosystemSoak` quick/ci/soak 三档 runner，覆盖 EF Core provider、批量/大量 measurement、KV TTL、对象 multipart、迁移校验、快照回滚、真子进程强杀和 torn WAL 掉电恢复，输出 JSON/Markdown 并由每周/手动 workflow 归档；上层 Profile 报告继续由上层项目维护。 | ✅ |
| #122 | **大量物理分表文件布局与启动扫描优化**：面向大量 measurement / 大量 segment 场景，设计并实现分层 segment 目录布局（例如按 segmentId 前缀或时间桶拆分）、目录枚举兼容层、备份扫描优化、旧段清理策略和布局迁移工具；保留旧 `segments/{id}.SDBSEG` 读取兼容。 | ✅ |
| #123 | **Compaction manifest 与重复段恢复**：为 compaction 引入 manifest 或等价 superseded segment 状态，记录 source segments、target segment、提交阶段和清理阶段；启动时根据 manifest 忽略或清理被替代旧段，解决 crash after swap before delete 后新旧段同时加载导致重复数据的问题。 | ✅ |
| #124 | **SegmentManager 增量索引与后台维护成本控制**：#207 已落每段索引缓存，本轮进一步修复一换一 swap 遗留旧索引、用有序 reader 集合把段发布从 O(N log N) 降为 O(N)、让纯 MemTable 发布真实复用 O(1) reader/index 快照；新增 16/256/1024 段、0/4 个并发 QueryEngine worker 下 add/swap/drop 与旧全量 build 参考基准。更复杂分层索引只在基准证明 O(N) 发布仍超预算时继续。详见 [复核报告](docs/benchmarks/m19-optimization-reassessment.md)。 | ✅ |
| #125 | **大量 measurement / 长稳专项扩展**：继续扩展 `SonnetDB.EcosystemSoak`，新增 high-cardinality、small-segments、maintenance-chaos、many-measurements 四个正交 profile；覆盖百万级 series、万级小 segment、确定性随机 kill/reopen、后台 flush/compaction/retention、目录/备份/drop，并输出缺失/重复/额外点/值摘要、working set/托管内存峰值、查询/恢复 P50/P95/P99 与显式容量边界。workflow 支持四档手动归档，默认容量档须在目标硬件生成发布证据。 | ✅ |
| #126 | **SQL 正则契约治理与 EF 翻译**：统一 SQL/Document Validator matcher，固定 250ms timeout、4096 pattern 字符、1Mi input 字符和 128 项有界缓存；交付 `regexp_like(input, pattern[, flags])`、`REGEXP`/`RLIKE` 别名、EXPLAIN `scan_filter`、跨模型回归和 EF `Regex.IsMatch` 翻译。`regexp_substr/replace/instr` 与 `^literal` 前缀剪枝按原边界后置。 | ✅ |
| #126.1 | **关系表大批量删除与后台回收**：复用 KV tombstone，交付带 commit record 的分块 batch delete（运行时/恢复原子）、table/keyspace generation、`TRUNCATE TABLE` 与 `DELETE WHERE TRUE` 快路、v4 state generation、可恢复 cleanup manifest、重启后未打开实例发现、state 文件删除白名单、文件预算与查询/flush/CPU/内存压力节流、pending/rate/reason/error 任务观测和四路径基准/smoke；入站外键保持拒绝或回退既有级联语义。 | ✅ |

### 推进顺序

```
#109（生态能力边界）
  → #110（ADO.NET 事务 / async）
  → #111（DDL / schema metadata）
  → #112（查询能力）
  → #113（跨表事务 / 约束）
  → #114（EF Core provider MVP）
  → #115（EF migrations history / 典型 ApplicationDbContext 兼容）
  → #116（KV TTL / 缓存 Provider）
  → #117（S3 API）
  → #118（对象治理）
  → #119（生态接入样例 / Profile 文档边界）
  → #120（通用迁移与校验原语）
  → #121（通用长稳 / 压测 / 报告）
  → #122（大量物理分表文件布局，已完成）
  → #123（Compaction manifest / 重复段恢复，已完成）
  → #124（增量索引 / 后台维护成本，已完成）
  → #125（扩展 #121 的大量 measurement / 长稳专项，已完成）
  → #126（正则契约治理 / EF 翻译，已完成）
  → #126.1（关系表大批量删除 / 后台回收，已完成）
```

### 验收标准

- SonnetDB ADO.NET、EF Core provider、KV/cache provider 和 object storage API 提供稳定的通用能力边界；
- EF Core provider 可通过典型 `ApplicationDbContext` 迁移历史表创建、迁移升级/回滚、重复迁移幂等检查、Identity 登录、主数据 CRUD 和核心查询；
- KV/cache provider 的 TTL 行为、批量操作、命名空间、过期清理和并发语义有独立测试；
- SonnetDB SQL 模式匹配能力必须覆盖 `LIKE`、`NOT LIKE`、`regexp_like` 在 `WHERE` 与 `SELECT` 中的行为，并明确正则超时、模式长度、编译缓存和 scan filter 边界；
- object storage API 覆盖上传、下载、删除、range read、multipart、presigned URL、版本、生命周期、审计和 quota 回归；Web Admin 由 M29 #256 统一对象工作台承接；
- 向量搜索可通过 `VECTOR(N)`、KNN、向量索引重建、topK/distance 校验和过滤组合回归；
- 全文搜索可通过全文索引创建/删除/展示、中文/英文查询、BM25 排序、分页和索引重建回归；
- 通用迁移与校验原语支持导出、导入、checksum、scan、backup/restore 组合；上层业务双写和回滚流程由上层项目维护；
- 长稳报告明确 SonnetDB 自身的适用规模、单机边界、边缘部署边界和仍建议使用外部专用组件的场景。
- 大量物理分表场景必须覆盖启动目录扫描、备份枚举、compaction 清理、retention 删除和单目录文件数量上限，不再只以功能测试证明可用。
- Compaction 恢复必须证明崩溃后不会重复加载 source + target 段；若选择保守恢复，也必须有明确的重复检测与修复流程。
- 关系表大批量删除必须覆盖 IoTSharp 设备重建场景：3000+ 设备、数万最新值和相关身份/属性数据的删除请求不得长时间占用前台 HTTP 请求；删除后查询可见性应立即符合语义，物理空间允许由后台清理逐步回收，并能通过指标看到待清理字节数、清理速率、节流原因和最近错误。
- 后台清理/收缩必须支持资源感知调度：在 CPU、IO、内存或活跃查询压力高时自动降速或暂停，在空闲窗口继续推进；崩溃或重启后能从 manifest/checkpoint 恢复，不重复删除、不破坏索引和统计。
- 当前不把 IoTSharp 每设备 measurement 改为共享 measurement + `deviceId` TAG 作为默认路线；SonnetDB 侧优化应优先兼容现有物理分表/多 measurement 模式。

---

## Milestone 24 — SonnetDB Studio 管理体验升级（Document 管理面）

> **目标**：把 Document Store 已经具备的集合、索引、validator、维护端点和导入导出能力组织成 SonnetDB Studio 里的可用管理体验。本里程碑只做 Studio / Web Admin / 桌面壳相关的管理面，不把新的 Document Store 引擎能力塞回 Milestone 21。
>
> **边界**：管理 UI 可以消费 Milestone 21 暴露的 HTTP API、schema endpoint、maintenance endpoint 和 Document API；若发现后端缺少必要只读 metadata，可以补最小 server contract，但不在本里程碑新增查询语义、索引语义或存储格式。
>
> **与 Milestone 29 的关系**：本里程碑（#170~#172）是**文档模型的专属管理面**，仍在本里程碑交付；Milestone 29（多模型统一管理工作台）的 #257 只负责把本里程碑的 Document Explorer / Validator / 导入导出**接入统一外壳与共享结果 / 写审批框架**，不重复实现文档管理能力。

### PR 拆分

| PR | 主题 | 状态 |
|----|------|------|
| #170 | **Studio Document Explorer**：新增 Document Explorer，支持数据库 / collection 列表、集合 schema、索引列表、JSON 查询编辑器、结果表 / JSON 双视图、分页浏览、文档详情与只读复制；复用现有 SonnetDB Studio 布局、权限模型和 `CopilotDock` 上下文。 | ✅ |
| #171 | **Studio Validator Governance**：把 Milestone 21 的 collection validator 暴露为 Studio 管理体验，支持查看 / 编辑 validator、切换 validation action（error / warn）、查看 schema evolution / 变更历史、预检样本文档和保存前 dry-run；所有写入操作走现有写审批模式。 | ✅ |
| #172 | **Studio Document 导入导出与维护操作**：支持 JSONL/NDJSON 导入导出、`_id` path 映射、dry-run、批量错误报告、进度显示、取消、索引 rebuild 触发与状态查看；危险维护动作需要二次确认并记录审计事件。 | ✅ |

> **#170~#172 落地说明**：M29 #257 的共享 Document Collection Workbench 已承接 Explorer、查询/分页/详情复制、Validator 编辑与样本预检、JSON/JSONL 导入导出和索引 rebuild；所有写入、Validator 与维护操作进入共享审批并记录工作台历史。Document 导入按 100 条分批，显示进度并支持停止后续批次，已成功批次不会回滚，批量警告/错误保留在操作结果中。Studio 桌面壳复用同一 Web 工作台。

### 验收标准

- Studio 能完成集合浏览、文档查询、文档详情查看、validator 管理、索引查看 / rebuild 和 JSONL/NDJSON 导入导出。
- Document Explorer 与 SQL Console、Schema Explorer、CopilotDock 的数据库选择和权限状态保持一致。
- 所有写入、导入、rebuild、validator 保存动作都有 preview / dry-run / confirm 中至少一种防误操作机制。
- 管理面缺少后端能力时，只补 metadata / maintenance contract，不把 Document Store 查询、索引、事务或存储能力混入本里程碑。

---

## Milestone 25 — Document Store 验收、文档与发布治理

> **目标**：在 Milestone 21 的能力闭环和 Milestone 24 的 Studio 管理面之后，再集中做 Document Store 的参考 parity、长稳、容量报告和对外文档。这里是发布治理阶段，不阻塞 Milestone 21 的能力交付。

### PR 拆分

| PR | 主题 | 状态 |
|----|------|------|
| #173 | **MongoDB 参考 parity 套件**：在 `tests/SonnetDB.Parity` 新增 `MongoAdapter`（官方 MongoDB .NET Driver 仅连接参考 MongoDB 容器）与 `DocumentAdapter`（SonnetDB 自有 API），覆盖 CRUD、filter、projection、sort、update operators、index unique/TTL、aggregation、并发写、崩溃恢复后的索引一致性；报告中明确“语义对齐，不做协议兼容”。 | ✅ |
| #174 | **Document 长稳、容量与发布文档**：百万 / 千万文档 profile 长测，输出写入、查询、索引 rebuild、TTL 清理、冷启动、备份恢复、内存占用报告；README / docs 新增 Document Store 能力矩阵、MongoDB-like 迁移指南、明确不支持项、推荐规模和 Studio 管理入口说明。 | ✅（runner + quick 基线；百万/千万报告按目标硬件发布门禁生成） |

### 验收标准

- `tests/SonnetDB.Parity` 的 MongoDB 参考文档场景全部 PASS 或结构化 SKIP，SKIP 必须带明确 `gap_reason`。
- 长稳报告覆盖热 / 冷启动、索引 rebuild、TTL 清理、backup/restore、崩溃恢复和内存曲线，性能数字进入报告但不做主 CI gating。
- 发布文档必须明确 SonnetDB Document Store 与 MongoDB 的差异、迁移边界、不支持项、推荐数据规模和不做协议兼容的原则。
- 文档更新放在发布治理阶段收尾，不反向扩大 Milestone 21 的能力范围。

---

## 已完成里程碑（摘要 + 归档指针）

> 以下里程碑已 100% 交付。详细 PR 拆分、缺陷附录与落地说明归档在 [docs/roadmap-history.md](docs/roadmap-history.md)，主路线图仅保留摘要。

### 早期路线 Milestone 0 ~ 16

> Milestone 0 ~ 16 的详细 PR 拆分、设计说明、SQL/API 示例与历史路线差异说明见 [docs/roadmap-history.md](docs/roadmap-history.md)。

| Milestone | 主题 | PR 范围 | 状态 |
|-----------|------|---------|------|
| 0 | 项目脚手架 | #1 ~ #3 | ✅ |
| 1 | 内存与二进制基础设施 | #4 ~ #6 | ✅ |
| 2 | 逻辑模型与目录 | #7 ~ #9 | ✅ |
| 3 | 写入路径 | #10 ~ #13 | ✅ |
| 4 | 查询路径 | #14 ~ #16 | ✅ |
| 5 | 稳定性与性能（写入侧） | #17 ~ #21 | ✅ |
| 6 | SQL 前端 + Tag 倒排索引 | #22 ~ #28 | ✅ |
| 7 | 压缩编码（Delta / Gorilla） | #29 ~ #31 | ✅ |
| 8 | 服务器模式（HTTP + 远端 ADO + 控制面 + Vue3 后台 + SSE） | #32 ~ #34c | ✅ |
| 9 | 性能基准与发布 | #35 ~ #39 | ✅ |
| 10 | 批量入库快路径（历史扩展占位已拆分） | #42 ~ #45 | ✅（#40 转入 Milestone 18，#41 并入 Milestone 28 P5b） |
| 11 | 写入快路径（PR #45 瓶颈收尾） | #46 ~ #49 | ✅ |
| 12 | 函数与算子扩展（PID / Forecast / UDF） | #50 ~ #57 | ✅ |
| 13 | 向量类型与嵌入式向量索引（Copilot 知识库底座） | #58 ~ #62 | ✅ |
| 14 | SonnetDB Copilot：MCP 工具 + 知识库 + 智能体 | #63 ~ #69 | ✅ |
| 15 | 地理空间类型与轨迹分析 | #70 ~ #77 | ✅ |
| 16 | Copilot 产品化升级（嵌入式 AI 助手 UX） | #78 ~ #88 | ✅ |

### Milestone 20 — 多模能力对齐与平移测试（Parity）

一份 docker-compose 同时拉起 SonnetDB 与开源全家桶（PostgreSQL / Redis / InfluxDB / VictoriaMetrics / MinIO / NATS / Mosquitto / Meilisearch / Qdrant / ClickHouse），用同一套 `IDataPlane` 适配器跑同一批场景，证明「一台 SonnetDB 在边缘 / 单机场景能替掉这一组组件」。不做协议兼容、不做替代主张；三类对齐 = 能力 / 可靠性 / 算法准确度；八大支柱 × ≥3 场景 = 24+ 场景红绿门槛 + nightly（#127~#136）。详细正文见 [docs/roadmap-history.md](docs/roadmap-history.md) 与 [docs/parity-roadmap.md](docs/parity-roadmap.md)。

### Milestone 21 — Document Store 单机能力升级（MongoDB-like，不做协议兼容）

把 KV-backed Documents 从「JSON 文档集合 MVP」升级到 MongoDB 单机常用能力子集：集合 CRUD、find filter/projection/sort、cursor 分页、局部更新操作符、复合 / unique / sparse / partial / TTL 索引、aggregation pipeline 子集、单文档原子性、批量写轻事务、validator 执行与磁盘有序容量底座（#137~#146）。不做 wire protocol / BSON / 官方 driver 直连；Studio 管理面归 M24、参考 parity 与长稳归 M25。详细正文见 [docs/roadmap-history.md](docs/roadmap-history.md)。

### Milestone 23 — 搜索与向量引擎合并（DotSearch / DotVector 收编）

> **状态**：已完成。详细路线、Phase 1~5 范围和验收记录见 [`docs/search-vector-engine-consolidation-roadmap.md`](docs/search-vector-engine-consolidation-roadmap.md)。本节保留为总览中的里程碑锚点，避免已完成的搜索 / 向量收编历史散落到其他规划章节。

### Milestone 26 — 连接器路线独立化（C ABI + 多模型 API）

C ABI 从「嵌入式示例」升级为独立产品路线：底座改走 `SonnetDB.Data`（`sonnetdb_open` 支持嵌入式 + 远程），按 bulk / KV / Document / Object / MQ 分组扩展 ABI 函数（#175~#181），Go / Rust / Java / Python 优先同步 bulk + KV + Document，VB6 / PureBasic 作源码级示例。不做 Redis / Mongo / S3 / Kafka / PG wire 兼容。详细正文见 [docs/roadmap-history.md](docs/roadmap-history.md)。

### Milestone 28 — 可靠性、并发正确性与热路径加固（Reliability / Concurrency / Performance Hardening）

2026 跨子系统深度审计（54 项缺陷 + P5 新增 MQ/N 专项）转成分阶段逐一交付的加固主线，遵循「先止血 → 再正确 → 再吞吐 → 再能力」。**全部收官**：

| 阶段 | 主题 | PR 范围 | 状态 |
|------|------|---------|------|
| P0 | 数据可靠性止血 | #189~#196 | ✅ |
| P1 | 正确性与稳定性 | #197~#203 | ✅ |
| P2 | 写路径吞吐 | #204~#211 | ✅ |
| P3 | 查询与 SQL 能力 | #212~#220 | ✅ |
| P4 | 索引与向量能力 | #221~#229 | ✅ |
| P5a | SonnetMQ 热路径硬化 | #230~#234 | ✅ |
| P5b | 全模型高吞吐接入（帧 over HTTP/2 + MQTT 双形态） | #235~#244 | ✅ |
| 补口 | SDK 时序写 / 对象客户端帧贯通 | #261~#262 | ✅ |

覆盖面：Windows 目录 fsync / flush 原子性 / 后台 worker 租约 / 段头尾 CRC / 默认持久性决策（P0）；SQL 三值逻辑 / count(\*) / 事务 / 解析递归上限（P1）；MemTable 双缓冲 / 增量段索引 / catalog 防抖（P2）；plan cache / 参数化 / hash join / 时序 WHERE / DISTINCT / 流式合并（P3）；文档惰性 scan / FTS 批量成段 / 向量度量贯通 / KNN skip-index / HNSW ef 补偿 / 文档持久 ANN（P4）；SonnetMQ 热路径硬化 + 自定义二进制帧 over HTTP/2 覆盖七 service + MQTT broker/client 双形态 + SDK 帧贯通（P5a/P5b/#261/#262）。**SonnetMQ 四个 🔴 全部关闭，引擎数据面不再携带 Critical 缺陷。** 详细 PR 拆分、54 项缺陷附录与 P5 MQ/N 专项见 [docs/roadmap-history.md](docs/roadmap-history.md)。

### MM9 — 多模型统一备份、恢复和管理工具第一批

`BackupService` + `sndb backup create/inspect/verify/restore`（开源核心第一批）；企业级定时、增量、审计与 UI 编排由 SonnetDBEE 承接。

---

## 性能优化待办（2026 审计后回收的中等优先项）

以下是一次完整审计后留下的纯性能优化点；功能上是对的，只是热路径里有可优化的常数因子或代数复杂度。2026-07-12 已按当前实现重新复核，完成状态以本表为准。

| 编号 | 位置 | 复核现状 | 建议改造 | 状态 |
|------|------|----------|----------|------|
| P1 | `src/SonnetDB.Core/Query/KnnExecutor.cs:129`、`src/SonnetDB.Core/Engine/TombstoneTable.cs:71` | M28 #208 已把 `IsCovered` / `GetForSeriesField` 改为 `Volatile` 发布的 per-key 不可变快照，读路径不再加锁或逐次 `ToArray()`；原审计缺陷已消除 | 原项关闭；高墓碑基数下仍可进一步合并区间、按查询时间窗判定是否需要放弃 ANN，并在距离计算前过滤墓碑点 | ✅ 已关闭（#208） |
| P2 | `src/SonnetDB.Core/Sql/Execution/RelationalSelectExecutor.cs` 子查询路径 | 单个 `SubqueryMemo` 已贯穿同一顶层 SELECT 的递归子查询、JOIN、WHERE、投影、聚合、排序和函数参数；相关子查询仍由运行时探针识别并逐行或逐候选执行 | 已增加四类表达式位置的非相关/相关回归与内部执行计数；10k 外层行和 10k JOIN 候选基准均确认非相关子树只执行 1 次 | ✅ 已完成 |
| P3 | `src/SonnetDB.Core/FullText/DocumentFullTextIndexStore.cs`、`src/SonnetDB.Core/FullText/Storage/PersistentFullTextIndex.cs` | 持久全文索引现按写入代次缓存仍有非 tombstone posting 的活跃 term；删除、批量删除和写入会使快照失效，同一次 fuzzy 查询一次取得全部字段快照并由所有 token 共享。Damerau-Levenshtein 改为三行滚动 DP，短 term 走栈缓冲、长 term 走 `ArrayPool<int>` | 100k term、90% tombstone、双字段双 token 基准为 23.96 ms / 549.15 KB；滚动 DP 前同场景为 27.25 ms / 20.68 MB，分配下降约 97.4%。活跃 term 与代次失效、历史 tombstoned-only term 排除、查询共享均有执行计数回归 | ✅ 已完成 |
| P4 | `src/SonnetDB.Core/Tables/TableManager.cs` | 级联展开每批只取一次 catalog 快照并建立 `principal -> FK` 反向元数据；完整 FK 列序存在二级索引时按父键调用 `GetByIndex`，否则每个 child/FK 只扫描一次并按父主键编码建立临时哈希桶 | 100k 子行、100 父键、CASCADE/SET NULL 四组合基准均用内部计数确认：无索引固定为 1 次扫描/100k 解码，有索引固定为 100 次查找/0 次回退扫描；多级、显式触及行、准备失败不提交及索引列序回退均有回归。全父键高选择率下单扫快于 100 次索引查找，批量自适应留作后续独立优化 | ✅ 已完成 |

### 2026-07-12 复核后的实施顺序与验收

1. **P2 已完成**：memo 生命周期已统一；SELECT 投影、ORDER BY、JOIN ON、函数参数均有非相关/相关子查询回归，10k 外层行和 10k JOIN 候选基准确认非相关子树执行次数为 1。
2. **P4 已完成**：匹配的持久二级索引与单批临时哈希回退均已接入；100k 子行、100 个父键的 cascade / set-null 基准通过执行计数证明子表解码从约 1000 万行降到一次扫描或 100 次索引查找，并保留多级、显式触及行和事务失败回滚覆盖。
3. **P3 已完成**：仅历史 tombstoned term 不再进入 fuzzy 候选；活跃快照按写入代次失效并在同一查询内跨 token 共享。100k term、90% tombstone、双字段双 token 基准记录为 23.96 ms / 549.15 KB，未引入逐 term `Search`、BK-tree 或 Levenshtein automaton。
4. **P1 只作为后续增强**：用 disjoint / overlapping tombstone 时间窗分别验证 ANN gate；只有基准显示墓碑区间扫描仍占主要成本时，再增加排序合并区间和二分覆盖索引。

P3/P4 的原始重复工作已经关闭。P4 基准同时显示：当一次删除覆盖全部父键时，单次哈希扫描（CASCADE 约 212.7 ms、SET NULL 约 185.1 ms）优于 100 次持久索引查找（约 916.5 ms / 1,006.2 ms）；后续如增加按父键选择率切换索引或单扫的自适应策略，应作为新的独立性能项，不回开本次完成状态。

### 后续性能观察项（未排期）

以下项目来自 P1/P3/P4 收口后的基准结论，不属于已完成项的遗留验收，也不回开 P1/P3/P4。只有触发条件成立并取得独立基准证据后，才进入正式实现排期。

| 编号 | 方向 | 触发条件与设计边界 | 验收要求 | 状态 |
|------|------|--------------------|----------|------|
| PF1 | 级联删除按选择率自适应查找 | 在 100k 及更大子表上补 1/10/50/100 个父键、均匀/倾斜分布、CASCADE/SET NULL 的选择率矩阵；低选择率继续使用完整 FK 二级索引，高选择率允许切换为单次扫描临时哈希。策略必须基于可解释的批量规模/代价信号，不能把某次机器上的固定父键数量直接写成通用阈值 | 与强制索引、强制单扫两条参考路径对拍，选择结果语义完全一致；自适应路径 Median/P90 不应明显劣于同场景更优参考路径（目标不超过 1.2 倍），并保持每批一次 catalog 快照、循环 FK 去重、显式触及行优先和事务回滚测试 | 👀 观察，未排期 |
| PF2 | 高活跃词基数 fuzzy 词典结构 | 先把基准从“100k 历史 term、10k 活跃 term”扩展到 100k/500k 活跃 term、多字段、多 token；只有活跃快照线性枚举成为主要 CPU 成本或 P90 超出检索预算时，才比较 BK-tree 与 Levenshtein automaton。不得用逐 term `Search` 验证，不引入近似召回或第三方运行时依赖 | 新结构与线性枚举生成完全相同的候选 term 集，保留编辑距离阈值、代次失效和查询内共享语义；提交构建成本、查询 Median/P90、分配、索引内存和写后失效成本，至少证明查询延迟有稳定 2 倍收益后再替换线性路径 | 👀 观察，未排期 |
| PF3 | ANN tombstone gate 与区间索引 | 在 disjoint/overlapping tombstone 时间窗和高墓碑基数下复核 ANN 放弃条件；只有墓碑区间扫描仍占主要成本时，才合并排序区间、按查询时间窗快速判定，并在距离计算前过滤已覆盖点 | ANN/暴力扫召回对拍不退化；分别记录无墓碑、低/高墓碑基数下的 P50/P90、距离计算次数和分配，证明 gate 不会让低墓碑常规查询回退 | 👀 观察，未排期 |

PF1 优先级高于 PF2/PF3：它已有本轮 100% 父键覆盖基准证明索引路径在高选择率下明显落后；PF2 当前 10k 活跃 term 场景为 23.96 ms / 549.15 KB，PF3 的原始锁与拷贝问题也已关闭，两者暂时只保留基准门槛，不提前引入复杂索引结构。

> **与 Milestone 28 的关系**：P1 的原始锁与拷贝问题已由 M28 #208 完整消除并关闭。P2 由 M28 #216 完成 WHERE 第一批，后续性能变更已将 memo 贯穿其它表达式位置。P3（fuzzy 活跃 term 视图）与 P4（cascade delete 索引查找）作为 M28 收官后的独立性能补齐现已完成；Milestone 28（已收官，详细正文见 [docs/roadmap-history.md](docs/roadmap-history.md)）的历史结论不变。
