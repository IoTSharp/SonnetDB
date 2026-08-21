namespace SonnetDB.SemanticContent;

/// <summary>
/// Semantic Content 索引状态机。
/// 异步任务的 pending/processing/retry 等调度状态不得直接写入该状态机。
/// </summary>
public static class SemanticIndexStateMachine
{
    /// <summary>
    /// 判断两个索引状态之间是否允许迁移。
    /// </summary>
    /// <param name="from">当前状态。</param>
    /// <param name="to">目标状态。</param>
    /// <returns>允许迁移时为 true。</returns>
    public static bool CanTransition(SemanticIndexState from, SemanticIndexState to)
    {
        if (!Enum.IsDefined(from) || !Enum.IsDefined(to))
            return false;
        if (from == to)
            return true;

        return from switch
        {
            SemanticIndexState.Pending => to is SemanticIndexState.Running
                or SemanticIndexState.Ready
                or SemanticIndexState.Failed
                or SemanticIndexState.Stale,
            SemanticIndexState.Running => to is SemanticIndexState.Ready
                or SemanticIndexState.Failed
                or SemanticIndexState.Stale,
            SemanticIndexState.Ready => to is SemanticIndexState.Running
                or SemanticIndexState.Stale,
            SemanticIndexState.Stale => to is SemanticIndexState.Pending
                or SemanticIndexState.Running,
            SemanticIndexState.Failed => to is SemanticIndexState.Pending
                or SemanticIndexState.Running
                or SemanticIndexState.Stale,
            _ => false,
        };
    }

    /// <summary>
    /// 执行一次状态迁移并生成新的状态快照。
    /// </summary>
    /// <param name="current">当前状态快照。</param>
    /// <param name="to">目标状态。</param>
    /// <param name="updatedUtc">更新时间；为空时使用当前 UTC 时间。</param>
    /// <param name="lastError">失败状态的错误信息。</param>
    /// <returns>迁移后的状态快照。</returns>
    /// <exception cref="InvalidOperationException">状态迁移不允许或失败状态缺少错误信息。</exception>
    public static SemanticIndexStateInfo Transition(
        SemanticIndexStateInfo current,
        SemanticIndexState to,
        DateTimeOffset? updatedUtc = null,
        string? lastError = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!CanTransition(current.State, to))
        {
            throw new InvalidOperationException(
                $"语义索引状态不能从 '{current.State}' 迁移到 '{to}'。");
        }

        if (to == SemanticIndexState.Failed && string.IsNullOrWhiteSpace(lastError))
            throw new ArgumentException("failed 状态必须提供 lastError。", nameof(lastError));

        if (to != SemanticIndexState.Failed)
            lastError = null;

        int attempt = current.Attempt;
        if (to == SemanticIndexState.Running && current.State != SemanticIndexState.Running)
            attempt = checked(attempt + 1);

        return new SemanticIndexStateInfo(
            to,
            attempt,
            lastError,
            updatedUtc ?? DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 尝试执行状态迁移，不抛出状态不允许异常。
    /// </summary>
    /// <param name="current">当前状态快照。</param>
    /// <param name="to">目标状态。</param>
    /// <param name="next">迁移后的状态快照。</param>
    /// <param name="updatedUtc">更新时间。</param>
    /// <param name="lastError">失败状态的错误信息。</param>
    /// <returns>成功迁移时为 true。</returns>
    public static bool TryTransition(
        SemanticIndexStateInfo current,
        SemanticIndexState to,
        out SemanticIndexStateInfo? next,
        DateTimeOffset? updatedUtc = null,
        string? lastError = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (!CanTransition(current.State, to)
            || to == SemanticIndexState.Failed && string.IsNullOrWhiteSpace(lastError))
        {
            next = null;
            return false;
        }

        next = Transition(current, to, updatedUtc, lastError);
        return true;
    }
}
