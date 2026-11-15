# Issue #396：EF Provider AOT/Trim 边界探针

本目录提供一个最小的 .NET 10 EF Core 消费者，用来把 EF Provider 的 AOT/Trim 边界记录成可复核收据。`Program.cs` 只创建 `DbContextOptions`、调用 `UseSonnetDB`、建立一个最小模型并读取 Provider 名称；它不启动 Server、不写数据库，也不模拟 ABP 应用。

探针使用 `ProjectReference` 指向当前仓库的 `src/SonnetDB.EntityFrameworkCore`。这能验证源码消费者能够编译，并在强制打开 trim/AOT analyzer 时收集诊断；它不能代表公开 NuGet 包消费。Provider 项目当前明确声明 `IsAotCompatible=false`，因此收据中的 `providerContract` 必须是 `NOT_SUPPORTED`，即使强制 analyzer 的构建返回 0 也不会改变这个状态。

从仓库根目录使用一个新的证据目录运行：

```powershell
& ./eng/probes/issue396-ef-aot/run.ps1 `
  -EvidenceRoot 'D:/sndb-t/issue396-ef-aot-20261115'
```

`run.ps1` 使用 PowerShell 7，restore/build 每个最多 600 秒，记录启动 PID、启动时间、命令行和父进程链，并在超时或失败后只回收本次启动的进程树。它会执行隔离 NuGet restore 和 Release build；build 使用 `IsAotCompatible=true`、`EnableTrimAnalyzer=true`、`EnableAotAnalyzer=true` 作为强制诊断实验，不修改 Provider 项目。结果和 stdout/stderr 保存在传入的证据目录，收据结构由 [receipt.schema.json](receipt.schema.json) 固定。

默认不会执行 NativeAOT publish/run。`-RunNativeAot` 只在有独立时间预算和新证据目录时使用；即使 publish 成功，也只能关闭 publish 这一层，仍不能替代运行时、NuGet-only、ABP 官方应用和发布包验收。

该探针的固定边界如下：

- `providerContract=NOT_SUPPORTED`：源码明确关闭 `IsAotCompatible`。
- `forcedAnalyzerBuild=PASS`：只表示本地 ProjectReference 消费者在强制 analyzer 下构建完成；不等于 Provider AOT 兼容。
- `nugetOnlyConsumer=NOT_RUN`：本目录没有公开 `SonnetDB.EntityFrameworkCore` 包来源和独立包缓存消费。
- `abpConsumer=NOT_RUN`：没有 ABP BookStore、Passkey 或其它真实 ABP 应用快照。
- `nativeAotRun=NOT_RUN`：默认不发布或运行 NativeAOT 二进制。
