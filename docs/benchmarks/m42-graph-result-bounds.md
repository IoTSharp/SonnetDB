# M42 直接 Graph SQL 累计物化准入

Core `SqlExecutionOptions.MaxMaterializedRows` / `MaxMaterializedBytes` 的 opt-in 准入覆盖直接 `graph_nodes` / `graph_edges`，以及原生固定一跳 `GRAPH_TABLE` 的单源标量投影、WHERE、OFFSET/LIMIT。原有未启用预算的查询行为保持兼容。

预算查询使用分页游标逐行获取候选，过滤、跳过 OFFSET 和投影后，在追加结果前调用共享 `SqlRowRetentionBudget`。结果超过累计行数或估算字节上限时整条调用拒绝，不返回静默截断。EXPLAIN 输出同样计费；EXPLAIN ANALYZE 的执行结果和解释输出共享根预算。候选扫描和操作时限独立限制不会返回不完整前缀。

排序、去重、聚合/窗口、关系映射图、可变路径、JOIN、视图/CTE/嵌套来源、集合运算、用户函数和非标准标量值仍在打开图快照前拒绝。预算表达式和标识符校验遵循现有 SQL 引号与大小写合同。

此估算沿用 `64 + 8 * 列数 + 值载荷` 的累计保留合同。图固定快照、底层 KV 页、属性单值解析和投影求值的瞬时分配仍有独立工作集，因此不宣称 CLR heap 硬上限或首行延迟门禁。REST/Frame 预算选项、固定硬件/架构、大结果容量和长稳仍需独立验证。代码及本地证据见[本轮报告](../audits/roadmap-parallel-next-20261002.md)。
