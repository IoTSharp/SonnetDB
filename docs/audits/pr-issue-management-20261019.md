# SonnetDB PR / Issue 整合与监督（2026-10-19）

## 当前基线与文件整合

- 用户要求先获取最新代码并合并当前仓库文件，然后由主智能体监督三个子智能体按倒序实施、逐个合并、最后统一推送并核对真实 CI。
- 本次起点为 canonical `D:/source/SonnetDB` 的 `main@dcadffc7137f5653ec46c55a4171cf5f2d83de03`；远端先为 `c194debd`，随后推进至 `0986f92e0d575c329a81b026e9431a16f4b8e2dd`。本地并发出现的 `e9f4f15a` 已另存 `codex/preserve-local-e9f4f15`，其路线图增量也存在于新的远端提交。
- 当前 canonical `main` 已同步至 `0986f92e`。对于旧基线、本地文件、远端文件进行三方合并，保留远端 M48/M49、SQL 交付和本地 M42 热点写、Workbench、发布历史；未执行全量 stash、reset、clean。
- 整合前保护清单实际包含 1,609 个文件，其中 1,473 个未跟踪文件。最终仅替换 `CHANGELOG.md`、`HANDOFF.md`、`ROADMAP.md` 及 M49 确认需求文档四个文件；其它文件逐个 SHA-256 保持。12 份发布 JSON 的远端数据均为本地记录的递归子集，全部远端发布事件也完整存在于本地历史，因此保留本地原字节。
- `src/`、`connectors/`、`tests/` 与远端代码一致；三个既有 VS Code helper 脚本仍是原来的本地增量，未覆盖。`parity-results` 不合并、不删除、不回写。
- 同步中只恢复了本任务造成的旧路径残留及两份索引差异；现有本地增量保持未提交。恢复快照、逐文件哈希和三方解决记录位于忽略目录 `.codex-temp/sync-20261019/`，作为在途工作恢复材料保留，未当作构建垃圾删除。

## 已确认队列

公开 API 完整快照：11 个 open PR、18 个 open Issue。编号倒序为：

| 类型 | 队列 | 当前状态 |
|---|---|---|
| PR | 327、326、325、324、321、320、319、318、317、316、315 | 11 项已逐个本地普通合并；尚未统一推送，远端仍开放 |
| Issue | 247、245、244、242、241、236、235、233、232、230、228、227、225、223、222、221、220、219 | 先核已有实现，不能重复实现或提前关闭 |

## 失败分类与在途工作

| 对象 | 真实证据 | 处理状态 |
|---|---|---|
| PR #327 / #317 | Ubuntu `WarmUpAll_WithConcurrency_OpensDifferentTablesInParallel` 失败；普通 `_tableLockTokens` Dictionary 被并发访问 | 已以共享字典短锁修复并合入；保留 Barrier 并行测试，原定向 92/92；最终 CI 待验 |
| PR #326 | CI 38987711357 的 Windows Core Test 达到 50 分钟上限，未生成该片结果产物 | 诊断中，未判通过 |
| PR #325 | CI 38987682844 的 restore 发生 NU1605：Design 10.0.13 要求 Relational >=10.0.13，直接版本仍为 10.0.12 | 按真实依赖先合 #324、再合 #325；整合的 EF 四包均为 10.0.13，完整 restore/Format 已通过 |
| PR #320 | CI 38987337288 的 CAP 提前 CommitAsync 测试超时 | 原样 CapJourneyTests 本机 10/10；历史超时根因仍 UNKNOWN，未放宽或跳过断言 |
| Issue #247 | Core 参数方向、命名调用及提交前完成校验已实现；ADO/Protocol/Server 在途 | Core 213/213 后提交 5d6c3587；原生 ADO 35/35。首轮远程 21/42，通过不能替代其余真实失败；整项未完成 |
| Issue #245 | 连接会话和 COW 临时工作区已接线，持久 CTAS 暂存/恢复仍实施中 | 基于 5d6c3587，尚未构建或提交；行为验收 NOT_RUN |
| Issue #242 | 全冲突集合 DELETE→INSERT、最终唯一索引和 FK 删除动作已接线 | 基于 5d6c3587；首轮编译缺 using 已保留，增量定向链进行中，未提交 |
| 已有实现 Issue | #244/#241/#235/#228/#225/#223/#222/#221/#220/#219 | 逐项复核源码与正式包/消费者边界中 |

## 执行门禁

1. 每项维护明确修改清单；共享 SQL 核心按依赖串行整合，独立诊断可并行。代理不向远端推送。
2. 每次代码提交前在最终待提交树执行完整 `dotnet restore SonnetDB.slnx` 和 `dotnet format SonnetDB.slnx --verify-no-changes --no-restore --severity warn --exclude extensions/`，不可由旧结果或提交后 CI 替代。
3. 尽量复用一次定向构建的结果；测试强度、失败样本、AOT/版本兼容和真实消费者要求不降低。
4. 本地逐个普通合并，最终候选统一推送；若最终 CI 发现真实缺陷，必须修复并重新验证，不能预先保证只有一次推送即可通过。
5. 只删除本任务创建且已确认不再需要的进程、临时文件或构建输出；保留原有文件、共享缓存和恢复证据。
6. 本文是当前检查点，不是所有任务已完成、CI 已通过、正式发行或生产验收的声明。

## 03:36 接续检查点（2026-10-19，Asia/Shanghai）

- 受控集成树 `codex/pr-issue-integration-20261019@5d6c3587b8ae703f0093092a84dd6f44b47e89d3` 已含 11 个 PR 合并、RAG 取消修复和 #247 Core 切片，共 13 个本任务提交；推送次数为 0。每次提交的最终 restore/规定 Format 均已留回执。
- 远端新回读仍为 11 个开放 PR、18 个开放 Issue，PR 原头未变。#244/#241/#235/#228/#225/#223/#222/#221/#220/#219 已有源码实现，不重做；正式包及真实 ORM 消费者交付仍分别验收。
- main `0986f92e` 的 CI 39147045021：Core 成功、Windows rest 因 StudioManagedServerHost 与 RelationalTableWarmup 两个测试失败。此前关联为 CAP 不准确，已纠正；本地原样重验分别 8/8、5/5，不冒称已确定 Studio 历史根因。
- RAG 取消修复已提交 `af391283`，与新依赖整合后 11/11；PR326 历史 Windows Core 50 分钟超时仍独立 UNKNOWN。
- #247 routine 目录 v5 读取 v1–v4 为 IN，旧引擎拒绝新目录；FileHeader 未改。完整远程、SqlSugar、AOT/正式包未完成。
- 三名活动子智能体分别负责 #247 远程/ADO、#245 TEMP/CTAS、#242 Replace；根按共享文件边界复核后串行集成。专属临时目录 `main-studio-temp-01` 删除被自动命令策略阻止，仍保留；不记清理成功。

## 2026-10-19 本地最终整合（当前仍未推送）

受控分支 `codex/pr-issue-integration-20261019` 当前 HEAD `9faced95d9adddd3e45fe8d009fbefa24a7ef8cc`，已普通合并 11 个依赖 PR、RAG 取消修复、Issue #247、#245、#242，并补齐 CHANGELOG/HANDOFF。远端推送次数为 0；公开 PR 仍开放，Issue 未关闭，`parity-results` 保持不变。

最终合并树完成完整 restore（`dotnet restore SonnetDB.slnx --locked-mode`，exit 0）和规定 Format（exit 0，仅 workspace-load warning）。联合定向验证：#247/#245/#242 相关 Core 130/130、真实远程存储过程/多结果 60/60、CTAS 崩溃恢复 7/7、Replace 134/134，均 0 failure/0 skip。#247 独立 ADO 246/246、Server 60/60；#245 Core 28/28、CTAS amendment 11/11、Crash 7/7；#242 134/134。旧失败收据保留，SqlSugar 原版/候选仅构建通过，Reader 实际消费者仍 NOT_RUN。

完成任务的 247/245/242 父工作树已按哈希/manifest/无独有提交证明移除，证据与 patch 收据保留；仅 #236 Core 工作树仍活动。#236 当前 Core DATETIME2 切片 6/6，既有 Data/ADO 2 项兼容失败明确保留，未修改 Data；继续完成 Core commit 后再协调 Data/Protocol/Server。#233 provider 候选与 #232 schema 只读审计并行，均未声称完成。main-studio-temp-01 删除仍被自动命令策略拦截，保持保留并报告。

本检查点不是最终远端 CI 通过、正式发行或 Issue 关闭声明。下一步按编号继续 #236/#233/#232/#227/#230 等；全部实现冻结后再集中 push，随后只认最终 SHA 的真实 GitHub Actions 结果。