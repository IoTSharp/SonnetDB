# SonnetDB.Core

`SonnetDB.Core` 是 SonnetDB 的多模型核心引擎包，适合嵌入式本地数据库场景，包含时序、关系表、KV、文档、搜索、向量、对象存储适配、SonnetMQ 本地消息队列和公开原生属性图 API。

当前 SonnetDB 以数据库目录作为持久化边界，而不是单个数据库文件。嵌入式模式通过 `TsdbOptions.RootDirectory` 打开目录；目录内会按能力拆分 schema、catalog、WAL、segments、tombstone、KV / document 等文件。

`SonnetDB.Core` 继承仓库默认的 trim / Native AOT 分析配置，核心引擎路径面向 AOT 友好实现。需要 AOT 发布的嵌入式应用优先直接使用 `Tsdb` / `SqlExecutor`。

## 安装

```bash
dotnet add package SonnetDB.Core
```

## 最小示例

```csharp
using SonnetDB.Engine;
using SonnetDB.Sql.Execution;

var root = Path.Combine(AppContext.BaseDirectory, "demo-data");

using var db = Tsdb.Open(new TsdbOptions
{
    RootDirectory = root,
});

SqlExecutor.Execute(db, """
    CREATE MEASUREMENT cpu (
        host TAG,
        usage FIELD FLOAT
    )
""");

SqlExecutor.Execute(db, """
    INSERT INTO cpu(host, usage, time)
    VALUES ('server-1', 63.2, 1776477601000)
""");

var result = (SelectExecutionResult)SqlExecutor.Execute(
    db,
    "SELECT time, usage FROM cpu WHERE host = 'server-1'")!;

foreach (var row in result.Rows)
{
    Console.WriteLine($"{row[0]} {row[1]}");
}
```

## 迁移与校验

`MigrationService` 复用一致性备份 manifest，提供 `Export`、`Scan`、`Checksum`、`ImportDryRun` 和 `Import`：

```csharp
using SonnetDB.Backup;

var migration = new MigrationService();
var export = migration.Export(db, new MigrationExportOptions
{
    PackageDirectory = "./exports/demo-v1",
});

Console.WriteLine($"{export.Checksum.Verified}: {export.Checksum.PackageSha256}");
```

迁移包用于兼容 SonnetDB 数据库格式间的离线迁移和回滚准备，不是跨数据库产品的逻辑交换格式。

## 原生属性图 API

`SonnetDB.Graphs` 命名空间公开 `GraphManager`、`GraphStore`、`GraphTransaction`、`GraphReadSession` 和有界遍历合同。以下示例创建一个图并写入顶点：

```csharp
using SonnetDB.Graphs;

var graphRoot = Path.Combine(AppContext.BaseDirectory, "demo-graphs");

using var graphManager = new GraphManager(graphRoot);
GraphStore graph = graphManager.Create("code");

GraphTransaction transaction = graph.BeginTransaction(Guid.NewGuid());
transaction.UpsertVertex(
    new GraphElementId(1),
    expectedElementVersion: 0,
    labels: [new LabelId(1)],
    properties: []);
transaction.Commit();

using GraphReadSession read = graph.BeginRead();
GraphVertex? vertex = read.GetVertex(new GraphElementId(1));
Console.WriteLine(vertex?.Id);
```

该 API 可用于固定版本的跨仓联调和能力验证。M40 的固定硬件、外部语义对拍、Native AOT journey、Couplet 联合门禁和 7 天生产证据尚未全部完成，因此包可用不等于 Native Graph Preview、Graph Beta 或 Production 门禁已经通过。

更多发布包、CLI 与服务端说明见仓库根目录 `docs/releases/`。
