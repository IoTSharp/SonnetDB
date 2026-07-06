# ROADMAP

本文件描述 SonnetDB 的分批 PR 开发计划，按 Milestone 组织。每个 PR 均包含：变更点、新增文件、测试覆盖与验收标准。

> **维护方式**：主路线图聚焦当前与未来 Milestone；已完成的早期详细路线已归档，避免当前规划被历史实现细节淹没。

图例：✅ 已完成 / 🚧 进行中 / 📋 计划中

---

## 已归档早期路线摘要

> Milestone 0 ~ 16 的详细 PR 拆分、设计说明、SQL/API 示例与历史路线差异说明已移至 [docs/roadmap-history.md](docs/roadmap-history.md)。主路线图仅保留摘要，聚焦当前和未来规划。

| Milestone | 主题 | PR 范围 | 状态 |
|-----------|------|---------|------|
| 0 | 项目脚手架 | #1 ~ #3 | ✅ |
| 1 | 内存与二进制基础设施 | #4 ~ #6 | ✅ |
| 2 | 逻辑模型与目录 | #7 ~ #9 | ✅ |
| 3 | 写入路径 | #10 ~ #13 | ✅ |
| 4 | 查询路径 | #14 ~ #16 | ✅ |
| 5 | 稳定性与性能（写入侧） | #17 ~ #21 | ✅ |
| 6 | SQL 前端 + Tag 倒排索引 | #22 ~ #28 | ✅ |
| 7 | 压缩编码（Delta / Gorilla） | #29 ~ #31 | ✅ |
| 8 | 服务器模式（HTTP + 远端 ADO + 控制面 + Vue3 后台 + SSE） | #32 ~ #34c | ✅ |
| 9 | 性能基准与发布 | #35 ~ #39 | ✅ |
| 10 | 批量入库快路径（历史扩展占位已拆分） | #42 ~ #45 | ✅（#40 转入 Milestone 18，#41 并入 Milestone 28 P5b） |
| 11 | 写入快路径（PR #45 瓶颈收尾） | #46 ~ #49 | ✅ |
| 12 | 函数与算子扩展（PID / Forecast / UDF） | #50 ~ #57 | ✅ |
| 13 | 向量类型与嵌入式向量索引（Copilot 知识库底座） | #58 ~ #62 | ✅ |
| 14 | SonnetDB Copilot：MCP 工具 + 知识库 + 智能体 | #63 ~ #69 | ✅ |
| 15 | 地理空间类型与轨迹分析 | #70 ~ #77 | ✅ |
| 16 | Copilot 产品化升级（嵌入式 AI 助手 UX） | #78 ~ #88 | ✅ |

---
## Milestone 17 — 可观测性与运行时可见性（Observability & Runtime Visibility）

> **目标**：把 SonnetDB 从「能跑起来」推进到「生产可运维」。统一指标 / 追踪 / 日志三大支柱，把当前散落在写入路径、Compaction、查询引擎、Copilot Agent 内部的状态以**标准化**形式暴露给运维与用户。
>
> **不变约束**：
> - **零运行时第三方依赖原则不变**：`SonnetDB.Core` 仅依赖 `System.Diagnostics.DiagnosticSource`（BCL 内置 Activity / Meter API），不引入 OpenTelemetry SDK。
> - `SonnetDB.Server`（HTTP / Web Admin / Copilot 宿主）允许引入 `OpenTelemetry`、`OpenTelemetry.Extensions.Hosting`、`OpenTelemetry.Exporter.Prometheus.AspNetCore`、`OpenTelemetry.Instrumentation.AspNetCore`、`OpenTelemetry.Instrumentation.Http`，因为该程序集本身已经依赖 ASP.NET Core。
> - 不破坏二进制格式（`FileHeader.Version` 不变）。
> - 默认开启基本指标 / 追踪；Prometheus 端点、Slow Query Log、Diagnostic Dump 默认关闭，需在 `appsettings.json` 显式开启。
> - 所有新端点遵守现有 Bearer + 三角色权限模型。
>
> **优先级调整（2026-07-04）**：**#89 ~ #91 前置于 M28 P5b #235 之前落地**——#89（Core Meter / ActivitySource 基线，纯 BCL 零依赖）插桩的正是 P5b 帧接入层将要施压的路径（`Tsdb.Insert` / `WriteMany` / WAL fsync / `QueryEngine.Execute`），#90/#91（Server OTel 引导 + Prometheus 端点/监控面板）紧随其后，让全模型高吞吐接入层**从第一天起就有生产级指标可观测**（#230 基准只覆盖 benchmark 环境，不解决线上可见性）；M29 #253 的 MQ 吞吐/积压曲线也显式依赖 M17 metrics。**#92 ~ #98 不阻塞 P5b**，按原顺序随后推进。

### PR 拆分

| PR | 标题与范围 | 状态 |
|----|------------|------|
| #89 | **M17.1：Core 端 Meter / ActivitySource 基线**：在 `SonnetDB.Core` 新增 `SonnetDB.Diagnostics` 命名空间，引入静态 `SonnetDbMeter`（`Meter("SonnetDB.Core", "1.0.0")`）与 `SonnetDbActivitySource`（`ActivitySource("SonnetDB.Core")`）。在写入路径（`Tsdb.Insert` / `BulkValuesParser` / `MemTable.Append`）、Flush / Compaction、Segment 读取、`QueryEngine.Execute`、WAL fsync 处插入 `Counter<long>` / `Histogram<double>` / `Activity?.Start()`，遵守 OTel 语义约定（`db.system=sonnetdb`、`db.operation`、`db.statement.kind`、`sonnetdb.segment.id`、`sonnetdb.measurement.name`）。**禁止引入 OpenTelemetry NuGet**，仅用 BCL `System.Diagnostics.Metrics`。 | ✅ |
| #90 | **M17.2：Server OpenTelemetry 引导**：在 `src/SonnetDB`（Server 入口）引入 `OpenTelemetry.Extensions.Hosting`，按官方推荐结构注册 `WithMetrics(b => b.AddMeter("SonnetDB.Core", "SonnetDB.Server").AddAspNetCoreInstrumentation().AddHttpClientInstrumentation())` 与 `WithTracing(b => b.AddSource("SonnetDB.Core", "SonnetDB.Copilot").AddAspNetCoreInstrumentation())`。Resource attributes 自动包含 `service.name=sonnetdb`、`service.version`、`service.instance.id`、`host.name`。OTLP Exporter 走 `OTEL_EXPORTER_OTLP_ENDPOINT` 环境变量，默认不导出（Console exporter 仅在 `Development` 启用）。 | ✅ |
| #91 | **M17.3：Prometheus 端点 + Web 内嵌指标面板**：可选启用 `/metrics`（`OpenTelemetry.Exporter.Prometheus.AspNetCore`），用 `Observability:Prometheus:Enabled=true` 开关。Web Admin 新增「监控」侧边栏，使用 `fetch('/metrics')` 客户端解析 prom 文本，实时绘制：写入吞吐（`sonnetdb.write.points`）、查询 P95（histogram bucket 还原）、MemTable 大小、Segment 数、WAL 落盘延迟、Copilot 调用数 / token 总量。零图表第三方依赖，使用既有 `naive-ui` + 简易 SVG 折线（与现有 dashboard 风格一致）。 | ✅ |
| #92 | **M17.4：Copilot 指标与追踪**：`SonnetDB.Copilot` 命名空间下新增 `CopilotMeter`（`Meter("SonnetDB.Copilot")`）记录 `copilot.chat.requests`（按 model / mode tag）、`copilot.chat.duration`、`copilot.chat.tokens`（in/out）、`copilot.tool.calls`（按 tool name tag）、`copilot.knowledge.recall.hits` / `.misses`；Agent 每次 `PlanToolsAsync` / `RunToolAsync` / `GenerateAnswerAsync` 都开 `Activity` span，把 `tool.name`、`tool.arguments.length`、`tool.result.rows` 写到 tags。CopilotDock 与 AiSettingsView 增加「最近 1 小时调用 / token 用量」摘要卡片（消费 `/v1/copilot/metrics` 简化端点）。 | 📋 |
| #93 | **M17.5：结构化日志统一**：所有 `ILogger` 调用改用源生成日志（`[LoggerMessage]`），消除运行时 string interpolation 装箱。统一日志事件分类（Write / Query / Flush / Compaction / Wal / Copilot / Auth / Http）与 EventId 区段（1000~1999 写入；2000~2999 查询；…）。在 `Program.cs` 引入 `JsonConsoleFormatter`，生产模式默认输出 JSON 行（`logging.json`），开发模式保持单行简化格式。 | 📋 |
| #94 | **M17.6：Health / Readiness 端点扩展**：把现有 `/healthz` 拆为 `/healthz/live`（进程存活）与 `/healthz/ready`（细分 checks：`segment_store_writable`、`wal_writable`、`copilot_provider_reachable`、`copilot_embedding_provider_reachable`）。引入 `IHealthCheck` 接口的 SonnetDB 实现（无第三方依赖），结果以 ASP.NET Core HealthChecks 标准 JSON 输出。Web Admin 顶部状态条改为消费 `/healthz/ready`，单独显示 4 个 check 的颜色点。 | 📋 |
| #95 | **M17.7：Slow Query Log + Top-N 查询统计**：可选开关 `Observability:SlowQueryLog:Enabled=true` + `ThresholdMs=10000`，并支持 30s / 60s 分级。`QueryEngine.Execute` 完成后若超过阈值则发 `Activity.RecordException`-风格的结构化日志事件，并写入内存环形缓冲（`SonnetDB.Diagnostics.SlowQueryRing` 默认 256 条）。新增 `GET /v1/diagnostics/slow-queries` 与 `GET /v1/diagnostics/top-queries`（按归一化 SQL 指纹聚合 count / p50 / p95 / max）。Web Admin SQL Console 旁边新增「慢查询」抽屉。 | 📋 |
| #96 | **M17.8：Diagnostic Dump 端点**：新增 `GET /v1/diagnostics/dump`（仅 admin token）返回 JSON 快照：进程 GC（`GC.GetGCMemoryInfo()` / `GC.GetTotalMemory(false)`）、ThreadPool（`ThreadPool.GetAvailableThreads`）、SonnetDB 内部计数（每 db 的 MemTable 大小 / Segment 数 / 待 Compaction 任务 / WAL 文件列表 / Copilot 在飞会话数）。**禁止 dump 用户数据点本身**，仅 metadata。CLI 新增 `sonnetdb-cli diag dump` 命令直接调该端点，便于复现性能问题时一键采集。 | 📋 |
| #97 | **M17.9：Copilot 服务端会话持久化（M16 M5 二阶段）**：在 `__copilot__` 系统库新增 `conversations`（`id TAG, title TAG, owner TAG, created_at, updated_at, message_count, summary FIELD STRING`）与 `messages`（`id TAG, conversation_id TAG, role TAG, content FIELD STRING, model TAG, tokens FIELD INT, ts`）两张 measurement；新增 `GET/POST/DELETE /v1/copilot/conversations[/{id}]` 与 `GET /v1/copilot/conversations/{id}/messages`；CopilotDock 「会话历史」Popover 在登录态下从服务端拉取（owner=当前 user），匿名/未登录回落到现有 `localStorage` 存储。会话历史可按 owner 隔离与跨设备同步。 | 📋 |
| #98 | **M17.10：CHANGELOG / docs / OTel 端到端验证**：补 `docs/observability.md`（指标列表、追踪 span 树、health checks 含义、prom scrape 配置示例、`OTEL_EXPORTER_OTLP_ENDPOINT` 与本地 Aspire Dashboard 联调）；补 `docs/troubleshooting.md`（常见慢查询模式 + diagnostic dump 解读）；补 docker-compose 示例追加可选 `otel-collector` + `prometheus` + `grafana` 三服务（`profile: observability`，默认不启动）；端到端验证：嵌入式启动 → 触发写入 / 查询 / Copilot 调用 → 在 Aspire Dashboard 看到完整 trace（HTTP → SQL → Segment 读取 → Copilot Agent → tool 调用）。 | 📋 |

### 推进顺序

```
第一波（前置于 M28 P5b #235，见上方优先级调整）：
PR #89（Core Meter / Activity 基线）
  → #90（Server OTel 引导）
  → #91（Prometheus + Web 监控面板）

第二波（不阻塞 P5b，按带宽推进）：
#92（Copilot 指标 / 追踪）
  → #93（结构化日志）
  → #94（Health 拆分）
  → #95（Slow Query Log / Top-N）
  → #96（Diagnostic Dump）
  → #97（Copilot 会话服务端持久化）
  → #98（文档 / docker-compose / 端到端联调）
```

**前置依赖**：Milestone 16 已合并。本 Milestone 不破坏 SonnetDB Core 二进制格式，对 `__copilot__` 系统库新增 measurement 走现有 schema 升级路径（`SeriesCatalog` 自动 upsert）。**Core 仍坚持零第三方运行时依赖**，OpenTelemetry SDK 只允许出现在 `src/SonnetDB`（Server 程序集）的 `csproj`。

**验收标准**：
- 嵌入式 + 服务器两种启动方式下 `dotnet-counters monitor SonnetDB.Core` 可立即看到核心指标；
- 启用 Prometheus 端点后 `curl /metrics` 可被标准 prom scraper 采集，关键 metric 含语义化 tag；
- Web Admin 监控面板在不依赖外部图表库的情况下展示写入吞吐 / 查询 P95 / Copilot token；
- 慢查询日志可在 `/v1/diagnostics/slow-queries` 看到归一化 SQL 指纹与时延分布；
- Diagnostic Dump 在 admin token 下返回完整 JSON，匿名访问 401；
- Copilot 会话历史登录态走服务端，匿名态回落 `localStorage`，切换设备能拉到自己的历史；
- 端到端：通过 Aspire Dashboard 或 OTLP Collector 能看到一次 HTTP → Tsdb 写入 → WAL fsync 的完整 span 树。

---

## Milestone 18 — VS Code 数据库扩展（SonnetDB for VS Code）

> **背景**：当前 SonnetDB 已经具备 VS Code 扩展所需的大部分服务端能力：`GET /v1/db` 数据库列表、`GET /v1/db/{db}/schema` schema 快照、`POST /v1/db/{db}/sql` ndjson 查询、三条 bulk ingest 端点、`POST /v1/copilot/chat/stream` 流式 Copilot，以及 `/mcp/{db}` 只读 MCP 工具集。与其再发明一套编辑器协议，不如直接把这些现成 contract 包装成 VS Code 原生体验。
>
> **核心策略**：
> 1. **Remote-first**：第一版优先连接远程 SonnetDB Server，复用现有 HTTP contract；不在首版把 `SonnetDB.Data` / `Tsdb` 直接嵌入 Node 扩展宿主。
> 2. **托管本地模式**：后续本地目录支持走“扩展帮用户启动一个指向指定 `data root` 的 SonnetDB Server”方案，再通过同一套 HTTP client 连接，避免 Node ↔ .NET 直连复杂度。
> 3. **TypeScript-first**：扩展主体用 TypeScript 实现，目录位于 `extensions/sonnetdb-vscode/`；后续若要复用 C# `SqlParser` 做 diagnostics，再以 sidecar / LSP 形式接入。
> 4. **安全默认值**：token 存放在 VS Code `SecretStorage`；Copilot 默认 `read-only`，切换到 `read-write` 需要显式确认。
> 5. **复用现有前端经验**：直接吸收 `web/` 中现有的 ndjson 解析、schema 自动补全、SonnetDB SQL 方言、结果图表和 Copilot 请求模型，避免重复造轮子。

### PR 拆分

| PR | 主题 | 状态 |
|----|------|------|
| #99 | **扩展骨架 + Manifest + Activity Bar 容器**：在 `extensions/sonnetdb-vscode/` 建立 `package.json` / `tsconfig.json` / `src/` / `media/` 结构；注册 `SonnetDB` Activity Bar、基础命令（Add Connection / Refresh / Run Query / Open Copilot / Start Managed Local Server）与 TreeView 骨架；本次仓库先落规划与占位代码，后续实现按下列 PR 继续填充。 | 🚧 |
| #100 | **远程连接模型 + SecretStorage**：实现连接配置模型（`remote` / `managed-local`）、`SecretStorage` token 持久化、活动连接选择、`/healthz` 探活、`/v1/setup/status` 首次安装探测；连接面板支持测试连通性与提示未初始化状态。 | 📋 |
| #101 | **Explorer 树：Connections → Databases → Measurements → Columns**：消费 `GET /v1/db` 与 `GET /v1/db/{db}/schema`，展示数据库 / measurement / 列结构；支持刷新 schema、复制 measurement 名、预留 sample rows / open in query runner 入口。 | 📋 |
| #102 | **SQL 执行链路 + SonnetDB 方言补全**：实现 `POST /v1/db/{db}/sql` ndjson 解析、Run Current Statement / Run Selection 命令；复用 `web/src/components/sonnetdb-dialect.ts` 的关键词与 schema 补全思路，先以编辑器命令为主，不急着上完整 Notebook。 | 📋 |
| #103 | **结果面板：Table / Raw / Chart 三视图**：新增 Query Result Webview Panel，支持表格、原始 ndjson/JSON、时间序列图表三视图；图表规则复用 Web Admin `SqlResultChart` 的时间列 / 数值列 / tag 分组启发式；补 query history 与导出钩子。 | 📋 |
| #104 | **VS Code 内置 Copilot 面板**：接入 `POST /v1/copilot/chat/stream`、`GET /v1/copilot/models` 与 `GET /v1/copilot/knowledge/status`；支持 `read-only` / `read-write` 模式切换、模型选择、引用折叠、最近执行 SQL 一键发送到查询面板。 | 📋 |
| #105 | **托管本地 SonnetDB Server 模式**：扩展选择本地 `data root` 后，自动启动 / 关闭本地 SonnetDB Server 进程，处理端口占用、日志输出与健康检查；本地与远程共用同一个 HTTP client 与 Explorer/UI。 | 📋 |
| #106 | **生产力增强**：Create Measurement 向导、bulk import（LP / JSON / Bulk VALUES）、starter snippets、从当前 SQL 或 schema 上下文打开 help / docs / explain 入口。 | 📋 |
| #107 | **Language Service / LSP Sidecar**：通过独立 C# sidecar 或轻量协议复用现有 `SqlParser` / schema 能力，补 diagnostics、hover、signature help、repair suggestion 与 `explain_sql` 集成。 | 📋 |
| #108 | **打包发布 + CI + 文档**：补扩展测试、VSIX 打包、Marketplace 元数据、截图与文档；在主 README / docs 中增加安装、连接、权限与本地模式说明。 | 📋 |

### 首批实现建议

第一批建议先做 `#99 ~ #103`，把“能连、能看、能查、能画”闭环跑通：

```
#99（骨架）
  → #100（连接 + SecretStorage）
    → #101（Explorer）
      → #102（执行 SQL）
        → #103（结果三视图）
```

`#104`（Copilot 面板）可以在查询闭环后立即接入；`#105`（托管本地模式）可与 `#104` 并行，但不应阻塞首个可用版本。

> **与 Milestone 29 的关系**：本里程碑保留 VS Code 扩展交付主线（#99~#108）。Milestone 29 的 #259 在 A/B/C 工作台契约（#245）落地后，负责**补完本里程碑 #103 结果三视图 + #104 Copilot 面板**（`streamCopilot` 客户端已实现只差接线），并把 Explorer 扩展为消费 M29 契约做 KV / 向量 / 全文 / MQ **只读浏览**；VS Code 定位开发者只读 + SQL 执行子集，完整 per-model 编辑体验以 Web Admin 旗舰为准。

### 目录约定

```text
extensions/
  sonnetdb-vscode/
    README.md
    ROADMAP.md
    package.json
    docs/
      architecture.md
      api-contract.md
    src/
      extension.ts
      commands/
      core/
      tree/
      panels/
      lsp/
```

### 验收标准

- 用户可在 VS Code 中保存至少一个 SonnetDB 连接，token 不落到明文 `settings.json`；
- Explorer 能展示数据库、measurement 与列信息，并可手动刷新；
- 编辑器可执行当前 SQL，结果在独立面板中查看；
- 结果面板至少支持 Table / Raw / Chart 三视图；
- Copilot 面板默认只读，切换读写前有显式确认；
- 本地模式不要求首版完成，但架构上已经明确走“托管本地 Server”路线，而非 Node 直嵌引擎。

**前置依赖**：无新的 Core 二进制格式变更；Milestone 18 第一阶段主要依赖现有 `src/SonnetDB` HTTP API 与 `web/` 中可复用的客户端逻辑。当前仓库已新增 `extensions/sonnetdb-vscode/` 目录，用于承载扩展骨架与后续实现。

---

## Milestone 19 — 生态适配底座能力（关系 + KV/缓存 + 对象桶 + 大量 measurement）

> **目标**：为上层平台和应用提供通用数据库能力，而不是在 SonnetDB 仓库规划某个上层项目的迁移、灰度、双写或回滚流程。IoTSharp 如何使用 SonnetDB 已迁入 IoTSharp 仓库 `ROADMAP.md` 的 RD-10；本仓库仅保留 SonnetDB 自身需要交付的通用能力。
>
> **推进原则**：
> - 不把 SonnetDB 当前 table MVP 直接包装成“完整关系库”；先补 ADO.NET、SQL、事务、迁移和查询翻译硬能力。
> - 不把普通 KV keyspace 直接冒充 Redis；先补 TTL、过期清理、并发语义和缓存 Provider。
> - 对象桶能力以 SonnetDB 通用 object storage API 为边界；上层项目的 BlobStorage/S3 接入和回滚策略由上层项目维护。
> - 大量 measurement、文件布局、compaction 恢复、增量索引和长稳专项属于 SonnetDB 通用能力，继续保留在本仓库。

### PR 拆分

| PR | 主题 | 状态 |
|----|------|------|
| #109 | **生态兼容边界与能力基线**：梳理 SonnetDB 作为关系、时序、KV/缓存、对象桶、向量搜索与全文搜索能力底座时需要承诺的通用 API、测试域和不支持清单；具体上层项目的兼容矩阵迁出到对应项目仓库维护。 | ✅ |
| #110 | **ADO.NET 事务与异步 API**：实现 `SndbTransaction`，把 SQL 层 `BEGIN/COMMIT/ROLLBACK` 接入 `DbConnection.BeginDbTransaction` / `DbCommand.Transaction`；补 `OpenAsync`、`ExecuteReaderAsync`、`ExecuteNonQueryAsync`、`ExecuteScalarAsync`、取消令牌和远程 `/sql/batch` 事务语义。第一阶段允许单表轻事务，测试明确拒绝跨表事务。 | ✅ |
| #111 | **关系表 DDL 与 schema metadata 扩展**：补 `ALTER TABLE ADD/DROP/RENAME COLUMN`、`RENAME TABLE`、默认值、nullable 变更、索引重命名、`INFORMATION_SCHEMA` / `GetSchemaTable` / provider manifest metadata；为 EF Core migrations 生成器提供稳定数据库能力描述。当前已落 `ALTER TABLE ADD/DROP/RENAME COLUMN`、`ALTER TABLE RENAME TO`、`INFORMATION_SCHEMA.tables/columns/indexes`、`DbDataReader.GetSchemaTable()` 与 `DbConnection.GetSchema()` provider metadata 基线；首版明确拒绝主键列变更、被索引列删除和缺省值不足的 NOT NULL 新列。 | ✅ |
| #112 | **关系查询能力补齐一：表表 JOIN / 子查询 / 聚合**：在 table executor 增加 table-table inner join、基础 left join、`COUNT/SUM/MIN/MAX/AVG`、`GROUP BY column`、`HAVING`、`IN`、`EXISTS`、简单子查询；覆盖 ORM 常见 `Include`、权限过滤、分页统计可翻译的通用查询形态。当前已落表表连续 `INNER JOIN`、派生表、WHERE 标量子查询和基础 GROUP BY 聚合；outer join / HAVING / IN / EXISTS 留后续 provider 兼容压测补齐。 | ✅ |
| #113 | **关系事务能力补齐二：跨表小事务与约束**：实现同一数据库内多表 DML 的原子提交与回滚；补唯一约束、外键约束的第一版校验策略、乐观并发列、并发冲突错误码；明确隔离级别边界。 | ✅ |
| #114 | **SonnetDB.EntityFrameworkCore Provider MVP**：新增 EF Core provider 包，包含 `UseSonnetDB(...)`、SQL generator、type mapping、migrations SQL generator、query translation 基础能力；先通过 provider 自测与最小 `DbContext` CRUD、Identity 子集、迁移创建/回滚测试。 | ✅ |
| #115 | **EF migrations history 与典型 ApplicationDbContext 兼容基线**：补齐 SonnetDB EF provider 的 migrations history 支持（`__EFMigrationsHistory` 或等价可配置历史表），让 `Database.Migrate()`、迁移升级、回滚、重复执行幂等检查和空库初始化成为 provider 入口验收；典型 ASP.NET Core Identity / ApplicationDbContext 兼容样例只作为 provider 通用测试，不承载上层项目路线图。 | ✅ |
| #116 | **KV TTL 与缓存 Provider**：在 KV keyspace 增加 expires-at metadata、惰性过期 + 后台清理、命名空间、批量 get/set/remove、前缀删除和过期统计；新增 EasyCaching provider 与可选 `IDistributedCache` provider。 | ✅ |
| #117 | **对象桶 API 第一版**：新增 bucket/object metadata 表、multipart upload 会话、etag/sha256、range read、copy object、delete marker、object tags、presigned URL；HTTP API 覆盖通用对象存储常用子集。 | ✅ |
| #118 | **对象生命周期、版本、审计与配额**：补 bucket policy、retention/lifecycle、object versioning、legal hold 占位、访问审计、容量统计和 quota；Web Admin 增加 Buckets / Objects / Multipart / Audit 页面。 | 🚧 |
| #119 | **生态接入样例与 Profile 文档边界**：保留 SonnetDB 作为嵌入式/远程服务、EF、缓存和对象桶的通用接入样例；具体 IoTSharp Profile、灰度、双写、回滚和生产验收迁出到 IoTSharp 仓库维护。 | 🚧 |
| #120 | **通用迁移与校验原语评估**：只规划 SonnetDB 通用 export/import、checksum、scan、backup/restore 原语；不在本仓库维护 `iotsharp migrate/verify/rollback` 等上层产品专用命令。 | 📋 |
| #121 | **通用长稳、压测和故障恢复报告**：覆盖 SonnetDB EF Core provider、批量写入、KV TTL、对象 multipart、备份恢复、断电恢复、升级回滚；上层 Profile 长稳报告由上层项目维护。 | 📋 |
| #122 | **大量物理分表文件布局与启动扫描优化**：面向大量 measurement / 大量 segment 场景，设计并实现分层 segment 目录布局（例如按 segmentId 前缀或时间桶拆分）、目录枚举兼容层、备份扫描优化、旧段清理策略和布局迁移工具；保留旧 `segments/{id}.SDBSEG` 读取兼容。 | ✅ |
| #123 | **Compaction manifest 与重复段恢复**：为 compaction 引入 manifest 或等价 superseded segment 状态，记录 source segments、target segment、提交阶段和清理阶段；启动时根据 manifest 忽略或清理被替代旧段，解决 crash after swap before delete 后新旧段同时加载导致重复数据的问题。 | ✅ |
| #124 | **SegmentManager 增量索引与后台维护成本控制**：将 `AddSegment` / `SwapSegments` 从全量重建索引快照优化为增量更新或分层索引发布；补充大量 segment 下 flush、compaction、retention、query 并发时的 CPU、内存和暂停时间基准。 | 📋 |
| #125 | **大量 measurement / 长稳专项套件**：新增百万级 series、万级 measurement、海量小 segment、随机重启、后台 flush/compaction/retention 并发、重复数据检测和恢复时间统计；输出“能改善什么、不能改善什么”的容量边界报告。 | 📋 |
| #126 | **SQL 正则模式查询与 EF 翻译规划**：在 `LIKE` 基线之后引入正则匹配能力，第一阶段支持 `regexp_like(input, pattern[, flags])` 标量函数，可用于 `WHERE` 过滤与 `SELECT` 投影；同时评估 `expr REGEXP pattern`、`expr NOT REGEXP pattern`、`RLIKE` 别名，兼容 MySQL、SQLite 常见写法。第二阶段补 `regexp_substr`、`regexp_replace`、`regexp_instr`，并在 EF provider 中把 `Regex.IsMatch(...)` 翻译为 `regexp_like(...)`。所有正则执行必须设置超时、限制模式长度、缓存编译结果，并在执行计划中明确标注 scan filter；后续可识别 `^literal` 前缀模式做索引剪枝优化。 | 📋 |
| #126.1 | **关系表大批量删除、逻辑删除与后台收缩**：补齐 rowstore / table executor 的批量删除快路径，避免 `DELETE FROM ... WHERE ...` 对大表逐行阻塞 HTTP/Kestrel 和前台事务。默认路线采用逻辑删除或 tombstone 标记，前台删除只写入删除标记、索引可见性变更和轻量统计；后台 compaction/vacuum/shrink 任务根据 CPU、IO、内存、活跃连接数和业务时段限速执行，逐步回收 WAL、snapshot、segment/rowstore 空间。新增 `TRUNCATE TABLE` / `DROP TABLE DATA` 等受权限保护的整表清空原语，用于测试重置和明确的运维场景，并提供可取消、可观测、可恢复的任务状态。 | 📋 |

### 推进顺序

```
#109（生态能力边界）
  → #110（ADO.NET 事务 / async）
  → #111（DDL / schema metadata）
  → #112（查询能力）
  → #113（跨表事务 / 约束）
  → #114（EF Core provider MVP）
  → #115（EF migrations history / 典型 ApplicationDbContext 兼容）
  → #116（KV TTL / 缓存 Provider）
  → #117（S3 API）
  → #118（对象治理）
  → #119（生态接入样例 / Profile 文档边界）
  → #120（通用迁移与校验原语）
  → #121（通用长稳 / 压测 / 报告）
  → #122（大量物理分表文件布局，已完成）
  → #123（Compaction manifest / 重复段恢复，已完成）
  → #124（增量索引 / 后台维护成本）
  → #125（大量 measurement / 长稳专项）
  → #126（正则模式查询）
  → #126.1（关系表大批量删除 / 逻辑删除 / 后台收缩）
```

### 验收标准

- SonnetDB ADO.NET、EF Core provider、KV/cache provider 和 object storage API 提供稳定的通用能力边界；
- EF Core provider 可通过典型 `ApplicationDbContext` 迁移历史表创建、迁移升级/回滚、重复迁移幂等检查、Identity 登录、主数据 CRUD 和核心查询；
- KV/cache provider 的 TTL 行为、批量操作、命名空间、过期清理和并发语义有独立测试；
- SonnetDB SQL 模式匹配能力必须覆盖 `LIKE`、`NOT LIKE`、`regexp_like` 在 `WHERE` 与 `SELECT` 中的行为，并明确正则超时、模式长度、编译缓存和 scan filter 边界；
- object storage API 覆盖上传、下载、删除、range read、multipart、presigned URL、版本、生命周期和审计回归；quota 与 Web Admin 继续推进；
- 向量搜索可通过 `VECTOR(N)`、KNN、向量索引重建、topK/distance 校验和过滤组合回归；
- 全文搜索可通过全文索引创建/删除/展示、中文/英文查询、BM25 排序、分页和索引重建回归；
- 通用迁移与校验原语支持导出、导入、checksum、scan、backup/restore 组合；上层业务双写和回滚流程由上层项目维护；
- 长稳报告明确 SonnetDB 自身的适用规模、单机边界、边缘部署边界和仍建议使用外部专用组件的场景。
- 大量物理分表场景必须覆盖启动目录扫描、备份枚举、compaction 清理、retention 删除和单目录文件数量上限，不再只以功能测试证明可用。
- Compaction 恢复必须证明崩溃后不会重复加载 source + target 段；若选择保守恢复，也必须有明确的重复检测与修复流程。
- 关系表大批量删除必须覆盖 IoTSharp 设备重建场景：3000+ 设备、数万最新值和相关身份/属性数据的删除请求不得长时间占用前台 HTTP 请求；删除后查询可见性应立即符合语义，物理空间允许由后台清理逐步回收，并能通过指标看到待清理字节数、清理速率、节流原因和最近错误。
- 后台清理/收缩必须支持资源感知调度：在 CPU、IO、内存或活跃查询压力高时自动降速或暂停，在空闲窗口继续推进；崩溃或重启后能从 manifest/checkpoint 恢复，不重复删除、不破坏索引和统计。
- 当前不把 IoTSharp 每设备 measurement 改为共享 measurement + `deviceId` TAG 作为默认路线；SonnetDB 侧优化应优先兼容现有物理分表/多 measurement 模式。

---

## Milestone 20 — 多模能力对齐与平移测试 (Parity)

> **目标**：用一份 docker-compose 同时拉起 SonnetDB 与开源组件全家桶（PostgreSQL / Redis / InfluxDB / VictoriaMetrics / MinIO / NATS / Mosquitto / Meilisearch / Qdrant / ClickHouse），用同一份场景脚本两边各跑一遍，证明"一台 SonnetDB 在边缘 / 单机场景能替掉这一组组件"。详细设计见 [docs/parity-roadmap.md](docs/parity-roadmap.md)。
>
> **设计原则**：
>
> 1. **不做协议兼容**。SonnetDB 走自有 `SndbConnection` / `SndbMqClient` / `SndbObjectStorageClient` / EF Core provider；竞品走它们的官方 .NET 客户端（`Npgsql` / `StackExchange.Redis` / `InfluxDB.Client` / `Minio` / `NATS.Client.Core` / `Meilisearch.Net` / `Qdrant.Client` / `ClickHouse.Client`）。
> 2. **不做替代主张**。对齐"一台开源组件、单进程、单节点"的能力面，不对齐 Redis Cluster / Kafka / Postgres HA / MinIO 分布式集群。
> 3. **三类对齐**：能力对齐（同场景两边都跑通）、可靠性对齐（同注入两边恢复语义一致）、算法准确度对齐（同数据两边统计量在容差内）。
> 4. **分布式留作下一步**。本里程碑不引入复制 / 副本 / Raft；待客户和长稳数据要求后再启动。
> 5. **够用即可**。性能数字写报告不做 gating；只对"在数量级以内"做健全性检查。
>
> **关键产出**：`tests/SonnetDB.Parity/` 测试项目 + `tests/SonnetDB.Parity/docker-compose.parity.yml` + GitHub Actions nightly + README parity badge + 八大支柱 × 至少 3 场景 = 24+ 场景红绿门槛。
>
> **连带产出**（不另立 PR）：KV `INCR/DECR/CAS/EXPIRE/PERSIST/TTL`、SonnetMQ `RecordTypeTombstone` 段滚动 + `FlushOnPublish=true` 默认、对象桶 `ListObjectsV2 ContinuationToken` 分页、`tests/SonnetDB.CrashTests/` 真子进程 SIGKILL、README 措辞与代码同步。

### PR 拆分

| PR | 主题 | 状态 |
|----|------|------|
| #127 | **Parity 骨架与第一对适配器**：新增 `tests/SonnetDB.Parity/` 测试项目（独立 csproj，`<IsAotCompatible>false</IsAotCompatible>`，不进 `SonnetDB.slnx` AOT 流水线）；新增 `tests/SonnetDB.Parity/docker-compose.parity.yml` 12 服务栈 + `.env` + `light/full` profiles + named volumes + healthchecks；harness 服务（dotnet sdk 镜像跑 `dotnet test`）+ ParityRunner xUnit 驱动 + JSON/Markdown reporter；落地 `IDataPlane` 契约 + `Capability` 标志位 + `ScenarioContext` + `ResultDiffer` + 容差判定基础设施；首对适配器 `SonnetDbAdapter` + `PostgresAdapter`（`Npgsql`），跑通 1 个 hello-world relational 场景作冒烟。 | ✅ |
| #128 | **关系型场景套件（vs Postgres）**：`scenarios/relational/` 目录新增 `tpcc_lite`（5 仓库 30 分钟）、`fk_cascade_constraint`、`isolation_read_committed`、`subquery_correlated`、`groupby_having`、`information_schema_introspection`、`update_returning_count`、`alter_table_evolution`；输出能力差异表（哪些 SonnetDB SKIPPED 哪些 PASS）。 | ✅ |
| #129 | **TSDB 场景套件（vs InfluxDB / VictoriaMetrics）+ 算法准确度对齐**：新增 `InfluxAdapter`（`InfluxDB.Client`）+ `VictoriaMetricsAdapter`（Prometheus remote_write + PromQL HTTP）；场景 `ingest_1m_points`、`groupby_time_window`、`derivative_accuracy`、`rate_irate_consistency`、`holt_winters_forecast_recall`、`percentile_p95_tdigest_vs_quantile`、`distinct_count_hll_2pct_error`；准确度判定接入 `ResultDiffer` 容差合同。首轮暴露的缺陷按 #129.1 / #129.2 拆分修复。 | ✅ |
| #129.1 | **修复 TSDB parity 缺陷：GROUP BY time bucket 投影**：允许 `SELECT time, avg(v) FROM m GROUP BY time(...)` 返回 bucket 起始时间，或提供稳定等价列名（如 `bucket` / `time`），并与 InfluxDB `aggregateWindow`、PromQL `query_range` 的时间戳语义写入 ResultDiffer 对齐合同。 | ✅ |
| #129.2 | **修复 TSDB parity 缺陷：forecast TVF 列投影契约**：`forecast(...)` 表值函数需暴露稳定列集合并支持 `SELECT time, value FROM forecast(...)`（或外层投影等价语法），让 Holt-Winters 预测召回可与 InfluxDB Flux `holtWinters` 做同构比较。 | ✅ |
| #130 | **KV 场景套件（vs Redis）+ 向量套件（vs Qdrant）**：新增 `RedisAdapter`（`StackExchange.Redis`）+ `QdrantAdapter`（`Qdrant.Client`）；KV 场景 `set_get_scan_throughput`、`ttl_accuracy`、`incr_concurrency_16_clients`、`cas_optimistic_lock`、`scan_cursor_10m_keys`；向量场景 `ann_recall_at_10`、`filtered_search`、`upsert_during_query`；连带交付 KV `INCR/DECR/CAS/EXPIRE/PERSIST/TTL` 实现（`KvKeyspace`）+ 后台 expirer worker。 | ✅ |
| #131 | **对象桶套件（vs MinIO）**：新增 `MinioAdapter`（AWS SDK pointed at MinIO endpoint）；场景 `putget_1gb_object`、`multipart_upload_5gb`、`range_read_offsets`、`list_objects_v2_pagination`、`copy_object`、`delete_marker_versioning`、`presigned_url_lifecycle`；连带交付 `ListObjectsV2 ContinuationToken` 实现 + `DeleteObjects` 批量端点（保留私有 JSON 协议，不引入 SigV4）。 | ✅ |
| #132 | **MQ 套件（vs NATS JetStream）+ replay 语义对齐**：新增 `NatsAdapter`（`NATS.Client.Core` + `NATS.Client.JetStream`）；场景 `publish_consume_ack`、`consumer_group_offset`、`replay_after_restart`、`fan_out_10p_10c`、`backpressure_unbounded_producer`；连带交付 SonnetMQ `RecordTypeTombstone(3)` + 段滚动 + 后台 RetentionWorker（time/size 双维度 trim）+ `FlushOnPublish=true` 默认值切换 + `TopicState` 分段化（64MB 切片，预留 LRU 读缓存入口）。 | ✅ |
| #133 | **全文套件（vs Meilisearch）+ BM25 排序对齐**：新增 `MeiliAdapter`（官方 `MeiliSearch` .NET 包 + HTTP API）；场景 `index_1m_documents`、`bm25_ranking_top10_overlap`、`cjk_tokenize_correctness`、`facet_filter_query`、`incremental_update_during_query`、`typo_tolerant_query`；BM25 top-10 重合率 ≥ 0.8 作为判定。 | ✅ |
| #134 | **分析套件（vs ClickHouse）+ 聚合精度对齐**：新增 `ChAdapter`（`ClickHouse.Client`）；场景 `groupby_time_1b_rows_wallclock`、`window_avg_7day`、`topn_per_device`、`columnar_compression_ratio`、`percentile_accuracy_p50_p95_p99`；明确 SonnetDB 不打吞吐战，但聚合数值必须在容差内。 | ✅ |
| #135 | **可靠性套件（kill -9 / disk-full / oom / power-loss）**：新增 `tests/SonnetDB.CrashTests/`（真子进程 + `Process.Kill(true)` 注杀，**不再用 `CrashSimulationCloseWal`**）；场景 `crash_kill9_during_fsync`、`crash_kill9_mid_compaction`、`disk_full_during_wal_append`、`oom_protection_memtable_backpressure`、`power_loss_torn_record`、`power_loss_half_renamed_segment`；对齐 Redis AOF / Postgres pg_basebackup / MinIO mc 的恢复语义。连带交付 `MemTableFlushPolicy.HardCapBytes` back-pressure（默认 4× MaxBytes，超限时同步等待 Flush 完成）+ `SegmentCompactor.Execute` `CancellationToken` 检查 + 三个后台 worker `catch` 块路由到 `ReportDiagnostic`（ROADMAP M17 已有 `TsdbDiagnosticEvent` 基础设施）。 | ✅ |
| #136 | **CI gating + nightly + parity-results 分支 + README badge**：`.github/workflows/parity.yml` 每日 02:00 UTC + manual dispatch；`{light, full}` × `ubuntu-latest` 矩阵；能力 / 可靠性 / 算法准确度三类作为红绿门槛，性能数字 warning only；nightly 结果 push 到 `parity-results` 孤立分支；README 新增 "Parity vs Open-Source Stack" 段落 + 通过率 badge；`tests/SonnetDB.Parity/reports/sample-run.md` 产出可读样例报告。 | ✅ |

### 推进顺序

```text
#127 (compose 骨架 + IDataPlane + 第一对适配器)
  → #128 (关系型 vs Postgres)
  → #129 (TSDB vs InfluxDB/VictoriaMetrics + 算法准确度)
  → #130 (KV vs Redis + 向量 vs Qdrant，连带 INCR/CAS/TTL 落地)
  → #131 (对象桶 vs MinIO，连带 ContinuationToken)
  → #132 (MQ vs NATS，连带 SonnetMQ Tombstone + 段滚动)
  → #133 (全文 vs Meilisearch)
  → #134 (分析 vs ClickHouse)
  → #135 (可靠性套件 + tests/SonnetDB.CrashTests/)
  → #136 (CI gating + nightly + badge)
```

### 验收标准

- 八大支柱 × 至少 3 场景 = 24+ 场景全部 PASS（含 SKIPPED 但有 `gap_reason` 字段）。
- `docker compose --profile full up` 在干净 ubuntu-latest 5 分钟内全部 healthy。
- nightly 连续 7 天通过率 ≥ 95%（剩下 5% 留给容器抖动）。
- README 新增 "Parity vs Open-Source Stack" 段落 + 链接到最新 nightly 报告 + 通过率 badge。
- `tests/SonnetDB.Parity/reports/sample-run.md` 产出可读样例报告（含 24+ 场景表格 + diff 列）。
- 至少 1 个真实算法精度差异被 parity 抓出来并修复（证明判定有效，不是橡皮图章）。
- 可靠性套件用 `Process.Kill(true)` 真崩溃，不再依赖 `CrashSimulationCloseWal`；torn-record / 半重命名段 / disk-full 三个剧本必须通过。
- KV `INCR / DECR / CompareAndSet / EXPIRE / PERSIST / TTL` 与 SonnetMQ `RecordTypeTombstone` + 段滚动作为本里程碑连带产出落地。
- Parity 项目设 `<IsAotCompatible>false</IsAotCompatible>`，不进 `SonnetDB.slnx`，不污染主仓 AOT 流水线；竞品官方客户端依赖隔离在 adapters 各自 csproj。

### 不做的事

- **不**实现 SigV4 / MQTT 3.1.1 / RESP / Postgres wire / Kafka wire 等协议兼容（永久不做）。
- **不**测试 aws-cli / mosquitto_pub / redis-cli 直连 SonnetDB（不在能力对齐范围内）。
- **不**做上层产品专用迁移工具（属于对应上层项目路线图；SonnetDB 仅保留 [Milestone 19](#milestone-19--生态适配底座能力关系--kvcache--对象桶--大量-measurement) 的通用迁移与校验原语）。
- **不**做绝对性能 gating（已在 [tests/SonnetDB.Benchmarks](tests/SonnetDB.Benchmarks/) 处理）。
- **不**引入 Testcontainers / k6 / Gatling / Allure / TestRail，不引入 Java/Go/Python 客户端。

---

## Milestone 21 — Document Store 单机能力升级（MongoDB-like，不做协议兼容）

> **目标**：把现有 KV-backed `Documents` 能力从"JSON 文档集合 MVP"升级到**MongoDB 单机常用能力子集**：集合 CRUD、文档查询、局部更新、二级索引、聚合、游标分页、单文档原子性和单机可靠性都达到日常应用可用水平。SonnetDB 继续使用自有 SQL / HTTP / `SndbDocumentClient` / ADO.NET 能力面，**明确不实现 MongoDB wire protocol，不承诺官方 MongoDB Driver 直连兼容**。
>
> **设计原则**：
>
> 1. **不做协议兼容**。不实现 MongoDB wire protocol / BSON command 协议 / replica set 握手；对比 MongoDB 时仅作为参考后端，SonnetDB 走自有 API。
> 2. **做常用语义兼容**。对齐单机应用最常用的 document CRUD、filter、projection、sort、limit、update operators、index、aggregation 子集，但允许 SQL / JSON API 语法不同。
> 3. **先单机，后分布式**。本里程碑不做 replica set、sharding、change streams、oplog、read preference、write concern majority。
> 4. **索引可解释、可重建**。所有 document index 都必须能在 `EXPLAIN`、schema endpoint、maintenance endpoint 和 backup manifest 中呈现，并支持离线 / 在线 rebuild。
> 5. **存储边界说清楚**。若 KV 底座仍以内存字典为主，本里程碑必须给出容量边界；若引入磁盘有序 KV/LSM，则作为独立 PR 明确文件格式、恢复和 compaction 验收。
>
> **关键产出**：`SonnetDB.Documents` 查询/更新/索引执行层升级 + `SndbDocumentClient` + 文档 REST API + 文档校验执行层 + 文档容量底座。MongoDB 参考 parity、Studio 管理界面、长稳报告和发布文档后移到独立里程碑，不再作为 Milestone 21 的交付范围。

### PR 拆分（仅能力 / 功能）

| PR | 主题 | 状态 |
|----|------|------|
| #137 | **Document API 契约与客户端第一版**：新增 `SndbDocumentClient`（嵌入式 + 远程），提供 `CreateCollection`、`DropCollection`、`InsertOne/Many`、`Find`、`FindOne`、`UpdateOne/Many`、`DeleteOne/Many`、`Count`、`Distinct`；服务端新增 `/v1/db/{db}/documents/{collection}/...` 私有 JSON API；保留 SQL 路径不变，并补齐 OpenAPI/README 示例。 | ✅ |
| #138 | **Find 查询语义补齐**：新增 document filter AST，支持 `_id`、嵌套 JSON path、`eq/ne/gt/gte/lt/lte/in/nin/exists/contains`、`and/or/not`、数组包含与 null/missing 区分；支持 projection、`sort`、`limit`、`skip` 与稳定结果排序；SQL `SELECT` 与 Document API 共享同一 planner。 | ✅ |
| #139 | **游标分页与批量读取**：新增 cursor token / continuation token，支持 `find` 分批返回、prefix/index scan 分页、服务端最大 batch size、token 过期与只读快照边界；HTTP API 和客户端统一消费 cursor。 | ✅ |
| #140 | **局部更新操作符**：实现 `$set`、`$unset`、`$inc`、`$min`、`$max`、`$rename`、`$push`、`$pull`、`$addToSet`、`$currentDate`、upsert 与 multi update；更新前后同步维护 JSON path index / fulltext index / hybrid index，并补齐冲突路径校验。 | ✅ |
| #141 | **文档索引体系升级**：在现有 JSON path index 基础上新增单字段 / 复合索引、unique index、sparse index、partial index、TTL index；索引 schema 持久化、在线 rebuild、增量维护、`SHOW/DESCRIBE INDEXES`、`EXPLAIN access_path=document_index` 全部落地。 | ✅ |
| #142 | **Document Query Planner 与代价模型**：根据 filter / sort / projection 选择 `_id`、单字段索引、复合索引、partial index、full scan；支持 index intersection 的第一版或明确不支持并给出 `gap_reason`；`EXPLAIN` 输出候选行估算、过滤下推、排序是否使用索引。 | ✅ |
| #143 | **Aggregation Pipeline 子集**：Document API 新增 `aggregate`，支持 `$match`、`$project`、`$group`、`$sort`、`$limit`、`$skip`、`$unwind`、`$count`、`$distinct` 等价能力；SQL 侧复用现有聚合函数与 window/extended aggregate 能力，保证数值结果与 SonnetDB 文档 API 契约测试在容差内一致。 | ✅ |
| #144 | **单文档原子性与批量写轻事务**：明确单文档更新原子提交；同 collection `InsertMany/UpdateMany/DeleteMany` 提供 ordered/unordered 批量语义和可回滚边界；错误码覆盖 duplicate key、validation failed、write conflict、document too large；并发写入保持索引一致。 | ✅ |
| #145 | **文档校验执行能力**：支持 collection validator（JSON Schema 子集或 SonnetDB 自有 schema 表达式）、required/type/range/enum/pattern 校验、validation action（error/warn）、稳定错误码与 SQL / HTTP / `SndbDocumentClient` 统一行为；仅落地引擎、契约和测试，不包含 Studio 治理界面。 | ✅ |
| #146 | **磁盘有序 KV / 文档容量底座**：评估并落地 document 主数据和索引所需的磁盘有序结构（LSM/SSTable 或 B+Tree page store 二选一）；目标是不再要求百万级文档全部常驻内存，覆盖冷启动、range scan、compaction、崩溃恢复和 backup/restore。若本 PR 选择延期，必须在能力边界内给出明确容量上限和替代实现计划。 | ✅ |

### 已迁出范围

- **Studio 管理面**：原 #145 中的 schema governance 查看 / 编辑，以及原 #148 Document Explorer、索引管理、JSONL/NDJSON 导入导出，迁入 [Milestone 24](#milestone-24--sonnetdb-studio-管理体验升级document-管理面)。
- **验收、长稳和发布文档**：原 #147 MongoDB 参考 parity，以及原 #149 百万 / 千万文档长稳报告、README / docs 能力矩阵和迁移指南，迁入 [Milestone 25](#milestone-25--document-store-验收文档与发布治理)。

### 推进顺序

```text
#137 (Document API + client 契约)
  → #138 (Find/filter/projection/sort)
  → #139 (cursor pagination)
  → #140 (update operators)
  → #141 (index体系)
  → #142 (planner + explain)
  → #143 (aggregation pipeline 子集)
  → #144 (原子性 + 批量写轻事务)
  → #145 (validator 执行能力)
  → #146 (磁盘有序 KV / 容量底座)
```

### 验收标准

- Document API 覆盖常用 CRUD：单条 / 批量 insert、find、update、delete、count、distinct、aggregate。
- `find` 支持嵌套字段过滤、数组包含、projection、sort、limit/skip 和 cursor 分页；百万文档场景下索引查询不退化为全表扫描。
- `$set/$unset/$inc/$push/$pull/$addToSet` 等局部更新能正确维护主数据、JSON path index、fulltext index 和 TTL index。
- 单字段、复合、unique、sparse、partial、TTL index 均可创建、删除、展示、解释、重建，并进入 backup manifest。
- `EXPLAIN` 能清楚显示 `_id` lookup、document index scan、fulltext candidate、document scan、sort in-memory 等访问路径。
- Aggregation 子集至少覆盖 `$match → $project → $group → $sort → $limit` 常见链路，并与 SonnetDB 文档 API 语义契约一致。
- 并发写入、崩溃恢复、索引 rebuild、TTL 清理、backup/restore 后文档主数据与索引一致。
- collection validator 对 SQL / HTTP / client 写入路径行为一致，校验失败返回稳定错误码，warn 模式不阻塞写入但可被调用方观测。
- 文档主数据和索引的容量边界清晰；若引入磁盘有序 KV，冷启动、range scan、compaction、崩溃恢复和 backup/restore 均有对应测试。

### 不做的事

- **不**实现 MongoDB wire protocol、BSON command 协议、`mongosh` / Compass / 官方 MongoDB Driver 直连 SonnetDB。
- **不**实现 replica set、sharding、oplog、change streams、transactions across databases、read concern / write concern majority。
- **不**承诺 MongoDB 查询语言逐字兼容；SonnetDB 可以提供 SQL / JSON API / client builder 三种自有入口。
- **不**把 MongoDB 作为运行时依赖；MongoDB 只允许出现在 parity / benchmark / migration reference 测试环境中。
- **不**为了兼容 MongoDB 引入 `src/SonnetDB.Core` 第三方运行时依赖。
- **不**在本里程碑交付 SonnetDB Studio 管理界面、MongoDB 参考 parity、长稳报告或发布 / 迁移文档；这些分别进入 Milestone 24 / 25。

---

## Milestone 22 — Agent Memory / Codebase Intelligence（应用层候选，非内置路线）

> **当前状态**：⏸️ 应用层候选，暂停内置派单。该方向更像“基于 SonnetDB 构建的 Code Memory / Agent Memory 应用”，不是 SonnetDB Core / Server / Studio 必须内置的数据库能力；#150~#159 不再作为 SonnetDB 内置路线派单。
>
> **复核确认（2026-07-04）**：本轮里程碑复核再次确认**不派单、不内置**。判断依据：(a) M22 是「建在 SonnetDB 上的应用」而非引擎能力；(b) 其所需能力（Document + FullText BM25 + Vector HNSW + Hybrid + MCP）**均已在库内存在**，M22 不会产出任何新引擎能力；(c) #152/#153 需要 Roslyn / tree-sitter / libgit2，违反 `src/SonnetDB.Core` 零第三方依赖铁律。M22 唯一保留价值是当「能力缺口探针」——若将来在 `examples/` 里 dogfood（如摄入 IoTSharp 自身仓库）暴露出某个**通用** Document / Vector / Hybrid 能力缺口，才把该缺口拆成独立 PR；Code Memory 应用本身不进产品面。
>
> **目标（应用视角）**：验证用户能否把 Git 仓库、设计文档、ADR、CI 变更、代码评审记录和 Agent 会话作为上层应用数据摄入 SonnetDB，并用 SQL / HTTP / MCP 查询“代码是什么、谁调用谁、为什么这么设计、改这里会影响哪里”。SonnetDB 的职责是提供通用数据引擎能力，不直接承诺内置 Code Memory 产品。
>
> **重新定位**：M22 若继续保留，应进入 `examples/`、独立应用仓库、插件或 Solution Accelerator，用来展示 SonnetDB 的 Document / FullText / Vector / Hybrid Search / MCP 组合能力。只有当应用验证出通用数据库能力缺口时，才拆出独立 Core / Server / Studio PR；不得因为 Code Memory 应用本身而把 Roslyn、Git 扫描、专用 code schema、专用 MCP tools 或 Code Memory Explorer 默认塞进 SonnetDB 内置产品面。
>
> **设计原则**：
>
> 1. **应用优先，不内置优先**。Code Memory schema、ingest、MCP tools 和 UI 默认属于上层应用，不进入 SonnetDB 默认产品面。
> 2. **数据库能力抽象优先**。若应用暴露出共性能力缺口，应沉淀为通用 Document / FullText / Vector / Hybrid Search / MCP / 权限能力，而不是沉淀为 codebase 专用 API。
> 3. **Core 零依赖边界不破坏**。`src/SonnetDB.Core` 不引入 tree-sitter、Roslyn、libgit2 等大型运行时依赖；代码解析与 Git 扫描放在独立应用、插件、扩展包或示例工具中。
> 4. **结构化优先，向量补充**。文件、符号、调用边、引用边、commit、ADR、会话、工具调用都以结构化表/文档/边表落库；embedding 用于语义召回，不替代确定性的 symbol / edge 查询。
> 5. **安全只读默认**。MCP memory tools 默认只读，按 project/repo/branch/owner 隔离；代码片段读取要有大小限制、路径白名单和审计事件。
>
> **候选产出**：独立 Code Memory 应用方案、示例 schema、独立 ingest 工具、独立 MCP Memory Server、Hybrid Search 示例和 VS Code / Copilot 接入样例；不默认新增 SonnetDB 内置 CLI 命令、Server 专用端点或 Studio 页面。

### 数据模型草案

| 类型 | 建议实体 | 用途 |
|------|----------|------|
| 仓库与文件 | `code_repositories`、`code_files`、`code_file_versions` | repo/project/branch/commit、路径、语言、hash、mtime、大小、license 元数据 |
| 符号与结构 | `code_symbols`、`code_symbol_locations` | namespace/type/method/property/endpoint/test 等符号定义与位置 |
| 关系边 | `code_edges` | calls / references / implements / tests / imports / routes_to / owns 等边 |
| 文本与向量 | `code_chunks` | 代码块、注释、README、docs、embedding、BM25/Hybrid Search |
| Git 演化 | `code_commits`、`code_changes` | commit 时间线、作者、文件变更、热点模块、变更趋势 |
| 决策与记忆 | `code_decisions`、`agent_memories`、`agent_tool_events` | ADR、设计决策、review 结论、Agent 会话摘要和工具调用审计 |

### 应用化候选拆分（暂停内置派单）

| PR | 主题 | 状态 |
|----|------|------|
| #150 | **Code Memory 应用方案与 schema 草案**：若保留，仅在示例 / 独立应用文档中定义 repo/file/symbol/edge/chunk/commit/decision/memory schema、索引建议、权限模型、规模边界和与 Document / FullText / Vector / Hybrid Search 的映射；不作为 SonnetDB 内置 schema 或默认文档主线。 | ⏸️ 应用化候选 |
| #151 | **独立 ingest 工具第一版（Git + 文件 + 文档块）**：作为应用 CLI / 示例工具扫描 Git 工作区、README/docs/source 文件，写入 repo/file/chunk/commit 基础数据；不新增 SonnetDB 内置 `sndb memory` 命令。 | ⏸️ 应用化候选 |
| #152 | **C# 符号索引器（Roslyn 可选应用层）**：在独立应用 / 工具层引入可选 Roslyn 分析路径，输出写入应用自定义 schema；不进入 `src/SonnetDB.Core` 或默认 CLI 运行时依赖。 | ⏸️ 应用化候选 |
| #153 | **调用边与引用边第一版**：作为应用层索引能力提取 calls/references/implements/tests/imports/routes_to 边；若需要通用图查询能力，另行论证为独立数据库能力。 | ⏸️ 应用化候选 |
| #154 | **独立 Code Memory MCP tools**：由应用自带 MCP Server 暴露 `code_search`、`symbol_search`、`code_callers`、`code_callees`、`code_impact`、`code_snippet`、`decision_search`；不新增 SonnetDB Server 内置专用端点。 | ⏸️ 应用化候选 |
| #155 | **Hybrid Search 示例与排序融合**：把 `code_chunks` 作为应用数据接入全文 BM25 + embedding KNN + metadata filter 融合，用于验证 SonnetDB 通用检索能力。 | ⏸️ 应用化候选 |
| #156 | **Agent Memory 应用 API**：面向 Agent 的 memory 写入/读取契约保留在上层应用；若后续证明为通用需求，再抽象为 SonnetDB 通用审计 / conversation / memory 能力。 | ⏸️ 应用化候选 |
| #157 | **Code Memory Explorer 应用 UI**：作为独立应用 UI 或示例页面展示 repo/project、索引状态、文件/符号搜索和影响分析；不默认进入 SonnetDB Studio / Web Admin。 | ⏸️ 应用化候选 |
| #158 | **VS Code / Copilot 接入样例**：在扩展或示例中消费独立 Code Memory MCP / API，展示“解释当前符号”“查找调用者”“改动影响分析”等应用场景。 | ⏸️ 应用化候选 |
| #159 | **应用规模与验证报告**：用 SonnetDB 自身仓库、IoTSharp 仓库和一个中大型开源 C# 仓库做 profile，输出应用层 ingest / 查询 / 增量成本报告；不作为 SonnetDB 核心发布门槛。 | ⏸️ 应用化候选 |

### 原草案推进顺序（暂停）

> 当前不按以下顺序派单；仅保留为后续重新论证时的历史草案。

```text
#150 (schema + docs)
  → #151 (Git/files/chunks ingest)
  → #152 (C# symbols)
  → #153 (edges)
  → #154 (HTTP/MCP query tools)
  → #155 (Hybrid Search)
  → #156 (Agent Memory API)
  → #157 (Web Admin Explorer)
  → #158 (VS Code / Copilot examples)
  → #159 (scale + docs)
```

### 验收标准

- 用户可以把任意本地 Git 仓库摄入 SonnetDB，并在 `GET /v1/db/{db}/schema` 或专用 status 端点看到 repo、文件、chunk、symbol、edge、commit、memory 的索引统计。
- MCP tools 能回答常见代码智能问题：搜索代码/文档、查符号定义、查 callers/callees、做一跳或多跳影响分析、返回带 source location 的片段。
- Hybrid Search 能融合代码文本、文档、符号 metadata、向量相似度和 Git 时间维度，结果带稳定 score 分解与引用。
- Agent Memory API 能保存和检索会话摘要、工具调用、review finding、ADR/decision，并按 owner/project/repo 隔离。
- 索引器支持增量重建、dry-run、取消、失败文件报告和可重复运行；不把生成索引提交到源码仓库。
- Web Admin 和 VS Code 至少各有一个可演示闭环：搜索符号、查看调用关系、把结果发送给 Copilot 或 MCP Host。
- `src/SonnetDB.Core` 继续保持零第三方运行时依赖；语言解析器依赖只允许出现在 CLI/扩展/测试/示例项目中。

### 不做的事

- **不**把 SonnetDB 绑定为某一个 MCP Host 或 IDE 的私有实现；MCP 只是对外接口之一。
- **不**在第一版实现任意图查询语言或复杂代码属性图数据库；先提供 typed tools 和稳定 schema。
- **不**承诺多语言 AST 全覆盖；第一阶段优先 C# / TypeScript / Markdown 的实用闭环。
- **不**把第三方语言解析器、Git 原生库或大型 AI framework 引入 `src/SonnetDB.Core`。
- **不**默认保存 secrets、大文件、二进制文件或 `.git` 内部对象内容；ingest 必须尊重 exclude 配置与大小限制。

---

## Milestone 23 — 搜索与向量引擎合并（DotSearch / DotVector 收编）

> **状态**：已完成。详细路线、Phase 1~5 范围和验收记录见 [`docs/search-vector-engine-consolidation-roadmap.md`](docs/search-vector-engine-consolidation-roadmap.md)。本节保留为总览中的里程碑锚点，避免已完成的搜索 / 向量收编历史散落到其他规划章节。

---

## Milestone 24 — SonnetDB Studio 管理体验升级（Document 管理面）

> **目标**：把 Document Store 已经具备的集合、索引、validator、维护端点和导入导出能力组织成 SonnetDB Studio 里的可用管理体验。本里程碑只做 Studio / Web Admin / 桌面壳相关的管理面，不把新的 Document Store 引擎能力塞回 Milestone 21。
>
> **边界**：管理 UI 可以消费 Milestone 21 暴露的 HTTP API、schema endpoint、maintenance endpoint 和 Document API；若发现后端缺少必要只读 metadata，可以补最小 server contract，但不在本里程碑新增查询语义、索引语义或存储格式。
>
> **与 Milestone 29 的关系**：本里程碑（#170~#172）是**文档模型的专属管理面**，仍在本里程碑交付；Milestone 29（多模型统一管理工作台）的 #257 只负责把本里程碑的 Document Explorer / Validator / 导入导出**接入统一外壳与共享结果 / 写审批框架**，不重复实现文档管理能力。

### PR 拆分

| PR | 主题 | 状态 |
|----|------|------|
| #170 | **Studio Document Explorer**：新增 Document Explorer，支持数据库 / collection 列表、集合 schema、索引列表、JSON 查询编辑器、结果表 / JSON 双视图、分页浏览、文档详情与只读复制；复用现有 SonnetDB Studio 布局、权限模型和 `CopilotDock` 上下文。 | 📋 |
| #171 | **Studio Validator Governance**：把 Milestone 21 的 collection validator 暴露为 Studio 管理体验，支持查看 / 编辑 validator、切换 validation action（error / warn）、查看 schema evolution / 变更历史、预检样本文档和保存前 dry-run；所有写入操作走现有写审批模式。 | 📋 |
| #172 | **Studio Document 导入导出与维护操作**：支持 JSONL/NDJSON 导入导出、`_id` path 映射、dry-run、批量错误报告、进度显示、取消、索引 rebuild 触发与状态查看；危险维护动作需要二次确认并记录审计事件。 | 📋 |

### 验收标准

- Studio 能完成集合浏览、文档查询、文档详情查看、validator 管理、索引查看 / rebuild 和 JSONL/NDJSON 导入导出。
- Document Explorer 与 SQL Console、Schema Explorer、CopilotDock 的数据库选择和权限状态保持一致。
- 所有写入、导入、rebuild、validator 保存动作都有 preview / dry-run / confirm 中至少一种防误操作机制。
- 管理面缺少后端能力时，只补 metadata / maintenance contract，不把 Document Store 查询、索引、事务或存储能力混入本里程碑。

---

## Milestone 25 — Document Store 验收、文档与发布治理

> **目标**：在 Milestone 21 的能力闭环和 Milestone 24 的 Studio 管理面之后，再集中做 Document Store 的参考 parity、长稳、容量报告和对外文档。这里是发布治理阶段，不阻塞 Milestone 21 的能力交付。

### PR 拆分

| PR | 主题 | 状态 |
|----|------|------|
| #173 | **MongoDB 参考 parity 套件**：在 `tests/SonnetDB.Parity` 新增 `MongoAdapter`（官方 MongoDB .NET Driver 仅连接参考 MongoDB 容器）与 `DocumentAdapter`（SonnetDB 自有 API），覆盖 CRUD、filter、projection、sort、update operators、index unique/TTL、aggregation、并发写、崩溃恢复后的索引一致性；报告中明确“语义对齐，不做协议兼容”。 | 📋 |
| #174 | **Document 长稳、容量与发布文档**：百万 / 千万文档 profile 长测，输出写入、查询、索引 rebuild、TTL 清理、冷启动、备份恢复、内存占用报告；README / docs 新增 Document Store 能力矩阵、MongoDB-like 迁移指南、明确不支持项、推荐规模和 Studio 管理入口说明。 | 📋 |

### 验收标准

- `tests/SonnetDB.Parity` 的 MongoDB 参考文档场景全部 PASS 或结构化 SKIP，SKIP 必须带明确 `gap_reason`。
- 长稳报告覆盖热 / 冷启动、索引 rebuild、TTL 清理、backup/restore、崩溃恢复和内存曲线，性能数字进入报告但不做主 CI gating。
- 发布文档必须明确 SonnetDB Document Store 与 MongoDB 的差异、迁移边界、不支持项、推荐数据规模和不做协议兼容的原则。
- 文档更新放在发布治理阶段收尾，不反向扩大 Milestone 21 的能力范围。

---

## Milestone 26 — 连接器路线独立化（C ABI + 多模型 API）

> **目标**：把连接器从“嵌入式示例”升级为独立产品路线。C ABI 继续作为跨语言稳定底座，当前保持 SQL-only；随后按 bulk / KV / Document / Object / MQ 分组扩展，不把多模型能力塞进单个 `execute` 函数。

> **边界**：
> 1. C ABI 只暴露 opaque handle、primitive、UTF-8 / byte buffer，不暴露 C# 对象、内部 engine 指针或磁盘格式结构体。
> 2. 先保证 `sonnetdb_open` 能通过 `SonnetDB.Data` 同时支持嵌入式与远程连接；若 NativeAOT 因项目引用失败，才使用链接文件方式引入必要 Data 层源码。
> 3. 新能力必须以独立 ABI 函数组落地，再由 Go / Rust / Java / Python / VB6 / PureBasic 等语言包装。
> 4. 不做 Redis / MongoDB / S3 / Kafka / Postgres wire protocol 兼容；连接器走 SonnetDB 自有 API。

| PR | 主题 | 状态 |
|----|------|------|
| #175 | **C ABI 底座改为 `SonnetDB.Data`**：`SonnetDB.Native` 引用 `SonnetDB.Data`，`sonnetdb_open` 接受完整连接字符串或旧式本地目录；当前 ABI 仍只覆盖 SQL 执行、result cursor、typed getter、flush、version 与 last_error。 | ✅ |
| #176 | **C ABI bulk ingest 分组**：新增 bulk handle / payload 写入函数，覆盖 LP / JSON / Bulk VALUES，支持 measurement、onerror、flush 参数；嵌入式和远程均走 `SonnetDB.Data` 的 bulk 路径。 | ✅ |
| #177 | **C ABI KV 分组**：新增 keyspace open、get/set/delete、scan prefix、ttl、incr、cas 基础函数；语言连接器包装为各自 idiomatic API。 | ✅ |
| #178 | **C ABI Document 分组**：新增 collection CRUD、find page、insert/update/delete、aggregate 的 JSON payload 函数；保持 JSON/UTF-8 边界，不暴露内部 document 类型。 | ✅ |
| #179 | **C ABI Object Storage 分组**：新增 bucket/object put/get/range/list/delete 与 multipart 基础函数；大对象采用 streaming/chunk handle，避免一次性内存复制。 | ✅ |
| #180 | **C ABI MQ 分组**：新增 topic publish/pull/ack/stats 函数；明确 offset、consumer group、ack 语义并对齐 `SndbMqClient`。 | ✅ |
| #181 | **上层语言连接器同步包装**：Go / Rust / Java / Python 优先同步 bulk + KV + Document；VB6 / PureBasic 作为源码级示例按能力选择性暴露。 | ✅ |

### 验收标准

- C ABI SQL-only 旧 quickstart 不改代码仍能运行。
- C ABI 可以通过 `Data Source=sonnetdb+http://...;Mode=Remote` 连接远程服务执行 SQL。
- 每个新增 ABI 分组都有 C quickstart 和至少一个上层语言 smoke。
- NativeAOT publish、CMake quickstart、Java JNI/FFM quickstart 在 Windows/Linux 能继续通过可用工具链验证。

---

## Milestone 27 — Industrial Data Agent 与 AI-ready 产品化路线

> **当前状态**：⏳ 依赖 M28，分两拨推进。#182 门面文档已基本落地（`llms.txt`、`docs/industrial-ai-applications.md`、README 第一屏定位均已就位），且 #183 想要的 MCP 工具契约（`list_databases` / `list_measurements` / `describe_measurement` / `sample_rows` / `query_sql` / `explain_sql` / `docs_search`）与 #185 想要的 provider 抽象（`OpenAICompatibleChatProvider` + `IChatProvider` / `IEmbeddingProvider`）**代码已实现**。因此 M27 剩余工作**不是「建 AI 功能」，而是「打包、定位、证明、去重」**。
>
> **排序原则（关键纠偏）**：M27 的产品主张是「可靠的工业本地引擎 + 可信 Agent」，而该主张的「可靠」部分要靠 **Milestone 28** 为真——M28 审计发现 Windows 默认配置下的真实丢数据缺陷，且 P4（索引/向量）、P5（MQ/接入）尚未完成。**在引擎可靠性做真之前做 Demo / eval，等于给一个仍会丢数据的引擎拍宣传片。** 故 M27 拆成两拨：
>
> - **可与 M28 并行的纯文档条**（零引擎风险、是采纳门槛）：#182 收尾、#185 provider-neutral 配置文档、#183 降级为「稳定 + 文档化现有 MCP 工具」（**不新增 Agent 工具**）、#188 边界声明。
> - **必须等 M28 收口后再启动**：#184 端到端工业异常 Demo（依赖 P5 MQTT 接入 + 可靠写入）、#187 eval 与成本指标（无真实用户前做 eval 是自嗨，推迟到有采纳之后）。
> - **移交去重**：#186 写入审批二阶段与 **Milestone 29** 的「共享写审批框架」重叠，归属 M29（审批是管理面能力），M27 只消费不重复实现。

> **目标**：把 SonnetDB 的对外门面从“多模型数据库”收敛为“面向 .NET 工业边缘应用的本地优先数据引擎”，并把 Copilot 从通用 SQL 助手推进到可被生产场景理解、演示和集成的 **Industrial Data Agent**。本里程碑优先做产品定位、AI-ready 文档、工业 Demo 和 provider-neutral 能力，**不新增引擎语义、不扩张 Agent 工具面、不改动核心二进制格式**。

> **边界**：
> 1. 多模型能力仍然保留，但作为能力矩阵描述，不再作为 README 第一屏的唯一定位。
> 2. Copilot / Agent 的第一责任是读取 schema、生成 SonnetDB 方言 SQL、执行只读分析、解释结果和请求写入审批；不绕过现有权限模型。
> 3. AI provider 必须走抽象层，不把 SonnetDB 绑定到 GPT、Claude、Gemini、DeepSeek、Qwen、Ollama 或任一单一供应商。
> 4. 工业 Demo 以 MQTT / HTTP ingest、设备异常、维修建议和上层平台集成为主，不把 SonnetDB 宣传为分布式云 TSDB 或大型集群平台；IoTSharp 联合样例归 IoTSharp 仓库维护。
> 5. **不新增 Agent 工具即可满足 #183**：现有 MCP 工具面已覆盖 list/describe/sample/query/explain/docs_search，M27 只稳定命名与参数并文档化，不铺大 Agent 表面。写入审批走 M29 框架，不在 M27 重复。

### PR 拆分

| PR | 主题 | 状态 |
|----|------|------|
| #182 | **AI-ready 门面文档第一批**：README / README.en 第一屏改为 `.NET industrial edge local-first data engine`；新增 `llms.txt`、`docs/industrial-ai-applications.md`，让开发者和 AI Agent 明确 SonnetDB 适合工业边缘、IoT telemetry、本地数据引擎、Copilot / MCP 场景。 | 🚧（可与 M28 并行收尾） |
| #183 | **稳定并文档化现有 MCP / Copilot 工具契约（降级：不新增工具）**：`list_databases` / `list_measurements` / `describe_measurement` / `sample_rows` / `query_sql` / `explain_sql` / `docs_search` **已在 `src/SonnetDB/Mcp/SonnetDbMcpTools.cs` 实现**；本 PR 只稳定命名、参数与权限边界并形成 typed contract 文档，**不铺大 Agent 表面、不新增专用端点**。异常分析优先复用已有 Core 算子 `anomaly(field,'zscore'/'mad'/'iqr',threshold)`（`AnomalyFunctions.cs`），仅在文档中给出「异常设备」查询范式，`analyze_measurement_anomaly` 单独工具**暂不新增**。 | 📋（可与 M28 并行，纯文档） |
| #184 | **工业异常分析 Demo（等 M28 收口后启动）**：新增 MQTT / HTTP ingest 示例，演示设备温度 / 电流 / 振动写入 SonnetDB，再通过 Copilot / MCP 提问“哪台设备今天最异常？”并生成报告；README、docs 和视频脚本统一使用同一数据模型。**依赖**：M28 P5b #242 MQTT 内建 broker + P0/P2 可靠写入（引擎主张为真后再拍 Demo）。 | ⏳（阻塞于 M28 P5 / P0） |
| #185 | **Provider-neutral Copilot 配置回归**：`OpenAICompatibleChatProvider` + `IChatProvider` / `IEmbeddingProvider` 抽象**已实现**；本 PR 把 Chat / Embedding provider 抽象文档化并补齐 OpenAI-compatible、Azure OpenAI、国内兼容网关、本地 Ollama / vLLM 的配置样例；Web Admin 模型选择器明确区分“平台默认模型”“自定义模型”“本地模型”。 | 📋（可与 M28 并行，纯文档 + 少量前端） |
| #186 | **写入审批二阶段 → 移交 Milestone 29**：与 M29「共享写审批框架」重叠，归属 M29（审批是管理面能力）。M27 只消费该框架、不重复实现。原范围（Copilot 写 SQL 进入 staged preview、`CREATE / INSERT / UPDATE / DELETE / DROP / GRANT / REVOKE` 展示 SQL diff / 影响范围 / 二次确认、服务端以权限和 `mode=read-write` 为上限）在 M29 交付。 | ➡️ 移交 M29 |
| #187 | **Agent eval 与成本指标（推迟到有真实采纳之后）**：新增 Industrial Data Agent eval 场景（异常设备、慢查询、schema 建模、维修建议、写入审批），并在 Copilot 指标中记录 provider、model、tool 调用数、失败原因和近似 token 成本，便于企业按成本选择模型。**排序**：无真实用户前做 eval 收益低，排在 #184 Demo 与首批采纳之后。 | ⏳（推迟，排在 #184 之后） |
| #188 | **上层平台联合样例边界**：SonnetDB 侧只提供工业边缘数据引擎、Studio、Copilot/Agent 和备份恢复的通用样例素材；具体 IoTSharp + SonnetDB 边缘节点样例迁入 IoTSharp 仓库 RD-10 维护。 | 📋（纯边界声明，可随时做） |

### 验收标准

- README 第一屏、docs 首页、`llms.txt` 和工业 AI 文档对 SonnetDB 的第一定位保持一致。
- AI / Agent 能从 `llms.txt` 找到 SQL 参考、工业应用文档、Studio / Copilot 文档和 Roadmap。
- 现有 MCP 工具（list/describe/sample/query/explain/docs_search）命名、参数、权限边界已文档化为稳定 typed contract，**未新增 Agent 工具面**。
- Provider 文档必须说明 OpenAI-compatible 抽象、本地模型路线和不绑定单一供应商的原则。
- Industrial Data Agent Demo 可以从样例数据跑到自然语言分析结果，且所有写操作都走 **M29 写审批框架**；**该验收项在 M28 P5（MQTT 接入）与 P0（可靠写入）收口后才启动**。
- 本里程碑不修改 `.SDBSEG` / `.SDBWAL` / KV / Document 等二进制格式，**不新增引擎语义、不扩张 Agent 工具面**。

---

## Milestone 28 — 可靠性、并发正确性与热路径加固（Reliability / Concurrency / Performance Hardening）

> **背景**：2026 年对 SonnetDB 做了一轮跨子系统深度审计（存储/持久化、索引、SQL 引擎、并发/性能四条线并行走查），共确认 54 项缺陷与优化点。其中若干在 **Windows 默认配置**下会真实丢数据或使数据"复活"，另有一批 SQL 层"返回错误结果 / 崩进程"的正确性 bug。本里程碑把这些发现按"先止血、再正确、再吞吐、再能力"的顺序拆成可逐一交付的 PR，逐步收口。
>
> **核心判断**：引擎架构方向正确（LSM 写路径、不可变 segment、CRC 校验、reader-lease 快照隔离、SIMD 聚合、有界 LRU 缓存底子都在），但多处为了吞吐牺牲了持久性与并发正确性，且这些取舍没有在默认值或文档中充分暴露。本里程碑不是重写，而是把这些取舍**要么修正、要么显式化并文档化**。
>
> **设计原则**：
>
> 1. **数据安全优先于吞吐**。P0 阶段所有改动以"不丢数据、不复活、不损坏"为唯一验收硬门槛，即使牺牲写吞吐也先修正，再在 P2 用双缓冲把吞吐补回来。
> 2. **不破坏二进制格式**。段头/尾 CRC（#195）走版本化可选字段或新 footer 版本，保留旧库读取兼容；`FileHeader.Version` 升级必须携带向后兼容读路径。
> 3. **复用既有机制**。后台 worker 的并发修复直接复用查询路径已验证的 `AcquireSnapshot()` 租约，不发明新同步原语。
> 4. **每个 PR 自带回归测试**。崩溃/掉电类缺陷必须有 `tests/SonnetDB.CrashTests/`（M20 #135 已建，真子进程 `Process.Kill(true)`）覆盖；SQL 正确性缺陷必须有确定性单测，附"修复前返回错误结果"的证据用例。
> 5. **默认值变更需显式声明**。凡改动默认持久性/并发语义（如 group-commit 默认开、Delete 强制 sync）都要在 CHANGELOG、`docs/architecture.md` 写入路径章节和 `TsdbOptions` XML 注释三处同步说明。
>
> **不变约束**：不引入 `src/SonnetDB.Core` 第三方运行时依赖；不改动对外 SQL / HTTP / ADO.NET / Document API 契约（能力增强除外）；Windows 目录 fsync 通过 P/Invoke `CreateFile(FILE_FLAG_BACKUP_SEMANTICS)` + `FlushFileBuffers` 实现，隔离在平台适配层。

### 阶段总览

| 阶段 | 主题 | PR 范围 | 目标 |
|------|------|---------|------|
| **P0** | 数据可靠性止血（Critical / 高危持久性 + 并发正确性） | #189 ~ #196 | ✅ 已完成——消除"会丢数据 / 复活 / 损坏 / 索引不可加载"的整类问题 |
| **P1** | 正确性与稳定性（SQL 错误结果 + 崩溃 + worker 静默死亡） | #197 ~ #203 | ✅ 已完成——消除"返回错误结果 / StackOverflow / 后台线程静默停摆" |
| **P2** | 写路径吞吐（锁内 I/O + 每点分配 + O(N²) 维护） | #204 ~ #211 | ✅ 已完成——把 P0 牺牲的写吞吐补回并超越，去除代数复杂度陷阱 |
| **P3** | 查询与 SQL 能力（plan cache + 下推 + join + 能力缺口） | #212 ~ #220 | ✅ 已完成——让 SQL/关系路径达到日常应用与 EF Core 可用水平 |
| **P4** | 索引与向量能力（文档惰性 scan + FTS 写放大 + 向量度量/ANN） | #221 ~ #229 | 让二级索引真正被使用、向量非 cosine/文档集合可加速 |
| **P5** | 消息队列吞吐 + 全模型高吞吐接入（MQ 硬化 + 自定义二进制帧 over HTTP/2 覆盖 MQ/时序/关系/向量 + MQTT broker/client 双形态设备接入） | #230 ~ #244 | 消除 MQ 单锁/无界内存/每写 flush；通用帧层消灭全模型 JSON/Base64 税、支持推送订阅与流式结果集；IoT 设备走 MQTT（内建 broker + 订阅外部 broker） |

> **当前焦点（2026-07）：P5a SonnetMQ 热路径硬化（#231 → #234）已全部收官 ✅；M17 #89~#91 可观测性基线已落地 ✅；P5b #235 通用二进制帧协议、#236 HTTP/2 流式推送订阅、#237 时序列式批量写、#238 SQL 流式结果集、#239 向量检索接入、#240 KV/对象/文档接入均已落地 ✅（帧协议覆盖 MQ + 时序 + SQL + 向量 + KV + 对象 + 文档全部七个 service，全模型二进制接入完成）；**#241 客户端 SDK 帧协议贯通 ✅（`SonnetDB.Data` MQ/KV/文档/ADO 只读 SQL 远程模式优先走帧、回落 REST，连接串 `Protocol=auto/frame-http2/rest` + `FrameChannel` 惰性探测，N2 收口）**；**P4 已开工：#221 文档查询惰性 scan ✅（I2 收口）、#222 FTS 批量成段 + 增量语料统计 ✅（I3 收口）、#223 向量度量贯通 + efConstruction 独立 ✅（I7、I9 收口——声明的度量 L2/InnerProduct 贯通建图与 ANN gate、efConstruction 与 efSearch 解耦，schema 文件升 v5 + SDBVIDX 升 v4 持久化度量/efConstruction、DDL 加 `metric=`/`ef_construction=`）**；下一步 = P4 #224（KNN block skip-index）或 P5b #242（MQTT 内建 broker），可按带宽并行；M17 #92~#98 不阻塞。** P0~P3 已收口，时序侧的「止血 → 正确 → 吞吐」已做真；**SonnetMQ 曾是引擎数据面唯一仍开着 🔴 Critical 的整块**——缺陷附录里这一条线挂了四个 🔴：MQ1（单一全局锁串行化所有 topic 的 Publish/Pull/Ack/Stats/Trim）、MQ2（单条 publish 两次 `ToArray()` + 空 header 仍 `new Dictionary`）、MQ4（`FlushOnPublish=true` 每消息一次 flush 系统调用），尤其 **MQ3（未裁剪消息全量常驻内存、段文件从不读、`SegmentCacheSize` 未实现 → 无界内存 / OOM）是架构级隐患**——边缘节点长期高吞吐会把进程 OOM 并丢掉所有在飞消息，与 P0 的「掉电丢数据」同属**数据可靠性**而非性能。按本里程碑「先止血、再正确、再吞吐、再能力」原则，**P5a 应先于 P4（#221~#229）**：P4 全是 🟠/🟡/⚪ 能力项（非 cosine 向量白占空间、文档查询多付一次全扫、FTS 写放大），命不中索引只是「慢但对」、可优雅降级、不阻塞正确性；M29 管理工作台 / M27 产品化 / M17 可观测性同理，均为零引擎风险的管理面与门面。**#230 基线已完成**（commit 79bb931，基线数字已量化 #233 组提交目标与 MQ4 每消息 flush 成本），**#231 per-topic 锁分片已完成**（`_topics` 改 `ConcurrentDictionary` + 每 topic `SyncRoot`，MQ1、MQ7 收口，SingleFile 模式回退共享锁保证流串行），**#232 写路径零冗余拷贝已完成**（`PublishPrepared` 消除双 `ToArray()`、空 header 复用 `EmptyHeaders`，`WriteRecord` 合并前缀缓冲 2 写，`EncodeHeaders` 走 `ArrayBufferWriter`+`Base64.EncodeToUtf8`，MQ2/MQ5/MQ6 收口），**#233 组提交 + 批量入口已完成**（`GroupCommitPublish` leader-flush 无定时合并刷盘 + `publish-batch` 端点/`PublishManyAsync`，MQ4 收口；未套 WAL 定时窗以免拖慢 os-flush 默认路径），**#234 冷数据下沉已完成**（`HotTailMaxBytes` 有界热尾 + 位置索引冷读 + `SegmentCacheSize` 只读句柄 LRU 真正生效 + `RetentionMaxAge` 改按段粒度，MQ3 收口——**SonnetMQ 四个 🔴 全部关闭，引擎数据面不再携带 Critical 缺陷**）。**P5b #235 已完成**（Core `SonnetDB.Protocol` 帧信封 + `MqFrameCodec`、Server `/v1/frame` PipeReader 增量解析 + h2c 5081 口、`EvaluateMqAccess` 两传输共用、`FrameEncodingBenchmark` 量化 JSON/Base64 税——16KiB publish 编码 5×/解码 60× 快、pull100 编码分配 16.8KB vs 2.2MB；REST 全保留、帧层零引擎语义变更），帧信封与增量解析循环即 #236~#240 的挂载底座。**P5b #236 已完成**（`WaitForMessagesAsync` per-topic pulse + `/v1/frame/stream` HTTP/2 双工推送订阅，N3 收口）。**P5b #237 已完成**（`TsdbFrameCodec` 列式批量写 + `TsdbColumnarPointReader` 流式列转行直通 `WriteMany`，wire 3.73× 小于 JSON、100k 行编码 326× 快、解析 4.5× 快，N5 收口——帧协议自此覆盖 MQ + 时序两个 service）。**P5b #238 已完成**（`SqlFrameCodec` SQL 流式列式结果集：meta→rows×N→end 同 streamId 帧序列、块内列类型推断 + null 位图 + variant 保大 long 精度、逐块 flush 响应缓冲上界 = 单块；只读语句门禁与 REST 同一判定；100k 行编码 2.7× 快且分配 2.2KB vs 24MB 零 LOH，N6 收口——帧协议覆盖 MQ + 时序 + SQL 三个 service）。**P5b #239 已完成**（`VectorFrameCodec` 向量 KNN 检索：查询向量 f32 二进制 `MemoryMarshal` 直传、响应复用 sql 帧块布局（`SqlFrameCodec` 响应编码抽 service/op 参数化内核）、检索内核与 SQL knn TVF 共用 `ExecuteKnnSearch` 零语义分叉、插入侧由 #237 Vector 列覆盖不重复路径，dim=1536 请求编码 ~1100× 快零分配、top-100 结果集编码 ~590× 快零 LOH，N7 收口——帧协议覆盖 MQ + 时序 + SQL + 向量四个 service）。**P5b #240 已完成**（`KvFrameCodec`（get/put/scan，key/value 原始字节直传）+ `ObjectFrameCodec`（get 流式 meta→data×N→end 分块复用 #238 思路 + put，内容零 Base64）+ `DocFrameCodec`（find ID/扫描 + insert，JSON 原始 UTF-8 直传），Server 挂载 kv/doc 同步 + object 流式分派，资源级鉴权抽 `EvaluateNamedResourceAccess` 与 REST 判定同序；管理面/复杂查询留 REST/SQL，N8 收口——**帧协议覆盖 MQ + 时序 + SQL + 向量 + KV + 对象 + 文档全部七个 service，全模型二进制接入完成**）。

### P0 — 数据可靠性止血

| PR | 标题与范围 | 关联发现 | 状态 |
|----|------------|----------|------|
| #189 | **Windows 目录 fsync + segment 落盘早于 WAL 回收**：实现真实目录 flush（P/Invoke `FlushFileBuffers` on dir handle），替换 `FlushDirectoryBestEffort` 在 Windows 上的空操作；保证 flush 路径中 segment 改名 + 目录项落盘**先于** WAL `RecycleUpTo` 删除旧段。同一目录 flush 修复应用到所有原子改名写入器（catalog / tombstone manifest / replacement manifest / checkpoint）。 | 存储 S2、S6 | ✅（新增 `DirectoryFsync`：Windows 走 `CreateFileW(FILE_FLAG_BACKUP_SEMANTICS)`+`FlushFileBuffers`（DllImport，无需 AllowUnsafeBlocks），Unix 走目录 fsync；`FlushDirectoryBestEffort` 委派之，段目录 flush 在 WAL recycle 之前的既有顺序即刻生效；补 `CatalogFileCodec`/`MeasurementSchemaCodec` 改名后目录 flush；新增 DirectoryFsyncTests；Core 2297 + CrashTests 6 全绿） |
| #190 | **Flush 改 add-then-reset**：`FlushNowLocked` 调整为"先 `Segments.AddSegment` 发布新 segment reader，再 `memTable.Reset()`"，消除并发查询在 Reset→Add 窗口内数据既不在 MemTable 也不在已发布 segment 的瞬时丢数据。 | 存储 S3 / 并发 C2(正确性部分) | ✅（随 #204 Phase 1 统一快照落地：SegmentManager 作为 {active+sealing MemTable+segments} 单一原子发布者，flush 用 `AddSegmentAndSwapActive` 一次 Volatile.Write 发布新段+换空表；QueryEngine/4 executor 改从统一租约读；新增 TsdbFlushAtomicityTests 并验证可捕获旧顺序回归） |
| #191 | **后台 worker 走租约 + maintenance 串行锁**：Compaction / Retention / `DropMeasurement` 全部改为 `using var lease = Segments.AcquireSnapshot()` 读租约，消除 reader use-after-dispose；引入一把 maintenance 串行锁序列化 Compaction 与 Retention 对 `SegmentManager` 的并发变更，杜绝"compaction 把 retention 刚删的过期点重新写回"的数据复活；`DropMeasurement` 补 try/catch。 | 存储 S5、S9 / 并发 C1 | ✅（新增 `_maintenanceSync`，锁序 maintenance→_writeSync 全局一致；CompactionWorker 整轮持租约 + 外层 try 兜住规划阶段防静默死亡；RetentionWorker/DropMeasurement 走维护锁 + 租约；新增 TsdbMaintenanceConcurrencyTests；Core 2293 + CrashTests 6 全绿） |
| #192 | **FTS manifest 原子改写 + 缺失重建 + fsync**：`ManifestFile.Save` 改为 temp → fsync → `File.Replace` 原子覆盖（杜绝 delete-then-move 中间窗口）；`LoadOrCreate` 在 manifest 缺失但 segment 文件存在时从 segment 重建而非静默建空；segment/manifest 写入补 fsync（内容 + 目录项）。 | 索引 I1、I15 | ✅（`ManifestFile.Save`/`SegmentFile.Write` 改 temp+fsync → `File.Move(overwrite:true)` 原子改名 + 目录 fsync（复用 #189 DirectoryFsync），杜绝 delete-then-move 丢文件窗口；`LoadOrCreate` 在 manifest 缺失时枚举 `segments/*.seg` 重建 ActiveSegments/NextSegmentId（tombstone 留空，宁少删不丢索引），无段文件才退化空 manifest；新增 2 项重建回归测试；Core 全绿） |
| #193 | **HNSW 快照跳过 tombstone**：`PopulateFromSnapshot` 重建 `_keyToRow` 时跳过 tombstoned 行（或 last-writer-wins），最好快照序列化阶段直接排除 tombstone；修复"删除后重插同 key 的持久化向量索引无法加载（ArgumentException）"。 | 索引 I4 | ✅（`PopulateFromSnapshot` 先登记 tombstone，再重建 `_keyToRow` 时跳过 tombstoned 行，并用索引器赋值（last-writer-wins）替代 `Add` 双保险；删除+重插同 key 的快照往返不再抛 ArgumentException；新增 `Snapshot_RoundTrip_AfterDeleteAndReinsertSameKey_Reloads` 回归测试并回插旧行为确认可捕获重复键异常；HNSW 15 全绿） |
| #194 | **Delete 强制持久化**：`Delete` 路径无条件 WAL sync（不受 `SyncWalOnEveryWrite` 影响）或同步持久化 tombstone manifest，消除"已持久化数据被删后崩溃恢复复活"。 | 存储 S4 | ✅（`Delete` 无条件走 `_walGroupCommit.Prepare`+`Wait` 强制 fsync WAL Delete 记录（group-commit 批处理并发删除、fsync 在锁外），WAL 为权威恢复来源；新增 `SonnetDB.CrashTests` 真 kill-9 场景 `crash_kill9_after_delete`（写不同步 + 单删未 checkpoint manifest），验证删除存活、数据不复活，并回插旧行为确认可捕获 51 点复活；Crash 7 + Core delete/crash/tombstone 120 全绿） |
| #195 | **段头/尾自校验 CRC**：为 `SegmentHeader` / `SegmentFooter`（含 v6 mini-footer 的 IndexOffset/IndexCount/FileLength/SegmentId）增加覆盖头尾字段的 CRC，open 时校验；位翻转若仍满足布局等式不再静默定位错误索引。走版本化字段，保留旧库读取兼容。 | 存储 S8 | ✅（`SegmentFooter` 的原 `Reserved0`（offset 36）改为 `FooterChecksum`，覆盖前 36 字节（Magic..Crc32，含 IndexCount/IndexOffset/FileLength）；writer `ComputeAndSetFooterChecksum` 写入，reader `TryReadPrimaryFooter` 在 `FormatVersion>=6 && FooterChecksum!=0` 时校验——版本门控 + 非零门控双保险，v2~v5 与旧 v6 文件（checksum=0）跳过，向后读兼容；struct 布局零变化（同大小字段改名）；新增 3 项 footer CRC 回归测试；segment/vector 722 + Core 2302 全绿。注：header 全 64B 已占满，未额外加 header CRC；footer 自校验已覆盖 S8 关注的字段位翻转） |
| #196 | **默认持久性语义决策 + 文档化**：决策并落地默认写入持久性——将 WAL group-commit 设为默认开（含 append 后至少 flush 到 OS），或显式声明"segment flush 前写入非持久"的窗口语义；在 CHANGELOG、`docs/architecture.md` 写入路径章节、`TsdbOptions` XML 注释三处同步说明，消除类注释"含 fsync 持久化"与实际行为的矛盾。 | 存储 S1、S2(文档) | ✅（决策：折中方案——新增 `TsdbOptions.FlushWalToOsOnWrite` 默认 `true`，每写把 WAL flush 到 OS（不 fsync），普通进程崩溃不丢已确认写、仅掉电可能丢；开销为一次用户态→内核态拷贝。可显式设 `false` 换极限吞吐。三级持久性（false ＜ 默认 ＜ SyncWalOnEveryWrite）在 TsdbOptions XML + CHANGELOG（Changed+Fixed）+ architecture.md 分级表三处文档化；新增 `crash_kill9_os_flushed_writes` 真 kill-9 测试证明 300 条已确认写存活，并回插旧行为确认全丢） |

### P1 — 正确性与稳定性

| PR | 标题与范围 | 关联发现 | 状态 |
|----|------------|----------|------|
| #197 | **SQL NULL 三值逻辑修正**：任一操作数为 NULL 的比较（`=` / `!=` / `<>` / `<` / `>` 等）判 UNKNOWN（行被排除），仅 `IS [NOT] NULL` 检查 null；修复 `NULL != 5` 判 TRUE、`NULL = NULL` 判 TRUE。统一应用到 WHERE / JOIN ON / HAVING 三条关系执行路径（TableSqlExecutor / RelationalSelectExecutor / JoinSqlExecutor）。 | SQL Q1 | ✅ |
| #198 | **`count(*)` 语义修正**：`count(*)` 定义为按时间戳并集的行/时刻计数，而非遍历每个字段列逐点累加（当前 3 字段 × N 时刻返回 3N）。 | SQL Q14 | ✅ |
| #199 | **事务覆盖时序 / 文档写**：`BEGIN` 内的 measurement `INSERT` 与 document DML 纳入事务缓冲以支持 ROLLBACK，或在事务上下文内显式拒绝（measurement 写当前直接绕过 transaction 立即持久化，ROLLBACK 只翻标志位）。 | SQL Q2 | ✅ |
| #200 | **解析器递归深度限制**：在 `ParsePrimary` / `ParseNot` / `ParseUnary` 跟踪嵌套深度，超过上限（如 200）抛 `SqlParseException`；杜绝深层括号 / `NOT NOT NOT…` / `------x` 触发不可捕获的 StackOverflow 崩溃整个宿主进程。 | SQL Q3 | ✅ |
| #201 | **后台 worker 异常兜底统一**：`CompactionWorker` 把 plan 获取步骤（`Segments.Readers` + `CompactionPlanner.Plan`）纳入 per-iteration try/catch，杜绝瞬时抛出逃逸 `WorkerLoop` 致 compaction 永久静默停摆；`KvExpirerWorker` 补 `ReportBackgroundWorkerDiagnostic` 诊断事件（与其余三个 worker 对齐）。 | 并发 C6、C11 | ✅ |
| #202 | **`WriteMany(Span)` 批内 backpressure**：大批量写入在批内分块检查硬顶（`MemTableFlushPolicy.HardCapBytes`），或限制单批大小并在 chunk 之间让出锁；杜绝百万点单批在一次 `_writeSync` 持有内无限撑大 MemTable/WAL 致 OOM 且阻塞所有写入者。 | 并发 C4 | ✅ |
| #203 | **durability fsync 移出写锁 + 关闭时排空 group-commit**：`SyncWalOnEveryWrite=true` 且 group-commit 关闭（或 window=0）时，把 `walSet.Sync()` 移到 `_writeSync` 之外执行（锁内捕获 sync 目标，锁外 fsync + Wait），消除"所有写入者串行排在 fsync 后"的吞吐悬崖；`Dispose` 前排空 pending group-commit，避免延迟 `Sync()` 在 WAL 已 dispose 后抛 ODE 到已返回的 `Write` 调用方。 | 存储 S10、S11 / 并发 C5 | ✅ |

### P2 — 写路径吞吐

| PR | 标题与范围 | 关联发现 | 状态 |
|----|------------|----------|------|
| #204 | **MemTable 双缓冲，flush 移出 `_writeSync`**：经典 LSM double-buffer——短锁内 swap 出新 MemTable 并捕获 WAL 位置，密封的旧表在**锁外**编码 + 落盘；写入者对新表并发写入，不再被整个 flush（编码 16MB + 写文件 + 2×fsync + WAL roll/recycle）阻塞。预计为写吞吐最大单项提升。 | 并发 C2 | ✅（RocksDB 式 `FlushPump` 单线程 FIFO 泵：`_writeSync` 内仅 O(1) 密封 swap + Roll，编码落盘/checkpoint/recycle 全在锁外泵线程；泵绝不取 `_writeSync`（checkpoint 走 Interlocked）破锁序死锁——schema 提升等写锁内触发只密封不等待。崩溃安全：Roll@seal 隔离并发写入，checkpoint 记录在段落盘后才追加，recycle(sealLsn) 精确回收。7 个触发点分流：FlushNow/backup/DropMeasurement/hardcap 同步等待，后台/schema 提升异步。新增 3 项 Phase 2 回归测试；Core 2290 + CrashTests 6 全绿） |
| #205 | **消除锁内每点堆分配 + 去枚举器装箱**：`EnsureMeasurementSchemaLocked` 仅在真检测到 schema 变化时才 copy-on-write（当前稳态每点 `new List(schema.Columns)` 后丢弃）；具体化或以 struct 枚举器暴露 `Point.Fields` / `Point.Tags`，消除 `IReadOnlyDictionary` foreach 的枚举器装箱，缩短 `_writeSync` 临界区、降低 GC 压力。 | 并发 C3 | ✅ |
| #206 | **MemTable 写路径同步开销精简**：在写入已被 `_writeSync` 串行化的前提下，减轻单写者路径的 `ReaderWriterLockSlim` + `ConcurrentDictionary.GetOrAdd` + per-bucket 锁 + 多次 `Interlocked` 冗余（这些机制只为 lock-free 读者需要）；保留读者安全语义。 | 并发 C10 | ✅（移除冗余的 `ReaderWriterLockSlim` 生命周期门——Append/Reset/RemoveSeries 已由 `_writeSync` 串行化；`ConcurrentDictionary` + 每桶锁 + `Interlocked` 统计量保留服务 lock-free 读者，新增读者并发压测回归） |
| #207 | **SegmentManager 增量索引**：`AddSegment` / `SwapSegments` / `DropSegments` 从全量重建所有 segment 索引（`SegmentIndex.Build` for all + `OrderBy().ToList()`）改为向 `MultiSegmentIndex` 增量增删单段索引；消除 flush 时 O(总 block 数)、segment 多时趋 O(N²) 的成本。与 M19 #124 目标一致，本 PR 落地。 | 并发 C7 | ✅ |
| #208 | **TombstoneTable 查询免拷贝**：`IsCovered` / `GetForSeriesField` 维护 per-key 不可变快照（比照现有 `_allSnapshot`），查询热路径 lock-free 返回，消除每次调用锁内 `list.ToArray()`。 | 并发 C8 | ✅ |
| #209 | **Catalog 快照发布防抖**：高基数写入时 `TagInvertedIndex` / `SeriesCatalog` 的单条 `Add` 不再每次全量重建整棵 `FrozenDictionary`/`FrozenSet`（当前 O(N²) + 大量瞬时分配）；改为合并/防抖发布或用不需全量 refreeze 的并发结构。 | 索引 I5 | ✅（改多级 `ConcurrentDictionary` 原地增量插入，读者无锁立即可见；单条插入 O(N) 冻结→O(1) 摊还） |
| #210 | **SegmentReplacementManifest 修剪与快照化**：修剪 source 与 replacement 都已不存在的 Committed 记录；启动时一次性快照 readability 而非对每条 committed replacement 都开 SegmentReader；避免会话内 O(N²) 重写与线性增长的启动成本。 | 存储 S7 | ✅ |
| #211 | **孤儿文件清理 + WAL footer 不变式收口**：启动时扫描并重试清理 manifest 标记为 suppressed 的死 `.SDBSEG`/索引文件（当前删除吞异常致磁盘泄漏）；把 `WriteLastLsnFooterIfDirty` 依赖"`_stream.Flush()` 先清空缓冲"的隐式不变式显式化（走同一 stream 或文档化断言），防未来改动破坏 WAL 帧。 | 存储 S12、S13 | ✅ |

### P3 — 查询与 SQL 能力

| PR | 标题与范围 | 关联发现 | 状态 |
|----|------------|----------|------|
| #212 | **SQL plan / parse 缓存**：按 SQL 文本（结合 schema 版本）缓存已解析 AST（有界 LRU），消除每次 `Execute` 重新 lex+parse 的分配与 CPU；为高频轮询同一 query 形状的仪表盘场景显著降本。 | SQL Q7 | ✅（`SqlParser.Parse` 进程级 512 条 LRU，按 SQL 文本 key；解析与 schema 无关且 AST 不可变，无需 schema 版本参与；所有 Parse 调用方透明受益） |
| #213 | **参数化查询 / 绑定变量**：新增位置 `?` / 命名 `@p` 占位符，贯穿 lexer→AST→executor；消除应用层字符串拼接的注入风险，并让 plan cache 对不同参数值复用。 | SQL Q10 | ✅（`TokenKind.Parameter` + `ParameterExpression` + `SqlParameterBinder` 值绑定；嵌入式 ADO 走 Core AST 绑定，远程因线协议仅 SQL 字符串仍客户端安全替换） |
| #214 | **LIMIT / Top-N 下推**：`Offset+Fetch` 下推到 scan/sort；`ORDER BY … LIMIT k` 用有界堆而非全量物化+排序（当前百万点全量物化排序后切片）。 | SQL Q6 | ✅（`TopN` 有界堆 O(N log K) 融合 ORDER BY + 分页；measurement / 关系表 / 关系子查询三路径统一走 `ApplyOrderByAndPagination`，稳定序保持） |
| #215 | **关系 JOIN hash join**：识别等值连接键，对 build 侧建哈希表（复用 `JoinSqlExecutor.BuildTableHash` 思路），替换关系路径全物化嵌套循环笛卡尔积（两张 1 万行表 = 1 亿次谓词求值）。 | SQL Q9 | ✅（`TryPlanHashJoin` 拆等值键建哈希探测，残差非等值项候选对上再过滤，含子查询 ON 回退嵌套循环；NULL 键不匹配 / LEFT 未命中保留 / 多列键 / 数值跨类型一致） |
| #216 | **相关子查询去关联 / memoize**：对 `IN(subquery)` / `EXISTS` / 标量子查询先做"是否引用外层列"静态判定；非相关子查询执行 0/1 次并缓存，相关子查询去关联为 semi/anti-join 或哈希内表；消除每外层行重扫内表 O(n_outer × n_inner)。（与末尾性能待办 P2 合并落地。） | SQL Q8 | ✅（运行时相关性探针 + per-查询记忆表：非相关子查询整段外层扫描只执行一次并缓存；相关子查询探针置位→不缓存逐行执行。基于运行时观测，杜绝误缓存。去关联为 semi/anti-join 留后续。） |
| #217 | **时序 WHERE 字段谓词 + OR**：`WhereClauseDecomposer` 增加按数据点求值的残差字段谓词（比照 JOIN 路径已有能力）并支持 OR；让 `WHERE temp > 30`、`WHERE tag='a' OR tag='b'` 可用（当前直接抛"不在 v1 支持范围"）。对 IoT 时序库是 table-stakes。 | SQL Q5 | ✅（不可下推谓词收集为残差合取，扫描路径逐点三值 Kleene 求值，仅保留确定 TRUE 的点；tag/time 仍下推为等值过滤+时间窗；有残差时禁用 latest / 流式窗口 / 扩展聚合 sidecar 快路径改走物化路径；`EXPLAIN` 复用同一分解器；DELETE 遇残差显式拒绝，字段级定向删除留 #219） |
| #218 | **事务隔离 / read-your-writes**：事务内 SELECT 叠加本事务已缓冲的 insert/update（当前读提交态、看不到自身缓冲写）；明确并文档化隔离级别。 | SQL Q4 | ✅（`SqlTransactionContext` ambient `AsyncLocal` 作用域；关系表 SELECT 读路径在已提交基线上按主键叠加本事务缓冲写，覆盖直接查询/聚合/子查询；隔离级别=读已提交+本事务 read-your-writes；ADO `BeginTransaction()` 透明获得；measurement/document 事务写已由 #199 拒绝故不涉及） |
| #219 | **关系 SQL 语义补齐**：`DISTINCT` 加关键字并实现或显式拒绝（当前静默误解析为列别名）；统一未加引号标识符大小写策略（关系/JOIN 路径当前 Ordinal 大小写敏感，与 projection 的 OrdinalIgnoreCase 不一致）；DELETE 支持按字段/值定向删除（当前对匹配 series 无差别 tombstone 所有字段列）；聚合返回类型改由 schema 静态类型决定而非额外全量预扫，避免 `Convert.ToDouble` 把整型/浮点混淆与大 long 精度丢失。 | SQL Q11、Q12、Q13、Q15 | ✅（`DISTINCT` 加关键字 + AST `Distinct` 标志，在 `ExecuteSelect` 单一收敛点结构化去重覆盖所有 SELECT 路径，标准顺序 SELECT→DISTINCT→LIMIT，去重比较器按"整型/浮点"两命名空间规范化避免大 long 折 double 误合并；关系/JOIN 列名比较全部经 `NameEquals`/`QualifierEquals` 统一为 OrdinalIgnoreCase，与投影一致；DELETE 遇残差（字段谓词/OR/IN）复用 #217 逐点三值 Kleene 求值，按命中时刻对该 series 所有 field 列单点 `[ts,ts]` 定向删除，未知列静态预校验硬报错；关系聚合输入类型由 `RelColumn.StaticType`（schema 静态类型）静态推断整型/浮点，命中即省全量预扫并对大 long 保持整型累加，仅表达式派生列回退逐行预扫） |
| #220 | **QueryEngine 流式合并**：大范围扫描在租约内 block-by-block 流式 merge/yield 并限制解码工作集，替换"先把全部候选 block 解码进 `List<DataPoint[]>` 再合并"的 LOH 堆峰值；decode cache 命中避免每次整份拷贝。 | 并发 C9 | ✅ |

### P4 — 索引与向量能力

| PR | 标题与范围 | 关联发现 | 状态 |
|----|------------|----------|------|
| #221 | **文档查询惰性 scan**：`DocumentQueryPlanner` 的全表 `store.Scan()` 候选改惰性，仅在真被选中时才物化（当前 `ChooseAccessPath` 的 `.ToArray()` 强制反序列化全集合，即便选了 `_id`/索引路径也付 O(collection)）。 | 索引 I2 | ✅（访问路径候选改惰性：`AccessCandidate` 持 `LoadRows` 委托，代价估算改用不物化文档的计数——`KvKeyspace` 新增 `CountPrefix`（可见性/过期语义与 `ScanPrefix` 一致但不读 value），`DocumentCollectionStore` 新增 `Count()` 走文档前缀计数、`CountByIndex`/`CountByIndexPrefix` 走索引条目前缀计数；planner 全表 scan 候选与索引候选的代价均由计数给出，只有胜出候选才真正加载行，`Execute` 命中 `_id`/索引路径时不再付整集合反序列化。EXPLAIN 输出形状不变（落选候选的 `rows=` 从物化行数变为计数估算，语义等价）。顺带收口同型强制物化：`DocumentSqlExecutor.ExplainAccess`/`DescribeCollection`、`HybridSearchExecutor`/`DocumentVectorSearchExecutor` 的 `ExplainAccess`、Server `MaintenanceEndpointHandler` 质量报告的 `Scan().Count` 全部改走 `Count()`/`CountByIndex`；并修复 `KvKeyspace.Delete` 对 compact 后磁盘驻留 key 直接 `_values.Remove` 不留 tombstone 的会话内复活缺陷（改走 `DeleteExistingLocked`）。新增惰性回归（索引/`_id`/复合前缀路径命中时 `FullScanCount` 不增长）、scan 回退等价、计数-物化一致性、KV `CountPrefix`（过期/删除/compact overlay）与 compact 后删除不复活测试） |
| #222 | **FTS 批量成段 + 增量语料统计**：`PersistentFullTextIndex.Index` 批量写入较大 segment 而非每文档一个单文档 segment + 全量改写 manifest（当前 O(N²)）；`ScoreTerm`/`GetFieldStats` 用增量维护的 docCount/totalLength 而非每查询遍历所有 segment。 | 索引 I3 | ✅（新增 `IndexMany`（整批单段 + manifest 一次落盘，批内重复 ID last-write-wins）与 `DeleteMany`（整批 tombstone 一次落盘）；manifest 墓碑列表按段惰性物化（`SaveManifest` 前只排 dirty 段，不再每次 tombstone 全量重排）；字段语料统计 `_fieldStats` 增量维护（随段加载/写入/tombstone/merge 增减），`GetFieldStats` O(segments×docs)→O(1)，BM25 分数与重开全量重建逐位一致。上层贯通：`DocumentFullTextIndexStore.UpsertMany`/`DeleteMany`、`Rebuild` 走 `IndexMany`；`DocumentCollectionStore` 批量路径（InsertMany/UpdateMany/DeleteMany/TTL 清扫）经 `ApplyPlannedMutationsLocked` 每索引一次 DeleteMany + 一次 UpsertMany 整批成段，KV 索引逐条语义不变。段格式/manifest 格式/崩溃恢复（#192）不变） |
| #223 | **向量度量贯通 + efConstruction 独立**：`VectorIndexAdapter` 把声明的度量（L2 / InnerProduct）贯通到建图与查询（当前一律按 cosine 建图且 ANN gate 仅 cosine，非 cosine 索引白占空间且仍暴力扫）；`efConstruction` 与 `efSearch` 解耦，默认 construction 更高，避免小 search-ef 把低质量图永久烤进持久化 blob。 | 索引 I7、I9 | ✅ |
| #224 | **向量 KNN 用 block skip-index**：`KnnExecutor.ScanSegment` 经 `MultiSegmentIndex`/`SegmentIndex.GetBlocks(series, from, to)` 做 series/时间范围 prefix-max 剪枝，替换 `foreach reader.Blocks` 全块逐一过滤（O(总 block 数)）。 | 索引 I8 | 📋 |
| #225 | **compaction 向量索引 catalog 必需**：对含 VECTOR 列的段，`SegmentCompactor`/`SegmentWriter` 的 `seriesCatalog`+`measurementCatalog` 由可选改为必需或加断言，避免调用方省略致 compacted 向量块无索引段、静默退化为暴力扫。 | 索引 I11 | 📋 |
| #226 | **HNSW ef 补偿 tombstone + 重建回收**：搜索按 tombstone 比例放大 ef 或持续搜索至收集满 topK 个存活结果（当前 `ef=max(EfSearch,topK)` 过滤 tombstone 后可能欠返回）；提供周期性 compaction/rebuild 物理丢弃 tombstoned 行并重指 `_entryPoint`，回收 churn 下的无界内存增长。 | 索引 I6、I14 | 📋 |
| #227 | **文档集合持久 ANN 索引**：为 document collection 的 `vector_search` 提供持久化 per-collection ANN 索引或至少缓存已解析向量，替换全表 `store.Scan()` + 每行 `JsonDocument.Parse` + 距离的 O(N·dim) 暴力扫。 | 索引 I12 | 📋 |
| #228 | **删除遗留 `HnswVectorBlockIndex`**：删除或明确隔离仍被测试维护的死代码 `HnswVectorBlockIndex`（图质量更差、O(n·ef²) 建图），统一到 `HnswIndex<int>`，消除误用风险。 | 索引 I13 | 📋 |
| #229 | **文档索引原子维护 + 崩溃重建校验**：验证 document 二级索引在 insert/update 时与主数据原子写入、崩溃后随集合重建（当前 planner 依赖索引"过包含"再用 `Matches` 复检，一旦"欠包含"会静默漏行）；补一个覆盖扫描一致性校验。 | 索引 I10（疑似，需先验证） | 📋 |

### P5 — 消息队列吞吐 + 全模型高吞吐接入

> **背景**：本阶段合并两条主线。**(1) SonnetMQ 热路径**（`src/SonnetDB.Core/Mq/SonnetMqStore.cs`）——审计发现与 P2 写路径同类的问题（单一全局锁、锁内 I/O、每消息多次拷贝），且有一处**架构级隐患**：所有未裁剪消息全量常驻内存、段文件从不被读、`SegmentCacheSize` 明写「保留未实现」，长期高吞吐会 OOM。**(2) 全模型接入层**——SonnetDB 的时序 bulk-ingest、SQL/关系结果集、向量检索、KV、对象、文档、MQ **全部走同一套 HTTP+JSON 端点**，二进制/数值负载被 JSON 编码课税：MQ/对象 payload 走 Base64（+33% 体积 + 编解码 CPU），向量 `float[]` 走 JSON 数字文本（比 Base64 更浪费），大 SQL 结果集序列化是真瓶颈，且全部只能请求-响应、无推送/流式。
>
> **核心判断**：与 M28 其余阶段一致——方向对（各模型引擎本身没问题），但接入层用「一套 JSON 打天下」牺牲了二进制/数值/大结果集场景的吞吐，且 MQ 自身热路径为「先做出来」牺牲了并发与内存边界。P5 不重写任何引擎，而是**修正 MQ 热路径**并**补一条覆盖全模型的高吞吐通用接入通道**，现有 REST/JSON 全部保留向后兼容。
>
> **两段式结构**：
>
> - **P5a MQ 热路径硬化（#230~#234）**：基准先行，然后去全局锁、零拷贝写、组提交、冷数据下沉。纯 `SonnetDB.Core` 内改动，与接入层解耦。
> - **P5b 全模型高吞吐接入（#235~#244）**：设计**通用二进制帧**（帧头带 `service`+`op`+`stream-id` 多路复用字段），**承载于 Kestrel HTTP/2**（复用现有鉴权/路由/TLS/流控/多路复用，Core 与 Server 均零第三方依赖），先落 MQ，再逐个模型接入（时序列式批量写 → SQL 流式结果集 → 向量检索 → KV/对象/文档）；IoT 设备侧兼容 **MQTT 双形态**（#242 内建 broker 设备直连 + #243 client 订阅外部 broker，均用 IoTSharp/MQTTnet.AspNetCore.Routing）。**不做裸 TCP**——评估表明其相对 HTTP/2 的收益（小消息高频约 1.2~2×）不足以抵消重写分帧/鉴权/心跳/TLS/流控的复杂度，且本仓 #230 基线显示传输层开销（个位数 µs）被 store 的锁/flush（几十~几百 µs）碾压，传输不是当前瓶颈。
>
> **行业对标依据（2026-07 走查主流数据库 / MQ / 时序库线协议）**：
>
> - **二进制 + 长度分帧是铁律**：PostgreSQL(pgwire)、MySQL、MongoDB(OP_MSG+BSON)、Redis(RESP)、Cassandra(CQL)、Kafka、Pulsar、TDengine(taosc)、IoTDB(Thrift) 的数据面**无一用 HTTP/1.1+JSON**。→ 印证 #235 二进制帧方向，收益主要来自**消灭 JSON/Base64**，与本仓 #230 基线一致（传输层开销是个位数 µs，被 store 的锁/flush 几十~几百 µs 碾压）。
> - **时序写入收敛到列式批 + Line Protocol**：IoTDB `insertTablet`(列式 Tablet)、TDengine STMT binary、PG `COPY BINARY`、InfluxDB Line Protocol——**没有一个用行式 JSON**。IoTDB/TDengine/QuestDB 都兼容 InfluxDB Line Protocol（本仓已有 `InfluxLineProtocolEndpointHandler`）。→ 支撑 #237 列式二进制批量写。
> - **新系统的传输在倒向 HTTP/2，而非自造裸 TCP**：InfluxDB v3(IOx) 从 HTTP 演进选了 **Arrow Flight SQL over gRPC(HTTP/2) + 列式 Arrow**（大 payload 走列式二进制、控制面走 RPC）；etcd、Google Pub/Sub 走 gRPC(HTTP/2)；连 TDengine 都为跨语言易用补了 **WebSocket** 层。裸 TCP 自定义协议（pgwire/taosc/Kafka）多是十余年历史资产 + 巨量分帧/流控/TLS/心跳工程投入。→ SonnetDB 处境最像 InfluxDB v3，故传输选 **HTTP/2**；帧内大 payload 学 Arrow Flight「控制面 RPC + 数据面列式二进制」的分层。
> - **设备接入普遍内建 MQTT**：IoTDB、TDengine 都内建 MQTT broker 供设备直连；InfluxDB 则靠 Telegraf 作 MQTT client 订阅外部 broker。IoTSharp 是 IoT 场景，设备侧真正在说 MQTT。→ 新增 #242（内建 broker）+ #243（client 订阅外部 broker）两形态，统一用 IoTSharp/MQTTnet.AspNetCore.Routing。
>
> **传输决策（自定义 HTTP/2 帧，非 gRPC，非裸 TCP）**：评估 gRPC(grpc-dotnet 纯托管、无 C/C++ native、可省跨语言 codegen) vs 自定义 HTTP/2 帧后，选**自定义帧**——理由：(a) SonnetDB 重负载是**列式时序批与向量 `float[]`**，protobuf 行式 field-tag 编码对其不友好，塞进 `bytes` 字段等于绕过 protobuf（这正是 InfluxDB 用 Arrow Flight 而非裸 gRPC 的原因），自定义帧对列式/向量零拷贝**完全自由**；(b) 维持 **Core 与 Server 双零第三方依赖**的一贯约束；(c) 代价是跨语言客户端需自写（#241 保留），但本仓已有 C ABI 连接器底座可承接。
>
> **设计原则**：
>
> 1. **复用引擎与传输已验证的机制**。MQ 组提交借鉴 `WalGroupCommitCoordinator` 窗口化批量 fsync；冷数据下沉借鉴 segment reader 按需读取；推送/流式借鉴既有 `SseEndpointHandler` 并走 **HTTP/2 流**（多路复用、流控、TLS 由 Kestrel 提供，不自造）。不发明新原语、不重造传输层能力。
> 2. **Core 纯 C# + BCL 零第三方依赖不变；传输承载于 Kestrel HTTP/2**。帧编解码用 `System.IO.Pipelines` / `System.Buffers`；向量化 I/O 用 `System.IO.RandomAccess`；进程内解耦/背压用 `System.Threading.Channels`。二进制帧作为 HTTP/2 请求/响应体或长生命周期 HTTP/2 流传输，鉴权/路由/TLS/多路复用复用 Kestrel。**不引入 gRPC / 裸 TCP / AMQP / 第三方 MQ 运行时**；**MQTT 设备接入允许在 Server 层引入成熟托管库（如 MQTTnet）**——QoS/retain/will/session 协议细节多、自造不划算，且 `SonnetDB.Core` 仍保持零依赖。
> 3. **通用帧、多路复用、payload 自由**。二进制帧头携带 `service`（mq/tsdb/sql/vector/kv/object/doc）+ `op` + `stream-id`（一条 HTTP/2 连接多请求交错）+ 长度；帧体承载各 service 自定义的列式/二进制编码（时序列式批、向量 `float[]` `MemoryMarshal`、SQL 列式结果块），零 JSON/Base64。帧协议一次设计，各模型逐 PR 挂载 opcode。
> 4. **契约新增而非替换**。二进制帧、MQTT 都是**并列新增**，所有现有 REST/JSON 端点保留向后兼容；选型交由客户端按 `docs/` 矩阵决定。契约在 CHANGELOG + `docs/` 标注「新增」。
> 5. **先基准后优化**。每段第一件事是建立吞吐/延迟/编码开销基准（P5a #230 已完成 MQ 基线），用数字驱动，避免拍脑袋优化。

**P5a — MQ 热路径硬化**

| PR | 标题与范围 | 关联发现 | 状态 |
|----|------------|----------|------|
| #230 | **MQ 吞吐/延迟基准基线**：在 `tests/SonnetDB.Benchmarks` 新增 `MqThroughputBenchmark`（单/多 topic publish、批量 vs 单条、pull+ack 回环、不同 payload 尺寸）与 `MqLatencyBenchmark`（P50/P99 publish 延迟随 `FlushOnPublish`/`SyncOnPublish` 变化）。用既有 `[Config]`+`RunStrategy.Monitoring` 骨架，产出报告数字，作为 P5a 后续每项的验收对照。**先建基准再改代码**。 | MQ0 | ✅（`MqThroughputBenchmark` 用 `RunStrategy.Monitoring`+`[Params]` 覆盖单/多 topic(1/8)、`Publish` 单条 vs `PublishMany` 批量、pull+ack 回环、64B/1KB/16KB payload；`MqLatencyBenchmark` 为独立 runner（`--mq-latency`）采样 publish 尾延迟输出 P50/P90/P99/P99.9/max，对比 no-flush/os-flush/fsync-durable 三档。基线数字：fsync-durable P50≈367µs vs os-flush≈5.6µs（约 65× 惩罚，量化 #233 组提交目标）；os-flush P99≈34µs vs no-flush≈8µs（量化 MQ4 每消息 flush 成本）。两基准编译零警告、实测可运行） |
| #231 | **MQ 去全局锁：per-topic 锁分片**：`SonnetMqStore._sync` 单锁串行化所有 topic 的 Publish/Pull/Ack/Stats/Trim。改为顶层 `ConcurrentDictionary<string, TopicState>` 查找 + 每个 `TopicState` 自持一把锁（Kafka partition 思路），topic 间发布/拉取互不阻塞；retention worker 只锁被裁剪的单个 topic。保留单 topic 内 publish 顺序与 offset 单调。 | MQ1、MQ7 | ✅（`_topics` 改 `ConcurrentDictionary` 无锁查找 + `GetOrAdd` 原子建 topic；每个 `TopicState` 携 `SyncRoot`，Publish/Pull/Ack/Tombstone/Stats 均先无锁取 state 再 `lock(state.SyncRoot)`，topic 间零阻塞；`TrimRetention`/retention worker 逐 topic 各自锁，文件系统调用不再全线阻塞（MQ7）。单 topic 内同一 `SyncRoot` 串行 → offset 单调/顺序不变。**SingleFile 模式**所有 topic 共享底层 `FileStream`，`SyncRoot` 回退全局锁保证流写入串行；`Dispose`/`Flush` 相应逐 topic 锁。新增同 topic 并发连续唯一 offset + 跨 topic 独立 offset 两项并发测试；Core 全量 2412 + CrashTests 8 全绿） |
| #232 | **MQ 写路径零冗余拷贝 + 向量化 I/O**：单条 `Publish` 当前把 payload `ToArray()` 两次（封 entry + PublishMany）、无 header 也 `new Dictionary`；`WriteRecord` 分 4 次 `stream.Write`。改为 payload 单次拷贝入段、空 header 走 `EmptyHeaders.Instance` 免分配、header 编码走 `ArrayBufferWriter`/`Base64.EncodeToUtf8` 免 `StringBuilder`+LINQ `OrderBy`；记录帧用 `RandomAccess.Write(handle, IReadOnlyList<ReadOnlyMemory<byte>>)` 一次 scatter/gather 写完头/topic/meta/payload。 | MQ2、MQ5、MQ6 | ✅（`Publish`/`PublishMany` 共用 `PublishPrepared`，payload 仅入常驻消息拷贝一次；空 header 复用 `EmptyHeaders.Instance`（MQ2）。`WriteRecord` 由 4 写改为「定长头+topic+meta 合并进 `ArrayPool` 前缀缓冲一次写 + payload 直写」2 写（MQ5）。`EncodeHeaders` 改 `Array.Sort(CompareOrdinal)`+`ArrayBufferWriter<byte>`+`Base64.EncodeToUtf8`，弃 `StringBuilder`/LINQ（MQ6）。**取舍**：scatter/gather 原拟用 `RandomAccess.Write`，但写入器是带 128KB 缓冲的 `FileStream`（同 `WalWriter`「组装单缓冲再写 BufferedStream」范式），#233 组提交建立其上——裸句柄 `RandomAccess` 会绕过/失步 FileStream 缓冲、反拖慢小消息热路径，故按 house 风格合并缓冲写。段帧二进制布局不变；新增 header（空值/unicode/乱序 key）编码→落盘→重启 replay→解码往返测试。Core 2413 + CrashTests 8 全绿） |
| #233 | **MQ group-commit 组提交**：`FlushOnPublish=true` 默认导致每条消息一次 `Flush` 系统调用，抵消 128KB BufferedStream 的批量意义。引入借鉴 `WalGroupCommitCoordinator` 的窗口化批量刷盘协调器：并发 publish 合并到一个 flush 窗口，`fsync` 在段锁外执行；新增批量 publish 入口（当前 REST 端点只调单条 `Publish`）。默认持久性语义（窗口大小、丢窗口风险）三处文档化，比照 #196。 | MQ4 | ✅（两部分。**批量入口**：新增 `POST .../mq/{topic}/publish-batch` + `SndbMqClient.PublishManyAsync`，复用 `PublishMany`「批末仅刷盘一次」。**组提交**：新增 `GroupCommitPublish`（默认开）leader-flush——各 publish 在 topic `SyncRoot` 内追加+推进 `AppendedSeq`，`SyncRoot` 外经 `FlushRoot` 选举 leader，leader 仅刷盘瞬间借回 `SyncRoot`（FileStream 非线程安全），一次刷盘覆盖此刻全部记录并写 `FlushedSeq`；已覆盖的并发发布者跳过自刷。**取舍（偏离 ROADMAP 原文）**：未照搬 WAL 的「定时窗口」——那为 `SyncWalOnEveryWrite`（fsync≈367µs、2ms 窗可摊薄）设计，而 MQ 默认 os-flush≈5.6µs（#230 基线），定时窗会拖慢默认路径约两个数量级；改用**无定时** leader-flush，合并窗口=一次刷盘在途时长本身，单发布者延迟不变，仅争用下减刷盘次数。持久性不变（跨段滚动旧段 Dispose 前先 fsync）；单文件/`GroupCommitPublish=false` 回退逐条刷盘。文档化三处：`SonnetMqOptions.GroupCommitPublish` XML 注释 + CHANGELOG + 本行。新增并发 durable 重启不丢/跨段滚动不丢/关组提交仍持久 3 测试。Core 2417 + CrashTests 8 + Server 225 全绿） |
| #234 | **MQ 冷数据下沉，修复无界内存**：`TopicState.Messages` 全量常驻 + `PullFromState` 只读内存、段文件从不被读、`SegmentCacheSize` 未实现——高吞吐长期运行 OOM。落地按需段读取：内存只保留「热尾部」+ offset 稀疏索引，Pull 冷 offset 时经 `RandomAccess`/`MemoryMappedFile` 从段文件读并走有界 LRU（`SegmentCacheSize`），比照引擎 segment reader 有界缓存。 | MQ3 | ✅（目录模式改「有界热尾 + 冷数据按需读盘」：新增 `HotTailMaxBytes`（默认 64 MiB）超限从头驱逐最老消息；offset 稀疏索引升级为位置索引（offset → 段 baseOffset + 段内字节位置，publish/replay 均按 stride 采样、每段首条必采）；Pull 命中热尾走原内存路径零回归，冷 offset 二分位置索引取锚点、`RandomAccess` 顺序解码跳到目标连续读，跨段续读、抵热尾边界无缝转内存；只读 `SafeFileHandle` LRU 落地 `SegmentCacheSize`（活跃写段不入缓存、retention 删段时失效）；replay 同样施加热尾上限，大积压重启后内存亦有界。**取舍**：未用 `MemoryMappedFile`——`RandomAccess` 足够且免 Windows 文件锁/生命周期复杂度；冷读前 `Flush(false)` 把写缓冲推到页缓存保证已驱逐记录可见。唯一语义变更 = `RetentionMaxAge` 按段粒度（整段最新记录超龄且非活跃才裁，同 `RetentionMaxBytes`/Kafka；免每条时间戳常驻）；`MessageCount` 明确为未裁剪数。段格式不变；单文件模式/legacy 日志保持全驻不驱逐。新增冷读正确性/跨冷热边界/LRU 压测/重启 replay/按段 age 五测试。**P5a 收官，SonnetMQ 不再携带 🔴 Critical**） |

**P5b — 全模型高吞吐接入（自定义二进制帧 over HTTP/2 + MQTT 设备接入）**

> 传输统一为**自定义二进制帧承载于 Kestrel HTTP/2**（复用鉴权/路由/TLS/多路复用/流控），不做裸 TCP、不引入 gRPC；帧体各 service 自定义列式/二进制编码，零 JSON/Base64。IoT 设备侧另开 **MQTT 内建 broker**（服务端形态，用 IoTSharp/MQTTnet.AspNetCore.Routing，Server 层托管、Core 仍零依赖；topic `db/{db}/m/{measurement}`、payload=measurement 内容复用 BulkIngest 三格式）。

| PR | 标题与范围 | 关联发现 | 状态 |
|----|------------|----------|------|
| #235 | **通用二进制帧协议 + 编解码 + MQ service + 编码基准**：定义 length-prefixed 二进制帧（`System.IO.Pipelines` + `System.Buffers`），帧头含 `service`(mq/tsdb/sql/vector/kv/object/doc) + `op` + `stream-id`(多路复用) + `flags` + 长度；帧体走各 service 自定义二进制编码，零 Base64。承载于 Kestrel HTTP/2 端点（`application/x-sonnetdb-frame`），复用现有 Bearer + 三角色鉴权与路由。**首个 service 落 MQ** 的 publish/pull/ack opcode。基准对比帧 vs JSON+Base64 的体积与 CPU（扩展 #230 的 MQ 基准）。REST 全保留。 | N1、N2 | ✅（Core 新增 `SonnetDB.Protocol`（纯 BCL）：12 字节 LE 帧头（u32 len ≤132MiB 先于分配校验 / u8 ver=1 / u8 service 七编号全保留 / u8 op / u8 flags bit0=Response bit1=Error 保留位 MBZ / u32 streamId 回显），基元 varuint(LEB128)/varstr/bytes（SpanWriter/Reader 新增 `WriteVarString`/`ReadVarString`+`Measure*`）；`FrameCodec.TryReadFrame` 基于 `ReadOnlySequence` 增量解析——**#236 长流复用同一循环**；`MqFrameCodec` 落 publish/publish-batch/pull/ack 四 opcode，payload 零 Base64、解码零拷贝视图直通 `Publish(ReadOnlySpan)`，防御上限名字≤512B/header≤1024 个/headers≤64KiB。Server `POST /v1/frame`：`PipeReader` 增量解析（内存上界=单帧）、逐帧鉴权分发流式回帧、豁免 30MB 请求体限制；错误模型「未成帧走 HTTP 400/415/401，成帧后一切按帧回错误帧（code 复用 REST 词汇+bad_frame/unsupported_*），批内失败隔离」；`TryResolveMqAsync` 判定核心抽 `EvaluateMqAccess` 供两条传输共用（REST 零行为变化）。**新增 h2c 口 5081**（明文无法同口协商 h1/h2 故单独 `Protocols: Http2` 端点），/v1/frame 同时在 5080 HTTP/1.1 可达。**取舍**：本 PR 非双工请求-响应（1..N 帧一体），双工推送归 #236；MQ browse/stats 不进帧（管理面走 REST #245 契约）；codec 编码用 `IBufferWriter` 而非给 Core 加 Pipelines 包依赖。基准数字（`FrameEncodingBenchmark`）：体积 publish 16KiB 帧 16 459B vs JSON 21 921B（1.33×）、pull100×64B 帧 11.4KB vs 24.3KB（2.13×）；CPU publish 16KiB 编码 5×/解码 60× 快，pull100×16KiB 编码 12× 快且分配 16.8KB vs 2.2MB 零 LOH。协议文档 `docs/frame-protocol.md`。Core 2478 + Server 242（含 14 新帧测试：h2c 真 HTTP/2、跨协议等价、40MB 大体、混合成败）+ CrashTests 8 全绿） |
| #236 | **HTTP/2 流式推送订阅（MQ）**：MQ 消费从**轮询**升级为基于 HTTP/2 长生命周期流的**服务端推送**——新消息到达即经帧投递，比照既有 `SseEndpointHandler` 但走二进制帧；服务端用 `System.Threading.Channels` 做 producer/consumer 解耦与背压，`stream-id` 支持一条连接多订阅交错。复用 Kestrel HTTP/2 流控，不自造连接管理。 | N3 | ✅（Core `SonnetMqStore.WaitForMessagesAsync` per-topic pulse `TaskCompletionSource`（无订阅零开销、`SyncRoot` 内查条件取 pulse 杜绝丢唤醒、有效起点前移穿越 retention gap、Dispose 故障等待者）；`MqFrameOp` += Subscribe=5/Unsubscribe=6，`FrameFlags` += Push=4（独立于 Response），`EncodePullResponse` 抽 `EncodeMessagesFrame` 供 `EncodePushFrame` 复用（推送帧布局同 pull 响应）。Server `POST /v1/frame/stream`（仅 HTTP/2）：reader 循环复用 #235 `TryReadFrame`，控制帧 op1~4 语义不变、订阅帧 pump 经 `WaitForMessagesAsync`→`Pull(offset)`→推送；单写者独占 `PipeWriter` + 有界 Wait channel 解耦，HTTP/2 流控经 `FlushAsync` 反压不丢消息；清 `MinRequestBodyDataRate` 免误杀；动态用户逐批复查权限（SSE parity）；组模式推送不进组位点、流上 ack 显式确认、重连续传（至少一次）；单连接订阅上限 64；有序 teardown 无死锁。**取舍**：客户端帧贯通归 #241 不动 `SndbMqClient`；双工测试自写 `PushStreamContent`——关键 gotcha：不能 await 整个 `SendAsync`，否则 full-duplex 下请求头随首字节冲刷、空 body 阻塞导致响应头永不到达（惰性解析响应）。测试 Core 5 + codec 6 + Server 7 双工 h2c 全绿；`docs/frame-protocol.md` 补 op5/6+Push+流端点章。） |
| #237 | **时序列式批量写接入帧协议**：为帧加 `tsdb` service 的 `ingest`/`write-many` opcode，measurement 批量写以**列式紧凑二进制**直传（对齐 IoTDB Tablet / PG COPY BINARY / InfluxDB Line Protocol 的列式批思路，非行式 JSON），避免大批量 JSON 序列化；复用 P0/P2 已硬化的 `WriteMany` 背压路径。基准对比列式帧 ingest vs JSON / InfluxLine 吞吐。 | N5 | ✅（Core `TsdbFrameCodec`（service=2、op=1 write-columnar）：帧体 = db+measurement+flushMode(u8 对应 REST `?flush` 三档)+块序列，每块 = tag 组（同一序列族）+ 时间戳列（i64 LE 定宽，`MemoryMarshal` 整段直传）+ 字段列（类型+稀疏标志+可选 presence 位图+紧凑值序列；全部六种 `FieldType` 含 Vector f32×dim/GeoPoint）；`TsdbColumnarBlock`/`TsdbColumnarColumn` 编码模型零装箱。解码走 `TsdbColumnarPointReader`（`IPointReader`）**流式列转行**直通 `BulkIngestor`→`WriteMany`（与 REST lp/json/bulk 完全同一引擎入口）；名称防御按块整体校验一次，行数/列数/值长度先于分配校验。Server 信封校验按 service 分派，tsdb 走新抽的 `EvaluateDatabaseAccess`（语义同 REST）要求 Write 权限，行级/schema 错误映射 `bulk_ingest_error`（与 REST bulk 同码），计数进 `ServerMetrics`。**基准数字**（`ColumnarIngestBenchmark`，2 字段×100k 行）：wire 帧 240KB vs JSON 897KB（3.73×）vs LP 676KB（2.82×）；编码帧 91µs vs JSON 29.8ms（326×）且分配 368B vs 46MB；解析→Point 帧 11.9ms vs JSON 53.1ms（4.5×）。测试 Core codec 13 + Server 端到端 7（含跨协议数据等价、混合批隔离、schema 冲突）全绿；REST 批量端点全保留） |
| #238 | **SQL/关系查询流式结果集**：为帧加 `sql` service 的 `query` opcode，大结果集经 HTTP/2 流 + `System.IO.Pipelines` **列式二进制分块流式**回传（不先全量物化 JSON），复用 P3 #220 流式合并成果；`stream-id` 支持一条连接并发多查询。基准对比列式二进制流 vs JSON 数组的体积/延迟/峰值内存。 | N6 | ✅（Core `SqlFrameCodec`（service=3、op=1 query）：请求 = db+sql(≤1MiB)+命名标量参数（null/i64/f64/bool/string，`SqlParameterBinder` 绑定，复用 #213）；响应 = 同 streamId 帧序列 **meta→rows×N→end**，rows 帧按列存储 + 块内类型推断——单一类型列稠密定宽/紧凑编码（u8 kind+可选 null 位图+仅有值行），混合列回退 variant 逐值带标记，**整型/浮点混列不合并保大 long 精度**（对齐 #219 Q15）；值类型九种含 Bytes 零 Base64/Timestamp/Vector f32/GeoPoint；`SelectChunkRowCount` 按行字节估算切块（默认 256KiB/4096 行封顶）。Server `ExecuteSqlQueryAsync`：`EvaluateDatabaseAccess` 要求 Read；**语句门禁**只放行 SELECT/SHOW/DESCRIBE/EXPLAIN（`RequiresWritePermission`/`IsControlPlaneStatement` 与 REST 同一判定，写语句/控制面回 bad_request）；执行同一 `SqlExecutor`（含 #220 流式合并全部能力）；逐块编码逐块 flush——响应缓冲内存上界 = 单块；失败若已发 meta/rows 则同 streamId 追加 `sql_error` 错误帧；指标/慢查询与 REST 同源。**取舍**：引擎 `SelectExecutionResult` 契约是同步物化行集合，本 PR 流式化的是**编码与传输侧**（分块把峰值响应缓冲从全量压到单块，客户端增量消费）——执行侧行集合流式化需改全部 executor 契约，不在本 PR；一元端点天然支持一体多查询帧（streamId 隔离）。**基准数字**（`SqlResultEncodingBenchmark`，4 列×100k 行）：wire 帧 3.40MB vs NDJSON 3.97MB（1.17×）；编码帧 9.7ms vs 26.2ms（2.7×）且分配 **2.2KB vs 24MB**（编码零 GC/零 LOH）；解码帧 7.4ms vs 逐行 JsonDocument 21.3ms（2.9×）。测试 Core codec 20 + Server 端到端 11（REST NDJSON 逐行等价、8192 行多 rows 帧、NULL 位图、参数化、门禁、混合批隔离）全绿；REST SQL 端点全保留） |
| #239 | **向量检索接入帧协议**：为帧加 `vector` service 的 `search`/`insert` opcode，向量 `float[]` 以紧凑二进制（`ReadOnlySpan<float>` 直接 `MemoryMarshal`）传输，消灭 JSON 数字文本编码（比 Base64 更浪费）；KNN 结果集走 #238 流式回传。基准对比二进制向量 vs JSON 数字数组的体积与 CPU。 | N7 | ✅（Core `VectorFrameCodec`（service=4、op=1 search）：请求 = db+measurement+column+k+metric(u8 同 SQL knn 词汇)+tag 等值过滤(≤1024)+闭区间时间窗(i64×2)+查询向量（varuint 维度 + f32 LE `MemoryMarshal` 整段直传，维度先于分配校验）；响应 **meta→rows×N→end 与 sql 帧同一块布局**——`SqlFrameCodec` 三个响应帧编码抽 service/op 参数化内核（`Encode{Meta,Rows,End}FrameCore`）供 vector 复用，客户端同一套 sql 块解码器解析，KNN 结果集自动享受 #238 切块逐块 flush；向量字段列 `SqlValueKind.Vector` f32 回传（REST NDJSON 向量列实际降级 ToString，帧是语义正确通道）。**检索内核与 SQL knn TVF 共用**：`ExecuteKnn` 编排抽 `TableValuedFunctionExecutor.ExecuteKnnSearch`（列/维度校验→tag 过滤定位候选→单次读快照 `KnnExecutor`→tag/field 批量回填），两路径同一入口零语义分叉，帧路径额外静态校验 tag 过滤键必须是 TAG 列。**取舍：insert 不设独立 opcode**——#237 tsdb 列式写的 Vector 列已是 f32 二进制直传通道，不重复写入路径。**基准数字**（`VectorSearchEncodingBenchmark`，dim=128/768/1536 × top-100）：wire 请求帧 2.6~2.8× 小（dim=1536：6.2KB vs 17.2KB）、结果集 2.8×（617KB vs 1.72MB）；CPU dim=1536 请求编码 **91ns vs 102µs（~1100×，零分配）**、结果集编码 **20µs vs 11.9ms（~590×，264B vs 1.7MB 零 LOH）**、解码 42µs vs 10.1ms（~240×）。测试 Core codec 15（往返/解码持有型/维度炸弹先于分配/重复 tag key/sql 解码器互通）+ Server e2e 11（含**与 sql knn TVF 帧逐行等价**、tag 过滤/时间窗/L2、错误帧 `vector_search_error` 与 REST 同码、混合批隔离）全绿；REST 向量端点全保留） |
| #240 | **KV / 对象 / 文档接入帧协议**：为帧加 `kv`/`object`/`doc` service 的 get/put/scan opcode，二进制 value / 对象字节 / BSON-like 文档走原始字节零 Base64；对象大 blob 走 #238 的 HTTP/2 流式分块。补齐全模型二进制覆盖。 | N8 | ✅（Core 新增 `KvFrameCodec`（get/put/scan，key/value 原始字节直传）、`ObjectFrameCodec`（get 流式 meta→data×N→end 分块复用 #238 思路默认 256KiB/块 + put，内容零 Base64）、`DocFrameCodec`（find ID/扫描 + insert，JSON 原始 UTF-8 直传零信封）三 codec（纯 BCL）+ `KvFrameOp`/`ObjectFrameOp`/`DocFrameOp`；七 service（mq/tsdb/sql/vector/kv/object/doc）全部就位。Server `FrameEndpointHandler` 挂载三 service 分派——kv/doc 同步 `ExecuteKvOp`/`ExecuteDocOp`（同 REST 引擎入口 `KvKeyspace`/`DocumentCollectionStore`），object 流式 `ExecuteObjectOpAsync`（get 边读边推、put 零拷贝 `ReadOnlyMemoryStream` 喂 `SndbObjectStore`）；资源级鉴权抽 `EvaluateNamedResourceAccess`（db→存在→资源名→权限，同 REST 判定顺序）供 kv/doc 共用、object 复用 `EvaluateDatabaseAccess`；get/scan/find 需 Read、put/insert 需 Write，集合缺失回 `collection_not_found`、对象引擎异常以自带码（`bucket_not_found`/`object_not_found`）回错误帧。**scope**：KV ttl/incr/cas、对象 bucket 管理/版本/multipart、文档复杂查询/update/delete 不进帧（走 REST/SQL）。测试 Core codec 41 + Server e2e 16（含帧↔REST 等价、600KB 多分块流式、混合批隔离）；`docs/frame-protocol.md` 补三章节 + 错误码；REST 端点全保留） |
| #241 | **客户端 SDK 帧协议贯通**：`SonnetDB.Data` 的 ADO / MQ / 向量 / 文档客户端在检测到服务端支持时优先走二进制帧（HTTP/2），回落 REST/JSON；连接字符串加传输选项（`Protocol=frame-http2` / `rest`）。保持嵌入式路径不变。跨语言（Go/Rust/Java/Python）经既有 C ABI 连接器底座逐步承接帧协议。 | N2 | ✅（新增连接串 `Protocol` 选项（`auto`/`frame-http2`/`rest`）+ 共享 `Remote/FrameChannel`（三态惰性探测：传输级失败缓存回落 REST，200+可解析帧缓存走帧、带内错误帧转 `SndbServerException`，`frame-http2` 传输失败不静默回落；一元 POST 回落安全不重复写入）。MQ publish/batch/pull/ack、KV get/set/scan（命名空间限定 key 字节 + scan 剥前缀）、文档 insert/findOne/单页非高级 find、ADO 只读 SQL（`SqlParser` 分类 SELECT/SHOW-数据面/DESCRIBE/EXPLAIN）走帧，其余回落 REST；**向量经 ADO SELECT vector_search 传递性走 sql service，不引入独立客户端**。**记录在案差异**：ADO SQL 帧路径以帧类型为准（时间戳 `DateTime`/blob `byte[]`/向量 `float[]` vs REST 字符串），MQ/KV/文档两传输字节一致。测试 `FrameChannelTests` 5 + `FrameTransportParityTests` 12（frame-http2 vs rest 等价 + 不支持 op 回落 + DATETIME 富类型差异固化）。跨语言 C ABI 承接留后续。） |
| #242 | **MQTT 内建 broker（设备直连落库/订阅推送）**：Server 层内建 MQTT **broker**（服务端形态，对标 IoTDB / TDengine 设备直连），采用 IoTSharp 自家 **[MQTTnet.AspNetCore.Routing](https://github.com/IoTSharp/MQTTnet.AspNetCore.Routing)**（MVC 风格 topic 路由，`SonnetDB.Core` 仍零依赖）。**topic 模板 `db/{db}/m/{measurement}`** 把 `PUBLISH` 路由到 database + measurement；**payload = measurement 内容**，复用现有 `BulkIngestEndpointHandler` 三格式（Line Protocol / JSON points / BulkValues）落库，**零重复落库逻辑**；设备 `SUBSCRIBE` 复用 #236 推送管线。MQTT 鉴权复用现有 Bearer/三角色权限模型（username/password 或 token 映射 database 权限）。范围：**单机内建 broker**，QoS 0/1、retain、LWT 支持范围在 `docs/` 明确；**不做 broker 集群 / 桥接 / 跨节点 session**（与 P5「不做分布式」边界一致）。 | N9 | 📋 |
| #243 | **MQTT client 订阅外部 broker（接入已有 EMQX/Mosquitto 基础设施）**：Server 作为 MQTT **client** 主动连接并 `SUBSCRIBE` 已有外部 broker，把消息拉入 SonnetDB 落库——同样用 **MQTTnet.AspNetCore.Routing** 的 topic 路由抽象（与 #242 broker 共享同一套 `[MqttRoute]` controller 与 `db/{db}/m/{measurement}` → `BulkIngestEndpointHandler` 落库逻辑，仅消息来源从内建 broker 换成外部 broker 的订阅回调）。配置外部 broker 地址/凭证/订阅 topic 过滤器与重连策略；与 #242 内建 broker 可同时启用（本机既是 broker 又订阅上游）。对标 InfluxDB+Telegraf 的 client 订阅范式。 | N10 | 📋 |
| #244 | **全模型接入收口 + 文档 + parity**：汇总 #230/#235/#237~#239 基准的吞吐/延迟/体积对照进报告；补 `docs/` 接入协议章节（帧格式、service/op 矩阵、REST vs 帧-HTTP2 选型矩阵、推送订阅与流式结果集用法、MQTT broker/client 两形态 topic 映射规则与 QoS 范围）；`tests/SonnetDB.Parity` 补各 service 二进制帧与 REST 的等价性平移测试，确保两条路径语义一致。 | MQ0、N1~N10 | 📋 |

### 推进顺序

```text
P0 止血：#189（Win 目录 fsync + 顺序）→ #190（add-then-reset）→ #191（worker 租约 + 串行锁）
        → #192（FTS manifest 原子）→ #193（HNSW 快照跳 tombstone）→ #194（Delete 持久化）
        → #195（段头尾 CRC）→ #196（默认持久性决策 + 文档）
P1 正确：#197（NULL 三值）→ #198（count(*)）→ #199（事务覆盖）→ #200（解析递归上限）
        → #201（worker 兜底）→ #202（批内 backpressure）→ #203（fsync 移出锁 + 排空）
P2 吞吐：#204（MemTable 双缓冲）→ #205（去锁内分配/装箱）→ #206（写路径同步精简）
        → #207（增量段索引）→ #208（tombstone 免拷贝）→ #209（catalog 防抖）
        → #210（manifest 修剪）→ #211（孤儿清理 + WAL footer 不变式）
P3 查询：#212（plan cache）→ #213（参数化）→ #214（LIMIT 下推）→ #215（hash join）
        → #216（子查询去关联）→ #217（时序 WHERE 字段/OR）→ #218（事务隔离）
        → #219（DISTINCT/大小写/DELETE/聚合类型）→ #220（流式合并）
P4 索引：#221（文档惰性 scan）→ #222（FTS 批量成段）→ #223（向量度量/efConstruction）
        → #224（KNN skip-index）→ #225（compaction 向量 catalog）→ #226（HNSW ef/回收）
        → #227（文档 ANN）→ #228（删遗留 HNSW）→ #229（文档索引原子性）
P5a MQ：#230（MQ 基准基线）→ #231（去全局锁 per-topic 分片）→ #232（写路径零拷贝 + RandomAccess）
        → #233（group-commit 组提交 + 批量入口）→ #234（冷数据下沉修无界内存）
P5b 接入：#235（通用二进制帧 + MQ service / HTTP-2）→ #236（HTTP-2 流式推送订阅）→ #237（时序列式批量写）
        → #238（SQL 流式结果集）→ #239（向量检索接入）→ #240（KV/对象/文档接入）
        → #241（客户端 SDK 帧贯通）→ #242（MQTT 内建 broker）→ #243（MQTT client 订阅外部 broker）
        → #244（全模型收口 + 文档 + parity）
```

> **阶段间可并行度**：P0 内 #189~#196 相互独立，可并行推进但建议 #189/#190/#191 最先（数据安全影响面最大）。P1~P4 各阶段建议顺序推进，但 P3/P4 的能力增强类 PR 与 P2 吞吐类 PR 之间无强依赖，可按团队带宽穿插。P5（#230~#244）独立于 P0~P4；内部约束：**P5a（#230~#234）纯 Core MQ 硬化与 P5b 接入层解耦，可并行**；P5a 内 #230 基准必须最先；P5b 内 **#235 通用帧编解码是 #236~#240 所有 service 接入的前置（#235 已 ✅，前置解除）**，各 service opcode（#237~#240）之间无强依赖可穿插，#236 推送订阅依赖 #235（复用其 `TryReadFrame` 增量解析循环，响应侧换 `Channels`），#241 SDK 贯通需至少一个 service 落地后，#242 内建 broker 可与 #237~#241 并行（复用 #236 推送管线），#243 MQTT client 订阅与 #242 共享路由 controller、宜紧随 #242，#244 收口最后。

### 缺陷完整附录（54 项，确保无遗漏）

> 编号规则：**S**=存储/持久化（13）、**I**=索引（15）、**Q**=SQL 引擎（15）、**C**=并发/性能（11）。严重度：🔴 Critical（丢数据/损坏/崩溃）、🟠 High、🟡 Medium、⚪ Low。"—" 表示该发现已并入同一 PR。

| 编号 | 严重度 | 位置 | 缺陷摘要 | 修复 PR |
|------|--------|------|----------|---------|
| S1 | 🔴 | `Engine/TsdbOptions.cs:30`、`Wal/WalWriter.cs:286` | `SyncWalOnEveryWrite=false` 默认，append 只入 BufferedStream 未交 OS，进程 crash 丢一个 flush 窗口的已确认写 | #196 |
| S2 | 🔴 | `Engine/FlushCoordinator.cs:83`、`Wal/WalCheckpointFile.cs:144` | Windows 目录 fsync 空操作，segment 落盘早于 WAL 回收的顺序不受保护，掉电永久丢数据 | #189 |
| S3 | 🔴 | `Engine/FlushCoordinator.cs:110`、`Engine/Tsdb.cs:807` | Flush 先 Reset MemTable 再发布 segment，窗口内并发查询丢数据 | #190 |
| S4 | 🟠 | `Engine/Tsdb.cs:441` | Delete 默认非持久，已持久化数据被删后崩溃恢复复活 | #194 |
| S5 | 🟠 | `Engine/Compaction/CompactionWorker.cs:113`、`Engine/Retention/RetentionWorker.cs:83` | Compaction/Retention 不持 reader 租约直接读 readers → use-after-dispose | #191 |
| S6 | 🟠 | `Catalog/CatalogFileCodec.cs:50`、`Engine/SegmentReplacementManifest.cs:328` | catalog/tombstone/replacement/checkpoint 原子改名从不做目录 fsync（Windows） | #189 |
| S7 | 🟡 | `Engine/SegmentReplacementManifest.cs:130`、`Engine/SegmentManager.cs:52` | replacement manifest 无限增长，启动 O(N) 重开 reader、每 compaction O(N) 重写 → O(N²) | #210 |
| S8 | 🟡 | `Storage/Format/SegmentFooter.cs:57`、`SegmentHeader.cs:102` | 段头/尾无自校验 CRC，位翻转静默错定位索引 | #195 |
| S9 | 🟡 | `Engine/Retention/RetentionWorker.cs:78` | Retention plan→drop 与 compaction 非原子，phantom id 污染 manifest | #191 |
| S10 | 🟡 | `Engine/WalGroupCommitCoordinator.cs:28`、`Engine/Tsdb.cs:322` | group-commit window=0/禁用时在 `_writeSync` 内 fsync，串行化所有写入者 | #203 |
| S11 | ⚪ | `Engine/WalGroupCommitCoordinator.cs:81` | 关闭时延迟 `Sync()` 在 WAL dispose 后抛 ODE 到已返回的 Write 调用方 | #203 |
| S12 | ⚪ | `Engine/Tsdb.cs:950`、`Engine/Compaction/CompactionWorker.cs:173` | 旧文件删除吞异常，孤儿 segment/索引文件累积泄漏磁盘 | #211 |
| S13 | ⚪ | `Wal/WalWriter.cs:353` | `WriteLastLsnFooterIfDirty` 依赖隐式缓冲清空不变式，脆弱 | #211 |
| I1 | 🔴 | `FullText/Storage/ManifestFile.cs:64` | FTS manifest delete-then-move，崩溃丢整个全文索引且重启静默建空 | #192 |
| I2 | ✅ | `Documents/DocumentQueryPlanner.cs:260` | 每次文档查询强制全集合 `Scan()` 反序列化，即便选了索引路径 | #221 |
| I3 | ✅ | `FullText/Storage/PersistentFullTextIndex.cs:77` | 每文档一个单文档 segment + 全量改写 manifest（O(N²）），查询遍历所有 segment | #222 |
| I4 | 🟠 | `Vector/Index/Hnsw/HnswIndex.cs:393` | HNSW 删除后重插同 key，快照往返 `_keyToRow.Add` 重复键异常 → 索引不可加载 | #193 |
| I5 | 🟠 | `Catalog/TagInvertedIndex.cs:148`、`Catalog/SeriesCatalog.cs:213` | 每新增 series 全量重建 Frozen 结构，高基数 ingest O(N²) | #209 |
| I6 | 🟡 | `Vector/Index/Hnsw/HnswIndex.cs:270` | HNSW 不为 tombstone 放大 ef，有删除时欠返回 topK | #226 |
| I7 | 🟡 | `Storage/Segments/VectorIndexAdapter.cs:141`、`Query/KnnExecutor.cs:199` | 非 cosine 向量索引按 cosine 建且 ANN gate 仅 cosine，白占空间不加速 | #223 |
| I8 | 🟡 | `Query/KnnExecutor.cs:186` | 向量 KNN 不用 block skip-index，每 series 全块扫 | #224 |
| I9 | 🟡 | `Storage/Segments/VectorIndexAdapter.cs:179` | efConstruction 被 search ef 绑死，低 search-ef 永久烤进低质量图 | #223 |
| I10 | 🟡(疑似) | `Documents/DocumentQueryPlanner.cs:31` | 文档索引若"欠包含"（写入未原子/崩溃未重建）静默漏行；需先验证维护路径 | #229 |
| I11 | 🟡 | `Engine/Compaction/SegmentCompactor.cs:86`、`Storage/Segments/SegmentWriter.cs:417` | compaction 向量索引仅在两个 catalog 都提供时构建，否则静默退化暴力扫 | #225 |
| I12 | 🟡 | `Sql/Execution/DocumentVectorSearchExecutor.cs:97` | 文档 `vector_search` 全表暴力 + 每行 JSON parse，O(N·dim) | #227 |
| I13 | ⚪ | `Storage/Segments/HnswVectorBlockIndex.cs:696` | 遗留死代码 HNSW，图质量差 O(n·ef²) 建图，误用风险 | #228 |
| I14 | ⚪ | `Vector/Index/Hnsw/HnswIndex.cs:222` | HNSW tombstone-only 删除从不回收内存，`_entryPoint` 不重指 | #226 |
| I15 | ⚪ | `FullText/Storage/SegmentFile.cs:79` | FTS segment/manifest 写入无 fsync，掉电不保证持久 | #192 |
| Q1 | 🔴 | `Sql/Execution/TableSqlExecutor.cs:986`（RelationalSelect/Join 同型） | 三值逻辑坏：`NULL != 5` 判 TRUE、`NULL = NULL` 判 TRUE，返回错误行 | #197 |
| Q2 | 🔴 | `Sql/Execution/SqlExecutor.cs:678` | 事务不覆盖 measurement/document 写，ROLLBACK 仍持久保留 | #199 |
| Q3 | 🔴 | `Sql/SqlParser.cs:1612`（ParseNot/ParseUnary 同型） | 解析器递归无深度限制，深层括号/NOT 链触发 StackOverflow 崩进程 | #200 |
| Q4 | ✅ | `Sql/Execution/SqlExecutor.cs:80`、`TableSqlExecutor.cs:588` | 事务内无隔离/无 read-your-writes，看不到自身缓冲写 | #218 |
| Q5 | ✅ | `Sql/Execution/WhereClauseDecomposer.cs:70` | 时序 WHERE 不能按字段值过滤、不支持 OR | #217 |
| Q6 | 🟠 | `Sql/Execution/SelectExecutor.cs:274`、`TableSqlExecutor.cs:1250` | LIMIT 不下推，先全量物化+排序再切片 | #214 |
| Q7 | 🟠 | `Sql/Execution/SqlExecutor.cs:64` | 无 plan/parse 缓存，每次 Execute 重新 lex+parse | #212 |
| Q8 | 🟠 | `Sql/Execution/RelationalSelectExecutor.cs:679/811/832` | 相关子查询/EXISTS/IN 每外层行重扫内表 O(n_outer×n_inner) | #216 |
| Q9 | 🟠 | `Sql/Execution/RelationalSelectExecutor.cs:110` | 关系 JOIN 全物化嵌套循环笛卡尔积，无 hash join | #215 |
| Q10 | 🟡 | 整个 SQL 入口（`SqlExecutor.cs:38`） | 无参数化/绑定变量，应用被迫拼字符串 → 注入回到应用层 | #213 |
| Q11 | ✅ | `Sql/SqlParser.cs:1249` | `DISTINCT` 非关键字，`SELECT DISTINCT x` 静默误解析为列别名 | #219 |
| Q12 | ✅ | `Sql/Execution/RelationalSelectExecutor.cs:856` | 关系/JOIN 标量求值 Ordinal 大小写敏感，与 projection 不一致 | #219 |
| Q13 | ✅ | `Sql/Execution/DeleteExecutor.cs:26` | 时序 DELETE 无字段定向，无差别删所有字段 | #219 |
| Q14 | 🟡 | `Sql/Execution/SelectExecutor.cs:983` | `count(*)` 数 field-value 非行，3 字段返回 3N | #198 |
| Q15 | ✅ | `Sql/Execution/RelationalSelectExecutor.cs:290` | 聚合类型判定额外全量预扫 + `Convert.ToDouble` 混淆整型/浮点、丢 long 精度 | #219 |
| C1 | 🟠 | `Engine/Compaction/CompactionWorker.cs:113`、`Tsdb.cs:883` | 维护 worker 绕过 reader 租约 → use-after-dispose；无串行锁致 retention 被 compaction 撤销（数据复活）；DropMeasurement 无 try/catch | #191 |
| C2 | 🟠 | `Engine/Tsdb.cs:827`、`FlushCoordinator.cs:50` | 整个 segment flush 在全局 `_writeSync` 内，阻塞所有写入者 | #204 |
| C3 | 🟠 | `Engine/Tsdb.cs:976`（859/981/996/1050 装箱） | 锁内每点 `new List(schema.Columns)` + `IReadOnlyDictionary` foreach 装箱枚举器 | #205 |
| C4 | 🟠 | `Engine/Tsdb.cs:402` | `WriteMany(Span)` 整批只在末尾一次 backpressure → OOM 风险 | #202 |
| C5 | 🟡 | `Engine/Tsdb.cs:322`、`WalGroupCommitCoordinator.cs:28` | group-commit 禁用时 fsync 在 `_writeSync` 内（与 S10 同源） | #203 |
| C6 | 🟡 | `Engine/Compaction/CompactionWorker.cs:113` | plan 步骤在 try/catch 外，瞬时抛出致 compaction 后台线程静默死亡 | #201 |
| C7 | 🟡 | `Engine/SegmentManager.cs:241` | 每 flush/compaction 全量重建所有 segment 索引，趋 O(N²) | #207 |
| C8 | 🟡 | `Engine/TombstoneTable.cs:83/100` | `IsCovered`/`GetForSeriesField` 每查询锁内 `ToArray()` | #208 |
| C9 | 🟡 | `Query/QueryEngine.cs:93`、`Storage/Segments/SegmentReader.cs:480` | 大扫描先全量解码进 `List<DataPoint[]>` 再合并，LOH 堆峰值；缓存命中每次整拷贝 | #220 |
| C10 | 🟡 | `Memory/MemTable.cs:58` | 单写者路径冗余 RWLock+ConcurrentDictionary+bucket 锁+多次 Interlocked | #206 |
| C11 | ⚪ | `Engine/KvExpirerWorker.cs:93` | KV expirer 吞异常无诊断事件，反复失败不可见 | #201 |

### P5 消息队列 / 接入协议附录（本轮新增，独立于上表 54 项）

> 编号规则：**MQ**=SonnetMQ 存储/并发/内存、**N**=接入协议/网络传输（覆盖全模型：MQ/时序/关系/向量/KV/对象/文档）。严重度同上。这批是围绕 P5 主题单独走查 MQ 热路径与全模型接入层的发现，不计入原审计 54 项。

| 编号 | 严重度 | 位置 | 缺陷摘要 | 修复 PR |
|------|--------|------|----------|---------|
| MQ0 | 🟡 | `tests/SonnetDB.Benchmarks/` | 无任何 MQ 吞吐/延迟基准，优化无对照基线 | #230 |
| MQ1 | 🔴 | `Mq/SonnetMqStore.cs:23` | 单一全局 `_sync` 锁串行化所有 topic 的 Publish/Pull/Ack/Stats/Trim，零分片并发 | #231 ✅ |
| MQ2 | 🔴 | `Mq/SonnetMqStore.cs:87/115/117` | 单条 Publish 把 payload `ToArray()` 两次；无 header 仍 `new Dictionary` | #232 ✅ |
| MQ3 | 🔴 | `Mq/SonnetMqStore.cs:811/506`、`Mq/SonnetMqOptions.cs:69` | 未裁剪消息全量常驻内存、Pull 从不读段文件、`SegmentCacheSize` 未实现 → 无界内存/OOM | #234 ✅ |
| MQ4 | 🔴 | `Mq/SonnetMqStore.cs:460`、`Program.cs:116` | `FlushOnPublish=true` 默认致每条消息一次 flush 系统调用；HTTP 端点只调单条 Publish | #233 ✅ |
| MQ5 | 🟠 | `Mq/SonnetMqStore.cs:456-459` | `WriteRecord` 分 4 次 `stream.Write`（头/topic/meta/payload），非向量化 | #232 ✅ |
| MQ6 | 🟠 | `Mq/SonnetMqStore.cs:703` | `EncodeHeaders` 每次 `StringBuilder`+LINQ `OrderBy`+逐值 Base64 | #232 ✅ |
| MQ7 | 🟠 | `Mq/SonnetMqStore.cs:250-257/545/551` | Retention worker 持全局锁做 `File.Exists`/`FileInfo.Length` 文件系统调用，裁剪期全线阻塞 | #231 ✅ |
| N1 | 🔴 | `Contracts/Dtos.cs:265`、`Mq/SndbMqClient.cs` | MQ payload 经 JSON+Base64 编码（+33% 体积 + 发布/拉取各一次 CPU 编解码税） | #235 ✅ |
| N2 | 🟠 | `Endpoints/Routes/MessageQueueEndpoints.cs` | 无 HTTP/2 二进制端点，无多路复用 / 流式帧 | #235 ✅（服务端）+ #241 ✅（客户端 SDK 贯通：`SonnetDB.Data` MQ/KV/文档/ADO SQL 远程优先走帧、回落 REST） |
| N3 | 🟠 | `Endpoints/Routes/MessageQueueEndpoints.cs:61` | 消费纯轮询无推送，未走 HTTP/2 流式推送（可比照 `SseEndpointHandler` 但用二进制帧） | #236 ✅ |
| N5 | 🟠 | `Endpoints/Routes/IngestionEndpoints.cs` | 时序批量写走 HTTP+JSON 行式，非列式二进制批（对标 IoTDB Tablet / PG COPY BINARY） | #237 ✅ |
| N6 | 🟠 | `Endpoints/Routes/SqlEndpoints.cs`、`Json/NdjsonRowWriter.cs` | SQL/关系结果集全量物化 JSON 回传，大结果集序列化瓶颈、无流式 | #238 ✅ |
| N7 | 🔴 | `Endpoints/Routes/*`（向量 query/insert） | 向量 `float[]` 经 JSON 数字文本编解码，比 Base64 更浪费（每 float 变文本） | #239 ✅（search 请求/结果集 f32 二进制；插入侧由 #237 tsdb 列式写 Vector 列覆盖） |
| N8 | 🟡 | `Endpoints/Routes/KeyValueEndpoints.cs`、`ObjectStorageEndpoints.cs`、`DocumentEndpoints.cs` | KV value / 对象 blob / 文档二进制负载经 JSON+Base64，无原始字节路径 | #240 ✅（kv/object/doc 三 service 帧 opcode，原始字节直传零 Base64；对象 get 流式分块） |
| N9 | 🟠 | `src/SonnetDB/`（无 MQTT broker） | 无内建 MQTT broker，IoT 设备无法直连发布/订阅（对标 IoTDB/TDengine 内建 broker；拟用 IoTSharp/MQTTnet.AspNetCore.Routing，topic `db/{db}/m/{measurement}`，payload 复用 BulkIngest 三格式） | #242 |
| N10 | 🟡 | `src/SonnetDB/`（无 MQTT client） | 无 MQTT client 订阅能力，无法接入已有 EMQX/Mosquitto 基础设施（对标 InfluxDB+Telegraf；同用 MQTTnet.AspNetCore.Routing 路由，与内建 broker 共享落库逻辑） | #243 |

### 验收标准

- **P0**：`tests/SonnetDB.CrashTests/` 新增剧本证明——(a) Windows 掉电后 segment 与 WAL 不会同时丢失已 flush 数据；(b) flush 期间并发查询不返回不完整结果；(c) Compaction+Retention 并发下无 `ObjectDisposedException` 且过期数据不复活；(d) FTS manifest 崩溃后索引可从 segment 重建；(e) HNSW 删除+重插后持久化索引可正常加载；(f) Delete 后崩溃恢复数据不复活；(g) 段头/尾位翻转被 CRC 检出。默认持久性语义在 CHANGELOG + architecture.md + XML 注释三处一致。
- **P1**：每个 SQL 正确性缺陷有"修复前返回错误结果 / 崩溃"的确定性回归用例；`NULL != 0` 不再返回 null 行，`count(*)` 返回行数，`BEGIN…ROLLBACK` 不再持久化时序/文档写，深层嵌套 SQL 抛 `SqlParseException` 而非崩溃；Compaction/KvExpirer 后台异常可在诊断事件观测。
- **P2**：`tests/SonnetDB.Benchmarks` 显示写吞吐在双缓冲后不再随 flush 周期性塌陷；持续 ingest 下 P99 写延迟显著下降；高基数 series（百万级）ingest 不再 O(N²)；`WriteMany` 大批量不 OOM。基准数字进报告不做主 CI gating。
- **P3**：plan cache 命中路径不再重复 parse；参数化查询贯通 lexer→executor；`ORDER BY…LIMIT k` 内存与延迟不随数据量线性增长；等值 JOIN 走 hash；`WHERE temp>30`、`WHERE a OR b` 可用；EF Core 关系查询翻译在这些能力上回归通过。
- **P4**：文档索引点查不再 O(collection)；FTS 批量写不再 N 文件 + O(N²) manifest；声明 L2/IP 的向量索引真正走对应度量的 ANN；文档集合 `vector_search` 有可用加速路径；遗留 `HnswVectorBlockIndex` 移除后测试全绿。
- **P5a（MQ 硬化）**：`tests/SonnetDB.Benchmarks` 显示——多 topic 并发 publish 吞吐随 topic 数近线性扩展（去全局锁后不再互相阻塞）；单条 publish 分配数与拷贝次数下降（零冗余 `ToArray`、空 header 免分配）；group-commit 下持续 publish 的 P99 延迟显著优于每写 flush；长期高吞吐运行内存有界（冷数据下沉，不再 OOM）。MQ 默认持久性语义在 CHANGELOG + `docs/` + XML 注释三处一致。
- **P5b（全模型接入）**：通用二进制帧 over HTTP/2 覆盖 MQ/时序/关系/向量/KV/对象/文档各 service，帧头 `service`+`op`+`stream-id` 多路复用可用；相比 JSON/Base64——MQ/对象 payload 与向量 `float[]` 的线上体积与 CPU 明显下降，时序批量写走列式二进制、大 SQL 结果集走流式二进制不再全量物化；HTTP/2 流式推送订阅端到端延迟低于轮询、支持一条连接并发多请求/多订阅；MQTT **内建 broker** 设备可直连发布落库并订阅推送、**client** 可订阅外部 EMQX/Mosquitto 拉数落库（两形态共享 `db/{db}/m/{measurement}` 路由与 BulkIngest 落库逻辑）；每个 service 的二进制帧路径与 REST 路径通过 `tests/SonnetDB.Parity` 等价性平移；客户端 SDK 能协商传输并回落 REST。REST/JSON 端点全部保留向后兼容。基准数字进报告不做主 CI gating。

### 不做的事

- **不**引入分布式复制 / 副本 / Raft / 多写节点（超出单机可靠性范围）。
- **不**为兼容而在 `src/SonnetDB.Core` 引入第三方运行时依赖（Windows 目录 fsync 只用 BCL P/Invoke）。
- **不**在本里程碑重写 SQL 引擎为完整成本模型优化器；P3 只做规则级下推、plan cache、hash join 与能力补齐，成本模型留后续里程碑论证。
- **不**改动对外 SQL / HTTP / ADO.NET / Document API 已有契约语义（三值逻辑等属修正错误行为，需在 CHANGELOG 明确"行为变更"并给迁移说明）。
- **不**把默认持久性从"性能优先"切到"每写 fsync"而不给关闭开关——#196 的决策必须保留可配置项与明确的吞吐/持久性权衡文档。
- **不**为 P5b 引入 gRPC、裸 TCP 或 AMQP；传输统一为**自定义二进制帧 over Kestrel HTTP/2**（复用鉴权/路由/TLS/流控/多路复用），帧编解码用 BCL（`System.IO.Pipelines`/`System.Buffers`）。裸 TCP 经评估收益（约 1.2~2×）不抵重写分帧/心跳/TLS 的复杂度且传输非当前瓶颈；gRPC 的 protobuf 行式编码对列式/向量负载不友好且引入第三方栈——故均不采用。
- **MQTT 例外**：MQTT 接入以**内建 broker（#242，服务端，对标 IoTDB/TDengine 设备直连）+ client 订阅外部 broker（#243，对标 InfluxDB+Telegraf）双形态**落地，二者**统一经 Server 层 IoTSharp 自家 [MQTTnet.AspNetCore.Routing](https://github.com/IoTSharp/MQTTnet.AspNetCore.Routing)** 实现（MVC 风格 topic 路由，运行时纯 C#、无 native，共享 `db/{db}/m/{measurement}` 路由 controller 与 `BulkIngestEndpointHandler` 三格式落库逻辑），因 QoS/retain/will/session/重连协议细节多、自造不划算；`src/SonnetDB.Core` 零第三方依赖不变。**不做 broker 集群 / 桥接 / 跨节点 session**。
- **不**为 P5 引入分布式 broker / 分区副本 / 跨节点消费者组 rebalance；SonnetMQ 保持单机嵌入式队列定位，per-topic 锁分片只解并发不引入集群。
- **不**删除任何现有 REST/JSON 端点（MQ/时序/SQL/向量/KV/对象/文档）；二进制帧与 MQTT 是**并列新增**，JSON/Base64 路径全部保留向后兼容，选型交由客户端按 `docs/` 矩阵决定。
- **不**为 P5b 新帧协议改动任何模型引擎的查询/写入语义；帧层只是传输编码，`service`/`op` opcode 一一映射到既有 API 行为，不借机改语义。

---

## Milestone 29 — 多模型统一管理工作台（Multi-Model Management Workbench）

> **背景**：SonnetDB 已是覆盖 8 种数据模型的多模态库（时序 / 关系 SQL / 文档 / KV / 全文 / 向量 / 对象存储 / 消息队列 SonnetMQ），但管理工具只覆盖了「时序 + SQL」一条线。当前唯一成型的 UI 是 `web/`（Vue3 + Naive UI + ECharts + CodeMirror）Web Admin：有 Dashboard、SQL Console（即 Studio 工作台）、schema 树、结果表/图、Trajectory 地图、Events 监控（SSE）、Users/Grants/Tokens、Copilot；`src/SonnetDB.Studio` 只是把 `web/dist` 打包进 WebView2 的桌面壳，**无任何独立能力**；VS Code 扩展（M18）大部分仍是脚手架（只有 Explorer 树 + SQL 执行客户端能跑）。对照 pgAdmin / SSMS / Navicat / DBeaver（关系）、RedisInsight（KV）、Kafka UI / RabbitMQ Management / EMQX Dashboard（MQ）、Milvus Attu / Qdrant / Weaviate Console（向量）、Kibana / OpenSearch Dashboards（全文）、MinIO Console（对象）、MongoDB Compass（文档），SonnetDB 缺一整批 per-model 管理工作台。
>
> **核心策略**：把「管理工具」从三个孤立工程重构为「一张能力矩阵 × 三个交付面」——(1) **Web Admin 旗舰**，逐模型做到对标单品级别（**本里程碑推进优先级最高的交付面**）；(2) **Studio 桌面** = 打包的 Web Admin + 桌面原生桥（原生文件对话框、磁盘连接库、本地 data-root 托管 server）；(3) **VS Code** = 开发者子集，复用同一批 HTTP 契约。世界级多模态管理工具 = 统一 Explorer + 外壳 + 每模型一个专用工作台，各自向该模型最好的单品看齐；三面共享同一套 server contract、权限模型与写审批框架，不各写各的。
>
> **边界**（与 M24 / M28 一致）：本里程碑只做**管理面 + 最小只读 metadata / browse 契约**。UI 消费 M19 / M21 / M23 / M28 已交付的引擎能力与 HTTP API；发现后端缺必要只读 metadata 时可补最小 server contract，但**不新增任何查询语义、索引语义、存储格式或写入语义**——所有写操作复用既有 data-plane API（SQL / Document / KV / Object / MQ 端点）。**文档模型管理面仍归 M24（#170~#172）**，**对象存储后端治理仍归 M19 #118**；本里程碑只把它们接入统一外壳并补齐对象浏览体验，不重复造引擎能力。`SonnetDB.Core` 零第三方依赖不变；契约新增走 Server 层。

### 能力矩阵（现状 → 目标工作台 → 对标单品）

| 模型 | 现有管理 UI | 目标工作台 | 对标单品 | 归属 PR |
|---|---|---|---|---|
| 时序 measurement | ✅ schema 树 + SQL Console + Trajectory 地图/图 | 保持并接入统一外壳 | InfluxDB UI / Grafana | #246（并入外壳） |
| 关系 SQL 表 | ⚠️ 仅能写 SQL，无数据网格 / 行内编辑 | 数据网格 + 行内编辑 + 可视化 EXPLAIN + 表设计器 + ER + 导入导出 | pgAdmin / SSMS / Navicat / DBeaver | #248~#250 |
| 文档集合 | ⚠️ 树可见 + `documents/find` API，无浏览器 | Document Explorer（**M24 交付**，本里程碑接入外壳） | MongoDB Compass | M24 #170~#172 + #257 |
| KV keyspace | ❌ 无 | keyspace 前缀树 + TTL 查看/编辑 + 类型化值查看 + 批量 / 前缀删 + 过期统计 | RedisInsight / AnotherRedisDesktopManager | #245 契约 + #251 |
| 全文索引 | ⚠️ 索引可见可 rebuild，无检索 UI | BM25 检索 + 高亮 + 分词器（Jieba/CJK）预览 + 模糊 / 短语构建器 | Kibana / OpenSearch Dashboards | #245 契约 + #255 |
| 向量索引 | ❌ 无（仅 schema 类型可见） | ANN 检索 playground（文本→embed / 原始向量→Top-K + score + 过滤）+ 索引统计 + HNSW 参数 | Milvus Attu / Qdrant / Weaviate Console | #245 契约 + #254 |
| 对象桶 | ❌ 无（M19 #118 治理页 🚧） | 桶浏览 + 对象上传 / 下载 / 预览 + 前缀导航 + 版本 / 生命周期 / 保留 + presigned URL + 审计 | MinIO Console / S3 Browser | M19 #118 后端 + #256 |
| 消息队列 SonnetMQ | ❌ 无 | topic / 消息浏览（按 offset / 时间 seek + header）+ 发布测试 + 消费 / 订阅 lag + ack + 吞吐 + DLQ / retention | Kafka UI / RabbitMQ Management / EMQX Dashboard | #245 契约 + #252~#253 |

### 阶段总览

| 阶段 | 主题 | PR 范围 | 目标 |
|------|------|---------|------|
| **A** | 管理契约与统一外壳 | #245 ~ #247 | 补齐 KV / 向量 / 全文 / MQ / 对象只读 metadata + browse 契约；Web Admin 左侧改统一多模型 Explorer；连接库 + 统一结果面板 / 写审批框架 |
| **B** | 关系工作台（对标 pgAdmin/SSMS/Navicat） | #248 ~ #250 | 数据网格 + 行内编辑 + 可视化 EXPLAIN + 表设计器 + ER + 导入导出 |
| **C** | KV / MQ / 向量 / 全文 工作台 | #251 ~ #255 | 四个缺失模型的专用管理工作台，各自对标其最佳单品 |
| **D** | 对象桶与文档收口 | #256 ~ #257 | 对象桶浏览器（收编 M19 #118 UI）；文档浏览器（M24）接入统一外壳与共享框架 |
| **E** | Studio 桌面原生桥 + VS Code 消费 + 收口 | #258 ~ #260 | 桌面原生能力；VS Code 复用同契约做多模型只读浏览；能力矩阵文档 + 三面 parity |

### A — 管理契约与统一外壳

| PR | 标题与范围 | 状态 |
|----|------------|------|
| #245 | **管理契约补齐（只读 metadata + browse endpoints）**：为当前无管理端点或端点过薄的模型补最小只读契约——KV keyspace `scan`（前缀/分隔符 + TTL + 类型 + 游标分页）与 keyspace 统计；向量索引 `stat`（度量/维度/图参数 ef/M/efConstruction）与 `search-preview`；全文索引 `stat`（doc/term 数、分词器）与 `search-preview`（BM25 + 高亮 + 分词器 analyze）；MQ topic `list` / `offsets` / `browse`（按 offset/时间 seek，含 header）/ `lag`；对象 bucket / object `list` 与 metadata。全部**读优先**、游标分页、走既有 Bearer + 三角色鉴权；写操作复用既有 data-plane API 不新增。`SonnetDB.Core` 不动，Server 层落地。 | ✅ |
| #246 | **统一多模型 Explorer + 连接库**：把 Web Admin 左侧导航从「时序/表/文档/索引/备份」扩展为覆盖 8 模型的统一树（Connection → Database → {Measurements / Tables / Collections / KV Keyspaces / Vector Indexes / FullText Indexes / MQ Topics / Buckets}）；每类节点的右键菜单路由到对应工作台；新增可持久化的连接库（Remote / Managed-local，token 走既有安全存储），活动连接与数据库选择全局一致，复用 SQL Console / CopilotDock 的 db 选择与权限状态。 | 📋 |
| #247 | **统一结果面板 + 写审批 / 历史 / 导出框架**：抽出跨模型共享的结果面板（Table / Raw / JSON / Chart 四视图，复用 `SqlResultPanel` / `SqlResultChart`）与**写审批框架**（staged preview → danger confirm → dry-run，比照 SQL Console 既有危险确认与 M24 写审批），供 B~D 各工作台统一挂载；统一 query/操作历史与 CSV/JSON 导出钩子；所有写、导入、rebuild、删除动作至少有 preview / dry-run / confirm 之一。 | 📋 |

> **#245 落地说明**：Server 层新增 `ManagementContractEndpoints`，已交付 KV `keyspaces`/`scan`（base64 游标分页）、向量 `indexes`/`search-preview`（复用既有 `knn(...)` data-plane）、全文 `indexes`/`search-preview`/`analyze`、MQ `topics`/`offsets`（含 lag）/`browse`（按 offset 只读）。**对象** bucket/object list 与 metadata **已由既有 S3 端点覆盖**，本 PR 不重复实现。相对"`SonnetDB.Core` 不动"的初始约束，仅新增一个只读枚举方法 `SonnetMqStore.ListTopicStats()`（MQ topic 私有集合无其他公开枚举入口，纯读、不改任何队列语义）。**本 PR 范围外、留待后续里程碑**（Core 无公开 API）：全文 term 数与 BM25 高亮、MQ 按时间 seek、向量索引 live count 与 per-index 有效度量（当前引擎构建固定 cosine，已如实回显）。写/删/rebuild 一律不在本 PR，留给 #247 写审批框架 + 既有 data-plane。

### B — 关系工作台（对标 pgAdmin / SSMS / Navicat / DBeaver）

| PR | 标题与范围 | 状态 |
|----|------------|------|
| #248 | **关系数据网格 + 行内编辑**：表数据网格，游标分页、列排序 / 过滤、单元格类型化渲染；行内 INSERT / UPDATE / DELETE 经生成的**参数化 SQL**（复用 M28 #213）提交，编辑批次走 #247 staged preview + 事务确认（复用 M19 #110/#113 事务）；主键/唯一约束冲突走既有错误码回显。只调既有 SQL 端点，不新增查询语义。 | 📋 |
| #249 | **可视化 EXPLAIN + 表设计器 + 索引管理**：把既有 SQL `EXPLAIN` 计划渲染为可视化计划树（scan / filter / join / topN / 下推标注，复用 M28 #214~#217/#220 的 EXPLAIN 输出）；表设计器以可视化编辑生成 `CREATE TABLE` / `ALTER TABLE ADD/DROP/RENAME COLUMN` / `RENAME TABLE` DDL（复用 M19 #111 能力与其明确拒绝项），DDL 保存前 preview + confirm；索引查看 / 创建 / rebuild。 | 📋 |
| #250 | **关系导入导出 + ER 图**：CSV / JSON 导入导出（列映射、dry-run、批量错误报告、进度、取消）；基于 `INFORMATION_SCHEMA`（M19 #111）绘制 ER 图（表 / 列 / 主外键关系）；DDL 脚本导出。导入写入走 #247 写审批。 | 📋 |

### C — KV / MQ / 向量 / 全文 工作台

| PR | 标题与范围 | 状态 |
|----|------------|------|
| #251 | **KV 浏览器（对标 RedisInsight）**：消费 #245 `scan` 契约做按前缀 / 分隔符的 keyspace 树扫描（游标分页，避免全量拉取）；TTL 显示与编辑（复用 M19 #116 TTL）；按类型的值查看 / 编辑；批量 get/set/remove、前缀删除、命名空间切换、过期统计。写与前缀删走 #247 写审批。 | 📋 |
| #252 | **SonnetMQ 控制台一（topic + 消息浏览 + 发布）**：topic 列表 + offset / 分区 / retention 概览；消息浏览器支持按 offset / 时间 seek、查看 header 与 payload（消费 #245 `browse`）；发布测试消息（复用既有 MQ 发布端点）；依赖 **M28 P5a（#231~#234）** 提供的 per-topic 统计与冷数据可读性。 | 📋 |
| #253 | **SonnetMQ 控制台二（消费 / 订阅监控 + 吞吐 + DLQ）**：消费者 / 订阅 lag 与 ack 监控、消费进度可视化；吞吐 / 积压曲线（复用 M17 metrics + Events SSE）；DLQ 查看与 retention 策略展示。依赖 #245 `lag` 契约与 M28 P5a MQ 统计，随 P5b #236 推送订阅落地可展示实时推送状态。 | 📋 |
| #254 | **向量检索 playground（对标 Milvus Attu / Qdrant）**：向量索引 / 集合统计（维度、行数、度量 L2/IP/cosine、HNSW ef/M/efConstruction，复用 M28 #223/#226 参数暴露）；ANN 检索 playground——文本经 Copilot embed 或直接粘原始 `float[]`，返回 Top-K + score + 元数据过滤（消费 #245 `search-preview` + 既有向量检索端点）；度量方式与图参数只读展示，不改索引语义。 | 📋 |
| #255 | **全文检索 playground（对标 Kibana / OpenSearch Dashboards）**：全文索引列表 + 统计（doc/term 数、分词器）；BM25 检索 UI 带高亮、评分与分页；分词器 / analyzer 预览（Jieba/CJK，展示切词结果）；模糊 / 短语 / 布尔查询构建器（消费 #245 `search-preview` + 既有全文检索端点）；索引 rebuild 走 #247 写审批。 | 📋 |

### D — 对象桶与文档收口

| PR | 标题与范围 | 状态 |
|----|------------|------|
| #256 | **对象桶浏览器（对标 MinIO Console，收编 M19 #118 UI）**：桶列表 / 创建 / 删除；对象浏览（前缀导航、上传 / 下载 / 预览、range read）；multipart 会话查看；版本 / 生命周期 / 保留 / legal hold 展示与编辑；presigned URL 生成；访问审计与容量 / quota 统计。**后端能力复用 M19 #118**（bucket policy / lifecycle / versioning / audit / quota）；本 PR 把 #118 规划的 Buckets / Objects / Multipart / Audit 页面**收编进统一外壳的对象工作台**，#118 只保留后端治理能力交付。 | 📋 |
| #257 | **文档浏览器接入统一外壳**：把 **M24（#170~#172）** 的 Document Explorer / Validator Governance / 导入导出接入 #246 统一 Explorer 与 #247 共享结果 / 写审批框架，确保文档模型与其余模型的连接选择、权限状态、结果面板、写审批一致；**不新增文档引擎能力**（引擎与专属管理语义仍归 M24 / M21）。 | 📋 |

### E — Studio 桌面原生桥 + VS Code 消费 + 收口

| PR | 标题与范围 | 状态 |
|----|------------|------|
| #258 | **Studio 桌面原生桥**：`SonnetDB.Studio`（`NativeWebHost` WebView2 壳）从纯 WebView 升级为带原生桥——原生文件打开 / 保存对话框（供导入导出、对象上传下载、备份恢复）、磁盘持久化连接库、本地 `data root` 托管 SonnetDB Server 启动 / 停止 / 健康检查（对齐 M18 #105 托管本地模式思路）、原生菜单。Web Admin 检测到运行在 Studio 壳内时启用原生能力，浏览器内优雅降级。 | 📋 |
| #259 | **VS Code 多模型消费（复用 M29 契约）**：VS Code 扩展先补完 **M18 #103（结果 Table/Raw/Chart 三视图）+ #104（Copilot 面板，客户端 `streamCopilot` 已写好只差接线）**，再把 Explorer 与结果面板扩展为消费 #245 契约做 **KV / 向量 / 全文 / MQ 只读浏览**；写操作与完整工作台仍以 Web Admin 为主，VS Code 定位开发者只读 + SQL 执行子集。与 **M18 交叉引用**：M18 保留 VS Code 交付主线，多模型浏览契约由本 PR 落地。 | 📋 |
| #260 | **管理工作台收口 + 文档 + 三面 parity**：汇总能力矩阵文档（模型 → 工作台 → 对标单品 → 交付面覆盖度）；`docs/` 增管理工具章节与截图；Web Admin / Studio 桌面 / VS Code 三面能力 parity 表（谁支持哪些模型的浏览 / 查询 / 编辑 / 导入导出 / 监控）；各工作台 e2e smoke。 | 📋 |

### 推进顺序

```text
Web Admin 旗舰优先（用户决策 2026-07-04）：
A 外壳：#245（管理契约补齐）→ #246（统一多模型 Explorer + 连接库）→ #247（统一结果 + 写审批框架）
B 关系：#248（数据网格 + 行内编辑）→ #249（可视化 EXPLAIN + 表设计器）→ #250（导入导出 + ER）
C 四模型：#251（KV 浏览器）→ #252（MQ 控制台一）→ #253（MQ 控制台二）→ #254（向量 playground）→ #255（全文 playground）
D 收口：#256（对象桶浏览器，收编 M19 #118 UI）→ #257（文档浏览器接入外壳）
E 三面：#258（Studio 桌面原生桥）∥ #259（VS Code 多模型消费）→ #260（收口 + 文档 + parity）
```

> **阶段间依赖与并行度**：**A（#245~#247）是所有 per-model 工作台的地基，必须最先**——#245 契约是 #251~#256 的前置，#246/#247 外壳与框架是 B~D 所有工作台的挂载点。B / C / D 各工作台在 A 落地后**相互独立可并行 / 穿插**（各消费自己的 #245 契约 + 挂 #247 框架）。跨里程碑依赖：**#252/#253 MQ 控制台依赖 M28 P5a（#231~#234）** 的 per-topic 统计与冷数据可读性、`#253` 实时推送状态随 P5b `#236` 落地增强；**#254 向量 playground** 依赖 M28 #223/#226 的 HNSW 参数与度量暴露；**#256 对象桶** 依赖 M19 #118 后端治理能力；**#257 文档** 依赖 M24 #170~#172。E 的 #258 桌面桥可在任一模型工作台落地后并行；#259 VS Code 需先补完 M18 #103/#104；#260 收口最后。

### 验收标准

- **A**：KV / 向量 / 全文 / MQ / 对象都有可用的只读 metadata + browse 契约（游标分页、走既有鉴权）；Web Admin 左侧统一 Explorer 能展开全部 8 模型对象并路由到对应工作台；连接库可持久化多连接、token 不落明文；统一结果面板与写审批框架被至少一个工作台复用。
- **B**：关系表可在数据网格中浏览、排序、过滤、分页，并完成行内 INSERT/UPDATE/DELETE（参数化 SQL + 事务确认）；可视化 EXPLAIN 展示计划树；表设计器生成的 DDL 与 M19 #111 能力一致且保存前 preview；CSV/JSON 导入导出与 ER 图可用。
- **C**：KV 浏览器能按前缀树扫描、看 / 改 TTL、批量与前缀删除；MQ 控制台能列 topic、按 offset/时间浏览消息含 header、发布测试消息、观测消费 / 订阅 lag 与吞吐；向量 playground 能对索引做 ANN 检索返回 Top-K + score 并展示 HNSW 参数；全文 playground 能做 BM25 检索带高亮并预览分词结果。
- **D**：对象桶浏览器能浏览桶 / 对象、上传下载预览、看版本 / 生命周期 / 审计、生成 presigned URL；文档浏览器（M24）与其余模型共享同一连接选择、权限状态、结果面板与写审批框架。
- **E**：Studio 桌面壳提供原生文件对话框、磁盘连接库与本地托管 server；VS Code 补完 #103/#104 并能只读浏览 KV / 向量 / 全文 / MQ；能力矩阵文档与三面 parity 表齐备。
- **全局**：所有写 / 导入 / rebuild / 删除动作至少有 preview / dry-run / confirm 之一；本里程碑未新增任何引擎查询 / 写入 / 索引 / 存储语义，所有写走既有 data-plane API。

### 不做的事

- **不**新增任何模型的引擎查询 / 写入 / 索引 / 存储语义——本里程碑是管理面 + 只读 metadata / browse 契约，写复用既有 data-plane API（与 M24 边界一致）。
- **不**把文档引擎能力塞回本里程碑——文档管理面（Explorer / Validator / 导入导出）仍归 **M24 #170~#172**，本里程碑（#257）只做接入。
- **不**把对象存储后端治理（bucket policy / lifecycle / versioning / audit / quota）塞进本里程碑——仍归 **M19 #118**，本里程碑（#256）只收编其 UI 页面进统一对象工作台。
- **不**在 `SonnetDB.Core` 引入第三方依赖；管理契约与 UI 均走 Server / 前端层。
- **不**替换任何现有 Web Admin 页面或 REST 端点——统一 Explorer / 工作台是**扩展与整合**，SQL Console / Dashboard / Events / Users / Copilot 保留。
- **不**在 VS Code 做完整 per-model 编辑工作台——VS Code 定位开发者只读浏览 + SQL 执行子集，完整编辑体验以 Web Admin 旗舰为准。
- **不**做多节点 / 集群管理面（监控与备份编排的分布式形态由 SonnetDBEE 承接，本里程碑限单机 / 单连接管理）。

---

## 里程碑总览

| Milestone | 主题 | PR 范围 | 状态 |
|-----------|------|---------|------|
| 0 | 项目脚手架 | #1 ~ #3 | ✅ |
| 1 | 内存与二进制基础设施 | #4 ~ #6 | ✅ |
| 2 | 逻辑模型与目录 | #7 ~ #9 | ✅ |
| 3 | 写入路径 | #10 ~ #13 | ✅ |
| 4 | 查询路径 | #14 ~ #16 | ✅ |
| 5 | 稳定性与性能（写入侧） | #17 ~ #21 | ✅ |
| 6 | SQL 前端 + Tag 倒排索引 | #22 ~ #28 | ✅ |
| 7 | 压缩编码（Delta / Gorilla） | #29 ~ #31 | ✅ |
| 8 | 服务器模式（HTTP + 远端 ADO + 控制面 + Vue3 后台 + SSE） | #32 ~ #34c | ✅ |
| 9 | 性能基准与发布 | #35 ~ #39（含 #36、#37a、#37b） | ✅ |
| 10 | 批量入库快路径（历史扩展占位已拆分） | #42~#45 批量入库专题 | ✅（#40 转入 M18，#41 并入 M28 P5b） |
| 11 | 写入快路径（PR #45 瓶颈收尾） | #46 ~ #49 | ✅ |
| 12 | 函数与算子扩展（PID / Forecast / UDF） | #50 ~ #57 | ✅ |
| 13 | 向量类型与嵌入式向量索引（Copilot 知识库底座） | #58 ~ #62 | ✅ |
| 14 | SonnetDB Copilot：MCP 工具 + 知识库 + 智能体 | #63 ~ #69 | ✅ |
| 15 | 地理空间类型与轨迹分析 | #70 ~ #77 | ✅ |
| 16 | Copilot 产品化升级（嵌入式 AI 助手 UX） | #78 ~ #88 | ✅ |
| 17 | 可观测性与运行时可见性（OTel + 结构化日志 + 诊断端点） | #89 ~ #98 | 🚧（**#89~#91 ✅ 已完成**（Core Meter/Activity 基线 + Server OTel 引导 + Prometheus 端点/监控面板），P5b #235 前置条件已解除；#92~#98 📋 按带宽推进） |
| 18 | VS Code 数据库扩展（SonnetDB for VS Code） | #99 ~ #108 | 🚧（#99 骨架与规划已落目录） |
| 19 | 生态适配底座能力（关系 + KV/缓存 + 对象桶 + 大量 measurement） | #109 ~ #126 | 🚧（#109~#117、#122/#123 已完成；IoTSharp 专属规划已迁出） |
| 20 | 多模能力对齐与平移测试（Parity） | #127 ~ #136 | ✅（实现已落地；nightly 稳定率继续按 `parity-results` 监控） |
| 21 | Document Store 单机能力升级（MongoDB-like，不做协议兼容） | #137 ~ #146 | ✅ |
| 22 | Agent Memory / Codebase Intelligence（应用层候选，非内置路线） | #150 ~ #159 | ⏸️ 应用层候选 / 暂停内置派单 |
| 23 | 搜索与向量引擎合并（DotSearch / DotVector 收编） | #160 ~ #169 | ✅ |
| 24 | SonnetDB Studio 管理体验升级（Document 管理面） | #170 ~ #172 | 📋 |
| 25 | Document Store 验收、文档与发布治理 | #173 ~ #174 | 📋 |
| 26 | 连接器路线独立化（C ABI + 多模型 API） | #175 ~ #181 | ✅ |
| 27 | Industrial Data Agent 与 AI-ready 产品化路线 | #182 ~ #188 | ⚠️ 滞后（#182 已落第一批文档；#183~#188 待追赶） |
| 28 | 可靠性、并发正确性与热路径加固（P0~P5 分阶段） | #189 ~ #244 | 🚧（P0~P3 + P5a ✅；**SonnetMQ 硬化 #230→#234 收官，数据面 🔴 全部关闭（含 MQ3 无界内存/OOM）**；**M17 #89~#91 可观测性基线已落地**；**P5b #235 帧协议 + #236 流式推送订阅 + #237 时序列式批量写 + #238 SQL 流式结果集 + #239 向量检索接入 ✅（帧协议已覆盖 MQ+时序+SQL+向量四 service）**；**P4 已开工：#221 文档惰性 scan + #222 FTS 批量成段 + #223 向量度量/efConstruction ✅**；下一步 = P4 #224 或 P5b #240 KV/对象/文档接入，可按带宽并行；审计 54 项 + P5 新增 MQ/N 专项） |
| 29 | 多模型统一管理工作台（Multi-Model Management Workbench） | #245 ~ #260 | 📋（Web Admin 旗舰优先；A 外壳→B 关系→C KV/MQ/向量/全文→D 对象/文档收口→E 桌面/VS Code；文档管理面归 M24、对象后端归 M19 #118） |
| MM9 | 多模型统一备份、恢复和管理工具第一批 | BackupService + sndb backup | ✅ |

**当前推进顺序**：Milestone 14（Copilot）、Milestone 15（地理空间）、Milestone 16（Copilot 产品化升级）、Milestone 20（Parity #127~#136 实现）、Milestone 21（Document Store 单机能力升级 #137~#146）、Milestone 23（搜索与向量引擎合并）与 Milestone 26（连接器路线独立化 #175~#181）均已完成或收口。**Milestone 28（可靠性、并发正确性与热路径加固 #189~#229）** 是 2026 跨子系统深度审计后新增的加固主线，优先级最高：先做 **P0 数据可靠性止血**（#189~#196，Windows 目录 fsync / flush add-then-reset / 后台 worker 租约 / FTS manifest 原子 / HNSW 快照 / Delete 持久化 / 段头尾 CRC / 默认持久性决策），再依次推进 P1 正确性（#197~#203）、P2 写吞吐（#204~#211）、P3 查询与 SQL 能力（#212~#220）、P4 索引与向量能力（#221~#229）。**当前焦点（2026-07）：P0~P3、P5a SonnetMQ 热路径硬化（#230→#234）与 P5b #235/#236 均已收口**——SonnetMQ 曾是引擎数据面唯一仍开着 🔴 Critical 的整块（MQ1~MQ4，尤其 MQ3 无界内存 / OOM 属数据可靠性隐患），按「先止血、再正确、再吞吐、再能力」原则先于 P4 完成；#235 已落地帧信封 + MQ service + h2c 端点 + 编码基准，#236 已落地 MQ HTTP/2 双工流式推送订阅（Core `WaitForMessagesAsync` pulse + `/v1/frame/stream` 端点），#237 已落地时序列式批量写（`TsdbFrameCodec` + `TsdbColumnarPointReader` 流式列转行直通 `WriteMany`，帧 wire 3.73× 小于 JSON、100k 行编码 326× 快），#238 已落地 SQL 流式列式结果集（`SqlFrameCodec` meta→rows×N→end 分块流式 + 只读语句门禁，100k 行编码分配 2.2KB vs NDJSON 24MB），#239 已落地向量检索接入（`VectorFrameCodec` f32 二进制查询向量 + 响应复用 sql 帧块布局 + 检索内核与 SQL knn TVF 共用，dim=1536 请求编码 ~1100× 快零分配）（M17 #89~#91 可观测性基线按 2026-07-04 决策先行落地）；**P4 已开工：#221 文档查询惰性 scan 已落地（planner 候选惰性化 + 计数式代价估算，I2 收口），#222 FTS 批量成段 + 增量语料统计已落地（IndexMany 整批单段 + manifest 一次落盘 + 字段语料统计增量维护，I3 收口），#223 向量度量贯通 + efConstruction 独立已落地（声明度量 L2/InnerProduct 贯通建图与 ANN gate、efConstruction 与 efSearch 解耦，measurement schema 升 v5 + 段内 SDBVIDX 升 v4 持久化度量/efConstruction、DDL 加 `metric=`/`ef_construction=`，I7、I9 收口）**；下一步 = P4 #224（KNN block skip-index）或 P5b #240（KV/对象/文档接入帧协议），可按带宽并行。详见 [Milestone 28](#milestone-28--可靠性并发正确性与热路径加固reliability--concurrency--performance-hardening) 阶段总览下的「当前焦点」callout。**Milestone 27（Industrial Data Agent 与 AI-ready 产品化路线）** 仍是对外门面与中长期 AI 产品主线，但**依赖 M28、分两拨推进**：#182 门面文档、#183 想要的 MCP 工具契约与 #185 想要的 provider 抽象**代码均已就位**，故 M27 剩余工作是「打包、定位、证明、去重」而非「建 AI 功能」。**可与 M28 并行的纯文档条**——#182 收尾、#185 provider-neutral 配置文档、#183 降级为「稳定并文档化现有 MCP 工具（不新增 Agent 工具）」、#188 边界声明；**必须等 M28 收口后再启动**——#184 端到端工业异常 Demo（依赖 P5 MQTT + P0 可靠写入，引擎主张为真后再拍 Demo）、#187 eval 与成本指标（推迟到有真实采纳之后）；**#186 写入审批二阶段移交 Milestone 29**（与其「共享写审批框架」重叠，M27 只消费）。同时并行推进 **Milestone 17（可观测性与运行时可见性）** 的 OTel / 结构化日志 / 诊断端点 / Copilot 服务端会话持久化，以及 **Milestone 18（VS Code 扩展）** 的 `#99 ~ #103` “远程连接 + Explorer + SQL + 结果视图”闭环。**Milestone 19（生态适配底座能力）** 只保留 SonnetDB 通用数据库能力，#109~#117 与 #122/#123 已完成；IoTSharp 专属 Profile、兼容矩阵、灰度、双写、回滚和长稳验收已迁入 IoTSharp 仓库 RD-10。后续继续推进对象治理、通用迁移/校验原语、增量索引 / 后台维护成本与大量 measurement 长稳专项（其中 #124 增量索引与 Milestone 28 #207 目标一致，以 M28 为落地口径）。Studio 管理面进入 **Milestone 24**，MongoDB 参考 parity、长稳、容量报告和发布文档进入 **Milestone 25**。**Milestone 29（多模型统一管理工作台 #245~#260）** 是在 Web Admin 已有「时序 + SQL」管理面基础上，把管理工具从三个孤立工程重构为「一张能力矩阵 × 三个交付面（Web Admin 旗舰 / Studio 桌面 / VS Code）」，逐模型补齐 KV / MQ / 向量 / 全文 / 对象 / 关系数据网格等对标 pgAdmin / RedisInsight / Kafka UI / Attu / Kibana / MinIO Console 的专用工作台；**Web Admin 旗舰优先**，先做 A 阶段管理契约 + 统一外壳（#245~#247）再逐工作台推进；文档管理面仍归 M24、对象后端治理仍归 M19 #118，M29 只做接入与收编，且不新增任何引擎语义。**Milestone 22（Agent Memory / Codebase Intelligence）** 重新定位为基于 SonnetDB 的上层应用 / 示例方案候选，**本轮复核再次确认暂停 #150~#159 内置派单、不内置**；理由是其所需能力（Document / FullText / Vector / Hybrid / MCP）均已在库内、M22 不产出新引擎能力，且代码解析依赖违反 Core 零第三方铁律。只有将来在 `examples/` dogfood 验证出通用数据库能力缺口时，才把该缺口拆成独立 Core / Server / Studio PR。SonnetDBEE C5.7 / MM9 的开源核心第一批已提供 `BackupService` 和 `sndb backup create/inspect/verify/restore`，企业级定时、增量、审计和 UI 编排继续由 SonnetDBEE 承接。**Milestone 20** 后续不再按 #129 继续派单，而是通过 `.github/workflows/parity.yml`、`parity-results` 分支与 `tests/SonnetDB.Parity/reports/sample-run.md` 持续暴露能力缺口、SKIP 原因和 nightly 稳定性。


---

## 性能优化待办（2026 审计后回收的中等优先项）

以下是一次完整审计后留下的纯性能优化点；功能上是对的，只是热路径里有可优化的常数因子或代数复杂度。每项都有目标位置和现状成本，便于后续按需安排。

| 编号 | 位置 | 现状 | 建议改造 | 估时 |
|------|------|------|---------|------|
| P1 | `src/SonnetDB.Core/Query/KnnExecutor.cs:103` | 每个候选都调用 `TombstoneTable.IsCovered` —— 内部锁 + `ToArray()` 快照 | 提到 ScanSegment 之前一次性拿快照（已在 KnnExecutor 顶层做 GetForSeriesField 检查），把候选过滤改成直接遍历该快照 | 15 分钟 |
| P2 | `src/SonnetDB.Core/Sql/Execution/RelationalSelectExecutor.cs` 子查询路径 | 同一个子查询 SELECT 子树在每个外层行上重新执行；只要不引用外层列就能 memoize | 对 ExistsExpression / SubqueryExpression 加 `Cache<SelectStatement, IReadOnlyList<...>\>`，先做一次 "是否相关" 静态判定；非相关查询执行 0 或 1 次 | 30 分钟 |
| P3 | `src/SonnetDB.Core/FullText/DocumentFullTextIndexStore.cs` ExpandFuzzyTermQuery | 模糊扩展时把 tombstoned term 也参与编辑距离计算 | 让内置全文引擎的 EnumerateTerms 暴露一份 "未 tombstone" 视图，或者在 PersistentFullTextIndex 端先过滤；当前简单做法是上层把展开候选再用一次 Search 验证 | 10 分钟 |
| P4 | `src/SonnetDB.Core/Tables/TableManager.cs` ExpandCascadeDeletesLocked | BFS 每一步都对子表做 `childStore.Scan()` 全表线性扫描——O(parents × FKs × N) | 在子表 FK 列上建临时哈希索引（`Dictionary<keyBytes, List<row>>`），或直接给 FK 列建持久化二级索引，cascade 改成索引查找 | 60 分钟 |

这些不阻塞功能正确性，不影响 parity 通过率，并且在小数据量上不会被察觉。当任一线上场景遇到瓶颈时（高基数 KNN / 重相关子查询 / 高基数 fuzzy / 万行级 cascade）按需挑出来做。

> **与 Milestone 28 的关系**：本表是上一轮审计遗留的独立性能小项，其中 P2（子查询 memoize）已被 [Milestone 28](#milestone-28--可靠性并发正确性与热路径加固reliability--concurrency--performance-hardening) 的 #216 吸收合并落地；P1（KnnExecutor tombstone 快照）与 M28 #208/#226 相邻，可一并处理。P3（fuzzy tombstone 视图）与 P4（cascade delete 哈希索引）不在 M28 范围内，保留在本表按需推进。Milestone 28 是 2026 年更完整一轮跨子系统审计（54 项）的成果，涵盖数据可靠性、并发正确性、SQL 正确性与更广的热路径，优先级高于本表。
