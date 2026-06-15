using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Object;

/// <summary>
/// put/get 对象正确性场景，默认小载荷，可通过环境变量放大到 1GB。
/// </summary>
public sealed class PutGetObjectScenario : ObjectScenarioBase
{
    /// <inheritdoc />
    public override string Name => "putget_1gb_object";

    /// <inheritdoc />
    public override Capability Required => Capability.Object;

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunObjectAsync(IObjectOps ops, ScenarioContext ctx)
    {
        string bucket = Bucket(ctx, "putget");
        int size = EnvInt("PARITY_OBJECT_PUTGET_BYTES", 256 * 1024);
        byte[] payload = Payload(size, 13101);
        await ops.ResetBucketAsync(bucket, ctx.Cancellation).ConfigureAwait(false);

        await using (var input = new MemoryStream(payload, writable: false))
            _ = await ops.PutAsync(bucket, "objects/payload.bin", input, "application/octet-stream", ctx.Cancellation).ConfigureAwait(false);

        var read = await ops.GetAsync(bucket, "objects/payload.bin", null, ctx.Cancellation).ConfigureAwait(false);
        bool same = read is not null && payload.AsSpan().SequenceEqual(read.Content);

        var result = MetricRow((long)size, read?.SizeBytes ?? -1L, same ? 1L : 0L);
        result.Pass = same;
        result.Metrics["size_bytes"] = size;
        return result;
    }

    private static int EnvInt(string key, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
    }
}
