# BM25 持久化全文 block 读基线（2026-11-17）

本记录绑定源码 `main@7f4ddb052ce8568b5e4b2f14126a94ab25a08c05`、Windows 11 `10.0.26300.9974`、Intel Core Ultra 9 185H（22 logical / 16 physical cores）、X64、.NET runtime `10.0.15` 和临时使用的 SDK `11.0.100`。仓库要求的 SDK `10.0.100` 在本机不存在，因此这份记录不能替代 SDK 10 的构建或测试门禁。

## BenchmarkDotNet 结果

使用受跟踪的唯一工程 `tests/SonnetDB.Benchmarks/SonnetDB.Benchmarks.csproj` 的隔离源码副本运行，副本的两个 `ProjectReference` 改为绝对路径；`.codex-temp` 下的历史同名工程没有读取、修改或删除。标准 BDN harness 在生成阶段超过其内部 120 秒构建期限，执行 0 个样本；随后使用 InProcess toolchain 避开该生成瓶颈。

命令（在隔离副本目录执行）：

```text
dotnet run --project <isolated>/bench/SonnetDB.Benchmarks.csproj --configuration Release --no-build --no-restore -- --filter *FullTextBlockReadBenchmark* --inProcess --launchCount 1 --warmupCount 1 --iterationCount 3 --artifacts <isolated>/artifacts-3iter
```

基准固定创建 1,024 个文档：偶数文档为 `alpha beta`，奇数文档为 `alpha`；查询为同一字段 `OR(alpha,beta)`，`topK=20`。冷读每次打开一个新 `PersistentFullTextIndex`；热读复用 `GlobalSetup` 中已查询过的同一实例和 block cache。

| 方法 | 迭代 | 均值 | 标准差 | 分配 |
| --- | ---: | ---: | ---: | ---: |
| PersistentFullTextIndex cold block read | 3 | 1,949.1 µs | 158.71 µs | 1,165.11 KB |
| PersistentFullTextIndex hot block read | 3 | 333.0 µs | 9.06 µs | 198.56 KB |

这是一轮同机小语料 InProcess 测量，样本量不足以成为延迟门禁；BDN 给出的 99.9% 区间也因三次迭代而很宽。冷读均值约为热读的 5.85 倍，只能作为本机当前实现的相对基线。

## 物理读取和缓存对账

另用同一 1,024 文档语料和同一 `OR` 查询运行一次性诊断探针，读取 `PersistentFullTextIndex.GetBlockReadStatistics()` 前后快照。该探针不是 BDN 的返回值注入，而是独立对账，避免把延迟统计误写成物理 I/O 统计。

| 阶段 | 物理 block | 物理 bytes | cache hit | cache miss | 当前缓存 block / bytes |
| --- | ---: | ---: | ---: | ---: | ---: |
| 查询前 | 0 | 0 | 0 | 0 | 0 / 0 |
| 冷读后累计 | 12 | 6,154 | 0 | 12 | 12 / 6,154 |
| 热读后累计 | 12 | 6,154 | 12 | 12 | 12 / 6,154 |
| 冷读增量 | 12 | 6,154 | 0 | 12 | — |
| 热读增量 | 0 | 0 | 12 | 0 | — |

结果证明在这个语料、单进程和 4 MiB cache 预算下，第一次查询确实完成了 segment posting block 物理读取，紧接着的同实例查询复用了缓存，没有增加物理读取字节。它没有证明跨进程共享、多个索引实例共享、缓存满载淘汰、I/O 错误恢复、百万文档容量或长时间稳定性。

## 有界容量和长稳验证设计

下一阶段应使用独立的可取消 runner，而不是把长时间负载塞入默认 BDN 统计。每个运行都必须同时受文档数、请求数和墙钟限制约束，任一上限到达即停止，并在 finally 中关闭索引、删除本轮创建的目录和回收本轮子进程。

| 层级 | 语料 / cache 预算 | 负载上限 | 主要收据 |
| --- | --- | --- | --- |
| 容量 smoke | 1,024 / 10,000 / 100,000 docs；64 KiB / 1 MiB / 4 MiB / 64 MiB | 每格最多 200 次查询、最多 8 并发、墙钟 5 分钟 | 结果对拍、物理读 block/bytes、hit/miss/eviction、当前缓存占用、峰值工作集 |
| reopen 争用 | 10,000 docs；4 MiB | 8 个固定 worker，每 worker 最多 100 次 `Open → Search → close`，墙钟 5 分钟 | ID/score 一致性、异常分类、物理读取与 single-flight 计数 |
| bounded soak | 10,000 docs；4 MiB | 8 个 reader、固定查询集合、最多 10,000 次请求或 15 分钟 | 每 1,000 次输出 checkpoint；失败数、取消/超时、缓存统计单调性、进程/托管内存趋势 |
| extended local soak | 100,000 docs；4 MiB / 64 MiB | 8 个 reader，最多 100,000 次请求或 60 分钟 | 每 5,000 次 checkpoint；目录大小、句柄、GC、读取错误、重开后结果摘要 |

每个容量格应固定 tokenizer、BM25 参数、segment 生成批次、查询 seed、topK 和运行机器；先运行小格验证 runner 的退出和清理，再逐级扩大。长稳阶段只报告“在给定上限内完成/失败/超时”，不能把本机通过写成固定硬件、百万文档、生产或公开发布门禁。固定 x64/ARM64、真实设备 I/O、跨进程共享和发布消费者仍需各自独立验证。

## 当前状态

- 源码与现有全文回归：沿用交接中已记录的结果；本轮未修改产品代码。
- BenchmarkDotNet 工程构建（SDK 11 临时覆盖）：PASS，0 warning / 0 error。
- 标准 BDN harness：BLOCKED，内部 120 秒构建超时，0 个 benchmark 样本。
- InProcess BDN 冷/热读：PASS（3 iterations；本机小语料基线）。
- 物理 block/cache 对账：PASS（独立一次性探针，12 block / 6,154 bytes 冷读，热读零新增物理读）。
- SDK 10.0.100 默认路径、固定硬件、百万文档、长稳、生产和公开读回：BLOCKED 或 NOT_RUN。
