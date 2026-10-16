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

## 2026-10-16 07:51 批次02线上闭环与日期候选复审

远端代码批次8ead8bf2的CI38823012523七个job全部success，#272/#271/#270已分别回复并关闭completed。评论ID为6204704191、6204705523、6204706988，逐条核回作者maikebing、正文及state/state_reason；[永久回执](issue-supervision-batch02-actions-20261016.json)保存实际结果，没有把草稿当作发送。公开预览满足这三条原Issue公开修复包要求；264/263及后续明确正式发行的要求仍分别保留。

#262冻结13份候选按SHA整合到根及#261隔离树，旧目标均核同基线且未修改。根Release三构建零警告/错误、China Standard Time下Core164/164、Server189/189通过；代理最终Core91/91、Server152/152和Data/Server显式AOT分析通过，重叠结果不相加。真实Chloe原provider每协议5PASS/9FAIL，最小六文件本地候选三协议42项全PASS，原失败保留，外部未发PR/未正式发行。生产/测试已冻结，报告/工具交付整理中，因此本阶段主树候选仍未提交。261已接concat缺口继续验证，260等261共享基线；现有代理/工作树复用，无重复派单。下一步按每Issue最终完整restore/Format、独立提交和批次推送继续。

## 2026-10-16 #261 根集成与下一批衔接

批次03已推送 cfa92314，264/263/262已逐条回复并回读，仍OPEN；实际回执见 issue-supervision-batch03-actions-20261016.json。CI38827086358运行中，Format/Linux x64 AOT已成功，尚不能宣称完整CI通过。前批272/271/270完成关闭状态不变。

261已按冻结27文件集成，根核406证据文件、5二进制、16个物化快照及两份原TRX；两个Release构建0警告/错误，Core44/44、Server114/114。审计JSON转LF的哈希映射保存在issue261/root-review/line-ending-normalization.json；XCode补丁保留原始上下文字节，以局部.gitattributes标识，不降低dotnet门禁。本项完整restore/Format实际通过后才独立提交，门禁收据issue261/final。正式发行与XCode上游交付仍缺，保持OPEN。

260冻结13文件尚待根集成，原生50+318/远程6+8/Chloe6进程证据已初步核回。原代理各接唯一259/258/256，使用D盘树cfa92314基线；261+260共11依赖已按SHA同步259/256，259首轮3/3生产冻结4文件已同步256，后者可继续RSE分页实现。258报告公开包/Chloe矩阵通过仍待冻结；两项源码/公开包/候选/正式交付分别记录。未重复派单，无清理旧树或parity-results，监督继续。

## 2026-10-16 日期CI断言修正

261已完整门禁后独立提交b157dddc，尚未推送。远端cfa92314的CI38827086358三平台AOT与Format成功，Ubuntu Core7194/7195唯一失败为旧DATE_ADD期待DateTimeOffset；Server1347/1347。根原DLL复现baseline0/1并保留；仅改为DateTime类型、值和Kind.UTC三项断言，Release构建零警告/错误，完整Core结果另从issue262-ci-contract/full-core.trx核回，原run不改写为PASS。完整提交门禁在issue262-ci-contract/final。

258冻结14文件已核69证据、28DLL与14执行副本，尚未集成；issue312接续唯一255，b157dddc新D树，核#243既有无主键实现与真实XCode消费。259/256继续当前任务，生产共享冻结交接已完成；255不重写243、不与256抢共享文件。

## 2026-10-16 全Core回归中的超时断言分支

- 补充实际结果：#262日期断言修正已通过完整restore/Format并独立提交0a51102c，定向197/197通过。其后原完整Core最终7198/7199，唯一失败为Streaming死信测试遗漏检查点自身超时消息，日期项通过；原失败TRX保持。根单独给该测试补第三种既有Timeout分支，仍校验InnerException、截止未取消、原spool/意图恢复不变；独立产物构建并回归FileStreamingDeadLetter/CheckpointStore共34/34。此补丁仅测试，完整门禁在streaming-timeout-contract/final，实际通过后独立提交，不把34项提升为完整Core PASS。原cfa的Windows Core也仅同一旧DATE_ADD失败，三平台AOT/Format成功；Windows rest尚运行。
- 259已冻结18文件，根尚未集成；代理Core145/145、远程9/9+36/36、Chloe候选48/48。issue313接续唯一254，在D:/source/SonnetDB-worktrees/issue254-public-aggregate-cast（0a51102c），既有CAST聚合实现不重做，补公开包及真实Chloe消费；共享RSE等256冻结，255继续XCode无主键消费。下一步根260→259→258串行复审/门禁/提交，再批次推送核CI并按真实正式发行条件回复。

## 最新监督检查点（2026-10-16，#260 主树验收）

- 本地已独立提交261=b157dddc、262日期旧断言=0a51102c、Streaming测试既有超时分支=bf59710e，均已实际通过完整restore/原Format门禁。尚未推送，远端仍cfa92314；其CI三平台AOT/Format成功，Ubuntu和Windows Core均只因旧日期断言7194/7195失败，Windows rest仍待结束，不改写旧run。
- #260冻结13文件已按SHA集成，根核182证据文件、16二进制、25源码/依赖、105 Server快照文件、四份TRX和6个Chloe候选进程。根两个Release构建均0警告/错误，Core258/258（含解析、日期旧断言及Streaming修正）、真实Server120/120，零失败/跳过；组合与代理集合不累加。最终完整门禁位于issue260/final，实际通过后才独立提交。正式Core/Data/Server与Chloe交付仍缺，保持OPEN。
- 之前完整Core原始7198/7199保留，唯一Streaming测试遗漏分支已由独立34/34和本次258/258覆盖；未将其提升为完整Core全绿。根后续按正常push触发新CI确认完整集合。
- #259冻结18文件已核163资产/6TRX，#258冻结14文件已核69证据/28DLL/14源码副本；二者尚未根集成。接下来259→258独立门禁提交，再与261/260及两个测试修正批次推送并回读SHA/CI/Issue状态。
- 三代理各一项：issue314→256（D:/source/SonnetDB-worktrees/issue256-int64-pagination，Core SQL3025/3025明确排除旧日期断言、远程6/6，XCode消费继续）；issue312→255（issue255-keyless-xcode，既有243源码与真实XCode验收）；issue313→254（issue254-public-aggregate-cast，既有CAST实现与公开包/Chloe验收）。256独占共享RSE，其他生产缺口须先协调。#226/#243源码恢复补缺已推送不重做，正式消费条件保留；监督ACTIVE，旧树/失败/parity-results全部保留。


## 最新监督检查点（2026-10-16，#259 主树验收）

- 本地已独立提交261=b157dddc、262日期旧断言=0a51102c、Streaming测试超时分支=bf59710e、260=f8bce983，均通过最终完整restore/原Format门禁，尚未推送。远端main仍cfa92314，CI38827086358已completed/failure：Format、三平台AOT和Windows rest成功，Ubuntu及Windows Core均因旧日期断言7194/7195失败；原run不改写为成功。
- #259冻结18文件已按SHA集成，根核163证据资产及六份TRX；两个Release构建零警告/错误，Core83/83、真实Server129/129，零失败/跳过。新增精确Decimal承载的bit_and_uint64/bit_or_uint64，完整UInt64范围不经过浮点或Int64；裸UInt64大于Int64的ADO参数限制单列保留。最终完整门禁在issue259/final，实际通过后才独立提交，正式发行/Chloe正式交付未完成继续OPEN。
- #258冻结14文档/工具已核69证据、28DLL、14执行源码副本，尚未集成。下一步258独立完整门禁提交，与261/260/259和两项测试修正正常批次push，回读远端SHA、CI及Issue实际回复状态。旧完整Core7198/7199仍保留，不能用后续定向PASS替代新的完整CI。
- 三代理各一项：issue314→256（Core SQL3032/3032，明确排除已由根修正的日期旧断言，远程126/126及XCode六进程各301检查，整理冻结中）；issue312→255（真实DbPackage.Sync发现SQLite数值NULL在XCode源读取阶段变0，按每调用内部入口修候选并补多页/异常/双向验证，不改共享Sonnet生产）；issue313→254（公开旧包CONCAT元数据13/14失败保留，源码三协议14/14及Chloe候选48/48，整理冻结中）。256独占共享RSE，生产按冻结基线串行交接。
- #226/#243恢复补缺已提交推送，GitHub两项实际OPEN，原要求的正式发行和消费者证据仍缺；不重复派单。监督继续ACTIVE，所有旧树、失败证据及parity-results保留。


## 最新监督检查点（2026-10-16，#258 集成与本批待推送）

- #259已通过最终完整restore/Format（issue259/final-corrected-docs）独立提交5d7bf064；主树已提交261=b157dddc、260=f8bce983及日期/Streaming两个测试修正。远端仍cfa92314，其CI38827086358已failure，唯一Core旧日期断言已由0a51102c修正；等待本批正常push后新CI，不改写原run。
- #258冻结14文档/工具已按SHA集成，另为原始补丁增加局部gitattributes保留字节。根已核69证据、28DLL、14执行源码副本及PS语法；公开预览原生8正向进程，原Chloe3个guard负向、最小候选6正向进程分别成立。无新生产源码，不重做uuid；完整门禁issue258/final实际通过后独立提交，再与261/260/259及两个测试修正批次推送。上述四Issue正式发行/消费者上游交付未完成，回复进展后保持OPEN。
- #256已冻结30文件（19生产/2测试/9文档工具），根已审读所有生产差异；Core3032/3032（排除旧日期断言）、远程126/126、XCode六进程各301断言。PaginationSpec属性Int32→Int64有ABI变化，保留旧构造器仍需已编译AST消费者重编译，集成时明确记录。#254已冻结文档/候选，源码无重复实现；公开embedded13/14中的CONCAT元数据旧包失败与源码补验14/14单列，Chloe候选48/48。
- 空槽已复用：issue313→#253，D:/source/SonnetDB-worktrees/issue253-keyless-chloe；issue314→#252，issue252-timespan，均f8bce983基线。253核#243既有无主键DML及Chloe真实消费，不重做引擎；252先审计TimeSpan合同/格式与AOT边界，等根按259/256最终基线交接共享文件。issue312继续255，NULL源读取候选matrix08通过，matrix09补多页、逐列NULL和普通SQLite控制。
- 下一步正常push当前批、回读SHA/CI/逐Issue评论，再串行集成256/255/254。三代理上限、独立提交及完整门禁不变；#226/#243已交付恢复补缺不重做，正式发行/消费者仍跟踪。监督ACTIVE，旧树/失败证据/parity-results保留。


## 最新监督检查点（2026-10-16，批次04已推送；#256主树验收）

- 批次04正常push后main已回读76955cf604655c2ec32f4db1cf019441d4e19577，parity-results保持2ab6caa94b9f07d25fdd0760a7aa682f7ca26295。261=b157dddc、260=f8bce983、259=5d7bf064、258=76955cf6及日期/Streaming测试修正均已推送；每次提交前完整restore/Format实际通过。新CI38831797384进行中，旧cfa的38827086358 failure不改写。
- 261/260/259/258已逐项回复并API核回作者、正文和OPEN状态，评论ID6205519305/6205520541/6205521461/6205522429；正式发行及对应ORM上游交付仍缺。永久回执issue-supervision-batch04-actions-20261016.json已生成，本项纳入提交。没有外发ORM补丁或关闭未满足正式验收的Issue。
- #256冻结30文件按SHA集成，根核257证据、17二进制及8份TRX。根复审移除误带的JsonFile SkipWhitespace非分页类型修改，作者已确认，原/新SHA记录issue256/root-review/integration.json；外部patch字节通过局部gitattributes保留。两个Release构建零警告/错误，完整Core SQL3047/3047、真实Server组合135/135，零失败/跳过，包含根日期断言修正和既有259。最终完整门禁在issue256/final，实际通过后独立提交，暂不push取消批次04正在运行的CI。
- #256 PaginationSpec.Offset/Fetch由Int32/int?改为Int64/long?，即使保留旧构造器，属性getter/init ABI已改变，已编译直接AST消费者必须重编译。SQL原Int32输入兼容，不变更存储/协议。正式匹配Core/Data/Server和XCode上游交付仍未完成，保持OPEN。
- #255冻结18文件收到但复审未通过：默认NULL保留路径按抽取器运行时类型判断，遗漏用户callback返回普通PagingExtracter且指定不同DAL的情况；已要求issue312暂缓251、回到255修正并补真实callback不同源控制，保留matrix09和旧冻结证据。#254冻结16文件，根核154证据、12运行DLL及3份TRX，待独立门禁集成；不受255外部补丁返工影响。
- 代理：issue313→253，在issue253-keyless-chloe核既有243源码/真实Chloe无主键消费；issue314→252，在issue252-timespan独占新增DURATION合同，根已按SHA交接259/256共22生产依赖。252先完成格式v14永久屏障/旧端拒绝、Frame/REST v5协商和AOT边界，不能把251一起实现。issue312完成255返修后回到251准备，issue251-datetimeoffset树76955cf6已建；251等252最终共享基线再改核心。
- #226/#243源码恢复补缺已交付推送，不重做；正式发行及消费者验收继续跟踪。监督ACTIVE，三个代理上限不变，旧树/失败/远端parity-results完整保留。


## 最新监督检查点（2026-10-16，独立整合树接续）

- 本监督当前整合树为 D:/source/SonnetDB-worktrees/issue-supervision-batch05，分支codex/issue-supervision-batch05，起点f9ee14b7。主目录D:/source/SonnetDB另有活动聊天“更新两份 LLMs 文档”编辑llms.txt，以及“分析 ELK 能力移植”的未交付docs/design/elk-single-node-capability-analysis-20261016.md；这些属于他人，本监督停止修改主目录，不暂存、不清理、不混入。后续在独立树审查/门禁/提交，等主目录工作收尾后串行整合；如主main前进，须保留其提交并重核差异/最终门禁。
- #256已完整restore/Format后独立提交f9ee14b7aabbe7eb7a3c842319d12bcd402d3175，目前在本地main及本整合树，未推送。根两个Release构建零警告/错误、Core SQL3047/3047、真实Server135/135；公共PaginationSpec属性ABI变更及重编译要求已明确写入CHANGELOG与用户合同。#259/#256共22生产依赖已按SHA交接252，当前无新增生产修改。
- 批次04已推送76955cf604655c2ec32f4db1cf019441d4e19577，实际评论与OPEN回读见issue-supervision-batch04-actions-20261016.json。CI38831797384最新Format成功，三平台AOT及完整测试仍进行中，不再push取消该run；parity-results保持2ab6caa94b9f07d25fdd0760a7aa682f7ca26295。旧cfa的CI failure保留，不用定向PASS改写。
- #254的16份文档/工具已按SHA集成到本独立树，根核154证据、12运行二进制、3份TRX及PowerShell语法，单文件三行Chloe保护删除已审读。没有新Sonnet生产代码：公开embedded13/14唯一失败为#261旧包CONCAT声明，源码补验及远程各14/14，Chloe候选48/48；正式发行与上游交付未满足继续OPEN。本树完整门禁位于artifacts/issue-supervision-20261016/issue254/final，实际通过后独立提交；不为文档重跑已核的消费者矩阵。
- #255根复审退回自定义PagingExtracter数据源覆盖问题，issue312当前只返修255，251准备暂停。旧matrix09真实通过但原多页探针走callback，不能称默认路径已验；新矩阵须实际默认BatchSize=2、双向NULL及callback alternate DAL控制，旧失败/清单保留。交付前不整合255。
- issue313继续253：公开正式/预览原生各3PASS/4拒绝，源码7/7；Chloe原三协议各9PASS/11guard FAIL，候选initial20/20+独立重开5/5，每协议成立，共75/75；Core25/25，远程6项待最终收据/冻结。issue314独占252时长核心实施，DURATION全signed ticks、v14目录屏障/可恢复迁移、物化视图v3及Frame/REST v5要独立验收；251等待252共享最终基线，禁止并行改同一核心。
- 主目录的HANDOFF暂不再修改，最新接续以本独立树的本文件为准，主main整合时保留其他聊天最新记录并合并此检查点。#226/#243已交付恢复补缺不重做，正式发行及消费者继续跟踪；30分钟监督ACTIVE，三代理上限不变，全部旧树/证据/parity-results保留。


## 最新监督检查点（2026-10-16，#255返修验收）

- 当前独立整合树 D:/source/SonnetDB-worktrees/issue-supervision-batch05，分支codex/issue-supervision-batch05；主目录另有“更新两份 LLMs 文档”和ELK分析未交付修改，本监督保持主目录只读，不混入他人文件。#256=f9ee14b7本地main已提交，#254=6749d202已在独立树提交，均未推送；等主目录收尾后保留其提交并串行整合。
- #254首次完整restore因新树未初始化扩展子模块失败（issue254/final，保留原收据）；随后按Git锁定SHA初始化两个子模块，完整restore与规定Format在issue254/final-with-submodules均exit0才独立提交。没有降低门禁或排除solution项目。#254仅16文档/工具，真实证据与正式发行边界见报告。
- #255修订18文件已按delivery-callback-final.json（SHA9F9A941A79BC2215494C3597DB5F21614D17FD8FE593C8FB6AF7F077AE3E3E69）集成；根实际核2582证据文件、全部回执SHA与PS语法，审读三文件XCode候选。matrix10以旧候选真实复现custom-paging-uses-alternate-dal失败；matrix11的14个源码正向进程及原XML基线1通过，覆盖真实默认双向多页typed NULL、null callback fallback、构造内建委托、外部callback alternate DAL/调用一次/Row及TotalCount原行为。matrix09不能证明默认路径，已在报告撤销该结论；旧文件/清单/失败原样保留。
- #255不改Sonnet生产，原#243无主键写入/稳定身份复用；外部候选修NULL源读取和迁移，不新增公开API或会话可变标志。公开4.0.0/.ef266.5拒绝负基线4进程分列。正式发行与上游交付未完成，保持OPEN。本树最终完整门禁issue255/final实际通过后独立提交，不重跑已核独立消费者矩阵。
- #253已冻结16文件，根核167证据、14运行程序集、两TRX及PS语法，Chloe两处共4行guard删除已审读。源码Core25/25、remote6/6，候选三协议主流程60场景+独立重开15场景通过；公开两版本各3PASS/4FAIL保留。下一步本树独立集成/门禁/提交，不重复引擎实现。
- 批次04远端仍76955cf6；CI38831797384最新Ubuntu完整Build/Test、三平台AOT及Format已success，两个Windows测试分片仍in_progress。261/260/259/258评论和OPEN回读永久回执已随本地f9ee14b7保存，未推送；旧失败CI保留，暂不push取消新run。parity-results保持2ab6caa9。
- 当前三个代理各一项：issue314→252（issue252-timespan，独占DURATION核心，已接259/256共22冻结生产依赖）；issue312完成255返修后恢复251审计/探针（issue251-datetimeoffset），等252冻结再动共享类型/协议；issue313→250（issue250-maintenance，f9ee14b7），先约束目录、索引DESC/INCLUDE、自增起点及SqlSugar原包/消费者核对，重叠核心必须串行。#226/#243已交付恢复补缺不重做，正式版本和消费者仍跟踪；监督ACTIVE，三代理上限及所有旧树/证据/parity-results不变。


## 最新监督检查点（2026-10-16，批次05独立整合树）

- 接续目录：D:/source/SonnetDB-worktrees/issue-supervision-batch05，分支codex/issue-supervision-batch05。主目录活动聊天“更新两份 LLMs 文档”正在改llms.txt和llms-full.txt，另有ELK分析未交付文件；本监督保持主目录只读，不暂存/清理/混入。待其收尾，再检查主main新提交及工作树，保留他人交付后串行整合。主目录HANDOFF暂不能覆盖，最新状态见本树。
- 本批独立提交：256=f9ee14b7（本地main及本树）、254=6749d202、255=da1199a2（后两仅本树），均未push。每项实际通过完整restore与原Format；256根Core SQL3047/3047、真实Server135/135、两Release零警告/错误。254首次restore缺扩展失败保留，初始化两个锁定submodule后，最终issue254/final-with-submodules两门禁exit0。255最终issue255/final两门禁exit0；根仅移除Common.ps1末尾空行，SHA映射在issue255/root-review/normalization.json，原交付保留。
- 本变更承载253的16份复核文档/工具：根核167证据、14运行二进制、两TRX、PS语法及四行Chloe guard删除；源码Core25/25、远程6/6，候选三协议60主流程+15重开场景通过。公开4.0.0/.ef266.5各3PASS/4写入拒绝保留，不重复243源码、不把源码Server称正式发行。完整提交门禁在本树artifacts/issue-supervision-20261016/issue253/final，提交以两条实际exit0为前提。
- 255两层保真问题均有真实失败及修订证据：源读取NULL变0和旧候选覆盖callback指定DAL。最终matrix11的14正向进程+原XML基线1通过，根核2582文件。使用每调用入口、默认paging.Dal；外部非空callback保持Fetch、调用一次、Row/TotalCount原行为及实际默认双向多页typed NULL均验证。旧matrix09不作为默认路径证明；无Sonnet生产修改或上游外发。
- 本批256/255/254/253回复草稿在本树artifacts/issue-supervision-20261016/batch05-actions，尚未发送、未关闭；待整合及正常push后核远端SHA/CI，再发送并逐条回读。四项正式Core/ADO/Server或消费者上游交付仍缺，继续OPEN。
- 已推送批次04仍76955cf604655c2ec32f4db1cf019441d4e19577；CI38831797384最新Ubuntu完整测试、三平台AOT及Format success，Windows core/rest仍运行，避免push取消该run。261/260/259/258实际回复与OPEN回读在issue-supervision-batch04-actions-20261016.json（随本地256提交待推送）。旧cfa CI failure保留；parity-results保持2ab6caa94b9f07d25fdd0760a7aa682f7ca26295。
- 三代理各一项：issue314→252，issue252-timespan独占DURATION类型/100ns、v14屏障和可恢复schema迁移、物化视图v3及Frame/REST v5；已接259/256共22生产依赖，负基线已复现，尚未宣称构建/完整验收。issue312→251，issue251-datetimeoffset只审计/公开包和Chloe探针，等252冻结再改共享类型；四进程已复现通用DateTimeOffset转换失败，专用getter控制通过。issue313→250，issue250-maintenance准备三原生维护缺口与SqlSugar负基线/测试。核心移交顺序252→251→250，版本号按前项实交顺延。
- 下一步取得本项最终完整门禁并独立提交，保持本树清洁；继续监督代理与Windows CI。主目录他人工作结束后串行整合本批，核最终树/完整提交门禁，正常push、读SHA、核CI、回复Issue。226/243恢复源码补缺已推送不重做，正式发行及对应消费者继续跟踪。30分钟监督ACTIVE，三代理上限、旧树/失败证据/parity-results完整保留。


## 最新监督检查点（2026-10-16 09:30，CI 回填页断言修正）

- 本监督继续在 D:/source/SonnetDB-worktrees/issue-supervision-batch05（codex/issue-supervision-batch05）工作。批次05四项独立提交已齐：256=f9ee14b7、254=6749d202、255=da1199a2、253=18d9019f，目前尚未推送。各项既有完整门禁实际通过，不重做；四项正式发行/对应ORM上游交付尚缺，推送后回复并保持OPEN。
- 批次04精确SHA76955cf6的CI38831797384已completed/failure：Format、三平台NativeAOT、Ubuntu完整测试和Windows Core六job成功，Windows rest唯一失败为ObjectProcessingSchedulerTests预算恢复例Expected3/Actual2（Server1363/1364，其余rest项目全通过）。原失败日志、10份TRX及SHA保留，永久核查见issue-supervision-ci-38831797384-20261016.json；不得改写为全绿。
- 根确认默认250ms页预算可合法在第3条注入钩子之前结束；生产已持久保存两条前缀及游标。增加第二条提前结束前页的确定性用例，在旧断言下真实复现2PASS/1FAIL；修订为只续读逐页计数/游标已验证的合法前缀，并要求目标取消钩子确实执行。服务重建续读和重复扫描去重仍严格核验，不重试失败断言、不修改生产预算/恢复逻辑。修订Release零警告/错误，ObjectProcessingSchedulerTests完整20/20、零失败/跳过；仅为此测试范围PASS。
- 本项修正单独提交，提交前须在本最终树完成完整restore和原Format，回执artifacts/issue-supervision-20261016/backfill-budget-contract/final-lf。接着普通快进push本独立树HEAD:main、回读main/parity SHA并跟踪新CI；批次05回复草稿已备，发送后逐条回读且保存永久回执。
- 主目录其他文档聊天已结束，但仍有未提交CHANGELOG/HANDOFF/llms.txt/llms-full.txt及ELK分析文件，本监督保持不混入、不丢弃；可从独立树完成快进推送，主目录本地main暂留f9ee14b7并待保留其修改后同步。不要为了主目录同步接管他人未交付提交。
- 三代理复用：issue314唯一252继续DURATION/v14/v5实施；37项定向通过但恢复、真实消费者及完整回归未完成。根复审发现显式DbType.Time被绑定为DURATION导致旧Server兼容风险，代理已调整两入口并补真实旧DLL/Chloe控制，待完整验收。issue312的251准备已完成等待252冻结；issue313的250只做真实负基线、设计和独立测试准备，不改共享生产。共享核心移交仍252→251→250。
- 30分钟监督sonnetdb-issue已更新当前目录、队列及已完成优先任务边界，仍ACTIVE。226=285186b8、243=b6e674c4源码及恢复补缺已推送不重做；正式版本/对应消费者继续跟踪。保留全部旧树/失败证据/parity-results，不强推。


## 最新监督检查点（2026-10-16，批次05已推送与回复）

- 当前监督树仍 D:/source/SonnetDB-worktrees/issue-supervision-batch05，分支codex/issue-supervision-batch05，HEAD与远端main实际回读390af19d2c24ef8e9f0d7ccfa42dbe1f5fd8bb0e。已用普通快进git push origin HEAD:refs/heads/main推送；parity-results保持2ab6caa94b9f07d25fdd0760a7aa682f7ca26295。
- 本批已推送独立提交：256=f9ee14b7、254=6749d202、255=da1199a2、253=18d9019f；另有独立对象回填页预算测试修正390af19d（仅测试，确定性旧断言2PASS/1FAIL，新整组20/20，两次Release零警告/错误）。该修正初轮完整门禁通过后暂存检查发现新审计JSON的CRLF，原文件留证并仅归一LF；最终final-lf完整restore/Format均实际exit0，暂存检查通过才commit。
- 256/255/254/253已逐项回复并回读作者maikebing、完整正文和OPEN状态，评论ID依次6206069579/6206070592/6206071543/6206072464。永久回执docs/audits/issue-supervision-batch05-actions-20261016.json。正式匹配Core/Data/Server与对应ORM上游交付仍缺，四项未关闭；外部候选未外发。
- 新精确SHA CI38836248581已启动，当前in_progress，尚不能宣称全绿；继续回读，避免为记录再push取消它。旧批次04 CI38831797384保持failure，六job成功、Windows rest唯一页预算断言失败；原日志、10TRX、确定性复现及SHA保留在永久审计，不以本地20/20覆盖旧run。
- 主目录D:/source/SonnetDB仍本地main=f9ee14b7、落后origin/main4提交；其它文档会话虽已结束，仍留下CHANGELOG/HANDOFF/llms.txt/llms-full.txt和ELK分析未交付文件，本监督未修改或暂存这些内容。后续同步须保留他人差异及交接，不重置/丢弃/混入，继续在独立树推进。
- 代理issue314唯一252尚未冻结：远程整类126/126、实际迁移强杀7/7及旧13打开/新恢复/再拒14的18步已由代理报告。旧13对pending/recovery文件hash和mtime不变；complete仅重复写同字节日志完成标记导致mtime变，raw/WAL和字节不变，不能称旧进程完全零文件写。新Data显式DbType.Time+一天内TimeSpan在真实旧v4Server两协议写查重启通过，27h显式TIME拒绝；Chloe批量入口候选亦修正，但最终全Core/AOT与冻结清单仍待根复审，不提前算验收完成。
- issue312的251准备完成，等待252最终共享基线；issue313的250只准备约束/方向/INCLUDE/自增及真实SqlSugar证据，不预占版本或改共享生产。依赖交接252→251→250保持。226/243源码及恢复补缺已推送不重做，正式发行/对应消费者继续跟踪。
- 本检查点和批次05回执在push后产生，暂留本监督树待下一Issue独立提交时随交接纳入，不为巡检单独制造提交或push；主目录他人交接不覆盖。30分钟监督ACTIVE，当前队列已更新；全部旧树/失败证据/parity-results保留。

## 最新监督检查点（2026-10-16 10:08，#252 根验收与基线交接）

- 当前监督树仍 D:/source/SonnetDB-worktrees/issue-supervision-batch05，基线 HEAD390af19d；本条随 #252 的独立实现提交，提交前必须在最终树实际通过完整 restore 与原 Format Check（root-gate）。本项尚未推送、未回复252、未关闭；完整正式发行/Chloe上游交付仍缺。
- #252 冻结manifest SHA521BC1F4DEE62C151CCABB2F89D709E4FC482015561FD3124505B5310D297F70，根核70源码、341证据、31运行程序集及4TRX后只集成54文件，16未变依赖不覆盖。清单旧integrationRoot指向主目录f9，但所有前置文件SHA与实际390监督树匹配，实际映射另存。原主目录他人文档及所有旧树/失败证据保留。
- 根最终源码：Core SQL/协议/迁移3139/3139、真实远程整类141/141，两个Release及Data显式AOT/Trim构建均零警告/错误。代理冻结Core3084/3084、定向44/44、远程126/126、强杀7/7；Chloe每协议26+17，旧v4TIME每协议5+2，旧v13屏障6阶段共18次探针。重叠计数不累加；根核原证据未重复消费者/强杀。旧引擎对completed日志重复写同字节完成标记，仅mtime变，不能称零文件写；没有断电或NativeAOT发布证据。
- #252 使用DURATION完整Int64 ticks、100ns，显式DbType.Time保留TIME旧Server兼容；schema v14永久屏障与恢复、含时长snapshot v3、Frame/REST v5。DATETIME列/默认日期参数仍毫秒，不冒称解决236。正式报告issue-252-implementation-20261016.md及完整哈希issue-252-evidence-20261016.json；首轮暂存CRLF仅CHANGELOG新行，原件保存后归一LF，生产未改。
- issue312已在issue251-datetimeoffset保留准备后快进390af并按SHA接54文件，交接baseline-252-sync.json SHA5CB9CCB8D7D0370F45C74C6B9982ED0FF94DF402D2FD2E9DA734070173200866。现在唯一共享生产写入者，实施DTO原生UTC instant，保留DATETIME及显式旧DbType，混合DTO/DateTime/整数比较须在所有快捷路径前一致拒绝；版本从252顺延，Chloe继承252五文件候选。
- issue313唯一250在issue250-maintenance等待251交付；准备checkpoint02 SHA1BEA7DF2693AEE9128BE1294EFE421AA56943D1103E4376D1A56944527A58C68，根核16源码，Core1PASS/27FAIL、三协议6FAIL，5个kill例仅编译未接dispatcher。不得把失败准备提交成完成，三合同不能因227停做，CodeFirst真实阻断保留。
- issue314完成252转唯一249，树D:/source/SonnetDB-worktrees/issue249-dynamic-json（codex/issue249-dynamic-json，390af）。真实公开4.0/预览/旧源码每份4静态控制PASS+11缺口拒绝；原First()排序失败保留。批准新增独占JSON helper/测试/消费者准备，现有FunctionRegistry/TableSqlExecutor/RSE及文档执行器接线等251→250冻结；decimal对象构造/谓词须保真，动态JSON_VALUE沿用静态Int64优先否则Double合同。
- 批次05远端main仍390af19d，parity-results保持2ab6caa9。CI38836248581最新六job已success（含先前失败的Windows rest），仅Windows Core运行；不为本记录push取消CI。256/255/254/253既有评论及OPEN回读不重复；批次05永久回执随本项纳入提交。旧CI38831797384仍failure，原证据不改写。
- 226=285186b8、243=b6e674c4源码恢复补缺已推送，线上原评论及OPEN已再次核实无新变化，不重做；正式包/对应消费者条件继续跟踪。下一步完成252独立提交、继续监督251，冻结后交250；249独立文件可并行、共享接线串行。批次齐后正常push、回读main/parity及CI、逐项回复再核关闭条件。主目录仍main=f9加他人未提交文档，不混入或清理。

## 最新监督检查点（2026-10-16 10:15，#252已提交、批次05 CI全绿）

- 监督树 HEAD=16b266c2e43d2120dd6538c8ced66f270132958e，#252独立提交完成，尚未推送/回复/关闭。最终树a6f837f88c4efcd5e7f6272ecb694b81c855c209在02:11:14Z开始完整restore、02:11:24Z开始原Format，均实际exit0，Format于02:13:43Z结束；核最终树未变后才commit。较早门禁也通过，但审计EOF随后修正，故完整复跑；原回执保留root-gate-before-audit-eof-fix。root-review/commit.json记录提交及最终门禁，生产54交付文件与冻结manifest一致。
- 根Core3139/3139、真实远程141/141，Core/Server Release及显式Data AOT/Trim零警告/错误；代理强杀7/7、Chloe/旧端证据已核。#252正式包及Chloe上游交付仍缺，不能关闭。批次05的256/255/254/253实际评论回执已随16b266c2提交，不重复发送。
- 精确远端main390af19d的CI38836248581现已completed/success，全部7个作业success，包括两Windows分片、Ubuntu完整测试、三平台AOT与Format。永久回读docs/audits/issue-supervision-ci-38836248581-20261016.json待下一Issue提交。它不覆盖尚未推送252；旧38831797384仍failure，原失败不改写。parity-results仍2ab6caa94b9f07d25fdd0760a7aa682f7ca26295。
- issue312唯一251已收252最终SHA且独占共享生产，完成第一轮DTO接线，正首次Core/Data构建纠错与补验；源码尚未验收。issue313唯一250准备完毕等待251核心冻结，不重复派单。issue314唯一249在新树并行独占helper/测试/消费者准备，24/24 helper通过；旧Core SQL集成1PASS/10FAIL真实保留，尚未接共享执行器，不能称249完成。共享修改仍251→250→249接线。
- 主目录仍main=f9ee14b7，其他会话CHANGELOG/HANDOFF/llms两文件和ELK分析未交付差异原样保留。226/243源码补缺已推送，不重做；正式发行和消费者继续跟踪。当前批次待251/250交付后正常推送、回读SHA/CI、回复并核关闭条件。
- 此提交后检查点、全绿CI回读暂留监督树，随下一Issue独立提交纳入；不为状态记录另造提交。30分钟监督继续ACTIVE，保留所有旧树/失败证据/parity-results，不强推、不外发ORM候选。

## 最新监督检查点（2026-10-16 10:30，#251复审与#249准备冻结）

- 线上再次回读main仍390af19d2c24ef8e9f0d7ccfa42dbe1f5fd8bb0e，parity-results仍2ab6caa94b9f07d25fdd0760a7aa682f7ca26295；精确CI38836248581保持7作业success。本轮没有push/comment/close。监督HEAD仍16b266c2，#252已独立提交但未推送；回复草稿已保存artifacts/issue-supervision-20261016/batch06-actions/issue252-comment-draft.md，发送前必须补本批实际push、远端SHA及对应CI回执。
- issue312唯一#251仍独占共享核心并运行。Core首轮37PASS/1FAIL（快照声明编号）已修源码；第二轮DTO/Duration组合109PASS/1FAIL（递归CTE值正确但ColumnInfo丢失）。代理已修递归声明传播并补入口检查，正第4次Core构建，尚无最终验收。原失败回执保留issue251-work/logs/core-native01/02及对应TRX。
- 根用02:23的Core DLL独立复制后运行17条SQL×空表/有行两阶段探针。相关子查询、CTE/派生表、CASE/COALESCE、BETWEEN和显式旧CAST的类型拒绝一致；复合别名ORDER/HAVING原已被名称绑定拒绝，未冒称新缺陷。发现MAX(Stamp)标量子查询有行时报数值参数错误而声明推断DTO；已协调代理增加按UtcTicks的MIN/MAX实际聚合分支及回归。MV类型检查曾加载全代际，代理已改仅读持久声明且每层遍历检查取消，完整读取仍校验CRC；这些新修改待实际重建验证。
- 根探针和SHA回执在监督树artifacts/issue-supervision-20261016/issue251/root-review-live01；它只是活动构建快照的复审，不能作为最终冻结验收。此前根关于ExecuteStatement验证位于queryResources前的定位不准确，重读确认其实际已在资源作用域内；反馈已纠正。
- issue314唯一#249准备现已冻结并等待：preparation-checkpoint01.json SHA ADD52AD46E455D74FE2F7ED76D4B64854B5F5747388BC835627667E53D8A7856，根逐份核7源码/39证据均一致，既有tracked生产修改=0；helper24/24，旧SQL1PASS/10FAIL、三消费者各4PASS/11准确拒绝仍是准备证据，未接共享执行器、未提交。issue313唯一#250继续保留已核checkpoint02等待#251；不重复派单。共享写入次序仍251→250→249。
- #226/#243原文及评论再次实际回读OPEN，无新验收交付；GitHub发行列表正式仍v4.0.0（2026-09-26），最新workbench-preview-1.12为预发行，不代表匹配Core/Data/Server正式包。226/243源码补缺已推送不重做，正式发行/对应消费者条件仍需跟踪；252当前线上尚无评论，不提前发送草稿。
- 主目录main仍f9ee14b7，其他会话CHANGELOG/HANDOFF/llms两文件及ELK分析保留；本轮只改监督树交接/审计及ignored复审产物。当前两份交接改动与全绿CI回读随下一Issue提交，不为巡检另造提交；下一步继续251真实远程/恢复/Chloe/AOT及冻结，根验证提交后交250，再按批次推送验收。

## 最新监督检查点（2026-10-16 10:54，已交付文档整合与#251回归）

- 主目录他人文档已分别提交16ff58f8（LLM指南）和ffdb734c（M48规划），实际远端main=ffdb734c7fb9769913088c7bdee2415d73050f1b，主目录clean，两对应聊天idle。监督树以保留原#252提交16b266c2的普通merge整合已发布文档，不重写或重复提交252、不接管其他工作；原两份文档提交完整保留。parity-results仍2ab6caa94b9f07d25fdd0760a7aa682f7ca26295。
- 此整合仅新增远端文档与监督记录，src/tests/scripts与252最终提交完全相同；ROADMAP、LLM两文件和两份设计与ffdb734c字节对应版本一致。原未提交交接/CI回读先保存副本及SHA并定向stash，合并后成功apply，stash保留不删；根目录分支/路径已校正到本监督树。保留252实现记录和两个外部交接段，不覆盖任一交付。
- 这是真实分支整合，提交前必须在本最终树实际通过完整restore与规定Format，原始日志/退出码/最终tree/commit保存在artifacts/issue-supervision-20261016/main-docs-integration-1051/。本轮不push，避免取消ffdb734c的新CI38842022794（02:45Z启动，核查时in_progress）；此前390af19d的38836248581仍是其自身SHA全绿，不扩称覆盖252或新main。
- issue312唯一251继续实施：前一候选真实RemoteAdoHttp2TransportTests147/147零跳过，Server构建零警告/错误；较大Core SQL/Tables/Protocol/ADO为3760PASS/1FAIL，唯一旧默认DTO参数直接比较DATETIME断言已按新明确合同改为拒绝+显式CAST正向，尚待最终复跑。此前core-native03的152PASS/3FAIL原证据仍保留，不能跳过失败记录。
- 251已补DTO MIN/MAX实际UtcTicks聚合、递归声明传播、MV仅读声明和取消检查，以及既有*_datetime_offset函数原生声明/UTC归一；新触发器scope和forced-spill断言正待最终Core重建。当前开始真实终止恢复测试，Chloe候选已继承252五文件补丁，251增量仅3文件(+3/-2)，仍待三协议实际消费、最终远程重验及AOT/冻结。早期147/147不覆盖后续语义改动。
- 根本轮只读审查251并整合已交付文档；代理活动树无需pull/reset，生产基线仍252冻结54文件。250/249各自准备冻结继续保留，共享写入次序251→250→249。252回复草稿未发送，252/251/250批次完成后才普通push、核main/parity及精确CI、逐条回复并按正式发行/消费者条件验收关闭。
- 226/243源码及恢复补缺已推送，不重做；正式发布及对应ORM条件仍追踪。30分钟监督保持ACTIVE，全部旧树/失败证据及parity-results保留；下一步接251最终冻结，根复审独立提交后交250。

## 提交后回读（2026-10-16 10:57）

- 本监督HEAD=a3932c49976bc9398beb5139eb2d0c0cab39482b，实际完成普通文档整合merge；父提交16b266c2和ffdb734c均保留。最终tree=2f5cce7cb0cc70ee4ddaf40cd1c088597b5cf986，完整restore于02:53:00Z至02:53:12Z、规定Format于02:53:12Z至02:54:55Z实际exit0，tree/工作区未变才commit；Format仅工作区加载警告，无格式违规，未压制。实际回执main-docs-integration-1051/commit.json。源代码/测试/脚本与16b266c2一致，不重复功能验收；所有已交付文档及之前监督记录/CI回读已纳入。
- 02:56Z远端main仍ffdb734c、parity仍2ab6caa9；其CI38842022794的三平台AOT和Format已success，Windows两分片及Ubuntu测试仍in_progress。本监督尚未push，252尚未评论/关闭。
- 根已读251的真实强杀组合crash-native01：14/14零失败/跳过，其中DTO7项、既有Duration7项，不称14项新DTO；Chloe候选构建exit0，但上游38条nullable警告，与Sonnet零警告产品构建分开。代理继续真实消费者和最终回归，不修改其活动生产文件；冻结后按252基线交付，根整合到a3932c49。
- 代理随后报告core-native04定向174/174零失败/跳过，含forced sort/distinct/group/hash-join spill、递归来源遮蔽和transition trigger；core-sql02扩大回归串行运行。Chloe embedded初写42检查及独立重开通过，最终冻结版本矩阵仍待；这些进展不提前算最终251验收。
- 此提交后状态随下一Issue提交；不为巡检再造提交。监督树现在仅本条HANDOFF/审计待提交，主目录clean的ffdb734c不修改。自动监督同步新SHA、CI及251/250/249顺序，保持每30分钟ACTIVE。

## 最新监督检查点（2026-10-16 11:26，递归取消断言独立修正）

- 远端main仍ffdb734c，CI38842022794已completed/success、7作业全部成功；它不覆盖未推送的252。parity-results仍2ab6caa9。监督仍在a3932c49上，252原提交16b266c2保留，尚未push/comment/close。
- 251已报告最终远程remote-ado04为147/147，Chloe最终候选包含262→252→251（未替换converter），三协议各exercise42+reopen34全通过；新Data→真实旧v5Server显式DateTime/2初写及重开4进程通过；旧Core拒绝v15且前后目录SHA一致、新Core重开全ticks通过；Data显式AOT/trim零警告错误。根待核冻结清单/DLL SHA，不把代理报告提前提升为根最终验收。
- 251最终core-sql02为3765PASS/1FAIL、core-native06为174PASS/1FAIL，唯一失败均为既有递归10ms取消精确包装断言。根在252冻结DLL确定性复现第1/4候选取消，原断言0PASS/2FAIL；独立修正只改测试及对应说明，不改生产、不掩盖原失败。新测试移除timer，校验直接OCE或Cancelled包装、相同令牌、精确调用数、0内存/ambient释放及后续数据读取；整类25/25通过，实际日志/SHA在recursive-cancellation，永久审计recursive-cancellation-contract-20261016.md。
- 本修正独立提交前完整restore/规定Format必须在最终树实际通过，回执recursive-cancellation/gate及commit.json。当前交接记录随该真实修正纳入，不为状态另造提交。251继续最终冻结，不覆盖SqlRecursiveCteTests根修订；随后根核哈希集成251并执行最终回归。250/249仍各自准备冻结，待251→250→249核心交接，不重复派单。
- 主目录main=ffdb734c且clean；当前仅监督树有授权测试修正/交接。旧树、定向stash、失败证据、parity-results全部保留；正式发行及对应ORM上游仍是关闭条件，226/243源码不重做。

## 最新监督检查点（2026-10-16 11:58，#251根复审通过、最终提交门禁待执行）

- 监督HEAD=ea04b240，独立递归取消测试修正已提交；其最终tree1e6a469a333cd7a9fbc133c021e675a499dbd4cc，restore03:34:05Z→03:34:13Z、原Format03:34:13Z→03:35:35Z均exit0，树未变才commit。原a3932c49和252的16b266c2保留，尚未push/comment/close。
- #251代理已冻结，根核manifest613D27DDA235F29A8DCAEBB30C130B5FCE014C462421F601DD79E20D4A912A13、71交付文件/302证据/23程序集/12TRX及Chloe1210跟踪源码。只复制71文件，未覆盖根取消测试或外部文档。根Core3768/3768、真实远程147/147零失败/跳过，Core/Server最终Release与Data显式AOT/trim零警告/错误。四文件只经格式整理，忽略空白/空行的逐文件比较一致；原冻结SHA、运行DLL及最终源另列，不混称。
- 根首次远程restore因共享NuGet审计缓存锁失败；第二次独立缓存构建带扩展旧assets的4条NU1900但147测试通过；再独立新缓存force-evaluate restore/构建零警告错误。全部失败/警告记录保留，未关闭审计。原代理3765/3766及174/175失败保留，根新结果不改写旧窗。完整报告issue-251-root-review-20261016.md/json。
- DTO保存完整UTC ticks/offset0，旧DATETIME保持，默认DTO原生，显式DateTime/2走旧UTC毫秒；混合比较扫描前拒绝。catalog永久v15、snapshot4、结果v6。代理最终Chloe保留262→252→251，三协议42+34，真实旧Server每协议10+6、旧Core拒15且14持久文件SHA不变、DTO7+Duration7强杀已核。无正式发行/上游交付、NativeAOT发布运行或断电证据，保持OPEN。
- 当前#251交付与根报告/CHANGELOG/交接待独立提交；必须最终全solution restore与原Format通过且树不变才commit，随后把实际SHA交issue313实施唯一#250。issue314的#249继续保留准备，核心顺序251→250→249。空槽下一候选#248已读原文，依赖真实233行锁生命周期；只能先隔离准备，不可将unsupported当完成。
- 远端main仍ffdb734c，CI38842022794已7作业success，仅覆盖该SHA；parity2ab6caa9保持。主目录main clean。226/243线上原评论及OPEN已回读，无新正式交付，不重复派单或评论。批次252/251/250齐后正常push、回读远端/CI再回复；所有旧树/stash/失败证据保留。

## 提交后监督检查点（2026-10-16 12:49，#251已提交、#250实施与#248准备）

- #251实际独立提交aa4fbffba0caf7d400cb10e7ebb1d45cc99ff5c5，最终tree9f53eaea36f952086198894709ed6ff4e6fdb059；完整restore04:09:26Z→04:09:34Z、原Format04:09:34Z→04:11:08Z均exit0，树/工作区未变才提交，回执artifacts/issue-supervision-20261016/issue251/commit.json。首门禁Format仅新测试using排序失败，修正后完整复跑；原root-gate失败保留，最终root-gate02通过。Format workspace-load警告保留，无格式违规。
- 根Core3768/3768、真实远程147/147，Core/Server最终Release和Data显式AOT/trim零警告错误；71冻结文件和302证据已核。最终差异仅四源格式、两审计/脚本行尾和一个测试两个既有using排序，原源/失败/运行DLL与新SHA分开记录。未改变生产语义；原代理3765/3766及174/175失败不改写。251正式包/Chloe交付未齐，尚未push/comment/close。
- 250代理issue313已在原issue250-maintenance树正常ff-only aa4fbffb，16准备源与checkpoint02逐项SHA相同；已授权唯一共享核心实施真实约束目录、索引DESC/INCLUDE、identity维护三合同。当前新基线catalog15/snapshot4/result6；保226/243及252/251合同。249代理issue314保留checkpoint01准备，等待250冻结后共享接线。
- 原251代理issue312转唯一248，新隔离树D:/source/SonnetDB-worktrees/issue248-nowait（codex/issue248-nowait，aa4fbffb）。原248/233和SqlSugar PR1457已读；233真实普通行锁尚未实现，248准备必要锁基础与NOWAIT、写者尊重锁/取消超时/释放、真实双连接/SqlSugar负基线。当前仅可写独占helper/测试/消费者/设计，既有共享核心待250→249→248串行交接，不把依赖拒绝或helper通过当SQL能力完成。
- 12:47两个代理分别模型服务504（250）/502（248）中断。根核250只有准备untracked、248 clean及保存的原Issue JSON，12:48已followup恢复同一代理，无代码丢失、不重派。248已确认恢复并继续准备。该故障不计产品测试失败。
- main与远端仍ffdb734c且主目录clean，CI38842022794全部7jobsuccess，永久回读docs/audits/issue-supervision-ci-38842022794-20261016.json待下一Issue提交；它不覆盖未推送252/251。parity2ab6caa9保持。25216b266c2、文档mergea3932c49及递归取消ea04b240保留。批次252/251/250齐后正常push、回读SHA/CI再评论；252和251草稿均未发送。
- 30分钟监督sonnetdb-issue已ACTIVE更新到新提交/代理/失败恢复边界。当前提交后HANDOFF、监督记录及CI回读随下一真实Issue提交，不为状态另造提交。旧树、stash、失败证据和parity全部保留，226/243源码补缺不重做，正式发行与消费者条件继续跟踪。

## 最新监督检查点（2026-10-16，#250复审、#248准备冻结及独立测试修正）

- 监督基线仍为 aa4fbffba0caf7d400cb10e7ebb1d45cc99ff5c5；252/251尚未push/comment/close。主目录main和实际远端均ffdb734c，parity-results仍2ab6caa9，主目录clean；CI38842022794精确ffdb的7作业全绿已再次回读，不扩称覆盖当前本地树。
- issue313唯一250继续独占共享核心。首28合同26/28的INCLUDE迁移载荷和覆盖扫描问题已修，复测28/28，新增载荷测试38/38。regression01运行器300秒终止且未保存stdout/TRX，JSON failure为Process discovery deadline exceeded，不计测试通过。分批storage01=333/337：3旧v5/v6/v7夹具依赖新格式删字节而失效，现改独立冻结旧布局+CRC；1陈旧派生索引损坏键阻止清理，现仅在维护归属识别返回null以允许清理，查询、主行/WAL/目录损坏仍明确抛出。core04覆盖上述修复及v14/v15后231/232，唯一失败为下述旧锁重试测试；仍未冻结最终交付。
- 根独立已构建Core探针20/20：DESC复合条件、跨零/NULL/分页/残余/别名与无索引对照，INCLUDE经增删非索引列、列和表重命名、主键重命名/UPDATE及重开均一致。DLL SHA42CB16C5DD893248854E6ED650540279422A9299700DA69666909ABADF1ACA3A；证据issue250/root-review-live01。首19/20为探针聚合ORDER BY非投影列触发原合同拒绝，修正投影后20/20；原日志保留，未改生产，不代替最终冻结验收。
- 锁重试旧测试在251/250两份DLL默认均1/1，但DOTNET_PROCESSOR_COUNT=2均0/1；根单独将测试同步保存改LongRunning专用线程，保原75ms和生产重试。修正完整类链接251冻结Core，CPU2与CPU1各20/20。审计schema-lock-retry-scheduling-20261016.md；修正待最终全solution restore/原Format，独立提交。已通知250仅应用方法增量供其验证，最终集成剔除重复；不复制全测试文件覆盖250新legacy夹具。
- 250真实remote01六项及compatibility-upgrade01报告到information_schema COUNT(*)才失败；SqlSugar真实维护入口需要该聚合，代理已补原生单COUNT(*)过滤/Int64/分页并加测试，core05待验。真实消费者/重开/旧格式拒绝/强杀/AOT/最终门禁尚未全部完成，不提前验收或推送。
- issue312唯一248准备已冻结，manifest8127BFF635DBF78684D6E70E7AC8643512E9A1F521FECC6724A718A4CF5C69E4。根逐项核10新文件、87证据、238 DLL、16运行DLL记录、3 TRX、603 SqlSugar原源码，零差异；回执issue248/preparation-root-review01.json。helper18/18仅组件；真实ADO正向草稿0/12全部既有解析守卫拒绝。公开/源码embedded和REST/h2c配置各10控制+28拒绝，远程锁语句在客户端先拒，只有控制查询/事务生命周期命中真实服务；不得称Server锁已测。原缺Newtonsoft与CS4007失败保留，tracked生产修改0。
- issue314唯一249仍保持准备checkpoint01等待，issue312的248也等待，共享实施顺序250→249→248。226/243源码补缺和原评论不重做，正式发行/对应消费者关闭条件继续追踪。旧树/stash/失败证据全部保留。本次监督记录与CI回读随真实锁重试测试修正提交，不创建纯巡检提交；批次待250最终提交后正常push、回读SHA/精确CI、回复并核完整关闭条件。

## 提交后检查点（2026-10-16 13:44，文件锁测试修正已提交）

- 实际HEAD=9b3b983304d249fafe485235761c1f8a70671cab，独立test(storage)提交保留aa4fbffb的全部生产代码。最终tree a94d28119cfe4697d3a0a982a752c996063e56dc；全solution restore05:40:42Z→05:40:46Z、规定原Format05:40:46Z→05:41:58Z均exit0且树未变才commit。Format一条workspace-load警告保留，无格式违规。回执artifacts/issue-supervision-20261016/schema-lock-retry/commit.json；ffdb全绿CI永久JSON随该提交纳入。
- 根已实际回读250的core06=238/238、remote02=6/6，零失败/跳过，仍属未冻结候选。代理报告预编译public4消费者替换新DLL10/10、public4 v10目录升级和真实PK碰撞命名/rename/reopen/drop通过；根尚待最终清单核验，不提升为正式包消费。原SqlSugar+新引擎9PASS/3FAIL（两identity适配器未接，一CodeFirst受227）；候选继续。crash01=0/5是child DLL未部署，未启动强杀子进程；代理按同构建SHA部署后重跑中，不能说恢复已验收。
- 250已获最小LongRunning测试方法增量和9b3b实际SHA；不对活动树pull/reset，不覆盖其v5/v6/v7夹具修复。最终按aa4fbffb列交付，根集成到9b3b保独立测试提交。249/248准备冻结等待保持，共享次序250→249→248。
- 13:44再次回读main仍ffdb734c、parity仍2ab6caa9、主目录clean；本批尚未push/comment/close。30分钟自动化sonnetdb-issue已更新并回读ACTIVE，保存9b3b、250真实进度/失败边界及248冻结SHA。当前仅本次提交后HANDOFF/监督状态随下一真实Issue提交，不为巡检另造提交。

## 最新监督检查点（2026-10-16 14:01，#250扩大回归与优先任务交叉核验）

- 监督 HEAD 仍 `9b3b983304d249fafe485235761c1f8a70671cab`；14:00 远端 main=`ffdb734c7fb9769913088c7bdee2415d73050f1b`、parity=`2ab6caa94b9f07d25fdd0760a7aa682f7ca26295`，主目录 clean。ffdb 精确 CI 38842022794 回读 completed/success，不覆盖未推送批次。226/243/250/251/252/249/248 的线上原文和评论已保存 `issue250/root-live-1355`，均 OPEN、没有新正式交付或评论，不重复回复。
- 根回读 #250 的 core06=238/238、remote02=6/6、storage02=337/337；扩大 SQL 第一组1249/1250唯一旧自定义 PK 拒绝断言，代理已按新合同改持久名称/重开/实际 DROP 正向，13:55 Core 重建零警告错误，复跑待。原失败不改写，生产自 impl02 未再变。
- crash02 的 identity 真实终止为5/5；crash03 的31/31已核 TRX 和源码，分为命名 PK 删除携 DESC/INCLUDE 新7项（六阶段及恢复再终止）、旧PK迁移10、Duration7、DTO7，零失败/跳过。不称31个新用例或断电；原未部署child的crash01=0/5保留。
- SqlSugar 候选impl02为11 PASS/1 FAIL，两identity入口通过，唯一标准SugarIndex/CodeFirst因#227事务DDL拒绝，仍不能称完整交付。public4预编译消费者替换Core/Data10/10和真实v10副本升级/碰撞名pk_Legacy_2/rename/reopen/drop通过。旧4.0拒v16只核schema SHA不变，不扩称全目录零写。正式包/上游交付仍缺。
- 根独立交叉探针 `issue250/root-cross-226-243`：9/9，构建零警告错误，Core SHA `482EEA5CA5AC4C491C1FB6647065C3417A6D56D5A8FC2ADBBCFCFA426D2F69E9`。在DESC/INCLUDE下验证主键类型转换与入站FK、跨类型主键替换、载荷事务回滚/提交、受引用PK删除拒绝、移除FK/PK后的重复行和LIMIT1删除、重开及keyless→命名复合PK再次重开，decimal scale保真。原20/20探针另保留，最终冻结再核源/程序集绑定，不当正式包证据。
- issue313继续#250最后验证：SQL余下批次/失败组、147+6真实远程、显式AOT、SqlSugar远程和文档/patch冻结。249和248仍为准备完成，共享核心按250→249→248，复用原代理、不重复派单。根集成到9b3b时保留其独立锁测试修正，最终完整restore/Format后独立提交；252/251/250批次未push/comment/close。
- 自动化sonnetdb-issue已更新且回读ACTIVE，每30分钟不变。交接两文件随下一真实Issue提交，不为本次巡检单独提交；226/243源码不重做，其正式发行与消费者条件继续追踪。

## 追加监督检查点（2026-10-16 14:14，#250最终代理回归通过、冻结待交付）

- 最终SQL三组1250/1250、1003/1003、911/911及remote03整类153/153已根回读，零失败/跳过。原三组1249/1250、1000/1003、905/911分别保留；旧名称/目录版本/回表hook断言已修，两覆盖扫描保完整3候选、0回表及原排序分页，keyless仍先核13再核新PK16；生产未再改变。
- 真实SqlSugar REST/h2c每路11 PASS/1个#227失败、实际宿主强停重开各5/5已读；外部三文件patch在独立git目录实际apply并核候选SHA。固定上游依赖ProjectReference重开探针4项NU1902/NU1903失败保留；直接引用冻结完整DLL用于消费者运行，不声明依赖审计通过，未关审计/降警告/升级固定上游。
- Data显式AOT/Trim零警告错误；Server强制全图AOT有4条NETSDK1210，随后按原项目作用域AOT声明Rebuild实际零警告错误，两运行分开。未做NativeAOT发布运行。
- 代理正在文档/复现说明/最终SHA冻结；根已备Run-RootChecks.ps1和250评论草稿但未接源或外发。监督HEAD9b3b、远端ffdb/parity2ab6不变；250→249→248保持，下一空槽候选247仅只读原文、未派单。自动化同步最新结果，两份交接随下一真实Issue提交。
## 最新监督检查点（2026-10-16 14:33，#250根最终回归通过、提交门禁待执行）

- #250 冻结清单 `58FC5DB1FA39925B28E9AFED736DDFB41530449E58142850184009282E1B0EF5` 已逐项核 66 交付/687 运行时/372 证据/25 TRX/603 上游源/3 补丁；最终 SQL 按 testId 去重为 3164。首次显示名去重错误在复制前停止，失败说明和原脚本保留。66 文件已集成到 9b3b，根保留该提交 LongRunning 方法原排版/注释及代理旧布局 fixture 修复。
- 根最终 Core 扩大回归 3812/3812、真实远程 153/153，零失败/跳过；Core/Server Release 和 Data 显式 AOT/Trim 构建零警告错误。首次 Core/AOT 并行 restore 在共享 packages 缺 NuGet 临时文件，未启动产品测试；原失败保留，串行还原/构建后通过，没有清缓存或关闭审计。根候选 20/20 索引探针与 9/9 的 226/243 交叉探针单列，不冒称最终 DLL 运行。
- 代理真实 identity 终止 5/5，扩展 31/31（新 PK 删除及 DESC/INCLUDE 7、旧 PK 10、Duration 7、DTO 7）；SqlSugar 三协议本地候选各 11/12，唯一 #227 CodeFirst 事务 DDL 失败，REST/h2c 强停重开各 5/5。正式包和 SqlSugar 上游交付仍缺，旧 v4 拒绝仅核 schema SHA，AOT 分析不等于发布运行，250 保持 OPEN。详细根报告 `docs/audits/issue-250-root-review-20261016.md/json`。
- 当前 HEAD 仍 9b3b，本轮 250/CHANGELOG/交接/根报告待独立提交。最终完整 restore/规定 Format 通过且树不变才 commit；随后把实际 SHA 交原 issue314 实施 249，issue312 的 248 继续等待，空闲 issue313 可转 247 隔离准备。共享顺序 250→249→248→247，不重复派单。
- 14:29 核主目录 clean、远端 main=ffdb734c、parity=2ab6caa9。252/251/250 本批尚未 push/comment/close，待 250 提交后正常 push HEAD:main、回读 SHA 和精确 CI，再逐项回复。正式交付不齐不关闭，226/243源码不重做；旧树、stash、失败证据和结果分支全部保留。

## 提交推送后检查点（2026-10-16 14:47，批次06已推送并回复、CI运行中）

- #250 已独立提交 `67a5c8c8d64c36002aee5ebcfcb40634e7d5f482`，父 `9b3b983304d249fafe485235761c1f8a70671cab`，71 路径，最终 tree `6a9e0170efd52d9d0200eef713d8ab9c05dc4694`。最终完整 restore 06:35:39Z→06:35:44Z、原 Format 06:35:44Z→06:36:54Z 均 exit0，树未变才提交；workspace-load 警告保留，无格式违规。首门禁也返回0，但期间文档行尾/EOF修订，因此最终完整重跑 root-gate02。精确回执 `issue250/commit.json`，根报告已随提交。
- 本批 #252=`16b266c2`、#251=`aa4fbffb`、#250=`67a5c8c8` 连同原独立测试修正和文档 merge，已普通快进 push HEAD:main。远端 main 回读 `67a5c8c8d64c36002aee5ebcfcb40634e7d5f482`，parity-results 仍 `2ab6caa94b9f07d25fdd0760a7aa682f7ca26295`。主目录本地 main 仍 ffdb734c 且 clean，尚未同步，不覆盖其它会话。原 push.json 的 PowerShell ErrorRecord 序列化深度警告保留，push.log及独立新回读提供完整正文与SHA证据。
- 精确新 SHA 的 CI **38862841705** 已启动，发帖前回读 in_progress，7作业均在运行，尚无最终结论；旧ffdb/390af的绿灯不扩称覆盖本批。同时触发的 CodeQL/Docs Pages/Docker/Connectors 工作流以各自实际状态为准，不能当正式配套包与ORM验收。先等待本CI再下一批push，避免取消运行中的CI。
- 已逐项发布并回读作者 maikebing、完整正文和 OPEN：252评论 **6209776617**、251评论 **6209777759**、250评论 **6209778676**。评论准确标记新CI未结束、实现及本地/代理测试范围和剩余条件，不重复发帖。三项均未关闭：252/251需正式发行与Chloe交付，250还需SqlSugar正式消费者及#227完整CodeFirst条件。永久批次回执 `docs/audits/issue-supervision-batch06-20261016.json`、初始CI回读 `issue-supervision-ci-38862841705-initial-20261016.json` 待下个真实Issue提交，不为巡检单独提交。
- 原 issue314 已恢复唯一249，原树正常 ff-only 到67a5c8c8，七准备文件前后SHA一致、tracked修改0，收据 issue249-work/resume-checkpoint01.json；现独占共享执行器接线。原 issue312 唯一248继续冻结等待，原 issue313 已完成250并转唯一247隔离准备，新树 `D:/source/SonnetDB-worktrees/issue247-stored-procedure`、分支 codex/issue247-stored-procedure、基线67a5c8c8。247已读原文/评论及229，229现已CLOSED且通用多结果已实现，但过程仍仅lastResult，需接既有结果链；不能重做229或把CALL当StoredProcedure支持。共享次序249→248→247，不冲突准备可并行。
- 代理使用独占D盘产物/cache/短TEMP；除独立HTTP缓存外，各自packages根也须独立，或串行restore，避免本次已记录的并发缓存临时文件冲突。GitHub直连本轮两次网络失败，临时使用127.0.0.1:7890代理后实际读写成功，未更改全局配置或泄露凭据。
- 30分钟监督继续ACTIVE，下一次先核新CI和249进度。226/243源码补缺及原评论不重做，正式发行/对应消费者仍追踪；旧树、stash、失败证据和结果分支保留。本提交后HANDOFF/监督/线上回执暂随下一真实Issue提交，故当前监督树只有这些记录待提交，不另造状态提交。
- 14:50 追加回读：CI 38862841705 的 Format Check 已 completed/success，其余六作业仍 in_progress，整体尚无结论；第二份原始回读为 issue-supervision-ci-38862841705-progress02-20261016.json。249已接线并开始首轮Core验证，根已要求新准入执行器的JSON_OBJECT高精度decimal保真，不能只凭关系路径通过扩称全部入口；247正常准备，248冻结等待。自动化已更新并回读ACTIVE/每30分钟/目标聊天及新SHA、CI。

## 最新监督检查点（2026-10-16，批次06 CI修复与已实现 Issue 关闭）

- 当前监督 HEAD/远端 main 仍67a5c8c8。精确 CI38862841705 最终 failure：Windows rest/Ubuntu 的 EF DTO 映射1项及远程类型目录/旧DTO参数4项失败；Windows Core、三平台AOT、Format success。完整原TRX/日志和本地旧源码0/1、冻结DLL0/4复现已保留。
- 根独立修复 EF 默认原生DTO/显式旧DATETIME查询CAST及DataTypes漏项，不改Core守卫；EF全套两次295/295、真实远程两整类80/80、EF/Server/Data AOT构建0warning/error。报告 ci-38862841705-datetimeoffset-compatibility-20261016.md/json。当前修复待最终完整restore/Format独立提交及正常push；#249尚未集成。
- 用户本轮明确要求“把之前的实现了的，记得要关闭issuse”和“代码也要记得提交和推送”。按源码实现和已验证合同关闭16项：314/313/312/264/263/262/261/260/259/258/256/255/254/253/243/226，全部逐条回读closed/completed及maikebing评论。每项提交均为390af19d祖先，该精确CI38836248581全部7job全绿已新回读；评论明确新67a CI仍失败在修，正式发行和对应上游交付未由关闭推定完成。永久回执 issue-implemented-closures-20261016.json。不得再按旧规则重新开放或重复派单；252/251/250待当前CI修复后核关闭。
- parity-results已由github-actions[bot]正常推进至042748d841c5eca6e47261cf6b63e220280ee102（run38863384603），保留新引用，不回写旧2ab。主目录main仍ffdb734c且clean，不覆盖其它会话。
- issue314唯一249已冻结：manifest91A1125EA2C529AE69CD82E8E3147D50CB57E340936AD3A76D801DC03C2E40CD，25交付/249证据/869运行文件/14TRX，基线67a；代理3682/3682、邻接265/265、末补四项后的动态类24/24、远程159/159、真实SqlSugar六阶段各28/28、AOT/Server零warning/error、最终完整restore/Format通过。根尚未逐项核验/集成，先提交独立CI修复再接25文件。
- issue312唯一248准备保持冻结，manifest8127BFF635DBF78684D6E70E7AC8643512E9A1F521FECC6724A718A4CF5C69E4；249根最终提交后才能修改共享核心。必须实施真实FOR UPDATE基础/NOWAIT，覆盖250新增维护入口，不能仅以233阻断停工。
- issue313唯一247准备已冻结：manifest33AFDE6D08794EE1D4AFB352551FCB0E1A678EA4C63F29C3BD12C4EEAC4FDEB4，10新源/1144运行文件/194证据/603上游源/3TRX，tracked生产修改0。最终合同2PASS22FAIL；public4/preview原生2/15、源码3/15，SqlSugar2/11；REST/h2c原生各3/16、SqlSugar各2/12。h2c只有普通SELECT的2请求走Frame，CALL走HTTP2 REST。远程CREATE PROCEDURE体内参数被旧Binder误认为ADO参数，准备使用公开SQL端点预建仅隔离调用；CIM中断/DDL缺参均保留。此为准备完成而非功能完成，等待249→248共享交接。
- 下一步：CI兼容修复完整门禁、独立commit、普通push/远端和新CI回读；核249冻结/根验收/独立commit并交248，247等待。状态记录随实际修复提交，不造巡检提交，外部ORM候选不外发，旧树/stash/失败证据保留。
