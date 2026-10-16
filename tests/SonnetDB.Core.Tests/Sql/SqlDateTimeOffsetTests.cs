using System.Buffers.Binary;
using System.Data;
using System.Data.Common;
using SonnetDB.Backup;
using SonnetDB.Data;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using SonnetDB.Views;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>#251：原生 DateTimeOffset、精确时刻与显式旧 DATETIME 兼容。</summary>
[Collection(SqlSpillSerialCollection.Name)]
public sealed class SqlDateTimeOffsetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "offset-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(639270596961230001L)]
    [InlineData(3155378975999999999L)]
    public void Parameter_NativeTicks_PreservesGeneralReaderAndReopen(long ticks)
    {
        using (var connection = OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE offsets(id INT, Stamp DATETIMEOFFSET NULL, PRIMARY KEY(id))";
            command.ExecuteNonQuery();
            command.CommandText = "INSERT INTO offsets(id,Stamp) VALUES(1,@stamp),(2,NULL)";
            command.Parameters.Add(new SndbParameter("stamp", new DateTimeOffset(ticks, TimeSpan.Zero)));
            Assert.Equal(2, command.ExecuteNonQuery());
            command.CommandText = "SELECT Stamp FROM offsets WHERE Stamp=@stamp";
            using var reader = command.ExecuteReader();
            AssertNativeMetadata(reader, 0);
            Assert.True(reader.Read());
            var value = Assert.IsType<DateTimeOffset>(reader.GetValue(0));
            Assert.Equal(ticks, value.UtcTicks);
            Assert.Equal(TimeSpan.Zero, value.Offset);
            var values = new object[1];
            Assert.Equal(1, reader.GetValues(values));
            Assert.Equal(value, values[0]);
            Assert.False(reader.Read());
        }
        using var reopened = OpenConnection();
        using var query = reopened.CreateCommand();
        query.CommandText = "SELECT Stamp FROM offsets ORDER BY id";
        using var result = query.ExecuteReader();
        AssertNativeMetadata(result, 0);
        Assert.True(result.Read());
        Assert.Equal(ticks, result.GetFieldValue<DateTimeOffset>(0).UtcTicks);
        Assert.True(result.Read());
        Assert.True(result.IsDBNull(0));
    }

    [Theory]
    [InlineData("2026-10-08T12:34:56.1230001Z")]
    [InlineData("2026-10-08T20:34:56.1230001+08:00")]
    [InlineData("2026-10-08T05:34:56.1230001-07:00")]
    [InlineData("2026-10-08T12:34:56.1230001")]
    public void Cast_TextOffsetAndNoOffset_NormalizesSameUtcInstant(string text)
    {
        using var database = OpenDatabase();
        var result = Query(database, $"SELECT CAST('{text}' AS DATETIMEOFFSET)");
        var expected = DateTimeOffset.Parse("2026-10-08T12:34:56.1230001Z", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(expected, Assert.IsType<DateTimeOffset>(result.Rows[0][0]));
        Assert.Equal(TableColumnType.DateTimeOffset, result.ColumnInfo![0].DataType);
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void ExplicitNativeParameter_DateTimeKind_NormalizesAtCallerWithoutLosingTicks(DateTimeKind kind)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var value = new DateTime(2026, 10, 8, 12, 34, 56, kind).AddTicks(1230001);
        command.CommandText = "SELECT @stamp AS Stamp";
        command.Parameters.Add(new SndbParameter("stamp", value) { DbType = DbType.DateTimeOffset });
        using var reader = command.ExecuteReader();
        AssertNativeMetadata(reader, 0);
        Assert.True(reader.Read());
        long expected = kind == DateTimeKind.Unspecified ? value.Ticks : value.ToUniversalTime().Ticks;
        Assert.Equal(expected, Assert.IsType<DateTimeOffset>(reader.GetValue(0)).UtcTicks);
    }

    [Fact]
    public void Declaration_EmptyAllNullAndTypedParameter_PreservesNativeType()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE offsets(id INT, Stamp DATETIMEOFFSET NULL, PRIMARY KEY(id))";
        command.ExecuteNonQuery();
        command.Parameters.Add(new SndbParameter("stamp", DBNull.Value) { DbType = DbType.DateTimeOffset });
        for (int phase = 0; phase < 2; phase++)
        {
            command.CommandText = "SELECT Stamp, CAST(NULL AS DATETIMEOFFSET), @stamp FROM offsets";
            using (var reader = command.ExecuteReader())
            {
                for (int column = 0; column < 3; column++) AssertNativeMetadata(reader, column);
                Assert.Equal(phase == 1, reader.Read());
                if (phase == 1) for (int column = 0; column < 3; column++) Assert.True(reader.IsDBNull(column));
            }
            if (phase == 0)
            {
                command.CommandText = "INSERT INTO offsets(id,Stamp) VALUES(1,NULL)";
                command.ExecuteNonQuery();
            }
        }
    }

    [Theory]
    [InlineData(DbType.DateTime)]
    [InlineData(DbType.DateTime2)]
    public void LegacyDateTime_ExplicitParameterType_PreservesMillisecondsAndGeneralDateTime(DbType dbType)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE dates(id INT, Stamp DATETIME, PRIMARY KEY(id))";
        command.ExecuteNonQuery();
        var stamp = new DateTimeOffset(2026, 10, 8, 20, 34, 56, TimeSpan.FromHours(8)).AddTicks(1230001);
        command.Parameters.Add(new SndbParameter("stamp", stamp));
        command.CommandText = "INSERT INTO dates(id,Stamp) VALUES(1,@stamp)";
        command.ExecuteNonQuery();
        command.CommandText = "SELECT Stamp FROM dates WHERE Stamp=@stamp";
        Assert.Throws<InvalidOperationException>(() => command.ExecuteReader());
        command.Parameters[0].DbType = dbType;
        using var reader = command.ExecuteReader();
        Assert.Equal(typeof(DateTime), reader.GetFieldType(0));
        Assert.True(reader.Read());
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(stamp.ToUnixTimeMilliseconds()).UtcDateTime,
            Assert.IsType<DateTime>(reader.GetValue(0)));
    }

    [Theory]
    [InlineData("SELECT Stamp FROM offsets WHERE Stamp=Legacy")]
    [InlineData("SELECT Stamp FROM offsets WHERE Stamp=0 LIMIT 0")]
    [InlineData("SELECT Stamp FROM offsets WHERE FALSE AND Stamp=Legacy")]
    [InlineData("SELECT CASE WHEN TRUE THEN Stamp ELSE Legacy END FROM offsets LIMIT 0")]
    [InlineData("SELECT COALESCE(Stamp,Legacy) FROM offsets")]
    [InlineData("SELECT Stamp FROM offsets WHERE Stamp IN (Legacy)")]
    [InlineData("SELECT Stamp FROM offsets WHERE Stamp IN (SELECT Legacy FROM offsets)")]
    [InlineData("SELECT o.Stamp FROM offsets o WHERE EXISTS (SELECT i.id FROM offsets i WHERE o.Stamp=i.Legacy)")]
    [InlineData("SELECT a.Stamp FROM offsets a JOIN offsets b ON a.Stamp=b.Legacy")]
    [InlineData("SELECT a.Stamp FROM offsets a JOIN offsets b ON a.id=b.id JOIN offsets c ON a.Stamp=c.Legacy LIMIT 0")]
    [InlineData("SELECT MIN(Stamp) FROM offsets HAVING MIN(Stamp)=MAX(Legacy)")]
    [InlineData("WITH a AS (SELECT Stamp,Legacy FROM offsets) SELECT Stamp FROM a WHERE Stamp=Legacy")]
    [InlineData("SELECT Stamp FROM offsets WHERE FALSE UNION SELECT Legacy FROM offsets WHERE FALSE")]
    [InlineData("SELECT NULL FROM offsets WHERE FALSE UNION SELECT Stamp FROM offsets WHERE FALSE UNION SELECT Legacy FROM offsets WHERE FALSE")]
    [InlineData("UPDATE offsets SET label='wrong' WHERE Stamp=Legacy")]
    [InlineData("DELETE FROM offsets WHERE Stamp=Legacy LIMIT 0")]
    [InlineData("UPDATE offsets AS o SET label='wrong' FROM (VALUES(CAST(NULL AS DATETIMEOFFSET)),(CAST(NULL AS DATETIME))) AS v(x) WHERE o.Stamp=v.x")]
    [InlineData("SELECT o.Stamp FROM offsets o JOIN (VALUES(CAST(NULL AS DATETIMEOFFSET)),(CAST(NULL AS DATETIME))) AS v(x) ON o.Stamp=v.x")]
    [InlineData("WITH RECURSIVE r(x) AS (SELECT Stamp FROM offsets UNION ALL SELECT Legacy FROM offsets) SELECT x FROM r LIMIT 0")]
    [InlineData("INSERT INTO offsets(id,Stamp,Legacy,label) VALUES(100,NULL,NULL,'wrong') ON CONFLICT(id) DO UPDATE SET label=CASE WHEN excluded.Stamp=offsets.Legacy THEN 'wrong' ELSE 'bad' END")]
    [InlineData("ALTER TABLE offsets ADD CONSTRAINT invalid_stamp CHECK (Stamp=Legacy)")]
    public void Comparison_EmptyNullPopulatedAndIndex_RejectsBeforeCandidateSelection(string sql)
    {
        using var database = OpenDatabase();
        Execute(database, "CREATE TABLE offsets(id INT, Stamp DATETIMEOFFSET NULL, Legacy DATETIME NULL, label STRING, PRIMARY KEY(id))");
        for (int phase = 0; phase < 4; phase++)
        {
            var failure = Assert.Throws<InvalidOperationException>(() => Execute(database, sql));
            Assert.Contains("DATETIMEOFFSET", failure.Message);
            if (phase == 0)
                Execute(database, "INSERT INTO offsets(id,Stamp,Legacy,label) VALUES(1,NULL,NULL,'null')");
            else if (phase == 1)
                Execute(database, "INSERT INTO offsets(id,Stamp,Legacy,label) VALUES(2,CAST('2026-10-08T12:34:56.1230001Z' AS DATETIMEOFFSET),CAST('2026-10-08T12:34:56.123Z' AS DATETIME),'value')");
            else if (phase == 2)
                Execute(database, "CREATE INDEX ix_stamp ON offsets(Stamp)");
        }
        Assert.Equal(new object?[] { "null", "value" }, Query(database, "SELECT label FROM offsets ORDER BY id").Rows.Select(row => row[0]));
    }

    [Fact]
    public void ExactInstants_KeysGroupingJoinAndSorting_DistinguishesAdjacentTicks()
    {
        using var database = OpenDatabase();
        Execute(database, "CREATE TABLE offsets(Stamp DATETIMEOFFSET, label STRING, PRIMARY KEY(Stamp))");
        Execute(database, "INSERT INTO offsets(Stamp,label) VALUES(CAST('2026-10-08T12:34:56.1230000Z' AS DATETIMEOFFSET),'a'),(CAST('2026-10-08T20:34:56.1230001+08:00' AS DATETIMEOFFSET),'b')");
        Assert.ThrowsAny<Exception>(() => Execute(database, "INSERT INTO offsets(Stamp,label) VALUES(CAST('2026-10-08T05:34:56.1230000-07:00' AS DATETIMEOFFSET),'duplicate')"));
        Execute(database, "CREATE UNIQUE INDEX ix_offset ON offsets(Stamp)");
        var ordered = Query(database, "SELECT Stamp FROM offsets ORDER BY Stamp");
        Assert.Equal(1, Assert.IsType<DateTimeOffset>(ordered.Rows[1][0]).UtcTicks - Assert.IsType<DateTimeOffset>(ordered.Rows[0][0]).UtcTicks);
        Assert.Equal(2, Query(database, "SELECT Stamp,COUNT(*) FROM offsets GROUP BY Stamp").Rows.Count);
        Assert.Equal(2, Query(database, "SELECT DISTINCT Stamp FROM offsets").Rows.Count);
        Assert.Equal(2, Query(database, "SELECT a.Stamp,b.label FROM offsets a JOIN offsets b ON a.Stamp=b.Stamp").Rows.Count);
        Assert.Equal("b", Query(database, "SELECT label FROM offsets WHERE Stamp=(SELECT MAX(Stamp) FROM offsets)").Rows[0][0]);
        Assert.Equal(ordered.Rows[0][0], Query(database, "SELECT MIN(Stamp) FROM offsets").Rows[0][0]);
        Assert.Equal("b", Query(database, "SELECT label FROM offsets WHERE Stamp=CAST('2026-10-08T12:34:56.1230001Z' AS DATETIMEOFFSET)").Rows[0][0]);
        Assert.Equal("b", Query(database, "SELECT label FROM offsets WHERE Stamp>CAST('2026-10-08T12:34:56.123Z' AS DATETIMEOFFSET)").Rows[0][0]);
        Execute(database, "ANALYZE TABLE offsets");
        Assert.Equal(TimeSpan.FromTicks(1), Query(database,
            "SELECT CAST('2026-10-08T12:34:56.1230001Z' AS DATETIMEOFFSET)-CAST('2026-10-08T12:34:56.123Z' AS DATETIMEOFFSET)").Rows[0][0]);
    }

    [Fact]
    public void Schema_MigrationSnapshotAndDrop_PreservesNativeVersionFloor()
    {
        string schemaPath;
        using (var database = OpenDatabase())
        {
            Execute(database, "CREATE TABLE offsets(id INT, Stamp INT, PRIMARY KEY(id))");
            Execute(database, "INSERT INTO offsets(id,Stamp) VALUES(1,639270596961230001)");
            schemaPath = database.Tables.SchemaPath;
            Execute(database, "ALTER TABLE offsets ALTER COLUMN Stamp DATETIMEOFFSET");
            Assert.Equal(15, TableSchemaCodec.ReadFormatVersion(schemaPath));
            var result = Query(database, "SELECT Stamp FROM offsets WHERE FALSE");
            string path = Path.Combine(_root, "offset.snapshot");
            MaterializedViewSnapshotCodec.Save(path, result);
            Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(path).AsSpan(8)));
            Assert.Equal(TableColumnType.DateTimeOffset, MaterializedViewSnapshotCodec.Load(path).ColumnInfo![0].DataType);
            byte[] schema = File.ReadAllBytes(schemaPath);
            BinaryPrimitives.WriteInt32LittleEndian(schema.AsSpan(8), 14);
            Assert.Throws<InvalidDataException>(() => TableSchemaCodec.Load(new MemoryStream(schema)));
        }
        using var reopened = OpenDatabase();
        Assert.Equal(639270596961230001, Assert.IsType<DateTimeOffset>(Query(reopened, "SELECT Stamp FROM offsets").Rows[0][0]).UtcTicks);
        Execute(reopened, "DROP TABLE offsets");
        Assert.Equal(15, TableSchemaCodec.ReadFormatVersion(schemaPath));
    }

    [Fact]
    public void Snapshot_DeclarationPrecheck_DoesNotValidateOrBypassPayloadChecksum()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "declaration.snapshot");
        var source = new SelectExecutionResult(["Stamp"], [new object?[] { DateTimeOffset.UnixEpoch }])
        {
            ColumnInfo = [new SelectColumnInfo(TableColumnType.DateTimeOffset)],
        };
        MaterializedViewSnapshotCodec.Save(path, source);
        byte[] bytes = File.ReadAllBytes(path);
        bytes[^16] ^= 1;
        File.WriteAllBytes(path, bytes);
        var declaration = MaterializedViewSnapshotCodec.LoadDeclaration(path, out int rowCount);
        Assert.Equal(1, rowCount);
        Assert.Empty(declaration.Rows);
        Assert.Equal(TableColumnType.DateTimeOffset, declaration.ColumnInfo![0].DataType);
        Assert.Contains("CRC", Assert.Throws<InvalidDataException>(() => MaterializedViewSnapshotCodec.Load(path)).Message);
    }

    [Fact]
    public void LegacyResult_ReturningNullAndSequence_RejectsBeforeSideEffects()
    {
        using var database = OpenDatabase();
        Execute(database, "CREATE TABLE offsets(id INT, Stamp DATETIMEOFFSET NULL, PRIMARY KEY(id))");
        Execute(database, "INSERT INTO offsets(id,Stamp) VALUES(1,NULL)");
        Execute(database, "CREATE SEQUENCE offset_numbers START WITH 10");
        foreach (string sql in new[]
        {
            "SELECT Stamp FROM offsets WHERE FALSE",
            "SELECT Stamp FROM offsets",
            "SELECT CAST(NULL AS DATETIMEOFFSET)",
            "INSERT INTO offsets(id,Stamp) VALUES(2,NULL) RETURNING Stamp",
            "UPDATE offsets SET Stamp=CAST(1 AS DATETIMEOFFSET) WHERE id=1 RETURNING *",
            "DELETE FROM offsets WHERE id=1 RETURNING Stamp",
            "SELECT nextval('offset_numbers'),CAST(NULL AS DATETIMEOFFSET)",
        })
            Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(database, null, sql, null, null,
                new SqlExecutionOptions { AllowDurationResults = true, AllowDateTimeOffsetResults = false }));
        Assert.Single(Query(database, "SELECT * FROM offsets").Rows);
        Assert.Null(Query(database, "SELECT Stamp FROM offsets").Rows[0][0]);
        Assert.Equal(10L, Query(database, "SELECT nextval('offset_numbers')").Rows[0][0]);
        Assert.Equal(TimeSpan.FromTicks(1), SqlExecutor.Execute(database, null, "SELECT CAST(1 AS DURATION)", null, null,
            new SqlExecutionOptions { AllowDurationResults = true, AllowDateTimeOffsetResults = false }) is SelectExecutionResult duration
            ? duration.Rows[0][0] : null);
    }

    [Fact]
    public void Comparison_ProcedureAndCreateCheck_RejectBeforePersistentSequenceOrSchema()
    {
        using var database = OpenDatabase();
        Execute(database, "CREATE TABLE offsets(id INT, Stamp DATETIMEOFFSET NULL, Legacy DATETIME NULL, PRIMARY KEY(id))");
        Execute(database, "CREATE SEQUENCE offset_numbers START WITH 10");
        Execute(database, "CREATE PROCEDURE invalid_offset() LANGUAGE SQL AS BEGIN SELECT nextval('offset_numbers'); SELECT Stamp FROM offsets WHERE Stamp=Legacy; END");
        Assert.ThrowsAny<Exception>(() => Execute(database, "CALL invalid_offset()"));
        Assert.Equal(10L, Query(database, "SELECT nextval('offset_numbers')").Rows[0][0]);
        Assert.Throws<InvalidOperationException>(() => Execute(database,
            "CREATE TABLE invalid_check(id INT, Stamp DATETIMEOFFSET, Legacy DATETIME, PRIMARY KEY(id), CONSTRAINT stamp_check CHECK(Stamp=Legacy))"));
        Assert.Null(database.Tables.Catalog.TryGet("invalid_check"));
    }

    [Theory]
    [InlineData("SELECT CASE WHEN TRUE THEN CAST(1 AS DATETIMEOFFSET) ELSE NULL END")]
    [InlineData("SELECT COALESCE(NULL,CAST(1 AS DATETIMEOFFSET))")]
    [InlineData("WITH RECURSIVE r(x) AS (SELECT CAST(1 AS DATETIMEOFFSET) UNION ALL SELECT x FROM r WHERE FALSE) SELECT x FROM r")]
    public void NativeExpressions_CaseCoalesceAndRecursiveCte_PreserveDeclaredType(string sql)
    {
        using var database = OpenDatabase();
        var result = Query(database, sql);
        Assert.Equal(new DateTimeOffset(1, TimeSpan.Zero), result.Rows[0][0]);
        Assert.Equal(TableColumnType.DateTimeOffset, result.ColumnInfo![0].DataType);
    }

    [Fact]
    public void OffsetFunctions_NativeDeclarationsAndNull_PreserveUtcType()
    {
        using var database = OpenDatabase();
        var result = Query(database, "SELECT CURRENT_DATETIME_OFFSET(),CURRENT_UTC_DATETIME_OFFSET(),DATE_ADD_DATETIME_OFFSET(NULL,1,'day'),DATE_ADD(CAST(1 AS DATETIMEOFFSET),1,'day')");
        Assert.All(result.ColumnInfo!, column => Assert.Equal(TableColumnType.DateTimeOffset, column.DataType));
        Assert.All(result.Rows[0].OfType<DateTimeOffset>(), value => Assert.Equal(TimeSpan.Zero, value.Offset));
        Assert.Null(result.Rows[0][2]);
        Assert.Equal(new DateTimeOffset(TimeSpan.TicksPerDay + 1, TimeSpan.Zero), result.Rows[0][3]);
    }

    [Fact]
    public void Declaration_RecursiveShadowAndTransitionTables_UseSourceScope()
    {
        using var database = OpenDatabase();
        Execute(database, "CREATE TABLE r(x DATETIME)");
        var recursive = Query(database, "WITH RECURSIVE r(x) AS (SELECT CAST(1 AS DATETIMEOFFSET) UNION ALL SELECT x FROM r WHERE FALSE) SELECT x FROM r");
        Assert.Equal(TableColumnType.DateTimeOffset, recursive.ColumnInfo![0].DataType);
        Execute(database, "CREATE TABLE offsets(id INT, Stamp DATETIMEOFFSET, PRIMARY KEY(id))");
        Execute(database, "CREATE TABLE audit_offsets(id INT, Stamp DATETIMEOFFSET, PRIMARY KEY(id))");
        Execute(database, "CREATE TRIGGER copied AFTER INSERT ON offsets REFERENCING NEW TABLE AS incoming FOR EACH STATEMENT LANGUAGE SQL AS BEGIN INSERT INTO audit_offsets(id,Stamp) SELECT id,Stamp FROM incoming WHERE Stamp=CAST(1 AS DATETIMEOFFSET); END");
        Execute(database, "INSERT INTO offsets(id,Stamp) VALUES(1,CAST(1 AS DATETIMEOFFSET))");
        Assert.Equal(new DateTimeOffset(1, TimeSpan.Zero), Query(database, "SELECT Stamp FROM audit_offsets").Rows[0][0]);
        Assert.Single(Query(database, "SELECT id FROM (SELECT id FROM offsets) AS offsets WHERE EXISTS (SELECT Stamp FROM offsets WHERE Stamp=CAST(1 AS DATETIMEOFFSET))").Rows);
    }

    [Theory]
    [InlineData("CAST(-1 AS DATETIMEOFFSET)")]
    [InlineData("CAST(3155378976000000000 AS DATETIMEOFFSET)")]
    [InlineData("CAST(1.5 AS DATETIMEOFFSET)")]
    [InlineData("CAST('12:34:56' AS DATETIMEOFFSET)")]
    [InlineData("CAST('2026-10-08T12:34:56+15:00' AS DATETIMEOFFSET)")]
    public void Cast_InvalidOrAmbiguousInput_Rejects(string expression)
    {
        using var database = OpenDatabase();
        Assert.Throws<InvalidOperationException>(() => Execute(database, "SELECT " + expression));
    }

    [Theory]
    [InlineData("after-barrier")]
    [InlineData("after-journal")]
    [InlineData("after-table:offsets")]
    [InlineData("after-catalog")]
    public void Migration_InjectedFailure_RestoresOldRowsAndRetainsV15(string failurePhase)
    {
        using (var database = OpenDatabase())
        {
            Execute(database, "CREATE TABLE offsets(id INT, Stamp INT, PRIMARY KEY(id))");
            Execute(database, "INSERT INTO offsets(id,Stamp) VALUES(1,1),(2,639270596961230001)");
            database.Tables.SchemaMigrationPhaseTestHook = phase =>
            {
                if (phase == failurePhase) throw new IOException("injected offset migration failure");
            };
            Assert.ThrowsAny<Exception>(() => Execute(database, "ALTER TABLE offsets ALTER COLUMN Stamp DATETIMEOFFSET"));
        }
        using var reopened = OpenDatabase();
        Assert.Equal(15, TableSchemaCodec.ReadFormatVersion(reopened.Tables.SchemaPath));
        Assert.Equal(1L, Query(reopened, "SELECT Stamp FROM offsets WHERE id=1").Rows[0][0]);
        Execute(reopened, "ALTER TABLE offsets ALTER COLUMN Stamp DATETIMEOFFSET");
        Assert.Equal(new DateTimeOffset(1, TimeSpan.Zero), Query(reopened, "SELECT Stamp FROM offsets WHERE id=1").Rows[0][0]);
    }

    [Fact]
    public void Backup_NativeRowsIndexesAndNull_RestoreExactTicksAndV15()
    {
        string backup = Path.Combine(_root, "backup");
        string restored = Path.Combine(_root, "restored");
        using (var database = Tsdb.Open(new TsdbOptions { RootDirectory = Path.Combine(_root, "source") }))
        {
            Execute(database, "CREATE TABLE offsets(id INT, Stamp DATETIMEOFFSET NULL, PRIMARY KEY(id))");
            Execute(database, "CREATE INDEX ix_stamp ON offsets(Stamp)");
            Execute(database, "INSERT INTO offsets(id,Stamp) VALUES(1,CAST(1 AS DATETIMEOFFSET)),(2,CAST(3155378975999999999 AS DATETIMEOFFSET)),(3,NULL)");
            new BackupService().Create(database, new BackupCreateOptions { DestinationDirectory = backup });
        }
        new BackupService().Restore(new BackupRestoreOptions { BackupDirectory = backup, TargetDirectory = restored });
        using var reopened = Tsdb.Open(new TsdbOptions { RootDirectory = restored });
        Assert.Equal(15, TableSchemaCodec.ReadFormatVersion(reopened.Tables.SchemaPath));
        Assert.Equal(new DateTimeOffset(1, TimeSpan.Zero), Query(reopened, "SELECT Stamp FROM offsets WHERE Stamp=CAST(1 AS DATETIMEOFFSET)").Rows[0][0]);
        Assert.Equal(new object?[] { new DateTimeOffset(1, TimeSpan.Zero), DateTimeOffset.MaxValue, null },
            Query(reopened, "SELECT Stamp FROM offsets ORDER BY id").Rows.Select(row => row[0]));
    }

    [Fact]
    public void Migration_BarrierIoFailure_LeavesOldWalAndNoJournal()
    {
        string temporary;
        using (var database = OpenDatabase())
        {
            Execute(database, "CREATE TABLE offsets(id INT, Stamp INT, PRIMARY KEY(id))");
            Execute(database, "INSERT INTO offsets(id,Stamp) VALUES(1,1)");
            temporary = database.Tables.SchemaPath + ".tmp";
            Directory.CreateDirectory(temporary);
            var store = database.Tables.Open("offsets");
            long wal = store.ActiveWalLengthForEvidence;
            Assert.ThrowsAny<Exception>(() => Execute(database, "ALTER TABLE offsets ALTER COLUMN Stamp DATETIMEOFFSET"));
            Assert.Equal(wal, store.ActiveWalLengthForEvidence);
            Assert.False(File.Exists(Path.Combine(_root, "tables", TableSchemaMigrationJournal.FileName)));
        }
        Directory.Delete(temporary);
        using var reopened = OpenDatabase();
        Assert.True(TableSchemaCodec.ReadFormatVersion(reopened.Tables.SchemaPath) < 15);
        Assert.Equal(1L, Query(reopened, "SELECT Stamp FROM offsets").Rows[0][0]);
        Execute(reopened, "ALTER TABLE offsets ALTER COLUMN Stamp DATETIMEOFFSET");
        Assert.Equal(new DateTimeOffset(1, TimeSpan.Zero), Query(reopened, "SELECT Stamp FROM offsets").Rows[0][0]);
    }

    [Fact]
    public void Create_PublishFailureAndRetry_RetainsV15WithoutPartialTable()
    {
        using (var database = OpenDatabase())
        {
            database.Tables.AfterCatalogPersistedBeforePublishTestHook = () => throw new IOException("injected offset publish failure");
            Assert.Throws<IOException>(() => Execute(database, "CREATE TABLE offsets(id INT, Stamp DATETIMEOFFSET, PRIMARY KEY(id))"));
            Assert.Empty(database.Tables.Catalog.Snapshot());
            Assert.Equal(15, TableSchemaCodec.ReadFormatVersion(database.Tables.SchemaPath));
        }
        using var reopened = OpenDatabase();
        Assert.Empty(reopened.Tables.Catalog.Snapshot());
        Execute(reopened, "CREATE TABLE offsets(id INT, Stamp DATETIMEOFFSET, PRIMARY KEY(id))");
        Execute(reopened, "INSERT INTO offsets(id,Stamp) VALUES(1,CAST(1 AS DATETIMEOFFSET))");
        Assert.Equal(new DateTimeOffset(1, TimeSpan.Zero), Query(reopened, "SELECT Stamp FROM offsets").Rows[0][0]);
    }

    [Theory]
    [InlineData("SELECT Stamp FROM offsets ORDER BY Stamp DESC")]
    [InlineData("SELECT DISTINCT Stamp FROM offsets ORDER BY Stamp")]
    [InlineData("SELECT Stamp,COUNT(*) FROM offsets GROUP BY Stamp ORDER BY Stamp")]
    [InlineData("SELECT a.id,b.id FROM offsets a JOIN offsets b ON a.Stamp=b.Stamp ORDER BY a.id,b.id")]
    public void BlockingOperators_ForcedSpill_PreserveExactInstantsAndReleaseResources(string sql)
    {
        using var database = OpenDatabase();
        Execute(database, "CREATE TABLE offsets(id INT, Stamp DATETIMEOFFSET, PRIMARY KEY(id))");
        string rows = string.Join(',', Enumerable.Range(1, 40).Select(id => $"({id},CAST({639270596961230000 + id % 20} AS DATETIMEOFFSET))"));
        Execute(database, "INSERT INTO offsets(id,Stamp) VALUES" + rows);
        var expected = Run(1024 * 1024, out var memory);
        var actual = Run(96, out var spill);
        Assert.Equal(0, memory.SpillCount);
        Assert.True(spill.SpillCount > 0);
        Assert.True(spill.SpillBytes > 0);
        Assert.Equal(expected.Rows.Count, actual.Rows.Count);
        for (int index = 0; index < actual.Rows.Count; index++)
            Assert.Equal(expected.Rows[index], actual.Rows[index]);
        Assert.Equal(0, database.SqlMemoryBudget.ReservedBytes);
        string path = Path.Combine(_root, SqlSpillWorkspace.DirectoryName);
        if (Directory.Exists(path)) Assert.Empty(Directory.EnumerateDirectories(path, "query-*"));

        SelectExecutionResult Run(long limit, out SqlExecutionMetricsSnapshot snapshot)
        {
            var metrics = new SqlExecutionMetrics();
            using var telemetry = SqlExecutionTelemetry.Enter(metrics);
            var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, null, sql, null, null,
                new SqlExecutionOptions { BlockingOperatorMemoryLimitBytes = limit }));
            snapshot = metrics.Complete();
            return result;
        }
    }

    private static void AssertNativeMetadata(DbDataReader reader, int ordinal)
    {
        Assert.Equal(typeof(DateTimeOffset), reader.GetFieldType(ordinal));
        Assert.Equal(typeof(DateTimeOffset), reader.GetColumnSchema()[ordinal].DataType);
        Assert.Equal(typeof(DateTimeOffset), reader.GetSchemaTable()!.Rows[ordinal][SchemaTableColumn.DataType]);
    }

    private SndbConnection OpenConnection()
    {
        var connection = new SndbConnection($"Data Source={_root}");
        connection.Open();
        return connection;
    }

    private Tsdb OpenDatabase() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });
    private static object? Execute(Tsdb database, string sql) => SqlExecutor.Execute(database, null, sql);
    private static SelectExecutionResult Query(Tsdb database, string sql) => Assert.IsType<SelectExecutionResult>(Execute(database, sql));
}
