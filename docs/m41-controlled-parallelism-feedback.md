# M41 #380 受控并行与运行时反馈

## 目标与边界

SQL 并行只用于可以按输入项独立计算、且结果可以按输入序号重新合并的算子。当前接入点是 measurement per-series scan、legacy numeric aggregate 的 per-series scan，以及无残差、无 spill 且 probe 已物化的关系 Hash JOIN。窗口状态仍在每个 series 内独立创建；事务 ambient、相关残差、未物化 probe、spill 或资源不足时保持串行并记录回退原因。

并行是执行期的有界优化，不改变 SQL 可观察语义：worker 只写固定索引的输出槽，最终按输入目录/关系行顺序拼接；LEFT JOIN 的未匹配行、NULL 键不匹配、聚合桶合并和取消行为与串行路径相同。

## 准入与资源合同

`SqlMemoryOptions` 增加三个数据库级边界：

- `MaxParallelWorkers`：单个数据库实例的 worker semaphore 上限。
- `ParallelismMinRows`：估算输入低于该值时不并行。
- `ParallelWorkerMemoryBytes`：每个 worker 必须从查询级和全局 SQL 阻塞算子预算各预留的额度。

`SqlExecutionOptions` 可以关闭并行、降低 `MaxDegreeOfParallelism` 或覆盖行数阈值。准入顺序是：估算收益、事务门控、用户函数纯度门控、数据库 worker 槽位、查询/全局内存预留和取消检查。没有线程安全/纯函数合同的用户标量或窗口函数会记录 `user_defined_function` 并串行执行；不能拿到至少两个 worker 时回退串行。回退不截断结果，也不泄漏 semaphore 或预算；并行 worker 的异常会解包为与串行路径一致的实际异常类型。数据库关闭与 crash-simulation 路径都释放协调器。

## 估算反馈

SQL 根作用域为 AST 生成不含参数值和行内容的 SHA-256 fingerprint。现有 `SqlExplainPlanner` 的扫描估算写入执行资源，点读取和聚合 bucket 的实际计数写入同一反馈记录。`SqlRuntimeFeedbackStore` 在内存中保留最多 1,024 个 fingerprint，滚动平均 estimated/actual rows，并提供有限比例修正给后续同形状查询的并行准入；目录不持久化业务数据，超限按最旧记录淘汰。

`SqlExecutionMetricsSnapshot` 追加 `EstimatedRows`、`ActualToEstimatedRowsRatio`、`ParallelOperator`、`ParallelismEnabled`、`ParallelWorkerCount`、`ParallelCompletedItems` 和 `ParallelFallbackReason`。这些字段属于内部执行证据，HTTP/JSON 合同保持 extend-only；#381 已完成本地收口，固定硬件、生产 mixed workload 和发布门禁作为现场观察项后置，不能用本机数字替代。

## 自动化门禁

`SqlControlledParallelismTests` 覆盖 worker 上界、固定顺序、查询预算竞争回退、取消后的 worker/预算释放、事务 ambient 禁用并行、measurement scan 与 aggregate 串并行逐行对拍、Hash JOIN LEFT/NULL 语义，以及 estimated/actual feedback。失败、取消和 spill 路径都必须经过资源 finally；无法证明独立性时必须选择串行路径。
