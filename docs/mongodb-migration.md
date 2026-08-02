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

以下依赖应停止迁移并先重新设计：replica set/sharding、session 或跨 collection 事务、change stream、复杂 MQL/aggregation、locale collation、MongoDB 特有 BSON 类型/排序、hashed/geospatial index，或要求官方 Driver 零改造直连。multikey 与 wildcard subtree 已有 SonnetDB-native 语义，但仍必须用 EXPLAIN 和结构化差异清单验证。

## 迁移步骤

1. 盘点 collection、文档数/大小、`_id` 类型、索引、validator、TTL、查询样本和峰值写入。
2. 对照 [Document Store 能力矩阵](document-store.md)，给每个不支持项登记替代方案或 `gap_reason`。
3. 将 `_id` 规范化为稳定字符串。日期建议导出为 ISO-8601 UTC 字符串或 Unix 毫秒，并对 TTL 字段单独验收。
4. 对含 Decimal128、code/scope 或少用 BSON 类型的数据优先导出 canonical Extended JSON；常见 BSON 类型也可直接读取 mongodump 的 `<collection>.bson`。
5. 先执行 CLI dry-run 并保存 JSON report。dry-run 保证零写入且不打开目标，因此不会验证目标 validator、unique index 或权限，这些检查必须在真实小批次中完成。
6. 检查 report 中的 `errorCount`、有界 per-item error 样本、gap 和 indexSuggestions；索引建议只供评审，不会自动修改 schema。
7. 分批导入。unordered 默认提交同批有效项；ordered 任一错误使该批零变更并停止后续批次。每批是一个 collection 内原子边界，不是跨批事务。
8. 网络失败后原命令可安全重试：CLI 由 source hash、target、collection、选项、batch 序号和 payload 派生稳定 `requestId`。长迁移可使用经过 source/target/options 校验的 checkpoint。
9. 先创建或人工转换 validator 和索引；不要假设 MongoDB index option 会自动转换。
10. 用业务查询样本和 `tests/SonnetDB.Parity` 的 Document 套件验证 CRUD、查询、更新、unique/TTL、aggregation、并发写和恢复后一致性。
11. 在目标硬件运行 `tests/SonnetDB.DocumentSoak`，达到实际文档规模后再切流。
12. 切换前创建 SonnetDB 一致性备份；切换后保留源 MongoDB 只读窗口和可执行回退方案。

## 可执行导入

mongodump 目录 dry-run，不连接或写入目标：

```powershell
sndb document import `
  --input ./dump/app `
  --collection devices `
  --dry-run `
  --report ./reports/devices-dry-run.json
```

通过已保存的嵌入式或远程 profile 导入 NDJSON；`replace` 使用 replace/upsert，适合可重复迁移：

```powershell
sndb document import `
  --input ./devices.ndjson `
  --collection devices `
  --profile production `
  --mode replace `
  --batch-size 500 `
  --checkpoint ./reports/devices.checkpoint.json `
  --report ./reports/devices-import.json
```

进程中断后使用完全相同的 source、target、collection、mode、ordered 和 batch-size：

```powershell
sndb document import `
  --input ./devices.ndjson `
  --collection devices `
  --profile production `
  --mode replace `
  --batch-size 500 `
  --checkpoint ./reports/devices.checkpoint.json `
  --resume `
  --report ./reports/devices-resume.json
```

也可用 `--path ./data`、`--connection "<conn>"` 或 `--url/--database/--token` 指定目标。`--json` 把机器报告写到 stdout。报告以 `errorCount` 记录累计错误，`errors` 最多保留 1000 条样本，`errorsTruncated` 表示明细是否截断；checkpoint 使用相同边界并在 resume 时保留累计失败状态。单批最多 1000 项，并受约 12 MiB CLI 安全预算和 Core 16 MiB canonical payload 上限共同约束。

支持的直接输入是 JSON array、JSONL/NDJSON、mongodump 目录或连接 BSON 文件。JSON array 会逐项流式读取；大规模导出仍优先使用 NDJSON 或 BSON，以便单文档隔离错误。ObjectId 转成小写 24 位字符串；常用 Extended JSON number/date 转成 JSON 数值或 ISO-8601 字符串；binary/regex/timestamp wrapper 会保留并在 report 标记 `partial`。不支持的 BSON 类型报告 `unsupported_bson_type`，损坏或截断输入报告 `invalid_bson`，两者都不会被静默改写。

## .NET API 映射

| MongoDB Driver 心智 | SonnetDB 入口 | 备注 |
|---|---|---|
| `IMongoCollection.InsertOneAsync` | `SndbDocumentClient.InsertOneAsync` | 调用方提供字符串 ID |
| `InsertManyAsync` / `BulkWriteAsync` | `InsertManyAsync` / mixed `BulkWriteAsync` | mixed Bulk 返回逐项状态、批次提交状态和 replay 标志 |
| `Find(...).Project().Sort()` | `FindAsync(SndbDocumentFindOptions)` / `FindCursor` | 类型化 builder 生成 SonnetDB DTO；cursor 可逐页枚举 |
| `UpdateOne/UpdateMany/findOneAndUpdate` | 对应 SDK 方法 | before/after 与 upsert 使用单文档原子提交 |
| `DeleteOne/DeleteMany` | `DeleteOneAsync` / `DeleteManyAsync` | bulk delete 可选 ordered |
| `CountDocuments` | `CountAsync` | 当前过滤计数能力以 API 参考为准 |
| `Distinct` | `DistinctAsync` | JSON path 作为字段入口 |
| `Aggregate` | `AggregateAsync` | 只接受 SonnetDB 支持的 stage 子集 |
| `CreateIndex` | SonnetDB HTTP / SQL / Studio 索引管理 | 需要显式转换 path/wildcard、unique/sparse/partial/TTL；CLI 只建议不自动创建 |
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

完整能力状态与证据见 [`document-mongodb-gap.json`](document-mongodb-gap.json)。迁移 report 的 gap 是针对本次输入；该 catalog 是产品范围的权威清单，两者不能互相替代。

## 回退与验收

迁移期不要双写后直接假设一致。至少比较：文档总数、ID 集合、抽样文档 canonical JSON、unique/TTL 行为、关键查询排序、aggregation 数值、恢复后索引一致性。任何不可比较项必须写入迁移记录，不能用“MongoDB-like”掩盖差异。

回退以源 MongoDB 只读快照和切换窗口内的增量记录为边界。SonnetDB 备份用于恢复 SonnetDB 自身，不可直接还原为 MongoDB 数据目录。
