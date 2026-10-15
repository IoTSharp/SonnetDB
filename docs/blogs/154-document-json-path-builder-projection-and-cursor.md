---
title: SonnetDB 文档查询工作流：JSON Path、Builder、投影与 Cursor
categories: SonnetDB
tags: dotnet, JSON, Document, Cursor
draft: false
---

# SonnetDB 文档查询工作流：JSON Path、Builder、投影与 Cursor

SonnetDB 的 Document Store 可以通过 .NET Builder 组合 JSON Path 条件、投影和排序，再用 continuation token 分页读取 JSON 文档。本文从设备档案查询出发，说明如何得到可核对的结果，以及 token 无效、过期或集合变化时怎样停止并明确重查。

![SonnetDB 文档查询与分页：JSON Path、Builder 与 Cursor](https://img2024.cnblogs.com/blog/1725297/202610/1725297-20261014111450929-875178087.png)

图：AI 主题示意图，并非实测截图；分页 token 与查询及快照绑定，不表示 MongoDB 协议兼容或长期断点恢复承诺。

## 1. 先选对客户端和版本依据

入口是 `SonnetDB.Data.Documents.SndbDocumentClient`。本地连接指向数据库目录，远程连接指向 SonnetDB Server：

```text
Data Source=./document-quickstart-data
Data Source=sonnetdb+http://127.0.0.1:5080/app;Token=<token>
```

它不是 MongoDB wire protocol，不能用 MongoDB URI 或官方 MongoDB Driver 替换接入。文档 ID 使用 SonnetDB 字符串身份，也不能推断为 MongoDB `ObjectId`。

本文于 **2026-10-14** 对照本地源码提交 `dcadffc7137f5653ec46c55a4171cf5f2d83de03`，链接固定到已公开且与本专题文件相同的 `5424b66672e37f49a2a5633fdde0b54a9b1f0290`。当日已核正式 GitHub Release 与 NuGet Core 为 **4.0.0**；本篇描述当前复核源码合同，不把源码存在自动写成所有发行资产已经通过同样验收。Graph 仍是 Beta，与本文文档查询验收分开。

## 2. 先运行仓库提供的 Quickstart

准备 .NET 10 SDK，在仓库根目录运行：

```powershell
dotnet run --project samples/SonnetDB.DocumentQuickstart
```

要连接已配置的远程 Server：

```powershell
$env:SONNETDB_CONNECTION='Data Source=sonnetdb+http://127.0.0.1:5080/app;Token=<token>'
dotnet run --project samples/SonnetDB.DocumentQuickstart
```

样例会初始化数据库和集合、执行 Bulk、更新文档并查询，必须指向允许写入的测试环境。远程查询需要目标数据库 Read，准备数据与集合需要相应 Write/管理权限。

样例把 `device-001` 的 `score` 从 21 乘以 2，并将状态设为 `online`；因此，后续“online 且 score ≥ 20”的过滤应命中该文档，投影中应能观察到 `site=east` 和 `score=42`。这些是代码可核对的预期；本次写作发布没有执行数据库示例，不能当作本次实测输出。

## 3. 条件、投影、排序分别表达什么

下面三段组成同一个查询：

```csharp
var filter = new SndbDocumentFilterBuilder()
    .Equal("$.status", "online")
    .GreaterThanOrEqual("$.score", 20)
    .Build();

var projection = new SndbDocumentProjectionBuilder()
    .Include("_id")
    .Include("$.site", "site")
    .Include("$.score", "score")
    .Build();

var sort = new SndbDocumentSortBuilder()
    .Descending("$.score")
    .Ascending("_id")
    .Build();
```

同一个 FilterBuilder 连续添加这两个条件，表示同时满足。`$.score` 是 JSON path；`_id` 是文档身份入口。JSON 属性键不因 SQL 标识符的大小写规则而自动折叠。

投影的 `Include(path, name)` 控制输出字段，其中 `site`、`score` 是选定的结果名，不会修改原文档。排序先按 score 降序，再按 ID 升序，便于相同分数时核对结果。Builder 生成 SonnetDB DTO，并不意味着支持完整 MQL 或任意 MongoDB 表达式投影。

## 4. 用一个独立集合检查逐页结果

以下是可放进已经引用 `SonnetDB.Data` 的 .NET 控制台项目的完整流程。每次使用独立测试集合，避免旧示例数据影响结果；运行后测试集合仍保留，按实际名称有界清理，不应清空共享数据库。

```csharp
using SonnetDB.Data;
using SonnetDB.Data.Documents;
using SonnetDB.Documents;

var connectionString = Environment.GetEnvironmentVariable("SONNETDB_CONNECTION")
    ?? "Data Source=./document-walkthrough";
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var ct = timeout.Token;
await SndbResourceInitializer.EnsureDatabaseAsync(connectionString, "文档查询示例", ct);
using var documents = new SndbDocumentClient(connectionString);
var collection = $"blog_query_{Guid.NewGuid():N}";
Console.WriteLine($"collection={collection}");
await documents.CreateCollectionAsync(collection, cancellationToken: ct);
await documents.InsertOneAsync(collection, "device-001",
    """{"site":"east","status":"online","score":42}""", ct);
await documents.InsertOneAsync(collection, "device-002",
    """{"site":"west","status":"idle","score":9}""", ct);
await documents.InsertOneAsync(collection, "device-003",
    """{"site":"east","status":"online","score":25}""", ct);

var options = new SndbDocumentFindOptions(
    Filter: new SndbDocumentFilterBuilder()
        .Equal("$.status", "online")
        .GreaterThanOrEqual("$.score", 20).Build(),
    Projection: new SndbDocumentProjectionBuilder()
        .Include("_id").Include("$.site", "site")
        .Include("$.score", "score").Build(),
    Sort: new SndbDocumentSortBuilder()
        .Descending("$.score").Ascending("_id").Build(),
    Limit: 1);

string? continuation = null;
try
{
    for (var pageNumber = 1; pageNumber <= 5; pageNumber++)
    {
        ct.ThrowIfCancellationRequested();
        var page = await documents.FindPageAsync(collection,
            options with { ContinuationToken = continuation }, ct);
        Console.WriteLine($"page={pageNumber}, count={page.Documents.Count}, more={page.HasMore}");
        foreach (var document in page.Documents)
            Console.WriteLine($"{document.Id}: {document.Json}");
        continuation = page.ContinuationToken;
        if (!page.HasMore)
            break;
        if (continuation is null)
            throw new InvalidOperationException("有后续页却未提供 token。");
        if (pageNumber == 5)
            throw new InvalidOperationException("达到本示例页数预算，停止读取。");
    }
}
catch (DocumentCursorException ex)
{
    Console.WriteLine($"查询停止：{ex.Code}；由调用方决定是否明确重查。");
    throw;
}
```

在没有并发集合变更、操作均成功且未到取消期限的前提下，应看到：

| 页 | 文档 | 投影关键值 | HasMore |
|---|---|---|---|
| 1 | device-001 | east、42 | true |
| 2 | device-003 | east、25 | false |

这里 `Limit: 1` 是每页 batch size，**不是整个 cursor 只允许返回一条**。如果应用要限制总输出量，需要另设累计文档预算。上例总计最多五页并有取消期限；这个应用预算也不是引擎总内存或扫描成本的证明。

## 5. Cursor 的便利与续页边界

不需要自己搬运 token 时，可使用：

```csharp
var cursor = documents.FindCursor(collection, options);
var read = 0;
await foreach (var document in cursor.ReadAllAsync(ct))
{
    Console.WriteLine(document.Id);
    if (++read >= 10)
        break;
}
```

这是把上节查询循环替换为 SDK cursor 的独立读取方式。SDK 仍按页请求；异步枚举不证明执行器以固定内存流式扫描全部结果。同一个 cursor 不允许并发读取。

当前 token 绑定集合、查询指纹、快照版本及有效期。嵌入式实现生成有后续页的 token 时设置十五分钟到期；调用方应查看页响应的 `CursorExpiresAtUtc`，不能把这个实现值视作永久 SLA。SDK 续页会保留查询条件并将后续 Skip 归零，位置由 token 决定。

`ReadAllAsync()` 从当前 cursor 位置读取剩余页；如果已经手动读取并消费 `Current`，不要期待它重放那一页。需要从外部保存的 token 重新建立 cursor 时，保持原查询形状；这仍受过期和快照检查约束，不是可跨任意重启的持久摄取 checkpoint。

| 错误码 | 含义 | 调用方处理 |
|---|---|---|
| `document_cursor_invalid` | 格式、版本或签名无效 | 停止使用该 token，核对来源 |
| `document_cursor_mismatch` | 集合或查询形状改变 | 明确创建新查询，不拼接旧结果 |
| `document_cursor_expired` | 超过有效期 | 按业务策略重新查询并处理已消费结果 |
| `document_cursor_stale` | 集合版本变化 | 停止旧分页，决定新的读取批次 |
| `document_cursor_concurrent_read` | 同一 cursor 正在读取 | 串行使用该 cursor |

SDK 不会静默从第一页重跑。若应用在已输出若干条后重启查询，应自行处理重复消费；不能把分页错误吞掉后假装一次完整快照读取成功。

## 6. 性能与容量该怎样表述

小页限制返回量，不能由此推断排序、过滤和投影只扫描少量数据。索引、查询形状与应用预算需要分别检查。文档容量专题中的历史验证也不能换算成本次机器、任意百万文档或长期生产承诺。

本次复核覆盖查询 DTO、Builder、SDK 分页、token 校验及既有测试源文件，没有执行本篇 C#、Server、容量或故障恢复测试；没有产生新的性能数字。更完整的索引、Bulk、持久恢复和迁移任务应按各自合同验收。

## 参考与复核入口

- [Document Store 能力矩阵](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/document-store.md)
- [Document Quickstart](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/samples/SonnetDB.DocumentQuickstart/README.md)
- [Builder 实现](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Data/Documents/SndbDocumentBuilders.cs)
- [查询与分页客户端](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Data/Documents/SndbDocumentClient.cs)
- [Cursor 逐页读取实现](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Data/Documents/SndbDocumentCursor.cs)
- [Token 与查询指纹](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Core/Documents/DocumentCursorToken.cs)
