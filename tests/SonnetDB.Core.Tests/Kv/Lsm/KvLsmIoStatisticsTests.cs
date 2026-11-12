using SonnetDB.Kv;
using SonnetDB.Kv.Lsm;
using Xunit;

namespace SonnetDB.Core.Tests.Kv.Lsm;

/// <summary>验证成功 SST 作业的会话累计计量与存量、故障及重开的边界。</summary>
public sealed class KvLsmIoStatisticsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-kv-io-stats-" + Guid.NewGuid().ToString("N"));

    /// <summary>累计 flush 与 compaction 大小独立于当前库存；重开只重置计量，不丢数据。</summary>
    [Fact]
    public void CompletedJobs_AccumulateAndResetOnReopen()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using (KvLsmEngine engine = KvLsmEngine.Open(_root, Options()))
        {
            engine.Flush(deadline.Token);
            engine.CompactAll(deadline.Token);
            AssertZero(engine.GetStats());

            engine.Apply([KvBatchMutation.Put("key"u8.ToArray(), "first"u8.ToArray())], deadline.Token);
            engine.Flush(deadline.Token);
            long firstBytes = engine.GetStats().BytesByLevel.Sum();
            Assert.True(firstBytes > 0);
            Assert.Equal(firstBytes, engine.GetStats().CompletedFlushOutputBytes);

            engine.Apply([KvBatchMutation.Put("key"u8.ToArray(), "second"u8.ToArray())], deadline.Token);
            engine.Flush(deadline.Token);
            KvLsmEngineStats before = engine.GetStats();
            Assert.True(before.CompletedFlushOutputBytes > firstBytes);
            Assert.Equal(before.BytesByLevel.Sum(), before.CompletedFlushOutputBytes);
            engine.CompactAll(deadline.Token);
            KvLsmEngineStats after = engine.GetStats();
            Assert.Equal(before.CompletedFlushOutputBytes, after.CompletedFlushOutputBytes);
            Assert.Equal(before.BytesByLevel.Sum(), after.CompletedCompactionInputBytes);
            Assert.Equal(after.BytesByLevel.Sum(), after.CompletedCompactionOutputBytes);
            Assert.True(after.CompletedFlushOutputBytes + after.CompletedCompactionOutputBytes > after.BytesByLevel.Sum());
            Assert.Equal("second"u8.ToArray(), engine.Get("key"u8, deadline.Token)!.Value.ToArray());

            engine.Clear(deadline.Token);
            KvLsmEngineStats cleared = engine.GetStats();
            Assert.Equal(0, cleared.BytesByLevel.Sum());
            Assert.Equal(after.CompletedFlushOutputBytes, cleared.CompletedFlushOutputBytes);
            Assert.Equal(after.CompletedCompactionOutputBytes, cleared.CompletedCompactionOutputBytes);
            engine.Apply([KvBatchMutation.Put("retained"u8.ToArray(), "value"u8.ToArray())], deadline.Token);
            engine.Flush(deadline.Token);
        }

        using KvLsmEngine reopened = KvLsmEngine.Open(_root, Options());
        AssertZero(reopened.GetStats());
        Assert.Equal("value"u8.ToArray(), reopened.Get("retained"u8, deadline.Token)!.Value.ToArray());
    }

    /// <summary>仅修改层号的 trivial move 仍增加作业次数，但不能重复累计输入或输出字节。</summary>
    [Fact]
    public void TrivialMoves_DoNotCountFileBytesAgain()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using KvLsmEngine engine = KvLsmEngine.Open(_root, Options() with
        {
            Compaction = new KvCompactionOptions { Level0CompactionTrigger = 2, MaxBytesForLevelBase = 1024 },
        });
        byte[] value = new byte[8192];
        new Random(42).NextBytes(value);
        for (int round = 0; round < 2; round++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            engine.Apply([KvBatchMutation.Put("key"u8.ToArray(), value)], deadline.Token);
            engine.Flush(deadline.Token);
        }
        KvLsmEngineStats stats = engine.GetStats();
        Assert.True(stats.CompactionCount > 1, "Expected an L0 rewrite followed by a trivial move.");
        Assert.Equal(stats.CompletedFlushOutputBytes, stats.CompletedCompactionInputBytes);
        Assert.Equal(stats.BytesByLevel.Sum(), stats.CompletedCompactionOutputBytes);
        Assert.Equal(value, engine.Get("key"u8, deadline.Token)!.Value.ToArray());
    }

    /// <summary>输出已生成但作业失败的 SST 不计入成功指标；重开恢复仍保留确认写。</summary>
    [Theory]
    [InlineData((int)KvLsmEnginePhase.FlushAfterSstWritten)]
    [InlineData((int)KvLsmEnginePhase.FlushAfterManifestCommit)]
    [InlineData((int)KvLsmEnginePhase.CompactionAfterOutputsWritten)]
    [InlineData((int)KvLsmEnginePhase.CompactionAfterManifestCommit)]
    public void FailedJob_DoesNotCountAsCompleted(int failAtValue)
    {
        var failAt = (KvLsmEnginePhase)failAtValue;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        bool compaction = failAt is KvLsmEnginePhase.CompactionAfterOutputsWritten or KvLsmEnginePhase.CompactionAfterManifestCommit;
        using (KvLsmEngine engine = KvLsmEngine.Open(_root, Options() with
        {
            TestHook = phase => { if (phase == failAt) throw new IOException("injected job failure"); },
        }))
        {
            engine.Apply([KvBatchMutation.Put("key"u8.ToArray(), "value"u8.ToArray())], deadline.Token);
            if (compaction)
            {
                engine.Flush(deadline.Token);
                Assert.Throws<IOException>(() => engine.CompactAll(deadline.Token));
                Assert.True(engine.GetStats().CompletedFlushOutputBytes > 0);
            }
            else
            {
                Assert.Throws<IOException>(() => engine.Flush(deadline.Token));
                Assert.Equal(0, engine.GetStats().CompletedFlushOutputBytes);
            }
            Assert.Equal(0, engine.GetStats().CompletedCompactionInputBytes);
            Assert.Equal(0, engine.GetStats().CompletedCompactionOutputBytes);
        }
        using KvLsmEngine reopened = KvLsmEngine.Open(_root, Options());
        AssertZero(reopened.GetStats());
        Assert.Equal("value"u8.ToArray(), reopened.Get("key"u8, deadline.Token)!.Value.ToArray());
    }

    /// <summary>取消在取得 flush 许可前终止作业，不累计成功字节；之后仍可完整刷盘。</summary>
    [Fact]
    public void CancelledFlush_DoesNotCountUntilCompleted()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using KvLsmEngine engine = KvLsmEngine.Open(_root, Options());
        engine.Apply([KvBatchMutation.Put("key"u8.ToArray(), "value"u8.ToArray())], deadline.Token);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => engine.Flush(cancelled.Token));
        AssertZero(engine.GetStats());
        engine.Flush(deadline.Token);
        Assert.True(engine.GetStats().CompletedFlushOutputBytes > 0);
    }

    private static KvLsmEngineOptions Options() => new()
    {
        BackgroundFlush = false,
        SyncWalOnEveryWrite = true,
        Compaction = new KvCompactionOptions { Level0CompactionTrigger = 20 },
    };

    private static void AssertZero(KvLsmEngineStats stats)
    {
        Assert.Equal(0, stats.CompletedFlushOutputBytes);
        Assert.Equal(0, stats.CompletedCompactionInputBytes);
        Assert.Equal(0, stats.CompletedCompactionOutputBytes);
    }

    /// <summary>只删除本测试拥有的数据目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
