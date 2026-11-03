using System.Text;
using SonnetDB.Exceptions;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Kv;

public sealed class KvBoundedReadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-bounded-read-" + Guid.NewGuid().ToString("N"));

    public KvBoundedReadTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetEntryBounded_OversizedStoredValue_RejectsBeforeCopyOrPhysicalRead(bool cold)
    {
        using var space = KvKeyspace.Open("test", _root, KvOptions.Default);
        space.Put("large", new byte[4 * 1024 * 1024]);
        if (cold)
            space.Compact();
        int reads = 0;
        if (cold)
            space.ConfigureDiskReadTestHook(() => reads++);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<PayloadLimitExceededException>(() => space.GetEntryBounded(Encoding.UTF8.GetBytes("large"), 65536));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 512 * 1024, $"预算拒绝额外分配了 {allocated} 字节。");
        Assert.Equal(0, reads);
        // 原生不带预算入口仍保留原有大值合同。
        Assert.Equal(4 * 1024 * 1024, space.GetEntry("large")!.Value.Length);
    }

    [Fact]
    public void ScanPrefixBounded_ColdLargePage_ReadsOnlyValuesThatFit()
    {
        using var space = KvKeyspace.Open("test", _root, KvOptions.Default);
        byte[] value = new byte[65536];
        for (int index = 0; index < 20; index++)
            space.Put($"key{index:D2}", value);
        space.Compact();
        int reads = 0;
        space.ConfigureDiskReadTestHook(() => reads++);
        var first = space.ScanPrefixBounded("key"u8.ToArray(), default, 1000, 65536, 80000);
        Assert.Single(first.Entries);
        Assert.True(first.Truncated);
        Assert.Equal(1, reads);
        var next = space.ScanPrefixBounded("key"u8.ToArray(), first.NextKey!, 1000, 65536, 80000);
        Assert.Single(next.Entries);
        Assert.NotEqual(first.Entries[0].Key.ToArray(), next.Entries[0].Key.ToArray());
        Assert.Equal(2, reads);
    }

    [Fact]
    public void ScanPrefixBounded_OversizedNextValue_ReturnsCursorThenExplicitFailure()
    {
        using var space = KvKeyspace.Open("test", _root, KvOptions.Default);
        space.Put("a", new byte[10]);
        space.Put("b", new byte[4 * 1024 * 1024]);
        space.Compact();
        int reads = 0;
        space.ConfigureDiskReadTestHook(() => reads++);
        var first = space.ScanPrefixBounded(default, default, 1000, 65536, 80000);
        Assert.Single(first.Entries);
        Assert.True(first.Truncated);
        Assert.Equal(1, reads);
        Assert.Throws<PayloadLimitExceededException>(() => space.ScanPrefixBounded(default, first.NextKey!, 1000, 65536, 80000));
        Assert.Equal(1, reads);
    }

    [Fact]
    public void ScanPrefixBounded_CancelledCall_DoesNotReadStorage()
    {
        using var space = KvKeyspace.Open("test", _root, KvOptions.Default);
        space.Put("a", new byte[10]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => space.ScanPrefixBounded(default, default, 10, 65536, 80000, cancellation.Token));
    }
}
