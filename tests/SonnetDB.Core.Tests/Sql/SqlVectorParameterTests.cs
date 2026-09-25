using System.Data;
using System.Data.Common;
using System.Text.Json;
using SonnetDB.Backup;
using SonnetDB.Data;
using SonnetDB.Data.Internal;
using SonnetDB.Data.Remote;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Engine.Retention;
using SonnetDB.Kv;
using SonnetDB.Model;
using SonnetDB.Query;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>
/// GH-Issue #186：VECTOR 参数绑定和远程 JSON 数组解码的确定性合同。
/// </summary>
public sealed class SqlVectorParameterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-vector-param-" + Guid.NewGuid().ToString("N"));

    public SqlVectorParameterTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Embedded_VectorParameter_BindsAndRoundTripsFloatArray()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database, "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3))");

        object? insert = SqlExecutor.Execute(
            database,
            databaseName: null,
            "INSERT INTO docs (time, source, embedding) VALUES (1000, @source, @embedding)",
            new SqlParameters()
                .AddNamed("source", "a")
                .AddNamed("embedding", new float[] { 1.25f, -0.5f, 3f }));

        Assert.Equal(1, Assert.IsType<InsertExecutionResult>(insert).RowsInserted);

        var selected = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, "SELECT embedding FROM docs"));
        var vector = Assert.IsType<float[]>(Assert.Single(selected.Rows)[0]);
        Assert.Equal(new float[] { 1.25f, -0.5f, 3f }, vector);
    }

    [Fact]
    public void Embedded_VectorUpdate_ReplacesRawSqlAndKnnAndRecovers()
    {
        using (var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root }))
        {
            SqlExecutor.Execute(database, "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3))");
            SqlExecutor.Execute(database,
                "INSERT INTO docs (time, source, embedding) VALUES (1000, 'a', [1,0,0])");
            var updated = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(
                database,
                databaseName: null,
                "UPDATE docs SET embedding = @embedding WHERE source = 'a'",
                new SqlParameters().AddNamed("embedding", new float[] { 0f, 1f, 0f })));
            Assert.Equal(1, updated.RowsAffected);
            Assert.Equal(0, Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(
                database, "UPDATE docs SET embedding = [0,0,1] WHERE source = 'missing'"))
                .RowsAffected);
            AssertUpdated(database);
            database.FlushNow();
            AssertUpdated(database);
        }

        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        AssertUpdated(reopened);
    }

    private static void AssertUpdated(Tsdb database)
    {
        var seriesId = SeriesId.Compute(new SeriesKey("docs",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["source"] = "a" }));
        var point = Assert.Single(database.Query.Execute(new PointQuery(
            seriesId, "embedding", new TimeRange(1000, 1000))));
        Assert.Equal(new float[] { 0f, 1f, 0f }, point.Value.AsVector().ToArray());

        var selected = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            database, "SELECT embedding FROM docs WHERE source = 'a'"));
        Assert.Equal(new float[] { 0f, 1f, 0f }, Assert.IsType<float[]>(Assert.Single(selected.Rows)[0]));

        var knn = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            database, "SELECT embedding FROM knn(docs, embedding, [1,0,0], 1)"));
        Assert.Equal(new float[] { 0f, 1f, 0f }, Assert.IsType<float[]>(Assert.Single(knn.Rows)[0]));
    }

    [Fact]
    public void Embedded_VectorUpdate_MultipleRowsAndValidation_AreAtomic()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database, "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3))");
        SqlExecutor.Execute(database,
            "INSERT INTO docs (time, source, embedding) VALUES "
            + "(1000, 'a', [1,0,0]), (2000, 'a', [1,0,0]), (3000, 'b', [0,0,1])");

        Assert.Contains("维度不匹配", Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(
            database, "UPDATE docs SET embedding = [1,2] WHERE source = 'a'"))
            .Message, StringComparison.Ordinal);
        Assert.Contains("NULL", Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(
            database, "UPDATE docs SET embedding = NULL WHERE source = 'a'"))
            .Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => SqlExecutor.Execute(
            database, null, "UPDATE docs SET embedding = @embedding WHERE source = 'a'",
            new SqlParameters().AddNamed("embedding", new float[] { float.NaN, 0f, 0f })));
        Assert.Equal(0, database.VectorReplacements.Count);

        var updated = Assert.IsType<RowsAffectedExecutionResult>(SqlExecutor.Execute(
            database, null, "UPDATE docs SET embedding = @embedding WHERE source = 'a'",
            new SqlParameters().AddNamed("embedding", new float[] { 0f, 1f, 0f })));
        Assert.Equal(2, updated.RowsAffected);
        var rows = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            database, "SELECT time, source, embedding FROM docs ORDER BY time")).Rows;
        Assert.Equal(3, rows.Count);
        Assert.Equal(new float[] { 0f, 1f, 0f }, Assert.IsType<float[]>(rows[0][2]));
        Assert.Equal(new float[] { 0f, 1f, 0f }, Assert.IsType<float[]>(rows[1][2]));
        Assert.Equal(new float[] { 0f, 0f, 1f }, Assert.IsType<float[]>(rows[2][2]));

        Assert.Throws<NotSupportedException>(() => SqlExecutor.Execute(database,
            "INSERT INTO docs (time, source, embedding) VALUES (1000, 'a', [1,0,0])"));
        SqlExecutor.Execute(database, "DELETE FROM docs WHERE source = 'a' AND time = 1000");
        Assert.Equal(1, database.VectorReplacements.Count);
        var remaining = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            database, "SELECT time, embedding FROM docs WHERE source = 'a'"));
        Assert.Equal(2000L, Assert.Single(remaining.Rows)[0]);

        Assert.True(database.DropMeasurement("docs"));
        Assert.Equal(0, database.VectorReplacements.Count);
        SqlExecutor.Execute(database, "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3))");
        SqlExecutor.Execute(database,
            "INSERT INTO docs (time, source, embedding) VALUES (2000, 'a', [1,0,0])");
        var recreated = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database,
            "SELECT embedding FROM docs WHERE source = 'a'"));
        Assert.Equal(new float[] { 1f, 0f, 0f },
            Assert.IsType<float[]>(Assert.Single(recreated.Rows)[0]));
    }

    [Fact]
    public void Embedded_VectorUpdate_SparseTargetAndFieldPredicate_RejectBeforeCommit()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database,
            "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3), score FIELD FLOAT)");
        SqlExecutor.Execute(database,
            "INSERT INTO docs (time, source, embedding) VALUES (1000, 'a', [1,0,0])");
        SqlExecutor.Execute(database,
            "INSERT INTO docs (time, source, score) VALUES (2000, 'a', 1.0)");

        Assert.Contains("缺少原 VECTOR FIELD", Assert.Throws<NotSupportedException>(() =>
            SqlExecutor.Execute(database,
                "UPDATE docs SET embedding = [0,1,0] WHERE source = 'a'"))
            .Message, StringComparison.Ordinal);
        Assert.Contains("TAG 和 time", Assert.Throws<NotSupportedException>(() =>
            SqlExecutor.Execute(database,
                "UPDATE docs SET embedding = [0,1,0] WHERE score > 0"))
            .Message, StringComparison.Ordinal);
        Assert.Equal(0, database.VectorReplacements.Count);
        var selected = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database,
            "SELECT embedding FROM docs WHERE source = 'a' AND time = 1000"));
        Assert.Equal(new float[] { 1f, 0f, 0f },
            Assert.IsType<float[]>(Assert.Single(selected.Rows)[0]));
    }

    [Fact]
    public void Embedded_VectorUpdate_CompactionAndReopen_KeepOnlyReplacementVisible()
    {
        var baseOptions = new TsdbOptions
        {
            RootDirectory = _root,
            Compaction = new CompactionPolicy { Enabled = false },
        };
        using (var database = Tsdb.Open(baseOptions))
        {
            SqlExecutor.Execute(database,
                "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3) WITH INDEX hnsw(m=4, ef=8))");
            SqlExecutor.Execute(database,
                "INSERT INTO docs (time, source, embedding) VALUES (1000, 'a', [1,0,0])");
            database.FlushNow();
            SqlExecutor.Execute(database,
                "INSERT INTO docs (time, source, embedding) VALUES (2000, 'b', [0,0,1])");
            database.FlushNow();
            Assert.True(database.Segments.SegmentCount >= 2);
            SqlExecutor.Execute(database, null,
                "UPDATE docs SET embedding = @embedding WHERE source = 'a'",
                new SqlParameters().AddNamed("embedding", new float[] { 0f, 1f, 0f }));
            AssertUpdated(database);
        }

        var compactOptions = baseOptions with
        {
            Compaction = new CompactionPolicy
            {
                Enabled = true,
                MinTierSize = 2,
                PollInterval = TimeSpan.FromMilliseconds(100),
            },
        };
        using (var compacted = Tsdb.Open(compactOptions))
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline && compacted.Segments.SegmentCount > 1)
                Thread.Sleep(100);
            Assert.Equal(1, compacted.Segments.SegmentCount);
            AssertUpdated(compacted);
        }

        using var reopened = Tsdb.Open(baseOptions);
        AssertUpdated(reopened);
    }

    [Fact]
    public void Embedded_VectorUpdate_TooManyRows_RejectsBeforeChangingAnyPoint()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database, "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3))");
        string values = string.Join(", ", Enumerable.Range(1, 257)
            .Select(i => $"({i}, 'a', [1,0,0])"));
        SqlExecutor.Execute(database, $"INSERT INTO docs (time, source, embedding) VALUES {values}");

        Assert.Contains("最多修改 256 行", Assert.Throws<InvalidOperationException>(() =>
            SqlExecutor.Execute(database, "UPDATE docs SET embedding = [0,1,0] WHERE source = 'a'"))
            .Message, StringComparison.Ordinal);
        Assert.Equal(0, database.VectorReplacements.Count);
        var series = Assert.Single(database.Catalog.Find("docs",
            new Dictionary<string, string> { ["source"] = "a" }));
        Assert.All(database.Query.Execute(new PointQuery(series.Id, "embedding", TimeRange.All)),
            point => Assert.Equal(new float[] { 1f, 0f, 0f }, point.Value.AsVector().ToArray()));
    }

    [Fact]
    public void Embedded_VectorUpdate_WalSyncFailure_RejectsReadsUntilReopen()
    {
        using (var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root }))
        {
            SqlExecutor.Execute(database, "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3))");
            SqlExecutor.Execute(database,
                "INSERT INTO docs (time, source, embedding) VALUES (1000, 'a', [1,0,0]), (2000, 'a', [1,0,0])");
            database.VectorReplacements.WalSyncTestHook = () => throw new IOException("injected sync failure");
            Assert.Throws<IOException>(() => SqlExecutor.Execute(database,
                "UPDATE docs SET embedding = [0,1,0] WHERE source = 'a'"));
            Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(database,
                "SELECT embedding FROM docs WHERE source = 'a'"));
            Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(database,
                "UPDATE docs SET embedding = [0,0,1] WHERE source = 'a'"));
            database.VectorReplacements.WalSyncTestHook = null;
        }

        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        var selected = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            reopened, "SELECT time, embedding FROM docs WHERE source = 'a' ORDER BY time"));
        Assert.Equal(2, selected.Rows.Count);
        Assert.All(selected.Rows, row =>
            Assert.Equal(new float[] { 0f, 1f, 0f }, Assert.IsType<float[]>(row[1])));
    }

    [Fact]
    public void Embedded_VectorUpdate_WalBudgetRejectsBeforeAppend_LeavesDatabaseReadable()
    {
        using var database = Tsdb.Open(new TsdbOptions
        {
            RootDirectory = _root,
            Kv = new KvOptions { MaxWalBytes = KvWalFile.HeaderSize },
        });
        SqlExecutor.Execute(database, "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3))");
        SqlExecutor.Execute(database,
            "INSERT INTO docs (time, source, embedding) VALUES (1000, 'a', [1,0,0])");

        Assert.Throws<IOException>(() => SqlExecutor.Execute(database,
            "UPDATE docs SET embedding = [0,1,0] WHERE source = 'a'"));
        Assert.Equal(0, database.VectorReplacements.Count);
        var selected = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database,
            "SELECT embedding FROM docs WHERE source = 'a'"));
        Assert.Equal(new float[] { 1f, 0f, 0f },
            Assert.IsType<float[]>(Assert.Single(selected.Rows)[0]));
    }

    [Fact]
    public void Embedded_VectorUpdate_LegacyNamedUserKeyspace_RemainsIndependent()
    {
        const string legacyName = "_Measurement-Vector-Replacements";
        using (var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root }))
        {
            database.Keyspaces.Open(legacyName).Put("owner", [42]);
            SqlExecutor.Execute(database, "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3))");
            SqlExecutor.Execute(database,
                "INSERT INTO docs (time, source, embedding) VALUES (1000, 'a', [1,0,0])");
            SqlExecutor.Execute(database,
                "UPDATE docs SET embedding = [0,1,0] WHERE source = 'a'");
            Assert.Equal([42], database.Keyspaces.Open(legacyName).Get("owner"));
            Assert.Contains(legacyName, database.Keyspaces.List());
        }

        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        Assert.Equal([42], reopened.Keyspaces.Open(legacyName).Get("owner"));
        AssertUpdated(reopened);
    }

    [Fact]
    public void Embedded_VectorUpdate_DropAndRecreateAfterReopen_DoesNotApplyOldReplacement()
    {
        using (var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root }))
        {
            SqlExecutor.Execute(database, "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3))");
            SqlExecutor.Execute(database,
                "INSERT INTO docs (time, source, embedding) VALUES (1000, 'a', [1,0,0])");
            SqlExecutor.Execute(database,
                "UPDATE docs SET embedding = [0,1,0] WHERE source = 'a'");
            Assert.True(database.DropMeasurement("docs"));
        }

        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        Assert.Equal(0, reopened.VectorReplacements.Count);
        SqlExecutor.Execute(reopened, "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3))");
        SqlExecutor.Execute(reopened,
            "INSERT INTO docs (time, source, embedding) VALUES (1000, 'a', [0,0,1])");
        var selected = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(reopened,
            "SELECT embedding FROM docs WHERE source = 'a'"));
        Assert.Equal(new float[] { 0f, 0f, 1f },
            Assert.IsType<float[]>(Assert.Single(selected.Rows)[0]));
    }

    [Fact]
    public void Embedded_VectorUpdate_RetentionDrop_ReclaimsReplacementAndRecovers()
    {
        long now = 1000;
        var options = new TsdbOptions
        {
            RootDirectory = _root,
            Compaction = new CompactionPolicy { Enabled = false },
            Retention = new RetentionPolicy
            {
                Enabled = true,
                TtlInTimestampUnits = 1000,
                NowFn = () => Volatile.Read(ref now),
                PollInterval = TimeSpan.FromHours(24),
            },
        };
        using (var database = Tsdb.Open(options))
        {
            SqlExecutor.Execute(database, "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3))");
            SqlExecutor.Execute(database,
                "INSERT INTO docs (time, source, embedding) VALUES (100, 'a', [1,0,0])");
            database.FlushNow();
            SqlExecutor.Execute(database,
                "UPDATE docs SET embedding = [0,1,0] WHERE source = 'a'");
            Assert.Equal(1, database.VectorReplacements.Count);

            Volatile.Write(ref now, 5000);
            Assert.True(database.Retention!.RunOnce().DroppedSegments > 0);
            Assert.Equal(0, database.VectorReplacements.Count);
            Assert.Empty(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
                database, "SELECT embedding FROM docs")).Rows);
        }

        using var reopened = Tsdb.Open(options with { Retention = RetentionPolicy.Default });
        Assert.Equal(0, reopened.VectorReplacements.Count);
    }

    [Fact]
    public async Task Embedded_VectorUpdate_ConcurrentKnn_DistanceMatchesReturnedVector()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database, "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3))");
        SqlExecutor.Execute(database,
            "INSERT INTO docs (time, source, embedding) VALUES (1000, 'a', [1,0,0])");
        SqlExecutor.Execute(database,
            "UPDATE docs SET embedding = [0,1,0] WHERE source = 'a'");

        var writer = Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
                SqlExecutor.Execute(database, i % 2 == 0
                    ? "UPDATE docs SET embedding = [1,0,0] WHERE source = 'a'"
                    : "UPDATE docs SET embedding = [0,1,0] WHERE source = 'a'");
        });
        var reader = Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database,
                    "SELECT distance, embedding FROM knn(docs, embedding, [1,0,0], 1)"));
                var row = Assert.Single(result.Rows);
                var vector = Assert.IsType<float[]>(row[1]);
                Assert.Equal(1d - vector[0], Assert.IsType<double>(row[0]), 6);
            }
        });
        await Task.WhenAll(writer, reader).WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Embedded_VectorUpdate_DeleteThenBackupRestore_DoesNotReviveOldVector()
    {
        string source = Path.Combine(_root, "source");
        string backup = Path.Combine(_root, "backup");
        string restored = Path.Combine(_root, "restored");
        using (var database = Tsdb.Open(new TsdbOptions { RootDirectory = source }))
        {
            SqlExecutor.Execute(database, "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3))");
            SqlExecutor.Execute(database,
                "INSERT INTO docs (time, source, embedding) VALUES (1000, 'a', [1,0,0]), (2000, 'b', [0,0,1])");
            database.FlushNow();
            SqlExecutor.Execute(database,
                "UPDATE docs SET embedding = [0,1,0] WHERE source = 'a'");
            SqlExecutor.Execute(database,
                "DELETE FROM docs WHERE source = 'a' AND time = 1000");
            _ = new BackupService().Create(database,
                new BackupCreateOptions { DestinationDirectory = backup });
        }

        _ = new BackupService().Restore(new BackupRestoreOptions
        {
            BackupDirectory = backup,
            TargetDirectory = restored,
        });
        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = restored });
        var rows = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            reopened, "SELECT time, embedding FROM docs ORDER BY time")).Rows;
        Assert.Equal(2000L, Assert.Single(rows)[0]);
        var knn = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            reopened, "SELECT time FROM knn(docs, embedding, [1,0,0], 2)"));
        Assert.Equal(2000L, Assert.Single(knn.Rows)[0]);
    }

    [Fact]
    public void SameTimestampInsert_DoesNotReplaceOldVectorInRawOrKnn()
    {
        using var database = Tsdb.Open(new TsdbOptions { RootDirectory = _root });
        SqlExecutor.Execute(database, "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3))");
        SqlExecutor.Execute(database,
            "INSERT INTO docs (time, source, embedding) VALUES (1000, 'a', [1,0,0])");
        SqlExecutor.Execute(database,
            "INSERT INTO docs (time, source, embedding) VALUES (1000, 'a', [0,1,0])");

        var seriesId = SeriesId.Compute(new SeriesKey("docs",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["source"] = "a" }));
        Assert.Equal(2, database.Query.Execute(new PointQuery(
            seriesId, "embedding", new TimeRange(1000, 1000))).Count());

        var selected = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            database, "SELECT embedding FROM docs WHERE source = 'a'"));
        Assert.Equal(new float[] { 0f, 1f, 0f }, Assert.IsType<float[]>(Assert.Single(selected.Rows)[0]));

        var knn = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            database, "SELECT embedding FROM knn(docs, embedding, [1,0,0], 1)"));
        Assert.Equal(new float[] { 1f, 0f, 0f }, Assert.IsType<float[]>(Assert.Single(knn.Rows)[0]));
    }

    [Fact]
    public async Task EmbeddedAdo_VectorParametersAndResults_PreserveFloatArrayMetadata()
    {
        using var connection = new SndbConnection($"Data Source={_root}");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE MEASUREMENT docs (source TAG, embedding FIELD VECTOR(3), score FIELD FLOAT)";
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO docs (time, source, embedding) VALUES (1000, 'a', @embedding)";
            command.Parameters.AddWithValue("@embedding", new float[] { 1.25f, -0.5f, 3f });
            Assert.Equal(DbType.Object, command.Parameters[0].DbType);
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
            command.Parameters.Clear();
            command.CommandText = "INSERT INTO docs (time, source, score) VALUES (2000, 'b', 1.0)";
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        using var select = connection.CreateCommand();
        select.CommandText = "SELECT embedding, time, score FROM docs ORDER BY time";
        using var reader = await select.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(typeof(float[]), reader.GetFieldType(0));
        Assert.Equal(typeof(float[]), reader.GetSchemaTable()!.Rows[0][SchemaTableColumn.DataType]);
        Assert.Equal((int)DbType.Object, reader.GetSchemaTable()!.Rows[0][SchemaTableColumn.ProviderType]);
        Assert.Equal(new float[] { 1.25f, -0.5f, 3f }, Assert.IsType<float[]>(reader.GetValue(0)));
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.IsDBNull(0));
        Assert.False(await reader.ReadAsync());

        using var knn = connection.CreateCommand();
        knn.CommandText = "SELECT embedding FROM knn(docs, embedding, @query, 1)";
        knn.Parameters.AddWithValue("@query", new float[] { 1.25f, -0.5f, 3f });
        using var knnReader = await knn.ExecuteReaderAsync();
        Assert.True(await knnReader.ReadAsync());
        Assert.Equal(new float[] { 1.25f, -0.5f, 3f }, Assert.IsType<float[]>(knnReader.GetValue(0)));

        using var invalid = connection.CreateCommand();
        invalid.CommandText = "INSERT INTO docs (time, source, embedding) VALUES (3000, 'c', @embedding)";
        invalid.Parameters.AddWithValue("@embedding", new float[] { 1f, 2f });
        var error = Assert.Throws<InvalidOperationException>(() => invalid.ExecuteNonQuery());
        Assert.Contains("维度不匹配", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void VectorParameterBinder_SupportsMemoryAndRejectsInvalidVectors()
    {
        var literal = Assert.IsType<VectorLiteralExpression>(SqlParameterBinder.ToLiteral(new ReadOnlyMemory<float>([1f, 2f])));
        Assert.Equal(new double[] { 1d, 2d }, literal.Components);

        Assert.Throws<ArgumentException>(() => SqlParameterBinder.ToLiteral(Array.Empty<float>()));
        Assert.Throws<ArgumentException>(() => SqlParameterBinder.ToLiteral(new float[] { float.NaN }));
        Assert.IsType<LiteralExpression>(SqlParameterBinder.ToLiteral(null));

        Assert.Equal("[1.25, -0.5, 3]", ParameterBinder.FormatLiteral(new float[] { 1.25f, -0.5f, 3f }));
        Assert.Throws<ArgumentException>(() => ParameterBinder.FormatLiteral(new float[] { float.PositiveInfinity }));
    }

    [Fact]
    public void RemoteExecutionResult_DecodesNumericArrayAsFloatArrayAndPreservesOtherArrays()
    {
        using (var document = JsonDocument.Parse("[1.25,-0.5,3]"))
        {
            var vector = Assert.IsType<float[]>(RemoteExecutionResult.ReadScalar(document.RootElement));
            Assert.Equal(new float[] { 1.25f, -0.5f, 3f }, vector);
        }

        using (var document = JsonDocument.Parse("[1,null,3]"))
            Assert.Equal("[1,null,3]", Assert.IsType<string>(RemoteExecutionResult.ReadScalar(document.RootElement)));

        using (var document = JsonDocument.Parse("null"))
            Assert.Null(RemoteExecutionResult.ReadScalar(document.RootElement));
    }
}
