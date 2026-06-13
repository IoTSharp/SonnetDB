namespace SonnetDB.Sql.Execution;

internal static class LikePatternMatcher
{
    public static bool IsMatch(object? value, object? pattern)
        => value is string text
            && pattern is string likePattern
            && IsMatch(text, likePattern);

    private static bool IsMatch(string text, string pattern)
    {
        var textIndex = 0;
        var patternIndex = 0;
        var starPatternIndex = -1;
        var starTextIndex = -1;

        while (textIndex < text.Length)
        {
            if (patternIndex < pattern.Length)
            {
                var patternChar = pattern[patternIndex];
                if (patternChar == '\\' && patternIndex + 1 < pattern.Length)
                {
                    patternIndex++;
                    patternChar = pattern[patternIndex];
                    if (patternChar == text[textIndex])
                    {
                        patternIndex++;
                        textIndex++;
                        continue;
                    }
                }
                else if (patternChar == '_' || patternChar == text[textIndex])
                {
                    patternIndex++;
                    textIndex++;
                    continue;
                }
                else if (patternChar == '%')
                {
                    starPatternIndex = patternIndex++;
                    starTextIndex = textIndex;
                    continue;
                }
            }

            if (starPatternIndex < 0)
                return false;

            patternIndex = starPatternIndex + 1;
            textIndex = ++starTextIndex;
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '%')
            patternIndex++;

        return patternIndex == pattern.Length;
    }
}
