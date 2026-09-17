using SonnetDB.SemanticContent;
using SonnetDB.Vector.Primitives;

namespace SonnetDB.Configuration;

/// <summary>可由 AOT 配置绑定器填充的 RAG 模型合同；运行前转换为不可变 profile。</summary>
public sealed class CopilotRagProfileOptions
{
    /// <summary>不可变模型空间 ID。</summary>
    public string Id { get; set; } = string.Empty;
    /// <summary>provider 名称。</summary>
    public string Provider { get; set; } = string.Empty;
    /// <summary>实际模型名称。</summary>
    public string Model { get; set; } = string.Empty;
    /// <summary>模型与预处理版本。</summary>
    public string Revision { get; set; } = string.Empty;
    /// <summary>输出维度。</summary>
    public int Dimensions { get; set; }
    /// <summary>向量距离度量。</summary>
    public KnnMetric Metric { get; set; } = KnnMetric.Cosine;
    /// <summary>provider 输出归一化合同。</summary>
    public EmbeddingNormalization Normalization { get; set; }
    /// <summary>支持模态；RAG 文档知识库必须包含 Text 与 Document。</summary>
    public List<SemanticContentModality> SupportedModalities { get; set; } = [];
    /// <summary>外发与审计策略。</summary>
    public CopilotRagEgressOptions DataEgressPolicy { get; set; } = new();

    internal EmbeddingProfile ToProfile() => new(Id, Provider, Model, Revision, Dimensions, Metric, Normalization,
        SupportedModalities.ToArray(), new(DataEgressPolicy.Mode, DataEgressPolicy.Target, DataEgressPolicy.AuditRequired));
}

/// <summary>RAG 配置绑定使用的显式外发策略。</summary>
public sealed class CopilotRagEgressOptions
{
    /// <summary>外发模式，默认只在本地执行。</summary>
    public SemanticDataEgressMode Mode { get; set; }
    /// <summary>允许的完整 HTTPS provider 基地址。</summary>
    public string? Target { get; set; }
    /// <summary>是否要求每次调用前持久化审计；默认开启。</summary>
    public bool AuditRequired { get; set; } = true;
}
