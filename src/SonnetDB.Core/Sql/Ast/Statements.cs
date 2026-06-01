namespace SonnetDB.Sql.Ast;

/// <summary>SQL 语句抽象基类。</summary>
public abstract record SqlStatement;

/// <summary>
/// <c>CREATE MEASUREMENT [IF NOT EXISTS] name (col TAG, col FIELD type, ...)</c>。
/// </summary>
/// <param name="Name">measurement 名称。</param>
/// <param name="Columns">列定义（按声明顺序）。</param>
/// <param name="IfNotExists">是否带 <c>IF NOT EXISTS</c> 修饰；为 <c>true</c> 时若同名 measurement 已存在则视为成功并复用现有 schema。</param>
public sealed record CreateMeasurementStatement(
    string Name,
    IReadOnlyList<ColumnDefinition> Columns,
    bool IfNotExists = false) : SqlStatement;

/// <summary>
/// <c>CREATE TABLE [IF NOT EXISTS] name (col TYPE [NULL|NOT NULL], ..., PRIMARY KEY (...))</c>。
/// </summary>
/// <param name="Name">关系表名称。</param>
/// <param name="Columns">列定义（按声明顺序）。</param>
/// <param name="PrimaryKey">主键列名（按声明顺序）。</param>
/// <param name="IfNotExists">是否带 <c>IF NOT EXISTS</c> 修饰；为 <c>true</c> 时同名表已存在则视为成功。</param>
public sealed record CreateTableStatement(
    string Name,
    IReadOnlyList<TableColumnDefinition> Columns,
    IReadOnlyList<string> PrimaryKey,
    bool IfNotExists = false) : SqlStatement;

/// <summary>关系表列定义。</summary>
/// <param name="Name">列名。</param>
/// <param name="DataType">列数据类型。</param>
/// <param name="Nullability">列空值修饰符；主键列执行时始终强制为 NOT NULL。</param>
public sealed record TableColumnDefinition(
    string Name,
    SqlDataType DataType,
    ColumnNullability Nullability = ColumnNullability.Unspecified);

/// <summary>
/// 向量索引声明抽象基类。
/// </summary>
public abstract record VectorIndexSpec;

/// <summary>
/// HNSW 向量索引声明：<c>WITH INDEX hnsw(m=16, ef=200)</c>。
/// </summary>
/// <param name="M">每个节点在每层保留的最大邻接数。</param>
/// <param name="Ef">建图与查询默认使用的候选规模。</param>
public sealed record HnswVectorIndexSpec(int M, int Ef) : VectorIndexSpec;

/// <summary>
/// IVF-Flat 向量索引声明：<c>WITH INDEX ivf(nlist=64, nprobe=8, max_iterations=25)</c>。
/// </summary>
public sealed record IvfVectorIndexSpec(int NList, int NProbe, int MaxIterations) : VectorIndexSpec;

/// <summary>
/// IVF-PQ 向量索引声明：<c>WITH INDEX ivf_pq(nlist=64, nprobe=8, m=8, nbits=8)</c>。
/// </summary>
public sealed record IvfPqVectorIndexSpec(int NList, int NProbe, int MaxIterations, int M, int NBits) : VectorIndexSpec;

/// <summary>
/// Vamana / DiskANN 向量索引声明：<c>WITH INDEX vamana(max_degree=32, search_list_size=75, alpha=1.2, beam_width=4)</c>。
/// </summary>
public sealed record VamanaVectorIndexSpec(int MaxDegree, int SearchListSize, float Alpha, int BeamWidth) : VectorIndexSpec;

/// <summary>列定义。</summary>
/// <param name="Name">列名。</param>
/// <param name="Kind">Tag 或 Field。</param>
/// <param name="DataType">列数据类型；Tag 列固定为 <see cref="SqlDataType.String"/>。</param>
/// <param name="VectorDimension">
/// 向量列的维度（仅当 <see cref="DataType"/> 为 <see cref="SqlDataType.Vector"/> 时非 <c>null</c>，且 &gt; 0）。
/// </param>
/// <param name="VectorIndex">
/// 向量列的可选索引声明；仅当 <see cref="DataType"/> 为 <see cref="SqlDataType.Vector"/> 时允许非 <c>null</c>。
/// </param>
/// <param name="Nullability">
/// DDL 兼容用的 <c>NULL</c> / <c>NOT NULL</c> 修饰符；当前不持久化到 catalog，也不改变稀疏字段语义。
/// </param>
/// <param name="DefaultExpression">
/// DDL 兼容用的 <c>DEFAULT</c> 表达式；parser 保留 AST，执行层会明确拒绝。
/// </param>
public sealed record ColumnDefinition(
    string Name,
    ColumnKind Kind,
    SqlDataType DataType,
    int? VectorDimension = null,
    VectorIndexSpec? VectorIndex = null,
    ColumnNullability Nullability = ColumnNullability.Unspecified,
    SqlExpression? DefaultExpression = null);

/// <summary>
/// <c>INSERT INTO measurement (col, ...) VALUES (v, ...), (...)</c>。
/// </summary>
/// <param name="Measurement">目标 measurement 名称。</param>
/// <param name="Columns">列名列表（按 VALUES 行内位置顺序）。</param>
/// <param name="Rows">每行的字面量表达式（与 <paramref name="Columns"/> 等长）。</param>
public sealed record InsertStatement(
    string Measurement,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<SqlExpression>> Rows) : SqlStatement;

/// <summary>
/// <c>SELECT projections FROM measurement [WHERE expr] [GROUP BY expr, ...]</c>。
/// </summary>
/// <param name="Projections">投影列表，可包含 <c>*</c> / 函数 / 列引用。</param>
/// <param name="Measurement">目标 measurement 名称（FROM 是 TVF 时为 TVF 推断的 source measurement，例如 <c>forecast(meter, ...)</c> → <c>meter</c>）。</param>
/// <param name="Where">可选 WHERE 表达式。</param>
/// <param name="GroupBy">GROUP BY 表达式列表；当未指定 GROUP BY 时为空集合（不为 <c>null</c>）。</param>
/// <param name="TableValuedFunction">FROM 子句若为表值函数调用（PR #55 起的 forecast 等）则非 <c>null</c>，否则 <c>null</c>。</param>
/// <param name="Pagination">可选分页子句；支持 <c>OFFSET/FETCH</c> 与兼容语法 <c>LIMIT</c>。</param>
/// <param name="OrderBy">可选排序子句；measurement 执行层支持 <c>ORDER BY time [ASC|DESC]</c>，关系表执行层支持结果列名。</param>
/// <param name="TableAlias">FROM 子句声明的可选单表别名；当前不支持 JOIN。</param>
public sealed record SelectStatement(
    IReadOnlyList<SelectItem> Projections,
    string Measurement,
    SqlExpression? Where,
    IReadOnlyList<SqlExpression> GroupBy,
    FunctionCallExpression? TableValuedFunction = null,
    PaginationSpec? Pagination = null,
    OrderBySpec? OrderBy = null,
    string? TableAlias = null) : SqlStatement;

/// <summary>排序方向。</summary>
public enum SortDirection
{
    /// <summary>升序。</summary>
    Ascending,
    /// <summary>降序。</summary>
    Descending,
}

/// <summary>
/// 排序子句参数。
/// </summary>
/// <param name="Expression">排序表达式。</param>
/// <param name="Direction">排序方向；未显式指定时为升序。</param>
public sealed record OrderBySpec(SqlExpression Expression, SortDirection Direction);

/// <summary>
/// 分页子句参数：<c>OFFSET</c> + 可选 <c>FETCH</c>。
/// 当 <see cref="Fetch"/> 为 <c>null</c> 时表示“从偏移量开始返回全部剩余行”。
/// </summary>
/// <param name="Offset">跳过行数（&gt;= 0）。</param>
/// <param name="Fetch">返回行数上限（&gt;= 0）；<c>null</c> 表示不限制。</param>
public sealed record PaginationSpec(int Offset, int? Fetch);

/// <summary>SELECT 投影项。</summary>
/// <param name="Expression">投影表达式（可能为 <see cref="StarExpression"/>）。</param>
/// <param name="Alias">可选 <c>AS alias</c> 别名。</param>
public sealed record SelectItem(
    SqlExpression Expression,
    string? Alias);

/// <summary>GROUP BY time(duration) 桶规格。</summary>
/// <param name="BucketSizeMs">桶大小（毫秒，&gt; 0）。</param>
public sealed record TimeBucketSpec(long BucketSizeMs);

/// <summary>
/// <c>DELETE FROM measurement WHERE expr</c>；目前仅支持 WHERE 时间窗 + tag 等值组合。
/// </summary>
/// <param name="Measurement">目标 measurement 名称。</param>
/// <param name="Where">WHERE 表达式（必填）。</param>
public sealed record DeleteStatement(
    string Measurement,
    SqlExpression Where) : SqlStatement;

/// <summary>
/// <c>UPDATE table SET col = expr [, ...] WHERE expr</c>。
/// </summary>
/// <param name="TableName">目标关系表名称。</param>
/// <param name="Assignments">SET 子句中的列赋值列表。</param>
/// <param name="Where">WHERE 表达式（必填）。</param>
public sealed record UpdateStatement(
    string TableName,
    IReadOnlyList<UpdateAssignment> Assignments,
    SqlExpression Where) : SqlStatement;

/// <summary>UPDATE SET 子句中的一个列赋值。</summary>
/// <param name="ColumnName">列名。</param>
/// <param name="Value">赋值表达式。</param>
public sealed record UpdateAssignment(string ColumnName, SqlExpression Value);

/// <summary>
/// <c>DROP TABLE name</c>：删除关系表 schema 与 rowstore。
/// </summary>
/// <param name="Name">目标关系表名称。</param>
public sealed record DropTableStatement(string Name) : SqlStatement;

/// <summary>
/// <c>SHOW MEASUREMENTS</c>：列出当前数据库中所有 measurement。
/// 返回单列 <c>name</c>(string)，按字典序升序排列。
/// </summary>
public sealed record ShowMeasurementsStatement : SqlStatement;

/// <summary>
/// <c>SHOW TABLES</c>：列出当前数据库中所有关系表。
/// 返回单列 <c>name</c>(string)，按字典序升序排列。
/// </summary>
public sealed record ShowTablesStatement : SqlStatement;

/// <summary>
/// <c>DESCRIBE [MEASUREMENT] &lt;name&gt;</c>（兼容别名 <c>DESC</c>）：描述指定 measurement 的列结构。
/// 返回三列 <c>column_name</c>(string)、<c>column_type</c>(string，取值 <c>tag</c> / <c>field</c>)、<c>data_type</c>(string，例如 <c>float64</c> / <c>int64</c> / <c>boolean</c> / <c>string</c>)。
/// </summary>
/// <param name="Name">目标 measurement 名称。</param>
public sealed record DescribeMeasurementStatement(string Name) : SqlStatement;

/// <summary>
/// <c>DESCRIBE TABLE &lt;name&gt;</c>：描述指定关系表的列结构。
/// </summary>
/// <param name="Name">目标关系表名称。</param>
public sealed record DescribeTableStatement(string Name) : SqlStatement;

/// <summary>
/// <c>EXPLAIN &lt;read-only statement&gt;</c>：对只读语句返回估算扫描与命中统计。
/// 当前仅支持 <c>SELECT</c>、<c>SHOW MEASUREMENTS</c> / <c>SHOW TABLES</c> 与 <c>DESCRIBE [MEASUREMENT|TABLE]</c>。
/// </summary>
/// <param name="Statement">被解释的只读语句。</param>
public sealed record ExplainStatement(SqlStatement Statement) : SqlStatement;
