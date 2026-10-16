# #252 原生时长复现

这组文件是实际 Chloe 消费端及真实 REST/HTTP/2 服务验证工具。业务实体直接使用
`TimeSpan` / `TimeSpan?`，不要求业务代码手工把时长转成整数或字符串。
源码合同见 `docs/compatibility/duration-native-contract.md`。

## 构建与执行

需要 .NET 10 SDK、PowerShell 7、独立 Chloe checkout，以及已初始化子模块的 SonnetDB
源码。下列变量均应改为本机绝对路径，输出和数据库目录必须是本次新建的目录。
Chloe 应放在 SonnetDB checkout 外，避免继承 SonnetDB 的 `Directory.Build.props`。

```powershell
$SonnetRepo = 'D:/source/SonnetDB'
$ChloeRepo = 'D:/validation/Chloe'
$Output = 'D:/validation/issue252'
$Validation = Join-Path $SonnetRepo 'scripts/validation/issue252-native-duration'
$EngineBuild = Join-Path $Output 'engine'
$ChloeBuild = Join-Path $Output 'chloe'
$ConsumerBuild = Join-Path $Output 'consumer'

# 在独立、无未提交修改的真实 Chloe checkout 中应用候选。
git -C $ChloeRepo checkout --detach bb7ec8735bd4511c35453eb009f70cc606795703
git -C $ChloeRepo apply --check (Join-Path $Validation 'chloe-timespan.patch')
git -C $ChloeRepo apply (Join-Path $Validation 'chloe-timespan.patch')

dotnet build (Join-Path $SonnetRepo 'src/SonnetDB/SonnetDB.csproj') -c Release --artifacts-path $EngineBuild
dotnet build (Join-Path $SonnetRepo 'src/SonnetDB.Data/SonnetDB.Data.csproj') -c Release --artifacts-path $EngineBuild
dotnet build (Join-Path $ChloeRepo 'src/Chloe.SonnetDB/Chloe.SonnetDB.csproj') -c Release '-p:TargetFrameworks=net10.0' '-p:TargetFramework=net10.0' --artifacts-path $ChloeBuild

$ServerDirectory = Join-Path $EngineBuild 'bin/SonnetDB/release'
$EngineBin = Join-Path $EngineBuild 'bin/SonnetDB.Data/release'
$ChloeBin = Join-Path $ChloeBuild 'bin/Chloe.SonnetDB/release_net10.0'
dotnet build (Join-Path $Validation 'DurationConsumer.csproj') -c Release --artifacts-path $ConsumerBuild "-p:ChloeBin=$ChloeBin" "-p:EngineBin=$EngineBin" "-p:DependencyBin=$ServerDirectory"
$Probe = Join-Path $ConsumerBuild 'bin/DurationConsumer/release/DurationConsumer.dll'

# 两个独立进程，第二个进程重开同一数据库。
$EmbeddedRoot = Join-Path $Output 'embedded-data'
dotnet $Probe exercise $EmbeddedRoot
dotnet $Probe reopen $EmbeddedRoot

# 实际 HTTP/1.1 REST 和 HTTP/2 Frame；每个协议在 exercise 后终止服务再重开。
$SourceCommit = git -C $SonnetRepo rev-parse HEAD
& (Join-Path $Validation 'Remote.ps1') -EvidenceDirectory (Join-Path $Output 'remote-evidence') -ServerDirectory $ServerDirectory -ServerSourceCommit $SourceCommit -ProbeAssembly $Probe -RuntimeDirectory (Join-Path $Output 'remote-runtime')
```

请检查每一步退出码。`ServerSourceCommit` 是人为提供的来源标签；存在未提交源码时须在
标签中明确说明，并保存源码清单，不能仅用 HEAD 代表实际二进制。脚本同时保存服务文件
SHA256、精确子进程身份、退出/终止记录、stdout/stderr 和服务访问协议证据。
临时访问凭据只用于本次本地服务进程，未写入参数回执。脚本保留数据库便于后续检查，
不会自动删除证据。进程终止验证不代表断电或文件系统故障验证。

`exercise` 通过时输出 `checks=26`，`reopen` 输出 `checks=17`；每种连接共 43 个断言。
覆盖生成 DDL、完整 ticks 范围、NULL、参数比较、批量、同步/异步读写、事务提交/回滚、
空结果、全 NULL、日期相减及重开。消费者还会打印实际加载的四个程序集的 SHA256。

未修改的同一 Chloe commit 可单独构建为另一份输出，使用 `negative <新数据库目录>`
验证其明确拒绝 Duration DDL/写入/投影，预期为 3 个拒绝断言。此负向对照需要新引擎，
用于隔离 Chloe 的原始类型限制。

对真实旧服务，可向 `Remote.ps1` 提供旧服务目录并追加 `-ProbeMode oldtime`：新客户端
显式 `DbType.Time` 的一天内 TimeSpan 仍写入/返回 TimeOnly；27 小时在本地拒绝。
每协议为 exercise 5 个断言、reopen 2 个断言。该模式不声称旧服务支持 DURATION。

## 本次证据与边界（2026-10-16）

证据保存在工作区 `artifacts/issue-supervision-20261016/issue252-work/`，未提交生成物。
测试计数以 TRX 和原始进程日志为准，不将多个有重叠筛选器的计数相加作为独立覆盖数。

| 检查 | 结果与证据 |
| --- | --- |
| SQL、Frame/REST 解码与 schema 迁移较大范围回归 | `results/core-sql02.trx`：3084/3084，通过且无跳过 |
| Duration/TIME、Frame、REST 解码定向单元测试 | `results/duration03.trx`：44/44，通过且无跳过 |
| 真实远程 ADO.NET、旧协议拒绝与回退 | `results/remote02.trx`：126/126，通过且无跳过 |
| 迁移子进程强杀/恢复再强杀 | `results/duration-crash01.trx`：7/7，通过且无跳过 |
| 原始真实 Chloe 拒绝对照 | `logs/chloe-original-negative01.*`：3 个拒绝断言 |
| Chloe 候选嵌入式 | `logs/chloe-candidate-embedded02.*` 与 `logs/chloe-candidate-embedded-reopen01.*`：26+17 |
| Chloe 候选真实 REST 与 HTTP/2 | `chloe-remote01/`：每协议 26+17，共 86 个远程断言 |
| 新客户端连接真实旧 v4 服务 | `old-server-time01/`：每协议 5+2，共 14 个 TIME 兼容断言 |
| 实际旧 v13 引擎与 v14 屏障 | `old-barrier01/`：6 个迁移/恢复阶段各验证旧引擎拒绝、新引擎恢复与删除后旧引擎拒绝，共 18 次探针调用 |
| Core/Server AOT 分析 | `logs/build-server-tests03.*`：0 警告/错误；由项目配置启用分析 |
| Data 显式 AOT/Trim 分析 | `logs/aot-data01.*`：0 警告/错误；显式开启 IsAotCompatible、EnableAotAnalyzer、EnableTrimAnalyzer |
| 可移植消费者项目 | `logs/portable-consumer-build01.*`：0 警告/错误；使用目录参数构建成功 |

旧 v13 DLL 的 SHA256 为
`B838405885FC15F0874AAEAA2499CD4616D2B879251BC88E69684E24E505ADB6`。
pending 日志及恢复再终止场景中的表文件内容与 mtime 均未改变；completed 日志场景中，
旧引擎先重写相同完成标记才拒绝 v14，仅日志 mtime 改变，日志字节、raw 数据、WAL 和
catalog 未变。不能把该旧实现称为零文件写入。旧引擎/旧服务均来自冻结的真实旧源码
构建，不能标作公开正式 4.0 NuGet 包。

保留了最初失败的证据：首次 Core Duration 测试暴露 ALTER 类型识别遗漏；首次较大范围
Core 测试有 1 个旧 snapshot 未知版本测试失败（v3 已合法，现使用 v4）；首次远程回归
暴露重启后测试连接端口复用和旧版本断言；首次 Chloe 候选暴露 InsertRange 独立参数路径
仍使用 DbType.Time。相应代码/测试修复后的结果单独记录。首次 Server 构建因缺少仓库
子模块失败，初始化固定子模块后构建通过。Chloe 构建自身有 38 个既存警告；不计入
SonnetDB 的零警告结论。

`chloe-timespan.patch` 是基于上述真实 commit 的 5 文件候选补丁，保留 #251
DateTimeOffset 和 #236 日期亚毫秒输入的明确拒绝。后续 #251 必须在该补丁之上串行
修改共享文件。尚未执行 Chloe 上游提交/合入/正式发行及 SonnetDB 正式 NuGet 发行；
此证据只能支持源码候选交付。NativeAOT 二进制发布未执行，不能把分析通过写成原生发布
通过。主工作树最终 restore、CI 同级 Format Check、提交和推送由集成监督者执行，
本目录的定向验证不能替代这些门禁。
