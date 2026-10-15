# GH-Issue #272：公开包命名外键迁移验收

2026-10-16（Asia/Shanghai）。公开 NuGet `4.5.0-preview.ef266.5` 已通过原始独立复现，以及建表、移除、替换、回退和跨进程重开验收。原 Issue 要求的嵌入式命名外键合同已满足，可由主监督者在审查、提交并推送本报告后回复并关闭 #272。本报告生成时 Issue 为 OPEN；没有提前执行关闭操作。

## 范围与证据来源

- 对照 GitHub #272 原文和全部评论；最终快照为 `2026-10-11T02:12:31Z` 更新、0 条评论。原复现使用 Guid 主键、`HasConstraintName("FK_A06_Book_Author")`，来源和目标使用不同 DbContext 类型。
- 保留原 `Program.cs` 内容，仅增加末尾换行；消费者项目把 SonnetDB 依赖固定到公开修复包，并保留 EF Core `10.0.12`。原 `4.0.0` 的失败是 Issue 已有记录，本轮没有再次运行旧版本。
- 当前生产源码已有实现，本轮没有修改生产代码，也没有用源码构建产物替换公开包。此前源代码三通道测试和 [#273 公开包报告](issue-273-public-package-20261015.md) 作为背景，不累计到本次运行结果。
- 本次 SDK 实测为 **10.0.400**，Windows `10.0.26300` / `win-x64`，EF Core `10.0.12`；与原 Issue 记录的 SDK `10.0.401` 明确区分。

## 包来源与隔离

原复现和增强生命周期各有一个独立消费者项目。两项目仅使用 NuGet.org，每轮创建全新的私有 packages、HTTP cache、plugins cache 和 CLI home；清空 fallback 配置并禁用 SDK 隐式 library-packs 源。实际 `project.assets.json` 均只有私有 package folder、NuGet.org 一个源及 0 个 project 类型依赖。

项目的 `Directory.Build.props`、`Directory.Build.targets` 和 `Directory.Packages.props` 在消费者边界阻断仓库继承；没有 ProjectReference、跨仓库源码引用、DLL 替换或全局包缓存修改。隔离 restore 关闭 NuGetAudit，因此本报告不提供包漏洞审计结论。

三个 SonnetDB 包均固定为 `[4.5.0-preview.ef266.5]`。`.nupkg.metadata` 来源均为 `https://api.nuget.org/v3/index.json`，nuspec repository commit 和实际程序集 InformationalVersion 均绑定 `1af23d8f299febfdf66b040ab3419980396acf46`。两个消费者 bin 中的三个 SonnetDB DLL 均与公开包解包 DLL 的 SHA256 相同。

| NuGet 包 | nupkg SHA256 |
|---|---|
| SonnetDB.Core | `10D1A1B43F4B310007DE4E2D009DA5BA36F1AD9CB84558C0B330AA000CD1C268` |
| SonnetDB（ADO.NET） | `0CDC30620AE08658F1A2E7227F95870ED7DE64EF1095C725A8FB17B69B7D2AC4` |
| SonnetDB.EntityFrameworkCore | `9688BA682E160639681AF3E4882DE6A58F70CF289B42552987FA7CD85D87E125` |

## 实际运行结果

最终运行于 **2026-10-16 06:43:58–06:44:28 Asia/Shanghai**，UTC `2026-10-15T22:43:58.7251011Z` 至 `2026-10-15T22:44:28.8216611Z`。两个独立消费者的 Release 构建均为 **0 warning / 0 error**。原复现 2 个进程、生命周期 10 个进程，合计 **12/12 进程 exit 0**，没有超时。

原复现 SonnetDB 实际生成以下命名外键和删除语句：

```sql
CONSTRAINT "FK_A06_Book_Author" FOREIGN KEY ("AuthorId")
    REFERENCES "A06Authors" ("Id") ON DELETE CASCADE
ALTER TABLE "A06Books" DROP CONSTRAINT "FK_A06_Book_Author";
```

DROP 执行成功后，`SaveChangesAsync` 插入原外键不允许的孤儿书籍成功。SonnetDB 与 SQLite 原始消费者均输出 `ORPHAN INSERT PASSED AFTER CONSTRAINT REMOVAL` 后退出 0；SQLite 使用原文中独立的无外键目标 DbContext，避免来源模型缓存污染对照。

增强消费者使用真实 EF migrations、每个 migration 的 `BuildTargetModel` 以及最终 ModelSnapshot；没有压制 PendingModelChanges 警告。三次迁移为 `20261016010000_Initial`、`20261016020000_Removed`、`20261016030000_Replaced`。表名和约束名包含中文、空格及双引号，例如表 `A06 书"籍`、列 `Author ID`、原约束 `FK_书"籍_作者`、替换约束 `FK_替"代_作者`。

| 独立进程阶段 | 实际操作与断言 | SonnetDB | SQLite |
|---|---|---|---|
| initial | 空历史开始；建表 SQL 保留转义后的命名外键；实际目录名称、子列、父表/父列及 Cascade 一致；有效数据成功，孤儿写入失败且行不存在 | exit 0 | exit 0 |
| removed | 新进程重开，确认初始目录、数据、历史和拒绝语义；升级删除原约束；实际外键数为 0，原父表中不存在的作者值可写入 | exit 0 | exit 0 |
| replacement | 新进程确认删除已持久化；升级增加指向替换作者表的新命名外键；旧父表有而新父表没有的值被拒绝；有效写入成功，删除新父行实际级联删除两本书 | exit 0 | exit 0 |
| rollback | 新进程确认替换名称、引用目标与约束语义；降级到无约束后孤儿写入成功，清理孤儿后继续降级；原命名外键恢复、孤儿写入再次被拒绝，历史精确回到初始迁移 | exit 0 | exit 0 |
| reopen | 新进程确认回退结果、数据和约束；降级到 0，三个业务表消失、迁移历史为空；重新建库、验证孤儿拒绝与正常写入，再次执行同一目标迁移保持精确数据和单条历史 | exit 0 | exit 0 |

SonnetDB 通过公开 ADO `GetSchema("ForeignKeys", ...)` 核对真实目录；SQLite 通过 `PRAGMA foreign_key_list` 核对列与删除动作，并从 `sqlite_master.sql` 核对持久化名称。每个阶段使用独立 dotnet 进程、复用各 Provider 专属数据库，形成上一个阶段退出后再重开的验证。所有失败写入都清理跟踪状态并验证失败行不存在；阶段边界核对精确行集与迁移历史。

### 不存在名称的 DROP

SonnetDB 当前允许对不存在的外键名执行 DROP：增强探针生成并执行 `DROP CONSTRAINT "Missing_约束"`，结果为 0。随后实际目录仍精确保留原命名外键、孤儿写入仍被拒绝、迁移历史仍只有初始迁移。这是明确验证的 no-op 行为，不写成“不存在名称时抛错”。随后对实际一致的原约束名执行正常迁移，目录才变为 0 个外键，并允许孤儿写入；因此修复没有以 DROP 返回成功代替实际移除证据。

## 可复现入口与持久收据

已保存 [独立项目与增强消费者](issue-272-public-probe/Consumer.csproj)、[生命周期程序](issue-272-public-probe/Program.cs)、[运行脚本](issue-272-public-probe/Run.ps1) 和 [验收摘要](issue-272-public-package-20261016.json)。在仓库根目录执行：

```powershell
pwsh -NoLogo -NoProfile -File docs/audits/issue-272-public-probe/Run.ps1
```

运行脚本从 GitHub 读取 #272 原始代码，在新的 artifacts 目录建立两个消费者；已有目录被拒绝，历史证据不会被覆盖。需要 GitHub CLI、.NET 10 SDK 和可访问 GitHub/NuGet.org 的网络。本机直连失败后使用现有 `127.0.0.1:7890` 代理，最终命令额外传入 `-UseLocalProxy`；代理只传给自建子进程，没有修改系统代理。

完整证据保留在本地忽略目录 `artifacts/issue-272-public-20261016-37bab8ef/`，包括原 Issue 快照、原始代码、消费者源码/配置、restore/build 日志、12 个运行 stdout/stderr、实际 DLL、私有包和生命周期数据库。`receipt.json` 保存命令参数、工作目录、PID、起止时间、退出码、包来源及文件 SHA256，SHA256 为 `F4EA672BA6CFF2C955982321F6F2FD10049DAF04E668A676B8AF2B22FDC8D3ED`。永久 JSON 摘要保留该收据身份、全部命令结果、日志哈希、资产源验证、源码副本一致性和公开 DLL 比对结果。

这是 console 真实执行证据，没有 TRX；内部 PASS 行包含重复目录断言和程序集身份检查，不把它们另算成测试用例，也不与源码测试累计。本次工作树基线为 `285186b868a59d4b22e47279ea51deb38a3a1f45`，公开包源码提交仍为前述 `1af23d8f...`，二者没有混淆。

## 保留的前置失败与边界

- `artifacts/issue-272-public-20261016-b0e11e2a/`：GitHub 直连路由拒绝连接；确认已有本地代理可用后，在后续子进程环境使用代理。
- `artifacts/issue-272-public-20261016-dfd58ac3/`：restore 成功，但资产检查发现 SDK 额外加入 `C:\Program Files\dotnet\library-packs` 源，探针主动停止；加入 `DisableImplicitLibraryPacksFolder=true` 后最终两消费者均核为唯一 NuGet.org 源。
- `artifacts/issue-272-public-20261016-a3d97196/`：SQLite 重建后的探针一次性提交原父行和子行，运行时最终模型引用替换父表，导致 EF 无法按原关系排序。本轮只修正探针为先保存父行、再保存子行，并显式使用 UTF-8 输出；随后在全新缓存和数据库上重新执行完整流程。此失败没有归为 SonnetDB 产品缺陷，也没有把早期局部成功计入最终 12/12。

本次未执行公开 Server/REST/h2c 消费者、完整 ABP BookStore、NativeAOT 发布或远端 CI；也没有新增包发布、tag、部署或修改生产代码。它们不属于原 #272 已明确的本地嵌入式复现范围，本报告不据此给出这些路径的新验收结论。主监督者负责共享 CHANGELOG/HANDOFF、完整 solution restore/Format Check、独立提交、批次推送及线上回复/关闭回读。

## 主监督者复审与集成

根重新读取原Issue及评论，审查原复现提取、迁移模型/快照、目录断言、失败行不存在、真实级联和跨进程重开流程；核验9份交付文件SHA256及最终收据列出的198份实际证据文件。18条执行记录全部exit 0且未超时，其中12条为消费者运行；两次Release构建、私有单一包源与运行DLL核验记录完整。根没有重复运行并累计同一组消费者。

9份文件按冻结清单集成，清单保存在 `artifacts/issue-supervision-20261016/issue272/delivery-manifest.json`，本段为根追加审查说明。C盘空间耗尽后，已结束的最终证据目录整体迁到D盘，779份文件逐一验证SHA256，原C路径保留junction，原收据和本报告引用仍可解析；没有删除公开包或失败证据。迁移回执为 `artifacts/issue-supervision-20261016/relocated-caches-272/final-relocation.json`。

根完整restore及原Format Check实际通过后才独立提交此项；本项没有生产变更。前批#243的旧测试断言已由独立提交 `81095ad092a612a1808df2b65b28fdba82dc85c4` 修正，完整Server1328/1328通过；该结果不计入本项12进程。推送、相关CI与GitHub回复/关闭状态由监督记录和线上回执继续更新。
