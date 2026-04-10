using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Compass.Infrastructure;
using Compass.Models;
using Compass.Services;

namespace Compass;

public class BuildDrillCommandBridge
{
    private const string ExecuteCommandName = "COMPASSBUILDDRILL_EXECUTE";
    private static readonly object Sync = new();
    private static PendingBuildDrillRequest? _pending;

    public static bool TryQueue(BuildDrillRequest request, string? documentName, out string? error)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (Sync)
        {
            if (_pending != null)
            {
                error = "A Build a Drill request is already queued. Let that one finish and try again.";
                return false;
            }

            _pending = new PendingBuildDrillRequest(Clone(request), documentName);
            error = null;
            return true;
        }
    }

    public static void QueueExecution(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        CompassStartupDiagnostics.Log($"Build a Drill execution command queued for '{document.Name}'.");
        document.SendStringToExecute($"{ExecuteCommandName} ", true, false, false);
    }

    [CommandMethod(ExecuteCommandName, CommandFlags.Modal)]
    public static void ExecuteQueuedBuild()
    {
        PendingBuildDrillRequest? pending;
        lock (Sync)
        {
            pending = _pending;
            _pending = null;
        }

        if (pending == null)
        {
            Application.ShowAlertDialog("No Build a Drill request was queued.");
            return;
        }

        var document = Application.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            Application.ShowAlertDialog("No active AutoCAD document is available.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(pending.DocumentName) &&
            !string.Equals(pending.DocumentName, document.Name, StringComparison.OrdinalIgnoreCase))
        {
            Application.ShowAlertDialog("The active drawing changed before Build a Drill could run. Reopen the dialog and try again.");
            return;
        }

        CompassEnvironment.Initialize();
        CompassStartupDiagnostics.Log($"Build a Drill execution starting in '{document.Name}'.");
        var service = new BuildDrillService(CompassEnvironment.Log, new LayerService());
        service.BuildDrill(pending.Request);
    }

    private static BuildDrillRequest Clone(BuildDrillRequest request)
    {
        var points = new List<BuildDrillPointRequest>(request.Points.Count);
        foreach (var point in request.Points)
        {
            points.Add(new BuildDrillPointRequest
            {
                Source = point.Source,
                Zone = point.Zone,
                X = point.X,
                Y = point.Y,
                Section = point.Section,
                Township = point.Township,
                Range = point.Range,
                Meridian = point.Meridian,
                UseAtsFabric = point.UseAtsFabric,
                NorthSouthDistance = point.NorthSouthDistance,
                NorthSouthReference = point.NorthSouthReference,
                EastWestDistance = point.EastWestDistance,
                EastWestReference = point.EastWestReference
            });
        }

        return new BuildDrillRequest
        {
            DrillName = request.DrillName,
            DrillLetter = request.DrillLetter,
            SurfacePoint = request.SurfacePoint,
            Points = points
        };
    }

    private sealed record PendingBuildDrillRequest(BuildDrillRequest Request, string? DocumentName);
}
