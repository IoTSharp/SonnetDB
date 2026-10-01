namespace SonnetDB.Cdc;

/// <summary>
/// 文档集合 CDC 源捕获自动调度的一次有界运行设置。
/// </summary>
public sealed record CdcDocumentSourceCaptureScheduleOptions
{
    /// <summary>本次运行最多执行的捕获批次数。</summary>
    public int MaxBatches { get; init; } = 1;

    /// <summary>本次运行最多持久化的事件数。</summary>
    public int MaxEvents { get; init; } = 256;

    /// <summary>相邻捕获批次之间的等待时间；零表示不等待。</summary>
    public TimeSpan Interval { get; init; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBatches, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxBatches, 100_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxEvents, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxEvents, 1_000_000);
        if (Interval < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(Interval), "捕获调度间隔不能为负数。");
        if (Interval > TimeSpan.FromDays(1))
            throw new ArgumentOutOfRangeException(nameof(Interval), "捕获调度间隔不能超过一天。");
    }
}

/// <summary>
/// 文档集合 CDC 源捕获自动调度的一次运行结果。
/// </summary>
/// <param name="StartSequence">本次运行开始时的源 change feed 游标。</param>
/// <param name="EndSequence">本次运行完成后可恢复的源 change feed 游标。</param>
/// <param name="CapturedEvents">本次运行成功持久化的事件数。</param>
/// <param name="CompletedBatches">本次运行完成的捕获批次数。</param>
/// <param name="HasMore">运行结束时源或批次边界之后是否仍有待捕获事件。</param>
/// <param name="ReachedBatchLimit">是否因达到最大批次数而停止。</param>
/// <param name="ReachedEventLimit">是否因达到最大事件数而停止。</param>
/// <param name="Progress">按完成顺序记录的每批捕获结果。</param>
public sealed record CdcDocumentSourceCaptureScheduleResult(
    long StartSequence,
    long EndSequence,
    int CapturedEvents,
    int CompletedBatches,
    bool HasMore,
    bool ReachedBatchLimit,
    bool ReachedEventLimit,
    IReadOnlyList<CdcDocumentSourceCaptureResult> Progress);

/// <summary>
/// 对文档集合 CDC 源捕获执行显式、有界、可取消的连续批次调度。
/// </summary>
/// <remarks>
/// 调度器只在调用 <see cref="RunAsync"/> 的任务中执行，不创建后台线程或无界循环。
/// 一次运行同时受最大批次数和最大事件数约束；达到源端末尾时提前结束。调度器不拥有
/// 捕获器、文档集合或 spool，调用方负责它们的生命周期。
/// </remarks>
public sealed class CdcDocumentSourceCaptureScheduler
{
    private readonly CdcDocumentSourceCapture _capture;
    private readonly CdcDocumentSourceCaptureScheduleOptions _options;

    /// <summary>
    /// 创建文档集合 CDC 源捕获调度器。
    /// </summary>
    /// <param name="capture">要连续推进的源捕获器。</param>
    /// <param name="options">本次调度运行的批次数、事件数和间隔边界。</param>
    public CdcDocumentSourceCaptureScheduler(
        CdcDocumentSourceCapture capture,
        CdcDocumentSourceCaptureScheduleOptions options)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _capture = capture;
        _options = options;
    }

    /// <summary>
    /// 在当前调用任务中有界推进源捕获，直到源耗尽、达到一个配置上限或被取消。
    /// </summary>
    /// <param name="cancellationToken">取消令牌；取消会停止后续批次并抛出取消异常。</param>
    /// <returns>包含总量、停止原因和每批进度的运行结果。</returns>
    public async ValueTask<CdcDocumentSourceCaptureScheduleResult> RunAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var progress = new List<CdcDocumentSourceCaptureResult>(
            capacity: Math.Min(_options.MaxBatches, 256));
        int capturedEvents = 0;
        long startSequence = 0;
        long endSequence = 0;
        bool hasMore = false;

        for (int batch = 0; batch < _options.MaxBatches; batch++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int remainingEvents = _options.MaxEvents - capturedEvents;
            if (remainingEvents <= 0)
                break;

            int batchLimit = Math.Min(remainingEvents, _capture.BatchSize);
            CdcDocumentSourceCaptureResult result = await _capture
                .CaptureBatchAsync(batchLimit, cancellationToken)
                .ConfigureAwait(false);
            if (result.CapturedEvents > remainingEvents)
                throw new InvalidDataException("CDC 源捕获调度器收到超过事件上限的批次结果。");

            if (progress.Count == 0)
                startSequence = result.StartSequence;
            endSequence = result.EndSequence;
            capturedEvents = checked(capturedEvents + result.CapturedEvents);
            progress.Add(result);
            hasMore = result.HasMore;

            if (!result.HasMore)
                break;
            if (result.CapturedEvents == 0)
                throw new InvalidDataException("CDC 源捕获调度器在仍有事件时未取得进展。");
            if (capturedEvents >= _options.MaxEvents || batch + 1 >= _options.MaxBatches)
                break;

            if (_options.Interval > TimeSpan.Zero)
                await Task.Delay(_options.Interval, cancellationToken).ConfigureAwait(false);
        }

        bool reachedBatchLimit = hasMore && progress.Count >= _options.MaxBatches;
        bool reachedEventLimit = hasMore && capturedEvents >= _options.MaxEvents;
        return new CdcDocumentSourceCaptureScheduleResult(
            startSequence,
            endSequence,
            capturedEvents,
            progress.Count,
            hasMore,
            reachedBatchLimit,
            reachedEventLimit,
            progress.ToArray());
    }
}
