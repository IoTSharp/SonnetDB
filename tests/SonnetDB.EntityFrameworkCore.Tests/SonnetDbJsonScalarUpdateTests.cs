using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace SonnetDB.EntityFrameworkCore.Tests;

public sealed partial class SonnetDbJsonAggregateModelTests
{
    [Theory]
    [InlineData("embedded", "DisplayName")]
    [InlineData("embedded", "Counter")]
    [InlineData("embedded", "Small")]
    [InlineData("embedded", "Short")]
    [InlineData("embedded", "Enabled")]
    [InlineData("embedded", "ExactLong")]
    [InlineData("embedded", "Metric")]
    [InlineData("embedded", "Single")]
    [InlineData("embedded", "Optional")]
    [InlineData("embedded", "OptionalNull")]
    [InlineData("embedded", "NullableCounter")]
    [InlineData("embedded", "NullableNull")]
    [InlineData("rest", "DisplayName")]
    [InlineData("rest", "Counter")]
    [InlineData("rest", "Small")]
    [InlineData("rest", "Short")]
    [InlineData("rest", "Enabled")]
    [InlineData("rest", "ExactLong")]
    [InlineData("rest", "Metric")]
    [InlineData("rest", "Single")]
    [InlineData("rest", "Optional")]
    [InlineData("rest", "OptionalNull")]
    [InlineData("rest", "NullableCounter")]
    [InlineData("rest", "NullableNull")]
    [InlineData("h2c", "DisplayName")]
    [InlineData("h2c", "Counter")]
    [InlineData("h2c", "Small")]
    [InlineData("h2c", "Short")]
    [InlineData("h2c", "Enabled")]
    [InlineData("h2c", "ExactLong")]
    [InlineData("h2c", "Metric")]
    [InlineData("h2c", "Single")]
    [InlineData("h2c", "Optional")]
    [InlineData("h2c", "OptionalNull")]
    [InlineData("h2c", "NullableCounter")]
    [InlineData("h2c", "NullableNull")]
    public async Task ToJson_ScalarUpdate_ActualTransportPreservesOtherMembers(string profile, string field)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        var propertyName = field switch { "OptionalNull" => "Optional", "NullableNull" => "NullableCounter", _ => field };
        var jsonName = propertyName == "DisplayName" ? DisplayNameJson : propertyName;
        object? value = field switch
        {
            "DisplayName" => "O'Reilly \"\\ 中文😃 ; DROP TABLE ignored;",
            "Counter" => int.MinValue,
            "Small" => byte.MaxValue,
            "Short" => short.MinValue,
            "Enabled" => false,
            "ExactLong" => long.MinValue + 1,
            "Metric" => -123.456d,
            "Single" => -0.1f,
            "Optional" => "added leaf",
            "NullableCounter" => long.MaxValue,
            _ => null,
        };
        string before;
        await using (var setup = fixture.CreateContext(true, capture))
        {
            await setup.Database.EnsureCreatedAsync(token);
            var data = InitialAggregate();
            if (field == "OptionalNull")
                data.Optional = "before null";
            if (field == "NullableNull")
                data.NullableCounter = 42;
            setup.Add(new AggregateRow { Id = 1, Stamp = "initial", Data = data });
            Assert.Equal(1, await setup.SaveChangesAsync(token));
            var raw = (await ReadRawAsync(setup, 1, token))!;
            if (field == "Optional")
            {
                raw = raw.Replace("\"Optional\":null,", "", StringComparison.Ordinal);
                using var missing = JsonDocument.Parse(raw);
                Assert.False(missing.RootElement.TryGetProperty("Optional", out _));
            }
            before = raw[..^1] + ",\"Unknown\":{\"large\":9007199254740993,\"lexical\":1.2300e+04}}";
            Assert.Equal(1, await setup.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AggregateRows\" SET \"Data\"={before} WHERE \"Id\"=1", token));
        }

        await using (var context = fixture.CreateContext(true, capture))
        {
            var row = await context.Set<AggregateRow>().SingleAsync(token);
            context.Entry(row.Data).Property(propertyName).CurrentValue = value;
            var executedBefore = capture.Commands.Count;
            Assert.Equal(1, field == "Counter" ? context.SaveChanges() : await context.SaveChangesAsync(token));
            var update = Assert.Single(capture.Commands.Skip(executedBefore), command => command.Sql.StartsWith("UPDATE", StringComparison.Ordinal));
            AssertScalarCommand(update, jsonName, value);
            AssertScalarDocument(before, (await ReadRawAsync(context, 1, token))!, jsonName, value);
        }

        await fixture.ReopenAsync(token);
        await using (var reopened = fixture.CreateContext(true, capture))
        {
            var row = await reopened.Set<AggregateRow>().SingleAsync(token);
            Assert.Equal(value, reopened.Entry(row.Data).Property(propertyName).CurrentValue);
            AssertScalarDocument(before, (await ReadRawAsync(reopened, 1, token))!, jsonName, value);
        }
        fixture.AssertTransport();
    }

    private static void AssertScalarCommand(CapturedCommand command, string jsonName, object? value)
    {
        var path = "$['" + jsonName.Replace("'", "''", StringComparison.Ordinal) + "']";
        Assert.Contains("\"Data\" = json_set_value(\"Data\", '" + path.Replace("'", "''", StringComparison.Ordinal) + "', @", command.Sql);
        Assert.Contains("WHERE", command.Sql);
        Assert.Contains("\"Stamp\"", command.Sql);
        Assert.Contains(command.Parameters, parameter => value is null ? parameter.Value is null or DBNull : Equals(value, parameter.Value));
        Assert.DoesNotContain(command.Parameters, parameter => parameter.Value is string text && text.StartsWith('{'));
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("h2c")]
    public async Task ToJson_ScalarUpdate_RollbackAndConcurrencyUseActualAffectedRows(string profile)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await SeedScalarRowsAsync(fixture, capture, token);
        await using (var context = fixture.CreateContext(true, capture))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(token);
            var row = await context.Set<AggregateRow>().SingleAsync(row => row.Id == 1, token);
            row.Data.Counter = 12;
            Assert.Equal(1, await context.SaveChangesAsync(token));
            Assert.Equal(12, await context.Set<AggregateRow>().Where(row => row.Id == 1).Select(row => row.Data.Counter).SingleAsync(token));
            await using (var outside = fixture.CreateContext(true, new JsonCommandCapture()))
                Assert.Equal(7, await outside.Set<AggregateRow>().Where(row => row.Id == 1).Select(row => row.Data.Counter).SingleAsync(token));
            await transaction.RollbackAsync(token);
        }

        await using (var winner = fixture.CreateContext(true, capture))
        await using (var stale = fixture.CreateContext(true, capture))
        {
            var winnerRow = await winner.Set<AggregateRow>().SingleAsync(row => row.Id == 1, token);
            var staleRow = await stale.Set<AggregateRow>().SingleAsync(row => row.Id == 1, token);
            Assert.Equal(7, winnerRow.Data.Counter);
            winnerRow.Data.Counter = 99;
            winnerRow.Stamp = "winner";
            Assert.Equal(1, await winner.SaveChangesAsync(token));
            Assert.Equal(1, capture.CompletedUpdates.Last().Affected);
            staleRow.Data.Counter = 100;
            staleRow.Stamp = "stale";
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync(token));
            Assert.Equal(0, capture.CompletedUpdates.Last().Affected);
            AssertScalarCommand(capture.CompletedUpdates.Last().Command, "Counter", 100);
        }

        await fixture.ReopenAsync(token);
        await using (var reopened = fixture.CreateContext(true, capture))
        {
            var row = await reopened.Set<AggregateRow>().SingleAsync(row => row.Id == 1, token);
            Assert.Equal(99, row.Data.Counter);
            Assert.Equal("winner", row.Stamp);
        }
        fixture.AssertTransport();
    }

    [Theory]
    [InlineData("embedded", false)]
    [InlineData("embedded", true)]
    [InlineData("rest", false)]
    [InlineData("rest", true)]
    [InlineData("h2c", false)]
    [InlineData("h2c", true)]
    public async Task ToJson_ScalarUpdate_SecondRowFailsAfterFirstUpdateAndRollsBack(string profile, bool explicitTransaction)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await SeedScalarRowsAsync(fixture, capture, token);
        string beforeFirst;
        string beforeSecond;
        await using (var context = fixture.CreateContext(true, capture))
        {
            var rows = await context.Set<AggregateRow>().OrderBy(row => row.Id).ToArrayAsync(token);
            beforeFirst = (await ReadRawAsync(context, 1, token))!;
            beforeSecond = (await ReadRawAsync(context, 2, token))!;
            await using var transaction = explicitTransaction ? await context.Database.BeginTransactionAsync(token) : null;
            Assert.Equal(1, await context.Database.ExecuteSqlRawAsync("UPDATE \"AggregateRows\" SET \"Data\"='null' WHERE \"Id\"=2", token));
            rows[0].Data.Counter = 11;
            rows[1].Data.Counter = 22;
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(token));
            var successful = Assert.Single(capture.CompletedUpdates);
            Assert.Equal(1, successful.Affected);
            AssertScalarCommand(successful.Command, "Counter", 11);
            var failed = capture.Commands.Last(command => command.Sql.StartsWith("UPDATE", StringComparison.Ordinal));
            AssertScalarCommand(failed, "Counter", 22);
            Assert.Contains(failed.Parameters, parameter => Equals(parameter.Value, 2L));
            if (transaction is not null)
                await transaction.RollbackAsync(token);
        }

        await fixture.ReopenAsync(token);
        await using (var reopened = fixture.CreateContext(true, capture))
        {
            Assert.Equal(beforeFirst, await ReadRawAsync(reopened, 1, token));
            Assert.Equal(explicitTransaction ? beforeSecond : "null", await ReadRawAsync(reopened, 2, token));
        }
        fixture.AssertTransport();
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("h2c")]
    public async Task ToJson_ScalarUpdate_CancelAfterFirstExecutedUpdateRollsBack(string profile)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        using var cancelUpdate = CancellationTokenSource.CreateLinkedTokenSource(token);
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await SeedScalarRowsAsync(fixture, capture, token);
        await using (var context = fixture.CreateContext(true, capture))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(token);
            var rows = await context.Set<AggregateRow>().OrderBy(row => row.Id).ToArrayAsync(token);
            rows[0].Data.Counter = 11;
            rows[1].Data.Counter = 22;
            capture.AfterUpdate = cancelUpdate.Cancel;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.SaveChangesAsync(cancelUpdate.Token));
            capture.AfterUpdate = null;
            var successful = Assert.Single(capture.CompletedUpdates);
            Assert.Equal(1, successful.Affected);
            AssertScalarCommand(successful.Command, "Counter", 11);
            await transaction.RollbackAsync(token);
        }

        await fixture.ReopenAsync(token);
        await using (var reopened = fixture.CreateContext(true, capture))
            Assert.Equal(new[] { 7, 7 }, await reopened.Set<AggregateRow>().OrderBy(row => row.Id).Select(row => row.Data.Counter).ToArrayAsync(token));
        fixture.AssertTransport();
    }

    [Theory]
    [InlineData("Child")]
    [InlineData("Tags")]
    [InlineData("Identifier")]
    [InlineData("OccurredAt")]
    [InlineData("Bytes")]
    [InlineData("Converted")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public async Task ToJson_ScalarUpdate_UnsupportedShapesRejectBeforeSql(string shape)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        await using var fixture = new JsonFixture("embedded", _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await SeedScalarRowsAsync(fixture, capture, token);
        await using var context = fixture.CreateContext(true, capture);
        var row = await context.Set<AggregateRow>().SingleAsync(row => row.Id == 1, token);
        var before = await ReadRawAsync(context, 1, token);
        switch (shape)
        {
            case "Child": row.Data.Child.Label = "nested"; break;
            case "Tags": row.Data.Tags = ["array"]; break;
            case "Identifier": row.Data.Identifier = Guid.Empty; break;
            case "OccurredAt": row.Data.OccurredAt = DateTime.UnixEpoch; break;
            case "Bytes": row.Data.Bytes = [4, 5]; break;
            case "Converted": row.Data.Converted = "changed"; break;
            case "NaN": row.Data.Metric = double.NaN; break;
            case "Infinity": row.Data.Single = float.PositiveInfinity; break;
            default: throw new InvalidOperationException("Unknown unsupported shape.");
        }
        var executedBefore = capture.Commands.Count;
        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => context.SaveChangesAsync(token));
        Assert.Contains("This JSON operation is not supported", exception.Message);
        Assert.Equal(executedBefore, capture.Commands.Count);
        Assert.Empty(capture.CompletedUpdates);
        Assert.Equal(before, await ReadRawAsync(context, 1, token));
    }

    private static async Task SeedScalarRowsAsync(JsonFixture fixture, JsonCommandCapture capture, CancellationToken token)
    {
        await using var context = fixture.CreateContext(true, capture);
        await context.Database.EnsureCreatedAsync(token);
        context.AddRange(new AggregateRow { Id = 1, Stamp = "initial", Data = InitialAggregate() },
            new AggregateRow { Id = 2, Stamp = "initial", Data = InitialAggregate() });
        Assert.Equal(2, await context.SaveChangesAsync(token));
    }

    [Theory]
    [InlineData("embedded", "sql-null")]
    [InlineData("embedded", "json-null")]
    [InlineData("embedded", "array")]
    [InlineData("embedded", "container")]
    [InlineData("embedded", "duplicate")]
    [InlineData("rest", "sql-null")]
    [InlineData("rest", "json-null")]
    [InlineData("rest", "array")]
    [InlineData("rest", "container")]
    [InlineData("rest", "duplicate")]
    [InlineData("h2c", "sql-null")]
    [InlineData("h2c", "json-null")]
    [InlineData("h2c", "array")]
    [InlineData("h2c", "container")]
    [InlineData("h2c", "duplicate")]
    public async Task ToJson_ScalarUpdate_InvalidStoredShapeRejectsWithoutWrite(string profile, string shape)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await SeedScalarRowsAsync(fixture, capture, token);
        await using var context = fixture.CreateContext(true, capture);
        var row = await context.Set<AggregateRow>().SingleAsync(row => row.Id == 1, token);
        string? raw = shape switch
        {
            "sql-null" => null,
            "json-null" => "null",
            "array" => "[]",
            "container" => "{\"Counter\":{}}",
            "duplicate" => "{\"Counter\":1,\"Counter\":2}",
            _ => throw new InvalidOperationException("Unexpected invalid stored shape."),
        };
        Assert.Equal(1, await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AggregateRows\" SET \"Data\"={raw} WHERE \"Id\"=1", token));
        row.Data.Counter = 99;
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(token));
        AssertScalarCommand(capture.Commands.Last(), "Counter", 99);
        Assert.Empty(capture.CompletedUpdates);
        Assert.Equal(raw, await ReadRawAsync(context, 1, token));
        fixture.AssertTransport();
    }

    private static void AssertScalarDocument(string before, string after, string jsonName, object? value)
    {
        using var previous = JsonDocument.Parse(before);
        using var current = JsonDocument.Parse(after);
        var changed = current.RootElement.GetProperty(jsonName);
        switch (value)
        {
            case null: Assert.Equal(JsonValueKind.Null, changed.ValueKind); break;
            case string text: Assert.Equal(text, changed.GetString()); break;
            case bool boolean: Assert.Equal(boolean, changed.GetBoolean()); break;
            case byte number: Assert.Equal(number, changed.GetByte()); break;
            case short number: Assert.Equal(number, changed.GetInt16()); break;
            case int number: Assert.Equal(number, changed.GetInt32()); break;
            case long number: Assert.Equal(number, changed.GetInt64()); break;
            case float number: Assert.Equal(number, changed.GetSingle()); break;
            case double number: Assert.Equal(number, changed.GetDouble()); break;
            default: throw new InvalidOperationException("Unexpected test scalar.");
        }
        var siblings = previous.RootElement.EnumerateObject().Where(property => property.Name != jsonName).ToArray();
        Assert.Equal(siblings.Select(property => property.Name),
            current.RootElement.EnumerateObject().Where(property => property.Name != jsonName).Select(property => property.Name));
        Assert.All(siblings, property => Assert.Equal(property.Value.GetRawText(), current.RootElement.GetProperty(property.Name).GetRawText()));
    }
}
