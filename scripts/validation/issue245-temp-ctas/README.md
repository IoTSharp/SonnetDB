# #245 临时表 / CTAS / SqlSugar Fastest 验收探针

这是待实现合同的真实 ADO/SqlSugar 验收程序。当前源码会返回非零，不能将其中普通工作表的成功当作临时表支持。设计与当前证据见 `docs/audits/issue-245-temp-ctas-design-20261016.md`。

## 构建

使用 .NET 10。`EngineBin` 指向待测 SonnetDB.Core/Data DLL；`SugarBin` 指向真实 SqlSugar/SqlSugar.SonnetDBCore 和其依赖 DLL。不要将此探针的反射型 ORM 消费边界当作生产 NativeAOT 能力。

```powershell
dotnet build scripts/validation/issue245-temp-ctas/TempCtasProbe.csproj -c Release `
  --artifacts-path D:/validation/issue245/build `
  -p:EngineBin=D:/validation/issue245/engine `
  -p:SugarBin=D:/validation/issue245/sugar
```

各执行实例须使用独占数据库目录；NUGET_PACKAGES、HTTP/plugin cache、DOTNET_CLI_HOME、TEMP/TMP 放在独占 D 盘目录。本准备使用 `issue245-work/Invoke.ps1` 保存实际 Process 身份、参数、stdout/stderr、退出码、超时和时间，全部失败原样保留。

## 执行

```powershell
dotnet D:/validation/issue245/build/bin/TempCtasProbe/release/TempCtasProbe.dll native 'Data Source=D:/sndb-t/issue245-native' local
dotnet D:/validation/issue245/build/bin/TempCtasProbe/release/TempCtasProbe.dll sugar 'Data Source=D:/sndb-t/issue245-sugar' original-provider
dotnet D:/validation/issue245/build/bin/TempCtasProbe/release/TempCtasProbe.dll sugar-native 'Data Source=D:/sndb-t/issue245-native-provider' candidate-provider
```

`native` 有 30 项：3 个现有能力控制，27 个临时表/CTAS目标。涵盖同步异步、两连接同名、关闭重开、空/非空 CTAS、九种类型及 DECIMAL 精度、持久 CTAS 重开、不继承约束、名称大小写、创建/删除/行变更回滚、提交后保留、Reader 关闭及取消。目标语法拒绝时，后续断言没有执行。

`sugar` 有 14 项：10,000 行 BulkCopy 控制、同步异步 BulkUpdate/BulkMerge 的无事务/提交/回滚组合、引擎 CHECK 失败清理。成功集合路径必须是 10 个批量导入 INSERT + 一个 UPDATE FROM，合并额外一个 INSERT SELECT；调用方事务之外的连接观察提交前状态。程序通过 Fastest 的公开 Begin 日志事件重新开启 SQL 观察，不替换命令或执行器。

`sugar-native` 使用同一真实 Fastest 入口，并要求实际创建命令为原生临时 CTAS。附带的 provider patch 只是待合入上游的候选，**尚无完整候选 provider 的运行证据**；全 provider 严格构建的四条既有 nullable 诊断和单类零警告编译分别保存。

## 真实远程

```powershell
pwsh -File scripts/validation/issue245-temp-ctas/Remote.ps1 `
  -EvidenceDirectory D:/validation/issue245/remote `
  -ServerDirectory D:/validation/issue245/server `
  -ServerSourceCommit '<实际同源提交>' `
  -ConsumerAssembly D:/validation/issue245/build/bin/TempCtasProbe/release/TempCtasProbe.dll `
  -RuntimeDirectory D:/sndb-t/issue245-remote
```

运行器只启动并结束自己创建的 Process。按 REST 与 HTTP/2-only Kestrel 分别运行 Native 和 SqlSugar，保存完整 Server 文件 SHA、进程身份与实际请求协议。令牌只在进程内存/环境中传递，不写入证据。负基线不会因第一个失败跳过后续消费者；任何失败最终退出 1。`-SugarMode sugar-native` 可选择原生候选。

真实 HTTP/2 不代表每条 SQL 走 Frame；当前普通 SELECT 有 Frame 请求，事务会话与不支持语句仍走 REST。进程终止只是宿主生命周期管理，不是本项临时对象或持久 CTAS 故障恢复证据。
