# #254：聚合 CAST 公开包与真实 Chloe 验证

核查日期：2026-10-16（Asia/Shanghai）。本项只补现有实现的公开消费者证据与可复现工具，没有修改 SonnetDB 生产代码。正式稳定发行、正式 Server 二进制和 Chloe 上游交付尚未验证，Issue 应保持 **OPEN**。

工作树 `D:/source/SonnetDB-worktrees/issue254-public-aggregate-cast`，分支 `codex/issue254-public-aggregate-cast`，源码基线 `0a51102cf4e1b203565d6b02af7d5fb4c73513a8`。公开包固定 `4.5.0-preview.ef266.5`，其 nuspec 和 DLL informational version 指向 `1af23d8f299febfdf66b040ab3419980396acf46`。真实 Chloe PR435 本轮在线回读仍 OPEN，head `bb7ec8735bd4511c35453eb009f70cc606795703`。

## 已有实现与本轮范围

`RelationalSelectExecutor.InlineAggregates` 已递归处理 `CastExpression`，聚合先按全体输入或每组计算，再做目标类型转换；精确 Decimal 字面量替换路径已经存在。已有 `SqlAggregateCastTests` 和真实传输测试覆盖历史 #254 修复。本轮未重写共享执行器、未把已有实现记为新增功能，也没有混入正在独立实施的 #256 分页改动。

公开消费者项目没有 SonnetDB `ProjectReference`；直接锁定 NuGet.org 两个包。真实 Chloe 使用固定源码、真正的 `SonnetDBContext` / `IQuery` / `Sql` 生成 SQL 并物化结果，不在客户端替代聚合计算。候选只有一个文件的三行删除：解除 `VisitConvert` 对聚合 CAST 的显式拒绝，保留所有其它类型限制和普通聚合处理器。原版与候选运行目录中的消费者、Chloe、公开 Core/Data DLL SHA 相同，仅 `Chloe.SonnetDB.dll` 不同。

## 本轮结果

| 证据层 | embedded | REST | h2c |
|---|---:|---:|---:|
| 原生公开包消费者 | 13 PASS / 1 FAIL | 14/14 | 14/14 |
| Chloe 原 provider + 公开包 | 4 PASS / 12 guard FAIL | 4 PASS / 12 guard FAIL | 4 PASS / 12 guard FAIL |
| Chloe 最小本地候选 + 公开包 | 16/16 | 16/16 | 16/16 |
| 同原生消费者换用本轮源码 Core/Data | 14/14 | 未重复 | 未重复 |

表中远程两列都使用**公开客户端连接源码 Server**，不能算成官方发布 Server 验收。候选 Chloe 共 48 项场景通过，原 provider 36 次 guard 拒绝并未被写成通过。各组与源码测试有覆盖重叠，不累加成唯一测试总数。

公开 embedded 的唯一失败为扩展分组投影 `concat('n=', CAST(COUNT(*) AS STRING))` 的 `GetFieldType` 返回 `Object`，不是预期 `String`。公开原始失败保留；该相邻函数声明合同已由 #261 的 `b157dddc` 修复，本工作树包含该修复。源码 embedded 补验及源码 Server 两协议均为 `String`，14/14；不能把这个依赖写成公开包已经修复。本轮没有为避开失败更改 SQL 或放宽原断言。

覆盖 SUM / AVG / MIN / MAX / COUNT，空输入与全 NULL，分组与 CAST HAVING、空组元数据、COUNT DISTINCT、CASE/COALESCE 嵌套、异步物化、Decimal 精确小数和阶段性溢出。原生消费者在 `Read()` 前核对类型及聚合 nullable 声明，并验证整数形状的 Double 值仍通过 `GetDouble` 与 `GetFieldValue<double>` 返回 Double。

- `9007199254740993.125 + 0.875` 精确得到 `9007199254740994m`，平均值 `4503599627370497m`；Decimal MAX 转 INT 保留 `9007199254740993L`。
- `CAST(SUM(INT) AS DECIMAL)` 仍在 Int64 累加阶段溢出；`SUM(CAST(value AS DECIMAL))` 精确得到 `9223372036854775808m`；再外转 INT 则在 CAST 阶段拒绝。
- Decimal.MaxValue 的 MAX 保持精确，而 MAX+1 的 SUM 在 Decimal 聚合阶段报错。溢出后连接仍可查询。
- Chloe 原普通聚合、Decimal 和分组通过；显式五聚合 CAST、空/NULL、CAST HAVING、精确 Decimal→INT、异步和外层溢出均被原 guard 拒绝。仅删除 guard 后全部通过，生成 SQL 和异常原文可查。

本轮现有源码测试实际执行：Core **10/10**，真实 REST/h2c **2/2**，无跳过。首次真实传输测试将用于 restore 的网络代理带入测试，结果为 REST 通过、h2c 建连失败 **1/2**；去掉代理后同 DLL 通过 **2/2**。两份 TRX 保留，不将初次失败改写为成功。

源码 Server 在默认 AOT/trim 分析开启下正常 Release 构建，0 警告、0 错误；两个公开消费者构建也为 0/0。外部 Chloe 原始构建有 38 个现存警告，未抑制；候选增量 provider 构建为 0/0，这不表示整个 Chloe 上游源码零警告。归档源码初次构建继承外层 SonnetDB revision，最终显式固定 `SourceRevisionId=bb7ec873...` 并重新保存原版/候选运行目录；初次日志也保留。

## 证据与继续顺序

原始日志、命令回执、包缓存、来源快照、TRX 和独立消费者输出在 `D:/source/SonnetDB/artifacts/issue-supervision-20261016/issue254-work`。短 TEMP/数据库使用 `D:/sndb-t/i254-*`；按自身 PID/start-time 结束三个矩阵 Server。最终日志有实际 HTTP/1.1 SQL 与 HTTP/2 `/v1/frame` 请求，审计脚本已逐组核对。

机器清单见同目录 `issue-254-public-aggregate-cast-20261016.json`：9 份最终公开消费者进程结果、12 份运行程序集 SHA、154 份证据文件及源码补验；补丁在独立 Git 目录实际 `apply --check` / `apply`，应用后源码 SHA 与编译候选一致。首次错误仓库名的 GH 查询和修正后的 PR435 实时回读也保留。

可复现工具位于 `eng/probes/issue254-aggregate-cast/`。JSON/Markdown 使用 UTF-8 无 BOM 和 LF；上游补丁保留原 CRLF，通过局部 `.gitattributes` 避免转换。提交前完整 solution restore/Format Check、HANDOFF/CHANGELOG 更新、独立提交与批次推送由监督主智能体在集成后的最终工作树执行；本子任务未提交、推送或对外回复。

继续顺序：监督主智能体复核并集成工具/报告 → 最终提交门禁 → 批次推送和 CI 回读 → 回复 Issue 说明现有实现和真实候选证据 → 等正式稳定包、正式 Server 与 Chloe 上游交付后复验，再判断关闭。源码与本地候选通过不能代替这些剩余条件。
