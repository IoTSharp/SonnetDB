namespace SonnetDB.Documents.Vector;

/// <summary>已加载向量图的只读运行状态，不扫描主文档，也不触发派生索引加载或重建。</summary>
/// <param name="Definition">当前 catalog 中的索引声明。</param>
/// <param name="State">loaded 或 not_loaded；loaded 不表示已通过主数据一致性或召回验证。</param>
/// <param name="GraphVectorCount">已加载图的有效向量数；尚未加载时为空。</param>
public sealed record DocumentVectorIndexHealth(
    DocumentVectorIndex Definition,
    string State,
    long? GraphVectorCount);
