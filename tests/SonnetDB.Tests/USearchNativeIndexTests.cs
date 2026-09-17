using System.Collections;
using System.Runtime.InteropServices;
using Cloud.Unum.USearch;
using SonnetDB.Configuration;
using SonnetDB.SemanticSearch;
using Xunit;

namespace SonnetDB.Tests;

public sealed class USearchNativeIndexTests
{
    [Fact]
    public void SearchFiltered_WithCloserExcludedVectors_ReturnsOnlyAllowedKeys()
    {
        if (!USearchSemanticIndexRegistry.IsSupportedPlatform)
            return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var index = new USearchNativeIndex(3);
        for (ulong key = 0; key < 32; key++)
        {
            timeout.Token.ThrowIfCancellationRequested();
            index.Add(key, [1, 0, 0]);
        }
        index.Add(100, [0, 1, 0]);
        index.Add(101, [0, 0, 1]);
        int count = index.SearchFiltered([1, 0, 0], 2, new HashSet<ulong> { 100, 101 }, 512,
            timeout.Token, out ulong[] keys, out float[] distances, out bool exceeded);
        Assert.False(exceeded);
        Assert.Equal(2, count);
        Assert.Equal(new ulong[] { 100, 101 }, keys.Order().ToArray());
        Assert.All(distances, distance => Assert.Equal(1f, distance, 5));
        index.Remove(100);
        index.Add(100, [1, 0, 0]);
        Assert.Equal(1, index.SearchFiltered([1, 0, 0], 1, new HashSet<ulong> { 100 }, 512,
            timeout.Token, out keys, out distances, out exceeded));
        Assert.False(exceeded);
        Assert.Equal(100UL, keys[0]);
        Assert.Equal(0f, distances[0], 5);
    }

    [Fact]
    public void SearchFiltered_WithSmallBudget_ReportsExhaustion()
    {
        if (!USearchSemanticIndexRegistry.IsSupportedPlatform)
            return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var index = new USearchNativeIndex(3);
        for (ulong key = 0; key < 8; key++)
        {
            timeout.Token.ThrowIfCancellationRequested();
            index.Add(key, [1, 0, 0]);
        }
        _ = index.SearchFiltered([1, 0, 0], 1, new HashSet<ulong>(), 1, timeout.Token,
            out _, out _, out bool exceeded);
        Assert.True(exceeded);
    }

    [Fact]
    public void SearchFiltered_WithCanceledToken_ThrowsAndKeepsIndexUsable()
    {
        if (!USearchSemanticIndexRegistry.IsSupportedPlatform)
            return;
        using var index = new USearchNativeIndex(3);
        Assert.Equal(0, index.Search([1, 0, 0], 1, out _, out _));
        index.Add(1, [1, 0, 0]);
        Assert.ThrowsAny<OperationCanceledException>(() => index.SearchFiltered(
            [1, 0, 0], 1, new HashSet<ulong> { 1 }, 512, new CancellationToken(true), out _, out _, out _));
        Assert.Equal(1, index.Search([1, 0, 0], 1, out _, out _));
        Assert.Throws<ArgumentException>(() => index.Search([1, 0], 1, out _, out _));
    }

    [Fact]
    public void BoundedCopy_WithInvalidOptions_ClampsAndFreezesBudgets()
    {
        var original = new SemanticSearchQueryOptions
        {
            AnnCandidateLimit = 0,
            ExactCompensationLimit = -1,
            CandidatePageSize = int.MaxValue,
            MaxScannedCandidates = 0,
            TimeoutMilliseconds = -1,
        };
        SemanticSearchQueryOptions bounded = original.BoundedCopy();
        original.MaxScannedCandidates = 100;
        Assert.Equal(1, bounded.AnnCandidateLimit);
        Assert.Equal(0, bounded.ExactCompensationLimit);
        Assert.Equal(4096, bounded.CandidatePageSize);
        Assert.Equal(1, bounded.MaxScannedCandidates);
        Assert.Equal(1, bounded.TimeoutMilliseconds);
    }

    [Fact]
    public void NativeAbi_OnSupportedRuntime_MatchesPinnedHeaderLayout()
    {
        if (!USearchSemanticIndexRegistry.IsSupportedPlatform)
            return;
        Assert.Equal(64, Marshal.SizeOf<IndexOptions>());
        Assert.Equal((nint)8, Marshal.OffsetOf<IndexOptions>(nameof(IndexOptions.metric)));
        Assert.Equal((nint)24, Marshal.OffsetOf<IndexOptions>(nameof(IndexOptions.dimensions)));
        Assert.Equal((nint)56, Marshal.OffsetOf<IndexOptions>(nameof(IndexOptions.multi)));
        Assert.Equal(1, (int)ScalarKind.Float32);
        Assert.Equal(1, (int)MetricKind.Cos);
    }

    [Fact]
    public void SearchFiltered_WithCallbackException_RethrowsAfterNativeReturn()
    {
        if (!USearchSemanticIndexRegistry.IsSupportedPlatform)
            return;
        using var index = new USearchNativeIndex(3);
        index.Add(1, [1, 0, 0]);
        var expected = new InvalidOperationException("predicate failure");
        var actual = Assert.Throws<InvalidOperationException>(() => index.SearchFiltered(
            [1, 0, 0], 1, new CallbackSet(() => throw expected), 512, CancellationToken.None,
            out _, out _, out _));
        Assert.Same(expected, actual);
        Assert.Equal(1, index.Search([1, 0, 0], 1, out _, out _));
    }

    [Fact]
    public void SearchFiltered_WithCancellationDuringCallback_DiscardsNativeResult()
    {
        if (!USearchSemanticIndexRegistry.IsSupportedPlatform)
            return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var index = new USearchNativeIndex(3);
        index.Add(1, [1, 0, 0]);
        Assert.ThrowsAny<OperationCanceledException>(() => index.SearchFiltered(
            [1, 0, 0], 1, new CallbackSet(timeout.Cancel), 512, timeout.Token, out _, out _, out _));
        Assert.Equal(1, index.Search([1, 0, 0], 1, out _, out _));
    }

    private sealed class CallbackSet(Action callback) : IReadOnlySet<ulong>
    {
        public int Count => 1;
        public bool Contains(ulong item) { callback(); return true; }
        public bool IsProperSubsetOf(IEnumerable<ulong> other) => throw new NotSupportedException();
        public bool IsProperSupersetOf(IEnumerable<ulong> other) => throw new NotSupportedException();
        public bool IsSubsetOf(IEnumerable<ulong> other) => throw new NotSupportedException();
        public bool IsSupersetOf(IEnumerable<ulong> other) => throw new NotSupportedException();
        public bool Overlaps(IEnumerable<ulong> other) => throw new NotSupportedException();
        public bool SetEquals(IEnumerable<ulong> other) => throw new NotSupportedException();
        public IEnumerator<ulong> GetEnumerator() => throw new NotSupportedException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
