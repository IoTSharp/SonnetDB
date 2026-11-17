using System.IO.Hashing;
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

    /// <summary>
    /// DSSEG003/DSSEG004 中该词项的 posting block 目录；旧格式读取时为空。
    /// </summary>
    public List<PostingBlockInfo> Blocks { get; } = new();
}

/// <summary>
/// 持久化 posting block 的安全读取目录项。
/// </summary>
internal sealed record PostingBlockInfo(
    long FileOffset,
    int Length,
    int FirstDocumentId,
    int LastDocumentId,
    int MaxTermFrequency,
    int PostingCount,
    uint Crc32);

/// <summary>
/// 按 local document ID 排序的只读 posting 视图。
/// </summary>
internal sealed class PostingListView
{
    public PostingListView(int[] documentIds, int[] termFrequencies)
    {
        DocumentIds = documentIds;
        TermFrequencies = termFrequencies;
    }

    public int[] DocumentIds { get; }

    public int[] TermFrequencies { get; }
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
    private const int MaxCachedPostingViews = 1024;
    private readonly Dictionary<string, Dictionary<string, Dictionary<int, int>>> _postings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, Dictionary<int, List<int>>>> _positions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<int, int>> _fieldLengths = new(StringComparer.Ordinal);
    private readonly Dictionary<int, DocumentId> _documents = new();
    private readonly Dictionary<string, SegmentDocument> _documentSnapshots = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Field, string Term), PostingListView> _postingViews = new();
    private readonly Queue<(string Field, string Term)> _postingViewCacheOrder = new();
    private readonly Dictionary<(string Field, string Term), IReadOnlyList<PostingBlockInfo>> _postingBlocks = new();
    private readonly FullTextBlockCache? _blockCache;
    private readonly bool _lazyPostingBlocks;

    public SegmentReader(
        SegmentData data,
        string path,
        long sizeBytes,
        FullTextBlockCache? blockCache = null,
        bool lazyPostingBlocks = false)
    {
        Id = data.Id;
        Path = path;
        SizeBytes = sizeBytes;
        _blockCache = blockCache;
        _lazyPostingBlocks = lazyPostingBlocks;

        foreach (SegmentDocument document in data.Documents)
        {
            _documents[document.LocalId] = document.Id;
            _documentSnapshots[document.Id.Value] = document;
        }

        foreach (SegmentPostingList postingList in data.PostingLists)
        {
            if (postingList.Blocks.Count > 0)
            {
                _postingBlocks[(postingList.Field, postingList.Term)] = postingList.Blocks.ToArray();
            }

            if (!_lazyPostingBlocks || postingList.Blocks.Count == 0)
            {
                Dictionary<string, Dictionary<int, int>> terms = GetOrCreate(_postings, postingList.Field);
                terms[postingList.Term] = postingList.Postings;

                Dictionary<string, Dictionary<int, List<int>>> positions = GetOrCreate(_positions, postingList.Field);
                positions[postingList.Term] = postingList.Positions;
            }
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
        if (_postings.TryGetValue(field, out Dictionary<string, Dictionary<int, int>>? terms)
            && terms.TryGetValue(term, out postings!))
        {
            return true;
        }

        return _lazyPostingBlocks
            && _postingBlocks.ContainsKey((field, term))
            && LoadPostingList(field, term, out postings);
    }

    /// <summary>
    /// 获取 DSSEG003 posting block 目录。旧 segment 返回 false。
    /// </summary>
    public bool TryGetPostingBlocks(string field, string term, out IReadOnlyList<PostingBlockInfo> blocks)
    {
        return _postingBlocks.TryGetValue((field, term), out blocks!);
    }

    /// <summary>
    /// 按 DSSEG003 block 目录从磁盘读取一个已经 CRC 校验的 posting payload。
    /// </summary>
    /// <param name="field">全文字段。</param>
    /// <param name="term">词项。</param>
    /// <param name="blockIndex">block 在该词项目录中的从零开始索引。</param>
    /// <param name="cache">可选的有界 block 缓存。</param>
    /// <param name="payload">读取后的不可变语义字节快照；调用方不得修改。</param>
    /// <returns>找到 DSSEG003 block 时返回 <see langword="true"/>；旧格式或索引越界时返回 <see langword="false"/>。</returns>
    public bool TryReadPostingBlock(
        string field,
        string term,
        int blockIndex,
        FullTextBlockCache? cache,
        out byte[] payload)
    {
        payload = [];
        if (blockIndex < 0
            || !_postingBlocks.TryGetValue((field, term), out IReadOnlyList<PostingBlockInfo>? blocks)
            || blockIndex >= blocks.Count)
        {
            return false;
        }

        PostingBlockInfo block = blocks[blockIndex];
        var key = new FullTextBlockCacheKey(Id, block.FileOffset, block.Length, block.Crc32);
        FullTextBlockCache? activeCache = cache ?? _blockCache;
        if (activeCache is not null && activeCache.TryGet(key, out byte[]? cached))
        {
            payload = cached;
            return true;
        }

        if (block.FileOffset < 0 || block.Length <= 0)
        {
            throw new InvalidDataException("Posting block directory contains an invalid offset or length.");
        }

        using (FileStream stream = new(Path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (block.FileOffset > stream.Length - block.Length)
            {
                throw new InvalidDataException("Posting block exceeds the segment file boundary.");
            }

            stream.Position = block.FileOffset;
            payload = new byte[block.Length];
            ReadExactly(stream, payload);
        }

        uint actualCrc = Crc32.HashToUInt32(payload);
        if (actualCrc != block.Crc32)
        {
            payload = [];
            throw new InvalidDataException("Posting block CRC32 mismatch.");
        }

        activeCache?.TryAdd(key, payload);
        return true;
    }

    /// <summary>
    /// 获取按 local document ID 排序且可复用的 posting 视图。
    /// </summary>
    public bool TryGetPostingView(string field, string term, out PostingListView view)
    {
        if (_postingViews.TryGetValue((field, term), out view!))
        {
            return true;
        }

        if (!TryGetPostings(field, term, out Dictionary<int, int>? postings))
        {
            view = null!;
            return false;
        }

        int[] documentIds = new int[postings.Count];
        int[] termFrequencies = new int[postings.Count];
        int index = 0;
        foreach (KeyValuePair<int, int> posting in postings.OrderBy(static item => item.Key))
        {
            documentIds[index] = posting.Key;
            termFrequencies[index] = posting.Value;
            index++;
        }

        view = new PostingListView(documentIds, termFrequencies);
        if (_postingViews.Count >= MaxCachedPostingViews)
        {
            (string Field, string Term) evicted = _postingViewCacheOrder.Dequeue();
            _postingViews.Remove(evicted);
        }

        _postingViews[(field, term)] = view;
        _postingViewCacheOrder.Enqueue((field, term));
        return true;
    }

    /// <summary>列出本 segment 在指定字段下索引过的所有 term。</summary>
    public IEnumerable<string> EnumerateTerms(string field)
    {
        if (!_lazyPostingBlocks)
        {
            return _postings.TryGetValue(field, out Dictionary<string, Dictionary<int, int>>? loadedTerms)
                ? loadedTerms.Keys
                : Array.Empty<string>();
        }

        var terms = new HashSet<string>(StringComparer.Ordinal);
        if (_postings.TryGetValue(field, out Dictionary<string, Dictionary<int, int>>? loaded))
        {
            terms.UnionWith(loaded.Keys);
        }

        foreach ((string Field, string Term) key in _postingBlocks.Keys)
        {
            if (string.Equals(key.Field, field, StringComparison.Ordinal))
            {
                terms.Add(key.Term);
            }
        }

        return terms;
    }

    public bool TryGetFieldLengths(string field, out Dictionary<int, int> lengths)
    {
        return _fieldLengths.TryGetValue(field, out lengths!);
    }

    public bool TryGetPositions(string field, string term, out Dictionary<int, List<int>> positions)
    {
        positions = null!;
        if (_positions.TryGetValue(field, out Dictionary<string, Dictionary<int, List<int>>>? positionTerms)
            && positionTerms.TryGetValue(term, out positions!))
        {
            return true;
        }

        return _lazyPostingBlocks
            && _postingBlocks.ContainsKey((field, term))
            && LoadPostingList(field, term, out _)
            && _positions.TryGetValue(field, out positionTerms)
            && positionTerms.TryGetValue(term, out positions!);
    }

    private bool LoadPostingList(string field, string term, out Dictionary<int, int> postings)
    {
        postings = new Dictionary<int, int>();
        if (!_postingBlocks.TryGetValue((field, term), out IReadOnlyList<PostingBlockInfo>? blocks))
        {
            return false;
        }

        var positions = new Dictionary<int, List<int>>();
        for (int blockIndex = 0; blockIndex < blocks.Count; blockIndex++)
        {
            if (!TryReadPostingBlock(field, term, blockIndex, _blockCache, out byte[] payload))
            {
                return false;
            }

            DecodePostingBlock(
                payload,
                postings,
                positions,
                out int firstDocumentId,
                out int lastDocumentId,
                out int maxTermFrequency,
                out int blockPostingCount);
            PostingBlockInfo block = blocks[blockIndex];
            if (block.FirstDocumentId != firstDocumentId
                || block.LastDocumentId != lastDocumentId
                || block.MaxTermFrequency < maxTermFrequency
                || (block.PostingCount > 0 && block.PostingCount != blockPostingCount))
            {
                throw new FormatException("Posting block directory does not match its payload.");
            }
        }

        GetOrCreate(_postings, field)[term] = postings;
        GetOrCreate(_positions, field)[term] = positions;
        return true;
    }

    private static void DecodePostingBlock(
        byte[] payload,
        Dictionary<int, int> postings,
        Dictionary<int, List<int>> positions,
        out int firstDocumentId,
        out int lastDocumentId,
        out int maxTermFrequency,
        out int blockPostingCount)
    {
        using var payloadStream = new MemoryStream(payload, writable: false);
        int previousDocId = 0;
        bool hasPreviousDocId = false;
        firstDocumentId = int.MaxValue;
        lastDocumentId = int.MinValue;
        maxTermFrequency = 0;
        blockPostingCount = 0;
        while (payloadStream.Position < payloadStream.Length)
        {
            int docId = checked(previousDocId + VarInt.Read(payloadStream));
            if (hasPreviousDocId && docId <= previousDocId)
            {
                throw new FormatException("Posting block document IDs are not strictly increasing.");
            }
            int termFrequency = VarInt.Read(payloadStream);
            if (termFrequency <= 0)
            {
                throw new FormatException("Invalid posting term frequency.");
            }

            postings[docId] = termFrequency;
            int positionCount = VarInt.Read(payloadStream);
            if (positionCount < 0)
            {
                throw new FormatException("Invalid posting position count.");
            }

            var values = new List<int>(positionCount);
            int position = 0;
            for (int index = 0; index < positionCount; index++)
            {
                position = checked(position + VarInt.Read(payloadStream));
                values.Add(position);
            }

            positions[docId] = values;
            previousDocId = docId;
            hasPreviousDocId = true;
            firstDocumentId = Math.Min(firstDocumentId, docId);
            lastDocumentId = Math.Max(lastDocumentId, docId);
            maxTermFrequency = Math.Max(maxTermFrequency, termFrequency);
            blockPostingCount++;
        }

        if (firstDocumentId == int.MaxValue)
        {
            throw new FormatException("Posting block payload is empty.");
        }
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

    private static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer[offset..]);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }

            offset += read;
        }
    }
}
