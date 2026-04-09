using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AtsBackgroundBuilder.Core
{
    internal static class PlsrVersionDateService
    {
        public static string ResolveStatus(
            string? dispositionVersionDateRaw,
            DateTime? xmlVersionDate,
            IEnumerable<DateTime>? xmlVersionDates)
        {
            if (!TryParseDispositionVersionDate(dispositionVersionDateRaw, out var odVersionDate))
            {
                return "N/A";
            }

            var candidateDates = new HashSet<DateTime>();
            if (xmlVersionDates != null)
            {
                foreach (var versionDate in xmlVersionDates)
                {
                    candidateDates.Add(versionDate.Date);
                }
            }

            if (xmlVersionDate.HasValue)
            {
                candidateDates.Add(xmlVersionDate.Value.Date);
            }

            if (candidateDates.Count == 0)
            {
                return "N/A";
            }

            return candidateDates.Contains(odVersionDate.Date)
                ? "MATCH"
                : "NON-MATCH";
        }

        public static string FormatExpectedForDisplay(DateTime? xmlVersionDate, IEnumerable<DateTime>? xmlVersionDates)
        {
            if (xmlVersionDates != null)
            {
                var preferredVersionDate = xmlVersionDates.DefaultIfEmpty().Max();
                if (preferredVersionDate != default)
                {
                    return FormatVersionDateForDisplay(preferredVersionDate);
                }
            }

            if (xmlVersionDate.HasValue)
            {
                return FormatVersionDateForDisplay(xmlVersionDate);
            }

            return "N/A";
        }

        public static string ResolveMismatchDetail()
        {
            return "Disposition OD VER_DATE differs from PLSR XML VersionDate.";
        }

        public static string FormatDispositionDateFieldsForDisplay(string? dispositionVersionDateRaw)
        {
            var verDateDisplay = FormatDispositionVersionDateForDisplay(dispositionVersionDateRaw);
            return string.Equals(verDateDisplay, "N/A", StringComparison.OrdinalIgnoreCase)
                ? "N/A"
                : $"VER_DATE={verDateDisplay}";
        }

        public static string FormatDispositionVersionDateForDisplay(string? rawValue)
        {
            if (TryParseDispositionVersionDate(rawValue, out var parsed))
            {
                return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            return "N/A";
        }

        public static string FormatVersionDateForDisplay(DateTime? value)
        {
            if (!value.HasValue)
            {
                return "N/A";
            }

            return value.Value.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        public static bool TryParseDispositionVersionDate(string? rawValue, out DateTime versionDate)
        {
            versionDate = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return false;
            }

            var trimmed = rawValue.Trim();
            if (DateTime.TryParseExact(trimmed, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exactDate))
            {
                versionDate = exactDate.Date;
                return true;
            }

            var digitsOnly = new string(trimmed.Where(char.IsDigit).ToArray());
            if (digitsOnly.Length >= 8 &&
                DateTime.TryParseExact(digitsOnly.Substring(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var digitDate))
            {
                versionDate = digitDate.Date;
                return true;
            }

            if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var numericDate))
            {
                var rounded = Math.Round(numericDate).ToString("0", CultureInfo.InvariantCulture);
                if (rounded.Length >= 8 &&
                    DateTime.TryParseExact(rounded.Substring(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var numericParsedDate))
                {
                    versionDate = numericParsedDate.Date;
                    return true;
                }
            }

            if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsedDate) ||
                DateTime.TryParse(trimmed, out parsedDate))
            {
                versionDate = parsedDate.Date;
                return true;
            }

            return false;
        }

        public static bool TryParseXmlVersionDate(string? rawValue, out DateTime versionDate)
        {
            versionDate = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return false;
            }

            var trimmed = rawValue.Trim();
            if (trimmed.Length >= 10 &&
                DateTime.TryParseExact(trimmed.Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var isoDate))
            {
                versionDate = isoDate.Date;
                return true;
            }

            if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsedDate) ||
                DateTime.TryParse(trimmed, out parsedDate))
            {
                versionDate = parsedDate.Date;
                return true;
            }

            return false;
        }
    }
}
