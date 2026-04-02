namespace AtsBackgroundBuilder.Core
{
    internal static class CorrectionOuterConsistencyPromotionPolicy
    {
        public static bool ShouldPromoteSegment(
            bool startTouchesCorrectionChain,
            bool endTouchesCorrectionChain,
            bool hasParallelInsetCompanion)
        {
            return !hasParallelInsetCompanion ||
                   (startTouchesCorrectionChain && endTouchesCorrectionChain);
        }
    }
}
