namespace SonnetDB.Documents.Vector;

/// <summary>一次已加载 HNSW 图重建的真实进度；不表示主文档或持久向量 KV 已修复。</summary>
/// <param name="OperationId">本次进程内操作标识。</param>
/// <param name="Index">向量索引名。</param>
/// <param name="State">queued、waiting_lock、snapshotting、building、publishing、completed、canceled 或 failed。</param>
/// <param name="ScannedEntries">已经检查的持久向量条目数，不包含尚未处理的页内条目。</param>
/// <param name="IndexedVectors">已经成功加入候选图的向量数。</param>
/// <param name="StartedAtUtc">操作创建时间。</param>
/// <param name="CompletedAtUtc">操作终止时间；运行时为空。</param>
/// <param name="ErrorCode">失败类型的稳定分类；详细原始异常由 Completion 传播。</param>
public sealed record DocumentVectorGraphRebuildProgress(
    Guid OperationId,
    string Index,
    string State,
    long ScannedEntries,
    long IndexedVectors,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    string? ErrorCode);

/// <summary>
/// 已加载 HNSW 图的进程内重建操作。进度读取不获取索引锁；调用方应等待 Completion 并保留取消令牌源。
/// </summary>
public sealed class DocumentVectorGraphRebuildOperation
{
    private DocumentVectorGraphRebuildProgress _progress;

    internal DocumentVectorGraphRebuildOperation(string index, Action<DocumentVectorGraphRebuildOperation> execute)
    {
        _progress = new(Guid.NewGuid(), index, "queued", 0, 0, DateTimeOffset.UtcNow, null, null);
        // 不将令牌传给 Task.Run，保证排队期间取消也经过工作体并发布 canceled 终态。
        Completion = ExecuteAsync(execute);
    }

    /// <summary>无需等待索引锁的不可变进度快照；总量未知时不推算百分比。</summary>
    public DocumentVectorGraphRebuildProgress Progress => Volatile.Read(ref _progress);

    /// <summary>重建完成结果；取消和失败分别传播 OperationCanceledException 与原始异常。</summary>
    public Task<DocumentVectorGraphRebuildProgress> Completion { get; }

    private async Task<DocumentVectorGraphRebuildProgress> ExecuteAsync(Action<DocumentVectorGraphRebuildOperation> execute)
    {
        // async 边界把工作体传播的 OperationCanceledException 转为真正的 Canceled Task 状态。
        await Task.Run(() => execute(this)).ConfigureAwait(false);
        return Progress;
    }

    internal void Update(string state, long scannedEntries, long indexedVectors, string? errorCode = null)
        => Volatile.Write(ref _progress, Progress with
        {
            State = state,
            ScannedEntries = scannedEntries,
            IndexedVectors = indexedVectors,
            CompletedAtUtc = state is "completed" or "canceled" or "failed" ? DateTimeOffset.UtcNow : null,
            ErrorCode = errorCode,
        });
}
