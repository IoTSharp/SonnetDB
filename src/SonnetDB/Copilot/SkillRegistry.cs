using Microsoft.Extensions.Logging;
using SonnetDB.Diagnostics;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Copilot;

/// <summary>
/// 技能库摄入统计结果（PR #65）。
/// </summary>
internal sealed record SkillIngestStats(
    int ScannedSkills,
    int IndexedSkills,
    int SkippedSkills,
    int DeletedSkills,
    bool DryRun);

/// <summary>
/// 技能库检索单条命中（PR #65）。
/// </summary>
internal sealed record SkillSearchHit(
    string Name,
    string Description,
    IReadOnlyList<string> Triggers,
    IReadOnlyList<string> RequiresTools,
    double Score);

/// <summary>
/// 通过 <c>skill_load</c> 返回的完整技能内容（PR #65）。
/// </summary>
internal sealed record SkillLoadResult(
    string Name,
    string Description,
    IReadOnlyList<string> Triggers,
    IReadOnlyList<string> RequiresTools,
    string Body,
    string Source);

/// <summary>
/// 扫描 <c>copilot/skills</c> 目录，把每个技能 markdown 解析、嵌入并写入 <c>__copilot__.skills</c>，
/// 同时维护增量摄入状态表 <c>skills_state</c>（PR #65）。
/// </summary>
internal sealed class SkillRegistry
{
    internal const string SkillsMeasurementName = "skills";
    internal const string SkillsStateMeasurementName = "skills_state";
    internal const int ExpectedEmbeddingDimensions = 384;

    private readonly TsdbRegistry _registry;
    private readonly SkillSourceScanner _scanner;
    private readonly IEmbeddingProvider _embeddingProvider;
    private readonly ILogger<SkillRegistry> _logger;

    public SkillRegistry(
        TsdbRegistry registry,
        SkillSourceScanner scanner,
        IEmbeddingProvider embeddingProvider,
        ILogger<SkillRegistry> logger)
    {
        _registry = registry;
        _scanner = scanner;
        _embeddingProvider = embeddingProvider;
        _logger = logger;
    }

    public async Task<SkillIngestStats> IngestAsync(
        string root,
        bool force = false,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var skills = _scanner.Scan(root);
        var stateByName = LoadState();
        var scannedNames = new HashSet<string>(skills.Select(static item => item.Name), StringComparer.OrdinalIgnoreCase);

        var indexed = 0;
        var skipped = 0;
        var deleted = 0;

        foreach (var stale in stateByName.Keys.Where(name => !scannedNames.Contains(name)).ToArray())
        {
            deleted++;
            if (!dryRun)
                DeleteSkill(GetKnowledgeDb(), stale);
        }

        foreach (var skill in skills)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!force && stateByName.TryGetValue(skill.Name, out var existing) && existing.Matches(skill))
            {
                skipped++;
                continue;
            }

            indexed++;
            if (dryRun)
                continue;

            var database = GetKnowledgeDb();
            DeleteSkill(database, skill.Name);
            await InsertSkillAsync(database, skill, cancellationToken).ConfigureAwait(false);
            InsertState(database, skill);
        }

        return new SkillIngestStats(skills.Count, indexed, skipped, deleted, dryRun);
    }

    public IReadOnlyList<SkillSearchHit> Search(float[] queryEmbedding, int k)
    {
        ArgumentNullException.ThrowIfNull(queryEmbedding);
        if (queryEmbedding.Length != ExpectedEmbeddingDimensions)
            throw new InvalidOperationException($"embedding 维度必须为 {ExpectedEmbeddingDimensions}，实际为 {queryEmbedding.Length}。");
        if (k <= 0)
            throw new InvalidOperationException("k 必须大于 0。");

        var database = GetKnowledgeDb();
        if (database.Measurements.TryGet(SkillsMeasurementName) is null)
            return [];

        var queryVector = new VectorLiteralExpression(queryEmbedding.Select(static value => (double)value).ToArray());
        var statement = new SelectStatement(
            [new SelectItem(StarExpression.Instance, null)],
            SkillsMeasurementName,
            Where: null,
            GroupBy: [],
            TableValuedFunction: new FunctionCallExpression(
                "knn",
                [
                    new IdentifierExpression(SkillsMeasurementName),
                    new IdentifierExpression("embedding"),
                    queryVector,
                    LiteralExpression.Integer(k),
                ]),
            Pagination: new PaginationSpec(0, k));

        if (SqlExecutor.ExecuteStatement(database, statement) is not SelectExecutionResult selectResult)
            return [];

        // SELECT * + knn(...) 列序：[time, score, name, description, triggers, requires_tools, path, body, embedding]
        var hits = new List<SkillSearchHit>(selectResult.Rows.Count);
        foreach (var row in selectResult.Rows)
        {
            hits.Add(new SkillSearchHit(
                Name: row.Count > 2 ? row[2]?.ToString() ?? string.Empty : string.Empty,
                Description: row.Count > 3 ? row[3]?.ToString() ?? string.Empty : string.Empty,
                Triggers: row.Count > 4 ? SplitList(row[4]?.ToString()) : [],
                RequiresTools: row.Count > 5 ? SplitList(row[5]?.ToString()) : [],
                Score: row.Count > 1 && row[1] is double score ? score : 0d));
        }

        return hits;
    }

    public SkillLoadResult? Load(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var database = GetKnowledgeDb();
        if (database.Measurements.TryGet(SkillsMeasurementName) is null)
            return null;

        var statement = new SelectStatement(
            [new SelectItem(StarExpression.Instance, null)],
            SkillsMeasurementName,
            Where: new BinaryExpression(
                SqlBinaryOperator.Equal,
                new IdentifierExpression("name"),
                LiteralExpression.String(name)),
            GroupBy: []);

        if (SqlExecutor.ExecuteStatement(database, statement) is not SelectExecutionResult selectResult
            || selectResult.Rows.Count == 0)
        {
            return null;
        }

        // SELECT * 列序：[time, name, description, triggers, requires_tools, path, body, embedding]
        var row = selectResult.Rows[0];
        return new SkillLoadResult(
            Name: row.Count > 1 ? row[1]?.ToString() ?? name : name,
            Description: row.Count > 2 ? row[2]?.ToString() ?? string.Empty : string.Empty,
            Triggers: row.Count > 3 ? SplitList(row[3]?.ToString()) : [],
            RequiresTools: row.Count > 4 ? SplitList(row[4]?.ToString()) : [],
            Body: row.Count > 6 ? row[6]?.ToString() ?? string.Empty : string.Empty,
            Source: row.Count > 5 ? row[5]?.ToString() ?? string.Empty : string.Empty);
    }

    public IReadOnlyList<SkillSearchHit> List()
    {
        var database = GetKnowledgeDb();
        if (database.Measurements.TryGet(SkillsMeasurementName) is null)
            return [];

        var statement = new SelectStatement(
            [new SelectItem(StarExpression.Instance, null)],
            SkillsMeasurementName,
            Where: null,
            GroupBy: []);

        if (SqlExecutor.ExecuteStatement(database, statement) is not SelectExecutionResult selectResult)
            return [];

        var hits = new List<SkillSearchHit>(selectResult.Rows.Count);
        foreach (var row in selectResult.Rows)
        {
            hits.Add(new SkillSearchHit(
                Name: row.Count > 1 ? row[1]?.ToString() ?? string.Empty : string.Empty,
                Description: row.Count > 2 ? row[2]?.ToString() ?? string.Empty : string.Empty,
                Triggers: row.Count > 3 ? SplitList(row[3]?.ToString()) : [],
                RequiresTools: row.Count > 4 ? SplitList(row[4]?.ToString()) : [],
                Score: 0d));
        }

        return hits
            .OrderBy(static item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal Tsdb GetKnowledgeDb()
    {
        _registry.TryCreate(DocsIngestor.CopilotDatabaseName, out var tsdb);
        EnsureMeasurements(tsdb);
        return tsdb;
    }

    private Dictionary<string, SkillStateRow> LoadState()
    {
        var database = GetKnowledgeDb();
        var result = SqlExecutor.ExecuteStatement(database,
            new SelectStatement([new SelectItem(StarExpression.Instance, null)], SkillsStateMeasurementName, null, []));
        if (result is not SelectExecutionResult selectResult)
            return new Dictionary<string, SkillStateRow>(StringComparer.OrdinalIgnoreCase);

        var rows = new Dictionary<string, SkillStateRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in selectResult.Rows)
        {
            var name = row.Count > 1 ? row[1]?.ToString() : null;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            rows[name] = new SkillStateRow(
                name,
                Fingerprint: row.Count > 2 ? row[2]?.ToString() : null,
                ModifiedUtc: row.Count > 3 ? row[3]?.ToString() : null);
        }

        return rows;
    }

    private async Task InsertSkillAsync(Tsdb database, SkillDocument skill, CancellationToken cancellationToken)
    {
        var embedding = await _embeddingProvider.EmbedAsync(skill.ToEmbeddingText(), cancellationToken).ConfigureAwait(false);
        if (embedding.Length != ExpectedEmbeddingDimensions)
            throw new InvalidOperationException($"embedding 维度必须为 {ExpectedEmbeddingDimensions}，实际为 {embedding.Length}。");

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var statement = new InsertStatement(
            SkillsMeasurementName,
            ["name", "description", "triggers", "requires_tools", "path", "body", "time", "embedding"],
            [[
                LiteralExpression.String(skill.Name),
                LiteralExpression.String(skill.Description),
                LiteralExpression.String(string.Join(",", skill.Triggers)),
                LiteralExpression.String(string.Join(",", skill.RequiresTools)),
                LiteralExpression.String(skill.RelativePath),
                LiteralExpression.String(skill.Body),
                LiteralExpression.Integer(now),
                new VectorLiteralExpression(embedding.Select(static value => (double)value).ToArray()),
            ]]);

        SqlExecutor.ExecuteStatement(database, statement);
        _logger.CopilotSkillIndexed(skill.Name, skill.RelativePath);
    }

    private static void InsertState(Tsdb database, SkillDocument skill)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var state = new InsertStatement(
            SkillsStateMeasurementName,
            ["name", "fingerprint", "modified_utc", "time"],
            [[
                LiteralExpression.String(skill.Name),
                LiteralExpression.String(skill.Fingerprint),
                LiteralExpression.String(skill.LastWriteTimeUtc.UtcDateTime.ToString("O")),
                LiteralExpression.Integer(timestamp),
            ]]);
        SqlExecutor.ExecuteStatement(database, state);
    }

    private static void DeleteSkill(Tsdb database, string name)
    {
        var predicate = new BinaryExpression(
            SqlBinaryOperator.Equal,
            new IdentifierExpression("name"),
            LiteralExpression.String(name));
        SqlExecutor.ExecuteStatement(database, new DeleteStatement(SkillsMeasurementName, predicate));
        SqlExecutor.ExecuteStatement(database, new DeleteStatement(SkillsStateMeasurementName, predicate));
    }

    private static void EnsureMeasurements(Tsdb database)
    {
        if (database.Measurements.TryGet(SkillsMeasurementName) is null)
        {
            SqlExecutor.ExecuteStatement(database, new CreateMeasurementStatement(
                SkillsMeasurementName,
                [
                    new ColumnDefinition("name", ColumnKind.Tag, SqlDataType.String),
                    new ColumnDefinition("description", ColumnKind.Field, SqlDataType.String),
                    new ColumnDefinition("triggers", ColumnKind.Field, SqlDataType.String),
                    new ColumnDefinition("requires_tools", ColumnKind.Field, SqlDataType.String),
                    new ColumnDefinition("path", ColumnKind.Field, SqlDataType.String),
                    new ColumnDefinition("body", ColumnKind.Field, SqlDataType.String),
                    new ColumnDefinition("embedding", ColumnKind.Field, SqlDataType.Vector, VectorDimension: ExpectedEmbeddingDimensions),
                ]));
        }

        if (database.Measurements.TryGet(SkillsStateMeasurementName) is null)
        {
            SqlExecutor.ExecuteStatement(database, new CreateMeasurementStatement(
                SkillsStateMeasurementName,
                [
                    new ColumnDefinition("name", ColumnKind.Tag, SqlDataType.String),
                    new ColumnDefinition("fingerprint", ColumnKind.Field, SqlDataType.String),
                    new ColumnDefinition("modified_utc", ColumnKind.Field, SqlDataType.String),
                ]));
        }
    }

    private static IReadOnlyList<string> SplitList(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        return text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
    }

    private sealed record SkillStateRow(string Name, string? Fingerprint, string? ModifiedUtc)
    {
        public bool Matches(SkillDocument skill)
            => string.Equals(Fingerprint, skill.Fingerprint, StringComparison.Ordinal)
               && string.Equals(ModifiedUtc, skill.LastWriteTimeUtc.UtcDateTime.ToString("O"), StringComparison.Ordinal);
    }
}
