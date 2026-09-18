using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SonnetDB.Engine;
using SonnetDB.Kv;
using SonnetDB.ObjectStorage;
using SonnetDB.Vector.Primitives;

namespace SonnetDB.SemanticContent;

/// <summary>
/// 默认关闭的受治理人脸图库。仅处理外部预计算向量，使用同一 KV/WAL 原子发布模板变更与终态审计。
/// </summary>
/// <remarks>
/// 嵌入式宿主是信任边界，负责身份认证、外部模型治理、来源真实性和备份保留。
/// 不提供远程端点，不执行检测/推理，不声明真实身份准确率或物理介质擦除。
/// </remarks>
public sealed class FaceRecognitionStore
{
    private static readonly ConditionalWeakTable<Tsdb, SemaphoreSlim> Gates = new();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const int MaxTemplateBytes = 16 * 1024 * 1024;
    private readonly Tsdb _database;
    private readonly FaceRecognitionProfile _profile;
    private readonly FaceRecognitionOptions _options;
    private readonly IBiometricAuthorizer _authorizer;
    private readonly SemaphoreSlim _gate;
    private readonly string _prefix;
    private readonly byte[] _profileBytes;
    private readonly TimeProvider _clock;
    private SndbObjectStore? _objects;

    /// <summary>构造一个图库的可信嵌入式访问入口；构造本身不创建资源。</summary>
    /// <param name="database">生命周期由宿主管理的数据库。</param>
    /// <param name="gallery">用途隔离图库的稳定名称。</param>
    /// <param name="profile">不可变人脸与检测模型合同。</param>
    /// <param name="authorizer">由可信宿主注入的独立权限判定器。</param>
    /// <param name="options">门禁及有界处理配置，省略时默认关闭。</param>
    /// <param name="timeProvider">用于保留期判断的可信时钟；默认使用系统 UTC 时间。</param>
    public FaceRecognitionStore(Tsdb database, string gallery, FaceRecognitionProfile profile,
        IBiometricAuthorizer authorizer, FaceRecognitionOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(authorizer);
        ValidateIdentifier(gallery, nameof(gallery));
        if (profile.Embedding is null || profile.Detector is null || profile.Embedding.DataEgressPolicy is null)
            throw new ArgumentException("人脸 profile 和外发策略不能为空。", nameof(profile));
        profile = profile with
        {
            Embedding = profile.Embedding with { SupportedModalities = FreezeList(profile.Embedding.SupportedModalities, 6) },
            Detector = profile.Detector with { SupportedModalities = FreezeList(profile.Detector.SupportedModalities, 2) },
        };
        if (profile.Embedding is not null)
        {
            ValidateIdentifier(profile.Embedding.Id, nameof(profile));
            ValidateIdentifier(profile.Embedding.Provider, nameof(profile));
            ValidateIdentifier(profile.Embedding.Model, nameof(profile));
            ValidateIdentifier(profile.Embedding.Revision, nameof(profile));
            if (profile.Embedding.DataEgressPolicy?.Target is { } target)
                ValidateIdentifier(target, nameof(profile));
        }
        if (profile.Embedding is null || profile.Detector is null
            || profile.Embedding.SupportedModalities is null
            || profile.Embedding.SupportedModalities.Count > 6
            || profile.Detector.SupportedModalities is null
            || profile.Detector.SupportedModalities.Count > 2
            || SemanticContentValidator.ValidateProfile(profile.Embedding).Count != 0
            || !VisualContentValidator.ValidateProfile(profile.Detector).IsValid
            || profile.Embedding.Metric != KnnMetric.Cosine
            || profile.Embedding.Dimensions > 4096
            || !profile.Embedding.Supports(SemanticContentModality.Image))
            throw new ArgumentException("人脸 profile 必须是维度不超过 4096 的图片余弦向量空间。", nameof(profile));
        _options = options ?? new();
        if (_options.MaxTemplates is < 1 or > 10_000
            || _options.MaxDuration <= TimeSpan.Zero || _options.MaxDuration > TimeSpan.FromMinutes(1)
            || _options.MaxRetention <= TimeSpan.Zero || _options.MaxRetention > TimeSpan.FromDays(365)
            || _options.AllowedPurposes is null || _options.AllowedPurposes.Count > 100)
            throw new ArgumentException("人脸处理预算或用途配置无效。", nameof(options));
        _options = _options with { AllowedPurposes = FreezeList(_options.AllowedPurposes, 100) };
        foreach (string purpose in _options.AllowedPurposes)
            ValidateIdentifier(purpose, nameof(options));
        _profileBytes = JsonSerializer.SerializeToUtf8Bytes(profile, FaceRecognitionJsonContext.Default.FaceRecognitionProfile);
        if (_profileBytes.Length > 65_536)
            throw new ArgumentException("人脸 profile 超过 64 KiB。", nameof(profile));
        _profile = JsonSerializer.Deserialize(_profileBytes, FaceRecognitionJsonContext.Default.FaceRecognitionProfile)!;
        _database = database;
        _authorizer = authorizer;
        _clock = timeProvider ?? TimeProvider.System;
        _gate = Gates.GetValue(database, static _ => new SemaphoreSlim(1, 1));
        _prefix = Hash(gallery) + "/";
    }

    /// <summary>登记或替换同一用途内的模板；模板与成功审计原子提交。</summary>
    /// <param name="context">可信访问上下文。</param>
    /// <param name="template">外部生成、绑定固定原对象的派生模板。</param>
    /// <param name="cancellationToken">提交前取消令牌。</param>
    public void Enroll(BiometricAccessContext context, FaceRecognitionTemplate template, CancellationToken cancellationToken = default)
        => Execute(context, BiometricOperation.Enroll, (store, changes, token) =>
        {
            ArgumentNullException.ThrowIfNull(template);
            ValidateIdentifier(template.Id, nameof(template));
            ValidateIdentifier(template.SubjectId, nameof(template));
            if (template.Purpose != context.Purpose || template.ProfileId != _profile.Embedding.Id)
                throw new ArgumentException("模板用途或 profile 不匹配。", nameof(template));
            DateTimeOffset now = _clock.GetUtcNow();
            if (template.ExpiresUtc <= now || template.ExpiresUtc > now + _options.MaxRetention)
                throw new ArgumentException("模板必须具有允许保留期内的未来到期时间。", nameof(template));
            float[] vector = ValidateVector(template.Vector);
            ArgumentNullException.ThrowIfNull(template.Target);
            var validation = VisualContentValidator.ValidateTarget(template.Target, _profile.Detector, token);
            if (!validation.IsValid)
                throw new ArgumentException("人脸目标或固定原对象来源无效。", nameof(template));
            if (!IsCurrentTarget(template.Target))
                throw new InvalidOperationException("face_source_stale");
            byte[] encoded = JsonSerializer.SerializeToUtf8Bytes(template with { Vector = vector },
                FaceRecognitionJsonContext.Default.FaceRecognitionTemplate);
            if (encoded.Length > 262_144)
                throw new ArgumentException("单个人脸模板超过 256 KiB。", nameof(template));
            string key = TemplateKey(context.Purpose, template.Id);
            IReadOnlyList<KvEntry> records = ReadTemplates(store, context.Purpose, token);
            if (records.Count == _options.MaxTemplates && store.Get(key) is null)
                throw new InvalidOperationException("face_template_budget_exceeded");
            long resultingBytes = encoded.Length;
            byte[] templateKey = Encoding.UTF8.GetBytes(key);
            foreach (KvEntry row in records)
            {
                token.ThrowIfCancellationRequested();
                if (!row.Key.Span.SequenceEqual(templateKey))
                    resultingBytes += row.Value.Length;
            }
            if (resultingBytes > MaxTemplateBytes)
                throw new InvalidOperationException("face_template_byte_budget_exceeded");
            changes.Add(KvBatchMutation.Put(Encoding.UTF8.GetBytes(key), encoded));
            changes.Add(KvBatchMutation.Put(Encoding.UTF8.GetBytes(_prefix + "profile"), _profileBytes));
            return (true, 1);
        }, cancellationToken);

    /// <summary>对指定模板执行一对一阈值比较；不把命中视为已经评测的身份结论。</summary>
    /// <param name="context">可信访问上下文。</param>
    /// <param name="probe">同一 profile 的预计算查询向量。</param>
    /// <param name="templateId">明确指定的模板标识。</param>
    /// <param name="threshold">显式余弦阈值，范围 -1..1。</param>
    /// <param name="cancellationToken">提交前取消令牌。</param>
    /// <returns>阈值比较结果。</returns>
    public FaceVerificationResult Verify(BiometricAccessContext context, FaceRecognitionProbe probe,
        string templateId, double threshold, CancellationToken cancellationToken = default)
        => Execute(context, BiometricOperation.Verify, (store, _, token) =>
        {
            float[] vector = ValidateProbe(probe);
            ValidateThreshold(threshold);
            FaceRecognitionTemplate template = ReadVisible(store, context.Purpose, templateId);
            token.ThrowIfCancellationRequested();
            double similarity = Similarity(vector, template.Vector);
            return (new FaceVerificationResult
            {
                TemplateId = template.Id, Similarity = similarity, Threshold = threshold,
                IsMatch = similarity >= threshold,
            }, 1);
        }, cancellationToken);

    /// <summary>在用途内执行精确、有界一对多候选查询，按分数降序及模板 ID 稳定排序。</summary>
    /// <param name="context">可信访问上下文。</param>
    /// <param name="probe">同一 profile 的查询向量。</param>
    /// <param name="limit">最多返回候选数，范围 1..100。</param>
    /// <param name="threshold">显式最低相似度。</param>
    /// <param name="cancellationToken">协作取消令牌。</param>
    /// <returns>不含向量和原媒体路径的候选。</returns>
    public IReadOnlyList<FaceRecognitionCandidate> Search(BiometricAccessContext context, FaceRecognitionProbe probe,
        int limit, double threshold, CancellationToken cancellationToken = default)
        => Execute<IReadOnlyList<FaceRecognitionCandidate>>(context, BiometricOperation.Search, (store, _, token) =>
        {
            if (limit is < 1 or > 100)
                throw new ArgumentOutOfRangeException(nameof(limit));
            float[] vector = ValidateProbe(probe);
            ValidateThreshold(threshold);
            var candidates = new List<FaceRecognitionCandidate>();
            DateTimeOffset now = _clock.GetUtcNow();
            foreach (KvEntry row in ReadTemplates(store, context.Purpose, token))
            {
                token.ThrowIfCancellationRequested();
                FaceRecognitionTemplate template = Decode(row.Value.Span);
                ValidateStored(template, context.Purpose);
                if (template.ExpiresUtc <= now || !IsCurrentTarget(template.Target))
                    continue;
                double similarity = Similarity(vector, template.Vector);
                if (similarity >= threshold)
                    candidates.Add(new() { TemplateId = template.Id, SubjectId = template.SubjectId, Similarity = similarity });
            }
            FaceRecognitionCandidate[] result = candidates.OrderByDescending(x => x.Similarity)
                .ThenBy(x => x.TemplateId, StringComparer.Ordinal).Take(limit).ToArray();
            return (result, result.Length);
        }, cancellationToken);

    /// <summary>以独立导出权限读取模板；普通验证和搜索权限不能导出向量。</summary>
    /// <param name="context">可信访问上下文。</param>
    /// <param name="templateId">模板标识。</param>
    /// <param name="cancellationToken">协作取消令牌。</param>
    /// <returns>包含来源和向量的独立反序列化副本。</returns>
    public FaceRecognitionTemplate Export(BiometricAccessContext context, string templateId,
        CancellationToken cancellationToken = default)
        => Execute(context, BiometricOperation.Export, (store, _, _) =>
            (ReadVisible(store, context.Purpose, templateId), 1), cancellationToken);

    /// <summary>删除当前用途内指定主体的全部模板，禁用能力后仍可授权执行。</summary>
    /// <param name="context">可信访问上下文。</param>
    /// <param name="subjectId">主体标识。</param>
    /// <param name="cancellationToken">原子提交前的取消令牌。</param>
    /// <returns>实际删除数量。</returns>
    public int DeleteSubject(BiometricAccessContext context, string subjectId, CancellationToken cancellationToken = default)
    {
        ValidateIdentifier(subjectId, nameof(subjectId));
        return DeleteWhere(context, BiometricOperation.Delete, template => template.SubjectId == subjectId, cancellationToken);
    }

    /// <summary>删除用途内引用该固定原对象版本的全部模板，供对象删除对账显式调用。</summary>
    /// <param name="context">可信访问上下文。</param>
    /// <param name="source">精确原对象版本身份。</param>
    /// <param name="cancellationToken">原子提交前的取消令牌。</param>
    /// <returns>实际删除数量。</returns>
    public int DeleteSource(BiometricAccessContext context, SemanticObjectReference source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrWhiteSpace(source.Bucket) || string.IsNullOrWhiteSpace(source.Key)
            || source.Bucket.Length > 256 || source.Key.Length > 4096
            || (string.IsNullOrWhiteSpace(source.VersionId) && string.IsNullOrWhiteSpace(source.ETag)))
            throw new ArgumentException("删除来源必须绑定非空的 bucket、key 和版本或 ETag。", nameof(source));
        return DeleteWhere(context, BiometricOperation.Delete,
            template => SameFixedSource(template.Target.Source.ObjectRef, source), cancellationToken);
    }

    /// <summary>删除用途内所有到期模板；逻辑删除不等同于擦除历史备份或物理介质。</summary>
    /// <param name="context">可信访问上下文。</param>
    /// <param name="cancellationToken">原子提交前的取消令牌。</param>
    /// <returns>实际删除数量。</returns>
    public int ApplyRetention(BiometricAccessContext context, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        return DeleteWhere(context, BiometricOperation.ApplyRetention, template => template.ExpiresUtc <= now, cancellationToken);
    }

    /// <summary>读取当前用途最近的最多 200 条脱敏访问审计，审计保留 30 天。</summary>
    /// <param name="context">可信访问上下文。</param>
    /// <param name="limit">结果上限，范围 1..200。</param>
    /// <param name="cancellationToken">协作取消令牌。</param>
    /// <returns>开始时间倒序的脱敏审计，包括本次读取的 started 记录。</returns>
    public IReadOnlyList<FaceRecognitionAuditEntry> ReadAudit(BiometricAccessContext context, int limit = 100,
        CancellationToken cancellationToken = default)
        => Execute<IReadOnlyList<FaceRecognitionAuditEntry>>(context, BiometricOperation.ReadAudit, (store, _, token) =>
        {
            if (limit is < 1 or > 200)
                throw new ArgumentOutOfRangeException(nameof(limit));
            var result = new List<FaceRecognitionAuditEntry>();
            using KvReadSnapshot snapshot = store.AcquireReadSnapshot();
            using KvRangeCursor cursor = snapshot.OpenRangeCursor(new()
            {
                Prefix = Encoding.UTF8.GetBytes(AuditPrefix(context.Purpose)), PageSize = limit, MaxPageBytes = 262_144,
            });
            foreach (KvEntry entry in cursor.ReadNextPage(token))
            {
                token.ThrowIfCancellationRequested();
                result.Add(JsonSerializer.Deserialize(entry.Value.Span,
                    FaceRecognitionJsonContext.Default.FaceRecognitionAuditEntry)
                    ?? throw new InvalidDataException("人脸审计记录无效。"));
            }
            return (result, result.Count);
        }, cancellationToken);

    private int DeleteWhere(BiometricAccessContext context, BiometricOperation operation,
        Func<FaceRecognitionTemplate, bool> predicate, CancellationToken token)
        => Execute(context, operation, (store, changes, cancellation) =>
        {
            foreach (KvEntry row in ReadTemplates(store, context.Purpose, cancellation))
            {
                cancellation.ThrowIfCancellationRequested();
                FaceRecognitionTemplate template = Decode(row.Value.Span);
                if (template.Purpose != context.Purpose)
                    throw new InvalidDataException("人脸模板用途不匹配。" );
                if (predicate(template))
                    changes.Add(KvBatchMutation.Delete(row.Key.ToArray()));
            }
            return (changes.Count, changes.Count);
        }, token);

    private T Execute<T>(BiometricAccessContext context, BiometricOperation operation,
        Func<KvKeyspace, List<KvBatchMutation>, CancellationToken, (T Value, int Count)> action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ValidateIdentifier(context.Actor, nameof(context));
        ValidateIdentifier(context.Purpose, nameof(context));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.MaxDuration);
        CancellationToken token = deadline.Token;
        _gate.Wait(token);
        try
        {
            KvKeyspace store = _database.Keyspaces.Open(FaceReservedResourceNames.KeyspaceName);
            var audit = new FaceRecognitionAuditEntry
            {
                Id = Guid.NewGuid().ToString("N"), StartedUtc = _clock.GetUtcNow(),
                ActorHash = Hash(context.Actor), PurposeHash = Hash(context.Purpose),
                Operation = operation, Outcome = "started",
            };
            byte[] auditKey = Encoding.UTF8.GetBytes(AuditPrefix(context.Purpose)
                + (long.MaxValue - audit.StartedUtc.UtcTicks).ToString("D19", System.Globalization.CultureInfo.InvariantCulture)
                + "/" + audit.Id);
            // 初始审计失败时不执行授权器、比较、导出或变更。
            WriteAudit(store, auditKey, audit, token);
            bool commitStarted = false;
            try
            {
                bool housekeeping = operation is BiometricOperation.Delete or BiometricOperation.ApplyRetention or BiometricOperation.ReadAudit;
                if ((!_options.Enabled && !housekeeping)
                    || !_options.AllowedPurposes.Contains(context.Purpose, StringComparer.Ordinal)
                    || !_authorizer.IsAuthorized(context, "face", operation))
                    throw new UnauthorizedAccessException("face_access_denied");
                token.ThrowIfCancellationRequested();
                if (!housekeeping)
                {
                    byte[]? storedProfile = store.Get(_prefix + "profile");
                    if (storedProfile is not null && !storedProfile.AsSpan().SequenceEqual(_profileBytes))
                        throw new InvalidOperationException("face_profile_mismatch");
                }
                var changes = new List<KvBatchMutation>();
                (T value, int count) = action(store, changes, token);
                token.ThrowIfCancellationRequested();
                changes.Add(AuditMutation(auditKey, audit with { Outcome = "succeeded", AffectedCount = count }));
                commitStarted = true;
                store.ApplyConditionalBatch(changes, [], token);
                // WAL 已开始提交后不再因取消改报取消；fsync 失败不返回敏感结果。
                store.SyncWalForMaintenance();
                return value;
            }
            catch (Exception exception)
            {
                string outcome = commitStarted ? "unknown" : exception switch
                {
                    UnauthorizedAccessException => "denied",
                    OperationCanceledException => "cancelled",
                    _ => "failed",
                };
                WriteAudit(store, auditKey, audit with { Outcome = outcome }, CancellationToken.None);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private IReadOnlyList<KvEntry> ReadTemplates(KvKeyspace store, string purpose, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using KvReadSnapshot snapshot = store.AcquireReadSnapshot();
        using KvRangeCursor cursor = snapshot.OpenRangeCursor(new()
        {
            Prefix = Encoding.UTF8.GetBytes(_prefix + "t/" + Hash(purpose) + "/"),
            PageSize = 32, MaxPageBytes = 524_288,
        });
        var entries = new List<KvEntry>();
        long bytes = 0;
        for (int page = 0; page <= _options.MaxTemplates; page++)
        {
            token.ThrowIfCancellationRequested();
            IReadOnlyList<KvEntry> rows = cursor.ReadNextPage(token);
            if (rows.Count == 0)
                return entries;
            foreach (KvEntry row in rows)
            {
                bytes += row.Value.Length;
                if (row.Value.Length > 262_144 || bytes > MaxTemplateBytes)
                    throw new InvalidOperationException("face_template_byte_budget_exceeded");
                entries.Add(row);
                if (entries.Count > _options.MaxTemplates)
                    throw new InvalidOperationException("face_template_budget_exceeded");
            }
        }
        throw new InvalidOperationException("face_template_budget_exceeded");
    }

    private FaceRecognitionTemplate ReadVisible(KvKeyspace store, string purpose, string id)
    {
        ValidateIdentifier(id, nameof(id));
        byte[]? bytes = store.Get(TemplateKey(purpose, id));
        FaceRecognitionTemplate template = bytes is null ? throw new KeyNotFoundException("face_template_not_found") : Decode(bytes);
        ValidateStored(template, purpose);
        if (template.ExpiresUtc <= _clock.GetUtcNow() || !IsCurrentTarget(template.Target))
            throw new KeyNotFoundException("face_template_not_found");
        return template;
    }

    private void ValidateStored(FaceRecognitionTemplate template, string purpose)
    {
        if (template.Purpose != purpose || template.ProfileId != _profile.Embedding.Id)
            throw new InvalidDataException("人脸模板用途或 profile 不匹配。" );
        float[] normalized = ValidateVector(template.Vector);
        for (int i = 0; i < normalized.Length; i++)
            if (Math.Abs(normalized[i] - template.Vector[i]) > 0.00001f)
                throw new InvalidDataException("持久人脸向量未规范化。" );
    }

    private bool IsCurrent(VisualSourceReference source)
    {
        _objects ??= new SndbObjectStore(_database);
        SemanticObjectReference reference = source.ObjectRef;
        try
        {
            SndbObjectInfo? current = _objects.HeadObject(reference.Bucket, reference.Key);
            return current is not null
                && (reference.VersionId is null || current.VersionId == reference.VersionId)
                && (reference.ETag is null || current.ETag == reference.ETag)
                && string.Equals(current.Sha256, source.ContentHash, StringComparison.OrdinalIgnoreCase);
        }
        catch (SndbObjectStorageException exception) when (exception.Code == "bucket_not_found")
        {
            return false;
        }
    }

    private bool IsCurrentTarget(VisualDerivedTarget target)
    {
        if (target.IndexState.State != SemanticIndexState.Ready || !IsCurrent(target.Source))
            return false;
        if (target.CropObjectRef is not { } crop)
            return true;
        try
        {
            SndbObjectInfo? current = _objects!.HeadObject(crop.Bucket, crop.Key);
            return current is not null
                && (crop.VersionId is null || crop.VersionId == current.VersionId)
                && (crop.ETag is null || crop.ETag == current.ETag);
        }
        catch (SndbObjectStorageException exception) when (exception.Code == "bucket_not_found")
        {
            return false;
        }
    }

    private float[] ValidateProbe(FaceRecognitionProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        if (probe.ProfileId != _profile.Embedding.Id)
            throw new ArgumentException("查询 profile 不匹配。", nameof(probe));
        return ValidateVector(probe.Vector);
    }

    private float[] ValidateVector(float[] vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        if (vector.Length != _profile.Embedding.Dimensions)
            throw new ArgumentException("人脸向量维度不匹配。", nameof(vector));
        float[] frozen = vector.ToArray();
        double norm = 0;
        foreach (float value in frozen)
        {
            if (!float.IsFinite(value))
                throw new ArgumentException("人脸向量必须为有限值。", nameof(vector));
            norm += (double)value * value;
        }
        if (norm == 0)
            throw new ArgumentException("人脸向量不能为零向量。", nameof(vector));
        // 双精度归一化避免有限的大 float 经基础余弦内积产生溢出。
        double divisor = Math.Sqrt(norm);
        for (int i = 0; i < frozen.Length; i++)
            frozen[i] = (float)(frozen[i] / divisor);
        return frozen;
    }

    private static double Similarity(float[] left, float[] right)
    {
        double value = 1d - VectorDistance.ComputeCosine(left, right);
        if (!double.IsFinite(value))
            throw new InvalidDataException("人脸向量比较结果无效。" );
        return Math.Clamp(value, -1, 1);
    }

    private static FaceRecognitionTemplate Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 262_144)
            throw new InvalidDataException("人脸模板记录超过 256 KiB。" );
        return JsonSerializer.Deserialize(bytes, FaceRecognitionJsonContext.Default.FaceRecognitionTemplate)
            ?? throw new InvalidDataException("人脸模板记录无效。" );
    }

    private static void WriteAudit(KvKeyspace store, byte[] key, FaceRecognitionAuditEntry entry, CancellationToken token)
    {
        store.ApplyConditionalBatch([AuditMutation(key, entry)], [], token);
        store.SyncWalForMaintenance(token);
    }

    private static KvBatchMutation AuditMutation(byte[] key, FaceRecognitionAuditEntry entry)
        => KvBatchMutation.Put(key, JsonSerializer.SerializeToUtf8Bytes(entry,
            FaceRecognitionJsonContext.Default.FaceRecognitionAuditEntry), entry.StartedUtc.AddDays(30));

    private string TemplateKey(string purpose, string id) => _prefix + "t/" + Hash(purpose) + "/" + Hash(id);
    private string AuditPrefix(string purpose) => _prefix + "a/" + Hash(purpose) + "/";
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void ValidateIdentifier(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl))
            throw new ArgumentException("标识必须为不超过 256 字符的非空文本且不含控制字符。", parameter);
        _ = StrictUtf8.GetByteCount(value);
    }

    private static void ValidateThreshold(double threshold)
    {
        if (!double.IsFinite(threshold) || threshold is < -1 or > 1)
            throw new ArgumentOutOfRangeException(nameof(threshold));
    }

    private static bool SameFixedSource(SemanticObjectReference stored, SemanticObjectReference requested)
        => stored.Bucket == requested.Bucket && stored.Key == requested.Key
            && (stored.VersionId is not null && requested.VersionId is not null
                ? stored.VersionId == requested.VersionId
                : stored.ETag is not null && requested.ETag is not null && stored.ETag == requested.ETag);

    private static T[] FreezeList<T>(IReadOnlyList<T>? list, int maximum)
    {
        ArgumentNullException.ThrowIfNull(list);
        int count = list.Count;
        if (count < 0 || count > maximum)
            throw new ArgumentException("集合超过人脸合同预算。", nameof(list));
        var result = new T[count];
        for (int index = 0; index < count; index++)
            result[index] = list[index];
        return result;
    }
}
