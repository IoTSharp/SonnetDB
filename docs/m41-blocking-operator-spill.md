# M41 #379 阻塞算子内存预算与 spill

## 配置合同

`TsdbOptions.SqlMemory` 定义数据库实例级资源边界：`QueryLimitBytes` 是每条 SQL 的默认阻塞算子额度，`GlobalLimitBytes` 是该实例并发查询共享的总额度。单次调用可用 `SqlExecutionOptions.BlockingOperatorMemoryLimitBytes` 覆盖查询额度；值必须大于零。

独立 Server 部署可通过 `SonnetDBServer:SqlExecution` 配置同名的查询/全局预算以及内部并行参数；启动绑定会强制 `GlobalLimitBytes >= QueryLimitBytes`，并把配置快照传给新建与自动加载的每个数据库。配置修改不会热替换已打开数据库的预算，需要重启 Server。

预算只约束算子的工作集，不截断公开查询结果。算子无法同时取得查询额度和全局额度时必须切换 spill；不能把候选行、分组、连接匹配或排序尾部静默丢弃。

## 算子行为

- Hash Join 在额度内使用构建侧哈希表，额度耗尽后把构建行迁移到磁盘哈希桶；探测只读取对应桶，并继续执行残余谓词和 LEFT JOIN 补空语义。
- Sort 与 Top-N 生成带原始序号的稳定排序 run，再按同一比较器执行最多 32 路的多轮归并；内存与落盘路径对同序行保持相同顺序，也不会随 run 数量无界打开文件。
- GROUP BY 的键目录和组内行可分别落盘；超大单组通过磁盘 `IReadOnlyList` 顺序枚举，不重新整体物化。
- DISTINCT 与索引 OR 候选集合在额度内使用 HashSet，切换后使用磁盘哈希桶按 SQL 行/主键内容去重，保留首次出现语义。

临时标量/行格式是查询内部格式，不属于持久化数据库格式。它使用手写二进制 codec，覆盖当前 SQL 标量类型，不调用反射 JSON；遇到无法编码的运行时类型会明确抛出并清理，不会返回部分结果。

## 生命周期与诊断

首次 spill 时在 `<database>/sql-spill/query-<id>/` 创建工作区和 `.sonnetdb-sql-spill` 所有权标记。SQL 根作用域在成功、异常和取消时释放全部预算并删除工作区。数据库启动只删除名称匹配且含所有权标记的遗留查询目录，不触碰无标记目录。

`EXPLAIN ANALYZE` 公开 `actual_peak_memory_bytes`、`actual_spill_count` 和 `actual_spill_bytes`。峰值是查询内阻塞算子同时预留量，不包含调用方最终持有的结果对象。

自动化门禁使用同一数据分别执行大预算内存路径和 96-byte 强制 spill 路径，逐列对拍 Hash Join、全量排序、Top-N、GROUP BY、DISTINCT 和索引候选集合，并覆盖取消清理、启动遗留清理、全局额度竞争与释放。
