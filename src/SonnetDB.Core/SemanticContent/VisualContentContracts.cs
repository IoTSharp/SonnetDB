using System.Text.Json.Serialization;

namespace SonnetDB.SemanticContent;

/// <summary>原对象方向下的归一化矩形区域；坐标以左上角为原点，右下边界不超过 1。</summary>
public sealed record VisualRegion
{
    /// <summary>左边界，取值为 0 到 1。</summary>
    public double X { get; init; }

    /// <summary>上边界，取值为 0 到 1。</summary>
    public double Y { get; init; }

    /// <summary>严格大于 0 的归一化宽度。</summary>
    public double Width { get; init; }

    /// <summary>严格大于 0 的归一化高度。</summary>
    public double Height { get; init; }
}

/// <summary>派生视觉结果绑定的原始图片或视频；原始字节仍只由 Object Bucket 持有。</summary>
public sealed record VisualSourceReference
{
    /// <summary>原始内容在数据库内的稳定标识。</summary>
    public string ContentId { get; init; } = string.Empty;

    /// <summary>固定 versionId 或 ETag 的原对象引用。</summary>
    public SemanticObjectReference ObjectRef { get; init; } = new();

    /// <summary>原对象内容哈希，由导入方提供并负责核对。</summary>
    public string ContentHash { get; init; } = string.Empty;

    /// <summary>原始模态，只允许 Image 或 Video。</summary>
    public SemanticContentModality Modality { get; init; }

    /// <summary>应用原对象方向信息后的画面像素宽度。</summary>
    public int Width { get; init; }

    /// <summary>应用原对象方向信息后的画面像素高度。</summary>
    public int Height { get; init; }

    /// <summary>视频总时长（毫秒）；图片必须省略。</summary>
    public long? DurationMs { get; init; }
}

/// <summary>检测器的不可变兼容边界；模型、预处理或标签语义变化时必须使用新标识。</summary>
public sealed record VisualDetectorProfile
{
    /// <summary>不可变 profile 标识。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>提供检测结果的 provider 名称。</summary>
    public string Provider { get; init; } = string.Empty;

    /// <summary>模型名称。</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>模型权重或导出版本。</summary>
    public string Revision { get; init; } = string.Empty;

    /// <summary>缩放、裁剪、颜色与方向转换等预处理合同版本。</summary>
    public string PreprocessingRevision { get; init; } = string.Empty;

    /// <summary>类别编号与标签语义的版本。</summary>
    public string LabelSetRevision { get; init; } = string.Empty;

    /// <summary>支持的原对象模态，只允许 Image 与 Video 且不能重复。</summary>
    public IReadOnlyList<SemanticContentModality> SupportedModalities { get; init; } = [];

    /// <summary>复用语义内容外发策略；默认仅本地处理并要求审计。</summary>
    public SemanticDataEgressPolicy DataEgressPolicy { get; init; } = SemanticDataEgressPolicy.LocalOnly;

    /// <summary>比较完整 profile 合同，不接受同标识下的模型、预处理或外发策略漂移。</summary>
    /// <param name="other">待比较的 profile。</param>
    /// <returns>两个已通过校验的 profile 完全兼容时为 true；模态声明顺序不影响结果。</returns>
    public bool IsCompatibleWith(VisualDetectorProfile? other)
        => other is not null
            && Id == other.Id && Provider == other.Provider && Model == other.Model
            && Revision == other.Revision && PreprocessingRevision == other.PreprocessingRevision
            && LabelSetRevision == other.LabelSetRevision && DataEgressPolicy == other.DataEgressPolicy
            && SupportedModalities is { Count: > 0 and <= 2 }
            && other.SupportedModalities is { Count: > 0 and <= 2 }
            && SupportedModalities.Count == other.SupportedModalities.Count
            && Supports(SemanticContentModality.Image) == other.Supports(SemanticContentModality.Image)
            && Supports(SemanticContentModality.Video) == other.Supports(SemanticContentModality.Video);

    internal bool Supports(SemanticContentModality modality)
    {
        int count = SupportedModalities?.Count ?? 0;
        return count is > 0 and <= 2
            && (SupportedModalities![0] == modality || (count == 2 && SupportedModalities[1] == modality));
    }
}

/// <summary>一个检测目标或单帧观察结果；其标识只在原对象版本与检测 profile 的范围内解释。</summary>
public sealed record VisualDerivedTarget
{
    /// <summary>当前派生清单内唯一的目标标识。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>产生此结果的原对象版本与画面合同。</summary>
    public VisualSourceReference Source { get; init; } = new();

    /// <summary>产生此结果的检测器 profile 标识。</summary>
    public string DetectorProfileId { get; init; } = string.Empty;

    /// <summary>检测器标签；不解释为真实身份或数据库实体主键。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>检测置信度，必须为 0 到 1 的有限数。</summary>
    public double Confidence { get; init; }

    /// <summary>原对象方向下的归一化矩形；不得使用模型输入 letterbox 坐标。</summary>
    public VisualRegion Region { get; init; } = new();

    /// <summary>视频观察时间（毫秒），必须小于总时长；图片必须省略。</summary>
    public long? TimestampMs { get; init; }

    /// <summary>可选的视频帧序号，从 0 开始；不由时间戳反推帧率。</summary>
    public long? FrameIndex { get; init; }

    /// <summary>可选的同一视频、profile 内 track 标识，不代表跨视频身份。</summary>
    public string? TrackId { get; init; }

    /// <summary>可选裁剪派生对象；不能替代 Source，也不能指向原对象本身。</summary>
    public SemanticObjectReference? CropObjectRef { get; init; }

    /// <summary>派生结果状态；原对象或 profile 变化后调用方应将其标记为 Stale。</summary>
    public SemanticIndexStateInfo IndexState { get; init; } = SemanticIndexStateInfo.Pending;
}

/// <summary>同一视频版本与检测 profile 内的有序目标轨迹；不提供跨摄像头身份关联。</summary>
public sealed record VisualTrack
{
    /// <summary>当前派生清单内唯一的轨迹标识。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>轨迹绑定的原视频版本。</summary>
    public VisualSourceReference Source { get; init; } = new();

    /// <summary>轨迹关联的检测 profile 标识。</summary>
    public string DetectorProfileId { get; init; } = string.Empty;

    /// <summary>轨迹所属检测标签。</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>轨迹开始时间（毫秒，inclusive）。</summary>
    public long StartMs { get; init; }

    /// <summary>轨迹结束时间（毫秒，exclusive）。</summary>
    public long EndMs { get; init; }

    /// <summary>按时间严格递增排列的目标引用；每个目标只能属于一个轨迹。</summary>
    public IReadOnlyList<string> TargetIds { get; init; } = [];
}

/// <summary>一个原对象版本的完整视觉派生快照；不复制原对象字节，不承诺持久化或推理执行。</summary>
public sealed record VisualDerivationManifest
{
    /// <summary>合同版本；当前只接受 1。</summary>
    public int SchemaVersion { get; init; } = 1;

    /// <summary>该派生快照的稳定标识。</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>本快照的唯一原始对象版本。</summary>
    public VisualSourceReference Source { get; init; } = new();

    /// <summary>完整、唯一的检测 profile 声明，最多 64 个。</summary>
    public IReadOnlyList<VisualDetectorProfile> DetectorProfiles { get; init; } = [];

    /// <summary>派生目标，最多 4096 个；空集合可表达未检出目标。</summary>
    public IReadOnlyList<VisualDerivedTarget> Targets { get; init; } = [];

    /// <summary>视频轨迹，最多 512 条；所有轨迹合计最多引用 4096 个目标。</summary>
    public IReadOnlyList<VisualTrack> Tracks { get; init; } = [];

    /// <summary>该快照的派生状态。</summary>
    public SemanticIndexStateInfo IndexState { get; init; } = SemanticIndexStateInfo.Pending;
}

/// <summary>视觉派生合同的公开 Native AOT JSON 元数据。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(VisualRegion))]
[JsonSerializable(typeof(VisualSourceReference))]
[JsonSerializable(typeof(VisualDetectorProfile))]
[JsonSerializable(typeof(VisualDerivedTarget))]
[JsonSerializable(typeof(VisualTrack))]
[JsonSerializable(typeof(VisualDerivationManifest))]
public partial class VisualContentJsonContext : JsonSerializerContext;
