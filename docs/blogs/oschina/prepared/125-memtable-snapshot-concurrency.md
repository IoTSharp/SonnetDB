# MemTable 优化：从热路径统计到快照并发

MemTable 承接新写入、未落段数据查询与 flush 判断。它的优化重点是减少重复统计和排序，同时让查询读到稳定快照。当前实现同时使用增量统计、版本化快照和桶内锁，并不是把所有读写变成完全无锁。

本文依据当前 MemTable 与 MemTableSeries 实现整理，正式 4.0.0 与 main 的内部实现需要分别核对。结构优化不等于已取得固定硬件的性能或长稳证据。

## 统计在写入时维护

`EstimatedBytes`、点数、最小/最大时间戳等指标由 append 和生命周期操作增量维护，顶层读取使用 Interlocked，避免 flush 判断每次遍历全部 series。

字符串字段的估算在追加时计算 UTF-8 byte count；向量和 GeoPoint 有各自估算方式。这是引擎内部的近似内存口径，不是 CLR 完整对象大小，也不能直接当作进程 RSS。

WAL replay 和 reset 同样要维护这些统计。如果只优化普通 append，重启后的 flush 决策可能与在线运行不同。

## 版本化 Snapshot

MemTableSeries 在 append 时递增版本并使缓存失效。`Snapshot()` 先尝试读取匹配当前版本的缓存；未命中时在桶锁内复制数据，必要的稳定排序在复制后进行，再尝试发布对应版本的结果。

```text
append
    -> 修改桶数据与统计
    -> version + 1
    -> 失效旧缓存

snapshot
    -> 命中同版本缓存：复用只读快照
    -> 未命中：受锁复制、必要时排序、按版本发布
```

排序结果不能覆盖更新版本的缓存。同 timestamp 的点保留写入顺序，不能为了使用更快排序而无意改变重复时间戳的行为。

## SnapshotRange 的快路径有条件

`SnapshotRange(fromInclusive,toInclusive)` 使用闭区间 `[from,to]`。空桶或 `from > to` 返回空；有序桶可以先二分范围，只复制命中区间；已有排序缓存也可直接裁剪。

若桶含乱序数据且没有可用缓存，窄查询仍可能复制并排序全桶。带读取预算的路径因此会按全桶点数/估算大小收费，不应宣传“任意范围只复制命中数据”。

例如桶里有时间戳 `[1000,3000,2000,2000]`，查询 `[2000,2000]` 应得到两个 2000 的点并保留它们原写入顺序；第一次查询的排序工作不能只按返回的两点估计。

## 并发不是单一属性

顶层索引/统计读取、series 快照命中和未命中路径有不同同步机制。append、统计快照和部分范围操作仍使用桶锁，排序移到复制之后可以缩短锁持有，但持续写入时快照复制仍会产生开销。

`TryGetAggregate` 等聚合需要一次读取一致的 count/sum/min/max/时间范围，不能把不同时间读取的几个统计值拼成一份“原子快照”。这也是保留局部锁的重要原因。

## 怎样评价这一改动

先用小样本覆盖单调/乱序、重复 timestamp、空范围、字符串/向量与并发追加，再比较完整查询结果。性能评估分别记录缓存命中、重建排序、append 等待、分配与 RSS，不只看 `Snapshot()` 单次耗时。

本次只是源码与文档复核，没有运行新的并发、恢复或目标硬件测试；已有实现仍需要按其原有证据范围说明。

参考：[MemTable](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Memory/MemTable.cs)、[MemTableSeries](https://github.com/IoTSharp/SonnetDB/blob/main/src/SonnetDB.Core/Memory/MemTableSeries.cs)、[正式 4.0.0](https://github.com/IoTSharp/SonnetDB/releases/tag/v4.0.0)。
