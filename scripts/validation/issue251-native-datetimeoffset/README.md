# #251 原生 DateTimeOffset 消费验收

本目录是源码候选验证工具。公开 4.0.0 / preview `.5` 的负基线另在 `docs/audits/issue-251-datetimeoffset-probe`，不得用本候选覆盖。

1. 独立 clone Chloe commit `bb7ec8735bd4511c35453eb009f70cc606795703`。
2. 先应用 `eng/probes/issue262-datetime-consumer/chloe-native-datetime.patch`，再应用相邻 `issue252-native-duration/chloe-timespan.patch`，最后应用本目录 `chloe-datetimeoffset.patch`。后者只包含三个文件的 +3/-2，不修改框架默认转换器；最终候选同时保留已有 DateTime/TimeSpan 修正。
3. 使用 .NET 10 Release 构建 Chloe.SonnetDB，然后构建本目录 `OffsetConsumer.csproj`。传入 `ChloeBin`、`EngineBin`（新 Core/Data DLL）和 `DependencyBin`（System.IO.Hashing/System.Numerics.Tensors 所在目录）。使用独立 artifacts、NuGet 和 TEMP。
4. 以两个独立进程运行 `OffsetConsumer.dll exercise <新数据库目录>` 和 `OffsetConsumer.dll reopen <同一目录>`。真实 Chloe 执行 DDL、普通/批量/异步写入、事务提交/回滚、实体更新、参数比较、标量/对象/nullable 物化；检查完整 ticks、UTC、不同 offset、无 offset 文本、NULL 和空结果。
5. `Remote.ps1` 启动给定源码 Server，分别以 REST 和显式 h2c Frame 执行 exercise/reopen，重开前强制终止并重新启动该脚本自己的 Server 子进程。它保存运行二进制 SHA、实际请求协议、命令、进程身份和 stdout/stderr。
6. 新客户端对真实旧 v5 Server 的兼容验证传 `-ProbeMode old-datetime`。该模式仅使用显式 `DbType.DateTime/DateTime2` 的 DTO 参数，检查旧 DATETIME 返回 DateTime 及毫秒语义；不能作为原生类型验收。
7. `CatalogFloorProbe.csproj` 分别绑定新 Core 与冻结的旧 Core 构建两个程序。依次以新程序执行 `create`、旧程序执行 `reject`、新程序执行 `reopen`，传同一新建数据库目录；比较旧引擎运行前后的全部持久文件 SHA，确认 v15 拒绝没有改写目录。

远程调用示例：

```powershell
pwsh -NoProfile -File ./Remote.ps1 `
  -EvidenceDirectory D:/evidence/offset-remote `
  -ServerDirectory D:/build/server `
  -ServerSourceCommit <已核对的源码提交或候选清单SHA> `
  -ProbeAssembly D:/build/consumer/OffsetConsumer.dll `
  -RuntimeDirectory D:/sndb-t/offset-remote
```

使用全新证据与运行目录。该脚本不会发布包、修改上游仓库或关闭 Issue；正式发行与上游集成须独立核验。
