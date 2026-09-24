using System.Collections;
using System.Text.Json;
using SonnetDB.SemanticContent;

namespace SonnetDB.Core.Tests.SemanticContent;

/// <summary>视觉派生模型、原对象绑定、轨迹引用与 AOT JSON 合同测试。</summary>
public sealed class VisualContentContractTests
{
    [Fact]
    public void Validate_ImageTarget_PreservesSourceAndNormalizedRegion()
    {
        VisualSourceReference source = ImageSource();
        VisualDerivedTarget target = Target(source);
        VisualDerivationManifest manifest = Manifest(source) with { Targets = [target] };

        Assert.True(VisualContentValidator.Validate(manifest).IsValid);
        Assert.True(VisualContentValidator.ValidateTarget(target, Profile()).IsValid);
        Assert.Equal("original-v1", target.Source.ObjectRef.VersionId);
        Assert.Equal(SemanticDataEgressMode.LocalOnly, Profile().DataEgressPolicy.Mode);
    }

    [Fact]
    public void Validate_VideoTrack_RequiresSameSourceProfileLabelAndTimeOrder()
    {
        VisualDerivationManifest manifest = VideoManifest();

        Assert.True(VisualContentValidator.Validate(manifest).IsValid);
        Assert.Equal(["target-1", "target-2"], manifest.Tracks[0].TargetIds);
    }

    [Fact]
    public void Validate_EmptyDetection_PreservesProfileAndSource()
    {
        Assert.True(VisualContentValidator.Validate(Manifest(ImageSource())).IsValid);
        AssertFailure(Manifest(ImageSource()) with { DetectorProfiles = [] }, "detectorProfiles", "required");
    }

    [Theory]
    [InlineData(double.NaN, 0, 0.2, 0.2)]
    [InlineData(0, double.PositiveInfinity, 0.2, 0.2)]
    [InlineData(0, 0, double.NegativeInfinity, 0.2)]
    [InlineData(0, 0, 0.2, double.NaN)]
    [InlineData(-0.1, 0, 0.2, 0.2)]
    [InlineData(0, 0, 0, 0.2)]
    [InlineData(0.9, 0, 0.2, 0.2)]
    [InlineData(0, 0.9, 0.2, 0.2)]
    public void ValidateTarget_InvalidRegion_RejectsCoordinates(double x, double y, double width, double height)
    {
        VisualDerivedTarget target = Target(ImageSource()) with { Region = new() { X = x, Y = y, Width = width, Height = height } };

        AssertFailure(VisualContentValidator.ValidateTarget(target, Profile()), "target.region", "range");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void ValidateTarget_InvalidConfidence_RejectsNonProbability(double confidence)
        => AssertFailure(VisualContentValidator.ValidateTarget(Target(ImageSource()) with { Confidence = confidence }, Profile()), "target.confidence", "range");

    [Fact]
    public void ValidateTarget_FullFrameAndConfidenceBoundaries_Accepts()
    {
        VisualDerivedTarget target = Target(ImageSource()) with { Region = new() { Width = 1, Height = 1 }, Confidence = 0 };

        Assert.True(VisualContentValidator.ValidateTarget(target, Profile()).IsValid);
        Assert.True(VisualContentValidator.ValidateTarget(target with { Confidence = 1 }, Profile()).IsValid);
    }

    [Fact]
    public void Validate_MixedOriginalVersion_RejectsTargetAndTrack()
    {
        VisualDerivationManifest manifest = VideoManifest();
        VisualSourceReference changed = manifest.Source with { ObjectRef = manifest.Source.ObjectRef with { VersionId = "other-version" } };

        AssertFailure(manifest with { Targets = [manifest.Targets[0] with { Source = changed }, manifest.Targets[1]] }, "targets[0].source", "source");
        AssertFailure(manifest with { Tracks = [manifest.Tracks[0] with { Source = changed }] }, "tracks[0].source", "source");
        AssertFailure(manifest with { Targets = [manifest.Targets[0] with { Source = manifest.Source with { ContentHash = "other-hash" } }, manifest.Targets[1]] }, "targets[0].source", "source");
    }

    [Fact]
    public void ValidateTarget_MissingObjectIdentityAndOriginalCrop_Rejects()
    {
        VisualSourceReference source = ImageSource();
        VisualDerivedTarget target = Target(source);

        AssertFailure(VisualContentValidator.ValidateTarget(target with { Source = source with { ObjectRef = new("images", "source.jpg") } }, Profile()), "target.source.objectRef", "identity");
        AssertFailure(VisualContentValidator.ValidateTarget(target with { CropObjectRef = source.ObjectRef with { ETag = "additional-etag" } }, Profile()), "target.cropObjectRef", "source");
        Assert.True(VisualContentValidator.ValidateTarget(target with { CropObjectRef = new("derived", "crop.jpg", eTag: "crop-etag") }, Profile()).IsValid);
    }

    [Fact]
    public void ValidateTarget_ImageWithVideoFields_Rejects()
    {
        VisualDerivedTarget target = Target(ImageSource()) with { TimestampMs = 0, FrameIndex = 0, TrackId = "track" };

        AssertFailure(VisualContentValidator.ValidateTarget(target, Profile()), "target", "modality");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1000)]
    [InlineData(long.MaxValue)]
    public void ValidateTarget_VideoTimeOutsideHalfOpenDuration_Rejects(long timestamp)
    {
        VisualDerivedTarget target = Target(VideoSource()) with { TimestampMs = timestamp };

        AssertFailure(VisualContentValidator.ValidateTarget(target, Profile()), "target.timestampMs", "range");
    }

    [Fact]
    public void Validate_VideoTrackWithUnknownOrDuplicateTarget_Rejects()
    {
        VisualDerivationManifest manifest = VideoManifest();

        AssertFailure(manifest with { Tracks = [manifest.Tracks[0] with { TargetIds = ["missing"] }] }, "tracks[0].targetIds[0]", "reference");
        AssertFailure(manifest with { Tracks = [manifest.Tracks[0] with { TargetIds = ["target-1", "target-1"] }] }, "tracks[0].targetIds[1]", "unique");
        AssertFailure(manifest with { Tracks = [] }, "targets[0].trackId", "reference");
    }

    [Fact]
    public void Validate_VideoTrackWithMismatchedProfileLabelOrTrack_Rejects()
    {
        VisualDerivationManifest manifest = VideoManifest();

        AssertFailure(manifest with { Targets = [manifest.Targets[0] with { TrackId = "other" }, manifest.Targets[1]] }, "tracks[0].targetIds[0]", "binding");
        AssertFailure(manifest with { Tracks = [manifest.Tracks[0] with { Label = "other" }] }, "tracks[0].targetIds[0]", "binding");
        AssertFailure(manifest with { Tracks = [manifest.Tracks[0] with { DetectorProfileId = "other" }] }, "tracks[0].detectorProfileId", "reference");
    }

    [Fact]
    public void Validate_VideoTrackWithReversedTimeOrFrameOrder_Rejects()
    {
        VisualDerivationManifest manifest = VideoManifest();

        AssertFailure(manifest with { Tracks = [manifest.Tracks[0] with { TargetIds = ["target-2", "target-1"] }] }, "tracks[0].targetIds[1]", "ordering");
        AssertFailure(manifest with { Targets = [manifest.Targets[0], manifest.Targets[1] with { TimestampMs = 10 }] }, "tracks[0].targetIds[1]", "ordering");
        AssertFailure(manifest with { Targets = [manifest.Targets[0], manifest.Targets[1] with { FrameIndex = 0 }] }, "tracks[0].targetIds[1]", "ordering");
        AssertFailure(manifest with { Tracks = [manifest.Tracks[0] with { EndMs = 20 }] }, "tracks[0].targetIds[1]", "range");
    }

    [Fact]
    public void Validate_DuplicateTargetTrackOrProfile_Rejects()
    {
        VisualDerivationManifest manifest = VideoManifest();

        AssertFailure(manifest with { Targets = [manifest.Targets[0], manifest.Targets[0]] }, "targets[1].id", "unique");
        AssertFailure(manifest with { Tracks = [manifest.Tracks[0], manifest.Tracks[0]] }, "tracks[1].id", "unique");
        AssertFailure(manifest with { DetectorProfiles = [Profile(), Profile()] }, "detectorProfiles[1].id", "unique");
    }

    [Fact]
    public void ValidateProfile_IncompleteOrUnsupportedContract_Rejects()
    {
        AssertFailure(VisualContentValidator.ValidateProfile(Profile() with { PreprocessingRevision = "" }), "profile.preprocessingRevision", "required");
        AssertFailure(VisualContentValidator.ValidateProfile(Profile() with { LabelSetRevision = "" }), "profile.labelSetRevision", "required");
        AssertFailure(VisualContentValidator.ValidateProfile(Profile() with { SupportedModalities = [SemanticContentModality.Text] }), "profile.supportedModalities", "modality");
        AssertFailure(VisualContentValidator.ValidateProfile(Profile() with { SupportedModalities = [SemanticContentModality.Image, SemanticContentModality.Image] }), "profile.supportedModalities", "unique");
        AssertFailure(VisualContentValidator.ValidateTarget(Target(VideoSource()) with { TimestampMs = 0 }, Profile() with { SupportedModalities = [SemanticContentModality.Image] }), "target.detectorProfileId", "modality");
    }

    [Fact]
    public void ValidateProfile_InvalidEgressOrDisabledAudit_Rejects()
    {
        AssertFailure(VisualContentValidator.ValidateProfile(Profile() with { DataEgressPolicy = new(SemanticDataEgressMode.ConfiguredProvider) }), "profile.dataEgressPolicy.target", "required");
        AssertFailure(VisualContentValidator.ValidateProfile(Profile() with { DataEgressPolicy = new(SemanticDataEgressMode.LocalOnly, "remote") }), "profile.dataEgressPolicy.target", "egress");
        AssertFailure(VisualContentValidator.ValidateProfile(Profile() with { DataEgressPolicy = new(SemanticDataEgressMode.LocalOnly, auditRequired: false) }), "profile.dataEgressPolicy.auditRequired", "audit");
        Assert.True(VisualContentValidator.ValidateProfile(Profile() with { DataEgressPolicy = new(SemanticDataEgressMode.ConfiguredProvider, "approved-detector") }).IsValid);
    }

    [Fact]
    public void IsCompatibleWith_ChangedCompleteProfile_RejectsDrift()
    {
        VisualDetectorProfile profile = Profile();

        Assert.True(profile.IsCompatibleWith(profile with { SupportedModalities = [SemanticContentModality.Video, SemanticContentModality.Image] }));
        Assert.False(profile.IsCompatibleWith(profile with { Id = "other" }));
        Assert.False(profile.IsCompatibleWith(profile with { Provider = "other" }));
        Assert.False(profile.IsCompatibleWith(profile with { Model = "other" }));
        Assert.False(profile.IsCompatibleWith(profile with { Revision = "other" }));
        Assert.False(profile.IsCompatibleWith(profile with { PreprocessingRevision = "other" }));
        Assert.False(profile.IsCompatibleWith(profile with { LabelSetRevision = "other" }));
        Assert.False(profile.IsCompatibleWith(profile with { SupportedModalities = [SemanticContentModality.Image] }));
        Assert.False(profile.IsCompatibleWith(profile with { DataEgressPolicy = new(SemanticDataEgressMode.ConfiguredProvider, "approved") }));
    }

    [Fact]
    public void ValidateAndCompatibility_CustomEnumerable_OnlyReadBoundedIndexes()
    {
        VisualDetectorProfile profile = Profile() with
        {
            SupportedModalities = new IndexedOnlyList<SemanticContentModality>([SemanticContentModality.Image, SemanticContentModality.Video]),
        };
        VisualDerivationManifest manifest = Manifest(ImageSource()) with
        {
            DetectorProfiles = [profile],
            Targets = [Target(ImageSource())],
        };

        Assert.True(VisualContentValidator.Validate(manifest).IsValid);
        Assert.True(VisualContentValidator.ValidateTarget(manifest.Targets[0], profile).IsValid);
        Assert.True(profile.IsCompatibleWith(Profile()));
        Assert.True(Profile().IsCompatibleWith(profile));
    }

    [Fact]
    public void Validate_NullNestedContracts_ReturnsFailures()
    {
        VisualDerivationManifest manifest = Manifest(ImageSource());

        Assert.False(VisualContentValidator.Validate(manifest with { Source = null!, DetectorProfiles = null!, Targets = null!, Tracks = null!, IndexState = null! }).IsValid);
        Assert.False(VisualContentValidator.Validate(manifest with { DetectorProfiles = [null!], Targets = [null!], Tracks = [null!] }).IsValid);
        Assert.False(VisualContentValidator.ValidateTarget(Target(ImageSource()) with { Source = null!, Region = null!, IndexState = null! }, Profile() with { SupportedModalities = null!, DataEgressPolicy = null! }).IsValid);
    }

    [Fact]
    public void Validate_OverBudgetCollection_RejectsBeforeReadingItems()
    {
        VisualDerivationManifest manifest = Manifest(ImageSource()) with { Targets = new CountOnlyList<VisualDerivedTarget>(4097) };

        AssertFailure(manifest, "targets", "budget");
        AssertFailure(Manifest(ImageSource()) with { DetectorProfiles = new CountOnlyList<VisualDetectorProfile>(65) }, "detectorProfiles", "budget");
        AssertFailure(Manifest(ImageSource()) with { Tracks = new CountOnlyList<VisualTrack>(513) }, "tracks", "budget");
    }

    [Fact]
    public void Validate_TrackReferenceTotalOverBudget_RejectsBeforeSecondTraversal()
    {
        VisualDerivationManifest manifest = VideoManifest();
        string[] references = new string[4096];
        Array.Fill(references, "target-1");
        VisualTrack track = manifest.Tracks[0] with { TargetIds = references };
        VisualTrack second = track with { Id = "track-2", TargetIds = new CountOnlyList<string>(1) };

        SemanticContentValidationResult result = VisualContentValidator.Validate(manifest with { Tracks = [track, second] });

        Assert.False(result.IsValid);
        Assert.Equal(256, result.Failures.Count);
    }

    [Fact]
    public void Validate_OverlongOrMalformedText_Rejects()
    {
        AssertFailure(Manifest(ImageSource()) with { Id = new string('x', 257) }, "id", "budget");
        AssertFailure(Manifest(ImageSource()) with { Id = "unpaired\uD800" }, "id", "encoding");
        Assert.True(VisualContentValidator.Validate(Manifest(ImageSource()) with { Id = "目标-😀" }).IsValid);
    }

    [Fact]
    public void Validate_CancellationBeforeAndDuringTraversal_Stops()
    {
        using var cancellation = new CancellationTokenSource();
        VisualDerivationManifest manifest = Manifest(ImageSource()) with
        {
            Targets = new CancelOnReadList<VisualDerivedTarget>(Target(ImageSource()), cancellation),
        };

        Assert.Throws<OperationCanceledException>(() => VisualContentValidator.Validate(manifest, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => VisualContentValidator.Validate(Manifest(ImageSource()), cancellation.Token));
    }

    [Fact]
    public void Validate_UnknownSchemaOrInvalidState_Rejects()
    {
        AssertFailure(Manifest(ImageSource()) with { SchemaVersion = 2 }, "schemaVersion", "version");
        AssertFailure(Manifest(ImageSource()) with { IndexState = new(SemanticIndexState.Failed) }, "indexState.lastError", "required");
        AssertFailure(Manifest(ImageSource()) with { IndexState = new(SemanticIndexState.Ready, lastError: "old error") }, "indexState.lastError", "state");
        ArgumentException error = Assert.Throws<ArgumentException>(() => VisualContentValidator.ValidateOrThrow(Manifest(ImageSource()) with { Id = "" }));
        Assert.Contains("[id] required", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonRoundTrip_UsesPublicSourceGeneratedMetadataAndRetainsSourceBindings()
    {
        VisualDerivationManifest manifest = VideoManifest();
        string json = JsonSerializer.Serialize(manifest, VisualContentJsonContext.Default.VisualDerivationManifest);
        VisualDerivationManifest restored = Assert.IsType<VisualDerivationManifest>(JsonSerializer.Deserialize(json, VisualContentJsonContext.Default.VisualDerivationManifest));

        Assert.Contains("\"modality\":\"Video\"", json, StringComparison.Ordinal);
        Assert.Contains("\"detectorProfileId\"", json, StringComparison.Ordinal);
        Assert.Equal(manifest.Source, restored.Source);
        Assert.Equal(manifest.Targets, restored.Targets);
        Assert.Equal(manifest.Tracks[0].TargetIds, restored.Tracks[0].TargetIds);
        Assert.True(manifest.DetectorProfiles[0].IsCompatibleWith(restored.DetectorProfiles[0]));
        Assert.True(VisualContentValidator.Validate(restored).IsValid);
        Assert.NotNull(VisualContentJsonContext.Default.VisualDerivedTarget);
        Assert.NotNull(VisualContentJsonContext.Default.VisualRegion);
        Assert.NotNull(VisualContentJsonContext.Default.VisualTrack);
    }

    private static void AssertFailure(VisualDerivationManifest manifest, string path, string rule)
        => AssertFailure(VisualContentValidator.Validate(manifest), path, rule);

    private static void AssertFailure(SemanticContentValidationResult result, string path, string rule)
    {
        Assert.False(result.IsValid);
        Assert.Contains(result.Failures, failure => failure.Path == path && failure.Rule == rule);
    }

    private static VisualSourceReference ImageSource() => new()
    {
        ContentId = "content-1",
        ObjectRef = new("images", "source.jpg", versionId: "original-v1"),
        ContentHash = "sha256:fixture",
        Modality = SemanticContentModality.Image,
        Width = 1920,
        Height = 1080,
    };

    private static VisualSourceReference VideoSource() => ImageSource() with
    {
        ObjectRef = new("videos", "source.mp4", eTag: "original-etag"),
        Modality = SemanticContentModality.Video,
        DurationMs = 1000,
    };

    private static VisualDetectorProfile Profile() => new()
    {
        Id = "detector-v1",
        Provider = "external-import",
        Model = "fixture-detector",
        Revision = "weights-v1",
        PreprocessingRevision = "rgb-resize-v1",
        LabelSetRevision = "classes-v1",
        SupportedModalities = [SemanticContentModality.Image, SemanticContentModality.Video],
    };

    private static VisualDerivedTarget Target(VisualSourceReference source) => new()
    {
        Id = "target-1",
        Source = source,
        DetectorProfileId = "detector-v1",
        Label = "vehicle",
        Confidence = 0.8,
        Region = new() { X = 0.1, Y = 0.2, Width = 0.3, Height = 0.4 },
    };

    private static VisualDerivationManifest Manifest(VisualSourceReference source) => new()
    {
        Id = "derivation-v1",
        Source = source,
        DetectorProfiles = [Profile()],
    };

    private static VisualDerivationManifest VideoManifest()
    {
        VisualSourceReference source = VideoSource();
        VisualDerivedTarget first = Target(source) with { TimestampMs = 10, FrameIndex = 0, TrackId = "track-1" };
        return Manifest(source) with
        {
            Targets = [first, first with { Id = "target-2", TimestampMs = 20, FrameIndex = 1 }],
            Tracks = [new() { Id = "track-1", Source = source, DetectorProfileId = "detector-v1", Label = "vehicle", StartMs = 10, EndMs = 21, TargetIds = ["target-1", "target-2"] }],
        };
    }

    private sealed class CountOnlyList<T>(int count) : IReadOnlyList<T>
    {
        public int Count => count;
        public T this[int index] => throw new InvalidOperationException("Budget must be checked before reading items.");
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class IndexedOnlyList<T>(T[] items) : IReadOnlyList<T>
    {
        public int Count => items.Length;
        public T this[int index] => items[index];
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException("Custom enumerators must never be invoked.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CancelOnReadList<T>(T item, CancellationTokenSource cancellation) : IReadOnlyList<T>
    {
        public int Count => 1;
        public T this[int index]
        {
            get
            {
                cancellation.Cancel();
                return item;
            }
        }
        public IEnumerator<T> GetEnumerator() => throw new InvalidOperationException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
