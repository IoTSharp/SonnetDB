using SonnetDB.Documents;
using SonnetDB.Sql.Ast;

namespace SonnetDB.ObjectStorage
{
    /// <summary>保留已发布的对象读取构造和解构入口。</summary>
    public sealed partial record SndbObjectReadResult
    {
        /// <summary>按 4.0 五参数合同创建读取结果；完整长度保持未知。</summary>
        /// <param name="Info">对象元数据。</param>
        /// <param name="Content">内容流。</param>
        /// <param name="Offset">读取偏移。</param>
        /// <param name="Length">读取长度。</param>
        /// <param name="IsRange">是否范围读取。</param>
        public SndbObjectReadResult(SndbObjectInfo Info, Stream Content, long Offset, long Length, bool IsRange)
            : this(Info, Content, Offset, Length, IsRange, TotalLength: 0) { }

        /// <summary>按已发布的五字段合同解构读取结果。</summary>
        /// <param name="info">对象元数据。</param>
        /// <param name="content">内容流。</param>
        /// <param name="offset">读取偏移。</param>
        /// <param name="length">读取长度。</param>
        /// <param name="isRange">是否范围读取。</param>
        public void Deconstruct(out SndbObjectInfo info, out Stream content, out long offset, out long length, out bool isRange)
            => (info, content, offset, length, isRange) = (Info, Content, Offset, Length, IsRange);
    }
}

namespace SonnetDB.Sql.Ast
{
    /// <summary>保留已发布的建表语句构造和解构入口。</summary>
    public sealed partial record CreateTableStatement
    {
        /// <summary>按 4.0 五参数合同创建建表语句；检查约束使用现有空值默认。</summary>
        /// <param name="Name">表名。</param>
        /// <param name="Columns">列定义。</param>
        /// <param name="PrimaryKey">主键列名。</param>
        /// <param name="IfNotExists">是否允许表已存在。</param>
        /// <param name="ForeignKeys">外键声明。</param>
        public CreateTableStatement(string Name, IReadOnlyList<TableColumnDefinition> Columns,
            IReadOnlyList<string> PrimaryKey, bool IfNotExists, IReadOnlyList<TableForeignKeyClause>? ForeignKeys)
            : this(Name, Columns, PrimaryKey, IfNotExists, ForeignKeys, CheckConstraints: null) { }

        /// <summary>按已发布的五字段合同解构建表语句。</summary>
        /// <param name="name">表名。</param>
        /// <param name="columns">列定义。</param>
        /// <param name="primaryKey">主键列名。</param>
        /// <param name="ifNotExists">是否允许表已存在。</param>
        /// <param name="foreignKeys">外键声明。</param>
        public void Deconstruct(out string name, out IReadOnlyList<TableColumnDefinition> columns,
            out IReadOnlyList<string> primaryKey, out bool ifNotExists, out IReadOnlyList<TableForeignKeyClause>? foreignKeys)
            => (name, columns, primaryKey, ifNotExists, foreignKeys) = (Name, Columns, PrimaryKey, IfNotExists, ForeignKeys);
    }

    /// <summary>保留已发布的查询语句构造和解构入口。</summary>
    public sealed partial record SelectStatement
    {
        /// <summary>按 4.0 十四参数合同创建查询语句；不添加集合运算分支。</summary>
        /// <param name="Projections">投影列表。</param>
        /// <param name="Measurement">数据源名。</param>
        /// <param name="Where">筛选表达式。</param>
        /// <param name="GroupBy">分组表达式。</param>
        /// <param name="TableValuedFunction">表值函数。</param>
        /// <param name="Pagination">分页子句。</param>
        /// <param name="OrderBy">首项排序。</param>
        /// <param name="TableAlias">表别名。</param>
        /// <param name="Join">首项连接。</param>
        /// <param name="FromSubquery">数据源子查询。</param>
        /// <param name="Joins">完整连接列表。</param>
        /// <param name="Having">分组筛选。</param>
        /// <param name="OrderByItems">完整排序列表。</param>
        /// <param name="Distinct">是否去重。</param>
        public SelectStatement(IReadOnlyList<SelectItem> Projections, string Measurement, SqlExpression? Where,
            IReadOnlyList<SqlExpression> GroupBy, FunctionCallExpression? TableValuedFunction, PaginationSpec? Pagination,
            OrderBySpec? OrderBy, string? TableAlias, JoinClause? Join, SelectStatement? FromSubquery,
            IReadOnlyList<JoinClause>? Joins, SqlExpression? Having, IReadOnlyList<OrderBySpec>? OrderByItems, bool Distinct)
            : this(Projections, Measurement, Where, GroupBy, TableValuedFunction, Pagination, OrderBy, TableAlias,
                Join, FromSubquery, Joins, Having, OrderByItems, Distinct, Unions: null)
        { }

        /// <summary>按已发布的十四字段合同解构查询语句。</summary>
        /// <param name="projections">投影列表。</param>
        /// <param name="measurement">数据源名。</param>
        /// <param name="where">筛选表达式。</param>
        /// <param name="groupBy">分组表达式。</param>
        /// <param name="tableValuedFunction">表值函数。</param>
        /// <param name="pagination">分页子句。</param>
        /// <param name="orderBy">首项排序。</param>
        /// <param name="tableAlias">表别名。</param>
        /// <param name="join">首项连接。</param>
        /// <param name="fromSubquery">数据源子查询。</param>
        /// <param name="joins">完整连接列表。</param>
        /// <param name="having">分组筛选。</param>
        /// <param name="orderByItems">完整排序列表。</param>
        /// <param name="distinct">是否去重。</param>
        public void Deconstruct(out IReadOnlyList<SelectItem> projections, out string measurement, out SqlExpression? where,
            out IReadOnlyList<SqlExpression> groupBy, out FunctionCallExpression? tableValuedFunction,
            out PaginationSpec? pagination, out OrderBySpec? orderBy, out string? tableAlias, out JoinClause? join,
            out SelectStatement? fromSubquery, out IReadOnlyList<JoinClause>? joins, out SqlExpression? having,
            out IReadOnlyList<OrderBySpec>? orderByItems, out bool distinct)
            => (projections, measurement, where, groupBy, tableValuedFunction, pagination, orderBy, tableAlias,
                join, fromSubquery, joins, having, orderByItems, distinct) =
                (Projections, Measurement, Where, GroupBy, TableValuedFunction, Pagination, OrderBy, TableAlias,
                    Join, FromSubquery, Joins, Having, OrderByItems, Distinct);
    }
}

namespace SonnetDB.Sql.Execution
{
    /// <summary>保留已发布的扫描估算构造和解构入口。</summary>
    public sealed partial record SqlExplainExecutionResult
    {
        /// <summary>按 4.0 十四参数合同创建扫描估算；不添加扫描筛选文本。</summary>
        /// <param name="Database">数据库名。</param>
        /// <param name="StatementType">语句类型。</param>
        /// <param name="Measurement">数据源名。</param>
        /// <param name="MatchedSeriesCount">匹配序列数。</param>
        /// <param name="EstimatedSegmentCount">估算段数。</param>
        /// <param name="EstimatedBlockCount">估算块数。</param>
        /// <param name="EstimatedScannedRows">估算扫描行数。</param>
        /// <param name="EstimatedMemTableRows">估算内存行数。</param>
        /// <param name="EstimatedSegmentRows">估算段行数。</param>
        /// <param name="HasTimeFilter">是否存在时间筛选。</param>
        /// <param name="TagFilterCount">标签筛选数。</param>
        /// <param name="AccessPath">访问路径。</param>
        /// <param name="IndexName">索引名。</param>
        /// <param name="DocumentPlan">文档查询计划。</param>
        public SqlExplainExecutionResult(string? Database, string StatementType, string? Measurement,
            int MatchedSeriesCount, int EstimatedSegmentCount, int EstimatedBlockCount, long EstimatedScannedRows,
            long EstimatedMemTableRows, long EstimatedSegmentRows, bool HasTimeFilter, int TagFilterCount,
            string? AccessPath, string? IndexName, DocumentQueryPlan? DocumentPlan)
            : this(Database, StatementType, Measurement, MatchedSeriesCount, EstimatedSegmentCount, EstimatedBlockCount,
                EstimatedScannedRows, EstimatedMemTableRows, EstimatedSegmentRows, HasTimeFilter, TagFilterCount,
                AccessPath, IndexName, DocumentPlan, ScanFilter: null)
        { }

        /// <summary>按已发布的十四字段合同解构扫描估算。</summary>
        /// <param name="database">数据库名。</param>
        /// <param name="statementType">语句类型。</param>
        /// <param name="measurement">数据源名。</param>
        /// <param name="matchedSeriesCount">匹配序列数。</param>
        /// <param name="estimatedSegmentCount">估算段数。</param>
        /// <param name="estimatedBlockCount">估算块数。</param>
        /// <param name="estimatedScannedRows">估算扫描行数。</param>
        /// <param name="estimatedMemTableRows">估算内存行数。</param>
        /// <param name="estimatedSegmentRows">估算段行数。</param>
        /// <param name="hasTimeFilter">是否存在时间筛选。</param>
        /// <param name="tagFilterCount">标签筛选数。</param>
        /// <param name="accessPath">访问路径。</param>
        /// <param name="indexName">索引名。</param>
        /// <param name="documentPlan">文档查询计划。</param>
        public void Deconstruct(out string? database, out string statementType, out string? measurement,
            out int matchedSeriesCount, out int estimatedSegmentCount, out int estimatedBlockCount,
            out long estimatedScannedRows, out long estimatedMemTableRows, out long estimatedSegmentRows,
            out bool hasTimeFilter, out int tagFilterCount, out string? accessPath, out string? indexName,
            out DocumentQueryPlan? documentPlan)
            => (database, statementType, measurement, matchedSeriesCount, estimatedSegmentCount, estimatedBlockCount,
                estimatedScannedRows, estimatedMemTableRows, estimatedSegmentRows, hasTimeFilter, tagFilterCount,
                accessPath, indexName, documentPlan) =
                (Database, StatementType, Measurement, MatchedSeriesCount, EstimatedSegmentCount, EstimatedBlockCount,
                    EstimatedScannedRows, EstimatedMemTableRows, EstimatedSegmentRows, HasTimeFilter, TagFilterCount,
                    AccessPath, IndexName, DocumentPlan);
    }
}
