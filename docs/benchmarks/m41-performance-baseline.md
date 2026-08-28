# M41 性能合同与可观测性基线（#368）

本基线固定木垒关系查询暴露出的五类形状，并用确定性合成数据验证结果与执行证据合同：

| 工作负载 | 固定形状 | 后续优化归属 |
| --- | --- | --- |
| `indexed_exists` | 唯一幂等键 `EXISTS` 加非覆盖残余条件 | #369 |
| `scalar_in` | 单列非相关 `IN (SELECT ...)` | #369 |
| `nullable_or` | `IS NULL OR range` | #370 |
| `multi_table_join` | 任务与设备等值 JOIN 加残余过滤 | #372/#378 |
| `descending_pagination` | 复合排序键倒序 `LIMIT/OFFSET` | #371 |

数据集 `mulei-relational-synthetic-v1` 固定设备、任务与审计表的生成规则和 seed `20260815`。quick 模式使用 64 条任务、64 条审计和 8 台设备；本地完整模式使用 10,000 条任务、10,000 条审计及 128 台设备。初始化不计入查询样本。

## 运行

快速验证报告管线：

```powershell
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --m41-baseline-evidence --quick --output artifacts/m41-performance-baseline
```

本地完整规模：

```powershell
dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --m41-baseline-evidence --output artifacts/m41-performance-baseline
```

输出文件为 `m41-performance-baseline.json` 和 `m41-performance-baseline.md`。JSON 使用 source-generated metadata，包含 commit、运行环境、规范化 SQL/fingerprint、访问路径、索引、fallback、候选/检查/返回行、检查放大比、P50/P95/P99 执行时间与分配、锁/WAL 等待、逻辑/物理 I/O 和 GC 次数。报告不包含参数值、行内容、数据库目录或机器名。

## 证据边界

runner 使用嵌入式 SQL 执行以隔离 Core 查询成本，不经过 Server SQL permit，因此 `sqlPermitQueueEvidence` 固定为 `NOT_APPLICABLE_EMBEDDED`，队列等待证据必须在 REST/Frame 生产复测中采集。quick 与本地完整运行都只验证工作负载、正确性和报告合同，不代表固定硬件容量、尾延迟 SLO 或生产发布结论。

在固定目标硬件报告归档前，`fixedHardware` 与 `productionGate` 必须保持 `NOT_RUN`。后续 #369～#380 使用同一数据与查询名称做前后对比；#381 负责本地收口合同并登记 x64/ARM64 固定硬件、七天 mixed workload 和最终生产门禁的后置验证，不把本地报告视为生产通过。
