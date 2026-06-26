using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Endpoints;

namespace SonnetDB.Hosting;

/// <summary>
/// 周期性把 <see cref="ServerMetrics"/> + <see cref="TsdbRegistry"/> 的快照
/// 通过 <see cref="EventBroadcaster"/> 推送到 <c>metrics</c> 通道。
/// 仅在有订阅者时才生成快照，避免空跑序列化开销。
/// </summary>
internal sealed class MetricsTickService : BackgroundService
{
    private readonly EventBroadcaster _broadcaster;
    private readonly ServerMetrics _metrics;
    private readonly TsdbRegistry _registry;
    private readonly TimeSpan _interval;

    /// <summary>
    /// 构造后台服务。
    /// </summary>
    public MetricsTickService(EventBroadcaster broadcaster, ServerMetrics metrics, TsdbRegistry registry, IOptions<ServerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(broadcaster);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        _broadcaster = broadcaster;
        _metrics = metrics;
        _registry = registry;
        var seconds = Math.Max(1, options.Value.MetricsTickSeconds);
        _interval = TimeSpan.FromSeconds(seconds);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_interval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                if (_broadcaster.SubscriberCount == 0)
                    continue;
                var snapshot = BuildSnapshot();
                _broadcaster.Publish(ServerEventFactory.Metrics(snapshot));
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
    }

    private MetricsSnapshotEvent BuildSnapshot()
    {
        var dbs = _registry.ListDatabases();
        var per = new Dictionary<string, int>(dbs.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var name in dbs)
        {
            if (_registry.TryGet(name, out var db))
                per[name] = db.Segments.SegmentCount;
        }
        return new MetricsSnapshotEvent(
            UptimeSeconds: _metrics.UptimeSeconds,
            Databases: _registry.Count,
            SqlRequests: _metrics.SqlRequests,
            SqlErrors: _metrics.SqlErrors,
            RowsInserted: _metrics.RowsInserted,
            RowsReturned: _metrics.RowsReturned,
            SubscriberCount: _broadcaster.SubscriberCount,
            PerDatabaseSegments: per);
    }
}
