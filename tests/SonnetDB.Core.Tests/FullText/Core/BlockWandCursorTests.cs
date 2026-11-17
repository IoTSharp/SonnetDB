using SonnetDB.FullText.Index;
using SonnetDB.FullText.Storage;
using Xunit;

namespace SonnetDB.Core.Tests.FullText;

public sealed class BlockWandCursorTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-fulltext-block-wand-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void AdvanceTo_skips_unread_blocks_until_target()
    {
        var metadata = new BlockWandBlockMetadata[]
        {
            new(0, 9, 1, 1),
            new(10, 19, 1, 1),
        };
        int reads = 0;
        var cursor = new BlockWandCursor(
            metadata,
            blockIndex =>
            {
                reads++;
                return blockIndex == 0
                    ? [new BlockWandPosting(0, 1)]
                    : [new BlockWandPosting(10, 2)];
            });

        cursor.AdvanceTo(10);

        Assert.Equal(0, reads);
        Assert.Equal(10, cursor.LowerBoundDocument);
        Assert.Equal(10, cursor.CurrentDocument);
        Assert.Equal(1, reads);
        Assert.Equal(2, cursor.CurrentScore);
    }

    [Fact]
    public void Executor_returns_stable_top_k_and_reads_only_needed_payloads()
    {
        int leftReads = 0;
        int rightReads = 0;
        var left = new BlockWandCursor(
            [
                new BlockWandBlockMetadata(0, 9, 1, 1),
                new BlockWandBlockMetadata(10, 19, 1, 10),
            ],
            blockIndex =>
            {
                leftReads++;
                return blockIndex == 0
                    ? [new BlockWandPosting(0, 1)]
                    : [new BlockWandPosting(10, 10)];
            });
        var right = new BlockWandCursor(
            [new BlockWandBlockMetadata(0, 9, 1, 1)],
            _ =>
            {
                rightReads++;
                return [new BlockWandPosting(0, 1)];
            });

        IReadOnlyList<BlockWandHit> hits = BlockWandExecutor.Search([left, right], topK: 1);

        Assert.Single(hits);
        Assert.Equal(10, hits[0].DocumentId);
        Assert.Equal(10, hits[0].Score);
        Assert.True(leftReads <= 2);
        Assert.True(rightReads <= 1);
    }

    [Fact]
    public void Executor_honors_pre_cancellation_without_reading_payloads()
    {
        int reads = 0;
        var cursor = new BlockWandCursor(
            [new BlockWandBlockMetadata(0, 9, 1, 1)],
            _ =>
            {
                reads++;
                return [new BlockWandPosting(0, 1)];
            });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            BlockWandExecutor.Search([cursor], topK: 1, cancellation.Token));
        Assert.Equal(0, reads);
    }

    [Fact]
    public void Lazy_dsseg003_read_fails_closed_on_first_corrupted_block_access()
    {
        SegmentData data = BuildData();
        SegmentReader written = SegmentFile.Write(_directory, data);
        Assert.True(written.TryGetPostingBlocks("body", "alpha", out IReadOnlyList<PostingBlockInfo>? blocks));
        string path = SegmentFile.GetPath(_directory, data.Id);
        byte[] bytes = File.ReadAllBytes(path);
        bytes[checked((int)blocks[0].FileOffset)] ^= 0x40;
        File.WriteAllBytes(path, bytes);

        var cache = new FullTextBlockCache(1024);
        SegmentReader lazy = SegmentFile.Read(path, cache, lazyPostingBlocks: true);

        Assert.Throws<InvalidDataException>(() =>
            lazy.TryReadPostingBlock("body", "alpha", 0, cache, out _));
        Assert.Equal(1, cache.MissCount);
        Assert.Equal(0, cache.HitCount);
    }

    [Fact]
    public void Lazy_dsseg003_small_cache_does_not_report_false_hits()
    {
        SegmentData data = BuildData();
        SegmentReader reader = SegmentFile.Write(_directory, data);
        Assert.True(reader.TryGetPostingBlocks("body", "alpha", out IReadOnlyList<PostingBlockInfo>? blocks));
        var cache = new FullTextBlockCache(Math.Max(1, blocks[0].Length - 1));
        SegmentReader lazy = SegmentFile.Read(
            SegmentFile.GetPath(_directory, data.Id),
            cache,
            lazyPostingBlocks: true);

        Assert.True(lazy.TryReadPostingBlock("body", "alpha", 0, cache, out _));
        Assert.True(lazy.TryReadPostingBlock("body", "alpha", 0, cache, out _));
        Assert.Equal(2, cache.MissCount);
        Assert.Equal(0, cache.HitCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static SegmentData BuildData()
    {
        var data = new SegmentData(77);
        var postings = new Dictionary<int, int> { [0] = 1 };
        var positions = new Dictionary<int, List<int>> { [0] = [0] };
        data.Documents.Add(new SegmentDocument(0, new DocumentId("doc-0")));
        data.Documents[0].Fields["body"] = "alpha";
        data.FieldLengths["body"] = new Dictionary<int, int> { [0] = 1 };
        data.PostingLists.Add(new SegmentPostingList("body", "alpha", postings, positions));
        return data;
    }
}
