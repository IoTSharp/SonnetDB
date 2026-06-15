using System.Text;
using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Mq;

/// <summary>
/// 发布、消费、确认基础场景。
/// </summary>
public sealed class PublishConsumeAckScenario : MqScenarioBase
{
    /// <inheritdoc />
    public override string Name => "publish_consume_ack";

    /// <inheritdoc />
    public override Capability Required => Capability.Mq | Capability.MqConsumerGroup;

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunMqAsync(IMqOps ops, ScenarioContext ctx)
    {
        string topic = Topic(ctx, "puback");
        await ops.ResetTopicAsync(topic, ctx.Cancellation).ConfigureAwait(false);

        await ops.PublishAsync(topic, Encoding.UTF8.GetBytes("a"), null, ctx.Cancellation).ConfigureAwait(false);
        await ops.PublishAsync(topic, Encoding.UTF8.GetBytes("b"), null, ctx.Cancellation).ConfigureAwait(false);
        var firstPull = await ops.PullAsync(topic, "workers", 10, ctx.Cancellation).ConfigureAwait(false);
        long next = await ops.AckAsync(topic, "workers", firstPull[0].Offset, ctx.Cancellation).ConfigureAwait(false);
        var secondPull = await ops.PullAsync(topic, "workers", 10, ctx.Cancellation).ConfigureAwait(false);

        bool pass = firstPull.Count == 2 &&
            Encoding.UTF8.GetString(firstPull[0].Payload) == "a" &&
            secondPull.Count == 1 &&
            Encoding.UTF8.GetString(secondPull[0].Payload) == "b";
        var result = MetricRow((long)firstPull.Count, next, (long)secondPull.Count);
        result.Pass = pass;
        return result;
    }
}
