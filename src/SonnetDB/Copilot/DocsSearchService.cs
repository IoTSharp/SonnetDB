using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;

namespace SonnetDB.Copilot;

/// <summary>
/// 文档检索结果。
/// </summary>
internal sealed record DocsSearchResult(
    string Source,
    string Title,
    string Section,
    string Content,
    double Score,
    long Time);

/// <summary>
/// 封装 docs knowledge 库的 embedding 检索。
/// </summary>
internal sealed class DocsSearchService
{
    private const int CandidateMultiplier = 6;
    private const int MinimumCandidateCount = 32;

    private readonly DocsIngestor _ingestor;
    private readonly IEmbeddingProvider _embeddingProvider;

    public DocsSearchService(DocsIngestor ingestor, IEmbeddingProvider embeddingProvider)
    {
        _ingestor = ingestor;
        _embeddingProvider = embeddingProvider;
    }

    public async Task<IReadOnlyList<DocsSearchResult>> SearchAsync(string query, int k, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (k <= 0)
            throw new InvalidOperationException("k 必须大于 0。");

        var embedding = await _embeddingProvider.EmbedAsync(query, cancellationToken).ConfigureAwait(false);
        if (embedding.Length != DocsIngestor.ExpectedEmbeddingDimensions)
            throw new InvalidOperationException($"embedding 维度必须为 {DocsIngestor.ExpectedEmbeddingDimensions}，实际为 {embedding.Length}。");

        var database = _ingestor.GetKnowledgeDb();
        if (database.Measurements.TryGet(DocsIngestor.DocsMeasurementName) is null)
            return [];

        var candidateCount = Math.Max(k * CandidateMultiplier, MinimumCandidateCount);
        var vectorHits = ExecuteVectorSearch(database, embedding, candidateCount);
        var queryTerms = Tokenize(query);
        var lexicalHits = ExecuteLexicalSearch(database, query, queryTerms, candidateCount);

        return MergeAndRank(query, queryTerms, vectorHits, lexicalHits, k);
    }

    private static IReadOnlyList<DocsSearchResult> ExecuteVectorSearch(
        SonnetDB.Engine.Tsdb database,
        float[] embedding,
        int candidateCount)
    {
        var queryVector = new VectorLiteralExpression(embedding.Select(static value => (double)value).ToArray());
        var statement = new SelectStatement(
            [new SelectItem(StarExpression.Instance, null)],
            DocsIngestor.DocsMeasurementName,
            Where: null,
            GroupBy: [],
            TableValuedFunction: new FunctionCallExpression(
                "knn",
                [
                    new IdentifierExpression(DocsIngestor.DocsMeasurementName),
                    new IdentifierExpression("embedding"),
                    queryVector,
                    LiteralExpression.Integer(candidateCount),
                ]),
            Pagination: new PaginationSpec(0, candidateCount));

        var result = SqlExecutor.ExecuteStatement(database, statement);
        if (result is not SelectExecutionResult selectResult)
            return [];

        var rows = new List<DocsSearchResult>(selectResult.Rows.Count);
        foreach (var row in selectResult.Rows)
        {
            rows.Add(new DocsSearchResult(
                Source: row.Count > 2 ? row[2]?.ToString() ?? string.Empty : string.Empty,
                Title: row.Count > 4 ? row[4]?.ToString() ?? string.Empty : string.Empty,
                Section: row.Count > 3 ? row[3]?.ToString() ?? string.Empty : string.Empty,
                Content: row.Count > 5 ? row[5]?.ToString() ?? string.Empty : string.Empty,
                Score: row.Count > 1 && row[1] is double score ? score : 0d,
                Time: row.Count > 0 && row[0] is long time ? time : 0L));
        }

        return rows;
    }

    private static IReadOnlyList<DocsSearchResult> ExecuteLexicalSearch(
        SonnetDB.Engine.Tsdb database,
        string query,
        IReadOnlyList<string> queryTerms,
        int candidateCount)
    {
        if (queryTerms.Count == 0)
            return [];

        var statement = new SelectStatement(
            [new SelectItem(StarExpression.Instance, null)],
            DocsIngestor.DocsMeasurementName,
            Where: null,
            GroupBy: []);

        if (SqlExecutor.ExecuteStatement(database, statement) is not SelectExecutionResult selectResult)
            return [];

        var timeIndex = IndexOf(selectResult.Columns, "time");
        var sourceIndex = IndexOf(selectResult.Columns, "source");
        var sectionIndex = IndexOf(selectResult.Columns, "section");
        var titleIndex = IndexOf(selectResult.Columns, "title");
        var contentIndex = IndexOf(selectResult.Columns, "content");

        return selectResult.Rows
            .Select(row =>
            {
                var source = ReadString(row, sourceIndex);
                var title = ReadString(row, titleIndex);
                var section = ReadString(row, sectionIndex);
                var content = ReadString(row, contentIndex);
                var score = ScoreLexicalMatch(query, queryTerms, source, title, section, content);
                if (score <= 0)
                    return null;

                return new DocsSearchResult(
                    Source: source,
                    Title: title,
                    Section: section,
                    Content: content,
                    Score: LexicalDistance(score),
                    Time: ReadLong(row, timeIndex));
            })
            .Where(static row => row is not null)
            .Select(static row => row!)
            .OrderBy(static row => row.Score)
            .Take(candidateCount)
            .ToArray();
    }

    private static IReadOnlyList<DocsSearchResult> MergeAndRank(
        string query,
        IReadOnlyList<string> queryTerms,
        IReadOnlyList<DocsSearchResult> vectorHits,
        IReadOnlyList<DocsSearchResult> lexicalHits,
        int k)
    {
        var candidates = new Dictionary<string, Candidate>(StringComparer.Ordinal);

        foreach (var hit in vectorHits)
        {
            var key = CandidateKey(hit);
            candidates[key] = candidates.TryGetValue(key, out var existing)
                ? existing with { Hit = existing.Hit, VectorDistance = Math.Min(existing.VectorDistance, NormalizeDistance(hit.Score)) }
                : new Candidate(hit, NormalizeDistance(hit.Score), 1.25d);
        }

        foreach (var hit in lexicalHits)
        {
            var key = CandidateKey(hit);
            var lexicalDistance = NormalizeDistance(hit.Score);
            candidates[key] = candidates.TryGetValue(key, out var existing)
                ? existing with { LexicalDistance = Math.Min(existing.LexicalDistance, lexicalDistance) }
                : new Candidate(hit, 1.1d, lexicalDistance);
        }

        return candidates.Values
            .Select(candidate =>
            {
                var lexicalScore = ScoreLexicalMatch(
                    query,
                    queryTerms,
                    candidate.Hit.Source,
                    candidate.Hit.Title,
                    candidate.Hit.Section,
                    candidate.Hit.Content);
                var lexicalDistance = lexicalScore > 0
                    ? Math.Min(candidate.LexicalDistance, LexicalDistance(lexicalScore))
                    : candidate.LexicalDistance;
                var blended = (0.42d * candidate.VectorDistance) + (0.58d * lexicalDistance);
                return candidate.Hit with { Score = blended };
            })
            .OrderBy(static hit => hit.Score)
            .ThenBy(static hit => hit.Source, StringComparer.OrdinalIgnoreCase)
            .Take(k)
            .ToArray();
    }

    private static double ScoreLexicalMatch(
        string query,
        IReadOnlyList<string> queryTerms,
        string source,
        string title,
        string section,
        string content)
    {
        var normalizedQuery = NormalizeText(query);
        var sourceText = NormalizeText(source);
        var titleText = NormalizeText(title);
        var sectionText = NormalizeText(section);
        var contentText = NormalizeText(content);
        var score = 0d;

        if (normalizedQuery.Length > 1)
        {
            if (titleText.Contains(normalizedQuery, StringComparison.Ordinal))
                score += 12;
            if (sectionText.Contains(normalizedQuery, StringComparison.Ordinal))
                score += 10;
            if (contentText.Contains(normalizedQuery, StringComparison.Ordinal))
                score += 8;
        }

        foreach (var term in queryTerms)
        {
            if (sourceText.Contains(term, StringComparison.Ordinal))
                score += 3.5;
            if (titleText.Contains(term, StringComparison.Ordinal))
                score += 5;
            if (sectionText.Contains(term, StringComparison.Ordinal))
                score += 4;
            if (contentText.Contains(term, StringComparison.Ordinal))
                score += term.Length <= 2 ? 1.4 : 2.2;
        }

        return score;
    }

    private static IReadOnlyList<string> Tokenize(string text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var ascii = new System.Text.StringBuilder();
        var cjk = new System.Text.StringBuilder();

        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (IsAsciiWord(value))
            {
                FlushCjk();
                ascii.Append(char.ToLowerInvariant((char)value));
                continue;
            }

            if (IsCjk(value))
            {
                FlushAscii();
                cjk.Append(char.ConvertFromUtf32(value));
                continue;
            }

            FlushAscii();
            FlushCjk();
        }

        FlushAscii();
        FlushCjk();
        return tokens.OrderByDescending(static token => token.Length).ToArray();

        void FlushAscii()
        {
            if (ascii.Length <= 1)
            {
                ascii.Clear();
                return;
            }

            tokens.Add(ascii.ToString());
            ascii.Clear();
        }

        void FlushCjk()
        {
            if (cjk.Length == 0)
                return;

            var value = cjk.ToString();
            if (value.Length is > 1 and <= 8)
                tokens.Add(value);
            if (value.Length >= 2)
            {
                for (var i = 0; i < value.Length - 1; i++)
                    tokens.Add(value.Substring(i, 2));
            }

            cjk.Clear();
        }
    }

    private static bool IsAsciiWord(int value)
        => (value >= 'a' && value <= 'z')
           || (value >= 'A' && value <= 'Z')
           || (value >= '0' && value <= '9')
           || value == '_'
           || value == '-';

    private static bool IsCjk(int value)
        => (value >= 0x4E00 && value <= 0x9FFF)
           || (value >= 0x3400 && value <= 0x4DBF);

    private static string NormalizeText(string value)
        => value.Trim().ToLowerInvariant();

    private static double NormalizeDistance(double value)
        => double.IsFinite(value) ? Math.Clamp(value, 0d, 1.25d) : 1.25d;

    private static double LexicalDistance(double score)
        => 1d / (1d + score);

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static string ReadString(IReadOnlyList<object?> row, int index)
        => index >= 0 && index < row.Count ? row[index]?.ToString() ?? string.Empty : string.Empty;

    private static long ReadLong(IReadOnlyList<object?> row, int index)
        => index >= 0 && index < row.Count && row[index] is long value ? value : 0L;

    private static string CandidateKey(DocsSearchResult hit)
        => $"{hit.Source}\n{hit.Section}\n{hit.Content}";

    private sealed record Candidate(
        DocsSearchResult Hit,
        double VectorDistance,
        double LexicalDistance);
}
