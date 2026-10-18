using System.Data;
using SonnetDB.Data;
using SonnetDB.Data.Internal;
using SonnetDB.Exceptions;
using Xunit;

namespace SonnetDB.Core.Tests.Ado;

public sealed class StoredProcedureSnapshotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-sp-snapshot-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    [Fact]
    public void Reader_ParameterCollectionAndDeclarationsChanged_PublishesOnlyFrozenTargets()
    {
        using var connection = Open();
        using var command = Call(connection);
        var original = command.Parameters["output"];
        using var reader = command.ExecuteReader();
        command.Parameters.Clear();
        var replacement = command.Parameters.AddWithValue("output", "replacement");
        original.DbType = DbType.Int64;
        original.Size = 1;
        Assert.True(reader.Read());
        Assert.Equal(42L, reader.GetInt64(0));
        Assert.Equal("old", original.Value);
        reader.Close();
        reader.Close();
        Assert.Equal("done", original.Value);
        Assert.Equal("replacement", replacement.Value);
    }

    [Fact]
    public async Task Reader_CancelledRead_DoesNotPublishOutputsOnDispose()
    {
        using var connection = Open();
        using var command = Call(connection);
        using var reader = await command.ExecuteReaderAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(cancellation.Token));
        reader.Close();
        Assert.Equal("old", command.Parameters["output"].Value);
    }

    [Fact]
    public void Completion_OutputSizeViolation_RollsBackImplicitWriteBeforeReturning()
    {
        using var connection = Open();
        using var command = Call(connection);
        command.Parameters["output"].Size = 2;
        Assert.Throws<RoutineExecutionException>(() => command.ExecuteNonQuery());
        Assert.Equal("old", command.Parameters["output"].Value);
        using var check = new SndbCommand("SELECT COUNT(*) FROM items", connection);
        Assert.Equal(0L, check.ExecuteScalar());
    }

    [Theory]
    [InlineData("write_value(); DROP TABLE items")]
    [InlineData("CALL write_value()")]
    [InlineData("write_value -- trailing comment")]
    public void ProcedureCommand_InvalidName_RejectsBeforeWriting(string name)
    {
        using var connection = Open();
        using var command = Call(connection);
        command.CommandText = name;
        Assert.Throws<ArgumentException>(() => command.ExecuteNonQuery());
        using var check = new SndbCommand("SELECT COUNT(*) FROM items", connection);
        Assert.Equal(0L, check.ExecuteScalar());
    }

    [Fact]
    public void TextCommand_OutputDirection_IsExplicitlyRejected()
    {
        using var connection = Open();
        using var command = new SndbCommand("SELECT 1", connection);
        command.Parameters.Add(new SndbParameter("output", 0L) { Direction = ParameterDirection.Output });
        Assert.Throws<NotSupportedException>(() => command.ExecuteScalar());
        Assert.Throws<ArgumentOutOfRangeException>(() => command.Parameters[0].Direction = (ParameterDirection)99);
    }

    [Fact]
    public void Reader_LeaseCleanupFails_LeavesOutputsUnchangedAndClosesOnce()
    {
        using var command = new SndbCommand("write_value") { CommandType = CommandType.StoredProcedure };
        var output = new SndbParameter("output", "old") { Direction = ParameterDirection.Output, DbType = DbType.String };
        command.Parameters.Add(output);
        var result = new ProcedureExecutionResultAdapter([], new Dictionary<string, object?> { ["output"] = "done" },
            0, 1, ProcedureCallSnapshot.Create(command.CommandText, command.Parameters));
        int releases = 0;
        using var reader = new SndbDataReader(result, CommandBehavior.Default, null,
            releaseExecutionLease: () => { releases++; throw new IOException("lease cleanup failed"); });
        Assert.Equal("lease cleanup failed", Assert.Throws<IOException>(() => reader.Close()).Message);
        reader.Close();
        Assert.Equal(1, releases);
        Assert.True(reader.IsClosed);
        Assert.Equal("old", output.Value);
    }

    [Fact]
    public void Reader_ClosePublishesOnce_ReleasesCommandForReuse()
    {
        using var connection = Open();
        using var command = Call(connection);
        var output = command.Parameters["output"];
        using var reader = command.ExecuteReader();
        Assert.Throws<InvalidOperationException>(() => command.ExecuteScalar());
        Assert.Equal("old", output.Value);
        reader.Close();
        Assert.Equal("done", output.Value);
        command.CommandType = CommandType.Text;
        command.Parameters.Clear();
        command.CommandText = "SELECT COUNT(*) FROM items";
        Assert.Equal(1L, command.ExecuteScalar());
        output.Value = "after close";
        reader.Close();
        Assert.Equal("after close", output.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Completion_OutputTypeViolation_RollsBackCallAndPreservesCallerWrites(bool callerTransaction)
    {
        using var connection = Open();
        using var transaction = callerTransaction ? connection.BeginTransaction() : null;
        if (transaction is not null)
        {
            using var before = new SndbCommand("INSERT INTO items(id) VALUES(50)", connection) { Transaction = transaction };
            before.ExecuteNonQuery();
        }
        using var command = Call(connection);
        command.Transaction = transaction;
        command.Parameters["output"].DbType = DbType.Int64;
        Assert.Throws<RoutineExecutionException>(() => command.ExecuteNonQuery());
        Assert.Equal("old", command.Parameters["output"].Value);
        transaction?.Commit();
        using var check = new SndbCommand("SELECT COUNT(*) FROM items", connection);
        Assert.Equal(callerTransaction ? 1L : 0L, check.ExecuteScalar());
        if (callerTransaction)
        {
            check.CommandText = "SELECT id FROM items";
            Assert.Equal(50L, check.ExecuteScalar());
        }
    }

    [Theory]
    [InlineData("\"MixedName\"")]
    [InlineData("\"say\"\"hello\"")]
    public void ProcedureName_ExactQuotedSpellingAndEscapedQuote_ResolvesOriginalName(string name)
    {
        using var connection = Open();
        using (var create = new SndbCommand("CREATE PROCEDURE " + name + "() LANGUAGE SQL AS BEGIN SELECT 42 AS value; END", connection))
            create.ExecuteNonQuery();
        using var call = new SndbCommand(name, connection) { CommandType = CommandType.StoredProcedure };
        Assert.Equal(42L, call.ExecuteScalar());
    }

    private SndbConnection Open()
    {
        var connection = new SndbConnection("Data Source=" + _root);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE items(id INT,PRIMARY KEY(id))";
        command.ExecuteNonQuery();
        command.CommandText = "CREATE PROCEDURE write_value(OUT output STRING) LANGUAGE SQL AS BEGIN INSERT INTO items(id) VALUES(1); SET @output='done'; SELECT 42 AS result; END";
        command.ExecuteNonQuery();
        return connection;
    }

    private static SndbCommand Call(SndbConnection connection)
    {
        var command = new SndbCommand("write_value", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add(new SndbParameter("output", "old") { Direction = ParameterDirection.Output, DbType = DbType.String });
        return command;
    }
}
