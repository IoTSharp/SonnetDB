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

## 执行、取消与回收边界

#367 的 quick crash/reopen、manifest artifact 回放以及 Git/.NET 元数据探测共用同一个有界子进程 runner。它只接受 `UseShellExecute=false`、不重定向 stdin、同时重定向 stdout/stderr 且通过 `ArgumentList` 传参的进程。runner 先启动仓库内受信 launcher；launcher 在 stdin 握手前不创建真实目标，只有父进程确认可靠平台 containment 后才放行。launcher 和父 runner 都立即并发排空两路输出，避免目标因 pipe 背压停滞。需要诊断输出的 quick/元数据调用每路只留存最前 64 KiB，超出部分继续排空但不保留，并附加截断标记；artifact 回放不留存正文，但仍排空两路流。目标根进程结束后，launcher 会把 token、目标退出码和内部 drain 结果原子写入任务专属 control 目录，并继续留在 containment 中；父 runner 读取该状态后才统一终止并确认整个 Job/PGID 清空。内部 drain timeout/fault 映射为 launcher failure，不能再借目标退出码伪装成成功。

每个子进程使用相互独立的等待预算：执行期由调用点指定，超时或取消后回收确认轮询最多 10 秒，随后 stdout/stderr drain 最多等待 5 秒；drain 超时后会取消读取、分别关闭两路 pipe，并最多再等待 2 秒确认 drain task 停止。manifest 的 `timeout_seconds` 必须在 1~3,600 秒之间；Git/.NET 元数据探测为 30 秒；quick 的 crash marker 同时受 30 秒墙钟、25 ms 间隔和最多 1,200 次轮询约束。退出等待也同时按墙钟与由 timeout 推导的最大轮询次数收敛，长等待会周期输出进度。通常一次调用的等待上限由“执行预算 + 10 秒 cleanup 确认轮询 + 5 秒 drain + 2 秒 drain task join”组成，但 `Process.Start`、Job/PGID 终止和单根进程 `Kill` 属于同步 OS API，无法由托管 token 强制中断，因此这里不把它们误述为严格的整个方法墙钟上限。runner 还拒绝超过 256 项的参数列表和超过 32 KiB 的单个参数，并在调用前已经取消时完全不启动进程。

发生执行超时、外部取消、收到 launcher completion 状态，或 launcher 意外退出时，runner 请求终止平台隔离容器，并在 10 秒预算内同时确认 launcher 退出和可靠隔离容器为空；可靠 Job/PGID 已负责整树终止，不再调用可能同步遍历任意规模后代的 `.NET` tree kill。root-only fallback 不会收到握手，因此真实命令不会启动；runner 只 best-effort 终止已记录的 launcher 根进程，且结果始终 fail closed。隔离状态无法查询也会触发终止并 fail closed。普通 `Run` 和 marker 检查时已经自然退出的目标必须提供 token-authenticated completion；缺失时追加 runner failure，目标退出码固定为 `-1`，绝不回退为 launcher 退出码。只有 marker 已满足、当时尚未观察到目标 completion，且 runner 随后主动终止 containment 的条件等待允许无 completion 完成。除此之外，只有执行未超时/未取消、可靠隔离存在、cleanup 已确认、launcher 与目标两层输出均已 drain、drain task 已停止且没有其他 runner failure 时，结果才算 `Completed`；回收或 drain 未确认会使 quick/回放失败，quick 不会继续重开数据库或删除相关临时目录。即使 launcher 已由 OS 启动后才发生 containment/output 初始化异常，结果也会保留真实 supervisor PID、启动时间和 cleanup 状态，不会降格为“未启动”。`m40-process-start`/completion 记录 supervisor 身份，launcher 另以 `m40-process-target-start` 记录真实目标 PID；日志还包含父 PID、启动 UTC、工作目录、结构化目标命令、containment kind、tree-tracking 可靠性、timeout/cancel、cleanup、drain、drain task、completion requirement/observation 和退出码。quick 原始日志保留 supervisor PID/启动时间及 cleanup/drain 状态。

CLI 的 `Ctrl+C` 会先设置取消令牌并阻止控制台立即终止当前进程；当前正在等待的 child/replay 先走上述 containment termination、cleanup 和 drain，再传播取消。`RunQuick` 自身还创建 10 分钟 linked 总 deadline，`EvaluateManifest` 与 `GraphProductionGateEvaluator.Evaluate` 各自创建 12 小时 linked 总 deadline；任一总 deadline 或调用方取消先到时，当前子进程先完成有界回收等待，取消随后抛出，且不再启动下一项 replay。`EvaluateManifest` 在打开文件前传播预取消，并在反序列化前拒绝超过 4 MiB 的 manifest；随后使用 source-generated `JsonTypeInfo` 的可取消异步流式反序列化，同时保留同步 API。总 deadline 限制执行工作；其后 cleanup、drain 和 task join 分别拥有 10/5/2 秒等待预算，另须考虑上一段列出的不可中断同步 OS 调用。quick marker API 已收窄为本地文件存在性轮询，不接受可能永久阻塞的任意 callback。通用 launcher 在握手期及其后每 100 ms 核对父 PID + 稳定启动标识：Windows 使用 UTC start ticks，Linux 使用 `/proc/<pid>/stat` 的 starttime，避免跨进程墙钟换算差异和 PID 重用；握手最多等待 10 秒，总 lifetime 为目标执行预算加回收/drain grace。握手后独立 watchdog 覆盖 target start、运行、drain、completion 发布和留守阶段；父丢失或 hard lifetime 到达时，Windows launcher 使用复制到自身的 Job handle 调用 `TerminateJobObject`，Linux launcher 对已确认属于自己的当前进程组发送 `SIGKILL`。quick crash child 使用同一稳定父身份标识，并有 60 秒 hard TTL。

Production manifest 先受 4 MiB 文件大小上限约束，并在任何 Git 探测或 artifact 回放前做集合上限检查：journey 最多 16 项、correctness check 最多 10 项、performance check 最多 10 项、gap 最多 12 项；集合上限同样适用于非 Production 输入，任一超量时双 gate 直接失败且不启动外部进程。通过该检查后，唯一 artifact 回放命令另有 64 项硬上限；每项仍使用自己的 `timeout_seconds` 并串行执行，同时受 `EvaluateManifest` 和 evaluator 的 12 小时 linked 总 deadline 约束。总 deadline 到达后不再启动后续回放；需要更早停止时可使用 `Ctrl+C` 或调用方取消令牌。

每个原始 artifact 另有 16 MiB 文件上限；文件在同一只读 handle 上先用可取消的异步 SHA-256 校验，再通过 source-generated JSON 元数据异步读取。反序列化后、任何排序、分组、聚合或逐项校验前，evaluator 会拒绝超量嵌套集合：复现参数最多 256 项；soak checkpoint/cold-open/resource 样本分别最多 20,000/20,000/25,000 项，kill/reopen 最多 1,024 项；journey 最多 16 轮、每个数值样本列最多 20,000 项、每轮 oracle 最多 64 项；check 或 closed-gap assertion 最多 256 项。预取消会在 Git 探测和 artifact 打开前传播，读取期间取消则由 hash/JSON 异步 API 直接传播。

平台隔离边界必须如实理解：

- Windows 在 `Process.Start` 后创建、配置并 attach 带 `KILL_ON_JOB_CLOSE` 的 Job Object；终止时调用 `TerminateJobObject`，并通过 Job accounting 的 active-process 计数确认清空。Start 到 attach 的短窗口中只有受信 launcher 存活，真实命令仍被 stdin 握手阻塞；确认 Job 后，runner 以 `DuplicateHandle` 把 Job handle 复制进 launcher，再发送握手。父异常退出时 launcher watchdog 会主动终止同一 Job；若 launcher 也异常退出，父与 launcher 持有的 handle 全部关闭后 `KILL_ON_JOB_CLOSE` 仍提供内核兜底。父仍存活但卡死时，hard-lifetime watchdog 同样主动终止 Job。极短目标不会先于 attach 退出。
- Linux 只从固定路径 `/usr/bin/setsid` 或 `/bin/setsid` 以 `setsid -- dotnet <launcher> ...` 启动，不经过 shell；runner 在 500 ms、最多 50 次探测内确认 launcher PGID 等于启动 PID，再发送握手启动目标。launcher 在 target completion 后仍保持为该组 leader，直到父 runner 向该进程组发送 `SIGKILL` 并以 group existence probe 确认清空；因此父进程不会在“launcher 已退出、后代仍存活”的窗口丢失 PGID 所有权。该边界覆盖保持在同一进程组的后代，不防护受信命令主动再次调用 `setsid` 或改组逃逸。
- 其他平台、Linux 缺少固定路径 `setsid`、Windows Job 建立/配置/attach 失败，或隔离查询无法确认时，runner 会记录 `root-only-*`/fallback 状态，不发送 launcher 握手并 fail closed；此时可以确认尚未创建真实目标，只对 launcher 根做有界回收。即使 launcher 根已确认退出，`TreeTrackingReliable=false` 仍使目标执行结果不能成为 `Completed`。

quick child 的父身份 watchdog 为父进程异常退出提供额外兜底。临时 quick 数据目录只允许删除系统临时目录下带本任务前缀的路径，最多重试 3 次且总清理时间不超过 2 秒；launcher control 目录同样必须是系统临时目录的直接子目录和固定 GUID 前缀，最多重试 3 次且总清理时间不超过 1 秒。两者失败都会输出保留的绝对路径，不扩大删除范围；进程树未确认回收时父 runner 不删除仍供 launcher 使用的 control 目录，父丢失/hard lifetime 时 launcher 会尝试清理自己的 control 目录后再终止 containment。

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
