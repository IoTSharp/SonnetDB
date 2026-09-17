# M35 #298：USearch filtered ANN 与查询预算

语义图片的文本、图片与 similar-by-id 查询共用过滤合同。source bucket、metadata 与 tag 的索引先产生允许的 ID；`Backend=usearch` 或受支持平台上的 `auto` 现在将这些 ID 传入 USearch 原生 `usearch_filtered_search`，不再必须转为 managed HNSW。similar-by-id 的源 ID 在进入原生过滤前排除。

USearch 2.26.0 NuGet 的公开 C# 包装没有 filtered API。Server 的内部包装使用该包现有的原生资产，签名依据固定版本的 [C header](https://github.com/unum-cloud/usearch/blob/v2.26.0/c/usearch.h)。它用 SafeHandle 管理索引、以 `nuint` 表达 `size_t`、以 `int` 表达过滤回调的返回值，并复用该版本的 `IndexOptions`。支持范围仍是 Windows x64、Linux x64 和 macOS ARM64。没有新增依赖或持久化格式。

原生回调只接纳允许的 key。回调异常、取消和预算耗尽均在原生调用返回后处理，异常不会跨越 C ABI。允许的 ID 缺失、ANN 返回不足或回调候选预算耗尽时，丢弃该次 ANN 结果并从权威 Document 精确补偿；不返回截断成功。`FallbackToManaged=false` 禁止原生不可用时切换 managed ANN，但不禁止权威数据的精确扫描。

## 部署配置

以下配置位于 `SonnetDBServer:SemanticSearch:Query`。服务启动时复制并限制范围，运行中的预算不会被可变配置对象意外修改。

```json
{
  "SonnetDBServer": {
    "SemanticSearch": {
      "Query": {
        "AnnCandidateLimit": 512,
        "ExactCompensationLimit": 4096,
        "CandidatePageSize": 256,
        "MaxScannedCandidates": 100000,
        "TimeoutMilliseconds": 30000
      }
    }
  }
}
```

| 配置 | 范围 | 行为 |
| --- | --- | --- |
| `AnnCandidateLimit` | 1–100000 | managed filtered ANN 的候选预算；USearch 的 search expansion 与过滤回调接纳预算。耗尽时改为精确补偿。 |
| `ExactCompensationLimit` | 0–100000 | 允许物化的过滤 ID 数量。超过后直接使用分页精确扫描；0 强制过滤查询采用分页扫描。 |
| `CandidatePageSize` | 1–4096 | 预过滤、候选索引和精确回退的单页读取数量。 |
| `MaxScannedCandidates` | 1–10000000 | 同一次查询累计扫描行数；预过滤、精确补偿和首次 USearch 重建共同扣减。同一文档被不同阶段读取时分别计数。 |
| `TimeoutMilliseconds` | 1–600000 | 检索阶段的协作超时；不包含 embedding 生成。 |

USearch 的过滤回调不能中断原生图遍历，因此 expansion/回调预算不等于距离计算次数的硬上限，超时也不是原生调用的硬中断。取消或超时会拒绝后续候选，并在原生函数返回时丢弃结果。managed ANN 与存储调用也在安全边界观察取消。

USearch 首次重建不再使用全量 `Scan()`，而是按 256 行分页。查询触发的重建参与共享扫描/时间预算；写入触发的重建另有 1000 万行与 30 秒协作上限。失败时释放半建索引，下次请求可以重试。

## 响应与证据边界

候选扫描或时间预算不足返回 HTTP 503，错误码为 `semantic_query_budget_exceeded`，不会返回部分 hits。允许精确回退时，`backend=exact-filtered`；解释请求继续提供 `searchMode`、`candidateCount`、`filteredCandidateCount`，新增可选 `fallbackReason` 表明补偿或回退原因。没有请求解释时不返回该字段。

回归使用固定向量与测试 embedding provider 验证过滤、源 ID 排除、索引漂移、更新/删除、取消、预算失败、回调异常与 ABI 布局。这些是执行合同证据，不是实际模型语义质量、固定硬件 recall、延迟或容量报告；后者仍待完成。
