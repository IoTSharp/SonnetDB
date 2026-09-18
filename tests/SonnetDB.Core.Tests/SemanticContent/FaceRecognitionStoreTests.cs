using System.Text;
using System.Text.Json;
using SonnetDB.Engine;
using SonnetDB.Engine.Compaction;
using SonnetDB.Kv;
using SonnetDB.ObjectStorage;
using SonnetDB.SemanticContent;

namespace SonnetDB.Core.Tests.SemanticContent;

public sealed class FaceRecognitionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnetdb-face-" + Guid.NewGuid().ToString("N"));
    private static readonly BiometricAccessContext Context = new() { Actor = "authenticated-alice", Purpose = "consented-access" };
    private static readonly FaceRecognitionProfile Profile = new()
    {
        Embedding = new("face-fixture-v1", "test", "fixture", "1", 3,
            supportedModalities: [SemanticContentModality.Image]),
        Detector = new()
        {
            Id = "detector-fixture-v1", Provider = "test", Model = "fixture", Revision = "1",
            PreprocessingRevision = "1", LabelSetRevision = "1", SupportedModalities = [SemanticContentModality.Image],
        },
    };
    private static readonly FaceRecognitionProbe Probe = new() { ProfileId = "face-fixture-v1", Vector = [1, 0, 0] };

    [Fact]
    public void Enroll_DefaultDisabled_DeniesBeforeAuthorizerAndPersistsDeniedAudit()
    {
        using Tsdb db = Open();
        var authorizer = new Authorizer();
        var service = new FaceRecognitionStore(db, "gallery", Profile, authorizer,
            new() { AllowedPurposes = [Context.Purpose] });
        Assert.Throws<UnauthorizedAccessException>(() => service.Enroll(Context, new()));
        Assert.Equal(0, authorizer.Calls);
        Assert.Contains(service.ReadAudit(Context), entry => entry.Operation == BiometricOperation.Enroll && entry.Outcome == "denied");
        Assert.Empty(db.Keyspaces.Open(FaceReservedResourceNames.KeyspaceName).ScanPrefix("missing"));
    }

    [Fact]
    public async Task VerifyAndSearch_ExplicitIndependentPermissions_DoNotGrantExportOrCrossPurposeAccess()
    {
        using Tsdb db = Open();
        VisualDerivedTarget target = await Target(db);
        var admin = Service(db);
        admin.Enroll(Context, Template("a", target));
        var limited = Service(db, new Authorizer((_, operation) => operation is BiometricOperation.Verify or BiometricOperation.Search));
        Assert.True(limited.Verify(Context, Probe, "a", 0.8).IsMatch);
        Assert.Single(limited.Search(Context, Probe, 10, 0.8));
        Assert.Throws<UnauthorizedAccessException>(() => limited.Export(Context, "a"));
        Assert.Throws<UnauthorizedAccessException>(() => limited.DeleteSubject(Context, "subject-a"));
        BiometricAccessContext another = Context with { Purpose = "another-approved-purpose" };
        var bothPurposes = Service(db, purposes: [Context.Purpose, another.Purpose]);
        Assert.Empty(bothPurposes.Search(another, Probe, 10, -1));
        Assert.Throws<KeyNotFoundException>(() => bothPurposes.Verify(another, Probe, "a", 0.8));
    }

    [Fact]
    public async Task EnrollSearchDelete_Reopen_PreservesRankingAuditAndDeletion()
    {
        using (Tsdb db = Open())
        {
            VisualDerivedTarget target = await Target(db);
            var service = Service(db);
            service.Enroll(Context, Template("b", target));
            service.Enroll(Context, Template("a", target));
            service.Enroll(Context, Template("c", target) with { Vector = [0, 1, 0] });
            Assert.Equal(["a", "b"], service.Search(Context, Probe, 2, 0.9).Select(x => x.TemplateId));
            Assert.False(service.Verify(Context, Probe, "c", 0.5).IsMatch);
        }
        using (Tsdb db = Open())
        {
            var service = Service(db);
            Assert.Equal(3, service.Search(Context, Probe, 10, -1).Count);
            Assert.Equal(1, service.DeleteSubject(Context, "subject-a"));
            Assert.Equal(0, service.DeleteSubject(Context, "subject-a"));
            Assert.Throws<KeyNotFoundException>(() => service.Export(Context, "a"));
            Assert.Contains(service.ReadAudit(Context), entry => entry.Operation == BiometricOperation.Delete
                && entry.Outcome == "succeeded" && entry.AffectedCount == 1);
        }
        using (Tsdb db = Open())
            Assert.Equal(["b", "c"], Service(db).Search(Context, Probe, 10, -1).Select(x => x.TemplateId));
    }

    [Fact]
    public async Task SearchAndExport_SourceOverwrittenOrDeleted_ExcludeStaleTemplate()
    {
        using Tsdb db = Open();
        VisualDerivedTarget target = await Target(db);
        var service = Service(db);
        service.Enroll(Context, Template("a", target));
        var objects = new SndbObjectStore(db);
        await objects.PutObjectAsync("face-fixtures", "source.png", new MemoryStream([9, 8, 7]));
        Assert.Empty(service.Search(Context, Probe, 10, -1));
        Assert.Throws<KeyNotFoundException>(() => service.Export(Context, "a"));
        Assert.Throws<InvalidOperationException>(() => service.Enroll(Context, Template("b", target)));
        Assert.Equal(1, service.DeleteSource(Context, target.Source.ObjectRef));
        VisualDerivedTarget current = await Target(db);
        service.Enroll(Context, Template("c", current));
        objects.DeleteObject("face-fixtures", "source.png");
        Assert.Empty(service.Search(Context, Probe, 10, -1));
        Assert.Throws<KeyNotFoundException>(() => service.Verify(Context, Probe, "c", 0.5));
    }

    [Fact]
    public async Task ApplyRetention_ExpiredTemplate_IsExcludedAndCanBeDeletedWhileDisabled()
    {
        using Tsdb db = Open();
        var clock = new TestClock(DateTimeOffset.UtcNow.AddMinutes(1));
        VisualDerivedTarget target = await Target(db);
        var service = Service(db, clock: clock);
        service.Enroll(Context, Template("a", target) with { ExpiresUtc = clock.GetUtcNow().AddHours(1) });
        clock.Advance(TimeSpan.FromHours(2));
        Assert.Empty(service.Search(Context, Probe, 10, -1));
        Assert.Throws<KeyNotFoundException>(() => service.Export(Context, "a"));
        var disabled = Service(db, enabled: false, clock: clock);
        Assert.Equal(1, disabled.ApplyRetention(Context));
        Assert.Equal(0, disabled.ApplyRetention(Context));
        Assert.Contains(disabled.ReadAudit(Context), entry => entry.Operation == BiometricOperation.ApplyRetention
            && entry.Outcome == "succeeded" && entry.AffectedCount == 1);
    }

    [Fact]
    public async Task DeleteSource_OptionalEtagAndDifferentVersions_MatchesFixedIdentityWithoutCrossVersionDeletion()
    {
        using Tsdb db = Open();
        VisualDerivedTarget target = await Target(db);
        var service = Service(db);
        service.Enroll(Context, Template("a", target with
        {
            Source = target.Source with { ObjectRef = target.Source.ObjectRef with { ETag = null } },
        }));
        Assert.Equal(0, service.DeleteSource(Context, target.Source.ObjectRef with { VersionId = "another-version" }));
        Assert.Equal(1, service.DeleteSource(Context, target.Source.ObjectRef));
    }

    [Fact]
    public async Task Search_SameProfileIdWithDifferentModel_RejectsDriftButAllowsDeletion()
    {
        using Tsdb db = Open();
        VisualDerivedTarget target = await Target(db);
        Service(db).Enroll(Context, Template("a", target));
        var changed = Service(db, profile: Profile with { Embedding = Profile.Embedding with { Revision = "2" } });
        Assert.Throws<InvalidOperationException>(() => changed.Search(Context, Probe, 10, -1));
        Assert.Equal(1, changed.DeleteSubject(Context, "subject-a"));
    }

    [Fact]
    public async Task Enroll_AtTemplateBudget_RejectsNewTemplateAndAllowsReplacement()
    {
        using Tsdb db = Open();
        VisualDerivedTarget target = await Target(db);
        var service = Service(db, maxTemplates: 1);
        service.Enroll(Context, Template("a", target));
        Assert.Throws<InvalidOperationException>(() => service.Enroll(Context, Template("b", target)));
        service.Enroll(Context, Template("a", target) with { Vector = [0, 1, 0] });
        Assert.False(service.Verify(Context, Probe, "a", 0.5).IsMatch);
        Assert.Single(service.Search(Context, Probe, 10, -1));
    }

    [Fact]
    public async Task Verify_InvalidProfileNonfiniteZeroAndCancelledInput_RejectsWithoutResult()
    {
        using Tsdb db = Open();
        VisualDerivedTarget target = await Target(db);
        var service = Service(db);
        service.Enroll(Context, Template("a", target));
        Assert.Throws<ArgumentException>(() => service.Verify(Context, Probe with { ProfileId = "other" }, "a", 0.5));
        Assert.Throws<ArgumentException>(() => service.Verify(Context, Probe with { Vector = [float.NaN, 0, 0] }, "a", 0.5));
        Assert.Throws<ArgumentException>(() => service.Verify(Context, Probe with { Vector = [0, 0, 0] }, "a", 0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.Verify(Context, Probe, "a", double.NaN));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => service.Search(Context, Probe, 10, -1, cancelled.Token));
    }

    [Fact]
    public async Task Enroll_LargeFiniteVector_NormalizesWithoutOverflowAndDoesNotAliasCaller()
    {
        using Tsdb db = Open();
        VisualDerivedTarget target = await Target(db);
        var service = Service(db);
        float[] vector = [float.MaxValue, 0, 0];
        service.Enroll(Context, Template("a", target) with { Vector = vector });
        vector[0] = 0;
        Assert.True(service.Verify(Context, Probe, "a", 0.99).IsMatch);
        float[] exported = service.Export(Context, "a").Vector;
        exported[0] = 0;
        Assert.True(service.Verify(Context, Probe, "a", 0.99).IsMatch);
    }

    [Fact]
    public async Task Enroll_AuditSyncFailure_DoesNotCallAuthorizerOrPersistTemplate()
    {
        using Tsdb db = Open();
        VisualDerivedTarget target = await Target(db);
        KvKeyspace keyspace = db.Keyspaces.Open(FaceReservedResourceNames.KeyspaceName);
        var authorizer = new Authorizer();
        var service = Service(db, authorizer);
        keyspace.WalSyncTestHook = () => throw new IOException("injected audit fsync failure");
        try
        {
            Assert.Throws<IOException>(() => service.Enroll(Context, Template("a", target)));
            Assert.Equal(0, authorizer.Calls);
        }
        finally
        {
            keyspace.WalSyncTestHook = null;
        }
        Assert.DoesNotContain(keyspace.ScanPrefix(""), entry => Encoding.UTF8.GetString(entry.Key.Span).Contains("/t/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Audit_SourceGeneratedJson_ContainsNoIdentityMediaPathVectorOrScore()
    {
        using Tsdb db = Open();
        VisualDerivedTarget target = await Target(db);
        var service = Service(db);
        service.Enroll(Context, Template("a", target));
        service.Verify(Context, Probe, "a", 0.9);
        string json = JsonSerializer.Serialize(service.ReadAudit(Context).ToArray(), FaceRecognitionJsonContext.Default.FaceRecognitionAuditEntryArray);
        Assert.DoesNotContain(Context.Actor, json, StringComparison.Ordinal);
        Assert.DoesNotContain(Context.Purpose, json, StringComparison.Ordinal);
        Assert.DoesNotContain("subject-a", json, StringComparison.Ordinal);
        Assert.DoesNotContain("source.png", json, StringComparison.Ordinal);
        Assert.DoesNotContain("vector", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("similarity", json, StringComparison.OrdinalIgnoreCase);
        Assert.NotEmpty(JsonSerializer.Deserialize(json, FaceRecognitionJsonContext.Default.FaceRecognitionAuditEntryArray)!);
    }

    [Fact]
    public void Constructor_MalformedUtf16OrOversizedProfile_RejectsBeforeCreatingKeyspace()
    {
        using Tsdb db = Open();
        Assert.Throws<EncoderFallbackException>(() => new FaceRecognitionStore(db, "bad\ud800", Profile, new Authorizer()));
        Assert.Throws<ArgumentException>(() => Service(db, profile: Profile with
        {
            Embedding = Profile.Embedding with { Model = new string('x', 257) },
        }));
        Assert.DoesNotContain(FaceReservedResourceNames.KeyspaceName, db.Keyspaces.List());
    }

    [Fact]
    public void Constructor_BoundedListsWithThrowingEnumerators_UsesFixedIndexCopies()
    {
        using Tsdb db = Open();
        var profile = Profile with
        {
            Embedding = Profile.Embedding with { SupportedModalities = new IndexOnlyList<SemanticContentModality>([SemanticContentModality.Image]) },
            Detector = Profile.Detector with { SupportedModalities = new IndexOnlyList<SemanticContentModality>([SemanticContentModality.Image]) },
        };
        var service = new FaceRecognitionStore(db, "gallery", profile, new Authorizer(), new()
        {
            AllowedPurposes = new IndexOnlyList<string>([Context.Purpose]),
        });
        Assert.Throws<UnauthorizedAccessException>(() => service.Enroll(Context, new()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions
    {
        RootDirectory = _root,
        BackgroundFlush = new BackgroundFlushOptions { Enabled = false },
        Compaction = new CompactionPolicy { Enabled = false },
        Kv = KvOptions.Default with { AutoCheckpointEnabled = false, ExpirerEnabled = false, CleanupEnabled = false },
    });

    private static FaceRecognitionStore Service(Tsdb db, Authorizer? authorizer = null, bool enabled = true,
        int maxTemplates = 256, FaceRecognitionProfile? profile = null, string[]? purposes = null, TimeProvider? clock = null)
        => new(db, "gallery", profile ?? Profile, authorizer ?? new Authorizer(), new()
        {
            Enabled = enabled, AllowedPurposes = purposes ?? [Context.Purpose], MaxTemplates = maxTemplates,
        }, clock);

    private static FaceRecognitionTemplate Template(string id, VisualDerivedTarget target) => new()
    {
        Id = id, SubjectId = "subject-" + id, Purpose = Context.Purpose, ProfileId = Profile.Embedding.Id,
        Target = target, Vector = [1, 0, 0], ExpiresUtc = DateTimeOffset.UtcNow.AddDays(1),
    };

    private static async Task<VisualDerivedTarget> Target(Tsdb db)
    {
        var objects = new SndbObjectStore(db);
        objects.CreateBucket("face-fixtures");
        SndbObjectInfo original = await objects.PutObjectAsync("face-fixtures", "source.png", new MemoryStream([1, 2, 3]));
        return new()
        {
            Id = "face-region", DetectorProfileId = Profile.Detector.Id, Label = "face", Confidence = 0.9,
            Region = new() { X = 0.1, Y = 0.2, Width = 0.3, Height = 0.4 },
            Source = new()
            {
                ContentId = "source-content", ContentHash = original.Sha256, Modality = SemanticContentModality.Image,
                ObjectRef = new(original.Bucket, original.Key, original.VersionId, original.ETag), Width = 640, Height = 480,
            },
            IndexState = new(SemanticIndexState.Ready),
        };
    }

    private sealed class Authorizer(Func<BiometricAccessContext, BiometricOperation, bool>? predicate = null) : IBiometricAuthorizer
    {
        public int Calls { get; private set; }
        public bool IsAuthorized(BiometricAccessContext context, string capability, BiometricOperation operation)
        {
            Calls++;
            return capability == "face" && context.Actor == Context.Actor && (predicate?.Invoke(context, operation) ?? true);
        }
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class IndexOnlyList<T>(T[] values) : IReadOnlyList<T>
    {
        public int Count => values.Length;
        public T this[int index] => values[index];
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Unexpected unbounded enumeration.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
