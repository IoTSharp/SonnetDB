using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace SonnetDB.EntityFrameworkCore.Tests;

public sealed partial class SonnetDbJsonAggregateModelTests
{
    [Theory]
    [InlineData("embedded", "Identifier")]
    [InlineData("embedded", "OccurredAt")]
    [InlineData("embedded", "Bytes")]
    [InlineData("embedded", "Converted")]
    [InlineData("rest", "Identifier")]
    [InlineData("rest", "OccurredAt")]
    [InlineData("rest", "Bytes")]
    [InlineData("rest", "Converted")]
    [InlineData("h2c", "Identifier")]
    [InlineData("h2c", "OccurredAt")]
    [InlineData("h2c", "Bytes")]
    [InlineData("h2c", "Converted")]
    public async Task ToJson_MetadataWriterUpdate_UsesPropertyJsonEncoding(string profile, string field)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await SeedScalarRowsAsync(fixture, capture, token);
        object expected = field switch
        {
            "Identifier" => Guid.Parse("aa1e29bf-5a81-4f94-8059-0fb6c4130251"),
            "OccurredAt" => DateTime.UnixEpoch,
            "Bytes" => new byte[] { 0, 4, 128, 255 },
            "Converted" => "changed value",
            _ => throw new InvalidOperationException("Unknown field."),
        };
        await using (var context = fixture.CreateContext(true, capture))
        {
            var row = await context.Set<AggregateRow>().SingleAsync(row => row.Id == 1, token);
            context.Entry(row.Data).Property(field).CurrentValue = expected;
            Assert.Equal(1, await context.SaveChangesAsync(token));
            Assert.Contains(", TRUE)", capture.CompletedUpdates.Last().Command.Sql);
        }
        await fixture.ReopenAsync(token);
        await using (var context = fixture.CreateContext(true, capture))
        {
            var row = await context.Set<AggregateRow>().SingleAsync(row => row.Id == 1, token);
            var actual = context.Entry(row.Data).Property(field).CurrentValue;
            if (expected is byte[] bytes)
                Assert.Equal(bytes, Assert.IsType<byte[]>(actual));
            else
                Assert.Equal(expected, actual);
            if (field == "Converted")
            {
                using var document = JsonDocument.Parse((await ReadRawAsync(context, 1, token))!);
                Assert.Equal("CHANGED VALUE", document.RootElement.GetProperty("Converted").GetString());
            }
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
    public async Task ToJson_SubtreeUpdate_PreservesOtherRootMembersAndReopens(string profile, bool escaped)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        AggregateContext Create() => escaped ? fixture.CreateEscapedOwnedContext(capture) : fixture.CreateContext(true, capture);
        await using (var setup = Create())
        {
            await setup.Database.EnsureCreatedAsync(token);
            setup.Add(new AggregateRow { Id = 1, Stamp = "initial", Data = InitialAggregate() });
            Assert.Equal(1, await setup.SaveChangesAsync(token));
        }
        var childName = escaped ? EscapedOwnedName : "Child";
        string before;
        await using (var context = Create())
        {
            var original = (await ReadRawAsync(context, 1, token))!;
            before = original[..^1] + ",\"Unknown\":9007199254740993}";
            Assert.Equal(1, await context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AggregateRows\" SET \"Data\"={before} WHERE \"Id\"=1", token));
            var row = await context.Set<AggregateRow>().SingleAsync(row => row.Id == 1, token);
            row.Data.Child.Label = "subtree 中文";
            row.Data.Child.Counter = 19;
            Assert.Equal(1, await context.SaveChangesAsync(token));
            var path = "$['" + childName.Replace("'", "''", StringComparison.Ordinal) + "']";
            Assert.Contains("json_set_value(\"Data\", '" + path.Replace("'", "''", StringComparison.Ordinal) + "', @", capture.CompletedUpdates.Last().Command.Sql);
            Assert.Contains(", TRUE)", capture.CompletedUpdates.Last().Command.Sql);
        }
        await fixture.ReopenAsync(token);
        await using (var context = Create())
        {
            var row = await context.Set<AggregateRow>().SingleAsync(row => row.Id == 1, token);
            Assert.Equal("subtree 中文", row.Data.Child.Label);
            Assert.Equal(19, row.Data.Child.Counter);
            using var previous = JsonDocument.Parse(before);
            using var current = JsonDocument.Parse((await ReadRawAsync(context, 1, token))!);
            Assert.All(previous.RootElement.EnumerateObject().Where(property => property.Name != childName),
                property => Assert.Equal(property.Value.GetRawText(), current.RootElement.GetProperty(property.Name).GetRawText()));
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
    public async Task ToJson_ArrayUpdate_PreservesSiblingsRollsBackAndReopens(string profile, bool empty)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await SeedScalarRowsAsync(fixture, capture, token);
        string before;
        await using (var setup = fixture.CreateContext(true, capture))
        {
            var original = (await ReadRawAsync(setup, 1, token))!;
            before = original[..^1] + ",\"Unknown\":{\"large\":9007199254740993,\"lexical\":1.2300e+04}}";
            Assert.Equal(1, await setup.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"AggregateRows\" SET \"Data\"={before} WHERE \"Id\"=1", token));
        }
        await using (var context = fixture.CreateContext(true, capture))
        {
            await using var transaction = await context.Database.BeginTransactionAsync(token);
            var row = await context.Set<AggregateRow>().SingleAsync(row => row.Id == 1, token);
            row.Data.Tags = ["rollback array"];
            Assert.Equal(1, await context.SaveChangesAsync(token));
            using var changed = JsonDocument.Parse((await ReadRawAsync(context, 1, token))!);
            Assert.Equal("rollback array", changed.RootElement.GetProperty("Tags")[0].GetString());
            await using (var outside = fixture.CreateContext(true, new JsonCommandCapture()))
                Assert.Equal(before, await ReadRawAsync(outside, 1, token));
            await transaction.RollbackAsync(token);
        }
        string[] expected = empty ? [] : ["O'Reilly", "中文😃", "[1]", "quote\"\\"];
        await using (var context = fixture.CreateContext(true, capture))
        {
            Assert.Equal(before, await ReadRawAsync(context, 1, token));
            var row = await context.Set<AggregateRow>().SingleAsync(row => row.Id == 1, token);
            if (empty)
                row.Data.Tags = [];
            else
            {
                row.Data.Tags.Clear();
                row.Data.Tags.AddRange(expected);
            }
            Assert.Equal(1, await context.SaveChangesAsync(token));
            var update = capture.CompletedUpdates.Last();
            Assert.Equal(1, update.Affected);
            Assert.Contains("json_set_value(\"Data\", '$[''Tags'']', @", update.Command.Sql);
            Assert.Contains(", TRUE)", update.Command.Sql);
            Assert.Contains(update.Command.Parameters, parameter => parameter.Value is string text && text.StartsWith('['));
        }
        await fixture.ReopenAsync(token);
        await using (var reopened = fixture.CreateContext(true, capture))
        {
            Assert.Equal(expected, (await reopened.Set<AggregateRow>().SingleAsync(row => row.Id == 1, token)).Data.Tags);
            using var previous = JsonDocument.Parse(before);
            using var current = JsonDocument.Parse((await ReadRawAsync(reopened, 1, token))!);
            Assert.Equal(expected, current.RootElement.GetProperty("Tags").EnumerateArray().Select(item => item.GetString()));
            Assert.All(previous.RootElement.EnumerateObject().Where(property => property.Name != "Tags"),
                property => Assert.Equal(property.Value.GetRawText(), current.RootElement.GetProperty(property.Name).GetRawText()));
        }
        fixture.AssertTransport();
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("h2c")]
    public async Task ToJson_ArrayUpdate_SecondRowFailureRollsBackFirstActualUpdate(string profile)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await SeedScalarRowsAsync(fixture, capture, token);
        await using var context = fixture.CreateContext(true, capture);
        var rows = await context.Set<AggregateRow>().OrderBy(row => row.Id).ToArrayAsync(token);
        var before = await ReadRawAsync(context, 1, token);
        Assert.Equal(1, await context.Database.ExecuteSqlRawAsync("UPDATE \"AggregateRows\" SET \"Data\"='null' WHERE \"Id\"=2", token));
        rows[0].Data.Tags = ["first"];
        rows[1].Data.Tags = ["second"];
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync(token));
        Assert.Equal(1, Assert.Single(capture.CompletedUpdates).Affected);
        Assert.Equal(before, await ReadRawAsync(context, 1, token));
        Assert.Equal("null", await ReadRawAsync(context, 2, token));
        fixture.AssertTransport();
    }
}
