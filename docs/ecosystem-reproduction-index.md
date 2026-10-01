# M43 #401 生态案例与基准复现索引

**状态：本地索引已整理；外部案例、固定硬件基准与发布级证据仍待取得。** 本页复用已有样例和 verifier；命令的存在不表示已执行。每次复现应记录实际 commit、工作树状态、配置、原始报告和正确性对账，公开分享时不得包含 Token。

## 案例入口

| 场景 | 入口与复现说明 | 当前证据边界 |
|---|---|---|
| EF Core、ADO.NET、KV/TTL 与对象组合 | [Ecosystem Sample](../samples/SonnetDB.EcosystemSample/README.md) | 嵌入式与远程使用同一连接字符串合同；真实远程需 Server、建库/SQL/KV/对象权限。 |
| 工业设备写入、SQL 异常诊断 | [Industrial Diagnostics](../samples/SonnetDB.IndustrialDiagnostics/README.md) | HTTP 与鉴权 MQTT 数据链路分别对账；建议/引用是演示素材，provider 未取得真实模型/usage 证据时仍为 `NOT_READY`。 |
| 图片对象、持久摄取、文本/图像检索 | [Semantic Images](../samples/SonnetDB.SemanticImages/README.md) | 需配置真实 SigLIP2 profile 和现场图片；默认生成的确定性 PNG 不证明专业视觉质量。 |
| CDC 快照、增量物化与恢复 | [CDC 合同](cdc-contract.md)、[本地多分区拓扑](m43-local-cdc-topology.md)、[组合恢复样例](../samples/SonnetDB.CdcStreamingJourney/README.md) | 专用路由独立恢复、真实文档源捕获及本地 COUNT 对账；远程拓扑、持续事务化 CDC 到流桥接及十四能力总验收仍未完成。 |
| 持久订阅、watermark、背压、ACK 与重投 | [Streaming 合同](streaming-subscription-contract.md)、[持久窗口 COUNT](m43-persistent-windows.md)、[数值窗口](m43-numeric-windows.md)、[订阅运维](m43-subscription-operations.md) | 单个本地执行者、至少一次；COUNT/SUM/MIN/MAX/AVG、暂停/恢复与条件重试已有本地切片，业务副作用不与 ACK 形成事务；分组/滑动窗口、DLQ 和远程运维仍待补。 |

已有组合样例的嵌入式入口：

```powershell
dotnet run --project samples/SonnetDB.EcosystemSample -c Release
```

CDC、Streaming 和 SQL 资源合同的定向复现：

```powershell
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release `
  --filter 'FullyQualifiedName~Cdc|FullyQualifiedName~Streaming|FullyQualifiedName~SqlMaterializationBudgetTests'
```

真实进程终止后的本地恢复对账由 `tests/SonnetDB.CrashTests` 执行。它与远程故障、真机掉电和 168 小时结果独立，不由单元测试的 `PASS` 推断。

## 基准与发布证据

| 范围 | 可复现入口 / 合同 | 需要补充的证据 |
|---|---|---|
| M19 四档容量/恢复 | [EcosystemSoak](../tests/SonnetDB.EcosystemSoak/README.md)、`scripts/invoke-m19-capacity-bundle.ps1` | 受保护固定目标机、四档原始报告、hardware/checkout、artifact attestation。 |
| M20 Parity | [nightly 证据合同](benchmarks/m20-parity-nightly-evidence.md)、`verify-parity-nightly-evidence.ps1` | 连续七个 UTC scheduled 日期、light/full、日志和原始 artifact；手动复验与 fixture 不能替代。 |
| M25 文档容量 | `tests/SonnetDB.DocumentSoak/scripts/verify-document-soak-evidence.ps1` | million/ten-million、恢复/备份/TTL 与外部 attestation；当前合法自声明报告仍为 `DEFERRED`。 |
| M40 Graph Production | [strict evaluator](m40-graph-367-production-gate.md) | 外部数据库对拍、LDBC/Graphalytics、固定硬件/AOT、kill/reopen 与 168 小时样本。 |
| M42 SQL 结果边界 | [执行/预览合同](benchmarks/m42-sql-result-bounds.md) | opt-in 保留预算为保守估算；端到端首行/流式读取、CLR heap、固定双架构与生产尾延迟仍待补。 |
| M43 八组汇总 | [汇总入口与清单](m43-release-evidence.md) | 实际候选 commit/version；所有未执行范围继续公开 `NOT_READY` / `DEFERRED`。 |

## 公开资料

[DBDB.io / DB-Engines 中英文资料包](ecosystem-directory-dossier.md)保留十四能力分类、Graph Beta 和兼容边界。当前外部提交状态为 `NOT_SUBMITTED`。本地索引不表示已经公开发布案例、取得外部背书或形成性能排名；后续应链接可回读的 release、原始样本和外部回执。
