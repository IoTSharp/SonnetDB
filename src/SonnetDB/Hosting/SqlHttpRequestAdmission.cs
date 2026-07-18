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

    public SqlHttpRequestAdmission(IOptions<ServerOptions> serverOptions)
    {
        var options = serverOptions.Value.SqlHttpAdmission;
        int permitLimit = options.PermitLimit;
        int queueLimit = options.QueueLimit;

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
    /// <see cref="OperationCanceledException"/>，由限流器同时移除该等待项。
    /// </summary>
    public ValueTask<RateLimitLease> AcquireAsync(string database, CancellationToken cancellationToken)
        => _limiter.AcquireAsync(database, permitCount: 1, cancellationToken);

    internal RateLimiterStatistics? GetStatistics(string database)
        => _limiter.GetStatistics(database);

    public void Dispose() => _limiter.Dispose();
}
