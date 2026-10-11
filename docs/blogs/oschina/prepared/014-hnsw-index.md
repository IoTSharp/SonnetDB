---
title: HNSW 向量索引：加速向量搜索的强力引擎
categories: SonnetDB,HNSW,向量检索
draft: false
---

HNSW 通过分层邻接图生成近似最近邻候选，适合在延迟、索引成本和 Recall 之间做取舍。实际复杂度、召回和响应时间与数据、参数、过滤和硬件有关，不能把百万向量毫秒响应写成 SonnetDB 的普遍保证。

SonnetDB 已有正式 [4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文使用当前文档中的 measurement 示例；开发主线与发行物的差异应核对实际版本。

## 在向量 FIELD 上声明索引

```sql
CREATE MEASUREMENT sensor_embeddings (
    sensor_id TAG,
    value FIELD FLOAT,
    embedding FIELD VECTOR(3)
        WITH INDEX hnsw(m=16, ef=64, ef_construction=200, metric='cosine')
);

INSERT INTO sensor_embeddings (time, sensor_id, value, embedding)
VALUES
    (1700000000000, 'sensor-a', 23.5, [1, 0, 0]),
    (1700000001000, 'sensor-a', 23.8, [0.8, 0.2, 0]),
    (1700000002000, 'sensor-b', 24.1, [0, 1, 0]);
```

3 维向量便于完整展示。实际写入长度必须与 schema 一致，分量不能是 NaN/Infinity。关系表不支持 VECTOR 列，旧稿中的 `CREATE TABLE ... VECTOR` 和任意关系索引示例不能沿用。

## 使用 KNN 查询入口

```sql
SELECT *
FROM knn(sensor_embeddings, embedding, [1, 0, 0], 5, 'cosine')
WHERE sensor_id = 'sensor-a'
  AND time >= 1700000000000
  AND time < 1700000002000;
```

KNN 的 k 是返回数量上限。measurement 路径会结合 TAG/time 范围、度量、维度和数据状态选择 ANN、补偿或精确回退。标量 `ORDER BY l2_distance(...) LIMIT ...` 不应自动宣传为所有形状都会使用 HNSW。

索引是派生结构，按 Segment 等生命周期构建和读取。没有索引、索引暂不可用或条件不适合 ANN 时仍有精确路径；因此刚插入三个点并查到结果，并不证明这次执行已经使用索引。

## 参数需要与数据共同评估

- `m` 控制图连接规模，影响构建和存储成本。
- `ef_construction` 控制建图候选宽度。
- `ef` 对应 measurement 索引的搜索宽度配置。
- `metric` 应与查询度量匹配。

测 Recall@K 时应在相同向量与过滤条件下建立精确 Top-K 基线，同时记录构建时间、索引大小、P95/P99 和进程资源。小型合成数据只能验证调用路径，不能替代真实模型与语料质量。

SonnetDB 还提供文档集合的持久 HNSW 索引，但其声明和查询入口与 measurement 不同，按自己的合同使用。Graph Beta 是另一个原生属性图模型，HNSW 的内部索引图不等于 Graph 生产门禁。

参考：[向量与混合检索](https://github.com/IoTSharp/SonnetDB/blob/main/docs/vector-search.md)、[类型边界](https://github.com/IoTSharp/SonnetDB/blob/main/docs/relation-type-boundary.md)和[向量生命周期预检](https://github.com/IoTSharp/SonnetDB/blob/main/docs/vector-lifecycle-preflight.md)。本文核对文档，未执行新索引或 Recall 基准。
