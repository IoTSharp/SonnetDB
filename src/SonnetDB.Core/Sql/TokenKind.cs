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

    // 关键字
    KeywordCreate,
    /// <summary>UNIQUE（二级索引唯一性声明）。</summary>
    KeywordUnique,
    /// <summary>INDEX（二级索引 DDL）。</summary>
    KeywordIndex,
    KeywordMeasurement,
    /// <summary>TABLE（关系表 DDL）。</summary>
    KeywordTable,
    KeywordInsert,
    KeywordInto,
    KeywordValues,
    KeywordSelect,
    KeywordFrom,
    KeywordWhere,
    KeywordGroup,
    KeywordBy,
    KeywordTime,
    KeywordDelete,
    /// <summary>UPDATE（关系表 DML）。</summary>
    KeywordUpdate,
    /// <summary>SET（UPDATE SET 子句）。</summary>
    KeywordSet,
    KeywordAnd,
    KeywordOr,
    KeywordNot,
    /// <summary>IF（用于 IF NOT EXISTS 等条件子句）。</summary>
    KeywordIf,
    /// <summary>EXISTS（用于 IF NOT EXISTS 等条件子句）。</summary>
    KeywordExists,
    KeywordAs,
    KeywordNull,
    KeywordDefault,
    KeywordTrue,
    KeywordFalse,
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
    KeywordTo,
    KeywordWith,
    KeywordRead,
    KeywordWrite,
    KeywordAdmin,
    KeywordDatabase,
    KeywordDrop,
    KeywordAlter,
    /// <summary>PRIMARY（PRIMARY KEY 子句）。</summary>
    KeywordPrimary,
    /// <summary>KEY（PRIMARY KEY 子句）。</summary>
    KeywordKey,

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
