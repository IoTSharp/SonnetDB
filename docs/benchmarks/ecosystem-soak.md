---
layout: default
title: "生态底座长稳与恢复基线"
description: "EF Core、批量 measurement、KV TTL、对象 multipart、迁移校验、回滚和崩溃恢复的统一验收报告。"
permalink: /benchmarks/ecosystem-soak/
---

## 验收范围

`tests/SonnetDB.EcosystemSoak` 对 Milestone 19 / #121 的通用数据库能力做同一轮组合验收：

1. EF Core Provider 建库、关系写入和查询；
2. `WriteMany` 批量写入和大量 measurement；
3. `IDistributedCache` KV TTL 到期不可见与非过期 key 保留；
4. 对象桶 multipart 分片、完成和逐字节读取校验；
5. `MigrationService` export、scan、checksum、dry-run 和 import；
6. 快照后新增数据不进入恢复目标，验证离线回滚边界；
7. 子进程 `Process.Kill(true)` 后 WAL 重放已确认写入。
8. 追加不完整 WAL 尾记录后重开，验证掉电 torn-tail 恢复。

每个 profile 都输出 `report.json` 和 `report.md`。报告包含操作规模、阶段耗时、吞吐、阶段 working set/托管内存峰值、包级 SHA-256、数据库格式和故障注入方式。

## Profile

| Profile | 轮数 | 关系行 | Measurement | 每 Measurement 点数 | KV TTL key | Multipart |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| quick | 1 | 100 | 64 | 16 | 100 | 3 x 64 KiB |
| ci | 2 | 1,000 | 1,000 | 100 | 1,000 | 4 x 1 MiB |
| soak | 10 | 10,000 | 10,000 | 1,000 | 100,000 | 8 x 5 MiB |

`soak` 一轮包含 1,000 万时序点，默认执行十轮。目标硬件磁盘或 CI 时限不足时，应减少 `--cycles` 做预检，再在固定规格长测机运行完整档；不能用 quick 数字代替生产容量结论。

## #125 专项 Profile

#125 继续使用同一个 runner，新增四个正交 profile：

| Profile | 默认容量档 | 验收重点 | 明确不覆盖 |
| --- | --- | --- | --- |
| `high-cardinality` | 1,000,000 series，单 measurement、每 series 1 点 | catalog/tag index、目录持久化、确定性采样点、reopen 和查询分位数 | 海量 segment、compaction I/O、服务端并发 |
| `small-segments` | 10,000 segment，每段 1 点、32 series | 主动 flush、段发布/枚举、全量 series/time/value 摘要、reopen 和查询分位数 | 稳态 compaction 后的段数；#124 也不会减少物理段或解码成本 |
| `maintenance-chaos` | 64 series、20 次确定性随机 kill/reopen | 后台 flush/compaction/retention 并发、已确认序列缺失/重复/额外点/值、恢复 P50/P95/P99 | 内核崩溃、整机掉电和无限运行证明 |
| `many-measurements` | 10,000 measurement、约 100 segment、drop 100 个 | 目录枚举、备份扫描、retention、drop、reopen 和查询分位数 | 单 measurement 高基数、HTTP/认证/租户开销 |

专项 profile 的默认值是目标硬件容量档，不进入普通 PR 主 CI。开发预检应先用命令行参数缩小规模；正常缩规模和 hosted 运行不提供目标机声明，因此报告为 `targetHardware.status=NOT_READY`。报告即使记录了人为设置的 `PASS` 环境变量，也不能升级为发布证据。完整数字只有在受保护固定硬件执行默认档、核对 bundle 后归档报告才构成发布证据。

完整性摘要区分 `expected`、`observed`、`missing`、`duplicate`、`unexpected`、`value mismatch` 和 digest。`maintenance-chaos` 的每个 worker 会在 `WriteMany` 前持久化该 batch 的序列预留上界，并在写入后才推进 progress；父进程只有在本轮 progress 达到 `max(series, maintenanceBatches * pointsPerBatch)`、且确认点数按完整 batch 对齐后才调用 `Process.Kill`，所以未确认点仅能来自确认范围之后的预留尾部。强杀后总是从预留上界的下一个序列重启，不以 observed/progress 推断下一起点。报告 phase 顶层的 `maintenanceChaosReservations` 逐轮记录 `restart`、`startInclusive`、`endInclusive` 和 `acknowledgedThroughInclusive`。progress 仍是已确认批次的持久性下界；仅已预留但未确认跨度内的恢复点可以计入 `unexpected`，且必须与 `unacknowledgedButRecovered` 相等并不超过该跨度。已确认范围仍严格禁止缺失、重复和值差异；预留范围外的点、任何重复或值差异都会失败。该受控 `Process.Kill` 协议不等同于内核崩溃、整机掉电或无限运行证明。

该场景同时覆盖中断 segment publication 的恢复边界。Core 在 `wal/<segmentId:X16>.SDBFPUB` 维护 64-byte、CRC 保护的 v1 `Pending`/`Committed` marker：先落盘 `Pending`，再写入、重命名并同步 segment 及其父目录，持久化 checkpoint 后才原子写入 `Committed`，随后尽力清理 marker。reopen 会先把遗留 `wal/active.SDBWAL` 升级为 LSN 命名 WAL segment，再在扫描 segment 前对账，使旧 checkpoint 也进入同一判断：只有与有效 segment/header 和精确 durable checkpoint 一致的 `Pending` 才能提升并保留；不存在对应 checkpoint 时清理未发布 artifact、sidecar 和使用当前 `SegmentWriterOptions.TempFileSuffix` 的中断临时文件后重放 WAL；checkpoint/WAL 已越过未解决的 `Pending`、marker 损坏或不匹配、或清理失败均 fail closed。`.SDBFPUB.tmp` 不参与发布判断。该路径收紧受控中断的恢复合同，不把当前 `Process.Kill` 或容量 profile 表述为断电、设备缓存落盘或物理耐久性证明。

恢复和查询分位数使用当前报告内样本的 nearest-rank P50/P95/P99。它们用于比较同机器、同配置的回归，不是跨机器 SLA。报告同时记录父进程的分配/GC/RSS/private-memory/CPU 和 OS-accounted read/write 操作及传输字节，以及 `maintenance-chaos` 子进程的独立资源贡献，二者不得混合解释。I/O 项只反映进程可见的 OS-accounted 计数，不能当作物理盘写放大、设备缓存落盘或 flush 耐久性证明。

专项容量档的耐久性设置也必须随报告审阅：`OpenManual` 关闭 WAL OS flush、segment fsync、后台 flush 和 compaction；maintenance-chaos worker 开启每次写入 WAL 同步、后台 flush/compaction/retention，但仍关闭 segment fsync。该差异用于隔离工作负载，不构成统一生产耐久性承诺。

## 2026-07-12 quick 基线

环境：Windows 10.0.26200、x64、.NET 10.0.9、22 logical processors。该次运行结果为 PASS。

| 阶段 | 规模 | 耗时 |
| --- | ---: | ---: |
| EF Core Provider | 100 rows | 1,333.88 ms |
| 批量时序写入 | 64 measurement / 1,024 points | 275.01 ms |
| KV/cache TTL | 100 expired + 1 durable key | 355.15 ms |
| 对象 multipart | 3 parts / 196,608 bytes | 132.08 ms |
| 迁移、校验、恢复与回滚 | 12 files / 1,333,155 bytes | 250.94 ms |
| 子进程崩溃恢复 | 128 acknowledged / 128 recovered | 2,413.89 ms |
| Torn WAL 掉电恢复 | 1 acknowledged / 1 recovered | 109.09 ms |

迁移包格式为 `SonnetDB/MM9`，包级摘要为 `a79c61144a03d56164991c4f6ba59db74c879e46d655e20ddb596cd29c04021d`。恢复目标成功读取关系表、measurement、缓存 keyspace 和 multipart 对象，并排除了快照后写入的升级探针；掉电阶段追加不完整 WAL 尾记录后仍恢复 1/1 个已确认点。

以上数字只证明该机器、该规模下组合路径可复现，不是服务端 SLA，也不代表 `soak` 容量档已经在目标生产硬件完成。完整命令和参数见 [runner README](../../tests/SonnetDB.EcosystemSoak/README.md)。

## 自动化

`Ecosystem Soak` workflow 每周运行 quick 档，也支持手动选择 quick、ci 或 soak 并归档报告。它不调度四个默认 #125 容量档，也不能生成固定目标硬件 PASS。

`M19 Capacity Evidence` workflow 是唯一的自动化容量取证入口：仅 `main`、固定 `[self-hosted, linux, x64, sonnetdb-m19-capacity-x64-v1]` 标签、受保护 `m19-capacity-frozen-x64` environment 和全局串行锁。非 `main` 的手动触发会在接触目标机前显式失败。它 checkout 触发 SHA 并检查 clean tree，调用 `invoke-m19-capacity-bundle.ps1` 串行运行四个默认 profile；每个 profile 的 `--work` 位于同一 bundle 卷，避免报告磁盘快照与实际数据卷脱节。它生成硬件快照、checkout attestation 和 raw manifest，并在 profile 结束后记录终态 worktree；随后用单报告和 bundle verifier 核对同一 commit、机器、架构、硬件合同、存储型号、原始 JSON/Markdown 哈希及 artifact run URL。无论成功或失败，已生成内容都会上传；任意 profile、终态 worktree 或 verifier 失败则总结果为 `NOT_READY`。

目标机的部署前置条件、受保护变量和 artifact 审阅规则见 [M19 #125 固定目标硬件容量证据](m19-capacity-hardware.md)。

上层应用的 Profile、租户隔离、灰度、双写、业务校验、切流与回滚报告不在此 runner 中。它们必须由上层项目使用公开 SonnetDB 契约单独验收。
