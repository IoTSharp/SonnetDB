using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.FullText;
using SonnetDB.Generations;
using Xunit;

namespace SonnetDB.Core.Tests.Compatibility;

public sealed class PublicApiCompatibilityTests
{
    [Fact]
    public void DocumentFullTextFilteredSearch_ExtendOnlyPublicContract_IsConsumable()
    {
        Assert.NotNull(typeof(DocumentCollectionStore).GetMethod(
            nameof(DocumentCollectionStore.SearchFullTextFiltered),
            [
                typeof(DocumentFullTextIndex),
                typeof(string),
                typeof(string),
                typeof(int),
                typeof(IReadOnlySet<string>),
                typeof(long),
                typeof(CancellationToken),
            ]));
        Assert.Equal(
            typeof(IReadOnlyList<DocumentFullTextSearchHit>),
            typeof(DocumentFullTextFilteredSearchResult)
                .GetProperty(nameof(DocumentFullTextFilteredSearchResult.Hits))!
                .PropertyType);
        Assert.Equal(
            typeof(bool),
            typeof(DocumentFullTextFilteredSearchResult)
                .GetProperty(nameof(DocumentFullTextFilteredSearchResult.PostingBudgetExceeded))!
                .PropertyType);
    }

    [Fact]
    public void DatabaseGeneration_ExtendOnlyPublicContract_IsConsumable()
    {
        var resource = new DatabaseGenerationResource(
            "state",
            DatabaseGenerationResourceKind.KvKeyspace,
            "workspace-a");
        var request = new DatabaseGenerationPublishRequest
        {
            Stream = "workspace",
            GenerationId = "commit-a",
            ExpectedRevision = 0,
            Resources = [resource],
        };

        Assert.Equal("workspace", request.Stream);
        Assert.Equal("commit-a", request.GenerationId);
        Assert.Equal(0, request.ExpectedRevision);
        Assert.Same(resource, Assert.Single(request.Resources));
        Assert.Equal(typeof(DatabaseGenerationManager), typeof(Tsdb).GetProperty(nameof(Tsdb.Generations))!.PropertyType);
        Assert.NotNull(typeof(DatabaseGenerationManager).GetMethod(
            nameof(DatabaseGenerationManager.Publish),
            [typeof(DatabaseGenerationPublishRequest), typeof(CancellationToken)]));
        Assert.NotNull(typeof(DatabaseGenerationManager).GetMethod(
            nameof(DatabaseGenerationManager.AcquireActive),
            [typeof(string)]));
        Assert.NotNull(typeof(DatabaseGenerationManager).GetMethod(
            nameof(DatabaseGenerationManager.Acquire),
            [typeof(string), typeof(long)]));
        Assert.NotNull(typeof(DatabaseGenerationManager).GetMethod(
            nameof(DatabaseGenerationManager.CleanupRetired),
            [typeof(string), typeof(CancellationToken)]));
        Assert.NotNull(typeof(DatabaseGenerationManager).GetMethod(
            nameof(DatabaseGenerationManager.CleanupRetired),
            [
                typeof(string),
                typeof(DatabaseGenerationCleanupOptions),
                typeof(CancellationToken),
            ]));
        var cleanupOptions = new DatabaseGenerationCleanupOptions(
            new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.FromHours(8)));
        Assert.Equal(TimeSpan.Zero, cleanupOptions.PublishedBeforeUtc.Offset);
        Assert.Equal(
            new DateTimeOffset(2026, 8, 30, 4, 0, 0, TimeSpan.Zero),
            cleanupOptions.PublishedBeforeUtc);
        Assert.NotNull(typeof(DatabaseGenerationCleanupResult).GetProperty(
            nameof(DatabaseGenerationCleanupResult.RetentionDeferredRevisions)));
        Assert.Equal(1, (int)DatabaseGenerationResourceKind.KvKeyspace);
        Assert.Equal(2, (int)DatabaseGenerationResourceKind.DocumentCollection);
        Assert.Equal(3, (int)DatabaseGenerationResourceKind.DocumentFullTextIndex);
    }

}
