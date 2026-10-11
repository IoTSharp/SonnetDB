---
title: 'SonnetDB KNN 向量搜索入门：knn() TVF 语法与距离度量选择'
categories: SonnetDB,数据库
draft: false
---

正式发行见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文按 2026-10-07 当前 main 文档与源码修订旧稿；主线改动不自动代表全部 v4 发行物与客户端的已验收能力。以下 SQL/API 为复现示例，本次没有实际执行或性能测量。

knn 是 SonnetDB measurement 向量检索的表值函数，出现在 FROM 子句中。它返回最接近的候选，应用负责生成与存量数据相同模型的查询向量。

## 可阅读的完整示例

```sql
CREATE MEASUREMENT documents (
  source TAG, title FIELD STRING, embedding FIELD VECTOR(3)
);
INSERT INTO documents (time, source, title, embedding) VALUES
  (1700000000000, 'demo', '样本A', [1, 0, 0]),
  (1700000001000, 'demo', '样本B', [0, 1, 0]);
SELECT time, title, distance
FROM knn(documents, embedding, [1, 0, 0], 2, 'cosine');
```

参数依次为 measurement、向量 FIELD、查询向量、k，以及可选 metric。查询向量长度必须与 VECTOR(N) 一致，组件为有限数值；示例没有用省略号冒充可执行向量。

## 距离含义

cosine 为 1 减余弦相似度，l2 为欧氏距离，inner_product 在 KNN 中采用负内积。三者都以距离越小越近，内积的负号不要与返回正点积的标量函数混淆。

KNN 返回 time、distance、全部 TAG 和 FIELD；向量 FIELD 为 float[]。k 是结果上限，过滤后数据不足时不能要求一定有 k 条。

本文的三维向量只解释数学输入，不代表文本语义质量。相同维度不代表不同嵌入模型可以比较；零向量也不适用于要求非零范数的余弦距离。索引与回退路径需另行核对。

依据：[vector-search.md](https://github.com/IoTSharp/SonnetDB/blob/main/docs/vector-search.md)。
