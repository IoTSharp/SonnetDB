---
title: SonnetDB 数据保留策略：RetentionWorker、自动 Tombstone 与过期段清理
categories: SonnetDB,存储,Retention
draft: false
---

Retention 把历史数据清理变成明确的存储策略。SonnetDB 当前引擎提供全局 TTL、后台 RetentionWorker、部分过期 Tombstone 与整段 drop。默认策略禁用，不能假设安装后自动删除所有过期数据。

## 按真实 API 配置

当前 `RetentionPolicy` 是数据库级全局策略。旧底稿中的 measurement `WITH(ttl=...)`、`ALTER MEASUREMENT ... SET TTL` 和 `SHOW RETENTION POLICIES`，不作为已验证 SQL 合同继续发布。

```csharp
using SonnetDB.Engine;
using SonnetDB.Engine.Retention;

using var db = Tsdb.Open(new TsdbOptions
{
    RootDirectory = "./retention-demo",
    Retention = new RetentionPolicy
    {
        Enabled = true,
        Ttl = TimeSpan.FromDays(7),
        PollInterval = TimeSpan.FromMinutes(10),
        MaxTombstonesPerRound = 1024
    }
});
```

这里只演示独立数据库的配置。启用前需确认已有数据的时间单位和保留需求，不应直接把示例应用到生产目录。

## 时间边界

默认 `NowFn` 返回 Unix 毫秒，TTL 也按毫秒换算。过期条件是 `timestamp < now - TTL`，cutoff 本身仍在保留范围内。

如果自定义时间单位，需要让 `NowFn` 与 `TtlInTimestampUnits` 保持一致。秒与毫秒混用可能清掉错误范围；测试可注入虚拟时钟验证边界，而不用等待真实时间。

## 两条维护路径

整段 `MaxTimestamp < cutoff` 时，规划器可以将它列入 drop。部分过期段则按 series/field 注入截至 `cutoff - 1` 的 Tombstone，后续 Compaction 消化删除点。

本轮注入数量受 `MaxTombstonesPerRound` 控制，并检查等价墓碑和去重。这个上限限制墓碑注入，不应被描述为全轮所有扫描、I/O 或内存的完整预算。

worker 在维护锁与读租约下规划，通过 replacement manifest 提交整段移除，再发布段集合与清理文件。它与 Compaction、flush 的生命周期需要协调，不是随意删除段旁边的 `.tombstone` 文件。

## 回收与恢复边界

TTL 到期不意味着磁盘空间在那个瞬间完成回收。轮询、部分删除和 Compaction 都会影响回收时间。当前规划主要面向持久段，不能据此宣称所有入口和所有热数据在写入瞬间都强制执行 TTL。

Retention 是删除策略，不是可撤销归档。需要长期保留或恢复时，另行建立备份与归档流程，观察 `FailureCount`、`LastError`、drop 与注入统计，并验证重启后的可见性。

本文按当前 main 校对：[RetentionPolicy](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Engine/Retention/RetentionPolicy.cs)、[RetentionPlanner](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Engine/Retention/RetentionPlanner.cs)、[RetentionWorker](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Engine/Retention/RetentionWorker.cs)。
