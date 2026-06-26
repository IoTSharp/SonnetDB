namespace SonnetDB.Vector.Indexing;

/// <summary>
/// 向量索引搜索结果。
/// </summary>
/// <param name="PointIndex">命中的点位序号，对应构建输入中的行号。</param>
/// <param name="Distance">距离值，越小表示越相似。</param>
public readonly record struct VectorSearchResult(int PointIndex, float Distance);
