---
layout: default
title: MongoDB-like 迁移指南
description: 从 MongoDB 文档负载迁移到 SonnetDB Document Store 的边界、步骤和 API 映射。
permalink: /mongodb-migration/
---

# MongoDB-like 迁移指南

本指南适用于希望把单节点、常用 JSON 文档负载迁入 SonnetDB 的应用。它不是原地替换指南：SonnetDB 不支持 MongoDB wire protocol，应用必须从 MongoDB Driver 改用 `SndbDocumentClient`、SonnetDB HTTP API 或 SQL。

## 迁移前判断

适合优先试迁的负载：单节点、字符串主键、常用 CRUD/过滤/投影/排序、有限 aggregation、可接受离线 JSONL 导入，并希望与 SonnetDB 的时序、KV、全文、向量或对象能力共用一个本地数据库目录。

以下依赖应停止迁移并先重新设计：replica set/sharding、多文档事务、change stream、复杂 MQL/aggregation、MongoDB 特有 BSON 类型/排序、multikey/geospatial/wildcard index，或要求官方 Driver 零改造直连。

## 迁移步骤

1. 盘点 collection、文档数/大小、`_id` 类型、索引、validator、TTL、查询样本和峰值写入。
2. 对照 [Document Store 能力矩阵](document-store.md)，给每个不支持项登记替代方案或 `gap_reason`。
3. 将 `_id` 规范化为稳定字符串。日期建议导出为 ISO-8601 UTC 字符串或 Unix 毫秒，并对 TTL 字段单独验收。
4. 用 MongoDB 工具导出 canonical/relaxed JSONL，再通过 Studio Document Explorer 的 JSONL/NDJSON dry-run 检查 ID 映射、重复键、validator 和文档大小。
5. 先创建 collection，再迁移 validator 和索引声明；不要假设 MongoDB index option 会自动转换。
6. 分批导入并保存 per-item error。ordered 用于必须整批拒绝的批次，unordered 用于允许有效项先提交的导入。
7. 用业务查询样本和 `tests/SonnetDB.Parity` 的 Document 套件验证 CRUD、查询、更新、unique/TTL、aggregation、并发写和恢复后一致性。
8. 在目标硬件运行 `tests/SonnetDB.DocumentSoak`，达到实际文档规模后再切流。
9. 切换前创建 SonnetDB 一致性备份；切换后保留源 MongoDB 只读窗口和可执行回退方案。

## .NET API 映射

| MongoDB Driver 心智 | SonnetDB 入口 | 备注 |
|---|---|---|
| `IMongoCollection.InsertOneAsync` | `SndbDocumentClient.InsertOneAsync` | 调用方提供字符串 ID |
| `InsertManyAsync` | `InsertManyAsync(..., ordered)` | 返回稳定 per-item error code |
| `Find(...).Project().Sort()` | `FindAsync(SndbDocumentFindOptions)` / `FindCursor` | 类型化 builder 生成 SonnetDB DTO；cursor 可逐页枚举 |
| `UpdateOne/UpdateMany` | `UpdateOneAsync` / `UpdateManyAsync` | 支持的 operator 见能力矩阵 |
| `DeleteOne/DeleteMany` | `DeleteOneAsync` / `DeleteManyAsync` | bulk delete 可选 ordered |
| `CountDocuments` | `CountAsync` | 当前过滤计数能力以 API 参考为准 |
| `Distinct` | `DistinctAsync` | JSON path 作为字段入口 |
| `Aggregate` | `AggregateAsync` | 只接受 SonnetDB 支持的 stage 子集 |
| `CreateIndex` | SonnetDB SQL / Studio 索引管理 | 需要显式转换 paths、unique/sparse/partial/TTL |
| collection validator | `SetValidatorAsync` / Studio | 使用 SonnetDB validator DTO |
| change stream | SonnetDB change feed | token 与保留语义不同，不能复用 resume token |

最小示例：

```csharp
using SonnetDB.Data.Documents;

using var documents = new SndbDocumentClient("Data Source=./data");
await documents.CreateCollectionAsync("devices");
await documents.InsertOneAsync(
    "devices",
    "device-001",
    """{"site":"east","status":"online","score":42}""");

var filter = new SndbDocumentFilterBuilder()
    .Equal("$.site", "east")
    .GreaterThanOrEqual("$.score", 10)
    .Build();
var projection = new SndbDocumentProjectionBuilder()
    .Include("_id")
    .Include("$.status", "status")
    .Build();
var sort = new SndbDocumentSortBuilder()
    .Descending("$.score")
    .Build();

var cursor = documents.FindCursor(
    "devices",
    new SndbDocumentFindOptions(
        Filter: filter,
        Projection: projection,
        Sort: sort,
        Limit: 100));

await foreach (var document in cursor.ReadAllAsync())
    Console.WriteLine(document.Json);
```

builder 只减少 SonnetDB DTO 的字符串操作符与 `JsonElement` 样板，不接受 MongoDB FilterDefinition。cursor 也不是 MongoDB server cursor：token 只能交回原集合和同一查询形状；收到 `document_cursor_invalid/mismatch/expired/stale` 时必须重新发起查询并按业务规则处理可能重复读取。

完整 DTO 和端点说明见 [SQL 参考的 Document API 章节](sql-reference.md#document-api)。

## 回退与验收

迁移期不要双写后直接假设一致。至少比较：文档总数、ID 集合、抽样文档 canonical JSON、unique/TTL 行为、关键查询排序、aggregation 数值、恢复后索引一致性。任何不可比较项必须写入迁移记录，不能用“MongoDB-like”掩盖差异。

回退以源 MongoDB 只读快照和切换窗口内的增量记录为边界。SonnetDB 备份用于恢复 SonnetDB 自身，不可直接还原为 MongoDB 数据目录。
