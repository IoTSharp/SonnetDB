using System.Text;
using SonnetMQ;
using Xunit;

namespace SonnetDB.Core.Tests.Mq;

public sealed class SonnetMqStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnetmq-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Publish_ThenPull_ReturnsMessagesInOffsetOrder()
    {
        using var store = Open();

        long first = store.Publish("iot.telemetry", Encoding.UTF8.GetBytes("a"));
        long second = store.Publish("iot.telemetry", Encoding.UTF8.GetBytes("b"));

        var messages = store.Pull("iot.telemetry", "iotsharp", 10);

        Assert.Equal(0, first);
        Assert.Equal(1, second);
        Assert.Equal(["a", "b"], messages.Select(m => Encoding.UTF8.GetString(m.Payload)).ToArray());
    }

    [Fact]
    public void Ack_WithOffset_SkipsAcknowledgedMessages()
    {
        using var store = Open();
        store.Publish("iot.events", Encoding.UTF8.GetBytes("a"));
        store.Publish("iot.events", Encoding.UTF8.GetBytes("b"));

        long next = store.Ack("iot.events", "rules", 0);
        var messages = store.Pull("iot.events", "rules", 10);

        Assert.Equal(1, next);
        Assert.Single(messages);
        Assert.Equal(1, messages[0].Offset);
    }

    [Fact]
    public void PublishMany_ReturnsContiguousOffsetsAndPreservesOrder()
    {
        using var store = Open();

        var offsets = store.PublishMany(
            "iot.telemetry",
            [
                new SonnetMqPublishEntry(Encoding.UTF8.GetBytes("a")),
                new SonnetMqPublishEntry(Encoding.UTF8.GetBytes("b")),
                new SonnetMqPublishEntry(Encoding.UTF8.GetBytes("c"))
            ]);

        var messages = store.Pull("iot.telemetry", "iotsharp", 10);

        Assert.Equal([0L, 1L, 2L], offsets);
        Assert.Equal(["a", "b", "c"], messages.Select(m => Encoding.UTF8.GetString(m.Payload)).ToArray());
    }

    [Fact]
    public void Pull_FromOffset_ReturnsMessagesAtOrAfterOffset()
    {
        using var store = Open(offsetIndexStride: 2);
        store.PublishMany(
            "iot.telemetry",
            Enumerable.Range(0, 8)
                .Select(i => new SonnetMqPublishEntry(Encoding.UTF8.GetBytes(i.ToString())))
                .ToArray());

        var messages = store.Pull("iot.telemetry", 5, 10);

        Assert.Equal([5L, 6L, 7L], messages.Select(m => m.Offset).ToArray());
        Assert.Equal(["5", "6", "7"], messages.Select(m => Encoding.UTF8.GetString(m.Payload)).ToArray());
    }

    [Fact]
    public void Pull_AfterAck_UsesSparseOffsetIndexForLargeOffsets()
    {
        using var store = Open(offsetIndexStride: 4);
        store.PublishMany(
            "iot.telemetry",
            Enumerable.Range(0, 16)
                .Select(i => new SonnetMqPublishEntry(Encoding.UTF8.GetBytes(i.ToString())))
                .ToArray());

        store.Ack("iot.telemetry", "iotsharp", 10);
        var messages = store.Pull("iot.telemetry", "iotsharp", 3);

        Assert.Equal([11L, 12L, 13L], messages.Select(m => m.Offset).ToArray());
    }

    [Fact]
    public void Open_WithExistingLog_ReplaysMessagesAndAcks()
    {
        using (var store = Open())
        {
            store.Publish("iot.commands", Encoding.UTF8.GetBytes("a"));
            store.Publish("iot.commands", Encoding.UTF8.GetBytes("b"));
            store.Ack("iot.commands", "device-agent", 0);
        }

        using var reopened = Open();
        var messages = reopened.Pull("iot.commands", "device-agent", 10);
        var stats = reopened.GetStats("iot.commands");

        Assert.Single(messages);
        Assert.Equal("b", Encoding.UTF8.GetString(messages[0].Payload));
        Assert.Equal(1, stats.MessageCount);
        Assert.Equal(2, stats.NextOffset);
        Assert.Equal(1, stats.ConsumerOffsets["device-agent"]);
    }

    [Fact]
    public void Open_WithSingleFileMode_PersistsInOneFile()
    {
        string file = Path.Combine(_root, "queue.smq");
        using (var store = SonnetMqStore.Open(new SonnetMqOptions { Path = file, OpenMode = SonnetMqOpenMode.SingleFile }))
        {
            store.Publish("iot.audit", Encoding.UTF8.GetBytes("created"));
        }

        Assert.True(File.Exists(file));
        using var reopened = SonnetMqStore.Open(new SonnetMqOptions { Path = file, OpenMode = SonnetMqOpenMode.SingleFile });
        Assert.Single(reopened.Pull("iot.audit", "audit-sink", 10));
    }

    [Fact]
    public void Options_DefaultsFlushOnPublish()
    {
        var options = new SonnetMqOptions { Path = _root };

        Assert.True(options.FlushOnPublish);
    }

    [Fact]
    public void Publish_WithSmallSegmentMaxBytes_RollsSegmentsAndReplays()
    {
        var options = new SonnetMqOptions
        {
            Path = _root,
            SegmentMaxBytes = 128,
            RetentionInterval = TimeSpan.Zero,
        };

        using (var store = SonnetMqStore.Open(options))
        {
            store.PublishMany(
                "iot.telemetry",
                Enumerable.Range(0, 12)
                    .Select(i => new SonnetMqPublishEntry(Encoding.UTF8.GetBytes("payload-" + i)))
                    .ToArray());
        }

        string[] segments = Directory.GetFiles(_root, "*.smqseg", SearchOption.AllDirectories);
        Assert.True(segments.Length > 1);

        using var reopened = SonnetMqStore.Open(options);
        var messages = reopened.Pull("iot.telemetry", 0, 20);

        Assert.Equal(12, messages.Count);
        Assert.Equal(Enumerable.Range(0, 12).Select(i => (long)i).ToArray(), messages.Select(m => m.Offset).ToArray());
    }

    [Fact]
    public void TombstoneBefore_TrimsMessagesAndSurvivesRestart()
    {
        var options = new SonnetMqOptions
        {
            Path = _root,
            SegmentMaxBytes = 256,
            RetentionInterval = TimeSpan.Zero,
        };

        using (var store = SonnetMqStore.Open(options))
        {
            store.PublishMany(
                "iot.telemetry",
                Enumerable.Range(0, 8)
                    .Select(i => new SonnetMqPublishEntry(Encoding.UTF8.GetBytes(i.ToString())))
                    .ToArray());

            long firstKept = store.TombstoneBefore("iot.telemetry", 5);
            var messages = store.Pull("iot.telemetry", 0, 20);

            Assert.Equal(5, firstKept);
            Assert.Equal([5L, 6L, 7L], messages.Select(m => m.Offset).ToArray());
        }

        using var reopened = SonnetMqStore.Open(options);
        var replayed = reopened.Pull("iot.telemetry", 0, 20);

        Assert.Equal([5L, 6L, 7L], replayed.Select(m => m.Offset).ToArray());
    }

    [Fact]
    public void AckRetention_TrimsMessagesAcknowledgedByAllConsumers()
    {
        var options = new SonnetMqOptions
        {
            Path = _root,
            AckRetentionMinOffsetDelta = 1,
            RetentionInterval = TimeSpan.Zero,
        };

        using var store = SonnetMqStore.Open(options);
        store.PublishMany(
            "iot.telemetry",
            Enumerable.Range(0, 3)
                .Select(i => new SonnetMqPublishEntry(Encoding.UTF8.GetBytes(i.ToString())))
                .ToArray());

        store.Ack("iot.telemetry", "iotsharp", 1);
        var remaining = store.Pull("iot.telemetry", 0, 10);
        var stats = store.GetStats("iot.telemetry");

        Assert.Equal([2L], remaining.Select(m => m.Offset).ToArray());
        Assert.Equal(1, stats.MessageCount);
        Assert.Equal(3, stats.NextOffset);
    }

    [Fact]
    public void AckRetention_DoesNotTrimPastSlowestConsumer()
    {
        var options = new SonnetMqOptions
        {
            Path = _root,
            AckRetentionMinOffsetDelta = 1,
            RetentionInterval = TimeSpan.Zero,
        };

        using var store = SonnetMqStore.Open(options);
        store.PublishMany(
            "iot.telemetry",
            Enumerable.Range(0, 3)
                .Select(i => new SonnetMqPublishEntry(Encoding.UTF8.GetBytes(i.ToString())))
                .ToArray());

        store.Ack("iot.telemetry", "slow", 0);
        store.Ack("iot.telemetry", "fast", 2);
        var remaining = store.Pull("iot.telemetry", 0, 10);

        Assert.Equal([1L, 2L], remaining.Select(m => m.Offset).ToArray());
    }

    [Fact]
    public void TrimRetention_WithMaxBytes_RemovesOldSegments()
    {
        var options = new SonnetMqOptions
        {
            Path = _root,
            SegmentMaxBytes = 160,
            RetentionMaxBytes = 260,
            RetentionInterval = TimeSpan.Zero,
        };

        using var store = SonnetMqStore.Open(options);
        store.PublishMany(
            "iot.telemetry",
            Enumerable.Range(0, 20)
                .Select(i => new SonnetMqPublishEntry(Encoding.UTF8.GetBytes("payload-" + i)))
                .ToArray());

        int before = Directory.GetFiles(_root, "*.smqseg", SearchOption.AllDirectories).Length;
        store.TrimRetention();
        int after = Directory.GetFiles(_root, "*.smqseg", SearchOption.AllDirectories).Length;
        var remaining = store.Pull("iot.telemetry", 0, 50);

        Assert.True(before > after);
        Assert.NotEmpty(remaining);
        Assert.True(remaining[0].Offset > 0);
    }

    [Fact]
    public void ListTopicStats_ReturnsAllTopicsInNameOrder()
    {
        using var store = Open();
        Assert.Empty(store.ListTopicStats());

        store.Publish("iot.telemetry", Encoding.UTF8.GetBytes("a"));
        store.Publish("iot.telemetry", Encoding.UTF8.GetBytes("b"));
        store.Publish("iot.commands", Encoding.UTF8.GetBytes("c"));
        store.Ack("iot.commands", "rules", 0);

        var stats = store.ListTopicStats();

        Assert.Equal(["iot.commands", "iot.telemetry"], stats.Select(s => s.Topic).ToArray());
        var telemetry = stats.Single(s => s.Topic == "iot.telemetry");
        Assert.Equal(2, telemetry.MessageCount);
        Assert.Equal(2, telemetry.NextOffset);
        var commands = stats.Single(s => s.Topic == "iot.commands");
        Assert.Equal(1, commands.ConsumerOffsets["rules"]);
    }

    [Fact]
    public void ConcurrentPublish_SameTopic_ProducesContiguousUniqueOffsets()
    {
        using var store = Open();
        const int writers = 8;
        const int perWriter = 500;

        var offsets = new System.Collections.Concurrent.ConcurrentBag<long>();
        Parallel.For(0, writers, _ =>
        {
            for (int i = 0; i < perWriter; i++)
                offsets.Add(store.Publish("iot.telemetry", Encoding.UTF8.GetBytes("x")));
        });

        var ordered = offsets.OrderBy(o => o).ToArray();
        Assert.Equal(writers * perWriter, ordered.Length);
        Assert.Equal(Enumerable.Range(0, writers * perWriter).Select(i => (long)i).ToArray(), ordered);
        Assert.Equal(writers * perWriter, store.GetStats("iot.telemetry").NextOffset);
    }

    [Fact]
    public void ConcurrentPublish_DistinctTopics_KeepsPerTopicOffsetsIndependent()
    {
        using var store = Open();
        const int topics = 6;
        const int perTopic = 400;

        Parallel.For(0, topics, t =>
        {
            string topic = "iot.topic-" + t;
            for (int i = 0; i < perTopic; i++)
                store.Publish(topic, Encoding.UTF8.GetBytes(i.ToString()));
        });

        var stats = store.ListTopicStats();
        Assert.Equal(topics, stats.Count);
        foreach (var s in stats)
        {
            Assert.Equal(perTopic, s.MessageCount);
            Assert.Equal(perTopic, s.NextOffset);
            var messages = store.Pull(s.Topic, 0, perTopic + 10);
            Assert.Equal(Enumerable.Range(0, perTopic).Select(i => (long)i).ToArray(), messages.Select(m => m.Offset).ToArray());
        }
    }

    [Fact]
    public void Publish_WithHeaders_RoundTripsThroughEncodeAndReplay()
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["z-last"] = "值 with 空格 & =符号",
            ["a-first"] = "plain",
            ["b-empty"] = "",
        };

        using (var store = Open())
        {
            store.Publish("iot.telemetry", Encoding.UTF8.GetBytes("body"), new SonnetMqPublishOptions(headers));

            var live = store.Pull("iot.telemetry", 0, 1);
            Assert.Equal(headers, live[0].Headers);
        }

        using var reopened = Open();
        var replayed = reopened.Pull("iot.telemetry", 0, 1);
        Assert.Equal("body", Encoding.UTF8.GetString(replayed[0].Payload));
        Assert.Equal(headers, replayed[0].Headers);
    }

    [Fact]
    public void ConcurrentDurablePublish_WithGroupCommit_PersistsEveryMessageAcrossRestart()
    {
        var options = new SonnetMqOptions
        {
            Path = _root,
            SyncOnPublish = true,
            GroupCommitPublish = true,
            RetentionInterval = TimeSpan.Zero,
        };

        const int writers = 8;
        const int perWriter = 250;
        using (var store = SonnetMqStore.Open(options))
        {
            Parallel.For(0, writers, _ =>
            {
                for (int i = 0; i < perWriter; i++)
                    store.Publish("iot.telemetry", Encoding.UTF8.GetBytes("x"));
            });

            Assert.Equal(writers * perWriter, store.GetStats("iot.telemetry").NextOffset);
        }

        // 组提交 leader-flush 后，所有已返回的 publish 必须在重开后可见（fsync 覆盖不丢消息）。
        using var reopened = SonnetMqStore.Open(options);
        var replayed = reopened.Pull("iot.telemetry", 0, writers * perWriter + 10);
        Assert.Equal(writers * perWriter, replayed.Count);
        Assert.Equal(
            Enumerable.Range(0, writers * perWriter).Select(i => (long)i).ToArray(),
            replayed.Select(m => m.Offset).ToArray());
    }

    [Fact]
    public void Publish_WithGroupCommitDisabled_StillPersistsAndReplays()
    {
        var options = new SonnetMqOptions
        {
            Path = _root,
            SyncOnPublish = true,
            GroupCommitPublish = false,
            RetentionInterval = TimeSpan.Zero,
        };

        using (var store = SonnetMqStore.Open(options))
        {
            store.Publish("iot.telemetry", Encoding.UTF8.GetBytes("a"));
            store.Publish("iot.telemetry", Encoding.UTF8.GetBytes("b"));
        }

        using var reopened = SonnetMqStore.Open(options);
        var replayed = reopened.Pull("iot.telemetry", 0, 10);
        Assert.Equal(["a", "b"], replayed.Select(m => Encoding.UTF8.GetString(m.Payload)).ToArray());
    }

    [Fact]
    public void ConcurrentDurablePublish_WithSegmentRolling_PersistsEveryMessageAcrossRestart()
    {
        var options = new SonnetMqOptions
        {
            Path = _root,
            SyncOnPublish = true,
            GroupCommitPublish = true,
            SegmentMaxBytes = 256,
            RetentionInterval = TimeSpan.Zero,
        };

        const int writers = 6;
        const int perWriter = 200;
        using (var store = SonnetMqStore.Open(options))
        {
            Parallel.For(0, writers, _ =>
            {
                for (int i = 0; i < perWriter; i++)
                    store.Publish("iot.telemetry", Encoding.UTF8.GetBytes("payload"));
            });
        }

        // 跨段滚动 + 组提交：旧段在滚动前 fsync，重开后不丢任何消息。
        using var reopened = SonnetMqStore.Open(options);
        Assert.Equal(writers * perWriter, reopened.Pull("iot.telemetry", 0, writers * perWriter + 10).Count);
    }

    [Fact]
    public void ColdPull_AfterEviction_ReturnsCorrectPayloadsFromSegments()
    {
        // 小 HotTailMaxBytes 强制驱逐后，冷 offset 经位置索引 + 有界句柄 LRU 从段文件读回，
        // payload / offset / headers 与热尾时期完全一致。
        var options = new SonnetMqOptions
        {
            Path = _root,
            HotTailMaxBytes = 128, // 极小热尾 → 大量驱逐
            OffsetIndexStride = 4,
            RetentionInterval = TimeSpan.Zero,
        };

        using var store = SonnetMqStore.Open(options);
        var payloads = Enumerable.Range(0, 40)
            .Select(i => Encoding.UTF8.GetBytes("cold-payload-" + i))
            .ToArray();

        store.PublishMany(
            "iot.telemetry",
            payloads.Select(p => new SonnetMqPublishEntry(p)).ToArray());

        // 从最早 offset 读回：全部命中冷 offset（含 offset 0）。
        var cold = store.Pull("iot.telemetry", 0, 40);
        Assert.Equal(40, cold.Count);
        for (int i = 0; i < 40; i++)
        {
            Assert.Equal(i, cold[i].Offset);
            Assert.Equal(payloads[i], cold[i].Payload);
        }
    }

    [Fact]
    public void ColdPull_CrossesColdHotBoundary_InSingleCall()
    {
        // 小 HotTailMaxBytes + 小 SegmentMaxBytes → 冷段 + 热尾同存；从被驱逐的 offset 连续拉 maxCount 条
        // 跨越冷/热边界，内容与 offset 连续、无重复无遗漏。
        var options = new SonnetMqOptions
        {
            Path = _root,
            HotTailMaxBytes = 256,
            SegmentMaxBytes = 512,
            OffsetIndexStride = 4,
            RetentionInterval = TimeSpan.Zero,
        };

        using var store = SonnetMqStore.Open(options);
        var payloads = Enumerable.Range(0, 60)
            .Select(i => Encoding.UTF8.GetBytes("mix-" + i.ToString("D3")))
            .ToArray();

        store.PublishMany(
            "iot.telemetry",
            payloads.Select(p => new SonnetMqPublishEntry(p)).ToArray());

        var mixed = store.Pull("iot.telemetry", 0, 60);
        Assert.Equal(60, mixed.Count);
        Assert.Equal(
            Enumerable.Range(0, 60).Select(i => (long)i).ToArray(),
            mixed.Select(m => m.Offset).ToArray());
        for (int i = 0; i < 60; i++)
            Assert.Equal(payloads[i], mixed[i].Payload);
    }

    [Fact]
    public void ColdPull_SpansManySegments_RespectsSegmentCacheBound()
    {
        // 段数 ≫ SegmentCacheSize 时冷读跨全部段仍然正确返回，句柄 LRU 按容量关闭最久未用。
        var options = new SonnetMqOptions
        {
            Path = _root,
            HotTailMaxBytes = 64, // 极小热尾
            SegmentMaxBytes = 200, // 每段仅容几条
            SegmentCacheSize = 2, // 极小 LRU 容量
            OffsetIndexStride = 2,
            RetentionInterval = TimeSpan.Zero,
        };

        using var store = SonnetMqStore.Open(options);
        var payloads = Enumerable.Range(0, 32)
            .Select(i => Encoding.UTF8.GetBytes("seg-" + i))
            .ToArray();

        store.PublishMany(
            "iot.telemetry",
            payloads.Select(p => new SonnetMqPublishEntry(p)).ToArray());

        string[] segments = Directory.GetFiles(_root, "*.smqseg", SearchOption.AllDirectories);
        Assert.True(segments.Length > options.SegmentCacheSize, "测试前提：段数应 > SegmentCacheSize 才能真正压 LRU");

        var all = store.Pull("iot.telemetry", 0, 40);
        Assert.Equal(32, all.Count);
        for (int i = 0; i < 32; i++)
        {
            Assert.Equal(i, all[i].Offset);
            Assert.Equal(payloads[i], all[i].Payload);
        }
    }

    [Fact]
    public void ColdPull_AfterRestart_ReplaysBoundedAndReadsColdFromDisk()
    {
        // 驱逐后重启，replay 依然填充位置索引且 payload 不丢：pull 冷 offset 仍然返回原 payload。
        var options = new SonnetMqOptions
        {
            Path = _root,
            HotTailMaxBytes = 128,
            SegmentMaxBytes = 256,
            OffsetIndexStride = 4,
            RetentionInterval = TimeSpan.Zero,
        };

        var payloads = Enumerable.Range(0, 24)
            .Select(i => Encoding.UTF8.GetBytes("restart-" + i))
            .ToArray();

        using (var store = SonnetMqStore.Open(options))
        {
            store.PublishMany(
                "iot.telemetry",
                payloads.Select(p => new SonnetMqPublishEntry(p)).ToArray());
            Assert.Equal(24, store.GetStats("iot.telemetry").NextOffset);
        }

        using var reopened = SonnetMqStore.Open(options);
        var replayed = reopened.Pull("iot.telemetry", 0, 30);
        Assert.Equal(24, replayed.Count);
        for (int i = 0; i < 24; i++)
        {
            Assert.Equal(i, replayed[i].Offset);
            Assert.Equal(payloads[i], replayed[i].Payload);
        }
    }

    [Fact]
    public void TrimRetention_ByAge_DiscardsFullyExpiredSegmentsAndKeepsActive()
    {
        // 按段粒度：Sleep 使旧段整段超龄 → cutoff 推进至该段之后 baseOffset；活跃段永不裁剪。
        var options = new SonnetMqOptions
        {
            Path = _root,
            SegmentMaxBytes = 200,
            RetentionMaxAge = TimeSpan.FromMilliseconds(150),
            RetentionInterval = TimeSpan.Zero,
        };

        using var store = SonnetMqStore.Open(options);
        store.PublishMany(
            "iot.telemetry",
            Enumerable.Range(0, 12)
                .Select(i => new SonnetMqPublishEntry(Encoding.UTF8.GetBytes("old-" + i)))
                .ToArray());

        string[] segmentsBefore = Directory.GetFiles(_root, "*.smqseg", SearchOption.AllDirectories);
        Assert.True(segmentsBefore.Length >= 2, "测试前提：应产生多个段");

        Thread.Sleep(400);
        // 追加一批新记录 → 落在新活跃段（活跃段的最新记录必然新鲜、不裁）。
        store.PublishMany(
            "iot.telemetry",
            Enumerable.Range(0, 3)
                .Select(i => new SonnetMqPublishEntry(Encoding.UTF8.GetBytes("fresh-" + i)))
                .ToArray());

        store.TrimRetention();

        var stats = store.GetStats("iot.telemetry");
        Assert.True(stats.NextOffset - stats.MessageCount > 0, "按段粒度裁剪后应有 TrimmedBeforeOffset 推进");

        // 活跃段仍在，剩余消息可读、offset 单调、payload 正确。
        string[] segmentsAfter = Directory.GetFiles(_root, "*.smqseg", SearchOption.AllDirectories);
        Assert.True(segmentsAfter.Length >= 1);
        var remaining = store.Pull("iot.telemetry", 0, 50);
        Assert.NotEmpty(remaining);
        for (int i = 1; i < remaining.Count; i++)
            Assert.Equal(remaining[i - 1].Offset + 1, remaining[i].Offset);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    // ────────────────────────────── WaitForMessagesAsync (#236) ──────────────────────────────

    [Fact]
    public async Task WaitForMessages_DataAlreadyPresent_ReturnsImmediately()
    {
        using var store = Open();
        store.Publish("iot.telemetry", Encoding.UTF8.GetBytes("a"));

        long start = await store.WaitForMessagesAsync("iot.telemetry", 0, CancellationToken.None).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(0, start);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WaitForMessages_WakesOnPublish(bool groupCommit)
    {
        var options = new SonnetMqOptions
        {
            Path = _root,
            GroupCommitPublish = groupCommit,
        };
        using var store = SonnetMqStore.Open(options);

        // 订阅先于任何消息：应挂起。
        ValueTask<long> waitTask = store.WaitForMessagesAsync("iot.telemetry", 0, CancellationToken.None);
        Assert.False(waitTask.IsCompleted);

        store.Publish("iot.telemetry", Encoding.UTF8.GetBytes("hello"));

        long start = await waitTask.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, start);
        Assert.Single(store.Pull("iot.telemetry", start, 10));
    }

    [Fact]
    public async Task WaitForMessages_AfterTrimGap_ReturnsAdvancedOffset()
    {
        var options = new SonnetMqOptions { Path = _root, OffsetIndexStride = 1 };
        using var store = SonnetMqStore.Open(options);
        store.PublishMany(
            "iot.telemetry",
            Enumerable.Range(0, 6)
                .Select(i => new SonnetMqPublishEntry(Encoding.UTF8.GetBytes(i.ToString())))
                .ToArray());

        // 裁掉 offset < 4，请求方仍从 0 起 → 有效起点应前移到 4，不空转。
        store.TombstoneBefore("iot.telemetry", 4);

        long start = await store.WaitForMessagesAsync("iot.telemetry", 0, CancellationToken.None).AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(4, start);
    }

    [Fact]
    public async Task WaitForMessages_Cancellation_Throws()
    {
        using var store = Open();
        using var cts = new CancellationTokenSource();

        ValueTask<long> waitTask = store.WaitForMessagesAsync("iot.telemetry", 0, cts.Token);
        Assert.False(waitTask.IsCompleted);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waitTask);
    }

    [Fact]
    public async Task WaitForMessages_StoreDisposed_FaultsWaiter()
    {
        var store = Open();
        ValueTask<long> waitTask = store.WaitForMessagesAsync("iot.telemetry", 0, CancellationToken.None);
        Assert.False(waitTask.IsCompleted);

        store.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await waitTask);
    }

    private SonnetMqStore Open(int offsetIndexStride = 1024)
        => SonnetMqStore.Open(new SonnetMqOptions { Path = _root, OffsetIndexStride = offsetIndexStride });
}
