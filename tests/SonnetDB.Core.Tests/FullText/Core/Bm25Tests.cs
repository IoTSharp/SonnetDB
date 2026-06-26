using SonnetDB.FullText.Scoring;
using Xunit;

namespace SonnetDB.Core.Tests.FullText;

public class Bm25Tests
{
    [Fact]
    public void Score_zero_for_zero_term_frequency()
    {
        double s = Bm25.Score(0, 100, 100, 10, 5, Bm25Parameters.Default);
        Assert.Equal(0.0, s);
    }

    [Fact]
    public void Score_increases_with_term_frequency()
    {
        double s1 = Bm25.Score(1, 100, 100, 1000, 50, Bm25Parameters.Default);
        double s5 = Bm25.Score(5, 100, 100, 1000, 50, Bm25Parameters.Default);
        Assert.True(s5 > s1);
    }

    [Fact]
    public void Rare_term_outscores_common_term()
    {
        double rare = Bm25.Score(1, 100, 100, 1000, 1, Bm25Parameters.Default);
        double common = Bm25.Score(1, 100, 100, 1000, 800, Bm25Parameters.Default);
        Assert.True(rare > common);
    }
}
