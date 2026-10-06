# M47 原型检查记录

日期：2026-10-05（Asia/Shanghai）。结论：**外轮廓、导航、共享流程与状态语义已获用户确认，作为生产实现基线；逐页像素与 WB-05 仍按切片验收，不能视为全量生产完成。**

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
- 实现已提交为 `175cf674`（`feat(m47): add settings and release state contracts`），交接文档哈希同步提交为 `68cd0154`（`docs(m47): record WB-02H commit checkpoint`）；两次提交前的 restore/format 门禁均通过。

## WB-04 迁移前导航兼容预检（2026-10-05）

- 新增 `web/tests/navigation-compat.test.mjs`，只读检查 `web/src/router/index.ts` 与 `web/src/views/AppShell.vue`，没有修改生产壳、导航或路由。
- `node --test web/tests/navigation-compat.test.mjs`：5/5 通过；覆盖 `/admin`、`/admin/app`、Studio、databases、trajectory-map 的 legacy redirect，现有 route names 与管理员 meta，7 项 baseNavigation、5 项 adminNavigation、secondaryNavigation 的管理员条件、设置/关于入口，setup/auth/app/admin guards，以及 trajectory query 到共享 SQL 工作区的兼容映射。
- 该预检只证明 M47 生产迁移前的 legacy 兼容基线；当前 README/navigation/validation 仍为 `REVIEW_DRAFT`，不代表七模块生产导航、视觉像素、真实权限、三宿主或发布验收已完成。未启动服务、未安装依赖。

## WB-04B Explorer → SQL 深链接兼容预检（2026-10-05）

- 新增 `web/tests/explorer-routing-compat.test.mjs`，只读检查 `web/src/composables/useSqlExplorerRouting.ts`，未修改生产源码、路由或依赖。
- `node --test web/tests/explorer-routing-compat.test.mjs`：5/5 通过；覆盖 measurement/table/document/kv/mq/vector/fulltext/bucket/graph 的 `name: sql` + `tool/model/node` 深链接、先选择 database、index/backup 的 `{model,node}` legacy fallback、Open-in-SQL 的 measurement/table/document/index/vector/fulltext/backup 分支、KV/MQ/Bucket 当前排除，以及 route-only 不自动执行。
- 与本轮其它静态合同/兼容测试联合为 21/21 通过（管理核心 6、Explorer 5、导航 5、本切片 5）。该证据只说明源码兼容基线，不代表 Vue 运行时、真实路由、Server、权限、三宿主或生产迁移已完成。

下一步：按已确认基线推进 WB-05 结果/草稿/历史/审批切片；逐页最终像素、真实 Server、三宿主、安装、发布和 AOT 证据继续独立验收。

## WB-05 结果、草稿、历史与审批工作流迁移（2026-10-05）

- 结果与草稿：`sqlConsole` 将客户端预览限制统一为 `DEFAULT_RESULT_PREVIEW_MAX_ROWS`（10,000），服务端 `end.truncated` 优先；结果面板在截断时显示当前预览范围，导出/复制只说明已加载行；没有服务端完成标记时不显示成功且禁止导出。关闭 SQL 页签保存到有界 `closedTabs`，SQL 工作区提供恢复/丢弃入口，恢复只回填草稿与已有快照，不自动执行。
- 历史：新增 `unknown` 与 `completeness`（`complete` / `truncated` / `partial` / `unknown`），未知结果在历史中显示待核对且没有恢复按钮；敏感文本及 JSON/JSONL 的 `token`、`authorization`、`access_token`、`password`、`secret`、`apiKey` 等字段做有界脱敏，避免凭据进入本地历史载荷。
- SQL 接线：取消、传输中断、无 `end` 完成标记及损坏/不完整响应记录为 `unknown + completeness=unknown`，显式服务器错误仍为 `error`；写预览绑定连接、端点、数据库和草稿指纹，确认前通过共享审批上下文校验，失效则标记 stale 并要求重新预览/审批。未知写结果保留请求、服务器终态和审计来源合同，客户端不自动重试或重放。
- 审批面板：显示请求 ID、影响范围、风险、服务器终态、审计来源与未知/待核对提示；`pending`、`unknown`、`stale` 状态禁用确认或要求重新审批。
- 验证：`node --experimental-vm-modules --test web/tests/wb05-results-drafts-history.test.mjs web/tests/wb05-approval-unknown.test.mjs web/tests/wb05-sql-unknown-integration.test.mjs` 9/9；`node --experimental-vm-modules --test web/tests/*.test.mjs` 94/94；`web/node_modules/.bin/tsc.cmd --noEmit --pretty false --project web/tsconfig.json` 通过；共享工作台 Playwright（历史抽屉、SQL 诊断、KV 审批）3/3；`git diff --check` 通过。
- 证据边界：本切片验证的是现有 Web 工作台的合同、状态和宿主接线；不宣称真实 Server 终态查询、跨宿主持久化、AOT、安装、发布或全量页面迁移已验收。其它模型工作台继续沿用各自已有 stale/审批接线，待后续统一迁移批次补齐。

## MQ 导航更正（2026-10-05）

用户指出消息与事件也需要存储，质疑 MQ 为什么被独立分组。核查生产代码后更正初稿：Topic 已按数据库名称限定并使用数据库读写权限，物理日志由 Server 实例共享。前稿把物理存储范围直接用于导航与页签身份，造成数据库语义丢失；现调整为九模型统一在数据库资源树，MQ 同时标明逻辑 scope=database / persistenceScope=instance。仅更正设计，未改变消息存储实现。

生产依据：[数据库范围发布与拉取](../../../src/SonnetDB/Endpoints/Routes/MessageQueueEndpoints.cs)、[Server 注册共享日志](../../../src/SonnetDB/Hosting/SonnetDbServiceRegistration.cs)、[消息日志与 ACK 记录](../../../src/SonnetDB.Core/Mq/SonnetMqStore.cs)。现状采用专用 append-only 日志，不宣称已经复用 KV/关系表或具备跨模型原子提交。单库备份不覆盖共享消息日志的限制继续在 Inspector 与备份流程呈现。

原来的 39 页/166 任务点击记录属于初稿证据；更正后的验证限定 MQ 七任务、统一导航、数据库上下文与持久化说明，另记录结果，避免把旧检查写成新版全部验收。

更正验证：七个 MQ 任务均已实际打开，逻辑数据库与对象上下文保持 factory / DeviceEvents，物理范围说明含 .system/mq；整体恢复任务保留 factory 上下文并明确实例共享 Store 范围。首次断言过度要求恢复说明合并显示“数据库 / Topic”，实际整体恢复以数据库上下文加实例存储说明呈现，读 DOM 核对后调整断言，未修改恢复能力。浏览器 error 日志为空，39/166/60/7 结构与 Node 语法、git diff --check 通过。390 巡检未横向溢出，重复底部结果仍关闭。记录见 [mq-scope-checks.json](screenshots/mq-scope-checks.json)，[更新后的 MQ 页面](screenshots/desktop-mq.png)；原有五张截图已同步新导航。

## WB-06 生产外壳与七模块导航迁移（2026-10-05）

- `web/src/views/AppShell.vue` 将一级 rail 收敛为唯一七模块 descriptor：概览、工作台、观测、数据流、AI 与 MCP、治理、设置；设置按确认的外轮廓置于 footer，关于入口保留。查询/数据/Studio 旧同义入口不再重复渲染为一级按钮。
- `web/src/router/index.ts` 增加 `overview`、`workbench`、`observe`、`flows`、`ai`、`govern`、`settings` 兼容入口，映射到当前真实页面；flows→Modbus、settings→About、ai→RAG 明确是兼容落点，不代表 planned 页面已实现。flows/govern 维持管理员守卫，旧 studio/databases/trajectory-map、setup/auth/admin guards 和 trajectory query→SQL 合同保留。
- active 模块映射覆盖 events、monitoring、Modbus、RAG/Copilot、users/grants/tokens、About 等现有二级/管理员路由；About 与 Settings 不会同时高亮。九模型 Explorer、MQ database + Topic / `scope=database` / `persistenceScope=instance` / `.system/mq` 及旧 key 未修改。
- 验证：`node --test web/tests/workbench-shell-migration.test.mjs web/tests/navigation-compat.test.mjs web/tests/explorer-routing-compat.test.mjs` 13/13；`node --experimental-vm-modules --test web/tests/management-explorer-compat.test.mjs` 5/5；`web/node_modules/.bin/tsc.cmd --noEmit --pretty false --project web/tsconfig.json` 通过；`git diff --check` 通过。独立只读复核 PASS。
- 证据边界：普通用户按现有权限隐藏治理/数据流模块；模块兼容落点和静态/TypeScript 证据不代表 Vue 运行时、真实 Server、三宿主、AOT、安装、发布或全量页面迁移已验收。

## WB-07～WB-09 Explorer 资源身份与深链接（2026-10-05）

- WB-07 将九模型、index 和 backup Explorer 节点投影为 `ResourceDescriptor`，对象页签携带 database/resource/legacyKey；MQ 保持 database + Topic、`scope=database`、`persistenceScope=instance`、`.system/mq` 与单库备份排除，Graph 保持 Beta。自有测试 5/5、Explorer/路由/壳联合 18/18、全 Web Node 102/102、TypeScript/Vite build 通过。
- WB-08 为旧 `tool/model/node`（及 tool-only）链接恢复数据库内对象回选，覆盖九模型、index、backup、原名大小写/冒号与缺失/未知回退；route-only 不执行 SQL。自有测试 5/5、定向回归 20/20、全 Web Node 107/107、TypeScript/Vite build 通过。
- WB-09 为新生成链接增加可选 `database` query，保留旧字段；有效数据库等待列表后选择，未知/缺失数据库安全回退，database-only 与 token 恢复路径可重试。自有测试 4/4、Explorer/路由兼容 10/10、全 Web Node 111/111、TypeScript/Vite build 通过。
- 上述证据均为现有 Web 源码/运行时静态与本地构建范围，不代表真实 Server、权限、三宿主、AOT、安装、发布或全量页面迁移验收。

## WB-10 手动切库后的深链接投影（2026-10-05）

- `SqlConsoleView` 的 route watcher 在新的 `database` token 变化时仍优先选择有效数据库；同一旧 URL 下用户手动切换数据库后，清空旧 projection token，让后续只改变 `model/node` 的导航在当前数据库解析并安全回退，不强制切回 URL 中的旧数据库。route-only 不自动执行 SQL。
- 验证：`node --experimental-vm-modules --test web/tests/explorer-database-route-selection.test.mjs` 5/5；`node --experimental-vm-modules --test web/tests/*.test.mjs` 112/112；Explorer/路由/管理兼容定向 15/15；`web/node_modules/.bin/tsc.cmd --noEmit --pretty false --project web/tsconfig.json` 通过；`npm run build`（vue-tsc + Vite）通过；`git diff --check` 通过；独立只读复核 PASS。
- MQ 仍使用 database + Topic 身份，Graph 仍 Beta，invalid database token 恢复路径未回归。证据不覆盖真实 Server、权限、三宿主、AOT、安装、发布或全量页面迁移；最终仓库 restore/format 门禁在提交前单独执行。

## WB-11 Measurement Workbench（2026-10-05）

- `MeasurementWorkbench.vue` 沿用现有 measurement 路由和旧深链接，在组件上暴露五区外壳锚点、database 与原始 measurement 名称/大小写/旧 key 身份；不改 MQ 存储、Graph 语义、Server API 或三宿主发布。
- 生产组件合同覆盖 `normal`、`empty`、`error`、`permission`、`readonly`、`longContent` 六态。查询、刷新、CSV/JSON 导出和 Measurement/关系表监控保持可用；写入、删除、导入暂存与确认继续唯一进入 `WriteApprovalPanel`，只读和无权限状态禁用写操作。
- 权限错误不仅依赖宿主 prop：SQL 返回 permission/forbidden/unauthorized/access denied 时会派生有效权限态，清理点值、Schema、监控结果和旧请求，导出按钮与导出函数同时拒绝旧载荷。切换数据库或同名 Measurement 会递增 point/monitor request token、清理旧结果并重新查询，旧成功或失败响应不能回写新资源。
- 验证：`node --test web/tests/measurement-workbench-migration.test.mjs` 5/5；`node --experimental-vm-modules --test web/tests/*.test.mjs` 117/117；`npm --prefix web run build`（vue-tsc + Vite）通过；现有 Measurement Playwright 场景 8/8（共享合同渲染、导入审批、分批停止/恢复、监控切换、校正校验、窄桌面布局）通过；`git diff --check` 通过。
- 证据边界：上述为生产 Web 组件、现有 API 接线和本地浏览器 fixture 证据，不等同真实 Server 权限矩阵、固定硬件、AOT、Studio/VS Code/WorkBuddy 宿主、安装、发布或全量九模型验收。最终 `dotnet restore SonnetDB.slnx` 与 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/` 已通过；代码提交为 `390ff526`。

## WB-13 Studio 宿主身份与生命周期（2026-10-06）

- 原位扩展 `/connections` 与 `/server/status`；没有新增未接线 API。身份由 canonical profileId、HTTP(S) baseUrl（保留部署子路径）和 database 原名派生，宿主为 studio-desktop。保存拒绝相对/file/userinfo/query/fragment，保留 Managed Local 旧 `/` 的配置地址解析；非法请求返回无敏感 URL 的400。
- JSON DTO 注册到 StudioBridgeJsonContext；客户端不能覆盖派生宿主字段，Token 不落盘。GUID临时文件在成功、失败路径均回收；未知active profile不继承旧数据库。canStop仅在running、StartedByStudio与真实PID同时满足时为true，external目标不开放停止能力。
- 最终命令：`dotnet test tests/SonnetDB.Studio.Tests/SonnetDB.Studio.Tests.csproj -c Release --no-restore --disable-build-servers -p:UseSharedCompilation=false -m:1 --filter 'FullyQualifiedName~StudioConnectionLibraryTests|FullyQualifiedName~StudioHostContractTests|FullyQualifiedName~StartEmbeddedAsync_WithExistingDatabase_MountsItAndRejectsInvalidSwitch|FullyQualifiedName~StartAsync_WhenExternalHealthyServerOwnsTargetPort_DoesNotStartOrStopIt' --logger 'console;verbosity=minimal'`。退出0，34/34（两个新增类32项与上述两个既有测试；非整个ManagedLocal类），Studio/Core/Server依赖构建通过，含真实bridge GET/PUT与ManagedLocal/external实例证据；独立复核及diff check通过。
- 证据限于本轮6文件；Web客户端尚未消费新增展示字段，干净Windows/WebView2、安装升级卸载、AOT/发行物和全三宿主旅程不在PASS内。既有测试helper的循环/临时目录治理也不由本轮定向PASS宣称全量完成。
- 根在最终待提交树串行执行完整solution restore与Format Check，未通过不提交；实际提交哈希在后续检查点记录。
## WB-14 VS Code 九模型资源与 Workbench 深链接（2026-10-06）

- 六文件：core/types、workbenchResource、新专属Node测试、extension命令注册、package contribution与host/index注册断言；tree/sqlText未改，package-lock未改。恢复315个既有lock依赖时禁用scripts，没有新增工具或依赖。
- Explorer九模型与index/backup保留database、原名、大小写、冒号和旧key；MQ身份含database+Topic，物理instance/.system/mq与单库备份排除，Graph Beta。Open Workbench只导航到部署子路径下的Web Admin，以白名单database/model/tool/node投影，不携带query正文/Token/SecretStorage、不自动SQL或扩大治理权限。
- `npm test`完成TypeScript compile与Node20/20（新增8项）；设置明确本机Code.exe/VSCODE_EXECUTABLE_PATH的Extension Host smoke退出0，验证新命令注册及旧语言能力，未下载VS Code。Studio实施者独立只读六文件复核PASS，diff check通过。
- Host smoke未执行外部浏览器导航；最终Web对象回选、真实登录/权限/Server、VSIX、安装/发布与全三宿主仍待独立验收。任务PID与Host临时目录已核验回收；Git忽略的node_modules/out保留供复用。
- WB-13代码提交为7483584771ba7164b30047eff59f2c6f2e97e96f，已通过完整restore/Format Check。WB-14由根在其最终树再次执行完整门禁，未通过不提交；真实哈希在后续检查点记录。
## WB-12 Document 页面与审批/异步隔离（2026-10-06）

- 两文件：DocumentCollectionWorkbench.vue与document-workbench-migration.test.mjs。保留database/collection原名、大小写与旧入口；五区锚点、六态、permission清空敏感载荷、readonly阶段/确认及高级写面板门禁；Advanced组件本身未改，通过父级身份key与门禁隔离。
- Find/count/aggregate/distinct使用context与request token；切换连接/数据库/同名集合或卸载时旧成功/错误不能回写，审批捕获API/原目标并失效，import冻结模式/items且禁止下一批跨context写。Find预览1000文档；其他读取预算不由该数字扩写。
- 首轮现有浏览器回归6/7，暴露同一集合schema对象替换误清导入停止进度；分离identity重置与同身份metadata刷新后，停止100/101、duplicate_key和导入草稿保留。独立复核另发现旧file失败/native fallback及bridge握手后旧picker副作用，已逐项修复并加入VM行为回归。
- 最终专属Node10/10，全Web `node --experimental-vm-modules --test web/tests/*.test.mjs` 127/127；`npm --prefix web run build`（vue-tsc + Vite）退出0；现有runner限定 `[Dd]ocument` 场景7/7（渲染、Validator/更新/索引/ChangeFeed、update preview/feed、101导入停止）；独立最终复核PASS与diff check通过。必要UI为本地fixture，无真实Server权限声明；Vite现有大chunk提示保留。
- 403后同身份schema刷新不会恢复permission；显式安全恢复入口留WB-18，不能恢复旧写审批。Aggregate/Distinct及高级读取预算、真实权限矩阵、Studio/VSCode全旅程、安装/发布与全量九模型仍独立验收。
- 自有Web/e2e进程由有界runner核对PID/创建/命令/父链后回收，Studio与VSCode代理亦报告自有进程/临时对象已回收；忽略的依赖/构建输出与必要UI证据保留。
- WB-14提交为e7cf2fe512974a1cd9bdbac3f767529f2b065e1a，已通过完整restore/Format Check；WB-12由根在最终树再次执行完整门禁，实际哈希在交接检查点记录。
## 三宿主本轮提交与会话迁移检查点（2026-10-06）

- 实际源代码提交：WB-13 `7483584771ba7164b30047eff59f2c6f2e97e96f`；WB-14 `e7cf2fe512974a1cd9bdbac3f767529f2b065e1a`；WB-12 `54c787551186f74036a1845e25a937d1a268ddc1`。每项均独立复核后按专属文件列表stage，提交前最终树完整restore、原级别Format Check与staged diff check退出0，未混入其它会话文件；源代码提交后工作树clean。Format Check仅有既有workspace加载提示，没有绕过/降低检查。
- 新会话 `01a10d27-d964-7550-9b8e-066447122527`（host=local，SonnetDB local project）已创建并经wait_threads确认开始只读接收；原自动化ID workbench已更新该目标，ACTIVE、每30分钟，保存配置回读匹配。旧会话完成最后文档提交即停止仓库写入；新会话后续从WB-15接续，三个实施者均已结束，无重复派单。
- 本轮实际验证为Web/Studio bridge/ManagedLocal/Extension Host定向证据；WebView2/干净Windows、真实Server权限、完整九模型、VSCode浏览器最终回选/认证、AI/MCP与三宿主发布仍NOT_READY，不因此暂停持续研发。无push、发布、部署或外部沟通；origin/parity-results保持独立。
- 最后迁移记录仅修改共享文档；仍在其最终树重新执行完整restore/Format Check和diff check，未通过不提交。本交接文档提交的实际哈希以git log最后的docs(m47)提交及会话最终输出为准，不能在自身内容预写未知哈希。

## WB-15 Studio Web客户端消费宿主合同（2026-10-06）

- 代码提交：`e1f93a6995e6e1e24e1badfaefb2eb918f31393a`，五个客户端文件加专属Node/浏览器测试与CHANGELOG，共8文件；从`d773a62e`接续，不改Studio/Server API、存储、MQ、Graph或SQL名称合同。根串行维护共享文档与git，三个专属代理实施/夹具/只读复核，无竞争写入。
- 身份来自连接库GET/PUT确认的`studio-desktop/profileId/baseUrl/database`，显示当前工作区数据库原名，保持同端点不同profile身份；不将身份落入浏览器连接持久化，也不伪造宿主确认。生命周期字段缺失、矛盾或URL不合法时拒绝管理，保留Health；external健康/不健康都不开放Start/Stop，Studio归属且canStop才可停止，stopped/failed区分展示。
- bootstrap在第一个await前取代际；status/Start/Stop/openEmbedded拒绝旧响应，同ID endpoint变化同步认证上下文。目录同时保护profile/store与工作区代际，工作区库名可合法不同于连接库默认库名，A→B→A拒绝旧路径。连接PUT串行、专属连接代际与fingerprint匹配后仅消费身份；失败不伪装保存成功，确认不会产生反馈PUT。status的URL/dataRoot须完整合同确认才采纳，外部dataRoot不写本地偏好。
- 专属`node --experimental-vm-modules --test web/tests/studio-host-client-contract.test.mjs`：10/10（3纯合同、6生产store真实Vue/Pinia、1生产composable+deferred目录）；全Web Node回归：137/137，无skip；`web`内`npm run build`（vue-tsc+Vite）退出0，既有大chunk提示保留。最终源码SHA与独立复核一致，diff check通过。
- `dotnet test tests/SonnetDB.Studio.Tests/SonnetDB.Studio.Tests.csproj -c Release --no-restore --filter 'FullyQualifiedName~StudioConnectionLibraryTests|FullyQualifiedName~StudioHostContractTests|FullyQualifiedName~StudioManagedServerHostTests' --logger 'console;verbosity=minimal'`：40/40，无skip。此命令覆盖三个完整测试类，较WB-13的34项定向集合不同；含真实bridge/ManagedLocal/external的本机证据，不能混作浏览器或WebView2安装证据。
- `SONNETDB_E2E_RUNTIME=StudioNative`、video=off、显式现有本机Chrome路径与空闲端口，使用既有`e2e/run-playwright.mjs studio-host-client.spec.ts`：8/8。覆盖原名身份、同端点profile切换、external健康/不健康与native动作拒绝、owned停止、stopped/failed、矛盾合同、迟返状态和bridge失败不伪造健康。夹具仅mock native bootstrap/API，GET/PUT分别确认默认库与当前库；不调用真实Server或桌面文件对话框。此前无runtime的skip与初始化失败不计PASS。
- 独立复核发现的computed缓存、bootstrap覆盖、直接store绕过、保存确认丢失及目录DB误拒均已修复并用行为证据收口。最终独立复核PASS；完整`dotnet restore SonnetDB.slnx`与`dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`在代码提交前最终树退出0，只有既有workspace加载警告，无降低级别；staged diff check通过。共享交接文档提交在最后文档树再次执行相同门禁。
- 自有进程由有界runner记录PID/创建/命令/父链并finally回收；Studio编译服务和Web/e2e/复核PID已核验不存在。临时runner在最后门禁后核验绝对路径回收；忽略的依赖/构建输出与必要UI证据保留，未清理未证实归属对象。没有安装工具、push、发布、部署或外部消息；`origin/parity-results`保持`e4c558538f8d4cc0aa0ba9ffc1f49a8a499376b9`。
- 剩余边界：真实Server权限、原生WebView2/干净Windows、安装升级卸载、固定硬件/长稳/AOT发布、完整三宿主与九模型仍NOT_READY；旧未使用SqlWorkbenchHeader和Explorer全部DB/Schema异步组合不由本切片宣称已验收。下一有界切片为WB-16，然后WB-17/WB-18，heartbeat保持ACTIVE。

## WB-16 VS Code→Web最终回选、认证与显式部署base（2026-10-06）

- 代码提交 `0bb628adde45ad160f05a67057f098fa982c4f85`，从干净 `941550a5` 接续。专属实施/浏览器与Host证据/独立只读复核三代理分工，根串行维护共享文档、集成与git；最终12文件含八Web生产文件、专属Node测试、浏览器spec、Host测试和CHANGELOG。复用WB-14链接生产者及WB-08～WB-10回选，不修改Server、MQ/Graph存储、原名/旧key或发布合同。
- `validatedLoginRedirect` 只接纳已注册内部管理页面，拒绝非字符串/数组、外站/协议相对、反斜线、控制字符、dot segments、匿名/递归登录及未知页面。setup状态请求失败和已认证login入口保留原目标；实际Login提交使用同一校验。旧合法SQL URL仍保留query，不执行草稿。无token/SQL/执行字段仅指扩展新生成链接的白名单，不能推断任意旧URL被全局清洗。
- 部署配置为进程级 `SONNETDB_WEB_BASE_PATH`，默认 `/`，本轮显式值 `/Gateway/SonnetDB/`；Vite base、router history、初始Axios API、默认/旧内建Local连接、health和自动SSE使用同一构建base。仅无Studio身份且非remote的内建旧 `/` 迁移；显式remote与宿主确认端点不覆盖。dev proxy只去掉配置的前缀；生产代理必须另配置对应路径与Server转发，本切片没有部署反向代理。
- 首轮根浏览器14/17未计完成证据：真实索引id的 `table:` 前缀使Sidebar错误展开Tables并隐藏选中项；现改为当前数据库真实Explorer item key→所属group，保留legacy fallback和用户显式折叠。两项登录断言的 `%3A`/`:` 等价序列化误判只改fixture，按origin/path/hash及完整decoded字段比较并拒绝extras/duplicates，未改生产URL迎合字面断言。
- 专属Node **9/9**（真实MemoryRouter/生产guard、Login脚本、Axios、Pinia、Vite配置与EventSource构造/关闭），根最终 `node --experimental-vm-modules --test web/tests/*.test.mjs` **146/146**，无skip；根/代理配置分别 `npm run build`（vue-tsc+Vite）退出0。代理生产HTML的4个asset引用实查均位于 `/Gateway/SonnetDB/assets/`。既有大chunk、Node实验模块提示保留，没有降低检查。
- `web/e2e/vscode-workbench-navigation.spec.ts` 通过Playwright TS hook真实加载扩展 `src/core/workbenchResource.ts`，未复制链接算法。使用已有本机Chrome、BrowserDirect、video=off、独立空闲端口4196/4197，根/代理两部署分别 **17/17**，无skip。覆盖九模型、index/backup、混合case/冒号与完整vector/fulltext/index key、不同连接默认库、East/West同名MQ、缺失/未知安全回退、匿名fixture登录/已认证返回、只导航字段、SSE实际构造URL。index前置不同owner同显示名干扰项，并用实际选中metadata证明完整owner key回选；Graph实际描述保持Beta且页面回选原名，不伪造页面Beta badge。
- route-only证据分开：不执行保存的SQL草稿或写SQL；Measurement/Relation现有自动只读 `SELECT … LIMIT @limit` 预览仍允许且记录数据库，其余目标SQL调用为0。index/backup仍为SQL工作区+Explorer选择，未宣称新增专用模型页。API/认证响应为fixture，EventSource仅捕获构造URL，不建立真实SSE流；不得写成真实Server登录/权限/流式事件验收。
- 扩展release preflight使用已有清单0.4.1通过；`npm test` TypeScript+Node **20/20**。显式本机 `C:\Users\mysti\AppData\Local\Programs\Microsoft VS Code\Code.exe` 的真实Extension Host退出0，调用 **13节点** `sonnetdb.openWorkbench`（九模型、同名West MQ、index/backup与database-only），在 `vscode.env.openExternal` 边界捕获实际命令URI并finally恢复原属性；proxy路径、database原名、node完整key与仅导航字段均断言。旧SQL语言诊断/Signature/QuickFix同次通过。未下载VS Code或工具；拦截浏览器开启不等同OS浏览器/真实Server交接，未生成或发布VSIX。
- 独立最终代码/测试复核 **PASS**，已覆盖SSE追加和Sidebar修复。代码提交前完整 `dotnet restore SonnetDB.slnx` 与 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`、staged diff check退出0，仅既有workspace加载提示；共享交接提交在最终文档树再次执行同一门禁，未沿用旧结果。根runner分别设置命令15～900秒/最多1800次轮询与192个进程预算，记录PID/创建时间/完整命令/父链、finally按身份只回收自有树；子代理无临时/长驻对象。本轮目录 C:\Users\mysti\AppData\Local\Temp\sonnetdb-wb16-73be578e6aa94e93aee31d340ef55950 的删除被自动审查拒绝，仅返回blocked by policy；58个PID身份核验无任务存活，runner/日志和cleanup-status.txt保留，不重试或绕过。旧 `sonnetdb-workbench-handoff-20261006-01` 因自动审查blocked by policy继续保留，不重新删除或绕过。
- 尚未验收：真实Server认证/权限矩阵与部署代理、remote profile的SSE端点、同origin跨部署auth/连接库存储隔离、完整三宿主/九模型生产旅程、AOT、安装升级卸载、固定硬件/长稳、AI/MCP和VSIX/发布。忽略的最后Web产物为代理测试base，后续构建须选择真实目标base；不push、发布、部署或外部沟通，`origin/parity-results` 保持 `e4c558538f8d4cc0aa0ba9ffc1f49a8a499376b9`。下一有界切片WB-17，其后WB-18；heartbeat保持ACTIVE。

## WB-17 Relation Table Workbench（2026-10-06）

- 代码提交 `f19782638789279e8099d7cbdf6f82b40cac12c0`，起点为干净 `main / fefcc72e`。三名专属代理分别实施组件/Node 测试、UI fixture、独立只读复核；根串行管理共享文档、集成、git 和完整门禁。只推进本切片，未启动 WB-18；复用现有设计器、索引、导入导出、ER/DDL、SQL API 和 WriteApprovalPanel，没有修改 Server、宿主或路由合同。
- 根 `node --experimental-vm-modules --test tests/*.test.mjs`（cwd=`web`）**161/161**，无 skip，包含专属 **15/15**：六态/原名/旧 key、200 行请求与超限截断、同名跨库与并发读取、profile/endpoint/Token ABA、普通错误保留输入、403 清载荷、参数值/前后差异、schema/PK/只读同步过期、一次确认及原上下文历史、unknown/no-replay、读取与写入卸载隔离、历史只恢复输入。测试每项 5 秒，结果参数化集合明确限 8 项。
- 独立复核三轮发现并关闭默认 `http_403` 漏判、Schema 刷新解除权限锁存、不完整/传输错误未记录 unknown、首次审批 computed 缓存和异步凭据分派竞态。实际生产 `api/sql.ts` 加既有 Axios 默认异步 interceptor、自有 adapter 的 Node 证据确认：立即切 Token 时 signal 同步 abort，旧 adapter 调用为 0；这不等于真实网络/Server 取消证据。已发送写请求的断连/取消、缺终态、损坏响应、HTTP 408/5xx 显示并记录 `unknown / completeness=unknown`，明确先核对服务器终态；确定 SQL 错误仍为 error，旧审批无自动重放入口。
- 最终 `npm run build`（vue-tsc + Vite，显式 `SONNETDB_WEB_BASE_PATH=/`、BrowserDirect）退出 0；最后 ignored Web `dist` 为根部署测试产物，非发行物。既有大 chunk/Node 实验/NO_COLOR 提示保留，没有降级检查。UI spec 独立 tsc 静态检查由夹具代理通过，没有安装缺失工具或依赖。
- 显式现有本机 Chrome、BrowserDirect、video=off、单 worker、retries=0、空闲端口 4198，`e2e/run-playwright.mjs relational-workbench-migration.spec.ts` 最终 **12/12**，无 skip；涵盖六态、大小写/冒号身份、同名跨库迟返、无 code HTTP403 实际 API 回退、长字段/满页→末页、审批取消/双点击仅一批、DDL/历史只恢复 SQL 草稿。readonly 是动态加载真实组件/Vue/Pinia/Naive UI 的独立 harness，未宣称路由宿主已有真实权限 adapter。认证、Schema 与 SQL 返回均为 fixture，没有真实 Server 请求。
- UI 初轮 **10/12** 未计完成证据：Copilot 固定 FAB 实际遮挡 Next 的指针点击，已在本组件分页栏保留 84px 右侧空间，保持原窄屏换行；取消 selector 同时匹配 header 图标/footer，现限定 footer。复验没有 force click、隐藏 FAB 或取消断言。既有 `management-workbenches.spec.ts --grep 'Relation designer'` **2/2** 通过，覆盖 ALTER 的 ROWVERSION 选项和有符号 64 位默认值原精度。
- 403 锁存不能由同身份 Schema 刷新解除；连接/认证身份变化或显式 permission prop 的恢复才可再次读取。只读允许本页 SELECT/结果导出及纯 DDL；无只读权限合同的设计器、索引、导入、ER 子页暂隐藏。200 行及选定页上限是客户端预览合同，长字段保留类型/NULL 与折叠；未证明 Server 物化/字节预算、全表行数、continuation 或大数据传输上限。
- 代码提交前最终完整 `dotnet restore SonnetDB.slnx` 与 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/` 均退出 0，Format Check 仅既有 workspace 加载提示；最终 staged diff check 通过。返修和 CSS 改动后未沿用旧门禁。共享交接文档提交在最终文档树另执行同一完整门禁。
- 有界 runner 规格 30～900 秒、最多 1800 次轮询/192 个自有进程，记录 PID/创建时间/完整命令/父链并 finally 核身份清理自有树。代码门禁后核验 **46** 个已跟踪身份，**0** 个任务进程存活；最终文档门禁完成后另核验。规格、日志和 cleanup-status 作为验证证据保留于 `D:\source\SonnetDB\artifacts\wb17-validation-20261006`，未创建新的临时目录；旧策略保留的两处 Temp 目录不删除、不换工具绕过。子代理无临时对象/长进程。
- 仍未验收：真实 Server 权限与事务/终态旅程、Server 物化/字节预算、安全子页完整权限、三宿主/九模型全旅程、安装升级卸载、AOT、固定硬件/长稳、AI/MCP 与发布。下一次为 WB-18，heartbeat 保持 ACTIVE；未 push、发布、部署或外部沟通，`origin/parity-results` 不合并或改写。

## WB-18 Document 读取恢复与高级预览预算（2026-10-06）

- 从干净 `5dec4282` 接续；三个专属代理分别实施 Document 组件/Node 合同、浏览器证据和独立只读复核。生产实现仅修改 `DocumentCollectionWorkbench.vue` 与其专属 Node 测试；浏览器证据新增 `document-recovery-budget.spec.ts`。没有修改 Server API、DocumentAdvancedWorkbench 的更新/索引/Change Feed、路由或其它宿主。
- HTTP 401/403 后保持 permission 并清除行、字段、结果、旧游标、写草稿和审批。显式“重新验证读取权限”只发当前数据库/原名集合、当前 profile/endpoint/auth/epoch 的全新 Find `{ limit: 100, skip: 0, collation: "ordinal" }`；不恢复旧过滤器、投影、排序或写操作。成功校验集合原名、文档数组、`id/version/document` 形状和最多 100 项后才解除锁存；失败、迟返、Schema/认证/数据库 ABA、外部 permissionDenied 和卸载均不能解锁。恢复失败使用固定提示，不把响应正文写入页面或历史。
- Aggregate 保留用户 pipeline 并在末尾追加 `$limit: 1001`，Distinct 将 1～1000 的所选预览上限加一个哨兵值；结果先截断再格式化，最多显示/导出 1000 或所选上限，历史记录实际 preview count 与 `complete/truncated`，无 continuation 或虚构下一页。页面明确这些只约束输出/客户端预览，不代表服务端扫描、中间物化、字节或总堆预算。
- 验证：专属 Node **18/18**；全 Web Node **169/169**；最终 TypeScript/Vite build 通过；Chrome/BrowserDirect、单 worker、retries=0 的恢复/迟返/readonly/审批清载荷/Aggregate/Distinct/导出/history 夹具 **11/11**；既有真实 Kestrel `DocumentEndpointTests` HTTP CRUD、权限与 Aggregate 回归 **3/3**。Node、Chrome 与 build 使用有界 runner，记录 PID/创建时间/完整命令/父链并 finally 只回收自有树；最终文档门禁另行执行。真实 Server 新恢复/1001 旅程、服务端物化/字节预算、Advanced 子页、三宿主、安装/AOT/固定硬件/长稳/发布仍未验收。

## WB-19 FullText 上下文、权限与预览预算（2026-10-06）

- 起点为干净 `main / 1d9465fb`，本轮只做FullText兼容切片。本轮提交说明 `feat(m47): isolate FullText context and approval outcomes`，实际哈希以git log为准。实施、浏览器夹具与只读复核三个专属代理分别独占组件/Node、UI spec与只读检查；根串行维护共享文档、验证、git及完整门禁。复用既有检索、Analyzer、Document导入与维护审批；Server、路由、其它模型与Studio/VS Code未修改。
- 保留数据库/集合/索引原名、大小写/冒号、legacy key及六态。请求冻结profile、实际API端点、profile端点、认证、Schema/epoch/API和发起参数；新Search同步使旧Find/Analyzer失效。正常database/auth/Schema ABA、迟返和卸载不能回填；每次读取live endpoint防止markRaw Axios defaults计算缓存，但没有reactive事件的raw defaults A→B→A不声称覆盖。401/403清命中、文档、分析Token、结果、原始导入文本和审批，保留检索输入，同身份刷新不解锁；没有伪造安全读取恢复入口。
- Top-K仍为现有Server的1～100；响应先截断到发起预算，再Find/格式化/显示/导出。超返标truncated，历史记录实际预览数量及完整性；当前Top-K完整不等于所有匹配完整，无新哨兵或continuation。Find校验原集合、IDs及基本响应形状；没有宣称Server扫描、中间物化、字节、总堆或网络传输上限。
- 导入/重建审批仅消费一次并冻结目标、模式、项目与API。重建只有真实同步operation=rebuild_index/status=ok/success=true、合法completedUtc、目标document/原owner/name/fulltext/sync_touch、planned=false/rebuildable与index check才记success；canonical failed/request error为error，planned/缺字段/错目标/传输不完整为unknown。导入最多1000文档、文件10MiB/文本10MiB字符、60秒批次窗口与30秒请求超时；已派首批之后切身份仅记录原目标unknown并停止后续批次，不重放旧审批。部分导入不能写success。
- 根最终 `fulltext-node-final2` 专属 **15/15**、`web-node-final` 全Web **184/184**，无skip；包括实际Axios异步interceptor/adapter的分派前取消0次、忽略abort的旧子请求隔离、markRaw endpoint实时核验、7个maintenance终态fixture与首批已派后ABA隔离。初轮专属14/15是fixture structuredClone拒绝Vue文档代理，已改JSON传输快照，并显式等待首批handler启动；修正后在最终树重新验证，未放宽calls或unknown断言。
- 根 `web-build-final` 的TypeScript/Vite build退出0，显式base=/与BrowserDirect；忽略的dist为测试产物。`fulltext-ui-final` 使用现有本机Chrome、端口4203、单worker、retries=0、video=off，**8/8**无skip：同名跨库、Search401/403、迟返/卸载、Top-K截断先于Find及导出、Find/Analyzer403清载荷、导入/重建审批切库失效。认证/API/SSE为fixture，不代表真实Server权限或宿主permission adapter；readonly/Schema等其余合同由Node覆盖。
- 真实Kestrel既有HTTP兼容回归 **4/4**：`ManagementContractEndpointTests.FullText_*`三项（Search/Analyzer、typed分页、settings/explain/rebuild）与`SchemaAndMaintenanceEndpointTests.Maintenance_HealthCheckAndRebuildIndex_Work`。trx在 `artifacts/wb19-validation-20261006/server-compat-results/server-compat.trx`；没有通过新UI执行真实认证、权限与写终态旅程，不把端点兼容合写为整体完成。
- 独立只读复核最终PASS。reviewer早期直接运行build/Node未持久记录完整进程身份，阶段PASS不计最终证据；其后有界核验无对应任务进程存活，没有终止不明进程。最终测试全部由根有界runner执行：每命令90～900秒、最多1800次/192个自有PID/12层，记录PID/创建/完整命令/父链并finally仅回收自有树；小输入runner smoke通过。最终门禁前记录 **32** 个身份、**0** 个自有进程存活，门禁后另核验。规格、stdout/stderr、身份和cleanup记录保留在 `D:\source\SonnetDB\artifacts\wb19-validation-20261006`。没有新建Temp目录，两处旧策略保留目录只读复用或不触碰，不请求删除、不绕过。
- 提交放行条件为最终待提交树的完整 `dotnet restore SonnetDB.slnx`、`dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/` 均退出0及staged diff check通过；命令/退出值保存于上述目录 `restore-final`/`format-final` 日志，代码再变须重跑。没有降低检查级别或使用旧门禁替代。
- 未验收：新客户端真实Server权限与写终态、安全读取恢复、完整九模型/三宿主、安装升级卸载、Extension Host、AOT、固定硬件/长稳、AI/MCP与发布。heartbeat保持ACTIVE；下一次先接收检查点，再盘点下一未迁移Web模型切片。本轮未push、发布、部署或外部沟通，`origin/parity-results` 保持 `e4c558538f8d4cc0aa0ba9ffc1f49a8a499376b9`。

## WB-20 Vector 原始检索上下文、权限与预览（2026-10-06）

- 干净起点 `main / 2f9a5477672e28bddc1fa9548c04dccaf3e97e8e`；本轮只做 Vector 兼容切片，提交说明 `feat(m47): isolate Vector preview context and permission payloads`，实际哈希以 git log 为准。两名专属代理分别独占生产组件/Node和UI spec；UI代理冻结自身交付后独立只读复核组件/Node，根负责共享文档、最终运行与git，两代理已结束写入。Server、路由、其它模型与Studio/VS Code未改。
- 保留database、measurement/column原名、内部`${measurement}:${column}`和外层Explorer key。原始检索冻结profile、实际API/baseURL、认证、完整索引/measurement Schema、epoch与查询参数；同步失效隔离正常database/resource/auth/Schema ABA、新查询和卸载。读取实时markRaw API endpoint以防计算缓存，完成finally失配会清旧载荷与子页代际；没有reactive事件的raw defaults A→B→A不声称覆盖。
- HTTP401/403只显示固定脱敏错误，清命中、tags/fields、解析/生成向量、结果与旧子页，保留用户raw/text/filter输入。deniedContext保留被拒绝基础身份与原resource；空Schema→原resource、同身份Schema/auth刷新保持锁存，明确不同基础身份或不同已知资源才结束当前资源锁存。外部deny不能由本地读取覆盖；安全恢复另验。只读保留raw检索/导出，Measurement子页传递readonly/permission并以身份/Schema代际key重建；不验收子页写执行终态或真实权限全旅程。
- JSON原始向量拒绝非number/coercion与非有限数，要求已知索引维度精确匹配；Search解析当前raw输入。Top-K仍1～100，先按发起预算slice，再校验保留DTO、格式化/显示/导出；超返明确truncated，历史绑定发起身份/参数与实际preview数量/完整性。Inspector标Distance。无哨兵、continuation或全部匹配承诺；服务端扫描/中间物化/字节/总堆、传输预算仍另验。
- 既有embed-preview只返回vector/dimension，不能验证所选索引的显式Embedding Profile。本轮文本入口明确未就绪、分派embed/search为0，保留raw路径；不使用图片语义搜索status或hash fallback冒充索引Profile。真实Provider/model/revision、Recall、质量、成本和硬件报告未验收。
- 根最终 `vector-node-final2` **17/17**、`web-node-final` **201/201**，无skip；真实Vue VM、实际vector API helper与Axios adapter分别证明分派前身份变化取消0次、捕获Token保持、忽略abort的迟返/ABA隔离、DTO错误脱敏、Top-K先截断（含坏尾项）与完整Schema变化。compileScript+Vue自定义renderer验证真实父模板传递子页门禁及代际重建，子页测试替身不计其写执行器验收。`web-build-final` TypeScript/Vite退出0，base=/、BrowserDirect；只有既有chunk提示，dist是忽略的测试产物。
- `vector-ui-final2` 现有Chrome、端口4204、单worker、retries=0、video=off，**10/10**无skip：原名/跨库、401/403、空Schema锁存、迟返/卸载/ABA、Top-K100先截断和导出/历史、有限数/维度、缺Profile分派0、导入暂存切身份失效与readonly子页。认证/API为fixture，readonly/ABA部分采用真实组件Vite harness。`vector-legacy-ui-final`既有VECTOR校验/Measurement导入暂存回归 **1/1**。初轮UI9/10误将Teleport抽屉限定组件，下一轮8/10默认Chart不显示metadata；修复为真实打开精确Vector抽屉、Raw确认旧载荷后断言deny时目标panel0，最终10/10未放宽断言。独立复核发现空Schema解锁缺口，生产短修后专属/全Web/build/UI分别复验并PASS。首次staged diff check捕获新增Node测试末尾混合CRLF，统一LF后staged检查通过，并再次取得专属17/17；没有语义变更。
- 真实Kestrel既有HTTP兼容 **3/3**：`ManagementContractEndpointTests.Vector_*`，覆盖索引统计、只读检索/过滤、既有embed端点返回，以及标识符/不支持过滤拒绝；trx在 `artifacts/wb20-validation-20261006/server-compat-results/server-compat.trx`。此处既有embed响应兼容不构成索引Profile/语义质量证据；没有新UI真实权限、读取恢复、客户端与Server组合旅程。
- 独立只读复核PASS，两代理无自有常驻进程或临时对象。根有界runner每命令30～900秒、最多1800轮/192个PID/12层，记录PID/创建/完整命令/父链并finally仅回收自有树；小输入smoke通过。门禁前 **46** 个记录身份、**0** 个任务进程存活，门禁/提交后另核验。规格、stdout/stderr、身份、最终源码SHA256与cleanup记录保留在 `D:\source\SonnetDB\artifacts\wb20-validation-20261006`。没有新建Temp目录或工具安装，两处策略保留Temp不请求删除或绕过。
- 提交前置是最终待提交树完整 `dotnet restore SonnetDB.slnx` 与 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/` 两项退出0，以及staged diff check；命令/退出值保存为上述证据目录 `restore-final`/`format-final` 日志，代码再变须重跑。只提交本任务九个文件，未push、发布、部署或外部沟通；`origin/parity-results`保持`e4c558538f8d4cc0aa0ba9ffc1f49a8a499376b9`。
- 未验收边界为新客户端真实权限/安全恢复、索引Profile、服务端资源预算、子页完整写终态、全九模型/三宿主、安装、Extension Host、AOT、固定硬件/长稳、AI/MCP和发行物。heartbeat保持ACTIVE；下次先接收检查点，盘点下一未迁移Web模型并冻结一个有界切片，不重复WB-20。

## WB-21 KV 权限锁存、六态与有界预览（2026-10-06）

- 干净起点 `main / 08bd329f9ec99da0654d0801d34077dbe7bb9734`；本轮只推进KV兼容切片，提交说明 `feat(m47): isolate KV permission state and bounded previews`，实际哈希以git log为准。实施者独占组件/新Node及既有workflow必要断言，UI者独占新spec，第三代理只读复核；三代理已结束写入，根维护六个共享文档/验证/git，没有启动下一切片。
- 保留database/keyspace/key原名、旧 `kv:keyspace`、真实prefix/cursor和既有JSONL/NX/XX/交换/删除/精确版本。增加normal/empty/error/permission/readonly/longContent；readonly保留Scan/Get/统计/导出，write/TTL/delete/import及程序入口均门禁。readonly由真实组件prop harness证明，当前页面未消费完整Server权限元数据，不能称实际授权矩阵通过。
- Scan/Stats/Get/Write任一路401/403均锁存并清value、stats、cursor、result、write/import drafts与审批，错误正文/非allowlist错误码不进入页面或历史；并发成功不能回填，已派写仍按发起身份记录确定计数/unknown而不重放。同身份auth/metadata刷新、空database/profile/endpoint/keyspace与fallback往返不解锁。首轮复核发现空database解除deny，短修后新增Node/UI往返断言；明确新身份或新选择资源才释放本资源锁存，安全读取恢复另验。
- 原有实际API/baseURL/token快照与同步revision、Abort隔离迟返/ABA/卸载，新增profile URL快照及占位锁存。真实client.ts模块仅将Vite编译常量BASE_URL替换为固定fixture地址，真实axios.create/生产request interceptor/实际KV API使用自有transport adapter：Get数据库ABA、Write认证ABA在分派前adapter=0，旧审批不重放、写历史unknown。裸markRaw defaults没有Vue信号的独立A→B→A仍不声称覆盖。
- Scan每页1～1000，累计/Get预览1000，先slice再map/format/display/export；超返弃cursor避免跳过未保留项，累计达到上限停止Load more，prefix/limit变化使旧cursor失效。结果end与历史记录实际preview count/truncated，加载页/客户端上限不代表全keyspace、snapshot、签名或服务端预算。Inspector按真实原始字节数提示，四种模式最多格式化4096字节，长值不自动填入editor；round-trip仍导出已加载记录的完整raw值。Base64仍完整decode，未证明传输、解码内存或总CLR/浏览器heap预算；atomic全响应形状/导入写字节预算另切片。
- 根最终 `kv-node-final` **29/29**（专属17+既有KV12）、`web-node-final` **218/218**、`web-build-final` TypeScript/Vite通过，无skip；base=/、BrowserDirect，只有既有Node实验性/chunk提示。首轮27/27、全Web216/216和build通过后复核补上述锁存修复及两项真实Axios测试，最终分别重跑，源码SHA256保存在validated-code-hashes.json。
- `kv-ui-final` 使用既有Chrome、端口4205、单worker、retries=0、video=off，**12/12**无skip；覆盖原名/跨库、Scan401/403/Get403/Stats403/Write403清载荷、空库/资源往返、并发迟返/卸载/ABA、readonly、真实样式/fixture cursor累计1000与超返、4096四模式/rawJSONL及unknown503不重放。认证/API全为fixture，部分身份/readonly为Vite真实组件harness；没有真实Server权限或新客户端组合旅程。`kv-legacy-ui`既有渲染/批量页/JSONL再暂存/窄桌面/审批 **5/5**。
- 既有真实Kestrel兼容 **6/6**：ManagementContractEndpointTests.Kv_Keyspaces_And_Scan_Cursor_Paginates、KvAtomicHttpContractTests的Anonymous与ReadOnly（各REST/Frame两例）、EmptyValueAcrossRestAndFrame；真实权限拒绝/不突变/空值/精确版本与cursor单列，trx在 `artifacts/wb21-validation-20261006/server-compat-results/server-compat.trx`，不扩大为新UI权限/写终态验收。
- 两轮独立复核最终PASS。根有界runner每命令30～900秒、最多1800轮/192个PID/12层，记录PID/创建/完整命令/父链并finally仅回收自有树；smoke先通过。门禁前 **35** 个记录身份核验、**0** 个自有进程存活；门禁/提交后另核验，日志/规格/身份/cleanup保留于 `D:\source\SonnetDB\artifacts\wb21-validation-20261006`。没有新建Temp、工具安装或删除请求，旧两处策略保留Temp不重试、不绕过。
- 最终待提交树完整 `dotnet restore SonnetDB.slnx` 与原级别 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/` 两项退出0以及staged diff check是提交放行条件，命令/退出值见restore-final/format-final日志，代码再改须重跑。只提交本任务十个文件，不push、发布、部署或外部沟通；`origin/parity-results`保持`e4c558538f8d4cc0aa0ba9ffc1f49a8a499376b9`。
- 显式恢复、新客户端真实Server权限/写旅程、完整atomic DTO/预算、九模型/三宿主、安装、Extension Host、AOT、固定硬件/长期运行、AI/MCP与发行物仍未全量验收。heartbeat保持ACTIVE；下一次先接收检查点，再盘点MQ/Object/Graph等下一未迁移Web模型，冻结一个有界切片。
## WB-22 MQ 权限、实际Topic与有界预览（2026-10-06）

- 干净起点 `main / 92c73a5baa3bfd9aff7a4553eba24382e6420ba5`；本轮只推进MQ兼容切片，提交说明 `feat(m47): isolate MQ preview context and approval outcomes`，实际哈希以git log为准。实施者独占组件/API optional signal/新Node，UI者独占新spec，第三代理全程只读；三代理停止写入，根维护六共享文档/验证/git，本轮不启动下一切片。
- 复用Overview/Messages/Consumers/Configuration、Publish/Ack、JSONL、真实offset窗口与趋势。database、Topic与consumerGroup保留原名，旧`mq:Topic`不变；逻辑scope=database、identity=database+Topic、persistenceScope=instance与`.system/mq`不变，页面说明单库备份不覆盖实例MQ。六态与readonly/permission程序入口门禁完成；readonly保留浏览/采样/导出，完整Server授权元数据尚未由页面消费，prop harness不能称真实授权矩阵。
- Topics/Browse/Stats/Offsets/Retention/Write任一路401/403均锁存并清topics、消息/payload/header、metadata、趋势、结果、写草稿与审批，错误正文固定脱敏；同身份刷新与空database/Topic/profile/端点往返不解除。同步epoch/固定实际API、endpoint/auth/profile和参数隔离迟返/新读/ABA/卸载；props.topic为空时，实际fallback Topic变化亦增epoch/清旧载荷和审批。真实client.ts、MQ API和Axios interceptor使用自有adapter证明Browse数据库ABA、Publish认证ABA在发送前adapter=0；裸markRaw defaults独立无信号ABA仍不夸大。
- Browse每窗1～1000，先slice再map/分类/显示/导出，结果/历史写实际preview count与窗口/超返完整性；Next从最后保留项真实offset+1前进，不从丢弃项跳页、不造cursor/快照。unsafe JSON整数offset禁止Ack/分页/Seek，high-water先验证本身再减1。Inspector四种模式最多格式化4096原始字节，header最多32项/4096字符，明确截断；JSONL保留已加载消息完整payloadBase64与headers。完整Base64仍decode，传输/解码内存、metadata大小、Server扫描/字节和总堆预算不由此预览上限证明。
- Seek最多25窗、每窗1000、总60秒含最终Browse；严格安全offset前进、父signal/原上下文校验，后续迟返不改新上下文selection。自动监控采样最多12轮/60秒单飞，显式可重启；只领取/取消自身请求，不能误取消手工Sample，旧轮迟返不能清新轮归属。Node采用可控timer验证次数/截止/重启所有权，不把静态常量当运行时证据。自动采样仅读metadata，Browse不推进消费者组offset；Ack仍为写操作。
- 审批dispatch前一次消费，冻结原API/上下文与每项实际Topic，混合Topic目标可见；最多1000项/60秒，Publish终态匹配原Topic与safe非负offset，Ack匹配原Topic/consumerGroup与safe nextOffset（允许单调值超过offset+1）。缺失/错目标/unsafe终态、5xx/传输异常或已派后ABA为unknown，绑定原身份、后续停止且旧审批不重放；客户端abort不等于Server未执行。
- 最终`mq-node-final` **22/22**、`web-node-final` **240/240**、`web-build-final` TypeScript/Vite通过，无skip。`mq-ui-final`既有Chrome、端口4206、base=/、BrowserDirect、单worker/retries=0/video=off，**16/16**；原名同Topic跨库、六态、五读端点401/403与Publish403、空身份锁存、迟返/卸载/ABA、readonly程序入口、超返真实offset、unsafe high-water、4096四模式/header及完整JSONL、unknown不重放。所有认证/API为fixture，部分为Vite真实组件prop/program harness；仅两场景显式fixture写各1，其余0，不计新UI真实Server旅程。`mq-legacy-ui`既有消息task、JSONL按文件顺序暂存及compact Inspector **3/3**。
- 既有真实Kestrel兼容 **2/2**：`ManagementContractEndpointTests.Mq_Topics_Offsets_And_Browse`与`Mq_Monitor_ReturnsConsumersRetentionAndDlqCandidates`，真实Publish/Ack铺数据及只读Topics/Offsets/Browse/Retention/Monitor/消费者lag/DLQ候选单列；trx在`artifacts/wb22-validation-20261006/server-compat-results/server-compat.trx`。未用新客户端执行真实权限拒绝、写终态或实例恢复，也未新增Server测试。
- 初轮Node17/17和build通过，Chrome **13/16** 暴露新NDrawer内共享ResultPanel缺inline而隐藏的实际组合问题；修复后真实Raw旧载荷与deny清理断言保留。独立复核另发现high-water减1、Seek后续迟返/总截止、auto截止与请求归属、fallback Topic ABA缺口，修复并增加5项Node后最终22/22、全Web/build/UI均按第三冻结树复验。最终独立只读复核PASS，五个交付源码SHA256见validated-code-hashes.json。
- 根有界runner每条命令30～900秒、最多1800轮/192PID/12层，记录PID/创建/完整命令/父链，finally仅回收自有树；smoke先通过。门禁前 **46** 个身份核验、**0** 个自有进程存活，复用PID保留；门禁/提交后另核验。本轮审计复制时显式用UTC字符串解析JSON时间，活进程小输入检查检出任务pwsh/conhost，结束后核验0，初始日期自动转换造成的interim结果不作为清理证据。日志/规格/身份/cleanup保留于`D:\source\SonnetDB\artifacts\wb22-validation-20261006`；无新Temp目录或工具安装，两处策略保留Temp不请求删除、不重试或绕过。
- 最终待提交树完整`dotnet restore SonnetDB.slnx`和原级别`dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`两项退出0及staged diff check是提交放行条件，命令/退出值见restore-final/format-final和final-gates；代码再改须重跑。只提交本任务11个文件，不push、发布、部署或外部沟通；`origin/parity-results`保持`e4c558538f8d4cc0aa0ba9ffc1f49a8a499376b9`。
- 显式读取恢复、新UI真实Server权限/写与实例恢复、完整metadata/传输/解码/资源预算、九模型/三宿主、安装、Extension Host、AOT、固定硬件/长期运行、AI/MCP及发行物仍未全量验收。heartbeat保持ACTIVE；下次先接收检查点，再盘点Object/Graph下一未迁移Web模型并冻结一个有界切片。
## WB-23 Graph 权限、请求隔离与有界画布（2026-10-06）

- 干净起点 `main / 759f36912fdb6a82edc008db6c02d181b66ebb9e`。提交说明 `feat(m47): isolate Graph canvas and approval outcomes`，实际哈希以git log为准。本轮只推进 Graph Beta 工作台的权限锁存、请求代际隔离、客户端画布预算和维护终态；实施代理独占 `GraphWorkbench.vue`、`graphs.ts` signal、既有 Graph workflow 合同和新 Node；UI 代理独占新 Chrome spec；第三代理全程只读复核。Object 与 Graph 引擎未重做，根维护共享文档、验证和 git。
- overview、visualization、element、audit、export 与写入路径的 401/403 清理画布、诊断、元素、导入、维护与审批载荷并锁存；只读保留画布、元素读取和导出，写入/导入/维护程序入口均拒绝。错误正文固定脱敏，不写入历史；同身份刷新、空数据库/Graph、profile/endpoint 往返不自动解锁，显式安全恢复另验。
- 请求冻结数据库、Graph、实际 Axios client/base URL、Authorization/token、profile 与 revision；AbortController 在代际或卸载时取消未派请求，已派请求迟返不能回写新资源。审批 dispatch 前一次消费并冻结输入；mutation 要求安全非负 sequence 与布尔 `isDuplicate`，导入核对 vertex/edge 数量，维护只把匹配的 `completed + result.isComplete` 记成功，`staged/paused/applying` 和缺失/错误/传输终态记 dry-run/unknown，不重放。
- overview 的 `capabilities.boundedVisualization` 缺失或 false 时不请求、不显示画布。可视化所选上限夹取 10～1000，客户端先截取总元素并仅保留端点仍在集合中的边，保留 Server `truncated`；该上限只约束客户端映射/渲染，不冒称 Server 扫描、传输或总堆预算。Inspector 先保留最多 32 个属性，再对文本/JSON/Blob 字段限制 4096 字符；完整已加载元素仍可用于只读编辑器和 JSON 导出。现有 JSON number 的 ID、版本和 edge endpoint 超出安全整数时拒绝读取、编辑、导入和审批，不扩展为完整 Int64 字符串合同。
- 最终专属 Node `graph-node-final` **27/27**，包含既有 workflow **7** 项与本切片 **20** 项；全 Web `web-node-final` **260/260**，Vite/TypeScript build 通过。Graph Chrome fixture `graph-ui-final` **16/16**（端口4207、BrowserDirect、base=/、单 worker、retries=0、video=off），既有 Graph 浏览器回归 `graph-legacy-ui-final` **3/3**（端口4208），无 skip；真实 ECharts DOM 在页签/权限/能力/身份变化后重建并由浏览器检查，UI/API均为 fixture，不计真实权限。
- 既有真实 Kestrel Graph 兼容 **4/4**：operations overview/有界 visualization/JSON export typed round-trip、权限与预算状态、点读 missingGraph/missingElement、维护审批及重启审计；证据与 trx 在 `artifacts/wb23-validation-20261006/server-compat-results`。该结果不升级为新 UI 真实 Server 权限、实例恢复或三宿主证据。
- 初轮 Node `26/27` 唯一失败为测试夹具在 edge invalid 循环后未恢复 vertex kind，修正为显式 vertex 后最终 27/27；初轮 Chrome `15/16` 暴露只读刷新后 NInputNumber 原生填充未更新安全 editor ID，改用同一真实组件 setup 的显式 setEditorId 程序入口复验 16/16，未放宽读取或门禁断言。独立只读复核 PASS；画布 DOM/ResizeObserver 旧实例复用和 unsafe number 风险已修复并补行为证据。
- 根 runner 每命令有明确 timeout/轮询/PID/父链和 finally 自有树回收；UI 后审计 43 条身份记录、门禁前 49 条均 `liveOwned=[]`，活进程小输入成功检出当前任务 shell、退出后复验0存活。审计用一次有界 PID 条件查询避免逐项查询超时，仍核对创建时间/完整命令/父PID，不终止复用PID。证据目录 `D:\source\SonnetDB\artifacts\wb23-validation-20261006`；两处策略保留 Temp 未删除、不重试或绕过。既有UI首轮因同端口4207冲突未启动测试，改独立4208后3/3，不计失败启动为兼容证据。
- 最终树完整 `dotnet restore SonnetDB.slnx` 与原级别 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`、staged diff check 均是提交放行前置；命令/退出值保存在 `restore-final2`/`format-final2` 和 `final-gates.json`，代码再改须重跑。验证树源码SHA256单列，只提交本任务11文件；本轮不 push、发布、部署或外部沟通，`origin/parity-results`保持 `e4c558538f8d4cc0aa0ba9ffc1f49a8a499376b9`。
- 仍未验收：完整 Graph Int64 字符串身份、真实新 UI 权限/读取恢复、服务端资源预算、三宿主/安装/Extension Host/AOT、固定硬件、长稳、AI/MCP 和发行物；heartbeat 保持 ACTIVE，下一片再盘点 Object。

## WB-24 Object 读取隔离与有界预览（2026-10-06）

- 干净起点 `main / cb32050b54e5f5d3b767f09b7593460ce18152d1`。提交说明 `feat(m47): isolate Object reads and bounded previews`，实际哈希以git log为准。实施代理独占 Object组件、Object/语义API读取signal与新Node，UI代理仅新spec，第三代理全程只读；三代理已停止写入，根维护六共享文档/验证/git。本轮复用已有六页签，只推进读取隔离及有界预览/门禁，不重做Multipart、语义或写执行器。
- 全部桶列表/对象列表、治理、tags/hold、版本、审计、Multipart、processing/thumbnail、语义查询/status/protected image、Range及下载读取冻结实际API/baseURL/Authorization/token/profile/database/Bucket同步epoch，prefix/continuation与选中key/version、模式各请求归属隔离迟返/ABA/新读/卸载，catch/finally/history/URL均校验原归属。固定并发组用allSettled及逐路拒绝观察，先普通500后兄弟403仍锁存。401/403清载荷、结果、URL、写草稿/审批，空身份与同身份刷新不解除；外部permissionDenied及readonly保守门禁，readonly保持读取/下载，22个stage、文件/confirm程序与按钮入口禁写。
- 列表请求1～1000、累计1000、先slice再map；prefix按Server去开头斜线且保留key原名/空白语义，首token null/空串等价，不解析opaque token字面。响应bucket/prefix/保留条目目标需匹配，next非空/变化/未复用；超返不采用会跳项cursor，到累计上限停Load more。实际预览数/完整性入结果/原身份历史，加载窗口不称全桶快照。
- Range请求start/length/end需安全整数，先用差值检查再计算start+(length-1)防先舍入再减1，长度最多4096；固定版本/模式。真实206 Content-Range起点/终点/total需匹配安全窗口及冻结version；先Blob.slice至请求/声明长度/4096最小值，再arrayBuffer与格式化；超返明确truncated。200只允许start0首窗保守预览。head.bucket/key为API回填、sizeBytes为返回Content-Length，不作为Server目标/全对象长度证据。Blob已完整接收，未证明传输/扫描/总堆预算；超Blob fixture仅为客户端slice证据，不算HTTP Range合规Server证据。
- 审批身份/草稿改变即失效；已派写迟返不回写新URL/Multipart状态/历史，native和Web fallback文件dialog固定打开时身份，Web change消费归属，ABA后旧file不得填新草稿；合法直接当前file change保留。写执行器完整终态/unknown/一次消费、批次预算仍另片，不能把本轮门禁/隔离写成写入旅程全验收。
- 最终第三生产树 `object-node-final3` **20/20**、`web-node-final` **280/280**、`web-build-final3` TypeScript/Vite通过，无skip。最终Chrome `object-ui-final` **15/15**：12条deny路径、六态、原名/跨库同Bucket、有效22stage readonly零写、列表clamp/累计/目标/token、Range版本/模式/整数/slice、prefix/selection/version/认证/API/ABA/卸载及URL；BrowserDirect、base=/、端口4209、单worker、retries0、video off。既有Object恢复Multipart、语义搜索/窄桌面/390浏览器 `object-legacy-ui` **4/4**，独立端口4210。全部UI/API/auth为fixture，未执行新UI真实权限/真实OS对话框或三宿主旅程。
- 既有真实 Kestrel Object 兼容 **4/4**：`ObjectStorage_PutRangeCopyTagsDeleteMarkerAndPresign_Works`、`ObjectStorage_ListContinuationTokenAndDeleteObjects_Work`、`ObjectStorage_VersionsLifecycleAndAudit_Work`、`ObjectStorage_GovernancePolicyQuotaRetentionLegalHoldAndStats_Work`。Release/单 MSBuild worker、共享编译及 node reuse 关闭，600秒命令上限，无 skip；trx 在 `artifacts/wb24-validation-20261006/server-compat-results/server-compat.trx`。本轮没有改 Server 或这些测试，不计新 UI 真实权限旅程。
- 首次兼容 runner 参数索引配置错误导致 MSBuild 拒绝启动测试；根重建显式参数数组后通过上述4项。原失败规格/日志保留，未算测试失败或完成证据。进程审计小输入在验证运行时检出自有 dotnet/父链，完成后7条身份、0存活；审计命令在活进程时按设计拒绝“已清理”结论。
- 首轮Node **17/18**暴露真实安全整数左结合舍入漏洞，build报告一个未用变量；修复并增声明长度/两Web picker断言后最终20/20。首Chrome **9/15**的六deny组均失败于全window URL回收，单场景创建stack诊断证明唯一未回收URL为MapLibre模块初始化script worker（text/javascript、direct define2 maplibre-gl.js:24:46），与Object payload无关；最终只精确排除此源，所有其它made/未知URL继续逐个revoke，deny前的两个真实Object thumbnail/hit URL另断言made+revoked。没有降低Object载荷或回收断言，MapLibre全局worker生命周期独立；最终第三生产+UI独立只读复核PASS。
- 根有界runner每命令明确timeout/最多1800轮/192PID/12层，记录PID/创建/完整命令/父链并finally仅清自有树。最终门禁前 **53** 条身份核验、0自有进程存活，复用PID保留；日志/规格/身份/trace/审计与源码SHA256保留于 `D:\source\SonnetDB\artifacts\wb24-validation-20261006`。无新Temp目录或工具安装，两处策略保留Temp未删除、不重试或绕过。
- 最终待提交树完整 `dotnet restore SonnetDB.slnx` 与原级别 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`、staged diff check均为本地提交放行前置，命令/退出值见restore-final/format-final/final-gates；代码再改须重跑。仅提交本任务11文件，保留origin/parity-results `e4c558538f8d4cc0aa0ba9ffc1f49a8a499376b9`；不push/发布/部署/外部沟通。
- 下一片优先Object写终态/unknown/一次消费及批次预算；完整语义/Multipart/Server资源预算、新UI真实权限/恢复、真实OS对话框、九模型/三宿主、安装/Extension Host/AOT、固定硬件/长稳、AI/MCP与发行物仍分别待验。heartbeat保持ACTIVE，本轮不启动下一片。

## WB-25 Object 写终态、一次消费与批次预算（2026-10-06）

- 本地切片从 `a2bc3f1a6af69df5e4716f0f41c80013ca7c64e3` 接续；范围限于现有 Object 22 个暂存入口及审批执行器，不改 Server、路由或其它宿主。生产改动为 `web/src/components/ObjectBucketWorkbench.vue`、`web/src/api/objectStorage.ts`，Node 合同为 `web/tests/object-workbench-migration.test.mjs`，浏览器证据为 `web/e2e/object-write-terminal.spec.ts`。
- 审批快照在 dispatch 前一次消费；最多1000项，60秒只约束客户端启动新操作，单请求继续沿用现有 Axios 超时，不解释为 Server 已取消。原 context/API/身份/输入在 stage 时冻结，草稿变化不能改变批准请求；重复确认、并发确认、超限批次均不重放或新增 dispatch。
- 成功必须验证实际响应目标及必要字段：ObjectInfo、桶 setter、presign、copy、Multipart init/part/complete、legal hold、processing 入队、semantic backfill 枚举与批删逐项批准 key。批删缺失、重复、外来 key 或损坏项为 unknown；完整明确单项错误为 error，并保留已确认影响数。处理与语义结果只证明入队/枚举，不称派生处理完成。
- void DELETE/删桶/Multipart abort API helper 验证 HTTP 204；单对象 DELETE 另验证 `x-amz-delete-marker=true`、非空 `x-amz-version-id` 和 ETag。Server 省略 nullable 字段按实际 source-generated JSON 合同兼容；非 null 批准字段必须回显且更新时间有效。已证明 Multipart 写终态不被伴随读取刷新失败改成 unknown。
- 结果未知使用固定脱敏提示“结果未知，请核对原目标；不要重放审批。”并写入 history `status=unknown`、`completeness=unknown`、原 database/bucket/context；401/403 保持权限锁存并记录脱敏 error。失败/断连/缺终态/错目标不记录 success，已开始和已确认计数单列。
- 验证：专属 Node **32/32**；全 Web Node **292/292**；Vite/TypeScript build PASS；Chrome fixture **5/5**（端口4211，真实组件+fixture API，含并发重复确认、真实 PUT 错目标/缺终态、断连及 synthetic 1000/1001 批次）；既有 Object/语义浏览器 **4/4**（端口4210）；既有真实 Kestrel Object **4/4** 与 Multipart **2/2**。新 UI fixture 与真实 Server 结果分开，未宣称真实新 UI 权限或三宿主验收。
- 独立只读复核 PASS：真实 Server DTO/Handler、nullable省略、DELETE status/头、Copy/Part无目标回显边界、processing/backfill任务接受、partial计数、Multipart刷新隔离与UI请求路径均核对。桶创建响应日期目前只校验字符串类型，真实Server返回合法DateTimeOffset；没有将本片写成全响应Schema验证或新UI真实Server权限旅程。
- 证据根 `artifacts/wb25-validation-20261006`，runner 每命令设置 timeout、PID/创建时间/完整命令/父链并 finally 仅回收自有树；门禁前24条身份核验、0自有进程存活。started计数只表示客户端开始执行，不是实际网络发送计量；虚拟时钟测试证明60秒后停止下一操作并保留首项确认的影响。无新Temp或安装，两处策略保留Temp不触碰。
- 最终完整 `dotnet restore SonnetDB.slnx`、原级别 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/` 与 staged diff check 为提交前置；首轮两命令退出0，format有工作区加载警告、无格式差异，文档收口后按最终树复验。退出值与源码SHA256单列于final-gates/final-tree-hashes，实际提交见commit-checkpoint，提交说明 `feat(m47): validate Object write terminal outcomes`，仅本任务10文件；无push/发布/部署/外部沟通，parity-results保留。
- 下一次优先九模型新UI真实权限/恢复与Studio/VS Code剩余合同/旅程，不重复WB-25。三宿主、安装、Extension Host、AOT、固定硬件、长稳、发布、完整Multipart/语义与Server资源预算继续 NOT_READY；heartbeat保持ACTIVE。
