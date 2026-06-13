namespace SonnetDB.Sql.Ast;

/// <summary>列在 measurement schema 中的角色。</summary>
public enum ColumnKind
{
    /// <summary>Tag 列：参与 SeriesKey 规范化、可作为索引维度。</summary>
    Tag,
    /// <summary>Field 列：实际承载时间序列值。</summary>
    Field,
}

/// <summary>
/// DDL 兼容用的列空值修饰符；当前仅在 SQL AST 中保留，不改变 SonnetDB 稀疏字段写入语义。
/// </summary>
public enum ColumnNullability
{
    /// <summary>未显式声明 <c>NULL</c> 或 <c>NOT NULL</c>。</summary>
    Unspecified,
    /// <summary>显式声明 <c>NULL</c>。</summary>
    Nullable,
    /// <summary>显式声明 <c>NOT NULL</c>；当前执行层不强制该约束。</summary>
    NotNull,
}

/// <summary>SQL 层支持的列数据类型，对应 <see cref="SonnetDB.Storage.Format.FieldType"/>。</summary>
public enum SqlDataType
{
    /// <summary>64 位双精度浮点。</summary>
    Float64,
    /// <summary>64 位有符号整数。</summary>
    Int64,
    /// <summary>布尔值。</summary>
    Boolean,
    /// <summary>字符串。</summary>
    String,
    /// <summary>定长 32 位浮点向量；维度由 <c>ColumnDefinition.VectorDimension</c> 声明（PR #58 b）。</summary>
    Vector,
    /// <summary>WGS84 地理点；使用 <c>POINT(lat, lon)</c> 写入（PR #70）。</summary>
    GeoPoint,
    /// <summary>UTC 时间戳；关系表中按 Unix 毫秒持久化。</summary>
    DateTime,
    /// <summary>二进制大对象；关系表中以 BLOB 存储。</summary>
    Blob,
    /// <summary>JSON 文本；关系表 MVP 中以字符串形式存储和返回。</summary>
    Json,
}

/// <summary>SQL 层支持的二元运算符。</summary>
public enum SqlBinaryOperator
{
    /// <summary>逻辑或。</summary>
    Or,
    /// <summary>逻辑与。</summary>
    And,
    /// <summary>等于。</summary>
    Equal,
    /// <summary>不等于。</summary>
    NotEqual,
    /// <summary>小于。</summary>
    LessThan,
    /// <summary>小于等于。</summary>
    LessThanOrEqual,
    /// <summary>大于。</summary>
    GreaterThan,
    /// <summary>大于等于。</summary>
    GreaterThanOrEqual,
    /// <summary>LIKE 字符串模式匹配。</summary>
    Like,
    /// <summary>NOT LIKE 字符串模式不匹配。</summary>
    NotLike,
    /// <summary>REGEX 正则表达式匹配。</summary>
    Regex,
    /// <summary>NOT REGEX 正则表达式不匹配。</summary>
    NotRegex,
    /// <summary>加。</summary>
    Add,
    /// <summary>减。</summary>
    Subtract,
    /// <summary>乘。</summary>
    Multiply,
    /// <summary>除。</summary>
    Divide,
    /// <summary>取模。</summary>
    Modulo,
}

/// <summary>SQL 层支持的一元运算符。</summary>
public enum SqlUnaryOperator
{
    /// <summary>逻辑非。</summary>
    Not,
    /// <summary>负号。</summary>
    Negate,
}
