---
title: 标量向量函数：cosine_distance / l2_distance / inner_product / vector_norm
categories: SonnetDB,SQL,向量
draft: false
---

向量检索除了 Top-K，还需要检查距离、相似度与向量长度。SonnetDB 提供四个标量向量函数，可以在投影中返回这些值。理解分数方向，比记住函数名称更重要。

## 准备四维向量

```sql
CREATE MEASUREMENT documents (
    source TAG,
    title FIELD STRING,
    embedding FIELD VECTOR(4)
);

INSERT INTO documents (time, source, title, embedding) VALUES
    (1700000000000, 'demo', '样本A', [1, 0, 0, 0]),
    (1700000001000, 'demo', '样本B', [0, 1, 0, 0]),
    (1700000002000, 'demo', '样本C', [0.8, 0.6, 0, 0]);

SELECT title,
       cosine_distance(embedding, [1, 0, 0, 0]) AS cosine,
       l2_distance(embedding, [1, 0, 0, 0]) AS l2,
       inner_product(embedding, [1, 0, 0, 0]) AS dot,
       vector_norm(embedding) AS norm
FROM documents
ORDER BY time;
```

示例使用合成坐标，便于观察函数合同，不能用来证明真实文本的语义检索质量。

## 四个值分别表示什么

| 函数 | 返回值 | 常见排序方向 |
| --- | --- | --- |
| `cosine_distance(a,b)` | `1 - cosine_similarity` | 升序，越小越接近 |
| `l2_distance(a,b)` | 欧氏距离 | 升序 |
| `inner_product(a,b)` | 正的点积 | 相似度通常降序 |
| `vector_norm(a)` | 向量的 L2 长度 | 按业务目的决定 |

```sql
SELECT title, inner_product(embedding, [1, 0, 0, 0]) AS score
FROM documents
ORDER BY score DESC
LIMIT 3;
```

点积受向量长度影响，不能把它无条件视为余弦相似度。即使选择相同的嵌入模型，也应确定是否归一化以及检索度量。

## 输入与分数方向的边界

二元函数要求非空且相同维度的向量。当前参数转换不把 `NULL` 当作可计算向量。余弦距离明确拒绝零向量，因为其归一化分母为零。

还要区分三个名称相近的入口：标量 `inner_product(a,b)` 返回正点积；pgvector 风格的 `<#>` 运算符返回负点积；`knn` 的 `inner_product` 度量也按负点积距离排序，以保持“越小越好”的检索方向。

普通标量距离查询不保证自动使用 HNSW。需要专门检索路径时，应核对 `knn`、字段索引声明和具体版本的执行合同，再实测召回和延迟。

本文按当前仓库实现校对：[函数实现](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/FunctionRegistry.cs)、[向量搜索指南](https://github.com/IoTSharp/SonnetDB/blob/main/docs/vector-search.md)、[4.0.0 正式发行](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
