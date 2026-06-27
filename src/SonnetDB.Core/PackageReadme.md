# SonnetDB.Core

`SonnetDB.Core` 是 SonnetDB 的多模型核心引擎包，适合嵌入式本地数据库场景，包含时序、关系表、KV、文档、搜索、向量、对象存储适配和 SonnetMQ 本地消息队列能力。

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

更多发布包、CLI 与服务端说明见仓库根目录 `docs/releases/`。
