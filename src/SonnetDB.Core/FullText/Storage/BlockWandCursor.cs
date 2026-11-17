namespace SonnetDB.FullText.Storage;

/// <summary>
/// 一个 posting block 的安全 WAND 元数据。
/// </summary>
/// <param name="FirstDocumentId">block 中第一个可能出现的文档 ID。</param>
/// <param name="LastDocumentId">block 中最后一个可能出现的文档 ID。</param>
/// <param name="PostingCount">block 中 posting 数量。</param>
/// <param name="MaxScoreUpperBound">该 block 对单个查询词贡献的安全最高分数上界。</param>
internal readonly record struct BlockWandBlockMetadata(
    int FirstDocumentId,
    int LastDocumentId,
    int PostingCount,
    double MaxScoreUpperBound)
{
    public bool IsValid
        => FirstDocumentId >= 0
            && LastDocumentId >= FirstDocumentId
            && PostingCount > 0
            && double.IsFinite(MaxScoreUpperBound)
            && MaxScoreUpperBound >= 0;
}

/// <summary>
/// block-level WAND 的一个 term 游标。
/// </summary>
/// <remarks>
/// 未达到 Top-K 阈值时，游标只使用 block 元数据推进，不读取 block payload。
/// 只有需要确定当前候选文档时，才调用 <paramref name="readBlock"/>。
/// </remarks>
internal sealed class BlockWandCursor
{
    private readonly IReadOnlyList<BlockWandBlockMetadata> _blocks;
    private readonly Func<int, IReadOnlyList<BlockWandPosting>> _readBlock;
    private readonly Action? _onBlockRead;
    private int _blockIndex;
    private IReadOnlyList<BlockWandPosting>? _currentPostings;
    private int _postingIndex;

    public BlockWandCursor(
        IReadOnlyList<BlockWandBlockMetadata> blocks,
        Func<int, IReadOnlyList<BlockWandPosting>> readBlock,
        Action? onBlockRead = null)
    {
        ArgumentNullException.ThrowIfNull(blocks);
        ArgumentNullException.ThrowIfNull(readBlock);
        if (blocks.Count == 0)
        {
            throw new ArgumentException("A WAND cursor requires at least one block.", nameof(blocks));
        }

        for (int index = 0; index < blocks.Count; index++)
        {
            if (!blocks[index].IsValid)
            {
                throw new ArgumentException($"Block metadata at index {index} is invalid.", nameof(blocks));
            }

            if (index > 0 && blocks[index - 1].LastDocumentId >= blocks[index].FirstDocumentId)
            {
                throw new ArgumentException("WAND blocks must have strictly increasing document ranges.", nameof(blocks));
            }
        }

        _blocks = blocks;
        _readBlock = readBlock;
        _onBlockRead = onBlockRead;
    }

    public bool HasCurrent => _blockIndex < _blocks.Count;

    /// <summary>
    /// 当前候选文档下界；payload 未读取时使用 block 首文档，已读取时使用当前 posting。
    /// </summary>
    public int LowerBoundDocument
    {
        get
        {
            if (!HasCurrent)
            {
                return int.MaxValue;
            }

            return _currentPostings is { } postings && _postingIndex < postings.Count
                ? postings[_postingIndex].DocumentId
                : _blocks[_blockIndex].FirstDocumentId;
        }
    }

    /// <summary>当前 block 的安全分数上界。</summary>
    public double CurrentBlockMaxScore
        => HasCurrent ? _blocks[_blockIndex].MaxScoreUpperBound : 0.0;

    /// <summary>当前已加载 posting 的文档 ID。</summary>
    public int CurrentDocument
    {
        get
        {
            EnsureCurrentLoaded();
            return HasCurrent ? _currentPostings![_postingIndex].DocumentId : int.MaxValue;
        }
    }

    /// <summary>当前已加载 posting 的分数。</summary>
    public double CurrentScore
    {
        get
        {
            EnsureCurrentLoaded();
            return HasCurrent ? _currentPostings![_postingIndex].Score : 0.0;
        }
    }

    /// <summary>
    /// 将游标推进到指定文档 ID；完整跳过范围外 block 时不会读取 payload。
    /// </summary>
    public void AdvanceTo(int targetDocumentId)
    {
        while (HasCurrent)
        {
            BlockWandBlockMetadata block = _blocks[_blockIndex];
            if (block.LastDocumentId < targetDocumentId)
            {
                MoveToNextBlock();
                continue;
            }

            if (targetDocumentId <= block.FirstDocumentId)
            {
                return;
            }

            EnsureCurrentLoaded();
            while (HasCurrent && _postingIndex < _currentPostings!.Count
                && _currentPostings[_postingIndex].DocumentId < targetDocumentId)
            {
                _postingIndex++;
            }

            if (_postingIndex < _currentPostings!.Count)
            {
                return;
            }

            MoveToNextBlock();
        }
    }

    /// <summary>推进一个 posting；block 末尾时转到下一个 block。</summary>
    public void Advance()
    {
        if (!HasCurrent)
        {
            return;
        }

        EnsureCurrentLoaded();
        _postingIndex++;
        if (_postingIndex >= _currentPostings!.Count)
        {
            MoveToNextBlock();
        }
    }

    /// <summary>确保当前 block 已加载，并将空 block/尾部 block 清除。</summary>
    public void EnsureCurrentLoaded()
    {
        while (HasCurrent)
        {
            if (_currentPostings is null)
            {
                IReadOnlyList<BlockWandPosting> postings = _readBlock(_blockIndex);
                ArgumentNullException.ThrowIfNull(postings);
                if (postings.Count == 0 || postings.Count > _blocks[_blockIndex].PostingCount)
                {
                    throw new InvalidDataException("WAND block payload count is outside its metadata bound.");
                }

                int previous = int.MinValue;
                for (int index = 0; index < postings.Count; index++)
                {
                    BlockWandPosting posting = postings[index];
                    if (posting.DocumentId <= previous
                        || posting.DocumentId < _blocks[_blockIndex].FirstDocumentId
                        || posting.DocumentId > _blocks[_blockIndex].LastDocumentId
                        || !double.IsFinite(posting.Score)
                        || posting.Score < 0)
                    {
                        throw new InvalidDataException("WAND block payload is not sorted or exceeds its metadata.");
                    }

                    previous = posting.DocumentId;
                }

                _currentPostings = postings;
                _postingIndex = 0;
                _onBlockRead?.Invoke();
            }

            if (_postingIndex < _currentPostings.Count)
            {
                return;
            }

            MoveToNextBlock();
        }
    }

    private void MoveToNextBlock()
    {
        _blockIndex++;
        _currentPostings = null;
        _postingIndex = 0;
    }
}

/// <summary>WAND block 中一个已经计算好的 posting。</summary>
internal readonly record struct BlockWandPosting(int DocumentId, double Score);

/// <summary>一个 block-level WAND 执行结果。</summary>
internal readonly record struct BlockWandHit(int DocumentId, double Score);

/// <summary>
/// 对多个 term block 游标执行安全的 disjunction WAND。
/// </summary>
internal static class BlockWandExecutor
{
    public static IReadOnlyList<BlockWandHit> Search(
        IReadOnlyList<BlockWandCursor> cursors,
        int topK,
        CancellationToken cancellationToken = default,
        Action? onPostingVisit = null)
    {
        ArgumentNullException.ThrowIfNull(cursors);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);
        if (cursors.Count == 0)
        {
            return [];
        }

        var heap = new BlockWandTopKHeap(topK);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var active = cursors.Where(static cursor => cursor.HasCurrent).ToList();
            if (active.Count == 0)
            {
                break;
            }

            active.Sort(static (left, right) => left.LowerBoundDocument.CompareTo(right.LowerBoundDocument));
            double threshold = heap.MinimumScore;
            double upperBound = 0.0;
            int pivot = int.MaxValue;
            foreach (BlockWandCursor cursor in active)
            {
                upperBound += cursor.CurrentBlockMaxScore;
                if (upperBound >= threshold)
                {
                    pivot = cursor.LowerBoundDocument;
                    break;
                }
            }

            if (pivot == int.MaxValue)
            {
                break;
            }

            if (active[0].LowerBoundDocument < pivot)
            {
                foreach (BlockWandCursor cursor in active)
                {
                    if (cursor.LowerBoundDocument < pivot)
                    {
                        cursor.AdvanceTo(pivot);
                    }
                }

                continue;
            }

            int candidate = pivot;
            bool candidateExists = true;
            double score = 0.0;
            foreach (BlockWandCursor cursor in active)
            {
                if (cursor.LowerBoundDocument != candidate)
                {
                    continue;
                }

                cursor.EnsureCurrentLoaded();
                onPostingVisit?.Invoke();
                if (!cursor.HasCurrent || cursor.CurrentDocument != candidate)
                {
                    candidateExists = false;
                    continue;
                }

                score += cursor.CurrentScore;
            }

            if (candidateExists && score > 0.0)
            {
                heap.Add(new BlockWandHit(candidate, score));
            }

            foreach (BlockWandCursor cursor in active)
            {
                if (cursor.HasCurrent && cursor.LowerBoundDocument <= candidate)
                {
                    cursor.AdvanceTo(candidate + 1);
                }
            }
        }

        return heap.ToSortedList();
    }

    private sealed class BlockWandTopKHeap
    {
        private readonly List<BlockWandHit> _items = new();
        private readonly int _capacity;

        public BlockWandTopKHeap(int capacity) => _capacity = capacity;

        public double MinimumScore => _items.Count < _capacity ? double.NegativeInfinity : _items[0].Score;

        public void Add(BlockWandHit hit)
        {
            if (_items.Count < _capacity)
            {
                _items.Add(hit);
                SiftUp(_items.Count - 1);
            }
            else if (IsBetter(hit, _items[0]))
            {
                _items[0] = hit;
                SiftDown(0);
            }
        }

        public IReadOnlyList<BlockWandHit> ToSortedList()
        {
            _items.Sort(static (left, right) =>
            {
                int score = right.Score.CompareTo(left.Score);
                return score != 0 ? score : left.DocumentId.CompareTo(right.DocumentId);
            });
            return _items;
        }

        private static bool IsWorse(BlockWandHit left, BlockWandHit right)
        {
            int score = left.Score.CompareTo(right.Score);
            return score != 0 ? score < 0 : left.DocumentId > right.DocumentId;
        }

        private static bool IsBetter(BlockWandHit left, BlockWandHit right) => IsWorse(right, left);

        private void SiftUp(int index)
        {
            while (index > 0)
            {
                int parent = (index - 1) / 2;
                if (!IsWorse(_items[index], _items[parent]))
                    break;
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
                    return;
                int right = left + 1;
                int worst = right < _items.Count && IsWorse(_items[right], _items[left]) ? right : left;
                if (!IsWorse(_items[worst], _items[index]))
                    return;
                (_items[index], _items[worst]) = (_items[worst], _items[index]);
                index = worst;
            }
        }
    }
}
