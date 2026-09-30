using SonnetDB.Cdc;
using SonnetDB.Documents;
using SonnetDB.Engine;

namespace SonnetDB.Core.Tests.Cdc;

/// <summary>
/// CDC 源端固定读视图、spool 和本地接收端的最小可验证旅程。
/// </summary>
public sealed class CdcSourcePipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-cdc-source-pipeline-" + Guid.NewGuid().ToString("N"));

    public CdcSourcePipelineTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public async Task FixedReadView_SnapshotThenConcurrentWrites_ReplaysAndReconcilesAfterReopen()
    {
        CdcSnapshotDescriptor descriptor = Descriptor(rowCount: 2, offset: 10);
        string viewPath = Path.Combine(_root, "view.bin");
        string spoolPath = Path.Combine(_root, "events.log");
        string replicaPath = Path.Combine(_root, "replica.bin");
        IReadOnlyList<CdcSnapshotRow> fixedRows =
        [
            Row("a", 1),
            Row("b", 1),
        ];

        await using (var captured = await CdcSourceReadView.CaptureAsync(
            viewPath,
            descriptor,
            static (afterKey, maxRows, _) =>
            {
                IReadOnlyList<CdcSnapshotRow> rows =
                    afterKey is null
                        ? [Row("a", 1), Row("b", 1)]
                        : [];
                return ValueTask.FromResult(rows);
            }))
        {
        }

        // These writes occur after the view's checkpoint and must be retained independently.
        await using (var spool = new CdcEventSpool(spoolPath))
        {
            await spool.AppendAsync(Event(11, "a", CdcOperation.Update, "{\"value\":2}"));
            await spool.AppendAsync(Event(12, "c", CdcOperation.Insert, "{\"value\":1}"));
        }

        await using (var view = CdcSourceReadView.Open(viewPath, descriptor))
        await using (var replica = await CdcSnapshotReplica.CreateAsync(replicaPath, descriptor))
        {
            CdcSnapshotRowBatch page = await view.ReadPageAsync(maxRows: 2);
            Assert.Equal(fixedRows, page.Rows);
            await replica.WriteSnapshotPageAsync(0, page.Rows);
            await replica.CompleteSnapshotAsync();

            await using var spool = new CdcEventSpool(spoolPath);
            CdcEventSpoolBatch pending = await spool.ReplayBatchAsync(replica.GetState().AppliedCheckpoint);
            Assert.Equal(["a", "c"], pending.Events.Select(static value => value.Key));
            CdcSnapshotReplicaState state = await replica.ApplyIncrementalAsync(pending.Events);
            await spool.AcknowledgeAsync(state.AppliedCheckpoint);
            Assert.Equal(new CdcCheckpoint(7, 12), state.AppliedCheckpoint);
        }

        await using var reopenedReplica = CdcSnapshotReplica.Open(replicaPath, descriptor);
        await using var reopenedSpool = new CdcEventSpool(spoolPath);
        Assert.Equal(new CdcCheckpoint(7, 12), reopenedReplica.GetState().AppliedCheckpoint);
        Assert.Equal(0, reopenedSpool.EventCount);
        Assert.Equal(
            [Row("a", 2), Row("b", 1), Row("c", 1)],
            (await reopenedReplica.ReadRowsAsync()).Rows);
    }

    [Fact]
    public async Task FixedReadView_PageReaderIsBoundedAndReopenPreservesIdentity()
    {
        CdcSnapshotDescriptor descriptor = Descriptor(rowCount: 3, offset: 21);
        string path = Path.Combine(_root, "paged-view.bin");
        int calls = 0;
        await using (var captured = await CdcSourceReadView.CaptureAsync(
            path,
            descriptor,
            (afterKey, maxRows, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                calls++;
                IReadOnlyList<CdcSnapshotRow> rows = afterKey switch
                {
                    null => [Row("a", 1), Row("b", 1)],
                    "b" => [Row("c", 1)],
                    _ => [],
                };
                Assert.True(rows.Count <= maxRows);
                return ValueTask.FromResult(rows);
            }))
        {
        }

        await using (var view = CdcSourceReadView.Open(path, descriptor))
        {
            CdcSnapshotRowBatch first = await view.ReadPageAsync(maxRows: 2);
            Assert.Equal(["a", "b"], first.Rows.Select(static row => row.Key));
            Assert.True(first.HasMore);
            CdcSnapshotRowBatch second = await view.ReadPageAsync(first.NextKey, maxRows: 2);
            Assert.Equal(["c"], second.Rows.Select(static row => row.Key));
            Assert.False(second.HasMore);
        }
        Assert.Equal(3, calls);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Task.Run(() => CdcSourceReadView.Open(path, descriptor with { SnapshotId = "other" })));
    }

    [Fact]
    public async Task Pipeline_CapacityFailureDoesNotAdvanceReplicaOrAcknowledgeSpool()
    {
        CdcSnapshotDescriptor descriptor = Descriptor(rowCount: 1, offset: 10);
        string viewPath = Path.Combine(_root, "capacity-view.bin");
        string replicaPath = Path.Combine(_root, "capacity-replica.bin");
        string spoolPath = Path.Combine(_root, "capacity-events.log");
        var options = new CdcSnapshotReplicaOptions { MaxRows = 1, MaxBatchRows = 1, MaxBytes = 4096, MaxBatchBytes = 4096 };

        await using (var captured = await CdcSourceReadView.CaptureAsync(
            viewPath,
            descriptor,
            static (afterKey, _, _) => ValueTask.FromResult<IReadOnlyList<CdcSnapshotRow>>(
                afterKey is null ? [Row("a", 1)] : []),
            options))
        {
        }
        await using (var replica = await CdcSnapshotReplica.CreateAsync(replicaPath, descriptor, options))
        {
            await replica.WriteSnapshotPageAsync(0, [Row("a", 1)]);
            await replica.CompleteSnapshotAsync();
        }

        await using (var spool = new CdcEventSpool(spoolPath))
        {
            await spool.AppendAsync(Event(11, "a", CdcOperation.Update, "{\"value\":2}"));
            await spool.AppendAsync(Event(12, "b", CdcOperation.Insert, "{\"value\":1}"));
            await using var reopened = CdcSnapshotReplica.Open(replicaPath, descriptor, options);
            IReadOnlyList<CdcEvent> pending = await spool.ReplayAsync(reopened.GetState().AppliedCheckpoint);
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                reopened.ApplyIncrementalAsync(pending).AsTask());
            Assert.Equal(new CdcCheckpoint(7, 10), reopened.GetState().AppliedCheckpoint);
            Assert.Equal(2, spool.EventCount);
            Assert.Empty(spool.AcknowledgedCheckpoints);
        }
    }

    [Fact]
    public async Task DocumentSourceCapture_ReopenResumesAfterSpoolHighWatermark()
    {
        string databasePath = Path.Combine(_root, "source-db");
        string spoolPath = Path.Combine(_root, "source-events.log");
        var captureOptions = new CdcDocumentSourceCaptureOptions
        {
            Source = "source-db-1",
            Entity = "docs",
            Schema = "docs",
            SchemaVersion = 1,
            Partition = 7,
            BatchSize = 16,
        };

        using (var database = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath }))
        {
            database.Documents.Create(DocumentCollectionSchema.Create("docs"));
            var store = database.Documents.Open("docs");
            store.Insert("a", "{\"value\":1}");
            store.Replace("a", "{\"value\":2}");

            await using var spool = new CdcEventSpool(spoolPath);
            await using var capture = new CdcDocumentSourceCapture(store, spool, captureOptions);
            CdcDocumentSourceCaptureResult first = await capture.CaptureAsync();
            Assert.Equal(0, first.StartSequence);
            Assert.Equal(2, first.EndSequence);
            Assert.Equal(2, first.CapturedEvents);

            store.Replace("a", "{\"value\":3}");
            CdcDocumentSourceCaptureResult second = await capture.CaptureAsync();
            Assert.Equal(2, second.StartSequence);
            Assert.Equal(3, second.EndSequence);
            Assert.Equal(1, second.CapturedEvents);
        }

        using (var database = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath }))
        {
            var store = database.Documents.Open("docs");
            store.Replace("a", "{\"value\":4}");
            await using var spool = new CdcEventSpool(spoolPath);
            await using var capture = new CdcDocumentSourceCapture(store, spool, captureOptions);
            CdcDocumentSourceCaptureResult resumed = await capture.CaptureAsync();
            Assert.Equal(3, resumed.StartSequence);
            Assert.Equal(4, resumed.EndSequence);
            Assert.Equal(1, resumed.CapturedEvents);

            IReadOnlyList<CdcEvent> events = await spool.ReplayAsync();
            Assert.Equal([1L, 2L, 3L, 4L], events.Select(static value => value.Sequence));
            Assert.All(events, value =>
            {
                Assert.Equal("source-db-1", value.Source);
                Assert.Equal("docs", value.Entity);
                Assert.Equal(7, value.Metadata.Checkpoint.Partition);
            });
            Assert.Equal("{\"value\":4}", events[^1].AfterJson);
        }
    }

    [Fact]
    public async Task DocumentCollectionFixedReadView_UsesStableKvSnapshotAndCheckpoint()
    {
        string databasePath = Path.Combine(_root, "fixed-source-db");
        string viewPath = Path.Combine(_root, "fixed-source-view.bin");
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath });
        database.Documents.Create(DocumentCollectionSchema.Create("docs"));
        var store = database.Documents.Open("docs");
        store.Insert("b", "{\"value\":1}");
        store.Insert("a", "{\"value\":1}");

        await using var view = await CdcSourceReadView.CaptureDocumentCollectionAsync(
            viewPath,
            store,
            source: "fixed-source",
            partition: 3,
            options: new CdcSnapshotReplicaOptions { MaxBatchRows = 2 });
        store.Insert("c", "{\"value\":1}");

        Assert.Equal(2, view.RowCount);
        Assert.Equal(new CdcCheckpoint(3, 2), view.Descriptor.Checkpoint);
        CdcSnapshotRowBatch page = await view.ReadPageAsync(maxRows: 2);
        Assert.Equal(["a", "b"], page.Rows.Select(static row => row.Key));
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task DocumentSourceCapture_PendingFramesBeyondBatchSize_ReopensAtLastFrame()
    {
        string databasePath = Path.Combine(_root, "paged-source-db");
        string spoolPath = Path.Combine(_root, "paged-source-events.log");
        CdcDocumentSourceCaptureOptions options = CaptureOptions(batchSize: 2);
        var spoolOptions = new CdcEventSpoolOptions { MaxReplayEvents = 2 };

        using (var database = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath }))
        {
            database.Documents.Create(DocumentCollectionSchema.Create("docs"));
            var store = database.Documents.Open("docs");
            store.Insert("a", "{\"value\":1}");
            for (int value = 2; value <= 5; value++)
                store.Replace("a", $"{{\"value\":{value}}}");

            await using var spool = new CdcEventSpool(spoolPath, spoolOptions);
            await using var capture = new CdcDocumentSourceCapture(store, spool, options);
            for (int batch = 0; batch < 3; batch++)
                await capture.CaptureAsync();
            Assert.Equal(5, spool.EventCount);
        }

        using (var database = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath }))
        {
            var store = database.Documents.Open("docs");
            store.Replace("a", "{\"value\":6}");
            await using var spool = new CdcEventSpool(spoolPath, spoolOptions);
            await using var capture = new CdcDocumentSourceCapture(store, spool, options);
            CdcDocumentSourceCaptureResult result = await capture.CaptureAsync();
            Assert.Equal(5, result.StartSequence);
            Assert.Equal(6, result.EndSequence);
            Assert.Equal(1, result.CapturedEvents);
            var sequences = new List<long>();
            CdcCheckpoint? cursor = null;
            do
            {
                CdcEventSpoolBatch batch = await spool.ReplayBatchAsync(cursor);
                sequences.AddRange(batch.Events.Select(static value => value.Sequence));
                if (batch.Events.Count == 0)
                    break;
                cursor = batch.Events[^1].Metadata.Checkpoint;
            } while (sequences.Count < spool.EventCount);
            Assert.Equal([1L, 2L, 3L, 4L, 5L, 6L], sequences);
        }
    }

    [Fact]
    public async Task DocumentSourceCapture_MismatchedSpoolEvent_RejectsWithoutAppending()
    {
        string databasePath = Path.Combine(_root, "mismatch-source-db");
        string spoolPath = Path.Combine(_root, "mismatch-source-events.log");
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath });
        database.Documents.Create(DocumentCollectionSchema.Create("docs"));
        var store = database.Documents.Open("docs");
        store.Insert("a", "{\"value\":1}");
        await using var spool = new CdcEventSpool(spoolPath);
        await spool.AppendAsync(new CdcEvent(
            "wrong-source", "other-source", "docs", "a", 1,
            DateTimeOffset.UtcNow,
            new CdcEventMetadata(1, "documents", 1, CdcOperation.Insert, new CdcCheckpoint(7, 1)),
            null, "{\"value\":1}"));
        await using var capture = new CdcDocumentSourceCapture(store, spool, CaptureOptions());

        await Assert.ThrowsAsync<InvalidDataException>(() => capture.CaptureAsync().AsTask());
        Assert.Equal(1, spool.EventCount);
        Assert.Empty(spool.AcknowledgedCheckpoints);
        Assert.Equal(1, store.LatestChangeSequence);
    }

    [Fact]
    public async Task DocumentSourceCapture_ForeignPartitionAfterFirstReplayPage_Rejects()
    {
        string databasePath = Path.Combine(_root, "foreign-source-db");
        string spoolPath = Path.Combine(_root, "foreign-source-events.log");
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath });
        database.Documents.Create(DocumentCollectionSchema.Create("docs"));
        var store = database.Documents.Open("docs");
        store.Insert("a", "{\"value\":1}");
        await using var spool = new CdcEventSpool(spoolPath,
            new CdcEventSpoolOptions { MaxReplayEvents = 1 });
        await using var capture = new CdcDocumentSourceCapture(store, spool, CaptureOptions());
        await capture.CaptureAsync();
        await spool.AppendAsync(new CdcEvent(
            "foreign-partition", "source-db-1", "docs", "a", 1,
            DateTimeOffset.UtcNow,
            new CdcEventMetadata(1, "documents", 1, CdcOperation.Insert, new CdcCheckpoint(9, 1)),
            null, "{\"value\":1}"));
        store.Replace("a", "{\"value\":2}");

        await Assert.ThrowsAsync<InvalidDataException>(() => capture.CaptureAsync().AsTask());
        Assert.Equal(2, spool.EventCount);
        Assert.Equal(2, store.LatestChangeSequence);
    }

    [Fact]
    public async Task DocumentSourceCapture_SingleEventCapacity_AcknowledgementAllowsNextCapture()
    {
        string databasePath = Path.Combine(_root, "single-capacity-db");
        string spoolPath = Path.Combine(_root, "single-capacity-events.log");
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath });
        database.Documents.Create(DocumentCollectionSchema.Create("docs"));
        var store = database.Documents.Open("docs");
        store.Insert("a", "{\"value\":1}");
        await using var spool = new CdcEventSpool(spoolPath,
            new CdcEventSpoolOptions { MaxEvents = 1, MaxReplayEvents = 1 });
        await using var capture = new CdcDocumentSourceCapture(store, spool, CaptureOptions(batchSize: 1));
        await capture.CaptureAsync();
        await spool.AcknowledgeAsync(new CdcCheckpoint(7, 1));
        Assert.Equal(0, spool.EventCount);
        store.Replace("a", "{\"value\":2}");

        CdcDocumentSourceCaptureResult result = await capture.CaptureAsync();
        Assert.Equal(1, result.StartSequence);
        Assert.Equal(2, result.EndSequence);
        Assert.Equal(1, result.CapturedEvents);
        Assert.Equal(2, Assert.Single(await spool.ReplayAsync()).Sequence);
    }

    [Fact]
    public async Task DocumentCollectionFixedReadView_ExceedsRowCapacity_DoesNotPublishState()
    {
        string databasePath = Path.Combine(_root, "bounded-source-db");
        string viewPath = Path.Combine(_root, "bounded-source-view.bin");
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath });
        database.Documents.Create(DocumentCollectionSchema.Create("docs"));
        var store = database.Documents.Open("docs");
        store.Insert("a", "{\"value\":1}");
        store.Insert("b", "{\"value\":2}");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CdcSourceReadView.CaptureDocumentCollectionAsync(
                viewPath, store, "source", options: new CdcSnapshotReplicaOptions
                {
                    MaxRows = 1,
                    MaxBatchRows = 1,
                }).AsTask());
        Assert.False(File.Exists(viewPath));
    }

    [Fact]
    public async Task DocumentCollectionFixedReadView_ExceedsFileCapacity_DoesNotPublishState()
    {
        string databasePath = Path.Combine(_root, "file-capacity-db");
        string viewPath = Path.Combine(_root, "file-capacity-view.bin");
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath });
        database.Documents.Create(DocumentCollectionSchema.Create("docs"));
        var store = database.Documents.Open("docs");
        store.Insert("a", $"{{\"value\":\"{new string('x', 900)}\"}}");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CdcSourceReadView.CaptureDocumentCollectionAsync(
                viewPath, store, "source", options: new CdcSnapshotReplicaOptions
                {
                    MaxBytes = 1024,
                    MaxBatchBytes = 1024,
                }).AsTask());
        Assert.False(File.Exists(viewPath));
    }

    [Fact]
    public async Task DocumentCollectionFixedReadView_CorruptionAndDescriptorMismatch_FailClosedOnOpen()
    {
        string databasePath = Path.Combine(_root, "recovery-source-db");
        string viewPath = Path.Combine(_root, "recovery-source-view.bin");
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath });
        database.Documents.Create(DocumentCollectionSchema.Create("docs"));
        var store = database.Documents.Open("docs");
        store.Insert("a", "{\"value\":1}");
        CdcSnapshotDescriptor descriptor;
        await using (var view = await CdcSourceReadView.CaptureDocumentCollectionAsync(
            viewPath, store, "source", partition: 7))
            descriptor = view.Descriptor;

        Assert.Throws<InvalidDataException>(() => CdcSourceReadView.Open(
            viewPath, descriptor with { Checkpoint = new CdcCheckpoint(7, descriptor.Checkpoint.Offset + 1) }));
        await using (var reopened = CdcSourceReadView.Open(viewPath, descriptor))
            Assert.Equal([Row("a", 1)], (await reopened.ReadPageAsync()).Rows);

        byte[] content = await File.ReadAllBytesAsync(viewPath);
        content[^1] ^= 0x01;
        await File.WriteAllBytesAsync(viewPath, content);
        Assert.Throws<InvalidDataException>(() => CdcSourceReadView.Open(viewPath, descriptor));
    }

    [Fact]
    public async Task DocumentCollectionFixedReadView_OpenExistingRecoversDescriptorAndRejectsIdentityOrCorruption()
    {
        string databasePath = Path.Combine(_root, "unknown-descriptor-db");
        string viewPath = Path.Combine(_root, "unknown-descriptor-view.bin");
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath });
        database.Documents.Create(DocumentCollectionSchema.Create("docs"));
        var store = database.Documents.Open("docs");
        store.Insert("a", "{\"value\":1}");
        await using (var created = await CdcSourceReadView.CaptureDocumentCollectionAsync(
            viewPath, store, "source", partition: 7, snapshotId: "persisted-view"))
        {
        }

        await using (var recovered = CdcSourceReadView.OpenExisting(
            viewPath, "source", "docs", "documents", 1, 7, "persisted-view"))
        {
            Assert.Equal(new CdcCheckpoint(7, 1), recovered.Descriptor.Checkpoint);
            Assert.Equal(1, recovered.Descriptor.RowCount);
            Assert.Equal("persisted-view", recovered.Descriptor.SnapshotId);
            Assert.Equal([Row("a", 1)], (await recovered.ReadPageAsync()).Rows);
        }

        Assert.Throws<InvalidDataException>(() => CdcSourceReadView.OpenExisting(
            viewPath, "wrong-source", "docs", "documents", 1, 7, "persisted-view"));
        Assert.Throws<InvalidDataException>(() => CdcSourceReadView.OpenExisting(
            viewPath, "source", "wrong-entity", "documents", 1, 7, "persisted-view"));
        Assert.Throws<InvalidDataException>(() => CdcSourceReadView.OpenExisting(
            viewPath, "source", "docs", "wrong-schema", 1, 7, "persisted-view"));
        Assert.Throws<InvalidDataException>(() => CdcSourceReadView.OpenExisting(
            viewPath, "source", "docs", "documents", 2, 7, "persisted-view"));
        Assert.Throws<InvalidDataException>(() => CdcSourceReadView.OpenExisting(
            viewPath, "source", "docs", "documents", 1, 8, "persisted-view"));
        Assert.Throws<InvalidDataException>(() => CdcSourceReadView.OpenExisting(
            viewPath, "source", "docs", "documents", 1, 7, "wrong-view"));

        byte[] content = await File.ReadAllBytesAsync(viewPath);
        content[^1] ^= 0x01;
        await File.WriteAllBytesAsync(viewPath, content);
        Assert.Throws<InvalidDataException>(() => CdcSourceReadView.OpenExisting(
            viewPath, "source", "docs", "documents", 1, 7, "persisted-view"));
    }

    [Fact]
    public async Task DocumentPipeline_WritesAfterFixedCheckpoint_BoundedSpoolReconcilesAfterReopen()
    {
        string databasePath = Path.Combine(_root, "handoff-source-db");
        string viewPath = Path.Combine(_root, "handoff-view.bin");
        string replicaPath = Path.Combine(_root, "handoff-replica.bin");
        string spoolPath = Path.Combine(_root, "handoff-events.log");
        CdcDocumentSourceCaptureOptions captureOptions = CaptureOptions(batchSize: 2);
        var spoolOptions = new CdcEventSpoolOptions { MaxEvents = 2, MaxReplayEvents = 1 };
        CdcSnapshotDescriptor descriptor;

        using (var database = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath }))
        {
            database.Documents.Create(DocumentCollectionSchema.Create("docs"));
            var store = database.Documents.Open("docs");
            store.Insert("a", "{\"value\":1}");
            store.Insert("b", "{\"value\":1}");
            await using var spool = new CdcEventSpool(spoolPath, spoolOptions);
            await using var capture = new CdcDocumentSourceCapture(store, spool, captureOptions);
            await capture.CaptureAsync();
            await using (var view = await CdcSourceReadView.CaptureDocumentCollectionAsync(
                viewPath, store, captureOptions.Source, partition: captureOptions.Partition,
                options: new CdcSnapshotReplicaOptions { MaxBatchRows = 1 }))
            {
                descriptor = view.Descriptor;
                await spool.AcknowledgeAsync(descriptor.Checkpoint);
                await using var replica = await CdcSnapshotReplica.CreateAsync(replicaPath, descriptor,
                    new CdcSnapshotReplicaOptions { MaxBatchRows = 1 });
                CdcSnapshotRowBatch first = await view.ReadPageAsync(maxRows: 1);
                await replica.WriteSnapshotPageAsync(0, first.Rows);

                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task writeTask = Task.Run(async () =>
                {
                    await start.Task;
                    store.Replace("a", "{\"value\":2}");
                    store.Delete("b");
                });
                Task<CdcSnapshotRowBatch> readTask = Task.Run(async () =>
                {
                    await start.Task;
                    return await view.ReadPageAsync("a", maxRows: 1);
                });
                start.SetResult();
                await Task.WhenAll(writeTask, readTask);
                Assert.Equal([Row("b", 1)], (await readTask).Rows);
                await capture.CaptureAsync();
                Assert.Equal(2, spool.EventCount);
                Assert.Equal(descriptor.Checkpoint, first.AppliedCheckpoint);
                Assert.Equal(["a"], first.Rows.Select(static row => row.Key));
            }
        }

        using (var database = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath }))
        {
            var store = database.Documents.Open("docs");
            await using var view = CdcSourceReadView.Open(viewPath, descriptor,
                new CdcSnapshotReplicaOptions { MaxBatchRows = 1 });
            await using var replica = CdcSnapshotReplica.Open(replicaPath, descriptor,
                new CdcSnapshotReplicaOptions { MaxBatchRows = 1 });
            CdcSnapshotRowBatch second = await view.ReadPageAsync("a", maxRows: 1);
            Assert.Equal(["b"], second.Rows.Select(static row => row.Key));
            await replica.WriteSnapshotPageAsync(1, second.Rows);
            await replica.CompleteSnapshotAsync();
            await using var spool = new CdcEventSpool(spoolPath, spoolOptions);
            await using var capture = new CdcDocumentSourceCapture(store, spool, captureOptions);
            CdcCheckpoint cursor = descriptor.Checkpoint;
            for (int index = 0; index < 2; index++)
            {
                CdcEventSpoolBatch batch = await spool.ReplayBatchAsync(cursor, maxEvents: 1);
                CdcSnapshotReplicaState state = await replica.ApplyIncrementalAsync(batch.Events);
                await spool.AcknowledgeAsync(state.AppliedCheckpoint);
                cursor = state.AppliedCheckpoint;
            }
            Assert.Equal(0, spool.EventCount);

            store.Insert("c", "{\"value\":3}");
            CdcDocumentSourceCaptureResult resumed = await capture.CaptureAsync();
            Assert.Equal(4, resumed.StartSequence);
            Assert.Equal(5, resumed.EndSequence);
            CdcEventSpoolBatch tail = await spool.ReplayBatchAsync(cursor);
            CdcSnapshotReplicaState final = await replica.ApplyIncrementalAsync(tail.Events);
            await spool.AcknowledgeAsync(final.AppliedCheckpoint);
            CdcSnapshotRowBatch resultFirst = await replica.ReadRowsAsync(maxRows: 1);
            CdcSnapshotRowBatch resultSecond = await replica.ReadRowsAsync(resultFirst.NextKey, maxRows: 1);
            Assert.Equal([Row("a", 2), Row("c", 3)], resultFirst.Rows.Concat(resultSecond.Rows));
            Assert.Equal(new CdcCheckpoint(7, 5), final.AppliedCheckpoint);
        }
    }

    [Fact]
    public async Task DocumentPipeline_PumpAfterReplicaCommitBeforeAck_ReconcilesAndContinues()
    {
        string databasePath = Path.Combine(_root, "pump-source-db");
        string viewPath = Path.Combine(_root, "pump-view.bin");
        string replicaPath = Path.Combine(_root, "pump-replica.bin");
        string spoolPath = Path.Combine(_root, "pump-events.log");
        var replicaOptions = new CdcSnapshotReplicaOptions { MaxBatchRows = 1 };
        var spoolOptions = new CdcEventSpoolOptions { MaxEvents = 2, MaxReplayEvents = 1 };
        CdcDocumentSourceCaptureOptions captureOptions = CaptureOptions(batchSize: 2);
        CdcSnapshotDescriptor descriptor;

        using (var database = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath }))
        {
            database.Documents.Create(DocumentCollectionSchema.Create("docs"));
            var store = database.Documents.Open("docs");
            store.Insert("a", "{\"value\":1}");
            store.Insert("b", "{\"value\":1}");
            await using var spool = new CdcEventSpool(spoolPath, spoolOptions);
            await using var capture = new CdcDocumentSourceCapture(store, spool, captureOptions);
            await capture.CaptureAsync();
            await using var view = await CdcSourceReadView.CaptureDocumentCollectionAsync(
                viewPath, store, captureOptions.Source, partition: captureOptions.Partition,
                options: replicaOptions);
            descriptor = view.Descriptor;
            await spool.AcknowledgeAsync(descriptor.Checkpoint);
            await using var replica = await CdcSnapshotReplica.CreateAsync(
                replicaPath, descriptor, replicaOptions);

            CdcSnapshotReplicaState first = await CdcLocalReplicaPump.AdvanceAsync(
                view, spool, replica, maxRows: 1, maxBytes: 4096);
            Assert.Equal(1, first.SnapshotRowsCopied);
            store.Replace("a", "{\"value\":2}");
            store.Insert("c", "{\"value\":3}");
            await capture.CaptureAsync();
            for (int attempt = 0; attempt < 3 && replica.GetState().Phase == CdcSnapshotPhase.Snapshot; attempt++)
                await CdcLocalReplicaPump.AdvanceAsync(view, spool, replica, maxRows: 1, maxBytes: 4096);
            Assert.Equal(CdcSnapshotPhase.Incremental, replica.GetState().Phase);
            Assert.Equal(2, replica.GetState().SnapshotRowsCopied);

            CdcEventSpoolBatch firstIncrement = await spool.ReplayBatchAsync(descriptor.Checkpoint);
            Assert.Equal(3, Assert.Single(firstIncrement.Events).Sequence);
            CdcSnapshotReplicaState materialized = await replica.ApplyIncrementalAsync(firstIncrement.Events);
            Assert.Equal(new CdcCheckpoint(7, 3), materialized.AppliedCheckpoint);
            Assert.Equal(2, spool.EventCount);
            Assert.Equal(new CdcCheckpoint(7, 2), spool.AcknowledgedCheckpoint);
        }

        await using (var view = CdcSourceReadView.Open(viewPath, descriptor, replicaOptions))
        await using (var replica = CdcSnapshotReplica.Open(replicaPath, descriptor, replicaOptions))
        await using (var spool = new CdcEventSpool(spoolPath, spoolOptions))
        {
            CdcSnapshotReplicaState resumed = await CdcLocalReplicaPump.AdvanceAsync(
                view, spool, replica, maxRows: 1, maxBytes: 4096);
            Assert.Equal(new CdcCheckpoint(7, 4), resumed.AppliedCheckpoint);
            Assert.Equal(new CdcCheckpoint(7, 4), spool.AcknowledgedCheckpoint);
            Assert.Equal(0, spool.EventCount);
            CdcSnapshotRowBatch first = await replica.ReadRowsAsync(maxRows: 1);
            CdcSnapshotRowBatch second = await replica.ReadRowsAsync(first.NextKey, maxRows: 1);
            CdcSnapshotRowBatch third = await replica.ReadRowsAsync(second.NextKey, maxRows: 1);
            Assert.Equal([Row("a", 2), Row("b", 1), Row("c", 3)],
                first.Rows.Concat(second.Rows).Concat(third.Rows));
        }
    }

    [Fact]
    public async Task DocumentPipeline_ConcurrentPumpCalls_DoNotDuplicateSnapshotPageOrIncrement()
    {
        string databasePath = Path.Combine(_root, "concurrent-pump-db");
        string viewPath = Path.Combine(_root, "concurrent-pump-view.bin");
        string replicaPath = Path.Combine(_root, "concurrent-pump-replica.bin");
        string spoolPath = Path.Combine(_root, "concurrent-pump-events.log");
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath });
        database.Documents.Create(DocumentCollectionSchema.Create("docs"));
        var store = database.Documents.Open("docs");
        store.Insert("a", "{\"value\":1}");
        CdcDocumentSourceCaptureOptions captureOptions = CaptureOptions(batchSize: 1);
        await using var spool = new CdcEventSpool(spoolPath,
            new CdcEventSpoolOptions { MaxEvents = 1, MaxReplayEvents = 1 });
        await using var capture = new CdcDocumentSourceCapture(store, spool, captureOptions);
        await capture.CaptureAsync();
        await using var view = await CdcSourceReadView.CaptureDocumentCollectionAsync(
            viewPath, store, captureOptions.Source, partition: captureOptions.Partition,
            options: new CdcSnapshotReplicaOptions { MaxBatchRows = 1 });
        await spool.AcknowledgeAsync(view.Descriptor.Checkpoint);
        await using var replica = await CdcSnapshotReplica.CreateAsync(replicaPath, view.Descriptor,
            new CdcSnapshotReplicaOptions { MaxBatchRows = 1 });
        store.Replace("a", "{\"value\":2}");
        await capture.CaptureAsync();

        Task<CdcSnapshotReplicaState>[] calls = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() => CdcLocalReplicaPump.AdvanceAsync(
                view, spool, replica, maxRows: 1, maxBytes: 4096).AsTask()))
            .ToArray();
        await Task.WhenAll(calls);

        Assert.Equal(CdcSnapshotPhase.Incremental, replica.GetState().Phase);
        Assert.Equal(1, replica.GetState().SnapshotRowsCopied);
        Assert.Equal(new CdcCheckpoint(7, 2), replica.GetState().AppliedCheckpoint);
        Assert.Equal(new CdcCheckpoint(7, 2), spool.AcknowledgedCheckpoint);
        Assert.Equal(0, spool.EventCount);
        Assert.Equal([Row("a", 2)], (await replica.ReadRowsAsync()).Rows);
    }

    [Fact]
    public async Task DocumentPipeline_ConcurrentCaptureAndPump_PagedBacklogReconciles()
    {
        string databasePath = Path.Combine(_root, "concurrent-capture-db");
        string viewPath = Path.Combine(_root, "concurrent-capture-view.bin");
        string replicaPath = Path.Combine(_root, "concurrent-capture-replica.bin");
        string spoolPath = Path.Combine(_root, "concurrent-capture-events.log");
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = databasePath });
        database.Documents.Create(DocumentCollectionSchema.Create("docs"));
        var store = database.Documents.Open("docs");
        store.Insert("a", "{\"value\":1}");
        await using var spool = new CdcEventSpool(spoolPath,
            new CdcEventSpoolOptions { MaxEvents = 20, MaxReplayEvents = 1 });
        await using var capture = new CdcDocumentSourceCapture(store, spool, CaptureOptions(batchSize: 1));
        await capture.CaptureAsync();
        await using var view = await CdcSourceReadView.CaptureDocumentCollectionAsync(
            viewPath, store, "source-db-1", partition: 7);
        await spool.AcknowledgeAsync(view.Descriptor.Checkpoint);
        await using var replica = await CdcSnapshotReplica.CreateAsync(replicaPath, view.Descriptor);
        await CdcLocalReplicaPump.AdvanceAsync(view, spool, replica, maxRows: 1, maxBytes: 4096);
        await CdcLocalReplicaPump.AdvanceAsync(view, spool, replica, maxRows: 1, maxBytes: 4096);
        Assert.Equal(CdcSnapshotPhase.Incremental, replica.GetState().Phase);

        for (int value = 2; value <= 6; value++)
        {
            store.Replace("a", $"{{\"value\":{value}}}");
            await capture.CaptureAsync();
        }
        Assert.Equal(5, spool.EventCount);
        for (int value = 7; value <= 13; value++)
            store.Replace("a", $"{{\"value\":{value}}}");

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task captureTask = Task.Run(async () =>
        {
            await start.Task;
            for (int index = 0; index < 7; index++)
                await capture.CaptureAsync();
        });
        Task pumpTask = Task.Run(async () =>
        {
            await start.Task;
            for (int index = 0; index < 16; index++)
            {
                await CdcLocalReplicaPump.AdvanceAsync(
                    view, spool, replica, maxRows: 1, maxBytes: 4096);
                await Task.Yield();
            }
        });
        start.SetResult();
        await Task.WhenAll(captureTask, pumpTask);
        for (int index = 0; index < 12 && replica.GetState().AppliedCheckpoint.Offset < 13; index++)
            await CdcLocalReplicaPump.AdvanceAsync(
                view, spool, replica, maxRows: 1, maxBytes: 4096);

        Assert.Equal(new CdcCheckpoint(7, 13), replica.GetState().AppliedCheckpoint);
        Assert.Equal(new CdcCheckpoint(7, 13), spool.AcknowledgedCheckpoint);
        Assert.Equal(0, spool.EventCount);
        Assert.Equal([Row("a", 13)], (await replica.ReadRowsAsync()).Rows);
    }

    private static CdcDocumentSourceCaptureOptions CaptureOptions(int batchSize = 16)
        => new()
        {
            Source = "source-db-1",
            Entity = "docs",
            Schema = "documents",
            SchemaVersion = 1,
            Partition = 7,
            BatchSize = batchSize,
        };

    private static CdcSnapshotDescriptor Descriptor(long rowCount, long offset)
        => new("fixed-view-1", "test-source", "documents", "documents", 1, new CdcCheckpoint(7, offset), rowCount);

    private static CdcSnapshotRow Row(string key, int value)
        => new(key, $"{{\"value\":{value}}}");

    private static CdcEvent Event(long offset, string key, CdcOperation operation, string? after = null)
        => new(
            $"event-{offset}",
            "test-source",
            "documents",
            key,
            offset,
            new DateTimeOffset(2026, 9, 30, 0, 0, 0, TimeSpan.Zero),
            new CdcEventMetadata(1, "documents", 1, operation, new CdcCheckpoint(7, offset)),
            null,
            after);
}
