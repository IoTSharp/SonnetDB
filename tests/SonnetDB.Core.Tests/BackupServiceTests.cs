using System.Security.Cryptography;
using System.Text.Json;
using SonnetDB.Backup;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Graphs;
using SonnetDB.Graphs.Storage;
using SonnetDB.Model;
using SonnetDB.Sql.Execution;
using SonnetDB.Storage.Segments;
using Xunit;

namespace SonnetDB.Core.Tests.Backup;

public sealed class BackupServiceTests : IDisposable
{
    private readonly string _rootDirectory = Path.Combine(
        Path.GetTempPath(),
        $"SonnetDB.Backup.Tests.{Guid.NewGuid():N}");

    public BackupServiceTests()
    {
        Directory.CreateDirectory(_rootDirectory);
    }

    [Fact]
    public void RestoreDryRun_ForMissingTarget_ReportsEmptyTargetWithoutCreatingDirectory()
    {
        string backupDirectory = CreateBackupWithSingleFile("data/catalog.SDBCAT");
        string restoreTarget = Path.Combine(_rootDirectory, "restored");

        var result = new BackupService().RestoreDryRun(new BackupRestoreOptions
        {
            BackupDirectory = backupDirectory,
            TargetDirectory = restoreTarget,
        });

        Assert.True(result.IsValid);
        Assert.False(result.TargetDirectoryExists);
        Assert.True(result.TargetDirectoryEmpty);
        Assert.False(Directory.Exists(restoreTarget));
    }

    [Fact]
    public void Restore_WithMissingOptionalFile_SkipsOptionalEntry()
    {
        string backupDirectory = CreateBackupWithSingleFile("data/catalog.SDBCAT");
        BackupManifest manifest = new BackupService().ReadManifest(backupDirectory);
        string restoreTarget = Path.Combine(_rootDirectory, "restored-without-optional");
        WriteBackupManifest(backupDirectory, manifest with
        {
            Files =
            [
                .. manifest.Files,
                new BackupFileEntry(
                    "optional/not-present.bin",
                    17,
                    new string('0', 64),
                    BackupFileKind.Other,
                    Required: false),
            ],
        });

        BackupManifest restored = new BackupService().Restore(new BackupRestoreOptions
        {
            BackupDirectory = backupDirectory,
            TargetDirectory = restoreTarget,
        });

        Assert.Equal(2, restored.Files.Count);
        Assert.True(File.Exists(Path.Combine(restoreTarget, "data", "catalog.SDBCAT")));
        Assert.False(File.Exists(Path.Combine(restoreTarget, "optional", "not-present.bin")));
    }

    [Fact]
    public void RestoreDryRun_WithNoVerify_RejectsManifestPathTraversal()
    {
        string backupDirectory = CreateBackupWithSingleFile("../outside.SDBCAT");
        string restoreTarget = Path.Combine(_rootDirectory, "restored");

        var result = new BackupService().RestoreDryRun(new BackupRestoreOptions
        {
            BackupDirectory = backupDirectory,
            TargetDirectory = restoreTarget,
            VerifyBeforeRestore = false,
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("不安全路径", StringComparison.Ordinal));
        Assert.False(Directory.Exists(restoreTarget));
    }

    [Fact]
    public void Restore_RejectsManifestPathTraversalWithoutCopyingOutsideTarget()
    {
        string backupDirectory = CreateBackupWithSingleFile("../outside.SDBCAT");
        string restoreTarget = Path.Combine(_rootDirectory, "restored");

        var exception = Assert.Throws<InvalidDataException>(() => new BackupService().Restore(new BackupRestoreOptions
        {
            BackupDirectory = backupDirectory,
            TargetDirectory = restoreTarget,
            VerifyBeforeRestore = false,
        }));

        Assert.Contains("恢复预检失败", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_rootDirectory, "outside.SDBCAT")));
        Assert.False(Directory.Exists(restoreTarget));
    }

    [Fact]
    public void VerifyAndDryRun_WithNullCharacterInManifestPath_ReturnInvalidInsteadOfThrowing()
    {
        string backupDirectory = CreateBackupWithSingleFile("data/catalog.SDBCAT");
        BackupManifest manifest = new BackupService().ReadManifest(backupDirectory);
        WriteBackupManifest(backupDirectory, manifest with
        {
            Files = [manifest.Files[0] with { Path = "data/\0invalid.SDBCAT" }],
        });
        var service = new BackupService();

        BackupVerificationResult verification = service.Verify(backupDirectory);
        BackupRestoreDryRunResult dryRun = service.RestoreDryRun(new BackupRestoreOptions
        {
            BackupDirectory = backupDirectory,
            TargetDirectory = Path.Combine(_rootDirectory, "invalid-path-target"),
            VerifyBeforeRestore = false,
        });

        Assert.False(verification.IsValid);
        Assert.Contains(verification.Errors, static error =>
            error.Contains("无效路径", StringComparison.Ordinal));
        Assert.False(dryRun.IsValid);
        Assert.Contains(dryRun.Errors, static error =>
            error.Contains("无效路径", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_WithLayeredSegments_RecordsNestedSegmentPath()
    {
        string dbRoot = Path.Combine(_rootDirectory, "db");
        string backupDirectory = Path.Combine(_rootDirectory, "backup-layered");

        using (var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = dbRoot,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
            SegmentWriterOptions = new SegmentWriterOptions { FsyncOnCommit = false },
        }))
        {
            db.Write(Point.Create(
                "cpu",
                1000L,
                new Dictionary<string, string> { ["host"] = "a" },
                new Dictionary<string, FieldValue> { ["usage"] = FieldValue.FromDouble(42.0) }));
            db.FlushNow();

            var manifest = new BackupService().Create(db, new BackupCreateOptions
            {
                DestinationDirectory = backupDirectory,
            });

            var segment = Assert.Single(manifest.Files, static file => file.Kind == BackupFileKind.Segment);
            Assert.StartsWith("segments/v2/", segment.Path, StringComparison.Ordinal);
            Assert.EndsWith(".SDBSEG", segment.Path, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(backupDirectory, segment.Path.Replace('/', Path.DirectorySeparatorChar))));
        }
    }

    [Fact]
    public void CreateRestore_WithDocumentCollection_UsesOrderedKvSegmentAndRestoresIndexes()
    {
        string dbRoot = Path.Combine(_rootDirectory, "db-documents");
        string backupDirectory = Path.Combine(_rootDirectory, "backup-documents");
        string restoreRoot = Path.Combine(_rootDirectory, "restored-documents");

        using (var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = dbRoot,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
        }))
        {
            db.Documents.Create(DocumentCollectionSchema.Create("devices"));
            db.Documents.CreateIndex(
                "devices",
                new DocumentPathIndexDefinition("idx_type", ["$.type"]));
            var store = db.Documents.Open("devices");
            store.Insert("b", """{"type":"sensor","site":"west"}""");
            store.Insert("a", """{"type":"sensor","site":"east"}""");
            store.Insert("c", """{"type":"gateway","site":"west"}""");

            var manifest = new BackupService().Create(db, new BackupCreateOptions
            {
                DestinationDirectory = backupDirectory,
            });

            Assert.Contains(manifest.Files, static file =>
                file.Kind == BackupFileKind.Document &&
                file.Path.Contains("documents/collections/", StringComparison.Ordinal) &&
                file.Path.EndsWith(".SDBKVSEG", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(manifest.Files, static file =>
                Path.GetFileName(file.Path) == "keyspace.lock");
        }

        new BackupService().Restore(new BackupRestoreOptions
        {
            BackupDirectory = backupDirectory,
            TargetDirectory = restoreRoot,
        });

        using var restored = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = restoreRoot,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
        });
        var restoredStore = restored.Documents.Open("devices");
        var rows = restoredStore.Scan();

        Assert.Equal(["a", "b", "c"], rows.Select(static row => row.Id).ToArray());
        Assert.Equal(["a", "b"], restoredStore.GetByIndex(
                restoredStore.Schema.TryGetIndex("idx_type")!,
                "sensor")
            .Select(static row => row.Id)
            .ToArray());
    }

    [Fact]
    public void CreateRestore_WithGraph_RecordsV2SummaryAndReopensRestoredStore()
    {
        string dbRoot = Path.Combine(_rootDirectory, "db-graph");
        string backupDirectory = Path.Combine(_rootDirectory, "backup-graph");
        string restoreRoot = Path.Combine(_rootDirectory, "restored-graph");
        Guid storageId;

        using (var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = dbRoot,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
        }))
        {
            GraphStore store = db.Graphs.Create("social");
            storageId = store.StorageId;
            WriteGraphFixture(store);

            BackupManifest manifest = new BackupService().Create(db, new BackupCreateOptions
            {
                DestinationDirectory = backupDirectory,
            });

            Assert.Equal(BackupManifest.CurrentFormatVersion, manifest.FormatVersion);
            BackupGraphCatalogEntry graphCatalog = Assert.IsType<BackupGraphCatalogEntry>(
                manifest.Models.GraphCatalog);
            BackupGraphEntry graph = Assert.Single(graphCatalog.Graphs);
            Assert.Equal("social", graph.Name);
            Assert.Equal(storageId, graph.StorageId);
            Assert.True(graph.CheckpointedDuringBackup);
            Assert.All(graph.Indexes, static index =>
            {
                Assert.True(index.Included);
                Assert.False(index.Rebuildable);
            });
            Assert.Contains(manifest.Files, static file =>
                file.Kind == BackupFileKind.GraphCatalog
                && file.Path == "graphs/graphs.sdbgraph");
            Assert.Contains(manifest.Files, static file =>
                file.Kind == BackupFileKind.GraphData
                && file.Path.EndsWith("/store.sdbgraph", StringComparison.Ordinal));
            Assert.Contains(manifest.Indexes, static index =>
                index.Model == "graph"
                && index.Owner == "social"
                && !index.Rebuildable);
        }

        BackupVerificationResult verification = new BackupService().Verify(backupDirectory);
        Assert.True(verification.IsValid, string.Join(Environment.NewLine, verification.Errors));

        new BackupService().Restore(new BackupRestoreOptions
        {
            BackupDirectory = backupDirectory,
            TargetDirectory = restoreRoot,
        });

        using var restored = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = restoreRoot,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
        });
        GraphStore restoredStore = restored.Graphs.Open("social");
        Assert.Equal(storageId, restoredStore.StorageId);
        GraphInvariantReport report = GraphInvariantChecker.Check(restoredStore);
        Assert.True(report.IsValid, string.Join(
            Environment.NewLine,
            report.Issues.Select(static issue => issue.Message)));
        Assert.Equal(1, report.EdgeCount);
    }

    [Fact]
    public void CreateRestore_WithIncompleteGraphMaintenance_PreservesAndResumesSidecar()
    {
        string dbRoot = Path.Combine(_rootDirectory, "db-graph-maintenance");
        string backupDirectory = Path.Combine(_rootDirectory, "backup-graph-maintenance");
        string restoreRoot = Path.Combine(_rootDirectory, "restored-graph-maintenance");
        Guid operationId;
        Guid storageId;
        var unique = new GraphUniqueIndexDefinition(GraphElementType.Vertex, new LabelId(1), 7);

        using (var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = dbRoot,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
        }))
        {
            GraphStore store = db.Graphs.Create("repairable");
            storageId = store.StorageId;
            GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
            transaction.UpsertVertex(
                new GraphElementId(1),
                0,
                [new LabelId(1)],
                [new GraphProperty(7, GraphPropertyValue.FromString("alpha"))],
                uniquePropertyIds: [7]);
            transaction.Commit();
            Assert.True(store.Keyspace.Delete(GraphKeyCodec.EncodeUniqueProperty(
                GraphElementKind.Vertex,
                new LabelId(1),
                7,
                GraphPropertyValue.FromString("alpha"))));

            GraphMaintenanceResult first = store.RunMaintenance(new GraphMaintenanceOptions
            {
                UniqueIndexes = [unique],
                PageSize = 1,
                MaxWorkUnits = 1,
                CheckpointEveryWorkUnits = 0,
            });
            Assert.False(first.IsComplete);
            operationId = first.OperationId;

            BackupManifest manifest = new BackupService().Create(db, new BackupCreateOptions
            {
                DestinationDirectory = backupDirectory,
            });
            Assert.Contains(manifest.Files, file =>
                file.Kind == BackupFileKind.GraphData
                && file.Path.EndsWith("/maintenance.sdbgraph", StringComparison.Ordinal));
        }

        new BackupService().Restore(new BackupRestoreOptions
        {
            BackupDirectory = backupDirectory,
            TargetDirectory = restoreRoot,
        });
        using var restored = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = restoreRoot,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
        });
        GraphStore restoredStore = restored.Graphs.Open("repairable");
        GraphMaintenanceResult? result = null;
        for (int attempt = 0; attempt < 100 && (result is null || !result.IsComplete); attempt++)
        {
            result = restoredStore.RunMaintenance(new GraphMaintenanceOptions
            {
                MaxWorkUnits = 16,
                CheckpointEveryWorkUnits = 0,
            });
        }

        Assert.NotNull(result);
        Assert.True(result.IsComplete);
        Assert.True(result.WasResumed);
        Assert.Equal(operationId, result.OperationId);
        Assert.False(File.Exists(Path.Combine(
            restoreRoot,
            "graphs",
            "stores",
            storageId.ToString("N"),
            GraphMaintenanceManifestCodec.FileName)));
        Assert.True(GraphInvariantChecker.Check(restoredStore).IsValid);

        GraphTransaction conflict = restoredStore.BeginTransaction(Guid.NewGuid());
        conflict.UpsertVertex(
            new GraphElementId(2),
            0,
            [new LabelId(1)],
            [new GraphProperty(7, GraphPropertyValue.FromString("alpha"))],
            uniquePropertyIds: [7]);
        Assert.Throws<GraphUniqueConstraintException>(() => conflict.Commit());
    }

    [Fact]
    public void CreateRestore_WithPropertyGraph_RecordsMappingSummaryAndReopensCatalog()
    {
        string dbRoot = Path.Combine(_rootDirectory, "db-property-graph");
        string backupDirectory = Path.Combine(_rootDirectory, "backup-property-graph");
        string restoreRoot = Path.Combine(_rootDirectory, "restored-property-graph");
        using (var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = dbRoot,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
        }))
        {
            _ = SqlExecutor.Execute(db, "CREATE TABLE person (id INT NOT NULL, name STRING, PRIMARY KEY (id))");
            _ = SqlExecutor.Execute(db, """
                CREATE TABLE follows (
                    id INT NOT NULL,
                    source_id INT NOT NULL,
                    target_id INT NOT NULL,
                    PRIMARY KEY (id),
                    FOREIGN KEY (source_id) REFERENCES person (id),
                    FOREIGN KEY (target_id) REFERENCES person (id)
                )
                """);
            _ = SqlExecutor.Execute(db, "CREATE INDEX ix_follows_source ON follows (source_id)");
            _ = SqlExecutor.Execute(db, "INSERT INTO person (id, name) VALUES (1, 'Ada'), (2, 'Lin')");
            _ = SqlExecutor.Execute(db, "INSERT INTO follows (id, source_id, target_id) VALUES (10, 1, 2)");
            _ = SqlExecutor.Execute(db, """
                CREATE PROPERTY GRAPH social
                VERTEX TABLES (person KEY (id) LABEL person PROPERTIES (id, name))
                EDGE TABLES (
                    follows KEY (id)
                        SOURCE KEY (source_id) REFERENCES person (id)
                        DESTINATION KEY (target_id) REFERENCES person (id)
                        LABEL follows PROPERTIES (id)
                )
                """);

            BackupManifest manifest = new BackupService().Create(db, new BackupCreateOptions
            {
                DestinationDirectory = backupDirectory,
            });

            BackupPropertyGraphCatalogEntry catalog = Assert.IsType<BackupPropertyGraphCatalogEntry>(
                manifest.Models.PropertyGraphCatalog);
            BackupPropertyGraphEntry graph = Assert.Single(catalog.Graphs);
            Assert.Equal("social", graph.Name);
            Assert.Equal("person", Assert.Single(graph.VertexTables).TableName);
            Assert.Equal("follows", Assert.Single(graph.EdgeTables).TableName);
            Assert.Contains(manifest.Files, static file =>
                file.Kind == BackupFileKind.PropertyGraphCatalog
                && file.Path == "graphs/property-graphs.sdbpgq"
                && file.Required);
        }

        BackupVerificationResult verification = new BackupService().Verify(backupDirectory);
        Assert.True(verification.IsValid, string.Join(Environment.NewLine, verification.Errors));
        _ = new BackupService().Restore(new BackupRestoreOptions
        {
            BackupDirectory = backupDirectory,
            TargetDirectory = restoreRoot,
        });

        using var restored = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = restoreRoot,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
        });
        Assert.NotNull(restored.Graphs.PropertyGraphs.TryGet("social"));
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(restored, """
            SELECT source_id, target_id FROM GRAPH_TABLE (
                social
                MATCH (a IS person)-[e IS follows]->(b IS person)
                COLUMNS (a.id AS source_id, b.id AS target_id)
            )
            """));
        Assert.Equal(new object?[] { 1L, 2L }, Assert.Single(result.Rows));

        var service = new BackupService();
        BackupManifest storedManifest = service.ReadManifest(backupDirectory);
        BackupPropertyGraphCatalogEntry storedCatalog = Assert.IsType<BackupPropertyGraphCatalogEntry>(
            storedManifest.Models.PropertyGraphCatalog);
        BackupPropertyGraphEntry storedGraph = Assert.Single(storedCatalog.Graphs);
        BackupPropertyGraphVertexTableEntry storedVertex = Assert.Single(storedGraph.VertexTables);
        WriteBackupManifest(backupDirectory, storedManifest with
        {
            Models = storedManifest.Models with
            {
                PropertyGraphCatalog = storedCatalog with
                {
                    Graphs =
                    [
                        storedGraph with
                        {
                            VertexTables = [storedVertex with { Label = "tampered_person" }],
                        },
                    ],
                },
            },
        });

        BackupVerificationResult tamperedVerification = service.Verify(backupDirectory);
        Assert.False(tamperedVerification.IsValid);
        Assert.Contains(tamperedVerification.Errors, static error =>
            error.Contains("restored mapping does not match", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Create_DuringGraphCommit_BlocksPublishAndRestoresCheckpointState()
    {
        string dbRoot = Path.Combine(_rootDirectory, "db-graph-concurrent-backup");
        string backupDirectory = Path.Combine(_rootDirectory, "backup-graph-concurrent");
        string restoreRoot = Path.Combine(_rootDirectory, "restored-graph-concurrent");
        using var database = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = dbRoot,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
        });
        GraphStore store = database.Graphs.Create("concurrent");
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        transaction.UpsertVertex(
            new GraphElementId(1),
            expectedElementVersion: 0,
            [new LabelId(1)],
            [new GraphProperty(1, GraphPropertyValue.FromString("after-backup"))]);

        using var graphCatalogCopied = new ManualResetEventSlim();
        using var releaseFileCopy = new ManualResetEventSlim();
        using var commitAtGate = new ManualResetEventSlim();
        var backupService = new BackupService();
        backupService.AfterFileCopiedTestHook = relativePath =>
        {
            if (!string.Equals(relativePath, "graphs/graphs.sdbgraph", StringComparison.Ordinal))
                return;
            graphCatalogCopied.Set();
            if (!releaseFileCopy.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("测试未能按时恢复 Graph 备份文件复制。");
        };
        store.BeforeTransactionConditionalCommitTestHook = commitAtGate.Set;

        Task<BackupManifest> backupTask = Task.Run(() => backupService.Create(
            database,
            new BackupCreateOptions { DestinationDirectory = backupDirectory }));
        Task<GraphCommitResult>? commitTask = null;
        try
        {
            Assert.True(
                graphCatalogCopied.Wait(TimeSpan.FromSeconds(5)),
                "Graph 备份未到达 catalog 文件复制同步点。");
            commitTask = Task.Run(() => transaction.Commit());
            Assert.True(
                commitAtGate.Wait(TimeSpan.FromSeconds(5)),
                "Graph commit 未到达备份/提交门。");
            Task firstCompleted = await Task.WhenAny(
                commitTask,
                Task.Delay(TimeSpan.FromMilliseconds(250)));
            Assert.NotSame(commitTask, firstCompleted);
        }
        finally
        {
            store.BeforeTransactionConditionalCommitTestHook = null;
            backupService.AfterFileCopiedTestHook = null;
            releaseFileCopy.Set();
        }

        _ = await backupTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(commitTask);
        _ = await commitTask.WaitAsync(TimeSpan.FromSeconds(10));
        BackupVerificationResult verification = backupService.Verify(backupDirectory);
        Assert.True(verification.IsValid, string.Join(Environment.NewLine, verification.Errors));

        _ = backupService.Restore(new BackupRestoreOptions
        {
            BackupDirectory = backupDirectory,
            TargetDirectory = restoreRoot,
        });
        Assert.Empty(Directory.EnumerateFiles(
            restoreRoot,
            "keyspace.lock",
            SearchOption.AllDirectories));
        using var restored = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = restoreRoot,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
        });
        GraphInvariantReport report = GraphInvariantChecker.Check(restored.Graphs.Open("concurrent"));
        Assert.True(report.IsValid, string.Join(
            Environment.NewLine,
            report.Issues.Select(static issue => issue.Message)));
        Assert.Equal(0, report.VertexCount);
        Assert.Equal(0, report.EdgeCount);
    }

    [Fact]
    public void VerifyRestore_WithHashValidGraphOrphan_RejectsInvariantAfterReopen()
    {
        string dbRoot = Path.Combine(_rootDirectory, "db-graph-orphan");
        string backupDirectory = Path.Combine(_rootDirectory, "backup-graph-orphan");
        string restoreRoot = Path.Combine(_rootDirectory, "restored-graph-orphan");

        using (var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = dbRoot,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
        }))
        {
            GraphStore store = db.Graphs.Create("broken");
            WriteGraphFixture(store);
            Assert.True(store.Keyspace.Delete(GraphKeyCodec.EncodeIncomingAdjacency(
                new GraphElementId(2),
                new LabelId(3),
                new GraphElementId(1),
                new GraphElementId(10))));

            _ = new BackupService().Create(db, new BackupCreateOptions
            {
                DestinationDirectory = backupDirectory,
            });
        }

        BackupVerificationResult verification = new BackupService().Verify(backupDirectory);
        Assert.False(verification.IsValid);
        Assert.Contains(verification.Errors, static error =>
            error.Contains(nameof(GraphInvariantIssueKind.MissingIncomingAdjacency), StringComparison.Ordinal));

        Directory.CreateDirectory(restoreRoot);
        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            new BackupService().Restore(new BackupRestoreOptions
            {
                BackupDirectory = backupDirectory,
                TargetDirectory = restoreRoot,
                Overwrite = true,
                VerifyBeforeRestore = false,
            }));
        Assert.Contains("reopen/invariant", exception.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(restoreRoot));
        Assert.Empty(Directory.EnumerateFileSystemEntries(restoreRoot));
        Assert.DoesNotContain(
            Directory.EnumerateDirectories(_rootDirectory),
            path => Path.GetFileName(path).StartsWith(
                Path.GetFileName(restoreRoot) + ".restore-",
                StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_V1ManifestWithoutGraphCatalog_RemainsReadable()
    {
        string backupDirectory = CreateBackupWithSingleFile(
            "data/catalog.SDBCAT",
            formatVersion: 1);
        string json = File.ReadAllText(Path.Combine(backupDirectory, BackupManifest.FileName));

        Assert.DoesNotContain("graphCatalog", json, StringComparison.Ordinal);
        BackupVerificationResult verification = new BackupService().Verify(backupDirectory);
        Assert.True(verification.IsValid, string.Join(Environment.NewLine, verification.Errors));

        BackupManifest manifest = new BackupService().ReadManifest(backupDirectory);
        Assert.Equal(1, manifest.FormatVersion);
        Assert.Null(manifest.Models.GraphCatalog);
    }

    [Fact]
    public void VerifyAndDryRun_V1ManifestWithNullOrAliasedPath_ReturnInvalidInsteadOfBypassingGraphGuard()
    {
        string backupDirectory = CreateBackupWithSingleFile(
            "data/catalog.SDBCAT",
            formatVersion: 1);
        BackupManifest manifest = new BackupService().ReadManifest(backupDirectory);
        string restoreTarget = Path.Combine(_rootDirectory, "restored-v1-aliased-graph");
        BackupFileEntry original = manifest.Files[0];
        BackupFileEntry[] invalidEntries =
        [
            original with { Path = null! },
            original with { Path = "./graphs/graphs.sdbgraph", Kind = BackupFileKind.Other },
            original with { Path = "graphs./graphs.sdbgraph", Kind = BackupFileKind.Other },
        ];

        foreach (BackupFileEntry invalid in invalidEntries)
        {
            WriteBackupManifest(backupDirectory, manifest with { Files = [invalid] });

            BackupVerificationResult verification = new BackupService().Verify(backupDirectory);
            BackupRestoreDryRunResult dryRun = new BackupService().RestoreDryRun(new BackupRestoreOptions
            {
                BackupDirectory = backupDirectory,
                TargetDirectory = restoreTarget,
                VerifyBeforeRestore = false,
            });

            Assert.False(verification.IsValid);
            Assert.NotEmpty(verification.Errors);
            Assert.False(dryRun.IsValid);
            Assert.NotEmpty(dryRun.Errors);
            Assert.False(Directory.Exists(restoreTarget));
        }
    }

    [Fact]
    public void Verify_V2GraphBackupDowngradedToV1_IsRejected()
    {
        string dbRoot = Path.Combine(_rootDirectory, "db-graph-downgrade");
        string backupDirectory = Path.Combine(_rootDirectory, "backup-graph-downgrade");
        BackupManifest manifest;
        using (var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = dbRoot,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
        }))
        {
            _ = db.Graphs.Create("downgrade");
            manifest = new BackupService().Create(db, new BackupCreateOptions
            {
                DestinationDirectory = backupDirectory,
            });
        }

        BackupManifest downgraded = manifest with
        {
            FormatVersion = 1,
            Models = manifest.Models with { GraphCatalog = null },
            Files = manifest.Files
                .Select(static file => file.Kind is BackupFileKind.GraphCatalog or BackupFileKind.GraphData
                    ? file with { Kind = BackupFileKind.Other }
                    : file)
                .ToArray(),
            Indexes = manifest.Indexes
                .Where(static index => index.Model != "graph")
                .ToArray(),
        };
        WriteBackupManifest(backupDirectory, downgraded);

        BackupVerificationResult verification = new BackupService().Verify(backupDirectory);

        Assert.False(verification.IsValid);
        Assert.Contains(verification.Errors, static error =>
            error.Contains("Graph backups require manifest format version 2", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_AfterDroppedGraphLeavesOrphanStore_ExcludesOrphanFiles()
    {
        string dbRoot = Path.Combine(_rootDirectory, "db-graph-orphan-store");
        string backupDirectory = Path.Combine(_rootDirectory, "backup-graph-orphan-store");
        BackupManifest manifest;
        Guid storageId;
        using (var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = dbRoot,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
        }))
        {
            GraphStore store = db.Graphs.Create("dropped");
            storageId = store.StorageId;
            store.Dispose();
            Assert.True(db.Graphs.Drop("dropped"));
            string orphanStore = TsdbPaths.GraphStoreDir(dbRoot, storageId);
            Directory.CreateDirectory(orphanStore);
            File.WriteAllText(Path.Combine(orphanStore, "orphan.bin"), "orphan");

            manifest = new BackupService().Create(db, new BackupCreateOptions
            {
                DestinationDirectory = backupDirectory,
            });
        }

        string storageSegment = storageId.ToString("N");
        Assert.DoesNotContain(manifest.Files, file =>
            file.Path.Contains(storageSegment, StringComparison.OrdinalIgnoreCase));
        BackupVerificationResult verification = new BackupService().Verify(backupDirectory);
        Assert.True(verification.IsValid, string.Join(Environment.NewLine, verification.Errors));
    }

    [Fact]
    public void Verify_FutureManifestVersion_IsRejectedWithoutChangingLegacyEnumValues()
    {
        string backupDirectory = CreateBackupWithSingleFile(
            "data/catalog.SDBCAT",
            formatVersion: BackupManifest.CurrentFormatVersion + 1);

        BackupVerificationResult verification = new BackupService().Verify(backupDirectory);

        Assert.False(verification.IsValid);
        Assert.Contains(verification.Errors, static error =>
            error.Contains("Unsupported manifest format version", StringComparison.Ordinal));
        Assert.Equal(11, (int)BackupFileKind.Other);
        Assert.Equal(12, (int)BackupFileKind.GraphCatalog);
        Assert.Equal(13, (int)BackupFileKind.GraphData);
        Assert.Equal(14, (int)BackupFileKind.PropertyGraphCatalog);
    }

    [Fact]
    public void Verify_UnsupportedDatabaseFormat_IsRejected()
    {
        string backupDirectory = CreateBackupWithSingleFile("data/catalog.SDBCAT");
        BackupManifest manifest = new BackupService().ReadManifest(backupDirectory);
        WriteBackupManifest(backupDirectory, manifest with { DatabaseFormat = "OtherDB/v999" });

        BackupVerificationResult verification = new BackupService().Verify(backupDirectory);

        Assert.False(verification.IsValid);
        Assert.Contains(verification.Errors, static error =>
            error.Contains("Unsupported database format", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_V2ManifestWithoutGraphCatalog_IsRejected()
    {
        string backupDirectory = CreateBackupWithSingleFile(
            "data/catalog.SDBCAT",
            formatVersion: 2,
            includeGraphCatalog: false);

        BackupVerificationResult verification = new BackupService().Verify(backupDirectory);

        Assert.False(verification.IsValid);
        Assert.Contains(verification.Errors, static error =>
            error.Contains("models.graphCatalog", StringComparison.Ordinal));
    }

    [Fact]
    public void RestoreDryRun_WithNoVerifyAndMissingV2GraphCatalog_IsRejected()
    {
        string backupDirectory = CreateBackupWithSingleFile(
            "data/catalog.SDBCAT",
            formatVersion: 2,
            includeGraphCatalog: false);
        string restoreTarget = Path.Combine(_rootDirectory, "restored-missing-graph-summary");

        BackupRestoreDryRunResult result = new BackupService().RestoreDryRun(new BackupRestoreOptions
        {
            BackupDirectory = backupDirectory,
            TargetDirectory = restoreTarget,
            VerifyBeforeRestore = false,
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static error =>
            error.Contains("models.graphCatalog", StringComparison.Ordinal));
        Assert.False(Directory.Exists(restoreTarget));
    }

    [Theory]
    [InlineData("graphs/graphs.sdbgraph", BackupFileKind.Other)]
    [InlineData("data/catalog.SDBCAT", BackupFileKind.GraphData)]
    public void RestoreDryRun_WithNoVerifyAndMismatchedGraphClassification_IsRejected(
        string manifestPath,
        BackupFileKind kind)
    {
        string backupDirectory = CreateBackupWithSingleFile(
            manifestPath,
            fileKind: kind);
        string restoreTarget = Path.Combine(_rootDirectory, "restored-classification");

        BackupRestoreDryRunResult result = new BackupService().RestoreDryRun(new BackupRestoreOptions
        {
            BackupDirectory = backupDirectory,
            TargetDirectory = restoreTarget,
            VerifyBeforeRestore = false,
        });

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, static error =>
            error.Contains("classification", StringComparison.Ordinal));
        Assert.False(Directory.Exists(restoreTarget));
    }

    [Fact]
    public void Verify_GraphPathsRelabeledAsOther_CannotEscapeGraphVerification()
    {
        string dbRoot = Path.Combine(_rootDirectory, "db-graph-reclassified");
        string backupDirectory = Path.Combine(_rootDirectory, "backup-graph-reclassified");
        BackupManifest manifest;
        using (var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = dbRoot,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
        }))
        {
            GraphStore store = db.Graphs.Create("hidden");
            WriteGraphFixture(store);
            manifest = new BackupService().Create(db, new BackupCreateOptions
            {
                DestinationDirectory = backupDirectory,
            });
        }

        BackupManifest tampered = manifest with
        {
            Models = manifest.Models with
            {
                GraphCatalog = new BackupGraphCatalogEntry(0, []),
            },
            Files = manifest.Files
                .Select(static file => file.Kind is BackupFileKind.GraphCatalog or BackupFileKind.GraphData
                    ? file with { Kind = BackupFileKind.Other }
                    : file)
                .ToArray(),
        };
        WriteBackupManifest(backupDirectory, tampered);

        BackupVerificationResult verification = new BackupService().Verify(backupDirectory);

        Assert.False(verification.IsValid);
        Assert.Contains(verification.Errors, static error =>
            error.Contains("classification", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_V2GraphCatalogWithNullGraph_ReturnsInvalidInsteadOfThrowing()
    {
        string backupDirectory = CreateBackupWithSingleFile("data/catalog.SDBCAT");
        BackupManifest manifest = new BackupService().ReadManifest(backupDirectory);
        BackupManifest tampered = manifest with
        {
            Models = manifest.Models with
            {
                GraphCatalog = new BackupGraphCatalogEntry(0, [null!]),
            },
        };
        WriteBackupManifest(backupDirectory, tampered);

        BackupVerificationResult verification = new BackupService().Verify(backupDirectory);

        Assert.False(verification.IsValid);
        Assert.Contains(verification.Errors, static error =>
            error.Contains("null graph entry", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_V2ManifestWithNullRequiredMembers_ReturnsInvalidInsteadOfThrowing()
    {
        string backupDirectory = CreateBackupWithSingleFile("data/catalog.SDBCAT");
        BackupManifest manifest = new BackupService().ReadManifest(backupDirectory);
        BackupManifest[] malformedManifests =
        [
            manifest with { Models = null! },
            manifest with { Models = manifest.Models with { Measurements = null! } },
            manifest with { Consistency = null! },
            manifest with { Consistency = manifest.Consistency with { TotalBytes = -1 } },
            manifest with { DatabaseFormat = null! },
            manifest with { Files = null! },
            manifest with { Files = [null!] },
            manifest with { Files = [manifest.Files[0] with { SizeBytes = -1 }] },
            manifest with { Files = [manifest.Files[0] with { Sha256 = "invalid" }] },
            manifest with { Indexes = null! },
            manifest with { Indexes = [null!] },
        ];

        foreach (BackupManifest malformed in malformedManifests)
        {
            WriteBackupManifest(backupDirectory, malformed);

            BackupVerificationResult verification = new BackupService().Verify(backupDirectory);

            Assert.False(verification.IsValid);
            Assert.NotEmpty(verification.Errors);
        }
    }

    [Fact]
    public void RestoreDryRun_WithOverflowingFileSizes_ReturnsInvalidAndSaturatedTotal()
    {
        string backupDirectory = CreateBackupWithSingleFile("data/catalog.SDBCAT");
        BackupManifest manifest = new BackupService().ReadManifest(backupDirectory);
        string digest = new('0', 64);
        WriteBackupManifest(backupDirectory, manifest with
        {
            Files =
            [
                new BackupFileEntry("optional-a", long.MaxValue, digest, BackupFileKind.Other, Required: false),
                new BackupFileEntry("optional-b", long.MaxValue, digest, BackupFileKind.Other, Required: false),
            ],
        });

        BackupRestoreDryRunResult dryRun = new BackupService().RestoreDryRun(new BackupRestoreOptions
        {
            BackupDirectory = backupDirectory,
            TargetDirectory = Path.Combine(_rootDirectory, "overflow-target"),
            VerifyBeforeRestore = false,
        });

        Assert.False(dryRun.IsValid);
        Assert.Equal(long.MaxValue, dryRun.TotalBytes);
        Assert.Contains(dryRun.Errors, static error =>
            error.Contains("exceed the supported Int64 total", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_GraphDataWithEmptySummaryAndOmittedCatalog_IsRejected()
    {
        string dbRoot = Path.Combine(_rootDirectory, "db-graph-unowned-data");
        string backupDirectory = Path.Combine(_rootDirectory, "backup-graph-unowned-data");
        BackupManifest manifest;
        using (var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = dbRoot,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
        }))
        {
            _ = db.Graphs.Create("unowned");
            manifest = new BackupService().Create(db, new BackupCreateOptions
            {
                DestinationDirectory = backupDirectory,
            });
        }

        BackupManifest tampered = manifest with
        {
            Models = manifest.Models with
            {
                GraphCatalog = new BackupGraphCatalogEntry(0, []),
            },
            Files = manifest.Files
                .Where(static file => file.Kind != BackupFileKind.GraphCatalog)
                .ToArray(),
            Indexes = manifest.Indexes
                .Where(static index => index.Model != "graph")
                .ToArray(),
        };
        WriteBackupManifest(backupDirectory, tampered);

        BackupVerificationResult verification = new BackupService().Verify(backupDirectory);

        Assert.False(verification.IsValid);
        Assert.Contains(verification.Errors, static error =>
            error.Contains("not owned by a graph", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_V2GraphIndexSummariesAreFrozenAndCrossChecked()
    {
        string dbRoot = Path.Combine(_rootDirectory, "db-graph-index-contract");
        string backupDirectory = Path.Combine(_rootDirectory, "backup-graph-index-contract");
        BackupManifest manifest;
        using (var db = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = dbRoot,
            BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
            Compaction = new CompactionPolicy { Enabled = false },
        }))
        {
            _ = db.Graphs.Create("contract");
            manifest = new BackupService().Create(db, new BackupCreateOptions
            {
                DestinationDirectory = backupDirectory,
            });
        }

        BackupGraphCatalogEntry catalog = Assert.IsType<BackupGraphCatalogEntry>(
            manifest.Models.GraphCatalog);
        BackupGraphEntry graph = Assert.Single(catalog.Graphs);
        BackupGraphIndexEntry[] perGraphIndexes = graph.Indexes
            .SkipLast(1)
            .Select((index, position) => position == 0
                ? index with { Included = false, Rebuildable = true }
                : index)
            .ToArray();
        BackupIndexEntry[] topLevelIndexes = manifest.Indexes
            .Where(static index => index.Model != "graph" || index.Kind != "edge-property")
            .Select(static index => index.Model == "graph" && index.Kind == "outgoing-adjacency"
                ? index with { Included = false, Rebuildable = true }
                : index)
            .Append(manifest.Indexes.First(static index => index.Model == "graph") with { Owner = null! })
            .ToArray();
        BackupManifest tampered = manifest with
        {
            Models = manifest.Models with
            {
                GraphCatalog = catalog with
                {
                    Graphs = [graph with { Indexes = perGraphIndexes }],
                },
            },
            Indexes = topLevelIndexes,
        };
        WriteBackupManifest(backupDirectory, tampered);

        BackupVerificationResult verification = new BackupService().Verify(backupDirectory);

        Assert.False(verification.IsValid);
        Assert.Contains(verification.Errors, static error =>
            error.Contains("must be included and non-rebuildable", StringComparison.Ordinal));
        Assert.Contains(verification.Errors, static error =>
            error.Contains("is missing index kind", StringComparison.Ordinal));
        Assert.Contains(verification.Errors, static error =>
            error.Contains("top-level Graph index", StringComparison.Ordinal)
            && error.Contains("invalid lifecycle", StringComparison.Ordinal));
        Assert.Contains(verification.Errors, static error =>
            error.Contains("missing top-level Graph index", StringComparison.Ordinal));
        Assert.Contains(verification.Errors, static error =>
            error.Contains("empty model, owner, name, or kind", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
            Directory.Delete(_rootDirectory, recursive: true);
    }

    private string CreateBackupWithSingleFile(
        string manifestPath,
        int? formatVersion = null,
        bool includeGraphCatalog = true,
        BackupFileKind fileKind = BackupFileKind.Catalog)
    {
        string backupDirectory = Path.Combine(_rootDirectory, "backup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backupDirectory);

        string hash = "not-checked-in-no-verify";
        if (!manifestPath.Contains("..", StringComparison.Ordinal))
        {
            string filePath = Path.Combine(backupDirectory, manifestPath);
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath, "catalog");
            hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(filePath))).ToLowerInvariant();
        }

        int effectiveFormatVersion = formatVersion ?? BackupManifest.CurrentFormatVersion;
        var models = new BackupModelSummary([], [], [], []);
        if (effectiveFormatVersion >= 2 && includeGraphCatalog)
            models = models with { GraphCatalog = new BackupGraphCatalogEntry(0, []) };

        var manifest = new BackupManifest(
            effectiveFormatVersion,
            "SonnetDB/MM9",
            DateTimeOffset.UtcNow,
            _rootDirectory,
            new BackupConsistency(0, 0, 0, 0),
            models,
            [new BackupFileEntry(manifestPath, 7, hash, fileKind, Required: true)],
            []);

        WriteBackupManifest(backupDirectory, manifest);
        return backupDirectory;
    }

    private static void WriteBackupManifest(string backupDirectory, BackupManifest manifest)
    {
        string json = JsonSerializer.Serialize(manifest, BackupJsonContext.Default.BackupManifest);
        File.WriteAllText(Path.Combine(backupDirectory, BackupManifest.FileName), json);
    }

    private static void WriteGraphFixture(GraphStore store)
    {
        GraphTransaction transaction = store.BeginTransaction(Guid.NewGuid());
        transaction.UpsertVertex(
            new GraphElementId(1),
            expectedElementVersion: 0,
            [new LabelId(1)],
            [new GraphProperty(1, GraphPropertyValue.FromString("source"))]);
        transaction.UpsertVertex(
            new GraphElementId(2),
            expectedElementVersion: 0,
            [new LabelId(2)],
            [new GraphProperty(2, GraphPropertyValue.FromString("target"))]);
        transaction.UpsertEdge(
            new GraphElementId(10),
            expectedElementVersion: 0,
            new GraphElementId(1),
            new GraphElementId(2),
            new LabelId(3),
            [new GraphProperty(3, GraphPropertyValue.FromString("calls"))]);
        _ = transaction.Commit();
    }
}
