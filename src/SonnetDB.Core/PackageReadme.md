# SonnetDB.Core

`SonnetDB.Core` 是 SonnetDB 的多模型核心引擎包，适合嵌入式本地数据库场景，包含时序、关系表、KV、文档、搜索、向量、对象存储适配和 SonnetMQ 本地消息队列能力。

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
