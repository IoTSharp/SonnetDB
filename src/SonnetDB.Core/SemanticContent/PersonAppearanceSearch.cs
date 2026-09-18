using SonnetDB.Vector.Primitives;

namespace SonnetDB.SemanticContent;

/// <summary>面向已授权预计算候选的有界精确相似查询；不持久化、推断或登记人员身份。</summary>
public sealed class PersonAppearanceSearch
{
    private readonly PersonAppearanceProfile _profile;
    private readonly PersonAppearanceSearchOptions _options;
    private readonly IBiometricAuthorizer _authorizer;
    private readonly Action<PersonAppearanceAudit> _audit;
    private readonly Func<VisualDerivedTarget, CancellationToken, bool> _isCurrent;
    private readonly HashSet<string> _purposes;
    private readonly TimeProvider _time;
    private readonly string _capability;

    /// <summary>配置一项默认关闭的能力；宿主负责可信授权、持久审计和对象新鲜度验证。</summary>
    /// <param name="profile">完整专业 profile。</param><param name="authorizer">可信能力授权器。</param>
    /// <param name="audit">同步持久审计；抛错时禁止交付结果。</param>
    /// <param name="isCurrent">检查目标原对象/裁剪版本仍可见且新鲜。</param>
    /// <param name="options">单项能力的显式启用、用途及预算。</param><param name="timeProvider">可选时钟。</param>
    public PersonAppearanceSearch(PersonAppearanceProfile profile, IBiometricAuthorizer authorizer,
        Action<PersonAppearanceAudit> audit, Func<VisualDerivedTarget, CancellationToken, bool> isCurrent,
        PersonAppearanceSearchOptions? options = null, TimeProvider? timeProvider = null)
    {
        _profile = FreezeProfile(profile);
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(isCurrent);
        _authorizer = authorizer;
        _audit = audit;
        _isCurrent = isCurrent;
        _time = timeProvider ?? TimeProvider.System;
        _options = options ?? new();
        int purposeCount = _options.AllowedPurposes?.Count ?? -1;
        if (_options.MaxCandidates is < 1 or > 10_000 || _options.MaxVectorValues is < 1 or > 10_000_000
            || _options.MaxDuration <= TimeSpan.Zero || _options.MaxDuration > TimeSpan.FromMinutes(1)
            || purposeCount is < 0 or > 32)
            throw new ArgumentException("人员查询预算或用途集合无效。", nameof(options));
        _purposes = new(StringComparer.Ordinal);
        for (int i = 0; i < purposeCount; i++)
        {
            string purpose = _options.AllowedPurposes![i];
            VisualEmbeddingValidation.Text(purpose, "purpose");
            _purposes.Add(purpose);
        }
        _capability = _profile.Task switch
        {
            PersonAppearanceTask.ReIdentification => "reid",
            PersonAppearanceTask.Gait => "gait",
            PersonAppearanceTask.Pose => "pose",
            _ => "action",
        };
    }

    /// <summary>对有限候选执行精确距离查询，校验 profile、保留期限、来源和审计后交付结果。</summary>
    /// <param name="access">调用者及用途。</param><param name="query">与配置相同 profile 的查询向量。</param>
    /// <param name="candidates">已授权候选；不得输入无界枚举。</param><param name="topK">结果上界，1..1000。</param>
    /// <param name="cancellationToken">取消标记。</param><returns>按距离、目标 ID 稳定排序的相似结果。</returns>
    public IReadOnlyList<PersonAppearanceHit> Search(BiometricAccessContext access, IReadOnlyList<float> query,
        IReadOnlyList<PersonAppearanceCandidate> candidates, int topK = 10, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        VisualEmbeddingValidation.Text(access.Actor, nameof(access.Actor));
        VisualEmbeddingValidation.Text(access.Purpose, nameof(access.Purpose));
        string operation = Guid.NewGuid().ToString("N");
        bool authorized;
        try
        {
            authorized = _options.Enabled && _purposes.Contains(access.Purpose)
                && _authorizer.IsAuthorized(access, _capability, BiometricOperation.Search);
        }
        catch
        {
            _audit(new(operation, access.Actor, access.Purpose, _capability, "denied", 0, 0, _time.GetUtcNow()));
            throw;
        }
        if (!authorized)
        {
            _audit(new(operation, access.Actor, access.Purpose, _capability, "denied", 0, 0, _time.GetUtcNow()));
            throw new UnauthorizedAccessException("此专业能力未启用或未授权指定用途。");
        }
        if (topK is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(topK));
        ArgumentNullException.ThrowIfNull(candidates);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_options.MaxDuration);
        CancellationToken token = deadline.Token;
        token.ThrowIfCancellationRequested();
        _audit(new(operation, access.Actor, access.Purpose, _capability, "started", 0, 0, _time.GetUtcNow()));
        int checkedCount = 0;
        try
        {
            int count = candidates.Count;
            if (count < 0 || count > _options.MaxCandidates
                || ((long)count + 1) * _profile.Embedding.Dimensions > _options.MaxVectorValues)
                throw new InvalidOperationException("人员查询超过候选或向量标量预算。");
            float[] vector = VisualEmbeddingValidation.Vector(_profile.Embedding, query, token);
            var hits = new List<PersonAppearanceHit>(count);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var frozen = new PersonAppearanceCandidate[count];
            DateTimeOffset now = _time.GetUtcNow();
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                PersonAppearanceCandidate candidate = candidates[i] ?? throw new ArgumentException("候选为空。");
                PersonAppearanceProfile profile = FreezeProfile(candidate.Profile);
                if (!EqualProfile(profile, _profile) || candidate.Purpose != access.Purpose)
                    throw new ArgumentException("人员候选必须使用相同用途和完整 profile。");
                VisualDetectorProfile detector = VisualEmbeddingValidation.FreezeDetector(candidate.DetectorProfile);
                VisualDerivedTarget target = VisualEmbeddingValidation.FreezeTarget(candidate.Target, detector, token);
                if (!profile.Embedding.Supports(target.Source.Modality) || !ids.Add(VisualEmbeddingValidation.TargetIdentity(target)))
                    throw new ArgumentException("人员候选来源模态无效或目标 ID 重复。");
                if (profile.Task is PersonAppearanceTask.Gait or PersonAppearanceTask.Action
                    && target.Source.Modality != SemanticContentModality.Video)
                    throw new ArgumentException("步态/动作候选必须来自视频。");
                checkedCount++;
                float[] candidateVector = VisualEmbeddingValidation.Vector(profile.Embedding, candidate.Embedding, token);
                frozen[i] = candidate with { Target = target, DetectorProfile = detector, Profile = profile, Embedding = Array.AsReadOnly(candidateVector) };
            }
            for (int i = 0; i < frozen.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                PersonAppearanceCandidate candidate = frozen[i];
                if (candidate.ExpiresAtUtc <= now || candidate.Target.IndexState.State != SemanticIndexState.Ready
                    || !_isCurrent(candidate.Target, token)) continue;
                float distance = VectorDistance.Compute(_profile.Embedding.Metric, vector, candidate.Embedding.ToArray());
                if (!float.IsFinite(distance)) throw new InvalidDataException("人员距离非有限值。");
                hits.Add(new(VisualEmbeddingValidation.TargetIdentity(candidate.Target), candidate.Target, distance));
            }
            PersonAppearanceHit[] result = hits.OrderBy(static h => h.Distance)
                .ThenBy(static h => h.Target.Id, StringComparer.Ordinal)
                .ThenBy(static h => h.CandidateId, StringComparer.Ordinal).Take(topK).ToArray();
            token.ThrowIfCancellationRequested();
            _audit(new(operation, access.Actor, access.Purpose, _capability, "succeeded", checkedCount, result.Length, _time.GetUtcNow()));
            token.ThrowIfCancellationRequested();
            return Array.AsReadOnly(result);
        }
        catch
        {
            _audit(new(operation, access.Actor, access.Purpose, _capability, "failed", checkedCount, 0, _time.GetUtcNow()));
            throw;
        }
    }

    internal static PersonAppearanceProfile FreezeProfile(PersonAppearanceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!Enum.IsDefined(profile.Task) || profile.WindowMilliseconds is < 0 or > 600_000)
            throw new ArgumentException("专业用途或时序窗口无效。");
        VisualEmbeddingValidation.Text(profile.InputLayoutRevision, nameof(profile.InputLayoutRevision));
        EmbeddingProfile embedding = VisualEmbeddingValidation.Freeze(profile.Embedding);
        if (profile.Task is PersonAppearanceTask.Gait or PersonAppearanceTask.Action
            && (profile.WindowMilliseconds == 0 || !embedding.Supports(SemanticContentModality.Video)))
            throw new ArgumentException("步态/动作必须声明视频输入和有限时序窗口。");
        return profile with { Embedding = embedding };
    }

    private static bool EqualProfile(PersonAppearanceProfile left, PersonAppearanceProfile right)
        => left.Task == right.Task && left.InputLayoutRevision == right.InputLayoutRevision
            && left.WindowMilliseconds == right.WindowMilliseconds
            && VisualEmbeddingValidation.Equal(left.Embedding, right.Embedding);
}
