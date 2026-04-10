using System;
using System.Text.RegularExpressions;

namespace Compass.Services;

public static class DrillParsers
{
    private static readonly Regex NameRegex = new(@"(\d{1,2}-\d{1,2}-\d{1,3}-\d{1,2})", RegexOptions.Compiled);
    private static readonly Regex OffsetRegex = new(@"^\s*([+-]?\d+(\.\d+)?)\s*([NnSsEeWw])\s*$", RegexOptions.Compiled);

    public static string NormalizeDrillName(string name)
    {
        var match = NameRegex.Match(name ?? string.Empty);
        if (!match.Success)
        {
            return string.Empty;
        }

        var parts = match.Value.Split('-');
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = parts[i].TrimStart('0');
        }

        return string.Join("-", parts);
    }

    public static string NormalizeTableValue(string value)
    {
        value = Regex.Replace(value ?? string.Empty, @"\{.*?;", string.Empty);
        value = value.Replace("}", string.Empty);
        value = value.ToUpperInvariant().Replace(" ", string.Empty);
        value = Regex.Replace(value, "W\\d+", string.Empty, RegexOptions.IgnoreCase);

        var parts = value.Split('-');
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = parts[i].TrimStart('0');
        }

        return string.Join("-", parts);
    }

    public static bool IsGridLabel(string text)
    {
        return Regex.IsMatch(text?.Trim() ?? string.Empty, "^[A-Z][1-9][0-9]{0,2}$");
    }

    public static bool TryParseOffset(string text, out double value, out char direction)
    {
        value = 0;
        direction = '\0';

        var match = OffsetRegex.Match(text ?? string.Empty);
        if (!match.Success)
        {
            return false;
        }

        value = double.Parse(match.Groups[1].Value);
        direction = char.ToUpperInvariant(match.Groups[3].Value[0]);
        return true;
    }
}
