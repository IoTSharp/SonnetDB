# SonnetDB

[中文](README.md) | [English](README.en.md)

[![CI](https://github.com/IoTSharp/SonnetDB/actions/workflows/ci.yml/badge.svg)](https://github.com/IoTSharp/SonnetDB/actions/workflows/ci.yml)
[![CodeQL](https://github.com/IoTSharp/SonnetDB/actions/workflows/codeql.yml/badge.svg)](https://github.com/IoTSharp/SonnetDB/actions/workflows/codeql.yml)
[![Parity](https://github.com/IoTSharp/SonnetDB/actions/workflows/parity.yml/badge.svg)](https://github.com/IoTSharp/SonnetDB/actions/workflows/parity.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![GitHub Release](https://img.shields.io/github/v/release/IoTSharp/SonnetDB?label=Release)](https://github.com/IoTSharp/SonnetDB/releases)

## What Is SonnetDB

**SonnetDB is a local-first, multi-model embedded data engine written in C# / .NET.**

A SonnetDB database is a directory on disk. You can embed it in your application and use it directly, or deploy it as a database server. It provides time-series, relational tables, KV, JSON documents, full-text search, vector search, object storage, and a message queue inside one engine, all reachable through SQL, REST / binary-frame APIs, and a Web Admin.

The design goal is to **reduce the number of components you deploy at the edge**: instead of running a separate time-series store, cache, message broker, and object store on a device gateway or industrial PC, one process covers these common needs. It fits workloads like:

- device gateways, data collectors, offline loggers, and data-acquisition industrial PCs.
- local data foundations for lightweight MES / SCADA / maintenance systems.
- private, weakly connected, or edge-side .NET software.
- industrial data analysis and diagnostics called by Copilot / MCP / agents.

## Capabilities

One local engine, one SQL / API surface, and one Web Admin provide:

| Capability | What it is for |
| --- | --- |
| Time-series database | Device metrics, industrial telemetry, log metrics, time-window aggregation, compression, retention |
| Relational database | Business tables, dimensions, configuration, primary keys, indexes, joins, transactions, foreign keys, EF Core |
| KV / cache | Device state, sessions, configuration, TTL, prefix scan, cache provider |
| JSON documents | Document collections, JSON path queries, document indexes, document vector index |
| Full-text + vector search | BM25 full-text, vector KNN (HNSW approximate index + exact fallback), Hybrid Search |
| Object storage | S3-compatible buckets, multipart upload, range reads, presigned URLs |
| Message queue | SonnetMQ topics, consumer groups, pull / ack / push, restart replay |
| AI Copilot | SQL generation, explanation, repair, troubleshooting, citations, write approval |

The relational SQL surface is a practical subset covering common queries, aggregation, joins, transactions, and multi-model extensions — it is not a full SQL-standard implementation. Treat the topic docs as the source of truth for each capability's maturity.

## How To Use It

| Usage mode | Entry point |
| --- | --- |
| Embedded | `Tsdb.Open(...)` opens a database directory in process |
| Server | Docker / HTTP API / Web Admin |
| .NET ecosystem | ADO.NET, EF Core, `IDistributedCache` / EasyCaching provider |
| CLI | `sndb` for local / remote SQL, backup, and maintenance |
| Binary frame protocol | High-throughput frame access over HTTP/2 across all seven models (data plane); REST kept for compatibility |
| Device ingest | Built-in MQTT broker (devices publish straight into the DB) + external MQTT client (subscribe to existing EMQX / Mosquitto) |
| Multi-language | C, Go, Rust, Java, Python, VB6, and PureBasic connectors |
| AI / Agent | Web CopilotDock and MCP tool entry points |

## Positioning & Boundaries

To avoid overselling, here is what SonnetDB is **not**, and where its current limits are:

- **Single-node engine**: it runs on a single node today. There is **no** built-in replication, high availability, automatic failover, or sharded clustering. Cross-node redundancy must be handled at the application or ops layer (e.g. cold backup, external orchestration).
- **Edge and small-to-medium scale**: it targets device gateways, industrial PCs, edge nodes, and small-to-medium services — not large cloud-scale OLAP / warehouse clusters.
- **Multi-model is a combination, not "best at everything"**: putting several data models in one process is about reducing component count, not a claim to beat every specialized database on its own turf. For extreme time-series, vector, or relational performance, still evaluate the dedicated products.
- **SQL is a subset**: the dialect covers common queries, aggregation, joins, transactions, and multi-model extensions, but does not guarantee full SQL-standard compatibility.
- **Benchmarks are same-machine and rough**: the numbers in this README and the docs come from same-machine comparisons on a single dev box, used for relative reference and regression — they **do not** predict performance on your production hardware.

Within these boundaries, the goal is to make "one component for the common data needs at the edge" reliable, fast enough, and good enough.

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

foreach (var row in result.Rows)
{
    Console.WriteLine($"{row[0]} {row[1]} {row[2]}");
}
```

### Server

The repo's Docker release workflow builds and pushes prebuilt images `iotsharp/sonnetdb` and `ghcr.io/<owner>/sonnetdb`, which you can pull directly:

```bash
docker run --rm -p 5080:5080 -v ./sonnetdb-data:/data iotsharp/sonnetdb:latest
```

Or build the image from source:

```bash
docker build -f src/SonnetDB/Dockerfile -t sonnetdb .
docker run --rm -p 5080:5080 -v ./sonnetdb-data:/data sonnetdb
```

Then open:

- `http://127.0.0.1:5080/admin/`
- `http://127.0.0.1:5080/help/`

If `/data/.system` is empty, `/admin/` guides you through the first-run setup for server ID, organization, admin username / password, and an initial static Bearer token.

### ADO.NET

```csharp
using SonnetDB.Data;

using var connection = new SndbConnection("Data Source=./demo-data");
connection.Open();

using var command = connection.CreateCommand();
command.CommandText = "SELECT count(*) FROM cpu";

var count = (long)(command.ExecuteScalar() ?? 0L);
Console.WriteLine(count);
```

Remote mode supports both the `Data Source=` URL form and a PostgreSQL-style connection string:

```csharp
using var connection = new SndbConnection(
    "Host=127.0.0.1;Port=5080;Database=metrics;Username=alice;Password=secret");
connection.Open();
```

### CLI

```bash
# Install
dotnet tool install --global SonnetDB.Cli

# Use a local database directly
sndb local --path ./demo-data --command "SELECT count(*) FROM cpu"

# Save a profile so you can skip the path next time
sndb local --path ./demo-data --save-profile home --default
sndb connect home --command "SELECT count(*) FROM cpu"

# Connect to a remote server
sndb remote --url http://127.0.0.1:5080 --database metrics --token your-token --repl
```

More CLI, ADO.NET, document-client, embedded, remote, and bulk-ingest examples are in [docs](docs/index.md).

## Ecosystem Downloads

### NuGet

[![SonnetDB.Core Version](https://img.shields.io/nuget/v/SonnetDB.Core?label=SonnetDB.Core)](https://www.nuget.org/packages/SonnetDB.Core)
[![SonnetDB Version](https://img.shields.io/nuget/v/SonnetDB?label=SonnetDB)](https://www.nuget.org/packages/SonnetDB)
[![SonnetDB.EntityFrameworkCore Version](https://img.shields.io/nuget/v/SonnetDB.EntityFrameworkCore?label=SonnetDB.EntityFrameworkCore)](https://www.nuget.org/packages/SonnetDB.EntityFrameworkCore)
[![SonnetDB.Cli Version](https://img.shields.io/nuget/v/SonnetDB.Cli?label=SonnetDB.Cli)](https://www.nuget.org/packages/SonnetDB.Cli)

### Docker

[![Docker Image](https://img.shields.io/docker/v/iotsharp/sonnetdb?label=iotsharp/sonnetdb&sort=semver)](https://hub.docker.com/r/iotsharp/sonnetdb)
[![Docker Pulls](https://img.shields.io/docker/pulls/iotsharp/sonnetdb?label=Docker%20Pulls)](https://hub.docker.com/r/iotsharp/sonnetdb)
[![GHCR Package](https://img.shields.io/badge/GHCR-ghcr.io%2Fiotsharp%2Fsonnetdb-2ea44f)](https://github.com/IoTSharp/SonnetDB/pkgs/container/sonnetdb)

### Connectors

[![C Connector](https://img.shields.io/badge/C-Connector-blue)](connectors/c/README.md)
[![Go Connector](https://img.shields.io/badge/Go-Connector-00ADD8)](connectors/go/README.md)
[![Rust Connector](https://img.shields.io/badge/Rust-Connector-DEA584)](connectors/rust/README.md)
[![Java Connector](https://img.shields.io/badge/Java-Connector-f89820)](connectors/java/README.md)
[![Python Connector](https://img.shields.io/badge/Python-Connector-3776AB)](connectors/python/README.md)
[![Connector Releases](https://img.shields.io/badge/Downloads-GitHub%20Releases-black)](https://github.com/IoTSharp/SonnetDB/releases)

## What Is Included

| Component | Purpose |
| --- | --- |
| `src/SonnetDB.Core` | Multi-model core library: time-series, relational tables, KV, documents, search, object-storage adapter, local message queue, backup/restore, and persistence. No third-party runtime dependencies |
| `src/SonnetDB` | HTTP server, first-run setup, auth/RBAC, SSE, MCP, Admin UI, Copilot bridge, binary-frame endpoints, MQTT broker/client, and bundled `/help` docs |
| `src/SonnetDB.Data` | ADO.NET provider; NuGet package ID `SonnetDB`, namespace `SonnetDB.Data` |
| `src/SonnetDB.EntityFrameworkCore` | EF Core provider with `UseSonnetDB(...)`, type mapping, query translation, and migrations SQL |
| `src/SonnetDB.Cli` | `sndb` CLI: local/remote connections, profile management, interactive REPL |
| `extensions/SonnetDB.Caching.EasyCaching` | EasyCaching provider backed by SonnetDB KV keyspaces |
| `extensions/SonnetDB.Caching.Distributed` | `IDistributedCache` provider backed by SonnetDB KV keyspaces |
| `web` | Admin frontend (SonnetDB Studio, global CopilotDock, published SPA assets) |
| `src/SonnetDB.Studio` | NativeWebHost-based SonnetDB Studio desktop shell |
| `docs` | JekyllNet documentation site source, bundled into the Docker image |

## Deep-Dive Docs

README keeps the product overview and shortest setup path. Detailed material lives in topic docs:

| Topic | Docs |
| --- | --- |
| Getting started, deployment, first-run setup | [Getting Started](docs/getting-started.md) |
| Time-series modeling and measurement / tag / field / time | [Data Model](docs/data-model.md) |
| SQL grammar, functions, control-plane SQL | [SQL Reference](docs/sql-reference.md), [SQL Cookbook](docs/sql-cookbook.md) |
| Web Admin, SQL Workbench, Copilot | [SonnetDB Workbench](docs/web-workbench.md) |
| Industrial AI applications and agent workflows | [Building Industrial AI Applications with SonnetDB](docs/industrial-ai-applications.md) |
| Embedded API, ADO.NET, EF Core, CLI | [Embedded API](docs/embedded-api.md), [ADO.NET](docs/ado-net.md), [CLI](docs/cli-reference.md) |
| Bulk ingest, Line Protocol, JSON ingest | [Bulk Ingest](docs/bulk-ingest.md) |
| KV, documents, full-text, vector, Hybrid Search | [KV Keyspace](docs/kv-keyspace.md), [Vector Search](docs/vector-search.md) |
| Binary frame protocol, MQTT ingest | [Frame Protocol](docs/frame-protocol.md) |
| Geospatial, trajectory, forecast, PID | [Geospatial](docs/geo-spatial.md), [Forecast](docs/forecast.md), [PID Control](docs/pid-control.md) |
| Architecture, file layout, backup/restore | [Architecture](docs/architecture.md), [File Format](docs/file-format.md), [Backup & Restore](docs/backup-restore.md) |
| Release artifacts, Docker, installers | [Release Docs](docs/releases/README.md) |
| Performance and reliability | [Benchmark README](tests/SonnetDB.Benchmarks/README.md), [Recent Performance & Reliability Updates](docs/performance-reliability-updates.md) |

## Benchmarks And Reliability

Use [tests/SonnetDB.Benchmarks/README.md](tests/SonnetDB.Benchmarks/README.md) as the source of truth for benchmarks — it has the full environment, commands, and reproduction steps. **All comparisons run between same-machine Docker containers on a single dev box; they are for relative reference and regression only and do not predict production performance.** The README keeps only the high-level pointers:

- In the same-machine Server-vs-Server write comparison, SonnetDB Server's average throughput is about **1.98x** that of Apache IoTDB Server on the same machine.
- Embedded write, range query, time-window aggregation, vector recall, and geospatial queries have dedicated benchmarks; the vector side currently focuses on SonnetDB's own brute-force vs HNSW comparison.
- WAL durability has three levels (in-process buffer / OS page cache / per-batch fsync); flushed segment data survives any crash, and Delete is unconditionally WAL-synced. See [Architecture](docs/architecture.md) and [Recent Performance & Reliability Updates](docs/performance-reliability-updates.md).

## Parity vs Open-Source Stack

SonnetDB continuously checks its multi-model behavior against open-source peers: PostgreSQL, InfluxDB, VictoriaMetrics, Redis, Qdrant, MinIO, NATS JetStream, Meilisearch, and ClickHouse. `.github/workflows/parity.yml` runs the `light` / `full` matrix daily; capability, reliability, and algorithmic accuracy are merge gates, while performance numbers are warning/report only. A readable example is at [tests/SonnetDB.Parity/reports/sample-run.md](tests/SonnetDB.Parity/reports/sample-run.md), with the plan in [docs/parity-roadmap.md](docs/parity-roadmap.md).

## Design Principles

- Safe-only core: no `unsafe`, and no third-party runtime dependencies.
- A database is persisted as a directory, not positioned as a single-file database.
- The product is a local-first multi-model data engine for .NET edge / industrial software; multi-model storage is the capability set, not a "best at everything" claim.
- Embedded and server modes share SQL / API semantics.
- Management capabilities are built into Server, Web Admin, CLI, and Copilot.
- AI capabilities serve data query, diagnostics, and operations through Copilot, MCP, and provider abstractions without binding SonnetDB to one model vendor.

## Related Files

- [ROADMAP.md](ROADMAP.md)
- [CHANGELOG.md](CHANGELOG.md)
- [AGENTS.md](AGENTS.md)
- [llms.txt](llms.txt)

## License

[MIT](LICENSE)
