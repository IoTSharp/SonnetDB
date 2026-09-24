using System.Globalization;
using SonnetDB.Engine;
using SonnetDB.Sql;
using SonnetDB.Sql.Execution;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

/// <summary>GH-Issue #181：DECIMAL/NUMERIC 的精确执行和持久化回归。</summary>
public sealed class SqlDecimalTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sndb-decimal-" + Guid.NewGuid().ToString("N"));

    public SqlDecimalTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Theory]
    [InlineData("DECIMAL")]
    [InlineData("NUMERIC")]
    public void Cast_PreservesExactDecimalValue(string type)
    {
        using var db = Open();
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db, $"SELECT CAST('9007199254740993.125' AS {type})"));

        Assert.Equal(
            decimal.Parse("9007199254740993.125", CultureInfo.InvariantCulture),
            Assert.IsType<decimal>(Assert.Single(result.Rows)[0]));
    }

    [Fact]
    public void Arithmetic_PreservesDecimalWithoutBinaryRounding()
    {
        using var db = Open();
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db, "SELECT CAST('0.1' AS DECIMAL) + CAST('0.2' AS NUMERIC)"));

        Assert.Equal(0.3m, Assert.IsType<decimal>(Assert.Single(result.Rows)[0]));
    }

    [Fact]
    public void Table_RoundTripsDecimalAcrossReopen()
    {
        using (var db = Open())
        {
            SqlExecutor.Execute(db,
                "CREATE TABLE ledger (id INT, amount DECIMAL(20,6), PRIMARY KEY (id))");
            SqlExecutor.Execute(db,
                "INSERT INTO ledger (id, amount) VALUES (1, '9007199254740993.125000')");

            var row = Assert.Single(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
                db, "SELECT amount FROM ledger")).Rows);
            Assert.Equal(9007199254740993.125000m, Assert.IsType<decimal>(row[0]));
        }

        using var reopened = Open();
        var reopenedRow = Assert.Single(Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            reopened, "SELECT amount FROM ledger")).Rows);
        Assert.Equal(9007199254740993.125000m, Assert.IsType<decimal>(reopenedRow[0]));
    }

    [Fact]
    public void Table_DecimalPrimaryKey_RangeFilterUsesNumericOrder()
    {
        using var db = Open();
        SqlExecutor.Execute(db,
            "CREATE TABLE decimal_keys (id DECIMAL(18,4), label STRING, PRIMARY KEY (id))");
        SqlExecutor.Execute(db,
            "INSERT INTO decimal_keys (id, label) VALUES "
            + "('-10.00', 'minus10'), ('-2.00', 'minus2'), ('0.00', 'zero'), "
            + "('1.00', 'one'), ('10.00', 'ten')");

        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(
            db, "SELECT label FROM decimal_keys WHERE id >= -2.00 ORDER BY id"));

        Assert.Equal(
            new object?[] { "minus2", "zero", "one", "ten" },
            result.Rows.Select(static row => row[0]).ToArray());
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });
}
