using System.Buffers.Binary;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using SonnetDB.Views;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlDateTime2Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sndb-datetime2-{Guid.NewGuid():N}");

    [Fact]
    public void DateTime2_ParameterizedInsertAndReopen_PreservesTicksAndDeclaration()
    {
        DateTime value = new DateTime(2026, 10, 8, 1, 2, 3, DateTimeKind.Utc).AddTicks(4_567_890);
        using (var db = Open())
        {
            Execute(db, "CREATE TABLE events(id INT, occurred DATETIME2, PRIMARY KEY(id))");
            Execute(db, "INSERT INTO events(id,occurred) VALUES(1,@stamp)",
                new SqlParameters().AddNamed("stamp", value));
            SelectExecutionResult result = Rows(db, "SELECT occurred FROM events");
            Assert.Equal(value.Ticks, Assert.IsType<DateTime>(Assert.Single(result.Rows)[0]).Ticks);
            Assert.Equal(DateTimeKind.Utc, Assert.IsType<DateTime>(result.Rows[0][0]).Kind);
            Assert.Equal(TableColumnType.DateTime2, Assert.Single(result.ColumnInfo!).DataType);
            Assert.Equal(17, TableSchemaCodec.ReadFormatVersion(db.Tables.SchemaPath));
        }

        using var reopened = Open();
        DateTime reopenedValue = Assert.IsType<DateTime>(Assert.Single(Rows(reopened, "SELECT occurred FROM events").Rows)[0]);
        Assert.Equal(value.Ticks, reopenedValue.Ticks);
        Assert.Equal(DateTimeKind.Utc, reopenedValue.Kind);
    }

    [Fact]
    public void DateTime2_AdjacentTicks_AreDistinctInEqualityAndOrder()
    {
        DateTime first = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1);
        using var db = Open();
        Execute(db, "CREATE TABLE events(id INT, occurred DATETIME2, PRIMARY KEY(id))");
        Execute(db, "INSERT INTO events(id,occurred) VALUES(1,@a),(2,@b)",
            new SqlParameters().AddNamed("a", first).AddNamed("b", first.AddTicks(1)));

        SelectExecutionResult equal = Rows(db, "SELECT id FROM events WHERE occurred=@stamp",
            new SqlParameters().AddNamed("stamp", first.AddTicks(1)));
        Assert.Equal([new object?[] { 2L }], equal.Rows);
        SelectExecutionResult ordered = Rows(db, "SELECT occurred FROM events ORDER BY occurred");
        Assert.Equal([first.Ticks, first.AddTicks(1).Ticks], ordered.Rows.Select(static row => Assert.IsType<DateTime>(row[0]).Ticks));
    }

    [Fact]
    public void LegacyDateTime_SubMillisecondParameter_IsRejectedBeforeWrite()
    {
        DateTime value = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1);
        using var db = Open();
        Execute(db, "CREATE TABLE events(id INT, occurred DATETIME, PRIMARY KEY(id))");
        Assert.Throws<InvalidOperationException>(() => Execute(db,
            "INSERT INTO events(id,occurred) VALUES(1,@stamp)",
            new SqlParameters().AddNamed("stamp", value)));
        Assert.Empty(Rows(db, "SELECT id FROM events").Rows);
    }

    [Fact]
    public void DateTime2_RowAndCoveredIndexCodecs_PreserveTicks()
    {
        TableSchema schema = TableSchema.Create(
            "events",
            [("id", TableColumnType.Int64, false), ("occurred", TableColumnType.DateTime2, false)],
            ["id"],
            [new TableIndexDefinition("ix_occurred", ["occurred"], false)]);
        DateTime value = new DateTime(1, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(1);
        object?[] row = [1L, value];
        object?[] decoded = TableRowCodec.Decode(schema, TableRowCodec.Encode(schema, row));
        Assert.Equal(value.Ticks, Assert.IsType<DateTime>(decoded[1]).Ticks);

        TableIndex index = schema.Indexes[0];
        byte[] primaryKey = TableKeyCodec.EncodePrimaryKey(schema, row);
        object?[] covered = TableIndexCodec.DecodeCoveredValues(
            index,
            schema,
            TableIndexCodec.EncodeIndexEntryKey(index, row, schema, primaryKey),
            primaryKey,
            TableIndexCodec.EncodeLookupPrefix(index, [], schema)!);
        Assert.Equal(value.Ticks, Assert.IsType<DateTime>(covered[1]).Ticks);

        TableSchema keySchema = TableSchema.Create(
            "datetime_keys",
            [("occurred", TableColumnType.DateTime2, false)],
            ["occurred"]);
        byte[] encodedKey = TableKeyCodec.EncodePrimaryKey(keySchema, [value]);
        Assert.True(TableKeyCodec.IsEncodedPrimaryKey(keySchema, encodedKey));
    }

    [Fact]
    public void DateTime2_DateAdd_PreservesSubMillisecondTicks()
    {
        DateTime value = new DateTime(2026, 10, 8, 1, 2, 3, DateTimeKind.Utc).AddTicks(4_567_890);
        using var db = Open();
        Execute(db, "CREATE TABLE events(id INT, occurred DATETIME2, PRIMARY KEY(id))");
        Execute(db, "INSERT INTO events(id,occurred) VALUES(1,@stamp)", new SqlParameters().AddNamed("stamp", value));
        SelectExecutionResult result = Rows(db, "SELECT date_add_datetime(occurred,1,'microsecond') FROM events");
        DateTime actual = Assert.IsType<DateTime>(Assert.Single(result.Rows)[0]);
        Assert.Equal(value.AddTicks(TimeSpan.TicksPerMicrosecond).Ticks, actual.Ticks);
    }

    [Fact]
    public void DateTime2_ToLegacyDateTimeMigration_RejectsLossAndKeepsRows()
    {
        DateTime value = new DateTime(2026, 10, 8, 1, 2, 3, DateTimeKind.Utc).AddTicks(1);
        using var db = Open();
        Execute(db, "CREATE TABLE events(id INT, occurred DATETIME2, PRIMARY KEY(id))");
        Execute(db, "INSERT INTO events(id,occurred) VALUES(1,@stamp)", new SqlParameters().AddNamed("stamp", value));

        var error = Assert.Throws<InvalidOperationException>(() => Execute(db,
            "ALTER TABLE events ALTER COLUMN occurred TYPE DATETIME"));
        Assert.Contains("毫秒", error.Message, StringComparison.Ordinal);
        DateTime actual = Assert.IsType<DateTime>(Assert.Single(Rows(db, "SELECT occurred FROM events").Rows)[0]);
        Assert.Equal(value.Ticks, actual.Ticks);
        Assert.Equal(TableColumnType.DateTime2, db.Tables.Catalog.TryGet("events")!.TryGetColumn("occurred")!.DataType);
    }

    [Theory]
    [InlineData(1024 * 1024)]
    [InlineData(96)]
    public void LegacyDateTime_HashAndSpillJoinWithUnixMilliseconds_PreservesEquality(long budget)
    {
        using var db = Open();
        Execute(db, "CREATE TABLE dates(id INT, stamp DATETIME, PRIMARY KEY(id))");
        Execute(db, "CREATE TABLE milliseconds(id INT, stamp INT, PRIMARY KEY(id))");
        long[] values = [-1, 0, 1, 1_760_000_000_123];
        for (int index = 0; index < values.Length; index++)
        {
            db.Tables.Open("dates").Insert([(long)index, DateTimeOffset.FromUnixTimeMilliseconds(values[index]).UtcDateTime]);
            db.Tables.Open("milliseconds").Insert([(long)index + 100, values[index]]);
        }

        var metrics = new SqlExecutionMetrics();
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, null,
            "SELECT d.id,m.id FROM dates d JOIN milliseconds m ON d.stamp=m.stamp ORDER BY d.id",
            null, null, new SqlExecutionOptions { BlockingOperatorMemoryLimitBytes = budget, Metrics = metrics }));

        Assert.Equal(4, result.Rows.Count);
        for (int index = 0; index < values.Length; index++)
            Assert.Equal(new object?[] { (long)index, (long)index + 100 }, result.Rows[index]);
        Assert.Equal(budget == 96, metrics.Complete().SpillCount > 0);
        Assert.Equal(0, db.SqlMemoryBudget.ReservedBytes);
    }

    [Theory]
    [InlineData("SELECT occurred FROM events WHERE occurred=legacy LIMIT 0")]
    [InlineData("SELECT occurred FROM events WHERE FALSE AND occurred=legacy")]
    [InlineData("SELECT occurred FROM events WHERE occurred=raw")]
    [InlineData("SELECT occurred FROM events WHERE occurred='2026-01-01T00:00:00Z'")]
    [InlineData("SELECT occurred FROM events WHERE occurred=0")]
    [InlineData("SELECT occurred FROM events WHERE occurred IN (legacy)")]
    [InlineData("SELECT occurred FROM events WHERE occurred IN (SELECT legacy FROM events)")]
    [InlineData("SELECT CASE WHEN TRUE THEN occurred ELSE legacy END FROM events LIMIT 0")]
    [InlineData("SELECT COALESCE(occurred,legacy) FROM events LIMIT 0")]
    [InlineData("SELECT occurred FROM events WHERE FALSE UNION SELECT legacy FROM events WHERE FALSE")]
    [InlineData("WITH source AS (SELECT occurred,legacy FROM events) SELECT occurred FROM source WHERE occurred=legacy")]
    [InlineData("SELECT a.id FROM events a JOIN events b ON a.occurred=b.legacy LIMIT 0")]
    [InlineData("SELECT date_diff('tick',occurred,legacy) FROM events LIMIT 0")]
    [InlineData("UPDATE events SET occurred=0 WHERE FALSE")]
    [InlineData("UPDATE events SET occurred=legacy WHERE FALSE")]
    [InlineData("INSERT INTO targets(occurred) SELECT legacy FROM events WHERE FALSE")]
    public void DateTime2_IncompatibleDeclarations_RejectBeforeEmptyScanAndShortCircuit(string sql)
    {
        using var db = Open();
        Execute(db, "CREATE TABLE events(id INT, occurred DATETIME2, legacy DATETIME, raw INT, PRIMARY KEY(id))");
        Execute(db, "CREATE TABLE targets(occurred DATETIME2)");
        Assert.Throws<InvalidOperationException>(() => Execute(db, sql));
        Execute(db, "INSERT INTO events(id) VALUES(1)");
        Assert.Throws<InvalidOperationException>(() => Execute(db, sql));
        Assert.Null(Assert.Single(Rows(db, "SELECT occurred FROM events").Rows)[0]);
        Assert.Empty(Rows(db, "SELECT occurred FROM targets").Rows);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("'2026-01-01T00:00:00.0000001Z'")]
    [InlineData("CAST(0 AS DATETIME)")]
    [InlineData("CAST(0 AS DATETIMEOFFSET)")]
    public void DateTime2_ImplicitInput_RejectsBeforeBatchIdentityAndSequence(string value)
    {
        using var db = Open();
        Execute(db, "CREATE SEQUENCE stamps START WITH 10");
        Execute(db, "CREATE TABLE events(id INT AUTO_INCREMENT, ticket INT DEFAULT nextval('stamps'), occurred DATETIME2, PRIMARY KEY(id))");
        var parameters = new SqlParameters().AddNamed("stamp", DateTime.UnixEpoch.AddTicks(1));
        Assert.Throws<InvalidOperationException>(() => Execute(db,
            $"INSERT INTO events(occurred) VALUES(@stamp),({value})", parameters));
        Assert.Empty(Rows(db, "SELECT id FROM events").Rows);
        Execute(db, "INSERT INTO events(occurred) VALUES(@stamp)", parameters);
        Assert.Equal(new object?[] { 1L, 10L }, Assert.Single(Rows(db, "SELECT id,ticket FROM events").Rows));
    }

    [Theory]
    [InlineData("SELECT id FROM dates WHERE stamp=@stamp LIMIT 0")]
    [InlineData("SELECT id FROM dates WHERE FALSE AND stamp=@stamp")]
    [InlineData("UPDATE dates SET id=id WHERE stamp=@stamp")]
    [InlineData("DELETE FROM dates WHERE stamp=@stamp")]
    public void LegacyDateTime_SubMillisecondCondition_RejectsBeforeEmptyScan(string sql)
    {
        using var db = Open();
        Execute(db, "CREATE TABLE dates(id INT, stamp DATETIME, PRIMARY KEY(id))");
        Assert.Throws<InvalidOperationException>(() => Execute(db, sql,
            new SqlParameters().AddNamed("stamp", DateTime.UnixEpoch.AddTicks(1))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyDateTime_AlignedDefaultParameter_RemainsCompatibleWithScanAndIndex(bool indexed)
    {
        using var db = Open();
        Execute(db, "CREATE TABLE dates(id INT, stamp DATETIME, PRIMARY KEY(id))");
        if (indexed) Execute(db, "CREATE INDEX ix_stamp ON dates(stamp)");
        var parameters = new SqlParameters().AddNamed("stamp", DateTime.UnixEpoch.AddMilliseconds(-1));
        Execute(db, "INSERT INTO dates(id,stamp) VALUES(1,@stamp)", parameters);
        Assert.Equal(1L, Assert.Single(Rows(db, "SELECT id FROM dates WHERE stamp=@stamp", parameters).Rows)[0]);
    }

    [Theory]
    [InlineData(1024 * 1024)]
    [InlineData(96)]
    public void DateTime2_HashJoinGroupingDistinctAndMinMax_PreserveAdjacentTicks(long budget)
    {
        using var db = Open();
        Execute(db, "CREATE TABLE precise(id INT, stamp DATETIME2, PRIMARY KEY(id))");
        DateTime start = DateTime.UnixEpoch.AddTicks(-1);
        for (int index = 0; index < 4; index++)
            Execute(db, "INSERT INTO precise(id,stamp) VALUES(@id,@stamp)",
                new SqlParameters().AddNamed("id", index).AddNamed("stamp", start.AddTicks(index)));
        var options = new SqlExecutionOptions { BlockingOperatorMemoryLimitBytes = budget };
        SelectExecutionResult Query(string sql) => Assert.IsType<SelectExecutionResult>(
            SqlExecutor.Execute(db, null, sql, null, null, options));

        Assert.Equal(4, Query("SELECT a.id,b.id FROM precise a JOIN precise b ON a.stamp=b.stamp").Rows.Count);
        Assert.Equal(4, Query("SELECT DISTINCT stamp FROM precise").Rows.Count);
        Assert.Equal(4, Query("SELECT stamp,COUNT(*) FROM precise GROUP BY stamp").Rows.Count);
        var extrema = Assert.Single(Query("SELECT MIN(stamp),MAX(stamp) FROM precise").Rows);
        Assert.Equal(start.Ticks, Assert.IsType<DateTime>(extrema[0]).Ticks);
        Assert.Equal(start.AddTicks(3).Ticks, Assert.IsType<DateTime>(extrema[1]).Ticks);
    }

    [Fact]
    public void DateTime2_PrimaryAndUniqueKeysAndRange_PreserveAdjacentTicksAfterReopen()
    {
        DateTime start = DateTime.UnixEpoch.AddTicks(-1);
        using (var db = Open())
        {
            Execute(db, "CREATE TABLE precise(stamp DATETIME2, label STRING, PRIMARY KEY(stamp))");
            Execute(db, "CREATE UNIQUE INDEX ux_stamp ON precise(stamp DESC) INCLUDE(label)");
            for (int index = 0; index < 4; index++)
                Execute(db, "INSERT INTO precise(stamp,label) VALUES(@stamp,@label)",
                    new SqlParameters().AddNamed("stamp", start.AddTicks(index)).AddNamed("label", index.ToString()));
        }
        using var reopened = Open();
        var result = Rows(reopened, "SELECT stamp,label FROM precise WHERE stamp>=@from AND stamp<@to ORDER BY stamp DESC",
            new SqlParameters().AddNamed("from", start.AddTicks(1)).AddNamed("to", start.AddTicks(3)));
        Assert.Equal([start.AddTicks(2).Ticks, start.AddTicks(1).Ticks],
            result.Rows.Select(static row => Assert.IsType<DateTime>(row[0]).Ticks));
        Assert.Equal(["2", "1"], result.Rows.Select(static row => (string)row[1]!));
        Assert.Throws<TableConstraintException>(() => Execute(reopened,
            "INSERT INTO precise(stamp,label) VALUES(@stamp,'duplicate')", new SqlParameters().AddNamed("stamp", start)));
    }

    [Fact]
    public void DateTime2_CatalogFloor_Remains17AfterLastPreciseTableIsRemoved()
    {
        using (var db = Open())
        {
            Execute(db, "CREATE TABLE precise(id DATETIME2, PRIMARY KEY(id))");
            byte[] catalog = File.ReadAllBytes(db.Tables.SchemaPath);
            BinaryPrimitives.WriteInt32LittleEndian(catalog.AsSpan(8), 16);
            string fakeLegacy = Path.Combine(_root, "invalid-legacy.tblschema");
            File.WriteAllBytes(fakeLegacy, catalog);
            Assert.Throws<InvalidDataException>(() => TableSchemaCodec.Load(fakeLegacy));
            Execute(db, "DROP TABLE precise");
            Assert.Equal(17, TableSchemaCodec.ReadFormatVersion(db.Tables.SchemaPath));
        }
        using (var reopened = Open())
        {
            Assert.Empty(reopened.Tables.Catalog.Snapshot());
            Assert.Equal(17, TableSchemaCodec.ReadFormatVersion(reopened.Tables.SchemaPath));
            Execute(reopened, "CREATE TABLE ordinary(id INT, PRIMARY KEY(id))");
            Assert.Equal(17, TableSchemaCodec.ReadFormatVersion(reopened.Tables.SchemaPath));
        }
        using var again = Open();
        Assert.Equal(17, TableSchemaCodec.ReadFormatVersion(again.Tables.SchemaPath));
        Assert.Equal(TableColumnType.Int64, again.Tables.Catalog.TryGet("ordinary")!.Columns[0].DataType);
    }

    [Fact]
    public void LegacyDateTime_FrozenMillisecondRow_RetainsExactBytesAndReadPrecision()
    {
        var schema = TableSchema.Create("legacy", [("stamp", TableColumnType.DateTime, false)], ["stamp"]);
        byte[] frozen = Convert.FromHexString("01FFFFFFFFFFFFFFFF");
        DateTime expected = DateTimeOffset.FromUnixTimeMilliseconds(-1).UtcDateTime;
        DateTime decoded = Assert.IsType<DateTime>(Assert.Single(TableRowCodec.Decode(schema, frozen)));
        Assert.Equal(expected.Ticks, decoded.Ticks);
        Assert.Equal(DateTimeKind.Utc, decoded.Kind);
        Assert.Equal(frozen, TableRowCodec.Encode(schema, [expected]));
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void DateTime2_ParameterKinds_NormalizeUtcWithoutLosingTicks(DateTimeKind kind)
    {
        using var db = Open();
        DateTime input = new DateTime(2026, 10, 8, 1, 2, 3, kind).AddTicks(4_567_890);
        long expected = kind == DateTimeKind.Unspecified ? input.Ticks : input.ToUniversalTime().Ticks;
        var result = Rows(db, "SELECT @stamp", new SqlParameters().AddNamed("stamp", input));
        DateTime actual = Assert.IsType<DateTime>(Assert.Single(result.Rows)[0]);
        Assert.Equal(expected, actual.Ticks);
        Assert.Equal(DateTimeKind.Utc, actual.Kind);
        Assert.Equal(TableColumnType.DateTime2, Assert.Single(result.ColumnInfo!).DataType);
    }

    [Fact]
    public void DateTime2_ExtremeAndNullValues_RoundTripFullClrRange()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE precise(id INT, stamp DATETIME2, PRIMARY KEY(id))");
        var empty = Rows(db, "SELECT stamp,CAST(NULL AS DATETIME2),@stamp FROM precise",
            new SqlParameters().AddNamed("stamp", null, SqlDataType.DateTime2));
        Assert.Empty(empty.Rows);
        Assert.All(empty.ColumnInfo!, static info => Assert.Equal(TableColumnType.DateTime2, info.DataType));
        DateTime[] values = [DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc),
            DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc), DateTime.UnixEpoch.AddTicks(-1), DateTime.UnixEpoch.AddTicks(1)];
        for (int index = 0; index < values.Length; index++)
            Execute(db, "INSERT INTO precise(id,stamp) VALUES(@id,@stamp)",
                new SqlParameters().AddNamed("id", index).AddNamed("stamp", values[index]));
        Execute(db, "INSERT INTO precise(id,stamp) VALUES(4,@stamp)",
            new SqlParameters().AddNamed("stamp", DBNull.Value, SqlDataType.DateTime2));
        var rows = Rows(db, "SELECT stamp FROM precise ORDER BY id").Rows;
        Assert.Equal(values.Select(static value => value.Ticks), rows.Take(4).Select(static row => Assert.IsType<DateTime>(row[0]).Ticks));
        Assert.Null(rows[4][0]);
    }

    [Fact]
    public void DateTime2_ExplicitCasts_AcceptTicksAndIsoTextWhileNativeInputsStayStrict()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE precise(stamp DATETIME2, PRIMARY KEY(stamp))");
        Execute(db, "INSERT INTO precise(stamp) VALUES(CAST(1 AS DATETIME2)),(CAST('1970-01-01T00:00:00.0000001Z' AS DATETIME2))");
        Assert.Equal([1L, DateTime.UnixEpoch.AddTicks(1).Ticks],
            Rows(db, "SELECT stamp FROM precise ORDER BY stamp").Rows.Select(static row => Assert.IsType<DateTime>(row[0]).Ticks));
        var schema = db.Tables.Catalog.TryGet("precise")!;
        Assert.Throws<InvalidOperationException>(() => TableRowCodec.Encode(schema, [1L]));
        Assert.Throws<InvalidOperationException>(() => TableKeyCodec.EncodePrimaryKey(schema, [1L]));
        Assert.Throws<InvalidOperationException>(() => TableRowCodec.Encode(schema, ["1970-01-01T00:00:00Z"]));
    }

    [Fact]
    public void DateTime2_LegacyPrimaryKeyMigration_ReencodesRowsAndAllIndexes()
    {
        DateTime value = DateTime.UnixEpoch.AddMilliseconds(-1);
        using (var db = Open())
        {
            Execute(db, "CREATE TABLE precise(stamp DATETIME, label STRING, PRIMARY KEY(stamp))");
            Execute(db, "CREATE UNIQUE INDEX ux_stamp ON precise(stamp DESC) INCLUDE(label)");
            Execute(db, "INSERT INTO precise(stamp,label) VALUES(-1,'legacy')");
            Execute(db, "ALTER TABLE precise ALTER COLUMN stamp TYPE DATETIME2");
            Assert.Equal(TableColumnType.DateTime2, db.Tables.Catalog.TryGet("precise")!.TryGetColumn("stamp")!.DataType);
            Execute(db, "INSERT INTO precise(stamp,label) VALUES(@stamp,'adjacent')",
                new SqlParameters().AddNamed("stamp", value.AddTicks(1)));
        }
        using var reopened = Open();
        var rows = Rows(reopened, "SELECT stamp,label FROM precise WHERE stamp>=@stamp ORDER BY stamp",
            new SqlParameters().AddNamed("stamp", value)).Rows;
        Assert.Equal([value.Ticks, value.AddTicks(1).Ticks], rows.Select(static row => Assert.IsType<DateTime>(row[0]).Ticks));
        Assert.Equal(["legacy", "adjacent"], rows.Select(static row => (string)row[1]!));
    }

    [Fact]
    public void DateTime2_MaterializedSnapshot_UsesV5AndPreservesDeclarationAndTicks()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "snapshot.bin");
        DateTime value = DateTime.UnixEpoch.AddTicks(-1);
        var result = new SelectExecutionResult(["stamp"], [new object?[] { value }])
        { ColumnInfo = [new SelectColumnInfo(TableColumnType.DateTime2, false)] };
        MaterializedViewSnapshotCodec.Save(path, result);
        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8)));
        var decoded = MaterializedViewSnapshotCodec.Load(path);
        Assert.Equal(value.Ticks, Assert.IsType<DateTime>(Assert.Single(decoded.Rows)[0]).Ticks);
        Assert.Equal(TableColumnType.DateTime2, Assert.Single(decoded.ColumnInfo!).DataType);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), 4);
        File.WriteAllBytes(path, bytes);
        Assert.Throws<InvalidDataException>(() => MaterializedViewSnapshotCodec.Load(path));
    }

    [Theory]
    [InlineData("CASE WHEN FALSE THEN @aligned ELSE @precise END")]
    [InlineData("COALESCE(@aligned,@precise)")]
    public void LegacyDateTime_ParameterizedBranchMerge_DoesNotReuseOneBranchesAlignment(string expression)
    {
        using var db = Open();
        Execute(db, "CREATE TABLE dates(id INT, stamp DATETIME, PRIMARY KEY(id))");
        Assert.Throws<InvalidOperationException>(() => Execute(db,
            $"SELECT id FROM dates WHERE stamp=({expression}) LIMIT 0",
            new SqlParameters().AddNamed("aligned", DateTime.UnixEpoch).AddNamed("precise", DateTime.UnixEpoch.AddTicks(1))));
    }

    [Fact]
    public void DateTime2_SubqueryAndOffsetFunction_KeepTheirDeclaredTypes()
    {
        using var db = Open();
        Execute(db, "CREATE TABLE source(id INT, stamp DATETIME2, PRIMARY KEY(id))");
        Execute(db, "CREATE TABLE target(id INT, stamp DATETIME2, PRIMARY KEY(id))");
        DateTime value = DateTime.UnixEpoch.AddTicks(1);
        Execute(db, "INSERT INTO source(id,stamp) VALUES(1,@stamp)", new SqlParameters().AddNamed("stamp", value));
        Execute(db, "INSERT INTO target(id,stamp) SELECT id,stamp FROM source");
        var result = Rows(db, "SELECT stamp FROM target WHERE stamp=(SELECT stamp FROM source WHERE id=1)");
        Assert.Equal(value.Ticks, Assert.IsType<DateTime>(Assert.Single(result.Rows)[0]).Ticks);
        Assert.Equal(TableColumnType.DateTime2, Assert.Single(result.ColumnInfo!).DataType);
        var offset = Rows(db, "SELECT date_add_datetime_offset(stamp,1,'tick') FROM target");
        Assert.Equal(TableColumnType.DateTimeOffset, Assert.Single(offset.ColumnInfo!).DataType);
        Assert.Equal(value.AddTicks(1).Ticks, Assert.IsType<DateTimeOffset>(Assert.Single(offset.Rows)[0]).UtcTicks);
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    [InlineData(DateTimeKind.Local)]
    public void Measurement_DateTimeTimeInput_RetainsMillisecondsWhileRelationKeepsTicks(DateTimeKind kind)
    {
        using var db = Open();
        Execute(db, "CREATE MEASUREMENT readings(value FIELD INT)");
        Execute(db, "CREATE TABLE precise(id INT, stamp DATETIME2, PRIMARY KEY(id))");
        DateTime stamp = new DateTime(2026, 10, 8, 1, 2, 3, kind).AddTicks(4_567_890);
        DateTime utc = kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(stamp, DateTimeKind.Utc) : stamp.ToUniversalTime();
        var parameters = new SqlParameters().AddNamed("stamp", stamp);
        Execute(db, "INSERT INTO readings(time,value) VALUES(@stamp,1)", parameters);
        Execute(db, "INSERT INTO precise(id,stamp) VALUES(1,@stamp)", parameters);
        Assert.Equal(new DateTimeOffset(utc).ToUnixTimeMilliseconds(),
            Assert.Single(Rows(db, "SELECT time FROM readings WHERE time=@stamp", parameters).Rows)[0]);
        Assert.Equal(utc.Ticks, Assert.IsType<DateTime>(Assert.Single(Rows(db, "SELECT stamp FROM precise").Rows)[0]).Ticks);
    }

    [Fact]
    public void Measurement_TimeParameters_ValidateBeforeEmptySelectAndDelete()
    {
        using var db = Open();
        Execute(db, "CREATE MEASUREMENT readings(value FIELD INT)");
        var parameters = new SqlParameters().AddNamed("stamp", DateTime.UnixEpoch.AddTicks(1));
        Assert.Empty(Rows(db, "SELECT time FROM readings WHERE time>=@stamp LIMIT 0", parameters).Rows);
        Execute(db, "DELETE FROM readings WHERE time>=@stamp", parameters);
        Assert.Empty(Rows(db, "SELECT time FROM readings").Rows);
        Assert.Throws<InvalidOperationException>(() => Execute(db,
            "SELECT time FROM readings WHERE time=CAST(1 AS DATETIME2) LIMIT 0"));
    }

    [Fact]
    public void Measurement_TimeParameterRangeAndDelete_MatchesUnixIntegerControl()
    {
        using var db = Open();
        Execute(db, "CREATE MEASUREMENT readings(value FIELD INT)");
        DateTime stamp = new DateTime(2026, 10, 8, 1, 2, 3, DateTimeKind.Utc).AddTicks(4_567_890);
        long ms = new DateTimeOffset(stamp).ToUnixTimeMilliseconds();
        for (int index = 0; index < 3; index++)
            Execute(db, "INSERT INTO readings(time,value) VALUES(@stamp,@value)",
                new SqlParameters().AddNamed("stamp", stamp.AddMilliseconds(index)).AddNamed("value", index));
        var parameters = new SqlParameters().AddNamed("from", stamp).AddNamed("to", stamp.AddMilliseconds(2));
        Assert.Equal([ms, ms + 1], Rows(db, "SELECT time FROM readings WHERE time>=@from AND time<@to ORDER BY time", parameters)
            .Rows.Select(static row => (long)row[0]!));
        Assert.Equal([ms, ms + 1], Rows(db, "SELECT time FROM readings WHERE @from<=time AND time<@to ORDER BY time", parameters)
            .Rows.Select(static row => (long)row[0]!));
        Assert.Equal([ms, ms + 1], Rows(db, "SELECT time FROM readings WHERE time IN (@from,@to) ORDER BY time",
            new SqlParameters().AddNamed("from", stamp).AddNamed("to", stamp.AddMilliseconds(1)))
            .Rows.Select(static row => (long)row[0]!));
        Assert.Equal([ms, ms + 1], Rows(db, $"SELECT time FROM readings WHERE time>={ms} AND time<{ms + 2} ORDER BY time")
            .Rows.Select(static row => (long)row[0]!));
        Execute(db, "DELETE FROM readings WHERE time>=@from AND time<@to", parameters);
        Assert.Equal(ms + 2, Assert.Single(Rows(db, "SELECT time FROM readings").Rows)[0]);
    }

    private Tsdb Open()
    {
        Directory.CreateDirectory(_root);
        return Tsdb.Open(new TsdbOptions { RootDirectory = _root });
    }

    private static void Execute(Tsdb db, string sql, SqlParameters? parameters = null)
        => SqlExecutor.Execute(db, "default", sql, parameters, null);

    private static SelectExecutionResult Rows(Tsdb db, string sql, SqlParameters? parameters = null)
        => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, "default", sql, parameters, null));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
