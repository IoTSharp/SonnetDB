using System.Data;
using SonnetDB.Data;
using SonnetDB.Model;
using Xunit;

namespace SonnetDB.Core.Tests.Ado;

/// <summary>
/// <see cref="SndbConnection"/> / <see cref="SndbCommand"/> / <see cref="SndbDataReader"/>
/// 的 ADO.NET 端到端测试（PR #28）。
/// </summary>
public sealed class TsdbAdoApiTests : IDisposable
{
    private readonly string _root;

    public TsdbAdoApiTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-ado-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private string ConnString => $"Data Source={_root}";

    private SndbConnection OpenConn()
    {
        var c = new SndbConnection(ConnString);
        c.Open();
        return c;
    }

    private static int ExecNonQuery(SndbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    private static void EnsureCpuSchema(SndbConnection c)
    {
        ExecNonQuery(c, "CREATE MEASUREMENT cpu (host TAG, value FIELD FLOAT)");
    }

    // ── Connection ────────────────────────────────────────────────────────────

    [Fact]
    public void Open_WithDataSource_StateOpen()
    {
        using var c = OpenConn();
        Assert.Equal(ConnectionState.Open, c.State);
        Assert.NotNull(c.UnderlyingTsdb);
        Assert.Equal(_root, c.DataSource);
    }

    [Fact]
    public void Open_MissingDataSource_Throws()
    {
        using var c = new SndbConnection();
        Assert.Throws<InvalidOperationException>(c.Open);
    }

    [Fact]
    public void ChangeConnectionString_WhileOpen_Throws()
    {
        using var c = OpenConn();
        Assert.Throws<InvalidOperationException>(() => c.ConnectionString = "Data Source=other");
    }

    [Fact]
    public void Close_RestoresClosedState()
    {
        var c = OpenConn();
        c.Close();
        Assert.Equal(ConnectionState.Closed, c.State);
        Assert.Null(c.UnderlyingTsdb);
    }

    [Fact]
    public void Open_TwiceSamePath_SharesUnderlyingTsdb()
    {
        using var a = OpenConn();
        using var b = OpenConn();
        Assert.Same(a.UnderlyingTsdb, b.UnderlyingTsdb);
    }

    [Fact]
    public void BeginTransaction_NotSupported()
    {
        using var c = OpenConn();
        Assert.Throws<NotSupportedException>(() => c.BeginTransaction());
    }

    // ── ConnectionStringBuilder ───────────────────────────────────────────────

    [Fact]
    public void ConnectionStringBuilder_RoundTripsDataSource()
    {
        var b = new SndbConnectionStringBuilder { DataSource = "C:/data/x" };
        Assert.Contains("Data Source", b.ConnectionString);
        var parsed = new SndbConnectionStringBuilder(b.ConnectionString);
        Assert.Equal("C:/data/x", parsed.DataSource);
    }

    [Fact]
    public void ConnectionStringBuilder_KeyIsCaseInsensitive()
    {
        var b = new SndbConnectionStringBuilder("DATA SOURCE=./x");
        Assert.Equal("./x", b.DataSource);
    }

    // ── ExecuteNonQuery ───────────────────────────────────────────────────────

    [Fact]
    public void ExecuteNonQuery_CreateMeasurement_ReturnsZero()
    {
        using var c = OpenConn();
        Assert.Equal(0, ExecNonQuery(c, "CREATE MEASUREMENT cpu (host TAG, value FIELD FLOAT)"));
    }

    [Fact]
    public void ExecuteNonQuery_Insert_ReturnsRowCount()
    {
        using var c = OpenConn();
        EnsureCpuSchema(c);
        int n = ExecNonQuery(c,
            "INSERT INTO cpu (time, host, value) VALUES (1000, 'a', 1.5), (2000, 'a', 2.5)");
        Assert.Equal(2, n);
    }

    [Fact]
    public void ExecuteNonQuery_Delete_ReturnsTombstoneCount()
    {
        using var c = OpenConn();
        EnsureCpuSchema(c);
        ExecNonQuery(c, "INSERT INTO cpu (time, host, value) VALUES (1000, 'a', 1.5)");
        // 1 series × 1 field = 1 tombstone
        int n = ExecNonQuery(c, "DELETE FROM cpu WHERE host = 'a'");
        Assert.Equal(1, n);
    }

    [Fact]
    public void ExecuteNonQuery_Select_ReturnsMinusOne()
    {
        using var c = OpenConn();
        EnsureCpuSchema(c);
        Assert.Equal(-1, ExecNonQuery(c, "SELECT * FROM cpu"));
    }

    // ── ExecuteScalar ─────────────────────────────────────────────────────────

    [Fact]
    public void ExecuteScalar_SelectCount_ReturnsLong()
    {
        using var c = OpenConn();
        EnsureCpuSchema(c);
        ExecNonQuery(c, "INSERT INTO cpu (time, host, value) VALUES (1000, 'a', 1), (2000, 'a', 2), (3000, 'a', 3)");
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM cpu";
        var v = cmd.ExecuteScalar();
        Assert.Equal(3L, v);
    }

    [Fact]
    public void ExecuteScalar_SelectEmpty_ReturnsNull()
    {
        using var c = OpenConn();
        EnsureCpuSchema(c);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM cpu WHERE host = 'never'";
        Assert.Null(cmd.ExecuteScalar());
    }

    // ── ExecuteReader (SELECT) ────────────────────────────────────────────────

    [Fact]
    public void ExecuteReader_Select_ReadsAllRows()
    {
        using var c = OpenConn();
        EnsureCpuSchema(c);
        ExecNonQuery(c, "INSERT INTO cpu (time, host, value) VALUES (1000, 'a', 1.5), (2000, 'a', 2.5)");
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT time, host, value FROM cpu";
        using var r = cmd.ExecuteReader();

        Assert.Equal(3, r.FieldCount);
        Assert.Equal("time", r.GetName(0));
        Assert.True(r.HasRows);

        Assert.True(r.Read());
        Assert.Equal(1000L, r.GetInt64(0));
        Assert.Equal("a", r.GetString(1));
        Assert.Equal(1.5, r.GetDouble(2));

        Assert.True(r.Read());
        Assert.Equal(2000L, r.GetInt64(0));
        Assert.False(r.Read());

        Assert.Equal(-1, r.RecordsAffected);
    }

    [Fact]
    public void ExecuteReader_Select_GetOrdinalAndIndexer()
    {
        using var c = OpenConn();
        EnsureCpuSchema(c);
        ExecNonQuery(c, "INSERT INTO cpu (time, host, value) VALUES (1000, 'a', 9.0)");
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT time, value FROM cpu";
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal(0, r.GetOrdinal("time"));
        Assert.Equal(1, r.GetOrdinal("value"));
        Assert.Equal(9.0, (double)r["value"]);
        Assert.Throws<IndexOutOfRangeException>(() => r.GetOrdinal("nope"));
    }

    [Fact]
    public void ExecuteReader_Select_IsDBNullForMissingField()
    {
        using var c = OpenConn();
        ExecNonQuery(c, "CREATE MEASUREMENT m (host TAG, a FIELD FLOAT, b FIELD FLOAT)");
        ExecNonQuery(c, "INSERT INTO m (time, host, a) VALUES (1000, 'h', 1.0)");
        ExecNonQuery(c, "INSERT INTO m (time, host, b) VALUES (2000, 'h', 2.0)");
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT time, a, b FROM m";
        using var r = cmd.ExecuteReader();

        Assert.True(r.Read());
        Assert.Equal(1000L, r.GetInt64(0));
        Assert.False(r.IsDBNull(1));
        Assert.True(r.IsDBNull(2));

        Assert.True(r.Read());
        Assert.True(r.IsDBNull(1));
        Assert.False(r.IsDBNull(2));
    }

    [Fact]
    public void ExecuteReader_NonQuery_HasZeroRowsAndRecordsAffected()
    {
        using var c = OpenConn();
        EnsureCpuSchema(c);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO cpu (time, host, value) VALUES (1000, 'a', 1)";
        using var r = cmd.ExecuteReader();
        Assert.False(r.HasRows);
        Assert.False(r.Read());
        Assert.Equal(1, r.RecordsAffected);
    }

    [Fact]
    public void ExecuteReader_GetFieldType_InferredFromFirstNonNullRow()
    {
        using var c = OpenConn();
        EnsureCpuSchema(c);
        ExecNonQuery(c, "INSERT INTO cpu (time, host, value) VALUES (1000, 'a', 1.5)");
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT time, host, value FROM cpu";
        using var r = cmd.ExecuteReader();
        Assert.Equal(typeof(long), r.GetFieldType(0));
        Assert.Equal(typeof(string), r.GetFieldType(1));
        Assert.Equal(typeof(double), r.GetFieldType(2));
    }

    [Fact]
    public void ExecuteReader_GeoPointColumn_ReturnsGeoPointStruct()
    {
        using var c = OpenConn();
        ExecNonQuery(c, "CREATE MEASUREMENT vehicle (device TAG, position FIELD GEOPOINT)");

        using (var ins = c.CreateCommand())
        {
            ins.CommandText = "INSERT INTO vehicle (time, device, position) VALUES (@t, @d, @p)";
            ins.Parameters.AddWithValue("@t", 1000L);
            ins.Parameters.AddWithValue("@d", "car-1");
            ins.Parameters.AddWithValue("@p", new GeoPoint(39.9042, 116.4074));
            Assert.Equal(1, ins.ExecuteNonQuery());
        }

        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT position FROM vehicle";
        using var r = cmd.ExecuteReader();

        Assert.Equal(typeof(GeoPoint), r.GetFieldType(0));
        Assert.True(r.Read());
        Assert.Equal(new GeoPoint(39.9042, 116.4074), Assert.IsType<GeoPoint>(r.GetValue(0)));
    }

    [Fact]
    public void ExecuteReader_TableBlobColumn_SupportsGetBytes()
    {
        using var c = OpenConn();
        Assert.Equal(0, ExecNonQuery(c, "CREATE TABLE files (id INT, payload BLOB, PRIMARY KEY (id))"));

        using (var ins = c.CreateCommand())
        {
            ins.CommandText = "INSERT INTO files (id, payload) VALUES (@id, @payload)";
            ins.Parameters.AddWithValue("@id", 1L);
            ins.Parameters.AddWithValue("@payload", new byte[] { 1, 2, 3, 4 });
            Assert.Equal(1, ins.ExecuteNonQuery());
        }

        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT payload FROM files WHERE id = 1";
        using var r = cmd.ExecuteReader();
        Assert.Equal(typeof(byte[]), r.GetFieldType(0));
        Assert.True(r.Read());

        Assert.Equal(4, r.GetBytes(0, 0, null, 0, 0));
        var buffer = new byte[2];
        Assert.Equal(2, r.GetBytes(0, 1, buffer, 0, buffer.Length));
        Assert.Equal(new byte[] { 2, 3 }, buffer);
    }

    // ── Parameters ────────────────────────────────────────────────────────────

    [Fact]
    public void Parameters_AtName_StringValue_IsEscaped()
    {
        using var c = OpenConn();
        EnsureCpuSchema(c);
        ExecNonQuery(c, "INSERT INTO cpu (time, host, value) VALUES (1000, 'a', 1)");
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT host FROM cpu WHERE host = @h";
        cmd.Parameters.AddWithValue("@h", "a");
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal("a", r.GetString(0));
    }

    [Fact]
    public void Parameters_NumericAndBool_FormatLiteral()
    {
        using var c = OpenConn();
        ExecNonQuery(c, "CREATE MEASUREMENT m (host TAG, v FIELD FLOAT, b FIELD BOOL, i FIELD INT)");
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO m (time, host, v, b, i) VALUES (@t, @h, @v, @b, @i)";
        cmd.Parameters.AddWithValue("@t", 12345L);
        cmd.Parameters.AddWithValue("@h", "x");
        cmd.Parameters.AddWithValue("@v", 3.5);
        cmd.Parameters.AddWithValue("@b", true);
        cmd.Parameters.AddWithValue("@i", 42);
        Assert.Equal(1, cmd.ExecuteNonQuery());

        using var sel = c.CreateCommand();
        sel.CommandText = "SELECT v, b, i FROM m WHERE host = @h";
        sel.Parameters.AddWithValue("@h", "x");
        using var r = sel.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal(3.5, r.GetDouble(0));
        Assert.True(r.GetBoolean(1));
        Assert.Equal(42L, r.GetInt64(2));
    }

    [Fact]
    public void Parameters_StringWithSingleQuote_IsEscaped()
    {
        // 防止 SQL 注入：参数值中的 ' 必须被转义为 ''
        using var c = OpenConn();
        EnsureCpuSchema(c);
        ExecNonQuery(c, "INSERT INTO cpu (time, host, value) VALUES (1000, 'O''Brien', 1)");

        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT host FROM cpu WHERE host = @h";
        cmd.Parameters.AddWithValue("@h", "O'Brien");
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal("O'Brien", r.GetString(0));
    }

    [Fact]
    public void Parameters_MissingValue_Throws()
    {
        using var c = OpenConn();
        EnsureCpuSchema(c);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT * FROM cpu WHERE host = @missing";
        Assert.Throws<InvalidOperationException>(() => cmd.ExecuteReader());
    }

    [Fact]
    public void Parameters_DoNotSubstituteInsideStringLiteral()
    {
        using var c = OpenConn();
        EnsureCpuSchema(c);
        // SQL 字符串字面量 '@h' 内不应被替换；这里 host 写死为 '@h'
        ExecNonQuery(c, "INSERT INTO cpu (time, host, value) VALUES (1000, '@h', 1)");
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT host FROM cpu WHERE host = '@h'";
        cmd.Parameters.AddWithValue("@h", "wrong");
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
        Assert.Equal("@h", r.GetString(0));
    }

    [Fact]
    public void Parameters_ColonName_AlsoSupported()
    {
        using var c = OpenConn();
        EnsureCpuSchema(c);
        ExecNonQuery(c, "INSERT INTO cpu (time, host, value) VALUES (1000, 'a', 1)");
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT host FROM cpu WHERE host = :h";
        cmd.Parameters.AddWithValue(":h", "a");
        using var r = cmd.ExecuteReader();
        Assert.True(r.Read());
    }

    [Fact]
    public void Parameters_NullValueRendersSqlNull()
    {
        // null 参数会被替换成 NULL；DELETE 使用 NULL tag 比较应抛错（解析层），保证替换安全
        using var c = OpenConn();
        EnsureCpuSchema(c);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT host FROM cpu WHERE host = @h";
        cmd.Parameters.AddWithValue("@h", null);
        Assert.ThrowsAny<Exception>(() => cmd.ExecuteReader());
    }

    // ── Command lifecycle ─────────────────────────────────────────────────────

    [Fact]
    public void ExecuteNonQuery_NoConnection_Throws()
    {
        using var cmd = new SndbCommand("CREATE MEASUREMENT cpu (host TAG, v FIELD FLOAT)");
        Assert.Throws<InvalidOperationException>(() => cmd.ExecuteNonQuery());
    }

    [Fact]
    public void ExecuteNonQuery_ConnectionClosed_Throws()
    {
        using var c = new SndbConnection(ConnString);
        using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE MEASUREMENT cpu (host TAG, v FIELD FLOAT)";
        Assert.Throws<InvalidOperationException>(() => cmd.ExecuteNonQuery());
    }

    [Fact]
    public void ExecuteNonQuery_EmptyText_Throws()
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "  ";
        Assert.Throws<InvalidOperationException>(() => cmd.ExecuteNonQuery());
    }

    [Fact]
    public void CommandType_NonText_Throws()
    {
        using var cmd = new SndbCommand();
        Assert.Throws<NotSupportedException>(() => cmd.CommandType = CommandType.StoredProcedure);
    }

    [Fact]
    public void Reader_CloseConnection_ClosesUnderlying()
    {
        var c = OpenConn();
        EnsureCpuSchema(c);
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM cpu";
            using var r = cmd.ExecuteReader(CommandBehavior.CloseConnection);
            // 让 reader 释放时连同关闭连接
        }
        Assert.Equal(ConnectionState.Closed, c.State);
    }

    // ── SQL Console 风格元命令（USE / current_database） ──────────────────────────

    [Fact]
    public void SelectCurrentDatabase_Embedded_ReturnsDataSourcePath()
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT current_database()";
        var v = cmd.ExecuteScalar();
        Assert.Equal(c.Database, v);
    }

    [Fact]
    public void ShowCurrentDatabase_Embedded_ReturnsDataSourcePath()
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SHOW CURRENT_DATABASE";
        using var reader = cmd.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("current_database", reader.GetName(0));
        Assert.Equal(c.Database, reader.GetValue(0));
    }

    [Fact]
    public void Use_Embedded_Throws()
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "USE foo";
        var ex = Assert.Throws<NotSupportedException>(() => cmd.ExecuteNonQuery());
        Assert.Contains("USE", ex.Message);
    }

    [Fact]
    public void ChangeDatabase_Embedded_Throws()
    {
        using var c = OpenConn();
        Assert.Throws<NotSupportedException>(() => c.ChangeDatabase("foo"));
    }
}
