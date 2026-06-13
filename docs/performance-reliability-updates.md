---
layout: default
title: "性能与可靠性近期变更"
description: "汇总最近一天 SonnetDB.Core 在写入、查询、窗口函数、Segment/WAL 格式、恢复语义和分析器治理上的功能变化。"
permalink: /performance-reliability-updates/
---

# 性能与可靠性近期变更

本文汇总最近一天 SonnetDB.Core 的主要工程变更，便于评审、测试和发布说明复核。重点集中在热路径降分配、读多写少快照、Segment v6 格式整合、WAL/Checkpoint 崩溃恢复，以及可持续的 analyzer 治理。

## 写入与 MemTable

- `MemTable.EstimatedBytes`、`MinTimestamp`、`MaxTimestamp` 改为由 `Append`、WAL replay 和 flush reset 生命周期增量维护，`ShouldFlush` 不再遍历全部 series。
- `MemTableSeries` 在 append 字符串字段时增量累加 UTF-8 byte count，避免 `EstimatedBytes` 每次全量扫描并重复调用 `Encoding.UTF8.GetByteCount`。
- `MemTableSeries` 增加快照缓存与 immutable snapshot swap 思路：无追加且数据已排序时，重复 `Snapshot`/range 查询复用已排序快照，并避免暴露可变内部数组。
- `SnapshotRange` 在有序数据上先二分命中区间，只复制范围内点，保持空范围、单点和 inclusive 边界语义。
- 读写并发路径改为减少查询长时间持锁，`Snapshot`/`TryGetAggregate` 不再长期阻塞 append。

## SegmentReader 与查询热路径

- `SegmentReader.Open` 构建只读 `SeriesId -> BlockDescriptor[]` 内存索引，`FindBySeries` 和 `FindBySeriesAndField` 不再线性扫描所有 block。
- 增加按时间范围查找 block 的内存索引，保留 block 顺序稳定、重叠区间和 inclusive 边界语义。
- `QueryEngine.BuildReaderMap` 与 `SegmentManager` 的 Readers/Index 绑定快照关联，snapshot 未变化时复用 reader map；segment add、compaction swap 和 dispose 会发布新快照以避免使用过期 reader。
- `QueryEngine.Execute(PointQuery)` 的 tombstone 过滤热路径去除 LINQ `Where`，改为手写迭代器，并对 tombstone 时间窗做预筛。
- `SegmentReader` 增加 block 解码 LRU 缓存，key 包含 `(SegmentId, BlockIndex, Crc32)`，缓存值为解码后的点数组，Dispose 时释放引用。
- 新增可选 mmap 读取路径，用 safe-only `MemoryMappedViewAccessor` 读取大 segment，默认 byte[] reader 仍作为回退。

## 编码与解码专用化

- `TimestampCodec` 增加直接把 delta-of-delta 时间戳写入 `DataPoint` 目标视图的重载。
- `ValuePayloadCodecV2` 新增全量与范围 `DecodeInto` 路径，直接填充已有时间戳的 `DataPoint` 目标视图。
- `BlockDecoder` 对 V2 全量和范围解码走手写 fast path，避免中间 `long[]` / `FieldValue[]` 分配。
- `CodecSpecializationBenchmark` 对比旧组合式解码与生产快路径。短跑结果显示 V2 全量生产路径分配从约 `1984 KB` 降至约 `1024 KB`，V2 range 分配降至约 `128 KB`。

## 窗口函数与聚合

- 窗口函数执行接口增加 typed evaluator，优先支持 double 路径，减少 `object?[]` 与逐行装箱。
- 新增流式窗口状态接口，`SelectExecutor` 可按 row/chunk 推进窗口计算，旧接口保留适配层。
- `moving_average`、`running_sum`、`running_min`、`running_max` 等内部实现改为 `ReadOnlySpan` / `Span` 批量处理，减少临时数组。
- 数值聚合 `sum/min/max/count` 增加可选 SIMD 快路径，使用 `System.Numerics.Vector<T>`，不支持硬件加速或语义不适合时自动回退标量。
- 扩展聚合快路径新增 TDigest 与 HyperLogLog sketch，v6 新段内嵌 sketch section，旧 `.SDBAIDX` sidecar 仍可按需回退读取。

## Segment v6 与向量索引

- Segment 写入版本升级到 v6，把原先为保持 v5 而外置的 HNSW `.SDBVIDX` 与扩展聚合 `.SDBAIDX` 内容整合进 `.SDBSEG` extension section。
- `SegmentHeader` 保留区写入 mini-footer 摘要副本，包含 IndexCount、IndexOffset、FileLength 与 IndexCrc32，用于尾部损坏时诊断与受控 fallback。
- `SegmentReader` 继续兼容 v4/v5 段文件，并保留旧 sidecar 懒加载回退路径。
- HNSW vector index 不再在 `Open` 时 eager 加载，而是在 `TryGetVectorIndex` 时按需加载，并受统一 LRU 预算控制。

## WAL、Checkpoint 与 Tombstone

- 新写入 WAL record 在不改变 32 字节 header 尺寸的前提下启用 header checksum，增强 torn-write 检测；旧 WAL record 继续兼容读取。
- Replay 遇到第一条坏 record 会停止并忽略尾部，覆盖 header 截断、payload 截断、长度字段损坏和 CRC 错误。
- Checkpoint LSN 持久化改为 tmp 写入、flush、原子 rename，并在可用平台 best-effort flush 父目录。恢复时只有对应 segment 文件存在且长度匹配才采用 checkpoint。
- Tombstone manifest 增加周期性 checkpoint，可按删除数量或时间间隔保存快照，降低大量删除后崩溃恢复对 WAL 全量 replay 的依赖。
- `WalSegmentSet.ReplayWithCheckpoint` 改为单遍扫描，并利用 WAL segment `LastLsn` 元数据跳过 checkpoint 之前的整段。

## Compaction 与 Retention 恢复

- 新增 `segment-replacements.sdbmanifest` 作为段替换状态清单，记录 replacement segment、source segments 与 pending/committed 状态；清单采用 tmp 写入、fsync、原子 rename 与 CRC32 校验，不修改 `.SDBSEG` 二进制格式。
- Compaction 在分配新段后先写 pending 记录；若崩溃发生在新段写完但提交前，启动扫描会跳过 pending target，只加载旧 source 段，避免未提交的新段造成重复。
- Compaction 新段完整写入后先提交 committed 记录，再发布 `SegmentManager.SwapSegments` 并异步删除旧段；若崩溃发生在 swap 后、delete 前，启动扫描会按 manifest 跳过 superseded source 段，只加载 replacement 段。
- Retention 整段 drop 也先写 committed drop 记录，再从内存快照移除并删除文件；若删除文件失败或重启前未完成，启动扫描不会重新加载已 drop 的段。
- 启动时 `Tsdb.Open` 会把 manifest 中出现过的 segment id 纳入 `NextSegmentId` 计算，避免 pending target 尚未落盘时被后续 flush/compaction 复用。
- 验证结果：`CompactionCrashSafetyTests` 覆盖 pending target、committed replacement 与 SegmentId 复用防护；`RetentionWorkerTests` 覆盖 drop 已提交但文件仍残留的重启恢复；全量 `SonnetDB.Core.Tests` 1925 个测试通过。

## Catalog、配置与 Analyzer

- 启动后读多写少的 `SeriesCatalog`、`MeasurementCatalog`、`MeasurementSchema` 和 `TagInvertedIndex` 改为发布 `FrozenDictionary` / `FrozenSet` 快照，写入更新时原子替换。
- Options/config 类型改为 `sealed record` 与 init-only 属性，保留对象初始化器兼容，并通过值语义减少运行时共享配置被修改带来的并发不确定性。
- SQL Lexer 的空白、标识符、数字、duration 后缀与运算符判断改为 `SearchValues<char>` ASCII 快路径，并保留 Unicode fallback。
- `SonnetDB.Core` 增加低噪声性能 analyzer 配置，覆盖热路径 LINQ、重复分配、Count/Any、Dictionary 查询、`SearchValues` 与字符串比较建议；新增 warning 命中点均通过代码修复，无 suppress。

## 诊断能力

- `Tsdb.Dispose` 中 final flush 失败仍保持不抛异常，但会写入 `Tsdb.LastError` 并触发 `Tsdb.DiagnosticEvent`。
- 诊断事件订阅者抛错不会影响关闭语义，测试可以观测 LastError/DiagnosticEvent。

## 验证建议

建议发布前至少运行：

```powershell
dotnet build SonnetDB.slnx
dotnet test SonnetDB.slnx --configuration Release --no-build
dotnet run -c Release --project tests\SonnetDB.Benchmarks\SonnetDB.Benchmarks.csproj -- --filter *CodecSpecialization*
```

连接器验证依赖 CMake、C/C++ 编译器、.NET NativeAOT 工具链和 JDK。Windows x64 推荐：

```powershell
cmake -S connectors/c --preset windows-x64
cmake --build artifacts/connectors/c/win-x64 --config Release
.\artifacts\connectors\c\win-x64\Release\sonnetdb_quickstart.exe

cmake -S connectors/java --preset windows-x64
cmake --build artifacts/connectors/java/windows-x64 --config Release
cmake --build artifacts/connectors/java/windows-x64 --target run_sonnetdb_java_quickstart --config Release
cmake --build artifacts/connectors/java/windows-x64 --target run_sonnetdb_java_quickstart_ffm --config Release
```
