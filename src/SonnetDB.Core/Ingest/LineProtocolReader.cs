using System.Globalization;
using SonnetDB.Model;

namespace SonnetDB.Ingest;

/// <summary>
/// 批量入库 reader 通用契约。流式产出 <see cref="Point"/>，到达末尾返回 <c>false</c>。
/// </summary>
public interface IPointReader
{
    /// <summary>
    /// 读取下一个 <see cref="Point"/>。
    /// </summary>
    /// <param name="point">读取到的数据点。</param>
    /// <returns>读取成功返回 <c>true</c>；已无数据返回 <c>false</c>。</returns>
    /// <exception cref="BulkIngestException">payload 格式无效时抛出。</exception>
    bool TryRead(out Point point);
}

/// <summary>
/// InfluxDB Line Protocol 子集 reader：
/// <code>measurement[,tag=val,...] field=val[,field2=val2,...] [timestamp]</code>
/// 按 <c>\n</c> 或 <c>\r\n</c> 分行；空行、<c>#</c> 起始的行将被跳过。
/// </summary>
/// <remarks>
/// <para>
/// 第一版限制：
/// <list type="bullet">
/// <item>field value 仅支持 <c>double</c>（裸数字）、<c>integer</c>（带 <c>i</c> 后缀，如 <c>42i</c>）、<c>bool</c>（<c>t/T/true/True/TRUE/f/F/false/False/FALSE</c>）、<c>string</c>（双引号包裹）。</item>
/// <item>转义字符仅支持 <c>\,</c> <c>\=</c> <c>\ </c> 在 measurement / tag key / tag value / field key 中。</item>
/// <item>无 timestamp 时按 <see cref="DefaultTimestampMs"/> 取值（默认本地 UTC now，毫秒）。</item>
/// </list>
/// </para>
/// </remarks>
public sealed class LineProtocolReader : IPointReader
{
    private readonly ReadOnlyMemory<char> _payload;
    private readonly TimePrecision _precision;
    private readonly string? _measurementOverride;
    private int _cursor;

    /// <summary>未提供 timestamp 时使用的默认毫秒时间戳。可外部注入便于测试。</summary>
    public long DefaultTimestampMs { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>构造一个 Line Protocol reader。</summary>
    /// <param name="payload">完整的 LP 文本。</param>
    /// <param name="precision">timestamp 精度（默认毫秒）。</param>
    /// <param name="measurementOverride">若非 <c>null</c>，则忽略行内 measurement，统一使用此值。</param>
    public LineProtocolReader(
        ReadOnlyMemory<char> payload,
        TimePrecision precision = TimePrecision.Milliseconds,
        string? measurementOverride = null)
    {
        _payload = payload;
        _precision = precision;
        _measurementOverride = measurementOverride;
    }

    /// <inheritdoc />
    public bool TryRead(out Point point)
    {
        var span = _payload.Span;
        while (_cursor < span.Length)
        {
            // 1) 切出本行
            int lineStart = _cursor;
            int lineEnd = lineStart;
            while (lineEnd < span.Length && span[lineEnd] != '\n') lineEnd++;
            int nextCursor = lineEnd < span.Length ? lineEnd + 1 : lineEnd;
            // 去掉行尾 \r
            int realEnd = lineEnd;
            if (realEnd > lineStart && span[realEnd - 1] == '\r') realEnd--;
            _cursor = nextCursor;

            // 2) 跳过空行 / 注释
            int s = lineStart;
            while (s < realEnd && (span[s] == ' ' || span[s] == '\t')) s++;
            if (s >= realEnd) continue;
            if (span[s] == '#') continue;

            try
            {
                point = ParseLine(span.Slice(s, realEnd - s));
                return true;
            }
            catch (BulkIngestException)
            {
                throw;
            }
            catch (Exception exception) when (exception is ArgumentException or OverflowException)
            {
                throw new BulkIngestException($"Line Protocol: {exception.Message}", exception);
            }
        }

        point = null!;
        return false;
    }

    private Point ParseLine(ReadOnlySpan<char> line)
    {
        // 三段式：measurement[,tags] SP fields[ SP timestamp]
        // 1) measurement[,tags]
        int sp1 = FindUnescapedSpace(line, 0);
        if (sp1 < 0)
            throw new BulkIngestException("Line Protocol: 缺少 measurement 与 fields 之间的空格。");

        var headSeg = line[..sp1];
        int afterHead = sp1 + 1;

        // measurement + tags 拆分
        int comma = FindUnescapedComma(headSeg, 0);
        string measurement = _measurementOverride
            ?? Unescape(comma < 0 ? headSeg : headSeg[..comma]);
        var tags = comma < 0
            ? (IReadOnlyDictionary<string, string>?)null
            : ParseTags(headSeg[(comma + 1)..]);

        // 2) fields[ SP timestamp]
        // fields 段也允许包含 string field 内的双引号，需识别引号转义
        int sp2 = FindFieldsEnd(line, afterHead);
        ReadOnlySpan<char> fieldsSeg;
        long timestampMs = DefaultTimestampMs;
        if (sp2 < 0)
        {
            fieldsSeg = line[afterHead..];
        }
        else
        {
            fieldsSeg = line[afterHead..sp2];
            int tsStart = sp2 + 1;
            while (tsStart < line.Length && (line[tsStart] == ' ' || line[tsStart] == '\t')) tsStart++;
            if (tsStart < line.Length)
                timestampMs = ParseTimestamp(line[tsStart..]);
        }

        var fields = ParseFields(fieldsSeg);
        try
        {
            return Point.Create(measurement, timestampMs, tags, fields);
        }
        catch (ArgumentException exception)
        {
            throw new BulkIngestException($"Line Protocol: {exception.Message}", exception);
        }
    }

    private static int FindUnescapedSpace(ReadOnlySpan<char> span, int start)
    {
        bool inQuotes = false;
        for (int i = start; i < span.Length; i++)
        {
            char c = span[i];
            if (c == '\\' && i + 1 < span.Length) { i++; continue; }
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (!inQuotes && c == ' ') return i;
        }
        return -1;
    }

    private static int FindFieldsEnd(ReadOnlySpan<char> line, int start)
        => FindUnescapedSpace(line, start);

    private static int FindUnescapedComma(ReadOnlySpan<char> span, int start)
    {
        for (int i = start; i < span.Length; i++)
        {
            char c = span[i];
            if (c == '\\' && i + 1 < span.Length) { i++; continue; }
            if (c == ',') return i;
        }
        return -1;
    }

    private static int FindUnescapedEquals(ReadOnlySpan<char> span)
    {
        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            if (c == '\\' && i + 1 < span.Length) { i++; continue; }
            if (c == '=') return i;
        }
        return -1;
    }

    private static IReadOnlyDictionary<string, string> ParseTags(ReadOnlySpan<char> seg)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        int idx = 0;
        while (idx < seg.Length)
        {
            int comma = FindUnescapedComma(seg, idx);
            int end = comma < 0 ? seg.Length : comma;
            var pair = seg[idx..end];
            int eq = FindUnescapedEquals(pair);
            if (eq <= 0 || eq == pair.Length - 1)
                throw new BulkIngestException($"Line Protocol: tag 段缺少 key=value（'{new string(pair)}'）。");
            dict[Unescape(pair[..eq])] = Unescape(pair[(eq + 1)..]);
            idx = comma < 0 ? seg.Length : comma + 1;
        }
        return dict;
    }

    private static IReadOnlyDictionary<string, FieldValue> ParseFields(ReadOnlySpan<char> seg)
    {
        var dict = new Dictionary<string, FieldValue>(StringComparer.Ordinal);
        int idx = 0;
        while (idx < seg.Length)
        {
            // 找下一个未转义、未在引号内的 ','
            int comma = FindFieldsSeparator(seg, idx);
            int end = comma < 0 ? seg.Length : comma;
            var pair = seg[idx..end];
            int eq = FindUnescapedEquals(pair);
            if (eq <= 0 || eq == pair.Length - 1)
                throw new BulkIngestException($"Line Protocol: field 段缺少 key=value（'{new string(pair)}'）。");
            string key = Unescape(pair[..eq]);
            var valueSpan = pair[(eq + 1)..];
            dict[key] = ParseFieldValue(valueSpan);
            idx = comma < 0 ? seg.Length : comma + 1;
        }
        if (dict.Count == 0)
            throw new BulkIngestException("Line Protocol: 至少需要一个 field。");
        return dict;
    }

    private static int FindFieldsSeparator(ReadOnlySpan<char> span, int start)
    {
        bool inQuotes = false;
        for (int i = start; i < span.Length; i++)
        {
            char c = span[i];
            if (c == '\\' && i + 1 < span.Length) { i++; continue; }
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (!inQuotes && c == ',') return i;
        }
        return -1;
    }

    private static FieldValue ParseFieldValue(ReadOnlySpan<char> v)
    {
        if (v.Length == 0)
            throw new BulkIngestException("Line Protocol: field value 不能为空。");

        // string："..."
        if (v[0] == '"')
        {
            if (v.Length < 2 || v[^1] != '"')
                throw new BulkIngestException("Line Protocol: 未闭合的字符串 field。");
            return FieldValue.FromString(UnescapeQuoted(v[1..^1]));
        }

        // bool
        if (v.Length == 1 && (v[0] == 't' || v[0] == 'T')) return FieldValue.FromBool(true);
        if (v.Length == 1 && (v[0] == 'f' || v[0] == 'F')) return FieldValue.FromBool(false);
        if (Equals(v, "true") || Equals(v, "True") || Equals(v, "TRUE")) return FieldValue.FromBool(true);
        if (Equals(v, "false") || Equals(v, "False") || Equals(v, "FALSE")) return FieldValue.FromBool(false);

        // integer：以 i / I 结尾
        char last = v[^1];
        if (last == 'i' || last == 'I')
        {
            if (long.TryParse(v[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long l))
                return FieldValue.FromLong(l);
            throw new BulkIngestException($"Line Protocol: 无法解析整数 field value '{new string(v)}'。");
        }

        // double
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            return FieldValue.FromDouble(d);

        throw new BulkIngestException($"Line Protocol: 无法解析 field value '{new string(v)}'。");
    }

    private static bool Equals(ReadOnlySpan<char> span, string literal)
        => span.Length == literal.Length && span.SequenceEqual(literal.AsSpan());

    private long ParseTimestamp(ReadOnlySpan<char> tsSpan)
    {
        // 取连续数字段（允许前导 -）
        int end = 0;
        if (end < tsSpan.Length && (tsSpan[end] == '-' || tsSpan[end] == '+')) end++;
        while (end < tsSpan.Length && tsSpan[end] >= '0' && tsSpan[end] <= '9') end++;
        if (end == 0)
            throw new BulkIngestException($"Line Protocol: 无法解析 timestamp '{new string(tsSpan)}'。");

        if (!long.TryParse(tsSpan[..end], NumberStyles.Integer, CultureInfo.InvariantCulture, out long raw))
            throw new BulkIngestException($"Line Protocol: timestamp 超出 Int64 范围 '{new string(tsSpan[..end])}'。");

        return _precision switch
        {
            TimePrecision.Nanoseconds => raw / 1_000_000L,
            TimePrecision.Microseconds => raw / 1_000L,
            TimePrecision.Milliseconds => raw,
            TimePrecision.Seconds => checked(raw * 1_000L),
            _ => raw,
        };
    }

    private static string Unescape(ReadOnlySpan<char> span)
    {
        // 仅识别 \, \= \space
        bool needs = false;
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == '\\') { needs = true; break; }
        }
        if (!needs) return new string(span);

        var sb = new System.Text.StringBuilder(span.Length);
        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            if (c == '\\' && i + 1 < span.Length)
            {
                char n = span[i + 1];
                if (n == ',' || n == '=' || n == ' ' || n == '\\')
                {
                    sb.Append(n);
                    i++;
                    continue;
                }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string UnescapeQuoted(ReadOnlySpan<char> span)
    {
        bool needs = false;
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i] == '\\') { needs = true; break; }
        }
        if (!needs) return new string(span);

        var sb = new System.Text.StringBuilder(span.Length);
        for (int i = 0; i < span.Length; i++)
        {
            char c = span[i];
            if (c == '\\' && i + 1 < span.Length)
            {
                char n = span[i + 1];
                if (n == '"' || n == '\\')
                {
                    sb.Append(n);
                    i++;
                    continue;
                }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
