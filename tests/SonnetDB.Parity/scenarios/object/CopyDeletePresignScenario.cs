using System.Net;
using System.Text;
using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Object;

/// <summary>
/// copy object、DeleteObjects 与 presigned GET 生命周期场景。
/// </summary>
public sealed class CopyDeletePresignScenario : ObjectScenarioBase
{
    /// <inheritdoc />
    public override string Name => "copy_delete_presigned_url_lifecycle";

    /// <inheritdoc />
    public override Capability Required => Capability.Object;

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunObjectAsync(IObjectOps ops, ScenarioContext ctx)
    {
        string bucket = Bucket(ctx, "copydelete");
        await ops.ResetBucketAsync(bucket, ctx.Cancellation).ConfigureAwait(false);

        byte[] payload = Encoding.UTF8.GetBytes("copy me through object storage");
        await using (var input = new MemoryStream(payload, writable: false))
            _ = await ops.PutAsync(bucket, "src/a.txt", input, "text/plain", ctx.Cancellation).ConfigureAwait(false);

        var copied = await ops.CopyAsync(bucket, "src/a.txt", "dst/a.txt", ctx.Cancellation).ConfigureAwait(false);
        var copiedRead = await ops.GetAsync(bucket, "dst/a.txt", null, ctx.Cancellation).ConfigureAwait(false);
        var deleted = await ops.DeleteManyAsync(bucket, ["src/a.txt"], ctx.Cancellation).ConfigureAwait(false);
        var missing = await ops.GetAsync(bucket, "src/a.txt", null, ctx.Cancellation).ConfigureAwait(false);

        int presignStatus = 0;
        try
        {
            string url = await ops.CreatePresignedGetUrlAsync(bucket, "dst/a.txt", TimeSpan.FromMinutes(5), ctx.Cancellation)
                .ConfigureAwait(false);
            using var http = new HttpClient();
            using var response = await http.GetAsync(url, ctx.Cancellation).ConfigureAwait(false);
            presignStatus = (int)response.StatusCode;
        }
        catch (NotSupportedException)
        {
            presignStatus = (int)HttpStatusCode.NotImplemented;
        }

        bool copiedOk = copied.SizeBytes == payload.Length
            && copiedRead is not null
            && copiedRead.Content.AsSpan().SequenceEqual(payload);
        bool deleteOk = deleted.Count == 1 && deleted[0].DeleteMarker && missing is null;
        bool presignOk = presignStatus is (int)HttpStatusCode.OK or (int)HttpStatusCode.NotImplemented;

        var result = MetricRow(copied.SizeBytes, deleteOk ? 1L : 0L, presignOk ? 1L : 0L);
        result.Pass = copiedOk && deleteOk && presignOk;
        result.Metrics["presign_status"] = presignStatus;
        return result;
    }
}
