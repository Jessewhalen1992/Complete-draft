namespace AtsBackgroundBuilder.Core
{
    internal static class QuarterLabelFallbackPolicy
    {
        public static bool ShouldAllowQuarterOnlyLeaderPlacement(
            bool allowOutsideDisposition,
            bool isWidthAligned,
            bool addLeader,
            bool hasQuarterIntersectionPiece,
            int candidateCount)
        {
            return !allowOutsideDisposition &&
                   !isWidthAligned &&
                   addLeader &&
                   hasQuarterIntersectionPiece &&
                   candidateCount == 0;
        }

        public static int ExpandSearchPointsForQuarterOnlyLeaderPlacement(int maxPoints)
        {
            return maxPoints < 320 ? 320 : maxPoints;
        }
    }
}
