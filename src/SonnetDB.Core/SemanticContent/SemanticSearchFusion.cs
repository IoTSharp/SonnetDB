namespace SonnetDB.SemanticContent;

/// <summary>确定性、有界的候选归一化、去重、融合和可选重排。</summary>
public static class SemanticSearchFusion
{
    /// <summary>融合至多十六路已授权候选；相同分数按稳定 Id 序数升序排列。</summary>
    /// <param name="query">非空查询，最多八千一百九十二字符。</param>
    /// <param name="sources">有限候选列表，分数越大越相关。</param>
    /// <param name="options">硬预算和融合策略。</param>
    /// <param name="reranker">可选重排器；默认不调用任何模型。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>只包含原始候选的稳定结果。</returns>
    public static async ValueTask<IReadOnlyList<SemanticSearchHit>> FuseAsync(
        string query, IReadOnlyList<SemanticSearchSource> sources, SemanticSearchFusionOptions? options = null,
        ISemanticSearchReranker? reranker = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(sources);
        options ??= new();
        options.Validate();
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Length, 8192);
        int sourceCount = sources.Count;
        ArgumentOutOfRangeException.ThrowIfNegative(sourceCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sourceCount, 16);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.MaxDuration);
        CancellationToken token = deadline.Token;
        token.ThrowIfCancellationRequested();
        long characters = query.Length;
        int totalCandidates = 0;
        var all = new Dictionary<string, SemanticSearchCandidate>(StringComparer.Ordinal);
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        if (characters > options.MaxTextCharacters)
            throw new InvalidOperationException("融合查询超过文本字符预算。");

        for (int sourceIndex = 0; sourceIndex < sourceCount; sourceIndex++)
        {
            token.ThrowIfCancellationRequested();
            SemanticSearchSource source = sources[sourceIndex];
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(source.Candidates);
            if (!double.IsFinite(source.Weight) || source.Weight <= 0 || source.Weight > 1000)
                throw new ArgumentOutOfRangeException(nameof(sources), "通道权重必须为有限正数且不超过一千。");
            int candidateCount = source.Candidates.Count;
            ArgumentOutOfRangeException.ThrowIfNegative(candidateCount);
            totalCandidates = checked(totalCandidates + candidateCount);
            if (candidateCount > options.MaxCandidatesPerSource || totalCandidates > options.MaxTotalCandidates)
                throw new InvalidOperationException("融合候选超过条目预算。");
            var unique = new Dictionary<string, SemanticSearchCandidate>(StringComparer.Ordinal);
            for (int candidateIndex = 0; candidateIndex < candidateCount; candidateIndex++)
            {
                token.ThrowIfCancellationRequested();
                SemanticSearchCandidate candidate = source.Candidates[candidateIndex];
                ArgumentNullException.ThrowIfNull(candidate);
                ArgumentException.ThrowIfNullOrWhiteSpace(candidate.Id);
                ArgumentException.ThrowIfNullOrWhiteSpace(candidate.ContentId);
                ArgumentNullException.ThrowIfNull(candidate.Text);
                if (!double.IsFinite(candidate.Score))
                    throw new ArgumentException("候选分数必须为有限值。", nameof(sources));
                characters += candidate.Id.Length + (long)candidate.ContentId.Length + candidate.Text.Length
                    + (candidate.Source?.Length ?? 0) + (candidate.Section?.Length ?? 0);
                if (characters > options.MaxTextCharacters)
                    throw new InvalidOperationException("融合候选超过文本字符预算。");
                if (all.TryGetValue(candidate.Id, out SemanticSearchCandidate? previous))
                {
                    if (candidate with { Score = previous.Score } != previous)
                        throw new ArgumentException("同一候选标识的正文或元数据不一致。", nameof(sources));
                }
                else
                    all.Add(candidate.Id, candidate);
                if (!unique.TryGetValue(candidate.Id, out SemanticSearchCandidate? best) || candidate.Score > best.Score)
                    unique[candidate.Id] = candidate;
            }
            SemanticSearchCandidate[] ranked = unique.Values.OrderByDescending(static c => c.Score)
                .ThenBy(static c => c.Id, StringComparer.Ordinal).ToArray();
            double min = ranked.Length == 0 ? 0 : ranked[^1].Score;
            double max = ranked.Length == 0 ? 0 : ranked[0].Score;
            for (int i = 0; i < ranked.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                SemanticSearchCandidate candidate = ranked[i];
                // 先缩放避免有限极值相减溢出；常量非空通道统一归一化为一。
                double scale = Math.Max(Math.Abs(min), Math.Abs(max));
                double normalized = min == max ? 1 :
                    ((candidate.Score / scale) - (min / scale)) / ((max / scale) - (min / scale));
                double score = source.Weight * (options.Mode == SemanticSearchFusionMode.ReciprocalRank
                    ? 1d / (options.RankConstant + i + 1d) : normalized);
                scores[candidate.Id] = scores.GetValueOrDefault(candidate.Id) + score;
            }
        }

        token.ThrowIfCancellationRequested();
        IEnumerable<SemanticSearchHit> rankedHits = scores.Select(pair =>
        {
            SemanticSearchCandidate candidate = all[pair.Key];
            return new SemanticSearchHit(candidate.Id, candidate.ContentId, candidate.Text, candidate.Source, candidate.Section, pair.Value, pair.Value);
        }).OrderByDescending(static hit => hit.Score).ThenBy(static hit => hit.Id, StringComparer.Ordinal);
        if (options.DeduplicateByContent)
            rankedHits = rankedHits.DistinctBy(static hit => hit.ContentId, StringComparer.Ordinal);
        SemanticSearchHit[] hits = rankedHits.Take(reranker is null ? options.TopK : options.MaxRerankCandidates).ToArray();
        if (reranker is null || hits.Length == 0)
        {
            token.ThrowIfCancellationRequested();
            return Array.AsReadOnly(hits);
        }

        // 扩展收到只读快照，只能返回 ID/分数；不允许注入、重复、缺失或修改授权内容。
        token.ThrowIfCancellationRequested();
        IReadOnlyList<SemanticSearchRerankScore> reranked = await reranker.RerankAsync(query, Array.AsReadOnly(hits), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (reranked is null || reranked.Count != hits.Length)
            throw new InvalidDataException("重排结果必须完整覆盖候选窗口。");
        var byId = hits.ToDictionary(static h => h.Id, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var final = new List<SemanticSearchHit>(hits.Length);
        for (int i = 0; i < hits.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            SemanticSearchRerankScore score = reranked[i];
            if (score is null || score.Id is null || !double.IsFinite(score.Score)
                || !seen.Add(score.Id) || !byId.TryGetValue(score.Id, out SemanticSearchHit? hit))
                throw new InvalidDataException("重排结果包含未知、重复或无效候选分数。");
            final.Add(hit with { Score = score.Score });
        }
        SemanticSearchHit[] result = final.OrderByDescending(static h => h.Score)
            .ThenBy(static h => h.Id, StringComparer.Ordinal).Take(options.TopK).ToArray();
        token.ThrowIfCancellationRequested();
        return Array.AsReadOnly(result);
    }
}
