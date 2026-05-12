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
}
