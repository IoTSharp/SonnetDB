using SonnetDB.FullText.Index;
using SonnetDB.FullText.Scoring;
using SonnetDB.FullText.Tokenization;

namespace SonnetDB.FullText.Storage;

/// <summary>
/// 单目录持久化全文索引。每次写入生成一个不可变段，通过 manifest 记录活动段与 tombstone。
/// </summary>
public sealed class PersistentFullTextIndex : IFullTextIndex, IIndexStorage
{
    private readonly System.Threading.Lock _lock = new();
    private readonly ITokenizer _tokenizer;
    private readonly Bm25Parameters _bm25;
    private readonly Bm25FOptions _bm25f;
    private readonly PersistentIndexOptions _options;
    private readonly string _segmentsDirectory;
    private readonly Dictionary<long, SegmentReader> _segments = new();
    private readonly Dictionary<long, HashSet<int>> _tombstones = new();
    private readonly Dictionary<string, (long SegmentId, int LocalId)> _liveDocuments = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MutableFieldStats> _fieldStats = new(StringComparer.Ordinal);
    private readonly HashSet<long> _dirtyTombstoneSegments = new();

    private IndexManifest _manifest;
    private bool _mergeScheduled;
    private Task? _backgroundMergeTask;

    private PersistentFullTextIndex(
        string directory,
        ITokenizer tokenizer,
        Bm25Parameters? bm25,
        PersistentIndexOptions? options)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(tokenizer);

        Directory = Path.GetFullPath(directory);
        _segmentsDirectory = Path.Combine(Directory, "segments");
        _tokenizer = tokenizer;
        _bm25 = bm25 ?? Bm25Parameters.Default;
        _bm25f = options?.Bm25F ?? Bm25FOptions.Default;
        _options = options ?? new PersistentIndexOptions();

        System.IO.Directory.CreateDirectory(Directory);
        System.IO.Directory.CreateDirectory(_segmentsDirectory);
        _manifest = ManifestFile.LoadOrCreate(Directory);
        LoadSegments();
        RebuildLiveDocuments();
        RebuildFieldStats();
    }

    /// <inheritdoc />
    public string Directory { get; }

    /// <inheritdoc />
    public int DocumentCount
    {
        get
        {
            lock (_lock)
            {
                return _liveDocuments.Count;
            }
        }
    }

    /// <summary>
    /// 打开或创建一个持久化索引目录。
    /// </summary>
    public static PersistentFullTextIndex Open(
        string directory,
        ITokenizer tokenizer,
        Bm25Parameters? bm25 = null,
        PersistentIndexOptions? options = null)
    {
        return new PersistentFullTextIndex(directory, tokenizer, bm25, options);
    }

    /// <inheritdoc />
    public void Index(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        IndexMany([document]);
    }

    /// <summary>
    /// 批量写入或更新一组文档：整批构建为单个不可变段，manifest 只保存一次。
    /// 批内相同 ID 以最后一次出现为准（last-write-wins），与逐条 <see cref="Index"/> 语义一致。
    /// </summary>
    public void IndexMany(IReadOnlyList<Document> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        if (documents.Count == 0)
        {
            return;
        }

        IReadOnlyList<Document> unique = DeduplicateLastWins(documents);

        lock (_lock)
        {
            foreach (Document document in unique)
            {
                TombstoneExisting(document.Id.Value);
            }

            long segmentId = _manifest.NextSegmentId++;
            SegmentData data = BuildSegment(segmentId, unique);
            SegmentReader reader = SegmentFile.Write(_segmentsDirectory, data);

            _segments[segmentId] = reader;
            _tombstones[segmentId] = new HashSet<int>();
            _manifest.ActiveSegments.Add(new SegmentManifestEntry
            {
                Id = segmentId,
                DocCount = reader.DocumentCount,
                SizeBytes = reader.SizeBytes,
            });
            _manifest.Tombstones[segmentId.ToString()] = new List<int>();
            for (int localId = 0; localId < unique.Count; localId++)
            {
                _liveDocuments[unique[localId].Id.Value] = (segmentId, localId);
            }
            AddSegmentFieldStats(reader, tombstones: null);

            SaveManifest();
            ScheduleBackgroundMergeIfNeeded();
        }
    }

    /// <inheritdoc />
    public bool Delete(DocumentId id)
    {
        lock (_lock)
        {
            if (!TombstoneExisting(id.Value))
            {
                return false;
            }

            SaveManifest();
            return true;
        }
    }

    /// <summary>
    /// 批量删除一组文档，manifest 只保存一次。
    /// </summary>
    /// <returns>实际删除的文档数。</returns>
    public int DeleteMany(IEnumerable<DocumentId> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        lock (_lock)
        {
            int deleted = 0;
            foreach (DocumentId id in ids)
            {
                if (TombstoneExisting(id.Value))
                {
                    deleted++;
                }
            }

            if (deleted > 0)
            {
                SaveManifest();
            }
            return deleted;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<SearchHit> Search(Query.Query query, int topK)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (topK <= 0)
        {
            return Array.Empty<SearchHit>();
        }

        lock (_lock)
        {
            Dictionary<string, double> scores = new(StringComparer.Ordinal);
            Score(query, scores);

            if (scores.Count == 0)
            {
                return Array.Empty<SearchHit>();
            }

            List<SearchHit> hits = new(scores.Count);
            foreach (KeyValuePair<string, double> kv in scores)
            {
                if (_liveDocuments.ContainsKey(kv.Key))
                {
                    hits.Add(new SearchHit(new DocumentId(kv.Key), kv.Value));
                }
            }

            hits.Sort(static (a, b) =>
            {
                int scoreCompare = b.Score.CompareTo(a.Score);
                return scoreCompare != 0
                    ? scoreCompare
                    : string.CompareOrdinal(a.DocumentId.Value, b.DocumentId.Value);
            });
            if (hits.Count > topK)
            {
                hits.RemoveRange(topK, hits.Count - topK);
            }
            return hits;
        }
    }

    /// <summary>
    /// 枚举指定字段下所有活跃 segment 中出现过的 term，用于实现 fuzzy / wildcard 这类需要
    /// 在索引侧做 term 展开的查询。返回的是去重后的快照，与并发写互斥。
    /// 注意：由于不依赖 tombstone 信息，被全部 tombstone 的 term 也会出现在结果里——
    /// 调用方在收集到候选 term 后通过 <see cref="Search(Query.Query, int)"/> 二次过滤即可。
    /// </summary>
    /// <param name="field">字段名。</param>
    public IReadOnlyCollection<string> EnumerateTerms(string field)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(field);

        lock (_lock)
        {
            HashSet<string> terms = new(StringComparer.Ordinal);
            foreach (SegmentReader segment in _segments.Values)
            {
                foreach (string term in segment.EnumerateTerms(field))
                    terms.Add(term);
            }
            return terms;
        }
    }

    /// <summary>
    /// 同步执行一次段合并，将当前可见文档重写为单个不可变段。
    /// </summary>
    /// <returns>实际是否发生合并。</returns>
    public bool MergeSegments()
    {
        lock (_lock)
        {
            _mergeScheduled = false;
            if (_manifest.ActiveSegments.Count <= 1)
            {
                return false;
            }

            List<Document> liveDocuments = SnapshotLiveDocuments();
            List<string> oldPaths = _segments.Values.Select(static x => x.Path).ToList();

            _segments.Clear();
            _tombstones.Clear();
            _liveDocuments.Clear();
            _fieldStats.Clear();
            _dirtyTombstoneSegments.Clear();
            _manifest.ActiveSegments.Clear();
            _manifest.Tombstones.Clear();
            _manifest.UpdatedDocumentIds.Clear();

            if (liveDocuments.Count > 0)
            {
                long segmentId = _manifest.NextSegmentId++;
                SegmentData data = BuildSegment(segmentId, liveDocuments);
                SegmentReader reader = SegmentFile.Write(_segmentsDirectory, data);
                _segments[segmentId] = reader;
                _tombstones[segmentId] = new HashSet<int>();
                _manifest.ActiveSegments.Add(new SegmentManifestEntry
                {
                    Id = segmentId,
                    DocCount = reader.DocumentCount,
                    SizeBytes = reader.SizeBytes,
                });
                _manifest.Tombstones[segmentId.ToString()] = new List<int>();

                foreach (SegmentDocument document in reader.DocumentSnapshots.Values)
                {
                    _liveDocuments[document.Id.Value] = (segmentId, document.LocalId);
                }
                AddSegmentFieldStats(reader, tombstones: null);
            }

            SaveManifest();
            DeleteOldSegments(oldPaths);
            return true;
        }
    }

    internal bool WaitForBackgroundMerge(TimeSpan timeout)
    {
        Task? task;
        lock (_lock)
        {
            task = _backgroundMergeTask;
        }

        return task is null || task.Wait(timeout);
    }

    private void LoadSegments()
    {
        foreach (SegmentManifestEntry entry in _manifest.ActiveSegments)
        {
            string path = SegmentFile.GetPath(_segmentsDirectory, entry.Id);
            SegmentReader reader = SegmentFile.Read(path);
            _segments[entry.Id] = reader;

            if (_manifest.Tombstones.TryGetValue(entry.Id.ToString(), out List<int>? tombstones))
            {
                _tombstones[entry.Id] = new HashSet<int>(tombstones);
            }
            else
            {
                _tombstones[entry.Id] = new HashSet<int>();
                _manifest.Tombstones[entry.Id.ToString()] = new List<int>();
            }
        }
    }

    private void RebuildLiveDocuments()
    {
        foreach (SegmentManifestEntry entry in _manifest.ActiveSegments.OrderBy(static x => x.Id))
        {
            SegmentReader segment = _segments[entry.Id];
            HashSet<int> tombstones = _tombstones[entry.Id];
            foreach (KeyValuePair<int, DocumentId> document in segment.Documents)
            {
                if (!tombstones.Contains(document.Key))
                {
                    _liveDocuments[document.Value.Value] = (entry.Id, document.Key);
                }
            }
        }
    }

    private bool TombstoneExisting(string documentId)
    {
        if (!_liveDocuments.TryGetValue(documentId, out (long SegmentId, int LocalId) existing))
        {
            return false;
        }

        HashSet<int> tombstones = _tombstones[existing.SegmentId];
        tombstones.Add(existing.LocalId);
        _dirtyTombstoneSegments.Add(existing.SegmentId);
        _manifest.UpdatedDocumentIds[documentId] = existing.SegmentId;
        _liveDocuments.Remove(documentId);
        RemoveDocumentFieldStats(existing.SegmentId, existing.LocalId);
        return true;
    }

    private SegmentData BuildSegment(long id, IReadOnlyList<Document> documents)
    {
        SegmentData data = new(id);
        Dictionary<string, Dictionary<string, Dictionary<int, int>>> postings = new(StringComparer.Ordinal);
        Dictionary<string, Dictionary<string, Dictionary<int, List<int>>>> positions = new(StringComparer.Ordinal);
        CollectingTokenSink sink = new();

        for (int localId = 0; localId < documents.Count; localId++)
        {
            Document source = documents[localId];
            SegmentDocument target = new(localId, source.Id);
            data.Documents.Add(target);

            foreach (KeyValuePair<string, string> field in source.Fields)
            {
                target.Fields[field.Key] = field.Value;
                sink.Clear();
                _tokenizer.Tokenize(field.Value.AsSpan(), sink);

                int length = 0;
                int position = -1;
                Dictionary<string, Dictionary<int, int>> terms = GetOrCreate(postings, field.Key);
                Dictionary<string, Dictionary<int, List<int>>> termPositionsByField = GetOrCreate(positions, field.Key);
                foreach (Token token in sink.Tokens)
                {
                    int increment = token.PositionIncrement;
                    if (increment > 0)
                    {
                        position += increment;
                    }
                    else if (length == 0)
                    {
                        position = 0;
                    }

                    length++;
                    Dictionary<int, int> docs = GetOrCreate(terms, token.Text);
                    docs[localId] = docs.TryGetValue(localId, out int tf) ? tf + 1 : 1;

                    Dictionary<int, List<int>> termPositions = GetOrCreate(termPositionsByField, token.Text);
                    if (!termPositions.TryGetValue(localId, out List<int>? values))
                    {
                        values = new List<int>();
                        termPositions[localId] = values;
                    }
                    values.Add(position);
                }

                Dictionary<int, int> fieldLengths = GetOrCreate(data.FieldLengths, field.Key);
                fieldLengths[localId] = length;
            }
        }

        foreach (KeyValuePair<string, Dictionary<string, Dictionary<int, int>>> field in postings)
        {
            foreach (KeyValuePair<string, Dictionary<int, int>> term in field.Value)
            {
                Dictionary<int, List<int>> termPositions = positions[field.Key][term.Key];
                data.PostingLists.Add(new SegmentPostingList(field.Key, term.Key, term.Value, termPositions));
            }
        }

        return data;
    }

    private void Score(Query.Query query, Dictionary<string, double> scores)
    {
        switch (query)
        {
            case Query.TermQuery term:
                ScoreTerm(term, scores);
                break;
            case Query.PhraseQuery phrase:
                ScorePhrase(phrase, scores);
                break;
            case Query.NearQuery near:
                ScoreNear(near, scores);
                break;
            case Query.OrQuery or:
                foreach (Query.Query clause in or.Clauses)
                {
                    Score(clause, scores);
                }
                break;
            case Query.AndQuery and:
                ScoreAnd(and, scores);
                break;
            default:
                throw new NotSupportedException($"Unsupported query node: {query.GetType().Name}");
        }
    }

    private void ScoreTerm(Query.TermQuery term, Dictionary<string, double> scores)
    {
        int liveDocumentCount = _liveDocuments.Count;
        if (liveDocumentCount == 0)
        {
            return;
        }

        FieldStats stats = GetFieldStats(term.Field);
        if (stats.DocumentCount == 0)
        {
            return;
        }

        List<(SegmentReader Segment, Dictionary<int, int> Postings, HashSet<int> Tombstones)> matchingSegments = new();
        int documentFrequency = 0;
        foreach (SegmentReader segment in _segments.Values)
        {
            if (!segment.TryGetPostings(term.Field, term.Term, out Dictionary<int, int>? postings))
            {
                continue;
            }

            HashSet<int> tombstones = _tombstones[segment.Id];
            int livePostings = 0;
            foreach (int localId in postings.Keys)
            {
                if (!tombstones.Contains(localId))
                {
                    livePostings++;
                }
            }

            if (livePostings > 0)
            {
                documentFrequency += livePostings;
                matchingSegments.Add((segment, postings, tombstones));
            }
        }

        if (documentFrequency == 0)
        {
            return;
        }

        double averageLength = (double)stats.TotalLength / stats.DocumentCount;
        foreach ((SegmentReader segment, Dictionary<int, int> postings, HashSet<int> tombstones) in matchingSegments)
        {
            if (!segment.TryGetFieldLengths(term.Field, out Dictionary<int, int>? lengths))
            {
                continue;
            }

            foreach (KeyValuePair<int, int> posting in postings)
            {
                if (tombstones.Contains(posting.Key) || !segment.Documents.TryGetValue(posting.Key, out DocumentId documentId))
                {
                    continue;
                }

                int documentLength = lengths.TryGetValue(posting.Key, out int len) ? len : 0;
                double score = Bm25.Score(posting.Value, documentLength, averageLength, liveDocumentCount, documentFrequency, _bm25)
                    * _bm25f.GetWeight(term.Field);
                scores[documentId.Value] = scores.TryGetValue(documentId.Value, out double existing) ? existing + score : score;
            }
        }
    }

    private void ScorePhrase(Query.PhraseQuery phrase, Dictionary<string, double> scores)
    {
        if (phrase.Terms.Count == 1)
        {
            ScoreTerm(new Query.TermQuery(phrase.Field, phrase.Terms[0]), scores);
            return;
        }

        ScorePositional(phrase.Field, phrase.Terms, scores, PositionMatcher.CountPhraseMatches);
    }

    private void ScoreNear(Query.NearQuery near, Dictionary<string, double> scores)
    {
        if (near.Terms.Count == 1)
        {
            ScoreTerm(new Query.TermQuery(near.Field, near.Terms[0]), scores);
            return;
        }

        ScorePositional(
            near.Field,
            near.Terms,
            scores,
            lists => PositionMatcher.CountNearMatches(lists, near.MaxDistance, near.InOrder));
    }

    private void ScorePositional(
        string field,
        IReadOnlyList<string> terms,
        Dictionary<string, double> scores,
        Func<List<int>[], int> matchCounter)
    {
        int liveDocumentCount = _liveDocuments.Count;
        if (liveDocumentCount == 0)
        {
            return;
        }

        FieldStats stats = GetFieldStats(field);
        if (stats.DocumentCount == 0)
        {
            return;
        }

        Dictionary<string, (int Count, int Length)> matches = new(StringComparer.Ordinal);
        foreach (SegmentReader segment in _segments.Values)
        {
            Dictionary<int, List<int>>[] postings = new Dictionary<int, List<int>>[terms.Count];
            bool segmentHasAllTerms = true;
            for (int i = 0; i < terms.Count; i++)
            {
                if (!segment.TryGetPositions(field, terms[i], out Dictionary<int, List<int>>? termPositions))
                {
                    segmentHasAllTerms = false;
                    break;
                }
                postings[i] = termPositions;
            }

            if (!segmentHasAllTerms || !segment.TryGetFieldLengths(field, out Dictionary<int, int>? lengths))
            {
                continue;
            }

            HashSet<int> tombstones = _tombstones[segment.Id];
            foreach (int localId in postings[0].Keys)
            {
                if (tombstones.Contains(localId) || !segment.Documents.TryGetValue(localId, out DocumentId documentId))
                {
                    continue;
                }

                List<int>[] lists = new List<int>[postings.Length];
                lists[0] = postings[0][localId];
                bool allTermsMatch = true;
                for (int i = 1; i < postings.Length; i++)
                {
                    if (!postings[i].TryGetValue(localId, out List<int>? values))
                    {
                        allTermsMatch = false;
                        break;
                    }
                    lists[i] = values;
                }

                if (!allTermsMatch)
                {
                    continue;
                }

                int count = matchCounter(lists);
                if (count > 0)
                {
                    int length = lengths.TryGetValue(localId, out int len) ? len : 0;
                    matches[documentId.Value] = (count, length);
                }
            }
        }

        if (matches.Count == 0)
        {
            return;
        }

        double averageLength = (double)stats.TotalLength / stats.DocumentCount;
        int documentFrequency = matches.Count;
        double weight = _bm25f.GetWeight(field);
        foreach (KeyValuePair<string, (int Count, int Length)> match in matches)
        {
            double score = Bm25.Score(match.Value.Count, match.Value.Length, averageLength, liveDocumentCount, documentFrequency, _bm25) * weight;
            scores[match.Key] = scores.TryGetValue(match.Key, out double existing) ? existing + score : score;
        }
    }

    private void ScoreAnd(Query.AndQuery and, Dictionary<string, double> scores)
    {
        if (and.Clauses.Count == 0)
        {
            return;
        }

        Dictionary<string, double> first = new(StringComparer.Ordinal);
        Score(and.Clauses[0], first);

        for (int i = 1; i < and.Clauses.Count; i++)
        {
            Dictionary<string, double> next = new(StringComparer.Ordinal);
            Score(and.Clauses[i], next);

            Dictionary<string, double> merged = new(StringComparer.Ordinal);
            foreach (KeyValuePair<string, double> kv in first)
            {
                if (next.TryGetValue(kv.Key, out double s))
                {
                    merged[kv.Key] = kv.Value + s;
                }
            }
            first = merged;
            if (first.Count == 0)
            {
                return;
            }
        }

        foreach (KeyValuePair<string, double> kv in first)
        {
            scores[kv.Key] = scores.TryGetValue(kv.Key, out double existing) ? existing + kv.Value : kv.Value;
        }
    }

    private FieldStats GetFieldStats(string field)
    {
        return _fieldStats.TryGetValue(field, out MutableFieldStats? stats)
            ? new FieldStats(stats.DocumentCount, stats.TotalLength)
            : new FieldStats(0, 0);
    }

    private void RebuildFieldStats()
    {
        _fieldStats.Clear();
        foreach (SegmentReader segment in _segments.Values)
        {
            AddSegmentFieldStats(segment, _tombstones[segment.Id]);
        }
    }

    private void AddSegmentFieldStats(SegmentReader segment, HashSet<int>? tombstones)
    {
        foreach (KeyValuePair<string, Dictionary<int, int>> field in segment.FieldLengths)
        {
            if (!_fieldStats.TryGetValue(field.Key, out MutableFieldStats? stats))
            {
                stats = new MutableFieldStats();
                _fieldStats[field.Key] = stats;
            }

            foreach (KeyValuePair<int, int> length in field.Value)
            {
                if (tombstones is null || !tombstones.Contains(length.Key))
                {
                    stats.DocumentCount++;
                    stats.TotalLength += length.Value;
                }
            }
        }
    }

    private void RemoveDocumentFieldStats(long segmentId, int localId)
    {
        SegmentReader segment = _segments[segmentId];
        foreach (KeyValuePair<string, Dictionary<int, int>> field in segment.FieldLengths)
        {
            if (field.Value.TryGetValue(localId, out int length) && _fieldStats.TryGetValue(field.Key, out MutableFieldStats? stats))
            {
                stats.DocumentCount--;
                stats.TotalLength -= length;
            }
        }
    }

    private static IReadOnlyList<Document> DeduplicateLastWins(IReadOnlyList<Document> documents)
    {
        HashSet<string> seen = new(documents.Count, StringComparer.Ordinal);
        bool hasDuplicates = false;
        foreach (Document document in documents)
        {
            if (!seen.Add(document.Id.Value))
            {
                hasDuplicates = true;
                break;
            }
        }

        if (!hasDuplicates)
        {
            return documents;
        }

        // 存在批内重复：反向遍历保留每个 ID 最后一次出现，再恢复原始相对顺序。
        seen.Clear();
        List<Document> unique = new(documents.Count);
        for (int i = documents.Count - 1; i >= 0; i--)
        {
            if (seen.Add(documents[i].Id.Value))
            {
                unique.Add(documents[i]);
            }
        }
        unique.Reverse();
        return unique;
    }

    private List<Document> SnapshotLiveDocuments()
    {
        List<Document> documents = new(_liveDocuments.Count);
        foreach (KeyValuePair<string, (long SegmentId, int LocalId)> live in _liveDocuments.OrderBy(static x => x.Key, StringComparer.Ordinal))
        {
            SegmentReader segment = _segments[live.Value.SegmentId];
            SegmentDocument snapshot = segment.DocumentSnapshots[live.Key];
            Document document = new(snapshot.Id);
            foreach (KeyValuePair<string, string> field in snapshot.Fields)
            {
                document.Set(field.Key, field.Value);
            }
            documents.Add(document);
        }
        return documents;
    }

    private void SaveManifest()
    {
        // 墓碑集合按段惰性物化：整批只在保存前排序一次，而非每次 tombstone 都重排（O(N²) 根因之一）。
        if (_dirtyTombstoneSegments.Count > 0)
        {
            foreach (long segmentId in _dirtyTombstoneSegments)
            {
                _manifest.Tombstones[segmentId.ToString()] = _tombstones[segmentId].Order().ToList();
            }
            _dirtyTombstoneSegments.Clear();
        }

        ManifestFile.Save(Directory, _manifest);
    }

    private void ScheduleBackgroundMergeIfNeeded()
    {
        if (!_options.EnableBackgroundMerge
            || _mergeScheduled
            || _manifest.ActiveSegments.Count < _options.BackgroundMergeSegmentThreshold)
        {
            return;
        }

        _mergeScheduled = true;
        _backgroundMergeTask = Task.Factory.StartNew(
            MergeSegments,
            CancellationToken.None,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);
    }

    private static void DeleteOldSegments(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static Dictionary<TKey, TValue> GetOrCreate<TKey, TValue>(Dictionary<string, Dictionary<TKey, TValue>> outer, string key)
        where TKey : notnull
        where TValue : new()
    {
        if (!outer.TryGetValue(key, out Dictionary<TKey, TValue>? inner))
        {
            inner = new Dictionary<TKey, TValue>();
            outer[key] = inner;
        }
        return inner;
    }

    private static Dictionary<int, int> GetOrCreate(Dictionary<string, Dictionary<int, int>> outer, string key)
    {
        if (!outer.TryGetValue(key, out Dictionary<int, int>? inner))
        {
            inner = new Dictionary<int, int>();
            outer[key] = inner;
        }
        return inner;
    }

    private readonly record struct FieldStats(int DocumentCount, long TotalLength);

    private sealed class MutableFieldStats
    {
        public int DocumentCount;
        public long TotalLength;
    }
}
