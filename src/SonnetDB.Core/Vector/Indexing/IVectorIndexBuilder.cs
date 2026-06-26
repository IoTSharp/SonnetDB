namespace SonnetDB.Vector.Indexing;

/// <summary>
/// 向量索引构建器。
/// </summary>
public interface IVectorIndexBuilder
{
    /// <summary>
    /// 根据输入向量构建可搜索的索引 reader。
    /// </summary>
    /// <param name="input">构建输入。</param>
    /// <returns>可搜索的索引 reader。调用方负责释放返回实例。</returns>
    IVectorIndexReader Build(VectorIndexBuildInput input);
}
