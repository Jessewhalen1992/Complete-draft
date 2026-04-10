using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Compass.Infrastructure.Logging;

using AutoCADApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Compass.Services;

public class WellCornerTableService
{
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

            var insertionResult = editor.GetPoint("\nSelect insertion point for the WELL CORNERS table:");
            if (insertionResult.Status != PromptStatus.OK)
            {
                editor.WriteMessage("\nInsertion point cancelled.");
                return;
            }

            using (document.LockDocument())
            {
                _layerService.EnsureLayer(database, "CG-NOTES");

                using var transaction = database.TransactionManager.StartTransaction();
                var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                var table = BuildTable(database, transaction, vertices, insertionResult.Value);
                modelSpace.AppendEntity(table);
                transaction.AddNewlyCreatedDBObject(table, true);

                table.GenerateLayout();
                UnmergeAllCells(table);
                table.GenerateLayout();

                transaction.Commit();
            }

            MessageBox.Show("WELL CORNERS table created successfully on CG-NOTES, with unmerged cells.", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
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

    private Table BuildTable(Database database, Transaction transaction, IReadOnlyList<Point2d> vertices, Point3d insertionPoint)
    {
        const int columnCount = 2;
        const double columnWidth = 89.41;
        const double rowHeight = 25.0;

        var table = new Table
        {
            Position = insertionPoint,
            Layer = "CG-NOTES"
        };

        var styleId = GetTableStyleId(database, transaction, "induction Bend");
        if (!styleId.IsNull)
        {
            table.TableStyle = styleId;
        }

        table.SetSize(vertices.Count, columnCount);

        for (var column = 0; column < columnCount; column++)
        {
            table.Columns[column].Width = columnWidth;
        }

        for (var row = 0; row < vertices.Count; row++)
        {
            table.Rows[row].Height = rowHeight;
            var vertex = vertices[row];
            table.Cells[row, 0].TextString = vertex.Y.ToString("F2", CultureInfo.InvariantCulture);
            table.Cells[row, 1].TextString = vertex.X.ToString("F2", CultureInfo.InvariantCulture);
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
}
