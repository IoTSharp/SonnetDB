# M43 本地文件订阅持久死信隔离

`FileStreamingSubscription` 为当前已经耗尽投递次数的未确认批次增加管理员显式隔离入口。隔离会保存整个原批次的事件标识、序号、业务时间、二进制载荷、大小写敏感头、迟到标记、订阅/流身份、管理员原因和前后检查点。普通业务确认仍使用 `AcknowledgeAsync`；死信隔离返回独立 `FileStreamingDeadLetterReceipt`。

```csharp
FileStreamingSubscriptionStatus status = await subscription.GetStatusAsync(token);
FileStreamingDeadLetterReceipt receipt = await subscription.MoveExhaustedBatchToDeadLetterAsync(
    status.InFlightDeliveryId!, status.StateRevision, status.InFlightAttempt,
    "载荷不符合业务契约", token);
IReadOnlyList<FileStreamingDeadLetterSummary> page = await subscription.ListDeadLettersAsync(
    afterSequence: 0, maxCount: 100, cancellationToken: token);
FileStreamingDeadLetterBatch? saved = await subscription.ReadDeadLetterAsync(
    receipt.DeadLetterSequence, token);
```

只有稳定投递标识、当前状态修订号和已耗尽投递次数全部匹配才允许写入。陈旧快照、错误批次、未耗尽计数或调用前取消均不产生隔离意图。宿主负责管理员鉴权，本地核心入口不提供远程权限系统。原因不能为空且最多为 4096 UTF-8 字节。

## 持久协议和结果未知

每个订阅目录新增独立 `dead-letters/state.json`，首切片使用单独的 v1 源生成 JSON DTO、必需字段、禁止未知字段、重复字段检查和 SHA-256。2026-10-02 的重放/删除入口将严格校验后的 v1 迁移为 v2，新增独立目录 revision、序号高水位和重放身份，详见[运维合同](m43-streaming-dlq-operations.md)。隔离协议依次执行：

1. 持久保存完整原事件和管理员 CAS 条件，设置唯一 pending 隔离意图。
2. 使用原检查点 revision 条件保存该批次的新检查点。
3. 持久确认 spool 位点并回收原事件空间。
4. 保存订阅状态，减去原批次数并清除未确认批次；暂停状态保持不变。
5. 持久清除 pending 意图，再返回隔离回执。

第 1 步之后遇到存储失败、操作超时或取消，结果可能未知；当前对象会拒绝后续操作，必须关闭重开。重开核对意图身份、原状态修订号、投递次数、前后检查点、事件边界和仍可回放时的完整 payload/headers。只有原检查点或该意图检查点可被接受。已发布的意图会幂等完成，既不重复新增死信，也不在缺少完整死信保存记录时回收原事件。若哈希、完整事件、CAS 或位点不匹配，打开失败，需外部修复或恢复备份。

独立目录初始化先在专属临时目录持久写入空 journal，再原子发布目录并 flush 父目录；发布前失败不会留下空正式目录。发布后失败结果未知，下一次打开读取已发布状态。正式死信目录已存在而 `state.json` 缺失时必须拒绝打开，不能假定为空并覆盖可能丢失的记录。

## 边界和兼容性

`FileStreamingDeadLetterOptions` 默认最多 1000 批、64 MiB 状态文件，允许上限为 10000 批、128 MiB。容量检查在任何隔离写入之前完成，满载抛出 `FileStreamingDeadLetterCapacityException` 并保留原批次、投递次数、检查点和事件空间。事件继续受原单事件、批次数、批次字节和操作时间边界约束。状态写入整体序列化并重写 journal，字节限制不是整个托管堆分配的硬上限，也不是追加式日志的性能承诺。

分页每次返回 1–100 个摘要，`afterSequence` 为独立死信序号；读取一个批次返回可修改的完整副本，不会改变持久记录。2026-10-02 的[本地重放与删除合同](m43-streaming-dlq-operations.md)新增持久领取和条件删除，删除回收批次及字节容量，序号不复用。消费者负责幂等业务处理；重放不自动重新发布到原订阅，也不自动隔离，死信记录或删除回执不证明外部业务副作用已经完成。

原四参数 `OpenAsync` 保留 CLR 签名和调用方式。显式配置可用五参数重载：

```csharp
await using var subscription = await FileStreamingSubscription.OpenAsync(
    directory, definition, subscriptionOptions, token,
    new FileStreamingDeadLetterOptions { MaxBatches = 100, MaxStoredBytes = 16 * 1024 * 1024 });
```

未提供死信配置时，新建使用默认值，重开恢复保存的值；显式提供时必须完全匹配。`subscription.json` 仍使用现有 v3，v1/v2 仍通过原哈希模型严格迁移到 v3，新增功能不改文件二进制格式。旧版本不能协调新死信 pending 意图，回退前必须先用当前版本完成隔离恢复；回退后旧版本不会提供死信读取或治理。整个订阅目录（包括 `dead-letters`）需要作为恢复边界，不能只备份原 spool/checkpoints。

本合同只覆盖本地单执行者、文件锁和至少一次投递，未实现分布式租约、远程 DLQ、业务副作用事务或 exactly-once。隔离会跳过原流批次，但下游窗口状态仍保留原基线，不能自动将该缺口当成已处理；后续窗口处理必须通过明确的新基线或未来的专门位点桥接合同完成。

## 回归范围

`FileStreamingDeadLetterTests` 确定性覆盖完整事件保存和副本隔离、正常尾批次、条件拒绝、调用前取消、UTF-8 原因边界、批次数/字节容量原子拒绝、分页、保存配置恢复、SHA-256/未知/重复/缺失字段/版本/身份拒绝、checkpoint 锁超时后的恢复，以及 checkpoint/spool 已提交但订阅状态写入失败后的恢复。额外将 pending 的 payload、状态修订号和检查点重新计算合法哈希后篡改，确认重开仍拒绝不匹配意图且原 spool 不被回收。

这些是本地文件持久状态和阶段故障证据，不代替远程实例、固定硬件、nightly、真实模型或生产门禁验证。
