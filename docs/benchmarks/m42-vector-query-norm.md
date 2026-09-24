# M42 向量批量余弦性能切片

**核查日期：2026-09-21**
**范围：M42 P1「向量 query norm 复用」**

本切片把批量余弦扫描中的查询向量范数平方从“每个候选行重新计算”改为“每次批量扫描计算一次”。候选向量仍在每行计算点积和自身范数，余弦距离的零向量、维度错误和裁剪语义保持不变。实现不修改持久化格式、索引文件或距离排序规则。

## 实现与证据

| 项目 | 结果 | 证据 |
| --- | --- | --- |
| 生产路径 | `PASS` | `CpuTensorPrimitivesScorer` 在 `Metric.Cosine` 下缓存一次 `Distance.NormSquared(query)` |
| 公共距离 API | `PASS` | `Distance.CosineWithQueryNormSquared` 显式接收 query norm squared |
| 数值差分 | `PASS` | `CosineWithQueryNormSquared_MatchesRegularPath_AndHandlesZeroQuery` |
| 批量路径差分 | `PASS` | `CpuScorer_Cosine_ReusesQueryNorm_AndMatchesIndependentRows` |
| 输入边界 | `PASS` | 空向量、零向量、维度不一致和空输出覆盖 |
| Core 定向测试 | `PASS` | `BatchScorerTests`：11/11，Release，0 skipped |
| Benchmark 项目构建 | `PASS` | Release，0 warnings / 0 errors |
| 本机 x64 BDN 短跑 | `DIRECTIONAL` | 64/1,000：0.68x；64/10,000：0.69x；384/1,000：0.84x（波动较大）；384/10,000：0.62x。原始 CSV 保留在 `artifacts/m42-vector-query-norm/results/` |
| 固定 x64 目标机 | `NOT_RUN` | 尚无受保护的固定硬件 artifact |
| 固定 ARM64 目标机 | `NOT_RUN` | 尚无实体 ARM64 artifact；CI matrix 不等于运行证据 |

本地测试只证明合同和结果一致，不证明吞吐倍数、容量或生产尾延迟。没有固定硬件、冻结 commit、磁盘和运行时清单时，不得将 benchmark 输出标为发布级性能结论。

## 复现

在仓库根目录执行以下命令。BenchmarkDotNet 会输出基线（每行重算 query norm）与优化路径（复用 query norm）的 Median、P90 和分配信息。

```powershell
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj `
  --configuration Release --no-restore `
  --filter FullyQualifiedName~BatchScorerTests

dotnet run --configuration Release --project tests/SonnetDB.Benchmarks/SonnetDB.Benchmarks.csproj -- `
  --filter '*VectorCosineBatchBenchmark*' `
  --artifacts artifacts/m42-vector-query-norm
```

固定目标机运行时必须额外记录：

- `git` commit、工作树是否干净、.NET SDK/runtime 和 BenchmarkDotNet 版本；
- CPU 架构、型号、核心数、SIMD 支持、操作系统、容器/虚拟机信息；
- 磁盘型号、文件系统、挂载选项、电源/CPU governor 和后台负载；
- `Dimension`（64、384）与 `CandidateCount`（1,000、10,000）四个组合的原始 BDN 结果；
- Median/P90/P99（若 BDN 样本不足，P99 必须记为 `INSUFFICIENT_SAMPLE`）、bytes/op、GC 和校验和；
- x64 与 ARM64 各自的 scalar 差分结果。不得根据 CPU 型号推断具体 ISA 已执行。

## 固定硬件门禁

| 门禁 | 退出条件 | 当前状态 |
| --- | --- | --- |
| 正确性 | 优化路径与逐行参考路径逐样本一致；零/NaN/维度边界通过 | `PASS`（本机合同） |
| 资源 | 批量扫描无额外候选结果分配；异常/取消不泄漏租借缓冲 | `PASS`（单元合同；无取消 API） |
| x64 性能 | 固定目标机四组合至少 3 个有效测量窗口，提交原始 artifact 与环境清单 | `NOT_RUN` |
| ARM64 性能 | 实体 ARM64 同 workload、scalar 差分和 Native AOT/运行时记录 | `NOT_RUN` |
| 生产门禁 | 168 小时 mixed workload、冷/热启动和恢复报告引用本切片结果 | `NOT_RUN` |

该切片只关闭 M42 P1 向量 query norm 复用的代码合同。页感知索引成本、参数敏感计划、独立 I/O 预算、covering/index-only、大值复制、冷启动统一指标以及固定 x64/ARM64 和 168 小时证据仍保持 M42 原有状态，不在本报告中重复声明完成。
