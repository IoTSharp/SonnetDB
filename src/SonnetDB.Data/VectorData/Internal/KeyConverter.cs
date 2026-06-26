using System.Globalization;

namespace SonnetDB.Data.VectorData.Internal;

internal static class KeyConverter<TKey>
    where TKey : notnull
{
    private static readonly Func<TKey, string> ToStringFn = CreateToString();
    private static readonly Func<string, TKey> FromStringFn = CreateFromString();

    public static string ToStorageId(TKey key) => ToStringFn(key);

    public static TKey FromStorageId(string id) => FromStringFn(id);

    private static Func<TKey, string> CreateToString()
    {
        var type = typeof(TKey);
        if (type == typeof(string))
            return key => (string)(object)key!;
        if (type == typeof(int))
            return key => ((int)(object)key!).ToString(CultureInfo.InvariantCulture);
        if (type == typeof(long))
            return key => ((long)(object)key!).ToString(CultureInfo.InvariantCulture);
        if (type == typeof(Guid))
            return key => ((Guid)(object)key!).ToString("D", CultureInfo.InvariantCulture);
        return _ => throw new NotSupportedException(
            $"SonnetDB VectorData 暂不支持键类型 {type.FullName}；请使用 string/int/long/Guid。");
    }

    private static Func<string, TKey> CreateFromString()
    {
        var type = typeof(TKey);
        if (type == typeof(string))
            return id => (TKey)(object)id;
        if (type == typeof(int))
            return id => (TKey)(object)int.Parse(id, CultureInfo.InvariantCulture);
        if (type == typeof(long))
            return id => (TKey)(object)long.Parse(id, CultureInfo.InvariantCulture);
        if (type == typeof(Guid))
            return id => (TKey)(object)Guid.Parse(id, CultureInfo.InvariantCulture);
        return _ => throw new NotSupportedException(
            $"SonnetDB VectorData 暂不支持键类型 {type.FullName}；请使用 string/int/long/Guid。");
    }
}
