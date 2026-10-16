# 原生 DURATION 与 TimeSpan（#252）

`DURATION` 表示有符号时长，存储 CLR `TimeSpan.Ticks`：100ns 精度、完整
`Int64.MinValue` 至 `Int64.MaxValue` 范围。零、负值、超过 24 小时及 NULL 均有效。
`TIME` 仍表示一天内的 `TimeOnly` 钟面值。

```sql
CREATE TABLE runs(id INT, elapsed DURATION NULL, PRIMARY KEY(id));
INSERT INTO runs(id,elapsed) VALUES(1,CAST(972000000001 AS DURATION));
SELECT elapsed FROM runs WHERE elapsed > CAST('1.00:00:00' AS DURATION);
```

整数 CAST 输入按 ticks 解释；文本只接受 invariant `c` 格式，不接受月份等日历单位。
浮点输入不能隐式取整。时长加减与取负使用检查算术，越界报错；NULL 保持传播。
`DateTime - DateTime` 返回原生时长，按已经求值的两个 CLR 钟面 ticks 相减。
运行时 CAST/函数保留 100ns；现有 DATETIME 列及日期参数仍按 Unix 毫秒持久化/绑定，
本变更没有改变 #236 的存储精度边界。

## ADO.NET

默认 `SndbParameter` 的 `DbType.Object` 加 `TimeSpan` 值绑定为 DURATION。
声明元数据、`GetFieldType`、`GetColumnSchema`、`GetSchemaTable` 和读取值均使用
`System.TimeSpan`，包括空结果和全 NULL 结果。

显式 `DbType.Time` 加 `TimeSpan` 沿用旧钟面合同：只有 `0 <= ticks < TicksPerDay`
可转换为 `TimeOnly`。负值和 27 小时等输入在绑定时拒绝，不截断、不取模。
这一分支可与旧服务器配合；完整 DURATION 需要新服务器。

Chloe 候选须同时更新普通 SQL 参数和 `InsertRange` 的独立参数构造路径，
把 TimeSpan 设为 `DbType.Object`；只删除旧类型拒绝会使批量插入仍走 DbType.Time。
候选补丁基于真实 Chloe `bb7ec8735bd4511c35453eb009f70cc606795703`，
保留 DateTimeOffset 的 #251 拒绝及 DateTime 亚毫秒输入的 #236 拒绝。

## 持久化与恢复

关系目录 `tables.tblschema` 升为 v14，Duration 类型码为 10。首次涉及该类型后，
目录格式下限永久保留，即使迁移回滚、删除时长列或删除所有表也不降低版本。
旧目录中不能伪装携带 Duration 声明。没有时长的旧库保持原来的可写格式。

涉及 Duration 的 ALTER 使用有界 schema 迁移日志；v14 屏障先于日志和 raw 行改写。
新引擎在行存储开放前恢复，未完成操作恢复完整旧状态，已完成操作保留新状态。
索引键、覆盖值、行值、事务 undo、查询 spill、备份恢复均保留 ticks。
时长不启用仅适用于 INT/DATETIME 的范围索引优化。

物化视图快照只有包含时长声明/值时写 v3；其它结果继续写 v2。读取保留 v1/v2 兼容。
文件主头 `FileHeader` 布局未改变。

实际旧 v13 引擎会拒绝 v14。对 pending 日志及恢复中再次终止的库，实测表文件
字节与 mtime 均不变。对已完成日志，旧引擎会先重复写入相同完成标记，再拒绝 v14；
实测只有 `schema-migration.sdbddl` 的 mtime 变化，日志字节、raw 行与 WAL 均不变。
因此不能把该旧引擎行为称为完全零文件写入。强杀证据属于进程终止，不代表断电测试。

## 传输与旧客户端

SQL 结果协议 v5 含 v4 的日期 ticks/Kind 合同，并新增原生时长。
Frame 元数据声明版本为 4，Duration 值 tag 为 11，载荷为 little-endian Int64 ticks。
REST 值为 `{"$type":"duration","ticks":972000000001}`，只在服务器确认 v5 后解码；
缺少确认、越界 ticks、浮点、字符串、重复/多余属性与未知标签均拒绝。
原协议重载保留，旧重载不能承载时长。

旧协议 DML RETURNING 在绑定返回列时拒绝时长，避免写入后才序列化失败。
当前 RETURNING 语法仅接受列名和 `*`；CAST、算术及别名返回表达式在解析时拒绝。
查询仍校验声明和运行时值。带序列的查询及整个过程依赖树在首个副作用前验证输出；
包括 CAST、日期相减、别名、混合 CASE、嵌套过程与 DEFAULT nextval。
复杂来源或不透明函数无法事先确认类型时，旧协议保守拒绝该有序列调用。

新客户端仅在收到精确的执行前 `unsupported_result_version` 错误后回退。
REST 最多 v5→v4；Frame 最多 v5→v4→v3→v2。普通 SQL 错误和不确定传输不重放。

## 验收边界

源码、真实服务与真实 Chloe 候选的证据分别保存。公开正式 NuGet 发布、Chloe
上游合入和上游正式发行仍是独立门禁，不能由本地源码候选代替，因此源码交付本身
不能作为 #252 已可关闭的证据。
