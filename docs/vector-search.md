# SonnetDB 向量与混合检索

SonnetDB 当前同时支持两类向量主数据：

- Measurement 的 `FIELD VECTOR(N)`，适合带时间、TAG 和其它遥测字段的向量数据。
- Document Collection 中 JSON 数组形式的向量字段，适合通用文档、知识块和应用记录。

两类模型都保留精确扫描作为正确性回退。Measurement 可声明 HNSW、IVF、IVF-PQ 或 Vamana 段内 ANN 索引；Document Collection 可声明持久 HNSW 向量索引，并与全文 BM25 组成 Hybrid Search。SonnetDB 还通过 `SonnetDB.Data` 提供 `Microsoft.Extensions.VectorData` adapter。

> SonnetDB 当前是具备向量、全文、Document 和对象存储能力的多模型数据库。数据库不会自动为图片、音频、视频或任意对象生成 embedding；原始内容摄取、模型版本和多模态索引生命周期归 Milestone 35。

---

## 能力选择

| 需求 | 推荐入口 |
|---|---|
| 带时间和 TAG 过滤的向量检索 | Measurement `VECTOR(N)` + `knn(...)` |
| 通用记录 / 文档向量检索 | Document Collection + `vector_search(...)` |
| 文本 BM25 + 向量融合 | Document Collection + `hybrid_search(...)` |
| 遥测向量关联知识文档 | Measurement + Document 的 `hybrid_search(...)` |
| .NET Vector Store 抽象 | `SonnetDB.Data.VectorData` |
| 原始图片 / 文件保存 | Object Bucket；应用负责生成向量并写入 Document / Measurement |

## Measurement 向量

### Schema 与 ANN 索引

在 `CREATE MEASUREMENT` 中声明 `FIELD VECTOR(dim)`。一个 Measurement 可以包含多个不同维度的向量字段。

```sql
CREATE MEASUREMENT documents (
    source TAG,
    title FIELD STRING,
    embedding FIELD VECTOR(3)
        WITH INDEX hnsw(
            m=16,
            ef=64,
            ef_construction=200,
            metric='cosine'
        )
);
```

Measurement 向量索引支持：

- `hnsw(m, ef[, ef_construction][, metric])`
- `ivf(nlist, nprobe[, max_iterations][, metric])`
- `ivf_pq(nlist, nprobe, m, nbits[, max_iterations][, metric])`
- `vamana(max_degree, search_list_size, alpha, beam_width[, metric])`

索引是由主数据构建的派生结构。未声明索引、索引暂不可用、查询条件不适合 ANN，或 ANN 候选不足以保证结果时，查询会保留精确扫描 / 补偿路径。

### 写入

向量字面量使用 `[f1, f2, ..., fN]`：

```sql
INSERT INTO documents (source, title, embedding, time) VALUES
    ('wiki', '量子计算简介', [0.12, -0.34, 0.57], 1700000000000),
    ('wiki', '神经网络基础', [0.88, 0.23, -0.11], 1700000001000),
    ('blog', '时序数据库选型', [0.03, 0.74, 0.16], 1700000002000);
```

示例使用 3 维向量便于阅读；生产模型通常使用更高维度，实际写入长度必须与 schema 的 `VECTOR(N)` 完全一致，且所有分量必须是有限数值。

### KNN 查询

```sql
SELECT *
FROM knn(measurement, column, query_vector, k [, metric])
[WHERE tag_condition [AND time_condition]]
```

| 参数 | 说明 |
|---|---|
| `measurement` | 目标 Measurement |
| `column` | `VECTOR` 类型 FIELD 列 |
| `query_vector` | 与列维度一致的查询向量 |
| `k` | 返回最近邻数量上限 |
| `metric` | 可选，默认 `cosine` |

```sql
-- 余弦距离 Top-5
SELECT *
FROM knn(documents, embedding, [0.12, -0.34, 0.57], 5);

-- TAG 与时间范围过滤
SELECT *
FROM knn(documents, embedding, [0.12, -0.34, 0.57], 10, 'l2')
WHERE source = 'wiki'
  AND time >= 1700000000000
  AND time < 1700000002000;
```

`knn(...)` 返回固定顺序的 `time`、`distance`、全部 TAG 和全部 FIELD。距离越小表示越相似；向量 FIELD 会以 `float[]` 返回。

Measurement 的 TAG / 时间条件会用于缩小候选范围。执行器只在索引度量、维度、数据状态和过滤条件满足要求时使用 ANN，并在必要时补偿或回退，不把 ANN 的近似候选误报成精确过滤结果。

## 距离度量

| 名称 | 含义 | 常见用途 |
|---|---|---|
| `cosine` / `cosine_distance` | `1 - cosine_similarity` | 文本、图片等归一化 embedding |
| `l2` / `l2_distance` / `euclidean` | 欧几里得距离 | 需要绝对几何距离的向量 |
| `inner_product` / `dot` / `ip` | 负内积，越小表示内积越大 | 已按模型约定归一化的向量 |

索引声明与查询使用的 metric 必须一致，才能命中对应 ANN 路径。相同维度不代表不同 embedding 模型生成的向量可以互相比较；应用当前必须自行保存和校验模型信息，M35 将补正式的 Embedding Profile。

## Document 向量搜索

Document Collection 把 JSON 文档作为主数据，向量字段是 JSON number array。`vector_search(...)` 可直接执行精确检索，也可命中相同 path、维度和 metric 的持久 HNSW 索引。

```sql
CREATE VECTOR INDEX idx_logs_embedding ON logs ('$.embedding')
WITH (
    dimensions = 3,
    metric = 'cosine',
    m = 16,
    ef_construction = 200,
    ef_search = 64
);
```

```sql
SELECT id,
       json_value(document, '$.title') AS title,
       vector_distance() AS distance,
       vector_score() AS score
FROM vector_search(
    source => logs,
    vector_field => '$.embedding',
    vector => [1, 0, 0],
    k => 20,
    metric => 'cosine'
)
ORDER BY distance;
```

当前 Document ANN 边界：

- 无 `WHERE`、按距离升序、path / dimension / metric 匹配时可以使用持久向量索引。
- 带 `WHERE` 时当前回退为先过滤、再精确扫描，保证过滤后的 Top-K 语义正确。
- `EXPLAIN` 会显示 `document_vector_index` 或 `document_vector_scan` 及索引名 / vector path。
- `INSERT` / `UPDATE` / `DELETE` 会维护派生索引；索引缺失或损坏时可从 Document 主数据重建。

带 metadata filter 的 ANN pre-filter、精确补偿和 `similar-by-id` 归 M35 #298。实现前不能把当前带过滤的精确回退描述为 filtered ANN。

## Hybrid Search

Document Hybrid Search 把全文 BM25 与向量分数融合：

```sql
SELECT id,
       bm25_score() AS text_score,
       vector_distance() AS distance,
       hybrid_score() AS score
FROM hybrid_search(
    source => logs,
    text_index => ft_logs_message,
    text_field => '$.message',
    text => 'pump alarm',
    vector_field => '$.embedding',
    vector => [1, 0, 0],
    k => 20,
    text_weight => 0.6,
    vector_weight => 0.4
)
WHERE site = 'north'
ORDER BY score DESC;
```

当前融合分数为归一化 BM25 与向量分数的加权和，默认权重各为 0.5。结果可以投影 `bm25_score()`、`vector_distance()`、`vector_score()` 和 `hybrid_score()`。

`hybrid_search(...)` 还支持 Measurement KNN 与 Document 知识条目的关联融合。完整命名参数、关系维表过滤和结果伪列见 [SQL 参考](sql-reference.md#measurement-knn-与知识文档融合)。

## .NET 与管理入口

- 嵌入式与远程 SQL 均支持 Measurement `knn(...)`、Document `vector_search(...)` 和 `hybrid_search(...)`。
- 二进制帧协议的 `vector` service 使用紧凑 f32 传输 Measurement 查询向量与结果，REST 保留兼容入口。
- `SonnetDB.Data` 提供 `Microsoft.Extensions.VectorData` adapter，默认把通用 VectorData collection 映射为 Document Collection。
- Web Admin / Studio 的 Vector Playground 支持原始 `float[]`、文本 embedding、Top-K、受限 metadata filter、命中详情和索引参数查看。
- Playground 的文本 embedding 复用 Copilot Provider，仅生成查询向量，不自动为已有业务数据建索引。

## 当前限制与后续路线

| 当前能力 | 当前边界 | 后续归口 |
|---|---|---|
| Measurement ANN | 支持 HNSW / IVF / IVF-PQ / Vamana，必要时精确回退 | 继续以 recall、过滤和恢复基准治理 |
| Document ANN | 持久 HNSW；无过滤查询可走 ANN | M35 #298 filtered ANN / similar-by-id |
| Embedding Provider | Copilot 入口当前只处理文本 | M35 #300 多模态 Provider |
| 对象存储 + 向量 | 两类能力已存在，但没有自动关联和索引状态机 | M35 #297 / #299 |
| 以图搜图 | 尚无图片摄取、模型 profile 和产品 API | M35 #301 |
| 通用 RAG | Copilot 有内部文档管线，仍固定 384 维并维护私有检索逻辑 | M35 #302 |
| 音视频检索 | 尚无 transcript / 关键帧 / timecode 分段模型 | M35 #304 |

SonnetDB 在 M35 首个图片检索闭环完成前，应使用“多模型数据库、具备多模态检索底座”的表述，不应把现有向量字段本身等同于已经完成的多模态产品能力。
