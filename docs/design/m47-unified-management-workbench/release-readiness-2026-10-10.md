# Workbench 发布就绪审查（2026-10-10）

审查时间：2026-10-10，Asia/Shanghai。范围：D:\source\SonnetDB 的 Web Admin、Studio、VS Code 和现有发行物。只做审查与交接；不接续 WB98 实施，不执行测试、构建、安装、提交、push、发布或部署。

**结论：Workbench 已有实质交付，适合准备范围受限的 Web 预览候选；当前三宿主正式版、公开 Beta 和分宿主新发行物均未取得可放行证据。最快路线是先收口 Web Admin 预览，不必先完成旧 helper observation 集成。** 这是发行条件结论，不是声称所有功能有故障，也不撤销已经发布的旧版本。

## 候选与已发布版本

- 本地 main / HEAD：`78045e8b407e9caceae2548116390ffb985fc959`；远端 API 与本地 origin/main：`5424b66672e37f49a2a5633fdde0b54a9b1f0290`。本地领先 8 个提交；本次没有 fetch/push。这些后续提交的宿主路径改动以验收脚本、测试和证据为主，不能凭领先数量判断产品成熟度。
- 暂存区为空。WB85～WB98 的三个 VS Code 验收脚本仍是 dirty；它们与 HEAD 的 Git blob 不同。另有 Workbench 文档和其它发布会话的 dirty/untracked 内容，不能整棵工作树作为已冻结发行候选。
- GitHub `releases/latest` 实查仍为正式 `v4.0.0`，发布时间 2026-09-27 02:02:26（Asia/Shanghai），发行资产更新时间集中于 2026-09-28，包含 Studio ZIP/MSI。旧发行证明已有打包渠道，不能证明 10 月 M47 改动已经进入这些包。
- Marketplace 公共只读查询确认 `iotsharp.sonnetdb-vscode` 最新为 `0.4.1`，更新时间 2026-07-14 10:05:46；对应 VSIX SHA256 为 `f40f52f143275a93d3a32d0e70a30b317e58774b363c33a86a7c28d8e11ab8de`。本地 package.json 仍是 `0.4.1`，更新必须使用新的不可变版本。
- 根 VersionPrefix 是 `0.0.0-dev`，发布工作流注入版本；不能把该默认值当作正式发布版本。M47 要求同一发布系列、合同/能力与兼容矩阵，目前尚未核到绑定当前候选的完整三面版本清单。
- 标准路径 `extensions/sonnetdb-vscode/dist`、`artifacts/release/bundles/win-x64`、`artifacts/release/installers/win-x64` 本次均不存在。web/dist/index.html 存在，时间为 2026-10-07。没有核实整个磁盘其它目录，也没有把 dist 存在当作发行来源/哈希/运行证明。

## 宿主结论

| 宿主 | 已完成证据 | 当前公开发布结论 | 最小剩余验收 |
|---|---|---|---|
| Web Admin | 共享外壳、七模块导航、九模型资源身份与页面、结果/草稿/历史/审批已提交；九模型都有后续本机 Web→真实 Kestrel 的受限旅程 | 可作为内部预览和新 Web 预览候选的基础；当前公开预览/Beta 未放行，正式版未完成 | 同一冻结候选的 Web smoke 通过；真实登录及所承诺权限/只读/取消/截断/恢复范围；当前静态资源打包与 Server 兼容清单 |
| Studio | Windows native bridge、Managed Local、Health/Stop/Start、宽窗及默认/窄窗切片；WB80 两次原生启动、A/B 切库、被动恢复 B、真实 B 查询、两次正常关闭通过 | 本机开发预览证据较完整；新公开桌面 Beta/正式版未放行 | 当前源码绑定的包，干净 Windows/WebView2 下安装/启动/升级卸载；所承诺 OS 文件对话框；Managed Local 与 Remote 边界 |
| VS Code | 九模型树与 Web 深链接、Extension Host 基础调用、SQL 元数据兼容与 history 合同；历史实跑取得 current/selection/EXPLAIN 的真实响应子集；远端基础 Host/consumer 检查通过 | 现有 Marketplace 0.4.1 已发布；本次 M47 更新的公开预览/Beta/正式版未放行 | 连接与实际 Query→结果→history→退出的一次完整通过；所承诺分页/Notebook/LSP 范围；新版本 VSIX 与安装/兼容/哈希 |

九模型真实证据索引：Document WB26、Relation WB27、FullText WB28、KV WB29、Measurement WB31、Vector WB32/WB33、MQ WB34、Graph WB35/WB36、Object WB37。它们证明各自明确场景，未证明所有读写、全部权限矩阵、全库扫描/物化/传输/堆预算或所有恢复路径。Graph 继续标为 Beta；MQ 的 database/database+Topic 是逻辑身份，实例 `.system/mq` 的持久化不属于单库备份。

## 实际阻断与不能误判的历史记录

1. **远端最近一次 Workbench smoke 未通过。** Run `37731752397`，2026-10-08，候选 SHA `5424b666...`，总结论 failure。五个 job 中 Server management contracts、Studio desktop host、VS Code HTTP consumer、VS Code Extension Host 为 success；Web Admin and Studio bridge 的 `Run management workbench smoke` 步骤 failure。这是最近远端候选的验收阻断，不是五项全部失败，也不能直接推断产品代码根因。详细 annotations 请求在直连失败后经本机代理重试仍遇 GitHub API rate limit，本次未核到根因。新本地 HEAD 没有对应远端验收证据，不能继承其它 SHA 的 PASS。
2. **候选、版本和发行物尚未闭合。** 当前 dirty 工作树不能直接对应一个可复算的新发布物；已发布 4.0.0 / 0.4.1 也不能代替当前 M47 内容。应先选择明确已提交候选，保持其它会话及 pending 诊断改动隔离，再生成与候选绑定的静态资源/包/manifest/hash。涉及代码提交时仍须完整 restore 与原 CI Format Check。
3. **Studio 安装与原生文件对话框证据不足。** 构建、宿主单元测试和本机启动不等同干净安装/升级卸载，也不等同 OS 文件对话框。不能再列“数据库恢复始终失败”：WB80 result.json 实查 passed/normalExit/cleanupProven/requiredResultWriter 全为 true，SHA256 `7D11234153486A47CE1BB14EE17CDC53353A6AA60B3E04312CDB885FD332A3B2`。它复用旧 Release 产物，不证明当前源码与旧二进制等价，也不证明 Server 优雅关闭或任意崩溃恢复。
4. **VS Code 完整产品旅程仍缺通过证据。** WB83 最新实际 result 保留 FAIL、preflight/helper_deadline、primaryFailure=true；没有建立该次 Server/Code 旅程。历史 WB55/WB57 的真实查询响应可用作局部证据，history/退出等失败或未知不能靠后续 PID absence 升级。普通 Query/history 故障与验证 helper 故障必须分别核实。
5. **正式版范围大于现有切片。** M47-U04/U05 的完整九模型与 Web 治理、U06 的桌面发布合同、U07/U08 的开发者面与 AI/MCP 入驻均没有完整收口。若只发预览，可明确延期未承诺的分页/Notebook/AI/完整矩阵等；不能把未验能力留在正式版承诺中。这里不把 M44～M46 的全部路线图自动加入单独 Web 预览的阻断清单。

WB96 已在独立收据中证明当前 97 项命名测试及语法退出，status 为 `CLOSED_PASS_CURRENT_NAMED_TESTS_AND_SYNTAX_ONLY`；这覆盖 dirty 验收脚本的本地合同，不是一次新真实 Host 或发行物测试。WB98 的 `FAIL_REVIEW_TIME`、micro/restore/format/commit=0 属于集成审阅未完成，不能自行解释为产品功能故障，也无需为了 Web 预览而继续旧诊断循环。

## 建议继续顺序

1. 选择 Web Admin 预览的已提交候选和明确功能范围；先核上述 smoke 的失败日志并在该候选取得通过证据，按证据修正必要缺口。冻结真实登录/只读/审批终态、取消/截断、撤权与恢复的预览承诺。
2. 生成候选绑定的 Web 资源和 Server 兼容/版本/哈希清单，以真实包取得一次启动、登录、查询与所承诺写审批旅程。这一步通过后，Web 可以独立作为公开预览，不必等 Studio/VS Code 全量。
3. Studio 另收口当前包的干净安装、两种连接模式、文件对话框及卸载/退出；VS Code 另收口完整 Query/history/退出与新版本 VSIX。按各宿主结果分别放行。
4. 最后评审三面正式版必选范围与版本兼容矩阵。持续暂停旧 workbench 定时，不恢复滚动任务；实际发布需来自用户的新授权。

## 审查边界

本次读取 20 个具名文件；四个大文档完整字节已接收并记录大小/行数/SHA256，语义审阅限于当前交接、M47 状态、九模型真实旅程和最新宿主/发布记录，**没有声称逐条审读所有历史记录**。曾发生 PowerShell 区间构造错误和部分控制台输出截断；随后对决策所需具名报告/行及 JSON 标量补读，未把截断输出写成完整语义审计。

本次未新增产品测试/构建、未运行 restore/format、未修改产品源码或验收脚本、未 stage/commit/push/发布。HANDOFF 仅追加本次审查交接，未提交，因为本次是审查且未取得最终树的提交门禁。既有 dirty 内容、原失败证据和 parity-results 引用保留。旧 workbench 配置实查为 PAUSED。远端只读 API 的响应证据另存 artifacts/workbench-release-review-20261010-01a12429。
