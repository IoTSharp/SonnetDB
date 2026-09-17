# 产品工作流核查：2026-09-05

## 结论与证据级别

九种模型均能在 Web 的代码中追踪到真实 HTTP/SQL 入口；这支持“具备九类模型实现”，不支持“九种模型已经在全部客户端达到相同成熟度、生产容量或完整恢复验收”。Web 与 Studio 共享管理界面，Studio 另外提供宿主桥接；VS Code 是独立客户端，其 Explorer 尚无 Graph 节点。

本次为限定范围的源码/合同审计和工作流验证。初审后主任务安装仓库锁定的 Web 依赖，完成类型构建、真实 Chrome 的 mock 管理合同回归，并运行独立 Server 启动和事务冒烟；最新测试汇总见[综合报告](2026-09-05_project-SonnetDB-report.md)。未进行 Studio/VS Code 实际宿主安装、九模型完整恢复、固定硬件性能或长时间稳定性验收。不能将下表的“代码存在”改写为发布门禁 PASS。

`web/e2e/management-workbenches.spec.ts` 的 `beforeEach` 调用 `mockManagementContracts`，Studio 相关用例另外调用 `mockStudioBridge`。它们证明页面与模拟响应合同、布局和部分交互，不能证明 Server 数据落盘、WebView2 宿主、真实授权或安装升级。`extensions/sonnetdb-vscode/src/test/README.md` 同样明确 HTTP consumer smoke 使用临时 loopback 模拟服务。CHANGELOG 中已经区分部分 mock/真实证据，后续汇总必须保留此边界。

## 九模型入口清单

| 模型 | Web/Studio 实现线索 | VS Code 实现线索 | 尚缺的闭环证据或体验 |
| --- | --- | --- | --- |
| 时序 | `MeasurementWorkbench.vue`、SQL、批量导入、图表/轨迹结果 | measurement Explorer、SQL、bulk import | 同一真实数据的导入/查询/retention/重启与备份恢复 journey；大结果前端内存 |
| 关系表 | `RelationalTableWorkbench.vue`、schema/index/import/export、SQL EXPLAIN | table/column Explorer、SQL/EXPLAIN | HTTP 事务批次接线，失败回滚与取消结果确认；真实执行计划与 UI 一致 |
| KV | `KvKeyspaceWorkbench.vue`、cursor scan、TTL、批量 set/get/delete、导入导出 | keyspace scan/preview | 分页并发修改语义、跨库请求隔离、批量部分失败、TTL 重启恢复 |
| JSON 文档 | `DocumentCollectionWorkbench.vue`、filter/projection/sort/cursor、validator、bulk import；`DocumentAdvancedWorkbench.vue` | collection find、document result panel | 真实 change feed 续传/断线、导入中断后恢复、深分页和错误逐条归因 |
| 全文检索 | `FullTextSearchWorkbench.vue`、analyze/search/index API | fulltext search/analyze 命令 | 多语言真实语料质量、索引生命周期/重建中查询、分页与排序稳定性 |
| 向量检索 | `VectorSearchWorkbench.vue`、index/search/embed-preview API | vector search 命令 | 真实 embedding 模型 readiness、质量与过滤后召回、索引回退可观察性 |
| 对象存储 | `ObjectBucketWorkbench.vue`、prefix/continuation、range、versions、multipart、lifecycle | bucket/object preview | multipart 中断恢复、实际大文件内存、版本删除与权限失败、下载校验 |
| 消息队列 | `SonnetMqWorkbench.vue`、offset 浏览、publish、consumer/retention/monitor | topic/message preview、runtime monitor | 真正消费/ack/retry/DLQ/崩溃重投 journey，监控采样不能代替投递保障 |
| 原生属性图 | `GraphWorkbench.vue`、canvas、版本条件编辑、导入导出、维护审批/审计，`api/graphs.ts` | 通用 SQL 可发送 Graph SQL；Explorer 无 Graph 类型/section/命令 | Graph Beta 的容量/恢复/168h 证据；三端 Graph 浏览与错误 parity；过期审批和异步跨图覆盖 |

管理端文件根目录为 `web/src/components/`；VS Code 入口集中在 `extensions/sonnetdb-vscode/src/tree/sonnetdbTreeDataProvider.ts`、`core/sonnetdbClient.ts` 和 `package.json`。

## 本次关闭的问题

### P1：多语句执行可在切换标签后改变目标库

原 `useSqlExecution.executeStatements` 每次循环重新读取 `targetDb.value` 和 `auth.api`；第 1 条 HTTP 尚未完成时切换 tab/数据库/连接，第 2 条会使用新目标。history 也读取切换后的连接/数据库，导致错误归属。真实 UI 复现步骤：在 A 库批准两条写语句，延迟第 1 条响应，切到 B 库，放行响应，观察第 2 条 URL。

现已固定执行库、API 对象和连接身份；同步监听上下文变化并中止请求，每次发送前再次核对上下文，停止余下语句；结果始终写回原 tab，history 保留原连接。组件 scope 释放取消请求，同 tab 重复运行不再并发发送。显式 `USE` 更新执行上下文，仍允许用户主动的批内切库。取消提示明确说明已发送的语句可能已完成，不能自动重试或声称回滚。

### P1：损坏或截断 NDJSON 被当作成功

原 `api/sql.ts` 吞掉 JSON 解析错误，允许缺少 `end` 的结果，空响应也没有 error。Console 因此可能显示成功并继续后续写语句。现已拒绝损坏 JSON、无列定义或宽度不匹配的行、未知记录、空响应、未结束的结果和相邻未闭合结果；校验 `end` 的实际行数、受影响行数和耗时；单条入口传播尾部错误并拒绝多个结果，已经完成的前序 batch 结果仍被保留。服务端返回的正常 `end` 和稳定错误合同继续支持。

### P1：事务脚本被拆成多个独立 HTTP 请求

`SqlEndpointHandler.HandleAsync` 将单次 SQL 包装成 `[request]`，`HandleBatchAsync` 将 `request.Statements` 传入同一 `ExecuteAsync`；后者局部 `SqlTransactionContext? transaction` 只在一次请求内保留。原 Console 将 `BEGIN; INSERT; COMMIT` 逐条 POST，无法形成用户预期的同一事务。

现已将受支持的单数据库事务脚本整体发送至现有 `/sql/batch`，按顺序映射结果，保留服务端错误并停止后续结果映射；信号贯穿同一 HTTP 请求。含 `USE`、控制面、`SAVEPOINT` 的混合事务在发送前拒绝，避免自行发明跨库或 savepoint 语义。审批计划额外绑定生成时的连接、base URL 和凭据，切换后不能确认旧计划。本次运行的是客户端真实模块与模拟 HTTP 合同，实际落盘/回滚结果仍需真实 Server 验收。

## 缺口与修复状态

| 编号 | 严重度 | 证据与影响 | 验收 |
| --- | --- | --- | --- |
| UI-001 | P1，本轮合同已验证 | Console 已使用单请求事务；真实 HTTP Server 新增 commit/rollback/中途错误三项测试通过，并在新请求和重启后核对两行或零行，后续 COMMIT/标记写未误执行。 | 客户端模块与真实 Server 合同分别通过；完整真实浏览器到 Server 旅程继续纳入 #310，保留同库轻事务和取消边界。 |
| UI-002 | P1，本轮已修复 | 原 Graph 审批可在切图后作用于新目标；现审批绑定不可变库/图/连接/凭据和参数，变更后同步失效，旧写 history 保留原目标。 | 真实 Vue setup 的延迟、切图、参数与凭据变化、卸载回归通过；浏览器 mock 维护审批通过，完整真机工作流另验。 |
| UI-003 | P2，部分修复 | Graph 已按请求通道序号隔离 `refreshAll/loadVisualization` 等旧响应；KV/Object/Document 类似跨目标模式仍需逐项验证。 | Graph 乱序/加载归属回归通过；其他工作台同样要证明最新目标独占 UI/错误/history。 |
| UI-004 | P2 | Web SQL 使用 Axios `responseType: text` 完整接收再 `split/JSON.parse`，结果转换为对象；`sqlConsole` 深度 watch 将含全部 results 的 tabs 存入 localStorage。表格分页不限制网络或解析内存。 | 10k/100k/1m 行测首屏时间、峰值 JS heap、主线程阻塞、导出内存；提供有界预览/流式解析/截断标记，结果不随每次编辑完整持久化。 |
| UI-005 | P2 | VS Code query 使用 `withProgress` 但无 cancellable token；client SQL 与 Copilot fetch 没有 AbortSignal/明确请求期限。 | 用户取消及扩展停用终止请求；服务端资源回收、错误显示、已提交写入不自动重试。 |
| UI-006 | P2 | VS Code schema section 列表未包含 Graph；Web 三端“同等九模型管理能力”无法成立。 | Graph 发现、schema、受限浏览、SQL 打开、权限/不存在/预算超限错误，与 Web/SDK 同一真实 fixture。 |
| UI-007 | P2 | 管理工作台 mock、Studio bridge mock、HTTP consumer smoke 无法覆盖真实安装/服务/权限/持久化。 | 独立真实 Server golden journey；Windows Studio 干净安装、升级、卸载保留数据、端口冲突、宿主异常退出；VS Code host 实际交互。 |

## Copilot 与宣传边界

`web/src/copilot/browserDirect.ts` 已包含独立 readiness、token provider、continuation 和工具预算检查，`CopilotReadiness.cs` 有 provider 校验；不能再笼统声称所有 provider 接线均不存在。但 token provider 接口与模拟公网合同并不等于可信 OAuth/PKCE、部署后的 CORS/CSP、跨网络或重启续流验收。CHANGELOG 当前明确“StudioNative、真实双网验收仍未实现”，这是应该保留的边界。

实际 ONNX、固定语料质量和 tokenizer/profile 证据由 M27 专页约束；出现 embedding 预览 UI 不代表真实模型质量已过关。`Microsoft.Extensions.AI` 抽象和自研 CopilotAgent 的存在，不能单独证明真实 Microsoft Agent Framework 运行时集成。

README 的九类列表可以作为模型清单；不得从“共享备份机制”“Graph Beta”推导出九模型已经全部完成同样的恢复、容量和长时间发布验证。CHANGELOG 描述已经实现的代码切片；ROADMAP 要单列尚缺的用户旅程和证据，避免把历史已交付 UI 重建一遍。

## 可借鉴的开源工作流

以下是待评估的具体设计方向；本次没有联网抓取这些项目或做竞品性能测试，也不声称 SonnetDB 已经移植其实现。

| 开源项目/参考 | 值得采用的工作流 | SonnetDB 对应验收 |
| --- | --- | --- |
| pgAdmin Query Tool / DBeaver SQL editor | 连接/库/事务状态绑定执行上下文，取消与提交状态明确，计划结果可解释 | UI-001、UI-004；一次执行不可因切 tab 重定向 |
| RedisInsight | namespace 浏览、有界 scan、明确 TTL、值类型与批量危险操作预览 | KV cursor/TTL/取消/并发修改真实 journey |
| MongoDB Compass | 查询构建与原生查询互通、执行计划、逐条导入错误和 schema 检视 | Document cursor、validator、bulk partial failure 与真实索引命中 |
| Neo4j Browser | 图/表双视图、查询预算、选中元素详情和结果截断标识 | Graph Beta 预算/截断/错误不能显示为空图成功 |
| MinIO Console | 对象版本、multipart 进度/恢复、按对象错误和下载校验 | 大文件传输不全量驻留内存，失败恢复和版本权限可追溯 |

评价“学习和采纳”的依据应是可复现实验、ADR/设计理由、适用语义与实现许可证记录，不能仅以使用 ECharts/MapLibre 或视觉相似认定已采用竞品关键能力。

## 建议接入路线图的验收顺序

1. 正确性：SQL/Graph 修复已有本地回归；继续实际客户端到 Server 的整体旅程和其他工作台目标隔离，所有写操作按不可变目标、权限、版本绑定。
2. 九模型最短真实旅程：同一真实 Server 数据目录完成建模/写入/查询/更新或消费/导出/备份/重启恢复/权限拒绝。模型专有语义单列，不能强行要求 MQ 与对象完全模仿关系 CRUD。
3. 三端合同：Web/Studio 共用工作台加真实宿主门禁；VS Code 以其实际目标明确最小 Graph 等管理闭环，不要求逐页复制 Web。
4. 性能：以固定数据和机器记录服务端执行与前端传输/解析/渲染分别耗时，记录 cold/warm、P50/P95、错误/取消、峰值内存和结果行/字节数，不以页面加载时间替代 SQL 性能。
5. 发布：将真实旅程、权限拒绝、取消、不确定写入、恢复、容量与长时间证据分开归档；mock PASS、构建 PASS、未运行 NOT_RUN 不得合并成一项“完成”。

## 本次验证与限制

- 环境：PowerShell 7.6.5、`C:\Program Files\nodejs\node.exe` v24.15.0；初审未装依赖，后由主任务按原 package-lock 执行 `npm ci`，未改依赖清单。
- 命令：`node --experimental-vm-modules --test --test-timeout=15000 web/tests/sql-workflow.test.mjs`，外层 `Invoke-Bounded.ps1` 墙钟上限 45 秒。
- 结果：9 项通过，覆盖损坏/空/截断 NDJSON、完整批次保留、尾部错误传播/行数/行宽、审批后的跨 tab 取消/原结果归属、切连接后阻止后续语句及 history 归属、显式 USE、scope 释放和重复运行、旧连接审批失效、单请求事务、混合事务拒绝与批次错误早停。
- 测试加载真实 TypeScript 生产模块，使用 Node 自带 `stripTypeScriptTypes` 和 VM；仅以最小 Vue reactivity 替身与模拟 HTTP 验证执行控制，不是 Vue 渲染或真实服务器事务测试。每项 5 秒、整体 15 秒；测试模块限定 7 个源模块。
- 新增 `graph-workflow.test.mjs`，使用真实 Vue reactivity 与 compiler-sfc 的组件 setup，7 项通过，涵盖目标/参数/凭据/markRaw API 变化、乱序、旧写 history 和卸载。主任务最终 Vue 类型检查及 Vite 生产构建通过，保留真实大 chunk 告警。
- 既有 Chrome 上 12 项管理回归通过，涵盖九模型入口及 Graph 桌面 canvas 像素/手机可用性；另重跑两项 Graph 并保存桌面/手机截图到 `output/playwright/m40-graph-operations/`。这些仍使用 mock API，不能替代真实 Server 或 Studio WebView2。
- 最终运行日志位于 `artifacts/project-audit-20260905/web-sql-workflow-final.stdout.log` 和 `.stderr.log`。没有启动持续运行的 Server 或浏览器，也未创建新源代码 checkout。
