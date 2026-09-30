using SonnetDB.Catalog;
using SonnetDB.Query;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using SonnetDB.Storage.Format;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class WhereClauseDecomposerTests
{
    private const long DayMs = 86_400_000L;

    [Fact]
    public void Decompose_TimeRangeWithNowAndDurationExpressions_ReturnsNormalizedBounds()
    {
        var schema = MeasurementSchema.Create("cpu",
            [
                new MeasurementColumn("host", MeasurementColumnRole.Tag, FieldType.String),
                new MeasurementColumn("usage", MeasurementColumnRole.Field, FieldType.Float64),
            ]);

        var stmt = (SonnetDB.Sql.Ast.SelectStatement)SqlParser.Parse(
            "SELECT * FROM cpu WHERE host = 'h1' AND time >= now() - 1d AND time < now() + 1d");

        const long nowMs = 1_000_000_000L;
        var clause = WhereClauseDecomposer.Decompose(stmt.Where, schema, nowMs);

        Assert.Equal("h1", clause.TagFilter["host"]);
        Assert.Equal(new TimeRange(nowMs - DayMs, nowMs + DayMs - 1), clause.TimeRange);
        Assert.Empty(clause.GeoFilters);
    }

    [Fact]
    public void Decompose_QuotedTimeField_RemainsResidualWithoutTimestampPruning()
    {
        var schema = MeasurementSchema.Create("Cpu",
            [new MeasurementColumn("Time", MeasurementColumnRole.Field, FieldType.Int64)]);
        var statement = (SonnetDB.Sql.Ast.SelectStatement)SqlParser.Parse(
            "SELECT * FROM Cpu WHERE \"Time\" = 1000");

        var clause = WhereClauseDecomposer.Decompose(statement.Where, schema);

        Assert.Equal(new TimeRange(long.MinValue, long.MaxValue), clause.TimeRange);
        Assert.NotNull(clause.Residual);
    }

    [Fact]
    public void Decompose_UnquotedTagAndTime_UsesDeclaredTagSpelling()
    {
        var schema = MeasurementSchema.Create("Cpu",
            [
                new MeasurementColumn("Host", MeasurementColumnRole.Tag, FieldType.String),
                new MeasurementColumn("Usage", MeasurementColumnRole.Field, FieldType.Int64),
            ]);
        var statement = (SonnetDB.Sql.Ast.SelectStatement)SqlParser.Parse(
            "SELECT * FROM Cpu WHERE HOST = 'h1' AND TIME >= 1000");

        var clause = WhereClauseDecomposer.Decompose(statement.Where, schema);

        Assert.Equal("h1", clause.TagFilter["Host"]);
        Assert.Equal(new TimeRange(1000, long.MaxValue), clause.TimeRange);
        Assert.Null(clause.Residual);
    }

    [Fact]
    public void Decompose_LegacyTagCaseConflict_RequiresQuotedReference()
    {
        var schema = MeasurementSchema.CreateLoaded("Cpu",
            [
                new MeasurementColumn("Host", MeasurementColumnRole.Tag, FieldType.String),
                new MeasurementColumn("host", MeasurementColumnRole.Tag, FieldType.String),
                new MeasurementColumn("Usage", MeasurementColumnRole.Field, FieldType.Int64),
            ]);
        var unquoted = (SonnetDB.Sql.Ast.SelectStatement)SqlParser.Parse(
            "SELECT * FROM Cpu WHERE HOST = 'h1'");
        Assert.Throws<InvalidOperationException>(() =>
            WhereClauseDecomposer.Decompose(unquoted.Where, schema));

        var quoted = (SonnetDB.Sql.Ast.SelectStatement)SqlParser.Parse(
            "SELECT * FROM Cpu WHERE \"Host\" = 'h1'");
        var clause = WhereClauseDecomposer.Decompose(quoted.Where, schema);

        Assert.Equal("h1", clause.TagFilter["Host"]);
        Assert.Null(clause.Residual);
    }
}
