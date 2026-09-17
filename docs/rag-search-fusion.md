# 持久 RAG 检索、融合与重排（M35 #303）

`RagGenerationSearch` 是可直接调用的 Core/嵌入式查询入口，读取 `RagIngestionWriter` 已发布的 generation。它在同一个查询租约中完成全文、精确向量候选、融合和可选重排。新版本发布或 retired 清理不会让一次查询混合不同版本。现有 SQL `hybrid_search` 和 legacy Copilot 的默认计分合同保持兼容；SQL 与新 reader 共用原有向量距离归一化实现。

本 PR 交付代码合同及受控 fixture 测试。真实模型的 Recall@K、nDCG、P50/P95、体积和重建评测仍归后置验证；没有声明模型质量或大规模 ANN 性能。

## 直接查询已发布的快照

以下代码放在引用 `SonnetDB.Core` 的 .NET 10 程序中执行。数据库目录中必须已经用 `RagIngestionWriter` 发布 `manuals` stream；`profile` 必须完整复制摄取时的配置，`queryVector` 必须由该模型对查询生成。

```csharp
using SonnetDB.Engine;
using SonnetDB.SemanticContent;

static async Task<IReadOnlyList<SemanticSearchHit>> SearchManualsAsync(
    Tsdb database, EmbeddingProfile profile, float[] queryVector,
    CancellationToken cancellationToken)
{
    var reader = new RagGenerationSearch(database, "manuals", profile);
    return await reader.SearchAsync(
        "pressure calibration",
        queryVector,
        new SemanticSearchFusionOptions
        {
            TopK = 5,
            MaxCandidatesPerSource = 100,
            MaxScannedDocuments = 10_000,
            MaxDuration = TimeSpan.FromSeconds(5),
            Mode = SemanticSearchFusionMode.ReciprocalRank,
        },
        cancellationToken: cancellationToken);
}
```

reader 核对快照中的完整 profile（包含 provider、model、revision、normalization、外发策略和模态），同时核对 generation 身份、向量索引维数/metric 和每个分块 profile。相同 profile ID 不能掩盖换模型；空语料也会检查。未发布 stream、错误维度、非有限向量和余弦零向量均拒绝。结果 `Id` 是 Document 中稳定分块 ID，`ContentId` 是父内容身份；不会用正文相同作为跨内容去重理由。

## 融合与确定性规则

`SemanticSearchFusion.FuseAsync` 也可供已有授权检索通道单独调用。最多十六路，每路输入分数越大越相关，不要求预先排序。

- 同一通道的重复 Id 取最高分，仅贡献一个名次；跨通道的 Id 融合，正文和元数据不一致则拒绝。
- RRF 使用 `weight / (rankConstant + rank)`，名次从一开始；默认常数 60。输入同分先按 Id 序数升序排列，结果同分也按 Id 序数升序排列。
- `NormalizedScore` 对各路执行 min-max 后加权求和，缺失通道贡献零。常量非空通道统一贡献一；先缩放再计算差值，避免有限极值相减溢出。NaN/Infinity 一律拒绝。
- 默认保留不同分块；`DeduplicateByContent` 可在融合后、重排窗口之前只保留父内容的最佳分块。
- reader 向量 RRF 使用负原始距离排名；归一化模式先复用 SQL 的 cosine/L2/inner-product 距离映射，再执行通道 min-max。全文复用现有 BM25 与 posting 预算 API。

## 重排与权限边界

`ISemanticSearchReranker` 默认不启用。宿主显式注入后，只收到前 `MaxRerankCandidates` 个已去重候选的只读快照。返回值仅允许 Id 与有限分数，必须完整覆盖该窗口，不得注入、重复或省略 Id。结果正文和来源始终来自原始候选，`FusionScore` 保留重排前分数，`Score` 是最终排序值。窗口外候选不会与重排分数混排。

嵌入式调用方负责确认整个 stream 的访问权限。需要按分块授权时，传入 `allowedDocumentIds`，reader 在向量计分、全文候选和重排前过滤；不得检索之后才裁剪模型输入。reader 不生成 embedding，也不自带远程 rerank provider。扩展若调用远端，宿主必须自行执行批准目标、外发与审计策略；本 hook 不构成外发授权。

## 有界执行与已知限制

所有调用创建 linked cancellation token，默认期限 30 秒、最多 5 分钟。默认每路 100 条、各路条目合计 1,600 条、正文与元数据 4 Mi UTF-16 字符、重排窗口 100 条。预算计算包括重复条目；自定义选项可提高到 API 明示上限，不能静默裁剪超预算输入。

reader 当前选择**有界精确扫描**：最多 10,000 个文档，逐条 keyset 读取，扫描 JSON 默认 16 Mi 字符，已发布快照默认 8 MiB，全文默认 100,000 posting 检查。超量、超时、取消或 hook 错误不返回部分结果。候选窗口只保留各通道前 N 条，因此融合召回范围受窗口约束。此实现适用于这些预算内的语料，未包装现有缺少取消预算的 Document ANN 为硬截止服务。

期限是协作取消：索引同步打开/锁等待、单次 KV 读取/JSON 解析、向量运算和不遵守 token 的宿主 hook 不能被强行终止；每个阶段之间检查期限。持久 generation 资源须遵守已有发布后不可变合同。查询持有租约直到 hook 完成或异常返回，届时自动释放。最坏驻留量受扫描文档、JSON/文本、候选与快照预算约束，但预算不是精确进程 RSS 上限。

## 验证

```text
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --filter "FullyQualifiedName~SemanticSearchFusionTests|FullyQualifiedName~RagGenerationSearchTests|FullyQualifiedName~RagIngestionWriterTests|FullyQualifiedName~SqlExecutorDocumentTests" --disable-build-servers -m:2 /nodeReuse:false /p:UseSharedCompilation=false
```

覆盖数学分数、并列排序、同路/跨路去重、极值和非有限值、重排窗口与注入拒绝、预算和取消；通过真实 writer、Document/FullText、重开和发布期间租约验证生产读取路径。fixture 向量只证明行为合同。

2026-09-17 本地目标验证：新增 26 个用例，连同既有 writer 与 SQL 文档查询回归共 92/92 通过；Core/CLI 编译包含现有 AOT/trim 分析，无编译警告。此记录不包含 NativeAOT 发布、真实 provider 或固定硬件质量评测。
