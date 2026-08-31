using System.Text;
using SonnetDB.SemanticContent;

namespace SonnetDB.Core.Tests.SemanticContent;

/// <summary>
/// 通用 RAG 文本分块的确定性、边界和预算测试。
/// </summary>
public sealed class RagTextChunkerTests
{
    [Fact]
    public void Chunk_WithSameInput_ReturnsDeterministicHashIdsAndOffsets()
    {
        const string text = "hello";

        var first = RagTextChunker.Chunk("manual/intro", text);
        var second = RagTextChunker.Chunk("manual/intro", text);

        Assert.Equal(
            "sha256:2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824",
            first.ContentHash);
        Assert.Equal(first.ContentId, second.ContentId);
        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.Equal(first.Chunks, second.Chunks);
        var chunk = Assert.Single(first.Chunks);
        Assert.StartsWith("rag:", chunk.Id, StringComparison.Ordinal);
        Assert.Equal(first.ContentHash, chunk.ContentHash);
        Assert.Equal(0, chunk.StartOffset);
        Assert.Equal(text.Length, chunk.EndOffset);
        Assert.Equal(text, chunk.Text);
    }

    [Fact]
    public void Chunk_WithWhitespaceAndSurrogatePairs_PreservesSourceBoundaries()
    {
        const string text = "alpha 😀 beta\ngamma delta epsilon";
        var options = new RagTextChunkingOptions
        {
            MaxCharacters = 10,
            OverlapCharacters = 2,
        };

        var snapshot = RagTextChunker.Chunk("unicode", text, options);

        Assert.True(snapshot.Chunks.Count > 1);
        Assert.Equal(snapshot.Chunks.Count, snapshot.Chunks.Select(chunk => chunk.Id).Distinct().Count());
        foreach (var chunk in snapshot.Chunks)
        {
            int start = checked((int)chunk.StartOffset!.Value);
            int end = checked((int)chunk.EndOffset!.Value);
            Assert.InRange(chunk.Text.Length, 1, options.MaxCharacters);
            Assert.Equal(text[start..end], chunk.Text);
            Assert.False(char.IsLowSurrogate(chunk.Text[0]));
            Assert.False(char.IsHighSurrogate(chunk.Text[^1]));
        }
    }

    [Fact]
    public void Chunk_WhenOverlapLandsInsideLeadingSurrogatePair_MakesForwardProgress()
    {
        var snapshot = RagTextChunker.Chunk(
            "emoji",
            "😀x",
            new RagTextChunkingOptions
            {
                MaxCharacters = 2,
                OverlapCharacters = 1,
                MaxChunks = 2,
            });

        Assert.Collection(
            snapshot.Chunks,
            chunk =>
            {
                Assert.Equal("😀", chunk.Text);
                Assert.Equal(0, chunk.StartOffset);
                Assert.Equal(2, chunk.EndOffset);
            },
            chunk =>
            {
                Assert.Equal("x", chunk.Text);
                Assert.Equal(2, chunk.StartOffset);
                Assert.Equal(3, chunk.EndOffset);
            });
    }

    [Fact]
    public void Chunk_WithDistinctUnpairedSurrogates_RejectsInsteadOfHashingReplacementBytes()
    {
        string unpairedHigh = new(['\ud800']);
        string unpairedLow = new(['\udc00']);

        Assert.Throws<EncoderFallbackException>(() =>
            RagTextChunker.Chunk("invalid-high", unpairedHigh));
        Assert.Throws<EncoderFallbackException>(() =>
            RagTextChunker.Chunk("invalid-low", unpairedLow));
    }

    [Fact]
    public void Chunk_WithInvalidOrOversizedContentId_RejectsBeforeEmptyTextShortcut()
    {
        string invalidContentId = "content-" + new string(['\ud800']);

        Assert.Throws<EncoderFallbackException>(() =>
            RagTextChunker.Chunk(invalidContentId, string.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RagTextChunker.Chunk(
                "12345",
                string.Empty,
                new RagTextChunkingOptions { MaxContentIdCharacters = 4 }));
    }

    [Fact]
    public void Chunk_WithEmptyOrWhitespaceOnlyText_ReturnsNoChunksAndStableHash()
    {
        var empty = RagTextChunker.Chunk("empty", string.Empty);
        var whitespace = RagTextChunker.Chunk("space", " \r\n\t");

        Assert.Equal(
            "sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            empty.ContentHash);
        Assert.Empty(empty.Chunks);
        Assert.Empty(whitespace.Chunks);
        Assert.NotEqual(empty.ContentHash, whitespace.ContentHash);
    }

    [Fact]
    public void Chunk_WhenInputOrChunkCountExceedsBudget_FailsBeforeUnboundedGrowth()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RagTextChunker.Chunk(
            "large",
            "12345",
            new RagTextChunkingOptions { MaxInputCharacters = 4 }));

        Assert.Throws<InvalidOperationException>(() => RagTextChunker.Chunk(
            "many",
            "abcdefghijklmnop",
            new RagTextChunkingOptions
            {
                MaxCharacters = 4,
                OverlapCharacters = 0,
                MaxChunks = 2,
            }));
    }

    [Fact]
    public void Chunk_WithInvalidOverlapOrCanceledToken_RejectsWork()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RagTextChunker.Chunk(
            "invalid",
            "text",
            new RagTextChunkingOptions
            {
                MaxCharacters = 4,
                OverlapCharacters = 4,
            }));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => RagTextChunker.Chunk(
            "cancel",
            "text",
            cancellationToken: cancellation.Token));
    }
}
