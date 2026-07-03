using System.Buffers;
using System.Globalization;
using System.Text;

namespace SonnetDB.Sql;

/// <summary>
/// 单遍 SQL 词法分析器：把源文本扫描成 <see cref="Token"/> 序列。
/// 关键字大小写不敏感；标识符保留原始大小写（双引号引用的标识符按字面保留）。
/// </summary>
public sealed class SqlLexer
{
    private static readonly SearchValues<char> _asciiWhitespace =
        SearchValues.Create(" \t\r\n\f\v\u001c\u001d\u001e\u001f");

    private static readonly SearchValues<char> _lineBreaks = SearchValues.Create("\r\n");

    private static readonly SearchValues<char> _asciiIdentifierStart =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_");

    private static readonly SearchValues<char> _asciiIdentifierContinue =
        SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz_0123456789");

    private static readonly SearchValues<char> _asciiDigits = SearchValues.Create("0123456789");

    private static readonly SearchValues<char> _durationSuffixStarts = SearchValues.Create("numshd");

    private static readonly SearchValues<char> _operatorOrPunctuationStarts =
        SearchValues.Create("()[],;.*+-/%=!<>");

    private static readonly Dictionary<string, TokenKind> _keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["create"] = TokenKind.KeywordCreate,
        ["unique"] = TokenKind.KeywordUnique,
        ["sparse"] = TokenKind.KeywordSparse,
        ["ttl"] = TokenKind.KeywordTtl,
        ["measurement"] = TokenKind.KeywordMeasurement,
        ["table"] = TokenKind.KeywordTable,
        ["document"] = TokenKind.KeywordDocument,
        ["collection"] = TokenKind.KeywordCollection,
        ["collections"] = TokenKind.KeywordCollections,
        ["insert"] = TokenKind.KeywordInsert,
        ["into"] = TokenKind.KeywordInto,
        ["import"] = TokenKind.KeywordImport,
        ["format"] = TokenKind.KeywordFormat,
        ["path"] = TokenKind.KeywordPath,
        ["values"] = TokenKind.KeywordValues,
        ["select"] = TokenKind.KeywordSelect,
        ["distinct"] = TokenKind.KeywordDistinct,
        ["from"] = TokenKind.KeywordFrom,
        ["join"] = TokenKind.KeywordJoin,
        ["inner"] = TokenKind.KeywordInner,
        ["left"] = TokenKind.KeywordLeft,
        ["outer"] = TokenKind.KeywordOuter,
        ["where"] = TokenKind.KeywordWhere,
        ["is"] = TokenKind.KeywordIs,
        ["in"] = TokenKind.KeywordIn,
        ["group"] = TokenKind.KeywordGroup,
        ["by"] = TokenKind.KeywordBy,
        ["having"] = TokenKind.KeywordHaving,
        ["time"] = TokenKind.KeywordTime,
        ["delete"] = TokenKind.KeywordDelete,
        ["update"] = TokenKind.KeywordUpdate,
        ["set"] = TokenKind.KeywordSet,
        ["and"] = TokenKind.KeywordAnd,
        ["or"] = TokenKind.KeywordOr,
        ["not"] = TokenKind.KeywordNot,
        ["like"] = TokenKind.KeywordLike,
        ["regex"] = TokenKind.KeywordRegex,
        ["if"] = TokenKind.KeywordIf,
        ["exists"] = TokenKind.KeywordExists,
        ["as"] = TokenKind.KeywordAs,
        ["null"] = TokenKind.KeywordNull,
        ["default"] = TokenKind.KeywordDefault,
        ["true"] = TokenKind.KeywordTrue,
        ["false"] = TokenKind.KeywordFalse,
        ["case"] = TokenKind.KeywordCase,
        ["when"] = TokenKind.KeywordWhen,
        ["then"] = TokenKind.KeywordThen,
        ["else"] = TokenKind.KeywordElse,
        ["end"] = TokenKind.KeywordEnd,
        ["tag"] = TokenKind.KeywordTag,
        ["field"] = TokenKind.KeywordField,
        ["float"] = TokenKind.KeywordFloat,
        ["int"] = TokenKind.KeywordInt,
        ["bool"] = TokenKind.KeywordBool,
        ["string"] = TokenKind.KeywordString,
        ["datetime"] = TokenKind.KeywordDateTime,
        ["blob"] = TokenKind.KeywordBlob,
        ["json"] = TokenKind.KeywordJson,
        ["fulltext"] = TokenKind.KeywordFullText,
        ["using"] = TokenKind.KeywordUsing,
        ["vector"] = TokenKind.KeywordVector,
        ["geopoint"] = TokenKind.KeywordGeoPoint,

        // PR #34a：控制面 DDL 关键字
        ["user"] = TokenKind.KeywordUser,
        ["password"] = TokenKind.KeywordPassword,
        ["grant"] = TokenKind.KeywordGrant,
        ["revoke"] = TokenKind.KeywordRevoke,
        ["on"] = TokenKind.KeywordOn,
        ["cascade"] = TokenKind.KeywordCascade,
        ["to"] = TokenKind.KeywordTo,
        ["with"] = TokenKind.KeywordWith,
        ["read"] = TokenKind.KeywordRead,
        ["write"] = TokenKind.KeywordWrite,
        ["admin"] = TokenKind.KeywordAdmin,
        ["database"] = TokenKind.KeywordDatabase,
        ["drop"] = TokenKind.KeywordDrop,
        ["alter"] = TokenKind.KeywordAlter,
        ["column"] = TokenKind.KeywordColumn,
        ["rename"] = TokenKind.KeywordRename,
        ["primary"] = TokenKind.KeywordPrimary,
        ["key"] = TokenKind.KeywordKey,
        ["foreign"] = TokenKind.KeywordForeign,
        ["references"] = TokenKind.KeywordReferences,
        ["rowversion"] = TokenKind.KeywordRowVersion,

        // PR #34b-1：SHOW 控制面查询
        ["show"] = TokenKind.KeywordShow,
        ["users"] = TokenKind.KeywordUsers,
        ["grants"] = TokenKind.KeywordGrants,
        ["databases"] = TokenKind.KeywordDatabases,
        ["for"] = TokenKind.KeywordFor,
        // PR #34b-3：CREATE USER ... SUPERUSER
        ["superuser"] = TokenKind.KeywordSuperuser,

        // PR #34b-3-tokens：API token 管理
        ["tokens"] = TokenKind.KeywordTokens,
        ["token"] = TokenKind.KeywordToken,
        ["issue"] = TokenKind.KeywordIssue,

        // 元数据查询：EXPLAIN / SHOW MEASUREMENTS / SHOW TABLES / DESCRIBE [MEASUREMENT|TABLE] <name>
        ["explain"] = TokenKind.KeywordExplain,
        ["measurements"] = TokenKind.KeywordMeasurements,
        ["tables"] = TokenKind.KeywordTables,
        ["describe"] = TokenKind.KeywordDescribe,
        ["desc"] = TokenKind.KeywordDesc,

        // 分页子句
        ["order"] = TokenKind.KeywordOrder,
        ["asc"] = TokenKind.KeywordAsc,
        ["offset"] = TokenKind.KeywordOffset,
        ["fetch"] = TokenKind.KeywordFetch,
        ["limit"] = TokenKind.KeywordLimit,
        ["begin"] = TokenKind.KeywordBegin,
        ["commit"] = TokenKind.KeywordCommit,
        ["rollback"] = TokenKind.KeywordRollback,
        ["transaction"] = TokenKind.KeywordTransaction,
    };

    private readonly string _source;
    private int _position;

    /// <summary>构造一个新的词法分析器。</summary>
    /// <param name="source">SQL 源文本。</param>
    public SqlLexer(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = source;
        _position = 0;
    }

    /// <summary>
    /// 一次性把源文本完整 token 化（最后一个 token 总是 <see cref="TokenKind.EndOfFile"/>）。
    /// </summary>
    /// <param name="source">SQL 源文本。</param>
    /// <returns>token 列表，结尾包含 EOF。</returns>
    public static IReadOnlyList<Token> Tokenize(string source)
    {
        var lexer = new SqlLexer(source);
        var list = new List<Token>(32);
        while (true)
        {
            var token = lexer.NextToken();
            list.Add(token);
            if (token.Kind == TokenKind.EndOfFile) break;
        }
        return list;
    }

    /// <summary>读取下一个 token；到达末尾时持续返回 EOF。</summary>
    public Token NextToken()
    {
        SkipWhitespaceAndComments();

        if (_position >= _source.Length)
            return new Token(TokenKind.EndOfFile, string.Empty, _position);

        var start = _position;
        var ch = _source[_position];

        // 标识符 / 关键字
        if (IsIdentifierStart(ch))
            return ScanIdentifierOrKeyword(start);

        // 数字（含 duration 后缀）
        if (char.IsAsciiDigit(ch))
            return ScanNumber(start);

        // 字符串字面量（单引号；引号内 '' 表示一个 '）
        if (ch == '\'')
            return ScanString(start);

        // 双引号引用的标识符
        if (ch == '"')
            return ScanQuotedIdentifier(start);

        // 参数占位符：位置 '?' 或命名 '@name' / ':name'（#213）
        if (ch == '?')
        {
            _position++;
            return new Token(TokenKind.Parameter, string.Empty, start);
        }
        if ((ch == '@' || ch == ':') && IsIdentifierStart(Peek(1)))
            return ScanNamedParameter(start);

        if (!_operatorOrPunctuationStarts.Contains(ch))
            throw new SqlParseException($"无法识别的字符 '{ch}'", start);

        // 标点 / 运算符
        switch (ch)
        {
            case '(': _position++; return new Token(TokenKind.LeftParen, "(", start);
            case ')': _position++; return new Token(TokenKind.RightParen, ")", start);
            case '[': _position++; return new Token(TokenKind.LeftBracket, "[", start);
            case ']': _position++; return new Token(TokenKind.RightBracket, "]", start);
            case ',': _position++; return new Token(TokenKind.Comma, ",", start);
            case ';': _position++; return new Token(TokenKind.Semicolon, ";", start);
            case '.': _position++; return new Token(TokenKind.Dot, ".", start);
            case '*': _position++; return new Token(TokenKind.Star, "*", start);
            case '+': _position++; return new Token(TokenKind.Plus, "+", start);
            case '-': _position++; return new Token(TokenKind.Minus, "-", start);
            case '/': _position++; return new Token(TokenKind.Slash, "/", start);
            case '%': _position++; return new Token(TokenKind.Percent, "%", start);
            case '=':
                if (Peek(1) == '>')
                {
                    _position += 2;
                    return new Token(TokenKind.Arrow, "=>", start);
                }
                _position++;
                return new Token(TokenKind.Equal, "=", start);
            case '!':
                if (Peek(1) == '=') { _position += 2; return new Token(TokenKind.NotEqual, "!=", start); }
                throw new SqlParseException("无法识别的字符 '!'", start);
            case '<':
                if (Peek(1) == '=' && Peek(2) == '>')
                {
                    _position += 3;
                    return new Token(TokenKind.VectorCosineDistance, "<=>", start);
                }
                if (Peek(1) == '-' && Peek(2) == '>')
                {
                    _position += 3;
                    return new Token(TokenKind.VectorL2Distance, "<->", start);
                }
                if (Peek(1) == '#' && Peek(2) == '>')
                {
                    _position += 3;
                    return new Token(TokenKind.VectorInnerProduct, "<#>", start);
                }
                if (Peek(1) == '=') { _position += 2; return new Token(TokenKind.LessThanOrEqual, "<=", start); }
                if (Peek(1) == '>') { _position += 2; return new Token(TokenKind.NotEqual, "<>", start); }
                _position++;
                return new Token(TokenKind.LessThan, "<", start);
            case '>':
                if (Peek(1) == '=') { _position += 2; return new Token(TokenKind.GreaterThanOrEqual, ">=", start); }
                _position++;
                return new Token(TokenKind.GreaterThan, ">", start);
        }

        throw new SqlParseException($"无法识别的字符 '{ch}'", start);
    }

    // ── 私有扫描例程 ─────────────────────────────────────────────────────────

    private Token ScanIdentifierOrKeyword(int start)
    {
        AdvanceIdentifierContinue();

        var text = _source.Substring(start, _position - start);
        return _keywords.TryGetValue(text, out var keyword)
            ? new Token(keyword, text, start)
            : new Token(TokenKind.IdentifierLiteral, text, start);
    }

    /// <summary>
    /// 扫描命名参数 <c>@name</c> / <c>:name</c>：跳过前缀符，把参数名（去前缀）放入 <see cref="Token.Text"/>。
    /// 调用前已确认下一个字符是标识符起始字符。
    /// </summary>
    private Token ScanNamedParameter(int start)
    {
        _position++; // 跳过 '@' / ':' 前缀
        int nameStart = _position;
        AdvanceIdentifierContinue();
        var name = _source.Substring(nameStart, _position - nameStart);
        return new Token(TokenKind.Parameter, name, start);
    }

    private Token ScanQuotedIdentifier(int start)
    {
        _position++; // 跳过开引号
        var sb = new StringBuilder();
        while (_position < _source.Length)
        {
            var ch = _source[_position];
            if (ch == '"')
            {
                if (Peek(1) == '"')
                {
                    sb.Append('"');
                    _position += 2;
                    continue;
                }
                _position++;
                return new Token(TokenKind.IdentifierLiteral, sb.ToString(), start);
            }
            sb.Append(ch);
            _position++;
        }
        throw new SqlParseException("未闭合的引号标识符", start);
    }

    private Token ScanString(int start)
    {
        _position++; // 跳过开引号
        var sb = new StringBuilder();
        while (_position < _source.Length)
        {
            var ch = _source[_position];
            if (ch == '\'')
            {
                if (Peek(1) == '\'')
                {
                    sb.Append('\'');
                    _position += 2;
                    continue;
                }
                _position++;
                return new Token(TokenKind.StringLiteral, sb.ToString(), start);
            }
            sb.Append(ch);
            _position++;
        }
        throw new SqlParseException("未闭合的字符串字面量", start);
    }

    private Token ScanNumber(int start)
    {
        AdvanceAsciiDigits();

        var isFloat = false;

        // 小数部分
        if (_position < _source.Length && _source[_position] == '.' && IsAsciiDigit(Peek(1)))
        {
            isFloat = true;
            _position++; // '.'
            AdvanceAsciiDigits();
        }

        // 指数部分
        if (_position < _source.Length && (_source[_position] == 'e' || _source[_position] == 'E'))
        {
            isFloat = true;
            _position++;
            if (_position < _source.Length && (_source[_position] == '+' || _source[_position] == '-'))
                _position++;
            if (_position >= _source.Length || !IsAsciiDigit(_source[_position]))
                throw new SqlParseException("浮点数指数缺少数字", start);
            AdvanceAsciiDigits();
        }

        var numericText = _source.Substring(start, _position - start);

        // duration 后缀：ns / us / ms / s / m / h / d；只对整数生效
        if (!isFloat && _position < _source.Length && IsDurationSuffixStart(_source[_position]))
        {
            var suffixStart = _position;
            // 最长匹配两字符（ns/us/ms）；其余为单字符（s/m/h/d）
            string suffix;
            if (_position + 1 < _source.Length && IsDurationTwoCharSuffix(_source, _position))
            {
                suffix = _source.Substring(_position, 2);
                _position += 2;
            }
            else
            {
                suffix = _source[_position].ToString();
                _position++;
            }

            // 后缀后必须是非标识符续字符（避免误把 "1day" 这类拆错）
            if (_position < _source.Length && IsIdentifierContinue(_source[_position]))
                throw new SqlParseException($"无效的 duration 后缀 '{suffix}{_source[_position]}'", suffixStart);

            if (!long.TryParse(numericText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawValue) || rawValue < 0)
                throw new SqlParseException($"非法的 duration 数值 '{numericText}'", start);

            var ms = ConvertToMilliseconds(rawValue, suffix, suffixStart);
            return new Token(TokenKind.DurationLiteral, numericText, start, IntegerValue: ms);
        }

        if (isFloat)
        {
            var floatValue = double.Parse(numericText, NumberStyles.Float, CultureInfo.InvariantCulture);
            return new Token(TokenKind.FloatLiteral, numericText, start, DoubleValue: floatValue);
        }

        if (!long.TryParse(numericText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
            throw new SqlParseException($"整数字面量超出 Int64 范围 '{numericText}'", start);

        return new Token(TokenKind.IntegerLiteral, numericText, start, IntegerValue: intValue);
    }

    private static long ConvertToMilliseconds(long value, string suffix, int position)
    {
        // 用 checked 保证溢出抛 OverflowException → 转换成 SqlParseException
        try
        {
            return suffix switch
            {
                "ns" => checked(value / 1_000_000L),
                "us" => checked(value / 1_000L),
                "ms" => value,
                "s" => checked(value * 1_000L),
                "m" => checked(value * 60_000L),
                "h" => checked(value * 3_600_000L),
                "d" => checked(value * 86_400_000L),
                _ => throw new SqlParseException($"未知的 duration 单位 '{suffix}'", position),
            };
        }
        catch (OverflowException)
        {
            throw new SqlParseException($"duration 计算溢出（{value}{suffix}）", position);
        }
    }

    private void SkipWhitespaceAndComments()
    {
        while (_position < _source.Length)
        {
            var rest = _source.AsSpan(_position);
            var firstNonWhitespace = rest.IndexOfAnyExcept(_asciiWhitespace);
            if (firstNonWhitespace < 0)
            {
                _position = _source.Length;
                return;
            }
            if (firstNonWhitespace > 0)
                _position += firstNonWhitespace;

            var ch = _source[_position];
            if (IsWhitespace(ch))
            {
                _position++;
                continue;
            }
            if (IsLineCommentStart())
            {
                SkipToEndOfLine();
                continue;
            }
            // 块注释 /* ... */
            if (ch == '/' && Peek(1) == '*')
            {
                var commentStart = _position;
                _position += 2;
                while (_position < _source.Length && !(_source[_position] == '*' && Peek(1) == '/'))
                    _position++;
                if (_position >= _source.Length)
                    throw new SqlParseException("未闭合的块注释", commentStart);
                _position += 2;
                continue;
            }
            return;
        }
    }

    private bool IsLineCommentStart()
    {
        var ch = _source[_position];
        if (ch == '-' && Peek(1) == '-')
            return true;

        if (ch == '/' && Peek(1) == '/')
            return true;

        return IsRemCommentStart();
    }

    private bool IsRemCommentStart()
    {
        if (!StartsWithIgnoreCase("rem"))
            return false;

        var next = Peek(3);
        if (next != '\0' && !IsWhitespace(next))
            return false;

        return IsAtLineStartOrAfterStatementTerminator();
    }

    private void SkipToEndOfLine()
    {
        _position += StartsWithIgnoreCase("rem") ? 3 : 2;
        var rest = _source.AsSpan(_position);
        var lineBreak = rest.IndexOfAny(_lineBreaks);
        _position += lineBreak < 0 ? rest.Length : lineBreak;
    }

    private bool StartsWithIgnoreCase(string value)
    {
        if (_position + value.Length > _source.Length)
            return false;

        for (var i = 0; i < value.Length; i++)
        {
            if (char.ToUpperInvariant(_source[_position + i]) != char.ToUpperInvariant(value[i]))
                return false;
        }

        return true;
    }

    private bool IsAtLineStartOrAfterStatementTerminator()
    {
        for (var index = _position - 1; index >= 0; index--)
        {
            var ch = _source[index];
            if (ch == '\n' || ch == '\r')
                return true;

            if (!IsWhitespace(ch))
                return ch == ';';
        }

        return true;
    }

    private char Peek(int offset)
    {
        var index = _position + offset;
        return index < _source.Length ? _source[index] : '\0';
    }

    private void AdvanceIdentifierContinue()
    {
        while (_position < _source.Length)
        {
            var rest = _source.AsSpan(_position);
            var firstNonIdentifier = rest.IndexOfAnyExcept(_asciiIdentifierContinue);
            if (firstNonIdentifier < 0)
            {
                _position = _source.Length;
                return;
            }

            _position += firstNonIdentifier;
            if (_position >= _source.Length || !IsIdentifierContinue(_source[_position]))
                return;

            _position++;
        }
    }

    private void AdvanceAsciiDigits()
    {
        var rest = _source.AsSpan(_position);
        var firstNonDigit = rest.IndexOfAnyExcept(_asciiDigits);
        _position += firstNonDigit < 0 ? rest.Length : firstNonDigit;
    }

    private static bool IsIdentifierStart(char ch)
        => _asciiIdentifierStart.Contains(ch) || (ch > '\u007f' && char.IsLetter(ch));

    private static bool IsIdentifierContinue(char ch)
        => _asciiIdentifierContinue.Contains(ch) || (ch > '\u007f' && char.IsLetterOrDigit(ch));

    private static bool IsWhitespace(char ch)
        => _asciiWhitespace.Contains(ch) || (ch > '\u007f' && char.IsWhiteSpace(ch));

    private static bool IsAsciiDigit(char ch) => _asciiDigits.Contains(ch);

    private static bool IsDurationSuffixStart(char ch)
        => _durationSuffixStarts.Contains(ch);

    private static bool IsDurationTwoCharSuffix(string s, int index)
    {
        var a = s[index];
        var b = s[index + 1];
        // 两字符单位：ns / us / ms
        return (a == 'n' && b == 's') || (a == 'u' && b == 's') || (a == 'm' && b == 's');
    }
}
