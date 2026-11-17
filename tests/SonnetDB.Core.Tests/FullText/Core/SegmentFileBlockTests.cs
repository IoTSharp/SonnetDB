using System.Buffers.Binary;
using System.Text;
using SonnetDB.FullText.Index;
using SonnetDB.FullText.Storage;
using Xunit;

namespace SonnetDB.Core.Tests.FullText;

public sealed class SegmentFileBlockTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-fulltext-segment-block-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Dsseg003_writes_block_directory_and_reopens()
    {
        SegmentData data = BuildData(260);
        SegmentReader reader = SegmentFile.Write(_directory, data);

        Assert.True(reader.TryGetPostingBlocks("body", "alpha", out IReadOnlyList<PostingBlockInfo>? blocks));
        Assert.Equal(3, blocks.Count);
        Assert.Equal(0, blocks[0].FirstDocumentId);
        Assert.Equal(127, blocks[0].LastDocumentId);
        Assert.Equal(128, blocks[1].FirstDocumentId);
        Assert.Equal(255, blocks[1].LastDocumentId);
        Assert.Equal(256, blocks[2].FirstDocumentId);
        Assert.Equal(259, blocks[2].LastDocumentId);
        Assert.All(blocks, block =>
        {
            Assert.True(block.FileOffset > 0);
            Assert.True(block.Length > 0);
            Assert.True(block.MaxTermFrequency >= 1);
            Assert.NotEqual(0U, block.Crc32);
            Assert.True(block.PostingCount > 0);
            Assert.InRange(block.PostingCount, 1, 128);
        });
        Assert.True(blocks[0].FileOffset < blocks[1].FileOffset);
        Assert.True(blocks[1].FileOffset < blocks[2].FileOffset);

        SegmentReader reopened = SegmentFile.Read(SegmentFile.GetPath(_directory, data.Id));
        Assert.True(reopened.TryGetPostingBlocks("body", "alpha", out IReadOnlyList<PostingBlockInfo>? reopenedBlocks));
        Assert.Equal(blocks, reopenedBlocks);
        Assert.True(reopened.TryGetPostings("body", "alpha", out Dictionary<int, int>? postings));
        Assert.Equal(260, postings.Count);
        Assert.Equal(3, postings[0]);
    }

    [Fact]
    public void Dsseg003_rejects_corrupted_posting_block_payload()
    {
        SegmentData data = BuildData(4);
        SegmentReader reader = SegmentFile.Write(_directory, data);
        Assert.True(reader.TryGetPostingBlocks("body", "alpha", out IReadOnlyList<PostingBlockInfo>? blocks));
        string path = SegmentFile.GetPath(_directory, data.Id);
        byte[] bytes = File.ReadAllBytes(path);
        bytes[checked((int)blocks[0].FileOffset)] ^= 0x01;
        File.WriteAllBytes(path, bytes);

        Assert.Throws<InvalidDataException>(() => SegmentFile.Read(path));
    }

    [Fact]
    public void Dsseg003_can_read_a_verified_block_through_a_bounded_cache()
    {
        SegmentData data = BuildData(260);
        SegmentReader reader = SegmentFile.Write(_directory, data);
        Assert.True(reader.TryGetPostingBlocks("body", "alpha", out IReadOnlyList<PostingBlockInfo>? blocks));
        var cache = new FullTextBlockCache(blocks[0].Length * 2L);

        Assert.True(reader.TryReadPostingBlock("body", "alpha", 0, cache, out byte[] first));
        Assert.Equal(blocks[0].Length, first.Length);
        Assert.Equal(1, cache.MissCount);
        Assert.True(reader.TryReadPostingBlock("body", "alpha", 0, cache, out byte[] second));
        Assert.Equal(first, second);
        Assert.Equal(1, cache.HitCount);
        Assert.True(reader.TryReadPostingBlock("body", "alpha", 1, cache, out _));
        Assert.Equal(2, cache.MissCount);
        Assert.False(reader.TryReadPostingBlock("body", "alpha", 99, cache, out _));
    }

    [Fact]
    public void Dsseg003_lazy_reader_loads_postings_through_cache_on_first_term_access()
    {
        SegmentData data = BuildData(260);
        SegmentReader written = SegmentFile.Write(_directory, data);
        Assert.True(written.TryGetPostingBlocks("body", "alpha", out IReadOnlyList<PostingBlockInfo>? blocks));

        var cache = new FullTextBlockCache(4L * 1024 * 1024);
        SegmentReader lazy = SegmentFile.Read(
            SegmentFile.GetPath(_directory, data.Id),
            cache,
            lazyPostingBlocks: true);

        Assert.True(lazy.TryGetPostings("body", "alpha", out Dictionary<int, int>? postings));
        Assert.Equal(260, postings.Count);
        Assert.Equal(blocks.Count, cache.MissCount);

        for (int blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
        {
            Assert.True(lazy.TryReadPostingBlock("body", "alpha", blockIndex, cache, out _));
        }

        Assert.Equal(blocks.Count, cache.HitCount);
    }

    [Fact]
    public void Unknown_segment_magic_fails_closed()
    {
        Directory.CreateDirectory(_directory);
        string path = SegmentFile.GetPath(_directory, 7);
        File.WriteAllBytes(path, "UNKNOWN!"u8.ToArray());

        Assert.Throws<FormatException>(() => SegmentFile.Read(path));
    }

    [Fact]
    public void Dsseg001_and_dsseg002_remain_readable()
    {
        foreach (byte[] magic in new[] { "DSSEG001"u8.ToArray(), "DSSEG002"u8.ToArray() })
        {
            Directory.CreateDirectory(_directory);
            string path = SegmentFile.GetPath(_directory, magic[7]);
            WriteLegacySegment(path, magic, hasPositions: magic.SequenceEqual("DSSEG002"u8));

            SegmentReader reader = SegmentFile.Read(path);
            Assert.Equal(1, reader.DocumentCount);
            Assert.False(reader.TryGetPostingBlocks("body", "alpha", out _));
            Assert.True(reader.TryGetPostings("body", "alpha", out Dictionary<int, int>? postings));
            Assert.Equal(1, postings[0]);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static SegmentData BuildData(int count)
    {
        var data = new SegmentData(42);
        var postings = new Dictionary<int, int>();
        var positions = new Dictionary<int, List<int>>();
        var lengths = new Dictionary<int, int>();
        for (int i = 0; i < count; i++)
        {
            var document = new SegmentDocument(i, new DocumentId($"doc-{i:D4}"));
            document.Fields["body"] = "alpha alpha alpha";
            data.Documents.Add(document);
            postings[i] = 3;
            positions[i] = [0, 1, 2];
            lengths[i] = 3;
        }

        data.FieldLengths["body"] = lengths;
        data.PostingLists.Add(new SegmentPostingList("body", "alpha", postings, positions));
        return data;
    }

    private static void WriteLegacySegment(string path, byte[] magic, bool hasPositions)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(magic);
        WriteInt64(stream, 1);
        VarInt.Write(stream, 1);
        VarInt.Write(stream, 0);
        WriteString(stream, "legacy");
        VarInt.Write(stream, 1);
        WriteString(stream, "body");
        WriteString(stream, "alpha");
        VarInt.Write(stream, 1);
        WriteString(stream, "body");
        VarInt.Write(stream, 1);
        VarInt.Write(stream, 0);
        VarInt.Write(stream, 1);
        VarInt.Write(stream, 1);
        WriteString(stream, "body");
        WriteString(stream, "alpha");
        VarInt.Write(stream, 1);
        VarInt.Write(stream, 0);
        VarInt.Write(stream, 1);
        if (hasPositions)
        {
            VarInt.Write(stream, 1);
            VarInt.Write(stream, 0);
        }
    }

    private static void WriteString(Stream stream, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        VarInt.Write(stream, bytes.Length);
        stream.Write(bytes);
    }

    private static void WriteInt64(Stream stream, long value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, value);
        stream.Write(buffer);
    }
}
