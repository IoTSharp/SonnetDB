---
layout: default
title: "M19 #125 固定目标硬件容量证据"
description: "四个生态容量 profile 的固定目标硬件报告契约和当前取证状态。"
permalink: /benchmarks/m19-capacity-hardware/
---

# M19 #125 固定目标硬件容量证据

## 当前状态

`研发完成，待验证`。仓库包含可复现的 `high-cardinality`、`small-segments`、`maintenance-chaos` 和 `many-measurements` runner、固定目标机 workflow、报告 schema 与 verifier；当前没有四个 profile 在 ROADMAP 指定固定规格目标硬件上的归档 PASS artifact。开发机 quick/ci/缩规模运行和历史结果不能替代容量证据，因此发布证据状态仍为 `NOT_READY`。

本轮 flush/维护发布优化改变了目录 fsync 次数和段发布分配路径；任何旧容量或延迟数字均不能直接作为优化后的基线。固定目标机取证必须使用优化后的 commit 重新采集写入、查询、恢复 P50/P95/P99、分配、GC 和进程 I/O。

## 必须归档的四份报告

| Profile | 默认容量档 | 必需完整性范围 |
| --- | ---: | --- |
| `high-cardinality` | 1,000,000 series、每 series 1 点 | catalog、采样点的 missing/duplicate/unexpected/value mismatch |
| `small-segments` | 10,000 segment、每段 1 点 | 段数量、全量点摘要和恢复后的完整性 |
| `maintenance-chaos` | 64 series、20 次确定性 kill/reopen | 已确认批次及每轮恢复完整性 |
| `many-measurements` | 10,000 measurement、约 100 segment | 目录、drop/retention、备份扫描和恢复完整性 |

`maintenance-chaos` 还必须归档 phase 顶层的 `maintenanceChaosReservations`。每项记录 `restart`、`startInclusive`、`endInclusive` 和 `acknowledgedThroughInclusive`：worker 在 batch 写入前持久化预留上界，成功后才推进 progress；父进程只在本轮 progress 达到 `max(series, maintenanceBatches * pointsPerBatch)` 且确认点数按完整 batch 对齐后强杀，因此未确认点仅限确认范围之后的预留尾部；下一轮从预留末尾加一开始，不能由 observed/progress 反推。已确认范围必须无 missing/duplicate/value mismatch；恢复到已预留但未确认跨度内的点计入 `unexpected`，并与 `unacknowledgedButRecovered` 精确相等且不超过该跨度；预留范围外的点、重复或值差异均使报告失败。此项仅审计受控 `Process.Kill` 的序列归属和 reopen 行为，不作为电源故障或物理落盘耐久性结论。

每份 `report.json` 还必须包含：提交 SHA、机器/架构/CPU/内存、工作目录所在磁盘的文件系统/总容量/可用容量、持续时间、父/子进程资源来源、working set/托管内存峰值、分配/GC/CPU、进程 OS-accounted read/write 操作及传输字节、恢复和查询 P50/P95/P99，以及完整性计数。I/O 计数仅限进程可见的 OS-accounted 活动，不能作为物理盘写放大、设备缓存落盘或 flush 耐久性证据。runner 将这些环境快照写入 `environment`、`evidence` 和 `effectiveConfiguration`，并写入 `targetHardware.status/id/contract`。后者默认是 `NOT_READY`，会原样记录显式环境声明；单份报告中的 `PASS` 不是固定目标机真实性或发布证据：

```powershell
$env:SONNETDB_M19_TARGET_HARDWARE_STATUS = 'PASS'
$env:SONNETDB_M19_TARGET_HARDWARE_ID = 'frozen-x64-runner-01'
$env:SONNETDB_M19_TARGET_HARDWARE_CONTRACT = 'M19-#125-frozen-target-v1'
```

还必须提供 `SONNETDB_M19_STORAGE_MODEL`，以便报告和硬件快照将存储型号与已声明合同关联。在 `M19 Capacity Evidence` workflow 中，这些变量由受保护 environment 提供；它们仍只是硬件合同元数据，不是硬件真实性证明。只有该 workflow 的 bundle attestation 和 verifier 都通过，声明才可参与发布证据判定。bundle 只能核对 artifact 内记录的 GitHub 上下文，不能独立证明 runner 标签授权或 environment 的保护策略；报告审阅仍需核对关联的受保护 run、机器清单、磁盘清单/命令输出和 artifact 追溯链。

## 目标机与自动化前置条件

容量证据只能使用 `.github/workflows/m19-capacity-evidence.yml` 的 `M19 Capacity Evidence` workflow。仓库管理员必须先配置以下不可变边界：

1. self-hosted runner 同时带有 `self-hosted`、`linux`、`x64` 和 `sonnetdb-m19-capacity-x64-v1` 标签，且标签只授予冻结目标机；runner group 必须在取证期间拒绝其他 workload，并保持单一 runner executor；
2. protected environment `m19-capacity-frozen-x64` 审批后才允许工作流读取 `SONNETDB_M19_TARGET_HARDWARE_STATUS`、`SONNETDB_M19_TARGET_HARDWARE_ID`、`SONNETDB_M19_TARGET_HARDWARE_CONTRACT` 和 `SONNETDB_M19_STORAGE_MODEL`；
3. 状态变量固定为 `PASS`，ID/contract/storage model 与已审批机器清单完全一致；它们不是 workflow dispatch 输入；
4. 仅从 GitHub 标记为受保护的 `main` 手动触发（`GITHUB_EVENT_NAME=workflow_dispatch` 且 `GITHUB_REF_PROTECTED=true`）。非 `main`、未受保护 ref 或其他事件会在接触目标机前失败；workflow 以 `github.sha` checkout、要求 clean tree、记录 checkout attestation，并使用仓库内全局 concurrency lock 串行化 M19 workflow。GitHub concurrency 不能代替第 1 条的 runner 隔离；
5. 目标机须预装 `global.json` 所需 .NET SDK、PowerShell 7、Git，以及 Linux `findmnt`（工作目录挂载解析）和 `lsblk`（磁盘清单）命令；工作目录和 artifact 保留策略必须满足四个默认 profile 的容量与保留期。若任一工具不可用，硬件快照应保持不可用并由 bundle verifier 判为 `NOT_READY`，不得手工填补。

不满足任何一项时，不得手工补写 `PASS` 或改动报告 JSON；应停止在 `NOT_READY` 并修复目标机配置。

## 执行与检查

`M19 Capacity Evidence` workflow 会在同一干净 checkout 中调用 `invoke-m19-capacity-bundle.ps1` 串行执行四个默认 profile。每个 profile 以 `--work <bundleRoot>/work/<profile>` 运行，因此 `report.json` 的磁盘快照对应实际 workload 与 evidence 所在卷。它自动生成 `target-hardware.json`、`checkout-attestation.json`、每个 profile 的 `report.json`/`report.md`、`raw-artifact-manifest.json` 和 `m19-capacity-bundle-verification.json`，再上传为单个 artifact。raw manifest 为每个原始文件记录 SHA-256，并在四个 profile 结束后记录终态 worktree 状态；任一 checkout 改动都会被 verifier 拒绝。bundle verifier 还要求 protected storage declaration、checkout attestation、硬件快照和四份报告的存储型号完全一致。artifact run URL 被写入 bundle verifier 输出，便于从报告回溯 GitHub Actions run。

仅在隔离的目标机诊断中，才可分别执行默认 profile；这不会替代 workflow 的 bundle attestation：

```powershell
dotnet run -c Release --project tests/SonnetDB.EcosystemSoak -- --profile high-cardinality --output artifacts/m19/high-cardinality
dotnet run -c Release --project tests/SonnetDB.EcosystemSoak -- --profile small-segments --output artifacts/m19/small-segments
dotnet run -c Release --project tests/SonnetDB.EcosystemSoak -- --profile maintenance-chaos --output artifacts/m19/maintenance-chaos
dotnet run -c Release --project tests/SonnetDB.EcosystemSoak -- --profile many-measurements --output artifacts/m19/many-measurements
```

目标机诊断可对单份报告运行 schema/证据检查：

```powershell
pwsh -File tests/SonnetDB.EcosystemSoak/scripts/verify-m19-capacity-report.ps1 -ReportPath artifacts/m19/high-cardinality/report.json
```

正式 bundle 必须使用统一入口，它先采集硬件快照，再串行运行四个默认 profile、写 raw manifest，最后运行 bundle verifier。下面的命令是受保护的 `M19 Capacity Evidence` workflow step 示例；由于入口刻意要求 GitHub Actions、`main` 和受保护环境上下文，普通本地 shell 直接运行会保持失败/`NOT_READY`，本地诊断请使用上面的单 profile 命令：

```powershell
$commit = (git rev-parse --verify HEAD).Trim()
pwsh -File tests/SonnetDB.EcosystemSoak/scripts/invoke-m19-capacity-bundle.ps1 `
  -BundleRoot artifacts/m19 `
  -ArtifactUrl https://github.com/<owner>/<repo>/actions/runs/<run-id> `
  -TargetHardwareId $env:SONNETDB_M19_TARGET_HARDWARE_ID `
  -TargetHardwareContract $env:SONNETDB_M19_TARGET_HARDWARE_CONTRACT `
  -ExpectedCommitSha $commit
```

单报告 verifier 重算原始延迟样本的 nearest-rank P50/P95/P99，并核对默认容量参数、阶段、完整性、目标声明与环境。bundle verifier 还核对四份报告和硬件快照的 commit、机器、架构、硬件 ID/contract、存储声明一致性、JSON/Markdown SHA-256、raw manifest、终态 worktree 和 artifact URL。它们不会把非目标硬件或缩规模结果升级为发布证据。硬件不可用时，报告和本页必须保持 `NOT_READY`，并记录阻塞原因、目标机标识和待执行命令。

## 耐久性解释

容量报告的 `effectiveConfiguration` 是验收的一部分，不能用一个 profile 的耐久性推断另一个 profile。`high-cardinality`、`small-segments` 和大部分 `many-measurements` 路径经由 `OpenManual` 运行，关闭 WAL OS flush、segment fsync、后台 flush 和 compaction，以便隔离目录/segment 工作负载。`maintenance-chaos` worker 启用逐写 WAL 同步、后台 flush、compaction 和 retention，但 segment fsync 仍关闭；验证 reopen 也使用单独的维护配置。因而四份报告共同说明给定配置下的容量和恢复行为，不能声称所有路径达到同一掉电耐久性或生产 SLA。

`maintenance-chaos` 也验证 interrupted segment publication 的恢复合同：Core 使用 `wal/<segmentId:X16>.SDBFPUB` 64-byte CRC-protected v1 sidecar，记录 `Pending`/`Committed`、segment ID、checkpoint LSN 和创建时间。`Pending` 在 segment 最终写入前同步；只有 segment、其父目录和 durable checkpoint 均完成后，才原子持久化 `Committed`。reopen 先把遗留 `wal/active.SDBWAL` 升级为 LSN 命名段，再在 segment 扫描前对账：有效、精确 checkpoint 的 `Pending` 可提升保留；没有覆盖 checkpoint 的 `Pending` 清理未发布 artifact 后由 WAL 回放；WAL/checkpoint 已跨越未解决 marker、marker 损坏/不匹配或清理失败一律 fail closed。`.SDBFPUB.tmp` 被忽略。此合同防止受控中断发布被静默接受，不把 profile 的 `Process.Kill`、关闭的 segment fsync 或报告 I/O 计数升级为电源故障或物理持久性证明。

## 发布判定

四个 profile 的研发交付已完成；只有它们均在同一份冻结目标硬件合同下完成，且报告中的 commit、配置、原始 JSON/Markdown、硬件快照、checkout attestation、SHA-256 raw manifest 和 artifact URL 可追溯时，才能把 M19 #125 的外部验收标记为通过。任一 profile 缺失、失败、环境字段不可用、工作树不干净或完整性/分位数缺失，都保持 `NOT_READY`。
