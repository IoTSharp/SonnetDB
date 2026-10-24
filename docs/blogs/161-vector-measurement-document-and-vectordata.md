---
title: SonnetDB 向量放在哪：Measurement、Document 与 VectorData 的选择
categories: SonnetDB
draft: false
---

# SonnetDB 向量放在哪：Measurement、Document 与 VectorData 的选择

SonnetDB 提供 Measurement 和 Document 两类向量主数据入口，并通过 VectorData adapter 接入 .NET 的向量存储抽象。本文从数据身份、查询方式和模型兼容检查出发，说明三种入口如何选择，并给出可以逐步核对的 SQL 和生命周期预检示例。

![SonnetDB 向量入口：Measurement、Document 与 VectorData](https://img2024.cnblogs.com/blog/1725297/202610/1725297-20261016130506382-1320102946.png)

图：AI 主题示意图，并非实测截图；VectorData 映射 Document，维度一致不等于模型语义兼容。

## 先看主数据，而不是先选 ANN 名称

| 读者要保存什么 | 入口 | 身份与查询重点 |
|---|---|---|
| 有时间、TAG 和其它遥测字段的记录 | Measurement `VECTOR(N)` | 点的时间/TAG 身份，`knn(...)` 与时间条件 |
| 文章片段、知识条目和应用 JSON 记录 | Document Collection | 文档 ID、JSON path，`vector_search(...)` 与文档过滤 |
| 已使用 Microsoft.Extensions.VectorData 的 .NET 应用 | SonnetDBVectorStore | collection 映射 Document，不增加第三套存储引擎 |

例如，一次设备采样的向量与其温度、时间共同进入 Measurement；一段维修说明的向量与标题、来源共同进入 Document。原始文件可放 Object Bucket，但文件上传本身不会自动生成 embedding 或建立索引。

本文不是重新讲解向量字面量、距离公式或四种 ANN 参数。重点是先确定原始记录是什么，再选择接入合同，避免为一个模型在两套主数据中建立无法对账的副本。

## Measurement：向量跟随时间和 TAG

在独立测试数据库创建最小对象：

```sql
CREATE MEASUREMENT device_vectors (
    device TAG,
    note FIELD STRING,
    embedding FIELD VECTOR(3)
);

INSERT INTO device_vectors (device, note, embedding, time) VALUES
    ('pump-01', 'sample-a', [1, 0, 0], 1700000000000),
    ('pump-01', 'sample-b', [0, 1, 0], 1700000001000);

SELECT *
FROM knn(device_vectors, embedding, [1, 0, 0], 2, 'cosine')
WHERE device = 'pump-01'
  AND time >= 1700000000000
  AND time < 1700000002000;
```

预期核对两条记录的 `time`、`distance`、TAG 和 FIELD。按余弦距离手算，`sample-a` 距离为 0，`sample-b` 为 1；这是教学输入的数学预期，本轮没有实际运行 SQL。没有声明 ANN 索引的这个例子用于检查基本语义，不用于证明索引加速。

写入维度必须等于 `VECTOR(3)`，各分量必须有限。若后续声明 HNSW、IVF、IVF-PQ 或 Vamana，查询 metric 还要与对应索引一致；是否走 ANN、是否补偿或回退，需要结合真实执行条件核对。

## Document：向量是 JSON 中的一条路径

```sql
CREATE DOCUMENT COLLECTION knowledge;
INSERT INTO knowledge (id, document) VALUES
    ('manual-a', '{"title":"泵维护说明","embedding":[1,0,0]}'),
    ('manual-b', '{"title":"阀门检查说明","embedding":[0,1,0]}');

CREATE VECTOR INDEX idx_knowledge_embedding
ON knowledge ('$.embedding')
WITH (dimensions=3, metric='cosine', m=16,
      ef_construction=200, ef_search=64);

SELECT id,
       json_value(document, '$.title') AS title,
       vector_distance() AS distance,
       vector_score() AS score
FROM vector_search(
    source => knowledge,
    vector_field => '$.embedding',
    vector => [1, 0, 0],
    k => 2,
    metric => 'cosine'
)
ORDER BY distance;
```

核对 `manual-a` 排在 `manual-b` 前，标题由原文档读取。`vector_distance()` 越小越近；不能把 `vector_score()` 当作模型置信概率。先保留这组查询，再用 `EXPLAIN` 查看 `document_vector_index` 或 `document_vector_scan`，不要凭索引名称判断命中。

当前通用 SQL 的 Document ANN 要求 path、维度、metric 和排序等条件匹配。加入 `WHERE` 时，当前通用路径采用过滤后的精确扫描；特定 M35 语义图片服务的 filtered ANN 已有独立实现，不能据此宣称任意 SQL `WHERE` 已走同一服务合同。过滤后的正确结果和低延迟是两项不同的验收。

## VectorData：复用 Document 的接入抽象

`SonnetDB.Data.VectorData.SonnetDBVectorStore` 把 collection 映射为 Document Collection，记录整体保存为 JSON，主键对应文档 ID，默认向量路径为 `$.embedding`。选择它的理由是应用已使用 VectorData 的接口和记录定义。

不要把这层 adapter 当作新的原生模型，也不要由普通查询远程可用推断所有生命周期接口都已接通远程。当前 `PreflightAsync`、`GetIndexHealthAsync` 及图重建切片只支持嵌入式连接，远程明确拒绝。

前面的 `knowledge` 和索引已经存在时，可以按当前源码调用：

```csharp
using SonnetDB.Data;
using SonnetDB.Data.VectorData;
using SonnetDB.Query;

using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
await using var connection = new SndbConnection("Data Source=./vector-demo");
await connection.OpenAsync(timeout.Token);
using var vectors = new SonnetDBVectorStore(connection);

var report = await vectors.PreflightAsync(
    "knowledge", "idx_knowledge_embedding",
    new float[] { 1, 0, 0 }, KnnMetric.Cosine, timeout.Token);

Console.WriteLine(report.IsIndexCompatible);
Console.WriteLine(report.IsProfileCompatible);
```

连接的数据目录必须就是建立该集合的目录；否则应先修正目录，不能把“找不到索引”解释为模型不兼容。此例读取已有索引定义，不会创建集合、生成 embedding 或自动修复索引。

普通 collection 未持久绑定完整模型合同时，预检返回 `IsProfileCompatible = null`、`profile_unbound`。`IsIndexCompatible` 通过只说明维度、metric、有限值和余弦非零范数等索引条件满足，不能写成“模型已验证”。

## 同为三维，仍可能不能互查

生产应用至少要独立保存 provider、model、revision、维度、metric、normalization 和用途。模型 A 与模型 B 即使都输出相同长度的向量，也不能据此混入一个可比较的空间。本文的三维数组仅便于核对数据和距离，不是真实模型效果。

已有 M35 RAG stream 的 `PreflightRagAsync` 会读取持久完整 profile 和发布 generation，按严格合同比较；这仍只证明模型合同一致，不能把 hash fallback、合成 fixture 或字段 `verified` 当作真实语义、Recall、成本或用户收益证明。

生命周期里另有“已加载 HNSW 图重建”，从已有持久向量 KV 构建候选图。它不重读主文档生成 embedding，不修复主文档/KV，也不是无阻塞在线服务或持久任务；不能因为健康状态为 `loaded` 就宣称主数据一致。

## 使用版本和可核查边界

2026-10-16 核查的本机源码基线为 `dcadffc7137f5653ec46c55a4171cf5f2d83de03`；当次公开正式 GitHub Release 和 NuGet Core 页面为 4.0.0。本文依据当前源码介绍已实现入口，不保证每个入口均存在于任意 4.0.0 二进制；使用生命周期扩展前先核对所用 `SonnetDB.Data` 的实际 API。泛型 VectorData 记录访问带有反射/动态代码标注，不能把生命周期诊断 DTO 的 source-generated JSON 证明扩展为整个泛型 adapter 已通过 Native AOT。

本轮未运行这些 SQL/C# 示例、产品构建或测试；没有真实模型质量、固定硬件吞吐/Recall、断电恢复或长期生产证据。Graph 仍为 Beta；本文不以图或 UI 预览的进展替代向量能力验收。

- [向量检索与通用 SQL 边界](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/vector-search.md)
- [VectorData 生命周期预检](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/vector-lifecycle-preflight.md)
- [VectorStore 的 Document 映射](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Data/VectorData/SonnetDBVectorStore.cs)
- [生命周期扩展实现](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Data/VectorData/SonnetDBVectorLifecycle.cs)
