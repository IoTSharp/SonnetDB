using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Kv;

/// <summary>
/// 16 并发客户端原子 INCR 场景。
/// </summary>
public sealed class IncrConcurrency16ClientsScenario : KvScenarioBase
{
    /// <inheritdoc />
    public override string Name => "incr_concurrency_16_clients";

    /// <inheritdoc />
    public override Capability Required => Capability.Kv | Capability.KvIncr;

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunKvAsync(IKvOps ops, ScenarioContext ctx)
    {
        string scope = Scope(ctx, "incr");
        int perClient = EnvInt("PARITY_KV_INCR_PER_CLIENT", 1_000);
        await ops.ResetAsync(scope, ctx.Cancellation).ConfigureAwait(false);

        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            for (int i = 0; i < perClient; i++)
                await ops.IncrementAsync(scope, "counter", 1, ctx.Cancellation).ConfigureAwait(false);
        }, ctx.Cancellation)).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);

        long actual = await ops.IncrementAsync(scope, "counter", 0, ctx.Cancellation).ConfigureAwait(false);
        long expected = 16L * perClient;
        var result = MetricRow(expected, actual);
        result.Pass = actual == expected;
        return result;
    }
}
