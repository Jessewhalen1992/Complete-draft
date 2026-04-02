using System;

namespace AtsBackgroundBuilder
{
    internal static class CorrectionZeroTargetPreference
    {
        private const double ComparisonTolerance = 1e-6;
        private const string CorrectionZeroKind = "CORRZERO";

        public static bool IsBetterCandidate(
            double moveDistance,
            double boundaryGap,
            double bestMoveDistance,
            double bestBoundaryGap)
        {
            return IsBetterByPrimaryThenSecondary(
                moveDistance,
                bestMoveDistance,
                boundaryGap,
                bestBoundaryGap);
        }

        public static bool IsBetterInsetCandidate(
            double targetOffsetError,
            double boundaryGap,
            double moveDistance,
            double bestTargetOffsetError,
            double bestBoundaryGap,
            double bestMoveDistance)
        {
            return IsBetterByPrimaryThenSecondaryThenTertiary(
                targetOffsetError,
                bestTargetOffsetError,
                boundaryGap,
                bestBoundaryGap,
                moveDistance,
                bestMoveDistance);
        }

        public static bool ShouldPreserveExistingPrimaryBoundary(string kind)
        {
            return string.Equals(kind, CorrectionZeroKind, StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsBetterLiveSnapCandidate(
            double targetDelta,
            double moveDistance,
            double bestTargetDelta,
            double bestMoveDistance)
        {
            return IsBetterByPrimaryThenSecondary(
                targetDelta,
                bestTargetDelta,
                moveDistance,
                bestMoveDistance);
        }

        public static bool IsBetterEndpointAdjustmentCandidate(
            bool hasExistingCandidate,
            double candidateDistanceFromOriginal,
            double bestDistanceFromOriginal)
        {
            return !hasExistingCandidate ||
                   IsStrictlyBetter(candidateDistanceFromOriginal, bestDistanceFromOriginal);
        }

        private static bool IsBetterByPrimaryThenSecondary(
            double candidatePrimary,
            double incumbentPrimary,
            double candidateSecondary,
            double incumbentSecondary)
        {
            return IsStrictlyBetter(candidatePrimary, incumbentPrimary) ||
                   (AreEquivalent(candidatePrimary, incumbentPrimary) &&
                    IsStrictlyBetter(candidateSecondary, incumbentSecondary));
        }

        private static bool IsBetterByPrimaryThenSecondaryThenTertiary(
            double candidatePrimary,
            double incumbentPrimary,
            double candidateSecondary,
            double incumbentSecondary,
            double candidateTertiary,
            double incumbentTertiary)
        {
            return IsStrictlyBetter(candidatePrimary, incumbentPrimary) ||
                   (AreEquivalent(candidatePrimary, incumbentPrimary) &&
                    IsBetterByPrimaryThenSecondary(
                        candidateSecondary,
                        incumbentSecondary,
                        candidateTertiary,
                        incumbentTertiary));
        }

        private static bool IsStrictlyBetter(double candidate, double incumbent)
        {
            return candidate < incumbent - ComparisonTolerance;
        }

        private static bool AreEquivalent(double left, double right)
        {
            return Math.Abs(left - right) <= ComparisonTolerance;
        }
    }
}
