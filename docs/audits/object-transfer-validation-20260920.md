# M36 #323 对象分页与传输验证状态（2026-09-20）

本记录收口当前环境能够重复执行的 #323 预检，并登记固定硬件与完整传输门禁仍未取得的事实。它不把本机嵌入式结果升级为容量或发布证据。

## 本机高变更率预检

执行命令：

```powershell
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --m36-object-validation-evidence --quick --output artifacts/m36-object-validation-current
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --m36-object-validation-evidence --output artifacts/m36-object-validation-current-full
```

两次报告都通过内置 verifier 的合同字段：`localPrecheck=PASS`、`fixedHardware=NOT_READY`、`capacity=NOT_RUN`、`transferValidation=DEFERRED`、`releaseDecision=DEFERRED`。

| 模式 | 对象/样本 | 变更（写/删） | P50/P95/P99 | 分配 P95 |
|---|---:|---:|---:|---:|
| quick | 256 / 64 | 72 / 8 | 1.5252 / 1.8602 / 26.6701 ms | 289,728 B |
| full | 8,192 / 1,024 | 1,152 / 128 | 0.3074 / 1.6152 / 2.2169 ms | 294,784 B |

首样本包含数据库和索引初始化成本；这些是当前 Windows x64 单进程嵌入式样本，不能作为稳定 SLO。runner 不覆盖真实 Server、SDK/CLI 网络路径、multipart 大文件、resume 跨进程重开、断电未知完成结果或 checksum 服务端合同。

## 固定硬件与完整传输门禁

当前没有受保护的固定 x64/ARM64 目标机、冻结硬件/磁盘清单和可追溯外部 artifact，因此 #323 的现场验证保持 `⏳`；代码合同与本机预检已完成。正式验证必须绑定 commit、Server/SDK/CLI 版本和 workload，至少覆盖：

- 8K/更大对象集合的并发 PUT、覆盖、删除、delimiter、continuation 和预算拒绝不推进 token；
- 真实 Kestrel、SDK、CLI 的分页与重开对账，记录 P50/P95/P99、吞吐、RSS、GC、I/O、WAL 和冷启动；
- ≥64 MiB 文件的流式 multipart、取消、幂等分片重试、checksum、跨进程 resume manifest、未知 complete 结果和逐对象错误；
- 失败后临时文件清理、目标文件原子发布、重开后的对象内容与分页一致性。

在上述 artifact 进入受保护 verifier 前，不得把 `fixedHardware`、`capacity` 或 `transferValidation` 改成 `PASS`，也不得用本记录关闭 #323。
