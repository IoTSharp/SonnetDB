using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Data.ObjectStorage;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Hosting;
using SonnetDB.Json;
using SonnetDB.ObjectStorage;
using SonnetDB.SemanticSearch;
using Xunit;

namespace SonnetDB.Tests;

public sealed class SemanticSearchEndpointTests : IAsyncLifetime
{
    private const string AdminToken = "admin-semantic-token";
    private WebApplication? _app;
    private string? _baseUrl;
    private string? _dataRoot;

    public async Task InitializeAsync()
    {
        _dataRoot = Path.Combine(Path.GetTempPath(), "sonnetdb-semantic-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dataRoot);
        var options = CreateOptions(_dataRoot, "managed");

        _app = TestServerHost.Build(options, services =>
            services.AddSingleton<IMultimodalEmbeddingProvider>(new FakeMultimodalEmbeddingProvider()));
        await _app.StartAsync();
        var addresses = _app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel 未暴露监听地址。");
        _baseUrl = addresses.Addresses.First();

        using var client = CreateClient();
        var create = await client.PostAsJsonAsync(
            "/v1/db",
            new CreateDatabaseRequest("images"),
            ServerJsonContext.Default.CreateDatabaseRequest);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (_dataRoot is not null && Directory.Exists(_dataRoot))
        {
            try { Directory.Delete(_dataRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SemanticSearch_ManagedBackend_TextAndImageSearchWork()
    {
        using var client = CreateClient();

        var status = await client.GetFromJsonAsync(
            "/v1/semantic-search/status",
            ServerJsonContext.Default.SemanticSearchStatusResponse);
        Assert.NotNull(status);
        Assert.True(status!.Ready);
        Assert.Equal("managed", status.EffectiveBackend);
        Assert.Contains("text-to-image", status.Capabilities);

        var redPut = await PutImageAsync(client, "red-car", [255, 1, 1], "red.jpg");
        Assert.Equal(HttpStatusCode.Created, redPut.StatusCode);
        var bluePut = await PutImageAsync(client, "blue-car", [1, 1, 255], "blue.jpg");
        Assert.Equal(HttpStatusCode.Created, bluePut.StatusCode);

        var textSearch = await client.PostAsJsonAsync(
            "/v1/db/images/images/search/text",
            new ImageTextSearchRequest("red vehicle", TopK: 2),
            ServerJsonContext.Default.ImageTextSearchRequest);
        Assert.Equal(HttpStatusCode.OK, textSearch.StatusCode);
        var textResult = await textSearch.Content.ReadFromJsonAsync(ServerJsonContext.Default.ImageSearchResponse);
        Assert.NotNull(textResult);
        Assert.Equal("text", textResult!.QueryKind);
        Assert.Equal("red-car", textResult.Hits[0].Id);
        Assert.True(textResult.Hits[0].Score > textResult.Hits[1].Score);

        using var imageSearchRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/v1/db/images/images/search/image?topK=1")
        {
            Content = CreateImageContent([1, 9, 9]),
        };
        var imageSearch = await client.SendAsync(imageSearchRequest);
        Assert.Equal(HttpStatusCode.OK, imageSearch.StatusCode);
        var imageResult = await imageSearch.Content.ReadFromJsonAsync(ServerJsonContext.Default.ImageSearchResponse);
        Assert.NotNull(imageResult);
        Assert.Equal("blue-car", Assert.Single(imageResult!.Hits).Id);

        var info = await client.GetFromJsonAsync(
            "/v1/db/images/images/red-car",
            ServerJsonContext.Default.ImageInfoResponse);
        Assert.NotNull(info);
        Assert.Equal("red.jpg", info!.FileName);
        Assert.Equal(3, info.SizeBytes);

        byte[] original = await client.GetByteArrayAsync("/v1/db/images/images/red-car/content");
        Assert.Equal([255, 1, 1], original);

        var delete = await client.DeleteAsync("/v1/db/images/images/red-car");
        Assert.Equal(HttpStatusCode.OK, delete.StatusCode);
        var missing = await client.GetAsync("/v1/db/images/images/red-car");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task SemanticSearch_ReadOnlyToken_CannotIngestImage()
    {
        using var readOnly = CreateClient("readonly-semantic-token");
        var response = await PutImageAsync(readOnly, "denied", [255], "denied.jpg");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SemanticSearch_InvalidProviderOutput_ReturnsServiceUnavailable()
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync(
            "/v1/db/images/images/search/text",
            new ImageTextSearchRequest("invalid-provider-output"),
            ServerJsonContext.Default.ImageTextSearchRequest);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task SemanticSearch_FilteredSearch_UsesPrefilteredAnnAndMatchesAllMetadata()
    {
        using var client = CreateClient();
        await CreateSemanticBucketAsync(client, "north-images");
        await CreateSemanticBucketAsync(client, "south-images");

        var targetPut = await PutBucketImageAsync(
            client,
            "north-images",
            "fleet/target.png",
            [1, 2, 3],
            metadata: new Dictionary<string, string>
            {
                ["owner"] = "ops",
                ["region"] = "north",
            },
            tags: new Dictionary<string, string>
            {
                ["class"] = "truck",
                ["lane"] = "1",
            });
        Assert.Equal(HttpStatusCode.OK, targetPut.StatusCode);
        _ = await WaitForProcessingAsync(client, "north-images", "fleet/target.png", "completed");

        for (int i = 0; i < 8; i++)
        {
            string key = $"fleet/distractor-{i}.png";
            var put = await PutBucketImageAsync(
                client,
                "south-images",
                key,
                [255, 2, 3],
                metadata: new Dictionary<string, string>
                {
                    ["owner"] = "ops",
                    ["region"] = "south",
                },
                tags: new Dictionary<string, string> { ["class"] = "car" });
            Assert.Equal(HttpStatusCode.OK, put.StatusCode);
            _ = await WaitForProcessingAsync(client, "south-images", key, "completed");
        }

        var textRequest = new ImageTextSearchRequest("red", TopK: 1)
        {
            Explain = true,
            Filter = new ImageSearchFilter(
                SourceBucket: "north-images",
                SourceKeyPrefix: "fleet/",
                ContentType: "image/png",
                Metadata: new Dictionary<string, string>
                {
                    ["owner"] = "ops",
                    ["region"] = "north",
                },
                Tags: new Dictionary<string, string>
                {
                    ["class"] = "truck",
                    ["lane"] = "1",
                }),
        };
        var textSearch = await client.PostAsJsonAsync(
            "/v1/db/images/images/search/text",
            textRequest,
            ServerJsonContext.Default.ImageTextSearchRequest);
        Assert.Equal(HttpStatusCode.OK, textSearch.StatusCode);
        var textResult = await textSearch.Content.ReadFromJsonAsync(ServerJsonContext.Default.ImageSearchResponse);
        Assert.NotNull(textResult);
        Assert.Equal("managed", textResult!.Backend);
        Assert.Equal("prefiltered-ann", textResult.SearchMode);
        Assert.Equal(1, textResult.CandidateCount);
        Assert.Equal(1, textResult.FilteredCandidateCount);
        var textHit = Assert.Single(textResult.Hits);
        Assert.Equal("north-images", textHit.SourceBucket);
        Assert.Equal("fleet/target.png", textHit.SourceKey);
        Assert.Equal("north", textHit.Metadata!["region"]);
        Assert.Equal("truck", textHit.Tags!["class"]);

        using var imageRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/v1/db/images/images/search/image?topK=1&sourceBucket=north-images&sourceKeyPrefix=fleet%2F&contentType=image%2Fpng&metadata.owner=ops&tag.class=truck&explain=true")
        {
            Content = CreateImageContent([255]),
        };
        var imageSearch = await client.SendAsync(imageRequest);
        Assert.Equal(HttpStatusCode.OK, imageSearch.StatusCode);
        var imageResult = await imageSearch.Content.ReadFromJsonAsync(ServerJsonContext.Default.ImageSearchResponse);
        Assert.Equal("fleet/target.png", Assert.Single(imageResult!.Hits).SourceKey);
        Assert.Equal("prefiltered-ann", imageResult.SearchMode);

        var prefixOnlySearch = await client.PostAsJsonAsync(
            "/v1/db/images/images/search/text",
            new ImageTextSearchRequest("red", TopK: 1)
            {
                Explain = true,
                Filter = new ImageSearchFilter(SourceKeyPrefix: "fleet/target"),
            },
            ServerJsonContext.Default.ImageTextSearchRequest);
        var prefixOnlyResult = await prefixOnlySearch.Content.ReadFromJsonAsync(
            ServerJsonContext.Default.ImageSearchResponse);
        Assert.Equal("exact-filtered", prefixOnlyResult!.Backend);
        Assert.Equal("exact-filtered-fallback", prefixOnlyResult.SearchMode);
        Assert.Equal(9, prefixOnlyResult.CandidateCount);
        Assert.Equal(1, prefixOnlyResult.FilteredCandidateCount);

        var noMatchRequest = textRequest with
        {
            Filter = textRequest.Filter! with
            {
                Metadata = new Dictionary<string, string>
                {
                    ["owner"] = "ops",
                    ["region"] = "south",
                },
            },
        };
        var noMatch = await client.PostAsJsonAsync(
            "/v1/db/images/images/search/text",
            noMatchRequest,
            ServerJsonContext.Default.ImageTextSearchRequest);
        var noMatchResult = await noMatch.Content.ReadFromJsonAsync(ServerJsonContext.Default.ImageSearchResponse);
        Assert.Empty(noMatchResult!.Hits);
        Assert.Equal(0, noMatchResult.FilteredCandidateCount);

        var updateTags = await client.PutAsJsonAsync(
            "/v1/db/images/s3/north-images/fleet/target.png?tagging",
            new ObjectTagsRequest(new Dictionary<string, string>
            {
                ["class"] = "truck",
                ["lane"] = "2",
            }),
            ServerJsonContext.Default.ObjectTagsRequest);
        Assert.Equal(HttpStatusCode.OK, updateTags.StatusCode);
        Assert.True(updateTags.Headers.Contains("x-sonnetdb-processing-job-id"));
        _ = await WaitForProcessingAsync(client, "north-images", "fleet/target.png", "completed");

        var staleTagSearch = await client.PostAsJsonAsync(
            "/v1/db/images/images/search/text",
            textRequest,
            ServerJsonContext.Default.ImageTextSearchRequest);
        var staleTagResult = await staleTagSearch.Content.ReadFromJsonAsync(
            ServerJsonContext.Default.ImageSearchResponse);
        Assert.Empty(staleTagResult!.Hits);

        var updatedTagRequest = textRequest with
        {
            Filter = textRequest.Filter! with
            {
                Tags = new Dictionary<string, string>
                {
                    ["class"] = "truck",
                    ["lane"] = "2",
                },
            },
        };
        var updatedTagSearch = await client.PostAsJsonAsync(
            "/v1/db/images/images/search/text",
            updatedTagRequest,
            ServerJsonContext.Default.ImageTextSearchRequest);
        var updatedTagResult = await updatedTagSearch.Content.ReadFromJsonAsync(
            ServerJsonContext.Default.ImageSearchResponse);
        Assert.Equal("fleet/target.png", Assert.Single(updatedTagResult!.Hits).SourceKey);

        var peerPut = await PutBucketImageAsync(
            client,
            "north-images",
            "fleet/peer.png",
            [1, 3, 4],
            metadata: new Dictionary<string, string>
            {
                ["owner"] = "ops",
                ["region"] = "north",
            },
            tags: new Dictionary<string, string>
            {
                ["class"] = "truck",
                ["lane"] = "2",
            });
        Assert.Equal(HttpStatusCode.OK, peerPut.StatusCode);
        _ = await WaitForProcessingAsync(client, "north-images", "fleet/peer.png", "completed");

        var similarSearch = await client.PostAsJsonAsync(
            $"/v1/db/images/images/{Uri.EscapeDataString(textHit.Id)}/similar",
            new SimilarImageSearchRequest(TopK: 1)
            {
                Explain = true,
                Filter = updatedTagRequest.Filter,
            },
            ServerJsonContext.Default.SimilarImageSearchRequest);
        var similarResult = await similarSearch.Content.ReadFromJsonAsync(
            ServerJsonContext.Default.ImageSearchResponse);
        Assert.Equal("prefiltered-ann", similarResult!.SearchMode);
        Assert.Equal(2, similarResult.CandidateCount);
        Assert.Equal(1, similarResult.FilteredCandidateCount);
        Assert.Equal("fleet/peer.png", Assert.Single(similarResult.Hits).SourceKey);
        Assert.DoesNotContain(similarResult.Hits, hit => hit.Id == textHit.Id);
    }

    [Fact]
    public async Task SemanticSearch_HighCardinalityIndexedFilter_UsesPagedExactFallbackAndObservesCancellation()
    {
        const int documentCount = 4097;
        Assert.True(_app!.Services.GetRequiredService<TsdbRegistry>().TryGet("images", out var tsdb));
        var semanticImages = _app.Services.GetRequiredService<SemanticImageSearchService>();
        _ = await semanticImages.SearchTextAsync(
            "images",
            tsdb,
            "red",
            topK: 1,
            minScore: null,
            filter: null,
            explain: false,
            CancellationToken.None);

        DocumentCollectionSchema schema = Assert.Single(
            tsdb.Documents.Catalog.Snapshot(),
            static candidate => candidate.Name.StartsWith("__semantic_images_", StringComparison.Ordinal));
        DocumentCollectionStore store = tsdb.Documents.Open(schema.Name);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IEnumerable<DocumentWriteRequest> Documents()
        {
            for (int i = 0; i < documentCount; i++)
            {
                string id = $"paged-{i:D5}";
                var document = new SemanticImageDocument(
                    id,
                    $"paged/{i:D5}.png",
                    $"{i:D5}.png",
                    "image/png",
                    SizeBytes: 1,
                    Sha256: new string('a', 64),
                    SourceUri: null,
                    Profile: "fake-siglip2-test",
                    Dimensions: 3,
                    Embedding: [1f],
                    now,
                    now,
                    ObjectBucket: "paged-images",
                    Metadata: new Dictionary<string, string> { ["owner"] = "ops" });
                yield return new DocumentWriteRequest(
                    id,
                    JsonSerializer.Serialize(document, ServerJsonContext.Default.SemanticImageDocument));
            }
        }

        DocumentWriteResult write = store.InsertMany(Documents());
        Assert.Equal(documentCount, write.Inserted);

        var filter = new ImageSearchFilter(
            Metadata: new Dictionary<string, string> { ["owner"] = "ops" });
        ImageSearchResponse result = await semanticImages.SearchTextAsync(
            "images",
            tsdb,
            "red",
            topK: 1,
            minScore: null,
            filter,
            explain: true,
            CancellationToken.None);

        Assert.Equal("exact-filtered", result.Backend);
        Assert.Equal("exact-filtered-fallback", result.SearchMode);
        Assert.Equal(documentCount, result.CandidateCount);
        Assert.Empty(result.Hits);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => semanticImages.SearchTextAsync(
            "images",
            tsdb,
            "red",
            topK: 1,
            minScore: null,
            filter,
            explain: false,
            canceled.Token));
    }

    [Fact]
    public async Task SemanticSearch_ExistingCollectionWithoutFilterIndexes_RebuildsOnSearch()
    {
        using var client = CreateClient();
        const string bucket = "legacy-filter-images";
        await CreateSemanticBucketAsync(client, bucket);

        var put = await PutBucketImageAsync(
            client,
            bucket,
            "camera.png",
            CreatePng(16, 16, new Rgb24(220, 20, 20)),
            metadata: new Dictionary<string, string> { ["owner"] = "ops" });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        _ = await WaitForProcessingAsync(client, bucket, "camera.png", "completed");

        Assert.True(_app!.Services.GetRequiredService<TsdbRegistry>().TryGet("images", out var tsdb));
        var existing = Assert.Single(
            tsdb.Documents.Catalog.Snapshot(),
            static schema => schema.Name.StartsWith("__semantic_images_", StringComparison.Ordinal));
        Assert.Equal(3, existing.Indexes.Count);
        foreach (var index in existing.Indexes)
            Assert.True(tsdb.Documents.DropIndex(existing.Name, index.Name));
        Assert.Empty(tsdb.Documents.Catalog.TryGet(existing.Name)!.Indexes);

        var search = await client.PostAsJsonAsync(
            "/v1/db/images/images/search/text",
            new ImageTextSearchRequest("red", TopK: 1)
            {
                Explain = true,
                Filter = new ImageSearchFilter(
                    Metadata: new Dictionary<string, string> { ["owner"] = "ops" }),
            },
            ServerJsonContext.Default.ImageTextSearchRequest);

        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        var result = await search.Content.ReadFromJsonAsync(ServerJsonContext.Default.ImageSearchResponse);
        Assert.Equal("prefiltered-ann", result!.SearchMode);
        Assert.Equal("camera.png", Assert.Single(result.Hits).SourceKey);

        var upgraded = tsdb.Documents.Catalog.TryGet(existing.Name);
        Assert.NotNull(upgraded);
        Assert.Equal(3, upgraded!.Indexes.Count);
        Assert.Contains(upgraded.Indexes, static index => index.Path == "$.objectBucket");
        Assert.Contains(upgraded.Indexes, static index => index.Path == "$.metadata");
        Assert.Contains(upgraded.Indexes, static index => index.Path == "$.tags");
    }

    [Fact]
    public async Task SemanticSearch_SimilarById_ExcludesSourceAndExplainsAnnSearch()
    {
        using var client = CreateClient();
        Assert.Equal(HttpStatusCode.Created, (await PutImageAsync(client, "reference", [255], "reference.jpg")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PutImageAsync(client, "peer", [255], "peer.jpg")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PutImageAsync(client, "other", [1], "other.jpg")).StatusCode);

        var similar = await client.PostAsJsonAsync(
            "/v1/db/images/images/reference/similar",
            new SimilarImageSearchRequest(TopK: 2) { Explain = true },
            ServerJsonContext.Default.SimilarImageSearchRequest);
        Assert.Equal(HttpStatusCode.OK, similar.StatusCode);
        var result = await similar.Content.ReadFromJsonAsync(ServerJsonContext.Default.ImageSearchResponse);
        Assert.NotNull(result);
        Assert.Equal("image", result!.QueryKind);
        Assert.Equal("managed", result.Backend);
        Assert.Equal("ann", result.SearchMode);
        Assert.Equal(3, result.CandidateCount);
        Assert.Equal(2, result.FilteredCandidateCount);
        Assert.Equal("peer", result.Hits[0].Id);
        Assert.DoesNotContain(result.Hits, static hit => hit.Id == "reference");

        var ordinary = await client.PostAsJsonAsync(
            "/v1/db/images/images/search/text",
            new ImageTextSearchRequest("red", TopK: 1),
            ServerJsonContext.Default.ImageTextSearchRequest);
        var ordinaryResult = await ordinary.Content.ReadFromJsonAsync(ServerJsonContext.Default.ImageSearchResponse);
        Assert.Null(ordinaryResult!.SearchMode);
        Assert.Null(ordinaryResult.CandidateCount);
        Assert.Null(ordinaryResult.FilteredCandidateCount);

        var missing = await client.PostAsJsonAsync(
            "/v1/db/images/images/missing/similar",
            new SimilarImageSearchRequest(),
            ServerJsonContext.Default.SimilarImageSearchRequest);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task SemanticSearch_BucketOptions_AsyncIngestionThumbnailAndDeleteSyncWork()
    {
        using var client = CreateClient();
        const string bucket = "camera-images";
        var createBucket = await client.PutAsJsonAsync(
            $"/v1/db/images/s3/{bucket}",
            new ObjectBucketCreateRequest("camera-images"),
            ServerJsonContext.Default.ObjectBucketCreateRequest);
        Assert.Equal(HttpStatusCode.OK, createBucket.StatusCode);

        var defaults = await client.GetFromJsonAsync(
            $"/v1/db/images/s3/{bucket}?semantic",
            ServerJsonContext.Default.ObjectBucketSemanticOptionsResponse);
        Assert.NotNull(defaults);
        Assert.False(defaults!.AsyncIngestionEnabled);
        Assert.False(defaults.ThumbnailEnabled);

        byte[] image = CreatePng(64, 32, new Rgb24(220, 20, 20));
        var disabledPut = await PutBucketImageAsync(client, bucket, "disabled.png", image);
        Assert.Equal(HttpStatusCode.OK, disabledPut.StatusCode);
        Assert.False(disabledPut.Headers.Contains("x-sonnetdb-processing-job-id"));
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync($"/v1/db/images/s3/{bucket}/disabled.png?processing")).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await PutBucketImageAsync(client, bucket, "disabled-delete.png", image)).StatusCode);
        var disabledDelete = await client.DeleteAsync(
            $"/v1/db/images/s3/{bucket}/disabled-delete.png");
        Assert.Equal(HttpStatusCode.NoContent, disabledDelete.StatusCode);
        Assert.False(disabledDelete.Headers.Contains("x-sonnetdb-processing-job-id"));

        var enable = await client.PutAsJsonAsync(
            $"/v1/db/images/s3/{bucket}?semantic",
            new ObjectBucketSemanticOptionsRequest(
                AsyncIngestionEnabled: true,
                ThumbnailEnabled: true,
                ThumbnailMaxWidth: 32,
                ThumbnailMaxHeight: 32,
                ThumbnailQuality: 75),
            ServerJsonContext.Default.ObjectBucketSemanticOptionsRequest);
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);

        var put = await PutBucketImageAsync(client, bucket, "nested/red-camera.png", image);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.True(put.Headers.Contains("x-sonnetdb-processing-job-id"));

        ObjectProcessingStatusResponse completed = await WaitForProcessingAsync(
            client,
            bucket,
            "nested/red-camera.png",
            "completed");
        Assert.Equal("upsert", completed.Operation);
        Assert.NotNull(completed.SemanticImageId);
        Assert.NotNull(completed.ThumbnailUrl);

        var thumbnail = await client.GetAsync(
            $"/v1/db/images/s3/{bucket}/nested/red-camera.png?thumbnail");
        Assert.Equal(HttpStatusCode.OK, thumbnail.StatusCode);
        Assert.Equal("image/webp", thumbnail.Content.Headers.ContentType?.MediaType);
        using (Image decoded = Image.Load(await thumbnail.Content.ReadAsByteArrayAsync()))
        {
            Assert.True(decoded.Width <= 32);
            Assert.True(decoded.Height <= 32);
        }

        var search = await client.PostAsJsonAsync(
            "/v1/db/images/images/search/text",
            new ImageTextSearchRequest("red", TopK: 10),
            ServerJsonContext.Default.ImageTextSearchRequest);
        Assert.Equal(HttpStatusCode.OK, search.StatusCode);
        var searchResult = await search.Content.ReadFromJsonAsync(ServerJsonContext.Default.ImageSearchResponse);
        var hit = Assert.Single(searchResult!.Hits);
        Assert.Equal(bucket, hit.SourceBucket);
        Assert.Equal("nested/red-camera.png", hit.SourceKey);
        Assert.NotNull(hit.ThumbnailUrl);

        var delete = await client.DeleteAsync($"/v1/db/images/s3/{bucket}/nested/red-camera.png");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        await WaitForNoSearchHitsAsync(client);

        var backfill = await client.PostAsync($"/v1/db/images/s3/{bucket}?semantic", content: null);
        Assert.Equal(HttpStatusCode.OK, backfill.StatusCode);
        var backfillResult = await backfill.Content.ReadFromJsonAsync(
            ServerJsonContext.Default.ObjectBucketSemanticBackfillResponse);
        Assert.NotNull(backfillResult);
        Assert.Equal(1, backfillResult!.QueuedObjects);
        _ = await WaitForProcessingAsync(client, bucket, "disabled.png", "completed");

        var repeatedBackfill = await client.PostAsync($"/v1/db/images/s3/{bucket}?semantic", content: null);
        Assert.Equal(HttpStatusCode.OK, repeatedBackfill.StatusCode);
        var repeatedBackfillResult = await repeatedBackfill.Content.ReadFromJsonAsync(
            ServerJsonContext.Default.ObjectBucketSemanticBackfillResponse);
        Assert.NotNull(repeatedBackfillResult);
        Assert.Equal(1, repeatedBackfillResult!.ScannedObjects);
        Assert.Equal(0, repeatedBackfillResult.QueuedObjects);
        Assert.Equal(1, repeatedBackfillResult.SkippedObjects);
    }

    [Fact]
    public async Task SemanticSearch_StaleDeletionAndNewerSourceVersion_PreserveIndexedVersion()
    {
        using var client = CreateClient();
        const string bucket = "versioned-images";
        const string key = "camera.png";
        await CreateSemanticBucketAsync(client, bucket);

        byte[] firstImage = CreatePng(16, 16, new Rgb24(220, 20, 20));
        var firstPut = await PutBucketImageAsync(client, bucket, key, firstImage);
        var firstInfo = await firstPut.Content.ReadFromJsonAsync(ServerJsonContext.Default.ObjectInfoResponse);
        Assert.NotNull(firstInfo);
        _ = await WaitForProcessingAsync(client, bucket, key, "completed");

        byte[] indexedImage = CreatePng(16, 16, new Rgb24(20, 20, 220));
        var secondPut = await PutBucketImageAsync(client, bucket, key, indexedImage);
        var secondInfo = await secondPut.Content.ReadFromJsonAsync(ServerJsonContext.Default.ObjectInfoResponse);
        Assert.NotNull(secondInfo);
        ObjectProcessingStatusResponse secondCompleted = await WaitForProcessingAsync(
            client,
            bucket,
            key,
            "completed");
        Assert.NotNull(secondCompleted.SemanticImageId);

        Assert.True(_app!.Services.GetRequiredService<TsdbRegistry>().TryGet("images", out var tsdb));
        var semanticImages = _app.Services.GetRequiredService<SemanticImageSearchService>();
        Assert.False(await semanticImages.DeleteStoredObjectVersionAsync(
            "images",
            tsdb,
            secondCompleted.SemanticImageId!,
            bucket,
            key,
            firstInfo!.VersionId,
            CancellationToken.None));

        var search = await client.PostAsJsonAsync(
            "/v1/db/images/images/search/text",
            new ImageTextSearchRequest("red", TopK: 10),
            ServerJsonContext.Default.ImageTextSearchRequest);
        var searchResult = await search.Content.ReadFromJsonAsync(ServerJsonContext.Default.ImageSearchResponse);
        var hit = Assert.Single(searchResult!.Hits);
        Assert.Equal(secondInfo!.VersionId, hit.SourceVersionId);

        byte[] newerUnindexedImage = CreatePng(16, 16, new Rgb24(20, 220, 20));
        using (var content = new MemoryStream(newerUnindexedImage, writable: false))
        {
            _ = await new SndbObjectStore(tsdb).PutObjectAsync(
                bucket,
                key,
                content,
                "image/png");
        }

        byte[] indexedContent = await client.GetByteArrayAsync(
            $"/v1/db/images/images/{secondCompleted.SemanticImageId}/content");
        Assert.Equal(indexedImage, indexedContent);
    }

    [Fact]
    public async Task SemanticSearch_LifecycleExpiration_CleansIndexAndThumbnail()
    {
        using var client = CreateClient();
        const string bucket = "lifecycle-images";
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PutAsJsonAsync(
                $"/v1/db/images/s3/{bucket}",
                new ObjectBucketCreateRequest("camera-images"),
                ServerJsonContext.Default.ObjectBucketCreateRequest)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PutAsJsonAsync(
                $"/v1/db/images/s3/{bucket}?semantic",
                new ObjectBucketSemanticOptionsRequest(
                    AsyncIngestionEnabled: true,
                    ThumbnailEnabled: true),
                ServerJsonContext.Default.ObjectBucketSemanticOptionsRequest)).StatusCode);

        byte[] image = CreatePng(32, 16, new Rgb24(220, 20, 20));
        Assert.Equal(
            HttpStatusCode.OK,
            (await PutBucketImageAsync(client, bucket, "expired-camera.png", image)).StatusCode);
        ObjectProcessingStatusResponse completed = await WaitForProcessingAsync(
            client,
            bucket,
            "expired-camera.png",
            "completed");
        Assert.NotNull(completed.ThumbnailUrl);
        Assert.Equal(1, await GetThumbnailObjectCountAsync(client));

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PutAsJsonAsync(
                $"/v1/db/images/s3/{bucket}?lifecycle",
                new ObjectLifecycleRequest(ExpireCurrentAfterDays: 0),
                ServerJsonContext.Default.ObjectLifecycleRequest)).StatusCode);
        string connectionString =
            $"Data Source=sonnetdb+http://{new Uri(_baseUrl!).Authority}/images;Token={AdminToken};Timeout=30;Protocol=rest";
        using var objectClient = new SndbObjectStorageClient(connectionString);
        var result = await objectClient.ApplyLifecycleAsync(bucket);
        Assert.Equal(1, result.ExpiredCurrentObjects);
        Assert.Equal(1, result.SemanticCleanupJobs);
        var expired = Assert.Single(result.ExpiredObjects);
        Assert.Equal("expired-camera.png", expired.Key);
        Assert.Equal("image/png", expired.ContentType);

        await WaitForNoSearchHitsAsync(client);
        await WaitForNoThumbnailObjectsAsync(client);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync($"/v1/db/images/s3/{bucket}/expired-camera.png?thumbnail")).StatusCode);
    }

    [Fact]
    public async Task SemanticSearch_AutoBackend_UsesUsearchOnSupportedRuntime()
    {
        if (!USearchSemanticIndexRegistry.IsSupportedPlatform)
            return;

        string root = Path.Combine(Path.GetTempPath(), "sonnetdb-usearch-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        WebApplication? app = null;
        try
        {
            app = TestServerHost.Build(CreateOptions(root, "auto"), services =>
                services.AddSingleton<IMultimodalEmbeddingProvider>(new FakeMultimodalEmbeddingProvider()));
            await app.StartAsync();
            string baseUrl = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.First();
            using var client = CreateClient(baseUrl, AdminToken);
            var create = await client.PostAsJsonAsync(
                "/v1/db",
                new CreateDatabaseRequest("usearchimages"),
                ServerJsonContext.Default.CreateDatabaseRequest);
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await PutImageAsync(client, "sample", [255], "sample.jpg", "usearchimages")).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await PutImageAsync(client, "reference", [255], "reference.jpg", "usearchimages")).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await PutImageAsync(client, "sample", [1], "sample-updated.jpg", "usearchimages")).StatusCode);

            var search = await client.PostAsJsonAsync(
                "/v1/db/usearchimages/images/search/text",
                new ImageTextSearchRequest("red", TopK: 2),
                ServerJsonContext.Default.ImageTextSearchRequest);
            Assert.Equal(HttpStatusCode.OK, search.StatusCode);
            var result = await search.Content.ReadFromJsonAsync(ServerJsonContext.Default.ImageSearchResponse);
            Assert.Equal("usearch", result!.Backend);
            Assert.Equal("reference", result.Hits[0].Id);
            Assert.Equal("sample", result.Hits[1].Id);
            Assert.Equal([1], await client.GetByteArrayAsync("/v1/db/usearchimages/images/sample/content"));

            for (int i = 0; i < 5; i++)
            {
                Assert.Equal(
                    HttpStatusCode.Created,
                    (await PutImageAsync(client, $"old-{i}", [255], $"old-{i}.jpg", "usearchimages")).StatusCode);
            }

            Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("/v1/db/usearchimages")).StatusCode);
            var recreate = await client.PostAsJsonAsync(
                "/v1/db",
                new CreateDatabaseRequest("usearchimages"),
                ServerJsonContext.Default.CreateDatabaseRequest);
            Assert.Equal(HttpStatusCode.Created, recreate.StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await PutImageAsync(client, "fresh", [1], "fresh.jpg", "usearchimages")).StatusCode);

            var afterRecreate = await client.PostAsJsonAsync(
                "/v1/db/usearchimages/images/search/text",
                new ImageTextSearchRequest("red", TopK: 1),
                ServerJsonContext.Default.ImageTextSearchRequest);
            Assert.Equal(HttpStatusCode.OK, afterRecreate.StatusCode);
            var afterRecreateResult = await afterRecreate.Content.ReadFromJsonAsync(ServerJsonContext.Default.ImageSearchResponse);
            Assert.Equal("fresh", Assert.Single(afterRecreateResult!.Hits).Id);
        }
        finally
        {
            if (app is not null)
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SemanticSearch_StrictUsearchFilteredSearch_FailsClosed()
    {
        if (!USearchSemanticIndexRegistry.IsSupportedPlatform)
            return;

        string root = Path.Combine(Path.GetTempPath(), "sonnetdb-usearch-filter-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        WebApplication? app = null;
        try
        {
            ServerOptions options = CreateOptions(root, "usearch");
            options.SemanticSearch.FallbackToManaged = false;
            app = TestServerHost.Build(options, services =>
                services.AddSingleton<IMultimodalEmbeddingProvider>(new FakeMultimodalEmbeddingProvider()));
            await app.StartAsync();
            string baseUrl = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.First();
            using var client = CreateClient(baseUrl, AdminToken);
            var create = await client.PostAsJsonAsync(
                "/v1/db",
                new CreateDatabaseRequest("strictusearch"),
                ServerJsonContext.Default.CreateDatabaseRequest);
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);

            var search = await client.PostAsJsonAsync(
                "/v1/db/strictusearch/images/search/text",
                new ImageTextSearchRequest("red", TopK: 1)
                {
                    Explain = true,
                    Filter = new ImageSearchFilter(
                        Metadata: new Dictionary<string, string> { ["owner"] = "ops" }),
                },
                ServerJsonContext.Default.ImageTextSearchRequest);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, search.StatusCode);
            var error = await search.Content.ReadFromJsonAsync(ServerJsonContext.Default.ErrorResponse);
            Assert.Equal("semantic_provider_unavailable", error!.Error);
            Assert.Contains("不支持带过滤的向量检索", error.Message, StringComparison.Ordinal);
            Assert.Contains("已禁用 managed 回退", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (app is not null)
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SemanticSearch_PersistedProcessingJob_RecoversAfterRestart()
    {
        string root = Path.Combine(Path.GetTempPath(), "sonnetdb-semantic-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        WebApplication? first = null;
        WebApplication? second = null;
        try
        {
            first = TestServerHost.Build(CreateOptions(root, "managed"), services =>
                services.AddSingleton<IMultimodalEmbeddingProvider>(new BlockingMultimodalEmbeddingProvider()));
            await first.StartAsync();
            using (var client = CreateClient(GetBaseUrl(first), AdminToken))
            {
                var create = await client.PostAsJsonAsync(
                    "/v1/db",
                    new CreateDatabaseRequest("recovery"),
                    ServerJsonContext.Default.CreateDatabaseRequest);
                Assert.Equal(HttpStatusCode.Created, create.StatusCode);
                Assert.Equal(
                    HttpStatusCode.OK,
                    (await client.PutAsJsonAsync(
                        "/v1/db/recovery/s3/images",
                        new ObjectBucketCreateRequest("camera-images"),
                        ServerJsonContext.Default.ObjectBucketCreateRequest)).StatusCode);
                Assert.Equal(
                    HttpStatusCode.OK,
                    (await client.PutAsJsonAsync(
                        "/v1/db/recovery/s3/images?semantic",
                        new ObjectBucketSemanticOptionsRequest(AsyncIngestionEnabled: true),
                        ServerJsonContext.Default.ObjectBucketSemanticOptionsRequest)).StatusCode);
                byte[] image = CreatePng(16, 16, new Rgb24(200, 10, 10));
                Assert.Equal(
                    HttpStatusCode.OK,
                    (await PutBucketImageAsync(client, "images", "recover.png", image, "recovery")).StatusCode);
                _ = await WaitForProcessingAsync(client, "images", "recover.png", "processing", "recovery");
            }

            await first.StopAsync();
            await first.DisposeAsync();
            first = null;

            second = TestServerHost.Build(CreateOptions(root, "managed"), services =>
                services.AddSingleton<IMultimodalEmbeddingProvider>(new FakeMultimodalEmbeddingProvider()));
            await second.StartAsync();
            using var recoveredClient = CreateClient(GetBaseUrl(second), AdminToken);
            ObjectProcessingStatusResponse recovered = await WaitForProcessingAsync(
                recoveredClient,
                "images",
                "recover.png",
                "completed",
                "recovery");
            Assert.True(recovered.Attempts >= 2);
            Assert.NotNull(recovered.SemanticImageId);
        }
        finally
        {
            if (first is not null)
            {
                await first.StopAsync();
                await first.DisposeAsync();
            }
            if (second is not null)
            {
                await second.StopAsync();
                await second.DisposeAsync();
            }
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SemanticSearch_BucketOptions_CopyAndMultipartScheduleProcessing()
    {
        using var client = CreateClient();
        const string bucket = "derived-images";
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PutAsJsonAsync(
                $"/v1/db/images/s3/{bucket}",
                new ObjectBucketCreateRequest("camera-images"),
                ServerJsonContext.Default.ObjectBucketCreateRequest)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PutAsJsonAsync(
                $"/v1/db/images/s3/{bucket}?semantic",
                new ObjectBucketSemanticOptionsRequest(AsyncIngestionEnabled: true),
                ServerJsonContext.Default.ObjectBucketSemanticOptionsRequest)).StatusCode);

        byte[] image = CreatePng(16, 16, new Rgb24(180, 20, 20));
        Assert.Equal(
            HttpStatusCode.OK,
            (await PutBucketImageAsync(client, bucket, "source.png", image)).StatusCode);
        _ = await WaitForProcessingAsync(client, bucket, "source.png", "completed");

        using var copyRequest = new HttpRequestMessage(
            HttpMethod.Put,
            $"/v1/db/images/s3/{bucket}/copied.png");
        copyRequest.Headers.TryAddWithoutValidation("x-amz-copy-source", $"/{bucket}/source.png");
        var copy = await client.SendAsync(copyRequest);
        Assert.Equal(HttpStatusCode.OK, copy.StatusCode);
        Assert.True(copy.Headers.Contains("x-sonnetdb-processing-job-id"));
        _ = await WaitForProcessingAsync(client, bucket, "copied.png", "completed");

        var initiate = await client.PostAsJsonAsync(
            $"/v1/db/images/s3/{bucket}/multipart.png?uploads",
            new MultipartUploadCreateRequest("image/png"),
            ServerJsonContext.Default.MultipartUploadCreateRequest);
        var upload = await initiate.Content.ReadFromJsonAsync(
            ServerJsonContext.Default.MultipartUploadCreateResponse);
        Assert.NotNull(upload);
        int split = image.Length / 2;
        await PutMultipartBytesAsync(client, bucket, upload!.UploadId, 1, image[..split]);
        await PutMultipartBytesAsync(client, bucket, upload.UploadId, 2, image[split..]);
        var complete = await client.PostAsJsonAsync(
            $"/v1/db/images/s3/{bucket}/multipart.png?uploadId={upload.UploadId}",
            new MultipartCompleteRequest([1, 2]),
            ServerJsonContext.Default.MultipartCompleteRequest);
        Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
        var completedObject = await complete.Content.ReadFromJsonAsync(ServerJsonContext.Default.ObjectInfoResponse);
        Assert.Equal("image/png", completedObject!.ContentType);
        Assert.True(complete.Headers.Contains("x-sonnetdb-processing-job-id"));
        _ = await WaitForProcessingAsync(client, bucket, "multipart.png", "completed");
    }

    private static ServerOptions CreateOptions(string dataRoot, string backend)
        => new()
        {
            DataRoot = dataRoot,
            AutoLoadExistingDatabases = true,
            Tokens = new Dictionary<string, string>
            {
                [AdminToken] = ServerRoles.Admin,
                ["readonly-semantic-token"] = ServerRoles.ReadOnly,
            },
            SemanticSearch = new SemanticSearchOptions
            {
                Enabled = true,
                Provider = "siglip2-onnx",
                Profile = "fake-siglip2-test",
                Dimensions = 3,
                MaxImageBytes = 1024,
                Backend = backend,
            },
        };

    private HttpClient CreateClient(string token = AdminToken)
        => CreateClient(_baseUrl!, token);

    private static HttpClient CreateClient(string baseUrl, string token)
    {
        var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<HttpResponseMessage> PutImageAsync(
        HttpClient client,
        string id,
        byte[] bytes,
        string fileName,
        string database = "images")
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/v1/db/{Uri.EscapeDataString(database)}/images/{Uri.EscapeDataString(id)}?fileName={Uri.EscapeDataString(fileName)}")
        {
            Content = CreateImageContent(bytes),
        };
        return await client.SendAsync(request);
    }

    private static ByteArrayContent CreateImageContent(byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return content;
    }

    private static async Task<HttpResponseMessage> PutBucketImageAsync(
        HttpClient client,
        string bucket,
        string key,
        byte[] image,
        string database = "images",
        IReadOnlyDictionary<string, string>? metadata = null,
        IReadOnlyDictionary<string, string>? tags = null)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"/v1/db/{Uri.EscapeDataString(database)}/s3/{Uri.EscapeDataString(bucket)}/{key}")
        {
            Content = new ByteArrayContent(image),
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        if (metadata is not null)
        {
            foreach (var pair in metadata)
                request.Headers.TryAddWithoutValidation("x-amz-meta-" + pair.Key, pair.Value);
        }
        if (tags is not null)
        {
            string header = string.Join(
                '&',
                tags.Select(static pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));
            request.Headers.TryAddWithoutValidation("x-amz-tagging", header);
        }
        return await client.SendAsync(request);
    }

    private static async Task CreateSemanticBucketAsync(HttpClient client, string bucket)
    {
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PutAsJsonAsync(
                $"/v1/db/images/s3/{bucket}",
                new ObjectBucketCreateRequest("semantic-test"),
                ServerJsonContext.Default.ObjectBucketCreateRequest)).StatusCode);
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.PutAsJsonAsync(
                $"/v1/db/images/s3/{bucket}?semantic",
                new ObjectBucketSemanticOptionsRequest(AsyncIngestionEnabled: true),
                ServerJsonContext.Default.ObjectBucketSemanticOptionsRequest)).StatusCode);
    }

    private static byte[] CreatePng(int width, int height, Rgb24 color)
    {
        using var image = new Image<Rgb24>(width, height, color);
        using var output = new MemoryStream();
        image.SaveAsPng(output);
        return output.ToArray();
    }

    private static async Task PutMultipartBytesAsync(
        HttpClient client,
        string bucket,
        string uploadId,
        int partNumber,
        byte[] content)
    {
        using var response = await client.PutAsync(
            $"/v1/db/images/s3/{bucket}/multipart.png?uploadId={uploadId}&partNumber={partNumber}",
            new ByteArrayContent(content));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<ObjectProcessingStatusResponse> WaitForProcessingAsync(
        HttpClient client,
        string bucket,
        string key,
        string expectedStatus,
        string database = "images")
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            var response = await client.GetAsync(
                $"/v1/db/{Uri.EscapeDataString(database)}/s3/{bucket}/{key}?processing");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var status = await response.Content.ReadFromJsonAsync(
                    ServerJsonContext.Default.ObjectProcessingStatusResponse);
                if (status is not null
                    && string.Equals(status.Status, expectedStatus, StringComparison.Ordinal))
                    return status;
                if (status is not null
                    && string.Equals(status.Status, "failed", StringComparison.Ordinal))
                    throw new InvalidOperationException(status.Error);
            }
            await Task.Delay(50);
        }

        throw new TimeoutException($"对象派生任务未进入 {expectedStatus} 状态。");
    }

    private static async Task WaitForNoSearchHitsAsync(HttpClient client)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            var response = await client.PostAsJsonAsync(
                "/v1/db/images/images/search/text",
                new ImageTextSearchRequest("red", TopK: 10),
                ServerJsonContext.Default.ImageTextSearchRequest);
            var result = await response.Content.ReadFromJsonAsync(ServerJsonContext.Default.ImageSearchResponse);
            if (result is not null && result.Hits.Count == 0)
                return;
            await Task.Delay(50);
        }

        throw new TimeoutException("删除源对象后语义索引未及时清理。");
    }

    private static async Task<int> GetThumbnailObjectCountAsync(HttpClient client)
    {
        var response = await client.GetFromJsonAsync(
            $"/v1/db/images/s3/{ObjectSemanticProcessingService.ThumbnailBucket}?list-type=2",
            ServerJsonContext.Default.ObjectListResponse);
        return response?.Objects.Count ?? 0;
    }

    private static async Task WaitForNoThumbnailObjectsAsync(HttpClient client)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (await GetThumbnailObjectCountAsync(client) == 0)
                return;
            await Task.Delay(50);
        }

        throw new TimeoutException("生命周期删除源对象后缩略图未及时清理。");
    }

    private sealed class FakeMultimodalEmbeddingProvider : IMultimodalEmbeddingProvider
    {
        public MultimodalEmbeddingProviderInfo Info { get; }
            = new("fake", "fake-siglip2-test", 3, Ready: true);

        public ValueTask<float[]> EmbedTextAsync(string text, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(
                string.Equals(text, "invalid-provider-output", StringComparison.Ordinal)
                    ? new[] { 1f, 0f }
                    : text.Contains("red", StringComparison.OrdinalIgnoreCase)
                        ? new[] { 1f, 0f, 0f }
                        : new[] { 0f, 1f, 0f });

        public ValueTask<float[]> EmbedImageAsync(ReadOnlyMemory<byte> image, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(image.Span[0] >= 128
                ? new[] { 1f, 0f, 0f }
                : new[] { 0f, 1f, 0f });
    }

    private sealed class BlockingMultimodalEmbeddingProvider : IMultimodalEmbeddingProvider
    {
        public MultimodalEmbeddingProviderInfo Info { get; }
            = new("blocking", "fake-siglip2-test", 3, Ready: true);

        public ValueTask<float[]> EmbedTextAsync(string text, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new[] { 1f, 0f, 0f });

        public async ValueTask<float[]> EmbedImageAsync(
            ReadOnlyMemory<byte> image,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return [1f, 0f, 0f];
        }
    }

    private static string GetBaseUrl(WebApplication app)
        => app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First();
}
