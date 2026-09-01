# M40 Phase 3 #362 加权路径与批量执行合同

## 状态

- `✅` 功能与本地收益证据已闭环：Core、HTTP、typed SDK correctness smoke 和 topology journey 三算法收益矩阵均已通过。
- `✅` quick smoke 三算法 correctness `PASS`，A* 与双向 Dijkstra 的 expanded-edge/P95 局部准入 `PASS`；固定目标硬件与生产发布证据继续归 #367。
- `📋` Couplet 代码知识真实语料、1m/10m 固定规模、固定目标硬件 P50/P95/P99、退化矩阵、7 天 mixed workload 和 #367 发布门禁尚未完成。
- 本合同不改变九模型中的 Graph Beta 定位；在 #367 通过前仍不得宣称图能力 Production 就绪。

## 公共入口

- `GraphReadSession.WeightedShortestPath`：按选项执行 Dijkstra、A* 或双向 Dijkstra。
- `Dijkstra`、`AStar`、`BidirectionalDijkstra`、`ShortestPathWeighted`：相同执行器的便捷入口。
- `GraphAlgorithmExecutor.ExecuteShortestPaths` / `RunShortestPaths`：在同一 `GraphReadSession` snapshot 上按输入顺序执行批量查询。
- `POST /v1/db/{db}/graphs/{graph}/weighted-shortest-path` 与 `SndbGraphClient.WeightedShortestPathAsync`：source-generated JSON 的远程合同。

`GraphWeightedPath` 返回路径、总权重、实际算法、snapshot sequence、expanded vertex state 数和 expanded edge 数。不可达返回 `null`；远程请求中端点不存在沿用现有 shortest-path 行为返回 `404 vertex_not_found`。

## 权重与算法

- 远程合同从一个正数 property ID 读取边权；属性必须是 `Int64` 或 `Float64`。
- 嵌入式 API 还可提供 `Func<GraphEdge, double>` selector；property ID 与 selector 不能同时设置。
- 所有边权必须有限且非负。缺失、错误类型、NaN、Infinity、负值和累加溢出分别稳定失败，不跳过坏边伪造结果。
- Dijkstra 是默认算法。
- A* 是显式 opt-in；省略 heuristic 时按零启发式运行，等价于 Dijkstra。调用方负责 heuristic 可采纳且一致，Core 只校验其有限、非负及优先级不溢出。
- 双向 Dijkstra 从目标按反方向 adjacency 扩展；`Outgoing` 对应反向 `Incoming`，`Incoming` 对应反向 `Outgoing`，`Both` 保持 `Both`。

## 深度与正确性

`MaxDepth` 是路径 hop 的语义约束，不只是循环保护。搜索状态必须是 `(vertex, depth)`：同一顶点上“更便宜但已耗尽 hop”的状态不能遮蔽“更贵但仍可到达目标”的状态。实现只删除被“更少或相同 hop 且成本更低或相同”支配的状态。

双向算法保存 forward/backward 两侧的 depth state；meeting 只有在两侧 depth 之和不超过 `MaxDepth` 时才合法。路径重建分别使用 forward predecessor 和 backward successor 链，并保持请求方向。

三种算法都只读取创建 `GraphReadSession` 时的单一 KV sequence。搜索过程中并发提交只对后续 session 可见，不会改变当前结果。

## 预算与取消

- `MaxDepth`：最大 hop 数。
- `MaxFrontier`：排队 state 上限；双向算法计算两侧总和。
- `MaxVisitedVertices`：发现的不同顶点上限。
- `MaxExpandedEdges`：检查过的邻接边总上限。
- `MaxTotalWeight`：可选成本上限，超过上限的候选不进入 frontier。
- `PageSize` / `MaxPageBytes`：复用有界 Graph adjacency cursor。
- cancellation 在 frontier 取项、分页读取和逐边处理处检查；取消后不返回部分路径。

## 当前证据

自动回归覆盖：

- 总权重优先于 hop 数的路径选择。
- A*、双向 Dijkstra 与 Dijkstra 结果对拍。
- outgoing/incoming、深度状态、不可达和零长度路径。
- 负权、缺失/错误类型、溢出、取消和批量输入顺序。
- 固定随机有向图上 Dijkstra/双向 Dijkstra 与最大 4 hop 穷举 oracle 对拍。
- 远程 typed SDK 经真实 HTTP endpoint round-trip，并保留 snapshot sequence 和诊断字段。

### 本地 topology 收益矩阵（2026-08-22）

运行入口：`dotnet run --project tests/SonnetDB.Benchmarks/SonnetDB.Benchmarks.csproj -c Release -- --m40-weighted-path-evidence --quick`。

该 runner 使用固定 seed `0x534F4E4E45544442` 的 `gj-topology-weighted-route-v1` 缩小拓扑（256 vertex、960 directed edge、16x16 网格），在同一 statement snapshot 上完成 5 次正式样本；输入摘要、机器信息、path digest、分配、working set、P50/P95/P99、expanded vertices/edges 和实际 `native_adjacency` path 写入 source-generated JSON/Markdown。quick 结果如下：

| 算法 | 正确性 | expanded edges | 相对 Dijkstra | P95 | P95 ratio | 局部准入 |
|---|---|---:|---:|---:|---:|---|
| Dijkstra | PASS | 704 | baseline | 4.437 ms | 1.000x | BASELINE |
| A*（Manhattan） | PASS | 59 | -91.6% | 2.065 ms | 0.465x | PASS |
| Bidirectional Dijkstra | PASS | 423 | -39.9% | 3.262 ms | 0.735x | PASS |

A* 样本的启发式由嵌入式调用方提供，满足非负、可采纳和一致约束；远程零启发式 A* 不从该样本外推收益。该矩阵只证明当前缩小 topology 上的局部算法准入，不改变发布边界。

尚未取得的证据必须保持 `NOT_RUN`：Couplet 代码知识真实语料、#341 gate 档 1m vertex/10m edge、固定目标硬件 P50/P95/P99 与峰值内存、退化/负向 workload、7 天 mixed workload 和 #367 发布决定。
