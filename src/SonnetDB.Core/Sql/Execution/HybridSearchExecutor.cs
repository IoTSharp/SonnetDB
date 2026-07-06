using System.Globalization;
using System.Text.Json;
using SonnetDB.Catalog;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.FullText;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Sql.Ast;
using SonnetDB.Storage.Format;
using SonnetDB.Tables;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// MM8 文档集合 Hybrid Search 执行器。
/// </summary>
internal static class HybridSearchExecutor
{
    private const string FunctionName = "hybrid_search";
    private const int DefaultK = 20;
    private const int DefaultTextCandidateMultiplier = 8;

    public static bool IsHybridSearch(SelectStatement statement)
        => statement.TableValuedFunction is { Name: var name }
            && string.Equals(name, FunctionName, StringComparison.OrdinalIgnoreCase);

    public static SelectExecutionResult Execute(Tsdb tsdb, SelectStatement statement)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);

        var call = statement.TableValuedFunction
            ?? throw new InvalidOperationException("hybrid_search 只能出现在 FROM 表值函数中。");
        if (statement.GroupBy.Count != 0)
            throw new InvalidOperationException("hybrid_search(...) 暂不支持 GROUP BY。");

        var documentSchema = tsdb.Documents.Catalog.TryGet(statement.Measurement);
        if (documentSchema is not null)
            return ExecuteDocumentCollection(tsdb, statement, call, documentSchema);

        var measurementSchema = tsdb.Measurements.TryGet(statement.Measurement)
            ?? throw new InvalidOperationException(
                $"hybrid_search(...) 的 source '{statement.Measurement}' 必须是 measurement 或 document collection。");
        return ExecuteMeasurementKnowledge(tsdb, statement, call, measurementSchema);
    }

    private static SelectExecutionResult ExecuteDocumentCollection(
        Tsdb tsdb,
        SelectStatement statement,
        FunctionCallExpression call,
        DocumentCollectionSchema schema)
    {
        var options = BindDocumentOptions(schema, call, statement.Pagination);
        var store = tsdb.Documents.Open(schema.Name);
        var projections = BuildDocumentProjections(statement.Projections);
        var rows = ScoreRows(store, options);
        rows = ApplyWhere(rows, statement.Where);
        rows = ApplyOrderBy(rows, statement.OrderBy, projections);
        rows = rows.Take(options.K).ToList();
        rows = ApplyPagination(rows, statement.Pagination);

        var resultRows = new List<IReadOnlyList<object?>>(rows.Count);
        foreach (var row in rows)
        {
            var output = new object?[projections.Length];
            for (int i = 0; i < projections.Length; i++)
                output[i] = EvaluateScalar(projections[i].Expression, row);
            resultRows.Add(output);
        }

        return new SelectExecutionResult(
            projections.Select(static p => p.ColumnName).ToArray(),
            resultRows);
    }

    private static SelectExecutionResult ExecuteMeasurementKnowledge(
        Tsdb tsdb,
        SelectStatement statement,
        FunctionCallExpression call,
        MeasurementSchema schema)
    {
        var options = BindMeasurementKnowledgeOptions(tsdb, schema, call, statement.Pagination);
        var relationPlan = BindRelationPlan(tsdb, statement, schema, options);
        var projections = BuildKnowledgeProjections(statement.Projections, schema, relationPlan);
        var filterPlan = PlanKnowledgeFilters(statement.Where, schema, relationPlan);
        var rows = ScoreMeasurementKnowledgeRows(tsdb, schema, options, filterPlan, relationPlan);
        rows = ApplyKnowledgeWhere(rows, filterPlan.ResidualExpression);
        rows = ApplyOrderBy(rows, statement.OrderBy, projections);
        rows = rows.Take(options.K).ToList();
        rows = ApplyPagination(rows, statement.Pagination);

        var resultRows = new List<IReadOnlyList<object?>>(rows.Count);
        foreach (var row in rows)
        {
            var output = new object?[projections.Length];
            for (int i = 0; i < projections.Length; i++)
                output[i] = EvaluateScalar(projections[i].Expression, row);
            resultRows.Add(output);
        }

        return new SelectExecutionResult(
            projections.Select(static p => p.ColumnName).ToArray(),
            resultRows);
    }

    public static (string AccessPath, string? IndexName, int EstimatedRows) ExplainAccess(
        Tsdb tsdb,
        SelectStatement statement,
        DocumentCollectionSchema schema)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(schema);

        var call = statement.TableValuedFunction
            ?? throw new InvalidOperationException("hybrid_search 只能出现在 FROM 表值函数中。");
        var options = BindDocumentOptions(schema, call, statement.Pagination);
        var store = tsdb.Documents.Open(schema.Name);
        int documentCount = store.Count();
        return ("hybrid_search", options.FullTextIndex.Name, documentCount);
    }

    public static (string AccessPath, string? IndexName, int EstimatedRows) ExplainAccess(
        Tsdb tsdb,
        SelectStatement statement,
        MeasurementSchema schema)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(schema);

        var call = statement.TableValuedFunction
            ?? throw new InvalidOperationException("hybrid_search 只能出现在 FROM 表值函数中。");
        var options = BindMeasurementKnowledgeOptions(tsdb, schema, call, statement.Pagination);
        var relationPlan = BindRelationPlan(tsdb, statement, schema, options);
        var filterPlan = PlanKnowledgeFilters(statement.Where, schema, relationPlan);
        int matchedSeries = tsdb.Catalog.Find(schema.Name, filterPlan.MeasurementWhere.TagFilter).Count;
        int documentCount = options.DocumentStore.Count();
        int relationCount = relationPlan?.RowsByJoinKey.Count ?? 0;
        string indexName = options.FullTextIndex is null
            ? options.VectorColumn.Name
            : $"{options.VectorColumn.Name};{options.FullTextIndex.Name}";
        if (relationPlan is not null)
            indexName = relationPlan.IndexName is null
                ? $"{indexName};{relationPlan.TableSchema.Name}"
                : $"{indexName};{relationPlan.IndexName}";

        string accessPath = relationPlan is null
            ? "hybrid_search_measurement_knn_documents"
            : $"hybrid_search_measurement_knn_documents;relation_filter:{relationPlan.AccessPath}";
        return (accessPath, indexName, matchedSeries + documentCount + relationCount);
    }

    private static IReadOnlyList<KnowledgeHybridRow> ScoreMeasurementKnowledgeRows(
        Tsdb tsdb,
        MeasurementSchema schema,
        MeasurementKnowledgeOptions options,
        CrossModelFilterPlan filterPlan,
        RelationFilterPlan? relationPlan)
    {
        var matchedSeries = tsdb.Catalog.Find(schema.Name, filterPlan.MeasurementWhere.TagFilter).ToList();
        if (relationPlan is not null)
            matchedSeries = matchedSeries
                .Where(series => series.Tags.TryGetValue(options.MeasurementJoinTag.Name, out var joinValue)
                    && relationPlan.RowsByJoinKey.ContainsKey(MakeRelationJoinKey(joinValue)))
                .ToList();
        if (matchedSeries.Count == 0)
            return [];

        var seriesById = new Dictionary<ulong, SeriesEntry>(matchedSeries.Count);
        foreach (var series in matchedSeries)
            seriesById[series.Id] = series;

        // 单次租约拿到 {MemTable(active+sealing) + 段读取器} 一致视图，避免跨越 flush 边界。
        IReadOnlyList<KnnSearchResult> knnResults;
        using (var readSnapshot = tsdb.AcquireReadSnapshot())
        {
            knnResults = KnnExecutor.Execute(
                readSnapshot.AllMemTables(),
                readSnapshot.Readers,
                matchedSeries,
                options.VectorColumn.Name,
                options.QueryVector.AsMemory(),
                options.MeasurementCandidateLimit,
                options.Metric,
                filterPlan.MeasurementWhere.TimeRange,
                tsdb.Tombstones);
        }
        if (knnResults.Count == 0)
            return [];

        var bm25ById = BuildBm25Scores(options);
        double maxBm25 = bm25ById.Count == 0 ? 0d : bm25ById.Values.Max();
        var rows = new List<KnowledgeHybridRow>();
        foreach (var hit in knnResults)
        {
            if (!seriesById.TryGetValue(hit.SeriesId, out var series))
                continue;
            if (!series.Tags.TryGetValue(options.MeasurementJoinTag.Name, out string? joinValue))
                continue;
            IReadOnlyList<TableRow> relationRows = [];
            if (relationPlan is not null)
            {
                if (!relationPlan.RowsByJoinKey.TryGetValue(MakeRelationJoinKey(joinValue), out var matchedRelationRows))
                    continue;
                relationRows = matchedRelationRows;
            }

            string joinPathScalar = JsonPathEvaluator.ToIndexScalar(joinValue)!;
            foreach (var document in GetAssociatedDocuments(options, joinValue, joinPathScalar))
            {
                bm25ById.TryGetValue(document.Id, out double bm25);
                double textScore = maxBm25 <= 0d ? 0d : bm25 / maxBm25;
                var (documentVectorDistance, documentVectorScore) = ScoreDocumentVector(document, options);
                double measurementScore = DistanceToScore(options.Metric, hit.Distance);
                double hybridScore =
                    (options.MeasurementWeight * measurementScore)
                    + (options.TextWeight * textScore)
                    + (options.DocumentVectorWeight * documentVectorScore);

                var measurementFields = BuildMeasurementFieldValues(tsdb, schema, hit);
                if (relationPlan is null)
                {
                    rows.Add(new KnowledgeHybridRow(
                        hit.Timestamp,
                        hit.Distance,
                        measurementScore,
                        series,
                        measurementFields,
                        document,
                        null,
                        null,
                        bm25,
                        textScore,
                        documentVectorDistance,
                        documentVectorScore,
                        hybridScore));
                    continue;
                }

                foreach (var relationRow in relationRows)
                {
                    rows.Add(new KnowledgeHybridRow(
                        hit.Timestamp,
                        hit.Distance,
                        measurementScore,
                        series,
                        measurementFields,
                        document,
                        relationPlan,
                        relationRow,
                        bm25,
                        textScore,
                        documentVectorDistance,
                        documentVectorScore,
                        hybridScore));
                }
            }
        }

        return rows;
    }

    private static Dictionary<string, double> BuildBm25Scores(MeasurementKnowledgeOptions options)
    {
        if (options.FullTextIndex is null || options.TextQuery is null)
            return new Dictionary<string, double>(StringComparer.Ordinal);

        var hits = options.DocumentStore.SearchFullText(
            options.FullTextIndex,
            options.TextField,
            options.TextQuery,
            options.TextCandidateLimit);
        return hits.ToDictionary(static h => h.DocumentId, static h => h.Score, StringComparer.Ordinal);
    }

    private static IReadOnlyList<DocumentRow> GetAssociatedDocuments(
        MeasurementKnowledgeOptions options,
        string joinValue,
        string joinPathScalar)
    {
        if (options.DocumentJoinIndex is not null)
            return options.DocumentStore.GetByIndex(options.DocumentJoinIndex, joinValue);

        var rows = new List<DocumentRow>();
        foreach (var row in options.DocumentStore.Scan())
        {
            var value = JsonPathEvaluator.Evaluate(row.Json, options.DocumentJoinPath);
            string? scalar = JsonPathEvaluator.ToIndexScalar(value);
            if (scalar is not null && string.Equals(scalar, joinPathScalar, StringComparison.Ordinal))
                rows.Add(row);
        }

        return rows;
    }

    private static RelationFilterPlan? BindRelationPlan(
        Tsdb tsdb,
        SelectStatement statement,
        MeasurementSchema schema,
        MeasurementKnowledgeOptions options)
    {
        if (statement.Join is null)
            return null;

        var join = statement.Join;
        var tableSchema = tsdb.Tables.Catalog.TryGet(join.TableName)
            ?? throw new InvalidOperationException(
                $"hybrid_search JOIN 右侧必须是关系表，table '{join.TableName}' 不存在。");
        var keys = ResolveRelationJoinKeys(join.On, schema, tableSchema, join, options.MeasurementJoinTag);
        var preliminaryPlan = new RelationFilterPlan(
            tableSchema,
            join.Alias,
            keys.TableColumn,
            new Dictionary<RelationJoinKey, List<TableRow>>(),
            "table_scan",
            null);
        var filterPlan = PlanKnowledgeFilters(statement.Where, schema, preliminaryPlan);
        var tableStore = tsdb.Tables.Open(tableSchema.Name);
        var tableCandidateRows = TableSqlExecutor.LoadCandidateRows(tableStore, tableSchema, filterPlan.TableExpression);
        var tableRows = tableCandidateRows
            .Where(row => TableSqlExecutor.EvaluateWhere(filterPlan.TableExpression, tableSchema, row.Values))
            .ToArray();
        var rowsByJoinKey = BuildRelationRowsByJoinKey(keys.TableColumn, tableRows);
        var (accessPath, indexName, _) = JoinSqlExecutor.ExplainTableAccess(tableStore, tableSchema, filterPlan.TableExpression);

        return new RelationFilterPlan(
            tableSchema,
            join.Alias,
            keys.TableColumn,
            rowsByJoinKey,
            accessPath,
            indexName is null ? null : $"{tableSchema.Name}.{indexName}");
    }

    private static RelationJoinKeys ResolveRelationJoinKeys(
        SqlExpression on,
        MeasurementSchema measurementSchema,
        TableSchema tableSchema,
        JoinClause join,
        MeasurementColumn expectedMeasurementJoinTag)
    {
        if (on is not BinaryExpression { Operator: SqlBinaryOperator.Equal } binary)
            throw new InvalidOperationException("hybrid_search JOIN 仅支持 ON 中一个等值条件。");

        if (binary.Left is not IdentifierExpression left || binary.Right is not IdentifierExpression right)
            throw new InvalidOperationException("hybrid_search JOIN ON 等值两侧必须都是列引用。");

        var leftSource = ResolveJoinIdentifierSource(left, measurementSchema, tableSchema, join);
        var rightSource = ResolveJoinIdentifierSource(right, measurementSchema, tableSchema, join);
        if (leftSource.Source == rightSource.Source)
            throw new InvalidOperationException("hybrid_search JOIN ON 等值条件必须连接 measurement 列和关系表列。");

        var measurementIdentifier = leftSource.Source == CrossModelFilterSource.Measurement ? left : right;
        var tableIdentifier = leftSource.Source == CrossModelFilterSource.Table ? left : right;

        var measurementColumn = measurementSchema.TryGetColumn(measurementIdentifier.Name)
            ?? throw new InvalidOperationException($"hybrid_search JOIN ON 引用了未知 measurement 列 '{measurementIdentifier.Name}'。");
        if (measurementColumn.Role != MeasurementColumnRole.Tag)
            throw new InvalidOperationException($"hybrid_search JOIN ON 的 measurement 侧连接键必须是 TAG 列。");
        if (!string.Equals(measurementColumn.Name, expectedMeasurementJoinTag.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"hybrid_search JOIN ON 的 measurement 侧连接键必须与 measurement_join_tag '{expectedMeasurementJoinTag.Name}' 一致。");
        }

        var tableColumn = tableSchema.TryGetColumn(tableIdentifier.Name)
            ?? throw new InvalidOperationException($"hybrid_search JOIN ON 引用了未知 table 列 '{tableIdentifier.Name}'。");
        return new RelationJoinKeys(measurementColumn, tableColumn);
    }

    private static (CrossModelFilterSource Source, MeasurementColumn? MeasurementColumn, TableColumn? TableColumn)
        ResolveJoinIdentifierSource(
            IdentifierExpression identifier,
            MeasurementSchema measurementSchema,
            TableSchema tableSchema,
            JoinClause join)
    {
        if (identifier.Qualifier is not null)
        {
            if (IsMeasurementQualifier(identifier.Qualifier, measurementSchema.Name))
            {
                var column = measurementSchema.TryGetColumn(identifier.Name)
                    ?? throw new InvalidOperationException($"hybrid_search JOIN 引用了未知 measurement 列 '{identifier.Name}'。");
                return (CrossModelFilterSource.Measurement, column, null);
            }

            if (string.Equals(identifier.Qualifier, join.Alias, StringComparison.OrdinalIgnoreCase)
                || string.Equals(identifier.Qualifier, tableSchema.Name, StringComparison.OrdinalIgnoreCase))
            {
                var column = tableSchema.TryGetColumn(identifier.Name)
                    ?? throw new InvalidOperationException($"hybrid_search JOIN 引用了未知 table 列 '{identifier.Name}'。");
                return (CrossModelFilterSource.Table, null, column);
            }

            throw new InvalidOperationException($"hybrid_search JOIN 引用了未知限定符 '{identifier.Qualifier}'。");
        }

        var measurementColumn = measurementSchema.TryGetColumn(identifier.Name);
        var tableColumn = tableSchema.TryGetColumn(identifier.Name);
        if (measurementColumn is not null && tableColumn is not null)
        {
            throw new InvalidOperationException(
                $"hybrid_search JOIN 中未限定列名 '{identifier.Name}' 同时存在于 measurement 和 table。");
        }

        if (measurementColumn is not null)
            return (CrossModelFilterSource.Measurement, measurementColumn, null);
        if (tableColumn is not null)
            return (CrossModelFilterSource.Table, null, tableColumn);
        throw new InvalidOperationException($"hybrid_search JOIN 引用了未知列 '{identifier.Name}'。");
    }

    private static Dictionary<RelationJoinKey, List<TableRow>> BuildRelationRowsByJoinKey(
        TableColumn joinColumn,
        IReadOnlyList<TableRow> rows)
    {
        var result = new Dictionary<RelationJoinKey, List<TableRow>>();
        foreach (var row in rows)
        {
            var value = row.Values[joinColumn.Ordinal];
            if (value is null)
                continue;

            var key = MakeRelationJoinKey(value);
            if (!result.TryGetValue(key, out var bucket))
            {
                bucket = [];
                result.Add(key, bucket);
            }

            bucket.Add(row);
        }

        return result;
    }

    private static (double? Distance, double Score) ScoreDocumentVector(
        DocumentRow row,
        MeasurementKnowledgeOptions options)
    {
        if (options.DocumentVectorPath is null)
            return (null, 0d);
        if (!TryReadVector(row, options.DocumentVectorPath, out var vector))
            return (null, 0d);
        if (vector.Length != options.QueryVector.Length)
        {
            throw new InvalidOperationException(
                $"hybrid_search 文档 '{row.Id}' 的知识向量维度 {vector.Length} 与查询向量维度 {options.QueryVector.Length} 不一致。");
        }

        double distance = VectorDistance.Compute(options.Metric, options.QueryVector, vector);
        return (distance, DistanceToScore(options.Metric, distance));
    }

    private static Dictionary<string, object?> BuildMeasurementFieldValues(
        Tsdb tsdb,
        MeasurementSchema schema,
        KnnSearchResult hit)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        var exactRange = new TimeRange(hit.Timestamp, hit.Timestamp);
        foreach (var column in schema.FieldColumns)
        {
            var points = tsdb.Query.Execute(new PointQuery(hit.SeriesId, column.Name, exactRange)).ToList();
            fields[column.Name] = points.Count > 0
                ? ConvertFieldValue(points[0].Value)
                : null;
        }

        return fields;
    }

    private static IReadOnlyList<HybridRow> ScoreRows(
        DocumentCollectionStore store,
        HybridSearchOptions options)
    {
        var fullTextHits = store.SearchFullText(
            options.FullTextIndex,
            options.TextField,
            options.TextQuery,
            options.TextCandidateLimit);
        var bm25ById = fullTextHits.ToDictionary(static h => h.DocumentId, static h => h.Score, StringComparer.Ordinal);
        double maxBm25 = bm25ById.Count == 0 ? 0d : bm25ById.Values.Max();

        var rows = new List<HybridRow>();
        foreach (var documentRow in store.Scan())
        {
            if (!TryReadVector(documentRow, options.VectorPath, out var vector))
                continue;
            if (vector.Length != options.QueryVector.Length)
            {
                throw new InvalidOperationException(
                    $"hybrid_search 文档 '{documentRow.Id}' 的向量维度 {vector.Length} 与查询向量维度 {options.QueryVector.Length} 不一致。");
            }

            double distance = VectorDistance.Compute(options.Metric, options.QueryVector, vector);
            double vectorScore = DistanceToScore(options.Metric, distance);
            bm25ById.TryGetValue(documentRow.Id, out double bm25);
            double textScore = maxBm25 <= 0d ? 0d : bm25 / maxBm25;
            double hybridScore = (options.TextWeight * textScore) + (options.VectorWeight * vectorScore);

            rows.Add(new HybridRow(
                documentRow,
                Bm25Score: bm25,
                VectorDistance: distance,
                VectorScore: vectorScore,
                HybridScore: hybridScore));
        }

        return rows;
    }

    private static List<HybridRow> ApplyWhere(
        IReadOnlyList<HybridRow> rows,
        SqlExpression? where)
    {
        if (where is null)
            return rows.ToList();

        var filtered = new List<HybridRow>(rows.Count);
        foreach (var row in rows)
        {
            if (EvaluateBoolean(where, row))
                filtered.Add(row);
        }

        return filtered;
    }

    private static List<HybridRow> ApplyOrderBy(
        IReadOnlyList<HybridRow> rows,
        OrderBySpec? orderBy,
        IReadOnlyList<Projection> projections)
    {
        if (orderBy is null)
        {
            return rows
                .OrderByDescending(static r => r.HybridScore)
                .ThenByDescending(static r => r.Bm25Score)
                .ThenBy(static r => r.VectorDistance)
                .ThenBy(static r => r.Document.Id, StringComparer.Ordinal)
                .ToList();
        }

        var expression = ResolveOrderByExpression(orderBy.Expression, projections);
        var ordered = orderBy.Direction == SortDirection.Descending
            ? rows.OrderByDescending(row => EvaluateScalar(expression, row), ScalarComparer.Instance)
            : rows.OrderBy(row => EvaluateScalar(expression, row), ScalarComparer.Instance);
        return ordered
            .ThenBy(static r => r.Document.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static SqlExpression ResolveOrderByExpression(
        SqlExpression orderBy,
        IReadOnlyList<Projection> projections)
    {
        if (orderBy is not IdentifierExpression { Qualifier: null, Name: var name })
            return orderBy;

        foreach (var projection in projections)
        {
            if (string.Equals(projection.ColumnName, name, StringComparison.OrdinalIgnoreCase))
                return projection.Expression;
        }

        return orderBy;
    }

    private static List<HybridRow> ApplyPagination(
        IReadOnlyList<HybridRow> rows,
        PaginationSpec? pagination)
    {
        if (pagination is null)
            return rows.ToList();

        if (pagination.Offset >= rows.Count)
            return [];

        int take = pagination.Fetch ?? (rows.Count - pagination.Offset);
        if (take <= 0)
            return [];

        return rows
            .Skip(pagination.Offset)
            .Take(Math.Min(take, rows.Count - pagination.Offset))
            .ToList();
    }

    private static List<KnowledgeHybridRow> ApplyKnowledgeWhere(
        IReadOnlyList<KnowledgeHybridRow> rows,
        SqlExpression? residualWhere)
    {
        if (residualWhere is null)
            return rows.ToList();

        var filtered = new List<KnowledgeHybridRow>(rows.Count);
        foreach (var row in rows)
        {
            if (EvaluateBoolean(residualWhere, row))
                filtered.Add(row);
        }

        return filtered;
    }

    private static List<KnowledgeHybridRow> ApplyOrderBy(
        IReadOnlyList<KnowledgeHybridRow> rows,
        OrderBySpec? orderBy,
        IReadOnlyList<Projection> projections)
    {
        if (orderBy is null)
        {
            return rows
                .OrderByDescending(static r => r.HybridScore)
                .ThenByDescending(static r => r.MeasurementScore)
                .ThenByDescending(static r => r.Bm25Score)
                .ThenByDescending(static r => r.DocumentVectorScore)
                .ThenBy(static r => r.MeasurementDistance)
                .ThenBy(static r => r.Document.Id, StringComparer.Ordinal)
                .ToList();
        }

        var expression = ResolveOrderByExpression(orderBy.Expression, projections);
        var ordered = orderBy.Direction == SortDirection.Descending
            ? rows.OrderByDescending(row => EvaluateScalar(expression, row), ScalarComparer.Instance)
            : rows.OrderBy(row => EvaluateScalar(expression, row), ScalarComparer.Instance);
        return ordered
            .ThenBy(static r => r.Document.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static List<KnowledgeHybridRow> ApplyPagination(
        IReadOnlyList<KnowledgeHybridRow> rows,
        PaginationSpec? pagination)
    {
        if (pagination is null)
            return rows.ToList();

        if (pagination.Offset >= rows.Count)
            return [];

        int take = pagination.Fetch ?? (rows.Count - pagination.Offset);
        if (take <= 0)
            return [];

        return rows
            .Skip(pagination.Offset)
            .Take(Math.Min(take, rows.Count - pagination.Offset))
            .ToList();
    }

    private static CrossModelFilterPlan PlanKnowledgeFilters(
        SqlExpression? where,
        MeasurementSchema schema,
        RelationFilterPlan? relationPlan)
        => CrossModelFilterPlanner.Plan(where, schema, leaf =>
        {
            var result = CrossModelFilterSource.None;
            foreach (var identifier in EnumerateIdentifiers(leaf))
                result |= ResolveKnowledgeFilterSource(identifier, schema, relationPlan);
            return result;
        });

    private static CrossModelFilterSource ResolveKnowledgeFilterSource(
        IdentifierExpression identifier,
        MeasurementSchema schema,
        RelationFilterPlan? relationPlan)
    {
        if (identifier.Qualifier is not null)
        {
            if (IsMeasurementQualifier(identifier.Qualifier, schema.Name))
                return CrossModelFilterSource.Measurement;
            if (IsDocumentQualifier(identifier.Qualifier))
                return CrossModelFilterSource.Document;
            if (relationPlan is not null && relationPlan.MatchesQualifier(identifier.Qualifier))
                return CrossModelFilterSource.Table;
            throw new InvalidOperationException(
                $"hybrid_search 查询引用了未知限定符 '{identifier.Qualifier}'。");
        }

        if (string.Equals(identifier.Name, "time", StringComparison.OrdinalIgnoreCase)
            || schema.TryGetColumn(identifier.Name) is not null)
        {
            return CrossModelFilterSource.Measurement;
        }

        if (relationPlan?.TableSchema.TryGetColumn(identifier.Name) is not null)
            return CrossModelFilterSource.Table;

        return CrossModelFilterSource.Document;
    }

    private static IEnumerable<IdentifierExpression> EnumerateIdentifiers(SqlExpression expression)
    {
        switch (expression)
        {
            case IdentifierExpression identifier:
                yield return identifier;
                yield break;
            case UnaryExpression unary:
                foreach (var identifier in EnumerateIdentifiers(unary.Operand))
                    yield return identifier;
                yield break;
            case BinaryExpression binary:
                foreach (var identifier in EnumerateIdentifiers(binary.Left))
                    yield return identifier;
                foreach (var identifier in EnumerateIdentifiers(binary.Right))
                    yield return identifier;
                yield break;
            case FunctionCallExpression function:
                foreach (var argument in function.Arguments)
                    foreach (var identifier in EnumerateIdentifiers(argument))
                        yield return identifier;
                yield break;
            case NamedArgumentExpression named:
                foreach (var identifier in EnumerateIdentifiers(named.Value))
                    yield return identifier;
                yield break;
        }
    }

    private static HybridSearchOptions BindDocumentOptions(
        DocumentCollectionSchema schema,
        FunctionCallExpression call,
        PaginationSpec? pagination)
    {
        if (call.IsStar)
            throw new InvalidOperationException("hybrid_search(*) 非法。");

        var args = BindArguments(call);
        var source = RequireIdentifierArgument(args, "source");
        if (!string.Equals(source, schema.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"hybrid_search source '{source}' 与解析出的 document collection '{schema.Name}' 不一致。");
        }

        string textQuery = RequireStringArgument(args, "text", "text_query");
        float[] queryVector = RequireVectorArgument(args, "vector", "query_vector");
        int k = GetPositiveIntArgument(args, DefaultK, "k", "top_k");
        string textField = GetFieldArgument(args, defaultValue: "*", "text_field", "field");
        string vectorField = NormalizeJsonPath(GetFieldArgument(args, defaultValue: "$.embedding", "vector_field", "embedding_field"));
        var fullTextIndex = ResolveFullTextIndex(schema, args);
        var metric = GetMetricArgument(args);
        double textWeight = GetNonNegativeDoubleArgument(args, 0.5d, "text_weight");
        double vectorWeight = GetNonNegativeDoubleArgument(args, 0.5d, "vector_weight");
        if (textWeight == 0d && vectorWeight == 0d)
            throw new InvalidOperationException("hybrid_search 的 text_weight 与 vector_weight 不能同时为 0。");

        int textCandidateLimit = Math.Max(k * DefaultTextCandidateMultiplier, DefaultFullTextTopK(pagination));
        return new HybridSearchOptions(
            fullTextIndex,
            textField,
            textQuery,
            JsonPath.Parse(vectorField),
            queryVector,
            k,
            textCandidateLimit,
            metric,
            textWeight,
            vectorWeight);
    }

    private static MeasurementKnowledgeOptions BindMeasurementKnowledgeOptions(
        Tsdb tsdb,
        MeasurementSchema schema,
        FunctionCallExpression call,
        PaginationSpec? pagination)
    {
        if (call.IsStar)
            throw new InvalidOperationException("hybrid_search(*) 非法。");
        if (!call.Arguments.Any(static arg => arg is NamedArgumentExpression))
        {
            throw new InvalidOperationException(
                "measurement 与 document collection 融合必须使用命名参数：source/documents/vector/measurement_join_tag/document_join_path。");
        }

        var args = BindArguments(call);
        var source = RequireIdentifierArgument(args, "source");
        if (!string.Equals(source, schema.Name, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"hybrid_search source '{source}' 与解析出的 measurement '{schema.Name}' 不一致。");
        }

        string documentsName = RequireIdentifierArgument(args, "documents", "document_collection", "knowledge", "knowledge_collection");
        var documentSchema = tsdb.Documents.Catalog.TryGet(documentsName)
            ?? throw new InvalidOperationException($"hybrid_search documents '{documentsName}' 必须是 document collection。");
        var documentStore = tsdb.Documents.Open(documentSchema.Name);

        string vectorColumnName = GetFieldArgument(args, "embedding", "vector_field", "measurement_vector_field", "column");
        var vectorColumn = schema.TryGetColumn(vectorColumnName)
            ?? throw new InvalidOperationException($"hybrid_search measurement '{schema.Name}' 中不存在向量列 '{vectorColumnName}'。");
        if (vectorColumn.Role != MeasurementColumnRole.Field || vectorColumn.DataType != FieldType.Vector)
        {
            throw new InvalidOperationException(
                $"hybrid_search 的 measurement 向量列 '{vectorColumnName}' 必须是 VECTOR FIELD。");
        }
        int dimension = vectorColumn.VectorDimension
            ?? throw new InvalidOperationException($"VECTOR 列 '{vectorColumnName}' 缺少维度声明（schema 损坏）。");

        float[] queryVector = RequireVectorArgument(args, "vector", "query_vector");
        if (queryVector.Length != dimension)
        {
            throw new InvalidOperationException(
                $"hybrid_search 查询向量维度 {queryVector.Length} 与列 '{vectorColumnName}' 声明的维度 {dimension} 不一致。");
        }

        string joinTagName = RequireIdentifierArgument(args, "measurement_join_tag", "join_tag", "tag");
        var joinTag = schema.TryGetColumn(joinTagName)
            ?? throw new InvalidOperationException($"hybrid_search measurement '{schema.Name}' 中不存在关联 tag '{joinTagName}'。");
        if (joinTag.Role != MeasurementColumnRole.Tag)
            throw new InvalidOperationException($"hybrid_search 关联列 '{joinTagName}' 必须是 measurement TAG。");

        string documentJoinPathText = NormalizeJsonPath(RequireStringArgument(args, "document_join_path", "join_path"));
        var documentJoinPath = JsonPath.Parse(documentJoinPathText);
        DocumentPathIndex? documentJoinIndex = ResolveDocumentJoinIndex(documentSchema, args, documentJoinPath);

        string? textQuery = TryGetArgument(args, out var textExpression, "text", "text_query")
            ? RequireStringLiteral(textExpression, "text")
            : null;
        string textField = GetFieldArgument(args, defaultValue: "*", "text_field", "field");
        DocumentFullTextIndex? fullTextIndex = null;
        if (textQuery is not null)
            fullTextIndex = ResolveFullTextIndex(documentSchema, args);

        JsonPath? documentVectorPath = null;
        if (TryGetArgument(args, out _, "document_vector_field", "knowledge_vector_field"))
        {
            string documentVectorField = NormalizeJsonPath(GetFieldArgument(
                args,
                defaultValue: "$.embedding",
                "document_vector_field",
                "knowledge_vector_field"));
            documentVectorPath = JsonPath.Parse(documentVectorField);
        }

        int k = GetPositiveIntArgument(args, DefaultK, "k", "top_k");
        int measurementCandidateLimit = GetPositiveIntArgument(
            args,
            Math.Max(k * DefaultTextCandidateMultiplier, DefaultFullTextTopK(pagination)),
            "measurement_top_k",
            "measurement_k",
            "knn_k");
        var metric = GetMetricArgument(args);
        double measurementWeight = GetNonNegativeDoubleArgument(args, 0.6d, "measurement_weight", "knn_weight", "vector_weight");
        double textWeight = GetNonNegativeDoubleArgument(args, textQuery is null ? 0d : 0.3d, "text_weight");
        double documentVectorWeight = GetNonNegativeDoubleArgument(args, documentVectorPath is null ? 0d : 0.1d, "document_vector_weight", "knowledge_vector_weight");
        if (measurementWeight == 0d && textWeight == 0d && documentVectorWeight == 0d)
            throw new InvalidOperationException("hybrid_search 的 measurement/text/document vector 权重不能同时为 0。");

        int textCandidateLimit = Math.Max(k * DefaultTextCandidateMultiplier, DefaultFullTextTopK(pagination));
        return new MeasurementKnowledgeOptions(
            vectorColumn,
            queryVector,
            metric,
            k,
            measurementCandidateLimit,
            documentSchema,
            documentStore,
            joinTag,
            documentJoinPath,
            documentJoinIndex,
            fullTextIndex,
            textField,
            textQuery,
            textCandidateLimit,
            documentVectorPath,
            measurementWeight,
            textWeight,
            documentVectorWeight);
    }

    private static Dictionary<string, SqlExpression> BindArguments(FunctionCallExpression call)
    {
        var result = new Dictionary<string, SqlExpression>(StringComparer.OrdinalIgnoreCase);
        bool hasNamed = call.Arguments.Any(static arg => arg is NamedArgumentExpression);
        if (hasNamed)
        {
            foreach (var argument in call.Arguments)
            {
                if (argument is not NamedArgumentExpression named)
                    throw new InvalidOperationException("hybrid_search(...) 使用命名参数时，所有参数都必须写成 name => value。");
                if (!result.TryAdd(named.Name, named.Value))
                    throw new InvalidOperationException($"hybrid_search(...) 参数 '{named.Name}' 重复。");
            }

            return result;
        }

        if (call.Arguments.Count is < 3 or > 4)
        {
            throw new InvalidOperationException(
                "hybrid_search(source, text, vector[, k]) 或 hybrid_search(source => ..., text => ..., vector => ..., k => ...) 需要 source/text/vector 参数。");
        }

        result["source"] = call.Arguments[0];
        result["text"] = call.Arguments[1];
        result["vector"] = call.Arguments[2];
        if (call.Arguments.Count == 4)
            result["k"] = call.Arguments[3];
        return result;
    }

    private static DocumentFullTextIndex ResolveFullTextIndex(
        DocumentCollectionSchema schema,
        IReadOnlyDictionary<string, SqlExpression> args)
    {
        string? indexName = TryGetArgument(args, out var indexExpression, "text_index", "fulltext_index", "index")
            ? ExpressionToName(indexExpression, "全文索引名")
            : null;

        if (indexName is not null)
        {
            return schema.TryGetFullTextIndex(indexName)
                ?? throw new InvalidOperationException($"document collection '{schema.Name}' 中不存在全文索引 '{indexName}'。");
        }

        if (schema.FullTextIndexes.Count == 1)
            return schema.FullTextIndexes[0];

        throw new InvalidOperationException(
            $"hybrid_search 需要 text_index 参数；document collection '{schema.Name}' 当前有 {schema.FullTextIndexes.Count} 个全文索引。");
    }

    private static DocumentPathIndex? ResolveDocumentJoinIndex(
        DocumentCollectionSchema schema,
        IReadOnlyDictionary<string, SqlExpression> args,
        JsonPath documentJoinPath)
    {
        string? indexName = TryGetArgument(args, out var indexExpression, "document_join_index", "join_index")
            ? ExpressionToName(indexExpression, "关联 JSON 索引名")
            : null;

        if (indexName is not null)
        {
            var index = schema.TryGetIndex(indexName)
                ?? throw new InvalidOperationException($"document collection '{schema.Name}' 中不存在 JSON 索引 '{indexName}'。");
            if (index.Paths.Count != 1 || !string.Equals(index.Path, documentJoinPath.Text, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"document_join_index '{indexName}' 必须是 path '{documentJoinPath.Text}' 的单字段文档索引。");
            }

            return index;
        }

        return schema.Indexes.FirstOrDefault(index =>
            index.Paths.Count == 1 &&
            string.Equals(index.Path, documentJoinPath.Text, StringComparison.Ordinal));
    }

    private static bool TryReadVector(DocumentRow row, JsonPath path, out float[] vector)
    {
        vector = [];
        using var document = JsonDocument.Parse(row.Json);
        if (!JsonPathEvaluator.TryResolve(document.RootElement, path, out var element))
            return false;
        if (element.ValueKind == JsonValueKind.Null)
            return false;
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(
                $"hybrid_search 文档 '{row.Id}' 的向量字段 '{path.Text}' 必须是 JSON number array。");

        int length = element.GetArrayLength();
        if (length == 0)
            throw new InvalidOperationException(
                $"hybrid_search 文档 '{row.Id}' 的向量字段 '{path.Text}' 不能为空数组。");

        var result = new float[length];
        int index = 0;
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetSingle(out float value))
            {
                throw new InvalidOperationException(
                    $"hybrid_search 文档 '{row.Id}' 的向量字段 '{path.Text}' 只能包含 number。");
            }

            result[index++] = value;
        }

        vector = result;
        return true;
    }

    private static Projection[] BuildDocumentProjections(IReadOnlyList<SelectItem> items)
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
                    projections.Add(new Projection("bm25_score", new IdentifierExpression("bm25_score")));
                    projections.Add(new Projection("vector_distance", new IdentifierExpression("vector_distance")));
                    projections.Add(new Projection("vector_score", new IdentifierExpression("vector_score")));
                    projections.Add(new Projection("hybrid_score", new IdentifierExpression("hybrid_score")));
                    break;

                case IdentifierExpression id:
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
                        $"hybrid_search SELECT 暂不支持投影表达式 '{item.Expression.GetType().Name}'。");
            }
        }

        return [.. projections];
    }

    private static Projection[] BuildKnowledgeProjections(
        IReadOnlyList<SelectItem> items,
        MeasurementSchema schema,
        RelationFilterPlan? relationPlan)
    {
        var projections = new List<Projection>(items.Count);
        foreach (var item in items)
        {
            switch (item.Expression)
            {
                case StarExpression:
                    if (item.Alias is not null)
                        throw new InvalidOperationException("'*' 不允许带 alias。");
                    projections.Add(new Projection("time", new IdentifierExpression("time")));
                    projections.Add(new Projection("measurement_distance", new IdentifierExpression("measurement_distance")));
                    projections.Add(new Projection("measurement_score", new IdentifierExpression("measurement_score")));
                    foreach (var tag in schema.TagColumns)
                        projections.Add(new Projection(tag.Name, new IdentifierExpression(tag.Name, "measurement")));
                    foreach (var field in schema.FieldColumns)
                        projections.Add(new Projection(field.Name, new IdentifierExpression(field.Name, "measurement")));
                    projections.Add(new Projection("document_id", new IdentifierExpression("document_id")));
                    projections.Add(new Projection("document", new IdentifierExpression("document")));
                    projections.Add(new Projection("bm25_score", new IdentifierExpression("bm25_score")));
                    projections.Add(new Projection("text_score", new IdentifierExpression("text_score")));
                    projections.Add(new Projection("document_vector_distance", new IdentifierExpression("document_vector_distance")));
                    projections.Add(new Projection("document_vector_score", new IdentifierExpression("document_vector_score")));
                    projections.Add(new Projection("hybrid_score", new IdentifierExpression("hybrid_score")));
                    if (relationPlan is not null)
                    {
                        foreach (var column in relationPlan.TableSchema.Columns)
                            projections.Add(new Projection(
                                $"{relationPlan.Alias}.{column.Name}",
                                new IdentifierExpression(column.Name, relationPlan.Alias)));
                    }
                    break;

                case IdentifierExpression id:
                    projections.Add(new Projection(item.Alias ?? FormatIdentifierColumnName(id), item.Expression));
                    break;

                case FunctionCallExpression function:
                    projections.Add(new Projection(item.Alias ?? FormatFunctionColumnName(function), item.Expression));
                    break;

                case LiteralExpression literal:
                    projections.Add(new Projection(item.Alias ?? FormatLiteralColumnName(literal), item.Expression));
                    break;

                default:
                    throw new InvalidOperationException(
                        $"hybrid_search SELECT 暂不支持投影表达式 '{item.Expression.GetType().Name}'。");
            }
        }

        return [.. projections];
    }

    private static bool EvaluateBoolean(SqlExpression expression, HybridRow row)
    {
        switch (expression)
        {
            case BinaryExpression binary:
                if (binary.Operator == SqlBinaryOperator.And)
                    return EvaluateBoolean(binary.Left, row) && EvaluateBoolean(binary.Right, row);
                if (binary.Operator == SqlBinaryOperator.Or)
                    return EvaluateBoolean(binary.Left, row) || EvaluateBoolean(binary.Right, row);
                if (IsComparisonOperator(binary.Operator))
                    return EvaluateComparison(binary, row);
                break;

            case UnaryExpression { Operator: SqlUnaryOperator.Not } unary:
                return !EvaluateBoolean(unary.Operand, row);

            case IsNullExpression isNull:
                var isNullValue = EvaluateScalar(isNull.Operand, row) is null;
                return isNull.Negated ? !isNullValue : isNullValue;
        }

        var value = EvaluateScalar(expression, row);
        if (value is bool b)
            return b;
        throw new InvalidOperationException("hybrid_search WHERE 表达式必须计算为布尔值。");
    }

    private static bool EvaluateComparison(BinaryExpression binary, HybridRow row)
    {
        var left = EvaluateScalar(binary.Left, row);
        var right = EvaluateScalar(binary.Right, row);
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

    private static bool EvaluateBoolean(SqlExpression expression, KnowledgeHybridRow row)
    {
        switch (expression)
        {
            case BinaryExpression binary:
                if (binary.Operator == SqlBinaryOperator.And)
                    return EvaluateBoolean(binary.Left, row) && EvaluateBoolean(binary.Right, row);
                if (binary.Operator == SqlBinaryOperator.Or)
                    return EvaluateBoolean(binary.Left, row) || EvaluateBoolean(binary.Right, row);
                if (IsComparisonOperator(binary.Operator))
                    return EvaluateComparison(binary, row);
                break;

            case UnaryExpression { Operator: SqlUnaryOperator.Not } unary:
                return !EvaluateBoolean(unary.Operand, row);

            case IsNullExpression isNull:
                var isNullValue = EvaluateScalar(isNull.Operand, row) is null;
                return isNull.Negated ? !isNullValue : isNullValue;
        }

        var value = EvaluateScalar(expression, row);
        if (value is bool b)
            return b;
        throw new InvalidOperationException("hybrid_search WHERE 表达式必须计算为布尔值。");
    }

    private static bool EvaluateComparison(BinaryExpression binary, KnowledgeHybridRow row)
    {
        var left = EvaluateScalar(binary.Left, row);
        var right = EvaluateScalar(binary.Right, row);
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

    private static object? EvaluateScalar(SqlExpression expression, HybridRow row)
    {
        return expression switch
        {
            LiteralExpression literal => EvaluateLiteral(literal),
            IdentifierExpression identifier => GetIdentifierValue(identifier, row),
            FunctionCallExpression function => EvaluateFunction(function, row),
            UnaryExpression { Operator: SqlUnaryOperator.Negate } unary => -RequireDouble(EvaluateScalar(unary.Operand, row), "一元负号"),
            BinaryExpression binary when IsArithmeticOperator(binary.Operator) => EvaluateArithmetic(binary, row),
            _ => throw new InvalidOperationException(
                $"hybrid_search 表达式暂不支持 '{expression.GetType().Name}'。"),
        };
    }

    private static object? EvaluateScalar(SqlExpression expression, KnowledgeHybridRow row)
    {
        return expression switch
        {
            LiteralExpression literal => EvaluateLiteral(literal),
            IdentifierExpression identifier => GetIdentifierValue(identifier, row),
            FunctionCallExpression function => EvaluateFunction(function, row),
            UnaryExpression { Operator: SqlUnaryOperator.Negate } unary => -RequireDouble(EvaluateScalar(unary.Operand, row), "一元负号"),
            BinaryExpression binary when IsArithmeticOperator(binary.Operator) => EvaluateArithmetic(binary, row),
            _ => throw new InvalidOperationException(
                $"hybrid_search 表达式暂不支持 '{expression.GetType().Name}'。"),
        };
    }

    private static object? EvaluateFunction(FunctionCallExpression function, HybridRow row)
    {
        if (function.IsStar)
            throw new InvalidOperationException($"hybrid_search 不支持函数 {function.Name}(*)。");

        if (string.Equals(function.Name, "bm25_score", StringComparison.OrdinalIgnoreCase))
            return RequireNoArguments(function, row.Bm25Score);
        if (string.Equals(function.Name, "vector_distance", StringComparison.OrdinalIgnoreCase))
            return RequireNoArguments(function, row.VectorDistance);
        if (string.Equals(function.Name, "vector_score", StringComparison.OrdinalIgnoreCase))
            return RequireNoArguments(function, row.VectorScore);
        if (string.Equals(function.Name, "hybrid_score", StringComparison.OrdinalIgnoreCase))
            return RequireNoArguments(function, row.HybridScore);

        if (string.Equals(function.Name, "json_value", StringComparison.OrdinalIgnoreCase)
            && function.Arguments.Count == 2
            && function.Arguments[1] is LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var path })
        {
            var json = EvaluateScalar(function.Arguments[0], row) as string;
            return JsonPathEvaluator.Evaluate(json, path!);
        }

        throw new InvalidOperationException(
            "hybrid_search 当前仅支持 json_value(document, '$.path')、bm25_score()、vector_distance()、vector_score() 与 hybrid_score() 函数。");
    }

    private static object? EvaluateFunction(FunctionCallExpression function, KnowledgeHybridRow row)
    {
        if (function.IsStar)
            throw new InvalidOperationException($"hybrid_search 不支持函数 {function.Name}(*)。");

        if (string.Equals(function.Name, "bm25_score", StringComparison.OrdinalIgnoreCase))
            return RequireNoArguments(function, row.Bm25Score);
        if (string.Equals(function.Name, "text_score", StringComparison.OrdinalIgnoreCase))
            return RequireNoArguments(function, row.TextScore);
        if (string.Equals(function.Name, "measurement_distance", StringComparison.OrdinalIgnoreCase)
            || string.Equals(function.Name, "vector_distance", StringComparison.OrdinalIgnoreCase))
            return RequireNoArguments(function, row.MeasurementDistance);
        if (string.Equals(function.Name, "measurement_score", StringComparison.OrdinalIgnoreCase)
            || string.Equals(function.Name, "vector_score", StringComparison.OrdinalIgnoreCase))
            return RequireNoArguments(function, row.MeasurementScore);
        if (string.Equals(function.Name, "document_vector_distance", StringComparison.OrdinalIgnoreCase))
            return RequireNoArguments(function, row.DocumentVectorDistance);
        if (string.Equals(function.Name, "document_vector_score", StringComparison.OrdinalIgnoreCase))
            return RequireNoArguments(function, row.DocumentVectorScore);
        if (string.Equals(function.Name, "hybrid_score", StringComparison.OrdinalIgnoreCase))
            return RequireNoArguments(function, row.HybridScore);

        if (string.Equals(function.Name, "json_value", StringComparison.OrdinalIgnoreCase)
            && function.Arguments.Count == 2
            && function.Arguments[1] is LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var path })
        {
            var json = EvaluateScalar(function.Arguments[0], row) as string;
            return JsonPathEvaluator.Evaluate(json, path!);
        }

        throw new InvalidOperationException(
            "hybrid_search 当前仅支持 json_value(document, '$.path')、bm25_score()、measurement_distance()、measurement_score()、document_vector_distance()、document_vector_score() 与 hybrid_score() 函数。");
    }

    private static object? RequireNoArguments(FunctionCallExpression function, object? value)
    {
        if (function.Arguments.Count != 0)
            throw new InvalidOperationException($"函数 {function.Name}() 不接受参数。");
        return value;
    }

    private static object EvaluateArithmetic(BinaryExpression binary, HybridRow row)
    {
        var left = RequireDouble(EvaluateScalar(binary.Left, row), binary.Operator.ToString());
        var right = RequireDouble(EvaluateScalar(binary.Right, row), binary.Operator.ToString());
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

    private static object EvaluateArithmetic(BinaryExpression binary, KnowledgeHybridRow row)
    {
        var left = RequireDouble(EvaluateScalar(binary.Left, row), binary.Operator.ToString());
        var right = RequireDouble(EvaluateScalar(binary.Right, row), binary.Operator.ToString());
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

    private static object? GetIdentifierValue(IdentifierExpression identifier, HybridRow row)
    {
        if (identifier.Qualifier is not null)
            throw new InvalidOperationException("hybrid_search 当前不支持限定列名。");

        if (string.Equals(identifier.Name, "id", StringComparison.OrdinalIgnoreCase))
            return row.Document.Id;
        if (string.Equals(identifier.Name, "document", StringComparison.OrdinalIgnoreCase)
            || string.Equals(identifier.Name, "json", StringComparison.OrdinalIgnoreCase))
            return row.Document.Json;
        if (string.Equals(identifier.Name, "bm25_score", StringComparison.OrdinalIgnoreCase))
            return row.Bm25Score;
        if (string.Equals(identifier.Name, "vector_distance", StringComparison.OrdinalIgnoreCase))
            return row.VectorDistance;
        if (string.Equals(identifier.Name, "vector_score", StringComparison.OrdinalIgnoreCase))
            return row.VectorScore;
        if (string.Equals(identifier.Name, "hybrid_score", StringComparison.OrdinalIgnoreCase))
            return row.HybridScore;

        return JsonPathEvaluator.Evaluate(row.Document.Json, "$." + identifier.Name);
    }

    private static object? GetIdentifierValue(IdentifierExpression identifier, KnowledgeHybridRow row)
    {
        if (identifier.Qualifier is not null)
            return GetQualifiedIdentifierValue(identifier, row);

        if (string.Equals(identifier.Name, "time", StringComparison.OrdinalIgnoreCase))
            return row.Timestamp;
        if (string.Equals(identifier.Name, "document_id", StringComparison.OrdinalIgnoreCase)
            || string.Equals(identifier.Name, "id", StringComparison.OrdinalIgnoreCase))
            return row.Document.Id;
        if (string.Equals(identifier.Name, "document", StringComparison.OrdinalIgnoreCase)
            || string.Equals(identifier.Name, "json", StringComparison.OrdinalIgnoreCase))
            return row.Document.Json;
        if (string.Equals(identifier.Name, "measurement_distance", StringComparison.OrdinalIgnoreCase)
            || string.Equals(identifier.Name, "vector_distance", StringComparison.OrdinalIgnoreCase))
            return row.MeasurementDistance;
        if (string.Equals(identifier.Name, "measurement_score", StringComparison.OrdinalIgnoreCase)
            || string.Equals(identifier.Name, "vector_score", StringComparison.OrdinalIgnoreCase))
            return row.MeasurementScore;
        if (string.Equals(identifier.Name, "bm25_score", StringComparison.OrdinalIgnoreCase))
            return row.Bm25Score;
        if (string.Equals(identifier.Name, "text_score", StringComparison.OrdinalIgnoreCase))
            return row.TextScore;
        if (string.Equals(identifier.Name, "document_vector_distance", StringComparison.OrdinalIgnoreCase))
            return row.DocumentVectorDistance;
        if (string.Equals(identifier.Name, "document_vector_score", StringComparison.OrdinalIgnoreCase))
            return row.DocumentVectorScore;
        if (string.Equals(identifier.Name, "hybrid_score", StringComparison.OrdinalIgnoreCase))
            return row.HybridScore;

        if (row.Series.Tags.TryGetValue(identifier.Name, out string? tagValue))
            return tagValue;
        if (row.MeasurementFields.TryGetValue(identifier.Name, out var fieldValue))
            return fieldValue;
        if (row.RelationPlan is not null
            && row.RelationRow is not null
            && row.RelationPlan.TableSchema.TryGetColumn(identifier.Name) is { } tableColumn)
        {
            return row.RelationRow.Values[tableColumn.Ordinal];
        }

        return JsonPathEvaluator.Evaluate(row.Document.Json, "$." + identifier.Name);
    }

    private static object? GetQualifiedIdentifierValue(IdentifierExpression identifier, KnowledgeHybridRow row)
    {
        if (string.Equals(identifier.Qualifier, "measurement", StringComparison.OrdinalIgnoreCase)
            || string.Equals(identifier.Qualifier, row.Series.Measurement, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(identifier.Name, "time", StringComparison.OrdinalIgnoreCase))
                return row.Timestamp;
            if (row.Series.Tags.TryGetValue(identifier.Name, out string? tagValue))
                return tagValue;
            if (row.MeasurementFields.TryGetValue(identifier.Name, out var fieldValue))
                return fieldValue;
            return null;
        }

        if (string.Equals(identifier.Qualifier, "document", StringComparison.OrdinalIgnoreCase)
            || string.Equals(identifier.Qualifier, "documents", StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(identifier.Name, "id", StringComparison.OrdinalIgnoreCase)
                || string.Equals(identifier.Name, "document_id", StringComparison.OrdinalIgnoreCase))
                return row.Document.Id;
            if (string.Equals(identifier.Name, "document", StringComparison.OrdinalIgnoreCase)
                || string.Equals(identifier.Name, "json", StringComparison.OrdinalIgnoreCase))
                return row.Document.Json;
            return JsonPathEvaluator.Evaluate(row.Document.Json, "$." + identifier.Name);
        }

        if (row.RelationPlan is not null
            && row.RelationRow is not null
            && row.RelationPlan.MatchesQualifier(identifier.Qualifier!))
        {
            var column = row.RelationPlan.TableSchema.TryGetColumn(identifier.Name)
                ?? throw new InvalidOperationException(
                    $"hybrid_search JOIN 查询引用了未知 table 列 '{identifier.Name}'。");
            return row.RelationRow.Values[column.Ordinal];
        }

        throw new InvalidOperationException(
            $"hybrid_search 限定列名 '{identifier.Qualifier}.{identifier.Name}' 仅支持 measurement、document 或 JOIN 关系表限定符。");
    }

    private static double DistanceToScore(KnnMetric metric, double distance)
    {
        if (metric == KnnMetric.Cosine)
            return Math.Clamp(1d - (distance / 2d), 0d, 1d);
        if (metric == KnnMetric.InnerProduct)
        {
            if (distance <= -60d)
                return 1d;
            if (distance >= 60d)
                return 0d;
            return 1d / (1d + Math.Exp(distance));
        }

        return 1d / (1d + Math.Max(0d, distance));
    }

    private static int DefaultFullTextTopK(PaginationSpec? pagination)
    {
        if (pagination is null)
            return 100;

        long topK = pagination.Fetch is int fetch
            ? (long)pagination.Offset + fetch
            : (long)pagination.Offset + 100;
        return topK <= 0 ? 0 : topK > int.MaxValue ? int.MaxValue : (int)topK;
    }

    private static string RequireIdentifierArgument(
        IReadOnlyDictionary<string, SqlExpression> args,
        string name)
    {
        if (!TryGetArgument(args, out var expression, name))
            throw new InvalidOperationException($"hybrid_search 缺少必填参数 '{name}'。");
        return ExpressionToName(expression, name);
    }

    private static string RequireIdentifierArgument(
        IReadOnlyDictionary<string, SqlExpression> args,
        params ReadOnlySpan<string> names)
    {
        if (!TryGetArgument(args, out var expression, names))
            throw new InvalidOperationException($"hybrid_search 缺少必填参数 '{names[0]}'。");
        return ExpressionToName(expression, names[0]);
    }

    private static string RequireStringArgument(
        IReadOnlyDictionary<string, SqlExpression> args,
        params ReadOnlySpan<string> names)
    {
        if (!TryGetArgument(args, out var expression, names))
            throw new InvalidOperationException($"hybrid_search 缺少必填参数 '{names[0]}'。");
        return RequireStringLiteral(expression, names[0]);
    }

    private static string RequireStringLiteral(SqlExpression expression, string name)
    {
        if (expression is LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var value })
            return value!;
        throw new InvalidOperationException($"hybrid_search 参数 '{name}' 必须是字符串字面量。");
    }

    private static float[] RequireVectorArgument(
        IReadOnlyDictionary<string, SqlExpression> args,
        params ReadOnlySpan<string> names)
    {
        if (!TryGetArgument(args, out var expression, names))
            throw new InvalidOperationException($"hybrid_search 缺少必填参数 '{names[0]}'。");
        if (expression is not VectorLiteralExpression vector)
            throw new InvalidOperationException($"hybrid_search 参数 '{names[0]}' 必须是向量字面量。");

        var result = new float[vector.Components.Count];
        for (int i = 0; i < result.Length; i++)
            result[i] = (float)vector.Components[i];
        return result;
    }

    private static int GetPositiveIntArgument(
        IReadOnlyDictionary<string, SqlExpression> args,
        int defaultValue,
        params ReadOnlySpan<string> names)
    {
        if (!TryGetArgument(args, out var expression, names))
            return defaultValue;
        if (expression is LiteralExpression { Kind: SqlLiteralKind.Integer, IntegerValue: > 0 and <= int.MaxValue } literal)
            return (int)literal.IntegerValue;
        throw new InvalidOperationException($"hybrid_search 参数 '{names[0]}' 必须是正整数字面量。");
    }

    private static double GetNonNegativeDoubleArgument(
        IReadOnlyDictionary<string, SqlExpression> args,
        double defaultValue,
        params ReadOnlySpan<string> names)
    {
        if (!TryGetArgument(args, out var expression, names))
            return defaultValue;
        double value = expression switch
        {
            LiteralExpression { Kind: SqlLiteralKind.Integer, IntegerValue: var integer } => integer,
            LiteralExpression { Kind: SqlLiteralKind.Float, FloatValue: var number } => number,
            _ => throw new InvalidOperationException($"hybrid_search 参数 '{names[0]}' 必须是非负数字字面量。"),
        };
        if (value < 0d || double.IsNaN(value) || double.IsInfinity(value))
            throw new InvalidOperationException($"hybrid_search 参数 '{names[0]}' 必须是非负数字字面量。");
        return value;
    }

    private static string GetFieldArgument(
        IReadOnlyDictionary<string, SqlExpression> args,
        string defaultValue,
        params ReadOnlySpan<string> names)
    {
        if (!TryGetArgument(args, out var expression, names))
            return defaultValue;
        if (expression is StarExpression)
            return "*";
        return ExpressionToName(expression, names[0]);
    }

    private static KnnMetric GetMetricArgument(IReadOnlyDictionary<string, SqlExpression> args)
    {
        if (!TryGetArgument(args, out var expression, "metric"))
            return KnnMetric.Cosine;
        if (expression is not LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var metric })
            throw new InvalidOperationException("hybrid_search 参数 'metric' 必须是字符串字面量。");
        return metric!.ToLowerInvariant() switch
        {
            "cosine" or "cosine_distance" => KnnMetric.Cosine,
            "l2" or "l2_distance" or "euclidean" => KnnMetric.L2,
            "inner_product" or "dot" or "ip" => KnnMetric.InnerProduct,
            _ => throw new InvalidOperationException(
                $"hybrid_search 不支持 metric '{metric}'，仅支持 'cosine' / 'l2' / 'inner_product'。"),
        };
    }

    private static bool TryGetArgument(
        IReadOnlyDictionary<string, SqlExpression> args,
        out SqlExpression expression,
        params ReadOnlySpan<string> names)
    {
        foreach (string name in names)
        {
            if (args.TryGetValue(name, out expression!))
                return true;
        }

        expression = null!;
        return false;
    }

    private static string ExpressionToName(SqlExpression expression, string argumentName)
        => expression switch
        {
            IdentifierExpression identifier => identifier.Name,
            LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var value } => value!,
            _ => throw new InvalidOperationException($"hybrid_search 参数 '{argumentName}' 必须是标识符或字符串字面量。"),
        };

    private static string NormalizeJsonPath(string path)
    {
        if (path == "*")
            throw new InvalidOperationException("hybrid_search 的 vector_field 不能是 '*'。");
        return path.StartsWith('$') ? JsonPath.Parse(path).Text : JsonPath.Parse("$." + path).Text;
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

    private static object? ConvertFieldValue(FieldValue value) => value.Type switch
    {
        FieldType.Float64 => value.AsDouble(),
        FieldType.Int64 => value.AsLong(),
        FieldType.Boolean => value.AsBool(),
        FieldType.String => value.AsString(),
        FieldType.Vector => value.AsVector().ToArray(),
        FieldType.GeoPoint => value.AsGeoPoint(),
        _ => null,
    };

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

    private static string FormatLiteralColumnName(LiteralExpression literal) => literal.Kind switch
    {
        SqlLiteralKind.Null => "NULL",
        SqlLiteralKind.Boolean => literal.BooleanValue ? "TRUE" : "FALSE",
        SqlLiteralKind.Integer => literal.IntegerValue.ToString(CultureInfo.InvariantCulture),
        SqlLiteralKind.Float => literal.FloatValue.ToString(CultureInfo.InvariantCulture),
        SqlLiteralKind.String => literal.StringValue ?? string.Empty,
        _ => literal.Kind.ToString(),
    };

    private static string FormatIdentifierColumnName(IdentifierExpression identifier)
        => identifier.Qualifier is null
            ? identifier.Name
            : $"{identifier.Qualifier}.{identifier.Name}";

    private static string FormatFunctionColumnName(FunctionCallExpression function)
        => function.Arguments.Count == 2
            && function.Arguments[1] is LiteralExpression { Kind: SqlLiteralKind.String, StringValue: var path }
            ? path!
            : function.Name;

    private static bool IsMeasurementQualifier(string qualifier, string measurementName)
        => string.Equals(qualifier, "measurement", StringComparison.OrdinalIgnoreCase)
            || string.Equals(qualifier, measurementName, StringComparison.OrdinalIgnoreCase);

    private static bool IsDocumentQualifier(string qualifier)
        => string.Equals(qualifier, "document", StringComparison.OrdinalIgnoreCase)
            || string.Equals(qualifier, "documents", StringComparison.OrdinalIgnoreCase);

    private static RelationJoinKey MakeRelationJoinKey(object value)
        => value is string s
            ? new RelationJoinKey(s)
            : new RelationJoinKey(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);

    private sealed record Projection(string ColumnName, SqlExpression Expression);

    private sealed record HybridSearchOptions(
        DocumentFullTextIndex FullTextIndex,
        string TextField,
        string TextQuery,
        JsonPath VectorPath,
        float[] QueryVector,
        int K,
        int TextCandidateLimit,
        KnnMetric Metric,
        double TextWeight,
        double VectorWeight);

    private sealed record HybridRow(
        DocumentRow Document,
        double Bm25Score,
        double VectorDistance,
        double VectorScore,
        double HybridScore);

    private sealed record MeasurementKnowledgeOptions(
        MeasurementColumn VectorColumn,
        float[] QueryVector,
        KnnMetric Metric,
        int K,
        int MeasurementCandidateLimit,
        DocumentCollectionSchema DocumentSchema,
        DocumentCollectionStore DocumentStore,
        MeasurementColumn MeasurementJoinTag,
        JsonPath DocumentJoinPath,
        DocumentPathIndex? DocumentJoinIndex,
        DocumentFullTextIndex? FullTextIndex,
        string TextField,
        string? TextQuery,
        int TextCandidateLimit,
        JsonPath? DocumentVectorPath,
        double MeasurementWeight,
        double TextWeight,
        double DocumentVectorWeight);

    private sealed record KnowledgeHybridRow(
        long Timestamp,
        double MeasurementDistance,
        double MeasurementScore,
        SeriesEntry Series,
        IReadOnlyDictionary<string, object?> MeasurementFields,
        DocumentRow Document,
        RelationFilterPlan? RelationPlan,
        TableRow? RelationRow,
        double Bm25Score,
        double TextScore,
        double? DocumentVectorDistance,
        double DocumentVectorScore,
        double HybridScore);

    private sealed record RelationJoinKeys(
        MeasurementColumn MeasurementColumn,
        TableColumn TableColumn);

    private readonly record struct RelationJoinKey(string Value);

    private sealed record RelationFilterPlan(
        TableSchema TableSchema,
        string Alias,
        TableColumn JoinColumn,
        Dictionary<RelationJoinKey, List<TableRow>> RowsByJoinKey,
        string AccessPath,
        string? IndexName)
    {
        public bool MatchesQualifier(string qualifier)
            => string.Equals(qualifier, Alias, StringComparison.OrdinalIgnoreCase)
                || string.Equals(qualifier, TableSchema.Name, StringComparison.OrdinalIgnoreCase);
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
