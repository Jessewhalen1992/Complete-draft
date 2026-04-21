using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Compass.Infrastructure.Logging;
using Compass.Models;
using OfficeOpenXml;
using Microsoft.Win32;
using WildlifeSweeps;

using AutoCADApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Compass.Services;

public class DrillCadToolService
{
    private const string DrillPointsLayer = "Z-DRILL-POINT";
    private const string HeadingBlockName = "DRILL HEADING";
    private const string DrillBlockName = "DRILL";
    private const string OffsetsLayer = "P-Drill-Offset";
    private const string HorizontalLayer = "L-SEC-HB";
    private const string NotesLayer = "CG-NOTES";
    private const string CordsDirectory = @"C:\\CORDS";
    private const string ExistingWellTableTitle = "EXISTING WELL HEADS WITHIN PAD BOUNDARY";
    private const string ExistingWellTableStyleName = "induction Bend";
    private const short ExistingWellTableHeadingColorIndex = 14;
    private const double ExistingWellTableRowHeight = 25.0;
    private const double ExistingWellTableNameColumnWidth = 85.0;
    private const double ExistingWellTableCoordinateColumnWidth = 120.0;
    private static readonly Regex HeadingLabelRegex = new(@"\b(?:ICP|HEEL|LANDING)\b", RegexOptions.IgnoreCase);
    private static readonly string[] CordsExecutableSearchPaths =
    {
        @"C:\\AUTOCAD-SETUP CG\\CG_LISP\\COMPASS\\cords.exe",
        @"C:\\AUTOCAD-SETUP\\Lisp_2000\\DRILL PROPERTIES\\cords.exe"
    };

    private readonly ILog _log;
    private readonly LayerService _layerService;
    private readonly NaturalStringComparer _naturalComparer = new();

    private sealed class GridPointCoordinate
    {
        public GridPointCoordinate(string label, double northing, double easting)
        {
            Label = label;
            Northing = northing;
            Easting = easting;
        }

        public string Label { get; }

        public double Northing { get; }

        public double Easting { get; }
    }

    private sealed class LabeledPoint
    {
        public LabeledPoint(string label, Point3d point)
        {
            Label = label;
            Point = point;
        }

        public string Label { get; }

        public Point3d Point { get; }
    }

    private sealed class TableCellLocation
    {
        public TableCellLocation(int row, int column)
        {
            Row = row;
            Column = column;
        }

        public int Row { get; }

        public int Column { get; }
    }

    private sealed class NativeCordsRow
    {
        public NativeCordsRow(
            string pointLabel,
            string location,
            string metes,
            string bounds,
            double nad83Northing,
            double nad83Easting,
            double nad83Latitude,
            double nad83Longitude,
            double nad27Northing,
            double nad27Easting,
            double nad27Latitude,
            double nad27Longitude)
        {
            PointLabel = pointLabel;
            Location = location;
            Metes = metes;
            Bounds = bounds;
            Nad83Northing = nad83Northing;
            Nad83Easting = nad83Easting;
            Nad83Latitude = nad83Latitude;
            Nad83Longitude = nad83Longitude;
            Nad27Northing = nad27Northing;
            Nad27Easting = nad27Easting;
            Nad27Latitude = nad27Latitude;
            Nad27Longitude = nad27Longitude;
        }

        public string PointLabel { get; }
        public string Location { get; }
        public string Metes { get; }
        public string Bounds { get; }
        public double Nad83Northing { get; }
        public double Nad83Easting { get; }
        public double Nad83Latitude { get; }
        public double Nad83Longitude { get; }
        public double Nad27Northing { get; }
        public double Nad27Easting { get; }
        public double Nad27Latitude { get; }
        public double Nad27Longitude { get; }
    }

    private sealed class ExistingWellTablePoint
    {
        public ExistingWellTablePoint(string name, Point3d point)
        {
            Name = name;
            Point = point;
        }

        public string Name { get; }

        public Point3d Point { get; }
    }

    private sealed class ExistingWellTableRow
    {
        public ExistingWellTableRow(string name, string firstValue, string secondValue)
        {
            Name = name;
            FirstValue = firstValue;
            SecondValue = secondValue;
        }

        public string Name { get; }

        public string FirstValue { get; }

        public string SecondValue { get; }
    }

    public DrillCadToolService(ILog log, LayerService layerService)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _layerService = layerService ?? throw new ArgumentNullException(nameof(layerService));
    }

    public DrillCheckSummary Check(IReadOnlyList<string> drillNames)
    {
        var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            MessageBox.Show("No active AutoCAD document is available.", "Check", MessageBoxButton.OK, MessageBoxImage.Information);
            return new DrillCheckSummary(completed: false, new DrillCheckResult[0], reportPath: null);
        }

        var editor = document.Editor;
        var database = document.Database;

        try
        {
            var tableOptions = new PromptEntityOptions("\nSelect the data-linked table:")
            {
                AllowObjectOnLockedLayer = true
            };
            tableOptions.SetRejectMessage("\nOnly table entities are allowed.");
            tableOptions.AddAllowedClass(typeof(Table), exactMatch: true);

            var tableResult = editor.GetEntity(tableOptions);
            if (tableResult.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\nTable selection cancelled.");
                return new DrillCheckSummary(completed: false, new DrillCheckResult[0], reportPath: null);
            }

            List<string> tableValues;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                if (transaction.GetObject(tableResult.ObjectId, OpenMode.ForRead) is not Table table)
                {
                    MessageBox.Show("Selected entity is not a valid table.", "Check", MessageBoxButton.OK, MessageBoxImage.Error);
                    return new DrillCheckSummary(completed: false, new DrillCheckResult[0], reportPath: null);
                }

                tableValues = ExtractBottomHoleValues(table);
                transaction.Commit();
            }

            if (tableValues.Count == 0)
            {
                return new DrillCheckSummary(completed: false, new DrillCheckResult[0], reportPath: null);
            }

            var blockData = SelectBlocksWithDrillName(document);
            if (blockData == null || blockData.Count == 0)
            {
                MessageBox.Show("No blocks selected or no blocks with DRILLNAME attribute found.", "Check", MessageBoxButton.OK, MessageBoxImage.Warning);
                return new DrillCheckSummary(completed: false, new DrillCheckResult[0], reportPath: null);
            }

            return CompareDrillNamesWithTable(drillNames, tableValues, blockData);
        }
        catch (System.Exception ex)
        {
            _log.Error($"Error during check operation: {ex.Message}", ex);
            MessageBox.Show($"An error occurred during the check operation: {ex.Message}", "Check", MessageBoxButton.OK, MessageBoxImage.Error);
            return new DrillCheckSummary(completed: false, new DrillCheckResult[0], reportPath: null);
        }
    }

    public void HeadingsAll(IReadOnlyList<string> drillNames, string heading)
    {
        var confirm = MessageBox.Show(
            "Are you sure you want to insert heading blocks (and DRILL blocks) for all non-default drills?",
            "Confirm HEADING ALL",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            MessageBox.Show("No active AutoCAD document is available.", "Headings All", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var editor = document.Editor;
        var database = document.Database;

        try
        {
            var headingLabel = NormalizeHeadingLabel(heading);
            var surfaceOptions = new PromptEntityOptions("\nSelect the data-linked table containing 'SURFACE':")
            {
                AllowObjectOnLockedLayer = true
            };
            surfaceOptions.SetRejectMessage("\nOnly table entities are allowed.");
            surfaceOptions.AddAllowedClass(typeof(Table), exactMatch: true);

            var surfaceResult = editor.GetEntity(surfaceOptions);
            if (surfaceResult.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\nTable selection cancelled.");
                return;
            }

            List<TableCellLocation> surfaceCells;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                if (transaction.GetObject(surfaceResult.ObjectId, OpenMode.ForRead) is not Table surfaceTable)
                {
                    MessageBox.Show("Selected entity is not a valid table.", "Headings All", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                surfaceCells = FindSurfaceCells(surfaceTable);
                transaction.Commit();
            }

            if (surfaceCells.Count == 0)
            {
                MessageBox.Show("No 'SURFACE' cells found in the selected table.", "Headings All", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var nonDefault = GetNonDefaultDrills(drillNames);
            if (nonDefault.Count == 0)
            {
                MessageBox.Show("No non-default drills to insert blocks for.", "Headings All", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (nonDefault.Count > surfaceCells.Count)
            {
                MessageBox.Show("Not enough SURFACE cells for all non-default drills.", "Headings All", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            using (var transaction = AutoCADHelper.StartTransaction())
            {
                for (var i = 0; i < nonDefault.Count; i++)
                {
                    var drillName = nonDefault[i];
                    var surfaceCell = surfaceCells[i];
                    var row = surfaceCell.Row;
                    var column = surfaceCell.Column;
                    if (transaction.GetObject(surfaceResult.ObjectId, OpenMode.ForRead) is not Table surfaceTable)
                    {
                        continue;
                    }

                    var nwCorner = GetCellNorthWest(surfaceTable, row, column);
                    var headingAttributes = new Dictionary<string, string> { { "DRILLNAME", drillName } };
                    var headingId = AutoCADHelper.InsertBlock(HeadingBlockName, nwCorner, headingAttributes, 1.0);
                    if (headingId == ObjectId.Null)
                    {
                        MessageBox.Show($"Failed to insert {HeadingBlockName} for {drillName}.", "Headings All", MessageBoxButton.OK, MessageBoxImage.Error);
                        continue;
                    }

                    var drillInsertion = new Point3d(nwCorner.X - 50.0, nwCorner.Y, nwCorner.Z);
                    var drillAttributes = new Dictionary<string, string> { { "DRILL", drillName } };
                    var drillId = AutoCADHelper.InsertBlock(DrillBlockName, drillInsertion, drillAttributes, 2.0);
                    if (drillId == ObjectId.Null)
                    {
                        MessageBox.Show($"Failed to insert DRILL block for {drillName}.", "Headings All", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }

                transaction.Commit();
            }

            MessageBox.Show($"Successfully created DRILL + {headingLabel} blocks for all non-default drills.", "Headings All", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (System.Exception ex)
        {
            _log.Error($"Exception in HeadingsAll: {ex.Message}", ex);
            MessageBox.Show($"An error occurred while inserting heading blocks: {ex.Message}", "Headings All", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void CreateXlsFromTable()
    {
        var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            MessageBox.Show("No active AutoCAD document is available.", "Create XLS", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var editor = document.Editor;
        var database = document.Database;

        try
        {
            var options = new PromptEntityOptions("\nSelect the table to export to XLS:")
            {
                AllowObjectOnLockedLayer = true
            };
            options.SetRejectMessage("\nOnly table entities are allowed.");
            options.AddAllowedClass(typeof(Table), exactMatch: true);

            var result = editor.GetEntity(options);
            if (result.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\nXLS save cancelled.");
                return;
            }

            Table table;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                table = transaction.GetObject(result.ObjectId, OpenMode.ForRead) as Table ?? throw new InvalidOperationException("Selected entity is not a table.");
                transaction.Commit();
            }

            var saveDialog = new SaveFileDialog
            {
                Filter = "Excel Files (*.xlsx)|*.xlsx|All Files (*.*)|*.*",
                FileName = "ExportedTable.xlsx",
                Title = "Save XLS File"
            };

            if (saveDialog.ShowDialog() != true)
            {
                editor.WriteMessage("\nXLS save cancelled.");
                return;
            }

            var rows = table.Rows.Count;
            var cols = table.Columns.Count;

            EpplusCompat.EnsureLicense();
            using (var package = new ExcelPackage())
            {
                var worksheet = package.Workbook.Worksheets.Add("ExportedTable");
                for (var row = 0; row < rows; row++)
                {
                    for (var col = 0; col < cols; col++)
                    {
                        var value = table.Cells[row, col].TextString.Trim();
                        worksheet.Cells[row + 1, col + 1].Value = value;
                    }
                }

                if (cols >= 3)
                {
                    worksheet.Column(1).Width = 15;
                    worksheet.Column(2).Width = 12;
                    worksheet.Column(3).Width = 12;
                    worksheet.Column(2).Style.Numberformat.Format = "0.00";
                    worksheet.Column(3).Style.Numberformat.Format = "0.00";
                }

                package.SaveAs(new FileInfo(saveDialog.FileName));
            }

            MessageBox.Show($"XLS file created successfully at:\n{saveDialog.FileName}", "Create XLS", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (System.Exception ex)
        {
            MessageBox.Show($"An error occurred while creating XLS:\n{ex.Message}", "Create XLS", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void CompleteCordsArchive(IReadOnlyList<string> drillNames, string heading)
    {
        var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            ShowAlert("No active AutoCAD document.");
            return;
        }

        var confirm = MessageBox.Show(
            "ARE YOU IN UTM?",
            "Complete CORDS (Archive)",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            EpplusCompat.EnsureLicense();

            var csvPath = DrillCsvPipeline(document.Database);
            if (string.IsNullOrEmpty(csvPath))
            {
                return;
            }

            var excelPath = RunCordsExecutable(csvPath, heading);
            if (string.IsNullOrEmpty(excelPath))
            {
                return;
            }

            var tableData = ReadExcel(excelPath);
            if (tableData == null)
            {
                return;
            }

            AdjustTableForClient(tableData, heading);

            if (!InsertTablePipeline(document, tableData))
            {
                return;
            }

            MessageBox.Show("Coordinate table created!", "Complete CORDS (Archive)", MessageBoxButton.OK, MessageBoxImage.Information);
            _log.Info("COMPLETE CORDS (Archive) succeeded.");

            HeadingsAll(drillNames, heading);
        }
        catch (System.Exception ex)
        {
            _log.Error($"Error in COMPLETE CORDS (Archive): {ex.Message}", ex);
            ShowAlert($"Error in COMPLETE CORDS (Archive): {ex.Message}");
        }
    }

    public void CompleteCords(IReadOnlyList<string> drillNames, string heading)
    {
        var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            ShowAlert("No active AutoCAD document.");
            return;
        }

        var confirm = MessageBox.Show(
            "ARE YOU IN UTM?",
            "Complete CORDS",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var gridPoints = ReadGridPoints(document.Database)
                .OrderBy(point => point.Label, _naturalComparer)
                .ToList();
            if (gridPoints.Count == 0)
            {
                ShowAlert("No drill-point labels were found on Z-DRILL-POINT.");
                return;
            }

            if (!TryResolveNativeCordsContext(document, gridPoints, out var zone, out var resolver, out var zoneDetail))
            {
                ShowAlert(zoneDetail);
                return;
            }

            if (!UtmCoordinateConverter.TryCreate(zone.ToString(CultureInfo.InvariantCulture), out var nad83Converter) || nad83Converter == null)
            {
                ShowAlert($"Could not create the NAD83 UTM converter for zone {zone}.");
                return;
            }

            if (!UtmCoordinateConverter.TryCreateNad27(zone.ToString(CultureInfo.InvariantCulture), out var nad27Converter) || nad27Converter == null)
            {
                ShowAlert($"Could not create the NAD27 UTM converter for zone {zone}.");
                return;
            }

            var rows = BuildNativeCordsRows(gridPoints, zone, resolver, nad83Converter, nad27Converter);
            var tableData = BuildNativeCordsTableData(rows);
            AdjustTableForClient(tableData, heading);

            if (!InsertTablePipeline(document, tableData))
            {
                return;
            }

            MessageBox.Show("Coordinate table created!", "Complete CORDS", MessageBoxButton.OK, MessageBoxImage.Information);
            _log.Info($"COMPLETE CORDS succeeded in zone {zone} for {rows.Count} point(s).");

            HeadingsAll(drillNames, heading);
        }
        catch (System.Exception ex)
        {
            _log.Error($"Error in COMPLETE CORDS: {ex.Message}", ex);
            ShowAlert($"Error in COMPLETE CORDS: {ex.Message}");
        }
    }

    public void CreateExistingWellTable(ExistingWellTableConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            ShowAlert("No active AutoCAD document.");
            return;
        }

        var confirm = MessageBox.Show(
            "ARE YOU IN UTM?",
            "EXISTING WELL TABLE",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var pickedPoints = PromptForExistingWellPoints(document);
            if (pickedPoints == null)
            {
                return;
            }

            if (pickedPoints.Count == 0)
            {
                document.Editor.WriteMessage("\nNo existing well points were picked.");
                return;
            }

            var gridPoints = pickedPoints
                .Select(point => new GridPointCoordinate(point.Name, point.Point.Y, point.Point.X))
                .ToList();

            if (!TryResolveExistingWellZone(document, gridPoints, configuration.Zone, out var zone, out var zoneDetail))
            {
                ShowAlert(zoneDetail);
                return;
            }

            if (!UtmCoordinateConverter.TryCreate(zone.ToString(CultureInfo.InvariantCulture), out var nad83Converter) || nad83Converter == null)
            {
                ShowAlert($"Could not create the NAD83 UTM converter for zone {zone}.");
                return;
            }

            if (!UtmCoordinateConverter.TryCreateNad27(zone.ToString(CultureInfo.InvariantCulture), out var nad27Converter) || nad27Converter == null)
            {
                ShowAlert($"Could not create the NAD27 UTM converter for zone {zone}.");
                return;
            }

            var rows = BuildExistingWellTableRows(pickedPoints, configuration.CoordinateFormat, zone, nad83Converter, nad27Converter);
            var insertionResult = document.Editor.GetPoint("\nSelect top-left insertion point for the EXISTING WELL TABLE:");
            if (insertionResult.Status != PromptStatus.OK)
            {
                document.Editor.WriteMessage("\nInsertion point cancelled.");
                return;
            }

            using (document.LockDocument())
            {
                _layerService.EnsureLayer(document.Database, NotesLayer);
                DrawExistingWellTable(document.Database, insertionResult.Value, configuration.CoordinateFormat, rows);
            }

            MessageBox.Show("Existing well table created!", "EXISTING WELL TABLE", MessageBoxButton.OK, MessageBoxImage.Information);
            _log.Info($"EXISTING WELL TABLE succeeded in zone {zone} for {rows.Count} point(s) using {configuration.CoordinateFormat}.");
        }
        catch (System.Exception ex)
        {
            _log.Error($"Error in EXISTING WELL TABLE: {ex.Message}", ex);
            ShowAlert($"Error in EXISTING WELL TABLE: {ex.Message}");
        }
    }

    private bool TryResolveNativeCordsContext(
        Document document,
        IReadOnlyList<GridPointCoordinate> gridPoints,
        out int zone,
        out AtsQuarterLocationResolver resolver,
        out string detail)
    {
        zone = 0;
        resolver = null!;
        detail = string.Empty;

        if (TryInferZoneFromResolver(document.Name, gridPoints, out zone, out resolver))
        {
            return true;
        }

        if (!TryPromptForZone(document.Editor, out zone))
        {
            detail = "Complete CORDS cancelled before a UTM zone was selected.";
            return false;
        }

        if (!AtsQuarterLocationResolver.TryCreate(zone, document.Database.Filename, out var selectedResolver) || selectedResolver == null)
        {
            detail = $"Could not load the ATS section-index resolver for UTM zone {zone}.";
            return false;
        }

        resolver = selectedResolver;
        return true;
    }

    private static bool TryInferZoneFromResolver(string? drawingPath, IReadOnlyList<GridPointCoordinate> gridPoints, out int zone, out AtsQuarterLocationResolver resolver)
    {
        zone = 0;
        resolver = null!;

        var candidates = new List<(int Zone, int Score, AtsQuarterLocationResolver Resolver)>();
        foreach (var candidateZone in new[] { 11, 12 })
        {
            if (!AtsQuarterLocationResolver.TryCreate(candidateZone, drawingPath, out var candidateResolver) || candidateResolver == null)
            {
                continue;
            }

            var score = 0;
            foreach (var point in gridPoints)
            {
                if (candidateResolver.TryResolveLsdMatch(new Point2d(point.Easting, point.Northing), out _))
                {
                    score++;
                }
            }

            if (score > 0)
            {
                candidates.Add((candidateZone, score, candidateResolver));
            }
        }

        if (candidates.Count == 0)
        {
            return false;
        }

        var bestScore = candidates.Max(candidate => candidate.Score);
        var winners = candidates.Where(candidate => candidate.Score == bestScore).ToList();
        if (winners.Count != 1)
        {
            return false;
        }

        zone = winners[0].Zone;
        resolver = winners[0].Resolver;
        return true;
    }

    private static bool TryPromptForZone(Editor editor, out int zone)
    {
        zone = 0;
        var options = new PromptKeywordOptions("\nSelect UTM zone [11/12] <12>: ")
        {
            AllowNone = true
        };
        options.Keywords.Add("11");
        options.Keywords.Add("12");
        options.Keywords.Default = "12";

        var result = editor.GetKeywords(options);
        if (result.Status == PromptStatus.None)
        {
            zone = 12;
            return true;
        }

        if (result.Status != PromptStatus.OK)
        {
            return false;
        }

        return int.TryParse(result.StringResult, NumberStyles.Integer, CultureInfo.InvariantCulture, out zone);
    }

    private bool TryResolveExistingWellZone(
        Document document,
        IReadOnlyList<GridPointCoordinate> gridPoints,
        int? configuredZone,
        out int zone,
        out string detail)
    {
        zone = 0;
        detail = string.Empty;

        if (configuredZone.HasValue)
        {
            zone = configuredZone.Value;
            if (zone is 11 or 12)
            {
                return true;
            }

            detail = "Existing Well Table only supports UTM zone 11 or 12.";
            return false;
        }

        if (TryInferZoneFromResolver(document.Name, gridPoints, out zone, out _))
        {
            return true;
        }

        detail = "Could not automatically determine the UTM zone for the picked wells. Re-run EXISTING WELL TABLE and choose zone 11 or 12.";
        return false;
    }

    private List<ExistingWellTablePoint>? PromptForExistingWellPoints(Document document)
    {
        var editor = document.Editor;
        var points = new List<ExistingWellTablePoint>();
        editor.WriteMessage("\nPick existing well-head points one at a time. Press Enter when finished.");

        while (true)
        {
            var pointOptions = new PromptPointOptions(
                points.Count == 0
                    ? "\nPick existing well-head point or press Enter to cancel:"
                    : "\nPick another existing well-head point or press Enter to finish:")
            {
                AllowNone = true
            };

            var pointResult = editor.GetPoint(pointOptions);
            if (pointResult.Status == PromptStatus.None)
            {
                return points;
            }

            if (pointResult.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\nPoint selection cancelled.");
                return null;
            }

            if (!TryPromptForExistingWellName(editor, points.Count + 1, out var name))
            {
                editor.WriteMessage("\nName entry cancelled.");
                return null;
            }

            points.Add(new ExistingWellTablePoint(name, pointResult.Value));
        }
    }

    private static bool TryPromptForExistingWellName(Editor editor, int pointIndex, out string name)
    {
        name = string.Empty;

        while (true)
        {
            var nameOptions = new PromptStringOptions($"\nEnter name for picked well #{pointIndex}:")
            {
                AllowSpaces = true
            };

            var nameResult = editor.GetString(nameOptions);
            if (nameResult.Status != PromptStatus.OK)
            {
                return false;
            }

            var trimmed = (nameResult.StringResult ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                name = trimmed;
                return true;
            }

            editor.WriteMessage("\nName cannot be blank.");
        }
    }

    private List<ExistingWellTableRow> BuildExistingWellTableRows(
        IReadOnlyList<ExistingWellTablePoint> pickedPoints,
        ExistingWellTableCoordinateFormat coordinateFormat,
        int zone,
        UtmCoordinateConverter nad83Converter,
        UtmCoordinateConverter nad27Converter)
    {
        var rows = new List<ExistingWellTableRow>(pickedPoints.Count);
        var sourceCode = $"UTM83-{zone}";
        var destinationCode = $"UTM27-{zone}";

        foreach (var pickedPoint in pickedPoints)
        {
            var nad83Point = pickedPoint.Point;
            Point3d? nad27Point = null;

            if (coordinateFormat is ExistingWellTableCoordinateFormat.Nad27Utms or ExistingWellTableCoordinateFormat.Nad27LatLong)
            {
                if (!AutoCadProjectionHelper.TryTransformUtm(
                        sourceCode,
                        destinationCode,
                        nad83Point.X,
                        nad83Point.Y,
                        out var convertedPoint,
                        out var detail))
                {
                    throw new InvalidOperationException(
                        $"Could not convert existing well '{pickedPoint.Name}' from {sourceCode} to {destinationCode}. {detail}");
                }

                nad27Point = convertedPoint;
            }

            string firstValue;
            string secondValue;
            switch (coordinateFormat)
            {
                case ExistingWellTableCoordinateFormat.Nad83Utms:
                    firstValue = FormatUtmValue(nad83Point.Y);
                    secondValue = FormatUtmValue(nad83Point.X);
                    break;
                case ExistingWellTableCoordinateFormat.Nad27Utms:
                    firstValue = FormatUtmValue(nad27Point!.Value.Y);
                    secondValue = FormatUtmValue(nad27Point.Value.X);
                    break;
                case ExistingWellTableCoordinateFormat.Nad83LatLong:
                    if (!nad83Converter.TryProject(nad83Point, out var nad83Latitude, out var nad83Longitude))
                    {
                        throw new InvalidOperationException(
                            $"Could not convert NAD83 lat/long for existing well '{pickedPoint.Name}' in UTM zone {zone}.");
                    }

                    firstValue = FormatLatLongValue(nad83Latitude);
                    secondValue = FormatLatLongValue(nad83Longitude);
                    break;
                case ExistingWellTableCoordinateFormat.Nad27LatLong:
                    if (!nad27Converter.TryProject(nad27Point!.Value, out var nad27Latitude, out var nad27Longitude))
                    {
                        throw new InvalidOperationException(
                            $"Could not convert NAD27 lat/long for existing well '{pickedPoint.Name}' in UTM zone {zone}.");
                    }

                    firstValue = FormatLatLongValue(nad27Latitude);
                    secondValue = FormatLatLongValue(nad27Longitude);
                    break;
                default:
                    throw new InvalidOperationException($"Unsupported Existing Well Table coordinate format '{coordinateFormat}'.");
            }

            rows.Add(new ExistingWellTableRow(pickedPoint.Name, firstValue, secondValue));
        }

        return rows;
    }

    private static (string FirstHeader, string SecondHeader) GetExistingWellTableHeaders(ExistingWellTableCoordinateFormat coordinateFormat)
    {
        return coordinateFormat switch
        {
            ExistingWellTableCoordinateFormat.Nad83Utms => ("NORTHING (NAD83)", "EASTING (NAD83)"),
            ExistingWellTableCoordinateFormat.Nad27Utms => ("NORTHING (NAD27)", "EASTING (NAD27)"),
            ExistingWellTableCoordinateFormat.Nad83LatLong => ("LATITUDE (NAD83)", "LONGITUDE (NAD83)"),
            ExistingWellTableCoordinateFormat.Nad27LatLong => ("LATITUDE (NAD27)", "LONGITUDE (NAD27)"),
            _ => ("NORTHING (NAD83)", "EASTING (NAD83)")
        };
    }

    private void DrawExistingWellTable(
        Database database,
        Point3d topLeftInsertionPoint,
        ExistingWellTableCoordinateFormat coordinateFormat,
        IReadOnlyList<ExistingWellTableRow> rows)
    {
        using var transaction = database.TransactionManager.StartTransaction();
        var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
        var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

        var totalRows = rows.Count + 2;
        var totalHeight = ExistingWellTableRowHeight * totalRows;
        var insertionPoint = new Point3d(
            topLeftInsertionPoint.X,
            topLeftInsertionPoint.Y - totalHeight,
            topLeftInsertionPoint.Z);
        var table = new Table
        {
            Position = insertionPoint,
            Layer = NotesLayer
        };
        var styleId = GetTableStyleId(database, transaction, ExistingWellTableStyleName);
        if (!styleId.IsNull)
        {
            table.TableStyle = styleId;
        }

        table.SetSize(totalRows, 3);
        table.Columns[0].Width = ExistingWellTableNameColumnWidth;
        table.Columns[1].Width = ExistingWellTableCoordinateColumnWidth;
        table.Columns[2].Width = ExistingWellTableCoordinateColumnWidth;

        for (var rowIndex = 0; rowIndex < totalRows; rowIndex++)
        {
            table.Rows[rowIndex].Height = ExistingWellTableRowHeight;
        }

        var headers = GetExistingWellTableHeaders(coordinateFormat);
        table.Cells[0, 0].TextString = FormatExistingWellTableHeadingText(ExistingWellTableTitle);
        table.Cells[0, 0].Alignment = CellAlignment.MiddleCenter;

        table.Cells[1, 0].TextString = "NAME";
        table.Cells[1, 1].TextString = headers.FirstHeader;
        table.Cells[1, 2].TextString = headers.SecondHeader;

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var tableRow = index + 2;
            table.Cells[tableRow, 0].TextString = row.Name;
            table.Cells[tableRow, 1].TextString = row.FirstValue;
            table.Cells[tableRow, 2].TextString = row.SecondValue;
        }

        for (var rowIndex = 0; rowIndex < totalRows; rowIndex++)
        {
            for (var columnIndex = 0; columnIndex < 3; columnIndex++)
            {
                table.Cells[rowIndex, columnIndex].Alignment = CellAlignment.MiddleCenter;
            }
        }

        modelSpace.AppendEntity(table);
        transaction.AddNewlyCreatedDBObject(table, true);

        table.MergeCells(CellRange.Create(table, 0, 0, 0, 2));
        table.GenerateLayout();
        ConfigureExistingWellTableBorders(table);
        table.RecomputeTableBlock(true);
        transaction.Commit();
    }

    private static void ConfigureExistingWellTableBorders(Table table)
    {
        RemoveAllCellBorders(table);
        var borderColor = Color.FromColorIndex(ColorMethod.ByAci, 7);

        for (var row = 1; row < table.Rows.Count; row++)
        {
            for (var column = 0; column < table.Columns.Count; column++)
            {
                SetCellBorders(table.Cells[row, column], true, borderColor);
            }
        }
    }

    private static string FormatExistingWellTableHeadingText(string value)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{{\\C{ExistingWellTableHeadingColorIndex};{EscapeMTextValue(value)}}}");
    }

    private static string EscapeMTextValue(string value)
    {
        return (value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("{", "\\{", StringComparison.Ordinal)
            .Replace("}", "\\}", StringComparison.Ordinal);
    }

    private List<NativeCordsRow> BuildNativeCordsRows(
        IReadOnlyList<GridPointCoordinate> gridPoints,
        int zone,
        AtsQuarterLocationResolver resolver,
        UtmCoordinateConverter nad83Converter,
        UtmCoordinateConverter nad27Converter)
    {
        var rows = new List<NativeCordsRow>(gridPoints.Count);
        var sourceCode = $"UTM83-{zone}";
        var destinationCode = $"UTM27-{zone}";

        foreach (var point in gridPoints)
        {
            var pointLabel = NormalizePointLabel(point.Label);
            var nad83Point = new Point3d(point.Easting, point.Northing, 0.0);
            var point2d = new Point2d(point.Easting, point.Northing);

            if (!resolver.TryResolveLsdMatch(point2d, out var lsdMatch))
            {
                throw new InvalidOperationException(
                    $"Could not resolve ATS location data for drill point {pointLabel}.");
            }

            if (!nad83Converter.TryProject(nad83Point, out var nad83Latitude, out var nad83Longitude))
            {
                throw new InvalidOperationException(
                    $"Could not convert NAD83 lat/long for drill point {pointLabel} in UTM zone {zone}.");
            }

            if (!AutoCadProjectionHelper.TryTransformUtm(sourceCode, destinationCode, point.Easting, point.Northing, out var nad27Point, out var nad27Detail))
            {
                throw new InvalidOperationException(
                    $"Could not convert {pointLabel} from {sourceCode} to {destinationCode}. {nad27Detail}");
            }

            if (!nad27Converter.TryProject(nad27Point, out var nad27Latitude, out var nad27Longitude))
            {
                throw new InvalidOperationException(
                    $"Could not convert NAD27 lat/long for drill point {pointLabel} in UTM zone {zone}.");
            }

            rows.Add(new NativeCordsRow(
                pointLabel,
                FormatNativeLocation(lsdMatch),
                lsdMatch.Metes,
                lsdMatch.Bounds,
                point.Northing,
                point.Easting,
                nad83Latitude,
                nad83Longitude,
                nad27Point.Y,
                nad27Point.X,
                nad27Latitude,
                nad27Longitude));
        }

        return rows;
    }

    private static string[,] BuildNativeCordsTableData(IReadOnlyList<NativeCordsRow> rows)
    {
        var rowCount = rows.Count;
        if (rowCount == 0)
        {
            return new string[0, 0];
        }

        var displayRows = rowCount;
        for (var i = 0; i < rowCount - 1; i++)
        {
            var currentLetter = GetGroupLetter(rows[i].PointLabel);
            var nextLetter = GetGroupLetter(rows[i + 1].PointLabel);
            if (currentLetter != '\0' && nextLetter != '\0' && currentLetter != nextLetter)
            {
                displayRows++;
            }
        }

        var data = new string[displayRows, 12];
        var outputRow = 0;
        for (var i = 0; i < rowCount; i++)
        {
            var row = rows[i];
            data[outputRow, 0] = row.PointLabel;
            data[outputRow, 1] = row.Location;
            data[outputRow, 2] = row.Metes;
            data[outputRow, 3] = row.Bounds;
            data[outputRow, 4] = FormatUtmValue(row.Nad83Northing);
            data[outputRow, 5] = FormatUtmValue(row.Nad83Easting);
            data[outputRow, 6] = FormatLatLongValue(row.Nad83Latitude);
            data[outputRow, 7] = FormatLatLongValue(row.Nad83Longitude);
            data[outputRow, 8] = FormatUtmValue(row.Nad27Northing);
            data[outputRow, 9] = FormatUtmValue(row.Nad27Easting);
            data[outputRow, 10] = FormatLatLongValue(row.Nad27Latitude);
            data[outputRow, 11] = FormatLatLongValue(row.Nad27Longitude);
            outputRow++;

            if (i >= rowCount - 1)
            {
                continue;
            }

            var currentLetter = GetGroupLetter(row.PointLabel);
            var nextLetter = GetGroupLetter(rows[i + 1].PointLabel);
            if (currentLetter != '\0' && nextLetter != '\0' && currentLetter != nextLetter)
            {
                outputRow++;
            }
        }

        return data;
    }

    private static char GetGroupLetter(string pointLabel)
    {
        var normalized = NormalizePointLabel(pointLabel);
        return normalized.Length == 0 ? '\0' : char.ToUpperInvariant(normalized[0]);
    }

    private static string NormalizePointLabel(string pointLabel)
    {
        return (pointLabel ?? string.Empty).Trim().ToUpperInvariant();
    }

    private static string FormatNativeLocation(AtsQuarterLocationResolver.LsdMatch match)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{FormatLocationNumber(match.Lsd.ToString(CultureInfo.InvariantCulture), 2)}-" +
            $"{FormatLocationNumber(match.Section, 2)}-" +
            $"{FormatLocationNumber(match.Township, 3)}-" +
            $"{FormatLocationNumber(match.Range, 2)}W" +
            $"{FormatLocationNumber(match.Meridian, 1)}");
    }

    private static string FormatLocationNumber(string token, int minimumWidth)
    {
        var trimmed = (token ?? string.Empty).Trim();
        if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value.ToString(new string('0', minimumWidth), CultureInfo.InvariantCulture);
        }

        return trimmed;
    }

    private static string FormatUtmValue(double value)
    {
        return value.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static string FormatLatLongValue(double value)
    {
        return value.ToString("0.000000", CultureInfo.InvariantCulture);
    }

    public void GetUtms()
    {
        var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            ShowAlert("No active AutoCAD document.");
            return;
        }

        var confirm = MessageBox.Show(
            "ARE YOU IN UTM?",
            "Get UTMs",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var database = document.Database;
            var points = ReadGridPoints(database);
            points = points.OrderBy(p => p.Label, _naturalComparer).ToList();

            Directory.CreateDirectory(CordsDirectory);
            var csvPath = Path.Combine(CordsDirectory, "CORDS.csv");
            using (var writer = new StreamWriter(csvPath, false))
            {
                writer.WriteLine("Label,Northing,Easting");
                foreach (var point in points)
                {
                    writer.WriteLine($"{point.Label},{point.Northing},{point.Easting}");
                }
            }

            MessageBox.Show($"UTM CSV created successfully at:\n{csvPath}", "Get UTMs", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (System.Exception ex)
        {
            _log.Error($"Error generating UTMs CSV: {ex.Message}", ex);
            MessageBox.Show($"Error generating UTMs CSV: {ex.Message}", "Get UTMs", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void AddDrillPoints()
    {
        var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            ShowAlert("No active AutoCAD document.");
            return;
        }

        var editor = document.Editor;
        var promptOptions = new PromptStringOptions("\nEnter letter for drill points:")
        {
            AllowSpaces = false,
            DefaultValue = "A"
        };
        var promptResult = editor.GetString(promptOptions);
        if (promptResult.Status != PromptStatus.OK)
        {
            editor.WriteMessage("\nOperation cancelled.");
            return;
        }

        var letter = promptResult.StringResult.Trim();
        if (string.IsNullOrWhiteSpace(letter))
        {
            MessageBox.Show("Letter cannot be empty.", "Add Drill Points", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var database = document.Database;

        var options = new PromptEntityOptions("\nSelect a polyline:");
        options.SetRejectMessage("\nOnly polylines are allowed.");
        options.AddAllowedClass(typeof(Polyline), false);

        var result = editor.GetEntity(options);
        if (result.Status != PromptStatus.OK)
        {
            editor.WriteMessage("\nSelection cancelled.");
            return;
        }

        try
        {
            using (document.LockDocument())
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                _layerService.EnsureLayer(database, DrillPointsLayer);

                var polyline = transaction.GetObject(result.ObjectId, OpenMode.ForRead) as Polyline;
                if (polyline == null)
                {
                    MessageBox.Show("Selected entity is not a polyline.", "Add Drill Points", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var count = Math.Min(polyline.NumberOfVertices, 150);
                var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                for (var i = 0; i < count; i++)
                {
                    var point2d = polyline.GetPoint2dAt(i);
                    var point3d = new Point3d(point2d.X, point2d.Y, 0);
                    var label = $"{letter.Trim()}{i + 1}";

                    var text = new DBText
                    {
                        Position = point3d,
                        Height = 2.0,
                        TextString = label,
                        Layer = DrillPointsLayer,
                        ColorIndex = 7
                    };

                    modelSpace.AppendEntity(text);
                    transaction.AddNewlyCreatedDBObject(text, true);
                }

                transaction.Commit();
            }

            MessageBox.Show("Drill points added successfully.", "Add Drill Points", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (System.Exception ex)
        {
            _log.Error($"Error in AddDrillPoints: {ex.Message}", ex);
            MessageBox.Show($"Error: {ex.Message}", "Add Drill Points", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public void AddOffsets()
    {
        var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            ShowAlert("No active AutoCAD document.");
            return;
        }

        var confirm = MessageBox.Show(
            "ARE YOU IN GROUND?",
            "Add Offsets",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var database = document.Database;
            var textEntities = GetEntitiesOnLayer(database, DrillPointsLayer, typeof(DBText), typeof(MText)).ToList();
            var gridPoints = new List<LabeledPoint>();
            var gridRegex = new Regex("^[A-Z][1-9][0-9]{0,2}$", RegexOptions.IgnoreCase);

            foreach (var entity in textEntities)
            {
                switch (entity)
                {
                    case DBText dbText when gridRegex.IsMatch(dbText.TextString.Trim()):
                        gridPoints.Add(new LabeledPoint(dbText.TextString.Trim(), dbText.Position));
                        break;
                    case MText mText when gridRegex.IsMatch(mText.Contents.Trim()):
                        gridPoints.Add(new LabeledPoint(mText.Contents.Trim(), mText.Location));
                        break;
                }
            }

            if (gridPoints.Count == 0)
            {
                ShowAlert("No Z-DRILL-POINT labels found.");
                return;
            }

            var curves = GetEntitiesOnLayer(database, HorizontalLayer, typeof(Line), typeof(Polyline), typeof(Polyline2d), typeof(Polyline3d))
                .OfType<Curve>()
                .ToList();
            if (curves.Count == 0)
            {
                ShowAlert("No L-SEC-HB polylines/lines found.");
                return;
            }

            var tolerance = new Tolerance(1e-3, 1e-3);
            var warnings = new List<string>();

            using (document.LockDocument())
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                _layerService.EnsureLayer(database, OffsetsLayer);

                var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                bool OffsetExists(Point3d first, Point3d second)
                {
                    foreach (ObjectId id in modelSpace)
                    {
                        if (transaction.GetObject(id, OpenMode.ForRead) is Line line &&
                            line.Layer.Equals(OffsetsLayer, StringComparison.OrdinalIgnoreCase))
                        {
                            if ((line.StartPoint.IsEqualTo(first, tolerance) && line.EndPoint.IsEqualTo(second, tolerance)) ||
                                (line.StartPoint.IsEqualTo(second, tolerance) && line.EndPoint.IsEqualTo(first, tolerance)))
                            {
                                return true;
                            }
                        }
                    }

                    return false;
                }

                void DrawOffset(Point3d from, Point3d to)
                {
                    if (OffsetExists(from, to))
                    {
                        return;
                    }

                    var distance = from.DistanceTo(to);
                    var distanceText = distance.ToString("0.0", CultureInfo.InvariantCulture);

                    var line = new Line(from, to)
                    {
                        Layer = OffsetsLayer
                    };
                    modelSpace.AppendEntity(line);
                    transaction.AddNewlyCreatedDBObject(line, true);

                    var midPoint = new Point3d((from.X + to.X) / 2.0, (from.Y + to.Y) / 2.0, (from.Z + to.Z) / 2.0);
                    var text = new MText
                    {
                        Location = midPoint,
                        TextHeight = 2.5,
                        Contents = $"{{\\C1;{distanceText}}}",
                        Layer = OffsetsLayer
                    };
                    modelSpace.AppendEntity(text);
                    transaction.AddNewlyCreatedDBObject(text, true);

                    document.SendStringToExecute("DIMPERP ", true, false, false);
                }

                foreach (var gridPoint in gridPoints)
                {
                    var label = gridPoint.Label;
                    var point = gridPoint.Point;
                    Curve? northSouth = null;
                    Curve? eastWest = null;
                    Point3d northSouthClosest = Point3d.Origin;
                    Point3d eastWestClosest = Point3d.Origin;
                    double northSouthDelta = double.MaxValue;
                    double eastWestDelta = double.MaxValue;

                    foreach (var curve in curves)
                    {
                        var closest = curve.GetClosestPointTo(point, false);
                        var distance = point.DistanceTo(closest);
                        if (distance > 830.0)
                        {
                            continue;
                        }

                        var dx = Math.Abs(point.X - closest.X);
                        var dy = Math.Abs(point.Y - closest.Y);

                        if (dx < northSouthDelta)
                        {
                            northSouthDelta = dx;
                            northSouth = curve;
                            northSouthClosest = closest;
                        }

                        if (dy < eastWestDelta)
                        {
                            eastWestDelta = dy;
                            eastWest = curve;
                            eastWestClosest = closest;
                        }
                    }

                    var nsMade = false;
                    var ewMade = false;

                    if (northSouth != null)
                    {
                        DrawOffset(point, northSouthClosest);
                        nsMade = true;
                    }

                    if (eastWest != null)
                    {
                        DrawOffset(point, eastWestClosest);
                        ewMade = true;
                    }

                    if (!nsMade)
                    {
                        warnings.Add($"{label} (N-S)");
                    }

                    if (!ewMade)
                    {
                        warnings.Add($"{label} (E-W)");
                    }
                }

                transaction.Commit();
            }

            if (warnings.Count > 0)
            {
                ShowAlert("Unable to find L-SEC-HB for:\n  • " + string.Join("\n  • ", warnings));
            }
            else
            {
                ShowAlert("Add Offsets complete.");
            }
        }
        catch (System.Exception ex)
        {
            _log.Error($"AddOffsets: {ex.Message}", ex);
            ShowAlert($"Error: {ex.Message}");
        }
    }

    public void UpdateOffsets()
    {
        var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            MessageBox.Show("No active AutoCAD document.", "Update Offsets", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var confirm = MessageBox.Show(
            "ARE YOU IN GROUND?",
            "Update Offsets",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes)
        {
            return;
        }

        var editor = document.Editor;
        var database = document.Database;

        try
        {
            var options = new PromptEntityOptions("\nSelect the table to update offsets:")
            {
                AllowObjectOnLockedLayer = true
            };
            options.SetRejectMessage("\nOnly table entities are allowed.");
            options.AddAllowedClass(typeof(Table), exactMatch: true);

            var result = editor.GetEntity(options);
            if (result.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\nUpdate Offsets canceled by user.\n");
                return;
            }

            using (document.LockDocument())
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                if (transaction.GetObject(result.ObjectId, OpenMode.ForRead) is not Table table)
                {
                    MessageBox.Show("The selected entity is not a table.", "Update Offsets", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                var columnsToUpdate = new[] { 2, 3 }; // C, D
                if (table.Columns.Count <= columnsToUpdate.Max())
                {
                    MessageBox.Show("The selected table does not contain columns C and D.", "Update Offsets", MessageBoxButton.OK, MessageBoxImage.Warning);
                    _log.Info("Update Offsets: Table missing columns C/D.");
                    return;
                }

                // -----------------------------
                // Local helpers (self-contained)
                // -----------------------------
                var tagRegex = new Regex("^[A-Z][1-9][0-9]*$", RegexOptions.IgnoreCase);

                string Clean(string? text)
                {
                    var cleaned = Regex.Replace(text ?? string.Empty, @"\{.*?;", string.Empty);
                    cleaned = cleaned.Replace("}", string.Empty);
                    cleaned = cleaned.Replace("\\P", " ");
                    return cleaned.Trim();
                }

                string NormalizeKey(string? text)
                {
                    var cleaned = Clean(text);
                    return cleaned.ToUpperInvariant().Replace(" ", string.Empty);
                }

                bool RowHasAnyText(int row)
                {
                    for (var col = 0; col < table.Columns.Count; col++)
                    {
                        if (!string.IsNullOrWhiteSpace(Clean(table.Cells[row, col].TextString)))
                        {
                            return true;
                        }
                    }
                    return false;
                }

                bool TryGetGridTag(string? text, out string tag)
                {
                    tag = string.Empty;
                    var cleaned = Clean(text);
                    if (string.IsNullOrWhiteSpace(cleaned))
                    {
                        return false;
                    }

                    var parts = cleaned.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var part in parts)
                    {
                        var token = part.Trim();
                        if (tagRegex.IsMatch(token))
                        {
                            tag = token.ToUpperInvariant();
                            return true;
                        }
                    }

                    return false;
                }

                bool TryParseTag(string tag, out char letter, out int number)
                {
                    letter = '\0';
                    number = 0;

                    if (string.IsNullOrWhiteSpace(tag) || tag.Length < 2)
                    {
                        return false;
                    }

                    letter = char.ToUpperInvariant(tag[0]);
                    if (letter < 'A' || letter > 'Z')
                    {
                        return false;
                    }

                    if (!int.TryParse(tag.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
                    {
                        return false;
                    }

                    return true;
                }

                char? ExtractDirectionLetter(string? cellText)
                {
                    var cleaned = Clean(cellText);
                    if (string.IsNullOrWhiteSpace(cleaned))
                    {
                        return null;
                    }

                    for (var i = cleaned.Length - 1; i >= 0; i--)
                    {
                        var c = cleaned[i];
                        var upper = char.ToUpperInvariant(c);
                        if (upper == 'N' || upper == 'S' || upper == 'E' || upper == 'W')
                        {
                            return c; // preserve original casing
                        }
                    }

                    return null;
                }

                // ----------------------------------------------------
                // 1) Read drill point anchors by tag from Z-DRILL-POINT
                // ----------------------------------------------------
                var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                var drillPointByTag = new Dictionary<string, Point3d>(StringComparer.OrdinalIgnoreCase);

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

                    switch (entity)
                    {
                        case DBText dbText:
                            if (TryGetGridTag(dbText.TextString, out var dbTag) && !drillPointByTag.ContainsKey(dbTag))
                            {
                                drillPointByTag[dbTag] = dbText.Position;
                            }
                            break;

                        case MText mText:
                            if (TryGetGridTag(mText.Contents, out var mtTag) && !drillPointByTag.ContainsKey(mtTag))
                            {
                                drillPointByTag[mtTag] = mText.Location;
                            }
                            break;
                    }
                }

                if (drillPointByTag.Count == 0)
                {
                    MessageBox.Show($"No drill point labels found on layer \"{DrillPointsLayer}\".", "Update Offsets", MessageBoxButton.OK, MessageBoxImage.Warning);
                    _log.Info("Update Offsets: No Z-DRILL-POINT labels found.");
                    return;
                }

                // Available letters in the drawing (A, B, C...) – used to map each SURFACE..BOTTOM HOLE block.
                var lettersAvailable = drillPointByTag.Keys
                    .Select(tag =>
                    {
                        char letter;
                        int number;
                        var ok = TryParseTag(tag, out letter, out number);
                        return new { Ok = ok, Letter = letter };
                    })
                    .Where(p => p.Ok)
                    .Select(p => p.Letter)
                    .Distinct()
                    .OrderBy(c => c)
                    .ToList();

                if (lettersAvailable.Count == 0)
                {
                    MessageBox.Show("No valid drill point tags (e.g., A1, B2) were found on Z-DRILL-POINT.", "Update Offsets", MessageBoxButton.OK, MessageBoxImage.Warning);
                    _log.Info("Update Offsets: Drill point tags existed but none parsed as A1/B2 style.");
                    return;
                }

                // ------------------------------------------------------------
                // 2) Build row -> expected tag mapping using SURFACE/BOTTOM HOLE
                // ------------------------------------------------------------
                var groups = new List<List<int>>();
                List<int>? currentGroup = null;

                for (var row = 0; row < table.Rows.Count; row++)
                {
                    var col0 = NormalizeKey(table.Cells[row, 0].TextString);
                    var isSurface = col0.Contains("SURFACE");
                    var isBottomHole = col0.Contains("BOTTOMHOLE");

                    if (isSurface)
                    {
                        if (currentGroup != null && currentGroup.Count > 0)
                        {
                            groups.Add(currentGroup);
                        }

                        currentGroup = new List<int>();
                    }

                    if (currentGroup != null)
                    {
                        if (RowHasAnyText(row))
                        {
                            currentGroup.Add(row);
                        }

                        if (isBottomHole)
                        {
                            groups.Add(currentGroup);
                            currentGroup = null;
                        }
                    }
                }

                if (currentGroup != null && currentGroup.Count > 0)
                {
                    groups.Add(currentGroup);
                }

                // Fallback if SURFACE/BOTTOM HOLE blocks weren't found:
                // map “offset-looking rows” in order to tags in order.
                var expectedTagByRow = new Dictionary<int, string>();
                if (groups.Count == 0)
                {
                    var orderedTags = drillPointByTag.Keys
                        .Select(tag =>
                        {
                            char letter;
                            int number;
                            var ok = TryParseTag(tag, out letter, out number);
                            return new { Tag = tag, Ok = ok, Letter = letter, Number = number };
                        })
                        .Where(x => x.Ok)
                        .OrderBy(x => x.Letter)
                        .ThenBy(x => x.Number)
                        .Select(x => x.Tag)
                        .ToList();

                    var candidateRows = new List<int>();
                    for (var row = 0; row < table.Rows.Count; row++)
                    {
                        var cDir = ExtractDirectionLetter(table.Cells[row, columnsToUpdate[0]].TextString);
                        var dDir = ExtractDirectionLetter(table.Cells[row, columnsToUpdate[1]].TextString);
                        if ((cDir.HasValue || dDir.HasValue) && RowHasAnyText(row))
                        {
                            candidateRows.Add(row);
                        }
                    }

                    var count = Math.Min(candidateRows.Count, orderedTags.Count);
                    for (var i = 0; i < count; i++)
                    {
                        expectedTagByRow[candidateRows[i]] = orderedTags[i];
                    }
                }
                else
                {
                    for (var groupIndex = 0; groupIndex < groups.Count; groupIndex++)
                    {
                        char letter;
                        if (groupIndex < lettersAvailable.Count)
                        {
                            letter = lettersAvailable[groupIndex];
                        }
                        else
                        {
                            // If the table has more groups than letters found, continue after the last known letter.
                            var last = lettersAvailable[lettersAvailable.Count - 1];
                            var offset = groupIndex - lettersAvailable.Count + 1;
                            letter = (char)(last + offset);
                        }

                        var rowNumber = 1;
                        foreach (var row in groups[groupIndex])
                        {
                            expectedTagByRow[row] = $"{letter}{rowNumber}";
                            rowNumber++;
                        }
                    }
                }

                if (expectedTagByRow.Count == 0)
                {
                    MessageBox.Show(
                        "Could not determine any rows to update.\n" +
                        "Expected SURFACE..BOTTOM HOLE groups in column A, or offset rows with N/S/E/W in columns C/D.",
                        "Update Offsets",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);

                    _log.Info("Update Offsets: No expectedTagByRow entries created.");
                    return;
                }

                // Map row -> anchor point (only if that tag exists in the drawing)
                var anchorPointByRow = new Dictionary<int, Point3d>();
                foreach (var kvp in expectedTagByRow)
                {
                    if (drillPointByTag.TryGetValue(kvp.Value, out var pt))
                    {
                        anchorPointByRow[kvp.Key] = pt;
                    }
                }

                // ----------------------------------------
                // 3) Collect offset lines on P-Drill-Offset
                // ----------------------------------------
                var offsetLines = new List<Line>();
                foreach (ObjectId entityId in modelSpace)
                {
                    if (transaction.GetObject(entityId, OpenMode.ForRead) is not Entity entity)
                    {
                        continue;
                    }

                    if (entity.Layer.Equals(OffsetsLayer, StringComparison.OrdinalIgnoreCase) && entity is Line line)
                    {
                        offsetLines.Add(line);
                    }
                }

                if (offsetLines.Count == 0)
                {
                    MessageBox.Show($"No offset lines found on layer \"{OffsetsLayer}\".", "Update Offsets", MessageBoxButton.OK, MessageBoxImage.Information);
                    _log.Info("Update Offsets: No offset lines on P-Drill-Offset layer.");
                    return;
                }

                // ----------------------------------------------------
                // 4) For each anchor row, find its 2 connected lines
                // ----------------------------------------------------
                var offsetsByRow = new Dictionary<int, OffsetMatch>();
                foreach (var row in expectedTagByRow.Keys)
                {
                    offsetsByRow[row] = new OffsetMatch();
                }

                const double pointTolerance = 1.0;

                foreach (var line in offsetLines)
                {
                    foreach (var entry in anchorPointByRow)
                    {
                        var row = entry.Key;
                        var anchorPoint = entry.Value;

                        var startDistance = line.StartPoint.DistanceTo(anchorPoint);
                        var endDistance = line.EndPoint.DistanceTo(anchorPoint);
                        var minDistance = Math.Min(startDistance, endDistance);

                        if (minDistance > pointTolerance)
                        {
                            continue;
                        }

                        // Orientation decides C vs D; direction letter is preserved from the table cell.
                        var dx = Math.Abs(line.EndPoint.X - line.StartPoint.X);
                        var dy = Math.Abs(line.EndPoint.Y - line.StartPoint.Y);
                        var isNorthSouth = dy >= dx;

                        var match = offsetsByRow[row];
                        if (isNorthSouth)
                        {
                            if (!match.NorthSouthDistance.HasValue || line.Length < match.NorthSouthDistance.Value)
                            {
                                match.NorthSouthDistance = line.Length;
                            }
                        }
                        else
                        {
                            if (!match.EastWestDistance.HasValue || line.Length < match.EastWestDistance.Value)
                            {
                                match.EastWestDistance = line.Length;
                            }
                        }

                        offsetsByRow[row] = match;
                    }
                }

                // ------------------------
                // 5) Update table cells C/D
                // ------------------------
                table.UpgradeOpen();

                var updatedCount = 0;
                var noMatchCount = 0;

                foreach (var row in expectedTagByRow.Keys.OrderBy(r => r))
                {
                    offsetsByRow.TryGetValue(row, out var match);

                    for (var i = 0; i < columnsToUpdate.Length; i++)
                    {
                        var col = columnsToUpdate[i];
                        var originalText = table.Cells[row, col].TextString;

                        var isNorthSouth = col == columnsToUpdate[0];
                        var distance = isNorthSouth ? match?.NorthSouthDistance : match?.EastWestDistance;

                        // Direction must come from the original cell, always.
                        var direction = ExtractDirectionLetter(originalText);

                        if (distance.HasValue && direction.HasValue)
                        {
                            table.Cells[row, col].TextString =
                                distance.Value.ToString("F1", CultureInfo.InvariantCulture) + " " + direction.Value;
                            updatedCount++;
                        }
                        else
                        {
                            // keep original text, but mark as problem
                            table.Cells[row, col].TextString = originalText;
                            table.Cells[row, col].BackgroundColor = Color.FromRgb(255, 0, 0);
                            noMatchCount++;
                        }
                    }
                }

                table.GenerateLayout();
                transaction.Commit();

                editor.WriteMessage($"\nUpdate Offsets completed: {updatedCount} cells updated, {noMatchCount} cells with no match.\n");
                MessageBox.Show($"Update Offsets completed: {updatedCount} cells updated, {noMatchCount} cells with no match.", "Update Offsets", MessageBoxButton.OK, MessageBoxImage.Information);
                _log.Info($"Update Offsets done => {updatedCount} updated, {noMatchCount} no-match cells.");
            }
        }
        catch (System.Exception ex)
        {
            _log.Error($"UpdateOffsets: {ex.Message}", ex);
            MessageBox.Show($"Error updating offsets: {ex.Message}", "Update Offsets", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private sealed class OffsetMatch
    {
        public double? NorthSouthDistance { get; set; }
        public char? NorthSouthDirection { get; set; }
        public double? EastWestDistance { get; set; }
        public char? EastWestDirection { get; set; }
    }

    private static bool TryParseTableDouble(string? text, out double value)
    {
        value = 0;
        var cleaned = Regex.Replace(text ?? string.Empty, @"\{.*?;", string.Empty);
        cleaned = cleaned.Replace("}", string.Empty).Trim();
        return double.TryParse(
            cleaned,
            NumberStyles.Float | NumberStyles.AllowThousands,
            CultureInfo.InvariantCulture,
            out value);
    }

    // Used by UpdateOffsets for coordinate cells (E/F): tolerate spaces/NBSP as thousands separators.
    private static bool TryParseTableCoordinateDouble(string? text, out double value)
    {
        value = 0;

        var cleaned = Regex.Replace(text ?? string.Empty, @"\{.*?;", string.Empty);
        cleaned = cleaned.Replace("}", string.Empty).Trim();

        cleaned = cleaned.Replace(" ", string.Empty)
            .Replace("\u00A0", string.Empty); // non-breaking space

        if (double.TryParse(cleaned, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        if (double.TryParse(cleaned, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value))
        {
            return true;
        }

        var normalized = cleaned;
        if (normalized.Count(c => c == ',') == 1 && !normalized.Contains('.'))
        {
            normalized = normalized.Replace(',', '.');
            if (double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }

        normalized = Regex.Replace(cleaned, @"[^0-9\.\-]", string.Empty);
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    // Pull the direction letter from the existing cell text (C/D) so we can preserve it.
    private static char? TryExtractDirectionLetter(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var cleaned = Regex.Replace(text, @"\{.*?;", string.Empty);
        cleaned = cleaned.Replace("}", string.Empty).Trim();

        for (var i = cleaned.Length - 1; i >= 0; i--)
        {
            var c = cleaned[i];
            var upper = char.ToUpperInvariant(c);
            if (upper == 'N' || upper == 'S' || upper == 'E' || upper == 'W')
            {
                return c; // preserve original casing
            }
        }

        return null;
    }

    private static List<string> ExtractBottomHoleValues(Table table)
    {
        var results = new List<string>();
        for (var row = 0; row < table.Rows.Count; row++)
        {
            var columnValue = table.Cells[row, 0].TextString.Trim();
            var normalized = columnValue.ToUpperInvariant().Replace(" ", string.Empty);
            if (normalized.Contains("BOTTOMHOLE"))
            {
                var value = table.Cells[row, 1].TextString.Trim();
                results.Add(value);
            }
        }

        if (results.Count == 0)
        {
            MessageBox.Show("No 'BOTTOM HOLE' entries found in the selected table.", "Check", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        return results;
    }

    private List<BlockAttributeData>? SelectBlocksWithDrillName(Document document)
    {
        try
        {
            var editor = document.Editor;
            var database = document.Database;

            var options = new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect blocks with 'DRILLNAME' attribute:",
                AllowDuplicates = false
            };

            var filter = new SelectionFilter(new[] { new TypedValue((int)DxfCode.Start, "INSERT") });
            var selectionResult = editor.GetSelection(options, filter);
            if (selectionResult.Status != PromptStatus.OK)
            {
                return null;
            }

            var results = new List<BlockAttributeData>();
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject selected in selectionResult.Value)
                {
                    if (selected == null)
                    {
                        continue;
                    }

                    if (transaction.GetObject(selected.ObjectId, OpenMode.ForRead) is not BlockReference block)
                    {
                        continue;
                    }

                    var drillName = GetAttributeValue(block, "DRILLNAME", transaction);
                    if (string.IsNullOrEmpty(drillName))
                    {
                        continue;
                    }

                    results.Add(new BlockAttributeData
                    {
                        BlockReference = null,
                        DrillName = drillName,
                        YCoordinate = block.Position.Y
                    });
                }

                transaction.Commit();
            }

            return results;
        }
        catch (System.Exception ex)
        {
            _log.Error($"Error in SelectBlocksWithDrillName: {ex.Message}", ex);
            return null;
        }
    }

    private static string GetAttributeValue(BlockReference blockReference, string tag, Transaction transaction)
    {
        foreach (ObjectId id in blockReference.AttributeCollection)
        {
            if (transaction.GetObject(id, OpenMode.ForRead) is AttributeReference attribute &&
                AutoCADBlockService.TagMatches(attribute.Tag, tag))
            {
                return attribute.TextString.Trim();
            }
        }

        return string.Empty;
    }

    private DrillCheckSummary CompareDrillNamesWithTable(IReadOnlyList<string> drillNames, IReadOnlyList<string> tableValues, List<BlockAttributeData> blockData)
    {
        blockData.Sort((a, b) => b.YCoordinate.CompareTo(a.YCoordinate));

        var comparisons = Math.Min(Math.Min(tableValues.Count, drillNames.Count), blockData.Count);
        var discrepancies = new List<string>();
        var reportLines = new List<string>();
        var results = new List<DrillCheckResult>();

        for (var i = 0; i < comparisons; i++)
        {
            var tableValue = tableValues[i];
            var drillName = (drillNames[i] ?? string.Empty).Trim();
            var blockDrillName = blockData[i].DrillName;

            var normalizedDrillName = DrillParsers.NormalizeDrillName(drillName);
            var normalizedTableValue = DrillParsers.NormalizeTableValue(tableValue);
            var normalizedBlockName = DrillParsers.NormalizeDrillName(blockDrillName);

            var mismatch = false;
            var details = new List<string>();

            if (!string.Equals(normalizedDrillName, normalizedTableValue, StringComparison.OrdinalIgnoreCase))
            {
                details.Add("Drill name does not match table value.");
                mismatch = true;
            }

            if (!string.Equals(normalizedBlockName, normalizedDrillName, StringComparison.OrdinalIgnoreCase))
            {
                details.Add("Block DRILLNAME does not match drill name.");
                mismatch = true;
            }

            if (!string.Equals(normalizedBlockName, normalizedTableValue, StringComparison.OrdinalIgnoreCase))
            {
                details.Add("Block DRILLNAME does not match table value.");
                mismatch = true;
            }

            reportLines.Add($"DRILL_{i + 1} NAME: {drillName}");
            reportLines.Add($"TABLE RESULT: {tableValue}");
            reportLines.Add($"BLOCK DRILLNAME: {blockDrillName}");
            reportLines.Add($"Normalized Drill Name: {normalizedDrillName}");
            reportLines.Add($"Normalized Table Value: {normalizedTableValue}");
            reportLines.Add($"Normalized Block DrillName: {normalizedBlockName}");
            reportLines.Add($"STATUS: {(mismatch ? "FAIL" : "PASS")}");

            if (mismatch)
            {
                reportLines.Add("Discrepancies:");
                foreach (var detail in details)
                {
                    reportLines.Add($"- {detail}");
                }

                discrepancies.Add($"DRILL_{i + 1}: {string.Join("; ", details)}");
            }

            results.Add(new DrillCheckResult(i + 1, drillName, tableValue, blockDrillName, details));
            reportLines.Add(string.Empty);
        }

        var reportPath = GetReportFilePath();
        try
        {
            File.WriteAllLines(reportPath, reportLines);
        }
        catch (System.Exception ex)
        {
            _log.Error($"Error writing report file: {ex.Message}", ex);
            MessageBox.Show($"An error occurred while writing the report file: {ex.Message}", "Check", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        if (discrepancies.Count > 0)
        {
            var message = "Discrepancies found:\n" + string.Join("\n", discrepancies);
            MessageBox.Show($"{message}\n\nDetailed report saved at:\n{reportPath}", "Check Results", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            MessageBox.Show($"All drill names match the table values and block DRILLNAME attributes.\n\nDetailed report saved at:\n{reportPath}", "Check Results", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        return new DrillCheckSummary(completed: true, results, reportPath);
    }
    private static string GetReportFilePath()
    {
        var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
        if (document == null || string.IsNullOrEmpty(document.Name))
        {
            throw new InvalidOperationException("No active AutoCAD document found.");
        }

        var directory = Path.GetDirectoryName(document.Name) ?? CordsDirectory;
        var drawingName = Path.GetFileNameWithoutExtension(document.Name);
        return Path.Combine(directory, $"{drawingName}_CheckReport.txt");
    }

    private List<GridPointCoordinate> ReadGridPoints(Database database)
    {
        var results = new List<GridPointCoordinate>();

        using (var transaction = database.TransactionManager.StartTransaction())
        {
            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId id in modelSpace)
            {
                if (transaction.GetObject(id, OpenMode.ForRead) is Entity entity &&
                    entity.Layer.Equals(DrillPointsLayer, StringComparison.OrdinalIgnoreCase))
                {
                    switch (entity)
                    {
                        case DBText text when DrillParsers.IsGridLabel(text.TextString):
                            results.Add(new GridPointCoordinate(text.TextString.Trim(), text.Position.Y, text.Position.X));
                            break;
                        case MText mText when DrillParsers.IsGridLabel(mText.Contents):
                            results.Add(new GridPointCoordinate(mText.Contents.Trim(), mText.Location.Y, mText.Location.X));
                            break;
                    }
                }
            }

            transaction.Commit();
        }

        return results;
    }

    private static List<TableCellLocation> FindSurfaceCells(Table table)
    {
        var cells = new List<TableCellLocation>();
        for (var row = 0; row < table.Rows.Count; row++)
        {
            for (var column = 0; column < table.Columns.Count; column++)
            {
                var text = table.Cells[row, column].TextString.Trim().ToUpperInvariant();
                if (text.Contains("SURFACE"))
                {
                    cells.Add(new TableCellLocation(row, column));
                }
            }
        }

        return cells;
    }

    private static List<string> GetNonDefaultDrills(IReadOnlyList<string> drillNames)
    {
        var results = new List<string>();
        for (var i = 0; i < drillNames.Count; i++)
        {
            var name = (drillNames[i] ?? string.Empty).Trim();
            var defaultName = $"DRILL_{i + 1}";
            if (!string.IsNullOrWhiteSpace(name) &&
                !name.Equals(defaultName, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(name);
            }
        }

        return results;
    }

    private static Point3d GetCellNorthWest(Table table, int row, int column)
    {
        try
        {
            // The table API exposes precise cell bounds that account for rotation, scaling, and
            // title/header rows. Using the extents ensures the heading blocks are anchored to
            // the actual north-west (top-left) corner of the target cell even for data-linked
            // tables whose insertion point is the lower-left corner of the overall grid.
            var points = new Point3dCollection();
            table.GetCellExtents(row, column, isOuterCell: true, points);
            if (points.Count > 0)
            {
                var minX = points.Cast<Point3d>().Min(point => point.X);
                var maxY = points.Cast<Point3d>().Max(point => point.Y);
                var minZ = points.Cast<Point3d>().Min(point => point.Z);
                return new Point3d(minX, maxY, minZ);
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            // Older drawing versions may not support resolving cell extents. Fall back to the
            // manual calculation so we still place a block, even if alignment is approximate.
        }

        var x = table.Position.X;
        var y = table.Position.Y;

        for (var index = 0; index < column; index++)
        {
            x += table.Columns[index].Width;
        }

        // Table.Position is the lower-left of the table. To emulate a north-west corner we
        // need to walk upward from the bottom of the table to the requested row. Summing the
        // height of the target row and every row beneath it moves us to the correct vertical
        // offset.
        for (var index = row; index < table.Rows.Count; index++)
        {
            y += table.Rows[index].Height;
        }

        return new Point3d(x, y, 0.0);
    }

    private string DrillCsvPipeline(Database database)
    {
        if (!LayerExists(database, DrillPointsLayer))
        {
            ShowAlert("Layer 'Z-DRILL-POINT' not found.");
            return string.Empty;
        }

        var gridData = ReadGridPoints(database).OrderBy(p => p.Label, _naturalComparer).ToList();

        Directory.CreateDirectory(CordsDirectory);
        var csvPath = Path.Combine(CordsDirectory, "cords.csv");
        using (var writer = new StreamWriter(csvPath, false))
        {
            writer.WriteLine("Label,Northing,Easting");
            foreach (var point in gridData)
            {
                writer.WriteLine($"{point.Label},{point.Northing},{point.Easting}");
            }
        }

        ShowAlert("DONT TOUCH, WAIT FOR INSTRUCTION");
        return csvPath;
    }

    private string RunCordsExecutable(string csvPath, string heading)
    {
        if (string.IsNullOrEmpty(csvPath))
        {
            return string.Empty;
        }

        var headingArgument = GetCordsHeadingArgument(heading);
        var processExe = FindCordsExecutable();
        if (!string.IsNullOrEmpty(processExe))
        {
            _log.Info($"Running: {processExe} \"{csvPath}\" \"{headingArgument}\"");
            var startInfo = new ProcessStartInfo(processExe, $"\"{csvPath}\" \"{headingArgument}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.GetEncoding(1252),
                StandardErrorEncoding = Encoding.GetEncoding(1252)
            };

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                {
                    ShowAlert("Failed to start cord.exe.");
                    return string.Empty;
                }

                if (!process.WaitForExit(180_000))
                {
                    try
                    {
                        process.Kill();
                    }
                    catch
                    {
                        // ignore
                    }

                    _log.Error("cord.exe timeout");
                    ShowAlert("cord.exe did not exit in time.");
                    return string.Empty;
                }

                var output = process.StandardOutput.ReadToEnd();
                var error = process.StandardError.ReadToEnd();
                if (!string.IsNullOrEmpty(output))
                {
                    _log.Info(output);
                }

                if (!string.IsNullOrEmpty(error))
                {
                    _log.Error(error);
                }

                if (process.ExitCode != 0)
                {
                    _log.Error($"cord.exe exited with {process.ExitCode}");
                    ShowAlert($"cord.exe exited with {process.ExitCode}");
                    return string.Empty;
                }
            }
        }
        else
        {
            _log.Warn("cord.exe not found in any expected location, skipping.");
            ShowAlert("cord.exe not found in the expected locations.");
        }

        var excelPath = Path.Combine(Path.GetDirectoryName(csvPath) ?? CordsDirectory, "ExportedCoordsFormatted.xlsx");
        if (!File.Exists(excelPath))
        {
            ShowAlert("ExportedCoordsFormatted.xlsx not found.");
            return string.Empty;
        }

        return excelPath;
    }

    private static string? FindCordsExecutable()
    {
        foreach (var path in CordsExecutableSearchPaths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    private static string[,]? ReadExcel(string excelFilePath)
    {
        using (var package = new ExcelPackage(new FileInfo(excelFilePath)))
        {
            var worksheet = package.Workbook.Worksheets.First();
            if (!TryGetWorksheetDimension(worksheet, out var startRow, out var startColumn, out var endRow, out var endColumn))
            {
                ShowAlert("The Excel file is empty.");
                return null;
            }
            var lastRow = endRow;
            for (var row = endRow; row >= startRow; row--)
            {
                var blank = true;
                for (var column = startColumn; column <= endColumn; column++)
                {
                    if (!string.IsNullOrWhiteSpace(worksheet.Cells[row, column].Text))
                    {
                        blank = false;
                        break;
                    }
                }

                if (!blank)
                {
                    lastRow = row;
                    break;
                }
            }

            var rowCount = lastRow - startRow + 1;
            var columnCount = endColumn - startColumn + 1;
            var data = new string[rowCount, columnCount];
            for (var row = 0; row < rowCount; row++)
            {
                for (var column = 0; column < columnCount; column++)
                {
                    data[row, column] = worksheet.Cells[startRow + row, startColumn + column].Text;
                }
            }

            return data;
        }
    }

    private static bool TryGetWorksheetDimension(
        ExcelWorksheet worksheet,
        out int startRow,
        out int startColumn,
        out int endRow,
        out int endColumn)
    {
        startRow = 0;
        startColumn = 0;
        endRow = 0;
        endColumn = 0;

        if (worksheet == null)
        {
            return false;
        }

        var dimensionProperty = worksheet.GetType().GetProperty("Dimension");
        if (dimensionProperty?.GetValue(worksheet) is ExcelAddressBase dimension && dimension.Start != null && dimension.End != null)
        {
            startRow = dimension.Start.Row;
            startColumn = dimension.Start.Column;
            endRow = dimension.End.Row;
            endColumn = dimension.End.Column;
            return true;
        }

        var hasValues = false;
        startRow = int.MaxValue;
        startColumn = int.MaxValue;

        foreach (var cell in worksheet.Cells)
        {
            if (cell == null)
            {
                continue;
            }

            var hasContent = cell.Value != null || !string.IsNullOrEmpty(cell.Text) || !string.IsNullOrEmpty(cell.Formula);
            if (!hasContent)
            {
                continue;
            }

            hasValues = true;
            startRow = Math.Min(startRow, cell.Start.Row);
            startColumn = Math.Min(startColumn, cell.Start.Column);
            endRow = Math.Max(endRow, cell.End.Row);
            endColumn = Math.Max(endColumn, cell.End.Column);
        }

        if (!hasValues)
        {
            startRow = 0;
            startColumn = 0;
            endRow = 0;
            endColumn = 0;
            return false;
        }

        return true;
    }

    private void AdjustTableForClient(string[,] tableData, string heading)
    {
        if (tableData == null || tableData.GetLength(0) == 0 || tableData.GetLength(1) == 0)
        {
            return;
        }

        var headingValue = NormalizeHeadingLabel(heading);
        for (var row = 0; row < tableData.GetLength(0); row++)
        {
            for (var column = 0; column < tableData.GetLength(1); column++)
            {
                var cellText = tableData[row, column];
                if (string.IsNullOrEmpty(cellText))
                {
                    continue;
                }

                tableData[row, column] = HeadingLabelRegex.Replace(cellText, headingValue);
            }
        }

        var groups = new Dictionary<char, List<int>>();
        var tagRegex = new Regex("^[A-Z][0-9]+$", RegexOptions.IgnoreCase);

        for (var row = 0; row < tableData.GetLength(0); row++)
        {
            char? letter = null;
            for (var column = 0; column < tableData.GetLength(1); column++)
            {
                var value = tableData[row, column];
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                var trimmed = value.Trim();
                if (tagRegex.IsMatch(trimmed))
                {
                    letter = char.ToUpperInvariant(trimmed[0]);
                    break;
                }
            }

            if (!letter.HasValue)
            {
                continue;
            }

            if (!groups.TryGetValue(letter.Value, out var rows))
            {
                rows = new List<int>();
                groups[letter.Value] = rows;
            }

            rows.Add(row);
        }

        foreach (var entry in groups)
        {
            var rows = entry.Value;
            rows.Sort();
            var count = rows.Count;
            if (count == 0)
            {
                continue;
            }

            tableData[rows[0], 0] = "SURFACE";

            if (count == 1)
            {
                continue;
            }

            tableData[rows[count - 1], 0] = "BOTTOM HOLE";
            tableData[rows[1], 0] = headingValue;

            for (var index = 2; index < count - 1; index++)
            {
                tableData[rows[index], 0] = $"TURN #{index - 1}";
            }
        }

        var nonEmptyRows = 0;
        for (var row = 0; row < tableData.GetLength(0); row++)
        {
            var hasData = false;
            for (var column = 0; column < tableData.GetLength(1); column++)
            {
                if (!string.IsNullOrWhiteSpace(tableData[row, column]))
                {
                    hasData = true;
                    break;
                }
            }

            if (hasData)
            {
                nonEmptyRows++;
            }
        }

        if (nonEmptyRows == 1)
        {
            var cellValue = tableData[0, 0] ?? string.Empty;
            var normalized = cellValue.ToUpperInvariant().Replace(" ", string.Empty);
            if (normalized.Contains("BOTTOMHOLE"))
            {
                tableData[0, 0] = "SURFACE";
            }
        }

        var hasSurfaceInFirstColumn = false;
        var bottomHoleRows = new List<int>();
        for (var row = 0; row < tableData.GetLength(0); row++)
        {
            var value = tableData[row, 0] ?? string.Empty;
            var normalized = value.ToUpperInvariant().Replace(" ", string.Empty);
            if (normalized.Contains("SURFACE"))
            {
                hasSurfaceInFirstColumn = true;
                break;
            }

            if (normalized.Contains("BOTTOMHOLE"))
            {
                bottomHoleRows.Add(row);
            }
        }

        if (!hasSurfaceInFirstColumn)
        {
            foreach (var row in bottomHoleRows)
            {
                tableData[row, 0] = "SURFACE";
            }
        }
    }

    private static string NormalizeHeadingLabel(string? heading)
    {
        if (string.IsNullOrWhiteSpace(heading))
        {
            return "ICP";
        }

        return heading.Trim().ToUpperInvariant() switch
        {
            "HEEL" => "HEEL",
            "LANDING" => "LANDING",
            _ => "ICP"
        };
    }

    private static string GetCordsHeadingArgument(string? heading)
    {
        // Preserve the existing non-HEEL export path when LANDING is selected in the UI.
        return string.Equals(NormalizeHeadingLabel(heading), "HEEL", StringComparison.Ordinal)
            ? "HEEL"
            : "ICP";
    }

    private bool InsertTablePipeline(Document document, string[,] tableData)
    {
        var editor = document.Editor;
        ShowAlert("BACK TO CAD, PICK A POINT");
        var pointResult = editor.GetPoint("\nSelect insertion point:");
        if (pointResult.Status != PromptStatus.OK)
        {
            editor.WriteMessage("\nCancelled.");
            return false;
        }

        using (document.LockDocument())
        {
            var database = document.Database;
            _layerService.EnsureLayer(database, "CG-NOTES");
            InsertAndFormatTable(database, pointResult.Value, tableData, "induction Bend");
        }

        return true;
    }

    private void InsertAndFormatTable(Database database, Point3d insertionPoint, string[,] cellData, string tableStyleName)
    {
        using (var transaction = database.TransactionManager.StartTransaction())
        {
            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            var rows = cellData.GetLength(0);
            var columns = cellData.GetLength(1);

            double[] columnWidths;
            if (columns == 12)
            {
                columnWidths = new[]
                {
                    100.0, 100.0, 60.0, 60.0,
                    80.0, 80.0, 80.0, 80.0,
                    80.0, 80.0, 80.0, 80.0
                };
            }
            else
            {
                columnWidths = Enumerable.Repeat(80.0, columns).ToArray();
            }

            var table = new Table
            {
                TableStyle = GetTableStyleId(database, transaction, tableStyleName),
                Position = insertionPoint,
                Layer = "CG-NOTES"
            };
            table.SetSize(rows, columns);

            const double defaultRowHeight = 25.0;
            const double emptyRowHeight = 125.0;

            for (var row = 0; row < rows; row++)
            {
                var hasEmpty = false;
                for (var column = 0; column < columns; column++)
                {
                    var value = cellData[row, column] ?? string.Empty;
                    table.Cells[row, column].TextString = value;
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        hasEmpty = true;
                    }
                }

                table.Rows[row].Height = hasEmpty ? emptyRowHeight : defaultRowHeight;
            }

            for (var column = 0; column < columns; column++)
            {
                table.Columns[column].Width = columnWidths[column];
            }

            modelSpace.AppendEntity(table);
            transaction.AddNewlyCreatedDBObject(table, true);

            table.GenerateLayout();
            UnmergeAllCells(table);
            table.GenerateLayout();
            RemoveAllCellBorders(table);
            AddBordersForDataCells(table);
            table.RecomputeTableBlock(true);

            transaction.Commit();
        }
    }

    private static ObjectId GetTableStyleId(Database database, Transaction transaction, string styleName)
    {
        var dictionary = (DBDictionary)transaction.GetObject(database.TableStyleDictionaryId, OpenMode.ForRead);
        foreach (var entry in dictionary)
        {
            if (entry.Key.Equals(styleName, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Value;
            }
        }

        return ObjectId.Null;
    }

    private static void UnmergeAllCells(Table table)
    {
        for (var row = 0; row < table.Rows.Count; row++)
        {
            for (var column = 0; column < table.Columns.Count; column++)
            {
                var range = table.Cells[row, column].GetMergeRange();
                if (range != null && range.TopRow == row && range.LeftColumn == column)
                {
                    table.UnmergeCells(range);
                }
            }
        }
    }

    private static void RemoveAllCellBorders(Table table)
    {
        for (var row = 0; row < table.Rows.Count; row++)
        {
            for (var column = 0; column < table.Columns.Count; column++)
            {
                var cell = table.Cells[row, column];
                cell.Borders.Top.IsVisible = false;
                cell.Borders.Bottom.IsVisible = false;
                cell.Borders.Left.IsVisible = false;
                cell.Borders.Right.IsVisible = false;
            }
        }
    }

    private static void AddBordersForDataCells(Table table)
    {
        var color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 7);
        for (var row = 0; row < table.Rows.Count; row++)
        {
            for (var column = 0; column < table.Columns.Count; column++)
            {
                var cell = table.Cells[row, column];
                var value = cell.TextString;
                var hasData = !string.IsNullOrWhiteSpace(value);
                if (hasData)
                {
                    SetCellBorders(cell, true, color);
                }
                else
                {
                    var top = row > 0 && !string.IsNullOrWhiteSpace(table.Cells[row - 1, column].TextString);
                    var bottom = row < table.Rows.Count - 1 && !string.IsNullOrWhiteSpace(table.Cells[row + 1, column].TextString);
                    var left = column > 0 && !string.IsNullOrWhiteSpace(table.Cells[row, column - 1].TextString);
                    var right = column < table.Columns.Count - 1 && !string.IsNullOrWhiteSpace(table.Cells[row, column + 1].TextString);

                    cell.Borders.Top.IsVisible = top;
                    cell.Borders.Bottom.IsVisible = bottom;
                    cell.Borders.Left.IsVisible = left;
                    cell.Borders.Right.IsVisible = right;
                }
            }
        }
    }

    private static void SetCellBorders(Cell cell, bool isVisible, Autodesk.AutoCAD.Colors.Color color)
    {
        cell.Borders.Top.IsVisible = isVisible;
        cell.Borders.Bottom.IsVisible = isVisible;
        cell.Borders.Left.IsVisible = isVisible;
        cell.Borders.Right.IsVisible = isVisible;
        if (isVisible)
        {
            cell.Borders.Top.LineWeight = LineWeight.LineWeight025;
            cell.Borders.Bottom.LineWeight = LineWeight.LineWeight025;
            cell.Borders.Left.LineWeight = LineWeight.LineWeight025;
            cell.Borders.Right.LineWeight = LineWeight.LineWeight025;
            cell.Borders.Top.Color = color;
            cell.Borders.Bottom.Color = color;
            cell.Borders.Left.Color = color;
            cell.Borders.Right.Color = color;
        }
    }

    private static IEnumerable<Entity> GetEntitiesOnLayer(Database database, string layer, params Type[] types)
    {
        var results = new List<Entity>();
        using (var transaction = database.TransactionManager.StartTransaction())
        {
            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            foreach (ObjectId id in modelSpace)
            {
                if (transaction.GetObject(id, OpenMode.ForRead) is Entity entity)
                {
                    if (!entity.Layer.Equals(layer, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (types != null && types.Length > 0)
                    {
                        var rx = entity.GetRXClass();
                        var match = types.Any(type => rx.IsDerivedFrom(RXObject.GetClass(type)));
                        if (!match)
                        {
                            continue;
                        }
                    }

                    results.Add((Entity)entity.Clone());
                }
            }

            transaction.Commit();
        }

        return results;
    }

    private static bool LayerExists(Database database, string name)
    {
        using (var transaction = database.TransactionManager.StartTransaction())
        {
            var layerTable = (LayerTable)transaction.GetObject(database.LayerTableId, OpenMode.ForRead);
            var exists = layerTable.Has(name);
            transaction.Commit();
            return exists;
        }
    }

    private static void ShowAlert(string message)
    {
        AutoCADApplication.ShowAlertDialog(message);
    }
}
