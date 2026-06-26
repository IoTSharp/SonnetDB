using SonnetDB.FullText.Tokenization;
using Xunit;

namespace SonnetDB.Core.Tests.FullText;

public class TokenFilterTests
{
    [Fact]
    public void Synonym_filter_rewrites_known_token()
    {
        SynonymTokenFilter filter = new(new Dictionary<string, string> { ["iot"] = "internetofthings" });
        Span<char> buffer = stackalloc char[32];

        Assert.True(filter.TryFilter("iot".AsSpan(), buffer, out int written));
        Assert.Equal("internetofthings", buffer[..written].ToString());
    }

    [Fact]
    public void Pinyin_filter_rewrites_known_token()
    {
        PinyinTokenFilter filter = new(new Dictionary<string, string> { ["北京"] = "beijing" });
        Span<char> buffer = stackalloc char[16];

        Assert.True(filter.TryFilter("北京".AsSpan(), buffer, out int written));
        Assert.Equal("beijing", buffer[..written].ToString());
    }
}
