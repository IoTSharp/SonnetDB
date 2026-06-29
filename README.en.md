# SonnetDB

[中文](README.md) | [English](README.en.md)

[![CI](https://github.com/IoTSharp/SonnetDB/actions/workflows/ci.yml/badge.svg)](https://github.com/IoTSharp/SonnetDB/actions/workflows/ci.yml)
[![CodeQL](https://github.com/IoTSharp/SonnetDB/actions/workflows/codeql.yml/badge.svg)](https://github.com/IoTSharp/SonnetDB/actions/workflows/codeql.yml)
[![Parity](https://github.com/IoTSharp/SonnetDB/actions/workflows/parity.yml/badge.svg)](https://github.com/IoTSharp/SonnetDB/actions/workflows/parity.yml)
[![Parity vs OSS](https://img.shields.io/endpoint?url=https://raw.githubusercontent.com/IoTSharp/SonnetDB/parity-results/latest.json)](https://github.com/IoTSharp/SonnetDB/tree/parity-results)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![GitHub Release](https://img.shields.io/github/v/release/IoTSharp/SonnetDB?label=Release)](https://github.com/IoTSharp/SonnetDB/releases)

## 🚀 What Is SonnetDB

**SonnetDB is a local-first data engine for .NET industrial edge applications.**

SonnetDB provides a local data foundation for industrial software: a database is persisted as a directory, can run inside a .NET process, and can also be deployed as a server. It exposes SQL, APIs, and Web Admin entry points for time-series ingestion, relational tables, KV / cache, JSON documents, search, objects, lightweight messaging, and AI Copilot workflows, so teams can start with SonnetDB and enable the capabilities they need.

SonnetDB is designed for practical edge workloads:

- device gateways, data collectors, offline loggers, and Windows industrial PCs.
- local data foundations for lightweight MES / SCADA / maintenance systems.
- private, weakly connected, or edge-side .NET industrial software.
- industrial data analysis and diagnostics workflows called by Copilot / MCP / agents.

One local engine, one SQL / API surface, and one Web Admin provide:

| Capability | What it is for |
| --- | --- |
| Time-series database | Device metrics, industrial telemetry, log metrics, time-window aggregation, compression, retention |
| Relational database | Business tables, dimensions, configuration, primary keys, indexes, joins, transactions, EF Core |
| KV / cache | Device state, sessions, configuration, TTL, prefix scan, cache provider |
| JSON documents | Document collections, JSON path, document queries, document indexes |
| Full-text + vector search | BM25, vector KNN, Hybrid Search, knowledge retrieval |
| Object storage | S3-compatible buckets, multipart upload, range reads, presigned URLs |
| Message queue | SonnetMQ topics, consumer groups, pull / ack, restart replay |
| AI Copilot | SQL generation, explanation, repair, troubleshooting, citations, write approval |

| Usage mode | Entry point |
| --- | --- |
| Embedded | `Tsdb.Open(...)` opens a database directory in process |
| Server | Docker / HTTP API / `/admin/` / `/help/` |
| .NET ecosystem | ADO.NET, EF Core, `IDistributedCache` provider |
| CLI | `sndb` for local / remote SQL, backup, and maintenance |
| Multi-language | C, Go, Rust, Java, Python, VB6, and PureBasic connectors |
| AI / Agent | Web CopilotDock and MCP tool entry points |

## 🏷️ Ecosystem Downloads and Versions

### 📦 NuGet

[![SonnetDB.Core Version](https://img.shields.io/nuget/v/SonnetDB.Core?label=SonnetDB.Core)](https://www.nuget.org/packages/SonnetDB.Core)
[![SonnetDB.Core Downloads](https://img.shields.io/nuget/dt/SonnetDB.Core?label=Downloads)](https://www.nuget.org/packages/SonnetDB.Core)
[![SonnetDB Version](https://img.shields.io/nuget/v/SonnetDB?label=SonnetDB)](https://www.nuget.org/packages/SonnetDB)
[![SonnetDB Downloads](https://img.shields.io/nuget/dt/SonnetDB?label=Downloads)](https://www.nuget.org/packages/SonnetDB)
[![SonnetDB.EntityFrameworkCore Version](https://img.shields.io/nuget/v/SonnetDB.EntityFrameworkCore?label=SonnetDB.EntityFrameworkCore)](https://www.nuget.org/packages/SonnetDB.EntityFrameworkCore)
[![SonnetDB.EntityFrameworkCore Downloads](https://img.shields.io/nuget/dt/SonnetDB.EntityFrameworkCore?label=Downloads)](https://www.nuget.org/packages/SonnetDB.EntityFrameworkCore)
[![SonnetDB.Caching Version](https://img.shields.io/nuget/v/SonnetDB.Caching?label=SonnetDB.Caching)](https://www.nuget.org/packages/SonnetDB.Caching)
[![SonnetDB.Caching Downloads](https://img.shields.io/nuget/dt/SonnetDB.Caching?label=Downloads)](https://www.nuget.org/packages/SonnetDB.Caching)
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

## ✨ Core Traits

- **Industrial edge first**: built for device metrics, factory LANs, edge gateways, offline collection, local diagnostics, and private deployments.
- **One process, many data capabilities**: time-series, relational, KV, documents, search, objects, queues, and Copilot work together in one product.
- **Embedded + server modes**: small apps can call `Tsdb.Open(...)` in process; larger deployments can run Docker / HTTP Server / Web Admin.
- **Agent-ready**: CopilotDock, MCP tools, schema inspection, SQL drafting, read-only analysis, and write approval make SonnetDB usable from Industrial Data Agents.
- **Native .NET integration**: NuGet, ADO.NET, EF Core, `IDistributedCache`, CLI, and multi-language connectors.

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
| `src/SonnetDB.Core` | Multi-model core library: time-series, relational tables, KV, documents, search, object-storage adapter, local message queue, backup/restore, and persistence |
| `src/SonnetDB` | HTTP server, first-run setup, auth/RBAC, SSE, MCP, Admin UI, Copilot bridge, and bundled `/help` docs |
| `src/SonnetDB.Data` | ADO.NET provider; NuGet package ID is `SonnetDB`, namespace is `SonnetDB.Data` |
| `src/SonnetDB.EntityFrameworkCore` | EF Core Provider; NuGet package ID is `SonnetDB.EntityFrameworkCore`, with `UseSonnetDB(...)`, type mapping, query translation, and migrations SQL |
| `src/SonnetDB.Cli` | `sndb` CLI: local/remote connections, profile management (`local`/`remote`/`connect`), and interactive REPL |
| `extensions/SonnetDB.Caching` | Cache provider backed by SonnetDB KV keyspaces for IoTSharp / EasyCaching / IDistributedCache scenarios |
| `web` | Admin frontend (includes SonnetDB Workbench, global CopilotDock, and published SPA assets) |
| `docs` | JekyllNet documentation site source, bundled into the Docker image |

Web Admin Workbench details are in [docs/web-workbench.md](docs/web-workbench.md).

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
| Geospatial, trajectory, forecast, PID | [Geospatial](docs/geo-spatial.md), [Forecast](docs/forecast.md), [PID Control](docs/pid-control.md) |
| Architecture, file layout, backup/restore | [Architecture](docs/architecture.md), [File Format](docs/file-format.md), [Backup & Restore](docs/backup-restore.md) |
| Release artifacts, Docker, installers | [Release Docs](docs/releases/README.md) |
| Performance and reliability | [Benchmark README](tests/SonnetDB.Benchmarks/README.md), [Recent Performance & Reliability Updates](docs/performance-reliability-updates.md) |

## Benchmarks And Reliability

Use [tests/SonnetDB.Benchmarks/README.md](tests/SonnetDB.Benchmarks/README.md) as the source of truth for same-host benchmarks and reproduction steps. The README keeps only the high-level pointers:

- In the Server-vs-Server write benchmark, SonnetDB Server is about **1.98x** faster than IoTDB on the same machine.
- Bulk ingest, range query, time-window aggregation, vector recall, and geospatial queries have dedicated benchmarks.
- WAL, segments, compaction, retention, backup/restore, and index lifecycle updates are tracked in [Recent Performance & Reliability Updates](docs/performance-reliability-updates.md).

## Parity vs Open-Source Stack

SonnetDB continuously checks its multi-model behavior against open-source peers: PostgreSQL, InfluxDB, VictoriaMetrics, Redis, Qdrant, MinIO, NATS JetStream, Meilisearch, and ClickHouse. `.github/workflows/parity.yml` runs the `light` / `full` matrix every day at 02:00 UTC; capability, reliability, and algorithmic accuracy are merge gates, while performance numbers are warning/report only.

The latest nightly output is published to the [`parity-results`](https://github.com/IoTSharp/SonnetDB/tree/parity-results) orphan branch. A readable example is available at [tests/SonnetDB.Parity/reports/sample-run.md](tests/SonnetDB.Parity/reports/sample-run.md), with the broader plan in [docs/parity-roadmap.md](docs/parity-roadmap.md).

## Design Principles

- Safe-only core: no `unsafe`.
- A database is persisted as a directory, not positioned as a single-file database.
- The product front door is a local-first data engine for .NET industrial edge applications; multi-model storage is the capability set, not the first-line positioning.
- Embedded and server modes share SQL / API semantics.
- Management capabilities are built into Server, Web Admin, CLI, and Copilot.
- AI capabilities should serve industrial data query, diagnostics, and operations through Copilot, MCP, and provider abstractions without binding SonnetDB to one model vendor.

## Related Files

- [ROADMAP.md](ROADMAP.md)
- [CHANGELOG.md](CHANGELOG.md)
- [AGENTS.md](AGENTS.md)
- [llms.txt](llms.txt)

## License

[MIT](LICENSE)
