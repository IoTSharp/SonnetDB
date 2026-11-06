# #392：EF 实际进程恢复与库存应用迁移

本次以 `origin/main@bee8b35ce9937125ec5e6681abf46f6cba68432a` 为基线，接续已合入的关系 DDL 事务接线；没有重做 `EndDdlStatement`。公开 5.0.0 不包含基线之后的修复，本地候选也不代表公开版本。本单仍须共同新 Provider 的公共缓存验收完成后才能关闭。

## 实际失败与修复

库存应用的真实 `Down` 交错执行 `DropTable → UpdateData → DropCheck → DropIndex → DropColumn → history DELETE`。第一轮强杀后的两次独立进程重开成功，但业务往返失败：在事务提交时，`Products` 的缓冲行仍绑定后续 DDL 更换前的 schema 对象。前两轮父进程没有持续读取子进程输出，最终以超时结束；修正输出采集后，原始子进程异常明确为 `TableConstraintException` 的 schema 冲突。没有通过调整迁移顺序或删除断言掩盖问题。

`SqlExecutor` 现在在后续受支持 DDL 前，先校验缓冲行的 schema，再把此前成功的 DML 应用到同一私有工作区，复用现有 promotion 路径。既有外部 schema 冲突检查仍保留，源数据库目录仍只在同一 journal 提交点发布。新增 Core 用例验证交错 DML/DDL 的整笔提交、回滚、两次重开及新增 CHECK 对前一条 DML 的真实可见性。

扩大强杀初轮为 6 PASS / 3 FAIL。prepared 与 published 阶段重开暴露了两个恢复根因：补偿 backend 提前加载 catalog，构造器随后再次 `LoadExisting` 导致重复表；真实补偿在删除 prepared journal 前已经删除 undo，再次强杀后无法重做补偿，历史检查失败。

启动恢复现在只修复磁盘，构造器统一装载 catalog。undo 保留到 journal 删除后才清理；恢复过程中再次终止仍能从同一备份重做。补偿在删除当前 schema/rowstore 前校验固定 32 字节 undo owner，缺失或不匹配时拒绝，不能把缺失备份当成空库。本次未修改 journal、目录或段格式、协议、公开 API、运行时依赖及生产 JSON 路径。

原始失败在独立工作树 `artifacts/issue392-20261106/` 保留：`crash-smoke02/03/04`、`crash-all.trx` 与各自 children 日志；初次新增 Core 夹具误用了列内 `PRIMARY KEY` 方言，3 项解析失败另行保留，已按现有表级方言修正，没有扩大 SQL 解析器。

## 强杀合同

所有迁移均由真实 EF `IMigrator.MigrateAsync` 驱动，通过实际嵌入式 Provider 操作独占数据库目录。父进程观察阶段 marker、记录 PID/启动时间/父 PID/完整命令后，调用 `Process.Kill(entireProcessTree: true)`，等待退出；没有用托管异常模拟强杀。钩子复用既有 internal 测试边界，没有给产品添加环境变量后门。

| 强杀点 | 重开应恢复 | 具体边界 |
| --- | --- | --- |
| `before-commit` | 旧状态 | EF 已执行全部迁移命令和历史 INSERT，事务尚未提交。 |
| `undo-durable` | 旧状态 | undo 已写，prepared 决策未写。 |
| `prepared-durable` | 旧状态 | prepared journal 已写，尚未发布目录。 |
| `published` | 旧状态 | 新目录已发布，committed 决策未写。 |
| `committed-durable` | 新状态 | committed 决策已持久，清理未结束。 |
| `cleanup-complete` | 新状态 | 提交清理已完成，journal 仍在。 |
| `after-ack` | 新状态 | `MigrateAsync` 已返回，父进程独立观察后才强杀。 |

另外在 published/prepared 补偿及 committed 清理的真实恢复 backend 完成后再次强杀，验证下一进程重复同一决策。每个场景由两个新 OS 进程重开，验证数据、schema、索引、外部 FK、自引用、CHECK、迁移历史及失败后连接可用性；再执行业务往返与幂等迁移/回退，最后另一个进程重开。损坏 undo owner 的场景检查恢复拒绝、已发布 catalog 字节未变及 journal 保留。

这是进程崩溃证据。托管失败/取消、物理掉电及生产环境是不同门禁，不能由本测试推定。

## 独立业务消费与回退

[`eng/probes/issue392-migration-app`](../../eng/probes/issue392-migration-app/README.md) 是最小真实库存应用：关系分类与自引用、产品与外键、发货表与种子、库存和备注演进。它通过包引用运行真实 EF 查询、`SaveChanges`、两条业务 Migration 与 Down，不依赖 xUnit 或仓库源码项目引用。隔离执行器复制固定五个应用输入到新目录，以独占 NuGet/HTTP 缓存还原，保存实际依赖来源、nuspec repository commit、DLL SHA 和三次独立进程日志。

测试子进程链接相同业务模型/迁移以使用既有 internal 强杀钩子，但引用源码 Provider；这部分只计源码恢复门。应用包引用测试另列候选与 NuGet.org-only 两个门，不把源码链接夹具当成公开应用验收。

应用 Down 会删除发货模型并恢复固定验收种子的库存，真实生产数据须另设计升级后写入回补。应用 Down 不等于引擎降级；旧目录 floor / 新段格式的旧引擎拒绝仍适用。升级前保留完整备份；调用方未收到提交确认时，重连核对历史和业务状态再调用 `Migrate`。Provider 仍拒绝通用 idempotent SQL script，不能与 `Migrate` 重复调用混淆。

## 所有权与发布

本聊天 `01a1af49-10c1-7831-b3c5-75e976049f56` 负责 #392 Core 恢复修复、Migration 强杀及库存应用，不修改 #395 JSON 翻译文件。聊天 `01a1af4a-1732-79f3-bba6-c9a78edb07e1` 已接受 #392/#395 共同 Provider 候选/发行唯一协调职责；双方用 `D:/source/SonnetDB/artifacts/sonnetdb-heavy-20261106.lock` 的 `FileShare.None` 串行重负载，CPU 2、MSBuild 单节点、禁用持久构建服务。

暂态本地版本 `0.0.0-issue392.local.20261106.1` 仅属于本聊天精确源的 Protocol/Core/Data/EF 四包，必须标记 LOCAL_ONLY；共同公开版本待双方源码门禁后由 #395 冻结。没有授权改写 5.0.0、替换不可变工件、改 Secrets 或部署生产。本报告的后续验证结果与正式发布状态以最终门禁表及 issue 最新回执为准。

## 本次门禁记录

Windows / SDK 10.0.403 / Release，`DOTNET_PROCESSOR_COUNT=2`、MSBuild 单节点、关闭持久构建服务。外层 Cordonix 为每次任务设置墙钟上限并回收所属进程树；子进程保留身份、完整 SQL 和父进程观察记录。`git diff --check` 已退出 0。

| 门禁 | 当前实际结果 | 身份与边界 |
| --- | --- | --- |
| Core 工作区定向 | 13/13 PASS | `core-targeted02.trx`；包含新增的 3 项 DML/DDL 回归，早于后续 prepared 恢复修复，不转记为最终 Core 全回归。 |
| 恢复最难点定向 | 1/1 PASS | `crash-recovery-smoke.trx`，published 后强杀、补偿后再次强杀及后续完整业务往返。 |
| 全部提交/恢复强杀 | **9/9 PASS** | `crash-all-final.trx`，0 失败/跳过；全部七个提交点及两个恢复再杀点，每项两次独立进程重开、业务 SaveChanges、重复迁移/Down/Up 与最终重开。 |
| undo 归属损坏保护 | **1/1 PASS** | `crash-owner-guard02.trx`；补入的保护项独立构建，不计进前一轮 9 项。首轮 xUnit 阻塞 Task 分析警告失败保留，改为 async/await 后通过，没有压制分析器。 |
| 最终 Core 事务扩大回归 | **290/290 PASS** | `core-related.trx`，筛选 DDL / Transaction / SchemaMigration，0 失败/跳过；不是 Core 全套结果。 |
| 本分支完整 EF | **388/388 PASS** | `ef-full02.trx`，0 失败/跳过；本次实际结果，包含 embedded / REST / Frame，不能用历史相同数量替代。 |
| 独立本地四包消费 | **PASS（LOCAL_ONLY）** | `D:/sndb-t/issue392-20261106-local3/result.json`；仓库外工作目录/空包和 HTTP 缓存、四包精确身份及 cache→应用 DLL SHA 一致，业务往返与两次独立重开共 3 个进程均退出 0。 |
| NuGet.org 5.0.0 负控 | **FAIL（预期旧缺口）** | `D:/sndb-t/issue392-20261106-public500/result.json`；四包均来自官方源、repository commit=`84433f9e`，history CHECK 失败后库存仍从 7 改为 5，`Atomic stock` 断言失败。不是修复验收 PASS。 |
| 公开版本发现 | PASS（只读） | 2026-11-06 14:47 +08:00 匿名 NuGet.org flat-container 索引 HTTP 200，包含 5.0.0、不含 5.1.0；原始索引和 SHA 保存在 `public-provider-readback.json`。 |
| 新共同公开 Provider/匹配 Server | NOT_RUN | 发行协调者暂选 5.1.0，尚未公开，未冻结共同源或发布；本聊天不执行正式上传。 |
| 最终完整 restore / 规定 Format | **PASS** | 两条与 CI 相同的完整命令均退出 0；Format 有一条工作区加载警告，未宣称零警告。没有降低级别、增加排除项或用 CI 替代提交前检查。 |
| 额外 Crash 项目格式 | 本项文件 PASS / 整项目基线 FAIL | 全项目额外检查仅报既有 `M43CrashReliabilityTests.cs:33` 四处空白；文件与 HEAD blob 都为 `c9f5901685fd05cee7e6ff327c7ffdb37382ab20`。本项 8 个新增/修改 C# 文件独立 `--include` 检查退出 0；未改动或暂存 M43 文件，不将整项目称为通过。 |
| 物理掉电、生产部署 | NOT_RUN | 未由本次进程强杀或受控应用验收替代。 |

源码重现命令：

```powershell
dotnet test tests/SonnetDB.CrashTests/SonnetDB.CrashTests.csproj -c Release -m:1 --filter FullyQualifiedName~EfMigration --logger 'trx;LogFileName=ef-migration-crash.trx' --results-directory artifacts/issue392 --blame-hang-timeout 2m
```

设置 `SONNETDB_EF_CRASH_EVIDENCE` 为任务独占目录，可保留每个强杀/重开进程的完整 stdout/stderr、启动身份与父进程确认；测试自己建立的数据库目录在进程退出后由原套件回收。本次没有启动持久服务。过程只读进程快照有不可访问项，不据此声明全机没有孤儿进程。

完整 EF 首轮 `ef-full.trx` 为 318 PASS / 70 FAIL，70 项全部是 `RequestVersionExact` 无法建立 HTTP/2 连接。任务执行器为 NuGet 设置外网代理时遗漏了回环 `NO_PROXY`；只修改任务执行环境以绕过 `localhost,127.0.0.1,::1` 后，同一套二进制的真实 Frame 迁移定向 1/1 通过，完整套件 388/388 通过。没有修改产品协议、HTTP/2 要求或测试断言，原始失败和所有 owned cleanup 回执保留。

本地四包实际 ID 为 `SonnetDB.Protocol`、`SonnetDB.Core`、`SonnetDB`（ADO.NET，DLL 为 `SonnetDB.Data.dll`）及 `SonnetDB.EntityFrameworkCore`；均为暂态版本 `0.0.0-issue392.local.20261106.1`。包、DLL SHA 和 nuspec 在 `local-candidate.json` / `local-candidate-dlls.json`，源绑定基线 `bee8b35c` 加 Core 补丁 SHA-256 `2A5521D2738775A7884416FFFE7B89A7A76971178D8D0C21371B25B387B2A343`，明确标记 dirty source / LOCAL_ONLY，不冒称新的公开包或已发布提交。

包应用的首次编译缺少 `Microsoft.EntityFrameworkCore` 命名空间导入，原失败保留在 `D:/sndb-t/issue392-20261106-local1/`；修正后 local2 与最终仓库外工作目录执行器 local3 均通过。一次消费者因等待共享锁满 10 分钟而被外层按界终止，目录尚未创建；确认未执行业务后才安排下一时段，没有盲目重跑未知外部写入。
