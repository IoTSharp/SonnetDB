# #247 存储过程准备探针

状态：实施前正向验收草稿，当前源码实际失败。细节见 [准备核查报告](../../../docs/audits/issue-247-stored-procedure-preparation-20261016.md)。

`StoredProcedureProbe.csproj` 从 `RuntimeDirectory` 引用固定完整运行时，使用真实 `SndbCommand` 与真实 SqlSugar `UseStoredProcedure`/`SugarParameter.Direction`。不创建替代 ORM。独立 Directory.Build/Packages/NuGet 文件隔离主 solution 的项目属性；没有关闭 NuGet 审计或降低告警。

```powershell
dotnet build ./StoredProcedureProbe.csproj -c Release --artifacts-path D:/your-new-build -p:RuntimeDirectory=D:/your-frozen-runtime -m:1 -p:UseSharedCompilation=false -nodeReuse:false
dotnet D:/your-new-build/bin/StoredProcedureProbe/release/StoredProcedureProbe.dll 'Data Source=D:/your-fresh-db' baseline native
dotnet D:/your-new-build/bin/StoredProcedureProbe/release/StoredProcedureProbe.dll 'Data Source=D:/your-other-fresh-db' baseline sqlsugar
```

每次运行使用新数据库目录；native 与 sqlsugar 模式分别是 15 与 11 例，控制 CALL 的通过不代表标准过程入口通过。每个 FAIL 会打印异常与实际原因，最终有失败则 exit 1。启动时输出实际 Core/Data/SqlSugar/适配器的 informational version 与 SHA256。

`RemoteBaseline.ps1` 需要完整 Server 目录、消费者目录及新的证据/运行目录。它只终止本次拥有句柄的进程，保存每次请求、协议、DLL 清单及原始日志。它通过公开 HTTP SQL 接口预建 fixture，以隔离当前远程 ADO 会误绑过程体 `@参数` 的缺陷；该缺陷仍各有单独失败用例，所以远程 native/sqlsugar 分别是 16/12 例。标准 setter 拒绝不记为到达 Server。

远程子进程通过 `ISSUE247_CONNECTION` 接收临时连接信息，命令行只传 `--connection-env`；`ISSUE247_EXTERNAL_FIXTURE=1` 仅启用明确标注的预建 fixture。不得将此选项描述为远程 ADO 建过程成功。脚本有功能失败仍返回 1，`harnessCompleted=true` 只表示收集完整。

准备文件没有接入生产实现；不要把两条控制 PASS 或脚本成功运行提升为 #247 验收通过。正式包、Server、上游 SqlSugar 的交付另行核对。


2026-10-17 人工整合：24 项正向合同草稿的完整源文（仅统一 LF 行尾）保存在本目录 `StoredProcedureCommandContractTests.cs.txt`，未加入主 solution。准备阶段实际结果仍为 2 PASS /22 FAIL；本次只移动存档位置并统一 LF 行尾，未删除/跳过断言，不能当作当前主线功能已完成。原独立工作树和失败 TRX 保留。后续实现时可显式编译该源文件并运行正向合同。
