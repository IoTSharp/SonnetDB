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
| MM9 | 多模型统一备份、恢复和管理工具第一批 | BackupService + sndb backup | ✅ |

**当前推进顺序**：Milestone 14（Copilot）与 Milestone 16（Copilot 产品化升级）均已合并；当前主线转向 **Milestone 17（可观测性与运行时可见性）**，从 PR #89（Core Meter / ActivitySource 基线）起步。Milestone 15（地理空间）已完成并收尾。**Milestone 18（VS Code 扩展）** 也可并行推进，建议先以 `#99 ~ #103` 打出第一个“远程连接 + Explorer + SQL + 结果视图”闭环。SonnetDBEE C5.7 / MM9 的开源核心第一批已提供 `BackupService` 和 `sndb backup create/inspect/verify/restore`，企业级定时、增量、审计和 UI 编排继续由 SonnetDBEE 承接。

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



