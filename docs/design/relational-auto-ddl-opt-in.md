# 关系表未知列自动 DDL 的 opt-in 设计

## 当前合同

`INSERT INTO` 关系表时，列必须已存在于 table schema。未知列返回错误，且不会修改表定义。measurement 的 schema-on-write 和 `TAG` / `FIELD` 列提示不适用于关系表。显式 `ALTER TABLE ... ADD COLUMN` 是当前可执行路径。

## 独立开关

未来如需在关系表 INSERT 上自动追加列，应采用独立于 measurement schema-on-write 的策略，例如数据库默认 `Disabled`、按表显式选择 `AddNullableColumns`。开关必须由具备 DDL 管理权限的调用方设置，普通写权限不能隐式授权 DDL。未指定的表保持 `Disabled`，不能因为 SQL 语句、数据类型或客户端驱动不同而改变默认值。

## 启用范围

- 仅处理已存在关系表的 `INSERT ... VALUES` 中明确列出的未知列；未知表、`UPDATE`、`SELECT`、`INSERT ... SELECT`、文件导入及触发器中的隐式 DDL 不在首版范围。
- 新列只能是可空、无默认值、非主键且无索引的普通列。ROWVERSION、AUTO_INCREMENT、外键、CHECK、生成列和列重命名仍使用显式 DDL。
- 类型须从整条语句的非 NULL 值一致推断；全 NULL、互不兼容的值和不能稳定映射的表达式在发布 schema 前拒绝。新增列数、单表列数和 SQL 语句大小需有独立上限。
- 现有列仍严格按 table schema 校验；自动 DDL 不修改现有列类型、空值约束或默认值，也不把未知列当作 JSON 属性。

## 原子性与并发

实现前必须定义并验证 DDL 与 DML 的提交边界：持久化新 schema 失败时不发布内存 schema 或写入数据；数据写入失败时是否保留已提交的空列必须作为可观察合同明确说明。活动轻事务内不能绕过当前的 DDL 禁令。并发写入同一新列需要在 schema 锁下比较名称、类型和版本；相同定义可幂等复用，冲突定义必须拒绝，不可静默覆盖。

成功扩列应增加 table schema revision 并发出不含行值的审计事件，供客户端刷新元数据缓存。实现门禁应覆盖权限拒绝、并发同列冲突、持久化故障、批量中途失败、重启恢复和客户端 schema 刷新。完成上述合同与验证前，该 opt-in 不应暴露为可启用选项。
