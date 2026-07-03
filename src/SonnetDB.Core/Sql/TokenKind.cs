namespace SonnetDB.Sql;

/// <summary>
/// SQL 词法分析器产出的 token 类别。
/// </summary>
public enum TokenKind
{
    // 终止符
    EndOfFile,

    // 字面量
    IdentifierLiteral,
    IntegerLiteral,
    FloatLiteral,
    StringLiteral,
    DurationLiteral,

    /// <summary>
    /// 参数占位符：位置参数 <c>?</c>（<see cref="Token.Text"/> 为空，序号按出现顺序隐式分配）
    /// 或命名参数 <c>@name</c> / <c>:name</c>（<see cref="Token.Text"/> 为去前缀后的参数名）。
    /// </summary>
    Parameter,

    // 标点
    LeftParen,
    RightParen,
    LeftBracket,
    RightBracket,
    Comma,
    Semicolon,
    Dot,
    Star,

    // 比较 / 算术运算符
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    /// <summary><c>&lt;=&gt;</c>：pgvector 兼容余弦距离运算符（PR #59）。</summary>
    VectorCosineDistance,
    /// <summary><c>&lt;-&gt;</c>：pgvector 兼容 L2 距离运算符（PR #59）。</summary>
    VectorL2Distance,
    /// <summary><c>&lt;#&gt;</c>：pgvector 兼容内积运算符（PR #59）。</summary>
    VectorInnerProduct,
    GreaterThan,
    GreaterThanOrEqual,
    Plus,
    Minus,
    Slash,
    Percent,
    /// <summary><c>=&gt;</c>：函数命名参数分隔符。</summary>
    Arrow,

    // 关键字
    KeywordCreate,
    /// <summary>UNIQUE（二级索引唯一性声明）。</summary>
    KeywordUnique,
    /// <summary>SPARSE（文档索引稀疏声明）。</summary>
    KeywordSparse,
    /// <summary>TTL（文档 TTL 索引声明）。</summary>
    KeywordTtl,
    /// <summary>INDEX（二级索引 DDL）。</summary>
    KeywordIndex,
    KeywordMeasurement,
    /// <summary>TABLE（关系表 DDL）。</summary>
    KeywordTable,
    /// <summary>DOCUMENT（JSON 文档集合 DDL）。</summary>
    KeywordDocument,
    /// <summary>COLLECTION（JSON 文档集合 DDL）。</summary>
    KeywordCollection,
    /// <summary>COLLECTIONS（SHOW DOCUMENT COLLECTIONS）。</summary>
    KeywordCollections,
    KeywordInsert,
    KeywordInto,
    /// <summary>IMPORT JSON 文件导入。</summary>
    KeywordImport,
    /// <summary>FORMAT 导入格式声明。</summary>
    KeywordFormat,
    /// <summary>PATH 导入 ID path 声明。</summary>
    KeywordPath,
    KeywordValues,
    KeywordSelect,
    /// <summary>DISTINCT（SELECT DISTINCT 去重）。</summary>
    KeywordDistinct,
    KeywordFrom,
    /// <summary>JOIN（MM4 时序 measurement JOIN 关系维表）。</summary>
    KeywordJoin,
    /// <summary>INNER（可选 INNER JOIN 修饰词）。</summary>
    KeywordInner,
    /// <summary>LEFT（LEFT JOIN 修饰词）。</summary>
    KeywordLeft,
    /// <summary>OUTER（可选 LEFT OUTER JOIN 修饰词）。</summary>
    KeywordOuter,
    KeywordWhere,
    /// <summary>IS（NULL 判定谓词）。</summary>
    KeywordIs,
    /// <summary>IN（集合或子查询成员谓词）。</summary>
    KeywordIn,
    KeywordGroup,
    KeywordBy,
    KeywordHaving,
    KeywordTime,
    KeywordDelete,
    /// <summary>UPDATE（关系表 DML）。</summary>
    KeywordUpdate,
    /// <summary>SET（UPDATE SET 子句）。</summary>
    KeywordSet,
    KeywordAnd,
    KeywordOr,
    KeywordNot,
    /// <summary>LIKE 字符串模式匹配。</summary>
    KeywordLike,
    /// <summary>REGEX 正则表达式匹配。</summary>
    KeywordRegex,
    /// <summary>IF（用于 IF NOT EXISTS 等条件子句）。</summary>
    KeywordIf,
    /// <summary>EXISTS（用于 IF NOT EXISTS 等条件子句）。</summary>
    KeywordExists,
    KeywordAs,
    KeywordNull,
    KeywordDefault,
    KeywordTrue,
    KeywordFalse,
    /// <summary>CASE 条件表达式。</summary>
    KeywordCase,
    /// <summary>WHEN 条件分支。</summary>
    KeywordWhen,
    /// <summary>THEN 条件结果。</summary>
    KeywordThen,
    /// <summary>ELSE 默认结果。</summary>
    KeywordElse,
    /// <summary>END 结束 CASE 表达式。</summary>
    KeywordEnd,
    KeywordTag,
    KeywordField,
    KeywordFloat,
    KeywordInt,
    KeywordBool,
    KeywordString,
    /// <summary>DATETIME 关系表列声明。</summary>
    KeywordDateTime,
    /// <summary>BLOB 关系表列声明。</summary>
    KeywordBlob,
    /// <summary>JSON 关系表列声明。</summary>
    KeywordJson,
    /// <summary>FULLTEXT 索引 DDL。</summary>
    KeywordFullText,
    /// <summary>USING 分词器声明。</summary>
    KeywordUsing,
    /// <summary>VECTOR(dim) 列声明（PR #58 b）。</summary>
    KeywordVector,
    /// <summary>GEOPOINT 列声明（PR #70）。</summary>
    KeywordGeoPoint,

    // PR #34a：控制面 DDL
    KeywordUser,
    KeywordPassword,
    KeywordGrant,
    KeywordRevoke,
    KeywordOn,
    KeywordCascade,
    KeywordTo,
    KeywordWith,
    KeywordRead,
    KeywordWrite,
    KeywordAdmin,
    KeywordDatabase,
    KeywordDrop,
    KeywordAlter,
    /// <summary>COLUMN（ALTER TABLE 列级 DDL）。</summary>
    KeywordColumn,
    /// <summary>RENAME（ALTER TABLE rename DDL）。</summary>
    KeywordRename,
    /// <summary>PRIMARY（PRIMARY KEY 子句）。</summary>
    KeywordPrimary,
    /// <summary>KEY（PRIMARY KEY 子句）。</summary>
    KeywordKey,
    /// <summary>FOREIGN（FOREIGN KEY 子句）。</summary>
    KeywordForeign,
    /// <summary>REFERENCES（FOREIGN KEY 引用子句）。</summary>
    KeywordReferences,
    /// <summary>ROWVERSION（关系表乐观并发列）。</summary>
    KeywordRowVersion,

    // PR #34b-1：SHOW 控制面查询
    KeywordShow,
    KeywordUsers,
    KeywordGrants,
    KeywordDatabases,
    KeywordFor,

    // PR #34b-3：CREATE USER ... SUPERUSER
    KeywordSuperuser,

    // PR #34b-3-tokens：API token 管理（SHOW TOKENS / ISSUE TOKEN / REVOKE TOKEN）
    KeywordTokens,
    KeywordToken,
    KeywordIssue,

    // 元数据查询：EXPLAIN / SHOW MEASUREMENTS / SHOW TABLES / DESCRIBE [MEASUREMENT|TABLE] <name>
    KeywordExplain,
    KeywordMeasurements,
    KeywordTables,
    KeywordDescribe,
    KeywordDesc,

    // 排序 / 分页子句：ORDER BY / ASC / DESC / OFFSET / FETCH / LIMIT
    KeywordOrder,
    KeywordAsc,
    KeywordOffset,
    KeywordFetch,
    KeywordLimit,

    /// <summary>BEGIN 轻事务起始。</summary>
    KeywordBegin,
    /// <summary>COMMIT 轻事务提交。</summary>
    KeywordCommit,
    /// <summary>ROLLBACK 轻事务回滚。</summary>
    KeywordRollback,
    /// <summary>TRANSACTION，可选轻事务修饰词。</summary>
    KeywordTransaction,
}
