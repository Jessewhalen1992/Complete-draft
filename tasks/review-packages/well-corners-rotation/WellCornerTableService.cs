using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Compass.Infrastructure;
using Compass.Infrastructure.Logging;

using AutoCADApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Compass.Services;

public class WellCornerTableService
{
    private const string NotesLayer = "CG-NOTES";
    private const string TableStyleName = "induction Bend";
    private const string TableTitle = "TABLE OF COORDINATES";
    private const string BubbleBlockName = "Induction_Bend_No";
    private const string BubbleAttributeTag = "BEND";
    private const short TitleColorIndex = 14;
    private const short HeaderShadeColorIndex = 254;
    private const double TitleTextHeight = 14.0;
    private const double BodyTextHeight = 10.0;
    private const double RowHeight = 25.0;
    private const double IdColumnWidth = 70.0;
    private const double NorthingColumnWidth = 80.0;
    private const double EastingColumnWidth = 80.0;
    private const double ElevationColumnWidth = 80.0;
    private const double BubbleBlockScale = 1.0;
    private const double BubbleBlockDisplayedRotationRadians = Math.PI / 2.0;
    private const int AcBlockCell = 2;
    private const int AcMiddleCenter = 5;

    private static readonly Color TitleTextColor = Color.FromColorIndex(ColorMethod.ByAci, TitleColorIndex);
    private static readonly Color HeaderShadeColor = Color.FromColorIndex(ColorMethod.ByAci, HeaderShadeColorIndex);
    private static readonly Color TableTextColor = Color.FromColorIndex(ColorMethod.ByAci, 7);
    private static readonly Color BorderColor = Color.FromColorIndex(ColorMethod.ByAci, 7);
    private static readonly Color TableBlackColor = Color.FromRgb(0, 0, 0);
    private static readonly string[] BubbleBlockSearchPaths =
    {
        @"C:\AUTOCAD-SETUP\BLOCKS\_CG BLOCKS\Induction_Bend_No.dwg",
        @"C:\AUTOCAD-SETUP CG\BLOCKS\_CG BLOCKS\Updated Blocks\Induction_Bend_No.dwg",
        @"C:\AUTOCAD-SETUP CG\BLOCKS\_CG BLOCKS\Induction_Bend_No.dwg"
    };

    private readonly LayerService _layerService;
    private readonly ILog _log;

    public WellCornerTableService(ILog log, LayerService layerService)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _layerService = layerService ?? throw new ArgumentNullException(nameof(layerService));
    }

    public void CreateWellCornersTable()
    {
        var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            MessageBox.Show("No active AutoCAD document is available.", "WELL CORNERS", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var editor = document.Editor;
        var database = document.Database;

        try
        {
            var polylineResult = PromptForPolyline(editor);
            if (polylineResult.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\nSelection cancelled.");
                return;
            }

            var vertices = ReadPolylineVertices(database, polylineResult.ObjectId);
            if (vertices.Count == 0)
            {
                MessageBox.Show("No vertices found in the selected polygon.", "No Data", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var insertionResult = editor.GetPoint("\nSelect top-left insertion point for the WELL CORNERS table:");
            if (insertionResult.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\nInsertion point cancelled.");
                return;
            }

            using (document.LockDocument())
            {
                _layerService.EnsureLayer(database, NotesLayer);

                var bubbleBlockId = EnsureBlockDefinitionLoaded(database, BubbleBlockName, BubbleBlockSearchPaths);
                if (bubbleBlockId.IsNull)
                {
                    throw new InvalidOperationException(
                        $"Could not load the required block definition '{BubbleBlockName}' for the WELL CORNERS ID cells.");
                }

                ObjectId tableId;
                ObjectId bubbleAttributeDefinitionId;
                using (var transaction = database.TransactionManager.StartTransaction())
                {
                    var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                    var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    if (!TryGetBlockAttributeDefinitionId(transaction, bubbleBlockId, BubbleAttributeTag, out bubbleAttributeDefinitionId))
                    {
                        throw new InvalidOperationException(
                            $"Block '{BubbleBlockName}' does not contain the required '{BubbleAttributeTag}' attribute definition.");
                    }

                    var table = BuildTable(database, transaction, vertices, insertionResult.Value);
                    modelSpace.AppendEntity(table);
                    transaction.AddNewlyCreatedDBObject(table, true);

                    table.MergeCells(CellRange.Create(table, 0, 0, 0, 3));
                    ApplyTableBorders(table);
                    table.GenerateLayout();
                    table.RecomputeTableBlock(true);
                    tableId = table.ObjectId;

                    transaction.Commit();
                }

                PopulateBubbleCellsViaActiveX(database, tableId, bubbleBlockId, bubbleAttributeDefinitionId, vertices.Count);
                editor.Regen();
            }

            MessageBox.Show("WELL CORNERS coordinate table created successfully on CG-NOTES.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _log.Error("Error creating WELL CORNERS table.", ex);
            MessageBox.Show($"Error creating WELL CORNERS table: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static PromptEntityResult PromptForPolyline(Editor editor)
    {
        var options = new PromptEntityOptions("\nSelect a polygon (polyline):")
        {
            AllowObjectOnLockedLayer = true
        };
        options.SetRejectMessage("\nOnly polylines are allowed.");
        options.AddAllowedClass(typeof(Polyline), true);

        return editor.GetEntity(options);
    }

    private static List<Point2d> ReadPolylineVertices(Database database, ObjectId polylineId)
    {
        var vertices = new List<Point2d>();
        using var transaction = database.TransactionManager.StartTransaction();
        var polyline = transaction.GetObject(polylineId, OpenMode.ForRead) as Polyline;
        if (polyline == null)
        {
            MessageBox.Show("Selected entity is not a valid polyline.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return vertices;
        }

        for (var i = 0; i < polyline.NumberOfVertices; i++)
        {
            vertices.Add(polyline.GetPoint2dAt(i));
        }

        transaction.Commit();
        return vertices;
    }

    private Table BuildTable(
        Database database,
        Transaction transaction,
        IReadOnlyList<Point2d> vertices,
        Point3d topLeftInsertionPoint)
    {
        var totalRows = vertices.Count + 2;
        var totalHeight = RowHeight * totalRows;
        var insertionPoint = new Point3d(
            topLeftInsertionPoint.X,
            topLeftInsertionPoint.Y - totalHeight,
            topLeftInsertionPoint.Z);

        var table = new Table
        {
            Position = insertionPoint,
            Layer = NotesLayer
        };

        var styleId = GetTableStyleId(database, transaction, TableStyleName);
        if (!styleId.IsNull)
        {
            table.TableStyle = styleId;
        }

        table.SetSize(totalRows, 4);
        table.Columns[0].Width = IdColumnWidth;
        table.Columns[1].Width = NorthingColumnWidth;
        table.Columns[2].Width = EastingColumnWidth;
        table.Columns[3].Width = ElevationColumnWidth;

        for (var row = 0; row < totalRows; row++)
        {
            table.Rows[row].Height = RowHeight;
        }

        for (var row = 0; row < totalRows; row++)
        {
            for (var column = 0; column < table.Columns.Count; column++)
            {
                table.Cells[row, column].Alignment = CellAlignment.MiddleCenter;
            }
        }

        ConfigureTextCell(table.Cells[0, 0], TableTitle, TitleTextHeight, TitleTextColor, useBackgroundFill: false, backgroundColor: null);

        ConfigureTextCell(table.Cells[1, 0], "ID", BodyTextHeight, TableTextColor, useBackgroundFill: true, HeaderShadeColor);
        ConfigureTextCell(table.Cells[1, 1], "NORTHING", BodyTextHeight, TableTextColor, useBackgroundFill: true, HeaderShadeColor);
        ConfigureTextCell(table.Cells[1, 2], "EASTING", BodyTextHeight, TableTextColor, useBackgroundFill: true, HeaderShadeColor);
        ConfigureTextCell(table.Cells[1, 3], "ELEVATION", BodyTextHeight, TableTextColor, useBackgroundFill: true, HeaderShadeColor);

        for (var index = 0; index < vertices.Count; index++)
        {
            var tableRow = index + 2;
            var vertex = vertices[index];
            ConfigureTextCell(table.Cells[tableRow, 0], string.Empty, BodyTextHeight, TableTextColor, useBackgroundFill: false, backgroundColor: null);
            ConfigureTextCell(table.Cells[tableRow, 1], vertex.Y.ToString("F2", CultureInfo.InvariantCulture), BodyTextHeight, TableTextColor, useBackgroundFill: false, backgroundColor: null);
            ConfigureTextCell(table.Cells[tableRow, 2], vertex.X.ToString("F2", CultureInfo.InvariantCulture), BodyTextHeight, TableTextColor, useBackgroundFill: false, backgroundColor: null);
            ConfigureTextCell(table.Cells[tableRow, 3], string.Empty, BodyTextHeight, TableTextColor, useBackgroundFill: false, backgroundColor: null);
        }

        return table;
    }

    private static ObjectId GetTableStyleId(Database database, Transaction transaction, string styleName)
    {
        var tableStyles = (DBDictionary)transaction.GetObject(database.TableStyleDictionaryId, OpenMode.ForRead);
        foreach (var entry in tableStyles)
        {
            if (entry.Key.Equals(styleName, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Value;
            }
        }

        return ObjectId.Null;
    }

    private static void ConfigureTextCell(Cell cell, string text, double textHeight, Color contentColor, bool useBackgroundFill, Color? backgroundColor)
    {
        cell.TextString = text;
        cell.TextHeight = textHeight;
        cell.ContentColor = contentColor;
        cell.IsBackgroundColorNone = !useBackgroundFill;
        if (useBackgroundFill && backgroundColor != null)
        {
            cell.BackgroundColor = backgroundColor;
        }

        cell.Alignment = CellAlignment.MiddleCenter;
    }

    private static void PopulateBubbleCellsViaActiveX(
        Database database,
        ObjectId tableId,
        ObjectId bubbleBlockId,
        ObjectId bubbleAttributeDefinitionId,
        int bubbleCount)
    {
        if (bubbleCount <= 0 || bubbleBlockId.IsNull)
        {
            return;
        }

        using var transaction = database.TransactionManager.StartTransaction();
        var table = (Table)transaction.GetObject(tableId, OpenMode.ForWrite);
        object? acadTable = null;

        try
        {
            acadTable = table.AcadObject;
            if (acadTable == null)
            {
                throw new InvalidOperationException("AutoCAD did not expose an ActiveX table object for WELL CORNERS block-cell population.");
            }

            var bubbleRotation = GetDisplayedAngleAsWorldRotation();
            var blockDefinitionComId = bubbleBlockId.OldIdPtr.ToInt64();
            var attributeDefinitionComId = bubbleAttributeDefinitionId.OldIdPtr.ToInt64();

            SetComProperty(acadTable, "RegenerateTableSuppressed", true);
            try
            {
                for (var index = 0; index < bubbleCount; index++)
                {
                    var row = index + 2;
                    InvokeComMethod(acadTable, "SetCellType", row, 0, AcBlockCell);
                    InvokeComMethod(acadTable, "SetBlockTableRecordId", row, 0, blockDefinitionComId, false);
                    InvokeComMethod(acadTable, "SetAutoScale", row, 0, false);
                    InvokeComMethod(acadTable, "SetBlockScale", row, 0, BubbleBlockScale);
                    InvokeComMethod(acadTable, "SetCellAlignment", row, 0, AcMiddleCenter);
                    InvokeComMethod(acadTable, "SetBlockRotation", row, 0, bubbleRotation);
                    InvokeComMethod(
                        acadTable,
                        "SetBlockAttributeValue",
                        row,
                        0,
                        attributeDefinitionComId,
                        (index + 1).ToString(CultureInfo.InvariantCulture));
                }
            }
            finally
            {
                SetComProperty(acadTable, "RegenerateTableSuppressed", false);
            }

            InvokeComMethod(acadTable, "Update");
            table.GenerateLayout();
            table.RecomputeTableBlock(true);
            transaction.Commit();
        }
        finally
        {
            if (OperatingSystem.IsWindows() && acadTable != null && Marshal.IsComObject(acadTable))
            {
                Marshal.FinalReleaseComObject(acadTable);
            }
        }
    }

    private static double GetDisplayedAngleAsWorldRotation()
    {
        try
        {
            var angBase = Convert.ToDouble(AutoCADApplication.GetSystemVariable("ANGBASE"), CultureInfo.InvariantCulture);
            var angDir = Convert.ToInt32(AutoCADApplication.GetSystemVariable("ANGDIR"), CultureInfo.InvariantCulture);
            var worldRotation = angDir == 1
                ? angBase - BubbleBlockDisplayedRotationRadians
                : angBase + BubbleBlockDisplayedRotationRadians;

            return NormalizeAngle(worldRotation);
        }
        catch
        {
            return BubbleBlockDisplayedRotationRadians;
        }
    }

    private static double NormalizeAngle(double angle)
    {
        var fullTurn = Math.PI * 2.0;
        var normalized = angle % fullTurn;
        return normalized < 0.0 ? normalized + fullTurn : normalized;
    }

    private static object? InvokeComMethod(object comObject, string methodName, params object[] args)
    {
        return comObject.GetType().InvokeMember(
            methodName,
            BindingFlags.InvokeMethod,
            binder: null,
            target: comObject,
            args: args);
    }

    private static void SetComProperty(object comObject, string propertyName, object value)
    {
        comObject.GetType().InvokeMember(
            propertyName,
            BindingFlags.SetProperty,
            binder: null,
            target: comObject,
            args: new[] { value });
    }

    private static void ApplyTableBorders(Table table)
    {
        ApplyCellBorders(table.Cells[0, 0], BorderColor);

        for (var row = 1; row < table.Rows.Count; row++)
        {
            for (var column = 0; column < table.Columns.Count; column++)
            {
                ApplyCellBorders(table.Cells[row, column], BorderColor);
            }
        }
    }

    private static void ApplyCellBorders(Cell cell, Color color)
    {
        cell.Borders.Top.IsVisible = true;
        cell.Borders.Bottom.IsVisible = true;
        cell.Borders.Left.IsVisible = true;
        cell.Borders.Right.IsVisible = true;

        cell.Borders.Top.Color = color;
        cell.Borders.Bottom.Color = color;
        cell.Borders.Left.Color = color;
        cell.Borders.Right.Color = color;
    }

    private static bool TryGetBlockAttributeDefinitionId(
        Transaction transaction,
        ObjectId blockDefinitionId,
        string attributeTag,
        out ObjectId attributeDefinitionId)
    {
        attributeDefinitionId = ObjectId.Null;
        var blockDefinition = (BlockTableRecord)transaction.GetObject(blockDefinitionId, OpenMode.ForRead);
        foreach (ObjectId entityId in blockDefinition)
        {
            if (transaction.GetObject(entityId, OpenMode.ForRead) is AttributeDefinition attributeDefinition &&
                AutoCADBlockService.TagMatches(attributeDefinition.Tag, attributeTag))
            {
                attributeDefinitionId = attributeDefinition.ObjectId;
                return true;
            }
        }

        return false;
    }

    private static ObjectId EnsureBlockDefinitionLoaded(Database destinationDatabase, string blockName, IReadOnlyList<string> searchPaths)
    {
        using (var transaction = destinationDatabase.TransactionManager.StartTransaction())
        {
            var blockTable = (BlockTable)transaction.GetObject(destinationDatabase.BlockTableId, OpenMode.ForRead);
            if (blockTable.Has(blockName))
            {
                var blockId = blockTable[blockName];
                transaction.Commit();
                return blockId;
            }

            transaction.Commit();
        }

        foreach (var candidatePath in searchPaths)
        {
            if (!File.Exists(candidatePath))
            {
                continue;
            }

            var importedBlockId = ImportBlockDefinition(destinationDatabase, candidatePath, blockName);
            if (!importedBlockId.IsNull)
            {
                return importedBlockId;
            }
        }

        return ObjectId.Null;
    }

    private static ObjectId ImportBlockDefinition(Database destinationDatabase, string sourceDrawingPath, string blockName)
    {
        if (destinationDatabase == null || string.IsNullOrWhiteSpace(sourceDrawingPath) || !File.Exists(sourceDrawingPath))
        {
            return ObjectId.Null;
        }

        using var sourceDatabase = new Database(false, true);
        sourceDatabase.ReadDwgFile(sourceDrawingPath, FileShare.Read, allowCPConversion: true, password: string.Empty);

        using (var sourceTransaction = sourceDatabase.TransactionManager.StartTransaction())
        {
            var sourceBlockTable = (BlockTable)sourceTransaction.GetObject(sourceDatabase.BlockTableId, OpenMode.ForRead);
            if (!sourceBlockTable.Has(blockName))
            {
                return ObjectId.Null;
            }

            var sourceBlockDefinitionId = sourceBlockTable[blockName];
            var idsToClone = new ObjectIdCollection { sourceBlockDefinitionId };
            sourceTransaction.Commit();

            var mapping = new IdMapping();
            destinationDatabase.WblockCloneObjects(idsToClone, destinationDatabase.BlockTableId, mapping, DuplicateRecordCloning.Replace, false);
        }

        using var destinationTransaction = destinationDatabase.TransactionManager.StartTransaction();
        var destinationBlockTable = (BlockTable)destinationTransaction.GetObject(destinationDatabase.BlockTableId, OpenMode.ForRead);
        if (!destinationBlockTable.Has(blockName))
        {
            return ObjectId.Null;
        }

        var blockDefinitionId = destinationBlockTable[blockName];
        destinationTransaction.Commit();
        return blockDefinitionId;
    }
}
