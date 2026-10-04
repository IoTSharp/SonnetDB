# SonnetDB 交接记录

交接日期：2026-10-04（Asia/Shanghai）<br>
当前分支：`main`<br>
远端基线：`origin/main`
项目目录：`D:\source\SonnetDB`

## 当前状态

- 本地 `codex/*` 分支已全部确认可达 `main` 并删除。
- 远端 `origin/codex/*` 已无存活分支；本地缓存引用已 prune。
- `origin/parity-results` 仍保留，未合并、未删除、未改写。它是 parity 结果专用分支，任何后续智能体都不得把它合并到 `main` 或其它开发分支。
- 工作树在本次交接完成后应保持干净；`bin/`、`obj/`、`.nupkg` 等构建产物不纳入提交。

## 会话铁律

- 新会话开始前先阅读本文件，接收上次会话留下的未完成事项、待完善内容、风险边界和注意事项。
- 会话结束前更新本文件，写清已完成、未完成、下一步、验证结果和需要提醒后续会话的内容；计划、局部 PASS 和完整完成必须保持区分。
- 该规则已写入 `AGENTS.md` 的“会话交接”约束；本文件固定放在仓库根目录并随交接提交。

## 已提交变更

- `41ff1afe chore(repo): 固化 parity-results 分支隔离铁律`：把 parity 分支治理规则写入 `AGENTS.md`。
- `d03e36d6 feat(sql): 实现 measurement TAG 与时间组合分组首批合同`：完成 M45-C01 的首批实现、测试、EXPLAIN 合同、验证审计和边界文档。
- `01bbae97 docs(roadmap): 固化 SonnetDB 4.5 推进路线与专题`：提交此前保留的路线、审计和三个设计专题文档。
- 本次交接提交（当前 HEAD，提交说明为 `docs(repo): 建立会话交接记录与铁律`）：提交本文件和会话开始/结束铁律；若本文件再次更新，提交哈希以 `git log --oneline -5` 为准。

## M45-C01 首批实现

主要文件：

- `src/SonnetDB.Core/Sql/Execution/SelectExecutor.cs`
- `src/SonnetDB.Core/Sql/Execution/SqlExplainPlanner.cs`
- `tests/SonnetDB.Core.Tests/Sql/SqlExecutorMeasurementGroupByTests.cs`
- `docs/benchmarks/m45-measurement-tag-grouping.md`
- `docs/audits/m45-measurement-grouping-20261004.md`

当前支持一个或多个 TAG 与可选 `time(duration)` 组合键的裸聚合投影。分组按时间桶和 TAG 原值稳定排序；`COUNT(*)` 使用所有 FIELD 时间戳并集，同一 series、同一时刻只计一行；稀疏 FIELD 的 `COUNT` 为 0，其它空聚合为 `NULL`；TAG 分组的 Int64 `SUM` 使用 checked Int64 累加并在溢出时拒绝。SQL/直接 planner 的名称绑定遵守 GH-Issue #211，EXPLAIN 声明分组阻塞及结果物化边界。

当前明确拒绝：FIELD/未知/重复分组键、未分组投影、复合标量聚合投影、measurement `HAVING`、残差或 Geo WHERE、TAG/别名/多键排序，以及多 series 的 FIRST/LAST。现有仅 `GROUP BY time(...)` 或未分组聚合路径的历史行为没有被这批代码宣称统一。显式 measurement SQL 物化预算仍拒绝聚合；没有新增总 CLR heap 上限、spill 或流式首行承诺。

## 验证证据

- `dotnet build src/SonnetDB.Core/SonnetDB.Core.csproj -c Release --no-restore`：通过，0 warning / 0 error。
- 定向 Release 回归：181/181 通过，覆盖新增 TAG/time 分组、旧 SELECT/聚合/EXPLAIN、扩展聚合、流聚合和 SQL 表达式。
- `dotnet restore SonnetDB.slnx`：通过，项目均为最新。
- `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`：通过。
- `git diff --check`：通过。

完整范围和边界见 [M45-C01 验证记录](docs/audits/m45-measurement-grouping-20261004.md) 与 [分组合同](docs/benchmarks/m45-measurement-tag-grouping.md)。上述结果是本机定向证据，不等同固定硬件、真实 Server/SDK/MCP、AOT 发布、容量、长稳或生产门禁通过。

## 后续顺序

按当前 `ROADMAP.md`：

1. 完成基线与合同冻结中仍未覆盖的 M44-A01/A02/A06、M45-C01/C02/C09、M46-S01/S09 和 V45-X06；不要把本批首实现写成完整 M45-C01 完成。
2. 推进 M45-C02 版本化聚合 state 与 M45-C03/C04/C05/C08 的更新删除、增量物化、event-time 和恢复预算；复用 M43 的位点、任务和投递边界。
3. 在独立证据窗口执行 M20 parity/nightly、M19/M25 固定硬件、M27 真实模型/双网、M29 安装、M35/M36/M40/M41/M42/M43 现场门禁。

恢复工作前先执行：

```powershell
& 'C:\Program Files\PowerShell\7\pwsh.exe' -NoLogo -NoProfile -Command '$PSVersionTable.PSVersion.ToString(); git status --short --branch; git log --oneline --decorate -8; git branch --all --no-color'
```

任何分支整理都必须再次核对并保留 `origin/parity-results`；禁止使用它作为合并来源。
