using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>CTAS 声明推断必须独立于首行、NULL 和源对象约束。</summary>
public sealed class SqlCreateTableAsTests
{
    /// <summary>空结果的九种声明类型、名称和 Decimal p/s 保留，源键和生成约束不继承。</summary>
    [Fact]
    public void Schema_EmptyDeclaredResult_PreservesNineTypesWithoutSourceConstraints()
    {
        TableColumnType[] types = [TableColumnType.Int64, TableColumnType.Decimal, TableColumnType.Time,
            TableColumnType.DateTime, TableColumnType.Boolean, TableColumnType.Json, TableColumnType.Blob,
            TableColumnType.Duration, TableColumnType.DateTimeOffset];
        string[] names = ["DeviceID", "Amount", "AtTime", "Stamp", "Enabled", "Payload", "Bytes", "Elapsed", "Zoned"];
        var result = new SelectExecutionResult(names, [])
        {
            ColumnInfo = types.Select(static type => new SelectColumnInfo(type, false,
                IsKey: true, IsAutoIncrement: true, IsRowVersion: true)
            {
                NumericPrecision = type == TableColumnType.Decimal ? (byte)28 : null,
                NumericScale = type == TableColumnType.Decimal ? (byte)12 : null,
            }).ToArray(),
        };
        TableSchema schema = SqlCreateTableAsSchema.Create("Copied", result);
        Assert.Equal(names, schema.Columns.Select(static column => column.Name));
        Assert.Equal(types, schema.Columns.Select(static column => column.DataType));
        Assert.Equal((byte)28, schema.Columns[1].DecimalPrecision);
        Assert.Equal((byte)12, schema.Columns[1].DecimalScale);
        Assert.Empty(schema.PrimaryKey);
        Assert.Empty(schema.Indexes);
        Assert.Empty(schema.ForeignKeys);
        Assert.Empty(schema.CheckConstraints);
        Assert.All(schema.Columns, column =>
        {
            Assert.True(column.IsNullable);
            Assert.False(column.IsPrimaryKey);
            Assert.False(column.IsAutoIncrement);
            Assert.False(column.IsRowVersion);
            Assert.Null(column.DefaultExpressionSql);
            Assert.Null(column.Comment);
        });
    }

    /// <summary>重复列名、仅大小写不同的名称和未定类型 NULL 都拒绝。</summary>
    [Theory]
    [InlineData("Name", "Name")]
    [InlineData("Name", "name")]
    public void Schema_CollidingProjectionNames_Rejects(string first, string second)
    {
        var result = new SelectExecutionResult([first, second], [])
        {
            ColumnInfo = [new(TableColumnType.String), new(TableColumnType.String)],
        };
        Assert.Throws<InvalidOperationException>(() => SqlCreateTableAsSchema.Create("Copied", result));
    }

    /// <summary>截断预览、未知声明和缺少类型的 NULL 不得物化为看似完整的表。</summary>
    [Fact]
    public void Schema_TruncatedOrUndeclaredResult_RejectsBeforeCreation()
    {
        var result = new SelectExecutionResult(["Value"], [])
        {
            ColumnInfo = [new(TableColumnType.Int64)],
        };
        Assert.Throws<InvalidOperationException>(() => SqlCreateTableAsSchema.Create("Copied", result with { Truncated = true }));
        Assert.Throws<InvalidOperationException>(() => SqlCreateTableAsSchema.Create("Copied", result with { ColumnInfo = null }));
        Assert.Throws<InvalidOperationException>(() => SqlCreateTableAsSchema.Create("Copied", result with { ColumnInfo = [new(null)] }));
        Assert.Throws<InvalidOperationException>(() => SqlCreateTableAsSchema.Create("Copied",
            result with { ColumnInfo = [new(TableColumnType.Int64) { IsDeclared = false }] }));
    }
}
