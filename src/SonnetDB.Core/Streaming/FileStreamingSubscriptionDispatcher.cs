using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace SonnetDB.Streaming;

/// <summary>显式运行真实文件订阅的串行、有界自动投递与重投驱动器。</summary>
/// <remarks>
/// <para>调用方提供业务处理函数并负责幂等副作用；函数成功后才持久确认。此入口不提供远程调度或业务 exactly-once。</para>
/// <para>处理函数在线程池执行。超时或取消不会强制终止函数，驱动器保留同一订阅对象的执行租约直到函数真正结束；运行返回或抛出后，宿主可等待 <see cref="HandlerCompletion"/> 后关闭订阅。</para>
/// <para>同一订阅对象上的驱动器共享租约。宿主必须让驱动器独占消费，不能并行直接读取、确认、重置或隔离批次；发布、watermark 和暂停操作仍由原订阅串行管理。</para>
/// <para>存储异常按原订阅合同传播；确认失败可能结果未知，必须关闭并重开核查，不能自动重试确认。</para>
/// </remarks>
public sealed class FileStreamingSubscriptionDispatcher
{
    private static readonly ConditionalWeakTable<FileStreamingSubscription, DispatchLease> Leases = new();
    private readonly FileStreamingSubscription _subscription;
    private readonly FileStreamingDispatcherOptions _options;
    private readonly DispatchLease _lease;

    /// <summary>创建一个显式自动投递驱动器，不取得订阅所有权，也不会自行启动后台任务。</summary>
    /// <param name="subscription">已打开、由宿主独占消费的真实文件订阅。</param>
    /// <param name="options">本次运行的数量与墙钟边界；省略时使用默认值。</param>
    public FileStreamingSubscriptionDispatcher(
        FileStreamingSubscription subscription,
        FileStreamingDispatcherOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        _options = options ?? new FileStreamingDispatcherOptions();
        _options.Validate();
        _subscription = subscription;
        _lease = Leases.GetValue(subscription, static _ => new DispatchLease());
    }

    /// <summary>最近处理函数的收敛任务，已观察并吸收函数异常；超时或取消后还包括执行租约释放。</summary>
    /// <remarks>函数忽略取消且一直不返回时此任务不会完成，宿主不得重新消费或关闭它仍在使用的资源。</remarks>
    public Task HandlerCompletion => Volatile.Read(ref _lease.HandlerCompletion);

    /// <summary>串行处理有界批次；普通处理异常按固定间隔重投，超时停止且未确认批次保留。</summary>
    /// <param name="handler">处理函数，收到稳定批次身份与可取消令牌；正常完成表示允许持久确认。</param>
    /// <param name="cancellationToken">取消本次运行及处理函数的令牌；取消时抛出取消异常且不开始新的确认。</param>
    /// <returns>停止原因和成功返回的确认、隔离及投递统计。</returns>
    /// <exception cref="InvalidOperationException">同一订阅对象已经有运行或尚未结束的处理函数。</exception>
    public async ValueTask<FileStreamingDispatcherResult> RunAsync(
        Func<StreamingDeliveryBatch, CancellationToken, ValueTask> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);
        cancellationToken.ThrowIfCancellationRequested();
        if (Interlocked.CompareExchange(ref _lease.Active, 1, 0) != 0)
            throw new InvalidOperationException("同一文件订阅已有自动投递运行或尚未结束的处理函数。");

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_options.MaxDuration);
        var elapsed = Stopwatch.StartNew();
        int deliveries = 0;
        int failedDeliveries = 0;
        int acknowledgedBatches = 0;
        int deadLetteredBatches = 0;
        string? lastDeliveryId = null;
        Task? pendingHandler = null;
        CancellationTokenSource? pendingHandlerCancellation = null;
        bool committing = false;
        try
        {
            // 每轮至少投递一次或隔离一个已耗尽批次；显式数量上限与共享墙钟令牌同时约束循环。
            int maxOperations = checked(_options.MaxBatches + _options.MaxDeliveries);
            for (int operation = 0; operation < maxOperations; operation++)
            {
                budget.Token.ThrowIfCancellationRequested();
                if (elapsed.Elapsed >= _options.MaxDuration)
                    return Result(FileStreamingDispatcherStopReason.TimeBudgetReached);
                FileStreamingSubscriptionStatus status = await _subscription.GetStatusAsync(budget.Token).ConfigureAwait(false);
                if (status.PublishingCompleted && status.PendingEventCount == 0)
                    return Result(FileStreamingDispatcherStopReason.Completed);
                if (status.ConsumptionPaused)
                    return Result(FileStreamingDispatcherStopReason.Paused);
                if (acknowledgedBatches + deadLetteredBatches >= _options.MaxBatches)
                    return Result(FileStreamingDispatcherStopReason.BatchLimitReached);
                if (status.DeliveryAttemptsExhausted)
                {
                    lastDeliveryId = status.InFlightDeliveryId;
                    if (_options.ExhaustedBatchPolicy == FileStreamingExhaustedBatchPolicy.Retain)
                        return Result(FileStreamingDispatcherStopReason.DeliveryAttemptsExhausted);
                    committing = true;
                    await _subscription.MoveExhaustedBatchToDeadLetterAsync(
                        status.InFlightDeliveryId!, status.StateRevision, status.InFlightAttempt,
                        _options.DeadLetterReason!, budget.Token).ConfigureAwait(false);
                    committing = false;
                    deadLetteredBatches++;
                    continue;
                }

                if (deliveries >= _options.MaxDeliveries)
                    return Result(FileStreamingDispatcherStopReason.DeliveryLimitReached);
                StreamingDeliveryBatch? batch = await _subscription.ReadBatchAsync(budget.Token).ConfigureAwait(false);
                if (batch is null)
                    return Result(FileStreamingDispatcherStopReason.Completed);
                lastDeliveryId = batch.DeliveryId;
                deliveries++;
                var handlerCancellation = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
                handlerCancellation.CancelAfter(_options.HandlerTimeout);
                var handlerElapsed = Stopwatch.StartNew();
                Task handlerTask = Task.Run(async () =>
                    await handler(batch, handlerCancellation.Token).ConfigureAwait(false), CancellationToken.None);
                Volatile.Write(ref _lease.HandlerCompletion, ObserveHandlerAsync(handlerTask));
                bool succeeded = false;
                try
                {
                    await handlerTask.WaitAsync(handlerCancellation.Token).ConfigureAwait(false);
                    if (elapsed.Elapsed >= _options.MaxDuration || handlerElapsed.Elapsed >= _options.HandlerTimeout)
                    {
                        handlerCancellation.Cancel();
                        pendingHandler = handlerTask;
                        pendingHandlerCancellation = handlerCancellation;
                        return Result(elapsed.Elapsed >= _options.MaxDuration
                            ? FileStreamingDispatcherStopReason.TimeBudgetReached
                            : FileStreamingDispatcherStopReason.HandlerTimedOut);
                    }
                    handlerCancellation.Token.ThrowIfCancellationRequested();
                    succeeded = true;
                }
                catch (OperationCanceledException) when (handlerCancellation.IsCancellationRequested)
                {
                    // 不继续等待不协作的函数，也不释放其消费租约，防止超时函数与重投并发。
                    pendingHandler = handlerTask;
                    pendingHandlerCancellation = handlerCancellation;
                    cancellationToken.ThrowIfCancellationRequested();
                    return Result(budget.IsCancellationRequested
                        ? FileStreamingDispatcherStopReason.TimeBudgetReached
                        : FileStreamingDispatcherStopReason.HandlerTimedOut);
                }
                catch (Exception)
                {
                    failedDeliveries++;
                }
                finally
                {
                    if (pendingHandlerCancellation is null)
                        handlerCancellation.Dispose();
                }

                budget.Token.ThrowIfCancellationRequested();
                if (succeeded)
                {
                    committing = true;
                    await _subscription.AcknowledgeAsync(batch.DeliveryId, budget.Token).ConfigureAwait(false);
                    committing = false;
                    acknowledgedBatches++;
                }
                else if (batch.Attempt < _subscription.Options.MaxDeliveryAttempts
                    && deliveries < _options.MaxDeliveries)
                {
                    await Task.Delay(_options.RetryDelay, budget.Token).ConfigureAwait(false);
                }
            }

            return Result(FileStreamingDispatcherStopReason.DeliveryLimitReached);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested && !committing)
        {
            return Result(FileStreamingDispatcherStopReason.TimeBudgetReached);
        }
        finally
        {
            if (pendingHandler is not null)
            {
                ReleaseAfterHandler(pendingHandler, pendingHandlerCancellation!);
            }
            else
            {
                Interlocked.Exchange(ref _lease.Active, 0);
            }
        }

        FileStreamingDispatcherResult Result(FileStreamingDispatcherStopReason reason)
            => new(reason, deliveries, failedDeliveries, acknowledgedBatches, deadLetteredBatches, lastDeliveryId);
    }

    private void ReleaseAfterHandler(Task handlerTask, CancellationTokenSource cancellation)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // 必须先发布收敛任务再释放租约，避免旧运行覆盖新运行刚发布的处理任务。
        Volatile.Write(ref _lease.HandlerCompletion, completion.Task);
        _ = ReleaseAfterHandlerAsync(handlerTask, cancellation, completion);
    }

    private async Task ReleaseAfterHandlerAsync(
        Task handlerTask,
        CancellationTokenSource cancellation,
        TaskCompletionSource completion)
    {
        try
        {
            await ObserveHandlerAsync(handlerTask).ConfigureAwait(false);
        }
        finally
        {
            cancellation.Dispose();
            Interlocked.Exchange(ref _lease.Active, 0);
            completion.TrySetResult();
        }
    }

    private static async Task ObserveHandlerAsync(Task handlerTask)
    {
        try
        {
            await handlerTask.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // 处理函数的异常由运行统计反映；脱离运行的超时函数也必须观察异常，且永远不补做确认。
        }
    }

    private sealed class DispatchLease
    {
        internal int Active;
        internal Task HandlerCompletion = Task.CompletedTask;
    }
}
