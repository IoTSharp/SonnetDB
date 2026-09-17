namespace SonnetDB.Benchmarks.Benchmarks;

/// <summary>按 #341 原始阈值判定 #352 Preview 门禁，复用严格 artifact 校验与有界复现执行器。</summary>
public static class GraphPreviewGateEvaluator
{
    /// <summary>同步判定 Preview 原始证据；任何缺失、摘要漂移或复现失败均不能发布。</summary>
    /// <param name="input">独立的 Preview 证据清单。</param>
    /// <param name="artifactBaseDirectory">证据文件基准目录。</param>
    /// <param name="cancellationToken">取消令牌；取消时先回收当前复现进程树。</param>
    /// <returns>独立的 Preview 双门禁报告。</returns>
    public static GraphPreviewGateReport Evaluate(
        GraphPreviewGateInput input,
        string artifactBaseDirectory,
        CancellationToken cancellationToken = default)
        => EvaluateAsync(input, artifactBaseDirectory, cancellationToken)
            .ConfigureAwait(false).GetAwaiter().GetResult();

    /// <summary>异步判定 Preview 原始证据，支持有界读取、哈希计算和取消。</summary>
    /// <param name="input">独立的 Preview 证据清单。</param>
    /// <param name="artifactBaseDirectory">证据文件基准目录。</param>
    /// <param name="cancellationToken">取消令牌；取消时先回收当前复现进程树。</param>
    /// <returns>独立的 Preview 双门禁报告。</returns>
    public static Task<GraphPreviewGateReport> EvaluateAsync(
        GraphPreviewGateInput input,
        string artifactBaseDirectory,
        CancellationToken cancellationToken = default)
        => GraphProductionGateEvaluator.EvaluatePreviewAsync(input, artifactBaseDirectory, cancellationToken);

    /// <summary>返回 #352 必须验证的十二条原生图旅程 ID。</summary>
    /// <returns>按 ordinal 排列的旅程 ID。</returns>
    public static IReadOnlyList<string> GetRequiredJourneyIds()
        => GraphProductionGateEvaluator.GetPreviewRequiredJourneyIds();

    /// <summary>返回 Preview 正确性与恢复检查 ID。</summary>
    /// <returns>稳定检查 ID。</returns>
    public static IReadOnlyList<string> GetRequiredCorrectnessCheckIds()
        => GraphProductionGateEvaluator.GetPreviewRequiredCorrectnessCheckIds();

    /// <summary>返回 Preview 性能与容量检查 ID。</summary>
    /// <returns>稳定检查 ID。</returns>
    public static IReadOnlyList<string> GetRequiredPerformanceCheckIds()
        => GraphProductionGateEvaluator.GetPreviewRequiredPerformanceCheckIds();

    /// <summary>返回阻塞 Preview 的冻结 capability gap ID。</summary>
    /// <returns>M40-GAP-001～007。</returns>
    public static IReadOnlyList<string> GetRequiredGapIds()
        => GraphProductionGateEvaluator.GetPreviewRequiredGapIds();
}
