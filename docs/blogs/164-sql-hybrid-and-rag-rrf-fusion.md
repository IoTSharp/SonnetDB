---
title: SonnetDB 融合检索实操：SQL 加权分数与 RAG RRF 的区别
categories: SonnetDB
tags: 混合检索,RRF,RAG
draft: false
---

# SonnetDB 融合检索实操：SQL 加权分数与 RAG RRF 的区别

SonnetDB 同时提供 SQL `hybrid_search` 和 Core 的 RAG 融合接口。本文从两路候选的最小 C# 示例开始，说明加权分数与倒数排名融合的差别，再连接持久 generation、权限过滤和重排边界，让读者能解释一次排名来自哪里。

![SonnetDB SQL 加权分数与 RAG RRF 融合](https://img2024.cnblogs.com/blog/1725297/202610/1725297-20261017120312321-1068781698.png)

图：AI 主题示意图，并非实测截图；展示召回和排序关系，不代表真实模型质量。

## 先核对入口，避免把同一名字理解成同一算法

本文于 2026-10-17 对照源码提交 `dcadffc7137f5653ec46c55a4171cf5f2d83de03` 和专题文档复核；公开参考固定到 `5424b66672e37f49a2a5633fdde0b54a9b1f0290`，相关源码内容已在本次发稿前核对。正式 Release/NuGet Core 当前核到 4.0.0；新接口是否包含在读者安装的包中，必须按实际版本确认，不能用源码存在代替发包证明。

此前[SQL Hybrid Search 专题](https://www.cnblogs.com/IoTSharp/p/20524623)介绍了 BM25 与向量加权。本篇新增 Core 融合器、RRF 名次核算、generation 查询与重排权限流程，不重复把旧 SQL 示例当新功能。本轮未运行以下代码或数据库示例；人工分数仅用于核对算法，不是模型效果。

| 入口 | 当前合同 |
|---|---|
| SQL `hybrid_search(...)` | 文档全文 BM25 与向量评分的加权融合，暴露相关性伪列 |
| `SemanticSearchFusion.FuseAsync` | 融合宿主提供的已授权有限候选，可选 RRF 或分数归一化 |
| `RagGenerationSearch.SearchAsync` | 在同一已发布 generation 租约内读取分块、召回、融合并可选重排 |

选择 RRF 是在 Core 选项中设置融合模式，不是给现有 SQL 权重换个名字。

## 第一步：用两路候选核算 RRF

在 .NET 10 控制台项目中引用包含此接口的 SonnetDB.Core 源码项目或已核实版本的包，把以下代码放入 `Program.cs`：

```csharp
using SonnetDB.SemanticContent;

SemanticSearchCandidate A(double score) =>
    new("a", "manual-a", "校准步骤 A", score, "manual-a.md", "校准");
SemanticSearchCandidate B(double score) =>
    new("b", "manual-b", "校准步骤 B", score, "manual-b.md", "校准");

var sources = new SemanticSearchSource[]
{
    new(new SemanticSearchCandidate[] { A(9), B(7), A(8) }),
    new(new SemanticSearchCandidate[] { B(2), A(1) })
};

var hits = await SemanticSearchFusion.FuseAsync(
    "校准",
    sources,
    new SemanticSearchFusionOptions
    {
        TopK = 2,
        Mode = SemanticSearchFusionMode.ReciprocalRank,
        RankConstant = 60,
        MaxDuration = TimeSpan.FromSeconds(5)
    });

foreach (var hit in hits)
    Console.WriteLine($"{hit.Id}: fusion={hit.FusionScore:R}, source={hit.Source}");
```

同一路重复 ID 只保留最高分形成一个名次，因此第一路是 a、b，第二路是 b、a。名次从 1 开始，权重默认 1：

```text
a = 1 / (60 + 1) + 1 / (60 + 2)
b = 1 / (60 + 2) + 1 / (60 + 1)
```

两者融合分数相同，结果按 ID 的 Ordinal 顺序排列，应为 a、b。这是按代码推导的预期，不是本轮运行日志。第一路的 9 分和第二路的 2 分没有直接相加；RRF 使用它们决定的名次。

同一 ID 跨路出现时，正文、ContentId 和来源等元数据必须一致；若一路把 a 的正文换成另一段，融合器拒绝输入，而不是随机挑一份。这也是示例把正文和来源固定在 A/B 函数里的原因。

## 第二步：区分分数归一化和 SQL 加权

把上例 `Mode` 改成 `NormalizedScore`，Core 会对各路做 min-max 再加权，缺失通道贡献零；常量非空通道统一贡献一。它与 RRF 的名次公式不同。非有限分数会被拒绝。

SQL 路径则有自己的合同：

```text
hybrid_score = text_weight * normalized_bm25
             + vector_weight * vector_score
```

已有带全文索引与 embedding 的文档集合，可以分别投影 `bm25_score()`、`vector_distance()`、`vector_score()` 和 `hybrid_score()`，再查看 EXPLAIN 的 `hybrid_search` 路径。不要把 SQL 里的向量 score 当成 Core reader 输入的负距离，也不要把 RRF 分数解释成概率。

候选窗口会影响融合：没进入任一路前 N 条的文档，融合器无法凭空找回来。扩大窗口需要重新考虑条目、文本、扫描和时间预算，不能仅按最终 TopK 估算开销。

## 第三步：接到已发布 RAG generation

生产内容应先通过 `RagIngestionWriter` 发布完整 generation。已有 `database`、摄取时完整复制的 `profile`，以及同一模型生成的 `queryVector` 后，调用形状如下：

```csharp
var reader = new RagGenerationSearch(database, "manuals", profile);
var result = await reader.SearchAsync(
    "pressure calibration",
    queryVector,
    new SemanticSearchFusionOptions
    {
        TopK = 5,
        MaxCandidatesPerSource = 100,
        MaxScannedDocuments = 10_000,
        MaxDuration = TimeSpan.FromSeconds(5),
        Mode = SemanticSearchFusionMode.ReciprocalRank
    },
    allowedDocumentIds: authorizedChunkIds,
    cancellationToken: cancellationToken);
```

这些变量是宿主前提，不是可复制后自动生成模型和数据的完整程序。`authorizedChunkIds` 是宿主已授权的分块文档 ID 集；只有整个 stream 都已授权时才可省略。查询租约让同一次召回与重排使用同一 generation，发布新版本不会让一次调用混读版本。

reader 核完整 profile，包括 provider、model、revision、normalization、模态和外发策略；还核 generation、维度、metric 和分块身份。相同 profile ID 或相同维度都不能证明换模型后的语义兼容。

当前 reader 使用有界精确向量扫描，并复用全文索引，不是把无取消预算的通用 ANN 路径包装成绝对硬截止服务。它不生成 embedding。

## 第四步：在召回和重排之前处理权限

授权应在向量计分、全文候选和重排之前落实；不能先把全部候选交给外部模型，再裁掉无权结果。`Source`、`Section`、稳定分块 ID 与父 ContentId 应跟随结果保留，后续回答才有依据。

`DeduplicateByContent=true` 会在融合后、重排窗口前只保留每个父内容的最佳分块。它不是“正文一样就去重”；默认仍保留不同分块。

可选 `ISemanticSearchReranker` 默认不开启。宿主注入后只收到有限只读窗口，返回值必须覆盖窗口里全部且仅有的 ID 及有限分数，不能新增、遗漏或重复 ID，也不能替换正文来源。返回结果中 `FusionScore` 保留融合前后可对照的原融合分数，`Score` 是最终排序值。

如重排器或 embedding provider 要联网，宿主仍须落实批准目标、内容外发与审计策略。接口存在不是数据外发授权。

## 第五步：记录失败和验证边界

默认融合预算包括每路 100 条、总计 1,600 条、文本与元数据 4 Mi UTF-16 字符、重排窗口 100 条、协作期限 30 秒。reader 另有默认 10,000 文档、16 Mi JSON 字符、8 MiB 快照与 100,000 posting 检查预算。重复输入条目也计入预算；这些不是进程 RSS、吞吐或性能测试数字。

超量、超时、取消、profile 不匹配或 hook 错误不应被记录为“返回少量结果也成功”。协作取消不能强行中断不遵守 token 的扩展、一次同步索引打开或底层读取。

验证建议分三层保存：先用上述人工候选核对名次与去重；再用 writer 发布、重开和删除/授权样例核对真实数据路径；最后用真实模型、语料和固定目标硬件测 Recall@K、nDCG、延迟及成本。本篇没有执行后三类验收，也没有把 fixture、hash fallback 或合成向量计入真实质量证据；Graph 继续为 Beta。

参考：[RAG 融合合同](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/rag-search-fusion.md)、[融合器源码](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Core/SemanticContent/SemanticSearchFusion.cs)、[generation reader](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Core/SemanticContent/RagGenerationSearch.cs)。
