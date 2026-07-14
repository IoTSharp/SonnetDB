using Microsoft.Extensions.Logging;
using SonnetDB.Catalog;
using SonnetDB.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Copilot;

/// <summary>
/// 文档摄入统计结果。
/// </summary>
internal sealed record DocsIngestStats(
    int ScannedFiles,
    int IndexedFiles,
    int SkippedFiles,
    int DeletedFiles,
    int WrittenChunks,
    bool DryRun);

/// <summary>
/// 扫描帮助文档、生成 embedding，并写入系统知识库 <c>__copilot__</c>。
/// </summary>
internal sealed class DocsIngestor
{
    internal const string CopilotDatabaseName = "__copilot__";
    internal const string DocsMeasurementName = "docs";
    internal const string DocsStateMeasurementName = "docs_state";
    internal const int ExpectedEmbeddingDimensions = 384;

    private readonly TsdbRegistry _registry;
    private readonly DocsSourceScanner _scanner;
    private readonly DocsChunker _chunker;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly ILogger<DocsIngestor> _logger;

    public DocsIngestor(
        TsdbRegistry registry,
        DocsSourceScanner scanner,
        DocsChunker chunker,
        IEmbeddingProvider embeddingProvider,
        ILogger<DocsIngestor> logger)
    {
        _registry = registry;
        _scanner = scanner;
        _chunker = chunker;
        _embeddingProvider = embeddingProvider;
        _logger = logger;
    }

    public async Task<DocsIngestStats> IngestAsync(
        IReadOnlyList<string> roots,
        bool force = false,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var files = _scanner.Scan(roots);
        var stateBySource = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        var scannedSources = new HashSet<string>(
            files.Select(static item => NormalizeStoredSource(item.Source)),
            StringComparer.OrdinalIgnoreCase);

        var indexedFiles = 0;
        var skippedFiles = 0;
        var deletedFiles = 0;
        var writtenChunks = 0;

        foreach (var staleSource in stateBySource.Keys.Where(source => !scannedSources.Contains(source)).ToArray())
        {
            deletedFiles++;
            if (!dryRun)
                DeleteSource(GetKnowledgeDb(), staleSource);
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var storedSource = NormalizeStoredSource(file.Source);
            if (!force && stateBySource.TryGetValue(storedSource, out var existing) && existing.Matches(file))
            {
                skippedFiles++;
                continue;
            }

            var chunks = _chunker.Chunk(file);
            indexedFiles++;
            writtenChunks += chunks.Count;

            if (dryRun)
                continue;

            var database = GetKnowledgeDb();
            DeleteSource(database, storedSource);
            await InsertChunksAsync(database, file, storedSource, chunks, cancellationToken).ConfigureAwait(false);
            InsertState(database, file, storedSource, chunks.Count);
        }

        return new DocsIngestStats(files.Count, indexedFiles, skippedFiles, deletedFiles, writtenChunks, dryRun);
    }

    /// <summary>
    /// 知识库当前已索引状态汇总（供 Web Admin 状态卡片只读展示）。
    /// </summary>
    /// <param name="IndexedFiles">已建索引的文档源数。</param>
    /// <param name="IndexedChunks">已写入向量库的块总数。</param>
    /// <param name="LastIngestedUtc">最近一次摄入完成时间（UTC ISO-8601）；从未摄入则为 null。</param>
    internal sealed record DocsIndexState(int IndexedFiles, int IndexedChunks, string? LastIngestedUtc);

    /// <summary>
    /// 读取 docs_state 表，汇总当前知识库已索引文件数 / 块数 / 最近摄入时间。
    /// </summary>
    internal async Task<DocsIndexState> GetIndexStateAsync(CancellationToken cancellationToken = default)
    {
        var rows = await LoadStateAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count == 0)
            return new DocsIndexState(0, 0, null);

        var indexedFiles = rows.Count;
        var indexedChunks = 0;
        string? latest = null;
        foreach (var row in rows.Values)
        {
            indexedChunks += (int)row.ChunkCount;
            if (string.IsNullOrEmpty(row.ModifiedUtc))
                continue;
            if (latest is null || string.CompareOrdinal(row.ModifiedUtc, latest) > 0)
                latest = row.ModifiedUtc;
        }

        return new DocsIndexState(indexedFiles, indexedChunks, latest);
    }

    internal Tsdb GetKnowledgeDb()
    {
        _registry.TryCreate(CopilotDatabaseName, out var tsdb);
        EnsureMeasurements(tsdb);
        return tsdb;
    }

    private async Task<Dictionary<string, DocsStateRow>> LoadStateAsync(CancellationToken cancellationToken)
    {
        var database = GetKnowledgeDb();
        var result = SqlExecutor.ExecuteStatement(database,
            new SelectStatement([new SelectItem(StarExpression.Instance, null)], DocsStateMeasurementName, null, []));
        if (result is not SelectExecutionResult selectResult)
            return new Dictionary<string, DocsStateRow>(StringComparer.OrdinalIgnoreCase);

        var rows = new Dictionary<string, DocsStateRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in selectResult.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = row.Count > 1 ? row[1]?.ToString() : null;
            if (string.IsNullOrWhiteSpace(source))
                continue;

            rows[source] = new DocsStateRow(
                source,
                Fingerprint: row.Count > 2 ? row[2]?.ToString() : null,
                ModifiedUtc: row.Count > 3 ? row[3]?.ToString() : null,
                ChunkCount: row.Count > 4 && row[4] is long chunkCount ? chunkCount : 0L);
        }

        return rows;
    }

    private async Task InsertChunksAsync(
        Tsdb database,
        DocsSourceFile file,
        string storedSource,
        IReadOnlyList<DocsChunk> chunks,
        CancellationToken cancellationToken)
    {
        if (chunks.Count == 0)
            return;

        var docsSchema = database.Measurements.TryGet(DocsMeasurementName)
            ?? throw new InvalidOperationException($"measurement '{DocsMeasurementName}' 不存在。");
        var usesLegacyTagCompatibility = UsesLegacyTagCompatibility(docsSchema);
        if (usesLegacyTagCompatibility)
        {
            _logger.CopilotLegacyDocsSchema(DocsMeasurementName);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var rows = new List<IReadOnlyList<SqlExpression>>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var embedding = await _embeddingProvider.EmbedAsync(chunks[i].Content, cancellationToken).ConfigureAwait(false);
            if (embedding.Length != ExpectedEmbeddingDimensions)
                throw new InvalidOperationException($"embedding 维度必须为 {ExpectedEmbeddingDimensions}，实际为 {embedding.Length}。");

            rows.Add([
                LiteralExpression.String(storedSource),
                LiteralExpression.String(PrepareStringValueForColumn(docsSchema, "section", chunks[i].Section)),
                LiteralExpression.String(PrepareStringValueForColumn(docsSchema, "title", chunks[i].Title)),
                LiteralExpression.String(chunks[i].Content),
                LiteralExpression.Integer(now + i),
                new VectorLiteralExpression(embedding.Select(static value => (double)value).ToArray()),
            ]);
        }

        var statement = new InsertStatement(
            DocsMeasurementName,
            ["source", "section", "title", "content", "time", "embedding"],
            rows);
        SqlExecutor.ExecuteStatement(database, statement);
        _logger.CopilotDocsSourceIndexed(chunks.Count, file.Source);
    }

    private static void InsertState(Tsdb database, DocsSourceFile file, string storedSource, int chunkCount)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var state = new InsertStatement(
            DocsStateMeasurementName,
            ["source", "fingerprint", "modified_utc", "chunk_count", "time"],
            [[
                LiteralExpression.String(storedSource),
                LiteralExpression.String(file.Fingerprint),
                LiteralExpression.String(file.LastWriteTimeUtc.UtcDateTime.ToString("O")),
                LiteralExpression.Integer(chunkCount),
                LiteralExpression.Integer(timestamp),
            ]]);
        SqlExecutor.ExecuteStatement(database, state);
    }

    private static void DeleteSource(Tsdb database, string source)
    {
        var predicate = new BinaryExpression(
            SqlBinaryOperator.Equal,
            new IdentifierExpression("source"),
            LiteralExpression.String(source));
        SqlExecutor.ExecuteStatement(database, new DeleteStatement(DocsMeasurementName, predicate));
        SqlExecutor.ExecuteStatement(database, new DeleteStatement(DocsStateMeasurementName, predicate));
    }

    private static void EnsureMeasurements(Tsdb database)
    {
        if (database.Measurements.TryGet(DocsMeasurementName) is null)
        {
            SqlExecutor.ExecuteStatement(database, new CreateMeasurementStatement(
                DocsMeasurementName,
                [
                    new ColumnDefinition("source", ColumnKind.Tag, SqlDataType.String),
                    new ColumnDefinition("section", ColumnKind.Field, SqlDataType.String),
                    new ColumnDefinition("title", ColumnKind.Field, SqlDataType.String),
                    new ColumnDefinition("content", ColumnKind.Field, SqlDataType.String),
                    new ColumnDefinition("embedding", ColumnKind.Field, SqlDataType.Vector, VectorDimension: ExpectedEmbeddingDimensions),
                ]));
        }

        if (database.Measurements.TryGet(DocsStateMeasurementName) is null)
        {
            SqlExecutor.ExecuteStatement(database, new CreateMeasurementStatement(
                DocsStateMeasurementName,
                [
                    new ColumnDefinition("source", ColumnKind.Tag, SqlDataType.String),
                    new ColumnDefinition("fingerprint", ColumnKind.Field, SqlDataType.String),
                    new ColumnDefinition("modified_utc", ColumnKind.Field, SqlDataType.String),
                    new ColumnDefinition("chunk_count", ColumnKind.Field, SqlDataType.Int64),
                ]));
        }
    }

    private sealed record DocsStateRow(string Source, string? Fingerprint, string? ModifiedUtc, long ChunkCount)
    {
        public bool Matches(DocsSourceFile file)
            => string.Equals(Fingerprint, file.Fingerprint, StringComparison.Ordinal)
               && string.Equals(ModifiedUtc, file.LastWriteTimeUtc.UtcDateTime.ToString("O"), StringComparison.Ordinal);
    }

    private static bool UsesLegacyTagCompatibility(MeasurementSchema schema)
        => IsTagColumn(schema, "section") || IsTagColumn(schema, "title");

    private static bool IsTagColumn(MeasurementSchema schema, string columnName)
        => schema.TryGetColumn(columnName)?.Role == MeasurementColumnRole.Tag;

    private static string PrepareStringValueForColumn(MeasurementSchema schema, string columnName, string value)
    {
        var column = schema.TryGetColumn(columnName)
            ?? throw new InvalidOperationException($"measurement '{schema.Name}' 缺少列 '{columnName}'。");
        return column.Role == MeasurementColumnRole.Tag
            ? NormalizeTagValue(value)
            : value;
    }

    private static string NormalizeStoredSource(string source) => NormalizeTagValue(source);

    private static string NormalizeTagValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value
            .Replace(",", "，", StringComparison.Ordinal)
            .Replace("=", "＝", StringComparison.Ordinal)
            .Replace("\"", "'", StringComparison.Ordinal)
            .Replace('\n', ' ')
            .Replace('\r', ' ')
            .Replace('\t', ' ');
    }
}
