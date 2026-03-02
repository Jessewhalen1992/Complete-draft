using System.Collections.Generic;

namespace AtsBackgroundBuilder.Core
{
    internal sealed class AtsBuildOptionSelection
    {
        internal bool IncludeDispositionLinework { get; init; }
        internal bool IncludeDispositionLabels { get; init; }
        internal bool AllowMultiQuarterDispositions { get; init; }
        internal bool IncludeAtsFabric { get; init; }
        internal bool DrawLsdSubdivisionLines { get; init; }
        internal bool IncludeP3Shapefiles { get; init; }
        internal bool IncludeCompassMapping { get; init; }
        internal bool IncludeCrownReservations { get; init; }
        internal bool AutoCheckUpdateShapefilesAlways { get; init; }
        internal bool CheckPlsr { get; init; }
        internal bool IncludeSurfaceImpact { get; init; }
        internal bool IncludeQuarterSectionLabels { get; init; }
    }

    internal static class AtsBuildInputFactory
    {
        internal static AtsBuildInput Create(
            string currentClient,
            int zone,
            double textHeight,
            int maxOverlapAttempts,
            IEnumerable<SectionRequest> sectionRequests,
            AtsBuildOptionSelection options)
        {
            options ??= new AtsBuildOptionSelection();

            var input = new AtsBuildInput
            {
                CurrentClient = currentClient ?? string.Empty,
                Zone = zone,
                TextHeight = textHeight,
                MaxOverlapAttempts = maxOverlapAttempts,
                IncludeDispositionLinework = options.IncludeDispositionLinework,
                IncludeDispositionLabels = options.IncludeDispositionLabels,
                AllowMultiQuarterDispositions = options.AllowMultiQuarterDispositions,
                IncludeAtsFabric = options.IncludeAtsFabric,
                DrawLsdSubdivisionLines = options.DrawLsdSubdivisionLines,
                IncludeP3Shapefiles = options.IncludeP3Shapefiles,
                IncludeCompassMapping = options.IncludeCompassMapping,
                IncludeCrownReservations = options.IncludeCrownReservations,
                AutoCheckUpdateShapefilesAlways = options.AutoCheckUpdateShapefilesAlways,
                CheckPlsr = options.CheckPlsr,
                IncludeSurfaceImpact = options.IncludeSurfaceImpact,
                IncludeQuarterSectionLabels = options.IncludeQuarterSectionLabels,
                UseAlignedDimensions = true,
            };

            if (sectionRequests != null)
            {
                input.SectionRequests.AddRange(sectionRequests);
            }

            return input;
        }
    }
}
