using System;

namespace AtsBackgroundBuilder
{
    internal static class CorrectionSouthBoundaryPreference
    {
        private const double ComparisonTolerance = 1e-6;
        private const double HardBoundaryCoverageFraction = 0.70;
        private const double ShortCompanionThresholdMeters = 120.0;
        private const double ShortCompanionCoverageFloorMeters = 20.0;
        private const double ShortCompanionCoverageFraction = 0.45;
        private const double LongCompanionCoverageFraction = 0.60;

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
            return IsAtOrBelow(dividerGapMeters, maxAllowedGapMeters);
        }

        public static bool IsPlausibleInsetOffset(
            double outwardDistanceFromSection,
            double correctionInsetMeters)
        {
            if (correctionInsetMeters <= 0.0)
            {
                return false;
            }

            return IsAtOrAbove(outwardDistanceFromSection, correctionInsetMeters * 0.5);
        }

        public static bool IsHardBoundaryCoverageAcceptable(
            double projectedOverlapMeters,
            double frameSpanMeters)
        {
            if (projectedOverlapMeters <= 0.0 || frameSpanMeters <= 0.0)
            {
                return false;
            }

            return IsAtOrAbove(projectedOverlapMeters, frameSpanMeters * HardBoundaryCoverageFraction);
        }

        public static bool IsCompanionCoverageAcceptable(
            double projectedOverlapMeters,
            double sourceLengthMeters)
        {
            if (projectedOverlapMeters <= 0.0 || sourceLengthMeters <= 0.0)
            {
                return false;
            }

            return IsAtOrAbove(projectedOverlapMeters, ResolveMinimumCompanionCoverage(sourceLengthMeters));
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
            if (outerDistance <= ComparisonTolerance || candidateDistance <= ComparisonTolerance)
            {
                return false;
            }

            if (Math.Sign(outerSignedOffset) != Math.Sign(candidateSignedOffset))
            {
                return false;
            }

            if (IsAtOrAbove(candidateDistance, outerDistance))
            {
                return false;
            }

            return Math.Abs((outerDistance - candidateDistance) - expectedInsetMeters) <= toleranceMeters + ComparisonTolerance;
        }

        private static double ResolveMinimumCompanionCoverage(double sourceLengthMeters)
        {
            return sourceLengthMeters <= ShortCompanionThresholdMeters
                ? Math.Max(ShortCompanionCoverageFloorMeters, sourceLengthMeters * ShortCompanionCoverageFraction)
                : sourceLengthMeters * LongCompanionCoverageFraction;
        }

        private static bool IsAtOrAbove(double value, double threshold)
        {
            return value + ComparisonTolerance >= threshold;
        }

        private static bool IsAtOrBelow(double value, double threshold)
        {
            return value <= threshold + ComparisonTolerance;
        }
    }
}
