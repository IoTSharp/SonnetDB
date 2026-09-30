# SQL 标识符大小写规则（GH-Issue #211）

SonnetDB 保留创建时的名称拼写。普通引用使用 `OrdinalIgnoreCase`，双引号引用使用 `Ordinal`；禁止新建或重命名为仅大小写不同的同作用域名称。具体语法合同见[SQL 参考](../sql-reference.md#标识符大小写合同gh-issue-211)。

## 为什么保留拼写

SonnetDB 的 schema 既来自 SQL，也来自 Point、Line Protocol 等设备摄取。设备提供的 `DeviceID`、`Temperature` 等名称可以直接呈现在目录、查询元数据和管理工具中；SQL 用 `deviceid` 或 `TEMPERATURE` 仍能访问它们。名称解析后使用已保存的拼写写入 series/WAL，避免大小写变化产生第二份列或数据。

例如创建列 `DeviceID` 后，普通 `deviceid`、`DEVICEID` 与双引号 `"DeviceID"` 都能访问；`"deviceid"` 精确匹配失败。不能再创建 `deviceid` 或 `"deviceid"` 列。双引号保证精确引用，但不提供大小写变体的独立命名空间。

## PostgreSQL 与 Oracle 的选择

| 数据库/方案 | 未加引号创建 `DeviceID` | 普通引用 | 双引号引用 |
| --- | --- | --- | --- |
| PostgreSQL | 保存为 `deviceid` | 先折叠小写再精确查找 | 保留拼写并精确查找 |
| Oracle | 保存为 `DEVICEID` | 先折叠大写再精确查找 | 保留拼写并精确查找 |
| SonnetDB | 保存为 `DeviceID` | 忽略大小写查找已保存的原名 | 精确查找已保存的原名 |

[PostgreSQL 官方文档](https://www.postgresql.org/docs/current/sql-syntax-lexical.html#SQL-SYNTAX-IDENTIFIERS)指出，SQL 标准使用大写折叠，而 PostgreSQL 使用小写；建议跨数据库应用始终加引号或始终不加引号。[Oracle 官方文档](https://docs.oracle.com/en/database/oracle/oracle-database/23/sqlrf/Database-Object-Names-and-Qualifiers.html)规定普通名称解释为大写，双引号名称区分大小写。

从设计效果看，折叠让普通名称具有唯一的目录表示，并且可以让 `deviceid` 与 `"DeviceID"` 并存。保留拼写也能确定地解析名称，但须明确禁止重名、保存名称索引，以及处理旧目录歧义。这些是名称合同的取舍，不能仅凭官方语法规则断言数据库作者的全部历史设计动机。

性能不作为本决策的依据。折叠通常在解析阶段转换一次字符串；忽略大小写查找可用 `OrdinalIgnoreCase` 字典索引，避免每次遍历整个 schema。两者都有可控的实现成本，实际差异需用具体查询基准验证。

## 其他数据库的规则

| 数据库 | 表名和列名的比较 | 对 SonnetDB 的启示 |
| --- | --- | --- |
| MySQL | 列名在所有平台忽略大小写；表名受操作系统及 `lower_case_table_names` 配置影响 | 表名和列名不一定采用同一规则，跨平台迁移必须考虑名称冲突 |
| SQL Server | 表名和列名受数据库标识符 collation 控制；CI 忽略大小写，CS 区分大小写 | 保留拼写与忽略大小写可以并存；加引号本身不切换大小写规则 |
| SQLite | 内部标识符比较忽略大小写 | 忽略大小写是成熟的可选设计，但其字符比较规则不能直接等同于 .NET 的 `OrdinalIgnoreCase` |

依据：[MySQL 8.4 官方手册](https://docs.oracle.com/cd/E17952_01/mysql-8.4-en/identifier-case-sensitivity.html)、[SQL Server 标识符文档](https://learn.microsoft.com/en-us/sql/relational-databases/databases/database-identifiers)、[SQLite 标识符比较说明](https://www.sqlite.org/c3ref/stricmp.html)。不同数据库没有统一的实现形式，SonnetDB 明确采用固定、与平台和语言区域无关的规则；双引号精确匹配是本项目的合同，不应据此宣称完全兼容上述数据库。

## 一致性与兼容

解析器保留引号来源。名称绑定阶段统一选择比较规则，将引用解析为 catalog 中的原名；执行和持久化使用绑定结果。别名、CTE、主外键、索引列和 DDL/DML 目标也遵循这一流程。

旧目录中的大小写变体不自动改写或合并。普通引用命中多个名称时报告歧义；管理员可用双引号精确检查并显式迁移。新建、扩列和重命名均不能新增此类冲突。

标识符规则与字符串值比较、数据 collation、JSON 文档属性键和其他模型的数据键分开定义。不能把 SQL 名称比较器应用到用户数据上。
