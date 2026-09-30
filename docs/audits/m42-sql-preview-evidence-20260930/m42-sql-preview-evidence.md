# M42 SQL preview local evidence

Mode: `local_full`; rows: 20000; payload characters: 512; preview rows: 100; iterations: 5.
Started UTC: 2026-09-30T08:04:39.6023946+00:00; runtime: .NET 10.0.12; OS: Microsoft Windows 10.0.26200; process: 103892.

| Path | Core P50 ms | Core P50 allocated bytes | Core P50 sampled heap delta bytes | REST P50 first body byte ms | REST P50 complete ms |
| --- | ---: | ---: | ---: | ---: | ---: |
| Full | 52.782 | 38682232 | 22938920 | 73.613 | 146.546 |
| Preview | 0.566 | 312664 | 328920 | 2.385 | 3.110 |

The JSON artifact contains every sample, response byte count, data shape, process identity, and environment details.

- Local synthetic comparison only; it is not fixed-hardware or production SLO evidence.
- Core allocation is synchronous-thread allocation. Sampled and immediate post-execution heap deltas are observations, not retained heap or a CLR heap hard bound.
- REST time to first body byte includes SQL execution and current NDJSON buffering; it does not prove row streaming.
- Full and preview samples alternate after one warmup per path. The client and Kestrel share one process.
