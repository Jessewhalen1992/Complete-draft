using System.Collections.Generic;
using Autodesk.AutoCAD.Geometry;

namespace Compass.Models;

public enum BuildDrillSource
{
    Nad83Utms,
    Nad27Utms,
    SectionOffsets
}

public enum BuildDrillNorthSouthReference
{
    NorthOfSouth,
    SouthOfNorth
}

public enum BuildDrillEastWestReference
{
    EastOfWest,
    WestOfEast
}

public sealed class BuildDrillPointRequest
{
    public required BuildDrillSource Source { get; init; }

    public int Zone { get; init; }

    public double X { get; init; }

    public double Y { get; init; }

    public string Section { get; init; } = string.Empty;

    public string Township { get; init; } = string.Empty;

    public string Range { get; init; } = string.Empty;

    public string Meridian { get; init; } = string.Empty;

    public bool UseAtsFabric { get; init; }

    public double NorthSouthDistance { get; init; }

    public BuildDrillNorthSouthReference NorthSouthReference { get; init; }

    public double EastWestDistance { get; init; }

    public BuildDrillEastWestReference EastWestReference { get; init; }
}

public sealed class BuildDrillRequest
{
    public required string DrillName { get; init; }

    public required string DrillLetter { get; init; }

    public Point3d? SurfacePoint { get; init; }

    public required IReadOnlyList<BuildDrillPointRequest> Points { get; init; }
}
