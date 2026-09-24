# M42 覆盖索引读取切片

## 范围

关系表的普通二级索引现在可以在谓词、投影列均被同一索引覆盖时直接产生候选行。实现支持：

- 连续等值前缀（例如 `(status, rank, device_id)` 的 `status = 'ready'`）；
- 等值前缀后的 `INT64` 或 `DATETIME` 有符号范围，并保持跨零范围的逻辑顺序；
- 索引页分页、稳定读快照、取消传播和 SQL `secondary_index_only` 访问指标。

索引项中的列值按既有 Table V1 编码解码，主键后缀、列类型、NULL 标记和尾部字节都会校验。覆盖路径不读取或解码基表 payload；删除、事务 overlay、未覆盖列、残余谓词和 JSON path 索引继续使用回表路径。DECIMAL 列仍回表，因为已发布索引编码只保存 G29 文本，无法恢复原始 scale。

## 本地验收

使用 PowerShell 7 和隔离 artifacts 目录执行：

```text
dotnet test tests/SonnetDB.Core.Tests/SonnetDB.Core.Tests.csproj -c Release --no-restore --artifacts-path C:\Users\mysti\AppData\Local\Temp\sonnetdb-roadmap-20260923 --filter "FullyQualifiedName~TableCoveredIndexTests|FullyQualifiedName~RelationalCoveredIndexTests|FullyQualifiedName~RelationalInputPushdownTests|FullyQualifiedName~TableIndexEntryKeyLengthTests" -m:1 -p:BuildInParallel=false -p:UseSharedCompilation=false -nodeReuse:false --disable-build-servers
```

结果：42/42 通过，Core、Data、CLI、缓存扩展和测试项目均以 Release 编译。用例覆盖全标量编码、NULL/空 payload、损坏索引项、跨页稳定快照、取消、重开、跨零范围、事务 read-your-writes、未覆盖列回表及无基表解码的访问指标。该结果是本机代码合同证据，不替代固定 x64/ARM64、统一语料和 168 小时 M42 生产门禁。

2026-09-24 在最终工作树上复验仍为 42/42；与 SQL 预览、ADO 及命令超时回归合并运行共 87/87，0 失败、0 跳过，使用 Release、`/warnaserror`、单个 MSBuild worker 和 C 盘隔离 artifacts。原始 [Core TRX](../audits/roadmap-closure-evidence-20260924/core.trx)、[命令/PID](../audits/roadmap-closure-evidence-20260924/core-tests.process.json)和[分项计数](../audits/roadmap-closure-evidence-20260924/validation.json)已留存；临时构建目录不作为交付物。
