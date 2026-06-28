# ROADMAP

本文件描述 SonnetDB 的分批 PR 开发计划，按 Milestone 组织。每个 PR 均包含：变更点、新增文件、测试覆盖与验收标准。

> **状态注记**：本路线图于 PR #20 合并后做过一次大幅修订。原计划在 Milestone 5 直接进入 SQL 前端，但实际开发中插入了"稳定性与性能（写入侧）"工作（后台 Flush / Compaction / 多 WAL 滚动 / DELETE-Tombstone）。SQL 前端已顺延到 Milestone 6，原 Milestone 6/7/8 编号顺移。

图例：✅ 已完成 / 🚧 进行中 / 📋 计划中

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
## Milestone 10 — 扩展和第三方

| PR | 主题 | 状态 |
|----|------|------|
| #40 | **SonnetDB for VS Code（Epic）**：官方 VS Code 数据库扩展，支持连接远程 SonnetDB Server、浏览 schema、执行 SQL、查看结果、接入 Copilot，并在后续支持“托管本地 SonnetDB Server 打开 data root”；详细 PR 拆分见 Milestone 18（#99 ~ #108）。 | 🚧 |
| #41 |  SonnetDB 支持 订阅MQTT消息，通过后台管理来添加订阅。   | 📋 |
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

## Milestone 19 — IoTSharp 生态数据底座选项（关系 + 时序 + KV/缓存 + S3 + 搜索）

> **目标**：把 SonnetDB 增加为 IoTSharp 生态的数据底座选择，默认优先支撑 IoTSharp 的关系、时序、缓存、对象桶与搜索能力。SonnetDB 与 Redis/LiteDB/InMemory 或其他数据库保持并列选择关系；用户可以按场景选择 SonnetDB 或继续使用既有后端。覆盖六类可选接入能力：
> 1. **时序数据库**：作为 InfluxDB / TimescaleDB / TDengine / IoTDB 等遥测存储后端之外的可选路径；
> 2. **KV / 缓存**：作为 Redis / LiteDB / InMemory 之外的可选缓存路径；
> 3. **关系型数据库**：通过 EF Core provider 支撑 `ApplicationDbContext`、Identity、租户、设备、资产、规则等主数据；
> 4. **S3 对象桶**：提供 S3-compatible API 与对象元数据/生命周期/审计能力，支撑 IoTSharp BlobStorage、固件、工件、附件和备份对象。
> 5. **向量搜索与全文搜索**：把 SonnetDB 已有 `VECTOR(N)` / KNN / 向量索引、内置全文索引和 Hybrid Search 纳入 IoTSharp 能力增强基线，明确当前 IoTSharp 未独立消费这些后端，后续接入不得误标为既有能力。
>
> **推进原则**：
> - 不把 SonnetDB 当前 table MVP 直接包装成“完整关系库”；先补 ADO.NET、SQL、事务、迁移和查询翻译硬能力。
> - 不把普通 KV keyspace 直接冒充 Redis；先补 TTL、过期清理、并发语义和缓存 Provider。
> - S3 能力采用 S3-compatible API 优先，内容存储可落本地卷、SonnetDB 管理目录或外部对象存储；SonnetDB 负责 bucket/object metadata、审计和生命周期。
> - IoTSharp 接入必须是显式可选、可灰度、双写、校验、回滚；不能要求用户一次性不可逆迁移，也不能移除既有数据库选择。

### PR 拆分

| PR | 主题 | 状态 |
|----|------|------|
| #109 | **IoTSharp 兼容矩阵与基线套件**：梳理 IoTSharp 当前 PostgreSQL/MySQL/SQLServer/SQLite/Oracle/Cassandra/ClickHouse、InfluxDB/TimescaleDB/Taos/IoTDB/SonnetDB、Redis/LiteDB/InMemory、BlobStorage/S3，以及向量搜索、全文搜索的能力矩阵；新增 `docs/iotsharp-compat-matrix.md` 与 `tests/SonnetDB.IoTSharpCompat.Tests` 占位，定义关系、时序、缓存、对象桶、向量搜索、全文搜索验收用例和迁移/回滚测试清单。 | ✅ |
| #110 | **ADO.NET 事务与异步 API**：实现 `SndbTransaction`，把 SQL 层 `BEGIN/COMMIT/ROLLBACK` 接入 `DbConnection.BeginDbTransaction` / `DbCommand.Transaction`；补 `OpenAsync`、`ExecuteReaderAsync`、`ExecuteNonQueryAsync`、`ExecuteScalarAsync`、取消令牌和远程 `/sql/batch` 事务语义。第一阶段允许单表轻事务，测试明确拒绝跨表事务。 | ✅ |
| #111 | **关系表 DDL 与 schema metadata 扩展**：补 `ALTER TABLE ADD/DROP/RENAME COLUMN`、`RENAME TABLE`、默认值、nullable 变更、索引重命名、`INFORMATION_SCHEMA` / `GetSchemaTable` / provider manifest metadata；为 EF Core migrations 生成器提供稳定数据库能力描述。当前已落 `ALTER TABLE ADD/DROP/RENAME COLUMN`、`ALTER TABLE RENAME TO`、`INFORMATION_SCHEMA.tables/columns/indexes`、`DbDataReader.GetSchemaTable()` 与 `DbConnection.GetSchema()` provider metadata 基线；首版明确拒绝主键列变更、被索引列删除和缺省值不足的 NOT NULL 新列。 | ✅ |
| #112 | **关系查询能力补齐一：表表 JOIN / 子查询 / 聚合**：在 table executor 增加 table-table inner join、基础 left join、`COUNT/SUM/MIN/MAX/AVG`、`GROUP BY column`、`HAVING`、`IN`、`EXISTS`、简单子查询；保证 IoTSharp 常见 `Include`、权限过滤、分页统计能翻译。当前已落表表连续 `INNER JOIN`、派生表、WHERE 标量子查询和基础 GROUP BY 聚合；outer join / HAVING / IN / EXISTS 留后续 provider 兼容压测补齐。 | ✅ |
| #113 | **关系事务能力补齐二：跨表小事务与约束**：实现同一数据库内多表 DML 的原子提交与回滚；补唯一约束、外键约束的第一版校验策略、乐观并发列、并发冲突错误码；明确隔离级别边界。 | ✅ |
| #114 | **SonnetDB.EntityFrameworkCore Provider MVP**：新增 EF Core provider 包，包含 `UseSonnetDB(...)`、SQL generator、type mapping、migrations SQL generator、query translation 基础能力；先通过 provider 自测与最小 `DbContext` CRUD、Identity 子集、迁移创建/回滚测试。 | ✅ |
| #115 | **IoTSharp EF migrations history 与 ApplicationDbContext 兼容适配**：优先补齐 SonnetDB EF provider 的 migrations history 支持（`__EFMigrationsHistory` 或等价可配置历史表），让 `Database.Migrate()`、迁移升级、回滚、重复执行幂等检查和空库初始化成为 #115 的入口验收；随后在 IoTSharp 增加 `IoTSharp.Data.SonnetDB` storage 扩展，跑通 `ApplicationDbContext` schema 创建、Identity 登录、租户/客户/设备/资产/规则 CRUD、`Include`、分页、常用查询、`StartsWith` / `EndsWith` / `Contains` 到标准 `LIKE` 的查询翻译和 `SaveChanges` 事务；形成不支持清单。 | ✅ |
| #116 | **KV TTL 与缓存 Provider**：在 KV keyspace 增加 expires-at metadata、惰性过期 + 后台清理、命名空间、批量 get/set/remove、前缀删除和过期统计；新增 EasyCaching provider 与可选 `IDistributedCache` provider；IoTSharp 可新增 `CachingUseIn=SonnetDB` 作为 Redis/LiteDB/InMemory 之外的选择。 | ✅ |
| #117 | **S3-compatible Bucket API 第一版**：新增 bucket/object metadata 表、multipart upload 会话、etag/sha256、range read、copy object、delete marker、object tags、presigned URL；HTTP API 对齐 S3 常用子集，先覆盖 IoTSharp BlobStorage、固件、附件、工件和备份对象。 | ✅ |
| #118 | **对象生命周期、版本、审计与配额**：补 bucket policy、retention/lifecycle、object versioning、legal hold 占位、访问审计、容量统计和 quota；Web Admin 增加 Buckets / Objects / Multipart / Audit 页面。 | 🚧 |
| #119 | **IoTSharp SonnetDB Profile**：提供 `appsettings.SonnetDB.json`、Docker Compose、健康检查和配置说明；用户可选择关系库、遥测库、缓存、对象桶走 SonnetDB；保留 PostgreSQL/Redis/S3 等既有 Profile，支持一键切回。 | 🚧 |
| #120 | **迁移、双写与一致性校验工具**：新增 `sndb iotsharp migrate` / `verify` / `rollback` 工具，支持关系库、时序库、缓存 key、对象桶 metadata/content 迁移；支持 IoTSharp 显式选择 SonnetDB 时的双写模式、采样校验、一致性报告和失败回滚。 | 📋 |
| #121 | **长稳、压测和故障恢复报告**：新增 7x24 小时 IoTSharp SonnetDB Profile 长稳脚本，覆盖 EF Core CRUD、遥测批量写入、缓存 TTL、对象 multipart、备份恢复、断电恢复、升级回滚；输出容量边界、性能曲线和生产建议。 | 📋 |
| #122 | **大量物理分表文件布局与启动扫描优化**：面向大量 measurement / 大量 segment 场景，设计并实现分层 segment 目录布局（例如按 segmentId 前缀或时间桶拆分）、目录枚举兼容层、备份扫描优化、旧段清理策略和布局迁移工具；保留旧 `segments/{id}.SDBSEG` 读取兼容。 | ✅ |
| #123 | **Compaction manifest 与重复段恢复**：为 compaction 引入 manifest 或等价 superseded segment 状态，记录 source segments、target segment、提交阶段和清理阶段；启动时根据 manifest 忽略或清理被替代旧段，解决 crash after swap before delete 后新旧段同时加载导致重复数据的问题。 | ✅ |
| #124 | **SegmentManager 增量索引与后台维护成本控制**：将 `AddSegment` / `SwapSegments` 从全量重建索引快照优化为增量更新或分层索引发布；补充大量 segment 下 flush、compaction、retention、query 并发时的 CPU、内存和暂停时间基准。 | 📋 |
| #125 | **大量 measurement / 长稳专项套件**：新增百万级 series、万级 measurement、海量小 segment、随机重启、后台 flush/compaction/retention 并发、重复数据检测和恢复时间统计；输出“能改善什么、不能改善什么”的容量边界报告。 | 📋 |
| #126 | **SQL 正则模式查询与 EF 翻译规划**：在 `LIKE` 基线之后引入正则匹配能力，第一阶段支持 `regexp_like(input, pattern[, flags])` 标量函数，可用于 `WHERE` 过滤与 `SELECT` 投影；同时评估 `expr REGEXP pattern`、`expr NOT REGEXP pattern`、`RLIKE` 别名，兼容 MySQL、SQLite 常见写法。第二阶段补 `regexp_substr`、`regexp_replace`、`regexp_instr`，并在 EF provider 中把 `Regex.IsMatch(...)` 翻译为 `regexp_like(...)`。所有正则执行必须设置超时、限制模式长度、缓存编译结果，并在执行计划中明确标注 scan filter；后续可识别 `^literal` 前缀模式做索引剪枝优化。 | 📋 |

### 推进顺序

```
#109（兼容矩阵）
  → #110（ADO.NET 事务 / async）
  → #111（DDL / schema metadata）
  → #112（查询能力）
  → #113（跨表事务 / 约束）
  → #114（EF Core provider MVP）
  → #115（EF migrations history / IoTSharp ApplicationDbContext 兼容）
  → #116（KV TTL / 缓存 Provider）
  → #117（S3 API）
  → #118（对象治理）
  → #119（SonnetDB Profile）
  → #120（迁移 / 双写 / 回滚）
  → #121（长稳 / 压测 / 报告）
  → #122（大量物理分表文件布局，已完成）
  → #123（Compaction manifest / 重复段恢复，已完成）
  → #124（增量索引 / 后台维护成本）
  → #125（大量 measurement / 长稳专项）
  → #126（正则模式查询）
```

### 验收标准

- `TelemetryStorage=SonnetDB` 可通过 IoTSharp 遥测写入、最新值、历史查询和聚合回归；
- `CachingUseIn=SonnetDB` 可通过 IoTSharp 现有 EasyCaching 调用路径，TTL 行为与 Redis/LiteDB/InMemory 有明确兼容矩阵，并作为新增选择存在；
- `DataBase=SonnetDB` 可通过 IoTSharp `ApplicationDbContext` 迁移历史表创建、迁移升级/回滚、重复迁移幂等检查、Identity 登录、租户/客户/设备/资产/规则 CRUD 和核心查询；
- SonnetDB SQL 模式匹配能力必须覆盖 `LIKE`、`NOT LIKE`、`regexp_like` 在 `WHERE` 与 `SELECT` 中的行为，并明确正则超时、模式长度、编译缓存和 scan filter 边界；
- S3-compatible API 已通过 IoTSharp BlobStorage 的上传、下载、删除、range read、multipart、presigned URL、版本、生命周期和审计回归；quota、Web Admin 与跨后端迁移继续推进；
- 向量搜索可通过 `VECTOR(N)`、KNN、向量索引重建、topK/distance 校验和租户/标签/时间过滤回归；
- 全文搜索可通过全文索引创建/删除/展示、中文/英文查询、BM25 排序、分页和索引重建回归；
- 迁移工具支持从 PostgreSQL/MySQL/SQLite + Redis/LiteDB + InfluxDB/TimescaleDB/Taos + 文件系统/S3 迁入，并可生成校验报告；
- SonnetDB Profile 必须支持双写校验和回滚，不能要求生产环境一次性切换；
- 长稳报告明确 SonnetDB Profile 的适用规模、单机边界、边缘部署边界和仍建议使用外部 PostgreSQL/Redis/S3 的场景。
- 大量物理分表场景必须覆盖启动目录扫描、备份枚举、compaction 清理、retention 删除和单目录文件数量上限，不再只以功能测试证明可用。
- Compaction 恢复必须证明崩溃后不会重复加载 source + target 段；若选择保守恢复，也必须有明确的重复检测与修复流程。
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
- **不**做跨产品迁移工具（属于 [Milestone 19](#milestone-19--iotsharp-生态数据底座选项关系--时序--kvcache--s3--搜索) 的 #120）。
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

## Milestone 22 — Agent Memory / Codebase Intelligence（代码知识库与 MCP Memory 后端）

> **目标**：把 SonnetDB 从“被 Agent 连接的数据库”推进到**Agent 的长期记忆和代码知识底座**。用户可以把任意 Git 仓库、设计文档、ADR、CI 变更、代码评审记录和 Agent 会话摄入到 SonnetDB，用 SQL / HTTP / MCP 查询“代码是什么、谁调用谁、为什么这么设计、改这里会影响哪里”。这不是仅供 SonnetDB 自己 Copilot 使用的内部索引，而是 SonnetDB 对外提供的原生工作负载与产品能力。
>
> **背景**：`codebase-memory-mcp` 展示了“代码库结构记忆 + MCP 工具”对 Agent 开发效率的价值：相比逐文件读取，Agent 更需要结构化的符号、调用边、文件变更、架构摘要与决策记录。SonnetDB 已经具备时序、关系、KV、文档、全文、向量、Hybrid Search 与 MCP 能力，本里程碑将这些能力组合成可复用的 **Code Memory Backend**。
>
> **设计原则**：
>
> 1. **数据库能力优先**。SonnetDB 负责 schema、存储、查询、全文/向量/混合检索、MCP 暴露和权限治理；语言解析器只是 ingest 辅助，不反过来绑架核心架构。
> 2. **对外产品形态**。CLI、HTTP API、MCP tools、VS Code 扩展和 Web Admin 都面向用户自己的仓库；SonnetDB Copilot 只是第一个 dogfooding 客户。
> 3. **Core 零依赖边界不破坏**。`src/SonnetDB.Core` 不引入 tree-sitter、Roslyn、libgit2 等大型运行时依赖；代码解析与 Git 扫描放在 CLI、独立 ingest 工具、扩展包或示例连接器中。
> 4. **结构化优先，向量补充**。文件、符号、调用边、引用边、commit、ADR、会话、工具调用都以结构化表/文档/边表落库；embedding 用于语义召回，不替代确定性的 symbol / edge 查询。
> 5. **安全只读默认**。MCP memory tools 默认只读，按 project/repo/branch/owner 隔离；代码片段读取要有大小限制、路径白名单和审计事件。
>
> **关键产出**：Code Memory 标准 schema + `sndb memory ingest` + 增量索引状态 + MCP Memory Server tools + Hybrid Search 示例 + Web Admin Code Memory Explorer + VS Code / Copilot 接入样例。

### 数据模型草案

| 类型 | 建议实体 | 用途 |
|------|----------|------|
| 仓库与文件 | `code_repositories`、`code_files`、`code_file_versions` | repo/project/branch/commit、路径、语言、hash、mtime、大小、license 元数据 |
| 符号与结构 | `code_symbols`、`code_symbol_locations` | namespace/type/method/property/endpoint/test 等符号定义与位置 |
| 关系边 | `code_edges` | calls / references / implements / tests / imports / routes_to / owns 等边 |
| 文本与向量 | `code_chunks` | 代码块、注释、README、docs、embedding、BM25/Hybrid Search |
| Git 演化 | `code_commits`、`code_changes` | commit 时间线、作者、文件变更、热点模块、变更趋势 |
| 决策与记忆 | `code_decisions`、`agent_memories`、`agent_tool_events` | ADR、设计决策、review 结论、Agent 会话摘要和工具调用审计 |

### PR 拆分

| PR | 主题 | 状态 |
|----|------|------|
| #150 | **Code Memory 标准 schema 与能力矩阵**：新增 `docs/code-memory.md`，定义 repo/file/symbol/edge/chunk/commit/decision/memory 的标准 schema、索引建议、权限模型、规模边界和与 Document / FullText / Vector / Hybrid Search 的映射；提供一组可直接运行的 `CREATE TABLE` / `CREATE DOCUMENT COLLECTION` / `CREATE FULLTEXT INDEX` / `VECTOR` 示例。 | 📋 |
| #151 | **`sndb memory ingest` 第一版（Git + 文件 + 文档块）**：CLI 新增 `sndb memory ingest --repo <path> --project <name> --db <db>`，扫描 Git 工作区、README/docs/source 文件，写入 repo/file/chunk/commit 基础数据；支持 include/exclude glob、最大文件大小、dry-run、fingerprint 增量、进度输出和取消。解析器第一版只做语言识别与文本切片，不要求完整 AST。 | 📋 |
| #152 | **C# 符号索引器（Roslyn 可选工具层）**：在 CLI/独立工具层引入可选 Roslyn 分析路径，提取 namespace/type/member/public API/test method/Minimal API route 等符号与定义位置；不进入 `src/SonnetDB.Core` 运行时依赖；输出写入 `code_symbols` / `code_symbol_locations`。 | 📋 |
| #153 | **调用边与引用边第一版**：基于 C# 编译语义模型提取 calls/references/implements/tests/imports/routes_to 边，落入 `code_edges`；提供 `EXPLAIN` / 统计视图展示每次索引的边数量、失败文件和不支持语法；对大型仓库支持分批提交和断点续跑。 | 📋 |
| #154 | **Code Memory 查询 API 与 MCP tools**：服务端新增只读 HTTP/MCP 工具 `code_search`、`symbol_search`、`code_callers`、`code_callees`、`code_impact`、`code_snippet`、`decision_search`，统一权限、row limit、snippet limit 与 structured content 返回；默认不开放任意图查询语言。 | 📋 |
| #155 | **Hybrid Search 与排序融合**：把 `code_chunks` 接入全文 BM25 + embedding KNN + metadata filter 融合，支持按 repo/branch/path/language/symbol/time 过滤；新增示例查询“找慢查询相关实现”“找 WAL replay 设计与测试”“找最近改动过的 API”。 | 📋 |
| #156 | **Agent Memory 持久化 API**：新增面向 Agent 的 memory 写入/读取契约，覆盖 conversation summary、tool event、decision note、todo、review finding、source citation；支持 TTL、owner/project/repo 隔离、敏感内容脱敏 hooks 和审计事件。 | 📋 |
| #157 | **Web Admin Code Memory Explorer**：新增 Code Memory 页面，支持 repo/project 列表、索引状态、文件/符号搜索、调用关系邻接列表、影响分析结果、代码片段引用、ADR/decision 检索与手动重建索引。 | 📋 |
| #158 | **VS Code / Copilot 接入样例**：在 SonnetDB for VS Code 路线中消费 Code Memory API/MCP tools，支持“解释当前符号”“查找调用者”“改动影响分析”“把当前 diff 写入 memory”；提供第三方 MCP Host 配置示例。 | 📋 |
| #159 | **规模、可靠性与发布文档**：用 SonnetDB 自身仓库、IoTSharp 仓库和一个中大型开源 C# 仓库做 profile，输出 ingest 时间、索引大小、查询延迟、Hybrid Search 命中率、增量重建成本和恢复测试报告；README 增加 Agent Memory / Codebase Intelligence 能力矩阵。 | 📋 |

### 推进顺序

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
| #178 | **C ABI Document 分组**：新增 collection CRUD、find page、insert/update/delete、aggregate 的 JSON payload 函数；保持 JSON/UTF-8 边界，不暴露内部 document 类型。 | 📋 |
| #179 | **C ABI Object Storage 分组**：新增 bucket/object put/get/range/list/delete 与 multipart 基础函数；大对象采用 streaming/chunk handle，避免一次性内存复制。 | 📋 |
| #180 | **C ABI MQ 分组**：新增 topic publish/pull/ack/stats 函数；明确 offset、consumer group、ack 语义并对齐 `SndbMqClient`。 | 📋 |
| #181 | **上层语言连接器同步包装**：Go / Rust / Java / Python 优先同步 bulk + KV + Document；VB6 / PureBasic 作为源码级示例按能力选择性暴露。 | 📋 |

### 验收标准

- C ABI SQL-only 旧 quickstart 不改代码仍能运行。
- C ABI 可以通过 `Data Source=sonnetdb+http://...;Mode=Remote` 连接远程服务执行 SQL。
- 每个新增 ABI 分组都有 C quickstart 和至少一个上层语言 smoke。
- NativeAOT publish、CMake quickstart、Java JNI/FFM quickstart 在 Windows/Linux 能继续通过可用工具链验证。

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
| 19 | IoTSharp 生态数据底座选项（关系 + 时序 + KV/缓存 + S3 + 搜索 + 大量物理分表长稳） | #109 ~ #125 | 🚧（#109~#117、#122/#123 已完成） |
| 20 | 多模能力对齐与平移测试（Parity） | #127 ~ #136 | ✅（实现已落地；nightly 稳定率继续按 `parity-results` 监控） |
| 21 | Document Store 单机能力升级（MongoDB-like，不做协议兼容） | #137 ~ #146 | 🚧（#137~#144 已完成） |
| 22 | Agent Memory / Codebase Intelligence（代码知识库与 MCP Memory 后端） | #150 ~ #159 | 📋 |
| 23 | 搜索与向量引擎合并（DotSearch / DotVector 收编） | #160 ~ #169 | ✅ |
| 24 | SonnetDB Studio 管理体验升级（Document 管理面） | #170 ~ #172 | 📋 |
| 25 | Document Store 验收、文档与发布治理 | #173 ~ #174 | 📋 |
| 26 | 连接器路线独立化（C ABI + 多模型 API） | #175 ~ #181 | 🚧（#175/#176/#177 已完成） |
| MM9 | 多模型统一备份、恢复和管理工具第一批 | BackupService + sndb backup | ✅ |

**当前推进顺序**：Milestone 14（Copilot）、Milestone 15（地理空间）、Milestone 16（Copilot 产品化升级）与 Milestone 20（Parity #127~#136 实现）均已合并；新增 **Milestone 23（搜索与向量引擎合并）** 作为当前结构收敛主线，先完成 DotSearch / DotVector 收编，降低独立模块维护成本，再回到 Milestone 17 的可观测性增强。**Milestone 18（VS Code 扩展）** 继续并行推进，建议先以 `#99 ~ #103` 打出第一个“远程连接 + Explorer + SQL + 结果视图”闭环。**Milestone 19（IoTSharp 生态数据底座选项）** 已纳入正式规划，#109~#117 与 #122/#123 已完成；后续继续推进对象治理、Profile 周边、增量索引 / 后台维护成本与大量 measurement 长稳专项。**Milestone 21（Document Store 单机能力升级）** 已完成 #137~#146；Studio 管理面进入 **Milestone 24**，MongoDB 参考 parity、长稳、容量报告和发布文档进入 **Milestone 25**。**Milestone 22（Agent Memory / Codebase Intelligence）** 作为面向 Agent 生态的对外数据库能力线进入规划，建议在 M18 VS Code 基础闭环与 M21 Document/Hybrid Search 能力稳定后，从 #150 的标准 schema 与文档开始派单。SonnetDBEE C5.7 / MM9 的开源核心第一批已提供 `BackupService` 和 `sndb backup create/inspect/verify/restore`，企业级定时、增量、审计和 UI 编排继续由 SonnetDBEE 承接。**Milestone 20** 后续不再按 #129 继续派单，而是通过 `.github/workflows/parity.yml`、`parity-results` 分支与 `tests/SonnetDB.Parity/reports/sample-run.md` 持续暴露能力缺口、SKIP 原因和 nightly 稳定性。

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
16. **新增 Milestone 20 — 多模能力对齐与平移测试 (Parity)**：用一份 docker-compose 同时拉起 SonnetDB 与开源全家桶（PostgreSQL / Redis / InfluxDB / VictoriaMetrics / MinIO / NATS / Mosquitto / Meilisearch / Qdrant / ClickHouse），用同一套 `IDataPlane` 适配器跑同一套场景，证明"一台 SonnetDB 在边缘 / 单机场景能替掉这一组组件"。**显式不做协议兼容**（自有 `SndbConnection` / `SndbMqClient` / `SndbObjectStorageClient` / EF Core provider，竞品走它们的官方 .NET 客户端），**显式不做替代主张**（不对齐 Redis Cluster / Kafka / Postgres HA / MinIO 集群），三类对齐边界为：能力对齐、可靠性对齐、算法准确度对齐；性能数字写报告不做 gating。本里程碑同时连带把 KV `INCR/CAS/EXPIRE/PERSIST/TTL`、SonnetMQ `RecordTypeTombstone` 段滚动 + `FlushOnPublish=true` 默认、对象桶 `ListObjectsV2 ContinuationToken` 分页、`tests/SonnetDB.CrashTests/` 真子进程 SIGKILL 注杀、README 措辞与代码同步落地。详细设计见 [`docs/parity-roadmap.md`](docs/parity-roadmap.md)。
17. **新增 Milestone 21 — Document Store 单机能力升级（MongoDB-like，不做协议兼容）**：在 MM5 JSON 文档能力、MM6 全文索引、MM8 Hybrid Search 与 Milestone 20 parity 基础上，把 SonnetDB Document Store 推进到 MongoDB 单机常用能力子集。范围仅保留能力和功能实现：Document API / client、find filter、projection、sort、cursor、局部更新操作符、复合 / unique / sparse / partial / TTL 索引、aggregation pipeline 子集、单文档原子性、批量写轻事务、validator 执行能力与文档容量底座。该里程碑**明确不做 MongoDB wire protocol / BSON command / 官方 driver 直连兼容**，也不交付 Studio 管理面、MongoDB 参考 parity、长稳报告或发布文档。
18. **新增 Milestone 22 — Agent Memory / Codebase Intelligence（代码知识库与 MCP Memory 后端）**：吸收 codebase-memory-mcp 代表的“代码库结构记忆 + MCP 工具”产品形态，把 SonnetDB 定位为 Agent 的长期记忆与代码知识底座，而不是仅供 SonnetDB Copilot 自用的内部 RAG。范围包括 Code Memory 标准 schema、Git/files/chunks ingest、C# 符号与调用边索引、只读 MCP typed tools、Hybrid Search 融合、Agent Memory 持久化 API、Web Admin Code Memory Explorer、VS Code / Copilot 接入样例与规模报告。该里程碑明确 `src/SonnetDB.Core` 不引入 tree-sitter/Roslyn/libgit2 等大型运行时依赖，语言解析与 Git 扫描放在 CLI/扩展/测试/示例工具层。
19. **新增 Milestone 23 — 搜索与向量引擎合并**：DotSearch / DotVector 不再作为 SonnetDB 之外的独立产品线继续扩张。BM25、分词、距离计算、HNSW / IVF / Vamana、量化和索引序列化逐步收编到 `src/SonnetDB.Core`；`Microsoft.Extensions.VectorData` adapter 迁移到 `src/SonnetDB.Data`。路线见 [`docs/search-vector-engine-consolidation-roadmap.md`](docs/search-vector-engine-consolidation-roadmap.md)。
20. **新增 Milestone 24 — SonnetDB Studio 管理体验升级（Document 管理面）**：承接从 Milestone 21 迁出的管理相关内容，集中做 Document Explorer、validator governance、索引管理、JSONL/NDJSON 导入导出、rebuild / dry-run / 审批等 Studio 体验；本里程碑只补必要 metadata / maintenance contract，不新增 Document Store 查询、索引、事务或存储能力。
21. **新增 Milestone 25 — Document Store 验收、文档与发布治理**：承接从 Milestone 21 迁出的文档和发布治理内容，集中做 MongoDB 参考 parity、百万 / 千万文档长稳 profile、容量报告、README / docs 能力矩阵、MongoDB-like 迁移指南、不支持项和推荐规模说明。


## 性能优化待办（2026 审计后回收的中等优先项）

以下是一次完整审计后留下的纯性能优化点；功能上是对的，只是热路径里有可优化的常数因子或代数复杂度。每项都有目标位置和现状成本，便于后续按需安排。

| 编号 | 位置 | 现状 | 建议改造 | 估时 |
|------|------|------|---------|------|
| P1 | `src/SonnetDB.Core/Query/KnnExecutor.cs:103` | 每个候选都调用 `TombstoneTable.IsCovered` —— 内部锁 + `ToArray()` 快照 | 提到 ScanSegment 之前一次性拿快照（已在 KnnExecutor 顶层做 GetForSeriesField 检查），把候选过滤改成直接遍历该快照 | 15 分钟 |
| P2 | `src/SonnetDB.Core/Sql/Execution/RelationalSelectExecutor.cs` 子查询路径 | 同一个子查询 SELECT 子树在每个外层行上重新执行；只要不引用外层列就能 memoize | 对 ExistsExpression / SubqueryExpression 加 `Cache<SelectStatement, IReadOnlyList<...>\>`，先做一次 "是否相关" 静态判定；非相关查询执行 0 或 1 次 | 30 分钟 |
| P3 | `src/SonnetDB.Core/FullText/DocumentFullTextIndexStore.cs` ExpandFuzzyTermQuery | 模糊扩展时把 tombstoned term 也参与编辑距离计算 | 让内置全文引擎的 EnumerateTerms 暴露一份 "未 tombstone" 视图，或者在 PersistentFullTextIndex 端先过滤；当前简单做法是上层把展开候选再用一次 Search 验证 | 10 分钟 |
| P4 | `src/SonnetDB.Core/Tables/TableManager.cs` ExpandCascadeDeletesLocked | BFS 每一步都对子表做 `childStore.Scan()` 全表线性扫描——O(parents × FKs × N) | 在子表 FK 列上建临时哈希索引（`Dictionary<keyBytes, List<row>>`），或直接给 FK 列建持久化二级索引，cascade 改成索引查找 | 60 分钟 |

这些不阻塞功能正确性，不影响 parity 通过率，并且在小数据量上不会被察觉。当任一线上场景遇到瓶颈时（高基数 KNN / 重相关子查询 / 高基数 fuzzy / 万行级 cascade）按需挑出来做。
