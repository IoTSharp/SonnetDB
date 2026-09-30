using SonnetDB.Catalog;
using SonnetDB.Storage.Format;
using Xunit;

namespace SonnetDB.Core.Tests.Catalog;

public class MeasurementSchemaTests
{
    private static MeasurementColumn Field(string name, FieldType type = FieldType.Float64)
        => new(name, MeasurementColumnRole.Field, type);

    private static MeasurementColumn Tag(string name)
        => new(name, MeasurementColumnRole.Tag, FieldType.String);

    [Fact]
    public void Create_WithValidColumns_BuildsSchema()
    {
        var schema = MeasurementSchema.Create("cpu", new MeasurementColumn[]
        {
            Tag("host"),
            Field("usage", FieldType.Float64),
        });

        Assert.Equal("cpu", schema.Name);
        Assert.Equal(2, schema.Columns.Count);
        Assert.NotNull(schema.TryGetColumn("host"));
        Assert.NotNull(schema.TryGetColumn("usage"));
        Assert.Null(schema.TryGetColumn("missing"));
        Assert.Single(schema.TagColumns);
        Assert.Single(schema.FieldColumns);
    }

    [Fact]
    public void Create_WithEmptyColumns_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            MeasurementSchema.Create("cpu", []));
    }

    [Fact]
    public void Create_WithoutAnyField_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            MeasurementSchema.Create("cpu", new[] { Tag("host") }));
    }

    [Fact]
    public void Create_WithDuplicateColumnName_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            MeasurementSchema.Create("cpu", new[]
            {
                Field("a"),
                Field("a"),
            }));
    }

    [Fact]
    public void Create_WithCaseOnlyColumnNames_RejectsDuplicates()
    {
        Assert.Throws<ArgumentException>(() =>
            MeasurementSchema.Create("Sensors", new[] { Field("Temperature"), Field("temperature") }));
    }

    [Fact]
    public void Resolve_WithQuotedAndUnquotedColumnNames_PreservesSpellingAndMatchesCorrectly()
    {
        var schema = MeasurementSchema.Create("Sensors", new[] { Field("Temperature") });

        Assert.Equal("Temperature", schema.Resolve("TEMPERATURE", quoted: false)!.Name);
        Assert.Equal("Temperature", schema.Resolve("Temperature", quoted: true)!.Name);
        Assert.Null(schema.Resolve("TEMPERATURE", quoted: true));
        Assert.Null(schema.TryGetColumn("TEMPERATURE"));
    }

    [Fact]
    public void Resolve_WithLegacyCaseVariants_RejectsUnquotedAmbiguity()
    {
        var schema = MeasurementSchema.CreateLoaded("Sensors", new[] { Field("Temperature"), Field("temperature") });

        Assert.Throws<InvalidOperationException>(() => schema.Resolve("TEMPERATURE", quoted: false));
        Assert.Throws<InvalidOperationException>(() => schema.Resolve("Temperature", quoted: false));
        Assert.Equal("Temperature", schema.Resolve("Temperature", quoted: true)!.Name);
        Assert.Equal("temperature", schema.Resolve("temperature", quoted: true)!.Name);
    }

    [Fact]
    public void Create_WithTimeColumn_PreservesExplicitField()
    {
        var schema = MeasurementSchema.Create("m", new[] { Field("time") });
        Assert.Equal("time", schema.Resolve("time", quoted: true)!.Name);
    }

    [Fact]
    public void CreateLoaded_WithLegacyTimeColumn_PreservesExistingSchema()
    {
        var schema = MeasurementSchema.CreateLoaded("m", new[] { Field("time") });
        Assert.Equal("time", schema.TryGetColumn("time")!.Name);
    }

    [Fact]
    public void WithColumns_WithLegacyConflict_PreservesOldNamesAndRejectsNewVariants()
    {
        var schema = MeasurementSchema.CreateLoaded("m", new[] { Field("Usage"), Field("usage") });
        var changed = schema.WithColumns(new[] { Field("Usage", FieldType.Int64), Field("usage"), Field("Status") });

        Assert.Equal(FieldType.Int64, changed.Resolve("Usage", quoted: true)!.DataType);
        Assert.Equal("Status", changed.Resolve("status", quoted: false)!.Name);
        Assert.Throws<InvalidOperationException>(() => changed.Resolve("Usage", quoted: false));
        Assert.Throws<ArgumentException>(() => changed.WithColumns(
            new[] { Field("Usage"), Field("usage"), Field("Status"), Field("status") }));
    }

    [Fact]
    public void Create_WithNonStringTag_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            MeasurementSchema.Create("cpu", new[]
            {
                new MeasurementColumn("host", MeasurementColumnRole.Tag, FieldType.Int64),
                Field("usage"),
            }));
    }

    [Fact]
    public void Create_WithUnknownDataType_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            MeasurementSchema.Create("cpu", new[]
            {
                new MeasurementColumn("usage", MeasurementColumnRole.Field, FieldType.Unknown),
            }));
    }

    [Fact]
    public void Create_WithEmptyName_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            MeasurementSchema.Create(" ", new[] { Field("x") }));
    }

    [Fact]
    public void Catalog_Add_RejectsDuplicate()
    {
        var cat = new MeasurementCatalog();
        cat.Add(MeasurementSchema.Create("m", new[] { Field("x") }));
        Assert.Throws<InvalidOperationException>(() =>
            cat.Add(MeasurementSchema.Create("m", new[] { Field("y") })));
        Assert.True(cat.Contains("m"));
        Assert.Equal(1, cat.Count);
    }

    [Fact]
    public void Catalog_Add_RejectsCaseOnlyDistinctNames()
    {
        var catalog = new MeasurementCatalog();
        catalog.Add(MeasurementSchema.Create("Sensors", new[] { Field("value") }));
        Assert.Throws<InvalidOperationException>(() =>
            catalog.Add(MeasurementSchema.Create("sensors", new[] { Field("value") })));

        Assert.Equal(1, catalog.Count);
    }

    [Fact]
    public void Catalog_Resolve_WithQuotedAndUnquotedNames_PreservesSpellingAndMatchesCorrectly()
    {
        var catalog = new MeasurementCatalog();
        var upper = MeasurementSchema.Create("Sensors", new[] { Field("value") });
        catalog.Add(upper);

        Assert.Same(upper, catalog.Resolve("SENSORS", quoted: false));
        Assert.Same(upper, catalog.Resolve("Sensors", quoted: true));
        Assert.Null(catalog.Resolve("SENSORS", quoted: true));
        Assert.Null(catalog.TryGet("SENSORS"));
    }

    [Fact]
    public void Catalog_Resolve_WithLegacyCaseVariants_RejectsUnquotedAmbiguity()
    {
        var catalog = new MeasurementCatalog();
        var schema = MeasurementSchema.Create("Sensors", new[] { Field("value") });
        var lower = MeasurementSchema.Create("sensors", new[] { Field("value") });
        catalog.LoadOrReplace(schema);
        catalog.LoadOrReplace(lower);

        Assert.Throws<InvalidOperationException>(() => catalog.Resolve("SENSORS", quoted: false));
        Assert.Throws<InvalidOperationException>(() => catalog.Resolve("Sensors", quoted: false));
        Assert.Same(schema, catalog.Resolve("Sensors", quoted: true));
        Assert.Same(lower, catalog.Resolve("sensors", quoted: true));
        Assert.Throws<InvalidOperationException>(() =>
            catalog.Add(MeasurementSchema.Create("SENSORS", new[] { Field("value") })));
    }

    [Fact]
    public void Catalog_TryGet_AfterAdd_ReturnsPublishedSchema()
    {
        var cat = new MeasurementCatalog();
        var schema = MeasurementSchema.Create("cpu", new[] { Field("usage") });

        cat.Add(schema);

        Assert.Same(schema, cat.TryGet("cpu"));
        Assert.True(cat.Contains("cpu"));
        Assert.Equal(1, cat.Count);
    }

    [Fact]
    public void Catalog_LoadOrReplace_AfterSnapshot_PublishesReplacement()
    {
        var cat = new MeasurementCatalog();
        var original = MeasurementSchema.Create("cpu", new[] { Field("usage") });
        var replacement = MeasurementSchema.Create("cpu", new[] { Field("load", FieldType.Int64) });
        cat.Add(original);

        var before = cat.Snapshot();
        cat.LoadOrReplace(replacement);

        Assert.Same(original, Assert.Single(before));
        Assert.Same(replacement, cat.TryGet("cpu"));
        Assert.Equal("load", Assert.Single(cat.Snapshot()).Columns[0].Name);
    }

    [Fact]
    public async Task Catalog_ConcurrentReadsWhileAddingSchemas_AreSafeAndFinalVisible()
    {
        var cat = new MeasurementCatalog();
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var stop = new System.Threading.CancellationTokenSource();

        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    _ = cat.Snapshot();
                    for (int i = 0; i < 64; i++)
                        _ = cat.TryGet("m" + i);
                }
                catch (Exception ex)
                {
                    errors.Enqueue(ex);
                    break;
                }
            }
        });

        for (int i = 0; i < 64; i++)
            cat.Add(MeasurementSchema.Create("m" + i, new[] { Field("v") }));

        stop.Cancel();
        await reader;

        Assert.Empty(errors);
        Assert.Equal(64, cat.Count);
        for (int i = 0; i < 64; i++)
            Assert.NotNull(cat.TryGet("m" + i));
    }

    [Fact]
    public void Catalog_Snapshot_ReturnsSortedByName()
    {
        var cat = new MeasurementCatalog();
        cat.Add(MeasurementSchema.Create("zeta", new[] { Field("x") }));
        cat.Add(MeasurementSchema.Create("alpha", new[] { Field("x") }));
        cat.Add(MeasurementSchema.Create("mu", new[] { Field("x") }));

        var names = cat.Snapshot().Select(s => s.Name).ToArray();
        Assert.Equal(new[] { "alpha", "mu", "zeta" }, names);
    }
}
