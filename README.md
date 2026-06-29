# SonnetDB

[中文](README.md) | [English](README.en.md)

[![CI](https://github.com/IoTSharp/SonnetDB/actions/workflows/ci.yml/badge.svg)](https://github.com/IoTSharp/SonnetDB/actions/workflows/ci.yml)
[![CodeQL](https://github.com/IoTSharp/SonnetDB/actions/workflows/codeql.yml/badge.svg)](https://github.com/IoTSharp/SonnetDB/actions/workflows/codeql.yml)
[![Parity](https://github.com/IoTSharp/SonnetDB/actions/workflows/parity.yml/badge.svg)](https://github.com/IoTSharp/SonnetDB/actions/workflows/parity.yml)
[![Parity vs OSS](https://img.shields.io/endpoint?url=https://raw.githubusercontent.com/IoTSharp/SonnetDB/parity-results/latest.json)](https://github.com/IoTSharp/SonnetDB/tree/parity-results)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![GitHub Release](https://img.shields.io/github/v/release/IoTSharp/SonnetDB?label=Release)](https://github.com/IoTSharp/SonnetDB/releases)

## 🚀 SonnetDB 是什么

**SonnetDB 是面向 .NET 工业边缘应用的本地优先数据引擎。**

SonnetDB 提供一个面向现场软件的本地数据底座：数据库以目录为持久化边界，既能嵌入 .NET 进程，也能作为服务端部署。它围绕工业采集、配置、缓存、文档、检索、对象、消息和 AI Copilot 等常见场景提供统一的 SQL / API / Web Admin 入口，让应用先接入 SonnetDB，再按需要启用相应能力。

SonnetDB 的核心场景不是云端大集群，而是更贴近现场的：

- 设备网关、采集程序、离线数据记录器和 Windows 工控机。
- 轻量 MES / SCADA / 运维系统的本地数据底座。
- 私有化、弱联网或边缘节点上的 .NET 工业软件。
- 能被 Copilot / MCP / Agent 调用的工业数据分析与诊断场景。

用一个本地引擎、一套 SQL / API 和一个 Web Admin，SonnetDB 同时提供：

| 能力 | 用来做什么 |
| --- | --- |
| 时序数据库 | 设备指标、工业采集、日志指标、时间窗口聚合、压缩、Retention |
| 关系型数据库 | 业务表、维表、配置表、主键、索引、JOIN、事务、EF Core |
| KV / 缓存 | 设备状态、会话、配置、TTL、前缀扫描、缓存 Provider |
| JSON 文档 | 文档集合、JSON path、文档查询、文档索引 |
| 全文 + 向量搜索 | BM25、向量 KNN、Hybrid Search、知识检索 |
| 对象存储 | S3-compatible bucket、分片上传、Range 读取、Presigned URL |
| 消息队列 | SonnetMQ topic、consumer group、pull / ack、重启 replay |
| AI Copilot | SQL 生成、解释、修复、排障、知识引用、写入审批 |

| 使用方式 | 入口 |
| --- | --- |
| 嵌入式 | `Tsdb.Open(...)` 直接在进程内打开数据库目录 |
| 服务端 | Docker / HTTP API / `/admin/` / `/help/` |
| .NET 生态 | ADO.NET、EF Core、`IDistributedCache` Provider |
| 命令行 | `sndb` 本地 / 远程执行 SQL、备份和维护 |
| 多语言 | C、Go、Rust、Java、Python、VB6、PureBasic 连接器 |
| AI / Agent | Web CopilotDock、MCP 工具入口 |

## 🏷️ 生态下载与版本

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

### 🔌 连接器与下载入口

[![C Connector](https://img.shields.io/badge/C-Connector-blue)](connectors/c/README.md)
[![Go Connector](https://img.shields.io/badge/Go-Connector-00ADD8)](connectors/go/README.md)
[![Rust Connector](https://img.shields.io/badge/Rust-Connector-DEA584)](connectors/rust/README.md)
[![Java Connector](https://img.shields.io/badge/Java-Connector-f89820)](connectors/java/README.md)
[![Python Connector](https://img.shields.io/badge/Python-Connector-3776AB)](connectors/python/README.md)
[![VB6 Connector](https://img.shields.io/badge/VB6-Connector-5C2D91)](connectors/vb6/README.md)
[![PureBasic Connector](https://img.shields.io/badge/PureBasic-Connector-5A5A5A)](connectors/purebasic/README.md)
[![Connector Releases](https://img.shields.io/badge/Downloads-GitHub%20Releases-black)](https://github.com/IoTSharp/SonnetDB/releases)

## ✨ 核心特点

- **工业边缘优先**：面向设备指标、工厂局域网、边缘网关、离线采集、现场诊断和私有化部署。
- **一个进程，多种数据能力**：时序、关系、KV、文档、搜索、对象、消息队列和 Copilot 在同一个产品里协同。
- **嵌入式 + 服务端双形态**：小项目可直接 `Tsdb.Open(...)` 嵌入进程，大一点的项目可用 Docker / HTTP Server / Web Admin 部署。
- **Agent-ready**：提供 CopilotDock、MCP 工具入口、schema 查询、SQL 生成、只读分析和写入审批，适合构建 Industrial Data Agent。
- **.NET 原生集成**：提供 NuGet、ADO.NET、EF Core、`IDistributedCache` Provider、CLI 和多语言连接器。

## 🌐 官网与资源

| 入口 | 地址 |
| --- | --- |
| 官方主页 | https://sonnetdb.com |
| 在线文档 | https://sonnetdb.com/docs |
| 开源仓库 | https://github.com/IoTSharp/SonnetDB |
| 企业版与云平台 | https://sonnetdb.com/platform |

## 💬 交流社群

欢迎扫码加入企业微信群，交流 SonnetDB 使用、开发和落地问题。

<img src="web/public/qr-group.png" alt="扫描二维码加入 SonnetDB 企业微信群" width="240">

## 当前组成

| 组件 | 说明 |
| --- | --- |
| `src/SonnetDB.Core` | 多模型核心库，包含时序、关系表、KV、文档、搜索、对象存储适配、本地消息队列、备份恢复和底层持久化能力 |
| `src/SonnetDB` | HTTP 服务端、首次安装流程、认证授权、SSE、MCP、Admin UI、Copilot 桥接和内置 `/help` 文档站点 |
| `src/SonnetDB.Data` | ADO.NET 提供程序，NuGet 包名为 `SonnetDB`，命名空间为 `SonnetDB.Data`；承接 `Microsoft.Extensions.VectorData` 的 SonnetDB adapter |
| `src/SonnetDB.EntityFrameworkCore` | EF Core Provider，NuGet 包名为 `SonnetDB.EntityFrameworkCore`，提供 `UseSonnetDB(...)`、类型映射、查询翻译和 migrations SQL |
| `src/SonnetDB.Cli` | 命令行工具 `sndb`：本地/远程连接、profile 管理（`local`/`remote`/`connect`）、交互式 REPL |
| `extensions/SonnetDB.Caching` | 基于 SonnetDB KV keyspace 的缓存 Provider，可用于 IoTSharp / EasyCaching / IDistributedCache 场景 |
| `web` | 管理后台前端（包含 SonnetDB Studio、全局 CopilotDock 与 SPA 发布静态资源） |
| `src/SonnetDB.Studio` | 基于 NativeWebHost 的 SonnetDB Studio 桌面壳 |
| `docs` | JekyllNet 文档站点源码；构建镜像时会生成并打包到 `/help` |

DotSearch / DotVector 合并路线已完成。BM25、分词、距离计算、HNSW / IVF / Vamana、量化和索引序列化能力已收编为 `SonnetDB.Core` 内部引擎；VectorData 适配已迁移到 `SonnetDB.Data`。归档和边界见 [搜索与向量引擎合并路线图](docs/search-vector-engine-consolidation-roadmap.md)。

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

### 4. 通过 Document Client 访问

```csharp
using System.Text.Json;
using SonnetDB.Data.Documents;

using var documents = new SndbDocumentClient("Data Source=./demo-data");
await documents.CreateCollectionAsync("device_docs");
await documents.InsertOneAsync("device_docs", "dev-1", """{"site":"north","kind":"pump","score":9}""");

var hits = await documents.FindAsync("device_docs", new SndbDocumentFindOptions(
    Filter: new SndbDocumentFilter("$.site", "eq", JsonDocument.Parse("\"north\"").RootElement.Clone()),
    Projection: [new SndbDocumentProjection("_id", "_id"), new SndbDocumentProjection("score", "$.score")],
    Sort: [new SndbDocumentSort("$.score", Descending: true)],
    Limit: 20));

Console.WriteLine(hits[0].Json);

var page = await documents.FindPageAsync("device_docs", new SndbDocumentFindOptions(Limit: 100));
while (page.HasMore)
{
    page = await documents.FindPageAsync("device_docs", new SndbDocumentFindOptions(
        Limit: 100,
        ContinuationToken: page.ContinuationToken));
}
```

远程模式使用同一连接字符串格式：

```csharp
using var documents = new SndbDocumentClient(
    "Data Source=sonnetdb+http://127.0.0.1:5080/metrics;Token=your-token");
```

### 5. 通过 CLI 访问

```bash
# 安装
dotnet tool install --global SonnetDB.Cli

# 本地直接使用
sndb local --path ./demo-data --command "SELECT count(*) FROM cpu"

# 保存 profile，下次免输路径
sndb local --path ./demo-data --save-profile home --default
sndb connect home --command "SELECT count(*) FROM cpu"

# 连接远程服务端
sndb remote --url http://127.0.0.1:5080 --database metrics --token your-token --repl
```

更完整的 CLI、ADO.NET、嵌入式、远程和批量写入示例见 [docs](docs/index.md)。
SonnetDB Studio（Schema Explorer / SQL Editor / Staged Preview / Result Grid / Trajectory 模式）说明见 [docs/web-workbench.md](docs/web-workbench.md)。

## 深入文档

README 只保留项目概览和最短入门路径，完整说明放在专题文档中：

| 主题 | 文档 |
| --- | --- |
| 入门、部署、首次安装 | [开始使用](docs/getting-started.md) |
| 时序建模、measurement / tag / field / time | [数据模型](docs/data-model.md) |
| SQL 语法、函数、控制面 SQL | [SQL 参考](docs/sql-reference.md)、[SQL Cookbook](docs/sql-cookbook.md) |
| Web Admin、SQL 工作台、Copilot | [SonnetDB Studio](docs/web-workbench.md) |
| 工业 AI 应用与 Agent 场景 | [使用 SonnetDB 构建工业 AI 应用](docs/industrial-ai-applications.md) |
| 嵌入式 API、ADO.NET、EF Core、CLI | [嵌入式 API](docs/embedded-api.md)、[ADO.NET](docs/ado-net.md)、[CLI](docs/cli-reference.md) |
| 批量写入、Line Protocol、JSON ingest | [批量写入](docs/bulk-ingest.md) |
| KV、文档、全文、向量、Hybrid Search | [KV Keyspace](docs/kv-keyspace.md)、[向量搜索](docs/vector-search.md) |
| 地理空间、轨迹、预测、PID | [地理空间](docs/geo-spatial.md)、[预测](docs/forecast.md)、[PID 控制](docs/pid-control.md) |
| 架构、目录布局、备份恢复 | [架构总览](docs/architecture.md)、[文件格式](docs/file-format.md)、[备份恢复](docs/backup-restore.md) |
| 发布产物、Docker、安装包 | [发布与打包](docs/releases/README.md) |
| 性能与可靠性 | [基准说明](tests/SonnetDB.Benchmarks/README.md)、[近期性能与可靠性变更](docs/performance-reliability-updates.md) |

## 基准与可靠性

同机基准和复现实验请以 [tests/SonnetDB.Benchmarks/README.md](tests/SonnetDB.Benchmarks/README.md) 为准。README 只保留结论入口：

- Server-vs-Server 写入对比中，SonnetDB Server 在同机测试下约为 IoTDB 的 **1.98x**。
- 批量写入、范围查询、时间窗口聚合、向量召回和地理空间查询都有独立 benchmark。
- WAL、Segment、Compaction、Retention、备份恢复和索引生命周期的近期变更见 [性能与可靠性近期变更](docs/performance-reliability-updates.md)。

## Parity vs Open-Source Stack

SonnetDB 的多模型能力通过独立 parity 套件持续和开源组件对齐：PostgreSQL、InfluxDB、VictoriaMetrics、Redis、Qdrant、MinIO、NATS JetStream、Meilisearch 和 ClickHouse。`.github/workflows/parity.yml` 每日 02:00 UTC 运行 `light` / `full` 两档矩阵，能力、可靠性和算法准确度作为红绿门槛，性能数字只进入 warning/report。

最新 nightly 结果发布到 [`parity-results`](https://github.com/IoTSharp/SonnetDB/tree/parity-results) 孤立分支；可读样例见 [tests/SonnetDB.Parity/reports/sample-run.md](tests/SonnetDB.Parity/reports/sample-run.md)，路线说明见 [docs/parity-roadmap.md](docs/parity-roadmap.md)。

## 设计原则

- 核心库 safe-only，不使用 `unsafe`。
- 一个数据库目录承载持久化数据，不再以单文件数据库作为产品描述。
- 产品门面优先表达为面向 .NET 工业边缘应用的本地数据引擎；多模型是能力组合，不是第一层定位。
- 嵌入式和服务端共享 SQL / API 语义。
- 管理能力内置到 Server、Web Admin、CLI 和 Copilot 中。
- AI 能力通过 Copilot、MCP 和 provider 抽象服务于工业数据查询、诊断和运维，不把 SonnetDB 绑定到单一模型供应商。

## 相关文件

- 路线图见 [ROADMAP.md](ROADMAP.md)
- 变更记录见 [CHANGELOG.md](CHANGELOG.md)
- AI 协作规范见 [AGENTS.md](AGENTS.md)
- AI / Agent 索引见 [llms.txt](llms.txt)

## License

[MIT](LICENSE)
