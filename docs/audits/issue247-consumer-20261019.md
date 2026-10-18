# Issue #247：真实 SqlSugar Reader 输出验收

固定 SqlSugar 源码为 `65e8226ce25a8e1cb9cca2590683f1fd6778cab3`，官方 ZIP SHA-256 为 `46615584F90E8E7857A91140130581F9E4C3D7E7AE20B48A9C9F05383633D766`。本次原版和候选均使用同一份 `SqlSugar.dll`（SHA-256 `990DDF0A71E0F0B25415E319451EF7110E08F35C2C981D02CC8E78F9853EAE8E`），候选只修改 SonnetDB provider，未修改 ORM 核心。

原 `ReaderOutputProbe.cs.txt` 的 23 个场景原样运行，原版在 embedded、REST、HTTP/2 均为 9 PASS / 14 FAIL；失败集中于 Reader 关闭时没有回写 SugarParameter。候选在三种协议均为 23 PASS / 0 FAIL / 0 SKIP，验证同步/异步、早关、Close/Dispose/DisposeAsync、取消后不公开输出、Size 失败回滚、原参数目标冻结、命令先释放及读取器关闭后复用。

候选在原生 Reader 成功关闭后才复制冻结的输出参数；读取、取消或关闭失败不复制，重复关闭不再次发布。补丁为 `eng/probes/issue247-stored-procedure/sqlsugar-reader-output.patch`，应用目录是固定源码中的 `Src/Asp.NetCore2/SqlSugar.SonnetDBCore`。为避免补丁上下文中的空行标记形成仓库尾随空白，使用零上下文补丁；应用时须指定 `git apply --unidiff-zero`。已独立执行检查和实际应用，并逐个核对补丁结果与运行候选源码的规范化字节一致。

实际 Kestrel HTTP/2 回执确认候选有 24 次 Procedure opcode 3 请求且全部使用 HTTP/2，没有 REST 过程回退。23 个场景中的 provider 复用场景执行两次调用，故请求数为 24。原版复用场景在第一轮输出断言失败，因此只有 23 次调用。首次网络验收工具误将候选也预期为 23 次，保留了该工具失败；按用例真实调用数修正后整体通过。

独立证据位于 `.codex-temp/issue247-consumer-20261019/acceptance.json`、`patch-application-receipt02.json`、原版/候选实际日志和 DLL 哈希；外置摘要为 `.codex-temp/sync-20261019/issue247-sugar-consumer-acceptance.json`，有界网络进程回执为 `issue247-real-sugar-network02.processes.json`。原版和候选的上游编译告警保留，这些结果不证明上游包审计、稳定 NuGet/Server 发行或 provider 补丁已发布。

根扩大执行 `Ado` 和 `Data` 两个命名空间及 CTAS 回归后，发现两项旧的 StoredProcedure 拒绝测试及两项裸 DECIMAL 元数据回归。前者改为标准存储过程实际执行与未知 CommandType 拒绝；后者修复生产推断，保留未知 precision/scale，不修改原元数据断言。原始 266/270 失败结果保留，修正后的 289/289、0 SKIP 见 `artifacts/pm-20261019/native-ado-fixed01/integrated-all-native-ado-fixed01.trx`。

此前 #247 子工作树清理同时移除了其树内原始 artifacts；不能再以已不存在的 worker TRX 作为可独立回读证据。本页采用根保留的真实远程 60/60、上述扩大 289/289 和重新执行的原版/候选消费者证据，未把报告中的历史数字当作可回读收据。
