using System.Collections.Generic;
using SonnetDB.FullText.Scoring;
using SonnetDB.FullText.Tokenization;

namespace SonnetDB.FullText.Index;

/// <summary>
/// 单段、内存常驻的倒排索引。
/// </summary>
/// <remarks>
/// <para>实现仅满足以下能力：</para>
/// <list type="bullet">
///   <item>按 (field, term) 维护 posting list（doc -> tf + positions）。</item>
///   <item>统计每个 (doc, field) 的长度，用于 BM25 长度归一化。</item>
///   <item>支持 term / phrase / NEAR / AND / OR 查询。</item>
///   <item>删除采用 tombstone：保留 docId，但在结果中跳过。</item>
/// </list>
/// <para>并发安全性：内部使用 <see cref="System.Threading.Lock"/> 串行化写入与查询。</para>
/// </remarks>
public sealed class InMemoryFullTextIndex : IFullTextIndex
{
    private readonly System.Threading.Lock _lock = new();
    private readonly ITokenizer _tokenizer;
    private readonly Bm25Parameters _bm25;
    private readonly Bm25FOptions _bm25f;

    // 文档表：docId -> 外部主键；删除时置 null。
    private readonly List<DocumentId?> _documents = new();
    private readonly Dictionary<string, int> _docIdLookup = new(StringComparer.Ordinal);

    // (field, term) -> (docId -> tf)
    private readonly Dictionary<string, Dictionary<string, Dictionary<int, int>>> _postings = new(StringComparer.Ordinal);

    // (field, term) -> (docId -> positions)
    private readonly Dictionary<string, Dictionary<string, Dictionary<int, List<int>>>> _positions = new(StringComparer.Ordinal);

    // (field, docId) -> length
    private readonly Dictionary<string, Dictionary<int, int>> _fieldLengths = new(StringComparer.Ordinal);

    private int _liveCount;

    /// <summary>
    /// 创建内存索引。
    /// </summary>
    /// <param name="tokenizer">分词器。</param>
    /// <param name="bm25">BM25 参数；默认 <see cref="Bm25Parameters.Default"/>。</param>
    public InMemoryFullTextIndex(ITokenizer tokenizer, Bm25Parameters? bm25 = null, Bm25FOptions? bm25f = null)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        _tokenizer = tokenizer;
        _bm25 = bm25 ?? Bm25Parameters.Default;
        _bm25f = bm25f ?? Bm25FOptions.Default;
    }

    /// <inheritdoc />
    public int DocumentCount
    {
        get
        {
            lock (_lock)
            {
                return _liveCount;
            }
        }
    }

    /// <inheritdoc />
    public void Index(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);

        lock (_lock)
        {
            // 已存在则先逻辑删除。
            if (_docIdLookup.TryGetValue(document.Id.Value, out int existing))
            {
                RemoveFromPostings(existing);
                _documents[existing] = null;
                _liveCount--;
            }

            int docId = _documents.Count;
            _documents.Add(document.Id);
            _docIdLookup[document.Id.Value] = docId;
            _liveCount++;

            CollectingTokenSink sink = new();
            foreach (KeyValuePair<string, string> field in document.Fields)
            {
                sink.Clear();
                _tokenizer.Tokenize(field.Value.AsSpan(), sink);

                Dictionary<string, Dictionary<int, int>> termsForField = GetOrCreate(_postings, field.Key);
                Dictionary<string, Dictionary<int, List<int>>> positionsForField = GetOrCreate(_positions, field.Key);
                int length = 0;
                int position = -1;
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
                    Dictionary<int, int> docs = GetOrCreate(termsForField, token.Text);
                    docs[docId] = docs.TryGetValue(docId, out int tf) ? tf + 1 : 1;

                    Dictionary<int, List<int>> termPositions = GetOrCreate(positionsForField, token.Text);
                    if (!termPositions.TryGetValue(docId, out List<int>? values))
                    {
                        values = new List<int>();
                        termPositions[docId] = values;
                    }
                    values.Add(position);
                }

                Dictionary<int, int> lengths = GetOrCreate(_fieldLengths, field.Key);
                lengths[docId] = length;
            }
        }
    }

    /// <inheritdoc />
    public bool Delete(DocumentId id)
    {
        lock (_lock)
        {
            if (!_docIdLookup.TryGetValue(id.Value, out int docId) || _documents[docId] is null)
            {
                return false;
            }

            RemoveFromPostings(docId);
            _documents[docId] = null;
            _docIdLookup.Remove(id.Value);
            _liveCount--;
            return true;
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
            Dictionary<int, double> scores = new();
            Score(query, scores);

            if (scores.Count == 0)
            {
                return Array.Empty<SearchHit>();
            }

            List<SearchHit> hits = new(scores.Count);
            foreach (KeyValuePair<int, double> kv in scores)
            {
                DocumentId? id = _documents[kv.Key];
                if (id is { } docId)
                {
                    hits.Add(new SearchHit(docId, kv.Value));
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

    private void Score(Query.Query query, Dictionary<int, double> scores)
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

    private void ScoreTerm(Query.TermQuery term, Dictionary<int, double> scores)
    {
        if (!_postings.TryGetValue(term.Field, out Dictionary<string, Dictionary<int, int>>? termsForField))
        {
            return;
        }
        if (!termsForField.TryGetValue(term.Term, out Dictionary<int, int>? docs))
        {
            return;
        }
        if (!_fieldLengths.TryGetValue(term.Field, out Dictionary<int, int>? lengths))
        {
            return;
        }

        double avg = AverageFieldLength(lengths);
        int n = _liveCount;
        int df = docs.Count;

        foreach (KeyValuePair<int, int> doc in docs)
        {
            if (_documents[doc.Key] is null)
            {
                continue;
            }
            int dl = lengths.TryGetValue(doc.Key, out int len) ? len : 0;
            double s = Bm25.Score(doc.Value, dl, avg, n, df, _bm25) * _bm25f.GetWeight(term.Field);
            scores[doc.Key] = scores.TryGetValue(doc.Key, out double existing) ? existing + s : s;
        }
    }

    private void ScorePhrase(Query.PhraseQuery phrase, Dictionary<int, double> scores)
    {
        if (phrase.Terms.Count == 1)
        {
            ScoreTerm(new Query.TermQuery(phrase.Field, phrase.Terms[0]), scores);
            return;
        }

        ScorePositional(phrase.Field, phrase.Terms, scores, PositionMatcher.CountPhraseMatches);
    }

    private void ScoreNear(Query.NearQuery near, Dictionary<int, double> scores)
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
        Dictionary<int, double> scores,
        Func<List<int>[], int> matchCounter)
    {
        if (!_positions.TryGetValue(field, out Dictionary<string, Dictionary<int, List<int>>>? termsForField))
        {
            return;
        }
        if (!_fieldLengths.TryGetValue(field, out Dictionary<int, int>? lengths))
        {
            return;
        }

        Dictionary<int, List<int>>[] postings = new Dictionary<int, List<int>>[terms.Count];
        for (int i = 0; i < terms.Count; i++)
        {
            if (!termsForField.TryGetValue(terms[i], out Dictionary<int, List<int>>? termPositions))
            {
                return;
            }
            postings[i] = termPositions;
        }

        Dictionary<int, int> matches = new();
        foreach (int docId in postings[0].Keys)
        {
            if (_documents[docId] is null)
            {
                continue;
            }

            List<int>[] lists = new List<int>[postings.Length];
            lists[0] = postings[0][docId];
            bool allTermsMatch = true;
            for (int i = 1; i < postings.Length; i++)
            {
                if (!postings[i].TryGetValue(docId, out List<int>? positions))
                {
                    allTermsMatch = false;
                    break;
                }
                lists[i] = positions;
            }

            if (!allTermsMatch)
            {
                continue;
            }

            int count = matchCounter(lists);
            if (count > 0)
            {
                matches[docId] = count;
            }
        }

        if (matches.Count == 0)
        {
            return;
        }

        double avg = AverageFieldLength(lengths);
        int n = _liveCount;
        int df = matches.Count;
        double weight = _bm25f.GetWeight(field);

        foreach (KeyValuePair<int, int> match in matches)
        {
            int dl = lengths.TryGetValue(match.Key, out int len) ? len : 0;
            double s = Bm25.Score(match.Value, dl, avg, n, df, _bm25) * weight;
            scores[match.Key] = scores.TryGetValue(match.Key, out double existing) ? existing + s : s;
        }
    }

    private void ScoreAnd(Query.AndQuery and, Dictionary<int, double> scores)
    {
        if (and.Clauses.Count == 0)
        {
            return;
        }

        Dictionary<int, double> first = new();
        Score(and.Clauses[0], first);

        for (int i = 1; i < and.Clauses.Count; i++)
        {
            Dictionary<int, double> next = new();
            Score(and.Clauses[i], next);

            Dictionary<int, double> merged = new();
            foreach (KeyValuePair<int, double> kv in first)
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

        foreach (KeyValuePair<int, double> kv in first)
        {
            scores[kv.Key] = scores.TryGetValue(kv.Key, out double existing) ? existing + kv.Value : kv.Value;
        }
    }

    private static double AverageFieldLength(Dictionary<int, int> lengths)
    {
        if (lengths.Count == 0)
        {
            return 0.0;
        }
        long total = 0;
        foreach (int v in lengths.Values)
        {
            total += v;
        }
        return (double)total / lengths.Count;
    }

    private void RemoveFromPostings(int docId)
    {
        foreach (Dictionary<string, Dictionary<int, int>> termsForField in _postings.Values)
        {
            foreach (Dictionary<int, int> docs in termsForField.Values)
            {
                docs.Remove(docId);
            }
        }
        foreach (Dictionary<string, Dictionary<int, List<int>>> termsForField in _positions.Values)
        {
            foreach (Dictionary<int, List<int>> docs in termsForField.Values)
            {
                docs.Remove(docId);
            }
        }
        foreach (Dictionary<int, int> lengths in _fieldLengths.Values)
        {
            lengths.Remove(docId);
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

}
