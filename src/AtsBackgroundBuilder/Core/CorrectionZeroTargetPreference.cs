using System;

namespace AtsBackgroundBuilder
{
    internal static class CorrectionZeroTargetPreference
    {
        public static bool IsBetterCandidate(
            double moveDistance,
            double boundaryGap,
            double bestMoveDistance,
            double bestBoundaryGap)
        {
            return moveDistance < bestMoveDistance - 1e-6 ||
                   (Math.Abs(moveDistance - bestMoveDistance) <= 1e-6 &&
                    boundaryGap < bestBoundaryGap - 1e-6);
        }

        public static bool IsBetterInsetCandidate(
            double targetOffsetError,
            double boundaryGap,
            double moveDistance,
            double bestTargetOffsetError,
            double bestBoundaryGap,
            double bestMoveDistance)
        {
            return targetOffsetError < bestTargetOffsetError - 1e-6 ||
                   (Math.Abs(targetOffsetError - bestTargetOffsetError) <= 1e-6 &&
                    boundaryGap < bestBoundaryGap - 1e-6) ||
                   (Math.Abs(targetOffsetError - bestTargetOffsetError) <= 1e-6 &&
                    Math.Abs(boundaryGap - bestBoundaryGap) <= 1e-6 &&
                    moveDistance < bestMoveDistance - 1e-6);
        }

        public static bool ShouldPreserveExistingPrimaryBoundary(string kind)
        {
            return string.Equals(kind, "CORRZERO", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsBetterLiveSnapCandidate(
            double targetDelta,
            double moveDistance,
            double bestTargetDelta,
            double bestMoveDistance)
        {
            return targetDelta < bestTargetDelta - 1e-6 ||
                   (Math.Abs(targetDelta - bestTargetDelta) <= 1e-6 &&
                    moveDistance < bestMoveDistance - 1e-6);
        }

        public static bool IsBetterEndpointAdjustmentCandidate(
            bool hasExistingCandidate,
            double candidateDistanceFromOriginal,
            double bestDistanceFromOriginal)
        {
            return !hasExistingCandidate ||
                   candidateDistanceFromOriginal < bestDistanceFromOriginal - 1e-6;
        }
    }
}
