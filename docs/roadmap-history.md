# ROADMAP 历史归档

本文件归档 SonnetDB 早期路线图的详细正文，避免主路线图被已完成实现细节淹没。

> 当前路线图见 [ROADMAP.md](../ROADMAP.md)。

---

## Milestone 0 — 项目脚手架 ✅

| PR | 主题 | 状态 |
|----|------|------|
| #1 | 初始化规划文档（README / CHANGELOG / ROADMAP / AGENTS） | ✅ |
| #2 | 解决方案与项目骨架（`net10.0` + Nullable + TreatWarningsAsErrors） | ✅ |
| #3 | GitHub Actions CI（ubuntu + windows 双矩阵） | ✅ |

---

## Milestone 1 — 内存与二进制基础设施（Safe-only） ✅

| PR | 主题 | 状态 |
|----|------|------|
| #4 | `SpanReader` / `SpanWriter`（基于 `BinaryPrimitives` + `MemoryMarshal`） | ✅ |
| #5 | `[InlineArray]` 工具：`InlineBytes8/16/32` + `TsdbMagic` | ✅ |
| #6 | 核心 unmanaged struct：`SegmentHeader` / `BlockHeader` / `BlockIndexEntry` / `SegmentFooter` / `WalRecordHeader` / `WalFileHeader` / `FormatSizes` | ✅ |

---

## Milestone 2 — 逻辑模型与目录 ✅

| PR | 主题 | 状态 |
|----|------|------|
| #7 | 核心数据模型：`Point` / `DataPoint` / `FieldValue` / `SeriesFieldKey` / `AggregateResult` / `TimeBucket` | ✅ |
| #8 | `SeriesKey` 规范化 + `SeriesId = XxHash64(series_key)` | ✅ |
| #9 | `SeriesCatalog` + `CatalogFileCodec`（持久化 catalog 文件） | ✅ |

---

## Milestone 3 — 写入路径 ✅

| PR | 主题 | 状态 |
|----|------|------|
| #10 | `WalWriter` / `WalReader` / `WalReplay` / `WalRecord`（Append-only WAL + CRC + 截断容忍） | ✅ |
| #11 | `MemTable` / `MemTableSeries` / `MemTableFlushPolicy` | ✅ |
| #12 | `SegmentWriter` / `SegmentBuildResult` / `ValuePayloadCodec`（不可变 `.SDBSEG`：临时文件 + 原子 rename） | ✅ |
| #13 | `Tsdb` 引擎门面 + `FlushCoordinator` + `WalTruncator` + `TsdbPaths`（写入路径闭环；崩溃恢复矩阵齐全） | ✅ |

磁盘布局（M3 落地）：
```
<root>/
  catalog.SDBCAT
  wal/active.SDBWAL
  segments/{id:X16}.SDBSEG
```

---

## Milestone 4 — 查询路径 ✅

| PR | 主题 | 状态 |
|----|------|------|
| #14 | `SegmentReader`：零拷贝只读访问 + 完整一致性校验（Magic / Version / FooterOffset / IndexCrc / BlockCrc） | ✅ |
| #15 | `SegmentIndex` / `MultiSegmentIndex` / `SegmentManager`：多段查询索引层 + 跨段时间窗剪枝 + Volatile 发布的无锁读 | ✅ |
| #16 | `QueryEngine`：MemTable + 多段 N 路堆合并 + 7 种聚合（Count/Sum/Min/Max/Avg/First/Last） + `GROUP BY time(...)` 桶聚合 | ✅ |

> 注：原计划中的 PR #15（"QueryEngine.QueryRaw"）已合并进 PR #16；新 PR #15 替换为查询所需的多段索引层。

---

## Milestone 5 — 稳定性与性能（写入侧）

| PR | 主题 | 状态 |
|----|------|------|
| #17 | 后台异步 Flush 工作线程 `BackgroundFlushWorker` + Checkpoint LSN 驱动的 WAL Replay 跳过（消除冗余回放） | ✅ |
| #18 | Size-Tiered Segment Compaction：`CompactionPlanner` / `SegmentCompactor` / `CompactionWorker` + `SegmentManager.SwapSegments` | ✅ |
| #19 | 多 WAL 滚动（segmented WAL）：`WalSegmentSet` / `WalRollingPolicy` + Legacy `active.SDBWAL` 自动升级 | ✅ |
| #20 | 删除支持（DELETE / Tombstone + WAL Delete 记录 + `tombstones.tslmanifest` + Compaction 阶段消化） | ✅ |
| #21 | Retention TTL：按策略自动注入墓碑 + 过期段直接 drop | ✅ |

磁盘布局（M5 落地后）：
```
<root>/
  catalog.SDBCAT
  tombstones.tslmanifest
  wal/{startLsn:X16}.SDBWAL             # 多 segment 滚动
  segments/{id:X16}.SDBSEG
```

---

## Milestone 6 — SQL 前端 + Tag 倒排索引

| PR | 主题 | 状态 |
|----|------|------|
| #22 | SQL 词法 / 语法分析器（递归下降，无第三方依赖；AST 节点） | ✅ |
| #23 | `CREATE MEASUREMENT` + schema 持久化 | ✅ |
| #24 | `INSERT INTO ... VALUES (...)`（含批量、TAG/FIELD 类型校验、时间戳缺省） | ✅ |
| #25 | `SELECT ... WHERE ... GROUP BY time(...)`（含 tag 过滤、聚合下推） | ✅ |
| #26 | `DELETE FROM ... WHERE time >= a AND time <= b`（落到 PR #20 的 Tombstone） | ✅ |
| #27 | Tag 倒排索引：`(tagKey, tagValue) → [SeriesId]`（加速 WHERE tag=...） | ✅ |
| #28 | 按照标准的ADO.NET  API, 实现`SndbConnection / SndbCommand / SndbDataReader`等等。 | ✅ |

---

## Milestone 7 — 压缩编码

| PR | 主题 | 状态 |
|----|------|------|
| #29 | 时间戳 Delta-of-Delta 编码（block payload V2，向后兼容 V1） | ✅ |
| #30 | 数值列 Gorilla / XOR 编码（Double） + RLE（Bool） + 字典（String） | ✅ |
| #31 | 块级压缩开关与统计：在 `SegmentWriter.Options` 暴露编码选择，`SegmentReader` 自动按 `BlockEncoding` 解码 | ✅ |

> 注：BlockEncoding 字段 PR #6 已预留；本 Milestone 真正启用 Delta / Gorilla。

---

## Milestone 8 — 服务器模式

> **进入条件**：PR #21（Retention TTL）与 PR #35（嵌入式模式 BenchmarkDotNet 基准）必须先完成，建立性能基线后再服务化，避免后续无法归因 HTTP / 序列化 / 引擎本身的性能差异。

### 设计要点（PR #32）

- **运行时形态**：AOT-friendly Minimal API（`net10.0` + `PublishAot=true`），单进程；项目位于 `src/SonnetDB/`，引用 `SonnetDB`。
- **多租户隔离**：进程内 `ConcurrentDictionary<string, Tsdb>` 注册表，一个数据库 = 一个子目录 + 一个 `Tsdb` 实例，复用现有 `BackgroundFlushWorker` / `CompactionWorker`。`CREATE DATABASE <name>` 创建子目录并注册；`DROP DATABASE` 通过引用计数 + `Dispose` 回收。**不**采用子进程隔离（与 AOT min api 风格冲突且开销过大）。
- **协议（仅 HTTP，先不做 WebSocket）**：
  - `POST /v1/db/{db}/sql` 提交单条 SQL；请求体 `application/json`，包含 `sql` 与可选 `parameters`。
  - `POST /v1/db/{db}/sql/batch` 批量 INSERT（直接走 ADO.NET 的 batch 接口，零额外解析）。
  - 结果集采用 **`application/x-ndjson` 流式输出**，配合 `System.Text.Json` AOT source generator，避免 JIT 反射 + 全量缓冲；这是 AOT + 大结果集场景下吞吐最佳的组合。
  - WebSocket 推后评估：仅当出现真实的"订阅 / 长查询流"需求再加（订阅语义本身需要引擎层支持，目前不具备）。
- **认证（极简）**：单层 `Authorization: Bearer <token>` + 配置文件里的静态 token 列表（角色仅 `admin` / `readwrite` / `readonly`）。不实现 SQL 级 `CREATE USER / GRANT`，避免引入控制面元数据。等到真有多用户场景再升级。
- **可观测性**：`/healthz`、`/metrics`（Prometheus 文本格式，最少包含 per-db 写入速率、Flush/Compaction 次数、活跃 segment 数）。

### PR 列表

| PR | 主题 | 状态 |
|----|------|------|
| #32 | `SonnetDB`：AOT Minimal API + 多 `Tsdb` 实例注册表 + `POST /v1/db/{db}/sql` + ndjson 流式结果 + Bearer token 三角色认证 + `/healthz` + `/metrics` | ✅ |
| #33 | 远端 ADO.NET 客户端 `SonnetDB.Data`：与 PR #28 共享 `SndbConnectionStringBuilder`，通过 scheme（`sonnetdb://` 本地、`sonnetdb+http://` 远程）切换实现，结果集流式反序列化 | ✅ |
| #34a | 服务端控制面：用户/权限存储 + SQL DDL（CREATE/ALTER/DROP USER、GRANT/REVOKE、CREATE/DROP DATABASE）+ `POST /v1/auth/login` 颁发动态 token + Bearer 中间件接入 `UserStore` | ✅ |
| #34b | Vue3 管理后台（Naive UI）：登录页、数据库列表/状态、SQL 控制台、用户/权限/Token 管理 | ✅ |
| #34c | 实时推送：基于 SSE 的指标 / 慢查询 / 数据库事件流，前端订阅自动刷新 | ✅ |


---

## Milestone 9 — 性能与发布

| PR | 主题 | 状态 |
|----|------|------|
| #35 | BenchmarkDotNet：写入 / 查询 / 聚合 / Compaction 基准，编写评测 InfluxDB TDengine SQLite SonnetDB  SonnetDB 等五个时序数据库的各项指标对比， 并连同机器性能都写在readme.md 里面。  | ✅|
| #36 | `SonnetDB` Docker 性能测试：补齐 `src/SonnetDB/Dockerfile`、基准用 `docker-compose` 环境、`ServerBenchmark`（写入 / 查询 / 聚合）与 README 中的服务端性能基线。 | ✅ |
| #37a | 文档完善（已落地部分）：重写 `README.md` / `README.en.md`，补齐 `docs/getting-started.md` / `docs/data-model.md` / `docs/sql-reference.md` / `docs/file-format.md` 及发布文档，使用 JekyllNet 构建内置 `/help` 帮助站点，核对路线图与当前代码/功能，清理过时说明。 | ✅ |
| #37b | 文档发布：将 JekyllNet 文档站点接入 GitHub Pages 自动构建与发布流水线，支持从同一套 `docs/` 源码同时产出服务端 `/help` 站点与 Pages 静态站点，避免文档仅内置在 `SonnetDB` 镜像中。 | ✅ |
| #38 | 发布 NuGet 包 `SonnetDB 0.1.0` + `.github/workflows/publish.yml`，打包生成一套包含 `SonnetDB`、`SonnetDB.Data`、`SonnetDB.Cli` 的 SDK Bundle，并附带使用说明；发布 Windows 和 Linux 版本；再打包 `SonnetDB` 完整 Bundle，包含前端、`SonnetDB.Cli`、`SonnetDB.Data` 等，能够一键启动；同时生成 Windows `msi` 与 Linux `deb` / `rpm` 安装包。 | ✅ |
| #39 | Docker 服务端模式镜像自动发布：新增 GitHub Actions 工作流，自动构建并推送 `SonnetDB` 镜像到 `iotsharp/sonnetdb` 与 `ghcr.io/<owner>/sonnetdb`，补齐标签策略、运行说明与 Secrets 要求。 | ✅ |
---
## Milestone 10 — 批量入库快路径（原扩展和第三方占位已拆分）

| PR | 主题 | 状态 |
|----|------|------|
| #40 | **SonnetDB for VS Code（Epic）**：官方 VS Code 数据库扩展，支持连接远程 SonnetDB Server、浏览 schema、执行 SQL、查看结果、接入 Copilot，并在后续支持“托管本地 SonnetDB Server 打开 data root”；详细 PR 拆分见主路线图 Milestone 18（#99 ~ #108）。 | ↪ 已拆入 M18 |
| #41 | SonnetDB MQTT 接入：原“后台订阅 MQTT 消息”占位已并入 Milestone 28 P5b 的 MQTT broker/client 接入设计（#242 / #243），统一处理内建 broker、订阅外部 broker、路由落库与传输兼容。 | ↪ 已并入 M28 P5b |
| #42 | 批量入库快路径核心库 `SonnetDB.Ingest`：协议嗅探（Detector）+ 三协议 reader（LineProtocol / JSON / Bulk INSERT VALUES）+ `BulkIngestor` 统一消费入口（ArrayPool 8192 批 → `Tsdb.WriteMany`，支持 FailFast/Skip 与可选 FlushOnComplete）。绕开每条 INSERT 的 SQL Lexer→Parser→Planner 开销，为大批量写入提供基础；`src/SonnetDB` 仍保持零第三方运行时依赖。 | ✅ |
| #43 | `SonnetDB.Data` 接入：`SndbCommand.CommandType = CommandType.TableDirect` 走批量入库快路径；`IConnectionImpl.ExecuteBulk` + `EmbeddedConnectionImpl` 桥接 `Tsdb.Measurements` 的 schema 到 `BulkValuesReader` 的列角色 resolver；嵌入式连接零拷贝直达 `BulkIngestor`。 | ✅ |
| #44 | `SonnetDB` 远程批量端点：`POST /v1/db/{db}/measurements/{m}/lp\|json\|bulk` 三个端点 + `RemoteConnectionImpl.ExecuteBulk`；保留 SQL 路径不变。 | ✅ |
| #45 | 批量入库基准：在 `SonnetDB.Benchmarks` 新增 `BulkIngestBenchmark`，对比 SQL INSERT 单点 / TableDirect LP / TableDirect JSON / TableDirect Bulk VALUES，刷新 README 写入吞吐对比表。 | ✅ |

---
## Milestone 11 — 写入快路径（PR #45 瓶颈收收尾）

> **背景**：PR #45 实测发现 100k 点下嵌入式 LP/JSON/Bulk 三路与 SQL VALUES baseline 吞吐几乎打平（~170–200ms），内存仅节省 25～42%；
> 服务端 `/sql/batch` 1M 点 ~21s vs 嵌入式 0.62s，差 33.8×。调用链详细剖析定位出三个主要瓶颈：
> 1. `Tsdb.WriteMany(IEnumerable<Point>)` 是假批量，逐点 lock + 逐字段 WAL record；
> 2. 服务端 LP/Bulk payload `Encoding.UTF8.GetString` + `JsonPointsReader.ToArray()` 二次拷贝；
> 3. 端点默认 `flush=true` 同步落盘占用 RTT。
>
> **目标**：嵌入式 100k 点 ≤ 80ms（1M 点 ≤ 300ms），服务端 LP/Bulk 达到 ≥ 700k pts/s。

| PR | 主题 | 状态 |
|----|------|------|
| #46 | **引擎真批量**（已落地，最小切片）：`Tsdb.WriteMany(ReadOnlySpan<Point>)` 整批仅取一次 `_writeSync` 锁、批末仅 `Signal` 一次；`WriteMany(IEnumerable<Point>)` 自动嗅探 `Point[]` / `List<Point>` / `ArraySegment<Point>` 下沉到 span 重载。**WAL 记录格式与 `FileHeader.Version` 保持不变**（向后兼容；`WalRecordType.WriteBatch` 实测 ROI 偏低，留给后续按需追加）。`BulkIngestor`、三端点、`RemoteConnectionImpl` 自动受益。基准（100k 点）：Mean 持平、**Allocated −42~58%**。 | ✅ |
| #47 | **服务端 + Reader 零拷贝**：`BulkIngestEndpointHandler.ReadAllAsync` 改 `ArrayPool<byte>` 租借（精确长度优先，未知则翻倍扩容），消除 LOH；`JsonPointsReader` 字段重构为 `ReadOnlyMemory<byte> _utf8Memory + byte[]? _pooledBuffer`，ROM ctor 零拷贝持有 caller buffer，string ctor 走 ArrayPool；`BulkIngestEndpointHandler.HandleAsync` JSON 直接喂 `ReadOnlyMemory<byte>`，LP 走 `ArrayPool<char>` rent + `Encoding.UTF8.GetChars`，BulkValues 用精确长度 `GetString(buffer,0,length)`；三端点追加 `DisableRequestSizeLimitAttribute` 解除 Kestrel 30MB 上限。**基准（1M 点 / 本地 dotnet run）**：LP `1.20s / 52MB`、JSON `1.20s / 71MB`、Bulk `1.10s / 34MB`、`/sql/batch` `5.09s / 668MB`，三端点 ~17–19× faster vs PR #45 baseline、alloc −89~95%。Reader 接口仍保 `ROM<char>` / `string`，byte 化留作未来独立 PR。 | ✅ |
| #48 | **端点 flush 三档位**：`?flush=false\|true\|async`，默认 `false`（最快，仅入 MemTable+WAL）；`async` 走新 `Tsdb.SignalFlush()` 仅向 `BackgroundFlushWorker.Signal()` 发信号后立即返回（未启用后台 Flush 时降级为同步 `FlushNow`）；`true|sync|yes|1` 保持同步 `FlushNow`。新增 `BulkFlushMode { None, Async, Sync }` 枚举与 `BulkIngestor.Ingest` 新主重载（旧 `bool flushOnComplete` 重载向后兼容）。`BulkIngestEndpointHandler.ParseFlush` + ADO `EmbeddedConnectionImpl.ParseFlushMode` 同步解析；`RemoteConnectionImpl` 自然透传 query string。补齐三档位 × 三端点端到端 + BulkIngestor 直测，全量回归 1241 + 97 通过。 | ✅ |
| #49 | **基准刷新 + 对外对比**（写入快路径专题收尾）：
 - ✅ README 「写入：100 万点」表新增 PR #47 服务端 LP/JSON/Bulk 三行（1.10–1.20 s / 34–71 MB / ~1.77–1.93× vs 嵌入式）；
 - ✅ README 「嵌入式 vs SonnetDB」同机对比表拆分为 SQL Batch + LP/JSON/Bulk 四行；
 - ✅ README 「批量入库快路径」补充 PR #48 `?flush=` 三档位表（None / Async / Sync 语义与适用场景）；
 - ✅ 新增 `InsertBenchmark.TDengine_InsertSchemaless_1M` + `TDengineRestClient.WriteLineProtocolAsync(db, lp, precision)`，走 TDengine InfluxDB-compat `POST /influxdb/v1/write?precision=ms`，按 100k 行/批切片；
 - ✅ 全量重跑 **24 个基准**（i9-13900HX / .NET 10.0.6 / Docker WSL2，~20 分钟）并把真实数字写进 `tests/SonnetDB.Benchmarks/README.md`：Insert SonnetDB **545 ms / 530 MB**、SQLite 811 ms / 465 MB、InfluxDB 5,222 ms / 1,457 MB（9.58×）、TDengine REST 44,137 ms / 156 MB（81×）、**TDengine schemaless LP 996 ms / 61 MB（1.83×）**〔同库 schemaless 比 REST INSERT 子表路径快 44× / 分配缩到 39%〕；Query SonnetDB 6.71 ms、Aggregate 42.3 ms、Compaction 16.3 ms；
 - ✅ 重建 `iotsharp/sonnetdb:bench` 镜像后首次跑通 **ServerInsertBenchmark 全部 4 个路径**：SQL Batch `19.80 s / 655 MB`、LP `1.293 s / 52 MB`、JSON `1.352 s / 71 MB`、Bulk `1.120 s / 34 MB`——PR #47 三端点稳定进入「秒级 1M 点 + ≤ 80 MB 分配」区间，比 SQL Batch 快 15–7×、分配缩到 5–11%，比嵌入式仅多 ~2.0–2.5×额外开销。 | ✅ |

**推进顺序**：PR #46 ✅ → PR #47 ✅ → PR #48 ✅ → PR #49 ✅。Milestone 11 「写入快路径」专题完整收尾：嵌入式写入达到 ~1.83 M pts/s（545 ms / 1M 点）；服务端三端点 LP/JSON/Bulk 全部重跑通过后仍保持 1.12–1.35 s / 34–71 MB，远超 Milestone 11 原定 ≥ 700k pts/s 目标；对外同机粗略对比表明 SonnetDB 写入比 InfluxDB 快 **9.6×**、比 TDengine REST INSERT 快 **81×**、比 TDengine schemaless LP 快 **1.83×**，范围查询比 InfluxDB 快 **61×**、比 SQLite 快 **6.6×**。

---

## Milestone 12 — 函数与算子扩展（PID / Forecast / UDF）

> **背景**：当前 `Aggregator` 是 `enum`（7 个内置聚合）+ `AggregateResult` 单累加器结构，已经无法承载 stddev / percentile / derivative / PID / forecast 等函数族。本里程碑把"函数"提升为一等公民，引入 `FunctionRegistry` + `IAggregateFunction` + `WindowOperator` + `TableValuedFunction` 四类扩展点，并以 **PID 与 Forecast** 作为首批内置示例，建立 SonnetDB 在工业 / IoT / 可观测性场景的差异化能力。
>
> **设计原则**：
> 1. **零破坏**：现有 7 个聚合迁移到 `IAggregateFunction`，对外 SQL / ADO.NET / Server 行为不变。
> 2. **贴合现有架构**：复用 `AggregateResult.Merge` 的 mergeable accumulator 模型，与 MemTable + N 路堆合并 + 跨段聚合天然兼容。
> 3. **AOT 友好**：内置函数 `sealed class` + 静态注册，零反射，与 `SonnetDB` 的 `PublishAot=true` 路线兼容。
> 4. **Dogfooding**：PID / Forecast 自身使用 UDF 接口实现，验证 API 设计合理性。

### Tier 划分

| Tier | 主题 | 代表函数 |
|------|------|----------|
| 1 | 标量 / 逐点函数 | `abs` `round` `sqrt` `log` `coalesce` `case when` `cast` `time_bucket` `date_trunc` `extract` |
| 2 | 扩展聚合 | `stddev` `variance` `percentile` `p50/p90/p95/p99` `median` `mode` `spread` `distinct_count(HLL)` `tdigest_agg` `histogram` |
| 3 | 时序窗口算子 | `derivative` `non_negative_derivative` `difference` `integral` `moving_average` `ewma` `cumulative_sum` `rate` `irate` `increase` `delta` `holt_winters` `interpolate` `fill` `locf` `state_duration` `state_changes` |
| 4 | 控制与预测 | `pid(value, setpoint, kp, ki, kd)` `pid_series(...)` `forecast(...)` `anomaly(...)` `changepoint(...)` `dtw_distance(...)` |
| 5 | UDF 扩展点 | `RegisterScalarFunction` `RegisterAggregateFunction` `RegisterTableValuedFunction` |

### PR 列表

| PR | 主题 | 状态 |
|----|------|------|
| #50 | **`FunctionRegistry` + `IAggregateFunction` 基础设施**：新增 `src/SonnetDB/Query/Functions/`，定义 `IAggregateFunction` / `IAggregateState` / `FunctionRegistry`；把现有 7 个聚合（Count/Sum/Min/Max/Avg/First/Last）迁移为内置实现；保留 `enum Aggregator` 作为内部 fast-path 兼容层；现有 SQL / ADO.NET / Server / Benchmark 行为完全不变 | ✅ |
| #51 | **Tier 1 标量函数 + SQL 函数调用表达式**：SQL Parser/AST 增加 `FunctionCallExpr`，binder 阶段查 `FunctionRegistry` 区分 标量 / 聚合 / 窗口 / TVF；落地数学 / 时间 / 逻辑 / `cast` / `time_bucket` / `date_trunc` / `extract` 等 ~20 个标量函数 | ✅ |
| #52 | **Tier 2 扩展聚合**：`stddev` `variance` `percentile/p50/p90/p95/p99` `median` `mode` `spread` `distinct_count(HLL)` `tdigest_agg` `histogram`；`tdigest` 与 `HLL` 必须实现可合并 `Merge`，跨段聚合 / `GROUP BY time(...)` 桶聚合一致 | ✅ |
| #53 | **Tier 3 窗口算子框架**：新增 `src/SonnetDB/Query/Window/WindowOperator`，支持基于点数 N 和基于时间 `RANGE INTERVAL` 的滑动窗口；落地 `derivative` `non_negative_derivative` `difference` `integral` `moving_average` `ewma` `cumulative_sum` `rate` `irate` `increase` `delta` `holt_winters` `interpolate` `fill` `locf` `state_duration` `state_changes` | ✅ |
| #54 | **PID 内置函数 + 参数估算 + 控制回写示例**：聚合形态 `pid(value, setpoint, kp, ki, kd)` 在 `GROUP BY time(...)` 桶内输出最终 u(t)；行级窗口形态 `pid_series(...)` 输出每行 u(t) 用于回测；状态结构 `{ integral, prevError, prevTimeMs }`，跨段 `Merge` 按时间序拼接；**新增 `PidParameterEstimator.Estimate`（纯 C# 阶跃响应辨识）**：基于 Sundaresan & Krishnaswamy 35%/85% 两点法拟合 FOPDT 模型，支持 Ziegler-Nichols / Cohen-Coon / Skogestad IMC 三种整定规则，直接从历史时序数据推算 Kp / Ki / Kd；当前先以嵌入式/库级 API 交付，后续再接入 `FunctionRegistry` 暴露为 SQL 可查询函数；新增 `docs/pid-control.md` 端到端教程 + `INSERT … SELECT pid_series(...)` 控制回写示例 | ✅ |
| #55 | **Forecast TVF + 异常 / 变点检测**：表值函数 `forecast(measurement, field, horizon, 'algo'[, season])` 内置 **线性外推 + Holt-Winters**（纯 C#，无外部依赖），返回 `(time, value, lower, upper, ...tags)`；`anomaly(x, 'zscore|mad|iqr', threshold)` `changepoint(x, 'cusum'[, drift])`；ARIMA / Prophet 留给 UDF；新增 `docs/forecast.md` | ✅ |
| #56 | **UDF 注册 API**：`Tsdb.Functions.RegisterScalar(name, Func<...>)` / `RegisterAggregate(IAggregateFunction)` / `RegisterWindow(IWindowFunction)` / `RegisterTableValuedFunction(...)`；嵌入式默认启用，Server 端默认禁用 UDF（仅内置函数）以保证 AOT；新增 `docs/extending-functions.md` | ✅ |
| #57 | **函数基准 + README 函数支持矩阵**：在 `tests/SonnetDB.Benchmarks` 扩展 `AggregateBenchmark`，对比 InfluxDB `derivative` / `holt_winters`、Timescale `time_weight`、TDengine `forecast`；README 新增「支持的 SQL 函数」矩阵表 | ✅ |

### SQL 用法预览

```sql
-- Tier 2：扩展聚合
SELECT time_bucket('1m', time) AS minute,
       avg(usage), p95(usage), stddev(usage), spread(usage)
FROM cpu WHERE host = 'server-01' AND time > now() - 1h
GROUP BY minute;

-- Tier 3：速率与平滑
SELECT time, host,
       rate(bytes_in, 1s) AS bps,
       ewma(temperature, 0.2) AS temp_smooth
FROM nic WHERE time > now() - 5m;

-- Tier 4：PID 控制律回写
INSERT INTO actuator (time, device, valve)
SELECT time, device,
       pid_series(temperature, 75.0, 0.6, 0.1, 0.05) AS valve
FROM reactor WHERE time > now() - 1m;

-- Tier 4：预测
SELECT * FROM forecast(
    (SELECT time, value FROM meter WHERE device='m1' AND time > now()-7d),
    horizon => 1440, algo => 'holt_winters', season => 1440);
```

### 嵌入式 UDF 注册预览（Tier 5）

```csharp
using var db = Tsdb.Open(new TsdbOptions { RootDirectory = "./data" });

db.Functions.RegisterScalar("c2f",
    args => FieldValue.Float64(args[0].AsDouble() * 9 / 5 + 32));

db.Functions.RegisterAggregate(new KalmanAggregate()); // 实现 IAggregateFunction
```

**推进顺序**：PR #50 → #51 → #52 → #53 → #54 → #55 → #56 → #57。
其中 PR #50 是基础设施重构，必须先合并；PR #54 / #55 / #56 是对外差异化卖点，建议在 Milestone 9（发布）完成后立刻推进。

---

## Milestone 13 — 向量类型与嵌入式向量索引（Copilot 知识库底座）

> **背景**：Milestone 14 的 SonnetDB Copilot（智能体）需要一个"零外部依赖"的向量召回能力。我们已经决定 **dogfooding——把向量库做到 SonnetDB 自己里**，而不是引入 SQLite/sqlite-vec / Qdrant 等外部组件。这样既能复用 WAL/Segment/Compaction 的存储栈，也能成为 SonnetDB 的对外差异化能力（"时序 + 向量"二合一）。
>
> **设计原则**：
> 1. **Safe-only 仍生效**：首版距离计算保持 `unsafe`-free；当前以安全的 `Span<float>` / `for` 循环实现，后续可在不破坏 Safe-only 的前提下演进到 `System.Numerics.Tensors.TensorPrimitives` 等 SIMD 加速路径。
> 2. **零破坏**：新增 `VECTOR(dim)` 字段类型，复用 `FieldValue` 的 union 结构（新增 `Vector` 分支），现有 schema / 写入 / 查询路径保持兼容。
> 3. **AOT 友好**：内置距离函数 + 索引算子均为 `sealed class`，不引入反射或动态代码生成。
> 4. **可演进**：第一版用 brute-force 顺扫 + 段内裁剪，足够覆盖 Copilot 知识库（< 50k 切片）；HNSW 留到 PR #61 按需追加。

### PR 列表

| PR | 主题 | 状态 |
|----|------|------|
| #58 | **`VECTOR(dim)` 数据类型 + 编解码**：`FieldValue` 新增 `Vector` 分支（`ReadOnlyMemory<float>` + dim 校验）；`SegmentWriter` / `SegmentReader` 新增 `BlockEncoding.VectorRaw`（dim×4 字节定长）；schema 在 `CREATE MEASUREMENT` 中支持 `embedding VECTOR(384)` 列；INSERT 支持 `[0.1,0.2,...]` 字面量与参数化 `float[]`；`SegmentFormatVersion` 升级到 v3 并保留对 v2 的只读回退。<br/>**进度**：a) `FieldType.Vector` + `FieldValue.Vector` + WAL `WritePoint` 编解码 ✅；b) Schema VECTOR(dim) 列 + SQL 字面量 ✅；c) `BlockEncoding.VectorRaw` + Segment Header v3 升级 ✅。 | ✅ |
| #59 | **向量距离函数（Tier 1 标量 + Tier 2 聚合）**：实现 `cosine_distance(a,b)` `l2_distance(a,b)` `inner_product(a,b)` `vector_norm(a)`；新增聚合 `centroid(vec)`（按维度求均值，可合并）；`SqlParser` 支持 `<=>` `<->` `<#>` 三个 PostgreSQL/pgvector 兼容运算符（解析为对应函数调用） | ✅ |
| #60 | **`KNN` 表值函数 + brute-force 召回**：新增 `knn(measurement, column, query_vector, k[, metric])` TVF，返回 `(time, distance, ...tags, ...fields)`；`KnnExecutor` 对 MemTable + 全量 Segment 做段级时间窗剪枝后的顺扫，使用 `Parallel.ForEach` 并行扫描候选序列并在最终阶段按距离升序取 Top-K；`WHERE` 支持 tag 等值过滤与时间范围过滤；`docs/vector-search.md` 给出端到端用法示例 | ✅ |
| #61 | **HNSW 段内 ANN 索引（可选构建）**：`SegmentWriter` 在 flush/compaction 阶段对 `VECTOR` 列可选构建 HNSW 图（`SDBVIDX` 边表 sidecar 文件，不污染 `.SDBSEG`）；`SegmentReader` 检测到 `.SDBVIDX` 自动启用 ANN，否则降级为 brute-force；通过 `CREATE MEASUREMENT (... embedding VECTOR(384) WITH INDEX hnsw(m=16, ef=200))` 声明 | ✅ |
| #62 | **向量基准 + 对比**：`tests/SonnetDB.Benchmarks` 新增 `VectorRecallBenchmark`，默认覆盖 `10k / 100k` 384-dim 向量的 brute-force 顺扫 vs HNSW 延迟回归，并通过环境变量显式开启 `1M` 长测档位；README 已回填 SonnetDB 自身实测耗时，并为 `sqlite-vec`、`pgvector`（IVF/HNSW）预留同机粗略对比结果区 | ✅ |

**推进顺序**：PR #58 ✅ → #59 ✅ → #60 ✅ → #61 ✅ → #62 ✅。Milestone 13 的向量检索前置已闭环；后续若具备合适的长测 / 外部数据库环境，可继续补 `1M` 与 `sqlite-vec` / `pgvector` 结果，但不再阻塞 Milestone 14。

---

## Milestone 14 — SonnetDB Copilot：MCP 工具 + 知识库 + 智能体

> **背景**：当前服务端 `/mcp/{db}` 已经暴露只读 MCP 工具（`query_sql` / `list_measurements` / `describe_measurement`）+ 三个 schema/stats 资源。在此之上，我们要构建一个**真正能"对话操作 SonnetDB"的智能体**，目标是让用户用自然语言完成"看 schema → 写 SQL → 解释结果 → 调优 / 排错 / 预测"全链路。
>
> **架构总览**：
> ```
> [Web Admin Chat / 第三方 MCP Host]
>           │
>           ▼
>     SonnetDB（命名空间 SonnetDB.Copilot，Microsoft Agent Framework）
>           │
>     ┌─────┼──────────────────────────┐
>     ▼     ▼                          ▼
> Skills 库   Knowledge 检索      MCP Tool 调用
> (剧本/Prompt) (向量召回 ← M13)    (本进程内复用 MCP 工具)
>           │
>           ▼
>     SonnetDB Engine（Tsdb / SQL / Schema）
> ```
>
> **设计原则**：
> 1. **Agent SDK = Microsoft Agent Framework**（与 .NET 10 / AOT 生态原生契合，可直接 host MCP client）。
> 2. **知识库存储 = SonnetDB 自身**（依赖 Milestone 13 的 `VECTOR` + `knn(...)`，自我 dogfooding）。
> 3. **嵌入模型多供应商兼容**：
>    - **本地 ONNX**（默认 `bge-small-zh-v1.5` int8，~30 MB，CPU 30 ms / 句）——离线/内网/隐私优先。
>    - **OpenAI 兼容端点**——同一套 `IEmbeddingProvider` 接口，URL + Key + Model 即可切换；天然支持"国际版"（OpenAI / Azure OpenAI）和"国内版"（DashScope / 智谱 GLM / 月之暗面 Moonshot / DeepSeek / SiliconFlow / 火山方舟 等任何 OpenAI-compat 网关）。
>    - 配置 `SonnetDBServer__Copilot__Embedding__Provider = local|openai`，`Endpoint` / `ApiKey` / `Model` 三件套；**对话模型走同一抽象**，复用同一套 provider 切换逻辑。
> 4. **零破坏**：内容放在 ` SonnetDB.Copilot` 命名空间下， 不新增项目， 不污染 `SonnetDB.Core`；服务端默认启用。
> 5. **技能库 = 文件系统 + 前置语义召回**：`copilot/skills/*.md`（带 frontmatter `description` / `triggers`），第一轮根据用户问题做向量召回，命中后再加载到上下文。

### PR 列表

| PR | 主题 | 状态 |
|----|------|------|
| #63 | **`SonnetDB.Copilot` 命名空间骨架 + Embedding 抽象**（不新建项目，代码放入现有 `SonnetDB.Core` / `SonnetDB.Server`；引用 `Microsoft.Agents.AI` / `Microsoft.Extensions.AI` / `Microsoft.ML.OnnxRuntime`）；定义 `IEmbeddingProvider` / `IChatProvider` 抽象 + `LocalOnnxEmbeddingProvider`（bge-small-zh）+ `OpenAICompatibleEmbeddingProvider`（含 `OpenAICompatibleChatProvider`）；`SonnetDBServer__Copilot__*` 配置节 + DI 装配；`/healthz` 暴露 Copilot ready 标志；不接入任何业务流程 | ✅ |
| #64 | **文档摄入管线 + Knowledge 库**：新建 `Tsdb` 内嵌系统库 `__copilot__`，自动建表 `docs(time, source TAG, section TAG, title TAG, content STRING, embedding VECTOR(384))`；`DocsIngestor` 扫描 `docs/*.md` + `web/admin/help/`，按 H2/H3 切片（≤ 800 字 / 100 字 overlap）→ 嵌入 → 批量入库；CLI `sndb copilot ingest --root ./docs` 与服务端启动时自动增量同步（按文件 mtime 判定）；提供 MCP tool `docs_search(query, k)` | ✅ |
| #65 | **技能库 + 技能路由**：新增 `copilot/skills/*.md`（首批：`query-aggregation` / `pid-control-tuning` / `forecast-howto` / `troubleshoot-slow-query` / `schema-design` / `bulk-ingest`），frontmatter 含 `name` / `description` / `triggers` / `requires_tools`；`SkillRegistry` 启动时把每个技能 `description + triggers` 嵌入到 `__copilot__.skills`；新增 MCP tool `skill_search(query, k)` / `skill_load(name)`；技能加载后被 Agent 注入 system prompt | ✅ |
| #66 | **Schema 工具增强 + 抽样工具**：在现有 MCP 工具基础上补齐 `list_databases()` / `sample_rows(measurement, n=5)` / `explain_sql(sql)`（返回估算扫描段数 / 行数）；schema 工具结果加入 30s 内存缓存；所有新工具同样接入 `GrantsStore` 数据库级权限 | ✅ |
| #67 | **Agent Host：单轮问答闭环**：`CopilotAgent`（基于 Microsoft Agent Framework）= Embedding Provider + Chat Provider + MCP tools + Skills + Docs；HTTP 端点 `POST /v1/copilot/chat`（NDJSON 流式 SSE）+ `/v1/copilot/chat/stream`；最小回路：用户问题 → 召回 skills + docs → 选 tools → 执行 → 回答 + citations；Bearer 鉴权 + 数据库级 read 权限校验 | ✅ |
| #68 | **多轮 + 自我纠错 + Web Admin 集成**：Agent 支持多轮 history（按 token 预算裁剪）；SQL 执行失败时把 `SqlExecutionException` 反馈给模型让其改写（最多 3 轮）；`web/admin/` 新增 Chat Tab（Naive UI 流式渲染 + skill/citation 折叠展示 + 一键复制 SQL 到控制台执行） | ✅ |
| #69 | **Eval 套件 + 回归基准**：在现有 `tests/SonnetDB.Test` 下新增 `Copilot/` 目录，添加 30~50 个标准问答（schema 查询 / 聚合 / 时间过滤 / PID / forecast / 排错），用 `pytest-agent-evals` 风格的 .NET 实现：accuracy（SQL 等价/结果等价）、latency、citation 命中率三个指标；CI 中 nightly 运行（不阻塞主 CI）；README 新增"Copilot 能力矩阵"表 | ✅ |

### 配置预览

```jsonc
// appsettings.json
"SonnetDBServer": {
  "Copilot": {
    "Enabled": true,
    "Embedding": {
      "Provider": "local",                    // local | openai
      "LocalModelPath": "./models/bge-small-zh-v1.5-int8.onnx",
      "Endpoint": "https://api.openai.com/v1",
      "ApiKey": "${OPENAI_API_KEY}",
      "Model": "text-embedding-3-small"
    },
    "Chat": {
      "Provider": "openai",                   // openai
      "Endpoint": "https://dashscope.aliyuncs.com/compatible-mode/v1",
      "ApiKey": "${DASHSCOPE_API_KEY}",
      "Model": "qwen-max"
    },
    "Docs": { "AutoIngestOnStartup": true, "Roots": [ "./docs", "./web/admin/help" ] },
    "Skills": { "Root": "./copilot/skills" }
  }
}
```

### 推进顺序

PR #63（骨架 + Provider 抽象）→ #64（文档摄入）→ #65（技能库）→ #66（工具增强）→ #67（Agent 单轮）→ #68（多轮 + Web）→ #69（Eval）。
**前置依赖**：Milestone 13 的 PR #58 / #59 / #60 至少需要先合并，PR #64 才能在 SonnetDB 自身上落库。

---

## Milestone 15 — 地理空间类型与轨迹分析

> **背景**：IoT / 车联网 / 户外运动等场景大量产生带时间戳的经纬度序列（轨迹）。SonnetDB 已有时序存储底座与 SQL 函数扩展能力（Milestone 12），在此之上引入原生 `GEOPOINT` 字段类型，可以用一句 `INSERT` 写入轨迹点、一句 `SELECT` 做地理围栏过滤或总里程聚合，并在 Web Admin 地图页上实时回放。
>
> **设计原则**：
> 1. **新增 `GEOPOINT` 字段类型**（纬度 lat + 经度 lon，各 8 字节 float64，little-endian），存为 `FieldType.GeoPoint = 6`；`BlockEncoding.GeoPointRaw` = 16 字节定长编码，`SegmentFormatVersion` 升级到 v4，保留对 v3 只读回退。
> 2. **轨迹 = 带时间戳的 GEOPOINT 序列**，无需新增专用存储层——直接用现有 Measurement + time 列建模即可（`CREATE MEASUREMENT vehicle (time, device TAG, position GEOPOINT, altitude FLOAT64)`）。
> 3. **Safe-only 继续遵守**：Haversine 等地理计算全部用普通 C# double 运算；可选 `System.Numerics.Tensors.TensorPrimitives` 做 SIMD 批量距离计算（向量化 lat/lon 数组）。
> 4. **零第三方运行时依赖**：不引入 NetTopologySuite / GeoJSON.Net；GeoJSON 序列化在服务端 JSON 层手写 `GeoJsonConverter`（~100 行）。
> 5. **UI 地图层零破坏**：Vue3 Web Admin 新增独立"轨迹地图"标签页，复用现有 Naive UI 框架 + MapLibre GL（前端 npm 依赖，不影响 core 库）。

### PR 列表

| PR | 主题 | 状态 |
|----|------|------|
| #70 | **`GEOPOINT` 数据类型 + 编解码**：`FieldType.GeoPoint = 6`；`FieldValue` 新增 `GeoPoint` 分支（`struct { double Lat; double Lon }`）；`BlockEncoding.GeoPointRaw`：lat(8) + lon(8) = 16 字节定长，little-endian；`SegmentFormatVersion` v4，保留 v3 只读回退；WAL 编解码 round-trip；SQL `POINT(lat, lon)` 字面量 + 参数化 `GeoPoint` 结构体；`lat(field)` / `lon(field)` 标量提取函数 | ✅ |
| #71 | **地理空间标量函数（Tier 1）**：`geo_distance(p1,p2)→FLOAT64`（Haversine，米）、`geo_bearing(p1,p2)→FLOAT64`（方位角 0–360°）、`geo_within(p,lat,lon,radius_m)→BOOLEAN`（圆形围栏）、`geo_bbox(p,lat_min,lon_min,lat_max,lon_max)→BOOLEAN`（矩形框）、`geo_speed(p1,p2,elapsed_ms)→FLOAT64`（m/s）；`ST_Distance` / `ST_Within` / `ST_DWithin` 作为 PostGIS 兼容别名；`FunctionRegistry` 注册 | ✅ |
| #72 | **轨迹聚合函数（Tier 2）**：`trajectory_length(position)→FLOAT64`（累加 Haversine 总路程，可合并 Merge）、`trajectory_bbox(position)`（轨迹外包矩形，表值）、`trajectory_centroid(position)→GEOPOINT`（重心）、`trajectory_speed_max/avg/p95(position,time)→FLOAT64`；`GROUP BY time(...)` 窗口内跨段 Merge 兼容 | ✅ |
| #73 | **GeoJSON 序列化 + REST 端点扩展**：`GeoJsonConverter` 将 GEOPOINT 字段序列化为 `{"type":"Point","coordinates":[lon,lat]}`（GeoJSON 标准经纬顺序）；查询结果 ndjson 流自动输出 GeoJSON；新增 `GET /v1/db/{db}/geo/{measurement}/trajectory?device=...&from=...&to=...`，返回 GeoJSON `FeatureCollection`（每行为 `Feature/Point`）或 `?format=linestring`（单个 `LineString Feature`）；ADO.NET `DbDataReader` 对 GEOPOINT 列返回 `GeoPoint` struct | ✅ |
| #74 | **Web Admin 轨迹地图标签页（Vue3 + MapLibre GL）**：引入前端依赖 `maplibre-gl`（Apache-2.0）；新增 `TrajectoryMap.vue`：左侧筛选面板（数据库 / Measurement / 时间范围 / TAG）→ 调用轨迹端点；右侧 MapLibre GL 底图（OSM 瓦片）+ 轨迹 LineString 叠加层 + 起终点标记；底部时间轴播放器（逐帧动画回放）；ECharts 折线图联动展示速度 / 海拔等数值字段；多设备轨迹对比（不同颜色） | ✅ |
| #75 | **SQL 控制台地图渲染集成**：查询结果检测到 GEOPOINT 字段时自动在结果表下方展示 `ResultMapPreview.vue`；支持"表格 / 图表 / 地图"三视图切换；地图视图：散点图（多点）或带时间排序的轨迹连线；曲线视图增强：x 轴支持 `time`，y 轴自动识别数值字段，可叠加多 series | ✅ |
| #76 | **地理空间索引（Geohash 段内过滤）**：`BlockHeader` 新增 `GeoHashMin` / `GeoHashMax`（32-bit Geohash 前缀），`SegmentWriter` flush 时写入每 block 的 GEOPOINT 范围；`SegmentReader` 执行 `geo_within` / `geo_bbox` 时做 block 级 Geohash 剪枝（稀疏轨迹典型加速 10–20×）；`SegmentFormatVersion` v5，保留 v4 只读回退；`docs/geo-spatial.md` | ✅ |
| #77 | **地理空间基准 + 文档完善**：`GeoQueryBenchmark`（100k / 1M 轨迹点 `geo_within` 过滤 + `trajectory_length` 聚合，与 PostGIS 粗略对比）；README 新增"地理空间 & 轨迹"功能矩阵；`docs/geo-spatial.md` 补齐端到端示例（车辆追踪 / 户外运动 / IoT 地理围栏告警） | ✅ |

### SQL 用法预览

```sql
-- 车辆轨迹查询（返回 GeoJSON 用于前端地图渲染）
SELECT time, device, position,
       geo_speed(position, LAG(position) OVER w, 1000) AS speed
FROM vehicle
WHERE device = 'truck-01' AND time > now() - 6h
WINDOW w AS (PARTITION BY device ORDER BY time);

-- 地理围栏：查找进入北京五环内的车辆
SELECT DISTINCT device
FROM vehicle
WHERE geo_within(position, 39.9042, 116.4074, 18500)   -- 北京中心 18.5km 近似五环
  AND time > now() - 1h;

-- 各设备今日总里程
SELECT device,
       trajectory_length(position)          AS distance_m,
       trajectory_speed_max(position, time) AS max_speed_ms
FROM vehicle
WHERE time >= today()
GROUP BY device;

-- 户外运动：海拔 + 速度曲线（前端双轴折线图）
SELECT time,
       lat(position) AS lat, lon(position) AS lon,
       altitude,
       geo_speed(position, LAG(position) OVER (ORDER BY time), 1000) AS speed
FROM workout WHERE session_id = 'run-2026-04-22';
```

### 推进顺序

```
PR #70（GEOPOINT 类型）
  → PR #71（标量地理函数）
    → PR #72（轨迹聚合）
    → PR #73（GeoJSON 序列化 + REST 端点）
      → PR #74（Web Admin 地图页）
        → PR #75（SQL 控制台地图集成）
  → PR #76（Geohash 段内索引）   ← 可与 #74/#75 并行
  → PR #77（基准 + 文档）        ← 最后收尾
```

**前置依赖**：无硬性前置，Milestone 13/14 不需要完成即可开始 Milestone 15。但若 PR #58（VECTOR 类型 + SegmentFormatVersion v3）已合并，本 Milestone PR #70 需在其基础上升级到 v4。

---

## Milestone 16 — Copilot 产品化升级（嵌入式 AI 助手 UX）

> **背景**：Milestone 14 已经把 Copilot 的服务端能力（MCP 工具 / 知识库摄入 / Agent 编排 / Eval）全部跑通，但用户在实际使用时仍遇到三类问题：
> 1. **首次启动 503**：默认 `local` provider 需要手工下载 ONNX，导致开箱即用失败；
> 2. **知识库不可见**：`docs/` 已自动摄入但 UI 没有展示，用户以为没建；
> 3. **UX 散落**：Copilot 入口只在"AI 设置"页 chat tab，且 SQL Console 生成的 SQL 是 MySQL 方言、不带 SonnetDB 语法（CREATE MEASUREMENT / VECTOR / TAG / FIELD）。
>
> 本 Milestone 把 Copilot 推进到**真正可日常使用的一等公民**：零依赖就绪、全局浮窗、会话历史、上下文感知、权限审批、模型可选、SQL 生成对齐 SonnetDB 方言。

### PR 列表

| PR | 主题 | 状态 |
|----|------|------|
| #78 | **M1：内置零依赖 embedding + readiness 放宽**：新增 `BuiltinHashEmbeddingProvider`（SHA-256 哈希投影 → 384 维 L2 归一化向量）；`CopilotEmbeddingOptions.Provider` 默认 `builtin`；`CopilotReadiness` 接受 `builtin`；DI 工厂在 `local` 模型缺失时自动降级 | ✅ |
| #79 | **M1.5：知识库可视化 status 端点**：新增 `GET /v1/copilot/knowledge/status`，返回 provider / fallback / 维度 / docs roots / 已索引文件数 / 块数 / 最近摄入时间 / 技能数；`DocsIngestor.GetIndexStateAsync()` + `BuiltinHashEmbeddingProvider.IsFallback` | ✅ |
| #80 | **M2：SQL 生成走 Copilot Agent + SonnetDB 方言**：Web Admin SQL Console 的 `generateSql()` 改为调用 `/v1/copilot/chat`（带当前 db / 现有 measurement schema 上下文），让 Copilot Agent 通过 `draft_sql` 工具生成 SonnetDB 语法（`CREATE MEASUREMENT … (time, x TAG, y FIELD FLOAT64, z FIELD VECTOR(384))`、`INSERT`、`SELECT … knn(...)`）；`/v1/ai/chat` 兜底通道也加上 SonnetDB SQL system prompt；prompt 模板抽到 `Copilot/Prompts/*.md` 嵌入资源由 `PromptTemplates` 加载 | ✅ |
| #81 | **M3：SNDBCopilot → Copilot 文案统一**：`AppShell.vue` 菜单 `SNDBCopilot` → `Copilot`、`AiSettingsView` 卡片标题 `SNDBCopilot 设置` → `Copilot 设置`；保留路由 key `ai-settings` 不变（避免破坏书签） | ✅ |
| #82 | **M4：全局 CopilotDock 浮窗 + 知识库卡片**：在 `AppShell.vue` 右下角注入 `CopilotDock.vue`（可拖拽 / 折叠 / 全屏切换）；任意页面均可呼出；AiSettingsView 增加"知识库"卡片消费 `/v1/copilot/knowledge/status` + "立即重建索引"按钮（POST `/v1/copilot/docs/ingest {force:true}`） | ✅ |
| #83 | **M5：会话历史**：第一阶段（客户端本地持久化 ✅）— 新增 `useCopilotSessionsStore`（Pinia + `localStorage` `sndb.copilot.sessions.v1`），CopilotDock header 新增「会话历史」Popover：新建 / 切换 / 重命名 / 删除 / 清空，自动从首条用户消息派生标题，最多保留 50 条，按 `updatedAt` 倒序展示，切换会话同步还原 db 选择。第二阶段（服务端持久化，规划中）— 用 `__copilot__.conversations`（`id TAG, title TAG, created_at, updated_at, message_count, summary FIELD STRING`）+ `__copilot__.messages` 持久化；新增 `GET/POST/DELETE /v1/copilot/conversations[/{id}]` | 🚧（一阶段 ✅）|
| #84 | **M6：页面上下文感知**：CopilotDock 自动捕获当前路由 + SQL Console 编辑中的 SQL / 当前选中数据库，以 `system` 角色消息在 `send()` 时临时拼到 `messages[]` 头部（不写入会话历史）；UI 提供 `📍 当前页面：Xxx · SQL N 字符 · db=xxx` 状态标签与开关；后续（规划中）提示词模板支持 `{{page.route}}` / `{{page.selection}}` 变量 | ✅ |
| #85 | **M7：权限选择器 + 写操作审批**：CopilotDock 提供 `🔒 只读模式` / `⚠️ 读写模式` 切换，默认只读；切换为读写需 NPopconfirm 二次确认；服务端 `CopilotChatRequest.Mode` 字段在 `read-only` 时强制将 `CopilotAgentContext.CanWrite` 置为 false，使 `execute_sql` 写入在 agent 内部即遭拒；后续（规划）能在 UI 逐条弹“将执行以下 SQL，确认？”对话框 | ✅ |
| #86 | **M8：模型选择器**：CopilotDock 下拉选择 chat 模型，服务端新增 `GET /v1/copilot/models` 返回 `{default, candidates[]}`（`CopilotChatOptions.AvailableModels` 提供候选）；UI 支持自由输入 + `localStorage` 记忆；`/v1/copilot/chat` 请求体新增可选 `model`，`IChatProvider.CompleteAsync` 增加 `modelOverride` 参数，在 OpenAI-compatible provider 中临时覆盖 `CopilotChatOptions.Model` | ✅ |
| #87 | **M9：SQL Console 语法高亮回归**：新增 `web/src/components/sonnetdb-dialect.ts` 定义 `SonnetDbSQL = SQLDialect.define({ ...StandardSQL.spec, keywords + 'measurement|tag|field|...', types + 'vector|float|int|bool|string', builtin + 'knn|time_bucket|forecast|pid_*' })`；SqlEditor 改为使用 `SonnetDbSQL` 方言，lang-sql 内置的关键字补全与高亮自动覆盖 SonnetDB 词汇 | ✅ |
| #88 | **M10：新手引导 / 提示词模板**：新增 `web/src/copilot/starters.ts` 定义 `COPILOT_STARTERS`（建表 / 写入 / 聚合 / 向量 / 预测 / PID / 排查分类）与 `pickStarters(routeKey)` 路由过滤；CopilotDock 空白态按 grid 展示 starter 卡片，点击填入输入框 | ✅ |

### 已落地补充

- **Web Admin SQL Console 首版升级为 SonnetDB Workbench**：保持原有 `/admin/app/sql` 路由和 Vue + CodeMirror + Naive UI 技术栈不变，把页面重构为 Schema Explorer / SQL Editor / Staged Preview / Result Grid 双栏工作台；读语句可直接执行，写语句必须先 staging，`DELETE` / `DROP` / `GRANT` / `REVOKE` / `USER` / `TOKEN` 类危险操作需要勾选确认。Copilot 继续使用右下角全局 `CopilotDock` 浮窗，不在 Workbench 内单独占栏。

### 推进顺序

```
PR #78 ✅ → #79 ✅ → #80 ✅ → #81 ✅ → #82 ✅
  → #83（M5 会话历史）✅ → #84（M6 上下文）✅
  → #85（M7 权限）✅ → #86（M8 模型）✅
  → #87（M9 高亮）✅ → #88（M10 引导）✅
```

**前置依赖**：Milestone 14 已合并；本 Milestone 不破坏 SonnetDB Core 的二进制格式，全部为 `src/SonnetDB`（API 层）+ `web/`（前端）+ Copilot 子系统的扩展。

---

---

## 与原路线图的差异说明

1. **PR #15 重定义**：从原"QueryEngine.QueryRaw"改为"多段索引层（SegmentIndex / MultiSegmentIndex / SegmentManager）"；原 QueryRaw 内容并入新 PR #16。
2. **Milestone 5 重定义**：从原"SQL 前端"改为"稳定性与性能（写入侧）"，新增 PR #17（后台 Flush + Checkpoint replay 跳过） / #18（Compaction） / #19（多 WAL 滚动） / #20（DELETE/Tombstone） / #21（Retention TTL，待派单）。
3. **SQL 前端整体后移到 Milestone 6**，并扩充 Tag 倒排索引（PR #27）与 ADO.NET API（PR #28）。
4. **压缩编码独立为 Milestone 7**（原 Milestone 6 的 PR #22 / #23 / #24 中，Compaction 已在新 PR #18 完成；保留的 Delta / Gorilla 编码工作迁入此处）。
5. **单文件容器方案放弃**：当前多文件布局（`catalog.SDBCAT` + `wal/*.SDBWAL` + `segments/*.SDBSEG` + `tombstones.tslmanifest`）已稳定且对运维/备份/排错友好；单文件需新增 page manager + shadow paging，会重写 M3~M5 的崩溃恢复矩阵，收益不足以覆盖成本。原 M8 改为"服务器模式"。
6. **Milestone 8 重定义为服务器模式**：仅 HTTP（`POST /v1/db/{db}/sql` + ndjson 流式结果），WebSocket 推后评估；多租户采用进程内 `Tsdb` 注册表；权限仅 Bearer token + 三角色，不做 SQL 级 GRANT。
7. **执行顺序前置**：PR #21（Retention TTL）与 PR #35（嵌入式 Benchmark 基线）必须先于 M8 完成。
8. **发布顺延到 Milestone 9。**
9. **新增 Milestone 12 — 函数与算子扩展**：将 `enum Aggregator` 重构为 `FunctionRegistry` + `IAggregateFunction`，引入 Tier 1–5 共 ~50 个函数；以 **PID 与 Forecast** 作为内置差异化能力，并开放 UDF 注册 API 给嵌入式生态。该里程碑独立于原路线图，定位为 SonnetDB 在工业 / IoT / 可观测性场景的横向扩展层。
10. **新增 Milestone 13 — 向量类型与嵌入式向量索引**：引入 `VECTOR(dim)` 数据类型与 `cosine_distance` / `l2_distance` / `inner_product` 标量函数 + `knn(...)` 表值函数，第一版以 brute-force + 并行顺扫实现，HNSW 作为可选段内 sidecar 索引（`SDBVIDX`）。定位为 Milestone 14 Copilot 知识库的存储底座，同时让 SonnetDB 形成"时序 + 向量"二合一的对外差异化能力；后续可在继续遵守 Safe-only 原则的前提下演进到 `System.Numerics.Tensors.TensorPrimitives` 等 SIMD 加速路径。
11. **新增 Milestone 14 — SonnetDB Copilot**：基于 Microsoft Agent Framework 的智能体层，复用现有 `/mcp/{db}` 工具集 + Milestone 13 的向量召回，把"用户文档 / 技能库 / 数据库 schema"全部 dogfood 到 `__copilot__` 系统库中。Embedding/Chat 走统一 `IEmbeddingProvider` / `IChatProvider` 抽象，**本地 ONNX（bge-small-zh）** 与 **OpenAI 兼容端点（国际版 / 国内版任意 OpenAI-compat 网关）** 同时支持，可按部署场景切换。**不新增项目**，在现有 `SonnetDB.Core` / `SonnetDB.Server` 程序集内新增 `SonnetDB.Copilot` 命名空间；测试位于 `tests/SonnetDB.Tests/Copilot/`；服务端默认启用，可通过配置关闭。
12. **新增 Milestone 15 — 地理空间类型与轨迹分析**：引入原生 `GEOPOINT` 字段类型（`FieldType.GeoPoint = 6`，lat/lon 各 8 字节 little-endian，`SegmentFormatVersion` v4）；Tier 1 地理标量函数（`geo_distance` / `geo_bearing` / `geo_within` / `geo_bbox` / `geo_speed`，含 PostGIS 兼容别名）；Tier 2 轨迹聚合函数（`trajectory_length` / `trajectory_centroid` / `trajectory_bbox` / 速度统计）；GeoJSON 序列化 + `GET /v1/db/{db}/geo/{measurement}/trajectory` 端点；Vue3 Web Admin 轨迹地图标签页（MapLibre GL + ECharts 时间轴联动）；SQL 控制台三视图（表格 / 图表 / 地图）；Geohash 段内剪枝索引（`SegmentFormatVersion` v5）。全程遵守 Safe-only 与零第三方运行时依赖原则。
13. **新增 Milestone 17 — 可观测性与运行时可见性**：为 SonnetDB 补齐生产可运维三大支柱（指标 / 追踪 / 日志）。`SonnetDB.Core` 继续堅持**零运行时第三方依赖**，仅用 BCL `System.Diagnostics.Metrics` / `ActivitySource` 提供 Meter 与 Activity；OpenTelemetry SDK / Prometheus Exporter 仅出现在 `src/SonnetDB`（Server 程序集）。附带交付：Slow Query Log 与 Top-N 查询统计、Diagnostic Dump 端点、Health Live/Ready 拆分、Copilot token / tool 调用量指标与服务端会话持久化（M16 M5 二阶段）、Web Admin 内嵌监控面板（零图表第三方）。docker-compose 补 `profile: observability` 依需启动 `otel-collector` + `prometheus` + `grafana` 供本地联调。
14. **细化原 Milestone 10 的 #40 占位需求为独立的 Milestone 18 — VS Code 数据库扩展**：保留 `#40` 作为 Epic，占位层面明确为“SonnetDB for VS Code”；具体实现拆分为 `#99 ~ #108`，采用 **TypeScript-first + Remote-first** 路线，首版直接复用现有 `/v1/db`、`/v1/db/{db}/schema`、`/v1/db/{db}/sql`、`/v1/copilot/chat/stream` 等 HTTP contract。本地目录支持不走 Node 直嵌引擎，而是后续通过“扩展托管本地 SonnetDB Server”方式接入，降低 VS Code 宿主与 .NET 运行时耦合。
15. **新增 Milestone 19 — IoTSharp 生态数据底座选项**：把 SonnetDB 从“时序 + 管理后台 + Copilot”扩展为 IoTSharp 的可选数据底座。该里程碑覆盖关系型数据库、时序数据库、KV/缓存、S3-compatible 对象桶、向量搜索、全文搜索和大量物理分表长稳七条线，并把 EF Core provider、EasyCaching/IDistributedCache provider、S3 API、搜索索引生命周期、迁移双写、分层文件布局、compaction manifest、长稳压测作为同等重要的交付物。路线明确要求先做兼容矩阵和回滚策略，避免把不完整 table/KV/搜索能力过早宣称为 PostgreSQL/Redis/S3/搜索后端的生产级完整兼容。
16. **新增 Milestone 20 — 多模能力对齐与平移测试 (Parity)**：用一份 docker-compose 同时拉起 SonnetDB 与开源全家桶（PostgreSQL / Redis / InfluxDB / VictoriaMetrics / MinIO / NATS / Mosquitto / Meilisearch / Qdrant / ClickHouse），用同一套 `IDataPlane` 适配器跑同一套场景，证明"一台 SonnetDB 在边缘 / 单机场景能替掉这一组组件"。**显式不做协议兼容**（自有 `SndbConnection` / `SndbMqClient` / `SndbObjectStorageClient` / EF Core provider，竞品走它们的官方 .NET 客户端），**显式不做替代主张**（不对齐 Redis Cluster / Kafka / Postgres HA / MinIO 集群），三类对齐边界为：能力对齐、可靠性对齐、算法准确度对齐；性能数字写报告不做 gating。本里程碑同时连带把 KV `INCR/CAS/EXPIRE/PERSIST/TTL`、SonnetMQ `RecordTypeTombstone` 段滚动 + `FlushOnPublish=true` 默认、对象桶 `ListObjectsV2 ContinuationToken` 分页、`tests/SonnetDB.CrashTests/` 真子进程 SIGKILL 注杀、README 措辞与代码同步落地。详细设计见 [`docs/parity-roadmap.md`](parity-roadmap.md)。
17. **新增 Milestone 21 — Document Store 单机能力升级（MongoDB-like，不做协议兼容）**：在 MM5 JSON 文档能力、MM6 全文索引、MM8 Hybrid Search 与 Milestone 20 parity 基础上，把 SonnetDB Document Store 推进到 MongoDB 单机常用能力子集。范围仅保留能力和功能实现：Document API / client、find filter、projection、sort、cursor、局部更新操作符、复合 / unique / sparse / partial / TTL 索引、aggregation pipeline 子集、单文档原子性、批量写轻事务、validator 执行能力与文档容量底座。该里程碑**明确不做 MongoDB wire protocol / BSON command / 官方 driver 直连兼容**，也不交付 Studio 管理面、MongoDB 参考 parity、长稳报告或发布文档。
18. **新增 Milestone 22 — Agent Memory / Codebase Intelligence（代码知识库与 MCP Memory 后端）**：吸收 codebase-memory-mcp 代表的“代码库结构记忆 + MCP 工具”产品形态，把 SonnetDB 定位为 Agent 的长期记忆与代码知识底座，而不是仅供 SonnetDB Copilot 自用的内部 RAG。范围包括 Code Memory 标准 schema、Git/files/chunks ingest、C# 符号与调用边索引、只读 MCP typed tools、Hybrid Search 融合、Agent Memory 持久化 API、Web Admin Code Memory Explorer、VS Code / Copilot 接入样例与规模报告。该里程碑明确 `src/SonnetDB.Core` 不引入 tree-sitter/Roslyn/libgit2 等大型运行时依赖，语言解析与 Git 扫描放在 CLI/扩展/测试/示例工具层。
19. **新增 Milestone 23 — 搜索与向量引擎合并**：DotSearch / DotVector 不再作为 SonnetDB 之外的独立产品线继续扩张。BM25、分词、距离计算、HNSW / IVF / Vamana、量化和索引序列化逐步收编到 `src/SonnetDB.Core`；`Microsoft.Extensions.VectorData` adapter 迁移到 `src/SonnetDB.Data`。路线见 [`docs/search-vector-engine-consolidation-roadmap.md`](search-vector-engine-consolidation-roadmap.md)。
20. **新增 Milestone 24 — SonnetDB Studio 管理体验升级（Document 管理面）**：承接从 Milestone 21 迁出的管理相关内容，集中做 Document Explorer、validator governance、索引管理、JSONL/NDJSON 导入导出、rebuild / dry-run / 审批等 Studio 体验；本里程碑只补必要 metadata / maintenance contract，不新增 Document Store 查询、索引、事务或存储能力。
21. **新增 Milestone 25 — Document Store 验收、文档与发布治理**：承接从 Milestone 21 迁出的文档和发布治理内容，集中做 MongoDB 参考 parity、百万 / 千万文档长稳 profile、容量报告、README / docs 能力矩阵、MongoDB-like 迁移指南、不支持项和推荐规模说明。


---

# 已完成里程碑详细正文（归档）

> 以下为主路线图 [ROADMAP.md](../ROADMAP.md) 中已完成里程碑的详细正文归档，含逐 PR 拆分、缺陷附录与落地说明。

---

## Milestone 20 — 多模能力对齐与平移测试 (Parity)

> **目标**：用一份 docker-compose 同时拉起 SonnetDB 与开源组件全家桶（PostgreSQL / Redis / InfluxDB / VictoriaMetrics / MinIO / NATS / Mosquitto / Meilisearch / Qdrant / ClickHouse），用同一份场景脚本两边各跑一遍，证明"一台 SonnetDB 在边缘 / 单机场景能替掉这一组组件"。详细设计见 [docs/parity-roadmap.md](parity-roadmap.md)。
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
- **不**做上层产品专用迁移工具（属于对应上层项目路线图；SonnetDB 仅保留 [Milestone 19](../ROADMAP.md#milestone-19--生态适配底座能力关系--kv缓存--对象桶--大量-measurement) 的通用迁移与校验原语）。
- **不**做绝对性能 gating（已在 [tests/SonnetDB.Benchmarks](../tests/SonnetDB.Benchmarks/) 处理）。
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

- **Studio 管理面**：原 #145 中的 schema governance 查看 / 编辑，以及原 #148 Document Explorer、索引管理、JSONL/NDJSON 导入导出，迁入 [Milestone 24](../ROADMAP.md#milestone-24--sonnetdb-studio-管理体验升级document-管理面)。
- **验收、长稳和发布文档**：原 #147 MongoDB 参考 parity，以及原 #149 百万 / 千万文档长稳报告、README / docs 能力矩阵和迁移指南，迁入 [Milestone 25](../ROADMAP.md#milestone-25--document-store-验收文档与发布治理)。

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
| **P4** | 索引与向量能力（文档惰性 scan + FTS 写放大 + 向量度量/ANN） | #221 ~ #229 | ✅ 已完成——二级索引真正被使用、向量非 cosine 贯通、文档集合持久 ANN 加速、遗留 HNSW 死代码移除、文档索引原子性验证收口 |
| **P5** | 消息队列吞吐 + 全模型高吞吐接入（MQ 硬化 + 自定义二进制帧 over HTTP/2 覆盖 MQ/时序/关系/向量 + MQTT broker/client 双形态设备接入） | #230 ~ #244、#261 ~ #262 | 消除 MQ 单锁/无界内存/每写 flush；通用帧层消灭全模型 JSON/Base64 税、支持推送订阅与流式结果集；IoT 设备走 MQTT（内建 broker + 订阅外部 broker）；#261/#262 补 #241 遗漏的 SDK 时序写与对象客户端帧贯通 |

> **收官状态（归档）**：P0~P5（#189~#244）+ SDK 补口 #261/#262 全部完成——SonnetMQ 四个 🔴 全部关闭（含 MQ3 无界内存/OOM），帧协议覆盖 MQ / 时序 / SQL / 向量 / KV / 对象 / 文档七个 service，SDK 帧/REST parity 已横向收口，MQTT broker/client 双形态就位。

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
| #224 | **向量 KNN 用 block skip-index**：`KnnExecutor.ScanSegment` 经 `MultiSegmentIndex`/`SegmentIndex.GetBlocks(series, from, to)` 做 series/时间范围 prefix-max 剪枝，替换 `foreach reader.Blocks` 全块逐一过滤（O(总 block 数)）。 | 索引 I8 | ✅（`KnnExecutor.Execute` 新增 `MultiSegmentIndex` 参数，段侧扫描由「逐 series 遍历每个 reader 的全部 Block 再按 SeriesId/FieldName/FieldType/时间窗逐一过滤」改为 `segmentIndex.LookupCandidates(seriesId, field, from, to)` 只召回相交候选 block——段级时间剪枝 + block 级 prefix-max 二分（复用 #220/聚合快路径同款 skip-index）在索引内完成，O(总 block 数)→O(log n + 重叠块数)；候选 block 经 `SegmentId → SegmentReader` 映射回读，命中 metric 一致且无墓碑仍走段内 ANN 加速，否则精确扫描，语义与旧路径逐点等价（墓碑放弃 ANN、I7 度量 gate、CollectIndexedBlockCandidates 去重均保留）。两个调用点 `TableValuedFunctionExecutor`（knn TVF / 帧向量检索）与 `HybridSearchExecutor` 均传入同一读租约的 `Snapshot.Index`，保证候选 block 与 reader 同源。新增 5 个 E2E 回归：多段不相交时间带 + 窄窗只召回命中段、多段全窗跨段 Top-K、同段多 series tag 过滤只命中匹配 series、时间窗落在所有段外返回空、MemTable+多段合并） |
| #225 | **compaction 向量索引 catalog 必需**：对含 VECTOR 列的段，`SegmentCompactor`/`SegmentWriter` 的 `seriesCatalog`+`measurementCatalog` 由可选改为必需或加断言，避免调用方省略致 compacted 向量块无索引段、静默退化为暴力扫。 | 索引 I11 | ✅ |
| #226 | **HNSW ef 补偿 tombstone + 重建回收**：搜索按 tombstone 比例放大 ef 或持续搜索至收集满 topK 个存活结果（当前 `ef=max(EfSearch,topK)` 过滤 tombstone 后可能欠返回）；提供周期性 compaction/rebuild 物理丢弃 tombstoned 行并重指 `_entryPoint`，回收 churn 下的无界内存增长。 | 索引 I6、I14 | ✅ |
| #227 | **文档集合持久 ANN 索引**：为 document collection 的 `vector_search` 提供持久化 per-collection ANN 索引或至少缓存已解析向量，替换全表 `store.Scan()` + 每行 `JsonDocument.Parse` + 距离的 O(N·dim) 暴力扫。 | 索引 I12 | ✅（镜像全文索引子系统：schema `DocumentVectorIndex` + codec v5、`DocumentVectorIndexStore`（KV 持久化 id→向量 + `HnswIndex<string>` open 时 bulk-build，崩溃随集合重建自愈）、store `_vectorStores` + 单条/批量 mutation delete-old/upsert-new、manager Create/Drop/Rebuild + `vector/<hex>` 目录、DDL `CREATE/DROP VECTOR INDEX`；`vector_search` 无 WHERE + path/metric/dim 匹配时走 ANN 报 `document_vector_index` 否则回落暴力扫 `document_vector_scan`；#229 校验器 + quality 报告覆盖向量索引。`DocumentVectorIndexTests` 13 项含走索引与暴力扫逐行等价 + 重开 bulk-build） |
| #228 | **删除遗留 `HnswVectorBlockIndex`**：删除或明确隔离仍被测试维护的死代码 `HnswVectorBlockIndex`（图质量更差、O(n·ef²) 建图），统一到 `HnswIndex<int>`，消除误用风险。 | 索引 I13 | ✅（删除 `Storage/Segments/HnswVectorBlockIndex.cs` + 文件内 `HnswAnnSearchResult`——`src/` 零生产引用，生产路径早走 `VectorIndexAdapter`→`LocalVectorIndexBuilder`→`HnswIndex<int>`；仅剩的召回测试重写重命名为 `HnswIndexRecallTests`、`VectorRecallBenchmark` 迁移到 canonical `HnswIndex<int>`，Recall@10 ≥ 0.90 断言不变；catalog 生产类型 `HnswVectorIndexOptions` 不在删除文件内保留） |
| #229 | **文档索引原子维护 + 崩溃重建校验**：验证 document 二级索引在 insert/update 时与主数据原子写入、崩溃后随集合重建（当前 planner 依赖索引"过包含"再用 `Matches` 复检，一旦"欠包含"会静默漏行）；补一个覆盖扫描一致性校验。 | 索引 I10（疑似，需先验证） | ✅（**验证结论：无实缺陷**——二级索引是主文档纯函数，`DocumentCollectionStore` 构造时 `RebuildIndexesLocked` 从主数据全量重建，主写走 KV WAL 落盘故崩溃 / torn write 造成的欠包含在重开集合时自愈；live 维护全程持 `_sync` 锁原子写入。新增只读校验器 `VerifyIndexConsistency()`→`DocumentIndexConsistencyReport`：全表扫主文档重算期望条目集、与 KV 实际条目集按索引名对比得 Missing（欠包含）/Orphan（过包含）；接入 Server `quality_analysis` 质量报告（欠包含报 error、过包含报 warning、Detail 携带 entries/missing/orphan 计数）。新增 `DocumentIndexConsistencyTests` 6 项含崩溃删条目→重开自愈闭环 + Server `Maintenance_QualityAnalysis` 断言扩展） |

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
| #242 | **MQTT 内建 broker（设备直连落库/订阅推送）**：Server 层内建 MQTT **broker**（服务端形态，对标 IoTDB / TDengine 设备直连），采用 IoTSharp 自家 **[MQTTnet.AspNetCore.Routing](https://github.com/IoTSharp/MQTTnet.AspNetCore.Routing)**（MVC 风格 topic 路由，`SonnetDB.Core` 仍零依赖）。**topic 模板 `db/{db}/m/{measurement}`** 把 `PUBLISH` 路由到 database + measurement；**payload = measurement 内容**，复用现有 `BulkIngestEndpointHandler` 三格式（Line Protocol / JSON points / BulkValues）落库，**零重复落库逻辑**；设备 `SUBSCRIBE` 复用 #236 推送管线。MQTT 鉴权复用现有 Bearer/三角色权限模型（username/password 或 token 映射 database 权限）。范围：**单机内建 broker**，QoS 0/1、retain、LWT 支持范围在 `docs/` 明确；**不做 broker 集群 / 桥接 / 跨节点 session**（与 P5「不做分布式」边界一致）。 | N9 | ✅（Server 新增 MQTT TCP/WebSocket broker 配置与 docker 1883；`db/{db}/m/{measurement}` 复用 BulkIngest 三格式入库，`db/{db}/mq/{topic}` 桥接 SonnetMQ 并从 latest 复用 #236 pump 推送；鉴权复用用户/token/角色，QoS 0/1，精确 topic，无集群/桥接/跨节点 session。测试覆盖入库、readonly 拒绝、MQ 订阅推送；文档补 QoS/retain/LWT 范围。） |
| #243 | **MQTT client 订阅外部 broker（接入已有 EMQX/Mosquitto 基础设施）**：Server 作为 MQTT **client** 主动连接并 `SUBSCRIBE` 已有外部 broker，把消息拉入 SonnetDB 落库——同样用 **MQTTnet.AspNetCore.Routing** 的 topic 路由抽象（与 #242 broker 共享同一套 `[MqttRoute]` controller 与 `db/{db}/m/{measurement}` → `BulkIngestEndpointHandler` 落库逻辑，仅消息来源从内建 broker 换成外部 broker 的订阅回调）。配置外部 broker 地址/凭证/订阅 topic 过滤器与重连策略；与 #242 内建 broker 可同时启用（本机既是 broker 又订阅上游）。对标 InfluxDB+Telegraf 的 client 订阅范式。 | N10 | ✅（Server 新增 `SonnetDBServer:Mqtt:ExternalClient` 配置与后台 MQTT client，支持 TCP/TLS、外部 broker 凭据、topic filter 列表、QoS 0/1 与指数退避重连；收到实际 topic `db/{db}/m/{measurement}` 后复用 #242 抽出的共享 measurement ingestor 与 `BulkIngestEndpointHandler.IngestPayload` 三格式落库。外部 client 可独立于内建 broker 运行，也可同时启用；范围不含 broker 桥接、跨节点 session 或外部 `db/{db}/mq/{topic}` 到 SonnetMQ 桥接。测试覆盖临时外部 broker → SonnetDB client 订阅 → SQL 回查落库闭环。） |
| #244 | **全模型接入收口 + 文档 + parity**：汇总 #230/#235/#237~#239 基准的吞吐/延迟/体积对照进报告；补 `docs/` 接入协议章节（帧格式、service/op 矩阵、REST vs 帧-HTTP2 选型矩阵、推送订阅与流式结果集用法、MQTT broker/client 两形态 topic 映射规则与 QoS 范围）；`tests/SonnetDB.Parity` 补各 service 二进制帧与 REST 的等价性平移测试，确保两条路径语义一致。 | MQ0、N1~N10 | ✅（`docs/frame-protocol.md` 补 REST vs 帧-HTTP2 选型矩阵、#230/#235/#237~#239 基准索引、MQTT broker/client topic/QoS 边界与 #244 parity 验收入口；`tests/SonnetDB.Parity/runner/FrameRestTransportParitySuite.cs` 用真实 Kestrel 横向覆盖 MQ/TSDB/SQL/Vector/KV/Object/Document 七 service 的帧/REST 语义等价） |
| #261 | **客户端 SDK 时序写帧贯通（补 #241 缺口）**：#241 的 SDK 帧贯通遗漏了**时序批量写**——远程 `SndbConnection`/`SndbCommand` 的 bulk ingest 目前仍恒走 REST `/measurements/{m}/{lp\|json\|bulk}`，服务端 #237 的 `tsdb` 列式写帧（`TsdbColumnarBlock`，wire 3.73× 小于 JSON、编码 326× 快）在客户端一次都没接。落地：远程写路径在 `_frames.ShouldTryFrames()` 时把探测出的 measurement + tag 组 + 列值编码为 `TsdbFrameCodec` write-columnar 帧优先发送、传输级失败回落既有 REST bulk（`Protocol=frame-http2` 强制不回落）；三格式 payload（Line Protocol / JSON points / BulkValues）经既有 reader 解析为点集后走列式帧编码，flush 三档映射 `flushMode`。嵌入式路径不变。补 `SonnetDB.Data` 时序写帧 vs REST 的 parity 测试（数据等价、schema 冲突同码 `bulk_ingest_error`、混合批隔离）。 | N5（客户端侧收口） | ✅（新增 `TsdbColumnarBlockBuilder` 行→列聚合，`RemoteConnectionImpl` bulk 路径合流为单一 async 帧优先入口；**Line Protocol / JSON 走列式帧，BulkValues 恒走 REST**——其 tag/field 列角色需服务端 schema 解析、客户端无 schema 无法列式编码；`onerror=skip` 亦恒走 REST——帧端点 FailFast；解析/编码失败静默回落 REST，服务端 reader/schema 权威） |
| #262 | **对象存储客户端帧贯通（补 #241 缺口）**：`SndbObjectStorageClient` 当前**完全没有 `FrameChannel`**，get/put 全走 REST + `StreamContent`，服务端 #240 的 `object` service 帧（get 流式 meta→data×N→end 分块、put 原始字节零 Base64）在客户端未接。落地：给 `SndbObjectStorageClient` 加 `_frames` 字段（同其余客户端），`PutObjectAsync`（≤132MiB 单帧上限内）走 `ObjectFrameCodec` put、`OpenReadAsync`（非 Range 全量读）走 object get 流式分块帧、命中传输级失败回落 REST；**bucket 管理 / multipart / 大对象（>132MiB）/ Range 读 / presigned / tagging 仍恒走 REST**（与服务端 #240 帧 scope 一致，管理面不进帧）。嵌入式路径不变。补对象 get/put 帧 vs REST 的 parity 测试（字节等价、大 blob 多分块、错误码 `bucket_not_found`/`object_not_found` 同码）。 | N8（客户端侧收口） | ✅ |

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
        补口：#261（SDK 时序写帧贯通，补 #241）✅ ∥ #262（对象存储客户端帧贯通，补 #241）✅
```

> **阶段间可并行度**：P0 内 #189~#196 相互独立，可并行推进但建议 #189/#190/#191 最先（数据安全影响面最大）。P1~P4 各阶段建议顺序推进，但 P3/P4 的能力增强类 PR 与 P2 吞吐类 PR 之间无强依赖，可按团队带宽穿插。P5（#230~#244）独立于 P0~P4；内部约束：**P5a（#230~#234）纯 Core MQ 硬化与 P5b 接入层解耦，可并行**；P5a 内 #230 基准必须最先；P5b 内 **#235 通用帧编解码是 #236~#240 所有 service 接入的前置（#235 已 ✅，前置解除）**，各 service opcode（#237~#240）之间无强依赖可穿插，#236 推送订阅依赖 #235（复用其 `TryReadFrame` 增量解析循环，响应侧换 `Channels`），#241 SDK 贯通需至少一个 service 落地后，#242 内建 broker 可与 #237~#241 并行（复用 #236 推送管线），#243 MQTT client 订阅与 #242 共享路由 controller、宜紧随 #242，#244 收口最后。**#261（SDK 时序写帧）与 #262（对象存储客户端帧）是 #241 SDK 贯通遗漏的两条补口**——服务端 #237/#240 已 ✅，纯客户端 `SonnetDB.Data` 改动，二者互不依赖可并行，且不阻塞 #242~#244；#244 的 parity 平移测试宜在 #261/#262 落地后一并覆盖时序写与对象两条帧路径。

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
| I6 | ✅ | `Vector/Index/Hnsw/HnswIndex.cs:270` | HNSW 不为 tombstone 放大 ef，有删除时欠返回 topK | #226 |
| I7 | 🟡 | `Storage/Segments/VectorIndexAdapter.cs:141`、`Query/KnnExecutor.cs:199` | 非 cosine 向量索引按 cosine 建且 ANN gate 仅 cosine，白占空间不加速 | #223 |
| I8 | ✅ | `Query/KnnExecutor.cs:186` | 向量 KNN 不用 block skip-index，每 series 全块扫 | #224 |
| I9 | 🟡 | `Storage/Segments/VectorIndexAdapter.cs:179` | efConstruction 被 search ef 绑死，低 search-ef 永久烤进低质量图 | #223 |
| I10 | ✅（验证无实缺陷） | `Documents/DocumentQueryPlanner.cs` | 文档索引若"欠包含"（写入未原子/崩溃未重建）静默漏行；需先验证维护路径 | #229 |
| I11 | ✅ | `Engine/Compaction/SegmentCompactor.cs:86`、`Storage/Segments/SegmentWriter.cs:417` | compaction 向量索引仅在两个 catalog 都提供时构建，否则静默退化暴力扫 | #225 |
| I12 | ✅ | `Sql/Execution/DocumentVectorSearchExecutor.cs` | 文档 `vector_search` 全表暴力 + 每行 JSON parse，O(N·dim) | #227 |
| I13 | ✅ | ~~`Storage/Segments/HnswVectorBlockIndex.cs`~~（已删除） | 遗留死代码 HNSW，图质量差 O(n·ef²) 建图，误用风险 | #228 |
| I14 | ✅ | `Vector/Index/Hnsw/HnswIndex.cs:222` | HNSW tombstone-only 删除从不回收内存，`_entryPoint` 不重指 | #226 |
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
| N5 | 🟠 | `Endpoints/Routes/IngestionEndpoints.cs` | 时序批量写走 HTTP+JSON 行式，非列式二进制批（对标 IoTDB Tablet / PG COPY BINARY） | #237 ✅（服务端）+ #261 ✅（客户端 SDK 收口：远程 bulk ingest 的 Line Protocol / JSON 优先走 `TsdbFrameCodec` 列式写帧、传输级失败回落 REST；BulkValues 与 `onerror=skip` 恒走 REST） |
| N6 | 🟠 | `Endpoints/Routes/SqlEndpoints.cs`、`Json/NdjsonRowWriter.cs` | SQL/关系结果集全量物化 JSON 回传，大结果集序列化瓶颈、无流式 | #238 ✅ |
| N7 | 🔴 | `Endpoints/Routes/*`（向量 query/insert） | 向量 `float[]` 经 JSON 数字文本编解码，比 Base64 更浪费（每 float 变文本） | #239 ✅（search 请求/结果集 f32 二进制；插入侧由 #237 tsdb 列式写 Vector 列覆盖） |
| N8 | 🟡 | `Endpoints/Routes/KeyValueEndpoints.cs`、`ObjectStorageEndpoints.cs`、`DocumentEndpoints.cs` | KV value / 对象 blob / 文档二进制负载经 JSON+Base64，无原始字节路径 | #240 ✅（kv/object/doc 三 service 帧 opcode，原始字节直传零 Base64；对象 get 流式分块）+ #262 ✅（对象客户端 SDK 补口：`SndbObjectStorageClient` 加 `_frames`，put/非 Range 全量 get 走帧、Range 读与超帧上限对象回落 REST；KV/文档客户端已由 #241 贯通） |
| N9 | ✅ | `src/SonnetDB/`（无 MQTT broker） | 无内建 MQTT broker，IoT 设备无法直连发布/订阅（对标 IoTDB/TDengine 内建 broker；拟用 IoTSharp/MQTTnet.AspNetCore.Routing，topic `db/{db}/m/{measurement}`，payload 复用 BulkIngest 三格式） | #242 ✅ |
| N10 | ✅ | `src/SonnetDB/`（无 MQTT client） | 无 MQTT client 订阅能力，无法接入已有 EMQX/Mosquitto 基础设施（对标 InfluxDB+Telegraf；同用 MQTTnet.AspNetCore.Routing 路由，与内建 broker 共享落库逻辑） | #243 ✅ |

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
