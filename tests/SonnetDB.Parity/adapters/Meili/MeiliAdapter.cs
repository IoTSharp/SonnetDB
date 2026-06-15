using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SonnetDB.Parity.Adapters.Meili;

/// <summary>
/// Meilisearch 竞品适配器，使用官方 HTTP API 驱动全文检索场景。
/// </summary>
public sealed class MeiliAdapter : IDataPlane, IFullTextOps
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    /// <summary>使用 <c>PARITY_MEILI_*</c> 环境变量创建 Meilisearch 连接。</summary>
    public MeiliAdapter()
    {
        _http = new HttpClient { BaseAddress = new Uri(Env("PARITY_MEILI_URL", "http://127.0.0.1:27700")) };
        string key = Env("PARITY_MEILI_MASTER_KEY", "parity-master-key");
        if (!string.IsNullOrWhiteSpace(key))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    /// <inheritdoc />
    public string BackendName => "meilisearch";

    /// <inheritdoc />
    public Capability Capabilities =>
        Capability.Fulltext |
        Capability.FulltextCjk |
        Capability.FulltextFacetFilter |
        Capability.FulltextTypoTolerant;

    /// <inheritdoc />
    public IRelationalOps Relational => throw new NotSupportedException("Meilisearch 适配器不支持关系型操作。");

    /// <inheritdoc />
    public ITimeSeriesOps TimeSeries => UnsupportedTimeSeriesOps.Instance;

    /// <inheritdoc />
    public IKvOps Kv => UnsupportedKvOps.Instance;

    /// <inheritdoc />
    public IObjectOps Objects => UnsupportedObjectOps.Instance;

    /// <inheritdoc />
    public IVectorOps Vector => UnsupportedVectorOps.Instance;

    /// <inheritdoc />
    public IMqOps Mq => UnsupportedMqOps.Instance;

    /// <inheritdoc />
    public IFullTextOps FullText => this;

    /// <inheritdoc />
    public IAnalyticalOps Analytics => UnsupportedAnalyticalOps.Instance;

    /// <summary>探测 Meilisearch 是否可达。</summary>
    public static async Task<bool> TryConnectAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(Env("PARITY_MEILI_URL", "http://127.0.0.1:27700")) };
            string key = Env("PARITY_MEILI_MASTER_KEY", "parity-master-key");
            if (!string.IsNullOrWhiteSpace(key))
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await http.GetAsync("health", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <inheritdoc />
    public async Task ResetIndexAsync(string index, FullTextIndexOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        using (var delete = await _http.DeleteAsync($"indexes/{index}", ct).ConfigureAwait(false))
        {
            if (delete.IsSuccessStatusCode)
                await WaitTaskAsync(await ReadTaskUidAsync(delete, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            else if (delete.StatusCode != HttpStatusCode.NotFound)
                await ThrowAsync(delete, ct).ConfigureAwait(false);
        }

        using (var create = await _http.PostAsJsonAsync("indexes", new { uid = index, primaryKey = "id" }, JsonOptions, ct).ConfigureAwait(false))
        {
            await EnsureSuccessOrThrowAsync(create, ct).ConfigureAwait(false);
            await WaitTaskAsync(await ReadTaskUidAsync(create, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }

        var settings = new
        {
            searchableAttributes = new[] { "title", "body", "category", "tags" },
            filterableAttributes = options.FilterableFields,
            displayedAttributes = new[] { "id", "title", "body", "category", "tags" },
        };
        using var settingsResponse = await _http.PatchAsJsonAsync($"indexes/{index}/settings", settings, JsonOptions, ct).ConfigureAwait(false);
        await EnsureSuccessOrThrowAsync(settingsResponse, ct).ConfigureAwait(false);
        await WaitTaskAsync(await ReadTaskUidAsync(settingsResponse, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task UpsertAsync(string index, IReadOnlyList<FullTextDocument> documents, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(documents);
        const int BatchSize = 500;
        for (var offset = 0; offset < documents.Count; offset += BatchSize)
        {
            var batch = documents.Skip(offset).Take(BatchSize).Select(static d => new
            {
                id = d.Id,
                title = d.Title,
                body = d.Body,
                category = d.Category,
                tags = d.Tags,
            }).ToArray();

            using var response = await _http.PostAsJsonAsync($"indexes/{index}/documents", batch, JsonOptions, ct).ConfigureAwait(false);
            await EnsureSuccessOrThrowAsync(response, ct).ConfigureAwait(false);
            await WaitTaskAsync(await ReadTaskUidAsync(response, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task DeleteDocumentAsync(string index, string id, CancellationToken ct)
    {
        using var response = await _http.DeleteAsync($"indexes/{index}/documents/{Uri.EscapeDataString(id)}", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;
        await EnsureSuccessOrThrowAsync(response, ct).ConfigureAwait(false);
        await WaitTaskAsync(await ReadTaskUidAsync(response, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FullTextHit>> SearchAsync(string index, FullTextSearchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? filter = string.IsNullOrWhiteSpace(request.CategoryFilter)
            ? null
            : $"category = '{EscapeFilter(request.CategoryFilter)}'";
        var body = new
        {
            q = request.Query,
            limit = request.TopK,
            filter,
            showRankingScore = true,
        };

        using var response = await _http.PostAsJsonAsync($"indexes/{index}/search", body, JsonOptions, ct).ConfigureAwait(false);
        await EnsureSuccessOrThrowAsync(response, ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var search = await JsonSerializer.DeserializeAsync<MeiliSearchResponse>(stream, JsonOptions, ct).ConfigureAwait(false)
            ?? new MeiliSearchResponse([]);
        return search.Hits.Select(static hit => new FullTextHit(hit.Id, hit.RankingScore, hit.Category)).ToArray();
    }

    /// <inheritdoc />
    public async Task<long> CountDocumentsAsync(string index, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"indexes/{index}/stats", ct).ConfigureAwait(false);
        await EnsureSuccessOrThrowAsync(response, ct).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var stats = await JsonSerializer.DeserializeAsync<MeiliStatsResponse>(stream, JsonOptions, ct).ConfigureAwait(false);
        return stats?.NumberOfDocuments ?? 0L;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task WaitTaskAsync(long taskUid, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            using var response = await _http.GetAsync($"tasks/{taskUid}", ct).ConfigureAwait(false);
            await EnsureSuccessOrThrowAsync(response, ct).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var task = await JsonSerializer.DeserializeAsync<MeiliTaskResponse>(stream, JsonOptions, ct).ConfigureAwait(false);
            if (task is not null && string.Equals(task.Status, "succeeded", StringComparison.OrdinalIgnoreCase))
                return;
            if (task is not null && string.Equals(task.Status, "failed", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Meilisearch task {taskUid} failed: {task.Error?.Message}");
            if (DateTimeOffset.UtcNow >= deadline)
                throw new TimeoutException($"Meilisearch task {taskUid} did not finish within 30 seconds.");
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    private static async Task<long> ReadTaskUidAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var task = await JsonSerializer.DeserializeAsync<MeiliTaskEnqueuedResponse>(stream, JsonOptions, ct).ConfigureAwait(false);
        return task?.TaskUid ?? throw new InvalidOperationException("Meilisearch response did not include taskUid.");
    }

    private static async Task EnsureSuccessOrThrowAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        await ThrowAsync(response, ct).ConfigureAwait(false);
    }

    private static async Task ThrowAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        throw new HttpRequestException($"Meilisearch returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
    }

    private static string EscapeFilter(string value)
        => value.Replace("'", "\\'", StringComparison.Ordinal);

    private static string Env(string key, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private sealed record MeiliTaskEnqueuedResponse(long TaskUid);

    private sealed record MeiliTaskResponse(string Status, MeiliTaskError? Error);

    private sealed record MeiliTaskError(string? Message);

    private sealed record MeiliSearchResponse(IReadOnlyList<MeiliHit> Hits);

    private sealed record MeiliHit(
        string Id,
        string? Category,
        [property: JsonPropertyName("_rankingScore")] double RankingScore);

    private sealed record MeiliStatsResponse(long NumberOfDocuments);
}
