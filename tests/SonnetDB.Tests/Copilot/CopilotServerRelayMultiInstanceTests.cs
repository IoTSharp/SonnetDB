using System.Text.Json;
using SonnetDB.Contracts;
using SonnetDB.Endpoints;
using Xunit;

namespace SonnetDB.Tests.Copilot;

/// <summary>共享 journal 的单所有者、活跃续流和故障终态合同。</summary>
public sealed class CopilotServerRelayMultiInstanceTests
{
    private static readonly CopilotServerRelayRunBinding Binding = new("owner", "factory", "request");

    [Fact]
    public async Task Attach_WithoutSelectedDatabase_PersistsAndReplaysControlPlaneRun()
    {
        using var fixture = new SharedJournal();
        var binding = Binding with { DatabaseName = string.Empty };
        var created = fixture.First.Attach("control-plane", null, binding);
        Assert.Equal(CopilotServerRelayAttachStatus.Created, created.Status);
        var owner = created.Run!;
        owner.Publish(new CopilotChatEvent("start"));
        var attached = fixture.Second.Attach("control-plane", null, binding);
        Assert.Equal(CopilotServerRelayAttachStatus.Attached, attached.Status);
        owner.Publish(new CopilotChatEvent("final", Answer: "available databases"));
        owner.Complete();

        var followed = await ReadAllAsync(attached.Run!);
        using var reopened = new CopilotServerRelayRunStore(journalPath: fixture.Path);
        var replay = reopened.Attach("control-plane", null, binding);
        Assert.Equal(CopilotServerRelayAttachStatus.Attached, replay.Status);
        Assert.Equal(string.Empty, replay.Run!.Binding.DatabaseName);
        var replayed = await ReadAllAsync(replay.Run);
        Assert.Equal(["start", "final", "done"], replayed.Select(item => item.Type));
        Assert.Equal(followed.Select(item => item.Cursor), replayed.Select(item => item.Cursor));
        Assert.Equal("available databases", replayed[1].Answer);
    }

    [Fact]
    public async Task Attach_ConcurrentInstances_CreatesExactlyOneOwner()
    {
        using var fixture = new SharedJournal();
        using var barrier = new Barrier(2);
        Task<CopilotServerRelayAttachResult> Attach(CopilotServerRelayRunStore store) => Task.Run(() =>
        {
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(5)));
            return store.Attach("concurrent", null, Binding);
        });
        var results = await Task.WhenAll(Attach(fixture.First), Attach(fixture.Second)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(results, item => item.Status == CopilotServerRelayAttachStatus.Created);
        Assert.Single(results, item => item.Status == CopilotServerRelayAttachStatus.Attached);
        var owner = results.Single(item => item.Status == CopilotServerRelayAttachStatus.Created).Run!;
        owner.Publish(new CopilotChatEvent("final", Answer: "once"));
        owner.Complete();
        var events = await ReadAllAsync(results.Single(item => item.Status == CopilotServerRelayAttachStatus.Attached).Run!);
        Assert.Equal(["final", "done"], events.Select(item => item.Type));
    }

    [Fact]
    public async Task Attach_LiveOwner_FollowsNewEventsAndPreservesCursor()
    {
        using var fixture = new SharedJournal();
        var owner = fixture.First.Attach("live", null, Binding).Run!;
        var start = owner.Publish(new CopilotChatEvent("start", Message: "started"));
        var attached = fixture.Second.Attach("live", start.Cursor, Binding);
        Assert.Equal(CopilotServerRelayAttachStatus.Attached, attached.Status);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = attached.Run!.ReadAfterAsync(attached.AfterSequence, timeout.Token).GetAsyncEnumerator();
        Task<bool> pending = reader.MoveNextAsync().AsTask();
        Assert.False(pending.IsCompleted);
        owner.Publish(new CopilotChatEvent("final", Answer: "still running on owner"));
        owner.Complete();
        Assert.True(await pending);
        Assert.Equal("final", reader.Current.Type);
        Assert.Equal(2, reader.Current.Sequence);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal("done", reader.Current.Type);
        Assert.False(await reader.MoveNextAsync());
    }

    [Fact]
    public async Task Follower_CancellationAndUnrelatedWrites_DoNotCancelOrOverwriteOwner()
    {
        using var fixture = new SharedJournal();
        var owner = fixture.First.Attach("continued", null, Binding).Run!;
        var firstEvent = owner.Publish(new CopilotChatEvent("start"));
        var attached = fixture.Second.Attach("continued", firstEvent.Cursor, Binding);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await using var reader = attached.Run!.ReadAfterAsync(attached.AfterSequence, cancelled.Token).GetAsyncEnumerator();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.MoveNextAsync().AsTask());
        }
        Assert.False(owner.DeadlineToken.IsCancellationRequested);
        owner.Publish(new CopilotChatEvent("final", Answer: "newer"));
        owner.Complete();
        var unrelated = fixture.Second.Attach("other", null, Binding).Run!;
        unrelated.Publish(new CopilotChatEvent("final", Answer: "other"));
        unrelated.Complete();
        using var third = new CopilotServerRelayRunStore(journalPath: fixture.Path);
        var replay = third.Attach("continued", null, Binding);
        var events = await ReadAllAsync(replay.Run!);
        Assert.Equal(["start", "final", "done"], events.Select(item => item.Type));
        Assert.Equal("newer", events[1].Answer);
    }

    [Fact]
    public async Task Owner_Disposal_SealsOneInterruptedOutcomeForAllFollowers()
    {
        using var fixture = new SharedJournal();
        var owner = fixture.First.Attach("interrupted", null, Binding).Run!;
        owner.Publish(new CopilotChatEvent("start"));
        var follower = fixture.Second.Attach("interrupted", null, Binding).Run!;
        fixture.First.Dispose();
        var events = await ReadAllAsync(follower);
        Assert.Equal(["start", "error", "done"], events.Select(item => item.Type));
        using var third = new CopilotServerRelayRunStore(journalPath: fixture.Path);
        var replay = await ReadAllAsync(third.Attach("interrupted", null, Binding).Run!);
        Assert.Equal(events.Select(item => item.Cursor), replay.Select(item => item.Cursor));
        Assert.Single(replay, item => item.Type == "error");
        Assert.Single(replay, item => item.Type == "done");
        Assert.Throws<ObjectDisposedException>(() => owner.Publish(new CopilotChatEvent("final", Answer: "late")));
    }

    [Fact]
    public async Task Follower_StoreDisposal_CancelsItsWaitingReader()
    {
        using var fixture = new SharedJournal();
        var owner = fixture.First.Attach("dispose-follower", null, Binding).Run!;
        var follower = fixture.Second.Attach("dispose-follower", null, Binding).Run!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var reader = follower.ReadAfterAsync(0, timeout.Token).GetAsyncEnumerator();
        var pending = reader.MoveNextAsync().AsTask();
        fixture.Second.Dispose();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.False(owner.DeadlineToken.IsCancellationRequested);
    }

    [Fact]
    public void Follower_IdentityConflictAndPublish_AreRejected()
    {
        using var fixture = new SharedJournal();
        _ = fixture.First.Attach("identity", null, Binding);
        Assert.Equal(CopilotServerRelayAttachStatus.Conflict,
            fixture.Second.Attach("identity", null, Binding with { RequestFingerprint = "changed" }).Status);
        var follower = fixture.Second.Attach("identity", null, Binding).Run!;
        Assert.Throws<InvalidOperationException>(() => follower.Publish(new CopilotChatEvent("final", Answer: "wrong owner")));
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"runs\":[null]}")]
    [InlineData("{\"runs\":[{\"runId\":\"invalid\",\"binding\":{\"owner\":\"owner\",\"databaseName\":\"factory\",\"requestFingerprint\":\"request\"},\"events\":null}]}")]
    [InlineData("{\"runs\":[{\"runId\":\"invalid\",\"binding\":{\"owner\":\"owner\",\"databaseName\":\"factory\",\"requestFingerprint\":\"\"},\"events\":[]}]}")]
    public void Attach_MalformedJournal_RefusesNewProviderOwner(string json)
    {
        using var fixture = new SharedJournal();
        File.WriteAllText(fixture.Path, json);
        Assert.Throws<InvalidDataException>(() => fixture.First.Attach("invalid", null, Binding));
        Assert.Equal(json, File.ReadAllText(fixture.Path));
    }

    [Fact]
    public void Attach_DuplicatePersistedIdentity_RefusesNewOwner()
    {
        using var fixture = new SharedJournal();
        var snapshot = new CopilotServerRelayJournalRun("duplicate", Binding, DateTimeOffset.UtcNow.AddMinutes(1), null, false, []);
        WriteJournal(fixture.Path, [snapshot, snapshot]);
        Assert.Throws<InvalidDataException>(() => fixture.First.Attach("duplicate", null, Binding));
    }

    [Fact]
    public async Task Attach_DurableDoneBeforeOwnerCompletion_ReplaysCompletedOutcome()
    {
        using var fixture = new SharedJournal();
        using var source = new CopilotServerRelayRunStore();
        var run = source.Attach("durable-done", null, Binding).Run!;
        run.Publish(new CopilotChatEvent("final", Answer: "durable"));
        run.Publish(new CopilotChatEvent("done"));
        WriteJournal(fixture.Path, [run.CreateJournalSnapshot()]);
        var replay = fixture.First.Attach("durable-done", null, Binding);
        Assert.Equal(CopilotServerRelayAttachStatus.Attached, replay.Status);
        var events = await ReadAllAsync(replay.Run!);
        Assert.Equal(["final", "done"], events.Select(item => item.Type));
        var persisted = JsonSerializer.Deserialize(File.ReadAllText(fixture.Path),
            CopilotServerRelayJournalJsonContext.Default.CopilotServerRelayJournalDocument)!;
        Assert.True(Assert.Single(persisted.Runs).Completed);
    }

    [Fact]
    public async Task Attach_OwnerlessActiveTail_SealsExactlyOneDurableError()
    {
        using var fixture = new SharedJournal();
        using var source = new CopilotServerRelayRunStore();
        var run = source.Attach("orphan", null, Binding).Run!;
        run.Publish(new CopilotChatEvent("start"));
        WriteJournal(fixture.Path, [run.CreateJournalSnapshot()]);
        var first = await ReadAllAsync(fixture.First.Attach("orphan", null, Binding).Run!);
        var second = await ReadAllAsync(fixture.Second.Attach("orphan", null, Binding).Run!);
        Assert.Equal(["start", "error", "done"], first.Select(item => item.Type));
        Assert.Equal(first.Select(item => item.Cursor), second.Select(item => item.Cursor));
    }

    [Fact]
    public async Task Complete_JournalLockTimeout_ReleasesLeaseAndActiveCapacity()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var fixture = new SharedJournal();
        var owner = fixture.First.Attach("lock-timeout", null, Binding).Run!;
        owner.Publish(new CopilotChatEvent("start"));
        string before = File.ReadAllText(fixture.Path);
        using (var competingLock = new FileStream(fixture.Path + ".lock", FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            owner.Complete();
            Assert.InRange(started.Elapsed, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(8));
            Assert.Equal(before, File.ReadAllText(fixture.Path));
            Assert.Empty(Directory.GetFiles(System.IO.Path.GetDirectoryName(fixture.Path)!, "*.run-*.lock"));
        }

        deadline.Token.ThrowIfCancellationRequested();
        var recovered = fixture.Second.Attach("lock-timeout", null, Binding);
        Assert.Equal(CopilotServerRelayAttachStatus.Attached, recovered.Status);
        var events = await ReadAllAsync(recovered.Run!);
        Assert.Equal(["start", "error", "done"], events.Select(item => item.Type));

        // 完成失败后的旧 run 不得占用任一个活动槽位；64 是生产活动 run 上限。
        for (int index = 0; index < 64; index++)
        {
            deadline.Token.ThrowIfCancellationRequested();
            Assert.Equal(CopilotServerRelayAttachStatus.Created,
                fixture.First.Attach("capacity-" + index, null, Binding).Status);
        }
        var persisted = JsonSerializer.Deserialize(File.ReadAllText(fixture.Path),
            CopilotServerRelayJournalJsonContext.Default.CopilotServerRelayJournalDocument)!;
        var replay = Assert.Single(persisted.Runs, run => run.RunId == "lock-timeout");
        Assert.Equal(events.Select(item => item.Message), replay.Events.Select(item => item.Message));
    }

    [Fact]
    public void Dispose_ReentrantCancellationCallback_RejectsNewOwner()
    {
        using var fixture = new SharedJournal();
        var owner = fixture.First.Attach("dispose-reentrant", null, Binding).Run!;
        Exception? attachFailure = null;
        using var callback = owner.DeadlineToken.Register(() =>
        {
            fixture.First.Dispose();
            attachFailure = Record.Exception(() => fixture.First.Attach("late-owner", null, Binding));
        });

        fixture.First.Dispose();

        Assert.IsType<ObjectDisposedException>(attachFailure);
        Assert.Empty(Directory.GetFiles(System.IO.Path.GetDirectoryName(fixture.Path)!, "*.lock"));
        var persisted = JsonSerializer.Deserialize(File.ReadAllText(fixture.Path),
            CopilotServerRelayJournalJsonContext.Default.CopilotServerRelayJournalDocument)!;
        Assert.Equal("dispose-reentrant", Assert.Single(persisted.Runs).RunId);
        Assert.True(persisted.Runs[0].Completed);
    }

    [Fact]
    public void Dispose_ThrowingCancellationCallback_StillReleasesAllLeases()
    {
        using var fixture = new SharedJournal();
        var first = fixture.First.Attach("cancel-one", null, Binding).Run!;
        var second = fixture.First.Attach("cancel-two", null, Binding).Run!;
        using var callback = first.DeadlineToken.Register(static () => throw new InvalidOperationException("callback"));
        fixture.First.Dispose();
        Assert.True(first.DeadlineToken.IsCancellationRequested);
        Assert.True(second.DeadlineToken.IsCancellationRequested);
        Assert.Empty(Directory.GetFiles(System.IO.Path.GetDirectoryName(fixture.Path)!, "*.lock"));
    }

    private static void WriteJournal(string path, CopilotServerRelayJournalRun[] runs)
        => File.WriteAllText(path, JsonSerializer.Serialize(new CopilotServerRelayJournalDocument(runs),
            CopilotServerRelayJournalJsonContext.Default.CopilotServerRelayJournalDocument));

    private static async Task<List<CopilotChatEvent>> ReadAllAsync(CopilotServerRelayRun run)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var events = new List<CopilotChatEvent>();
        await foreach (var item in run.ReadAfterAsync(0, timeout.Token))
        {
            events.Add(item);
            Assert.True(events.Count <= 256);
        }
        return events;
    }

    private sealed class SharedJournal : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sndb-relay-multi-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public CopilotServerRelayRunStore First { get; }
        public CopilotServerRelayRunStore Second { get; }

        public SharedJournal()
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "journal.json");
            First = new CopilotServerRelayRunStore(journalPath: Path);
            Second = new CopilotServerRelayRunStore(journalPath: Path);
        }

        public void Dispose()
        {
            First.Dispose();
            Second.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }
}
