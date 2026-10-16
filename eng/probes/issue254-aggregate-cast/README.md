# #254 聚合 CAST 公开包与 Chloe 消费者

这组工具验证既有聚合 CAST 实现。它不修改引擎生产代码，也不代表正式发行或 Chloe 上游交付。

- 原生和 Chloe 消费者直接锁定 NuGet.org 的 `SonnetDB`、`SonnetDB.Core` `4.5.0-preview.ef266.5`，没有 SonnetDB `ProjectReference`。
- Chloe 固定 PR435 的 `bb7ec8735bd4511c35453eb009f70cc606795703`。候选补丁只删除 `SqlGenerator.VisitConvert` 中拒绝聚合 CAST 的三行保护代码。
- 原 provider 每协议 4 PASS / 12 guard FAIL；候选每协议 16 PASS。两组消费者、Chloe、公开 Core/Data DLL 相同，仅 provider DLL 不同。
- 原生公开 embedded 为 13 PASS / 1 FAIL。失败是 `concat('n=', CAST(COUNT(*) AS STRING))` 的类型元数据仍为 `Object`，属于 #261 已修的相邻合同；它不是聚合 CAST 的值计算失败。公开客户端连接本轮源码 Server 的 REST/h2c 各 14 PASS；同消费者换用本轮源码 Core/Data 的 embedded 14 PASS。
- 真实传输必须保留 Server 请求日志；`Record-Evidence.ps1` 核对 HTTP/1.1 SQL 与 HTTP/2 `/v1/frame` 请求、固定包资产、运行 DLL SHA 和失败原因。

本次原始产物在 `D:/source/SonnetDB/artifacts/issue-supervision-20261016/issue254-work`，数据库与 TEMP 在独占 `D:/sndb-t/i254-*`。有界命令器为本轮固定目录配置；迁移到其他目录时同步修改其缓存路径。每次执行必须使用新的 `Label`，不能覆盖旧失败回执。

复现准备：将本目录两个项目和各自 `.cs.txt` 分别复制到工作目录 `native-source`、`chloe-consumer-source`；三个 `Directory.*`、`NuGet.Config` 和两个执行脚本放在工作目录。三个隔离属性文件防止外部 Chloe 源码继承 SonnetDB 仓库的构建设置。

1. 在固定 SHA 的 Chloe 原始源码上构建 provider：

   ```powershell
   dotnet build chloe-source/src/Chloe.SonnetDB/Chloe.SonnetDB.csproj -c Release -m:1 -p:TargetFrameworks=net10.0 -p:NuGetAudit=false -p:SourceRevisionId=bb7ec8735bd4511c35453eb009f70cc606795703 /nodeReuse:false
   ```

   这是外部源码构建：原始 Chloe 有 38 个现存警告，保留日志，不压制。显式 `SourceRevisionId` 防止归档源码继承外层仓库 revision；本轮初次未指定的记录也已保留。

2. 保存原 provider 目录，构建两个消费者，Chloe 项目用 `-p:ChloeBin=<原provider输出目录>`。编译输出保留所有依赖，不能用仓库 Core/Data 替换公开组 DLL。
3. 对一份干净源码执行 `git apply --check` 和 `git apply chloe-aggregate-cast.patch`，仍使用相同 revision 参数重建。复制一份原消费者输出，仅替换 `Chloe.SonnetDB.dll` 为候选。补丁保留上游 CRLF；本轮实际 apply 后源码 SHA 与编译候选一致。
4. 在 SonnetDB 独立工作树正常构建 `src/SonnetDB/SonnetDB.csproj -c Release`，保留 AOT 分析。以下示例中的 `$taskServer` 为这个源码 Server 的绝对 DLL 路径，不能称为发布版 Server。

   ```powershell
   ./Invoke-ConsumerMatrix.ps1 -WorkRoot $taskWork -ConsumerDirectory $taskNative -ConsumerAssembly AggregateCastNative.dll -ServerDll $taskServer -Label native-public-final -TotalCases 14 -ExpectedEmbeddedFailures 1
   ./Invoke-ConsumerMatrix.ps1 -WorkRoot $taskWork -ConsumerDirectory $taskOriginal -ConsumerAssembly AggregateCastChloe.dll -ServerDll $taskServer -Label chloe-original-final -TotalCases 16 -ExpectedFailures 12
   ./Invoke-ConsumerMatrix.ps1 -WorkRoot $taskWork -ConsumerDirectory $taskCandidate -ConsumerAssembly AggregateCastChloe.dll -ServerDll $taskServer -Label chloe-candidate-final -TotalCases 16
   ```

   预期失败仅用于保留原始公开缺口和 provider guard，不应读作成功的功能用例。`Record-Evidence.ps1` 还会逐条验证失败原因。矩阵通过随机端口和独占数据库启动真实 Kestrel，结束后按 PID/start-time 验证并停止自有 Server。

5. 运行现有源码测试 `SqlAggregateCastTests`（10 项）和 `AggregateCast_ValuesNullMetadataAndOverflow_UseRealTransport`（REST/h2c 2 项）。依赖还原可用进程代理，执行本地 h2c 测试时不要把代理带入测试进程。本轮带代理的初次 1/2 和去掉代理后同 DLL 的 2/2 均留存。
6. `Record-Evidence.ps1` 按本轮固定目录名验证并生成审计 JSON。发布验收仍需正式稳定包、正式 Server 与 Chloe 上游交付后的真实消费者复验。

消费者为独立诊断程序，不进入解决方案生产发布；没有新增反射 JSON、NuGet 运行时依赖或磁盘格式变更。
