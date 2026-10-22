using System.Data;
using SonnetDB.Data;
using Xunit;

namespace SonnetDB.Core.Tests.Ado;

/// <summary>
/// <see cref="SndbCommand"/> 的 <see cref="CommandType.TableDirect"/> 批量入库快路径测试（PR #43）。
/// </summary>
public sealed class TsdbBulkIngestAdoTests : IDisposable
{
    private readonly string _root;

    public TsdbBulkIngestAdoTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "sndb-bulk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }

    private SndbConnection OpenConn()
    {
        var c = new SndbConnection($"Data Source={_root}");
        c.Open();
        return c;
    }

    private static int Exec(SndbConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteNonQuery();
    }

    private static void EnsureCpuSchema(SndbConnection c)
        => Exec(c, "CREATE MEASUREMENT cpu (host TAG, value FIELD FLOAT)");

    [Fact]
    public void TableDirect_LineProtocol_WritesAllPoints()
    {
        using var c = OpenConn();
        EnsureCpuSchema(c);

        using var cmd = c.CreateCommand();
        cmd.CommandType = CommandType.TableDirect;
        cmd.CommandText = "cpu,host=srv1 value=1 1\ncpu,host=srv1 value=2 2\ncpu,host=srv2 value=3 3";
        var written = cmd.ExecuteNonQuery();

        Assert.Equal(3, written);
    }

    [Fact]
    public void TableDirect_LineProtocolWithMeasurementPrefix_AppliedToAllLines()
    {
        using var c = OpenConn();
        EnsureCpuSchema(c);

        using var cmd = c.CreateCommand();
        cmd.CommandType = CommandType.TableDirect;
        // 首行作为 measurement 前缀；后续行的 measurement 字段被 override
        cmd.CommandText = "cpu\nignored,host=srv1 value=1 1\nignored,host=srv2 value=2 2";
        var written = cmd.ExecuteNonQuery();

        Assert.Equal(2, written);
    }

    [Fact]
    public void TableDirect_Json_WritesAllPoints()
    {
        using var c = OpenConn();
        EnsureCpuSchema(c);

        using var cmd = c.CreateCommand();
        cmd.CommandType = CommandType.TableDirect;
        cmd.CommandText = """
        {
          "m": "cpu",
          "points": [
            {"t": 1, "tags": {"host": "srv1"}, "fields": {"value": 1.5}},
            {"t": 2, "tags": {"host": "srv2"}, "fields": {"value": 2.5}}
          ]
        }
        """;
        var written = cmd.ExecuteNonQuery();

        Assert.Equal(2, written);
    }

    [Fact]
    public void TableDirect_BulkInsertValues_WritesAllPoints()
    {
        using var c = OpenConn();
        EnsureCpuSchema(c);

        using var cmd = c.CreateCommand();
        cmd.CommandType = CommandType.TableDirect;
        cmd.CommandText = "INSERT INTO cpu(host, value, time) VALUES "
            + "('srv1', 1.0, 1),"
            + "('srv2', 2.0, 2),"
            + "('srv3', 3.0, 3)";
        var written = cmd.ExecuteNonQuery();

        Assert.Equal(3, written);
    }

    [Fact]
    public void TableDirect_BulkInsertValues_UnknownColumn_AutoExtendsSchema()
    {
        using var c = OpenConn();
        EnsureCpuSchema(c);

        using var cmd = c.CreateCommand();
        cmd.CommandType = CommandType.TableDirect;
        cmd.CommandText = "INSERT INTO cpu(host, nope, time) VALUES ('a', 1, 1)";
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    [Fact]
    public void TableDirect_OnErrorSkipParameter_SkipsBadLines()
    {
        using var c = OpenConn();
        EnsureCpuSchema(c);

        using var cmd = c.CreateCommand();
        cmd.CommandType = CommandType.TableDirect;
        cmd.CommandText = "cpu,host=a value=1 1\nbad-line-no-fields\ncpu,host=a value=3 3";
        var p = cmd.CreateParameter();
        p.ParameterName = "onerror";
        p.Value = "skip";
        cmd.Parameters.Add(p);

        var written = cmd.ExecuteNonQuery();
        Assert.Equal(2, written);
    }

    [Fact]
    public void TableDirect_FlushParameter_WritesAndDoesNotThrow()
    {
        using var c = OpenConn();
        EnsureCpuSchema(c);

        using var cmd = c.CreateCommand();
        cmd.CommandType = CommandType.TableDirect;
        cmd.CommandText = "cpu,host=a value=1 1";
        var p = cmd.CreateParameter();
        p.ParameterName = "flush";
        p.Value = "true";
        cmd.Parameters.Add(p);

        var written = cmd.ExecuteNonQuery();
        Assert.Equal(1, written);
    }

    [Fact]
    public void TableDirect_MeasurementParameter_OverridesPayload()
    {
        using var c = OpenConn();
        EnsureCpuSchema(c);

        using var cmd = c.CreateCommand();
        cmd.CommandType = CommandType.TableDirect;
        cmd.CommandText = "ignored,host=srv1 value=1 1";
        var p = cmd.CreateParameter();
        p.ParameterName = "measurement";
        p.Value = "cpu";
        cmd.Parameters.Add(p);

        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    [Fact]
    public void CommandType_StoredProcedureAndTableDirect_CanShareCommand()
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "CREATE PROCEDURE scalar_probe() LANGUAGE SQL AS BEGIN SELECT 42 AS value; END";
        cmd.ExecuteNonQuery();
        cmd.CommandType = CommandType.TableDirect;
        Assert.Equal(CommandType.TableDirect, cmd.CommandType);
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = "scalar_probe";
        Assert.Equal(42L, cmd.ExecuteScalar());
    }

    [Fact]
    public void TableDirect_AndPlainSqlCoexist_OnSameConnection()
    {
        using var c = OpenConn();
        EnsureCpuSchema(c);

        // 1) TableDirect 写入
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandType = CommandType.TableDirect;
            cmd.CommandText = "cpu,host=srv1 value=10 100\ncpu,host=srv1 value=20 200";
            Assert.Equal(2, cmd.ExecuteNonQuery());
        }

        // 2) 普通 SQL 查询
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT value FROM cpu WHERE host='srv1' AND time >= 100 AND time <= 200";
            using var r = cmd.ExecuteReader();
            int n = 0;
            while (r.Read()) n++;
            Assert.Equal(2, n);
        }
    }
}
