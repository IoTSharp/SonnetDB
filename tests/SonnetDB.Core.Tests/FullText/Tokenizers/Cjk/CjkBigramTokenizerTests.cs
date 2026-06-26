using SonnetDB.FullText.Tokenization;
using SonnetDB.FullText.Tokenizers.Cjk;
using Xunit;

namespace SonnetDB.FullText.Tokenizers.Cjk.Tests;

public class CjkBigramTokenizerTests
{
    [Fact]
    public void Cjk_text_emits_bigrams()
    {
        CjkBigramTokenizer t = new();
        CollectingTokenSink sink = new();
        t.Tokenize("北京天气".AsSpan(), sink);

        // 期望生成 3 个 bigram：北京 / 京天 / 天气。
        // 尾字"气"已被 bigram"天气"覆盖，不再单独输出——这样查询 "天气" 与索引文本中嵌入
        // 的 "天气" 子串能匹配（旧实现尾字单 token 会让 AND-of-tokens 与中间位置的 bigram
        // 序列失配）。
        Assert.Equal(3, sink.Tokens.Count);
        Assert.Equal("北京", sink.Tokens[0].Text);
        Assert.Equal("京天", sink.Tokens[1].Text);
        Assert.Equal("天气", sink.Tokens[2].Text);
    }

    [Fact]
    public void Mixed_text_handles_latin_and_cjk()
    {
        CjkBigramTokenizer t = new();
        CollectingTokenSink sink = new();
        t.Tokenize("Hello 北京".AsSpan(), sink);

        Assert.Equal(2, sink.Tokens.Count);
        Assert.Equal("hello", sink.Tokens[0].Text);
        Assert.Equal("北京", sink.Tokens[1].Text);
    }

    [Fact]
    public void Single_isolated_cjk_char_emits_unigram()
    {
        // 一个孤立 CJK 字符（前后都不是 CJK）仍然要发射，否则单字检索完全不可用。
        CjkBigramTokenizer t = new();
        CollectingTokenSink sink = new();
        t.Tokenize("abc水def".AsSpan(), sink);

        Assert.Equal(3, sink.Tokens.Count);
        Assert.Equal("abc", sink.Tokens[0].Text);
        Assert.Equal("水", sink.Tokens[1].Text);
        Assert.Equal("def", sink.Tokens[2].Text);
    }

    [Fact]
    public void Single_cjk_input_emits_unigram()
    {
        CjkBigramTokenizer t = new();
        CollectingTokenSink sink = new();
        t.Tokenize("水".AsSpan(), sink);

        Assert.Single(sink.Tokens);
        Assert.Equal("水", sink.Tokens[0].Text);
    }

    [Fact]
    public void Two_char_cjk_query_matches_substring_in_longer_text()
    {
        // 回归：parity #133 — 查询 "水泵" 应能命中索引中的子串 "水泵报警"。
        // 索引侧 token 集合包含 [水泵, ...]；查询侧必须也只产出 [水泵]（而不是 [水泵, 泵]）
        // 否则 AND-of-tokens 会要求孤立 "泵" 出现而失配。
        CjkBigramTokenizer t = new();
        var indexSink = new CollectingTokenSink();
        var querySink = new CollectingTokenSink();
        t.Tokenize("水泵报警".AsSpan(), indexSink);
        t.Tokenize("水泵".AsSpan(), querySink);

        var indexTerms = indexSink.Tokens.Select(token => token.Text).ToHashSet();
        foreach (var token in querySink.Tokens)
            Assert.Contains(token.Text, indexTerms);
    }
}
