using SonnetDB.ObjectStorage;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using SonnetDB.Wal;
using Xunit;

namespace SonnetDB.Core.Tests.Compatibility;

public sealed class Release40RecordCompatibilityTests
{
    [Fact]
    public void ObjectRead_PreviousConstructorAndDeconstruct_PreserveRangeAndStream()
    {
        var info = new SndbObjectInfo("bucket", "key", "version", "text/plain", 12, "etag", "hash", false,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, new Dictionary<string, string>(), new Dictionary<string, string>());
        using var stream = new MemoryStream([1, 2]);
        var read = new SndbObjectReadResult(info, stream, 3, 2, true);
        var (actualInfo, actualStream, offset, length, isRange) = read;
        Assert.Same(info, actualInfo);
        Assert.Same(stream, actualStream);
        Assert.Equal(3, offset);
        Assert.Equal(2, length);
        Assert.True(isRange);
        Assert.Equal(0, read.TotalLength);
        Assert.Equal(12, new SndbObjectReadResult(info, stream, 3, 2, true, 12).TotalLength);
    }

    [Fact]
    public void CreateTable_PreviousConstructorAndDeconstruct_PreserveExistingFields()
    {
        IReadOnlyList<TableColumnDefinition> columns = Array.Empty<TableColumnDefinition>();
        IReadOnlyList<string> key = ["DeviceID"];
        IReadOnlyList<TableForeignKeyClause> foreignKeys = Array.Empty<TableForeignKeyClause>();
        var statement = new CreateTableStatement("Devices", columns, key, true, foreignKeys);
        var (name, actualColumns, primaryKey, ifNotExists, actualForeignKeys) = statement;
        Assert.Equal("Devices", name);
        Assert.Same(columns, actualColumns);
        Assert.Same(key, primaryKey);
        Assert.True(ifNotExists);
        Assert.Same(foreignKeys, actualForeignKeys);
        Assert.Empty(statement.CheckConstraintClauses);
    }

    [Fact]
    public void Select_PreviousConstructorAndDeconstruct_PreserveDistinctAndAliases()
    {
        IReadOnlyList<SelectItem> projections = Array.Empty<SelectItem>();
        IReadOnlyList<SqlExpression> groupBy = Array.Empty<SqlExpression>();
        IReadOnlyList<JoinClause> joins = Array.Empty<JoinClause>();
        IReadOnlyList<OrderBySpec> ordering = Array.Empty<OrderBySpec>();
        var statement = new SelectStatement(projections, "Devices", null, groupBy, null, null, null, "d",
            null, null, joins, null, ordering, true);
        var (actualProjections, measurement, where, actualGroupBy, function, pagination, orderBy, alias,
            join, subquery, actualJoins, having, actualOrdering, distinct) = statement;
        Assert.Same(projections, actualProjections);
        Assert.Equal("Devices", measurement);
        Assert.Equal("d", alias);
        Assert.Same(groupBy, actualGroupBy);
        Assert.Same(joins, actualJoins);
        Assert.Same(ordering, actualOrdering);
        Assert.True(distinct);
        Assert.Null(where);
        Assert.Null(function);
        Assert.Null(pagination);
        Assert.Null(orderBy);
        Assert.Null(join);
        Assert.Null(subquery);
        Assert.Null(having);
        Assert.Empty(statement.SetOperationList);
    }

    [Fact]
    public void Explain_PreviousConstructorAndDeconstruct_PreserveScanEstimates()
    {
        var result = new SqlExplainExecutionResult("db", "SELECT", "Devices", 1, 2, 3, 4, 5, 6, true, 7,
            "index", "IX_Devices", null);
        var (database, type, measurement, series, segments, blocks, scanned, memory, rows, timeFilter, tagFilters,
            accessPath, indexName, documentPlan) = result;
        Assert.Equal("db", database);
        Assert.Equal("SELECT", type);
        Assert.Equal("Devices", measurement);
        Assert.Equal((1, 2, 3, 4L, 5L, 6L, true, 7), (series, segments, blocks, scanned, memory, rows, timeFilter, tagFilters));
        Assert.Equal("index", accessPath);
        Assert.Equal("IX_Devices", indexName);
        Assert.Null(documentPlan);
        Assert.Null(result.ScanFilter);
    }

    [Fact]
    public void TableSchema_PreviousFactory_PreservesNameTimestampAndValidation()
    {
        IReadOnlyList<(string Name, TableColumnType DataType, bool IsNullable)> columns = [("DeviceID", TableColumnType.Int64, false)];
        var schema = TableSchema.Create("Devices", columns, ["DeviceID"], null, null, null, 123);
        Assert.Equal("Devices", schema.Name);
        Assert.Equal("DeviceID", Assert.Single(schema.Columns).Name);
        Assert.Equal(123, schema.CreatedAtUtcTicks);
        Assert.Empty(schema.CheckConstraints);
        Assert.Throws<ArgumentException>(() => TableSchema.Create("", columns, ["DeviceID"], null, null, null, 123));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WalTruncator_PreviousBinaryEntry_PreservesArchiveOption(bool keepArchive)
    {
        var directory = Path.Combine(Path.GetTempPath(), "sonnetdb-api40-wal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "active.SDBWAL");
            using var writer = WalWriter.Open(path);
            var method = typeof(WalTruncator).GetMethod("SwapAndTruncate", [typeof(WalWriter), typeof(string), typeof(long), typeof(int), typeof(bool)]);
            Assert.NotNull(method);
            using var replacement = Assert.IsType<WalWriter>(method.Invoke(null, [writer, path, 9L, 4096, keepArchive]));
            Assert.False(writer.IsOpen);
            Assert.True(replacement.IsOpen);
            Assert.Equal(9, replacement.NextLsn);
            Assert.True(File.Exists(path));
            Assert.Equal(keepArchive, File.Exists(path + ".archived-0000000000000009"));
        }
        finally
        {
            Assert.Equal(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), Path.GetDirectoryName(directory));
            Assert.StartsWith("sonnetdb-api40-wal-", Path.GetFileName(directory));
            Directory.Delete(directory, recursive: true);
        }
    }
}
