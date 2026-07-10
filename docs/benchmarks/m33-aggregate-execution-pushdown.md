# M33 时序聚合执行与下推基准

本文记录 Milestone 33 #285~#287 的同口径 before/after 基准。可复现入口为
`tests/SonnetDB.Benchmarks/Benchmarks/M33PushdownBenchmark.cs`。

## 环境与口径

- 日期：2026-07-10
- 系统：Windows 11 25H2，`10.0.26200.8737`
- CPU：Intel Core Ultra 9 185H，16 物理核 / 22 逻辑核
- 磁盘：NVMe PC SN8000S WD 2048GB
- SDK / Runtime：.NET SDK `10.0.301`，.NET Runtime `10.0.9`，x64 RyuJIT
- BenchmarkDotNet：`0.15.8`，3 次 warmup，7 次 iteration，取 Median / P90
- 数据：单 series，200,000 个不同时间戳，每时刻 3 个 field，共 600,000 个 field-value
- 落盘：全部 flush 为 16 个时间范围不重叠的 segment；后台 flush、compaction、retention 关闭
- 分页：`LIMIT 64`；#286 为自然正序，#287 为 `ORDER BY time DESC`
- 冷热：本表为热查询结果；每个 benchmark 进程在 GlobalSetup 完成大数据 before/after 结果对拍，
  再经 3 次 warmup 后测量，因此 segment reader 与 block cache 已预热。未把首次打开数据库的冷启动混入结果。
- 正确性：GlobalSetup 强制对拍 #285 的 count 值、#286 的前 64 行和 #287 的最新 64 行，
  任一单元格不一致都会终止基准。

运行命令：

```powershell
dotnet run -c Release --project tests\SonnetDB.Benchmarks\SonnetDB.Benchmarks.csproj -- --filter "*M33Pushdown*"
```

## 结果

| 场景 | 实现 | Median | P90 | 单次分配 |
|---|---|---:|---:|---:|
| #285 count(*) | before：三字段逐点 + HashSet 时间戳并集 | 37.104 ms | 45.749 ms | 12,273.20 KiB |
| #285 count(*) | after：有序流 k-way merge | 40.439 ms | 41.112 ms | 43.84 KiB |
| #286 LIMIT | before：全量物化后 Take(64) | 358.437 ms | 367.866 ms | 191,676.27 KiB |
| #286 LIMIT | after：时间轴合并凑够 64 行即停 | 54.410 us | 56.890 us | 56.26 KiB |
| #287 latest-N | before：全量物化、DESC 排序后 Take(64) | 276.897 ms | 296.988 ms | 197,926.54 KiB |
| #287 latest-N | after：从 MaxTimestamp 反向扫描 | 57.050 us | 68.320 us | 57.00 KiB |

## 结论

- #285 的目标是消除 O(N) 时间戳集合：分配下降约 280 倍；Median 基本持平，P90 约下降 10%。
  这符合“CPU 不回退、峰值内存显著下降”的预期，不能把它描述成吞吐型加速。
- #286 的 Median 约提升 6,588 倍，分配约下降 3,407 倍；读取量测试同时确认 8 个落盘 block
  中只读取首个 block。
- #287 的 Median 约提升 4,854 倍，分配约下降 3,472 倍；读取量测试确认只读取最新 block。
- 上述数量级只代表本机、热 cache、单 series、`LIMIT 64` 的固定口径；多 series、高重叠段、冷 cache
  和大 OFFSET 应分别复测，不从本表外推绝对延迟。
