# FreeSql #2291 冻结消费者与 SonnetDB 5.0 候选复验

2026-11-03（北京时间）。结论：**固定 FreeSql 源码与旧 SonnetDB 5.0 候选的完整 Provider 及三协议业务复验通过，最终公开包复验仍待执行。** 本报告不覆盖其它 ORM，也不把 FreeSql 的源码构建称为其公开 NuGet 发布。

## 固定身份

- FreeSql 来源为 [dotnetcore/FreeSql#2291](https://github.com/dotnetcore/FreeSql/pull/2291) 的 `IoTSharp/FreeSql` 精确提交 `a2268c1ff580b77d9ee202b3d34f230be59eab8b`。
- 源码使用该提交的 codeload ZIP，4,941,161字节、1,891项，ZIP SHA256 `fda55bcca576392ded395a21b28bdb73e3103689419709626df092ab6c9ddbb9`。在构建前后，把1,336个原始 C#、项目、props/targets及生命周期脚本的字节哈希与归档逐项核对，全部一致。
- SonnetDB 为正式 Actions run `41348714535` 的 `5.0.0` 候选 NuGet 与对应 Windows NativeAOT Server，提交 `455bf3b53b1e56df0064d8f574b0595cbb0b609e`；这不是公开 NuGet/Release 成功的结论。
- 恢复的 SonnetDB、Core、Protocol 包均核对精确版本、同一完整 nuspec repository commit，以及与指定候选 feed 的整包 SHA256。六个运行阶段实际加载的 Data、Core、Protocol DLL 哈希又分别匹配对应包的 DLL payload。
- 实际 Server SHA256：`b3a2991a7bbc4750c2d75f711e3151d5d7059f04b3bad853308e1481a8f22ca4`；消费者 DLL SHA256：`d8fc7e4111aab0955b2d7c91b0248c516cde61a03f51aeab72f732b549592cf4`。

FreeSql Core/Provider 从被冻结的源码项目构建。它们仅通过 NuGet 使用 SonnetDB，未引用任何 SonnetDB 源码项目。该组合不能替代未来公开 FreeSql Core/Provider 与公开 SonnetDB 的全部公开组合验收。

## 实际结果

成功阶段于 `2026-11-03T11:40:11Z` 结束。

| 执行范围 | 结果 |
| --- | --- |
| 完整冻结 Provider 套件 | **237/237 PASS，0 FAIL，0 SKIP** |
| embedded 业务／独立重开 | 25项／8项 PASS |
| REST 业务／终止 Server 后重开 | 25项／8项 PASS |
| HTTP/2 Frame 业务／终止 Server 后重开 | 25项／8项 PASS |
| 实际 NativeAOT Server 协议收据 | REST SQL68、HTTP/2 Frame32、HTTP/2事务21 |
| 六阶段运行程序集与 NuGet payload 身份 | PASS |
| 原始冻结源码在运行前后保持一致 | PASS |

业务旅程来自提交内 `FreeSql.Tests.Provider.SonnetDB.ReleaseAcceptance/Program.cs`，未修改业务断言：参数化 CRUD、INSERT/UPDATE/DELETE RETURNING、UPSERT/DO NOTHING、精确 DECIMAL、100ns DATETIME2、DATETIMEOFFSET UTC时刻、竞争锁定读、DDL提交/回滚、受支持的 STRING→INT 主键迁移、视图身份去重、列元数据、存储过程输出参数/多结果、空首结果，以及 measurement 分组/HAVING/排序/分页的同步与异步入口。

237项套件包括嵌入式执行和SQL生成等测试，不把它们全部称为三协议测试，也不与旅程检查数相加制造覆盖率。Server 在已确认提交后实际终止并在相同目录重启；这不证明物理掉电、介质损坏、全部模型恢复、生产负载或实机安装验收。

## 保留的初始失败及修正

1. 第一次恢复因外层 SonnetDB 中央包管理被继承而发生 NU1008。修正仅在本任务源码归档的**父目录**加入隔离 `Directory.Packages.props` 与空 `Directory.Build.targets`，没有改动冻结项目及依赖。原失败报告保留。
2. 隔离后首次完整套件为236 PASS、1 FAIL、0 SKIP。唯一失败在 `CompositeRowVersionBatchUpdate_UsesPerRowExpectedVersion` 对中文异常片段“行级乐观锁”的断言；FreeSql 的 `CoreErrorStrings` 依据 `CultureInfo.CurrentUICulture` 选择文本，而本任务最初把测试宿主设成英文。实际业务已抛出预期的 `DbUpdateVersionException`，`UPDATE` SQL断言也先通过。按该既有中文测试合同设置独立进程的 zh-CN 后，相同已编译程序集的原失败项1/1通过，再完整重跑取得237/237；没有删断言、修改源码或替换异常语义。
3. 最终候选阶段复用本任务首次隔离恢复创建的候选包缓存，各包重新校验为指定候选字节；新日志、HTTP缓存、TEMP和业务数据目录独立。该复用明确记录在报告中。最终**公开包模式禁止复用候选缓存**。
4. 生命周期脚本仍以提交内 `run.mjs` 为基础。原文件保持冻结；本任务在独占副本中只加入启动轮询600次／60秒上限、固定 dotnet 绝对路径以及 native Cordonix 进程身份快照和 spawn/exit记录。原脚本与副本分别保存哈希，语法和四种协议日志正则均先通过微型检查。

成功报告不会覆盖这些失败。对应阶段目录分别为 `run-candidate-455bf3b5`、`run-candidate-isolated-455bf3b5`、`locale-target` 和 `run-candidate-ready-455bf3b5`。

## 公开包复验的可重复入口

全部准备文件位于 `artifacts/release-5.0-20261103-freesql/`。复用 `run_acceptance.py`，为最终阶段选择新的 phase、传入已校验的实际公开 Server 路径，并把 `--expected-sonnet-commit` 设为最终发行提交；**省略** `--package-source` 和 `--prepared-candidate-cache`。

`run-public-template.json` 是待替换具体发行SHA/Server路径的 Cordonix 请求模板。它不是已经执行的公开验收。该模式只配置 NuGet.org，新建包/HTTP/插件缓存、TEMP、TRX和数据库目录，强制三包 nuspec 同源，并重新执行完整237项和同一业务旅程，再检查运行DLL与恢复包payload一致。每个新阶段保存实际执行脚本副本及完整身份收据。同一个冻结源码目录的阶段必须串行构建。

执行环境使用 .NET 10、已固定的 Node 和 Python 路径；MSBuild、.NET运行时及子进程最多使用2个处理器。单阶段总墙钟上限1,100秒，每个外部命令另有超时；本地 native Cordonix 最外层限20分钟。Server 的创建、停止和重开由同一完整生命周期 runner 负责，未把服务作为独立 daemon 留在 bounded 调用外。

## 证据及资源

主收据为 `artifacts/release-5.0-20261103-freesql/run-candidate-ready-455bf3b5/run-result.json`，整理收据为同任务根目录的 `candidate-summary.json`；TRX为该阶段 `suite-results/provider.trx`。`acceptance/` 保存六阶段业务/程序集报告、实际 Server 日志、协议计数、进程事件与保留原始 FILETIME 的 native进程快照，临时令牌已脱敏。

生命周期8个业务子进程均有 spawn/exit 配对，native Cordonix 归属Job清理报告残留0。额外全局只读进程查询因系统进程访问受限而返回 partial，不能据空匹配扩称全机没有其它进程。

最终成功阶段的 scratch TEMP经绝对路径、独占marker、数量/时间和reparse检查后已回收，共5,806项；Windows长路径经显式扩展路径处理。业务验收数据库、源归档、缓存、TRX、失败记录和快照作为证据保留。早期失败轮次仍使用上游测试默认系统TEMP，无法依据名称安全辨认其全部目录归属，本任务没有宽泛删除这些历史位置。

本智能体没有推送、合并、更新GitHub Issue、修改共享发布源码或发布任何包。正式公开包复验、最终发行SHA门禁和#230的整体判断由主智能体继续处理。
