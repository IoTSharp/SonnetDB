using SonnetDB.Endpoints;
using SonnetDB.Sql;
using Xunit;

namespace SonnetDB.Tests;

public sealed class SqlViewAuthorizationTests
{
    [Theory]
    [InlineData("SHOW VIEWS")]
    [InlineData("DESCRIBE VIEW active_devices")]
    [InlineData("SELECT * FROM active_devices")]
    public void RequiresWritePermission_ViewReadStatements_ReturnsFalse(string sql)
        => Assert.False(SqlEndpointHandler.RequiresWritePermission(SqlParser.Parse(sql)));

    [Theory]
    [InlineData("CREATE VIEW active_devices AS SELECT * FROM devices")]
    [InlineData("DROP VIEW active_devices")]
    public void RequiresWritePermission_ViewDdl_ReturnsTrue(string sql)
        => Assert.True(SqlEndpointHandler.RequiresWritePermission(SqlParser.Parse(sql)));
}
