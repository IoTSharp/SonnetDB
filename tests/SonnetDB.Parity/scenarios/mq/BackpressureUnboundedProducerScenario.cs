using System.Diagnostics;
using System.Text;
using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Mq;

/// <summary>
/// 无界生产者压力场景：验证后端可持续接受批量追加并完整重放。
/// </summary>
public sealed class BackpressureUnboundedProducerScenario : MqScenarioBase
{
    /// <inheritdoc />
    public override string Name => "backpressure_unbounded_producer";

    /// <inheritdoc />
    public override Capability Required => Capability.Mq | Capability.MqReplayFromOffset;

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunMqAsync(IMqOps ops, ScenarioContext ctx)
    {
        string topic = Topic(ctx, "pressure");
        int count = EnvInt("PARITY_MQ_BACKPRESSURE_COUNT", 2_000);
        await ops.ResetTopicAsync(topic, ctx.Cancellation).ConfigureAwait(false);

        var sw = Stopwatch.StartNew();
        const int BatchSize = 200;
        for (int start = 0; start < count; start += BatchSize)
        {
            var batch = Enumerable.Range(start, Math.Min(BatchSize, count - start))
                .Select(i => new MqPublishRecord(Encoding.UTF8.GetBytes("bp-" + i)))
                .ToArray();
            await ops.PublishManyAsync(topic, batch, ctx.Cancellation).ConfigureAwait(false);
        }

        sw.Stop();
        var replayed = await ops.ReplayAsync(topic, 0, count + 10, ctx.Cancellation).ConfigureAwait(false);
        bool pass = replayed.Count == count && replayed[0].Offset == 0 && replayed[^1].Offset == count - 1;

        var result = MetricRow((long)count, (long)replayed.Count);
        result.Pass = pass;
        result.Metrics["publish_per_sec"] = count / Math.Max(sw.Elapsed.TotalSeconds, 0.001d);
        return result;
    }
}
