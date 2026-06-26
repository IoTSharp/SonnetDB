using System.Globalization;

namespace SonnetDB.Data.VectorData.Internal;

internal static class SqlVectorStoreHelpers
{
    public static string FormatIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        if (!IsSqlIdentifier(identifier))
        {
            throw new ArgumentException(
                "SonnetDB VectorData collection 名称必须是 SonnetDB SQL 标识符：以字母或下划线开头，只包含字母、数字和下划线。",
                nameof(identifier));
        }

        return identifier;
    }

    public static string EscapeSqlString(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    public static string FormatVectorLiteral(IReadOnlyList<float> vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        if (vector.Count == 0)
            throw new ArgumentException("向量不能为空。", nameof(vector));
        return "[" + string.Join(", ", vector.Select(static v => v.ToString("R", CultureInfo.InvariantCulture))) + "]";
    }

    public static float[] ExtractVector<TInput>(TInput value)
        where TInput : notnull
        => value switch
        {
            float[] array => array,
            ReadOnlyMemory<float> memory => memory.ToArray(),
            Memory<float> memory => memory.ToArray(),
            IEnumerable<float> enumerable => enumerable.ToArray(),
            _ => throw new NotSupportedException(
                $"SonnetDB VectorData 当前仅支持 float[] / ReadOnlyMemory<float> 作为查询向量；收到 {typeof(TInput).Name}。"),
        };

    private static bool IsSqlIdentifier(string identifier)
    {
        if (identifier.Length == 0 || !IsIdentifierStart(identifier[0]))
            return false;
        for (int i = 1; i < identifier.Length; i++)
        {
            if (!IsIdentifierPart(identifier[i]))
                return false;
        }

        return true;
    }

    private static bool IsIdentifierStart(char value)
        => value == '_' || (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');

    private static bool IsIdentifierPart(char value)
        => IsIdentifierStart(value) || (value >= '0' && value <= '9');
}
