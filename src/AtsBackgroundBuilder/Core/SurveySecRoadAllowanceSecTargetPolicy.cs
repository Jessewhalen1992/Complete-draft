using System;
using System.Collections.Generic;

namespace AtsBackgroundBuilder.Core
{
    internal static class SurveySecRoadAllowanceSecTargetPolicy
    {
        public static bool ShouldUseSecTarget(
            IReadOnlyList<string>? preferredKinds,
            bool hasProjectedZeroCandidate,
            bool hasProjectedTwentyCandidate,
            bool hasProjectedCorrectionZeroCandidate)
        {
            if (preferredKinds == null || preferredKinds.Count == 0)
            {
                return false;
            }

            var primaryKind = preferredKinds[0];
            var isRoadAllowanceKind =
                string.Equals(primaryKind, "ZERO", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(primaryKind, "TWENTY", StringComparison.OrdinalIgnoreCase);
            if (!isRoadAllowanceKind)
            {
                return false;
            }

            var hasSecFallback = false;
            for (var i = 0; i < preferredKinds.Count; i++)
            {
                if (string.Equals(preferredKinds[i], "SEC", StringComparison.OrdinalIgnoreCase))
                {
                    hasSecFallback = true;
                    break;
                }
            }

            if (!hasSecFallback)
            {
                return false;
            }

            return !hasProjectedZeroCandidate &&
                   !hasProjectedTwentyCandidate &&
                   !hasProjectedCorrectionZeroCandidate;
        }
    }
}
