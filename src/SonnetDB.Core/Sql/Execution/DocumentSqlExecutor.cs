using System.Globalization;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.FullText;
using SonnetDB.Sql.Ast;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// JSON 文档集合的 SQL 执行辅助。
/// </summary>
internal static class DocumentSqlExecutor
{
    private static readonly IReadOnlyList<string> _nameColumns =
        new List<string>(1) { "name" }.AsReadOnly();
    private static readonly IReadOnlyList<string> _describeColumns =
        new List<string>(7) { "collection_name", "document_count", "index_count", "indexes", "fulltext_index_count", "fulltext_indexes", "created_utc" }.AsReadOnly();
    private static readonly IReadOnlyList<string> _showIndexColumns =
        new List<string>(3) { "index_name", "path", "created_utc" }.AsReadOnly();
    private static readonly IReadOnlyList<string> _showFullTextIndexColumns =
        new List<string>(5) { "index_name", "fields", "tokenizer", "document_count", "created_utc" }.AsReadOnly();

    public static DocumentCollectionSchema ExecuteCreateCollection(Tsdb tsdb, CreateDocumentCollectionStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        if (statement.IfNotExists)
        {
            var existing = tsdb.Documents.Catalog.TryGet(statement.Name);
            if (existing is not null)
                return existing;
        }

        var schema = DocumentCollectionSchema.Create(statement.Name);
        tsdb.Documents.Create(schema);
        return schema;
    }

    public static DocumentPathIndex ExecuteCreateIndex(Tsdb tsdb, CreateDocumentPathIndexStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        var schema = tsdb.Documents.Catalog.TryGet(statement.CollectionName)
            ?? throw new InvalidOperationException($"document collection '{statement.CollectionName}' 不存在。");
        if (statement.IfNotExists && schema.TryGetIndex(statement.IndexName) is { } existing)
            return existing;

        return tsdb.Documents.CreateIndex(
            statement.CollectionName,
            new DocumentPathIndexDefinition(statement.IndexName, statement.Path));
    }

    public static DocumentFullTextIndex ExecuteCreateFullTextIndex(Tsdb tsdb, CreateFullTextIndexStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        var schema = tsdb.Documents.Catalog.TryGet(statement.CollectionName)
            ?? throw new InvalidOperationException($"document collection '{statement.CollectionName}' 不存在。");
        if (statement.IfNotExists && schema.TryGetFullTextIndex(statement.IndexName) is { } existing)
            return existing;

        return tsdb.Documents.CreateFullTextIndex(
            statement.CollectionName,
            new DocumentFullTextIndexDefinition(statement.IndexName, statement.Fields, statement.Tokenizer));
    }

    public static RowsAffectedExecutionResult ExecuteDropCollection(Tsdb tsdb, DropDocumentCollectionStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        bool removed = tsdb.Documents.Drop(statement.Name);
        return new RowsAffectedExecutionResult(statement.Name, removed ? 1 : 0, "drop_document_collection");
    }

    public static RowsAffectedExecutionResult ExecuteDropIndex(Tsdb tsdb, DropDocumentPathIndexStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        bool removed = tsdb.Documents.DropIndex(statement.CollectionName, statement.IndexName);
        return new RowsAffectedExecutionResult(statement.CollectionName, removed ? 1 : 0, "drop_json_index");
    }

    public static RowsAffectedExecutionResult ExecuteDropFullTextIndex(Tsdb tsdb, DropFullTextIndexStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        bool removed = tsdb.Documents.DropFullTextIndex(statement.CollectionName, statement.IndexName);
        return new RowsAffectedExecutionResult(statement.CollectionName, removed ? 1 : 0, "drop_fulltext_index");
    }

    public static InsertExecutionResult ExecuteInsert(Tsdb tsdb, InsertStatement statement, DocumentCollectionSchema schema)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(schema);

        int idColumn = FindRequiredColumn(statement.Columns, "id");
        int documentColumn = FindRequiredDocumentColumn(statement.Columns);
        var store = tsdb.Documents.Open(schema.Name);

        foreach (var row in statement.Rows)
        {
            string id = ConvertId(row[idColumn]);
            string json = ConvertJson(row[documentColumn]);
            store.Upsert(id, json);
        }

        return new InsertExecutionResult(schema.Name, statement.Rows.Count);
    }

    public static SelectExecutionResult ExecuteSelect(Tsdb tsdb, SelectStatement statement, DocumentCollectionSchema schema)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(schema);

        ValidateAliasReferences(statement);
        if (statement.TableValuedFunction is not null)
            throw new InvalidOperationException("文档集合 SELECT 不支持 FROM 表值函数。");
        if (statement.GroupBy.Count != 0)
            throw new InvalidOperationException("文档集合暂不支持 GROUP BY。");

        var projections = BuildProjections(statement.Projections);
        var store = tsdb.Documents.Open(schema.Name);
        var match = TryExtractMatch(schema, statement.Where, statement.Pagination);
        if (match is not null)
            match = ResolveFullTextMatch(store, match);
        var candidateRows = LoadCandidateRows(store, schema, statement.Where, match);
        var matchScores = match is null
            ? new Dictionary<string, double>(StringComparer.Ordinal)
            : match.Hits.ToDictionary(static hit => hit.DocumentId, static hit => hit.Score, StringComparer.Ordinal);

        var filtered = new List<IReadOnlyList<object?>>();
        foreach (var row in candidateRows)
        {
            if (!EvaluateWhere(statement.Where, row, matchScores))
                continue;

            var output = new object?[projections.Length];
            for (int i = 0; i < projections.Length; i++)
                output[i] = EvaluateProjection(projections[i], row, matchScores);
            filtered.Add(output);
        }

        var result = new SelectExecutionResult(
            projections.Select(static p => p.ColumnName).ToArray(),
            filtered);
        return ApplyPagination(ApplyOrderBy(result, statement.OrderBy), statement.Pagination);
    }

    public static DeleteExecutionResult ExecuteDelete(Tsdb tsdb, DeleteStatement statement, DocumentCollectionSchema schema)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(schema);

        var store = tsdb.Documents.Open(schema.Name);
        int deleted = 0;
        if (TryExtractId(statement.Where, out var id))
        {
            deleted = store.Delete(id) ? 1 : 0;
        }
        else
        {
            var match = TryExtractMatch(schema, statement.Where, pagination: null);
            if (match is not null)
                match = ResolveFullTextMatch(store, match);
            var matchScores = match is null
                ? new Dictionary<string, double>(StringComparer.Ordinal)
                : match.Hits.ToDictionary(static hit => hit.DocumentId, static hit => hit.Score, StringComparer.Ordinal);
            foreach (var row in LoadCandidateRows(store, schema, statement.Where, match))
            {
                if (!EvaluateWhere(statement.Where, row, matchScores))
                    continue;
                if (store.Delete(row.Id))
                    deleted++;
            }
        }

        return new DeleteExecutionResult(statement.Measurement, SeriesAffected: deleted, TombstonesAdded: deleted);
    }

    public static RowsAffectedExecutionResult ExecuteUpdate(Tsdb tsdb, UpdateStatement statement, DocumentCollectionSchema schema)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(schema);

        if (statement.Assignments.Count != 1
            || !IsDocumentColumn(statement.Assignments[0].ColumnName))
        {
            throw new InvalidOperationException("文档集合 UPDATE 仅支持 SET document = '<json>'。");
        }

        var store = tsdb.Documents.Open(schema.Name);
        int updated = 0;
        var match = TryExtractMatch(schema, statement.Where, pagination: null);
        if (match is not null)
            match = ResolveFullTextMatch(store, match);
        var matchScores = match is null
            ? new Dictionary<string, double>(StringComparer.Ordinal)
            : match.Hits.ToDictionary(static hit => hit.DocumentId, static hit => hit.Score, StringComparer.Ordinal);
        foreach (var row in LoadCandidateRows(store, schema, statement.Where, match))
        {
            if (!EvaluateWhere(statement.Where, row, matchScores))
                continue;

            store.Upsert(row.Id, ConvertJson(statement.Assignments[0].Value));
            updated++;
        }

        return new RowsAffectedExecutionResult(schema.Name, updated, "update_document");
    }

    public static SelectExecutionResult ShowCollections(Tsdb tsdb)
    {
        ArgumentNullException.ThrowIfNull(tsdb);

        var snapshot = tsdb.Documents.Catalog.Snapshot();
        var rows = new List<IReadOnlyList<object?>>(snapshot.Count);
        foreach (var schema in snapshot)
            rows.Add(new object?[] { schema.Name });
        return new SelectExecutionResult(_nameColumns, rows);
    }

    public static SelectExecutionResult DescribeCollection(Tsdb tsdb, string name)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var schema = tsdb.Documents.Catalog.TryGet(name)
            ?? throw new InvalidOperationException($"document collection '{name}' 不存在。");
        var store = tsdb.Documents.Open(schema.Name);
        var rows = new List<IReadOnlyList<object?>>(1)
        {
            new object?[]
            {
                schema.Name,
                (long)store.Scan(int.MaxValue).Count,
                (long)schema.Indexes.Count,
                string.Join(",", schema.Indexes.Select(static i => $"{i.Name}:{i.Path}")),
                (long)schema.FullTextIndexes.Count,
                string.Join(",", schema.FullTextIndexes.Select(static i => $"{i.Name}:{string.Join("|", i.Fields)}:{i.Tokenizer}")),
                new DateTime(schema.CreatedAtUtcTicks, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture),
            },
        };

        return new SelectExecutionResult(_describeColumns, rows);
    }

    public static SelectExecutionResult ShowIndexes(Tsdb tsdb, string collectionName)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);

        var schema = tsdb.Documents.Catalog.TryGet(collectionName)
            ?? throw new InvalidOperationException($"document collection '{collectionName}' 不存在。");
        var rows = new List<IReadOnlyList<object?>>(schema.Indexes.Count);
        foreach (var index in schema.Indexes.OrderBy(static i => i.Name, StringComparer.Ordinal))
        {
            rows.Add(new object?[]
            {
                index.Name,
                index.Path,
                new DateTime(index.CreatedAtUtcTicks, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture),
            });
        }

        return new SelectExecutionResult(_showIndexColumns, rows);
    }

    public static SelectExecutionResult ShowFullTextIndexes(Tsdb tsdb, string collectionName)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);

        var schema = tsdb.Documents.Catalog.TryGet(collectionName)
            ?? throw new InvalidOperationException($"document collection '{collectionName}' 不存在。");
        var store = tsdb.Documents.Open(schema.Name);
        var rows = new List<IReadOnlyList<object?>>(schema.FullTextIndexes.Count);
        foreach (var index in schema.FullTextIndexes.OrderBy(static i => i.Name, StringComparer.Ordinal))
        {
            rows.Add(new object?[]
            {
                index.Name,
                string.Join(",", index.Fields),
                index.Tokenizer,
                (long)store.GetFullTextDocumentCount(index),
                new DateTime(index.CreatedAtUtcTicks, DateTimeKind.Utc).ToString("o", CultureInfo.InvariantCulture),
            });
        }

        return new SelectExecutionResult(_showFullTextIndexColumns, rows);
    }

    public static (string AccessPath, string? IndexName, int EstimatedRows) ExplainAccess(
        Tsdb tsdb,
        DocumentCollectionSchema schema,
        SqlExpression? where)
    {
        var store = tsdb.Documents.Open(schema.Name);
        if (TryExtractId(where, out var id))
            return ("document_id", "primary", store.Get(id) is null ? 0 : 1);

        if (TryExtractMatch(schema, where, pagination: null) is { } match)
        {
            match = ResolveFullTextMatch(store, match);
            return ("fulltext_index", match.Index.Name, match.Hits.Count);
        }

        if (TryChoosePathIndex(schema, where, out var index, out var value))
            return ("json_path_index", index.Name, store.GetByIndex(index, value).Count);

        return ("document_scan", null, store.Scan().Count);
    }

    private static int FindRequiredColumn(IReadOnlyList<string> columns, string name)
    {
        for (int i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        throw new InvalidOperationException($"文档集合 INSERT 必须包含 '{name}' 列。");
    }

    private static int FindRequiredDocumentColumn(IReadOnlyList<string> columns)
    {
        for (int i = 0; i < columns.Count; i++)
        {
            if (IsDocumentColumn(columns[i]))
                return i;
        }

        throw new InvalidOperationException("文档集合 INSERT 必须包含 'document' 或 'json' 列。");
    }

    private static bool IsDocumentColumn(string name)
        => string.Equals(name, "document", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "json", StringComparison.OrdinalIgnoreCase);

    private static string ConvertId(SqlExpression expression)
        => expression switch
        {
            LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var value } => value!,
            LiteralExpression { Kind: SqlLiteralKind.Integer, IntegerValue: var value } => value.ToString(CultureInfo.InvariantCulture),
            _ => throw new InvalidOperationException("文档 ID 必须是字符串或整数字面量。"),
        };

    private static string ConvertJson(SqlExpression expression)
        => expression switch
        {
            LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var value } => JsonPathEvaluator.NormalizeJson(value!),
            _ => throw new InvalidOperationException("文档 JSON 必须是字符串字面量。"),
        };

    private static IReadOnlyList<DocumentRow> LoadCandidateRows(
        DocumentCollectionStore store,
        DocumentCollectionSchema schema,
        SqlExpression? where,
        FullTextMatch? match = null)
    {
        if (match is not null)
        {
            var rows = new List<DocumentRow>(match.Hits.Count);
            foreach (var hit in match.Hits)
            {
                var row = store.Get(hit.DocumentId);
                if (row is not null)
                    rows.Add(row);
            }

            return rows;
        }

        if (TryExtractId(where, out var id))
        {
            var row = store.Get(id);
            return row is null ? Array.Empty<DocumentRow>() : [row];
        }

        if (TryChoosePathIndex(schema, where, out var index, out var value))
            return store.GetByIndex(index, value);

        return store.Scan();
    }

    private static FullTextMatch? TryExtractMatch(
        DocumentCollectionSchema schema,
        SqlExpression? where,
        PaginationSpec? pagination)
    {
        if (where is null)
            return null;

        FullTextMatch? found = null;
        foreach (var leaf in FlattenAnd(where))
        {
            if (leaf is FunctionCallExpression function
                && TryBindMatch(schema, function, pagination, out var match))
            {
                if (found is not null)
                    throw new InvalidOperationException("文档集合 WHERE 当前仅支持一个 match(...) 全文谓词。");
                found = match;
                continue;
            }
            if (ContainsMatchFunction(leaf))
                throw new InvalidOperationException("match(...) 必须作为 WHERE 中独立的 AND 谓词使用。");
        }

        return found;
    }

    private static bool IsMatchFunction(FunctionCallExpression function)
        => string.Equals(function.Name, "match", StringComparison.OrdinalIgnoreCase);

    private static bool TryBindMatch(
        DocumentCollectionSchema schema,
        FunctionCallExpression function,
        PaginationSpec? pagination,
        out FullTextMatch match)
    {
        match = null!;
        if (!IsMatchFunction(function))
            return false;

        if (function.IsStar || function.Arguments.Count is < 3 or > 4)
            throw new InvalidOperationException("match(...) 需要 3 到 4 个参数：match(index, field, query[, topK])。");

        if (function.Arguments[0] is not IdentifierExpression { Name: var indexName })
            throw new InvalidOperationException("match 第 1 个参数必须是全文索引名。");
        string field;
        if (function.Arguments[1] is StarExpression)
        {
            field = "*";
        }
        else if (function.Arguments[1] is IdentifierExpression { Name: var fieldName })
        {
            field = fieldName;
        }
        else if (function.Arguments[1] is LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var fieldText })
        {
            field = fieldText!;
        }
        else
        {
            throw new InvalidOperationException("match 第 2 个参数必须是全文索引字段名、'*' 或字符串字段名。");
        }
        if (function.Arguments[2] is not LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var queryText })
            throw new InvalidOperationException("match 第 3 个参数必须是查询字符串。");

        var index = schema.TryGetFullTextIndex(indexName)
            ?? throw new InvalidOperationException($"document collection '{schema.Name}' 中不存在全文索引 '{indexName}'。");

        int topK = DefaultFullTextTopK(pagination);
        if (function.Arguments.Count == 4)
        {
            if (function.Arguments[3] is not LiteralExpression { Kind: SqlLiteralKind.Integer, IntegerValue: var literalTopK })
                throw new InvalidOperationException("match 第 4 个参数 topK 必须是正整数字面量。");
            if (literalTopK <= 0 || literalTopK > int.MaxValue)
                throw new InvalidOperationException("match 第 4 个参数 topK 必须是正整数且不超过 Int32.MaxValue。");
            topK = (int)literalTopK;
        }

        match = new FullTextMatch(index, field, queryText!, topK, Hits: []);
        return true;
    }

    private static int DefaultFullTextTopK(PaginationSpec? pagination)
    {
        if (pagination is null)
            return 100;

        long topK = pagination.Fetch is int fetch
            ? (long)pagination.Offset + fetch
            : (long)pagination.Offset + 100;

        if (topK <= 0)
            return 0;
        return topK > int.MaxValue ? int.MaxValue : (int)topK;
    }

    private static FullTextMatch ResolveFullTextMatch(
        DocumentCollectionStore store,
        FullTextMatch match)
    {
        var hits = store.SearchFullText(match.Index, match.Field, match.QueryText, match.TopK);
        return match with { Hits = hits };
    }

    private sealed record FullTextMatch(
        DocumentFullTextIndex Index,
        string Field,
        string QueryText,
        int TopK,
        IReadOnlyList<DocumentFullTextSearchHit> Hits);

    private static bool TryExtractId(SqlExpression? where, out string id)
    {
        id = string.Empty;
        if (where is null)
            return false;

        foreach (var leaf in FlattenAnd(where))
        {
            if (leaf is not BinaryExpression { Operator: SqlBinaryOperator.Equal } binary)
                continue;

            var (identifier, value) = NormalizeIdentifierComparison(binary);
            if (identifier is null || !string.Equals(identifier.Name, "id", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                id = ConvertId(value!);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        return false;
    }

    private static bool TryChoosePathIndex(
        DocumentCollectionSchema schema,
        SqlExpression? where,
        out DocumentPathIndex index,
        out object? value)
    {
        index = null!;
        value = null;
        if (where is null || schema.Indexes.Count == 0)
            return false;

        foreach (var leaf in FlattenAnd(where))
        {
            if (leaf is not BinaryExpression { Operator: SqlBinaryOperator.Equal } binary)
                continue;

            if (TryExtractJsonValueComparison(binary, out var path, out value))
            {
                foreach (var candidate in schema.Indexes)
                {
                    if (string.Equals(candidate.Path, path.Text, StringComparison.Ordinal))
                    {
                        index = candidate;
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool TryExtractJsonValueComparison(
        BinaryExpression binary,
        out JsonPath path,
        out object? literalValue)
    {
        path = null!;
        literalValue = null;
        if (TryBindJsonValue(binary.Left, out path) && TryEvaluateLiteral(binary.Right, out literalValue))
            return true;
        if (TryBindJsonValue(binary.Right, out path) && TryEvaluateLiteral(binary.Left, out literalValue))
            return true;
        return false;
    }

    private static bool TryBindJsonValue(SqlExpression expression, out JsonPath path)
    {
        path = null!;
        if (expression is not FunctionCallExpression
            {
                Name: var name,
                IsStar: false,
                Arguments.Count: 2,
                Arguments: [IdentifierExpression documentColumn, LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var pathText }]
            }
            || !string.Equals(name, "json_value", StringComparison.OrdinalIgnoreCase)
            || !IsDocumentColumn(documentColumn.Name))
        {
            return false;
        }

        try
        {
            path = JsonPath.Parse(pathText!);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static Projection[] BuildProjections(IReadOnlyList<SelectItem> items)
    {
        var projections = new List<Projection>(items.Count);
        foreach (var item in items)
        {
            switch (item.Expression)
            {
                case StarExpression:
                    if (item.Alias is not null)
                        throw new InvalidOperationException("'*' 不允许带 alias。");
                    projections.Add(new Projection("id", new IdentifierExpression("id")));
                    projections.Add(new Projection("document", new IdentifierExpression("document")));
                    break;

                case IdentifierExpression id:
                    ValidateIdentifier(id);
                    projections.Add(new Projection(item.Alias ?? id.Name, item.Expression));
                    break;

                case FunctionCallExpression function:
                    projections.Add(new Projection(item.Alias ?? FormatFunctionColumnName(function), item.Expression));
                    break;

                case LiteralExpression literal:
                    projections.Add(new Projection(item.Alias ?? FormatLiteralColumnName(literal), item.Expression));
                    break;

                default:
                    throw new InvalidOperationException(
                        $"文档集合 SELECT 暂不支持投影表达式 '{item.Expression.GetType().Name}'。");
            }
        }

        return [.. projections];
    }

    private static object? EvaluateProjection(
        Projection projection,
        DocumentRow row,
        IReadOnlyDictionary<string, double> matchScores)
        => EvaluateScalar(projection.Expression, row, matchScores);

    private static bool EvaluateWhere(
        SqlExpression? expression,
        DocumentRow row,
        IReadOnlyDictionary<string, double> matchScores)
    {
        if (expression is null)
            return true;

        return EvaluateBoolean(expression, row, matchScores);
    }

    private static bool EvaluateBoolean(
        SqlExpression expression,
        DocumentRow row,
        IReadOnlyDictionary<string, double> matchScores)
    {
        switch (expression)
        {
            case BinaryExpression binary:
                if (binary.Operator == SqlBinaryOperator.And)
                    return EvaluateBoolean(binary.Left, row, matchScores) && EvaluateBoolean(binary.Right, row, matchScores);
                if (binary.Operator == SqlBinaryOperator.Or)
                    return EvaluateBoolean(binary.Left, row, matchScores) || EvaluateBoolean(binary.Right, row, matchScores);
                if (ContainsMatchFunction(binary))
                    return false;
                if (IsComparisonOperator(binary.Operator))
                    return EvaluateComparison(binary, row, matchScores);
                break;

            case UnaryExpression { Operator: SqlUnaryOperator.Not } unary:
                return !EvaluateBoolean(unary.Operand, row, matchScores);
        }

        var value = EvaluateScalar(expression, row, matchScores);
        if (value is bool b)
            return b;
        throw new InvalidOperationException("WHERE 表达式必须计算为布尔值。");
    }

    private static bool EvaluateComparison(
        BinaryExpression binary,
        DocumentRow row,
        IReadOnlyDictionary<string, double> matchScores)
    {
        var left = EvaluateScalar(binary.Left, row, matchScores);
        var right = EvaluateScalar(binary.Right, row, matchScores);
        int? compare = CompareScalar(left, right);

        return binary.Operator switch
        {
            SqlBinaryOperator.Equal => ValuesEqual(left, right),
            SqlBinaryOperator.NotEqual => !ValuesEqual(left, right),
            SqlBinaryOperator.LessThan => compare is < 0,
            SqlBinaryOperator.LessThanOrEqual => compare is <= 0,
            SqlBinaryOperator.GreaterThan => compare is > 0,
            SqlBinaryOperator.GreaterThanOrEqual => compare is >= 0,
            SqlBinaryOperator.Like => LikePatternMatcher.IsMatch(left, right),
            SqlBinaryOperator.NotLike => !LikePatternMatcher.IsMatch(left, right),
            SqlBinaryOperator.Regex => RegexPatternMatcher.IsMatch(left, right),
            SqlBinaryOperator.NotRegex => !RegexPatternMatcher.IsMatch(left, right),
            _ => throw new InvalidOperationException($"不支持的比较运算符 {binary.Operator}。"),
        };
    }

    private static object? EvaluateScalar(
        SqlExpression expression,
        DocumentRow row,
        IReadOnlyDictionary<string, double> matchScores)
    {
        return expression switch
        {
            LiteralExpression literal => EvaluateLiteral(literal),
            IdentifierExpression identifier => GetIdentifierValue(identifier, row),
            FunctionCallExpression function => EvaluateFunction(function, row, matchScores),
            UnaryExpression { Operator: SqlUnaryOperator.Negate } unary => -RequireDouble(EvaluateScalar(unary.Operand, row, matchScores), "一元负号"),
            BinaryExpression binary when IsArithmeticOperator(binary.Operator) => EvaluateArithmetic(binary, row, matchScores),
            _ => throw new InvalidOperationException(
                $"文档集合表达式暂不支持 '{expression.GetType().Name}'。"),
        };
    }

    private static object? EvaluateFunction(
        FunctionCallExpression function,
        DocumentRow row,
        IReadOnlyDictionary<string, double> matchScores)
    {
        if (string.Equals(function.Name, "match", StringComparison.OrdinalIgnoreCase))
            return matchScores.ContainsKey(row.Id);

        if (string.Equals(function.Name, "bm25_score", StringComparison.OrdinalIgnoreCase))
        {
            if (function.IsStar || function.Arguments.Count != 0)
                throw new InvalidOperationException("bm25_score() 不接受参数。");
            return matchScores.TryGetValue(row.Id, out double score) ? score : null;
        }

        if (!string.Equals(function.Name, "json_value", StringComparison.OrdinalIgnoreCase)
            || function.IsStar
            || function.Arguments.Count != 2
            || function.Arguments[1] is not LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var path })
        {
            throw new InvalidOperationException("文档集合当前仅支持 json_value(document, '$.path')、match(...) 与 bm25_score() 函数。");
        }

        var json = EvaluateScalar(function.Arguments[0], row, matchScores) as string;
        return JsonPathEvaluator.Evaluate(json, path!);
    }

    private static object EvaluateArithmetic(
        BinaryExpression binary,
        DocumentRow row,
        IReadOnlyDictionary<string, double> matchScores)
    {
        var left = RequireDouble(EvaluateScalar(binary.Left, row, matchScores), binary.Operator.ToString());
        var right = RequireDouble(EvaluateScalar(binary.Right, row, matchScores), binary.Operator.ToString());
        return binary.Operator switch
        {
            SqlBinaryOperator.Add => left + right,
            SqlBinaryOperator.Subtract => left - right,
            SqlBinaryOperator.Multiply => left * right,
            SqlBinaryOperator.Divide => left / right,
            SqlBinaryOperator.Modulo => left % right,
            _ => throw new InvalidOperationException($"不支持的算术运算符 {binary.Operator}。"),
        };
    }

    private static object? GetIdentifierValue(IdentifierExpression identifier, DocumentRow row)
    {
        ValidateIdentifier(identifier);
        if (string.Equals(identifier.Name, "id", StringComparison.OrdinalIgnoreCase))
            return row.Id;
        return row.Json;
    }

    private static void ValidateIdentifier(IdentifierExpression identifier)
    {
        if (string.Equals(identifier.Name, "id", StringComparison.OrdinalIgnoreCase)
            || IsDocumentColumn(identifier.Name))
        {
            return;
        }

        throw new InvalidOperationException($"文档集合只暴露 id 与 document/json 伪列，未知列 '{identifier.Name}'。");
    }

    private static bool TryEvaluateLiteral(SqlExpression expression, out object? value)
    {
        value = null;
        if (expression is not LiteralExpression literal)
            return false;
        value = EvaluateLiteral(literal);
        return true;
    }

    private static object? EvaluateLiteral(LiteralExpression literal) => literal.Kind switch
    {
        SqlLiteralKind.Null => null,
        SqlLiteralKind.Boolean => literal.BooleanValue,
        SqlLiteralKind.Integer => literal.IntegerValue,
        SqlLiteralKind.Float => literal.FloatValue,
        SqlLiteralKind.String => literal.StringValue,
        _ => throw new InvalidOperationException($"不支持的字面量类型 {literal.Kind}。"),
    };

    private static IEnumerable<SqlExpression> FlattenAnd(SqlExpression expression)
    {
        if (expression is BinaryExpression { Operator: SqlBinaryOperator.And } binary)
        {
            foreach (var left in FlattenAnd(binary.Left))
                yield return left;
            foreach (var right in FlattenAnd(binary.Right))
                yield return right;
            yield break;
        }

        yield return expression;
    }

    private static (IdentifierExpression? Identifier, SqlExpression? Value) NormalizeIdentifierComparison(BinaryExpression binary)
    {
        if (binary.Left is IdentifierExpression left)
            return (left, binary.Right);
        if (binary.Right is IdentifierExpression right)
            return (right, binary.Left);
        return (null, null);
    }

    private static SelectExecutionResult ApplyOrderBy(SelectExecutionResult result, OrderBySpec? orderBy)
    {
        if (orderBy is null)
            return result;

        if (orderBy.Expression is not IdentifierExpression { Name: var name })
            throw new InvalidOperationException("文档集合 ORDER BY 当前仅支持结果列名。");

        int columnIndex = -1;
        for (int i = 0; i < result.Columns.Count; i++)
        {
            if (string.Equals(result.Columns[i], name, StringComparison.Ordinal))
            {
                columnIndex = i;
                break;
            }
        }

        if (columnIndex < 0)
            throw new InvalidOperationException($"ORDER BY 引用了结果集中不存在的列 '{name}'。");

        var rows = orderBy.Direction == SortDirection.Descending
            ? result.Rows.OrderByDescending(row => row[columnIndex], ScalarComparer.Instance).ToArray()
            : result.Rows.OrderBy(row => row[columnIndex], ScalarComparer.Instance).ToArray();
        return new SelectExecutionResult(result.Columns, rows);
    }

    private static SelectExecutionResult ApplyPagination(SelectExecutionResult result, PaginationSpec? pagination)
    {
        if (pagination is null)
            return result;

        int offset = pagination.Offset;
        if (offset >= result.Rows.Count)
            return new SelectExecutionResult(result.Columns, []);

        int take = pagination.Fetch ?? (result.Rows.Count - offset);
        if (take <= 0)
            return new SelectExecutionResult(result.Columns, []);

        return new SelectExecutionResult(
            result.Columns,
            result.Rows.Skip(offset).Take(Math.Min(take, result.Rows.Count - offset)).ToArray());
    }

    private static void ValidateAliasReferences(SelectStatement statement)
    {
        foreach (var identifier in EnumerateIdentifierReferences(statement))
        {
            if (identifier.Qualifier is null)
                continue;

            if (statement.TableAlias is null)
            {
                throw new InvalidOperationException(
                    $"限定列名 '{identifier.Qualifier}.{identifier.Name}' 要求 FROM 子句声明单表别名。");
            }

            if (!string.Equals(identifier.Qualifier, statement.TableAlias, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"限定列名 '{identifier.Qualifier}.{identifier.Name}' 引用了未知别名 '{identifier.Qualifier}'；当前查询只声明了别名 '{statement.TableAlias}'。");
            }
        }
    }

    private static IEnumerable<IdentifierExpression> EnumerateIdentifierReferences(SelectStatement statement)
    {
        foreach (var projection in statement.Projections)
        {
            foreach (var identifier in EnumerateIdentifierReferences(projection.Expression))
                yield return identifier;
        }

        if (statement.Where is not null)
        {
            foreach (var identifier in EnumerateIdentifierReferences(statement.Where))
                yield return identifier;
        }

        if (statement.OrderBy is not null)
        {
            foreach (var identifier in EnumerateIdentifierReferences(statement.OrderBy.Expression))
                yield return identifier;
        }
    }

    private static IEnumerable<IdentifierExpression> EnumerateIdentifierReferences(SqlExpression expression)
    {
        switch (expression)
        {
            case IdentifierExpression identifier:
                yield return identifier;
                yield break;

            case FunctionCallExpression function:
                if (string.Equals(function.Name, "match", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var argument in function.Arguments.Skip(2))
                    {
                        foreach (var identifier in EnumerateIdentifierReferences(argument))
                            yield return identifier;
                    }

                    yield break;
                }

                foreach (var argument in function.Arguments)
                {
                    foreach (var identifier in EnumerateIdentifierReferences(argument))
                        yield return identifier;
                }
                yield break;

            case UnaryExpression unary:
                foreach (var identifier in EnumerateIdentifierReferences(unary.Operand))
                    yield return identifier;
                yield break;

            case BinaryExpression binary:
                foreach (var identifier in EnumerateIdentifierReferences(binary.Left))
                    yield return identifier;
                foreach (var identifier in EnumerateIdentifierReferences(binary.Right))
                    yield return identifier;
                yield break;
        }
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left is null || right is null)
            return left is null && right is null;

        if (IsNumeric(left) && IsNumeric(right))
            return Convert.ToDouble(left, CultureInfo.InvariantCulture)
                .Equals(Convert.ToDouble(right, CultureInfo.InvariantCulture));

        return Equals(left, right);
    }

    private static int? CompareScalar(object? left, object? right)
    {
        if (left is null || right is null)
            return null;

        if (IsNumeric(left) && IsNumeric(right))
            return Convert.ToDouble(left, CultureInfo.InvariantCulture)
                .CompareTo(Convert.ToDouble(right, CultureInfo.InvariantCulture));

        if (left is string leftString && right is string rightString)
            return string.Compare(leftString, rightString, StringComparison.Ordinal);

        if (left is bool leftBool && right is bool rightBool)
            return leftBool.CompareTo(rightBool);

        throw new InvalidOperationException($"无法比较 {left.GetType().Name} 与 {right.GetType().Name}。");
    }

    private static double RequireDouble(object? value, string operatorName)
    {
        if (value is null)
            throw new InvalidOperationException($"运算 {operatorName} 不接受 NULL 参数。");
        if (!IsNumeric(value))
            throw new InvalidOperationException($"运算 {operatorName} 需要数值参数。");
        return Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private static bool IsNumeric(object value) => value is
        byte or sbyte or
        short or ushort or
        int or uint or
        long or ulong or
        float or double or decimal;

    private static bool IsComparisonOperator(SqlBinaryOperator op) => op is
        SqlBinaryOperator.Equal or
        SqlBinaryOperator.NotEqual or
        SqlBinaryOperator.LessThan or
        SqlBinaryOperator.LessThanOrEqual or
        SqlBinaryOperator.GreaterThan or
        SqlBinaryOperator.GreaterThanOrEqual or
        SqlBinaryOperator.Like or
        SqlBinaryOperator.NotLike or
        SqlBinaryOperator.Regex or
        SqlBinaryOperator.NotRegex;

    private static bool IsArithmeticOperator(SqlBinaryOperator op) => op is
        SqlBinaryOperator.Add or
        SqlBinaryOperator.Subtract or
        SqlBinaryOperator.Multiply or
        SqlBinaryOperator.Divide or
        SqlBinaryOperator.Modulo;

    private static bool ContainsMatchFunction(SqlExpression expression)
    {
        switch (expression)
        {
            case FunctionCallExpression function:
                if (string.Equals(function.Name, "match", StringComparison.OrdinalIgnoreCase))
                    return true;
                foreach (var argument in function.Arguments)
                {
                    if (ContainsMatchFunction(argument))
                        return true;
                }
                return false;

            case UnaryExpression unary:
                return ContainsMatchFunction(unary.Operand);

            case BinaryExpression binary:
                return ContainsMatchFunction(binary.Left) || ContainsMatchFunction(binary.Right);

            default:
                return false;
        }
    }

    private static string FormatLiteralColumnName(LiteralExpression literal) => literal.Kind switch
    {
        SqlLiteralKind.Null => "NULL",
        SqlLiteralKind.Boolean => literal.BooleanValue ? "TRUE" : "FALSE",
        SqlLiteralKind.Integer => literal.IntegerValue.ToString(CultureInfo.InvariantCulture),
        SqlLiteralKind.Float => literal.FloatValue.ToString(CultureInfo.InvariantCulture),
        SqlLiteralKind.String => literal.StringValue ?? string.Empty,
        _ => literal.Kind.ToString(),
    };

    private static string FormatFunctionColumnName(FunctionCallExpression function)
        => function.Arguments.Count == 2
            && function.Arguments[1] is LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var path }
            ? path!
            : function.Name;

    private sealed record Projection(string ColumnName, SqlExpression Expression);

    private sealed class ScalarComparer : IComparer<object?>
    {
        public static ScalarComparer Instance { get; } = new();

        public int Compare(object? x, object? y)
        {
            if (x is null && y is null)
                return 0;
            if (x is null)
                return -1;
            if (y is null)
                return 1;
            return CompareScalar(x, y) ?? 0;
        }
    }
}
