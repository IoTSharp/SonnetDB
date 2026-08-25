using System.Text;
using SonnetDB.Backup;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Generations;
using SonnetDB.Kv;
using Xunit;

namespace SonnetDB.Core.Tests.Generations;

public sealed class DatabaseGenerationManagerTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "sonnetdb-generations-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;

    public DatabaseGenerationManagerTests()
    {
        _root = Path.Combine(_testRoot, "database");
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Publish_AB_ReopenKeepsOnlyCompleteBActiveWithoutMixedModels()
    {
        using (var db = Open())
        {
            Publish(db, "a", "alpha", expectedRevision: 0);
        }

        using (var reopened = Open())
        {
            AssertGenerationQuery(reopened, "a", "alpha", expectedRevision: 1);
            Publish(reopened, "b", "bravo", expectedRevision: 1);
            AssertGenerationQuery(reopened, "b", "bravo", expectedRevision: 2);
            Assert.Equal([1L, 2L], reopened.Generations.List("workspace").Select(static item => item.Revision));
        }

        using var final = Open();
        AssertGenerationQuery(final, "b", "bravo", expectedRevision: 2);
        Assert.Equal([1L, 2L], final.Generations.List("workspace").Select(static item => item.Revision));
    }

    [Fact]
    public void Publish_FailureBeforeAtomicPointer_ReopenSeesA()
    {
        var db = Open();
        Publish(db, "a", "alpha", expectedRevision: 0);
        Stage(db, "b", "bravo");
        db.Generations.BeforePublishTestHook = static _ =>
            throw new InjectedGenerationFailureException();

        Assert.Throws<InjectedGenerationFailureException>(() =>
            db.Generations.Publish(Request("b", expectedRevision: 1)));
        db.CrashSimulationCloseWal();

        using var reopened = Open();
        AssertGenerationQuery(reopened, "a", "alpha", expectedRevision: 1);
        Assert.Single(reopened.Generations.List("workspace"));
    }

    [Fact]
    public void Publish_FailureAfterAtomicPointer_ReopenSeesCompleteB()
    {
        var db = Open();
        Publish(db, "a", "alpha", expectedRevision: 0);
        Stage(db, "b", "bravo");
        db.Generations.AfterPublishTestHook = static _ =>
            throw new InjectedGenerationFailureException();

        Assert.Throws<InjectedGenerationFailureException>(() =>
            db.Generations.Publish(Request("b", expectedRevision: 1)));
        db.CrashSimulationCloseWal();

        using var reopened = Open();
        AssertGenerationQuery(reopened, "b", "bravo", expectedRevision: 2);
        Assert.Equal([1L, 2L], reopened.Generations.List("workspace").Select(static item => item.Revision));
    }

    [Fact]
    public async Task CleanupRetired_ConcurrentActiveLease_DefersUntilLeaseReleased()
    {
        using var db = Open();
        Publish(db, "a", "alpha", expectedRevision: 0);
        DatabaseGenerationQueryLease firstLease = db.Generations.AcquireActive("workspace");
        DatabaseGenerationQueryLease secondLease = db.Generations.AcquireActive("workspace");
        Publish(db, "b", "bravo", expectedRevision: 1);

        DatabaseGenerationCleanupResult whileLeased = await Task.Run(() =>
            db.Generations.CleanupRetired("workspace"));

        Assert.Empty(whileLeased.RemovedRevisions);
        Assert.Equal([1L], whileLeased.DeferredRevisions);
        Assert.NotNull(db.Documents.Catalog.TryGet("docs_a"));
        Assert.Contains("kv_a", db.Keyspaces.List());

        firstLease.Dispose();
        DatabaseGenerationCleanupResult stillLeased = await Task.Run(() =>
            db.Generations.CleanupRetired("workspace"));

        Assert.Empty(stillLeased.RemovedRevisions);
        Assert.Equal([1L], stillLeased.DeferredRevisions);

        secondLease.Dispose();
        Assert.True(db.Documents.Drop("docs_a"));
        string orphanedFullTextDirectory = Path.Combine(
            _root,
            TsdbPaths.DocumentsDirName,
            "fulltext",
            Convert.ToHexString(Encoding.UTF8.GetBytes("docs_a")).ToLowerInvariant(),
            "orphan");
        Directory.CreateDirectory(orphanedFullTextDirectory);
        File.WriteAllText(Path.Combine(orphanedFullTextDirectory, "leftover"), "crash residue");
        DatabaseGenerationCleanupResult released = await Task.Run(() =>
            db.Generations.CleanupRetired("workspace"));

        Assert.Equal([1L], released.RemovedRevisions);
        Assert.Empty(released.DeferredRevisions);
        Assert.Null(db.Documents.Catalog.TryGet("docs_a"));
        Assert.DoesNotContain("kv_a", db.Keyspaces.List());
        Assert.False(Directory.Exists(Path.GetDirectoryName(orphanedFullTextDirectory)));
        AssertGenerationQuery(db, "b", "bravo", expectedRevision: 2);
    }

    [Fact]
    public void Cursor_ContinuesOnLeaseAndRejectsStaleMismatchAndTampering()
    {
        using var db = Open();
        Publish(db, "a", "alpha", expectedRevision: 0);
        using DatabaseGenerationQueryLease leaseA = db.Generations.AcquireActive("workspace");
        string cursor = leaseA.CreateCursor("code-search:v1", Encoding.UTF8.GetBytes("after:item-10"));

        Publish(db, "b", "bravo", expectedRevision: 1);

        Assert.Equal("after:item-10", Encoding.UTF8.GetString(leaseA.ReadCursor(cursor, "code-search:v1")));
        DatabaseGenerationException mismatch = Assert.Throws<DatabaseGenerationException>(() =>
            leaseA.ReadCursor(cursor, "symbol-get:v1"));
        Assert.Equal(DatabaseGenerationErrorCodes.CursorMismatch, mismatch.Code);

        using DatabaseGenerationQueryLease leaseB = db.Generations.AcquireActive("workspace");
        DatabaseGenerationException stale = Assert.Throws<DatabaseGenerationException>(() =>
            leaseB.ReadCursor(cursor, "code-search:v1"));
        Assert.Equal(DatabaseGenerationErrorCodes.CursorStale, stale.Code);

        string tampered = cursor[..^1] + (cursor[^1] == 'A' ? 'B' : 'A');
        DatabaseGenerationException invalid = Assert.Throws<DatabaseGenerationException>(() =>
            leaseA.ReadCursor(tampered, "code-search:v1"));
        Assert.Equal(DatabaseGenerationErrorCodes.CursorInvalid, invalid.Code);
    }

    [Fact]
    public void QueryLease_CancellationAndExceptionPathsReleaseRetiredGenerations()
    {
        using var db = Open();
        Publish(db, "a", "alpha", expectedRevision: 0);
        DatabaseGenerationQueryLease leaseA = db.Generations.AcquireActive("workspace");
        Publish(db, "b", "bravo", expectedRevision: 1);

        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try
            {
                Assert.Throws<OperationCanceledException>((Action)(() =>
                    cancellation.Token.ThrowIfCancellationRequested()));
            }
            finally
            {
                leaseA.Dispose();
            }
        }
        Assert.Equal([1L], db.Generations.CleanupRetired("workspace").RemovedRevisions);

        DatabaseGenerationQueryLease leaseB = db.Generations.AcquireActive("workspace");
        Publish(db, "c", "charlie", expectedRevision: 2);
        try
        {
            Assert.Throws<InvalidOperationException>(
                (Action)(static () => throw new InvalidOperationException("query failed")));
        }
        finally
        {
            leaseB.Dispose();
        }
        Assert.Equal([2L], db.Generations.CleanupRetired("workspace").RemovedRevisions);
        AssertGenerationQuery(db, "c", "charlie", expectedRevision: 3);
    }

    [Fact]
    public void Publish_RevisionAndResourceConflictsHaveStableCodes()
    {
        using var db = Open();
        Publish(db, "a", "alpha", expectedRevision: 0);
        Stage(db, "b", "bravo");

        DatabaseGenerationException revision = Assert.Throws<DatabaseGenerationException>(() =>
            db.Generations.Publish(Request("b", expectedRevision: 0)));
        Assert.Equal(DatabaseGenerationErrorCodes.RevisionConflict, revision.Code);

        var resourceReuse = new DatabaseGenerationPublishRequest
        {
            Stream = "workspace",
            GenerationId = "reuse",
            ExpectedRevision = 1,
            Resources =
            [
                new DatabaseGenerationResource("state", DatabaseGenerationResourceKind.KvKeyspace, "kv_a"),
            ],
        };
        DatabaseGenerationException resource = Assert.Throws<DatabaseGenerationException>(() =>
            db.Generations.Publish(resourceReuse));
        Assert.Equal(DatabaseGenerationErrorCodes.ResourceConflict, resource.Code);

        var missing = new DatabaseGenerationPublishRequest
        {
            Stream = "workspace",
            GenerationId = "missing",
            ExpectedRevision = 1,
            Resources =
            [
                new DatabaseGenerationResource("state", DatabaseGenerationResourceKind.KvKeyspace, "missing"),
            ],
        };
        DatabaseGenerationException invalid = Assert.Throws<DatabaseGenerationException>(() =>
            db.Generations.Publish(missing));
        Assert.Equal(DatabaseGenerationErrorCodes.ResourceInvalid, invalid.Code);

        var incompleteDocument = new DatabaseGenerationPublishRequest
        {
            Stream = "workspace",
            GenerationId = "incomplete-document",
            ExpectedRevision = 1,
            Resources =
            [
                new DatabaseGenerationResource(
                    "documents",
                    DatabaseGenerationResourceKind.DocumentCollection,
                    "docs_b"),
            ],
        };
        DatabaseGenerationException incomplete = Assert.Throws<DatabaseGenerationException>(() =>
            db.Generations.Publish(incompleteDocument));
        Assert.Equal(DatabaseGenerationErrorCodes.ResourceInvalid, incomplete.Code);
    }

    [Fact]
    public void BackupRestore_PreservesActiveGenerationAndAllOwnedModels()
    {
        string backupDirectory = Path.Combine(_testRoot, "backup");
        string restoredDirectory = Path.Combine(_testRoot, "restored");
        using (var db = Open())
        {
            Publish(db, "a", "alpha", expectedRevision: 0);
            Publish(db, "b", "bravo", expectedRevision: 1);
            _ = new BackupService().Create(db, new BackupCreateOptions
            {
                DestinationDirectory = backupDirectory,
            });
        }

        _ = new BackupService().Restore(new BackupRestoreOptions
        {
            BackupDirectory = backupDirectory,
            TargetDirectory = restoredDirectory,
        });

        using Tsdb restored = OpenAt(restoredDirectory);
        AssertGenerationQuery(restored, "b", "bravo", expectedRevision: 2);
        Assert.Equal([1L, 2L], restored.Generations.List("workspace").Select(static item => item.Revision));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testRoot))
                Directory.Delete(_testRoot, recursive: true);
        }
        catch
        {
            // Test cleanup must not hide the assertion result.
        }
    }

    private Tsdb Open()
        => OpenAt(_root);

    private static Tsdb OpenAt(string root)
        => Tsdb.Open(new TsdbOptions
        {
            RootDirectory = root,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
            Kv = KvOptions.Default with
            {
                AutoCheckpointEnabled = false,
                ExpirerEnabled = false,
                CleanupEnabled = false,
            },
        });

    private static DatabaseGeneration Publish(
        Tsdb db,
        string suffix,
        string token,
        long expectedRevision)
    {
        Stage(db, suffix, token);
        return db.Generations.Publish(Request(suffix, expectedRevision));
    }

    private static void Stage(Tsdb db, string suffix, string token)
    {
        KvKeyspace keyspace = db.Keyspaces.Open("kv_" + suffix);
        keyspace.Put("revision", Encoding.UTF8.GetBytes(token));

        string collectionName = "docs_" + suffix;
        db.Documents.Create(DocumentCollectionSchema.Create(collectionName));
        db.Documents.CreateFullTextIndex(
            collectionName,
            new DocumentFullTextIndexDefinition("ft_body", ["$.body"]));
        db.Documents.Open(collectionName).Upsert(
            "item-" + suffix,
            $$"""{"revision":"{{suffix}}","body":"{{token}}"}""");
    }

    private static DatabaseGenerationPublishRequest Request(string suffix, long expectedRevision)
        => new()
        {
            Stream = "workspace",
            GenerationId = "generation-" + suffix,
            ExpectedRevision = expectedRevision,
            Resources =
            [
                new DatabaseGenerationResource("state", DatabaseGenerationResourceKind.KvKeyspace, "kv_" + suffix),
                new DatabaseGenerationResource("documents", DatabaseGenerationResourceKind.DocumentCollection, "docs_" + suffix),
                new DatabaseGenerationResource(
                    "search",
                    DatabaseGenerationResourceKind.DocumentFullTextIndex,
                    "ft_body",
                    "docs_" + suffix),
            ],
        };

    private static void AssertGenerationQuery(
        Tsdb db,
        string suffix,
        string token,
        long expectedRevision)
    {
        using DatabaseGenerationQueryLease lease = db.Generations.AcquireActive("workspace");
        Assert.Equal(expectedRevision, lease.Generation.Revision);
        Assert.Equal("generation-" + suffix, lease.Generation.GenerationId);

        DatabaseGenerationResource kvResource = lease.GetRequiredResource(
            "state",
            DatabaseGenerationResourceKind.KvKeyspace);
        Assert.Equal(token, Encoding.UTF8.GetString(db.Keyspaces.Open(kvResource.Name).Get("revision")!));

        DatabaseGenerationResource documentResource = lease.GetRequiredResource(
            "documents",
            DatabaseGenerationResourceKind.DocumentCollection);
        DatabaseGenerationResource fullTextResource = lease.GetRequiredResource(
            "search",
            DatabaseGenerationResourceKind.DocumentFullTextIndex);
        DocumentCollectionStore store = db.Documents.Open(documentResource.Name);
        DocumentRow row = Assert.IsType<DocumentRow>(store.Get("item-" + suffix));
        Assert.Contains($"\"revision\":\"{suffix}\"", row.Json, StringComparison.Ordinal);
        DocumentFullTextIndex index = Assert.IsType<DocumentFullTextIndex>(
            store.Schema.TryGetFullTextIndex(fullTextResource.Name));
        Assert.Equal(
            ["item-" + suffix],
            store.SearchFullText(index, "$.body", token, 10)
                .Select(static hit => hit.DocumentId)
                .ToArray());
    }

    private sealed class InjectedGenerationFailureException : Exception;
}
