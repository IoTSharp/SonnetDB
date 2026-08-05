using SonnetDB.Modbus;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class ModbusParserTests
{
    /// <summary>
    /// 验证完整 source DDL 会保留连接、轮询和对象级编码选项。
    /// </summary>
    [Fact]
    public void Parse_CreateModbusSourceWithCompleteOptions_ReturnsDefinition()
    {
        var statement = Assert.IsType<CreateModbusSourceStatement>(SqlParser.Parse("""
            CREATE MODBUS SOURCE line1_plc
            WITH (
                TRANSPORT TCP,
                ENDPOINT '192.168.1.50:502',
                UNIT_ID 7,
                POLL_INTERVAL '1s',
                TIMEOUT 800ms,
                RETRY 2,
                ADDRESSING MODICON,
                BYTE_ORDER BIG_ENDIAN,
                WORD_ORDER LITTLE_ENDIAN,
                ENABLED FALSE,
                AUDIT TRUE
            )
            """));

        Assert.Equal("line1_plc", statement.Name);
        Assert.Equal("192.168.1.50", statement.Definition.Host);
        Assert.Equal(502, statement.Definition.Port);
        Assert.Equal(7, statement.Definition.UnitId);
        Assert.Equal(1_000, statement.Definition.PollIntervalMilliseconds);
        Assert.Equal(800, statement.Definition.TimeoutMilliseconds);
        Assert.Equal(2, statement.Definition.RetryCount);
        Assert.Equal(ModbusAddressingMode.Modicon, statement.Definition.AddressingMode);
        Assert.Equal(ModbusByteOrder.BigEndian, statement.Definition.ByteOrder);
        Assert.Equal(ModbusWordOrder.LittleEndian, statement.Definition.WordOrder);
        Assert.False(statement.Definition.Enabled);
    }

    /// <summary>
    /// 验证完整 endpoint DDL 会解析 CSV allowlist、连接上限和 staged 策略。
    /// </summary>
    [Fact]
    public void Parse_CreateModbusEndpointWithCompleteOptions_ReturnsDefinition()
    {
        var statement = Assert.IsType<CreateModbusEndpointStatement>(SqlParser.Parse("""
            CREATE MODBUS ENDPOINT line_shadow
            WITH (
                TRANSPORT TCP,
                BIND '127.0.0.1:1502',
                UNIT_ID 1,
                ADDRESSING ZERO_BASED,
                BYTE_ORDER LITTLE_ENDIAN,
                WORD_ORDER BIG_ENDIAN,
                WRITE_POLICY STAGED,
                ALLOWLIST '127.0.0.1, 192.168.10.0/24',
                MAX_CONNECTIONS 16,
                ENABLED TRUE
            )
            """));

        Assert.Equal("line_shadow", statement.Name);
        Assert.Equal("127.0.0.1", statement.Definition.BindAddress);
        Assert.Equal(1502, statement.Definition.Port);
        Assert.Equal(16, statement.Definition.MaxConnections);
        Assert.Equal(["127.0.0.1", "192.168.10.0/24"], statement.Definition.AllowedClientNetworks);
        Assert.Equal(ModbusAddressingMode.ZeroBased, statement.Definition.AddressingMode);
        Assert.Equal(ModbusByteOrder.LittleEndian, statement.Definition.ByteOrder);
        Assert.Equal(ModbusEndpointWritePolicy.Staged, statement.Definition.WritePolicy);
        Assert.True(statement.Definition.Enabled);
    }

    /// <summary>验证省略可选项时 source 与 endpoint 使用文档规定的稳定默认值。</summary>
    [Fact]
    public void Parse_ModbusObjectsWithRequiredOptions_UsesDocumentedDefaults()
    {
        var source = Assert.IsType<CreateModbusSourceStatement>(SqlParser.Parse("""
            CREATE MODBUS SOURCE defaults_source
            WITH (ENDPOINT 'plc.local:502', BYTE_ORDER BIG_ENDIAN, WORD_ORDER BIG_ENDIAN)
            """));
        Assert.Equal(1, source.Definition.UnitId);
        Assert.Equal(1_000, source.Definition.PollIntervalMilliseconds);
        Assert.Equal(3_000, source.Definition.TimeoutMilliseconds);
        Assert.Equal(3, source.Definition.RetryCount);
        Assert.Equal(ModbusAddressingMode.Modicon, source.Definition.AddressingMode);
        Assert.False(source.Definition.Enabled);

        var endpoint = Assert.IsType<CreateModbusEndpointStatement>(SqlParser.Parse("""
            CREATE MODBUS ENDPOINT defaults_endpoint
            WITH (BIND '127.0.0.1:502', BYTE_ORDER BIG_ENDIAN, WORD_ORDER BIG_ENDIAN)
            """));
        Assert.Equal(1, endpoint.Definition.UnitId);
        Assert.Equal(32, endpoint.Definition.MaxConnections);
        Assert.Empty(endpoint.Definition.AllowedClientNetworks!);
        Assert.Equal(ModbusAddressingMode.Modicon, endpoint.Definition.AddressingMode);
        Assert.Equal(ModbusEndpointWritePolicy.Staged, endpoint.Definition.WritePolicy);
        Assert.False(endpoint.Definition.Enabled);
    }

    /// <summary>
    /// 验证 source 表保留声明地址、可空顺序覆盖、缩放和采集策略语法。
    /// </summary>
    [Fact]
    public void Parse_CreateTableWithModbusSource_ReturnsUnresolvedMappingClauses()
    {
        var statement = Assert.IsType<CreateTableStatement>(SqlParser.Parse("""
            CREATE TABLE pump_runtime (
                sample_time DATETIME SAMPLE_TIME,
                temperature FLOAT
                    FROM MODBUS INPUT_REGISTER(30001)
                    AS INT16 SCALE 0.1 OFFSET -2,
                flow FLOAT
                    FROM MODBUS HOLDING_REGISTER(40010, 2)
                    AS FLOAT32 BYTE_ORDER BIG_ENDIAN WORD_ORDER LITTLE_ENDIAN,
                alarm BOOL
                    FROM MODBUS HOLDING_REGISTER(40020).BIT(3)
                    AS BIT ACCESS READ,
                PRIMARY KEY (sample_time)
            )
            USING MODBUS SOURCE line1_plc
            WITH (TABLE_MODE HISTORY, ON_ERROR MARK_BAD, STORE HISTORY)
            """));

        Assert.Equal(ModbusMappingDirection.SourceToTable, statement.ModbusBinding!.Direction);
        Assert.Equal("line1_plc", statement.ModbusBinding.TargetName);
        Assert.Equal(ModbusTableMode.History, statement.ModbusBinding.TableMode);
        Assert.Equal(ModbusErrorPolicy.MarkBad, statement.ModbusBinding.ErrorPolicy);
        Assert.True(statement.ModbusBinding.StoreHistory);
        Assert.True(statement.Columns[0].IsModbusSampleTime);

        ModbusColumnMappingClause temperature = statement.Columns[1].ModbusMapping!;
        Assert.Equal(30001, temperature.DeclaredAddress);
        Assert.Equal(ModbusValueType.Int16, temperature.ValueType);
        Assert.Equal(0.1m, temperature.Scale);
        Assert.Equal(-2m, temperature.Offset);
        Assert.Null(temperature.ByteOrderOverride);
        Assert.Null(temperature.WordOrderOverride);

        ModbusColumnMappingClause flow = statement.Columns[2].ModbusMapping!;
        Assert.Equal(2, flow.RegisterCount);
        Assert.Equal(ModbusByteOrder.BigEndian, flow.ByteOrderOverride);
        Assert.Equal(ModbusWordOrder.LittleEndian, flow.WordOrderOverride);
        Assert.Equal(3, statement.Columns[3].ModbusMapping!.BitIndex);
    }

    /// <summary>
    /// 验证 endpoint 表解析固定行与审批后 UPDATE_TABLE 动作。
    /// </summary>
    [Fact]
    public void Parse_CreateTableWithModbusEndpoint_ReturnsExposeBinding()
    {
        var statement = Assert.IsType<CreateTableStatement>(SqlParser.Parse("""
            CREATE TABLE line_shadow (
                id INT NOT NULL,
                running BOOL
                    EXPOSE AS MODBUS COIL(1)
                    AS BIT ACCESS READ_WRITE,
                speed INT
                    EXPOSE AS MODBUS HOLDING_REGISTER(40001)
                    AS UINT16 ACCESS READ_WRITE,
                PRIMARY KEY (id)
            )
            USING MODBUS ENDPOINT local_line_shadow
            WITH (ROW KEY 1, ON_EXTERNAL_WRITE UPDATE_TABLE)
            """));

        Assert.Equal(ModbusMappingDirection.TableToEndpoint, statement.ModbusBinding!.Direction);
        Assert.Equal(1, statement.ModbusBinding.RowKey);
        Assert.Equal(ModbusApprovedWriteAction.UpdateTable, statement.ModbusBinding.ApprovedWriteAction);
        Assert.All(statement.Columns.Where(static column => column.ModbusMapping is not null), column =>
            Assert.Equal(ModbusMappingDirection.TableToEndpoint, column.ModbusMapping!.Direction));
    }

    /// <summary>验证 endpoint 固定行键可表达完整 Int64 下界。</summary>
    [Fact]
    public void Parse_EndpointRowKeyAtInt64Minimum_PreservesValue()
    {
        var statement = Assert.IsType<CreateTableStatement>(SqlParser.Parse("""
            CREATE TABLE minimum_row_key (
                id INT NOT NULL,
                running BOOL EXPOSE AS MODBUS DISCRETE_INPUT(1) AS BIT ACCESS READ,
                PRIMARY KEY (id)
            )
            USING MODBUS ENDPOINT local_shadow
            WITH (ROW KEY -9223372036854775808)
            """));

        Assert.Equal(long.MinValue, statement.ModbusBinding!.RowKey);
    }

    /// <summary>
    /// 验证 Modbus metadata 可被 EXPLAIN 包装，且 contextual 名称不影响既有对象名。
    /// </summary>
    [Fact]
    public void Parse_ModbusMetadataAndContextualNames_PreservesReadOnlyGrammar()
    {
        Assert.IsType<ShowModbusSourcesStatement>(SqlParser.Parse("SHOW MODBUS SOURCES"));
        Assert.IsType<ShowModbusEndpointsStatement>(SqlParser.Parse("SHOW MODBUS ENDPOINTS"));
        Assert.IsType<DescribeModbusSourceStatement>(SqlParser.Parse("DESCRIBE MODBUS SOURCE line1"));
        Assert.IsType<DescribeModbusEndpointStatement>(SqlParser.Parse("DESC MODBUS ENDPOINT edge1"));
        Assert.IsType<DescribeModbusTableStatement>(SqlParser.Parse("DESCRIBE MODBUS TABLE readings"));

        var explain = Assert.IsType<ExplainStatement>(SqlParser.Parse("EXPLAIN SHOW MODBUS ENDPOINTS"));
        Assert.IsType<ShowModbusEndpointsStatement>(explain.Statement);

        var legacyDescribe = Assert.IsType<DescribeMeasurementStatement>(SqlParser.Parse("DESCRIBE modbus"));
        Assert.Equal("modbus", legacyDescribe.Name);
        var contextualTable = Assert.IsType<CreateTableStatement>(SqlParser.Parse(
            "CREATE TABLE modbus (source STRING, endpoint STRING, expose STRING, PRIMARY KEY (source))"));
        Assert.Null(contextualTable.ModbusBinding);
    }

    /// <summary>
    /// 验证不安全、方向冲突或与 wire type 不一致的 Modbus DDL 会在 parser 阶段拒绝。
    /// </summary>
    [Theory]
    [InlineData("CREATE MODBUS SOURCE s WITH (ENDPOINT 'x:502', BYTE_ORDER BIG_ENDIAN)")]
    [InlineData("CREATE MODBUS ENDPOINT e WITH (BYTE_ORDER BIG_ENDIAN, WORD_ORDER BIG_ENDIAN)")]
    [InlineData("CREATE MODBUS SOURCE s WITH (TRANSPORT RTU, ENDPOINT 'x:502', BYTE_ORDER BIG_ENDIAN, WORD_ORDER BIG_ENDIAN)")]
    [InlineData("CREATE MODBUS ENDPOINT e WITH (BIND '127.0.0.1:502', BYTE_ORDER BIG_ENDIAN, WORD_ORDER BIG_ENDIAN, AUDIT FALSE)")]
    [InlineData("CREATE TABLE t (v INT FROM MODBUS HOLDING_REGISTER(0, 1) AS INT32) USING MODBUS SOURCE s WITH (TABLE_MODE LATEST, ON_ERROR KEEP_LAST)")]
    [InlineData("CREATE TABLE t (v INT FROM MODBUS INPUT_REGISTER(0) AS INT16 ACCESS WRITE) USING MODBUS SOURCE s WITH (TABLE_MODE LATEST, ON_ERROR KEEP_LAST)")]
    [InlineData("CREATE TABLE t (v INT EXPOSE AS MODBUS HOLDING_REGISTER(0) AS INT16) USING MODBUS SOURCE s WITH (TABLE_MODE LATEST, ON_ERROR KEEP_LAST)")]
    [InlineData("CREATE TABLE t (v INT FROM MODBUS HOLDING_REGISTER(0) AS INT16, PRIMARY KEY (v))")]
    [InlineData("CREATE TABLE t (id INT, v INT EXPOSE AS MODBUS HOLDING_REGISTER(0) AS INT16, PRIMARY KEY (id)) USING MODBUS ENDPOINT e WITH (ON_EXTERNAL_WRITE STAGE_ONLY)")]
    [InlineData("CREATE TABLE t (v STRING FROM MODBUS HOLDING_REGISTER(0, 2) AS STRING(4) SCALE 1) USING MODBUS SOURCE s WITH (TABLE_MODE LATEST, ON_ERROR KEEP_LAST)")]
    [InlineData("CREATE TABLE t (v BOOL FROM MODBUS COIL(0) AS BIT SCALE 1) USING MODBUS SOURCE s WITH (TABLE_MODE LATEST, ON_ERROR KEEP_LAST)")]
    [InlineData("CREATE TABLE t (id INT, v BOOL EXPOSE AS MODBUS COIL(0) AS BIT, PRIMARY KEY (id)) USING MODBUS ENDPOINT e WITH (ROW KEY 1, ROW_KEY 2)")]
    [InlineData("CREATE TABLE t (id INT, v BOOL EXPOSE AS MODBUS COIL(0) AS BIT, PRIMARY KEY (id)) USING MODBUS ENDPOINT e WITH (ROW KEY 9223372036854775808)")]
    public void Parse_ModbusInvalidContract_Throws(string sql)
    {
        Assert.Throws<SqlParseException>(() => SqlParser.Parse(sql));
    }
}
