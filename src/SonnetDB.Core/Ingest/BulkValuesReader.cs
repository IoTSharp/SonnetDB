using System.Globalization;
using SonnetDB.Model;

namespace SonnetDB.Ingest;

/// <summary>
/// <c>INSERT INTO</c> 列绑定角色，由调用方根据目标 measurement 的 schema 提供。
/// </summary>
public enum BulkValuesColumnRole
{
    /// <summary>自动推断列角色：所有非 NULL 字面量均按 field 写入。</summary>
    Auto,

    /// <summary>tag 列：必须为字符串字面量。</summary>
    Tag,

    /// <summary>field 列：值类型按 schema 解释（数字、布尔、字符串）。</summary>
    Field,

    /// <summary>时间戳列：长整型（毫秒）。</summary>
    Time,
}

/// <summary>
/// PostgreSQL <c>COPY</c> 风格的 bulk INSERT VALUES reader：
/// 整段文本只解析一次表头与 measurement，后续 VALUES 走快路径，逐行构造 <see cref="Point"/>。
/// </summary>
/// <remarks>
/// <para>支持语法（与 <c>SqlExecutor.ExecuteInsert</c> 行为对齐，但不走完整 SQL 流水线）：</para>
/// <code>
/// INSERT INTO &lt;measurement&gt;(&lt;col1&gt;,&lt;col2&gt;,...) VALUES
///   (&lt;v1&gt;, &lt;v2&gt;, ...),
///   (&lt;v1&gt;, &lt;v2&gt;, ...),
///   ...;
/// </code>
/// <para>列角色由 <see cref="BulkValuesColumnRole"/> resolver 提供（通常由 PR #B/#C 桥接 <c>tsdb.Measurements</c>）。</para>
/// </remarks>
public sealed class BulkValuesReader : IPointReader
{
    private readonly string _payload;
    private readonly Func<string, BulkValuesColumnRole> _columnRoleResolver;
    private readonly string _measurement;
    private readonly string[] _columnNames;
    private readonly BulkValuesColumnRole[] _columnRoles;
    private readonly int _timeColumnIndex;
    private int _cursor;

    /// <summary>解析得到的目标 measurement 名。</summary>
    public string Measurement => _measurement;

    /// <summary>解析得到的列名顺序。</summary>
    public IReadOnlyList<string> Columns => _columnNames;

    /// <summary>构造一个 bulk INSERT VALUES reader。</summary>
    /// <param name="payload">完整的 SQL 文本（自 <c>INSERT</c> 起，单语句）。</param>
    /// <param name="columnRoleResolver">列名 → 角色解析函数；未知列应抛 <see cref="BulkIngestException"/>。</param>
    /// <param name="measurementOverride">若非 <c>null</c>，则忽略 SQL 中 measurement，统一替换为此值。</param>
    public BulkValuesReader(
        string payload,
        Func<string, BulkValuesColumnRole> columnRoleResolver,
        string? measurementOverride = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(columnRoleResolver);
        _payload = payload;
        _columnRoleResolver = columnRoleResolver;

        // 表头：INSERT INTO <m>(<cols>) VALUES
        ParseHeader(out _measurement, out _columnNames, out var roleHints, out _cursor);
        if (measurementOverride is not null) _measurement = measurementOverride;

        _columnRoles = new BulkValuesColumnRole[_columnNames.Length];
        _timeColumnIndex = -1;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < _columnNames.Length; i++)
        {
            var name = _columnNames[i];
            if (string.Equals(name, "time", StringComparison.OrdinalIgnoreCase))
            {
                if (roleHints[i] != BulkValuesColumnRole.Auto)
                    throw new BulkIngestException("Bulk INSERT: time 伪列不能指定 TAG / FIELD 提示。");
                if (_timeColumnIndex >= 0)
                    throw new BulkIngestException("Bulk INSERT: 列列表中 'time' 出现多次。");
                _columnRoles[i] = BulkValuesColumnRole.Time;
                _timeColumnIndex = i;
                continue;
            }
            if (!seen.Add(name))
                throw new BulkIngestException($"Bulk INSERT: 列列表中列 '{name}' 重复。");
            var resolvedRole = _columnRoleResolver(name);
            if (roleHints[i] != BulkValuesColumnRole.Auto
                && resolvedRole != BulkValuesColumnRole.Auto
                && resolvedRole != roleHints[i])
            {
                throw new BulkIngestException(
                    $"Bulk INSERT: 列 '{name}' 的 {roleHints[i]} 提示与已有 schema 的 {resolvedRole} 角色不一致。");
            }
            _columnRoles[i] = roleHints[i] == BulkValuesColumnRole.Auto
                ? resolvedRole
                : roleHints[i];
        }
    }

    /// <inheritdoc />
    public bool TryRead(out Point point)
    {
        var span = _payload.AsSpan();
        SkipWhitespaceAndCommas(span, ref _cursor);
        if (_cursor >= span.Length || span[_cursor] == ';')
        {
            point = null!;
            return false;
        }

        if (span[_cursor] != '(')
            throw new BulkIngestException(
                $"Bulk INSERT: 期望 '(' 开始下一行 VALUES，实际 '{span[_cursor]}'（位置 {_cursor}）。");
        _cursor++; // 吃掉 '('

        Dictionary<string, string>? tags = null;
        Dictionary<string, FieldValue>? fields = null;
        long timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        for (int i = 0; i < _columnNames.Length; i++)
        {
            SkipWhitespace(span, ref _cursor);
            var literal = ReadLiteral(span);

            switch (_columnRoles[i])
            {
                case BulkValuesColumnRole.Auto:
                    if (literal.Kind == LiteralKind.Null)
                    {
                        throw new BulkIngestException(
                            $"Bulk INSERT: 自动推断列 '{_columnNames[i]}' 不允许 NULL。");
                    }
                    fields ??= new Dictionary<string, FieldValue>(StringComparer.Ordinal);
                    fields[_columnNames[i]] = LiteralToFieldValue(literal, _columnNames[i]);
                    break;
                case BulkValuesColumnRole.Time:
                    if (literal.Kind != LiteralKind.Integer)
                        throw new BulkIngestException(
                            $"Bulk INSERT: 'time' 列必须为整数毫秒时间戳，实际 {literal.Kind}。");
                    timestampMs = literal.IntValue;
                    break;
                case BulkValuesColumnRole.Tag:
                    if (literal.Kind != LiteralKind.String)
                        throw new BulkIngestException(
                            $"Bulk INSERT: tag 列 '{_columnNames[i]}' 必须为字符串字面量，实际 {literal.Kind}。");
                    tags ??= new Dictionary<string, string>(StringComparer.Ordinal);
                    tags[_columnNames[i]] = literal.StringValue!;
                    break;
                case BulkValuesColumnRole.Field:
                    fields ??= new Dictionary<string, FieldValue>(StringComparer.Ordinal);
                    fields[_columnNames[i]] = LiteralToFieldValue(literal, _columnNames[i]);
                    break;
            }

            SkipWhitespace(span, ref _cursor);
            if (i < _columnNames.Length - 1)
            {
                if (_cursor >= span.Length || span[_cursor] != ',')
                    throw new BulkIngestException(
                        $"Bulk INSERT: 列 #{i + 1} 后期望 ','，实际 '{(_cursor < span.Length ? span[_cursor] : '∅')}'。");
                _cursor++;
            }
        }

        SkipWhitespace(span, ref _cursor);
        if (_cursor >= span.Length || span[_cursor] != ')')
            throw new BulkIngestException(
                $"Bulk INSERT: 期望 ')' 结束行 VALUES，实际 '{(_cursor < span.Length ? span[_cursor] : '∅')}'。");
        _cursor++;

        if (fields is null || fields.Count == 0)
            throw new BulkIngestException("Bulk INSERT: 行至少需要一个 field 列值。");

        point = Point.Create(_measurement, timestampMs, tags, fields);
        return true;
    }

    // ── 表头解析 ──────────────────────────────────────────────────────────

    private void ParseHeader(
        out string measurement,
        out string[] columns,
        out BulkValuesColumnRole[] roleHints,
        out int valuesCursor)
    {
        var span = _payload.AsSpan();
        int idx = 0;
        SkipWhitespace(span, ref idx);
        ExpectKeyword(span, ref idx, "INSERT");
        SkipWhitespace(span, ref idx);
        ExpectKeyword(span, ref idx, "INTO");
        SkipWhitespace(span, ref idx);

        measurement = ReadIdentifier(span, ref idx);
        SkipWhitespace(span, ref idx);
        if (idx >= span.Length || span[idx] != '(')
            throw new BulkIngestException(
                $"Bulk INSERT: measurement 后期望 '('，实际 '{(idx < span.Length ? span[idx] : '∅')}'（位置 {idx}）。");
        idx++;

        var cols = new List<string>();
        var hints = new List<BulkValuesColumnRole>();
        while (true)
        {
            SkipWhitespace(span, ref idx);
            cols.Add(ReadIdentifier(span, ref idx));
            SkipWhitespace(span, ref idx);
            if (TryConsumeKeyword(span, ref idx, "TAG"))
                hints.Add(BulkValuesColumnRole.Tag);
            else if (TryConsumeKeyword(span, ref idx, "FIELD"))
                hints.Add(BulkValuesColumnRole.Field);
            else
                hints.Add(BulkValuesColumnRole.Auto);
            SkipWhitespace(span, ref idx);
            if (idx < span.Length && span[idx] == ',') { idx++; continue; }
            if (idx < span.Length && span[idx] == ')') { idx++; break; }
            throw new BulkIngestException(
                $"Bulk INSERT: 列列表中期望 ',' 或 ')'，实际 '{(idx < span.Length ? span[idx] : '∅')}'。");
        }
        columns = cols.ToArray();
        roleHints = hints.ToArray();

        SkipWhitespace(span, ref idx);
        ExpectKeyword(span, ref idx, "VALUES");
        valuesCursor = idx;
    }

    // ── 工具方法 ──────────────────────────────────────────────────────────

    private static void SkipWhitespace(ReadOnlySpan<char> span, ref int idx)
    {
        while (idx < span.Length)
        {
            char c = span[idx];
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { idx++; continue; }
            break;
        }
    }

    private static void SkipWhitespaceAndCommas(ReadOnlySpan<char> span, ref int idx)
    {
        while (idx < span.Length)
        {
            char c = span[idx];
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == ',') { idx++; continue; }
            break;
        }
    }

    private static void ExpectKeyword(ReadOnlySpan<char> span, ref int idx, string kw)
    {
        if (idx + kw.Length > span.Length)
            throw new BulkIngestException($"Bulk INSERT: 期望关键字 '{kw}'。");
        for (int i = 0; i < kw.Length; i++)
        {
            char a = span[idx + i];
            char b = kw[i];
            if (char.ToUpperInvariant(a) != b)
                throw new BulkIngestException($"Bulk INSERT: 期望关键字 '{kw}'，实际 '{new string(span.Slice(idx, kw.Length))}'。");
        }
        idx += kw.Length;
    }

    private static bool TryConsumeKeyword(ReadOnlySpan<char> span, ref int idx, string keyword)
    {
        if (idx + keyword.Length > span.Length)
            return false;
        for (int i = 0; i < keyword.Length; i++)
        {
            if (char.ToUpperInvariant(span[idx + i]) != keyword[i])
                return false;
        }
        int end = idx + keyword.Length;
        if (end < span.Length && (char.IsLetterOrDigit(span[end]) || span[end] == '_'))
            return false;
        idx = end;
        return true;
    }

    private static string ReadIdentifier(ReadOnlySpan<char> span, ref int idx)
    {
        // 支持 "..." / `...` / 简单标识符（字母/数字/_）
        if (idx >= span.Length)
            throw new BulkIngestException("Bulk INSERT: 期望标识符，遇到末尾。");

        char first = span[idx];
        if (first == '"' || first == '`')
        {
            char quote = first;
            int start = ++idx;
            while (idx < span.Length && span[idx] != quote) idx++;
            if (idx >= span.Length)
                throw new BulkIngestException($"Bulk INSERT: 未闭合的标识符引号 {quote}。");
            string id = new(span[start..idx]);
            idx++;
            return id;
        }

        int s = idx;
        while (idx < span.Length && IsIdentifierChar(span[idx])) idx++;
        if (idx == s)
            throw new BulkIngestException($"Bulk INSERT: 期望标识符，实际 '{span[idx]}'（位置 {idx}）。");
        return new string(span[s..idx]);
    }

    private static bool IsIdentifierChar(char c)
        => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_';

    // ── 字面量 ────────────────────────────────────────────────────────────

    private enum LiteralKind { Integer, Double, String, Bool, Null }

    private readonly struct Literal
    {
        public LiteralKind Kind { get; init; }
        public long IntValue { get; init; }
        public double DoubleValue { get; init; }
        public string? StringValue { get; init; }
    }

    private Literal ReadLiteral(ReadOnlySpan<char> span)
    {
        if (_cursor >= span.Length)
            throw new BulkIngestException("Bulk INSERT: 期望字面量，遇到末尾。");

        char c = span[_cursor];

        // string '...'，内部 '' 转义为单引号
        if (c == '\'')
        {
            int start = ++_cursor;
            var sb = new System.Text.StringBuilder();
            while (_cursor < span.Length)
            {
                char ch = span[_cursor];
                if (ch == '\'')
                {
                    if (_cursor + 1 < span.Length && span[_cursor + 1] == '\'')
                    {
                        sb.Append('\'');
                        _cursor += 2;
                        continue;
                    }
                    _cursor++; // 吃掉收尾 '
                    return new Literal { Kind = LiteralKind.String, StringValue = sb.ToString() };
                }
                sb.Append(ch);
                _cursor++;
            }
            throw new BulkIngestException("Bulk INSERT: 未闭合的字符串字面量。");
        }

        // null / true / false
        if (TryConsumeKeyword(span, "NULL"))
            return new Literal { Kind = LiteralKind.Null };
        if (TryConsumeKeyword(span, "TRUE"))
            return new Literal { Kind = LiteralKind.Bool, IntValue = 1 };
        if (TryConsumeKeyword(span, "FALSE"))
            return new Literal { Kind = LiteralKind.Bool, IntValue = 0 };

        // 数字
        int s = _cursor;
        if (c == '+' || c == '-') _cursor++;
        bool sawDot = false;
        bool sawExp = false;
        while (_cursor < span.Length)
        {
            char d = span[_cursor];
            if (d >= '0' && d <= '9') { _cursor++; continue; }
            if (d == '.' && !sawDot && !sawExp) { sawDot = true; _cursor++; continue; }
            if ((d == 'e' || d == 'E') && !sawExp)
            {
                sawExp = true;
                _cursor++;
                if (_cursor < span.Length && (span[_cursor] == '+' || span[_cursor] == '-')) _cursor++;
                continue;
            }
            break;
        }
        if (_cursor == s || (_cursor == s + 1 && (span[s] == '+' || span[s] == '-')))
            throw new BulkIngestException(
                $"Bulk INSERT: 无法识别字面量，起始字符 '{c}'（位置 {s}）。");

        var numSpan = span[s.._cursor];
        if (!sawDot && !sawExp)
        {
            if (long.TryParse(numSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out long lv))
                return new Literal { Kind = LiteralKind.Integer, IntValue = lv };
        }
        if (double.TryParse(numSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv))
            return new Literal { Kind = LiteralKind.Double, DoubleValue = dv };
        throw new BulkIngestException($"Bulk INSERT: 无法解析数字 '{new string(numSpan)}'。");
    }

    private bool TryConsumeKeyword(ReadOnlySpan<char> span, string kw)
    {
        if (_cursor + kw.Length > span.Length) return false;
        for (int i = 0; i < kw.Length; i++)
        {
            if (char.ToUpperInvariant(span[_cursor + i]) != kw[i]) return false;
        }
        // 后续不能是标识符延续字符
        int after = _cursor + kw.Length;
        if (after < span.Length && IsIdentifierChar(span[after])) return false;
        _cursor += kw.Length;
        return true;
    }

    private static FieldValue LiteralToFieldValue(Literal literal, string columnName)
        => literal.Kind switch
        {
            LiteralKind.Integer => FieldValue.FromLong(literal.IntValue),
            LiteralKind.Double => FieldValue.FromDouble(literal.DoubleValue),
            LiteralKind.Bool => FieldValue.FromBool(literal.IntValue != 0),
            LiteralKind.String => FieldValue.FromString(literal.StringValue!),
            _ => throw new BulkIngestException(
                $"Bulk INSERT: field 列 '{columnName}' 不允许 NULL。"),
        };
}
