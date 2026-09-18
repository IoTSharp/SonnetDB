# VectorData 生命周期预检（M36 #321 首切片）

`SonnetDBVectorStore` 的生命周期扩展复用实际 Document catalog 和 M35 持久 RAG profile。当前切片仅支持嵌入式连接；远程调用明确返回 `NotSupportedException`，不尝试猜测服务端状态。

```csharp
using SonnetDB.Data;
using SonnetDB.Data.VectorData;
using SonnetDB.Query;

await using var connection = new SndbConnection("Data Source=./data");
await connection.OpenAsync(cancellationToken);
using var vectors = new SonnetDBVectorStore(connection);

var preflight = await vectors.PreflightAsync(
    "documents", "embedding_index", queryVector, KnnMetric.Cosine, cancellationToken);
var health = await vectors.GetIndexHealthAsync(
    "documents", "embedding_index", cancellationToken);
```

`PreflightAsync` 从真实索引声明读取维度和度量，检查查询维度、有限数值和余弦非零范数。检查最多 65536 维，接受取消，并设置 30 秒协作超时。普通 collection 没有持久模型绑定，因此返回 `IsProfileCompatible = null` 和 `profile_unbound`；索引兼容不能推导为模型兼容。

已有 M35 RAG stream 使用 `PreflightRagAsync(stream, queryProfile, queryVector, cancellationToken)`。该入口从 `RagIngestionManager` 读取已发布完整 profile，取得 generation 租约后核对身份和 revision，再读取该 generation 的实际向量索引。两次读取之间发生发布切换会拒绝本次预检；报告中的 generation 身份仅代表此次检查，不承诺后续独立查询仍使用同一代。

完整 profile 比较沿用 M35 严格合同，包含 Id、provider、model、revision、dimensions、metric、normalization、模态序列和外发策略；模态顺序变化也视为合同变化。若 profile 声明 L2 归一化，查询向量的平方范数与 1 的误差不得超过 0.001。`verified` 只表示合同一致：fixture、hash、合成模型与真实模型都不能凭该字段取得质量、成本或 Recall 证据。

健康读取要求连接已打开，以免查询状态时隐式执行数据库恢复。它不会打开尚未加载的 collection，不调用 `CountPrefix`、扫描主文档、清理 TTL 或触发派生索引重建。`loaded` 提供当前图的有效向量数；重开后尚未加载的索引返回 `not_loaded` 和空计数。这是运行状态快照，不能证明主数据一致性、TTL 新鲜度、Recall 或容量；读取会遵守现有索引同步锁，不承诺重建期间的实时进度。

诊断 DTO 使用公开 `SndbVectorLifecycleJsonContext` 的 source-generated 类型信息。该切片不修改二进制格式，也不新增持久 profile/catalog。

本地验证：新增 19 项生命周期用例，与现有向量索引、VectorData、RAG 查询/摄取和 public API 兼容回归合计 82/82 通过；Core、Data、CLI 与 Server Release 构建通过，Server 启用的 IL/AOT 分析为零警告。此记录不包含 NativeAOT 发布、远程 parity 或真实模型质量验证。

尚待完成：安全可取消重建与真正可观察进度、远程 lifecycle transport、通用 VectorData 的 ANN/scan/精确补偿执行解释及 Recall 报告。M35 #298 的 filtered ANN 和解释仍属于其既有语义内容路径，不被本切片重新宣称为通用 VectorData 能力。
