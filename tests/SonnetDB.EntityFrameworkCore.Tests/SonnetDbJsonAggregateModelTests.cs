using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using SonnetDB.Configuration;
using SonnetDB.EntityFrameworkCore.Extensions;
using Xunit;
using Xunit.Abstractions;

namespace SonnetDB.EntityFrameworkCore.Tests;

/// <summary>
/// 验证 ToJson 整容器、标量查询和直接标量更新及其拒绝边界。
/// </summary>
public sealed partial class SonnetDbJsonAggregateModelTests
{
    private readonly ITestOutputHelper _output;
    private const string DisplayNameJson = "display'name.with dot 😃";
    private const string ChildLabelJson = "嵌套'key.with space";

    /// <summary>创建 JSON 模型与真实数据库合同测试。</summary>
    public SonnetDbJsonAggregateModelTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToJson_WithoutExplicitColumnType_CreatesDesignAndRuntimeJsonContainer(bool owned)
    {
        using var context = CreateContext(owned);

        AssertJsonContainer(context.GetService<IDesignTimeModel>().Model);
        AssertJsonContainer(context.Model);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToJson_InitialMigration_GeneratesNativeJsonColumn(bool owned)
    {
        using var context = CreateContext(owned);
        var model = context.GetService<IDesignTimeModel>().Model;
        var operations = context.GetService<IMigrationsModelDiffer>()
            .GetDifferences(null, model.GetRelationalModel());
        var table = Assert.Single(operations.OfType<CreateTableOperation>());
        var container = Assert.Single(table.Columns, column => column.Name == "Data");

        Assert.Equal("JSON", container.ColumnType);
        Assert.DoesNotContain(table.Columns, column => column.Name == "DisplayName");
        var migrationSql = string.Join("\n", context.GetService<IMigrationsSqlGenerator>()
            .Generate(operations, model).Select(command => command.CommandText));
        Assert.Contains("\"Data\" JSON", migrationSql);
        Assert.Contains("\"Data\" JSON", context.Database.GenerateCreateScript());
    }

    [Fact]
    public void TypeMapping_OrdinaryStringAndExplicitRawJson_PreservesExistingScalarBehavior()
    {
        using var context = new ScalarContext(CreateOptions<ScalarContext>());
        var mappings = context.GetService<IRelationalTypeMappingSource>();

        Assert.IsType<StringTypeMapping>(mappings.FindMapping(typeof(string)));
        Assert.IsType<StringTypeMapping>(mappings.FindMapping("TEXT"));
        Assert.IsType<StringTypeMapping>(mappings.FindMapping("JSON"));
        Assert.IsAssignableFrom<JsonTypeMapping>(mappings.FindMapping(typeof(JsonTypePlaceholder)));
        var querySql = context.Set<ScalarRecord>().Select(record => new { record.Name, record.RawJson }).ToQueryString();
        Assert.Contains("\"Name\"", querySql);
        Assert.Contains("\"RawJson\"", querySql);
        Assert.Contains("\"RawJson\" STRING", context.Database.GenerateCreateScript());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ToJson_FixedStringPaths_GenerateNativeLiteralPathWithoutOpeningDatabase(bool owned)
    {
        var blocker = new ConnectionBlocker();
        using var context = CreateContext(owned, blocker);

        Assert.Contains("\"Data\"", context.Set<Passkey>().ToQueryString());
        Assert.Contains("json_value(", context.Set<Passkey>().Select(passkey => passkey.Data.DisplayName).ToQueryString());
        Assert.Contains("json_value(", context.Set<Passkey>()
            .Where(passkey => passkey.Data.DisplayName == "changed").Select(passkey => passkey.Id).ToQueryString());
        Assert.Equal(0, blocker.OpenAttempts);
    }

    /// <summary>在三种真实连接模式验证完整聚合及整根替换，不把局部 Owned 更新伪装成整根更新。</summary>
    [Theory]
    [InlineData("embedded", false)]
    [InlineData("embedded", true)]
    [InlineData("rest", false)]
    [InlineData("rest", true)]
    [InlineData("h2c", false)]
    [InlineData("h2c", true)]
    public async Task ToJson_WholeContainer_ActualTransportPreservesData(string profile, bool owned)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var cancellationToken = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(cancellationToken);
        var capture = new JsonCommandCapture();
        var expected = InitialAggregate();

        await using (var context = fixture.CreateContext(owned, capture))
        {
            await context.Database.EnsureCreatedAsync(cancellationToken);
            context.Add(new AggregateRow { Id = 1, Stamp = "stamp-1", Data = expected });
            Assert.Equal(1, await context.SaveChangesAsync(cancellationToken));
            AssertCapturedRoot(capture.Commands.Last(command => command.Sql.StartsWith("INSERT", StringComparison.Ordinal)), expected);
            context.ChangeTracker.Clear();
            var tracked = await context.Set<AggregateRow>().SingleAsync(row => row.Id == 1, cancellationToken);
            AssertAggregate(expected, tracked.Data);
            AssertAggregate(expected, (await context.Set<AggregateRow>().AsNoTracking()
                .SingleAsync(row => row.Id == 1, cancellationToken)).Data);
            await AssertActualScalarsAsync(context, expected, capture, cancellationToken);

            tracked.Data.DisplayName = "整根 O'Reilly \"\\ 中文😃";
            tracked.Data.Tags = ["新值", "quote'\\\"", "😃"];
            tracked.Stamp = "stamp-2";
            MarkWholeAggregate(context, tracked, owned);
            Assert.Equal(1, await context.SaveChangesAsync(cancellationToken));
            expected = tracked.Data;
            AssertCapturedRoot(capture.Commands.Last(command => command.Sql.StartsWith("UPDATE", StringComparison.Ordinal)), expected);
            Assert.Equal(7, expected.Counter);
            Assert.Equal("nested\"\\中文", expected.Child.Label);
            Assert.Null(expected.Optional);
        }

        if (owned)
        {
            await using var partial = fixture.CreateContext(owned, capture);
            var tracked = await partial.Set<AggregateRow>().SingleAsync(row => row.Id == 1, cancellationToken);
            tracked.Data.DisplayName = "owned single leaf 中文";
            Assert.Equal(1, await partial.SaveChangesAsync(cancellationToken));
            expected = tracked.Data;
            AssertScalarCommand(capture.Commands.Last(command => command.Sql.StartsWith("UPDATE", StringComparison.Ordinal)), DisplayNameJson, expected.DisplayName);
        }
        else
        {
            await using var singleLeaf = fixture.CreateContext(owned, capture);
            var tracked = await singleLeaf.Set<AggregateRow>().SingleAsync(row => row.Id == 1, cancellationToken);
            tracked.Data.DisplayName = "complex single leaf 中文";
            Assert.True(singleLeaf.Entry(tracked).ComplexProperty(row => row.Data).IsModified);
            Assert.Equal(1, await singleLeaf.SaveChangesAsync(cancellationToken));
            expected = tracked.Data;
            AssertCapturedRoot(capture.Commands.Last(command => command.Sql.StartsWith("UPDATE", StringComparison.Ordinal)), expected);
        }

        await using (var context = fixture.CreateContext(owned, capture))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var tracked = await context.Set<AggregateRow>().SingleAsync(row => row.Id == 1, cancellationToken);
            tracked.Data.DisplayName = "must roll back";
            tracked.Stamp = "rollback";
            MarkWholeAggregate(context, tracked, owned);
            Assert.Equal(1, await context.SaveChangesAsync(cancellationToken));
            await transaction.RollbackAsync(cancellationToken);
        }

        await using (var winner = fixture.CreateContext(owned, capture))
        await using (var stale = fixture.CreateContext(owned, capture))
        {
            var winnerRow = await winner.Set<AggregateRow>().SingleAsync(row => row.Id == 1, cancellationToken);
            var staleRow = await stale.Set<AggregateRow>().SingleAsync(row => row.Id == 1, cancellationToken);
            AssertAggregate(expected, winnerRow.Data);
            winnerRow.Data.DisplayName = "concurrency winner 中文";
            winnerRow.Stamp = "stamp-3";
            MarkWholeAggregate(winner, winnerRow, owned);
            Assert.Equal(1, await winner.SaveChangesAsync(cancellationToken));
            expected = winnerRow.Data;
            staleRow.Data.Counter = 999;
            staleRow.Stamp = "stale";
            MarkWholeAggregate(stale, staleRow, owned);
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync(cancellationToken));
            Assert.Equal(7, expected.Counter);
        }

        await using (var context = fixture.CreateContext(owned, capture))
        {
            var literalData = InitialAggregate();
            literalData.DisplayName = "literal O'Reilly \"\\ 中文😃";
            var json = AggregateJson(literalData);
            var mapping = context.GetService<IRelationalTypeMappingSource>().FindMapping(typeof(JsonTypePlaceholder))!;
            var literal = mapping.GenerateSqlLiteral(json);
            await context.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                await using var literalCommand = context.Database.GetDbConnection().CreateCommand();
                literalCommand.CommandText = "INSERT INTO \"AggregateRows\" (\"Id\", \"Stamp\", \"Data\") VALUES (5, 'literal', " + literal + ")";
                literalCommand.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
                _output.WriteLine("ACTUAL LITERAL SQL: " + literalCommand.CommandText);
                Assert.Equal(1, await literalCommand.ExecuteNonQueryAsync(cancellationToken));
            }
            finally
            {
                await context.Database.CloseConnectionAsync();
            }
            var literalRow = await context.Set<AggregateRow>().SingleAsync(row => row.Id == 5, cancellationToken);
            AssertAggregate(literalData, literalRow.Data);
            context.Remove(literalRow);
            Assert.Equal(1, await context.SaveChangesAsync(cancellationToken));
            Assert.False(await context.Set<AggregateRow>().AnyAsync(row => row.Id == 5, cancellationToken));

            if (owned)
            {
                await context.Database.ExecuteSqlRawAsync(
                    "INSERT INTO \"AggregateRows\" (\"Id\", \"Stamp\", \"Data\") VALUES (2, 'sql-null', NULL), (3, 'json-null', 'null')", cancellationToken);
                Assert.Null(await ReadRawAsync(context, 2, cancellationToken));
                Assert.Equal("null", await ReadRawAsync(context, 3, cancellationToken));
                Assert.Null((await context.Set<AggregateRow>().AsNoTracking()
                    .SingleAsync(row => row.Id == 2, cancellationToken)).Data);
                Assert.Null((await context.Set<AggregateRow>().AsNoTracking()
                    .SingleAsync(row => row.Id == 3, cancellationToken)).Data);
            }

            var missingJson = AggregateJson(InitialAggregate(), omitOptional: true);
            await context.Database.OpenConnectionAsync(cancellationToken);
            try
            {
                await using var missingCommand = context.Database.GetDbConnection().CreateCommand();
                missingCommand.CommandText = "INSERT INTO \"AggregateRows\" (\"Id\", \"Stamp\", \"Data\") VALUES (6, 'missing', "
                    + mapping.GenerateSqlLiteral(missingJson) + ")";
                missingCommand.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
                Assert.Equal(1, await missingCommand.ExecuteNonQueryAsync(cancellationToken));
            }
            finally
            {
                await context.Database.CloseConnectionAsync();
            }
            Assert.Null(await context.Set<AggregateRow>().Where(row => row.Id == 6)
                .Select(row => row.Data.Optional).SingleAsync(cancellationToken));
            await AssertExistsRawAsync(context, owned, cancellationToken);
            var executedBefore = capture.Commands.Count;
            Assert.ThrowsAny<Exception>(() => context.Set<AggregateRow>().Select(row => row.Data.Child).ToQueryString());
            Assert.ThrowsAny<Exception>(() => context.Set<AggregateRow>().Select(row => row.Data.Tags).ToQueryString());
            Assert.ThrowsAny<Exception>(() => context.Set<AggregateRow>().Select(row => row.Data.Tags[0]).ToQueryString());
            var dynamicIndex = 1;
            Assert.ThrowsAny<Exception>(() => context.Set<AggregateRow>().Select(row => row.Data.Tags[dynamicIndex]).ToQueryString());
            AssertUnsupported(() => context.Set<AggregateRow>().Select(row => row.Data.Identifier).ToQueryString());
            AssertUnsupported(() => context.Set<AggregateRow>().Select(row => row.Data.OccurredAt).ToQueryString());
            AssertUnsupported(() => context.Set<AggregateRow>().Select(row => row.Data.Bytes).ToQueryString());
            AssertUnsupported(() => context.Set<AggregateRow>().Select(row => row.Data.Converted).ToQueryString());
            Assert.Equal(executedBefore, capture.Commands.Count);
        }

        Assert.Equal(0, fixture.ContextReferences);
        await fixture.ReopenAsync(cancellationToken);
        await using (var reopened = fixture.CreateContext(owned, capture))
        {
            AssertAggregate(expected, (await reopened.Set<AggregateRow>().AsNoTracking()
                .SingleAsync(row => row.Id == 1, cancellationToken)).Data);
            using var raw = JsonDocument.Parse((await ReadRawAsync(reopened, 1, cancellationToken))!);
            Assert.Equal(7, raw.RootElement.GetProperty("Counter").GetInt32());
            Assert.Equal(expected.Tags, raw.RootElement.GetProperty("Tags").EnumerateArray().Select(element => element.GetString()!).ToArray());
        }

        fixture.AssertTransport();
        foreach (var command in capture.Commands.Where(command => command.Sql.StartsWith("INSERT", StringComparison.Ordinal)
                     || command.Sql.StartsWith("UPDATE", StringComparison.Ordinal)).Take(16))
        {
            _output.WriteLine("ACTUAL SQL: " + command.Sql);
            foreach (var parameter in command.Parameters.Take(12))
                _output.WriteLine($"ACTUAL PARAM: {parameter.Name}; DbType={parameter.DbType}; Value={parameter.Value}");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExecuteUpdate_JsonPath_RejectsBeforeOpeningDatabase(bool owned)
    {
        var blocker = new ConnectionBlocker();
        using var context = CreateContext(owned, blocker);

        var exception = Assert.ThrowsAny<Exception>(() => context.Set<Passkey>().ExecuteUpdate(setters =>
            setters.SetProperty(passkey => passkey.Data.DisplayName, "changed")));
        if (exception is NotSupportedException)
        {
            Assert.Contains("This JSON operation is not supported", exception.Message);
        }
        else
        {
            Assert.IsType<InvalidOperationException>(exception);
            if (owned)
            {
                Assert.Contains("could not be translated", exception.Message);
            }
            else
            {
                Assert.Equal(RelationalStrings.JsonPartialExecuteUpdateNotSupportedByProvider, exception.Message);
            }
        }

        Assert.Equal(0, blocker.OpenAttempts);
    }

    private static void AssertJsonContainer(IModel model)
    {
        var table = Assert.Single(model.GetRelationalModel().Tables);
        var container = Assert.Single(table.Columns, column => column.Name == "Data");
        Assert.Equal("JSON", container.StoreType);
        Assert.IsAssignableFrom<JsonTypeMapping>(container.StoreTypeMapping);
        Assert.DoesNotContain(table.Columns, column => column.Name == "DisplayName");
    }

    private static void AssertUnsupported(Action action)
    {
        var exception = Assert.Throws<NotSupportedException>(action);
        Assert.Contains("This JSON operation is not supported", exception.Message);
    }

    private static DbContext CreateContext(bool owned, ConnectionBlocker? blocker = null)
        => owned
            ? new OwnedContext(CreateOptions<OwnedContext>(blocker))
            : new ComplexContext(CreateOptions<ComplexContext>(blocker));

    private static DbContextOptions<T> CreateOptions<T>(ConnectionBlocker? blocker = null) where T : DbContext
    {
        var builder = new DbContextOptionsBuilder<T>()
            .UseSonnetDB("Host=127.0.0.1;Port=1;Database=issue266-model-only;Protocol=rest;Timeout=1");
        if (blocker is not null)
        {
            builder.AddInterceptors(blocker);
        }

        return builder.Options;
    }

    private static AggregateData InitialAggregate() => new()
    {
        DisplayName = "中文😃 O'Reilly \"\\",
        Counter = 7,
        Optional = null,
        Tags = ["first", "中文", "escape\\\""],
        Child = new AggregateChild { Label = "nested\"\\中文" },
        Enabled = true,
        ExactLong = long.MaxValue - 123,
        Metric = 1.25d,
        Single = 1.5f,
        NullableCounter = null,
        Identifier = Guid.Parse("142408e3-4c82-43a2-a7f5-2e82d4677769"),
        OccurredAt = new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc),
        Bytes = [1, 2, 3],
        Converted = "mixed",
    };

    private static void AssertAggregate(AggregateData expected, AggregateData actual)
    {
        Assert.Equal(expected.DisplayName, actual.DisplayName);
        Assert.Equal(expected.Counter, actual.Counter);
        Assert.Equal(expected.Optional, actual.Optional);
        Assert.Equal(expected.Tags, actual.Tags);
        Assert.Equal(expected.Child.Label, actual.Child.Label);
        Assert.Equal(expected.Enabled, actual.Enabled);
        Assert.Equal(expected.ExactLong, actual.ExactLong);
        Assert.Equal(expected.Metric, actual.Metric);
        Assert.Equal(expected.Single, actual.Single);
        Assert.Equal(expected.NullableCounter, actual.NullableCounter);
        Assert.Equal(expected.Identifier, actual.Identifier);
        Assert.Equal(expected.OccurredAt, actual.OccurredAt);
        Assert.Equal(expected.Bytes, actual.Bytes);
        Assert.Equal(expected.Converted, actual.Converted);
    }

    private static void MarkWholeAggregate(DbContext context, AggregateRow row, bool owned)
    {
        if (owned)
        {
            var entry = context.Entry(row).Reference(item => item.Data).TargetEntry!;
            entry.State = EntityState.Modified;
            Assert.All(entry.Properties.Where(property => !property.Metadata.IsPrimaryKey()), property => Assert.True(property.IsModified));
        }
        else
        {
            var entry = context.Entry(row).ComplexProperty(item => item.Data);
            entry.IsModified = true;
            Assert.True(entry.IsModified);
        }
    }

    private static void AssertCapturedRoot(CapturedCommand command, AggregateData expected)
    {
        var parameter = Assert.Single(command.Parameters, parameter => parameter.Value is string value
            && value.StartsWith('{'));
        Assert.Equal(DbType.String, parameter.DbType);
        using var document = JsonDocument.Parse((string)parameter.Value!);
        var root = document.RootElement;
        Assert.Equal(expected.DisplayName, root.GetProperty(DisplayNameJson).GetString());
        Assert.Equal(expected.Counter, root.GetProperty("Counter").GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("Optional").ValueKind);
        Assert.Equal(expected.Tags, root.GetProperty("Tags").EnumerateArray().Select(element => element.GetString()!).ToArray());
        Assert.Equal(expected.Child.Label, root.GetProperty("Child").GetProperty(ChildLabelJson).GetString());
        Assert.Equal(expected.ExactLong, root.GetProperty("ExactLong").GetInt64());
        Assert.DoesNotContain("json_set", command.Sql, StringComparison.OrdinalIgnoreCase);
    }

    private static string AggregateJson(AggregateData value, bool omitOptional = false)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString(DisplayNameJson, value.DisplayName);
            writer.WriteNumber("Counter", value.Counter);
            writer.WriteNumber("Small", value.Small);
            writer.WriteNumber("Short", value.Short);
            if (!omitOptional)
                writer.WriteNull("Optional");
            writer.WriteBoolean("Enabled", value.Enabled);
            writer.WriteNumber("ExactLong", value.ExactLong);
            writer.WriteNumber("Metric", value.Metric);
            writer.WriteNumber("Single", value.Single);
            writer.WriteNull("NullableCounter");
            writer.WriteString("Identifier", value.Identifier);
            writer.WriteString("OccurredAt", value.OccurredAt);
            writer.WriteBase64String("Bytes", value.Bytes);
            writer.WriteString("Converted", value.Converted.ToUpperInvariant());
            writer.WriteStartArray("Tags");
            foreach (var tag in value.Tags.Take(8))
                writer.WriteStringValue(tag);
            writer.WriteEndArray();
            writer.WriteStartObject("Child");
            writer.WriteString(ChildLabelJson, value.Child.Label);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static async Task AssertActualScalarsAsync(DbContext context, AggregateData expected, JsonCommandCapture capture, CancellationToken cancellationToken)
    {
        var query = context.Set<AggregateRow>().Where(row => row.Id == 1).Select(row => new
        {
            row.Data.DisplayName,
            row.Data.Counter,
            row.Data.ExactLong,
            row.Data.Enabled,
            row.Data.Metric,
            row.Data.Single,
            row.Data.NullableCounter,
            row.Data.Optional,
            Nested = row.Data.Child.Label,
        });
        var projected = await query.SingleAsync(cancellationToken);
        Assert.Equal(expected.DisplayName, projected.DisplayName);
        Assert.Equal(expected.Counter, projected.Counter);
        Assert.Equal(expected.ExactLong, projected.ExactLong);
        Assert.Equal(expected.Enabled, projected.Enabled);
        Assert.Equal(expected.Metric, projected.Metric);
        Assert.Equal(expected.Single, projected.Single);
        Assert.Null(projected.NullableCounter);
        Assert.Null(projected.Optional);
        Assert.Equal(expected.Child.Label, projected.Nested);
        var command = capture.Commands.Last();
        Assert.Contains("json_value(", command.Sql);
        Assert.Contains("display''''name.with dot 😃", command.Sql);
        Assert.Contains("嵌套''''key.with space", command.Sql);
        var name = expected.DisplayName;
        var exact = expected.ExactLong;
        Assert.Equal(1L, await context.Set<AggregateRow>()
            .Where(row => row.Data.DisplayName == name && row.Data.Enabled && row.Data.ExactLong == exact)
            .Select(row => row.Id).SingleAsync(cancellationToken));
        var whereCommand = capture.Commands.Last();
        Assert.Contains("json_value(", whereCommand.Sql);
        Assert.Contains(whereCommand.Parameters, parameter => parameter.DbType == DbType.String && Equals(parameter.Value, name));
        Assert.Contains(whereCommand.Parameters, parameter => parameter.DbType == DbType.Int64 && Equals(parameter.Value, exact));
        var adjacent = exact - 1;
        Assert.False(await context.Set<AggregateRow>().AnyAsync(row => row.Data.ExactLong == adjacent, cancellationToken));
    }

    private async Task AssertExistsRawAsync(DbContext context, bool owned, CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT \"Id\", json_value(\"Data\", '$.Optional'), json_exists(\"Data\", '$.Optional') FROM \"AggregateRows\" WHERE \"Id\" IN (1, 6)";
            command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            _output.WriteLine("ACTUAL JSON NULL/MISSING SQL: " + command.CommandText);
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                var count = 0;
                var timer = Stopwatch.StartNew();
                while (count < 2 && timer.Elapsed < TimeSpan.FromSeconds(5) && await reader.ReadAsync(cancellationToken))
                {
                    Assert.True(reader.IsDBNull(1));
                    var id = reader.GetInt64(0);
                    Assert.Equal(id == 1, reader.GetBoolean(2));
                    _output.WriteLine($"ACTUAL JSON NULL/MISSING id={id}; value=SQLNULL; exists={reader.GetBoolean(2)}");
                    count++;
                }
                Assert.Equal(2, count);
            }
            if (owned)
            {
                command.CommandText = "SELECT json_value(\"Data\", '$.Optional'), json_exists(\"Data\", '$.Optional') FROM \"AggregateRows\" WHERE \"Id\"=2";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                Assert.True(await reader.ReadAsync(cancellationToken));
                Assert.True(reader.IsDBNull(0));
                Assert.True(reader.IsDBNull(1));
                _output.WriteLine("ACTUAL SQLNULL AGGREGATE: value=SQLNULL; exists=SQLNULL");
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private static async Task<string?> ReadRawAsync(DbContext context, long id, CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT \"Data\" FROM \"AggregateRows\" WHERE \"Id\"=" + id.ToString(CultureInfo.InvariantCulture);
            command.Transaction = context.Database.CurrentTransaction?.GetDbTransaction();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            Assert.True(await reader.ReadAsync(cancellationToken));
            return reader.IsDBNull(0) ? null : reader.GetString(0);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    private sealed record CapturedParameter(string Name, DbType DbType, object? Value);

    private sealed record CapturedCommand(string Sql, IReadOnlyList<CapturedParameter> Parameters);

    private sealed class JsonCommandCapture : DbCommandInterceptor
    {
        public List<CapturedCommand> Commands { get; } = [];
        public List<(CapturedCommand Command, int Affected)> CompletedUpdates { get; } = [];
        public Action? AfterUpdate { get; set; }

        private void CompleteUpdate(DbCommand command, DbDataReader reader)
        {
            if (!command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal)
                || !command.CommandText.Contains("json_set_value(", StringComparison.Ordinal))
                return;
            CompletedUpdates.Add((Commands.Last(), reader.RecordsAffected));
            AfterUpdate?.Invoke();
        }

        public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
        {
            CompleteUpdate(command, result);
            return result;
        }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            CompleteUpdate(command, result);
            return ValueTask.FromResult(result);
        }

        private void Capture(DbCommand command)
        {
            if (Commands.Count >= 128 || command.Parameters.Count > 16)
                throw new InvalidOperationException("JSON fixture command/parameter budget exceeded.");
            Commands.Add(new CapturedCommand(command.CommandText, command.Parameters.Cast<DbParameter>()
                .Select(parameter => new CapturedParameter(parameter.ParameterName, parameter.DbType, parameter.Value)).ToArray()));
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Capture(command);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Capture(command);
            return ValueTask.FromResult(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Capture(command);
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Capture(command);
            return ValueTask.FromResult(result);
        }
    }

    private abstract class AggregateContext(DbContextOptions options, Action release) : DbContext(options)
    {
        private bool _released;

        public override void Dispose()
        {
            try { base.Dispose(); }
            finally { Release(); }
        }

        public override async ValueTask DisposeAsync()
        {
            try { await base.DisposeAsync(); }
            finally { Release(); }
        }

        private void Release()
        {
            if (_released)
                return;
            _released = true;
            release();
        }
    }

    private sealed class AggregateComplexContext(DbContextOptions<AggregateComplexContext> options, Action release)
        : AggregateContext(options, release)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<AggregateRow>().ToTable("AggregateRows");
            builder.Entity<AggregateRow>().Property(row => row.Id).ValueGeneratedNever();
            builder.Entity<AggregateRow>().Property(row => row.Stamp).IsConcurrencyToken();
            builder.Entity<AggregateRow>().ComplexProperty(row => row.Data, data =>
            {
                data.ToJson();
                data.Property(item => item.DisplayName).HasJsonPropertyName(DisplayNameJson);
                data.ComplexProperty(item => item.Child).Property(item => item.Label).HasJsonPropertyName(ChildLabelJson);
                data.Property(item => item.Tags);
                data.Property(item => item.Converted).HasConversion(value => value.ToUpperInvariant(), value => value.ToLowerInvariant());
            });
        }
    }

    private sealed class AggregateOwnedContext(DbContextOptions<AggregateOwnedContext> options, Action release)
        : AggregateContext(options, release)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<AggregateRow>().ToTable("AggregateRows");
            builder.Entity<AggregateRow>().Property(row => row.Id).ValueGeneratedNever();
            builder.Entity<AggregateRow>().Property(row => row.Stamp).IsConcurrencyToken();
            builder.Entity<AggregateRow>().OwnsOne(row => row.Data, data =>
            {
                data.ToJson("Data");
                data.Property(item => item.DisplayName).HasJsonPropertyName(DisplayNameJson);
                data.OwnsOne(item => item.Child, child => child.Property(item => item.Label).HasJsonPropertyName(ChildLabelJson));
                data.Property(item => item.Tags);
                data.Property(item => item.Converted).HasConversion(value => value.ToUpperInvariant(), value => value.ToLowerInvariant());
            });
            builder.Entity<AggregateRow>().Navigation(row => row.Data).IsRequired(false);
        }
    }

    private sealed class AggregateRow
    {
        public long Id { get; set; }
        public string Stamp { get; set; } = string.Empty;
        public AggregateData Data { get; set; } = null!;
    }

    private sealed class AggregateData
    {
        public string DisplayName { get; set; } = string.Empty;
        public int Counter { get; set; }
        public byte Small { get; set; }
        public short Short { get; set; }
        public string? Optional { get; set; }
        public List<string> Tags { get; set; } = [];
        public AggregateChild Child { get; set; } = new();
        public bool Enabled { get; set; }
        public long ExactLong { get; set; }
        public double Metric { get; set; }
        public float Single { get; set; }
        public long? NullableCounter { get; set; }
        public Guid Identifier { get; set; }
        public DateTime OccurredAt { get; set; }
        public byte[] Bytes { get; set; } = [];
        public string Converted { get; set; } = string.Empty;
    }

    private sealed class AggregateChild
    {
        public string Label { get; set; } = string.Empty;
        public int Counter { get; set; }
    }

    private sealed class JsonFixture(string profile, ITestOutputHelper output) : IAsyncDisposable
    {
        private readonly string _owner = Guid.NewGuid().ToString("N");
        private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "sndb-ef-json-crud-" + Guid.NewGuid().ToString("N")));
        private readonly List<string> _contentRoots = [];
        private readonly ConcurrentQueue<(string Path, string Protocol, string ContentType)> _requests = new();
        private WebApplication? _app;
        private string _connectionString = string.Empty;
        private int _contextReferences;

        public int ContextReferences => _contextReferences;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (Directory.Exists(_root))
                throw new InvalidOperationException("Fixture owner directory already exists.");
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "test-owner"), _owner);
            // 先用仅含 owner marker 的两对象目录试运行有界清理，再创建实际数据库目录。
            DeleteOwned(_root);
            Directory.CreateDirectory(_root);
            File.WriteAllText(Path.Combine(_root, "test-owner"), _owner);
            using var process = Process.GetCurrentProcess();
            output.WriteLine($"OWNER root={_root}; owner={_owner}; pid={process.Id}; start={process.StartTime.ToUniversalTime():O}; cmd={Environment.CommandLine}");
            if (profile == "embedded")
            {
                _connectionString = "Data Source=" + Path.Combine(_root, "embedded");
                return;
            }

            await StartHostAsync(cancellationToken);
        }

        private async Task StartHostAsync(CancellationToken cancellationToken)
        {
            if (_contentRoots.Count >= 2)
                throw new InvalidOperationException("JSON fixture host budget exceeded.");
            _app = EfTestServerHost.Build(new ServerOptions
            {
                DataRoot = Path.Combine(_root, "server-data"),
                AutoLoadExistingDatabases = true,
                AllowAnonymousProbes = true,
                Tokens = new Dictionary<string, string> { [_owner] = ServerRoles.Admin },
            }, extraArgs: ["--Kestrel:Endpoints:Http:Protocols=" + (profile == "h2c" ? "Http2" : "Http1")]);
            var contentRoot = Path.GetFullPath(_app.Environment.ContentRootPath);
            if (!string.Equals(Path.GetDirectoryName(contentRoot), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(contentRoot).StartsWith("sonnetdb-ef-test-host-", StringComparison.Ordinal)
                || (File.GetAttributes(contentRoot) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Unexpected fixture host directory.");
            File.WriteAllText(Path.Combine(contentRoot, "test-owner"), _owner);
            _contentRoots.Add(contentRoot);
            output.WriteLine($"OWNER contentRoot={contentRoot}; owner={_owner}");
            _app.Use(async (httpContext, next) =>
            {
                if (_requests.Count >= 256)
                    throw new InvalidOperationException("JSON fixture request budget exceeded.");
                _requests.Enqueue((httpContext.Request.Path.Value ?? string.Empty, httpContext.Request.Protocol, httpContext.Request.ContentType ?? string.Empty));
                await next(httpContext);
            });
            await _app.StartAsync(cancellationToken);
            var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
            var address = Assert.Single(addresses.Addresses);
            _connectionString = $"Data Source=sonnetdb+http://{new Uri(address).Authority}/json_{_owner};Token={_owner};Timeout=15;Protocol="
                + (profile == "h2c" ? "frame-http2" : "rest");
        }

        public AggregateContext CreateContext(bool owned, JsonCommandCapture capture)
        {
            _contextReferences++;
            Action release = () => _contextReferences--;
            return owned
                ? new AggregateOwnedContext(new DbContextOptionsBuilder<AggregateOwnedContext>().UseSonnetDB(_connectionString).AddInterceptors(capture).Options, release)
                : new AggregateComplexContext(new DbContextOptionsBuilder<AggregateComplexContext>().UseSonnetDB(_connectionString).AddInterceptors(capture).Options, release);
        }

        public AggregateContext CreateEscapedOwnedContext(JsonCommandCapture capture)
        {
            _contextReferences++;
            return new EscapedOwnedContext(new DbContextOptionsBuilder<EscapedOwnedContext>()
                .UseSonnetDB(_connectionString).AddInterceptors(capture).Options, () => _contextReferences--);
        }

        public async Task ReopenAsync(CancellationToken cancellationToken)
        {
            Assert.Equal(0, _contextReferences);
            if (profile == "embedded")
                return;
            await StopHostAsync();
            await StartHostAsync(cancellationToken);
        }

        public void AssertTransport()
        {
            if (profile == "embedded")
                return;
            var requests = _requests.Where(request => request.Path.Contains("/sql", StringComparison.Ordinal)
                || request.Path == "/v1/frame").ToArray();
            Assert.NotEmpty(requests);
            Assert.All(requests, request => Assert.Equal(profile == "h2c" ? "HTTP/2" : "HTTP/1.1", request.Protocol));
            if (profile == "h2c")
                Assert.Contains(requests, request => request.Path == "/v1/frame"
                    && request.ContentType.StartsWith("application/x-sonnetdb-frame", StringComparison.OrdinalIgnoreCase));
            output.WriteLine($"ACTUAL TRANSPORT profile={profile}; sqlRequests={requests.Length}; protocol={requests[0].Protocol}; frameRequests={requests.Count(request => request.ContentType.StartsWith("application/x-sonnetdb-frame", StringComparison.OrdinalIgnoreCase))}");
        }

        private async Task StopHostAsync()
        {
            if (_app is null)
                return;
            var app = _app;
            _app = null;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await app.StopAsync(timeout.Token); }
            finally { await app.DisposeAsync(); }
        }

        public async ValueTask DisposeAsync()
        {
            await StopHostAsync();
            Assert.Equal(0, _contextReferences);
            foreach (var root in _contentRoots.Append(_root))
                DeleteOwned(root);
        }

        private void DeleteOwned(string root)
        {
            if (_contextReferences != 0 || _app is not null)
                throw new InvalidOperationException("Fixture cleanup refuses live references.");
            if (!Directory.Exists(root))
                return;
            var absolute = Path.GetFullPath(root);
            if (!string.Equals(Path.GetDirectoryName(absolute), Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase)
                || File.ReadAllText(Path.Combine(absolute, "test-owner")) != _owner)
                throw new InvalidOperationException("Fixture cleanup ownership mismatch.");
            var timer = Stopwatch.StartNew();
            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((absolute, 0));
            var count = 0;
            while (pending.Count > 0 && count < 4096 && timer.Elapsed < TimeSpan.FromSeconds(10))
            {
                var item = pending.Pop();
                count++;
                var attributes = File.GetAttributes(item.Path);
                if (item.Depth > 16 || (attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Fixture cleanup refuses deep/reparse objects.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    foreach (var child in Directory.EnumerateFileSystemEntries(item.Path).Take(4097))
                    {
                        if (pending.Count + count >= 4096)
                            throw new InvalidOperationException("Fixture cleanup inventory cap.");
                        var full = Path.GetFullPath(child);
                        if (!full.StartsWith(absolute + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Fixture cleanup path escaped root.");
                        pending.Push((full, item.Depth + 1));
                    }
                }
            }

            if (pending.Count != 0)
                throw new InvalidOperationException("Fixture cleanup time/count cap.");
            Directory.Delete(absolute, recursive: true);
            output.WriteLine($"OWNER CLEAN root={absolute}; owner={_owner}; refs=0; inspected={count}; deleted=true");
        }
    }

    private sealed class ComplexContext(DbContextOptions<ComplexContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<Passkey>().ToTable("Passkeys");
            builder.Entity<Passkey>().Property(passkey => passkey.Id).ValueGeneratedNever();
            builder.Entity<Passkey>().ComplexProperty(passkey => passkey.Data, data => data.ToJson());
        }
    }

    private sealed class OwnedContext(DbContextOptions<OwnedContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<Passkey>().ToTable("Passkeys");
            builder.Entity<Passkey>().Property(passkey => passkey.Id).ValueGeneratedNever();
            builder.Entity<Passkey>().OwnsOne(passkey => passkey.Data, data => data.ToJson("Data"));
        }
    }

    private sealed class ScalarContext(DbContextOptions<ScalarContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<ScalarRecord>().Property(record => record.RawJson).HasColumnType("JSON");
        }
    }

    private sealed class Passkey
    {
        public long Id { get; set; }

        public PasskeyData Data { get; set; } = new();
    }

    private sealed class PasskeyData
    {
        public string DisplayName { get; set; } = string.Empty;

        public int Counter { get; set; }
    }

    private sealed class ScalarRecord
    {
        public long Id { get; set; }

        public string Name { get; set; } = string.Empty;

        public string RawJson { get; set; } = "{}";
    }

    private sealed class ConnectionBlocker : DbConnectionInterceptor
    {
        public int OpenAttempts { get; private set; }

        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
        {
            OpenAttempts++;
            throw new InvalidOperationException("The JSON model-only test must not open a database connection.");
        }
    }
}
