using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AtsBackgroundBuilder.Core
{
    internal static class PlsrTextNormalizationService
    {
        public static string ExtractDispositionNumber(
            IReadOnlyList<string>? lines,
            string rawContents,
            IEnumerable<string> dispositionPrefixes)
        {
            if (lines != null)
            {
                for (var i = lines.Count - 1; i >= 0; i--)
                {
                    if (TryParseDispositionNumberFromText(lines[i], dispositionPrefixes, out var parsed))
                    {
                        return parsed;
                    }
                }
            }

            if (TryParseDispositionNumberFromText(rawContents, dispositionPrefixes, out var fallback))
            {
                return fallback;
            }

            return string.Empty;
        }

        public static bool TryParseDispositionNumberFromText(
            string text,
            IEnumerable<string> dispositionPrefixes,
            out string dispNum)
        {
            dispNum = string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var upper = StripMTextControlCodes(text).ToUpperInvariant();
            foreach (var prefix in dispositionPrefixes ?? Array.Empty<string>())
            {
                var spaced = Regex.Match(upper, $@"\b{Regex.Escape(prefix)}\s*[-]?\s*(\d{{2,}})\b", RegexOptions.IgnoreCase);
                if (spaced.Success)
                {
                    dispNum = prefix + spaced.Groups[1].Value;
                    return true;
                }
            }

            var normalized = Regex.Replace(upper, "[^A-Z0-9]+", string.Empty);
            if (normalized.Length < 4)
            {
                return false;
            }

            foreach (var prefix in dispositionPrefixes ?? Array.Empty<string>())
            {
                if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (normalized.Length <= prefix.Length || !char.IsDigit(normalized[prefix.Length]))
                {
                    continue;
                }

                var end = prefix.Length;
                while (end < normalized.Length && char.IsDigit(normalized[end]))
                {
                    end++;
                }

                if (end > prefix.Length)
                {
                    dispNum = prefix + normalized.Substring(prefix.Length, end - prefix.Length);
                    return true;
                }
            }

            return false;
        }

        public static List<string> SplitMTextLines(string contents)
        {
            if (string.IsNullOrWhiteSpace(contents))
            {
                return new List<string>();
            }

            var normalized = contents
                .Replace("\\P", "\n")
                .Replace("\\X", "\n")
                .Replace("\r", "\n");
            var raw = normalized.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var lines = new List<string>();
            foreach (var line in raw)
            {
                var cleaned = StripMTextControlCodes(line).Trim();
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    lines.Add(cleaned);
                }
            }

            return lines;
        }

        public static string StripMTextControlCodes(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var cleaned = text
                .Replace("{", string.Empty)
                .Replace("}", string.Empty)
                .Replace("\\~", " ")
                .Replace("\\P", " ")
                .Replace("\\X", " ");

            cleaned = Regex.Replace(cleaned, @"\\[A-Za-z][^;\\]*;", string.Empty, RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"\\S[^;]*;", " ", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"\\[LOKlok]", string.Empty);
            cleaned = Regex.Replace(cleaned, @"\\[A-Za-z]", string.Empty);

            return cleaned;
        }

        public static string FlattenMTextForDisplay(string contents)
        {
            if (string.IsNullOrWhiteSpace(contents))
            {
                return string.Empty;
            }

            var lines = SplitMTextLines(contents);
            if (lines.Count == 0)
            {
                return StripMTextControlCodes(contents).Trim();
            }

            return string.Join(" | ", lines);
        }

        public static bool HasExpiredMarker(string? contents)
        {
            if (string.IsNullOrWhiteSpace(contents))
            {
                return false;
            }

            var flattened = FlattenMTextForDisplay(contents);
            return Regex.IsMatch(flattened, @"\bEXPIRED\b", RegexOptions.IgnoreCase);
        }

        public static string NormalizeDispNum(string dispNum)
        {
            if (string.IsNullOrWhiteSpace(dispNum))
            {
                return string.Empty;
            }

            var compact = Regex.Replace(dispNum.ToUpperInvariant(), "\\s+", string.Empty);
            compact = Regex.Replace(compact, "[^A-Z0-9]", string.Empty);
            if (string.IsNullOrWhiteSpace(compact))
            {
                return string.Empty;
            }

            var prefixMatch = Regex.Match(compact, "^[A-Z]{3}");
            if (!prefixMatch.Success)
            {
                return compact;
            }

            var prefix = prefixMatch.Value;
            var suffix = compact.Substring(prefix.Length);
            if (string.IsNullOrWhiteSpace(suffix))
            {
                return prefix;
            }

            var digits = new string(suffix.Where(char.IsDigit).ToArray());
            if (digits.Length > 0)
            {
                var trimmedDigits = digits.TrimStart('0');
                if (trimmedDigits.Length == 0)
                {
                    trimmedDigits = "0";
                }

                return prefix + trimmedDigits;
            }

            return prefix + suffix;
        }

        public static string GetDispositionPrefix(string dispNum)
        {
            if (string.IsNullOrWhiteSpace(dispNum))
            {
                return string.Empty;
            }

            var match = Regex.Match(dispNum, "^[A-Z]{3}");
            return match.Success ? match.Value.ToUpperInvariant() : string.Empty;
        }

        public static string NormalizeOwner(string owner)
        {
            if (string.IsNullOrWhiteSpace(owner))
            {
                return string.Empty;
            }

            var upper = StripMTextControlCodes(owner).ToUpperInvariant();
            return Regex.Replace(upper, "[^A-Z0-9]+", string.Empty);
        }

        public static string MapClientNameForCompare(ExcelLookup lookup, string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
            {
                return string.Empty;
            }

            var entry = lookup.Lookup(rawName);
            if (entry != null && !string.IsNullOrWhiteSpace(entry.Value))
            {
                return entry.Value;
            }

            var target = NormalizeOwner(rawName);
            foreach (var value in lookup.Values)
            {
                if (NormalizeOwner(value) == target)
                {
                    return value;
                }
            }

            return rawName.Trim();
        }
    }
}
