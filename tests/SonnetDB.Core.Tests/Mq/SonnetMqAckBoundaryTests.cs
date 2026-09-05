using System.Buffers.Binary;
using System.Text;
using SonnetMQ;
using Xunit;

namespace SonnetDB.Core.Tests.Mq;

public sealed class SonnetMqAckBoundaryTests : IDisposable
{
    private const string Topic = "ack.boundaries";
    private const string ConsumerGroup = "workers";
    private readonly string _root = Path.GetFullPath(Path.Combine(
        Path.GetTempPath(), "sonnetmq-ack-boundaries", Guid.NewGuid().ToString("N")));

    [Theory]
    [InlineData(SonnetMqOpenMode.Directory)]
    [InlineData(SonnetMqOpenMode.SingleFile)]
    public void Ack_WithOlderOffset_ReturnsAndPersistsCurrentConsumerPosition(SonnetMqOpenMode mode)
    {
        using (var store = Open(mode))
        {
            PublishThree(store);
            Assert.Equal(3, store.Ack(Topic, ConsumerGroup, 2));

            Assert.Equal(3, store.Ack(Topic, ConsumerGroup, 0));
            Assert.Equal(3, store.GetStats(Topic).ConsumerOffsets[ConsumerGroup]);
            Assert.Empty(store.Pull(Topic, ConsumerGroup, 10));
        }

        AssertLastAckRecord(mode, 3);
        using var reopened = Open(mode);
        Assert.Equal(3, reopened.GetStats(Topic).ConsumerOffsets[ConsumerGroup]);
        Assert.Empty(reopened.Pull(Topic, ConsumerGroup, 10));
    }

    [Theory]
    [InlineData(SonnetMqOpenMode.Directory)]
    [InlineData(SonnetMqOpenMode.SingleFile)]
    public void Ack_WithMaximumOffset_ClampsToPublishedPositionWithoutOverflow(SonnetMqOpenMode mode)
    {
        using (var store = Open(mode))
        {
            PublishThree(store);

            Assert.Equal(3, store.Ack(Topic, ConsumerGroup, long.MaxValue));
            Assert.Equal(3, store.GetStats(Topic).ConsumerOffsets[ConsumerGroup]);
            Assert.Empty(store.Pull(Topic, ConsumerGroup, 10));
        }

        AssertLastAckRecord(mode, 3);
        using var reopened = Open(mode);
        Assert.Equal(3, reopened.GetStats(Topic).ConsumerOffsets[ConsumerGroup]);
        Assert.Equal(3, reopened.Publish(Topic, "later"u8));
        Assert.Equal(3, Assert.Single(reopened.Pull(Topic, ConsumerGroup, 10)).Offset);
    }

    [Theory]
    [InlineData(SonnetMqOpenMode.Directory)]
    [InlineData(SonnetMqOpenMode.SingleFile)]
    public void Ack_WithMaximumOffsetOnEmptyTopic_DoesNotAcknowledgeFutureMessages(SonnetMqOpenMode mode)
    {
        using (var store = Open(mode))
        {
            Assert.Equal(0, store.Ack(Topic, ConsumerGroup, long.MaxValue));
            Assert.Equal(0, store.GetStats(Topic).ConsumerOffsets[ConsumerGroup]);
        }

        AssertLastAckRecord(mode, 0);
        using var reopened = Open(mode);
        Assert.Equal(0, reopened.Publish(Topic, "first"u8));
        Assert.Equal(0, Assert.Single(reopened.Pull(Topic, ConsumerGroup, 10)).Offset);
    }

    [Theory]
    [InlineData(SonnetMqOpenMode.Directory)]
    [InlineData(SonnetMqOpenMode.SingleFile)]
    public void Ack_BeforeRetentionCutoff_ReturnsAndPersistsFirstAvailablePosition(SonnetMqOpenMode mode)
    {
        using (var store = Open(mode))
        {
            PublishThree(store);
            Assert.Equal(2, store.TombstoneBefore(Topic, 2));

            Assert.Equal(2, store.Ack(Topic, ConsumerGroup, 0));
            Assert.Equal(2, store.GetStats(Topic).ConsumerOffsets[ConsumerGroup]);
            Assert.Equal(2, Assert.Single(store.Pull(Topic, ConsumerGroup, 10)).Offset);
        }

        AssertLastAckRecord(mode, 2);
        using var reopened = Open(mode);
        Assert.Equal(2, reopened.GetStats(Topic).ConsumerOffsets[ConsumerGroup]);
        Assert.Equal(2, Assert.Single(reopened.Pull(Topic, ConsumerGroup, 10)).Offset);
    }

    [Theory]
    [InlineData(SonnetMqOpenMode.Directory)]
    [InlineData(SonnetMqOpenMode.SingleFile)]
    public void Ack_WithDuplicateOffset_KeepsConsumerGroupsIndependent(SonnetMqOpenMode mode)
    {
        using (var store = Open(mode))
        {
            PublishThree(store);
            Assert.Equal(3, store.Ack(Topic, "fast", 2));
            Assert.Equal(1, store.Ack(Topic, ConsumerGroup, 0));

            Assert.Equal(1, store.Ack(Topic, ConsumerGroup, 0));
            Assert.Equal(1, store.GetStats(Topic).ConsumerOffsets[ConsumerGroup]);
            Assert.Equal(3, store.GetStats(Topic).ConsumerOffsets["fast"]);
        }

        AssertLastAckRecord(mode, 1);
        using var reopened = Open(mode);
        Assert.Equal(1, reopened.GetStats(Topic).ConsumerOffsets[ConsumerGroup]);
        Assert.Equal(3, reopened.GetStats(Topic).ConsumerOffsets["fast"]);
        Assert.Equal([1L, 2L], reopened.Pull(Topic, ConsumerGroup, 10).Select(message => message.Offset).ToArray());
    }

    [Theory]
    [InlineData(SonnetMqOpenMode.Directory)]
    [InlineData(SonnetMqOpenMode.SingleFile)]
    public void Ack_WithNegativeOffset_RejectsWithoutChangingConsumerPosition(SonnetMqOpenMode mode)
    {
        using (var store = Open(mode))
        {
            PublishThree(store);
            Assert.Equal(1, store.Ack(Topic, ConsumerGroup, 0));

            Assert.Throws<ArgumentOutOfRangeException>(() => store.Ack(Topic, ConsumerGroup, -1));
            Assert.Equal(1, store.GetStats(Topic).ConsumerOffsets[ConsumerGroup]);
        }

        AssertLastAckRecord(mode, 1);
        using var reopened = Open(mode);
        Assert.Equal(1, reopened.GetStats(Topic).ConsumerOffsets[ConsumerGroup]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private SonnetMqStore Open(SonnetMqOpenMode mode)
        => SonnetMqStore.Open(new SonnetMqOptions
        {
            Path = mode == SonnetMqOpenMode.SingleFile ? Path.Combine(_root, "queue.smq") : _root,
            OpenMode = mode,
            SyncOnPublish = true,
            TrimAcknowledgedMessages = false,
            RetentionInterval = TimeSpan.Zero,
        });

    private static void PublishThree(SonnetMqStore store)
        => store.PublishMany(Topic,
        [
            new SonnetMqPublishEntry("first"u8.ToArray()),
            new SonnetMqPublishEntry("second"u8.ToArray()),
            new SonnetMqPublishEntry("third"u8.ToArray()),
        ]);

    private void AssertLastAckRecord(SonnetMqOpenMode mode, long expectedOffset)
    {
        string logPath = mode == SonnetMqOpenMode.SingleFile
            ? Path.Combine(_root, "queue.smq")
            : Assert.Single(Directory.EnumerateFiles(
                Assert.Single(Directory.EnumerateDirectories(_root).Take(2))).Take(2));
        byte[] log = File.ReadAllBytes(logPath);

        // v1 ACK header is followed by topic and consumer-group bytes, with no payload.
        int recordLength = 36 + Encoding.UTF8.GetByteCount(Topic) + Encoding.UTF8.GetByteCount(ConsumerGroup);
        ReadOnlySpan<byte> record = log.AsSpan(log.Length - recordLength);
        Assert.Equal(0x514D_4E53U, BinaryPrimitives.ReadUInt32LittleEndian(record));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(record[4..]));
        Assert.Equal(2, record[6]);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(record[16..]));
        Assert.Equal(expectedOffset, BinaryPrimitives.ReadInt64LittleEndian(record[20..]));
    }
}
