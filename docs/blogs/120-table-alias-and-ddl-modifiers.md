## 单表别名与 DDL 修饰符：写出更地道的 SQL

在 SQL 的日常使用中，表别名（Table Alias）和列修饰符（NULL / NOT NULL / DEFAULT）是两项看似基础却极为实用的语法特性。表别名让你可以用简短的代称引用数据表，列修饰符则为数据模型提供了更丰富的约束表达能力。本次更新为 SonnetDB 带来了**单表别名与限定列名**的完整支持，同时对 DDL 中的列约束修饰符进行了框架性布局。

### 单表别名：让查询更简洁

在涉及大量列引用或较长表名的查询中，表别名可以显著提升 SQL 的可读性。SonnetDB 现在支持两种表别名语法——带 `AS` 关键字的标准写法和省略 `AS` 的简写形式：

```sql
-- 标准写法：FROM measurement AS alias
SELECT c.time, c.host, c.usage
FROM cpu AS c
WHERE c.host = 'edge-1'
ORDER BY c.time DESC
LIMIT 10;

-- 简写形式：FROM measurement alias（省略 AS）
SELECT c.time, c.host, c.usage
FROM cpu c
WHERE c.host = 'edge-1'
ORDER BY c.time DESC
LIMIT 10;
```

表别名一旦声明，就可以在 SELECT、WHERE 和 ORDER BY 子句中使用 `alias.column` 的限定列名语法来引用列。这不仅是书写上的便利，更重要的是**消除了多列查询中的列名歧义**——当你在一个包含多个测量的复杂查询中看到 `c.host`，你立刻知道它来自 `cpu` 表的别名 `c`。

### 限定列名：`alias.column` 和 `alias."Column"`

SonnetDB 的限定列名支持两种标识符格式：

```sql
-- 常规标识符：alias.column_name
SELECT c.time, c.usage FROM cpu AS c;

-- 带引号标识符：alias."Column Name"（支持特殊字符和大小写保留）
SELECT c."host", c."usage" FROM cpu AS c;
```

词法分析器新增了 `.` 作为独立 token（`TokenKind.Dot`），解析器在遇到 `identifier . identifier` 模式时，将点号前的标识符作为限定符（Qualifier），点号后的标识符作为列名。在 AST 层面，`IdentifierExpression` 记录类型增加了一个可选的 `Qualifier` 字段来表达这一结构。

### 别名校验：防患于未然

一个容易被忽视的细节是别名校验。当你在查询中使用了 `c.time`，但 FROM 子句并没有声明别名 `c`，或者声明的别名是 `x` 而实际引用的是 `c.usage`，会发生什么？

SonnetDB 在查询执行前会进行**全量别名引用校验**。执行器遍历 SELECT 语句中的所有标识符引用，检查每个限定符是否与声明的表别名匹配：

- 如果存在限定列名但 FROM 子句未声明表别名 → 抛出异常，提示"限定列名要求 FROM 子句声明单表别名"
- 如果限定符与声明的别名不匹配（如声明 `AS c` 却使用 `x.time`）→ 抛出异常，明确指出未知别名

这种在执行前的严格校验避免了静默错误——SQL 中的笔误不会产生令人费解的查询结果，而是给出清晰的错误提示。

### 当前限制：JOIN 仍保持小而明确

需要明确的是，SonnetDB 当前的 JOIN 支持仍然保持小而明确：MM4 第一版只覆盖一个 measurement 与一个关系维表的 inner 等值 JOIN，measurement 侧连接键必须是 TAG 列。跨 measurement JOIN、table 与 table JOIN、多表 JOIN 和 outer join 仍不在当前版本范围内，这是有意为之的设计决策。SonnetDB 的存储引擎以单测量独立存储为核心设计，跨测量 JOIN 的实现需要更深入的架构考量。

### DDL 修饰符：稀疏字段语义的框架

除了查询语法的增强，本次更新还对 DDL（数据定义语言）中的列修饰符进行了框架性布局。`NULL`、`NOT NULL` 和 `DEFAULT` 关键字已纳入词法分析器的识别范围，解析器接受了这些修饰符的语法，但在当前版本中，执行层会对 `DEFAULT` 子句返回明确的"不支持"提示。

这一设计选择背后是 SonnetDB **稀疏字段（sparse field）**的数据模型哲学。在传统的时序数据库中，每条记录都必须为所有列提供值；而在 SonnetDB 中，一个测量下的不同数据点可以有不同的 field 集合——某个数据点可能包含 `usage` 和 `cores`，另一个数据点可能只包含 `temperature`。这种稀疏模型天然不适合 `NOT NULL` 约束，因为"空"在本模型中并非缺失数据，而只是该字段在该时间点未被记录。

```sql
-- 当前版本：NULL / NOT NULL 修饰符在语法上被接受
CREATE MEASUREMENT sensors (
    device_id TAG,
    temperature FIELD FLOAT NULL,       -- 显式标记可空（当前为文档性修饰）
    pressure FIELD FLOAT NOT NULL       -- 标记不可空（当前不强制执行）
);

-- DEFAULT 子句：语法保留，执行时提示不支持
-- CREATE MEASUREMENT cpu (
--     host TAG,
--     usage FIELD FLOAT DEFAULT 0.0     -- 暂不支持，保留语法占位
-- );
```

未来的版本将根据实际需求逐步激活这些修饰符的执行语义，但在此之前，它们的语法已经被接受和保留，确保了向前兼容性。

### 小结

单表别名和限定列名让 SonnetDB 的 SQL 书写体验更接近主流关系型数据库，有效提升了复杂查询的可读性。DDL 修饰符的框架性支持则为未来的数据模型增强预留了语法空间。这两项改进共同体现了 SonnetDB SQL 引擎的演进策略：在保持时序数据库核心语义不变的前提下，持续向 SQL 标准靠拢。
