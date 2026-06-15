using System.Text;
using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Mq;

/// <summary>
/// 多消费者组 offset 互不影响场景。
/// </summary>
public sealed class ConsumerGroupOffsetScenario : MqScenarioBase
{
    /// <inheritdoc />
    public override string Name => "consumer_group_offset";

    /// <inheritdoc />
    public override Capability Required => Capability.Mq | Capability.MqConsumerGroup;

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunMqAsync(IMqOps ops, ScenarioContext ctx)
    {
        string topic = Topic(ctx, "groups");
        await ops.ResetTopicAsync(topic, ctx.Cancellation).ConfigureAwait(false);
        await ops.PublishManyAsync(
            topic,
            Enumerable.Range(0, 5)
                .Select(i => new MqPublishRecord(Encoding.UTF8.GetBytes("m" + i)))
                .ToArray(),
            ctx.Cancellation).ConfigureAwait(false);

        var groupA = await ops.PullAsync(topic, "a", 5, ctx.Cancellation).ConfigureAwait(false);
        await ops.AckAsync(topic, "a", groupA[2].Offset, ctx.Cancellation).ConfigureAwait(false);
        var groupANext = await ops.PullAsync(topic, "a", 5, ctx.Cancellation).ConfigureAwait(false);
        var groupB = await ops.PullAsync(topic, "b", 5, ctx.Cancellation).ConfigureAwait(false);

        bool pass = groupANext.Select(static m => m.Offset).SequenceEqual([3L, 4L]) &&
            groupB.Select(static m => m.Offset).SequenceEqual([0L, 1L, 2L, 3L, 4L]);
        var result = MetricRow((long)groupANext.Count, (long)groupB.Count, groupANext[0].Offset, groupB[0].Offset);
        result.Pass = pass;
        return result;
    }
}
