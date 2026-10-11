namespace SonnetDB.Streaming;

/// <summary>自动投递遇到已耗尽批次时采用的显式策略。</summary>
public enum FileStreamingExhaustedBatchPolicy
{
    /// <summary>保留原批次、位点与投递次数，等待管理员处理。</summary>
    Retain = 0,

    /// <summary>使用已有条件持久死信入口隔离完整批次；宿主必须授权此策略。</summary>
    DeadLetter = 1,
}

/// <summary>一次自动投递运行的数量、墙钟、回调与重试边界；这些选项不修改订阅持久格式。</summary>
public sealed record FileStreamingDispatcherOptions
{
    /// <summary>本次运行最多确认或隔离的不同批次数，范围为 1 至 100,000。</summary>
    public int MaxBatches { get; init; } = 100;

    /// <summary>本次运行最多调用处理函数的次数，包含重投，范围为 1 至 1,000,000。</summary>
    public int MaxDeliveries { get; init; } = 1000;

    /// <summary>整次运行的墙钟预算，包括等待读取、处理、确认与重试，范围为 10 毫秒至 5 分钟。</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>单次处理函数的超时，范围为 10 毫秒至 5 分钟；到期后停止运行且不确认批次。</summary>
    public TimeSpan HandlerTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>处理失败后的可取消重投间隔，范围为 1 毫秒至 1 分钟。</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>耗尽批次处理策略，默认保留；死信策略只作用于订阅本身已耗尽的批次。</summary>
    public FileStreamingExhaustedBatchPolicy ExhaustedBatchPolicy { get; init; }

    /// <summary>显式死信策略使用的固定非空隔离原因，最多为 4096 UTF-8 字节。</summary>
    public string? DeadLetterReason { get; init; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxBatches, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxBatches, 100_000);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxDeliveries, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxDeliveries, 1_000_000);
        ValidateDuration(MaxDuration, TimeSpan.FromMilliseconds(10), TimeSpan.FromMinutes(5), nameof(MaxDuration));
        ValidateDuration(HandlerTimeout, TimeSpan.FromMilliseconds(10), TimeSpan.FromMinutes(5), nameof(HandlerTimeout));
        ValidateDuration(RetryDelay, TimeSpan.FromMilliseconds(1), TimeSpan.FromMinutes(1), nameof(RetryDelay));
        if (!Enum.IsDefined(ExhaustedBatchPolicy))
            throw new ArgumentOutOfRangeException(nameof(ExhaustedBatchPolicy));
        if (ExhaustedBatchPolicy == FileStreamingExhaustedBatchPolicy.DeadLetter)
            FileStreamingDeadLetterStore.ValidateReason(DeadLetterReason!);
    }

    private static void ValidateDuration(TimeSpan value, TimeSpan minimum, TimeSpan maximum, string name)
    {
        if (value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, "时间边界超出允许范围。");
    }
}

/// <summary>一次有界自动投递运行停止的原因。</summary>
public enum FileStreamingDispatcherStopReason
{
    /// <summary>发布已完成，全部接受的事件已确认或按显式策略隔离。</summary>
    Completed = 0,

    /// <summary>读取前观察到订阅消费已暂停。</summary>
    Paused = 1,

    /// <summary>达到本次运行的确认或隔离批次数上限。</summary>
    BatchLimitReached = 2,

    /// <summary>达到本次运行的处理函数调用次数上限，未确认批次保留。</summary>
    DeliveryLimitReached = 3,

    /// <summary>达到本次运行墙钟预算；未成功返回确认的批次仍需按订阅恢复合同核查。</summary>
    TimeBudgetReached = 4,

    /// <summary>处理函数超时；已发出取消，不确认或自动重投仍在执行的函数。</summary>
    HandlerTimedOut = 5,

    /// <summary>订阅批次已耗尽持久投递次数，默认保留等待管理员处理。</summary>
    DeliveryAttemptsExhausted = 6,
}

/// <summary>一次有界自动投递运行的统计；确认数只计成功返回的持久确认。</summary>
/// <param name="StopReason">停止原因。</param>
/// <param name="Deliveries">本次处理函数调用次数，包含失败和重投。</param>
/// <param name="FailedDeliveries">已观察到处理函数异常的次数，不包含尚未收敛的超时调用。</param>
/// <param name="AcknowledgedBatches">成功返回持久确认的批次数。</param>
/// <param name="DeadLetteredBatches">成功返回条件持久隔离的批次数。</param>
/// <param name="LastDeliveryId">本次最后处理或隔离的稳定批次标识；未处理时为空。</param>
public sealed record FileStreamingDispatcherResult(
    FileStreamingDispatcherStopReason StopReason,
    int Deliveries,
    int FailedDeliveries,
    int AcknowledgedBatches,
    int DeadLetteredBatches,
    string? LastDeliveryId);
