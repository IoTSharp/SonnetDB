using System.Text;
using System.Text.Json;
using SonnetDB.SemanticContent;

namespace SonnetDB.Core.Tests.SemanticContent;

/// <summary>
/// 通用 RAG 增量计划、执行预算和 AOT JSON 合同测试。
/// </summary>
public sealed class RagIngestionPlannerTests
{
    [Fact]
    public void CreatePlan_WithUnchangedContentAndDifferentRuntimeState_ReturnsNoOp()
    {
        var previousManifest = CreateManifest("manual", "same content", "v1") with
        {
            IndexState = new SemanticIndexStateInfo(
                SemanticIndexState.Ready,
                attempt: 1,
                updatedUtc: DateTimeOffset.Parse("2026-08-01T00:00:00Z")),
            Embeddings =
            [
                new SemanticEmbeddingBinding("body", "text-v1")
                {
                    IndexState = new SemanticIndexStateInfo(SemanticIndexState.Ready),
                },
            ],
        };
        var currentManifest = previousManifest with
        {
            IndexState = new SemanticIndexStateInfo(
                SemanticIndexState.Running,
                attempt: 2,
                updatedUtc: DateTimeOffset.Parse("2026-08-02T00:00:00Z")),
            Embeddings =
            [
                new SemanticEmbeddingBinding("body", "text-v1")
                {
                    IndexState = new SemanticIndexStateInfo(SemanticIndexState.Running),
                },
            ],
            UpdatedUtc = DateTimeOffset.Parse("2026-08-02T00:00:00Z"),
        };

        var plan = RagIngestionPlanner.CreatePlan(
            Snapshot(previousManifest),
            Snapshot(currentManifest));

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.Actions);
    }

    [Fact]
    public void CreatePlan_WithAddedUpdatedAndMissingContent_EmitsExplicitStableActions()
    {
        var deleted = CreateManifest("a-deleted", "old", "v1");
        var oldUpdated = CreateManifest("b-updated", "before", "v1");
        var newUpdated = CreateManifest("b-updated", "after", "v2");
        var added = CreateManifest("c-added", "new", "v1");

        var plan = RagIngestionPlanner.CreatePlan(
            new RagIngestionSnapshot([oldUpdated, deleted]),
            new RagIngestionSnapshot([added, newUpdated]));

        Assert.Equal(3, plan.Actions.Count);
        Assert.Equal(1, plan.AddCount);
        Assert.Equal(1, plan.UpdateCount);
        Assert.Equal(1, plan.DeleteCount);
        Assert.Collection(
            plan.Actions,
            action =>
            {
                Assert.Equal(RagIngestionActionKind.Update, action.Kind);
                Assert.Equal("b-updated", action.ContentId);
                AssertManifestContent(oldUpdated, action.Previous);
                AssertManifestContent(newUpdated, action.Current);
            },
            action =>
            {
                Assert.Equal(RagIngestionActionKind.Add, action.Kind);
                Assert.Equal("c-added", action.ContentId);
                Assert.Null(action.Previous);
                AssertManifestContent(added, action.Current);
            },
            action =>
            {
                Assert.Equal(RagIngestionActionKind.Delete, action.Kind);
                Assert.Equal("a-deleted", action.ContentId);
                AssertManifestContent(deleted, action.Previous);
                Assert.Null(action.Current);
            });
    }

    [Fact]
    public void CreatePlan_WithOneShotManifestLists_ReadsEachSourceIndexOnce()
    {
        SemanticContentManifest previousManifest = CreateManifest("frozen", "before", "v1");
        SemanticContentManifest currentManifest = CreateManifest("frozen", "after", "v2");
        var previousManifests = new OneShotReadOnlyList<SemanticContentManifest>([previousManifest]);
        var currentManifests = new OneShotReadOnlyList<SemanticContentManifest>([currentManifest]);
        var previous = new RagIngestionSnapshot { Manifests = previousManifests };
        var current = new RagIngestionSnapshot { Manifests = currentManifests };

        RagIngestionPlan plan = RagIngestionPlanner.CreatePlan(
            previous,
            current,
            new RagIngestionPlanningOptions { MaxManifests = 1 });

        RagIngestionAction action = Assert.Single(plan.Actions);
        Assert.Equal(RagIngestionActionKind.Update, action.Kind);
        Assert.Equal("before", action.Previous?.Text);
        Assert.Equal("after", action.Current?.Text);
        Assert.Equal(1, previousManifests.CountReads);
        Assert.Equal(1, previousManifests.GetIndexReads(0));
        Assert.Equal(1, currentManifests.CountReads);
        Assert.Equal(1, currentManifests.GetIndexReads(0));
    }

    [Fact]
    public void CreatePlan_WithDuplicateIdsOrExceededBudget_RejectsAmbiguousWork()
    {
        var duplicate = CreateManifest("duplicate", "same", "v1");
        Assert.Throws<ArgumentException>(() => RagIngestionPlanner.CreatePlan(
            null,
            new RagIngestionSnapshot([duplicate, duplicate])));

        Assert.Throws<InvalidOperationException>(() => RagIngestionPlanner.CreatePlan(
            null,
            new RagIngestionSnapshot(
            [
                CreateManifest("one", "one", "v1"),
                CreateManifest("two", "two", "v1"),
            ]),
            new RagIngestionPlanningOptions { MaxActions = 1 }));
    }

    [Fact]
    public void CreatePlan_WhenManifestCountExceedsBudget_RejectsBeforeReadingItems()
    {
        var manifests = new CountOnlyReadOnlyList<SemanticContentManifest>(reportedCount: 2);
        var current = new RagIngestionSnapshot { Manifests = manifests };

        Assert.Throws<InvalidOperationException>(() => RagIngestionPlanner.CreatePlan(
            null,
            current,
            new RagIngestionPlanningOptions { MaxManifests = 1 }));

        Assert.Equal(1, manifests.CountReads);
        Assert.Equal(0, manifests.IndexReads);
    }

    [Fact]
    public void CreatePlan_WithCanceledToken_StopsBeforeComparison()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => RagIngestionPlanner.CreatePlan(
            null,
            Snapshot(CreateManifest("manual", "content", "v1")),
            cancellationToken: cancellation.Token));
    }

    [Fact]
    public void CreatePlan_WhenNestedSnapshotBudgetsAreExceeded_RejectsBeforeManifestValidation()
    {
        SemanticContentManifest baseline = CreateManifest("budget", "abcd", "v1");
        SemanticContentChunk chunk = Assert.Single(baseline.Chunks);
        var tooManyChunks = baseline with
        {
            MimeType = "invalid-before-budget",
            Chunks =
            [
                chunk,
                chunk with { Id = chunk.Id + "-second", Ordinal = 1 },
            ],
        };
        var tooManySegments = baseline with
        {
            Segments =
            [
                new SemanticContentSegment("segment-1", 0, 0, 1, "a"),
                new SemanticContentSegment("segment-2", 1, 1, 2, "b"),
            ],
        };

        Assert.Throws<InvalidOperationException>(() => RagIngestionPlanner.CreatePlan(
            null,
            Snapshot(tooManyChunks),
            new RagIngestionPlanningOptions { MaxTotalChunks = 1 }));
        Assert.Throws<InvalidOperationException>(() => RagIngestionPlanner.CreatePlan(
            null,
            Snapshot(tooManySegments),
            new RagIngestionPlanningOptions { MaxTotalSegments = 1 }));
        Assert.Throws<InvalidOperationException>(() => RagIngestionPlanner.CreatePlan(
            null,
            Snapshot(baseline),
            new RagIngestionPlanningOptions { MaxTotalTextCharacters = 7 }));
    }

    [Fact]
    public void CreatePlan_WhenCanceledDuringNestedBudgetTraversal_StopsPromptly()
    {
        using var cancellation = new CancellationTokenSource();
        SemanticContentManifest baseline = CreateManifest("nested-cancel", "content", "v1");
        SemanticContentChunk chunk = Assert.Single(baseline.Chunks);
        var manifest = baseline with
        {
            Chunks = new CancelingReadOnlyList<SemanticContentChunk>(
                [chunk, chunk with { Id = chunk.Id + "-second", Ordinal = 1 }],
                cancellation),
        };

        Assert.Throws<OperationCanceledException>(() => RagIngestionPlanner.CreatePlan(
            null,
            Snapshot(manifest),
            cancellationToken: cancellation.Token));
    }

    [Fact]
    public void CreatePlan_WhenCombinedEmbeddingBudgetIsExceeded_RejectsBeforeComparison()
    {
        SemanticContentManifest previous = CreateManifest("embedding-budget", "same", "v1") with
        {
            Embeddings = [new SemanticEmbeddingBinding("body", "text-v1")],
        };
        SemanticContentManifest current = previous with
        {
            Embeddings = [new SemanticEmbeddingBinding("body", "text-v1")],
        };

        Assert.Throws<InvalidOperationException>(() => RagIngestionPlanner.CreatePlan(
            Snapshot(previous),
            Snapshot(current),
            new RagIngestionPlanningOptions { MaxTotalEmbeddings = 1 }));
    }

    [Fact]
    public void CreatePlan_WhenCanceledDuringEmbeddingTraversal_StopsBeforeValidation()
    {
        using var cancellation = new CancellationTokenSource();
        SemanticContentManifest manifest = CreateManifest("embedding-cancel", "content", "v1") with
        {
            MimeType = "invalid-before-cancellation",
            Embeddings = new CancelingReadOnlyList<SemanticEmbeddingBinding>(
                [new SemanticEmbeddingBinding("body", "text-v1")],
                cancellation),
        };

        Assert.Throws<OperationCanceledException>(() => RagIngestionPlanner.CreatePlan(
            null,
            Snapshot(manifest),
            cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task ExecuteAsync_WithBoundedConcurrency_NeverExceedsConfiguredWorkers()
    {
        var manifests = Enumerable.Range(0, 6)
            .Select(index => CreateManifest($"content-{index}", $"text-{index}", "v1"))
            .ToArray();
        var plan = RagIngestionPlanner.CreatePlan(null, new RagIngestionSnapshot(manifests));
        var twoStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new object();
        int active = 0;
        int maximumActive = 0;
        int started = 0;

        Task<RagIngestionExecutionResult> execution = RagIngestionExecutor.ExecuteAsync(
            plan,
            async (_, token) =>
            {
                int current = Interlocked.Increment(ref active);
                lock (gate)
                    maximumActive = Math.Max(maximumActive, current);
                if (Interlocked.Increment(ref started) == 2)
                    twoStarted.TrySetResult();

                await release.Task.WaitAsync(token);
                Interlocked.Decrement(ref active);
            },
            new RagIngestionExecutionOptions { MaxConcurrency = 2 }).AsTask();

        await twoStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, maximumActive);
        release.TrySetResult();
        var result = await execution;

        Assert.Equal(plan.Actions.Count, result.TotalActions);
        Assert.Equal(plan.Actions.Count, result.CompletedActions);
    }

    [Fact]
    public async Task ExecuteAsync_WithCancellation_StopsSchedulingRemainingActions()
    {
        var plan = RagIngestionPlanner.CreatePlan(
            null,
            new RagIngestionSnapshot(
            [
                CreateManifest("one", "one", "v1"),
                CreateManifest("two", "two", "v1"),
                CreateManifest("three", "three", "v1"),
            ]));
        using var cancellation = new CancellationTokenSource();
        int calls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await RagIngestionExecutor.ExecuteAsync(
                plan,
                (_, token) =>
                {
                    Interlocked.Increment(ref calls);
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                    return ValueTask.CompletedTask;
                },
                new RagIngestionExecutionOptions { MaxConcurrency = 1 },
                cancellation.Token));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ExecuteAsync_WhenPlanExceedsBudget_DoesNotInvokeWriter()
    {
        var plan = RagIngestionPlanner.CreatePlan(
            null,
            new RagIngestionSnapshot(
            [
                CreateManifest("one", "one", "v1"),
                CreateManifest("two", "two", "v1"),
            ]));
        int calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await RagIngestionExecutor.ExecuteAsync(
                plan,
                (_, _) =>
                {
                    calls++;
                    return ValueTask.CompletedTask;
                },
                new RagIngestionExecutionOptions { MaxActions = 1 }));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ExecuteAsync_WhenActionCountExceedsBudget_RejectsBeforeReadingItems()
    {
        var actions = new CountOnlyReadOnlyList<RagIngestionAction>(reportedCount: 2);
        var plan = new RagIngestionPlan { Actions = actions };
        int calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await RagIngestionExecutor.ExecuteAsync(
                plan,
                (_, _) =>
                {
                    calls++;
                    return ValueTask.CompletedTask;
                },
                new RagIngestionExecutionOptions { MaxActions = 1 }));

        Assert.Equal(0, calls);
        Assert.Equal(1, actions.CountReads);
        Assert.Equal(0, actions.IndexReads);
    }

    [Fact]
    public async Task ExecuteAsync_WithOneShotActionList_UsesOnlyFrozenAction()
    {
        SemanticContentManifest expectedManifest = CreateManifest("expected", "safe", "v1");
        SemanticContentManifest injectedManifest = CreateManifest("injected", "unsafe", "v1");
        var expectedAction = new RagIngestionAction(
            RagIngestionActionKind.Add,
            expectedManifest.Id,
            previous: null,
            expectedManifest);
        var injectedAction = new RagIngestionAction(
            RagIngestionActionKind.Add,
            injectedManifest.Id,
            previous: null,
            injectedManifest);
        var source = new SwitchingReadOnlyList<RagIngestionAction>([expectedAction], injectedAction);
        var plan = new RagIngestionPlan { Actions = source };
        RagIngestionAction? received = null;

        RagIngestionExecutionResult result = await RagIngestionExecutor.ExecuteAsync(
            plan,
            (action, _) =>
            {
                received = action;
                return ValueTask.CompletedTask;
            });

        Assert.Equal(1, result.TotalActions);
        Assert.Equal(1, result.CompletedActions);
        Assert.Equal(expectedManifest.Id, received?.ContentId);
        Assert.Equal("safe", received?.Current?.Text);
        Assert.Equal(1, source.CountReads);
        Assert.Equal(1, source.GetIndexReads(0));
    }

    [Fact]
    public async Task ExecuteAsync_WithMutableNestedList_PassesFrozenManifestToWriter()
    {
        SemanticContentManifest baseline = CreateManifest("nested-freeze", "safe", "v1");
        SemanticContentChunk safeChunk = Assert.Single(baseline.Chunks);
        SemanticContentChunk injectedChunk = safeChunk with { Text = "injected" };
        var chunks = new SwitchingReadOnlyList<SemanticContentChunk>([safeChunk], injectedChunk);
        SemanticContentManifest manifest = baseline with { Chunks = chunks };
        var plan = AddPlan(manifest);
        RagIngestionAction? received = null;

        await RagIngestionExecutor.ExecuteAsync(
            plan,
            (action, _) =>
            {
                received = action;
                return ValueTask.CompletedTask;
            });

        SemanticContentChunk receivedChunk = Assert.Single(received!.Current!.Chunks);
        Assert.Equal("safe", receivedChunk.Text);
        Assert.IsType<SemanticContentChunk[]>(received.Current.Chunks);
        Assert.Equal(1, chunks.CountReads);
        Assert.Equal(1, chunks.GetIndexReads(0));
    }

    [Fact]
    public async Task ExecuteAsync_WhenNestedPlanBudgetsAreExceeded_DoesNotInvokeWriter()
    {
        SemanticContentManifest baseline = CreateManifest("execution-budget", "abcd", "v1");
        SemanticContentChunk chunk = Assert.Single(baseline.Chunks);
        var tooManyChunks = baseline with
        {
            Chunks =
            [
                chunk,
                chunk with { Id = chunk.Id + "-second", Ordinal = 1 },
            ],
        };
        var tooManySegments = baseline with
        {
            Segments =
            [
                new SemanticContentSegment("segment-1", 0, 0, 1, "a"),
                new SemanticContentSegment("segment-2", 1, 1, 2, "b"),
            ],
        };
        var tooManyEmbeddings = baseline with
        {
            Embeddings =
            [
                new SemanticEmbeddingBinding("body", "text-v1"),
                new SemanticEmbeddingBinding("title", "text-v1"),
            ],
        };
        int calls = 0;
        ValueTask Apply(RagIngestionAction _, CancellationToken __)
        {
            calls++;
            return ValueTask.CompletedTask;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await RagIngestionExecutor.ExecuteAsync(
                AddPlan(tooManyChunks),
                Apply,
                new RagIngestionExecutionOptions { MaxTotalChunks = 1 }));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await RagIngestionExecutor.ExecuteAsync(
                AddPlan(tooManySegments),
                Apply,
                new RagIngestionExecutionOptions { MaxTotalSegments = 1 }));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await RagIngestionExecutor.ExecuteAsync(
                AddPlan(tooManyEmbeddings),
                Apply,
                new RagIngestionExecutionOptions { MaxTotalEmbeddings = 1 }));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await RagIngestionExecutor.ExecuteAsync(
                AddPlan(baseline),
                Apply,
                new RagIngestionExecutionOptions { MaxTotalTextCharacters = 7 }));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ExecuteAsync_WhenDeserializedPlanExceedsNestedBudget_DoesNotInvokeWriter()
    {
        SemanticContentManifest baseline = CreateManifest("json-budget", "content", "v1");
        SemanticContentChunk chunk = Assert.Single(baseline.Chunks);
        var oversized = baseline with
        {
            Chunks =
            [
                chunk,
                chunk with { Id = chunk.Id + "-second", Ordinal = 1 },
            ],
        };
        string json = JsonSerializer.Serialize(
            AddPlan(oversized),
            SemanticContentJsonContext.Default.RagIngestionPlan);
        RagIngestionPlan? plan = JsonSerializer.Deserialize(
            json,
            SemanticContentJsonContext.Default.RagIngestionPlan);
        int calls = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await RagIngestionExecutor.ExecuteAsync(
                plan!,
                (_, _) =>
                {
                    calls++;
                    return ValueTask.CompletedTask;
                },
                new RagIngestionExecutionOptions { MaxTotalChunks = 1 }));

        Assert.NotNull(plan);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ExecuteAsync_WhenCanceledDuringNestedCopy_DoesNotInvokeWriter()
    {
        SemanticContentManifest baseline = CreateManifest("execution-cancel", "content", "v1");
        SemanticContentChunk chunk = Assert.Single(baseline.Chunks);
        int calls = 0;

        using (var chunkCancellation = new CancellationTokenSource())
        {
            SemanticContentManifest manifest = baseline with
            {
                Chunks = new CancelingReadOnlyList<SemanticContentChunk>([chunk], chunkCancellation),
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await RagIngestionExecutor.ExecuteAsync(
                    AddPlan(manifest),
                    (_, _) =>
                    {
                        calls++;
                        return ValueTask.CompletedTask;
                    },
                    cancellationToken: chunkCancellation.Token));
        }

        using (var embeddingCancellation = new CancellationTokenSource())
        {
            SemanticContentManifest manifest = baseline with
            {
                Embeddings = new CancelingReadOnlyList<SemanticEmbeddingBinding>(
                    [new SemanticEmbeddingBinding("body", "text-v1")],
                    embeddingCancellation),
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await RagIngestionExecutor.ExecuteAsync(
                    AddPlan(manifest),
                    (_, _) =>
                    {
                        calls++;
                        return ValueTask.CompletedTask;
                    },
                    cancellationToken: embeddingCancellation.Token));
        }

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ExecuteAsync_WithDuplicateContentIds_RejectsBeforeInvokingWriter()
    {
        SemanticContentManifest manifest = CreateManifest("duplicate-action", "content", "v1");
        var plan = new RagIngestionPlan(
        [
            new RagIngestionAction(
                RagIngestionActionKind.Add,
                manifest.Id,
                previous: null,
                manifest),
            new RagIngestionAction(
                RagIngestionActionKind.Add,
                manifest.Id,
                previous: null,
                manifest),
        ]);
        int calls = 0;

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await RagIngestionExecutor.ExecuteAsync(
                plan,
                (_, _) =>
                {
                    calls++;
                    return ValueTask.CompletedTask;
                }));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ExecuteAsync_WithMalformedActionOrManifest_RejectsWholePlanBeforeWriter()
    {
        SemanticContentManifest valid = CreateManifest("valid", "content", "v1");
        SemanticContentManifest invalid = CreateManifest("invalid", "content", "v1") with
        {
            MimeType = "not-a-mime-type",
        };
        var malformedAction = new RagIngestionPlan(
        [
            new RagIngestionAction(
                RagIngestionActionKind.Delete,
                valid.Id,
                previous: null,
                current: valid),
        ]);
        var malformedManifest = new RagIngestionPlan(
        [
            new RagIngestionAction(
                RagIngestionActionKind.Add,
                valid.Id,
                previous: null,
                valid),
            new RagIngestionAction(
                RagIngestionActionKind.Add,
                invalid.Id,
                previous: null,
                invalid),
        ]);
        int calls = 0;
        ValueTask Apply(RagIngestionAction _, CancellationToken __)
        {
            calls++;
            return ValueTask.CompletedTask;
        }

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await RagIngestionExecutor.ExecuteAsync(malformedAction, Apply));
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await RagIngestionExecutor.ExecuteAsync(malformedManifest, Apply));

        Assert.Equal(0, calls);
    }

    [Fact]
    public void JsonRoundTrip_WithSnapshotAndPlan_UsesSourceGeneratedContracts()
    {
        var manifest = CreateManifest("manual", "semantic content", "v1");
        var snapshot = Snapshot(manifest);
        var plan = RagIngestionPlanner.CreatePlan(null, snapshot);

        string snapshotJson = JsonSerializer.Serialize(
            snapshot,
            SemanticContentJsonContext.Default.RagIngestionSnapshot);
        string planJson = JsonSerializer.Serialize(
            plan,
            SemanticContentJsonContext.Default.RagIngestionPlan);
        var roundTrippedSnapshot = JsonSerializer.Deserialize(
            snapshotJson,
            SemanticContentJsonContext.Default.RagIngestionSnapshot);
        var roundTrippedPlan = JsonSerializer.Deserialize(
            planJson,
            SemanticContentJsonContext.Default.RagIngestionPlan);

        Assert.NotNull(roundTrippedSnapshot);
        Assert.NotNull(roundTrippedPlan);
        Assert.Equal(manifest.Id, Assert.Single(roundTrippedSnapshot!.Manifests).Id);
        Assert.Equal(RagIngestionActionKind.Add, Assert.Single(roundTrippedPlan!.Actions).Kind);
        Assert.Contains("\"kind\":\"Add\"", planJson, StringComparison.Ordinal);
        Assert.DoesNotContain("isEmpty", planJson, StringComparison.Ordinal);
    }

    private static RagIngestionSnapshot Snapshot(params SemanticContentManifest[] manifests)
        => new(manifests);

    private static RagIngestionPlan AddPlan(SemanticContentManifest manifest)
        => new(
        [
            new RagIngestionAction(
                RagIngestionActionKind.Add,
                manifest.Id,
                previous: null,
                manifest),
        ]);

    private static void AssertManifestContent(
        SemanticContentManifest expected,
        SemanticContentManifest? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.ObjectRef, actual.ObjectRef);
        Assert.Equal(expected.ContentHash, actual.ContentHash);
        Assert.Equal(expected.Text, actual.Text);
    }

    private static SemanticContentManifest CreateManifest(
        string id,
        string text,
        string version)
    {
        var chunked = RagTextChunker.Chunk(id, text);
        var timestamp = DateTimeOffset.Parse("2026-08-01T00:00:00Z");
        return new SemanticContentManifest(
            id,
            new SemanticObjectReference("rag", id + ".txt", versionId: version),
            chunked.ContentHash,
            "text/plain",
            SemanticContentModality.Text,
            Encoding.UTF8.GetByteCount(text),
            source: "tests")
        {
            Text = text,
            Chunks = chunked.Chunks,
            CreatedUtc = timestamp,
            UpdatedUtc = timestamp,
        };
    }

    private sealed class CancelingReadOnlyList<T>(
        IReadOnlyList<T> values,
        CancellationTokenSource cancellation) : IReadOnlyList<T>
    {
        public T this[int index]
        {
            get
            {
                cancellation.Cancel();
                return values[index];
            }
        }

        public int Count => values.Count;

        public IEnumerator<T> GetEnumerator() => values.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => GetEnumerator();
    }

    private sealed class OneShotReadOnlyList<T>(IReadOnlyList<T> values) : IReadOnlyList<T>
    {
        private readonly int[] _indexReads = new int[values.Count];

        public T this[int index]
        {
            get
            {
                if (++_indexReads[index] > 1)
                    throw new InvalidOperationException($"索引 {index} 被读取多次。");
                return values[index];
            }
        }

        public int Count
        {
            get
            {
                CountReads++;
                if (CountReads > 1)
                    throw new InvalidOperationException("Count 被读取多次。");
                return values.Count;
            }
        }

        public int CountReads { get; private set; }

        public int GetIndexReads(int index) => _indexReads[index];

        public IEnumerator<T> GetEnumerator()
            => throw new InvalidOperationException("不应枚举调用方列表。");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => GetEnumerator();
    }

    private sealed class SwitchingReadOnlyList<T>(
        IReadOnlyList<T> values,
        T replacement) : IReadOnlyList<T>
    {
        private readonly int[] _indexReads = new int[values.Count];

        public T this[int index]
        {
            get
            {
                int reads = ++_indexReads[index];
                return reads == 1 ? values[index] : replacement;
            }
        }

        public int Count
        {
            get
            {
                CountReads++;
                return values.Count;
            }
        }

        public int CountReads { get; private set; }

        public int GetIndexReads(int index) => _indexReads[index];

        public IEnumerator<T> GetEnumerator()
            => throw new InvalidOperationException("不应枚举调用方列表。");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => GetEnumerator();
    }

    private sealed class CountOnlyReadOnlyList<T>(int reportedCount) : IReadOnlyList<T>
    {
        public T this[int index]
        {
            get
            {
                IndexReads++;
                throw new InvalidOperationException($"不应读取索引 {index}。");
            }
        }

        public int Count
        {
            get
            {
                CountReads++;
                return reportedCount;
            }
        }

        public int CountReads { get; private set; }

        public int IndexReads { get; private set; }

        public IEnumerator<T> GetEnumerator()
            => throw new InvalidOperationException("不应枚举调用方列表。");

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
            => GetEnumerator();
    }
}
