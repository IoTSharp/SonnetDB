using System.Text.Json.Serialization;

namespace SonnetDB.SemanticContent;

/// <summary>
/// RAG 增量摄取动作类型。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RagIngestionActionKind>))]
public enum RagIngestionActionKind : byte
{
    /// <summary>未指定动作。</summary>
    Unknown = 0,

    /// <summary>新增当前不存在的内容。</summary>
    Add = 1,

    /// <summary>用新清单更新已有内容。</summary>
    Update = 2,

    /// <summary>删除当前输入中已经不存在的内容。</summary>
    Delete = 3,
}

/// <summary>
/// RAG 摄取快照；清单集合代表一次完整的期望状态，而不是只包含变化的补丁。
/// </summary>
public sealed record RagIngestionSnapshot
{
    /// <summary>创建空快照，供 source-generated JSON 反序列化使用。</summary>
    public RagIngestionSnapshot()
    {
    }

    /// <summary>创建完整摄取快照。</summary>
    /// <param name="manifests">该快照中的完整内容清单集合。</param>
    public RagIngestionSnapshot(IReadOnlyList<SemanticContentManifest> manifests)
    {
        ArgumentNullException.ThrowIfNull(manifests);
        Manifests = manifests.ToArray();
    }

    /// <summary>快照合同版本。</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>该快照中的完整内容清单集合。</summary>
    public IReadOnlyList<SemanticContentManifest> Manifests { get; init; }
        = Array.Empty<SemanticContentManifest>();

    /// <summary>不包含任何内容的共享快照。</summary>
    public static RagIngestionSnapshot Empty { get; } = new();
}

/// <summary>
/// 一项确定性的 RAG 增量摄取动作。
/// </summary>
public sealed record RagIngestionAction
{
    /// <summary>创建空动作，供 source-generated JSON 反序列化使用。</summary>
    public RagIngestionAction()
    {
    }

    /// <summary>创建增量摄取动作。</summary>
    /// <param name="kind">动作类型。</param>
    /// <param name="contentId">目标内容的稳定标识。</param>
    /// <param name="previous">动作前的清单；新增时为空。</param>
    /// <param name="current">动作后的清单；删除时为空。</param>
    public RagIngestionAction(
        RagIngestionActionKind kind,
        string contentId,
        SemanticContentManifest? previous,
        SemanticContentManifest? current)
    {
        Kind = kind;
        ContentId = contentId;
        Previous = previous;
        Current = current;
    }

    /// <summary>动作类型。</summary>
    public RagIngestionActionKind Kind { get; init; }

    /// <summary>目标内容的稳定标识。</summary>
    public string ContentId { get; init; } = string.Empty;

    /// <summary>动作前的清单；新增时为空。</summary>
    public SemanticContentManifest? Previous { get; init; }

    /// <summary>动作后的清单；删除时为空。</summary>
    public SemanticContentManifest? Current { get; init; }
}

/// <summary>
/// 按内容标识稳定排序的 RAG 增量摄取计划。
/// </summary>
public sealed record RagIngestionPlan
{
    /// <summary>创建空计划，供 source-generated JSON 反序列化使用。</summary>
    public RagIngestionPlan()
    {
    }

    /// <summary>创建增量摄取计划。</summary>
    /// <param name="actions">待执行动作。</param>
    public RagIngestionPlan(IReadOnlyList<RagIngestionAction> actions)
    {
        ArgumentNullException.ThrowIfNull(actions);
        Actions = actions.ToArray();
    }

    /// <summary>待执行动作。</summary>
    public IReadOnlyList<RagIngestionAction> Actions { get; init; }
        = Array.Empty<RagIngestionAction>();

    /// <summary>计划是否不包含任何写入或删除。</summary>
    [JsonIgnore]
    public bool IsEmpty => Actions.Count == 0;

    /// <summary>新增动作数。</summary>
    [JsonIgnore]
    public int AddCount => Count(RagIngestionActionKind.Add);

    /// <summary>更新动作数。</summary>
    [JsonIgnore]
    public int UpdateCount => Count(RagIngestionActionKind.Update);

    /// <summary>删除动作数。</summary>
    [JsonIgnore]
    public int DeleteCount => Count(RagIngestionActionKind.Delete);

    private int Count(RagIngestionActionKind kind)
        => Actions.Count(action => action.Kind == kind);
}

/// <summary>
/// RAG 增量计划的输入和输出预算。
/// </summary>
public sealed record RagIngestionPlanningOptions
{
    /// <summary>前后任一快照允许包含的最大清单数。</summary>
    public int MaxManifests { get; init; } = 100_000;

    /// <summary>先前与当前快照合计允许包含的最大分块数。</summary>
    public int MaxTotalChunks { get; init; } = 1_000_000;

    /// <summary>先前与当前快照合计允许包含的最大时间分段数。</summary>
    public int MaxTotalSegments { get; init; } = 1_000_000;

    /// <summary>先前与当前快照合计允许包含的最大 embedding 绑定数。</summary>
    public int MaxTotalEmbeddings { get; init; } = 1_000_000;

    /// <summary>
    /// 先前与当前快照合计允许包含的最大文本字符数。
    /// 该预算累计内容级文本、分块文本和分段文本的 UTF-16 长度。
    /// </summary>
    public long MaxTotalTextCharacters { get; init; } = 128L * 1024 * 1024;

    /// <summary>单个计划允许产生的最大动作数。</summary>
    public int MaxActions { get; init; } = 100_000;
}

/// <summary>
/// 比较完整快照并生成显式新增、更新和删除动作。
/// </summary>
public static class RagIngestionPlanner
{
    /// <summary>
    /// 生成从旧快照迁移到当前完整快照的确定性计划。
    /// 索引运行状态与时间戳不会触发内容更新，避免恢复或重试状态产生无效重写。
    /// </summary>
    /// <param name="previous">先前完整快照；首次摄取时可以为空。</param>
    /// <param name="current">当前完整期望快照。</param>
    /// <param name="options">可选的计划资源预算。</param>
    /// <param name="cancellationToken">用于停止比较工作的取消令牌。</param>
    /// <returns>按当前内容标识排序、再附加删除动作的增量计划。</returns>
    /// <exception cref="ArgumentException">快照合同不合法、清单无效或标识重复。</exception>
    /// <exception cref="InvalidOperationException">输入或输出超过预算。</exception>
    public static RagIngestionPlan CreatePlan(
        RagIngestionSnapshot? previous,
        RagIngestionSnapshot current,
        RagIngestionPlanningOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        options ??= new RagIngestionPlanningOptions();
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();

        previous ??= RagIngestionSnapshot.Empty;
        var limits = new RagNestedBudgetLimits(
            options.MaxTotalChunks,
            options.MaxTotalSegments,
            options.MaxTotalEmbeddings,
            options.MaxTotalTextCharacters);
        var usage = new RagNestedBudgetUsage();
        SemanticContentManifest[] previousManifests = RagIngestionPreflight.FreezeSnapshot(
            previous,
            options.MaxManifests,
            limits,
            usage,
            nameof(previous),
            cancellationToken);
        SemanticContentManifest[] currentManifests = RagIngestionPreflight.FreezeSnapshot(
            current,
            options.MaxManifests,
            limits,
            usage,
            nameof(current),
            cancellationToken);
        var previousById = Index(previousManifests, nameof(previous), cancellationToken);
        var currentById = Index(currentManifests, nameof(current), cancellationToken);
        var actions = new List<RagIngestionAction>();

        foreach (var pair in currentById.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!previousById.TryGetValue(pair.Key, out var oldManifest))
            {
                AddAction(
                    actions,
                    new RagIngestionAction(
                        RagIngestionActionKind.Add,
                        pair.Key,
                        previous: null,
                        pair.Value),
                    options);
            }
            else if (!EquivalentForIngestion(oldManifest, pair.Value, cancellationToken))
            {
                AddAction(
                    actions,
                    new RagIngestionAction(
                        RagIngestionActionKind.Update,
                        pair.Key,
                        oldManifest,
                        pair.Value),
                    options);
            }
        }

        foreach (var pair in previousById
                     .Where(pair => !currentById.ContainsKey(pair.Key))
                     .OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AddAction(
                actions,
                new RagIngestionAction(
                    RagIngestionActionKind.Delete,
                    pair.Key,
                    pair.Value,
                    current: null),
                options);
        }

        return new RagIngestionPlan(actions);
    }

    private static Dictionary<string, SemanticContentManifest> Index(
        IReadOnlyList<SemanticContentManifest> manifests,
        string parameterName,
        CancellationToken cancellationToken)
    {
        var byId = new Dictionary<string, SemanticContentManifest>(StringComparer.Ordinal);
        for (int index = 0; index < manifests.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SemanticContentManifest manifest = manifests[index];
            if (!byId.TryAdd(manifest.Id, manifest))
            {
                throw new ArgumentException(
                    $"快照包含重复内容标识 '{manifest.Id}'。",
                    parameterName);
            }
        }

        return byId;
    }

    private static bool EquivalentForIngestion(
        SemanticContentManifest left,
        SemanticContentManifest right,
        CancellationToken cancellationToken)
        => left.SchemaVersion == right.SchemaVersion
           && Equals(left.ObjectRef, right.ObjectRef)
           && string.Equals(left.ContentHash, right.ContentHash, StringComparison.Ordinal)
           && string.Equals(left.MimeType, right.MimeType, StringComparison.Ordinal)
           && left.Modality == right.Modality
           && left.SizeBytes == right.SizeBytes
           && string.Equals(left.Source, right.Source, StringComparison.Ordinal)
           && string.Equals(
               left.EmbeddingProfileId,
               right.EmbeddingProfileId,
               StringComparison.Ordinal)
           && string.Equals(left.Text, right.Text, StringComparison.Ordinal)
           && EquivalentItems(left.Chunks, right.Chunks, cancellationToken)
           && EquivalentItems(left.Segments, right.Segments, cancellationToken)
           && EquivalentBindings(left.Embeddings, right.Embeddings, cancellationToken);

    private static bool EquivalentItems<T>(
        IReadOnlyList<T> left,
        IReadOnlyList<T> right,
        CancellationToken cancellationToken)
    {
        if (left.Count != right.Count)
            return false;

        var comparer = EqualityComparer<T>.Default;
        for (int index = 0; index < left.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!comparer.Equals(left[index], right[index]))
                return false;
        }

        return true;
    }

    private static bool EquivalentBindings(
        IReadOnlyList<SemanticEmbeddingBinding> left,
        IReadOnlyList<SemanticEmbeddingBinding> right,
        CancellationToken cancellationToken)
    {
        if (left.Count != right.Count)
            return false;

        for (int index = 0; index < left.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SemanticEmbeddingBinding leftBinding = left[index];
            SemanticEmbeddingBinding rightBinding = right[index];
            if (!string.Equals(leftBinding.Name, rightBinding.Name, StringComparison.Ordinal)
                || !string.Equals(leftBinding.ProfileId, rightBinding.ProfileId, StringComparison.Ordinal)
                || !string.Equals(leftBinding.VectorField, rightBinding.VectorField, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void AddAction(
        ICollection<RagIngestionAction> actions,
        RagIngestionAction action,
        RagIngestionPlanningOptions options)
    {
        if (actions.Count == options.MaxActions)
        {
            throw new InvalidOperationException(
                $"增量计划动作数超过预算 {options.MaxActions}。");
        }

        actions.Add(action);
    }

    private static void ValidateOptions(RagIngestionPlanningOptions options)
    {
        if (options.MaxManifests <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxManifests 必须大于 0。");
        if (options.MaxTotalChunks <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxTotalChunks 必须大于 0。");
        if (options.MaxTotalSegments <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxTotalSegments 必须大于 0。");
        if (options.MaxTotalEmbeddings <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxTotalEmbeddings 必须大于 0。");
        if (options.MaxTotalTextCharacters <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxTotalTextCharacters 必须大于 0。");
        if (options.MaxActions <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxActions 必须大于 0。");
    }
}
