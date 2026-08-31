---
layout: default
title: "RAG 摄取 Core"
description: "使用 SonnetDB Core 生成确定性文本分块、完整快照增量计划，并以有界 callback 应用计划。"
permalink: /rag-ingestion-core/
---

# RAG 摄取 Core

本页说明 M35 #302 已交付的 Core building blocks。它负责确定性 hash/分块、内容清单校验、完整快照 diff 和有界计划执行；它**不读取文件、不调用 embedding provider、不自动写入 Document/FullText/Vector，也不在失败后自动回滚**。持久化 writer、重试/续跑、实际删除同步、CLI 和 Copilot 迁移仍由后续切片完成。

## 1. 生成稳定文本分块

调用方先为内容选择稳定 `contentId`。同一 `contentId`、原始文本和分块选项会产生相同的内容 hash、边界与 chunk ID：

```csharp
using SonnetDB.SemanticContent;

string contentId = "manuals/pump-1001";
string text = await File.ReadAllTextAsync(path, cancellationToken);

RagTextSnapshot chunked = RagTextChunker.Chunk(
    contentId,
    text,
    new RagTextChunkingOptions
    {
        MaxCharacters = 800,
        OverlapCharacters = 100,
        MaxInputCharacters = 4 * 1024 * 1024,
        MaxChunks = 10_000,
    },
    cancellationToken);
```

`ContentHash` 是原始文本精确 UTF-8 字节的 SHA-256。chunk offset 使用原始 .NET 字符串的 UTF-16 位置；分块不会切开 surrogate pair，并保证 overlap 后持续前进。`contentId` 或正文含不成对 surrogate 时会失败，不会用替换字符生成不稳定标识。

## 2. 构建内容清单

把分块和原始对象引用放入现有 Semantic Content 合同。对象、媒体提取结果、embedding profile 和实际派生索引仍由调用方管理：

```csharp
var now = DateTimeOffset.UtcNow;
var manifest = new SemanticContentManifest(
    chunked.ContentId,
    new SemanticObjectReference("manuals", "pump-1001.md", versionId: "rev-42"),
    chunked.ContentHash,
    "text/markdown",
    SemanticContentModality.Document,
    System.Text.Encoding.UTF8.GetByteCount(text),
    source: "maintenance-manuals")
{
    Text = text,
    Chunks = chunked.Chunks,
    CreatedUtc = now,
    UpdatedUtc = now,
};
```

`SemanticContentValidator` 会校验 ID、hash、offset、对象引用、chunk/segment/embedding 绑定和 profile 兼容性。planner/executor 还会冻结调用方提供的可变列表，后续修改不会改变已生成或正在应用的计划。

## 3. 比较完整快照

`RagIngestionSnapshot` 表示一次完整期望状态，不是变化补丁。当前快照省略旧内容会生成 `Delete`：

```csharp
RagIngestionSnapshot previous = LoadPreviousSnapshot();
var current = new RagIngestionSnapshot([manifest]);

RagIngestionPlan plan = RagIngestionPlanner.CreatePlan(
    previous,
    current,
    new RagIngestionPlanningOptions
    {
        MaxManifests = 100_000,
        MaxActions = 100_000,
        MaxTotalChunks = 1_000_000,
        MaxTotalSegments = 1_000_000,
        MaxTotalEmbeddings = 1_000_000,
        MaxTotalTextCharacters = 128L * 1024 * 1024,
    },
    cancellationToken);
```

动作类型为 `Add`、`Update` 或 `Delete`：planner 先按当前快照的内容 ID 排序 `Add`/`Update`，再追加按旧快照内容 ID 排序的 `Delete`。索引运行状态和时间戳不会单独触发内容更新，避免恢复或重试制造无效重写。只有完整快照成功应用后，调用方才能把它保存为下一轮 `previous`。

## 4. 应用计划

executor 在调用第一个 callback 前完成整份计划的结构、重复 ID、清单和资源预算校验，然后按有界并发调用应用函数：

```csharp
RagIngestionExecutionResult result = await RagIngestionExecutor.ExecuteAsync(
    plan,
    async (action, token) =>
    {
        switch (action.Kind)
        {
            case RagIngestionActionKind.Add:
            case RagIngestionActionKind.Update:
                await UpsertDerivedIndexesAsync(action.Current!, token);
                break;
            case RagIngestionActionKind.Delete:
                await DeleteDerivedIndexesAsync(action.ContentId, token);
                break;
        }
    },
    new RagIngestionExecutionOptions
    {
        MaxConcurrency = 4,
        MaxActions = 100_000,
        MaxTotalChunks = 1_000_000,
        MaxTotalSegments = 1_000_000,
        MaxTotalEmbeddings = 1_000_000,
        MaxTotalTextCharacters = 128L * 1024 * 1024,
    },
    cancellationToken);
```

callback 必须按 `ContentId` 和稳定派生 ID 实现幂等 upsert/delete；需要逐项 durable checkpoint 时，也必须由 callback 在该幂等边界内持久化。并发执行开始后，异常或取消可能发生在其他动作已经完成之后；executor 会传播失败，但不会返回部分执行结果，也不会伪造跨 Document/FullText/Vector 的自动回滚。`CompletedActions` 只在全部动作成功后返回，此时等于 `TotalActions`。需要进程重启续跑时，调用方必须读取自己持久化的进度，或重新读取权威来源并生成完整快照，不能把内存执行结果当作 durable checkpoint。

## 5. 预算、JSON 与发布边界

- planning 的 `MaxManifests` 分别约束前后快照，chunk、segment、embedding 和文本预算则合并累计两份快照；execution 对 `Update` 同时计入 `Previous` 与 `Current`，文本预算包含 manifest、chunk 和 segment 文本。所有预算均在 callback 前校验。
- 取消会贯穿快照冻结、嵌套清单校验、hash、diff 和 callback 调度。
- Core 内部已为 `RagTextSnapshot`、`RagIngestionSnapshot`、`RagIngestionPlan` 及其引用合同注册 source-generated JSON metadata；该 context 不属于 public API。外部应用必须在自己的 `JsonSerializerContext` 中注册实际使用的公开合同，并调用生成的 `JsonTypeInfo<T>` 重载。
- 本轮 win-x64 Native AOT 可达探针已实际执行 chunk、plan、executor 和 source-generated JSON 路径；这只证明本机构建兼容，不是生产摄取 journey、固定硬件容量或长稳证据。

完成一次真实摄取时，应用层仍需明确选择原始内容的权威来源、Document/FullText/Vector 写入顺序、删除范围、持久化 retry/resume 策略、provider 审计和模型换代流程。不要用本 Core planner 代替这些生命周期合同。
