# M47 导航与信息架构

**状态：设计评审稿，尚未授权生产前端实现。** 本文件定义七个一级模块、完整页面目录、资源作用域和三宿主导航边界。页面名称、页面 ID、设计状态以 [原型目录](prototype/catalog.js) 为准；静态原型中的数据与动作是设计示例，不构成真实服务或发布证据。

原型默认连接为 **Factory / Local**，默认数据库为 **factory**。连接显示名、服务地址与数据库名称是不同字段，不把同名连接或 URL 当作实例身份。

## 1. 继承 M29 的外轮廓

M47 延续 [M29 交互规范](../m29-workbench-redesign/interaction-spec.md) 和 [设计系统](../m29-workbench-redesign/design-system.md)，采用 icon rail、资源树、工作区页签、Inspector 和结果区组成的数据库工具外壳。五区职责保持稳定：

| 区域 | 职责 | 导航边界 |
|---|---|---|
| 全局区：顶部栏与 icon rail | 顶栏呈现连接、实例、作用域、命令搜索、健康与身份；64px rail 切换七个一级模块 | 不展示每个模型的编辑动作，不把 Studio 宿主做成重复一级模块 |
| 资源区：Explorer | 展开连接、实例、数据库、九模型对象和管理节点；搜索、分组加载与局部错误 | 资源树不重复全局功能菜单；SonnetMQ Topic 按逻辑数据库分组，物理恢复边界另行说明 |
| 工作区：页签、任务导航与内容 | 保留 SQL、Notebook、对象和治理任务；二级标签描述当前任务 | 对象任务不另造独立产品外壳；同一对象默认复用页签 |
| 详情区：Inspector | 展示选中记录、文档、key、消息、命中、图元素的 metadata 与 payload | 格式切换是局部视图；复杂编辑进入草稿任务，不把全部表单放进 Inspector |
| 结果与状态区 | 呈现结果、预算、截断、取消状态与当前执行上下文；历史和审批按需打开 | Table/Raw/JSON/Chart/Trajectory/Explain 是结果视图，不重复为一级菜单 |

56px 顶栏、可调整 Explorer/Inspector、底部结果区和响应式折叠继续使用 M29 语义。原型的面板宽度可随评审视口调整，不能据此把现有宿主重新拆成三个产品。Web Admin 与 Studio 共用页面核心；VS Code 复用资源、合同和状态语义，由扩展宿主呈现。

## 2. 七个一级模块与二级页面

一级固定为 **概览 / 工作台 / 观测 / 数据流 / AI 与 MCP / 治理 / 设置**。模型对象从工作台 Explorer 打开，不再增加“查询 / 数据 / Studio”三个同义入口。以下 30 个页面与原型目录逐项对应。

| 一级 / ID | 二级页面 / ID | 具体职责与任务标签 | 作用域 | 状态 |
|---|---|---|---|---|
| 概览 `overview` | 实例概览 `summary` | 总览、资源分布、待处理事项；从连接健康和实例摘要进入工作 | instance | extension |
| 概览 | 数据库目录 `database-catalog` | 可访问数据库、最近访问；查看权限与资源摘要，进入数据库或新建草稿 | instance | extension |
| 概览 | 连接管理 `connections` | 连接库、连接测试、凭据说明；管理宿主 profile、默认数据库与测试结果 | host | extension |
| 概览 | 最近工作区 `recent` | 最近、已固定、离线快照；仅恢复页签、输入、过滤器与已有快照 | host | planned |
| 工作台 `workbench` | SQL 工作区 `sql` | 编辑器、参数、查询说明；手动执行、Explain、结果视图和写操作预览 | database | existing |
| 工作台 | SQL Notebook `notebook` | 单元、大纲、版本与导出；组合说明、SQL 和结果快照 | database | planned |
| 工作台 | 执行历史 `history` | 查询、模型操作、审批记录；按原连接与范围恢复输入 | host | extension |
| 观测 `observe` | 性能指标 `metrics` | 吞吐与延迟、内存与存储、AI 用量；按实际指标能力解释采样窗口 | instance | existing |
| 观测 | 实时事件 `events` | 事件流、数据库事件、连接状态；暂停显示与保留已接收事件 | instance | existing |
| 观测 | 慢查询 `slow-queries` | 慢查询列表、选中 SQL、解释入口；复用事件与 SlowQueryDrawer，手动转入 SQL | instance | extension |
| 观测 | 告警规则 `alerts` | 规则、评估记录、通知路由；设计阈值与窗口，接口具备后才允许保存 | instance | planned |
| 观测 | 运行时诊断 `runtime` | 运行时、诊断采集、预算与取消；汇总各模型已暴露诊断，保留来源与时间 | instance | planned |
| 数据流 `flows` | Modbus TCP `modbus` | 运行时、待审批写入、审计；管理协议源、端点与数据库/表绑定的任务入口 | instance | existing |
| 数据流 | 导入任务 `imports` | 任务列表、字段映射、校验与错误；跨模型索引已有导入任务，转入具体目标 | database | extension |
| 数据流 | 对象传输 `transfers` | 传输、Multipart 会话、校验与错误；汇总上传下载及分片状态 | database | extension |
| 数据流 | 任务与位点 `jobs` | 任务索引、位点、重试与恢复；聚合任务引用，不取代各模型 resume 合同 | instance | planned |
| AI 与 MCP `ai` | AI Connect `ai-connect` | 连接向导、配置预览、工具列表、自检、数据外发；WorkBuddy/Claude/Cursor/Codex 配置设计 | database | planned |
| AI 与 MCP | Copilot 与 Provider `copilot-settings` | Provider、账号绑定、模型目录、用量、测试；复用已有配置与在线模型入口 | instance | existing |
| AI 与 MCP | RAG 管理 `rag` | 已发布快照、持久任务、派生重建、退役清理、审计；复用已有任务与版本发布 | database | existing |
| AI 与 MCP | 工具权限与外发 `tool-permissions` | 工具权限、结果预算、数据外发、调用记录；解释当前身份、工具与宿主策略共同限制 | database | planned |
| 治理 `govern` | 用户 `users` | 用户列表、选中用户授权、凭据管理；复用用户创建、修改与删除 | instance | existing |
| 治理 | 数据库授权 `grants` | 授权列表、授权预览、有效权限；预览授权或撤销对身份、数据库与工作区的影响 | instance | existing |
| 治理 | 访问 Token `tokens` | Token 列表、创建、安全说明；发放和撤销凭据，新值仅创建时展示 | instance | existing |
| 治理 | 审批与审计 `approvals` | 待处理、影响预览、执行记录、审计；索引现有 staged 操作与模型审计，标明每条记录范围 | instance | extension |
| 治理 | 数据库备份与恢复 `backup` | 备份状态、备份预览、验证、恢复预览、范围说明；单库目录边界和覆盖目标确认 | database | extension |
| 设置 `settings` | 工作台偏好 `preferences` | 外观、编辑器、结果、快捷键、工作区；当前宿主本地偏好，不扩大服务器预算 | host | planned |
| 设置 | 实例配置 `server-settings` | 服务配置、预算、可观测性、重启影响；接口缺失时仅显示配置说明 | instance | planned |
| 设置 | Studio 宿主 `studio-host` | 宿主状态、Managed Local、文件与凭据、安装与升级、进程生命周期；复用 native bridge | host | extension |
| 设置 | 能力与发布矩阵 `capability-matrix` | 宿主能力、契约版本、兼容性、发布证据；区分真实能力与待实现项 | host | planned |
| 设置 | 关于与帮助 `about` | 产品信息、指南、快捷键、许可证；统一品牌和版本摘要 | host | existing |

实例页面可以包含数据库筛选或跨数据库摘要；筛选不改变其管理作用域。数据库页面在打开时必须绑定一个具体数据库。host 页面读取宿主 profile、偏好或历史，不据这些本地数据推导服务器授权。

## 3. 连接、实例、数据库与模型对象

九模型统一使用连接 → 实例 → 数据库 → 模型对象的逻辑导航。资源的名称/权限作用域与物理持久化/恢复作用域是不同合同，不能用底层日志的位置决定用户应在哪个对象分组中工作：

```text
ConnectionProfile（原型：Factory / Local）
└─ ServerInstance（身份与版本来自实际服务能力；原型不伪造实例 ID）
   ├─ Database（原型：factory）
   │  ├─ 时序 / 关系 / 文档 / KV / SonnetMQ / 向量 / 全文 / 对象 / Graph Beta
   │  ├─ 数据库索引（生命周期管理节点）
   │  └─ 数据库备份（状态与恢复管理节点）
   └─ 实例治理与运行时（含 MQ 物理配置与整体快照/恢复边界）
```

九模型是数据语义分类，九类对象均在逻辑数据库上下文下浏览；索引和备份是管理节点，不计为额外数据模型。向量和全文对象须保留所属 measurement/column 或 collection/index 的结构身份，MQ 必须保存数据库和 Topic 两个组成部分，不把显示用点号或冒号作为无歧义解析方式。

切换连接先保留草稿和工作区，随后重新核对实例、数据库与权限。MQ 工作区显示“factory / DeviceEvents”，遵循当前数据库的 Read/Write 授权；切换数据库意味着切换逻辑 Topic 命名空间，不能把同名 Topic 视为原对象。MQ 的配置或恢复说明同时标注“物理存储：实例”，不把逻辑数据库读取权限当作整体实例恢复的执行权限。

当前实现证据明确区分两个作用域：`EndpointRequestSupport.cs` 的 `EvaluateMqAccess` 校验数据库存在性和有效 Read/Write 权限（100–122 行），`QualifyMqTopic` 用 `db + "." + topic` 生成队列内部名称（223 行）；`MessageQueueEndpoints.cs` 通过同一个 `SonnetMqStore` 发布或读取限定后的 Topic（29–42、117–138 行）。因此数据库是实际逻辑名称和权限边界，不能把它淡化为无意义的历史 URL 参数。

Server 在 `SonnetDbServiceRegistration.cs` 为 MQ 注册 DI 单例（91–99 行），目录为 `<DataRoot>/.system/mq`（96、308–313 行）。`SonnetMqStore.cs` 使用自有 append-only 二进制记录和 FileStream（927–952、1515–1524 行），并未调用 KV 或关系表存储；消息、ACK/NACK 和 offset 状态按 MQ 合同持久化。消息/事件可以与 KV、关系、文档等模型形成应用工作流；是否复用底层存储是后端架构决策，不要求导航拆成独立产品。

资源合同设计分别保留 `scope: 'database'` 与 `persistenceScope: 'instance'`，MQ 同时携带 `database` 和 `topic`。这些字段是 M47 规划，尚未迁移生产 ResourceDescriptor 或存储布局。`BackupService.CreateAfterCheckpoint` 复制单个 `tsdb.RootDirectory`（363–378 行）；MQ 的 `CreateSnapshot` 则在实例级锁下复制队列目录并生成独立 manifest（`SonnetMqStore.Snapshot.cs` 14–65 行），恢复校验文件长度与 SHA-256（99–141 行）。单数据库备份不能宣称包含物理 MQ；整体快照/恢复入口绑定真实实例、完整队列目录和独立验证，不改变 Topic 的逻辑数据库分组。

数据库索引节点汇总现有 index lifecycle DTO，保留 owner、model、kind、state、rebuildable 和备份包含信息；进入对应模型索引任务或维护预览，不建立第二套索引操作。数据库备份节点展示 schema 的 backupStatus，转入“数据库备份与恢复”；节点存在或 `backupCapable` 为真均不代表当前身份有执行权限，也不代表完整恢复门禁通过。

## 4. 九模型对象工作区与二级任务

以下名称、对象示例和状态与 `catalog.js` 的九个 model 描述一致。二级任务可在同一工作区内链接现有 SQL、结果、检查器或审批，页面级 `existing` 不代表每一个新排列的二级标签都已独立接线。

| 模型 / ID | 原型对象 | 作用域 | 对象工作区二级任务 | 状态 |
|---|---|---|---|---|
| 时序 Measurement `measurement` | `Telemetry` | database | 数据点、SQL、图表、轨迹、Schema、文件导入、保留策略 | extension |
| 关系表 `table` | `Assets` | database | 数据、设计器、索引、ER 图、DDL、导入 / 导出、Explain | existing |
| JSON 文档 `document` | `Manuals` | database | Find、文档、Aggregate、索引、Validator、Change Feed、导入 / 导出 | existing |
| KV Keyspace `kv` | `DeviceState` | database | 浏览、值检查器、TTL、批量操作、统计、操作历史 | extension |
| SonnetMQ `mq` | `DeviceEvents` | database；物理持久化 instance | 概览、消息、消费者组、配置、恢复边界、DLQ · 能力依赖、审计 | extension |
| 向量索引 `vector` | `ManualEmbeddings.Embedding` | database | Search、数据 / 导入、索引参数、Profile、命中详情、质量证据 | extension |
| 全文索引 `fulltext` | `Manuals.SearchIndex` | database | Search、Analyzer、索引、重建、数据导入、统计 | extension |
| 对象 Bucket `bucket` | `Evidence` | database | 浏览、预览、上传 / 下载、Multipart、版本与治理、图片语义、审计 | existing |
| Graph Beta `graph` | `FactoryTopology` | database | Canvas、Schema / 索引、SQL / PGQ、受限编辑、导入 / 导出、维护、审计 | extension |

Graph 在 Explorer、对象页签、详情和能力矩阵持续标记 Beta。Canvas 保留节点/边预算、截断和取消说明；SQL/PGQ 转入共用查询器；受限编辑和维护继续经过 staged preview 与有效审批。DLQ、保留策略、Profile、质量证据等新任务仅按实际服务能力启用；静态示例不代表真实路径、Recall、相关性、容量或恢复验收。

“数据流”中的导入任务与对象传输是跨任务索引；对象工作区内的文件导入、上传 / 下载与 Multipart 是具体对象的执行入口。两者共享任务引用、状态与返回路径，不重复生成上传或导入。“观测”中的性能指标、事件、慢查询和诊断是实例索引；模型实时监控、Change Feed、Graph 诊断仍保留模型专用含义。

## 5. 现状路由映射与兼容

生产主壳为 `web/src/views/AppShell.vue`，路由为 `web/src/router/index.ts`，本设计阶段不修改这些文件。当前导航存在多个标签共用路由：查询、数据、Studio 都进入 `sql`；Copilot 与管理员的设置都进入 `ai-settings`，普通用户的设置回到 dashboard。新七模块是设计目标，不能把已有同义按钮的数量当作独立页面交付数量。

| 当前入口 / 路由 | M47 归属 | 保留与迁移方式 |
|---|---|---|
| 概览 `/admin/app/dashboard` | 概览 → 实例概览 | 保留入口，逐步增加数据库目录与事项链接 |
| 查询 / 数据 / Studio `/admin/app/sql` | 工作台 → SQL 工作区及对象工作区 | 收敛一级标签；保留 SQL、对象 tool 与 legacy node key 的兼容读取 |
| 事件 `/admin/app/events` | 观测 → 实时事件；慢查询 | 事件与慢查询仍按来源区分；链接查询输入不自动执行 |
| 监控 `/admin/app/monitoring` | 观测 → 性能指标 | 保留当前真实指标入口；新增标签逐项适配 |
| Modbus `/admin/app/modbus` | 数据流 → Modbus TCP | 保留管理员边界与现有审批 |
| RAG `/admin/app/rag` | AI 与 MCP → RAG 管理 | 复用快照、持久任务、重建与清理 |
| Copilot / 管理员设置 `/admin/app/ai-settings` | AI 与 MCP → Copilot 与 Provider | 保留配置入口；“设置”获得独立偏好/宿主职责 |
| 用户 `/admin/app/users` | 治理 → 用户 | 保留真实服务端管理权限要求 |
| 权限 `/admin/app/grants` | 治理 → 数据库授权 | 保留数据库范围及 MQ Topic 现有 Read/Write 判定；不据此授予整体实例恢复权限 |
| Token `/admin/app/tokens` | 治理 → 访问 Token | 保留创建时一次性显示与撤销合同 |
| 关于 `/admin/app/about` | 设置 → 关于与帮助 | 统一产品/宿主/服务版本摘要 |
| `/admin/app/studio`、`/admin/app/databases` | 工作台兼容入口 | 当前都重定向 `sql`；实现后仍接收旧链接 |
| `/admin/app/trajectory-map` | 工作台 → 轨迹任务 | 当前重定向 `sql?tool=trajectory`；保留已有 query 合同 |
| `/admin/app/copilot-test` | AI 与 MCP 的诊断入口 | 保留管理员诊断路由，不升格为第八个一级模块 |
| `/admin`、`/admin/app` | 概览入口 | 当前默认重定向 dashboard；不在原型阶段改写 |
| `/`、setup/login/auto-login、OAuth callback | 主壳外入口 | 官网、认证、初始化与专用回调保持独立流程 |

现有 `tool=measurement/table/document/kv/mq/vector/fulltext/bucket/graph/trajectory`、model 和 node 参数先兼容读取。measurement 的原始名称、`table:`、`document:`、`kv:`、`vector:`、`fulltext:`、`mq:`、`bucket:`、`graph:` 前缀键、原始 index id 与 `backup-status` 均不得在视觉迁移中静默改写。新资源 identity 必须另携带 kind、原始名称与结构组成部分，以连接/实例/作用域消歧；旧 key 自身可能碰撞，不能按前缀猜测就声称已精确绑定。

## 6. 页签、深链接、返回与草稿

页面目录 ID 与模型 ID 是导航基准。对象页签 identity 包含连接 profile、实际实例、逻辑 scope、数据库、资源 kind 和结构 identity；显示名不是主键。MQ identity 必须包含逻辑数据库和 Topic，例如 `factory / DeviceEvents` 与 `analytics / DeviceEvents` 是不同对象；`persistenceScope: 'instance'` 不允许省略数据库。一个对象默认复用页签，SQL/Notebook 可明确另开。整体实例恢复是独立治理任务，不借用 Topic 页签 identity 执行。

深链接分为全局模块/页面、对象上下文、二级任务和局部选择。二级任务通过版本化 query 参数保存，浏览器前进/后退恢复当前任务，不自动执行查询、导入、发布、ack、删除或维护。页面可以保存过滤器和 Inspector 状态；凭据、审批执行权和完整生产 payload 不写入 URL。原型中切换页面只验证信息架构，生产 URI scheme 与 query 命名待合同冻结后实现。

从慢查询、历史、任务索引、索引管理或审批进入对象时，记录来源页面和 scope，提供“返回来源”操作；返回保留来源的筛选、滚动与选中项。历史恢复只恢复输入；结果快照显示原连接、范围、采样时间与 Offline/Truncated 状态。断线后保留最后结果，重新连接只刷新已失效分组，不重放写操作。

离开或关闭有未提交草稿的页签时，提供“保留草稿 / 丢弃草稿 / 返回编辑”。连接、身份、对象版本或目标变化后，原 staged preview 标为失效并重新预览；原批准不能转移到新连接或对象。执行失败保留草稿和错误；成功后链接结果、服务器审计和受影响对象。本地执行历史、草稿和服务器审计各自标明来源，不合并成伪造的统一持久审批队列。

## 7. 三宿主边界与权限呈现

| 领域 | Web Admin | Studio | VS Code |
|---|---|---|---|
| 导航呈现 | 七模块、Explorer、完整治理任务 | 同一 Web 工作台加宿主入口 | Remote-first tree、编辑器、结果/Webview；不复制完整七模块 DOM |
| 连接与凭据 | 浏览器会话与当前可用安全机制 | 连接库及 Windows 安全凭据宿主能力 | globalState 保存 profile，SecretStorage 保存密钥 |
| 模型任务 | 九模型浏览、查询、编辑、治理与审批 | 同一模型页面与状态合同 | SQL/schema、Explain、模型只读预览；复杂编辑、策略、导入及恢复深链接转交 |
| 文件与本地进程 | 浏览器文件选择/保存 | native bridge、data root、Managed Local、原生文件和目录对话框 | 工作区文件与扩展 API；新增统一导航以 Remote-first 为目标 |
| AI/MCP | typed HTTP MCP、默认只读、配置与外发说明 | 同合同加显式宿主配置和凭据边界 | 宿主 Chat/MCP 或自有 Webview 适配；密钥不进入可提交资产 |
| 发布证据 | 真实浏览器与服务旅程 | 干净 Windows、WebView2、升级/卸载、归属进程回收 | Extension Host、真实连接与查询旅程 |

VS Code 已有的 Managed Local、createMeasurement、bulkImport 等历史命令不能仅凭新设计宣称被移除或已经统一为只读；后续实现须逐项评审迁移与转交行为。共享资源能力描述说明“宿主支持此功能”，服务端权限说明“当前身份获准执行”；两者分开。权限未知时显示“待核对”，明确拒绝时显示原因，任何客户端开关不能扩大服务器授权。

## 8. 页面状态与实施门禁

| catalog 状态 | 定义 | 不能据此得出的结论 |
|---|---|---|
| `existing` | 已有真实页面或模型任务基线，M47 复用其能力与入口 | 不代表原型每个重排标签已有独立实现，也不代表三宿主真实验收通过 |
| `extension` | 在既有基线上扩展导航、聚合入口、作用域或交互合同 | 不把部分已有功能重新描述为空壳，也不把新增聚合能力称为已交付 |
| `planned` | 当前完整入口或统一合同尚待实现，原型提供可评审设计 | 不代表按钮执行了网络请求、保存配置、发出通知或完成 stdio bridge |

上述状态是页面交付来源；Loading、Empty、Partial failure、Read-only、Offline、Truncated、Permission denied 是运行状态，必须另行呈现。原型示例值、浏览器 fixture、真实 Server、Studio 安装、Extension Host、固定硬件和长期运行证据分开记录。

本轮顺序固定为：**完整导航与页面目录 → 外轮廓/对话框/状态设计 → 可交互原型 → 用户确认原型 → 生产代码切片**。用户确认原型以前，仅修改设计文档与静态原型，不启动生产前端实现。确认后再冻结资源 identity、深链接、权限/能力、结果、审批和宿主合同，并按单一职责切片迁移旧入口；确认视觉不等同认可未实现的服务 API 或发布门禁。

设计评审至少核对七模块和全部 30 页面、九模型任务、数据库索引/备份节点、实例 MQ 恢复边界、空/错/只读/离线/长内容、草稿返回与危险确认。实现阶段逐项验证 legacy 链接、原始名称大小写、跨连接身份、服务端拒绝、预算/取消/截断和真实宿主旅程；局部通过不能升级为 M47-U02/U03/U04 或三面发布整体完成。
