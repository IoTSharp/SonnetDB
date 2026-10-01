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

这里的固定副本导出不提供持续 CDC 到流的事务桥接。测试覆盖有序关闭及未确认批次重开；没有硬杀、断电、跨分区原子事务、远程拓扑、权限矩阵、真实设备、固定硬件或长期 SLO 证据。十四能力总验收继续保留为未完成。
