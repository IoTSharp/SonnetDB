---
layout: default
title: "RAG 摄取 Core"
description: "使用 SonnetDB Core 生成稳定文本分块，持久化摄取并恢复 Document、FullText 与 Vector generation。"
permalink: /rag-ingestion-core/
---

# RAG 摄取 Core

本页说明 M35 #302 的 Core SDK。`RagTextChunker`、planner 和 callback executor 提供稳定分块、清单校验与完整快照 diff；`RagIngestionWriter` 进一步保存摄取任务，通过调用方提供的 embedding 函数构建 Document、FullText 和 Vector 索引，支持有限重试、取消和重开续跑。只有完整 generation 才会原子发布。

本切片仅支持 Text/Document 文本分块。原始文件扫描和媒体提取、自动 provider 集成与 Copilot 可回滚迁移尚未交付；离线预计算向量的 CLI 入口见 [RAG CLI](rag-cli.md)。

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

callback 必须按 `ContentId` 和稳定派生 ID 实现幂等 upsert/delete；需要逐项 durable checkpoint 时，也必须由 callback 在该幂等边界内持久化。并发执行开始后，异常或取消可能发生在其他动作已经完成之后；executor 会传播失败，但不会返回部分执行结果，也不会伪造跨 Document/FullText/Vector 的自动回滚。`CompletedActions` 只在全部动作成功后返回，此时等于 `TotalActions`。需要进程重启续跑时，可使用下文的 `RagIngestionWriter`，或由 callback 自行持久化进度，不能把内存执行结果当作 durable checkpoint。

## 5. 预算、JSON 与发布边界

- planning 的 `MaxManifests` 分别约束前后快照，chunk、segment、embedding 和文本预算则合并累计两份快照；execution 对 `Update` 同时计入 `Previous` 与 `Current`，文本预算包含 manifest、chunk 和 segment 文本。所有预算均在 callback 前校验。
- 取消会贯穿快照冻结、嵌套清单校验、hash、diff 和 callback 调度。
- Core 内部已为 `RagTextSnapshot`、`RagIngestionSnapshot`、`RagIngestionPlan` 及其引用合同注册 source-generated JSON metadata；该 context 不属于 public API。外部应用必须在自己的 `JsonSerializerContext` 中注册实际使用的公开合同，并调用生成的 `JsonTypeInfo<T>` 重载。
- 本轮 win-x64 Native AOT 可达探针已实际执行 chunk、plan、executor 和 source-generated JSON 路径；这只证明本机构建兼容，不是生产摄取 journey、固定硬件容量或长稳证据。

完成一次真实摄取时，应用层仍需明确选择原始内容的权威来源、删除范围、provider 审计和模型换代流程。planner 本身不提供下面的持久化生命周期合同。

## 6. 持久化 writer 与重开续跑

writer 使用完整快照语义：每次输入必须包含本 stream 期望保留的全部内容。省略旧内容表示删除。stream 应由同一 RAG writer 工作流独占，不要混入其他 generation 发布者。

```csharp
using SonnetDB.Engine;
using SonnetDB.Generations;
using SonnetDB.SemanticContent;

using Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath });
var profile = new EmbeddingProfile(
    "manual-text-v1", "configured-provider", "selected-model", "revision-1", 384,
    supportedModalities: [SemanticContentModality.Text, SemanticContentModality.Document]);
var writer = new RagIngestionWriter(database, "maintenance-manuals", profile);

RagIngestionWriteResult result = await writer.WriteAsync(
    new RagIngestionSnapshot([manifest]),
    (chunk, token) => GenerateConfiguredEmbeddingAsync(chunk.Text, token),
    cancellationToken);

// 进程重启后，使用相同 stream 与完整 profile 合同构造 writer。
// 从 KV 读取冻结任务，不要求原始文件或原快照仍在内存。
RagIngestionWriteResult? resumed = await writer.ResumeAsync(
    (chunk, token) => GenerateConfiguredEmbeddingAsync(chunk.Text, token),
    cancellationToken);

using DatabaseGenerationQueryLease lease = database.Generations.AcquireActive("maintenance-manuals");
string collectionName = lease.GetRequiredResource(
    RagIngestionWriter.ChunksResourceRole,
    DatabaseGenerationResourceKind.DocumentCollection).Name;
var collection = database.Documents.Open(collectionName);
var hits = collection.SearchFullText(
    collection.Schema.TryGetFullTextIndex(RagIngestionWriter.FullTextIndexName)!,
    "$.text", "pump", 10);
```

profile 的维度、度量、归一化、模型 revision、模态与外发策略是兼容合同。调用方的 embedding 函数负责实际执行和审计；示例中的默认策略为 LocalOnly，外部 provider 必须显式配置相应 `SemanticDataEgressPolicy`。向量维度和有限值会在写入前检查，声明 L2 时还要求平方范数距 1 不超过 0.001。同一个 profile ID 不能更改模型属性，模型换代需要新 ID，并会重新生成全部向量。

任务先写入现有 KV 并 checkpoint，然后在独立物理名称下构建 chunk collection。每个文档包含 `contentId`、`chunkId`、`profileId`、`text`、`source`、`section`、`ordinal` 与 `embedding`，后两类索引由 Document 层维护；没有新增第二套 WAL。完整快照与清单使用 source-generated JSON 保存。相同 chunk ID、文本和完整 profile 可以从前一个 active 版本复用向量；恢复时复用已经恢复的 chunk 数据，并重放索引维护。provider 调用本身不保证 exactly-once，崩溃丢失未提交结果时可能再次调用。

完成后，现有 generation manager 校验并 checkpoint 所有派生索引，再原子更新 active 指针。发布前的异常、取消或 provider 失败都保留旧 active；发布已成功但响应中断时，`ResumeAsync` 返回同一版本，不重复发布。未完成任务阻止新的 `WriteAsync`，直到续跑成功或调用 `DiscardPendingAsync` 显式放弃；后者只删除未发布 staging，不删除 active 或 retired generation。

新版本查询立即不再命中被删除内容。旧 generation 仍受查询租约保护，调用 `database.Generations.CleanupRetired(stream)` 后才物理删除不再被租用的 Document、FullText、Vector 和快照 KV。需要保留回滚窗口时可以延后清理；本切片没有提供 Copilot 切换或回滚命令。

默认每次最多 10,000 份清单、100,000 个 chunk、4,194,304 个 UTF-16 字符和 8 MiB checkpoint JSON，最长十分钟。只重试 provider 抛出的 `IOException`、`HttpRequestException` 与 `TimeoutException`，最多三次、间隔 200 ms；取消、错误向量和数据库写入错误直接传播。provider 必须遵守取消令牌。本版本采用单数据库实例内串行 writer，generation 发布还校验 expected revision。每次发布都会构建完整集合，因此 staging 空间与索引工作量仍随完整快照增长；这不是固定硬件吞吐或真实模型质量证据。
