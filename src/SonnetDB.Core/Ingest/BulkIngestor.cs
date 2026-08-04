using System.Buffers;
using SonnetDB.Engine;
using SonnetDB.Model;

namespace SonnetDB.Ingest;

/// <summary>
/// 批量入库错误处理策略。
/// </summary>
public enum BulkErrorPolicy
{
    /// <summary>遇到第一行非法即抛出 <see cref="BulkIngestException"/>（默认）。</summary>
    FailFast,

    /// <summary>跳过非法行；返回结果中包含跳过计数。</summary>
    Skip,
}

/// <summary>批量入库结果。</summary>
public readonly record struct BulkIngestResult(int Written, int Skipped);

/// <summary>
/// 批量入库结尾的 Flush 行为档位（PR #48）。
/// </summary>
public enum BulkFlushMode
{
    /// <summary>不触发 Flush（默认，最快；MemTable 由后台 Flush 线程异步落盘）。</summary>
    None,

    /// <summary>异步触发：仅向 <see cref="BackgroundFlushWorker"/> 发信号后立即返回，不阻塞调用方。</summary>
    Async,

    /// <summary>同步触发：调用 <see cref="Tsdb.FlushNow"/>，等待落盘完成后返回。</summary>
    Sync,
}

/// <summary>
/// 统一的批量入库消费入口：把 <see cref="IPointReader"/> 的输出按批喂给 <see cref="Tsdb"/>，
/// 可选在结尾触发一次 Flush（同步 / 异步 / 不触发）。
/// </summary>
public static class BulkIngestor
{
    /// <summary>批的容量；超过即调用一次 <see cref="Tsdb.WriteMany"/>。</summary>
    public const int BatchSize = 8192;

    /// <summary>
    /// 消费 <paramref name="reader"/> 中的所有 <see cref="Point"/>，写入 <paramref name="tsdb"/>。
    /// </summary>
    /// <param name="tsdb">目标数据库实例。</param>
    /// <param name="reader">点 reader（LineProtocol/JSON/BulkValues 的任一实现）。</param>
    /// <param name="errorPolicy">错误策略。</param>
    /// <param name="flushOnComplete">写入完成后是否调用 <see cref="Tsdb.FlushNow"/>（默认 <c>false</c>）。</param>
    /// <returns>包含写入与跳过计数的 <see cref="BulkIngestResult"/>。</returns>
    /// <exception cref="ArgumentNullException">任一参数为 null 时抛出。</exception>
    /// <exception cref="BulkIngestException">FailFast 策略下解析或写入失败时抛出。</exception>
    public static BulkIngestResult Ingest(
        Tsdb tsdb,
        IPointReader reader,
        BulkErrorPolicy errorPolicy = BulkErrorPolicy.FailFast,
        bool flushOnComplete = false)
        => Ingest(tsdb, reader, errorPolicy, flushOnComplete ? BulkFlushMode.Sync : BulkFlushMode.None, CancellationToken.None);

    /// <summary>
    /// 消费 <paramref name="reader"/> 中的所有 <see cref="Point"/>，写入 <paramref name="tsdb"/>，
    /// 并按 <paramref name="flushMode"/> 控制结尾 Flush 行为（PR #48）。
    /// </summary>
    public static BulkIngestResult Ingest(
        Tsdb tsdb,
        IPointReader reader,
        BulkErrorPolicy errorPolicy,
        BulkFlushMode flushMode)
        => Ingest(tsdb, reader, errorPolicy, flushMode, CancellationToken.None);

    /// <summary>
    /// 消费全部点并按指定模式 Flush，同时在解析、分批写入和最终 Flush 前响应取消。
    /// </summary>
    /// <param name="tsdb">目标数据库实例。</param>
    /// <param name="reader">点 reader。</param>
    /// <param name="errorPolicy">错误策略。</param>
    /// <param name="flushMode">结尾 Flush 行为。</param>
    /// <param name="cancellationToken">批量摄取取消令牌。</param>
    /// <returns>包含写入与跳过计数的批量结果。</returns>
    public static BulkIngestResult Ingest(
        Tsdb tsdb,
        IPointReader reader,
        BulkErrorPolicy errorPolicy,
        BulkFlushMode flushMode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tsdb);
        ArgumentNullException.ThrowIfNull(reader);

        var pool = ArrayPool<Point>.Shared;
        var buffer = pool.Rent(BatchSize);
        int written = 0;
        int skipped = 0;
        int batchCount = 0;
        try
        {
            while (true)
            {
                // 逐点轮询，确保 TableDirect 的超时和 Cancel 不会等到整个 payload 结束。
                cancellationToken.ThrowIfCancellationRequested();
                Point point;
                try
                {
                    if (!reader.TryRead(out point))
                        break;
                }
                catch (BulkIngestException) when (errorPolicy == BulkErrorPolicy.Skip)
                {
                    skipped++;
                    continue;
                }

                buffer[batchCount++] = point;
                if (batchCount >= BatchSize)
                {
                    written += FlushBatch(tsdb, buffer, batchCount, errorPolicy, ref skipped, cancellationToken);
                    batchCount = 0;
                }
            }

            if (batchCount > 0)
                written += FlushBatch(tsdb, buffer, batchCount, errorPolicy, ref skipped, cancellationToken);
        }
        finally
        {
            Array.Clear(buffer, 0, BatchSize);
            pool.Return(buffer);
        }

        if (written > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (flushMode)
            {
                case BulkFlushMode.Sync:
                    tsdb.FlushNow();
                    break;
                case BulkFlushMode.Async:
                    tsdb.SignalFlush();
                    break;
            }
        }

        return new BulkIngestResult(written, skipped);
    }

    private static int FlushBatch(
        Tsdb tsdb,
        Point[] buffer,
        int count,
        BulkErrorPolicy policy,
        ref int skipped,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (policy == BulkErrorPolicy.FailFast)
        {
            try
            {
                return tsdb.WriteMany(buffer.AsSpan(0, count));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                throw new BulkIngestException(ex.Message, ex);
            }
        }

        // Skip：逐点写，捕获写入侧异常
        int written = 0;
        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                tsdb.Write(buffer[i]);
                written++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or BulkIngestException)
            {
                skipped++;
            }
        }
        return written;
    }
}
