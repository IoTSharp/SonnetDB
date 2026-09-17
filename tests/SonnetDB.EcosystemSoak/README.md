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
| `high-cardinality` | 1,000,000 series，1 点/series | catalog、tag index、采样点完整性、reopen 和查询分位数 |
| `small-segments` | 10,000 segment，1 点/segment | 主动 flush、段枚举、全量点摘要、reopen 和查询分位数 |
| `maintenance-chaos` | 64 series，20 次 kill/reopen | 后台 flush/compaction/retention、已确认点缺失/重复/额外点/值摘要 |
| `many-measurements` | 10,000 measurement，约 100 segment | 目录枚举、备份扫描、retention、drop 和 reopen |

专项容量档应先用缩小参数预检。例如：

```powershell
dotnet run -c Release --project tests/SonnetDB.EcosystemSoak -- `
  --profile maintenance-chaos `
  --series 8 `
  --restart-count 3 `
  --maintenance-batches 2 `
  --points-per-batch 16
```

`maintenance-chaos` 的子进程会在每个 batch 写入前持久化该 batch 的序列预留上界，写入成功后才推进 progress。父进程只在本轮 progress 达到 `max(series, maintenanceBatches * pointsPerBatch)` 的目标值、且确认点数按完整 batch 对齐后才调用 `Process.Kill`；因此允许未确认的点只来自确认范围之后的预留尾部。强杀后按预留上界的下一个序列启动下一 worker，而不是按已观察数据或 progress 推断，避免写入已发生但 progress 尚未落盘时复用序列。报告 phase 顶层的 `maintenanceChaosReservations` 为每次重启记录 `restart`、`startInclusive`、`endInclusive` 和 `acknowledgedThroughInclusive`：已确认范围必须严格无缺失、重复和值差异；只在已预留但尚未确认跨度内恢复的点计入 `unexpected`/`unacknowledgedButRecovered`，两者必须相等；任何预留范围外的点、重复或值差异都会失败。该协议只覆盖受控子进程 `Process.Kill` 的序列归属与恢复对账，不构成内核崩溃、整机掉电或统一生产耐久性证明。

缩规模运行仅证明功能路径。正常开发预检不提供目标机声明，因此输出为 `targetHardware.status=NOT_READY`；即使人为设置环境变量使报告记录 `PASS`，也不构成固定目标硬件或发布证据。普通 hosted `Ecosystem Soak` workflow 只提供 `quick`、`ci` 和 `soak`。四个默认容量档只能由受保护的 `M19 Capacity Evidence` workflow 在固定标签的 self-hosted 目标机上串行执行。

固定目标机的 workflow 会从受保护 environment 读取以下声明，而不是接受 dispatch 输入：

```text
SONNETDB_M19_TARGET_HARDWARE_STATUS=PASS
SONNETDB_M19_TARGET_HARDWARE_ID=<frozen-target-id>
SONNETDB_M19_TARGET_HARDWARE_CONTRACT=<frozen-contract-id>
SONNETDB_M19_STORAGE_MODEL=<verified-storage-model>
```

该 workflow 只接受 GitHub 标记为受保护的 `main` 上的 `workflow_dispatch`，固定到触发时的 SHA，并要求 checkout 无改动。它通过 `invoke-m19-capacity-bundle.ps1` 归档 `target-hardware.json`、四个 profile 的 `report.json`/`report.md`、bundle verifier 输出、checkout attestation 和带 SHA-256 的 `raw-artifact-manifest.json`。每个 profile 都以 `--work <bundleRoot>/work/<profile>` 运行，使报告的磁盘快照覆盖实际 workload 与 evidence 所在卷；profile 执行后还会把终态 checkout 状态写入 manifest，任何改动都会使 bundle 保持 `NOT_READY`。存储型号必须在 protected declaration、checkout attestation、硬件快照和四份报告中完全一致。目标硬件、环境变量保护和 artifact 保留策略的配置说明见 [M19 固定目标硬件证据](../../docs/benchmarks/m19-capacity-hardware.md)。

通用参数包括 `--cycles`、`--measurements`、`--points-per-measurement`、`--relational-rows`、`--cache-entries`、`--multipart-parts` 和 `--multipart-part-bytes`。专项参数包括 `--series`、`--target-segments`、`--points-per-segment`、`--restart-count`、`--recovery-samples`、`--query-samples`、`--maintenance-batches`、`--points-per-batch`、`--drop-measurements` 和 `--random-seed`。

输出目录包含 `report.json` 和 `report.md`。两种报告均包含阶段 working set/托管内存峰值、父/子进程资源来源、分配与 GC、进程 OS-accounted read/write 操作和传输字节、恢复与查询 P50/P95/P99、结构化完整性摘要、环境/磁盘/provenance 和实际有效配置。I/O 计数只描述进程可见的 OS-accounted 活动，不代表物理盘写放大、设备缓存落盘或 flush 完成。报告会明确区分 `OpenManual` 的弱耐久性设置（WAL OS flush 和 segment fsync 均关闭）与 maintenance-chaos worker 的逐写 WAL 同步；后者仍关闭 segment fsync。因此这四档不能被表述为统一生产耐久性 SLA。

长稳数字只对报告中记录的机器、运行时和配置有效，不是服务端 SLA。上层应用 Profile 的灰度、双写、回滚和 SLA 报告仍由上层仓库维护。
