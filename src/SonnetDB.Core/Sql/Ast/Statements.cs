namespace SonnetDB.Sql.Ast;

using SonnetDB.Query;

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
/// <c>CREATE TABLE [IF NOT EXISTS] name (col TYPE [NULL|NOT NULL] [ROWVERSION], ..., PRIMARY KEY (...), FOREIGN KEY (...) REFERENCES ... (...))</c>。
/// </summary>
/// <param name="Name">关系表名称。</param>
/// <param name="Columns">列定义（按声明顺序）。</param>
/// <param name="PrimaryKey">主键列名（按声明顺序）。</param>
/// <param name="IfNotExists">是否带 <c>IF NOT EXISTS</c> 修饰；为 <c>true</c> 时同名表已存在则视为成功。</param>
/// <param name="ForeignKeys">表级外键声明。</param>
public sealed record CreateTableStatement(
    string Name,
    IReadOnlyList<TableColumnDefinition> Columns,
    IReadOnlyList<string> PrimaryKey,
    bool IfNotExists = false,
    IReadOnlyList<TableForeignKeyClause>? ForeignKeys = null) : SqlStatement
{
    /// <summary>当前表级外键声明。</summary>
    public IReadOnlyList<TableForeignKeyClause> ForeignKeyClauses { get; } = ForeignKeys ?? Array.Empty<TableForeignKeyClause>();
}

/// <summary>外键 ON DELETE 引用动作（v1：NoAction / Cascade）。</summary>
public enum ForeignKeyAction
{
    /// <summary>缺省：父表删除时若有子行引用则拒绝（与 RESTRICT 等价）。</summary>
    NoAction,
    /// <summary>父表删除时将引用的子行一并删除。</summary>
    Cascade,
}

/// <summary>关系表表级外键声明。</summary>
/// <param name="Columns">本表外键列名。</param>
/// <param name="PrincipalTable">被引用表名。</param>
/// <param name="PrincipalColumns">被引用列名；第一版要求等于被引用表主键。</param>
/// <param name="OnDelete">ON DELETE 动作；缺省 NoAction。</param>
public sealed record TableForeignKeyClause(
    IReadOnlyList<string> Columns,
    string PrincipalTable,
    IReadOnlyList<string> PrincipalColumns,
    ForeignKeyAction OnDelete = ForeignKeyAction.NoAction);

/// <summary>
/// <c>CREATE DOCUMENT COLLECTION [IF NOT EXISTS] name</c>。
/// </summary>
/// <param name="Name">文档集合名称。</param>
/// <param name="IfNotExists">是否带 <c>IF NOT EXISTS</c> 修饰；为 <c>true</c> 时同名集合已存在则视为成功。</param>
public sealed record CreateDocumentCollectionStatement(
    string Name,
    bool IfNotExists = false) : SqlStatement;

/// <summary>
/// <c>CREATE [UNIQUE] INDEX [IF NOT EXISTS] index_name ON table_name (col, ...)</c>。
/// </summary>
/// <param name="IndexName">索引名。</param>
/// <param name="TableName">目标表名。</param>
/// <param name="Columns">索引列名。</param>
/// <param name="IsUnique">是否为唯一索引。</param>
/// <param name="IfNotExists">索引已存在时是否视为成功。</param>
/// <param name="DocumentOptions">文档集合索引专用选项；关系表索引执行时忽略。</param>
public sealed record CreateTableIndexStatement(
    string IndexName,
    string TableName,
    IReadOnlyList<string> Columns,
    bool IsUnique,
    bool IfNotExists = false,
    DocumentIndexOptions? DocumentOptions = null) : SqlStatement;

/// <summary>
/// 普通 <c>CREATE INDEX</c> 用于文档集合时的专用选项。
/// </summary>
/// <param name="IsSparse">是否为 sparse index。</param>
/// <param name="TtlSeconds">TTL 保留秒数。</param>
/// <param name="PartialFilter">partial index 过滤条件。</param>
public sealed record DocumentIndexOptions(
    bool IsSparse = false,
    long? TtlSeconds = null,
    SqlExpression? PartialFilter = null);

/// <summary>
/// <c>CREATE [UNIQUE] [SPARSE] [TTL] INDEX [IF NOT EXISTS] index_name ON collection_name ('$.path', ...)</c>。
/// </summary>
/// <param name="IndexName">索引名。</param>
/// <param name="CollectionName">目标文档集合名。</param>
/// <param name="Paths">JSON path 列表。</param>
/// <param name="IsUnique">是否为唯一索引。</param>
/// <param name="IsSparse">是否为 sparse 索引。</param>
/// <param name="TtlSeconds">TTL 保留秒数。</param>
/// <param name="PartialFilter">partial index 过滤条件。</param>
/// <param name="IfNotExists">索引已存在时是否视为成功。</param>
public sealed record CreateDocumentIndexStatement(
    string IndexName,
    string CollectionName,
    IReadOnlyList<string> Paths,
    bool IsUnique = false,
    bool IsSparse = false,
    long? TtlSeconds = null,
    SqlExpression? PartialFilter = null,
    bool IfNotExists = false) : SqlStatement;

/// <summary>
/// <c>CREATE JSON INDEX [IF NOT EXISTS] index_name ON collection_name ('$.path')</c>。
/// </summary>
/// <param name="IndexName">索引名。</param>
/// <param name="CollectionName">目标文档集合名。</param>
/// <param name="Path">JSON path 表达式。</param>
/// <param name="IfNotExists">索引已存在时是否视为成功。</param>
public sealed record CreateDocumentPathIndexStatement(
    string IndexName,
    string CollectionName,
    string Path,
    bool IfNotExists = false) : SqlStatement;

/// <summary>
/// <c>CREATE JSON INDEX [IF NOT EXISTS] index_name ON table_name (json_col, '$.path')</c>。
/// </summary>
/// <param name="IndexName">索引名。</param>
/// <param name="TableName">目标关系表名。</param>
/// <param name="JsonColumnName">JSON 列名。</param>
/// <param name="Path">JSON path 表达式。</param>
/// <param name="IfNotExists">索引已存在时是否视为成功。</param>
public sealed record CreateTableJsonPathIndexStatement(
    string IndexName,
    string TableName,
    string JsonColumnName,
    string Path,
    bool IfNotExists = false) : SqlStatement;

/// <summary>
/// <c>IMPORT JSON 'file.json' INTO target [FORMAT AUTO|ARRAY|LINES] [ID PATH '$.id']</c>。
/// </summary>
public sealed record ImportJsonStatement(
    string FilePath,
    string TargetName,
    JsonImportFormat Format = JsonImportFormat.Auto,
    string? IdPath = null) : SqlStatement;

/// <summary>JSON 文件导入格式。</summary>
public enum JsonImportFormat
{
    /// <summary>自动识别 JSON array / JSON Lines / 单对象。</summary>
    Auto,
    /// <summary>顶层 JSON array。</summary>
    Array,
    /// <summary>JSON Lines / NDJSON。</summary>
    Lines,
}

/// <summary>
/// <c>CREATE FULLTEXT INDEX [IF NOT EXISTS] index_name ON collection_name (field, ...) [USING tokenizer]</c>。
/// </summary>
/// <param name="IndexName">索引名。</param>
/// <param name="CollectionName">目标文档集合名。</param>
/// <param name="Fields">需要写入全文索引的文档伪字段或 JSON path 字段。</param>
/// <param name="Tokenizer">分词器名称，支持 <c>unicode</c> / <c>cjk</c> / <c>jieba</c>。</param>
/// <param name="IfNotExists">索引已存在时是否视为成功。</param>
public sealed record CreateFullTextIndexStatement(
    string IndexName,
    string CollectionName,
    IReadOnlyList<string> Fields,
    string Tokenizer,
    bool IfNotExists = false) : SqlStatement;

/// <summary>关系表列定义。</summary>
/// <param name="Name">列名。</param>
/// <param name="DataType">列数据类型。</param>
/// <param name="Nullability">列空值修饰符；主键列执行时始终强制为 NOT NULL。</param>
/// <param name="IsRowVersion">是否为乐观并发版本列；执行层自动维护。</param>
public sealed record TableColumnDefinition(
    string Name,
    SqlDataType DataType,
    ColumnNullability Nullability = ColumnNullability.Unspecified,
    bool IsRowVersion = false);

/// <summary>
/// <c>ALTER TABLE table ADD COLUMN col TYPE [NULL|NOT NULL] [DEFAULT expr]</c>。
/// </summary>
public sealed record AlterTableAddColumnStatement(
    string TableName,
    string ColumnName,
    SqlDataType DataType,
    ColumnNullability Nullability = ColumnNullability.Unspecified,
    SqlExpression? DefaultExpression = null) : SqlStatement;

/// <summary>
/// <c>ALTER TABLE table DROP COLUMN [IF EXISTS] col</c>。
/// </summary>
/// <param name="TableName">目标关系表名称。</param>
/// <param name="ColumnName">目标列名。</param>
/// <param name="IfExists">是否带 <c>IF EXISTS</c> 修饰；为 <c>true</c> 时列不存在视为成功。</param>
public sealed record AlterTableDropColumnStatement(string TableName, string ColumnName, bool IfExists = false) : SqlStatement;

/// <summary>
/// <c>ALTER TABLE table DROP CONSTRAINT constraint</c>。
/// </summary>
public sealed record AlterTableDropConstraintStatement(string TableName, string ConstraintName) : SqlStatement;

/// <summary>
/// <c>ALTER TABLE table RENAME COLUMN old TO new</c>。
/// </summary>
public sealed record AlterTableRenameColumnStatement(string TableName, string OldColumnName, string NewColumnName) : SqlStatement;

/// <summary>
/// <c>ALTER TABLE old RENAME TO new</c>。
/// </summary>
public sealed record AlterTableRenameTableStatement(string OldTableName, string NewTableName) : SqlStatement;

/// <summary>
/// <c>ALTER DOCUMENT COLLECTION name SET VALIDATOR '{...}' [VALIDATION ACTION ERROR|WARN]</c>。
/// </summary>
/// <param name="CollectionName">目标文档集合名。</param>
/// <param name="ValidatorJson">validator JSON 定义。</param>
/// <param name="ValidationAction">校验失败动作；为空时使用 validator JSON 或默认 error。</param>
public sealed record AlterDocumentCollectionSetValidatorStatement(
    string CollectionName,
    string ValidatorJson,
    string? ValidationAction = null) : SqlStatement;

/// <summary>
/// <c>ALTER DOCUMENT COLLECTION name DROP VALIDATOR</c>。
/// </summary>
/// <param name="CollectionName">目标文档集合名。</param>
public sealed record AlterDocumentCollectionDropValidatorStatement(string CollectionName) : SqlStatement;

/// <summary>
/// 向量索引声明抽象基类。
/// </summary>
/// <param name="Metric">
/// 距离度量（<c>metric=cosine|l2|inner_product</c>），默认 cosine；决定建图度量与 ANN gate（#223）。
/// </param>
public abstract record VectorIndexSpec(KnnMetric Metric);

/// <summary>
/// HNSW 向量索引声明：<c>WITH INDEX hnsw(m=16, ef=200[, ef_construction=200][, metric=cosine])</c>。
/// </summary>
/// <param name="M">每个节点在每层保留的最大邻接数。</param>
/// <param name="Ef">检索时（efSearch）使用的候选规模。</param>
/// <param name="EfConstruction">建图时（efConstruction）使用的候选规模，与 <paramref name="Ef"/> 解耦（#223 / I9）。</param>
/// <param name="Metric">距离度量。</param>
public sealed record HnswVectorIndexSpec(int M, int Ef, int EfConstruction, KnnMetric Metric) : VectorIndexSpec(Metric);

/// <summary>
/// IVF-Flat 向量索引声明：<c>WITH INDEX ivf(nlist=64, nprobe=8, max_iterations=25[, metric=cosine])</c>。
/// </summary>
public sealed record IvfVectorIndexSpec(int NList, int NProbe, int MaxIterations, KnnMetric Metric) : VectorIndexSpec(Metric);

/// <summary>
/// IVF-PQ 向量索引声明：<c>WITH INDEX ivf_pq(nlist=64, nprobe=8, m=8, nbits=8[, metric=cosine])</c>。
/// </summary>
public sealed record IvfPqVectorIndexSpec(int NList, int NProbe, int MaxIterations, int M, int NBits, KnnMetric Metric) : VectorIndexSpec(Metric);

/// <summary>
/// Vamana / DiskANN 向量索引声明：<c>WITH INDEX vamana(max_degree=32, search_list_size=75, alpha=1.2, beam_width=4[, metric=cosine])</c>。
/// </summary>
public sealed record VamanaVectorIndexSpec(int MaxDegree, int SearchListSize, float Alpha, int BeamWidth, KnnMetric Metric) : VectorIndexSpec(Metric);

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
/// <c>SELECT projections FROM measurement [JOIN table ON expr] [WHERE expr] [GROUP BY expr, ...]</c>。
/// </summary>
/// <param name="Projections">投影列表，可包含 <c>*</c> / 函数 / 列引用。</param>
/// <param name="Measurement">目标 measurement 名称（FROM 是 TVF 时为 TVF 推断的 source measurement，例如 <c>forecast(meter, ...)</c> → <c>meter</c>）。</param>
/// <param name="Where">可选 WHERE 表达式。</param>
/// <param name="GroupBy">GROUP BY 表达式列表；当未指定 GROUP BY 时为空集合（不为 <c>null</c>）。</param>
/// <param name="Having">可选 HAVING 表达式；在聚合分组之后过滤组，可以引用聚合函数（仅在存在 GROUP BY 或聚合投影时有效）。</param>
/// <param name="TableValuedFunction">FROM 子句若为表值函数调用（PR #55 起的 forecast 等）则非 <c>null</c>，否则 <c>null</c>。</param>
/// <param name="Pagination">可选分页子句；支持 <c>OFFSET/FETCH</c> 与兼容语法 <c>LIMIT</c>。</param>
/// <param name="OrderBy">可选排序子句；为兼容旧调用方，指向 <paramref name="OrderByItems"/> 的第一项。</param>
/// <param name="OrderByItems">完整排序列表；measurement 执行层当前使用第一项，关系表执行层支持多列结果列名排序。</param>
/// <param name="TableAlias">FROM 子句声明的可选单表别名。</param>
/// <param name="Join">可选 JOIN 子句；兼容旧调用方，等价于 <see cref="Joins"/> 第一项。</param>
/// <param name="FromSubquery">FROM 子句若为子查询则非 <c>null</c>。</param>
/// <param name="Joins">JOIN 子句列表；为空集合时表示无 JOIN。</param>
public sealed record SelectStatement(
    IReadOnlyList<SelectItem> Projections,
    string Measurement,
    SqlExpression? Where,
    IReadOnlyList<SqlExpression> GroupBy,
    FunctionCallExpression? TableValuedFunction = null,
    PaginationSpec? Pagination = null,
    OrderBySpec? OrderBy = null,
    string? TableAlias = null,
    JoinClause? Join = null,
    SelectStatement? FromSubquery = null,
    IReadOnlyList<JoinClause>? Joins = null,
    SqlExpression? Having = null,
    IReadOnlyList<OrderBySpec>? OrderByItems = null,
    bool Distinct = false) : SqlStatement
{
    /// <summary>当前 SELECT 的 JOIN 列表。</summary>
    public IReadOnlyList<JoinClause> JoinClauses { get; } =
        Joins ?? (Join is null ? Array.Empty<JoinClause>() : new[] { Join });

    /// <summary>当前 SELECT 的完整 ORDER BY 列表。</summary>
    public IReadOnlyList<OrderBySpec> OrderByList { get; } =
        OrderByItems ?? (OrderBy is null ? Array.Empty<OrderBySpec>() : new[] { OrderBy });
}

/// <summary>
/// <c>[INNER|LEFT] JOIN table [AS] alias ON expr</c> 子句。
/// </summary>
/// <param name="TableName">被 JOIN 的关系表名。</param>
/// <param name="Alias">关系表别名；未显式声明时为 <paramref name="TableName"/>。</param>
/// <param name="On">ON 条件表达式；MM4 第一版要求是 measurement tag 与 table 列之间的等值比较。</param>
public sealed record JoinClause(
    string TableName,
    string Alias,
    SqlExpression On,
    SelectStatement? Subquery = null,
    JoinKind Kind = JoinKind.Inner);

/// <summary>JOIN 类型。</summary>
public enum JoinKind
{
    /// <summary>内连接。</summary>
    Inner,
    /// <summary>左外连接。</summary>
    Left
}

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
/// <c>DROP TABLE [IF EXISTS] name</c>：删除关系表 schema 与 rowstore。
/// </summary>
/// <param name="Name">目标关系表名称。</param>
/// <param name="IfExists">是否带 <c>IF EXISTS</c> 修饰；为 <c>true</c> 时表不存在视为成功（0 行受影响），否则报错。</param>
public sealed record DropTableStatement(string Name, bool IfExists = false) : SqlStatement;

/// <summary>
/// <c>DROP MEASUREMENT [IF EXISTS] name</c>：删除 measurement schema、series catalog 与已落盘时序数据。
/// </summary>
/// <param name="Name">目标 measurement 名称。</param>
/// <param name="IfExists">是否带 <c>IF EXISTS</c> 修饰；为 <c>true</c> 时 measurement 不存在视为成功（0 行受影响）。</param>
public sealed record DropMeasurementStatement(string Name, bool IfExists = false) : SqlStatement;

/// <summary>
/// <c>DROP DOCUMENT COLLECTION name</c>：删除文档集合 schema 与主数据。
/// </summary>
/// <param name="Name">目标文档集合名称。</param>
public sealed record DropDocumentCollectionStatement(string Name) : SqlStatement;

/// <summary>
/// <c>DROP INDEX index_name ON table_name</c>：删除关系表二级索引声明。
/// </summary>
/// <param name="IndexName">索引名。</param>
/// <param name="TableName">表名。</param>
public sealed record DropTableIndexStatement(string IndexName, string TableName) : SqlStatement;

/// <summary>
/// <c>DROP JSON INDEX index_name ON collection_name</c>：删除文档集合 JSON path 索引声明。
/// </summary>
/// <param name="IndexName">索引名。</param>
/// <param name="CollectionName">集合名。</param>
public sealed record DropDocumentPathIndexStatement(string IndexName, string CollectionName) : SqlStatement;

/// <summary>
/// <c>DROP FULLTEXT INDEX index_name ON collection_name</c>：删除文档集合全文索引声明和派生索引目录。
/// </summary>
/// <param name="IndexName">索引名。</param>
/// <param name="CollectionName">集合名。</param>
public sealed record DropFullTextIndexStatement(string IndexName, string CollectionName) : SqlStatement;

/// <summary><c>BEGIN</c>：开始当前执行器作用域内的轻事务。</summary>
public sealed record BeginTransactionStatement : SqlStatement;

/// <summary><c>COMMIT</c>：提交当前轻事务。</summary>
public sealed record CommitTransactionStatement : SqlStatement;

/// <summary><c>ROLLBACK</c>：回滚当前轻事务。</summary>
public sealed record RollbackTransactionStatement : SqlStatement;

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
/// <c>SHOW DOCUMENT COLLECTIONS</c>：列出当前数据库中所有 JSON 文档集合。
/// </summary>
public sealed record ShowDocumentCollectionsStatement : SqlStatement;

/// <summary>
/// <c>SHOW INDEXES ON table</c>：列出指定关系表的二级索引。
/// </summary>
/// <param name="TableName">目标关系表名称。</param>
public sealed record ShowTableIndexesStatement(string TableName) : SqlStatement;

/// <summary>
/// <c>SHOW JSON INDEXES ON collection</c>：列出指定文档集合的 JSON path 索引。
/// </summary>
/// <param name="CollectionName">目标文档集合名称。</param>
public sealed record ShowDocumentIndexesStatement(string CollectionName) : SqlStatement;

/// <summary>
/// <c>SHOW FULLTEXT INDEXES ON collection</c>：列出指定文档集合的全文索引。
/// </summary>
/// <param name="CollectionName">目标文档集合名称。</param>
public sealed record ShowFullTextIndexesStatement(string CollectionName) : SqlStatement;

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
/// <c>DESCRIBE DOCUMENT COLLECTION &lt;name&gt;</c>：描述指定文档集合。
/// </summary>
/// <param name="Name">目标文档集合名称。</param>
public sealed record DescribeDocumentCollectionStatement(string Name) : SqlStatement;

/// <summary>
/// <c>EXPLAIN &lt;read-only statement&gt;</c>：对只读语句返回估算扫描与命中统计。
/// 当前仅支持 <c>SELECT</c>、<c>SHOW MEASUREMENTS</c> / <c>SHOW TABLES</c> 与 <c>DESCRIBE [MEASUREMENT|TABLE]</c>。
/// </summary>
/// <param name="Statement">被解释的只读语句。</param>
public sealed record ExplainStatement(SqlStatement Statement) : SqlStatement;
