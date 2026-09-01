# SonnetDB system performance evidence summary

Status: `LOCAL_SMOKE_ONLY`
Machine-readable report: [`system-performance-report.json`](system-performance-report.json)
Full analysis: [`../../docs/benchmarks/system-performance-20260901.md`](../../docs/benchmarks/system-performance-20260901.md)

## Baseline

| Item | Value | Check |
| --- | --- | --- |
| Requested outer baseline | `988ed78df18b46a399d5b544fd78904452d2405f` | parent of current outer HEAD |
| Current outer HEAD | `770a139f6f00512d7725458cb7ca43bb1ad75620` | retained |
| Required SonnetDB ancestor | `3f362d1c387149524c8d08f536a687c135aa45eb` | ancestor check passed |
| Current SonnetDB HEAD | `424f61ad16e883d6b9050a9eb29a352105d28cef` | dirty changes retained |
| Old gitlink | `0be6898b1f4ef8b646872ece749dd803cf990e24` | not restored |

Product scope remains eight official models. Native property graph is the ninth **performance domain** only; its M40 production gate is `NOT_RUN`. Relational SQL planning/execution is a shared core, not a tenth model.

## Gate state

| Gate | State |
| --- | --- |
| Local correctness | `PARTIAL_PASS` |
| Local performance direction | `PARTIAL_PASS` |
| win-x64 CLI Native AOT | `PASS_PUBLISH_AND_EXECUTION` |
| win-x64 Server Native AOT | `PASS_PUBLISH_NOT_STARTED` |
| Fixed hardware x64 | `NOT_RUN` |
| Fixed hardware ARM64 | `NOT_RUN` |
| ARM64 Native AOT CI configuration | `CONFIGURED_NOT_EXECUTED` |
| ARM64 Native AOT real run | `NOT_RUN` |
| Mulei same corpus | `NOT_RUN` |
| Seven-day mixed workload | `NOT_RUN` |
| Production release | `NOT_RUN` |

No Mulei production connection, DDL/DML, deployment or restart was performed.

## Before/after

### Table statistics refresh

Workload: 10,000 rows, four secondary indexes, full sample, two warmups and five iterations.

| Metric | Before | After | Change |
| --- | ---: | ---: | ---: |
| Mean | 54.81 ms | 51.25 ms | -6.495% |
| Median | 54.77 ms | 50.68 ms | -7.468% |
| P90 | 55.24 ms | 52.29 ms | -5.340% |
| Allocated | 34.72 MB/op | 22.67 MB/op | -34.706% |

Classification: local directional smoke. The After column is the final dirty rerun; the earlier 50.89 ms intermediate rerun is retained only in the full report. Fixed-hardware repetition is still required.

### Vector-file IEEE CRC32

| Bytes | Before production | After production | Speedup |
| ---: | ---: | ---: | ---: |
| 64 | 130.8 ns | 8.596 ns | 15.216x |
| 4,096 | 8,584.7 ns | 213.571 ns | 40.196x |
| 1,048,576 | 2,374,971.8 ns | 61,436.644 ns | 38.657x |

Both production paths allocate 0 B/op. The persisted algorithm remains IEEE CRC32, not CRC32C. This is an independent five-iteration before/after smoke with visible run variance, not an ARM64 or production claim.

## Model smoke

| Domain/operation | Normalization | Mean | Median | P95 | Allocated | Classification |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| KV point read, 10k keys/256 B value | per key | 2.921 us | 2.935 us | 2.996 us | 736 B | local smoke |
| KV GetMany, 256 dispersed keys | per key | 4.019 us | 4.043 us | 4.141 us | 823 B | local smoke |
| Document ID read, 10k documents | per document | 3.099 us | 3.047 us | 3.287 us | 1.24 KB | local smoke |
| Document indexed JSON-path query | per query | 1,375.524 us | 1,368.046 us | 1,447.969 us | 894.94 KB | local smoke |
| Object full read, 64 KiB | per object | 257.9 us | 266.1 us | 294.1 us | 8.84 KB | exploratory |
| Object range read, 4 KiB | per object | 230.2 us | 234.9 us | 259.6 us | 8.83 KB | exploratory |

The displayed P95 values are percentiles of BenchmarkDotNet iteration means after `OperationsPerInvoke` normalization. They are **not request-level or per-operation tail latency**, and normalization can hide individual-operation variance. BenchmarkDotNet statistic columns are not treated as request tails.

## Request-level hot-read smoke

`ModelReadLatencyEvidenceRunner` recorded 256 single-request samples per operation after 32 warmups. Nearest-rank percentiles were recomputed from every raw sample and matched the report.

| Domain/request | P50 ms | P95 ms | P99 ms | Max ms | req/s | Alloc P95 | Unknown alloc |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| KV point read | 0.0041 | 0.0050 | 0.0060 | 0.0182 | 234,303.50 | 760 B | 0 |
| KV GetMany 256 | 1.2828 | 1.4061 | 1.9250 | 6.1029 | 762.52 | 216,904 B | 0 |
| Document ID read | 0.0032 | 0.0039 | 0.0042 | 0.0044 | 307,803.29 | 1,376 B | 0 |
| Document indexed JSON-path | 2.6186 | 3.0759 | 4.0001 | 4.1119 | 374.03 | 921,992 B | 0 |
| Object full read 64 KiB | 0.1790 | 0.2715 | 0.3523 | 0.6137 | 5,275.21 | 9,112 B | 26 |
| Object range read 4 KiB | 0.1500 | 0.2216 | 0.3198 | 0.3317 | 6,171.54 | 9,144 B | 21 |

Artifact: `model-read-latency-current-final/model-read-latency.json`, SHA256 `6e189c38...116`. This is a single-threaded, local x64, hot embedded-read smoke. It does not cover Server admission, concurrency limits, cold cache, physical I/O, fixed hardware or ARM64. Async thread switches make 26/21 Object allocation samples unknown; latency samples remain complete, while known-sample allocation percentiles must not be extrapolated and unknown samples must not be replaced with zero.

## M41 quick behavior check

Both before and after runs report `localCorrectness=PASS`; access path, examined/returned counts and P50 allocations are unchanged.

| Query | Access path | Examined/returned | P50 before | P50 after | Allocation |
| --- | --- | ---: | ---: | ---: | ---: |
| `indexed_exists` | `secondary_index` | 1/1 | 0.3320 ms | 0.2925 ms | 12,832 B |
| `scalar_in` | `mixed` | 40/20 | 1.2302 ms | 1.2043 ms | 71,352 B |
| `nullable_or` | `index_union` | 20/20 | 0.8853 ms | 0.7756 ms | 45,456 B |
| `multi_table_join` | `table_scan` | 72/20 | 1.6045 ms | 1.1760 ms | 88,816 B |
| `descending_pagination` | `secondary_index_range` | 64/20 | 1.2814 ms | 1.6916 ms | 85,544 B |

Three samples per query are insufficient for a performance comparison. The evidence supports behavior/correctness only. Embedded execution bypasses Server SQL admission, so queue-wait evidence is `NOT_APPLICABLE_EMBEDDED`.

## Nine-domain status

| Domain | Current evidence | Capacity conclusion |
| --- | --- | --- |
| Time series | benchmark harness exists; not run this round | `NOT_RUN` |
| Relational | M41 quick plus statistics before/after | local partial only |
| KV/cache | 10k-key point/GetMany smoke | local smoke only |
| JSON document | 10k-document point/indexed-query smoke | local smoke only; million/ten-million `NOT_RUN` |
| Full text | benchmark/parity harness exists; not run this round | `NOT_RUN` |
| Vector | CRC before/after only | recall/capacity and ARM64 `NOT_RUN` |
| Object storage | 256 x 64 KiB exploratory smoke | rerun required |
| Message queue | bounded Sparkplug queue contract; throughput not run | `NOT_RUN` |
| Native property graph | M40 gate contract exists | release/capacity `NOT_RUN` |

## Implemented slices

- Statistics refresh avoids sampled primary-key copies and per-index key/prefix allocations while sharing real encoding length logic.
- Vector-file CRC delegates IEEE CRC32 to `System.IO.Hashing.Crc32.HashToUInt32`, with golden/random/offset/boundary/1 MiB differential coverage.
- SQL physical-read snapshots are bounded to 64 attempts and 5 ms; incomplete zeros do not pollute histograms or aggregates, and valid/degraded counts remain observable.
- Sparkplug Rebirth uses a bounded, coalescing, single-consumer queue with explicit enqueue/coalesce/reject/discard/depth metrics. Broker readiness uses bounded `MqttServer.IsStarted` checks; expected not-ready/deadline failures use `SparkplugPublisherUnavailableException`, while the readiness helper propagates unknown `InvalidOperationException` without reclassification.
- KV, Document and Object Storage now have independent native-API benchmarks.
- win-x64 CLI and Server Native AOT publish passed; the CLI executable ran `--version`, while the Server was not started.
- CI has a syntax-validated `ubuntu-24.04-arm` / `linux-arm64` Native AOT matrix entry with RID-separated NuGet cache; actionlint, GitHub execution and real ARM64 remain `NOT_RUN`.

## Highest remaining risks

- P0: move automatic table-statistics refresh off the first business-read path.
- P0 gate: execute and archive the configured ARM64 Native AOT job; this still does not prove Kunpeng instruction support or performance.
- P1: remove primary-key-prefix sampling bias and make narrow-index cost page-aware.
- P1: isolate parameter-sensitive plan/feedback families; current fingerprint averages are not a physical-plan selector.
- P1: add an embedded RandomAccess I/O budget and reuse vector query norms.
- P2: expand covering indexes, add cold-miss single-flight, reduce large-value copies, and measure file count/cold start.
- P2: add a real MQTT server integration test for `IsStarted` changing between readiness check and publish; current initial-publish/stop race coverage uses controlled publisher/readiness injection.
- P3: keep direct intrinsics and .NET 11 preview work isolated behind explicit experiment gates.

## Bounded source scan

The bounded scan covered only `src/SonnetDB.Core` and `src/SonnetDB`, excluding `bin/obj`. Selected counts: unbounded Channel `0`, bounded Channel `4`, `async void` `0`, `ToLower/Upper()` `0`, direct no-comparison literal `IndexOf/StartsWith/EndsWith` `0`, `Substring` `6`, potential string `Contains` `4`, triple `Replace` `2`, `params` `19`, char `All/Any` `3`, static readonly `Dictionary` `2`, `FrozenDictionary` candidates `0`, LINQ hot-path candidates `615`, `Task.Result` heuristic hits `53`, and `Wait` heuristic hits `18`.

These are review candidates, not defects. A checked `Task.Result` hit in `GraphTableSqlExecutor` is a synchronous record property false positive; waits are mainly explicit locks or bounded waits. The SqlLexer frozen-set candidate benchmark was attempted but produced no sample because BenchmarkDotNet's internal 120-second build timeout expired; `before/sql-lexer-frozen` is therefore `NO_SAMPLE_BUILD_TIMEOUT`, and no production code was changed for that candidate.

The complete bounded scan also found `1` unsealed-class candidate and `524` sealed classes. The count is descriptive only and is not a performance conclusion.

## Final local verification

- Core full `3990/3990 PASS`; Core targeted `33/33 PASS`; Benchmark tests `25/25 PASS`.
- Server full `722/722 PASS`; Sparkplug targeted `12/12 PASS`, including readiness, deadline, unknown-error and initial publish/stop race contracts.
- Release solution `/warnaserror`: `PASS`, 0 warnings and 0 errors.
- win-x64 Native AOT: CLI and Server publish `PASS`; CLI executable `--version` exited 0 with `SonnetDB CLI 0.0.0-dev+424f61ad16e883d6b9050a9eb29a352105d28cef`. Server was not started.
- CI YAML syntax: `PASS` with YamlDotNet 16.3.0, one document and five root entries; matrix/path inspection found `ubuntu-24.04-arm`, `linux-arm64` and RID separation. actionlint, GitHub schema/execution and real ARM64 remain `NOT_RUN`.
- Solution-wide `dotnet format --verify-no-changes` is `NOT_PASS` (exit 2). Dirty `SqlExecutionMetrics.cs` passes its scoped whitespace verification; only pre-existing, non-dirty `TableManager.cs` and `M27LocalOnnxEvidenceTests.cs` whitespace differences remain. No solution-wide format PASS is claimed.
- M41 before/after quick: local correctness `PASS`; fixed hardware and production gate `NOT_RUN`.

See the full report for unified metrics, concurrency budgets, .NET/AOT/hardware paths, pinned competitor source entries/licenses, Evidence -> Finding -> Path, and the complete P0-P3 queue.
