# CDC → 持久订阅 → 窗口恢复样例

此样例使用真实嵌入式文档集合、两条独立 CDC 分区路由、文件持久订阅和持久滚动 COUNT 窗口。它对应 M43 #396 的一个本地组合旅程，执行后输出 `PASS_LOCAL_ONLY`。

```powershell
dotnet run --project samples/SonnetDB.CdcStreamingJourney -c Release
```

加 `-- --keep` 可保留状态文件，程序会打印专属临时目录。默认只删除本次程序创建的临时目录，遇到错误也执行清理。整个旅程有三十秒时限，Ctrl+C 可取消；每次 CDC 调度另有十秒和推进次数上限。

旅程先从 `devices`、`alerts` 创建固定视图，在快照之后插入文档并捕获持久 change feed，只推进一页就关闭全部句柄。重开源和每条路由后，再写入更新和删除；继续复制并核对三条最终文档、各自的 `(partition, offset)` 位点和已回收的 spool。

随后将已追平副本的固定三行导出到订阅，序号保持确定。第一批窗口结果持久提交后故意不 ACK，关闭订阅和窗口文件再重开；核对稳定投递 ID、第二次投递和去重计数，再处理尾批、关闭窗口并再次重开。最终必须满足 `partitions=2 rows=3 window_count=3 redelivery_attempt=2 pending=0 closed=True`，任何不一致均使程序失败。

自动化回归调用同一个样例入口：

```powershell
dotnet test tests/SonnetDB.Core.Tests -c Release --filter FullyQualifiedName~CdcStreamingRecoveryJourneyTests
```

默认模式的固定副本导出不提供持续 CDC 到流的事务桥接。测试覆盖有序关闭及未确认批次重开；没有硬杀、断电、跨分区原子事务、远程拓扑、权限矩阵、真实设备、固定硬件或长期 SLO 证据。十四能力总验收继续保留为未完成。

## 实际 change feed 桥接与自动投递

```powershell
dotnet run --project samples/SonnetDB.CdcStreamingJourney -c Release -- --bridge
```

`--bridge` 模式复用真实文档持久 change feed。两次插入经过源捕获进入专用单分区 CDC spool；`CdcStreamingBridge` 用一事件批次、outbox 和独立目标接收凭证交付到持久订阅，证明交付后才确认源。自动驱动器的首次处理先保存窗口再故意抛出异常，以稳定批次身份退避重投并去重，随后 ACK。

关闭全部句柄后，再重开源并执行更新和删除，捕获后将剩余三次变更交付到同一目标，自动排空并再次重开核对。成功输出 `PASS_LOCAL_ONLY bridge source_offset=4 target_sequence=3 window_count=4 redelivery_attempt=2 pending=0`。窗口计数代表四次变更，包括删除，区别于默认模式的最终副本行数。

此入口设三十秒总时限、三次尾部推进上限和独立驱动器批次/次数/时间边界，支持 Ctrl+C。`--keep` 可同时使用；默认仅回收本次创建的专属目录。生产使用须先恢复 bridge outbox 再启动消费者；源 ACK 和目标发布均应由桥接独占。未知接收凭证而目标事件已经回收时明确拒绝推进，需要人工恢复交接，见[桥接合同](../../docs/m43-cdc-streaming-bridge.md)和[自动投递合同](../../docs/m43-streaming-dispatcher.md)。

这是本地真实嵌入式文件旅程；没有执行远程服务、跨业务副作用事务或生产门禁。

## 文件与双模型向量预算组合

```powershell
dotnet run --project samples/SonnetDB.CdcStreamingJourney -c Release -- --budgets
```

`--budgets` 实际从 JSON 文件读取三条记录并保存至文档集合和 measurement，检查文件 TVF、文档向量和 measurement KNN 的低预算拒绝，以及失败后正常查询、flush 和数据库重开的一致结果。
成功输出 `PASS_LOCAL_ONLY budgets imported=3 vector_id=a knn_time=0 rejected=3 reopened=true`。
它与 `--bridge` 互斥，可组合 `--keep`；默认回收本次独占目录，三十秒总时限和 Ctrl+C 取消保持有效。
此入口验证 [M42 累计物化合同](../../docs/benchmarks/m42-table-function-result-bounds.md)，本地合成向量不计模型质量、固定硬件性能或远程证据。
