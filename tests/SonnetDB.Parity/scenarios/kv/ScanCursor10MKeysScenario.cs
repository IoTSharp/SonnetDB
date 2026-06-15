using System.Text;
using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Kv;

/// <summary>
/// 大 keyspace 前缀扫描场景。默认轻量档可通过环境变量放大到 10M key。
/// </summary>
public sealed class ScanCursor10MKeysScenario : KvScenarioBase
{
    /// <inheritdoc />
    public override string Name => "scan_cursor_10m_keys";

    /// <inheritdoc />
    public override Capability Required => Capability.Kv | Capability.KvRangeScan;

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunKvAsync(IKvOps ops, ScenarioContext ctx)
    {
        string scope = Scope(ctx, "scan");
        int count = EnvInt("PARITY_KV_SCAN_KEYS", 10_000);
        int target = EnvInt("PARITY_KV_SCAN_LIMIT", 512);
        await ops.ResetAsync(scope, ctx.Cancellation).ConfigureAwait(false);

        for (int i = 0; i < count; i++)
        {
            string prefix = i % 10 == 0 ? "target:" : "other:";
            await ops.SetAsync(scope, prefix + i.ToString("D8"), Encoding.UTF8.GetBytes("v"), null, ctx.Cancellation)
                .ConfigureAwait(false);
        }

        var rows = await ops.ScanPrefixAsync(scope, "target:", target, ctx.Cancellation).ConfigureAwait(false);
        bool sorted = rows.Select(static r => r.Key).SequenceEqual(rows.Select(static r => r.Key).Order(StringComparer.Ordinal));
        var result = MetricRow((long)count, (long)rows.Count, sorted ? 1L : 0L);
        result.Metrics["configured_key_count"] = count;
        result.Metrics["full_10m_enabled"] = count >= 10_000_000;
        result.Pass = rows.Count == Math.Min(target, (count + 9) / 10) && sorted;
        return result;
    }
}
