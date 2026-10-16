# #259 UInt64 按位与/或真实 Chloe 消费者

此探针使用实际 Chloe Context，验证 UInt64 的精确存取、除法及新按位函数的 SQL 翻译、服务端执行和物化。DLL 由指定源码构建，**不构成正式 NuGet 发布组合验收**。

固定 Chloe PR #435 源码为 `bb7ec8735bd4511c35453eb009f70cc606795703`。原适配器在 embedded/REST/h2c 各 3 PASS、13 个按位 guard FAIL；本目录单文件 `chloe-native-uint64-bitwise.patch` 只修改 `SqlGenerator.VisitBitAnd/VisitBitOr` 的 UInt64 分支，候选三协议各 **16/16 PASS**。普通 UInt64 存取、比较、除法始终通过。没有混入 #260 Parse、#262 日期或 #264 DDL 补丁。

原始/候选两阶段使用完全相同的消费者、SonnetDB.Core、SonnetDB.Data、Chloe DLL，只替换 `Chloe.SonnetDB.dll`。程序记录实际加载的四个依赖程序集路径、信息版本、SHA256、生成 SQL 和精确值。信息版本由构建环境产生，不替代固定源码归档的身份。

16 项覆盖三项既有 UInt64 能力、与/或参数掩码、列/列、常量掩码、NULL、嵌套、高位 WHERE、异步查询、空结果和 UPDATE。取值包含 0、7、Int64.MaxValue、2^63、UInt64.MaxValue、2^53+1。按位投影先确认生成 SQL 含 `bit_and_uint64` / `bit_or_uint64`，再执行真实查询；不存在客户端逐行计算替代。

函数返回 Decimal(20,0)，Chloe 按已有映射物化为 UInt64。完整值域的原生 ADO 参数须用 Decimal/DbType.Decimal；现有 CLR UInt64 参数大于 Int64.MaxValue 仍会被 binder 拒绝。Chloe 保持已有的字符串参数加 `CAST(@P AS DECIMAL)` 无损传参方式。

## 复现

需要 Windows、PowerShell 7、.NET 10、固定 Chloe 源码及已构建的候选 SonnetDB Core/Data/Server。所有构建输出与包缓存使用 D 盘。先把此目录复制为独立工作目录的 `consumer`；三个空 `Directory.*` 文件隔离祖先仓库的构建设置，`Program.cs.txt` 作为显式 Compile 输入。

```powershell
$taskWork = 'D:/source/SonnetDB/artifacts/issue-supervision-20261016/issue259-work'
$taskEngine = 'D:/source/SonnetDB-worktrees/issue259-uint64-bitwise'
$taskChloe = "$taskWork/chloe-source"
$taskConsumer = "$taskWork/consumer"

dotnet build "$taskChloe/src/Chloe.SonnetDB/Chloe.SonnetDB.csproj" -c Release `
  -p:TargetFrameworks=net10.0 -p:Nullable=annotations -p:TreatWarningsAsErrors=true `
  -p:NuGetAudit=false -nr:false
dotnet build "$taskConsumer/UInt64BitwiseConsumer.csproj" -c Release `
  "-p:ChloeBin=$taskChloe/src/Chloe.SonnetDB/bin/Release/net10.0" `
  "-p:EngineBin=$taskEngine/src/SonnetDB.Data/bin/Release/net10.0" `
  "-p:EngineDependencyBin=$taskEngine/tests/SonnetDB.Core.Tests/bin/Release/net10.0" `
  -nr:false

# 原始 guard 阶段保留独立整个输出目录，预期恰好 13 个按位拒绝。
Copy-Item "$taskConsumer/bin/Release/net10.0" "$taskWork/consumer-original-bin" -Recurse
& "$taskConsumer/Invoke-ConsumerMatrix.ps1" -WorkRoot $taskWork `
  -ConsumerDirectory "$taskWork/consumer-original-bin" `
  -ServerDll "$taskEngine/src/SonnetDB/bin/Release/net10.0/SonnetDB.dll" `
  -Label 'reproduce-original' -ExpectedFailures 13

git -C $taskChloe apply --check "$taskConsumer/chloe-native-uint64-bitwise.patch"
git -C $taskChloe apply "$taskConsumer/chloe-native-uint64-bitwise.patch"
dotnet build "$taskChloe/src/Chloe.SonnetDB/Chloe.SonnetDB.csproj" -c Release `
  -p:TargetFrameworks=net10.0 -p:Nullable=annotations -p:TreatWarningsAsErrors=true `
  -p:NuGetAudit=false -nr:false
Copy-Item "$taskWork/consumer-original-bin" "$taskWork/consumer-candidate-bin" -Recurse
Copy-Item "$taskChloe/src/Chloe.SonnetDB/bin/Release/net10.0/Chloe.SonnetDB.dll" `
  "$taskWork/consumer-candidate-bin/Chloe.SonnetDB.dll"
& "$taskConsumer/Invoke-ConsumerMatrix.ps1" -WorkRoot $taskWork `
  -ConsumerDirectory "$taskWork/consumer-candidate-bin" `
  -ServerDll "$taskEngine/src/SonnetDB/bin/Release/net10.0/SonnetDB.dll" `
  -Label 'reproduce-candidate'
```

Chloe 归档若位于另一仓库内，须在其根目录放置同样的三个空 `Directory.*` 文件，使外部 ORM 保持自己的构建边界；它不是 NativeAOT 生产模块，不能把祖先 SonnetDB 的 IsAotCompatible 误套到它。`Nullable=annotations` 为外部源码提供注解上下文，没有压制任何警告。

矩阵使用动态回环端口、短路径 `D:/sndb-t/i259-*`、仅本机探针令牌；单个消费者限时 90 秒，Server 准备限时 30 秒，并在 finally 终止自己启动的 Server。每次使用新标签，原始非零退出必须严格匹配 3 PASS/13 FAIL；候选必须 16 PASS/0 FAIL。runner 保存退出码、命令及已观察进程身份，不声称未观察到的子进程已证明不存在。

补丁保留上游 CRLF，`.gitattributes` 保留其原始字节；已经在原始文件上实际 apply，并核对结果 SHA 等于编译候选。不要通过按行 PowerShell 管道重写补丁。完整证据见 `docs/audits/issue-259-uint64-bitwise-20261016.md` 与同名 JSON。
