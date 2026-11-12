using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using SonnetDB.Configuration;

namespace SonnetDB.Hosting;

/// <summary>
/// 对 SQL HTTP 请求执行数据库级、有界、异步并发准入。
/// </summary>
internal sealed class SqlHttpRequestAdmission : IDisposable
{
    internal const int RetryAfterSeconds = 1;

    private readonly PartitionedRateLimiter<string> _limiter;
    private readonly TimeSpan _queueTimeout;
    private static readonly Meter AdmissionMeter = new("SonnetDB.Server", "1.0.0");
    private static readonly Counter<long> Outcomes = AdmissionMeter.CreateCounter<long>(
        "sonnetdb.sql.admission.outcomes", description: "SQL admission outcomes before execution.");
    private static readonly Histogram<double> WaitDuration = AdmissionMeter.CreateHistogram<double>(
        "sonnetdb.sql.admission.wait.duration", unit: "ms", description: "SQL admission wait including rejection, timeout and cancellation.");

    public SqlHttpRequestAdmission(IOptions<ServerOptions> serverOptions)
    {
        var options = serverOptions.Value.SqlHttpAdmission;
        int permitLimit = options.PermitLimit;
        int queueLimit = options.QueueLimit;
        ArgumentOutOfRangeException.ThrowIfLessThan(options.QueueTimeoutMilliseconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.QueueTimeoutMilliseconds, 300_000);
        _queueTimeout = TimeSpan.FromMilliseconds(options.QueueTimeoutMilliseconds);

        _limiter = PartitionedRateLimiter.Create<string, string>(
            database => RateLimitPartition.GetConcurrencyLimiter(
                database,
                _ => new ConcurrencyLimiterOptions
                {
                    PermitLimit = permitLimit,
                    QueueLimit = queueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                }),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 异步取得指定数据库的一个请求许可。队列已满时返回未取得的 lease；等待取消时抛出
    /// <see cref="OperationCanceledException"/>，由限流器同时移除该等待项。排队超时返回未取得的 lease；
    /// 期限只覆盖准入等待，不中断已经取得许可的执行。调用方取消优先于排队超时。
    /// </summary>
    public async ValueTask<RateLimitLease> AcquireAsync(string database, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        string outcome = "canceled";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_queueTimeout);
        try
        {
            RateLimitLease lease = await _limiter.AcquireAsync(database, permitCount: 1, deadline.Token)
                .ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
            {
                lease.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }
            outcome = lease.IsAcquired ? "acquired" : "rejected";
            return lease;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            outcome = "timeout";
            return TimedOutLease.Instance;
        }
        finally
        {
            RecordOutcome(outcome, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }

    private static void RecordOutcome(string outcome, double elapsedMilliseconds)
    {
        // Listener callbacks are external code: telemetry must not lose an acquired lease or replace cancellation.
        try
        {
            var tag = new KeyValuePair<string, object?>("outcome", outcome);
            Outcomes.Add(1, tag);
            WaitDuration.Record(elapsedMilliseconds, tag);
        }
        catch (Exception)
        {
            // Admission and lease ownership remain authoritative if a metrics listener fails.
        }
    }

    private sealed class TimedOutLease : RateLimitLease
    {
        internal static readonly TimedOutLease Instance = new();
        public override bool IsAcquired => false;
        public override IEnumerable<string> MetadataNames => [];
        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }

    internal RateLimiterStatistics? GetStatistics(string database)
        => _limiter.GetStatistics(database);

    public void Dispose() => _limiter.Dispose();
}
