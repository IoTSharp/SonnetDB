namespace SonnetDB.FullText.Storage;

/// <summary>
/// 索引持久化存储抽象。
/// </summary>
/// <remarks>
/// <see cref="PersistentFullTextIndex"/> 实现该接口，用于暴露数据库目录。
/// </remarks>
public interface IIndexStorage
{
    /// <summary>
    /// 数据库目录的绝对路径。
    /// </summary>
    string Directory { get; }
}
