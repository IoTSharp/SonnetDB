---
title: SonnetDB 轻事务与 ROWVERSION：安全处理并发修改
categories: SonnetDB
tags: SQL,ROWVERSION,事务,乐观并发
draft: false
---

# SonnetDB 轻事务与 ROWVERSION：安全处理并发修改

SonnetDB 的关系表轻事务可以把同一数据库内多张关系表的写入一起提交，ROWVERSION 则帮助应用识别“读取以后数据已经被别人修改”的情况。本文从两个操作者编辑同一任务出发，给出版本条件写入、事务回滚和未知提交结果的处理步骤。

![SonnetDB 轻事务与 ROWVERSION：版本条件与提交确认](https://img2024.cnblogs.com/blog/1725297/202610/1725297-20261012111344942-1256787926.png)

图：AI 主题示意图，并非实测截图；版本冲突和提交确认以正文合同为准，不表示生产并发验收。

## 1. 示例和版本基线

2026-10-12 已核 GitHub latest 正式发行与 NuGet.org SonnetDB.Core 为 **4.0.0**。本文读取的本地源码提交是 `dcadffc7137f5653ec46c55a4171cf5f2d83de03`；相关关系写入、ADO 事务、远程会话与回归文件同公开提交 `5424b66672e37f49a2a5633fdde0b54a9b1f0290` 一致。链接固定到公开提交；实际包、Server 和部署行为仍要在目标环境验收。

本文选择 `ReadCommitted` 轻事务演示。旧 ADO 文档写过“仅默认或 ReadCommitted”，当前嵌入式和远程 BeginTransaction 入口也接受 Serializable 枚举，但都不能仅凭这个参数推断完整可串行化、MVCC 或悲观行锁。这里介绍的是关系写入缓冲与提交前校验，并不据接口名扩张隔离保证。

## 2. 让引擎维护行版本

```sql
CREATE TABLE jobs (
    id INT,
    status STRING,
    version INT ROWVERSION,
    PRIMARY KEY (id)
);

INSERT INTO jobs (id, status) VALUES (1, 'pending');
SELECT id, status, version FROM jobs WHERE id = 1;
```

预期初始版本是 1。ROWVERSION 只能声明在一列 INT 上，由引擎在 INSERT 时生成 1，在成功 UPDATE 时递增。它不是时间戳、全库序号或跨表锁，也不能手动 SET。

假设操作者 A 和 B 都读到 id=1、version=1。A 提交：

```sql
UPDATE jobs SET status = 'running'
WHERE id = 1 AND version = 1;
```

再查询时应得到 running、version=2。B 仍使用旧条件：

```sql
UPDATE jobs SET status = 'cancelled'
WHERE id = 1 AND version = 1;
```

对这类已存在目标行的旧版本谓词，当前合同和回归会报告 `table_concurrency_conflict`。目标行已经删除时，也可能得到 0 受影响行。因此应用同时处理冲突错误和未命中结果，不能把 ExecuteNonQuery 正常返回等同于“指定任务一定更新成功”。以上结果来自合同与现有回归推导，本轮没有执行这组 SQL。

## 3. 在同库两张关系表内提交业务动作

先在事务外建立审计表：

```sql
CREATE TABLE job_changes (
    id INT,
    job_id INT,
    action STRING,
    PRIMARY KEY (id)
);
```

下面用新数据库完整展示 ADO.NET 调用。先建表，再开启事务；命令都显式关联该事务。读版本、决定业务动作和生成变更编号应按实际应用要求处理，这里使用固定示例值。

```csharp
using System.Data;
using SonnetDB.Data;

using var connection = new SndbConnection("Data Source=./job-transaction-demo");
connection.Open();

using (var setup = connection.CreateCommand())
{
    setup.CommandText = "CREATE TABLE jobs (id INT, status STRING, version INT ROWVERSION, PRIMARY KEY (id))";
    setup.ExecuteNonQuery();
    setup.CommandText = "CREATE TABLE job_changes (id INT, job_id INT, action STRING, PRIMARY KEY (id))";
    setup.ExecuteNonQuery();
    setup.CommandText = "INSERT INTO jobs (id, status) VALUES (1, 'pending')";
    setup.ExecuteNonQuery();
}

using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
try
{
    using var update = connection.CreateCommand();
    update.Transaction = transaction;
    update.CommandText = "UPDATE jobs SET status = @status WHERE id = @id AND version = @version";
    update.Parameters.AddWithValue("status", "running");
    update.Parameters.AddWithValue("id", 1L);
    update.Parameters.AddWithValue("version", 1L);
    if (update.ExecuteNonQuery() != 1)
        throw new InvalidOperationException("任务不存在或条件未命中，需要重读。");

    using var change = connection.CreateCommand();
    change.Transaction = transaction;
    change.CommandText = "INSERT INTO job_changes (id, job_id, action) VALUES (@changeId, @jobId, @action)";
    change.Parameters.AddWithValue("changeId", 1001L);
    change.Parameters.AddWithValue("jobId", 1L);
    change.Parameters.AddWithValue("action", "start");
    change.ExecuteNonQuery();
    transaction.Commit();
    Console.WriteLine("本地提交返回成功，业务动作与审计一起完成。");
}
catch
{
    // 本例为嵌入式模式；回滚失败不能遮蔽原错误。
    try { transaction.Rollback(); }
    catch (Exception rollbackError) { Console.Error.WriteLine(rollbackError.Message); }
    throw;
}
```

在事务中，ExecuteNonQuery 的影响数是该语句处理的行数，不能替代 Commit 成功。提交之前还会校验主键、唯一索引、外键、CHECK、NOT NULL 及并发状态；任何一项失败，关系行与索引变更都不能被描述为已完成。回滚后应重新 SELECT 两张表验证存储状态，切勿只看异常是否抛出。

故障练习可以把变更 id 改为一个已存在的主键，或在提交前让另一连接修改 jobs；分别观察约束失败和并发冲突。需要固定调度的并发练习请采用屏障协调两次读取与提交，不能用随意 sleep 把偶然结果当作稳定证明。

## 4. 冲突后重读整个业务动作

关系事务会保留最初读取的目标行状态。另一事务修改或删除目标行，冲突可能在写入语句或 Commit 时才出现；即使表没有 ROWVERSION，事务队列也有提交前的状态校验。ROWVERSION 的价值在于把跨请求编辑时的“我基于哪版数据”明确传回服务器。

处理 `table_concurrency_conflict` 时，先结束原事务，再重新读取任务及版本，重新判断业务是否仍允许；若允许，用新的业务决定开启新事务。重试最多设置一个小上限，例如 3 次，并附操作总截止时间；冲突持续时返回应用层冲突，让用户重新确认。不要只重跑最后一条 INSERT，也不要原封不动重复旧版本 UPDATE。

嵌入式约束错误可通过 `TableConstraintException.ErrorCode` 获取；远程结构化错误通过 `SndbServerException.Error` 获取。用户取消、网络中断、权限错误及约束失败需要分别处理，不能都归为可重试的并发冲突。

## 5. 为什么不用 SELECT FOR UPDATE

SonnetDB 当前明确拒绝 `SELECT ... FOR UPDATE`、`FOR SHARE`、`NOWAIT`、`SKIP LOCKED`，解析错误码为 `sql_locking_read_unsupported`。嵌入式、远程以及绑定事务的 ADO 命令均不会将它静默降级为普通 SELECT。

因此读版本并不持有业务可见的行锁。不能声称读取后其它连接被阻塞，也不能把内部提交锁描述为应用可以控制的悲观锁。需要避免重复领取的任务，应把允许迁移的状态与旧版本一起放入条件 UPDATE，随后核对影响数和提交结果。

## 6. 远程提交丢失响应时保留未知状态

新 Server 的 ADO 事务使用短期服务端会话，而非每次请求预览后再重放。会话绑定数据库与凭据；2 分钟无操作会过期，最多 128 个活动会话；提交或回滚终态保留 2 分钟。会话缓冲及终态缓存属于单个 Server 进程，跨实例访问需要路由到原实例；该机制不提供持久的分布式事务状态。

提交返回不确定时，当前客户端先 GET 原会话状态：确认 commit 才报告成功，确认 rollback 才报告回滚；确认 active 且未取消时允许一次原会话幂等提交重试。终态也无法读取时报告提交结果未知，应用必须保留会话 ID 和业务操作编号进行对账。不要新建事务重复扣减、转移或插入审计，更不能从 IOException 推断“肯定没写”。

服务端重启会丢失内存会话和终态缓存；这与数据库文件中的已提交数据恢复是两件事。需要结合业务操作编号读取实际行及审计记录确认结果。本文没有提供重启期间提交、跨节点路由或长期生产恢复的现场验收结论。

## 7. 明确适用范围

轻事务覆盖同一数据库的关系表 INSERT、UPDATE、DELETE，可涉及多张关系表。DDL、measurement/文档写入、IMPORT JSON、嵌套或跨库事务不在这个合同内；不能将一个关系事务包住九模型写入，再宣称统一回滚。AUTO_INCREMENT 的预留值也可能在失败或回滚后留下间隙，连续编号不是事务成功证明。

本轮读取了事务实现、ROWVERSION 回归、远程会话审计与提交恢复测试，未新跑 SQL 示例、产品构建、恢复实验、性能、真实模型、固定硬件或长期生产测试。先在独立数据库检查成功、旧版本、目标删除、约束失败、回滚与 Commit 冲突，再按真实客户端/Server 组合验证响应丢失。Graph 在项目中仍为 Beta，与此处关系事务的支持范围分别评估。

## 参考与继续阅读

- [固定提交的 ADO.NET 接入说明](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/ado-net.md)
- [SQL 轻事务、ROWVERSION 和锁定读合同](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/sql-reference.md)
- [当前嵌入式事务入口源码](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/src/SonnetDB.Data/Embedded/EmbeddedConnectionImpl.cs)
- [远程会话实现及剩余部署边界](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/docs/audits/github-issue-184-remote-session-20260925.md)
- [响应丢失与未知提交的回归](https://github.com/IoTSharp/SonnetDB/blob/5424b66672e37f49a2a5633fdde0b54a9b1f0290/tests/SonnetDB.Tests/RemoteTransactionCommitRecoveryTests.cs)
- [SonnetDB 业务表从建表到演进：主键、默认值、约束与索引](https://www.cnblogs.com/IoTSharp/p/23260377)
