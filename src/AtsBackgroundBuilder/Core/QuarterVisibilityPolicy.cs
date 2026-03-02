namespace AtsBackgroundBuilder.Core
{
    internal readonly struct QuarterVisibilityPolicy
    {
        private QuarterVisibilityPolicy(
            bool showQuarterDefinitionView,
            bool keepQuarterHelperLinework)
        {
            ShowQuarterDefinitionView = showQuarterDefinitionView;
            KeepQuarterHelperLinework = keepQuarterHelperLinework;
        }

        // Controls visible L-QUATER quarter view output.
        public bool ShowQuarterDefinitionView { get; }

        // Controls whether L-QSEC helper linework remains after cleanup.
        public bool KeepQuarterHelperLinework { get; }

        public static QuarterVisibilityPolicy Create(
            bool includeAtsFabric,
            bool allowMultiQuarterDispositions,
            bool enableQuarterViewByEnvironment)
        {
            var showQuarterDefinitionView =
                allowMultiQuarterDispositions || enableQuarterViewByEnvironment;
            var keepQuarterHelperLinework =
                includeAtsFabric || showQuarterDefinitionView;

            return new QuarterVisibilityPolicy(
                showQuarterDefinitionView,
                keepQuarterHelperLinework);
        }
    }
}
