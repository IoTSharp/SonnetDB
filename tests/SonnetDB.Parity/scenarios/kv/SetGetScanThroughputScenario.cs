using System.Diagnostics;
using System.Text;
using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Kv;

/// <summary>
/// set/get/scan 基础吞吐场景。
/// </summary>
public sealed class SetGetScanThroughputScenario : KvScenarioBase
{
    /// <inheritdoc />
    public override string Name => "set_get_scan_throughput";

    /// <inheritdoc />
    public override Capability Required => Capability.Kv | Capability.KvRangeScan;

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunKvAsync(IKvOps ops, ScenarioContext ctx)
    {
        string scope = Scope(ctx, "setgetscan");
        int count = EnvInt("PARITY_KV_SET_GET_COUNT", 2_000);
        await ops.ResetAsync(scope, ctx.Cancellation).ConfigureAwait(false);

        var sw = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            await ops.SetAsync(
                scope,
                $"key:{i:D8}",
                Encoding.UTF8.GetBytes("value-" + i),
                null,
                ctx.Cancellation).ConfigureAwait(false);
        }
        sw.Stop();
        double setPerSecond = count / Math.Max(sw.Elapsed.TotalSeconds, 0.001d);

        sw.Restart();
        int hits = 0;
        for (int i = 0; i < count; i++)
        {
            if (await ops.GetAsync(scope, $"key:{i:D8}", ctx.Cancellation).ConfigureAwait(false) is not null)
                hits++;
        }
        sw.Stop();
        double getPerSecond = count / Math.Max(sw.Elapsed.TotalSeconds, 0.001d);

        sw.Restart();
        var scanned = await ops.ScanPrefixAsync(scope, "key:", Math.Min(count, 1000), ctx.Cancellation).ConfigureAwait(false);
        sw.Stop();

        var result = MetricRow((long)count, (long)hits, (long)scanned.Count);
        result.Metrics["set_per_sec"] = setPerSecond;
        result.Metrics["get_per_sec"] = getPerSecond;
        result.Metrics["scan_ms"] = sw.Elapsed.TotalMilliseconds;
        result.Pass = hits == count && scanned.Count == Math.Min(count, 1000);
        return result;
    }
}
