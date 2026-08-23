---
layout: default
title: "知识图谱与 GraphRAG 合同"
description: "SonnetDB M40 #365 provenance、confidence、source/chunk、valid time、alias/claim、community/summary 引用与 typed SDK 投影边界。"
permalink: /knowledge-graph-contract/
---

# 知识图谱与 GraphRAG 合同

M40 #365 提供版本化的知识图谱上层合同、严格校验器、Native AOT JSON 元数据，以及把合同投影到通用原生属性图的 typed SDK 入口。它覆盖 provenance、confidence、source/chunk、valid time、alias/claim、community/summary 和 Vector 引用。

本项没有修改 Graph V1 record、WAL、checkpoint、备份或查询执行器。Core 仍只持久化通用 Vertex、Edge、Label 和 typed Property；实体抽取、消歧、事实判断、embedding 和社区摘要生成由上层 job 完成。

当前产品阶段仍是 SonnetDB Graph Beta。运维产品面归 #366；固定硬件、LDBC/Graphalytics、7 天 mixed workload、crash/backup/Native AOT 发布报告和 Couplet 联合门禁归 #367。在 #367 通过前不宣称生产可用的第九种数据模型。

## 数据流

```text
extract/disambiguation/algorithm/LLM job
                  |
        KnowledgeGraphBatch v1
                  |
        KnowledgeGraphValidator
                  |
       KnowledgeGraphMapper v1
                  |
          GraphImportRequest
                  |
     existing Graph import transaction
                  |
      generic Graph V1 keyspace/WAL

Document/Object  <- authoritative text and bytes
Vector           <- embedding and index lifecycle
Graph            <- stable references and relations only
```

`KnowledgeGraphBatch.RequestId` 原样绑定现有 Graph 幂等事务。嵌入式和远程 `SndbGraphClient` 都调用同一个 `ImportAsync` 合同；没有知识图谱专用 HTTP endpoint、存储格式、认证或事务旁路。

## 合同

节点类别固定为：

| 节点 | 必需字段 | 用途 |
|---|---|---|
| `Entity` | `id`、`name`、`provenance` | 规范化实体；可引用 Vector 记录 |
| `Alias` | `name`、`confidence`、显式 `validTime` | 指向规范实体的别名 |
| `Claim` | 结构化 `subjectId/predicate/objectId|literalValue`、`confidence`、显式 `validTime` | 小型事实声明；literal 上限 4 KiB，不能承载正文 |
| `Source` | 不带 chunk 的 Document/Object `content` 引用 | 权威来源 |
| `Chunk` | 带稳定 `chunkId` 的 Document/Object `content` 引用 | 权威内容分块 |
| `Community` | `resultVersion`、algorithm、可选 source sequence | 引用 #363 或上层算法结果 |
| `Summary` | 不带 chunk 的权威 `content`、community result version | 引用存放在 Document/Object 的摘要；可另指向 Vector |

关系类别和同批次端点形状为：

| 关系 | 形状 | confidence / valid time |
|---|---|---|
| `Asserts` | Entity -> Claim | 必需 |
| `SupportedBy` | Claim -> Source/Chunk | 必需 |
| `Contradicts` | Claim -> Source/Chunk | 必需 |
| `AliasOf` | Alias -> Entity | 必需 |
| `ChunkOf` | Chunk -> Source | 可选 |
| `MemberOf` | Entity/Claim -> Community | 可选 |
| `SummarizedBy` | Community -> Summary | 可选 |

关系可以引用批次前已经存在的节点；若两个端点都在当前批次，校验器会提前检查形状。最终 endpoint 存在性和 element version 仍由同一个 Graph transaction 校验。

`KnowledgeProvenance` 要求 producer、revision、run ID 和 UTC observed time，并可直接引用具体 Document/Object 版本与 chunk。`KnowledgeValidTime` 使用 `[validFromUtc, validToUtc)`；任一端可以无界，完全无界时显式使用 `KnowledgeValidTime.Unbounded`。

## 权威引用

`KnowledgeContentReference` 只允许 `Document` 或 `Object`，并要求 container、ID 和不可变 version/ETag/revision。`KnowledgeVectorReference` 只保存 index、record ID 和 embedding profile ID。

合同中没有以下字段：

- Document 正文、OCR/transcript、社区摘要文本；
- Object bytes、媒体或缩略图；
- embedding 数组、ANN 邻接或全文倒排；
- prompt、模型响应正文或 provider 凭据。

这些内容分别由 Document、Object、Vector、FullText 和 Server/SDK job 管理。Graph property 只保存小型标量和稳定引用。

## .NET SDK

```csharp
using SonnetDB.Data.Graphs;
using SonnetDB.Data.KnowledgeGraphs;
using SonnetDB.KnowledgeGraphs;

using var graphClient = new SndbGraphClient(connectionString);
await graphClient.CreateGraphAsync("plant-knowledge");

var source = new KnowledgeContentReference(
    KnowledgeContentStoreKind.Document,
    "manuals",
    "temperature-alarm",
    "docs-v7",
    chunkId: "alarm-threshold");

var provenance = new KnowledgeProvenance(
    "industrial-extractor",
    "rules-v3",
    "run-20260823",
    DateTimeOffset.Parse("2026-08-23T00:00:00Z"),
    source);

var batch = new KnowledgeGraphBatch(
    Guid.NewGuid(),
    [
        new KnowledgeGraphNode("entity:sensor-a", KnowledgeGraphNodeKind.Entity, provenance)
        {
            Name = "Sensor A",
        },
        new KnowledgeGraphNode("claim:sensor-a-high", KnowledgeGraphNodeKind.Claim, provenance)
        {
            Claim = new KnowledgeClaimValue
            {
                SubjectId = "entity:sensor-a",
                Predicate = "temperature_status",
                LiteralValue = "high",
            },
            Confidence = 0.94,
            ValidTime = KnowledgeValidTime.Unbounded,
        },
        new KnowledgeGraphNode("chunk:alarm-threshold", KnowledgeGraphNodeKind.Chunk, provenance)
        {
            Content = source,
        },
    ],
    [
        new KnowledgeGraphRelation(
            "assert:sensor-a-high",
            KnowledgeGraphRelationKind.Asserts,
            "entity:sensor-a",
            "claim:sensor-a-high",
            provenance)
        {
            Confidence = 0.94,
            ValidTime = KnowledgeValidTime.Unbounded,
        },
    ]);

KnowledgeGraphValidationResult validation = KnowledgeGraphValidator.Validate(batch);
GraphImportResponse result = await graphClient.ImportKnowledgeGraphAsync(
    "plant-knowledge",
    batch);
```

单个合同批次最多包含 256 个节点和关系，映射到一个有界 Graph 原子事务。批量 job 应自行分页，并为每页保留稳定 request ID；不允许把多页描述为跨页原子。

更新时设置每个节点/关系的 `ExpectedElementVersion`。软撤回通过更新 `validToUtc` 完成；需要物理删除时使用既有 `DeleteEdgeAsync` / `DeleteVertexAsync`，顶点删除继续服从 Graph `RESTRICT`。合同不创建跨 graph、跨 Document/Object/Vector 的原子事务。

## 稳定投影

`KnowledgeGraphMapper.ProjectionVersion` 固定为 `m40-kg-v1`。节点和关系外部 ID 分别映射到稳定 vertex/edge ID；节点类别映射为 `__kg_entity`、`__kg_alias`、`__kg_claim`、`__kg_source`、`__kg_chunk`、`__kg_community`、`__kg_summary` label，关系使用对应的 `__kg_*` label。

字段投影到 `__kg_external_id`、`__kg_kind`、`__kg_confidence`、`__kg_valid_from/to`、`__kg_producer/revision/run_id/observed_at`、`__kg_content_*`、`__kg_provenance_source_*`、`__kg_claim_*`、`__kg_vector_*` 和 `__kg_community_*` typed properties。属性和 label ID 复用 `SndbGraphImporter` 的稳定 SHA-256 映射；`__kg_external_id` 在对应 label 内保持唯一。

投影版本是兼容边界。以后增加字段只能 extend-only；改变既有 label/property 含义或 ID 映射必须使用新的 projection version，不能静默重解释已写数据。

## JSON 与自动回归

`KnowledgeGraphJsonContext` 是公开的 source-generated JSON context，可用于 Native AOT job manifest 或队列边界，不需要反射型 `JsonSerializerOptions` 重载。

自动回归覆盖：

- 完整 Entity/Claim/Source/Chunk/Alias/Community/Summary 合同；
- 非法 confidence、倒置 valid time、混合 claim object、缺失 chunk 和错误关系形状；
- source-generated JSON round-trip 及无正文/向量数组字段；
- 稳定 ID、label、property、唯一 external ID 和 evidence endpoint 投影；
- 嵌入式与远程 typed SDK 写入、读取和相同 request ID 幂等重放。

这些是合同与接线 correctness smoke，不替代 #367 的固定规模、恢复、长稳和发布证据。
