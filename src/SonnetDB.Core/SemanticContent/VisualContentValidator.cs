using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace SonnetDB.SemanticContent;

/// <summary>有界视觉合同校验器；只校验元数据，不读取对象、运行模型或推断真实身份。</summary>
public static class VisualContentValidator
{
    /// <summary>校验完整快照，包括唯一来源、profile、轨迹引用与观察顺序。</summary>
    /// <param name="manifest">单一原对象版本的派生快照。</param>
    /// <param name="cancellationToken">取消令牌；校验同时受五秒协作时间上限约束。</param>
    /// <returns>最多包含 256 条失败的结构化结果。</returns>
    public static SemanticContentValidationResult Validate(
        VisualDerivationManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var validation = new Validation(cancellationToken);
        validation.Check();
        if (manifest.SchemaVersion != 1)
            validation.Fail("schemaVersion", "version", "只支持 schemaVersion 1。");
        validation.Text(manifest.Id, "id");
        ValidateSource(manifest.Source, "source", validation);
        ValidateState(manifest.IndexState, "indexState", validation);

        if (!validation.Count(manifest.DetectorProfiles, 64, "detectorProfiles", out int profileCount)
            | !validation.Count(manifest.Targets, 4096, "targets", out int targetCount)
            | !validation.Count(manifest.Tracks, 512, "tracks", out int trackCount))
            return validation.Result();
        if (profileCount == 0)
            validation.Fail("detectorProfiles", "required", "至少声明一个检测 profile，即使未检出目标。");

        var profiles = new Dictionary<string, VisualDetectorProfile>(StringComparer.Ordinal);
        for (int i = 0; i < profileCount; i++)
        {
            validation.Check();
            VisualDetectorProfile? profile = manifest.DetectorProfiles[i];
            string path = $"detectorProfiles[{i}]";
            ValidateProfile(profile, path, validation);
            if (profile is null || !validation.Identifier(profile.Id))
                continue;
            if (!profiles.TryAdd(profile.Id, profile))
                validation.Fail(path + ".id", "unique", "检测 profile 标识不能重复。");
            if (manifest.Source is not null && !Supports(profile, manifest.Source.Modality))
                validation.Fail(path + ".supportedModalities", "modality", "检测 profile 不支持原对象模态。");
        }

        var targets = new Dictionary<string, VisualDerivedTarget>(StringComparer.Ordinal);
        for (int i = 0; i < targetCount; i++)
        {
            validation.Check();
            VisualDerivedTarget? target = manifest.Targets[i];
            string path = $"targets[{i}]";
            VisualDetectorProfile? profile = null;
            if (target is not null && validation.Identifier(target.DetectorProfileId))
                profiles.TryGetValue(target.DetectorProfileId, out profile);
            ValidateTarget(target, profile, path, validation);
            if (target is null)
                continue;
            if (target.Source != manifest.Source)
                validation.Fail(path + ".source", "source", "目标必须绑定快照的同一原对象版本与画面合同。");
            if (validation.Identifier(target.Id) && !targets.TryAdd(target.Id, target))
                validation.Fail(path + ".id", "unique", "目标标识不能重复。");
        }

        var tracks = new HashSet<string>(StringComparer.Ordinal);
        var linkedTargets = new HashSet<string>(StringComparer.Ordinal);
        int referenceCount = 0;
        for (int i = 0; i < trackCount; i++)
        {
            validation.Check();
            VisualTrack? track = manifest.Tracks[i];
            string path = $"tracks[{i}]";
            if (track is null)
            {
                validation.Fail(path, "required", "轨迹不能为 null。");
                continue;
            }
            validation.Text(track.Id, path + ".id");
            validation.Text(track.Label, path + ".label");
            validation.Text(track.DetectorProfileId, path + ".detectorProfileId");
            ValidateSource(track.Source, path + ".source", validation);
            if (validation.Identifier(track.Id) && !tracks.Add(track.Id))
                validation.Fail(path + ".id", "unique", "轨迹标识不能重复。");
            if (track.Source != manifest.Source)
                validation.Fail(path + ".source", "source", "轨迹必须绑定快照的同一原视频版本。");
            if (track.Source?.Modality != SemanticContentModality.Video)
                validation.Fail(path + ".source.modality", "modality", "轨迹只允许绑定视频。");
            if (!validation.Identifier(track.DetectorProfileId) || !profiles.ContainsKey(track.DetectorProfileId))
                validation.Fail(path + ".detectorProfileId", "reference", "轨迹引用的检测 profile 不存在。");
            if (track.StartMs < 0 || track.EndMs <= track.StartMs || track.Source?.DurationMs is not { } duration || track.EndMs > duration)
                validation.Fail(path, "range", "轨迹必须满足 0 <= startMs < endMs <= 视频总时长。");
            if (!validation.Count(track.TargetIds, 4096, path + ".targetIds", out int trackTargetCount))
                continue;
            referenceCount += trackTargetCount;
            if (referenceCount > 4096)
            {
                validation.Fail("tracks", "budget", "所有轨迹合计最多引用 4096 个目标。");
                return validation.Result();
            }
            if (trackTargetCount == 0)
                validation.Fail(path + ".targetIds", "required", "轨迹至少引用一个目标。");
            long previousTime = -1;
            long? previousFrame = null;
            for (int j = 0; j < trackTargetCount; j++)
            {
                validation.Check();
                string? id = track.TargetIds[j];
                string referencePath = $"{path}.targetIds[{j}]";
                if (!validation.Identifier(id) || !targets.TryGetValue(id, out VisualDerivedTarget? target))
                {
                    validation.Fail(referencePath, "reference", "轨迹引用的目标不存在。");
                    continue;
                }
                if (!linkedTargets.Add(id))
                    validation.Fail(referencePath, "unique", "目标只能被一个轨迹引用一次。");
                if (target.TrackId != track.Id || target.DetectorProfileId != track.DetectorProfileId || target.Label != track.Label)
                    validation.Fail(referencePath, "binding", "目标与轨迹的 trackId、profile 和标签必须一致。");
                if (target.TimestampMs is not { } timestamp || timestamp < track.StartMs || timestamp >= track.EndMs)
                    validation.Fail(referencePath, "range", "目标时间必须位于轨迹的半开时间区间。");
                else
                {
                    if (timestamp <= previousTime)
                        validation.Fail(referencePath, "ordering", "轨迹目标时间必须严格递增。");
                    previousTime = timestamp;
                }
                if (target.FrameIndex is { } frame)
                {
                    if (previousFrame is { } prior && frame <= prior)
                        validation.Fail(referencePath, "ordering", "已声明的帧序号必须严格递增。");
                    previousFrame = frame;
                }
            }
        }
        for (int i = 0; i < targetCount; i++)
        {
            validation.Check();
            VisualDerivedTarget? target = manifest.Targets[i];
            if (target?.TrackId is not null && (!tracks.Contains(target.TrackId) || !linkedTargets.Contains(target.Id)))
                validation.Fail($"targets[{i}].trackId", "reference", "声明 trackId 的目标必须被对应轨迹引用。");
        }
        return validation.Result();
    }

    /// <summary>校验完整快照，失败时抛出含首条结构化路径的参数异常。</summary>
    /// <param name="manifest">待校验的快照。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    public static void ValidateOrThrow(VisualDerivationManifest manifest, CancellationToken cancellationToken = default)
    {
        SemanticContentValidationResult result = Validate(manifest, cancellationToken);
        if (!result.IsValid)
        {
            SemanticContentValidationFailure failure = result.Failures[0];
            throw new ArgumentException($"[{failure.Path}] {failure.Rule}: {failure.Message}", nameof(manifest));
        }
    }

    /// <summary>校验独立目标及其完整 profile；轨迹引用的完整性由快照校验负责。</summary>
    /// <param name="target">待校验的派生目标。</param>
    /// <param name="profile">产生此目标的完整检测 profile。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>结构化校验结果。</returns>
    public static SemanticContentValidationResult ValidateTarget(
        VisualDerivedTarget target,
        VisualDetectorProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(profile);
        var validation = new Validation(cancellationToken);
        ValidateProfile(profile, "profile", validation);
        ValidateTarget(target, profile, "target", validation);
        return validation.Result();
    }

    /// <summary>校验检测器声明，包括固定版本、输入模态及外发策略。</summary>
    /// <param name="profile">待校验的检测 profile。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>结构化校验结果。</returns>
    public static SemanticContentValidationResult ValidateProfile(
        VisualDetectorProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var validation = new Validation(cancellationToken);
        ValidateProfile(profile, "profile", validation);
        return validation.Result();
    }

    private static void ValidateProfile(VisualDetectorProfile? profile, string path, Validation validation)
    {
        validation.Check();
        if (profile is null)
        {
            validation.Fail(path, "required", "检测 profile 不能为 null。");
            return;
        }
        validation.Text(profile.Id, path + ".id");
        validation.Text(profile.Provider, path + ".provider");
        validation.Text(profile.Model, path + ".model");
        validation.Text(profile.Revision, path + ".revision");
        validation.Text(profile.PreprocessingRevision, path + ".preprocessingRevision");
        validation.Text(profile.LabelSetRevision, path + ".labelSetRevision");
        if (validation.Count(profile.SupportedModalities, 2, path + ".supportedModalities", out int modalityCount))
        {
            if (modalityCount == 0)
                validation.Fail(path + ".supportedModalities", "required", "至少声明一个视觉模态。");
            for (int i = 0; i < modalityCount; i++)
            {
                validation.Check();
                SemanticContentModality modality = profile.SupportedModalities[i];
                if (modality is not (SemanticContentModality.Image or SemanticContentModality.Video))
                    validation.Fail(path + ".supportedModalities", "modality", "检测器只支持 Image 或 Video。");
                if (i > 0 && modality == profile.SupportedModalities[0])
                    validation.Fail(path + ".supportedModalities", "unique", "支持的模态不能重复。");
            }
        }
        SemanticDataEgressPolicy? policy = profile.DataEgressPolicy;
        if (policy is null)
            validation.Fail(path + ".dataEgressPolicy", "required", "必须提供内容外发策略。");
        else
        {
            if (!Enum.IsDefined(policy.Mode))
                validation.Fail(path + ".dataEgressPolicy.mode", "enum", "外发模式非法。");
            if (!policy.AuditRequired)
                validation.Fail(path + ".dataEgressPolicy.auditRequired", "audit", "视觉检测要求调用方保留审计。");
            if (policy.Mode == SemanticDataEgressMode.LocalOnly)
            {
                if (policy.Target is not null)
                    validation.Fail(path + ".dataEgressPolicy.target", "egress", "本地处理策略不能携带外发目标。");
            }
            else
                validation.Text(policy.Target, path + ".dataEgressPolicy.target", 2048);
        }
    }

    private static void ValidateTarget(VisualDerivedTarget? target, VisualDetectorProfile? profile, string path, Validation validation)
    {
        validation.Check();
        if (target is null)
        {
            validation.Fail(path, "required", "目标不能为 null。");
            return;
        }
        validation.Text(target.Id, path + ".id");
        validation.Text(target.Label, path + ".label");
        validation.Text(target.DetectorProfileId, path + ".detectorProfileId");
        ValidateSource(target.Source, path + ".source", validation);
        ValidateState(target.IndexState, path + ".indexState", validation);
        if (profile is null || profile.Id != target.DetectorProfileId)
            validation.Fail(path + ".detectorProfileId", "reference", "目标必须引用提供的检测 profile。");
        else if (target.Source is not null && !Supports(profile, target.Source.Modality))
            validation.Fail(path + ".detectorProfileId", "modality", "检测 profile 不支持目标的原对象模态。");
        if (!double.IsFinite(target.Confidence) || target.Confidence is < 0 or > 1)
            validation.Fail(path + ".confidence", "range", "置信度必须为 0 到 1 的有限数。");
        VisualRegion? region = target.Region;
        if (region is null)
            validation.Fail(path + ".region", "required", "目标必须提供原图区域。");
        else if (!double.IsFinite(region.X) || !double.IsFinite(region.Y)
            || !double.IsFinite(region.Width) || !double.IsFinite(region.Height)
            || region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0
            || region.X + region.Width > 1 || region.Y + region.Height > 1)
            validation.Fail(path + ".region", "range", "矩形必须为原图内非空的有限归一化区域。");

        if (target.Source?.Modality == SemanticContentModality.Image)
        {
            if (target.TimestampMs is not null || target.FrameIndex is not null || target.TrackId is not null)
                validation.Fail(path, "modality", "图片目标不能声明时间、帧或轨迹。");
        }
        else if (target.Source?.Modality == SemanticContentModality.Video)
        {
            if (target.TimestampMs is not { } time || time < 0 || target.Source.DurationMs is not { } duration || time >= duration)
                validation.Fail(path + ".timestampMs", "range", "视频目标时间必须位于 [0, durationMs)。");
            if (target.FrameIndex is < 0)
                validation.Fail(path + ".frameIndex", "range", "帧序号不能为负数。");
            if (target.TrackId is not null)
                validation.Text(target.TrackId, path + ".trackId");
        }
        if (target.CropObjectRef is not null)
        {
            ValidateObject(target.CropObjectRef, path + ".cropObjectRef", validation);
            if (SameObjectVersion(target.CropObjectRef, target.Source?.ObjectRef))
                validation.Fail(path + ".cropObjectRef", "source", "裁剪派生对象不能指向原对象本身。");
        }
    }

    private static void ValidateSource(VisualSourceReference? source, string path, Validation validation)
    {
        validation.Check();
        if (source is null)
        {
            validation.Fail(path, "required", "必须提供原对象来源。");
            return;
        }
        validation.Text(source.ContentId, path + ".contentId");
        validation.Text(source.ContentHash, path + ".contentHash", 512);
        ValidateObject(source.ObjectRef, path + ".objectRef", validation);
        if (source.Width <= 0 || source.Height <= 0)
            validation.Fail(path, "dimensions", "原始画面宽度和高度必须大于 0。");
        if (source.Modality is not (SemanticContentModality.Image or SemanticContentModality.Video))
            validation.Fail(path + ".modality", "modality", "原对象必须是图片或视频。");
        if (source.Modality == SemanticContentModality.Image && source.DurationMs is not null)
            validation.Fail(path + ".durationMs", "modality", "图片不能声明视频时长。");
        if (source.Modality == SemanticContentModality.Video && source.DurationMs is not > 0)
            validation.Fail(path + ".durationMs", "range", "视频必须声明严格大于 0 的时长。");
    }

    private static void ValidateObject(SemanticObjectReference? reference, string path, Validation validation)
    {
        if (reference is null)
        {
            validation.Fail(path, "required", "必须引用原对象。");
            return;
        }
        validation.Text(reference.Bucket, path + ".bucket");
        validation.Text(reference.Key, path + ".key", 4096);
        if (reference.VersionId is null && reference.ETag is null)
            validation.Fail(path, "identity", "必须固定 versionId 或 ETag。");
        if (reference.VersionId is not null)
            validation.Text(reference.VersionId, path + ".versionId", 1024);
        if (reference.ETag is not null)
            validation.Text(reference.ETag, path + ".eTag", 1024);
    }

    private static void ValidateState(SemanticIndexStateInfo? state, string path, Validation validation)
    {
        if (state is null)
        {
            validation.Fail(path, "required", "派生状态不能为空。");
            return;
        }
        if (!Enum.IsDefined(state.State))
            validation.Fail(path + ".state", "enum", "派生状态非法。");
        if (state.Attempt < 0)
            validation.Fail(path + ".attempt", "range", "尝试次数不能为负数。");
        if (state.UpdatedUtc.Offset != TimeSpan.Zero)
            validation.Fail(path + ".updatedUtc", "utc", "状态时间必须使用 UTC。");
        if (state.State == SemanticIndexState.Failed)
            validation.Text(state.LastError, path + ".lastError", 1024);
        else if (state.LastError is not null)
            validation.Fail(path + ".lastError", "state", "只有 Failed 状态可以携带错误说明。");
    }

    private static bool Supports(VisualDetectorProfile profile, SemanticContentModality modality)
        => profile.Supports(modality);

    private static bool SameObjectVersion(SemanticObjectReference derived, SemanticObjectReference? source)
        => source is not null && derived.Bucket == source.Bucket && derived.Key == source.Key
            && ((source.VersionId is not null && derived.VersionId == source.VersionId)
                || (source.ETag is not null && derived.ETag == source.ETag));

    private sealed class Validation(CancellationToken cancellationToken)
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly List<SemanticContentValidationFailure> _failures = [];

        internal void Check()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(_started) > TimeSpan.FromSeconds(5))
                throw new TimeoutException("视觉合同校验超过五秒协作预算。");
        }

        internal bool Identifier([NotNullWhen(true)] string? value)
            => value is { Length: > 0 and <= 256 } && !string.IsNullOrWhiteSpace(value);

        internal void Text(string? value, string path, int maximum = 256)
        {
            Check();
            if (value is null || value.Length == 0)
                Fail(path, "required", "字段不能为空。");
            else if (value.Length > maximum)
                Fail(path, "budget", $"字段最多允许 {maximum} 个 UTF-16 字符。");
            else if (string.IsNullOrWhiteSpace(value))
                Fail(path, "required", "字段不能只包含空白。");
            else if (!IsValidUtf16(value))
                Fail(path, "encoding", "字段包含不成对的 UTF-16 surrogate。");
        }

        internal bool Count<T>(IReadOnlyList<T>? items, int maximum, string path, out int count)
        {
            Check();
            count = items?.Count ?? 0;
            if (items is not null && count >= 0 && count <= maximum)
                return true;
            Fail(path, items is null ? "required" : "budget", $"集合不能为空引用且最多允许 {maximum} 项。");
            return false;
        }

        internal void Fail(string path, string rule, string message)
        {
            if (_failures.Count < 256)
                _failures.Add(new(path, rule, message));
        }

        internal SemanticContentValidationResult Result()
        {
            Check();
            return _failures.Count == 0 ? SemanticContentValidationResult.Valid : new(false, _failures.AsReadOnly());
        }

        private static bool IsValidUtf16(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                if (char.IsLowSurrogate(character))
                    return false;
                if (char.IsHighSurrogate(character) && (++i >= value.Length || !char.IsLowSurrogate(value[i])))
                    return false;
            }
            return true;
        }
    }
}
