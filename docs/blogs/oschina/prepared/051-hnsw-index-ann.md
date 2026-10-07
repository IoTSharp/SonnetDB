---
title: 'SonnetDB HNSW 索引架构：m/ef 参数调优与 .SDBVIDX 文件格式'
categories: SonnetDB,数据库
draft: false
---

正式发行见 [4.0.0 Release](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。本文按 2026-10-07 当前 main 文档与源码修订旧稿；主线改动不自动代表全部 v4 发行物与客户端的已验收能力。以下 SQL/API 为复现示例，本次没有实际执行或性能测量。

HNSW 用分层近邻图减少候选搜索量。在 SonnetDB 中，measurement 段索引和 Document Collection 的持久索引是不同存储入口，参数与文件描述应分别核对。

## Measurement 声明

```sql
CREATE MEASUREMENT documents (
  source TAG,
  embedding FIELD VECTOR(3)
    WITH INDEX hnsw(m=16, ef=64, ef_construction=200, metric='cosine')
);
```

m 影响图的连接程度，ef_construction 影响构建候选，ef 影响查询候选。增大参数通常增加构建、内存或查询成本；它们不是固定召回率的保证。Document DDL 使用 ef_search 等字段，不能照搬 measurement 参数名称。

## .SDBVIDX 的现状

旧资料把 .SDBVIDX 描述成独立 sidecar；当前 v6 新段将向量索引 section 内嵌在 .SDBSEG 扩展区，并保留 legacy sidecar 读取路径。SegmentVectorIndexFile 当前使用 SDBVIDX2 magic、section format version 4，不能混称为主文件版本。

该 section 保存 block manifest 与本地向量索引 blob。索引是可从主数据重建的派生结构，不是业务向量的唯一持久化来源；缺失或损坏时应按具体读取/重建合同处理。

## 查询不能只看 DDL

索引 metric 和 dimension 必须匹配查询，过滤或数据状态不适合时可能回退或补偿。测量应分别记录建图时间、索引大小、恢复、候选数量、召回和延迟，不能由 m/ef 示例推断实际吞吐或全进程固定内存。

依据：[vector-search.md](https://github.com/IoTSharp/SonnetDB/blob/main/docs/vector-search.md)、[SegmentVectorIndexFile.cs](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Storage/Segments/SegmentVectorIndexFile.cs)、[HnswOptions.cs](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Vector/Index/Hnsw/HnswOptions.cs)。
