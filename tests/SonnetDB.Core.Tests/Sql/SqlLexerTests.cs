using SonnetDB.Sql;
using Xunit;

namespace SonnetDB.Core.Tests.Sql;

public class SqlLexerTests
{
    [Fact]
    public void Tokenize_EmptyInput_ReturnsOnlyEof()
    {
        var tokens = SqlLexer.Tokenize("   \t\r\n  ");
        Assert.Single(tokens);
        Assert.Equal(TokenKind.EndOfFile, tokens[0].Kind);
    }

    [Fact]
    public void Tokenize_KeywordsAreCaseInsensitive()
    {
        var tokens = SqlLexer.Tokenize("Select UNION FROM where Group By Time");
        var kinds = new[]
        {
            TokenKind.KeywordSelect, TokenKind.KeywordUnion, TokenKind.KeywordFrom, TokenKind.KeywordWhere,
            TokenKind.KeywordGroup, TokenKind.KeywordBy, TokenKind.KeywordTime,
            TokenKind.EndOfFile,
        };
        Assert.Equal(kinds, System.Linq.Enumerable.Select(tokens, t => t.Kind));
    }

    [Fact]
    public void Tokenize_QuotedIdentifier_PreservesContentAndEscapes()
    {
        var tokens = SqlLexer.Tokenize("\"my \"\"col\"\"\"");
        Assert.Equal(TokenKind.IdentifierLiteral, tokens[0].Kind);
        Assert.Equal("my \"col\"", tokens[0].Text);
    }

    [Fact]
    public void Tokenize_StringLiteral_HandlesEscapedSingleQuote()
    {
        var tokens = SqlLexer.Tokenize("'O''Hara'");
        Assert.Equal(TokenKind.StringLiteral, tokens[0].Kind);
        Assert.Equal("O'Hara", tokens[0].Text);
    }

    [Fact]
    public void Tokenize_UnterminatedString_Throws()
    {
        Assert.Throws<SqlParseException>(() => SqlLexer.Tokenize("'abc"));
    }

    [Theory]
    [InlineData("123", 123L)]
    [InlineData("0", 0L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    public void Tokenize_IntegerLiteral_ParsesValue(string source, long expected)
    {
        var tokens = SqlLexer.Tokenize(source);
        Assert.Equal(TokenKind.IntegerLiteral, tokens[0].Kind);
        Assert.Equal(expected, tokens[0].IntegerValue);
    }

    [Theory]
    [InlineData("1.5", 1.5)]
    [InlineData("0.25", 0.25)]
    [InlineData("1e3", 1000.0)]
    [InlineData("2.5E-2", 0.025)]
    public void Tokenize_FloatLiteral_ParsesValue(string source, double expected)
    {
        var tokens = SqlLexer.Tokenize(source);
        Assert.Equal(TokenKind.FloatLiteral, tokens[0].Kind);
        Assert.Equal(expected, tokens[0].DoubleValue, precision: 12);
    }

    [Theory]
    [InlineData("100ms", 100L)]
    [InlineData("5s", 5_000L)]
    [InlineData("1m", 60_000L)]
    [InlineData("2h", 7_200_000L)]
    [InlineData("1d", 86_400_000L)]
    [InlineData("500us", 0L)]      // 截断到毫秒
    [InlineData("1500us", 1L)]
    [InlineData("1000000ns", 1L)]
    public void Tokenize_DurationLiteral_NormalizesToMilliseconds(string source, long expectedMs)
    {
        var tokens = SqlLexer.Tokenize(source);
        Assert.Equal(TokenKind.DurationLiteral, tokens[0].Kind);
        Assert.Equal(expectedMs, tokens[0].IntegerValue);
    }

    [Theory]
    [InlineData("1day")]   // 非法后缀（多余字母）
    [InlineData("5n")]     // 单字符 n 不合法
    public void Tokenize_InvalidDurationSuffix_Throws(string source)
    {
        Assert.Throws<SqlParseException>(() => SqlLexer.Tokenize(source));
    }

    [Fact]
    public void Tokenize_LineCommentIsSkipped()
    {
        var tokens = SqlLexer.Tokenize("SELECT -- comment\n* FROM t");
        Assert.Equal(TokenKind.KeywordSelect, tokens[0].Kind);
        Assert.Equal(TokenKind.Star, tokens[1].Kind);
        Assert.Equal(TokenKind.KeywordFrom, tokens[2].Kind);
    }

    [Fact]
    public void Tokenize_DoubleSlashLineCommentIsSkipped()
    {
        var tokens = SqlLexer.Tokenize("SELECT // comment\r\n* FROM t");
        Assert.Equal(TokenKind.KeywordSelect, tokens[0].Kind);
        Assert.Equal(TokenKind.Star, tokens[1].Kind);
        Assert.Equal(TokenKind.KeywordFrom, tokens[2].Kind);
    }

    [Fact]
    public void Tokenize_RemCommentAtLineStartIsSkipped()
    {
        var tokens = SqlLexer.Tokenize("REM bootstrap\r\nSELECT * FROM t");
        Assert.Equal(TokenKind.KeywordSelect, tokens[0].Kind);
        Assert.Equal(TokenKind.Star, tokens[1].Kind);
        Assert.Equal(TokenKind.KeywordFrom, tokens[2].Kind);
    }

    [Fact]
    public void Tokenize_RemIdentifier_IsNotTreatedAsComment()
    {
        var tokens = SqlLexer.Tokenize("SELECT rem FROM t");
        Assert.Equal(TokenKind.KeywordSelect, tokens[0].Kind);
        Assert.Equal(TokenKind.IdentifierLiteral, tokens[1].Kind);
        Assert.Equal("rem", tokens[1].Text);
        Assert.Equal(TokenKind.KeywordFrom, tokens[2].Kind);
    }

    [Fact]
    public void Tokenize_BlockCommentIsSkipped()
    {
        var tokens = SqlLexer.Tokenize("SELECT /* hi\nthere */ * FROM t");
        Assert.Equal(TokenKind.KeywordSelect, tokens[0].Kind);
        Assert.Equal(TokenKind.Star, tokens[1].Kind);
    }

    [Fact]
    public void Tokenize_UnterminatedBlockComment_Throws()
    {
        Assert.Throws<SqlParseException>(() => SqlLexer.Tokenize("SELECT /* unterminated"));
    }

    [Fact]
    public void Tokenize_OperatorsAreLexedDistinctly()
    {
        var tokens = SqlLexer.Tokenize("= != <> < <= <=> <-> <#> > >= + - * / % .");
        var kinds = new[]
        {
            TokenKind.Equal, TokenKind.NotEqual, TokenKind.NotEqual,
            TokenKind.LessThan, TokenKind.LessThanOrEqual,
            TokenKind.VectorCosineDistance, TokenKind.VectorL2Distance, TokenKind.VectorInnerProduct,
            TokenKind.GreaterThan, TokenKind.GreaterThanOrEqual,
            TokenKind.Plus, TokenKind.Minus, TokenKind.Star, TokenKind.Slash, TokenKind.Percent,
            TokenKind.Dot,
            TokenKind.EndOfFile,
        };
        Assert.Equal(kinds, System.Linq.Enumerable.Select(tokens, t => t.Kind));
    }

    [Fact]
    public void Tokenize_PreservesIdentifierCase()
    {
        var tokens = SqlLexer.Tokenize("MyMeasurement");
        Assert.Equal(TokenKind.IdentifierLiteral, tokens[0].Kind);
        Assert.Equal("MyMeasurement", tokens[0].Text);
    }

    [Fact]
    public void Tokenize_TracksPosition()
    {
        var tokens = SqlLexer.Tokenize("SELECT *");
        Assert.Equal(0, tokens[0].Position);
        Assert.Equal(7, tokens[1].Position);
    }

    [Fact]
    public void Tokenize_ControlPlaneKeywords_AreRecognized()
    {
        var tokens = SqlLexer.Tokenize("CREATE USER alice WITH PASSWORD 'pwd' GRANT READ WRITE ADMIN ON DATABASE TO REVOKE FROM DROP ALTER");
        var kinds = System.Linq.Enumerable.Select(tokens, t => t.Kind).ToArray();
        var expected = new[]
        {
            TokenKind.KeywordCreate, TokenKind.KeywordUser, TokenKind.IdentifierLiteral,
            TokenKind.KeywordWith, TokenKind.KeywordPassword, TokenKind.StringLiteral,
            TokenKind.KeywordGrant, TokenKind.KeywordRead, TokenKind.KeywordWrite, TokenKind.KeywordAdmin,
            TokenKind.KeywordOn, TokenKind.KeywordDatabase, TokenKind.KeywordTo,
            TokenKind.KeywordRevoke, TokenKind.KeywordFrom,
            TokenKind.KeywordDrop, TokenKind.KeywordAlter,
            TokenKind.EndOfFile,
        };
        Assert.Equal(expected, kinds);
    }

    [Fact]
    public void Tokenize_ControlPlaneKeywords_AreCaseInsensitive()
    {
        var tokens = SqlLexer.Tokenize("create User wIth PaSSworD grant On to revoke from drop alter database read write admin");
        var kinds = System.Linq.Enumerable.Select(tokens, t => t.Kind).ToArray();
        Assert.Contains(TokenKind.KeywordCreate, kinds);
        Assert.Contains(TokenKind.KeywordUser, kinds);
        Assert.Contains(TokenKind.KeywordWith, kinds);
        Assert.Contains(TokenKind.KeywordPassword, kinds);
        Assert.Contains(TokenKind.KeywordGrant, kinds);
        Assert.Contains(TokenKind.KeywordRevoke, kinds);
        Assert.Contains(TokenKind.KeywordOn, kinds);
        Assert.Contains(TokenKind.KeywordTo, kinds);
        Assert.Contains(TokenKind.KeywordDrop, kinds);
        Assert.Contains(TokenKind.KeywordAlter, kinds);
        Assert.Contains(TokenKind.KeywordDatabase, kinds);
        Assert.Contains(TokenKind.KeywordRead, kinds);
        Assert.Contains(TokenKind.KeywordWrite, kinds);
        Assert.Contains(TokenKind.KeywordAdmin, kinds);
    }

    [Fact]
    public void Tokenize_OrderAndPaginationKeywords_AreRecognized()
    {
        var tokens = SqlLexer.Tokenize("SELECT * FROM cpu ORDER BY time DESC OFFSET 10 ROWS FETCH NEXT 5 ROWS ONLY LIMIT 3");
        var kinds = System.Linq.Enumerable.Select(tokens, t => t.Kind).ToArray();
        var texts = System.Linq.Enumerable.Select(tokens, t => t.Text).ToArray();

        Assert.Contains(TokenKind.KeywordOrder, kinds);
        Assert.Contains(TokenKind.KeywordAsc, SqlLexer.Tokenize("ASC").Select(t => t.Kind).ToArray());
        Assert.Contains(TokenKind.KeywordDesc, kinds);
        Assert.Contains(TokenKind.KeywordOffset, kinds);
        Assert.Contains(TokenKind.KeywordFetch, kinds);
        Assert.Contains(TokenKind.KeywordLimit, kinds);

        Assert.Contains("NEXT", texts, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("ROWS", texts, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("ONLY", texts, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Tokenize_DdlModifierKeywords_AreRecognized()
    {
        var tokens = SqlLexer.Tokenize("NULL NOT DEFAULT");
        var kinds = System.Linq.Enumerable.Select(tokens, t => t.Kind).ToArray();

        Assert.Equal(new[]
        {
            TokenKind.KeywordNull,
            TokenKind.KeywordNot,
            TokenKind.KeywordDefault,
            TokenKind.EndOfFile,
        }, kinds);
    }
}
