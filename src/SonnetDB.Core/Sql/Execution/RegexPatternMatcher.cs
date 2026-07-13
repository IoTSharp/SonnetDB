using System.Collections.Concurrent;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SonnetDB.Sql.Execution;

/// <summary>
/// SQL 与文档校验共用的正则匹配入口，统一限制模式、输入、执行时间和编译缓存。
/// </summary>
internal static class RegexPatternMatcher
{
    internal const int MaxPatternLength = 4 * 1024;
    internal const int MaxInputLength = 1024 * 1024;
    internal const int CacheCapacity = 128;
    internal static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly object CacheSync = new();
    private static readonly ConcurrentDictionary<RegexCacheKey, Regex> Cache = new();
    private static readonly Queue<RegexCacheKey> InsertionOrder = new();

    /// <summary>按默认大小写敏感规则判断输入是否匹配模式。</summary>
    public static bool IsMatch(object? value, object? pattern)
        => IsMatch(value, pattern, flags: null);

    /// <summary>
    /// 判断输入是否匹配模式。flags 支持 <c>i</c>、<c>m</c>、<c>s</c>、<c>x</c> 和显式恢复大小写敏感的 <c>c</c>。
    /// </summary>
    public static bool IsMatch(object? value, object? pattern, object? flags)
    {
        if (value is null || pattern is null)
            return false;

        string input = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        string regexPattern = Convert.ToString(pattern, CultureInfo.InvariantCulture) ?? string.Empty;
        string? regexFlags = flags is null
            ? null
            : Convert.ToString(flags, CultureInfo.InvariantCulture);

        ValidateInput(input);
        return GetRegex(regexPattern, regexFlags).IsMatch(input);
    }

    /// <summary>提前校验并缓存一个正则模式，供 schema/validator 创建阶段使用。</summary>
    public static void ValidatePattern(string pattern, string? flags = null)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        _ = GetRegex(pattern, flags);
    }

    /// <summary>清空有界正则缓存，仅供测试和显式资源回收使用。</summary>
    internal static void ClearCache()
    {
        lock (CacheSync)
        {
            Cache.Clear();
            InsertionOrder.Clear();
        }
    }

    /// <summary>返回当前缓存条目数，仅供诊断与测试。</summary>
    internal static int CachedPatternCount
    {
        get
        {
            lock (CacheSync)
                return Cache.Count;
        }
    }

    private static Regex GetRegex(string pattern, string? flags)
    {
        ValidatePatternLength(pattern);
        RegexOptions options = ParseOptions(flags);
        var key = new RegexCacheKey(pattern, options);
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        // 动态 SQL 模式不能使用 GeneratedRegex；仅缓存有限数量的 Regex 实例，避免无界动态代码/对象增长。
        var created = new Regex(pattern, options, MatchTimeout);
        lock (CacheSync)
        {
            if (Cache.TryGetValue(key, out var raced))
                return raced;

            Cache[key] = created;
            InsertionOrder.Enqueue(key);
            while (Cache.Count > CacheCapacity && InsertionOrder.TryDequeue(out var oldest))
            {
                Cache.TryRemove(oldest, out _);
            }

            return created;
        }
    }

    private static RegexOptions ParseOptions(string? flags)
    {
        RegexOptions options = RegexOptions.CultureInvariant;
        if (string.IsNullOrEmpty(flags))
            return options;
        if (flags.Length > 16)
            throw new InvalidOperationException("regexp_like flags 长度不能超过 16 个字符。");

        foreach (char flag in flags)
        {
            options = char.ToLowerInvariant(flag) switch
            {
                'i' => options | RegexOptions.IgnoreCase,
                'c' => options & ~RegexOptions.IgnoreCase,
                'm' => options | RegexOptions.Multiline,
                's' => options | RegexOptions.Singleline,
                'x' => options | RegexOptions.IgnorePatternWhitespace,
                _ => throw new InvalidOperationException(
                    $"regexp_like 不支持 flags 字符 '{flag}'；允许值为 i/c/m/s/x。"),
            };
        }

        return options;
    }

    private static void ValidatePatternLength(string pattern)
    {
        if (pattern.Length > MaxPatternLength)
        {
            throw new InvalidOperationException(
                $"正则模式长度 {pattern.Length} 超过上限 {MaxPatternLength} 个字符。");
        }
    }

    private static void ValidateInput(string input)
    {
        if (input.Length > MaxInputLength)
        {
            throw new InvalidOperationException(
                $"正则输入长度 {input.Length} 超过上限 {MaxInputLength} 个字符。");
        }
    }

    private readonly record struct RegexCacheKey(string Pattern, RegexOptions Options);
}
