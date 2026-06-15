using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Object;

/// <summary>
/// 多 offset range read 正确性场景。
/// </summary>
public sealed class RangeReadOffsetsScenario : ObjectScenarioBase
{
    /// <inheritdoc />
    public override string Name => "range_read_offsets";

    /// <inheritdoc />
    public override Capability Required => Capability.Object;

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunObjectAsync(IObjectOps ops, ScenarioContext ctx)
    {
        string bucket = Bucket(ctx, "range");
        byte[] payload = Payload(256 * 1024, 13104);
        await ops.ResetBucketAsync(bucket, ctx.Cancellation).ConfigureAwait(false);
        await using (var input = new MemoryStream(payload, writable: false))
            _ = await ops.PutAsync(bucket, "firmware/v1.bin", input, "application/octet-stream", ctx.Cancellation).ConfigureAwait(false);

        var ranges = new[] { (0L, 64L), (4096L, 128L), (payload.Length - 257L, 257L) };
        long total = 0;
        int matches = 0;
        foreach (var (offset, length) in ranges)
        {
            var read = await ops.GetAsync(bucket, "firmware/v1.bin", new ObjectRange(offset, length), ctx.Cancellation)
                .ConfigureAwait(false);
            total += read?.Content.Length ?? 0;
            if (read is not null && read.Content.AsSpan().SequenceEqual(payload.AsSpan((int)offset, (int)length)))
                matches++;
        }

        var result = MetricRow((long)ranges.Length, (long)matches, total);
        result.Pass = matches == ranges.Length;
        return result;
    }
}
