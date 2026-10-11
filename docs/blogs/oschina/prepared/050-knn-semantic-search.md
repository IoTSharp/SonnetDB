---
title: 'SonnetDB 语义搜索实战：knn() 与标签过滤和时间范围的联合查询'
categories: SonnetDB,数据库
draft: false
---

正式发行见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文按 2026-10-07 当前 main 文档与源码修订旧稿；主线改动不自动代表全部 v4 发行物与客户端的已验收能力。以下 SQL/API 为复现示例，本次没有实际执行或性能测量。

语义检索不只是计算向量距离，还要限制来源与时间。SonnetDB 的 measurement knn 可以联合 TAG 等值和时间范围过滤，适合带时间属性的知识片段或设备特征。

假设 documents 已声明 source TAG、title FIELD STRING、embedding FIELD VECTOR(3)：

```sql
SELECT time, source, title, distance
FROM knn(documents, embedding, [0.12, -0.34, 0.57], 10, 'cosine')
WHERE source = 'wiki'
  AND time >= 1700000000000 AND time < 1700003600000;
```

## 过滤与 Top-K

过滤条件应参与候选范围，而不是先取全库十条，再删除不满足权限或来源的命中。执行器会检查索引度量、维度、数据状态和过滤条件，必要时精确扫描或补偿。不能因声明 HNSW 就保证每种过滤组合都只走近似图遍历。

Document 的通用 vector_search 是另一个入口：当前带 WHERE 的 SQL 路径使用先过滤、再精确扫描；语义图片服务的 filtered ANN 又有单独合同。这些能力不应互相替代描述。

## 应用负责模型与权限

写入内容和查询必须使用同一模型与预处理。应用还应保存模型版本、生成时间、来源和更新状态；数据库不会因为存有 VECTOR 就自动生成 embedding。

TAG 来源过滤不是安全授权系统。租户、文档 ACL 和数据库权限仍要在明确的服务端边界执行；不能把前端隐藏命中当成隔离证明。

距离阈值与业务相关性需要真实语料、标注和误报评估。本文没有固定“0.2 就非常相似”的通用阈值，也没有以 toy 向量证明真实模型质量。

依据：[vector-search.md](https://github.com/IoTSharp/SonnetDB/blob/main/docs/vector-search.md)。
