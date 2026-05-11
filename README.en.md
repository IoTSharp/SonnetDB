# SonnetDB

[中文](README.md) | [English](README.en.md)

[![CI](https://github.com/IoTSharp/SonnetDB/actions/workflows/ci.yml/badge.svg)](https://github.com/IoTSharp/SonnetDB/actions/workflows/ci.yml)
[![CodeQL](https://github.com/IoTSharp/SonnetDB/actions/workflows/codeql.yml/badge.svg)](https://github.com/IoTSharp/SonnetDB/actions/workflows/codeql.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![GitHub Release](https://img.shields.io/github/v/release/IoTSharp/SonnetDB?label=Release)](https://github.com/IoTSharp/SonnetDB/releases)

## 🚀 What Is SonnetDB

SonnetDB is a time-series database for IoT, industrial telemetry, observability, and real-time analytics. It offers:

- 🧩 Embedded engine mode for low-latency in-process workloads
- 🌐 HTTP server mode with Admin UI, Help center, auth and RBAC
- 🔌 Multi-language connectors (C, Go, Rust, Java, Python, VB6, PureBasic)
- 🛠️ ADO.NET provider and CLI tooling

> SonnetDB persists data as a directory layout (catalog/schema/WAL/segments/tombstones), not as a single-file database.

## 🏷️ Ecosystem Downloads and Versions

### 📦 NuGet

[![SonnetDB.Core Version](https://img.shields.io/nuget/v/SonnetDB.Core?label=SonnetDB.Core)](https://www.nuget.org/packages/SonnetDB.Core)
[![SonnetDB.Core Downloads](https://img.shields.io/nuget/dt/SonnetDB.Core?label=Downloads)](https://www.nuget.org/packages/SonnetDB.Core)
[![SonnetDB.Data Version](https://img.shields.io/nuget/v/SonnetDB.Data?label=SonnetDB.Data)](https://www.nuget.org/packages/SonnetDB.Data)
[![SonnetDB.Data Downloads](https://img.shields.io/nuget/dt/SonnetDB.Data?label=Downloads)](https://www.nuget.org/packages/SonnetDB.Data)
[![SonnetDB.Cli Version](https://img.shields.io/nuget/v/SonnetDB.Cli?label=SonnetDB.Cli)](https://www.nuget.org/packages/SonnetDB.Cli)
[![SonnetDB.Cli Downloads](https://img.shields.io/nuget/dt/SonnetDB.Cli?label=Downloads)](https://www.nuget.org/packages/SonnetDB.Cli)

### 🐳 Docker

[![Docker Image](https://img.shields.io/docker/v/iotsharp/sonnetdb?label=iotsharp/sonnetdb&sort=semver)](https://hub.docker.com/r/iotsharp/sonnetdb)
[![Docker Pulls](https://img.shields.io/docker/pulls/iotsharp/sonnetdb?label=Docker%20Pulls)](https://hub.docker.com/r/iotsharp/sonnetdb)
[![Docker Download](https://img.shields.io/badge/Download-docker%20hub-0db7ed)](https://hub.docker.com/r/iotsharp/sonnetdb)
[![GHCR Package](https://img.shields.io/badge/GHCR-ghcr.io%2Fiotsharp%2Fsonnetdb-2ea44f)](https://github.com/IoTSharp/SonnetDB/pkgs/container/sonnetdb)

### 🔌 Connectors and Download Entries

[![C Connector](https://img.shields.io/badge/C-Connector-blue)](connectors/c/README.md)
[![Go Connector](https://img.shields.io/badge/Go-Connector-00ADD8)](connectors/go/README.md)
[![Rust Connector](https://img.shields.io/badge/Rust-Connector-DEA584)](connectors/rust/README.md)
[![Java Connector](https://img.shields.io/badge/Java-Connector-f89820)](connectors/java/README.md)
[![Python Connector](https://img.shields.io/badge/Python-Connector-3776AB)](connectors/python/README.md)
[![VB6 Connector](https://img.shields.io/badge/VB6-Connector-5C2D91)](connectors/vb6/README.md)
[![PureBasic Connector](https://img.shields.io/badge/PureBasic-Connector-5A5A5A)](connectors/purebasic/README.md)
[![Connector Releases](https://img.shields.io/badge/Downloads-GitHub%20Releases-black)](https://github.com/IoTSharp/SonnetDB/releases)

## ✨ Core Capabilities

- ⚡ High-throughput ingestion via SQL, Line Protocol, JSON, and bulk fast paths
- 🧠 Rich SQL features: aggregates, window functions, forecast and control functions (PID)
- 🗺️ GeoSpatial stack: `GEOPOINT`, trajectory analytics, geo filters, GeoJSON output
- 🔐 Control-plane SQL for users, databases, grants, and tokens
- 🧪 Continuous benchmarks against InfluxDB, TDengine, IoTDB, TimescaleDB, SQLite, and LiteDB
- 🛠️ Web Admin Workbench: Schema Explorer database tree, SQL Editor, staged preview, Result Grid, Trajectory mode switching, with Copilot kept as the original global floating dock

## � Why SonnetDB

- **Embedded + server, one codebase**: Run as an in-process zero-dependency engine, or deploy as a standalone HTTP service — from edge devices to cloud, the same database fits both.
- **Standard SQL for time-series**: No proprietary query language to learn. Use familiar SQL for writes, queries, aggregation, forecast functions, GeoSpatial analysis, and control-plane operations.
- **~2× faster than alternatives**: Our server-vs-server benchmark shows ~**1.98× higher query throughput** than comparable time-series databases on the same hardware.
- **First-class multi-language connectors**: Official connectors for C, Go, Rust, Java, Python, VB6, and PureBasic, plus a full ADO.NET provider for .NET ecosystems.
- **Built-in control plane**: User management, RBAC, token lifecycle, and Admin UI included — no extra ops tooling required.
- **MIT licensed, fully open source**: Free to use, modify, and redistribute. Enterprise features available via [sonnetdb.com](https://sonnetdb.com).

## 🌐 Website & Resources

| | Link |
| --- | --- |
| Homepage | https://sonnetdb.com |
| Documentation | https://sonnetdb.com/docs |
| Open-source repo | https://github.com/IoTSharp/SonnetDB |
| Enterprise & cloud platform | https://sonnetdb.com/platform |

## What Is Included

| Component | Purpose |
| --- | --- |
| `src/SonnetDB` | Embedded engine: schema, writes, queries, deletes, WAL, MemTable, Segment, compaction, retention |
| `src/SonnetDB.Data` | ADO.NET provider for both embedded and remote modes |
| `src/SonnetDB.Cli` | `sndb` CLI: local/remote connections, profile management (`local`/`remote`/`connect`), and interactive REPL |
| `src/SonnetDB` | HTTP server, first-run setup, auth/RBAC, SSE, admin UI, `/help` docs |
| `web` | Admin frontend (includes SonnetDB Workbench, global CopilotDock, and published SPA assets) |
| `docs` | JekyllNet documentation site source, bundled into the Docker image |

## Current Capabilities

- Embedded usage through `Tsdb.Open(...)`
- Explicit measurement schema with `CREATE MEASUREMENT`
- SQL writes with `INSERT`, reads with `SELECT`, deletes with `DELETE`
- Aggregates with `count`, `sum`, `min`, `max`, `avg`, `first`, `last`
- Time-bucket aggregation with `GROUP BY time(...)`
- ADO.NET access for local and remote deployments
- CLI access for scripting and ad hoc SQL
- Bulk ingest fast paths through `CommandType.TableDirect` and HTTP bulk endpoints
- Server control-plane SQL for users, databases, grants, and tokens

Web Admin Workbench details and the manual acceptance checklist are in [docs/web-workbench.md](docs/web-workbench.md).

## Quick Start

### Embedded

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
```

### Server

```bash
docker build -f src/SonnetDB/Dockerfile -t sonnetdb .
docker run --rm -p 5080:5080 -v ./sonnetdb-data:/data sonnetdb
```

Then open:

- `http://127.0.0.1:5080/admin/`
- `http://127.0.0.1:5080/help/`

If `/data/.system` is empty, `/admin/` will guide you through the first-run setup flow for:

- server ID
- organization
- admin username
- admin password
- initial static Bearer token

### ADO.NET

```csharp
using SonnetDB.Data;

using var connection = new SndbConnection("Data Source=./demo-data");
connection.Open();
```

Remote mode:

```csharp
using SonnetDB.Data;

using var connection = new SndbConnection(
    "Data Source=sonnetdb+http://127.0.0.1:5080/metrics;Token=your-token");
connection.Open();
```

## Data Model

| Concept | Meaning |
| --- | --- |
| `measurement` | A time-series entity such as `cpu`, `memory`, or `meter_reading` |
| `tag` | A series identity/filter dimension such as `host`, `region`, or `device_id` |
| `field` | An observed value such as `usage`, `temperature`, or `status` |
| `time` | Reserved timestamp column used in writes, queries, and deletes |
| `series` | Canonical `measurement + sorted(tags)` identity |

Field types currently supported:

- `FLOAT`
- `INT`
- `BOOL`
- `STRING`

## SQL Surface

Data-plane SQL currently supported:

- `CREATE MEASUREMENT`
- `INSERT INTO ... VALUES (...)`
- `SELECT ... FROM ... [WHERE ...] [GROUP BY time(...)]`
- `DELETE FROM ... WHERE ...`
- `SHOW MEASUREMENTS` / `SHOW TABLES` / `DESCRIBE [MEASUREMENT] <name>`
- `EXPLAIN <read-only statement>` (currently supports `SELECT`, `SHOW MEASUREMENTS` / `SHOW TABLES`, and `DESCRIBE [MEASUREMENT]`)

### Built-in SQL functions

`SELECT` supports the following built-in functions (PR #50–#56). Custom aggregates / scalars / TVFs may be registered via [`Tsdb.Functions`](docs/extending-functions.md) in embedded mode (Server mode disables UDF by default).

| Function | Kind | Introduced | Counterpart | Notes |
| --- | --- | --- | --- | --- |
| `count`, `sum`, `min`, `max`, `avg`, `first`, `last` | Aggregate (Tier 1) | PR #50 | InfluxDB / Timescale / TDengine | Basic aggregates with `GROUP BY time(...)` |
| `stddev`, `variance`, `spread`, `mode`, `median` | Aggregate (Tier 2) | PR #52 | InfluxDB `stddev` / TDengine `STDDEV` | Population stats |
| `percentile`, `p50/p90/p95/p99`, `tdigest_agg` | Aggregate (T-Digest) | PR #52 | InfluxDB `quantile(estimate_tdigest)`, TDengine `APERCENTILE` | Constant-space quantile estimation |
| `distinct_count` | Aggregate (HyperLogLog) | PR #52 | TDengine `HYPERLOGLOG` | Cardinality estimation |
| `histogram(x, n)` | Aggregate | PR #52 | Prometheus `histogram_quantile` source | Equal-width bucketing |
| `pid`, `pid_estimate` | Aggregate (Control) | PR #54 | — | Incremental PID controller output |
| `abs`, `round`, `sqrt`, `log`, `coalesce`, `time_bucket`, `date_trunc`, `extract`, `cast` | Scalar | PR #51 | TDengine scalars / Postgres `date_trunc` | Row-level expressions |
| `difference`, `delta`, `increase` | Window | PR #53 | InfluxDB `difference` / TDengine `DIFF` | Adjacent diffs |
| `derivative`, `non_negative_derivative`, `rate`, `irate` | Window | PR #53 | InfluxDB `derivative`, Prometheus `rate`, TDengine `DERIVATIVE` | Time-normalised rate |
| `cumulative_sum`, `integral` | Window | PR #53 | InfluxDB `cumulativeSum` / Timescale `time_weight` | Cumulative / time-weighted integral |
| `moving_average`, `ewma`, `holt_winters` | Window (smoothing) | PR #53 | InfluxDB `movingAverage` / `holtWinters`, TDengine `MAVG` | EMA / Holt double-exponential |
| `fill`, `locf`, `interpolate` | Window (gap fill) | PR #53 | InfluxDB `fill()`, TDengine `INTERP` | Missing-value fill |
| `state_changes`, `state_duration` | Window (state) | PR #53 | TDengine `STATECOUNT` / `STATEDURATION` | State machine transitions |
| `pid_series` | Window (Control) | PR #54 | — | Streaming PID time series |
| `anomaly(x, 'zscore'\|'iqr', k)`, `changepoint(x, 'cusum', k, drift)` | Window | PR #55 | TDengine `STATEDURATION` companion | Flag (0/1) / cumulative changepoint |
| `forecast(measurement, field, n, model, [season])` | TVF | PR #55 | TDengine `FORECAST` (3.3.6+), Influx `holtWinters` | Table-valued forecast horizon |
| user-defined | UDF | PR #56 | — | Embedded mode only; see `docs/extending-functions.md` |

> Function-family benchmarks live in `tests/SonnetDB.Benchmarks/Benchmarks/FunctionBenchmark.cs` and compare against InfluxDB Flux and TDengine REST equivalents.

Server control-plane SQL currently supported:

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

## Architecture At A Glance

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
```

Actual on-disk database layout:

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

Server control-plane layout:

```text
<data-root>/.system/
├─ installation.json
├─ users.json
└─ grants.json
```

## Benchmarks

> Numbers below are from **PR #49** local same-host comparison runs (i9-13900HX / Windows 11 / .NET 10.0.6 / Docker Desktop + WSL2; full suite of 24 benchmarks ~20 min). InfluxDB 2.7, TDengine 3.3.4.3 and `sonnetdb` all run in local docker containers; results illustrate relative magnitudes only and do not represent production deployment performance. Full numbers live in [tests/SonnetDB.Benchmarks/README.md](tests/SonnetDB.Benchmarks/README.md).

### Insert: 1,000,000 points (single series, IterationCount=3)

| Write path                                              | Mean       | Allocated  | vs SonnetDB embedded |
|-------------------------------------------------------- |-----------:|-----------:|-------------------:|
| **SonnetDB embedded** `Tsdb.WriteMany`                    |   544.9 ms |   529.7 MB |             1.00× |
| SonnetDB Server `POST /v1/.../bulk`                       |    1.120 s |    34.2 MB |             2.06× |
| SonnetDB Server `POST /v1/.../lp`                         |    1.293 s |    52.4 MB |             2.37× |
| SonnetDB Server `POST /v1/.../json`                       |    1.352 s |    71.4 MB |             2.48× |
| SonnetDB Server `POST /v1/.../sql/batch`                  |   19.797 s |   655.5 MB |            36.3× |
| SQLite (file + WAL, single-tx batch INSERT)             |   811.4 ms |   465.4 MB |             1.49× |
| InfluxDB 2.7 (`WriteApiAsync`, 10k/batch LP)            |    5.222 s |  1457.4 MB |             9.58× |
| TDengine 3.3.4.3 REST INSERT (1k/batch, explicit STable)|   44.137 s |   156.1 MB |            81.0× |
| TDengine 3.3.4.3 schemaless LP (`/influxdb/v1/write`)   |   996.0 ms |    61.2 MB |             1.83× |

> SonnetDB embedded ≈ **1.83 M pts/s**. The three server fast-path endpoints (LP/JSON/Bulk) all sit in the "1M points/s + ≤ 80 MB allocated" band, exceeding the original Milestone 11 goal of ≥ 700k pts/s. `/sql/batch` goes through the SQL parser and is the most expensive path by design.

### Range query: ~100k rows (last 10% time window)

| Engine               | Mean       | Allocated |
|--------------------- |-----------:|----------:|
| **SonnetDB embedded**  |   6.71 ms  |   18.7 MB |
| SonnetDB Server (HTTP) |  88.40 ms  |   16.1 MB |
| SQLite               |  44.54 ms  |    9.8 MB |
| InfluxDB 2.7         | 411.13 ms  |  280.5 MB |
| TDengine 3.3.4.3     |  56.29 ms  |   14.0 MB |

### 1-minute bucket aggregate (AVG / MIN / MAX / COUNT, 16,667 buckets)

| Engine               | Mean       | Allocated |
|--------------------- |-----------:|----------:|
| **SonnetDB embedded**  |  42.26 ms  |   39.4 MB |
| SonnetDB Server (HTTP) |  88.82 ms  |    2.5 MB |
| SQLite               | 327.29 ms  |    2.5 MB |
| InfluxDB 2.7         |  81.48 ms  |   47.2 MB |
| TDengine 3.3.4.3     |  59.63 ms  |    3.1 MB |

### Highlights

- SonnetDB write is **9.6×** faster than InfluxDB, **81×** faster than TDengine REST INSERT, and **1.83×** faster than TDengine schemaless LP.
- SonnetDB range query is **61×** faster than InfluxDB and **6.6×** faster than SQLite.
- The server LP / JSON / Bulk endpoints are **15–17×** faster than `/sql/batch` with **5–11%** of its allocations, and only ~2.0–2.5× more overhead than embedded (HTTP / Kestrel / auth / payload parsing).
- TDengine same-database schemaless LP is **44×** faster and uses **39%** of the allocations of REST INSERT into a sub-table, showing the gap between the schemaless fast path and the SQL parser path.

## Design Principles

- Safe-only core: no `unsafe` in the current core implementation stage
- Schema-first measurements
- Embedded-first engine with optional server deployment
- Directory-based persistence instead of single-file storage
- Shared API surface across embedded, ADO.NET, CLI, and remote usage
- Docs built into the Docker image through JekyllNet

## Docs

Detailed usage examples live under `docs/`:

- [Getting Started](docs/getting-started.md)
- [Data Model](docs/data-model.md)
- [SQL Reference](docs/sql-reference.md)
- [Embedded and In-Proc API](docs/embedded-api.md)
- [ADO.NET Reference](docs/ado-net.md)
- [CLI Reference](docs/cli-reference.md)
- [Bulk Ingest](docs/bulk-ingest.md)
- [Architecture Overview](docs/architecture.md)
- [File Layout](docs/file-format.md)
- [Release Docs](docs/releases/README.md)

## Related Files

- [ROADMAP.md](ROADMAP.md)
- [CHANGELOG.md](CHANGELOG.md)
- [AGENTS.md](AGENTS.md)

## License

[MIT](LICENSE)
