using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
    private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(90));
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
            options.ContinuationToken);

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
