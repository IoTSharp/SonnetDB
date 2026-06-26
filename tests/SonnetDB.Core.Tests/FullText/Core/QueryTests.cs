using DotSearch.Query;
using Xunit;

namespace DotSearch.Core.Tests;

public class QueryTests
{
    [Fact]
    public void Or_query_snapshots_clauses()
    {
        Query.Query[] clauses =
        [
            new TermQuery("body", "alpha"),
        ];

        OrQuery query = new(clauses);
        clauses[0] = new TermQuery("body", "beta");

        TermQuery term = Assert.IsType<TermQuery>(query.Clauses[0]);
        Assert.Equal("alpha", term.Term);
    }

    [Fact]
    public void And_query_rejects_null_clause()
    {
        Query.Query?[] clauses =
        [
            new TermQuery("body", "alpha"),
            null,
        ];

        Assert.Throws<ArgumentException>(() => new AndQuery(clauses!));
    }

    [Fact]
    public void Phrase_query_snapshots_terms()
    {
        string[] terms = ["alpha", "beta"];
        PhraseQuery query = new("body", terms);
        terms[0] = "changed";

        Assert.Equal("alpha", query.Terms[0]);
    }

    [Fact]
    public void Near_query_rejects_negative_distance()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NearQuery("body", ["alpha"], -1));
    }
}
