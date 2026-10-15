---
title: SonnetDB 文档索引与 Validator：复合、Multikey、TTL 和重建维护
categories: SonnetDB
tags: dotnet, JSON, Document, Index, Validator
draft: false
---

# SonnetDB 文档索引与 Validator：复合、Multikey、TTL 和重建维护

SonnetDB 的 Document Store 可以为 JSON 路径建立复合、Multikey、Wildcard 和 TTL 索引，并用 Validator 约束写入。本文从一个设备档案集合出发，说明怎样声明规则、查看实际查询路径、检查索引一致性，以及安排有明确边界的重建维护。

![SonnetDB 文档索引与 Validator：复合、Multikey、TTL 和重建维护](https://img2024.cnblogs.com/blog/1725297/202610/1725297-20261015110854675-1379303334.png)

图：AI 主题示意图，并非实测截图；索引类型与维护时钟用于解释能力，不表示立即过期、无锁重建或容量验收。

## 1. 先在独立测试目录声明集合

下面使用嵌入式 Core API，不需要 HTTP 管理权限配置。准备 .NET 10 控制台项目，在 SonnetDB 仓库旁创建该项目后，将项目引用指向当前仓库：

```powershell
dotnet new console --framework net10.0 -o ../document-index-demo
dotnet add ../document-index-demo/document-index-demo.csproj reference ./src/SonnetDB.Core/SonnetDB.Core.csproj
```

把下列代码放入该示例的 `Program.cs`，然后运行 `dotnet run --project ../document-index-demo`。目录应为新建的实验目录；示例不是对现有业务库执行的迁移脚本。

```csharp
using SonnetDB.Documents;
using SonnetDB.Engine;

using var db = Tsdb.Open(new TsdbOptions
{
    RootDirectory = "./document-index-demo-data"
});

db.Documents.Create(DocumentCollectionSchema.Create(
    "devices",
    indexes:
    [
        new DocumentPathIndexDefinition("ix_site_serial", ["$.site", "$.serial"]),
        new DocumentPathIndexDefinition("ix_tags", "$.tags"),
        new DocumentPathIndexDefinition(
            "ix_metadata", "$.metadata", Kind: DocumentIndexKind.Wildcard)
    ],
    validator: new DocumentValidatorDefinition(
    [
        new DocumentValidatorRuleDefinition(
            "$.site", Required: true,
            Types: [DocumentValidatorValueType.String]),
        new DocumentValidatorRuleDefinition(
            "$.score", Types: [DocumentValidatorValueType.Number],
            Minimum: 0, Maximum: 100)
    ])));

var store = db.Documents.Open("devices");
store.Upsert("device-001",
    """{"site":"east","serial":"P-001","score":42,"tags":["pump","pump","critical"],"metadata":{"line":"A"}}""");

var query = new DocumentQuery(
    Filter: new DocumentFieldFilter(
        DocumentFieldRef.JsonPath("$.tags"),
        DocumentFilterOperator.Equal, "pump"),
    Limit: 10);
var result = DocumentQueryPlanner.Execute(store, store.Schema, query);
Console.WriteLine($"{result.AccessPath} / {result.IndexName}");
foreach (var row in result.Items)
    Console.WriteLine(row.Id);
Console.WriteLine($"consistent={store.VerifyIndexConsistency().IsConsistent}");
```

按当前源码和测试合同，`$.tags == "pump"` 可以命中 `ix_tags` 的文档索引，输出路径应为 `document_index`，设备 ID 为 `device-001`。同一文档数组里的两个 pump 不应让该文档重复返回。实际输出与预期不同，应先记录查询、schema 和版本，再检查 planner，不能只看“建索引没抛异常”。本文没有执行上述程序，预期值来自源码与现有合同测试。

## 2. 各种索引的语义不能混用

| 类型 | 适用任务 | 当前边界 |
|---|---|---|
| 单字段/复合 path | 固定字段过滤、多路径声明 | 是否利用索引由具体谓词和 planner 决定 |
| Multikey | 数组成员或数组对象字段匹配 | 同文档重复值去重；compound parallel arrays 拒绝 |
| Wildcard subtree | 预先不知道全部叶子字段的 metadata | 不支持 unique/TTL；显式数组下标等谓词可能回退扫描 |
| unique | 在索引覆盖范围内约束键冲突 | 跨文档相同键会冲突；不是替代 Validator 的字段校验 |
| sparse / partial | 有条件地纳入文档 | 必须检查覆盖范围，不能凭唯一索引名推断全集合唯一 |
| TTL | 依照文档时间字段删除过期数据 | 时间单位、触发时机和清理代价需要明确 |

给复合索引的两个路径分别放数组，例如 `left:[1,2]` 与 `right:[3,4]`，当前实现会稳定拒绝 parallel arrays，而不会悄悄构建笛卡尔积。测试也区分数字 1、字符串 "1"、布尔 true 与字符串 "true"，应用不要把它们预先都转成字符串再推断索引相等。

Wildcard 的示例路径是 `$.metadata`，查询其下 `$.metadata.line` 时可选择对应派生条目；同时存在更具体的 path 索引时 planner 可优先选择专用索引。`$.metadata.sensors[0].kind` 这样的显式数组下标不保证命中 Wildcard，应该检查 `DocumentQueryPlanner.Explain(...)` 的 `GapReason` 与实际 `AccessPath`。

## 3. Validator 约束新写入，不替代存量审计

示例要求 site 存在且为非 null 字符串，score 若存在需为 0–100 的数字。当前还支持 enum 与 pattern；这套规则使用 SonnetDB DTO，并不是完整 MongoDB JSON Schema。

默认 `DocumentValidationAction.Error` 会拒绝不合规写入；`Warn` 允许写入，同时在写结果中返回 warning 级 `validation_failed`。如果需要观察历史应用会产生哪些坏数据，可先在测试库显式设置 Warn，但必须采集结果中的警告，不能把“请求返回了”当作全部通过。

现有集合可通过 `db.Documents.SetValidator("devices", definition)` 设置或替换规则。该入口持久化 schema，并不等于已经扫描、修复或认证全部存量文档。上线前应单独遍历存量记录核对规则，再处理缺失字段、类型错误和范围超限。

## 4. TTL 的字段和值应怎样准备

下面在现有集合上增加 TTL path index。`createdAt` 示例使用 Unix **毫秒**，TTL 保留秒数则使用 `TtlSeconds`：

```csharp
db.Documents.CreateIndex("devices",
    new DocumentPathIndexDefinition(
        "ix_created_ttl", "$.createdAt",
        TtlPath: "$.createdAt", TtlSeconds: 3600));
```

Core 用“字段时间戳 + TTL 秒数 × 1000 ≤ 当前 UTC 毫秒”判断过期。也能读取可解析的时间字符串，但为减少歧义，应用最好统一采用一种格式。缺少路径或无法解析的值不会由这项 TTL 规则判为过期，建议通过 Validator 和写入约定保证时间字段质量。

当前集合的多种读写入口会调用 `PurgeExpiredDocumentsLocked`，在锁内扫描并提交过期文档的删除。因此 TTL 不代表精确定时器，也不是只给查询结果加个隐藏条件；访问延迟可能承担清理工作。不要用 `Limit:10` 推断 TTL 扫描也只检查 10 个文档，更不能据此承诺大集合的尾延迟。

在独立测试目录放入一条远早于保留窗口的记录和一条未来时间记录，触发读取后核对旧文档消失、新文档仍在，再运行 `VerifyIndexConsistency()`。这是验证步骤建议，不是本文取得的现场验收结果。

## 5. 重建前后保留什么证据

path index 的显式维护入口为：

```csharp
var before = store.VerifyIndexConsistency();
var rebuilt = db.Documents.RebuildIndex("devices", "ix_tags");
var after = store.VerifyIndexConsistency();
Console.WriteLine($"{rebuilt.Name}: {before.IsConsistent} -> {after.IsConsistent}");
```

该方法从主文档应用 schema 并重建 path index，是同步调用，不能承诺仅重写某个索引的最小物理范围或全程无锁。安排维护窗口，记录库目录、源码/包版本、schema、开始结束时间、前后报告和相同查询结果，才能判断本次维护是否完成。

仓库另有“在线关系表索引”文档，其中 `CREATE INDEX ... ONLINE` 的 pending/complete、分批 WAL 进度与续建合同仅适用于声明支持的关系表索引；它明确不支持文档集合。文档 path 重建、全文重建和向量图后台重建也各有入口，不能因为它们都叫 rebuild 就互换完成标准。

索引一致性通过只证明该检查涵盖的主数据与索引条目关系；它不证明整机断电恢复、备份可用或百万文档容量。主文档及 path index/change feed 的同批 WAL 与派生全文/向量的修复标记，也需分别按对应恢复测试确认。

## 6. 版本与支持边界

本文于 2026-10-15 对照本地提交 `dcadffc7137f5653ec46c55a4171cf5f2d83de03` 审阅。下列专题源码与公开固定提交逐文件核对，便于复查 API、拒绝条件及测试预期：

- [Document Store 能力矩阵](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/document-store.md)
- [集合索引与 Validator 管理入口](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Core/Documents/DocumentCollectionManager.cs)
- [Multikey/Wildcard 合同测试](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/tests/SonnetDB.Core.Tests/Documents/DocumentAtomicMultikeyWildcardTests.cs)
- [TTL 与索引一致性测试](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/tests/SonnetDB.Core.Tests/Documents/DocumentIndexConsistencyTests.cs)

当次 GitHub 正式 Release 和 NuGet Core 页面显示 4.0.0，仍需核对读者实际安装包与源码 API。Document 总体为 `partial`，Graph 为 Beta；不支持 MongoDB wire protocol、官方 Driver 直连、replica set 或 sharding。百万/千万文档 profile 不等于目标硬件已经通过容量、恢复或长稳门禁。本文未执行产品构建、代码示例、性能、故障恢复或真实模型测试。
