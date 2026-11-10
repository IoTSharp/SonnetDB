<p align="center">
  <img src="web/public/favicon.svg" alt="SonnetDB Logo" width="120" height="120" />
</p>

<h1 align="center">SonnetDB</h1>

<p align="center">九种数据模型，各有原生语义，共享一套引擎</p>

<p align="center">
  <a href="README.md">中文</a> | <a href="README.en.md">English</a>
</p>

[![CI](https://github.com/IoTSharp/SonnetDB/actions/workflows/ci.yml/badge.svg)](https://github.com/IoTSharp/SonnetDB/actions/workflows/ci.yml)
[![CodeQL](https://github.com/IoTSharp/SonnetDB/actions/workflows/codeql.yml/badge.svg)](https://github.com/IoTSharp/SonnetDB/actions/workflows/codeql.yml)
[![Parity](https://github.com/IoTSharp/SonnetDB/actions/workflows/parity.yml/badge.svg)](https://github.com/IoTSharp/SonnetDB/actions/workflows/parity.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![GitHub Release](https://img.shields.io/github/v/release/IoTSharp/SonnetDB?label=Release)](https://github.com/IoTSharp/SonnetDB/releases)
[![VS Code Marketplace](https://img.shields.io/visual-studio-marketplace/v/iotsharp.sonnetdb-vscode?label=VS%20Code)](https://marketplace.visualstudio.com/items?itemName=iotsharp.sonnetdb-vscode)

## SonnetDB 是什么

SonnetDB 是一款面向 .NET 10 的单节点多模型数据引擎。它把时序、关系表、键值、JSON 文档、全文、向量、对象、消息队列和原生属性图放在同一个引擎中，同时保留每种模型自己的建模和查询语义。

应用可以在进程内打开数据库目录，也可以连接独立 Server。SQL、ADO.NET、EF Core、CLI、HTTP REST/Frame、Web Admin、Studio、VS Code、MCP 和多语言连接器共享清晰的边界与权限模型。

数据库目录是持久化边界。Server 的身份、授权、Token 和实例级 SonnetMQ 位于不同范围；单库备份不等于整个 Server 实例的备份。版本、配置、恢复和外部依赖应按 [发布与打包](docs/releases/README.md) 及对应专题合同核对。

当前源码默认使用 LSM KV 存储，关系表共享数据库内的 `tables/lsm/` 实例。已有 legacy 数据在首次打开时自动迁移；迁移后的目录旧版本无法打开。升级前备份、关闭自动迁移及保留期内回滚的条件见 [KV 存储引擎与 LSM 迁移](docs/operations/kv-lsm-migration.md)。使用已发布包时须核对该版本的引擎与迁移合同。

## 九种数据模型

| 模型 | 适合处理的内容 | 主要入口 |
| --- | --- | --- |
| 时序 | 设备指标、工业采集、时间窗口聚合、Retention | SQL、批量写入、Line Protocol、MQTT、SndbTimeSeriesClient |
| 关系表 | 业务实体、主键、索引、JOIN、约束与同库事务 | SQL、ADO.NET、EF Core、Frame SQL |
| KV / 缓存 | 状态、会话、配置、TTL、前缀读取与原子操作 | SndbKvClient、DistributedCache、EasyCaching |
| JSON 文档 | 集合、JSON path、过滤、投影、排序、索引和聚合 | SndbDocumentClient、SQL、REST |
| 全文检索 | BM25、中文和多语言分词、短语与模糊检索 | SndbFullTextClient、SQL |
| 向量检索 | 精确检索、HNSW、混合检索和 VectorData 集成 | SonnetDBVectorStore、SQL、REST |
| 对象存储 | Bucket、分片上传、Range 读取、版本和保留策略 | SndbObjectStorageClient、REST/Frame |
| SonnetMQ | Topic、消费者组、Pull/Push、ACK、Replay、Retention 和 DLQ | SndbMqClient、Server |
| 原生属性图（Graph Beta） | 顶点、边、标签、属性、受限路径和 SQL/PGQ | SndbGraphClient、SQL/PGQ、Graph API |

空间/轨迹、流处理/订阅、CDC/本地同步、AI/RAG、治理/恢复/观测是跨模型平台能力，不增加原生模型数量。每个能力的成熟度和证据见 [能力成熟度](docs/capability-maturity.md) 与 [十四项能力证据索引](docs/audits/fourteen-capability-evidence-index.json)。

## 选择使用方式

| 你的目标 | 推荐入口 |
| --- | --- |
| 在 .NET 进程内嵌入引擎 | SonnetDB.Core 的 `Tsdb.Open(...)` 与 SQL 执行器 |
| 为应用提供标准数据库连接 | SonnetDB 包中的 SonnetDB.Data / SndbConnection |
| 接入 EF Core | SonnetDB.EntityFrameworkCore 的 UseSonnetDB |
| 启动独立服务 | Docker 镜像或 src/SonnetDB，见 [开始使用](docs/getting-started.md) |
| 管理本地或远程实例 | sndb CLI、Web Admin、Studio 或 VS Code |
| 走高吞吐 HTTP/2 | Frame 协议；REST 仍是兼容入口 |
| 对接设备和工业协议 | MQTT、Sparkplug B、CoAP、Line Protocol、Modbus（按配置和权限启用） |
| 从其他语言接入 | connectors/c、go、rust、java、python、vb6、purebasic |
| 让 AI / Agent 查询数据 | Copilot、MCP、llms.txt 和 llms-full.txt |

## 快速开始

### 嵌入式最小示例

使用 .NET 10 创建示例应用并安装核心包：

~~~bash
dotnet new console -f net10.0 -n SonnetDemo
cd SonnetDemo
dotnet add package SonnetDB.Core
~~~

将 Program.cs 替换为以下内容，在新建的数据目录上运行：

~~~csharp
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
~~~

运行 `dotnet run` 查看查询结果。

### 启动服务端

下面使用滚动镜像快速体验。[Docker 镜像说明](docs/releases/docker-image.md)列出版本标签规则；需要固定版本时，从发行记录选择对应标签或 digest。

~~~bash
docker run --rm -p 127.0.0.1:5080:5080 \
  -v ./sonnetdb-data:/data \
  iotsharp/sonnetdb:latest
~~~

也可以从仓库根目录构建：

~~~bash
docker build -f src/SonnetDB/Dockerfile -t sonnetdb .
docker run --rm -p 127.0.0.1:5080:5080 -v ./sonnetdb-data:/data sonnetdb
~~~

启动后可访问管理界面和帮助中心：

- [管理界面](http://127.0.0.1:5080/admin/)
- [帮助中心](http://127.0.0.1:5080/help/)

首次安装、认证、远程开放接口和容器卷的边界见 [Docker 首次初始化与远程部署](docs/releases/docker-image.md)。

### ADO.NET 与 CLI

应用访问使用 NuGet 包 [SonnetDB](https://www.nuget.org/packages/SonnetDB)，命名空间为 `SonnetDB.Data`。以下示例查询前面创建的数据库：

~~~csharp
using SonnetDB.Data;

using var connection = new SndbConnection("Data Source=./demo-data");
connection.Open();

using var command = connection.CreateCommand();
command.CommandText = "SELECT count(*) FROM cpu";
var count = (long)(command.ExecuteScalar() ?? 0L);
Console.WriteLine(count);
~~~

~~~bash
dotnet tool install --global SonnetDB.Cli
sndb local --path ./demo-data --command "SELECT count(*) FROM cpu"
sndb remote --url http://127.0.0.1:5080 --database metrics --token your-token --repl
~~~

更多嵌入式、ADO.NET、EF Core、CLI、远程连接和批量写入示例见 [文档中心](docs/index.md)。

## 下载与生态

### NuGet

[SonnetDB.Core](https://www.nuget.org/packages/SonnetDB.Core) ·
[SonnetDB](https://www.nuget.org/packages/SonnetDB) ·
[SonnetDB.EntityFrameworkCore](https://www.nuget.org/packages/SonnetDB.EntityFrameworkCore) ·
[SonnetDB.Caching.Distributed](https://www.nuget.org/packages/SonnetDB.Caching.Distributed) ·
[SonnetDB.Caching.EasyCaching](https://www.nuget.org/packages/SonnetDB.Caching.EasyCaching) ·
[SonnetDB.Cli](https://www.nuget.org/packages/SonnetDB.Cli)

版本和兼容组合以 NuGet、GitHub Release 和 [发布说明](docs/releases/README.md)的实际读回为准。

### Docker、VS Code 和连接器

- [Docker Hub](https://hub.docker.com/r/iotsharp/sonnetdb) · [GHCR](https://github.com/IoTSharp/SonnetDB/pkgs/container/sonnetdb)
- [SonnetDB for VS Code](https://marketplace.visualstudio.com/items?itemName=iotsharp.sonnetdb-vscode)
- [C](connectors/c/README.md) · [Go](connectors/go/README.md) · [Rust](connectors/rust/README.md) · [Java](connectors/java/README.md)
- [Python](connectors/python/README.md) · [VB6](connectors/vb6/README.md) · [PureBasic](connectors/purebasic/README.md)

## 项目组成

| 目录或项目 | 职责 |
| --- | --- |
| src/SonnetDB.Core | 多模型核心、目录、WAL、索引与持久化 |
| src/SonnetDB | HTTP Server、REST/Frame、MCP、Admin、Copilot、MQTT 与帮助站 |
| src/SonnetDB.Data | ADO.NET 和 VectorData 适配 |
| src/SonnetDB.EntityFrameworkCore | EF Core Provider |
| src/SonnetDB.Cli | sndb 命令行工具 |
| src/SonnetDB.Studio | NativeWebHost 桌面壳 |
| web | Admin、Studio 和静态资源 |
| connectors | 多语言和 .NET 客户端 |
| extensions | 缓存、CAP、媒体和协议扩展 |
| samples / tests | 可运行样例、单元测试、生态测试、Parity 和基准 |
| docs / eng | 用户文档、设计/审计/发布记录、构建和验证工具 |

## 文档导航

- [文档中心](docs/index.md)：按用户目标选择主题。
- [开始使用](docs/getting-started.md)、[架构总览](docs/architecture.md)、[数据模型](docs/data-model.md)。
- [SQL 参考](docs/sql-reference.md)、[SQL Cookbook](docs/sql-cookbook.md)、[ADO.NET](docs/ado-net.md)、[EF Core](docs/efcore.md)、[CLI](docs/cli-reference.md)。
- [KV](docs/kv-keyspace.md)、[Document Store](docs/document-store.md)、[向量搜索](docs/vector-search.md)、[批量写入](docs/bulk-ingest.md)。
- [帧协议](docs/frame-protocol.md)、[设备接入](docs/protocol-ingest.md)、[Sparkplug B](docs/sparkplug-b.md)、[可观测性](docs/observability.md)。
- [Web Admin 与管理工具](docs/web-workbench.md)、[三面能力矩阵](docs/management-tools.md)、[发布与打包](docs/releases/README.md)。
- [AI / Agent 指南](docs/ai-agent-guide.md)、[MCP Typed Contract](docs/mcp-contract.md)、[llms.txt](llms.txt)、[llms-full.txt](llms-full.txt)。
- [路线图](ROADMAP.md)、[变更日志](CHANGELOG.md)、[AI 协作规范](AGENTS.md)。

## 适用范围

- SonnetDB 当前是单节点引擎，没有内置复制、分片、自动故障转移或高可用集群。
- SQL 是 SonnetDB 方言的实用子集；对象、MongoDB-like、Redis-like、MQ 和 Graph 入口不自动承诺外部协议兼容。
- Graph Beta、性能、恢复、安装、固定硬件、长稳、真实模型质量和生产门禁必须按各自证据报告判断。
- 源码、公开包、真实 Server/消费者、NativeAOT、现场和公开读回分别计证；一个层级通过不能推导其他层级通过。
- 版本、宿主和部署 profile 决定可用操作；具体范围以匹配版本的文档和实际 schema 为准。

## 性能与可靠性

性能数字必须连同版本、机器、语料、命令和原始样本阅读。已有报告多为同机或受限环境对照，不代表你的生产硬件。

WAL、segment、对象传输、MQ、CDC、RAG 和备份都有各自的持久化与恢复边界。写入响应、正常重开、强杀恢复、断电、介质故障和完整机器恢复是不同证据等级，详见 [性能与可靠性近期变更](docs/performance-reliability-updates.md) 与 [备份恢复](docs/backup-restore.md)。

## 在线演示与社区

- [在线演示](https://iotsharp.online/sonnetdb/admin/login)：账号和权限以演示站当前页面为准。
- [GitHub Issues](https://github.com/IoTSharp/SonnetDB/issues) · [GitHub Releases](https://github.com/IoTSharp/SonnetDB/releases)
- [企业微信交流群](docs/assets/qr-group.png)

## License

[MIT](LICENSE)
