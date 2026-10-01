# M42 measurement KNN 执行期候选边界

`knn(measurement, vector_field, query_vector, k[, metric])` 和 measurement hybrid search 共用 `KnnExecutor`。此前各扫描 worker 先建立完整候选 `List`，再汇入全量列表，最后才过滤墓碑和选 K。最终堆有界并没有限制前面的候选保留量。本切片把准入移到扫描阶段；已有 Document `vector_search(...)` 的有界默认排序没有重新实现。

## 执行合同

- 所有 worker 共用一个最多 K 项的最大堆，不保留 worker 本地候选列表。精确扫描候选处理为 O(N log K)，候选状态为 O(K)，结束出堆为 O(K log K)。并行写入通过堆锁合并；更快的扫描不承诺消除该锁的竞争。
- 排序按距离、时间戳、series ID 递增，距离相同时选取最早时间，再选较小 series ID；调度顺序不会改变并列结果。ANN 的确定排序仅针对其实际返回的候选集合，不宣称 ANN 等同完整精确 oracle。
- 墓碑在候选进入堆前过滤，因此删除的最近邻不会占满 K 后再被移除。向量替换仍通过既有可见点查询器读取；没有修改替换、去重或持久化格式合同。
- 度量一致且无该 series/field 墓碑时保留既有段 ANN。时间窗内 ANN 候选不足 K、且 ANN 没有覆盖整个 block 时，丢弃本次 ANN 结果并精确扫描时间窗。不会把相同点分别作为 ANN 命中与补扫候选重复加入。`k * 8` 的候选预算计算使用 64 位中间值，避免大 K 溢出。
- 使用既有 `SqlParallelExecution` 的实例级 worker 槽位、每 worker 内存预留和串行回退。初始收益估算使用当前 SQL 估算与 MemTable 点数；冷段没有充分估算时保守串行，不新增全段扫描来取得并行估算。
- 每个真实保留项按 256 字节保守估算计入单查询和实例共享 SQL 内存预算；替换现有候选不再次扣减。K 不会触发预分配 K 项，实际不足 K 时只预留实际保留项。准入失败抛出 `InvalidOperationException`，拒绝查询并释放预留，不返回部分成功。worker、候选预留和链接取消计时器通过现有执行范围或 `using` 回收。
- SQL 根取消与内部显式取消共同生效。扫描 series、MemTable 点、段/ANN 候选、精确补扫及结果出堆都检查取消。已开始的存储解码或索引内部调用仍在其安全边界返回后观察取消，不提供硬中断。

## 本地回归与分配证据

专项回归位于 `KnnBoundedExecutionTests` 和 `SqlKnnBoundedQueryTests`，并复验既有 `KnnExecutorTests`、`SqlExecutorKnnTests`、向量替换/恢复和 hybrid search 测试。覆盖精确 oracle、并列键、并行全局 Top-K、ANN 时间窗补偿、取消、预算失败/重试、内存和持久段墓碑补足、巨大 K 与 worker/预算归还。

固定分配回归采用 K=8，先预热 256 个逐步改善的候选，再送入 100,000 个持续替换候选。使用 `GC.GetAllocatedBytesForCurrentThread()` 测量这段候选处理的新增托管分配，输出原始字节数并要求不超过 4,096 字节。此输入持续触发候选替换，不只测量被直接丢弃的较差候选。循环最多 100,256 项并有 10 秒协作取消；SQL 语料最多 64 点，有 30 秒协作取消。

可复验命令：

```text
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~KnnExecutorTests|FullyQualifiedName~KnnBoundedExecutionTests|FullyQualifiedName~SqlKnnBoundedQueryTests|FullyQualifiedName~SqlExecutorKnnTests|FullyQualifiedName~SqlVectorParameterTests|FullyQualifiedName~HybridSearch"
```

2026-10-01 统一定向回归 250/250 通过，其中本片新增 Query 9/9、SQL 4/4。上述 K=8、100,000 次替换测得额外托管分配为 0 字节；只证明这段预热后的候选堆处理。运行结果与证据见[第二批闭环记录](../audits/roadmap-four-task-closure-20261001.md)。

## 证据边界

O(K) 只针对候选保留状态。`MemTable.SnapshotRange`、段解码、ANN 候选/索引内部状态、匹配 series、`MapOrdered` 的每 series 结果数组和最终 SQL 行物化仍沿用现有合同，可能产生与输入页、series 数或结果字段大小相关的分配。256 字节是候选准入估算，不是整个查询的 CLR heap 硬上限，也不是关系 `MaxMaterializedRows`/`MaxMaterializedBytes` 支持范围的扩展。

固定向量回归只证明本地执行和资源合同；真实模型质量、Recall@K、统一语料吞吐/尾延迟、冷启动、固定 x64/ARM64 硬件、168 小时与 M42 系统性能退出条件继续待验。
