using System.Collections.Frozen;

namespace SonnetDB.Documents;

/// <summary>
/// JSON 文档集合 schema catalog。
/// </summary>
public sealed class DocumentCollectionCatalog
{
    private readonly object _sync = new();
    private readonly Dictionary<string, DocumentCollectionSchema> _mutable = new(StringComparer.Ordinal);
    private FrozenDictionary<string, DocumentCollectionSchema> _snapshot =
        FrozenDictionary<string, DocumentCollectionSchema>.Empty;

    /// <summary>当前文档集合数量。</summary>
    public int Count
    {
        get
        {
            lock (_sync)
                return _mutable.Count;
        }
    }

    /// <summary>
    /// 新增文档集合 schema。
    /// </summary>
    /// <param name="schema">schema。</param>
    public void Add(DocumentCollectionSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        lock (_sync)
        {
            if (_mutable.ContainsKey(schema.Name))
                throw new InvalidOperationException($"document collection '{schema.Name}' 已存在。");
            _mutable.Add(schema.Name, schema);
            PublishSnapshot();
        }
    }

    /// <summary>
    /// 加载或替换文档集合 schema，主要用于启动恢复。
    /// </summary>
    /// <param name="schema">schema。</param>
    public void LoadOrReplace(DocumentCollectionSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        lock (_sync)
        {
            _mutable[schema.Name] = schema;
            PublishSnapshot();
        }
    }

    /// <summary>
    /// 删除文档集合 schema。
    /// </summary>
    /// <param name="name">集合名。</param>
    /// <returns>存在并删除时返回 true。</returns>
    public bool Remove(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_sync)
        {
            if (!_mutable.Remove(name))
                return false;
            PublishSnapshot();
            return true;
        }
    }

    /// <summary>
    /// 尝试按名称查找文档集合 schema。
    /// </summary>
    /// <param name="name">集合名。</param>
    /// <returns>找到时返回 schema；否则返回 null。</returns>
    public DocumentCollectionSchema? TryGet(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Volatile.Read(ref _snapshot).TryGetValue(name, out var schema) ? schema : null;
    }

    /// <summary>
    /// 返回当前 schema 快照，按集合名升序排列。
    /// </summary>
    public IReadOnlyList<DocumentCollectionSchema> Snapshot()
        => Volatile.Read(ref _snapshot).Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToArray();

    private void PublishSnapshot()
        => Volatile.Write(ref _snapshot, _mutable.ToFrozenDictionary(StringComparer.Ordinal));
}
