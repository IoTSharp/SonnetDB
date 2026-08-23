# M40 #367 Graph Production 发布门禁

## 状态与边界

#367 已实现 evidence manifest、artifact SHA-256 校验、双 gate 判定、机器可读 JSON/Markdown 报告，以及本地 quick 管线 smoke 的原型。当前 evaluator 尚未按 artifact schema 独立解析并重算原始结果，也未完整验证逐轮样本、真实 clean commit、复现命令退出码及 allocation/GC 阈值，因此不能称为严格发布门禁。quick 会真实执行 8 个 reader worker、1 个 update worker、checkpoint、正常进程重开、完整 invariant check 和 `BackupService` verify/restore，但它不是 1m vertex/10m edge、真子进程 kill 或 168 小时运行，因此输出必须保持：

```text
correctness_recovery: NOT_RUN
performance_capacity: NOT_RUN
release_decision: NOT_RUN
```

截至当前提交，evaluator 加固尚未完成，LDBC SNB、Graphalytics、Neo4j/PostgreSQL 外部对拍、Couplet C2~C4、Native AOT 发布 artifact 和 7 天固定硬件报告也尚未归档。M40 仍为进行中，产品定位继续是“八种数据模型，一套引擎”。

本项只增加 benchmark/evidence 工具和报告合同，不修改 Graph V1 key/record、WAL、checkpoint、backup format、Graph API 或 Server 权限。

## 运行入口

本地管线 smoke：

```powershell
dotnet run --project tests/SonnetDB.Benchmarks/SonnetDB.Benchmarks.csproj -c Release -- --m40-production-gate --quick --output artifacts/m40-graph-production-gate
```

该命令生成：

- `m40-graph-production-quick.log`：本地混合负载、重开和恢复摘要；
- `m40-graph-production-gate.json`：source-generated JSON 报告；
- `m40-graph-production-gate.md`：可审查摘要；
- `m40-graph-production-input.template.json`：完整 Production 清单模板，所有占位项默认不可通过。

候选 evidence 判定入口（只能在 M40 修复顺序步骤 1~7 全部通过后用于正式取证）：

```powershell
dotnet run --project tests/SonnetDB.Benchmarks/SonnetDB.Benchmarks.csproj -c Release -- --m40-production-gate --manifest artifacts/m40-graph-production-input.json --output artifacts/m40-graph-production-gate
```

清单中的相对 artifact 路径以清单所在目录为基准。当前实现会检查 artifact 存在性、SHA-256、manifest 摘要和最小复现命令字段，但尚未按各 artifact schema 独立解析原始测量值；仅含 `{ "status": "PASS" }` 的伪造文件仍可能满足现有测试。加固后的判定器必须要求逐轮原始样本并自行计算摘要，校验 commit 确实存在且工作树干净，回放命令并核对退出码；缺文件、摘要不匹配、重复 ID、占位摘要、`NOT_RUN` 或任一复现失败都不能形成 Production PASS。

`--quick` 与 `--manifest` 必须显式选择其一。quick 仅在本地 smoke 失败时返回非零退出码；manifest 仅在 `release_decision=PASS` 时返回零，因此 CI/长测调度器不能把 `FAIL` 或 `NOT_RUN` 当作成功发布。

## 目标严格判定

本节是 evaluator 加固后的验收合同，不表示当前实现已经满足。

Gate A `correctness_recovery` 要求以下检查全部 PASS：

- 五条 journey 的逐 ID/property/path oracle；
- Neo4j 与 PostgreSQL 对拍；
- edge 原子性、并发/idempotency、预算/取消；
- 真子进程 kill/reopen matrix、backup/restore；
- invariant corruption detection 和格式兼容。

Gate B `performance_capacity` 要求以下检查全部 PASS：

- `preview-small` 与 1m vertex/10m edge `gate/production-soak`；
- LDBC SNB、Graphalytics 和 Couplet C4；
- win-x64 Native AOT 发布 artifact；
- cold open、复杂度趋势和实际 access path；
- #341 固定目标硬件上的容量与 168 小时 mixed workload。

报告逐项要求 `SOC-1~3`、`TOP-1~3`、`EVD-1~3`、`CPL-1~4`、`PGQ-1~3`，并验证：

- 1,000 次 warmup、3 个独立轮次、每轮至少 10,000 个完整消费样本；
- Production P95/P99 为 #341 热查询阈值的 2 倍，query memory 不放宽；
- process working set 不超过 12 GiB，cold open P95/P99 不超过 2,000/5,000 ms；
- native journey 不得出现 relation/Table/full scan，`PGQ-1/2` 必须是 `relation_index_seek`，`PGQ-3` 必须稳定返回有界 `relation_scan_fallback`；
- candidates、examined、returned、expanded edges、frontier、logical/physical reads、allocation、Gen0/Gen1/Gen2、GC pause、WAL 和 cold-first-query 均有原始计数与阈值证据；仅报告非负值不构成资源门禁通过。

Soak 清单固定为 168 小时、8 reader + 1 update worker、`m40-frozen-update-profile-v1`、最多 30 分钟 checkpoint 间隔、至少 7 个每日 kill/reopen 周期，并保持 `SyncWalOnEveryWrite=true`、`AutoCheckpointEnabled=true`、`MaxWalBytes=256 MiB`、`MaxOverlayEntries=100,000`。缩短时长、减少 worker、关闭 fsync/checkpoint 或放宽资源预算会直接失败。

## Gap 与发布决定

报告必须带 `M40-GAP-001` 到 `M40-GAP-012` 的完整 catalog 快照。阻塞 Production/Couplet C4 的 `open`、`in_progress` 或 `not_planned` 项会使相应 gate 失败；`closed` 项必须附带可校验的关闭 artifact，只有代码提交不能作为关闭证据。

`release_decision=PASS` 只能由 Gate A 和 Gate B 同时 PASS 得出。quick 或尚未提交完整 evidence 时为 `NOT_RUN`；完整 Production 清单中任一缺失、失败、错误 access path 或 blocking gap 均为 `FAIL`，不能通过手工填写 release 字段绕过判定器。

## 当前待执行项

- 先让 evaluator 按 schema 读取各类 artifact、从逐轮原始样本重算摘要，并加入伪造 `{ "status": "PASS" }` 必须失败的回归；
- 验证 commit 对象真实存在且工作树干净，记录并回放复现命令及退出码，加入 allocation、Gen2 和 GC pause 阈值；
- 完成 M40 主路线图步骤 1~7；在此之前只允许运行 evaluator 自测和用于修复决策的 quick/microbenchmark；
- 在冻结目标机上生成 `preview-small`、`gate` 和 `production-soak` 数据并保留三个原始测量轮次；
- 执行 Neo4j/PostgreSQL、LDBC SNB 与 Graphalytics 对拍；
- 归档 Couplet C4 的代码知识/Agent 组合语料结果；
- 用发布命令生成并验证 win-x64 Native AOT artifact；
- 完成 168 小时 mixed workload、每日真 kill/reopen、invariant、backup/restore 和 cold-open 采样；
- 用完整 manifest 运行判定器；若出现 blocking gap，回收到对应 owner 后保持 M40 未发布。
