using System.Text;
using SonnetDB.Streaming;
using Xunit;

namespace SonnetDB.Core.Tests.Streaming;

/// <summary>M43 持久订阅任务目录的本地合同回归。</summary>
public sealed class FileStreamingTaskCatalogTests
{
    [Fact]
    public async Task RegisterReopenAndStatus_ReadsRealSubscriptionState()
    {
        using var directory = new TemporaryDirectory();
        StreamingTaskDefinition task = Task("orders-task", "orders");
        await using (var catalog = await FileStreamingTaskCatalog.OpenAsync(directory.Path))
        {
            Assert.Equal(0, catalog.Revision);
            Assert.Equal(1, await catalog.RegisterAsync(task, catalog.Revision));
            await using (FileStreamingSubscription subscription = await catalog.OpenSubscriptionAsync(task.TaskId))
                await subscription.PublishAsync(Event(1));
            FileStreamingSubscriptionStatus status = await catalog.GetStatusAsync(task.TaskId);
            Assert.Equal(1, status.PendingEventCount);
            Assert.Equal(task.SubscriptionDefinition.SubscriptionId, status.SubscriptionId);
        }

        await using var reopened = await FileStreamingTaskCatalog.OpenAsync(directory.Path);
        Assert.Equal(1, reopened.Revision);
        StreamingTaskDefinition? restored = await reopened.GetAsync(task.TaskId);
        Assert.NotNull(restored);
        Assert.Equal(task, restored);
        FileStreamingSubscriptionStatus restoredStatus = await reopened.GetStatusAsync(task.TaskId);
        Assert.Equal(1, restoredStatus.PendingEventCount);
    }

    [Fact]
    public async Task RegisterAndUpdate_RequireRevisionAndRejectDuplicateIdentity()
    {
        using var directory = new TemporaryDirectory();
        await using var catalog = await FileStreamingTaskCatalog.OpenAsync(directory.Path);
        StreamingTaskDefinition first = Task("task-a", "a");
        StreamingTaskDefinition second = Task("task-b", "b");
        await catalog.RegisterAsync(first);
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.RegisterAsync(second, expectedRevision: 0).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.RegisterAsync(
            first with { TaskId = "task-b" }, catalog.Revision).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.UpdateAsync(
            first with { DirectoryName = "b" }, catalog.Revision - 1).AsTask());
        long revision = await catalog.UpdateAsync(first with { SubscriptionDefinition = first.SubscriptionDefinition with { StreamName = "new-stream" } }, catalog.Revision);
        Assert.Equal(2, revision);
    }

    [Fact]
    public async Task ListAsync_UsesBoundedRevisionCursorAndRejectsStaleCursor()
    {
        using var directory = new TemporaryDirectory();
        await using var catalog = await FileStreamingTaskCatalog.OpenAsync(directory.Path, new FileStreamingTaskCatalogOptions { MaxPageSize = 2 });
        await catalog.RegisterAsync(Task("c", "c"));
        await catalog.RegisterAsync(Task("a", "a"), catalog.Revision);
        await catalog.RegisterAsync(Task("b", "b"), catalog.Revision);
        StreamingTaskCatalogPage first = await catalog.ListAsync(2);
        Assert.Equal(["a", "b"], first.Items.Select(static item => item.TaskId));
        Assert.NotNull(first.ContinuationToken);
        StreamingTaskCatalogPage second = await catalog.ListAsync(2, first.ContinuationToken);
        Assert.Equal(["c"], second.Items.Select(static item => item.TaskId));
        await catalog.RegisterAsync(Task("d", "d"), catalog.Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.ListAsync(2, first.ContinuationToken).AsTask());
    }

    [Fact]
    public async Task OpenAsync_WithCorruptJsonOrSecondExecutor_FailsClosed()
    {
        using var directory = new TemporaryDirectory();
        await using (FileStreamingTaskCatalog catalog = await FileStreamingTaskCatalog.OpenAsync(directory.Path))
        {
            await catalog.RegisterAsync(Task("task", "task"));
            await Assert.ThrowsAnyAsync<IOException>(() => FileStreamingTaskCatalog.OpenAsync(directory.Path).AsTask());
        }
        using var corruptDirectory = new TemporaryDirectory();
        await using (FileStreamingTaskCatalog catalog = await FileStreamingTaskCatalog.OpenAsync(corruptDirectory.Path))
        {
            await catalog.RegisterAsync(Task("corrupt", "corrupt"));
        }
        string statePath = System.IO.Path.Combine(corruptDirectory.Path, "tasks.catalog.json");
        await File.AppendAllTextAsync(statePath, "x", Encoding.UTF8);
        await Assert.ThrowsAsync<InvalidDataException>(
            () => FileStreamingTaskCatalog.OpenAsync(corruptDirectory.Path).AsTask());
        Assert.True(new FileInfo(statePath).Length > 0);
    }

    [Fact]
    public void Definition_RejectsAbsoluteAndEscapingPaths()
    {
        StreamingSubscriptionDefinition definition = StreamingSubscriptionDefinition.Create("id", "stream");
        Assert.Throws<ArgumentException>(() => StreamingTaskDefinition.Create("id", "..\\outside", definition));
        Assert.Throws<ArgumentException>(() => StreamingTaskDefinition.Create("id", Path.GetFullPath("outside"), definition));
        Assert.Throws<ArgumentException>(() => StreamingTaskDefinition.Create("id", "C:outside", definition));
    }

    [Fact]
    public async Task PauseAndResume_UseRealSubscriptionStateRevision()
    {
        using var directory = new TemporaryDirectory();
        await using var catalog = await FileStreamingTaskCatalog.OpenAsync(directory.Path);
        StreamingTaskDefinition task = Task("task", "task");
        await catalog.RegisterAsync(task);
        FileStreamingSubscriptionStatus before = await catalog.GetStatusAsync(task.TaskId);
        long paused = await catalog.PauseConsumptionAsync(task.TaskId, before.StateRevision);
        FileStreamingSubscriptionStatus pausedStatus = await catalog.GetStatusAsync(task.TaskId);
        Assert.True(pausedStatus.ConsumptionPaused);
        Assert.Equal(paused, pausedStatus.StateRevision);
        long resumed = await catalog.ResumeConsumptionAsync(task.TaskId, paused);
        Assert.False((await catalog.GetStatusAsync(task.TaskId)).ConsumptionPaused);
        Assert.Equal(paused + 1, resumed);
    }

    private static StreamingTaskDefinition Task(string taskId, string directoryName)
        => StreamingTaskDefinition.Create(
            taskId,
            directoryName,
            StreamingSubscriptionDefinition.Create(taskId, "orders", batchSize: 2, capacity: 4));

    private static StreamingEvent Event(long sequence)
        => new($"event-{sequence}", sequence, DateTimeOffset.UnixEpoch.AddSeconds(sequence), [1, 2, 3]);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sonnetdb-task-catalog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            string absolutePath = System.IO.Path.GetFullPath(Path);
            string tempRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
            if (!absolutePath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
                || !System.IO.Path.GetFileName(absolutePath).StartsWith("sonnetdb-task-catalog-", StringComparison.Ordinal))
                throw new InvalidOperationException("测试目录不属于任务目录测试。");
            for (int attempt = 0; attempt < 50 && Directory.Exists(absolutePath); attempt++)
            {
                try
                {
                    Directory.Delete(absolutePath, recursive: true);
                    break;
                }
                catch (IOException) when (attempt < 49)
                {
                    Thread.Sleep(100);
                }
            }
            if (Directory.Exists(absolutePath))
                throw new IOException("任务目录锁在有限清理重试后仍未释放。");
        }
    }
}
