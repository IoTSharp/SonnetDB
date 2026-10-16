using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using SonnetDB.Backup;
using SonnetDB.Data;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using SonnetDB.Views;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>#252：完整原生时长、旧 TIME 边界与永久格式屏障。</summary>
public sealed class SqlDurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "duration-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(972000000001L)]
    [InlineData(long.MinValue)]
    [InlineData(long.MaxValue)]
    public void Parameter_EntireTickRange_PreservesNativeTypeAndReopen(long ticks)
    {
        using (var connection = OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE durations(id INT, elapsed DURATION NULL, PRIMARY KEY(id))";
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO durations(id,elapsed) VALUES(1,@elapsed),(2,NULL)";
            command.Parameters.Add(new SndbParameter("elapsed", TimeSpan.FromTicks(ticks)));
            Assert.Equal(2, command.ExecuteNonQuery());
            command.CommandText = "SELECT elapsed FROM durations WHERE elapsed=@elapsed";
            using var reader = command.ExecuteReader();
            Assert.Equal(typeof(TimeSpan), reader.GetFieldType(0));
            Assert.Equal(typeof(TimeSpan), reader.GetColumnSchema()[0].DataType);
            Assert.Equal(typeof(TimeSpan), reader.GetSchemaTable()!.Rows[0][SchemaTableColumn.DataType]);
            Assert.True(reader.Read());
            Assert.Equal(ticks, Assert.IsType<TimeSpan>(reader.GetValue(0)).Ticks);
            Assert.False(reader.Read());
        }
        using var reopened = OpenConnection();
        using var query = reopened.CreateCommand();
        query.CommandText = "SELECT elapsed FROM durations ORDER BY id";
        using var result = query.ExecuteReader();
        Assert.Equal(typeof(TimeSpan), result.GetFieldType(0));
        Assert.True(result.Read());
        Assert.Equal(TimeSpan.FromTicks(ticks), result.GetFieldValue<TimeSpan>(0));
        Assert.True(result.Read());
        Assert.True(result.IsDBNull(0));
    }

    [Fact]
    public void Declaration_EmptyAndAllNull_PreservesTimeSpan()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE durations(id INT, elapsed DURATION NULL, PRIMARY KEY(id))";
        command.ExecuteNonQuery();
        for (int phase = 0; phase < 2; phase++)
        {
            command.CommandText = "SELECT elapsed, CAST(NULL AS DURATION), elapsed + CAST(1 AS DURATION) FROM durations";
            using (var reader = command.ExecuteReader())
            {
                for (int column = 0; column < 3; column++) Assert.Equal(typeof(TimeSpan), reader.GetFieldType(column));
                Assert.Equal(phase == 1, reader.Read());
                if (phase == 1) for (int column = 0; column < 3; column++) Assert.True(reader.IsDBNull(column));
            }
            if (phase == 0)
            {
                command.CommandText = "INSERT INTO durations(id,elapsed) VALUES(1,NULL)";
                command.ExecuteNonQuery();
            }
        }
    }

    [Fact]
    public void Duration_OrderingIndexesAndTransactions_PreserveSignedTicks()
    {
        using var database = OpenDatabase();
        Execute(database, "CREATE TABLE durations(elapsed DURATION, label STRING, PRIMARY KEY(elapsed))");
        Execute(database, "CREATE INDEX ix_label ON durations(label)");
        Execute(database, "INSERT INTO durations(elapsed,label) VALUES(CAST(-1 AS DURATION),'negative'),(CAST(972000000000 AS DURATION),'long'),(CAST(0 AS DURATION),'zero')");
        Assert.Equal(new long[] { -1, 0, 972000000000 }, Query(database, "SELECT elapsed FROM durations ORDER BY elapsed")
            .Rows.Select(static row => Assert.IsType<TimeSpan>(row[0]).Ticks));
        Assert.Equal(TimeSpan.FromTicks(-1), Query(database, "SELECT elapsed FROM durations WHERE label='negative'").Rows[0][0]);
        Execute(database, "ANALYZE TABLE durations");
        var transaction = Assert.IsType<SqlTransactionContext>(Execute(database, "BEGIN"));
        SqlExecutor.ExecuteStatement(database, null, SqlParser.Parse("INSERT INTO durations(elapsed,label) VALUES(CAST(-2 AS DURATION),'rollback')"), null, transaction);
        SqlExecutor.ExecuteStatement(database, null, SqlParser.Parse("ROLLBACK"), null, transaction);
        Assert.Equal(3L, Query(database, "SELECT COUNT(*) FROM durations").Rows[0][0]);
        transaction = Assert.IsType<SqlTransactionContext>(Execute(database, "BEGIN"));
        SqlExecutor.ExecuteStatement(database, null, SqlParser.Parse("INSERT INTO durations(elapsed,label) VALUES(CAST(-2 AS DURATION),'commit')"), null, transaction);
        SqlExecutor.ExecuteStatement(database, null, SqlParser.Parse("COMMIT"), null, transaction);
        Assert.Equal(4L, Query(database, "SELECT COUNT(*) FROM durations").Rows[0][0]);
    }

    [Fact]
    public void DateTimeSubtract_RuntimeTicksAndPersistedMilliseconds_UseEvaluatedOperands()
    {
        using var database = OpenDatabase();
        const string left = "CAST('2026-10-16T00:00:00.0000001Z' AS DATETIME)";
        const string right = "CAST('2026-10-15T00:00:00Z' AS DATETIME)";
        var result = Query(database, $"SELECT {left}-{right}, {right}-{left}, CAST(NULL AS DATETIME)-{right}");
        Assert.Equal(TimeSpan.FromDays(1).Add(TimeSpan.FromTicks(1)), result.Rows[0][0]);
        Assert.Equal(-TimeSpan.FromDays(1).Add(TimeSpan.FromTicks(1)), result.Rows[0][1]);
        Assert.Null(result.Rows[0][2]);
        Assert.All(result.ColumnInfo!, info => Assert.Equal(TableColumnType.Duration, info.DataType));
        Execute(database, "CREATE TABLE dates(id INT, start_at DATETIME, end_at DATETIME, PRIMARY KEY(id))");
        Execute(database, $"INSERT INTO dates(id,start_at,end_at) VALUES(1,{right},{left})");
        Assert.Equal(TimeSpan.FromDays(1), Query(database, "SELECT end_at-start_at FROM dates").Rows[0][0]);
    }

    [Fact]
    public void CastAndArithmetic_CanonicalTextBoundsAndOverflow_AreExplicit()
    {
        using var database = OpenDatabase();
        Assert.Equal(TimeSpan.FromHours(-27).Add(TimeSpan.FromTicks(-1)), Query(database,
            "SELECT CAST('-1.03:00:00.0000001' AS DURATION)").Rows[0][0]);
        Assert.Equal("1.03:00:00.0000001", Query(database,
            "SELECT CAST(CAST(972000000001 AS DURATION) AS STRING)").Rows[0][0]);
        Assert.Equal(-1L, Query(database, "SELECT CAST(CAST(-1 AS DURATION) AS INT)").Rows[0][0]);
        Assert.Equal(TimeSpan.FromTicks(1), Query(database, "SELECT CAST(2 AS DURATION)-CAST(1 AS DURATION)").Rows[0][0]);
        Assert.Throws<InvalidOperationException>(() => Execute(database, "SELECT -CAST(-9223372036854775808 AS DURATION)"));
        Assert.Throws<InvalidOperationException>(() => Execute(database, "SELECT CAST(9223372036854775807 AS DURATION)+CAST(1 AS DURATION)"));
        Assert.Throws<InvalidOperationException>(() => Execute(database, "SELECT CAST(1.5 AS DURATION)"));
        Assert.Throws<InvalidOperationException>(() => Execute(database, "SELECT CAST('1 month' AS DURATION)"));
    }

    [Fact]
    public void TimeColumn_InDayTimeSpanAndDbTypeTime_PreserveExistingContract()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE clocks(id INT, starts TIME, PRIMARY KEY(id))";
        command.ExecuteNonQuery();
        var time = TimeSpan.FromHours(12).Add(TimeSpan.FromTicks(1));
        command.Parameters.Add(new SndbParameter("time", time) { DbType = DbType.Time });
        command.CommandText = "INSERT INTO clocks(id,starts) VALUES(1,@time)";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT starts FROM clocks WHERE starts=@time";
        using (var reader = command.ExecuteReader())
        {
            Assert.Equal(typeof(TimeOnly), reader.GetFieldType(0));
            Assert.True(reader.Read());
            Assert.Equal(TimeOnly.FromTimeSpan(time), reader.GetValue(0));
        }
        command.Parameters[0].Value = TimeSpan.FromHours(27);
        command.CommandText = "INSERT INTO clocks(id,starts) VALUES(2,@time)";
        Assert.Throws<ArgumentOutOfRangeException>(() => command.ExecuteNonQuery());
    }

    [Fact]
    public void Schema_DurationMigrationDropAndReopen_PreserveVersionFloor()
    {
        string schemaPath;
        using (var database = OpenDatabase())
        {
            Execute(database, "CREATE TABLE durations(id INT, elapsed INT)");
            Execute(database, "INSERT INTO durations(id,elapsed) VALUES(1,-1),(2,972000000001)");
            schemaPath = database.Tables.SchemaPath;
            Assert.True(TableSchemaCodec.ReadFormatVersion(schemaPath) < 14);
            Execute(database, "ALTER TABLE durations ALTER COLUMN elapsed DURATION");
            Assert.Equal(14, TableSchemaCodec.ReadFormatVersion(schemaPath));
            Assert.Equal(TimeSpan.FromTicks(-1), Query(database, "SELECT elapsed FROM durations WHERE id=1").Rows[0][0]);
        }
        using (var reopened = OpenDatabase())
        {
            Assert.Equal(TimeSpan.FromTicks(972000000001), Query(reopened, "SELECT elapsed FROM durations WHERE id=2").Rows[0][0]);
            Execute(reopened, "ALTER TABLE durations DROP COLUMN elapsed");
            Assert.Equal(14, TableSchemaCodec.ReadFormatVersion(schemaPath));
            Execute(reopened, "DROP TABLE durations");
            Assert.Equal(14, TableSchemaCodec.ReadFormatVersion(schemaPath));
        }
        using var final = OpenDatabase();
        Assert.Empty(final.Tables.Catalog.Snapshot());
        Assert.Equal(14, TableSchemaCodec.ReadFormatVersion(schemaPath));
    }

    [Fact]
    public void Persistence_SnapshotSpillAndInvalidOldDeclaration_PreserveOrReject()
    {
        using var database = OpenDatabase();
        Execute(database, "CREATE TABLE durations(id INT, elapsed DURATION, PRIMARY KEY(id))");
        Execute(database, "INSERT INTO durations(id,elapsed) VALUES(1,CAST(-9223372036854775808 AS DURATION))");
        var result = Query(database, "SELECT elapsed FROM durations");
        string path = Path.Combine(_root, "snapshot.bin");
        MaterializedViewSnapshotCodec.Save(path, result);
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(path).AsSpan(8)));
        Assert.Equal(TimeSpan.MinValue, MaterializedViewSnapshotCodec.Load(path).Rows[0][0]);
        using var memory = new MemoryStream();
        using (var writer = new BinaryWriter(memory, System.Text.Encoding.UTF8, leaveOpen: true))
            SqlSpillRowCodec.WriteRow(writer, [TimeSpan.MinValue, TimeSpan.MaxValue, null]);
        memory.Position = 0;
        using var reader = new BinaryReader(memory, System.Text.Encoding.UTF8, leaveOpen: true);
        Assert.Equal(new object?[] { TimeSpan.MinValue, TimeSpan.MaxValue, null }, SqlSpillRowCodec.ReadRow(reader));
        byte[] schema = File.ReadAllBytes(database.Tables.SchemaPath);
        BinaryPrimitives.WriteInt32LittleEndian(schema.AsSpan(8), 13);
        using var malformed = new MemoryStream(schema);
        Assert.Throws<InvalidDataException>(() => TableSchemaCodec.Load(malformed));
    }

    [Fact]
    public void LegacyResultContract_ReturningAndProcedure_RejectBeforePersistentWrites()
    {
        using var database = OpenDatabase();
        Execute(database, "CREATE TABLE durations(id INT, elapsed DURATION NULL, PRIMARY KEY(id))");
        Execute(database, "INSERT INTO durations(id,elapsed) VALUES(1,NULL)");
        foreach (string sql in new[] {
            "INSERT INTO durations(id,elapsed) VALUES(2,CAST(-1 AS DURATION)) RETURNING elapsed",
            "UPDATE durations SET elapsed=CAST(-1 AS DURATION) WHERE id=1 RETURNING *",
            "DELETE FROM durations WHERE id=1 RETURNING elapsed",
            "SELECT elapsed FROM durations WHERE id=999",
            "SELECT CAST(NULL AS DURATION) AS elapsed" })
            Assert.Throws<InvalidOperationException>(() => ExecuteLegacy(database, sql));
        Assert.Single(Query(database, "SELECT * FROM durations").Rows);
        Assert.Null(Query(database, "SELECT elapsed FROM durations").Rows[0][0]);
        Execute(database, "CREATE PROCEDURE duration_result() LANGUAGE SQL AS BEGIN INSERT INTO durations(id,elapsed) VALUES(2,CAST(-1 AS DURATION)); SELECT CAST(1 AS DURATION) AS delta; END");
        Assert.ThrowsAny<Exception>(() => ExecuteLegacy(database, "CALL duration_result()"));
        Assert.Single(Query(database, "SELECT * FROM durations").Rows);
    }

    [Fact]
    public void LegacyResultContract_SequencesAndNestedProcedures_RejectBeforeConsumption()
    {
        using var database = OpenDatabase();
        Execute(database, "CREATE SEQUENCE numbers START WITH 10");
        foreach (string output in new[] { "CAST(1 AS DURATION)",
            "CAST('2026-10-16' AS DATETIME)-CAST('2026-10-15' AS DATETIME)",
            "CASE WHEN TRUE THEN CAST(1 AS DURATION) ELSE 0 END" })
            Assert.Throws<InvalidOperationException>(() => ExecuteLegacy(database, $"SELECT nextval('numbers'), {output} AS result"));
        Execute(database, "CREATE PROCEDURE duration_leaf() LANGUAGE SQL AS BEGIN SELECT CAST(NULL AS DURATION) AS result; END");
        Execute(database, "CREATE PROCEDURE duration_caller() LANGUAGE SQL AS BEGIN SELECT nextval('numbers'); CALL duration_leaf(); END");
        Assert.ThrowsAny<Exception>(() => ExecuteLegacy(database, "CALL duration_caller()"));
        Assert.Equal(10L, Query(database, "SELECT nextval('numbers')").Rows[0][0]);
        Execute(database, "CREATE TABLE defaults(id INT DEFAULT nextval('numbers'), elapsed DURATION NULL, PRIMARY KEY(id))");
        Execute(database, "CREATE PROCEDURE duration_default() LANGUAGE SQL AS BEGIN INSERT INTO defaults(elapsed) VALUES(NULL); SELECT CAST(NULL AS DURATION); END");
        Assert.ThrowsAny<Exception>(() => ExecuteLegacy(database, "CALL duration_default()"));
        Assert.Equal(11L, Query(database, "SELECT nextval('numbers')").Rows[0][0]);
        Assert.Empty(Query(database, "SELECT * FROM defaults").Rows);
        foreach (string returning in new[] { "CAST(1 AS DURATION)", "elapsed AS renamed", "elapsed+elapsed" })
            Assert.Throws<SqlParseException>(() => ExecuteLegacy(database,
                $"INSERT INTO defaults(elapsed) VALUES(NULL) RETURNING {returning}"));
        Assert.Equal(12L, Query(database, "SELECT nextval('numbers')").Rows[0][0]);
    }

    [Theory]
    [InlineData("after-barrier")]
    [InlineData("after-journal")]
    [InlineData("after-table:durations")]
    [InlineData("after-catalog")]
    public void Migration_FailureAndRetry_PreserveOriginalRowsAndPermanentBarrier(string failurePhase)
    {
        using (var database = OpenDatabase())
        {
            Execute(database, "CREATE TABLE durations(id INT, elapsed INT)");
            Execute(database, "INSERT INTO durations(id,elapsed) VALUES(1,-1),(2,972000000001)");
            database.Tables.SchemaMigrationPhaseTestHook = phase =>
            {
                if (phase == failurePhase) throw new IOException("injected duration migration failure");
            };
            Assert.ThrowsAny<Exception>(() => Execute(database, "ALTER TABLE durations ALTER COLUMN elapsed DURATION"));
        }
        using var reopened = OpenDatabase();
        Assert.Equal(14, TableSchemaCodec.ReadFormatVersion(reopened.Tables.SchemaPath));
        Assert.Equal(-1L, Query(reopened, "SELECT elapsed FROM durations WHERE id=1").Rows[0][0]);
        Execute(reopened, "ALTER TABLE durations ALTER COLUMN elapsed DURATION");
        Assert.Equal(TimeSpan.FromTicks(-1), Query(reopened, "SELECT elapsed FROM durations WHERE id=1").Rows[0][0]);
    }

    [Fact]
    public void Backup_DurationRowsAndIndex_RestoreNativeValuesAndBarrier()
    {
        string backup = Path.Combine(_root, "backup");
        string restored = Path.Combine(_root, "restored");
        using (var database = Tsdb.Open(new TsdbOptions { RootDirectory = Path.Combine(_root, "source") }))
        {
            Execute(database, "CREATE TABLE durations(id INT, elapsed DURATION NULL)");
            Execute(database, "CREATE INDEX ix_elapsed ON durations(elapsed)");
            Execute(database, "INSERT INTO durations(id,elapsed) VALUES(1,CAST(-9223372036854775808 AS DURATION)),(2,CAST(9223372036854775807 AS DURATION)),(3,NULL)");
            new BackupService().Create(database, new BackupCreateOptions { DestinationDirectory = backup });
        }
        new BackupService().Restore(new BackupRestoreOptions { BackupDirectory = backup, TargetDirectory = restored });
        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = restored });
        Assert.Equal(14, TableSchemaCodec.ReadFormatVersion(reopened.Tables.SchemaPath));
        Assert.Equal(TimeSpan.MinValue, Query(reopened, "SELECT elapsed FROM durations WHERE elapsed=CAST(-9223372036854775808 AS DURATION)").Rows[0][0]);
        Assert.Equal(new object?[] { TimeSpan.MinValue, TimeSpan.MaxValue, null },
            Query(reopened, "SELECT elapsed FROM durations ORDER BY id").Rows.Select(row => row[0]));
    }

    [Fact]
    public void Migration_BarrierIoFailure_LeavesRowsAndNoPendingJournal()
    {
        string temporary;
        using (var database = OpenDatabase())
        {
            Execute(database, "CREATE TABLE durations(id INT, elapsed INT)");
            Execute(database, "INSERT INTO durations(id,elapsed) VALUES(1,-1)");
            temporary = database.Tables.SchemaPath + ".tmp";
            Directory.CreateDirectory(temporary);
            var store = database.Tables.Open("durations");
            long wal = store.ActiveWalLengthForEvidence;
            Assert.ThrowsAny<Exception>(() => Execute(database, "ALTER TABLE durations ALTER COLUMN elapsed TYPE DURATION"));
            Assert.Equal(wal, store.ActiveWalLengthForEvidence);
            Assert.False(File.Exists(Path.Combine(_root, "tables", TableSchemaMigrationJournal.FileName)));
        }
        Directory.Delete(temporary);
        using var reopened = OpenDatabase();
        Assert.True(TableSchemaCodec.ReadFormatVersion(reopened.Tables.SchemaPath) < 14);
        Assert.Equal(-1L, Query(reopened, "SELECT elapsed FROM durations").Rows[0][0]);
        Execute(reopened, "ALTER TABLE durations ALTER COLUMN elapsed TYPE DURATION");
        Assert.Equal(TimeSpan.FromTicks(-1), Query(reopened, "SELECT elapsed FROM durations").Rows[0][0]);
    }

    [Fact]
    public void Create_PublishFailureAndRetry_KeepV14WithoutExposingPartialTable()
    {
        using (var database = OpenDatabase())
        {
            database.Tables.AfterCatalogPersistedBeforePublishTestHook = () => throw new IOException("injected publish failure");
            Assert.Throws<IOException>(() => Execute(database, "CREATE TABLE durations(id INT, elapsed DURATION)"));
            Assert.Empty(database.Tables.Catalog.Snapshot());
            Assert.Equal(14, TableSchemaCodec.ReadFormatVersion(database.Tables.SchemaPath));
        }
        using var reopened = OpenDatabase();
        Assert.Empty(reopened.Tables.Catalog.Snapshot());
        Execute(reopened, "CREATE TABLE durations(id INT, elapsed DURATION)");
        Execute(reopened, "INSERT INTO durations(id,elapsed) VALUES(1,CAST(-1 AS DURATION))");
        Assert.Equal(TimeSpan.FromTicks(-1), Query(reopened, "SELECT elapsed FROM durations").Rows[0][0]);
    }

    private Tsdb OpenDatabase() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });
    private SndbConnection OpenConnection()
    {
        var connection = new SndbConnection("Data Source=" + _root);
        connection.Open();
        return connection;
    }
    private static object? Execute(Tsdb database, string sql) => SqlExecutor.Execute(database, sql);
    private static SelectExecutionResult Query(Tsdb database, string sql) => Assert.IsType<SelectExecutionResult>(Execute(database, sql));
    private static object? ExecuteLegacy(Tsdb database, string sql) => SqlExecutor.ExecuteStatement(database, null,
        SqlParser.Parse(sql), null, null, new SqlExecutionOptions { AllowDurationResults = false });
}
