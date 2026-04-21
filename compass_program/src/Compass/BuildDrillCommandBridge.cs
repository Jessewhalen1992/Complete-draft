using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Compass.Infrastructure;
using Compass.Models;
using Compass.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace Compass;

public class BuildDrillCommandBridge
{
    private const string ExecuteCommandName = "COMPASSBUILDDRILL_EXECUTE";
    private const string HeadlessCommandName = "COMPASSBUILDDRILL_HEADLESS";
    private const string HeadlessRequestPathEnvironmentVariable = "COMPASS_BUILDDRILL_REQUEST_PATH";
    private const string HeadlessOutputPathEnvironmentVariable = "COMPASS_BUILDDRILL_OUTPUT_PATH";
    private const string HeadlessOutputJsonPathEnvironmentVariable = "COMPASS_BUILDDRILL_OUTPUT_JSON_PATH";
    private const string HeadlessSavedDrawingPathEnvironmentVariable = "COMPASS_BUILDDRILL_SAVED_DRAWING_PATH";
    private const string HeadlessCreateGeometryEnvironmentVariable = "COMPASS_BUILDDRILL_CREATE_GEOMETRY";
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

    [CommandMethod(HeadlessCommandName, CommandFlags.Modal)]
    public static void ExecuteHeadlessBuild()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        var textOutputPath = ResolveHeadlessOutputPath(
            Environment.GetEnvironmentVariable(HeadlessOutputPathEnvironmentVariable),
            Path.Combine(Path.GetTempPath(), "Compass-builddrill-headless.txt"));
        var jsonOutputPath = ResolveHeadlessOutputPath(
            Environment.GetEnvironmentVariable(HeadlessOutputJsonPathEnvironmentVariable),
            Path.ChangeExtension(textOutputPath, ".json"));
        var report = new HeadlessBuildDrillReport
        {
            CommandName = HeadlessCommandName,
            GeneratedAt = DateTimeOffset.Now,
            DocumentName = document?.Name ?? string.Empty,
            RequestPath = Environment.GetEnvironmentVariable(HeadlessRequestPathEnvironmentVariable) ?? string.Empty,
            TextOutputPath = textOutputPath,
            JsonOutputPath = jsonOutputPath,
            SavedDrawingPath = ResolveOptionalHeadlessOutputPath(Environment.GetEnvironmentVariable(HeadlessSavedDrawingPathEnvironmentVariable)),
            CreateGeometry = ReadBooleanEnvironmentVariable(HeadlessCreateGeometryEnvironmentVariable, defaultValue: false)
        };

        try
        {
            CompassEnvironment.Initialize();
            CompassStartupDiagnostics.Log($"Build a Drill headless execution starting in '{report.DocumentName}'.");

            if (document == null)
            {
                throw new InvalidOperationException("No active AutoCAD document is available.");
            }

            var request = LoadHeadlessRequest(report.RequestPath);
            var service = new BuildDrillService(CompassEnvironment.Log, new LayerService());
            var result = service.ExecuteBuildDrill(
                request,
                new BuildDrillExecutionOptions
                {
                    CreateGeometry = report.CreateGeometry && string.IsNullOrWhiteSpace(report.SavedDrawingPath)
                });
            if (report.CreateGeometry && !string.IsNullOrWhiteSpace(report.SavedDrawingPath))
            {
                SaveHeadlessDrawing(document, report.SavedDrawingPath, result);
                result = MarkGeometryCreated(result);
            }

            report.Success = true;
            report.Result = result;
            WriteHeadlessOutputs(report);
            CompassStartupDiagnostics.Log($"Build a Drill headless run completed. Report: {report.TextOutputPath}");
        }
        catch (System.Exception ex)
        {
            report.Success = false;
            report.ErrorType = ex.GetType().FullName ?? ex.GetType().Name;
            report.ErrorMessage = ex.Message;
            WriteHeadlessOutputs(report);
            CompassStartupDiagnostics.LogException("Build a Drill headless execution", ex);
        }
    }

    private static BuildDrillRequest LoadHeadlessRequest(string requestPath)
    {
        if (string.IsNullOrWhiteSpace(requestPath))
        {
            throw new InvalidOperationException(
                $"Missing {HeadlessRequestPathEnvironmentVariable}. Point it at a Build a Drill request JSON file.");
        }

        var fullPath = Path.GetFullPath(requestPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("Build a Drill request JSON was not found.", fullPath);
        }

        var serializerSettings = new JsonSerializerSettings();
        serializerSettings.Converters.Add(new StringEnumConverter());

        var json = File.ReadAllText(fullPath);
        var request = JsonConvert.DeserializeObject<HeadlessBuildDrillRequest>(json, serializerSettings);
        if (request == null)
        {
            throw new InvalidOperationException($"Could not deserialize Build a Drill request from '{fullPath}'.");
        }

        return request.ToModel();
    }

    private static void WriteHeadlessOutputs(HeadlessBuildDrillReport report)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(report.TextOutputPath) ?? Path.GetTempPath());
        Directory.CreateDirectory(Path.GetDirectoryName(report.JsonOutputPath) ?? Path.GetTempPath());

        File.WriteAllText(report.TextOutputPath, BuildTextReport(report), Encoding.UTF8);
        File.WriteAllText(report.JsonOutputPath, JsonConvert.SerializeObject(report, Formatting.Indented), Encoding.UTF8);
    }

    private static string BuildTextReport(HeadlessBuildDrillReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Compass Build a Drill headless report");
        builder.AppendLine($"generated: {report.GeneratedAt:yyyy-MM-dd HH:mm:ss zzz}");
        builder.AppendLine($"command: {report.CommandName}");
        builder.AppendLine($"success: {report.Success}");
        builder.AppendLine($"document: {report.DocumentName}");
        builder.AppendLine($"request: {report.RequestPath}");
        builder.AppendLine($"createGeometry: {report.CreateGeometry}");
        if (!string.IsNullOrWhiteSpace(report.SavedDrawingPath))
        {
            builder.AppendLine($"savedDrawing: {report.SavedDrawingPath}");
        }

        if (!report.Success || report.Result == null)
        {
            if (!string.IsNullOrWhiteSpace(report.ErrorType))
            {
                builder.AppendLine($"errorType: {report.ErrorType}");
            }

            if (!string.IsNullOrWhiteSpace(report.ErrorMessage))
            {
                builder.AppendLine($"error: {report.ErrorMessage}");
            }

            return builder.ToString();
        }

        builder.AppendLine($"drillName: {report.Result.DrillName}");
        builder.AppendLine($"drillLetter: {report.Result.DrillLetter}");
        builder.AppendLine($"geometryCreated: {report.Result.GeometryCreated}");
        builder.AppendLine($"pointCount: {report.Result.Points.Count}");
        builder.AppendLine("summary:");
        builder.AppendLine(report.Result.Summary);
        builder.AppendLine();

        foreach (var point in report.Result.Points)
        {
            builder.AppendLine($"Point {point.Sequence} [{point.Label}]");
            builder.AppendLine($"  x: {point.X:0.###}");
            builder.AppendLine($"  y: {point.Y:0.###}");
            builder.AppendLine($"  z: {point.Z:0.###}");
            builder.AppendLine($"  note: {point.Note}");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string ResolveHeadlessOutputPath(string? configuredPath, string fallbackPath)
    {
        var candidate = string.IsNullOrWhiteSpace(configuredPath) ? fallbackPath : configuredPath.Trim();
        return Path.GetFullPath(candidate);
    }

    private static string ResolveOptionalHeadlessOutputPath(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return string.Empty;
        }

        return Path.GetFullPath(configuredPath.Trim());
    }

    private static BuildDrillExecutionResult MarkGeometryCreated(BuildDrillExecutionResult result)
    {
        return new BuildDrillExecutionResult
        {
            DocumentName = result.DocumentName,
            DrillName = result.DrillName,
            DrillLetter = result.DrillLetter,
            GeometryCreated = true,
            Summary = result.Summary,
            Points = result.Points
        };
    }

    private static void SaveHeadlessDrawing(Document document, string outputPath, BuildDrillExecutionResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Path.GetTempPath());
        if (string.IsNullOrWhiteSpace(document.Name) || !File.Exists(document.Name))
        {
            throw new InvalidOperationException("The active headless drawing could not be found for export.");
        }

        var exportSeedPath = Path.Combine(
            Path.GetDirectoryName(outputPath) ?? Path.GetTempPath(),
            Path.GetFileNameWithoutExtension(outputPath) + ".seed" + Path.GetExtension(outputPath));
        File.Copy(document.Name, exportSeedPath, overwrite: true);

        using var exportDatabase = new Database(false, true);
        exportDatabase.ReadDwgFile(exportSeedPath, FileOpenMode.OpenForReadAndAllShare, allowCPConversion: false, password: null);
        exportDatabase.CloseInput(true);

        var exportService = new BuildDrillService(CompassEnvironment.Log, new LayerService());
        var previousWorkingDatabase = HostApplicationServices.WorkingDatabase;
        try
        {
            HostApplicationServices.WorkingDatabase = exportDatabase;
            exportService.WriteResolvedGeometry(exportDatabase, result);
            using var snapshotDatabase = exportDatabase.Wblock();
            snapshotDatabase.SaveAs(outputPath, true, DwgVersion.Current, snapshotDatabase.SecurityParameters);
        }
        catch (System.Exception ex)
        {
            throw new InvalidOperationException($"Failed while exporting the side database copy: {ex.Message}", ex);
        }
        finally
        {
            HostApplicationServices.WorkingDatabase = previousWorkingDatabase;
            try
            {
                if (File.Exists(exportSeedPath))
                {
                    File.Delete(exportSeedPath);
                }
            }
            catch
            {
                // Best-effort cleanup for the temporary seed drawing.
            }
        }
    }

    private static bool ReadBooleanEnvironmentVariable(string variableName, bool defaultValue)
    {
        var raw = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        raw = raw.Trim();
        if (string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "y", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(raw, "0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "no", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "n", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return bool.TryParse(raw, out var parsed) ? parsed : defaultValue;
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
                CombinedScaleFactor = point.CombinedScaleFactor,
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

    private sealed class HeadlessBuildDrillRequest
    {
        public string DrillName { get; set; } = string.Empty;

        public string DrillLetter { get; set; } = string.Empty;

        public HeadlessPoint? SurfacePoint { get; set; }

        public List<HeadlessBuildDrillPointRequest> Points { get; set; } = new();

        public BuildDrillRequest ToModel()
        {
            return new BuildDrillRequest
            {
                DrillName = DrillName,
                DrillLetter = DrillLetter,
                SurfacePoint = SurfacePoint?.ToPoint3d(),
                Points = Points.ConvertAll(point => point.ToModel())
            };
        }
    }

    private sealed class HeadlessBuildDrillPointRequest
    {
        [JsonConverter(typeof(StringEnumConverter))]
        public BuildDrillSource Source { get; set; }

        public int Zone { get; set; }

        public double X { get; set; }

        public double Y { get; set; }

        public string Section { get; set; } = string.Empty;

        public string Township { get; set; } = string.Empty;

        public string Range { get; set; } = string.Empty;

        public string Meridian { get; set; } = string.Empty;

        public bool UseAtsFabric { get; set; }

        public double CombinedScaleFactor { get; set; } = 1.0;

        public double NorthSouthDistance { get; set; }

        [JsonConverter(typeof(StringEnumConverter))]
        public BuildDrillNorthSouthReference NorthSouthReference { get; set; }

        public double EastWestDistance { get; set; }

        [JsonConverter(typeof(StringEnumConverter))]
        public BuildDrillEastWestReference EastWestReference { get; set; }

        public BuildDrillPointRequest ToModel()
        {
            return new BuildDrillPointRequest
            {
                Source = Source,
                Zone = Zone,
                X = X,
                Y = Y,
                Section = Section,
                Township = Township,
                Range = Range,
                Meridian = Meridian,
                UseAtsFabric = UseAtsFabric,
                CombinedScaleFactor = CombinedScaleFactor,
                NorthSouthDistance = NorthSouthDistance,
                NorthSouthReference = NorthSouthReference,
                EastWestDistance = EastWestDistance,
                EastWestReference = EastWestReference
            };
        }
    }

    private sealed class HeadlessPoint
    {
        public double X { get; set; }

        public double Y { get; set; }

        public double Z { get; set; }

        public Point3d ToPoint3d() => new(X, Y, Z);
    }

    private sealed class HeadlessBuildDrillReport
    {
        public bool Success { get; set; }

        public string CommandName { get; set; } = string.Empty;

        public DateTimeOffset GeneratedAt { get; set; }

        public string DocumentName { get; set; } = string.Empty;

        public string RequestPath { get; set; } = string.Empty;

        public string TextOutputPath { get; set; } = string.Empty;

        public string JsonOutputPath { get; set; } = string.Empty;

        public string SavedDrawingPath { get; set; } = string.Empty;

        public bool CreateGeometry { get; set; }

        public string ErrorType { get; set; } = string.Empty;

        public string ErrorMessage { get; set; } = string.Empty;

        public BuildDrillExecutionResult? Result { get; set; }
    }
}
