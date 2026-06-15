using System.Text;
using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Kv;

/// <summary>
/// TTL / EXPIRE / PERSIST 行为准确度场景。
/// </summary>
public sealed class TtlAccuracyScenario : KvScenarioBase
{
    /// <inheritdoc />
    public override string Name => "ttl_accuracy";

    /// <inheritdoc />
    public override Capability Required => Capability.Kv;

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunKvAsync(IKvOps ops, ScenarioContext ctx)
    {
        string scope = Scope(ctx, "ttl");
        await ops.ResetAsync(scope, ctx.Cancellation).ConfigureAwait(false);

        await ops.SetAsync(scope, "volatile", Encoding.UTF8.GetBytes("v"), DateTimeOffset.UtcNow.AddMilliseconds(350), ctx.Cancellation)
            .ConfigureAwait(false);
        long before = await ops.TtlMillisecondsAsync(scope, "volatile", ctx.Cancellation).ConfigureAwait(false);
        await Task.Delay(650, ctx.Cancellation).ConfigureAwait(false);
        var expired = await ops.GetAsync(scope, "volatile", ctx.Cancellation).ConfigureAwait(false);
        long after = await ops.TtlMillisecondsAsync(scope, "volatile", ctx.Cancellation).ConfigureAwait(false);

        await ops.SetAsync(scope, "persisted", Encoding.UTF8.GetBytes("v"), DateTimeOffset.UtcNow.AddSeconds(10), ctx.Cancellation)
            .ConfigureAwait(false);
        bool persisted = await ops.PersistAsync(scope, "persisted", ctx.Cancellation).ConfigureAwait(false);
        long persistedTtl = await ops.TtlMillisecondsAsync(scope, "persisted", ctx.Cancellation).ConfigureAwait(false);

        await ops.SetAsync(scope, "expire-call", Encoding.UTF8.GetBytes("v"), null, ctx.Cancellation).ConfigureAwait(false);
        bool expireOk = await ops.ExpireAsync(scope, "expire-call", DateTimeOffset.UtcNow.AddSeconds(5), ctx.Cancellation).ConfigureAwait(false);
        long expireTtl = await ops.TtlMillisecondsAsync(scope, "expire-call", ctx.Cancellation).ConfigureAwait(false);

        var result = MetricRow(
            before > 0 ? 1L : 0L,
            expired is null ? 1L : 0L,
            after == -2 ? 1L : 0L,
            persisted && persistedTtl == -1 ? 1L : 0L,
            expireOk && expireTtl > 0 ? 1L : 0L);
        result.Metrics["ttl_before_ms"] = before;
        result.Metrics["ttl_after_ms"] = after;
        result.Metrics["persisted_ttl_ms"] = persistedTtl;
        result.Metrics["expire_ttl_ms"] = expireTtl;
        result.Pass = before > 0 && expired is null && after == -2 && persisted && persistedTtl == -1 && expireOk && expireTtl > 0;
        return result;
    }
}
