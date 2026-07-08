# SonnetDB

[中文](README.md) | [English](README.en.md)

[![CI](https://github.com/IoTSharp/SonnetDB/actions/workflows/ci.yml/badge.svg)](https://github.com/IoTSharp/SonnetDB/actions/workflows/ci.yml)
[![CodeQL](https://github.com/IoTSharp/SonnetDB/actions/workflows/codeql.yml/badge.svg)](https://github.com/IoTSharp/SonnetDB/actions/workflows/codeql.yml)
[![Parity](https://github.com/IoTSharp/SonnetDB/actions/workflows/parity.yml/badge.svg)](https://github.com/IoTSharp/SonnetDB/actions/workflows/parity.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![GitHub Release](https://img.shields.io/github/v/release/IoTSharp/SonnetDB?label=Release)](https://github.com/IoTSharp/SonnetDB/releases)

## SonnetDB 是什么

**SonnetDB 是一个用 C# / .NET 编写的本地优先、多模型嵌入式数据引擎。**

一个 SonnetDB 数据库就是磁盘上的一个目录。你可以把它嵌入到应用中直接使用，也可以部署为数据库服务。它在同一个引擎里提供时序、关系表、KV、JSON 文档、全文检索、向量检索、对象存储和消息队列，并统一通过 SQL、REST/二进制帧 API 和 Web Admin 访问。

SonnetDB 设计目标是**减少现场部署的组件数量**：与其为设备网关或工控机同时装一套时序库、一套缓存、一套消息队列、一套对象存储，不如用一个进程覆盖这些常见需求。它更适合下面这些场景：

- 设备网关、采集程序、离线数据记录器、数据采集工控机。
- 轻量 MES / SCADA / 运维系统的本地数据底座。
- 私有化、弱联网或边缘节点上的 .NET 软件。
- 需要被 Copilot / MCP / Agent 调用的工业数据分析与诊断。


## 能力概览

用一个本地引擎、一套 SQL / API 和一个 Web Admin，SonnetDB 同时提供：

| 能力 | 用来做什么 |
| --- | --- |
| 时序数据库 | 设备指标、工业采集、日志指标、时间窗口聚合、压缩、Retention |
| 关系型数据库 | 业务表、维表、配置表、主键、索引、JOIN、事务、外键、EF Core |
| KV / 缓存 | 设备状态、会话、配置、TTL、前缀扫描、缓存 Provider |
| JSON 文档 | 文档集合、JSON path 查询、文档索引、文档向量索引 |
| 全文 + 向量检索 | BM25 全文、向量 KNN（HNSW 近邻索引 + 精确回退）、Hybrid Search |
| 对象存储 | S3 兼容 bucket、分片上传、Range 读取、Presigned URL |
| 消息队列 | SonnetMQ topic、consumer group、pull / ack / 推送、重启 replay |
| AI Copilot | SQL 生成、解释、修复、排障、知识引用、写入审批 |

关系型 SQL 是一个实用子集，覆盖常见查询、聚合、JOIN、事务和多模型扩展，但不是完整的 SQL 标准实现。各能力的成熟度以对应的专题文档为准。

## 接入方式

| 使用方式 | 入口 |
| --- | --- |
| 嵌入式 | `Tsdb.Open(...)` 直接在进程内打开数据库目录 |
| 服务端 | Docker / HTTP API / Web Admin |
| .NET 生态 | ADO.NET、EF Core、`IDistributedCache` / EasyCaching Provider |
| 命令行 | `sndb` 本地 / 远程执行 SQL、备份和维护 |
| 二进制帧协议 | HTTP/2 上的高吞吐帧接入，覆盖全部七个模型（数据面），REST 全保留作兼容 |
| 设备接入 | 内建 MQTT broker（设备直连落库）+ 外部 MQTT client（订阅现有 EMQX / Mosquitto） |
| 多语言 | C、Go、Rust、Java、Python、VB6、PureBasic 连接器 |
| AI / Agent | Web CopilotDock、MCP 工具入口 |

## 定位与边界

为了避免误解，这里明确说清 SonnetDB **不是**什么、当前的能力上限在哪里：

- **单机数据引擎**：目前只支持单节点部署，**没有**内置复制、高可用、自动故障转移或分片集群。跨节点冗余需要在应用层或运维层自行处理（例如冷备、外部编排）。
- **面向边缘与中小规模**：设计目标是设备网关、工控机、边缘节点和中小型服务，而不是云端大规模 OLAP / 数仓集群。
- **多模型是能力组合，不是各项第一**：把多种数据模型放进一个进程是为了减少组件数量，不是宣称在每个模型上都优于专用数据库。对时序、向量、关系有极致性能要求的场景，仍应评估对应的专用产品。
- **SQL 是子集**：SQL 方言覆盖常见查询、聚合、JOIN、事务和多模型扩展，但不保证完整的 SQL 标准兼容。
- **基准是同机粗测**：README 与文档里的性能数字来自同一台开发机上的同机对照，用于横向参考和持续回归，**不代表**你的生产硬件表现。

在这些边界内，SonnetDB 的目标是把「一个组件覆盖现场常见数据需求」做得可靠、够快、够用。

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

仓库的 Docker 发布工作流会额外构建并推送预编译镜像 `iotsharp/sonnetdb` 与 `ghcr.io/<owner>/sonnetdb`，可直接拉取：

```bash
docker run --rm -p 5080:5080 -v ./sonnetdb-data:/data iotsharp/sonnetdb:latest
```

也可以从源码自行构建镜像：

```bash
docker build -f src/SonnetDB/Dockerfile -t sonnetdb .
docker run --rm -p 5080:5080 -v ./sonnetdb-data:/data sonnetdb
```

启动后访问：

- `http://127.0.0.1:5080/admin/`
- `http://127.0.0.1:5080/help/`

当 `/data/.system` 为空时，`/admin/` 会进入首次安装流程，要求设置服务器 ID、组织名称、管理员用户名 / 密码，以及初始静态 Bearer Token。

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

远程连接示例（支持 `Data Source=` URL 与 PostgreSQL 风格的 `Host`/`Port`/`Username`/`Password` 两种连接串）：

```csharp
using var connection = new SndbConnection(
    "Host=127.0.0.1;Port=5080;Database=metrics;Username=alice;Password=secret");
connection.Open();
```

### 4. 通过 CLI 访问

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

更完整的 CLI、ADO.NET、文档客户端、嵌入式、远程和批量写入示例见 [docs](docs/index.md)。

## 生态下载

### NuGet

[![SonnetDB.Core Version](https://img.shields.io/nuget/v/SonnetDB.Core?label=SonnetDB.Core)](https://www.nuget.org/packages/SonnetDB.Core)
[![SonnetDB Version](https://img.shields.io/nuget/v/SonnetDB?label=SonnetDB)](https://www.nuget.org/packages/SonnetDB)
[![SonnetDB.EntityFrameworkCore Version](https://img.shields.io/nuget/v/SonnetDB.EntityFrameworkCore?label=SonnetDB.EntityFrameworkCore)](https://www.nuget.org/packages/SonnetDB.EntityFrameworkCore)
[![SonnetDB.Caching.EasyCaching Version](https://img.shields.io/nuget/v/SonnetDB.Caching.EasyCaching?label=SonnetDB.Caching.EasyCaching)](https://www.nuget.org/packages/SonnetDB.Caching.EasyCaching)
[![SonnetDB.Caching.Distributed Version](https://img.shields.io/nuget/v/SonnetDB.Caching.Distributed?label=SonnetDB.Caching.Distributed)](https://www.nuget.org/packages/SonnetDB.Caching.Distributed)
[![SonnetDB.Cli Version](https://img.shields.io/nuget/v/SonnetDB.Cli?label=SonnetDB.Cli)](https://www.nuget.org/packages/SonnetDB.Cli)

### Docker

[![Docker Image](https://img.shields.io/docker/v/iotsharp/sonnetdb?label=iotsharp/sonnetdb&sort=semver)](https://hub.docker.com/r/iotsharp/sonnetdb)
[![Docker Pulls](https://img.shields.io/docker/pulls/iotsharp/sonnetdb?label=Docker%20Pulls)](https://hub.docker.com/r/iotsharp/sonnetdb)
[![GHCR Package](https://img.shields.io/badge/GHCR-ghcr.io%2Fiotsharp%2Fsonnetdb-2ea44f)](https://github.com/IoTSharp/SonnetDB/pkgs/container/sonnetdb)

### 连接器

[![C Connector](https://img.shields.io/badge/C-Connector-blue)](connectors/c/README.md)
[![Go Connector](https://img.shields.io/badge/Go-Connector-00ADD8)](connectors/go/README.md)
[![Rust Connector](https://img.shields.io/badge/Rust-Connector-DEA584)](connectors/rust/README.md)
[![Java Connector](https://img.shields.io/badge/Java-Connector-f89820)](connectors/java/README.md)
[![Python Connector](https://img.shields.io/badge/Python-Connector-3776AB)](connectors/python/README.md)
[![VB6 Connector](https://img.shields.io/badge/VB6-Connector-5C2D91)](connectors/vb6/README.md)
[![PureBasic Connector](https://img.shields.io/badge/PureBasic-Connector-5A5A5A)](connectors/purebasic/README.md)
[![Connector Releases](https://img.shields.io/badge/Downloads-GitHub%20Releases-black)](https://github.com/IoTSharp/SonnetDB/releases)

## 项目组成

| 组件 | 说明 |
| --- | --- |
| `src/SonnetDB.Core` | 多模型核心库：时序、关系表、KV、文档、搜索、对象存储适配、本地消息队列、备份恢复和底层持久化。核心库不引入第三方运行时依赖 |
| `src/SonnetDB` | HTTP 服务端、首次安装流程、认证授权、SSE、MCP、Admin UI、Copilot 桥接、二进制帧端点、MQTT broker/client 和内置 `/help` 文档站点 |
| `src/SonnetDB.Data` | ADO.NET 提供程序，NuGet 包名 `SonnetDB`，命名空间 `SonnetDB.Data`；承接 `Microsoft.Extensions.VectorData` 的 SonnetDB adapter |
| `src/SonnetDB.EntityFrameworkCore` | EF Core Provider，提供 `UseSonnetDB(...)`、类型映射、查询翻译和 migrations SQL |
| `src/SonnetDB.Cli` | 命令行工具 `sndb`：本地 / 远程连接、profile 管理、交互式 REPL |
| `extensions/SonnetDB.Caching.EasyCaching` | 基于 SonnetDB KV keyspace 的 EasyCaching Provider |
| `extensions/SonnetDB.Caching.Distributed` | 基于 SonnetDB KV keyspace 的 `IDistributedCache` Provider |
| `web` | 管理后台前端（含 SonnetDB Studio、全局 CopilotDock 与 SPA 发布静态资源） |
| `src/SonnetDB.Studio` | 基于 NativeWebHost 的 SonnetDB Studio 桌面壳 |
| `docs` | JekyllNet 文档站点源码；构建镜像时生成并打包到 `/help` |

DotSearch / DotVector 合并已完成：BM25、分词、距离计算、HNSW / IVF / Vamana、量化和索引序列化能力已收编为 `SonnetDB.Core` 内部引擎，VectorData 适配迁移到 `SonnetDB.Data`。边界见[搜索与向量引擎合并路线图](docs/search-vector-engine-consolidation-roadmap.md)。

## 深入文档

README 只保留项目概览和最短入门路径，完整说明在专题文档中：

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
| 二进制帧协议、MQTT 接入 | [帧协议](docs/frame-protocol.md) |
| 地理空间、轨迹、预测、PID | [地理空间](docs/geo-spatial.md)、[预测](docs/forecast.md)、[PID 控制](docs/pid-control.md) |
| 架构、目录布局、备份恢复 | [架构总览](docs/architecture.md)、[文件格式](docs/file-format.md)、[备份恢复](docs/backup-restore.md) |
| 发布产物、Docker、安装包 | [发布与打包](docs/releases/README.md) |
| 性能与可靠性 | [基准说明](tests/SonnetDB.Benchmarks/README.md)、[近期性能与可靠性变更](docs/performance-reliability-updates.md) |

## 基准与可靠性

性能数字请以 [tests/SonnetDB.Benchmarks/README.md](tests/SonnetDB.Benchmarks/README.md) 为准，那里有完整环境、命令和可复现步骤。**所有对比都是同一台开发机上、Docker 同机容器之间的粗略对照，只用于横向参考和回归，不代表生产部署性能。** README 只保留结论入口：

- 同机 Server-vs-Server 写入对照中，SonnetDB Server 的平均吞吐约为同机 Apache IoTDB Server 的 **1.98x**。
- 嵌入式写入、范围查询、时间窗口聚合、向量召回和地理空间查询都有独立 benchmark；向量部分目前主要覆盖自身 brute-force 与 HNSW 的对比。
- WAL 落盘强度分三级（进程内缓冲 / OS page cache / 每批 fsync），已 flush 的 segment 数据在任何崩溃下都不丢，Delete 无条件同步 WAL；详见[架构总览](docs/architecture.md)与[近期性能与可靠性变更](docs/performance-reliability-updates.md)。

## 与开源栈的能力对齐（Parity）

SonnetDB 通过独立的 parity 套件持续与开源组件对齐行为：PostgreSQL、InfluxDB、VictoriaMetrics、Redis、Qdrant、MinIO、NATS JetStream、Meilisearch 和 ClickHouse。`.github/workflows/parity.yml` 每日运行 `light` / `full` 矩阵，能力、可靠性和算法准确度作为红绿门槛，性能数字只进入 warning/report。可读样例见 [tests/SonnetDB.Parity/reports/sample-run.md](tests/SonnetDB.Parity/reports/sample-run.md)，路线见 [docs/parity-roadmap.md](docs/parity-roadmap.md)。

## 设计原则

- 核心库 safe-only，不使用 `unsafe`，且不引入第三方运行时依赖。
- 一个数据库目录承载持久化数据，而不是以单文件数据库作为产品描述。
- 产品定位是面向 .NET 边缘 / 工业软件的本地多模型数据引擎；多模型是能力组合，不是「样样第一」。
- 嵌入式和服务端共享 SQL / API 语义。
- 管理能力内置到 Server、Web Admin、CLI 和 Copilot 中。
- AI 能力通过 Copilot、MCP 和 provider 抽象服务于数据查询、诊断和运维，不把 SonnetDB 绑定到单一模型供应商。

## 相关文件

- 路线图见 [ROADMAP.md](ROADMAP.md)
- 变更记录见 [CHANGELOG.md](CHANGELOG.md)
- AI 协作规范见 [AGENTS.md](AGENTS.md)
- AI / Agent 索引见 [llms.txt](llms.txt)

## License

[MIT](LICENSE)
