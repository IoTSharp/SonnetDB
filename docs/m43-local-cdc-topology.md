# M43 #386：多分区本地显式复制拓扑

`CdcLocalReplicaTopology` 在已有单分区源视图、spool 和接收端之上新增显式路由与公平调度。它最多接收 64 条 `CdcLocalReplicaPartition`，每条路由使用独立持久文件，并以 `(Source, Entity, Partition)` 的序数精确身份绑定视图与接收端。构造时拒绝身份重复、视图与接收端 descriptor 不同、规范化持久路径或 lease 重叠，以及专用 spool 中出现其它分区。

调用方必须使用与视图 descriptor 的 source、entity、schema、schemaVersion 和 partition 全部匹配的 `CdcDocumentSourceCapture` 生产专用 spool。构造检查只能证明视图与接收端身份一致，并检查 spool 的分区和文件隔离，不能证明 spool 事件的来源。已 ACK 回收的事件没有正文可供核验；泵确认快照边界以前的前缀时也不逐事件验证来源。不得把另一生产者的历史或已回收 spool 接到当前路由。实际应用的增量仍由 `CdcSnapshotReplica` 严格校验 source、entity、schema 和 partition，身份不匹配不会应用或 ACK 该事件。

每一步复用 `CdcLocalReplicaPump`，只复制一页快照、切换快照阶段，或应用并确认一批增量。成功完成后轮转到下一条路由；多次 `MaxSteps = 1` 的运行仍保持轮转位置。同一拓扑的并发运行串行执行，不创建后台线程。调用方继续负责源捕获及 view、spool、replica 的关闭。

```csharp
var topology = new CdcLocalReplicaTopology(
[
    new(alphaView, alphaSpool, alphaReplica),
    new(betaView, betaSpool, betaReplica),
]);

CdcLocalReplicaTopologyRunResult result = await topology.RunAsync(
    new CdcLocalReplicaTopologyRunOptions
    {
        MaxSteps = 32,
        MaxElapsedTime = TimeSpan.FromSeconds(10),
        MaxRowsPerStep = 1,
        MaxBytesPerStep = 4096,
    },
    cancellationToken: cancellationToken);
```

一次运行同时限制推进次数和墙钟时间，并通过每步行数、字节数、可取消间隔及可选 `IProgress<CdcLocalReplicaTopologyProgress>` 报告进度。所有单步限制还必须满足原视图、spool 和接收端的配置上限。调用方取消抛出取消异常；内部时间到期且各持久对象仍可核对时，返回 `ReachedTimeLimit` 和各分区独立持久状态。若存储操作因取消进入 faulted 状态，继续抛出恢复核对错误，调用方必须关闭并重开，不能生成假定成功或回滚的状态。时间限制包括排队和间隔；已开始的不可取消刷盘需要完成，因此它是协作取消边界，不承诺操作系统硬实时中断。进度接收方应迅速返回。

`IsCaughtUp` 只表示观测时各快照完成、已捕获 spool 高水位全部物化且确认。它不检查源 change feed 的最新位点，也不代表当前源端没有新写入。调用方应先持续捕获，再依据此结果决定下一次调度。`GetStates()` 和结果中的状态分别保持单分区一致，不提供跨分区原子快照。

关闭并重开各分区的原 view、spool、replica 后可重新构造拓扑；恢复依据各接收端持久位点和 spool 高水位，不需要一个新的拓扑文件格式。轮转序号属于进程内调度状态，不是复制位点。接收端已提交、ACK 尚未完成时，下一次推进仍先补确认，再读后续事件。一路容量或 I/O 失败会停止当前运行，已成功分区保留提交，失败分区不能提前 ACK；时间到期或异常都不能解释为全拓扑回滚。

本切片支持多个独立分区的本地推进。它保留每分区的快照身份、连续 offset 和原子提交合同，不合并同实体分片数据、不提供跨分区事务、远程传输、离线拓扑、冲突解决、schema 演进或 exactly-once。远程 parity、真机掉电、固定硬件容量和长期故障门禁继续待执行。

定向验收入口：

```text
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --filter FullyQualifiedName~CdcLocalReplicaTopologyTests
```

2026-10-01 专项 23/23、组合旅程 3/3 通过，见[第二批闭环记录](audits/roadmap-four-task-closure-20261001.md)。自动化场景覆盖真实 `Tsdb` 两文档集合、两独立分区，在固定快照建立后写入更新与删除、执行部分快照、关闭并重开全部资源、继续捕获增量、完成复制及独立位点和物化结果对账；另覆盖轮转公平、并发调度、一次分区失败保留其它分区提交、已物化未 ACK 的补确认、取消、时间/次数/字节边界以及错误路由拒绝。本机合同验证不能替代上述远程与现场证据。
