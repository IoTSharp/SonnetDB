---
layout: default
title: "M19 #125 固定目标硬件容量证据"
description: "四个生态容量 profile 的固定目标硬件报告契约和当前取证状态。"
permalink: /benchmarks/m19-capacity-hardware/
---

# M19 #125 固定目标硬件容量证据

## 当前状态

`研发完成，待验证`。仓库包含可复现的 `high-cardinality`、`small-segments`、`maintenance-chaos` 和 `many-measurements` runner、workflow、报告 schema 与 verifier；当前没有四个 profile 在 ROADMAP 指定固定规格目标硬件上的归档 PASS artifact。开发机 quick/ci/缩规模运行和历史结果不能替代容量证据，因此发布证据状态仍为 `NOT_READY`。

## 必须归档的四份报告

| Profile | 默认容量档 | 必需完整性范围 |
| --- | ---: | --- |
| `high-cardinality` | 1,000,000 series、每 series 1 点 | catalog、采样点的 missing/duplicate/unexpected/value mismatch |
| `small-segments` | 10,000 segment、每段 1 点 | 段数量、全量点摘要和恢复后的完整性 |
| `maintenance-chaos` | 64 series、20 次确定性 kill/reopen | 已确认批次及每轮恢复完整性 |
| `many-measurements` | 10,000 measurement、约 100 segment | 目录、drop/retention、备份扫描和恢复完整性 |

每份 `report.json` 还必须包含：提交 SHA、机器/架构/CPU/内存、工作目录所在磁盘的文件系统/总容量/可用容量、持续时间、working set/托管内存峰值、恢复和查询 P50/P95/P99，以及完整性计数。runner 现在会将这些环境快照写入 `environment.commitSha` 和 `environment.disk`，并写入 `targetHardware.status/id/contract`。后者默认是 `NOT_READY`，只有固定目标机执行者显式提供声明才可能为 `PASS`：

```powershell
$env:SONNETDB_M19_TARGET_HARDWARE_STATUS = 'PASS'
$env:SONNETDB_M19_TARGET_HARDWARE_ID = 'frozen-x64-runner-01'
$env:SONNETDB_M19_TARGET_HARDWARE_CONTRACT = 'M19-#125-frozen-target-v1'
```

这些变量只记录执行者提供的硬件合同元数据；它们不是硬件真实性证明。报告审阅仍需核对机器清单、磁盘截图/命令输出和 artifact 追溯链。

## 执行与检查

在固定目标机分别执行默认 profile，并把输出目录作为不可变 artifact 保存：

```powershell
dotnet run -c Release --project tests/SonnetDB.EcosystemSoak -- --profile high-cardinality --output artifacts/m19/high-cardinality --keep-data
dotnet run -c Release --project tests/SonnetDB.EcosystemSoak -- --profile small-segments --output artifacts/m19/small-segments --keep-data
dotnet run -c Release --project tests/SonnetDB.EcosystemSoak -- --profile maintenance-chaos --output artifacts/m19/maintenance-chaos --keep-data
dotnet run -c Release --project tests/SonnetDB.EcosystemSoak -- --profile many-measurements --output artifacts/m19/many-measurements --keep-data
```

每个报告生成后运行 schema/证据检查：

```powershell
pwsh -File tests/SonnetDB.EcosystemSoak/scripts/verify-m19-capacity-report.ps1 -ReportPath artifacts/m19/high-cardinality/report.json
```

检查脚本只验证报告结构、PASS 结果和环境快照完整性；它不会把非目标硬件或缩规模结果升级为发布证据。硬件不可用时，报告和本页必须保持 `NOT_READY`，并记录阻塞原因、目标机标识和待执行命令。

## 发布判定

四个 profile 的研发交付已完成；只有它们均在同一份冻结目标硬件合同下完成，且报告中的 commit、配置、原始 JSON/Markdown 和 artifact URL 可追溯时，才能把 M19 #125 的外部验收标记为通过。任一 profile 缺失、失败、环境字段不可用或完整性/分位数缺失，都保持 `NOT_READY`。
