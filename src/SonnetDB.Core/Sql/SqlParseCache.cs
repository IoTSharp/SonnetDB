namespace SonnetDB.Sql;

using SonnetDB.Sql.Ast;

/// <summary>
/// 已解析 SQL 语句 AST 的有界 LRU 缓存（SQL Q7 / #212）。
/// <para>
/// 解析是纯语法过程、与 schema 无关，且 <see cref="SqlStatement"/> AST 为不可变 record，
/// 因此按 SQL 文本缓存已解析结果并跨调用复用是安全的：消除高频轮询同一 query 形状时
/// 每次 <c>Execute</c> 重复 lex+parse 的分配与 CPU。
/// </para>
/// <para>
/// 线程安全：内部单锁保护 LRU（与 <c>BlockDecodeCache</c> 同惯用法）。缓存按条目数上限
/// 淘汰最久未用项，避免大量 ad-hoc 查询把缓存撑到无界。
/// </para>
/// </summary>
internal sealed class SqlParseCache
{
    /// <summary>单条 SQL 文本超过此长度不入缓存（大批量 INSERT 等一次性语句无复用价值，且占内存）。</summary>
    private const int MaxCacheableSqlLength = 8 * 1024;

    private readonly object _lock = new();
    private readonly int _capacity;
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries;
    private readonly LinkedList<Entry> _lru = new();
    private long _hitCount;
    private long _missCount;

    public SqlParseCache(int capacity)
    {
        _capacity = Math.Max(0, capacity);
        _entries = new Dictionary<string, LinkedListNode<Entry>>(_capacity, StringComparer.Ordinal);
    }

    public long HitCount
    {
        get { lock (_lock) return _hitCount; }
    }

    public long MissCount
    {
        get { lock (_lock) return _missCount; }
    }

    public int Count
    {
        get { lock (_lock) return _entries.Count; }
    }

    /// <summary>
    /// 取缓存的单语句 AST；未命中时调用 <paramref name="parse"/> 现场解析并入缓存后返回。
    /// 解析抛出（语法错误）不缓存，异常照常向上传播。
    /// </summary>
    public SqlStatement GetOrParse(string sql, Func<string, SqlStatement> parse)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parse);

        if (_capacity == 0 || sql.Length > MaxCacheableSqlLength)
            return parse(sql);

        lock (_lock)
        {
            if (_entries.TryGetValue(sql, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                _hitCount++;
                return node.Value.Statement;
            }
            _missCount++;
        }

        // 锁外解析：解析可能较重，不在临界区内持锁执行（也避免解析回调重入本缓存造成死锁）。
        var statement = parse(sql);

        lock (_lock)
        {
            if (_entries.TryGetValue(sql, out var existing))
            {
                // 并发下另一线程已插入：复用已在缓存的实例，提升为最近使用。
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return existing.Value.Statement;
            }

            var entry = new Entry(sql, statement);
            var newNode = new LinkedListNode<Entry>(entry);
            _lru.AddFirst(newNode);
            _entries.Add(sql, newNode);

            while (_entries.Count > _capacity && _lru.Last is not null)
            {
                var evict = _lru.Last;
                _lru.RemoveLast();
                _entries.Remove(evict.Value.Sql);
            }
        }

        return statement;
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            _lru.Clear();
        }
    }

    private sealed class Entry
    {
        public Entry(string sql, SqlStatement statement)
        {
            Sql = sql;
            Statement = statement;
        }

        public string Sql { get; }

        public SqlStatement Statement { get; }
    }
}
