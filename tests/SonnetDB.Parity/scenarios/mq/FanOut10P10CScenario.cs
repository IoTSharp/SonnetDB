using System.Text;
using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Mq;

/// <summary>
/// 10 producer / 10 consumer group 扇出场景。
/// </summary>
public sealed class FanOut10P10CScenario : MqScenarioBase
{
    /// <inheritdoc />
    public override string Name => "fan_out_10p_10c";

    /// <inheritdoc />
    public override Capability Required => Capability.Mq | Capability.MqConsumerGroup;

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunMqAsync(IMqOps ops, ScenarioContext ctx)
    {
        string topic = Topic(ctx, "fanout");
        int perProducer = EnvInt("PARITY_MQ_FANOUT_PER_PRODUCER", 20);
        await ops.ResetTopicAsync(topic, ctx.Cancellation).ConfigureAwait(false);

        var publishTasks = Enumerable.Range(0, 10)
            .Select(producer => ops.PublishManyAsync(
                topic,
                Enumerable.Range(0, perProducer)
                    .Select(i => new MqPublishRecord(Encoding.UTF8.GetBytes($"p{producer}:m{i}")))
                    .ToArray(),
                ctx.Cancellation))
            .ToArray();
        await Task.WhenAll(publishTasks).ConfigureAwait(false);

        int expected = 10 * perProducer;
        var pullTasks = Enumerable.Range(0, 10)
            .Select(i => ops.PullAsync(topic, "group-" + i, expected + 10, ctx.Cancellation))
            .ToArray();
        var pulled = await Task.WhenAll(pullTasks).ConfigureAwait(false);

        bool pass = pulled.All(messages => messages.Count == expected);
        var result = MetricRow((long)expected, (long)pulled.Min(static x => x.Count), (long)pulled.Max(static x => x.Count));
        result.Pass = pass;
        result.Metrics["producer_count"] = 10;
        result.Metrics["consumer_group_count"] = 10;
        return result;
    }
}
