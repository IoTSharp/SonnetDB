# Ecosystem Soak Runner

该 runner 对应 Milestone 19 / #121 与 #125。`quick` / `ci` / `soak` 统一覆盖：

- EF Core Provider 建库、写入和查询；
- `WriteMany` 批量写入与大量 measurement；
- `IDistributedCache` KV TTL；
- 对象桶 multipart 上传和内容校验；
- `MigrationService` export/scan/checksum/import；
- 快照后写入排除与离线回滚恢复；
- 真子进程 `Process.Kill(true)` 后 WAL 恢复。
- 不完整 WAL 尾记录的掉电恢复模型。

快速验收：

```powershell
dotnet run -c Release --project tests/SonnetDB.EcosystemSoak -- --profile quick
```

CI 档：

```powershell
dotnet run -c Release --project tests/SonnetDB.EcosystemSoak -- --profile ci --output artifacts/ecosystem-soak/ci
```

长稳档默认执行 10 轮，每轮 10,000 measurement、每 measurement 1,000 点：

```powershell
dotnet run -c Release --project tests/SonnetDB.EcosystemSoak -- --profile soak --keep-data
```

#125 在同一个 runner 中增加四个正交专项 profile，不把维度相乘成不可运行的巨型场景：

| Profile | 默认规模 | 主要验收 |
| --- | --- | --- |
| `high-cardinality` | 1,000,000 series，1 点/series | catalog、tag index、采样点完整性、冷启动和查询分位数 |
| `small-segments` | 10,000 segment，1 点/segment | 主动 flush、段枚举、全量点摘要、冷启动和查询分位数 |
| `maintenance-chaos` | 64 series，20 次 kill/reopen | 后台 flush/compaction/retention、已确认点缺失/重复/额外点/值摘要 |
| `many-measurements` | 10,000 measurement，约 100 segment | 目录枚举、备份扫描、retention、drop 和冷启动 |

专项容量档应先用缩小参数预检。例如：

```powershell
dotnet run -c Release --project tests/SonnetDB.EcosystemSoak -- `
  --profile maintenance-chaos `
  --series 8 `
  --restart-count 3 `
  --maintenance-batches 2 `
  --points-per-batch 16
```

通用参数包括 `--cycles`、`--measurements`、`--points-per-measurement`、`--relational-rows`、`--cache-entries`、`--multipart-parts` 和 `--multipart-part-bytes`。专项参数包括 `--series`、`--target-segments`、`--points-per-segment`、`--restart-count`、`--recovery-samples`、`--query-samples`、`--maintenance-batches`、`--points-per-batch`、`--drop-measurements` 和 `--random-seed`。

输出目录包含 `report.json` 和 `report.md`。两种报告均包含阶段 working set/托管内存峰值、恢复与查询 P50/P95/P99、结构化完整性摘要，以及当前 profile 能验证和不能证明的容量边界。

长稳数字只对报告中记录的机器、运行时和配置有效，不是服务端 SLA。上层应用 Profile 的灰度、双写、回滚和 SLA 报告仍由上层仓库维护。
