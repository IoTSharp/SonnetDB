---
title: '向量维度平均：centroid(embedding) 聚合函数详解'
categories: SonnetDB,数据库
draft: false
---

正式发行见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文按 2026-10-07 当前 main 文档与源码修订旧稿；主线改动不自动代表全部 v4 发行物与客户端的已验收能力。以下 SQL/API 为复现示例，本次没有实际执行或性能测量。

centroid 把一组同维向量按维度求平均，适合描述一个集合的中心。它既不训练嵌入模型，也不自动把中心归一化。

## 一个完整的三维示例

```sql
CREATE MEASUREMENT embeddings (source TAG, embedding FIELD VECTOR(3));
INSERT INTO embeddings (time, source, embedding) VALUES
  (1700000000000, 'demo', [1, 0, 0]),
  (1700000001000, 'demo', [0, 1, 0]);
SELECT centroid(embedding)
FROM embeddings WHERE source = 'demo';
```

按算术定义，这两个样本的中心为 `[0.5,0.5,0]`。这里是手算解释，并非本次实际查询回执。当前累加器使用 double 数组保存各维和，最终返回 float[]；空输入返回 NULL，空向量或维度不一致会拒绝。

## 中心与语义代表的区别

只有同一模型、同一预处理和同一维度的向量才适合共同求中心。把不同模型生成的同维向量混在一起，平均结果也缺少可靠含义。对余弦检索，应用可按模型约定归一化中心；零向量不能用于需要非零范数的余弦距离。

```sql
SELECT time, centroid(embedding)
FROM embeddings WHERE source = 'demo'
GROUP BY time(1h);
```

每小时中心可用于观察集合变化，但该函数不会返回簇成员、簇数量或聚类质量。聚类、离群检测和真实语义评估需要额外工作；toy 向量只能解释数学语义。

依据：[sql-reference.md](https://github.com/IoTSharp/SonnetDB/blob/main/docs/sql-reference.md)、[ExtendedAggregateFunctions.cs](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Query/Functions/Aggregates/ExtendedAggregateFunctions.cs)。
