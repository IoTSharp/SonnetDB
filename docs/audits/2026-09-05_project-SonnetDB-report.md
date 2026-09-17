# SonnetDB 综合核查与九模型闭环计划

日期：2026-09-05。基线：`3b5ff768adf946b09a3be11ed58a83cacb3c1696`，修复位于当前工作区，未提交、发布或部署。

## 结论

SonnetDB 确实具备时序、关系、KV、Document、全文、向量、对象、MQ、原生图九类实际实现，不是九个占位接口。但它们的成熟度、访问合同、持久化边界和发布证据不一致，不能宣称已完整替代九种专用数据库，也不能宣称九模型生产闭环已经全部完成。

发现了实际缺陷和过度概括：当前 scheduled Parity 连续七次失败；MCP 注册缺少源生成元数据导致 Server 启动失败；SQL 自动统计占用首个业务规划线程；Web 事务脚本拆成独立请求、切换目标可影响后续写入、损坏 NDJSON 可被当成成功；Graph 审批和异步结果未完全绑定目标；MQ ACK 极值溢出和返回位点不一致。已对这些明确问题实施本地修复，验证级别见下文。

“SQL 分帧”等于传输分块，不等于执行器和浏览器总内存恒定；“WAL + fsync”不等于任意硬件故障零丢失；database backup 不包含 Server 实例级 MQ；轻事务不等于跨 keyspace 掉电原子性。README 和 CHANGELOG 已校准这些声明。

本轮是有界、证据驱动的横向审计与优先修复，不是全部历史条目的逐项重放、全覆盖安全审计或生产认证。没有取得的新硬件、真实模型质量、Studio 干净安装、外部竞品及七天报告继续保留为未完成。

## 范围与材料

用户授权范围是当前仓库的分析、重规划和改进；未访问生产数据库、修改远程配置、推送提交、发布包或调度新的云端 workflow。三个子任务分别核对历史声明、九模型执行路径及产品工作流，主任务复核交叉结论并集中构建测试。

| 材料 | 用途 | 边界 |
|---|---|---|
| [CHANGELOG 核查](changelog-verification-20260905.md) | 版本、宣传、历史遗漏与当前状态 | 712 个机械顶层条目不是 712 项动态通过；旧 26 项 Planned 已归档 |
| [九模型实现证据](nine-model-capability-evidence-20260905.md) | Core/API/Server/测试源码逐模型追踪 | 代码和测试存在不自动等于本轮运行或容量通过 |
| [产品工作流证据](product-workflow-evidence-20260905.md) | Web/Studio/VS Code 的入口和交互缺口 | mock、真实服务、宿主安装三类证据分开 |
| [机器可读 gap catalog](nine-model-gap-catalog-20260905.json) | 稳定缺口 ID、归属、状态和退出条件 | `partial` 不计作产品闭环通过 |
| [路线图](../../ROADMAP.md) | 当前未来工作及门禁 | 已交付切片写入 CHANGELOG，未来内容不混进发布变更 |
| [历史性能报告](../benchmarks/system-performance-20260901.md) | 既有九域指标和版本固定的竞品入口 | 历史热读/quick 不替代当前 fixed-hardware 或生产报告 |

## 九大能力是否成立

| 能力 | 真实基础 | 尚未形成的闭环 |
|---|---|---|
| 时序 | TAG/FIELD、WAL/segment、范围/窗口聚合、retention、协议摄取 | 高基数/碎片段目标容量；类型化批写背压、重试与逐点失败；同身份删后重写边界 |
| 关系 | 独立 rowstore、主次索引、约束、JOIN、SQL、轻事务、spill | 跨 keyspace 掉电原子性不支持；统计采样/成本仍不成熟；SQL 子集与端到端大结果内存 |
| KV | bytes/TTL/CAS/INCR/batch/snapshot，嵌入式 NX/XX/原子交换 | 新原子 API 远程 parity；异步 cursor、分项失败、诊断与故障后重试 |
| Document | CRUD/局部更新、索引、validator、mixed bulk、change feed、常用聚合 | 百万/千万真实容量；精确兼容矩阵与失败恢复；Mongo wire/BSON/分片明确不支持 |
| 全文 | 持久倒排、BM25/BM25F、短语/模糊/分词、重建 | 高层 search/facet/服务端 offset-highlight、相关性解释、重建进度与真实中文语料 |
| 向量 | HNSW/IVF/PQ/Vamana、精确回退、Document/Measurement/Hybrid | 通用 filtered ANN、有界 Top-K/扫描、质量/内存曲线；vector WHERE 的 NULL 不等于完整 SQL 三值逻辑 |
| 对象 | blob/metadata、bucket/version/range/multipart/policy/lifecycle | 每页全 bucket 扫描/排序；传输管理器与断点续传/校验/取消；S3 全兼容不支持 |
| MQ | 持久顺序日志、offset/group ACK、retention、push/pull | 实例备份一致性；nack/redelivery/DLQ、drain 与故障消费旅程；默认耐久必须说明 |
| 原生图 | 邻接/索引、CRUD、受限遍历/路径、SQL-PGQ、Graph 工作台 | 仍为 Graph Beta 范围；VS Code Explorer、外部对拍、恢复/Native AOT/168 小时门禁 |

统一闭环应是“建模/导入 -> 真实查询 -> 模型原生修改或消费 -> 分页/取消/失败 -> 诊断 -> 备份或重启恢复 -> 数据与权限对账”。不要求 MQ、对象、图伪装为关系 CRUD，也不把“统一菜单”作为恢复一致性证据。

## 证据、缺陷与修复路径

以下 E 为证据，F 为结论，Path 为实际调用链。严重度按当前影响排序。

| ID | 证据 | 缺陷与影响 | 修复及剩余边界 |
|---|---|---|---|
| F01 / E01，P0 | [最新 CI](https://github.com/IoTSharp/SonnetDB/actions/runs/33950712561)，light `compose-logs.txt:2`，`McpContractTests` | `Program.BuildApp -> MapMcp -> WithTools -> DeriveOptions` 缺少 `McpDatabaseListResult` JSON metadata，监听前崩溃；两 profile Parity 均跳过 | 注册合并业务和 SDK 源生成 metadata；禁止用启用反射或放宽探活替代修复。修复后的远程 CI 尚未运行 |
| F02 / E02，P1 | `TableStore.TryAutomaticStatisticsRefresh`；修复前 `Estimate_MissingStatistics_DoesNotSampleOnPlanningThread` 失败（前台快照 1，期望 0） | `Estimate -> RefreshStatistics -> snapshot/sample/WAL` 在业务线程工作，首读或过期统计会增加延迟 | 改为每库单任务的后台合并、4096 行/5 秒协作取消/30 秒冷却、状态与失败码；显式 ANALYZE 可取消；迟到采样不覆盖新分析。采样仍为前 N 行，快照准备/I/O 不能声称硬实时 |
| F03 / E03，P1 | `useSqlExecution.ts`，真实 TS 模块回归 | 多语句 await 后重新读目标；`BEGIN/COMMIT` 分成不同请求，不能共享 Server 请求内 transaction | 固定执行目标/历史归属，切换或卸载取消；单库事务送现有 `/sql/batch`；不支持混合事务提前拒绝；取消不声称撤回已发送写入 |
| F04 / E04，P1 | `api/sql.ts`，损坏/空/截断/尾部错误测试 | 非法 NDJSON 被吞掉或错误被忽略，可让 UI 显示成功并继续写 | 严格 frame 顺序、列宽、终态及计数，传播尾部错误；仍整段接收，流式内存问题未解决 |
| F05 / E05，P1 | `GraphWorkbench.vue`，真实 Vue setup 的延迟响应测试 | 旧审批闭包和旧请求可使用/覆盖新图上下文 | 审批绑定图/库/连接/凭据/参数；请求通道序号隔离；旧写历史仍归原目标。其他工作台另做同类核查 |
| F06 / E06，P1 | `SonnetMqStore.Ack`、12 个双存储模式边界用例 | `long.MaxValue + 1` 溢出；重复/旧 ACK 返回位置与实际 consumer offset 不同 | 写入并返回单调实际位点；校验真实 ACK 日志和 reopen。不改变默认 `SyncOnPublish`，也不新增 exactly-once |
| F07 / E07，P2 | `DocumentVectorSearchExecutor.ScoreRows`、差分与距离计数用例 | 原通用 WHERE 先计算全部向量距离，过滤无法减少距离工作 | 只有可证明为纯 metadata 的整条谓词前移；保留维度校验、旧 NULL 行为和混合谓词残差。未实现通用 filtered ANN，Scan/排序仍物化 |
| F08 / E08，P1 声明风险 | `SndbObjectStore.ListObjects`、Server `.system/mq`、README/CHANGELOG | 有界分页和九模型统一备份表述超出实现；轻事务/帧内存/历史 scripted eval 被过度概括 | 已修正文案，真实实现缺口保留 gap。未对尚无恢复证据的实现作“完成”声明 |

最重要的运行路径是 E01：scheduled build 成功 -> 容器启动 -> MCP Schema 推导异常 -> health 未就绪 -> Parity 跳过 -> summary/gate 正确失败。成功构建不能证明可启动，11 项 reliability PASS 也不能覆盖未执行的 Parity。

原始日志位于仓库忽略目录 `artifacts/project-audit-20260905/`；它们是本机证据，不会作为源码提交。远程 run 提供可核验的持久链接。必要复现命令见验证节。

## 性能与存储判断

优先消除不必要的工作量，而不是先增加线程、缓存或资源限额。统计前台扫描已移出，向量纯 metadata 条件可减少实际 distance 调用，但没有证据证明所有 SQL 或向量场景按固定比例变快。

| 路径 | 当前风险 | 验收指标 |
|---|---|---|
| SQL 规划 | 前 N 行采样偏差、索引页成本、参数不敏感反馈 | estimated/actual、候选/解码行、cold/warm 计划、P95/P99、后台竞争 |
| SQL 结果 | 服务端 Rows 物化、Axios 全 text、split/parse、历史全量 localStorage | 首行/首屏、网络字节、峰值 managed/JS heap、主线程长任务、取消后资源 |
| 对象 list | 每页扫全 bucket 并排序 | examined pointer/decoded metadata 随页候选增长；Unicode/分隔符/cursor 边界 |
| 向量 | 全量 Scan/JSON 解析、排序；质量与后端回退混淆 | 距离次数、候选数、Recall@K、RSS/分配、p50/p95/p99、backend/fallback |
| 存储耐久 | OS flush、fsync、硬件缓存、实例/数据库归属差异 | 默认耐久配置、WAL 放大、物理/逻辑 I/O、hard-kill/replay 与备份 hash 对账 |
| Web 加载 | Vite 报告 naive 与 visualization 大 chunk，后者约 1.62 MB minified | 真实冷启动下载/解压/解析、按需入口拆包、低配设备交互，不靠调高告警阈值 |

`.NET` 性能模式扫描仅用于筛选 Tables/SQL Execution 的候选（如 LINQ、分配和阻塞），没有把匹配数量当成 bug 数，也没有批量重写已有集合或异步代码。本轮未测行覆盖率，不能声称达到 80% 目标；测试数量也不是覆盖率。

## 开源经验是否真正采用

有实际采用的算法和工程机制：Redis 风格条件写/TTL/CAS、Mongo-like 索引和局部更新、Lucene 类倒排/segment/BM25、HNSW/IVF/PQ/Vamana、Kafka 类日志位点、S3 类版本/multipart、PostgreSQL 类成本/统计/JOIN，以及属性图邻接和受限遍历。证据是本仓库执行路径，不是名称相同就认定直接移植竞品源码。

既有[系统性能报告](../benchmarks/system-performance-20260901.md)已登记 14 个固定版本/commit 的竞品入口。本轮未新下载竞品源码、未做许可证溯源或外部性能对拍，不宣称已追踪各竞品最新版本。下一步学习必须落实为“机制 -> 适用语义 -> 实现切片 -> oracle/失败场景 -> 性能证据”，具体采用优先级如下：

| 参照 | 要吸收的机制 | 对应工作 |
|---|---|---|
| PostgreSQL / SQLite / DBeaver | 有代表性的统计、计划解释、执行上下文和取消/提交状态 | M41/M42、M36 #311/#313 |
| Qdrant / pgvector | filter 与 ANN 召回的显式取舍、精确回退和质量计数 | M35 #298、M36 #320/#321 |
| MinIO / S3 SDK | 稳定分页、流式 transfer、重试/恢复/校验和 | M36 #322/#323 |
| RedisInsight / MongoDB Compass | 有界浏览、类型/TTL/索引诊断和逐条导入失败 | M36 #316/#317，复用 M32 |
| NATS / RabbitMQ / Kafka | drain、投递确认、失败重投与消费者位点恢复 | M36 #324/#325/#326 |
| Neo4j Browser | 图表双视图、预算/截断可见和错误归因 | M40 及 M36 Graph 验收行 |

不自动承诺 wire protocol 全兼容、分布式副本/分片、完整 SQL/Cypher/MQL 或 exactly-once。是否直接复用源码需另有版本、来源和许可证记录。

## 重新规划的退出条件

1. 发布阻断：MCP 真实 Server 启动和 typed MCP 调用通过，修复后 light/full 重跑，重新累计七天成功证据。当前窗口 0/7，不是“只差三天”。
2. 可靠性基础：SQL/Graph 不向新目标发送旧审批，MQ ACK 位点一致；明确 database/instance backup，补消息与 consumer offset 的恢复方案。轻事务边界始终公开。
3. 每模型一个真实 golden journey：相同 seed/fixture 贯穿 Embedded、Server、SDK 和 UI；权限失败、取消、分页、重启/恢复都要验证。mock 与宿主安装分别记录，不相互替代。
4. 高频缺口：远程 KV 原子合同、对象有界分页/传输、MQ drain/重投、通用向量有界搜索、SQL 大结果内存。复用 M32/M35/M40 已有实现，不另造第二套 catalog 或查询引擎。
5. 生产门禁：固定 x64/ARM64、默认耐久、P50/P95/P99、吞吐/内存/GC/I/O、原始样本与完整性对账；真实模型质量、Studio 安装、Graph 外部对拍和 168 小时按既定门禁执行。

按仓库规范，后续提交/PR 应拆为 MCP、统计、MQ、向量、SQL UI、Graph UI、文档核查等单一职责，不把整个审计修复打成一个跨里程碑 PR。本轮没有代用户创建或提交 PR。

## 验证记录

环境：Windows x64，PowerShell 7.6.5，.NET SDK 10.0.400，Node 24.15.0，机器既有 Chrome；不安装或下载新浏览器。外部命令经任务专属有界 runner，.NET 并发限制为 4，构建不复用后台 compiler/server。

| 运行 | 结果 | 证据解释 |
|---|---|---|
| 修复前统计复现 | 1 个预期失败 | 证明业务线程确有采样，不计通过 |
| Core 全量首轮 | 4013/4013 PASS，0 skip | 包含统计首切片和 MQ；后续统计竞态/关闭及向量变更需最终定向运行 |
| SQL 工作流模块 | 9/9 PASS | 真实 TS，模拟 HTTP/响应式，不是服务端持久化 |
| Graph 工作流模块 | 7/7 PASS | 真实 Vue reactivity 与组件 setup，含乱序/目标变更 |
| 浏览器九模型入口与 Graph | 12/12 PASS | 真实 Chrome，API mock；包含桌面 canvas 像素和手机可用性 |
| Web 类型检查与生产构建首轮 | PASS | 不等于最终 Graph 修改或真实后端验收；大 chunk 告警保留 |
| Server MCP 首轮 | 6 PASS / 15 FAIL | 定位第二层 SDK `CallToolResult` metadata 缺失；不能记作修复通过 |
| Core 最终全量 | 4037/4037 PASS，0 skip | `core-verified.trx`，包括 19 个向量、12 个 MQ、新统计生命周期/显式分析竞态与 Rename 超时保留原表回归；前一轮 4035 PASS / 1 FAIL 的 Windows Rename 复现已修复 |
| Server 最终合同 | 28/28 PASS，0 skip | `server-verified.trx`，MCP 工具/权限/Schema、远程事务、新增真实 HTTP commit/rollback/错误及重启数据对账 |
| SQL/Graph 模块最终合跑 | 16/16 PASS | `web-workflows-verified.stdout.log`；真实生产模块，模拟 HTTP |
| Web 最终类型检查/生产构建 | PASS | `web-build-final.stdout.log`，包括 Graph 最后修改；naive 1322.61 kB / visualization 1615.91 kB minified 告警未隐藏 |
| 独立托管 Server 启动/首查 | PASS | `managed-smoke.stdout.log`；关闭 JSON 反射的真实可执行程序完成 healthz、建库/表、BEGIN/INSERT/COMMIT/SELECT，临时数据与子进程已回收 |
| win-x64 Native AOT 发布及启动/首查 | PASS，0 IL/AOT warning | `server-native-publish.stdout.log` / `native-smoke.stdout.log`；实际 native exe 完成 healthz、建库/表、BEGIN/INSERT/COMMIT/SELECT；不代替 Linux/ARM64、Graph 专项或完整 Parity |
| Graph 截图复核 | 2/2 PASS | 1600x1000、390x844，截图已人工检查；桌面非空画布像素通过。截图在 `output/playwright/m40-graph-operations/`，仍为 mock API |

先前失败记录保留，不用新成功覆盖历史根因。第一次尝试不重建依赖的 Server 测试因旧 DLL 未包含新增 helper 编译失败，随后重建真实 Server（0 warning / 0 error）并运行上述 28 项通过；该次编译失败不计作任何测试执行结果。

可复现入口（从仓库根运行，长命令应使用有界 runner）：

```powershell
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release --filter 'FullyQualifiedName~TableStatisticsTests|FullyQualifiedName~TableReadSnapshotTests|FullyQualifiedName~SqlExplain|FullyQualifiedName~SonnetMqAckBoundaryTests|FullyQualifiedName~DocumentVectorPrefilterTests|FullyQualifiedName~DocumentVectorIndexTests|FullyQualifiedName~SqlExecutorDocumentTests'
dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj -c Release --filter 'FullyQualifiedName~McpContractTests|FullyQualifiedName~McpEndToEndTests|FullyQualifiedName~Remote_Transaction'
node --experimental-vm-modules --test --test-timeout=15000 web/tests/sql-workflow.test.mjs web/tests/graph-workflow.test.mjs
gh run list --repo IoTSharp/SonnetDB --workflow parity.yml --event schedule --limit 7 --json databaseId,headSha,status,conclusion,createdAt,url
dotnet publish src/SonnetDB/SonnetDB.csproj -c Release -r win-x64 -p:SonnetDbPublishAot=true -p:BuildAdminUi=false -o artifacts/project-audit-20260905/server-native -m:1 -p:UseSharedCompilation=false -nr:false
```

## 报告自检

- 本地验收预览：`http://127.0.0.1:5198/admin/`，17:11（UTC+8）启动，30 分钟后由有界 supervisor 自动停止；HTTP 与 Frame 仅绑定 loopback，MQTT 关闭。数据独立位于 `artifacts/project-audit-20260905/preview-data`，不使用现有业务库；首次访问可按实际 setup 流程初始化。停止不删除用户在预览中写入的数据。
- 已完成任务的进程记录已复核；预览 supervisor 是有期限保留的服务。两个 smoke 数据目录已回收。首轮 MCP 初始化失败留下 14 个纯测试配置目录，清理调用被工具策略拒绝，未删除；完整路径保留于 `artifacts/project-audit-20260905/retained-temp-configs.json`，未尝试绕过限制。源码、报告、截图、发布产物和依赖缓存保留。

- [x] 以通用工程审计结构组织，关键结论具备 Evidence/Finding/调用路径，不套用无关安全报告内容。
- [x] 历史计划与已实现变更分开；声明超界、实现缺口、证据缺口和明确排除项分别记录。
- [x] 本轮未提交、发布、部署或改动用户生产数据；无 712 条全部验证、80% 覆盖率或九模型生产完成的声明。
- [x] 后续任务有原路线图归属和可验证退出条件；硬件、真实 provider、安装及长稳限制显式保留。
