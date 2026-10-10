# M47-P01：Workbench Preview 1 范围与候选冻结

冻结日期：2026-10-10（Asia/Shanghai）。**P01 范围冻结完成；产品发布仍为 NOT_READY。** 本文与 [候选 01 清单](preview-1-candidate-01.json)、[独立分发合同](preview-1-distribution.md)共同作为 P02～P07 的输入。这里的“纳入”是待验收承诺，不是产品已通过、入口已禁用或已生成包。

## 候选、版本与平台

| 项目 | 冻结值 / 边界 |
|---|---|
| 已提交来源候选 01 | `c40fa670e3a170c5fac8cefa181d618631862270`，tree `7dd085919b51875e00f15807702e06fe0c47b670`；这是修复与验收起点，不能直接放行 |
| 选择依据 | 已提交的 main；本轮开始 HEAD 与本地 origin/main 均指向该 SHA，index 为空。父会话已有远端推送回读，本轮不把本地 tracking ref 称为新的远端实核 |
| 候选 01 的保留版本 | `4.5.0-preview.1.1`；尚未注入构建、打标签或产生发行物。4.5.0 表示所属计划系列，不表示 4.5 正式版范围已完成 |
| 版本规则 | `4.5.0-preview.1.N`，N 是单调递增的候选序号；一个版本只对应一个完整 source SHA、构建输入与最终资产哈希集合。任何源码/配置/依赖/打包规则变化，或重新生成不同字节的包，都用新 N 和新的不可变候选清单，不覆盖候选 01 |
| 标签规则 | 预留 `workbench-preview-1.N`，指向该候选 source SHA；禁止移动/复用标签。它不是 `v*`，不触发现有全套标签发布路径；新独立发布实现仍须全门禁与 P08 授权。当前无标签创建 |
| 发布目标 | Windows 11 x64 上的 `win-x64` NativeAOT Server＋同源内嵌 Web；Edge、Chrome 的实测完整版本由 P05 收据固定。其它 Windows 版本、Linux/macOS/ARM、移动浏览器、跨域/子路径托管延期，不凭 Web 可构建宣称兼容 |
| 产品兼容 | Web 与 Server 必须同一候选、同一版本、同一包；REST `/v1` 与下述 MCP 合同不等于任意旧 Server 兼容。4.0.0、任意 4.x/4.5.x 混配和滚动升级均未支持 |

候选 01 已知缺少首版入口控制与独立打包/分发实现，因此预计要产生后继候选。P02 若修复 smoke、P03 若补入口控制、P04/P06 若修改打包，均先提交变更，再追加候选 N+1，记录父候选/差异与受影响验收；P07 必须针对**最终完整 SHA**重新核全部必选门禁，不继承旧 SHA 的 PASS。仅更新报告也不能把一个 SHA 的工作流冒充另一个 SHA 的证据。

版本来源已核：根 [Directory.Build.props](../../../Directory.Build.props) 默认 `VersionPrefix=0.0.0-dev`，AssemblyVersion/FileVersion 默认 `0.0.0.0`；[release.ps1](../../../eng/release.ps1) 对 Server/Studio 传 `Version`，NuGet 另传 `PackageVersion`。Web [package.json](../../../web/package.json) 的 `0.1.0` 是 private 前端包版本，并非 Preview 发行号；[VS Code package.json](../../../extensions/sonnetdb-vscode/package.json) 为 `0.4.1`，本次延期，不复用此号发布 M47。Studio 无独立产品版本覆盖，继承发布参数。P04 须把发行号、完整 SHA、Web 静态资源哈希和实际 Server informational version 写入 manifest 并实际回读；不能只改文件名。旧 4.0.0 Release 与 0.4.1 VSIX 均不是本候选资产。

## 用户旅程与动作白名单

首版对象是用测试数据库评估 Web 工作台的用户。运营者先准备有权限的测试库/九模型小数据集；数据库与用户/授权的高级治理、生产数据迁移不在首版 Web 承诺中。P03/P05 的夹具准备可以用已有 Server API，但 API 登录/token 注入不能代替下面的真实登录 UI。

1. 从公开清单下载便携包，核 SHA256，解压到新目录，以独立数据目录启动；访问 `/admin`，空安装经 `/admin/setup` 完成初始化，再以 `/admin/login` 输入用户名/密码登录。不得随包分发通用可用 token、真实数据或预置登录状态。
2. 在获授权数据库间选择，确认活动库与原始资源名称；只显示允许访问的资源。打开九模型页面进行下表限定的浏览/查询，观察空、错误、无权限、只读和长内容状态。
3. 查询可取消、截断可见；导出只含当前已加载且仍获授权的窗口。切库、切身份、撤权、断连后清除旧载荷和待审批上下文；重连只允许用户显式重读，不自动重放写入。
4. 只有获 Write/Admin 的测试用户可以在关系表页暂存**单行插入**，核对数据库/表/参数，单次确认后提交并回读结果；拒绝、取消、失败、成功和 unknown 必须区分。其它数据写入口延期。
5. 退出、重新登录、关闭并重启实际 Server/Web，重新选库查询已确认的数据。仅承诺正常停止/重开与身份隔离；不承诺自动恢复 SQL 草稿、跨身份历史、崩溃恢复、任意备份恢复或写入回滚。

以下动作 ID 是本次发布范围标识，**不是已实现的 runtime capability 名称**；机器清单记录对应页面与当前证据。未列动作默认延期；所有纳入动作都须在最终候选/实际包取得适用通过证据后才可公开启用。

| 动作 ID | 用户入口 / 纳入动作 | 延期 / 约束 | 历史局部证据 |
|---|---|---|---|
| `session` | `/admin`、setup、login；选择有权限的库、退出、重新登录；About 查看版本 | auto-login、跨源连接、自定义 base path、凭据导入延期；真实 UI 与撤权清理待 P03 | 旧 token/API 登录不能代替 |
| `sql.read` | `/admin/app/sql`；单条 SELECT、只读 SHOW/DESCRIBE、EXPLAIN | 自由 SQL 的写/控制面/多语句执行必须拒绝；限制输出不等于扫描/堆内存有界 | WB26～WB37 的相关读取 |
| `relation.read` | `sql?tool=table`；schema、单页筛选/排序/前后翻页 | 页面上限 200 行；不是稳定快照游标；DDL、索引/外键管理延期 | WB27 |
| `relation.insert.one` | 同上；普通标量列单行参数化插入、预览、一次确认、回读 | 只含一个操作；批量/UPDATE/DELETE/DDL/自由 SQL 写延期；上下文变化使审批失效，unknown 不重放 | WB27 受限写/权限旅程；首版动作限制待 P03 |
| `measurement.read` | `sql?tool=measurement`；schema、有限时间范围点查询 | 最多 500 行；写点、摄取/导入、DDL、维护延期 | WB31 |
| `document.read` | `sql?tool=document`；集合/schema、Find 当前页及已有游标下一页 | 每页不超过 1000；累计窗口仍须预算；count/aggregate/distinct/高级管理及所有写延期 | WB26 |
| `kv.read` | `sql?tool=kv`；keyspace、prefix scan、当前键查看 | 窗口不超过 1000，检查器最多 4096 字节；CAS/TTL/原子数值及写/删延期 | WB29 |
| `fulltext.read` | `sql?tool=fulltext`；索引列表、词法搜索、命中预览 | TopK 不超过 100；导入、索引写/维护、高级查询延期 | WB28 |
| `vector.read` | `sql?tool=vector`；索引元数据、显式数值向量搜索 | TopK 不超过 100；自动 embedding、Profile、写/维护和真实语义质量承诺延期 | WB32/WB33 |
| `object.read` | `sql?tool=bucket`；桶/对象列表当前窗口、metadata、最多 4096 字节 Range 预览 | 全对象下载/上传、Multipart、presign、语义/图像模型、全桶导出延期；不自动遍历所有页 | WB37 |
| `mq.read` | `sql?tool=mq`；Topic 元数据/统计、显式 offset 消息浏览 | 窗口不超过 1000，检查器最多 4096 字节；自动轮询、publish/consume/ack/nack/retention 编辑延期 | WB34 |
| `graph.read.beta` | `sql?tool=graph`；Graph 元数据与有界节点/边只读预览 | Beta 标签常显；可视化最多 1000 元素；写/import/export/维护/任意遍历延期；超限拒绝而非后台全量加载 | WB35/WB36 |
| `result.window` | 以上结果视图；允许读取的当前窗口导出、截断/取消/错误状态 | 不导出凭据、未加载页面、已撤权数据；不是备份；Graph 文件导出和对象字节下载不因本项获准 | 各模型已有局部结果合同 |

表中数字来自候选现有局部常量/控件，**不证明统一传输、扫描或累计内存预算已实现**。P03 必须完成以下首版共同上限并测试拒绝路径：每个活动操作 30 秒、最多一个在途读取；SQL/通用窗口最多 1000 行，模型已有更小上限优先；一个活动结果最多 4 MiB 解码后保留载荷，单次响应正文最多 4 MiB，单次 SQL/插入输入最多 64 KiB（UTF-8），当前窗口导出最多 4 MiB。超限须中止/拒绝并清空不完整结果，不能先全量载入再裁剪，也不能后台无界翻页。4 MiB/64 KiB 是本次验收目标，非现有测量值；若某端点无法满足，P03 必须补控制或以新范围修订延期该动作，不能静默调大阈值。浏览器取消不是 Server 扫描停止或事务回滚证明，Server 资源预算未证路径不放行。

SQL 原拼写/OrdinalIgnoreCase 与双引号精确匹配合同保持；拒绝仅大小写重名。Graph Beta 不升级为正式图引擎承诺。MQ 身份为 `database + Topic`，物理持久化为实例 `.system/mq`；单库备份不覆盖实例 MQ，首版不提供备份恢复入口。

## 可见入口、现状与必须补齐的控制

[router](../../../web/src/router/index.ts) 与 [AppShell](../../../web/src/views/AppShell.vue) 现有七模块导航不会自动消费此清单；[CapabilityRegistry](../../../web/src/management-core/capabilityRegistry.ts) 只描述状态，不授权也不阻止请求。**候选 01 仍能到达范围外页面/动作，未实施 Preview profile；因此禁止把它直接打包为首版。** 下面全是 P03 的实现/验收阻断，不声称 P01 已禁用 UI。

| 控制 ID / 归属 | 发布时用户可见行为 | 当前缺口与验收出口 |
|---|---|---|
| `C01` / P03 | 七模块位置保留：概览仅连接/当前库概要，工作台只允许白名单，设置只允许 About；观测、数据流、AI 与 MCP、治理显示“本预览未开放”且不可进入 | 原 dashboard/monitoring/events/modbus/rag/users/grants/tokens/ai-settings/copilot-test/trajectory 与别名、OAuth callback、auto-login 仍需逐项限制。菜单、直接 URL、query 参数和恢复 tab 都须拒绝；不能只藏菜单 |
| `C02` / P03 | 每个模型只启用上表动作；其它 tab/工具栏给出禁用原因；自由 SQL 只读 | 现有 readOnly/canWrite 与管理员路由不足以表达发布白名单；在调度/发送请求边界增加相同检查，禁止键盘/旧 tab/审批队列绕过。未知 profile/动作/模型默认拒绝 |
| `C03` / P03 | 单行插入只在有效权限与当前上下文下允许一次审批；切库/切用户/撤权后不可提交 | 验证真实服务的授权/拒绝及所有终态；响应丢失/超时显示 unknown，不自动重试，不声称撤销已执行写入 |
| `C04` / P03 | 只读账户/撤权后清空旧结果、导出、草稿与待操作；结果有上述资源上限 | 普通用户的 HTTP role 与数据库 grant 必须分别核验；前端状态不是授权。必须测试深链接、手工请求、过期 token、超预算响应与断连 |
| `C05` / P04、P05 | 包内固定 Preview profile/manifest，Server 同源提供 `/admin`；旧静态文件与缓存不能混入 | 当前还没有 profile 构建注入/版本协商/匹配拒绝收据。P04 实现来源绑定，P05 实际错配拒绝、首开/重开和回退验收 |
| `D01` / P04、P06 | 只分发独立 Web/Server ZIP 与精确白名单文件 | 现有流水线默认包含 Studio/SDK/NuGet/安装器；新资产生成与 fail-closed 上传器未实现，详见分发合同 |

这些限制属于 Web Preview 产品入口控制，不改变已存在 Server API 的整体能力。匹配 Server 仍携带其它 REST/MCP 功能；部署时保持环回监听，使用最小数据库授权。需要把本 Preview 当成网络安全隔离或多租户沙箱的场景延期；不得宣称 UI 限制会移除已有 Server API。P03 不应为缩范围而削弱 Server 原有授权或 AOT 合同。

## 认证、部署与 MCP/能力映射

首版只支持**同一台机器、环回、同源、根路径**部署。Server 包的 HTTP/Frame 地址遵循 [网络默认设置](../../../eng/set-release-network-defaults.ps1)，可选 MQTT/CoAP/UDP/Modbus listener 与外部连接保持关闭；实际端口及配置摘要由 P04/P05 回读。外网暴露、远程 TLS 反代、Docker/服务安装、跨源浏览器连接不在首版承诺内。

认证走现有用户名/密码登录与 Bearer token；现有 Web 将 `sndb.auth` 保存在 localStorage，这不是安全凭据库。bootstrap 超级用户用于初始化，日常读取使用 Read grant，单行插入另用 Write/Admin grant。HTTP role（admin/readwrite/readonly）与数据库 None/Read/Write/Admin 两层授权共同决定访问，普通用户登录获得 readwrite role 不等于拥有库写权限。P03 必须真实验收退出/身份切换/撤权的载荷清理；日志、下载 manifest 不含密码/token/真实查询数据。

| 合同 / 能力 | 已提交来源值 | Preview 承诺 |
|---|---|---|
| MCP HTTP 入口 | `/mcp/{db}`，stateless；由 Server 原认证/数据库上下文管理 | 随 Server 保留既有 API；不作为首版 UI 入驻或第三方宿主已验收能力 |
| MCP typed result | `SonnetDbMcpContract.Version = "1.0"`，1.x extend-only | 与协议协商版本分开记录；兼容不同 minor 不能免除最终候选实测 |
| MCP tools | `list_databases`、`query_sql`、`list_measurements`、`describe_measurement`、`sample_rows`、`explain_sql`、`docs_search`、`skill_search`、`skill_load` | 源码注册清单；均不因列入 manifest 而获准 UI/AI 外发。`query_sql` 为 AST 只读约束；这不是所有 Web SQL 入口都只读的证明 |
| MCP resources | `measurements`、`measurement_schema`、`database_stats` | 源码资源清单；实际 initialize/tools/list/resources 必须按当前凭据实核 |
| browser-direct MCP 客户端 | 协议常量 `2025-11-25`，typed major=1；默认 64 KiB 结果预算、16 页/128 tools 发现上限 | 这是客户端源码要求，不是实测 Server 协商结论；browser-direct、外部模型/AI、stdio onboarding 首版延期 |
| 本次动作清单 | JSON 的 `actions` 与 `entryControls` | 产品支持范围；不能当成运行时返回的 capabilities，也不能替代服务端授权 |

来源：[MCP results](../../../src/SonnetDB/Mcp/SonnetDbMcpResults.cs)、[tools](../../../src/SonnetDB/Mcp/SonnetDbMcpTools.cs)、[resources](../../../src/SonnetDB/Mcp/SonnetDbMcpResources.cs)、[注册](../../../src/SonnetDB/Hosting/SonnetDbServiceRegistration.cs)、[browser-direct](../../../web/src/copilot/browserDirectMcp.ts)、[auth](../../../web/src/stores/auth.ts)、[数据库权限](../../../src/SonnetDB/Auth/DatabasePermission.cs)。机器清单绑定这些文件的候选 Git blob。

## P01 验收与接续

- 已完成：候选完整 SHA/tree/gitlinks、实际版本来源、不可变版本/标签规则、目标平台、兼容/认证/部署合同、动作白名单、逐入口延期控制、分发资产集合和原门禁映射；JSON 与文本交叉校验及来源 blob 校验保存在 `artifacts/m47-p01-freeze-20261010`。
- 未完成且不计 P01 PASS：C01～C05/D01 实现、候选 smoke、真实登录/权限/预算/终态、实物安装/部署/回退、最终版本/哈希及发布。P01 的完成仅表示这些输入和退出标准冻结。
- 下一项 P02：保留历史 run `37731752397` / `5424b66672e37f49a2a5633fdde0b54a9b1f0290` 的 Web smoke failure，核日志根因后在本候选或追加的后继候选验证；本轮未查询新远端结果，不称该历史记录为此刻最新。父会话 restore/format 与四个其它 job success 不替代五项全通过。
- P03 处理 C01～C04；P04 处理 C05 的来源/产物和 D01 的组包；P05 实物验证 C05；P06 实现 D01 分发筛选、说明与回退材料；P07 全部同候选放行；P08 另需用户实际发布授权。
- Studio WB80 本机恢复 PASS 保留，但旧 Release 不计当前安装；VS Code WB83 preflight/helper_deadline FAIL 与 WB98 审查超时不升级为已证产品故障。旧 workbench 自动化继续 PAUSED，不接续 helper 集成循环。

首版若需更改纳入动作、平台或预算，必须追加有理由/影响/验收范围的修订，并同步 JSON、根路线与交接；不能在 P04/P06 打包时偷偷放宽范围。


## 2026-10-10 P03修订R1

候选03起采用[有理由和验收边界的范围修订R1](preview-1-p03-scope-revision.md)：七模型专用读取因服务端预算未证延期，SQL/关系/时序保留受支持的有界子集；原候选01/02不修改，产品仍NOT_READY。真实验证见[P03报告](preview-1-p03-controls.md)。
