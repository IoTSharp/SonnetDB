using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonnetDB.Engine;
using SonnetDB.Media;
using SonnetDB.ObjectStorage;
using SonnetDB.SemanticContent;
using Xunit;

namespace SonnetDB.Media.Tests;

public sealed class MediaSegmentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sonnetdb-media-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ImportJson_TranscriptAndKeyFrame_ReopenPreservesSourceAndTimecode()
    {
        SemanticContentManifest frozen;
        using (Tsdb database = Open())
        {
            SemanticContentManifest manifest = await Manifest(database);
            frozen = new MediaSegmentStore(database).ImportJson(
                JsonSerializer.SerializeToUtf8Bytes(manifest, MediaSegmentJsonContext.Default.SemanticContentManifest));
            Assert.Equal(3, frozen.Segments.Count);
            Assert.NotNull(frozen.ObjectRef!.VersionId);
            Assert.NotNull(frozen.ObjectRef.ETag);
        }
        using Tsdb reopened = Open();
        var store = new MediaSegmentStore(reopened);
        MediaSegmentQueryResult result = Assert.IsType<MediaSegmentQueryResult>(store.Query("clip", new() { Text = "ALARM" }));
        Assert.False(result.IsStale);
        MediaSegmentHit hit = Assert.Single(result.Hits);
        Assert.Equal("alarm", hit.Segment.Id);
        Assert.Equal(1000, hit.Segment.StartMs);
        Assert.Equal(2000, hit.Segment.EndMs);
        Assert.Equal("camera/line-7", hit.Source);
        Assert.Equal(frozen.ObjectRef, hit.ObjectRef);
        MediaSegmentHit frame = Assert.Single(store.Query("clip", new() { KeyFramesOnly = true })!.Hits);
        Assert.Equal(25, frame.Segment.FrameIndex);
        Assert.NotNull(frame.Segment.KeyFrameRef!.VersionId);
        Assert.NotNull(frame.Segment.KeyFrameRef.ETag);
    }

    [Fact]
    public async Task Query_HalfOpenRangeOverlappingSegmentsAndLimit_ReturnsDeterministicBoundedHits()
    {
        using Tsdb database = Open();
        var store = new MediaSegmentStore(database);
        store.Import(await Manifest(database));
        MediaSegmentQueryResult result = store.Query("clip", new() { FromMs = 1000, ToMs = 2000, Limit = 1 })!;
        Assert.Equal("alarm", Assert.Single(result.Hits).Segment.Id);
        Assert.True(result.HasMore);
        Assert.Equal(["alarm", "frame"], store.Query("clip", new() { FromMs = 1000, ToMs = 2000 })!.Hits.Select(hit => hit.Segment.Id));
        Assert.Empty(store.Query("clip", new() { FromMs = 2000, ToMs = 2001 })!.Hits);
        Assert.Equal("intro", Assert.Single(store.Query("clip", new() { ToMs = 1000 })!.Hits).Segment.Id);
    }

    [Fact]
    public async Task Import_ReplacementAndDelete_RemoveOldSegmentsWithoutDeletingObjects()
    {
        using Tsdb database = Open();
        SemanticContentManifest manifest = await Manifest(database);
        var store = new MediaSegmentStore(database);
        store.Import(manifest);
        store.Import(manifest with { Segments = [new("replacement", 0, 0, 500, "changed")] });
        Assert.Empty(store.Query("clip", new() { Text = "alarm" })!.Hits);
        Assert.Equal("replacement", Assert.Single(store.Query("clip", new())!.Hits).Segment.Id);
        Assert.True(store.Delete("clip"));
        Assert.False(store.Delete("clip"));
        Assert.Null(store.Query("clip", new()));
        Assert.NotNull(new SndbObjectStore(database).HeadObject("media", "clip.mp4"));
        Assert.NotNull(new SndbObjectStore(database).HeadObject("media", "frame.png"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Query_SourceOrKeyFrameChanged_ReturnsStaleWithoutOldHits(bool frame, bool delete)
    {
        using Tsdb database = Open();
        var store = new MediaSegmentStore(database);
        store.Import(await Manifest(database));
        var objects = new SndbObjectStore(database);
        string key = frame ? "frame.png" : "clip.mp4";
        if (delete)
            objects.DeleteObject("media", key);
        else
            await Put(objects, key, "replacement", frame ? "image/png" : "video/mp4");
        MediaSegmentQueryResult result = store.Query("clip", new())!;
        Assert.True(result.IsStale);
        Assert.Empty(result.Hits);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("zero")]
    [InlineData("negative")]
    [InlineData("empty")]
    [InlineData("missing-frame")]
    [InlineData("wrong-frame-mime")]
    [InlineData("old-version")]
    [InlineData("hash")]
    [InlineData("size")]
    [InlineData("mime")]
    [InlineData("audio-frame")]
    [InlineData("null-segment")]
    [InlineData("text-budget")]
    [InlineData("segment-budget")]
    public async Task Import_InvalidManifest_RejectsBeforeReplacingExistingData(string invalid)
    {
        using Tsdb database = Open();
        SemanticContentManifest manifest = await Manifest(database);
        var store = new MediaSegmentStore(database);
        store.Import(manifest);
        SemanticContentSegment first = manifest.Segments[0];
        SemanticContentSegment frame = manifest.Segments[2];
        SemanticContentManifest broken = invalid switch
        {
            "duplicate" => manifest with { Segments = [first, first] },
            "zero" => manifest with { Segments = [first with { EndMs = first.StartMs }] },
            "negative" => manifest with { Segments = [frame with { FrameIndex = -1 }] },
            "empty" => manifest with { Segments = [first with { Text = "" }] },
            "missing-frame" => manifest with { Segments = [frame with { KeyFrameRef = frame.KeyFrameRef! with { Key = "missing" } }] },
            "wrong-frame-mime" => manifest with { Segments = [frame with { KeyFrameRef = manifest.ObjectRef }] },
            "old-version" => manifest with { ObjectRef = manifest.ObjectRef! with { VersionId = "obsolete" } },
            "hash" => manifest with { ContentHash = "wrong" },
            "size" => manifest with { SizeBytes = 9999 },
            "mime" => manifest with { MimeType = "video/webm" },
            "audio-frame" => manifest with { Modality = SemanticContentModality.Audio, MimeType = "audio/wav" },
            "null-segment" => manifest with { Segments = [null!] },
            "text-budget" => manifest with { Segments = [first with { Text = new string('a', MediaSegmentStore.MaxTextCharacters + 1) }] },
            "segment-budget" => manifest with { Segments = new SemanticContentSegment[MediaSegmentStore.MaxSegments + 1] },
            _ => throw new InvalidOperationException(),
        };
        Assert.ThrowsAny<ArgumentException>(() => store.Import(broken));
        Assert.Equal(3, store.Query("clip", new())!.Hits.Count);
    }

    [Fact]
    public async Task Import_AudioTranscriptAndEmptyReplacement_AreAccepted()
    {
        using Tsdb database = Open();
        var objects = new SndbObjectStore(database);
        objects.CreateBucket("media");
        SndbObjectInfo audio = await Put(objects, "audio.wav", "audio contract fixture", "audio/wav");
        SemanticContentManifest manifest = Create(audio) with
        {
            Modality = SemanticContentModality.Audio,
            Segments = [new("speaker-1", 0, 0, 100, "hello")],
        };
        var store = new MediaSegmentStore(database);
        store.Import(manifest);
        Assert.Single(store.Query("clip", new())!.Hits);
        store.Import(manifest with { Segments = [] });
        Assert.Empty(store.Query("clip", new())!.Hits);
    }

    [Fact]
    public async Task ImportAndQuery_PreCanceled_LeavePriorDataIntact()
    {
        using Tsdb database = Open();
        SemanticContentManifest manifest = await Manifest(database);
        var store = new MediaSegmentStore(database);
        store.Import(manifest);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => store.Import(manifest with { Segments = [] }, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => store.Query("clip", new(), cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => store.Delete("clip", cancellation.Token));
        Assert.Equal(3, store.Query("clip", new())!.Hits.Count);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"schemaVersion\":99}")]
    public void Query_CorruptedPersistedManifest_ThrowsInsteadOfReturningEmpty(string json)
    {
        using Tsdb database = Open();
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("clip")));
        database.Keyspaces.Open("media-segment-manifests").Put(key, Encoding.UTF8.GetBytes(json));
        Assert.Throws<InvalidDataException>(() => new MediaSegmentStore(database).Query("clip", new()));
    }

    [Fact]
    public void ImportJson_OversizeOrUnknownProperty_IsRejected()
    {
        using Tsdb database = Open();
        var store = new MediaSegmentStore(database);
        Assert.Throws<InvalidDataException>(() => store.ImportJson(new byte[MediaSegmentStore.MaxManifestBytes + 1]));
        Assert.Throws<InvalidDataException>(() => store.ImportJson("{\"mediaBytes\":\"abc\"}"u8));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Query("clip", new() { Limit = 1001 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Query("clip", new() { FromMs = 10, ToMs = 10 }));
    }

    [Fact]
    public async Task Import_InvalidUnicodeAndMetadataBudget_RejectBeforeReplacing()
    {
        using Tsdb database = Open();
        var store = new MediaSegmentStore(database);
        SemanticContentManifest manifest = await Manifest(database);
        store.Import(manifest);
        Assert.Throws<EncoderFallbackException>(() => store.Delete("\ud800"));
        Assert.Throws<EncoderFallbackException>(() => store.Import(manifest with { Id = "\udfff" }));
        SemanticContentSegment[] segments = Enumerable.Range(0, 20)
            .Select(index => new SemanticContentSegment(index.ToString() + new string('a', 4000), index, 0, 1, "short"))
            .ToArray();
        Assert.Throws<ArgumentException>(() => store.Import(manifest with { Segments = segments }));
        Assert.Equal(3, store.Query("clip", new())!.Hits.Count);
    }

    [Fact]
    public async Task Import_JsonEscapingExceedsOutputBudget_PreservesPriorManifest()
    {
        using Tsdb database = Open();
        SemanticContentManifest manifest = await Manifest(database);
        var store = new MediaSegmentStore(database);
        store.Import(manifest);
        Assert.Throws<ArgumentException>(() => store.Import(manifest with
        {
            Segments = [new("large", 0, 0, 1, new string('灯', 750_000))],
        }));
        Assert.Equal(3, store.Query("clip", new())!.Hits.Count);
    }

    private async Task<SemanticContentManifest> Manifest(Tsdb database)
    {
        var objects = new SndbObjectStore(database);
        objects.CreateBucket("media");
        SndbObjectInfo source = await Put(objects, "clip.mp4", "external video bytes contract fixture", "video/mp4");
        SndbObjectInfo frame = await Put(objects, "frame.png", "external frame bytes contract fixture", "image/png");
        return Create(source) with
        {
            Source = "camera/line-7",
            Segments =
            [
                new("intro", 0, 0, 1000, "Opening"),
                new("alarm", 1, 1000, 2000, "Alarm detected"),
                new("frame", 2, 1000, 1040, "red indicator")
                {
                    FrameIndex = 25,
                    KeyFrameRef = new(frame.Bucket, frame.Key, eTag: frame.ETag),
                },
            ],
        };
    }

    private static SemanticContentManifest Create(SndbObjectInfo source)
        => new("clip", new(source.Bucket, source.Key, eTag: source.ETag), source.Sha256,
            source.ContentType, SemanticContentModality.Video, source.SizeBytes);

    private static async Task<SndbObjectInfo> Put(SndbObjectStore objects, string key, string bytes, string mime)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(bytes));
        return await objects.PutObjectAsync("media", key, stream, mime);
    }

    private Tsdb Open() => Tsdb.Open(new TsdbOptions { RootDirectory = _root });

    public void Dispose()
    {
        // _root 是本测试实例创建的唯一临时目录；数据库均先释放。
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
