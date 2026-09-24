using System.Runtime.CompilerServices;
using System.Text.Json;
using SonnetDB.Documents;
using SonnetDB.Engine;
using SonnetDB.ObjectStorage;
using SonnetDB.Vector.Primitives;

namespace SonnetDB.SemanticContent;

/// <summary>复用现有 Document/KV/WAL 的车辆观察 SDK；由宿主治理集合访问，不运行 OCR 或视觉模型。</summary>
public sealed class VehicleObservationStore
{
    private const string PlateIndex = "vehicle_plate_exact_v1";
    private const string AppearanceIndex = "vehicle_appearance_profile_v1";
    private const string OcrIndex = "vehicle_ocr_profile_v1";
    private const string DetectorIndex = "vehicle_detector_profile_v1";
    private const int MaxDocumentCharacters = 256 * 1024;
    private static readonly ConditionalWeakTable<DocumentCollectionStore, SemaphoreSlim> ImportGates = new();
    private readonly DocumentCollectionStore _store;
    private readonly SndbObjectStore _objects;

    /// <summary>在调用方指定的普通 Document 集合中打开车辆观察及精确索引。</summary>
    /// <param name="database">持有原对象、Document 和 WAL 的数据库。</param>
    /// <param name="collectionName">专用于车辆观察的集合；已有集合必须具备本合同的四个索引。</param>
    public VehicleObservationStore(Tsdb database, string collectionName)
    {
        ArgumentNullException.ThrowIfNull(database);
        VisualEmbeddingValidation.Text(collectionName, nameof(collectionName));
        if (database.Documents.Catalog.TryGet(collectionName) is null)
            database.Documents.Create(DocumentCollectionSchema.Create(collectionName,
                [new(PlateIndex, "$.plateKey", IsSparse: true), new(AppearanceIndex, "$.appearanceProfileId", IsSparse: true),
                    new(OcrIndex, "$.ocrProfileId", IsSparse: true), new(DetectorIndex, "$.detectorProfileId", IsSparse: true)]));
        _store = database.Documents.Open(collectionName);
        ValidateIndex(PlateIndex, "$.plateKey");
        ValidateIndex(AppearanceIndex, "$.appearanceProfileId");
        ValidateIndex(OcrIndex, "$.ocrProfileId");
        ValidateIndex(DetectorIndex, "$.detectorProfileId");
        _objects = new SndbObjectStore(database);
    }

    /// <summary>原子替换单个目标的全部派生观察及索引；验证当前对象版本与内容 hash。</summary>
    /// <param name="observation">外部 detector、外观模型、OCR 的输出。</param>
    /// <param name="cancellationToken">取消标记。</param><returns>冻结并补齐来源版本的观察。</returns>
    public VehicleObservation Import(VehicleObservation observation, CancellationToken cancellationToken = default)
    {
        using var deadline = Deadline(cancellationToken, TimeSpan.FromSeconds(30));
        CancellationToken token = deadline.Token;
        VehicleStoredObservation stored = Freeze(observation, token);
        SemaphoreSlim gate = ImportGates.GetValue(_store, static _ => new SemaphoreSlim(1, 1));
        gate.Wait(token);
        try
        {
            EnsureProfileCompatibility(stored, token);
            SndbObjectInfo source = Resolve(stored.Observation.Target.Source.ObjectRef, token);
            if (!string.Equals(source.Sha256, stored.Observation.Target.Source.ContentHash, StringComparison.OrdinalIgnoreCase)
                || !source.ContentType.StartsWith(stored.Observation.Target.Source.Modality == SemanticContentModality.Image ? "image/" : "video/", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("车辆目标的来源 hash/模态与原对象不一致。", nameof(observation));
            VisualDerivedTarget target = stored.Observation.Target with
            {
                Source = stored.Observation.Target.Source with { ObjectRef = Reference(source) },
            };
            if (target.CropObjectRef is not null)
            {
                SndbObjectInfo crop = Resolve(target.CropObjectRef, token);
                if (!crop.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("车辆裁剪必须是图片对象。");
                target = target with { CropObjectRef = Reference(crop) };
            }
            target = VisualEmbeddingValidation.FreezeTarget(target, stored.Observation.DetectorProfile, token);
            stored = stored with { Observation = stored.Observation with { Target = target } };
            string json = JsonSerializer.Serialize(stored, VehicleStoreJsonContext.Default.VehicleStoredObservation);
            if (json.Length > MaxDocumentCharacters) throw new ArgumentException("车辆观察超出 256 Ki 字符预算。");
            token.ThrowIfCancellationRequested();
            _store.Upsert(GetObservationId(target), json);
            return stored.Observation;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>通过规范化精确索引查询号码；不同签发地区以及 O/0 等号码永不合并。</summary>
    /// <param name="jurisdiction">签发地区。</param><param name="text">精确号码文本。</param>
    /// <param name="options">读取/结果/时间上界。</param><param name="cancellationToken">取消标记。</param>
    /// <returns>仅含当前来源的精确命中；向量不参与号码判断。</returns>
    public VehicleSearchResult FindPlate(string jurisdiction, string text, VehicleSearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        string key = VehiclePlateNormalization.CreateKey(jurisdiction, text);
        options ??= new();
        ValidateOptions(options);
        using var deadline = Deadline(cancellationToken, options.MaxDuration);
        CancellationToken token = deadline.Token;
        var hits = new List<VehicleSearchHit>(options.Limit);
        int scanned = 0, stale = 0;
        string? afterId = null;
        DocumentPathIndex index = ValidateIndex(PlateIndex, "$.plateKey");
        // 至多 MaxCandidates + 1 次读取；额外一行只用于检测超预算。
        for (int pageNumber = 0; pageNumber <= options.MaxCandidates; pageNumber++)
        {
            token.ThrowIfCancellationRequested();
            int take = Math.Min(128, options.MaxCandidates - scanned + 1);
            IReadOnlyList<DocumentRow> page = _store.GetByIndexAfter(index, key, afterId, take);
            if (page.Count == 0) break;
            for (int i = 0; i < page.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (++scanned > options.MaxCandidates) throw new InvalidOperationException("车牌查询超过候选预算。");
                VehicleStoredObservation stored = Read(page[i], token);
                if (stored.PlateKey != key) throw new InvalidDataException("车牌索引与文档不一致。");
                if (!IsCurrent(stored.Observation.Target, token)) { stale++; continue; }
                if (hits.Count == options.Limit) return new(hits.AsReadOnly(), true, scanned, stale);
                hits.Add(new(page[i].Id, stored.Observation));
            }
            afterId = page[^1].Id;
            if (page.Count < take) break;
        }
        token.ThrowIfCancellationRequested();
        return new(hits.AsReadOnly(), false, scanned, stale);
    }

    /// <summary>按车辆专属完整 profile 执行有界精确向量查询，不推断车牌号码。</summary>
    /// <param name="profile">查询的完整模型合同。</param><param name="embedding">外部模型生成的查询向量。</param>
    /// <param name="options">预算。</param><param name="cancellationToken">取消标记。</param><returns>按距离和目标 ID 排序的命中。</returns>
    public VehicleSearchResult SearchAppearance(VehicleAppearanceProfile profile, IReadOnlyList<float> embedding,
        VehicleSearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        profile = FreezeProfile(profile);
        options ??= new();
        ValidateOptions(options);
        using var deadline = Deadline(cancellationToken, options.MaxDuration);
        CancellationToken token = deadline.Token;
        float[] vector = VisualEmbeddingValidation.Vector(profile.Embedding, embedding, token);
        if (vector.Length > options.MaxVectorValues) throw new InvalidOperationException("车辆查询向量超过标量预算。");
        var hits = new List<VehicleSearchHit>(options.Limit);
        int scanned = 0, stale = 0;
        int fresh = 0;
        long values = vector.Length;
        string? afterId = null;
        DocumentPathIndex index = ValidateIndex(AppearanceIndex, "$.appearanceProfileId");
        for (int pageNumber = 0; pageNumber <= options.MaxCandidates; pageNumber++)
        {
            token.ThrowIfCancellationRequested();
            int take = Math.Min(128, options.MaxCandidates - scanned + 1);
            IReadOnlyList<DocumentRow> page = _store.GetByIndexAfter(index, profile.Embedding.Id, afterId, take);
            if (page.Count == 0) break;
            for (int i = 0; i < page.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (++scanned > options.MaxCandidates) throw new InvalidOperationException("车辆外观查询超过候选预算。");
                VehicleStoredObservation stored = Read(page[i], token);
                VehicleObservation observation = stored.Observation;
                if (observation.AppearanceProfile is null || observation.Embedding is null
                    || !EqualProfile(profile, observation.AppearanceProfile))
                    throw new InvalidDataException("车辆候选的完整 profile 与查询不匹配。");
                values += observation.Embedding.Count;
                if (values > options.MaxVectorValues) throw new InvalidOperationException("车辆外观查询超过向量标量预算。");
                if (!IsCurrent(observation.Target, token)) { stale++; continue; }
                float[] candidate = VisualEmbeddingValidation.Vector(profile.Embedding, observation.Embedding, token);
                float distance = VectorDistance.Compute(profile.Embedding.Metric, vector, candidate);
                if (!float.IsFinite(distance)) throw new InvalidDataException("车辆外观距离非有限值。");
                fresh++;
                RetainBest(hits, new(page[i].Id, observation, distance), options.Limit);
            }
            afterId = page[^1].Id;
            if (page.Count < take) break;
        }
        token.ThrowIfCancellationRequested();
        return new(hits.AsReadOnly(), fresh > hits.Count, scanned, stale);
    }

    /// <summary>删除单个观察和精确/外观索引条目，保留原对象。</summary>
    /// <param name="observationId">查询返回的复合观察 ID，或 GetObservationId 的返回值。</param>
    /// <param name="cancellationToken">取消标记。</param><returns>是否删除已有观察。</returns>
    public bool Delete(string observationId, CancellationToken cancellationToken = default)
    {
        VisualEmbeddingValidation.Text(observationId, nameof(observationId), 64);
        if (observationId.Length != 64 || observationId.Any(static c => c is not (>= '0' and <= '9') and not (>= 'A' and <= 'F')))
            throw new ArgumentException("观察 ID 必须是 GetObservationId 或查询返回的大写 SHA-256。", nameof(observationId));
        cancellationToken.ThrowIfCancellationRequested();
        return _store.Delete(observationId);
    }

    /// <summary>按导入或查询返回的完整版本目标删除观察；不会读取原对象，所以源已删除时仍可使用。</summary>
    /// <param name="target">导入/查询返回且含 versionId 与 ETag 的固定目标。</param>
    /// <param name="cancellationToken">取消标记。</param><returns>是否删除已有观察。</returns>
    public bool Delete(VisualDerivedTarget target, CancellationToken cancellationToken = default)
        => Delete(GetObservationId(target), cancellationToken);

    /// <summary>计算观察主键；不把仅在单个原对象版本内唯一的局部 target ID 当作集合主键。</summary>
    /// <param name="target">Import 或查询返回的目标；必须同时包含实际 versionId 和 ETag。</param>
    /// <returns>固定字段顺序 JSON 的 SHA-256：bucket、key、versionId、eTag、detectorProfileId、targetId。</returns>
    public static string GetObservationId(VisualDerivedTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(target.Source);
        ArgumentNullException.ThrowIfNull(target.Source.ObjectRef);
        VisualEmbeddingValidation.Text(target.Source.ObjectRef.Bucket, "source.bucket");
        VisualEmbeddingValidation.Text(target.Source.ObjectRef.Key, "source.key", 4096);
        VisualEmbeddingValidation.Text(target.Source.ObjectRef.VersionId, "source.versionId");
        VisualEmbeddingValidation.Text(target.Source.ObjectRef.ETag, "source.eTag");
        VisualEmbeddingValidation.Text(target.DetectorProfileId, nameof(target.DetectorProfileId));
        VisualEmbeddingValidation.Text(target.Id, nameof(target.Id));
        return VisualEmbeddingValidation.TargetIdentity(target);
    }

    private DocumentPathIndex ValidateIndex(string name, string path)
    {
        DocumentPathIndex? index = _store.Schema.TryGetIndex(name);
        if (index is null || index.Kind != DocumentIndexKind.Path || index.Paths.Count != 1
            || index.Paths[0] != path || index.IsUnique || !index.IsSparse || index.PartialFilter is not null
            || index.TtlPath is not null)
            throw new InvalidOperationException("车辆观察集合缺少兼容的专属精确索引。");
        return index;
    }

    private VehicleStoredObservation Read(DocumentRow row, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (row.Json.Length > MaxDocumentCharacters) throw new InvalidDataException("车辆文档超过读取预算。");
        try
        {
            VehicleStoredObservation stored = JsonSerializer.Deserialize(row.Json, VehicleStoreJsonContext.Default.VehicleStoredObservation)
                ?? throw new InvalidDataException("车辆文档为空。");
            VehicleStoredObservation canonical = Freeze(stored.Observation, token);
            if (GetObservationId(canonical.Observation.Target) != row.Id || canonical.PlateKey != stored.PlateKey
                || canonical.AppearanceProfileId != stored.AppearanceProfileId || canonical.OcrProfileId != stored.OcrProfileId
                || canonical.DetectorProfileId != stored.DetectorProfileId)
                throw new InvalidDataException("车辆文档的标识或派生精确键不一致。");
            return canonical;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            throw new InvalidDataException("持久车辆观察违反合同。", exception);
        }
    }

    private static VehicleStoredObservation Freeze(VehicleObservation observation, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(observation);
        token.ThrowIfCancellationRequested();
        VisualDetectorProfile detector = VisualEmbeddingValidation.FreezeDetector(observation.DetectorProfile);
        VisualDerivedTarget target = VisualEmbeddingValidation.FreezeTarget(observation.Target, detector, token);
        observation = observation with { Target = target, DetectorProfile = detector };
        if (observation.Target.IndexState.State != SemanticIndexState.Ready)
            throw new ArgumentException("仅接受已完成的车辆派生目标。");
        VehicleAppearanceProfile? profile = observation.AppearanceProfile is null ? null : FreezeProfile(observation.AppearanceProfile);
        if (profile is not null && !profile.Embedding.Supports(target.Source.Modality))
            throw new ArgumentException("车辆来源模态与外观 profile 不一致。");
        float[]? vector = profile is null ? null : VisualEmbeddingValidation.Vector(profile.Embedding,
            observation.Embedding ?? throw new ArgumentException("缺少车辆外观向量。"), token);
        if (profile is null && observation.Embedding is not null || profile is null && observation.Plate is null)
            throw new ArgumentException("车辆观察至少需要车牌或完整的车辆外观向量。");
        string? key = null;
        if (observation.Plate is { } plate)
        {
            if (!double.IsFinite(plate.Confidence) || plate.Confidence is < 0 or > 1)
                throw new ArgumentException("OCR confidence 必须在 0..1。");
            ArgumentNullException.ThrowIfNull(plate.Profile);
            VisualEmbeddingValidation.Text(plate.Profile.Id, "ocr.id");
            VisualEmbeddingValidation.Text(plate.Profile.Provider, "ocr.provider");
            VisualEmbeddingValidation.Text(plate.Profile.Model, "ocr.model");
            VisualEmbeddingValidation.Text(plate.Profile.Revision, "ocr.revision");
            VisualEmbeddingValidation.Text(plate.Profile.PreprocessingRevision, "ocr.preprocessingRevision");
            key = VehiclePlateNormalization.CreateKey(plate.Jurisdiction, plate.Text);
        }
        return new(observation with { AppearanceProfile = profile, Embedding = vector is null ? null : Array.AsReadOnly(vector) },
            key, profile?.Embedding.Id, observation.Plate?.Profile.Id, detector.Id);
    }

    private void EnsureProfileCompatibility(VehicleStoredObservation proposed, CancellationToken token)
    {
        VehicleStoredObservation? Existing(string indexName, string path, string id)
        {
            token.ThrowIfCancellationRequested();
            IReadOnlyList<DocumentRow> matches = _store.GetByIndex(ValidateIndex(indexName, path), id, 1);
            return matches.Count == 0 ? null : Read(matches[0], token);
        }
        if (proposed.AppearanceProfileId is { } appearanceId
            && Existing(AppearanceIndex, "$.appearanceProfileId", appearanceId) is { } appearance
            && (appearance.Observation.AppearanceProfile is null
                || !EqualProfile(proposed.Observation.AppearanceProfile!, appearance.Observation.AppearanceProfile)))
            throw new ArgumentException("同一车辆外观 profile ID 不得改变完整模型合同。");
        if (proposed.OcrProfileId is { } ocrId && Existing(OcrIndex, "$.ocrProfileId", ocrId) is { } ocr
            && proposed.Observation.Plate!.Profile != ocr.Observation.Plate?.Profile)
            throw new ArgumentException("同一 OCR profile ID 不得改变完整模型合同。");
        if (Existing(DetectorIndex, "$.detectorProfileId", proposed.DetectorProfileId) is { } detector
            && !proposed.Observation.DetectorProfile.IsCompatibleWith(detector.Observation.DetectorProfile))
            throw new ArgumentException("同一 detector profile ID 不得改变完整模型合同。");
    }

    private static void RetainBest(List<VehicleSearchHit> hits, VehicleSearchHit hit, int limit)
    {
        int position = hits.BinarySearch(hit, VehicleHitComparer.Instance);
        if (position < 0) position = ~position;
        if (position >= limit) return;
        hits.Insert(position, hit);
        if (hits.Count > limit) hits.RemoveAt(limit);
    }

    private sealed class VehicleHitComparer : IComparer<VehicleSearchHit>
    {
        internal static readonly VehicleHitComparer Instance = new();
        public int Compare(VehicleSearchHit? left, VehicleSearchHit? right)
        {
            int distance = Nullable.Compare(left?.Distance, right?.Distance);
            if (distance != 0) return distance;
            int localId = StringComparer.Ordinal.Compare(left?.Observation.Target.Id, right?.Observation.Target.Id);
            return localId != 0 ? localId : StringComparer.Ordinal.Compare(left?.ObservationId, right?.ObservationId);
        }
    }

    private static VehicleAppearanceProfile FreezeProfile(VehicleAppearanceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        VisualEmbeddingValidation.Text(profile.InputLayoutRevision, nameof(profile.InputLayoutRevision));
        return profile with { Embedding = VisualEmbeddingValidation.Freeze(profile.Embedding) };
    }

    private static bool EqualProfile(VehicleAppearanceProfile left, VehicleAppearanceProfile right)
        => left.InputLayoutRevision == right.InputLayoutRevision && VisualEmbeddingValidation.Equal(left.Embedding, right.Embedding);

    private SndbObjectInfo Resolve(SemanticObjectReference reference, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        SndbObjectInfo? info = Head(reference);
        if (info is null || reference.VersionId is not null && reference.VersionId != info.VersionId
            || reference.ETag is not null && reference.ETag != info.ETag)
            throw new ArgumentException("原对象/裁剪不存在或固定版本已失效。");
        return info;
    }

    private bool IsCurrent(VisualDerivedTarget target, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Current(target.Source.ObjectRef) && (target.CropObjectRef is null || Current(target.CropObjectRef));
    }

    private bool Current(SemanticObjectReference reference)
    {
        SndbObjectInfo? info = Head(reference);
        return info is not null && info.VersionId == reference.VersionId && info.ETag == reference.ETag;
    }

    private SndbObjectInfo? Head(SemanticObjectReference reference)
        => _objects.GetBucket(reference.Bucket) is null ? null : _objects.HeadObject(reference.Bucket, reference.Key);

    private static SemanticObjectReference Reference(SndbObjectInfo info) => new(info.Bucket, info.Key, info.VersionId, info.ETag);

    private static void ValidateOptions(VehicleSearchOptions options)
    {
        if (options.Limit is < 1 or > 1000 || options.MaxCandidates is < 1 or > 10000
            || options.MaxVectorValues is < 1 or > 10_000_000
            || options.MaxDuration <= TimeSpan.Zero || options.MaxDuration > TimeSpan.FromMinutes(1))
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    private static CancellationTokenSource Deadline(CancellationToken token, TimeSpan duration)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(duration);
        return deadline;
    }
}
