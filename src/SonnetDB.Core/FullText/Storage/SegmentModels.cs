using SonnetDB.FullText.Index;

namespace SonnetDB.FullText.Storage;

internal sealed class SegmentDocument
{
    private readonly Dictionary<string, string> _fields = new(StringComparer.Ordinal);

    public SegmentDocument(int localId, DocumentId id)
    {
        LocalId = localId;
        Id = id;
    }

    public int LocalId { get; }

    public DocumentId Id { get; }

    public Dictionary<string, string> Fields => _fields;
}

internal sealed class SegmentPostingList
{
    public SegmentPostingList(string field, string term, Dictionary<int, int> postings, Dictionary<int, List<int>>? positions = null)
    {
        Field = field;
        Term = term;
        Postings = postings;
        Positions = positions ?? new Dictionary<int, List<int>>();
    }

    public string Field { get; }

    public string Term { get; }

    public Dictionary<int, int> Postings { get; }

    public Dictionary<int, List<int>> Positions { get; }
}

internal sealed class SegmentData
{
    public SegmentData(long id)
    {
        Id = id;
    }

    public long Id { get; }

    public List<SegmentDocument> Documents { get; } = new();

    public List<SegmentPostingList> PostingLists { get; } = new();

    public Dictionary<string, Dictionary<int, int>> FieldLengths { get; } = new(StringComparer.Ordinal);
}

internal sealed class SegmentReader
{
    private readonly Dictionary<string, Dictionary<string, Dictionary<int, int>>> _postings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, Dictionary<int, List<int>>>> _positions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<int, int>> _fieldLengths = new(StringComparer.Ordinal);
    private readonly Dictionary<int, DocumentId> _documents = new();
    private readonly Dictionary<string, SegmentDocument> _documentSnapshots = new(StringComparer.Ordinal);

    public SegmentReader(SegmentData data, string path, long sizeBytes)
    {
        Id = data.Id;
        Path = path;
        SizeBytes = sizeBytes;

        foreach (SegmentDocument document in data.Documents)
        {
            _documents[document.LocalId] = document.Id;
            _documentSnapshots[document.Id.Value] = document;
        }

        foreach (SegmentPostingList postingList in data.PostingLists)
        {
            Dictionary<string, Dictionary<int, int>> terms = GetOrCreate(_postings, postingList.Field);
            terms[postingList.Term] = postingList.Postings;

            Dictionary<string, Dictionary<int, List<int>>> positions = GetOrCreate(_positions, postingList.Field);
            positions[postingList.Term] = postingList.Positions;
        }

        foreach (KeyValuePair<string, Dictionary<int, int>> fieldLengths in data.FieldLengths)
        {
            _fieldLengths[fieldLengths.Key] = fieldLengths.Value;
        }
    }

    public long Id { get; }

    public string Path { get; }

    public long SizeBytes { get; }

    public int DocumentCount => _documents.Count;

    public IReadOnlyDictionary<int, DocumentId> Documents => _documents;

    public IReadOnlyDictionary<string, SegmentDocument> DocumentSnapshots => _documentSnapshots;

    public IReadOnlyDictionary<string, Dictionary<int, int>> FieldLengths => _fieldLengths;

    public bool TryGetPostings(string field, string term, out Dictionary<int, int> postings)
    {
        postings = null!;
        return _postings.TryGetValue(field, out Dictionary<string, Dictionary<int, int>>? terms)
            && terms.TryGetValue(term, out postings!);
    }

    /// <summary>列出本 segment 在指定字段下索引过的所有 term。</summary>
    public IEnumerable<string> EnumerateTerms(string field)
        => _postings.TryGetValue(field, out Dictionary<string, Dictionary<int, int>>? terms)
            ? terms.Keys
            : Array.Empty<string>();

    public bool TryGetFieldLengths(string field, out Dictionary<int, int> lengths)
    {
        return _fieldLengths.TryGetValue(field, out lengths!);
    }

    public bool TryGetPositions(string field, string term, out Dictionary<int, List<int>> positions)
    {
        positions = null!;
        return _positions.TryGetValue(field, out Dictionary<string, Dictionary<int, List<int>>>? terms)
            && terms.TryGetValue(term, out positions!);
    }

    private static Dictionary<TKey, TValue> GetOrCreate<TKey, TValue>(Dictionary<string, Dictionary<TKey, TValue>> outer, string key)
        where TKey : notnull
    {
        if (!outer.TryGetValue(key, out Dictionary<TKey, TValue>? inner))
        {
            inner = new Dictionary<TKey, TValue>();
            outer[key] = inner;
        }
        return inner;
    }
}
