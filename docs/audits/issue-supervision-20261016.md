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

三项已独立提交并正常推送 main 至 `4c116ec2b18e275eb2ca84ad7b05b4c3e58c9b4e`，远端回读确认；`parity-results` 仍 `2ab6caa94b9f07d25fdd0760a7aa682f7ca26295`。三条 Issue 已回复实现/测试/兼容边界并回读正文一致；原要求首次可安装版本和 FreeSql 发布包验收尚未完成，均保持 open，见 [线上回执](issue-supervision-batch01-actions-20261016.json)。以下实施阶段的“尚未推送”历史由本条更新，未宣称远端 CI 通过。

| 顺序 | Issue | 子智能体 | 隔离工作树 | 当前状态 |
| --- | --- | --- | --- | --- |
| 1 | #314 DECIMAL round | `/root/issue314` | `C:\Users\mysti\.codex\worktrees\issue314-decimal-round\SonnetDB` | 已提交/推送 `a5f3708f`；Core 215/215、metadata 52/52、ADO/Server 8/8；已回复，发布验收待完成 |
| 2 | #313 measurement 数值 FIELD 更新 | `/root/issue313` | `C:\Users\mysti\.codex\worktrees\issue313-measurement-update\SonnetDB` | 已提交/推送 `d2153289`；Core 117/117、Server 16/16，零失败/跳过；已回复，发布验收待完成 |
| 3 | #312 普通主键值更新 | `/root/issue312` | `C:\Users\mysti\.codex\worktrees\issue312-primary-key-update\SonnetDB` | 已提交/推送 `4c116ec2`；根 Core 67/67、Server 18/18；已回复，发布验收待完成 |

文件责任：#314 为标量函数及 SELECT 类型推断；#313 为 measurement 更新执行器与持久替换存储；#312 为关系写入、索引与事务变更合并。测试使用独立文件；远程 fixture 的 `partial` 声明与共享文档由根串行处理。

#314 最终主树两个 Release build 均零警告、零错误，三个测试集合均零失败、零跳过；与子树158/6有重叠，不累加。完整提交前 restore/Format Check 收据位于 `artifacts/issue-supervision-20261016/issue314/`，只有两命令 exit 0 后才执行本次独立提交，SHA 以 Git log 为准。本批尚未推送、回复或关闭 Issue。

#313 根组合回归包含 #314 Decimal 舍入、measurement schema、原有 VECTOR 及真实传输回归，117/16 与子树43/9不累加。最终完整 restore/Format Check 收据位于 `artifacts/issue-supervision-20261016/issue313/`，须实际均 exit 0 后独立提交。

#312 根组合覆盖其全部新增用例和 #313/#314 回归，Core 67/67、Server 18/18 均零失败/跳过；两个Release构建零警告/错误。子树扩大集合原始 843 项含一项新测试断言失败，修正后新增 26 项全部通过，817 既有全部通过；不改写为 843/843。根证据和最终完整 restore/Format Check 位于 `artifacts/issue-supervision-20261016/issue312/`。原代理现只读复审优先任务，不改冻结 #312 树。

验收重点：#314 十进制精度、ToEven、0..28、空结果元数据及真实远程同步/异步；#313 单列有界更新、稀疏/NULL/重复目标、失败原子性、聚合读取与恢复；#312 改键与索引原子提交、碰撞、事务连续改键/旧键复用、回滚和恢复。每项以原 Issue 实际要求为准，不以放行 SQL 或构建成功代替行为验收。

三项原文均包含首个可安装版本和 FreeSql 发布包消费条件。本轮尚未发布包含这些新实现的包，尚未完成对应消费者验收；闭环回复须准确区分源码完成、已推送、CI、发布和消费者结果，不把待验条件写成通过。

## 后续队列与已有结果

用户追加优先要求：补全 **#226** 与 **#243**。当前三项继续，空出实施槽位后优先分别派发这两项，再恢复其余编号倒序。#226 尚缺非空表主键类型和定义迁移以及失败/中断恢复；#243 尚缺无主键表路径，须实现必要的原生无主键写入和稳定内部行身份前置能力，不能把依赖 #253/#255 本身当成交付。这两项与 #312 共用关系存储/事务代码，按已完成基线顺序修改共享生产文件，准备和独立验收可并行。

#314 已交付并冻结原工作树，其代理 `/root/issue314` 现只负责 #226，在 `C:\Users\mysti\.codex\worktrees\issue226-primary-key-migration\SonnetDB` 准备独立迁移 journal/partial/测试。#313 也已冻结，代理 `/root/issue313` 现只负责 #243，在 `C:\Users\mysti\.codex\worktrees\issue243-keyless-delete\SonnetDB` 实施内部行身份和既有 DML 核心。

根已把 #312 四份冻结生产文件按 SHA256 同步到两个优先任务工作树作为候选基线，清单在 `artifacts/issue-supervision-20261016/issue312-dependent-candidate.json`，不等于 #312 已验收/提交。#243 先负责既有共享核心与 v13 目录 floor；#226 仅独立新增迁移类，待 #243 完成后再接线。双方约定统一 v13 延用 v12 payload、持久最低版本不降级，以及18字节 H/v1/GUID128内部行身份。

#226 首轮只读复审发现两处旧引擎介入窗口，已获实现者接受修正：完整只读预检 → 旧 schema 语义 v13 floor durable → journal durable → raw 修改 → 新 v13 catalog durable → complete → 内存发布；回滚/恢复也永久保留 v13 floor。最终接线、FK 映射、持久化不确定后的访问阻断及真实子进程强制终止测试仍待完成。#243 首轮重复行、事务和有界删除已通过，正在补 generation fence、目录升级、触发器、远程和独立审查。

### 优先批次当前结果

- #243 `b6e674c431cb8841f33d4a14941537d965184366`、#226 `285186b868a59d4b22e47279ea51deb38a3a1f45` 已正常推送main并通过独立ls-remote回读；parity-results保持原SHA。两条回复正文、作者及open状态均API回读确认，见[回执](issue-supervision-priority-actions-20261016.json)。代码CI `38818030145` 正在执行，尚不宣称完整CI通过；正式发布和ORM验收未完成，因此本批不关闭Issue。本次调度/回执文档按完整门禁本地提交，待代码CI完成或下一批再推送，避免同分支CI并发策略取消正在验证的代码提交。
- #226 最终源在根主树完成扩大验证：三个 Release build 零警告/错误，Core3313/3313、Server104/104、真实kill15/15，零失败/跳过。22份实施增量和1份远程测试按SHA集成，保留先前#312/#313/#314/#243；完整记录见 [实施审计](issue-226-implementation-20261016.md)。原先遗漏的旧事务INSERT错挂外键已在ReadCommitted和Serializable两条真实失败中复现并修复，独立复审通过；新增36项、既有Serializable38项和最终远程18项均包含于根扩大集合，不累加。主树完整restore和原Format Check实际通过后独立提交#226，和#243 `b6e674c4` 待整批推送。首次可安装版本及FreeSql/XCode消费者验收仍待完成，两项不得据此关闭。
- 2026-10-16 06:05 巡检：#243 已完成完整门禁并独立提交 `b6e674c4`，等待#226完成整批推送。#226两个实施子turn曾因上游504超时终止，原树未丢失，已恢复原代理；主实施者负责生产/Core/Crash/报告，原#243代理只新增226远程独立测试文件，不改生产或自行并行构建。前批SHA `4c116ec2` 的 CI `38809102934` 已 completed/success（全部七job），线上三原Issue仍open，发布/消费者条件不冒称满足。
- #243 已完成并冻结十生产/七测试文件，子树最终 Core447/447、真实传输14/14、真实kill3/3；较早415属于复审修补前版本，不绑定最终源。根在 `4c116ec2` 上按SHA整合，三个Release构建零警告/错误、Core404/404、Server15/15及既有有界删除8/8、真实kill5/5；均零失败/跳过，与子树重叠不累加。完整提交门禁见 `artifacts/issue-supervision-20261016/issue243/`，本次独立提交后待本优先批次推送。
- #243 独立复审修复覆盖索引对内部身份的识别、v13目录fsync、Serializable纯插入跨TRUNCATE隔离三项。共享生产源已按SHA同步#226，#226现在可修改既有核心接线；#243实施者原树保持冻结，不再并行改共享核心。
- #226 已接线新恢复流程，继续独立复审与实际crash矩阵；新复审修正大行读取前的预算检查和缓存可见complete标记在重开时再fsync，源码/测试尚未最终冻结。正式包和对应ORM消费者验收仍另计，两条Issue尚未关闭。

初次在线快照共 42 条 open，保存在本地忽略目录 `artifacts/issue-supervision-20261016/open-issues-initial.json`。当前批次后依次为 #272、#271、#270，再 #264、#263、#262 等；每批重新读取在线状态，避免与其他会话的关闭结果冲突。

#272/#271/#270 已有源码实现及部分真实执行证据，下一批优先核公开包独立原复现与剩余验收，不重复实现。前两批已由上一会话回复并关闭 #224/#229/#257/#231/#237/#240，#226/#243 保持开放，发布与 ORM 验收另记 #230；证据见 `issue-actions-batches-01-02-20261016.json`。

## 倒序批次02：公开包原复现验收
当前更新：#272 `d461f8ab`、#271 `13f2063b` 已独立提交，#270十四文件按SHA集成并根复审、待最终门禁/独立提交；详见三份公开包报告。#270独立消费者/SQLite102/102、REST/h2c102/102、官方作者TestServer1/1（六组过滤）通过，原合同满足；公开客户端与源码宿主分别记证。三项批次尚未推送/关闭。旧代码CI38818030145已完成failure（Windows Core、三平台AOT和Format成功；两rest失败已由81095ad0测试合同修正覆盖），待新SHA重验。当前三代理已接续#264/#263/#262，各一项；#262确认日期函数结果传输类型/Kind存在实际待补缺口，准备真实三协议复现，#261下一候选。

#272已通过完整门禁并独立提交 `d461f8ab`。#271十七文件已按SHA集成，根复审并核16源、240证据、12运行二进制及最终两份1/1 ABP TRX；报告[公开包BLOB与Passkey验收](issue-271-public-package-20261016.md)明确公开客户端、源码宿主及TestServer应用各自边界。原Issue合同满足，仍须完整门禁/独立提交/批次推送及GitHub回读。`/root/issue314`接续唯一#263，树`issue263-public-integers`；`/root/issue312`继续#264；`/root/issue313`尚在#270最终冻结，#262树准备但未派发。

2026-10-16 后续：#272 已完成公开包原复现及五阶段迁移（SonnetDB/SQLite共12个独立进程全exit 0），交付9文件冻结，根核清单与真实回执后待串行提交。`/root/issue312` 已接续唯一 #264（工作树 `issue264-public-decimal`，基线285186b8），先验公开包原DECIMAL复现及真实Chloe候选，正式稳定发行条件仍独立保留。#270/#271继续当前应用及真实传输验收。

#272九文件已按SHA集成，根另核最终198份证据文件和18条命令记录；原Issue要求的嵌入式命名FK合同满足，待完整门禁、独立提交及批次推送回读后关闭。完整结果见[公开包报告](issue-272-public-package-20261016.md)。#243测试合同修正已通过完整门禁并独立提交 `81095ad0`；#270/#271最终交付仍待冻结复审。下一#263/#262管理树已准备（`issue263-public-integers` / `issue262-datetime-contract`，285186b8），尚未派发；所有新大缓存/产物在D盘，数据库/TEMP用短D路径。#272最终运行证据也完整迁D并逐文件核SHA，原路径保留junction，记录在`relocated-caches-272/final-relocation.json`，没有丢弃缓存或日志内容。

本批监督同时修正前批CI暴露的#243旧测试合同：`38818030145`的Ubuntu和Windows rest均仅两条无主键INSERT拒绝断言失败，三平台AOT/Format及Ubuntu Core7168通过。根仅改测试，验证空/非空表增加主键、自增高水、ROWVERSION、目录和重连；Release构建零警告/错误，短D临时目录下完整Server1328/1328通过。首轮C盘耗尽78失败、第二轮深D路径56失败均保留，详情见#243报告。C盘新缓存已停止，三代理后续cache/clone在D专属artifacts，数据库与TEMP用短D独占目录；未清旧证据，#272两份失败run包缓存完整搬迁并SHA核对后以junction维持原路径。Windows Core CI继续等待，旧run不标成功。

优先任务源码交付并推送后，已复用三个内部代理继续下一批。三个新隔离树均通过管理工具创建/注册，基于已推送的 `285186b8`；旧实施树及证据保持冻结。已重新读取三Issue原文与评论，已知公开包为 `4.5.0-preview.ef266.5`，每项须独立核源、固定版本及包SHA，不能用本次主树DLL替代公开包。

| Issue | 当前代理 | 工作树名称 | 派发范围 |
|---|---|---|---|
| #272 | `/root/issue312` | `issue272-public-fk` | 命名FK目录、DROP实际效果、替换/升级/回退/重开与SQLite对照 |
| #271 | `/root/issue314` | `issue271-public-blob` | byte[]相等、Any+SequenceEqual、边界/多凭据/回滚/重开及公开包与应用证据 |
| #270 | `/root/issue313` | `issue270-public-contains` | 原五行数据及%/_/反斜杠参数的列表/Count、相关字符串方法和官方作者路径 |

绝对工作树根为 `C:\Users\mysti\.codex\worktrees\<工作树名称>\SonnetDB`。每个代理仅处理自己的Issue及专属probe/审计报告，不修改共享HANDOFF/CHANGELOG、不commit/push/评论/关闭；若发现生产缺口，先向根报告共享边界。新私有packages/http-cache、无fallback或ProjectReference，保留来源、实际退出码和资源归属。公开预览包是否满足发布条件以各原Issue为准；缺实际应用旅程仍准确列出，不据单次SQL成功结项。根随后按Issue逐项审查、集成、完整门禁和独立提交；本段仅记录派发，未把准备写成验收完成。

后续结果在对应 Issue 实施报告及本记录中更新；部分实现不得记为整条 Issue 验收完成。
## 倒序批次03与最新派发

批次02已正常推送main到8ead8bf2，远端SHA及parity-results回读一致；代码CI38823012523仍运行，270/271/272尚未评论/关闭，三份具体回复草稿在本地batch02-actions目录。#264已冻结13份交付并集成，原生公开包7/7、原Chloe保护1进程、仅DDL候选6/6进程均exit0，根复核65+1362证据资产与26条命令；正式稳定发行及Chloe上游交付未满足，保持OPEN。报告[Decimal公开包及消费者候选](issue-264-public-package-20261016.md)。每项仍须最终完整restore/Format Check后独立提交，本批齐备后推送。

当前代理：issue312已接续#261（D:/source/SonnetDB-worktrees/issue261-public-metadata，codex/issue261-public-metadata，285186b8），只验公开包和XCode真实候选，不与#262并行改共享生产；issue314的#263已完成12个最终消费者进程，整理冻结交付；issue313实施#262日期投影原生类型/Kind和协商。#262真实+08基线6FAIL/3PASS、既有日期参数另2PASS，旧结果保留。下一候选#260需先识别与#262共享标量/类型推断边界；旧树、原始失败、缓存与parity-results均保留。
#264已完整门禁后独立提交15d8d180，未推送。#263冻结12文件已集成，根核136证据文件/18二进制与12消费进程exit0；详见[小整数公开包与Chloe验收](issue-263-public-package-20261016.md)。两项正式发行和Chloe正式交付未完成，保持OPEN。issue314现接续#260，D:/source/SonnetDB-worktrees/issue260-uint64-parse，分支codex/issue260-uint64-parse，285186b8；与issue313的#262约定：260可先写独立解析helper/tests/FunctionRegistry，但共享RelationalSelectExecutor.cs声明推断等待262冻结，由根同步基线后再接线。issue312的261只验消费者。前批8ead8bf2的CI38823012523仍运行，Format成功，不预写270～272关闭结果。

## 当前接续检查点

批次02推送/CI/Issue实际状态见[线上回执](issue-supervision-batch02-actions-20261016.json)：远端8ead8bf2确认，parity-results未变化；38823012523的Ubuntu完整构建测试、三平台AOT和Format成功，两个Windows测试分片仍运行。270～272仍OPEN、回复尚未发送，待最终CI核对后回复/关闭并回读。264独立提交15d8d180、263独立提交eb80bd22均已通过最终完整门禁，尚未推送，等262批次与前批CI收尾，避免cancel-in-progress取消已有验收。264/263均继续OPEN，正式发行及对应Chloe上游正式交付未被预览/候选替代。

三个唯一任务现在为262（issue313）、261（issue312）、260（issue314）。261公开包矩阵暴露内置concat可推断却仍Object的真实视图声明缺口，已要求继续修复；不把旧8进程探针的unknown断言提升为完整元数据PASS。262还发现泛型DATE_ADD被C# switch隐式提升DateTimeOffset，以及真实Chloe的TO_DATETIME包装会抹Kind，分别实施原生修复和本地候选验收。260明确parse_uint64有界语法与精确Decimal承载。共享SELECT声明/远程测试按262→261→260串行同步，独立helper/registry/测试与消费者可并行。均不外发消费者补丁，正式发行边界保留；任务未全部验收，半小时监督继续ACTIVE。
