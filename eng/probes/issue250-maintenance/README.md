# #250 维护合同复现

这里保存原生、固定 SqlSugar 消费、旧目录兼容及真实远程重开探针。源码结果、公开包结果、上游候选与正式发行是不同证据。完整矩阵与原失败见 [实施审计](../../../docs/audits/issue-250-maintenance-design-20261016.md)，正式语法见 [维护说明](../../../docs/sql-maintenance.md)。

## 固定输入

- SqlSugar PR1457：`65e8226ce25a8e1cb9cca2590683f1fd6778cab3`。
- 原始 ZIP SHA256：`46615584F90E8E7857A91140130581F9E4C3D7E7AE20B48A9C9F05383633D766`。
- 从该 ZIP 提取 `SqlSugar` 和 `SqlSugar.SonnetDBCore`，共 603 个文件。不得混入重复 Mongo 扩展目录；以任务证据 `sqlsugar-selected-source-manifest.json` 核对每个原文件。
- 原始固定运行时、探针程序集与原源码另存；候选目录在副本中应用 `sqlsugar-candidate.patch`。补丁有三个相对路径，在 SqlSugar 仓库根执行 `git apply --check`、`git apply`。若副本位于其它 Git 仓库的子目录，应先初始化独立测试仓库，避免 Git 的子目录路径筛选静默跳过补丁；必须核对实际文件 SHA。

补丁应用后三个文件的 SHA256：

| 文件 | SHA256 |
| --- | --- |
| `SqlSugar.SonnetDBCore/SonnetDB/DbMaintenance/SonnetDBDbMaintenance.cs` | `4D49652C444BB7B6BE65C677DE00ADA3F0BB7F41B9DCB1975C524195B6817FF5` |
| `SqlSugar.SonnetDBCore/SonnetDB/CodeFirst/SonnetDBCodeFirst.cs` | `6218B0BFD84E334E49CECE5B276048D6E1A9D1E29120EB9E34056CA0B52578A5` |
| `SqlSugar/Abstract/CodeFirstProvider/CodeFirstProvider.cs` | `A5C484F72CCD155B6FF35D53D9BAD0469C55469EBE2C718122CA6F7BFEDEE39F` |

## 构建和单进程运行

使用 .NET 10，在新的外部 artifacts 目录构建，避免几个探针共享默认 obj。PowerShell 7 示例中 `$probeRoot` 指本目录，`$evidence` 为新证据目录，`$candidateRuntime` 为完整固定消费者运行时目录。保留 stdout、stderr、退出码、源文件与运行程序集 SHA。

```powershell
dotnet build "$probeRoot/MaintenanceNative.csproj" -c Release --artifacts-path "$evidence/native-build" -p:SonnetPackageVersion=4.0.0
dotnet build "$probeRoot/MaintenanceCompatibility.csproj" -c Release --artifacts-path "$evidence/compat-build" -p:SonnetPackageVersion=4.0.0
dotnet build "$probeRoot/MaintenanceSqlSugar.csproj" -c Release --artifacts-path "$evidence/sugar-build" "-p:SqlSugarSourceRoot=$sqlSugarSource"
dotnet build "$probeRoot/MaintenanceSqlSugarReopen.csproj" -c Release --artifacts-path "$evidence/reopen-build" "-p:SqlSugarRuntimeDirectory=$candidateRuntime"

dotnet "$nativeAssembly" 'Data Source=D:/sndb-t/unique-native' public4
dotnet "$sugarAssembly" 'Data Source=D:/sndb-t/unique-sugar' candidate
```

不要复用已存在数据库作为初始实验。源码对照使用相同已编译消费者，只在新的完整运行时副本中替换 Core/Data；候选对照再替换 SqlSugar 与 SonnetDB 适配器 DLL。核对所有实际加载程序集 SHA，不能将不同消费者的结果直接当作同程序集对照。

固定上游包含已知 NuGet 审计告警，启用 `TreatWarningsAsErrors` 的 ProjectReference 消费者恢复可能失败。本任务原始失败包含 Azure.Identity、Microsoft.Identity.Client、System.Security.Cryptography.Pkcs 的 NU1902/NU1903，原候选构建也有 13 项源码/NuGet 警告。不能关闭审计或压制警告来宣布通过。需要继续冻结运行时的功能实验时，可用 `-p:SqlSugarRuntimeDirectory=...` 直接引用既有 DLL 快照；这不是依赖审计通过或正式发布验收。

最早历史基线项目曾设置 `NuGetAudit=false`，原源快照与构建记录保留；本目录最终项目已移除该配置。历史基线只能用于功能对照，不能倒推成默认审计通过。

## 真实远程与宿主重开

`Remote.ps1` 启动独立 Server 进程，动态分配本地 HTTP/1.1 和纯 HTTP/2 端口，创建真实数据库，然后运行同一 SqlSugar 候选消费者。它终止自己持有的 Server Process，再启动新宿主并运行独立重开消费者。实际服务端请求日志校验 REST 与 Frame HTTP/2；不能用连接字符串代替协议证据。

```powershell
& "$probeRoot/Remote.ps1" `
  -EvidenceDirectory "$evidence/remote" `
  -RuntimeDirectory 'D:/sndb-t/unique-sugar-remote' `
  -ServerDirectory "$sourceServerOutput" `
  -ServerSourceCommit "$sourceCommitAndCandidate" `
  -ExerciseAssembly "$candidateRuntime/MaintenanceSqlSugar.dll" `
  -ReopenAssembly "$reopenOutput/MaintenanceSqlSugarReopen.dll"
```

当前 exercise 的完整真实结果预期为 **11 PASS / 1 FAIL**，进程退出码仍为1；唯一允许的已知失败是标准 `CodeFirst.InitTables` 的 #227 事务 DDL。脚本原样保存该失败，并验证失败的名称。重开消费者要求5/5和退出0。不能把脚本完成写成 CodeFirst 全通过；#227 实现后应更新预期并重新运行真实 CodeFirst，不能继续忽略新增失败。

重开验证实际目录名字、删除主键后的重复行、方向/包含列、删除索引、存量数据、自增继续、删除最大值与拒绝冲突之后的下一值。宿主终止不是物理断电模拟。

## 旧版本目录

```powershell
dotnet "$publicCompatibilityAssembly" create-legacy 'D:/sndb-t/unique-legacy-original'
# 复制完整旧目录至新的测试路径，原目录永久保留。
dotnet "$sourceCompatibilityAssembly" verify-upgrade 'D:/sndb-t/unique-legacy-copy'
dotnet "$sourceCompatibilityAssembly" create-options 'D:/sndb-t/unique-options'
dotnet "$publicCompatibilityAssembly" reject-old 'D:/sndb-t/unique-options'
dotnet "$sourceCompatibilityAssembly" remove-options 'D:/sndb-t/unique-options'
dotnet "$publicCompatibilityAssembly" reject-old 'D:/sndb-t/unique-options'
```

真正 public 4.0.0 创建 v10；新引擎分配避让冲突的实际 PK 名并保存 v16。最后特性删除后仍为16；旧引擎明确拒绝。此探针比较 schema SHA，不声称目录所有文件未写入。没有通过改新格式 header 冒充旧目录。

仓库 Core、真实 ADO、进程强杀测试负责更广的索引编码、在线续建、事务、并发和恢复边界；独立探针不能替代完整 solution restore 与提交前 Format Check。
