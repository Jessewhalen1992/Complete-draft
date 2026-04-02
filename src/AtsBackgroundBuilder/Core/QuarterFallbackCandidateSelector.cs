using System;
using System.Collections.Generic;

namespace AtsBackgroundBuilder.Core
{
    internal static class QuarterFallbackCandidateSelector
    {
        public static List<PlsrQuarterMatchPoint> Select(
            IEnumerable<PlsrQuarterMatchPoint>? seedCandidates,
            Func<PlsrQuarterMatchPoint, bool> isUsable,
            Func<PlsrQuarterMatchPoint, bool>? isSecondaryUsable = null)
        {
            var ordered = new List<PlsrQuarterMatchPoint>();
            if (seedCandidates == null || isUsable == null)
            {
                return ordered;
            }

            foreach (var candidate in seedCandidates)
            {
                if (!isUsable(candidate) &&
                    (isSecondaryUsable == null || !isSecondaryUsable(candidate)))
                {
                    continue;
                }

                if (Contains(ordered, candidate))
                {
                    continue;
                }

                ordered.Add(candidate);
            }

            return ordered;
        }

        private static bool Contains(IEnumerable<PlsrQuarterMatchPoint> candidates, PlsrQuarterMatchPoint candidate)
        {
            const double epsilon = 1e-6;

            foreach (var existing in candidates)
            {
                if (Math.Abs(existing.X - candidate.X) <= epsilon &&
                    Math.Abs(existing.Y - candidate.Y) <= epsilon)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
