# Ecosystem Soak Runner

该 runner 对应 Milestone 19 / #121，统一覆盖：

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

可以用 `--cycles`、`--measurements`、`--points-per-measurement`、`--relational-rows`、`--cache-entries`、`--multipart-parts` 和 `--multipart-part-bytes` 覆盖规模。输出目录包含 `report.json` 和 `report.md`。

长稳数字只对报告中记录的机器、运行时和配置有效，不是服务端 SLA。上层应用 Profile 的灰度、双写、回滚和 SLA 报告仍由上层仓库维护。
