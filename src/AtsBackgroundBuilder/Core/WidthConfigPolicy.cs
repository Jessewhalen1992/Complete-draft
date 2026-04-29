using System;
using System.Collections.Generic;
using System.Linq;

namespace AtsBackgroundBuilder.Core
{
    internal static class WidthConfigPolicy
    {
        private const double LegacySecWidth = 20.11;
        private const double CanonicalSecWidth = 20.12;
        private const double LegacyUsecWidth = 30.17;
        private const double CanonicalUsecWidth = 30.18;
        private const double MatchTolerance = 0.0001;

        public static double[] NormalizeAcceptableRowWidths(IEnumerable<double>? widths)
        {
            if (widths == null)
            {
                return Array.Empty<double>();
            }

            return widths
                .Where(w => !double.IsNaN(w) && !double.IsInfinity(w) && w > 0)
                .Select(NormalizeAcceptedRowWidth)
                .Select(w => Math.Round(w, 2))
                .Distinct()
                .OrderBy(w => w)
                .ToArray();
        }

        private static double NormalizeAcceptedRowWidth(double width)
        {
            if (Math.Abs(width - LegacySecWidth) <= MatchTolerance)
            {
                return CanonicalSecWidth;
            }

            if (Math.Abs(width - LegacyUsecWidth) <= MatchTolerance)
            {
                return CanonicalUsecWidth;
            }

            return width;
        }
    }
}
