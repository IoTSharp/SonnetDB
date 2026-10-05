# Workbench 持续推进队列

状态：2026-10-05 交接启动。用户授权持续推进、子智能体独立实施、无冲突并行、任务闭环后本地提交。任务状态以当前文件、验证记录和提交为准，本文的待办不表示已完成。

## 每个任务的闭环

1. 读取 HANDOFF、AGENTS、当前 git 状态与本队列，选择有证据的最小切片；记录任务 ID、负责人、文件归属、依赖和验收条件。
2. 为独立任务分配子智能体。主智能体最多并行三个子任务；只在文件范围和依赖不重叠时并行。共享队列、ROADMAP、CHANGELOG、HANDOFF 与 git 操作由主智能体串行维护，子智能体不得自行提交。
3. 设计切片先补可评审原型与状态规范；已有行为的兼容修复和合同可自主实施。最终视觉基线仍待确认的页面继续做设计，不把整个初稿当作用户已验收。
4. 实施后用适合变更的窄测试、必要构建和 UI 验证证明验收条件；由另一智能体独立复核关键合同及 diff。静态/mock、真实服务、三宿主和发布证据分开。
5. 完成最终待提交树的 `dotnet restore SonnetDB.slnx` 和 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`。失败先修正，再重新验证；通过后再改代码须重跑。未通过不得 commit。
6. 主智能体仅 stage 本任务改动，更新 CHANGELOG/HANDOFF/任务验收，执行 Conventional Commit，记录实际哈希并复核提交内容。不得 stage 其它会话文件、构建产物或凭据。
7. 有依赖的下一项在前项闭环后启动；独立项可已在进行。失败保留证据并派修复，不重复创建同一任务。没有可执行事项时等待定时唤醒，不能写成已全部完成。

## 初始任务

| ID | 任务与交付 | 文件归属与依赖 | 验收 / 状态 |
|---|---|---|---|
| WB-00 | 接收设计包、核对现状与提交设计交付基线 | 主智能体串行维护根文档与 design 包；不动生产代码 | 现有 7/30/9/166/60 目录、MQ 逻辑/物理范围、链接、证据可追溯；restore、Format Check、脚本语法与差异检查通过；已提交 `3484aafd` |
| WB-01 | 原型工作区页签、草稿与键盘交互闭环 | prototype/app.js、styles.css；先完成 WB-00。实施者独占这两文件 | 已完成并提交 `88fc7914`：active 页签滚入可见、内存草稿标记/关闭后保留、只读/规划/错误动作门禁、对话框焦点恢复、箭头与 Home/End、桌面/390 Playwright 回归；未接真实服务。 |
| WB-02 | 页面能力、字段与状态逐页定稿 | catalog.js、task-details.js、screen-specs.md；可与 WB-01 并行，各自不改对方文件 | 首批五页已提交 `11de7131`（SQL、KV、MQ、对象 Bucket、Graph Beta）；WB-02B 再完成五个模型页，详见下一行。其余页面和真实宿主接线留待后续批次。 |
| WB-02B | 下一批五模型状态合同 | `prototype/catalog.js`、`task-details.js`、`screen-specs.md`；依赖 WB-02，主会话串行维护测试与共享文档 | 已提交 `c3bfc2f1`：measurement、table、document、vector、fulltext 各补 capabilities 与 normal/empty/error/permission/readonly/longContent 六态，并注入对应任务页；Node 合同测试 2/2、VM 5 页×6 状态、动作/边界 30/30、语法与 diff 检查通过。仅为 REVIEW_DRAFT 原型设计，不代表真实 Server、权限、质量、三宿主或生产迁移。 |
| WB-02C | 五个全局页面状态合同 | `prototype/catalog.js`、`task-details.js`、`screen-specs.md`；依赖 WB-02B，主会话串行维护测试与共享文档 | 已提交 `2ebc6978`：`database-catalog`、`connections`、`notebook`、`history`、`metrics` 各补 capabilities、六态和规范字段并注入任务页；Node 合同测试 3/3、与 WB-02B 联合 5/5、VM 7/9/30/39 guard、语法与 diff 检查通过。仅为 REVIEW_DRAFT 原型设计，不代表真实 Server、权限、三宿主或生产迁移。 |
| WB-02D | 观测与数据流状态合同 | `prototype/catalog.js`、`task-details.js`、`screen-specs.md`、`web/tests/m47-observe-flow-state-contract.test.mjs`；依赖 WB-02C，主会话串行维护共享文档与提交 | 已提交 `07506a99`：`events`、`slow-queries`、`alerts`、`runtime`、`modbus` 各补 capabilities、六态和专用字段，并注入全部对应任务页；Node 合同测试 3/3、两个 JS `node --check`、独立复核 PASS、`git diff --check`，提交前 `dotnet restore` 与 `dotnet format` 通过。保持视图暂停不暂停服务器、慢查询恢复不重跑、告警/诊断不发送或伪造健康、Modbus Runtime/Pending/Audit 与审批边界。仅为 REVIEW_DRAFT 原型设计，不代表真实 Server、权限、现场写入、三宿主或生产迁移。 |
| WB-02E | 五个工作台/数据流页面状态合同 | `prototype/catalog.js`、`task-details.js`、`screen-specs.md` 由子智能体 `/root/wb02e_global_flow_contracts` 独占；主会话串行维护测试与共享文档；依赖 WB-02D | 已提交 `5cbd759a`：固定页面 `summary`、`recent`、`imports`、`transfers`、`jobs`，补 capabilities、六态、专用字段和任务页注入；新增合同测试 3/3，与 WB-02B/C/D 联合 11/11，两个 JS 语法检查与 `git diff --check` 通过，提交前 `dotnet restore` 与 `dotnet format` 通过；复核确认 MQ、恢复、导入、传输及异构位点边界。静态原型仍保持 REVIEW_DRAFT 与真实服务边界。 |
| WB-02F | AI 与 MCP 页面状态合同 | `prototype/catalog.js`、`task-details.js`、`screen-specs.md` 由子智能体 `/root/wb02f_ai_mcp_contracts` 独占；主会话串行维护测试与共享文档；依赖 WB-02E | 已提交 `4f9720e6`：固定页面 `ai-connect`、`copilot-settings`、`rag`、`tool-permissions`，补 capabilities、六态、专用字段和任务页注入；新增合同测试 3/3，与 WB-02B/C/D/E 联合 14/14，两个 JS 语法检查与 `git diff --check` 通过，提交前 `dotnet restore` 与 `dotnet format` 通过；复核确认 typed HTTP/stdio bridge、Provider 质量/成本、RAG profile/generation/revision、权限交集、默认只读和数据外发边界。生产迁移继续冻结。 |
| WB-02G | 治理页面状态合同 | `prototype/catalog.js`、`task-details.js`、`screen-specs.md` 由子智能体 `/root/wb02g_governance_contracts` 独占；主会话串行维护测试与共享文档；依赖 WB-02F | 已提交 `c6781b4d`：固定页面 `users`、`grants`、`tokens`、`approvals`、`backup`，补 capabilities、六态、专用字段和任务页注入；新增合同测试 3/3，与 WB-02B/C/D/E/F 联合 17/17，两个 JS 语法检查与 `git diff --check` 通过，提交前 `dotnet restore` 与 `dotnet format` 通过；复核确认控制平面权限、MQ database grant/实例 Store、一次性 Token、审批终态与错误不重试、单库备份不覆盖共享 `.system/mq` 的边界。生产迁移继续冻结。 |
| WB-02H | 设置与发布页面状态合同 | `prototype/catalog.js`、`task-details.js`、`screen-specs.md` 由子智能体独占；主会话串行维护测试与共享文档；依赖 WB-02G | 已完成并提交 `175cf674`：固定页面 `preferences`、`server-settings`、`studio-host`、`capability-matrix`、`about`，补 capabilities、六态、专用字段和任务页注入；合同测试 3/3，WB-02B–H 联合回归 20/20，两个 JS `node --check`、`git diff --check` 和独立只读复核 PASS。保持宿主范围/服务器配置只读边界、Studio 安装证据独立、版本 manifest/签名未就绪不显示 PASS、真实发行版本与敏感诊断边界；仍为 REVIEW_DRAFT 原型设计，生产迁移继续冻结。 |
| WB-03 | 统一资源身份与能力合同的兼容切片 | `web/src/management-core/resourceDescriptor.ts`、`capabilityRegistry.ts`、`index.ts` 与 `web/tests/management-core-contract.test.mjs` | 已完成并纳入本地提交：MQ identity 含 database + topic，通用工厂强制显式 topic 且 `name === topic`，逻辑 `scope` 与 `persistenceScope` 分开，保留原始拼写和现有 key/路由；实例 MQ 明确 `.system/mq`、共享边界与单库备份排除；Graph 始终 Beta；未知能力安全返回 `unavailable`；Node 6/6、TypeScript、`git diff --check` 通过。仅为静态/内存合同，不代表真实权限、Server、存储迁移、AOT 或三宿主证据。 |
| WB-03C | Explorer 兼容基线证据 | web/tests 下专属 contract 测试与自有 fixtures；只读消费当前 managementExplorer.ts，可与 WB-01/WB-03 并行 | 已完成并提交 `828b7638`：`web/tests/management-explorer-compat.test.mjs`，`node --experimental-vm-modules --test ...` 5/5；覆盖 keys、大小写/冒号、index/backup、MQ 旧 key 与外层 database 选择上下文。发现并记录 index-only fallback 与跨库 MQ key 兼容边界，未改源码。 |
| WB-04 | 按已确认基线迁移全局壳和一级/二级导航 | 预检仅新增 `web/tests/navigation-compat.test.mjs`；生产壳/路由文件仍冻结 | 迁移前兼容预检已完成并通过本地提交门禁：5/5 静态断言覆盖 `/admin`、`/admin/app`、Studio/databases/trajectory-map legacy redirects、现有路由与管理员 meta、7 项 baseNavigation、5 项 adminNavigation、secondaryNavigation 管理员条件、设置/关于入口、setup/auth/admin guards 及 trajectory query→SQL。用户已确认设计基线；本项生产壳迁移仍待独立切片，不把预检写成运行时或三宿主 PASS。 |
| WB-04B | Explorer → SQL 深链接兼容预检 | 仅新增 `web/tests/explorer-routing-compat.test.mjs`；只读消费 `useSqlExplorerRouting.ts`，不改生产源码 | 已完成并通过本地提交门禁：5/5 静态断言覆盖九模型 `tool/model/node` 深链接、database selection、index/backup `{model,node}` fallback、Open-in-SQL allowlist 与 KV/MQ/Bucket 排除、route-only 不自动执行。仅证明源码兼容基线，不代表 Vue 运行时、真实路由、Server 或迁移完成。 |
| WB-05 | 结果、草稿、历史和审批工作流迁移 | 主会话冻结共享文档与集成；WB-05A 独占 `sqlConsole.ts`、`workbenchHistory.ts`、结果/历史组件和自有测试；WB-05B 独占 `WriteApprovalPanel.vue`、`writeApproval.ts` 和自有测试；根会话接线 `useSqlExecution.ts`、`SqlQueryWorkspace.vue`、`SqlConsoleView.vue` 与自有集成测试；依赖 WB-04 兼容预检 | 已完成本轮切片并通过复核：结果最多 10,000 行且保留截断标记；关闭草稿可恢复/丢弃且不自动执行；历史保留 unknown/completeness 并对文本、JSON/JSONL 敏感值脱敏；SQL 取消、传输中断、缺少终态写入 unknown；审批绑定连接/端点/数据库/草稿指纹并在失效时拒绝确认；共享 SQL 工作区已接恢复入口。WB-05 窄测 9/9、全 Web Node 回归 94/94、SQL 回归 11/11、TypeScript、Playwright 3/3 通过；仍不代表真实 Server、三宿主、AOT、安装、发布或全量页面迁移验收。 |

后续模型与三宿主切片从 M47-U01~U09 的实际差距继续选取，复用现有交付，不重做 M29/M32/M34，也不自动改 MQ 存储为 KV/关系表。

## 有界执行与定时检查

定时检查建议每 30 分钟唤醒同一新会话。持续执行不等于单次运行无限长：每次选择一个有界集成切片；子任务一般 20–45 分钟并写清最大项目数、重试次数及墙钟超时。未完成时保存检查点，下一次唤醒接续。任务运行期间不得再次派同一文件；主智能体保持有意义的进度更新。

每次委派必须重复：PowerShell 7；禁止 Graphify、广域编译器扫描和未经授权安装；循环/搜索/重试同时有迭代或项目上限与墙钟超时，先小输入试运行；长进程记录 PID/创建时间/完整命令/父链，成功/失败/取消/超时后仅清理任务自有树；临时文件/下载/日志/目录有归属并在核验绝对路径后回收，保留交付物。禁止按进程名批量终止。

提交、分支切换与统一文档串行。保留 origin/parity-results，禁止合并；不可 reset、清除或覆盖不明来源的改动。当前用户未授权自动推送、发布、部署或外部沟通。机器资源异常时停止新增并行工作，清理归属明确的失控进程，报告检查点。

定时检查在未变化或无可执行事项时保持安静；仅有实质完成（含提交哈希）、失败、阻断或用户需要处理的事项才通知。全部已授权任务确实完成且队列没有下一项后，记录完成并暂停该 heartbeat；不删除工作成果或自动归档会话。

## 会话与自动检查

新会话：持续推进 SonnetDB Workbench，ID `01a10862-bcd5-7d82-ab22-c916c00221a3`，host `local`，本地 SonnetDB 项目。heartbeat：`workbench`（Workbench 持续推进与闭环），ACTIVE，每30分钟检查同一新会话。已从保存配置核对 kind=heartbeat、目标thread及周期；不是每次新建独立会话。旧会话在交接完成消息后停止修改工作区，新会话接管写入、验证和提交。
