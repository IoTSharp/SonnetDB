using System.Text.Json;
using SonnetDB.SemanticContent;
using SonnetDB.Vector.Primitives;

namespace SonnetDB.Core.Tests.SemanticContent;

public sealed class PersonAppearanceSearchTests
{
    private static readonly BiometricAccessContext Access = new() { Actor = "operator", Purpose = "approved-research" };
    private static PersonAppearanceProfile Profile() => new(PersonAppearanceTask.ReIdentification,
        new("reid-v1", "fixture", "reid", "1", 2, KnnMetric.Cosine,
            supportedModalities: [SemanticContentModality.Image]), "crop-v1");

    [Fact]
    public void Search_DefaultDisabled_AuditsDeniedBeforeReadingCandidates()
    {
        var audit = new List<PersonAppearanceAudit>();
        var search = new PersonAppearanceSearch(Profile(), new Authorizer(), audit.Add, (_, _) => true);
        Assert.Throws<UnauthorizedAccessException>(() => search.Search(Access, [1, 0], []));
        Assert.Equal("denied", Assert.Single(audit).Outcome);
    }

    [Fact]
    public void Search_SeparateCapabilityAndDeniedPurpose_Rejects()
    {
        var audit = new List<PersonAppearanceAudit>();
        var authorizer = new Authorizer { Allow = false };
        var search = Search(audit, authorizer);
        Assert.Throws<UnauthorizedAccessException>(() => search.Search(Access, [1, 0], []));
        Assert.Equal("reid", authorizer.LastCapability);
        Assert.Throws<UnauthorizedAccessException>(() => search.Search(Access with { Purpose = "other" }, [1, 0], []));
        Assert.All(audit, entry => Assert.Equal("denied", entry.Outcome));
    }

    [Fact]
    public void Search_PrecomputedCandidates_OrdersDistancesAndTiesWithAudit()
    {
        var audit = new List<PersonAppearanceAudit>();
        var results = Search(audit).Search(Access, [1, 0], [Candidate("z", [1, 0]), Candidate("a", [1, 0]), Candidate("b", [0, 1])], 2);
        Assert.Equal(["a", "z"], results.Select(x => x.Target.Id));
        Assert.Equal(0, results[0].Distance);
        Assert.Equal(["started", "succeeded"], audit.Select(a => a.Outcome));
        Assert.Equal(audit[0].OperationId, audit[1].OperationId);
        Assert.Equal(3, audit[1].CandidateCount);
    }

    [Fact]
    public void Search_FullProfileDriftOrDifferentTaskOrPurpose_RejectsWithoutResults()
    {
        var audit = new List<PersonAppearanceAudit>();
        var search = Search(audit);
        PersonAppearanceCandidate original = Candidate("a", [1, 0]);
        Assert.Throws<ArgumentException>(() => search.Search(Access, [1, 0], [original with
        {
            Profile = original.Profile with { Embedding = original.Profile.Embedding with { Model = "different" } },
        }]));
        Assert.Throws<ArgumentException>(() => search.Search(Access, [1, 0], [original with
        {
            Profile = original.Profile with { Task = PersonAppearanceTask.Pose },
        }]));
        Assert.Throws<ArgumentException>(() => search.Search(Access, [1, 0], [original with { Purpose = "other" }]));
        Assert.Equal(3, audit.Count(a => a.Outcome == "failed"));
    }

    [Fact]
    public void Search_ExpiredStaleOrNotReadyCandidates_Excludes()
    {
        var audit = new List<PersonAppearanceAudit>();
        var search = Search(audit, current: (target, _) => target.Id != "stale");
        var pending = Candidate("pending", [1, 0]);
        var results = search.Search(Access, [1, 0],
        [
            Candidate("expired", [1, 0]) with { ExpiresAtUtc = DateTimeOffset.UnixEpoch },
            Candidate("stale", [1, 0]),
            pending with { Target = pending.Target with { IndexState = SemanticIndexStateInfo.Pending } },
            Candidate("valid", [0, 1]),
        ]);
        Assert.Equal("valid", Assert.Single(results).Target.Id);
    }

    [Fact]
    public void Search_CallbackMutatesCallerLists_UsesFrozenSnapshot()
    {
        var secondVector = new float[] { 0, 1 };
        var candidates = new[] { Candidate("a", [1, 0]), Candidate("b", secondVector) };
        var search = Search([], current: (_, _) =>
        {
            secondVector[0] = 1;
            secondVector[1] = 0;
            candidates[1] = Candidate("injected", [1, 0]);
            return true;
        });
        var results = search.Search(Access, [1, 0], candidates);
        Assert.Equal(["a", "b"], results.Select(hit => hit.Target.Id));
        Assert.Equal(1, results[1].Distance);
    }

    [Fact]
    public void Search_CustomLists_CapturesCountsOnceAndDoesNotEnumerateCallerData()
    {
        var profile = Profile() with
        {
            Embedding = Profile().Embedding with { SupportedModalities = new SingleCountList<SemanticContentModality>([SemanticContentModality.Image]) },
        };
        var search = new PersonAppearanceSearch(profile, new Authorizer(), _ => { }, (_, _) => true,
            Options() with { AllowedPurposes = new SingleCountList<string>([Access.Purpose]) });
        PersonAppearanceCandidate candidate = Candidate("a", new SingleCountList<float>([1, 0]));
        candidate = candidate with
        {
            DetectorProfile = candidate.DetectorProfile with { SupportedModalities = new SingleCountList<SemanticContentModality>([SemanticContentModality.Image]) },
        };
        Assert.Single(search.Search(Access, new SingleCountList<float>([1, 0]), new SingleCountList<PersonAppearanceCandidate>([candidate])));
    }

    [Fact]
    public void Search_AuditFailureOrCancellation_ReturnsNoResult()
    {
        var search = new PersonAppearanceSearch(Profile(), new Authorizer(), _ => throw new IOException("audit unavailable"),
            (_, _) => true, Options());
        Assert.Throws<IOException>(() => search.Search(Access, [1, 0], []));
        Assert.ThrowsAny<OperationCanceledException>(() => Search([]).Search(Access, [1, 0], [], cancellationToken: new(true)));
    }

    [Fact]
    public void Search_OverBudgetOrDuplicate_Rejects()
    {
        var search = new PersonAppearanceSearch(Profile(), new Authorizer(), _ => { }, (_, _) => true,
            Options() with { MaxCandidates = 1 });
        Assert.Throws<InvalidOperationException>(() => search.Search(Access, [1, 0], [Candidate("a", [1, 0]), Candidate("b", [1, 0])]));
        Assert.Throws<ArgumentException>(() => Search([]).Search(Access, [1, 0], [Candidate("a", [1, 0]), Candidate("a", [1, 0])]));
    }

    [Fact]
    public void Search_SameLocalTargetIdFromDifferentObjects_KeepsBothCandidates()
    {
        PersonAppearanceCandidate first = Candidate("local-1", [1, 0]);
        PersonAppearanceCandidate second = first with
        {
            Target = first.Target with
            {
                Source = first.Target.Source with { ObjectRef = first.Target.Source.ObjectRef with { Key = "second-image" } },
            },
        };
        var hits = Search([]).Search(Access, [1, 0], [first, second]);
        Assert.Equal(2, hits.Count);
        Assert.NotEqual(hits[0].CandidateId, hits[1].CandidateId);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(2_000_000)]
    public void Search_InvalidVector_Rejects(float value)
        => Assert.Throws<ArgumentException>(() => Search([]).Search(Access, [value, 0], []));

    [Fact]
    public void Constructor_GaitWithoutVideoWindow_Rejects()
        => Assert.Throws<ArgumentException>(() => new PersonAppearanceSearch(Profile() with { Task = PersonAppearanceTask.Gait },
            new Authorizer(), _ => { }, (_, _) => true, Options()));

    [Theory]
    [InlineData(PersonAppearanceTask.Gait, "gait")]
    [InlineData(PersonAppearanceTask.Pose, "pose")]
    [InlineData(PersonAppearanceTask.Action, "action")]
    public void Search_IndependentVideoTask_UsesSeparateCapability(PersonAppearanceTask task, string capability)
    {
        PersonAppearanceProfile profile = Profile() with
        {
            Task = task,
            WindowMilliseconds = 1000,
            Embedding = Profile().Embedding with { SupportedModalities = [SemanticContentModality.Video] },
        };
        PersonAppearanceCandidate candidate = Candidate("video", [1, 0]);
        candidate = candidate with
        {
            Profile = profile,
            DetectorProfile = candidate.DetectorProfile with { SupportedModalities = [SemanticContentModality.Video] },
            Target = candidate.Target with
            {
                TimestampMs = 0,
                Source = candidate.Target.Source with { Modality = SemanticContentModality.Video, DurationMs = 1000 },
            },
        };
        var authorizer = new Authorizer();
        var search = new PersonAppearanceSearch(profile, authorizer, _ => { }, (_, _) => true, Options());
        Assert.Single(search.Search(Access, [1, 0], [candidate]));
        Assert.Equal(capability, authorizer.LastCapability);
    }

    [Theory]
    [InlineData(PersonAppearanceTask.Gait)]
    [InlineData(PersonAppearanceTask.Action)]
    public void Search_TemporalTaskWithBothModalities_RejectsStillImage(PersonAppearanceTask task)
    {
        PersonAppearanceProfile profile = Profile() with
        {
            Task = task,
            WindowMilliseconds = 1000,
            Embedding = Profile().Embedding with { SupportedModalities = [SemanticContentModality.Image, SemanticContentModality.Video] },
        };
        var search = new PersonAppearanceSearch(profile, new Authorizer(), _ => { }, (_, _) => true, Options());
        Assert.Throws<ArgumentException>(() => search.Search(Access, [1, 0], [Candidate("a", [1, 0]) with { Profile = profile }]));
    }

    [Fact]
    public void JsonRoundTrip_Candidate_UsesGeneratedMetadata()
    {
        PersonAppearanceCandidate candidate = Candidate("a", [1, 0]);
        string json = JsonSerializer.Serialize(candidate, PersonAppearanceJsonContext.Default.PersonAppearanceCandidate);
        var restored = JsonSerializer.Deserialize(json, PersonAppearanceJsonContext.Default.PersonAppearanceCandidate)!;
        Assert.Equal(candidate.Target, restored.Target);
        Assert.Equal(candidate.Embedding, restored.Embedding);
        Assert.Equal(candidate.Purpose, restored.Purpose);
    }

    private static PersonAppearanceSearchOptions Options() => new() { Enabled = true, AllowedPurposes = [Access.Purpose] };

    private static PersonAppearanceSearch Search(List<PersonAppearanceAudit> audit, Authorizer? authorizer = null,
        Func<VisualDerivedTarget, CancellationToken, bool>? current = null)
        => new(Profile(), authorizer ?? new(), audit.Add, current ?? ((_, _) => true), Options());

    private static PersonAppearanceCandidate Candidate(string id, IReadOnlyList<float> vector)
        => new(new()
        {
            Id = id,
            DetectorProfileId = "detector-v1",
            Label = "person",
            Confidence = 0.9,
            Region = new() { Width = 0.5, Height = 0.5 },
            Source = new()
            {
                ContentId = "content",
                ObjectRef = new("images", "frame", eTag: "etag"),
                ContentHash = "hash",
                Modality = SemanticContentModality.Image,
                Width = 640,
                Height = 480,
            },
            IndexState = new(SemanticIndexState.Ready, updatedUtc: DateTimeOffset.UnixEpoch),
        }, new()
        {
            Id = "detector-v1",
            Provider = "fixture",
            Model = "detector",
            Revision = "1",
            PreprocessingRevision = "1",
            LabelSetRevision = "1",
            SupportedModalities = [SemanticContentModality.Image],
        }, Profile(), vector, Access.Purpose, DateTimeOffset.UtcNow.AddHours(1));

    private sealed class Authorizer : IBiometricAuthorizer
    {
        public bool Allow { get; init; } = true;
        public string? LastCapability { get; private set; }
        public bool IsAuthorized(BiometricAccessContext context, string capability, BiometricOperation operation)
        {
            LastCapability = capability;
            return Allow && operation == BiometricOperation.Search;
        }
    }

    private sealed class SingleCountList<T>(T[] values) : IReadOnlyList<T>
    {
        private int _reads;
        public int Count => ++_reads == 1 ? values.Length : throw new InvalidOperationException("Count must be captured once.");
        public T this[int index] => values[index];
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Caller enumeration is not permitted.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
