---
layout: default
title: "嵌入式与 in-proc API"
description: "直接在进程内使用 SonnetDB 引擎的方式，包括 SQL 执行和 Point/WriteMany 示例。"
permalink: /embedded-api/
---

## 适用场景

如果你的应用和数据库在同一个进程内运行，最直接的方式是使用 `SonnetDB` 核心引擎：

- 打开一个本地数据库目录
- 定义 measurement schema
- 通过 SQL 或直接 `Point` API 写入
- 通过 SQL 读取

## 安装

```bash
dotnet add package SonnetDB.Core
```

## 用 SQL 执行最小示例

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
VALUES
    (1713676800000, 'server-01', 0.71),
    (1713676860000, 'server-01', 0.73)
""");

var result = (SelectExecutionResult)SqlExecutor.Execute(
    db,
    "SELECT time, host, usage FROM cpu WHERE host = 'server-01'")!;

foreach (var row in result.Rows)
{
    Console.WriteLine($"{row[0]} {row[1]} {row[2]}");
}
```

## 直接写入 `Point`

如果你已经在业务层拿到了结构化点位对象，可以直接使用 `Point.Create(...)` 和 `WriteMany(...)`：

```csharp
using SonnetDB.Engine;
using SonnetDB.Model;
using SonnetDB.Sql.Execution;

using var db = Tsdb.Open(new TsdbOptions
{
    RootDirectory = "./demo-data",
});

SqlExecutor.Execute(db, "CREATE MEASUREMENT cpu (host TAG, usage FIELD FLOAT)");

var points = new[]
{
    Point.Create(
        "cpu",
        1713676800000,
        new Dictionary<string, string> { ["host"] = "server-01" },
        new Dictionary<string, FieldValue> { ["usage"] = FieldValue.FromDouble(0.71) }),
    Point.Create(
        "cpu",
        1713676860000,
        new Dictionary<string, string> { ["host"] = "server-01" },
        new Dictionary<string, FieldValue> { ["usage"] = FieldValue.FromDouble(0.73) }),
};

db.WriteMany(points);
```

## 删除示例

```csharp
using SonnetDB.Engine;
using SonnetDB.Sql.Execution;

using var db = Tsdb.Open(new TsdbOptions
{
    RootDirectory = "./demo-data",
});

SqlExecutor.Execute(db, """
DELETE FROM cpu
WHERE host = 'server-01' AND time >= 1713676800000 AND time <= 1713677400000
""");
```

## 何时使用 SQL，何时直接用 `Point`

推荐用 SQL 的情况：

- 需要统一脚本和测试输入
- 业务本身就以 SQL 组织操作
- 需要和 ADO.NET / CLI 保持同一套用法

推荐直接用 `Point` 的情况：

- 你已经在代码里拥有结构化点位对象
- 想绕开 SQL 解析开销
- 需要配合 `WriteMany` 批量写入

## 目录路径而不是单文件

嵌入式模式打开的是一个目录：

```text
./demo-data/
├─ catalog.SDBCAT
├─ measurements.tslschema
├─ tombstones.tslmanifest
├─ wal/
└─ segments/
```

不是单个 `.tsl` 文件。

## 相关页面

- [SQL 参考]({{ site.docs_baseurl | default: '/help' }}/sql-reference/)
- [批量写入]({{ site.docs_baseurl | default: '/help' }}/bulk-ingest/)
- [架构总览]({{ site.docs_baseurl | default: '/help' }}/architecture/)
