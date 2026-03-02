using System;

namespace AtsBackgroundBuilder.Core
{
    internal sealed class BuildExecutionPlan
    {
        private BuildExecutionPlan(
            bool showQuarterDefinitionLinework,
            bool drawQuarterViewForBuild,
            bool enableInternalQuarterDefinitionProcessing,
            bool includeAtsFabric,
            bool drawLsdSubdivisionLines,
            bool shouldAutoUpdateShapes,
            bool shouldImportP3Shapefiles,
            bool shouldImportCompassMapping,
            bool shouldImportCrownReservations,
            bool includeDispositionLinework,
            bool includeDispositionLabels,
            bool shouldGenerateDispositionLabels,
            bool shouldRunPlsrCheck,
            bool shouldRunSurfaceImpact,
            bool shouldPlaceQuarterSectionLabels)
        {
            ShowQuarterDefinitionLinework = showQuarterDefinitionLinework;
            DrawQuarterViewForBuild = drawQuarterViewForBuild;
            EnableInternalQuarterDefinitionProcessing = enableInternalQuarterDefinitionProcessing;
            IncludeAtsFabric = includeAtsFabric;
            DrawLsdSubdivisionLines = drawLsdSubdivisionLines;
            ShouldAutoUpdateShapes = shouldAutoUpdateShapes;
            ShouldImportP3Shapefiles = shouldImportP3Shapefiles;
            ShouldImportCompassMapping = shouldImportCompassMapping;
            ShouldImportCrownReservations = shouldImportCrownReservations;
            IncludeDispositionLinework = includeDispositionLinework;
            IncludeDispositionLabels = includeDispositionLabels;
            ShouldGenerateDispositionLabels = shouldGenerateDispositionLabels;
            ShouldRunPlsrCheck = shouldRunPlsrCheck;
            ShouldRunSurfaceImpact = shouldRunSurfaceImpact;
            ShouldPlaceQuarterSectionLabels = shouldPlaceQuarterSectionLabels;
        }

        public bool ShowQuarterDefinitionLinework { get; }
        public bool DrawQuarterViewForBuild { get; }
        public bool EnableInternalQuarterDefinitionProcessing { get; }
        public bool IncludeAtsFabric { get; }
        public bool DrawLsdSubdivisionLines { get; }
        public bool ShouldAutoUpdateShapes { get; }
        public bool ShouldImportP3Shapefiles { get; }
        public bool ShouldImportCompassMapping { get; }
        public bool ShouldImportCrownReservations { get; }
        public bool IncludeDispositionLinework { get; }
        public bool IncludeDispositionLabels { get; }
        public bool ShouldGenerateDispositionLabels { get; }
        public bool ShouldRunPlsrCheck { get; }
        public bool ShouldRunSurfaceImpact { get; }
        public bool ShouldPlaceQuarterSectionLabels { get; }

        public bool ShouldBuildDispositionImportScope => IncludeDispositionLinework || ShouldGenerateDispositionLabels;
        public bool ShouldLoadQuartersForLabeling => IncludeDispositionLabels || ShouldRunPlsrCheck;
        public bool ShouldPlaceLabelsBeforePlsr => IncludeDispositionLabels && !ShouldRunPlsrCheck;

        public static BuildExecutionPlan Create(AtsBuildInput input, bool enableQuarterViewByEnvironment)
        {
            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            var shouldRunPlsrCheck = input.CheckPlsr;
            var shouldGenerateDispositionLabels = input.IncludeDispositionLabels || shouldRunPlsrCheck;
            return new BuildExecutionPlan(
                showQuarterDefinitionLinework: input.IncludeAtsFabric || input.AllowMultiQuarterDispositions || enableQuarterViewByEnvironment,
                // Keep quarter derivation on for stable per-quarter build behavior even when UI visibility is off.
                drawQuarterViewForBuild: true,
                enableInternalQuarterDefinitionProcessing: true,
                includeAtsFabric: input.IncludeAtsFabric,
                drawLsdSubdivisionLines: input.DrawLsdSubdivisionLines,
                shouldAutoUpdateShapes: input.AutoCheckUpdateShapefilesAlways,
                shouldImportP3Shapefiles: input.IncludeP3Shapefiles,
                shouldImportCompassMapping: input.IncludeCompassMapping,
                shouldImportCrownReservations: input.IncludeCrownReservations,
                includeDispositionLinework: input.IncludeDispositionLinework,
                includeDispositionLabels: input.IncludeDispositionLabels,
                shouldGenerateDispositionLabels: shouldGenerateDispositionLabels,
                shouldRunPlsrCheck: shouldRunPlsrCheck,
                shouldRunSurfaceImpact: input.IncludeSurfaceImpact,
                shouldPlaceQuarterSectionLabels: input.IncludeQuarterSectionLabels);
        }

        public bool ShouldScanExistingDispositionPolylinesForPlsrFallback(int dispositionImportScopeCount)
        {
            return ShouldRunPlsrCheck &&
                   !IncludeDispositionLabels &&
                   dispositionImportScopeCount > 0;
        }

        public bool ShouldImportDispositions(int existingDispositionPolylineCount)
        {
            return IncludeDispositionLinework ||
                   IncludeDispositionLabels ||
                   ShouldRunPlsrCheck;
        }

        public bool ShouldRunPlsrMissingLabelPrecheck(bool shouldImportDispositions, int existingDispositionPolylineCount)
        {
            return shouldImportDispositions &&
                   ShouldRunPlsrCheck &&
                   !IncludeDispositionLinework &&
                   !IncludeDispositionLabels;
        }

        public bool ShouldLoadSupplementalSectionInfos(int dispositionPolylineCount)
        {
            return ShouldGenerateDispositionLabels && dispositionPolylineCount > 0;
        }
    }
}
