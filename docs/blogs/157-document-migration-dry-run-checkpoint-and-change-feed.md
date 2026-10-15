---
title: SonnetDB MongoDB-like 迁移实操：Dry-run、Checkpoint 与 Change Feed
categories: SonnetDB
tags: Document, Migration, Checkpoint, dotnet
draft: false
---

# SonnetDB MongoDB-like 迁移实操：Dry-run、Checkpoint 与 Change Feed

SonnetDB 提供文档导入 CLI、可校验的迁移 checkpoint 和集合 Change Feed。本文用两条设备档案演示如何先检查输入，再分批导入和续跑，并说明为什么这些能力不能直接等同于 MongoDB 协议兼容、在线复制或业务自动切流。

![SonnetDB 文档迁移：Dry-run、Checkpoint 与 Change Feed](https://img2024.cnblogs.com/blog/1725297/202610/1725297-20261015130730770-1992194988.png)

图：AI 主题示意图，并非实测截图；导入 checkpoint 与集合变更序号是不同的恢复位置，不表示跨产品自动同步。

## 1. 先确认迁移的边界

SonnetDB 的 Document Store 适合单节点 JSON 文档负载，但不实现 MongoDB wire protocol。应用需要改用 `SndbDocumentClient`、SonnetDB HTTP API 或 SQL；不能只改 MongoDB URI。

依赖 replica set、sharding、MongoDB session、跨 collection 事务、完整 MQL 或 change stream 的应用，应先列出替代方案。SonnetDB Change Feed 的序号也不能接收 MongoDB resume token。

本文于 **2026-10-15** 核对本地源码提交 `dcadffc7137f5653ec46c55a4171cf5f2d83de03`；引用固定到与本专题一致的公开提交 `5424b66672e37f49a2a5633fdde0b54a9b1f0290`。当日正式 GitHub Release 与 NuGet Core 页面为 **4.0.0**；下面使用仓库源码 CLI，不能据此断言任意已安装 `sndb` 包都含相同入口。4.5/Workbench 预览各有范围，Graph 仍为 Beta。

## 2. 准备可复核的 NDJSON

在仓库根目录、可丢弃的测试工作目录中准备两个独立文档，每行一个 JSON。以下命令会创建 `migration-demo`，应先确认它是本次测试专用目录：

```powershell
$demo = Join-Path (Get-Location) 'migration-demo'
[IO.Directory]::CreateDirectory($demo) | Out-Null
$inputText = @'
{"_id":{"$oid":"64b64c2032f9a13f4c8e0001"},"site":"east","score":{"$numberLong":"42"}}
{"_id":"device-002","site":"west","score":9}
'@
[IO.File]::WriteAllText((Join-Path $demo 'devices.ndjson'),
    $inputText.Replace("`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))
```

常见 ObjectId 输入转为小写24位字符串；常用 Extended JSON 数值、日期按支持合同转换。不要把所有 BSON 类型都视为可无损搬运：少用类型、wrapper 或损坏输入应查看报告中的 gap/error，而不是直接忽略。

本文示例没有在本次发布任务执行，下面的结果均为源码合同预期，不是迁移完成回执。

## 3. Dry-run 只检查输入，不打开目标

准备 .NET 10 SDK 后，在仓库根目录执行源码 CLI：

```powershell
dotnet run --project src/SonnetDB.Cli -- document import `
  --input ./migration-demo/devices.ndjson `
  --collection devices `
  --path ./migration-demo/target `
  --dry-run `
  --report ./migration-demo/dry-run.json
```

对于这份正常输入，应核对报告中的 `dryRun=true`、`documentsValidated=2`、`documentsWritten=0`、`batchesAttempted=0`。还应看到 `dry_run_target_constraints_not_checked` 这一边界说明。

Dry-run 不创建目标客户端，不打开目标目录，所以它不能证明目标 unique index、validator、权限或远程连通性已通过。零输入错误仅是进入小批次试导入的条件。`indexSuggestions` 也是建议，不会自动创建索引。

需要审阅完整报告：`errorCount` 是累计错误数，`errors` 是有界样本；明细最多1000条，`errorsTruncated` 用来识别样本是否截断。不要把样本数组长度误当作总错误数。

## 4. 分批导入，并固定续跑身份

下面把目标限定为测试目录，并用每批一条让批次边界更容易观察：

```powershell
dotnet run --project src/SonnetDB.Cli -- document import `
  --input ./migration-demo/devices.ndjson `
  --collection devices `
  --path ./migration-demo/target `
  --mode replace `
  --unordered `
  --batch-size 1 `
  --checkpoint ./migration-demo/devices.checkpoint.json `
  --report ./migration-demo/import.json
```

`replace` 使用 replace/upsert，稳定字符串 ID 负责定位文档。正常空目标下，两条输入应形成两批提交；最终应核对报告成功标志、实际写入项、错误及目标数据，而不是只看命令结束。

单批上限1000项；CLI 另设约12 MiB估算安全预算，Core canonical payload 上限为16 MiB。这些是不同层次的限制，不代表任意单文档都能导入。

发生中断后，用相同 source、target、collection、mode、ordered 和 batch-size 加上 `--resume`：

```powershell
dotnet run --project src/SonnetDB.Cli -- document import `
  --input ./migration-demo/devices.ndjson `
  --collection devices `
  --path ./migration-demo/target `
  --mode replace `
  --unordered `
  --batch-size 1 `
  --checkpoint ./migration-demo/devices.checkpoint.json `
  --resume `
  --report ./migration-demo/resume.json
```

checkpoint 会校验源哈希、目标身份和上述选项。源文件或参数改变时，应保留旧记录并作为新的迁移任务处理，不要修改 checkpoint 字段来强行继续。

CLI 为批次派生稳定 `requestId`；它依赖源哈希、目标、集合、操作选项、批次序号及 payload。批次明确提交后才保存下一位置，恢复时也会带回累计错误。已完整导入后再 resume，可能没有剩余输入；不能拿“本次写入0条”断言目标为空。

## 5. 错误、原子性和未知提交分别处理

| 情况 | 当前合同 | 应保留的检查 |
|---|---|---|
| unordered 批次含坏项 | 有效项可提交，逐项错误报告保留 | 提交状态、每项结果及累计错误 |
| ordered 批次失败 | 该批零文档变更，后续批次停止 | 此前批次可能已经提交 |
| 请求发送后网络中断 | 不能直接判断服务器未写入 | 原请求身份、原目标和 checkpoint |
| checkpoint 与输入不匹配 | 明确拒绝续跑 | 原源文件/选项，不能手改放行 |

原子边界是**一个 collection 的一个 batch**，不是整个文件或多个集合。批次幂等记录存在有限保留窗口，文档 Bulk 合同为24小时；长时间停机后必须重新核对，不能把它写成永久 exactly-once。

迁移切流还要比较 ID 集合、文档内容、关键查询排序、unique/TTL 行为以及索引恢复。一致性备份用于恢复 SonnetDB 自身，不能直接还原为 MongoDB 数据目录。

## 6. Change Feed 是另一个读取位置

CLI checkpoint 描述源文件读取到哪里；Change Feed 的 `Sequence` 描述 SonnetDB 集合中发生的变更。它们不能互换。

在已打开的本地 Core 集合上，可以读取一页变更。这里 `store` 指已通过 `db.Documents.Open("devices")` 得到的 `DocumentCollectionStore`：

```csharp
var page = store.ReadChangeFeed(0, 100);
foreach (var change in page.Changes)
{
    Console.WriteLine($"{change.Sequence}: {change.Operation} {change.DocumentId}");
    if (change.PayloadTruncated)
        Console.WriteLine("镜像被省略，不能按完整文档处理。");
}
Console.WriteLine($"resume={page.ResumeSequence}, more={page.HasMore}");
```

调用方应在本页业务处理被可靠确认后，再保存 `ResumeSequence`。有过滤条件时，不能仅取“最后一条匹配变更”的序号代替页响应位置；没有匹配项也可能已经扫描并推进位置。

`BeforeJson`/`AfterJson` 可因操作种类或镜像过大而为空，`PayloadTruncated` 用于区别省略。记录还可携带 user/bulk/ttl 来源和批次请求身份。应按实际保留窗口检查 `OldestAvailableSequence`，窗口缺口需要业务补偿，不能静默当作增量已经齐全。

这段是 Core 本地入口示意，不是声称 CLI 自动桥接 MongoDB change stream，也没有构造不存在的 SDK 订阅 API。正式在线迁移的双写、增量消费、幂等处理和回退窗口由应用单独治理。

## 7. 本次复核的证据边界

本篇复核了导入解析、报告/批次/checkpoint 实现、Change Feed 模型与既有测试源码；没有运行数据库导入、故障恢复或容量测试。没有新的性能、固定硬件、跨产品一致性或长期生产证据。

- [MongoDB-like 迁移指南](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/mongodb-migration.md)
- [Document Store 能力矩阵](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/document-store.md)
- [导入 CLI 源码](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Cli/DocumentImportCommandRunner.cs)
- [Change Feed 模型](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Core/Documents/DocumentChangeFeedModels.cs)
- [导入命令测试](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/tests/SonnetDB.Core.Tests/Cli/DocumentImportCommandTests.cs)
