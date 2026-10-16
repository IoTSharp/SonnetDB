# #249 原生与真实 SqlSugar 消费者

此消费者直接调用 `SqlFunc.JsonField`、`SqlFunc.JsonIndex`、
`SqlFunc.JsonListObjectAny`，同时运行原生 SQL 静态控制和动态 JSON 合同。
支持旧版本负向基线和新实现的初次运行、重开验证。

准备两个绝对路径：`EngineBin` 含待验证的 SonnetDB.Core/Data DLL，`SugarBin` 含
真实 SqlSugar 及 SonnetDB provider 的构建输出和依赖。每次运行打印实际程序集版本、
SHA256、位置、SQL 和参数 CLR 类型。不要把新源码 Server 或未发行 provider 标为正式包。

```powershell
$Validation = 'D:/source/SonnetDB/scripts/validation/issue249-dynamic-json'
$EngineBin = 'D:/validation/public4'
$SugarBin = 'D:/validation/sqlsugar-original'
$Output = 'D:/validation/issue249-public4'
dotnet build (Join-Path $Validation 'DynamicJsonProbe.csproj') -c Release --artifacts-path $Output "-p:EngineBin=$EngineBin" "-p:SugarBin=$SugarBin"
$Probe = Join-Path $Output 'bin/DynamicJsonProbe/release/DynamicJsonProbe.dll'
dotnet $Probe baseline 'Data Source=D:/sndb-t/i249-public4-new' public4
```

每次必须使用独占新数据库目录。`baseline` 模式要求 4 个静态控制通过和 11 个已知
JSON 缺口拒绝，成功时输出 `passed=4|expectedRejections=11|failed=0`；其它异常和
断言失败不会变成成功。`candidate` 模式要求全部 28 个检查通过，拒绝均为失败。
`reopen` 模式复用已创建的数据库，重复同样 28 个检查；不会重建表或重新插入数据。
连接字符串也可使用已创建空数据库的 REST/HTTP/2 地址；需另外记录服务身份、版本与
实际协议，这个单进程消费者自身不证明网络协议、重开或正式发行已完成。

本次三组真实基线均通过上述负向验收：正式 4.0.0、预览 4.5.0-preview.ef266.5、
冻结旧源码 f9ee14b7；SqlSugar 源码固定在 65e8226ce25a8e1cb9cca2590683f1fd6778cab3。
原始日志在工作区 `artifacts/issue-supervision-20261016/issue249-work/logs/` 的
`probe-public4-run02`、`probe-preview-run02`、`probe-source-run02`。

首次消费者使用 `First()` 触发旧包不支持的自动 ORDER BY，失败日志 `run01` 保留；
当前使用真实 `ToList().Single()` / `ToListAsync()` 入口隔离 JSON 需求，没有修改 provider。
新实现的嵌入式初次运行及重开示例：

```powershell
dotnet $Probe candidate 'Data Source=D:/sndb-t/i249-candidate-new' embedded
dotnet $Probe reopen 'Data Source=D:/sndb-t/i249-candidate-new' embedded-reopen
```

真实 REST/HTTP/2 宿主测试：

```powershell
& (Join-Path $Validation 'Remote.ps1') -EvidenceDirectory 'D:/validation/i249-remote-new' `
  -ServerDirectory 'D:/validation/server' -ServerSourceCommit '<实际提交及工作树状态>' `
  -ConsumerAssembly $Probe -RuntimeDirectory 'D:/sndb-t/i249-remote-new'
```

脚本复制并逐份核验 Server 文件哈希，使用随机 loopback 端口及一次性认证令牌，
分别验证 REST 与 HTTP/2，每个协议在宿主强制终止后重开数据库并重复验证。
令牌只通过进程环境传递，消费者接收 `env:ISSUE249_CONNECTION`，不把令牌写入参数日志。
每阶段检查 Server 的实际请求协议及 Frame 请求；保留进程身份、退出码和原始日志。
当前本地候选三协议的初次/重开均为 28/28，不等同于正式包或 SqlSugar 上游已发布。

辅助组件、SQL 接线及失败保留情况见
`docs/audits/issue-249-dynamic-json-design-20261016.md`。
