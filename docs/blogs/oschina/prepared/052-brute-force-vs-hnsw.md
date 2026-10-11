---
title: '暴力搜索 vs HNSW 索引：精度与延迟的权衡'
categories: SonnetDB,数据库
draft: false
---

正式发行见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文按 2026-10-07 当前 main 文档与源码修订旧稿；主线改动不自动代表全部 v4 发行物与客户端的已验收能力。以下 SQL/API 为复现示例，本次没有实际执行或性能测量。

精确扫描和 HNSW 分别提供不同的搜索成本与结果性质。选择应基于数据规模、过滤选择性和实际召回，而不是“有索引一定快”的推断。

## 精确扫描

精确路径对符合条件的向量计算距离，形成过滤后的 Top-K。主要距离计算成本随候选数量和维度增长；小集合、强过滤或索引不可用时，它可能是合适的正确性基准。

## HNSW 近似搜索

HNSW 在图中访问部分候选，以减少计算，但可能遗漏精确最近邻。m、构建候选和查询候选参数影响精度与成本；实际速度还与缓存、维度、线程、段数量和读取介质有关。

```sql
SELECT time, distance
FROM knn(documents, embedding, [1, 0, 0], 10, 'cosine')
WHERE source = 'demo';
```

同一条 SQL 可能因实际 schema、索引状态和过滤合同走不同路径。需要记录路径与回退证据，不能只凭查询文本认定这是 ANN 基准。

## 公平比较

用同一批有限非零向量、同一距离度量和同一查询集合，保存精确 Top-K 作为真值，再比较 ANN 的命中集合与耗时。区分冷启动、热查询、建索引和恢复；记录实际返回不足 k 条与同距离并列的处理方式。

本次没有跑基准，不给出假定的毫秒、倍数或“99% 召回”。过滤后的正确性、恢复和固定硬件证据应分别验收。

依据：[vector-search.md](https://github.com/IoTSharp/SonnetDB/blob/main/docs/vector-search.md)。
