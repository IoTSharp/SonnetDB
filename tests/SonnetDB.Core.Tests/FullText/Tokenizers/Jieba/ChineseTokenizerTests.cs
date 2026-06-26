using SonnetDB.FullText.Tokenization;
using SonnetDB.FullText.Tokenizers.Jieba;
using Xunit;

namespace SonnetDB.FullText.Tokenizers.Jieba.Tests;

public class ChineseTokenizerTests
{
    [Fact]
    public void Embedded_dictionary_loads()
    {
        Assert.True(ChineseDictionary.Default.Count > 100_000);
        Assert.True(ChineseDictionary.Default.Contains("北京"));
        Assert.True(ChineseDictionary.Default.GetFrequency("北京") > 0);
        Assert.True(ChineseDictionary.Default.Contains("数据库"));
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
        t.Tokenize("SonnetDB 是 中国 IoT 项目".AsSpan(), sink);
        string[] tokens = sink.Tokens.Select(x => x.Text).ToArray();
        Assert.Contains("sonnetdb", tokens);
        Assert.Contains("中国", tokens);
        Assert.Contains("iot", tokens);
    }

    [Fact]
    public void Loads_multiple_text_dictionaries_and_later_files_override_frequency()
    {
        string directory = CreateTempDirectory();
        try
        {
            string basePath = Path.Combine(directory, "base.txt");
            string userPath = Path.Combine(directory, "user.txt");
            File.WriteAllText(basePath, "时序数据库 10 nz\n中文\t20\n");
            File.WriteAllText(userPath, "时序数据库\t9000\nSonnetDB\t8000\n");

            ChineseDictionary dictionary = ChineseDictionary.FromTextFiles(basePath, userPath);

            Assert.Equal(9000, dictionary.GetFrequency("时序数据库"));
            Assert.Equal(8000, dictionary.GetFrequency("SonnetDB"));
            Assert.Equal(20, dictionary.GetFrequency("中文"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Compiles_and_loads_dat_dictionary()
    {
        string directory = CreateTempDirectory();
        try
        {
            string basePath = Path.Combine(directory, "base.txt");
            string userPath = Path.Combine(directory, "user.txt");
            string datPath = Path.Combine(directory, "dict.dat");
            File.WriteAllText(basePath, "向量检索 10 nz\n全文搜索 20 n\n");
            File.WriteAllText(userPath, "混合检索\t300\n");

            ChineseDictionaryCompileResult result = ChineseDictionaryCompiler.Compile([basePath, userPath], datPath);
            ChineseDictionary dictionary = ChineseDictionary.FromCompiledFile(datPath);

            Assert.Equal(3, result.TermCount);
            Assert.Equal(300, dictionary.GetFrequency("混合检索"));
            Assert.True(dictionary.Contains("向量检索"));
            Assert.True(dictionary.Contains("全文搜索"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "sonnetdb-jieba-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
