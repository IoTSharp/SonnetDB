# M47 页面、导航与任务规格

初版日期：2026-10-04；MQ 范围核查修订：2026-10-05（Asia/Shanghai）。状态：**设计评审原型，尚未实施生产前端改造**。本文与 `prototype/catalog.js` 定义完整菜单、页面任务和九模型二级页签，供原型确认后的实现使用。所有记录、数量、延迟、命中分数和状态均为静态设计示例，不是网络自检、性能、质量、恢复或发布证据。

## 设计顺序与范围

先确认整体轮廓、一级导航、二级导航与全局交互，再审查每页任务，最后才进入生产前端的像素实现。本次沿用现有浅色 Fluent 工业数据库 IDE：56px 顶栏、64px 功能轨、可调 Explorer、中央 Workspace、可调 Inspector、底部 Result Plane 与状态条；不重新定义数据库模型。

默认示例连接统一为 `Factory / Local`，地址 `http://127.0.0.1:5080`，当前数据库 `factory`，其它示例数据库 `analytics` 与 `sandbox`。连接、页签、对象表与 Inspector 使用同一上下文，避免显示目标漂移。

一级导航仅保留七项：**概览、工作台、观测、数据流、AI 与 MCP、治理、设置**。原有“查询 / 数据 / Studio”都跳到 SQL 的重复入口收敛为工作台。工作台二级任务只有 SQL、Notebook、执行历史；九模型对象是同一数据库资源树中的对象上下文，不增加九个全局模块。选中 MQ 时顶部保留“factory / DeviceEvents”，Inspector 另注物理 Store 为实例共享 `.system/mq`，不能把物理存储范围混同为用户导航范围。

SonnetMQ 的 `/v1/db/{db}/mq` 路由按数据库 Read/Write 权限校验，`QualifyMqTopic(db, topic)` 为 Topic 加入数据库逻辑命名空间。原型据此统一九模型数据库导航：MQ `scope: database`、`persistenceScope: instance`。当前 MQ 使用其既有实例共享 Store；该归类不表示改用 KV/关系存储，也不把可观测性事件/SSE 当作消息模型。单数据库备份尚未覆盖该共享 Store，恢复/全局配置可能影响实例内其它数据库 Topic，必须独立核验合同、权限与影响。

顶栏负责产品、连接、数据库/实例范围、命令入口、通知与身份；左侧负责一级目的地及当前区域二级任务/资源；中央负责输入、对象任务、结果；右侧只解释当前选中项与上下文；底部负责结果预算、取消、状态和后台任务。功能轨切换不会关闭对象页签；连接切换先保留草稿，恢复只恢复输入和过滤器，不重放写操作。命令中心、工作区注册表、部分偏好为规划能力，不能通过可点击原型推断生产代码已存在。

## 实现标签与导航规则

| 标签 | 判定 | 原型使用方式 |
|---|---|---|
| `existing` / 既有 | 有真实路由或组件任务入口 | 沿用业务任务，统一外壳与呈现；不声明全新功能 |
| `extension` / 延伸 | 现有组件/接口被重组，或增加页面级编排 | 原型展示目标形态；新增导航、共同 DTO、状态编排仍需实现 |
| `planned` / 规划 | 尚无当前统一管理入口或接口合同 | 可评审输入/步骤，说明后端与宿主依赖，不能显示“已执行” |

`catalog.js` 的 `status` 是整页/整个模型设计标签。下文逐页签的标签更精确；一个模型标“延伸”不代表其已有浏览/编辑能力缺失，也不代表其新增页签已经完成。`existingRoute` 指向已核实的基线路由，未来新导航不能把它当作已实现的新 URL。现有数据库入口会重定向 SQL；连接、历史、备份原本是 SQL 工作区内的对话框/抽屉。

正常、加载、空、局部失败、只读、离线、截断、无权限、长内容与危险确认均复用外壳状态合同。数据加载失败时保留上次快照并标记时间；权限不足禁用动作并说明原因；截断必须显示预算与“未返回全部”；用户取消须区分“请求已取消”和“服务器动作终态”。结果的 continuation/分页只有真实 API 支持才显示下一页。

## 概览：四个二级页面

| 页面 / ID / 标签 | 主任务与页面结构 | 关键字段、动作与边界 |
|---|---|---|
| 实例概览 / `summary` / 延伸 | 上方连接与待处理摘要，中部数据库表，右侧选中数据库与实例 Inspector，下方最近工作入口。避免用大卡片吞掉资源上下文。 | 数据库数、最近访问、待处理项、健康检查时间；主动作打开工作台。复用 Dashboard 路由与已存在列表。健康只能来自明确响应，不把列表“可见”表示为全面健康。 |
| 数据库目录 / `database-catalog` / 延伸 | 筛选条 → 可访问数据库表 → 选中数据库九模型资源摘要。新建/删除是同一页的受控入口。 | 名称、权限、资源、Segment。新建仅管理员，删除先预览影响并输入精确对象名称。MQ Topic 计入逻辑数据库资源；物理 Store 为实例共享，不能据此假设单库删除/备份已统一处理其数据。 |
| 连接管理 / `connections` / 延伸 | 连接库为主列表，选中后侧边编辑；连接测试作为独立步骤，凭据说明单独页签。 | 名称、URL、默认数据库、认证来源、宿主、测试时间。保存 profile 不等于测试通过。切换连接保存工作区草稿；凭据在宿主安全存储或当前会话，不进入可共享文件。 |
| 最近工作区 / `recent` / 规划 | 最近 / 固定 / 离线快照三个过滤页签，列表显示工作区与未提交标识，Inspector 展示恢复内容。 | 连接、数据库、对象、输入、过滤器、快照时间。主动作“恢复选中工作区”；恢复后重新校验身份/能力。Workspace Registry 尚待实现，禁止自动重放 SQL/写入。 |

**WB-02C 全局目录与连接状态合同（`database-catalog`、`connections`）：** 两页保留实例/宿主范围，但选中数据库后进入九模型统一数据库上下文。MQ Topic 的 identity 始终包含 `database + topic`，逻辑作用域为 database；其物理 Store 为实例共享 `.system/mq`，当前单库备份不含该 Store。连接测试将 URL/服务健康、认证和数据库/对象权限分开报告；保存 profile 不等于测试或授权通过，凭据不进入共享工作区。

| 页面 | 能力字段（状态） | 六态主动作与边界 |
|---|---|---|
| 数据库目录 | `database-catalog-read`（既有）读取可访问数据库与九模型摘要；`database-create-preview`、`database-delete-preview`（延伸）只提供管理员草稿和影响预览；`database-mq-context`（既有）保留 MQ 的 database + topic identity 与实例持久化说明。 | 正常“打开选中数据库”；空“查看当前权限”并保留连接/筛选；错误“检查并重试”只替换目录表；无权限隐藏数据库与资源载荷；只读允许打开与浏览、禁用新建/删除；长目录按服务端分页/字节预算，不能虚构全量数量或 continuation。 |
| 连接管理 | `connection-profile`（既有）保存 profile 与宿主安全存储边界；`connection-health-check`、`connection-auth-check`（延伸）分别报告健康与认证；`connection-object-permissions`（规划）未知时明确拒绝；`connection-host-adapter`（延伸）区分 Web/Studio/VS Code。 | 正常“测试连接”并分别显示三类结果；空“添加连接”；错误“查看测试步骤”保留草稿且禁止标记已验证；无权限“查看权限要求”并隐藏对象数据；只读查看状态、禁用保存凭据/改默认数据库；长能力列表折叠并标记截断，不当作完整支持矩阵。 |

## 工作台：三个二级页面

| 页面 / ID / 标签 | 主任务与页面结构 | 关键字段、动作与边界 |
|---|---|---|
| SQL 工作区 / `sql` / 既有 | 工作区页签 → 数据库/预算与执行工具条 → 编辑器/参数 → 可调 Result Plane。对象资源树和右侧当前行/Explain Inspector 常驻。 | 对象名称保留原始拼写；未引用名称不区分大小写、双引号精确绑定。执行选中/整段/EXPLAIN；Table、Raw、Chart、Trajectory、Explain 按结果能力开放。写语句转入既有 staged preview 与审批。SQL NDJSON 不被包装为已实现通用 cursor。 |
| **WB-02 状态合同** | 页面能力：`existing` 的读取/Explain；`extension` 的写语句 staged preview；`planned` 的通用 continuation。专用字段为数据库、结果上限、超时预算。 | 正常主动作“执行查询”；空结果主动作“调整筛选”并保留 SQL/参数；错误主动作“检查并重试”且只替换结果面板；无权限隐藏载荷并进入权限说明；只读允许查询/Explain/导出、禁用写确认；长 SQL/结果按行与字节预算折叠，不能虚构 continuation。 |
| SQL Notebook / `notebook` / 规划 | 标题与连接 → 说明/SQL 单元连续排布 → 当前单元结果，左侧可切大纲，右侧解释选中单元。 | 单元类型、输入、执行状态、结果快照、保存版本。先支持手动运行当前单元；“全部运行”需要中止与写审批合同后再开放。导出排除凭据，生产结果快照需显式选择。当前路由无 Notebook 入口。 |
| 执行历史 / `history` / 延伸 | 连接/数据库/动作/状态/时间过滤 → 时间表 → SQL/操作细节 Inspector。可从工作台快捷入口打开。 | 时间、对象、动作、状态、耗时、返回/影响数量。恢复仅恢复输入；本地 History 与服务端 Audit 分开标记。复用 WorkbenchHistoryDrawer，不新增查询执行引擎。 |

**WB-02C 工作台全局状态合同（`notebook`、`history`）：** 页面状态沿用外壳的正常、空、错误、无权限、只读和长内容六态；所有记录为静态设计示例。

| 页面 | 能力字段（状态） | 六态主动作与边界 |
|---|---|---|
| SQL Notebook | `notebook-draft`、`notebook-manual-cell`、`notebook-result-snapshot`、`notebook-export` 均为规划；原型仅组织说明/SQL 单元、手动当前单元边界和脱敏导出规则。 | 正常“预览当前单元”，明确拒绝后端调用、自动执行和全部运行；空“添加说明或 SQL 单元”只建本地草稿；错误查看规划边界并保留输入；无权限隐藏结果载荷；只读可编辑说明/导出草稿但禁用执行和写入；长 Notebook 按单元/字节预算折叠，不虚构 continuation、任务 ID 或后台进度。 |
| 执行历史 | `history-local`、`history-input-restore`（既有）保存本地输入与恢复；`history-model-filter`、`history-server-audit`（延伸）区分九模型/数据库筛选和服务端审计；`history-write-replay`（规划）明确拒绝写重放。 | 正常“恢复输入”且不自动执行；空“打开 SQL 工作区”并保留筛选；错误只替换历史列表；无权限隐藏 SQL/Payload；只读允许筛选/恢复输入、禁用重放和修改；长历史按记录/字节预算分页并标明未返回全部，不虚构全量历史或可恢复任务。 |

对象页签在 SQL/Notebook/History 同一 Workspace 注册，不再单独复制一套布局。页面标题/页签、面包屑与选中 Explorer 对象必须一致；切换对象不能悄悄改变旧 SQL 页签的数据库。

## 观测：五个二级页面

| 页面 / ID / 标签 | 主任务与页面结构 | 关键字段、动作与边界 |
|---|---|---|
| 性能指标 / `metrics` / 既有 | 时间窗口与刷新 → 小型吞吐/延迟图 → 数据库 MemTable/Segment 表 → 采样 Inspector。 | 写入速率、query P95、WAL fsync P95、内存、采样时间、指标能力。Prometheus 未启用展示原因与最小指标降级，不造 0 值图。离线保留最后样本，明确过期。 |
| 实时事件 / `events` / 既有 | 级别/来源过滤 → 时间事件流 → Payload 与连接 Inspector。 | 事件时间、接收时间、级别、数据库/实例、来源、正文。暂停视图不会暂停服务器。SSE 断线状态清晰；本地已接收列表不表示持久事件审计。 |
| 慢查询 / `slow-queries` / 延伸 | 耗时/数据库/时间筛选 → 慢查询表 → SQL Inspector / 手动 Explain 入口。 | 阈值以服务端配置为准；状态、行数与耗时直接来自已接收事件。打开 SQL 只恢复输入，不自动重新运行慢请求。复用 Events 与 SlowQueryDrawer。 |
| 告警规则 / `alerts` / 规划 | 规则列表 → 指标/窗口/阈值表单 → 条件预览 → 通知路由说明。 | 未确认统一后端接口；规则是草稿，通知动作不能显示已发送。需要实例管理权限、去抖窗口、通知路由合同后才可实施。 |
| 运行时诊断 / `runtime` / 规划 | 组件索引 → 选中组件诊断摘要 → 有界采集预览。可转入 MQ/Graph 已有诊断页。 | 采集时间、预算、脱敏、取消、组件来源。没有能力时显示未提供，不能把客户端失败解释为健康。通用诊断聚合 DTO 待规划。 |

**WB-02C 性能指标状态合同（`metrics`）：** `metrics-sample`（既有）沿用 `/metrics` 与时间窗口；`metrics-minimal`、`metrics-offline-snapshot`、`metrics-ai-usage`（延伸）分别处理最小指标、离线最后样本和真实 usage/估算值区分；`metrics-export`（既有）受窗口、权限和字节预算约束。所有数值示例不等于采样、质量或成本证据。

六态主动作依次为：正常“刷新指标”；无采样“查看采样说明”且不造 0 值图；错误“检查并重试”并保留最后有效样本、标记过期；无权限“查看权限要求”并隐藏受限载荷；只读“导出当前样本”且禁用采样配置/告警写入；长时间窗“查看采样预算”并按点数/字节折叠，不能虚构全量历史、精确 P95 或 AI 质量/成本报告。

**WB-02D 观测页能力与状态合同（`events`、`slow-queries`、`alerts`、`runtime`）：** 原型页将以下能力作为页面级元数据提供给任务页签；这些标签只描述现有、延伸或规划边界，不产生网络请求或服务器状态。

| 页面 | capabilities | 六态主动作、字段与边界 |
|---|---|---|
| 实时事件 / `events` | `events-sse-stream`（既有 SSE 事件流）、`events-view-pause`（既有，仅暂停当前视图）、`events-filter`（既有级别/来源/数据库筛选）、`events-reconnect-state`（延伸断线/最后帧）、`events-local-buffer`（延伸本地已接收列表）、`events-server-audit`（规划服务端持久审计）。 | 正常“暂停视图”，字段为级别、来源、数据库/实例、事件时间、接收时间、正文/Payload、SSE 状态；空“检查事件连接”并保留筛选；错误“检查并重试”仅替换结果并保留本地列表，禁止标记服务器已暂停；无权限“查看权限要求”并隐藏受限 Payload；只读“导出当前事件”，禁用暂停服务器流/清理服务器事件；长内容“查看事件载荷预算”，按条数/字节截断并说明本地列表不等于持久审计。暂停视图不会暂停服务器。 |
| 慢查询 / `slow-queries` | `slow-query-events`（既有已接收事件）、`slow-query-filters`（延伸耗时/数据库/时间筛选）、`slow-query-input-restore`（既有恢复输入）、`slow-query-explain`（延伸手动 Explain）、`slow-query-payload-permission`（既有按权限脱敏 SQL）。 | 正常“打开查询输入”，字段为数据库、最短耗时、时间范围、事件时间、耗时、行数、状态、SQL 摘要、阈值来源；空“查看阈值说明”并保留筛选；错误“检查并重试”仅替换列表，禁止自动重跑；无权限“查看权限要求”并隐藏 SQL/Payload；只读“恢复查询输入”，禁用自动重跑、阈值修改和删除；长内容“查看结果预算”，SQL/参数按行和字节折叠，Explain 必须手动打开且不伪造计划。 |
| 告警规则 / `alerts` | `alert-rule-draft`（规划规则草稿）、`alert-evaluation`（规划评估记录）、`alert-notification-routing`（规划通知路由）、`alert-admin-permission`（规划实例管理权限检查）。 | 正常“预览规则”，字段为规则名称、指标、评估窗口、阈值、去抖窗口、通知路由、作用范围、草稿状态；空“创建规则草稿”且仅保存在页面内存；错误“查看规划边界”并保留草稿，禁用启用/通知；无权限“查看权限要求”并隐藏指标/评估载荷；只读“查看规则草稿”，禁用保存、启用、修改路由、发送通知；长内容“查看评估预算”，按记录/字节折叠。统一后端、评估与通道未确认，规则草稿不会建立服务端规则或发送消息。 |
| 运行时诊断 / `runtime` | `runtime-component-index`（规划组件索引）、`runtime-diagnostic-collection`（规划有界采集）、`runtime-cancel`（规划取消与服务器终态）、`runtime-native-handoff`（延伸转入 MQ/Graph 原生页）、`runtime-redaction`（规划采集前脱敏）。 | 正常“预览诊断采集”，字段为组件、范围、可见信息、时间预算、字节预算、脱敏策略、取消能力、证据状态；空“查看能力矩阵”，缺能力显示未提供而非 0/健康；错误“检查并重试”保留组件/预算/取消状态，禁止标记组件健康；无权限“查看权限要求”并隐藏诊断载荷；只读“查看诊断摘要”，禁用采集/修改预算/标记健康；长内容“查看采集预算”，日志与任务按时间/字节脱敏折叠，没有服务器终态不能显示完成、健康或可恢复任务。采集必须有界、可取消。 |

## 数据流：四个二级页面

| 页面 / ID / 标签 | 主任务与页面结构 | 关键字段、动作与边界 |
|---|---|---|
| Modbus TCP / `modbus` / 既有 | 数据库选择与 Runtime 状态 → 源/端点/绑定表 → Pending 与 Audit 页签 → 选中写请求 Inspector。 | 地址、寄存器、表绑定、请求 ID、范围、审批状态。复用现有批准/拒绝机制；非管理员仅按真实权限查看，现场写入不能绕过确认。 |
| 导入任务 / `imports` / 延伸 | 模型/目标选择 → 文件/重复策略 → 字段映射 → 校验 → 预览 → 导入记录。任务列表是统一入口。 | 格式、行数/字节、映射、重复/失败策略、错误位置。复用模型既有导入器；后端无持久 resume 的路径必须显式标记“重新选择文件”，不能提供伪恢复。 |
| 对象传输 / `transfers` / 延伸 | Bucket/方向筛选 → 上传下载与 Multipart 会话表 → 分片/校验 Inspector。 | Key、版本、字节、已完成分片、有效期、checksum、取消。复用对象工作台；Upload、complete、abort 分别预览。状态来自实际会话，不以浏览器进度当服务器提交完成。 |
| 任务与位点 / `jobs` / 规划 | 跨模型任务索引 → 位点与恢复边界 → 转入模型原生动作，不造共同“立即重试”。 | generation、revision、offset、发布点、可恢复原因。RAG 持久任务已有；统一任务列表尚无通用 DTO。MQ offset 归属数据库 Topic，可按逻辑数据库过滤；其物理位点由实例共享 Store 持久化，恢复影响需另注。 |

**WB-02D 数据流状态合同（`modbus`）：** capabilities 为 `modbus-runtime`（既有 Runtime 状态）、`modbus-bindings`（既有源/端点/数据库表绑定）、`modbus-pending-write`（既有待审批写入）、`modbus-audit`（既有审计）和 `modbus-write-approval`（既有批准写入边界）。

六态主动作与字段保持一致：正常“刷新状态”，字段为数据库、源/端点、模式、地址、寄存器范围、数据库/表绑定、Runtime 状态、请求 ID、审批状态；空“查看配置说明”并保留数据库与 Runtime 筛选；错误“检查并重试”仅替换 Runtime/Pending/Audit 结果并保留请求草稿，不能把刷新失败当作现场断电；无权限“查看权限要求”并隐藏端点/寄存器/写请求载荷，禁用查看寄存器和批准/拒绝；只读“查看运行时”，禁用批准、拒绝和端点配置；长内容“查看传输限制”，端点/寄存器与 Audit 按服务端分页/字节预算展示，浏览器进度或刷新不能代替服务器写入终态。现场写入必须复用批准/拒绝流程，不能绕过确认。

**WB-02E 工作台与数据流状态合同（`summary`、`recent`、`imports`、`transfers`、`jobs`）：** 五页把实例摘要、工作区恢复、导入、对象传输和跨模型任务索引连接到已有模型入口；能力标签只描述既有、延伸或规划边界，原型不发起网络请求、不写配置、不制造服务器终态。

| 页面 | capabilities | 六态主动作、`normal.fields` 与边界 |
|---|---|---|
| 实例概览 / `summary` | `summary-database-overview`（既有九模型摘要）、`summary-health-snapshot`、`summary-pending-items`（延伸摘要）、`summary-workspace-entry`、`summary-mq-persistence-boundary`（既有边界说明）。 | 正常“打开工作台”，字段为连接、数据库数、最近访问、待处理项、活动工作区、待审批、需关注、健康检查时间、数据来源；空“添加连接”并保留连接/权限入口；错误只替换摘要并保留过期最后快照，不能标记健康；无权限隐藏数据库/资源/待处理载荷；只读允许查看并进入读工作台，禁用新建/删除/批准/连接修改；长摘要按记录/字节分页，不能虚构全量数据库、健康或 continuation。MQ 逻辑 `scope=database`、`persistenceScope=instance`，物理 `.system/mq` 共享，当前单库备份不含该 Store。 |
| 最近工作区 / `recent` | `recent-workspace-list`、`recent-context-restore`、`recent-offline-snapshot`、`recent-connection-recheck` 均为规划。 | 正常“恢复选中工作区”，字段为宿主、连接、数据库、对象、工作区、当前页签、输入/过滤器、保存时间、快照时间、草稿状态；空“打开 SQL 工作区”只保留宿主/连接并说明保存入口；错误只替换列表并标旧快照过期；无权限隐藏输入、结果和对象载荷；只读可查看元数据/回填输入，禁用查询执行、写重放和固定状态修改；长内容按记录/字节预算折叠。Workspace Registry 未实现，恢复后须重新核验身份与能力，禁止自动重放。 |
| 导入任务 / `imports` | `import-target-selection`、`import-field-mapping`、`import-validation`、`import-staged-preview`（延伸）；`import-persistent-resume`（规划）。 | 正常“选择导入目标”，字段为数据库、模型、目标对象、源文件、格式、行数/字节、字段映射、重复策略、失败策略、错误位置、任务状态；空“选择导入目标”；错误返回修正映射并禁止提交；无权限隐藏文件/字段载荷；只读可查看任务与错误，禁用上传/覆盖/追加/确认；长内容按行/字节预算分页，未有持久任务身份时必须重新选择文件。复用模型导入器，原始名称/类型和失败策略以真实能力为准。 |
| 对象传输 / `transfers` | `transfer-session-list`、`transfer-upload-preview`、`transfer-download-range`、`transfer-checksum`（延伸）；`transfer-cancel`（规划）。 | 正常“新建上传”，字段为数据库、Bucket、Key、方向、版本、字节、已完成分片、有效期、checksum、取消状态；空“新建上传”保留 Bucket/方向/Key；错误只替换分片/校验并保留草稿，浏览器进度不等于服务器终态；无权限隐藏 metadata、Range 和上传载荷；只读允许会话/Range/校验查看，禁用上传、complete、abort、删除；长对象按条数/字节预算预览，不自动下载全量。Upload、complete、abort 分别预览并以服务器响应为准。 |
| 任务与位点 / `jobs` | `jobs-model-index`、`jobs-resume-boundary`（规划）；`jobs-rag-persistent`（既有）；`jobs-object-version`、`jobs-mq-offset`（延伸）。 | 正常“查看选中任务”，字段为范围、数据库、模型、任务 ID、generation、revision、offset、对象版本、发布点、状态、可恢复原因；空“查看任务能力”并保留筛选；错误只替换索引，禁止把客户端重试当持久恢复；无权限隐藏任务载荷/位点；只读可查看并转原生页，禁用恢复/重试/发布/删除；长任务按记录/字节分页，不拼接异构 continuation。`generation`、`revision`、`offset`、`object version` 保持不同语义；MQ offset 属于数据库 Topic，物理持久化为实例共享 Store，恢复影响须单独核验。 |

## AI 与 MCP：四个二级页面

| 页面 / ID / 标签 | 主任务与页面结构 | 关键字段、动作与边界 |
|---|---|---|
| AI Connect / `ai-connect` / 规划 | 客户端/传输/配置范围 → 数据库与只读预算 → 配置预览 → 自检 → tools/list → 外发说明。 | WorkBuddy/Claude/Cursor/Codex；typed Streamable HTTP `/mcp/{database}` 为已有服务，配置向导与 `sonnetdb mcp` stdio bridge 为 M47-U08。自检状态必须区分未执行/运行中/HTTP/认证/工具；原型不写配置、不执行网络请求。 |
| Copilot 与 Provider / `copilot-settings` / 既有 | Provider、账号绑定、模型目录、用量与测试；敏感字段只显示是否已配置。 | 真实模型 ID、Provider、令牌到期、实际/估算 usage、允许外发范围。复用 AiSettings；连接测试成功不表示真实质量/成本门禁通过。保存配置需要实例管理权限。 |
| RAG 管理 / `rag` / 既有 | 数据库/Stream → 已发布 revision/profile → 持久任务 → 派生重建/换代 → 清理退役版本 → 审计。 | 内容/分块、generation、expected revision、Profile identity。续跑必须匹配已配置 profile；新版本完整发布前保留当前；安全整数溢出时禁写。复用现有服务器权限与 WriteApprovalPanel。 |
| 工具权限与外发 / `tool-permissions` / 规划 | 实际工具清单 → 用户/宿主有效权限 → 预算 → 数据范围 → 调用记录。 | 权限是用户权限、工具边界、宿主策略的交集。默认只读，MCP 无直接写入口；工作台审批不能通过 AI 提示绕过。不存在通用权限管理 API 的字段只读/禁用并标原因。 |

**WB-02F AI 与 MCP 状态合同（`ai-connect`、`copilot-settings`、`rag`、`tool-permissions`）：** 四页把已有 typed HTTP、账号/Provider、RAG 持久任务和权限边界整理为页面级元数据；静态原型不发起网络请求、不写客户端配置、不伪造 `tools/list`、Provider、usage、质量/成本或任务终态。

| 页面 | capabilities | 六态主动作、`normal.fields` 与边界 |
|---|---|---|
| AI Connect / `ai-connect` | `ai-connect-streamable-http`（既有 typed Streamable HTTP）；`ai-connect-config-wizard`、`ai-connect-stdio-bridge`、`ai-connect-tools-list`、`ai-connect-egress-policy`（规划）。 | 正常“生成配置预览”，字段为客户端、传输方式、数据库、配置范围、HTTP Endpoint、结果预算、工具清单状态、外发策略；空“开始接入向导”只保留向导输入；错误保留 Endpoint/预算并标失败步骤，不能标记接入已验证；无权限隐藏工具与数据库载荷并显示实例/数据库/宿主权限；只读可查看边界，禁用配置写入、stdio 安装、工具调用和外发批准；长工具/诊断按条数/字节预算折叠，不能虚构完整清单或 continuation。typed HTTP 已有，`sonnetdb mcp` stdio bridge 与配置向导仍是 M47-U08 规划。默认只读、MCP 无直接写/删入口，凭据永不外发。 |
| Copilot 与 Provider / `copilot-settings` | `copilot-account-binding`、`copilot-model-catalog`、`copilot-provider-test`（既有）；`copilot-usage-evidence`（延伸）；`copilot-quality-cost-gate`（规划）。 | 正常“预览配置变更”，字段为 Provider、Chat 模型、Embedding profile、账号状态、Token 到期、usage 时间窗、实际/估算标记、外发范围、质量/成本证据；空“查看配置步骤”；错误仅替换目录/usage 并保留配置，不能将连接成功当质量 PASS；无权限隐藏模型、usage、凭据状态；只读可查看 profile/目录/口径，禁用绑定、保存和外发修改；长目录/usage 按记录/字节分页，不能伪造模型 ID、价格、质量分数或成本报告。真实模型质量与成本证据独立验收。 |
| RAG 管理 / `rag` | `rag-published-snapshot`、`rag-persistent-task`、`rag-profile-identity`、`rag-derived-rebuild`、`rag-retirement-cleanup`（既有）；`rag-audit`（延伸）。 | 正常“预览重建 / 换代”，字段为数据库、Stream、Active revision、Profile identity、Generation、Expected revision、内容/分块、任务 ID、发布状态、清理范围；空“查看摄取说明”；错误保留 profile/generation/expected revision，不能把客户端重试当持久续跑；无权限隐藏内容/分块/任务载荷；只读可查看快照、任务、审计，禁用重建、续跑、发布和清理；长内容/日志按记录/字节分页且保留 profile、generation、revision 关系。续跑必须匹配 profile、generation 与 expected revision；安全整数溢出或范围异常时拒绝写入。 |
| 工具权限与外发 / `tool-permissions` | `tool-permissions-intersection`、`tool-permissions-readonly-default`、`tool-permissions-egress`、`tool-permissions-call-audit`（规划）；`tool-permissions-budget`（延伸）。 | 正常“预览权限范围”，字段为客户端、用户身份、数据库、工具类别、有效权限交集、`maxRows`、字节预算、超时/取消、数据外发范围、调用记录来源；空“打开 AI Connect”且不把静态类别当实际授权；错误保留身份/数据库筛选，不能将缓存标成有效权限；无权限隐藏工具 schema、数据范围与载荷；只读可查看边界与预算，禁用策略修改、外发批准和写/删；长 schema/调用记录按条数/字节折叠，不伪造完整 `tools/list`。有效权限是用户授权 × 工具边界 × 宿主策略交集，默认只读，MCP 无直接写入口，显式允许的数据范围之外不外发，凭据永不进入工具结果。 |

## 治理：五个二级页面

| 页面 / ID / 标签 | 主任务与页面结构 | 关键字段、动作与边界 |
|---|---|---|
| 用户 / `users` / 既有 | 用户表 → 当前授权 Inspector → 新建/改密对话框 → 删除影响确认。 | 用户名、超级用户、创建时间、Token 数。用户管理是控制平面权限；新用户密码/改密不进入历史与审计明文。DELETE 确认精确名称与关联凭据。 |
| 数据库授权 / `grants` / 既有 | 用户/数据库筛选 → 授权表 → 权限预览 → 授予/撤销确认。 | 用户、数据库、真实支持权限、有效范围。MQ Topic Read/Write 使用数据库 grant；共享 Store 的恢复/全局配置权限另行核验。权限预览来自真实合同，不能从界面选项推断引擎能力。 |
| 访问 Token / `tokens` / 既有 | Token 标识表 → 创建 form → 一次性凭据展示 → 撤销确认。 | 所属用户、标识、创建时间及服务端实际支持有效期。创建时只展示一次，列表使用部分标识；复制失败提供明确提示。不能假造后端未支持的到期/轮换合同。 |
| 审批与审计 / `approvals` / 延伸 | 待处理列表 → 影响差异 → 风险/有效期 → 执行终态 → Audit。各模型已有审批保持专属合同。 | 连接、数据库/实例、模型、对象、影响数量、风险、发起人、有效期。统一索引尚需 DTO 适配；本地草稿与服务端暂存分开。确认不改权限，错误后保持草稿、不自动重试写操作。 |
| 数据库备份与恢复 / `backup` / 延伸 | 数据库能力与范围 → 路径 → 备份预览 → 验证 → 恢复目标/覆盖预览 → 独立审计。 | 版本、目录、manifest/校验、目标数据库、覆盖风险。复用现有 backupStatus/verify；不把验证通过表示为实机灾备通过。MQ Topic 逻辑属于数据库，但**当前单库备份尚未覆盖实例共享 `.system/mq` Store**；不能假称统一备份已实现。MQ Store 恢复可能影响多个数据库，须独立合同、权限与证据。 |

**WB-02G 治理页面状态合同（`users`、`grants`、`tokens`、`approvals`、`backup`）：** 五页只补齐 REVIEW_DRAFT 原型的页面能力、字段和状态矩阵；不发起请求、不生成凭据、不确认权限或服务器终态。任务页签通过同一页级对象复用 `capabilities` 与六态合同。

| 页面 | capabilities | 六态主动作、`normal.fields` 与边界 |
|---|---|---|
| 用户 / `users` | `users-control-plane-list`、`users-credential-management`、`users-database-grants-link`（既有）；`users-delete-impact-preview`（延伸）。 | 正常“新建用户”，字段为实例身份、用户名、超级用户、创建时间、Token 数、关联授权数、凭据状态、最后变更时间；空“清除筛选”并保留连接/名称筛选；错误仅替换用户结果并保留筛选，禁止自动创建、改密或删除；无权限隐藏用户/凭据载荷，显示实例控制平面权限，不能用数据库 grant 推断用户管理权；只读可查看并跳转授权，禁用新建、改密、删除和凭据变更；长目录按记录/字节分页，用户名原始拼写和敏感字段脱敏。 |
| 数据库授权 / `grants` | `grants-database-access`、`grants-mq-database-scope`、`grants-instance-store-boundary`（既有）；`grants-effective-permission-preview`（延伸）。 | 正常“预览授权”，字段为实例身份、用户、数据库、权限、MQ Topic 范围、来源、有效权限、撤销影响；空“清除筛选”并保留用户/数据库/权限；错误不把缓存 grant 当当前授权且不自动授予/撤销；无权限隐藏 grant、Topic 和撤销载荷；只读可查看数据库授权，授予/撤销及实例共享 Store 恢复/全局配置禁用。MQ Topic Read/Write 按 database grant，实例共享 Store 权限另行核验。 |
| 访问 Token / `tokens` | `tokens-list-redacted`、`tokens-create-once-display`、`tokens-revoke-preview`（既有）；`tokens-expiry-contract`（延伸）。 | 正常“创建 Token”，字段为实例身份、所属用户、脱敏 Token 标识、创建时间、有效期（服务端）、状态、最后使用时间、撤销影响；空“选择用户”；错误保留创建表单，不自动重发或撤销；无权限隐藏标识与状态载荷；只读可查看脱敏列表，创建、复制明文、撤销和轮换禁用；长历史分页且永不含明文。明文只在创建成功时一次性显示，关闭后不可恢复；有效期只能来自真实服务合同，不得假造。 |
| 审批与审计 / `approvals` | `approvals-staged-operation-index`、`approvals-impact-risk-expiry`、`approvals-server-terminal-state`、`approvals-audit-source-separation`、`approvals-no-write-auto-retry`（既有/延伸）。 | 正常“查看影响预览”，字段为连接、数据库/实例范围、模型、对象、动作、影响数量、风险、发起人、有效期、请求 ID、服务器终态、审计来源；空“查看审计”；错误保留草稿、影响和请求 ID，不自动重试写入、不标记未知终态成功；无权限隐藏差异/载荷/终态；只读禁用确认、拒绝、取消、恢复和重试。精确影响/风险/有效期与服务器终态必须来自真实能力；本地草稿不等同服务端审计。 |
| 数据库备份与恢复 / `backup` | `backup-status-scope`、`backup-manifest-checksum`、`backup-restore-impact-preview`、`backup-mq-shared-store-boundary`（既有/延伸）；`backup-independent-restore-contract`（规划）。 | 正常“预览备份”，字段为实例身份、数据库、源目录/文件、版本、manifest、checksum、备份范围、验证状态、目标数据库、目标目录、覆盖确认、恢复影响、MQ Store 范围；空“查看备份能力”；错误保留路径和恢复草稿，不能把局部读取当验证通过；无权限隐藏路径/manifest/校验/恢复载荷；只读仅查看状态/边界，禁用创建、验证写入、覆盖恢复和共享 Store 恢复；长 manifest/日志按记录/字节分页。当前单库备份不覆盖共享 `.system/mq`，跨数据库 MQ Store 恢复须独立合同、权限、影响预览和服务器证据。 |

## 设置：五个二级页面

| 页面 / ID / 标签 | 主任务与页面结构 | 关键字段、动作与边界 |
|---|---|---|
| 工作台偏好 / `preferences` / 规划 | 外观/编辑器/结果/快捷键/工作区分组，右侧实时展示偏好影响；只调整本宿主 UI。 | 密度、字号、默认结果上限、布局宽度。不能扩大服务器最大预算，不能修改权限。保存/重置明确当前宿主范围；移除旧偏好需兼容方案。 |
| 实例配置 / `server-settings` / 规划 | 可读配置摘要 → 实际修改路径 → 预算 → 重启影响。 | 配置来源、敏感字段脱敏、是否需重启、管理员权限。未有在线 API 不显示可保存；例如 Prometheus 配置继续解释文件来源，不伪装成当前已有开关。 |
| Studio 宿主 / `studio-host` / 延伸 | Native bridge 状态 → Managed Local/data root → 文件与凭据 → 版本/安装 → Server 生命周期。 | Web/Studio/VS Code 的宿主能力分开。只允许管理宿主归属 Server；Web 不提供假 Start/Stop。M29 宿主合同已存在，干净 Windows/WebView2/安装升级/卸载/回收独立验证仍未完成。 |
| 能力与发布矩阵 / `capability-matrix` / 规划 | 三面能力对照 → API/MCP/UI 契约版本 → 兼容性 → 每发行物真实证据。 | manifest、server capabilities、uiContractVersion、版本、校验/签名。统一 manifest 待建；没有证据时为未就绪，不显示 PASS。局部 browser fixture 不能代表 Studio 安装或 Extension Host。 |
| 关于与帮助 / `about` / 既有 | 产品/真实版本 → 三宿主指南 → 模型语义 → 快捷键 → 许可证与反馈。 | 品牌 SonnetDB Workbench，发行物 Web Admin/Studio/VSIX。复制诊断摘要前预览敏感字段；运行时版本从真实发行物获得，不用固定示例版本暗示发布完成。 |

**WB-02H 设置与发布状态合同（`preferences`、`server-settings`、`studio-host`、`capability-matrix`、`about`）：** 五页只补齐 REVIEW_DRAFT 原型的能力、专用字段和六态矩阵；静态页面不写宿主偏好、不保存实例配置、不启动/停止 Server、不生成 manifest 或发行证据。任务页签复用同一页级对象的 `capabilities` 与六态。

| 页面 | capabilities | 六态主动作、`normal.fields` 与边界 |
|---|---|---|
| 工作台偏好 / `preferences` | `preferences-host-local`、`preferences-ui-preview`、`preferences-result-budget-boundary`、`preferences-reset-scope`（规划/延伸/既有边界）。 | 正常“预览偏好影响”，字段为宿主、主题、密度、编辑器字号、默认结果上限、Explorer/Inspector 宽度、保存范围；空使用宿主默认值并保留输入；错误只替换本地偏好载荷；无权限说明宿主存储受限且不等同数据库权限；只读禁用保存/重置持久化及扩大服务器预算/权限；长快捷键和工作区按宿主能力分页。偏好只影响当前宿主 UI，不能扩大服务器预算或修改权限。 |
| 实例配置 / `server-settings` | `server-settings-config-summary`、`server-settings-source-provenance`、`server-settings-sensitive-redaction`、`server-settings-restart-impact`、`server-settings-budget-boundary`（规划/延伸/既有边界）。 | 正常“查看配置说明”，字段为实例、配置来源、Prometheus 来源、SQL/MCP/Object 预算、敏感字段摘要、是否需重启、管理员权限、MQ 持久化范围；空无摘要时不生成可保存表单；错误保留来源和重启草稿、不把缓存当当前配置；无权限隐藏敏感字段/预算/路径；只读无在线 API 时禁用保存、应用和重启；长配置和日志按键/字节预算分页。敏感字段脱敏，未有在线 API 不显示可保存；重启影响来自真实实例合同。 |
| Studio 宿主 / `studio-host` | `studio-host-native-bridge`、`studio-host-managed-local`、`studio-host-host-adapters`、`studio-host-install-evidence`、`studio-host-lifecycle-boundary`（既有/延伸/规划）。 | 正常“检查宿主能力”，字段为宿主、Native bridge、Managed Local、data root、凭据存储、Server 版本、PID/启动时间、进程归属；空浏览器宿主保留边界且禁用 Start/Stop；错误只替换 bridge/进程结果；无权限隐藏 data root/凭据/进程载荷；只读可检视但禁用 Start/Stop、安装/升级/卸载；长安装日志按记录/字节预算分页。Web 不提供假 Start/Stop；M29 安装包、干净 Windows、WebView2、升级/卸载和回收证据分开验收。 |
| 能力与发布矩阵 / `capability-matrix` | `capability-matrix-manifest`、`capability-matrix-server-capabilities`、`capability-matrix-ui-contract`、`capability-matrix-signature-evidence`、`capability-matrix-three-host-evidence`（既有/规划）。 | 正常“查看发布检查项”，字段为宿主、manifest、Server capabilities、API/MCP/uiContractVersion、发行版本、校验/签名、Web/Studio/VS Code 证据、Graph 稳定性；空无 manifest 时所有发布项未就绪；错误保留筛选且不把缓存标 PASS；无权限隐藏 manifest/签名载荷；只读不能编辑 manifest、上传发行物或标记 PASS；长能力/证据矩阵按宿主分页。统一 manifest、签名和三宿主证据缺一不可，局部 browser fixture 不代表 Studio/Extension Host。 |
| 关于与帮助 / `about` | `about-product-guide`、`about-model-semantics`、`about-release-version`、`about-diagnostic-preview`、`about-license-feedback`（既有/延伸）。 | 正常“查看使用指南”，字段为产品、当前宿主、发行物、真实版本来源、九模型语义、快捷键、许可证、诊断摘要范围；空保留产品/宿主/版本来源并打开指南；错误只替换帮助或版本载荷且不使用固定原型版本；无权限隐藏受限诊断并要求预览；只读可查看指南/许可证，诊断复制仍需敏感字段预览；长帮助/许可证按章节分页。版本来自真实发行物；复制诊断先预览并脱敏，不自动外发反馈。 |

## 九模型：资源树与页签详细设计

九模型共享“对象页签 → 任务页签 → 输入/操作 → Result Plane → Inspector”。页签只描述任务，不要求为每个页签新增后端。九模型目录独立于 30 个区域页面；每对象都显示范围、原始名称、权限、最近加载时间与实现标签。

### 时序 `measurement`：Telemetry

| 页签 | 状态 | 中央主任务与必要输入 | Inspector / 动作边界 |
|---|---|---|---|
| 数据点 | 既有 | 时间窗、TAG/FIELD 选择、点表；时间与稀疏字段保持原语义。 | 选中点的 series、timestamp、FIELD 值；写入/删除先预览。 |
| SQL | 延伸 | 在同对象上下文进入共用编辑器；插入保留原名的模板。 | 不更换 SQL binder；支持与拒绝的分组/预算按当前合同说明。 |
| 图表 | 延伸 | 共用结果图表，轴和 FIELD/series 选择；真实聚合结果才画聚合曲线。 | 时间缩放保持查询窗，NULL 不补零；新查询必须手动执行。 |
| 轨迹 | 延伸 | 共用轨迹结果视图，仅坐标列/时间列适用时开放。 | 坐标系、列映射、范围与预算；没有坐标能力时提供原因。 |
| Schema | 既有 | TAG/FIELD/类型列表；原始拼写、角色、类型可检视。 | DDL 预览并遵守 GH-Issue #211；Point 摄取映射到已有 schema 拼写。 |
| 文件导入 | 既有 | 文件、列映射、时间单位/时区、重复策略、错误预览。 | 导入权限与最大行/字节；持久恢复按实际能力。 |
| 保留策略 | 规划 | 从现有维护合同适配 retention 查看与影响预览，接口需单独核查。 | 显示真实支持策略与作用范围；不凭 UI 增加自动删除能力。 |

示例列 `DeviceID` / `Line` 为 TAG，`Temperature` / `Pressure` 为 FIELD。右侧解释选中 series 与时区；底部显示真实返回点数、截断和查询耗时。不同大小写不得新造 series/列。

**WB-02B 状态合同（`measurement` 页面 status=`extension`）：** 专用字段为数据库/Measurement、时间范围、TAG/FIELD 选择、结果上限、时区和选中 Series。能力标签区分既有点读取与 Schema、延伸图表/轨迹、规划保留策略；静态点表不代表实时采样。

| 状态 | 主动作与呈现 | 能力边界 |
|---|---|---|
| 正常 / 空 | “查询数据点”；空结果用“调整时间范围”，显示 0 点并保留 Measurement、时间窗和 TAG 过滤。 | `existing` 点读取、Schema 和文件映射；空结果不表示 Schema 或存储故障。 |
| 错误 / 无权限 | “检查并重试”只替换点结果；无权限隐藏点值/Schema 载荷并进入数据库 Measurement 权限说明。 | TAG/FIELD 原始拼写保留；客户端禁用不等于服务端授权。 |
| 只读 | 查询、图表、Schema、导出可用；写入、删除、文件导入提交和保留策略修改禁用。 | 图表/轨迹只在真实结果和坐标列可用时开放；写入仍需审批。 |
| 长结果 | 点表和图表按行/字节预算分页或折叠，NULL 不补零。 | 最大点数由服务端能力决定；不加载无限历史、不虚构 continuation 或聚合证据。 |

### 关系 `table`：Assets

| 页签 | 状态 | 中央主任务与必要输入 | Inspector / 动作边界 |
|---|---|---|---|
| 数据 | 既有 | 列筛选、排序与行网格；新增/修改先留下草稿。 | 主键、NULL、类型、变更前后差异；大结果按真实预算。 |
| 设计器 | 既有 | 列、类型、NULL、默认值、约束设计；形成 DDL 预览。 | 布局变更兼容性与影响；无能力的 ALTER 不可选择。 |
| 索引 | 既有 | 索引列、种类、唯一性、状态；维护预览。 | schema 返回的真实索引参数；创建/删除/重建受控。 |
| ER 图 | 既有 | 关系与约束可视浏览，节点点击保留表上下文。 | 仅依据真实约束画关系，不推断字符串同名就是外键。 |
| DDL | 既有 | 复制/导出当前定义，显示目标对象名。 | 保留大小写及正确引用，数据值不套用标识符规则。 |
| 导入 / 导出 | 既有 | 格式、列映射、重复策略、预览与批准。 | Native bridge/Web fallback 按宿主；导出范围与敏感列明确。 |
| Explain | 延伸 | 进入共用 SQL Explain 结果，编辑对应查询。 | 不自动重跑；物化、扫描、预算解释来源于真实 planner。 |

**WB-02B 状态合同（`table` 页面 status=`existing`）：** 专用字段为数据库/表、筛选与排序、分页/物化预算、选中主键和草稿差异。能力标签区分既有行/schema/导入导出、延伸草稿写入与 Explain；样例行不代表真实表快照。

| 状态 | 主动作与呈现 | 能力边界 |
|---|---|---|
| 正常 / 空 | “浏览数据”；空结果用“调整筛选”，保留表、筛选和排序输入。 | `existing` 行浏览、Schema、DDL、导入/导出；空结果不表示表不存在。 |
| 错误 / 无权限 | “检查并重试”只替换行结果；无权限隐藏行/字段载荷并显示数据库表权限要求。 | 当前页不等于全表；Manage/Write 必须由服务端授权返回。 |
| 只读 | 浏览、Schema、DDL、Explain、导出可用；行编辑、ALTER、索引维护、导入覆盖和删除禁用。 | 草稿不得冒充已写入；Explain 需手动进入，不自动重跑。 |
| 长结果 | 大结果按页与字节预算展示，长字段折叠并保留类型/NULL。 | 不虚构全量行数或 continuation；物化预算按真实能力显示。 |

### JSON `document`：Manuals

| 页签 | 状态 | 中央主任务与必要输入 | Inspector / 动作边界 |
|---|---|---|---|
| Find | 既有 | filter、projection、sort、limit、真实 continuation。 | 保留实际返回文档 ID；查询构建与 raw filter 可互查。 |
| 文档 | 既有 | 结果文档列表/JSON Tree/Raw，选择一个文档。 | 单文档 Payload、类型、大小；长 JSON 有界呈现。 |
| Aggregate | 延伸 | 复用 DocumentAdvancedWorkbench 可用聚合任务；阶段编辑与受支持列表。 | 未支持阶段拒绝并说明；不得把设计字段解释为完整 MongoDB 兼容。 |
| 索引 | 既有 | JSON 路径/全文索引、定义与运行状态。 | 路径和字段真实语义；维护进入预览审批。 |
| Validator | 既有 | 当前规则、候选文档测试、变更差异。 | 失败路径与原因；变更影响只按真实校验能力。 |
| Change Feed | 既有 | continuation、事件类型、时间/位点与 Payload。 | 客户端断开不表示订阅持久恢复已完成。 |
| 导入 / 导出 | 既有 | JSON/NDJSON、ID 路径、insert/replace、预览。 | 批量错误定位；覆盖文档需显式确认。 |

JSON 属性键与文档数据值保持原语义，不套用 SQL 大小写合同。

**WB-02B 状态合同（`document` 页面 status=`existing`）：** 专用字段为数据库/集合、filter/projection/sort、limit、真实 continuation、文档 ID 和 Payload 呈现模式。能力标签区分 Find/Tree/Raw、索引/Validator、Change Feed 与导入导出；Aggregate 和更新覆盖属于延伸合同。

| 状态 | 主动作与呈现 | 能力边界 |
|---|---|---|
| 正常 / 空 | “执行 Find”；空结果用“调整筛选”，保留集合、过滤和排序，不把空结果解释为索引损坏。 | `existing` Find、文档检视、Change Feed 和导入导出；continuation 必须来自服务端。 |
| 错误 / 无权限 | “检查并重试”只替换文档结果；无权限隐藏文档/变更载荷并显示 Collection Read/Write 权限。 | JSON 属性键保持文档语义；客户端控制不能推断授权。 |
| 只读 | Find、Tree/Raw、Change Feed、索引查看和导出可用；replace、Validator 修改、删除和导入写入禁用。 | 写入/覆盖先差异预览；不宣称完整 MongoDB Pipeline 兼容。 |
| 长文档 | 长 JSON 默认折叠，Tree/Raw 只展开有界路径并显示大小。 | 每页文档数/字节预算按真实能力；截断明确标记，不伪造 continuation。 |

**WB-18 显式读取恢复与高级预览合同（2026-10-06）：** 本地 401/403 后保持无权限且隐藏载荷，提供“重新验证读取权限”；只发全新 Find（空过滤、100 行、无旧游标），当前资源与会话成功才解除本地锁存。外部无权限约束不能被覆盖；失败、身份 ABA、迟返与卸载不能解锁。旧编辑/导入草稿和审批不恢复、不重放，高级查询及 Change Feed 不自动续跑。同身份 Schema 刷新或凭据更新只使旧请求失效，不能自行解锁。

Aggregate 保留输入 pipeline 并追加末尾 `$limit: 1001`；Distinct 所选上限为 1～1000，请求上限多一个哨兵值。结果先截断再格式化，最多显示和导出 1000 项（Distinct 为所选上限），超限显示“未返回全部”，历史记录预览数及 truncated。两条路径无 continuation 时不显示下一页；这些是输出/客户端预览预算，不能推断服务端扫描、中间物化、字节、总堆或长期容量门禁通过。更新、索引设计器和 Change Feed 子工作台继续独立验收。

### KV `kv`：DeviceState

| 页签 | 状态 | 中央主任务与必要输入 | Inspector / 动作边界 |
|---|---|---|---|
| 浏览 | 既有 | keyspace、prefix、scan 页/上限、key 表。 | 类型、大小、TTL、真实版本；无通用 cursor 时不造 continuation。 |
| 值检查器 | 延伸 | 将既有选中 key 的 Text/JSON/Hex/Base64 整理成任务视图。 | 二进制不强制解 UTF-8；长度限制与原始字节说明。 |
| TTL | 延伸 | 选中 key 的剩余时长/绝对到期；修改预览。 | 过期不当作错误；无过期与已过期分开；按真实 TTL 合同操作。 |
| 批量操作 | 既有 | 已选 key、命令、匹配范围、预览影响。 | 不允许“加载页内筛选”冒充全 keyspace 扫描；删除必须确认范围。 |
| 统计 | 既有 | key 数、类型、TTL 等当前端可取得统计。 | 局部加载统计与服务器全量统计分开。 |
| 操作历史 | 延伸 | 复用共用 History 过滤为当前 keyspace。 | CAS/原子操作仅实际能力支持时启用；失败不得自动重放写入。 |

**WB-02 状态合同（`kv` 页面 status=`extension`）：** 专用字段为 Keyspace、prefix、扫描上限、值格式、TTL 与版本/CAS。

| 状态 | 主动作与呈现 | 能力边界 |
|---|---|---|
| 正常 / 空 | “读取 Key”；无匹配时“调整前缀”，保留 Keyspace，不把空值当错误。 | `existing` 浏览；空态仍显示作用域和筛选。 |
| 错误 / 无权限 | “检查并重试”只重试当前 keyspace；无权限隐藏值载荷并进入数据库权限说明。 | Read/Write 来自服务端，客户端禁用不是授权证据。 |
| 只读 | 浏览、值检查、导出可用；TTL、CAS、批量删除显示禁用原因。 | `extension` 条件写入不得假装可执行。 |
| 长内容 | 大值折叠，按 Text/JSON/Hex/Base64 做有限预览并显示原始字节数。 | 不强制 UTF-8，不把截断值当完整值。 |

### SonnetMQ `mq`：DeviceEvents（逻辑数据库范围；物理实例共享）

| 页签 | 状态 | 中央主任务与必要输入 | Inspector / 动作边界 |
|---|---|---|---|
| 概览 | 既有 | `factory / DeviceEvents` 的 Topic/runtime、retention、消费者摘要。 | 数据库命名空间与 Read 权限；另注物理实例共享 `.system/mq` Store。 |
| 消息 | 既有 | 当前数据库 Topic 的 offset/time seek、最大条数与消息表。 | headers、key、payload、时间、offset；browse 不自动 ack；读取按数据库 Read 权限。 |
| 消费者组 | 既有 | 当前数据库 Topic 的 group、位点/lag、运行状态。 | 位点按逻辑 Topic 展示；ack/reset/publish 修改按数据库 Write 权限与现有预览审批。 |
| 配置 | 既有 | 当前数据库 Topic 配置与受支持修改预览。 | retention、容量作用范围按实际 API；可能影响实例 Store 的全局配置单独核验权限与跨数据库影响。不宣称 Kafka/RabbitMQ 全兼容。 |
| 恢复边界 | 延伸 | 实例共享 `.system/mq` 持久目录、恢复合同与维护说明，同时显示逻辑数据库。 | **当前单库备份尚未覆盖 MQ Store**；Store 恢复可能影响其它数据库 Topic；缺真实恢复接口时只提供范围说明。 |
| DLQ · 能力依赖 | 规划 | 有能力时浏览当前数据库 Topic 的死信与来源；否则禁用并给出原因。 | 不假造统一 DLQ/重放页面合同；重新投递保留数据库命名空间、权限与影响预览。 |
| 审计 | 延伸 | 当前数据库 Topic 的模型操作/批准记录；来源标签明确。 | 本地 History 与服务器审计区分；Topic offset 不是数据库备份位点。 |

**WB-02 状态合同（`mq` 页面 status=`extension`；逻辑 scope=`database`，persistenceScope=`instance`）：** 专用字段为 database + Topic identity、seek 模式/offset、最大条数、headers/payload、consumer lag。物理路径固定为实例共享 `.system/mq`；当前单库备份不含该 Store。

| 状态 | 主动作与呈现 | 能力边界 |
|---|---|---|
| 正常 / 空 | “浏览消息”；空 Topic 用“调整浏览范围”，显示 0 条但保留 database + Topic identity。 | `existing` Read browse，不自动 ack；空结果不表示 Store 故障。 |
| 错误 / 无权限 | “检查并重试”只替换 Topic 结果区；无权限隐藏消息/payload，进入数据库 Read 权限说明。 | 实例 Store 全局管理权限与数据库 Topic Read/Write 分开核验。 |
| 只读 | browse、导出、审计查看可用；ack/reset/publish、配置和恢复写动作禁用。 | 写动作必须有数据库 Write、真实 API 和预览审批。 |
| 长内容 | payload 默认摘要，按有界字节切换 JSON/Text/Hex/Base64；限制条数与字节预算。 | offset 是 Topic 位点，不是数据库备份位点；不能假造 DLQ 或跨库恢复。 |

### 向量 `vector`：ManualEmbeddings.Embedding

| 页签 | 状态 | 中央主任务与必要输入 | Inspector / 动作边界 |
|---|---|---|---|
| Search | 既有 | raw vector 或显式 text embed，Top-K、metadata filter。 | 维度不匹配在执行前拒绝；metric/score 方向按真实定义。 |
| 数据 / 导入 | 既有 | ID、向量、metadata、导入校验与预览。 | 长向量折叠但显示维度与有限 preview；不隐藏错误维度。 |
| 索引参数 | 既有 | 真实 kind、维度、metric、已支持索引参数。 | 更新/重建按能力与审批；不把 UI 参数增加为引擎特性。 |
| Profile | 延伸 | 展示服务端显式 profile 身份、Provider/model/revision/normalization。 | Profile 不可得时保持 raw vector 路径，不悄悄使用 hash fallback。 |
| 命中详情 | 延伸 | 将现有命中 Inspector 整理为完整 Payload/metadata/score 视图。 | 区分距离与相似度，源数据跳转保持对象上下文。 |
| 质量证据 | 规划 | 指向真实 Recall/质量/成本/模型/硬件报告，未有报告明确未就绪。 | tiny fixture、静态排名、合成模型不是语义质量证据。 |

**WB-02B 状态合同（`vector` 页面 status=`extension`）：** 专用字段为数据库/索引对象、Query mode、维度、metric、Top-K、metadata filter 与显式 Profile 身份。能力标签区分既有检索/导入/索引、延伸 Profile/命中 Inspector 和规划质量证据；原型排名不代表 Recall 通过。

| 状态 | 主动作与呈现 | 能力边界 |
|---|---|---|
| 正常 / 空 | “执行向量检索”；空结果用“调整查询或过滤”，保留向量/文本输入、索引和 Top-K。 | `existing` raw/text 检索、导入校验和索引查看；维度与 metric 必须匹配真实定义。 |
| 错误 / 无权限 | “检查并重试”只替换命中区；无权限隐藏向量、metadata 和源文档载荷。 | Profile 缺失或维度错误要在执行前拒绝；权限按数据库索引合同核验。 |
| 只读 | 检索、Profile、命中查看和导出可用；导入、索引重建与参数修改禁用。 | 不把 UI 参数变成引擎能力；不暗用 hash fallback。 |
| 长向量 / 长命中 | 按维度与字节预算折叠向量，命中 metadata 与源文档摘要有界呈现。 | Top-K/字节预算以服务端为准；静态排名、tiny fixture 和合成模型不构成质量证据。 |

### 全文 `fulltext`：Manuals.SearchIndex

| 页签 | 状态 | 中央主任务与必要输入 | Inspector / 动作边界 |
|---|---|---|---|
| Search | 既有 | term/phrase/fuzzy、field、all/any、Top-K。 | BM25、字段、高亮和文档 ID；仅真实匹配片段高亮。 |
| Analyzer | 既有 | 输入文本 → token/position 检视。 | tokenizer 身份与真实分词结果；原型示例不代表 analyzer 实测。 |
| 索引 | 既有 | fields、tokenizer、数量与索引定义。 | 配置差异与兼容性说明；删除/修改先预览。 |
| 重建 | 延伸 | 现有维护入口收口为进度与取消状态，按实际返回状态。 | 无持久任务 ID 时不显示 resume；不能编造后台百分比。 |
| 数据导入 | 既有 | ID path、insert/replace、JSON/NDJSON 校验。 | 重复 ID/错误定位；replace 不悄悄转 insert。 |
| 统计 | 延伸 | 将既有 index stats 整理为索引/词项统计视图。 | 仅显示真实字段，未知/未返回用未提供。 |

**WB-02B 状态合同（`fulltext` 页面 status=`extension`）：** 专用字段为数据库/索引、term/phrase、匹配模式、字段、Top-K、Analyzer 身份和重建任务 ID。能力标签区分既有检索/Analyzer/索引/导入、延伸重建与统计；示例 BM25 与高亮不是相关性评估。

| 状态 | 主动作与呈现 | 能力边界 |
|---|---|---|
| 正常 / 空 | “执行全文检索”；空结果用“调整检索条件”，保留关键词、字段和匹配模式。 | `existing` Search、Analyzer、索引查看和导入；高亮只呈现真实命中片段。 |
| 错误 / 无权限 | “检查并重试”只替换命中区；无权限隐藏摘要、文档文本和词项载荷并显示数据库索引权限。 | tokenizer/索引定义来自服务端；客户端禁用不等于授权。 |
| 只读 | Search、Analyzer、统计、索引查看和命中导出可用；重建、导入、删除及 Analyzer 修改禁用。 | 重建需真实任务 ID/终态；无任务不显示 resume 或伪百分比。 |
| 长文本 / 长命中 | 长摘要和高亮片段折叠，按文档数/字节预算读取，不自动拉取全文。 | Top-K/摘要预算按真实能力；高亮和静态 BM25 不算质量门禁。 |

### 对象 `bucket`：Evidence

| 页签 | 状态 | 中央主任务与必要输入 | Inspector / 动作边界 |
|---|---|---|---|
| 浏览 | 既有 | prefix、delimiter、限额、对象列表与版本。 | 列表分页按真实能力，不把局部筛选表示为服务器搜索。 |
| 预览 | 既有 | Range 起点/长度、格式预览、字节限制。 | Content-Type、版本、大小、checksum；不自动全量下载大对象。 |
| 上传 / 下载 | 既有 | key、文件、metadata/tags、进度与取消。 | 覆盖与新版本清晰；Native bridge/Web fallback 显示宿主来源。 |
| Multipart | 既有 | session、parts、有效期、complete/abort。 | 客户端上传进度与服务器分片确认分开；complete 需确认。 |
| 版本与治理 | 既有 | lifecycle、retention、quota、policy、legal hold。 | 当前对象删除/版本删除分开；受保留策略约束时解释拒绝原因。 |
| 图片语义 | 既有 | 配置、摄取/处理状态、搜索与 metadata filter。 | 不把合成模型或 hash fallback 计为真实语义证据。 |
| 审计 | 既有 | prefix、上限、模型 Audit 表。 | 数据来源、分页/上限、访问权限；不扩大完整 S3/SigV4 兼容声明。 |

**WB-02 状态合同（`bucket` 页面 status=`existing`）：** 专用字段为 Bucket、prefix/delimiter、Key、Range 起点/长度、Content-Type、版本、checksum 与 Multipart session。

| 状态 | 主动作与呈现 | 能力边界 |
|---|---|---|
| 正常 / 空 | “浏览对象”；空 prefix 用“调整对象范围”，保留 Bucket 和筛选。 | `existing` 有界列表与 Range 预览，空列表不表示 Bucket 不存在。 |
| 错误 / 无权限 | “检查并重试”保留 Key/Range 草稿；无权限隐藏 metadata/payload 并进入 bucket/数据库权限说明。 | 浏览器缓存和上传进度不等于服务器结果。 |
| 只读 | 浏览、Range、导出清单、审计可用；上传、complete、删除版本和治理修改禁用。 | Native bridge/Web fallback 需标注宿主来源。 |
| 长对象 | 默认只取 Range 并显示大小/校验；Multipart 只显示服务器确认的分片。 | 不自动下载全量对象，不扩大 S3/SigV4 兼容声明。 |

### 图 `graph`：FactoryTopology（Graph Beta）

| 页签 | 状态 | 中央主任务与必要输入 | Inspector / 动作边界 |
|---|---|---|---|
| Canvas | 既有 | graph、探索起点、最大节点/边、深度；有界画布。 | label、ID、property、边方向；点击不无限扩展。 |
| Schema / 索引 | 既有 | label、索引与慢遍历诊断。 | 实际 schema/capabilities；未提供诊断明确说明。 |
| SQL / PGQ | 延伸 | 进入共用 SQL 编辑器与 Graph 上下文。 | 遵守真实 PGQ 支持矩阵与结果预算；不另造图语言承诺。 |
| 受限编辑 | 既有 | 节点/边草稿 → 差异 → Stage。 | 显示影响与审批边界；Stage 不代表数据已修改。 |
| 导入 / 导出 | 既有 | vertices/edges 输入校验、数量预览、批准。 | 大 payload 预算、身份/关系校验、取消；无批准不执行。 |
| 维护 | 既有 | Repair/rebuild、checkpoint、compact 选择与暂存预览。 | 服务端审批有效期与权限、拒绝原因；过期要求重新暂存。 |
| 审计 | 延伸 | 从已有维护记录拆出浏览任务，按动作/状态筛选。 | Graph **Beta** 在树、页签、标题和能力矩阵一致保留。 |

**WB-02 状态合同（`graph` 页面 status=`extension`，全程保留 Graph Beta）：** 专用字段为图名、探索起点、深度、最大节点/边、label、identity、property 与 SQL/PGQ 预算。

| 状态 | 主动作与呈现 | 能力边界 |
|---|---|---|
| 正常 / 空 | “执行有界探索”；空图用“调整探索起点”，显示 0 节点/边并保留预算。 | `existing` 有界 Canvas、Schema 和导出；不无限扩展。 |
| 错误 / 无权限 | “检查并重试”只替换结果区并说明 PGQ/诊断来源；无权限隐藏顶点、边、属性。 | 权限按数据库 Graph Read/管理合同，原型关系不是真实查询证据。 |
| 只读 | Canvas、Schema、SQL/PGQ 和导出可用；Stage、维护和导入批准禁用。 | Stage 仅草稿，不代表数据已修改；维护需有效审批。 |
| 长属性/大图 | 属性折叠、关系分段加载并保持 200 节点/400 边设计预算。 | `extension` 长内容处理；完整 payload 只在有界 Inspector 中查看。 |

## 对话框、提示与可访问性

| 交互 | 内容与主次动作 | 状态处理 |
|---|---|---|
| 添加 / 切换连接 | 名称、URL、认证来源、默认数据库、测试结果；主动作保存并切换，次动作取消。 | 网络错误局部显示，草稿保留；离线连接有明确最后检查时间。 |
| 新建数据库 / 对象 | 名称、模型、范围、受支持参数与原名预览。 | SQL 标识符冲突按统一 binder 拒绝；MQ 创建显示数据库 Topic 命名空间与实际模型名称合同，另注物理实例共享 Store。 |
| 写审批 | 连接/数据库或实例/对象/动作/数量/差异/风险/权限/有效期，按钮“确认执行”与“返回修改”。 | 服务器暂存和本地草稿分开；不可确认未知影响；执行中显示取消能力。 |
| 删除与恢复 | 精确名称输入、覆盖对象、范围与不可逆影响。 | 命名匹配不代替权限；错误保留输入并提供可操作原因。 |
| 导入 / 传输 | 目标、格式、映射、重复策略、限制、错误位置和可取消进度。 | 持久任务支持才显示恢复；完成状态要以服务器终态为准。 |
| 未保存草稿 | 当前页签、修改数量与保存/丢弃/取消关闭。 | 默认焦点留在保留草稿动作；连接切换不悄悄丢弃草稿。 |
| Token 一次性显示 | 显示凭据、复制、已保存确认与关闭。 | 关闭后不再恢复明文；复制失败明确提醒。 |
| 成功/失败提示 | 成功短 toast；验证错误紧贴字段；局部 API 错误在当前面板；权限/离线/截断用持续 banner。 | 不用颜色作为唯一语义；toast 不代替操作审计；错误消息包含下一步。 |

键盘路径：功能轨 → 二级导航/Explorer → 工作区页签 → 当前任务 → Result Plane → Inspector。`Ctrl+K` 命令中心为规划入口；`Ctrl+Enter` 与既有执行快捷键需统一映射，危险确认不得绑定一个键直接越过审批。页签支持箭头、Home/End 与清晰 focus；modal 开启后焦点留在对话框，关闭返回触发处；Icon-only 控件有中文 label/tooltip。所有可调区域保留按钮级折叠替代拖拽。

## 确认顺序与后续实现门禁

1. 确认七个一级目的地是否符合运维/开发旅程，消除旧壳重复 SQL 入口。
2. 确认顶/左/中/右/下几何、折叠与缩放、结果和 Inspector 的主次关系。
3. 核对 30 页面菜单、九模型统一数据库对象导航及所有二级任务，特别是 MQ 数据库逻辑范围与实例物理持久化/恢复范围的区分。
4. 审查正常/空/加载/错/只读/离线/截断/无权限/长内容/危险确认。
5. 确认上述原型后，才按 M47-U02/U03 抽取共用 tokens/contracts/状态机并做生产前端像素实现；planned 页面先完成合同与能力拒绝，不先复制 fake 数据接成真实功能。

本文件核查依据为 HANDOFF、M47 专题、`web/src/router/index.ts`、现有 AppShell、managementExplorer 与九模型组件，读取范围限制为 25 个文件。当前设计没有执行网络请求、数据库操作、实机安装、真实 AI 测试或后端改造。

2026-10-05 的 MQ 范围修订另核查 `src/SonnetDB/Endpoints/Routes/MessageQueueEndpoints.cs` 的数据库路由与 Read/Write 调用，以及 `src/SonnetDB/Endpoints/Support/EndpointRequestSupport.cs` 的数据库访问校验和 `db + "." + topic` 内部限定名。逻辑数据库归属与实例共享物理 Store 是两个维度；修订只纠正设计分组与说明，不改后端持久化或宣称备份范围已补齐。
