## 嵌入式 C# API：Tsdb.Open() 与 SqlExecutor.Execute() 进程内数据库编程

SonnetDB 可以直接运行在应用进程中。应用打开一个数据库目录，通过 SQL 或结构化 `Point` 对象读写数据，不必为这一使用方式另外启动 HTTP 服务。适用场景包括桌面程序、边缘采集与需要真实持久化行为的集成测试。

本文依据当前嵌入式 API 文档整理。正式 4.0.0 与持续变化的 main 分开管理，包版本应与实际部署版本一致；下面示例是独立演示，使用新的空目录，避免与业务数据库混用。

### 引用核心包

```bash
dotnet add package SonnetDB.Core
```

核心引擎入口在 `SonnetDB.Engine`，SQL 执行入口在 `SonnetDB.Sql.Execution`。示例使用 .NET 10 与 C# 原始字符串。

### 从建表到查询

```csharp
using SonnetDB.Engine;
using SonnetDB.Sql.Execution;

using var db = Tsdb.Open(new TsdbOptions
{
    RootDirectory = "./embedded-cpu-demo",
});

SqlExecutor.Execute(db, """
CREATE MEASUREMENT cpu_usage (host TAG, cpu_pct FIELD FLOAT)
""");

SqlExecutor.Execute(db, """
INSERT INTO cpu_usage (time, host, cpu_pct) VALUES
    (1713676800000, 'web-01', 78.5),
    (1713676860000, 'web-01', 74.2)
""");

var result = (SelectExecutionResult)SqlExecutor.Execute(db, """
SELECT time, host, cpu_pct FROM cpu_usage
WHERE host = 'web-01'
ORDER BY time
LIMIT 100
""")!;

foreach (var row in result.Rows)
    Console.WriteLine($"{row[0]} {row[1]} {row[2]}");
```

`using` 管理数据库生命周期；同一个目录应由协调一致的宿主管理，不应假定多个独立进程能够随意共享写入。示例里的时间戳是 Unix 毫秒，字段数值使用 `FLOAT`。

### 已有结构化数据时直接写 Point

下面代码接在同一个 `db` 和已经创建的 `cpu_usage` 后执行。

```csharp
var points = new[]
{
    SonnetDB.Model.Point.Create(
        "cpu_usage", 1713676920000,
        new Dictionary<string, string> { ["host"] = "web-02" },
        new Dictionary<string, SonnetDB.Model.FieldValue>
        {
            ["cpu_pct"] = SonnetDB.Model.FieldValue.FromDouble(65.8)
        }),
    SonnetDB.Model.Point.Create(
        "cpu_usage", 1713676980000,
        new Dictionary<string, string> { ["host"] = "web-02" },
        new Dictionary<string, SonnetDB.Model.FieldValue>
        {
            ["cpu_pct"] = SonnetDB.Model.FieldValue.FromDouble(67.1)
        })
};
db.WriteMany(points);
```

SQL 适合维护统一脚本，`Point` 适合消费应用已有对象并减少 SQL 文本构造。`WriteMany` 是批量入口，吞吐量仍受字段数量、批大小、存储和 flush 策略影响。

### 解析、参数与持久化边界

SQL 解析器检查语法，但把外部字符串拼接成 SQL 仍可能造成注入。需要用户参数时，优先采用 `SonnetDB.Data` 的 ADO.NET 参数绑定；表名等标识符还需要应用自行校验。

持久化边界是整个数据库目录，包含 catalog、schema、WAL、segments 等文件，不能只复制某个 `.SDBSEG` 当作完整备份。查询结果也需要设置时间范围和 `LIMIT`，不能仅因接口在进程内就忽略内存规模。

嵌入引擎不会自动启用 AI 模型。Copilot 的 provider、模型与权限需要另外配置，在线模型调用仍会产生网络访问与成本。

参考：[嵌入式 API](https://github.com/IoTSharp/SonnetDB/blob/main/docs/embedded-api.md)、[ADO.NET](https://github.com/IoTSharp/SonnetDB/blob/main/docs/ado-net.md)、[正式 4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
