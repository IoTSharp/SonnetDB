# M40 #360 Graph Statement Snapshot 与并发写合同

## 1. 决策

原生 Graph 读使用 statement snapshot。`GraphStore.BeginRead` 在短锁内获取一个
`KvReadSnapshot`，固定创建时已经提交的 key/value、KV version 和 sequence；
`GraphReadSession` 的点读、索引 seek、Expand、BFS/DFS/path 以及从该会话创建的所有 cursor
都只能看到这个 snapshot。

当前不增加跨多个 `GraphReadSession` 或读写 statement 的 snapshot-isolation transaction。
#341 的 social、topology、evidence 与 Couplet journey 需要单次遍历内一致，而没有证明长事务 MVCC
的收益足以承担 version retention、冲突检测、checkpoint/compaction 和恢复复杂度。

## 2. 可见性合同

| 并发事件 | 当前 statement | 后续 statement | writer 结果 |
|---|---|---|---|
| 创建 vertex/edge | 不可见 | 可见 | 不被现有读 cursor 阻塞 |
| 更新 vertex/edge/property | 保留旧 element 与旧索引/邻接视图 | 看见完整新原子 batch | 按 element version 条件提交 |
| 删除 edge 或无邻接 vertex | 仍可读旧 record/adjacency | 完整不可见 | 按 record version/RESTRICT 条件提交 |
| 原子 re-parent | 整条路径只见提交前拓扑 | 整条路径只见提交后拓扑 | 不出现半条旧边或半条新边 |

Cursor 获取同一 snapshot 的独立 lease。释放 `GraphReadSession` 后，已经创建的 cursor 仍可读到
结束；cursor 只保留不可变 overlay 与 disk generation lease，不持有 Graph commit gate、store lock
或 KV mutation lock。单次 acquire 复制的 overlay 继续受 `MaxSnapshotOverlayEntries` 限制。

## 3. 并发写矩阵

| writer A | writer B | 允许结果 | 原子条件 |
|---|---|---|---|
| 更新 element A | 更新 element B，且不推进同一 metadata high-water | 两者都提交 | 各自 record key version |
| 更新同一 element/version | 更新同一 element/version | 恰有一个提交 | record key version |
| claim 同一 unique property value | claim 同一 unique property value | 恰有一个提交 | unique owner key version |
| 创建指向 vertex V 的 edge | 删除 V | 恰有一个提交 | endpoint record version + adjacency `PrefixEmpty` |
| 相同 request ID/相同 digest | 相同 request ID/相同 digest | 一个新提交，一个 duplicate resolve | request marker key version |
| 相同 request ID/不同 digest | 相同 request ID/不同 digest | 一个提交，另一个 request conflict | request marker digest |

推进同一个 vertex/edge/label/property high-water 的事务可能发生 metadata 乐观冲突，即使 element
不同；调用方按稳定 request ID 重试。此行为保证 high-water 单调，不用 graph-wide 长写锁掩盖冲突。

## 4. 诊断

原生 `EXPLAIN GRAPH_TABLE` 返回：

- `read_consistency=statement_snapshot`
- `accessor=NativeGraphAccessor`

原生 `EXPLAIN ANALYZE GRAPH_TABLE` 额外返回：

- `actual_read_consistency=statement_snapshot`
- `actual_snapshot_sequence=<KV sequence>`

关系映射图当前返回 `relation_accessor_current`，`actual_snapshot_sequence` 为 null。关系 table/KV
统一 statement snapshot 属于 M41 #374；M40 不复制关系 MVCC，也不把 current accessor 虚报为 snapshot。

## 5. 自动验收

`GraphStatementSnapshotTests` 固定以下结果：

1. page size 为 1 的 BFS 在并发 re-parent 后继续返回完整旧路径，writer 在旧 cursor 存活时完成，
   下一读会话只返回新路径。
2. 同一 session 在并发提交后新建的 cursor 仍与 session/cursor 的 `SnapshotSequence` 一致。
3. 不相交 element 更新无死锁或饥饿并全部提交。
4. unique claim 和 endpoint delete/edge insert 竞态都恰有一个提交。
5. 每组竞态结束后 `GraphInvariantChecker` 保持零 orphan、双向 adjacency mismatch 和 index drift。

这些是 #360 的功能关闭证据，不替代 #367 的 7 天 mixed workload、固定硬件、kill/reopen、
backup/restore、Native AOT 或 Couplet C4 联合发布门禁。
