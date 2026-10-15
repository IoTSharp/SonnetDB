---
title: SonnetDB 文档局部更新与 Mixed Bulk：版本、幂等和逐项结果
categories: SonnetDB
tags: dotnet, JSON, Document, Bulk, 幂等
draft: false
---

# SonnetDB 文档局部更新与 Mixed Bulk：版本、幂等和逐项结果

SonnetDB 的 Document Store 支持局部更新、原子 findOneAndUpdate 和单集合 Mixed Bulk。本文用设备档案说明怎样修改指定字段、读取更新后的文档，并通过版本和 requestId 区分并发冲突、幂等重放与尚未确认的提交结果。

![SonnetDB 文档局部更新与 Mixed Bulk：版本、幂等和逐项结果](https://img2024.cnblogs.com/blog/1725297/202610/1725297-20261015110840863-607967422.png)

图：AI 主题示意图，并非实测截图；图中的文档变化用于解释操作语义，不代表跨集合事务或无限期去重。

## 1. 准备可复现的文档客户端

使用 .NET 10 和仓库中的 `SonnetDB.DocumentQuickstart`。在 SonnetDB 仓库根目录运行：

```powershell
dotnet run --project samples/SonnetDB.DocumentQuickstart
```

默认连接为 `Data Source=./document-quickstart-data`，会在相对当前目录的数据库目录创建 `device_profiles`。远程测试需先准备支持同一 Document API 的 Server、数据库及写权限，再设置 `SONNETDB_CONNECTION`。不要把 MongoDB URI 或官方 MongoDB Driver 当作 SonnetDB 的接入方式。

下面的片段与 Quickstart 使用相同 API，可作为它的修改参考。独立程序需引用匹配源码版本的 `SonnetDB.Data` 项目：

```csharp
using SonnetDB.Data;
using SonnetDB.Data.Documents;

const string collection = "device_profiles";
const string connection = "Data Source=./document-quickstart-data";
await SndbResourceInitializer.EnsureDatabaseAsync(connection, "Document update tutorial");
using var documents = new SndbDocumentClient(connection);
await documents.CreateCollectionAsync(collection, ifNotExists: true);

var activate = new SndbDocumentUpdateBuilder()
    .Set("$.status", "online")
    .Multiply("$.score", 2)
    .Build();

SndbDocumentBulkWriteOperation[] operations =
[
    SndbDocumentBulkWrites.ReplaceOne(
        "device-001",
        """{"site":"east","status":"new","score":21,"tags":["pump","critical"]}""",
        upsert: true),
    SndbDocumentBulkWrites.ReplaceOne(
        "device-002",
        """{"site":"west","status":"idle","score":9,"tags":["meter"]}""",
        upsert: true),
    SndbDocumentBulkWrites.UpdateOne(
        SndbDocumentFilters.Equal("$.site", "east"), activate),
    SndbDocumentBulkWrites.DeleteMany(
        SndbDocumentFilters.Equal("$.status", "retired")),
];

string requestId = $"device-sync-{Guid.NewGuid():N}";
var first = await documents.BulkWriteAsync(
    collection, operations, ordered: true, requestId: requestId);
var replay = await documents.BulkWriteAsync(
    collection, operations, ordered: true, requestId: requestId);

Console.WriteLine($"committed={first.Committed}, replayed={replay.Replayed}");
foreach (var item in first.Items)
    Console.WriteLine($"{item.Index}: {item.Operation}, {item.Status}, {item.Error?.Code}");
```

用空目录或明确知道内容的测试目录验证。按源码合同且没有其它并发写入、validator 或唯一索引冲突时，第一批应提交；第二次应读取该 requestId 的持久化结果，并标记 `Replayed`。`device-001` 的 score 应为 42，第二次重放不会再乘成 84。`device-002` 不匹配 east 条件，score 保持 9；不存在 retired 文档时，删除影响数为 0。

这些是供读者核对的预期结果，本文没有把代码审阅写成已执行测试。

## 2. 局部修改与整体替换分别承担什么任务

`ReplaceOne` 用整份 JSON 替换已有文档，示例中的 `upsert:true` 允许目标不存在时创建。局部更新 builder 则只处理声明的路径，适合修改状态、计数器或标签：

| 目的 | 操作符或 builder 示例 | 核对点 |
|---|---|---|
| 设置属性 | `$set` / `.Set("$.status", "online")` | 路径和类型是否正确 |
| 数字递增或乘法 | `$inc` / `.Increment(...)`，`$mul` / `.Multiply(...)` | 重复执行会改变结果，不能盲目重发 |
| 集合式追加 | `$addToSet` / `.AddToSet("$.tags", "inspected")` | 区分数组成员去重与整个请求幂等 |
| 其它已实现更新 | `$unset/$min/$max/$rename/$push/$pull/$pop/$currentDate` | 按各自 DTO 与测试核对参数 |

不支持 positional `$/$[]` 或 `arrayFilters`。复杂数组改写可以读取后整体替换，并带上读取时的文档版本，避免覆盖其它写入者的修改。JSON 属性名保留自身语义，不套用 SQL 标识符的忽略大小写规则。

## 3. 需要前后镜像时用 findOneAndUpdate

```csharp
var result = await documents.FindOneAndUpdateAsync(
    collection,
    new SndbDocumentFindOneAndUpdateOptions(
        new SndbDocumentUpdateBuilder()
            .AddToSet("$.tags", "inspected")
            .Build(),
        Id: "device-001",
        ReturnDocument: SndbDocumentReturnDocument.After));
Console.WriteLine(result.Document?.Json);
```

`After` 返回本次更新后的文档；`Before` 返回修改前文档。Core 在集合锁内完成匹配、修改与镜像选择，避免应用分开执行 find 和 update 时把其它写入者的中间状态误认为本次结果。仍须检查返回的写入结果和错误，`Document` 为空不能单独解释为成功。

这一步是新的更新操作，不属于前一个 Bulk 的 requestId。它不会自动继承 Bulk 的幂等保护。

## 4. 版本解决并发覆盖，requestId 解决同一批次重放

读取返回的 `SndbDocument.Version` 是不透明的版本值，不是业务自增序号。将它交给 `SndbDocumentBulkWrites.ReplaceOne(..., expectedVersion: previouslyRead.Version)` 可以让替换在旧版本已经变化时返回冲突。冲突后需要重新读取、比较业务字段，再决定是否构造新的写入意图。

`requestId` 的使用方式不同：

1. 第一次发送前，为业务批次生成并持久保存一个 ID，同时保存操作内容与 `ordered` 设置。
2. 需要确认同一批次时，在保留期内提交相同 ID、相同操作及相同设置。
3. 相同 ID 对应不同指纹时返回幂等冲突，不能通过改内容复用旧 ID。
4. 已确定需要新的业务操作时才使用新 ID；不要把“原请求结果未知”直接改成一个新 ID。

当前 journal 保留期为 **24 小时**。超过窗口不提供无限去重承诺；没有持久保存原 ID、跨集合重新拆批、改动操作顺序或选项，都不能沿用“同一请求可安全重放”的结论。

## 5. ordered 与 unordered 的结果怎样读

| 模式 | 遇到坏项后的合同 | 应用应记录 |
|---|---|---|
| `ordered:true` | 任一错误使整批零文档提交，后续项标记未尝试；失败结果也可进入幂等 journal | `Committed`、全部 `Items/Errors`、失败位置和 requestId |
| `ordered:false` | 跳过失败项，将有效项作为单集合批次提交 | 每项状态及错误，区分已提交项与待修正项 |

这比“ordered 只会停止后续执行，前面已经写入”的常见假设更严格，应按 SonnetDB 源码合同处理。`Items.Count` 也不是成功数；已有项的 no-op、未匹配、失败和未尝试都需要分别判读。

批量写必须非空，最多 1000 项；Core canonical payload 上限为 16 MiB，网络服务的请求体限制还需单独核对。只允许与操作类型相关的字段。原子边界是一个 collection 的一个 batch，不能扩展为跨集合、跨模型或分布式事务。

收到网络超时只表明调用方尚未得到确定结果。先保留 requestId、原操作和错误，再按有限 journal 合同确认；不要自动把 `$inc`、`$mul` 作为新业务请求重发。

## 6. 核查依据与支持范围

本文于 2026-10-15 对照本地提交 `dcadffc7137f5653ec46c55a4171cf5f2d83de03` 的 SDK、Core 与对应测试审阅。文中专题和示例使用下列已公开固定提交链接；发布前逐文件核对哈希，不用浮动 main 替代依据：

- [Document Store 能力矩阵](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/document-store.md)
- [Document Quickstart 源码](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/samples/SonnetDB.DocumentQuickstart/Program.cs)
- [Mixed Bulk 实现](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Core/Documents/DocumentCollectionStore.Bulk.cs)
- [Bulk 合同测试](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/tests/SonnetDB.Core.Tests/Documents/DocumentBulkWriteTests.cs)

当次查询 GitHub 正式 Release 与 NuGet Core 页面显示 4.0.0；页面版本本身不能证明每个 SDK 方法都已在读者安装的包和 Server 组合上验证。复现优先使用匹配提交的仓库示例，再核自己的包版本。Document 总体仍为 `partial`，Graph 为 Beta。本文未运行产品构建、数据库示例、故障恢复或固定硬件性能测试，也不提供真实模型效果和长期生产结论。
