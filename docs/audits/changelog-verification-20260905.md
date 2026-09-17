# CHANGELOG 与产品声明核查

核查日期：2026-09-05。源码基线：`3b5ff768`。本文件区分历史交付、当前实现、验收证据和未来工作；不把修订文案当成已经补齐功能。

## 范围与方法

已检查 CHANGELOG 的全部版本节结构、中英文 README、14 个本地 Git 标签及重点能力对应源码。基线机械清单提取到 712 个顶层条目，其中早期 Planned 版本含 26 项；机械提取不等于逐项验证。历史 Copilot 的九个粘连条目原来不符合正常 Markdown 列表结构，机械清单也会漏计它们，本次已拆开。

本轮深入核对下表列出的高影响声明，包括 SQL 帧结果、备份目录、fsync、Copilot provider / eval、文档向量重建与编码基准。其余数百项历史测试数量、性能数字、旧版本制品内容和所有平台行为尚未逐项重放，不能据此宣称“全部 CHANGELOG 已通过核验”，也不能给出已验证能力百分比。

没有运行新的数据库基准、真实模型请求、七天长稳、外部 Parity 栈或远端发布查询。本文的代码结论来自源码、本地 Git 历史及最新 GitHub Actions 失败日志；引用的既有测试数字和报告状态不冒充本轮新结果。

## 已确认的声明偏差

| 编号 | 原声明 / 问题 | 源码与证据 | 结论和本次处理 |
| --- | --- | --- | --- |
| C01 | README 称 SQL 大结果无需全量物化、内存近乎恒定 | [FrameEndpointHandler.cs](../../src/SonnetDB/Endpoints/Handlers/FrameEndpointHandler.cs) 约 468 行先调用 `SqlExecutor.ExecuteStatement`，再按 `select.Rows.Count` 编码；[SelectExecutionResult.cs](../../src/SonnetDB.Core/Sql/Execution/SelectExecutionResult.cs) 的 `Rows` 是 `IReadOnlyList<IReadOnlyList<object?>>` | 该端点先取得完整结果，分块约束的是响应编码缓冲。已纠正文案；执行器到客户端的有界 cursor 仍需实现和压力验证。内部 scan / Top-N 的惰性优化不能自动证明最终结果也流式。 |
| C02 | README 称九模型共享一套备份恢复，容易理解为单库备份全覆盖 | [SonnetDbServiceRegistration.cs](../../src/SonnetDB/Hosting/SonnetDbServiceRegistration.cs) 约 88-96 行把 Server MQ 放在 `DataRoot/.system/mq`；[BackupService.cs](../../src/SonnetDB.Core/Backup/BackupService.cs) 约 379 行从 `tsdb.RootDirectory` 复制文件；[Tsdb.cs](../../src/SonnetDB.Core/Engine/Tsdb.cs) 的 `CreateConsistentBackup` 没有 MQ checkpoint | 单库备份不包含实例级 Server MQ，也没有九模型共同恢复点。已明确目录边界；实例备份、MQ checkpoint、恢复顺序及跨模型一致性是实际未完成工作。 |
| C03 | README 称 flush 后的 segment 在任何崩溃下都不丢 | [SegmentWriterOptions.cs](../../src/SonnetDB.Core/Storage/Segments/SegmentWriterOptions.cs) 允许配置 `FsyncOnCommit`，默认 true；[SegmentWriter.cs](../../src/SonnetDB.Core/Storage/Segments/SegmentWriter.cs) 约 278 行条件执行 `fs.Flush(true)`，随后 rename | 任意故障零丢失过度承诺。现表述为默认 fsync、受配置和系统存储保证约束；现有进程崩溃测试不覆盖任意断电与介质损坏。Delete 同步声明收窄到已核对的时序路径。 |
| C04 | 历史 M14 报告 100% accuracy / citation、p95 < 20 ms，并把里程碑总体标为完成 | [CopilotEvalSuiteTests.cs](../../tests/SonnetDB.Tests/Copilot/CopilotEvalSuiteTests.cs) 约 78-85 行注入 `ScriptedChatProvider` 和 `KeywordEmbeddingProvider` | 是脚本化编排回归，不能代表实际 LLM 质量、语义 embedding 或在线时延。保留历史数字和变更记录，并追加明确的核查补注。 |
| C05 | CHANGELOG `### Planned` 中承诺 Microsoft Agent Framework 和独立 Copilot 项目 | [SonnetDB.csproj](../../src/SonnetDB/SonnetDB.csproj) 引用 `Microsoft.Extensions.AI`；实际 [CopilotAgent.cs](../../src/SonnetDB/Copilot/CopilotAgent.cs) 位于 Server 项目；中央包配置没有 `Microsoft.Agents.*` 引用 | 自研编排器确实存在，不能称作 Microsoft Agent Framework 集成。旧计划移入归档；当前路线可以明确保留自研边界，或单列真实框架接线任务。 |
| C06 | CHANGELOG 混入 0.1.0 / 0.2.0 / 0.3.0 Planned 版本，含单一 `.tsl` 文件目标 | [TsdbPaths.cs](../../src/SonnetDB.Core/Engine/TsdbPaths.cs) 明确 `catalog`、`wal`、`segments`、`tables`、`documents`、`graphs` 等目录布局 | 基础功能很多已实现；单文件设想被数据库目录边界替代。26 项原文归档，不将它们继续冒充发布清单，也不把已放弃单文件设计重新强塞回待办。 |
| C07 | CHANGELOG 缺 3.0.1，部分修复误归 3.1.0 | 本地 `v3.0.1` 指向 `0f86e7ea`；`git log v3.0.0..v3.0.1` 有五次提交；标签版 CHANGELOG 的 Unreleased 已含 SQL 分页修复与 MQTT 诊断条目 | 补录标签可核对的版本节和四类变更，将两项误归内容移回该节。发布日期未做远端核验，明确写“发布日期未核验”。 |
| C08 | README 的每日 Parity 描述可能被当成持续通过证明 | [parity.yml](../../.github/workflows/parity.yml) 配置 daily schedule / light / full；本轮取得 2026-08-30 至 09-05 最近七次 Actions 记录，结论全部 `failure`。基线 HEAD 的 [run 33950712561](https://github.com/IoTSharp/SonnetDB/actions/runs/33950712561) 两个 profile 构建通过但 SonnetDB 探活失败，Parity 用例被跳过 | 最新窗口没有一次成功运行，不能沿用 08-29 的 4/7。下载的 light artifact 证明 Server 在 MCP 输出 Schema 推导时因缺少 `McpDatabaseListResult` 的源生成元数据而启动崩溃；已修复工具注册上下文，仍须真实 CI 重跑及重新累计七天证据。 |
| C09 | 历史 Copilot 多个 `Added` 条目粘连成一行 | 基线 CHANGELOG 约 642 行包含九条 `- **...**`，标题本身为 `### Added- ...` | 已恢复正常标题和独立列表项，避免人工阅读和工具索引遗漏。 |

## 已实现但不能扩展解释的能力

| 主题 | 当前证据 | 应采用的解释 |
| --- | --- | --- |
| ONNX provider | [LocalOnnxEmbeddingProvider.cs](../../src/SonnetDB/Copilot/LocalOnnxEmbeddingProvider.cs) 创建 `InferenceSession`、应用线程选项，并通过 `session.Run` 批量推理；[model profile 证据合同](../benchmarks/m27-provider-model-profile.md) 仍明确真实模型门禁 `NOT_READY` | 不能继续说整个本地 ONNX 路径是空壳；也不能说 bge-small-zh 的真实质量和生产性能已被证明。fallback、tiny fixture 与目标模型验收分别报告。 |
| OpenAI-compatible chat | [OpenAICompatibleChatProvider.cs](../../src/SonnetDB/Copilot/OpenAICompatibleChatProvider.cs) 真实向 `chat/completions` 发 HTTP 请求；当前 M27 已记录本地 provider 与云端两条路径 | 历史路径曾脱节不代表当前完全未接线。每个目标 provider 的实际鉴权、模型响应、成本和故障恢复仍需真实运行证据。 |
| 文档向量持久化 | [DocumentVectorIndexStore.cs](../../src/SonnetDB.Core/Documents/Vector/DocumentVectorIndexStore.cs) 构造时调用 `BuildGraphFromKeyspace` | 原 CHANGELOG 已明确持久化的是向量和声明，图拓扑在重开时重建。这是已说明的边界，不应判为“根本没有持久向量能力”；冷启动时间和全量重建内存仍是容量风险。 |
| 编码性能倍数 | [ColumnarIngestBenchmark.cs](../../tests/SonnetDB.Benchmarks/Benchmarks/ColumnarIngestBenchmark.cs) 与 [VectorSearchEncodingBenchmark.cs](../../tests/SonnetDB.Benchmarks/Benchmarks/VectorSearchEncodingBenchmark.cs) 比较编解码过程，历史日志也注明 codec / bytes-on-wire 范围 | 326 倍、1100 倍等历史数值不是数据库总吞吐或 KNN 算法提升倍数。本轮核对了测量对象，没有复跑或重新认证这些数值。 |
| 核心依赖 | [SonnetDB.Core.csproj](../../src/SonnetDB.Core/SonnetDB.Core.csproj) 直接包引用为 `System.IO.Hashing` / `System.Numerics.Tensors` | “无第三方运行时依赖”不等于没有任何 PackageReference。BCL 扩展包不能误判成第三方数据库或 ORM 依赖。 |
| Copilot Chat Tab / Dock、缓存包、目录重命名 | 历史 CHANGELOG 同时记录初始新增与后续删除/替换；`v3.0.1` 的缓存拆分提交可直接核对 | 已实现后被替代的 UI 或包路径不是虚构交付。审查时应关联后续变更，不能只以当前文件不存在定罪。 |
| Graph Beta | 当前 README 明确列为第九模型，同时标明 GQL/Neo4j 兼容、固定硬件、外部对拍与长稳门禁未完成 | 第九模型有代码与公共 API，不等于九模型成熟度相同，也不等于已完成生产发布验收；详细闭环差距应以九模型专项核查与 M40 为准。 |

## 版本完整性

原 CHANGELOG 只有 2.5.0、3.0.0、3.1.0 三个带日期的版本节，以及 Unreleased 和三个 Planned 草案。712 条机械记录中，2.5.0 下有 384 条、3.0.0 有 80 条、3.1.0 有 175 条、Unreleased 有 47 条，其余 26 条属于旧 Planned。重复的 Added / Fixed / Changed 小节体现了长期聚合追加，不能只按最近的标题判断实际首次发布日期。

本地共 14 个标签：`v0.0.1`、`v0.1.0`、`v0.2.0`、`v0.2.1`、`v1.1.0`、`v1.2.0`、`v2.0.0`、`v2.1.0`、`v2.1.1`、`v2.2.0`、`v2.5.0`、`v3.0.0`、`v3.0.1`、`v3.1.0`。未发现 `v0.3.0` 本地标签；这只描述当前本地证据，不证明远端从未发布。

3.0.1 已按五次提交补录：`98daf162`（MQTT 源码引用）、`4bffcc0a`（SQL 分页参数）、`d87edd83`（子模块 checkout）、`0375b1c5`（组件资源初始化）、`0f86e7ea`（缓存包拆分）。最早十个标签仍没有各自可审计的交付节，旧条目没有被删除或随意重分版本。下一步应逐标签 diff 并对照实际制品、API 和迁移边界补档，不能凭今天的代码替早期版本补写完成时间。

## 当前 Parity 启动阻断

2026-09-05 补充核查：`run 33950712561` 的 light `compose-ps.txt` 显示 SonnetDB 容器 `Exited (139)`，PostgreSQL / Redis / MinIO healthy，NATS 已启动。`compose-logs.txt` 第 2 行为 `NotSupportedException: JsonTypeInfo metadata for type 'SonnetDB.Mcp.McpDatabaseListResult' was not provided`，调用链为 `AIFunctionMcpServerTool.DeriveOptions -> WithTools -> MapMcp -> Program.BuildApp`。MCP 2.2 的输出 Schema 推导使用了 SDK 自身上下文，未包含业务 DTO；Server 显式关闭反射，因此在监听端口前退出。等待更久或放宽健康检查都不能修复它。

修复在 [SonnetDbServiceRegistration.cs](../../src/SonnetDB/Hosting/SonnetDbServiceRegistration.cs) 的 `WithTools` 使用[统一 MCP JSON 配置](../../src/SonnetDB/Mcp/SonnetDbMcpJson.cs)，只合并 Server 与 SDK 协议的源生成 context，继续保留 typed output schema 和只读权限。首轮仅传 Server context 的测试又暴露 `CallToolResult` metadata 缺失，因此不能丢弃 SDK 协议上下文。[McpContractTests.cs](../../tests/SonnetDB.Tests/McpContractTests.cs) 增加九工具注册、协议 round-trip 和未注册 DTO 拒绝回归，测试宿主即使启用反射也不会让该配置走反射回退。本节记录实现与失败原因，最终执行结果见[综合报告](2026-09-05_project-SonnetDB-report.md)；CI 尚未重新运行，门禁仍未通过。

## 应进入后续路线的工作

1. 完成 SQL pull cursor / reader 到 REST、Frame 和远程 ADO 的贯通，明确阻塞算子边界、取消、错误终态、背压和资源回收；用大结果、慢客户端与并发查询证明内存上界。
2. 设计实例级备份或显式受支持的分项恢复流程，覆盖 Server MQ 和 `.system` 必需元数据；定义 checkpoint、manifest、授权、恢复先后顺序与失败回滚，再验证跨模型一致性。修改文案仅关闭误导，不关闭这个能力缺口。
3. 把真实 ONNX 模型、provider、成本/时延 eval 与 scripted/tiny fixture 分开验收；保持 M27 和双网客户端未完成边界，不以本地回归通过代替真实服务质量。
4. 逐版本建立“声明 -> 首次提交/标签 -> 公共入口 -> 测试/运行报告 -> 后续替代”的交付索引，覆盖尚未逐项验证的历史清单；旧发布日只能由可核验记录补充。
5. 汇总固定硬件九模型容量、冷启动、读写放大、P50/P95/P99、并发干扰、恢复和七天 Parity 证据。现有 [系统性能报告](../benchmarks/system-performance-20260901.md) 明确这些生产门禁仍缺失，不能把微基准或开发机 smoke 升格为产品保证。

## 本次修订与验证

已修订 README 中英文和 CHANGELOG，并新增本报告及[历史计划归档](historical-plans-from-changelog.md)。原始计划保留可追溯文本，真实历史变更没有被当作未实现功能删除。文档检查范围包括 diff 空白检查、新增相对链接目标、Planned 迁出和粘连条目修复；未因纯文档改动启动额外长时间构建。

本子任务使用 PowerShell 7 和有明确目标的源码/Git 查询，补充诊断时通过本地代理只读下载了一个 GitHub Actions artifact，保存在 `artifacts/project-audit-20260905/nightly-artifact-light` 作为证据。下载进程由任务 `Invoke-Bounded.ps1` 记录身份并有界回收；没有启动后台服务、安装软件或创建临时测试目录。代码修复的构建、真实启动与测试结果由主任务单独记录。
