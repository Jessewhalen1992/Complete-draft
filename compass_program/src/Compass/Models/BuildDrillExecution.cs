using System.Collections.Generic;

namespace Compass.Models;

public sealed class BuildDrillExecutionOptions
{
    public bool CreateGeometry { get; init; } = true;
}

public sealed class BuildDrillExecutionResult
{
    public required string DocumentName { get; init; }

    public required string DrillName { get; init; }

    public required string DrillLetter { get; init; }

    public bool GeometryCreated { get; init; }

    public required string Summary { get; init; }

    public required IReadOnlyList<BuildDrillResolvedPoint> Points { get; init; }
}

public sealed class BuildDrillResolvedPoint
{
    public int Sequence { get; init; }

    public required string Label { get; init; }

    public double X { get; init; }

    public double Y { get; init; }

    public double Z { get; init; }

    public required string Note { get; init; }
}
