using System.Text;
using SonnetDB.Parity.Adapters;

namespace SonnetDB.Parity.Scenarios.Object;

/// <summary>
/// ListObjectsV2 continuation token 分页场景。
/// </summary>
public sealed class ListObjectsV2PaginationScenario : ObjectScenarioBase
{
    /// <inheritdoc />
    public override string Name => "list_objects_v2_pagination";

    /// <inheritdoc />
    public override Capability Required => Capability.Object;

    /// <inheritdoc />
    protected override async Task<ScenarioResult> RunObjectAsync(IObjectOps ops, ScenarioContext ctx)
    {
        string bucket = Bucket(ctx, "listv2");
        await ops.ResetBucketAsync(bucket, ctx.Cancellation).ConfigureAwait(false);

        for (int i = 0; i < 7; i++)
        {
            byte[] payload = Encoding.UTF8.GetBytes("object-" + i);
            await using var input = new MemoryStream(payload, writable: false);
            _ = await ops.PutAsync(bucket, $"logs/{i:D3}.txt", input, "text/plain", ctx.Cancellation).ConfigureAwait(false);
        }

        var keys = new List<string>();
        string? token = null;
        int pages = 0;
        do
        {
            var page = await ops.ListAsync(bucket, "logs/", 3, token, ctx.Cancellation).ConfigureAwait(false);
            pages++;
            keys.AddRange(page.Objects.Select(static item => item.Key));
            token = page.NextContinuationToken;
            if (!page.IsTruncated)
                break;
        }
        while (!string.IsNullOrWhiteSpace(token));

        string expectedLast = "logs/006.txt";
        bool ordered = keys.SequenceEqual(keys.OrderBy(static key => key, StringComparer.Ordinal));
        var result = MetricRow((long)keys.Count, (long)pages, ordered ? 1L : 0L, keys.LastOrDefault() ?? "");
        result.Pass = keys.Count == 7 && pages == 3 && ordered && keys[^1] == expectedLast;
        result.Metrics["last_key"] = keys.LastOrDefault();
        return result;
    }
}
