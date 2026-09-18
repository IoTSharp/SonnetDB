using System.Text;
using System.Text.Json;
using SonnetDB.Engine;
using SonnetDB.ObjectStorage;
using SonnetDB.SemanticContent;

namespace SonnetDB.Core.Tests.SemanticContent;

public sealed class VehicleObservationStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sndb-vehicle-" + Guid.NewGuid().ToString("N"));
    private static readonly VehicleAppearanceProfile Profile = new(new("vehicle-v1", "fixture", "vehicle", "1", 2,
        supportedModalities: [SemanticContentModality.Image]), "vehicle-crop-v1");

    [Theory]
    [InlineData(" 京ａ·１２３４５ ", "京A12345")]
    [InlineData("ab-c 123", "ABC123")]
    [InlineData("a0O1i", "A0O1I")]
    public void Normalize_SupportedForms_PreservesExactCharacters(string input, string expected)
        => Assert.Equal(expected, VehiclePlateNormalization.Normalize(input));

    [Theory]
    [InlineData("")]
    [InlineData("---")]
    [InlineData("ABC/123")]
    [InlineData("A\t123")]
    [InlineData("Α123")]
    [InlineData("A\u200b123")]
    public void Normalize_UnsupportedOrEmpty_Rejects(string input)
        => Assert.ThrowsAny<ArgumentException>(() => VehiclePlateNormalization.Normalize(input));

    [Fact]
    public async Task Import_QueryUpdateDeleteAndReopen_UsesPersistentExactIndex()
    {
        using (Tsdb database = Open())
        {
            VehicleObservation original = await Observation(database, "car-1", "京 A-12345");
            var store = new VehicleObservationStore(database, "vehicles");
            store.Import(original);
            Assert.Single(store.FindPlate("cn", "京Ａ１２３４５").Hits);
            Assert.Empty(store.FindPlate("us", "京A12345").Hits);
            store.Import(original with { Plate = original.Plate! with { Text = "京B23456" } });
            Assert.Empty(store.FindPlate("cn", "京A12345").Hits);
            Assert.Single(store.FindPlate("cn", "京B23456").Hits);
        }
        using (Tsdb database = Open())
        {
            var store = new VehicleObservationStore(database, "vehicles");
            VehicleSearchHit hit = Assert.Single(store.FindPlate("cn", "京B23456").Hits);
            Assert.Equal(hit.ObservationId, VehicleObservationStore.GetObservationId(hit.Observation.Target));
            Assert.True(store.Delete(hit.Observation.Target));
            Assert.False(store.Delete(hit.ObservationId));
            Assert.Empty(store.FindPlate("cn", "京B23456").Hits);
            Assert.Empty(store.SearchAppearance(Profile, [1, 0]).Hits);
        }
        using (Tsdb database = Open())
            Assert.Empty(new VehicleObservationStore(database, "vehicles").FindPlate("cn", "京B23456").Hits);
    }

    [Fact]
    public async Task FindPlate_OcrConfusablesAndJurisdiction_AreSeparateExactKeys()
    {
        using Tsdb database = Open();
        VehicleObservation original = await Observation(database, "a", "A0I1");
        var store = new VehicleObservationStore(database, "vehicles");
        store.Import(original);
        store.Import(original with { Target = original.Target with { Id = "b" }, Plate = original.Plate! with { Text = "AOI1" } });
        Assert.Equal("a", Assert.Single(store.FindPlate("cn", "A0I1").Hits).Observation.Target.Id);
        Assert.Equal("b", Assert.Single(store.FindPlate("cn", "AOI1").Hits).Observation.Target.Id);
        Assert.Empty(store.FindPlate("ca", "A0I1").Hits);
    }

    [Fact]
    public async Task Import_ProfileDrift_RejectsBeforeReplacingExistingDocument()
    {
        using Tsdb database = Open();
        VehicleObservation original = await Observation(database, "a", "A123");
        var store = new VehicleObservationStore(database, "vehicles");
        store.Import(original);
        Assert.Throws<ArgumentException>(() => store.Import(original with
        {
            AppearanceProfile = Profile with { Embedding = Profile.Embedding with { Model = "different" } },
        }));
        Assert.Throws<ArgumentException>(() => store.Import(original with
        {
            Plate = original.Plate! with { Profile = original.Plate.Profile with { Revision = "other" } },
        }));
        Assert.Throws<ArgumentException>(() => store.Import(original with
        {
            DetectorProfile = original.DetectorProfile with { Revision = "other" },
        }));
        Assert.Equal("fixture-ocr", Assert.Single(store.FindPlate("cn", "A123").Hits).Observation.Plate!.Profile.Model);
    }

    [Fact]
    public async Task SearchAppearance_ExactDistancesAndLimitedWindow_UsesVehicleProfile()
    {
        using Tsdb database = Open();
        VehicleObservation original = await Observation(database, "b", "A123");
        var store = new VehicleObservationStore(database, "vehicles");
        store.Import(original);
        store.Import(original with { Target = original.Target with { Id = "a" } });
        store.Import(original with { Target = original.Target with { Id = "c" }, Embedding = [0, 1] });
        var result = store.SearchAppearance(Profile, [1, 0], new() { Limit = 2 });
        Assert.Equal(["a", "b"], result.Hits.Select(hit => hit.Observation.Target.Id));
        Assert.All(result.Hits, hit => Assert.Equal(0, hit.Distance));
        Assert.True(result.HasMore);
        Assert.Throws<InvalidDataException>(() => store.SearchAppearance(Profile with { InputLayoutRevision = "other" }, [1, 0]));
    }

    [Fact]
    public async Task Import_ConcurrentConflictingProfileContracts_OnlyOneCommits()
    {
        using Tsdb database = Open();
        VehicleObservation original = await Observation(database, "a", "A123");
        var firstStore = new VehicleObservationStore(database, "vehicles");
        var secondStore = new VehicleObservationStore(database, "vehicles");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        bool TryImport(VehicleObservationStore store, VehicleObservation observation)
        {
            try { store.Import(observation, deadline.Token); return true; }
            catch (ArgumentException) { return false; }
        }
        bool[] outcomes = await Task.WhenAll(
            Task.Run(() => TryImport(firstStore, original), deadline.Token),
            Task.Run(() => TryImport(secondStore, original with
            {
                Target = original.Target with { Id = "b" },
                AppearanceProfile = Profile with { Embedding = Profile.Embedding with { Model = "other" } },
            }), deadline.Token)).WaitAsync(deadline.Token);
        Assert.Equal(1, outcomes.Count(static success => success));
        Assert.Single(firstStore.FindPlate("cn", "A123").Hits);
    }

    [Fact]
    public async Task Query_ChangedOrDeletedSource_SkipsStaleAndImportRejectsOldVersion()
    {
        using Tsdb database = Open();
        VehicleObservation original = await Observation(database, "a", "A123");
        var store = new VehicleObservationStore(database, "vehicles");
        store.Import(original);
        await Put(new SndbObjectStore(database), "replacement fixture");
        var result = store.FindPlate("cn", "A123");
        Assert.Empty(result.Hits);
        Assert.Equal(1, result.StaleSkipped);
        Assert.Empty(store.SearchAppearance(Profile, [1, 0]).Hits);
        Assert.Throws<ArgumentException>(() => store.Import(original));
    }

    [Fact]
    public async Task Query_BudgetCancellationAndInvalidVectors_FailWithoutPartialResults()
    {
        using Tsdb database = Open();
        VehicleObservation original = await Observation(database, "a", "A123");
        var store = new VehicleObservationStore(database, "vehicles");
        store.Import(original);
        store.Import(original with { Target = original.Target with { Id = "b" } });
        Assert.Throws<InvalidOperationException>(() => store.FindPlate("cn", "A123", new() { MaxCandidates = 1 }));
        Assert.Throws<InvalidOperationException>(() => store.SearchAppearance(Profile, [1, 0], new() { MaxVectorValues = 2 }));
        Assert.ThrowsAny<OperationCanceledException>(() => store.FindPlate("cn", "A123", cancellationToken: new(true)));
        Assert.Throws<ArgumentException>(() => store.Import(original with { Embedding = [float.NaN, 0] }));
        Assert.Throws<ArgumentException>(() => store.SearchAppearance(Profile, [0, 0]));
        Assert.Equal(2, store.FindPlate("cn", "A123").Hits.Count);
    }

    [Fact]
    public async Task FindPlate_MoreThanOnePage_UsesIndexContinuation()
    {
        using Tsdb database = Open();
        VehicleObservation original = await Observation(database, "base", "A123");
        var store = new VehicleObservationStore(database, "vehicles");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        for (int i = 0; i < 130; i++)
            store.Import(original with { Target = original.Target with { Id = i.ToString("D3") } }, deadline.Token);
        var result = store.FindPlate("cn", "A123", new() { Limit = 129 });
        Assert.Equal(129, result.Hits.Count);
        Assert.True(result.HasMore);
        Assert.Equal(130, result.Scanned);
    }

    [Fact]
    public async Task Read_CorruptedDerivedKey_RejectsPersistedContract()
    {
        using Tsdb database = Open();
        VehicleObservation original = await Observation(database, "a", "A123");
        var store = new VehicleObservationStore(database, "vehicles");
        VehicleObservation imported = store.Import(original);
        var collection = database.Documents.Open("vehicles");
        string id = VehicleObservationStore.GetObservationId(imported.Target);
        VehicleStoredObservation stored = JsonSerializer.Deserialize(collection.Get(id)!.Json,
            VehicleStoreJsonContext.Default.VehicleStoredObservation)!;
        collection.Upsert(id, JsonSerializer.Serialize(stored with { AppearanceProfileId = "wrong" },
            VehicleStoreJsonContext.Default.VehicleStoredObservation));
        Assert.Throws<InvalidDataException>(() => store.FindPlate("cn", "A123"));
    }

    [Fact]
    public async Task Import_SameLocalTargetIdOnDifferentObjects_DoesNotOverwrite()
    {
        using Tsdb database = Open();
        VehicleObservation first = await Observation(database, "local-1", "A123");
        SndbObjectInfo secondSource = await Put(new SndbObjectStore(database), "other frame fixture", "second.png");
        VehicleObservation second = first with
        {
            Target = first.Target with
            {
                Source = first.Target.Source with
                {
                    ObjectRef = new(secondSource.Bucket, secondSource.Key, eTag: secondSource.ETag),
                    ContentHash = secondSource.Sha256,
                },
            },
        };
        var store = new VehicleObservationStore(database, "vehicles");
        first = store.Import(first);
        second = store.Import(second);
        Assert.NotEqual(VehicleObservationStore.GetObservationId(first.Target), VehicleObservationStore.GetObservationId(second.Target));
        Assert.Equal(2, store.FindPlate("cn", "A123").Hits.Count);
        Assert.True(store.Delete(first.Target));
        Assert.Equal("second.png", Assert.Single(store.FindPlate("cn", "A123").Hits).Observation.Target.Source.ObjectRef.Key);
    }

    [Fact]
    public async Task Import_ResolvedCropAliasesOriginal_RejectsBeforeWrite()
    {
        using Tsdb database = Open();
        VehicleObservation original = await Observation(database, "a", "A123");
        SndbObjectInfo source = new SndbObjectStore(database).HeadObject("traffic", "frame.png")!;
        var store = new VehicleObservationStore(database, "vehicles");
        var invalid = original with
        {
            Target = original.Target with
            {
                Source = original.Target.Source with { ObjectRef = new(source.Bucket, source.Key, versionId: source.VersionId) },
                CropObjectRef = new(source.Bucket, source.Key, eTag: source.ETag),
            },
        };
        Assert.True(VisualContentValidator.ValidateTarget(invalid.Target, invalid.DetectorProfile).IsValid);
        Assert.Throws<ArgumentException>(() => store.Import(invalid));
        Assert.Empty(store.FindPlate("cn", "A123").Hits);
    }

    [Fact]
    public async Task JsonRoundTrip_Observation_UsesGeneratedMetadata()
    {
        using Tsdb database = Open();
        VehicleObservation original = await Observation(database, "a", "A123");
        string json = JsonSerializer.Serialize(original, VehicleAppearanceJsonContext.Default.VehicleObservation);
        VehicleObservation restored = JsonSerializer.Deserialize(json, VehicleAppearanceJsonContext.Default.VehicleObservation)!;
        Assert.Equal(original.Target, restored.Target);
        Assert.Equal(original.Plate, restored.Plate);
        Assert.Equal(original.Embedding, restored.Embedding);
    }

    private async Task<VehicleObservation> Observation(Tsdb database, string id, string plate)
    {
        var objects = new SndbObjectStore(database);
        objects.CreateBucket("traffic");
        SndbObjectInfo source = await Put(objects, "contract fixture image bytes");
        return new(new()
        {
            Id = id,
            DetectorProfileId = "detector-v1",
            Label = "vehicle",
            Confidence = 0.95,
            Region = new() { Width = 0.5, Height = 0.5 },
            IndexState = new(SemanticIndexState.Ready, updatedUtc: DateTimeOffset.UnixEpoch),
            Source = new()
            {
                ContentId = "frame",
                ObjectRef = new(source.Bucket, source.Key, eTag: source.ETag),
                ContentHash = source.Sha256,
                Modality = SemanticContentModality.Image,
                Width = 640,
                Height = 480,
            },
        }, new()
        {
            Id = "detector-v1",
            Provider = "fixture",
            Model = "detector",
            Revision = "1",
            PreprocessingRevision = "1",
            LabelSetRevision = "1",
            SupportedModalities = [SemanticContentModality.Image],
        }, Profile, [1, 0], new("CN", plate, 0.9, new("ocr-v1", "fixture", "fixture-ocr", "1", "1")));
    }

    private static async Task<SndbObjectInfo> Put(SndbObjectStore objects, string text, string key = "frame.png")
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        return await objects.PutObjectAsync("traffic", key, stream, "image/png");
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
