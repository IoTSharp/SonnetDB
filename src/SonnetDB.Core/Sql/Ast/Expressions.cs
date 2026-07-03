namespace SonnetDB.Sql.Ast;

/// <summary>SQL 表达式节点抽象基类。</summary>
public abstract record SqlExpression;

/// <summary>字面量类别。</summary>
public enum SqlLiteralKind
{
    /// <summary>SQL <c>NULL</c>。</summary>
    Null,
    /// <summary>布尔字面量。</summary>
    Boolean,
    /// <summary>整数字面量（64 位有符号）。</summary>
    Integer,
    /// <summary>浮点字面量（64 位双精度）。</summary>
    Float,
    /// <summary>字符串字面量。</summary>
    String,
}

/// <summary>字面量表达式：包装 NULL / Boolean / Integer / Float / String。</summary>
/// <param name="Kind">字面量类别。</param>
/// <param name="StringValue">字符串字面量内容（仅当 <see cref="Kind"/> 为 <see cref="SqlLiteralKind.String"/>）。</param>
/// <param name="IntegerValue">整数值（仅当 <see cref="Kind"/> 为 <see cref="SqlLiteralKind.Integer"/>）。</param>
/// <param name="FloatValue">浮点值（仅当 <see cref="Kind"/> 为 <see cref="SqlLiteralKind.Float"/>）。</param>
/// <param name="BooleanValue">布尔值（仅当 <see cref="Kind"/> 为 <see cref="SqlLiteralKind.Boolean"/>）。</param>
public sealed record LiteralExpression(
    SqlLiteralKind Kind,
    string? StringValue = null,
    long IntegerValue = 0,
    double FloatValue = 0,
    bool BooleanValue = false) : SqlExpression
{
    /// <summary>构造 NULL 字面量。</summary>
    public static LiteralExpression Null() => new(SqlLiteralKind.Null);
    /// <summary>构造布尔字面量。</summary>
    public static LiteralExpression Bool(bool value) => new(SqlLiteralKind.Boolean, BooleanValue: value);
    /// <summary>构造整数字面量。</summary>
    public static LiteralExpression Integer(long value) => new(SqlLiteralKind.Integer, IntegerValue: value);
    /// <summary>构造浮点字面量。</summary>
    public static LiteralExpression Float(double value) => new(SqlLiteralKind.Float, FloatValue: value);
    /// <summary>构造字符串字面量。</summary>
    public static LiteralExpression String(string value) => new(SqlLiteralKind.String, StringValue: value);
}

/// <summary>时间间隔字面量（单位毫秒），仅在 <c>time(...)</c> 与可能的时间运算上下文中出现。</summary>
/// <param name="Milliseconds">已转换为毫秒的整数值。</param>
public sealed record DurationLiteralExpression(long Milliseconds) : SqlExpression;

/// <summary>
/// 向量字面量 <c>[v0, v1, v2, ...]</c>（PR #58 b）。
/// 解析器将每个元素归一为 <see cref="double"/>，由执行器负责转换为 <see cref="float"/> 数组并校验维度匹配。
/// </summary>
/// <param name="Components">按声明顺序的分量值（长度即维度，&gt;= 1）。</param>
public sealed record VectorLiteralExpression(IReadOnlyList<double> Components) : SqlExpression;

/// <summary>
/// 地理点字面量 <c>POINT(lat, lon)</c>（PR #70）。
/// </summary>
/// <param name="Lat">纬度，范围由执行器校验为 [-90, 90]。</param>
/// <param name="Lon">经度，范围由执行器校验为 [-180, 180]。</param>
public sealed record GeoPointLiteralExpression(double Lat, double Lon) : SqlExpression;

/// <summary>标识符引用（列名 / 字段名 / tag 名），可带单表别名限定符。</summary>
/// <param name="Name">标识符名称（保留原始大小写）。</param>
/// <param name="Qualifier">可选限定符，例如 <c>alias.column</c> 中的 <c>alias</c>。</param>
public sealed record IdentifierExpression(string Name, string? Qualifier = null) : SqlExpression;

/// <summary>
/// 参数占位符表达式（#213）：位置参数 <c>?</c> 或命名参数 <c>@name</c> / <c>:name</c>。
/// 解析阶段产出此节点（与具体参数值无关，故带占位符的 AST 可被解析缓存跨不同参数值复用）；
/// 执行前由 <c>SqlParameterBinder</c> 用实际值重写为 <see cref="LiteralExpression"/>。
/// </summary>
/// <param name="Ordinal">位置序号（从 0 起，按占位符在 SQL 中出现顺序分配；命名参数也分配序号以支持按序绑定）。</param>
/// <param name="Name">命名参数名（去 <c>@</c>/<c>:</c> 前缀）；位置参数 <c>?</c> 为 <c>null</c>。</param>
public sealed record ParameterExpression(int Ordinal, string? Name = null) : SqlExpression;

/// <summary><c>*</c> 通配符（仅出现在 SELECT 列表或 COUNT(*) 中）。</summary>
public sealed record StarExpression : SqlExpression
{
    /// <summary>共享单例实例。</summary>
    public static StarExpression Instance { get; } = new();
}

/// <summary>函数调用，例如 <c>count(*)</c> / <c>avg(value)</c> / <c>time(1m)</c>。</summary>
/// <param name="Name">函数名（保留原始大小写）。</param>
/// <param name="Arguments">函数实参；当 <see cref="IsStar"/> 为 <c>true</c> 时为空列表。</param>
/// <param name="IsStar">是否为 <c>fn(*)</c> 形式。</param>
public sealed record FunctionCallExpression(
    string Name,
    IReadOnlyList<SqlExpression> Arguments,
    bool IsStar = false) : SqlExpression;

/// <summary>函数命名参数，例如 <c>source =&gt; docs</c>。</summary>
/// <param name="Name">参数名。</param>
/// <param name="Value">参数值表达式。</param>
public sealed record NamedArgumentExpression(
    string Name,
    SqlExpression Value) : SqlExpression;

/// <summary>标量子查询表达式，例如 <c>(SELECT count(*) FROM devices)</c>。</summary>
/// <param name="Select">子查询 SELECT 语句。</param>
public sealed record SubqueryExpression(SelectStatement Select) : SqlExpression;

/// <summary><c>EXISTS (SELECT ...)</c> 谓词表达式。</summary>
/// <param name="Select">用于判定是否至少返回一行的子查询。</param>
public sealed record ExistsExpression(SelectStatement Select) : SqlExpression;

/// <summary>二元运算表达式。</summary>
/// <param name="Operator">运算符。</param>
/// <param name="Left">左操作数。</param>
/// <param name="Right">右操作数。</param>
public sealed record BinaryExpression(
    SqlBinaryOperator Operator,
    SqlExpression Left,
    SqlExpression Right) : SqlExpression;

/// <summary><c>value [NOT] IN (...)</c> 谓词表达式。</summary>
/// <param name="Value">待匹配的左侧表达式。</param>
/// <param name="Values">常量/表达式列表；当使用子查询时为空。</param>
/// <param name="Subquery">可选右侧子查询。</param>
/// <param name="Negated">是否为 <c>NOT IN</c>。</param>
public sealed record InExpression(
    SqlExpression Value,
    IReadOnlyList<SqlExpression> Values,
    SelectStatement? Subquery = null,
    bool Negated = false) : SqlExpression;

/// <summary><c>CASE WHEN ... THEN ... [ELSE ...] END</c> 条件表达式。</summary>
/// <param name="WhenClauses">按顺序判断的 WHEN/THEN 分支。</param>
/// <param name="Else">可选 ELSE 表达式；省略时结果为 NULL。</param>
public sealed record CaseExpression(
    IReadOnlyList<CaseWhenClause> WhenClauses,
    SqlExpression? Else = null) : SqlExpression;

/// <summary>CASE 表达式的单个 WHEN/THEN 分支。</summary>
/// <param name="Condition">WHEN 条件。</param>
/// <param name="Result">THEN 结果。</param>
public sealed record CaseWhenClause(
    SqlExpression Condition,
    SqlExpression Result);

/// <summary>一元运算表达式（NOT / 取负）。</summary>
/// <param name="Operator">一元运算符。</param>
/// <param name="Operand">操作数。</param>
public sealed record UnaryExpression(
    SqlUnaryOperator Operator,
    SqlExpression Operand) : SqlExpression;

/// <summary>
/// <c>expr IS [NOT] NULL</c> 空值判定谓词。
/// 与普通比较不同，本谓词永远返回明确的 TRUE / FALSE（绝不产生三值逻辑中的 UNKNOWN），
/// 是唯一允许直接检测 <c>NULL</c> 的表达式；解析器把 <c>IS [NOT] NULL</c> 归约到此节点，
/// 而不再退化成 <c>= NULL</c> / <c>!= NULL</c> 比较（后者按三值逻辑判 UNKNOWN）。
/// </summary>
/// <param name="Operand">被判定的操作数表达式。</param>
/// <param name="Negated">是否为 <c>IS NOT NULL</c>。</param>
public sealed record IsNullExpression(
    SqlExpression Operand,
    bool Negated = false) : SqlExpression;
