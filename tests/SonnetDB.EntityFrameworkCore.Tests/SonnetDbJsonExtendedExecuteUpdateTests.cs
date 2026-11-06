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
    public async Task ExecuteUpdate_ExtendedScalar_UsesModelJsonEncoding(string profile, bool owned)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await using var context = fixture.CreateContext(owned, capture);
        await context.Database.EnsureCreatedAsync(token);
        context.Add(new AggregateRow { Id = 1, Stamp = "initial", Data = InitialAggregate() });
        Assert.Equal(1, await context.SaveChangesAsync(token));
        var identifier = Guid.NewGuid();
        var date = new DateTime(2026, 11, 6, 12, 34, 56, DateTimeKind.Utc).AddTicks(1234567);
        byte[] bytes = [0, 1, 127, 128, 255];
        var commandCount = capture.Commands.Count;
        var offset = new DateTimeOffset(date).ToOffset(TimeSpan.FromHours(5.5));
        var day = new DateOnly(2026, 11, 6);
        var time = new TimeOnly(12, 34, 56).Add(TimeSpan.FromTicks(1234567));
        var duration = TimeSpan.FromDays(-3).Add(TimeSpan.FromTicks(1234567));
        Assert.Equal(1, await context.Set<AggregateRow>().ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.Data.Identifier, identifier)
            .SetProperty(row => row.Data.OccurredAt, date)
            .SetProperty(row => row.Data.Bytes, bytes)
            .SetProperty(row => row.Data.Converted, "mixedCase")
            .SetProperty(row => row.Data.Offset, offset)
            .SetProperty(row => row.Data.Day, day)
            .SetProperty(row => row.Data.Time, time)
            .SetProperty(row => row.Data.Duration, duration)
            .SetProperty(row => row.Data.Amount, 123456789.123456789m)
            .SetProperty(row => row.Data.State, AggregateState.Ready)
            .SetProperty(row => row.Data.MaybeIdentifier, identifier), token));
        var command = Assert.Single(capture.Commands.Skip(commandCount));
        Assert.DoesNotContain(identifier.ToString(), command.Sql);
        Assert.Contains(command.Parameters, parameter => parameter.Value is string json && json.Contains(identifier.ToString(), StringComparison.Ordinal));
        context.ChangeTracker.Clear();
        var actual = await context.Set<AggregateRow>().AsNoTracking().SingleAsync(token);
        Assert.Equal(identifier, actual.Data.Identifier);
        Assert.Equal(date, actual.Data.OccurredAt);
        Assert.Equal(bytes, actual.Data.Bytes);
        Assert.Equal("mixedcase", actual.Data.Converted);
        Assert.Equal(offset, actual.Data.Offset);
        Assert.Equal(offset.Offset, actual.Data.Offset.Offset);
        Assert.Equal(day, actual.Data.Day);
        Assert.Equal(time, actual.Data.Time);
        Assert.Equal(duration, actual.Data.Duration);
        Assert.Equal(123456789.123456789m, actual.Data.Amount);
        Assert.Equal(AggregateState.Ready, actual.Data.State);
        Assert.Equal(identifier, actual.Data.MaybeIdentifier);
        using var raw = JsonDocument.Parse((await ReadRawAsync(context, 1, token))!);
        Assert.Equal("MIXEDCASE", raw.RootElement.GetProperty("Converted").GetString());
        Assert.Equal("Ready", raw.RootElement.GetProperty("State").GetString());
        Assert.Equal(1, context.Set<AggregateRow>().ExecuteUpdate(setters => setters
            .SetProperty(row => row.Data.MaybeIdentifier, (Guid?)null)));
        await context.DisposeAsync();
        await fixture.ReopenAsync(token);
        await using var reopened = fixture.CreateContext(owned, capture);
        var persisted = await reopened.Set<AggregateRow>().AsNoTracking().SingleAsync(token);
        Assert.Equal(identifier, persisted.Data.Identifier);
        Assert.Equal(date, persisted.Data.OccurredAt);
        Assert.Equal(bytes, persisted.Data.Bytes);
        Assert.Null(persisted.Data.MaybeIdentifier);
        fixture.AssertTransport();
    }

    [Theory]
    [InlineData("embedded", true)]
    [InlineData("embedded", false)]
    [InlineData("rest", true)]
    [InlineData("rest", false)]
    [InlineData("h2c", true)]
    [InlineData("h2c", false)]
    public async Task ExecuteUpdate_Subtree_UsesModelNamesAndPreservesSiblings(string profile, bool owned)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await using var context = fixture.CreateContext(owned, capture);
        await context.Database.EnsureCreatedAsync(token);
        context.Add(new AggregateRow { Id = 1, Stamp = "initial", Data = InitialAggregate() });
        Assert.Equal(1, await context.SaveChangesAsync(token));
        var original = (await ReadRawAsync(context, 1, token))!;
        var withUnknown = original[..^1] + ",\"Unknown\":{\"large\":9007199254740993,\"lexical\":1.2300e+04}}";
        Assert.Equal(1, await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AggregateRows\" SET \"Data\"={withUnknown} WHERE \"Id\"=1", token));
        var child = new AggregateChild { Label = "subtree O'Reilly 中文😃", Counter = 41 };
        var tags = new List<string> { "one", "quote'\\\"", "中文😃" };
        var before = capture.Commands.Count;
        Assert.Equal(1, await context.Set<AggregateRow>().ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.Data.Child, child)
            .SetProperty(row => row.Data.Tags, tags)
            .SetProperty(row => row.Data.Child.Counter, row => row.Data.Counter + 1)
            .SetProperty(row => row.Data.Optional, row => row.Data.Child.Label), token));
        var update = Assert.Single(capture.Commands.Skip(before));
        Assert.Contains("json_set_value(", update.Sql);
        Assert.DoesNotContain(child.Label, update.Sql);
        context.ChangeTracker.Clear();
        var actual = await context.Set<AggregateRow>().AsNoTracking().SingleAsync(token);
        Assert.Equal(child.Label, actual.Data.Child.Label);
        Assert.Equal(8, actual.Data.Child.Counter);
        Assert.Equal(tags, actual.Data.Tags);
        Assert.Equal(7, actual.Data.Counter);
        Assert.Equal(InitialAggregate().Child.Label, actual.Data.Optional);
        using var raw = JsonDocument.Parse((await ReadRawAsync(context, 1, token))!);
        Assert.Equal("{\"large\":9007199254740993,\"lexical\":1.2300e+04}", raw.RootElement.GetProperty("Unknown").GetRawText());
        Assert.Equal(child.Label, raw.RootElement.GetProperty("Child").GetProperty(ChildLabelJson).GetString());
        await using (var transaction = await context.Database.BeginTransactionAsync(token))
        {
            Assert.Equal(1, await context.Set<AggregateRow>().ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Data.Child, new AggregateChild { Label = "rollback", Counter = 99 }), token));
            await using (var outside = fixture.CreateContext(owned, new JsonCommandCapture()))
                Assert.Equal(child.Label, (await outside.Set<AggregateRow>().AsNoTracking().SingleAsync(token)).Data.Child.Label);
            await transaction.RollbackAsync(token);
        }
        Assert.Equal(child.Label, (await context.Set<AggregateRow>().AsNoTracking().SingleAsync(token)).Data.Child.Label);
        fixture.AssertTransport();
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("h2c")]
    public async Task ExecuteUpdate_SubtreeCollections_UsesOwnedNamesAndCodecs(string profile)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await SeedCollectionAsync(fixture, capture, token);
        await using var context = fixture.CreateCollectionContext(capture);
        var replacement = new List<CollectionItem> { NewItem("whole collection 中文😃"), NewItem("second") };
        var before = capture.Commands.Count;
        Assert.Equal(1, await context.Set<CollectionRow>().ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.Data!.Items, replacement)
            .SetProperty(row => row.Data!.Items[0].Name, "later scalar")
            .SetProperty(row => row.Data!.Title, row => row.Data!.Items[0].Name), token));
        Assert.Single(capture.Commands.Skip(before));
        var actual = await context.Set<CollectionRow>().AsNoTracking().SingleAsync(token);
        Assert.Equal("duplicate", actual.Data!.Title);
        Assert.Equal("later scalar", actual.Data.Items[0].Name);
        Assert.Equal(replacement[0].Token, actual.Data.Items[0].Token);
        Assert.Equal(replacement[0].Parts[0].Label, actual.Data.Items[0].Parts[0].Label);
        Assert.Equal("lower", actual.Data.Items[0].Converted);
        var changedId = Guid.NewGuid();
        Assert.Equal(1, await context.Set<CollectionRow>().ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.Data!.Items[0].Token, changedId)
            .SetProperty(row => row.Data!.Items[0].Converted, "mixedCase"), token));
        actual = await context.Set<CollectionRow>().AsNoTracking().SingleAsync(token);
        Assert.Equal(changedId, actual.Data!.Items[0].Token);
        Assert.Equal("mixedcase", actual.Data.Items[0].Converted);
        var item = NewItem("whole element");
        Assert.Equal(1, context.Set<CollectionRow>().ExecuteUpdate(setters => setters
            .SetProperty(row => row.Data!.Items[0], item)));
        actual = await context.Set<CollectionRow>().AsNoTracking().SingleAsync(token);
        Assert.Equal("whole element", actual.Data!.Items[0].Name);
        Assert.Equal("second", actual.Data.Items[1].Name);
        fixture.AssertTransport();
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("h2c")]
    public async Task ExecuteUpdate_Subtree_ExplicitRowVersionHasOneConcurrentWinner(string profile)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        await using (var setup = fixture.CreateContext(true, new JsonCommandCapture()))
        {
            await setup.Database.EnsureCreatedAsync(token);
            setup.Add(new AggregateRow { Id = 1, Stamp = "initial", Data = InitialAggregate() });
            Assert.Equal(1, await setup.SaveChangesAsync(token));
        }
        await using var first = fixture.CreateContext(true, new JsonCommandCapture());
        await using var second = fixture.CreateContext(true, new JsonCommandCapture());
        var a = new AggregateChild { Label = "winner-a", Counter = 1 };
        var b = new AggregateChild { Label = "winner-b", Counter = 2 };
        using var gate = new Barrier(2);
        Task<int> CompeteAsync(DbContext contender, AggregateChild child) => Task.Run(async () =>
        {
            if (!gate.SignalAndWait(TimeSpan.FromSeconds(10), token))
                throw new TimeoutException("Both independent update workers must reach the start barrier.");
            return await contender.Set<AggregateRow>().Where(row => row.Id == 1 && row.RowVersion == 0)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Data.Child, child)
                    .SetProperty(row => row.RowVersion, row => row.RowVersion + 1), token);
        }, token);
        var outcomes = await Task.WhenAll(CompeteAsync(first, a), CompeteAsync(second, b));
        Assert.Equal([0, 1], outcomes.Order().ToArray());
        var actual = await first.Set<AggregateRow>().AsNoTracking().SingleAsync(token);
        Assert.Equal(1, actual.RowVersion);
        Assert.Equal(outcomes[0] == 1 ? a.Label : b.Label, actual.Data.Child.Label);
        fixture.AssertTransport();
    }
}
