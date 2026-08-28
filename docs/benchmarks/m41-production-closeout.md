---
layout: default
title: M41 #381 生产收口
description: M41 查询规划与执行优化的本地收口报告和现场验证后置清单。
permalink: /benchmarks/m41-production-closeout/
---

# M41 #381 生产收口

M41 的查询规划与执行优化已经在本地完成实现、差分和自动化门禁。#381 本次交付的是可重复的本地收口报告；现场运行相关的验证明确后置，不把开发机或合成数据结果当作生产发布结论。

## 运行

```powershell
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --m41-production-closeout --quick --output artifacts/m41-production-closeout
```

完整本地语料去掉 `--quick` 即可运行。报告目录包含：

- `m41-production-closeout.json`：source-generated JSON，记录本地收口状态、稳定检查 ID 和后置验证清单；
- `m41-production-closeout.md`：便于审阅的同内容摘要；
- `baseline/`：由 #368 runner 生成的五类查询执行证据。

`localCloseout=PASS` 表示本地报告合同和 #368 基线通过；`releaseDecision=DEFERRED` 是预期结果，不能被 CI 或发布脚本解释为 `PASS`。

## 后置验证

以下项目统一保持 `DEFERRED`，触发条件是后续部署或现场分析取得可归档的原始 artifact：

| ID | 触发阶段 | 必须补充的证据 |
| --- | --- | --- |
| `field_concurrency_transactions` | 真实 reader/writer 混合负载 | 事务可见性、锁等待、队列等待和尾延迟 |
| `process_crash_replay` | 目标节点真进程 kill/reopen | WAL replay、checkpoint、恢复时间和逐模型不变量 |
| `backup_restore_deployment` | 现场备份介质与权限 | backup/restore 后逐模型数据对拍 |
| `native_aot_target_rids` | 实际 x64/ARM64 RID 部署 | CLI/Server 启动、原生依赖和首查 smoke |
| `fixed_hardware_x64_arm64` | 固定目标硬件 | P50/P95/P99、RSS、分配、GC、锁/队列等待和 I/O |
| `seven_day_mixed_workload` | 连续 168 小时运行 | 吞吐、尾延迟、内存上界、WAL/checkpoint 和异常重启 |
| `mulei_same_corpus` | 木垒现场同语料分析 | examined/returned amplification 与现网查询 fingerprint 对账 |

完成这些验证后，应更新同一报告 schema 的后置项并重新经过发布评审；在此之前，优化仍依赖现有正确性回退、资源预算和 feature gate 保护。
