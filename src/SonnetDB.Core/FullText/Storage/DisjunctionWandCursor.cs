using SonnetDB.FullText.Scoring;

namespace SonnetDB.FullText.Storage;

/// <summary>
/// 一个 term 在单个 segment 中的 WAND 游标。
/// </summary>
/// <remarks>
/// 当前版本从已加载的 posting 视图计算块上界，不改变旧 segment 文件格式。
/// 后续磁盘块格式可以复用这里的游标和上界合同，直接把 posting 视图替换为按块读取。
/// </remarks>
internal sealed class DisjunctionWandCursor
{
    private const int BlockSize = 128;
    private readonly int[] _documentIds;
    private readonly int[] _termFrequencies;
    private readonly Dictionary<int, int> _fieldLengths;
    private readonly HashSet<int> _tombstones;
    private readonly double[] _blockMaxScores;
    private readonly double _averageDocumentLength;
    private readonly int _documentCount;
    private readonly int _documentFrequency;
    private readonly Bm25Parameters _parameters;
    private readonly double _fieldWeight;
    private int _index;

    public DisjunctionWandCursor(
        PostingListView postings,
        Dictionary<int, int> fieldLengths,
        HashSet<int> tombstones,
        double averageDocumentLength,
        int documentCount,
        int documentFrequency,
        Bm25Parameters parameters,
        double fieldWeight)
    {
        ArgumentNullException.ThrowIfNull(postings);
        ArgumentNullException.ThrowIfNull(fieldLengths);
        ArgumentNullException.ThrowIfNull(tombstones);
        _documentIds = postings.DocumentIds;
        _termFrequencies = postings.TermFrequencies;
        _fieldLengths = fieldLengths;
        _tombstones = tombstones;
        _averageDocumentLength = averageDocumentLength;
        _documentCount = documentCount;
        _documentFrequency = documentFrequency;
        _parameters = parameters;
        _fieldWeight = fieldWeight;
        _blockMaxScores = BuildBlockMaxScores();
        AdvancePastInvisible();
    }

    public bool HasCurrent => _index < _documentIds.Length;

    public int CurrentDocument => HasCurrent ? _documentIds[_index] : int.MaxValue;

    public double CurrentBlockMaxScore
    {
        get
        {
            if (!HasCurrent)
            {
                return 0.0;
            }

            return _blockMaxScores[_index / BlockSize];
        }
    }

    public double CurrentScore
    {
        get
        {
            if (!HasCurrent)
            {
                return 0.0;
            }

            int documentLength = _fieldLengths.TryGetValue(_documentIds[_index], out int length) ? length : 0;
            return Bm25.Score(
                _termFrequencies[_index],
                documentLength,
                _averageDocumentLength,
                _documentCount,
                _documentFrequency,
                _parameters) * _fieldWeight;
        }
    }

    public void AdvanceTo(int targetDocument)
    {
        while (HasCurrent && _documentIds[_index] < targetDocument)
        {
            _index++;
        }

        AdvancePastInvisible();
    }

    public void Advance()
    {
        if (HasCurrent)
        {
            _index++;
            AdvancePastInvisible();
        }
    }

    private double[] BuildBlockMaxScores()
    {
        int blockCount = (_documentIds.Length + BlockSize - 1) / BlockSize;
        var result = new double[blockCount];
        for (int index = 0; index < _documentIds.Length; index++)
        {
            if (_tombstones.Contains(_documentIds[index]))
            {
                continue;
            }

            int documentLength = _fieldLengths.TryGetValue(_documentIds[index], out int length) ? length : 0;
            double score = Bm25.Score(
                _termFrequencies[index],
                documentLength,
                _averageDocumentLength,
                _documentCount,
                _documentFrequency,
                _parameters) * _fieldWeight;
            int block = index / BlockSize;
            if (score > result[block])
            {
                result[block] = score;
            }
        }

        return result;
    }

    private void AdvancePastInvisible()
    {
        while (HasCurrent && _tombstones.Contains(_documentIds[_index]))
        {
            _index++;
        }
    }
}
