namespace AtsBackgroundBuilder.Core
{
    internal readonly struct QuarterBoundaryOwnershipPolicy
    {
        private QuarterBoundaryOwnershipPolicy(
            double westExpectedOffset,
            double southFallbackOffset,
            bool allowWestInsetDowngrade,
            string westFallbackSource,
            string southFallbackSource)
        {
            WestExpectedOffset = westExpectedOffset;
            SouthFallbackOffset = southFallbackOffset;
            AllowWestInsetDowngrade = allowWestInsetDowngrade;
            WestFallbackSource = westFallbackSource;
            SouthFallbackSource = southFallbackSource;
        }

        public double WestExpectedOffset { get; }
        public double SouthFallbackOffset { get; }
        public bool AllowWestInsetDowngrade { get; }
        public string WestFallbackSource { get; }
        public string SouthFallbackSource { get; }

        public static QuarterBoundaryOwnershipPolicy Create(
            bool isBlindSouthBoundarySection,
            bool hasWestUsecZeroOwnershipCandidate,
            bool hasSouthUsecZeroOwnershipCandidate,
            double roadAllowanceSecWidthMeters,
            double roadAllowanceUsecWidthMeters)
        {
            var westExpectedOffset = hasWestUsecZeroOwnershipCandidate
                ? roadAllowanceUsecWidthMeters
                : roadAllowanceSecWidthMeters;
            var southFallbackOffset = isBlindSouthBoundarySection
                ? 0.0
                : (hasSouthUsecZeroOwnershipCandidate
                    ? roadAllowanceUsecWidthMeters
                    : roadAllowanceSecWidthMeters);
            var allowWestInsetDowngrade = !hasWestUsecZeroOwnershipCandidate;

            var westFallbackSource = westExpectedOffset >= (roadAllowanceUsecWidthMeters - 0.5)
                ? "fallback-30.16"
                : "fallback-20.12";
            var southFallbackSource = southFallbackOffset <= 0.0
                ? "fallback-blind"
                : (southFallbackOffset >= (roadAllowanceUsecWidthMeters - 0.5)
                    ? "fallback-30.16"
                    : "fallback-20.12");

            return new QuarterBoundaryOwnershipPolicy(
                westExpectedOffset,
                southFallbackOffset,
                allowWestInsetDowngrade,
                westFallbackSource,
                southFallbackSource);
        }
    }
}
