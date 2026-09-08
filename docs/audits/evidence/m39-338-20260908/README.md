# M39 #338 本地验收产物

验收合同见 [m39-338-20260908.md](../../m39-338-20260908.md)。`validation.json` 记录本次最终结果、时间、源码与程序集 SHA-256；`test-evidence.zip` 保留 TRX、构建日志、进程启动身份和有界运行脚本。

本次验收基线为 `40a8f09ab61a284fdd2cae166fa495f87d4d3bf1` 之上的提交前工作树，其中包含原有 #335/#336 工作及本次 #338。哈希标识实际验收内容，不将基线提交号当作包含本次实现的提交号。验收过程未执行远程 CI、合并、发布或 NativeAOT 发布；AOT 证据是生产 Core/Server 的 Release 构建分析。

进程检查确认 16 个已记录根进程全部退出，未发现遗留测试进程。自动审批拒绝了两种临时文件清理方式，原因仅为 `blocked by policy`，故保留 `C:\Users\mysti\AppData\Local\Temp\sndb338-validation-20260908` 下已归档的 44 个文件；未绕过策略继续删除。

## 最终运行

| 产物 | 用途 |
|---|---|
| `m39-338-core-complete.trx` / `core-complete.log` | 完整 Core 回归，包含最终新增的所有 #338 用例 |
| `m39-338-server.trx` / `server.log` | SqlFrameEndpointTests 和 ServerOptionsTests；真实本地 Kestrel REST/Frame 与配置验证 |
| `m39-338-crash.trx` / `crash.log` | 11 个关系触发器/outbox 真进程强杀恢复用例 |
| `release-aot.log` | Release 生产构建，Core 与 Server 的 trim/AOT 分析，零警告为通过条件 |
| `aot-properties-core.log` / `aot-properties-server.log` | 验证生产项目实际启用 IsAotCompatible、EnableAotAnalyzer、EnableTrimAnalyzer、TreatWarningsAsErrors |
| `*.log.process.json` | dotnet 根进程 PID、父进程、创建时间和完整参数 |
| `process-cleanup.json` | 最终进程归属检查结果 |

最终统计只计上述三个 TRX，不累计重叠的聚焦测试和中间重跑。`core-focused-*.log`、`m39-338-core-regression.trx` 等中间结果保留失败事实，不能当作最终 PASS；旧版本断言、测试 SQL 语法及编译问题的修复均由最终全量回归重新验证。

`m39-338-core-final.trx` 曾出现一个未修改测试夹具的收尾竞态：`KvRedirectTests.Create_WithMixedRedirectPolicies_IsolatesCachedHandlers(strictFirst: true)` 在 `KvLoopbackHttpServer.DisposeAsync/AcceptTcpClientAsync` 抛出 `ObjectDisposedException`，该轮 4364/4365 通过，#338 用例均通过。未修改 KV 源码或断言，单独复跑整个类 7/7 通过，结果保存在 `m39-338-kv-recheck.trx`。本条保留不稳定性事实，不把重跑通过当作该既有竞态已修复。

## 复现命令

从仓库根目录以 PowerShell 7 执行。SDK 是 `C:\Program Files\dotnet\dotnet.exe`（10.0.400）。本次使用归档内 `run.ps1` 限制单次运行最长 900 秒，关闭共享构建服务器、单进程构建；参数及精确运行命令也保存在各进程 JSON 中。

```powershell
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj --no-restore --disable-build-servers -m:1 -nr:false -p:UseSharedCompilation=false --logger trx
dotnet test tests/SonnetDB.Tests/SonnetDB.Tests.csproj --no-restore --disable-build-servers --filter 'FullyQualifiedName~SqlFrameEndpointTests|FullyQualifiedName~ServerOptionsTests' -m:1 -nr:false -p:UseSharedCompilation=false --logger trx
dotnet test tests/SonnetDB.CrashTests/SonnetDB.CrashTests.csproj --no-restore --disable-build-servers --filter 'FullyQualifiedName~crash_kill9_deferredTrigger|FullyQualifiedName~crash_kill9_outboxDelivery|FullyQualifiedName~crash_kill9_betweenTriggerTableCommits|FullyQualifiedName~crash_kill9_triggerCompletionBoundary' -m:1 -nr:false -p:UseSharedCompilation=false --logger trx
dotnet build src/SonnetDB/SonnetDB.csproj -c Release --no-restore --disable-build-servers -m:1 -nr:false -p:UseSharedCompilation=false
```

这些命令展示实际构建/测试参数；自动化执行时应使用归档中的有界 runner，并将其日志目录调整为本次独占目录。需要保留 `--no-restore` 前提下已恢复的依赖，不把构建缓存或二进制作为源码交付。
