using SonnetDB.FullText.Index;
using SonnetDB.FullText.Query;
using SonnetDB.FullText.Storage;
using SonnetDB.FullText.Tokenizers.Unicode;
using Xunit;
using FullTextQuery = SonnetDB.FullText.Query.Query;

namespace SonnetDB.Core.Tests.FullText;

public sealed class PersistentFullTextIndexTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "sonnetdb-fulltext-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Index_persists_documents_and_restores_after_reopen()
    {
        PersistentFullTextIndex index = Open();
        index.Index(new Document(new DocumentId("1")).Set("body", "hello sonnetdb"));
        index.Index(new Document(new DocumentId("2")).Set("body", "hello vector"));

        Assert.True(File.Exists(Path.Combine(_directory, "manifest.json")));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_directory, "segments"), "*.seg").Length);

        PersistentFullTextIndex reopened = Open();
        IReadOnlyList<SearchHit> hits = reopened.Search(new TermQuery("body", "sonnetdb"), topK: 10);

        Assert.Single(hits);
        Assert.Equal("1", hits[0].DocumentId.Value);
        Assert.Equal(2, reopened.DocumentCount);
    }

    [Fact]
    public void Reindexing_same_id_tombstones_previous_segment()
    {
        PersistentFullTextIndex index = Open();
        index.Index(new Document(new DocumentId("1")).Set("body", "old"));
        index.Index(new Document(new DocumentId("1")).Set("body", "new"));

        PersistentFullTextIndex reopened = Open();

        Assert.Empty(reopened.Search(new TermQuery("body", "old"), topK: 10));
        Assert.Single(reopened.Search(new TermQuery("body", "new"), topK: 10));
        Assert.Equal(1, reopened.DocumentCount);
    }

    [Fact]
    public void Delete_tombstone_survives_reopen()
    {
        PersistentFullTextIndex index = Open();
        index.Index(new Document(new DocumentId("1")).Set("body", "alpha"));
        index.Index(new Document(new DocumentId("2")).Set("body", "alpha beta"));

        Assert.True(index.Delete(new DocumentId("1")));

        PersistentFullTextIndex reopened = Open();
        IReadOnlyList<SearchHit> hits = reopened.Search(new TermQuery("body", "alpha"), topK: 10);

        Assert.Single(hits);
        Assert.Equal("2", hits[0].DocumentId.Value);
        Assert.Equal(1, reopened.DocumentCount);
    }

    [Fact]
    public void Merge_segments_rewrites_live_documents_and_removes_deleted_content()
    {
        PersistentFullTextIndex index = Open();
        index.Index(new Document(new DocumentId("1")).Set("body", "alpha old"));
        index.Index(new Document(new DocumentId("2")).Set("body", "beta"));
        index.Index(new Document(new DocumentId("1")).Set("body", "alpha new"));
        Assert.True(index.Delete(new DocumentId("2")));

        Assert.True(index.MergeSegments());

        Assert.Single(Directory.GetFiles(Path.Combine(_directory, "segments"), "*.seg"));

        PersistentFullTextIndex reopened = Open();
        Assert.Empty(reopened.Search(new TermQuery("body", "old"), topK: 10));
        Assert.Empty(reopened.Search(new TermQuery("body", "beta"), topK: 10));
        Assert.Single(reopened.Search(new TermQuery("body", "new"), topK: 10));
        Assert.Equal(1, reopened.DocumentCount);
    }

    [Fact]
    public void Background_merge_compacts_segments_after_threshold()
    {
        PersistentFullTextIndex index = PersistentFullTextIndex.Open(
            _directory,
            new UnicodeTokenizer(),
            options: new PersistentIndexOptions
            {
                EnableBackgroundMerge = true,
                BackgroundMergeSegmentThreshold = 2,
            });
        index.Index(new Document(new DocumentId("1")).Set("body", "alpha"));
        index.Index(new Document(new DocumentId("2")).Set("body", "beta"));

        Assert.True(index.WaitForBackgroundMerge(TimeSpan.FromSeconds(10)));
        Assert.Single(Directory.GetFiles(Path.Combine(_directory, "segments"), "*.seg"));

        PersistentFullTextIndex reopened = Open();
        Assert.Single(reopened.Search(new TermQuery("body", "alpha"), topK: 10));
        Assert.Single(reopened.Search(new TermQuery("body", "beta"), topK: 10));
        Assert.Equal(2, reopened.DocumentCount);
    }

    [Fact]
    public void And_or_queries_work_across_segments()
    {
        PersistentFullTextIndex index = Open();
        index.Index(new Document(new DocumentId("a")).Set("body", "alpha beta"));
        index.Index(new Document(new DocumentId("b")).Set("body", "alpha"));
        index.Index(new Document(new DocumentId("c")).Set("body", "gamma"));

        AndQuery and = new(new FullTextQuery[]
        {
            new TermQuery("body", "alpha"),
            new TermQuery("body", "beta"),
        });
        OrQuery or = new(new FullTextQuery[]
        {
            new TermQuery("body", "beta"),
            new TermQuery("body", "gamma"),
        });

        Assert.Single(index.Search(and, topK: 10));
        Assert.Equal(2, index.Search(or, topK: 10).Count);
    }

    [Fact]
    public void Phrase_and_near_queries_survive_reopen()
    {
        PersistentFullTextIndex index = Open();
        index.Index(new Document(new DocumentId("phrase")).Set("body", "alpha beta gamma"));
        index.Index(new Document(new DocumentId("near")).Set("body", "alpha x beta"));
        index.Index(new Document(new DocumentId("miss")).Set("body", "alpha x y beta"));

        PersistentFullTextIndex reopened = Open();

        IReadOnlyList<SearchHit> phraseHits = reopened.Search(new PhraseQuery("body", ["alpha", "beta"]), topK: 10);
        IReadOnlyList<SearchHit> nearHits = reopened.Search(new NearQuery("body", ["alpha", "beta"], maxDistance: 2), topK: 10);

        Assert.Single(phraseHits);
        Assert.Equal("phrase", phraseHits[0].DocumentId.Value);
        Assert.Equal(2, nearHits.Count);
        Assert.Contains(nearHits, hit => hit.DocumentId.Value == "phrase");
        Assert.Contains(nearHits, hit => hit.DocumentId.Value == "near");
    }

    // ── #192：manifest 崩溃后从段文件重建（原子写 + 缺失重建）────────────────────

    [Fact]
    public void Manifest_missing_but_segments_exist_rebuilds_index_not_empty()
    {
        // 建立 3 个文档（各成一段），确认 manifest 与段文件均存在。
        PersistentFullTextIndex index = Open();
        index.Index(new Document(new DocumentId("1")).Set("body", "alpha sonnetdb"));
        index.Index(new Document(new DocumentId("2")).Set("body", "beta sonnetdb"));
        index.Index(new Document(new DocumentId("3")).Set("body", "gamma sonnetdb"));

        string manifestPath = Path.Combine(_directory, "manifest.json");
        string segmentsDir = Path.Combine(_directory, "segments");
        Assert.True(File.Exists(manifestPath));
        Assert.Equal(3, Directory.GetFiles(segmentsDir, "*.seg").Length);

        // 模拟旧实现 delete-then-move 在 delete 之后崩溃：manifest 消失，但段文件仍在。
        File.Delete(manifestPath);
        Assert.False(File.Exists(manifestPath));

        // 重开：不再静默建空 manifest，而是从段文件重建——索引内容完整可查（#192）。
        PersistentFullTextIndex reopened = Open();
        Assert.Equal(3, reopened.DocumentCount);
        Assert.Single(reopened.Search(new TermQuery("body", "alpha"), topK: 10));
        Assert.Single(reopened.Search(new TermQuery("body", "beta"), topK: 10));
        Assert.Single(reopened.Search(new TermQuery("body", "gamma"), topK: 10));
        Assert.Equal(3, reopened.Search(new TermQuery("body", "sonnetdb"), topK: 10).Count);

        // 重建后 manifest 已重新落盘，且 next segment id 大于已有最大段 id（新写入不复用 id）。
        Assert.True(File.Exists(manifestPath));
        reopened.Index(new Document(new DocumentId("4")).Set("body", "delta sonnetdb"));
        PersistentFullTextIndex reopened2 = Open();
        Assert.Equal(4, reopened2.DocumentCount);
    }

    [Fact]
    public void Manifest_missing_and_no_segments_creates_empty_index()
    {
        // 边界：目录全空（从未写过）时，重建退化为空 manifest，行为与旧实现一致。
        PersistentFullTextIndex index = Open();
        Assert.Equal(0, index.DocumentCount);
        Assert.True(File.Exists(Path.Combine(_directory, "manifest.json")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private PersistentFullTextIndex Open()
    {
        return PersistentFullTextIndex.Open(
            _directory,
            new UnicodeTokenizer(),
            options: new PersistentIndexOptions { EnableBackgroundMerge = false });
    }

}
