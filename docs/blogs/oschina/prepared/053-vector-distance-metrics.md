---
title: '向量距离度量深度对比：Cosine vs L2 vs Inner Product'
categories: SonnetDB,数据库
draft: false
---

正式发行见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文按 2026-10-07 当前 main 文档与源码修订旧稿；主线改动不自动代表全部 v4 发行物与客户端的已验收能力。以下 SQL/API 为复现示例，本次没有实际执行或性能测量。

cosine、L2 和 inner product 不是可以任意互换的名字。它们比较不同的数学性质，排序方向还会随 API 返回约定变化。

| 度量 | 定义 | 关注点 |
|---|---|---|
| cosine distance | 1-cosine_similarity | 方向差异 |
| L2 distance | sqrt(sum((a-b)^2)) | 绝对几何距离 |
| KNN inner_product | -dot(a,b) | 点积越大，返回距离越小 |

SonnetDB 的标量 inner_product 返回正点积，KNN 与 `<#>` 返回负点积。正点积通常按降序表示更接近，负点积按升序；忽略负号会颠倒结果。

## 归一化向量之间的关系

对单位向量，L2 平方等于 2 倍余弦距离，因此排序可以一致，但数值并不相等。没有归一化时，幅度会影响 L2 和点积，不能把余弦阈值照搬给它们。

```sql
SELECT time, title, distance
FROM knn(documents, embedding, [1, 0, 0], 5, 'l2');
```

输入维度须一致且非空，分量应有限。余弦还要求非零范数。声明索引时选择的 metric 必须与查询一致，才能命中对应索引合同。

## 依据业务评估

文本模型、图像模型和推荐模型各自有距离与预处理约定。相同维度不能消除模型版本差异，距离也不自动变成概率。用真实语料与标注校准相关性；本文不提供通用的“低于某数值就是相似”阈值。

依据：[vector-search.md](https://github.com/IoTSharp/SonnetDB/blob/main/docs/vector-search.md)。
