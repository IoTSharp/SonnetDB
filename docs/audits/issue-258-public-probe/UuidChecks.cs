using System.Globalization;

internal static class UuidChecks
{
    internal static void Require(bool value, string label)
    {
        if (!value) throw new InvalidOperationException("Assertion failed: " + label);
        Console.WriteLine("PASS|" + label);
    }

    internal static string GuidText(object? value)
    {
        if (value is not string text || !Guid.TryParseExact(text, "D", out var guid) || guid == Guid.Empty || guid.ToString("D") != text)
            throw new InvalidOperationException("Expected nonempty canonical D GUID; got " + Convert.ToString(value, CultureInfo.InvariantCulture));
        return text;
    }

    internal static string[] Distinct(IEnumerable<object?> values, string label)
    {
        string[] guids = values.Select(GuidText).ToArray();
        Require(guids.Length == guids.Distinct(StringComparer.Ordinal).Count(), label + "/distinct/" + guids.Length);
        Console.WriteLine("GUIDS|" + label + "|" + string.Join(';', guids));
        return guids;
    }

    internal static void Expect(Action action, string fragment, string label)
    {
        try { action(); }
        catch (Exception error) when (error.ToString().Contains(fragment, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("EXPECTED-REJECTION|" + label + "|" + error.GetType().FullName + "|" + error.Message);
            return;
        }
        throw new InvalidOperationException("Expected rejection: " + label);
    }
}
