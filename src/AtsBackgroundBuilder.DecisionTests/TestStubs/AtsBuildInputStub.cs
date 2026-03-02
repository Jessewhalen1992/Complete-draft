namespace AtsBackgroundBuilder.Core
{
    internal sealed class AtsBuildInput
    {
        public bool CheckPlsr { get; set; }
        public bool IncludeDispositionLabels { get; set; }
        public bool IncludeDispositionLinework { get; set; }
        public bool AllowMultiQuarterDispositions { get; set; }
        public bool IncludeAtsFabric { get; set; }
        public bool DrawLsdSubdivisionLines { get; set; }
        public bool AutoCheckUpdateShapefilesAlways { get; set; }
        public bool IncludeP3Shapefiles { get; set; }
        public bool IncludeCompassMapping { get; set; }
        public bool IncludeCrownReservations { get; set; }
        public bool IncludeSurfaceImpact { get; set; }
        public bool IncludeQuarterSectionLabels { get; set; }
    }
}
