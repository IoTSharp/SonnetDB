using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql;

/// <summary>
/// 递归下降 SQL 语法分析器：把 token 流转换为 <see cref="SqlStatement"/> AST。
/// </summary>
/// <remarks>
/// 支持的语句：<c>CREATE MEASUREMENT</c> / <c>INSERT INTO ... VALUES</c> /
/// <c>SELECT ... FROM ... [WHERE ...] [GROUP BY time(...)]</c> / <c>DELETE FROM ... WHERE ...</c> /
/// <c>CREATE TABLE</c> / <c>UPDATE</c> 等关系表 MVP 语句。
/// 不做任何语义校验（measurement / column 是否存在留给执行层）。
/// </remarks>
public sealed class SqlParser
{
    private readonly IReadOnlyList<Token> _tokens;
    private int _index;
    private int _parameterOrdinal;

    /// <summary>
    /// 表达式递归下降的深度上限。超过即抛 <see cref="SqlParseException"/>，
    /// 杜绝深层括号 / <c>NOT NOT NOT…</c> / <c>------x</c> / 嵌套函数调用触发不可捕获的
    /// <see cref="StackOverflowException"/>（.NET 中 SO 会直接终止整个宿主进程，无法 catch）。
    /// </summary>
    private const int MaxExpressionDepth = 200;

    private int _expressionDepth;

    /// <summary>构造解析器实例。</summary>
    /// <param name="tokens">已经词法化的 token 序列（必须以 EOF 结尾）。</param>
    public SqlParser(IReadOnlyList<Token> tokens)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (tokens.Count == 0 || tokens[^1].Kind != TokenKind.EndOfFile)
            throw new ArgumentException("token 序列必须以 EndOfFile 结尾。", nameof(tokens));
        _tokens = tokens;
        _index = 0;
    }

    /// <summary>
    /// 已解析单语句 AST 的进程级有界 LRU 缓存（#212）。解析与 schema 无关且 AST 不可变，
    /// 按 SQL 文本缓存并复用是安全的；高频轮询同一 query 形状可跳过重复 lex+parse。
    /// </summary>
    private static readonly SqlParseCache ParseCache = new(capacity: 512);

    /// <summary>
    /// 解析单条 SQL 语句（支持末尾分号）。命中进程级解析缓存时直接返回已缓存的不可变 AST。
    /// </summary>
    /// <param name="source">SQL 源文本。</param>
    /// <returns>解析得到的语句 AST。</returns>
    /// <exception cref="SqlParseException">词法或语法错误时抛出。</exception>
    public static SqlStatement Parse(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return ParseCache.GetOrParse(source, ParseUncached);
    }

    /// <summary>清空进程级解析缓存（仅供测试）。</summary>
    internal static void ClearParseCache() => ParseCache.Clear();

    private static SqlStatement ParseUncached(string source)
    {
        var tokens = SqlLexer.Tokenize(source);
        var parser = new SqlParser(tokens);
        var statement = parser.ParseStatement();
        parser.ConsumeOptionalSemicolon();
        parser.ExpectEndOfFile();
        return statement;
    }

    /// <summary>解析 1 ~ N 条以分号分隔的语句（末尾分号可选）。</summary>
    /// <param name="source">SQL 源文本。</param>
    /// <returns>语句列表。</returns>
    public static IReadOnlyList<SqlStatement> ParseScript(string source)
    {
        var tokens = SqlLexer.Tokenize(source);
        var parser = new SqlParser(tokens);
        var list = new List<SqlStatement>();
        while (parser.Current.Kind != TokenKind.EndOfFile)
        {
            list.Add(parser.ParseStatement());
            parser.ConsumeOptionalSemicolon();
        }
        return list;
    }

    /// <summary>解析下一条语句。</summary>
    public SqlStatement ParseStatement()
    {
        return Current.Kind switch
        {
            TokenKind.KeywordCreate => ParseCreate(),
            TokenKind.KeywordBegin => ParseBegin(),
            TokenKind.KeywordCommit => ParseCommit(),
            TokenKind.KeywordRollback => ParseRollback(),
            TokenKind.KeywordInsert => ParseInsert(),
            TokenKind.KeywordImport => ParseImport(),
            TokenKind.KeywordSelect => ParseSelect(),
            TokenKind.KeywordDelete => ParseDelete(),
            TokenKind.KeywordUpdate => ParseUpdate(),
            TokenKind.KeywordDrop => ParseDrop(),
            TokenKind.KeywordAlter => ParseAlter(),
            TokenKind.KeywordGrant => ParseGrant(),
            TokenKind.KeywordRevoke => ParseRevoke(),
            TokenKind.KeywordShow => ParseShow(),
            TokenKind.KeywordExplain => ParseExplain(),
            TokenKind.KeywordIssue => ParseIssue(),
            TokenKind.KeywordDescribe => ParseDescribe(),
            TokenKind.KeywordDesc => ParseDescribe(),
            _ => throw Error("期望 CREATE / INSERT / IMPORT / SELECT / DELETE / UPDATE / DROP / ALTER / GRANT / REVOKE / SHOW / EXPLAIN / ISSUE / DESCRIBE / BEGIN / COMMIT / ROLLBACK 关键字"),
        };
    }

    // ── CREATE 分发：MEASUREMENT / USER / DATABASE ─────────────────────────

    private SqlStatement ParseCreate()
    {
        Expect(TokenKind.KeywordCreate);
        var unique = false;
        if (Current.Kind == TokenKind.KeywordUnique || IsIdentifier("unique"))
        {
            unique = true;
            Advance();
        }

        var sparse = false;
        var ttl = false;
        while (Current.Kind is TokenKind.KeywordSparse or TokenKind.KeywordTtl || IsIdentifier("sparse") || IsIdentifier("ttl"))
        {
            if (Current.Kind == TokenKind.KeywordSparse || IsIdentifier("sparse"))
                sparse = true;
            else
                ttl = true;
            Advance();
        }

        if (IsIndexKeyword())
            return ParseCreateIndexBody(unique, sparse, ttl);

        return Current.Kind switch
        {
            TokenKind.KeywordMeasurement => ParseCreateMeasurementBody(),
            TokenKind.KeywordTable => ParseCreateTableBody(),
            TokenKind.KeywordDocument => ParseCreateDocumentBody(),
            TokenKind.KeywordJson => ParseCreateJsonBody(),
            TokenKind.KeywordFullText => ParseCreateFullTextBody(),
            TokenKind.KeywordUser => ParseCreateUserBody(),
            TokenKind.KeywordDatabase => ParseCreateDatabaseBody(),
            _ => throw Error("CREATE 后面期望 MEASUREMENT / TABLE / DOCUMENT COLLECTION / JSON INDEX / FULLTEXT INDEX / INDEX / USER / DATABASE"),
        };
    }

    private CreateTableIndexStatement ParseCreateIndexBody(bool unique, bool sparse, bool ttl)
    {
        ExpectIndexKeyword("CREATE 后面期望 INDEX");

        var ifNotExists = false;
        if (Current.Kind == TokenKind.KeywordIf)
        {
            Advance();
            Expect(TokenKind.KeywordNot);
            Expect(TokenKind.KeywordExists);
            ifNotExists = true;
        }

        var indexName = ExpectIdentifierName();
        Expect(TokenKind.KeywordOn);
        var tableName = ExpectIdentifierName();
        Expect(TokenKind.LeftParen);
        var columns = new List<string> { ExpectIndexColumnOrPath() };
        while (Current.Kind == TokenKind.Comma)
        {
            Advance();
            columns.Add(ExpectIndexColumnOrPath());
        }
        Expect(TokenKind.RightParen);

        long? ttlSeconds = null;
        if (ttl)
            ttlSeconds = ParseOptionalTtlSeconds();

        SqlExpression? partialFilter = null;
        if (Current.Kind == TokenKind.KeywordWhere)
        {
            Advance();
            partialFilter = ParseExpression();
        }

        DocumentIndexOptions? documentOptions = sparse || ttlSeconds is not null || partialFilter is not null
            ? new DocumentIndexOptions(sparse, ttlSeconds, partialFilter)
            : null;
        return new CreateTableIndexStatement(indexName, tableName, columns, unique, ifNotExists, documentOptions);
    }

    private CreateDocumentCollectionStatement ParseCreateDocumentBody()
    {
        Expect(TokenKind.KeywordDocument);
        Expect(TokenKind.KeywordCollection);
        var ifNotExists = ParseOptionalIfNotExists();
        return new CreateDocumentCollectionStatement(ExpectIdentifierName(), ifNotExists);
    }

    private SqlStatement ParseCreateJsonBody()
    {
        Expect(TokenKind.KeywordJson);
        if (Current.Kind == TokenKind.KeywordIndex || IsIdentifier("index"))
        {
            Advance();
        }
        else
        {
            throw Error("CREATE JSON 后面期望 INDEX");
        }

        var ifNotExists = false;
        if (Current.Kind == TokenKind.KeywordIf)
        {
            Advance();
            Expect(TokenKind.KeywordNot);
            Expect(TokenKind.KeywordExists);
            ifNotExists = true;
        }

        var indexName = ExpectIdentifierName();
        Expect(TokenKind.KeywordOn);
        var collectionName = ExpectIdentifierName();
        Expect(TokenKind.LeftParen);
        string? columnName = null;
        string path;
        if (Current.Kind == TokenKind.StringLiteral)
        {
            path = ExpectStringLiteral();
        }
        else
        {
            columnName = ExpectColumnName();
            Expect(TokenKind.Comma);
            path = ExpectStringLiteral();
        }
        Expect(TokenKind.RightParen);
        return columnName is null
            ? new CreateDocumentIndexStatement(indexName, collectionName, [path], IfNotExists: ifNotExists)
            : new CreateTableJsonPathIndexStatement(indexName, collectionName, columnName, path, ifNotExists);
    }

    private CreateFullTextIndexStatement ParseCreateFullTextBody()
    {
        Expect(TokenKind.KeywordFullText);
        ExpectIndexKeyword("CREATE FULLTEXT 后面期望 INDEX");

        var ifNotExists = ParseOptionalIfNotExists();
        var indexName = ExpectIdentifierName();
        Expect(TokenKind.KeywordOn);
        var collectionName = ExpectIdentifierName();
        Expect(TokenKind.LeftParen);
        var fields = new List<string> { ExpectFullTextFieldName() };
        while (Current.Kind == TokenKind.Comma)
        {
            Advance();
            fields.Add(ExpectFullTextFieldName());
        }
        Expect(TokenKind.RightParen);

        var tokenizer = "unicode";
        if (Current.Kind == TokenKind.KeywordUsing)
        {
            Advance();
            tokenizer = ExpectFullTextTokenizerName();
        }

        return new CreateFullTextIndexStatement(indexName, collectionName, fields, tokenizer, ifNotExists);
    }

    private bool ParseOptionalIfNotExists()
    {
        if (Current.Kind != TokenKind.KeywordIf)
            return false;

        Advance();
        Expect(TokenKind.KeywordNot);
        Expect(TokenKind.KeywordExists);
        return true;
    }

    private bool ParseOptionalIfExists()
    {
        if (Current.Kind != TokenKind.KeywordIf)
            return false;

        Advance();
        Expect(TokenKind.KeywordExists);
        return true;
    }

    // ── CREATE MEASUREMENT ─────────────────────────────────────────────────

    private CreateMeasurementStatement ParseCreateMeasurementBody()
    {
        Expect(TokenKind.KeywordMeasurement);

        // 可选的 IF NOT EXISTS 子句：存在时执行幂等创建语义。
        var ifNotExists = false;
        if (Current.Kind == TokenKind.KeywordIf)
        {
            Advance();
            Expect(TokenKind.KeywordNot);
            Expect(TokenKind.KeywordExists);
            ifNotExists = true;
        }

        var name = ExpectIdentifierName();
        Expect(TokenKind.LeftParen);

        var columns = new List<ColumnDefinition>();
        while (true)
        {
            columns.Add(ParseColumnDefinition());
            if (Current.Kind == TokenKind.Comma) { Advance(); continue; }
            break;
        }

        Expect(TokenKind.RightParen);
        return new CreateMeasurementStatement(name, columns, ifNotExists);
    }

    // ── CREATE TABLE ───────────────────────────────────────────────────────

    private CreateTableStatement ParseCreateTableBody()
    {
        Expect(TokenKind.KeywordTable);

        var ifNotExists = false;
        if (Current.Kind == TokenKind.KeywordIf)
        {
            Advance();
            Expect(TokenKind.KeywordNot);
            Expect(TokenKind.KeywordExists);
            ifNotExists = true;
        }

        var name = ExpectIdentifierName();
        Expect(TokenKind.LeftParen);

        var columns = new List<TableColumnDefinition>();
        var primaryKey = new List<string>();
        var foreignKeys = new List<TableForeignKeyClause>();
        while (true)
        {
            if (Current.Kind == TokenKind.KeywordPrimary)
            {
                if (primaryKey.Count > 0)
                    throw Error("PRIMARY KEY 子句重复声明");
                primaryKey.AddRange(ParsePrimaryKeyClause());
            }
            else if (Current.Kind == TokenKind.KeywordForeign)
            {
                foreignKeys.Add(ParseForeignKeyClause());
            }
            else
            {
                columns.Add(ParseTableColumnDefinition());
            }

            if (Current.Kind == TokenKind.Comma)
            {
                Advance();
                continue;
            }

            break;
        }

        Expect(TokenKind.RightParen);
        return new CreateTableStatement(name, columns, primaryKey, ifNotExists, foreignKeys);
    }

    private TableColumnDefinition ParseTableColumnDefinition()
    {
        var columnName = ExpectColumnName();
        var dataType = ParseTableDataType();
        ColumnNullability nullability = ColumnNullability.Unspecified;
        SqlExpression? defaultExpression = null;
        var isRowVersion = false;
        ParseTableColumnModifiers(ref nullability, ref defaultExpression, ref isRowVersion);
        if (defaultExpression is not null)
            throw Error("CREATE TABLE 当前不支持列 DEFAULT；请在后续 ALTER TABLE ADD COLUMN 中使用 DEFAULT");

        if (isRowVersion && dataType != SqlDataType.Int64)
            throw Error("ROWVERSION 列必须使用 INT 类型");

        return new TableColumnDefinition(columnName, dataType, nullability, isRowVersion);
    }

    private AlterTableAddColumnStatement ParseAlterTableAddColumn(string tableName)
    {
        if (Current.Kind == TokenKind.KeywordColumn)
            Advance();

        var columnName = ExpectColumnName();
        var dataType = ParseTableDataType();
        ColumnNullability nullability = ColumnNullability.Unspecified;
        SqlExpression? defaultExpression = null;
        var isRowVersion = false;
        ParseTableColumnModifiers(ref nullability, ref defaultExpression, ref isRowVersion);
        if (isRowVersion)
            throw Error("ALTER TABLE ADD COLUMN 当前不支持新增 ROWVERSION 列");
        return new AlterTableAddColumnStatement(tableName, columnName, dataType, nullability, defaultExpression);
    }

    private void ParseTableColumnModifiers(
        ref ColumnNullability nullability,
        ref SqlExpression? defaultExpression,
        ref bool isRowVersion)
    {
        while (true)
        {
            switch (Current.Kind)
            {
                case TokenKind.KeywordNull:
                    SetNullability(ref nullability, ColumnNullability.Nullable);
                    Advance();
                    continue;

                case TokenKind.KeywordNot:
                    Advance();
                    Expect(TokenKind.KeywordNull);
                    SetNullability(ref nullability, ColumnNullability.NotNull);
                    continue;

                case TokenKind.KeywordDefault:
                    if (defaultExpression is not null)
                        throw Error("DEFAULT 子句重复声明");
                    Advance();
                    defaultExpression = ParseExpression();
                    continue;

                case TokenKind.KeywordRowVersion:
                    if (isRowVersion)
                        throw Error("ROWVERSION 子句重复声明");
                    isRowVersion = true;
                    if (nullability == ColumnNullability.Nullable)
                        throw Error("ROWVERSION 列不允许声明 NULL");
                    if (nullability == ColumnNullability.Unspecified)
                        nullability = ColumnNullability.NotNull;
                    Advance();
                    continue;

                default:
                    return;
            }
        }
    }

    private IReadOnlyList<string> ParsePrimaryKeyClause()
    {
        Expect(TokenKind.KeywordPrimary);
        Expect(TokenKind.KeywordKey);
        Expect(TokenKind.LeftParen);
        var columns = new List<string> { ExpectColumnName() };
        while (Current.Kind == TokenKind.Comma)
        {
            Advance();
            columns.Add(ExpectColumnName());
        }
        Expect(TokenKind.RightParen);
        return columns;
    }

    private TableForeignKeyClause ParseForeignKeyClause()
    {
        Expect(TokenKind.KeywordForeign);
        Expect(TokenKind.KeywordKey);
        Expect(TokenKind.LeftParen);
        var columns = new List<string> { ExpectColumnName() };
        while (Current.Kind == TokenKind.Comma)
        {
            Advance();
            columns.Add(ExpectColumnName());
        }
        Expect(TokenKind.RightParen);
        Expect(TokenKind.KeywordReferences);
        var principalTable = ExpectIdentifierName();
        Expect(TokenKind.LeftParen);
        var principalColumns = new List<string> { ExpectColumnName() };
        while (Current.Kind == TokenKind.Comma)
        {
            Advance();
            principalColumns.Add(ExpectColumnName());
        }
        Expect(TokenKind.RightParen);

        var onDelete = ForeignKeyAction.NoAction;
        if (Current.Kind == TokenKind.KeywordOn)
        {
            Advance();
            Expect(TokenKind.KeywordDelete);
            if (Current.Kind == TokenKind.KeywordCascade)
            {
                Advance();
                onDelete = ForeignKeyAction.Cascade;
            }
            else
            {
                throw Error("ON DELETE 后期望 CASCADE（v1 仅支持 CASCADE）。");
            }
        }

        return new TableForeignKeyClause(columns, principalTable, principalColumns, onDelete);
    }

    private SqlDataType ParseTableDataType()
    {
        if (Current.Kind == TokenKind.KeywordVector)
            throw Error("关系表 MVP 暂不支持 VECTOR 类型");

        var token = Current;
        switch (token.Kind)
        {
            case TokenKind.KeywordFloat: Advance(); return SqlDataType.Float64;
            case TokenKind.KeywordInt: Advance(); return SqlDataType.Int64;
            case TokenKind.KeywordBool: Advance(); return SqlDataType.Boolean;
            case TokenKind.KeywordString: Advance(); return SqlDataType.String;
            case TokenKind.KeywordDateTime: Advance(); return SqlDataType.DateTime;
            case TokenKind.KeywordBlob: Advance(); return SqlDataType.Blob;
            case TokenKind.KeywordJson: Advance(); return SqlDataType.Json;
            default: throw Error("期望关系表数据类型 INT / FLOAT / BOOL / STRING / DATETIME / BLOB / JSON");
        }
    }

    private ColumnDefinition ParseColumnDefinition()
    {
        var columnName = ExpectIdentifierName();
        ColumnKind kind;
        SqlDataType dataType;
        int? vectorDim = null;
        VectorIndexSpec? vectorIndex = null;
        ColumnNullability nullability = ColumnNullability.Unspecified;
        SqlExpression? defaultExpression = null;
        switch (Current.Kind)
        {
            case TokenKind.KeywordTag:
                Advance();
                kind = ColumnKind.Tag;
                dataType = SqlDataType.String;
                // tag 列可选地写 STRING 类型（仅允许 STRING）
                if (Current.Kind == TokenKind.KeywordString)
                {
                    Advance();
                }
                else if (IsDataTypeKeyword(Current.Kind))
                {
                    throw Error("Tag 列只能是 STRING 类型");
                }
                break;

            case TokenKind.KeywordField:
                Advance();
                kind = ColumnKind.Field;
                (dataType, vectorDim) = ParseFieldDataType();
                break;

            default:
                throw Error("期望 TAG 或 FIELD");
        }

        ParseColumnModifiers(dataType, ref vectorIndex, ref nullability, ref defaultExpression);
        return new ColumnDefinition(
            columnName,
            kind,
            dataType,
            vectorDim,
            VectorIndex: vectorIndex,
            Nullability: nullability,
            DefaultExpression: defaultExpression);
    }

    private void ParseColumnModifiers(
        SqlDataType dataType,
        ref VectorIndexSpec? vectorIndex,
        ref ColumnNullability nullability,
        ref SqlExpression? defaultExpression)
    {
        while (true)
        {
            switch (Current.Kind)
            {
                case TokenKind.KeywordNull:
                    SetNullability(ref nullability, ColumnNullability.Nullable);
                    Advance();
                    continue;

                case TokenKind.KeywordNot:
                    Advance();
                    Expect(TokenKind.KeywordNull);
                    SetNullability(ref nullability, ColumnNullability.NotNull);
                    continue;

                case TokenKind.KeywordDefault:
                    if (defaultExpression is not null)
                        throw Error("DEFAULT 子句重复声明");
                    Advance();
                    defaultExpression = ParseExpression();
                    continue;

                case TokenKind.KeywordWith:
                    if (dataType != SqlDataType.Vector)
                        throw Error("只有 VECTOR 列支持 WITH INDEX 声明");
                    if (vectorIndex is not null)
                        throw Error("WITH INDEX 子句重复声明");
                    vectorIndex = ParseVectorIndex();
                    continue;

                default:
                    return;
            }
        }
    }

    private void SetNullability(ref ColumnNullability current, ColumnNullability next)
    {
        if (current != ColumnNullability.Unspecified)
            throw Error("NULL / NOT NULL 修饰符重复或冲突");
        current = next;
    }

    private SqlDataType ParseDataType()
    {
        var token = Current;
        switch (token.Kind)
        {
            case TokenKind.KeywordFloat: Advance(); return SqlDataType.Float64;
            case TokenKind.KeywordInt: Advance(); return SqlDataType.Int64;
            case TokenKind.KeywordBool: Advance(); return SqlDataType.Boolean;
            case TokenKind.KeywordString: Advance(); return SqlDataType.String;
            case TokenKind.KeywordGeoPoint: Advance(); return SqlDataType.GeoPoint;
            default: throw Error("期望数据类型 FLOAT / INT / BOOL / STRING / GEOPOINT");
        }
    }

    /// <summary>
    /// 解析 FIELD 列数据类型，特别支持 <c>VECTOR(dim)</c> 形式（PR #58 b）。
    /// </summary>
    private (SqlDataType DataType, int? VectorDim) ParseFieldDataType()
    {
        if (Current.Kind != TokenKind.KeywordVector)
            return (ParseDataType(), null);

        var vecPos = Current.Position;
        Advance();
        Expect(TokenKind.LeftParen);
        if (Current.Kind != TokenKind.IntegerLiteral)
            throw Error("VECTOR 必须声明维度，例如 VECTOR(384)");
        long dimLong = Current.IntegerValue;
        Advance();
        Expect(TokenKind.RightParen);
        if (dimLong <= 0 || dimLong > int.MaxValue)
            throw new SqlParseException(
                $"VECTOR 维度必须为正且不超过 Int32.MaxValue，实际为 {dimLong}", vecPos);
        return (SqlDataType.Vector, (int)dimLong);
    }

    private static bool IsDataTypeKeyword(TokenKind kind)
        => kind is TokenKind.KeywordFloat or TokenKind.KeywordInt
                or TokenKind.KeywordBool or TokenKind.KeywordString
                or TokenKind.KeywordVector or TokenKind.KeywordGeoPoint;

    private VectorIndexSpec ParseVectorIndex()
    {
        Advance();
        ExpectIndexKeyword("WITH 后面期望 INDEX");

        string indexName = ExpectIdentifierName();
        Expect(TokenKind.LeftParen);

        return indexName.ToLowerInvariant() switch
        {
            "hnsw" => ParseHnswVectorIndex(),
            "ivf" or "ivf_flat" => ParseIvfVectorIndex(),
            "ivf_pq" or "ivfpq" => ParseIvfPqVectorIndex(),
            "vamana" => ParseVamanaVectorIndex(),
            _ => throw Error($"未知向量索引类型 '{indexName}'，支持 hnsw / ivf / ivf_pq / vamana"),
        };
    }

    private void ExpectIndexKeyword(string errorMessage)
    {
        if (Current.Kind == TokenKind.KeywordIndex || IsIdentifier("index"))
        {
            Advance();
            return;
        }

        ExpectIdentifier("index", errorMessage);
    }

    private HnswVectorIndexSpec ParseHnswVectorIndex()
    {
        int? m = null;
        int? ef = null;
        int? efConstruction = null;
        var metric = SonnetDB.Query.KnnMetric.Cosine;
        while (true)
        {
            string parameterName = ExpectIdentifierName();
            Expect(TokenKind.Equal);

            if (IsParameter(parameterName, "metric"))
            {
                metric = ParseVectorMetricValue();
            }
            else
            {
                int value = ExpectPositiveInt($"HNSW 参数 '{parameterName}' 后面期望正整数");
                if (string.Equals(parameterName, "m", StringComparison.OrdinalIgnoreCase))
                {
                    if (m is not null)
                        throw Error("HNSW 参数 m 重复声明");
                    m = value;
                }
                else if (string.Equals(parameterName, "ef", StringComparison.OrdinalIgnoreCase))
                {
                    if (ef is not null)
                        throw Error("HNSW 参数 ef 重复声明");
                    ef = value;
                }
                else if (IsParameter(parameterName, "ef_construction", "efconstruction"))
                {
                    if (efConstruction is not null)
                        throw Error("HNSW 参数 ef_construction 重复声明");
                    efConstruction = value;
                }
                else
                {
                    throw Error($"未知的 HNSW 参数 '{parameterName}'，仅支持 m / ef / ef_construction / metric");
                }
            }

            if (Current.Kind == TokenKind.Comma)
            {
                Advance();
                continue;
            }

            break;
        }

        Expect(TokenKind.RightParen);

        if (m is null || ef is null)
            throw Error("HNSW 索引声明必须同时提供 m 与 ef，例如 hnsw(m=16, ef=200)");

        // efConstruction 缺省取 max(ef, 200)，与 catalog 默认一致（I9：与 ef 解耦，默认不低于 200）。
        return new HnswVectorIndexSpec(m.Value, ef.Value, efConstruction ?? Math.Max(ef.Value, 200), metric);
    }

    /// <summary>解析向量索引度量字符串字面量：'cosine' / 'l2' / 'inner_product'。</summary>
    private SonnetDB.Query.KnnMetric ParseVectorMetricValue()
    {
        string raw = ExpectStringLiteral();
        return raw.ToLowerInvariant() switch
        {
            "cosine" or "cosine_distance" => SonnetDB.Query.KnnMetric.Cosine,
            "l2" or "l2_distance" or "euclidean" => SonnetDB.Query.KnnMetric.L2,
            "inner_product" or "dot" or "ip" => SonnetDB.Query.KnnMetric.InnerProduct,
            _ => throw Error($"未知的向量度量 '{raw}'，仅支持 'cosine' / 'l2' / 'inner_product'"),
        };
    }

    private IvfVectorIndexSpec ParseIvfVectorIndex()
    {
        int? nList = null;
        int? nProbe = null;
        int? maxIterations = null;
        var metric = SonnetDB.Query.KnnMetric.Cosine;
        while (true)
        {
            string parameterName = ExpectIdentifierName();
            Expect(TokenKind.Equal);
            if (IsParameter(parameterName, "metric"))
            {
                metric = ParseVectorMetricValue();
            }
            else
            {
                int value = ExpectPositiveInt($"IVF 参数 '{parameterName}' 后面期望正整数");
                if (IsParameter(parameterName, "nlist", "n_list"))
                    AssignOnce(ref nList, value, "IVF 参数 nlist 重复声明");
                else if (IsParameter(parameterName, "nprobe", "n_probe"))
                    AssignOnce(ref nProbe, value, "IVF 参数 nprobe 重复声明");
                else if (IsParameter(parameterName, "max_iterations", "maxiterations"))
                    AssignOnce(ref maxIterations, value, "IVF 参数 max_iterations 重复声明");
                else
                    throw Error($"未知的 IVF 参数 '{parameterName}'，仅支持 nlist / nprobe / max_iterations / metric");
            }

            if (Current.Kind == TokenKind.Comma)
            {
                Advance();
                continue;
            }

            break;
        }

        Expect(TokenKind.RightParen);
        return new IvfVectorIndexSpec(nList ?? 64, nProbe ?? 8, maxIterations ?? 25, metric);
    }

    private IvfPqVectorIndexSpec ParseIvfPqVectorIndex()
    {
        int? nList = null;
        int? nProbe = null;
        int? maxIterations = null;
        int? m = null;
        int? nBits = null;
        var metric = SonnetDB.Query.KnnMetric.Cosine;
        while (true)
        {
            string parameterName = ExpectIdentifierName();
            Expect(TokenKind.Equal);
            if (IsParameter(parameterName, "metric"))
            {
                metric = ParseVectorMetricValue();
            }
            else
            {
                int value = ExpectPositiveInt($"IVF-PQ 参数 '{parameterName}' 后面期望正整数");
                if (IsParameter(parameterName, "nlist", "n_list"))
                    AssignOnce(ref nList, value, "IVF-PQ 参数 nlist 重复声明");
                else if (IsParameter(parameterName, "nprobe", "n_probe"))
                    AssignOnce(ref nProbe, value, "IVF-PQ 参数 nprobe 重复声明");
                else if (IsParameter(parameterName, "max_iterations", "maxiterations"))
                    AssignOnce(ref maxIterations, value, "IVF-PQ 参数 max_iterations 重复声明");
                else if (IsParameter(parameterName, "m"))
                    AssignOnce(ref m, value, "IVF-PQ 参数 m 重复声明");
                else if (IsParameter(parameterName, "nbits", "n_bits"))
                    AssignOnce(ref nBits, value, "IVF-PQ 参数 nbits 重复声明");
                else
                    throw Error($"未知的 IVF-PQ 参数 '{parameterName}'，仅支持 nlist / nprobe / max_iterations / m / nbits / metric");
            }

            if (Current.Kind == TokenKind.Comma)
            {
                Advance();
                continue;
            }

            break;
        }

        Expect(TokenKind.RightParen);
        return new IvfPqVectorIndexSpec(nList ?? 64, nProbe ?? 8, maxIterations ?? 25, m ?? 8, nBits ?? 8, metric);
    }

    private VamanaVectorIndexSpec ParseVamanaVectorIndex()
    {
        int? maxDegree = null;
        int? searchListSize = null;
        float? alpha = null;
        int? beamWidth = null;
        var metric = SonnetDB.Query.KnnMetric.Cosine;
        while (true)
        {
            string parameterName = ExpectIdentifierName();
            Expect(TokenKind.Equal);
            if (IsParameter(parameterName, "metric"))
            {
                metric = ParseVectorMetricValue();
            }
            else if (IsParameter(parameterName, "alpha"))
            {
                if (alpha is not null)
                    throw Error("Vamana 参数 alpha 重复声明");
                alpha = ExpectPositiveFloat($"Vamana 参数 '{parameterName}' 后面期望正数");
            }
            else
            {
                int value = ExpectPositiveInt($"Vamana 参数 '{parameterName}' 后面期望正整数");
                if (IsParameter(parameterName, "max_degree", "maxdegree", "r"))
                    AssignOnce(ref maxDegree, value, "Vamana 参数 max_degree 重复声明");
                else if (IsParameter(parameterName, "search_list_size", "searchlistsize", "l"))
                    AssignOnce(ref searchListSize, value, "Vamana 参数 search_list_size 重复声明");
                else if (IsParameter(parameterName, "beam_width", "beamwidth"))
                    AssignOnce(ref beamWidth, value, "Vamana 参数 beam_width 重复声明");
                else
                    throw Error($"未知的 Vamana 参数 '{parameterName}'，仅支持 max_degree / search_list_size / alpha / beam_width / metric");
            }

            if (Current.Kind == TokenKind.Comma)
            {
                Advance();
                continue;
            }

            break;
        }

        Expect(TokenKind.RightParen);
        return new VamanaVectorIndexSpec(maxDegree ?? 32, searchListSize ?? 75, alpha ?? 1.2f, beamWidth ?? 4, metric);
    }

    // ── INSERT INTO ────────────────────────────────────────────────────────

    private InsertStatement ParseInsert()
    {
        Expect(TokenKind.KeywordInsert);
        Expect(TokenKind.KeywordInto);
        var measurement = ExpectIdentifierName();

        Expect(TokenKind.LeftParen);
        var columns = new List<string>();
        columns.Add(ExpectColumnName());
        while (Current.Kind == TokenKind.Comma)
        {
            Advance();
            columns.Add(ExpectColumnName());
        }
        Expect(TokenKind.RightParen);

        Expect(TokenKind.KeywordValues);

        var rows = new List<IReadOnlyList<SqlExpression>>();
        rows.Add(ParseValueRow(columns.Count));
        while (Current.Kind == TokenKind.Comma)
        {
            Advance();
            rows.Add(ParseValueRow(columns.Count));
        }

        return new InsertStatement(measurement, columns, rows);
    }

    private ImportJsonStatement ParseImport()
    {
        Expect(TokenKind.KeywordImport);
        Expect(TokenKind.KeywordJson);
        var filePath = ExpectStringLiteral();
        Expect(TokenKind.KeywordInto);
        var targetName = ExpectIdentifierName();

        var format = JsonImportFormat.Auto;
        string? idPath = null;
        while (Current.Kind is not TokenKind.EndOfFile and not TokenKind.Semicolon)
        {
            if (Current.Kind == TokenKind.KeywordFormat)
            {
                Advance();
                format = ParseJsonImportFormat();
                continue;
            }

            if (IsIdentifier("id"))
            {
                Advance();
                Expect(TokenKind.KeywordPath);
                idPath = ExpectStringLiteral();
                continue;
            }

            throw Error("IMPORT JSON 后面仅支持 FORMAT <AUTO|ARRAY|LINES> 或 ID PATH '$.path'");
        }

        return new ImportJsonStatement(filePath, targetName, format, idPath);
    }

    private JsonImportFormat ParseJsonImportFormat()
    {
        var name = ExpectIdentifierName();
        return name.ToLowerInvariant() switch
        {
            "auto" => JsonImportFormat.Auto,
            "array" => JsonImportFormat.Array,
            "lines" or "ndjson" or "jsonl" => JsonImportFormat.Lines,
            _ => throw Error("IMPORT JSON FORMAT 仅支持 AUTO / ARRAY / LINES"),
        };
    }

    private IReadOnlyList<SqlExpression> ParseValueRow(int expectedColumnCount)
    {
        var rowStart = Current.Position;
        Expect(TokenKind.LeftParen);
        var values = new List<SqlExpression>(expectedColumnCount);
        values.Add(ParseExpression());
        while (Current.Kind == TokenKind.Comma)
        {
            Advance();
            values.Add(ParseExpression());
        }
        Expect(TokenKind.RightParen);
        if (values.Count != expectedColumnCount)
            throw new SqlParseException(
                $"VALUES 行的列数 ({values.Count}) 与 INSERT 列列表 ({expectedColumnCount}) 不一致", rowStart);
        return values;
    }

    // ── SELECT ─────────────────────────────────────────────────────────────

    private SelectStatement ParseSelect()
    {
        Expect(TokenKind.KeywordSelect);
        bool distinct = false;
        if (Current.Kind == TokenKind.KeywordDistinct)
        {
            Advance();
            distinct = true;
        }
        var projections = ParseSelectList();

        if (Current.Kind != TokenKind.KeywordFrom)
        {
            return new SelectStatement(
                projections,
                string.Empty,
                Where: null,
                GroupBy: Array.Empty<SqlExpression>(),
                Distinct: distinct);
        }

        Advance();

        // FROM 后允许两种形式：
        //   1) 普通 measurement/table 标识符
        //   2) 表值函数调用，例如 forecast(...) / knn(...) / json_each('file.json')
        string measurement;
        string? tableAlias = null;
        var joins = new List<JoinClause>();
        FunctionCallExpression? tvf = null;
        SelectStatement? fromSubquery = null;
        if (Current.Kind == TokenKind.LeftParen && _index + 1 < _tokens.Count && _tokens[_index + 1].Kind == TokenKind.KeywordSelect)
        {
            Advance();
            fromSubquery = ParseSelect();
            Expect(TokenKind.RightParen);
            tableAlias = ParseRequiredTableAlias("FROM 子查询必须声明别名");
            measurement = tableAlias;
        }
        else if (Current.Kind == TokenKind.IdentifierLiteral
            && _index + 1 < _tokens.Count
            && _tokens[_index + 1].Kind == TokenKind.LeftParen)
        {
            var name = Current.Text;
            Advance();
            var fnCall = ParseFunctionCallTail(name);
            if (fnCall is not FunctionCallExpression call || call.IsStar)
                throw Error("FROM 子句的表值函数调用非法");
            tvf = call;
            if (IsJsonFileTableValuedFunction(name))
            {
                measurement = "__json_file__";
            }
            else
            {
                // 第一个参数通常是 source 标识符；hybrid_search / vector_search 也支持 source => docs 命名参数。
                if (call.Arguments.Count == 0)
                    throw Error($"表值函数 {name}(...) 第 1 个参数必须是 source 名称");
                measurement = ResolveTableValuedSourceName(name, call);
            }
        }
        else
        {
            measurement = ExpectIdentifierName();
            while (Current.Kind == TokenKind.Dot)
            {
                Advance();
                measurement += "." + ExpectSchemaObjectPart();
            }
            tableAlias = ParseOptionalTableAlias();
        }

        while (ParseOptionalJoinClause() is { } parsedJoin)
            joins.Add(parsedJoin);

        SqlExpression? where = null;
        if (Current.Kind == TokenKind.KeywordWhere)
        {
            Advance();
            where = ParseExpression();
        }

        var groupBy = Array.Empty<SqlExpression>();
        if (Current.Kind == TokenKind.KeywordGroup)
        {
            Advance();
            Expect(TokenKind.KeywordBy);
            groupBy = ParseGroupByList();
        }

        SqlExpression? having = null;
        if (Current.Kind == TokenKind.KeywordHaving)
        {
            Advance();
            having = ParseExpression();
        }

        var orderByItems = ParseOptionalOrderBy();
        var orderBy = orderByItems.Count > 0 ? orderByItems[0] : null;
        var pagination = ParseOptionalPagination();

        return new SelectStatement(
            projections,
            measurement,
            where,
            groupBy,
            TableValuedFunction: tvf,
            Pagination: pagination,
            OrderBy: orderBy,
            TableAlias: tableAlias,
            Join: joins.Count == 0 ? null : joins[0],
            FromSubquery: fromSubquery,
            Joins: joins,
            Having: having,
            OrderByItems: orderByItems,
            Distinct: distinct);
    }

    private string ResolveTableValuedSourceName(string functionName, FunctionCallExpression call)
    {
        var firstArgument = call.Arguments[0];
        if (firstArgument is IdentifierExpression sourceId)
            return sourceId.Name;

        if (string.Equals(functionName, "hybrid_search", StringComparison.OrdinalIgnoreCase)
            || string.Equals(functionName, "vector_search", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var argument in call.Arguments)
            {
                if (argument is NamedArgumentExpression { Name: var name, Value: IdentifierExpression source }
                    && string.Equals(name, "source", StringComparison.OrdinalIgnoreCase))
                {
                    return source.Name;
                }

                if (argument is NamedArgumentExpression
                    {
                        Name: var literalName,
                        Value: LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var sourceName }
                    }
                    && string.Equals(literalName, "source", StringComparison.OrdinalIgnoreCase))
                {
                    return sourceName!;
                }
            }
        }

        if (firstArgument is NamedArgumentExpression { Name: var parameterName, Value: IdentifierExpression namedSource }
            && string.Equals(parameterName, "source", StringComparison.OrdinalIgnoreCase))
        {
            return namedSource.Name;
        }

        throw Error($"表值函数 {functionName}(...) 第 1 个参数必须是 source 名称");
    }

    private static bool IsJsonFileTableValuedFunction(string name)
        => string.Equals(name, "json_each", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "json_table", StringComparison.OrdinalIgnoreCase);

    private string? ParseOptionalTableAlias()
    {
        if (Current.Kind == TokenKind.KeywordAs)
        {
            Advance();
            return ExpectIdentifierName();
        }

        if (Current.Kind == TokenKind.IdentifierLiteral)
        {
            var alias = Current.Text;
            Advance();
            return alias;
        }

        return null;
    }

    private string ParseRequiredTableAlias(string errorMessage)
    {
        var alias = ParseOptionalTableAlias();
        if (alias is null)
            throw Error(errorMessage);
        return alias;
    }

    private JoinClause? ParseOptionalJoinClause()
    {
        if (Current.Kind == TokenKind.KeywordInner)
        {
            Advance();
            Expect(TokenKind.KeywordJoin);
            return ParseJoinClauseTail(JoinKind.Inner);
        }

        if (Current.Kind == TokenKind.KeywordLeft)
        {
            Advance();
            if (Current.Kind == TokenKind.KeywordOuter)
                Advance();
            Expect(TokenKind.KeywordJoin);
            return ParseJoinClauseTail(JoinKind.Left);
        }

        if (Current.Kind != TokenKind.KeywordJoin)
            return null;

        Advance();
        return ParseJoinClauseTail(JoinKind.Inner);
    }

    private JoinClause ParseJoinClauseTail(JoinKind kind)
    {
        string tableName;
        SelectStatement? subquery = null;
        if (Current.Kind == TokenKind.LeftParen && _index + 1 < _tokens.Count && _tokens[_index + 1].Kind == TokenKind.KeywordSelect)
        {
            Advance();
            subquery = ParseSelect();
            Expect(TokenKind.RightParen);
            tableName = ParseRequiredTableAlias("JOIN 子查询必须声明别名");
        }
        else
        {
            tableName = ExpectIdentifierName();
        }

        var alias = subquery is null
            ? ParseOptionalTableAlias() ?? tableName
            : tableName;
        Expect(TokenKind.KeywordOn);
        var on = ParseExpression();
        return new JoinClause(tableName, alias, on, subquery, kind);
    }

    private IReadOnlyList<OrderBySpec> ParseOptionalOrderBy()
    {
        if (Current.Kind != TokenKind.KeywordOrder)
            return Array.Empty<OrderBySpec>();

        Advance();
        Expect(TokenKind.KeywordBy);
        var items = new List<OrderBySpec>();
        while (true)
        {
            var expression = ParseExpression();
            if (expression is not IdentifierExpression)
            {
                throw Error("ORDER BY 当前仅支持列名");
            }

            var direction = SortDirection.Ascending;
            if (Current.Kind == TokenKind.KeywordAsc)
            {
                Advance();
            }
            else if (Current.Kind == TokenKind.KeywordDesc)
            {
                Advance();
                direction = SortDirection.Descending;
            }

            items.Add(new OrderBySpec(expression, direction));
            if (Current.Kind != TokenKind.Comma)
                break;

            Advance();
        }

        return items;
    }

    private PaginationSpec? ParseOptionalPagination()
    {
        // 兼容 MySQL/PostgreSQL 风格：LIMIT <n> [OFFSET <m>]
        if (Current.Kind == TokenKind.KeywordLimit)
        {
            Advance();
            var fetch = ExpectNonNegativeInt("LIMIT 后面期望非负整数");
            var offset = 0;
            if (Current.Kind == TokenKind.KeywordOffset)
            {
                Advance();
                offset = ExpectNonNegativeInt("OFFSET 后面期望非负整数");
            }
            return new PaginationSpec(offset, fetch);
        }

        int offsetValue = 0;
        bool hasOffset = false;
        if (Current.Kind == TokenKind.KeywordOffset)
        {
            Advance();
            offsetValue = ExpectNonNegativeInt("OFFSET 后面期望非负整数");
            hasOffset = true;
            if (IsIdentifier("row") || IsIdentifier("rows"))
                Advance();
        }

        if (Current.Kind == TokenKind.KeywordFetch)
        {
            Advance();
            if (IsIdentifier("first") || IsIdentifier("next"))
                Advance();

            var fetch = ExpectNonNegativeInt("FETCH 后面期望非负整数");

            if (!(IsIdentifier("row") || IsIdentifier("rows")))
                throw Error("FETCH 子句期望 ROW 或 ROWS");
            Advance();

            if (!IsIdentifier("only"))
                throw Error("FETCH 子句期望 ONLY");
            Advance();
            return new PaginationSpec(offsetValue, fetch);
        }

        return hasOffset ? new PaginationSpec(offsetValue, null) : null;
    }

    private IReadOnlyList<SelectItem> ParseSelectList()
    {
        var items = new List<SelectItem>();
        items.Add(ParseSelectItem());
        while (Current.Kind == TokenKind.Comma)
        {
            Advance();
            items.Add(ParseSelectItem());
        }
        return items;
    }

    private SelectItem ParseSelectItem()
    {
        SqlExpression expression;
        if (Current.Kind == TokenKind.Star)
        {
            Advance();
            expression = StarExpression.Instance;
        }
        else
        {
            expression = ParseExpression();
        }

        string? alias = null;
        if (Current.Kind == TokenKind.KeywordAs)
        {
            Advance();
            alias = ExpectColumnName();
        }
        else if (Current.Kind == TokenKind.IdentifierLiteral)
        {
            // 可选的 alias（无 AS）；只接受一个标识符（避免吞掉后续子句关键字）
            alias = Current.Text;
            Advance();
        }

        return new SelectItem(expression, alias);
    }

    private SqlExpression[] ParseGroupByList()
    {
        var items = new List<SqlExpression> { ParseGroupByExpression() };
        while (Current.Kind == TokenKind.Comma)
        {
            Advance();
            items.Add(ParseGroupByExpression());
        }

        return items.ToArray();
    }

    private SqlExpression ParseGroupByExpression()
    {
        var expression = ParseExpression();

        if (expression is FunctionCallExpression
            {
                Name: var name,
                IsStar: false,
                Arguments: [DurationLiteralExpression { Milliseconds: <= 0 }]
            }
            && string.Equals(name, "time", StringComparison.OrdinalIgnoreCase))
        {
            throw Error("GROUP BY time(...) 桶大小必须 > 0");
        }

        return expression;
    }

    // ── DELETE ─────────────────────────────────────────────────────────────

    private DeleteStatement ParseDelete()
    {
        Expect(TokenKind.KeywordDelete);
        Expect(TokenKind.KeywordFrom);
        var measurement = ExpectIdentifierName();
        Expect(TokenKind.KeywordWhere);
        var where = ParseExpression();
        return new DeleteStatement(measurement, where);
    }

    // ── UPDATE ─────────────────────────────────────────────────────────────

    private UpdateStatement ParseUpdate()
    {
        Expect(TokenKind.KeywordUpdate);
        var table = ExpectIdentifierName();
        Expect(TokenKind.KeywordSet);

        var assignments = new List<UpdateAssignment>
        {
            ParseUpdateAssignment(),
        };
        while (Current.Kind == TokenKind.Comma)
        {
            Advance();
            assignments.Add(ParseUpdateAssignment());
        }

        Expect(TokenKind.KeywordWhere);
        var where = ParseExpression();
        return new UpdateStatement(table, assignments, where);
    }

    private UpdateAssignment ParseUpdateAssignment()
    {
        var column = ExpectColumnName();
        Expect(TokenKind.Equal);
        var value = ParseExpression();
        return new UpdateAssignment(column, value);
    }

    // ── 表达式（按优先级从低到高） ──────────────────────────────────────────

    /// <summary>解析单个表达式（公开供测试 / 子表达式调试使用）。</summary>
    public SqlExpression ParseExpression()
    {
        EnterExpression();
        try
        {
            return ParseOr();
        }
        finally
        {
            _expressionDepth--;
        }
    }

    /// <summary>
    /// 进入一层表达式递归前自增深度并校验上限。<see cref="ParseExpression"/> 是括号、子查询、
    /// 函数实参、IN 列表、CASE 分支等所有再入点的公共入口；<see cref="ParseNot"/> /
    /// <see cref="ParseUnary"/> 会自递归绕过它，故各自单独调用本方法。
    /// </summary>
    private void EnterExpression()
    {
        if (++_expressionDepth > MaxExpressionDepth)
            throw new SqlParseException(
                $"表达式嵌套深度超过上限 {MaxExpressionDepth}，疑似深层括号 / NOT / 一元运算链或恶意输入。",
                Current.Position);
    }

    private SqlExpression ParseOr()
    {
        var left = ParseAnd();
        while (Current.Kind == TokenKind.KeywordOr)
        {
            Advance();
            var right = ParseAnd();
            left = new BinaryExpression(SqlBinaryOperator.Or, left, right);
        }
        return left;
    }

    private SqlExpression ParseAnd()
    {
        var left = ParseNot();
        while (Current.Kind == TokenKind.KeywordAnd)
        {
            Advance();
            var right = ParseNot();
            left = new BinaryExpression(SqlBinaryOperator.And, left, right);
        }
        return left;
    }

    private SqlExpression ParseNot()
    {
        if (Current.Kind == TokenKind.KeywordNot)
        {
            Advance();
            // NOT 链会自递归绕过 ParseExpression，单独计入深度以防 `NOT NOT NOT…` 撑爆栈。
            EnterExpression();
            try
            {
                return new UnaryExpression(SqlUnaryOperator.Not, ParseNot());
            }
            finally
            {
                _expressionDepth--;
            }
        }
        return ParseComparison();
    }

    private SqlExpression ParseComparison()
    {
        var left = ParseAdditive();
        while (true)
        {
            if (TryMapComparison(Current.Kind, out var op))
            {
                Advance();
                var right = ParseAdditive();
                left = new BinaryExpression(op, left, right);
                continue;
            }

            if (Current.Kind == TokenKind.KeywordLike)
            {
                Advance();
                var right = ParseAdditive();
                left = new BinaryExpression(SqlBinaryOperator.Like, left, right);
                continue;
            }

            if (Current.Kind == TokenKind.KeywordRegex)
            {
                Advance();
                var right = ParseAdditive();
                left = new BinaryExpression(SqlBinaryOperator.Regex, left, right);
                continue;
            }

            if (Current.Kind == TokenKind.KeywordIs)
            {
                Advance();
                var negated = false;
                if (Current.Kind == TokenKind.KeywordNot)
                {
                    Advance();
                    negated = true;
                }

                Expect(TokenKind.KeywordNull);
                left = new IsNullExpression(left, negated);
                continue;
            }

            if (Current.Kind == TokenKind.KeywordNot
                && _index + 1 < _tokens.Count
                && _tokens[_index + 1].Kind == TokenKind.KeywordLike)
            {
                Advance();
                Advance();
                var right = ParseAdditive();
                left = new BinaryExpression(SqlBinaryOperator.NotLike, left, right);
                continue;
            }

            if (Current.Kind == TokenKind.KeywordNot
                && _index + 1 < _tokens.Count
                && _tokens[_index + 1].Kind == TokenKind.KeywordRegex)
            {
                Advance();
                Advance();
                var right = ParseAdditive();
                left = new BinaryExpression(SqlBinaryOperator.NotRegex, left, right);
                continue;
            }

            if (Current.Kind == TokenKind.KeywordIn
                || (Current.Kind == TokenKind.KeywordNot
                    && _index + 1 < _tokens.Count
                    && _tokens[_index + 1].Kind == TokenKind.KeywordIn))
            {
                var negated = Current.Kind == TokenKind.KeywordNot;
                if (negated)
                    Advance();
                Expect(TokenKind.KeywordIn);
                left = ParseInPredicate(left, negated);
                continue;
            }

            if (TryMapVectorDistance(Current.Kind, out var functionName))
            {
                Advance();
                var right = ParseAdditive();
                left = new FunctionCallExpression(functionName, new[] { left, right });
                continue;
            }

            break;
        }
        return left;
    }

    private InExpression ParseInPredicate(SqlExpression value, bool negated)
    {
        Expect(TokenKind.LeftParen);
        if (Current.Kind == TokenKind.KeywordSelect)
        {
            var subquery = ParseSelect();
            Expect(TokenKind.RightParen);
            return new InExpression(value, Array.Empty<SqlExpression>(), subquery, negated);
        }

        var values = new List<SqlExpression> { ParseExpression() };
        while (Current.Kind == TokenKind.Comma)
        {
            Advance();
            values.Add(ParseExpression());
        }
        Expect(TokenKind.RightParen);
        return new InExpression(value, values, Subquery: null, negated);
    }

    private static bool TryMapComparison(TokenKind kind, out SqlBinaryOperator op)
    {
        switch (kind)
        {
            case TokenKind.Equal: op = SqlBinaryOperator.Equal; return true;
            case TokenKind.NotEqual: op = SqlBinaryOperator.NotEqual; return true;
            case TokenKind.LessThan: op = SqlBinaryOperator.LessThan; return true;
            case TokenKind.LessThanOrEqual: op = SqlBinaryOperator.LessThanOrEqual; return true;
            case TokenKind.GreaterThan: op = SqlBinaryOperator.GreaterThan; return true;
            case TokenKind.GreaterThanOrEqual: op = SqlBinaryOperator.GreaterThanOrEqual; return true;
            default: op = default; return false;
        }
    }

    private static bool TryMapVectorDistance(TokenKind kind, out string functionName)
    {
        switch (kind)
        {
            case TokenKind.VectorCosineDistance:
                functionName = "cosine_distance";
                return true;
            case TokenKind.VectorL2Distance:
                functionName = "l2_distance";
                return true;
            case TokenKind.VectorInnerProduct:
                functionName = "inner_product";
                return true;
            default:
                functionName = string.Empty;
                return false;
        }
    }

    private SqlExpression ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (Current.Kind is TokenKind.Plus or TokenKind.Minus)
        {
            var op = Current.Kind == TokenKind.Plus ? SqlBinaryOperator.Add : SqlBinaryOperator.Subtract;
            Advance();
            var right = ParseMultiplicative();
            left = new BinaryExpression(op, left, right);
        }
        return left;
    }

    private SqlExpression ParseMultiplicative()
    {
        var left = ParseUnary();
        while (Current.Kind is TokenKind.Star or TokenKind.Slash or TokenKind.Percent)
        {
            var op = Current.Kind switch
            {
                TokenKind.Star => SqlBinaryOperator.Multiply,
                TokenKind.Slash => SqlBinaryOperator.Divide,
                _ => SqlBinaryOperator.Modulo,
            };
            Advance();
            var right = ParseUnary();
            left = new BinaryExpression(op, left, right);
        }
        return left;
    }

    private SqlExpression ParseUnary()
    {
        if (Current.Kind == TokenKind.Minus)
        {
            Advance();
            // 一元 +/- 链会自递归绕过 ParseExpression，单独计入深度以防 `------x` 撑爆栈。
            EnterExpression();
            try
            {
                return new UnaryExpression(SqlUnaryOperator.Negate, ParseUnary());
            }
            finally
            {
                _expressionDepth--;
            }
        }
        if (Current.Kind == TokenKind.Plus)
        {
            Advance();
            EnterExpression();
            try
            {
                return ParseUnary();
            }
            finally
            {
                _expressionDepth--;
            }
        }
        return ParsePrimary();
    }

    private SqlExpression ParsePrimary()
    {
        var token = Current;
        switch (token.Kind)
        {
            case TokenKind.IntegerLiteral:
                Advance();
                return LiteralExpression.Integer(token.IntegerValue);
            case TokenKind.FloatLiteral:
                Advance();
                return LiteralExpression.Float(token.DoubleValue);
            case TokenKind.StringLiteral:
                Advance();
                return LiteralExpression.String(token.Text);
            case TokenKind.DurationLiteral:
                Advance();
                return new DurationLiteralExpression(token.IntegerValue);
            case TokenKind.Parameter:
                Advance();
                // 位置参数 ? 的 Text 为空 → Name=null；命名参数 @p/:p 的 Text 为参数名。
                // 无论命名或位置，都按出现顺序分配 Ordinal（支持位置绑定 / 命名回退）。
                return new ParameterExpression(
                    _parameterOrdinal++,
                    string.IsNullOrEmpty(token.Text) ? null : token.Text);
            case TokenKind.KeywordNull:
                Advance();
                return LiteralExpression.Null();
            case TokenKind.KeywordTrue:
                Advance();
                return LiteralExpression.Bool(true);
            case TokenKind.KeywordFalse:
                Advance();
                return LiteralExpression.Bool(false);
            case TokenKind.KeywordCase:
                return ParseCaseExpression();
            case TokenKind.LeftParen:
                Advance();
                if (Current.Kind == TokenKind.KeywordSelect)
                {
                    var subquery = ParseSelect();
                    Expect(TokenKind.RightParen);
                    return new SubqueryExpression(subquery);
                }
                var inner = ParseExpression();
                Expect(TokenKind.RightParen);
                return inner;
            case TokenKind.LeftBracket:
                return ParseVectorLiteral();
            case TokenKind.IdentifierLiteral when string.Equals(Current.Text, "point", StringComparison.OrdinalIgnoreCase):
                return ParsePointLiteralOrFunctionCall();
            case TokenKind.KeywordTime:
                // time 既可以作为列名（time >= 100），也可以作为函数（time(1m)）；
                // 看下一个 token 是否为 '(' 决定。
                return ParseIdentifierOrFunctionCall();
            case TokenKind.KeywordDocument:
            case TokenKind.KeywordJson:
            case TokenKind.KeywordCollection:
            case TokenKind.KeywordMeasurement:
                return ParseIdentifierOrFunctionCall();
            case TokenKind.KeywordExists:
                return ParseExistsExpression();
            case TokenKind.IdentifierLiteral:
                return ParseIdentifierOrFunctionCall();
            default:
                throw Error("期望表达式");
        }
    }

    private CaseExpression ParseCaseExpression()
    {
        Expect(TokenKind.KeywordCase);
        var whenClauses = new List<CaseWhenClause>();
        do
        {
            Expect(TokenKind.KeywordWhen);
            var condition = ParseExpression();
            Expect(TokenKind.KeywordThen);
            var result = ParseExpression();
            whenClauses.Add(new CaseWhenClause(condition, result));
        }
        while (Current.Kind == TokenKind.KeywordWhen);

        SqlExpression? elseExpression = null;
        if (Current.Kind == TokenKind.KeywordElse)
        {
            Advance();
            elseExpression = ParseExpression();
        }

        Expect(TokenKind.KeywordEnd);
        return new CaseExpression(whenClauses, elseExpression);
    }

    private ExistsExpression ParseExistsExpression()
    {
        Expect(TokenKind.KeywordExists);
        Expect(TokenKind.LeftParen);
        var subquery = ParseSelect();
        Expect(TokenKind.RightParen);
        return new ExistsExpression(subquery);
    }

    private SqlExpression ParseIdentifierOrFunctionCall()
    {
        var name = Current.Text;
        Advance();
        if (Current.Kind == TokenKind.Dot)
        {
            Advance();
            return new IdentifierExpression(ExpectQualifiedIdentifierPart(), name);
        }

        if (Current.Kind == TokenKind.LeftParen)
        {
            return ParseFunctionCallTail(name);
        }
        return new IdentifierExpression(name);
    }

    private SqlExpression ParsePointLiteralOrFunctionCall()
    {
        var name = Current.Text;
        Advance();
        if (Current.Kind == TokenKind.Dot)
        {
            Advance();
            return new IdentifierExpression(ExpectQualifiedIdentifierPart(), name);
        }

        if (Current.Kind != TokenKind.LeftParen)
            return new IdentifierExpression(name);

        Expect(TokenKind.LeftParen);
        double lat = ParseVectorComponent();
        Expect(TokenKind.Comma);
        double lon = ParseVectorComponent();
        Expect(TokenKind.RightParen);
        return new GeoPointLiteralExpression(lat, lon);
    }

    private string ExpectQualifiedIdentifierPart()
    {
        if (Current.Kind == TokenKind.IdentifierLiteral)
            return ExpectIdentifierName();

        if (Current.Kind == TokenKind.KeywordTime)
        {
            Advance();
            return "time";
        }

        if (Current.Kind == TokenKind.KeywordDocument)
        {
            Advance();
            return "document";
        }

        if (Current.Kind == TokenKind.KeywordJson)
        {
            Advance();
            return "json";
        }

        if (Current.Kind == TokenKind.KeywordCollection)
        {
            Advance();
            return "collection";
        }

        if (Current.Kind == TokenKind.KeywordTag)
        {
            Advance();
            return "tag";
        }

        if (Current.Kind == TokenKind.KeywordField)
        {
            Advance();
            return "field";
        }

        throw Error("限定列名中 '.' 后面期望列名");
    }

    private SqlExpression ParseFunctionCallTail(string name)
    {
        Expect(TokenKind.LeftParen);

        // fn(*) 形式。其他位置的 * 作为普通函数参数保留给执行层解释，
        // 例如 match(ft_index, *, 'query')。
        if (Current.Kind == TokenKind.Star
            && _index + 1 < _tokens.Count
            && _tokens[_index + 1].Kind == TokenKind.RightParen)
        {
            Advance();
            Expect(TokenKind.RightParen);
            return new FunctionCallExpression(name, Array.Empty<SqlExpression>(), IsStar: true);
        }

        // fn() 零参
        if (Current.Kind == TokenKind.RightParen)
        {
            Advance();
            return new FunctionCallExpression(name, Array.Empty<SqlExpression>());
        }

        var args = new List<SqlExpression>();
        args.Add(ParseFunctionArgument());
        while (Current.Kind == TokenKind.Comma)
        {
            Advance();
            args.Add(ParseFunctionArgument());
        }
        Expect(TokenKind.RightParen);
        return new FunctionCallExpression(name, args);
    }

    private SqlExpression ParseFunctionArgument()
    {
        if (Current.Kind == TokenKind.Star)
        {
            Advance();
            return StarExpression.Instance;
        }

        if (TryParseNamedArgumentPrefix(out var name))
        {
            Expect(TokenKind.Arrow);
            return new NamedArgumentExpression(name, ParseExpression());
        }

        return ParseExpression();
    }

    private bool TryParseNamedArgumentPrefix(out string name)
    {
        name = string.Empty;
        if (_index + 1 >= _tokens.Count || _tokens[_index + 1].Kind != TokenKind.Arrow)
            return false;

        name = Current.Kind switch
        {
            TokenKind.IdentifierLiteral => Current.Text,
            TokenKind.KeywordVector => Current.Text,
            TokenKind.KeywordJson => Current.Text,
            TokenKind.KeywordDocument => Current.Text,
            TokenKind.KeywordTime => Current.Text,
            TokenKind.KeywordField => Current.Text,
            TokenKind.KeywordTag => Current.Text,
            _ => string.Empty,
        };

        if (name.Length == 0)
            return false;

        Advance();
        return true;
    }

    /// <summary>
    /// 解析向量字面量 <c>[v0, v1, v2, ...]</c>（PR #58 b）。
    /// 仅接受数值字面量（INT / FLOAT，可带 <c>+/-</c> 前缀）；至少包含 1 个元素。
    /// </summary>
    private VectorLiteralExpression ParseVectorLiteral()
    {
        var startPos = Current.Position;
        Expect(TokenKind.LeftBracket);
        if (Current.Kind == TokenKind.RightBracket)
            throw new SqlParseException("向量字面量至少需要 1 个元素", startPos);

        var components = new List<double>();
        components.Add(ParseVectorComponent());
        while (Current.Kind == TokenKind.Comma)
        {
            Advance();
            components.Add(ParseVectorComponent());
        }
        Expect(TokenKind.RightBracket);
        return new VectorLiteralExpression(components);
    }

    /// <summary>
    /// 解析向量字面量内部的单个分量：可选 <c>+/-</c> + INT/FLOAT 字面量。
    /// </summary>
    private double ParseVectorComponent()
    {
        int sign = 1;
        if (Current.Kind == TokenKind.Minus)
        {
            sign = -1;
            Advance();
        }
        else if (Current.Kind == TokenKind.Plus)
        {
            Advance();
        }
        switch (Current.Kind)
        {
            case TokenKind.IntegerLiteral:
                {
                    long iv = Current.IntegerValue;
                    Advance();
                    return sign * (double)iv;
                }
            case TokenKind.FloatLiteral:
                {
                    double dv = Current.DoubleValue;
                    Advance();
                    return sign * dv;
                }
            default:
                throw Error("向量字面量分量必须是数值字面量");
        }
    }

    // ── 工具方法 ────────────────────────────────────────────────────────────

    private Token Current => _tokens[_index];

    private void Advance() => _index++;

    private void Expect(TokenKind kind)
    {
        if (Current.Kind != kind)
            throw Error($"期望 token {kind}，实际为 {Current.Kind}");
        Advance();
    }

    private string ExpectIdentifierName()
    {
        return Current.Kind == TokenKind.IdentifierLiteral
            ? ExpectIdentifierLiteral()
            : throw Error("期望标识符");
    }

    private string ExpectSchemaObjectPart()
    {
        if (Current.Kind == TokenKind.IdentifierLiteral)
            return ExpectIdentifierLiteral();

        var name = Current.Kind switch
        {
            TokenKind.KeywordTables => "tables",
            TokenKind.KeywordColumn => "column",
            TokenKind.KeywordIndex => "index",
            TokenKind.KeywordCollections => "collections",
            TokenKind.KeywordMeasurements => "measurements",
            _ => null,
        };
        if (name is null)
            throw Error("期望 schema 对象名");
        Advance();
        return name;
    }

    /// <summary>
    /// 解析用户名：接受标识符、任意关键字，或单引号字符串字面量。
    /// 这样既兼容 <c>alice</c>，也兼容 <c>ops-admin</c> 这类包含非标识符字符的用户名。
    /// </summary>
    private string ExpectUserName()
    {
        if (Current.Kind == TokenKind.StringLiteral)
            return ExpectStringLiteral();
        if (Current.Kind != TokenKind.IdentifierLiteral && !IsKeyword(Current.Kind))
            throw Error("期望用户名");
        var name = Current.Text;
        Advance();
        return name;
    }

    private string ExpectIdentifierLiteral()
    {
        if (Current.Kind != TokenKind.IdentifierLiteral)
            throw Error("期望标识符");
        var name = Current.Text;
        Advance();
        return name;
    }

    private string ExpectUnquotedUserName()
    {
        if (Current.Kind != TokenKind.IdentifierLiteral && !IsKeyword(Current.Kind))
            throw Error("期望用户名");
        var name = Current.Text;
        Advance();
        return name;
    }

    private static bool IsKeyword(TokenKind kind) =>
        kind >= TokenKind.KeywordCreate;

    private int ExpectNonNegativeInt(string errorMessage)
    {
        if (Current.Kind != TokenKind.IntegerLiteral)
            throw Error(errorMessage);

        var value = Current.IntegerValue;
        Advance();

        if (value < 0)
            throw Error(errorMessage);
        if (value > int.MaxValue)
            throw Error("分页参数过大，必须 <= Int32.MaxValue");

        return (int)value;
    }

    private int ExpectPositiveInt(string errorMessage)
    {
        int value = ExpectNonNegativeInt(errorMessage);
        if (value <= 0)
            throw Error(errorMessage);
        return value;
    }

    private float ExpectPositiveFloat(string errorMessage)
    {
        double value;
        if (Current.Kind == TokenKind.IntegerLiteral)
        {
            value = Current.IntegerValue;
        }
        else if (Current.Kind == TokenKind.FloatLiteral)
        {
            value = Current.DoubleValue;
        }
        else
        {
            throw Error(errorMessage);
        }

        Advance();
        if (value <= 0 || value > float.MaxValue || double.IsNaN(value) || double.IsInfinity(value))
            throw Error(errorMessage);
        return (float)value;
    }

    private static bool IsParameter(string actual, params ReadOnlySpan<string> expected)
    {
        foreach (string candidate in expected)
        {
            if (string.Equals(actual, candidate, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private void AssignOnce(ref int? target, int value, string duplicateError)
    {
        if (target is not null)
            throw Error(duplicateError);
        target = value;
    }

    private bool IsIdentifier(string text)
        => Current.Kind == TokenKind.IdentifierLiteral
           && string.Equals(Current.Text, text, StringComparison.OrdinalIgnoreCase);

    private bool IsIndexKeyword()
        => Current.Kind == TokenKind.KeywordIndex || IsIdentifier("index");

    private void ExpectIdentifier(string text, string errorMessage)
    {
        if (!IsIdentifier(text))
            throw Error(errorMessage);
        Advance();
    }

    /// <summary>
    /// 期望一个列名 token：普通标识符；或者 <see cref="TokenKind.KeywordTime"/>（保留字 <c>time</c> 在列名上下文中
    /// 视为名为 <c>"time"</c> 的列，与时间戳伪列对应）。
    /// </summary>
    private string ExpectColumnName()
    {
        switch (Current.Kind)
        {
            case TokenKind.IdentifierLiteral:
                var name = Current.Text;
                Advance();
                return name;
            case TokenKind.KeywordTime:
                Advance();
                return "time";
            case TokenKind.KeywordKey:
                Advance();
                return "key";
            case TokenKind.KeywordDocument:
                Advance();
                return "document";
            case TokenKind.KeywordJson:
                Advance();
                return "json";
            case TokenKind.KeywordCollection:
                Advance();
                return "collection";
            case TokenKind.KeywordTag:
                Advance();
                return "tag";
            case TokenKind.KeywordField:
                Advance();
                return "field";
            default:
                throw Error("期望列名");
        }
    }

    private string ExpectIndexColumnOrPath()
    {
        if (Current.Kind == TokenKind.StringLiteral)
            return ExpectStringLiteral();

        return ExpectColumnName();
    }

    private long? ParseOptionalTtlSeconds()
    {
        if (Current.Kind != TokenKind.KeywordWith)
            throw Error("TTL INDEX 需要 WITH ttl_seconds = <seconds>");

        Advance();
        if (Current.Kind == TokenKind.LeftParen)
            Advance();

        string parameterName = ExpectIdentifierName();
        if (!IsParameter(parameterName, "ttl_seconds", "expire_after_seconds", "seconds"))
            throw Error("TTL INDEX WITH 后面期望 ttl_seconds 参数");
        Expect(TokenKind.Equal);
        int ttlSeconds = ExpectPositiveInt("ttl_seconds 必须是正整数");

        if (Current.Kind == TokenKind.RightParen)
            Advance();
        return ttlSeconds;
    }

    private string ExpectFullTextFieldName()
    {
        if (Current.Kind == TokenKind.StringLiteral)
            return ExpectStringLiteral();

        return ExpectColumnName();
    }

    private string ExpectFullTextTokenizerName()
    {
        if (Current.Kind == TokenKind.IdentifierLiteral)
            return ExpectIdentifierName();

        if (Current.Kind == TokenKind.KeywordString)
        {
            Advance();
            return "string";
        }

        throw Error("USING 后面期望分词器名称");
    }

    private void ConsumeOptionalSemicolon()
    {
        if (Current.Kind == TokenKind.Semicolon)
            Advance();
    }

    private void ExpectEndOfFile()
    {
        if (Current.Kind != TokenKind.EndOfFile)
            throw Error("语句末尾存在多余内容");
    }

    private BeginTransactionStatement ParseBegin()
    {
        Expect(TokenKind.KeywordBegin);
        if (Current.Kind == TokenKind.KeywordTransaction || IsIdentifier("transaction"))
            Advance();
        return new BeginTransactionStatement();
    }

    private CommitTransactionStatement ParseCommit()
    {
        Expect(TokenKind.KeywordCommit);
        return new CommitTransactionStatement();
    }

    private RollbackTransactionStatement ParseRollback()
    {
        Expect(TokenKind.KeywordRollback);
        return new RollbackTransactionStatement();
    }

    // ── 控制面 DDL（PR #34a）─────────────────────────────────────────────

    /// <summary><c>CREATE USER name WITH PASSWORD 'pwd'</c>。</summary>
    private CreateUserStatement ParseCreateUserBody()
    {
        Expect(TokenKind.KeywordUser);
        var name = ExpectUnquotedUserName();
        Expect(TokenKind.KeywordWith);
        Expect(TokenKind.KeywordPassword);
        var password = ExpectStringLiteral();
        bool isSuperuser = false;
        if (Current.Kind == TokenKind.KeywordSuperuser)
        {
            Advance();
            isSuperuser = true;
        }
        return new CreateUserStatement(name, password, IsSuperuser: isSuperuser);
    }

    /// <summary><c>CREATE DATABASE name</c>。</summary>
    private CreateDatabaseStatement ParseCreateDatabaseBody()
    {
        Expect(TokenKind.KeywordDatabase);
        var name = ExpectIdentifierName();
        return new CreateDatabaseStatement(name);
    }

    /// <summary><c>DROP USER name</c> 或 <c>DROP DATABASE name</c>。</summary>
    private SqlStatement ParseDrop()
    {
        Expect(TokenKind.KeywordDrop);
        switch (Current.Kind)
        {
            case TokenKind.KeywordIndex:
                Advance();
                var indexName = ExpectIdentifierName();
                Expect(TokenKind.KeywordOn);
                return new DropTableIndexStatement(indexName, ExpectIdentifierName());
            case TokenKind.KeywordJson:
                Advance();
                ExpectIndexKeyword("DROP JSON 后面期望 INDEX");
                var jsonIndexName = ExpectIdentifierName();
                Expect(TokenKind.KeywordOn);
                return new DropDocumentPathIndexStatement(jsonIndexName, ExpectIdentifierName());
            case TokenKind.KeywordFullText:
                Advance();
                ExpectIndexKeyword("DROP FULLTEXT 后面期望 INDEX");
                var fullTextIndexName = ExpectIdentifierName();
                Expect(TokenKind.KeywordOn);
                return new DropFullTextIndexStatement(fullTextIndexName, ExpectIdentifierName());
            case TokenKind.KeywordTable:
                Advance();
                var dropTableIfExists = ParseOptionalIfExists();
                return new DropTableStatement(ExpectIdentifierName(), dropTableIfExists);
            case TokenKind.KeywordMeasurement:
                Advance();
                var dropMeasurementIfExists = ParseOptionalIfExists();
                return new DropMeasurementStatement(ExpectIdentifierName(), dropMeasurementIfExists);
            case TokenKind.KeywordDocument:
                Advance();
                Expect(TokenKind.KeywordCollection);
                return new DropDocumentCollectionStatement(ExpectIdentifierName());
            case TokenKind.KeywordUser:
                Advance();
                return new DropUserStatement(ExpectUserName());
            case TokenKind.KeywordDatabase:
                Advance();
                return new DropDatabaseStatement(ExpectIdentifierName());
            default:
                if (IsIdentifier("index"))
                {
                    Advance();
                    var fallbackIndexName = ExpectIdentifierName();
                    Expect(TokenKind.KeywordOn);
                    return new DropTableIndexStatement(fallbackIndexName, ExpectIdentifierName());
                }

                throw Error("DROP 后面期望 MEASUREMENT / TABLE / INDEX / JSON INDEX / FULLTEXT INDEX / USER 或 DATABASE");
        }
    }

    private SqlStatement ParseAlter()
    {
        Expect(TokenKind.KeywordAlter);
        return Current.Kind switch
        {
            TokenKind.KeywordTable => ParseAlterTableBody(),
            TokenKind.KeywordDocument => ParseAlterDocumentBody(),
            TokenKind.KeywordUser => ParseAlterUserBody(),
            _ => throw Error("ALTER 后面期望 TABLE / DOCUMENT COLLECTION 或 USER"),
        };
    }

    private SqlStatement ParseAlterDocumentBody()
    {
        Expect(TokenKind.KeywordDocument);
        Expect(TokenKind.KeywordCollection);
        var collectionName = ExpectIdentifierName();
        if (Current.Kind == TokenKind.KeywordSet)
        {
            Advance();
            ExpectIdentifier("validator", "ALTER DOCUMENT COLLECTION SET 后面期望 VALIDATOR");
            string validatorJson = ExpectStringLiteral();
            string? validationAction = null;
            if (IsIdentifier("validation"))
            {
                Advance();
                ExpectIdentifier("action", "VALIDATION 后面期望 ACTION");
                validationAction = ExpectIdentifierName();
            }

            return new AlterDocumentCollectionSetValidatorStatement(collectionName, validatorJson, validationAction);
        }

        if (Current.Kind == TokenKind.KeywordDrop)
        {
            Advance();
            ExpectIdentifier("validator", "ALTER DOCUMENT COLLECTION DROP 后面期望 VALIDATOR");
            return new AlterDocumentCollectionDropValidatorStatement(collectionName);
        }

        throw Error("ALTER DOCUMENT COLLECTION 后面期望 SET VALIDATOR / DROP VALIDATOR");
    }

    private SqlStatement ParseAlterTableBody()
    {
        Expect(TokenKind.KeywordTable);
        var tableName = ExpectIdentifierName();
        if (IsIdentifier("add"))
        {
            Advance();
            return ParseAlterTableAddColumn(tableName);
        }

        if (Current.Kind == TokenKind.KeywordDrop)
        {
            Advance();
            if (Current.Kind == TokenKind.KeywordColumn)
            {
                Advance();
                var dropColumnIfExists = ParseOptionalIfExists();
                return new AlterTableDropColumnStatement(tableName, ExpectColumnName(), dropColumnIfExists);
            }

            if (IsIdentifier("constraint"))
            {
                Advance();
                return new AlterTableDropConstraintStatement(tableName, ExpectIdentifierName());
            }

            var dropColumnIfExistsWithoutColumnKeyword = ParseOptionalIfExists();
            return new AlterTableDropColumnStatement(
                tableName,
                ExpectColumnName(),
                dropColumnIfExistsWithoutColumnKeyword);
        }

        if (Current.Kind == TokenKind.KeywordRename)
        {
            Advance();
            if (Current.Kind == TokenKind.KeywordColumn)
            {
                Advance();
                var oldColumn = ExpectColumnName();
                Expect(TokenKind.KeywordTo);
                return new AlterTableRenameColumnStatement(tableName, oldColumn, ExpectColumnName());
            }

            Expect(TokenKind.KeywordTo);
            return new AlterTableRenameTableStatement(tableName, ExpectIdentifierName());
        }

        throw Error("ALTER TABLE 后面期望 ADD COLUMN / DROP COLUMN / DROP CONSTRAINT / RENAME COLUMN / RENAME TO");
    }

    /// <summary><c>ALTER USER name WITH PASSWORD 'pwd'</c>。</summary>
    private AlterUserPasswordStatement ParseAlterUserBody()
    {
        Expect(TokenKind.KeywordUser);
        var name = ExpectUserName();
        Expect(TokenKind.KeywordWith);
        Expect(TokenKind.KeywordPassword);
        var password = ExpectStringLiteral();
        return new AlterUserPasswordStatement(name, password);
    }

    /// <summary><c>GRANT READ|WRITE|ADMIN ON DATABASE db TO user</c>。</summary>
    private GrantStatement ParseGrant()
    {
        Expect(TokenKind.KeywordGrant);
        var perm = Current.Kind switch
        {
            TokenKind.KeywordRead => GrantPermission.Read,
            TokenKind.KeywordWrite => GrantPermission.Write,
            TokenKind.KeywordAdmin => GrantPermission.Admin,
            _ => throw Error("GRANT 后面期望 READ / WRITE / ADMIN"),
        };
        Advance();
        Expect(TokenKind.KeywordOn);
        Expect(TokenKind.KeywordDatabase);
        var db = ExpectDatabaseNameOrStar();
        Expect(TokenKind.KeywordTo);
        var user = ExpectUserName();
        return new GrantStatement(perm, db, user);
    }

    /// <summary><c>REVOKE ON DATABASE db FROM user</c> 或 <c>REVOKE TOKEN '&lt;id&gt;'</c>。</summary>
    private SqlStatement ParseRevoke()
    {
        Expect(TokenKind.KeywordRevoke);
        if (Current.Kind == TokenKind.KeywordToken)
        {
            Advance();
            var tokenId = ExpectStringLiteral();
            return new RevokeTokenStatement(tokenId);
        }
        Expect(TokenKind.KeywordOn);
        Expect(TokenKind.KeywordDatabase);
        var db = ExpectDatabaseNameOrStar();
        Expect(TokenKind.KeywordFrom);
        var user = ExpectUserName();
        return new RevokeStatement(db, user);
    }

    /// <summary><c>ISSUE TOKEN FOR &lt;user&gt;</c>：为指定用户颁发一个新 token。</summary>
    private IssueTokenStatement ParseIssue()
    {
        Expect(TokenKind.KeywordIssue);
        Expect(TokenKind.KeywordToken);
        Expect(TokenKind.KeywordFor);
        var user = ExpectUserName();
        return new IssueTokenStatement(user);
    }

    /// <summary>
    /// <c>SHOW USERS</c> / <c>SHOW GRANTS [FOR &lt;user&gt;]</c> / <c>SHOW DATABASES</c>。
    /// </summary>
    private SqlStatement ParseShow()
    {
        Expect(TokenKind.KeywordShow);
        switch (Current.Kind)
        {
            case TokenKind.KeywordUsers:
                Advance();
                return new ShowUsersStatement();
            case TokenKind.KeywordDatabases:
                Advance();
                return new ShowDatabasesStatement();
            case TokenKind.KeywordGrants:
                Advance();
                if (Current.Kind == TokenKind.KeywordFor)
                {
                    Advance();
                    var user = ExpectUserName();
                    return new ShowGrantsStatement(user);
                }
                return new ShowGrantsStatement(null);
            case TokenKind.KeywordTokens:
                Advance();
                if (Current.Kind == TokenKind.KeywordFor)
                {
                    Advance();
                    var tu = ExpectUserName();
                    return new ShowTokensStatement(tu);
                }
                return new ShowTokensStatement(null);
            case TokenKind.KeywordMeasurements:
                Advance();
                return new ShowMeasurementsStatement();
            case TokenKind.KeywordTables:
                Advance();
                return new ShowTablesStatement();
            case TokenKind.KeywordDocument:
                Advance();
                Expect(TokenKind.KeywordCollections);
                return new ShowDocumentCollectionsStatement();
            case TokenKind.KeywordJson:
                Advance();
                if (IsIdentifier("indexes"))
                {
                    Advance();
                    Expect(TokenKind.KeywordOn);
                    return new ShowDocumentIndexesStatement(ExpectIdentifierName());
                }

                throw Error("SHOW JSON 后面期望 INDEXES");
            case TokenKind.KeywordFullText:
                Advance();
                if (IsIdentifier("indexes"))
                {
                    Advance();
                    Expect(TokenKind.KeywordOn);
                    return new ShowFullTextIndexesStatement(ExpectIdentifierName());
                }

                throw Error("SHOW FULLTEXT 后面期望 INDEXES");
            default:
                if (IsIdentifier("indexes"))
                {
                    Advance();
                    Expect(TokenKind.KeywordOn);
                    return new ShowTableIndexesStatement(ExpectIdentifierName());
                }

                throw Error("SHOW 后面期望 USERS / GRANTS / DATABASES / TOKENS / MEASUREMENTS / TABLES / INDEXES");
        }
    }

    /// <summary>
    /// <c>EXPLAIN SELECT ...</c> / <c>EXPLAIN SHOW MEASUREMENTS</c> / <c>EXPLAIN DESCRIBE ...</c>。
    /// 当前仅接受只读语句，避免把写操作伪装成解释计划。
    /// </summary>
    private ExplainStatement ParseExplain()
    {
        Expect(TokenKind.KeywordExplain);

        SqlStatement statement = Current.Kind switch
        {
            TokenKind.KeywordSelect => ParseSelect(),
            TokenKind.KeywordShow => ParseShow(),
            TokenKind.KeywordDescribe => ParseDescribe(),
            TokenKind.KeywordDesc => ParseDescribe(),
            _ => throw Error("EXPLAIN 后面期望 SELECT / SHOW MEASUREMENTS / SHOW TABLES / SHOW DOCUMENT COLLECTIONS / DESCRIBE [MEASUREMENT|TABLE|DOCUMENT COLLECTION]"),
        };

        if (statement is not SelectStatement
            and not ShowMeasurementsStatement
            and not ShowTablesStatement
            and not ShowTableIndexesStatement
            and not ShowDocumentCollectionsStatement
            and not ShowDocumentIndexesStatement
            and not ShowFullTextIndexesStatement
            and not DescribeMeasurementStatement
            and not DescribeTableStatement
            and not DescribeDocumentCollectionStatement)
        {
            throw Error("EXPLAIN 仅支持 SELECT / SHOW MEASUREMENTS / SHOW TABLES / SHOW DOCUMENT COLLECTIONS / DESCRIBE [MEASUREMENT|TABLE|DOCUMENT COLLECTION]");
        }

        return new ExplainStatement(statement);
    }

    /// <summary>
    /// <c>DESCRIBE [MEASUREMENT] &lt;name&gt;</c> / <c>DESC [MEASUREMENT] &lt;name&gt;</c>。
    /// </summary>
    private SqlStatement ParseDescribe()
    {
        // 当前 token 是 DESCRIBE 或 DESC
        Advance();
        if (Current.Kind == TokenKind.KeywordTable)
        {
            Advance();
            return new DescribeTableStatement(ExpectIdentifierName());
        }

        if (Current.Kind == TokenKind.KeywordDocument)
        {
            Advance();
            Expect(TokenKind.KeywordCollection);
            return new DescribeDocumentCollectionStatement(ExpectIdentifierName());
        }

        if (Current.Kind == TokenKind.KeywordMeasurement)
            Advance();
        var name = ExpectIdentifierName();
        return new DescribeMeasurementStatement(name);
    }

    private string ExpectStringLiteral()
    {
        if (Current.Kind != TokenKind.StringLiteral)
            throw Error("期望字符串字面量");
        var value = Current.Text;
        Advance();
        return value;
    }

    /// <summary>数据库名：标识符或 <c>*</c>（通配）。</summary>
    private string ExpectDatabaseNameOrStar()
    {
        if (Current.Kind == TokenKind.Star)
        {
            Advance();
            return "*";
        }
        return ExpectIdentifierName();
    }

    private SqlParseException Error(string message)
        => new(message, Current.Position);
}
