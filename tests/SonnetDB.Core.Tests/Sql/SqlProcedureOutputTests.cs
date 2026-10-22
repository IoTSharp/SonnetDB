using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using SonnetDB.Engine;
using SonnetDB.Exceptions;
using SonnetDB.Routines;
using SonnetDB.Sql;
using SonnetDB.Sql.Ast;
using SonnetDB.Sql.Execution;
using SonnetDB.Tables;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public sealed class SqlProcedureOutputTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-procedure-output-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData(1.5, 2L)]
    [InlineData(2.5, 2L)]
    public void TextCall_LegacyIntegerInput_PreservesExistingConversion(double input, long expected)
    {
        using var database = Open();
        SqlExecutor.Execute(database, "CREATE PROCEDURE legacy_int(IN value INT) LANGUAGE SQL AS BEGIN SELECT @value AS result; END");
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.ExecuteStatement(database,
            new CallProcedureStatement("legacy_int", [LiteralExpression.Float(input)])));
        Assert.Equal(expected, Assert.IsType<long>(result.Rows.Single().Single()));
    }

    [Fact]
    public void TextCall_LegacyNestedInput_PreservesPositionAndConversion()
    {
        using var database = Open();
        SqlExecutor.Execute(database, "CREATE PROCEDURE legacy_child(IN value INT) LANGUAGE SQL AS BEGIN SELECT @value AS result; END");
        SqlExecutor.Execute(database, "CREATE PROCEDURE legacy_parent(IN value FLOAT) LANGUAGE SQL AS BEGIN CALL legacy_child(@value); END");
        var result = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, "CALL legacy_parent(1.5)"));
        Assert.Equal(2L, Assert.IsType<long>(result.Rows.Single().Single()));
    }

    [Fact]
    public void ProcedureParameter_LegacyConstructorAndDeconstruction_PreserveTwoFieldContract()
    {
        var parameter = new SqlProcedureParameter("value", SqlProcedureParameterType.Int64);
        var (name, dataType) = parameter;
        Assert.Equal("value", name);
        Assert.Equal(SqlProcedureParameterType.Int64, dataType);
        Assert.Equal(SqlProcedureParameterDirection.Input, parameter.Direction);
    }

    [Fact]
    public void ExecuteProcedure_NamedDirections_ReturnsOrderedResultsOutputsAndStatus()
    {
        using var database = Open();
        CreateExchange(database);
        var result = Exchange(database);
        Assert.Equal(12L, result.OutputParameters["state"]);
        Assert.Equal("done", result.OutputParameters["echo"]);
        Assert.Equal(7L, result.ReturnValue);
        Assert.Equal(-1, result.RecordsAffected);
        Assert.Equal(2, result.Results.Count);
        Assert.Equal("first", result.Results[0].Columns.Single());
        Assert.Equal(12L, result.Results[0].Rows.Single().Single());
        Assert.Equal("second", result.Results[1].Columns.Single());
        Assert.Null(result.Results[1].Rows.Single().Single());
        Assert.DoesNotContain("p_add", result.OutputParameters.Keys);
        var description = Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, "DESCRIBE PROCEDURE exchange"));
        Assert.Equal("IN p_add INT, INOUT state INT, OUT echo STRING", description.Rows.Single()[1]);
    }

    [Fact]
    public void ExecuteProcedure_Return_StopsBodyAndAccumulatesDmlOnce()
    {
        using var database = Open();
        SqlExecutor.Execute(database, "CREATE TABLE items(id INT, value INT, PRIMARY KEY(id))");
        SqlExecutor.Execute(database, """
            CREATE PROCEDURE change(OUT output INT) LANGUAGE SQL AS BEGIN
              INSERT INTO items(id,value) VALUES(1,10);
              UPDATE items SET value=20 WHERE id=1 RETURNING value;
              SET @output=20;
              RETURN 9;
              INSERT INTO items(id,value) VALUES(2,99);
            END
            """);
        var result = SqlExecutor.ExecuteProcedure(database, "change", [new("output", 999L, SqlProcedureParameterDirection.Output)]);
        Assert.Equal(2, result.RecordsAffected);
        Assert.Equal(9L, result.ReturnValue);
        Assert.Equal(20L, result.OutputParameters["output"]);
        Assert.Equal(20L, result.Results.Single().Rows.Single().Single());
        Assert.Single(Select(database, "SELECT * FROM items").Rows);
    }

    [Fact]
    public void ExecuteProcedure_OutStartsNullAndDefaultStatusIsZero()
    {
        using var database = Open();
        SqlExecutor.Execute(database, "CREATE PROCEDURE unset(OUT value STRING) LANGUAGE SQL AS BEGIN SELECT @value; END");
        var result = SqlExecutor.ExecuteProcedure(database, "unset", [new("value", "ignored", SqlProcedureParameterDirection.Output)]);
        Assert.Null(result.OutputParameters["value"]);
        Assert.Null(result.Results.Single().Rows.Single().Single());
        Assert.Equal(0L, result.ReturnValue);
    }

    [Fact]
    public void ExecuteProcedure_NestedOutputs_PreserveOrderAndParentScope()
    {
        using var database = Open();
        SqlExecutor.Execute(database, """
            CREATE PROCEDURE inner_proc(INOUT state INT,OUT label STRING) LANGUAGE SQL AS BEGIN
              SET @state=@state+2; SET @label='inner'; SELECT @state AS nested; RETURN 99;
            END
            """);
        SqlExecutor.Execute(database, """
            CREATE PROCEDURE outer_proc(INOUT counter INT,OUT text STRING) LANGUAGE SQL AS BEGIN
              SELECT 1 AS before_call; CALL inner_proc(@counter,@text);
              SET @counter=@counter+3; SELECT @counter AS after_call; RETURN 7;
            END
            """);
        var result = SqlExecutor.ExecuteProcedure(database, "outer_proc",
            [new("text", null, SqlProcedureParameterDirection.Output), new("counter", 10L, SqlProcedureParameterDirection.InputOutput)]);
        Assert.Equal(15L, result.OutputParameters["counter"]);
        Assert.Equal("inner", result.OutputParameters["text"]);
        Assert.Equal(7L, result.ReturnValue);
        Assert.Equal(new[] { 1L, 12L, 15L }, result.Results.Select(value => Assert.IsType<long>(value.Rows.Single().Single())));
        Assert.Equal(-1, result.RecordsAffected);
    }

    [Theory]
    [InlineData("SET @input=1")]
    [InlineData("SET @missing=1")]
    public void CreateProcedure_ReadOnlyOrUnknownAssignment_RejectsDefinition(string assignment)
    {
        using var database = Open();
        Assert.Throws<ArgumentException>(() => SqlExecutor.Execute(database,
            $"CREATE PROCEDURE invalid(IN input INT) LANGUAGE SQL AS BEGIN {assignment}; END"));
        Assert.Null(database.Routines.TryGetProcedure("invalid"));
    }

    [Theory]
    [InlineData("NULL")]
    [InlineData("1.5")]
    [InlineData("'bad'")]
    [InlineData("9223372036854775807+1")]
    public void ExecuteProcedure_InvalidReturn_RollsBackWrites(string expression)
    {
        using var database = Open();
        SqlExecutor.Execute(database, "CREATE TABLE items(id INT,PRIMARY KEY(id))");
        SqlExecutor.Execute(database,
            $"CREATE PROCEDURE invalid() LANGUAGE SQL AS BEGIN INSERT INTO items(id) VALUES(1); RETURN {expression}; END");
        Assert.Throws<RoutineExecutionException>(() => SqlExecutor.ExecuteProcedure(database, "invalid", []));
        Assert.Empty(Select(database, "SELECT * FROM items").Rows);
    }

    [Fact]
    public void ExecuteProcedure_ExistingTransactionAndFailure_RetainEarlierWrites()
    {
        using var database = Open();
        SqlExecutor.Execute(database, "CREATE TABLE items(id INT,PRIMARY KEY(id))");
        SqlExecutor.Execute(database, """
            CREATE PROCEDURE fail(OUT value STRING) LANGUAGE SQL AS BEGIN
              SET @value='partial'; INSERT INTO items(id) VALUES(2); RETURN 'invalid status';
            END
            """);
        var transaction = Assert.IsType<SqlTransactionContext>(SqlExecutor.Execute(database, "BEGIN"));
        SqlExecutor.ExecuteStatement(database, null, SqlParser.Parse("INSERT INTO items(id) VALUES(1)"), null, transaction);
        Assert.Throws<RoutineExecutionException>(() => SqlExecutor.ExecuteProcedure(database, "fail",
            [new("value", null, SqlProcedureParameterDirection.Output)], transaction));
        SqlExecutor.ExecuteStatement(database, null, SqlParser.Parse("COMMIT"), null, transaction);
        Assert.Equal(1L, Select(database, "SELECT id FROM items").Rows.Single().Single());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExecuteProcedure_CompletionValidationFailure_RollsBackOnlyCurrentCall(bool callerTransaction)
    {
        using var database = Open();
        SqlExecutor.Execute(database, "CREATE TABLE items(id INT,PRIMARY KEY(id))");
        SqlExecutor.Execute(database, "CREATE PROCEDURE emit(OUT value INT) LANGUAGE SQL AS BEGIN INSERT INTO items(id) VALUES(2); SET @value=2; SELECT 2 AS result; END");
        var transaction = callerTransaction
            ? Assert.IsType<SqlTransactionContext>(SqlExecutor.Execute(database, "BEGIN")) : null;
        if (transaction is not null)
            SqlExecutor.ExecuteStatement(database, null, SqlParser.Parse("INSERT INTO items(id) VALUES(1)"), null, transaction);
        int callbacks = 0;
        Assert.Throws<RoutineExecutionException>(() => SqlExecutor.ExecuteProcedure(database, "emit",
            [new("value", null, SqlProcedureParameterDirection.Output)], transaction,
            validateCompletion: completion =>
            {
                callbacks++;
                Assert.Equal(2L, completion.OutputParameters["value"]);
                Assert.Single(completion.Results);
                throw new InvalidDataException("response encoding budget exceeded");
            }));
        Assert.Equal(1, callbacks);
        if (transaction is not null)
        {
            SqlExecutor.ExecuteStatement(database, null, SqlParser.Parse("COMMIT"), null, transaction);
            Assert.Equal(1L, Select(database, "SELECT id FROM items").Rows.Single().Single());
        }
        else
            Assert.Empty(Select(database, "SELECT * FROM items").Rows);
    }

    [Fact]
    public void ExecuteProcedure_CompletionValidationSuccess_RunsOnceAndCommits()
    {
        using var database = Open();
        SqlExecutor.Execute(database, "CREATE TABLE items(id INT,PRIMARY KEY(id))");
        SqlExecutor.Execute(database, "CREATE PROCEDURE emit(OUT value INT) LANGUAGE SQL AS BEGIN INSERT INTO items(id) VALUES(2); SET @value=2; RETURN 7; END");
        int callbacks = 0;
        var result = SqlExecutor.ExecuteProcedure(database, "emit", [new("value", null, SqlProcedureParameterDirection.Output)],
            validateCompletion: completion =>
            {
                callbacks++;
                Assert.Equal(7L, completion.ReturnValue);
                Assert.Equal(2L, completion.OutputParameters["value"]);
            });
        Assert.Equal(1, callbacks);
        Assert.Equal(1, result.RecordsAffected);
        Assert.Equal(2L, Select(database, "SELECT id FROM items").Rows.Single().Single());
    }

    [Fact]
    public void ExecuteProcedure_DeferredCallerConstraint_DoesNotClaimCommittedTransaction()
    {
        using var database = Open();
        SqlExecutor.Execute(database, "CREATE TABLE items(id INT,value INT,PRIMARY KEY(id))");
        SqlExecutor.Execute(database, "CREATE UNIQUE INDEX ux_items_value ON items(value)");
        SqlExecutor.Execute(database, """
            CREATE PROCEDURE buffered(OUT value STRING) LANGUAGE SQL AS BEGIN
              INSERT INTO items(id,value) VALUES(1,2),(2,2); SET @value='executed';
            END
            """);
        var transaction = Assert.IsType<SqlTransactionContext>(SqlExecutor.Execute(database, "BEGIN"));
        var result = SqlExecutor.ExecuteProcedure(database, "buffered", [new("value", null, SqlProcedureParameterDirection.Output)], transaction);
        Assert.Equal("executed", result.OutputParameters["value"]);
        Assert.Equal(2, result.RecordsAffected);
        Assert.False(transaction.WasCommitted);
        Assert.Throws<TableConstraintException>(() => SqlExecutor.ExecuteStatement(database, null, SqlParser.Parse("COMMIT"), null, transaction));
        Assert.False(transaction.WasCommitted);
        Assert.Empty(Select(database, "SELECT * FROM items").Rows);
    }

    [Fact]
    public void ExecuteProcedure_DuplicateBufferedPrimaryKeys_RollsBackCallerScopeBeforeCompletion()
    {
        using var database = Open();
        SqlExecutor.Execute(database, "CREATE TABLE items(id INT,PRIMARY KEY(id))");
        SqlExecutor.Execute(database, "CREATE PROCEDURE duplicate(OUT value STRING) LANGUAGE SQL AS BEGIN SET @value='partial'; INSERT INTO items(id) VALUES(9),(9); END");
        var transaction = Assert.IsType<SqlTransactionContext>(SqlExecutor.Execute(database, "BEGIN"));
        SqlExecutor.ExecuteStatement(database, null, SqlParser.Parse("INSERT INTO items(id) VALUES(1)"), null, transaction);
        int callbacks = 0;
        var failure = Assert.Throws<RoutineExecutionException>(() => SqlExecutor.ExecuteProcedure(database, "duplicate",
            [new("value", "unchanged", SqlProcedureParameterDirection.Output)], transaction,
            validateCompletion: _ => callbacks++));
        Assert.Equal(TableConstraintException.UniqueViolation, failure.Code);
        Assert.Equal(0, callbacks);
        SqlExecutor.ExecuteStatement(database, null, SqlParser.Parse("COMMIT"), null, transaction);
        Assert.Equal(1L, Select(database, "SELECT id FROM items").Rows.Single().Single());
    }

    [Theory]
    [InlineData("IN target INT", "@target")]
    [InlineData("OUT target STRING", "@target")]
    [InlineData("OUT target INT", "1")]
    public void ExecuteProcedure_InvalidNestedOutput_PreflightsBeforeWrites(string declaration, string target)
    {
        using var database = Open();
        SqlExecutor.Execute(database, "CREATE TABLE items(id INT,PRIMARY KEY(id))");
        SqlExecutor.Execute(database, "CREATE PROCEDURE child(OUT value INT) LANGUAGE SQL AS BEGIN SET @value=1; END");
        SqlExecutor.Execute(database, $"CREATE PROCEDURE parent({declaration}) LANGUAGE SQL AS BEGIN INSERT INTO items(id) VALUES(1); CALL child({target}); END");
        var parameter = database.Routines.TryGetProcedure("parent")!.Parameters.Single();
        Assert.Equal(RoutineErrorCodes.InvalidArguments,
            Assert.Throws<RoutineExecutionException>(() => SqlExecutor.ExecuteProcedure(database, "parent", [new("target", null, parameter.Direction)])).Code);
        Assert.Empty(Select(database, "SELECT * FROM items").Rows);
    }

    [Fact]
    public void ExecuteProcedure_NestedReturnOnly_DoesNotTurnQueryIntoDml()
    {
        using var database = Open();
        SqlExecutor.Execute(database, "CREATE PROCEDURE child() LANGUAGE SQL AS BEGIN RETURN 1; END");
        SqlExecutor.Execute(database, "CREATE PROCEDURE parent() LANGUAGE SQL AS BEGIN CALL child(); SELECT 42 AS result; END");
        Assert.Equal(-1, SqlExecutor.ExecuteProcedure(database, "parent", []).RecordsAffected);
    }

    [Fact]
    public void ExecuteProcedure_RowBudgetAndCancellation_DoNotPublishCompletion()
    {
        using var database = Open();
        CreateExchange(database);
        Assert.Equal(RoutineErrorCodes.ResultRowLimit,
            Assert.Throws<RoutineExecutionException>(() => Exchange(database, new SqlExecutionOptions { MaxRoutineResultRows = 1 })).Code);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal(RoutineErrorCodes.Cancelled,
            Assert.Throws<RoutineExecutionException>(() => Exchange(database, new SqlExecutionOptions { CancellationToken = cancellation.Token })).Code);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("direction")]
    [InlineData("extra")]
    [InlineData("fractional")]
    public void ExecuteProcedure_InvalidNamedParameters_RejectsBeforeExecution(string input)
    {
        using var database = Open();
        CreateExchange(database);
        ProcedureCallArgument[] arguments =
        [new("state", 10L, SqlProcedureParameterDirection.InputOutput), new("echo", null, SqlProcedureParameterDirection.Output), new("p_add", 2L)];
        arguments = input switch
        {
            "missing" => arguments[..2],
            "duplicate" => [arguments[0], arguments[1], new("@STATE", 2L)],
            "direction" => [arguments[0] with { Direction = SqlProcedureParameterDirection.Output }, arguments[1], arguments[2]],
            "extra" => [arguments[0], arguments[1], arguments[2], new("extra", 1L)],
            _ => [arguments[0], arguments[1], arguments[2] with { Value = 1.5d }],
        };
        Assert.Equal(RoutineErrorCodes.InvalidArguments,
            Assert.Throws<RoutineExecutionException>(() => SqlExecutor.ExecuteProcedure(database, "exchange", arguments)).Code);
    }

    [Fact]
    public void Catalog_Reopen_PreservesDirectionsNullAndReturn()
    {
        using (var database = Open()) CreateExchange(database);
        using var reopened = Open();
        Assert.Equal(new[] { SqlProcedureParameterDirection.Input, SqlProcedureParameterDirection.InputOutput, SqlProcedureParameterDirection.Output },
            reopened.Routines.TryGetProcedure("exchange")!.Parameters.Select(parameter => parameter.Direction));
        Assert.Equal(7L, Exchange(reopened).ReturnValue);
        Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(CatalogPath).AsSpan(8)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Catalog_LegacyInputFixture_UpgradesToFiveAndReopens(int version)
    {
        using (var database = Open()) { }
        WriteLegacyFixture(version);
        using (var database = Open())
        {
            var result = SqlExecutor.ExecuteProcedure(database, "legacy", [new("value", 42L)]);
            Assert.Equal(42L, result.Results.Single().Rows.Single().Single());
            SqlExecutor.Execute(database, "CREATE PROCEDURE new_proc() LANGUAGE SQL AS BEGIN RETURN 0; END");
        }
        Assert.Equal(5, BinaryPrimitives.ReadInt32LittleEndian(File.ReadAllBytes(CatalogPath).AsSpan(8)));
        using var reopened = Open();
        Assert.Equal(7L, SqlExecutor.ExecuteProcedure(reopened, "legacy", [new("value", 7L)]).Results.Single().Rows.Single().Single());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Catalog_UnknownVersionOrDirection_RejectsWithoutWriting(bool version)
    {
        using (var database = Open()) CreateExchange(database);
        byte[] corrupt = File.ReadAllBytes(CatalogPath);
        if (version) BinaryPrimitives.WriteInt32LittleEndian(corrupt.AsSpan(8), 6);
        else
        {
            // Header + name length/name + creation ticks + parameter count + first parameter length/name + type.
            int offset = 32 + 4 + Encoding.UTF8.GetByteCount("exchange") + 8 + 4 + 4 + Encoding.UTF8.GetByteCount("p_add") + 1;
            corrupt[offset] = 255;
            BinaryPrimitives.WriteUInt32LittleEndian(corrupt.AsSpan(corrupt.Length - 16), Crc32.HashToUInt32(corrupt.AsSpan(32, corrupt.Length - 48)));
        }
        File.WriteAllBytes(CatalogPath, corrupt);
        Assert.Throws<InvalidDataException>(() => Open());
        Assert.Equal(corrupt, File.ReadAllBytes(CatalogPath));
    }

    private string CatalogPath => Path.Combine(_root, "routines", "routines.sdbrtn");

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root, BackgroundFlush = new BackgroundFlushOptions { Enabled = false } });

    private static SelectExecutionResult Select(Tsdb database, string sql) => Assert.IsType<SelectExecutionResult>(SqlExecutor.Execute(database, sql));

    private static void CreateExchange(Tsdb database) => SqlExecutor.Execute(database, """
        CREATE PROCEDURE exchange(IN p_add INT,INOUT state INT,OUT echo STRING) LANGUAGE SQL AS BEGIN
          SET @state=@state+@p_add; SELECT @state AS first; SELECT @echo AS second;
          SET @echo='done'; RETURN 7;
        END
        """);

    private static ProcedureExecutionResult Exchange(Tsdb database, SqlExecutionOptions? options = null)
        => SqlExecutor.ExecuteProcedure(database, "exchange",
            [new("@ECHO", "ignored", SqlProcedureParameterDirection.Output), new(":state", 10L, SqlProcedureParameterDirection.InputOutput), new("p_add", 2L)], options: options);

    private void WriteLegacyFixture(int version)
    {
        using var payload = new MemoryStream();
        using (var writer = new BinaryWriter(payload, Encoding.UTF8, leaveOpen: true))
        {
            WriteString(writer, "legacy"); writer.Write(DateTime.UtcNow.Ticks); writer.Write(1);
            WriteString(writer, "value"); writer.Write((byte)SqlProcedureParameterType.Int64);
            WriteString(writer, "SELECT @value AS result;");
        }
        byte[] bytes = new byte[32 + payload.Length + 16];
        "SDBRTN01"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), version);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), 32);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 1);
        payload.ToArray().CopyTo(bytes, 32);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(bytes.Length - 16), Crc32.HashToUInt32(bytes.AsSpan(32, bytes.Length - 48)));
        "SDBRTN01"u8.CopyTo(bytes.AsSpan(bytes.Length - 12));
        Directory.CreateDirectory(Path.GetDirectoryName(CatalogPath)!);
        File.WriteAllBytes(CatalogPath, bytes);
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        writer.Write(bytes.Length); writer.Write(bytes);
    }
}
