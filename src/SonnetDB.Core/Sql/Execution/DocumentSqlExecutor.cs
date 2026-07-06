using System.Globalization;
using System.Text.Json;
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
        new List<string>(10) { "collection_name", "document_count", "index_count", "indexes", "fulltext_index_count", "fulltext_indexes", "validator_enabled", "validation_action", "validator_rules", "created_utc" }.AsReadOnly();
    private static readonly IReadOnlyList<string> _showIndexColumns =
        new List<string>(9) { "index_name", "paths", "is_unique", "is_sparse", "is_partial", "partial_filter", "is_ttl", "ttl_seconds", "created_utc" }.AsReadOnly();
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

    public static DocumentPathIndex ExecuteCreateIndex(Tsdb tsdb, CreateDocumentIndexStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        var schema = tsdb.Documents.Catalog.TryGet(statement.CollectionName)
            ?? throw new InvalidOperationException($"document collection '{statement.CollectionName}' 不存在。");
        if (statement.IfNotExists && schema.TryGetIndex(statement.IndexName) is { } existing)
            return existing;

        return tsdb.Documents.CreateIndex(
            statement.CollectionName,
            new DocumentPathIndexDefinition(
                statement.IndexName,
                statement.Paths,
                IsUnique: statement.IsUnique,
                IsSparse: statement.IsSparse,
                PartialFilter: BindPartialFilter(statement.PartialFilter),
                TtlSeconds: statement.TtlSeconds));
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

    public static RowsAffectedExecutionResult ExecuteSetValidator(Tsdb tsdb, AlterDocumentCollectionSetValidatorStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        var definition = ParseValidatorDefinition(statement.ValidatorJson, statement.ValidationAction);
        tsdb.Documents.SetValidator(statement.CollectionName, definition);
        return new RowsAffectedExecutionResult(statement.CollectionName, 1, "set_document_validator");
    }

    public static RowsAffectedExecutionResult ExecuteDropValidator(Tsdb tsdb, AlterDocumentCollectionDropValidatorStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        bool removed = tsdb.Documents.DropValidator(statement.CollectionName);
        return new RowsAffectedExecutionResult(statement.CollectionName, removed ? 1 : 0, "drop_document_validator");
    }

    public static InsertExecutionResult ExecuteInsert(Tsdb tsdb, InsertStatement statement, DocumentCollectionSchema schema)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(schema);

        int idColumn = FindRequiredColumn(statement.Columns, "id");
        int documentColumn = FindRequiredDocumentColumn(statement.Columns);
        var store = tsdb.Documents.Open(schema.Name);
        var requests = statement.Rows
            .Select(row => new DocumentWriteRequest(
                ConvertId(row[idColumn]),
                ConvertJson(row[documentColumn])))
            .ToArray();
        var result = store.InsertMany(requests, ordered: true);
        if (result.HasErrors)
            throw new InvalidOperationException(result.Errors.First(static error => error.Severity == DocumentWriteErrorSeverity.Error).Message);

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

        var projections = BuildProjections(statement.Projections);
        var store = tsdb.Documents.Open(schema.Name);
        var match = TryExtractMatch(schema, statement.Where, statement.Pagination);
        if (match is not null)
            match = ResolveFullTextMatch(store, match);

        var matchScores = match is null
            ? new Dictionary<string, double>(StringComparer.Ordinal)
            : match.Hits.ToDictionary(static hit => hit.DocumentId, static hit => hit.Score, StringComparer.Ordinal);

        if (statement.GroupBy.Count != 0 || statement.Having is not null || ContainsAggregate(projections))
        {
            var aggregateRows = LoadCandidateRows(store, schema, statement.Where, match);
            var aggregateResult = ExecuteAggregateProjection(statement, aggregateRows, projections, matchScores);
            return ApplyPagination(ApplyOrderBy(aggregateResult, statement.OrderBy), statement.Pagination);
        }

        IReadOnlyList<DocumentRow> rows;
        bool plannerAppliedOrderByAndPagination = false;
        if (match is null
            && TryBuildDocumentFilter(statement.Where, out var filter)
            && TryBuildDocumentSort(projections, statement.OrderBy, out var sort))
        {
            var queryResult = DocumentQueryPlanner.Execute(
                store,
                schema,
                new DocumentQuery(
                    Filter: filter,
                    Sort: sort,
                    Limit: statement.Pagination?.Fetch,
                    Skip: statement.Pagination?.Offset ?? 0));
            rows = queryResult.Items
                .Select(static item => new DocumentRow(item.Id, item.Json, item.Version))
                .ToArray();
            plannerAppliedOrderByAndPagination = true;
        }
        else
        {
            rows = LoadCandidateRows(store, schema, statement.Where, match);
        }

        var result = new SelectExecutionResult(
            projections.Select(static p => p.ColumnName).ToArray(),
            ProjectRows(rows, projections, statement.Where, matchScores));
        return plannerAppliedOrderByAndPagination
            ? result
            : ApplyPagination(ApplyOrderBy(result, statement.OrderBy), statement.Pagination);
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

            var result = store.Replace(row.Id, ConvertJson(statement.Assignments[0].Value));
            if (result.HasErrors)
                throw new InvalidOperationException(result.Errors[0].Message);
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
                (long)store.Count(),
                (long)schema.Indexes.Count,
                string.Join(",", schema.Indexes.Select(FormatIndexSummary)),
                (long)schema.FullTextIndexes.Count,
                string.Join(",", schema.FullTextIndexes.Select(static i => $"{i.Name}:{string.Join("|", i.Fields)}:{i.Tokenizer}")),
                schema.Validator is not null,
                schema.Validator?.Action == DocumentValidationAction.Warn ? "warn" : schema.Validator is null ? null : "error",
                schema.Validator is null ? null : string.Join(",", schema.Validator.Rules.Select(static r => r.Path)),
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
                string.Join(",", index.Paths),
                index.IsUnique,
                index.IsSparse,
                index.PartialFilter is not null,
                FormatPartialFilter(index.PartialFilter),
                index.IsTtl,
                index.TtlSeconds,
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

        if (TryChoosePathIndex(schema, where, out var index, out var values))
            return ("document_index", index.Name, store.CountByIndex(index, values));

        return ("document_scan", null, store.Count());
    }

    public static DocumentQueryPlan ExplainPlan(
        Tsdb tsdb,
        DocumentCollectionSchema schema,
        SelectStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(statement);

        var store = tsdb.Documents.Open(schema.Name);
        if (TryExtractMatch(schema, statement.Where, statement.Pagination) is { } match)
        {
            match = ResolveFullTextMatch(store, match);
            return BuildFullTextExplainPlan(match, HasAdditionalFullTextResidual(statement.Where));
        }

        var projections = BuildProjections(statement.Projections);
        if (TryBuildDocumentFilter(statement.Where, out var filter)
            && TryBuildDocumentSort(projections, statement.OrderBy, out var sort))
        {
            return DocumentQueryPlanner.Explain(
                store,
                schema,
                new DocumentQuery(
                    Filter: filter,
                    Projection: TryBuildDocumentProjection(projections),
                    Sort: sort,
                    Limit: statement.Pagination?.Fetch,
                    Skip: statement.Pagination?.Offset ?? 0));
        }

        var (accessPath, indexName, estimatedRows) = ExplainAccess(tsdb, schema, statement.Where);
        return new DocumentQueryPlan(
            accessPath,
            indexName,
            estimatedRows,
            estimatedRows,
            FilterPushdown: indexName is not null,
            FilterPushdownFields: indexName is null ? Array.Empty<string>() : [indexName],
            ResidualFilterFields: statement.Where is null ? Array.Empty<string>() : ["where"],
            SortUsesIndex: statement.OrderBy is null && accessPath is ("document_id" or "document_scan"),
            ProjectionCoveredByIndex: false,
            Candidates:
            [
                new DocumentQueryPlanCandidate(
                    accessPath,
                    indexName,
                    estimatedRows,
                    estimatedRows,
                    Selected: true,
                    indexName is null ? Array.Empty<string>() : [indexName],
                    RejectReason: null),
            ],
            GapReason: "sql_expression_not_supported_by_shared_document_planner");
    }

    private static DocumentQueryPlan BuildFullTextExplainPlan(FullTextMatch match, bool hasResidualFilter)
    {
        var candidate = new DocumentQueryPlanCandidate(
            "fulltext_index",
            match.Index.Name,
            match.Hits.Count,
            match.Hits.Count,
            Selected: true,
            [match.Index.Name],
            RejectReason: null);
        return new DocumentQueryPlan(
            "fulltext_index",
            match.Index.Name,
            match.Hits.Count,
            match.Hits.Count,
            FilterPushdown: true,
            FilterPushdownFields: [match.Index.Name],
            ResidualFilterFields: hasResidualFilter ? ["where"] : Array.Empty<string>(),
            SortUsesIndex: false,
            ProjectionCoveredByIndex: false,
            Candidates: [candidate],
            GapReason: null);
    }

    private static DocumentValidatorDefinition ParseValidatorDefinition(string json, string? validationActionOverride)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("document validator 必须是 JSON object。");

        string action = validationActionOverride
            ?? (root.TryGetProperty("validationAction", out var actionElement) && actionElement.ValueKind == JsonValueKind.String
                ? actionElement.GetString()!
                : "error");

        if (!root.TryGetProperty("rules", out var rulesElement) || rulesElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("document validator 需要 rules 数组。");

        var rules = new List<DocumentValidatorRuleDefinition>();
        foreach (var ruleElement in rulesElement.EnumerateArray())
        {
            if (ruleElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("document validator rule 必须是 JSON object。");
            if (!ruleElement.TryGetProperty("path", out var pathElement) || pathElement.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException("document validator rule 需要 string path。");

            bool required = ruleElement.TryGetProperty("required", out var requiredElement)
                && requiredElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                && requiredElement.GetBoolean();
            var types = ReadValidatorTypes(ruleElement);
            double? minimum = ReadOptionalDouble(ruleElement, "minimum");
            double? maximum = ReadOptionalDouble(ruleElement, "maximum");
            var enumValues = ReadEnumValues(ruleElement);
            string? pattern = ruleElement.TryGetProperty("pattern", out var patternElement) && patternElement.ValueKind == JsonValueKind.String
                ? patternElement.GetString()
                : null;

            rules.Add(new DocumentValidatorRuleDefinition(
                pathElement.GetString()!,
                required,
                types,
                minimum,
                maximum,
                enumValues,
                pattern));
        }

        return new DocumentValidatorDefinition(rules, ParseValidationAction(action));
    }

    private static IReadOnlyList<DocumentValidatorValueType> ReadValidatorTypes(JsonElement ruleElement)
    {
        var types = new List<DocumentValidatorValueType>();
        if (ruleElement.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String)
            types.Add(ParseValidatorValueType(typeElement.GetString()!));
        if (ruleElement.TryGetProperty("types", out var typesElement) && typesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in typesElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException("document validator rule types 必须是 string 数组。");
                types.Add(ParseValidatorValueType(item.GetString()!));
            }
        }

        return types;
    }

    private static IReadOnlyList<string> ReadEnumValues(JsonElement ruleElement)
    {
        if (!ruleElement.TryGetProperty("enum", out var enumElement) || enumElement.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();

        return enumElement.EnumerateArray()
            .Select(static item => DocumentValidatorExecutor.ToComparableJson(item))
            .ToArray();
    }

    private static double? ReadOptionalDouble(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind == JsonValueKind.Null)
            return null;
        if (property.ValueKind != JsonValueKind.Number)
            throw new InvalidOperationException($"document validator rule {propertyName} 必须是 number。");
        return property.GetDouble();
    }

    private static DocumentValidationAction ParseValidationAction(string action)
        => action.ToLowerInvariant() switch
        {
            "error" => DocumentValidationAction.Error,
            "warn" => DocumentValidationAction.Warn,
            _ => throw new InvalidOperationException($"不支持的 validationAction '{action}'。"),
        };

    private static DocumentValidatorValueType ParseValidatorValueType(string type)
        => type.ToLowerInvariant() switch
        {
            "string" => DocumentValidatorValueType.String,
            "number" => DocumentValidatorValueType.Number,
            "integer" or "int" => DocumentValidatorValueType.Integer,
            "boolean" or "bool" => DocumentValidatorValueType.Boolean,
            "object" => DocumentValidatorValueType.Object,
            "array" => DocumentValidatorValueType.Array,
            "null" => DocumentValidatorValueType.Null,
            _ => throw new InvalidOperationException($"不支持的 validator type '{type}'。"),
        };

    private static bool HasAdditionalFullTextResidual(SqlExpression? where)
        => where is not null
           && FlattenAnd(where).Any(static leaf => !ContainsMatchFunction(leaf));

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

        if (TryChoosePathIndex(schema, where, out var index, out var values))
            return store.GetByIndex(index, values);

        return store.Scan();
    }

    private static List<IReadOnlyList<object?>> ProjectRows(
        IReadOnlyList<DocumentRow> rows,
        Projection[] projections,
        SqlExpression? where,
        IReadOnlyDictionary<string, double> matchScores)
    {
        var filtered = new List<IReadOnlyList<object?>>();
        foreach (var row in rows)
        {
            if (!EvaluateWhere(where, row, matchScores))
                continue;

            var output = new object?[projections.Length];
            for (int i = 0; i < projections.Length; i++)
                output[i] = EvaluateProjection(projections[i], row, matchScores);
            filtered.Add(output);
        }

        return filtered;
    }

    private static SelectExecutionResult ExecuteAggregateProjection(
        SelectStatement statement,
        IReadOnlyList<DocumentRow> rows,
        Projection[] projections,
        IReadOnlyDictionary<string, double> matchScores)
    {
        ValidateAggregateProjection(statement, projections);

        var filteredRows = rows
            .Where(row => EvaluateWhere(statement.Where, row, matchScores))
            .ToArray();
        var groups = new Dictionary<DocumentGroupKey, List<DocumentRow>>();
        foreach (var row in filteredRows)
        {
            var keyValues = statement.GroupBy
                .Select(group => EvaluateScalar(group, row, matchScores))
                .ToArray();
            var key = new DocumentGroupKey(keyValues);
            if (!groups.TryGetValue(key, out var bucket))
            {
                bucket = new List<DocumentRow>();
                groups.Add(key, bucket);
            }

            bucket.Add(row);
        }

        if (groups.Count == 0 && statement.GroupBy.Count == 0)
            groups.Add(new DocumentGroupKey([]), []);

        var resultRows = new List<IReadOnlyList<object?>>(groups.Count);
        foreach (var group in groups.Values)
        {
            var representative = group.Count == 0
                ? new DocumentRow(string.Empty, "{}", Version: 0)
                : group[0];

            if (statement.Having is not null
                && !EvaluateHavingPredicate(statement.Having, representative, group, matchScores))
            {
                continue;
            }

            var output = new object?[projections.Length];
            for (int i = 0; i < projections.Length; i++)
            {
                output[i] = projections[i].Expression is FunctionCallExpression function
                    && IsAggregateFunction(function.Name)
                        ? EvaluateAggregate(function, group, matchScores)
                        : EvaluateScalar(projections[i].Expression, representative, matchScores);
            }

            resultRows.Add(output);
        }

        return new SelectExecutionResult(
            projections.Select(static projection => projection.ColumnName).ToArray(),
            resultRows);
    }

    private static void ValidateAggregateProjection(SelectStatement statement, Projection[] projections)
    {
        foreach (var projection in projections)
        {
            if (projection.Expression is FunctionCallExpression function && IsAggregateFunction(function.Name))
                continue;
            if (statement.GroupBy.Any(group => ExpressionEquals(group, projection.Expression)))
                continue;

            throw new InvalidOperationException("文档集合聚合 SELECT 中的非聚合投影必须出现在 GROUP BY 中。");
        }
    }

    private static bool EvaluateHavingPredicate(
        SqlExpression expression,
        DocumentRow representative,
        IReadOnlyList<DocumentRow> group,
        IReadOnlyDictionary<string, double> matchScores)
    {
        if (expression is BinaryExpression binary)
        {
            if (binary.Operator == SqlBinaryOperator.And)
                return EvaluateHavingPredicate(binary.Left, representative, group, matchScores)
                    && EvaluateHavingPredicate(binary.Right, representative, group, matchScores);
            if (binary.Operator == SqlBinaryOperator.Or)
                return EvaluateHavingPredicate(binary.Left, representative, group, matchScores)
                    || EvaluateHavingPredicate(binary.Right, representative, group, matchScores);
            if (IsComparisonOperator(binary.Operator))
            {
                var left = EvaluateHavingScalar(binary.Left, representative, group, matchScores);
                var right = EvaluateHavingScalar(binary.Right, representative, group, matchScores);
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
                    _ => throw new InvalidOperationException($"HAVING 不支持的比较运算符 {binary.Operator}。"),
                };
            }
        }
        else if (expression is UnaryExpression { Operator: SqlUnaryOperator.Not } unary)
        {
            return !EvaluateHavingPredicate(unary.Operand, representative, group, matchScores);
        }
        else if (expression is IsNullExpression isNull)
        {
            var isNullValue = EvaluateHavingScalar(isNull.Operand, representative, group, matchScores) is null;
            return isNull.Negated ? !isNullValue : isNullValue;
        }

        var value = EvaluateHavingScalar(expression, representative, group, matchScores);
        if (value is bool b)
            return b;
        throw new InvalidOperationException("HAVING 表达式必须计算为布尔值。");
    }

    private static object? EvaluateHavingScalar(
        SqlExpression expression,
        DocumentRow representative,
        IReadOnlyList<DocumentRow> group,
        IReadOnlyDictionary<string, double> matchScores)
    {
        var inlined = InlineAggregates(expression, group, matchScores);
        return EvaluateScalar(inlined, representative, matchScores);
    }

    private static SqlExpression InlineAggregates(
        SqlExpression expression,
        IReadOnlyList<DocumentRow> group,
        IReadOnlyDictionary<string, double> matchScores)
    {
        return expression switch
        {
            FunctionCallExpression function when IsAggregateFunction(function.Name) =>
                LiteralFromValue(EvaluateAggregate(function, group, matchScores)),
            BinaryExpression binary => new BinaryExpression(
                binary.Operator,
                InlineAggregates(binary.Left, group, matchScores),
                InlineAggregates(binary.Right, group, matchScores)),
            UnaryExpression unary => new UnaryExpression(
                unary.Operator,
                InlineAggregates(unary.Operand, group, matchScores)),
            FunctionCallExpression function when !function.IsStar => function with
            {
                Arguments = function.Arguments.Select(argument => InlineAggregates(argument, group, matchScores)).ToArray(),
            },
            _ => expression,
        };
    }

    private static object? EvaluateAggregate(
        FunctionCallExpression function,
        IReadOnlyList<DocumentRow> group,
        IReadOnlyDictionary<string, double> matchScores)
    {
        string name = function.Name.ToLowerInvariant();
        if (name == "count")
        {
            if (function.IsStar || function.Arguments.Count == 0)
                return (long)group.Count;
            RequireArgumentCount(function, 1);
            return group.LongCount(row => EvaluateScalar(function.Arguments[0], row, matchScores) is not null);
        }

        RequireArgumentCount(function, 1);
        var values = group
            .Select(row => EvaluateScalar(function.Arguments[0], row, matchScores))
            .Where(static value => value is not null)
            .ToArray();

        return name switch
        {
            "sum" => SumValues(values),
            "avg" => values.Length == 0 ? null : values.Average(ToDouble),
            "min" => values.Length == 0 ? null : values.Min(ScalarComparer.Instance),
            "max" => values.Length == 0 ? null : values.Max(ScalarComparer.Instance),
            "first" => values.Length == 0 ? null : values[0],
            "last" => values.Length == 0 ? null : values[^1],
            _ => throw new InvalidOperationException($"文档集合不支持聚合函数 '{function.Name}'。"),
        };
    }

    private static object SumValues(IReadOnlyList<object?> values)
    {
        bool allIntegral = true;
        double sum = 0;
        foreach (var value in values)
        {
            if (value is null)
                continue;
            if (!IsNumeric(value))
                throw new InvalidOperationException("sum 聚合函数只能用于数值表达式。");
            allIntegral &= value is byte or sbyte or short or ushort or int or uint or long;
            sum += Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }

        return allIntegral ? (long)sum : sum;
    }

    private static double ToDouble(object? value)
    {
        if (value is null)
            return 0;
        if (!IsNumeric(value))
            throw new InvalidOperationException("avg 聚合函数只能用于数值表达式。");
        return Convert.ToDouble(value, CultureInfo.InvariantCulture);
    }

    private static LiteralExpression LiteralFromValue(object? value)
        => value switch
        {
            null => LiteralExpression.Null(),
            bool b => LiteralExpression.Bool(b),
            byte or sbyte or short or ushort or int or uint or long => LiteralExpression.Integer(
                Convert.ToInt64(value, CultureInfo.InvariantCulture)),
            float or double or decimal => LiteralExpression.Float(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
            _ => LiteralExpression.String(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty),
        };

    private static bool ContainsAggregate(Projection[] projections)
        => projections.Any(static projection =>
            projection.Expression is FunctionCallExpression function && IsAggregateFunction(function.Name));

    private static bool IsAggregateFunction(string name)
        => name.Equals("count", StringComparison.OrdinalIgnoreCase)
            || name.Equals("sum", StringComparison.OrdinalIgnoreCase)
            || name.Equals("avg", StringComparison.OrdinalIgnoreCase)
            || name.Equals("min", StringComparison.OrdinalIgnoreCase)
            || name.Equals("max", StringComparison.OrdinalIgnoreCase)
            || name.Equals("first", StringComparison.OrdinalIgnoreCase)
            || name.Equals("last", StringComparison.OrdinalIgnoreCase);

    private static void RequireArgumentCount(FunctionCallExpression function, int count)
    {
        if (function.IsStar || function.Arguments.Count != count)
            throw new InvalidOperationException($"{function.Name} 聚合函数需要 {count} 个参数。");
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

        if (function.IsStar || function.Arguments.Count is < 3 or > 5)
            throw new InvalidOperationException("match(...) 需要 3 到 5 个参数：match(index, field, query[, topK][, mode])。");

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
        FullTextSearchMode mode = FullTextSearchMode.Exact;

        if (function.Arguments.Count >= 4)
        {
            // 第 4 个参数可以是 topK（整数字面量）或 mode（字符串字面量）。
            if (function.Arguments[3] is LiteralExpression { Kind: SqlLiteralKind.Integer, IntegerValue: var literalTopK })
            {
                if (literalTopK <= 0 || literalTopK > int.MaxValue)
                    throw new InvalidOperationException("match 第 4 个参数 topK 必须是正整数且不超过 Int32.MaxValue。");
                topK = (int)literalTopK;

                if (function.Arguments.Count == 5)
                {
                    if (function.Arguments[4] is not LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var modeText })
                        throw new InvalidOperationException("match 第 5 个参数 mode 必须是字符串字面量（'exact' 或 'fuzzy'）。");
                    mode = ResolveSearchMode(modeText!);
                }
            }
            else if (function.Arguments[3] is LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var modeText2 })
            {
                if (function.Arguments.Count == 5)
                    throw new InvalidOperationException("match 第 4 个参数为 mode 时不能再传 topK；topK 应放在 mode 之前。");
                mode = ResolveSearchMode(modeText2!);
            }
            else
            {
                throw new InvalidOperationException("match 第 4 个参数必须是 topK 整数字面量或 mode 字符串字面量。");
            }
        }

        match = new FullTextMatch(index, field, queryText!, topK, mode, Hits: []);
        return true;
    }

    private static FullTextSearchMode ResolveSearchMode(string modeText)
    {
        return modeText.ToLowerInvariant() switch
        {
            "exact" => FullTextSearchMode.Exact,
            "fuzzy" or "typo" or "typo_tolerant" => FullTextSearchMode.Fuzzy,
            _ => throw new InvalidOperationException($"match 不支持的检索模式 '{modeText}'；可用：exact / fuzzy。"),
        };
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
        var hits = store.SearchFullText(match.Index, match.Field, match.QueryText, match.TopK, match.Mode);
        return match with { Hits = hits };
    }

    private sealed record FullTextMatch(
        DocumentFullTextIndex Index,
        string Field,
        string QueryText,
        int TopK,
        FullTextSearchMode Mode,
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
        out IReadOnlyList<object?> values)
    {
        index = null!;
        values = [];
        if (where is null || schema.Indexes.Count == 0)
            return false;

        var leaves = FlattenAnd(where).ToArray();
        var equalityByPath = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var leaf in leaves)
        {
            if (leaf is not BinaryExpression { Operator: SqlBinaryOperator.Equal } binary)
                continue;

            if (TryExtractJsonValueComparison(binary, out var path, out var literalValue))
                equalityByPath[path.Text] = literalValue;
        }

        foreach (var candidate in schema.Indexes.OrderByDescending(static i => i.Paths.Count))
        {
            if (!CanUsePartialIndex(candidate, leaves))
                continue;

            var candidateValues = new object?[candidate.Paths.Count];
            bool matched = true;
            for (int i = 0; i < candidate.Paths.Count; i++)
            {
                if (!equalityByPath.TryGetValue(candidate.Paths[i], out candidateValues[i]))
                {
                    matched = false;
                    break;
                }
            }

            if (!matched)
                continue;
            if (candidate.IsSparse && candidateValues.Any(static value => value is null))
                continue;

            index = candidate;
            values = candidateValues;
            return true;
        }

        return false;
    }

    private static bool CanUsePartialIndex(DocumentPathIndex index, IReadOnlyList<SqlExpression> leaves)
        => index.PartialFilter is null
           || leaves.Any(leaf => LeafSatisfiesPartialFilter(index.PartialFilter, leaf));

    private static bool LeafSatisfiesPartialFilter(DocumentIndexPartialFilter filter, SqlExpression leaf)
    {
        if (filter.Operator == DocumentIndexPartialFilterOperator.Exists
            && leaf is FunctionCallExpression existsFunction
            && TryBindExistsPartialFilter(existsFunction, out var existsPath, out var existsValue))
        {
            bool expected = filter.ValueScalar is null or "true";
            return string.Equals(existsPath.Text, filter.Path, StringComparison.Ordinal)
                   && existsValue == expected;
        }

        if (filter.Operator == DocumentIndexPartialFilterOperator.Exists
            && leaf is BinaryExpression comparison
            && TryExtractJsonValueComparison(comparison, out var comparisonPath, out _))
        {
            bool expected = filter.ValueScalar is null or "true";
            return expected
                   && ComparisonImpliesPathExists(comparison)
                   && string.Equals(comparisonPath.Text, filter.Path, StringComparison.Ordinal);
        }

        if (leaf is not BinaryExpression binary
            || !TryExtractJsonValueComparison(binary, out var path, out var value)
            || !string.Equals(path.Text, filter.Path, StringComparison.Ordinal)
            || !TryMapPartialFilterOperator(binary.Operator, out var mapped))
        {
            return false;
        }

        string? scalar = JsonPathEvaluator.ToIndexScalar(value);
        return mapped == filter.Operator
               && scalar is not null
               && string.Equals(scalar, filter.ValueScalar, StringComparison.Ordinal);
    }

    private static bool ComparisonImpliesPathExists(BinaryExpression binary)
    {
        if (!TryExtractJsonValueComparison(binary, out _, out var value))
            return false;

        return binary.Operator switch
        {
            SqlBinaryOperator.Equal => value is not null,
            SqlBinaryOperator.NotEqual => false,
            SqlBinaryOperator.GreaterThan
                or SqlBinaryOperator.GreaterThanOrEqual
                or SqlBinaryOperator.LessThan
                or SqlBinaryOperator.LessThanOrEqual => true,
            _ => false,
        };
    }

    private static bool TryMapPartialFilterOperator(
        SqlBinaryOperator source,
        out DocumentIndexPartialFilterOperator target)
    {
        switch (source)
        {
            case SqlBinaryOperator.Equal:
                target = DocumentIndexPartialFilterOperator.Equal;
                return true;
            case SqlBinaryOperator.GreaterThan:
                target = DocumentIndexPartialFilterOperator.GreaterThan;
                return true;
            case SqlBinaryOperator.GreaterThanOrEqual:
                target = DocumentIndexPartialFilterOperator.GreaterThanOrEqual;
                return true;
            case SqlBinaryOperator.LessThan:
                target = DocumentIndexPartialFilterOperator.LessThan;
                return true;
            case SqlBinaryOperator.LessThanOrEqual:
                target = DocumentIndexPartialFilterOperator.LessThanOrEqual;
                return true;
            default:
                target = default;
                return false;
        }
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

    private static DocumentIndexPartialFilter? BindPartialFilter(SqlExpression? expression)
    {
        if (expression is null)
            return null;

        if (expression is FunctionCallExpression existsFunction
            && TryBindExistsPartialFilter(existsFunction, out var existsPath, out var existsValue))
        {
            return new DocumentIndexPartialFilter(
                existsPath.Text,
                DocumentIndexPartialFilterOperator.Exists,
                existsValue ? "true" : "false");
        }

        if (expression is not BinaryExpression binary)
            throw new InvalidOperationException("文档 partial index WHERE 仅支持 json_value(document, '$.path') 与字面量比较。");

        if (!TryExtractJsonValueComparison(binary, out var path, out var value))
            throw new InvalidOperationException("文档 partial index WHERE 仅支持 json_value(document, '$.path') 与字面量比较。");

        return new DocumentIndexPartialFilter(
            path.Text,
            binary.Operator switch
            {
                SqlBinaryOperator.Equal => DocumentIndexPartialFilterOperator.Equal,
                SqlBinaryOperator.NotEqual => DocumentIndexPartialFilterOperator.NotEqual,
                SqlBinaryOperator.GreaterThan => DocumentIndexPartialFilterOperator.GreaterThan,
                SqlBinaryOperator.GreaterThanOrEqual => DocumentIndexPartialFilterOperator.GreaterThanOrEqual,
                SqlBinaryOperator.LessThan => DocumentIndexPartialFilterOperator.LessThan,
                SqlBinaryOperator.LessThanOrEqual => DocumentIndexPartialFilterOperator.LessThanOrEqual,
                _ => throw new InvalidOperationException("文档 partial index WHERE 仅支持 = / != / > / >= / < / <=。"),
            },
            JsonPathEvaluator.ToIndexScalar(value));
    }

    private static bool TryBindExistsPartialFilter(
        FunctionCallExpression function,
        out JsonPath path,
        out bool exists)
    {
        path = null!;
        exists = true;
        if (!string.Equals(function.Name, "exists", StringComparison.OrdinalIgnoreCase)
            || function.IsStar
            || function.Arguments.Count != 1
            || !TryBindJsonValue(function.Arguments[0], out path))
        {
            return false;
        }

        return true;
    }

    private static string FormatIndexSummary(DocumentPathIndex index)
    {
        var flags = new List<string>(4);
        if (index.IsUnique)
            flags.Add("unique");
        if (index.IsSparse)
            flags.Add("sparse");
        if (index.PartialFilter is not null)
            flags.Add("partial");
        if (index.IsTtl)
            flags.Add($"ttl={index.TtlSeconds}");

        string flagText = flags.Count == 0 ? string.Empty : $"[{string.Join("|", flags)}]";
        return $"{index.Name}:{string.Join("|", index.Paths)}{flagText}";
    }

    private static string? FormatPartialFilter(DocumentIndexPartialFilter? filter)
    {
        if (filter is null)
            return null;

        string op = filter.Operator switch
        {
            DocumentIndexPartialFilterOperator.Exists => "exists",
            DocumentIndexPartialFilterOperator.Equal => "=",
            DocumentIndexPartialFilterOperator.NotEqual => "!=",
            DocumentIndexPartialFilterOperator.GreaterThan => ">",
            DocumentIndexPartialFilterOperator.GreaterThanOrEqual => ">=",
            DocumentIndexPartialFilterOperator.LessThan => "<",
            DocumentIndexPartialFilterOperator.LessThanOrEqual => "<=",
            _ => filter.Operator.ToString(),
        };
        return filter.Operator == DocumentIndexPartialFilterOperator.Exists
            ? $"{filter.Path} exists {filter.ValueScalar ?? "true"}"
            : $"{filter.Path} {op} {filter.ValueScalar}";
    }

    private static bool TryBuildDocumentFilter(SqlExpression? expression, out DocumentFilter? filter)
    {
        filter = null;
        if (expression is null)
            return true;

        switch (expression)
        {
            case BinaryExpression binary:
                if (binary.Operator == SqlBinaryOperator.And)
                {
                    if (!TryBuildDocumentFilter(binary.Left, out var left)
                        || !TryBuildDocumentFilter(binary.Right, out var right)
                        || left is null
                        || right is null)
                    {
                        return false;
                    }

                    filter = MergeAnd(left, right);
                    return true;
                }

                if (binary.Operator == SqlBinaryOperator.Or)
                {
                    if (!TryBuildDocumentFilter(binary.Left, out var left)
                        || !TryBuildDocumentFilter(binary.Right, out var right)
                        || left is null
                        || right is null)
                    {
                        return false;
                    }

                    filter = MergeOr(left, right);
                    return true;
                }

                return TryBuildFieldFilter(binary, out filter);

            case UnaryExpression { Operator: SqlUnaryOperator.Not } unary:
                if (!TryBuildDocumentFilter(unary.Operand, out var child) || child is null)
                    return false;
                filter = new DocumentNotFilter(child);
                return true;

            default:
                return false;
        }
    }

    private static bool TryBuildFieldFilter(BinaryExpression binary, out DocumentFilter? filter)
    {
        filter = null;
        if (!TryMapDocumentOperator(binary.Operator, out var op))
            return false;

        if (TryBindDocumentField(binary.Left, out var leftField)
            && TryEvaluateLiteral(binary.Right, out var rightValue))
        {
            filter = new DocumentFieldFilter(leftField, op, rightValue);
            return true;
        }

        if (TryBindDocumentField(binary.Right, out var rightField)
            && TryEvaluateLiteral(binary.Left, out var leftValue)
            && TryFlipDocumentOperator(op, out var flipped))
        {
            filter = new DocumentFieldFilter(rightField, flipped, leftValue);
            return true;
        }

        return false;
    }

    private static bool TryBindDocumentField(SqlExpression expression, out DocumentFieldRef field)
    {
        field = null!;
        switch (expression)
        {
            case IdentifierExpression id when string.Equals(id.Name, "id", StringComparison.OrdinalIgnoreCase):
                field = DocumentFieldRef.Id;
                return true;

            case IdentifierExpression id when IsDocumentColumn(id.Name):
                field = DocumentFieldRef.Document;
                return true;

            case FunctionCallExpression function when TryBindJsonValue(function, out var path):
                field = DocumentFieldRef.JsonPath(path.Text);
                return true;

            default:
                return false;
        }
    }

    private static bool TryMapDocumentOperator(SqlBinaryOperator op, out DocumentFilterOperator mapped)
    {
        switch (op)
        {
            case SqlBinaryOperator.Equal:
                mapped = DocumentFilterOperator.Equal;
                return true;
            case SqlBinaryOperator.NotEqual:
                mapped = DocumentFilterOperator.NotEqual;
                return true;
            case SqlBinaryOperator.LessThan:
                mapped = DocumentFilterOperator.LessThan;
                return true;
            case SqlBinaryOperator.LessThanOrEqual:
                mapped = DocumentFilterOperator.LessThanOrEqual;
                return true;
            case SqlBinaryOperator.GreaterThan:
                mapped = DocumentFilterOperator.GreaterThan;
                return true;
            case SqlBinaryOperator.GreaterThanOrEqual:
                mapped = DocumentFilterOperator.GreaterThanOrEqual;
                return true;
            default:
                mapped = default;
                return false;
        }
    }

    private static bool TryFlipDocumentOperator(DocumentFilterOperator op, out DocumentFilterOperator flipped)
    {
        switch (op)
        {
            case DocumentFilterOperator.Equal:
            case DocumentFilterOperator.NotEqual:
                flipped = op;
                return true;
            case DocumentFilterOperator.LessThan:
                flipped = DocumentFilterOperator.GreaterThan;
                return true;
            case DocumentFilterOperator.LessThanOrEqual:
                flipped = DocumentFilterOperator.GreaterThanOrEqual;
                return true;
            case DocumentFilterOperator.GreaterThan:
                flipped = DocumentFilterOperator.LessThan;
                return true;
            case DocumentFilterOperator.GreaterThanOrEqual:
                flipped = DocumentFilterOperator.LessThanOrEqual;
                return true;
            default:
                flipped = default;
                return false;
        }
    }

    private static DocumentFilter MergeAnd(DocumentFilter left, DocumentFilter right)
    {
        var filters = new List<DocumentFilter>();
        if (left is DocumentAndFilter leftAnd)
            filters.AddRange(leftAnd.Filters);
        else
            filters.Add(left);
        if (right is DocumentAndFilter rightAnd)
            filters.AddRange(rightAnd.Filters);
        else
            filters.Add(right);
        return new DocumentAndFilter(filters);
    }

    private static DocumentFilter MergeOr(DocumentFilter left, DocumentFilter right)
    {
        var filters = new List<DocumentFilter>();
        if (left is DocumentOrFilter leftOr)
            filters.AddRange(leftOr.Filters);
        else
            filters.Add(left);
        if (right is DocumentOrFilter rightOr)
            filters.AddRange(rightOr.Filters);
        else
            filters.Add(right);
        return new DocumentOrFilter(filters);
    }

    private static bool TryBuildDocumentSort(
        Projection[] projections,
        OrderBySpec? orderBy,
        out IReadOnlyList<DocumentSort> sort)
    {
        sort = Array.Empty<DocumentSort>();
        if (orderBy is null)
            return true;

        if (orderBy.Expression is not IdentifierExpression { Name: var name })
            return false;

        foreach (var projection in projections)
        {
            if (!string.Equals(projection.ColumnName, name, StringComparison.Ordinal))
                continue;

            if (!TryBindDocumentField(projection.Expression, out var field))
                return false;

            sort = new[] { new DocumentSort(field, orderBy.Direction == SortDirection.Descending) };
            return true;
        }

        return false;
    }

    private static DocumentProjection? TryBuildDocumentProjection(Projection[] projections)
    {
        var fields = new List<DocumentProjectionField>(projections.Length);
        foreach (var projection in projections)
        {
            if (!TryBindDocumentField(projection.Expression, out var field))
                return null;

            fields.Add(new DocumentProjectionField(projection.ColumnName, field));
        }

        return fields.Count == 0 ? null : new DocumentProjection(fields);
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

            // 文档模型沿用 Mongo 风格的 = NULL 空值等值语义；IS [NOT] NULL 走独立节点。
            case IsNullExpression isNull:
                var isNullValue = EvaluateScalar(isNull.Operand, row, matchScores) is null;
                return isNull.Negated ? !isNullValue : isNullValue;
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

        foreach (var groupBy in statement.GroupBy)
        {
            foreach (var identifier in EnumerateIdentifierReferences(groupBy))
                yield return identifier;
        }

        if (statement.Having is not null)
        {
            foreach (var identifier in EnumerateIdentifierReferences(statement.Having))
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

            case IsNullExpression isNull:
                foreach (var identifier in EnumerateIdentifierReferences(isNull.Operand))
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

            case IsNullExpression isNull:
                return ContainsMatchFunction(isNull.Operand);

            case BinaryExpression binary:
                return ContainsMatchFunction(binary.Left) || ContainsMatchFunction(binary.Right);

            default:
                return false;
        }
    }

    private static bool ExpressionEquals(SqlExpression left, SqlExpression right)
    {
        if (ReferenceEquals(left, right))
            return true;
        if (left.GetType() != right.GetType())
            return false;

        return (left, right) switch
        {
            (IdentifierExpression l, IdentifierExpression r) =>
                string.Equals(l.Name, r.Name, StringComparison.OrdinalIgnoreCase)
                && string.Equals(l.Qualifier, r.Qualifier, StringComparison.OrdinalIgnoreCase),
            (LiteralExpression l, LiteralExpression r) => ValuesEqual(EvaluateLiteral(l), EvaluateLiteral(r)),
            (FunctionCallExpression l, FunctionCallExpression r) =>
                string.Equals(l.Name, r.Name, StringComparison.OrdinalIgnoreCase)
                && l.IsStar == r.IsStar
                && l.Arguments.Count == r.Arguments.Count
                && l.Arguments.Zip(r.Arguments).All(pair => ExpressionEquals(pair.First, pair.Second)),
            (UnaryExpression l, UnaryExpression r) =>
                l.Operator == r.Operator && ExpressionEquals(l.Operand, r.Operand),
            (IsNullExpression l, IsNullExpression r) =>
                l.Negated == r.Negated && ExpressionEquals(l.Operand, r.Operand),
            (BinaryExpression l, BinaryExpression r) =>
                l.Operator == r.Operator
                && ExpressionEquals(l.Left, r.Left)
                && ExpressionEquals(l.Right, r.Right),
            (StarExpression, StarExpression) => true,
            _ => Equals(left, right),
        };
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

    private sealed class DocumentGroupKey : IEquatable<DocumentGroupKey>
    {
        private readonly object?[] _values;

        public DocumentGroupKey(object?[] values)
        {
            _values = values;
        }

        public bool Equals(DocumentGroupKey? other)
        {
            if (other is null || other._values.Length != _values.Length)
                return false;

            for (int i = 0; i < _values.Length; i++)
            {
                if (!ValuesEqual(_values[i], other._values[i]))
                    return false;
            }

            return true;
        }

        public override bool Equals(object? obj) => Equals(obj as DocumentGroupKey);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var value in _values)
            {
                if (value is null)
                {
                    hash.Add(0);
                }
                else if (IsNumeric(value))
                {
                    hash.Add(Convert.ToDouble(value, CultureInfo.InvariantCulture));
                }
                else
                {
                    hash.Add(value);
                }
            }

            return hash.ToHashCode();
        }
    }

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
