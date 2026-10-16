# #260：完整 UInt64 字符串解析

本项源码实现、原生回归和真实 Chloe 本地候选消费已通过。最终证据已冻结；公开 `4.5.0-preview.ef266.5` 和 Chloe 正式发布状态未发生变化。

## 原问题与实现

在基线 `285186b868a59d4b22e47279ea51deb38a3a1f45` 的独立探针中，`CAST('2.9' AS DECIMAL)` 返回 Decimal `2.9`，随后的 `Convert.ToUInt64` 得到 `3`；`CAST('18446744073709551615' AS INT)` 拒绝合法 UInt64 最大值。基线尚未注册 `parse_uint64`。原始 stdout 保留了部分中文异常文本的编码替代字符，类型、数值和原日志没有被修写。

新增 `parse_uint64(text)`，将完整 `0..18446744073709551615` 解析为精确 `DECIMAL(20,0)` / `System.Decimal`，不经过 Double 或有符号 Int64。手动有界扫描允许首尾 ASCII 空白、一个前导加号、ASCII 十进制数字和预算内前导零；所有负号（含 `-0`）、小数（含 `2.0`）、指数、分组、Unicode 数字/空白、尾随 NUL、超范围及超过 1024 UTF-16 代码单元均拒绝。非字符串不隐式转换，NULL/DBNull 传播为 NULL，错误不回显输入文本。函数只调用输入表达式一次，并检查查询取消。

空结果、全 NULL、关系 JOIN/CTE/视图也声明 Decimal、precision 20、scale 0；可空性来自输入，主键/自增/ROWVERSION 属性不外泄。同名用户函数覆盖时不伪造原生声明。函数复用已有 Decimal 存储和传输，不新增二进制格式、持久值类型或协议值类型。解析后的通用 Decimal 算术仍可超过 UInt64 范围。

这是明确限定格式的 SQL 合同，不宣称完全复制 CLR `UInt64.Parse` 的区域、`NumberStyles`、超长文本或 NULL 行为；详细语法与适配边界见 [函数说明](../sql-uint64-parse.md)。浮点/Decimal 转 UInt64、一般 UInt64 参数绑定和 #259 按位运算不在本项范围。

## 最终源码与验证

工作树：`D:/source/SonnetDB-worktrees/issue260-uint64-parse`；基础提交为上述 `285186b8`。共享日期与 concat 元数据依赖按 #262 → #261 → #260 顺序接收，最终依赖和源码 SHA256 由同名 JSON 逐项记录；这些增量不是基线提交本身已经包含的内容。

证据根：`D:/source/SonnetDB/artifacts/issue-supervision-20261016/issue260-work`。所有 NuGet 缓存、构建和运行证据位于 D 盘；运行数据库和 TEMP 使用独占短目录。命令收据记录真实参数、PID、启动/结束时刻、退出码及超时，未将含令牌连接串写入命令或日志。

| 最终验证 | 结果 |
| --- | --- |
| Core Release 构建与本项测试 | 0 警告、0 错误；50/50，零失败/跳过 |
| Core 相邻函数/Decimal/CAST/元数据及接收的日期/concat 回归 | 318/318，零失败/跳过 |
| Server Release 构建与三协议同步/异步本项测试 | 0 警告、0 错误；6/6，零失败/跳过 |
| 远程 Decimal/元数据回归 | 8/8，零失败/跳过 |
| 真实 Chloe 候选 Release 增量构建 | 0 警告、0 错误；既有完整构建警告另列 |
| 真实 Chloe 候选三协议 exercise/reopen | 6/6 独立消费者进程 exit 0；每协议 22 + 8 断言 |

本项测试覆盖 UInt64 最大值、2^63、0、低位精度、NULL/DBNull、常量/参数/列、合法/非法格式、参数对象不变、空结果声明、后续行失败的 INSERT/UPDATE 原子性、事务回滚及重开。随机固定种子的 256 个完整 64 位样本归于一个测试，不另计 256 个测试。h2c 只读查询以实际 `/v1/frame` 请求验证；写入和事务使用 HTTP/2 REST，不称为 Frame 写入。

最终命令为 `core-build-final01`、`core-final01`、`core-adjacent-final01`、`remote-build-final01`、`remote-final01`、`remote-adjacent-final01`、`chloe-build-final01`、`chloe-embedded-exercise-final01`、`chloe-embedded-reopen-final01` 和 `chloe-remote-final01`；每项在证据根有 JSON 参数规格及 `logs/` 运行收据。四份最终 TRX 位于 `results/`。`source-before-final01.json` 固定 25 份实际参与构建的本项文件、候选及接收依赖；最终再次逐项核验未变化。完整映射及 SHA256 见 [JSON 清单](issue-260-implementation-20261016.json)。

## 真实 Chloe 候选

固定实际仓库 `maikebing/Chloe` PR #435 头 `bb7ec8735bd4511c35453eb009f70cc606795703`。未打补丁的原处理器实际抛出指向 #260 的 `NotSupportedException`：保护观察 1/1 通过，不属于功能可用的证据。

[候选目录](issue-260-chloe-candidate/README.md) 的补丁仅将既有单字符串 `ulong.Parse` 翻译为 `parse_uint64(...)`。真实查询生成：

```sql
SELECT parse_uint64("T"."Text") AS "C"
FROM "parsed_consumer" AS "T" ORDER BY "T"."Id" ASC
```

实际消费包括同步/异步可空 UInt64 投影、最大值谓词、原生 SqlQuery 物化、实际 ORM UPDATE、非法格式拒绝、后续行失败原子性、事务回滚和独立进程重开。区域和样式重载保留明确拒绝。SQL NULL 可空物化不同于 CLR `ulong.Parse(null)`，未把这一差异写成等价实现。

REST/h2c 分别启动两个独立 Server 进程；先运行 `exercise`，对该次自有 Process 执行终止，再以相同数据目录启动新 Server 运行 `reopen`，最后终止新进程。此处是进程终止恢复，不声称优雅关闭。四份 Server 身份收据均记录 `exited=true`；六个消费者均自然退出 0。实际协议证据如下：

| 协议/阶段 | 数据请求数 | 其中 Frame 请求 | 消费断言 |
| --- | ---: | ---: | ---: |
| 嵌入式 exercise / reopen | 不适用 | 不适用 | 22 / 8 |
| REST HTTP/1.1 exercise / reopen | 40 / 5 | 0 / 0 | 22 / 8 |
| h2c HTTP/2 exercise / reopen | 40 / 5 | 17 / 5 | 22 / 8 |

请求次数、断言数和 xUnit 测试数分别记录，不混为测试总数。远程证据在 `chloe-remote-final01/`，包含完整冻结 Server 文件哈希、实际请求日志、协议收据和独占运行目录。测试库和消费者均来自当前源码；同名 JSON 将每个进程实际加载的 Core/Data/Chloe 二进制与原始保护版本分开绑定。

候选以 SonnetDB 新源码项目引用构建，不使用公开 `.5` 包冒充新函数。Chloe 原始完整构建有 38 条既有 CS8632 警告、0 错误；增量候选构建零警告不能证明这些既有警告已修复。补丁只保留在本地审计目录，未提交到外部 Chloe PR。

## 保留的先前运行

- `baseline-build` 首次失败：临时探针有两处可空诊断 CS8600/CS8602；修正后 `baseline-build-corrected` 零警告/错误并运行原始复现。
- `remote-build01` 首次失败：新工作树未初始化仓库固定的 CoAP/MQTT 子模块，1 警告、132 错误。按固定提交初始化后 `remote-build02` 零警告/错误；没有更改子模块版本。
- 早期 `core-values01` 为 38/38，仅覆盖当时筛选出的值语义测试，没有验证最终元数据，也不等同于后来新增的完整测试集合。
- 早期 `chloe-embedded-exercise01` 为单嵌入式进程 22 断言，不能替代最终三协议重开矩阵。

早期日志、失败、原始保护二进制及基线探针均保留。后续最终结果与这些重叠运行不相加。

## 交付边界

提交前的全解决方案 restore/原 Format Check 由根代理在最终集成工作树执行，本子任务不自行提交、推送或外部回复。`git diff --check` 已通过，但不能代替该完整门禁。正式 Core/Data/Server 包和 Chloe 正式适配仍待交付；源码构建、本地候选及其进程验证不能写成正式发布验收。建议 #260 暂保持 OPEN 跟踪正式交付，由根代理按当前用户授权回复并核对关闭条件。

## 主树复审

根按SHA集成13文件，复核182证据文件、16关键二进制、25源码/依赖、105 Server快照文件、四份TRX与六个候选进程。主树两个Release构建0警告/错误，Core258/258、真实Server120/120，零失败/跳过；与代理重叠集合不累计。审查收据位于artifacts/issue-supervision-20261016/issue260/root-review，完整提交门禁位于issue260/final，实际成功后独立提交；正式发行/Chloe上游条件未满足。
