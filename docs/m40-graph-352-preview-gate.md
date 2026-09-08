# M40 #352 Native Graph Preview Gate

本文冻结 `--m40-preview-gate` 的本地 smoke 和正式 manifest 判定边界。它描述
Phase 1 的证据管线，不把 quick、开发机结果或部分 artifact 变成发布结论。

## 状态与入口

| 入口 | 用途 | 结论边界 |
|---|---|---|
| `dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --m40-preview-gate --quick --output <dir>` | 小输入验证 runner、schema、取消和报告管线 | 只能输出 `NOT_RUN` 的正式 gate |
| `dotnet run -c Release --project tests/SonnetDB.Benchmarks -- --m40-preview-gate --manifest <path> --output <dir>` | 从结构化原始 artifact 重算 Preview gate | 只有 correctness/performance 两个 gate 都 PASS 才可能给出 `release_decision=PASS` |

`--quick` 与 `--manifest` 必须显式选择其一；冲突选项、缺少 manifest/output 值或不完整参数会被拒绝。两种模式都写出 `m40-graph-preview-gate.json` 和 `m40-graph-preview-gate.md`；quick 另写 `m40-graph-preview-input.template.json`，模板保留未运行字段，不能直接作为 PASS 证据提交。

quick 复用既有有界恢复 harness，日志文件沿用 `m40-graph-production-quick.log`，不会把同一次本地采样伪装成新的生产证据。quick 仅在 `local_smoke=PASS` 时返回 0；manifest 仅在 `release_decision=PASS` 时返回 0，否则返回 1。`Ctrl+C` 传播取消并先回收当前受监督的子进程，随后解除 CLI 取消处理器。

步骤 6/7 的代码实现和本地回归已完成；固定硬件、Neo4j、Couplet C2 和正式容量证据留待后续真机/外部环境验证，当前仍为 `NOT_RUN`，不得把本地结果当作发布 PASS。

## 输入和输出 schema

正式输入使用 `m40-graph-preview-input-v1`，输出使用 `m40-graph-preview-gate-v1`。对应类型为 `GraphPreviewGateInput`、`GraphPreviewGateReport`，JSON 使用 source-generated `GraphPreviewGateJsonContext` 的 snake_case 命名。

| 输入字段 | 内容 |
|---|---|
| `schema`、`preview_run` | schema 标识；只有显式 `preview_run=true` 才提交正式门禁 |
| `commit_sha`、`started_utc`、`finished_utc` | 被测 HEAD 和本次恢复/混合读写时间范围 |
| `dataset`、`preview_small_dataset` | 分别为 gate、preview-small 数据生成证据 |
| `environment` | 固定硬件和 .NET runtime/SDK/GC/电源原始快照 |
| `recovery` | 复用 `m40-graph-soak-evidence-v1` 的耐久、checkpoint、kill/reopen、cold-open 和资源原始证据 |
| `journeys` | 12 个 native journey 的三轮原始样本 |
| `correctness_recovery_checks`、`performance_capacity_checks` | 两组具名检查和原始断言 |
| `gaps`、`limitations` | Preview 阻塞 gap 的关闭证据与本次限制 |

输入必须绑定被测 commit、生成器版本、固定 seed `0x534F4E4E45544442`、数据档位、环境快照和原始 artifact 的 SHA-256。每个 artifact 记录 `path`、`sha256`、`command`、结构化 `arguments`、`working_directory`、`expected_exit_code` 和 `timeout_seconds`；参数必须含唯一 `{artifact}` 占位符，命令不得依赖 shell 拼接。

Preview 数据档位为 `preview-small`（100,000 vertex/1,000,000 edge）和 `gate`
（1,000,000 vertex/10,000,000 edge）。两档 dataset 分别解析并重算摘要。每个 journey 和 recovery 原始 artifact 的 `dataset_output_digest` 必须等于 gate 的 `dataset.output_digest`，不能拿小数据样本充当 gate 数据证据。

Preview 的 required check artifact 同样必须提供 `dataset_output_digest`：`preview_small_capacity` 绑定 small 原始数据摘要，其余 check 绑定 gate 原始数据摘要。`complexity_trend` 还必须提供 `comparison_dataset_output_digest` 并绑定 small 原始摘要。`preview_small_capacity`、`gate_capacity` 的唯一具名断言 `expected`/`actual` 必须等于对应原始数据摘要；`complexity_trend` 的断言值必须精确为 `smallDigest:gateDigest`。gap 关闭 artifact 只要求具名 gap 断言，不要求 dataset 绑定。

每个正式 journey 必须记录三轮完整消费样本；每轮至少 1,000 次 warmup 和 10,000 个样本，并保留逐样本延迟、首行延迟、分配、query-owned peak、working set、读/WAL 字节、候选/检查/返回/扩展计数、frontier peak、GC 和实际 access path。

报告始终独立给出：

```text
correctness_recovery: PASS | FAIL | NOT_RUN
performance_capacity: PASS | FAIL | NOT_RUN
release_decision: PASS | FAIL | NOT_RUN
```

`release_decision=PASS` 只能由两个 gate 同时 PASS 得出。缺少原始样本、摘要漂移、错误 commit、脏工作树、无法重放、超时、取消或未确认的子进程回收均 fail closed。

## Gate A：correctness/recovery

必须覆盖 Phase 1 的 12 个 native journey：`SOC-1..3`、`TOP-1..3`、`EVD-1..3`、`CPL-1..3`。每项都要有独立 oracle 和逐 ID/property/path 摘要，结果不得有 missing、duplicate、unexpected 或 value/path mismatch。每轮 journey 的 `oracle_assertions` 名称必须恰好等于该 journey ID；`native_journey_oracle` 和 `neo4j_comparison` 的原始 `assertions` 必须覆盖全部 12 个 journey ID；其他 required check 和 gap 的断言名必须恰好等于对应 check/gap ID。只提供无关同值断言或自报 `status=PASS` 均不构成通过证据。

输入必须提供以下 correctness check：`native_journey_oracle`、`neo4j_comparison`、`edge_atomicity`、`concurrency_idempotency`、`kill_reopen_matrix`、`backup_restore`、`invariant_corruption_detection`、`format_compatibility` 和 `budget_cancel`。还必须通过空图、单点、自环、平行边、环、零长度路径、方向、NULL、首版属性类型、稳定 tie-break、取消、超时和每种预算超限；验证 edge record、双向 adjacency、label/property index、统计 dirty marker 的原子可见性；覆盖版本冲突、幂等请求、commit-outcome-unknown、WAL/checkpoint/backup/restore/index-repair 以及真进程 kill/reopen；invariant checker 必须发现故意构造的 orphan、单边 adjacency、stale index、projection mismatch 和 high-water 回退；损坏 magic/version/length/CRC 只能迁移或稳定拒绝。

## Gate B：performance/capacity

`preview-small` 只用于复杂度趋势和回归，`gate` 才能进入正式容量结论。必须在冻结硬件和耐久默认值下运行，完整消费结果并按 nearest-rank 计算三轮最差 P95/P99。查询内存、进程 working set、cold-open 和首查必须满足 [#341 冻结 SLO](m40-native-graph-341-contract.md)。

输入必须提供以下 performance check：`fixed_hardware`、`preview_small_capacity`、`gate_capacity`、`couplet_c2`、`cold_open`、`complexity_trend` 和 `access_path`。同时要求 `M40-GAP-001`～`M40-GAP-007` 全部为 `closed`，并为每项提供可重放的关闭 artifact。Preview recovery 至少包含一个 checkpoint、一个真 kill/reopen/invariant 样本、三个 cold-open 样本，以及至少一个 reader 和一个 writer；必须使用冻结更新 profile 与耐久默认值，但不要求 168 小时 soak。

每个样本必须证明 native journey 使用 `native_adjacency`，不经 Table JOIN；point/adjacency 的 examined 和 expanded edges 不得随无关全图线性增长；不得分页回扫、锁内消费、重复遍历、无界物化或用提高预算掩盖 OOM/timeout。报告需保留失败样本、环境、配置、generator digest 和 access path/fallback reason。

本次判定器自动重算延迟、GC、内存和资源样本，并核验两档 dataset 及上述摘要绑定。`complexity_trend` 的趋势数值仍由可复现的具名检查 artifact 提供；当前没有针对 small/gate 两档逐样本复杂度的专用 schema 或内置数值重算。摘要绑定证明检查引用了相应数据，不能单独证明复杂度达标；固定 workload 的趋势报告仍须提供并审查。本次工具实现不等于 #352 正式证据全部完成。

## 与后续门禁的关系

本文件只覆盖 #352。`CPL-4`、PGQ/SQL 映射、LDBC/Graphalytics、7 天 8+1 mixed workload 和 Production gate 继续分别由 #359/#367 负责。Phase 1 代码实现已经完成；#352 真机/外部准入证据完成前，产品不能宣称 Native Graph Preview 发布门禁已通过。

## 2026-09-08 本地实现验证

- Release 构建通过，0 warning / 0 error；Core `GraphPreviewPhase1Tests` 21 项通过。
- Preview 与 Production evaluator 定向回归共 29 项通过；最后补充的 null raw round 与错误阶段 schema 分别单独复验通过。测试中的完整 artifact fixture 只证明判定器行为，不是实际容量或外部服务证据。
- `--m40-preview-gate --quick` 实际执行 8 reader + 1 writer、checkpoint、backup/restore 和主动 kill/reopen；本地结果为 `PASS`，子进程报告 `cleanup_confirmed=True`。输出的 correctness/recovery、performance/capacity、release decision 均为 `NOT_RUN`。
- `--m40-preview-gate --quick --output` 缺值被拒绝并以非零码退出。
- 本机日志与报告保存在忽略目录 `artifacts/m40-preview-gate-20260908/`，不提交构建产物或将本地结果计入正式 #352 门禁。

本次实现的统一构建、判定器/runner 定向回归和 CLI quick 验证由本次交付统一记录；文档与 manifest 模板本身不构成任何测试或正式 gate 的 PASS 证据。
