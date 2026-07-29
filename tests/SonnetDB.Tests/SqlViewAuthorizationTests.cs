using SonnetDB.Endpoints;
using SonnetDB.Sql;
using Xunit;

namespace SonnetDB.Tests;

public sealed class SqlViewAuthorizationTests
{
    [Theory]
    [InlineData("SHOW VIEWS")]
    [InlineData("DESCRIBE VIEW active_devices")]
    [InlineData("SHOW MATERIALIZED VIEWS")]
    [InlineData("DESCRIBE MATERIALIZED VIEW active_devices")]
    [InlineData("SELECT * FROM active_devices")]
    [InlineData("SHOW PROCEDURES")]
    [InlineData("DESCRIBE PROCEDURE add_device")]
    [InlineData("SHOW TRIGGERS ON devices")]
    [InlineData("DESCRIBE TRIGGER audit_insert")]
    [InlineData("CALL read_device(1)")]
    public void RequiresWritePermission_ViewReadStatements_ReturnsFalse(string sql)
        => Assert.False(SqlEndpointHandler.RequiresWritePermission(SqlParser.Parse(sql)));

    [Theory]
    [InlineData("CREATE VIEW active_devices AS SELECT * FROM devices")]
    [InlineData("DROP VIEW active_devices")]
    [InlineData("CREATE MATERIALIZED VIEW active_devices AS SELECT * FROM devices")]
    [InlineData("REFRESH MATERIALIZED VIEW active_devices")]
    [InlineData("DROP MATERIALIZED VIEW active_devices")]
    [InlineData("CREATE PROCEDURE add_device () LANGUAGE SQL AS BEGIN SELECT 1; END")]
    [InlineData("DROP PROCEDURE add_device")]
    [InlineData("CREATE TRIGGER audit_insert AFTER INSERT ON devices FOR EACH ROW LANGUAGE SQL AS BEGIN INSERT INTO audit (id) VALUES (NEW.id); END")]
    [InlineData("DROP TRIGGER audit_insert")]
    public void RequiresWritePermission_ViewDdl_ReturnsTrue(string sql)
        => Assert.True(SqlEndpointHandler.RequiresWritePermission(SqlParser.Parse(sql)));
}
