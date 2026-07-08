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
| 17 | 可观测性与运行时可见性（OTel + 结构化日志 + 诊断端点） | #89 ~ #98 | 🚧（#89~#91 ✅ 基线；#92~#98 📋 第二波） |
| 18 | VS Code 数据库扩展（SonnetDB for VS Code） | #99 ~ #108 | 🚧（#99 骨架已落；#100~ 待续） |
| 19 | 生态适配底座能力（关系 + KV/缓存 + 对象桶 + 大量 measurement） | #109 ~ #126 | 🚧（#109~#117、#122/#123 ✅；余项按需） |
| 20 | 多模能力对齐与平移测试（Parity） | #127 ~ #136 | ✅ |
| 21 | Document Store 单机能力升级（MongoDB-like） | #137 ~ #146 | ✅ |
| 22 | Agent Memory / Codebase Intelligence（应用层候选） | #150 ~ #159 | ⏸️ 暂停内置派单 |
| 23 | 搜索与向量引擎合并（DotSearch / DotVector 收编） | #160 ~ #169 | ✅ |
| 24 | SonnetDB Studio 管理体验升级（Document 管理面） | #170 ~ #172 | 📋 |
| 25 | Document Store 验收、文档与发布治理 | #173 ~ #174 | 📋 |
| 26 | 连接器路线独立化（C ABI + 多模型 API） | #175 ~ #181 | ✅ |
| 27 | Industrial Data Agent 与 AI-ready 产品化路线 | #182 ~ #188 | 🚧（#182 文档已落；M28 收官后 #184 Demo 可启动；#183/#185 纯文档） |
| 28 | 可靠性、并发正确性与热路径加固（P0~P5 分阶段） | #189 ~ #244、#261 ~ #262 | ✅（全部收官，详情见归档） |
| 29 | 多模型统一管理工作台（Multi-Model Management Workbench） | #245 ~ #260 | 📋（#245 ✅；Web Admin 旗舰优先） |
| 30 | 多协议设备接入扩展（Sparkplug B / CoAP / Line Protocol UDP） | #263 ~ #268 | 📋（前置 M28 #242 ✅） |
| MM9 | 多模型统一备份、恢复和管理工具第一批 | BackupService + sndb backup | ✅ |

---

## 当前推进重点

> **旗舰（要开始做的）**：
> - **Milestone 29 — 多模型统一管理工作台**：把管理工具从三个孤立工程重构为「一张能力矩阵 × 三个交付面（Web Admin 旗舰 / Studio 桌面 / VS Code）」。**Web Admin 旗舰优先**：A 阶段管理契约 + 统一外壳（#245 ✅ → #246/#247），再逐模型工作台（B 关系 → C KV/MQ/向量/全文 → D 对象/文档收口 → E 桌面/VS Code）。管理界面跨里程碑归口见 M29「管理界面归口」表。
> - **Milestone 30 — 多协议设备接入扩展**：在 M28 已交付的 MQTT 双形态之上补 Sparkplug B（骑 #242 broker）、CoAP、Line Protocol UDP 三条被动接收通道，三段独立可并行，全部收敛既有 BulkIngest 落库。
>
> **进行中（按带宽穿插）**：
> - **M17 可观测性**：#89~#91 基线已落；下一步 #92 Copilot 指标（解锁 M29 #253 曲线）→ #97 会话持久化，详见 M17「可观测性与 Copilot 下一步规划」小节。
> - **M27 Industrial Data Agent**：M28 收官后 #184 端到端工业异常 Demo 阻塞解除、可启动；#183/#185 纯文档随时可做。
> - **M18 VS Code**（#99~#103 闭环）、**M19 生态底座**余项（对象治理 / 通用迁移原语 / 大量 measurement 长稳）。
>
> **已收官**：M28（可靠性 / 并发正确性 / 热路径加固，P0~P5 + SDK 补口全部完成）、M20 Parity、M21 Document Store、M23 搜索/向量合并、M26 连接器。详细正文见 [docs/roadmap-history.md](docs/roadmap-history.md)。

---

## Milestone 29 — 多模型统一管理工作台（Multi-Model Management Workbench）

> **背景**：SonnetDB 已是覆盖 8 种数据模型的多模态库（时序 / 关系 SQL / 文档 / KV / 全文 / 向量 / 对象存储 / 消息队列 SonnetMQ），但管理工具只覆盖了「时序 + SQL」一条线。当前唯一成型的 UI 是 `web/`（Vue3 + Naive UI + ECharts + CodeMirror）Web Admin：有 Dashboard、SQL Console（即 Studio 工作台）、schema 树、结果表/图、Trajectory 地图、Events 监控（SSE）、Users/Grants/Tokens、Copilot；`src/SonnetDB.Studio` 只是把 `web/dist` 打包进 WebView2 的桌面壳，**无任何独立能力**；VS Code 扩展（M18）大部分仍是脚手架（只有 Explorer 树 + SQL 执行客户端能跑）。对照 pgAdmin / SSMS / Navicat / DBeaver（关系）、RedisInsight（KV）、Kafka UI / RabbitMQ Management / EMQX Dashboard（MQ）、Milvus Attu / Qdrant / Weaviate Console（向量）、Kibana / OpenSearch Dashboards（全文）、MinIO Console（对象）、MongoDB Compass（文档），SonnetDB 缺一整批 per-model 管理工作台。
>
> **核心策略**：把「管理工具」从三个孤立工程重构为「一张能力矩阵 × 三个交付面」——(1) **Web Admin 旗舰**，逐模型做到对标单品级别（**本里程碑推进优先级最高的交付面**）；(2) **Studio 桌面** = 打包的 Web Admin + 桌面原生桥（原生文件对话框、磁盘连接库、本地 data-root 托管 server）；(3) **VS Code** = 开发者子集，复用同一批 HTTP 契约。世界级多模态管理工具 = 统一 Explorer + 外壳 + 每模型一个专用工作台，各自向该模型最好的单品看齐；三面共享同一套 server contract、权限模型与写审批框架，不各写各的。
>
> **边界**（与 M24 / M28 一致）：本里程碑只做**管理面 + 最小只读 metadata / browse 契约**。UI 消费 M19 / M21 / M23 / M28 已交付的引擎能力与 HTTP API；发现后端缺必要只读 metadata 时可补最小 server contract，但**不新增任何查询语义、索引语义、存储格式或写入语义**——所有写操作复用既有 data-plane API（SQL / Document / KV / Object / MQ 端点）。**文档模型管理面仍归 M24（#170~#172）**，**对象存储后端治理仍归 M19 #118**；本里程碑只把它们接入统一外壳并补齐对象浏览体验，不重复造引擎能力。`SonnetDB.Core` 零第三方依赖不变；契约新增走 Server 层。

### 能力矩阵（现状 → 目标工作台 → 对标单品）

| 模型 | 现有管理 UI | 目标工作台 | 对标单品 | 归属 PR |
|---|---|---|---|---|
| 时序 measurement | ✅ schema 树 + SQL Console + Trajectory 地图/图 | 保持并接入统一外壳 | InfluxDB UI / Grafana | #246（并入外壳） |
| 关系 SQL 表 | ⚠️ 仅能写 SQL，无数据网格 / 行内编辑 | 数据网格 + 行内编辑 + 可视化 EXPLAIN + 表设计器 + ER + 导入导出 | pgAdmin / SSMS / Navicat / DBeaver | #248~#250 |
| 文档集合 | ⚠️ 树可见 + `documents/find` API，无浏览器 | Document Explorer（**M24 交付**，本里程碑接入外壳） | MongoDB Compass | M24 #170~#172 + #257 |
| KV keyspace | ❌ 无 | keyspace 前缀树 + TTL 查看/编辑 + 类型化值查看 + 批量 / 前缀删 + 过期统计 | RedisInsight / AnotherRedisDesktopManager | #245 契约 + #251 |
| 全文索引 | ⚠️ 索引可见可 rebuild，无检索 UI | BM25 检索 + 高亮 + 分词器（Jieba/CJK）预览 + 模糊 / 短语构建器 | Kibana / OpenSearch Dashboards | #245 契约 + #255 |
| 向量索引 | ❌ 无（仅 schema 类型可见） | ANN 检索 playground（文本→embed / 原始向量→Top-K + score + 过滤）+ 索引统计 + HNSW 参数 | Milvus Attu / Qdrant / Weaviate Console | #245 契约 + #254 |
| 对象桶 | ❌ 无（M19 #118 治理页 🚧） | 桶浏览 + 对象上传 / 下载 / 预览 + 前缀导航 + 版本 / 生命周期 / 保留 + presigned URL + 审计 | MinIO Console / S3 Browser | M19 #118 后端 + #256 |
| 消息队列 SonnetMQ | ❌ 无 | topic / 消息浏览（按 offset / 时间 seek + header）+ 发布测试 + 消费 / 订阅 lag + ack + 吞吐 + DLQ / retention | Kafka UI / RabbitMQ Management / EMQX Dashboard | #245 契约 + #252~#253 |

### 管理界面归口（跨里程碑，单一出处）

> SonnetDB 的管理界面此前散落在多个里程碑（M18 VS Code、M19 #118 对象、M24 文档、M29 多模型），东一块西一块。下表把所有管理 UI 一次列清、给出**唯一归口**；后端治理能力（非 UI）仍归其原里程碑，本表只统一 UI 交付出处。

| 管理界面 | 交付面 | 归属 PR | 状态 |
|---|---|---|---|
| 统一 Explorer + 连接库 + 结果面板 + 写审批框架 | Web Admin | M29 #245~#247 | #245 ✅，#246/#247 📋 |
| 关系数据网格 / 可视化 EXPLAIN / 表设计器 / ER / 导入导出 | Web Admin | M29 #248~#250 | 📋 |
| KV / MQ / 向量 / 全文 专用工作台 | Web Admin | M29 #251~#255 | 📋 |
| 对象桶浏览器 | Web Admin | M29 #256（收编 M19 #118 的 Buckets / Objects / Multipart / Audit 页面） | 📋 |
| 文档 Explorer / Validator / 导入导出 | Web Admin / Studio | M24 #170~#172（M29 #257 接入统一外壳） | 📋 |
| Studio 桌面原生桥（文件对话框 / 连接库 / 本地托管 server） | Studio | M29 #258 | 📋 |
| VS Code 结果三视图 + Copilot 面板 | VS Code | M18 #103/#104（M29 #259 补完接线） | 🚧 |
| VS Code 多模型只读浏览（KV / 向量 / 全文 / MQ） | VS Code | M29 #259 | 📋 |
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
| #246 | **统一多模型 Explorer + 连接库**：把 Web Admin 左侧导航从「时序/表/文档/索引/备份」扩展为覆盖 8 模型的统一树（Connection → Database → {Measurements / Tables / Collections / KV Keyspaces / Vector Indexes / FullText Indexes / MQ Topics / Buckets}）；每类节点的右键菜单路由到对应工作台；新增可持久化的连接库（Remote / Managed-local，token 走既有安全存储），活动连接与数据库选择全局一致，复用 SQL Console / CopilotDock 的 db 选择与权限状态。 | 📋 |
| #247 | **统一结果面板 + 写审批 / 历史 / 导出框架**：抽出跨模型共享的结果面板（Table / Raw / JSON / Chart 四视图，复用 `SqlResultPanel` / `SqlResultChart`）与**写审批框架**（staged preview → danger confirm → dry-run，比照 SQL Console 既有危险确认与 M24 写审批），供 B~D 各工作台统一挂载；统一 query/操作历史与 CSV/JSON 导出钩子；所有写、导入、rebuild、删除动作至少有 preview / dry-run / confirm 之一。 | 📋 |

> **#245 落地说明**：Server 层新增 `ManagementContractEndpoints`，已交付 KV `keyspaces`/`scan`（base64 游标分页）、向量 `indexes`/`search-preview`（复用既有 `knn(...)` data-plane）、全文 `indexes`/`search-preview`/`analyze`、MQ `topics`/`offsets`（含 lag）/`browse`（按 offset 只读）。**对象** bucket/object list 与 metadata **已由既有 S3 端点覆盖**，本 PR 不重复实现。相对"`SonnetDB.Core` 不动"的初始约束，仅新增一个只读枚举方法 `SonnetMqStore.ListTopicStats()`（MQ topic 私有集合无其他公开枚举入口，纯读、不改任何队列语义）。**本 PR 范围外、留待后续里程碑**（Core 无公开 API）：全文 term 数与 BM25 高亮、MQ 按时间 seek、向量索引 live count 与 per-index 有效度量（当前引擎构建固定 cosine，已如实回显）。写/删/rebuild 一律不在本 PR，留给 #247 写审批框架 + 既有 data-plane。

### B — 关系工作台（对标 pgAdmin / SSMS / Navicat / DBeaver）

| PR | 标题与范围 | 状态 |
|----|------------|------|
| #248 | **关系数据网格 + 行内编辑**：表数据网格，游标分页、列排序 / 过滤、单元格类型化渲染；行内 INSERT / UPDATE / DELETE 经生成的**参数化 SQL**（复用 M28 #213）提交，编辑批次走 #247 staged preview + 事务确认（复用 M19 #110/#113 事务）；主键/唯一约束冲突走既有错误码回显。只调既有 SQL 端点，不新增查询语义。 | 📋 |
| #249 | **可视化 EXPLAIN + 表设计器 + 索引管理**：把既有 SQL `EXPLAIN` 计划渲染为可视化计划树（scan / filter / join / topN / 下推标注，复用 M28 #214~#217/#220 的 EXPLAIN 输出）；表设计器以可视化编辑生成 `CREATE TABLE` / `ALTER TABLE ADD/DROP/RENAME COLUMN` / `RENAME TABLE` DDL（复用 M19 #111 能力与其明确拒绝项），DDL 保存前 preview + confirm；索引查看 / 创建 / rebuild。 | 📋 |
| #250 | **关系导入导出 + ER 图**：CSV / JSON 导入导出（列映射、dry-run、批量错误报告、进度、取消）；基于 `INFORMATION_SCHEMA`（M19 #111）绘制 ER 图（表 / 列 / 主外键关系）；DDL 脚本导出。导入写入走 #247 写审批。 | 📋 |

### C — KV / MQ / 向量 / 全文 工作台

| PR | 标题与范围 | 状态 |
|----|------------|------|
| #251 | **KV 浏览器（对标 RedisInsight）**：消费 #245 `scan` 契约做按前缀 / 分隔符的 keyspace 树扫描（游标分页，避免全量拉取）；TTL 显示与编辑（复用 M19 #116 TTL）；按类型的值查看 / 编辑；批量 get/set/remove、前缀删除、命名空间切换、过期统计。写与前缀删走 #247 写审批。 | 📋 |
| #252 | **SonnetMQ 控制台一（topic + 消息浏览 + 发布）**：topic 列表 + offset / 分区 / retention 概览；消息浏览器支持按 offset / 时间 seek、查看 header 与 payload（消费 #245 `browse`）；发布测试消息（复用既有 MQ 发布端点）；依赖 **M28 P5a（#231~#234）** 提供的 per-topic 统计与冷数据可读性。 | 📋 |
| #253 | **SonnetMQ 控制台二（消费 / 订阅监控 + 吞吐 + DLQ）**：消费者 / 订阅 lag 与 ack 监控、消费进度可视化；吞吐 / 积压曲线（复用 M17 metrics + Events SSE）；DLQ 查看与 retention 策略展示。依赖 #245 `lag` 契约与 M28 P5a MQ 统计，随 P5b #236 推送订阅落地可展示实时推送状态。 | 📋 |
| #254 | **向量检索 playground（对标 Milvus Attu / Qdrant）**：向量索引 / 集合统计（维度、行数、度量 L2/IP/cosine、HNSW ef/M/efConstruction，复用 M28 #223/#226 参数暴露）；ANN 检索 playground——文本经 Copilot embed 或直接粘原始 `float[]`，返回 Top-K + score + 元数据过滤（消费 #245 `search-preview` + 既有向量检索端点）；度量方式与图参数只读展示，不改索引语义。 | 📋 |
| #255 | **全文检索 playground（对标 Kibana / OpenSearch Dashboards）**：全文索引列表 + 统计（doc/term 数、分词器）；BM25 检索 UI 带高亮、评分与分页；分词器 / analyzer 预览（Jieba/CJK，展示切词结果）；模糊 / 短语 / 布尔查询构建器（消费 #245 `search-preview` + 既有全文检索端点）；索引 rebuild 走 #247 写审批。 | 📋 |

### D — 对象桶与文档收口

| PR | 标题与范围 | 状态 |
|----|------------|------|
| #256 | **对象桶浏览器（对标 MinIO Console，收编 M19 #118 UI）**：桶列表 / 创建 / 删除；对象浏览（前缀导航、上传 / 下载 / 预览、range read）；multipart 会话查看；版本 / 生命周期 / 保留 / legal hold 展示与编辑；presigned URL 生成；访问审计与容量 / quota 统计。**后端能力复用 M19 #118**（bucket policy / lifecycle / versioning / audit / quota）；本 PR 把 #118 规划的 Buckets / Objects / Multipart / Audit 页面**收编进统一外壳的对象工作台**，#118 只保留后端治理能力交付。 | 📋 |
| #257 | **文档浏览器接入统一外壳**：把 **M24（#170~#172）** 的 Document Explorer / Validator Governance / 导入导出接入 #246 统一 Explorer 与 #247 共享结果 / 写审批框架，确保文档模型与其余模型的连接选择、权限状态、结果面板、写审批一致；**不新增文档引擎能力**（引擎与专属管理语义仍归 M24 / M21）。 | 📋 |

### E — Studio 桌面原生桥 + VS Code 消费 + 收口

| PR | 标题与范围 | 状态 |
|----|------------|------|
| #258 | **Studio 桌面原生桥**：`SonnetDB.Studio`（`NativeWebHost` WebView2 壳）从纯 WebView 升级为带原生桥——原生文件打开 / 保存对话框（供导入导出、对象上传下载、备份恢复）、磁盘持久化连接库、本地 `data root` 托管 SonnetDB Server 启动 / 停止 / 健康检查（对齐 M18 #105 托管本地模式思路）、原生菜单。Web Admin 检测到运行在 Studio 壳内时启用原生能力，浏览器内优雅降级。 | 📋 |
| #259 | **VS Code 多模型消费（复用 M29 契约）**：VS Code 扩展先补完 **M18 #103（结果 Table/Raw/Chart 三视图）+ #104（Copilot 面板，客户端 `streamCopilot` 已写好只差接线）**，再把 Explorer 与结果面板扩展为消费 #245 契约做 **KV / 向量 / 全文 / MQ 只读浏览**；写操作与完整工作台仍以 Web Admin 为主，VS Code 定位开发者只读 + SQL 执行子集。与 **M18 交叉引用**：M18 保留 VS Code 交付主线，多模型浏览契约由本 PR 落地。 | 📋 |
| #260 | **管理工作台收口 + 文档 + 三面 parity**：汇总能力矩阵文档（模型 → 工作台 → 对标单品 → 交付面覆盖度）；`docs/` 增管理工具章节与截图；Web Admin / Studio 桌面 / VS Code 三面能力 parity 表（谁支持哪些模型的浏览 / 查询 / 编辑 / 导入导出 / 监控）；各工作台 e2e smoke。 | 📋 |

### 推进顺序

```text
Web Admin 旗舰优先（用户决策 2026-07-04）：
A 外壳：#245（管理契约补齐）→ #246（统一多模型 Explorer + 连接库）→ #247（统一结果 + 写审批框架）
B 关系：#248（数据网格 + 行内编辑）→ #249（可视化 EXPLAIN + 表设计器）→ #250（导入导出 + ER）
C 四模型：#251（KV 浏览器）→ #252（MQ 控制台一）→ #253（MQ 控制台二）→ #254（向量 playground）→ #255（全文 playground）
D 收口：#256（对象桶浏览器，收编 M19 #118 UI）→ #257（文档浏览器接入外壳）
E 三面：#258（Studio 桌面原生桥）∥ #259（VS Code 多模型消费）→ #260（收口 + 文档 + parity）
```

> **阶段间依赖与并行度**：**A（#245~#247）是所有 per-model 工作台的地基，必须最先**——#245 契约是 #251~#256 的前置，#246/#247 外壳与框架是 B~D 所有工作台的挂载点。B / C / D 各工作台在 A 落地后**相互独立可并行 / 穿插**（各消费自己的 #245 契约 + 挂 #247 框架）。跨里程碑依赖：**#252/#253 MQ 控制台依赖 M28 P5a（#231~#234）** 的 per-topic 统计与冷数据可读性、`#253` 实时推送状态随 P5b `#236` 落地增强；**#254 向量 playground** 依赖 M28 #223/#226 的 HNSW 参数与度量暴露；**#256 对象桶** 依赖 M19 #118 后端治理能力；**#257 文档** 依赖 M24 #170~#172。E 的 #258 桌面桥可在任一模型工作台落地后并行；#259 VS Code 需先补完 M18 #103/#104；#260 收口最后。

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
> **核心判断**：SonnetDB 是**数据库 / 存储层**，不是采集网关。适合数据库原生支持的，是**「设备 push / 从消息总线被动接收」**这一类协议——即它对 MQTT 已做的模式；**「主动轮询设备」的现场总线协议（Modbus / OPC UA client / 西门子 S7 / 三菱 / FINS / AB / MTConnect）不进 DB**——数据库不去 poll 设备，该职责归边缘采集网关（IoTEdge）。本里程碑在此边界内，把 Sparkplug B、CoAP、Line Protocol UDP 三条「被动接收 / 直写」通道补齐，全部**收敛到既有 `BulkIngestEndpointHandler` 三格式落库路径**，不新增任何引擎写入 / 查询 / 索引 / 存储语义。
>
> **不变约束**（与 M28 P5b / #242 一致）：`SonnetDB.Core` 零第三方依赖不变——**所有协议栈限于 Server 层**。**依赖策略（本轮定档，倾向纯托管、避免 native 与重型第三方）**：**(1) Sparkplug 手写 protobuf 解码，零新依赖**——复用本仓已有的手写 protobuf wire-format 解码范式（`PrometheusRemoteWriteReader`：`ReadVarint` / `SkipField` / LEN 切片），Sparkplug `Payload` 与 Prometheus `WriteRequest` 同量级同手法，**不引 `Google.Protobuf`**（其 codegen 走 `Grpc.Tools` 会在 build 时拉 native `protoc`）；proto 字段号手写成常量，proto 文件都不带。**(2) CoAP 明文栈 vendor 自维护**——把 `IoTSharp.CoAP.NET`（本 org 已持有的 SmeshLink CoAP.NET fork，BSD、纯托管零 native）server 子集 vendored 进来现代化到 net10（同 `extensions/MQTTnet.AspNetCore.Routing` 范式），**不引 CoAPnet**。**(3) 唯一允许的新第三方 = DTLS 用 `BouncyCastle.Cryptography` 2.6.2**——.NET BCL 无 DTLS，纯托管 DTLS 现实上只有 BouncyCastle；它是单个纯托管程序集、零 native（与 build 时拉 native 的 Google.Protobuf 性质不同），仅 #266 的 `coaps` 传输层用，且默认关闭。三条协议都是**并列新增**，现有 REST / MQTT / 帧协议全部保留；单机形态，不做 broker 集群 / 桥接 / 跨节点 session。

### 行业对标依据（2026-07 走查工业与 IoT 接入协议）

> - **Sparkplug B = 工业 MQTT 事实标准**：Ignition / Inductive Automation SCADA、Eclipse Tahu 参考实现、HiveMQ / EMQX 原生支持、AWS IoT SiteWise 摄取均以其为准。它**不是新传输层，而是 MQTT 之上的 Protobuf payload + topic namespace（`spBv1.0/{group}/{msgtype}/{node}/[{device}]`）规范**——解决裸 MQTT「无统一 payload 语义 / 无设备发现 / 无死活检测 / 无带宽压缩」四大缺口。TDengine / IoTDB 内建 MQTT broker 但**不原生解 Sparkplug** → 这是 SonnetDB「工业采集平台」定位的差异化高杠杆项，且**纯 payload codec，复用 #242 broker 接入与落库路径，成本最低**。
> - **CoAP = 受约束设备承载协议**：RFC 7252，UDP:5683 / DTLS:5684，REST-like（GET/POST/PUT/DELETE + Observe），4 字节头，为 MCU / 低功耗 / 有损网络设计，是 OMA LwM2M 的承载层。IoTSharp 平台侧已内建 CoAP；DB 侧补 CoAP 直写，让**无 MQTT 栈的约束设备也能直连落库**，映射规则对齐 #242 的 MQTT topic → 资源路径。
> - **Line Protocol UDP = 低开销遥测入口**：InfluxDB 除 HTTP `/write` 外提供 UDP 行协议监听（无 ack、无背压、fire-and-forget），Telegraf `influxdb` output 可直连。本仓 **HTTP `/write` / `/api/v2/write` / Prometheus remote-write 已由 M8 交付**（`InfluxLineProtocolEndpointHandler`，Telegraf / EMQX 生态可直接对接），本里程碑只补**唯一缺失的 UDP 数据报入口**，复用既有 `LineProtocolReader`，零新依赖（`System.Net.Sockets`）。

### 能力矩阵（现状 → 目标接入 → 对标）

| 协议 | 现状 | 目标接入 | 对标 | 归属 PR |
|---|---|---|---|---|
| MQTT（裸）| ✅ 内建 broker + client 订阅外部 broker | 保持 | IoTDB / TDengine 内建 broker、InfluxDB+Telegraf | M28 #242 / #243 ✅ |
| **Sparkplug B** | ❌ | 骑 #242 broker 解码 Protobuf payload + 别名解析 + birth/death 生命周期状态机 → BulkIngest 落库 | Ignition / Eclipse Tahu / HiveMQ / AWS IoT SiteWise | #263 / #264 |
| **CoAP** | ❌ | UDP CoAP 服务端资源路由 `db/{db}/m/{measurement}` → BulkIngest 三格式；DTLS + Observe | OMA LwM2M / IoTSharp 平台 CoAP | #265 / #266 |
| Line Protocol（HTTP）| ✅ `/write`、`/api/v2/write`、Prometheus remote-write | 保持 | InfluxDB / Telegraf | M8 ✅ |
| **Line Protocol（UDP）** | ❌ | UDP 数据报监听复用 `LineProtocolReader` → BulkIngest | InfluxDB UDP listener / Telegraf | #267 |
| 现场总线轮询（Modbus / OPC UA client / S7 / 三菱 / FINS / AB / MTConnect）| ❌（**有意不做**）| 归边缘采集网关 IoTEdge——数据库不主动轮询设备 | — | 不做（见「不做的事」） |

### 阶段总览

| 阶段 | 主题 | PR 范围 | 目标 |
|------|------|---------|------|
| **A** | Sparkplug B（工业 SCADA 事实标准，骑 #242 broker） | #263 ~ #264 | 解码 Protobuf payload + 别名解析落库；birth/death 生命周期 + seq 缺口检测 + rebirth 命令 |
| **B** | CoAP 设备写入（受约束设备 UDP 直连） | #265 ~ #266 | CoAP 服务端资源路由 → BulkIngest 三格式落库；DTLS 安全 + Observe 订阅 |
| **C** | Line Protocol UDP 监听 + 收口 | #267 ~ #268 | 补 HTTP `/write` 之外的 UDP 遥测入口；协议接入文档矩阵 + 落库 parity |

### A — Sparkplug B（工业 SCADA 事实标准）

> 骑在 **M28 #242 内建 MQTT broker** 之上：SonnetDB broker 收到 `spBv1.0/...` 的 PUBLISH，由 Sparkplug 解码器解 Protobuf、解析别名、映射为 measurement 点后落库。**不新增 broker，不新增落库路径**——Sparkplug 是 #242 之上的一层 payload 编解码 + host application 状态机。**Protobuf 解码手写、零新依赖**：新增 `SparkplugPayloadReader : IPointReader`，抄本仓已有的 `PrometheusRemoteWriteReader` 手写 wire-format 骨架（`ReadVarint` / `SkipField` / LEN 切片全可复用，仅需补 float=field 12 / wire type 5 的 `ReadSingleLittleEndian` 一处），`Payload` / `Metric` 字段号手写成常量，**不引 `Google.Protobuf`**（其 codegen 会在 build 时拉 native `protoc`）。**落库直产 `Point` → `BulkIngestor.Ingest`**（与 `PrometheusRemoteWriteReader` 同一引擎入口），**不走 `IngestPayload` 的 LP/JSON/BulkValues 字节路径**——Sparkplug metric 已是强类型值，回序列化成 LP 文本再解析既浪费又丢类型；data-plane parity 仍成立（#268 parity 测试照旧）。**状态机自写**（broker 侧 host application 角色是 SonnetDB 特有，SparkplugNet 等库偏 client 侧）。

| PR | 标题与范围 | 状态 |
|----|------------|------|
| #263 | **Sparkplug B payload 解码 + 数据落库**：在 #242 broker 上挂 Sparkplug topic namespace（`spBv1.0/{group_id}/{message_type}/{edge_node_id}/[{device_id}]`）路由（新增并列 `[MqttController]`，不碰现有 `db/{db}` 控制器）；`NBIRTH`/`DBIRTH` 建立 `name↔alias` 映射表（进程内内存态）并注册设备，`NDATA`/`DDATA` 按 alias 解析 metric（Sparkplug 带宽压缩：DATA 只携 alias 不重发 name）；**metric 映射为点约定**——`measurement = edge_node_id`（带 device 时 = `device_id`），`group_id` / `edge_node_id` / `device_id` 为 tag，metric name 为 field key，per-metric timestamp（缺失回退 payload-level）为点时间戳；**类型映射**：整数族→Int64、Float(field 12,wire5)/Double→Float64、Bool→Boolean、String/Text/UUID→String、DateTime→Int64(ms)，**Bytes/DataSet/Template/File 等非标量本 PR 跳过并计数**（`FieldValue` 仅 Float64/Int64/Boolean/String）；metric name 含 `/` 的按名称合法性规则保留或 `.` 替换。**手写 `SparkplugPayloadReader : IPointReader` 解码，直产 `Point` → `BulkIngestor.Ingest`**（与 `PrometheusRemoteWriteReader` 同一入口，零新依赖，**不走 `IngestPayload` 字节路径**）。BIRTH 缺失时的孤儿 DATA（无 alias 映射）本 PR 丢弃并计数、不触发 rebirth。**本 PR 只做解码 + 落库**，生命周期 / seq 缺口 / rebirth 归 #264。 | 📋 |
| #264 | **Sparkplug B 生命周期与命令**：`bdSeq`（birth-death 序列）+ `seq`（每消息 0–255 滚动）校验与**缺口检测** → 经 `NCMD` 的「Node Control/Rebirth」请求边缘节点重生，补齐丢失的 birth 上下文；`NDEATH`/`DDEATH`（LWT）标记节点 / 设备离线状态；`alias` 表按 edge node 持久化 / 重建（断连重连不丢映射）；`STATE`/primary host application 语义（broker 侧宣告在线以触发边缘节点数据流）；下行命令 `NCMD`/`DCMD` 写入（可选，走写审批）。 | 📋 |

### B — CoAP 设备写入（受约束设备直连）

> CoAP 明文栈 **vendor 自维护**：把 `IoTSharp.CoAP.NET`（本 org 已持有的 SmeshLink CoAP.NET fork，BSD、纯托管零 native，但 NuGet 停在 2020/netstandard2.0）的 **server 子集 vendored 进来现代化到 net10**（同 `extensions/MQTTnet.AspNetCore.Routing` 范式，裁到 server + option 解析 + blockwise + observe，砍 client/net40），**不引 CoAPnet**。资源路径映射对齐 #242 的 MQTT topic：`db/{db}/m/{measurement}`，payload = measurement 内容，`Content-Format` option 选择 Line Protocol / JSON points / BulkValues 三格式，落库复用 `BulkIngestEndpointHandler`。**DTLS（#266）用 `BouncyCastle.Cryptography` 2.6.2**——.NET BCL 无 DTLS，纯托管 DTLS 现实上只有 BouncyCastle（单个纯托管程序集、零 native），版本对齐宿主已用的 2.6.2，仅 Server 层 `coaps` 传输层用。

| PR | 标题与范围 | 状态 |
|----|------------|------|
| #265 | **CoAP 服务端 + 写入落库**：UDP:5683 CoAP 服务端（RFC 7252），`POST`/`PUT` 到资源 `db/{db}/m/{measurement}` → `BulkIngestEndpointHandler` 三格式落库（格式由 `Content-Format` option 选择，回退首字节嗅探，与 #242 一致）；鉴权复用 Bearer/token（经 CoAP option 携带，映射三角色权限）；支持确认型（CON）/ 非确认型（NON）消息与块传输（RFC 7959，大 payload 分块）；错误以 CoAP response code 回（4.00/4.01/4.03/4.04 对齐 REST 语义）。 | 📋 |
| #266 | **CoAP 安全 + Observe 订阅**：DTLS（`coaps`:5684）经 **`BouncyCastle.Cryptography` 2.6.2**（`DtlsServerProtocol` + `DtlsServerTransport`，.NET BCL 无 DTLS，纯托管零 native，仅 Server 层）——**PSK 优先**（受约束设备最常用，`TlsPskIdentityManager`），RPK / 证书作后续增量；握手后解密 datagram 喂回 #265 vendored CoAP 解析 → 落库路径不变，默认关闭需显式启用（同 #242 / #267 安全姿态）。`Observe`（RFC 7641）资源订阅——设备 GET+Observe 一个 `db/{db}/mq/{topic}` 资源，服务端在新消息到达时推送（桥接 SonnetMQ，复用 #236 推送管线，对齐 #242 的 `mq/` 订阅），用 vendored CoAP 的 observe 关系、与 DTLS 正交不依赖 BouncyCastle。安全与 Observe 均为 #265 之上的增量。 | 📋 |

### C — Line Protocol UDP 监听 + 收口

| PR | 标题与范围 | 状态 |
|----|------------|------|
| #267 | **Line Protocol UDP 监听端点**：`System.Net.Sockets.UdpClient`（纯 BCL，零新依赖）监听 UDP 端口，每个数据报 = 一批 Line Protocol 行，复用既有 `LineProtocolReader` + `BulkIngestor`（与 HTTP `/write` 同一解析与落库路径）；目标数据库按监听端口绑定或配置项指定（UDP 无 query 参数）；对标 InfluxDB UDP listener / Telegraf `influxdb` UDP output。**安全边界文档化**：UDP fire-and-forget——无鉴权、无 ack、无背压、受数据报尺寸限制，**仅限可信内网**，默认关闭、需显式启用（与 #242 broker 默认关闭一致）。 | 📋 |
| #268 | **多协议接入收口 + 文档 + parity**：`docs/` 增协议接入矩阵（MQTT / Sparkplug B / CoAP / Line Protocol-HTTP / Line Protocol-UDP → 落库路径映射 + 安全 / QoS / 可靠性边界表 + 选型指引）；各协议落库与既有 `BulkIngestEndpointHandler` 路径的 **parity 平移测试**（同一 payload 经不同协议入口落库结果一致，复用 `tests/SonnetDB.Parity` 骨架）；Sparkplug 解码 / CoAP 吞吐基准进报告。 | 📋 |

### 推进顺序

```text
前置：M28 #242 内建 MQTT broker ✅（Sparkplug 骑其上）
A Sparkplug：#263（payload 解码 + 落库）→ #264（生命周期 + seq 缺口 + rebirth 命令）
B CoAP：#265（服务端 + 写入落库）→ #266（DTLS 安全 + Observe 订阅）
C LP-UDP + 收口：#267（Line Protocol UDP 监听）→ #268（协议矩阵文档 + parity）
```

> **阶段间依赖与并行度**：**A / B / C 相互独立，可按带宽并行 / 穿插**——各自挂在既有落库路径上。段内有序：**#263 是 #264 的前置**（先能解码落库，再补生命周期）；**#265 是 #266 的前置**（先能写入，再加 DTLS/Observe）；#267 独立，#268 收口最后。跨里程碑依赖：**A 段依赖 M28 #242 内建 broker（已 ✅）**——Sparkplug 是其上的 payload 层；#266 CoAP Observe 复用 M28 #236 推送管线（已 ✅）。三条协议的落库全部收敛到 #242/#243 已抽出的共享 `BulkIngestEndpointHandler.IngestPayload`，无新引擎语义。

### 验收标准

- **A（Sparkplug B）**：真实 Sparkplug 边缘节点（或 Eclipse Tahu 测试工具）连上 SonnetDB #242 broker，`NBIRTH`/`DBIRTH` 后 `NDATA`/`DDATA` 的按-alias metric 能正确解析并经 SQL 回查落库；`seq` 缺口触发 rebirth 请求；`NDEATH` 反映节点离线状态；metric→点映射约定文档化且可回查。
- **B（CoAP）**：CoAP 客户端 `POST` 到 `db/{db}/m/{measurement}` 三格式 payload 能落库并经 SQL 回查；readonly token 被拒；块传输大 payload 完整落库；DTLS(PSK) 加密连接可用；Observe 订阅 `mq/{topic}` 能收到服务端推送。
- **C（LP-UDP + 收口）**：UDP 数据报的 Line Protocol 行与 HTTP `/write` 落库结果逐点等价；UDP 监听默认关闭、启用后限可信网络的安全边界在 `docs/` 明确；协议接入矩阵 + 落库 parity 平移测试齐备。
- **全局**：三条协议均复用 `BulkIngestEndpointHandler` 落库、未新增任何引擎写入 / 查询 / 索引 / 存储语义；`SonnetDB.Core` 零第三方依赖不变，协议栈限 Server 层；REST / MQTT / 帧协议全部保留向后兼容。

### 不做的事

- **不**做**现场总线轮询类协议**（Modbus / OPC UA client / 西门子 S7 / 三菱 / FINS / AB / MTConnect）——这类协议要「主动轮询设备」，是**边缘采集网关（IoTEdge）的职责，数据库不去 poll 设备**。这是本里程碑最重要的边界：SonnetDB 只做「设备 push / 总线被动接收 / 直写」通道。
- **不**做设备管理导向协议的完整栈——**LwM2M**（设备管理 / 固件下发导向，非数据洪流）只在 CoAP 承载层落数据入口、不实现其对象模型 / DM 语义；**MQTT-SN**（传感网 UDP，一般由网关转 MQTT）不做，交给网关。
- **不**做 **DDS**（机器人 / 国防实时总线，重且小众）、**AMQP 1.0**（企业消息，本轮未选，如需再评估作 consumer）、**Kafka consumer**（本轮评估后未选，librdkafka native 依赖较重，留后续按现场需求再定）。
- **不**新增引擎语义——所有协议落库复用既有 `BulkIngestEndpointHandler` 三格式与 data-plane，与 #242/#243 边界一致。
- **不**在 `SonnetDB.Core` 引入第三方依赖——Sparkplug 手写解码 / vendored CoAP / DTLS 的 BouncyCastle 均限 Server 层；**不引 `Google.Protobuf`**（Sparkplug 手写 wire-format 解码，复用 `PrometheusRemoteWriteReader` 范式，零新依赖）、**不引 CoAPnet**（vendor `IoTSharp.CoAP.NET` server 子集自维护）；唯一新第三方 = #266 DTLS 的 `BouncyCastle.Cryptography` 2.6.2（纯托管零 native，BCL 无 DTLS 的唯一现实选项）。
- **不**选 **CoAPnet（chkr1011）作 CoAP 基底**（已评估）——CoAPnet 与 IoTSharp.CoAP.NET 两个 fork **均多年不维护**，但既已决定 **vendor 自维护**（而非引 NuGet 依赖），「谁在维护」不再是评判项，只比「哪份源码作 vendor 基底更省事」：选 **`IoTSharp.CoAP.NET`**（SmeshLink→Eclipse Californium 血统）而非 CoAPnet，因为 **(1) 它已是本 org 的 fork**（license 署名含 maikebing，谱系 / 授权 / 控制权零障碍）；**(2) server 子集更全**（observe / blockwise / 资源树是 Californium 血统强项，CoAPnet 偏 client）；**(3) 本地 NuGet 缓存可一手核实**（CoAPnet 从未引入，纯纸面）。chkr1011「与 MQTTnet 同作者、同 policy」的优点仅在**引依赖**语境成立，vendor 后失效。
- **不**引 **`protobuf-net`（Marc Gravell）解 Sparkplug**（已评估）——它确是**纯 C# protobuf**（Apache-2.0，依赖全托管、零 native，靠 `[ProtoContract]` 运行时特性映射、连 codegen 都不需要，比 `Google.Protobuf` 的 `Grpc.Tools`+native `protoc` 干净），是"纯 C# protobuf"问题的合法答案；但仍不选，因 **(1) 它靠 `System.Reflection.Emit` 运行时 IL 生成 → AOT / trim 不友好**（与本仓 NativeAOT 目标冲突，见 MQTTnet.Routing vendor 注释）；**(2) Sparkplug `Payload` 仅 ~10 字段，手写解码约 200 行、与已有 `PrometheusRemoteWriteReader` 同量级**，引库解一个小 message 不划算且 AOT 零摩擦。若未来某协议 message 复杂到手写不划算，protobuf-net 是纯托管回退项（代价 = AOT 友好性）。
- **不**做 broker 集群 / 桥接 / 跨节点 session——单机形态，与 P5「不做分布式」边界一致。
- **不**新造已存在的能力——**InfluxDB Line Protocol over HTTP（`/write`、`/api/v2/write`、Prometheus remote-write）已由 M8 交付**，本里程碑只补 UDP 入口，不重复 HTTP 形态。

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
| #92 | **M17.4：Copilot 指标与追踪**：`SonnetDB.Copilot` 命名空间下新增 `CopilotMeter`（`Meter("SonnetDB.Copilot")`）记录 `copilot.chat.requests`（按 model / mode tag）、`copilot.chat.duration`、`copilot.chat.tokens`（in/out）、`copilot.tool.calls`（按 tool name tag）、`copilot.knowledge.recall.hits` / `.misses`；Agent 每次 `PlanToolsAsync` / `RunToolAsync` / `GenerateAnswerAsync` 都开 `Activity` span，把 `tool.name`、`tool.arguments.length`、`tool.result.rows` 写到 tags。CopilotDock 与 AiSettingsView 增加「最近 1 小时调用 / token 用量」摘要卡片（消费 `/v1/copilot/metrics` 简化端点）。 | 📋 |
| #93 | **M17.5：结构化日志统一**：所有 `ILogger` 调用改用源生成日志（`[LoggerMessage]`），消除运行时 string interpolation 装箱。统一日志事件分类（Write / Query / Flush / Compaction / Wal / Copilot / Auth / Http）与 EventId 区段（1000~1999 写入；2000~2999 查询；…）。在 `Program.cs` 引入 `JsonConsoleFormatter`，生产模式默认输出 JSON 行（`logging.json`），开发模式保持单行简化格式。 | 📋 |
| #94 | **M17.6：Health / Readiness 端点扩展**：把现有 `/healthz` 拆为 `/healthz/live`（进程存活）与 `/healthz/ready`（细分 checks：`segment_store_writable`、`wal_writable`、`copilot_provider_reachable`、`copilot_embedding_provider_reachable`）。引入 `IHealthCheck` 接口的 SonnetDB 实现（无第三方依赖），结果以 ASP.NET Core HealthChecks 标准 JSON 输出。Web Admin 顶部状态条改为消费 `/healthz/ready`，单独显示 4 个 check 的颜色点。 | 📋 |
| #95 | **M17.7：Slow Query Log + Top-N 查询统计**：可选开关 `Observability:SlowQueryLog:Enabled=true` + `ThresholdMs=10000`，并支持 30s / 60s 分级。`QueryEngine.Execute` 完成后若超过阈值则发 `Activity.RecordException`-风格的结构化日志事件，并写入内存环形缓冲（`SonnetDB.Diagnostics.SlowQueryRing` 默认 256 条）。新增 `GET /v1/diagnostics/slow-queries` 与 `GET /v1/diagnostics/top-queries`（按归一化 SQL 指纹聚合 count / p50 / p95 / max）。Web Admin SQL Console 旁边新增「慢查询」抽屉。 | 📋 |
| #96 | **M17.8：Diagnostic Dump 端点**：新增 `GET /v1/diagnostics/dump`（仅 admin token）返回 JSON 快照：进程 GC（`GC.GetGCMemoryInfo()` / `GC.GetTotalMemory(false)`）、ThreadPool（`ThreadPool.GetAvailableThreads`）、SonnetDB 内部计数（每 db 的 MemTable 大小 / Segment 数 / 待 Compaction 任务 / WAL 文件列表 / Copilot 在飞会话数）。**禁止 dump 用户数据点本身**，仅 metadata。CLI 新增 `sonnetdb-cli diag dump` 命令直接调该端点，便于复现性能问题时一键采集。 | 📋 |
| #97 | **M17.9：Copilot 服务端会话持久化（M16 M5 二阶段）**：在 `__copilot__` 系统库新增 `conversations`（`id TAG, title TAG, owner TAG, created_at, updated_at, message_count, summary FIELD STRING`）与 `messages`（`id TAG, conversation_id TAG, role TAG, content FIELD STRING, model TAG, tokens FIELD INT, ts`）两张 measurement；新增 `GET/POST/DELETE /v1/copilot/conversations[/{id}]` 与 `GET /v1/copilot/conversations/{id}/messages`；CopilotDock 「会话历史」Popover 在登录态下从服务端拉取（owner=当前 user），匿名/未登录回落到现有 `localStorage` 存储。会话历史可按 owner 隔离与跨设备同步。 | 📋 |
| #98 | **M17.10：CHANGELOG / docs / OTel 端到端验证**：补 `docs/observability.md`（指标列表、追踪 span 树、health checks 含义、prom scrape 配置示例、`OTEL_EXPORTER_OTLP_ENDPOINT` 与本地 Aspire Dashboard 联调）；补 `docs/troubleshooting.md`（常见慢查询模式 + diagnostic dump 解读）；补 docker-compose 示例追加可选 `otel-collector` + `prometheus` + `grafana` 三服务（`profile: observability`，默认不启动）；端到端验证：嵌入式启动 → 触发写入 / 查询 / Copilot 调用 → 在 Aspire Dashboard 看到完整 trace（HTTP → SQL → Segment 读取 → Copilot Agent → tool 调用）。 | 📋 |

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
- **#93 结构化日志** —— `[LoggerMessage]` 源生成 + JSON 行 + EventId 分区，消除装箱、便于集中式日志检索。
- **#94 Health Live/Ready 拆分** —— `/healthz/live` vs `/healthz/ready`（segment / wal / copilot provider 细分 check），供编排探活。
- **#95 慢查询 Log + Top-N 统计** —— 归一化 SQL 指纹聚合 p50/p95/max，Web Admin 慢查询抽屉。
- **#96 诊断 Dump 端点** —— GC / ThreadPool / 每 db 内部计数快照（仅 metadata，不含用户数据点），CLI 一键采集。
- **#97 Copilot 服务端会话持久化** —— `__copilot__` 系统库存会话 / 消息，登录态跨设备同步，匿名态回落 localStorage。
- **#98 文档 + docker-compose + 端到端联调** —— `docs/observability.md`、Aspire Dashboard / OTLP Collector 全链路 trace 验证。

**Copilot 线索整合（M17 + M27，M28 收官后阻塞解除）**：

- **M17 侧**：#92（指标）+ #97（会话服务端持久化、跨设备同步）。
- **M27 侧（M28 已收官、依赖解除）**：#184 端到端工业异常 Demo（P5 MQTT 内建 broker + P0/P2 可靠写入已就绪，**现可启动**）、#187 eval 与成本指标（provider / model / tool 调用数 / 失败原因 / 近似 token 成本；仍建议推迟到有真实采纳之后）；#183（MCP 工具契约文档化）/ #185（provider-neutral 配置样例）纯文档，随时可做。

**建议下一步排序**：先 **#92**（Copilot 指标，解锁 M29 #253 曲线）→ **#97**（会话持久化）；**M27 #184 Demo** 现可与 M17 第二波并行启动；#93~#96 按运维带宽穿插；#98 收口最后。

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
| #185 | **Provider-neutral Copilot 配置回归**：`OpenAICompatibleChatProvider` + `IChatProvider` / `IEmbeddingProvider` 抽象**已实现**；本 PR 把 Chat / Embedding provider 抽象文档化并补齐 OpenAI-compatible、Azure OpenAI、国内兼容网关、本地 Ollama / vLLM 的配置样例；Web Admin 模型选择器明确区分“平台默认模型”“自定义模型”“本地模型”。 | 📋（可与 M28 并行，纯文档 + 少量前端） |
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
| #99 | **扩展骨架 + Manifest + Activity Bar 容器**：在 `extensions/sonnetdb-vscode/` 建立 `package.json` / `tsconfig.json` / `src/` / `media/` 结构；注册 `SonnetDB` Activity Bar、基础命令（Add Connection / Refresh / Run Query / Open Copilot / Start Managed Local Server）与 TreeView 骨架；本次仓库先落规划与占位代码，后续实现按下列 PR 继续填充。 | 🚧 |
| #100 | **远程连接模型 + SecretStorage**：实现连接配置模型（`remote` / `managed-local`）、`SecretStorage` token 持久化、活动连接选择、`/healthz` 探活、`/v1/setup/status` 首次安装探测；连接面板支持测试连通性与提示未初始化状态。 | 📋 |
| #101 | **Explorer 树：Connections → Databases → Measurements → Columns**：消费 `GET /v1/db` 与 `GET /v1/db/{db}/schema`，展示数据库 / measurement / 列结构；支持刷新 schema、复制 measurement 名、预留 sample rows / open in query runner 入口。 | 📋 |
| #102 | **SQL 执行链路 + SonnetDB 方言补全**：实现 `POST /v1/db/{db}/sql` ndjson 解析、Run Current Statement / Run Selection 命令；复用 `web/src/components/sonnetdb-dialect.ts` 的关键词与 schema 补全思路，先以编辑器命令为主，不急着上完整 Notebook。 | 📋 |
| #103 | **结果面板：Table / Raw / Chart 三视图**：新增 Query Result Webview Panel，支持表格、原始 ndjson/JSON、时间序列图表三视图；图表规则复用 Web Admin `SqlResultChart` 的时间列 / 数值列 / tag 分组启发式；补 query history 与导出钩子。 | 📋 |
| #104 | **VS Code 内置 Copilot 面板**：接入 `POST /v1/copilot/chat/stream`、`GET /v1/copilot/models` 与 `GET /v1/copilot/knowledge/status`；支持 `read-only` / `read-write` 模式切换、模型选择、引用折叠、最近执行 SQL 一键发送到查询面板。 | 📋 |
| #105 | **托管本地 SonnetDB Server 模式**：扩展选择本地 `data root` 后，自动启动 / 关闭本地 SonnetDB Server 进程，处理端口占用、日志输出与健康检查；本地与远程共用同一个 HTTP client 与 Explorer/UI。 | 📋 |
| #106 | **生产力增强**：Create Measurement 向导、bulk import（LP / JSON / Bulk VALUES）、starter snippets、从当前 SQL 或 schema 上下文打开 help / docs / explain 入口。 | 📋 |
| #107 | **Language Service / LSP Sidecar**：通过独立 C# sidecar 或轻量协议复用现有 `SqlParser` / schema 能力，补 diagnostics、hover、signature help、repair suggestion 与 `explain_sql` 集成。 | 📋 |
| #108 | **打包发布 + CI + 文档**：补扩展测试、VSIX 打包、Marketplace 元数据、截图与文档；在主 README / docs 中增加安装、连接、权限与本地模式说明。 | 📋 |

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

> **与 Milestone 29 的关系**：本里程碑保留 VS Code 扩展交付主线（#99~#108）。Milestone 29 的 #259 在 A/B/C 工作台契约（#245）落地后，负责**补完本里程碑 #103 结果三视图 + #104 Copilot 面板**（`streamCopilot` 客户端已实现只差接线），并把 Explorer 扩展为消费 M29 契约做 KV / 向量 / 全文 / MQ **只读浏览**；VS Code 定位开发者只读 + SQL 执行子集，完整 per-model 编辑体验以 Web Admin 旗舰为准。管理界面的跨里程碑归口见 Milestone 29「管理界面归口」表（VS Code 管理 UI 由 M29 #259 补完）。

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
| #118 | **对象生命周期、版本、审计与配额**：补 bucket policy、retention/lifecycle、object versioning、legal hold 占位、访问审计、容量统计和 quota；Web Admin 增加 Buckets / Objects / Multipart / Audit 页面。**UI 页面收编进 M29 #256 统一对象工作台，#118 只保留后端治理能力（见 M29「管理界面归口」表）。** | 🚧 |
| #119 | **生态接入样例与 Profile 文档边界**：保留 SonnetDB 作为嵌入式/远程服务、EF、缓存和对象桶的通用接入样例；具体 IoTSharp Profile、灰度、双写、回滚和生产验收迁出到 IoTSharp 仓库维护。 | 🚧 |
| #120 | **通用迁移与校验原语评估**：只规划 SonnetDB 通用 export/import、checksum、scan、backup/restore 原语；不在本仓库维护 `iotsharp migrate/verify/rollback` 等上层产品专用命令。 | 📋 |
| #121 | **通用长稳、压测和故障恢复报告**：覆盖 SonnetDB EF Core provider、批量写入、KV TTL、对象 multipart、备份恢复、断电恢复、升级回滚；上层 Profile 长稳报告由上层项目维护。 | 📋 |
| #122 | **大量物理分表文件布局与启动扫描优化**：面向大量 measurement / 大量 segment 场景，设计并实现分层 segment 目录布局（例如按 segmentId 前缀或时间桶拆分）、目录枚举兼容层、备份扫描优化、旧段清理策略和布局迁移工具；保留旧 `segments/{id}.SDBSEG` 读取兼容。 | ✅ |
| #123 | **Compaction manifest 与重复段恢复**：为 compaction 引入 manifest 或等价 superseded segment 状态，记录 source segments、target segment、提交阶段和清理阶段；启动时根据 manifest 忽略或清理被替代旧段，解决 crash after swap before delete 后新旧段同时加载导致重复数据的问题。 | ✅ |
| #124 | **SegmentManager 增量索引与后台维护成本控制**：将 `AddSegment` / `SwapSegments` 从全量重建索引快照优化为增量更新或分层索引发布；补充大量 segment 下 flush、compaction、retention、query 并发时的 CPU、内存和暂停时间基准。 | 📋 |
| #125 | **大量 measurement / 长稳专项套件**：新增百万级 series、万级 measurement、海量小 segment、随机重启、后台 flush/compaction/retention 并发、重复数据检测和恢复时间统计；输出“能改善什么、不能改善什么”的容量边界报告。 | 📋 |
| #126 | **SQL 正则模式查询与 EF 翻译规划**：在 `LIKE` 基线之后引入正则匹配能力，第一阶段支持 `regexp_like(input, pattern[, flags])` 标量函数，可用于 `WHERE` 过滤与 `SELECT` 投影；同时评估 `expr REGEXP pattern`、`expr NOT REGEXP pattern`、`RLIKE` 别名，兼容 MySQL、SQLite 常见写法。第二阶段补 `regexp_substr`、`regexp_replace`、`regexp_instr`，并在 EF provider 中把 `Regex.IsMatch(...)` 翻译为 `regexp_like(...)`。所有正则执行必须设置超时、限制模式长度、缓存编译结果，并在执行计划中明确标注 scan filter；后续可识别 `^literal` 前缀模式做索引剪枝优化。 | 📋 |
| #126.1 | **关系表大批量删除、逻辑删除与后台收缩**：补齐 rowstore / table executor 的批量删除快路径，避免 `DELETE FROM ... WHERE ...` 对大表逐行阻塞 HTTP/Kestrel 和前台事务。默认路线采用逻辑删除或 tombstone 标记，前台删除只写入删除标记、索引可见性变更和轻量统计；后台 compaction/vacuum/shrink 任务根据 CPU、IO、内存、活跃连接数和业务时段限速执行，逐步回收 WAL、snapshot、segment/rowstore 空间。新增 `TRUNCATE TABLE` / `DROP TABLE DATA` 等受权限保护的整表清空原语，用于测试重置和明确的运维场景，并提供可取消、可观测、可恢复的任务状态。 | 📋 |

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
  → #124（增量索引 / 后台维护成本）
  → #125（大量 measurement / 长稳专项）
  → #126（正则模式查询）
  → #126.1（关系表大批量删除 / 逻辑删除 / 后台收缩）
```

### 验收标准

- SonnetDB ADO.NET、EF Core provider、KV/cache provider 和 object storage API 提供稳定的通用能力边界；
- EF Core provider 可通过典型 `ApplicationDbContext` 迁移历史表创建、迁移升级/回滚、重复迁移幂等检查、Identity 登录、主数据 CRUD 和核心查询；
- KV/cache provider 的 TTL 行为、批量操作、命名空间、过期清理和并发语义有独立测试；
- SonnetDB SQL 模式匹配能力必须覆盖 `LIKE`、`NOT LIKE`、`regexp_like` 在 `WHERE` 与 `SELECT` 中的行为，并明确正则超时、模式长度、编译缓存和 scan filter 边界；
- object storage API 覆盖上传、下载、删除、range read、multipart、presigned URL、版本、生命周期和审计回归；quota 与 Web Admin 继续推进；
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
| #170 | **Studio Document Explorer**：新增 Document Explorer，支持数据库 / collection 列表、集合 schema、索引列表、JSON 查询编辑器、结果表 / JSON 双视图、分页浏览、文档详情与只读复制；复用现有 SonnetDB Studio 布局、权限模型和 `CopilotDock` 上下文。 | 📋 |
| #171 | **Studio Validator Governance**：把 Milestone 21 的 collection validator 暴露为 Studio 管理体验，支持查看 / 编辑 validator、切换 validation action（error / warn）、查看 schema evolution / 变更历史、预检样本文档和保存前 dry-run；所有写入操作走现有写审批模式。 | 📋 |
| #172 | **Studio Document 导入导出与维护操作**：支持 JSONL/NDJSON 导入导出、`_id` path 映射、dry-run、批量错误报告、进度显示、取消、索引 rebuild 触发与状态查看；危险维护动作需要二次确认并记录审计事件。 | 📋 |

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
| #173 | **MongoDB 参考 parity 套件**：在 `tests/SonnetDB.Parity` 新增 `MongoAdapter`（官方 MongoDB .NET Driver 仅连接参考 MongoDB 容器）与 `DocumentAdapter`（SonnetDB 自有 API），覆盖 CRUD、filter、projection、sort、update operators、index unique/TTL、aggregation、并发写、崩溃恢复后的索引一致性；报告中明确“语义对齐，不做协议兼容”。 | 📋 |
| #174 | **Document 长稳、容量与发布文档**：百万 / 千万文档 profile 长测，输出写入、查询、索引 rebuild、TTL 清理、冷启动、备份恢复、内存占用报告；README / docs 新增 Document Store 能力矩阵、MongoDB-like 迁移指南、明确不支持项、推荐规模和 Studio 管理入口说明。 | 📋 |

### 验收标准

- `tests/SonnetDB.Parity` 的 MongoDB 参考文档场景全部 PASS 或结构化 SKIP，SKIP 必须带明确 `gap_reason`。
- 长稳报告覆盖热 / 冷启动、索引 rebuild、TTL 清理、backup/restore、崩溃恢复和内存曲线，性能数字进入报告但不做主 CI gating。
- 发布文档必须明确 SonnetDB Document Store 与 MongoDB 的差异、迁移边界、不支持项、推荐数据规模和不做协议兼容的原则。
- 文档更新放在发布治理阶段收尾，不反向扩大 Milestone 21 的能力范围。

---

## Milestone 22 — Agent Memory / Codebase Intelligence（应用层候选，非内置路线）

> **当前状态**：⏸️ 应用层候选，暂停内置派单。该方向更像“基于 SonnetDB 构建的 Code Memory / Agent Memory 应用”，不是 SonnetDB Core / Server / Studio 必须内置的数据库能力；#150~#159 不再作为 SonnetDB 内置路线派单。
>
> **复核确认（2026-07-04）**：本轮里程碑复核再次确认**不派单、不内置**。判断依据：(a) M22 是「建在 SonnetDB 上的应用」而非引擎能力；(b) 其所需能力（Document + FullText BM25 + Vector HNSW + Hybrid + MCP）**均已在库内存在**，M22 不会产出任何新引擎能力；(c) #152/#153 需要 Roslyn / tree-sitter / libgit2，违反 `src/SonnetDB.Core` 零第三方依赖铁律。M22 唯一保留价值是当「能力缺口探针」——若将来在 `examples/` 里 dogfood（如摄入 IoTSharp 自身仓库）暴露出某个**通用** Document / Vector / Hybrid 能力缺口，才把该缺口拆成独立 PR；Code Memory 应用本身不进产品面。
>
> **目标（应用视角）**：验证用户能否把 Git 仓库、设计文档、ADR、CI 变更、代码评审记录和 Agent 会话作为上层应用数据摄入 SonnetDB，并用 SQL / HTTP / MCP 查询“代码是什么、谁调用谁、为什么这么设计、改这里会影响哪里”。SonnetDB 的职责是提供通用数据引擎能力，不直接承诺内置 Code Memory 产品。
>
> **重新定位**：M22 若继续保留，应进入 `examples/`、独立应用仓库、插件或 Solution Accelerator，用来展示 SonnetDB 的 Document / FullText / Vector / Hybrid Search / MCP 组合能力。只有当应用验证出通用数据库能力缺口时，才拆出独立 Core / Server / Studio PR；不得因为 Code Memory 应用本身而把 Roslyn、Git 扫描、专用 code schema、专用 MCP tools 或 Code Memory Explorer 默认塞进 SonnetDB 内置产品面。
>
> **设计原则**：
>
> 1. **应用优先，不内置优先**。Code Memory schema、ingest、MCP tools 和 UI 默认属于上层应用，不进入 SonnetDB 默认产品面。
> 2. **数据库能力抽象优先**。若应用暴露出共性能力缺口，应沉淀为通用 Document / FullText / Vector / Hybrid Search / MCP / 权限能力，而不是沉淀为 codebase 专用 API。
> 3. **Core 零依赖边界不破坏**。`src/SonnetDB.Core` 不引入 tree-sitter、Roslyn、libgit2 等大型运行时依赖；代码解析与 Git 扫描放在独立应用、插件、扩展包或示例工具中。
> 4. **结构化优先，向量补充**。文件、符号、调用边、引用边、commit、ADR、会话、工具调用都以结构化表/文档/边表落库；embedding 用于语义召回，不替代确定性的 symbol / edge 查询。
> 5. **安全只读默认**。MCP memory tools 默认只读，按 project/repo/branch/owner 隔离；代码片段读取要有大小限制、路径白名单和审计事件。
>
> **候选产出**：独立 Code Memory 应用方案、示例 schema、独立 ingest 工具、独立 MCP Memory Server、Hybrid Search 示例和 VS Code / Copilot 接入样例；不默认新增 SonnetDB 内置 CLI 命令、Server 专用端点或 Studio 页面。

### 数据模型草案

| 类型 | 建议实体 | 用途 |
|------|----------|------|
| 仓库与文件 | `code_repositories`、`code_files`、`code_file_versions` | repo/project/branch/commit、路径、语言、hash、mtime、大小、license 元数据 |
| 符号与结构 | `code_symbols`、`code_symbol_locations` | namespace/type/method/property/endpoint/test 等符号定义与位置 |
| 关系边 | `code_edges` | calls / references / implements / tests / imports / routes_to / owns 等边 |
| 文本与向量 | `code_chunks` | 代码块、注释、README、docs、embedding、BM25/Hybrid Search |
| Git 演化 | `code_commits`、`code_changes` | commit 时间线、作者、文件变更、热点模块、变更趋势 |
| 决策与记忆 | `code_decisions`、`agent_memories`、`agent_tool_events` | ADR、设计决策、review 结论、Agent 会话摘要和工具调用审计 |

### 应用化候选拆分（暂停内置派单）

| PR | 主题 | 状态 |
|----|------|------|
| #150 | **Code Memory 应用方案与 schema 草案**：若保留，仅在示例 / 独立应用文档中定义 repo/file/symbol/edge/chunk/commit/decision/memory schema、索引建议、权限模型、规模边界和与 Document / FullText / Vector / Hybrid Search 的映射；不作为 SonnetDB 内置 schema 或默认文档主线。 | ⏸️ 应用化候选 |
| #151 | **独立 ingest 工具第一版（Git + 文件 + 文档块）**：作为应用 CLI / 示例工具扫描 Git 工作区、README/docs/source 文件，写入 repo/file/chunk/commit 基础数据；不新增 SonnetDB 内置 `sndb memory` 命令。 | ⏸️ 应用化候选 |
| #152 | **C# 符号索引器（Roslyn 可选应用层）**：在独立应用 / 工具层引入可选 Roslyn 分析路径，输出写入应用自定义 schema；不进入 `src/SonnetDB.Core` 或默认 CLI 运行时依赖。 | ⏸️ 应用化候选 |
| #153 | **调用边与引用边第一版**：作为应用层索引能力提取 calls/references/implements/tests/imports/routes_to 边；若需要通用图查询能力，另行论证为独立数据库能力。 | ⏸️ 应用化候选 |
| #154 | **独立 Code Memory MCP tools**：由应用自带 MCP Server 暴露 `code_search`、`symbol_search`、`code_callers`、`code_callees`、`code_impact`、`code_snippet`、`decision_search`；不新增 SonnetDB Server 内置专用端点。 | ⏸️ 应用化候选 |
| #155 | **Hybrid Search 示例与排序融合**：把 `code_chunks` 作为应用数据接入全文 BM25 + embedding KNN + metadata filter 融合，用于验证 SonnetDB 通用检索能力。 | ⏸️ 应用化候选 |
| #156 | **Agent Memory 应用 API**：面向 Agent 的 memory 写入/读取契约保留在上层应用；若后续证明为通用需求，再抽象为 SonnetDB 通用审计 / conversation / memory 能力。 | ⏸️ 应用化候选 |
| #157 | **Code Memory Explorer 应用 UI**：作为独立应用 UI 或示例页面展示 repo/project、索引状态、文件/符号搜索和影响分析；不默认进入 SonnetDB Studio / Web Admin。 | ⏸️ 应用化候选 |
| #158 | **VS Code / Copilot 接入样例**：在扩展或示例中消费独立 Code Memory MCP / API，展示“解释当前符号”“查找调用者”“改动影响分析”等应用场景。 | ⏸️ 应用化候选 |
| #159 | **应用规模与验证报告**：用 SonnetDB 自身仓库、IoTSharp 仓库和一个中大型开源 C# 仓库做 profile，输出应用层 ingest / 查询 / 增量成本报告；不作为 SonnetDB 核心发布门槛。 | ⏸️ 应用化候选 |

### 原草案推进顺序（暂停）

> 当前不按以下顺序派单；仅保留为后续重新论证时的历史草案。

```text
#150 (schema + docs)
  → #151 (Git/files/chunks ingest)
  → #152 (C# symbols)
  → #153 (edges)
  → #154 (HTTP/MCP query tools)
  → #155 (Hybrid Search)
  → #156 (Agent Memory API)
  → #157 (Web Admin Explorer)
  → #158 (VS Code / Copilot examples)
  → #159 (scale + docs)
```

### 验收标准

- 用户可以把任意本地 Git 仓库摄入 SonnetDB，并在 `GET /v1/db/{db}/schema` 或专用 status 端点看到 repo、文件、chunk、symbol、edge、commit、memory 的索引统计。
- MCP tools 能回答常见代码智能问题：搜索代码/文档、查符号定义、查 callers/callees、做一跳或多跳影响分析、返回带 source location 的片段。
- Hybrid Search 能融合代码文本、文档、符号 metadata、向量相似度和 Git 时间维度，结果带稳定 score 分解与引用。
- Agent Memory API 能保存和检索会话摘要、工具调用、review finding、ADR/decision，并按 owner/project/repo 隔离。
- 索引器支持增量重建、dry-run、取消、失败文件报告和可重复运行；不把生成索引提交到源码仓库。
- Web Admin 和 VS Code 至少各有一个可演示闭环：搜索符号、查看调用关系、把结果发送给 Copilot 或 MCP Host。
- `src/SonnetDB.Core` 继续保持零第三方运行时依赖；语言解析器依赖只允许出现在 CLI/扩展/测试/示例项目中。

### 不做的事

- **不**把 SonnetDB 绑定为某一个 MCP Host 或 IDE 的私有实现；MCP 只是对外接口之一。
- **不**在第一版实现任意图查询语言或复杂代码属性图数据库；先提供 typed tools 和稳定 schema。
- **不**承诺多语言 AST 全覆盖；第一阶段优先 C# / TypeScript / Markdown 的实用闭环。
- **不**把第三方语言解析器、Git 原生库或大型 AI framework 引入 `src/SonnetDB.Core`。
- **不**默认保存 secrets、大文件、二进制文件或 `.git` 内部对象内容；ingest 必须尊重 exclude 配置与大小限制。

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

以下是一次完整审计后留下的纯性能优化点；功能上是对的，只是热路径里有可优化的常数因子或代数复杂度。每项都有目标位置和现状成本，便于后续按需安排。

| 编号 | 位置 | 现状 | 建议改造 | 估时 |
|------|------|------|---------|------|
| P1 | `src/SonnetDB.Core/Query/KnnExecutor.cs:103` | 每个候选都调用 `TombstoneTable.IsCovered` —— 内部锁 + `ToArray()` 快照 | 提到 ScanSegment 之前一次性拿快照（已在 KnnExecutor 顶层做 GetForSeriesField 检查），把候选过滤改成直接遍历该快照 | 15 分钟 |
| P2 | `src/SonnetDB.Core/Sql/Execution/RelationalSelectExecutor.cs` 子查询路径 | 同一个子查询 SELECT 子树在每个外层行上重新执行；只要不引用外层列就能 memoize | 对 ExistsExpression / SubqueryExpression 加 `Cache<SelectStatement, IReadOnlyList<...>\>`，先做一次 "是否相关" 静态判定；非相关查询执行 0 或 1 次 | 30 分钟 |
| P3 | `src/SonnetDB.Core/FullText/DocumentFullTextIndexStore.cs` ExpandFuzzyTermQuery | 模糊扩展时把 tombstoned term 也参与编辑距离计算 | 让内置全文引擎的 EnumerateTerms 暴露一份 "未 tombstone" 视图，或者在 PersistentFullTextIndex 端先过滤；当前简单做法是上层把展开候选再用一次 Search 验证 | 10 分钟 |
| P4 | `src/SonnetDB.Core/Tables/TableManager.cs` ExpandCascadeDeletesLocked | BFS 每一步都对子表做 `childStore.Scan()` 全表线性扫描——O(parents × FKs × N) | 在子表 FK 列上建临时哈希索引（`Dictionary<keyBytes, List<row>>`），或直接给 FK 列建持久化二级索引，cascade 改成索引查找 | 60 分钟 |

这些不阻塞功能正确性，不影响 parity 通过率，并且在小数据量上不会被察觉。当任一线上场景遇到瓶颈时（高基数 KNN / 重相关子查询 / 高基数 fuzzy / 万行级 cascade）按需挑出来做。

> **与 Milestone 28 的关系**：本表是上一轮审计遗留的独立性能小项，其中 P2（子查询 memoize）已被 [Milestone 28](#milestone-28--可靠性并发正确性与热路径加固reliability--concurrency--performance-hardening) 的 #216 吸收合并落地；P1（KnnExecutor tombstone 快照）与 M28 #208/#226 相邻，可一并处理。P3（fuzzy tombstone 视图）与 P4（cascade delete 哈希索引）不在 M28 范围内，保留在本表按需推进。Milestone 28（已收官，详细正文见 [docs/roadmap-history.md](docs/roadmap-history.md)）是 2026 年更完整一轮跨子系统审计（54 项）的成果，涵盖数据可靠性、并发正确性、SQL 正确性与更广的热路径。
