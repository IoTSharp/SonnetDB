# SonnetDB

[中文](README.md) | [English](README.en.md)

[![CI](https://github.com/IoTSharp/SonnetDB/actions/workflows/ci.yml/badge.svg)](https://github.com/IoTSharp/SonnetDB/actions/workflows/ci.yml)
[![CodeQL](https://github.com/IoTSharp/SonnetDB/actions/workflows/codeql.yml/badge.svg)](https://github.com/IoTSharp/SonnetDB/actions/workflows/codeql.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![GitHub Release](https://img.shields.io/github/v/release/IoTSharp/SonnetDB?label=Release)](https://github.com/IoTSharp/SonnetDB/releases)

## 🚀 SonnetDB 是什么

SonnetDB 是一个面向 IoT、工业数据、可观测性与实时分析场景的时序数据库。它同时提供：

- 🧩 进程内嵌入式引擎（低延迟、易集成）
- 🌐 HTTP 服务端（Admin UI、Help、认证和权限）
- 🔌 多语言连接器（C、Go、Rust、Java、Python、VB6、PureBasic）
- 🛠️ 标准 ADO.NET 与 CLI 工具链

> 当前版本采用目录化持久化布局（catalog/schema/WAL/segment/tombstone 文件集合），不是单文件数据库。

## 🏷️ 生态下载与版本（小标签）

### 📦 NuGet

[![SonnetDB.Core Version](https://img.shields.io/nuget/v/SonnetDB.Core?label=SonnetDB.Core)](https://www.nuget.org/packages/SonnetDB.Core)
[![SonnetDB.Core Downloads](https://img.shields.io/nuget/dt/SonnetDB.Core?label=Downloads)](https://www.nuget.org/packages/SonnetDB.Core)
[![SonnetDB.Data Version](https://img.shields.io/nuget/v/SonnetDB.Data?label=SonnetDB.Data)](https://www.nuget.org/packages/SonnetDB.Data)
[![SonnetDB.Data Downloads](https://img.shields.io/nuget/dt/SonnetDB.Data?label=Downloads)](https://www.nuget.org/packages/SonnetDB.Data)
[![SonnetDB.Caching Version](https://img.shields.io/nuget/v/SonnetDB.Caching?label=SonnetDB.Caching)](https://www.nuget.org/packages/SonnetDB.Caching)
[![SonnetDB.Caching Downloads](https://img.shields.io/nuget/dt/SonnetDB.Caching?label=Downloads)](https://www.nuget.org/packages/SonnetDB.Caching)
[![SonnetDB.Cli Version](https://img.shields.io/nuget/v/SonnetDB.Cli?label=SonnetDB.Cli)](https://www.nuget.org/packages/SonnetDB.Cli)
[![SonnetDB.Cli Downloads](https://img.shields.io/nuget/dt/SonnetDB.Cli?label=Downloads)](https://www.nuget.org/packages/SonnetDB.Cli)

### 🐳 Docker

[![Docker Image](https://img.shields.io/docker/v/iotsharp/sonnetdb?label=iotsharp/sonnetdb&sort=semver)](https://hub.docker.com/r/iotsharp/sonnetdb)
[![Docker Pulls](https://img.shields.io/docker/pulls/iotsharp/sonnetdb?label=Docker%20Pulls)](https://hub.docker.com/r/iotsharp/sonnetdb)
[![Docker Download](https://img.shields.io/badge/Download-docker%20hub-0db7ed)](https://hub.docker.com/r/iotsharp/sonnetdb)
[![GHCR Package](https://img.shields.io/badge/GHCR-ghcr.io%2Fiotsharp%2Fsonnetdb-2ea44f)](https://github.com/IoTSharp/SonnetDB/pkgs/container/sonnetdb)

### 🔌 连接器与下载入口

[![C Connector](https://img.shields.io/badge/C-Connector-blue)](connectors/c/README.md)
[![Go Connector](https://img.shields.io/badge/Go-Connector-00ADD8)](connectors/go/README.md)
[![Rust Connector](https://img.shields.io/badge/Rust-Connector-DEA584)](connectors/rust/README.md)
[![Java Connector](https://img.shields.io/badge/Java-Connector-f89820)](connectors/java/README.md)
[![Python Connector](https://img.shields.io/badge/Python-Connector-3776AB)](connectors/python/README.md)
[![VB6 Connector](https://img.shields.io/badge/VB6-Connector-5C2D91)](connectors/vb6/README.md)
[![PureBasic Connector](https://img.shields.io/badge/PureBasic-Connector-5A5A5A)](connectors/purebasic/README.md)
[![Connector Releases](https://img.shields.io/badge/Downloads-GitHub%20Releases-black)](https://github.com/IoTSharp/SonnetDB/releases)

## ✨ 核心特性（门面概览）

- ⚡ 高吞吐写入：支持 SQL、Line Protocol、JSON、Bulk fast-path。
- 🧠 丰富 SQL 能力：聚合、窗口函数、预测函数、控制函数（PID）和地理空间分析。
- 🗺️ GeoSpatial：`GEOPOINT`、轨迹长度/重心/速度统计、围栏查询、GeoJSON 输出。
- 🔐 服务端控制面：用户、数据库、授权、Token 生命周期管理。
- 🧪 基准体系：持续对比 InfluxDB、TDengine、IoTDB、TimescaleDB、SQLite、LiteDB。
- 🛠️ Web Admin Workbench：Schema Explorer 数据库树、SQL Editor、Staged Preview、Result Grid、Trajectory 模式切换，Copilot 继续保持原来的全局浮窗。

## � 为什么选择 SonnetDB

- **嵌入式 + 服务端双形态**：既可作为进程内零依赖引擎直接集成到你的应用，也能以独立 HTTP 服务运行，满足从边缘设备到云端的全场景部署需求。
- **标准 SQL 操作时序数据**：无需学习私有查询语言，用熟悉的 SQL 完成写入、查询、聚合、预测、GeoSpatial 分析。
- **比主流时序数据库快近 2×**：自有 Server-vs-Server 基准测试显示，相同硬件下查询吞吐约为同类产品的 **1.98×**。
- **原生多语言生态**：官方维护 C、Go、Rust、Java、Python、VB6、PureBasic 连接器，ADO.NET 驱动直接对接 .NET 生态。
- **开箱即用的控制面**：内置用户、权限、Token 管理与 Admin UI，无需额外运维工具链。
- **MIT 协议，完全开源**：自由使用、修改与分发，企业版功能通过 [sonnetdb.com](https://sonnetdb.com) 按需订阅。

## 🌐 官网与资源

| 入口 | 地址 |
| --- | --- |
| 官方主页 | https://sonnetdb.com |
| 在线文档 | https://sonnetdb.com/docs |
| 开源仓库 | https://github.com/IoTSharp/SonnetDB |
| 企业版与云平台 | https://sonnetdb.com/platform |

## 当前组成

| 组件 | 说明 |
| --- | --- |
| `src/SonnetDB` | 嵌入式时序引擎，负责 schema、写入、查询、删除、WAL、MemTable、Segment、Compaction、Retention |
| `src/SonnetDB.Data` | ADO.NET 提供程序，统一支持本地嵌入式和远程 `SonnetDB` |
| `src/SonnetDB.Cli` | 命令行工具 `sndb`：本地/远程连接、profile 管理（`local`/`remote`/`connect`）、交互式 REPL |
| `src/SonnetDB` | HTTP 服务、首次安装流程、用户与授权、SSE、Admin UI、内置 `/help` 文档站点 |
| `web` | 管理后台前端（包含 SonnetDB Workbench、全局 CopilotDock 与 SPA 发布静态资源） |
| `docs` | JekyllNet 文档站点源码；构建镜像时会生成并打包到 `/help` |

## 快速开始

### 1. 嵌入式最小示例

```csharp
using SonnetDB.Engine;
using SonnetDB.Sql.Execution;

using var db = Tsdb.Open(new TsdbOptions
{
    RootDirectory = "./demo-data",
});

SqlExecutor.Execute(db, """
CREATE MEASUREMENT cpu (
    host TAG,
    usage FIELD FLOAT
)
""");

SqlExecutor.Execute(db, """
INSERT INTO cpu (time, host, usage)
VALUES (1713676800000, 'server-01', 0.71)
""");

var result = (SelectExecutionResult)SqlExecutor.Execute(
    db,
    "SELECT time, host, usage FROM cpu WHERE host = 'server-01'")!;

foreach (var row in result.Rows)
{
    Console.WriteLine($"{row[0]} {row[1]} {row[2]}");
}
```

### 2. 启动服务端

```bash
docker build -f src/SonnetDB/Dockerfile -t sonnetdb .
docker run --rm -p 5080:5080 -v ./sonnetdb-data:/data sonnetdb
```

仓库的 Docker 发布工作流会额外构建并推送预编译镜像 `iotsharp/sonnetdb` 与 `ghcr.io/<owner>/sonnetdb`。当仓库 Secrets 配置完成后，也可以直接拉取：

```bash
docker run --rm -p 5080:5080 -v ./sonnetdb-data:/data iotsharp/sonnetdb:latest
```

启动后访问：

- `http://127.0.0.1:5080/admin/`
- `http://127.0.0.1:5080/help/`

当 `/data/.system` 为空时，`/admin/` 会进入首次安装流程，要求设置：

- 服务器 ID
- 组织名称
- 管理员用户名
- 管理员密码
- 初始静态 Bearer Token

### 3. 通过 ADO.NET 访问

```csharp
using SonnetDB.Data;

using var connection = new SndbConnection("Data Source=./demo-data");
connection.Open();

using var command = connection.CreateCommand();
command.CommandText = "SELECT count(*) FROM cpu";

var count = (long)(command.ExecuteScalar() ?? 0L);
Console.WriteLine(count);
```

远程连接示例：

```csharp
using SonnetDB.Data;

using var connection = new SndbConnection(
    "Data Source=sonnetdb+http://127.0.0.1:5080/metrics;Token=your-token");
connection.Open();
```

### 4. 通过 CLI 访问

```bash
# 安装
dotnet tool install --global SonnetDB.Cli --version 0.1.0

# 本地直接使用
sndb local --path ./demo-data --command "SELECT count(*) FROM cpu"

# 保存 profile，下次免输路径
sndb local --path ./demo-data --save-profile home --default
sndb connect home --command "SELECT count(*) FROM cpu"

# 连接远程服务端
sndb remote --url http://127.0.0.1:5080 --database metrics --token your-token --repl
```

更完整的 CLI、ADO.NET、嵌入式、远程和批量写入示例见 [docs](docs/index.md)。
Web Admin Workbench（Schema Explorer / SQL Editor / Staged Preview / Result Grid / Trajectory 模式）说明与验收清单见 [docs/web-workbench.md](docs/web-workbench.md)。

## 数据模型

SonnetDB 采用典型的时序数据建模方式：

| 概念 | 说明 |
| --- | --- |
| `measurement` | 一类时序对象或指标集合，例如 `cpu`、`memory`、`meter_reading` |
| `tag` | 参与序列身份识别和过滤的维度，例如 `host`、`region`、`device_id` |
| `field` | 真实观测值，例如 `usage`、`temperature`、`status` |
| `time` | 保留时间列，作为写入、查询、删除的时间轴，不需要在 schema 中单独声明 |
| `series` | `measurement + sorted(tags)` 规范化后形成的逻辑时间序列 |

支持的 field 类型：

- `FLOAT`
- `INT`
- `BOOL`
- `STRING`

建模建议：

- 把筛选维度放在 tag。
- 把随时间变化的采样值放在 field。
- 保持同一个 measurement 的 schema 稳定。
- 使用 `time` 作为唯一的时间列，而不是额外再定义一个“时间字段”。

## SQL 与访问方式

当前真实支持的数据面 SQL：

- `CREATE MEASUREMENT`
- `CREATE TABLE ... PRIMARY KEY (...)`
- `CREATE [UNIQUE] INDEX ... ON table (...)` / `DROP INDEX ... ON table`
- `INSERT INTO ... VALUES (...)`
- `SELECT ... FROM ... [WHERE ...] [GROUP BY time(...)]`
- `SELECT ... FROM measurement JOIN table ON measurement.tag = table.column ...`（MM4 时序 JOIN 关系维表第一版）
- `UPDATE ... SET ... WHERE ...`（关系表）
- `DELETE FROM ... WHERE ...`
- `BEGIN [TRANSACTION]` / `COMMIT` / `ROLLBACK`（关系表单表小批量轻事务）
- `SHOW MEASUREMENTS` / `SHOW TABLES` / `SHOW INDEXES ON table` / `DESCRIBE [MEASUREMENT] <name>` / `DESCRIBE TABLE <name>`
- `EXPLAIN <read-only statement>`（当前支持 `SELECT`、`SHOW MEASUREMENTS` / `SHOW TABLES` / `SHOW INDEXES` 与 `DESCRIBE [MEASUREMENT|TABLE]`，并返回 table 查询的 `access_path` / `index_name`）

### 支持的 SQL 函数

随 PR #50 ~ #56 落地，SonnetDB 在 `SELECT` 中支持以下内置函数；可通过 [`Tsdb.Functions`](docs/extending-functions.md) 注册自定义聚合 / 标量 / TVF（嵌入式模式）。Server 模式默认禁用 UDF。

| 函数 | 类别 | 引入 | 对标 | 备注 |
| --- | --- | --- | --- | --- |
| `count`, `sum`, `min`, `max`, `avg`, `first`, `last` | 聚合 (Tier 1) | PR #50 | InfluxDB / Timescale / TDengine 全量 | 基础聚合，支持 `GROUP BY time(...)` |
| `stddev`, `variance`, `spread`, `mode`, `median` | 聚合 (Tier 2) | PR #52 | InfluxDB `stddev` / TDengine `STDDEV` | 总体方差 / 标准差 |
| `percentile(x, p)`, `p50`, `p90`, `p95`, `p99`, `tdigest_agg` | 聚合 (T-Digest) | PR #52 | InfluxDB `quantile(method:"estimate_tdigest")`, TDengine `APERCENTILE` | 分位估计，常数空间复杂度 |
| `distinct_count` | 聚合 (HyperLogLog) | PR #52 | InfluxDB `distinct() \|> count()`, TDengine `HYPERLOGLOG` | 基数估计 |
| `histogram(x, n)` | 聚合 | PR #52 | Prometheus `histogram_quantile` 数据源 | 等宽分桶 |
| `pid(target, kp, ki, kd)`, `pid_estimate` | 聚合 (Control) | PR #54 | — | 增量式 PID 控制律输出 |
| `abs`, `round`, `sqrt`, `log`, `coalesce`, `time_bucket`, `date_trunc`, `extract`, `cast` | 标量 (Tier 1) | PR #51 | TDengine 标量函数 / Postgres `date_trunc` | 行级表达式 |
| `difference`, `delta`, `increase` | 窗口 | PR #53 | InfluxDB `difference` / TDengine `DIFF` | 相邻差分 |
| `derivative`, `non_negative_derivative`, `rate`, `irate` | 窗口 | PR #53 | InfluxDB `derivative` / Prometheus `rate` / TDengine `DERIVATIVE` | 时间归一化变化率 |
| `cumulative_sum`, `integral` | 窗口 | PR #53 | InfluxDB `cumulativeSum` / Timescale `time_weight` | 累积 / 时间加权积分 |
| `moving_average`, `ewma`, `holt_winters` | 窗口 (平滑) | PR #53 | InfluxDB `movingAverage` / `holtWinters`, TDengine `MAVG` | EMA / Holt 双指数平滑 |
| `fill`, `locf`, `interpolate` | 窗口 (插值) | PR #53 | InfluxDB `fill()`, TDengine `INTERP` | 缺失填充 |
| `state_changes`, `state_duration` | 窗口 (状态) | PR #53 | TDengine `STATECOUNT` / `STATEDURATION` | 状态机变换计数 |
| `pid_series` | 窗口 (Control) | PR #54 | — | 流式 PID 时序 |
| `anomaly(x, 'zscore'\|'iqr', k)`, `changepoint(x, 'cusum', k, drift)` | 窗口 (异常 / 变点) | PR #55 | TDengine `STATEDURATION` 配套 | 标记型 0/1 / 累积变点统计 |
| `forecast(measurement, field, n, 'linear'\|'holt_winters', [season])` | TVF | PR #55 | TDengine `FORECAST`(3.3.6+), Influx `holtWinters` | 表值函数：未来 N 步外推 |
| `lat`, `lon`, `geo_distance`, `geo_bearing`, `geo_within`, `geo_bbox`, `geo_speed` | 地理空间标量 | PR #70/#71 | PostGIS 部分语义 | `GEOPOINT` 经纬度提取、距离、方位、圆形 / 矩形围栏与速度 |
| `trajectory_length`, `trajectory_centroid`, `trajectory_bbox`, `trajectory_speed_max/avg/p95` | 轨迹聚合 | PR #72 | PostGIS / Timescale 轨迹分析场景 | 轨迹总路程、重心、外包框与速度统计，支持 `GROUP BY time(...)` |
| 用户自定义 | UDF | PR #56 | — | 嵌入式模式可注册聚合 / 标量 / TVF；详见 `docs/extending-functions.md` |

> 函数族基准：见 `tests/SonnetDB.Benchmarks/Benchmarks/FunctionBenchmark.cs`，对比 InfluxDB Flux 与 TDengine REST 的等价语义。

当前真实支持的服务端控制面 SQL：

- `CREATE USER`
- `ALTER USER ... WITH PASSWORD`
- `DROP USER`
- `CREATE DATABASE`
- `DROP DATABASE`
- `GRANT READ|WRITE|ADMIN ON DATABASE ... TO ...`
- `REVOKE ON DATABASE ... FROM ...`
- `SHOW USERS`
- `SHOW GRANTS [FOR user]`
- `SHOW DATABASES`
- `SHOW TOKENS [FOR user]`
- `ISSUE TOKEN FOR user`
- `REVOKE TOKEN 'tok_xxx'`

支持的访问路径：

- 进程内引擎：`SonnetDB.Engine.Tsdb`
- 嵌入式 SQL：`SonnetDB.Sql.Execution.SqlExecutor`
- ADO.NET：`SonnetDB.Data`
- CLI：`SonnetDB.Cli`
- 远程 HTTP：`SonnetDB`
- 批量快路径：`CommandType.TableDirect` 或 `/v1/db/{db}/measurements/{m}/{lp|json|bulk}`

## 架构总览

```text
Application / CLI / ADO.NET / Admin UI
                |
                v
      SQL / Remote HTTP / TableDirect
                |
                v
     Query Engine / Control Plane / Auth
                |
                v
  WAL -> MemTable -> Flush -> Segment -> Compaction
                |
                v
   catalog.SDBCAT / measurements.tslschema
   wal/*.SDBWAL / segments/*.SDBSEG
   tombstones.tslmanifest
```

数据库目录的实际布局：

```text
<database-root>/
├─ catalog.SDBCAT
├─ measurements.tslschema
├─ tombstones.tslmanifest
├─ wal/
│  └─ {startLsn:X16}.SDBWAL
└─ segments/
   └─ {id:X16}.SDBSEG
```

服务端控制面目录：

```text
<data-root>/.system/
├─ installation.json
├─ users.json
└─ grants.json
```

## 性能基准

> 以下摘要图来自 **PR #49** 同机粗略对比（i9-13900HX / Windows 11 / .NET 10.0.6 / Docker Desktop + WSL2，全集 24 个基准约 20 分钟）。InfluxDB 2.7、TDengine 3.3.4.3、`sonnetdb` 均跑在本机 Docker 容器中，仅作同机粗略对比，不代表生产部署性能。完整数据见 [tests/SonnetDB.Benchmarks/README.md](tests/SonnetDB.Benchmarks/README.md)。

![SonnetDB benchmark summary](docs/assets/benchmark-summary.svg)

一键运行本机基准：

```bash
dotnet run --project eng/benchmarks/run-benchmarks/run-benchmarks.csproj -- --filter *
```

基准入口由两个跨平台 C# 小工具组成：`start-benchmark-env.csproj` 负责构建并启动 [docker-compose.yml](tests/SonnetDB.Benchmarks/docker/docker-compose.yml) 中的 `sonnetdb`、InfluxDB、TDengine，并等待健康检查通过；`run-benchmarks.csproj` 负责调用环境入口并以 Release 模式运行 BenchmarkDotNet。README 只引用 [benchmark-summary.svg](docs/assets/benchmark-summary.svg)，后续刷新数字时优先更新 SVG 与 [基准 README](tests/SonnetDB.Benchmarks/README.md)，不需要反复改根 README 的表格。

2026-05-06 新增一组与 Apache IoTDB 的同口径“Server vs Server”写入对比：两边都走 HTTP 服务端，不再使用 SonnetDB 嵌入式路径；测试规模为 1,000 设备 × 30 字段 × 12 时间点，按 `AB BA AB BA` 四轮执行。实测 SonnetDB Server 平均吞吐为 **22,867 values/sec**，IoTDB 为 **11,541 values/sec**，SonnetDB 约快 **1.98x**。详细日志、分轮表格与旧的“嵌入式 vs REST”口径说明见 [tests/SonnetDB.Benchmarks/Benchmarks/DATABASE_COMPARISON_BENCHMARK_README.md](tests/SonnetDB.Benchmarks/Benchmarks/DATABASE_COMPARISON_BENCHMARK_README.md)。

向量召回基准见 [tests/SonnetDB.Benchmarks/Benchmarks/VectorRecallBenchmark.cs](tests/SonnetDB.Benchmarks/Benchmarks/VectorRecallBenchmark.cs) 与 [tests/SonnetDB.Benchmarks/README.md](tests/SonnetDB.Benchmarks/README.md)：当前已回填 SonnetDB 自身 `10k / 100k` 两档的 brute-force vs HNSW 实测耗时；`1M` 通过环境变量显式开启，`sqlite-vec` / `pgvector` 同机粗略对比结果区也已在基准 README 预留，后续如具备环境可单独补数。

地理空间与轨迹基准见 [tests/SonnetDB.Benchmarks/Benchmarks/GeoQueryBenchmark.cs](tests/SonnetDB.Benchmarks/Benchmarks/GeoQueryBenchmark.cs) 与 [docs/geo-spatial.md](docs/geo-spatial.md)：默认覆盖 `100k` 轨迹点的 `geo_within`、`geo_bbox`、`trajectory_length` 与 `GEOPOINT` range scan；`1M` 通过 `SONNETDB_GEO_BENCH_INCLUDE_1M=1` 显式开启。

## 设计原则

- Safe-only：核心库在当前阶段不使用 `unsafe`，底层内存操作基于 `Span<T>`、`MemoryMarshal`、`BinaryPrimitives` 等安全 API。
- Schema-first：measurement 先定义 schema，再写入数据，减少类型漂移和歧义。
- Embedded-first：引擎优先服务进程内场景，再通过 ADO.NET、CLI 和 HTTP 暴露统一能力。
- Directory-based persistence：数据库以目录为单位持久化，不再追求单文件存储。
- Single API surface：本地和远程尽量复用同一套 SQL、ADO.NET 和 CLI 使用方式。
- Docs-in-image：`docs/` 在 Docker 构建时会通过 JekyllNet 构建进镜像并挂到 `/help`。

## 仓库结构

```text
SonnetDB/
├─ src/
│  ├─ SonnetDB/
│  ├─ SonnetDB.Data/
│  ├─ SonnetDB.Cli/
│  └─ SonnetDB/
├─ tests/
├─ web/
├─ docs/
├─ CHANGELOG.md
├─ ROADMAP.md
└─ AGENTS.md
```

## 文档导航

根 README 只保留项目概览和最短入门路径，详细示例统一放到 `docs/`：

- [开始使用](docs/getting-started.md)
- [数据模型](docs/data-model.md)
- [KV Keyspace](docs/kv-keyspace.md)
- [SQL 参考](docs/sql-reference.md)
- [嵌入式与 in-proc API](docs/embedded-api.md)
- [ADO.NET 参考](docs/ado-net.md)
- [CLI 参考](docs/cli-reference.md)
- [批量写入](docs/bulk-ingest.md)
- [架构总览](docs/architecture.md)
- [文件格式与目录布局](docs/file-format.md)
- [发布与打包](docs/releases/README.md)

## 相关文件

- 路线图见 [ROADMAP.md](ROADMAP.md)
- 变更记录见 [CHANGELOG.md](CHANGELOG.md)
- AI 协作规范见 [AGENTS.md](AGENTS.md)

## License

[MIT](LICENSE)
