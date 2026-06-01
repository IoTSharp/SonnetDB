using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Engine.Retention;
using SonnetDB.Kv;
using SonnetDB.Memory;
using SonnetDB.Query.Functions.Control;
using SonnetDB.Storage.Segments;
using SonnetDB.Wal;
using Xunit;

namespace SonnetDB.Core.Tests.Engine;

public sealed class OptionsRecordTests
{
    [Fact]
    public void TsdbOptions_ObjectInitializer_RemainsCompatible()
    {
        var options = new TsdbOptions
        {
            RootDirectory = "data",
            FlushPolicy = new MemTableFlushPolicy { MaxPoints = 123 },
            SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
            SegmentReaderOptions = new SegmentReaderOptions { VerifyBlockCrc = false },
            WalGroupCommit = new WalGroupCommitOptions { FlushWindow = TimeSpan.FromMilliseconds(5) },
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
            WalRolling = new WalRollingPolicy { Enabled = false },
            Retention = new RetentionPolicy { Enabled = true, Ttl = TimeSpan.FromDays(7) },
            Kv = new KvOptions { DefaultScanLimit = 32 },
        };

        Assert.Equal("data", options.RootDirectory);
        Assert.Equal(123, options.FlushPolicy.MaxPoints);
        Assert.False(options.SegmentWriterOptions.FsyncOnCommit);
        Assert.False(options.SegmentReaderOptions.VerifyBlockCrc);
        Assert.Equal(TimeSpan.FromMilliseconds(5), options.WalGroupCommit.FlushWindow);
        Assert.False(options.BackgroundFlush.Enabled);
        Assert.False(options.Compaction.Enabled);
        Assert.False(options.WalRolling.Enabled);
        Assert.True(options.Retention.Enabled);
        Assert.Equal(32, options.Kv.DefaultScanLimit);
    }

    [Fact]
    public void TsdbOptions_WithExpression_CreatesIndependentSnapshot()
    {
        var original = new TsdbOptions
        {
            RootDirectory = "a",
            FlushPolicy = new MemTableFlushPolicy { MaxPoints = 10 },
            SegmentReaderOptions = new SegmentReaderOptions { DecodeBlockCacheMaxBytes = 1024 },
        };

        var changed = original with
        {
            RootDirectory = "b",
            FlushPolicy = original.FlushPolicy with { MaxPoints = 20 },
            SegmentReaderOptions = original.SegmentReaderOptions with { DecodeBlockCacheMaxBytes = 2048 },
        };

        Assert.Equal("a", original.RootDirectory);
        Assert.Equal(10, original.FlushPolicy.MaxPoints);
        Assert.Equal(1024, original.SegmentReaderOptions.DecodeBlockCacheMaxBytes);

        Assert.Equal("b", changed.RootDirectory);
        Assert.Equal(20, changed.FlushPolicy.MaxPoints);
        Assert.Equal(2048, changed.SegmentReaderOptions.DecodeBlockCacheMaxBytes);
    }

    [Fact]
    public void OptionsRecords_WithSameValues_AreEqual()
    {
        Assert.Equal(
            new MemTableFlushPolicy { MaxBytes = 1, MaxPoints = 2, MaxAge = TimeSpan.FromSeconds(3) },
            new MemTableFlushPolicy { MaxBytes = 1, MaxPoints = 2, MaxAge = TimeSpan.FromSeconds(3) });

        Assert.Equal(
            new PidEstimationOptions { Method = PidTuningMethod.Imc, ImcLambda = 5.0 },
            new PidEstimationOptions { Method = PidTuningMethod.Imc, ImcLambda = 5.0 });
    }
}
