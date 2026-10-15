# 独立持久序列

独立序列由数据库目录保存，可供多个关系表共享。首版只支持单 schema、Int64 和不循环的非零步长。

```sql
CREATE SEQUENCE [IF NOT EXISTS] OrderIds
    START WITH 100 INCREMENT BY 2 NO CYCLE;
SELECT nextval('OrderIds');
CREATE TABLE orders(id INT DEFAULT nextval('OrderIds'), PRIMARY KEY(id));
INSERT INTO orders DEFAULT VALUES;
INSERT INTO orders(id) VALUES(nextval('OrderIds') + 100), (DEFAULT);
DROP SEQUENCE [IF EXISTS] OrderIds;
```

`START WITH` 和 `INCREMENT BY` 均可省略，缺省为 1；负步长有效。最后一个合法 Int64 值可以返回，之后稳定报 `sequence_exhausted`，重开数据库也不会恢复号码。`CYCLE` 和 schema 限定名暂不支持。

名称保存创建时的拼写，普通引用按 `OrdinalIgnoreCase` 解析。精确引用在字符串内写双引号，例如 `nextval('"OrderIds"')`；名称中的双引号用两个双引号转义。同一数据库内不允许仅大小写不同的序列名称。

每个实际执行的 `nextval` 先同步持久化新状态，再返回已分配号码，且不受外层关系事务回滚影响。失败批量、事务回滚、客户端未读取结果或提前关闭 reader 均可能留下号码间隙；号码不会回收。序列查询的投影在 SQL 执行返回前物化，零行和 `LIMIT 0` 不分配号码；CASE 和 COALESCE 仅执行实际选中的分支。取消在持久化前生效，若持久化已经开始，已分配号码仍然消耗。

支持无 FROM 或单关系基表 SELECT 的标量投影、关系表 INSERT VALUES / INSERT SELECT、Int64 列直接 `DEFAULT nextval('literal')`。DEFAULT 按插入行单独求值，CREATE TABLE / ALTER COLUMN SET DEFAULT 校验不取号。首版拒绝 nextval 用于聚合、窗口、DISTINCT、集合操作、JOIN、CTE、子查询、谓词、排序表达式、分页表达式、ON CONFLICT、UPDATE/DELETE、图、Modbus、CHECK、索引过滤和触发器。ALTER ADD COLUMN 不能直接附带序列默认值，可先添加列再 SET DEFAULT。

nextval 是持久写操作，需要数据库写权限；通过 SELECT、嵌套函数或过程调用也不能绕过。Workbench `sql.read` 预览拒绝取号。内置 nextval 名称保留，用户注册的同名函数不会替换其行为。DROP SEQUENCE 不级联删除列 DEFAULT；其后插入会报 `sequence_not_found`，可显式更新默认值或重建序列。序列 DDL 不允许在活动轻事务中执行。

数据库一致备份会冻结序列分配直到目录复制完成，并包含已消费的高水位。恢复旧备份会恢复备份时点的序列状态；若外部系统仍保存备份之后分配的号码，必须由应用协调恢复策略。WAL 写入或同步结果不确定时，当前数据库实例停止序列操作并报 `sequence_recovery_required`，关闭再重新打开后从 WAL 恢复，避免使用旧内存状态重复发号。

错误码包括 `sequence_not_found`、`sequence_already_exists`、`sequence_exhausted`、`sequence_invalid_definition` 和 `sequence_recovery_required`。该切片的构建和定向测试不能替代完整 Parity、真实生产性能与发行门禁。
