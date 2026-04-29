using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using AtsBackgroundBuilder;
using AtsBackgroundBuilder.Sections;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Compass.Infrastructure.Logging;
using Compass.Models;
using WildlifeSweeps;

using AutoCADApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Compass.Services;

public sealed class BuildDrillService
{
    private static readonly object AdeProjectionSync = new();
    private const string Nad27TracePathEnvironmentVariable = "COMPASS_NAD27_TRACE_PATH";
    private const string DrillPathLayer = "P-PDRILLPATH";
    private const string DrillPointsLayer = "Z-DRILL-POINT";
    private const string HorizontalBoundaryLayer = "L-SEC-HB";
    private const double DrillPointTextHeight = 2.0;
    private const string DefaultSectionIndexFolder = @"C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\RES MANAGER";
    private const double AtsBoundarySearchDistance = 45.0;
    private const double HorizontalBoundarySearchDistance = 10.0;
    private const double MinimumLineLength = 1e-3;
    private const double ParallelDotTolerance = 0.8;
    private const double BoundaryProbeSegmentTolerance = 0.25;
    private const double BoundaryProbeExtensionTolerance = 30.0;
    private const double BoundarySideTolerance = 1e-3;
    private const double VisibleBoundaryFamilyTolerance = 2.5;
    private const double CordsSectionSearchDistance = 45.0;
    private static readonly string[] HardBoundaryLayers =
    {
        "L-USEC-C-0",
        "L-USEC-0",
        "L-USEC2012",
        "L-USEC-2012",
        "L-SEC",
        "L-SEC-0",
        "L-SEC2012",
        "L-SEC-2012"
    };
    private static readonly HashSet<string> AtsScratchIsolationLayers = new(StringComparer.OrdinalIgnoreCase)
    {
        "L-SEC",
        "L-SEC-0",
        "L-SEC2012",
        "L-SEC-2012",
        "L-QSEC",
        "L-QSEC-BOX",
        "L-USEC",
        "L-USEC-0",
        "L-USEC2012",
        "L-USEC3018",
        "L-USEC-2012",
        "L-USEC-3018",
        "L-USEC-C",
        "L-USEC-C-0",
        "L-SECTION-LSD",
        "L-QUATER",
        "L-QUARTER"
    };

    private readonly ILog _log;
    public BuildDrillService(ILog log, LayerService layerService)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        ArgumentNullException.ThrowIfNull(layerService);
    }

    public void BuildDrill(BuildDrillRequest request)
    {
        try
        {
            var result = ExecuteBuildDrill(request);
            _log.Info($"Build a Drill created a {result.Points.Count}-point path for '{request.DrillName}' on {DrillPathLayer} with {result.Points.Count} point labels on {DrillPointsLayer}. {result.Summary}");
            MessageBox.Show(
                $"Built {request.DrillName} with {result.Points.Count} point(s) on {DrillPathLayer}.\nPoint labels were refreshed on {DrillPointsLayer}.\n{result.Summary}",
                "Build a Drill",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (System.Exception ex)
        {
            _log.Error($"Build a Drill failed: {ex.Message}", ex);
            MessageBox.Show($"Build a Drill failed:\n{ex.Message}", "Build a Drill", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public BuildDrillExecutionResult ExecuteBuildDrill(BuildDrillRequest request, BuildDrillExecutionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        options ??= new BuildDrillExecutionOptions();

        if (request.Points == null || request.Points.Count < 2)
        {
            throw new InvalidOperationException("Build a Drill needs at least two points.");
        }

        var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            throw new InvalidOperationException("No active AutoCAD document is available.");
        }

        var pathPoints = new List<Point3d>(request.Points.Count + (request.SurfacePoint.HasValue ? 1 : 0));
        var pointNotes = new List<string>(request.Points.Count + (request.SurfacePoint.HasValue ? 1 : 0));
        var sectionBoundaryCache = new Dictionary<string, CachedBoundaryResolution>(StringComparer.OrdinalIgnoreCase);

        if (request.SurfacePoint.HasValue)
        {
            pathPoints.Add(request.SurfacePoint.Value);
            pointNotes.Add("surface start point");
        }

        for (var i = 0; i < request.Points.Count; i++)
        {
            var pointRequest = request.Points[i];
            try
            {
                var point = ResolveTargetPoint(
                    pointRequest,
                    document.Database,
                    document.Editor,
                    document.Name,
                    sectionBoundaryCache,
                    out var sourceDescription);
                pathPoints.Add(point);
                pointNotes.Add(sourceDescription);
            }
            catch (System.Exception ex)
            {
                throw new InvalidOperationException($"Point {i + 1} failed: {ex.Message}", ex);
            }
        }

        var drillLetter = NormalizeDrillLetter(request.DrillLetter);
        var labeledPoints = BuildLabeledPoints(drillLetter, request.SurfacePoint, request.Points.Count, pathPoints);
        if (options.CreateGeometry)
        {
            CreateDrillGeometry(document.Database, pathPoints, labeledPoints, drillLetter);
        }

        var summary = BuildPointSummary(pointNotes);
        return new BuildDrillExecutionResult
        {
            DocumentName = document.Name,
            DrillName = request.DrillName,
            DrillLetter = drillLetter,
            GeometryCreated = options.CreateGeometry,
            Summary = summary,
            Points = BuildResolvedPoints(pathPoints, labeledPoints, pointNotes)
        };
    }

    public void WriteResolvedGeometry(Database database, BuildDrillExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(result);

        if (result.Points == null || result.Points.Count == 0)
        {
            throw new InvalidOperationException("No resolved Build a Drill points were available to write.");
        }

        var pathPoints = new List<Point3d>(result.Points.Count);
        var labeledPoints = new List<LabeledDrillPoint>(result.Points.Count);
        foreach (var point in result.Points)
        {
            var resolvedPoint = new Point3d(point.X, point.Y, point.Z);
            pathPoints.Add(resolvedPoint);
            labeledPoints.Add(new LabeledDrillPoint(point.Label, resolvedPoint));
        }

        var drillLetter = NormalizeDrillLetter(result.DrillLetter);
        CreateDrillGeometry(database, pathPoints, labeledPoints, drillLetter);
    }

    private static string BuildPointSummary(IReadOnlyList<string> pointNotes)
    {
        if (pointNotes.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        for (var i = 0; i < pointNotes.Count; i++)
        {
            builder.Append("Point ");
            builder.Append(i + 1);
            builder.Append(": ");
            builder.Append(pointNotes[i]);
            if (i < pointNotes.Count - 1)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString();
    }

    private void CreateDrillGeometry(
        Database database,
        IReadOnlyList<Point3d> pathPoints,
        IReadOnlyList<LabeledDrillPoint> labeledPoints,
        string drillLetter)
    {
        using var transaction = database.TransactionManager.StartTransaction();
        EnsureLayerExists(database, transaction, DrillPathLayer);
        EnsureLayerExists(database, transaction, DrillPointsLayer);
        var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

        RemoveExistingDrillPointLabels(modelSpace, transaction, drillLetter);

        var polyline = new Polyline(pathPoints.Count)
        {
            Layer = DrillPathLayer
        };

        for (var i = 0; i < pathPoints.Count; i++)
        {
            polyline.AddVertexAt(i, new Point2d(pathPoints[i].X, pathPoints[i].Y), 0.0, 0.0, 0.0);
        }

        modelSpace.AppendEntity(polyline);
        transaction.AddNewlyCreatedDBObject(polyline, true);

        foreach (var labeledPoint in labeledPoints)
        {
            var text = new DBText
            {
                Position = labeledPoint.Point,
                Height = DrillPointTextHeight,
                TextString = labeledPoint.Label,
                Layer = DrillPointsLayer,
                ColorIndex = 7
            };

            modelSpace.AppendEntity(text);
            transaction.AddNewlyCreatedDBObject(text, true);
        }

        transaction.Commit();
    }

    private static void EnsureLayerExists(Database database, Transaction transaction, string layerName)
    {
        var layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        if (layerTable.Has(layerName))
        {
            return;
        }

        layerTable.UpgradeOpen();
        var record = new LayerTableRecord
        {
            Name = layerName
        };
        layerTable.Add(record);
        transaction.AddNewlyCreatedDBObject(record, true);
    }

    private static IReadOnlyList<BuildDrillResolvedPoint> BuildResolvedPoints(
        IReadOnlyList<Point3d> pathPoints,
        IReadOnlyList<LabeledDrillPoint> labeledPoints,
        IReadOnlyList<string> pointNotes)
    {
        var points = new List<BuildDrillResolvedPoint>(pathPoints.Count);
        for (var i = 0; i < pathPoints.Count; i++)
        {
            var label = i < labeledPoints.Count ? labeledPoints[i].Label : string.Empty;
            var note = i < pointNotes.Count ? pointNotes[i] : string.Empty;
            points.Add(new BuildDrillResolvedPoint
            {
                Sequence = i + 1,
                Label = label,
                X = pathPoints[i].X,
                Y = pathPoints[i].Y,
                Z = pathPoints[i].Z,
                Note = note
            });
        }

        return points;
    }

    private static string NormalizeDrillLetter(string? drillLetter)
    {
        var letter = string.IsNullOrWhiteSpace(drillLetter)
            ? 'A'
            : char.ToUpperInvariant(drillLetter.Trim()[0]);

        if (letter < 'A' || letter > 'Z')
        {
            letter = 'A';
        }

        return letter.ToString(CultureInfo.InvariantCulture);
    }

    private static List<LabeledDrillPoint> BuildLabeledPoints(string drillLetter, Point3d? surfacePoint, int resolvedPointCount, IReadOnlyList<Point3d> pathPoints)
    {
        var labeledPoints = new List<LabeledDrillPoint>(pathPoints.Count);
        var pathIndex = 0;

        if (surfacePoint.HasValue)
        {
            labeledPoints.Add(new LabeledDrillPoint($"{drillLetter}1", surfacePoint.Value));
            pathIndex = 1;
        }

        var nextNumber = 2;
        for (var i = pathIndex; i < pathPoints.Count; i++)
        {
            labeledPoints.Add(new LabeledDrillPoint($"{drillLetter}{nextNumber}", pathPoints[i]));
            nextNumber++;
        }

        return labeledPoints;
    }

    private static void RemoveExistingDrillPointLabels(BlockTableRecord modelSpace, Transaction transaction, string drillLetter)
    {
        foreach (ObjectId id in modelSpace)
        {
            if (transaction.GetObject(id, OpenMode.ForRead) is not Entity entity)
            {
                continue;
            }

            if (!entity.Layer.Equals(DrillPointsLayer, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryGetDrillPointLabel(entity, out var label))
            {
                continue;
            }

            if (!label.StartsWith(drillLetter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            entity.UpgradeOpen();
            entity.Erase();
        }
    }

    private static bool TryGetDrillPointLabel(Entity entity, out string label)
    {
        label = string.Empty;

        switch (entity)
        {
            case DBText dbText when DrillParsers.IsGridLabel(dbText.TextString):
                label = dbText.TextString.Trim().ToUpperInvariant();
                return true;
            case MText mText when DrillParsers.IsGridLabel(mText.Contents):
                label = mText.Contents.Trim().ToUpperInvariant();
                return true;
            default:
                return false;
        }
    }

    private sealed record LabeledDrillPoint(string Label, Point3d Point);

    internal bool TryResolveNativeCordsAtsMatch(
        Database database,
        Editor editor,
        string? drawingPath,
        int zone,
        Point2d point,
        out AtsQuarterLocationResolver.LsdMatch match,
        out string detail)
    {
        match = default;
        detail = string.Empty;

        if (!TryLoadNearestCordsSectionFrame(zone, drawingPath, point, out var section, out detail))
        {
            return false;
        }

        var request = CreateCordsSectionRequest(zone, section.Key);
        if (!TryResolveAtsScratchHardBoundaries(
                editor,
                database,
                drawingPath,
                section.Frame,
                request,
                out var candidates,
                out var boundaryDetail))
        {
            detail =
                $"Nearest section {section.Key.Section}-{section.Key.Township}-{section.Key.Range}-W{section.Key.Meridian} was found, but Complete CORDS could not build hidden ATS measurement fabric. {boundaryDetail}".Trim();
            return false;
        }

        if (!TryBuildNativeCordsAtsMatch(
                section.Key,
                section.Frame,
                point,
                candidates,
                out match,
                out var offsetDetail))
        {
            detail =
                $"Nearest section {section.Key.Section}-{section.Key.Township}-{section.Key.Range}-W{section.Key.Meridian} was found, but CORDS offsets could not be measured from hidden ATS measurement fabric. {offsetDetail}".Trim();
            return false;
        }

        detail = $"CORDS offsets from hidden ATS measurement fabric; {offsetDetail} ({boundaryDetail})";
        return true;
    }

    internal bool TryResolveNativeCordsAtsOffsets(
        Database database,
        Editor editor,
        string? drawingPath,
        int zone,
        Point2d point,
        AtsQuarterLocationResolver.LsdMatch baseMatch,
        out AtsQuarterLocationResolver.LsdMatch match,
        out string detail)
    {
        match = default;
        detail = string.Empty;
        var key = new SectionKey(zone, baseMatch.Section, baseMatch.Township, baseMatch.Range, baseMatch.Meridian);
        var request = CreateCordsSectionRequest(zone, key);
        if (!TryLoadSectionFrame(request, drawingPath, out var frame))
        {
            detail =
                $"Could not load ATS section {key.Section}-{key.Township}-{key.Range}-W{key.Meridian} in zone {zone} from the section index search path.";
            return false;
        }

        if (!TryResolveAtsScratchHardBoundaries(
                editor,
                database,
                drawingPath,
                frame,
                request,
                out var candidates,
                out var boundaryDetail))
        {
            detail =
                $"Complete CORDS could not build hidden ATS measurement fabric for {key.Section}-{key.Township}-{key.Range}-W{key.Meridian}. {boundaryDetail}".Trim();
            return false;
        }

        if (!TryResolveCordsAtsOffsets(
                frame,
                point,
                candidates,
                out var metes,
                out var bounds,
                out var offsetDetail))
        {
            detail =
                $"Complete CORDS could not measure offsets from hidden ATS measurement fabric for {key.Section}-{key.Township}-{key.Range}-W{key.Meridian}. {offsetDetail}".Trim();
            return false;
        }

        match = new AtsQuarterLocationResolver.LsdMatch(
            baseMatch.Location,
            baseMatch.Lsd,
            baseMatch.QuarterToken,
            baseMatch.Section,
            baseMatch.Township,
            baseMatch.Range,
            baseMatch.Meridian,
            metes,
            bounds,
            baseMatch.QuarterVertices);
        detail = $"CORDS offsets from hidden ATS measurement fabric; {offsetDetail} ({boundaryDetail})";
        return true;
    }

    private static BuildDrillPointRequest CreateCordsSectionRequest(int zone, SectionKey key)
    {
        return new BuildDrillPointRequest
        {
            Source = BuildDrillSource.SectionOffsets,
            Zone = zone,
            Section = key.Section,
            Township = key.Township,
            Range = key.Range,
            Meridian = key.Meridian,
            UseAtsFabric = true,
            CombinedScaleFactor = 1.0,
            NorthSouthReference = BuildDrillNorthSouthReference.NorthOfSouth,
            EastWestReference = BuildDrillEastWestReference.EastOfWest
        };
    }

    private Point3d ResolveTargetPoint(
        BuildDrillPointRequest request,
        Database database,
        Editor editor,
        string? drawingPath,
        IDictionary<string, CachedBoundaryResolution> sectionBoundaryCache,
        out string sourceDescription)
    {
        switch (request.Source)
        {
            case BuildDrillSource.Nad83Utms:
                sourceDescription = $"NAD83 UTM Zone {request.Zone}";
                return new Point3d(request.X, request.Y, 0.0);

            case BuildDrillSource.Nad27Utms:
                sourceDescription = $"NAD27 UTM Zone {request.Zone}";
                return ConvertNad27ToNad83(request);

            case BuildDrillSource.SectionOffsets:
                return ResolveSectionOffsetPoint(request, database, editor, drawingPath, sectionBoundaryCache, out sourceDescription);

            default:
                throw new InvalidOperationException($"Unsupported build source: {request.Source}.");
        }
    }

    private Point3d ConvertNad27ToNad83(BuildDrillPointRequest request)
    {
        var sourceCode = $"UTM27-{request.Zone}";
        var destinationCode = $"UTM83-{request.Zone}";
        var failureDetails = new List<string>(2);
        TraceNad27Step($"ConvertNad27ToNad83 start {sourceCode}->{destinationCode} x={request.X:0.###} y={request.Y:0.###}");

        TraceNad27Step("Attempting managed Map transformer creation.");
        if (Map3dCoordinateTransformer.TryCreate(sourceCode, destinationCode, out var transformer) && transformer != null)
        {
            TraceNad27Step("Managed Map transformer created. Attempting projection.");
            // The shared Map transformer helper returns Y first and X second.
            if (transformer.TryProject(new Point3d(request.X, request.Y, 0.0), out var transformedY, out var transformedX))
            {
                TraceNad27Step($"Managed Map transformer succeeded -> x={transformedX:0.###} y={transformedY:0.###}");
                return new Point3d(transformedX, transformedY, 0.0);
            }

            TraceNad27Step("Managed Map transformer returned no projected point.");
            failureDetails.Add("managed Map transformer loaded but returned no projected point");
        }
        else
        {
            TraceNad27Step("Managed Map transformer could not be created.");
            failureDetails.Add("managed Map transformer could not be created");
        }

        TraceNad27Step("Attempting ADE fallback.");
        if (TryConvertNad27ToNad83ViaAde(sourceCode, destinationCode, request.X, request.Y, out var fallbackPoint, out var adeDetail))
        {
            TraceNad27Step($"ADE fallback succeeded -> x={fallbackPoint.X:0.###} y={fallbackPoint.Y:0.###}");
            return fallbackPoint;
        }

        TraceNad27Step($"ADE fallback failed: {adeDetail}");
        failureDetails.Add(adeDetail);
        var detail = string.Join("; ", failureDetails.Where(part => !string.IsNullOrWhiteSpace(part)));
        _log.Warn($"Build a Drill NAD27 conversion failed for {sourceCode} -> {destinationCode}: {detail}");
        throw new InvalidOperationException(
            $"Could not convert {sourceCode} -> {destinationCode}. {detail}");
    }

    private static bool TryConvertNad27ToNad83ViaAde(string sourceCode, string destinationCode, double x, double y, out Point3d point, out string detail)
    {
        point = default;
        detail = string.Empty;

        lock (AdeProjectionSync)
        {
            TraceNad27Step("ADE invoke fallback starting.");
            if (TryConvertNad27ToNad83ViaAdeInvoke(sourceCode, destinationCode, x, y, out point, out detail))
            {
                TraceNad27Step("ADE invoke fallback succeeded.");
                return true;
            }

            TraceNad27Step($"ADE invoke fallback failed: {detail}");
            var invokeDetail = detail;
            TraceNad27Step("ADE SendCommand fallback starting.");
            if (TryConvertNad27ToNad83ViaSendCommand(sourceCode, destinationCode, x, y, out point, out var commandDetail))
            {
                TraceNad27Step("ADE SendCommand fallback succeeded.");
                return true;
            }

            TraceNad27Step($"ADE SendCommand fallback failed: {commandDetail}");
            detail = $"{invokeDetail}; command-line ADE returned {commandDetail}";
            return false;
        }
    }

    private static bool TryConvertNad27ToNad83ViaAdeInvoke(string sourceCode, string destinationCode, double x, double y, out Point3d point, out string detail)
    {
        point = default;
        detail = string.Empty;

        try
        {
            TraceNad27Step("ADE invoke: ade_errclear");
            InvokeLispFunction("ade_errclear");

            TraceNad27Step($"ADE invoke: ade_projsetsrc {sourceCode}");
            using var sourceResult = InvokeLispFunction("ade_projsetsrc", new TypedValue((int)LispDataType.Text, sourceCode));
            if (!HasTruthyLispResult(sourceResult))
            {
                detail = $"ADE ade_projsetsrc returned {DescribeResultBuffer(sourceResult)}";
                return false;
            }

            TraceNad27Step($"ADE invoke: ade_projsetdest {destinationCode}");
            using var destinationResult = InvokeLispFunction("ade_projsetdest", new TypedValue((int)LispDataType.Text, destinationCode));
            if (!HasTruthyLispResult(destinationResult))
            {
                detail = $"ADE ade_projsetdest returned {DescribeResultBuffer(destinationResult)}";
                return false;
            }

            TraceNad27Step("ADE invoke: ade_projptforward Point3d");
            if (TryInvokeAdeProjectionVariant(
                    "Point3d",
                    out point,
                    out detail,
                    new TypedValue((int)LispDataType.Point3d, new Point3d(x, y, 0.0))))
            {
                return true;
            }

            TraceNad27Step("ADE invoke: ade_projptforward XYZ list");
            if (TryInvokeAdeProjectionVariant(
                    "XYZ list",
                    out point,
                    out detail,
                    new TypedValue((int)LispDataType.ListBegin),
                    new TypedValue((int)LispDataType.Double, x),
                    new TypedValue((int)LispDataType.Double, y),
                    new TypedValue((int)LispDataType.Double, 0.0),
                    new TypedValue((int)LispDataType.ListEnd)))
            {
                return true;
            }

            TraceNad27Step("ADE invoke: ade_projptforward XY list");
            if (TryInvokeAdeProjectionVariant(
                    "XY list",
                    out point,
                    out detail,
                    new TypedValue((int)LispDataType.ListBegin),
                    new TypedValue((int)LispDataType.Double, x),
                    new TypedValue((int)LispDataType.Double, y),
                    new TypedValue((int)LispDataType.ListEnd)))
            {
                return true;
            }

            return false;
        }
        catch (System.Exception ex)
        {
            TraceNad27Step($"ADE invoke exception: {ex.GetType().Name}: {ex.Message}");
            detail = $"ADE invoke fallback threw {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static bool TryInvokeAdeProjectionVariant(string variantName, out Point3d point, out string detail, params TypedValue[] args)
    {
        point = default;
        detail = string.Empty;

        using var projectionResult = InvokeLispFunction("ade_projptforward", args);
        if (TryReadPointResult(projectionResult, out point))
        {
            return true;
        }

        detail = $"ADE ade_projptforward {variantName} returned {DescribeResultBuffer(projectionResult)}";
        return false;
    }

    private static bool TryConvertNad27ToNad83ViaSendCommand(string sourceCode, string destinationCode, double x, double y, out Point3d point, out string detail)
    {
        point = default;
        detail = string.Empty;

        var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            detail = "no active document for command-line ADE fallback";
            return false;
        }

        var symbolName = $"COMPASS_ADE_{Guid.NewGuid():N}";
        var xText = ToLispRealLiteral(x);
        var yText = ToLispRealLiteral(y);
        var expression = BuildAdeCommandExpression(symbolName, sourceCode, destinationCode, xText, yText);

        try
        {
            TraceNad27Step("ADE SendCommand: dispatching expression.");
            InvokeComSendCommand(expression);
            var symbolValue = document.GetLispSymbol(symbolName);
            var raw = symbolValue?.ToString() ?? string.Empty;
            if (TryParseAdeCommandResult(raw, out point, out detail))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(detail))
            {
                detail = string.IsNullOrWhiteSpace(raw) ? "empty symbol result" : raw;
            }

            return false;
        }
        catch (System.Exception ex)
        {
            TraceNad27Step($"ADE SendCommand exception: {ex.GetType().Name}: {ex.Message}");
            detail = $"SendCommand ADE fallback threw {ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            try
            {
                document.SetLispSymbol(symbolName, null);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    private static string BuildAdeCommandExpression(string symbolName, string sourceCode, string destinationCode, string xText, string yText)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $@"(progn (setq {symbolName} (cond ((not (ade_projsetsrc ""{sourceCode}"")) ""ERR:ade_projsetsrc"") ((not (ade_projsetdest ""{destinationCode}"")) ""ERR:ade_projsetdest"") ((setq compassPt (ade_projptforward (list {xText} {yText}))) (strcat ""OK:"" (rtos (car compassPt) 2 16) "","" (rtos (cadr compassPt) 2 16))) (T ""ERR:ade_projptforward""))) (princ)){Environment.NewLine}");
    }

    private static string ToLispRealLiteral(double value)
    {
        var text = value.ToString("0.0###############", CultureInfo.InvariantCulture);
        return text.Contains('.') ? text : $"{text}.0";
    }

    private static void InvokeComSendCommand(string expression)
    {
        var acadApplication = AutoCADApplication.AcadApplication;
        if (acadApplication == null)
        {
            throw new InvalidOperationException("AutoCAD COM automation is not available.");
        }

        var acadDocument = acadApplication.GetType().InvokeMember(
            "ActiveDocument",
            BindingFlags.GetProperty,
            binder: null,
            target: acadApplication,
            args: null);
        if (acadDocument == null)
        {
            throw new InvalidOperationException("AutoCAD COM active document is not available.");
        }

        acadDocument.GetType().InvokeMember(
            "SendCommand",
            BindingFlags.InvokeMethod,
            binder: null,
            target: acadDocument,
            args: new object[] { expression });
    }

    private static bool TryParseAdeCommandResult(string raw, out Point3d point, out string detail)
    {
        point = default;
        detail = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            detail = "command-line ADE result symbol was blank";
            return false;
        }

        if (!raw.StartsWith("OK:", StringComparison.OrdinalIgnoreCase))
        {
            detail = raw;
            return false;
        }

        var values = raw.Substring(3).Split(',');
        if (values.Length < 2 ||
            !double.TryParse(values[0], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var convertedX) ||
            !double.TryParse(values[1], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var convertedY))
        {
            detail = $"command-line ADE returned unparseable coordinates '{raw}'";
            return false;
        }

        point = new Point3d(convertedX, convertedY, 0.0);
        return true;
    }

    private static bool HasTruthyLispResult(ResultBuffer? result)
    {
        if (result == null)
        {
            return false;
        }

        foreach (var value in result.AsArray())
        {
            if (value.Value is string text && string.Equals(text, "nil", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (value.Value != null)
            {
                return true;
            }
        }

        return false;
    }

    private static ResultBuffer? InvokeLispFunction(string functionName, params TypedValue[] args)
    {
        using var request = new ResultBuffer();
        request.Add(new TypedValue((int)LispDataType.Text, functionName));
        foreach (var arg in args)
        {
            request.Add(arg);
        }

        return AutoCADApplication.Invoke(request);
    }

    private static bool TryReadPointResult(ResultBuffer? result, out Point3d point)
    {
        point = default;
        if (result == null)
        {
            return false;
        }

        var values = result.AsArray();
        var doubles = new List<double>(3);
        foreach (var value in values)
        {
            if (value.TypeCode == (int)LispDataType.Double)
            {
                doubles.Add(Convert.ToDouble(value.Value));
                continue;
            }

            if (value.Value is Point3d point3d)
            {
                point = point3d;
                return true;
            }

            if (value.Value is Point2d point2d)
            {
                point = new Point3d(point2d.X, point2d.Y, 0.0);
                return true;
            }
        }

        if (doubles.Count >= 2)
        {
            point = new Point3d(doubles[0], doubles[1], doubles.Count >= 3 ? doubles[2] : 0.0);
            return true;
        }

        return false;
    }

    private static string DescribeResultBuffer(ResultBuffer? result)
    {
        if (result == null)
        {
            return "null";
        }

        var values = result.AsArray();
        if (values.Length == 0)
        {
            return "empty";
        }

        return string.Join(", ", values.Select(DescribeTypedValue));
    }

    private static string DescribeTypedValue(TypedValue value)
    {
        var renderedValue = value.Value switch
        {
            null => "<null>",
            string text => $"\"{text}\"",
            _ => value.Value.ToString() ?? "<unprintable>"
        };

        return $"{value.TypeCode}:{renderedValue}";
    }

    private static void TraceNad27Step(string message)
    {
        try
        {
            var path = Environment.GetEnvironmentVariable(Nad27TracePathEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var fullPath = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? Path.GetTempPath());
            File.AppendAllText(
                fullPath,
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // Best-effort trace only.
        }
    }

    private Point3d ResolveSectionOffsetPoint(
        BuildDrillPointRequest request,
        Database database,
        Editor editor,
        string? drawingPath,
        IDictionary<string, CachedBoundaryResolution> sectionBoundaryCache,
        out string sourceDescription)
    {
        if (!TryLoadSectionFrame(request, drawingPath, out var sectionFrame))
        {
            throw new InvalidOperationException(
                $"Could not load ATS section {request.Section}-{request.Township}-{request.Range}-W{request.Meridian} in zone {request.Zone} from the section index search path.");
        }

        ValidateDistancesAgainstFrame(request, sectionFrame);

        if (!request.UseAtsFabric)
        {
            throw new InvalidOperationException(
                "Section offsets require ATS hard boundaries. Run from a saved DWG and make sure AtsBackgroundBuilder.dll is available.");
        }

        if (!TryGetOrResolveSectionHardBoundaryCandidates(
                request,
                database,
                editor,
                drawingPath,
                sectionFrame,
                sectionBoundaryCache,
                out var cachedResolution,
                out var resolutionDetail))
        {
            throw new InvalidOperationException(
                $"Could not resolve hard ATS boundaries for section {request.Section}-{request.Township}-{request.Range}-W{request.Meridian} in zone {request.Zone}. {resolutionDetail}");
        }

        if (!TryComputeOffsetPointFromBoundaryCandidates(sectionFrame, cachedResolution.Candidates, request, out var cachedPoint, out var cachedDetail))
        {
            throw new InvalidOperationException(
                $"Could not resolve hard ATS boundaries for section {request.Section}-{request.Township}-{request.Range}-W{request.Meridian} in zone {request.Zone}. {cachedDetail}");
        }

        sourceDescription = AppendResolutionDetail(cachedResolution.SourceDescription, cachedDetail);
        return cachedPoint;
    }

    private Point3d ResolveSectionOffsetPointWithoutAtsFabric(
        BuildDrillPointRequest request,
        Database database,
        Editor editor,
        string? drawingPath,
        SectionFrame frame,
        BoundarySet sectionIndexBoundaries,
        IDictionary<string, CachedBoundaryResolution> sectionBoundaryCache,
        out string sourceDescription)
    {
        if (!TryGetOrResolveSectionHardBoundaryCandidates(
                request,
                database,
                editor,
                drawingPath,
                frame,
                sectionBoundaryCache,
                out var hardBoundaryResolution,
                out var hardBoundaryDetail))
        {
            throw new InvalidOperationException(
                $"Could not resolve calculated hard ATS boundaries for section {request.Section}-{request.Township}-{request.Range}-W{request.Meridian} in zone {request.Zone}. {hardBoundaryDetail}");
        }

        if (!TryResolveCalculatedHardBoundariesForPoint(
                frame,
                request,
                hardBoundaryResolution.Candidates,
                sectionIndexBoundaries,
                out var referenceBoundaries,
                out var resolutionDetail))
        {
            throw new InvalidOperationException(
                $"Could not resolve calculated hard ATS boundaries for section {request.Section}-{request.Township}-{request.Range}-W{request.Meridian} in zone {request.Zone}. {resolutionDetail}");
        }

        var referencePoint = ComputeOffsetPoint(frame, referenceBoundaries, request);
        var hbSearch = ResolveNearestBoundaries(
            database,
            frame,
            new Point2d(referencePoint.X, referencePoint.Y),
            referenceBoundaries,
            new[] { HorizontalBoundaryLayer },
            HorizontalBoundarySearchDistance);

        sourceDescription = AppendResolutionDetail(
            UsesHorizontalBoundaryForRequestedSides(request, hbSearch.Boundaries)
                ? "section offsets from L-SEC-HB"
                : "section offsets from calculated hard boundaries",
            DescribeNonAtsBoundaryResolution(request, referenceBoundaries, hbSearch.Boundaries));

        return ComputeOffsetPoint(frame, hbSearch.Boundaries, request);
    }

    private static string AppendResolutionDetail(string sourceDescription, string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return sourceDescription;
        }

        return $"{sourceDescription} ({detail})";
    }

    private bool TryGetOrResolveSectionHardBoundaryCandidates(
        BuildDrillPointRequest request,
        Database database,
        Editor editor,
        string? drawingPath,
        SectionFrame frame,
        IDictionary<string, CachedBoundaryResolution> sectionBoundaryCache,
        out CachedBoundaryResolution resolution,
        out string detail)
    {
        var cacheKey = BuildSectionBoundaryCacheKey(request, "hard");
        if (sectionBoundaryCache.TryGetValue(cacheKey, out resolution))
        {
            detail = string.Empty;
            return true;
        }

        if (TryCollectLiveHardBoundaryCandidates(database, frame, out var liveCandidates) &&
            liveCandidates.Count > 0)
        {
            resolution = new CachedBoundaryResolution(liveCandidates, "section offsets from live hard ATS boundaries");
            sectionBoundaryCache[cacheKey] = resolution;
            detail = string.Empty;
            return true;
        }

        if (TryResolveAtsScratchHardBoundaries(editor, database, drawingPath, frame, request, out var scratchCandidates, out var scratchDetail) &&
            scratchCandidates.Count > 0)
        {
            resolution = new CachedBoundaryResolution(scratchCandidates, "section offsets from ATS-built hard boundaries");
            sectionBoundaryCache[cacheKey] = resolution;
            detail = string.Empty;
            return true;
        }

        resolution = default;
        detail = string.IsNullOrWhiteSpace(scratchDetail)
            ? "No hard ATS boundary candidates were available for this section."
            : scratchDetail;
        return false;
    }

    private static string BuildSectionBoundaryCacheKey(BuildDrillPointRequest request, string mode)
    {
        return string.Join(
            "|",
            request.Zone.ToString(CultureInfo.InvariantCulture),
            request.Section.Trim(),
            request.Township.Trim(),
            request.Range.Trim(),
            request.Meridian.Trim(),
            mode);
    }

    private static bool TryCollectLiveHardBoundaryCandidates(Database database, SectionFrame frame, out List<StraightBoundary> candidates)
    {
        candidates = CollectBoundaryCandidates(database, frame, null);
        return candidates.Count > 0;
    }

    private bool TryResolveAtsScratchHardBoundaries(
        Editor editor,
        Database database,
        string? drawingPath,
        SectionFrame frame,
        BuildDrillPointRequest request,
        out List<StraightBoundary> candidates,
        out string detail)
    {
        candidates = new List<StraightBoundary>();
        detail = string.Empty;
        if (database == null || editor == null)
        {
            detail = "The active AutoCAD document was unavailable for ATS hard-boundary generation.";
            return false;
        }

        if (!TryLoadAtsBackgroundBuilderAssembly(database, out var atsAssembly, out var assemblySource))
        {
            detail =
                "AtsBackgroundBuilder.dll was not found in the current probe paths. ATS measurement requires hidden ATS Builder fabric (`L-USEC-0`, `L-USEC-2012`, and `L-SEC`) and cannot fall back to section-index offsets.";
            return false;
        }

        var pluginType = atsAssembly.GetType("AtsBackgroundBuilder.Plugin", throwOnError: false);
        var loggerType = atsAssembly.GetType("AtsBackgroundBuilder.Logger", throwOnError: false);
        var configType = atsAssembly.GetType("AtsBackgroundBuilder.Core.Config", throwOnError: false);
        var sectionRequestType = atsAssembly.GetType("AtsBackgroundBuilder.SectionRequest", throwOnError: false);
        var quarterSelectionType = atsAssembly.GetType("AtsBackgroundBuilder.QuarterSelection", throwOnError: false);
        var sectionKeyType = atsAssembly.GetType("AtsBackgroundBuilder.Sections.SectionKey", throwOnError: false)
            ?? atsAssembly.GetType("AtsBackgroundBuilder.SectionKey", throwOnError: false);
        if (pluginType == null ||
            loggerType == null ||
            configType == null ||
            sectionRequestType == null ||
            quarterSelectionType == null ||
            sectionKeyType == null)
        {
            detail = "The required ATS section-build types were not found.";
            return false;
        }

        var drawSectionsMethod = pluginType
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(method =>
                string.Equals(method.Name, "DrawSectionsFromRequests", StringComparison.Ordinal) &&
                method.GetParameters().Length == 8);
        if (drawSectionsMethod == null)
        {
            detail = "The ATS DrawSectionsFromRequests method was not found.";
            return false;
        }

        object? logger = null;
        try
        {
            logger = Activator.CreateInstance(loggerType);
            var config = Activator.CreateInstance(configType);
            if (logger == null || config == null)
            {
                detail = "Failed to instantiate ATS runtime objects.";
                return false;
            }

            SetReflectionProperty(config, "UseSectionIndex", true);
            SetReflectionProperty(config, "SectionIndexFolder", ResolveSectionIndexFolderForAtsBuild(database, drawingPath));

            var quarterAll = Enum.Parse(quarterSelectionType, "All", ignoreCase: true);
            var requestListType = typeof(List<>).MakeGenericType(sectionRequestType);
            var requestList = Activator.CreateInstance(requestListType);
            var addRequest = requestListType.GetMethod("Add");
            if (requestList == null || addRequest == null)
            {
                detail = "Failed to create the ATS section request list.";
                return false;
            }

            var sectionKey = Activator.CreateInstance(
                sectionKeyType,
                request.Zone,
                request.Section,
                request.Township,
                request.Range,
                request.Meridian);
            if (sectionKey == null)
            {
                detail = "Failed to create the ATS section key.";
                return false;
            }

            var sectionRequest = Activator.CreateInstance(sectionRequestType, quarterAll, sectionKey, "AUTO");
            if (sectionRequest == null)
            {
                detail = "Failed to create the ATS section request.";
                return false;
            }

            addRequest.Invoke(requestList, new[] { sectionRequest });

            if (!TryOpenFileBackedScratchDatabase(database, out var scratchDb, out var scratchDetail))
            {
                detail = scratchDetail;
                return false;
            }

            using (scratchDb)
            {
                var unlockedScratchLayers = UnlockScratchLayers(scratchDb);
                var removedScratchEntities = RemoveScratchAtsLinework(scratchDb);
                object? sectionDrawResult;
                var previousWorkingDb = HostApplicationServices.WorkingDatabase;
                try
                {
                    HostApplicationServices.WorkingDatabase = scratchDb;
                    sectionDrawResult = drawSectionsMethod.Invoke(
                        null,
                        new object[] { editor, scratchDb, requestList, config, logger, false, false, true });
                }
                finally
                {
                    HostApplicationServices.WorkingDatabase = previousWorkingDb;
                }

                if (sectionDrawResult == null)
                {
                    detail = "The ATS scratch build returned no section result.";
                    return false;
                }

                var generatedRoadAllowanceIds = GetObjectIdsFromReflectionProperty(sectionDrawResult, "GeneratedRoadAllowanceEntityIds");
                var generatedSectionIds = GetObjectIdsFromReflectionProperty(sectionDrawResult, "SectionPolylineIds");
                var generatedContextSectionIds = GetObjectIdsFromReflectionProperty(sectionDrawResult, "ContextSectionPolylineIds");
                var generatedMeasurementIds = MergeObjectIds(
                    generatedRoadAllowanceIds,
                    generatedSectionIds,
                    generatedContextSectionIds);
                candidates = CollectBoundaryCandidates(scratchDb, frame, generatedMeasurementIds);
                if (candidates.Count == 0)
                {
                    detail =
                        $"The ATS scratch build completed but did not produce any usable generated `L-USEC-0`, `L-USEC-2012`, or `L-SEC` measurement boundary candidates for this section (generated IDs: {generatedRoadAllowanceIds.Count} road-allowance/correction, {generatedSectionIds.Count} section, {generatedContextSectionIds.Count} context-section). Refusing to scan original scratch linework.";
                    return false;
                }

                detail =
                    $"Derived {candidates.Count} ATS measurement boundary candidate segment(s) from generated ATS Builder entity IDs only after unlocking {unlockedScratchLayers} scratch layer(s) and removing {removedScratchEntities} pre-existing ATS helper entity(ies) from the scratch database ({generatedRoadAllowanceIds.Count} road-allowance/correction IDs, {generatedSectionIds.Count} section IDs, {generatedContextSectionIds.Count} context-section IDs; no original scratch linework scan; {assemblySource}).";
                return true;
            }
        }
        catch (TargetInvocationException ex)
        {
            var inner = ex.InnerException;
            detail = inner == null
                ? $"ATS scratch section build failed: {ex.Message}"
                : $"ATS scratch section build failed: {inner.GetType().Name}: {inner.Message}";
            return false;
        }
        catch (System.Exception ex)
        {
            detail = $"ATS scratch section build failed: {ex.Message}";
            return false;
        }
        finally
        {
            if (logger is IDisposable disposableLogger)
            {
                disposableLogger.Dispose();
            }
        }
    }

    private bool TryLoadSectionFrame(BuildDrillPointRequest request, string? drawingPath, out SectionFrame frame)
    {
        frame = default;
        var logger = new Logger();
        var key = new SectionKey(
            request.Zone,
            request.Section,
            request.Township,
            request.Range,
            request.Meridian);

        foreach (var folder in BuildSectionIndexSearchFolders(drawingPath))
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                continue;
            }

            if (!SectionIndexReader.TryLoadSectionOutline(folder, key, logger, out var outline) || outline == null)
            {
                continue;
            }

            if (!AtsPolygonFrameBuilder.TryBuildFrame(outline.Vertices, out var southWest, out var eastUnit, out var northUnit, out var width, out var height))
            {
                continue;
            }

            frame = new SectionFrame(
                southWest,
                southWest + (eastUnit * width),
                southWest + (northUnit * height),
                southWest + (eastUnit * width) + (northUnit * height),
                eastUnit.GetNormal(),
                northUnit.GetNormal(),
                width,
                height);
            return true;
        }

        return false;
    }

    private static bool TryLoadNearestCordsSectionFrame(
        int zone,
        string? drawingPath,
        Point2d point,
        out CordsSectionFrame section,
        out string detail)
    {
        section = default;
        detail = string.Empty;
        var logger = new Logger();
        var searchedFolders = 0;
        var consideredSections = 0;
        var bestOutsideDistance = double.MaxValue;
        var bestInsideMargin = double.NegativeInfinity;

        foreach (var folder in BuildSectionIndexSearchFolders(drawingPath))
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            {
                continue;
            }

            searchedFolders++;
            if (!SectionIndexReader.TryLoadSectionOutlinesForZone(folder, zone, logger, out var outlines) ||
                outlines.Count == 0)
            {
                continue;
            }

            foreach (var outline in outlines)
            {
                if (!TryBuildCordsSectionFrame(outline, out var candidate))
                {
                    continue;
                }

                consideredSections++;
                var outsideDistance = MeasureFrameOutsideDistance(candidate.Frame, point, out var insideMargin);
                if (outsideDistance > CordsSectionSearchDistance)
                {
                    continue;
                }

                if (outsideDistance < bestOutsideDistance - 1e-6 ||
                    (Math.Abs(outsideDistance - bestOutsideDistance) <= 1e-6 && insideMargin > bestInsideMargin))
                {
                    section = candidate;
                    bestOutsideDistance = outsideDistance;
                    bestInsideMargin = insideMargin;
                }
            }

            if (bestOutsideDistance < double.MaxValue)
            {
                detail =
                    $"Nearest section {section.Key.Section}-{section.Key.Township}-{section.Key.Range}-W{section.Key.Meridian} was {bestOutsideDistance:0.###}m from the section frame in {folder}.";
                return true;
            }
        }

        detail =
            $"No section index outline in zone {zone} was found within {CordsSectionSearchDistance:0.#}m of the CORDS point (folders searched: {searchedFolders}, sections considered: {consideredSections}).";
        return false;
    }

    private static bool TryBuildCordsSectionFrame(
        SectionIndexReader.SectionOutlineEntry entry,
        out CordsSectionFrame section)
    {
        section = default;
        if (entry.Outline.Vertices.Count < 3 ||
            !AtsPolygonFrameBuilder.TryBuildFrame(entry.Outline.Vertices, out var southWest, out var eastUnit, out var northUnit, out var width, out var height))
        {
            return false;
        }

        var frame = new SectionFrame(
            southWest,
            southWest + (eastUnit * width),
            southWest + (northUnit * height),
            southWest + (eastUnit * width) + (northUnit * height),
            eastUnit.GetNormal(),
            northUnit.GetNormal(),
            width,
            height);
        section = new CordsSectionFrame(entry.Key, frame);
        return true;
    }

    private static double MeasureFrameOutsideDistance(SectionFrame frame, Point2d point, out double insideMargin)
    {
        var local = point - frame.SouthWest;
        var easting = ProjectAlongAxis(local, frame.EastUnit);
        var northing = ProjectAlongAxis(local, frame.NorthUnit);

        var outsideEasting = easting < 0.0 ? -easting : easting > frame.Width ? easting - frame.Width : 0.0;
        var outsideNorthing = northing < 0.0 ? -northing : northing > frame.Height ? northing - frame.Height : 0.0;
        var outsideDistance = Math.Sqrt((outsideEasting * outsideEasting) + (outsideNorthing * outsideNorthing));

        insideMargin = outsideDistance <= 1e-6
            ? Math.Min(Math.Min(easting, frame.Width - easting), Math.Min(northing, frame.Height - northing))
            : -outsideDistance;
        return outsideDistance;
    }

    private static bool TryBuildNativeCordsAtsMatch(
        SectionKey key,
        SectionFrame frame,
        Point2d point,
        IReadOnlyList<StraightBoundary> candidates,
        out AtsQuarterLocationResolver.LsdMatch match,
        out string detail)
    {
        match = default;
        detail = string.Empty;
        if (!TryResolveCordsAtsOffsets(
                frame,
                point,
                candidates,
                out var metes,
                out var bounds,
                out detail))
        {
            return false;
        }

        var quarter = ResolveCordsQuarterBounds(frame, point);
        var lsd = ResolveCordsLsdNumber(frame, point);
        var location = $"{lsd}-{key.Section}-{key.Township}-{key.Range}-W{key.Meridian}";

        match = new AtsQuarterLocationResolver.LsdMatch(
            location,
            lsd,
            quarter.Token,
            key.Section,
            key.Township,
            key.Range,
            key.Meridian,
            metes,
            bounds,
            BuildCordsQuarterVertices(frame, quarter));

        return true;
    }

    private static bool TryResolveCordsAtsOffsets(
        SectionFrame frame,
        Point2d point,
        IReadOnlyList<StraightBoundary> candidates,
        out string metes,
        out string bounds,
        out string detail)
    {
        metes = string.Empty;
        bounds = string.Empty;
        detail = string.Empty;
        var allCandidates = new List<StraightBoundary>(candidates.Count);
        allCandidates.AddRange(candidates);

        var metesSide = ResolveCordsNorthSouthSide(frame, point);
        if (!TryFindCordsBoundaryIntersection(
                frame,
                allCandidates,
                point,
                metesSide,
                out var metesIntersection,
                out var metesBoundary,
                out var metesDetail))
        {
            detail = metesDetail;
            return false;
        }

        var boundsSide = ResolveCordsEastWestSide(frame, point);
        if (!TryFindCordsBoundaryIntersection(
                frame,
                allCandidates,
                point,
                boundsSide,
                out var boundsIntersection,
                out var boundsBoundary,
                out var boundsDetail))
        {
            detail = boundsDetail;
            return false;
        }

        metes = FormatCordsOffset(point, metesIntersection, frame.NorthUnit, "N", "S");
        bounds = FormatCordsOffset(point, boundsIntersection, frame.EastUnit, "E", "W");
        detail =
            $"CORDS offsets measured from ATS measurement fabric ({metesSide}: {metesBoundary.Layer}, {boundsSide}: {boundsBoundary.Layer}; {metesDetail} {boundsDetail})";
        return true;
    }

    private static BoundarySide ResolveCordsNorthSouthSide(SectionFrame frame, Point2d point)
    {
        var northing = ProjectAlongAxis(point - frame.SouthWest, frame.NorthUnit);
        if (northing <= 0.0)
        {
            return BoundarySide.South;
        }

        if (northing >= frame.Height)
        {
            return BoundarySide.North;
        }

        return northing <= frame.Height - northing ? BoundarySide.South : BoundarySide.North;
    }

    private static BoundarySide ResolveCordsEastWestSide(SectionFrame frame, Point2d point)
    {
        var easting = ProjectAlongAxis(point - frame.SouthWest, frame.EastUnit);
        if (easting <= 0.0)
        {
            return BoundarySide.West;
        }

        if (easting >= frame.Width)
        {
            return BoundarySide.East;
        }

        return easting <= frame.Width - easting ? BoundarySide.West : BoundarySide.East;
    }

    private static bool TryFindCordsBoundaryIntersection(
        SectionFrame frame,
        IReadOnlyList<StraightBoundary> candidates,
        Point2d probePoint,
        BoundarySide side,
        out Point2d intersection,
        out StraightBoundary boundary,
        out string detail)
    {
        intersection = default;
        boundary = default;
        detail = string.Empty;

        var sideAxis = side is BoundarySide.South or BoundarySide.North ? frame.NorthUnit : frame.EastUnit;
        var boundaryAxis = side is BoundarySide.South or BoundarySide.North ? frame.EastUnit : frame.NorthUnit;
        var probeAxis = side is BoundarySide.South or BoundarySide.North ? frame.NorthUnit : frame.EastUnit;

        var bestPriority = int.MaxValue;
        var bestExtensionDistance = double.MaxValue;
        var bestOffsetDistance = double.MaxValue;
        var sameSideParallelMatches = 0;
        var boundedCrossings = 0;

        foreach (var candidate in candidates)
        {
            if (candidate.Side != side || !IsBoundaryParallelToAxis(candidate, boundaryAxis))
            {
                continue;
            }

            sameSideParallelMatches++;
            if (!TryIntersectProbeWithBoundaryCandidate(
                    probePoint,
                    probeAxis,
                    candidate,
                    out var candidateIntersection,
                    out var extensionDistance))
            {
                continue;
            }

            boundedCrossings++;
            var priority = GetBoundaryLayerPriority(side, candidate.Layer);
            var offsetDistance = Math.Abs(ProjectAlongAxis(probePoint - candidateIntersection, sideAxis));
            if (offsetDistance < bestOffsetDistance - 1e-6 ||
                (Math.Abs(offsetDistance - bestOffsetDistance) <= 1e-6 && priority < bestPriority) ||
                (Math.Abs(offsetDistance - bestOffsetDistance) <= 1e-6 &&
                 priority == bestPriority &&
                 extensionDistance < bestExtensionDistance))
            {
                bestPriority = priority;
                bestExtensionDistance = extensionDistance;
                bestOffsetDistance = offsetDistance;
                intersection = candidateIntersection;
                boundary = new StraightBoundary(side, candidate.OrderedStart(side), candidate.OrderedEnd(side), candidate.Layer);
            }
        }

        if (bestPriority < int.MaxValue)
        {
            detail = bestExtensionDistance <= BoundaryProbeSegmentTolerance
                ? $"{side} CORDS boundary measured from {boundary.Layer}."
                : $"{side} CORDS boundary measured from {boundary.Layer} using {bestExtensionDistance:0.###}m bounded extension.";
            return true;
        }

        var projectionLimit = Math.Max(frame.Width, frame.Height) + BoundaryProbeExtensionTolerance;
        bestPriority = int.MaxValue;
        bestExtensionDistance = double.MaxValue;
        bestOffsetDistance = double.MaxValue;
        var projectedCrossings = 0;

        foreach (var candidate in candidates)
        {
            if (candidate.Side != side || !IsBoundaryParallelToAxis(candidate, boundaryAxis))
            {
                continue;
            }

            if (!TryIntersectProjectedProbeWithBoundaryCandidate(
                    probePoint,
                    probeAxis,
                    candidate,
                    projectionLimit,
                    out var candidateIntersection,
                    out var extensionDistance))
            {
                continue;
            }

            projectedCrossings++;
            var priority = GetBoundaryLayerPriority(side, candidate.Layer);
            var offsetDistance = Math.Abs(ProjectAlongAxis(probePoint - candidateIntersection, sideAxis));
            if (offsetDistance < bestOffsetDistance - 1e-6 ||
                (Math.Abs(offsetDistance - bestOffsetDistance) <= 1e-6 && priority < bestPriority) ||
                (Math.Abs(offsetDistance - bestOffsetDistance) <= 1e-6 &&
                 priority == bestPriority &&
                 extensionDistance < bestExtensionDistance))
            {
                bestPriority = priority;
                bestExtensionDistance = extensionDistance;
                bestOffsetDistance = offsetDistance;
                intersection = candidateIntersection;
                boundary = new StraightBoundary(side, candidate.OrderedStart(side), candidate.OrderedEnd(side), candidate.Layer);
            }
        }

        if (bestPriority < int.MaxValue)
        {
            detail = $"{side} CORDS boundary measured from projected {boundary.Layer} using {bestExtensionDistance:0.###}m extension.";
            return true;
        }

        detail =
            $"Could not find a {side.ToString().ToLowerInvariant()} ATS hard boundary crossing the CORDS point probe line (same-side parallel candidates: {sameSideParallelMatches}, bounded crossings: {boundedCrossings}, projected crossings: {projectedCrossings}, total candidates: {candidates.Count}).";
        return false;
    }

    private static string FormatCordsOffset(Point2d point, Point2d boundaryIntersection, Vector2d axis, string positiveDirection, string negativeDirection)
    {
        var signedDistance = ProjectAlongAxis(point - boundaryIntersection, axis);
        var direction = signedDistance >= 0.0 ? positiveDirection : negativeDirection;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Math.Abs(signedDistance):0.0} {direction}");
    }

    private static CordsQuarterBounds ResolveCordsQuarterBounds(SectionFrame frame, Point2d point)
    {
        var (u, t) = ResolveCordsFractions(frame, point);
        var east = u >= 0.5;
        var north = t >= 0.5;
        if (north && !east) return new CordsQuarterBounds("NW", 0.0, 0.5, 0.5, 1.0);
        if (north && east) return new CordsQuarterBounds("NE", 0.5, 1.0, 0.5, 1.0);
        if (!north && !east) return new CordsQuarterBounds("SW", 0.0, 0.5, 0.0, 0.5);
        return new CordsQuarterBounds("SE", 0.5, 1.0, 0.0, 0.5);
    }

    private static int ResolveCordsLsdNumber(SectionFrame frame, Point2d point)
    {
        var (u, t) = ResolveCordsFractions(frame, point);
        var col = (int)Math.Floor(u * 4.0);
        var row = (int)Math.Floor(t * 4.0);
        col = Math.Max(0, Math.Min(3, col));
        row = Math.Max(0, Math.Min(3, row));
        return LsdNumberingHelper.GetLsdNumber(row, col);
    }

    private static (double U, double T) ResolveCordsFractions(SectionFrame frame, Point2d point)
    {
        var vector = point - frame.SouthWest;
        var u = ProjectAlongAxis(vector, frame.EastUnit) / frame.Width;
        var t = ProjectAlongAxis(vector, frame.NorthUnit) / frame.Height;
        return (ClampCordsFraction(u), ClampCordsFraction(t));
    }

    private static double ClampCordsFraction(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0.0;
        }

        return Math.Max(0.0, Math.Min(0.999999, value));
    }

    private static IReadOnlyList<Point2d> BuildCordsQuarterVertices(SectionFrame frame, CordsQuarterBounds quarter)
    {
        return new[]
        {
            CordsLocalToWorld(frame, quarter.UMin, quarter.TMin),
            CordsLocalToWorld(frame, quarter.UMax, quarter.TMin),
            CordsLocalToWorld(frame, quarter.UMax, quarter.TMax),
            CordsLocalToWorld(frame, quarter.UMin, quarter.TMax)
        };
    }

    private static Point2d CordsLocalToWorld(SectionFrame frame, double u, double t)
    {
        var localOffset = (frame.EastUnit * (u * frame.Width)) + (frame.NorthUnit * (t * frame.Height));
        return frame.SouthWest + localOffset;
    }

    private static IReadOnlyList<string> BuildSectionIndexSearchFolders(string? drawingPath)
    {
        var folders = new List<string>();
        AddFolder(folders, Environment.GetEnvironmentVariable("COMPASS_SECTION_INDEX_FOLDER"));
        AddFolder(folders, Environment.GetEnvironmentVariable("WLS_SECTION_INDEX_FOLDER"));
        AddFolder(folders, Environment.GetEnvironmentVariable("ATSBUILD_SECTION_INDEX_FOLDER"));
        AddFolder(folders, Environment.GetEnvironmentVariable("ATS_SECTION_INDEX_FOLDER"));
        AddFolder(folders, TryGetDrawingFolder(drawingPath));
        AddFolder(folders, Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
        AddFolder(folders, Environment.CurrentDirectory);
        AddFolder(folders, DefaultSectionIndexFolder);
        return folders;
    }

    private static string? TryGetDrawingFolder(string? drawingPath)
    {
        if (string.IsNullOrWhiteSpace(drawingPath))
        {
            return null;
        }

        try
        {
            return Path.GetDirectoryName(drawingPath);
        }
        catch
        {
            return null;
        }
    }

    private static void AddFolder(List<string> folders, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        var trimmed = folder.Trim();
        if (!folders.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
        {
            folders.Add(trimmed);
        }
    }

    private static List<StraightBoundary> CollectBoundaryCandidates(Database database, SectionFrame frame, IReadOnlyCollection<ObjectId>? candidateIds)
    {
        var candidates = new List<StraightBoundary>();
        if (database == null)
        {
            return candidates;
        }

        using var transaction = database.TransactionManager.StartTransaction();

        if (candidateIds != null && candidateIds.Count > 0)
        {
            foreach (var candidateId in candidateIds)
            {
                TryAddBoundaryCandidate(transaction, candidateId, frame, candidates);
            }
        }
        else
        {
            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            if (blockTable.Has(BlockTableRecord.ModelSpace))
            {
                var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId entityId in modelSpace)
                {
                    TryAddBoundaryCandidate(transaction, entityId, frame, candidates);
                }
            }
        }

        transaction.Commit();
        return candidates;
    }

    private static void TryAddBoundaryCandidate(
        Transaction transaction,
        ObjectId entityId,
        SectionFrame frame,
        List<StraightBoundary> candidates)
    {
        if (entityId.IsNull ||
            transaction.GetObject(entityId, OpenMode.ForRead, false) is not Curve curve ||
            curve.IsErased)
        {
            return;
        }

        if (!TryCreateStraightBoundary(curve, out var boundary))
        {
            return;
        }

        if (!TryNormalizeBoundaryCandidate(frame, boundary, curve.Layer ?? string.Empty, out var normalized))
        {
            return;
        }

        candidates.Add(normalized);
    }

    private static bool TryNormalizeBoundaryCandidate(
        SectionFrame frame,
        StraightBoundary boundary,
        string layer,
        out StraightBoundary normalized)
    {
        normalized = default;
        if (!TryClassifyBoundarySide(frame, boundary, out var side, out var sideDistance))
        {
            return false;
        }

        string normalizedLayer;
        if (IsHardBoundaryLayer(layer))
        {
            normalizedLayer = layer;
        }
        else if (string.Equals(layer, "L-USEC", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryClassifyVisibleBoundaryFamily(sideDistance, out normalizedLayer))
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        normalized = new StraightBoundary(side, boundary.OrderedStart(side), boundary.OrderedEnd(side), normalizedLayer);
        return true;
    }

    private static bool TryClassifyBoundarySide(
        SectionFrame frame,
        StraightBoundary boundary,
        out BoundarySide side,
        out double sideDistance)
    {
        side = default;
        sideDistance = double.MaxValue;
        foreach (var anchor in EnumerateFrameBoundaries(frame))
        {
            if (!boundary.IsParallelTo(anchor))
            {
                continue;
            }

            var candidateDistance = boundary.DistanceToPoint(anchor.Midpoint);
            if (candidateDistance > AtsBoundarySearchDistance || candidateDistance >= sideDistance)
            {
                continue;
            }

            side = anchor.Side;
            sideDistance = candidateDistance;
        }

        return sideDistance < double.MaxValue;
    }

    private static IEnumerable<StraightBoundary> EnumerateFrameBoundaries(SectionFrame frame)
    {
        var boundaries = CreateFrameBoundaries(frame);
        yield return boundaries.South;
        yield return boundaries.North;
        yield return boundaries.West;
        yield return boundaries.East;
    }

    private static bool TryClassifyVisibleBoundaryFamily(double sideDistance, out string layer)
    {
        if (sideDistance <= VisibleBoundaryFamilyTolerance)
        {
            layer = "L-USEC-0";
            return true;
        }

        if (Math.Abs(sideDistance - 20.12) <= VisibleBoundaryFamilyTolerance)
        {
            layer = "L-USEC2012";
            return true;
        }

        layer = string.Empty;
        return false;
    }

    private static bool IsHardBoundaryLayer(string layer)
    {
        return IsCorrectionUsecZeroLayer(layer) ||
               IsActualUsecZeroLayer(layer) ||
               IsUsecTwentyLayer(layer) ||
               IsSectionBoundaryLayer(layer);
    }

    private static List<StraightBoundary> MergeBoundaryCandidates(params IReadOnlyList<StraightBoundary>[] sets)
    {
        var merged = new List<StraightBoundary>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var set in sets)
        {
            if (set == null)
            {
                continue;
            }

            foreach (var candidate in set)
            {
                var key = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{candidate.Side}|{candidate.Layer}|{candidate.Start.X:0.###}|{candidate.Start.Y:0.###}|{candidate.End.X:0.###}|{candidate.End.Y:0.###}");
                if (seen.Add(key))
                {
                    merged.Add(candidate);
                }
            }
        }

        return merged;
    }

    private static bool TryOpenFileBackedScratchDatabase(
        Database sourceDatabase,
        out Database? scratchDatabase,
        out string detail)
    {
        scratchDatabase = null;
        detail = string.Empty;
        if (sourceDatabase == null)
        {
            detail = "The active drawing database was unavailable for ATS hard-boundary generation.";
            return false;
        }

        var sourcePath = sourceDatabase.Filename;
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
        {
            detail = "Build a Drill needs the current drawing saved before ATS can open a file-backed scratch database.";
            return false;
        }

        try
        {
            scratchDatabase = new Database(false, true);
            scratchDatabase.ReadDwgFile(sourcePath, FileOpenMode.OpenForReadAndAllShare, allowCPConversion: false, password: null);
            scratchDatabase.CloseInput(true);
            return true;
        }
        catch (System.Exception ex)
        {
            scratchDatabase?.Dispose();
            scratchDatabase = null;
            detail = $"Failed to open a file-backed ATS scratch database from '{sourcePath}': {ex.Message}";
            return false;
        }
    }

    private static int UnlockScratchLayers(Database database)
    {
        if (database == null)
        {
            return 0;
        }

        var unlocked = 0;
        using var transaction = database.TransactionManager.StartTransaction();
        var layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
        foreach (ObjectId layerId in layerTable)
        {
            if (transaction.GetObject(layerId, OpenMode.ForRead, false) is not LayerTableRecord layer ||
                layer.IsErased ||
                !layer.IsLocked)
            {
                continue;
            }

            layer.UpgradeOpen();
            layer.IsLocked = false;
            unlocked++;
        }

        transaction.Commit();
        return unlocked;
    }

    private static int RemoveScratchAtsLinework(Database database)
    {
        if (database == null)
        {
            return 0;
        }

        var removed = 0;
        using var transaction = database.TransactionManager.StartTransaction();
        var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        if (!blockTable.Has(BlockTableRecord.ModelSpace))
        {
            return 0;
        }

        var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
        foreach (ObjectId entityId in modelSpace)
        {
            if (transaction.GetObject(entityId, OpenMode.ForWrite, false) is not Entity entity ||
                entity.IsErased)
            {
                continue;
            }

            if (!AtsScratchIsolationLayers.Contains(entity.Layer ?? string.Empty))
            {
                continue;
            }

            entity.Erase();
            removed++;
        }

        transaction.Commit();
        return removed;
    }

    private static List<ObjectId> GetObjectIdsFromReflectionProperty(object source, string propertyName)
    {
        var ids = new List<ObjectId>();
        if (source == null || string.IsNullOrWhiteSpace(propertyName))
        {
            return ids;
        }

        var property = source.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property?.GetValue(source) is not IEnumerable enumerable)
        {
            return ids;
        }

        foreach (var item in enumerable)
        {
            if (item is ObjectId objectId && !objectId.IsNull)
            {
                ids.Add(objectId);
            }
        }

        return ids;
    }

    private static List<ObjectId> MergeObjectIds(params IReadOnlyList<ObjectId>[] sets)
    {
        var ids = new List<ObjectId>();
        var seen = new HashSet<ObjectId>();
        foreach (var set in sets)
        {
            if (set == null)
            {
                continue;
            }

            foreach (var id in set)
            {
                if (id.IsNull || !seen.Add(id))
                {
                    continue;
                }

                ids.Add(id);
            }
        }

        return ids;
    }

    private static bool TryLoadAtsBackgroundBuilderAssembly(Database database, out Assembly atsAssembly, out string source)
    {
        atsAssembly = AppDomain.CurrentDomain
            .GetAssemblies()
            .FirstOrDefault(assembly =>
                string.Equals(assembly.GetName().Name, "AtsBackgroundBuilder", StringComparison.OrdinalIgnoreCase));
        if (atsAssembly != null)
        {
            source = string.IsNullOrWhiteSpace(atsAssembly.Location) ? "already loaded assembly" : atsAssembly.Location;
            return true;
        }

        var candidates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static void AddCandidate(List<string> target, HashSet<string> seenPaths, string? baseFolder, string fileName = "AtsBackgroundBuilder.dll")
        {
            if (string.IsNullOrWhiteSpace(baseFolder))
            {
                return;
            }

            try
            {
                var fullPath = Path.GetFullPath(Path.Combine(baseFolder, fileName));
                if (seenPaths.Add(fullPath))
                {
                    target.Add(fullPath);
                }
            }
            catch
            {
                // Ignore invalid probe paths.
            }
        }

        var assemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        var probe = assemblyDirectory;
        for (var i = 0; i < 8 && !string.IsNullOrWhiteSpace(probe); i++)
        {
            AddCandidate(candidates, seen, probe);
            AddCandidate(candidates, seen, Path.Combine(probe, "ATSBUILD_MANUAL"));
            AddCandidate(candidates, seen, Path.Combine(probe, "build", "net8.0-windows"));
            probe = Path.GetDirectoryName(probe);
        }

        AddCandidate(candidates, seen, AppContext.BaseDirectory);
        AddCandidate(candidates, seen, Environment.CurrentDirectory);
        AddCandidate(candidates, seen, SafeGetDirectoryName(database?.Filename));

        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
            {
                continue;
            }

            try
            {
                atsAssembly = Assembly.LoadFrom(candidate);
                source = candidate;
                return true;
            }
            catch
            {
                // Keep probing.
            }
        }

        source = string.Empty;
        return false;
    }

    private static string ResolveSectionIndexFolderForAtsBuild(Database database, string? drawingPath)
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("COMPASS_SECTION_INDEX_FOLDER"),
            Environment.GetEnvironmentVariable("WLS_SECTION_INDEX_FOLDER"),
            Environment.GetEnvironmentVariable("ATSBUILD_SECTION_INDEX_FOLDER"),
            Environment.GetEnvironmentVariable("ATS_SECTION_INDEX_FOLDER"),
            SafeGetDirectoryName(drawingPath),
            SafeGetDirectoryName(database?.Filename),
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
            Environment.CurrentDirectory,
            DefaultSectionIndexFolder
        };

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return DefaultSectionIndexFolder;
    }

    private static string SafeGetDirectoryName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetDirectoryName(path) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool SetReflectionProperty(object target, string propertyName, object value)
    {
        if (target == null || string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        var property = target.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
        if (property == null || !property.CanWrite)
        {
            return false;
        }

        try
        {
            var valueType = value?.GetType();
            if (valueType == null || property.PropertyType.IsAssignableFrom(valueType))
            {
                property.SetValue(target, value);
                return true;
            }

            var converted = Convert.ChangeType(value, property.PropertyType, CultureInfo.InvariantCulture);
            property.SetValue(target, converted);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ValidateDistancesAgainstFrame(BuildDrillPointRequest request, SectionFrame frame)
    {
        var northSouthDistance = GetScaledOffsetDistance(request.NorthSouthDistance, request.CombinedScaleFactor);
        var eastWestDistance = GetScaledOffsetDistance(request.EastWestDistance, request.CombinedScaleFactor);

        if (northSouthDistance > frame.Height + 1e-6)
        {
            throw new InvalidOperationException(
                $"North/south distance {northSouthDistance:0.###} is larger than the section height ({frame.Height:0.###}) after CSF scaling.");
        }

        if (eastWestDistance > frame.Width + 1e-6)
        {
            throw new InvalidOperationException(
                $"East/west distance {eastWestDistance:0.###} is larger than the section width ({frame.Width:0.###}) after CSF scaling.");
        }
    }

    private static double GetScaledOffsetDistance(double distance, double combinedScaleFactor)
    {
        if (distance <= 0.0 || combinedScaleFactor <= 0.0)
        {
            return distance;
        }

        return distance * combinedScaleFactor;
    }

    private static BoundarySet CreateFrameBoundaries(SectionFrame frame)
    {
        return new BoundarySet(
            new StraightBoundary(BoundarySide.South, frame.SouthWest, frame.SouthEast, "SECTION-INDEX"),
            new StraightBoundary(BoundarySide.North, frame.NorthWest, frame.NorthEast, "SECTION-INDEX"),
            new StraightBoundary(BoundarySide.West, frame.SouthWest, frame.NorthWest, "SECTION-INDEX"),
            new StraightBoundary(BoundarySide.East, frame.SouthEast, frame.NorthEast, "SECTION-INDEX"));
    }

    private static BoundarySet ResolveReferenceBoundariesForPoint(
        SectionFrame frame,
        BuildDrillPointRequest request,
        IReadOnlyList<StraightBoundary> candidates,
        BoundarySet fallback)
    {
        var provisional = BuildRawOffsetPoint(frame, request);
        var provisional2d = new Point2d(provisional.X, provisional.Y);
        var allCandidates = MergeBoundaryCandidates(candidates, CreateOuterSectionIndexZeroCandidates(frame));

        var south = TryResolveReferenceBoundary(frame, provisional2d, allCandidates, BoundarySide.South, fallback.South);
        var north = TryResolveReferenceBoundary(frame, provisional2d, allCandidates, BoundarySide.North, fallback.North);
        var west = TryResolveReferenceBoundary(frame, provisional2d, allCandidates, BoundarySide.West, fallback.West);
        var east = TryResolveReferenceBoundary(frame, provisional2d, allCandidates, BoundarySide.East, fallback.East);

        return new BoundarySet(south, north, west, east);
    }

    private static StraightBoundary TryResolveReferenceBoundary(
        SectionFrame frame,
        Point2d probePoint,
        IReadOnlyList<StraightBoundary> candidates,
        BoundarySide side,
        StraightBoundary fallback)
    {
        return TryFindBoundaryProbeIntersection(frame, candidates, probePoint, side, out _, out var boundary, out _)
            ? boundary
            : fallback;
    }

    private static bool TryResolveCalculatedHardBoundariesForPoint(
        SectionFrame frame,
        BuildDrillPointRequest request,
        IReadOnlyList<StraightBoundary> candidates,
        BoundarySet placeholders,
        out BoundarySet boundaries,
        out string detail)
    {
        boundaries = placeholders;
        detail = string.Empty;
        if (candidates == null || candidates.Count == 0)
        {
            detail = "No calculated 0 / 20.12 hard-boundary candidates were available.";
            return false;
        }

        var provisional = BuildRawOffsetPoint(frame, request);
        var provisional2d = new Point2d(provisional.X, provisional.Y);
        var calculatedCandidates = candidates.Concat(CreateOuterSectionIndexZeroCandidates(frame)).ToList();
        var south = placeholders.South;
        var north = placeholders.North;
        var west = placeholders.West;
        var east = placeholders.East;
        var resolvedSides = new List<string>(2);

        if (request.NorthSouthReference == BuildDrillNorthSouthReference.NorthOfSouth)
        {
            if (!TryFindBoundaryProbeIntersection(frame, calculatedCandidates, provisional2d, BoundarySide.South, out _, out south, out var southDetail))
            {
                detail = southDetail;
                return false;
            }

            resolvedSides.Add($"S:{south.Layer}");
        }
        else
        {
            if (!TryFindBoundaryProbeIntersection(frame, calculatedCandidates, provisional2d, BoundarySide.North, out _, out north, out var northDetail))
            {
                detail = northDetail;
                return false;
            }

            resolvedSides.Add($"N:{north.Layer}");
        }

        if (request.EastWestReference == BuildDrillEastWestReference.EastOfWest)
        {
            if (!TryFindBoundaryProbeIntersection(frame, calculatedCandidates, provisional2d, BoundarySide.West, out _, out west, out var westDetail))
            {
                detail = westDetail;
                return false;
            }

            resolvedSides.Add($"W:{west.Layer}");
        }
        else
        {
            if (!TryFindBoundaryProbeIntersection(frame, calculatedCandidates, provisional2d, BoundarySide.East, out _, out east, out var eastDetail))
            {
                detail = eastDetail;
                return false;
            }

            resolvedSides.Add($"E:{east.Layer}");
        }

        boundaries = new BoundarySet(south, north, west, east);
        detail = $"Calculated hard ATS boundaries resolved from {string.Join(", ", resolvedSides)}.";
        return true;
    }

    private static bool UsesHorizontalBoundaryForRequestedSides(BuildDrillPointRequest request, BoundarySet boundaries)
    {
        var northSouthBoundary = request.NorthSouthReference == BuildDrillNorthSouthReference.NorthOfSouth
            ? boundaries.South
            : boundaries.North;
        var eastWestBoundary = request.EastWestReference == BuildDrillEastWestReference.EastOfWest
            ? boundaries.West
            : boundaries.East;

        return string.Equals(northSouthBoundary.Layer, HorizontalBoundaryLayer, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(eastWestBoundary.Layer, HorizontalBoundaryLayer, StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeNonAtsBoundaryResolution(
        BuildDrillPointRequest request,
        BoundarySet references,
        BoundarySet resolved)
    {
        var parts = new List<string>(2);
        if (request.NorthSouthReference == BuildDrillNorthSouthReference.NorthOfSouth)
        {
            parts.Add(DescribeNonAtsBoundaryResolution("S", references.South, resolved.South));
        }
        else
        {
            parts.Add(DescribeNonAtsBoundaryResolution("N", references.North, resolved.North));
        }

        if (request.EastWestReference == BuildDrillEastWestReference.EastOfWest)
        {
            parts.Add(DescribeNonAtsBoundaryResolution("W", references.West, resolved.West));
        }
        else
        {
            parts.Add(DescribeNonAtsBoundaryResolution("E", references.East, resolved.East));
        }

        return $"Resolved ATS-off side selection from {string.Join(", ", parts)}.";
    }

    private static string DescribeNonAtsBoundaryResolution(string prefix, StraightBoundary reference, StraightBoundary resolved)
    {
        if (string.Equals(resolved.Layer, HorizontalBoundaryLayer, StringComparison.OrdinalIgnoreCase))
        {
            return $"{prefix}:{resolved.Layer} (ref {reference.Layer})";
        }

        return $"{prefix}:{resolved.Layer}";
    }

    private static BoundarySearchResult ResolveNearestBoundaries(
        Database database,
        SectionFrame frame,
        Point2d probePoint,
        BoundarySet anchors,
        IReadOnlyCollection<string> layers,
        double maxDistance)
    {
        var normalizedLayers = new HashSet<string>(layers, StringComparer.OrdinalIgnoreCase);
        var candidates = new List<StraightBoundary>();

        using (var transaction = database.TransactionManager.StartTransaction())
        {
            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId id in modelSpace)
            {
                if (transaction.GetObject(id, OpenMode.ForRead) is not Curve curve)
                {
                    continue;
                }

                if (!normalizedLayers.Contains(curve.Layer))
                {
                    continue;
                }

                if (!TryCreateStraightBoundary(curve, out var candidate))
                {
                    continue;
                }

                candidates.Add(candidate);
            }

            transaction.Commit();
        }

        var south = FindBoundary(frame, probePoint, anchors.South, candidates, maxDistance, out var southFallback);
        var north = FindBoundary(frame, probePoint, anchors.North, candidates, maxDistance, out var northFallback);
        var west = FindBoundary(frame, probePoint, anchors.West, candidates, maxDistance, out var westFallback);
        var east = FindBoundary(frame, probePoint, anchors.East, candidates, maxDistance, out var eastFallback);

        return new BoundarySearchResult(
            new BoundarySet(south, north, west, east),
            southFallback || northFallback || westFallback || eastFallback);
    }

    private static StraightBoundary FindBoundary(
        SectionFrame frame,
        Point2d probePoint,
        StraightBoundary anchor,
        IReadOnlyList<StraightBoundary> candidates,
        double maxDistance,
        out bool usedFallback)
    {
        usedFallback = true;

        StraightBoundary? best = null;
        var bestAnchorGap = double.MaxValue;
        var bestExtensionDistance = double.MaxValue;
        var bestProbeGap = double.MaxValue;
        var sideAxis = anchor.Side is BoundarySide.South or BoundarySide.North ? frame.NorthUnit : frame.EastUnit;
        var boundaryAxis = anchor.Side is BoundarySide.South or BoundarySide.North ? frame.EastUnit : frame.NorthUnit;
        var probeAxis = anchor.Side is BoundarySide.South or BoundarySide.North ? frame.NorthUnit : frame.EastUnit;
        var probeCoordinate = ProjectAlongAxis(probePoint - frame.SouthWest, sideAxis);
        var anchorIntersection = IntersectProbeWithBoundary(probePoint, probeAxis, anchor);
        var anchorCoordinate = ProjectAlongAxis(anchorIntersection - frame.SouthWest, sideAxis);

        foreach (var candidate in candidates)
        {
            if (!IsBoundaryParallelToAxis(candidate, boundaryAxis))
            {
                continue;
            }

            if (!TryIntersectProbeWithBoundaryCandidate(
                    probePoint,
                    probeAxis,
                    candidate,
                    out var candidateIntersection,
                    out var extensionDistance))
            {
                continue;
            }

            var candidateCoordinate = ProjectAlongAxis(candidateIntersection - frame.SouthWest, sideAxis);
            var probeGap = anchor.Side switch
            {
                BoundarySide.South => probeCoordinate - candidateCoordinate,
                BoundarySide.North => candidateCoordinate - probeCoordinate,
                BoundarySide.West => probeCoordinate - candidateCoordinate,
                BoundarySide.East => candidateCoordinate - probeCoordinate,
                _ => double.MaxValue
            };

            if (probeGap < -BoundarySideTolerance)
            {
                continue;
            }

            var anchorGap = Math.Abs(candidateCoordinate - anchorCoordinate);
            if (anchorGap > maxDistance)
            {
                continue;
            }

            if (anchorGap < bestAnchorGap ||
                (Math.Abs(anchorGap - bestAnchorGap) <= 1e-6 && extensionDistance < bestExtensionDistance) ||
                (Math.Abs(anchorGap - bestAnchorGap) <= 1e-6 &&
                 Math.Abs(extensionDistance - bestExtensionDistance) <= 1e-6 &&
                 probeGap < bestProbeGap))
            {
                bestAnchorGap = anchorGap;
                bestExtensionDistance = extensionDistance;
                bestProbeGap = probeGap;
                best = new StraightBoundary(anchor.Side, candidate.OrderedStart(anchor.Side), candidate.OrderedEnd(anchor.Side), candidate.Layer);
            }
        }

        if (best.HasValue)
        {
            usedFallback = false;
            return best.Value;
        }

        return anchor;
    }

    private static bool TryCreateStraightBoundary(Curve curve, out StraightBoundary boundary)
    {
        boundary = default;
        var start = new Point2d(curve.StartPoint.X, curve.StartPoint.Y);
        var end = new Point2d(curve.EndPoint.X, curve.EndPoint.Y);
        if (start.GetDistanceTo(end) <= MinimumLineLength)
        {
            return false;
        }

        var direction = end - start;
        var side = Math.Abs(direction.X) >= Math.Abs(direction.Y) ? BoundarySide.South : BoundarySide.West;
        boundary = new StraightBoundary(side, start, end, curve.Layer ?? string.Empty);
        return true;
    }

    private static Point3d ComputeOffsetPoint(SectionFrame frame, BoundarySet boundaries, BuildDrillPointRequest request)
    {
        var provisional = BuildRawOffsetPoint(frame, request);
        var provisional2d = new Point2d(provisional.X, provisional.Y);
        var eastWestDistance = GetScaledOffsetDistance(request.EastWestDistance, request.CombinedScaleFactor);
        var northSouthDistance = GetScaledOffsetDistance(request.NorthSouthDistance, request.CombinedScaleFactor);

        var southPoint = IntersectProbeWithBoundary(provisional2d, frame.NorthUnit, boundaries.South);
        var northPoint = IntersectProbeWithBoundary(provisional2d, frame.NorthUnit, boundaries.North);
        var westPoint = IntersectProbeWithBoundary(provisional2d, frame.EastUnit, boundaries.West);
        var eastPoint = IntersectProbeWithBoundary(provisional2d, frame.EastUnit, boundaries.East);

        var eastCoordinate = request.EastWestReference == BuildDrillEastWestReference.EastOfWest
            ? ProjectAlongAxis(westPoint - frame.SouthWest, frame.EastUnit) + eastWestDistance
            : ProjectAlongAxis(eastPoint - frame.SouthWest, frame.EastUnit) - eastWestDistance;

        var northCoordinate = request.NorthSouthReference == BuildDrillNorthSouthReference.NorthOfSouth
            ? ProjectAlongAxis(southPoint - frame.SouthWest, frame.NorthUnit) + northSouthDistance
            : ProjectAlongAxis(northPoint - frame.SouthWest, frame.NorthUnit) - northSouthDistance;

        var finalPoint2d = frame.SouthWest + (frame.EastUnit * eastCoordinate) + (frame.NorthUnit * northCoordinate);
        return new Point3d(finalPoint2d.X, finalPoint2d.Y, 0.0);
    }

    private static Point3d BuildRawOffsetPoint(SectionFrame frame, BuildDrillPointRequest request)
    {
        var eastWestDistance = GetScaledOffsetDistance(request.EastWestDistance, request.CombinedScaleFactor);
        var northSouthDistance = GetScaledOffsetDistance(request.NorthSouthDistance, request.CombinedScaleFactor);
        var eastCoordinate = request.EastWestReference == BuildDrillEastWestReference.EastOfWest
            ? eastWestDistance
            : frame.Width - eastWestDistance;
        var northCoordinate = request.NorthSouthReference == BuildDrillNorthSouthReference.NorthOfSouth
            ? northSouthDistance
            : frame.Height - northSouthDistance;

        var point = frame.SouthWest + (frame.EastUnit * eastCoordinate) + (frame.NorthUnit * northCoordinate);
        return new Point3d(point.X, point.Y, 0.0);
    }

    private static bool TryComputeOffsetPointFromBoundaryCandidates(
        SectionFrame frame,
        IReadOnlyList<StraightBoundary> candidates,
        BuildDrillPointRequest request,
        out Point3d point,
        out string detail)
    {
        point = default;
        detail = string.Empty;
        if (candidates == null || candidates.Count == 0)
        {
            detail = "No hard ATS boundary candidates were available.";
            return false;
        }

        var provisional = BuildRawOffsetPoint(frame, request);
        var provisional2d = new Point2d(provisional.X, provisional.Y);
        var outerZeroCandidates = CreateOuterSectionIndexZeroCandidates(frame);
        var allCandidates = candidates.Count == 0
            ? outerZeroCandidates
            : candidates.Concat(outerZeroCandidates).ToList();
        StraightBoundary southBoundary = default;
        StraightBoundary northBoundary = default;
        StraightBoundary westBoundary = default;
        StraightBoundary eastBoundary = default;

        if (request.NorthSouthReference == BuildDrillNorthSouthReference.NorthOfSouth)
        {
            if (!TryFindBoundaryProbeIntersection(frame, allCandidates, provisional2d, BoundarySide.South, out _, out southBoundary, out var southDetail))
            {
                detail = southDetail;
                return false;
            }
        }
        else
        {
            if (!TryFindBoundaryProbeIntersection(frame, allCandidates, provisional2d, BoundarySide.North, out _, out northBoundary, out var northDetail))
            {
                detail = northDetail;
                return false;
            }
        }

        if (request.EastWestReference == BuildDrillEastWestReference.EastOfWest)
        {
            if (!TryFindBoundaryProbeIntersection(frame, allCandidates, provisional2d, BoundarySide.West, out _, out westBoundary, out var westDetail))
            {
                detail = westDetail;
                return false;
            }
        }
        else
        {
            if (!TryFindBoundaryProbeIntersection(frame, allCandidates, provisional2d, BoundarySide.East, out _, out eastBoundary, out var eastDetail))
            {
                detail = eastDetail;
                return false;
            }
        }

        var northSouthBoundary = request.NorthSouthReference == BuildDrillNorthSouthReference.NorthOfSouth
            ? southBoundary
            : northBoundary;
        var eastWestBoundary = request.EastWestReference == BuildDrillEastWestReference.EastOfWest
            ? westBoundary
            : eastBoundary;
        if (!TryIntersectOffsetBoundaryLines(frame, request, northSouthBoundary, eastWestBoundary, out point, out var intersectionDetail))
        {
            detail = intersectionDetail;
            return false;
        }

        var usedSides = new List<string>(2);
        if (request.NorthSouthReference == BuildDrillNorthSouthReference.NorthOfSouth)
        {
            usedSides.Add($"S:{southBoundary.Layer}");
        }
        else
        {
            usedSides.Add($"N:{northBoundary.Layer}");
        }

        if (request.EastWestReference == BuildDrillEastWestReference.EastOfWest)
        {
            usedSides.Add($"W:{westBoundary.Layer}");
        }
        else
        {
            usedSides.Add($"E:{eastBoundary.Layer}");
        }

        detail = $"Resolved ATS probe crossings from {string.Join(", ", usedSides)}. {intersectionDetail}";
        return true;
    }

    private static List<StraightBoundary> CreateOuterSectionIndexZeroCandidates(SectionFrame frame)
    {
        return new List<StraightBoundary>(4)
        {
            new(BoundarySide.South, frame.SouthWest, frame.SouthEast, "SECTION-INDEX-0"),
            new(BoundarySide.West, frame.SouthWest, frame.NorthWest, "SECTION-INDEX-0"),
            new(BoundarySide.North, frame.NorthWest, frame.NorthEast, "SECTION-INDEX-0"),
            new(BoundarySide.East, frame.SouthEast, frame.NorthEast, "SECTION-INDEX-0")
        };
    }

    private static bool TryIntersectOffsetBoundaryLines(
        SectionFrame frame,
        BuildDrillPointRequest request,
        StraightBoundary northSouthBoundary,
        StraightBoundary eastWestBoundary,
        out Point3d point,
        out string detail)
    {
        point = default;
        detail = string.Empty;
        var northSouthDistance = GetScaledOffsetDistance(request.NorthSouthDistance, request.CombinedScaleFactor);
        var eastWestDistance = GetScaledOffsetDistance(request.EastWestDistance, request.CombinedScaleFactor);

        var northSouthOffset = GetBoundaryInwardNormal(
            northSouthBoundary,
            request.NorthSouthReference == BuildDrillNorthSouthReference.NorthOfSouth
                ? frame.NorthUnit
                : frame.NorthUnit.Negate()) * northSouthDistance;
        var eastWestOffset = GetBoundaryInwardNormal(
            eastWestBoundary,
            request.EastWestReference == BuildDrillEastWestReference.EastOfWest
                ? frame.EastUnit
                : frame.EastUnit.Negate()) * eastWestDistance;

        var shiftedNorthSouthStart = northSouthBoundary.Start + northSouthOffset;
        var shiftedNorthSouthEnd = northSouthBoundary.End + northSouthOffset;
        var shiftedEastWestStart = eastWestBoundary.Start + eastWestOffset;
        var shiftedEastWestEnd = eastWestBoundary.End + eastWestOffset;
        if (!TryIntersectInfiniteLines(
                shiftedNorthSouthStart,
                shiftedNorthSouthEnd,
                shiftedEastWestStart,
                shiftedEastWestEnd,
                out var resolvedPoint))
        {
            detail = "Resolved ATS offset lines could not be intersected.";
            return false;
        }

        point = new Point3d(resolvedPoint.X, resolvedPoint.Y, 0.0);
        detail = "Computed exact ATS offset-line intersection.";
        return true;
    }

    private static Vector2d GetBoundaryInwardNormal(StraightBoundary boundary, Vector2d preferredDirection)
    {
        var direction = boundary.End - boundary.Start;
        if (direction.Length <= 1e-9)
        {
            return preferredDirection.GetNormal();
        }

        var unit = direction.GetNormal();
        var leftNormal = new Vector2d(-unit.Y, unit.X);
        var rightNormal = new Vector2d(unit.Y, -unit.X);
        var preferred = preferredDirection.Length <= 1e-9
            ? preferredDirection
            : preferredDirection.GetNormal();
        return leftNormal.DotProduct(preferred) >= rightNormal.DotProduct(preferred)
            ? leftNormal
            : rightNormal;
    }

    private static bool TryFindBoundaryProbeIntersection(
        SectionFrame frame,
        IReadOnlyList<StraightBoundary> candidates,
        Point2d probePoint,
        BoundarySide side,
        out Point2d intersection,
        out StraightBoundary boundary,
        out string detail)
    {
        intersection = default;
        boundary = default;
        detail = string.Empty;

        var sideAxis = side is BoundarySide.South or BoundarySide.North ? frame.NorthUnit : frame.EastUnit;
        var boundaryAxis = side is BoundarySide.South or BoundarySide.North ? frame.EastUnit : frame.NorthUnit;
        var probeAxis = side is BoundarySide.South or BoundarySide.North ? frame.NorthUnit : frame.EastUnit;
        var probeSideCoordinate = ProjectAlongAxis(probePoint - frame.SouthWest, sideAxis);

        var bestPriority = int.MaxValue;
        var bestExtensionDistance = double.MaxValue;
        var bestGap = double.MaxValue;
        var parallelMatches = 0;

        foreach (var candidate in candidates)
        {
            if (!IsBoundaryParallelToAxis(candidate, boundaryAxis))
            {
                continue;
            }

            if (!TryIntersectProbeWithBoundaryCandidate(
                    probePoint,
                    probeAxis,
                    candidate,
                    out var candidateIntersection,
                    out var extensionDistance))
            {
                continue;
            }

            parallelMatches++;
            var candidateCoordinate = ProjectAlongAxis(candidateIntersection - frame.SouthWest, sideAxis);
            var gap = side switch
            {
                BoundarySide.South => probeSideCoordinate - candidateCoordinate,
                BoundarySide.North => candidateCoordinate - probeSideCoordinate,
                BoundarySide.West => probeSideCoordinate - candidateCoordinate,
                BoundarySide.East => candidateCoordinate - probeSideCoordinate,
                _ => double.MaxValue
            };

            if (gap < -BoundarySideTolerance)
            {
                continue;
            }

            var priority = GetBoundaryLayerPriority(side, candidate.Layer);
            if (priority < bestPriority ||
                (priority == bestPriority && extensionDistance < bestExtensionDistance) ||
                (priority == bestPriority &&
                 Math.Abs(extensionDistance - bestExtensionDistance) <= 1e-6 &&
                 gap < bestGap))
            {
                bestPriority = priority;
                bestExtensionDistance = extensionDistance;
                bestGap = gap;
                intersection = candidateIntersection;
                boundary = new StraightBoundary(side, candidate.OrderedStart(side), candidate.OrderedEnd(side), candidate.Layer);
            }
        }

        if (bestGap < double.MaxValue)
        {
            detail = bestExtensionDistance <= BoundaryProbeSegmentTolerance
                ? $"{side} hard ATS boundary resolved from {boundary.Layer}."
                : $"{side} hard ATS boundary resolved from {boundary.Layer} using {bestExtensionDistance:0.###}m bounded extension.";
            return true;
        }

        detail =
            $"Could not find a {side.ToString().ToLowerInvariant()} hard ATS boundary crossing the requested probe line (crossing candidates: {parallelMatches}, total candidates: {candidates.Count}).";
        return false;
    }

    private static int GetBoundaryLayerPriority(BoundarySide side, string layer)
    {
        if (IsCorrectionUsecZeroLayer(layer))
        {
            return -1;
        }

        if (IsActualUsecZeroLayer(layer))
        {
            return side is BoundarySide.North or BoundarySide.East ? 0 : 1;
        }

        if (IsUsecTwentyLayer(layer))
        {
            return side is BoundarySide.South or BoundarySide.West ? 0 : 1;
        }

        if (IsSectionBoundaryLayer(layer))
        {
            return 2;
        }

        if (IsSyntheticSectionIndexZeroLayer(layer))
        {
            return 3;
        }

        return 4;
    }

    private static bool IsActualUsecZeroLayer(string layer)
    {
        return string.Equals(layer, "L-USEC-0", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCorrectionUsecZeroLayer(string layer)
    {
        return string.Equals(layer, "L-USEC-C-0", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSyntheticSectionIndexZeroLayer(string layer)
    {
        return string.Equals(layer, "SECTION-INDEX-0", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUsecTwentyLayer(string layer)
    {
        return string.Equals(layer, "L-USEC2012", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSectionBoundaryLayer(string layer)
    {
        return string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(layer, "L-SEC-0", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(layer, "L-SEC2012", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(layer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBoundaryParallelToAxis(StraightBoundary boundary, Vector2d axis)
    {
        var direction = (boundary.End - boundary.Start).GetNormal();
        return Math.Abs(direction.DotProduct(axis.GetNormal())) >= ParallelDotTolerance;
    }

    private static bool TryIntersectProbeWithBoundaryCandidate(
        Point2d probePoint,
        Vector2d probeAxis,
        StraightBoundary boundary,
        out Point2d intersection,
        out double extensionDistance)
    {
        intersection = default;
        extensionDistance = double.MaxValue;
        var probeStart = probePoint - (probeAxis * 10000.0);
        var probeEnd = probePoint + (probeAxis * 10000.0);
        if (!TryIntersectInfiniteLines(probeStart, probeEnd, boundary.Start, boundary.End, out intersection))
        {
            return false;
        }

        return TryMeasureBoundaryProbeExtension(intersection, boundary.Start, boundary.End, out extensionDistance);
    }

    private static bool TryIntersectProjectedProbeWithBoundaryCandidate(
        Point2d probePoint,
        Vector2d probeAxis,
        StraightBoundary boundary,
        double projectionLimit,
        out Point2d intersection,
        out double extensionDistance)
    {
        intersection = default;
        extensionDistance = double.MaxValue;
        var probeStart = probePoint - (probeAxis * 10000.0);
        var probeEnd = probePoint + (probeAxis * 10000.0);
        if (!TryIntersectInfiniteLines(probeStart, probeEnd, boundary.Start, boundary.End, out intersection))
        {
            return false;
        }

        extensionDistance = MeasureBoundaryProbeExtensionDistance(intersection, boundary.Start, boundary.End);
        return extensionDistance <= projectionLimit;
    }

    private static double MeasureBoundaryProbeExtensionDistance(Point2d point, Point2d start, Point2d end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= 1e-12)
        {
            return point.GetDistanceTo(start);
        }

        var parameter = (((point.X - start.X) * dx) + ((point.Y - start.Y) * dy)) / lengthSquared;
        if (parameter >= -BoundaryProbeSegmentTolerance && parameter <= 1.0 + BoundaryProbeSegmentTolerance)
        {
            return 0.0;
        }

        var nearestEndpoint = parameter < 0.0 ? start : end;
        return point.GetDistanceTo(nearestEndpoint);
    }

    private static bool TryMeasureBoundaryProbeExtension(
        Point2d point,
        Point2d start,
        Point2d end,
        out double extensionDistance)
    {
        extensionDistance = double.MaxValue;
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= 1e-12)
        {
            var distance = point.GetDistanceTo(start);
            if (distance <= BoundaryProbeSegmentTolerance)
            {
                extensionDistance = 0.0;
                return true;
            }

            if (distance <= BoundaryProbeExtensionTolerance)
            {
                extensionDistance = distance;
                return true;
            }

            return false;
        }

        var parameter = (((point.X - start.X) * dx) + ((point.Y - start.Y) * dy)) / lengthSquared;
        if (parameter >= -BoundaryProbeSegmentTolerance && parameter <= 1.0 + BoundaryProbeSegmentTolerance)
        {
            extensionDistance = 0.0;
            return true;
        }

        var nearestEndpoint = parameter < 0.0 ? start : end;
        var nearestDistance = point.GetDistanceTo(nearestEndpoint);
        if (nearestDistance <= BoundaryProbeExtensionTolerance)
        {
            extensionDistance = nearestDistance;
            return true;
        }

        return false;
    }

    private static bool IsPointOnSegment(Point2d point, Point2d start, Point2d end, double tolerance)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= 1e-12)
        {
            return point.GetDistanceTo(start) <= tolerance;
        }

        var parameter = (((point.X - start.X) * dx) + ((point.Y - start.Y) * dy)) / lengthSquared;
        if (parameter < -tolerance || parameter > 1.0 + tolerance)
        {
            return false;
        }

        var closest = new Point2d(start.X + (parameter * dx), start.Y + (parameter * dy));
        return point.GetDistanceTo(closest) <= tolerance;
    }

    private static Point2d IntersectProbeWithBoundary(Point2d point, Vector2d axis, StraightBoundary boundary)
    {
        var probeStart = point - (axis * 10000.0);
        var probeEnd = point + (axis * 10000.0);
        if (TryIntersectInfiniteLines(probeStart, probeEnd, boundary.Start, boundary.End, out var intersection))
        {
            return intersection;
        }

        return boundary.Midpoint;
    }

    private static bool TryIntersectInfiniteLines(Point2d a1, Point2d a2, Point2d b1, Point2d b2, out Point2d intersection)
    {
        intersection = default;

        var denominator = ((a1.X - a2.X) * (b1.Y - b2.Y)) - ((a1.Y - a2.Y) * (b1.X - b2.X));
        if (Math.Abs(denominator) <= 1e-9)
        {
            return false;
        }

        var determinantA = (a1.X * a2.Y) - (a1.Y * a2.X);
        var determinantB = (b1.X * b2.Y) - (b1.Y * b2.X);
        var x = ((determinantA * (b1.X - b2.X)) - ((a1.X - a2.X) * determinantB)) / denominator;
        var y = ((determinantA * (b1.Y - b2.Y)) - ((a1.Y - a2.Y) * determinantB)) / denominator;
        intersection = new Point2d(x, y);
        return true;
    }

    private static double ProjectAlongAxis(Vector2d vector, Vector2d axis)
    {
        return vector.DotProduct(axis.GetNormal());
    }

    private readonly record struct SectionFrame(
        Point2d SouthWest,
        Point2d SouthEast,
        Point2d NorthWest,
        Point2d NorthEast,
        Vector2d EastUnit,
        Vector2d NorthUnit,
        double Width,
        double Height);

    private readonly record struct CordsSectionFrame(SectionKey Key, SectionFrame Frame);

    private readonly record struct CordsQuarterBounds(
        string Token,
        double UMin,
        double UMax,
        double TMin,
        double TMax);

    private readonly record struct StraightBoundary(BoundarySide Side, Point2d Start, Point2d End, string Layer)
    {
        public Point2d Midpoint => new((Start.X + End.X) * 0.5, (Start.Y + End.Y) * 0.5);

        public bool IsParallelTo(StraightBoundary other)
        {
            var first = (End - Start).GetNormal();
            var second = (other.End - other.Start).GetNormal();
            return Math.Abs(first.DotProduct(second)) >= ParallelDotTolerance;
        }

        public double DistanceToPoint(Point2d point)
        {
            return DistancePointToSegment(point, Start, End);
        }

        public Point2d OrderedStart(BoundarySide side)
        {
            return side switch
            {
                BoundarySide.South or BoundarySide.North => Start.X <= End.X ? Start : End,
                BoundarySide.West or BoundarySide.East => Start.Y <= End.Y ? Start : End,
                _ => Start
            };
        }

        public Point2d OrderedEnd(BoundarySide side)
        {
            return side switch
            {
                BoundarySide.South or BoundarySide.North => Start.X <= End.X ? End : Start,
                BoundarySide.West or BoundarySide.East => Start.Y <= End.Y ? End : Start,
                _ => End
            };
        }

        private static double DistancePointToSegment(Point2d point, Point2d start, Point2d end)
        {
            var dx = end.X - start.X;
            var dy = end.Y - start.Y;
            var lengthSquared = (dx * dx) + (dy * dy);
            if (lengthSquared <= 1e-12)
            {
                return point.GetDistanceTo(start);
            }

            var projection = (((point.X - start.X) * dx) + ((point.Y - start.Y) * dy)) / lengthSquared;
            var t = Math.Max(0.0, Math.Min(1.0, projection));
            var closest = new Point2d(start.X + (t * dx), start.Y + (t * dy));
            return point.GetDistanceTo(closest);
        }
    }

    private readonly record struct CachedBoundaryResolution(IReadOnlyList<StraightBoundary> Candidates, string SourceDescription);

    private readonly record struct BoundarySet(
        StraightBoundary South,
        StraightBoundary North,
        StraightBoundary West,
        StraightBoundary East);

    private readonly record struct BoundarySearchResult(BoundarySet Boundaries, bool UsedFallback);

    private enum BoundarySide
    {
        South,
        North,
        West,
        East
    }
}
