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
    public async Task ExecuteUpdate_RootAndSubtree_CachedParametersKeepOrderAndReopen(string profile, bool owned)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await using (var context = fixture.CreateContext(owned, capture))
        {
            await context.Database.EnsureCreatedAsync(token);
            context.Add(new AggregateRow { Id = 1, Stamp = "initial", Data = InitialAggregate() });
            Assert.Equal(1, await context.SaveChangesAsync(token));
            var root = InitialAggregate();
            root.DisplayName = "new root 中文😃";
            root.Counter = 29;
            var before = capture.Commands.Count;
            Assert.Equal(1, await context.Set<AggregateRow>().ExecuteUpdateAsync(setters => setters
                .SetProperty(row => row.Data, root)
                .SetProperty(row => row.Data.Child.Counter, row => row.Data.Counter + 1), token));
            Assert.Single(capture.Commands.Skip(before));
            var actual = await context.Set<AggregateRow>().AsNoTracking().SingleAsync(token);
            Assert.Equal(root.DisplayName, actual.Data.DisplayName);
            Assert.Equal(29, actual.Data.Counter);
            Assert.Equal(8, actual.Data.Child.Counter);
            var first = new AggregateChild { Label = "first", Counter = 11 };
            var last = new AggregateChild { Label = "last", Counter = 22 };
            var firstId = Guid.NewGuid();
            var lastId = Guid.NewGuid();
            async Task UpdateAsync(AggregateChild child, Guid id)
                => Assert.Equal(1, await context.Set<AggregateRow>().ExecuteUpdateAsync(setters => setters
                    .SetProperty(row => row.Data.Child.Counter, 999)
                    .SetProperty(row => row.Data.Child, child)
                    .SetProperty(row => row.Data.Identifier, id), token));
            await UpdateAsync(first, firstId);
            await UpdateAsync(last, lastId);
            actual = await context.Set<AggregateRow>().AsNoTracking().SingleAsync(token);
            Assert.Equal(last.Label, actual.Data.Child.Label);
            Assert.Equal(22, actual.Data.Child.Counter);
            Assert.Equal(lastId, actual.Data.Identifier);
        }
        await fixture.ReopenAsync(token);
        await using (var reopened = fixture.CreateContext(owned, capture))
        {
            var row = await reopened.Set<AggregateRow>().AsNoTracking().SingleAsync(token);
            Assert.Equal("last", row.Data.Child.Label);
            Assert.Equal(22, row.Data.Child.Counter);
        }
        fixture.AssertTransport();
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("h2c")]
    public async Task ExecuteUpdate_RootArray_ReplacesAndComposesSingleStatement(string profile)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await using var context = fixture.CreateRootCollectionContext(capture);
        await context.Database.EnsureCreatedAsync(token);
        context.Add(new RootCollectionRow { Id = 1, Items = [NewItem("old")] });
        Assert.Equal(1, await context.SaveChangesAsync(token));
        var replacement = new List<CollectionItem> { NewItem("first"), NewItem("second") };
        var before = capture.Commands.Count;
        Assert.Equal(1, context.Set<RootCollectionRow>().ExecuteUpdate(setters => setters
            .SetProperty(row => row.Items, replacement)
            .SetProperty(row => row.Items[0].Name, "later")));
        Assert.Single(capture.Commands.Skip(before));
        var actual = await context.Set<RootCollectionRow>().AsNoTracking().SingleAsync(token);
        Assert.Equal(["later", "second"], actual.Items.Select(item => item.Name).ToArray());
        Assert.Equal(1, await context.Set<RootCollectionRow>().ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.Items, new List<CollectionItem>()), token));
        using var raw = JsonDocument.Parse((await ReadRawAsync(context, 1, token))!);
        Assert.Equal(0, raw.RootElement.GetArrayLength());
        fixture.AssertTransport();
    }

    [Theory]
    [InlineData("embedded", "[]")]
    [InlineData("embedded", "null")]
    [InlineData("embedded", "3")]
    [InlineData("rest", "[]")]
    [InlineData("rest", "null")]
    [InlineData("rest", "3")]
    [InlineData("h2c", "[]")]
    [InlineData("h2c", "null")]
    [InlineData("h2c", "3")]
    public async Task ExecuteUpdate_ElementSubtree_InvalidSecondRowRejectsEverySetter(string profile, string invalid)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await SeedCollectionAsync(fixture, capture, token);
        await using var context = fixture.CreateCollectionContext(capture);
        context.Add(new CollectionRow { Id = 2, Stamp = "second", Data = new CollectionData { Items = [NewItem("old")] } });
        Assert.Equal(1, await context.SaveChangesAsync(token));
        var invalidJson = "{\"" + CollectionName + "\":" + invalid + "}";
        Assert.Equal(1, await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AggregateRows\" SET \"Data\"={invalidJson} WHERE \"Id\"=2", token));
        var firstBefore = await ReadRawAsync(context, 1, token);
        var replacement = NewItem("must-not-write");
        await Assert.ThrowsAnyAsync<Exception>(() => context.Set<CollectionRow>().ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.Data!.Title, "must-not-write")
            .SetProperty(row => row.Data!.Items[0], replacement)
            .SetProperty(row => row.Stamp, "must-not-write"), token));
        Assert.Equal(firstBefore, await ReadRawAsync(context, 1, token));
        Assert.Equal(invalidJson, await ReadRawAsync(context, 2, token));
        Assert.Equal(["first", "second"], await context.Set<CollectionRow>().OrderBy(row => row.Id).Select(row => row.Stamp).ToArrayAsync(token));
        fixture.AssertTransport();
    }

    [Theory]
    [InlineData("embedded")]
    [InlineData("rest")]
    [InlineData("h2c")]
    public async Task ExecuteUpdate_PrimitiveTarget_OutOfRangeRejectsEveryRow(string profile)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        await using var fixture = new JsonFixture(profile, _output);
        await fixture.StartAsync(token);
        var capture = new JsonCommandCapture();
        await SeedScalarRowsAsync(fixture, capture, token);
        await using var context = fixture.CreateContext(true, capture);
        var before = await ReadRawAsync(context, 1, token);
        const string invalid = "{\"Tags\":[]}";
        Assert.Equal(1, await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"AggregateRows\" SET \"Data\"={invalid} WHERE \"Id\"=2", token));
        await Assert.ThrowsAnyAsync<Exception>(() => context.Set<AggregateRow>().ExecuteUpdateAsync(setters => setters
            .SetProperty(row => row.Stamp, "must-not-write")
            .SetProperty(row => row.Data.Tags[0], "must-not-write"), token));
        Assert.Equal(before, await ReadRawAsync(context, 1, token));
        Assert.Equal(invalid, await ReadRawAsync(context, 2, token));
        Assert.Equal(["initial", "initial"], await context.Set<AggregateRow>().OrderBy(row => row.Id).Select(row => row.Stamp).ToArrayAsync(token));
        fixture.AssertTransport();
    }
}
