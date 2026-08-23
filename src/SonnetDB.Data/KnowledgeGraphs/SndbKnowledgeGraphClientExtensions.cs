using SonnetDB.Data.Graphs;
using SonnetDB.Graphs;
using SonnetDB.KnowledgeGraphs;

namespace SonnetDB.Data.KnowledgeGraphs;

/// <summary>知识图谱/GraphRAG 上层合同的 typed SDK 入口。</summary>
public static class SndbKnowledgeGraphClientExtensions
{
    /// <summary>
    /// 校验知识图谱批次，并通过现有 Graph import API 原子写入通用属性图。
    /// </summary>
    /// <param name="client">嵌入式或远程 Graph typed client。</param>
    /// <param name="graph">目标原生图名称。</param>
    /// <param name="batch">版本化知识图谱批次。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>现有 Graph import 提交结果。</returns>
    public static Task<GraphImportResponse> ImportKnowledgeGraphAsync(
        this SndbGraphClient client,
        string graph,
        KnowledgeGraphBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(graph);
        GraphImportRequest request = KnowledgeGraphMapper.ToGraphImportRequest(batch);
        return client.ImportAsync(graph, request, cancellationToken);
    }
}
