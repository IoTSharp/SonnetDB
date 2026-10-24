---
title: SonnetDB 过滤检索实操：文档 SQL、图片预过滤与查询预算
categories: SonnetDB
tags: 向量检索,过滤,查询预算
draft: false
---

# SonnetDB 过滤检索实操：文档 SQL、图片预过滤与查询预算

SonnetDB 的文档 SQL 向量检索和图片语义检索都能带过滤条件，但它们使用不同的执行路径。本文用一个三文档样例和一份图片查询请求，说明怎样核对过滤顺序、精确回退、查询解释与预算失败，避免把图片服务的 filtered ANN 能力套到通用 SQL 上。

![SonnetDB 文档 SQL 与图片语义过滤的两条路径](https://img2024.cnblogs.com/blog/1725297/202610/1725297-20261017120304215-1915955520.png)

图：AI 主题示意图，并非实测截图；两条路径分别核对，预算数字不是性能承诺。

## 先固定版本和验证范围

本文于 2026-10-17 对照源码提交 `dcadffc7137f5653ec46c55a4171cf5f2d83de03` 复核；下方公开源码链接固定到已可访问的 `5424b66672e37f49a2a5633fdde0b54a9b1f0290`，相关文件内容已在本次发布前核对。正式 GitHub Release 和 NuGet `SonnetDB.Core` 当前核到 4.0.0。源码中的增量能力不因此自动成为 4.0.0 包能力；使用发行包时先确认实际版本包含所需入口。

以下示例是按源码合同整理的操作步骤，本轮未启动数据库、模型或图片服务执行。三维数字是人工构造的距离样例，不能说明真实模型效果、Recall、延迟或容量。Graph 仍为 Beta，不属于本文的图片/文档验收范围。

## 第一步：建立一个能看出过滤差异的文档集合

在包含对应能力的 SonnetDB SQL 会话中，为实验使用独立数据库目录，执行：

```sql
CREATE DOCUMENT COLLECTION FilterDocs;

INSERT INTO FilterDocs (id, document)
VALUES ('a', '{"site":"south","embedding":[1,0,0]}');
INSERT INTO FilterDocs (id, document)
VALUES ('b', '{"site":"north","embedding":[0.8,0.6,0]}');
INSERT INTO FilterDocs (id, document)
VALUES ('c', '{"site":"north","embedding":[0,1,0]}');

CREATE VECTOR INDEX idx_filter_embedding
ON FilterDocs ('$.embedding')
WITH (dimensions = 3, metric = 'cosine', m = 16,
      ef_construction = 200, ef_search = 64);
```

这里 `a` 与查询向量方向相同，却属于 south；`b`、`c` 才是 north。这样可以检查“先取全局最近两个，再删掉 south”为什么可能少返回一条：全局窗口可能已经挤掉本应进入过滤后 Top-K 的 `c`。

## 第二步：对照无过滤和带 WHERE 的计划

```sql
EXPLAIN SELECT id, vector_distance() AS distance
FROM vector_search(
  source => FilterDocs,
  vector_field => '$.embedding',
  vector => [1,0,0], k => 2, metric => 'cosine'
)
ORDER BY distance;

EXPLAIN SELECT id, vector_distance() AS distance
FROM vector_search(
  source => FilterDocs,
  vector_field => '$.embedding',
  vector => [1,0,0], k => 2, metric => 'cosine'
)
WHERE site = 'north'
ORDER BY distance;
```

去掉 `EXPLAIN` 才会读取结果。核对 `access_path` 与 `index_name`：满足向量路径、维度、metric 和排序条件的无 WHERE 查询可以命中 `document_vector_index`；当前带 WHERE 的这一路使用 `document_vector_scan`。对本例元数据谓词，执行器先过滤文档再保留最近结果，按余弦距离应由 `b` 排在 `c` 前。不要把这个按合同推导的结果标成本文实测输出。

创建 HNSW 索引不意味着任意 WHERE 都会成为 filtered ANN。当前 SQL 执行器对受支持的元数据过滤有提前判断和有界保留路径，但没有自动调用图片服务的过滤 ID/USearch 管线；SQL 的物化预算也有自己的受支持语句子集。

## 第三步：图片服务单独准备模型和数据

图片路径需要显式启用语义检索、提供匹配 profile 的模型文件和 tokenizer，并把图片摄取到当前数据库。Server 不会自动下载模型；人工三维向量不能冒充 SigLIP2 的图片 embedding。

先读取 `/v1/semantic-search/status`，核对 configured/effective backend。图片摄取和删除需要数据库写权限，搜索需要读权限。确认 `camera-images` 已有相应图片及 metadata/tag 后，向本机实验服务提交：

```http
POST /v1/db/demo/images/search/text
Authorization: Bearer <实验账号的token>
Content-Type: application/json

{
  "text": "红色重型卡车",
  "topK": 10,
  "filter": {
    "sourceBucket": "camera-images",
    "sourceKeyPrefix": "lane-01/",
    "contentType": "image/jpeg",
    "metadata": { "site": "north" },
    "tags": { "vehicleType": "truck" }
  },
  "explain": true
}
```

metadata/tag 是键值精确匹配，多个条件共同成立；不是自然语言分类器。source bucket、metadata、tag 可产生允许 ID 的候选入口，再由残余条件复核；单独的 key prefix 或 content type 没有同样的 path prefilter，会转分页精确路径。

## 第四步：读取解释字段，区分 ANN 与精确补偿

当前 `SemanticImageSearchService` 和 M35 #298 合同已经包含 USearch 原生 filtered search。`semantic-image-search.md` 中仍有较早的“过滤仅 managed”段落；不能照抄它作为当前后端限制，需结合源码和后续合同判断。

| 核对项 | 应怎样理解 |
|---|---|
| `backend` | 实际使用的后端；精确过滤回退可为 `exact-filtered` |
| `searchMode` | 区分 `ann`、`prefiltered-ann`、补偿和精确回退 |
| `candidateCount` / `filteredCandidateCount` | 解释候选规模，不是 Recall 或距离计算总次数 |
| `fallbackReason` | 在请求解释时说明候选不足、索引漂移或物化预算等原因 |

派生 ANN 缺少允许 ID、候选不足或回调候选预算耗尽时，服务可丢弃 ANN 结果，从权威 Document 数据精确补偿。`FallbackToManaged=false` 限制原生不可用时切换 managed ANN，不禁止权威数据精确扫描。similar-by-id 会在原生过滤前排除源 ID。

因此看见 `backend=exact-filtered` 不能直接判定丢数据；要结合 searchMode、fallbackReason 和查询是否完整成功。

## 第五步：用预算失败验证错误处理

下面是 `SonnetDBServer:SemanticSearch:Query` 的合同示例，供实验配置核对：

```json
{
  "AnnCandidateLimit": 512,
  "ExactCompensationLimit": 4096,
  "CandidatePageSize": 256,
  "MaxScannedCandidates": 100000,
  "TimeoutMilliseconds": 30000
}
```

这些数字是预算配置，不能写成吞吐量或容量测试结果。`ExactCompensationLimit=0` 可让过滤查询采用分页扫描；扫描预算会累计预过滤、补偿及查询触发的 USearch 首次重建，同一文档在不同阶段被读取会分别计数。时间预算不包含 embedding 生成。

在独立实验配置中有意缩小扫描预算后，超预算应得到 HTTP 503 和 `semantic_query_budget_exceeded`，不应把部分 hits 当完整成功。客户端应把这种失败与正常空结果分开，不自动扩预算并无限重试。

超时是协作取消。USearch 回调不能强行中断原生图遍历，候选预算不是距离计算次数的硬上限；取消后须等原生调用返回才能丢弃结果。不要把“30 秒预算”宣传为整个 HTTP 请求的绝对墙钟上限。

## 留下可复核的实验记录

记录源码/包版本、profile、过滤条件、SQL explain 或图片解释字段、完整成功/明确失败状态；另记真实模型和目标硬件信息。本文只提供操作合同，没有执行产品测试、真实模型质量、固定硬件性能、恢复或长期生产验证。

参考：[SQL 文档向量检索](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/sql-reference.md)、[过滤 ANN 与预算](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/m35-filtered-search-budgets.md)、[图片服务源码](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB/SemanticSearch/SemanticImageSearchService.cs)。
