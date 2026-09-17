using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonnetDB.Engine;
using SonnetDB.Kv;
using SonnetDB.ObjectStorage;
using SonnetDB.SemanticContent;

namespace SonnetDB.Media;

/// <summary>
/// 将外部工具产出的 transcript/关键帧清单保存到数据库 KV/WAL，并查询时间片段。
/// 每个内容清单一次原子替换；不复制原始媒体、不解码媒体、不生成 embedding。
/// </summary>
public sealed class MediaSegmentStore
{
    /// <summary>单份 UTF-8 清单的最大字节数。</summary>
    public const int MaxManifestBytes = 4 * 1024 * 1024;

    /// <summary>单份清单的最大片段数。</summary>
    public const int MaxSegments = 10_000;

    /// <summary>单份清单中 transcript/OCR 的最大 UTF-16 字符总数。</summary>
    public const int MaxTextCharacters = 1_000_000;

    /// <summary>所有标识、引用和状态元数据的最大 UTF-16 字符总数。</summary>
    public const int MaxMetadataCharacters = 65_536;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly KvKeyspace _data;
    private readonly SndbObjectStore _objects;
    private readonly TimeSpan _maxDuration;

    /// <summary>打开可选媒体扩展；数据库生命周期由调用方管理。</summary>
    /// <param name="database">保存对象主数据及派生清单的数据库。</param>
    /// <param name="maxDuration">每次操作的协作超时，默认 30 秒，最多五分钟。</param>
    public MediaSegmentStore(Tsdb database, TimeSpan? maxDuration = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _maxDuration = maxDuration ?? TimeSpan.FromSeconds(30);
        if (_maxDuration <= TimeSpan.Zero || _maxDuration > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(maxDuration));
        _data = database.Keyspaces.Open("media-segment-manifests");
        _objects = new SndbObjectStore(database);
    }

    /// <summary>导入外部工具的 UTF-8 JSON 清单，校验及来源检查通过后才替换原有全部片段。</summary>
    /// <param name="utf8Json">不超过 4 MiB 的清单。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>已固定对象版本和 ETag 的清单。</returns>
    public SemanticContentManifest ImportJson(ReadOnlySpan<byte> utf8Json, CancellationToken cancellationToken = default)
    {
        using var deadline = Deadline(cancellationToken);
        return ImportCore(Decode(utf8Json, deadline.Token), deadline.Token);
    }

    /// <summary>导入音视频派生清单；同一内容 ID 的旧片段与新片段不会混合。</summary>
    /// <param name="manifest">外部转写或关键帧工具生成的清单。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>已固定对象版本和 ETag 的清单。</returns>
    public SemanticContentManifest Import(SemanticContentManifest manifest, CancellationToken cancellationToken = default)
    {
        using var deadline = Deadline(cancellationToken);
        return ImportCore(manifest, deadline.Token);
    }

    /// <summary>在单个内容的片段中执行有界文本及时间交集查询；来源失效时返回陈旧状态和空命中。</summary>
    /// <param name="contentId">稳定内容 ID。</param>
    /// <param name="query">查询条件及结果预算。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>不存在时为 null；损坏清单抛出 InvalidDataException。</returns>
    public MediaSegmentQueryResult? Query(string contentId, MediaSegmentQuery query, CancellationToken cancellationToken = default)
    {
        using var deadline = Deadline(cancellationToken);
        CancellationToken token = deadline.Token;
        ValidateString(contentId, required: true);
        ArgumentNullException.ThrowIfNull(query);
        ValidateString(query.Text);
        if (query.FromMs < 0 || query.ToMs <= query.FromMs || query.Limit is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(query));
        token.ThrowIfCancellationRequested();
        byte[]? bytes = _data.Get(Key(contentId));
        if (bytes is null)
            return null;
        SemanticContentManifest manifest;
        try
        {
            manifest = Freeze(Decode(bytes, token), token);
            if (manifest.Id != contentId)
                throw new InvalidDataException("媒体清单 ID 与持久键不一致。");
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("持久媒体清单违反合同。", exception);
        }

        if (!IsCurrent(manifest.ObjectRef!, token))
            return new(true, Array.Empty<MediaSegmentHit>(), false);
        var hits = new List<MediaSegmentHit>(query.Limit);
        bool hasMore = false;
        foreach (SemanticContentSegment segment in manifest.Segments)
        {
            token.ThrowIfCancellationRequested();
            if (segment.StartMs >= query.ToMs || segment.EndMs <= query.FromMs
                || query.KeyFramesOnly && segment.KeyFrameRef is null
                || !string.IsNullOrEmpty(query.Text)
                    && (segment.Text is null || !segment.Text.Contains(query.Text, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (segment.KeyFrameRef is not null && !IsCurrent(segment.KeyFrameRef, token))
                return new(true, Array.Empty<MediaSegmentHit>(), false);
            if (hits.Count == query.Limit)
            {
                hasMore = true;
                break;
            }
            hits.Add(new(manifest.Id, manifest.Source, manifest.ObjectRef!, segment));
        }
        token.ThrowIfCancellationRequested();
        return new(false, hits.AsReadOnly(), hasMore);
    }

    /// <summary>删除内容的全部派生片段；保留原始媒体与关键帧对象。</summary>
    /// <param name="contentId">稳定内容 ID。</param>
    /// <param name="cancellationToken">取消标记。</param>
    /// <returns>本次是否删除了清单。</returns>
    public bool Delete(string contentId, CancellationToken cancellationToken = default)
    {
        using var deadline = Deadline(cancellationToken);
        ValidateString(contentId, required: true);
        deadline.Token.ThrowIfCancellationRequested();
        return _data.Delete(Key(contentId));
    }

    private SemanticContentManifest ImportCore(SemanticContentManifest manifest, CancellationToken token)
    {
        manifest = Freeze(manifest, token);
        SndbObjectInfo source = Resolve(manifest.ObjectRef!, token);
        if (!string.Equals(source.Sha256, manifest.ContentHash, StringComparison.OrdinalIgnoreCase)
            || source.SizeBytes != manifest.SizeBytes
            || !string.Equals(source.ContentType, manifest.MimeType, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("清单 hash、长度或 MIME 与原始对象不一致。", nameof(manifest));
        var segments = new SemanticContentSegment[manifest.Segments.Count];
        for (int index = 0; index < segments.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            SemanticContentSegment segment = manifest.Segments[index];
            if (segment.KeyFrameRef is not null)
            {
                SndbObjectInfo frame = Resolve(segment.KeyFrameRef, token);
                if (!frame.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("关键帧必须引用 image/* 对象。", nameof(manifest));
                segment = segment with { KeyFrameRef = Reference(frame) };
            }
            segments[index] = segment;
        }
        manifest = Freeze(manifest with { ObjectRef = Reference(source), Segments = segments }, token);
        using var output = new BoundedManifestStream();
        JsonSerializer.Serialize(output, manifest, MediaSegmentJsonContext.Default.SemanticContentManifest);
        byte[] bytes = output.ToArray();
        token.ThrowIfCancellationRequested();
        _data.Put(Key(manifest.Id), bytes);
        return manifest;
    }

    private static SemanticContentManifest Freeze(SemanticContentManifest manifest, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        token.ThrowIfCancellationRequested();
        if (manifest.SchemaVersion != 1 || manifest.Modality is not (SemanticContentModality.Audio or SemanticContentModality.Video)
            || manifest.Chunks is null || manifest.Chunks.Count != 0 || manifest.Embeddings is null || manifest.Embeddings.Count != 0
            || manifest.EmbeddingProfileId is not null || manifest.Segments is null || manifest.Segments.Count > MaxSegments)
            throw new ArgumentException("仅接受 schemaVersion=1 的音视频分段清单，不接受 chunk 或 embedding。", nameof(manifest));
        ValidateString(manifest.Id, required: true);
        ValidateString(manifest.Source);
        ValidateString(manifest.ContentHash, required: true);
        ValidateString(manifest.MimeType, required: true);
        ValidateString(manifest.IndexState?.LastError);
        ValidateReference(manifest.ObjectRef);
        long metadataCharacters = manifest.Id.Length + (long)manifest.ContentHash.Length + manifest.MimeType.Length
            + (manifest.Source?.Length ?? 0) + (manifest.IndexState?.LastError?.Length ?? 0)
            + ReferenceCharacters(manifest.ObjectRef!);
        string prefix = manifest.Modality == SemanticContentModality.Audio ? "audio/" : "video/";
        if (!manifest.MimeType.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("MIME 与媒体模态不匹配。", nameof(manifest));
        long characters = manifest.Text?.Length ?? 0;
        if (characters > MaxTextCharacters)
            throw new ArgumentException("媒体文本超过字符预算。", nameof(manifest));
        ValidateTextEncoding(manifest.Text);
        var segments = new SemanticContentSegment[manifest.Segments.Count];
        for (int index = 0; index < segments.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            SemanticContentSegment segment = manifest.Segments[index]
                ?? throw new ArgumentException("片段不能为 null。", nameof(manifest));
            ValidateString(segment.Id, required: true);
            ValidateString(segment.ContentHash);
            if (segment.KeyFrameRef is not null)
                ValidateReference(segment.KeyFrameRef);
            metadataCharacters += segment.Id.Length + (long)(segment.ContentHash?.Length ?? 0)
                + (segment.KeyFrameRef is null ? 0 : ReferenceCharacters(segment.KeyFrameRef));
            characters += segment.Text?.Length ?? 0;
            if (metadataCharacters > MaxMetadataCharacters || characters > MaxTextCharacters || segment.FrameIndex < 0
                || segment.FrameIndex.HasValue && segment.KeyFrameRef is null
                || string.IsNullOrWhiteSpace(segment.Text) && segment.KeyFrameRef is null
                || manifest.Modality == SemanticContentModality.Audio && segment.KeyFrameRef is not null)
                throw new ArgumentException("片段缺少派生内容、关键帧合同无效或文本超出预算。", nameof(manifest));
            ValidateTextEncoding(segment.Text);
            segments[index] = segment;
        }
        manifest = manifest with { Segments = segments };
        SemanticContentValidator.ValidateOrThrow(manifest);
        token.ThrowIfCancellationRequested();
        Array.Sort(segments, static (left, right) =>
        {
            int result = left.StartMs.CompareTo(right.StartMs);
            if (result == 0) result = left.Ordinal.CompareTo(right.Ordinal);
            return result == 0 ? StringComparer.Ordinal.Compare(left.Id, right.Id) : result;
        });
        token.ThrowIfCancellationRequested();
        return manifest;
    }

    private SndbObjectInfo Resolve(SemanticObjectReference reference, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        SndbObjectInfo? info = Head(reference);
        if (info is null || reference.VersionId is not null && reference.VersionId != info.VersionId
            || reference.ETag is not null && reference.ETag != info.ETag)
            throw new ArgumentException("媒体或关键帧对象不存在，或版本/ETag 已变化。");
        token.ThrowIfCancellationRequested();
        return info;
    }

    private bool IsCurrent(SemanticObjectReference reference, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        SndbObjectInfo? info = Head(reference);
        token.ThrowIfCancellationRequested();
        return info is not null && info.VersionId == reference.VersionId && info.ETag == reference.ETag;
    }

    private SndbObjectInfo? Head(SemanticObjectReference reference)
        => _objects.GetBucket(reference.Bucket) is null ? null : _objects.HeadObject(reference.Bucket, reference.Key);

    private static SemanticObjectReference Reference(SndbObjectInfo info)
        => new(info.Bucket, info.Key, info.VersionId, info.ETag);

    private static SemanticContentManifest Decode(ReadOnlySpan<byte> bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (bytes.Length is <= 0 or > MaxManifestBytes)
            throw new InvalidDataException("媒体清单必须非空且不超过 4 MiB。");
        try
        {
            return JsonSerializer.Deserialize(bytes, MediaSegmentJsonContext.Default.SemanticContentManifest)
                ?? throw new InvalidDataException("媒体清单不能为 null。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("媒体清单 JSON 无效。", exception);
        }
    }

    private static void ValidateReference(SemanticObjectReference? reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ValidateString(reference.Bucket, required: true);
        ValidateString(reference.Key, required: true);
        ValidateString(reference.VersionId);
        ValidateString(reference.ETag);
    }

    private static void ValidateString(string? value, bool required = false)
    {
        if (required && string.IsNullOrWhiteSpace(value) || value?.Length > 4096)
            throw new ArgumentException("标识/元数据不能为空且不得超过 4096 字符。");
        ValidateTextEncoding(value);
    }

    private static long ReferenceCharacters(SemanticObjectReference reference)
        => reference.Bucket.Length + (long)reference.Key.Length + (reference.VersionId?.Length ?? 0) + (reference.ETag?.Length ?? 0);

    private static void ValidateTextEncoding(string? value)
    {
        if (value is not null)
            _ = StrictUtf8.GetByteCount(value);
    }

    private static string Key(string contentId)
        => Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(contentId)));

    private sealed class BoundedManifestStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            CheckLength(count);
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            CheckLength(buffer.Length);
            base.Write(buffer);
        }

        private void CheckLength(int count)
        {
            if (count > MaxManifestBytes - Length)
                throw new ArgumentException("媒体清单序列化后超过 4 MiB。");
        }
    }

    private CancellationTokenSource Deadline(CancellationToken token)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_maxDuration);
        return deadline;
    }
}
