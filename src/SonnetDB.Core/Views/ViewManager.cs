namespace SonnetDB.Views;

/// <summary>
/// 管理同一数据库目录下的逻辑视图定义及其持久化目录。
/// </summary>
public sealed class ViewManager
{
    private readonly object _sync = new();

    /// <summary>
    /// 初始化视图管理器并加载现有目录文件。
    /// </summary>
    /// <param name="rootDirectory">视图根目录。</param>
    public ViewManager(string rootDirectory)
    {
        ArgumentNullException.ThrowIfNull(rootDirectory);
        Directory.CreateDirectory(rootDirectory);
        CatalogPath = Path.Combine(rootDirectory, ViewDefinitionCodec.FileName);
        Catalog = new ViewCatalog();
        foreach (var definition in ViewDefinitionCodec.Load(CatalogPath))
            Catalog.LoadOrReplace(definition);
    }

    /// <summary>逻辑视图目录。</summary>
    public ViewCatalog Catalog { get; }

    /// <summary>逻辑视图目录文件路径。</summary>
    public string CatalogPath { get; }

    /// <summary>
    /// 新增视图并立即原子持久化。
    /// </summary>
    /// <param name="definition">待新增的视图定义。</param>
    public void Create(ViewDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        lock (_sync)
        {
            Catalog.Add(definition);
            try
            {
                PersistLocked();
            }
            catch
            {
                Catalog.Remove(definition.Name);
                throw;
            }
        }
    }

    /// <summary>
    /// 删除视图并立即原子持久化。
    /// </summary>
    /// <param name="name">视图名称。</param>
    /// <returns>存在并删除时返回 <c>true</c>。</returns>
    public bool Drop(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_sync)
        {
            var existing = Catalog.TryGet(name);
            if (existing is null)
                return false;
            Catalog.Remove(name);
            try
            {
                PersistLocked();
                return true;
            }
            catch
            {
                Catalog.Add(existing);
                throw;
            }
        }
    }

    /// <summary>
    /// 返回直接引用指定对象的视图，按视图名称升序排列。
    /// </summary>
    /// <param name="objectName">被引用对象名称。</param>
    /// <returns>直接依赖该对象的视图列表。</returns>
    public IReadOnlyList<ViewDefinition> FindDependents(string objectName)
    {
        ArgumentNullException.ThrowIfNull(objectName);
        return Catalog.Snapshot()
            .Where(definition => definition.Dependencies.Contains(objectName, StringComparer.Ordinal))
            .ToArray();
    }

    private void PersistLocked()
        => ViewDefinitionCodec.Save(CatalogPath, Catalog.Snapshot());
}
