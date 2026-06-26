using SonnetDB.FullText.Tokenization;
using SonnetDB.FullText.Tokenizers.Unicode;
using Xunit;

namespace SonnetDB.FullText.Tokenizers.Unicode.Tests;

public class UnicodeTokenizerTests
{
    [Fact]
    public void Splits_latin_words_and_lowercases()
    {
        UnicodeTokenizer t = new();
        CollectingTokenSink sink = new();
        t.Tokenize("Hello, World! 123-abc".AsSpan(), sink);

        Assert.Collection(sink.Tokens,
            tk => Assert.Equal("hello", tk.Text),
            tk => Assert.Equal("world", tk.Text),
            tk => Assert.Equal("123", tk.Text),
            tk => Assert.Equal("abc", tk.Text));
    }

    [Fact]
    public void Empty_input_emits_nothing()
    {
        UnicodeTokenizer t = new();
        CollectingTokenSink sink = new();
        t.Tokenize(ReadOnlySpan<char>.Empty, sink);
        Assert.Empty(sink.Tokens);
    }
}
