---
title: pgvector 兼容运算符：<=> <-> <#>
categories: SonnetDB,SQL,向量
draft: false
---

SonnetDB 支持 pgvector 风格的三种向量距离运算符：`<=>`、`<->` 和 `<#>`。熟悉这些符号的开发者可以用它们表达距离排序，但需要注意负点积方向，以及兼容范围。

## 使用完整向量示例

```sql
CREATE MEASUREMENT documents (
    source TAG,
    title FIELD STRING,
    embedding FIELD VECTOR(4)
);

INSERT INTO documents (time, source, title, embedding) VALUES
    (1700000000000, 'demo', '样本A', [1, 0, 0, 0]),
    (1700000001000, 'demo', '样本B', [0.8, 0.6, 0, 0]),
    (1700000002000, 'demo', '样本C', [0, 1, 0, 0]);

SELECT title, embedding <=> [1, 0, 0, 0] AS distance
FROM documents
ORDER BY distance
LIMIT 3;
```

SonnetDB 向量字面量直接使用方括号，不要把它替换成带单引号的 pgvector 字符串输入。字段维度与字面量分量数必须一致。

## 运算符的数学含义

| 运算符 | 含义 | 与标量函数的关系 |
| --- | --- | --- |
| `<=>` | 余弦距离 | 对应 `cosine_distance` |
| `<->` | 欧氏距离 | 对应 `l2_distance` |
| `<#>` | 负的点积 | 等于正点积的相反数 |

```sql
SELECT title, embedding <-> [1, 0, 0, 0] AS distance
FROM documents
ORDER BY distance
LIMIT 3;

SELECT title, embedding <#> [1, 0, 0, 0] AS distance
FROM documents
ORDER BY distance
LIMIT 3;
```

三个距离运算符都使用升序表达“越小越好”。但标量 `inner_product(a,b)` 返回正点积，按相似度排名通常使用 `DESC`。切换接口时如果忽略这个负号，会把排序方向反过来。

## 兼容符号之外还要核对什么

余弦距离拒绝零向量，二元距离要求相同且非空的维度，并应使用有限组件。演示坐标只验证表达形式，不代表真实嵌入质量。

这些运算符的支持不等于完整 PostgreSQL/pgvector 兼容：DDL、类型转换、wire protocol 和全部 SQL 语义仍需逐项核对。不能据此宣称现有 pgvector 应用无修改迁移。

`ORDER BY` 距离也不自动证明使用了 ANN/HNSW。SonnetDB 的专门检索入口是 `knn`，索引和过滤组合应以实际执行合同和测量为准。

本文按当前仓库合同校对：[向量搜索指南](https://github.com/IoTSharp/SonnetDB/blob/main/docs/vector-search.md)、[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[4.0.0 正式发行](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
