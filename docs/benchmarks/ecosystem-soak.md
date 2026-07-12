---
layout: default
title: "生态底座长稳与恢复基线"
description: "EF Core、批量 measurement、KV TTL、对象 multipart、迁移校验、回滚和崩溃恢复的统一验收报告。"
permalink: /benchmarks/ecosystem-soak/
---

## 验收范围

`tests/SonnetDB.EcosystemSoak` 对 Milestone 19 的通用数据库能力做同一轮组合验收：

1. EF Core Provider 建库、关系写入和查询；
2. `WriteMany` 批量写入和大量 measurement；
3. `IDistributedCache` KV TTL 到期不可见与非过期 key 保留；
4. 对象桶 multipart 分片、完成和逐字节读取校验；
5. `MigrationService` export、scan、checksum、dry-run 和 import；
6. 快照后新增数据不进入恢复目标，验证离线回滚边界；
7. 子进程 `Process.Kill(true)` 后 WAL 重放已确认写入。
8. 追加不完整 WAL 尾记录后重开，验证掉电 torn-tail 恢复。

每个 profile 都输出 `report.json` 和 `report.md`。报告包含操作规模、阶段耗时、吞吐、托管内存、包级 SHA-256、数据库格式和故障注入方式。

## Profile

| Profile | 轮数 | 关系行 | Measurement | 每 Measurement 点数 | KV TTL key | Multipart |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| quick | 1 | 100 | 64 | 16 | 100 | 3 x 64 KiB |
| ci | 2 | 1,000 | 1,000 | 100 | 1,000 | 4 x 1 MiB |
| soak | 10 | 10,000 | 10,000 | 1,000 | 100,000 | 8 x 5 MiB |

`soak` 一轮包含 1,000 万时序点，默认执行十轮。目标硬件磁盘或 CI 时限不足时，应减少 `--cycles` 做预检，再在固定规格长测机运行完整档；不能用 quick 数字代替生产容量结论。

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

`Ecosystem Soak` workflow 每周运行 quick 档，也支持手动选择 quick、ci 或 soak 并归档报告。故障会返回非零退出码，同时仍上传已经生成的报告。

上层应用的 Profile、租户隔离、灰度、双写、业务校验、切流与回滚报告不在此 runner 中。它们必须由上层项目使用公开 SonnetDB 契约单独验收。
