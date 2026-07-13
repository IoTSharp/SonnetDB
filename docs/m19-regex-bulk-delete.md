# M19 #126 / #126.1 正则与大批量删除契约

本文记录 Milestone 19 最后两项的稳定行为、存储恢复顺序与运维边界。

## SQL 正则契约

所有时序 measurement、关系表、文档集合、JSON 虚拟表、vector/hybrid search 残差过滤和 Document Validator 共用同一个 matcher：

- 操作符：`REGEX`、`REGEXP`、`RLIKE`，以及对应的 `NOT REGEX` / `NOT REGEXP` / `NOT RLIKE`；
- 标量函数：`regexp_like(input, pattern[, flags])`，可用于 `WHERE` 与 `SELECT`；
- flags：`i`（忽略大小写）、`c`（恢复大小写敏感）、`m`（多行）、`s`（singleline）、`x`（忽略模式空白）；
- 单次匹配超时固定为 250ms；
- pattern 上限为 4096 字符，input 上限为 1,048,576 字符，flags 上限为 16 字符；超限直接拒绝，不截断输入；
- 动态模式使用进程级 128 项有界 FIFO 编译缓存；命中读取无全局锁，miss 才进入短临界区；
- `EXPLAIN` 的 `scan_filter` 会把正则标为 `regex_residual(...)`，不宣称正则索引下推。

EF Core provider 把 `Regex.IsMatch(input, pattern)` 和使用常量 `RegexOptions` 的三参数重载翻译为 `REGEXP_LIKE`。可翻译选项为 `IgnoreCase`、`Multiline`、`Singleline`、`IgnorePatternWhitespace` 和 `CultureInvariant`；其他选项保留为不可翻译，避免静默改变语义。

`regexp_substr`、`regexp_replace`、`regexp_instr` 与 `^literal` 索引前缀剪枝不在本次交付内。它们需要独立的返回值/替换契约和真实索引能力证明。

## 批量 tombstone

`KvKeyspace.DeleteMany` 与关系表纯删除 batch 不再逐 key fsync。删除 key 按 `KvOptions.BatchDeleteMaxKeys`（默认 4096）和 `BatchDeleteMaxBytes`（默认 4MB）切成 WAL chunk，最后写入 batch commit record：

1. 所有 chunk 与 commit 写入同一 active KV WAL；
2. commit 完成后执行一次 WAL sync；
3. 持有 keyspace 写锁，一次性把整批 tombstone 发布到内存读视图；
4. 恢复时先缓存 chunk，只有 chunk 完整且 commit 存在才应用；未提交或 torn batch 整批忽略。

因此批量删除同时保证运行时可见性原子和崩溃恢复原子。它复用既有 KV delete/tombstone 视图，没有增加第二套关系行 tombstone。

## Generation 快速清表

`TRUNCATE TABLE name` 和无入站外键的 `DELETE FROM name WHERE TRUE` 走 table/keyspace generation 快路。关系表维护当前行数，所以返回受影响行数不需要先扫描全表。

提交顺序如下：

1. KV WAL 追加并 fsync `ClearGeneration`；
2. 原子写入并 fsync `generation.meta`；
3. 清空 overlay、卸载旧 disk state，发布空 generation；
4. 轮转 active KV WAL；
5. 写入 `cleanup.manifest.json`，列出旧 generation 的 snapshot/segment 文件。

任一崩溃点恢复时，WAL clear 或 `generation.meta` 至少有一个能阻止旧 state 重新可见。KV state 文件格式升级为 v4，在 header 中记录 generation；v1-v3 文件仍按 generation 0 读取。写出 v4 state 后不保证旧版 SonnetDB 二进制可回滚读取，降级前应使用当前版本导出/备份并由目标版本重新导入。

存在入站外键时，`TRUNCATE TABLE` 明确拒绝；`DELETE ... WHERE TRUE` 回退到既有约束、CASCADE/SET NULL 执行路径，不绕过关系完整性。

## 后台回收与观测

后台 KV maintenance 会扫描已打开实例，也会在重启后发现磁盘上尚未由业务打开、但带 cleanup manifest 的 keyspace/table。每轮每实例最多删除 `KvOptions.CleanupMaxFilesPerRound` 个文件（默认 2），轮询间隔由 `CleanupPollInterval` 控制。每删除一批文件就原子重写并 fsync manifest；文件已删但 manifest 未更新的崩溃可幂等重试，manifest 清空后删除任务文件。manifest 删除目标有严格白名单，只允许当前 keyspace 的 `snapshots/*.SDBKVSNP` 与 `segments/*.SDBKVSEG`，目录穿越或根内其他文件均拒绝。

回收任务默认启用资源感知调度：进程内有活跃查询、后台 flush 仍有排队/在途 I/O、GC 内存负载达到 90%，或相邻维护轮次采样的进程 CPU 达到 90% 时，本轮暂停且不推进 manifest。对应阈值和开关为 `CleanupPauseWhenQueriesActive`、`CleanupPauseWhenFlushPending`、`CleanupMaxCpuPercent`、`CleanupMaxMemoryLoadPercent`；压力解除后沿同一 manifest 自动续跑。CPU/内存阈值设为 0 可显式关闭该项检查。

公开状态：

- `KvKeyspace.Generation`；
- `KvKeyspace.GetCleanupStatus()`；
- `TableStore.RowCount` / `Generation` / `GetCleanupStatus()`；
- `Tsdb.GetKvMaintenanceStatus()`：已完成轮次、已删文件、pending 文件/字节、最近速率、节流轮数/原因和最近错误类型；完整异常仍通过 `Tsdb.LastError` 与诊断事件读取。

指标：

- `sonnetdb.kv.generation.changes`
- `sonnetdb.kv.clear.keys`
- `sonnetdb.kv.clear.duration`
- `sonnetdb.kv.cleanup.files`
- `sonnetdb.kv.cleanup.bytes`
- `sonnetdb.kv.cleanup.duration`
- `sonnetdb.kv.cleanup.pending.files`
- `sonnetdb.kv.cleanup.pending.bytes`
- `sonnetdb.kv.cleanup.rate`
- `sonnetdb.kv.cleanup.throttled{reason=active_queries|flush_pending|cpu_pressure|memory_pressure}`
- `sonnetdb.kv.cleanup.failures`

## 验证与基准

```bash
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release
dotnet test tests/SonnetDB.EntityFrameworkCore.Tests/SonnetDB.EntityFrameworkCore.Tests.csproj -c Release
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --table-delete-smoke
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --filter '*TableDelete*'
```

`TableDeleteBenchmark` 对照逐主键 delete record、谓词批量 tombstone、`DELETE WHERE TRUE` generation 和 `TRUNCATE TABLE` 四条路径。正式数据必须在目标硬件用 Release 构建采集；smoke 只验证 setup、受影响行数和四条路径可运行，不代替统计报告。
