namespace AtsBackgroundBuilder
{
    /// <summary>
    /// Canonical section-building rule constants.
    /// Keep values centralized here so geometry/layer logic reads from one source of truth.
    /// </summary>
    internal static class SectionRules
    {
        // Rule 3 / Rule 4 base widths.
        internal const double RoadAllowanceUsecWidthMeters = 30.16;
        internal const double RoadAllowanceSecWidthMeters = 20.11;

        // Rule 5/6 correction-line behavior.
        internal const double SurveyedUnsurveyedThresholdMeters = 25.0;
        internal const double CorrectionLineInsetMeters = 5.03;
        internal const double CorrectionLinePairGapMeters = CorrectionLineInsetMeters * 2.0;

        // Shared tolerances used by layer classification and endpoint snapping.
        internal const double RoadAllowanceWidthToleranceMeters = 0.10;
        internal const double RoadAllowanceGapOffsetToleranceMeters = 0.35;

        // Minimum LSD segment length that can be adjusted during cleanup.
        internal const double MinAdjustableLsdLineLengthMeters = 20.0;
    }
}
