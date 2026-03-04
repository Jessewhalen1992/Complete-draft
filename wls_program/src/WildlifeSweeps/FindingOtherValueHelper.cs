using System;

namespace WildlifeSweeps
{
    internal static class FindingOtherValueHelper
    {
        public static bool IsOtherValue(string? value, string otherValue)
        {
            return string.Equals(value, otherValue, StringComparison.OrdinalIgnoreCase);
        }

        public static string NormalizeOtherValue(string? value, string otherValue)
        {
            var trimmed = value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return string.Empty;
            }

            var normalized = trimmed.TrimEnd('.');
            return string.Equals(normalized, otherValue, StringComparison.OrdinalIgnoreCase)
                ? otherValue
                : trimmed;
        }
    }
}
