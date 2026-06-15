using System.Text;
using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Mq;

/// <summary>
/// 重启后按 offset 重放场景。
/// </summary>
public sealed class ReplayAfterRestartScenario : MqScenarioBase
{
    /// <inheritdoc />
    public override string Name => "replay_after_restart";

    /// <inheritdoc />
    public override Capability Required => Capability.Mq | Capability.MqReplayFromOffset;

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunMqAsync(IMqOps ops, ScenarioContext ctx)
    {
        string topic = Topic(ctx, "replay");
        await ops.ResetTopicAsync(topic, ctx.Cancellation).ConfigureAwait(false);
        await ops.PublishManyAsync(
            topic,
            Enumerable.Range(0, 6)
                .Select(i => new MqPublishRecord(Encoding.UTF8.GetBytes("r" + i)))
                .ToArray(),
            ctx.Cancellation).ConfigureAwait(false);

        await ops.RestartAsync(ctx.Cancellation).ConfigureAwait(false);
        var replayed = await ops.ReplayAsync(topic, 2, 10, ctx.Cancellation).ConfigureAwait(false);

        bool pass = replayed.Select(static m => m.Offset).SequenceEqual([2L, 3L, 4L, 5L]);
        var result = MetricRow((long)replayed.Count, replayed[0].Offset, replayed[^1].Offset);
        result.Pass = pass;
        return result;
    }
}
