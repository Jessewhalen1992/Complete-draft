using System;
using System.Collections.Generic;
using System.Linq;

namespace Compass.Models;

public enum DrillCheckStatus
{
    NotChecked,
    Pass,
    Fail
}

public class DrillCheckResult
{
    public static readonly DrillCheckResult[] EmptyResults = new DrillCheckResult[0];

    public DrillCheckResult(
        int index,
        string drillName,
        string tableValue,
        string blockDrillName,
        IReadOnlyList<string> discrepancies)
    {
        Index = index;
        DrillName = drillName ?? string.Empty;
        TableValue = tableValue ?? string.Empty;
        BlockDrillName = blockDrillName ?? string.Empty;
        Discrepancies = discrepancies ?? new string[0];
        Status = Discrepancies.Count == 0 ? DrillCheckStatus.Pass : DrillCheckStatus.Fail;
    }

    public int Index { get; }

    public string DrillName { get; }

    public string TableValue { get; }

    public string BlockDrillName { get; }

    public IReadOnlyList<string> Discrepancies { get; }

    public string[] Notes { get; set; } = new string[0];

    public DrillCheckStatus Status { get; }

    public string Summary =>
        Status == DrillCheckStatus.Pass
            ? "Drill matches table value and block attribute."
            : string.Join(" ", Discrepancies);
}

public class DrillCheckSummary
{
    public DrillCheckSummary(bool completed, IReadOnlyList<DrillCheckResult> results, string? reportPath)
    {
        Completed = completed;
        Results = results ?? new DrillCheckResult[0];
        ReportPath = reportPath;
    }

    public bool Completed { get; }

    public IReadOnlyList<DrillCheckResult> Results { get; }

    public string? ReportPath { get; }

    public bool HasFailures => Results.Any(result => result.Status == DrillCheckStatus.Fail);
}
