using System.Buffers.Binary;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Kv;

/// <summary>
/// 验证依赖提交序列的 KV 批处理语义。
/// </summary>
public sealed class KvSequenceAwareBatchTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-kv-sequence-batch-tests",
        Guid.NewGuid().ToString("N"));

    /// <summary>
    /// 创建独立的临时 keyspace 目录。
    /// </summary>
    public KvSequenceAwareBatchTests() => Directory.CreateDirectory(_root);

    /// <summary>
    /// 验证序列工厂拿到的版本与实际原子批次提交版本一致。
    /// </summary>
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

    /// <summary>
    /// 验证 sequence factory 在锁外运行，竞争写入后会按新 sequence 重建并保持整批版本一致。
    /// </summary>
    [Fact]
    public async Task ApplyBatch_SequenceFactory_RebuildsOutsideWriteLockAfterConcurrentWrite()
    {
        using var keyspace = KvKeyspace.Open("sequence-rebuild", _root, new KvOptions
        {
            SyncWalOnEveryWrite = true,
            AutoCheckpointEnabled = false,
        });
        using var factoryStarted = new ManualResetEventSlim();
        using var releaseFactory = new ManualResetEventSlim();
        int factoryCalls = 0;
        long observedSequence = 0;

        Task<long> batch = Task.Run(() => keyspace.ApplyBatch(sequence =>
        {
            int call = Interlocked.Increment(ref factoryCalls);
            Volatile.Write(ref observedSequence, sequence);
            if (call == 1)
            {
                factoryStarted.Set();
                releaseFactory.Wait(TimeSpan.FromSeconds(10));
            }

            return CreateSequenceMutations(sequence);
        }));
        try
        {
            Assert.True(factoryStarted.Wait(TimeSpan.FromSeconds(10)));

            Task<long> concurrentWrite = Task.Run(() => keyspace.Put("concurrent", [3]));
            long concurrentSequence = await concurrentWrite.WaitAsync(TimeSpan.FromSeconds(10));

            releaseFactory.Set();
            long batchSequence = await batch.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(concurrentSequence + 1, batchSequence);
            Assert.Equal(2, factoryCalls);
            Assert.Equal(batchSequence, Volatile.Read(ref observedSequence));
            Assert.Equal(batchSequence, BinaryPrimitives.ReadInt64LittleEndian(keyspace.Get("document")!));
            Assert.Equal(batchSequence, BinaryPrimitives.ReadInt64LittleEndian(keyspace.Get("index")!));
            Assert.Equal([3], keyspace.Get("concurrent"));
        }
        finally
        {
            releaseFactory.Set();
        }
    }

    /// <summary>
    /// 验证连续竞争达到上限后会在写锁内完成最终构造，避免 sequence factory 永久饥饿。
    /// </summary>
    [Fact]
    public async Task ApplyBatch_SequenceFactoryFallsBackUnderLockAfterRepeatedInvalidation()
    {
        using var keyspace = KvKeyspace.Open("sequence-fairness", _root, new KvOptions
        {
            SyncWalOnEveryWrite = true,
            AutoCheckpointEnabled = false,
        });
        using var factoryEntered = new SemaphoreSlim(0);
        using var releaseFactory = new SemaphoreSlim(0);
        int factoryCalls = 0;

        Task<long> batch = Task.Run(() => keyspace.ApplyBatch(sequence =>
        {
            Interlocked.Increment(ref factoryCalls);
            factoryEntered.Release();
            Assert.True(releaseFactory.Wait(TimeSpan.FromSeconds(10)));
            return CreateSequenceMutations(sequence);
        }));

        // 前八次构造均由竞争写使 sequence 失效，覆盖乐观重建上限。
        for (int attempt = 0; attempt < 8; attempt++)
        {
            Assert.True(await factoryEntered.WaitAsync(TimeSpan.FromSeconds(10)));
            _ = keyspace.Put($"competitor-{attempt}", [(byte)attempt]);
            releaseFactory.Release();
        }

        Assert.True(await factoryEntered.WaitAsync(TimeSpan.FromSeconds(10)));
        Task<long> blockedWriter = Task.Run(() => keyspace.Put("blocked-competitor", [9]));
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.False(blockedWriter.IsCompleted);

        releaseFactory.Release();
        long batchSequence = await batch.WaitAsync(TimeSpan.FromSeconds(10));
        long competingSequence = await blockedWriter.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(9, factoryCalls);
        Assert.Equal(batchSequence + 1, competingSequence);
        Assert.Equal(batchSequence, BinaryPrimitives.ReadInt64LittleEndian(keyspace.Get("document")!));
    }

    /// <summary>工厂首次返回空批次时，竞争写改变 sequence 后仍必须重建并提交新结果。</summary>
    [Fact]
    public async Task ApplyBatch_EmptySequenceFactoryResult_RebuildsAfterConcurrentWrite()
    {
        using var keyspace = KvKeyspace.Open("empty-sequence-rebuild", _root, new KvOptions
        {
            SyncWalOnEveryWrite = true,
            AutoCheckpointEnabled = false,
        });
        using var factoryStarted = new ManualResetEventSlim();
        using var releaseFactory = new ManualResetEventSlim();
        int factoryCalls = 0;

        Task<long> batch = Task.Run(() => keyspace.ApplyBatch(sequence =>
        {
            if (Interlocked.Increment(ref factoryCalls) == 1)
            {
                factoryStarted.Set();
                releaseFactory.Wait(TimeSpan.FromSeconds(10));
                return Array.Empty<KvBatchMutation>();
            }

            return CreateSequenceMutations(sequence);
        }));
        try
        {
            Assert.True(factoryStarted.Wait(TimeSpan.FromSeconds(10)));
            long concurrentSequence = keyspace.Put("concurrent-empty", [4]);

            releaseFactory.Set();
            long batchSequence = await batch.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(concurrentSequence + 1, batchSequence);
            Assert.Equal(2, factoryCalls);
            Assert.Equal(batchSequence, BinaryPrimitives.ReadInt64LittleEndian(keyspace.Get("document")!));
        }
        finally
        {
            releaseFactory.Set();
        }
    }

    /// <summary>
    /// 验证普通批次只在锁外读取一次，输入阻塞时不妨碍其他原子写入。
    /// </summary>
    [Fact]
    public async Task ApplyBatch_MaterializesInputOnceBeforeTakingWriteLock()
    {
        using var keyspace = KvKeyspace.Open("materialize", _root, new KvOptions
        {
            SyncWalOnEveryWrite = true,
            AutoCheckpointEnabled = false,
        });
        using var inputReadStarted = new ManualResetEventSlim();
        using var releaseInputRead = new ManualResetEventSlim();
        var mutations = new BlockingMutationList(
        [
            KvBatchMutation.Put("first"u8.ToArray(), [1]),
            KvBatchMutation.Put("second"u8.ToArray(), [2]),
        ],
        () =>
        {
            inputReadStarted.Set();
            releaseInputRead.Wait(TimeSpan.FromSeconds(10));
        });

        Task<long> batch = Task.Run(() => keyspace.ApplyBatch(mutations));
        try
        {
            Assert.True(inputReadStarted.Wait(TimeSpan.FromSeconds(10)));

            Task<long> concurrentWrite = Task.Run(() => keyspace.Put("concurrent", [3]));
            long concurrentSequence = await concurrentWrite.WaitAsync(TimeSpan.FromSeconds(10));

            releaseInputRead.Set();
            long batchSequence = await batch.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(concurrentSequence + 1, batchSequence);
            Assert.Equal(2, mutations.IndexerReadCount);
            Assert.Equal([1], keyspace.Get("first"));
            Assert.Equal([2], keyspace.Get("second"));
            Assert.Equal([3], keyspace.Get("concurrent"));
        }
        finally
        {
            releaseInputRead.Set();
        }
    }

    /// <summary>
    /// 清理测试目录。
    /// </summary>
    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>
    /// 首次读取时阻塞的只读批次，用于观察输入读取是否占用 keyspace 写锁。
    /// </summary>
    private sealed class BlockingMutationList : IReadOnlyList<KvBatchMutation>
    {
        private readonly KvBatchMutation[] _items;
        private readonly Action _onFirstRead;
        private int _firstRead;
        private int _indexerReadCount;

        /// <summary>
        /// 创建具有指定阻塞回调的批次包装。
        /// </summary>
        public BlockingMutationList(KvBatchMutation[] items, Action onFirstRead)
        {
            _items = items;
            _onFirstRead = onFirstRead;
        }

        /// <summary>
        /// 返回批次项数。
        /// </summary>
        public int Count => _items.Length;

        /// <summary>
        /// 返回指定变更，并在首次读取时通知测试线程。
        /// </summary>
        public KvBatchMutation this[int index]
        {
            get
            {
                Interlocked.Increment(ref _indexerReadCount);
                if (Interlocked.Exchange(ref _firstRead, 1) == 0)
                    _onFirstRead();
                return _items[index];
            }
        }

        /// <summary>
        /// 返回通过索引器读取的元素次数。
        /// </summary>
        public int IndexerReadCount => Volatile.Read(ref _indexerReadCount);

        /// <summary>
        /// 返回数组枚举器；生产代码应直接按索引读取该集合。
        /// </summary>
        public IEnumerator<KvBatchMutation> GetEnumerator()
            => ((IEnumerable<KvBatchMutation>)_items).GetEnumerator();

        /// <summary>
        /// 返回非泛型枚举器。
        /// </summary>
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => GetEnumerator();
    }

    /// <summary>
    /// 生成把指定 sequence 写入两个 key 的同一原子批次。
    /// </summary>
    private static IReadOnlyList<KvBatchMutation> CreateSequenceMutations(long sequence)
    {
        byte[] payload = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(payload, sequence);
        return
        [
            KvBatchMutation.Put("document"u8.ToArray(), payload),
            KvBatchMutation.Put("index"u8.ToArray(), payload),
        ];
    }
}
