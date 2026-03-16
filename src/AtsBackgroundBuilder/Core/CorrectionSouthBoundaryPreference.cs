using System;

namespace AtsBackgroundBuilder
{
    internal static class CorrectionSouthBoundaryPreference
    {
        public static bool IsCloserToInsetThanHardBoundary(
            double outwardDistanceFromSection,
            double correctionInsetMeters,
            double hardBoundaryMeters)
        {
            var insetDelta = Math.Abs(outwardDistanceFromSection - correctionInsetMeters);
            var hardDelta = Math.Abs(outwardDistanceFromSection - hardBoundaryMeters);
            return insetDelta <= hardDelta;
        }

        public static bool IsUnlinkedDividerGapAcceptable(
            double dividerGapMeters,
            double maxAllowedGapMeters)
        {
            return dividerGapMeters <= maxAllowedGapMeters + 1e-6;
        }

        public static bool IsPlausibleInsetOffset(
            double outwardDistanceFromSection,
            double correctionInsetMeters)
        {
            if (correctionInsetMeters <= 0.0)
            {
                return false;
            }

            return outwardDistanceFromSection + 1e-6 >= (correctionInsetMeters * 0.5);
        }

        public static bool IsHardBoundaryCoverageAcceptable(
            double projectedOverlapMeters,
            double frameSpanMeters)
        {
            if (projectedOverlapMeters <= 0.0 || frameSpanMeters <= 0.0)
            {
                return false;
            }

            return projectedOverlapMeters + 1e-6 >= (frameSpanMeters * 0.70);
        }

        public static bool IsCompanionCoverageAcceptable(
            double projectedOverlapMeters,
            double sourceLengthMeters)
        {
            if (projectedOverlapMeters <= 0.0 || sourceLengthMeters <= 0.0)
            {
                return false;
            }

            var minCoverage = sourceLengthMeters <= 120.0
                ? Math.Max(20.0, sourceLengthMeters * 0.45)
                : sourceLengthMeters * 0.60;
            return projectedOverlapMeters + 1e-6 >= minCoverage;
        }

        public static bool IsSameSideInsetCompanionCandidate(
            double outerSignedOffset,
            double candidateSignedOffset,
            double expectedInsetMeters,
            double toleranceMeters)
        {
            if (expectedInsetMeters <= 0.0 || toleranceMeters < 0.0)
            {
                return false;
            }

            var outerDistance = Math.Abs(outerSignedOffset);
            var candidateDistance = Math.Abs(candidateSignedOffset);
            if (outerDistance <= 1e-6 || candidateDistance <= 1e-6)
            {
                return false;
            }

            if (Math.Sign(outerSignedOffset) != Math.Sign(candidateSignedOffset))
            {
                return false;
            }

            if (candidateDistance + 1e-6 >= outerDistance)
            {
                return false;
            }

            return Math.Abs((outerDistance - candidateDistance) - expectedInsetMeters) <= toleranceMeters + 1e-6;
        }
    }
}
