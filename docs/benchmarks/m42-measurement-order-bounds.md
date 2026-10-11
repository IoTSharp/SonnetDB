# M42：直接 measurement raw SELECT 排序物化准入

2026-10-03 本地增量，基线 `a0e1dff4679f4450fc10fec87cd527515777c700`。这是已有 raw SELECT 预算的排序扩展，不重复 TVF 预算，也不表示 M42 整体验收完成。

显式 `SqlExecutionOptions.MaxMaterializedRows` / `MaxMaterializedBytes` 的直接 measurement raw SELECT 支持标量多键 ORDER BY，包括未投影 FIELD、TAG、time、投影别名及标量表达式。过滤后先保留排序候选与键，使用统一 SQL 标量比较规则，按输入顺序稳定处理相同键，最后执行 OFFSET/LIMIT。

```csharp
var options = new SqlExecutionOptions
{
    MaxMaterializedRows = 100_000,
    MaxMaterializedBytes = 64L * 1024 * 1024,
};
var result = SqlExecutor.Execute(database, null,
    "SELECT time FROM readings ORDER BY value DESC, time ASC LIMIT 10",
    null, null, options);
```

排序候选行、辅助键和最终输出行分别累计计费；即使引用同一值或行，也不退还累计配额。LIMIT 不能跳过阻塞排序输入准入。超限拒绝整个查询，不返回成功的截断结果；取消和失败释放根预算预留。字段流仍使用逐 timestamp 合并，不为排序键建立全字段时间戳查表。

聚合、窗口、去重、JOIN、嵌套来源、子查询、Geo 和非标准值保持预检拒绝。注册函数回调仍由公共合同拒绝。默认未启用预算的路径沿用原有执行语义；本增量不扩展默认路径的排序能力。

预算是累计行与 SQL 值估算；存储缓存、schema/catalog 快照、瞬时解码、表达式分配与 CLR heap 不等于此估算。远程协议、端到端首行、固定 x64/ARM64 与 168 小时门禁未由本机回归取得。

定向测试为 `MeasurementOrderMaterializationBudgetTests`；真实文件组合入口 `SonnetDB.CdcStreamingJourney --topology` 同时验证重开前后的未投影字段排序结果与 LIMIT 下预算拒绝。验证见[本轮审计](../audits/roadmap-continuation-20261003.md)。
