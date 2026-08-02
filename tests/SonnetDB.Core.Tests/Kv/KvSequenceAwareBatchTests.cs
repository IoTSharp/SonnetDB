using System.Buffers.Binary;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Kv;

public sealed class KvSequenceAwareBatchTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-kv-sequence-batch-tests",
        Guid.NewGuid().ToString("N"));

    public KvSequenceAwareBatchTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ApplyBatch_SequenceFactory_EncodesTheCommittedSequence()
    {
        using var keyspace = KvKeyspace.Open("sequence", _root, new KvOptions
        {
            SyncWalOnEveryWrite = true,
            AutoCheckpointEnabled = false,
        });
        keyspace.Put("before", [1]);
        long observedSequence = 0;

        long committedSequence = keyspace.ApplyBatch(sequence =>
        {
            observedSequence = sequence;
            byte[] payload = new byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(payload, sequence);
            return
            [
                KvBatchMutation.Put("document"u8.ToArray(), payload),
                KvBatchMutation.Put("index"u8.ToArray(), payload),
            ];
        });

        Assert.Equal(committedSequence, observedSequence);
        Assert.Equal(committedSequence, BinaryPrimitives.ReadInt64LittleEndian(keyspace.Get("document")!));
        Assert.Equal(committedSequence, BinaryPrimitives.ReadInt64LittleEndian(keyspace.Get("index")!));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
