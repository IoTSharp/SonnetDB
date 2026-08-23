using SonnetDB.Sql.Ast;

using System.Globalization;
using SonnetDB.Graphs;
using SonnetDB.Modbus;

namespace SonnetDB.Sql;

/// <summary>
/// 递归下降 SQL 语法分析器：把 token 流转换为 <see cref="SqlStatement"/> AST。
/// </summary>
/// <remarks>
/// 支持的语句：<c>CREATE MEASUREMENT</c> / <c>INSERT INTO ... VALUES</c> /
/// <c>SELECT ... FROM ... [WHERE ...] [GROUP BY time(...)]</c> / <c>DELETE FROM ... WHERE ...</c> /
/// <c>CREATE TABLE</c> / <c>CREATE VIEW</c> / <c>UPDATE</c> 等关系表 MVP 语句。
/// 不做任何语义校验（measurement / column 是否存在留给执行层）。
/// </remarks>
public sealed class SqlParser
{
    private readonly IReadOnlyList<Token> _tokens;
    private readonly string? _source;
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
        : this(tokens, source: null)
    {
    }

    private SqlParser(IReadOnlyList<Token> tokens, string? source)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        if (tokens.Count == 0 || tokens[^1].Kind != TokenKind.EndOfFile)
            throw new ArgumentException("token 序列必须以 EndOfFile 结尾。", nameof(tokens));
        _tokens = tokens;
        _source = source;
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
        var parser = new SqlParser(tokens, source);
        var statement = parser.ParseStatement();
        parser.ConsumeOptionalSemicolon();
        parser.ExpectEndOfFile();
        return statement;
    }

    /// <summary>解析 M40 #364 受限 GQL 风格只读查询。</summary>
    internal static SqlStatement ParseGql(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var parser = new SqlParser(SqlLexer.Tokenize(source), source);
        SqlStatement statement = parser.ParseGqlStatement();
        parser.ConsumeOptionalSemicolon();
        parser.ExpectEndOfFile();
        return statement;
    }

    private SqlStatement ParseGqlStatement()
    {
        bool explain = false;
        bool analyze = false;
        if (Current.Kind == TokenKind.KeywordExplain)
        {
            explain = true;
            Advance();
            if (IsIdentifier("analyze"))
            {
                analyze = true;
                Advance();
            }
        }

        ExpectIdentifier("use", "GQL 查询必须以 USE GRAPH 开始");
        ExpectIdentifier("graph", "GQL USE 后面期望 GRAPH");
        string graphName = ExpectIdentifierName();
        ExpectIdentifier("match", "GQL graph 名称后面期望 MATCH");
        ParsedGraphMatch match = ParseGraphMatch();
        ExpectIdentifier("return", "GQL MATCH 后面期望 RETURN");

        bool distinct = false;
        if (Current.Kind == TokenKind.KeywordDistinct)
        {
            distinct = true;
            Advance();
        }

        IReadOnlyList<SelectItem> columns = ParseSelectList();
        IReadOnlyList<SelectItem> projections = BuildGqlOutputProjections(columns);
        IReadOnlyList<OrderBySpec> orderByItems = ParseOptionalOrderBy();
        PaginationSpec? pagination = ParseOptionalPagination();
        var select = new SelectStatement(
            projections,
            "__graph_table__",
            Where: null,
            GroupBy: Array.Empty<SqlExpression>(),
            Pagination: pagination,
            OrderBy: orderByItems.Count == 0 ? null : orderByItems[0],
            OrderByItems: orderByItems,
            Distinct: distinct)
        {
            GraphTable = match.ToSource(graphName, columns),
        };
        return explain
            ? new ExplainStatement(select) { Analyze = analyze }
            : select;
    }

    private SqlParseException GqlError(string message) => Error("GQL: " + message);

    private IReadOnlyList<SelectItem> BuildGqlOutputProjections(IReadOnlyList<SelectItem> columns)
    {
        var projections = new List<SelectItem>(columns.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (SelectItem column in columns)
        {
            if (column.Expression is StarExpression)
                throw GqlError("RETURN 必须显式列出变量属性，不支持 '*'");

            string name = column.Alias
                ?? (column.Expression as IdentifierExpression)?.Name
                ?? "expression";
            if (!seen.Add(name))
                throw GqlError($"RETURN 输出列 '{name}' 重复，请使用 AS 区分");
            projections.Add(new SelectItem(new IdentifierExpression(name), Alias: null));
        }
        return projections;
    }

    /// <summary>解析 1 ~ N 条以分号分隔的语句（末尾分号可选）。</summary>
    /// <param name="source">SQL 源文本。</param>
    /// <returns>语句列表。</returns>
    public static IReadOnlyList<SqlStatement> ParseScript(string source)
    {
        var tokens = SqlLexer.Tokenize(source);
        var parser = new SqlParser(tokens, source);
        var list = new List<SqlStatement>();
        while (parser.Current.Kind != TokenKind.EndOfFile)
        {
            list.Add(parser.ParseStatement());
            parser.ConsumeOptionalSemicolon();
        }
        return list;
    }

    /// <summary>
    /// 解析独立 SQL 谓词表达式，供持久化约束重建 AST。
    /// </summary>
    /// <param name="source">不含外围 <c>CHECK (...)</c> 的表达式文本。</param>
    /// <returns>已解析表达式。</returns>
    public static SqlExpression ParsePredicate(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        var parser = new SqlParser(SqlLexer.Tokenize(source), source);
        var expression = parser.ParseExpression();
        parser.ExpectEndOfFile();
        return expression;
    }

    /// <summary>解析下一条语句。</summary>
    public SqlStatement ParseStatement()
    {
        if (IsIdentifier("refresh"))
            return ParseRefreshMaterializedView();
        if (IsIdentifier("call"))
            return ParseCallProcedure();
        if (IsIdentifier("analyze"))
            return ParseAnalyzeTable();
        if (IsIdentifier("upsert"))
            return ParseGraphUpsert();

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
            TokenKind.KeywordTruncate => ParseTruncate(),
            TokenKind.KeywordUpdate => ParseUpdate(),
            TokenKind.KeywordWrite => ParseWrite(),
            TokenKind.KeywordDrop => ParseDrop(),
            TokenKind.KeywordAlter => ParseAlter(),
            TokenKind.KeywordGrant => ParseGrant(),
            TokenKind.KeywordRevoke => ParseRevoke(),
            TokenKind.KeywordShow => ParseShow(),
            TokenKind.KeywordExplain => ParseExplain(),
            TokenKind.KeywordIssue => ParseIssue(),
            TokenKind.KeywordDescribe => ParseDescribe(),
            TokenKind.KeywordDesc => ParseDescribe(),
            _ => throw Error("期望 CREATE / REFRESH / CALL / INSERT / IMPORT / SELECT / DELETE / TRUNCATE / UPDATE / WRITE / DROP / ALTER / GRANT / REVOKE / SHOW / EXPLAIN / ISSUE / DESCRIBE / BEGIN / COMMIT / ROLLBACK 关键字"),
        };
    }

    /// <summary>解析单表、单列、显式阶段的受限 Modbus source 写入。</summary>
    private WriteModbusStatement ParseWrite()
    {
        Expect(TokenKind.KeywordWrite);
        ExpectIdentifier("modbus", "WRITE 后面期望 MODBUS");
        string tableName = ExpectIdentifierName();
        Expect(TokenKind.KeywordSet);
        string columnName = ExpectIdentifierName();
        Expect(TokenKind.Equal);
        SqlExpression value = ParseExpression();

        if (IsIdentifier("preview"))
        {
            Advance();
            return new WriteModbusStatement(
                tableName,
                columnName,
                value,
                ModbusWriteMode.Preview);
        }

        if (IsIdentifier("dry"))
        {
            Advance();
            ExpectIdentifier("run", "WRITE MODBUS DRY 后面期望 RUN");
            return new WriteModbusStatement(
                tableName,
                columnName,
                value,
                ModbusWriteMode.DryRun);
        }

        if (IsIdentifier("confirm"))
        {
            Advance();
            SqlExpression confirmationToken = ParseExpression();
            return new WriteModbusStatement(
                tableName,
                columnName,
                value,
                ModbusWriteMode.Confirm,
                confirmationToken);
        }

        throw Error("WRITE MODBUS 必须以 DRY RUN、PREVIEW 或 CONFIRM <token> 结束");
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

        if (IsIdentifier("modbus"))
        {
            if (unique || sparse || ttl)
                throw Error("CREATE MODBUS 不支持 UNIQUE / SPARSE / TTL 修饰符");
            Advance();
            return ParseCreateModbusBody();
        }

        if (IsIdentifier("property"))
        {
            if (unique || sparse || ttl)
                throw Error("CREATE PROPERTY GRAPH 不支持 UNIQUE / SPARSE / TTL 修饰符");
            Advance();
            ExpectIdentifier("graph", "CREATE PROPERTY 后面期望 GRAPH");
            return ParseCreatePropertyGraphBody();
        }

        if (IsIdentifier("graph"))
        {
            if (unique || sparse || ttl)
                throw Error("CREATE GRAPH 不支持 UNIQUE / SPARSE / TTL 修饰符");
            Advance();
            bool ifNotExists = ParseOptionalIfNotExists();
            return new CreateGraphStatement(ExpectIdentifierName(), ifNotExists);
        }

        if (IsIdentifier("materialized"))
        {
            if (unique || sparse || ttl)
                throw Error("CREATE MATERIALIZED VIEW 不支持 UNIQUE / SPARSE / TTL 修饰符");
            Advance();
            if (!IsIdentifier("view"))
                throw Error("CREATE MATERIALIZED 后面期望 VIEW");
            return ParseCreateMaterializedViewBody();
        }

        if (IsIdentifier("view"))
        {
            if (unique || sparse || ttl)
                throw Error("CREATE VIEW 不支持 UNIQUE / SPARSE / TTL 修饰符");
            return ParseCreateViewBody();
        }

        if (IsIdentifier("procedure"))
        {
            if (unique || sparse || ttl)
                throw Error("CREATE PROCEDURE 不支持 UNIQUE / SPARSE / TTL 修饰符");
            return ParseCreateProcedureBody();
        }

        if (IsIdentifier("trigger"))
        {
            if (unique || sparse || ttl)
                throw Error("CREATE TRIGGER 不支持 UNIQUE / SPARSE / TTL 修饰符");
            return ParseCreateTriggerBody();
        }

        return Current.Kind switch
        {
            TokenKind.KeywordMeasurement => ParseCreateMeasurementBody(),
            TokenKind.KeywordTable => ParseCreateTableBody(),
            TokenKind.KeywordDocument => ParseCreateDocumentBody(),
            TokenKind.KeywordJson => ParseCreateJsonBody(),
            TokenKind.KeywordFullText => ParseCreateFullTextBody(),
            TokenKind.KeywordVector => ParseCreateVectorBody(),
            TokenKind.KeywordUser => ParseCreateUserBody(),
            TokenKind.KeywordDatabase => ParseCreateDatabaseBody(),
            _ => throw Error("CREATE 后面期望 MODBUS / MEASUREMENT / TABLE / VIEW / PROCEDURE / TRIGGER / DOCUMENT COLLECTION / JSON INDEX / FULLTEXT INDEX / VECTOR INDEX / INDEX / USER / DATABASE"),
        };
    }

    private CreatePropertyGraphStatement ParseCreatePropertyGraphBody()
    {
        bool ifNotExists = ParseOptionalIfNotExists();
        string name = ExpectIdentifierName();
        ExpectIdentifier("vertex", "CREATE PROPERTY GRAPH 后面期望 VERTEX TABLES");
        Expect(TokenKind.KeywordTables);
        IReadOnlyList<PropertyGraphVertexTableClause> vertices = ParsePropertyGraphVertexTables();

        var edges = new List<PropertyGraphEdgeTableClause>();
        if (IsIdentifier("edge"))
        {
            Advance();
            Expect(TokenKind.KeywordTables);
            edges.AddRange(ParsePropertyGraphEdgeTables());
        }
        return new CreatePropertyGraphStatement(name, vertices, edges, ifNotExists);
    }

    private IReadOnlyList<PropertyGraphVertexTableClause> ParsePropertyGraphVertexTables()
    {
        Expect(TokenKind.LeftParen);
        var mappings = new List<PropertyGraphVertexTableClause>();
        while (true)
        {
            string tableName = ExpectIdentifierName();
            Expect(TokenKind.KeywordKey);
            IReadOnlyList<string> keys = ParsePropertyGraphColumnList();
            ExpectIdentifier("label", "VERTEX TABLE KEY 后面期望 LABEL");
            string label = ExpectIdentifierName();
            ExpectIdentifier("properties", "VERTEX TABLE LABEL 后面期望 PROPERTIES");
            IReadOnlyList<string> properties = ParsePropertyGraphColumnList(allowEmpty: true);
            mappings.Add(new PropertyGraphVertexTableClause(tableName, keys, label, properties));
            if (Current.Kind != TokenKind.Comma)
                break;
            Advance();
        }
        Expect(TokenKind.RightParen);
        return mappings;
    }

    private IReadOnlyList<PropertyGraphEdgeTableClause> ParsePropertyGraphEdgeTables()
    {
        Expect(TokenKind.LeftParen);
        var mappings = new List<PropertyGraphEdgeTableClause>();
        while (true)
        {
            string tableName = ExpectIdentifierName();
            Expect(TokenKind.KeywordKey);
            IReadOnlyList<string> keys = ParsePropertyGraphColumnList();
            ExpectIdentifier("source", "EDGE TABLE KEY 后面期望 SOURCE KEY");
            Expect(TokenKind.KeywordKey);
            IReadOnlyList<string> sourceColumns = ParsePropertyGraphColumnList();
            Expect(TokenKind.KeywordReferences);
            string sourceTable = ExpectIdentifierName();
            IReadOnlyList<string> sourceReferences = ParsePropertyGraphColumnList();
            ExpectIdentifier("destination", "EDGE TABLE SOURCE 后面期望 DESTINATION KEY");
            Expect(TokenKind.KeywordKey);
            IReadOnlyList<string> destinationColumns = ParsePropertyGraphColumnList();
            Expect(TokenKind.KeywordReferences);
            string destinationTable = ExpectIdentifierName();
            IReadOnlyList<string> destinationReferences = ParsePropertyGraphColumnList();
            ExpectIdentifier("label", "EDGE TABLE DESTINATION 后面期望 LABEL");
            string label = ExpectIdentifierName();
            ExpectIdentifier("properties", "EDGE TABLE LABEL 后面期望 PROPERTIES");
            IReadOnlyList<string> properties = ParsePropertyGraphColumnList(allowEmpty: true);
            mappings.Add(new PropertyGraphEdgeTableClause(
                tableName,
                keys,
                sourceTable,
                sourceColumns,
                sourceReferences,
                destinationTable,
                destinationColumns,
                destinationReferences,
                label,
                properties));
            if (Current.Kind != TokenKind.Comma)
                break;
            Advance();
        }
        Expect(TokenKind.RightParen);
        return mappings;
    }

    private IReadOnlyList<string> ParsePropertyGraphColumnList(bool allowEmpty = false)
    {
        Expect(TokenKind.LeftParen);
        var columns = new List<string>();
        if (allowEmpty && Current.Kind == TokenKind.RightParen)
        {
            Advance();
            return columns;
        }
        columns.Add(ExpectColumnName());
        while (Current.Kind == TokenKind.Comma)
        {
            Advance();
            columns.Add(ExpectColumnName());
        }
        Expect(TokenKind.RightParen);
        return columns;
    }

    /// <summary>
    /// 解析 <c>CREATE MODBUS SOURCE</c> 或 <c>CREATE MODBUS ENDPOINT</c> 的对象分派。
    /// </summary>
    private SqlStatement ParseCreateModbusBody()
    {
        if (IsIdentifier("source"))
            return ParseCreateModbusSourceBody();
        if (IsIdentifier("endpoint"))
            return ParseCreateModbusEndpointBody();

        throw Error("CREATE MODBUS 后面期望 SOURCE / ENDPOINT");
    }

    /// <summary>
    /// 解析主动轮询 source 的连接、轮询和默认编码选项。
    /// </summary>
    private CreateModbusSourceStatement ParseCreateModbusSourceBody()
    {
        Advance(); // SOURCE 是上下文关键字。
        string name = ExpectIdentifierName();
        Expect(TokenKind.KeywordWith);
        Expect(TokenKind.LeftParen);

        string? host = null;
        int port = 502;
        byte unitId = 1;
        var addressingMode = ModbusAddressingMode.Modicon;
        int pollIntervalMilliseconds = 1_000;
        int timeoutMilliseconds = 3_000;
        int retryCount = 3;
        var byteOrder = ModbusByteOrder.BigEndian;
        var wordOrder = ModbusWordOrder.BigEndian;
        var enabled = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (Current.Kind != TokenKind.RightParen)
        {
            string option = ExpectUniqueModbusOption(seen);
            switch (option.ToUpperInvariant())
            {
                case "TRANSPORT":
                    ParseModbusTcpTransport();
                    break;
                case "ENDPOINT":
                    (host, port) = ParseModbusHostAndPort(ExpectStringLiteral(), "ENDPOINT");
                    break;
                case "UNIT_ID":
                    unitId = ParseModbusUnitId();
                    break;
                case "POLL_INTERVAL":
                    pollIntervalMilliseconds = ParseModbusDurationMilliseconds("POLL_INTERVAL");
                    break;
                case "TIMEOUT":
                    timeoutMilliseconds = ParseModbusDurationMilliseconds("TIMEOUT");
                    break;
                case "RETRY":
                    retryCount = ExpectNonNegativeInt("RETRY 必须是非负整数");
                    break;
                case "ADDRESSING":
                    addressingMode = ParseModbusAddressingMode();
                    break;
                case "BYTE_ORDER":
                    byteOrder = ParseModbusByteOrder();
                    break;
                case "WORD_ORDER":
                    wordOrder = ParseModbusWordOrder();
                    break;
                case "ENABLED":
                    enabled = ParseModbusBoolean("ENABLED");
                    break;
                case "AUDIT":
                    ParseMandatoryModbusAudit();
                    break;
                default:
                    throw Error($"CREATE MODBUS SOURCE 不支持选项 {option}");
            }

            ConsumeModbusOptionSeparator();
        }

        Expect(TokenKind.RightParen);
        if (string.IsNullOrWhiteSpace(host))
            throw Error("CREATE MODBUS SOURCE 必须声明 ENDPOINT");
        if (!seen.Contains("BYTE_ORDER") || !seen.Contains("WORD_ORDER"))
            throw Error("CREATE MODBUS SOURCE 必须显式声明 BYTE_ORDER 和 WORD_ORDER");

        return new CreateModbusSourceStatement(new ModbusSourceDefinition(
            name,
            host,
            port,
            unitId,
            addressingMode,
            pollIntervalMilliseconds,
            timeoutMilliseconds,
            retryCount,
            byteOrder,
            wordOrder,
            enabled));
    }

    /// <summary>
    /// 解析从站 endpoint 的监听、安全边界和 staged 写入选项。
    /// </summary>
    private CreateModbusEndpointStatement ParseCreateModbusEndpointBody()
    {
        Advance(); // ENDPOINT 是上下文关键字。
        string name = ExpectIdentifierName();
        Expect(TokenKind.KeywordWith);
        Expect(TokenKind.LeftParen);

        string bindAddress = "127.0.0.1";
        int port = 502;
        byte unitId = 1;
        int maxConnections = 32;
        IReadOnlyList<string> allowlist = Array.Empty<string>();
        var addressingMode = ModbusAddressingMode.Modicon;
        var byteOrder = ModbusByteOrder.BigEndian;
        var wordOrder = ModbusWordOrder.BigEndian;
        var writePolicy = ModbusEndpointWritePolicy.Staged;
        var enabled = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (Current.Kind != TokenKind.RightParen)
        {
            string option = ExpectUniqueModbusOption(seen);
            switch (option.ToUpperInvariant())
            {
                case "TRANSPORT":
                    ParseModbusTcpTransport();
                    break;
                case "BIND":
                    (bindAddress, port) = ParseModbusHostAndPort(ExpectStringLiteral(), "BIND");
                    break;
                case "UNIT_ID":
                    unitId = ParseModbusUnitId();
                    break;
                case "ADDRESSING":
                    addressingMode = ParseModbusAddressingMode();
                    break;
                case "BYTE_ORDER":
                    byteOrder = ParseModbusByteOrder();
                    break;
                case "WORD_ORDER":
                    wordOrder = ParseModbusWordOrder();
                    break;
                case "WRITE_POLICY":
                    writePolicy = ParseModbusEndpointWritePolicy();
                    break;
                case "ALLOWLIST":
                    allowlist = ParseModbusAllowlist();
                    break;
                case "MAX_CONNECTIONS":
                    maxConnections = ExpectPositiveInt("MAX_CONNECTIONS 必须是正整数");
                    break;
                case "ENABLED":
                    enabled = ParseModbusBoolean("ENABLED");
                    break;
                case "AUDIT":
                    ParseMandatoryModbusAudit();
                    break;
                default:
                    throw Error($"CREATE MODBUS ENDPOINT 不支持选项 {option}");
            }

            ConsumeModbusOptionSeparator();
        }

        Expect(TokenKind.RightParen);
        if (!seen.Contains("BIND"))
            throw Error("CREATE MODBUS ENDPOINT 必须声明 BIND");
        if (!seen.Contains("BYTE_ORDER") || !seen.Contains("WORD_ORDER"))
            throw Error("CREATE MODBUS ENDPOINT 必须显式声明 BYTE_ORDER 和 WORD_ORDER");
        return new CreateModbusEndpointStatement(new ModbusEndpointDefinition(
            name,
            bindAddress,
            port,
            unitId,
            maxConnections,
            allowlist,
            addressingMode,
            byteOrder,
            wordOrder,
            writePolicy,
            enabled));
    }

    private CreateProcedureStatement ParseCreateProcedureBody()
    {
        Advance(); // PROCEDURE 保持为非保留标识符。
        string name = ExpectIdentifierName();
        Expect(TokenKind.LeftParen);
        var parameters = new List<SqlProcedureParameter>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (Current.Kind != TokenKind.RightParen)
        {
            while (true)
            {
                if (IsIdentifier("out") || IsIdentifier("inout"))
                    throw Error("SQL 过程首版只支持 IN 参数，不支持 OUT / INOUT");
                Expect(TokenKind.KeywordIn);
                string parameterName = ExpectIdentifierName();
                if (!names.Add(parameterName))
                    throw Error($"过程参数 '{parameterName}' 重复声明");
                parameters.Add(new SqlProcedureParameter(parameterName, ParseProcedureParameterType()));
                if (Current.Kind != TokenKind.Comma)
                    break;
                Advance();
            }
        }
        Expect(TokenKind.RightParen);
        var (body, bodySql) = ParseLanguageSqlBody("CREATE PROCEDURE");
        return new CreateProcedureStatement(name, parameters, body, bodySql);
    }

    private CreateTriggerStatement ParseCreateTriggerBody()
    {
        Advance(); // TRIGGER 保持为非保留标识符。
        string name = ExpectIdentifierName();
        ExpectIdentifier("after", "CREATE TRIGGER 后面期望 AFTER");
        SqlTriggerEvent triggerEvent = Current.Kind switch
        {
            TokenKind.KeywordInsert => SqlTriggerEvent.Insert,
            TokenKind.KeywordUpdate => SqlTriggerEvent.Update,
            TokenKind.KeywordDelete => SqlTriggerEvent.Delete,
            _ => throw Error("AFTER 后面期望 INSERT / UPDATE / DELETE"),
        };
        Advance();
        Expect(TokenKind.KeywordOn);
        string tableName = ExpectIdentifierName();
        Expect(TokenKind.KeywordFor);
        ExpectIdentifier("each", "FOR 后面期望 EACH ROW");
        ExpectIdentifier("row", "FOR EACH 后面期望 ROW");

        SqlExpression? when = null;
        string? whenSql = null;
        if (Current.Kind == TokenKind.KeywordWhen)
        {
            Advance();
            Expect(TokenKind.LeftParen);
            when = ParseExpression();
            Expect(TokenKind.RightParen);
            whenSql = SqlExpressionFormatter.Format(when);
        }

        var (body, bodySql) = ParseLanguageSqlBody("CREATE TRIGGER");
        return new CreateTriggerStatement(
            name,
            tableName,
            triggerEvent,
            when,
            whenSql,
            body,
            bodySql);
    }

    private SqlProcedureParameterType ParseProcedureParameterType()
    {
        var type = Current.Kind switch
        {
            TokenKind.KeywordInt => SqlProcedureParameterType.Int64,
            TokenKind.KeywordFloat => SqlProcedureParameterType.Float64,
            TokenKind.KeywordBool => SqlProcedureParameterType.Boolean,
            TokenKind.KeywordString => SqlProcedureParameterType.String,
            _ => throw Error("过程参数类型只支持 INT / FLOAT / BOOL / STRING"),
        };
        Advance();
        return type;
    }

    private (IReadOnlyList<SqlStatement> Statements, string Sql) ParseLanguageSqlBody(string context)
    {
        ExpectIdentifier("language", $"{context} 后面期望 LANGUAGE SQL");
        ExpectIdentifier("sql", $"{context} LANGUAGE 后面只支持 SQL");
        Expect(TokenKind.KeywordAs);
        Expect(TokenKind.KeywordBegin);

        int bodyTokenStart = _index;
        int bodyStart = Current.Position;
        int depth = 1;
        int cursor = _index;
        for (; cursor < _tokens.Count; cursor++)
        {
            TokenKind kind = _tokens[cursor].Kind;
            if (kind is TokenKind.KeywordBegin or TokenKind.KeywordCase)
            {
                depth++;
            }
            else if (kind == TokenKind.KeywordEnd)
            {
                depth--;
                if (depth == 0)
                    break;
            }
            else if (kind == TokenKind.EndOfFile)
            {
                break;
            }
        }

        if (cursor >= _tokens.Count || _tokens[cursor].Kind != TokenKind.KeywordEnd || depth != 0)
            throw Error($"{context} SQL body 缺少 END");

        int bodyEnd = _tokens[cursor].Position;
        string bodySql = _source is null
            ? FormatTokenRange(bodyTokenStart, cursor)
            : _source[bodyStart..bodyEnd].Trim();
        if (string.IsNullOrWhiteSpace(bodySql))
            throw Error($"{context} SQL body 不能为空");

        _index = cursor + 1;
        IReadOnlyList<SqlStatement> statements = ParseScript(bodySql);
        return (statements, bodySql);
    }

    private CallProcedureStatement ParseCallProcedure()
    {
        Advance(); // CALL 保持为非保留标识符。
        string name = ExpectIdentifierName();
        Expect(TokenKind.LeftParen);
        var arguments = new List<SqlExpression>();
        if (Current.Kind != TokenKind.RightParen)
        {
            while (true)
            {
                arguments.Add(ParseExpression());
                if (Current.Kind != TokenKind.Comma)
                    break;
                Advance();
            }
        }
        Expect(TokenKind.RightParen);
        return new CallProcedureStatement(name, arguments);
    }

    private CreateViewStatement ParseCreateViewBody()
    {
        Advance(); // VIEW 保持为非保留标识符，避免破坏已有同名列和对象。
        bool ifNotExists = ParseOptionalIfNotExists();
        string name = ExpectIdentifierName();
        Expect(TokenKind.KeywordAs);
        if (Current.Kind != TokenKind.KeywordSelect)
            throw Error("CREATE VIEW ... AS 后面期望 SELECT");

        int definitionStart = Current.Position;
        int definitionTokenStart = _index;
        var query = ParseSelect();
        int definitionEnd = Current.Position;
        string definitionSql = _source is null
            ? FormatTokenRange(definitionTokenStart, _index)
            : _source[definitionStart..definitionEnd].Trim();
        return new CreateViewStatement(name, query, definitionSql, ifNotExists);
    }

    private CreateMaterializedViewStatement ParseCreateMaterializedViewBody()
    {
        Advance(); // VIEW 保持为非保留标识符。
        bool ifNotExists = ParseOptionalIfNotExists();
        string name = ExpectIdentifierName();
        Expect(TokenKind.KeywordAs);
        if (Current.Kind != TokenKind.KeywordSelect)
            throw Error("CREATE MATERIALIZED VIEW ... AS 后面期望 SELECT");

        int definitionStart = Current.Position;
        int definitionTokenStart = _index;
        var query = ParseSelect();
        int definitionEnd = Current.Position;
        string definitionSql = _source is null
            ? FormatTokenRange(definitionTokenStart, _index)
            : _source[definitionStart..definitionEnd].Trim();
        return new CreateMaterializedViewStatement(name, query, definitionSql, ifNotExists);
    }

    private RefreshMaterializedViewStatement ParseRefreshMaterializedView()
    {
        Advance(); // REFRESH 保持为非保留标识符。
        if (!IsIdentifier("materialized"))
            throw Error("REFRESH 后面期望 MATERIALIZED VIEW");
        Advance();
        if (!IsIdentifier("view"))
            throw Error("REFRESH MATERIALIZED 后面期望 VIEW");
        Advance();
        return new RefreshMaterializedViewStatement(ExpectIdentifierName());
    }

    private string FormatTokenRange(int start, int end)
    {
        var builder = new System.Text.StringBuilder();
        for (int i = start; i < end; i++)
        {
            if (builder.Length != 0)
                builder.Append(' ');
            var token = _tokens[i];
            switch (token.Kind)
            {
                case TokenKind.IdentifierLiteral:
                    builder.Append('"').Append(token.Text.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
                    break;
                case TokenKind.StringLiteral:
                    builder.Append('\'').Append(token.Text.Replace("'", "''", StringComparison.Ordinal)).Append('\'');
                    break;
                case TokenKind.DurationLiteral:
                    builder.Append(token.IntegerValue).Append("ms");
                    break;
                case TokenKind.Parameter:
                    builder.Append(string.IsNullOrEmpty(token.Text) ? "?" : "@" + token.Text);
                    break;
                default:
                    builder.Append(token.Text);
                    break;
            }
        }
        return builder.ToString();
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

    private CreateDocumentVectorIndexStatement ParseCreateVectorBody()
    {
        Expect(TokenKind.KeywordVector);
        ExpectIndexKeyword("CREATE VECTOR 后面期望 INDEX");

        var ifNotExists = ParseOptionalIfNotExists();
        var indexName = ExpectIdentifierName();
        Expect(TokenKind.KeywordOn);
        var collectionName = ExpectIdentifierName();
        Expect(TokenKind.LeftParen);
        string path = Current.Kind == TokenKind.StringLiteral
            ? ExpectStringLiteral()
            : ExpectColumnName();
        Expect(TokenKind.RightParen);

        Expect(TokenKind.KeywordWith);
        Expect(TokenKind.LeftParen);

        int? dimensions = null;
        int? m = null;
        int? efConstruction = null;
        int? efSearch = null;
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
                int value = ExpectPositiveInt($"向量索引参数 '{parameterName}' 后面期望正整数");
                if (IsParameter(parameterName, "dimensions", "dim", "dims"))
                {
                    if (dimensions is not null)
                        throw Error("向量索引参数 dimensions 重复声明");
                    dimensions = value;
                }
                else if (string.Equals(parameterName, "m", StringComparison.OrdinalIgnoreCase))
                {
                    if (m is not null)
                        throw Error("向量索引参数 m 重复声明");
                    m = value;
                }
                else if (IsParameter(parameterName, "ef_construction", "efconstruction"))
                {
                    if (efConstruction is not null)
                        throw Error("向量索引参数 ef_construction 重复声明");
                    efConstruction = value;
                }
                else if (IsParameter(parameterName, "ef_search", "efsearch", "ef"))
                {
                    if (efSearch is not null)
                        throw Error("向量索引参数 ef_search 重复声明");
                    efSearch = value;
                }
                else
                {
                    throw Error($"未知的向量索引参数 '{parameterName}'，仅支持 dimensions / metric / m / ef_construction / ef_search");
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

        if (dimensions is null)
            throw Error("向量索引声明必须提供 dimensions，例如 WITH (dimensions=384, metric='cosine')");

        return new CreateDocumentVectorIndexStatement(
            indexName,
            collectionName,
            path,
            dimensions.Value,
            metric,
            m ?? 16,
            efConstruction ?? 200,
            efSearch ?? 64,
            ifNotExists);
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
        var checkConstraints = new List<TableCheckConstraintClause>();
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
            else if (Current.Kind == TokenKind.KeywordCheck)
            {
                checkConstraints.Add(ParseCheckConstraintClause(constraintName: null));
            }
            else if (IsIdentifier("constraint"))
            {
                Advance();
                var constraintName = ExpectIdentifierName();
                if (Current.Kind != TokenKind.KeywordCheck)
                    throw Error("CREATE TABLE 的命名约束当前期望 CHECK");
                checkConstraints.Add(ParseCheckConstraintClause(constraintName));
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
        ModbusTableBindingClause? modbusBinding = ParseOptionalModbusTableBinding();
        ValidateModbusTableSyntax(columns, modbusBinding);

        return new CreateTableStatement(name, columns, primaryKey, ifNotExists, foreignKeys, checkConstraints)
        {
            ModbusBinding = modbusBinding,
        };
    }

    private TableColumnDefinition ParseTableColumnDefinition()
    {
        var columnName = ExpectColumnName();
        var dataType = ParseTableDataType();
        ColumnNullability nullability = ColumnNullability.Unspecified;
        SqlExpression? defaultExpression = null;
        var isRowVersion = false;
        var isAutoIncrement = false;
        ParseTableColumnModifiers(ref nullability, ref defaultExpression, ref isRowVersion, ref isAutoIncrement);

        var isModbusSampleTime = false;
        var isModbusQuality = false;
        ModbusColumnMappingClause? modbusMapping = null;
        while (true)
        {
            if (IsIdentifier("sample_time"))
            {
                if (isModbusSampleTime)
                    throw Error("SAMPLE_TIME 子句重复声明");
                isModbusSampleTime = true;
                Advance();
                continue;
            }

            if (IsIdentifier("quality"))
            {
                if (isModbusQuality)
                    throw Error("QUALITY 子句重复声明");
                isModbusQuality = true;
                Advance();
                continue;
            }

            if (Current.Kind == TokenKind.KeywordFrom || IsIdentifier("expose"))
            {
                if (modbusMapping is not null)
                    throw Error("Modbus 列映射重复声明");
                modbusMapping = ParseModbusColumnMapping(dataType);
                continue;
            }

            break;
        }

        if (isRowVersion && dataType != SqlDataType.Int64)
            throw Error("ROWVERSION 列必须使用 INT 类型");
        if (isAutoIncrement && dataType != SqlDataType.Int64)
            throw Error("AUTO_INCREMENT 列必须使用 INT 类型");
        if (isAutoIncrement && isRowVersion)
            throw Error("AUTO_INCREMENT 与 ROWVERSION 不能声明在同一列上");
        if (isAutoIncrement && defaultExpression is not null)
            throw Error("AUTO_INCREMENT 列不允许声明 DEFAULT");
        if (isAutoIncrement && nullability == ColumnNullability.Nullable)
            throw Error("AUTO_INCREMENT 列不允许声明 NULL");
        if (isAutoIncrement && nullability == ColumnNullability.Unspecified)
            nullability = ColumnNullability.NotNull;
        if (isModbusSampleTime && dataType != SqlDataType.DateTime)
            throw Error("SAMPLE_TIME 列必须使用 DATETIME 类型");
        if (isModbusSampleTime && modbusMapping is not null)
            throw Error("SAMPLE_TIME 列不能同时声明 Modbus 地址映射");
        if (isModbusQuality && dataType != SqlDataType.Int64)
            throw Error("QUALITY 列必须使用 INT 类型");
        if (isModbusQuality && modbusMapping is not null)
            throw Error("QUALITY 列不能同时声明 Modbus 地址映射");
        if (isModbusSampleTime && isModbusQuality)
            throw Error("同一列不能同时声明 SAMPLE_TIME 和 QUALITY");

        return new TableColumnDefinition(columnName, dataType, nullability, isRowVersion)
        {
            IsAutoIncrement = isAutoIncrement,
            DefaultExpression = defaultExpression,
            ModbusMapping = modbusMapping,
            IsModbusSampleTime = isModbusSampleTime,
            IsModbusQuality = isModbusQuality,
        };
    }

    /// <summary>
    /// 解析关系表列上的 <c>FROM MODBUS</c> 或 <c>EXPOSE AS MODBUS</c> 映射。
    /// </summary>
    private ModbusColumnMappingClause ParseModbusColumnMapping(SqlDataType sqlDataType)
    {
        ModbusMappingDirection direction;
        if (Current.Kind == TokenKind.KeywordFrom)
        {
            direction = ModbusMappingDirection.SourceToTable;
            Advance();
            ExpectIdentifier("modbus", "FROM 后面期望 MODBUS");
        }
        else
        {
            direction = ModbusMappingDirection.TableToEndpoint;
            Advance(); // EXPOSE 是上下文关键字。
            Expect(TokenKind.KeywordAs);
            ExpectIdentifier("modbus", "EXPOSE AS 后面期望 MODBUS");
        }

        ModbusRegisterArea area = ParseModbusRegisterArea();
        Expect(TokenKind.LeftParen);
        int declaredAddress = ExpectNonNegativeInt("Modbus 声明地址必须是非负整数");
        int? explicitCount = null;
        if (Current.Kind == TokenKind.Comma)
        {
            Advance();
            explicitCount = ExpectPositiveInt("Modbus 地址数量必须是正整数");
        }
        Expect(TokenKind.RightParen);

        int? bitIndex = null;
        if (Current.Kind == TokenKind.Dot)
        {
            Advance();
            ExpectIdentifier("bit", "Modbus 地址点号后面期望 BIT");
            Expect(TokenKind.LeftParen);
            bitIndex = ExpectNonNegativeInt("BIT 索引必须位于 0..15");
            if (bitIndex > 15)
                throw Error("BIT 索引必须位于 0..15");
            Expect(TokenKind.RightParen);
        }

        Expect(TokenKind.KeywordAs);
        (ModbusValueType valueType, int stringLength, int inferredCount) = ParseModbusValueType();
        if (explicitCount is not null && explicitCount.Value != inferredCount)
            throw Error($"显式 count {explicitCount.Value} 与 wire type 所需数量 {inferredCount} 不一致");

        ModbusByteOrder? byteOrder = null;
        ModbusWordOrder? wordOrder = null;
        decimal scale = 1m;
        decimal offset = 0m;
        var access = ModbusAccessMode.Read;
        var scaleSpecified = false;
        var offsetSpecified = false;
        var accessSpecified = false;

        while (true)
        {
            if (IsIdentifier("byte_order"))
            {
                if (byteOrder is not null)
                    throw Error("BYTE_ORDER 子句重复声明");
                Advance();
                byteOrder = ParseModbusByteOrder();
                continue;
            }

            if (IsIdentifier("word_order"))
            {
                if (wordOrder is not null)
                    throw Error("WORD_ORDER 子句重复声明");
                Advance();
                wordOrder = ParseModbusWordOrder();
                continue;
            }

            if (IsIdentifier("scale"))
            {
                if (scaleSpecified)
                    throw Error("SCALE 子句重复声明");
                Advance();
                scale = ParseModbusDecimal("SCALE");
                scaleSpecified = true;
                continue;
            }

            if (Current.Kind == TokenKind.KeywordOffset)
            {
                if (offsetSpecified)
                    throw Error("OFFSET 子句重复声明");
                Advance();
                offset = ParseModbusDecimal("OFFSET");
                offsetSpecified = true;
                continue;
            }

            if (IsIdentifier("access"))
            {
                if (accessSpecified)
                    throw Error("ACCESS 子句重复声明");
                Advance();
                access = ParseModbusAccessMode();
                accessSpecified = true;
                continue;
            }

            break;
        }

        ValidateModbusColumnMappingSyntax(
            sqlDataType,
            area,
            valueType,
            bitIndex,
            scale,
            scaleSpecified,
            offsetSpecified,
            access);

        return new ModbusColumnMappingClause(
            direction,
            area,
            declaredAddress,
            inferredCount,
            bitIndex,
            valueType,
            stringLength,
            byteOrder,
            wordOrder,
            scale,
            offset,
            access);
    }

    /// <summary>
    /// 校验无需 catalog 上下文即可确定的列映射类型、区域和访问约束。
    /// </summary>
    private void ValidateModbusColumnMappingSyntax(
        SqlDataType sqlDataType,
        ModbusRegisterArea area,
        ModbusValueType valueType,
        int? bitIndex,
        decimal scale,
        bool scaleSpecified,
        bool offsetSpecified,
        ModbusAccessMode access)
    {
        bool bitArea = area is ModbusRegisterArea.Coil or ModbusRegisterArea.DiscreteInput;
        bool registerArea = area is ModbusRegisterArea.HoldingRegister or ModbusRegisterArea.InputRegister;
        if (bitArea && valueType != ModbusValueType.Bit)
            throw Error("COIL / DISCRETE_INPUT 只支持 BIT wire type");
        if (bitArea && bitIndex is not null)
            throw Error(".BIT(n) 只适用于 HOLDING_REGISTER / INPUT_REGISTER");
        if (registerArea && valueType == ModbusValueType.Bit && bitIndex is null)
            throw Error("寄存器 BIT 映射必须声明 .BIT(n)");
        if (bitIndex is not null && valueType != ModbusValueType.Bit)
            throw Error("声明 .BIT(n) 时 wire type 必须是 BIT");

        if (area is ModbusRegisterArea.DiscreteInput or ModbusRegisterArea.InputRegister
            && access != ModbusAccessMode.Read)
        {
            throw Error("DISCRETE_INPUT / INPUT_REGISTER 只允许 ACCESS READ");
        }
        if (bitIndex is not null && access != ModbusAccessMode.Read)
            throw Error("寄存器 .BIT(n) 第一版只允许 ACCESS READ");

        if (valueType == ModbusValueType.String)
        {
            if (sqlDataType != SqlDataType.String)
                throw Error("STRING wire type 只能映射到 STRING 列");
            if (scaleSpecified || offsetSpecified)
                throw Error("STRING wire type 不支持 SCALE / OFFSET");
        }
        else if (valueType == ModbusValueType.Bit)
        {
            if (sqlDataType != SqlDataType.Boolean)
                throw Error("BIT wire type 只能映射到 BOOL 列");
            if (scaleSpecified || offsetSpecified)
                throw Error("BIT wire type 不支持 SCALE / OFFSET");
        }
        else if (sqlDataType is not SqlDataType.Int64 and not SqlDataType.Float64)
        {
            throw Error("数值 wire type 只能映射到 INT / FLOAT 列");
        }

        if (scale == 0m)
            throw Error("SCALE 不能为 0");
    }

    /// <summary>
    /// 解析 CREATE TABLE 尾部可选的 <c>USING MODBUS SOURCE|ENDPOINT</c> 绑定。
    /// </summary>
    private ModbusTableBindingClause? ParseOptionalModbusTableBinding()
    {
        if (Current.Kind != TokenKind.KeywordUsing)
            return null;

        Advance();
        ExpectIdentifier("modbus", "USING 后面期望 MODBUS");

        ModbusMappingDirection direction;
        if (IsIdentifier("source"))
        {
            direction = ModbusMappingDirection.SourceToTable;
            Advance();
        }
        else if (IsIdentifier("endpoint"))
        {
            direction = ModbusMappingDirection.TableToEndpoint;
            Advance();
        }
        else
        {
            throw Error("USING MODBUS 后面期望 SOURCE / ENDPOINT");
        }

        string targetName = ExpectIdentifierName();
        var tableMode = ModbusTableMode.Latest;
        var errorPolicy = ModbusErrorPolicy.KeepLast;
        var storeHistory = false;
        long? rowKey = null;
        var approvedWriteAction = ModbusApprovedWriteAction.StageOnly;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Expect(TokenKind.KeywordWith);
        Expect(TokenKind.LeftParen);
        while (Current.Kind != TokenKind.RightParen)
        {
            string option = ExpectUniqueModbusOption(seen);
            string normalized = option.ToUpperInvariant();
            if (direction == ModbusMappingDirection.SourceToTable)
            {
                switch (normalized)
                {
                    case "TABLE_MODE":
                        tableMode = ParseModbusTableMode();
                        break;
                    case "ON_ERROR":
                        errorPolicy = ParseModbusErrorPolicy();
                        break;
                    case "STORE":
                        ExpectIdentifier("history", "STORE 后面期望 HISTORY");
                        storeHistory = true;
                        break;
                    default:
                        throw Error($"USING MODBUS SOURCE 不支持选项 {option}");
                }
            }
            else
            {
                switch (normalized)
                {
                    case "ROW":
                        if (rowKey is not null)
                            throw Error("Modbus 选项 ROW KEY 重复声明");
                        Expect(TokenKind.KeywordKey);
                        rowKey = ParseModbusSignedInteger("ROW KEY");
                        break;
                    case "ROW_KEY":
                        if (rowKey is not null)
                            throw Error("Modbus 选项 ROW KEY 重复声明");
                        rowKey = ParseModbusSignedInteger("ROW_KEY");
                        break;
                    case "ON_EXTERNAL_WRITE":
                        approvedWriteAction = ParseModbusApprovedWriteAction();
                        break;
                    default:
                        throw Error($"USING MODBUS ENDPOINT 不支持选项 {option}");
                }
            }

            ConsumeModbusOptionSeparator();
        }

        Expect(TokenKind.RightParen);
        if (direction == ModbusMappingDirection.TableToEndpoint && rowKey is null)
            throw Error("USING MODBUS ENDPOINT 必须声明 ROW KEY");

        return new ModbusTableBindingClause(
            direction,
            targetName,
            tableMode,
            errorPolicy,
            storeHistory,
            rowKey,
            approvedWriteAction);
    }

    /// <summary>
    /// 校验表级 target 与列级方向一致，并约束 SAMPLE_TIME / QUALITY 的使用范围。
    /// </summary>
    private void ValidateModbusTableSyntax(
        IReadOnlyList<TableColumnDefinition> columns,
        ModbusTableBindingClause? binding)
    {
        var mappings = columns.Where(static column => column.ModbusMapping is not null).ToArray();
        var sampleTimeColumns = columns.Where(static column => column.IsModbusSampleTime).ToArray();
        var qualityColumns = columns.Where(static column => column.IsModbusQuality).ToArray();
        if (sampleTimeColumns.Length > 1)
            throw Error("一张 Modbus 表只能声明一个 SAMPLE_TIME 列");
        if (qualityColumns.Length > 1)
            throw Error("一张 Modbus 表只能声明一个 QUALITY 列");

        if (binding is null)
        {
            if (mappings.Length > 0 || sampleTimeColumns.Length > 0 || qualityColumns.Length > 0)
                throw Error("声明 Modbus 列映射、SAMPLE_TIME 或 QUALITY 时必须提供 USING MODBUS 绑定");
            return;
        }

        if (mappings.Length == 0)
            throw Error("USING MODBUS 表必须至少声明一个列映射");

        foreach (TableColumnDefinition column in mappings)
        {
            if (column.ModbusMapping!.Direction != binding.Direction)
            {
                throw Error(binding.Direction == ModbusMappingDirection.SourceToTable
                    ? "USING MODBUS SOURCE 表只能包含 FROM MODBUS 列"
                    : "USING MODBUS ENDPOINT 表只能包含 EXPOSE AS MODBUS 列");
            }
        }

        if (binding.Direction == ModbusMappingDirection.TableToEndpoint && sampleTimeColumns.Length > 0)
            throw Error("SAMPLE_TIME 仅适用于 USING MODBUS SOURCE 表");
        if (binding.Direction == ModbusMappingDirection.TableToEndpoint && qualityColumns.Length > 0)
            throw Error("QUALITY 仅适用于 USING MODBUS SOURCE 表");
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
        var isAutoIncrement = false;
        ParseTableColumnModifiers(ref nullability, ref defaultExpression, ref isRowVersion, ref isAutoIncrement);
        if (isRowVersion)
            throw Error("ALTER TABLE ADD COLUMN 当前不支持新增 ROWVERSION 列");
        if (isAutoIncrement)
            throw Error("ALTER TABLE ADD COLUMN 当前不支持新增 AUTO_INCREMENT 列");
        return new AlterTableAddColumnStatement(tableName, columnName, dataType, nullability, defaultExpression);
    }

    private void ParseTableColumnModifiers(
        ref ColumnNullability nullability,
        ref SqlExpression? defaultExpression,
        ref bool isRowVersion,
        ref bool isAutoIncrement)
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

                case TokenKind.IdentifierLiteral when IsIdentifier("auto_increment")
                    || IsIdentifier("autoincrement")
                    || IsIdentifier("identity"):
                    if (isAutoIncrement)
                        throw Error("AUTO_INCREMENT 子句重复声明");
                    if (nullability == ColumnNullability.Nullable)
                        throw Error("AUTO_INCREMENT 列不允许声明 NULL");
                    isAutoIncrement = true;
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
            onDelete = ParseOnDeleteAction();
        }

        return new TableForeignKeyClause(columns, principalTable, principalColumns, onDelete);
    }

    private TableCheckConstraintClause ParseCheckConstraintClause(string? constraintName)
    {
        Expect(TokenKind.KeywordCheck);
        Expect(TokenKind.LeftParen);
        var expression = ParseExpression();
        Expect(TokenKind.RightParen);
        return new TableCheckConstraintClause(
            constraintName,
            SqlExpressionFormatter.Format(expression),
            expression);
    }

    private ForeignKeyAction ParseOnDeleteAction()
    {
        if (Current.Kind == TokenKind.KeywordCascade)
        {
            Advance();
            return ForeignKeyAction.Cascade;
        }

        if (Current.Kind == TokenKind.KeywordSet)
        {
            Advance();
            Expect(TokenKind.KeywordNull);
            return ForeignKeyAction.SetNull;
        }

        if (IsIdentifier("no"))
        {
            Advance();
            ExpectIdentifier("action", "ON DELETE NO 后期望 ACTION");
            return ForeignKeyAction.NoAction;
        }

        throw Error("ON DELETE 后期望 CASCADE / SET NULL / NO ACTION。");
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

    private SqlStatement ParseInsert()
    {
        Expect(TokenKind.KeywordInsert);
        Expect(TokenKind.KeywordInto);
        if (IsGraphInsertStart())
            return ParseGraphInsert();
        var measurement = ExpectIdentifierName();

        if (Current.Kind == TokenKind.KeywordDefault)
        {
            Advance();
            Expect(TokenKind.KeywordValues);
            return ParseInsertReturning(new InsertStatement(
                measurement,
                Array.Empty<string>(),
                new[] { (IReadOnlyList<SqlExpression>)Array.Empty<SqlExpression>() })
            {
                IsDefaultValues = true,
            });
        }

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

        return ParseInsertReturning(new InsertStatement(measurement, columns, rows));
    }

    private InsertGraphStatement ParseGraphInsert()
        => ParseGraphValuesMutation(GraphValuesMutationMode.Insert, "INSERT INTO GRAPH");

    private InsertGraphStatement ParseGraphUpsert()
    {
        ExpectIdentifier("upsert", "期望 UPSERT");
        Expect(TokenKind.KeywordInto);
        if (!IsGraphElementMutationStart())
            throw Error("UPSERT INTO 仅支持 GRAPH graph_name VERTEX|EDGE");
        return ParseGraphValuesMutation(GraphValuesMutationMode.Upsert, "UPSERT INTO GRAPH");
    }

    private InsertGraphStatement ParseGraphValuesMutation(
        GraphValuesMutationMode mode,
        string operation)
    {
        Advance();
        string graphName = ExpectIdentifierName();
        GraphMutationKind kind;
        if (IsIdentifier("vertex"))
            kind = GraphMutationKind.Vertex;
        else if (IsIdentifier("edge"))
            kind = GraphMutationKind.Edge;
        else
            throw Error($"{operation} 后面期望 VERTEX 或 EDGE");
        Advance();

        Expect(TokenKind.LeftParen);
        var columns = new List<string> { ExpectColumnName() };
        while (Current.Kind == TokenKind.Comma)
        {
            Advance();
            columns.Add(ExpectColumnName());
        }
        Expect(TokenKind.RightParen);
        Expect(TokenKind.KeywordValues);

        var rows = new List<IReadOnlyList<SqlExpression>> { ParseValueRow(columns.Count) };
        while (Current.Kind == TokenKind.Comma)
        {
            Advance();
            rows.Add(ParseValueRow(columns.Count));
        }
        return new InsertGraphStatement(graphName, kind, columns, rows) { Mode = mode };
    }

    private InsertStatement ParseInsertReturning(InsertStatement statement)
    {
        if (!IsIdentifier("returning"))
            return statement;

        Advance();
        if (Current.Kind == TokenKind.Star)
        {
            Advance();
            return statement with { ReturningColumns = ["*"] };
        }

        var columns = new List<string> { ExpectColumnName() };
        while (Current.Kind == TokenKind.Comma)
        {
            Advance();
            columns.Add(ExpectColumnName());
        }

        return statement with { ReturningColumns = columns };
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
        values.Add(ParseDmlValue());
        while (Current.Kind == TokenKind.Comma)
        {
            Advance();
            values.Add(ParseDmlValue());
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
        var statement = ParseSelectCore();
        var unions = new List<SelectStatement>();
        while (Current.Kind == TokenKind.KeywordUnion)
        {
            Advance();
            unions.Add(ParseSelectCore());
        }

        var orderByItems = ParseOptionalOrderBy();
        var orderBy = orderByItems.Count > 0 ? orderByItems[0] : null;
        var pagination = ParseOptionalPagination();
        return statement with
        {
            Unions = unions,
            OrderBy = orderBy,
            OrderByItems = orderByItems,
            Pagination = pagination
        };
    }

    private SelectStatement ParseSelectCore()
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
        GraphTableSource? graphTable = null;
        SelectStatement? fromSubquery = null;
        if (Current.Kind == TokenKind.LeftParen && _index + 1 < _tokens.Count && _tokens[_index + 1].Kind == TokenKind.KeywordSelect)
        {
            Advance();
            fromSubquery = ParseSelect();
            Expect(TokenKind.RightParen);
            tableAlias = ParseRequiredTableAlias("FROM 子查询必须声明别名");
            measurement = tableAlias;
        }
        else if (IsIdentifier("graph_table")
            && _index + 1 < _tokens.Count
            && _tokens[_index + 1].Kind == TokenKind.LeftParen)
        {
            graphTable = ParseGraphTableSource();
            measurement = "__graph_table__";
            tableAlias = ParseOptionalTableAlias();
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
            else if (IsGraphTableValuedFunction(name))
            {
                measurement = "__graph__";
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

        return new SelectStatement(
            projections,
            measurement,
            where,
            groupBy,
            TableValuedFunction: tvf,
            TableAlias: tableAlias,
            Join: joins.Count == 0 ? null : joins[0],
            FromSubquery: fromSubquery,
            Joins: joins,
            Having: having,
            Distinct: distinct)
        {
            GraphTable = graphTable,
        };
    }

    private GraphTableSource ParseGraphTableSource()
    {
        Advance();
        Expect(TokenKind.LeftParen);
        string graphName = ExpectIdentifierName();
        ExpectIdentifier("match", "GRAPH_TABLE graph 名称后面期望 MATCH");
        ParsedGraphMatch match = ParseGraphMatch();
        ExpectIdentifier("columns", "GRAPH_TABLE MATCH 后面期望 COLUMNS");
        Expect(TokenKind.LeftParen);
        IReadOnlyList<SelectItem> columns = ParseSelectList();
        Expect(TokenKind.RightParen);
        Expect(TokenKind.RightParen);
        return match.ToSource(graphName, columns);
    }

    private ParsedGraphMatch ParseGraphMatch()
    {
        string? pathVariable = null;
        if (Current.Kind == TokenKind.IdentifierLiteral
            && _index + 1 < _tokens.Count
            && _tokens[_index + 1].Kind == TokenKind.Equal)
        {
            pathVariable = ExpectIdentifierName();
            Expect(TokenKind.Equal);
        }

        bool isAnyShortest = false;
        if (IsIdentifier("any"))
        {
            Advance();
            ExpectIdentifier("shortest", "GRAPH_TABLE MATCH ANY 后面期望 SHORTEST");
            isAnyShortest = true;
        }

        GraphPathUniqueness uniqueness = GraphPathUniqueness.Vertex;
        if (IsIdentifier("walk"))
        {
            Advance();
            uniqueness = GraphPathUniqueness.None;
        }
        else if (IsIdentifier("trail"))
        {
            Advance();
            uniqueness = GraphPathUniqueness.Edge;
        }
        else if (IsIdentifier("simple") || IsIdentifier("acyclic"))
        {
            Advance();
            uniqueness = GraphPathUniqueness.Vertex;
        }

        GraphPatternVertex left = ParseGraphPatternVertex();

        GraphDirection direction;
        GraphPatternEdge edge;
        if (Current.Kind == TokenKind.Minus)
        {
            Advance();
            edge = ParseGraphPatternEdge();
            Expect(TokenKind.Minus);
            if (Current.Kind == TokenKind.GreaterThan)
            {
                Advance();
                direction = GraphDirection.Outgoing;
            }
            else
            {
                direction = GraphDirection.Both;
            }
        }
        else if (Current.Kind == TokenKind.LessThan)
        {
            Advance();
            Expect(TokenKind.Minus);
            edge = ParseGraphPatternEdge();
            Expect(TokenKind.Minus);
            direction = GraphDirection.Incoming;
        }
        else
        {
            throw Error("GRAPH_TABLE MATCH 顶点后面期望 -[edge]->、<-[edge]- 或 -[edge]-");
        }

        GraphPathPattern? path = ParseOptionalGraphPathPattern(
            pathVariable,
            uniqueness,
            isAnyShortest);
        GraphPatternVertex right = ParseGraphPatternVertex();
        if (string.Equals(left.Variable, edge.Variable, StringComparison.OrdinalIgnoreCase)
            || string.Equals(left.Variable, right.Variable, StringComparison.OrdinalIgnoreCase)
            || string.Equals(edge.Variable, right.Variable, StringComparison.OrdinalIgnoreCase))
        {
            throw Error("GRAPH_TABLE MATCH 的 vertex/edge 变量名必须互不相同");
        }
        if (pathVariable is not null
            && (string.Equals(pathVariable, left.Variable, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pathVariable, edge.Variable, StringComparison.OrdinalIgnoreCase)
                || string.Equals(pathVariable, right.Variable, StringComparison.OrdinalIgnoreCase)))
        {
            throw Error("GRAPH_TABLE MATCH 的 path/vertex/edge 变量名必须互不相同");
        }

        SqlExpression? predicate = null;
        if (Current.Kind == TokenKind.KeywordWhere)
        {
            Advance();
            predicate = ParseExpression();
        }
        return new ParsedGraphMatch(left, edge, right, direction, predicate, path);
    }

    private GraphPathPattern? ParseOptionalGraphPathPattern(
        string? pathVariable,
        GraphPathUniqueness uniqueness,
        bool isAnyShortest)
    {
        if (Current.Kind != TokenKind.LeftBrace)
        {
            return pathVariable is null && !isAnyShortest && uniqueness == GraphPathUniqueness.Vertex
                ? null
                : new GraphPathPattern(pathVariable, 1, 1, uniqueness, isAnyShortest);
        }

        Advance();
        int minDepth = ExpectNonNegativeInt("GRAPH_TABLE path 最小深度必须是非负整数");
        Expect(TokenKind.Comma);
        int maxDepth = ExpectNonNegativeInt("GRAPH_TABLE path 最大深度必须是非负整数");
        Expect(TokenKind.RightBrace);
        if (minDepth < 1)
            throw Error("GRAPH_TABLE path 第一版要求最小深度至少为 1");
        if (maxDepth < minDepth)
            throw Error("GRAPH_TABLE path 最大深度不能小于最小深度");
        if (maxDepth > 64)
            throw Error("GRAPH_TABLE path 最大深度不能超过 64");
        return new GraphPathPattern(pathVariable, minDepth, maxDepth, uniqueness, isAnyShortest);
    }

    private GraphPatternVertex ParseGraphPatternVertex()
    {
        Expect(TokenKind.LeftParen);
        string variable = ExpectIdentifierName();
        Expect(TokenKind.KeywordIs);
        string label = ExpectGraphPatternLabel();
        Expect(TokenKind.RightParen);
        return new GraphPatternVertex(variable, label);
    }

    private GraphPatternEdge ParseGraphPatternEdge()
    {
        Expect(TokenKind.LeftBracket);
        string variable = ExpectIdentifierName();
        Expect(TokenKind.KeywordIs);
        string label = ExpectGraphPatternLabel();
        Expect(TokenKind.RightBracket);
        return new GraphPatternEdge(variable, label);
    }

    private string ExpectGraphPatternLabel()
    {
        if (Current.Kind == TokenKind.IdentifierLiteral)
            return ExpectIdentifierName();
        if (Current.Kind == TokenKind.IntegerLiteral && Current.IntegerValue > 0)
        {
            string label = Current.IntegerValue.ToString(CultureInfo.InvariantCulture);
            Advance();
            return label;
        }
        throw Error("GRAPH_TABLE pattern label 必须是标识符或正整数 label ID");
    }

    private sealed record ParsedGraphMatch(
        GraphPatternVertex Left,
        GraphPatternEdge Edge,
        GraphPatternVertex Right,
        GraphDirection Direction,
        SqlExpression? Predicate,
        GraphPathPattern? Path)
    {
        public GraphTableSource ToSource(string graphName, IReadOnlyList<SelectItem> columns)
            => new(graphName, Left, Edge, Right, Direction, Predicate, columns)
            {
                Path = Path,
            };
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

    private static bool IsGraphTableValuedFunction(string name)
        => string.Equals(name, "graph_nodes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "graph_edges", StringComparison.OrdinalIgnoreCase);

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
            var fetch = ExpectPaginationValue("LIMIT 后面期望非负整数或参数占位符");
            SqlExpression offset = LiteralExpression.Integer(0);
            if (Current.Kind == TokenKind.KeywordOffset)
            {
                Advance();
                offset = ExpectPaginationValue("OFFSET 后面期望非负整数或参数占位符");
            }
            return new PaginationSpec(offset, fetch);
        }

        SqlExpression offsetValue = LiteralExpression.Integer(0);
        bool hasOffset = false;
        if (Current.Kind == TokenKind.KeywordOffset)
        {
            Advance();
            offsetValue = ExpectPaginationValue("OFFSET 后面期望非负整数或参数占位符");
            hasOffset = true;
            if (IsIdentifier("row") || IsIdentifier("rows"))
                Advance();
        }

        if (Current.Kind == TokenKind.KeywordFetch)
        {
            Advance();
            if (IsIdentifier("first") || IsIdentifier("next"))
                Advance();

            var fetch = ExpectPaginationValue("FETCH 后面期望非负整数或参数占位符");

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

    private SqlExpression ExpectPaginationValue(string errorMessage)
    {
        if (Current.Kind == TokenKind.IntegerLiteral)
            return LiteralExpression.Integer(ExpectNonNegativeInt(errorMessage));

        if (Current.Kind == TokenKind.Parameter)
            return ParsePrimary();

        throw Error(errorMessage);
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

    private SqlStatement ParseDelete()
    {
        Expect(TokenKind.KeywordDelete);
        Expect(TokenKind.KeywordFrom);
        if (IsGraphElementMutationStart())
            return ParseGraphDelete();
        var measurement = ExpectIdentifierName();
        Expect(TokenKind.KeywordWhere);
        var where = ParseExpression();
        return new DeleteStatement(measurement, where);
    }

    private DeleteGraphStatement ParseGraphDelete()
    {
        Advance();
        string graphName = ExpectIdentifierName();
        GraphMutationKind kind = ParseGraphMutationKind("DELETE FROM GRAPH");
        Expect(TokenKind.KeywordWhere);
        return new DeleteGraphStatement(graphName, kind, ParseExpression());
    }

    private TruncateTableStatement ParseTruncate()
    {
        Expect(TokenKind.KeywordTruncate);
        Expect(TokenKind.KeywordTable);
        return new TruncateTableStatement(ExpectIdentifierName());
    }

    // ── UPDATE ─────────────────────────────────────────────────────────────

    private SqlStatement ParseUpdate()
    {
        Expect(TokenKind.KeywordUpdate);
        if (IsGraphElementMutationStart())
            return ParseGraphUpdate();
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

    private UpdateGraphStatement ParseGraphUpdate()
    {
        Advance();
        string graphName = ExpectIdentifierName();
        GraphMutationKind kind = ParseGraphMutationKind("UPDATE GRAPH");
        Expect(TokenKind.KeywordSet);
        var assignments = new List<UpdateAssignment> { ParseUpdateAssignment() };
        while (Current.Kind == TokenKind.Comma)
        {
            Advance();
            assignments.Add(ParseUpdateAssignment());
        }
        Expect(TokenKind.KeywordWhere);
        return new UpdateGraphStatement(graphName, kind, assignments, ParseExpression());
    }

    private UpdateAssignment ParseUpdateAssignment()
    {
        var column = ExpectColumnName();
        Expect(TokenKind.Equal);
        var value = ParseDmlValue();
        return new UpdateAssignment(column, value);
    }

    private SqlExpression ParseDmlValue()
    {
        if (Current.Kind != TokenKind.KeywordDefault)
            return ParseExpression();

        Advance();
        return DefaultValueExpression.Instance;
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

    /// <summary>
    /// 读取 WITH 列表中的唯一 Modbus 选项名，并拒绝同名选项重复出现。
    /// </summary>
    private string ExpectUniqueModbusOption(HashSet<string> seen)
    {
        if (Current.Kind != TokenKind.IdentifierLiteral)
            throw Error("期望 Modbus 选项名");

        string option = Current.Text;
        Advance();
        if (!seen.Add(option))
            throw Error($"Modbus 选项 {option} 重复声明");
        return option;
    }

    /// <summary>
    /// 消费 Modbus WITH 选项间的逗号，并拒绝缺少分隔符或尾随逗号。
    /// </summary>
    private void ConsumeModbusOptionSeparator()
    {
        if (Current.Kind == TokenKind.RightParen)
            return;
        if (Current.Kind != TokenKind.Comma)
            throw Error("Modbus WITH 选项之间必须使用逗号分隔");

        Advance();
        if (Current.Kind == TokenKind.RightParen)
            throw Error("Modbus WITH 选项列表不允许尾随逗号");
    }

    /// <summary>
    /// 解析固定为 TCP 的 TRANSPORT 选项。
    /// </summary>
    private void ParseModbusTcpTransport()
    {
        if (!IsIdentifier("tcp"))
            throw Error("Modbus TRANSPORT 第一版仅支持 TCP");
        Advance();
    }

    /// <summary>
    /// 将 <c>host:port</c> 或 <c>[IPv6]:port</c> 字符串拆分为主机与端口。
    /// </summary>
    private (string Host, int Port) ParseModbusHostAndPort(string value, string optionName)
    {
        string host;
        string portText;
        if (value.StartsWith("[", StringComparison.Ordinal))
        {
            int closeBracket = value.IndexOf(']');
            if (closeBracket <= 1 || closeBracket + 1 >= value.Length || value[closeBracket + 1] != ':')
                throw Error($"{optionName} 必须使用 [IPv6]:port 格式");
            host = value[1..closeBracket];
            portText = value[(closeBracket + 2)..];
        }
        else
        {
            int separator = value.LastIndexOf(':');
            if (separator <= 0 || separator == value.Length - 1)
                throw Error($"{optionName} 必须使用 host:port 格式");
            host = value[..separator];
            portText = value[(separator + 1)..];
        }

        if (string.IsNullOrWhiteSpace(host)
            || host.Any(char.IsWhiteSpace)
            || !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            || port is < 1 or > 65_535)
        {
            throw Error($"{optionName} 的主机或端口无效");
        }

        return (host, port);
    }

    /// <summary>
    /// 解析 0..255 范围内的 Modbus Unit ID。
    /// </summary>
    private byte ParseModbusUnitId()
    {
        int unitId = ExpectNonNegativeInt("UNIT_ID 必须位于 0..255");
        if (unitId > byte.MaxValue)
            throw Error("UNIT_ID 必须位于 0..255");
        return (byte)unitId;
    }

    /// <summary>
    /// 解析 duration token 或字符串形式的正毫秒时长。
    /// </summary>
    private int ParseModbusDurationMilliseconds(string optionName)
    {
        long milliseconds;
        if (Current.Kind == TokenKind.DurationLiteral)
        {
            milliseconds = Current.IntegerValue;
            Advance();
        }
        else if (Current.Kind == TokenKind.StringLiteral)
        {
            string raw = ExpectStringLiteral();
            milliseconds = ParseModbusDurationString(raw, optionName);
        }
        else
        {
            throw Error($"{optionName} 必须是 duration 或字符串");
        }

        if (milliseconds is <= 0 or > int.MaxValue)
            throw Error($"{optionName} 必须位于 1..Int32.MaxValue 毫秒");
        return (int)milliseconds;
    }

    /// <summary>
    /// 把字符串中的 duration 后缀或标准 TimeSpan 转换为毫秒。
    /// </summary>
    private long ParseModbusDurationString(string raw, string optionName)
    {
        try
        {
            IReadOnlyList<Token> tokens = SqlLexer.Tokenize(raw);
            if (tokens.Count == 2
                && tokens[0].Kind == TokenKind.DurationLiteral
                && tokens[1].Kind == TokenKind.EndOfFile)
            {
                return tokens[0].IntegerValue;
            }
        }
        catch (SqlParseException)
        {
            // 继续尝试标准 TimeSpan 文本，以便兼容 00:00:01 形式。
        }

        if (!TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out TimeSpan duration)
            || duration <= TimeSpan.Zero
            || duration.Ticks % TimeSpan.TicksPerMillisecond != 0)
        {
            throw Error($"{optionName} 字符串不是有效的毫秒时长");
        }
        return checked((long)duration.TotalMilliseconds);
    }

    /// <summary>
    /// 解析 TRUE/FALSE 布尔选项。
    /// </summary>
    private bool ParseModbusBoolean(string optionName)
    {
        if (Current.Kind == TokenKind.KeywordTrue)
        {
            Advance();
            return true;
        }
        if (Current.Kind == TokenKind.KeywordFalse)
        {
            Advance();
            return false;
        }
        throw Error($"{optionName} 后面期望 TRUE / FALSE");
    }

    /// <summary>
    /// 兼容读取 AUDIT TRUE，同时保持审计不可关闭的不变量。
    /// </summary>
    private void ParseMandatoryModbusAudit()
    {
        if (!ParseModbusBoolean("AUDIT"))
            throw Error("Modbus 审计不可关闭，AUDIT FALSE 无效");
    }

    /// <summary>
    /// 解析单个 CSV 字符串或括号字符串列表形式的 endpoint allowlist。
    /// </summary>
    private IReadOnlyList<string> ParseModbusAllowlist()
    {
        var entries = new List<string>();
        if (Current.Kind == TokenKind.LeftParen)
        {
            Advance();
            if (Current.Kind == TokenKind.RightParen)
                throw Error("ALLOWLIST 至少需要一个 IP 或 CIDR");
            while (true)
            {
                AddModbusAllowlistEntries(entries, ExpectStringLiteral());
                if (Current.Kind != TokenKind.Comma)
                    break;
                Advance();
            }
            Expect(TokenKind.RightParen);
        }
        else
        {
            AddModbusAllowlistEntries(entries, ExpectStringLiteral());
        }

        if (entries.Count == 0)
            throw Error("ALLOWLIST 至少需要一个 IP 或 CIDR");
        return entries.AsReadOnly();
    }

    /// <summary>
    /// 拆分并规范化 allowlist CSV 片段，拒绝空白成员和重复成员。
    /// </summary>
    private void AddModbusAllowlistEntries(List<string> entries, string csv)
    {
        string[] values = csv.Split(',', StringSplitOptions.None);
        foreach (string value in values)
        {
            string entry = value.Trim();
            if (entry.Length == 0)
                throw Error("ALLOWLIST 不允许空成员");
            if (entries.Contains(entry, StringComparer.OrdinalIgnoreCase))
                throw Error($"ALLOWLIST 成员 {entry} 重复声明");
            entries.Add(entry);
        }
    }

    /// <summary>
    /// 解析 ZERO_BASED、ONE_BASED 或 MODICON 寻址模式。
    /// </summary>
    private ModbusAddressingMode ParseModbusAddressingMode()
    {
        string value = ExpectModbusIdentifierValue("ADDRESSING");
        return value.ToUpperInvariant() switch
        {
            "ZERO_BASED" => ModbusAddressingMode.ZeroBased,
            "ONE_BASED" => ModbusAddressingMode.OneBased,
            "MODICON" => ModbusAddressingMode.Modicon,
            _ => throw Error("ADDRESSING 仅支持 ZERO_BASED / ONE_BASED / MODICON"),
        };
    }

    /// <summary>
    /// 解析 BIG_ENDIAN 或 LITTLE_ENDIAN 寄存器内字节序。
    /// </summary>
    private ModbusByteOrder ParseModbusByteOrder()
    {
        string value = ExpectModbusIdentifierValue("BYTE_ORDER");
        return value.ToUpperInvariant() switch
        {
            "BIG_ENDIAN" => ModbusByteOrder.BigEndian,
            "LITTLE_ENDIAN" => ModbusByteOrder.LittleEndian,
            _ => throw Error("BYTE_ORDER 仅支持 BIG_ENDIAN / LITTLE_ENDIAN"),
        };
    }

    /// <summary>
    /// 解析 BIG_ENDIAN 或 LITTLE_ENDIAN 多寄存器字序。
    /// </summary>
    private ModbusWordOrder ParseModbusWordOrder()
    {
        string value = ExpectModbusIdentifierValue("WORD_ORDER");
        return value.ToUpperInvariant() switch
        {
            "BIG_ENDIAN" => ModbusWordOrder.BigEndian,
            "LITTLE_ENDIAN" => ModbusWordOrder.LittleEndian,
            _ => throw Error("WORD_ORDER 仅支持 BIG_ENDIAN / LITTLE_ENDIAN"),
        };
    }

    /// <summary>
    /// 解析 endpoint 的 REJECT 或 STAGED 外部写入口策略。
    /// </summary>
    private ModbusEndpointWritePolicy ParseModbusEndpointWritePolicy()
    {
        string value = ExpectModbusIdentifierValue("WRITE_POLICY");
        return value.ToUpperInvariant() switch
        {
            "REJECT" => ModbusEndpointWritePolicy.Reject,
            "STAGED" => ModbusEndpointWritePolicy.Staged,
            _ => throw Error("WRITE_POLICY 仅支持 REJECT / STAGED"),
        };
    }

    /// <summary>
    /// 解析四个相互独立的 Modbus 地址空间名称。
    /// </summary>
    private ModbusRegisterArea ParseModbusRegisterArea()
    {
        string value = ExpectModbusIdentifierValue("Modbus area");
        return value.ToUpperInvariant() switch
        {
            "COIL" => ModbusRegisterArea.Coil,
            "DISCRETE_INPUT" => ModbusRegisterArea.DiscreteInput,
            "HOLDING_REGISTER" => ModbusRegisterArea.HoldingRegister,
            "INPUT_REGISTER" => ModbusRegisterArea.InputRegister,
            _ => throw Error("Modbus area 仅支持 COIL / DISCRETE_INPUT / HOLDING_REGISTER / INPUT_REGISTER"),
        };
    }

    /// <summary>
    /// 解析 wire type，并返回 STRING 长度和推导出的地址数量。
    /// </summary>
    private (ModbusValueType ValueType, int StringLength, int RegisterCount) ParseModbusValueType()
    {
        if (Current.Kind == TokenKind.KeywordString)
        {
            Advance();
            Expect(TokenKind.LeftParen);
            int stringLength = ExpectPositiveInt("STRING(n) 的 n 必须是正整数");
            Expect(TokenKind.RightParen);
            int registerCount = ModbusValueCodec.GetRegisterCount(ModbusValueType.String, stringLength);
            return (ModbusValueType.String, stringLength, registerCount);
        }

        string value = ExpectModbusIdentifierValue("Modbus wire type");
        ModbusValueType valueType = value.ToUpperInvariant() switch
        {
            "BIT" => ModbusValueType.Bit,
            "INT16" => ModbusValueType.Int16,
            "UINT16" => ModbusValueType.UInt16,
            "INT32" => ModbusValueType.Int32,
            "UINT32" => ModbusValueType.UInt32,
            "FLOAT32" => ModbusValueType.Float32,
            "FLOAT64" => ModbusValueType.Float64,
            "BCD16" => ModbusValueType.Bcd16,
            "BCD32" => ModbusValueType.Bcd32,
            _ => throw Error("不支持的 Modbus wire type"),
        };
        return (valueType, 0, ModbusValueCodec.GetRegisterCount(valueType));
    }

    /// <summary>
    /// 解析 READ、WRITE 或 READ_WRITE 列访问模式。
    /// </summary>
    private ModbusAccessMode ParseModbusAccessMode()
    {
        if (Current.Kind == TokenKind.KeywordRead)
        {
            Advance();
            return ModbusAccessMode.Read;
        }
        if (Current.Kind == TokenKind.KeywordWrite)
        {
            Advance();
            return ModbusAccessMode.Write;
        }
        if (IsIdentifier("read_write"))
        {
            Advance();
            return ModbusAccessMode.ReadWrite;
        }
        throw Error("ACCESS 仅支持 READ / WRITE / READ_WRITE");
    }

    /// <summary>
    /// 解析 LATEST 或 HISTORY source 表模式。
    /// </summary>
    private ModbusTableMode ParseModbusTableMode()
    {
        string value = ExpectModbusIdentifierValue("TABLE_MODE");
        return value.ToUpperInvariant() switch
        {
            "LATEST" => ModbusTableMode.Latest,
            "HISTORY" => ModbusTableMode.History,
            _ => throw Error("TABLE_MODE 仅支持 LATEST / HISTORY"),
        };
    }

    /// <summary>
    /// 解析 KEEP_LAST、NULL、SKIP 或 MARK_BAD 采集错误策略。
    /// </summary>
    private ModbusErrorPolicy ParseModbusErrorPolicy()
    {
        if (Current.Kind == TokenKind.KeywordNull)
        {
            Advance();
            return ModbusErrorPolicy.Null;
        }

        string value = ExpectModbusIdentifierValue("ON_ERROR");
        return value.ToUpperInvariant() switch
        {
            "KEEP_LAST" => ModbusErrorPolicy.KeepLast,
            "SKIP" => ModbusErrorPolicy.Skip,
            "MARK_BAD" => ModbusErrorPolicy.MarkBad,
            _ => throw Error("ON_ERROR 仅支持 KEEP_LAST / NULL / SKIP / MARK_BAD"),
        };
    }

    /// <summary>
    /// 解析 endpoint 请求获批后的 STAGE_ONLY 或 UPDATE_TABLE 动作。
    /// </summary>
    private ModbusApprovedWriteAction ParseModbusApprovedWriteAction()
    {
        string value = ExpectModbusIdentifierValue("ON_EXTERNAL_WRITE");
        return value.ToUpperInvariant() switch
        {
            "STAGE_ONLY" => ModbusApprovedWriteAction.StageOnly,
            "UPDATE_TABLE" => ModbusApprovedWriteAction.UpdateTable,
            _ => throw Error("ON_EXTERNAL_WRITE 仅支持 STAGE_ONLY / UPDATE_TABLE"),
        };
    }

    /// <summary>
    /// 解析 SCALE/OFFSET 使用的有符号十进制字面量，避免先经 double 丢失精度。
    /// </summary>
    private decimal ParseModbusDecimal(string optionName)
    {
        var negative = false;
        if (Current.Kind == TokenKind.Minus)
        {
            negative = true;
            Advance();
        }
        else if (Current.Kind == TokenKind.Plus)
        {
            Advance();
        }

        decimal value;
        if (Current.Kind == TokenKind.IntegerLiteral)
        {
            value = Current.IntegerValue;
            Advance();
        }
        else if (Current.Kind == TokenKind.FloatLiteral
                 && decimal.TryParse(Current.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            Advance();
        }
        else
        {
            throw Error($"{optionName} 必须是有限十进制数");
        }

        return negative ? -value : value;
    }

    /// <summary>
    /// 解析 ROW KEY 使用的有符号 Int64 字面量。
    /// </summary>
    private long ParseModbusSignedInteger(string optionName)
    {
        var negative = false;
        if (Current.Kind == TokenKind.Minus)
        {
            negative = true;
            Advance();
        }
        else if (Current.Kind == TokenKind.Plus)
        {
            Advance();
        }

        if (negative && Current.Kind == TokenKind.Int64MinMagnitudeLiteral)
        {
            Advance();
            return long.MinValue;
        }

        if (Current.Kind != TokenKind.IntegerLiteral)
            throw Error($"{optionName} 必须是整数");
        long value = Current.IntegerValue;
        Advance();
        return negative ? -value : value;
    }

    /// <summary>
    /// 读取 Modbus 产生式中的非保留标识符值。
    /// </summary>
    private string ExpectModbusIdentifierValue(string optionName)
    {
        if (Current.Kind != TokenKind.IdentifierLiteral)
            throw Error($"{optionName} 后面期望标识符");
        string value = Current.Text;
        Advance();
        return value;
    }

    /// <summary>
    /// 判断 DESCRIBE 后的 MODBUS 是否确实引出 SOURCE、ENDPOINT 或 TABLE 元数据对象。
    /// </summary>
    private bool IsModbusDescribePrefix()
    {
        if (!IsIdentifier("modbus"))
            return false;

        Token next = _tokens[_index + 1];
        return next.Kind == TokenKind.KeywordTable
            || next.Kind == TokenKind.IdentifierLiteral
            && (string.Equals(next.Text, "source", StringComparison.OrdinalIgnoreCase)
                || string.Equals(next.Text, "endpoint", StringComparison.OrdinalIgnoreCase));
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

    private bool IsGraphInsertStart()
        => IsGraphElementMutationStart();

    private bool IsGraphElementMutationStart()
        => IsIdentifier("graph")
           && _index + 2 < _tokens.Count
           && _tokens[_index + 1].Kind == TokenKind.IdentifierLiteral
           && _tokens[_index + 2].Kind == TokenKind.IdentifierLiteral
           && (string.Equals(_tokens[_index + 2].Text, "vertex", StringComparison.OrdinalIgnoreCase)
               || string.Equals(_tokens[_index + 2].Text, "edge", StringComparison.OrdinalIgnoreCase));

    private GraphMutationKind ParseGraphMutationKind(string operation)
    {
        if (IsIdentifier("vertex"))
        {
            Advance();
            return GraphMutationKind.Vertex;
        }
        if (IsIdentifier("edge"))
        {
            Advance();
            return GraphMutationKind.Edge;
        }
        throw Error($"{operation} 后面期望 VERTEX 或 EDGE");
    }

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
            case TokenKind.KeywordVector:
                Advance();
                ExpectIndexKeyword("DROP VECTOR 后面期望 INDEX");
                var vectorIndexName = ExpectIdentifierName();
                Expect(TokenKind.KeywordOn);
                return new DropDocumentVectorIndexStatement(vectorIndexName, ExpectIdentifierName());
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
                if (IsIdentifier("property"))
                {
                    Advance();
                    ExpectIdentifier("graph", "DROP PROPERTY 后面期望 GRAPH");
                    bool ifExists = ParseOptionalIfExists();
                    return new DropPropertyGraphStatement(ExpectIdentifierName(), ifExists);
                }
                if (IsIdentifier("graph"))
                {
                    Advance();
                    bool ifExists = ParseOptionalIfExists();
                    return new DropGraphStatement(ExpectIdentifierName(), ifExists);
                }
                if (IsIdentifier("materialized"))
                {
                    Advance();
                    if (!IsIdentifier("view"))
                        throw Error("DROP MATERIALIZED 后面期望 VIEW");
                    Advance();
                    bool dropMaterializedViewIfExists = ParseOptionalIfExists();
                    return new DropMaterializedViewStatement(
                        ExpectIdentifierName(),
                        dropMaterializedViewIfExists);
                }

                if (IsIdentifier("view"))
                {
                    Advance();
                    bool dropViewIfExists = ParseOptionalIfExists();
                    return new DropViewStatement(ExpectIdentifierName(), dropViewIfExists);
                }

                if (IsIdentifier("procedure"))
                {
                    Advance();
                    bool ifExists = ParseOptionalIfExists();
                    return new DropProcedureStatement(ExpectIdentifierName(), ifExists);
                }

                if (IsIdentifier("trigger"))
                {
                    Advance();
                    bool ifExists = ParseOptionalIfExists();
                    return new DropTriggerStatement(ExpectIdentifierName(), ifExists);
                }

                if (IsIdentifier("index"))
                {
                    Advance();
                    var fallbackIndexName = ExpectIdentifierName();
                    Expect(TokenKind.KeywordOn);
                    return new DropTableIndexStatement(fallbackIndexName, ExpectIdentifierName());
                }

                throw Error("DROP 后面期望 MEASUREMENT / TABLE / VIEW / PROCEDURE / TRIGGER / INDEX / JSON INDEX / FULLTEXT INDEX / USER 或 DATABASE");
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
        if (Current.Kind == TokenKind.KeywordAlter)
        {
            Advance();
            return ParseAlterTableAlterColumn(tableName);
        }

        if (IsIdentifier("add"))
        {
            Advance();
            if (Current.Kind == TokenKind.KeywordForeign)
                return ParseAlterTableAddForeignKey(tableName, constraintName: null);

            if (Current.Kind == TokenKind.KeywordCheck)
                return ParseAlterTableAddCheckConstraint(tableName, constraintName: null);

            if (IsIdentifier("constraint"))
            {
                Advance();
                var constraintName = ExpectIdentifierName();
                if (Current.Kind == TokenKind.KeywordForeign)
                    return ParseAlterTableAddForeignKey(tableName, constraintName);
                if (Current.Kind == TokenKind.KeywordCheck)
                    return ParseAlterTableAddCheckConstraint(tableName, constraintName);
                throw Error("ALTER TABLE ADD CONSTRAINT 后面期望 FOREIGN KEY 或 CHECK");
            }

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

        throw Error("ALTER TABLE 后面期望 ADD COLUMN / ADD FOREIGN KEY / ADD CHECK / ALTER COLUMN / DROP COLUMN / DROP CONSTRAINT / RENAME COLUMN / RENAME TO");
    }

    private AlterTableAlterColumnStatement ParseAlterTableAlterColumn(string tableName)
    {
        if (Current.Kind == TokenKind.KeywordColumn)
            Advance();

        var columnName = ExpectColumnName();
        SqlDataType? dataType = null;
        ColumnNullability nullability = ColumnNullability.Unspecified;
        var defaultAction = ColumnDefaultAction.Unchanged;
        SqlExpression? defaultExpression = null;
        var changed = false;

        while (true)
        {
            if (Current.Kind is TokenKind.KeywordInt or TokenKind.KeywordFloat
                or TokenKind.KeywordBool or TokenKind.KeywordString
                or TokenKind.KeywordDateTime or TokenKind.KeywordBlob or TokenKind.KeywordJson
                or TokenKind.KeywordVector)
            {
                if (dataType is not null)
                    throw Error("ALTER COLUMN 数据类型重复声明");
                dataType = ParseTableDataType();
                changed = true;
                continue;
            }

            if (IsIdentifier("type"))
            {
                Advance();
                if (dataType is not null)
                    throw Error("ALTER COLUMN 数据类型重复声明");
                dataType = ParseTableDataType();
                changed = true;
                continue;
            }

            if (Current.Kind == TokenKind.KeywordNull)
            {
                SetNullability(ref nullability, ColumnNullability.Nullable);
                Advance();
                changed = true;
                continue;
            }

            if (Current.Kind == TokenKind.KeywordNot)
            {
                Advance();
                Expect(TokenKind.KeywordNull);
                SetNullability(ref nullability, ColumnNullability.NotNull);
                changed = true;
                continue;
            }

            if (Current.Kind == TokenKind.KeywordSet)
            {
                Advance();
                if (IsIdentifier("data"))
                {
                    Advance();
                    ExpectIdentifier("type", "ALTER COLUMN SET DATA 后面期望 TYPE");
                    if (dataType is not null)
                        throw Error("ALTER COLUMN 数据类型重复声明");
                    dataType = ParseTableDataType();
                    changed = true;
                    continue;
                }

                if (Current.Kind == TokenKind.KeywordNot)
                {
                    Advance();
                    Expect(TokenKind.KeywordNull);
                    SetNullability(ref nullability, ColumnNullability.NotNull);
                    changed = true;
                    continue;
                }

                if (Current.Kind == TokenKind.KeywordDefault)
                {
                    Advance();
                    if (defaultAction != ColumnDefaultAction.Unchanged)
                        throw Error("ALTER COLUMN DEFAULT 变更重复声明");
                    defaultExpression = ParseExpression();
                    defaultAction = ColumnDefaultAction.Set;
                    changed = true;
                    continue;
                }

                throw Error("ALTER COLUMN SET 后面期望 DATA TYPE / NOT NULL / DEFAULT");
            }

            if (Current.Kind == TokenKind.KeywordDrop)
            {
                Advance();
                if (Current.Kind == TokenKind.KeywordNot)
                {
                    Advance();
                    Expect(TokenKind.KeywordNull);
                    SetNullability(ref nullability, ColumnNullability.Nullable);
                    changed = true;
                    continue;
                }

                if (Current.Kind == TokenKind.KeywordDefault)
                {
                    Advance();
                    if (defaultAction != ColumnDefaultAction.Unchanged)
                        throw Error("ALTER COLUMN DEFAULT 变更重复声明");
                    defaultAction = ColumnDefaultAction.Drop;
                    changed = true;
                    continue;
                }

                throw Error("ALTER COLUMN DROP 后面期望 NOT NULL / DEFAULT");
            }

            break;
        }

        if (!changed)
            throw Error("ALTER TABLE ALTER COLUMN 至少需要指定一种变更");

        return new AlterTableAlterColumnStatement(
            tableName,
            columnName,
            dataType,
            nullability,
            defaultAction,
            defaultExpression);
    }

    private AlterTableAddForeignKeyStatement ParseAlterTableAddForeignKey(string tableName, string? constraintName)
    {
        var clause = ParseForeignKeyClause();
        return new AlterTableAddForeignKeyStatement(
            tableName,
            constraintName,
            clause.Columns,
            clause.PrincipalTable,
            clause.PrincipalColumns,
            clause.OnDelete);
    }

    private AlterTableAddCheckConstraintStatement ParseAlterTableAddCheckConstraint(
        string tableName,
        string? constraintName)
    {
        var clause = ParseCheckConstraintClause(constraintName);
        return new AlterTableAddCheckConstraintStatement(
            tableName,
            constraintName,
            clause.ExpressionSql,
            clause.Expression);
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
                if (IsIdentifier("property"))
                {
                    Advance();
                    ExpectIdentifier("graphs", "SHOW PROPERTY 后面期望 GRAPHS");
                    return new ShowPropertyGraphsStatement();
                }
                if (IsIdentifier("graphs"))
                {
                    Advance();
                    return new ShowGraphsStatement();
                }
                if (IsIdentifier("modbus"))
                {
                    Advance();
                    if (IsIdentifier("sources"))
                    {
                        Advance();
                        return new ShowModbusSourcesStatement();
                    }
                    if (IsIdentifier("endpoints"))
                    {
                        Advance();
                        return new ShowModbusEndpointsStatement();
                    }
                    if (Current.Kind == TokenKind.KeywordWrite)
                    {
                        Advance();
                        ExpectIdentifier("audit", "SHOW MODBUS WRITE 后面期望 AUDIT");
                        return new ShowModbusWriteAuditStatement();
                    }
                    throw Error("SHOW MODBUS 后面期望 SOURCES / ENDPOINTS / WRITE AUDIT");
                }

                if (IsIdentifier("materialized"))
                {
                    Advance();
                    if (!IsIdentifier("views"))
                        throw Error("SHOW MATERIALIZED 后面期望 VIEWS");
                    Advance();
                    return new ShowMaterializedViewsStatement();
                }

                if (IsIdentifier("views"))
                {
                    Advance();
                    return new ShowViewsStatement();
                }

                if (IsIdentifier("procedures"))
                {
                    Advance();
                    return new ShowProceduresStatement();
                }

                if (IsIdentifier("triggers"))
                {
                    Advance();
                    string? tableName = null;
                    if (Current.Kind == TokenKind.KeywordOn)
                    {
                        Advance();
                        tableName = ExpectIdentifierName();
                    }
                    return new ShowTriggersStatement(tableName);
                }

                if (IsIdentifier("indexes"))
                {
                    Advance();
                    Expect(TokenKind.KeywordOn);
                    return new ShowTableIndexesStatement(ExpectIdentifierName());
                }

                throw Error("SHOW 后面期望 MODBUS / USERS / GRANTS / DATABASES / TOKENS / MEASUREMENTS / TABLES / VIEWS / PROCEDURES / TRIGGERS / INDEXES");
        }
    }

    /// <summary>
    /// <c>EXPLAIN SELECT ...</c> / <c>EXPLAIN SHOW MEASUREMENTS</c> / <c>EXPLAIN DESCRIBE ...</c>。
    /// 当前仅接受只读语句，避免把写操作伪装成解释计划。
    /// </summary>
    private ExplainStatement ParseExplain()
    {
        Expect(TokenKind.KeywordExplain);
        bool analyze = false;
        if (IsIdentifier("analyze"))
        {
            Advance();
            analyze = true;
        }

        SqlStatement statement = Current.Kind switch
        {
            TokenKind.KeywordSelect => ParseSelect(),
            TokenKind.KeywordShow => ParseShow(),
            TokenKind.KeywordDescribe => ParseDescribe(),
            TokenKind.KeywordDesc => ParseDescribe(),
            _ => throw Error("EXPLAIN 后面期望 SELECT / SHOW / DESCRIBE 只读语句"),
        };

        if (statement is not SelectStatement
            and not ShowMeasurementsStatement
            and not ShowTablesStatement
            and not ShowViewsStatement
            and not ShowMaterializedViewsStatement
            and not ShowTableIndexesStatement
            and not ShowDocumentCollectionsStatement
            and not ShowDocumentIndexesStatement
            and not ShowFullTextIndexesStatement
            and not ShowGraphsStatement
            and not ShowPropertyGraphsStatement
            and not ShowModbusSourcesStatement
            and not ShowModbusEndpointsStatement
            and not DescribeMeasurementStatement
            and not DescribeTableStatement
            and not DescribeViewStatement
            and not DescribeMaterializedViewStatement
            and not DescribeDocumentCollectionStatement
            and not DescribeGraphStatement
            and not DescribePropertyGraphStatement
            and not DescribeModbusSourceStatement
            and not DescribeModbusEndpointStatement
            and not DescribeModbusTableStatement)
        {
            throw Error("EXPLAIN 仅支持 SELECT 及受支持的 SHOW / DESCRIBE 只读语句");
        }

        return new ExplainStatement(statement) { Analyze = analyze };
    }

    /// <summary>解析 <c>ANALYZE [TABLE] name</c>。</summary>
    private SqlStatement ParseAnalyzeTable()
    {
        ExpectIdentifier("analyze", "ANALYZE 后面期望 TABLE 或表名");
        if (IsIdentifier("graph")
            && _index + 1 < _tokens.Count
            && _tokens[_index + 1].Kind == TokenKind.IdentifierLiteral)
        {
            Advance();
            return new AnalyzeGraphStatement(ExpectIdentifierName());
        }
        if (Current.Kind == TokenKind.KeywordTable || IsIdentifier("table"))
            Advance();
        return new AnalyzeTableStatement(ExpectIdentifierName());
    }

    /// <summary>
    /// <c>DESCRIBE [MEASUREMENT] &lt;name&gt;</c> / <c>DESC [MEASUREMENT] &lt;name&gt;</c>。
    /// </summary>
    private SqlStatement ParseDescribe()
    {
        // 当前 token 是 DESCRIBE 或 DESC
        Advance();
        if (IsModbusDescribePrefix())
        {
            Advance();
            if (IsIdentifier("source"))
            {
                Advance();
                return new DescribeModbusSourceStatement(ExpectIdentifierName());
            }
            if (IsIdentifier("endpoint"))
            {
                Advance();
                return new DescribeModbusEndpointStatement(ExpectIdentifierName());
            }
            if (Current.Kind == TokenKind.KeywordTable)
            {
                Advance();
                return new DescribeModbusTableStatement(ExpectIdentifierName());
            }
            throw Error("DESCRIBE MODBUS 后面期望 SOURCE / ENDPOINT / TABLE");
        }

        if (Current.Kind == TokenKind.KeywordTable)
        {
            Advance();
            return new DescribeTableStatement(ExpectIdentifierName());
        }

        if (IsIdentifier("graph"))
        {
            Advance();
            return new DescribeGraphStatement(ExpectIdentifierName());
        }

        if (IsIdentifier("property"))
        {
            Advance();
            ExpectIdentifier("graph", "DESCRIBE PROPERTY 后面期望 GRAPH");
            return new DescribePropertyGraphStatement(ExpectIdentifierName());
        }

        if (Current.Kind == TokenKind.KeywordDocument)
        {
            Advance();
            Expect(TokenKind.KeywordCollection);
            return new DescribeDocumentCollectionStatement(ExpectIdentifierName());
        }

        if (IsIdentifier("view"))
        {
            Advance();
            return new DescribeViewStatement(ExpectIdentifierName());
        }

        if (IsIdentifier("procedure"))
        {
            Advance();
            return new DescribeProcedureStatement(ExpectIdentifierName());
        }

        if (IsIdentifier("trigger"))
        {
            Advance();
            return new DescribeTriggerStatement(ExpectIdentifierName());
        }

        if (IsIdentifier("materialized"))
        {
            Advance();
            if (!IsIdentifier("view"))
                throw Error("DESCRIBE MATERIALIZED 后面期望 VIEW");
            Advance();
            return new DescribeMaterializedViewStatement(ExpectIdentifierName());
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
