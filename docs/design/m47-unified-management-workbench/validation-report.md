# M47 原型检查记录

日期：2026-10-05（Asia/Shanghai）。结论：**可交付外轮廓、导航、典型页面与共享流程评审，仍为 REVIEW_DRAFT。** 用户尚未确认，生产前端实现未开始。

## 实际检查

| 项目 | 结果与范围 |
|---|---|
| 目录结构 | 七个一级、30 个全局页面、九模型、166 任务，模型任务 60；页面 ID 唯一，任务标题与目录顺序一致，示例表格宽度一致 |
| 页面点击 | IAB 逐项打开 39 个入口和 166 个任务页签，均有可见内容；浏览器 error 日志为空。只证明静态渲染与点击接线 |
| 二级任务 | 字段、专用表格、JSON/SQL 输入、图表配置、导入映射、说明及 Inspector 接线；规划任务使用说明视图，不能执行后端调用 |
| 草稿 | SQL 输入切换页面后恢复；字段草稿按页面/任务保存在内存中。未验证跨刷新、跨宿主或持久恢复 |
| 选中行 | 点击后样式、Inspector 和 aria-selected 同步；工作区打开与关闭为独立按钮 |
| 删除门禁 | 未确认时禁用；错误大小写仍禁用；对象原名 FactoryTopology + 风险勾选后启用。返回编辑，没有执行确认动作 |
| 无权限 | 主数据、底部载荷和 Inspector 数据隐藏；读取动作不可用 |
| 导入停止 | 示例显示已停止后续批次、已提交 300 / 未提交 200；已提交批次不写成回滚或无重复续传 |
| 全局状态 | 正常、空、加载、局部失败、只读、离线、无权限、错误、超时、截断、长内容 11 状态逐项切换 |
| 响应式 | 1600×1000 桌面；1280×900、1100×800 保留左侧并默认收起检查器；390×844 默认左右面板和重复底部结果收起。后三尺寸未发现文档横向溢出，宽表自身横向滚动 |
| Graph | 四顶点、三边，与 Inspector 的 Pump-01 → Station-01 关系一致；保持 Beta |
| 结果能力 | 只有 SQL 主编辑器显示图表 / EXPLAIN 示例；其它页面仅表格 / JSON，专用任务使用自己的数据，避免展示异模型温度或计划 |
| 语法与空白 | Node --check 检查 app.js、catalog.js、task-details.js、preview.mjs；git diff --check 无诊断 |

页面点击详细记录见 [page-task-checks.json](screenshots/page-task-checks.json)。这些是设计夹具检查，不代表 39 页面、166 任务的生产功能已完成。

## 截图与有界检查

- [桌面外壳](screenshots/desktop-shell.png)：1600×1000，顶部、一级轨、左资源树、中央 SQL、右 Inspector、下结果和状态栏。
- [Graph](screenshots/desktop-graph.png)：同一示例快照，Graph Beta 与作用域可见。
- [危险审批](screenshots/desktop-approval.png)：原名与影响范围确认；本地流程预览。
- [1280 桌面](screenshots/desktop-1280.png)：检查器默认收起。
- [390 巡检](screenshots/mobile-inspection.png)：单主表，按需打开导航/Inspector。

先以两页、五任务试运行（45 秒上限），再检查剩余 37 页、161 任务（90 秒、39 页/166 任务总数上限）。状态检查最多 11 项、25 秒；单交互设置 1.2–2 秒超时。没有无界轮询或完整测试扩张。

设计复核进行了两轮。首轮发现的重复列表、Graph 快照、异模型结果、偏好尺寸、辅助文字对比、删除占位指令和嵌套按钮问题均已修正，并重新捕获截图。最终只读复核 10 项（5 源文件 + 5 图）未发现阻断这次评审的新问题。多页签时当前页签自动滚入可见区域，留待逐页交互定稿确认。

## 机械检查限制

impeccable detector 只扫描原型目录，一次执行、退出码 0、JSON `[]`。但检查器明确报告 **DEGRADED**：htmlparser2/css-select/css-tree/domutils 不可用，降级 regex；CSS 变量、选择器及计算对比度未被完整评估。因此只记录“regex 未报问题”，不能记为完整 impeccable PASS 或可访问性验收。

人工复核辅助文字 token #607085 对 canvas/status 约为 4.75:1 / 4.70:1。原生 dialog、Escape、可见焦点、页签箭头及原名门禁已有设计基础；完整键盘树导航、屏幕阅读器、各字段错误提示与全部状态组合仍需后续独立核验。

## 生产边界与交接

- web/src 与 VS Code 生产代码未修改；没有运行生产构建、真实 Server、AI/MCP、Native AOT、安装或 Extension Host 验收。
- 九模型保持原生语义，统一数据库逻辑上下文导航；SonnetMQ Topic 的数据库命名空间/权限与物理实例存储分别说明，当前单库目录备份不包含共享 MQ 日志。保留 SQL 名称大小写合同，Graph 始终 Beta。
- 采用 HTML 评审布局与交互；未调用图片生成模型，未宣称使用 GPT Image 2.5。
- 当前是全目录、任务规范及可点击设计稿。真实数据量、接口能力、宿主边界和逐页最终像素仍待确认与细化。
- 文档与原型保留未提交，等待设计评审；本轮未运行提交前 restore/format，也未执行 commit/push。下次提交必须在最终工作树执行仓库要求的完整格式门禁。
- 原型预览两次运行均在 loopback、白名单文件及 30 分钟自动超时范围内。最后的 Node PID 66288 / PowerShell 父 PID 20460 已经停止并核验不存在；旧 PID 51720 已被无关 MCP 进程复用，未操作该进程。没有遗留任务进程或临时文件，已移除本轮撤回源码后留下的空目录，并恢复浏览器临时视口。已加载原型可继续交互；预览服务结束后刷新需重新启动，HTML 文件仍可直接在普通浏览器打开。

## WB-00 交接基线门禁（2026-10-05）

- `dotnet restore SonnetDB.slnx`：通过，所有项目均为最新。
- `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`：通过；未发现格式差异。
- `node --check`：`prototype/app.js`、`catalog.js`、`task-details.js`、`preview.mjs` 均通过。
- `git diff --check`：通过；设计包链接、7/30/9/166/60 目录标记及 MQ `scope=database` / `persistenceScope=instance`、实例共享 `.system/mq` 与单库备份边界已回读核对。
- 以上仅证明设计交付基线与静态原型证据可审查，不代表生产前端、真实 Server、三宿主、AOT、安装或发布门禁通过。
- 设计交付基线已在本地提交 `3484aafd`；本记录与该提交同属 WB-00，未推送远端。

## WB-01 / WB-02 / WB-03C 有界切片证据（2026-10-05）

- **WB-01**：`node --check` 通过；本地 loopback Playwright 在桌面与 390×844 视口验证 8 个页签 active 自动滚入、active 关闭后的相邻回退、SQL 草稿关闭后重开与“草稿”标记、对话框点按/Escape 返回触发器、只读/错误/规划动作门禁、箭头与 Home/End。预览 Node 进程已按 PID/父进程核验并停止，浏览器会话已关闭。
- **WB-02**：`prototype/catalog.js`、`task-details.js` 语法检查和 VM 元数据断言通过；SQL、KV、SonnetMQ、对象 Bucket、Graph Beta 五页各有专用能力/状态/字段合同，缺页注入有 guard。Bucket 未强行添加不存在的 planned 能力；其现有/延伸边界仍需真实宿主能力响应。
- **WB-02B**：为 measurement、table、document、vector、fulltext 五页补齐 capabilities、normal/empty/error/permission/readonly/longContent 六态与专用字段，并让所有对应任务页复用页级合同。Node 合同测试 `web/tests/m47-page-state-contract.test.mjs` 2/2 通过；五页×六态、动作/边界和任务注入断言通过。静态设计继续保留 TAG/FIELD、JSON 属性键、显式 Profile、hash fallback、全文重建任务 ID 与质量证据边界；未接真实 Server、权限、质量、三宿主或生产前端。
- **WB-02C**：为 `database-catalog`、`connections`、`notebook`、`history`、`metrics` 五个全局页面补齐 capabilities、六态与 screen-specs 专用字段，并将合同注入所有对应任务页。`web/tests/m47-global-page-state-contract.test.mjs` 3/3 通过，另与 WB-02B 状态测试 2/2 联合通过；VM 断言确认 7 sections、9 models、30 区域页/39 总页 guard。连接测试分离 URL/健康、认证、数据库/对象权限与 capabilities；Notebook 全规划且不自动执行；历史恢复不重放；指标示例不冒充采样或质量/成本证据。未接真实 Server、权限、三宿主或生产前端。
- **WB-03C**：`node --experimental-vm-modules --test web/tests/management-explorer-compat.test.mjs` 通过 5/5。该测试使用 Node 实验性 `SourceTextModule`，直接省略该 flag 会失败，因此验证命令固定保留 flag。测试是静态/源码兼容证据，不覆盖真实 Server、Vue UI、AOT 或三宿主。
- 当前仍保留两项 Explorer 源码边界：`firstExplorerKey` 在 index-only schema 下返回空；生产 MQ item key 仍是 `mq:${topic}`，跨数据库必须由外层上下文携带 database。WB-03C 固化证据，WB-03 只提供兼容合同，没有擅自改 Explorer、源码存储布局或恢复实现。
- 切片提交：WB-01 `88fc7914`、WB-02 `11de7131`、WB-03C `828b7638`；提交后工作树 clean，未推送远端。

## WB-03 资源与能力合同（2026-10-05）

- 新增 `web/src/management-core/resourceDescriptor.ts`、`capabilityRegistry.ts`、`index.ts` 与 `web/tests/management-core-contract.test.mjs`。资源工厂保留 database、原始名称、大小写、冒号和旧 Explorer key；index/backup 路由可显式保留已有 key。
- MQ identity 必须同时携带 database 与 topic，兼容 key 保持 `mq:${topic}`；逻辑作用域为 `database`，当前物理持久化边界为实例 `.system/mq`，共享标记为 true，单库备份标记为 false。该合同没有修改 MQ 存储或恢复实现。
- Graph 描述始终标记 `stability=beta` 与 `beta=true`。`CapabilityRegistry` 只管理稳定能力 id 的内存快照；`resolve` 对未知 id 返回 `state=unavailable`，不执行请求、不代替权限判断；快照及条目不可变。
- `node --experimental-vm-modules --test web/tests/management-core-contract.test.mjs`：6/6 通过；`web/node_modules/.bin/tsc.cmd --noEmit --pretty false --project web/tsconfig.json`：通过；`git diff --check`：通过。通用 MQ 工厂拒绝缺省 topic 或 name/topic 不一致；Explorer key 文档明确只用于路由兼容，跨库选择必须配合 database。
- 证据范围是 Web TypeScript 静态/内存合同与兼容夹具，不代表真实 Server/API、权限策略、存储迁移、Native AOT、VS Code/Studio/WorkBuddy 三宿主或发布门禁通过；`get` 返回未登记时仍为 `undefined`，调用方应使用 `resolve` 获取安全的 unavailable 描述。

## WB-02D 观测与数据流状态合同（2026-10-05）

- 原型页 `events`、`slow-queries`、`alerts`、`runtime`、`modbus` 已补齐页面级 capabilities、`normal/empty/error/permission/readonly/longContent` 六态、专用正常态字段和任务页注入；新增 `web/tests/m47-observe-flow-state-contract.test.mjs` 固化页级对象身份与边界断言。
- `node --test web/tests/m47-observe-flow-state-contract.test.mjs`：3/3；`node --check`：`prototype/catalog.js`、`prototype/task-details.js` 均通过；`git diff --check`：通过；独立只读复核：PASS。
- 事件暂停只影响本地视图，不暂停服务器；慢查询恢复只回填输入且 Explain 需手动执行；告警规则、评估、通知和运行时诊断仍为规划能力，不发送通知、不标记健康、不伪造服务器终态；Modbus 保留 Runtime/Pending/Audit、数据库绑定及批准/拒绝边界。
- 证据仍限于 REVIEW_DRAFT 静态原型与 Node 合同测试，不代表真实 Server、权限、现场写入、三宿主、AOT、安装、发布或生产迁移通过；原型不发起网络请求。

## WB-02E 工作台与数据流状态合同（2026-10-05）

- 原型页 `summary`、`recent`、`imports`、`transfers`、`jobs` 已补齐页面级 capabilities、`normal/empty/error/permission/readonly/longContent` 六态、专用正常态字段和任务页注入；新增 `web/tests/m47-global-flow-state-contract.test.mjs` 固化页级对象身份与边界断言。
- `node --test web/tests/m47-global-flow-state-contract.test.mjs`：3/3；与 WB-02B/C/D 合同测试联合：11/11；`node --check`：`prototype/catalog.js`、`prototype/task-details.js`；`git diff --check`：均通过。独立只读复核：PASS；复核曾发现并促成修正 jobs 位点字段断言，最终合同与测试一致。
- 实例概览保持九模型数据库逻辑上下文及 SonnetMQ 的 `scope=database`、`persistenceScope=instance`、共享 `.system/mq`、当前单库备份排除；最近工作区恢复只回填上下文并重校验身份/能力，不自动执行或重放写入。导入没有真实持久 resume 时要求重新选择文件；对象传输区分服务器 checksum、complete/abort 和取消终态；任务页区分 generation、revision、offset、object version，MQ 位点含 database + Topic 且恢复影响实例共享 Store 需单独核验。
- 证据仍限于 REVIEW_DRAFT 静态原型与 Node 合同测试，不代表真实 Server、权限、导入/对象现场写入、任务恢复、三宿主、AOT、安装、发布或生产迁移通过；原型不发起网络请求。

## WB-02F AI 与 MCP 状态合同（2026-10-05）

- 原型页 `ai-connect`、`copilot-settings`、`rag`、`tool-permissions` 已补齐页面级 capabilities、`normal/empty/error/permission/readonly/longContent` 六态、专用正常态字段和任务页注入；新增 `web/tests/m47-ai-mcp-state-contract.test.mjs` 固化页级对象身份与边界断言。
- `node --test web/tests/m47-ai-mcp-state-contract.test.mjs`：3/3；与 WB-02B/C/D/E 合同测试联合：14/14；`node --check`：`prototype/catalog.js`、`prototype/task-details.js`；`git diff --check`：均通过。独立只读复核：PASS。
- AI Connect 保留 typed Streamable HTTP 已有、配置向导/stdio bridge/tools/list 规划且不发网络请求；Copilot 连接/目录/usage 与真实质量、成本门禁分开；RAG 续跑要求真实任务、profile、generation、expected revision，安全整数溢出拒写；工具权限按用户授权、工具边界、宿主策略交集，默认只读，显式数据外发且凭据永不外发。
- 证据仍限于 REVIEW_DRAFT 静态原型与 Node 合同测试，不代表真实 MCP/tools/list、Provider、模型质量/成本、权限、三宿主、AOT、安装、发布或生产迁移通过；原型不发起网络请求。

## WB-02G 治理页面状态合同（2026-10-05）

- 原型页 `users`、`grants`、`tokens`、`approvals`、`backup` 已补齐页面级 capabilities、`normal/empty/error/permission/readonly/longContent` 六态、专用正常态字段和任务页注入；新增 `web/tests/m47-governance-state-contract.test.mjs` 固化页级对象身份与边界断言。
- `node --test web/tests/m47-governance-state-contract.test.mjs`：3/3；与 WB-02B/C/D/E/F 合同测试联合：17/17；`node --check`：`prototype/catalog.js`、`prototype/task-details.js`；`git diff --check`：均通过。独立只读复核：PASS，逐页确认 5 页六态、normal.fields、任务页级对象复用与治理边界。
- 用户/实例控制平面与数据库 grant 分开；MQ Topic Read/Write 使用 database grant，实例共享 Store 恢复/全局配置另行核验。Token 明文只在创建成功时一次显示，列表/历史/导出脱敏且有效期来自真实服务。审批保留影响、风险、有效期、请求 ID、服务器终态和审计来源，错误不自动重试；备份保留 manifest/checksum/验证/覆盖预览，当前单库备份不覆盖共享 `.system/mq`，跨库恢复需独立合同。
- 证据仍限于 REVIEW_DRAFT 静态原型与 Node 合同测试，不代表真实权限、Token、审批执行、灾备恢复、三宿主、AOT、安装、发布或生产迁移通过；原型不发起网络请求。

## WB-02H 设置与发布页面状态合同（2026-10-05）

- 原型页 `preferences`、`server-settings`、`studio-host`、`capability-matrix`、`about` 已补齐页面级 capabilities、`normal/empty/error/permission/readonly/longContent` 六态、专用正常态字段和任务页注入；新增 `web/tests/m47-settings-state-contract.test.mjs` 固化页级对象身份与证据边界。
- `node --test web/tests/m47-settings-state-contract.test.mjs`：3/3；与 WB-02B–G 状态合同联合回归：20/20；`node --check`：`prototype/catalog.js`、`prototype/task-details.js`；`git diff --check`：通过；独立只读复核：PASS。
- 偏好只影响当前宿主界面且不扩大服务端预算/权限；实例配置没有在线 API 时保持只读、敏感字段脱敏；Studio Web 原型不伪造 Native bridge、Managed Local 或 Start/Stop；能力矩阵在 manifest、`uiContractVersion`、校验/签名及三宿主证据未齐时保持未就绪；关于页版本来自真实发行物，诊断复制先预览脱敏。
- 证据仍限于 REVIEW_DRAFT 静态原型与 Node 合同测试，不代表真实 Server、三宿主、干净 Windows、Extension Host、安装、发布、AOT 或生产迁移通过；原型不发起网络请求。
- 实现已提交为 `175cf674`（`feat(m47): add settings and release state contracts`）；本提交后的交接文档哈希同步仍需单独通过同一仓库门禁。

## WB-04 迁移前导航兼容预检（2026-10-05）

- 新增 `web/tests/navigation-compat.test.mjs`，只读检查 `web/src/router/index.ts` 与 `web/src/views/AppShell.vue`，没有修改生产壳、导航或路由。
- `node --test web/tests/navigation-compat.test.mjs`：5/5 通过；覆盖 `/admin`、`/admin/app`、Studio、databases、trajectory-map 的 legacy redirect，现有 route names 与管理员 meta，7 项 baseNavigation、5 项 adminNavigation、secondaryNavigation 的管理员条件、设置/关于入口，setup/auth/app/admin guards，以及 trajectory query 到共享 SQL 工作区的兼容映射。
- 该预检只证明 M47 生产迁移前的 legacy 兼容基线；当前 README/navigation/validation 仍为 `REVIEW_DRAFT`，不代表七模块生产导航、视觉像素、真实权限、三宿主或发布验收已完成。未启动服务、未安装依赖。

## WB-04B Explorer → SQL 深链接兼容预检（2026-10-05）

- 新增 `web/tests/explorer-routing-compat.test.mjs`，只读检查 `web/src/composables/useSqlExplorerRouting.ts`，未修改生产源码、路由或依赖。
- `node --test web/tests/explorer-routing-compat.test.mjs`：5/5 通过；覆盖 measurement/table/document/kv/mq/vector/fulltext/bucket/graph 的 `name: sql` + `tool/model/node` 深链接、先选择 database、index/backup 的 `{model,node}` legacy fallback、Open-in-SQL 的 measurement/table/document/index/vector/fulltext/backup 分支、KV/MQ/Bucket 当前排除，以及 route-only 不自动执行。
- 与本轮其它静态合同/兼容测试联合为 21/21 通过（管理核心 6、Explorer 5、导航 5、本切片 5）。该证据只说明源码兼容基线，不代表 Vue 运行时、真实路由、Server、权限、三宿主或生产迁移已完成。

下一步：先确认外壳和导航，再确认共享流程，随后按页面逐项定稿；只有确认的设计进入生产实现。

## MQ 导航更正（2026-10-05）

用户指出消息与事件也需要存储，质疑 MQ 为什么被独立分组。核查生产代码后更正初稿：Topic 已按数据库名称限定并使用数据库读写权限，物理日志由 Server 实例共享。前稿把物理存储范围直接用于导航与页签身份，造成数据库语义丢失；现调整为九模型统一在数据库资源树，MQ 同时标明逻辑 scope=database / persistenceScope=instance。仅更正设计，未改变消息存储实现。

生产依据：[数据库范围发布与拉取](../../../src/SonnetDB/Endpoints/Routes/MessageQueueEndpoints.cs)、[Server 注册共享日志](../../../src/SonnetDB/Hosting/SonnetDbServiceRegistration.cs)、[消息日志与 ACK 记录](../../../src/SonnetDB.Core/Mq/SonnetMqStore.cs)。现状采用专用 append-only 日志，不宣称已经复用 KV/关系表或具备跨模型原子提交。单库备份不覆盖共享消息日志的限制继续在 Inspector 与备份流程呈现。

原来的 39 页/166 任务点击记录属于初稿证据；更正后的验证限定 MQ 七任务、统一导航、数据库上下文与持久化说明，另记录结果，避免把旧检查写成新版全部验收。

更正验证：七个 MQ 任务均已实际打开，逻辑数据库与对象上下文保持 factory / DeviceEvents，物理范围说明含 .system/mq；整体恢复任务保留 factory 上下文并明确实例共享 Store 范围。首次断言过度要求恢复说明合并显示“数据库 / Topic”，实际整体恢复以数据库上下文加实例存储说明呈现，读 DOM 核对后调整断言，未修改恢复能力。浏览器 error 日志为空，39/166/60/7 结构与 Node 语法、git diff --check 通过。390 巡检未横向溢出，重复底部结果仍关闭。记录见 [mq-scope-checks.json](screenshots/mq-scope-checks.json)，[更新后的 MQ 页面](screenshots/desktop-mq.png)；原有五张截图已同步新导航。
