using SonnetDB.Documents;
using SonnetDB.FullText;
using Xunit;

namespace SonnetDB.Core.Tests.FullText;

/// <summary>
/// 验证文档 fuzzy 查询按字段共享活跃 term 快照。
/// </summary>
public sealed class DocumentFullTextIndexStorePerformanceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-document-fulltext-performance-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Search_MultiFieldMultiTokenFuzzyQuery_BuildsOneSnapshotPerFieldAndReusesIt()
    {
        DocumentFullTextIndexStore store = Open();
        store.UpsertMany(
        [
            new DocumentRow(
                "pump-1",
                """{"title":"pump alarm","body":"north pump alarm"}""",
                Version: 1),
            new DocumentRow(
                "fan-1",
                """{"title":"fan normal","body":"south fan normal"}""",
                Version: 1),
        ]);

        IReadOnlyList<DocumentFullTextSearchHit> first = store.Search(
            "*",
            "pmp alrm",
            topK: 10,
            FullTextSearchMode.Fuzzy,
            FullTextQueryKind.All);
        IReadOnlyList<DocumentFullTextSearchHit> second = store.Search(
            "*",
            "pmp alrm",
            topK: 10,
            FullTextSearchMode.Fuzzy,
            FullTextQueryKind.All);

        Assert.Contains(first, static hit => hit.DocumentId == "pump-1");
        Assert.Equal(first.Select(static hit => hit.DocumentId), second.Select(static hit => hit.DocumentId));
        Assert.Equal(2, store.ActiveTermSnapshotBuildCount);

        store.Delete("pump-1");
        Assert.Empty(store.Search(
            "*",
            "pmp alrm",
            topK: 10,
            FullTextSearchMode.Fuzzy,
            FullTextQueryKind.All));
        Assert.Equal(4, store.ActiveTermSnapshotBuildCount);
    }

    [Theory]
    [InlineData("pump", "pump", 2, 0)]
    [InlineData("pmp", "pump", 1, 1)]
    [InlineData("alarm", "alram", 1, 1)]
    [InlineData("abc", "xyz", 1, 2)]
    [InlineData("", "ab", 2, 2)]
    public void Distance_CommonEdits_ReturnsBoundedDamerauLevenshteinDistance(
        string left,
        string right,
        int maxDistance,
        int expected)
    {
        Assert.Equal(expected, DamerauLevenshtein.Distance(left, right, maxDistance));
    }

    [Fact]
    public void Distance_LongTerms_UsesPooledRowsAndPreservesResult()
    {
        string left = new('a', 200);
        string right = left[..199] + "b";

        Assert.Equal(1, DamerauLevenshtein.Distance(left, right, maxDistance: 2));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private DocumentFullTextIndexStore Open()
        => DocumentFullTextIndexStore.Open(
            _directory,
            new DocumentFullTextIndex(
                "ft_documents",
                ["$.title", "$.body"],
                "unicode",
                DateTime.UtcNow.Ticks));
}
