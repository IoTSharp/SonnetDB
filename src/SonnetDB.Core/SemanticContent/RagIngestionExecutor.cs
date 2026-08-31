namespace SonnetDB.SemanticContent;

/// <summary>
/// RAG 增量计划执行的并发和动作预算。
/// </summary>
public sealed record RagIngestionExecutionOptions
{
    /// <summary>同时执行的最大动作数。</summary>
    public int MaxConcurrency { get; init; } = 1;

    /// <summary>单次执行允许接受的最大动作数。</summary>
    public int MaxActions { get; init; } = 100_000;

    /// <summary>整份计划合计允许包含的最大分块数。</summary>
    public int MaxTotalChunks { get; init; } = 1_000_000;

    /// <summary>整份计划合计允许包含的最大时间分段数。</summary>
    public int MaxTotalSegments { get; init; } = 1_000_000;

    /// <summary>整份计划合计允许包含的最大 embedding 绑定数。</summary>
    public int MaxTotalEmbeddings { get; init; } = 1_000_000;

    /// <summary>
    /// 整份计划合计允许包含的最大文本字符数。
    /// 该预算累计内容级文本、分块文本和分段文本的 UTF-16 长度。
    /// </summary>
    public long MaxTotalTextCharacters { get; init; } = 128L * 1024 * 1024;
}

/// <summary>
/// RAG 增量计划的执行结果。
/// </summary>
/// <param name="TotalActions">计划中的动作总数。</param>
/// <param name="CompletedActions">成功完成的动作数。</param>
public sealed record RagIngestionExecutionResult(
    int TotalActions,
    int CompletedActions);

/// <summary>
/// 以有界并发调用调用方提供的写入函数，执行 RAG 增量计划。
/// </summary>
public static class RagIngestionExecutor
{
    /// <summary>
    /// 执行一份已生成的增量计划。
    /// 写入函数必须遵守动作幂等语义；异常和取消会向调用方传播，已完成动作不会自动回滚。
    /// </summary>
    /// <param name="plan">待执行的增量计划。</param>
    /// <param name="applyAsync">实际应用单项新增、更新或删除的异步函数。</param>
    /// <param name="options">可选的并发和动作预算。</param>
    /// <param name="cancellationToken">用于停止调度并传递给写入函数的取消令牌。</param>
    /// <returns>全部动作成功完成后的执行统计。</returns>
    /// <exception cref="ArgumentException">计划含有结构不合法的动作。</exception>
    /// <exception cref="InvalidOperationException">动作或嵌套内容总量超过执行预算。</exception>
    public static async ValueTask<RagIngestionExecutionResult> ExecuteAsync(
        RagIngestionPlan plan,
        Func<RagIngestionAction, CancellationToken, ValueTask> applyAsync,
        RagIngestionExecutionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(applyAsync);
        options ??= new RagIngestionExecutionOptions();
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<RagIngestionAction> actionSource = plan.Actions
            ?? throw new ArgumentException("计划 Actions 不能为 null。", nameof(plan));
        int actionCount = actionSource.Count;
        cancellationToken.ThrowIfCancellationRequested();
        if (actionCount < 0)
            throw new ArgumentException("计划 Actions.Count 不能为负数。", nameof(plan));
        if (actionCount > options.MaxActions)
        {
            throw new InvalidOperationException(
                $"计划动作数 {actionCount} 超过执行预算 {options.MaxActions}。");
        }

        var actions = new RagIngestionAction[actionCount];
        for (int index = 0; index < actions.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            actions[index] = actionSource[index]!;
        }

        var contentIds = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < actions.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RagIngestionAction action = ValidateActionStructure(actions[index], index);
            if (!contentIds.Add(action.ContentId))
            {
                throw new ArgumentException(
                    $"计划包含重复 ContentId '{action.ContentId}'。",
                    nameof(plan));
            }
        }

        // 先完成整份计划的合同校验，再允许任一 callback 产生外部副作用。
        var limits = new RagNestedBudgetLimits(
            options.MaxTotalChunks,
            options.MaxTotalSegments,
            options.MaxTotalEmbeddings,
            options.MaxTotalTextCharacters);
        var usage = new RagNestedBudgetUsage();
        for (int index = 0; index < actions.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            actions[index] = FreezeActionManifests(
                actions[index],
                index,
                limits,
                usage,
                cancellationToken);
        }

        int completed = 0;
        await Parallel.ForEachAsync(
            actions,
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = options.MaxConcurrency,
            },
            async (action, token) =>
            {
                await applyAsync(action, token).ConfigureAwait(false);
                Interlocked.Increment(ref completed);
            }).ConfigureAwait(false);

        return new RagIngestionExecutionResult(actionCount, completed);
    }

    private static RagIngestionAction ValidateActionStructure(
        RagIngestionAction? action,
        int index)
    {
        if (action is null)
            throw new ArgumentException($"计划 Actions[{index}] 不能为 null。", nameof(action));
        if (string.IsNullOrWhiteSpace(action.ContentId))
            throw new ArgumentException($"计划 Actions[{index}].ContentId 不能为空。", nameof(action));

        bool valid = action.Kind switch
        {
            RagIngestionActionKind.Add => action.Previous is null
                && action.Current is not null
                && MatchesContentId(action.Current, action.ContentId),
            RagIngestionActionKind.Update => action.Previous is not null
                && action.Current is not null
                && MatchesContentId(action.Previous, action.ContentId)
                && MatchesContentId(action.Current, action.ContentId),
            RagIngestionActionKind.Delete => action.Previous is not null
                && action.Current is null
                && MatchesContentId(action.Previous, action.ContentId),
            _ => false,
        };

        if (!valid)
        {
            throw new ArgumentException(
                $"内容 '{action.ContentId}' 的动作结构与类型 '{action.Kind}' 不匹配。",
                nameof(action));
        }

        return action;
    }

    private static RagIngestionAction FreezeActionManifests(
        RagIngestionAction action,
        int index,
        RagNestedBudgetLimits limits,
        RagNestedBudgetUsage usage,
        CancellationToken cancellationToken)
    {
        try
        {
            SemanticContentManifest? previous = action.Previous is null
                ? null
                : RagIngestionPreflight.FreezeManifest(
                    action.Previous,
                    limits,
                    usage,
                    $"plan.Actions[{index}].Previous",
                    nameof(action),
                    cancellationToken);
            SemanticContentManifest? current = action.Current is null
                ? null
                : RagIngestionPreflight.FreezeManifest(
                    action.Current,
                    limits,
                    usage,
                    $"plan.Actions[{index}].Current",
                    nameof(action),
                    cancellationToken);
            return action with { Previous = previous, Current = current };
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                $"计划 Actions[{index}] 的内容清单无效：{exception.Message}",
                nameof(action),
                exception);
        }
    }

    private static bool MatchesContentId(SemanticContentManifest manifest, string contentId)
        => string.Equals(manifest.Id, contentId, StringComparison.Ordinal);

    private static void ValidateOptions(RagIngestionExecutionOptions options)
    {
        if (options.MaxConcurrency <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxConcurrency 必须大于 0。");
        if (options.MaxActions <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxActions 必须大于 0。");
        if (options.MaxTotalChunks <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxTotalChunks 必须大于 0。");
        if (options.MaxTotalSegments <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxTotalSegments 必须大于 0。");
        if (options.MaxTotalEmbeddings <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxTotalEmbeddings 必须大于 0。");
        if (options.MaxTotalTextCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxTotalTextCharacters 必须大于 0。");
    }
}
