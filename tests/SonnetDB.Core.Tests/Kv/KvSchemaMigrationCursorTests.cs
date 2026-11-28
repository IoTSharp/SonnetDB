using System.Buffers.Binary;
using SonnetDB.Exceptions;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Kv;

/// <summary>验证迁移游标的排他边界、重开续扫和页预算。</summary>
public sealed class KvSchemaMigrationCursorTests : IDisposable
{
    private const byte RowPrefix = (byte)'r';
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-migration-cursor-" + Guid.NewGuid().ToString("N"));

    /// <summary>续扫在重开后保持原始键排序，并且只读取该前缀下的最新可见值。</summary>
    [Theory]
    [InlineData(KvStorageEngine.Legacy)]
    [InlineData(KvStorageEngine.Lsm)]
    public void RestartsAfterExclusiveKeyAndPreservesRawOrder(KvStorageEngine engine)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        KvOptions options = KvOptions.Default with { StorageEngine = engine };
        byte[] firstKey = [RowPrefix, 0x10];
        byte[] deletedKey = [RowPrefix, 0x10, 0x00];
        byte[] secondKey = [RowPrefix, 0x10, 0x01];
        byte[] thirdKey = [RowPrefix, 0x20];
        byte[] cursor;
        using (var space = KvKeyspace.Open("items", _root, options))
        {
            Assert.Equal(engine, space.StorageEngine);
            space.Put(firstKey, new byte[12]);
            space.Put(deletedKey, new byte[12]);
            space.Put(secondKey, new byte[12]);
            space.Put(thirdKey, new byte[12]);
            space.Put([(byte)'s', 0], new byte[12]);
            space.Compact();
            byte[] replacement = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];
            space.Put(firstKey, replacement);
            Assert.True(space.Delete(deletedKey));

            KvBoundedReadPage first = space.ReadSchemaMigrationPage(RowPrefix, null, 10, 17, deadline.Token);
            KvEntry entry = Assert.Single(first.Entries);
            Assert.Equal(firstKey, entry.Key.ToArray());
            Assert.Equal(replacement, entry.Value.ToArray());
            Assert.True(first.Truncated);
            cursor = Assert.IsType<byte[]>(first.NextKey).ToArray();
            Assert.InRange(KvKeyComparer.Instance.Compare(cursor, firstKey), 0, 1);
            Assert.True(KvKeyComparer.Instance.Compare(cursor, secondKey) < 0);
        }

        using (var space = KvKeyspace.Open("items", _root, options))
        {
            KvBoundedReadPage second = space.ReadSchemaMigrationPage(RowPrefix, cursor, 1, 17, deadline.Token);
            Assert.Equal(secondKey, Assert.Single(second.Entries).Key.ToArray());
            Assert.True(second.Truncated);
            Assert.Equal(secondKey, second.NextKey);

            KvBoundedReadPage last = space.ReadSchemaMigrationPage(RowPrefix, second.NextKey, 1, 17, deadline.Token);
            Assert.Equal(thirdKey, Assert.Single(last.Entries).Key.ToArray());
            Assert.False(last.Truncated);
            Assert.Null(last.NextKey);
        }
    }

    /// <summary>Legacy 检查候选上限时也推进纯墓碑页，下一页不再扫描同一批墓碑。</summary>
    [Fact]
    public void LegacyTombstoneOnlyPageAdvancesCheckpoint()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var space = KvKeyspace.Open("items", _root, KvOptions.Default with { StorageEngine = KvStorageEngine.Legacy });
        const int tombstoneCount = 4096;
        var puts = new KvBatchMutation[tombstoneCount + 1];
        var deletes = new KvBatchMutation[tombstoneCount];
        for (int index = 0; index <= tombstoneCount; index++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            byte[] key = new byte[3];
            key[0] = RowPrefix;
            BinaryPrimitives.WriteUInt16BigEndian(key.AsSpan(1), (ushort)index);
            puts[index] = KvBatchMutation.Put(key, [1]);
            if (index < tombstoneCount)
                deletes[index] = KvBatchMutation.Delete(key);
        }
        space.ApplyBatch(puts);
        space.Compact();
        space.ApplyBatch(deletes);

        KvBoundedReadPage first = space.ReadSchemaMigrationPage(RowPrefix, null, 1, 64, deadline.Token);
        Assert.Empty(first.Entries);
        Assert.True(first.Truncated);
        Assert.Equal(new byte[] { RowPrefix, 15, 255 }, first.NextKey);
        KvBoundedReadPage next = space.ReadSchemaMigrationPage(RowPrefix, first.NextKey, 1, 64, deadline.Token);
        Assert.Equal(new byte[] { RowPrefix, 16, 0 }, Assert.Single(next.Entries).Key.ToArray());
        Assert.False(next.Truncated);
    }

    /// <summary>预算拒绝时 Legacy 不读取磁盘值，LSM 值投影在复制前执行预算回调。</summary>
    [Theory]
    [InlineData(KvStorageEngine.Legacy)]
    [InlineData(KvStorageEngine.Lsm)]
    public void RejectsOversizedFirstRowAndCancelsBeforeReading(KvStorageEngine engine)
    {
        const int valueBytes = 4 * 1024 * 1024;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var space = KvKeyspace.Open("items", _root, KvOptions.Default with { StorageEngine = engine });
        byte[] key = [RowPrefix, 1];
        space.Put(key, new byte[valueBytes]);
        int readCount = 0;
        if (engine == KvStorageEngine.Legacy)
        {
            space.Compact();
            space.ConfigureDiskReadTestHook(() => readCount++);
        }

        Assert.Throws<PayloadLimitExceededException>(() => space.ReadSchemaMigrationPage(RowPrefix, null, 1, 1024, deadline.Token));
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<PayloadLimitExceededException>(() => space.ReadSchemaMigrationValue(
            key, length => throw new PayloadLimitExceededException(length), deadline.Token));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < valueBytes / 2, $"预算拒绝额外分配了 {allocated} 字节。");
        Assert.Equal(0, readCount);

        deadline.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => space.ReadSchemaMigrationPage(RowPrefix, null, 1, valueBytes + 2, deadline.Token));
        Assert.Equal(0, readCount);
    }

    /// <summary>清理本用例独占的临时数据库。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
