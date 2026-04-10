using System;
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
    private const string DrillPathLayer = "P-PDRILLPATH";
    private const string DrillPointsLayer = "Z-DRILL-POINT";
    private const double DrillPointTextHeight = 2.0;
    private const string DefaultSectionIndexFolder = @"C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\RES MANAGER";
    private const double AtsBoundarySearchDistance = 45.0;
    private const double HorizontalBoundarySearchDistance = 10.0;
    private const double MinimumLineLength = 1e-3;
    private const double ParallelDotTolerance = 0.8;
    private static readonly string[] AtsBoundaryLayers =
    {
        "L-USEC-0",
        "L-USEC2012",
        "L-USEC-2012",
        "L-SEC"
    };
    private static readonly string[] HorizontalBoundaryLayers =
    {
        "L-SEC-HB"
    };

    private readonly ILog _log;
    private readonly LayerService _layerService;

    public BuildDrillService(ILog log, LayerService layerService)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _layerService = layerService ?? throw new ArgumentNullException(nameof(layerService));
    }

    public void BuildDrill(BuildDrillRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Points == null || request.Points.Count < 2)
        {
            MessageBox.Show("Build a Drill needs at least two points.", "Build a Drill", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            MessageBox.Show("No active AutoCAD document is available.", "Build a Drill", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var pathPoints = new List<Point3d>(request.Points.Count + (request.SurfacePoint.HasValue ? 1 : 0));
            var pointNotes = new List<string>(request.Points.Count + (request.SurfacePoint.HasValue ? 1 : 0));

            if (request.SurfacePoint.HasValue)
            {
                pathPoints.Add(request.SurfacePoint.Value);
                pointNotes.Add("surface start point");
            }

            for (var i = 0; i < request.Points.Count; i++)
            {
                var pointRequest = request.Points[i];
                var point = ResolveTargetPoint(pointRequest, document.Database, document.Name, out var sourceDescription);
                pathPoints.Add(point);
                pointNotes.Add(sourceDescription);
            }

            _layerService.EnsureLayer(document.Database, DrillPathLayer);
            _layerService.EnsureLayer(document.Database, DrillPointsLayer);
            var drillLetter = NormalizeDrillLetter(request.DrillLetter);
            var labeledPoints = BuildLabeledPoints(drillLetter, request.SurfacePoint, request.Points.Count, pathPoints);

            using (var transaction = document.Database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)transaction.GetObject(document.Database.BlockTableId, OpenMode.ForRead);
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

            var summary = BuildPointSummary(pointNotes);
            _log.Info($"Build a Drill created a {pathPoints.Count}-point path for '{request.DrillName}' on {DrillPathLayer} with {labeledPoints.Count} point labels on {DrillPointsLayer}. {summary}");
            MessageBox.Show(
                $"Built {request.DrillName} with {pathPoints.Count} point(s) on {DrillPathLayer}.\nPoint labels were refreshed on {DrillPointsLayer}.\n{summary}",
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

    private Point3d ResolveTargetPoint(BuildDrillPointRequest request, Database database, string? drawingPath, out string sourceDescription)
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
                return ResolveSectionOffsetPoint(request, database, drawingPath, out sourceDescription);

            default:
                throw new InvalidOperationException($"Unsupported build source: {request.Source}.");
        }
    }

    private Point3d ConvertNad27ToNad83(BuildDrillPointRequest request)
    {
        var sourceCode = $"UTM27-{request.Zone}";
        var destinationCode = $"UTM83-{request.Zone}";
        var failureDetails = new List<string>(2);

        if (Map3dCoordinateTransformer.TryCreate(sourceCode, destinationCode, out var transformer) && transformer != null)
        {
            // The shared Map transformer helper returns Y first and X second.
            if (transformer.TryProject(new Point3d(request.X, request.Y, 0.0), out var transformedY, out var transformedX))
            {
                return new Point3d(transformedX, transformedY, 0.0);
            }

            failureDetails.Add("managed Map transformer loaded but returned no projected point");
        }
        else
        {
            failureDetails.Add("managed Map transformer could not be created");
        }

        if (TryConvertNad27ToNad83ViaAde(sourceCode, destinationCode, request.X, request.Y, out var fallbackPoint, out var adeDetail))
        {
            return fallbackPoint;
        }

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
            if (TryConvertNad27ToNad83ViaAdeInvoke(sourceCode, destinationCode, x, y, out point, out detail))
            {
                return true;
            }

            var invokeDetail = detail;
            if (TryConvertNad27ToNad83ViaSendCommand(sourceCode, destinationCode, x, y, out point, out var commandDetail))
            {
                return true;
            }

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
            InvokeLispFunction("ade_errclear");

            using var sourceResult = InvokeLispFunction("ade_projsetsrc", new TypedValue((int)LispDataType.Text, sourceCode));
            if (!HasTruthyLispResult(sourceResult))
            {
                detail = $"ADE ade_projsetsrc returned {DescribeResultBuffer(sourceResult)}";
                return false;
            }

            using var destinationResult = InvokeLispFunction("ade_projsetdest", new TypedValue((int)LispDataType.Text, destinationCode));
            if (!HasTruthyLispResult(destinationResult))
            {
                detail = $"ADE ade_projsetdest returned {DescribeResultBuffer(destinationResult)}";
                return false;
            }

            if (TryInvokeAdeProjectionVariant(
                    "Point3d",
                    out point,
                    out detail,
                    new TypedValue((int)LispDataType.Point3d, new Point3d(x, y, 0.0))))
            {
                return true;
            }

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

    private Point3d ResolveSectionOffsetPoint(BuildDrillPointRequest request, Database database, string? drawingPath, out string sourceDescription)
    {
        if (!TryLoadSectionFrame(request, drawingPath, out var sectionFrame))
        {
            throw new InvalidOperationException(
                $"Could not load ATS section {request.Section}-{request.Township}-{request.Range}-W{request.Meridian} in zone {request.Zone} from the section index search path.");
        }

        ValidateDistancesAgainstFrame(request, sectionFrame);

        var rawBoundaries = CreateFrameBoundaries(sectionFrame);
        var atsResult = ResolveNearestBoundaries(database, rawBoundaries, AtsBoundaryLayers, AtsBoundarySearchDistance);
        if (request.UseAtsFabric)
        {
            sourceDescription = atsResult.UsedFallback
                ? "section offsets from ATS fabric / section-index fallback"
                : "section offsets from ATS fabric";
            return ComputeOffsetPoint(sectionFrame, atsResult.Boundaries, request);
        }

        var hbResult = ResolveNearestBoundaries(database, atsResult.Boundaries, HorizontalBoundaryLayers, HorizontalBoundarySearchDistance);
        sourceDescription = hbResult.UsedFallback
            ? "section offsets from L-SEC-HB / ATS fallback"
            : "section offsets from L-SEC-HB";
        return ComputeOffsetPoint(sectionFrame, hbResult.Boundaries, request);
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

    private static void ValidateDistancesAgainstFrame(BuildDrillPointRequest request, SectionFrame frame)
    {
        if (request.NorthSouthDistance > frame.Height + 1e-6)
        {
            throw new InvalidOperationException(
                $"North/south distance {request.NorthSouthDistance:0.###} is larger than the section height ({frame.Height:0.###}).");
        }

        if (request.EastWestDistance > frame.Width + 1e-6)
        {
            throw new InvalidOperationException(
                $"East/west distance {request.EastWestDistance:0.###} is larger than the section width ({frame.Width:0.###}).");
        }
    }

    private static BoundarySet CreateFrameBoundaries(SectionFrame frame)
    {
        return new BoundarySet(
            new StraightBoundary(BoundarySide.South, frame.SouthWest, frame.SouthEast, "SECTION-INDEX"),
            new StraightBoundary(BoundarySide.North, frame.NorthWest, frame.NorthEast, "SECTION-INDEX"),
            new StraightBoundary(BoundarySide.West, frame.SouthWest, frame.NorthWest, "SECTION-INDEX"),
            new StraightBoundary(BoundarySide.East, frame.SouthEast, frame.NorthEast, "SECTION-INDEX"));
    }

    private static BoundarySearchResult ResolveNearestBoundaries(Database database, BoundarySet anchors, IReadOnlyCollection<string> layers, double maxDistance)
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

        var south = FindBoundary(anchors.South, candidates, maxDistance, out var southFallback);
        var north = FindBoundary(anchors.North, candidates, maxDistance, out var northFallback);
        var west = FindBoundary(anchors.West, candidates, maxDistance, out var westFallback);
        var east = FindBoundary(anchors.East, candidates, maxDistance, out var eastFallback);

        return new BoundarySearchResult(
            new BoundarySet(south, north, west, east),
            southFallback || northFallback || westFallback || eastFallback);
    }

    private static StraightBoundary FindBoundary(StraightBoundary anchor, IReadOnlyList<StraightBoundary> candidates, double maxDistance, out bool usedFallback)
    {
        usedFallback = true;

        StraightBoundary? best = null;
        var bestDistance = double.MaxValue;
        foreach (var candidate in candidates)
        {
            if (!candidate.IsParallelTo(anchor))
            {
                continue;
            }

            var distance = candidate.DistanceToPoint(anchor.Midpoint);
            if (distance > maxDistance)
            {
                continue;
            }

            if (distance < bestDistance)
            {
                bestDistance = distance;
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
        boundary = new StraightBoundary(side, start, end, curve.Layer);
        return true;
    }

    private static Point3d ComputeOffsetPoint(SectionFrame frame, BoundarySet boundaries, BuildDrillPointRequest request)
    {
        var provisional = BuildRawOffsetPoint(frame, request);
        var provisional2d = new Point2d(provisional.X, provisional.Y);

        var southPoint = IntersectProbeWithBoundary(provisional2d, frame.NorthUnit, boundaries.South);
        var northPoint = IntersectProbeWithBoundary(provisional2d, frame.NorthUnit, boundaries.North);
        var westPoint = IntersectProbeWithBoundary(provisional2d, frame.EastUnit, boundaries.West);
        var eastPoint = IntersectProbeWithBoundary(provisional2d, frame.EastUnit, boundaries.East);

        var eastCoordinate = request.EastWestReference == BuildDrillEastWestReference.EastOfWest
            ? ProjectAlongAxis(westPoint - frame.SouthWest, frame.EastUnit) + request.EastWestDistance
            : ProjectAlongAxis(eastPoint - frame.SouthWest, frame.EastUnit) - request.EastWestDistance;

        var northCoordinate = request.NorthSouthReference == BuildDrillNorthSouthReference.NorthOfSouth
            ? ProjectAlongAxis(southPoint - frame.SouthWest, frame.NorthUnit) + request.NorthSouthDistance
            : ProjectAlongAxis(northPoint - frame.SouthWest, frame.NorthUnit) - request.NorthSouthDistance;

        var finalPoint2d = frame.SouthWest + (frame.EastUnit * eastCoordinate) + (frame.NorthUnit * northCoordinate);
        return new Point3d(finalPoint2d.X, finalPoint2d.Y, 0.0);
    }

    private static Point3d BuildRawOffsetPoint(SectionFrame frame, BuildDrillPointRequest request)
    {
        var eastCoordinate = request.EastWestReference == BuildDrillEastWestReference.EastOfWest
            ? request.EastWestDistance
            : frame.Width - request.EastWestDistance;
        var northCoordinate = request.NorthSouthReference == BuildDrillNorthSouthReference.NorthOfSouth
            ? request.NorthSouthDistance
            : frame.Height - request.NorthSouthDistance;

        var point = frame.SouthWest + (frame.EastUnit * eastCoordinate) + (frame.NorthUnit * northCoordinate);
        return new Point3d(point.X, point.Y, 0.0);
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
