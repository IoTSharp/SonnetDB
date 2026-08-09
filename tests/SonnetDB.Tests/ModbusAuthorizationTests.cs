using SonnetDB.Endpoints;
using SonnetDB.Sql;
using Xunit;

namespace SonnetDB.Tests;

/// <summary>
/// 验证 HTTP SQL 权限分类器对 Modbus DDL 与本地 metadata 查询的判定。
/// </summary>
public sealed class ModbusAuthorizationTests
{
    /// <summary>
    /// 验证 SHOW、DESCRIBE 与 EXPLAIN Modbus metadata 不要求数据库写权限。
    /// </summary>
    [Theory]
    [InlineData("SHOW MODBUS SOURCES")]
    [InlineData("SHOW MODBUS ENDPOINTS")]
    [InlineData("SHOW MODBUS WRITE AUDIT")]
    [InlineData("DESCRIBE MODBUS SOURCE line_source")]
    [InlineData("DESCRIBE MODBUS ENDPOINT line_endpoint")]
    [InlineData("DESCRIBE MODBUS TABLE line_values")]
    [InlineData("EXPLAIN SHOW MODBUS SOURCES")]
    [InlineData("EXPLAIN DESCRIBE MODBUS TABLE line_values")]
    public void RequiresWritePermission_ModbusMetadata_ReturnsFalse(string sql)
        => Assert.False(SqlEndpointHandler.RequiresWritePermission(SqlParser.Parse(sql)));

    /// <summary>
    /// 验证 source、endpoint、映射表 DDL 和受限远端写都要求数据库写权限。
    /// </summary>
    [Theory]
    [InlineData("""
        CREATE MODBUS SOURCE line_source
        WITH (ENDPOINT '192.0.2.10:502', BYTE_ORDER BIG_ENDIAN, WORD_ORDER BIG_ENDIAN)
        """)]
    [InlineData("""
        CREATE MODBUS ENDPOINT line_endpoint
        WITH (BIND '127.0.0.1:1502', BYTE_ORDER BIG_ENDIAN, WORD_ORDER BIG_ENDIAN,
              ALLOWLIST ('127.0.0.1'))
        """)]
    [InlineData("""
        CREATE TABLE line_values (
            id INT NOT NULL,
            value INT FROM MODBUS HOLDING_REGISTER(40001) AS UINT16,
            PRIMARY KEY (id)
        )
        USING MODBUS SOURCE line_source
        WITH (TABLE_MODE LATEST, ON_ERROR KEEP_LAST)
        """)]
    [InlineData("WRITE MODBUS line_values SET value = 42 DRY RUN")]
    [InlineData("WRITE MODBUS line_values SET value = 42 PREVIEW")]
    [InlineData("WRITE MODBUS line_values SET value = 42 CONFIRM 'one-time-token'")]
    public void RequiresWritePermission_ModbusMutations_ReturnsTrue(string sql)
        => Assert.True(SqlEndpointHandler.RequiresWritePermission(SqlParser.Parse(sql)));

    /// <summary>验证 Modbus 控制值和确认令牌不会进入慢查询诊断文本。</summary>
    [Theory]
    [InlineData("WRITE MODBUS controls SET value = 42 PREVIEW")]
    [InlineData("\n write\nmodbus controls SET value = 42 CONFIRM 'secret-token'")]
    public void RedactSqlForDiagnostics_ModbusWrite_RemovesEntireSensitiveStatement(string sql)
        => Assert.Equal("WRITE MODBUS <redacted>", SqlEndpointHandler.RedactSqlForDiagnostics(sql));

    /// <summary>验证普通 SQL 的诊断文本保持原样。</summary>
    [Fact]
    public void RedactSqlForDiagnostics_OrdinarySql_ReturnsOriginalText()
    {
        const string sql = "SELECT value FROM controls";
        Assert.Same(sql, SqlEndpointHandler.RedactSqlForDiagnostics(sql));
    }
}
