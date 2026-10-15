# GitHub Issue 倒序实施监督

日期：2026-10-16（Asia/Shanghai）。监督聊天：`01a1414d-e07f-7d40-ae68-7a2e4e2f0e7f`。

用户本轮要求按开放 Issue 编号从大到小逐项核对、实现；每个内部子智能体只负责一个 Issue，互不冲突时并行。主智能体负责审查与串行集成，每个 Issue 独立提交，每批推送后对照验收回复并决定是否关闭，再继续下一批。该顺序覆盖历史交接中按最早 Issue 排队、每批十项的安排。

## 调度与归属

- 已创建 ACTIVE heartbeat `sonnetdb-issue`，每 30 分钟在本监督聊天检查；子智能体完成可立即处理。状态不变时保持安静，只报告完成、失败、重要变化或需要用户处理的事项。
- 先读 `AGENTS.md`、`HANDOFF.md` 和本文件，复用活动子智能体和工作树，不重复派单。若代理上下文不可恢复，先审查保留的未提交差异再接续同一 Issue。
- 主目录 `D:\source\SonnetDB` 接收基线为 `b99bf142`，上一会话已完成两批收尾、推送并进入 idle，主目录干净。三个实施工作树基于相同源码基线 `89674db0`；差异仅为上一会话四份交接/审计文档，集成时保留主目录最新版本。
- 子智能体只修改自己的工作树并执行定向验证；由主智能体在最终集成树完成审查、必要组合回归、完整 restore 与规定 Format Check 后逐项 commit。子智能体不提交、推送或操作 Issue。
- `parity-results` 及远端引用保持独立，严禁合并或清理。已有批次工作树和证据保留。

## 倒序批次 01

| 顺序 | Issue | 子智能体 | 隔离工作树 | 当前状态 |
| --- | --- | --- | --- | --- |
| 1 | #314 DECIMAL round | `/root/issue314` | `C:\Users\mysti\.codex\worktrees\issue314-decimal-round\SonnetDB` | 已独立提交 `a5f3708f`；Core 215/215、metadata 52/52、ADO/Server 8/8；尚未推送 |
| 2 | #313 measurement 数值 FIELD 更新 | `/root/issue313` | `C:\Users\mysti\.codex\worktrees\issue313-measurement-update\SonnetDB` | 根已审查集成；Core 117/117、Server 16/16，零失败/跳过，两个Release构建零警告/错误；本次独立提交，尚未推送 |
| 3 | #312 普通主键值更新 | `/root/issue312` | `C:\Users\mysti\.codex\worktrees\issue312-primary-key-update\SonnetDB` | 实施中，尚未集成/提交/推送 |

文件责任：#314 为标量函数及 SELECT 类型推断；#313 为 measurement 更新执行器与持久替换存储；#312 为关系写入、索引与事务变更合并。测试使用独立文件；远程 fixture 的 `partial` 声明与共享文档由根串行处理。

#314 最终主树两个 Release build 均零警告、零错误，三个测试集合均零失败、零跳过；与子树158/6有重叠，不累加。完整提交前 restore/Format Check 收据位于 `artifacts/issue-supervision-20261016/issue314/`，只有两命令 exit 0 后才执行本次独立提交，SHA 以 Git log 为准。本批尚未推送、回复或关闭 Issue。

#313 根组合回归包含 #314 Decimal 舍入、measurement schema、原有 VECTOR 及真实传输回归，117/16 与子树43/9不累加。最终完整 restore/Format Check 收据位于 `artifacts/issue-supervision-20261016/issue313/`，须实际均 exit 0 后独立提交。

验收重点：#314 十进制精度、ToEven、0..28、空结果元数据及真实远程同步/异步；#313 单列有界更新、稀疏/NULL/重复目标、失败原子性、聚合读取与恢复；#312 改键与索引原子提交、碰撞、事务连续改键/旧键复用、回滚和恢复。每项以原 Issue 实际要求为准，不以放行 SQL 或构建成功代替行为验收。

三项原文均包含首个可安装版本和 FreeSql 发布包消费条件。本轮尚未发布包含这些新实现的包，尚未完成对应消费者验收；闭环回复须准确区分源码完成、已推送、CI、发布和消费者结果，不把待验条件写成通过。

## 后续队列与已有结果

用户追加优先要求：补全 **#226** 与 **#243**。当前三项继续，空出实施槽位后优先分别派发这两项，再恢复其余编号倒序。#226 尚缺非空表主键类型和定义迁移以及失败/中断恢复；#243 尚缺无主键表路径，须实现必要的原生无主键写入和稳定内部行身份前置能力，不能把依赖 #253/#255 本身当成交付。这两项与 #312 共用关系存储/事务代码，按已完成基线顺序修改共享生产文件，准备和独立验收可并行。

#314 已交付并冻结原工作树，其代理 `/root/issue314` 现只负责 #226，在 `C:\Users\mysti\.codex\worktrees\issue226-primary-key-migration\SonnetDB` 准备独立迁移 journal/partial/测试。#313 也已冻结，代理 `/root/issue313` 现只负责 #243，在 `C:\Users\mysti\.codex\worktrees\issue243-keyless-delete\SonnetDB` 实施内部行身份和既有 DML 核心。

根已把 #312 四份冻结生产文件按 SHA256 同步到两个优先任务工作树作为候选基线，清单在 `artifacts/issue-supervision-20261016/issue312-dependent-candidate.json`，不等于 #312 已验收/提交。#243 先负责既有共享核心与 v13 目录 floor；#226 仅独立新增迁移类，待 #243 完成后再接线。双方约定统一 v13 延用 v12 payload、持久最低版本不降级，以及18字节 H/v1/GUID128内部行身份。

初次在线快照共 42 条 open，保存在本地忽略目录 `artifacts/issue-supervision-20261016/open-issues-initial.json`。当前批次后依次为 #272、#271、#270，再 #264、#263、#262 等；每批重新读取在线状态，避免与其他会话的关闭结果冲突。

#272/#271/#270 已有源码实现及部分真实执行证据，下一批优先核公开包独立原复现与剩余验收，不重复实现。前两批已由上一会话回复并关闭 #224/#229/#257/#231/#237/#240，#226/#243 保持开放，发布与 ORM 验收另记 #230；证据见 `issue-actions-batches-01-02-20261016.json`。

后续结果在对应 Issue 实施报告及本记录中更新；部分实现不得记为整条 Issue 验收完成。
