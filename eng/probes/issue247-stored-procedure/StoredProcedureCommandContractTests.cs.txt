using System.Data;
using System.Data.Common;
using SonnetDB.Data;
using SonnetDB.Exceptions;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Data;

/// <summary>#247 的标准 ADO 正向验收草稿；准备阶段不得将预期失败写成已支持。</summary>
public sealed class StoredProcedureCommandContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-sp247-" + Guid.NewGuid().ToString("N"));

    /// <summary>清理本用例独占目录。</summary>
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>已有 CALL 的成功只作为基线控制。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TextCall_ExistingInputProcedure_ReturnsValue(bool asynchronous)
    {
        using var connection = OpenInputFixture();
        using var command = new SndbCommand("CALL echo_value(4,2)", connection);
        Assert.Equal(42L, asynchronous ? await command.ExecuteScalarAsync() : command.ExecuteScalar());
    }

    /// <summary>六个公开执行入口必须按参数名绑定，不能依赖集合顺序。</summary>
    [Theory]
    [InlineData("scalar", false)]
    [InlineData("scalar", true)]
    [InlineData("reader", false)]
    [InlineData("reader", true)]
    [InlineData("nonquery", false)]
    [InlineData("nonquery", true)]
    public async Task StoredProcedure_NamedInputs_SupportsEveryExecutionEntry(string entry, bool asynchronous)
    {
        using var connection = OpenInputFixture();
        using var command = new SndbCommand(entry == "nonquery" ? "write_value" : "echo_value", connection)
        {
            CommandType = CommandType.StoredProcedure,
        };
        command.Parameters.Add(new SndbParameter("@p_right", 2L));
        command.Parameters.Add(new SndbParameter("p_left", 4L));
        if (entry == "scalar")
            Assert.Equal(42L, asynchronous ? await command.ExecuteScalarAsync() : command.ExecuteScalar());
        else if (entry == "nonquery")
        {
            Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            Assert.Equal(42L, Scalar(connection, "SELECT value FROM items WHERE id=4"));
        }
        else
        {
            using DbDataReader reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
            Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
            Assert.Equal("result", reader.GetName(0));
            Assert.Equal(42L, reader.GetInt64(0));
            Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        }
    }

    /// <summary>输出与返回值须在完成后回写；Reader 持有期间保留调用前值。</summary>
    [Theory]
    [InlineData("scalar", false)]
    [InlineData("scalar", true)]
    [InlineData("reader", false)]
    [InlineData("reader", true)]
    [InlineData("nonquery", false)]
    [InlineData("nonquery", true)]
    public async Task StoredProcedure_OutputDirections_WriteBackAfterCompletion(string entry, bool asynchronous)
    {
        using var connection = OpenInputFixture();
        Execute(connection, OutputDefinition);
        using var command = OutputCommand(connection);
        if (entry == "reader")
        {
            DbDataReader reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
            try
            {
                Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
                Assert.Equal(12L, reader.GetInt64(0));
                Assert.Equal(10L, command.Parameters["p_state"].Value);
                Assert.Equal("unchanged", command.Parameters["p_echo"].Value);
                Assert.Equal(-1L, command.Parameters["status"].Value);
            }
            finally
            {
                if (asynchronous) await reader.DisposeAsync();
                else reader.Dispose();
            }
        }
        else if (entry == "scalar")
            Assert.Equal(12L, asynchronous ? await command.ExecuteScalarAsync() : command.ExecuteScalar());
        else
            _ = asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery();
        Assert.Equal(12L, command.Parameters["p_state"].Value);
        Assert.Equal("done", command.Parameters["p_echo"].Value);
        Assert.Equal(7L, command.Parameters["status"].Value);
    }

    /// <summary>失败不能发布部分输出，且调用方事务内的过程写入必须回滚到调用前。</summary>
    [Fact]
    public void StoredProcedure_Failure_RetainsOutputValuesAndCallerTransaction()
    {
        using var connection = OpenInputFixture();
        Execute(connection, "CREATE PROCEDURE fail_value(OUT p_echo STRING) LANGUAGE SQL AS BEGIN SET @p_echo='partial'; INSERT INTO items(id,value) VALUES(9,1),(9,2); END");
        using var transaction = connection.BeginTransaction();
        using var command = new SndbCommand("fail_value", connection)
        {
            CommandType = CommandType.StoredProcedure,
            Transaction = transaction,
        };
        command.Parameters.Add(new SndbParameter("p_echo", "unchanged") { Direction = ParameterDirection.Output, DbType = DbType.String });
        var failure = Assert.Throws<RoutineExecutionException>(() => command.ExecuteNonQuery());
        Assert.Equal(TableConstraintException.UniqueViolation, failure.Code);
        Assert.Equal("unchanged", command.Parameters["p_echo"].Value);
        transaction.Commit();
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM items"));
        Execute(connection, "INSERT INTO items(id,value) VALUES(1,1)");
    }

    /// <summary>调用必须属于原 ADO 事务，不能另建会话提前提交。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoredProcedure_CallerTransaction_RollsBackWrites(bool asynchronous)
    {
        using var connection = OpenInputFixture();
        using (var transaction = connection.BeginTransaction())
        using (var command = new SndbCommand("write_value", connection) { CommandType = CommandType.StoredProcedure, Transaction = transaction })
        {
            command.Parameters.Add(new SndbParameter("p_left", 4L));
            command.Parameters.Add(new SndbParameter("p_right", 2L));
            Assert.Equal(1, asynchronous ? await command.ExecuteNonQueryAsync() : command.ExecuteNonQuery());
            if (asynchronous) await transaction.RollbackAsync();
            else transaction.Rollback();
        }
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM items"));
    }

    /// <summary>取消发生在调用前时不得写入，释放命令租约后同一命令可再次执行。</summary>
    [Fact]
    public async Task StoredProcedure_PreCancelledCall_ReleasesLeaseAndDoesNotWrite()
    {
        using var connection = OpenInputFixture();
        using var command = new SndbCommand("write_value", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add(new SndbParameter("p_left", 4L));
        command.Parameters.Add(new SndbParameter("p_right", 2L));
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command.ExecuteNonQueryAsync(stop.Token));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM items"));
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    /// <summary>过程全部结果应顺序可读，不能沿用仅最后 SELECT 的旧行为。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoredProcedure_TwoSelects_ReturnsOrderedResults(bool asynchronous)
    {
        using var connection = OpenInputFixture();
        Execute(connection, "CREATE PROCEDURE two_values() LANGUAGE SQL AS BEGIN SELECT 11 AS first; SELECT 'second' AS second; END");
        using var command = new SndbCommand("two_values", connection) { CommandType = CommandType.StoredProcedure };
        using DbDataReader reader = asynchronous ? await command.ExecuteReaderAsync() : command.ExecuteReader();
        Assert.Equal("first", reader.GetName(0));
        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
        Assert.Equal(11L, reader.GetInt64(0));
        Assert.True(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
        Assert.Equal("second", reader.GetName(0));
        Assert.True(asynchronous ? await reader.ReadAsync() : reader.Read());
        Assert.Equal("second", reader.GetString(0));
        Assert.False(asynchronous ? await reader.NextResultAsync() : reader.NextResult());
    }

    /// <summary>提前关闭 Reader 仍需发布成功输出，并按 CloseConnection 释放连接。</summary>
    [Fact]
    public void StoredProcedure_EarlyReaderClose_WritesOutputsAndClosesConnection()
    {
        using var connection = OpenInputFixture();
        Execute(connection, OutputDefinition);
        using var command = OutputCommand(connection);
        DbDataReader reader = command.ExecuteReader(CommandBehavior.CloseConnection);
        reader.Close();
        reader.Close();
        Assert.Equal(12L, command.Parameters["p_state"].Value);
        Assert.Equal(7L, command.Parameters["status"].Value);
        Assert.Equal(ConnectionState.Closed, connection.State);
        connection.Open();
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM items"));
    }

    /// <summary>SqlSugar 会先释放命令；输出参数仍必须由存活的 Reader 在关闭时回写。</summary>
    [Fact]
    public void StoredProcedure_DisposedCommand_ReaderRetainsOutputCompletion()
    {
        using var connection = OpenInputFixture();
        Execute(connection, OutputDefinition);
        var command = OutputCommand(connection);
        SndbParameter output = (SndbParameter)command.Parameters["p_echo"];
        using DbDataReader reader = command.ExecuteReader();
        command.Dispose();
        Assert.Equal("unchanged", output.Value);
        Assert.True(reader.Read());
        Assert.Equal(12L, reader.GetInt64(0));
        reader.Close();
        Assert.Equal("done", output.Value);
    }

    /// <summary>过程方向与语句必须持久化，数据库 NULL 输出按 ADO 合同成为 DBNull。</summary>
    [Fact]
    public void StoredProcedure_ReopenedDefinition_PreservesDirectionsAndNullOutput()
    {
        using (var initial = OpenInputFixture())
            Execute(initial, "CREATE PROCEDURE null_output(OUT p_value STRING) LANGUAGE SQL AS BEGIN SET @p_value=NULL; RETURN 7; END");
        using var connection = new SndbConnection("Data Source=" + _root);
        connection.Open();
        using var command = new SndbCommand("null_output", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add(new SndbParameter("p_value", "unchanged") { Direction = ParameterDirection.Output, DbType = DbType.String });
        command.Parameters.Add(new SndbParameter("status", -1L) { Direction = ParameterDirection.ReturnValue, DbType = DbType.Int64 });
        command.ExecuteNonQuery();
        Assert.Equal(DBNull.Value, command.Parameters["p_value"].Value);
        Assert.Equal(7L, command.Parameters["status"].Value);
    }

    /// <summary>提前关闭多结果 Reader 只完成一次过程，不能为补输出重新执行过程。</summary>
    [Fact]
    public void StoredProcedure_MultipleResultsEarlyClose_ExecutesWritesOnce()
    {
        using var connection = OpenInputFixture();
        Execute(connection, "CREATE PROCEDURE write_and_select(OUT p_value INT) LANGUAGE SQL AS BEGIN INSERT INTO items(id,value) VALUES(1,42); SELECT 11 AS first; SELECT 'second' AS second; SET @p_value=42; RETURN 7; END");
        using var command = new SndbCommand("write_and_select", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add(new SndbParameter("p_value", -1L) { Direction = ParameterDirection.Output, DbType = DbType.Int64 });
        command.Parameters.Add(new SndbParameter("status", -1L) { Direction = ParameterDirection.ReturnValue, DbType = DbType.Int64 });
        using (DbDataReader reader = command.ExecuteReader())
        {
            Assert.True(reader.Read());
            Assert.Equal("first", reader.GetName(0));
            Assert.Equal(11L, reader.GetInt64(0));
        }
        Assert.Equal(42L, command.Parameters["p_value"].Value);
        Assert.Equal(7L, command.Parameters["status"].Value);
        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM items"));
    }

    private SndbConnection OpenInputFixture()
    {
        var connection = new SndbConnection("Data Source=" + _root);
        connection.Open();
        Execute(connection, "CREATE TABLE items(id INT,value INT,PRIMARY KEY(id))");
        Execute(connection, "CREATE PROCEDURE echo_value(IN p_left INT,IN p_right INT) LANGUAGE SQL AS BEGIN SELECT @p_left*10+@p_right AS result; END");
        Execute(connection, "CREATE PROCEDURE write_value(IN p_left INT,IN p_right INT) LANGUAGE SQL AS BEGIN INSERT INTO items(id,value) VALUES(@p_left,@p_left*10+@p_right); END");
        return connection;
    }

    private const string OutputDefinition = "CREATE PROCEDURE exchange_value(IN p_add INT,INOUT p_state INT,OUT p_echo STRING) LANGUAGE SQL AS BEGIN SET @p_state=@p_state+@p_add; SET @p_echo='done'; SELECT @p_state AS result; RETURN 7; END";

    private static SndbCommand OutputCommand(SndbConnection connection)
    {
        var command = new SndbCommand("exchange_value", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add(new SndbParameter("p_echo", "unchanged") { Direction = ParameterDirection.Output, DbType = DbType.String });
        command.Parameters.Add(new SndbParameter("p_add", 2L));
        command.Parameters.Add(new SndbParameter("p_state", 10L) { Direction = ParameterDirection.InputOutput, DbType = DbType.Int64 });
        command.Parameters.Add(new SndbParameter("status", -1L) { Direction = ParameterDirection.ReturnValue, DbType = DbType.Int64 });
        return command;
    }

    private static void Execute(SndbConnection connection, string sql)
    {
        using var command = new SndbCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SndbConnection connection, string sql)
    {
        using var command = new SndbCommand(sql, connection);
        return command.ExecuteScalar();
    }
}
