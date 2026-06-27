using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SonnetDB.Documents;

/// <summary>
/// Document find 游标 token 编解码器。
/// </summary>
public static class DocumentCursorToken
{
    private const int CurrentVersion = 1;

    /// <summary>
    /// 编码游标状态。
    /// </summary>
    /// <param name="state">游标状态。</param>
    /// <returns>可放入 HTTP JSON 的 token 文本。</returns>
    public static string Encode(DocumentCursorState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        string payload = string.Join("\n", new[]
        {
            CurrentVersion.ToString(CultureInfo.InvariantCulture),
            Escape(state.Collection),
            Escape(state.QueryFingerprint),
            state.SnapshotVersion.ToString(CultureInfo.InvariantCulture),
            state.ExpiresAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture),
            state.Offset.ToString(CultureInfo.InvariantCulture),
            Escape(state.LastId ?? string.Empty),
        });
        string signature = Sign(payload);
        return Base64UrlEncode(Encoding.UTF8.GetBytes(payload + "\n" + signature));
    }

    /// <summary>
    /// 解码并校验游标 token。
    /// </summary>
    /// <param name="token">游标 token。</param>
    /// <returns>解码后的游标状态。</returns>
    public static DocumentCursorState Decode(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        string text;
        try
        {
            text = Encoding.UTF8.GetString(Base64UrlDecode(token));
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("document cursor token is invalid.", ex);
        }

        string[] parts = text.Split('\n');
        if (parts.Length != 8)
            throw new InvalidOperationException("document cursor token is invalid.");

        string payload = string.Join("\n", parts.AsSpan(0, 7).ToArray());
        if (!FixedTimeEquals(parts[7], Sign(payload)))
            throw new InvalidOperationException("document cursor token signature is invalid.");

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int version)
            || version != CurrentVersion
            || !long.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out long snapshotVersion)
            || !long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out long expiresAtTicks)
            || !int.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out int offset)
            || offset < 0)
        {
            throw new InvalidOperationException("document cursor token is invalid.");
        }

        string lastId = Unescape(parts[6]);
        return new DocumentCursorState(
            Collection: Unescape(parts[1]),
            QueryFingerprint: Unescape(parts[2]),
            SnapshotVersion: snapshotVersion,
            ExpiresAtUtc: new DateTimeOffset(expiresAtTicks, TimeSpan.Zero),
            Offset: offset,
            LastId: string.IsNullOrEmpty(lastId) ? null : lastId);
    }

    /// <summary>
    /// 计算查询形状指纹，用于检测续页请求是否改变了查询条件。
    /// </summary>
    /// <param name="collection">集合名称。</param>
    /// <param name="query">查询计划。</param>
    /// <returns>稳定的查询指纹。</returns>
    public static string Fingerprint(string collection, DocumentQuery query)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collection);
        ArgumentNullException.ThrowIfNull(query);

        var builder = new StringBuilder();
        builder.Append("collection=").Append(Escape(collection)).Append('\n');
        builder.Append("filter=");
        AppendFilter(builder, query.Filter);
        builder.Append('\n');
        builder.Append("projection=");
        if (query.Projection is not null)
        {
            foreach (var field in query.Projection.Fields)
            {
                builder.Append('[')
                    .Append(Escape(field.Name))
                    .Append(':');
                AppendField(builder, field.Field);
                builder.Append(']');
            }
        }

        builder.Append('\n');
        builder.Append("sort=");
        foreach (var sort in query.Sort)
        {
            builder.Append('[');
            AppendField(builder, sort.Field);
            builder.Append(':').Append(sort.Descending ? 'd' : 'a').Append(']');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void AppendFilter(StringBuilder builder, DocumentFilter? filter)
    {
        switch (filter)
        {
            case null:
                builder.Append("null");
                return;

            case DocumentAndFilter and:
                AppendFilterList(builder, "and", and.Filters);
                return;

            case DocumentOrFilter or:
                AppendFilterList(builder, "or", or.Filters);
                return;

            case DocumentNotFilter not:
                builder.Append("not(");
                AppendFilter(builder, not.Filter);
                builder.Append(')');
                return;

            case DocumentFieldFilter field:
                builder.Append("field(");
                AppendField(builder, field.Field);
                builder.Append(',')
                    .Append(field.Operator)
                    .Append(',');
                AppendValue(builder, field.Value);
                builder.Append(')');
                return;

            default:
                builder.Append(Escape(filter.GetType().FullName ?? filter.GetType().Name));
                return;
        }
    }

    private static void AppendFilterList(StringBuilder builder, string op, IReadOnlyList<DocumentFilter> filters)
    {
        builder.Append(op).Append('(');
        for (int i = 0; i < filters.Count; i++)
        {
            if (i > 0)
                builder.Append(',');
            AppendFilter(builder, filters[i]);
        }

        builder.Append(')');
    }

    private static void AppendField(StringBuilder builder, DocumentFieldRef field)
    {
        builder.Append(field.Kind).Append(':').Append(Escape(field.Path ?? string.Empty));
    }

    private static void AppendValue(StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                return;

            case JsonElement element:
                builder.Append("json:").Append(Escape(element.GetRawText()));
                return;

            case string text:
                builder.Append("string:").Append(Escape(text));
                return;

            case bool boolean:
                builder.Append("bool:").Append(boolean ? "true" : "false");
                return;
        }

        if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
        {
            builder.Append("number:").Append(Convert.ToString(value, CultureInfo.InvariantCulture));
            return;
        }

        if (value is System.Collections.IEnumerable sequence && value is not string)
        {
            builder.Append('[');
            bool first = true;
            foreach (var item in sequence)
            {
                if (!first)
                    builder.Append(',');
                AppendValue(builder, item);
                first = false;
            }

            builder.Append(']');
            return;
        }

        builder.Append("object:").Append(Escape(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty));
    }

    private static string Sign(string payload)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("SonnetDB.DocumentCursor.v1\n" + payload)));

    private static bool FixedTimeEquals(string left, string right)
    {
        byte[] leftBytes = Encoding.UTF8.GetBytes(left);
        byte[] rightBytes = Encoding.UTF8.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string Escape(string value)
        => Uri.EscapeDataString(value);

    private static string Unescape(string value)
        => Uri.UnescapeDataString(value);

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] Base64UrlDecode(string text)
    {
        string normalized = text.Replace('-', '+').Replace('_', '/');
        switch (normalized.Length % 4)
        {
            case 2:
                normalized += "==";
                break;
            case 3:
                normalized += "=";
                break;
        }

        return Convert.FromBase64String(normalized);
    }
}

/// <summary>
/// Document find 游标状态。
/// </summary>
/// <param name="Collection">集合名称。</param>
/// <param name="QueryFingerprint">查询形状指纹。</param>
/// <param name="SnapshotVersion">创建 token 时观察到的集合版本。</param>
/// <param name="ExpiresAtUtc">UTC 过期时间。</param>
/// <param name="Offset">高级查询续页偏移量。</param>
/// <param name="LastId">普通扫描上一页最后一个文档 ID。</param>
public sealed record DocumentCursorState(
    string Collection,
    string QueryFingerprint,
    long SnapshotVersion,
    DateTimeOffset ExpiresAtUtc,
    int Offset,
    string? LastId);
