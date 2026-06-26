using DotSearch.Tokenization;
using DotSearch.Tokenizers.Jieba;
using Xunit;

namespace DotSearch.Tokenizers.Jieba.Tests;

public class ChineseTokenizerTests
{
    [Fact]
    public void Embedded_dictionary_loads()
    {
        Assert.True(ChineseDictionary.Default.Count > 0);
        Assert.True(ChineseDictionary.Default.Contains("北京"));
        Assert.True(ChineseDictionary.Default.GetFrequency("北京") > 0);
    }

    [Fact]
    public void Segments_known_words_from_dictionary()
    {
        ChineseTokenizer t = new();
        CollectingTokenSink sink = new();
        t.Tokenize("北京天气不错".AsSpan(), sink);

        // 词典里包含 北京、天气、不错；DP 应将整段切成这三个词。
        Assert.Equal(new[] { "北京", "天气", "不错" }, sink.Tokens.Select(x => x.Text).ToArray());
    }

    [Fact]
    public void Falls_back_to_single_chars_for_unknown_text()
    {
        ChineseTokenizer t = new();
        CollectingTokenSink sink = new();
        t.Tokenize("龘龘龘".AsSpan(), sink);
        Assert.Equal(3, sink.Tokens.Count);
        Assert.All(sink.Tokens, tk => Assert.Single(tk.Text));
    }

    [Fact]
    public void Mixed_chinese_and_latin()
    {
        ChineseTokenizer t = new();
        CollectingTokenSink sink = new();
        t.Tokenize("DotSearch 是 中国 IoT 项目".AsSpan(), sink);
        string[] tokens = sink.Tokens.Select(x => x.Text).ToArray();
        Assert.Contains("dotsearch", tokens);
        Assert.Contains("中国", tokens);
        Assert.Contains("iot", tokens);
    }
}
