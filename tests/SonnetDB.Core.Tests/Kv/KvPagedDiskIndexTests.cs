using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using SonnetDB.Kv;
using Xunit;
using Xunit.Abstractions;

namespace SonnetDB.Core.Tests.Kv;

/// <summary>验证大状态文件的索引分配、跨页顺序和并发读取保持原有数据合同。</summary>
public sealed class KvPagedDiskIndexTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-paged-index-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    /// <summary>创建当前用例独占的临时目录。</summary>
    public KvPagedDiskIndexTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
    }

    /// <summary>只删除本用例创建的目录。</summary>
    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>十万长键打开时不再为每个键分配常驻索引，同时保持点查及正反向跨页范围结果。</summary>
    [Fact]
    public void LargeStateOpeningAvoidsPerKeyAllocationAndPreservesRangeResults()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        string path = Path.Combine(_root, "large.SDBKVSEG");
        const int count = 100_000;
        KvStateFile.SaveSegment(path, count, Rows(count, deadline.Token), count);
        long before = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        using KvDiskState state = KvStateFile.OpenDiskState(path);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        _output.WriteLine($"entries={count}; allocatedBytes={allocated}; openMilliseconds={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F3}");
        Assert.True(allocated < 4 * 1024 * 1024, $"Opening allocated {allocated:N0} bytes for {count:N0} keys.");
        Assert.Equal(count, state.Count);
        foreach (int index in new[] { 0, 255, 256, 257, 65_535, count - 1 })
            Assert.Equal(index, BinaryPrimitives.ReadInt32LittleEndian(state.Get(Key(index))!.Value));
        Assert.Null(state.Get(Key(count)));
        byte[] prefix = Encoding.UTF8.GetBytes("delivery/");
        int[] expected = Enumerable.Range(250, 20).ToArray();
        Assert.Equal(expected, state.ScanRange(prefix, Key(250), Key(270), null)
            .Select(entry => BinaryPrimitives.ReadInt32LittleEndian(state.Read(entry).Value)).ToArray());
        Assert.Equal(expected.Reverse(), state.ScanRange(prefix, Key(250), Key(270), null, descending: true)
            .Select(entry => BinaryPrimitives.ReadInt32LittleEndian(state.Read(entry).Value)).ToArray());
    }

    /// <summary>已取得的快照租约跨 owner 关闭仍可并发读取不同页，避免分页改造破坏文件生命周期。</summary>
    [Fact]
    public async Task ConcurrentPagesRemainReadableThroughExistingLease()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        string path = Path.Combine(_root, "leased.SDBKVSEG");
        KvStateFile.SaveSegment(path, 4097, Rows(4097, deadline.Token), 4097);
        KvDiskState state = KvStateFile.OpenDiskState(path);
        using KvDiskStateLease lease = state.AcquireLease();
        state.Dispose();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int attempt = 0; attempt < 32; attempt++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                int index = (worker * 509 + attempt * 257) % 4097;
                Assert.Equal(index, BinaryPrimitives.ReadInt32LittleEndian(lease.State.Get(Key(index), deadline.Token)!.Value));
            }
        }, deadline.Token))).WaitAsync(deadline.Token);
    }

    /// <summary>按固定顺序生成重复前缀长键，覆盖文件中的键前缀压缩及页边界。</summary>
    private static IEnumerable<KeyValuePair<byte[], KvValueEntry>> Rows(int count, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        for (int i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(45)) throw new TimeoutException();
            byte[] value = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(value, i);
            yield return new KeyValuePair<byte[], KvValueEntry>(Key(i), new KvValueEntry(value, i + 1));
        }
    }

    /// <summary>生成可按字节序比较的业务索引样本键。</summary>
    private static byte[] Key(int index) => Encoding.UTF8.GetBytes("delivery/" + new string('x', 200) + index.ToString("D9"));
}
