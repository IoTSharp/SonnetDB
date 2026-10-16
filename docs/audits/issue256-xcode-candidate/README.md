# #256 XCode 本地候选复现

候选基于 NewLifeX/NewLife.XCode PR #101 的固定头 `757fa2a078906f87783e55fe0e9d5f54a9e916b9`，仅删除 `PageSplit` 的两行 Int32 范围保护；没有推送或修改上游 PR。`maximumRows=0` 保留 XCode 原有“无限制”语义。

`Program.cs.txt` 是本轮实际编译的消费者；`Consumer.csproj.txt` 只将验证工作树的绝对路径替换为 `$(SonnetDBSourceRoot)` 参数，其他内容与实际项目相同。将两文件复制为新独占目录中的 `Program.cs`、`Consumer.csproj`，在该目录提供以下 `Directory.Build.props`，并提供内容为 `<Project />` 的 `Directory.Build.targets` 和 `Directory.Packages.props`，隔离祖先仓库设置：

```xml
<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors><IsAotCompatible>false</IsAotCompatible><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>
```

使用独占 NuGet 缓存及只含 NuGet.org 的配置，先构建固定 XCode 的 `netstandard2.1`，把输出目录作为 `XCodeDirectory`。运行 `dotnet Consumer.dll original-guard <独占数据库目录>` 应观测到六次预期拒绝。保留原始输出后，在 XCode checkout 执行 `git apply --check XCodeInt64Pagination.patch` 和 `git apply XCodeInt64Pagination.patch`，使用不同 obj/bin 目录重建。

消费者构建参数包含 `-p:SonnetDBSourceRoot=<本次SonnetDB源码根> -p:XCodeDirectory=<候选XCode输出目录>`；候选先运行 `exercise`，再以同一数据库目录运行 `reopen`。默认使用嵌入式连接；远程连接从 `ISSUE256_CONNECTION` 环境变量读取。不要把凭据放在命令参数或仓库文件中。

`Remote.ps1` 与 `Common.ps1` 复制 Server 产物并校验哈希，在独占端口启动真实宿主，分别运行 REST/HTTP2 Frame 的 exercise/reopen。示例：

```powershell
./Remote.ps1 -EvidenceDirectory <新证据目录> -ServerDirectory <Server输出目录> -ServerSourceCommit <源码身份描述> -ProbeAssembly <Consumer.dll绝对路径> -RuntimeDirectory <新短运行目录>
```

每次必须使用新目录；脚本保留进程身份、超时、退出码、实际 HTTP 请求版本及 Frame 证据。Server 重开由终止脚本自己创建的子进程再启动完成，不冒称优雅停机。消费者每个进程有 301 项断言，覆盖 PageSplit 字符串/SelectBuilder、builder 不变、`DAL.Query`/`QueryAsync`/`Select`、空表、完整 Int64 窗口、参数化、错误输入及重开。

原始与候选 XCode 均有 105 个既有构建警告，0 错误；SonnetDB 和消费者构建为 0 警告/错误。此目录是可复现的本地候选，不代表正式包或 XCode 上游已交付。实际命令、固定包源、全部失败/成功记录及哈希见上一级 #256 审计报告和 JSON。
