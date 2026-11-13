using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SonnetDB.Data.Documents;
using SonnetDB.Data.FullText;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.FullText;

namespace SonnetDB.FullTextProcessProbe;

internal static class Program
{
    private const int Count = 10_003;

    private static async Task<int> Main(string[] args)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        if (args is ["seed", string serverRoot, string embeddedRoot])
        {
            Seed(serverRoot);
            Seed(embeddedRoot);
            return 0;
        }
        if (args is not ["consume", string embeddedPath, string baseAddress, string reportPath])
            throw new ArgumentException("Expected seed roots or consume embedded-path loopback-url report-path.");
        Uri uri = new(baseAddress, UriKind.Absolute);
        Require(uri.Host == "127.0.0.1", "Probe only permits the explicitly isolated loopback Server.");
        using var embedded = new SndbFullTextClient($"Data Source={embeddedPath}");
        using var remote = new SndbFullTextClient($"Data Source={baseAddress};Database=fulltextprobe;Token=fulltext-process-token;Timeout=20");
        using JsonDocument target = JsonDocument.Parse("\"target\"");
        int comparisons = 0;
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
            string? remoteToken = null;
            for (int page = 0; page < 2; page++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var left = await embedded.SearchAsync("logs", "by_message", "$.message", "shared", options with { ContinuationToken = embeddedToken }, deadline.Token);
                var right = await remote.SearchAsync("logs", "by_message", "$.message", "shared", options with { ContinuationToken = remoteToken }, deadline.Token);
                Equal(left, right);
                Require(left.TotalHits == 2 && left.Hits.Count == 1, "Filtered full count/window changed.");
                int number = int.Parse(left.Hits[0].DocumentId, CultureInfo.InvariantCulture);
                double average = (Count + (Count + 2) / 3d) / Count;
                int frequency = number % 3 == 0 ? 2 : 1;
                double oracle = Math.Log(1d + 0.5d / (Count + 0.5d)) * (frequency * 2.2d)
                    / (frequency + 1.2d * (0.25d + 0.75d * frequency / average));
                Require(Math.Abs(oracle - left.Hits[0].Score) < 1e-12, "Independent score oracle differs.");
                string firstId = sort.Descending ? "10002" : "10000";
                string expectedId = page == 0 ? firstId : firstId == "10000" ? "10002" : "10000";
                // 10000 的词频为 1、10002 的词频为 2；score 排序的方向与独立标量 oracle 一致。
                Require(left.Hits[0].DocumentId == expectedId, "Independent sort/id oracle differs.");
                embeddedToken = left.NextContinuationToken;
                remoteToken = right.NextContinuationToken;
                comparisons++;
            }
        }
        var fullOptions = new SndbFullTextSearchOptions(PageSize: 100,
            Facets: [new SndbFullTextFacetRequest("$.category", 100), new SndbFullTextFacetRequest("$.site", 100)], Sort: [new SndbDocumentSort("score", true)]);
        var fullEmbedded = await embedded.SearchAsync("logs", "by_message", "$.message", "shared", fullOptions, deadline.Token);
        var fullRemote = await remote.SearchAsync("logs", "by_message", "$.message", "shared", fullOptions, deadline.Token);
        Equal(fullEmbedded, fullRemote);
        Require(fullRemote.TotalHits == Count && fullRemote.Facets[0].Buckets.Count == 100, "High cardinality/full count changed.");
        Require(fullRemote.Facets[1].Buckets.SequenceEqual(new[] { new SndbFullTextFacetBucket("other", 10_001), new SndbFullTextFacetBucket("target", 2) }), "Facet was computed from a truncated window.");
        Require(fullRemote.Hits.Select(static hit => hit.DocumentId).SequenceEqual(Enumerable.Range(0, 100)
            .Select(static number => (number * 3).ToString("D5", CultureInfo.InvariantCulture))), "Equal-score hits do not use ordinal ID order.");
        comparisons++;
        int failures = 0;
        foreach (var limit in new[]
        {
            new FullTextSearchBudgetOptions { MaxDocuments = 2 },
            new FullTextSearchBudgetOptions { MaxPostingVisits = 1 },
            new FullTextSearchBudgetOptions { MaxDocumentBytes = 1 },
            new FullTextSearchBudgetOptions { MaxQueryLength = 2 },
            new FullTextSearchBudgetOptions { MaxWorkUnits = 1 },
            new FullTextSearchBudgetOptions { MaxResponseBytes = 128 },
            new FullTextSearchBudgetOptions { TimeoutMilliseconds = 1 },
        })
        {
            await ExpectBudgetAsync(() => embedded.SearchAsync("logs", "by_message", "$.message", "shared", fullOptions with { Budget = limit }, deadline.Token));
            await ExpectBudgetAsync(() => remote.SearchAsync("logs", "by_message", "$.message", "shared", fullOptions with { Budget = limit }, deadline.Token));
            failures += 2;
        }
        var recovered = await remote.SearchAsync("logs", "by_message", "$.message", "shared", new SndbFullTextSearchOptions(PageSize: 1), deadline.Token);
        Require(recovered.TotalHits == Count, "Recovery query returned an incomplete count.");
        var report = new ProbeReport(Environment.ProcessId, Process.GetCurrentProcess().StartTime.ToUniversalTime(),
            typeof(SndbFullTextClient).Assembly.Location, Sha(typeof(SndbFullTextClient).Assembly.Location),
            typeof(Tsdb).Assembly.Location, Sha(typeof(Tsdb).Assembly.Location), Count, comparisons, failures, true);
        await File.WriteAllBytesAsync(reportPath, JsonSerializer.SerializeToUtf8Bytes(report, ProbeJsonContext.Default.ProbeReport), deadline.Token);
        Console.WriteLine("PASS independent source consumer fulltext parity and budgets");
        return 0;
    }

    private static void Seed(string path)
    {
        using Tsdb database = Tsdb.Open(new TsdbOptions { RootDirectory = path });
        database.Documents.Create(DocumentCollectionSchema.Create("logs", fullTextIndexes:
            [new DocumentFullTextIndexDefinition("by_message", ["$.message"], "unicode")]));
        DocumentWriteRequest[] writes = Enumerable.Range(0, Count).Select(number => new DocumentWriteRequest(
            number.ToString("D5", CultureInfo.InvariantCulture),
            $"{{\"message\":\"{(number % 3 == 0 ? "shared shared" : "shared")}\",\"site\":\"{(number is 10_000 or 10_002 ? "target" : "other")}\",\"category\":\"category-{number:D5}\"}}"))
            .ToArray();
        Require(database.Documents.Open("logs").InsertMany(writes).Committed, "Seed transaction failed.");
    }

    private static void Equal(SndbFullTextSearchResult left, SndbFullTextSearchResult right)
    {
        Require(left.Collection == right.Collection && left.TotalHits == right.TotalHits && left.PageSize == right.PageSize
            && left.HasMore == right.HasMore && (left.NextContinuationToken is null) == (right.NextContinuationToken is null), "Result metadata differs.");
        Require(left.Hits.Count == right.Hits.Count && left.Facets.Count == right.Facets.Count, "Result sizes differ.");
        for (int index = 0; index < left.Hits.Count; index++)
        {
            SndbFullTextHit a = left.Hits[index];
            SndbFullTextHit b = right.Hits[index];
            Require(a.DocumentId == b.DocumentId && a.Score == b.Score
                && a.ScoreMetadata.Kind == b.ScoreMetadata.Kind && a.ScoreMetadata.Version == b.ScoreMetadata.Version
                && a.ScoreMetadata.QueryTerms.SequenceEqual(b.ScoreMetadata.QueryTerms)
                && a.MatchedTerms.SequenceEqual(b.MatchedTerms) && a.MatchedOffsets.SequenceEqual(b.MatchedOffsets)
                && a.Highlights.SequenceEqual(b.Highlights), "A result field differs.");
        }
        for (int index = 0; index < left.Facets.Count; index++)
            Require(left.Facets[index].Field == right.Facets[index].Field && left.Facets[index].Buckets.SequenceEqual(right.Facets[index].Buckets), "Facet differs.");
    }

    private static async Task ExpectBudgetAsync(Func<Task<SndbFullTextSearchResult>> query)
    {
        try { await query(); }
        catch (FullTextSearchBudgetExceededException) { return; }
        throw new InvalidOperationException("Budget failure did not reject the complete result.");
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

internal sealed record ProbeReport(int ProcessId, DateTime StartTimeUtc, string ClientAssembly, string ClientSha256,
    string CoreAssembly, string CoreSha256, int CorpusCount, int ComparedResponses, int BudgetFailures, bool RecoveryPassed);

[JsonSerializable(typeof(ProbeReport))]
internal sealed partial class ProbeJsonContext : JsonSerializerContext;
