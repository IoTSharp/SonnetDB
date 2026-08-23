# M40 Phase 3 #363 离线图算法合同

## 状态

- `✅` Core 功能切片已完成：degree、weakly connected components、PageRank 和确定性 label-propagation community 共用一个可恢复 runner。
- `✅` statement snapshot、CRC sidecar、取消/续作、内存预算、磁盘 spill、Graph/Table 幂等发布和结果版本已覆盖自动回归。
- `📋` Graphalytics/LDBC 子集、1m vertex/10m edge、固定目标硬件、7 天 mixed workload 和 Couplet C4 联合门禁仍归 #367，保持 `NOT_RUN`。
- 本项不改变产品定位；#367 通过前不得宣称生产可用的九模型数据库。

## 公共入口

`GraphStore.RunOfflineAlgorithms` 接收稳定 `GraphOfflineAlgorithmRequest.OperationId`、输出目标和显式预算。一次任务在同一个 source sequence 上生成以下顶点结果：

- `in_degree`、`out_degree` 与 `total_degree`；总度数为前两者之和，自环分别贡献一次入度和一次出度。
- weakly connected component；忽略边方向执行精确 union-find，component ID 为该分量最小的稳定 vertex ID。
- directed PageRank；处理 parallel edge、自环和 dangling vertex，使用全图均匀 dangling redistribution，并以 L1 delta 判断收敛。
- deterministic community；边按无向邻接投票，parallel edge 保留票数，票数相同时优先当前 label、再取较小稳定 vertex ID。达到迭代预算仍未稳定时发布有界近似，并把 `CommunityConverged` 如实置为 false。

`GraphOfflineAlgorithmResult` 返回 source sequence、稳定 `operationId@sequence` 结果版本、顶点/边数量、work unit、PageRank/community 迭代与收敛状态、发布进度、冻结内存预算和 spill bytes。

## 快照与恢复

采集阶段直接读取一个 Graph statement snapshot，并把排序 vertex ID、原始 vertex record 和 dense edge endpoint 写入任务 workspace。跨调用继续采集时，当前 sequence 必须仍等于 sidecar 中的 source sequence；否则抛出 `GraphOfflineAlgorithmSourceChangedException`，不会把两个版本拼成一个输入。

`manifest.sdbgraph` 使用固定 binary header、版本、CRC32、原子替换和目录 fsync。durable checkpoint 边界为：

- vertex/edge 采集页完成后；
- degree/connected-components 完整阶段完成后；
- 每个完整 PageRank/community iteration 完成后；
- 每个 Graph/Table 发布批次完成后。

取消会停止当前扫描页或算法阶段，并保留上一个 durable 边界；未完成的 degree/component 阶段或单次迭代会在续作时从该阶段起点重算，不会把部分状态当成结果。Graph 发布使用由 operation ID 与稳定 batch ordinal 派生的 request ID，可解析 commit-unknown；Table 发布以 `(operation_id, vertex_id)` 主键执行幂等 upsert。

## 内存与 spill

输入始终顺序 spill，不把全图 adjacency 常驻内存。定长 vertex state 在文件尺寸不超过分配预算时使用数组，否则通过固定 8-byte little-endian file-backed vector 随机访问。community vote 按内存预算生成排序 run，单 run 压缩相同 `(vertex,label)` 票，再以最多 32 路的多轮 merge 汇总，不无界打开文件或保留全图 vote dictionary。

`MaxMemoryBytes` 最低 256 KiB，任务首次创建时冻结并写入 sidecar；runner 会在同时打开的 vector 和 sort buffer 间分配该预算。任务完成后删除 input/state/vote spill，只保留带版本与计数的 manifest；Graph properties 或 Table 是唯一结果面，不保留第二份常驻图状态。

## 输出合同

`GraphOfflineAlgorithmTable.CreateSchema` 创建固定结果表：

`operation_id, vertex_id, source_sequence, component_id, page_rank, in_degree, out_degree, total_degree, community_id`

主键为 `(operation_id, vertex_id)`，因此同一 source graph 可保留多个可追溯版本，重试不会追加重复行。

`GraphOfflineAlgorithmGraphOutput` 由调用方显式分配七个互不重复的 property ID。发布复用 `GraphTransaction.UpsertVertex`，保留源 snapshot 的 labels/properties，并追加 component、PageRank、三种 degree、community 与 result version。调用方必须提供完整 vertex unique 声明；结果 property 不得声明为 unique。现有 transaction 合同无法表达多标签 vertex 仅部分 label 生效的 unique property，runner 会明确拒绝该组合并要求改用 Table output。若采集后目标 vertex 被外部写入，乐观 element version 会拒绝覆盖，不静默抹掉新属性。

Graph/Table 发布都是有界批次，不宣称全图结果跨 keyspace 原子。消费者只应在 `IsComplete=true` 后把该 result version 作为完整版本使用。

## 自动回归

`GraphOfflineAlgorithmTests` 覆盖：

- 两个 triangle 与 isolated vertex 的 degree/component/community 结果和 PageRank 总量不变量；
- `MaxWorkUnits=1` 的多次暂停/续作、版本化 Table upsert 和完成后 workspace 清理；
- Graph property 输出保留原属性与 unique owner，进程重开后重复调用不增加 element version；
- 采集期间 source sequence 漂移和 CRC 损坏 manifest 明确拒绝；预取消任务可从 durable manifest 继续；
- 超过内存阈值的 file-backed vector little-endian round-trip。

这些测试是功能与恢复 smoke，不替代 #367 的固定规模容量和长稳报告。
