using System.Text;
using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Kv;

/// <summary>
/// CAS 乐观锁场景。
/// </summary>
public sealed class CasOptimisticLockScenario : KvScenarioBase
{
    /// <inheritdoc />
    public override string Name => "cas_optimistic_lock";

    /// <inheritdoc />
    public override Capability Required => Capability.Kv | Capability.KvCas;

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunKvAsync(IKvOps ops, ScenarioContext ctx)
    {
        string scope = Scope(ctx, "cas");
        await ops.ResetAsync(scope, ctx.Cancellation).ConfigureAwait(false);

        await ops.SetAsync(scope, "item", Encoding.UTF8.GetBytes("v1"), null, ctx.Cancellation).ConfigureAwait(false);
        var initial = await ops.GetAsync(scope, "item", ctx.Cancellation).ConfigureAwait(false)
            ?? throw new InvalidOperationException("CAS 场景初始写入后未读到 key。");

        var fail = await ops.CompareAndSetAsync(scope, "item", initial.Version + 10, Encoding.UTF8.GetBytes("bad"), null, ctx.Cancellation)
            .ConfigureAwait(false);
        var ok = await ops.CompareAndSetAsync(scope, "item", initial.Version, Encoding.UTF8.GetBytes("v2"), null, ctx.Cancellation)
            .ConfigureAwait(false);
        var final = await ops.GetAsync(scope, "item", ctx.Cancellation).ConfigureAwait(false);

        bool finalOk = final is not null && Encoding.UTF8.GetString(final.Value) == "v2";
        var result = MetricRow(fail.Succeeded ? 1L : 0L, ok.Succeeded ? 1L : 0L, finalOk ? 1L : 0L);
        result.Metrics["initial_version"] = initial.Version;
        result.Metrics["new_version"] = ok.NewVersion;
        result.Pass = !fail.Succeeded && ok.Succeeded && finalOk;
        return result;
    }
}
