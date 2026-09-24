# VectorData 生命周期预检与已加载图重建（M36 #321）

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

// 先由应用显式打开集合，确保 health.State == "loaded"。
// 重建只读取已有持久向量，不重新生成 embedding 或修复主文档/KV。
var rebuild = vectors.StartIndexGraphRebuild(
    "documents", "embedding_index", cancellationToken);
var progress = rebuild.Progress; // 无需等待索引锁，可由另一个线程轮询。
var completed = await rebuild.Completion;
```

`PreflightAsync` 从真实索引声明读取维度和度量，检查查询维度、有限数值和余弦非零范数。检查最多 65536 维，接受取消，并设置 30 秒协作超时。普通 collection 没有持久模型绑定，因此返回 `IsProfileCompatible = null` 和 `profile_unbound`；索引兼容不能推导为模型兼容。

已有 M35 RAG stream 使用 `PreflightRagAsync(stream, queryProfile, queryVector, cancellationToken)`。该入口从 `RagIngestionManager` 读取已发布完整 profile，取得 generation 租约后核对身份和 revision，再读取该 generation 的实际向量索引。两次读取之间发生发布切换会拒绝本次预检；报告中的 generation 身份仅代表此次检查，不承诺后续独立查询仍使用同一代。

完整 profile 比较沿用 M35 严格合同，包含 Id、provider、model、revision、dimensions、metric、normalization、模态序列和外发策略；模态顺序变化也视为合同变化。若 profile 声明 L2 归一化，查询向量的平方范数与 1 的误差不得超过 0.001。`verified` 只表示合同一致：fixture、hash、合成模型与真实模型都不能凭该字段取得质量、成本或 Recall 证据。

健康读取要求连接已打开，以免查询状态时隐式执行数据库恢复。它不会打开尚未加载的 collection，不调用 `CountPrefix`、扫描主文档、清理 TTL 或触发派生索引重建。`loaded` 提供当前图的有效向量数；重开后尚未加载的索引返回 `not_loaded` 和空计数。这是运行状态快照，不能证明主数据一致性、TTL 新鲜度、Recall 或容量；读取会遵守现有索引同步锁，不承诺重建期间的实时进度。

`StartIndexGraphRebuild` 显式启动**已加载 HNSW 图重建**，要求嵌入式连接、集合和索引均已加载；冷集合、缺失索引与远程连接在调度前拒绝。Core 对应入口是 `DocumentCollectionManager.StartVectorGraphRebuild`。它读取既有持久向量 KV 的稳定快照，使用现有 HNSW 创建候选图，全部构建成功后才在索引锁内替换旧图。它不扫描主文档、不改写向量 KV、不清理主文档 TTL、不生成 embedding，也不重建 M35 的 generation/profile。持久 key 的无效 UTF-8、向量维度损坏、非有限数值或余弦零范数会拒绝候选图；不能通过跳过坏向量宣称成功。

图重建与现有 `RebuildVectorIndex` 的主文档/KV 修复是两个明确的操作。后者先删除旧目录、同步重建持久向量，仍没有安全可取消的持久状态替换合同。本入口不能替代该修复，也不能证明索引与主文档一致、Recall 或 TTL 新鲜度。

每个操作持有独立的 `Progress` 和 `Completion`。进度是通过真实处理计数发布的不可变快照，读取不获取索引锁；状态依次包含 `queued`、`waiting_lock`、`snapshotting`、`building`、`publishing` 及 `completed` / `canceled` / `failed`。`ScannedEntries` 是已检查条目数，`IndexedVectors` 是已加入候选图的向量数，没有预扫描总量或虚构百分比。操作和进度只在进程内存在，不是持久任务，重启不会恢复任务 ID。`Completion` 传播原始异常；失败进度仅包含 `invalid_vector_data`、`storage_error`、`index_disposed` 或 `graph_rebuild_failed` 分类，不携带文档内容、路径或异常消息。取消则返回 `operation_canceled` 并使任务进入 Canceled 终态。

构建持有该向量索引锁；查询、写入、删除、原重建、集合/索引删除和释放都按既有锁顺序等待。等待这些操作时上层集合或 manager 可能持有自己的锁，因此相关调用也可能等待；本切片不是在线无阻塞重建。同一索引拒绝重叠的图重建。每次等待索引锁最多 50 毫秒后检查取消，每页和每个向量之间也检查；快照覆盖层排序、一次磁盘读取和单个 HNSW 插入不能被中断。发布前取消或失败释放候选图并保留旧图，发布后取消不回滚。调用方须等待 `Completion` 再关闭连接，释放与构建并发时会等待；若索引在排队阶段已经释放，操作以 `index_disposed` 失败。

读取按每页 256 条和 4 MiB payload 限额进行，单条超过限额失败；KV 快照仍遵守 `MaxSnapshotOverlayEntries`，超限失败保留旧图，不隐式 checkpoint。旧图、候选图及快照覆盖层可能同时驻留；分页只约束读取页，不能理解为总内存上限或容量证据。

诊断 DTO（含 `DocumentVectorGraphRebuildProgress`）使用公开 `SndbVectorLifecycleJsonContext` 的 source-generated 类型信息。该切片不修改二进制格式，也不新增持久 profile/catalog。

此前预检切片的本地验证：新增 19 项生命周期用例，与当时的向量索引、VectorData、RAG 查询/摄取和 public API 兼容回归合计 82/82 通过；Core、Data、CLI 与 Server Release 构建通过，Server 启用的 IL/AOT 分析为零警告。此历史记录不包含 NativeAOT 发布、远程 parity 或真实模型质量验证。

本次图重建新增 21 项用例，覆盖空/单/跨页、成功后重开、取消与重试、等待索引锁取消、损坏向量/key、快照预算、注入 IO 失败、独立进度、并发写入/删除/释放/索引删除、排队后释放、冷集合与远程拒绝及 JSON round-trip。与现有 VectorData、Document 向量、RAG 查询/摄取和 public API 回归合计 **127/127** 通过，Core/Data/CLI Release 构建通过。Core/Data 额外执行 `dotnet build src/SonnetDB.Data/SonnetDB.Data.csproj -c Release -t:Rebuild -p:EnableAotAnalyzer=true -p:EnableTrimAnalyzer=true`，0 警告、0 错误；没有把 Data 默认关闭 AOT 分析的普通 Release 构建当作 AOT 证据。本记录不包含 NativeAOT 发布、远程 parity、真实模型或固定硬件验证。

尚待完成：从主文档安全修复持久向量 KV 的可取消重建与恢复合同、远程 lifecycle transport、通用 VectorData 的 ANN/scan/精确补偿执行解释及 Recall/固定硬件容量报告。M35 #298 的 filtered ANN 和解释仍属于其既有语义内容路径，不被本切片重新宣称为通用 VectorData 能力。
