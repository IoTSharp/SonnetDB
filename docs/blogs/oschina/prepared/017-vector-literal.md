---
title: VECTOR 字面量：使用 `[v0, v1, ...]` 语法操作嵌入向量
categories: SonnetDB,SQL,向量
draft: false
---

SonnetDB 用方括号表达向量字面量，例如 `[0.1, 0.2, 0.3, 0.4]`。这使向量写入、距离计算和 KNN 查询可以直接出现在 SQL 中。使用时首先要对齐字段维度，再检查数值和距离度量。

## 定义维度并写入数据

下面采用四维向量，方便完整展示每个分量。请在同一个练习数据库中执行建表、写入和查询。

```sql
CREATE MEASUREMENT documents (
    source TAG,
    title FIELD STRING,
    embedding FIELD VECTOR(4)
);

INSERT INTO documents (time, source, title, embedding) VALUES
    (1700000000000, 'guide', '向量检索入门', [0.1, 0.2, 0.3, 0.4]),
    (1700000001000, 'guide', 'SQL 查询入门', [0.2, 0.1, 0.4, 0.3]),
    (1700000002000, 'note', '设备维护记录', [0.8, 0.1, 0.1, 0.2]);

SELECT time, source, title, embedding FROM documents ORDER BY time;
```

`VECTOR(4)` 要求四个分量。声明 384 维却只写四个值，不能作为有效示例。生产系统应记录嵌入模型、版本、维度和归一化约定，避免把不同模型的坐标混在一起。

向量组件采用 float32，客户端结果中的向量表示为 `float[]`。`[1, 2, 3, 4]` 里的整数写法会成为向量数值分量，不会创建一个独立的“整数向量”类型。还应使用有限数值，避免把非数或无穷值送入检索路径。

## 在查询中使用向量

字面量不用外层单引号；`'[0.1, 0.2, 0.3, 0.4]'` 是字符串，不能把 pgvector 的字符串输入习惯直接搬过来。

```sql
SELECT title,
       cosine_distance(embedding, [0.1, 0.2, 0.3, 0.4]) AS distance
FROM documents
ORDER BY distance
LIMIT 3;

SELECT *
FROM knn(documents, embedding, [0.1, 0.2, 0.3, 0.4], 3, 'cosine');
```

标量距离表达式与 `knn` 分别提供普通表达式计算和专门检索入口。不能仅凭查询里出现了距离函数，就断言它会自动使用 HNSW。索引声明、过滤条件与索引生命周期共同影响执行路径。

余弦距离需要非零向量，两个参与计算的向量必须维度一致。`l2_distance` 返回欧氏距离；`inner_product` 标量函数返回正的点积，若按相似度排名通常使用降序。

## 示例坐标与真实语义

本文的四维坐标是演示数据，用于说明 SQL 合同。它们不能证明文本语义质量。真实应用需要用选定的嵌入模型生成向量，并在自己的检索集合上验证召回、延迟和成本。

本文按当前仓库文档与实现校对：[向量搜索指南](https://github.com/IoTSharp/SonnetDB/blob/main/docs/vector-search.md)、[SQL 参考](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[4.0.0 正式发行](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
