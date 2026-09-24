# Document Store Soak Profiles

本工具是 Milestone 25 / #174 的发布验收 runner，不进入主 CI gate。它依次执行：批量写入、索引创建、索引查询、在线 rebuild、TTL 清理、热重开、独立子进程冷启动、子进程异常退出恢复、备份恢复，以及工作集 / private bytes / managed heap 采样。冷启动表示新进程首次打开，runner 不尝试用高权限命令清空 OS page cache，报告会显式记录该边界。

## Profiles

| Profile | 文档数 | 用途 |
|---|---:|---|
| `quick` | 10,000 | 开发机冒烟与 runner 回归 |
| `million` | 1,000,000 | 发布候选容量验收 |
| `ten-million` | 10,000,000 | 专用长测机边界验证 |

```powershell
dotnet run --project tests/SonnetDB.DocumentSoak/SonnetDB.DocumentSoak.csproj -c Release -- `
  --profile million `
  --output artifacts/document-soak/million
```

可用 `--documents N` 做自定义规模，`--work-root PATH --keep-data` 保留数据库、备份和恢复目录供取证。输出固定包含 `report.json` 和 `report.md`。运行失败时仍写报告，并以非零退出码结束。

发布候选的固定硬件运行必须由操作员显式声明目标机并完成现场核对：

```powershell
dotnet run --project tests/SonnetDB.DocumentSoak/SonnetDB.DocumentSoak.csproj -c Release -- `
  --profile million `
  --target-hardware-id <inventory-id> `
  --disk-model <model> `
  --target-hardware-attested `
  --output artifacts/document-soak/million
```

`report.json` 使用 schema v2，包含 commit SHA、数据卷容量和目标硬件合同。未提供认证参数时硬件状态保持 `NOT_READY`。归档前运行只读 verifier：

固定目标机合同必须为 `M25-#174-fixed-target-v1`；verifier 会拒绝其他合同值。`--target-hardware-attested` 只是运行者声明，不能替代已审批的目标机清单、磁盘清单和 artifact 追溯链。

```powershell
pwsh -NoProfile -File tests/SonnetDB.DocumentSoak/scripts/verify-document-soak-evidence.ps1 `
  -Report artifacts/document-soak/million/report.json `
  -ExpectedCommitSha <40-character-commit-sha> `
  -ExpectedTargetHardwareId <inventory-id>
```

verifier 会把完整 million/ten-million 报告的结构结果写为 `reportStatus=PASS`，但当前仓库没有受保护 CI artifact bundle 或独立可核验 attestation 合同，因此发布结果强制为 `status=NOT_READY`、`releaseDecision=DEFERRED` 和 `releaseEvidence=false`。`targetHardware.status=PASS`、`ExpectedCommitSha` 和 `ExpectedTargetHardwareId` 只用于报告字段与预期身份的校验，不能构成信任根。`quick`、缺失阶段、失败运行、身份不匹配、无效 commit 或未认证固定目标机同样返回 `NOT_READY`。开发机预检可加 `-AllowNotReady`，但不会改变报告状态。

只有未来由受保护 CI 生成、绑定 commit SHA、目标机清单和 artifact 内容摘要的外部 attestation bundle，且 verifier 能独立核验该 bundle 后，`releaseDecision` 才可以从 `DEFERRED` 改为发布就绪；本地运行和手工编辑 JSON 永远不能升级为 release evidence。

百万 / 千万档必须在专用磁盘和固定硬件上执行，报告需与 commit SHA、OS、.NET runtime、CPU 数及磁盘型号一起归档。不得把 quick profile 数字线性外推成容量承诺。
