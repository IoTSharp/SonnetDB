namespace SonnetDB.FullText.Index;

/// <summary>
/// 有界的全文 Top-K 命中堆。
/// </summary>
/// <remarks>
/// 根节点始终是当前最差命中：分数更低的更差；分数相同时文档 ID 更大的更差。
/// 这样可以在候选数远大于 K 时避免保留并排序完整命中集合。
/// </remarks>
internal sealed class TopKSearchHitHeap
{
    private readonly List<SearchHit> _items;
    private readonly int _capacity;

    public TopKSearchHitHeap(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _capacity = capacity;
        _items = new List<SearchHit>(capacity);
    }

    public int Count => _items.Count;

    public double MinimumScore => _items.Count < _capacity ? double.NegativeInfinity : _items[0].Score;

    public void Add(SearchHit hit)
    {
        if (_items.Count < _capacity)
        {
            _items.Add(hit);
            SiftUp(_items.Count - 1);
            return;
        }

        if (!IsBetter(hit, _items[0]))
        {
            return;
        }

        _items[0] = hit;
        SiftDown(0);
    }

    public IReadOnlyList<SearchHit> ToSortedList()
    {
        _items.Sort(static (left, right) =>
        {
            int scoreCompare = right.Score.CompareTo(left.Score);
            return scoreCompare != 0
                ? scoreCompare
                : string.CompareOrdinal(left.DocumentId.Value, right.DocumentId.Value);
        });
        return _items;
    }

    private static bool IsWorse(SearchHit left, SearchHit right)
    {
        int scoreCompare = left.Score.CompareTo(right.Score);
        return scoreCompare != 0
            ? scoreCompare < 0
            : string.CompareOrdinal(left.DocumentId.Value, right.DocumentId.Value) > 0;
    }

    private static bool IsBetter(SearchHit left, SearchHit right)
        => IsWorse(right, left);

    private void SiftUp(int index)
    {
        while (index > 0)
        {
            int parent = (index - 1) / 2;
            if (!IsWorse(_items[index], _items[parent]))
            {
                break;
            }

            (_items[index], _items[parent]) = (_items[parent], _items[index]);
            index = parent;
        }
    }

    private void SiftDown(int index)
    {
        while (true)
        {
            int left = (index * 2) + 1;
            if (left >= _items.Count)
            {
                return;
            }

            int right = left + 1;
            int worst = right < _items.Count && IsWorse(_items[right], _items[left])
                ? right
                : left;
            if (!IsWorse(_items[worst], _items[index]))
            {
                return;
            }

            (_items[index], _items[worst]) = (_items[worst], _items[index]);
            index = worst;
        }
    }
}
