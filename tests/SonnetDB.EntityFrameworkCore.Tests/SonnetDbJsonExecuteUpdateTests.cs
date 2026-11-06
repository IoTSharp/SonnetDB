using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace SonnetDB.EntityFrameworkCore.Tests;

public sealed partial class SonnetDbJsonAggregateModelTests
{
    [Theory]
    [InlineData("embedded", true)]
    [InlineData("embedded", false)]
    [InlineData("rest", true)]
    [InlineData("rest", false)]
    [InlineData("h2c", true)]
    [InlineData("h2c", false)]
    public async Task ExecuteUpdate_JsonScalarPaths_ActualTransportPreservesAtomicity(string profile, bool owned)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        var display = "参数 O'Reilly \"\\ 中文😃 ; DROP TABLE ignored;";
        var initial = InitialAggregate();
        await using (var setup = fixture.CreateContext(owned, capture))
        {
            await setup.Database.EnsureCreatedAsync(token);
            setup.Add(new AggregateRow { Id = 1, Stamp = "initial", Data = initial });
            Assert.Equal(1, await setup.SaveChangesAsync(token));
            var raw = AggregateJson(initial, omitOptional: true);
            raw = raw[..^1] + ",\"Unknown\":{\"large\":9007199254740993,\"lexical\":1.2300e+04}}";
            Assert.Equal(1, await setup.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AggregateRows\" SET \"Data\"={raw} WHERE \"Id\"=1", token));
        }

        await using (var context = fixture.CreateContext(owned, capture))
        {
            var commandCount = capture.Commands.Count;
            Assert.Equal(1, await context.Set<AggregateRow>().Where(row => row.Id == 1 && row.Stamp == "initial")
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.Data.DisplayName, display)
                    .SetProperty(row => row.Data.Counter, row => row.Data.Counter + 5)
                    .SetProperty(row => row.Data.Child.Counter, 25)
                    .SetProperty(row => row.Data.Optional, "created")
                    .SetProperty(row => row.Stamp, "winner"), token));
            var update = Assert.Single(capture.Commands.Skip(commandCount));
            Assert.StartsWith("UPDATE", update.Sql, StringComparison.Ordinal);
            Assert.Contains("json_set_value(", update.Sql);
            Assert.DoesNotContain(display, update.Sql);
            Assert.Contains(update.Parameters, parameter => Equals(display, parameter.Value));
            Assert.Contains(update.Parameters, parameter => parameter.Value is string path && path.StartsWith("$[", StringComparison.Ordinal));
            using var raw = JsonDocument.Parse((await ReadRawAsync(context, 1, token))!);
            Assert.Equal(display, raw.RootElement.GetProperty(DisplayNameJson).GetString());
            Assert.Equal(12, raw.RootElement.GetProperty("Counter").GetInt32());
            Assert.Equal(25, raw.RootElement.GetProperty("Child").GetProperty("Counter").GetInt32());
            Assert.Equal(initial.Child.Label, raw.RootElement.GetProperty("Child").GetProperty(ChildLabelJson).GetString());
            Assert.Equal(initial.Tags, raw.RootElement.GetProperty("Tags").EnumerateArray().Select(value => value.GetString()!).ToArray());
            Assert.Equal("created", raw.RootElement.GetProperty("Optional").GetString());
            Assert.Equal("{\"large\":9007199254740993,\"lexical\":1.2300e+04}", raw.RootElement.GetProperty("Unknown").GetRawText());

            Assert.Equal(0, context.Set<AggregateRow>().Where(row => row.Id == 1 && row.Stamp == "initial")
                .ExecuteUpdate(setters => setters.SetProperty(row => row.Data.Counter, 999)));
            Assert.Equal(1, context.Set<AggregateRow>().Where(row => row.Id == 1)
                .ExecuteUpdate(setters => setters
                    .SetProperty(row => row.Data.Counter, 15)
                    .SetProperty(row => row.Data.Counter, 17)
                    .SetProperty(row => row.Data.Optional, (string?)null)));

            await using var transaction = await context.Database.BeginTransactionAsync(token);
            Assert.Equal(1, await context.Set<AggregateRow>().Where(row => row.Id == 1)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Data.Counter, 99), token));
            Assert.Equal(99, await context.Set<AggregateRow>().Where(row => row.Id == 1)
                .Select(row => row.Data.Counter).SingleAsync(token));
            await using (var outside = fixture.CreateContext(owned, new JsonCommandCapture()))
                Assert.Equal(17, await outside.Set<AggregateRow>().Where(row => row.Id == 1)
                    .Select(row => row.Data.Counter).SingleAsync(token));
            await transaction.RollbackAsync(token);
        }

        await fixture.ReopenAsync(token);
        await using (var reopened = fixture.CreateContext(owned, capture))
        {
            var row = await reopened.Set<AggregateRow>().AsNoTracking().SingleAsync(token);
            Assert.Equal(display, row.Data.DisplayName);
            Assert.Equal(17, row.Data.Counter);
            Assert.Equal(25, row.Data.Child.Counter);
            Assert.Null(row.Data.Optional);
            Assert.Equal("winner", row.Stamp);
        }
        fixture.AssertTransport();
    }

    [Theory]
    [InlineData(true, "array")]
    [InlineData(true, "converter")]
    [InlineData(true, "guid")]
    [InlineData(true, "date")]
    [InlineData(false, "array")]
    [InlineData(false, "converter")]
    [InlineData(false, "guid")]
    [InlineData(false, "date")]
    public async Task ExecuteUpdate_UnsupportedJsonShape_RejectsBeforeSendingSql(bool owned, string shape)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var fixture = new JsonFixture("embedded", _output);
        await fixture.StartAsync(timeout.Token);
        var capture = new JsonCommandCapture();
        await using var context = fixture.CreateContext(owned, capture);
        var rows = context.Set<AggregateRow>();
        var exception = Assert.Throws<NotSupportedException>(() =>
        {
            _ = shape switch
            {
                "array" => rows.ExecuteUpdate(setters => setters.SetProperty(row => row.Data.Tags[-1], "changed")),
                "converter" => rows.ExecuteUpdate(setters => setters.SetProperty(row => row.Data.Converted, "changed")),
                "guid" => rows.ExecuteUpdate(setters => setters.SetProperty(row => row.Data.Identifier, Guid.Empty)),
                "date" => rows.ExecuteUpdate(setters => setters.SetProperty(row => row.Data.OccurredAt, DateTime.UnixEpoch)),
                _ => throw new InvalidOperationException("Unknown test shape."),
            };
        });
        Assert.Contains("This JSON operation is not supported", exception.Message);
        Assert.Empty(capture.Commands);
    }

    [Theory]
    [InlineData("embedded", "null")]
    [InlineData("embedded", "[]")]
    [InlineData("embedded", "{\"Child\":null}")]
    [InlineData("embedded", "{\"Child\":3}")]
    [InlineData("rest", "{\"Child\":null}")]
    [InlineData("h2c", "{\"Child\":null}")]
    public async Task ExecuteUpdate_InvalidStoredJson_RejectsWholeStatement(string profile, string invalidJson)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await SeedScalarRowsAsync(fixture, capture, token);
        await using var context = fixture.CreateContext(true, capture);
        var firstBefore = await ReadRawAsync(context, 1, token);
        Assert.Equal(1, await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AggregateRows\" SET \"Data\"={invalidJson} WHERE \"Id\"=2", token));
        await Assert.ThrowsAnyAsync<Exception>(() => context.Set<AggregateRow>()
            .Where(row => row.Id <= 2).ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Data.Counter, 999)
                .SetProperty(row => row.Data.Child.Counter, 111)
                .SetProperty(row => row.Stamp, "must-not-write"), token));
        Assert.Equal(firstBefore, await ReadRawAsync(context, 1, token));
        Assert.Equal(invalidJson, await ReadRawAsync(context, 2, token));
        Assert.Equal(new[] { "initial", "initial" }, await context.Set<AggregateRow>()
            .OrderBy(row => row.Id).Select(row => row.Stamp).ToArrayAsync(token));
        fixture.AssertTransport();
    }
}
