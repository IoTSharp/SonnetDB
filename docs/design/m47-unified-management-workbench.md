# M47 统一数据库管理工作台与三面发布设计

**状态：`📋 planned`；HTML 原型为 `REVIEW_DRAFT`。** 本专题把 Web Admin、Studio 桌面和 VS Code 扩展收敛到一套可复用的管理工具核心。当前已整理 [M47 设计评审包](m47-unified-management-workbench/README.md)，覆盖导航、五区外壳、共享对话框与提示、页面规范和静态 HTML 原型；用户确认与逐页最终视觉仍待完成，不表示生产代码、安装包或真实发布门禁通过。

## 1. 产品判断

SonnetDB 的三个管理面不应做成三个独立产品，也不应把 VS Code 变成完整 Web Admin。统一的目标是：

> 一套资源模型、权限合同、工作区语义、结果呈现和设计令牌；三个宿主外壳，分别服务治理、桌面运维和开发者查询。

统一的是数据与交互语义，宿主可以不同。Web Admin/Studio 使用 Vue 工作台，VS Code 使用 Remote-first 的扩展宿主和 Webview；两者共享类型、API 客户端、状态语义、快捷键命名、主题令牌和测试夹具，不强行共享一个 DOM 树。

当前 M29 已交付全局外壳、Explorer、结果/历史/审批、八模型工作台、Studio bridge 和 VS Code 只读子集。本里程碑负责把这些能力整理为可发布的产品线，补齐九模型一致性、三面边界、AI/MCP 入驻和真实发布证据。

本轮推进顺序遵循用户要求：**先全菜单与导航目录、五区外壳、共享对话框与提示、一级/二级导航，再逐页细化；用户确认原型后才进入生产实现。** 当前七个一级模块固定为概览、工作台、观测、数据流、AI 与 MCP、治理、设置。设计目录覆盖 30 个全局页面、九模型工作区和 166 个任务页签，其中模型任务 60 个；这些数量表示规划覆盖，不表示生产页面或真实任务已全部完成。M29 的五区数据库工具外壳继续作为基线，M47 不另造三个独立产品。

## 2. 竞品学习与采纳边界

| 领域 | 参考产品 | 重点学习 | SonnetDB 的采纳决定 |
|---|---|---|---|
| 统一工作台 | dbx、Tabularis、DBeaver | 连接、Explorer、查询、结果、历史在同一任务空间内连续；连接状态和对象上下文不丢失 | 复用 `connection → database → object → workspace` 上下文；保留九模型语义，不追求通用多库驱动数量 |
| SQL 与分析 | DataGrip、DBeaver、Tabularis、DuckDB UI | 编辑器标签、选中执行、查询历史、Notebook、Visual Explain、可恢复工作区 | SQL Workspace 与 Notebook 进入共用核心；Visual Explain 作为结果视图，不另造诊断页面 |
| 时序 | InfluxDB Explorer、Grafana Explore、TDengine Studio | 时间范围、设备/TAG 筛选、图表、轨迹、聚合和 downsample 的连续探索 | 保持 Measurement Workbench；时间轴、TAG、Trajectory 与查询结果共享过滤上下文 |
| 关系 | pgAdmin、DataGrip、DBeaver、SSMS | 数据网格、表设计器、索引、ER、DDL、EXPLAIN、导入导出审批 | 使用统一工作区页签；关系专用设计器和 ER 只在 Web Admin/Studio 出现 |
| 文档 | MongoDB Compass | JSON/树/原始视图、查询构建、索引/validator、游标分页 | Inspector 支持 JSON/Tree/Raw；服务端 cursor 和批量导入状态进入通用结果合同 |
| KV | RedisInsight | 前缀浏览、TTL、类型化值、批量操作和 key 详情 | Explorer 显示 keyspace/prefix；二进制值使用 Text/JSON/Hex/Base64 检查器 |
| MQ | Kafka UI、RabbitMQ Management、EMQX Dashboard | topic/消息/消费者组、lag、retention、DLQ 和消息 inspector | SonnetMQ 保留本地持久语义；消息浏览、运行时和恢复状态共用 Inspector/History |
| 向量 | Milvus Attu、Qdrant Console、pgvector 工具 | 维度/metric、Top-K、metadata filter、命中详情和索引状态 | Vector Search Workbench 只解释真实 profile、维度和过滤；不把 tiny fixture 当质量证据 |
| 全文 | Kibana、OpenSearch Dashboards、Meilisearch | 查询构建、BM25、highlight、analyzer/token 预览和重建进度 | FullText Workbench 共用结果分页、命中 Inspector 和异步重建状态 |
| 对象 | MinIO Console、S3 Browser | bucket/prefix、metadata、Range 预览、multipart、版本和治理 | 对象工作台保留 S3 风格体验，但不宣称完整 S3/SigV4 兼容；大文件操作必须有进度和校验 |
| 图 | Neo4j Browser、Memgraph Lab | 有界画布、schema/索引、路径查询、节点/边 Inspector | Graph Beta 使用有界画布和 SQL/PGQ；所有图编辑走 staged preview 与审批 |
| AI/MCP | dbx、Tabularis、WorkBuddy | MCP 是机器入口；默认只读、逐工具权限、安装向导、连接自检和结果预算 | 复用现有 typed Streamable HTTP MCP，增加 stdio bridge、AI Connect 页面和宿主配置生成 |
| 扩展与发布 | dbx plugin store、Tabularis plugin registry | manifest、权限、哈希/签名、独立版本和市场审查 | 先建立 SonnetDB 管理工具 manifest/能力矩阵；插件生态晚于核心工作台，不阻塞 M47 P0 |

## 3. 统一核心模型

### 3.1 统一资源上下文

所有宿主都使用同一组可序列化上下文：

```text
ConnectionProfile
  → ServerInstance
    → Database → ModelObject (measurement/table/collection/keyspace/vector/fulltext/bucket/graph)
               → DatabaseManagementNode (index/backup)
    → InstanceResource (SonnetMQ topic/consumer/runtime/recovery)
  → WorkspaceTab → QueryOrOperation → ResultSnapshot / AuditEntry
```

上下文必须携带原始对象名称、数据库、连接、权限快照、模型类型和最后更新时间。断线时保留最后结果并显示 Offline；恢复连接后只刷新明确失效的分组，不自动重放写操作。

九模型在当前数据库上下文统一呈现。资源的逻辑 `scope` 与物理 `persistenceScope` 分别说明：SonnetMQ Topic 按数据库命名空间和数据库权限访问，Server 当前使用实例共享的 `.system/mq` 消息日志。对象页签包含数据库与 Topic，不能仅按 Topic 名识别；底层目录结构不直接决定前端导航分组。数据库索引与备份是管理节点，不增加模型数量；当前单数据库目录备份尚未覆盖共享 MQ 存储。检查器及备份/恢复流程明确这一限制，不能宣称已有统一快照或跨模型原子恢复。当前原型连接为 `Factory / Local`，数据库为 `factory`，数据与状态均为静态设计示例。

### 3.2 共用工作台外壳

继续使用 M29 的稳定几何：64px 功能轨、56px 顶栏、可调 Explorer、工作区页签、可调 Inspector 和底部结果区。M47 增加三项全局能力：

1. **Command Center**：命令、对象搜索、最近工作区、连接/数据库切换和 AI/MCP 接入统一入口。
2. **Workspace Registry**：保存 SQL、Notebook、对象页签、过滤器、结果快照和权限提示；恢复只恢复上下文与输入，不重放危险操作。
3. **Capability Rail**：按当前权限和模型能力显示“可查询、可编辑、可导入、可治理、可监控”，禁用项提供原因，不用隐藏制造误解。

### 3.3 共用交互原语

- Explorer：分组加载、局部错误、名称搜索、模型图标、数量、只读/离线状态。
- Workspace：对象页签、SQL/Notebook 页签、二级任务页签和深链接。
- Result Plane：Table、Raw/JSON、Chart、Map/Trajectory、Explain、Audit；目标是支持服务端分页/continuation、列筛选、复制、导出、取消和结果预算。当前 SQL NDJSON 与部分对象 API 尚未提供统一 cursor，先由 core 固化结果类型和能力降级，服务端缺口继续归 M36/V45-X01/X05，不用 fixture 宣称真实分页完成。
- Inspector：选中行/文档/key/message/vector hit/graph node 后显示 Header、Metadata、Payload 和适用动作。
- History：按连接、数据库、模型、动作、状态、耗时过滤；恢复只恢复编辑上下文。
- Approval：草稿 → staged preview → 影响范围/风险 → dry-run → 人工确认 → 执行 → 审计。
- Status：Loading、Empty、Partial failure、Read-only、Offline、Truncated、Permission denied 必须有统一语义、文字说明和可操作入口。

### 3.4 AI 与 MCP 共用合同

- Server 保持 `/mcp/{database}` typed Streamable HTTP 和现有只读工具合同。
- 新增 `sonnetdb mcp` stdio bridge 时只代理同一 typed contract，不维护第二套业务实现。
- MCP 工具默认只读，强制 `maxRows`、字节/时间预算和结果截断标识；写入只从 Web Admin/Studio 审批入口进入。
- 管理工具提供 AI Connect：选择数据库、生成 WorkBuddy/Claude/Cursor/Codex 配置、复制或写入用户/项目级配置、连接测试、工具列表和数据外发说明。
- `llms.txt`、`llms-full.txt`、MCP schema、能力清单和权限说明由同一版本发布，避免文档与工具漂移。

## 4. 三种发布面的框架与特殊点

### 4.1 Web Admin：完整治理工作台

Web Admin 是功能完整的旗舰面，负责九模型浏览、查询、编辑、导入导出、索引/策略维护、监控、备份恢复和审批审计。它采用三栏桌面布局、模型专用二级页签和共享结果/审批层。

特殊点：

- 支持完整写操作，但所有危险动作都进入 WriteApproval；确认文案必须包含对象和影响数量。
- 支持多数据库、多窗口上下文、批量任务进度、取消/恢复和审计追踪。
- 提供 AI Connect、Provider/出域设置和工具权限矩阵；AI 结果不能绕过本地权限。
- 承载九模型全部专用工作台和 Graph Beta 的有界维护入口。

### 4.2 Studio：同一 Web 工作台的桌面宿主

Studio 不另做一套页面。它加载同一套 Web Admin 资源，并增加 Windows 宿主能力：本地 data root、托管 Server、原生文件对话框、连接库、Credential Manager、端口/进程生命周期和安装升级。

特殊点：

- 顶部连接菜单显示 Managed Local、data root、Server 版本、Health、Start/Stop 和最近检查时间。
- 导入、导出、对象上传下载和备份优先走 native bridge；bridge 不可用时保留 Web fallback。
- AI 出域必须由显式宿主配置、允许工具清单和短期凭据共同决定。
- 干净 Windows、WebView2、升级/卸载、端口冲突和进程回收属于独立发布证据，不由浏览器 fixture 代替。

### 4.3 VS Code：Remote-first 开发者工作台

VS Code 是数据库开发者入口，负责连接、schema、SQL、EXPLAIN、结果视图、LSP 和只读 Copilot/MCP 调试。它不复制完整治理工作台。

特殊点：

- 连接 profile 在 `globalState`，凭据在 `SecretStorage`；支持 URI/配置导入和健康自检。
- Explorer 只显示可快速理解的 schema 和只读预览；复杂编辑、导入导出、策略和恢复通过深链接转到 Web Admin/Studio。
- 结果视图必须支持 Table/Raw/Chart/Trajectory/Explain 的开发者路径，并有分页、列筛选、复制和文件导出。
- 优先接入稳定的 VS Code Chat/MCP 宿主能力；宿主能力不足时继续使用自有 Copilot Webview，但底层调用转向统一 typed MCP。
- SQL 文件、Notebook 和 Query History 是开发者的可提交资产，连接密钥绝不进入工作区文件。

## 5. 九模型统一工作台与模型专用部分

每个模型都遵循“Explorer 对象 → 工作区任务页签 → 共享结果/Inspector/History/Approval”的骨架；差异只体现在查询器、数据平面、专用操作和验收指标。

| 模型 | 工作区页签 | 必须保留的专用交互 | 三面范围 |
|---|---|---|---|
| 时序 measurement | 概览、点/文件、SQL、图表、Trajectory、Retention | 时间窗、TAG/FIELD、时间轴缩放、downsample、设备轨迹、异常/预测入口 | Web/Studio 完整；VS Code 查询、图表和轨迹只读 |
| 关系表 | 数据、设计器、索引、ER、DDL、导入导出、EXPLAIN | 分页网格、行编辑草稿、约束/索引预览、参数化 SQL | Web/Studio 完整；VS Code SQL/schema/EXPLAIN |
| JSON 文档 | Find、Aggregate、索引、Validator、Change Feed、导入导出 | JSON Tree/Raw、filter/projection/sort、cursor、局部更新预览 | Web/Studio 完整；VS Code Find 只读 |
| KV keyspace | Browse、TTL、批量操作、统计 | 前缀树、类型化值、TTL、Base64/Hex、CAS/原子操作预览 | Web/Studio 完整；VS Code scan/get 只读 |
| SonnetMQ | 概览、消息、消费者组、配置、DLQ | offset/time seek、header/payload、lag、retention、publish/ack 审批 | Web/Studio 完整；VS Code browse/runtime 只读 |
| 向量 | Search、索引详情、Profile、命中 Inspector | raw/embed、Top-K、metric、filter、维度和 Recall 证据 | Web/Studio 完整；VS Code search preview |
| 全文 | Search、Analyzer、索引/重建、统计 | BM25、phrase/fuzzy、highlight、token、重建进度 | Web/Studio 完整；VS Code search/analyze 只读 |
| 对象桶 | Browse、Preview、Multipart、治理、审计 | prefix、metadata、Range、版本、retention、quota、checksum | Web/Studio 完整；VS Code list/metadata 只读 |
| 原生属性图（Graph Beta） | Schema、Canvas、Query、Maintenance、Audit | 有界画布、label/index、节点/边 staged edit、SQL/PGQ、路径证据 | Web/Studio Beta；VS Code SQL/PGQ 和只读快照 |

## 6. 原型重新整理

M29 的原型保留为已实现工作台基线；M47 在 [设计评审包 README](m47-unified-management-workbench/README.md) 中整理“共享骨架 + 模型适配器 + 宿主壳”。当前产物是可交互的 HTML 设计评审稿，目录与任务规范覆盖 7 个一级模块、30 个全局页面、9 个模型和 166 个任务页签（模型任务 60 个），尚待用户确认外轮廓、导航、对话框与状态，再逐页细化最终视觉：

```text
docs/design/m47-unified-management-workbench/
├─ README.md             评审顺序、产物入口与确认记录
├─ navigation.md         七一级、二级页面、路由兼容与资源作用域
├─ layout-spec.md        五区外壳、尺寸、token 与响应式
├─ interaction-spec.md   共享对话框、提示、状态、审批与恢复
├─ screen-specs.md       全局页面、九模型和具体任务规范
├─ prototype/            静态 HTML、页面/任务目录与交互脚本
├─ screenshots/          典型视口与共享流程评审图
└─ validation-report.md  实际检查范围与未验证事项
```

每个关键帧至少有正常、空、加载、局部失败、只读、离线、长内容、无权限和危险确认状态。原型导航必须能从同一个连接上下文打开 Web Admin、Studio 和 VS Code 的等价任务，并明确哪些动作需要跳转宿主。

HTML 能点击、切换示例状态或打开审批对话框，仅证明设计评审路径可见。通用模板不等于逐页最终视觉完成，浏览器截图不替代真实 Server、WebView2、安装、Extension Host、固定硬件或长期运行证据。用户确认原型以前继续完善设计包，不启动 `web/src`、VS Code 或共享资源合同的生产实现。

## 7. 代码与发布组织

实现采用“一个核心、三个壳、一个发布矩阵”：

```text
management-core
  ├─ typed API/MCP client、资源类型、能力矩阵、状态机、结果/审批合同
  ├─ design tokens、可访问性规则、快捷键和文案键
  ├─ model adapters：九模型查询器、结果视图和权限能力
  └─ fixtures/e2e journeys

web-admin-shell      完整治理与浏览器发布
studio-host-shell    Web Admin + native bridge + Managed Local
vscode-shell         Remote-first Explorer + SQL/LSP + 开发者 AI
```

以下是用户确认原型后的迁移候选，不是当前下一步开发指令：将 `web/src/utils/managementExplorer.ts` 包装为 ResourceDescriptor/Capability Registry，保留当前 key 兼容；`web/src/components/ManagementExplorerSidebar.vue`、`SqlQueryWorkspace.vue`、`WorkbenchResultPanel.vue`、`WriteApprovalPanel.vue`、`WorkbenchHistoryDrawer.vue` 和 `CopilotDock.vue` 作为第一批共享组件候选。设计令牌从现有 `docs/design/m29-workbench-redesign/design-system.md` 与 `web/src/styles/studio-workbench.css` 抽取为 JSON/CSS/VS Code 可消费的版本化资产。结果模型要从当前一次性 rows payload 演进为 `{ columns, rows, cursor, hasMore, truncated, metrics, error }`，再按服务端能力接 continuation 和虚拟网格；VS Code 只消费 contracts/core/tokens 与自身 Webview 适配器，不复制 Vue 页面。

发布统一品牌为 **SonnetDB Workbench**，三个发行物分别为 **SonnetDB Web Admin**（Web 静态资源/容器）、**SonnetDB Studio**（Windows 安装包，后续再评估跨平台宿主）和 **SonnetDB for VS Code**（VSIX）。三者使用同一版本号、API/MCP contract version、`uiContractVersion`、`server capabilities`、`capabilities.json` 和兼容矩阵，分别产出 Web 静态资源、Studio 安装包、VSIX 与 CLI/MCP 资产；每个宿主独立签名、安装和取证。任何一个宿主未通过自己的安装或 Electron/Extension Host 验证，都不能把三面整体标成发布完成。

## 8. M47 工作包与验收

| ID | 优先级 | 交付 | 退出条件 |
|---|---|---|---|
| M47-U01 | P0 | 竞品矩阵、完整菜单/导航、五区外壳、共享对话框/提示、三面边界与资源合同设计 | 用户确认原型；逐页细化最终视觉后再生产实现，不重复声明 M29 已实现内容 |
| M47-U02 | P0 | management-core、设计令牌、状态/权限/结果/审批共用合同 | Web/Studio/VS Code 至少各有一个真实入口消费同一合同 |
| M47-U03 | P0 | 统一 Explorer、Workspace、Result Plane、Inspector、History | 九模型对象可从同一上下文打开；分页/取消/截断语义一致 |
| M47-U04 | P0 | 九模型 adapter 和模型专用工作台矩阵；Graph adapter 明确标记 Graph Beta | 每模型正常/空/错/只读/长内容状态均有实现或明确拒绝；不把 M29 八模型原型状态升级为九模型已完成 |
| M47-U05 | P0 | Web Admin 完整工作台收口 | 查询、编辑、导入、治理、监控、审批和审计串成真实旅程 |
| M47-U06 | P0 | Studio 桌面壳、native bridge 和安装/升级证据 | 干净 Windows、WebView2、端口冲突、卸载保留和进程回收通过 |
| M47-U07 | P1 | VS Code 连接向导、Notebook/结果增强、Chat/MCP 接入 | Extension Host 真实旅程通过；复杂写操作可深链接转交治理面 |
| M47-U08 | P1 | WorkBuddy/Claude/Cursor/Codex AI Connect 与 stdio bridge | HTTP/stdio 配置、自检、只读工具、凭据/出域说明和取消均可复现 |
| M47-U09 | P1 | M47 原型目录、页面/任务规范、验证矩阵和三面发布矩阵 | HTML 评审稿与逐页最终视觉分开记录；关键帧、状态、视口、权限和真实版本证据齐全 |
| M47-U10 | P2 | manifest、签名/哈希、插件/连接器目录和市场提交流程 | 安全审查、可撤销权限、升级/回滚和来源信息可验证 |

M47 不改变九模型存储语义、SQL 名称合同、MCP 只读边界或 M29 已有审批规则。真实容量、恢复、模型质量、Studio 安装和长期运行仍按 M19/M25/M27/M29/M35/M36/M40/M41/M42/M43/M44/M45/M46 的独立门禁验收。

## 9. 发布完成判定

1. 三个宿主使用同一资源、权限、结果、审批和设计令牌合同，并能从同一连接上下文完成对应任务。
2. 九模型都有模型专用工作区；模型差异进入 adapter，不通过复制外壳制造三个产品分支。
3. Web Admin 的治理能力完整，Studio 的原生宿主能力可验证，VS Code 的开发者边界清晰且可深链接转交治理面。
4. MCP/AI 接入默认只读，结果预算、凭据隔离、数据外发和工具状态可见；WorkBuddy 等宿主有可复现的安装与自检路径。
5. 原型、真实实现、Extension Host/Electron、干净安装、固定硬件和长期运行证据分开记录；任何局部 PASS 不升级为三面整体发布。

## 10. 参考资料

- [dbx](https://github.com/t8y2/dbx)：统一桌面/Web/CLI/MCP、插件权限和 AI 查询入口。
- [Tabularis](https://github.com/TabularisDB/tabularis) 与 [Tabularis MCP 文档](https://tabularis.dev/wiki/mcp-server)：SQL Notebook、Visual Explain、JSON-RPC 插件和客户端一键配置。
- [DBeaver 文档](https://dbeaver.com/docs/dbeaver/)、[DataGrip 文档](https://www.jetbrains.com/help/datagrip/)、[pgAdmin 文档](https://www.pgadmin.org/docs/pgadmin4/latest/)：连接树、查询标签、数据网格、Explain、ER 和导入导出。
- [MongoDB Compass](https://www.mongodb.com/docs/compass/)、[RedisInsight](https://redis.io/docs/latest/operate/redisinsight/)、[Grafana Explore](https://grafana.com/docs/grafana/latest/explore/)：文档、KV 和时序专用工作台。
- [Kafka UI](https://github.com/provectus/kafka-ui)、[RabbitMQ Management](https://www.rabbitmq.com/docs/management)、[MinIO Console](https://min.io/docs/minio/linux/administration/minio-console.html)：消息、对象、审计和治理交互。
- [Milvus Attu](https://milvus.io/docs/attu.md)、[Qdrant Web UI](https://qdrant.tech/documentation/interfaces/web-ui/)、[OpenSearch Discover](https://docs.opensearch.org/latest/dashboards/discover/)、[Neo4j Browser](https://neo4j.com/docs/browser-manual/current/)：向量、全文和图工作台。
- [WorkBuddy MCP 中文指南](https://www.workbuddy.ai/docs/zh/workbuddy/From-Beginner-to-Expert-Guide/Function-Description/MCP-Guide) 与 [默认 Connector 透明度说明](https://www.workbuddy.ai/document/default-mcp-connectors)：用户级/项目级 MCP 配置、市场和自托管服务器责任边界。
