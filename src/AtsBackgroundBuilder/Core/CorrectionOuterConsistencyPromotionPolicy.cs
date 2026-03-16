namespace AtsBackgroundBuilder.Core
{
    internal static class CorrectionOuterConsistencyPromotionPolicy
    {
        public static bool ShouldPromoteSegment(
            bool startTouchesCorrectionChain,
            bool endTouchesCorrectionChain,
            bool hasParallelInsetCompanion)
        {
            if (!hasParallelInsetCompanion)
            {
                return true;
            }

            return startTouchesCorrectionChain && endTouchesCorrectionChain;
        }
    }
}
