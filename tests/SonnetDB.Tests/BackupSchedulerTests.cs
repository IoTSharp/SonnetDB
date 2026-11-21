using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SonnetDB.Backup;
using SonnetDB.Configuration;
using SonnetDB.Hosting;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>核对计划备份的范围拒绝、目录归属和成功批次发布。</summary>
public sealed class BackupSchedulerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnetdb-task-backup-" + Guid.NewGuid().ToString("N"));

    /// <summary>拒绝路径型 ID 和设备名，同时兼容既有每日任务 ID。</summary>
    [Theory]
    [InlineData("../foreign")]
    [InlineData("nested/name")]
    [InlineData("CON")]
    public void Upsert_RejectsUnsafeId(string id)
    {
        var store = new BackupTaskStore(_root);
        Assert.Throws<ArgumentException>(() => store.Upsert(new BackupTaskDefinition { Id = id }));
        Assert.Equal("daily-ab12", store.Upsert(new BackupTaskDefinition { Id = "daily-ab12" }).Id);
    }

    /// <summary>任务拒绝没有实现的模型或资源选择。</summary>
    [Fact]
    public void Upsert_RejectsUnsupportedScope()
    {
        var store = new BackupTaskStore(_root);
        foreach (var scope in new BackupScopeOptions[]
        {
            new() { IncludeMessageQueue = true },
            new() { Measurements = ["metrics"] },
            new() { Tables = ["roads"] },
            new() { DocumentCollections = ["logs"] },
            new() { Keyspaces = ["cache"] },
            new() { Buckets = ["images"] },
            new() { Graphs = ["roads"] },
            new() { IncludeObjects = true, IncludeKeyValue = false },
        })
            Assert.Throws<ArgumentException>(() => store.Upsert(new BackupTaskDefinition { Scope = scope }));
    }

    /// <summary>失败保留最近成功点，后续成功只轮换有归属标记的批次。</summary>
    [Fact]
    public async Task RunNow_PublishesVerifiedBatchAndPreservesForeignDirectories()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string backupRoot = Path.Combine(_root, "backups");
        using var registry = new TsdbRegistry(Path.Combine(_root, "source"));
        Assert.True(registry.TryCreate("site", out _));
        var store = new BackupTaskStore(registry.DataRoot);
        using var scheduler = new BackupSchedulerService(registry, store,
            Options.Create(new ServerOptions { Backup = new BackupRuntimeOptions { DestinationRoot = backupRoot } }),
            NullLogger<BackupSchedulerService>.Instance, TimeProvider.System);
        var task = store.Upsert(new BackupTaskDefinition { Id = "daily-site", RetentionCount = 1, DatabaseNames = ["site"] });
        string taskRoot = Path.Combine(backupRoot, task.Id);
        string foreignPath = Path.Combine(taskRoot, "20200101-010101-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(foreignPath);
        File.WriteAllText(Path.Combine(foreignPath, "foreign.txt"), "owner");
        string unmarkedStaging = Path.Combine(taskRoot, ".staging-foreign");
        Directory.CreateDirectory(unmarkedStaging);

        BackupTaskState first = await scheduler.RunNowAsync(task.Id, deadline.Token);
        Assert.Equal("ok:1", first.Status);
        var manifests = new BackupService();
        string firstPath = Assert.IsType<string>(first.LastOutputDirectory);
        BackupManifest manifest = manifests.ReadManifest(Path.Combine(firstPath, "site"));
        Assert.False(manifest.Scope.Includes(BackupModelScope.KeyValue));
        Assert.False(manifest.Scope.Includes(BackupModelScope.ObjectContent));
        Assert.True(manifests.Verify(Path.Combine(firstPath, "site")).IsValid);
        Assert.True(Directory.Exists(foreignPath));

        task.DatabaseNames = ["site", "missing"];
        store.Upsert(task);
        BackupTaskState failed = await scheduler.RunNowAsync(task.Id, deadline.Token);
        Assert.Equal("failed", failed.Status);
        Assert.Equal(firstPath, failed.LastOutputDirectory);
        Assert.True(Directory.Exists(firstPath));

        task.DatabaseNames = ["site"];
        store.Upsert(task);
        BackupTaskState second = await scheduler.RunNowAsync(task.Id, deadline.Token);
        Assert.Equal("ok:1", second.Status);
        Assert.NotEqual(firstPath, second.LastOutputDirectory);
        Assert.False(Directory.Exists(firstPath));
        Assert.True(Directory.Exists(second.LastOutputDirectory));
        Assert.True(Directory.Exists(foreignPath));
        Assert.True(Directory.Exists(unmarkedStaging));
        Assert.Equal(3, Directory.GetDirectories(taskRoot).Length);
    }

    /// <summary>任务不会将外置对象卷误报为数据库备份的一部分。</summary>
    [Fact]
    public void ValidateTaskDestination_RejectsExternalObjectVolume()
    {
        using var registry = new TsdbRegistry(Path.Combine(_root, "source"), null, null, null, null,
            Path.Combine(_root, "objects"));
        Assert.True(registry.TryCreate("site", out _));
        using var scheduler = new BackupSchedulerService(registry, new BackupTaskStore(registry.DataRoot),
            Options.Create(new ServerOptions
            {
                Backup = new BackupRuntimeOptions { DestinationRoot = Path.Combine(_root, "backups") },
            }), NullLogger<BackupSchedulerService>.Instance, TimeProvider.System);
        var task = new BackupTaskDefinition
        {
            Scope = new BackupScopeOptions { IncludeKeyValue = true, IncludeObjects = true },
        };

        Assert.Throws<InvalidOperationException>(() => scheduler.ValidateTaskDestination(task));
    }

    /// <summary>阻止不同任务在彼此的保留目录内写入备份。</summary>
    [Fact]
    public void ValidateTaskDestination_RejectsOverlappingTaskDirectories()
    {
        string backupRoot = Path.Combine(_root, "backups");
        using var registry = new TsdbRegistry(Path.Combine(_root, "source"));
        var store = new BackupTaskStore(registry.DataRoot);
        var first = store.Upsert(new BackupTaskDefinition { Id = "first" });
        using var scheduler = new BackupSchedulerService(registry, store,
            Options.Create(new ServerOptions { Backup = new BackupRuntimeOptions { DestinationRoot = backupRoot } }),
            NullLogger<BackupSchedulerService>.Instance, TimeProvider.System);

        var second = new BackupTaskDefinition { Id = "second", DestinationRoot = Path.Combine(backupRoot, first.Id) };

        Assert.Throws<InvalidOperationException>(() => scheduler.ValidateTaskDestination(second));
        Assert.False(Directory.Exists(backupRoot));
    }

    /// <summary>清理该测试独有的临时目录。</summary>
    public void Dispose()
    {
        BackupSchedulerService.RejectLinkedPath(_root);
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
