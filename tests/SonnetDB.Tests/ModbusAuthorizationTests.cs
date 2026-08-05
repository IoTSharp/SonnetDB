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
    [InlineData("DESCRIBE MODBUS SOURCE line_source")]
    [InlineData("DESCRIBE MODBUS ENDPOINT line_endpoint")]
    [InlineData("DESCRIBE MODBUS TABLE line_values")]
    [InlineData("EXPLAIN SHOW MODBUS SOURCES")]
    [InlineData("EXPLAIN DESCRIBE MODBUS TABLE line_values")]
    public void RequiresWritePermission_ModbusMetadata_ReturnsFalse(string sql)
        => Assert.False(SqlEndpointHandler.RequiresWritePermission(SqlParser.Parse(sql)));

    /// <summary>
    /// 验证 source、endpoint 和带映射表的创建语句都要求数据库写权限。
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
    public void RequiresWritePermission_ModbusDdl_ReturnsTrue(string sql)
        => Assert.True(SqlEndpointHandler.RequiresWritePermission(SqlParser.Parse(sql)));
}
