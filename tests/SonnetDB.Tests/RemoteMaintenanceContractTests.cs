using System.Data;
using SonnetDB.Data;
using Xunit;

namespace SonnetDB.Tests;

public sealed partial class RemoteAdoHttp2TransportTests
{
    /// <summary>维护命令和目录在嵌入式、REST、HTTP/2 的同步异步路径及完整重开后保持一致。</summary>
    [Theory]
    [InlineData("embedded", false)]
    [InlineData("embedded", true)]
    [InlineData("rest", false)]
    [InlineData("rest", true)]
    [InlineData("frame-http2", false)]
    [InlineData("frame-http2", true)]
    public async Task Maintenance_ConstraintIndexAndIdentity_PersistAcrossHostRestart(string protocol, bool asynchronous)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        string embeddedRoot = Path.Combine(_dataRoot!, "maintenance-contract");
        await using (var connection = new SndbConnection(IntegralConnectionString(protocol, embeddedRoot)))
        {
            await OpenPrimaryKeyMigrationAsync(connection, asynchronous, deadline.Token);
            foreach (string sql in new[]
            {
                "CREATE TABLE MaintParent(Id INT,CONSTRAINT PkMaintParent PRIMARY KEY(Id))",
                "CREATE TABLE MaintDevice(Id INT AUTO_INCREMENT,Name STRING,ParentId INT,Value INT,CONSTRAINT PkMaintDevice PRIMARY KEY(Id),CONSTRAINT FkMaintDevice FOREIGN KEY(ParentId) REFERENCES MaintParent(Id),CONSTRAINT CkMaintDevice CHECK(Value>=0))",
                "CREATE INDEX IxMaintName ON MaintDevice(Name DESC) INCLUDE(Id,Value)",
                "INSERT INTO MaintParent(Id) VALUES(1)",
                "ALTER TABLE MaintDevice AUTO_INCREMENT = 50",
            })
                await ExecutePrimaryKeyMigrationAsync(connection, sql, asynchronous, deadline.Token);

            await AssertPrimaryKeyMigrationRowsAsync(connection, "SELECT COUNT(*) FROM MaintDevice",
                [typeof(long)], [[0L]], asynchronous, deadline.Token);
            await ExecutePrimaryKeyMigrationAsync(connection,
                "INSERT INTO MaintDevice(Name,ParentId,Value) VALUES('first',1,10)", asynchronous, deadline.Token);
            await ExecutePrimaryKeyMigrationAsync(connection,
                "INSERT INTO MaintDevice(Id,Name,ParentId,Value) VALUES(100,'explicit',1,20)", asynchronous, deadline.Token);
            await ExecutePrimaryKeyMigrationAsync(connection,
                "ALTER TABLE MaintDevice AUTO_INCREMENT = 200", asynchronous, deadline.Token);
            await Assert.ThrowsAnyAsync<Exception>(() => ExecutePrimaryKeyMigrationAsync(connection,
                "ALTER TABLE MaintDevice AUTO_INCREMENT = 100", asynchronous, deadline.Token));
            await ExecutePrimaryKeyMigrationAsync(connection,
                "ALTER TABLE MaintDevice RENAME COLUMN Name TO Label", asynchronous, deadline.Token);
            await ExecutePrimaryKeyMigrationAsync(connection,
                "ALTER TABLE MaintDevice RENAME TO MaintRenamed", asynchronous, deadline.Token);
            AssertMaintenanceIndexMetadata(connection);
        }

        if (protocol != "embedded") await ReopenHostAsync(deadline.Token);
        await using (var reopened = new SndbConnection(IntegralConnectionString(protocol, embeddedRoot)))
        {
            await OpenPrimaryKeyMigrationAsync(reopened, asynchronous, deadline.Token);
            AssertMaintenanceIndexMetadata(reopened);
            await AssertPrimaryKeyMigrationRowsAsync(reopened,
                "SELECT constraint_name,constraint_type,table_name FROM information_schema.table_constraints WHERE constraint_name='PkMaintDevice'",
                [typeof(string), typeof(string), typeof(string)], [["PkMaintDevice", "PRIMARY KEY", "MaintRenamed"]], asynchronous, deadline.Token);
            await AssertPrimaryKeyMigrationRowsAsync(reopened,
                "SELECT constraint_name,constraint_type FROM information_schema.table_constraints WHERE constraint_name='FkMaintDevice'",
                [typeof(string), typeof(string)], [["FkMaintDevice", "FOREIGN KEY"]], asynchronous, deadline.Token);
            await AssertPrimaryKeyMigrationRowsAsync(reopened,
                "SELECT constraint_name,constraint_type FROM information_schema.table_constraints WHERE constraint_name='CkMaintDevice'",
                [typeof(string), typeof(string)], [["CkMaintDevice", "CHECK"]], asynchronous, deadline.Token);
            await ExecutePrimaryKeyMigrationAsync(reopened,
                "INSERT INTO MaintRenamed(Label,ParentId,Value) VALUES('after',1,30)", asynchronous, deadline.Token);
            await AssertPrimaryKeyMigrationRowsAsync(reopened,
                "SELECT Id,Label,Value FROM MaintRenamed ORDER BY Id",
                [typeof(long), typeof(string), typeof(long)], [[50L, "first", 10L], [100L, "explicit", 20L], [200L, "after", 30L]], asynchronous, deadline.Token);
            await ExecutePrimaryKeyMigrationAsync(reopened,
                "ALTER TABLE MaintRenamed DROP CONSTRAINT CkMaintDevice", asynchronous, deadline.Token);
            await ExecutePrimaryKeyMigrationAsync(reopened,
                "ALTER TABLE MaintRenamed DROP CONSTRAINT FkMaintDevice", asynchronous, deadline.Token);
            await ExecutePrimaryKeyMigrationAsync(reopened,
                "ALTER TABLE MaintRenamed DROP CONSTRAINT PkMaintDevice", asynchronous, deadline.Token);
            await ExecutePrimaryKeyMigrationAsync(reopened,
                "INSERT INTO MaintRenamed(Id,Label,ParentId,Value) VALUES(50,'first',999,-1)", asynchronous, deadline.Token);
            await AssertPrimaryKeyMigrationRowsAsync(reopened,
                "SELECT COUNT(*) FROM MaintRenamed WHERE Id=50", [typeof(long)], [[2L]], asynchronous, deadline.Token);
            await AssertPrimaryKeyMigrationRowsAsync(reopened,
                "SELECT COUNT(*) FROM information_schema.table_constraints WHERE table_name='MaintRenamed'",
                [typeof(long)], [[0L]], asynchronous, deadline.Token);
            await ExecutePrimaryKeyMigrationAsync(reopened,
                "DROP INDEX IxMaintName ON MaintRenamed", asynchronous, deadline.Token);
            Assert.Empty(reopened.GetSchema("Indexes", [null, null, "MaintRenamed", null]).Rows.Cast<DataRow>());
        }
        AssertPrimaryKeyMigrationTransport(protocol);
    }

    private static void AssertMaintenanceIndexMetadata(SndbConnection connection)
    {
        var rows = connection.GetSchema("Indexes", [null, null, "MaintRenamed", "IxMaintName"])
            .Rows.Cast<DataRow>().ToArray();
        Assert.Equal(3, rows.Length);
        Assert.Contains(rows, row => Equals(row["COLUMN_NAME"], "Label")
            && Equals(row["SORT_DIRECTION"], "DESC") && Equals(row["IS_INCLUDED"], false));
        Assert.Contains(rows, row => Equals(row["COLUMN_NAME"], "Id") && Equals(row["IS_INCLUDED"], true));
        Assert.Contains(rows, row => Equals(row["COLUMN_NAME"], "Value") && Equals(row["IS_INCLUDED"], true));
    }
}
