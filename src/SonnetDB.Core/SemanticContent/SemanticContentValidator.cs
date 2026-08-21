namespace SonnetDB.SemanticContent;

/// <summary>
/// Semantic Content 合同校验器。
/// 该校验器只验证确定性的元数据和引用，不读取对象内容，也不调用 embedding provider。
/// </summary>
public static class SemanticContentValidator
{
    /// <summary>
    /// 校验内容清单及其结构不变量。
    /// </summary>
    /// <param name="manifest">待校验的内容清单。</param>
    /// <param name="profiles">可选的 profile 注册表；提供时会校验 profile 引用和模态兼容性。</param>
    /// <returns>结构化校验结果。</returns>
    public static SemanticContentValidationResult Validate(
        SemanticContentManifest manifest,
        IReadOnlyDictionary<string, EmbeddingProfile>? profiles = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var failures = new List<SemanticContentValidationFailure>();

        if (manifest.SchemaVersion <= 0)
            Add(failures, "schemaVersion", "positive", "schemaVersion 必须大于 0。");
        if (string.IsNullOrWhiteSpace(manifest.Id))
            Add(failures, "id", "required", "内容清单 id 不能为空。");
        if (manifest.ObjectRef is null)
        {
            Add(failures, "objectRef", "required", "内容清单必须引用原始对象。");
        }
        else
        {
            ValidateObjectReference(manifest.ObjectRef, failures, "objectRef");
        }

        if (string.IsNullOrWhiteSpace(manifest.ContentHash))
            Add(failures, "contentHash", "required", "contentHash 不能为空。");
        if (!IsMimeType(manifest.MimeType))
            Add(failures, "mimeType", "format", "mimeType 必须包含非空的 type/subtype。");
        if (!Enum.IsDefined(manifest.Modality) || manifest.Modality == SemanticContentModality.Unknown)
            Add(failures, "modality", "enum", "modality 必须是受支持的模态。");
        if (manifest.SizeBytes < 0)
            Add(failures, "sizeBytes", "minimum", "sizeBytes 不能为负数。");

        ValidateState(manifest.IndexState, failures, "indexState");
        ValidateChunks(manifest.Chunks, failures);
        ValidateSegments(manifest.Segments, failures);
        ValidateBindings(manifest, profiles, failures);

        if (manifest.CreatedUtc != default
            && manifest.UpdatedUtc != default
            && manifest.UpdatedUtc < manifest.CreatedUtc)
        {
            Add(failures, "updatedUtc", "ordering", "updatedUtc 不能早于 createdUtc。");
        }

        return failures.Count == 0
            ? SemanticContentValidationResult.Valid
            : new SemanticContentValidationResult(false, failures.AsReadOnly());
    }

    /// <summary>
    /// 校验清单，失败时抛出带有结构化路径信息的 <see cref="ArgumentException"/>。
    /// </summary>
    /// <param name="manifest">待校验的内容清单。</param>
    /// <param name="profiles">可选的 profile 注册表。</param>
    public static void ValidateOrThrow(
        SemanticContentManifest manifest,
        IReadOnlyDictionary<string, EmbeddingProfile>? profiles = null)
    {
        var result = Validate(manifest, profiles);
        if (result.IsValid)
            return;

        string message = string.Join(
            " ",
            result.Failures.Select(static failure =>
                $"[{failure.Path}] {failure.Rule}: {failure.Message}"));
        throw new ArgumentException(message, nameof(manifest));
    }

    /// <summary>
    /// 校验单个 embedding profile。
    /// </summary>
    /// <param name="profile">待校验的 profile。</param>
    /// <param name="path">错误路径前缀。</param>
    /// <returns>profile 校验失败列表。</returns>
    public static IReadOnlyList<SemanticContentValidationFailure> ValidateProfile(
        EmbeddingProfile profile,
        string path = "profile")
    {
        ArgumentNullException.ThrowIfNull(profile);
        var failures = new List<SemanticContentValidationFailure>();
        if (string.IsNullOrWhiteSpace(profile.Id))
            Add(failures, path + ".id", "required", "profile id 不能为空。");
        if (string.IsNullOrWhiteSpace(profile.Provider))
            Add(failures, path + ".provider", "required", "provider 不能为空。");
        if (string.IsNullOrWhiteSpace(profile.Model))
            Add(failures, path + ".model", "required", "model 不能为空。");
        if (string.IsNullOrWhiteSpace(profile.Revision))
            Add(failures, path + ".revision", "required", "revision 不能为空。");
        if (profile.Dimensions <= 0)
            Add(failures, path + ".dimensions", "positive", "dimensions 必须大于 0。");
        if (!Enum.IsDefined(profile.Metric))
            Add(failures, path + ".metric", "enum", "metric 不是受支持的距离度量。");
        if (!Enum.IsDefined(profile.Normalization))
            Add(failures, path + ".normalization", "enum", "normalization 不是受支持的归一化方式。");
        if (profile.SupportedModalities.Count == 0)
            Add(failures, path + ".supportedModalities", "required", "至少声明一个支持的模态。");
        else
        {
            var seen = new HashSet<SemanticContentModality>();
            for (var i = 0; i < profile.SupportedModalities.Count; i++)
            {
                var modality = profile.SupportedModalities[i];
                if (!Enum.IsDefined(modality) || modality == SemanticContentModality.Unknown)
                    Add(failures, $"{path}.supportedModalities[{i}]", "enum", "模态值非法。");
                else if (!seen.Add(modality))
                    Add(failures, $"{path}.supportedModalities[{i}]", "unique", "模态不能重复。");
            }
        }

        if (!Enum.IsDefined(profile.DataEgressPolicy.Mode))
            Add(failures, path + ".dataEgressPolicy.mode", "enum", "外发模式非法。");
        if (profile.DataEgressPolicy.Mode != SemanticDataEgressMode.LocalOnly
            && string.IsNullOrWhiteSpace(profile.DataEgressPolicy.Target))
        {
            Add(failures, path + ".dataEgressPolicy.target", "required", "非本地外发模式必须指定 target。");
        }

        return failures.AsReadOnly();
    }

    private static void ValidateObjectReference(
        SemanticObjectReference reference,
        ICollection<SemanticContentValidationFailure> failures,
        string path)
    {
        if (string.IsNullOrWhiteSpace(reference.Bucket))
            Add(failures, path + ".bucket", "required", "bucket 不能为空。");
        if (string.IsNullOrWhiteSpace(reference.Key))
            Add(failures, path + ".key", "required", "key 不能为空。");
        if (string.IsNullOrWhiteSpace(reference.VersionId)
            && string.IsNullOrWhiteSpace(reference.ETag))
        {
            Add(failures, path, "identity", "objectRef 至少需要 versionId 或 eTag。");
        }
    }

    private static void ValidateState(
        SemanticIndexStateInfo? state,
        ICollection<SemanticContentValidationFailure> failures,
        string path)
    {
        if (state is null)
        {
            Add(failures, path, "required", "indexState 不能为空。");
            return;
        }

        if (!Enum.IsDefined(state.State))
            Add(failures, path + ".state", "enum", "indexState.state 非法。");
        if (state.Attempt < 0)
            Add(failures, path + ".attempt", "minimum", "attempt 不能为负数。");
        if (state.State == SemanticIndexState.Failed && string.IsNullOrWhiteSpace(state.LastError))
            Add(failures, path + ".lastError", "required", "failed 状态必须保留 lastError。");
        if (state.State != SemanticIndexState.Failed && !string.IsNullOrWhiteSpace(state.LastError))
            Add(failures, path + ".lastError", "state", "非 failed 状态不能携带 lastError。");
    }

    private static void ValidateChunks(
        IReadOnlyList<SemanticContentChunk>? chunks,
        ICollection<SemanticContentValidationFailure> failures)
    {
        if (chunks is null)
        {
            Add(failures, "chunks", "required", "chunks 不能为 null；没有分块时使用空数组。");
            return;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            string path = $"chunks[{i}]";
            if (string.IsNullOrWhiteSpace(chunk.Id))
                Add(failures, path + ".id", "required", "分块 id 不能为空。");
            else if (!ids.Add(chunk.Id))
                Add(failures, path + ".id", "unique", "同一内容中的分块 id 不能重复。");
            if (chunk.Ordinal < 0)
                Add(failures, path + ".ordinal", "minimum", "分块 ordinal 不能为负数。");
            if (string.IsNullOrWhiteSpace(chunk.Text))
                Add(failures, path + ".text", "required", "分块 text 不能为空。");
            if (chunk.StartOffset is null != (chunk.EndOffset is null))
                Add(failures, path, "range", "startOffset 和 endOffset 必须同时提供或同时省略。");
            else if (chunk.StartOffset is { } start
                && chunk.EndOffset is { } end
                && (start < 0 || end <= start))
            {
                Add(failures, path, "range", "分块偏移必须满足 0 <= startOffset < endOffset。");
            }
        }
    }

    private static void ValidateSegments(
        IReadOnlyList<SemanticContentSegment>? segments,
        ICollection<SemanticContentValidationFailure> failures)
    {
        if (segments is null)
        {
            Add(failures, "segments", "required", "segments 不能为 null；没有分段时使用空数组。");
            return;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            string path = $"segments[{i}]";
            if (string.IsNullOrWhiteSpace(segment.Id))
                Add(failures, path + ".id", "required", "分段 id 不能为空。");
            else if (!ids.Add(segment.Id))
                Add(failures, path + ".id", "unique", "同一内容中的分段 id 不能重复。");
            if (segment.Ordinal < 0)
                Add(failures, path + ".ordinal", "minimum", "分段 ordinal 不能为负数。");
            if (segment.StartMs < 0 || segment.EndMs <= segment.StartMs)
                Add(failures, path, "range", "分段时间必须满足 0 <= startMs < endMs。");
            if (segment.KeyFrameRef is not null)
                ValidateObjectReference(segment.KeyFrameRef, failures, path + ".keyFrameRef");
        }
    }

    private static void ValidateBindings(
        SemanticContentManifest manifest,
        IReadOnlyDictionary<string, EmbeddingProfile>? profiles,
        ICollection<SemanticContentValidationFailure> failures)
    {
        if (!string.IsNullOrWhiteSpace(manifest.EmbeddingProfileId)
            && manifest.Embeddings.Count == 0
            && profiles is not null
            && !profiles.ContainsKey(manifest.EmbeddingProfileId))
        {
            Add(failures, "embeddingProfileId", "reference", "embeddingProfileId 未在 profile 注册表中找到。");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < manifest.Embeddings.Count; i++)
        {
            var binding = manifest.Embeddings[i];
            string path = $"embeddings[{i}]";
            if (string.IsNullOrWhiteSpace(binding.Name))
                Add(failures, path + ".name", "required", "命名向量名称不能为空。");
            else if (!names.Add(binding.Name))
                Add(failures, path + ".name", "unique", "命名向量名称不能重复。");
            if (string.IsNullOrWhiteSpace(binding.ProfileId))
                Add(failures, path + ".profileId", "required", "命名向量必须引用 profile。");
            EmbeddingProfile? profile = null;
            if (profiles is not null
                && !string.IsNullOrWhiteSpace(binding.ProfileId)
                && !profiles.TryGetValue(binding.ProfileId, out profile))
            {
                Add(failures, path + ".profileId", "reference", "命名向量引用的 profile 不存在。");
            }
            else if (profiles is not null && profile is not null)
            {
                foreach (var failure in ValidateProfile(profile, path + ".profile"))
                    failures.Add(failure);
                if (!profile.Supports(manifest.Modality))
                {
                    Add(
                        failures,
                        path + ".profileId",
                        "modality",
                        $"profile '{profile.Id}' 不支持内容模态 '{manifest.Modality}'。");
                }
            }

            ValidateState(binding.IndexState, failures, path + ".indexState");
        }
    }

    private static bool IsMimeType(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType))
            return false;
        int separator = mimeType.IndexOf('/');
        return separator > 0 && separator < mimeType.Length - 1
            && !mimeType.Any(char.IsWhiteSpace);
    }

    private static void Add(
        ICollection<SemanticContentValidationFailure> failures,
        string path,
        string rule,
        string message)
        => failures.Add(new SemanticContentValidationFailure(path, rule, message));
}

/// <summary>
/// Semantic Content 合同校验器的兼容名称。
/// </summary>
public static class SemanticContentContractValidator
{
    /// <summary>转发到 <see cref="SemanticContentValidator.Validate"/>。</summary>
    public static SemanticContentValidationResult Validate(
        SemanticContentManifest manifest,
        IReadOnlyDictionary<string, EmbeddingProfile>? profiles = null)
        => SemanticContentValidator.Validate(manifest, profiles);

    /// <summary>转发到 <see cref="SemanticContentValidator.ValidateOrThrow"/>。</summary>
    public static void ValidateOrThrow(
        SemanticContentManifest manifest,
        IReadOnlyDictionary<string, EmbeddingProfile>? profiles = null)
        => SemanticContentValidator.ValidateOrThrow(manifest, profiles);
}
