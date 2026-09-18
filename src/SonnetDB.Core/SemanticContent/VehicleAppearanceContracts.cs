using System.Text.Json.Serialization;

namespace SonnetDB.SemanticContent;

/// <summary>车辆外观模型完整合同；不能与图片通用或人员向量混用。</summary>
/// <param name="Embedding">完整 embedding 合同。</param><param name="InputLayoutRevision">车辆裁剪/预处理规范版本。</param>
public sealed record VehicleAppearanceProfile(EmbeddingProfile Embedding, string InputLayoutRevision);

/// <summary>外部车牌 OCR 模型的版本身份。</summary>
/// <param name="Id">不可变身份。</param><param name="Provider">提供方。</param><param name="Model">模型。</param>
/// <param name="Revision">权重版本。</param><param name="PreprocessingRevision">预处理版本。</param>
public sealed record VehiclePlateOcrProfile(string Id, string Provider, string Model, string Revision, string PreprocessingRevision);

/// <summary>外部工具的车牌读取；置信度不改变精确号码语义。</summary>
/// <param name="Jurisdiction">显式签发地区，ASCII 字母/数字/连字符，大小写无关。</param>
/// <param name="Text">外部 OCR 原始文本；不会将 O/0 或 I/1 互换。</param>
/// <param name="Confidence">0..1 的提供方置信度。</param><param name="Profile">OCR 模型版本。</param>
public sealed record VehiclePlateReading(string Jurisdiction, string Text, double Confidence, VehiclePlateOcrProfile Profile);

/// <summary>车辆目标和可选的外观向量、车牌读取；至少提供一种。</summary>
/// <param name="Target">带固定原对象来源的车辆目标。</param><param name="DetectorProfile">检测模型合同。</param>
/// <param name="AppearanceProfile">可选车辆外观 profile。</param><param name="Embedding">对应外观向量。</param>
/// <param name="Plate">可选车牌 OCR 读取。</param>
public sealed record VehicleObservation(VisualDerivedTarget Target, VisualDetectorProfile DetectorProfile,
    VehicleAppearanceProfile? AppearanceProfile = null, IReadOnlyList<float>? Embedding = null, VehiclePlateReading? Plate = null);

/// <summary>车辆命中；外观距离与精确号码查询分离。</summary>
/// <param name="ObservationId">原对象版本、detector profile 与局部目标 ID 的稳定复合身份。</param>
/// <param name="Observation">完整派生目标及模型来源。</param><param name="Distance">外观查询的距离；号码查询为 null。</param>
public sealed record VehicleSearchHit(string ObservationId, VehicleObservation Observation, float? Distance = null);

/// <summary>有界查询结果。</summary>
/// <param name="Hits">命中。</param><param name="HasMore">精确号码查询存在更多新鲜匹配。</param>
/// <param name="Scanned">读取候选数。</param><param name="StaleSkipped">跳过的陈旧候选数。</param>
public sealed record VehicleSearchResult(IReadOnlyList<VehicleSearchHit> Hits, bool HasMore, int Scanned, int StaleSkipped);

/// <summary>车辆查询预算。</summary>
public sealed record VehicleSearchOptions
{
    /// <summary>创建默认预算。</summary>
    public VehicleSearchOptions() { }
    /// <summary>最大结果数，1..1000。</summary>
    public int Limit { get; init; } = 100;
    /// <summary>最大读取候选数，1..10000；超限不返回部分结果。</summary>
    public int MaxCandidates { get; init; } = 10_000;
    /// <summary>最多计算的向量标量总数，1..10000000。</summary>
    public int MaxVectorValues { get; init; } = 1_000_000;
    /// <summary>每次查询的协作期限，最多一分钟。</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromSeconds(10);
}

internal sealed record VehicleStoredObservation(VehicleObservation Observation, string? PlateKey,
    string? AppearanceProfileId, string? OcrProfileId, string DetectorProfileId);

/// <summary>车辆外观和车牌的 source-generated JSON 元数据。</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(VehicleAppearanceProfile))]
[JsonSerializable(typeof(VehiclePlateOcrProfile))]
[JsonSerializable(typeof(VehiclePlateReading))]
[JsonSerializable(typeof(VehicleObservation))]
[JsonSerializable(typeof(VehicleSearchHit))]
[JsonSerializable(typeof(VehicleSearchResult))]
[JsonSerializable(typeof(VehicleSearchOptions))]
public sealed partial class VehicleAppearanceJsonContext : JsonSerializerContext;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(VehicleStoredObservation))]
internal sealed partial class VehicleStoreJsonContext : JsonSerializerContext;
