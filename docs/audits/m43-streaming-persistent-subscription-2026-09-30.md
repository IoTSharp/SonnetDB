# M43 #392 本地持久订阅验收

核查日期：2026-09-30。该记录只证明本地文件持久订阅与确定性恢复合同，不标记 M43 #392~#395 或 M43 总里程碑完成。

## 实现入口

- `FileStreamingSubscription.OpenAsync` 创建或重开单执行者订阅目录，校验已保存定义、选项与三个持久层之间的状态。
- `PublishAsync` 在返回 `Accepted` 前刷盘事件与订阅状态，`LastAcceptedSequence` 供发布源重开后继续。
- `ReadBatchAsync` 保存批次描述后投递，未确认批次跨关闭/重开保留 `DeliveryId` 和事件范围，增加投递次数。
- `AcknowledgeAsync` 先条件持久化 Streaming 检查点，再提交 spool ACK 和回收事件，最后发布订阅状态；恢复时核对并完成中断的 ACK。
- `AdvanceWatermarkAsync` 独立保存当前 watermark；`CompleteAsync` 保存发布完成标记；`DisposeAsync` 关闭句柄并唤醒等待者。

事件文件复用 `CdcEventSpool` 的版本化编解码、单分区 CRC 帧、append/replay/ack 与写 lease。状态文件使用 source-generated JSON、SHA-256、临时文件 `Flush(true)`、原子替换与目录 fsync。检查点使用既有 `FileStreamingSubscriptionCheckpointStore` 的 revision 条件保存。

## 验证

| 证据 | 状态 | 实际结果 |
|---|---|---|
| Release 编译与默认 AOT/trim 分析 | PASS | Core、Data、CLI、Caching 扩展和 Core.Tests 均编译成功；命令输出 0 warnings，未出现 IL/AOT warnings；未执行 NativeAOT 发布。 |
| Streaming 全部回归 | PASS | 51/51 通过，0 失败、0 跳过；总测试时间 3.5545 秒。 |
| CDC 与 Streaming 合并回归 | PASS | 最终 Release 专项 99/99 通过，0 失败、0 跳过、0 编译警告；包含快照尾批补 ACK 与 spool 恢复扫描取消新增的 4 个案例。 |
| 新文件入口回归 | PASS | 21 个测试方法，Theory 展开为 32 个案例，全部通过。 |
| 未确认重投 | PASS | dispose/reopen 后批次 ID 与候选检查点保持，Attempt 增加；已确认前缀不再投递。 |
| ACK 中断恢复 | PASS | 条件检查点已提交而 spool 尚未 ACK、spool 已 ACK 而订阅状态尚未更新，两种阶段均恢复后继续剩余事件。 |
| append 中断恢复 | PASS | spool 事件已持久化而订阅状态仍是前一版本时，重开保留该结果未知事件。 |
| 取消与背压 | PASS | 事件数及字节容量满时等待 ACK；读取不会释放 in-flight 容量；取消等待不接受事件；锁等待受超时/取消约束。 |
| spool 重开扫描取消 | PASS | `OpenAsync` 的 `OperationTimeout` 令牌传入 spool 内部构造；预取消、metadata 条目和帧扫描中取消均经回归验证，失败后释放写 lease 并可重开。同步文件系统调用无法被令牌抢占，超时在扫描边界生效。 |
| 损坏与错配 | PASS | 定义/选项错配、状态/检查点/事件文件缺失、截断、状态 SHA-256 或帧 CRC 失败均 fail closed；外部 checkpoint revision 冲突不回收事件。 |
| 未确认批次真实进程强杀 | PASS_LOCAL_ONLY | `M43_StreamingInFlight_HardKill_RedeliversStableDeliveryIdAndPersistsAck` 在真实 Child 程序投递后强杀，重开按同一 ID 重投并确认，再次重开核对空 spool；见[强杀记录](m43-cdc-snapshot-20260930.md#真实子进程强制终止)。 |
| 机器掉电和文件系统故障 | NOT_READY | ACK 提交阶段仍使用文件快照模拟；未执行掉电或真实文件系统故障注入。 |
| Frame/REST 远程 parity | NOT_READY | 文件入口没有新增远程协议或传输宿主。 |
| 分布式租约和业务 exactly-once | DEFERRED | 仅本机同一路径单执行者、至少一次投递；业务副作用事务不在该合同内。 |

运行命令：

```powershell
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --configuration Release --filter FullyQualifiedName~SonnetDB.Core.Tests.Streaming --no-restore --logger "console;verbosity=normal" --logger "trx;LogFileName=streaming.trx" --results-directory artifacts/m43-local-20260930
```

最终合并专项命令：

```powershell
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --configuration Release --no-restore -p:TreatWarningsAsErrors=true --filter "FullyQualifiedName~SonnetDB.Core.Tests.Cdc|FullyQualifiedName~SonnetDB.Core.Tests.Streaming" --logger "trx;LogFileName=cdc-streaming-final.trx" --results-directory artifacts/m43-local-20260930
```

运行产物：`artifacts/m43-local-20260930/streaming.trx` 与最终 `artifacts/m43-local-20260930/cdc-streaming-final.trx`，留在本地 artifacts，不提交生成物。

首轮编译曾被并行 SQL 切片的 xUnit2013、后续 CS0136 阻断，均由 SQL owner 修复后重新运行；上表结果取最后成功执行的完整命令。新增恢复取消测试初稿曾触发 xUnit1031，改为确定的扫描观察点后最终专项通过。

## 容量与剩余边界

`Capacity` 包含全部未确认事件，最高 100,000 个。事件、批次、spool 文件、状态 JSON、两类 checkpoint 元数据均有明确上限；单个事件超过可读批次上限时立即拒绝。原子替换可能短暂占用额外磁盘；没有后台自动重试、DLQ 或无限堆积。

默认事件 JSON 256 KiB、spool 64 MiB、批次 CDC 正文 4 MiB、存储操作 10 秒。重开需要相同定义与选项。正在进行的存储操作取消或失败后，当前实例停止接受后续操作，调用方必须重开核对提交结果。

尚需独立交付的范围包括：远程 Frame/REST parity、持久窗口聚合、任务列表/暂停/重试运维面、真实 change-feed 生产源、更多提交阶段的真实进程故障注入和掉电、容量/性能真机报告。文件入口保存调用方显式发布的事件，不自动捕获数据库写入。

## CHANGELOG 建议

在 `[Unreleased] / Added` 中记录：

> 新增 M43 #392 `FileStreamingSubscription` 本地有界文件订阅，复用 CDC spool 与条件检查点持久化事件、watermark 和未确认批次，支持 ACK 中断及重开恢复、可取消容量背压和至少一次重投；尚未覆盖远程 parity、分布式租约或 exactly-once。
