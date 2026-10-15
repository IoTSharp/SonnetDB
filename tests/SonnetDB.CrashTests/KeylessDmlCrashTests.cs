using SonnetDB.Engine;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.CrashTests;

public sealed partial class CrashReliabilityTests
{
    [Theory]
    [InlineData("between_tables", false)]
    [InlineData("before_complete", false)]
    [InlineData("after_complete", true)]
    public void KeylessDml_HardKillAtJournalBoundary_RecoversRowsIndexesAndPhysicalIdentity(string phase, bool committed)
    {
        string root = RunKillScenario("crash_kill9_keyless_" + phase, TimeSpan.Zero);
        string[] expectedIds = File.ReadAllLines(Path.Combine(root, committed ? "at-boundary.ids" : "before.ids"));
        using (var db = Tsdb.Open(MakeOptions(root)))
        {
            var store = db.Tables.Open("logs");
            Assert.Equal(expectedIds, store.Scan().Select(row => Convert.ToHexString(row.PrimaryKey.Span)).Order());
            Assert.Empty(store.Schema.PrimaryKey);
            Assert.Single(store.Schema.Columns);
            Assert.Equal(committed ? 3 : 2, store.RowCount);
            Assert.Equal(committed ? 0 : 2, Rows(db, "SELECT value FROM logs WHERE value=1").Count);
            Assert.Equal(committed ? 1 : 0, Rows(db, "SELECT value FROM logs WHERE value=2").Count);
            Assert.Equal(committed ? 2 : 0, Rows(db, "SELECT value FROM logs WHERE value=3").Count);
            Assert.Equal(committed ? 2L : 1L, Assert.Single(Rows(db, "SELECT value FROM keyed WHERE id=1"))[0]);
            Assert.Equal(13, TableSchemaCodec.ReadFormatVersion(db.Tables.SchemaPath));
            Assert.Equal(1, Assert.IsType<DeleteExecutionResult>(SqlExecutor.Execute(db, "DELETE FROM logs WHERE TRUE LIMIT 1")).SeriesAffected);
            SqlExecutor.Execute(db, "INSERT INTO logs(value) VALUES(4),(4)");
        }
        using var reopened = Tsdb.Open(MakeOptions(root));
        Assert.Equal(committed ? 4 : 3, reopened.Tables.Open("logs").RowCount);
        Assert.Equal(2, Rows(reopened, "SELECT value FROM logs WHERE value=4").Count);

        static IReadOnlyList<IReadOnlyList<object?>> Rows(Tsdb db, string sql)
            => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(db, sql)).Rows;
    }
}
