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
| 10 | 扩展和第三方 | #40, #41 + #42 ~ #45 | 🚧（#42 ~ #45 ✅，#40 转入 Milestone 18） |
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

### PR 拆分

| PR | 标题与范围 | 状态 |
|----|------------|------|
| #89 | **M17.1：Core 端 Meter / ActivitySource 基线**：在 `SonnetDB.Core` 新增 `SonnetDB.Diagnostics` 命名空间，引入静态 `SonnetDbMeter`（`Meter("SonnetDB.Core", "1.0.0")`）与 `SonnetDbActivitySource`（`ActivitySource("SonnetDB.Core")`）。在写入路径（`Tsdb.Insert` / `BulkValuesParser` / `MemTable.Append`）、Flush / Compaction、Segment 读取、`QueryEngine.Execute`、WAL fsync 处插入 `Counter<long>` / `Histogram<double>` / `Activity?.Start()`，遵守 OTel 语义约定（`db.system=sonnetdb`、`db.operation`、`db.statement.kind`、`sonnetdb.segment.id`、`sonnetdb.measurement.name`）。**禁止引入 OpenTelemetry NuGet**，仅用 BCL `System.Diagnostics.Metrics`。 | 📋 |
| #90 | **M17.2：Server OpenTelemetry 引导**：在 `src/SonnetDB`（Server 入口）引入 `OpenTelemetry.Extensions.Hosting`，按官方推荐结构注册 `WithMetrics(b => b.AddMeter("SonnetDB.Core", "SonnetDB.Server").AddAspNetCoreInstrumentation().AddHttpClientInstrumentation())` 与 `WithTracing(b => b.AddSource("SonnetDB.Core", "SonnetDB.Copilot").AddAspNetCoreInstrumentation())`。Resource attributes 自动包含 `service.name=sonnetdb`、`service.version`、`service.instance.id`、`host.name`。OTLP Exporter 走 `OTEL_EXPORTER_OTLP_ENDPOINT` 环境变量，默认不导出（Console exporter 仅在 `Development` 启用）。 | 📋 |
| #91 | **M17.3：Prometheus 端点 + Web 内嵌指标面板**：可选启用 `/metrics`（`OpenTelemetry.Exporter.Prometheus.AspNetCore`），用 `Observability:Prometheus:Enabled=true` 开关。Web Admin 新增「监控」侧边栏，使用 `fetch('/metrics')` 客户端解析 prom 文本，实时绘制：写入吞吐（`sonnetdb.write.points`）、查询 P95（histogram bucket 还原）、MemTable 大小、Segment 数、WAL 落盘延迟、Copilot 调用数 / token 总量。零图表第三方依赖，使用既有 `naive-ui` + 简易 SVG 折线（与现有 dashboard 风格一致）。 | 📋 |
| #92 | **M17.4：Copilot 指标与追踪**：`SonnetDB.Copilot` 命名空间下新增 `CopilotMeter`（`Meter("SonnetDB.Copilot")`）记录 `copilot.chat.requests`（按 model / mode tag）、`copilot.chat.duration`、`copilot.chat.tokens`（in/out）、`copilot.tool.calls`（按 tool name tag）、`copilot.knowledge.recall.hits` / `.misses`；Agent 每次 `PlanToolsAsync` / `RunToolAsync` / `GenerateAnswerAsync` 都开 `Activity` span，把 `tool.name`、`tool.arguments.length`、`tool.result.rows` 写到 tags。CopilotDock 与 AiSettingsView 增加「最近 1 小时调用 / token 用量」摘要卡片（消费 `/v1/copilot/metrics` 简化端点）。 | 📋 |
| #93 | **M17.5：结构化日志统一**：所有 `ILogger` 调用改用源生成日志（`[LoggerMessage]`），消除运行时 string interpolation 装箱。统一日志事件分类（Write / Query / Flush / Compaction / Wal / Copilot / Auth / Http）与 EventId 区段（1000~1999 写入；2000~2999 查询；…）。在 `Program.cs` 引入 `JsonConsoleFormatter`，生产模式默认输出 JSON 行（`logging.json`），开发模式保持单行简化格式。 | 📋 |
| #94 | **M17.6：Health / Readiness 端点扩展**：把现有 `/healthz` 拆为 `/healthz/live`（进程存活）与 `/healthz/ready`（细分 checks：`segment_store_writable`、`wal_writable`、`copilot_provider_reachable`、`copilot_embedding_provider_reachable`）。引入 `IHealthCheck` 接口的 SonnetDB 实现（无第三方依赖），结果以 ASP.NET Core HealthChecks 标准 JSON 输出。Web Admin 顶部状态条改为消费 `/healthz/ready`，单独显示 4 个 check 的颜色点。 | 📋 |
| #95 | **M17.7：Slow Query Log + Top-N 查询统计**：可选开关 `Observability:SlowQueryLog:Enabled=true` + `ThresholdMs=100`。`QueryEngine.Execute` 完成后若超过阈值则发 `Activity.RecordException`-风格的结构化日志事件，并写入内存环形缓冲（`SonnetDB.Diagnostics.SlowQueryRing` 默认 256 条）。新增 `GET /v1/diagnostics/slow-queries` 与 `GET /v1/diagnostics/top-queries`（按归一化 SQL 指纹聚合 count / p50 / p95 / max）。Web Admin SQL Console 旁边新增「慢查询」抽屉。 | 📋 |
| #96 | **M17.8：Diagnostic Dump 端点**：新增 `GET /v1/diagnostics/dump`（仅 admin token）返回 JSON 快照：进程 GC（`GC.GetGCMemoryInfo()` / `GC.GetTotalMemory(false)`）、ThreadPool（`ThreadPool.GetAvailableThreads`）、SonnetDB 内部计数（每 db 的 MemTable 大小 / Segment 数 / 待 Compaction 任务 / WAL 文件列表 / Copilot 在飞会话数）。**禁止 dump 用户数据点本身**，仅 metadata。CLI 新增 `sonnetdb-cli diag dump` 命令直接调该端点，便于复现性能问题时一键采集。 | 📋 |
| #97 | **M17.9：Copilot 服务端会话持久化（M16 M5 二阶段）**：在 `__copilot__` 系统库新增 `conversations`（`id TAG, title TAG, owner TAG, created_at, updated_at, message_count, summary FIELD STRING`）与 `messages`（`id TAG, conversation_id TAG, role TAG, content FIELD STRING, model TAG, tokens FIELD INT, ts`）两张 measurement；新增 `GET/POST/DELETE /v1/copilot/conversations[/{id}]` 与 `GET /v1/copilot/conversations/{id}/messages`；CopilotDock 「会话历史」Popover 在登录态下从服务端拉取（owner=当前 user），匿名/未登录回落到现有 `localStorage` 存储。会话历史可按 owner 隔离与跨设备同步。 | 📋 |
| #98 | **M17.10：CHANGELOG / docs / OTel 端到端验证**：补 `docs/observability.md`（指标列表、追踪 span 树、health checks 含义、prom scrape 配置示例、`OTEL_EXPORTER_OTLP_ENDPOINT` 与本地 Aspire Dashboard 联调）；补 `docs/troubleshooting.md`（常见慢查询模式 + diagnostic dump 解读）；补 docker-compose 示例追加可选 `otel-collector` + `prometheus` + `grafana` 三服务（`profile: observability`，默认不启动）；端到端验证：嵌入式启动 → 触发写入 / 查询 / Copilot 调用 → 在 Aspire Dashboard 看到完整 trace（HTTP → SQL → Segment 读取 → Copilot Agent → tool 调用）。 | 📋 |

### 推进顺序

```
PR #89（Core Meter / Activity 基线）
  → #90（Server OTel 引导）
  → #91（Prometheus + Web 监控面板）
  → #92（Copilot 指标 / 追踪）
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

> **当前状态**：⚠️ 滞后。#182 已落第一批 AI-ready 门面文档，但 #183~#188 尚未形成工具契约、工业 Demo、provider-neutral 配置、本地模型、写入审批二阶段与 eval / 成本指标闭环，后续派单应优先追赶这些缺口。

> **目标**：把 SonnetDB 的对外门面从“多模型数据库”收敛为“面向 .NET 工业边缘应用的本地优先数据引擎”，并把 Copilot 从通用 SQL 助手推进到可被生产场景理解、演示和集成的 **Industrial Data Agent**。本里程碑优先做产品定位、AI-ready 文档、工业 Demo、Agent 工具边界和 provider-neutral 能力，不改动核心二进制格式。

> **边界**：
> 1. 多模型能力仍然保留，但作为能力矩阵描述，不再作为 README 第一屏的唯一定位。
> 2. Copilot / Agent 的第一责任是读取 schema、生成 SonnetDB 方言 SQL、执行只读分析、解释结果和请求写入审批；不绕过现有权限模型。
> 3. AI provider 必须走抽象层，不把 SonnetDB 绑定到 GPT、Claude、Gemini、DeepSeek、Qwen、Ollama 或任一单一供应商。
> 4. 工业 Demo 以 MQTT / HTTP ingest、设备异常、维修建议和上层平台集成为主，不把 SonnetDB 宣传为分布式云 TSDB 或大型集群平台；IoTSharp 联合样例归 IoTSharp 仓库维护。

### PR 拆分

| PR | 主题 | 状态 |
|----|------|------|
| #182 | **AI-ready 门面文档第一批**：README / README.en 第一屏改为 `.NET industrial edge local-first data engine`；新增 `llms.txt`、`docs/industrial-ai-applications.md`，让开发者和 AI Agent 明确 SonnetDB 适合工业边缘、IoT telemetry、本地数据引擎、Copilot / MCP 场景。 | 🚧 |
| #183 | **Industrial Data Agent 工具契约**：梳理并稳定 MCP / Copilot 工具命名、参数和权限边界，形成 `list_databases`、`list_measurements`、`describe_measurement`、`sample_rows`、`draft_sql`、`query_sql`、`explain_sql`、`execute_sql` 的 typed contract 文档；新增只读诊断工具 `analyze_measurement_anomaly` 的设计稿或最小实现。 | 📋 |
| #184 | **工业异常分析 Demo**：新增 MQTT / HTTP ingest 示例，演示设备温度 / 电流 / 振动写入 SonnetDB，再通过 Copilot / MCP 提问“哪台设备今天最异常？”并生成报告；README、docs 和视频脚本统一使用同一数据模型。 | 📋 |
| #185 | **Provider-neutral Copilot 配置回归**：把 Chat / Embedding provider 抽象文档化并补齐 OpenAI-compatible、Azure OpenAI、国内兼容网关、本地 Ollama / vLLM 的配置样例；Web Admin 模型选择器明确区分“平台默认模型”“自定义模型”“本地模型”。 | 📋 |
| #186 | **写入审批二阶段**：Copilot 生成写 SQL 时统一进入 staged preview，Web Admin 对 `CREATE / INSERT / UPDATE / DELETE / DROP / GRANT / REVOKE` 展示 SQL diff、影响范围和二次确认；服务端继续以权限和 `mode=read-write` 作为上限。 | 📋 |
| #187 | **Agent eval 与成本指标**：新增 Industrial Data Agent eval 场景（异常设备、慢查询、schema 建模、维修建议、写入审批），并在 Copilot 指标中记录 provider、model、tool 调用数、失败原因和近似 token 成本，便于企业按成本选择模型。 | 📋 |
| #188 | **上层平台联合样例边界**：SonnetDB 侧只提供工业边缘数据引擎、Studio、Copilot/Agent 和备份恢复的通用样例素材；具体 IoTSharp + SonnetDB 边缘节点样例迁入 IoTSharp 仓库 RD-10 维护。 | 📋 |

### 验收标准

- README 第一屏、docs 首页、`llms.txt` 和工业 AI 文档对 SonnetDB 的第一定位保持一致。
- AI / Agent 能从 `llms.txt` 找到 SQL 参考、工业应用文档、Studio / Copilot 文档和 Roadmap。
- Industrial Data Agent Demo 可以从样例数据跑到自然语言分析结果，且所有写操作都需要审批。
- Provider 文档必须说明 OpenAI-compatible 抽象、本地模型路线和不绑定单一供应商的原则。
- 本里程碑不修改 `.SDBSEG` / `.SDBWAL` / KV / Document 等二进制格式。

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
| 10 | 扩展和第三方 | #40, #41 + #42~#45 批量入库专题 | 🚧（#42~#45 ✅） |
| 11 | 写入快路径（PR #45 瓶颈收尾） | #46 ~ #49 | ✅ |
| 12 | 函数与算子扩展（PID / Forecast / UDF） | #50 ~ #57 | ✅ |
| 13 | 向量类型与嵌入式向量索引（Copilot 知识库底座） | #58 ~ #62 | ✅ |
| 14 | SonnetDB Copilot：MCP 工具 + 知识库 + 智能体 | #63 ~ #69 | ✅ |
| 15 | 地理空间类型与轨迹分析 | #70 ~ #77 | ✅ |
| 16 | Copilot 产品化升级（嵌入式 AI 助手 UX） | #78 ~ #88 | ✅ |
| 17 | 可观测性与运行时可见性（OTel + 结构化日志 + 诊断端点） | #89 ~ #98 | 📋 |
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
| MM9 | 多模型统一备份、恢复和管理工具第一批 | BackupService + sndb backup | ✅ |

**当前推进顺序**：Milestone 14（Copilot）、Milestone 15（地理空间）、Milestone 16（Copilot 产品化升级）、Milestone 20（Parity #127~#136 实现）、Milestone 21（Document Store 单机能力升级 #137~#146）、Milestone 23（搜索与向量引擎合并）与 Milestone 26（连接器路线独立化 #175~#181）均已完成或收口。**Milestone 27（Industrial Data Agent 与 AI-ready 产品化路线）** 仍是对外门面与中长期 AI 产品主线，但当前状态为**滞后**：#182 已落第一批文档，#183~#188 需要优先追赶工具契约、工业 Demo、provider-neutral、本地模型、写入审批二阶段、eval 与成本指标；同时并行推进 **Milestone 17（可观测性与运行时可见性）** 的 OTel / 结构化日志 / 诊断端点 / Copilot 服务端会话持久化，以及 **Milestone 18（VS Code 扩展）** 的 `#99 ~ #103` “远程连接 + Explorer + SQL + 结果视图”闭环。**Milestone 19（生态适配底座能力）** 只保留 SonnetDB 通用数据库能力，#109~#117 与 #122/#123 已完成；IoTSharp 专属 Profile、兼容矩阵、灰度、双写、回滚和长稳验收已迁入 IoTSharp 仓库 RD-10。后续继续推进对象治理、通用迁移/校验原语、增量索引 / 后台维护成本与大量 measurement 长稳专项。Studio 管理面进入 **Milestone 24**，MongoDB 参考 parity、长稳、容量报告和发布文档进入 **Milestone 25**。**Milestone 22（Agent Memory / Codebase Intelligence）** 重新定位为基于 SonnetDB 的上层应用 / 示例方案候选，暂停 #150~#159 内置派单；只有应用验证出通用数据库能力缺口时，才拆成独立 Core / Server / Studio PR。SonnetDBEE C5.7 / MM9 的开源核心第一批已提供 `BackupService` 和 `sndb backup create/inspect/verify/restore`，企业级定时、增量、审计和 UI 编排继续由 SonnetDBEE 承接。**Milestone 20** 后续不再按 #129 继续派单，而是通过 `.github/workflows/parity.yml`、`parity-results` 分支与 `tests/SonnetDB.Parity/reports/sample-run.md` 持续暴露能力缺口、SKIP 原因和 nightly 稳定性。


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
