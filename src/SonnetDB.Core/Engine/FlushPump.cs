using System.Collections.Concurrent;
using SonnetDB.Memory;
using SonnetDB.Storage.Segments;

namespace SonnetDB.Engine;

/// <summary>
/// Flush 泵：单线程 FIFO 消费"已密封 MemTable"队列，在 <b>锁外</b>（不持有 <c>Tsdb._writeSync</c>）
/// 执行编码落盘 + WAL checkpoint/roll/recycle，完成后回调 <see cref="Tsdb"/> 原子发布新段并
/// 从 sealing 列表移除该表。
/// <para>
/// 设计目的（ROADMAP #204 / C2）：把 flush 的重活（编码 16MB + 写文件 + 2×fsync + WAL 回收）
/// 移出全局写锁，使写入者在 flush 期间可对新活跃 MemTable 并发写入。
/// </para>
/// <para>
/// 破死锁：泵线程只取 SegmentManager / WalSegmentSet 各自的内部锁，<b>绝不</b>获取
/// <c>_writeSync</c>。密封（swap + 入队）是 <c>_writeSync</c> 内的 O(1) 操作，因此写锁内
/// 触发的 flush（如 int→float schema 提升）只需密封即可返回，不会与泵形成锁序环。
/// </para>
/// <para>FIFO 单线程保证 checkpoint LSN 单调、段 ID 顺序发布，等价于 single-flight。</para>
/// </summary>
internal sealed class FlushPump : IDisposable
{
    /// <summary>队列中的一次 flush 请求。</summary>
    internal sealed class FlushRequest
    {
        public FlushRequest(MemTable sealedTable, long sealLsn, long segmentId)
        {
            SealedTable = sealedTable;
            SealLsn = sealLsn;
            SegmentId = segmentId;
        }

        /// <summary>被密封、待落盘的不可变 MemTable。</summary>
        public MemTable SealedTable { get; }

        /// <summary>
        /// 密封瞬间捕获的 LastLsn。seal 已在锁内 Roll，被密封数据完整落在 lastLsn = sealLsn 的段；
        /// 泵在段编码成功后追加 checkpoint(sealLsn) 并 RecycleUpTo(sealLsn)，精确回收这些段，
        /// 绝不触及 roll 之后的并发写入（startLsn > sealLsn）。
        /// </summary>
        public long SealLsn { get; }

        /// <summary>为本次 flush 预分配的段 ID。</summary>
        public long SegmentId { get; }

        /// <summary>flush 完成后由泵写入的段构建结果（成功且非空表时非 null）。</summary>
        public SegmentBuildResult? Result { get; set; }

        /// <summary>完成信号（成功或失败都会 set）。调用方可 await 以获得同步语义。</summary>
        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Tsdb _owner;
    private readonly BlockingCollection<FlushRequest> _queue = new(new ConcurrentQueue<FlushRequest>());
    private readonly Thread _thread;
    private long _completedCount;
    private long _failureCount;
    private volatile bool _disposed;

    public FlushPump(Tsdb owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
        _thread = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "SonnetDB-FlushPump",
        };
        _thread.Start();
    }

    /// <summary>累计成功完成的 flush 次数（诊断/测试）。</summary>
    public long CompletedCount => Interlocked.Read(ref _completedCount);

    /// <summary>累计失败的 flush 次数（诊断/测试）。</summary>
    public long FailureCount => Interlocked.Read(ref _failureCount);

    /// <summary>当前排队中（含正在处理）的 flush 请求数。</summary>
    public int PendingCount => _queue.Count;

    /// <summary>把一次已密封的 flush 请求入队；泵线程将按 FIFO 处理。</summary>
    public void Enqueue(FlushRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        _queue.Add(request);
    }

    /// <summary>停止入队，等待泵处理完已入队请求并退出线程。</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _queue.CompleteAdding();
        // 等待泵线程把队列排空并退出（Dispose 语义要求已入队的 flush 落盘完成）。
        _thread.Join();
        _queue.Dispose();
    }

    private void WorkerLoop()
    {
        foreach (var request in _queue.GetConsumingEnumerable())
        {
            try
            {
                _owner.ExecutePumpFlush(request);
                Interlocked.Increment(ref _completedCount);
                request.Completion.TrySetResult();
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _failureCount);
                _owner.OnPumpFlushFailed(request, ex);
                request.Completion.TrySetException(ex);
            }
        }
    }
}
