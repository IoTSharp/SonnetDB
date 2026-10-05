# SonnetDB 交接记录

交接日期：2026-10-05（Asia/Shanghai）<br>
当前分支：`main`<br>
远端基线：`origin/main`
项目目录：`D:\source\SonnetDB`

## 当前状态

- 本地 `codex/*` 分支已全部确认可达 `main` 并删除。
- 远端 `origin/codex/*` 已无存活分支；本地缓存引用已 prune。
- `origin/parity-results` 仍保留，未合并、未删除、未改写。它是 parity 结果专用分支，任何后续智能体都不得把它合并到 `main` 或其它开发分支。
- WB-04 迁移前导航兼容预检已通过最终门禁，工作树状态以 `git status` 为准；此前 WB-03 提交 `b084680e` / `f2158143` 已保持干净。M47 设计评审包与前置工作台切片已按各自提交记录保存；用户确认原型以前仍不进入生产前端迁移。`bin/`、`obj/`、`.nupkg` 等构建产物不纳入提交。
- 最新授权（2026-10-05）：用户要求创建新 Workbench 会话，持续自主推进独立任务、按不冲突的文件范围并行分派智能体、定时检查、验证闭环后本地提交并继续下一项。先设计再按确认基线迁移的顺序保留；此次授权不是“166 个任务页签均已验收”的证据，可自主推进原型细化、现有语义兼容的共享合同与工作台修复。

## 会话铁律

- 新会话开始前先阅读本文件，接收上次会话留下的未完成事项、待完善内容、风险边界和注意事项。
- 会话结束前更新本文件，写清已完成、未完成、下一步、验证结果和需要提醒后续会话的内容；计划、局部 PASS 和完整完成必须保持区分。
- 该规则已写入 `AGENTS.md` 的“会话交接”约束；本文件固定放在仓库根目录并随交接提交。

## 已提交变更

- `41ff1afe chore(repo): 固化 parity-results 分支隔离铁律`：把 parity 分支治理规则写入 `AGENTS.md`。
- `d03e36d6 feat(sql): 实现 measurement TAG 与时间组合分组首批合同`：完成 M45-C01 的首批实现、测试、EXPLAIN 合同、验证审计和边界文档。
- `01bbae97 docs(roadmap): 固化 SonnetDB 4.5 推进路线与专题`：提交此前保留的路线、审计和三个设计专题文档。
- 本次交接提交（当前 HEAD，提交说明为 `docs(repo): 建立会话交接记录与铁律`）：提交本文件和会话开始/结束铁律；若本文件再次更新，提交哈希以 `git log --oneline -5` 为准。

## M45-C01 首批实现

主要文件：

- `src/SonnetDB.Core/Sql/Execution/SelectExecutor.cs`
- `src/SonnetDB.Core/Sql/Execution/SqlExplainPlanner.cs`
- `tests/SonnetDB.Core.Tests/Sql/SqlExecutorMeasurementGroupByTests.cs`
- `docs/benchmarks/m45-measurement-tag-grouping.md`
- `docs/audits/m45-measurement-grouping-20261004.md`

当前支持一个或多个 TAG 与可选 `time(duration)` 组合键的裸聚合投影。分组按时间桶和 TAG 原值稳定排序；`COUNT(*)` 使用所有 FIELD 时间戳并集，同一 series、同一时刻只计一行；稀疏 FIELD 的 `COUNT` 为 0，其它空聚合为 `NULL`；TAG 分组的 Int64 `SUM` 使用 checked Int64 累加并在溢出时拒绝。SQL/直接 planner 的名称绑定遵守 GH-Issue #211，EXPLAIN 声明分组阻塞及结果物化边界。

当前明确拒绝：FIELD/未知/重复分组键、未分组投影、复合标量聚合投影、measurement `HAVING`、残差或 Geo WHERE、TAG/别名/多键排序，以及多 series 的 FIRST/LAST。现有仅 `GROUP BY time(...)` 或未分组聚合路径的历史行为没有被这批代码宣称统一。显式 measurement SQL 物化预算仍拒绝聚合；没有新增总 CLR heap 上限、spill 或流式首行承诺。

## 验证证据

- `dotnet build src/SonnetDB.Core/SonnetDB.Core.csproj -c Release --no-restore`：通过，0 warning / 0 error。
- 定向 Release 回归：181/181 通过，覆盖新增 TAG/time 分组、旧 SELECT/聚合/EXPLAIN、扩展聚合、流聚合和 SQL 表达式。
- `dotnet restore SonnetDB.slnx`：通过，项目均为最新。
- `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`：通过。
- `git diff --check`：通过。

完整范围和边界见 [M45-C01 验证记录](docs/audits/m45-measurement-grouping-20261004.md) 与 [分组合同](docs/benchmarks/m45-measurement-tag-grouping.md)。上述结果是本机定向证据，不等同固定硬件、真实 Server/SDK/MCP、AOT 发布、容量、长稳或生产门禁通过。

## 后续顺序

按当前 `ROADMAP.md`：

1. 完成基线与合同冻结中仍未覆盖的 M44-A01/A02/A06、M45-C01/C02/C09、M46-S01/S09 和 V45-X06；不要把本批首实现写成完整 M45-C01 完成。
2. 推进 M45-C02 版本化聚合 state 与 M45-C03/C04/C05/C08 的更新删除、增量物化、event-time 和恢复预算；复用 M43 的位点、任务和投递边界。
3. 在独立证据窗口执行 M20 parity/nightly、M19/M25 固定硬件、M27 真实模型/双网、M29 安装、M35/M36/M40/M41/M42/M43 现场门禁。

恢复工作前先执行：

```powershell
& 'C:\Program Files\PowerShell\7\pwsh.exe' -NoLogo -NoProfile -Command '$PSVersionTable.PSVersion.ToString(); git status --short --branch; git log --oneline --decorate -8; git branch --all --no-color'
```

任何分支整理都必须再次核对并保留 `origin/parity-results`；禁止使用它作为合并来源。

## 本次会话（2026-10-04）

### 已完成

- 只读核查 dbx、Tabularis、DBeaver/DataGrip/pgAdmin、MongoDB Compass、RedisInsight、Grafana/InfluxDB、Kafka UI/RabbitMQ、Milvus/Qdrant、OpenSearch、MinIO、Neo4j 以及 WorkBuddy MCP 公开资料。
- 新增 [M47 统一数据库管理工作台与三面发布设计](docs/design/m47-unified-management-workbench.md)，冻结“一套核心、三个宿主、三个发布物”的产品方向、九模型工作台、竞品采纳边界、AI/MCP 入驻、原型目录、共享合同和 U01~U10 规划。
- 在根 `ROADMAP.md` 增加 Milestone 47，并把 M47-U01~U09 纳入 4.5 必选范围、U10 保留为 P2 条件；没有重派 M29/M32/M34、M18/M24 或 #258/#259 已有实现。
- 更新 `docs/roadmap-total-milestone.md` 的新增里程碑索引，并在 M29 设计 README 中标记 M47 为三面统一与发布层的后续基线。

### 验证

- `git diff --check`：通过。
- 文档引用、M47 编号和路线图链接已通过 `rg` 核对。
- 本次只修改规划文档，没有运行代码构建或测试；没有声明任何实现、安装、真实模型、固定硬件或长期发布门禁通过。

### 未完成与下一步

1. 由产品/架构评审确认 M47-U01~U10 的范围、优先级和是否纳入 4.5 完成判定。
2. 先评审新的 M47 HTML 原型，确认外轮廓、全菜单/导航、对话框与状态，再逐页细化与确认最终像素基线。暂不直接开始 ResourceDescriptor 或生产前端开发。
3. 已确认的设计才按 U01/U02/U03 抽出 ResourceDescriptor/Capability Registry、contracts、design tokens、Result/Approval/History 状态机，保持 key 兼容；不要先复制 Vue 页面到 VS Code。
4. M47-U08 的 `sonnetdb mcp` stdio bridge、WorkBuddy 用户级/项目级配置生成和 tools/list/health 自检仍未实现。
5. M47 的三面真实旅程、Studio 干净 Windows、VS Code Extension Host、版本 manifest 和发布证据仍是 `NOT_READY`。

### 风险边界

- M47 是 UI/宿主/发布层规划，不改变九模型存储语义、SQL 名称大小写合同、MCP 只读边界或现有写审批。
- VS Code 不直接嵌入完整 Web Admin；共享的是 contracts/core/tokens/状态语义，宿主仍各自适配 auth、storage、文件和通知。
- WorkBuddy 的自托管 MCP 与官方默认 Connector 是两条路径；官方目录审核、数据驻留和外部服务责任不能由本地文档推断已通过。

## 本轮前端设计（2026-10-04 至 2026-10-05）

### 用户方向与已完成

- 用户要求结合现有底子，先全面规划菜单、顶部/左/右/下/中央布局、对话框与提示、一级/二级导航，再逐页设计；原型确认后才像素实现。这是当前推进门禁。
- 新增 [M47 设计评审包](docs/design/m47-unified-management-workbench/README.md)：navigation、layout、interaction、screen specs、可点击 HTML、任务数据、截图与检查记录。
- 延续 M29 浅色工业数据库 IDE、现有品牌与 Fluent 蓝。七个一级模块、30 全局页面、九模型工作区、166 任务页签（模型 60），Graph Beta。初稿按物理存储将 MQ 单列；用户提出异议后已纠正为数据库逻辑上下文内九模型统一导航，物理存储范围单独说明。
- HTML 已覆盖外壳、典型任务、连接/命令/历史/导入/写审批/原名删除/未知写结果，以及 11 状态。所有数值为静态示例，规划能力明确拒绝执行；草稿仅页面内存，不持久保存。
- 起初 ResourceDescriptor 切片的本轮生产源码改动已撤回。web/src 与 VS Code 生产代码无本轮变更；没有发布或调用真实服务。
- 更新专题、ROADMAP、CHANGELOG 以“设计确认后实现”为下一步；保留进入本轮时已有未提交规划改动。

### 验证与未完成

- 39 个入口、166 任务页签实际点击检查，无浏览器 error；目录结构、JS 语法、git diff --check 通过。
- 1600/1280/1100/390 布局、危险删除门禁、草稿切页恢复、无权限载荷隐藏、导入停止、aria-selected 与 11 状态已做限定检查。首轮主要问题修复后截图重捕获，最终设计复核可用于外壳/导航/共享流程评审。
- impeccable 机械扫描降级为 regex，不能算完整机械/可访问性 PASS。完整键盘树导航、屏幕阅读器、所有状态组合、逐页最终像素、真实数据与三宿主验收仍未完成。多页签当前项自动滚入可见区域须在最终交互定稿时确认。
- 证据范围见 [validation-report.md](docs/design/m47-unified-management-workbench/validation-report.md)。没有生产构建、Server/AI/MCP、AOT、安装或 Extension Host PASS。
- 本轮没有调用图片模型，没有使用或安装所谓 GPT Image 2.5；采用可验证交互的 HTML。

### 下次继续

1. 先接收用户对外壳、导航和共享流程的评审意见；不要自行进入生产实现。
2. 按意见调整原型与规范，然后按工作台 / 九模型 / 全局治理顺序逐页定稿，明确已有、延伸和规划能力。
3. 已确认切片才实施共享合同与像素迁移，复用现有代码。真实 Server/宿主/发布门禁必须另取证据。

当前分支 main，HEAD 仍为 3a59f2c9。未 commit/push：设计稿待确认，且本轮未执行最终工作树提交前 restore/format 门禁；不得沿用上一轮 PASS 直接提交。HANDOFF 随本轮设计改动保留待提交，origin/parity-results 仍保留。

资源回收完成：两次本机白名单预览均已结束，最终 Node 66288 / 父 PowerShell 20460 核验不存在。旧服务 PID 51720 已复用为无关 MCP，未触碰。子代理报告无临时文件/长驻进程；撤回源码后留下的本轮空 management-core 目录已移除。临时浏览器视口已恢复，已加载原型标签保留；HTTP URL 不是永久地址，刷新需要重新启动 preview.mjs，普通浏览器可直接打开 HTML。

### MQ 导航更正（用户跟进）

用户质疑 SonnetMQ 为什么单列，指出事件/消息同样需要存储。生产代码核查发现：业务 MQ API 使用数据库命名空间与数据库权限，Server 物理日志共享在 DataRoot/.system/mq，当前采用自有追加日志。前稿混淆逻辑归属与物理存储范围；原型与规范现改为数据库资源树内九模型统一呈现，MQ 的物理持久化及恢复边界在 Inspector/备份流程单独说明。MQ 页签身份必须包含数据库和 Topic，不能删除数据库身份。没有修改生产存储、KV 或关系表；复用底层存储与跨模型原子性仍属后续架构合同，不能由导航更正推断完成。单库备份不覆盖共享 MQ 的事实保留。

本次只复验受影响的 MQ 七任务、作用域、九模型分组和390巡检布局；166任务数及60模型任务数不变，浏览器无error，JS语法与diff检查通过，六张截图已同步。旧39页/166任务点击证据仍属于初稿，不能当作此次全量重验。仍未提交/未进入生产实现。

本轮预览 Node PID 29892 / 父 PowerShell 16796 已通过所属会话停止，并核验不存在；临时视口已恢复。子代理无临时文件或常驻进程，已加载 MQ 原型保留。

## 持续 Workbench 新会话交接（2026-10-05）

用户已明确授权新会话持续实施 Workbench 任务并在验证闭环后执行本地 git commit；无须对已授权的可逆工作和普通实施选择再次请求许可。没有授权自动 push、发布、生产部署、外部消息或改写历史。先执行 [Workbench 持续任务队列](docs/design/m47-unified-management-workbench/work-queue.md)，根会话负责集成、检查与提交，子智能体只在分配的文件范围实现或复核。

新会话应使用同一本地项目 D:\source\SonnetDB，接收当前所有未提交设计交付，不创建额外用户会话来充当子任务。每次定时唤醒先核对未完成任务/智能体和最新工作树，避免重复派单或与运行中的工作冲突；未完成事项推进一次有界切片并记录检查点，不用无界长循环模拟定时器。

建议顺序：先冻结任务目录/文件归属和设计交付基线，再修复原型交互与逐页状态，推进与现有 API/名称/权限兼容的资源合同；已确认页面再迁移五区壳/导航、结果/草稿/审批等生产切片。所有提交必须在最终待提交树执行完整 restore + format 校验；每个独立任务包含对应 CHANGELOG、HANDOFF、验收与真实边界，不能把静态原型或定向测试写成全部生产门禁通过。

最新基线仍为 main / 3a59f2c9，main 相对 origin/main ahead 4。本轮所有设计与交接仍未提交，未执行本轮完整 restore/format；首个新会话任务应核对和完成这些门禁再建立设计文档提交。origin/parity-results 必须保持独立。其他活跃会话可能触及 SonnetDB，首轮派单前检查文件归属，不 stage 或覆盖其它任务改动。

已创建新会话 **持续推进 SonnetDB Workbench**：`01a10862-bcd5-7d82-ab22-c916c00221a3`，host=local，SonnetDB 本地项目。定时检查 **Workbench 持续推进与闭环**（automation ID `workbench`）已 ACTIVE，每30分钟唤醒该新会话；保存配置已核对 heartbeat 类型、目标thread与周期，无同项目重复自动化。新会话已开始只读接收并准备并行盘点，当前会话将在发出“交接完成”消息后停止仓库写入，提交与后续实施交新会话负责。本次新增 work-queue 并更新 README/HANDOFF，git diff --check 通过；无新增长驻进程、临时文件或commit/push。

## 当前 Workbench 会话实施检查点（2026-10-05）

- 交接完成消息已收到；旧会话停止写入。本会话确认使用 `D:\source\SonnetDB`、`main`、HEAD `3a59f2c9`，`origin/main` ahead 4，`origin/parity-results` 保留且未操作。
- 只读盘点确认主树的五个既有修改与 23 个 M47 设计文件属于本次授权交付；无冲突索引条目，其他注册 worktree clean。子智能体仅做只读盘点，未写入、暂存或提交。
- WB-00 门禁已通过：`dotnet restore SonnetDB.slnx`、`dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`、四个原型脚本 `node --check`、`git diff --check`；验证记录已写入 M47 `validation-report.md`。
- WB-00 设计交付基线已提交：`3484aafd docs(m47): establish workbench design review baseline`。随后冻结 WB-01/WB-02/WB-03C（或合同）文件归属，分派独立实施与复核。不得把 REVIEW_DRAFT 或静态/mock 证据写成生产、真实 Server、三宿主、AOT、安装或发布 PASS。

### WB-01 / WB-02 / WB-03C 当前检查点

- WB-01 已完成并待提交：仅改 `prototype/app.js`、`styles.css`；页签可见性/草稿/焦点/键盘/Home-End/错误与规划动作门禁已由 Playwright 桌面与 390×844 回归，预览 PID 85328 已停止并核验不存在。
- WB-02 已完成并待提交：仅改 `prototype/catalog.js`、`task-details.js`、`screen-specs.md`；首批 SQL、KV、MQ、Bucket、Graph Beta 五页完成状态矩阵与能力字段，缺页注入 guard 通过。
- WB-03C 已完成并提交：仅新增 `web/tests/management-explorer-compat.test.mjs`；`node --experimental-vm-modules --test ...` 5/5 通过。已记录 index-only fallback 与 MQ 旧 key 需外层 database 的源码边界，未修复生产 Explorer。
- WB-03 资源与能力合同已完成并纳入本地提交：新增 `web/src/management-core/resourceDescriptor.ts`、`capabilityRegistry.ts`、`index.ts` 与 `web/tests/management-core-contract.test.mjs`；评审后补强通用 MQ 工厂必须显式提供 topic 且 `name === topic`，并明确 Explorer key 只用于路由兼容。Node 6/6、TypeScript 与 `git diff --check` 通过；补强提交为 `f2158143 fix(m47): tighten MQ resource identity contract`，工作树已恢复干净。
- WB-04 迁移前导航兼容预检已完成并通过本地提交门禁：仅新增 `web/tests/navigation-compat.test.mjs`，5/5 通过，覆盖 `/admin`/`/admin/app`、Studio/databases/trajectory-map legacy redirects、现有路由/meta、7+5 导航及管理员条件、setup/auth/admin guards 和 trajectory query→SQL；未修改 `web/src`。生产迁移仍受 `REVIEW_DRAFT` 用户确认门禁约束，提交为 `f479f54b test(m47): add navigation compatibility preflight`。
- WB-04B Explorer→SQL 深链接兼容预检已完成并通过本地提交门禁：仅新增 `web/tests/explorer-routing-compat.test.mjs`，5/5 通过，覆盖九模型 `tool/model/node`、database selection、index/backup fallback、Open-in-SQL 边界和 route-only 不自动执行；独立复核 PASS。生产源码未修改，当前变更提交哈希以 `git log -1` 核对。
- 前置切片已分别提交：WB-01 `88fc7914 feat(m47): close prototype interaction loop`；WB-02 `11de7131 docs(m47): define initial page state contracts`；WB-03C `828b7638 test(m47): add explorer compatibility evidence`；WB-04 `f479f54b test(m47): add navigation compatibility preflight`；WB-04B `6699a6d9 test(m47): add explorer routing compatibility preflight`。WB-03、WB-04 与 WB-04B 均通过提交前 restore、Format Check 与最终差异检查；所有静态/mock 证据仍与真实 Server、AOT、三宿主、安装和发布门禁分开。

## WB-03 资源与能力合同（2026-10-05）

- 新增 `web/src/management-core/resourceDescriptor.ts`、`capabilityRegistry.ts`、`index.ts` 和 `web/tests/management-core-contract.test.mjs`。合同保留 database、原始名称/大小写、冒号与兼容 key；index/backup 可显式传入已有路由 key。
- MQ identity 同时含 database 与 topic，旧 Explorer key 保持 `mq:${topic}`；逻辑作用域为 database，物理持久化作用域为 instance，路径 `.system/mq`、共享 true、单库备份 false。没有修改 MQ 存储、恢复或目录布局。
- Graph 描述强制 `stability=beta` / `beta=true`。能力注册表是纯内存且快照不可变；`resolve` 对未知能力返回 unavailable，不执行请求或权限判断。
- 独立验证：`node --experimental-vm-modules --test web/tests/management-core-contract.test.mjs` 6/6；`web/node_modules/.bin/tsc.cmd --noEmit --pretty false --project web/tsconfig.json` 通过；`dotnet restore SonnetDB.slnx`、`dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/` 与 `git diff --check` 均通过。基础提交为 `b084680e feat(m47): add shared resource and capability contracts`，评审补强为 `f2158143 fix(m47): tighten MQ resource identity contract`，均未推送。
- 边界：本切片只提供 Web 静态/内存合同，不代表真实 Server/API、权限、存储迁移、Native AOT、VS Code/Studio/WorkBuddy 三宿主、安装或发布证据；`CapabilityRegistry.get` 未知 id 仍返回 `undefined`，需要安全回退时调用 `resolve`。

## 当前 Workbench 检查点（WB-02B，2026-10-05）

- 在 `main`、`HEAD 0e029fd9` 的干净基线继续实施 WB-02B；子智能体只改 `docs/design/m47-unified-management-workbench/prototype/catalog.js`、`task-details.js`、`screen-specs.md`，主会话新增 `web/tests/m47-page-state-contract.test.mjs`，未修改生产 `web/src`、路由或宿主代码。
- 五个模型页 `measurement`、`table`、`document`、`vector`、`fulltext` 已补 capabilities 与 `normal/empty/error/permission/readonly/longContent` 六态，专用字段与 screen-specs 对齐；任务详情对五页全部注入同一页级合同。保留 TAG/FIELD 与 SQL 名称、JSON 属性键、显式 Profile、hash fallback、全文重建任务和质量证据边界。
- 独立复核先发现五页 `normal.fields` 缺失 screen-specs 专用字段，已补齐并把缺口固化为测试断言：measurement 的时区/选中 Series，table 的物化预算/草稿差异，document 的 Sort/文档 ID/Payload 呈现模式，vector 的显式 Profile，fulltext 的 Analyzer/重建任务 ID。
- 已通过：PowerShell 7.6.6；两个 `node --check`；`node --test web/tests/m47-page-state-contract.test.mjs` 2/2；五页×六态、动作/边界 30/30、任务注入 VM 断言；`git diff --check`。独立复核更新后的最终 PASS 仍需收到；随后必须在最终待提交树运行完整 `dotnet restore SonnetDB.slnx` 与 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`，通过后才可提交。
- 当前状态：WB-02B 已提交为 `c3bfc2f1`（`feat(m47): add next model state contracts`），提交后工作树保持 clean；设计包仍为 `REVIEW_DRAFT`，不能推进 WB-04 生产迁移或将静态原型写成真实 Server/权限/质量/三宿主验收。剩余 29 页状态合同待后续有界批次；不 push、不发布、不部署。

## 当前 Workbench 检查点（WB-02C，2026-10-05）

- WB-02B 的 `c3bfc2f1` 与交接同步 `73266aef` 已在 `main`；本轮继续只改原型目录、状态测试和共享记录，没有改生产 `web/src`、路由或宿主代码。
- `database-catalog`、`connections`、`notebook`、`history`、`metrics` 五个全局页面已补 capabilities、`normal/empty/error/permission/readonly/longContent` 六态、screen-specs 专用字段，并由 `task-details.js` 注入所有对应任务页。连接测试显式区分 URL/健康、认证、数据库/对象权限、capabilities 和测试时间；Notebook 全规划且不自动执行；History 恢复不重放；Metrics 示例不等于采样、质量或成本证据。
- 独立复核最初发现字段映射缺口，已补齐并固化测试：数据库目录名称/权限/资源/Segment，连接名称/URL/默认数据库/认证来源/宿主/测试时间，Notebook 单元类型/输入/执行状态/结果快照/保存版本，History 时间/对象/动作/状态/耗时/返回影响数量，Metrics 写入速率/query P95/WAL fsync P95/内存/采样时间/指标能力。
- 已通过：PowerShell 7.6.6；两个 `node --check`；`node --test web/tests/m47-global-page-state-contract.test.mjs` 3/3；WB-02B 测试 2/2；VM sections=7/models=9/区域页=30/总页=39、五页六态和 taskDetails identity；`git diff --check`；独立最终复核 PASS；最终 `dotnet restore SonnetDB.slnx` 与 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/` 均通过。
- 设计包仍是 `REVIEW_DRAFT`，生产迁移继续冻结；WB-02C 已提交为 `2ebc6978`（`feat(m47): add global page state contracts`），提交后工作树保持 clean。本切片不代表真实 Server、权限、三宿主、安装、发布或 AOT 验收；剩余 24 页状态合同继续按有界批次推进。不 push、不发布、不部署。

## 当前 Workbench 检查点（WB-02D，2026-10-05）

- 在 `main`、HEAD `f6393108` 的干净基线继续实施 WB-02D；本轮仅改 `prototype/catalog.js`、`task-details.js`、`screen-specs.md`、新增 `web/tests/m47-observe-flow-state-contract.test.mjs`，并更新 CHANGELOG 与本交接/队列/验证记录，未修改生产 `web/src`、路由或宿主代码。
- `events`、`slow-queries`、`alerts`、`runtime`、`modbus` 已补 capabilities、六态 stateMatrix、专用字段和任务注入。边界保持：视图暂停不暂停服务器；慢查询恢复不自动重跑，Explain 手动执行；告警与诊断规划能力不发通知、不伪造健康或终态；Modbus Runtime/Pending/Audit 与审批/拒绝复用现有语义。
- 已通过：`node --test web/tests/m47-observe-flow-state-contract.test.mjs` 3/3；`node --check` 两个 JS；`git diff --check`；独立只读复核 PASS。完整仓库 `dotnet restore` 与 `dotnet format` 将在最终待提交树上运行，未通过不得提交。
- 当前状态：WB-02D 已提交为 `07506a99`（`feat(m47): add observe and flow state contracts`）；提交包含上述 8 个专属文件，提交前 `dotnet restore SonnetDB.slnx` 与 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/` 均通过。设计包仍为 `REVIEW_DRAFT`，不代表真实 Server、权限、现场写入、三宿主、安装、发布或 AOT 验收。后续继续选择剩余状态合同的有界切片；不得重复 WB-02D。保留 `origin/parity-results`，不 push、不发布、不部署。

## 当前 Workbench 检查点（WB-02E，2026-10-05）

- WB-02D 已闭环后，主会话冻结 WB-02E 的文件归属：子智能体 `/root/wb02e_global_flow_contracts` 仅改 `prototype/catalog.js`、`task-details.js`、`screen-specs.md`；主会话负责新增窄测试、CHANGELOG、validation-report、work-queue、HANDOFF、验证和提交。没有修改生产 `web/src`、路由或宿主代码。
- 目标是剩余五个工作台/数据流页面 `summary`、`recent`、`imports`、`transfers`、`jobs` 的 capabilities、六态 stateMatrix、专用字段和任务注入；实现仍为 REVIEW_DRAFT 静态原型，保留现有权限、取消、分页/预算、恢复不重放和服务器终态边界。
- 实施者已回报完成：五页均补 capabilities、六态、normal.fields 与任务注入，`node --check` 两个 JS 和 `git diff --check` 通过；主会话新增合同测试 3/3，并与 WB-02B/C/D 联合 11/11。独立只读复核 PASS；复核发现的 jobs 位点字段断言已修正并重新通过。WB-02E 已提交为 `5cbd759a`（`feat(m47): add workbench flow state contracts`），提交前 `dotnet restore SonnetDB.slnx` 与 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/` 均通过。设计包仍为 REVIEW_DRAFT，不代表真实 Server、权限、任务恢复、三宿主或生产迁移验收；不得重复派 WB-02E。保留 `origin/parity-results`，不 push、不发布、不部署。

## 当前 Workbench 检查点（WB-02F，2026-10-05）

- WB-02E 已闭环后，主会话冻结 WB-02F 文件归属：实现者仅改 `prototype/catalog.js`、`task-details.js`、`screen-specs.md`；主会话负责新增窄测试、CHANGELOG、validation-report、work-queue、HANDOFF、验证和提交。生产 `web/src`、路由与宿主代码继续冻结。
- 目标是 AI 与 MCP 四个页面 `ai-connect`、`copilot-settings`、`rag`、`tool-permissions` 的 capabilities、六态 stateMatrix、专用字段和任务注入。需保留 typed HTTP/stdio bridge 尚未实现边界、模型质量/成本独立证据、RAG 持久任务 profile/generation/revision 合同、默认只读工具与显式数据外发策略。
- 实施者已回报完成：四页均补 capabilities、六态、normal.fields 与任务注入，`node --check` 两个 JS 和 `git diff --check` 通过；主会话新增合同测试 3/3，并与 WB-02B/C/D/E 联合 14/14。独立只读复核 PASS，确认 typed HTTP/stdio bridge、Provider 质量/成本、RAG profile/generation/revision、权限交集、默认只读和数据外发边界。WB-02F 已提交为 `4f9720e6`（`feat(m47): add AI and MCP state contracts`），提交前 `dotnet restore SonnetDB.slnx` 与 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/` 均通过。设计包仍为 REVIEW_DRAFT，不代表真实 MCP/tools/list、Provider、模型质量/成本、三宿主或生产迁移验收；不得重复派 WB-02F。保留 `origin/parity-results`，不 push、不发布、不部署。

## 当前 Workbench 检查点（WB-02G，2026-10-05）

- WB-02F 已闭环后，主会话冻结 WB-02G 文件归属：实现者仅改 `prototype/catalog.js`、`task-details.js`、`screen-specs.md`；主会话负责新增窄测试、CHANGELOG、validation-report、work-queue、HANDOFF、验证和提交。生产 `web/src`、路由与宿主代码继续冻结。
- 目标是治理五个页面 `users`、`grants`、`tokens`、`approvals`、`backup` 的 capabilities、六态 stateMatrix、专用字段和任务注入。需保留控制平面权限、MQ Topic 按数据库 grant 且实例共享 Store 另核验、Token 明文只展示一次、审批错误不自动重试写操作、单库备份不覆盖实例共享 `.system/mq` 的事实。
- 实施者已回报完成：五页均补 capabilities、六态、normal.fields 与任务注入，`node --check` 两个 JS 和 `git diff --check` 通过；主会话新增合同测试 3/3，并与 WB-02B–F 联合 17/17。独立只读复核 PASS，逐页确认五页六态、normal.fields、任务页级对象复用及治理边界。WB-02G 已提交为 `c6781b4d`（`feat(m47): add governance state contracts`），提交前 `dotnet restore SonnetDB.slnx` 与 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/` 均通过。设计包仍为 REVIEW_DRAFT，不代表真实权限、Token、审批执行、灾备恢复、三宿主或生产迁移验收；不得重复派 WB-02G。保留 `origin/parity-results`，不 push、不发布、不部署。

## 当前 Workbench 检查点（WB-02H，2026-10-05）

- WB-02G 已闭环后，主会话冻结 WB-02H 文件归属：实现者仅改 `prototype/catalog.js`、`task-details.js`、`screen-specs.md`；主会话负责新增窄测试、CHANGELOG、validation-report、work-queue、HANDOFF、验证和提交。生产 `web/src`、路由与宿主代码继续冻结。
- 目标是设置与发布五个页面 `preferences`、`server-settings`、`studio-host`、`capability-matrix`、`about` 的 capabilities、六态 stateMatrix、专用字段和任务注入。需保留宿主范围/服务器配置只读、Studio 安装与 Extension Host 证据独立、manifest/签名未就绪不显示 PASS、版本来自真实发行物及诊断敏感字段预览边界。
- 实施已完成：五页均补 capabilities、六态、normal.fields 与任务注入；`node --test web/tests/m47-settings-state-contract.test.mjs` 3/3，WB-02B–H 联合回归 20/20，两个 JS `node --check`、`git diff --check` 通过，独立只读复核 PASS。偏好、实例配置、Studio 宿主、发布矩阵和关于页的宿主/预算/敏感字段/真实发行物边界已固化；仍为 REVIEW_DRAFT 静态原型，不代表真实 Server、三宿主、安装、发布或生产迁移验收。
- 下一检查点：在最终待提交树执行完整 `dotnet restore SonnetDB.slnx` 与 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`，通过后仅暂存本切片 8 个文件并提交；随后回写实际提交哈希并重新核对 clean tree、`origin/parity-results` 和 WB-05 依赖。不得重复 WB-02H 或覆盖其它会话文件；不 push、不发布、不部署。
