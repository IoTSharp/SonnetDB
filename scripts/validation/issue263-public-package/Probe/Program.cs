using System.Data;
using System.Data.Common;
using System.Reflection;
using System.Security.Cryptography;
using Chloe;
using Chloe.Annotations;
using Chloe.Infrastructure.Interception;
using Chloe.SonnetDB;
using Chloe.SonnetDB.DDL;
using SonnetDB.Data;

if (args.Length != 3 || args[0] is not ("ado" or "chloe") || args[1] is not ("exercise" or "reopen"))
{
    Console.Error.WriteLine("Usage: Probe ado|chloe exercise|reopen owned-directory");
    return 2;
}
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(150));
try
{
    string directory = Path.GetFullPath(args[2]);
    if (!Directory.Exists(directory) || !File.Exists(Path.Combine(directory, ".owner")))
        throw new InvalidOperationException("Owned runtime directory is required.");
    string connectionString = Environment.GetEnvironmentVariable("ISSUE263_CONNECTION")
        ?? "Data Source=" + Path.Combine(directory, args[0]);
    bool remote = Environment.GetEnvironmentVariable("ISSUE263_CONNECTION") is not null;
    foreach (string name in new[] { "SonnetDB.Core", "SonnetDB.Data", "Chloe", "Chloe.SonnetDB" })
    {
        var assembly = Assembly.Load(name);
        Console.WriteLine($"ASSEMBLY {name} {assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion} SHA256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location)))}");
    }
    if (args[0] == "ado") await Probe.AdoAsync(connectionString, args[1] == "exercise", remote, deadline.Token);
    else await Probe.ChloeAsync(connectionString, args[1] == "exercise", remote, deadline.Token);
    Console.WriteLine($"PASS {args[0]} {args[1]} ASSERTIONS={Probe.Assertions}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}

internal sealed record Input(string Name, object Value, object NullableValue, object Alternate, DbType Declared, long Expected)
{
    internal object Underlying => Value.GetType().IsEnum ? Convert.ChangeType(Value, Enum.GetUnderlyingType(Value.GetType())) : Value;
}

internal static class Probe
{
    internal static int Assertions { get; private set; }
    private static readonly Input[] Inputs =
    [
        new("sbyte_min", sbyte.MinValue, (sbyte?)sbyte.MinValue, sbyte.MaxValue, DbType.SByte, sbyte.MinValue),
        new("sbyte_max", sbyte.MaxValue, (sbyte?)sbyte.MaxValue, sbyte.MinValue, DbType.SByte, sbyte.MaxValue),
        new("ushort_min", ushort.MinValue, (ushort?)ushort.MinValue, ushort.MaxValue, DbType.UInt16, ushort.MinValue),
        new("ushort_max", ushort.MaxValue, (ushort?)ushort.MaxValue, ushort.MinValue, DbType.UInt16, ushort.MaxValue),
        new("uint_min", uint.MinValue, (uint?)uint.MinValue, uint.MaxValue, DbType.UInt32, uint.MinValue),
        new("uint_max", uint.MaxValue, (uint?)uint.MaxValue, uint.MinValue, DbType.UInt32, uint.MaxValue),
        new("enum_sbyte_min", Signed.Min, (Signed?)Signed.Min, Signed.Max, DbType.SByte, sbyte.MinValue),
        new("enum_sbyte_max", Signed.Max, (Signed?)Signed.Max, Signed.Min, DbType.SByte, sbyte.MaxValue),
        new("enum_ushort_min", Unsigned16.Min, (Unsigned16?)Unsigned16.Min, Unsigned16.Max, DbType.UInt16, ushort.MinValue),
        new("enum_ushort_max", Unsigned16.Max, (Unsigned16?)Unsigned16.Max, Unsigned16.Min, DbType.UInt16, ushort.MaxValue),
        new("enum_uint_min", Unsigned32.Min, (Unsigned32?)Unsigned32.Min, Unsigned32.Max, DbType.UInt32, uint.MinValue),
        new("enum_uint_max", Unsigned32.Max, (Unsigned32?)Unsigned32.Max, Unsigned32.Min, DbType.UInt32, uint.MaxValue),
    ];

    internal static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("ASSERTION FAILED: " + label);
        Console.WriteLine($"ASSERT {++Assertions:D4} {label}");
    }

    private static void Preserved(SndbParameter parameter, object? value, DbType type)
        => Require(ReferenceEquals(parameter.Value, value) && parameter.DbType == type, "SndbParameter retains original Value reference and DbType");

    internal static async Task AdoAsync(string connectionString, bool exercise, bool remote, CancellationToken token)
    {
        await using var connection = new SndbConnection(connectionString);
        await connection.OpenAsync(token);
        foreach (var input in Inputs)
        {
            token.ThrowIfCancellationRequested();
            string table = "ado_" + input.Name;
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 15;
            if (exercise)
            {
                command.CommandText = $"CREATE TABLE {table} (id INT, value INT NULL, PRIMARY KEY(id))";
                command.ExecuteNonQuery();
                var parameter = new SndbParameter("@value", input.Value);
                command.Parameters.Add(parameter);
                command.CommandText = $"INSERT INTO {table} (id,value) VALUES(1,@value)";
                Require(command.ExecuteNonQuery() == 1, table + ": raw value insert");
                Preserved(parameter, input.Value, DbType.Object);
                parameter.DbType = input.Declared;
                command.CommandText = $"INSERT INTO {table} (id,value) VALUES(2,@value),(3,@value)";
                Require(await command.ExecuteNonQueryAsync(token) == 2, table + ": explicit DbType multi-row insert");
                Preserved(parameter, input.Value, input.Declared);
                parameter.Value = input.NullableValue;
                command.CommandText = $"INSERT INTO {table} (id,value) VALUES(4,@value)";
                Require(command.ExecuteNonQuery() == 1, table + ": boxed nullable value insert");
                Preserved(parameter, input.NullableValue, input.Declared);
                parameter.Value = null;
                command.CommandText = $"INSERT INTO {table} (id,value) VALUES(5,@value)";
                Require(await command.ExecuteNonQueryAsync(token) == 1, table + ": null insert");
                Preserved(parameter, null, input.Declared);
                parameter.Value = DBNull.Value;
                command.CommandText = $"INSERT INTO {table} (id,value) VALUES(6,@value)";
                Require(command.ExecuteNonQuery() == 1, table + ": DBNull insert");
                Preserved(parameter, DBNull.Value, input.Declared);
                using (var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted))
                {
                    command.Transaction = transaction;
                    parameter.Value = input.Value;
                    command.CommandText = $"INSERT INTO {table} (id,value) VALUES(7,@value)";
                    Require(await command.ExecuteNonQueryAsync(token) == 1, table + ": transaction insert");
                    parameter.Value = input.Alternate;
                    command.CommandText = $"UPDATE {table} SET value=@value WHERE id=1";
                    Require(command.ExecuteNonQuery() == 1, table + ": transaction update");
                    Preserved(parameter, input.Alternate, input.Declared);
                    command.CommandText = $"DELETE FROM {table} WHERE id=3";
                    Require(command.ExecuteNonQuery() == 1, table + ": transaction delete");
                    transaction.Rollback();
                    command.Transaction = null;
                }
                command.Parameters.Clear();
            }
            var queryParameter = new SndbParameter("@value", input.Value) { DbType = input.Declared };
            command.Parameters.Add(queryParameter);
            command.CommandText = $"SELECT id,value FROM {table} WHERE value=@value ORDER BY id";
            await using (var reader = await command.ExecuteReaderAsync(token))
            {
                Require(reader.GetFieldType(1) == typeof(long), table + ": INT metadata is Int64");
                for (int index = 1; index <= 4; index++)
                {
                    Require(await reader.ReadAsync(token), table + ": expected persisted row " + index);
                    Require(reader.GetInt64(0) == index && reader.GetValue(1) is long exact && exact == input.Expected,
                        table + ": exact Int64 result and rollback-restored row " + index);
                }
                Require(!await reader.ReadAsync(token), table + ": no unexpected matching rows");
            }
            Preserved(queryParameter, input.Value, input.Declared);
            command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE value IS NULL";
            Require(command.ExecuteScalar() is long nulls && nulls == 2, table + ": exactly two nulls");
            command.CommandText = $"SELECT COUNT(*) FROM {table}";
            Require(await command.ExecuteScalarAsync(token) is long count && count == 6, table + ": rollback removes seventh row");
            queryParameter.Value = input.Alternate;
            command.CommandText = $"SELECT COUNT(*) FROM {table} WHERE value=@value";
            Require(command.ExecuteScalar() is long absent && absent == 0, table + ": different value does not match");
        }
        if (exercise) await AdoUInt64Async(connection, remote, token);
    }

    private static async Task AdoUInt64Async(SndbConnection connection, bool remote, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT @value";
        object withinSignedRange = (ulong)long.MaxValue;
        var parameter = new SndbParameter("@value", withinSignedRange);
        command.Parameters.Add(parameter);
        if (remote)
            await RejectAsync(() => command.ExecuteScalarAsync(token), remote,
                "Public .5 remote UInt64 within Int64 range remains unsupported (outside #263)");
        else
            Require(await command.ExecuteScalarAsync(token) is long allowed && allowed == long.MaxValue,
                "Embedded UInt64 within Int64 range stays exact");
        Preserved(parameter, withinSignedRange, DbType.Object);
        foreach (object value in new object[] { ulong.MaxValue, Unsigned64.Max })
        {
            foreach (DbType type in new[] { DbType.Object, DbType.UInt64 })
            {
                parameter.Value = value;
                parameter.DbType = type;
                await RejectAsync(() => command.ExecuteScalarAsync(token), remote, "ADO UInt64 boundary");
                Preserved(parameter, value, type);
            }
        }
    }

    internal static async Task ChloeAsync(string connectionString, bool exercise, bool remote, CancellationToken token)
    {
        using var context = new SonnetDBContext(() => new SndbConnection(connectionString));
        context.Session.CommandTimeout = 15;
        var capture = new ParameterCapture();
        context.Session.AddInterceptor(capture);
        foreach (var input in Inputs)
        {
            token.ThrowIfCancellationRequested();
            string table = "chloe_" + input.Name;
            var parameter = new DbParam("@value", input.Value);
            if (exercise)
            {
                context.Session.ExecuteNonQuery($"CREATE TABLE {table} (id INT, value INT NULL, PRIMARY KEY(id))");
                capture.Expect(input.Underlying, input.Declared);
                Require(context.Session.ExecuteNonQuery($"INSERT INTO {table}(id,value) VALUES(1,@value)", parameter) == 1,
                    table + ": Session raw DbParam insert");
                Require(ReferenceEquals(parameter.Value, input.Value) && parameter.DbType is null, "Chloe raw DbParam unchanged after inferred binding");
                parameter.DbType = input.Declared;
                capture.Expect(input.Underlying, input.Declared);
                Require(await context.Session.ExecuteNonQueryAsync($"INSERT INTO {table}(id,value) VALUES(2,@value),(3,@value)", parameter).WaitAsync(token) == 2,
                    table + ": Session explicit DbType batch");
                parameter.Value = input.NullableValue;
                capture.Expect(input.Underlying, input.Declared);
                Require(context.Session.ExecuteNonQuery($"INSERT INTO {table}(id,value) VALUES(4,@value)", parameter) == 1,
                    table + ": nullable value");
                parameter.Value = null!;
                parameter.Type = typeof(Nullable<>).MakeGenericType(input.Underlying.GetType());
                capture.Expect(DBNull.Value, input.Declared);
                Require(await context.Session.ExecuteNonQueryAsync($"INSERT INTO {table}(id,value) VALUES(5,@value)", parameter).WaitAsync(token) == 1,
                    table + ": typed nullable NULL");
                Require(parameter.Value is null && parameter.DbType == input.Declared, "Chloe original null and DbType preserved");
                parameter.Value = DBNull.Value;
                capture.Expect(DBNull.Value, input.Declared);
                Require(context.Session.ExecuteNonQuery($"INSERT INTO {table}(id,value) VALUES(6,@value)", parameter) == 1,
                    table + ": DBNull");
                context.Session.BeginTransaction(IsolationLevel.ReadCommitted);
                try
                {
                    parameter.Value = input.Value;
                    capture.Expect(input.Underlying, input.Declared);
                    Require(await context.Session.ExecuteNonQueryAsync($"INSERT INTO {table}(id,value) VALUES(7,@value)", parameter).WaitAsync(token) == 1,
                        table + ": transaction insert");
                    parameter.Value = input.Alternate;
                    capture.Expect(Underlying(input.Alternate), input.Declared);
                    Require(context.Session.ExecuteNonQuery($"UPDATE {table} SET value=@value WHERE id=1", parameter) == 1,
                        table + ": transaction update");
                    Require(context.Session.ExecuteNonQuery($"DELETE FROM {table} WHERE id=3") == 1, table + ": transaction delete");
                }
                finally { context.Session.RollbackTransaction(); }
            }
            parameter.Value = input.Value;
            parameter.DbType = input.Declared;
            capture.Expect(input.Underlying, input.Declared);
            var rows = await context.SqlQueryAsync<long>($"SELECT value FROM {table} WHERE value=@value ORDER BY id", parameter).WaitAsync(token);
            Require(rows.Count == 4 && rows.All(value => value == input.Expected), table + ": SqlQueryAsync exact values and rollback");
            Require(ReferenceEquals(parameter.Value, input.Value) && parameter.DbType == input.Declared,
                "Chloe original value/DbType preserved after SqlQuery");
            capture.Expect(input.Underlying, input.Declared);
            using (var reader = context.Session.ExecuteReader($"SELECT value FROM {table} WHERE value=@value ORDER BY id", parameter))
            {
                Require(reader.GetFieldType(0) == typeof(long), table + ": Session INT reader metadata");
                int count = 0;
                while (reader.Read()) { Require(reader.GetValue(0) is long value && value == input.Expected, table + ": Session raw Int64 value"); count++; }
                Require(count == 4, table + ": Session exact matching count");
            }
            Require(context.SqlQuery<long>($"SELECT COUNT(*) FROM {table}").Single() == 6, table + ": exactly six persisted rows");
            Require(context.SqlQuery<long>($"SELECT COUNT(*) FROM {table} WHERE value IS NULL").Single() == 2, table + ": two persisted nulls");
            parameter.Value = input.Alternate;
            capture.Expect(Underlying(input.Alternate), input.Declared);
            Require(context.SqlQuery<long>($"SELECT COUNT(*) FROM {table} WHERE value=@value", parameter).Single() == 0,
                table + ": different raw parameter does not match");
            // ExplicitParameter keeps actual boxed enums all the way to SndbParameter; ordinary DbParam uses Chloe's underlying-type conversion.
            var explicitParameter = new SndbParameter("@value", input.Value) { DbType = input.Declared };
            capture.Expect(input.Value, input.Declared);
            Require(context.Session.ExecuteScalar($"SELECT COUNT(*) FROM {table} WHERE value=@value",
                new DbParam { ExplicitParameter = explicitParameter }) is long explicitCount && explicitCount == 4,
                table + ": Chloe ExplicitParameter reaches the driver unchanged");
            Preserved(explicitParameter, input.Value, input.Declared);
        }
        await OrmControlAsync(context, capture, exercise, token);
        if (exercise)
        {
            object withinSignedRange = (ulong)long.MaxValue;
            var rangeParameter = new DbParam("@value", withinSignedRange) { DbType = DbType.UInt64 };
            capture.Expect(withinSignedRange, DbType.UInt64);
            if (remote)
                await RejectAsync(() => context.Session.ExecuteScalarAsync("SELECT @value", rangeParameter), remote,
                    "Public .5 Chloe raw UInt64 within Int64 range remains unsupported (outside #263)");
            else
                Require(await context.Session.ExecuteScalarAsync("SELECT @value", rangeParameter).WaitAsync(token) is long allowed && allowed == long.MaxValue,
                    "Embedded Chloe raw UInt64 within signed range stays exact");
            Require(ReferenceEquals(rangeParameter.Value, withinSignedRange) && rangeParameter.DbType == DbType.UInt64,
                "Chloe UInt64 range control input remains unchanged");
            foreach (object value in new object[] { ulong.MaxValue, Unsigned64.Max })
            {
                var parameter = new DbParam("@value", value) { DbType = DbType.UInt64 };
                capture.Expect(Underlying(value), DbType.UInt64);
                await RejectAsync(() => context.Session.ExecuteScalarAsync("SELECT @value", parameter), remote, "Chloe raw UInt64 boundary");
                Require(ReferenceEquals(parameter.Value, value) && parameter.DbType == DbType.UInt64, "Chloe rejected UInt64 input remains unchanged");
            }
        }
    }

    private static async Task OrmControlAsync(SonnetDBContext context, ParameterCapture capture, bool exercise, CancellationToken token)
    {
        if (exercise)
        {
            new SonnetDBTableGenerator(context).CreateTable(typeof(OrmControl), null, Chloe.RDBMS.DDL.TableCreateMode.CreateNew);
            context.Insert(new OrmControl { Id = 1, Optional = uint.MaxValue });
            context.InsertRange(new List<OrmControl> { new() { Id = 2, Optional = null }, new() { Id = 3, Optional = 0 } }, 1, null);
        }
        capture.Observed.Clear();
        sbyte signed = sbyte.MinValue;
        ushort unsigned16 = ushort.MaxValue;
        uint unsigned32 = uint.MaxValue;
        Require(await context.Query<OrmControl>().Where(row => row.Signed == signed && row.Unsigned16 == unsigned16 && row.Unsigned32 == unsigned32)
            .CountAsync().WaitAsync(token) == 3, "ORM control parameter predicate finds three rows");
        foreach (long expected in new long[] { signed, unsigned16, unsigned32 })
            Require(capture.Observed.Any(parameter => parameter.Type == DbType.Int64 && parameter.Value is long number && number == expected),
                "ORM control widens to Int64, recorded separately from raw parameters");
        var rows = context.Query<OrmControl>().OrderBy(row => row.Id).ToList();
        Require(rows.Select(row => row.Optional).SequenceEqual(new uint?[] { uint.MaxValue, null, 0 }), "ORM control nullable values and reopen");
    }

    private static object Underlying(object value) => value.GetType().IsEnum ? Convert.ChangeType(value, Enum.GetUnderlyingType(value.GetType())) : value;

    private static async Task RejectAsync(Func<Task<object?>> operation, bool remote, string label)
    {
        Exception? failure = null;
        try { await operation(); } catch (Exception exception) { failure = exception; }
        Type expected = remote ? typeof(NotSupportedException) : typeof(OverflowException);
        var chain = new List<string>();
        bool found = false;
        for (var current = failure; current is not null; current = current.InnerException)
        {
            chain.Add(current.GetType().FullName!);
            found |= current.GetType() == expected;
        }
        Require(found, label + ": preserves " + expected.Name);
        Console.WriteLine("EXPECTED_REJECTION " + string.Join(" -> ", chain));
    }

    private enum Signed : sbyte { Min = sbyte.MinValue, Max = sbyte.MaxValue }
    private enum Unsigned16 : ushort { Min = ushort.MinValue, Max = ushort.MaxValue }
    private enum Unsigned32 : uint { Min = uint.MinValue, Max = uint.MaxValue }
    private enum Unsigned64 : ulong { Max = ulong.MaxValue }
}

internal sealed class ParameterCapture : IDbCommandInterceptor
{
    internal List<(DbType Type, object? Value)> Observed { get; } = [];
    private (object Value, DbType Type)? _expected;
    internal void Expect(object value, DbType type) => _expected = (value, type);

    private void Before<T>(IDbCommand command, DbCommandInterceptionContext<T> context)
    {
        var parameters = command.Parameters.Cast<IDbDataParameter>().ToArray();
        if (_expected is { } expected)
        {
            var parameter = parameters.Single(item => item.ParameterName == "@value");
            Probe.Require(parameter is SndbParameter && parameter.Value?.GetType() == expected.Value.GetType()
                && Equals(parameter.Value, expected.Value) && parameter.DbType == expected.Type,
                $"Chloe actual driver input {expected.Value.GetType().Name}/{expected.Type} without Int64/double coercion");
            _expected = null;
        }
        context.DataBag["parameters"] = parameters.Select(parameter => (parameter, parameter.Value, parameter.DbType)).ToArray();
        foreach (var parameter in parameters)
        {
            Observed.Add((parameter.DbType, parameter.Value));
            Console.WriteLine($"PARAMETER {parameter.ParameterName} CLR={parameter.Value?.GetType().FullName ?? "null"} DBTYPE={parameter.DbType} VALUE={parameter.Value}");
        }
        Console.WriteLine("SQL " + command.CommandText.Replace('\n', ' ').Replace('\r', ' '));
    }

    private static void After<T>(DbCommandInterceptionContext<T> context)
    {
        var parameters = ((IDbDataParameter Parameter, object? Value, DbType Type)[])context.DataBag["parameters"];
        Probe.Require(parameters.All(item => ReferenceEquals(item.Parameter.Value, item.Value) && item.Parameter.DbType == item.Type),
            "Chloe command completion preserves every original driver parameter");
    }

    public void ReaderExecuting(IDbCommand command, DbCommandInterceptionContext<IDataReader> context) => Before(command, context);
    public void ReaderExecuted(IDbCommand command, DbCommandInterceptionContext<IDataReader> context) => After(context);
    public void NonQueryExecuting(IDbCommand command, DbCommandInterceptionContext<int> context) => Before(command, context);
    public void NonQueryExecuted(IDbCommand command, DbCommandInterceptionContext<int> context) => After(context);
    public void ScalarExecuting(IDbCommand command, DbCommandInterceptionContext<object> context) => Before(command, context);
    public void ScalarExecuted(IDbCommand command, DbCommandInterceptionContext<object> context) => After(context);
}

/// <summary>单独记录 Chloe 既有整数扩大适配的实体对照。</summary>
[Table("orm_control")]
public sealed class OrmControl
{
    /// <summary>固定主键。</summary>
    [Column(IsPrimaryKey = true), NonAutoIncrement]
    public long Id { get; set; }
    /// <summary>有符号小整数。</summary>
    public sbyte Signed { get; set; } = sbyte.MinValue;
    /// <summary>无符号十六位整数。</summary>
    public ushort Unsigned16 { get; set; } = ushort.MaxValue;
    /// <summary>无符号三十二位整数。</summary>
    public uint Unsigned32 { get; set; } = uint.MaxValue;
    /// <summary>可空整数。</summary>
    public uint? Optional { get; set; }
}
