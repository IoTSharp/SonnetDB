# M40 #367 Graph Production 发布门禁

## 状态与边界

#367 strict evaluator 已完成：`m40-graph-production-input-v2` 只接受带 schema 的 dataset、environment、soak、journey 和 check/closed-gap 原始 artifact，独立重算 manifest 摘要，并校验 artifact SHA-256、真实 commit/HEAD、clean worktree 及结构化复现命令退出码。quick 会真实执行 8 个 reader worker、1 个 update worker、checkpoint、正常进程重开、一次独立子进程 kill/reopen、完整 invariant check 和 `BackupService` verify/restore，但它不是 1m vertex/10m edge、每日 kill matrix 或 168 小时运行，因此输出必须保持：

```text
correctness_recovery: NOT_RUN
performance_capacity: NOT_RUN
release_decision: NOT_RUN
```

截至当前提交，evaluator 自身加固和防误报回归已经完成；M40 修复顺序步骤 3~7、LDBC SNB、Graphalytics、Neo4j/PostgreSQL 外部对拍、Couplet C2~C4、Native AOT 发布 artifact 和 7 天固定硬件报告仍未完成或归档。原生属性图已以 Graph Beta 计入九模型产品定位，但 M40 仍为进行中，不能据此宣称 Production 门禁通过。

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

清单中的相对 artifact 路径以清单所在目录为基准，artifact 路径不得越过该目录；复现工作目录相对于 Git 仓库根目录，且不得越过仓库。复现命令由 `command`、`arguments[]`、`working_directory`、`expected_exit_code` 和 `timeout_seconds` 组成，通过 `ProcessStartInfo.ArgumentList` 直接执行，不接受 shell command line；参数必须且只能包含一个独立的 `{artifact}` 占位符，回放时替换为已校验的绝对 artifact 路径，成功退出码固定为 0。v2 allowlist 只接受仓库内的 `dotnet run --project ...` harness，或当前 Benchmarks 程序的 `dotnet exec ... --m40-verify-artifact {artifact}` schema 验证入口；Neo4j/PostgreSQL、LDBC、Graphalytics 与 Couplet 必须经仓库内 .NET evidence harness 归档，manifest 不能直接启动任意程序。判定器要求 artifact 内记录的 commit、命令、参数、工作目录和退出码与 manifest 一致，在回放前后重复校验 SHA-256，并在所有回放完成后再次确认 worktree 干净。

原始 artifact schema 如下：

| Schema | 原始内容 | evaluator 重算结果 |
|---|---|---|
| `m40-graph-dataset-evidence-v1` | generator/seed、输入/输出 digest、vertex/edge 数 | dataset 摘要与固定规模合同 |
| `m40-graph-environment-evidence-v1` | OS/CPU/memory/disk/runtime/GC/power 快照 | 固定目标机合同 |
| `m40-graph-soak-evidence-v1` | 运行区间、checkpoint 时间戳、kill/reopen/invariant、cold-open、working-set/WAL 样本 | 时长、最大 checkpoint 间隔、恢复/冷开分位数和资源峰值 |
| `m40-graph-journey-evidence-v1` | 每轮逐样本 latency/allocation/GC/I/O/访问计数、access path 和 oracle assertion | 最差轮 nearest-rank P50/P95/P99、吞吐、资源汇总和 oracle 状态 |
| `m40-graph-check-evidence-v1` | 具名 expected/actual assertion | correctness/performance check 与 closed gap 状态 |

缺文件、未知/缺失 schema、仅含 `{ "status": "PASS" }`、manifest 摘要不匹配、样本列长度不一致、重复 ID、占位摘要、`NOT_RUN`、无效/非 HEAD commit、脏工作树或任一复现失败都不能形成 Production PASS。

`--quick` 与 `--manifest` 必须显式选择其一。quick 仅在本地 smoke 失败时返回非零退出码；manifest 仅在 `release_decision=PASS` 时返回零，因此 CI/长测调度器不能把 `FAIL` 或 `NOT_RUN` 当作成功发布。

## 严格判定

本节是 evaluator 已实现的判定合同；各项真实 Production artifact 仍为 `NOT_RUN`，不表示实际发布门禁已经通过。

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
- 每查询 allocation P95 不超过对应 journey 的 query-memory 上限；按每 1,000 个正式样本计，Gen0/Gen1/Gen2 分别不超过 100/10/1 次；GC pause P99 不超过 50 ms。

Soak 清单固定为 168 小时、8 reader + 1 update worker、`m40-frozen-update-profile-v1`、最多 30 分钟 checkpoint 间隔、至少 7 个每日 kill/reopen 周期，并保持 `SyncWalOnEveryWrite=true`、`AutoCheckpointEnabled=true`、`MaxWalBytes=256 MiB`、`MaxOverlayEntries=100,000`。缩短时长、减少 worker、关闭 fsync/checkpoint 或放宽资源预算会直接失败。

## Gap 与发布决定

报告必须带 `M40-GAP-001` 到 `M40-GAP-012` 的完整 catalog 快照。阻塞 Production/Couplet C4 的 `open`、`in_progress` 或 `not_planned` 项会使相应 gate 失败；`closed` 项必须附带可校验的关闭 artifact，只有代码提交不能作为关闭证据。

`release_decision=PASS` 只能由 Gate A 和 Gate B 同时 PASS 得出。quick 或尚未提交完整 evidence 时为 `NOT_RUN`；完整 Production 清单中任一缺失、失败、错误 access path 或 blocking gap 均为 `FAIL`，不能通过手工填写 release 字段绕过判定器。

## 当前待执行项

- 固定并归档步骤 6~7 的性能、恢复与 parity 证据；当前实现 quick 已通过，但在这些证据齐全前仍只允许运行缺陷回归、evaluator 自测和用于修复决策的 quick/microbenchmark；
- 在冻结目标机上生成 `preview-small`、`gate` 和 `production-soak` 数据并保留三个原始测量轮次；
- 执行 Neo4j/PostgreSQL、LDBC SNB 与 Graphalytics 对拍；
- 归档 Couplet C4 的代码知识/Agent 组合语料结果；
- 用发布命令生成并验证 win-x64 Native AOT artifact；
- 完成 168 小时 mixed workload、每日真 kill/reopen、invariant、backup/restore 和 cold-open 采样；
- 用完整 manifest 运行判定器；若出现 blocking gap，回收到对应 owner 后保持 M40 未发布。
