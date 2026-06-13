using System.Text.RegularExpressions;

namespace SonnetDB.Sql.Execution;

internal static partial class RegexPatternMatcher
{
    private static readonly TimeSpan _matchTimeout = TimeSpan.FromMilliseconds(250);

    public static bool IsMatch(object? value, object? pattern)
    {
        if (value is null || pattern is null)
            return false;

        return Regex.IsMatch(
            Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            Convert.ToString(pattern, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            RegexOptions.CultureInvariant,
            _matchTimeout);
    }
}
