using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Object;

/// <summary>
/// multipart 上传合并正确性场景，默认小分片，可通过环境变量放大到 5GB。
/// </summary>
public sealed class MultipartUploadScenario : ObjectScenarioBase
{
    /// <inheritdoc />
    public override string Name => "multipart_upload_5gb";

    /// <inheritdoc />
    public override Capability Required => Capability.Object | Capability.ObjectMultipart;

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunObjectAsync(IObjectOps ops, ScenarioContext ctx)
    {
        string bucket = Bucket(ctx, "multipart");
        int partSize = EnvInt("PARITY_OBJECT_MULTIPART_PART_BYTES", 5 * 1024 * 1024);
        byte[] part1 = Payload(partSize, 13102);
        byte[] part2 = Payload(partSize, 13103);
        await ops.ResetBucketAsync(bucket, ctx.Cancellation).ConfigureAwait(false);

        string uploadId = await ops.InitiateMultipartAsync(bucket, "backup/full.bin", "application/octet-stream", ctx.Cancellation)
            .ConfigureAwait(false);
        await using (var input = new MemoryStream(part1, writable: false))
            await ops.UploadPartAsync(bucket, "backup/full.bin", uploadId, 1, input, ctx.Cancellation).ConfigureAwait(false);
        await using (var input = new MemoryStream(part2, writable: false))
            await ops.UploadPartAsync(bucket, "backup/full.bin", uploadId, 2, input, ctx.Cancellation).ConfigureAwait(false);

        var completed = await ops.CompleteMultipartAsync(bucket, "backup/full.bin", uploadId, [1, 2], ctx.Cancellation)
            .ConfigureAwait(false);
        var read = await ops.GetAsync(bucket, "backup/full.bin", null, ctx.Cancellation).ConfigureAwait(false);

        bool ok = read is not null
            && completed.SizeBytes == part1.Length + part2.Length
            && read.Content.AsSpan(0, part1.Length).SequenceEqual(part1)
            && read.Content.AsSpan(part1.Length).SequenceEqual(part2);

        var result = MetricRow(completed.SizeBytes, read?.Content.Length ?? -1L, ok ? 1L : 0L);
        result.Pass = ok;
        result.Metrics["part_size_bytes"] = partSize;
        return result;
    }

    private static int EnvInt(string key, int fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : fallback;
    }
}
