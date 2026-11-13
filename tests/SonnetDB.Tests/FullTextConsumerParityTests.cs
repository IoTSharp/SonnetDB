using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.Contracts;
using SonnetDB.Data.Documents;
using SonnetDB.Data.FullText;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.FullText;
using SonnetDB.Hosting;
using SonnetDB.Json;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>同一有界语料在嵌入式、Server HTTP 和源码引用远程 SDK 间的逐字段合同对拍。</summary>
public sealed class FullTextConsumerParityTests : IAsyncLifetime
{
    private const string Database = "fulltextparity";
    private const string Token = "fulltext-parity-admin";
    private const int DocumentCount = 105;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnetdb-fulltext-consumers-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(180));
    private WebApplication? _app;
    private string? _serverContentRoot;
    private HttpClient? _http;
    private SndbFullTextClient? _embedded;
    private SndbFullTextClient? _remote;

    public async Task InitializeAsync()
    {
        try
        {
            string embeddedRoot = Path.Combine(_root, "embedded");
            using (Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = embeddedRoot }))
                Seed(database);

            _app = TestServerHost.Build(new ServerOptions
            {
                DataRoot = Path.Combine(_root, "server"),
                AllowAnonymousProbes = true,
                Tokens = new Dictionary<string, string> { [Token] = ServerRoles.Admin },
            });
            _serverContentRoot = Path.GetFullPath(_app.Environment.ContentRootPath);
            var registry = _app.Services.GetRequiredService<TsdbRegistry>();
            Assert.True(registry.TryCreate(Database, out Tsdb? serverDatabase));
            Seed(serverDatabase!);
            await _app.StartAsync(_deadline.Token);
            string baseUrl = _app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(15) };
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            _embedded = new SndbFullTextClient($"Data Source={embeddedRoot}");
            _remote = new SndbFullTextClient($"Data Source={baseUrl};Database={Database};Token={Token};Timeout=15");
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    [Fact]
    public async Task SameCorpus_FilterSortAndEveryPageMatchAllResultFields()
    {
        using JsonDocument filterValue = JsonDocument.Parse("\"north\"");
        // 每次查询最多 70 条命中、5 页；四种已有稳定排序均独立使用各自的不透明 token。
        foreach (var sort in new[]
        {
            new SndbDocumentSort("id"), new SndbDocumentSort("id", true),
            new SndbDocumentSort("score"), new SndbDocumentSort("score", true),
        })
        {
            _deadline.Token.ThrowIfCancellationRequested();
            var options = new SndbFullTextSearchOptions(
                PageSize: 17,
                Filter: new SndbDocumentFilter("$.site", "eq", filterValue.RootElement.Clone()),
                Sort: [sort],
                Facets: [new SndbFullTextFacetRequest("$.site", 10)],
                Highlight: new SndbFullTextHighlightRequest(80, 2));
            string? embeddedToken = null;
            string? wireToken = null;
            string? remoteToken = null;
            var allIds = new List<string>();
            var allScores = new List<(string Id, double Score)>();
            for (int page = 0; page < 5; page++)
            {
                SndbFullTextSearchResult embedded = await SearchEmbeddedAsync(options with { ContinuationToken = embeddedToken });
                FullTextSearchResponse wire = await SearchHttpAsync(ToRequest(options with { ContinuationToken = wireToken }));
                SndbFullTextSearchResult remote = await SearchRemoteAsync(options with { ContinuationToken = remoteToken });
                AssertEquivalent(embedded, remote);
                AssertEquivalent(remote, wire);
                Assert.Equal(70, remote.TotalHits);
                Assert.Equal(70, Assert.Single(remote.Facets).Buckets.Sum(static bucket => bucket.Count));
                Assert.All(remote.Hits, static hit => Assert.NotEmpty(hit.MatchedOffsets));
                allIds.AddRange(remote.Hits.Select(static hit => hit.DocumentId));
                allScores.AddRange(remote.Hits.Select(static hit => (hit.DocumentId, hit.Score)));
                embeddedToken = embedded.NextContinuationToken;
                wireToken = wire.NextContinuationToken;
                remoteToken = remote.NextContinuationToken;
                Assert.Equal(page < 4, remote.HasMore);
            }
            Assert.Equal(70, allIds.Count);
            Assert.Equal(70, allIds.Distinct(StringComparer.Ordinal).Count());
            Assert.DoesNotContain("000", allIds);
            if (sort.Path.Equals("score", StringComparison.Ordinal))
            {
                for (int index = 1; index < allScores.Count; index++)
                {
                    var previous = allScores[index - 1];
                    var current = allScores[index];
                    Assert.True(sort.Descending ? previous.Score >= current.Score : previous.Score <= current.Score);
                    if (previous.Score == current.Score)
                        Assert.True(string.CompareOrdinal(previous.Id, current.Id) <= 0);
                }
            }
            if (sort.Path == "id")
            {
                var expected = Enumerable.Range(0, DocumentCount).Where(static value => value % 3 != 0)
                    .Select(static value => value.ToString("D3", CultureInfo.InvariantCulture));
                Assert.Equal(sort.Descending ? expected.Reverse() : expected, allIds);
            }
        }
    }

    [Fact]
    public async Task ResponseProjectionLimitsMatchWithoutTruncatingTotalHits()
    {
        // 验证现有各字段投影上限；这些上限不等价于总响应字节/端到端时间预算。
        var options = new SndbFullTextSearchOptions(
            PageSize: int.MaxValue,
            Sort: [new SndbDocumentSort("id")],
            Facets: Enumerable.Range(0, 17).Select(static _ => new SndbFullTextFacetRequest("$.category", int.MaxValue)).ToArray(),
            Highlight: new SndbFullTextHighlightRequest(int.MaxValue, int.MaxValue));
        var embedded = await SearchEmbeddedAsync(options);
        var wire = await SearchHttpAsync(ToRequest(options));
        var remote = await SearchRemoteAsync(options);
        AssertEquivalent(embedded, remote);
        AssertEquivalent(remote, wire);
        Assert.Equal(100, remote.PageSize);
        Assert.Equal(100, remote.Hits.Count);
        Assert.Equal(DocumentCount, remote.TotalHits);
        Assert.True(remote.HasMore);
        Assert.Equal(16, remote.Facets.Count);
        Assert.All(remote.Facets, static facet =>
        {
            Assert.Equal(100, facet.Buckets.Count);
            Assert.All(facet.Buckets, static bucket => Assert.Equal(1, bucket.Count));
        });
        Assert.All(remote.Hits, static hit =>
        {
            Assert.Equal(64, hit.MatchedOffsets.Count);
            Assert.Equal(8, hit.Highlights.Count);
            Assert.All(hit.Highlights, static fragment => Assert.Equal(1_024, fragment.Length));
        });
        var nextOptions = options with { ContinuationToken = remote.NextContinuationToken };
        var nextRemote = await SearchRemoteAsync(nextOptions);
        var nextWire = await SearchHttpAsync(ToRequest(nextOptions));
        AssertEquivalent(nextRemote, nextWire);
        Assert.Equal(5, nextRemote.Hits.Count);
        Assert.False(nextRemote.HasMore);
        Assert.Null(nextRemote.NextContinuationToken);
        Assert.Equal(DocumentCount, nextRemote.TotalHits);
    }

    [Fact]
    public async Task QueryBudgetFailsWith422AndTypedSdkErrorWithoutPartialResult()
    {
        string overBudget = new('x', 4_097);
        await Assert.ThrowsAsync<FullTextSearchBudgetExceededException>(() =>
            _embedded!.SearchAsync("logs", "by_message", "$.message", overBudget, cancellationToken: _deadline.Token));
        using HttpResponseMessage response = await PostAsync(new FullTextSearchRequest("logs", "by_message", "$.message", overBudget));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using JsonDocument error = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(_deadline.Token), cancellationToken: _deadline.Token);
        Assert.Contains("fulltext_search_budget_exceeded", error.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.False(error.RootElement.TryGetProperty("hits", out _));
        Assert.False(error.RootElement.TryGetProperty("totalHits", out _));
        await Assert.ThrowsAsync<FullTextSearchBudgetExceededException>(() =>
            _remote!.SearchAsync("logs", "by_message", "$.message", overBudget, cancellationToken: _deadline.Token));

        // 错误后的普通查询仍返回完整总数，未把上次失败变成可消费的部分结果。
        var embedded = await SearchEmbeddedAsync(new SndbFullTextSearchOptions(PageSize: 1));
        var remote = await SearchRemoteAsync(new SndbFullTextSearchOptions(PageSize: 1));
        AssertEquivalent(embedded, remote);
        Assert.Equal(DocumentCount, remote.TotalHits);
    }

    [Fact]
    public async Task EveryBudgetFailsAtomicallyAndAnOrdinaryQueryRecovers()
    {
        var limits = new[]
        {
            new FullTextSearchBudgetOptions { MaxDocuments = 2 },
            new FullTextSearchBudgetOptions { MaxDocumentBytes = 1 },
            new FullTextSearchBudgetOptions { MaxPostingVisits = 1 },
            new FullTextSearchBudgetOptions { MaxQueryLength = 2 },
            new FullTextSearchBudgetOptions { MaxWorkUnits = 1 },
            new FullTextSearchBudgetOptions { MaxResponseBytes = 128 },
        };
        foreach (FullTextSearchBudgetOptions limit in limits)
        {
            _deadline.Token.ThrowIfCancellationRequested();
            var options = new SndbFullTextSearchOptions(PageSize: 1) { Budget = limit };
            await Assert.ThrowsAsync<FullTextSearchBudgetExceededException>(() => SearchEmbeddedAsync(options));
            using HttpResponseMessage response = await PostAsync(ToRequest(options));
            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            using JsonDocument error = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(_deadline.Token), cancellationToken: _deadline.Token);
            Assert.Contains("fulltext_search_budget_exceeded", error.RootElement.GetRawText(), StringComparison.Ordinal);
            Assert.False(error.RootElement.TryGetProperty("hits", out _));
            Assert.False(error.RootElement.TryGetProperty("totalHits", out _));
            await Assert.ThrowsAsync<FullTextSearchBudgetExceededException>(() => SearchRemoteAsync(options));

            var normal = new SndbFullTextSearchOptions(PageSize: 1);
            SndbFullTextSearchResult embedded = await SearchEmbeddedAsync(normal);
            SndbFullTextSearchResult remote = await SearchRemoteAsync(normal);
            AssertEquivalent(embedded, remote);
            Assert.Equal(DocumentCount, remote.TotalHits);
        }
    }

    [Fact]
    public async Task SortFacetProjectionAndSerializationShareTheRemainingWorkBudget()
    {
        // 基准查询只投影一个命中且不计算 facet。相同较低预算必须在后续响应工作耗尽。
        var small = new SndbFullTextSearchOptions(PageSize: 1)
        {
            Budget = new FullTextSearchBudgetOptions { MaxWorkUnits = 10_000 },
        };
        Assert.Equal(DocumentCount, (await SearchEmbeddedAsync(small)).TotalHits);
        Assert.Equal(DocumentCount, (await SearchRemoteAsync(small)).TotalHits);
        var large = small with
        {
            PageSize = 100,
            Facets = [new SndbFullTextFacetRequest("$.category", 100)],
            Highlight = new SndbFullTextHighlightRequest(1_024, 8),
        };
        await Assert.ThrowsAsync<FullTextSearchBudgetExceededException>(() => SearchEmbeddedAsync(large));
        using HttpResponseMessage response = await PostAsync(ToRequest(large));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        await Assert.ThrowsAsync<FullTextSearchBudgetExceededException>(() => SearchRemoteAsync(large));
        Assert.Equal(DocumentCount, (await SearchRemoteAsync(new SndbFullTextSearchOptions(PageSize: 1))).TotalHits);
    }

    [Fact]
    public async Task BeyondOldWindow_HighCardinalityAndIndependentSortOracleMatchEveryConsumer()
    {
        const int count = 10_003;
        _embedded!.Dispose();
        _embedded = null;
        using (Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = Path.Combine(_root, "embedded") }))
            SeedLarge(database, count);
        _embedded = new SndbFullTextClient($"Data Source={Path.Combine(_root, "embedded")}");
        Assert.True(_app!.Services.GetRequiredService<TsdbRegistry>().TryGet(Database, out Tsdb? serverDatabase));
        SeedLarge(serverDatabase!, count);

        using JsonDocument target = JsonDocument.Parse("\"target\"");
        foreach (SndbDocumentSort sort in new[]
        {
            new SndbDocumentSort("id"), new SndbDocumentSort("id", true),
            new SndbDocumentSort("score"), new SndbDocumentSort("score", true),
        })
        {
            var options = new SndbFullTextSearchOptions(PageSize: 1,
                Filter: new SndbDocumentFilter("$.site", "eq", target.RootElement.Clone()),
                Sort: [sort], Facets: [new SndbFullTextFacetRequest("$.category", 100)],
                Highlight: new SndbFullTextHighlightRequest(80, 2));
            string? embeddedToken = null;
            string? wireToken = null;
            string? remoteToken = null;
            var actualIds = new List<string>();
            for (int page = 0; page < 2; page++)
            {
                var embedded = await _embedded!.SearchAsync("large", "by_message", "$.message", "shared", options with { ContinuationToken = embeddedToken }, _deadline.Token);
                var remote = await _remote!.SearchAsync("large", "by_message", "$.message", "shared", options with { ContinuationToken = remoteToken }, _deadline.Token);
                FullTextSearchRequest request = ToRequest(options with { ContinuationToken = wireToken }) with { Collection = "large" };
                FullTextSearchResponse wire = await SearchHttpAsync(request);
                AssertEquivalent(embedded, remote);
                AssertEquivalent(remote, wire);
                Assert.Equal(2, remote.TotalHits);
                Assert.Equal(2, Assert.Single(remote.Facets).Buckets.Sum(static bucket => bucket.Count));
                SndbFullTextHit hit = Assert.Single(remote.Hits);
                int number = int.Parse(hit.DocumentId, CultureInfo.InvariantCulture);
                Assert.Equal(IndependentScore(number, count), hit.Score, precision: 12);
                actualIds.Add(hit.DocumentId);
                embeddedToken = embedded.NextContinuationToken;
                wireToken = wire.NextContinuationToken;
                remoteToken = remote.NextContinuationToken;
            }
            IEnumerable<int> oracle = new[] { 10_000, 10_002 };
            oracle = sort.Path == "id"
                ? sort.Descending ? oracle.OrderDescending() : oracle.Order()
                : sort.Descending ? oracle.OrderByDescending(number => IndependentScore(number, count)).ThenBy(static number => number)
                    : oracle.OrderBy(number => IndependentScore(number, count)).ThenBy(static number => number);
            Assert.Equal(oracle.Select(static number => number.ToString("D5", CultureInfo.InvariantCulture)), actualIds);
        }

        var full = new SndbFullTextSearchOptions(PageSize: 100, Skip: 9_990,
            Sort: [new SndbDocumentSort("id")], Facets: [new SndbFullTextFacetRequest("$.category", 100), new SndbFullTextFacetRequest("$.site", 100)]);
        SndbFullTextSearchResult completeEmbedded = await _embedded!.SearchAsync("large", "by_message", "$.message", "shared", full, _deadline.Token);
        SndbFullTextSearchResult completeRemote = await _remote!.SearchAsync("large", "by_message", "$.message", "shared", full, _deadline.Token);
        FullTextSearchResponse completeWire = await SearchHttpAsync(ToRequest(full) with { Collection = "large" });
        AssertEquivalent(completeEmbedded, completeRemote);
        AssertEquivalent(completeRemote, completeWire);
        Assert.Equal(count, completeRemote.TotalHits);
        Assert.Equal(Enumerable.Range(9_990, 13).Select(static number => number.ToString("D5", CultureInfo.InvariantCulture)),
            completeRemote.Hits.Select(static hit => hit.DocumentId));
        Assert.Equal(100, completeRemote.Facets[0].Buckets.Count);
        Assert.Equal(Enumerable.Range(0, 100).Select(static number => $"category-{number:D5}"), completeRemote.Facets[0].Buckets.Select(static bucket => bucket.Value));
        Assert.All(completeRemote.Facets[0].Buckets, static bucket => Assert.Equal(1, bucket.Count));
        Assert.Equal(new[] { new SndbFullTextFacetBucket("other", 10_001), new SndbFullTextFacetBucket("target", 2) }, completeRemote.Facets[1].Buckets);

        var scoreOptions = full with { Skip = 0, Sort = [new SndbDocumentSort("score", true)] };
        SndbFullTextSearchResult scoreResult = await _remote.SearchAsync("large", "by_message", "$.message", "shared", scoreOptions, _deadline.Token);
        Assert.Equal(Enumerable.Range(0, count).OrderByDescending(number => IndependentScore(number, count)).ThenBy(static number => number)
            .Take(100).Select(static number => number.ToString("D5", CultureInfo.InvariantCulture)), scoreResult.Hits.Select(static hit => hit.DocumentId));

        var timed = full with { Budget = new FullTextSearchBudgetOptions { TimeoutMilliseconds = 1 } };
        await Assert.ThrowsAsync<FullTextSearchBudgetExceededException>(() =>
            _embedded!.SearchAsync("large", "by_message", "$.message", "shared", timed, _deadline.Token));
        using HttpResponseMessage timeout = await PostAsync(ToRequest(timed) with { Collection = "large" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, timeout.StatusCode);
        await Assert.ThrowsAsync<FullTextSearchBudgetExceededException>(() =>
            _remote!.SearchAsync("large", "by_message", "$.message", "shared", timed, _deadline.Token));
        Assert.Equal(count, (await _remote!.SearchAsync("large", "by_message", "$.message", "shared",
            new SndbFullTextSearchOptions(PageSize: 1), _deadline.Token)).TotalHits);
    }

    [Fact]
    public async Task EmptyWindowInvalidInputAndCancellationKeepTheirContracts()
    {
        var options = new SndbFullTextSearchOptions(PageSize: 1, Skip: DocumentCount);
        var embedded = await SearchEmbeddedAsync(options);
        var remote = await SearchRemoteAsync(options);
        var wire = await SearchHttpAsync(ToRequest(options));
        AssertEquivalent(embedded, remote);
        AssertEquivalent(remote, wire);
        Assert.Empty(remote.Hits);
        Assert.False(remote.HasMore);
        Assert.Equal(DocumentCount, remote.TotalHits);

        using HttpResponseMessage invalid = await PostAsync(new FullTextSearchRequest("logs", "by_message", "$.message", "shared", Skip: -1));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        await Assert.ThrowsAsync<ArgumentException>(() => _embedded!.SearchAsync("logs", "by_message", "$.message", ""));
        await Assert.ThrowsAsync<ArgumentException>(() => _remote!.SearchAsync("logs", "by_message", "$.message", ""));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _embedded!.SearchAsync("logs", "by_message", "$.message", "shared", cancellationToken: cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _remote!.SearchAsync("logs", "by_message", "$.message", "shared", cancellationToken: cancelled.Token));

        var first = await SearchRemoteAsync(new SndbFullTextSearchOptions(PageSize: 1));
        var mismatched = new SndbFullTextSearchOptions(PageSize: 1, Sort: [new SndbDocumentSort("id")], ContinuationToken: first.NextContinuationToken);
        using HttpResponseMessage invalidToken = await PostAsync(ToRequest(mismatched));
        Assert.Equal(HttpStatusCode.BadRequest, invalidToken.StatusCode);
        HttpRequestException exception = await Assert.ThrowsAsync<HttpRequestException>(() => SearchRemoteAsync(mismatched));
        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Contains("invalid_continuation_token", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedRequestBodyIsRejectedBeforeJsonMaterialization()
    {
        string oversizedQuery = new('x', 4 * 1024 * 1024);
        using var content = new StringContent(
            $"{{\"collection\":\"logs\",\"index\":\"by_message\",\"field\":\"$.message\",\"query\":\"{oversizedQuery}\"}}",
            Encoding.UTF8,
            "application/json");
        using HttpResponseMessage response = await _http!.PostAsync($"/v1/db/{Database}/fulltext/search", content, _deadline.Token);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        using JsonDocument error = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(_deadline.Token), cancellationToken: _deadline.Token);
        Assert.Contains("fulltext_search_budget_exceeded", error.RootElement.GetRawText(), StringComparison.Ordinal);
        Assert.False(error.RootElement.TryGetProperty("hits", out _));
    }

    public async Task DisposeAsync()
    {
        Exception? cleanupError = null;
        try { _remote?.Dispose(); }
        catch (Exception ex) { cleanupError ??= ex; }
        finally
        {
            _remote = null;
            try { _embedded?.Dispose(); }
            catch (Exception ex) { cleanupError ??= ex; }
            finally
            {
                _embedded = null;
                try { _http?.Dispose(); }
                catch (Exception ex) { cleanupError ??= ex; }
                finally
                {
                    _http = null;
                    if (_app is not null)
                    {
                        using var stopDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        try { await _app.StopAsync(stopDeadline.Token); }
                        catch (Exception ex) { cleanupError ??= ex; }
                        finally
                        {
                            try { await _app.DisposeAsync(); } catch (Exception ex) { cleanupError ??= ex; }
                            _app = null;
                        }
                    }
                }
            }
        }
        try { _deadline.Dispose(); } catch (Exception ex) { cleanupError ??= ex; }
        try { DeleteOwnedDirectory(_root, "sonnetdb-fulltext-consumers-"); }
        catch (Exception ex) { cleanupError ??= ex; }
        try { DeleteOwnedDirectory(_serverContentRoot, "sonnetdb-test-host-"); }
        catch (Exception ex) { cleanupError ??= ex; }
        if (cleanupError is not null)
            throw cleanupError;
    }

    private static void DeleteOwnedDirectory(string? path, string prefix)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;
        string fullPath = Path.GetFullPath(path);
        string tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        StringComparison pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var directory = new DirectoryInfo(fullPath);
        string name = directory.Name;
        if (!string.Equals(directory.Parent?.FullName?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), tempRoot, pathComparison)
            || !name.StartsWith(prefix, StringComparison.Ordinal)
            || name.Length != prefix.Length + 32
            || !name[prefix.Length..].All(static c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            throw new InvalidOperationException($"拒绝清理不满足任务 GUID 边界的临时目录: '{fullPath}'。");
        if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"拒绝递归清理重解析临时目录: '{fullPath}'。");
        if (directory.Exists)
            directory.Delete(recursive: true);
    }

    private static void Seed(Tsdb database)
    {
        var schema = DocumentCollectionSchema.Create("logs", fullTextIndexes:
            [new DocumentFullTextIndexDefinition("by_message", ["$.message"], "unicode")]);
        database.Documents.Create(schema);
        string message = string.Concat(Enumerable.Repeat("shared ", 70)) + new string('z', 1_200);
        DocumentWriteRequest[] writes = Enumerable.Range(0, DocumentCount).Select(number =>
            new DocumentWriteRequest(number.ToString("D3", CultureInfo.InvariantCulture),
                $"{{\"message\":\"{(number % 2 == 0 ? "shared " : "")}{message}\",\"site\":\"{(number % 3 == 0 ? "south" : "north")}\",\"category\":\"category-{number:D3}\"}}"))
            .ToArray();
        Assert.True(database.Documents.Open("logs").InsertMany(writes).Committed);
    }

    private static void SeedLarge(Tsdb database, int count)
    {
        database.Documents.Create(DocumentCollectionSchema.Create("large", fullTextIndexes:
            [new DocumentFullTextIndexDefinition("by_message", ["$.message"], "unicode")]));
        DocumentWriteRequest[] writes = Enumerable.Range(0, count).Select(number => new DocumentWriteRequest(
            number.ToString("D5", CultureInfo.InvariantCulture),
            $"{{\"message\":\"{(number % 3 == 0 ? "shared shared" : "shared")}\",\"site\":\"{(number is 10_000 or 10_002 ? "target" : "other")}\",\"category\":\"category-{number:D5}\"}}"))
            .ToArray();
        Assert.True(database.Documents.Open("large").InsertMany(writes).Committed);
    }

    private static double IndependentScore(int number, int count)
    {
        // 独立的标量 BM25 oracle：本语料只含一个查询词，所有文档都命中，词频为 1 或 2。
        int frequency = number % 3 == 0 ? 2 : 1;
        double averageLength = (count + (count + 2) / 3d) / count;
        double inverseFrequency = Math.Log(1d + 0.5d / (count + 0.5d));
        return inverseFrequency * (frequency * 2.2d)
            / (frequency + 1.2d * (0.25d + 0.75d * frequency / averageLength));
    }

    private Task<SndbFullTextSearchResult> SearchEmbeddedAsync(SndbFullTextSearchOptions options)
        => _embedded!.SearchAsync("logs", "by_message", "$.message", "shared", options, _deadline.Token);

    private Task<SndbFullTextSearchResult> SearchRemoteAsync(SndbFullTextSearchOptions options)
        => _remote!.SearchAsync("logs", "by_message", "$.message", "shared", options, _deadline.Token);

    private Task<HttpResponseMessage> PostAsync(FullTextSearchRequest request)
        => _http!.PostAsync($"/v1/db/{Database}/fulltext/search",
            JsonContent.Create(request, ServerJsonContext.Default.FullTextSearchRequest), _deadline.Token);

    private async Task<FullTextSearchResponse> SearchHttpAsync(FullTextSearchRequest request)
    {
        using HttpResponseMessage response = await PostAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync(ServerJsonContext.Default.FullTextSearchResponse, _deadline.Token)
            ?? throw new InvalidDataException("全文 HTTP 响应为空。");
    }

    private static FullTextSearchRequest ToRequest(SndbFullTextSearchOptions options)
        => new("logs", "by_message", "$.message", "shared", options.PageSize, options.Skip, options.Mode, options.QueryKind,
            options.Filter is { } filter ? new DocumentFilterContract(filter.Path, filter.Op, filter.Value) : null,
            options.Sort?.Select(static sort => new DocumentSortContract(sort.Path, sort.Descending)).ToArray(),
            options.Facets?.Select(static facet => new FullTextFacetRequest(facet.Field, facet.Limit)).ToArray(),
            options.Highlight is { } highlight ? new FullTextHighlightRequest(highlight.FragmentSize, highlight.MaxFragments) : null,
            options.ContinuationToken)
        { Budget = options.Budget };

    private static void AssertEquivalent(SndbFullTextSearchResult expected, FullTextSearchResponse actual)
    {
        // 同一 HTTP 合同的直接消费者与 SDK 应保留完全相同的 token 字节。
        Assert.Equal(expected.NextContinuationToken, actual.NextContinuationToken);
        AssertEquivalent(expected, new SndbFullTextSearchResult(actual.Collection,
            actual.Hits.Select(static hit => new SndbFullTextHit(hit.DocumentId, hit.Score,
                new SndbFullTextScoreMetadata(hit.ScoreMetadata.Kind, hit.ScoreMetadata.Version, hit.ScoreMetadata.QueryTerms),
                hit.MatchedTerms,
                hit.MatchedOffsets.Select(static offset => new SndbFullTextMatchedOffset(offset.Field, offset.Term, offset.Start, offset.End)).ToArray(),
                hit.Highlights)).ToArray(),
            actual.Facets.Select(static facet => new SndbFullTextFacetResult(facet.Field,
                facet.Buckets.Select(static bucket => new SndbFullTextFacetBucket(bucket.Value, bucket.Count)).ToArray())).ToArray(),
            actual.NextContinuationToken, actual.HasMore, actual.PageSize, actual.TotalHits));
    }

    private static void AssertEquivalent(SndbFullTextSearchResult expected, SndbFullTextSearchResult actual)
    {
        Assert.Equal(expected.Collection, actual.Collection);
        Assert.Equal(expected.TotalHits, actual.TotalHits);
        Assert.Equal(expected.PageSize, actual.PageSize);
        Assert.Equal(expected.HasMore, actual.HasMore);
        // token 对调用方不透明，embedded 与 HTTP 编码不同；只比较存在性并分别消费。
        Assert.Equal(expected.NextContinuationToken is null, actual.NextContinuationToken is null);
        Assert.Equal(expected.Hits.Count, actual.Hits.Count);
        for (int index = 0; index < expected.Hits.Count; index++)
        {
            var left = expected.Hits[index];
            var right = actual.Hits[index];
            Assert.Equal(left.DocumentId, right.DocumentId);
            Assert.Equal(left.Score, right.Score);
            Assert.Equal(left.ScoreMetadata.Kind, right.ScoreMetadata.Kind);
            Assert.Equal(left.ScoreMetadata.Version, right.ScoreMetadata.Version);
            Assert.Equal(left.ScoreMetadata.QueryTerms, right.ScoreMetadata.QueryTerms);
            Assert.Equal(left.MatchedTerms, right.MatchedTerms);
            Assert.Equal(left.MatchedOffsets, right.MatchedOffsets);
            Assert.Equal(left.Highlights, right.Highlights);
        }
        Assert.Equal(expected.Facets.Count, actual.Facets.Count);
        for (int index = 0; index < expected.Facets.Count; index++)
        {
            Assert.Equal(expected.Facets[index].Field, actual.Facets[index].Field);
            Assert.Equal(expected.Facets[index].Buckets, actual.Facets[index].Buckets);
        }
    }
}
