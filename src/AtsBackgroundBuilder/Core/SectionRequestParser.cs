using System;
using System.Collections.Generic;
using System.Globalization;

namespace AtsBackgroundBuilder.Core
{
    internal enum SectionRequestParseFailure
    {
        None,
        MissingMeridianRangeTownship,
        MissingSection,
        InvalidQuarter
    }

    internal readonly struct SectionRequestRowInput
    {
        internal SectionRequestRowInput(string meridian, string range, string township, string section, string quarter)
        {
            Meridian = meridian ?? string.Empty;
            Range = range ?? string.Empty;
            Township = township ?? string.Empty;
            Section = section ?? string.Empty;
            Quarter = quarter ?? string.Empty;
        }

        internal string Meridian { get; }
        internal string Range { get; }
        internal string Township { get; }
        internal string Section { get; }
        internal string Quarter { get; }
    }

    internal sealed class SectionRequestParseResult
    {
        private SectionRequestParseResult(
            List<SectionRequest> requests,
            SectionRequestParseFailure failure,
            string invalidQuarterValue)
        {
            Requests = requests ?? new List<SectionRequest>();
            Failure = failure;
            InvalidQuarterValue = invalidQuarterValue ?? string.Empty;
        }

        internal List<SectionRequest> Requests { get; }
        internal SectionRequestParseFailure Failure { get; }
        internal string InvalidQuarterValue { get; }
        internal bool IsSuccess => Failure == SectionRequestParseFailure.None;

        internal static SectionRequestParseResult Success(List<SectionRequest> requests)
        {
            return new SectionRequestParseResult(
                requests ?? new List<SectionRequest>(),
                SectionRequestParseFailure.None,
                string.Empty);
        }

        internal static SectionRequestParseResult FailureResult(
            SectionRequestParseFailure failure,
            string invalidQuarterValue = "")
        {
            return new SectionRequestParseResult(
                new List<SectionRequest>(),
                failure,
                invalidQuarterValue ?? string.Empty);
        }
    }

    internal static class SectionRequestParser
    {
        internal static SectionRequestParseResult Parse(int zone, IEnumerable<SectionRequestRowInput> rows)
        {
            var requests = new List<SectionRequest>();
            var lastMeridian = string.Empty;
            var lastRange = string.Empty;
            var lastTownship = string.Empty;
            var lastSection = string.Empty;

            if (rows == null)
            {
                return SectionRequestParseResult.Success(requests);
            }

            foreach (var rawRow in rows)
            {
                var m = NormalizeCell(rawRow.Meridian);
                var rge = NormalizeCell(rawRow.Range);
                var twp = NormalizeCell(rawRow.Township);
                var sec = NormalizeCell(rawRow.Section);
                var q = NormalizeCell(rawRow.Quarter);

                var anyFilled =
                    !string.IsNullOrWhiteSpace(m) ||
                    !string.IsNullOrWhiteSpace(rge) ||
                    !string.IsNullOrWhiteSpace(twp) ||
                    !string.IsNullOrWhiteSpace(sec) ||
                    !string.IsNullOrWhiteSpace(q);
                if (!anyFilled)
                {
                    continue;
                }

                var hasExplicitMeridian = !string.IsNullOrWhiteSpace(m);
                var hasExplicitRange = !string.IsNullOrWhiteSpace(rge);
                var hasExplicitTownship = !string.IsNullOrWhiteSpace(twp);
                var hasExplicitSection = !string.IsNullOrWhiteSpace(sec);

                if (string.IsNullOrWhiteSpace(m))
                {
                    m = lastMeridian;
                }

                if (string.IsNullOrWhiteSpace(rge))
                {
                    rge = lastRange;
                }

                if (string.IsNullOrWhiteSpace(twp))
                {
                    twp = lastTownship;
                }

                var expandAllSections =
                    !hasExplicitSection &&
                    (hasExplicitMeridian || hasExplicitRange || hasExplicitTownship);
                if (!expandAllSections && string.IsNullOrWhiteSpace(sec))
                {
                    sec = lastSection;
                }

                if (string.IsNullOrWhiteSpace(q))
                {
                    q = "ALL";
                }

                if (string.IsNullOrWhiteSpace(m) || string.IsNullOrWhiteSpace(rge) || string.IsNullOrWhiteSpace(twp))
                {
                    return SectionRequestParseResult.FailureResult(SectionRequestParseFailure.MissingMeridianRangeTownship);
                }

                if (!expandAllSections && string.IsNullOrWhiteSpace(sec))
                {
                    return SectionRequestParseResult.FailureResult(SectionRequestParseFailure.MissingSection);
                }

                lastMeridian = m;
                lastRange = rge;
                lastTownship = twp;

                if (!TryParseQuarter(q, out var quarter))
                {
                    return SectionRequestParseResult.FailureResult(SectionRequestParseFailure.InvalidQuarter, q);
                }

                if (expandAllSections)
                {
                    for (var sectionNumber = 1; sectionNumber <= 36; sectionNumber++)
                    {
                        var key = new SectionKey(zone, sectionNumber.ToString(CultureInfo.InvariantCulture), twp, rge, m);
                        requests.Add(new SectionRequest(quarter, key, "AUTO"));
                    }

                    lastSection = string.Empty;
                }
                else
                {
                    lastSection = sec;
                    var key = new SectionKey(zone, sec, twp, rge, m);
                    requests.Add(new SectionRequest(quarter, key, "AUTO"));
                }
            }

            return SectionRequestParseResult.Success(requests);
        }

        private static string NormalizeCell(string? value)
        {
            return value?.Trim() ?? string.Empty;
        }

        private static bool TryParseQuarter(string raw, out QuarterSelection quarter)
        {
            quarter = QuarterSelection.None;
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var s = raw.Trim().ToUpperInvariant();
            switch (s)
            {
                case "NW":
                    quarter = QuarterSelection.NorthWest;
                    return true;
                case "NE":
                    quarter = QuarterSelection.NorthEast;
                    return true;
                case "SW":
                    quarter = QuarterSelection.SouthWest;
                    return true;
                case "SE":
                    quarter = QuarterSelection.SouthEast;
                    return true;
                case "N":
                    quarter = QuarterSelection.NorthHalf;
                    return true;
                case "S":
                    quarter = QuarterSelection.SouthHalf;
                    return true;
                case "E":
                    quarter = QuarterSelection.EastHalf;
                    return true;
                case "W":
                    quarter = QuarterSelection.WestHalf;
                    return true;
                case "ALL":
                case "A":
                    quarter = QuarterSelection.All;
                    return true;
                default:
                    return false;
            }
        }
    }
}
