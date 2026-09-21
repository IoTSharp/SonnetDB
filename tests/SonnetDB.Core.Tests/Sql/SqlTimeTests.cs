using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>GH-Issue #182：关系表 TIME/TimeOnly 的有界语义回归。</summary>
public sealed class SqlTimeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-time-" + Guid.NewGuid().ToString("N"));

    public SqlTimeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Table_RoundTripsTimeOnlyAndOrdersWithinDay()
    {
        using (var db = Open())
        {
            SqlExecutor.Execute(db,
                "CREATE TABLE schedule (id INT, starts TIME NULL, PRIMARY KEY (id))");
            SqlExecutor.Execute(db,
                "INSERT INTO schedule (id, starts) VALUES (1, '23:59:59.1234567'), (2, '08:30:00'), (3, NULL)");

            var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
                db, "SELECT starts FROM schedule ORDER BY starts ASC"));
            Assert.Equal(
                new object?[] { null, new TimeOnly(8, 30), new TimeOnly(23, 59, 59, 123).Add(TimeSpan.FromTicks(4567)) },
                result.Rows.Select(static row => row[0]).ToArray());
        }

        using var reopened = Open();
        var row = Assert.Single(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            reopened, "SELECT starts FROM schedule WHERE id = 1")).Rows);
        Assert.Equal(new TimeOnly(23, 59, 59, 123).Add(TimeSpan.FromTicks(4567)), row[0]);
    }

    [Fact]
    public void Cast_AndParameterBinding_PreserveTimeOnly()
    {
        using var db = Open();
        var cast = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db, "SELECT CAST('12:34:56.1234567' AS TIME)"));
        Assert.Equal(new TimeOnly(12, 34, 56, 123).Add(TimeSpan.FromTicks(4567)), cast.Rows[0][0]);

        SqlExecutor.Execute(db, "CREATE TABLE schedule (id INT, starts TIME, PRIMARY KEY (id))");
        SqlExecutor.Execute(
            db,
            databaseName: null,
            sql: "INSERT INTO schedule (id, starts) VALUES (@id, @starts)",
            parameters: new SqlParameters()
                .AddNamed("id", 1)
                .AddNamed("starts", new TimeOnly(6, 7, 8)),
            controlPlane: null);
        var row = Assert.Single(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db, "SELECT starts FROM schedule WHERE starts = '06:07:08'")).Rows);
        Assert.Equal(new TimeOnly(6, 7, 8), row[0]);
    }

    [Fact]
    public void InvalidTime_AndCrossDayValues_AreRejected()
    {
        using var db = Open();
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(
            db, "SELECT CAST('24:00:00' AS TIME)"));
        SqlExecutor.Execute(db, "CREATE TABLE schedule (id INT, starts TIME, PRIMARY KEY (id))");
        Assert.Throws<InvalidOperationException>(() => SqlExecutor.Execute(
            db, "INSERT INTO schedule (id, starts) VALUES (1, '24:00:00')"));
        Assert.Throws<ArgumentOutOfRangeException>(() => SqlParameterBinder.Bind(
            SqlParser.Parse("SELECT @starts"),
            new SqlParameters().AddNamed("starts", TimeSpan.FromDays(1))));
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });
}
