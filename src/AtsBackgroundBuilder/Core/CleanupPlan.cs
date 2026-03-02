using System;

namespace AtsBackgroundBuilder.Core
{
    internal readonly struct CleanupPlan
    {
        private CleanupPlan(
            bool eraseQuarterDefinitionQuarterView,
            bool eraseQuarterDefinitionHelperLines,
            bool eraseQuarterBoxes,
            bool eraseQuarterHelpers,
            bool eraseSectionOutlines,
            bool eraseContextSectionPieces,
            bool eraseSectionLabels,
            bool eraseDispositionLinework)
        {
            EraseQuarterDefinitionQuarterView = eraseQuarterDefinitionQuarterView;
            EraseQuarterDefinitionHelperLines = eraseQuarterDefinitionHelperLines;
            EraseQuarterBoxes = eraseQuarterBoxes;
            EraseQuarterHelpers = eraseQuarterHelpers;
            EraseSectionOutlines = eraseSectionOutlines;
            EraseContextSectionPieces = eraseContextSectionPieces;
            EraseSectionLabels = eraseSectionLabels;
            EraseDispositionLinework = eraseDispositionLinework;
        }

        public bool EraseQuarterDefinitionQuarterView { get; }
        public bool EraseQuarterDefinitionHelperLines { get; }
        public bool EraseQuarterBoxes { get; }
        public bool EraseQuarterHelpers { get; }
        public bool EraseSectionOutlines { get; }
        public bool EraseContextSectionPieces { get; }
        public bool EraseSectionLabels { get; }
        public bool EraseDispositionLinework { get; }

        public static CleanupPlan Create(AtsBuildInput input, QuarterVisibilityPolicy quarterVisibility)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            var eraseAtsFabricGeometry = !input.IncludeAtsFabric;
            return new CleanupPlan(
                eraseQuarterDefinitionQuarterView: !quarterVisibility.ShowQuarterDefinitionView,
                eraseQuarterDefinitionHelperLines: !quarterVisibility.KeepQuarterHelperLinework,
                eraseQuarterBoxes: true,
                eraseQuarterHelpers: eraseAtsFabricGeometry,
                eraseSectionOutlines: eraseAtsFabricGeometry,
                eraseContextSectionPieces: eraseAtsFabricGeometry,
                eraseSectionLabels: eraseAtsFabricGeometry,
                eraseDispositionLinework: !input.IncludeDispositionLinework);
        }
    }
}
