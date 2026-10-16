# Issue #262 原生日期消费者探针

此探针验证候选 SonnetDB 源码与真实 Chloe 消费者之间的 DateTime 值、ticks、Kind 和查询物化行为。它引用指定目录的 DLL，不引用公开 SonnetDB NuGet 包，因此不构成正式发布包验收。

## 固定输入与结果

- SonnetDB 基线：`285186b868a59d4b22e47279ea51deb38a3a1f45` 加本 Issue 的冻结补丁，文件哈希见审计 JSON。
- Chloe 原始源码：`bb7ec8735bd4511c35453eb009f70cc606795703` 的干净 `git archive`；未混入 #264 DECIMAL 补丁。
- 原始 Chloe + 候选引擎：嵌入式、实际 REST、实际 h2c 各 **5 PASS / 9 FAIL**。原始失败保留，不能作为通过证据。
- 六文件 `chloe-native-datetime.patch` + 候选引擎：三种协议各 **14/14 PASS**，共 **42/42**。
- 实际测试进程时区：Windows `China Standard Time`，偏移 `+08:00`。脚本不修改系统时区；复现时应确认输出的 `ENV` 行为相同非 UTC 环境，不能用 UTC 环境代替本次边界证据。
- 程序打印加载的四个核心程序集路径、信息版本和 SHA256；信息版本可能来自构建环境，固定源码身份以源归档及清单 SHA 为准。

14 项覆盖 UTC/Local/Unspecified 参数持久值、列 `.Date`/`.AddDays`、闰日跨午夜嵌套、NULL、空结果、`Now`/`Today`/`UtcNow`、当前时间嵌套、Local 参数比较以及原生 `SELECT @stamp` 的毫秒边界。日期断言同时比较 ticks 与 Kind；动态当前时间在有界前后时刻之间检查。

## 候选适配补丁的边界

补丁删除 `Now`、`Today`、`UtcNow`、`.Date`、DATEADD 翻译中多余的外层 `TO_DATETIME`，避免它把已具有 Kind 的值重新转为 Unspecified。它也删除 `Utils.NormalizeParameterValue` 把 Local 改标为 Unspecified 的步骤，让原生 ADO 参数绑定按既有规则处理 Local 的真实瞬时。

因此在 `+08:00`，Local `2024-02-29 00:30` 的持久值由旧适配器的 `2024-02-29 00:30Z` 改为 `2024-02-28 16:30Z`。这是 **Chloe 参数行为修正**；SonnetDB 原生绑定、存储格式及毫秒精度不变。旧适配器写入的数据不会自动迁移，迁移必须知道当时采用的本地时区。原有亚毫秒参数保护、DateTimeOffset/TimeSpan 限制不变。

这不意味着所有参数保留 Kind/ticks：原生 DateTime 参数仍绑定为 UTC Unix 毫秒，Unspecified 按 UTC，原生 `SELECT @stamp` 仍返回 Int64。完整 ticks/Kind 保真适用于内存中的日期表达式和双方确认结果版本 4 的传输。DateTimeOffset 不在此合同内。

## 在 D 盘复现

需要 Windows、PowerShell 7、.NET 10、固定 Chloe 源码和已构建的候选 SonnetDB。把此文件夹复制到 D 盘独立工作目录的 `consumer`；`Program.cs.txt` 是显式编译项，文件名用于避免仓库的泛扫描将它计入其他项目。三个空 `Directory.*.props/targets` 隔离上级构建设置。

以下路径对应本次留存证据，可替换为同结构的新独立目录。使用新标签，勿覆盖原收据。

```powershell
$taskWork = 'D:/source/SonnetDB/artifacts/issue-supervision-20261016/issue262-work'
$taskChloe = "$taskWork/chloe-source"
$taskEngine = "$taskWork/source"
$taskConsumer = "$taskWork/consumer"

# 先在干净的固定 Chloe 源码构建原始适配器，再用下列命令应用候选补丁。
git -C $taskChloe apply --check "$taskConsumer/chloe-native-datetime.patch"
git -C $taskChloe apply "$taskConsumer/chloe-native-datetime.patch"

dotnet build "$taskChloe/src/Chloe.SonnetDB/Chloe.SonnetDB.csproj" -c Release `
  -p:TargetFrameworks=net10.0 -p:Nullable=annotations -p:TreatWarningsAsErrors=true `
  -p:NuGetAudit=false -nr:false

dotnet build "$taskConsumer/DateTimeConsumer.csproj" -c Release `
  "-p:ChloeBin=$taskChloe/src/Chloe.SonnetDB/bin/Release/net10.0" `
  "-p:EngineBin=$taskEngine/src/SonnetDB.Data/bin/Release/net10.0" `
  "-p:EngineDependencyBin=$taskEngine/tests/SonnetDB.Tests/bin/Release/net10.0/win-x64" `
  -nr:false

& "$taskConsumer/Invoke-ConsumerMatrix.ps1" -WorkRoot $taskWork `
  -ConsumerDirectory "$taskConsumer/bin/Release/net10.0" `
  -ServerDll "$taskEngine/src/SonnetDB/bin/Release/net10.0/SonnetDB.dll" `
  -Label 'chloe-reproduce-candidate'
```

先构建完整 SonnetDB Server/Data 和 Server 测试项目，使上述依赖目录存在。原始 Chloe 阶段应在未应用补丁的源码构建，并复制整个消费者输出到单独的不可变目录后使用 `-ExpectedFailures 9`；候选阶段必须省略此参数并取得零失败。每个协议的原始失败数量严格匹配 9，绝不忽略任意非零退出。`Nullable=annotations` 为旧外部源码提供注解上下文，没有压制警告；原始和候选最终构建均零警告/错误。

矩阵启动自己拥有的 Server，使用动态回环端口、短 D 盘数据目录和仅用于本机探针的测试令牌；HTTP/1 REST 与强制 HTTP/2 h2c 分别执行。每个消费者限时 90 秒，启动准备限时 30 秒，结束时核对并终止自己启动的 Server。`Invoke-CheckedCommand.ps1` 保留命令、退出码、已记录进程身份及清理结果；它不宣称未观察到的子进程已证明不存在。

完整结果、原始失败、AOT 分析边界和生产文件清单见 `docs/audits/issue-262-native-datetime-20261016.md` 与同名 JSON。正式发布的 Server、驱动及 Chloe 组合仍待发布后验收；本探针不执行发布、提交或 Issue 关闭。
