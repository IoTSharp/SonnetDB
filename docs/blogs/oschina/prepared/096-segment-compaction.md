---
title: SonnetDB 段合并机制：基于大小分层的 Compaction 策略
categories: SonnetDB,存储,Compaction
draft: false
---

写入 flush 会产生持久化 segment。随着小段增加，查询候选与文件维护成本也会增长。SonnetDB 采用 Size-Tiered Compaction：先按大小规划合并批次，再生成新段并发布替换。

## 当前策略的配置

当前 main 的默认配置为：启用 Compaction、同 tier 最少四段、大小倍率四、第一级上限 4 MiB，后台轮询周期五秒。它不是旧底稿里“64 MiB 起步、每层十段、二倍分层”的参数。

嵌入式配置示例：

```csharp
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;

using var db = Tsdb.Open(new TsdbOptions
{
    RootDirectory = "./compaction-demo",
    Compaction = new CompactionPolicy
    {
        Enabled = true,
        MinTierSize = 4,
        TierSizeRatio = 4,
        FirstTierMaxBytes = 4 * 1024 * 1024,
        PollInterval = TimeSpan.FromSeconds(5)
    }
});
```

该片段展示配置，不会自动生成指定数量的段。测试数据应放在独立目录，并按实际安装版本核对 API。

## Planner 与执行器分工

`CompactionPlanner` 基于 reader 快照计算计划，本身不修改持久状态。段按大小分 tier，同一 tier 每凑齐 `MinTierSize` 个产生一项计划；不是不加限制地把整个 tier 全部合并。

后台 worker 在维护锁内取得稳定读租约，规划和执行与 Retention、DropMeasurement 的段变化协调。执行时把 Tombstone 与 schema 纳入合并，不能把已经删除的数据重新写成可见数据。

## 新段发布与旧 reader 生命周期

当前执行顺序包括记录 pending replacement、生成新段、提交 replacement manifest，然后由 `SegmentManager` 发布新段集合。旧 reader 会进入退休状态，读租约结束后再释放。

文件清理发生在替换发布之后，相关索引 artifact 也需处理。这个路径比“写完新文件就立即删除所有旧文件”的伪代码更完整。维护失败会留下诊断，不能把后台异常当作已经成功回收。

## 如何观察效果

```bash
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --filter '*Compaction*'
```

报告应同时记录输入段数、大小、时间重叠、输出段、删除可见性、查询等价与磁盘占用。合并消耗 CPU 和 I/O，并可能与前台竞争；段数减少不保证所有查询都按固定倍数加速。

本文按当前 main 校对，未执行新的合并基准。[策略](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Engine/Compaction/CompactionPolicy.cs)、[规划器](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Engine/Compaction/CompactionPlanner.cs)、[worker](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Engine/Compaction/CompactionWorker.cs)与[4.0.0 发行](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)可用于核对对应版本。
