# GH-Issue #396：EF Provider AOT/Trim 边界

## 探针范围

`eng/probes/issue396-ef-aot/` 保存一个最小 .NET 10 `DbContext` 消费者。它使用 `UseSonnetDB`、一个实体模型和一次模型读取，Provider 通过 `ProjectReference` 接入。探针不会修改 `src/`，不会把工作树 DLL 伪装成公开包，也不会把本地 EF 测试项目当作 ABP 应用验收。

`run.ps1` 的强制 analyzer 构建把 `IsAotCompatible=true`、`EnableTrimAnalyzer=true` 和 `EnableAotAnalyzer=true` 作为诊断输入，目的是观察当前源码在更严格分析设置下的编译边界。这个属性覆盖只作用于本次 MSBuild 进程，不能改变 Provider 项目自身的合同。

## 当前合同与状态

Provider 项目 `src/SonnetDB.EntityFrameworkCore/SonnetDB.EntityFrameworkCore.csproj` 显式声明 `IsAotCompatible=false`。因此本 Issue 当前的 Provider 合同是 `NOT_SUPPORTED`。即使最小 ProjectReference 消费者的强制 analyzer 构建为 `PASS`，也只能说明该构建入口没有在本轮产生阻断错误，不能把 Provider 标成 AOT 兼容。

本探针不含公开 NuGet 包消费，也不含 ABP BookStore、Passkey 或其它固定的真实应用快照。NuGet-only consumer、ABP consumer、NativeAOT publish 和 NativeAOT run 在没有对应输入和独立收据时保持 `NOT_RUN` 或 `UNKNOWN`。源码、CoreCLR 测试、普通 Release build 和公开包/真实应用/NativeAOT 运行是不同证据层，不能互相替代。

## 运行与收据

使用新的证据目录运行：

```powershell
& ./eng/probes/issue396-ef-aot/run.ps1 `
  -EvidenceRoot 'D:/sndb-t/issue396-ef-aot-<timestamp>'
```

收据格式由 `eng/probes/issue396-ef-aot/receipt.schema.json` 固定，至少记录源码文件 SHA-256、消费者类型、restore/build 退出码、warning/error 计数、PID/父进程链和以下独立状态：

| 门禁 | 当前结论 | 含义 |
| --- | --- | --- |
| Provider `IsAotCompatible` | `NOT_SUPPORTED` | 源码明确关闭 AOT 兼容声明。 |
| 强制 trim/AOT analyzer build | 按收据记录 `PASS`/`FAIL` | 只证明本地 ProjectReference 诊断构建。 |
| NativeAOT publish/run | `NOT_RUN`（默认） | 没有二进制发布、启动和运行时证据。 |
| NuGet-only consumer | `NOT_RUN` | 探针使用 ProjectReference，不是公开包。 |
| ABP consumer | `NOT_RUN` | 没有真实 ABP 应用或固定快照。 |

Issue 只有在真实匹配版本的公开包、最小 NuGet-only 消费者、ABP 应用（包括 compiled model/预编译查询适用路径）和实际 NativeAOT publish/run 各自取得证据后，才可重新评估；本探针本身不会关闭 Issue。

## 2026-11-15 本地复现

在 `main@fa96b1a3` 的共享工作树中，使用 PowerShell 7 和新的临时证据目录执行：

```powershell
& ./eng/probes/issue396-ef-aot/run.ps1 `
  -EvidenceRoot 'D:/sndb-t/issue396-ef-aot-20261115-run3'
```

结果为：

- restore：`PASS`，exit 0，5.89 秒。
- 强制 trim/AOT analyzer Release rebuild：`PASS`，exit 0，88.07 秒，0 error；保留 2 条唯一警告：`IL2026`（EF Core `DbContext` 构造函数的 `RequiresUnreferencedCode`）和 `IL3050`（同一路径的 `RequiresDynamicCode`）。构建使用 `-p:IsAotCompatible=true` 仅作为诊断输入，没有修改 Provider 项目。
- Provider 合同：`NOT_SUPPORTED`，因为源项目仍显式为 `IsAotCompatible=false`。
- NativeAOT publish/run：`NOT_RUN`；NuGet-only consumer：`NOT_RUN`；ABP consumer：`NOT_RUN`。

该结果保留了实际 analyzer 警告，未使用 pragma、`UnconditionalSuppressMessage` 或降低 analyzer 级别来制造通过。运行产生的临时 receipt、日志、`bin/` 和 `obj/` 在读取后按绝对路径精确清理；`receipt.schema.json` 与探针源码是可重复入口。
