using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.Geometry;

namespace WildlifeSweeps
{
    internal static class PhotoTextMatcher
    {
        private static readonly Regex LeadingPhotoRefRegex = new Regex(@"^\s*#?(\d+)\b", RegexOptions.Compiled);
        private static readonly Regex ParentheticalPhotoRefRegex = new Regex(@"\((\d+)\)", RegexOptions.Compiled);
        private static readonly Regex HashPhotoRefRegex = new Regex(@"#(\d+)", RegexOptions.Compiled);
        private static readonly Regex LeadingNumericPrefixRegex = new Regex(@"^\s*#?\d+\s*[_\-\.\s]+\s*", RegexOptions.Compiled);
        private static readonly Regex NumberRegex = new Regex(@"\d+", RegexOptions.Compiled);
        private static readonly Regex NonAlphaNumericRegex = new Regex(@"[^A-Za-z0-9]+", RegexOptions.Compiled);

        internal static bool IsWithinDistance(Point3d textPosition, double photoEasting, double photoNorthing, double maxDistanceMeters)
        {
            var dx = photoEasting - textPosition.X;
            var dy = photoNorthing - textPosition.Y;
            return (dx * dx) + (dy * dy) <= (maxDistanceMeters * maxDistanceMeters);
        }

        internal static bool IsTextMatchingPhotoName(string? text, string? photoFileName)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(photoFileName))
            {
                return false;
            }

            var trimmedText = text.Trim();
            var trimmedPhoto = photoFileName.Trim();
            if (string.Equals(trimmedText, trimmedPhoto, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var normalizedText = NormalizeAlphaNumeric(trimmedText);
            var normalizedPhoto = NormalizeAlphaNumeric(trimmedPhoto);
            if (!string.IsNullOrWhiteSpace(normalizedText)
                && !string.IsNullOrWhiteSpace(normalizedPhoto)
                && string.Equals(normalizedText, normalizedPhoto, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Treat leading numeric prefixes as sortable metadata, not part of the finding name
            // (e.g., "1_Beaver dam" should match "Beaver dam").
            var textWithoutPrefix = TrimLeadingNumericPrefix(trimmedText);
            var photoWithoutPrefix = TrimLeadingNumericPrefix(trimmedPhoto);
            var normalizedTextWithoutPrefix = NormalizeAlphaNumeric(textWithoutPrefix);
            var normalizedPhotoWithoutPrefix = NormalizeAlphaNumeric(photoWithoutPrefix);
            if (!string.IsNullOrWhiteSpace(normalizedTextWithoutPrefix)
                && !string.IsNullOrWhiteSpace(normalizedPhotoWithoutPrefix)
                && string.Equals(normalizedTextWithoutPrefix, normalizedPhotoWithoutPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var textRefs = ExtractTextReferenceTokens(trimmedText);
            if (textRefs.Count == 0)
            {
                return false;
            }

            var photoRefs = ExtractPhotoReferenceTokens(trimmedPhoto);
            if (photoRefs.Count == 0)
            {
                return false;
            }

            foreach (var textRef in textRefs)
            {
                if (photoRefs.Contains(textRef))
                {
                    return true;
                }
            }

            return false;
        }

        private static HashSet<string> ExtractTextReferenceTokens(string text)
        {
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var leading = LeadingPhotoRefRegex.Match(text);
            if (leading.Success)
            {
                AddToken(tokens, leading.Groups[1].Value);
            }

            foreach (Match match in ParentheticalPhotoRefRegex.Matches(text))
            {
                AddToken(tokens, match.Groups[1].Value);
            }

            foreach (Match match in HashPhotoRefRegex.Matches(text))
            {
                AddToken(tokens, match.Groups[1].Value);
            }

            // Fallback: accept numeric tokens anywhere in the text (e.g., "L.S. 12", "Nest 03").
            if (tokens.Count == 0)
            {
                foreach (Match match in NumberRegex.Matches(text))
                {
                    AddToken(tokens, match.Value);
                }
            }

            if (tokens.Count == 0)
            {
                var normalized = NormalizeAlphaNumeric(text);
                if (IsDigitsOnly(normalized))
                {
                    AddToken(tokens, normalized);
                }
            }

            return tokens;
        }

        private static HashSet<string> ExtractPhotoReferenceTokens(string photoFileName)
        {
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in NumberRegex.Matches(photoFileName))
            {
                AddToken(tokens, match.Value);
            }

            return tokens;
        }

        private static void AddToken(HashSet<string> tokens, string? rawToken)
        {
            if (string.IsNullOrWhiteSpace(rawToken))
            {
                return;
            }

            var normalized = NormalizeNumericToken(rawToken);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                tokens.Add(normalized);
            }
        }

        private static string NormalizeNumericToken(string value)
        {
            var trimmed = value.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return string.Empty;
            }

            if (long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed.ToString(CultureInfo.InvariantCulture);
            }

            var withoutLeadingZeros = trimmed.TrimStart('0');
            return withoutLeadingZeros.Length == 0 ? "0" : withoutLeadingZeros;
        }

        private static string NormalizeAlphaNumeric(string value)
        {
            return NonAlphaNumericRegex.Replace(value, string.Empty).Trim().ToLowerInvariant();
        }

        private static string TrimLeadingNumericPrefix(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return LeadingNumericPrefixRegex.Replace(value, string.Empty).Trim();
        }

        private static bool IsDigitsOnly(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            foreach (var c in value)
            {
                if (!char.IsDigit(c))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
