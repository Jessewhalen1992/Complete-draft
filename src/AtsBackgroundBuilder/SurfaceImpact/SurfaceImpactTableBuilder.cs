using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;

namespace AtsBackgroundBuilder.SurfaceImpact
{
    internal sealed class SurfaceImpactTableBuilder
    {
        private const double RowHeight = 20.0;
        private static readonly double[] ColumnWidths = { 115.0, 130.0, 250.0 };

        private const string PreferredTableStyleName = "Induction Bend";

        private static readonly Color HeadingColor = Color.FromColorIndex(ColorMethod.ByAci, 14);
        private static readonly Color HeaderBorderColor = Color.FromColorIndex(ColorMethod.ByAci, 7);
        private static readonly Color HeaderTextColor = Color.FromColorIndex(ColorMethod.ByAci, 1);
        private static readonly Color ByBlockColor = Color.FromColorIndex(ColorMethod.ByBlock, 0);

        // Header row background (ACI 254)
        private static readonly Color HeaderBackgroundColor = Color.FromColorIndex(ColorMethod.ByAci, 254);

        public Table BuildTable(
            Database db,
            Transaction tr,
            IReadOnlyList<SurfaceImpactActivityRecord> fma,
            IReadOnlyList<SurfaceImpactActivityRecord> tpa,
            IReadOnlyList<SurfaceImpactActivityRecord> surface)
        {
            fma = fma ?? new List<SurfaceImpactActivityRecord>();
            tpa = tpa ?? new List<SurfaceImpactActivityRecord>();
            surface = surface ?? new List<SurfaceImpactActivityRecord>();

            var totalRows = CalculateRowCount(fma, tpa, surface);

            var table = new Table
            {
                TableStyle = GetTableStyleIdOrFallback(db, tr, PreferredTableStyleName)
            };

            table.SetSize(totalRows, 3);

            for (var i = 0; i < ColumnWidths.Length; i++)
            {
                table.Columns[i].Width = ColumnWidths[i];
            }

            for (var r = 0; r < totalRows; r++)
            {
                table.Rows[r].Height = RowHeight;
            }

            var currentRow = 0;

            if (fma.Count > 0)
            {
                InsertHeaderRowLeft(table, currentRow, "FORESTRY MANAGEMENT AREA(S)");
                currentRow++;

                foreach (var record in fma)
                {
                    InsertMergedOwnerRow(table, currentRow, record);
                    currentRow++;
                }
            }

            if (tpa.Count > 0)
            {
                InsertHeaderRowLeft(table, currentRow, "TRAPPING AREA(S)");
                currentRow++;

                foreach (var record in tpa)
                {
                    InsertMergedOwnerRow(table, currentRow, record);
                    currentRow++;
                }
            }

            InsertHeaderRowLeft(table, currentRow, "SURFACE IMPACT CONSIDERATIONS");
            currentRow++;

            InsertSurfaceColumnHeader(table, currentRow); // Header has background 254.
            var surfaceHeaderRow = currentRow;
            currentRow++;

            var dataStartRow = currentRow;
            InsertSurfaceRowsCentered(table, ref currentRow, surface); // Data rows: no background.
            var dataEndRow = currentRow - 1;

            ApplyDataBorders(table, surfaceHeaderRow, dataEndRow);
            ApplySurfaceMerges(table, surface, dataStartRow, dataEndRow);

            table.GenerateLayout();
            return table;
        }

        private static ObjectId GetTableStyleIdOrFallback(Database db, Transaction tr, string styleName)
        {
            var fallback = db.Tablestyle;
            if (string.IsNullOrWhiteSpace(styleName))
            {
                return fallback;
            }

            try
            {
                var dict = (DBDictionary)tr.GetObject(db.TableStyleDictionaryId, OpenMode.ForRead);
                if (dict.Contains(styleName))
                {
                    return dict.GetAt(styleName);
                }
            }
            catch
            {
                // ignore and use fallback
            }

            return fallback;
        }

        private static int CalculateRowCount(
            IReadOnlyList<SurfaceImpactActivityRecord> fma,
            IReadOnlyList<SurfaceImpactActivityRecord> tpa,
            IReadOnlyList<SurfaceImpactActivityRecord> surface)
        {
            var rows = 0;
            if (fma.Count > 0)
            {
                rows += 1 + fma.Count;
            }

            if (tpa.Count > 0)
            {
                rows += 1 + tpa.Count;
            }

            rows += 1; // surface heading
            rows += 1; // column headers
            rows += surface.Count;
            return rows;
        }

        private static void InsertHeaderRowLeft(Table table, int rowIndex, string text)
        {
            for (var c = 0; c < 3; c++)
            {
                var cell = table.Cells[rowIndex, c];
                cell.TextString = c == 0 ? text : string.Empty;
                cell.TextHeight = 12;
                cell.Alignment = CellAlignment.MiddleLeft;
                cell.ContentColor = HeadingColor;
                HideCellBorders(cell);
                SetCellBackgroundNone(cell);
            }

            table.MergeCells(CellRange.Create(table, rowIndex, 0, rowIndex, 2));
            table.Cells[rowIndex, 0].Alignment = CellAlignment.MiddleLeft;
        }

        private static void InsertMergedOwnerRow(Table table, int rowIndex, SurfaceImpactActivityRecord record)
        {
            record = record ?? new SurfaceImpactActivityRecord();

            var disp = record.DispositionNumber ?? string.Empty;
            var owner = record.OwnerName ?? string.Empty;

            var cell0 = table.Cells[rowIndex, 0];
            cell0.TextString = disp;
            cell0.Alignment = CellAlignment.MiddleLeft;
            cell0.TextHeight = 10;
            cell0.ContentColor = ByBlockColor;
            HideCellBorders(cell0);
            SetCellBackgroundNone(cell0);

            var cell1 = table.Cells[rowIndex, 1];
            cell1.TextString = owner;
            cell1.Alignment = CellAlignment.MiddleLeft;
            cell1.TextHeight = 10;
            cell1.ContentColor = ByBlockColor;
            HideCellBorders(cell1);
            SetCellBackgroundNone(cell1);

            var cell2 = table.Cells[rowIndex, 2];
            HideCellBorders(cell2);
            SetCellBackgroundNone(cell2);

            table.MergeCells(CellRange.Create(table, rowIndex, 1, rowIndex, 2));
        }

        private static void InsertSurfaceColumnHeader(Table table, int rowIndex)
        {
            var headers = new[] { "LAND LOCATION", "ACTIVITY NO.", "NAME" };

            for (var c = 0; c < headers.Length; c++)
            {
                var cell = table.Cells[rowIndex, c];
                cell.TextString = headers[c];
                cell.TextHeight = 10;
                cell.Alignment = CellAlignment.MiddleCenter;
                cell.ContentColor = HeaderTextColor;

                SetCellBackground(cell, HeaderBackgroundColor);
                SetAllBorders(cell, true);
            }
        }

        private static void InsertSurfaceRowsCentered(Table table, ref int currentRow, IReadOnlyList<SurfaceImpactActivityRecord> surface)
        {
            foreach (var record in surface)
            {
                var land = record != null ? (record.LandLocation ?? string.Empty) : string.Empty;
                var act = record != null ? (record.ActivityNoForTable ?? string.Empty) : string.Empty;
                var owner = record != null ? (record.OwnerName ?? string.Empty) : string.Empty;

                var landCell = table.Cells[currentRow, 0];
                landCell.TextString = land;
                landCell.TextHeight = 10;
                landCell.Alignment = CellAlignment.MiddleCenter;
                landCell.ContentColor = ByBlockColor;
                SetCellBackgroundNone(landCell);
                SetAllBorders(landCell, true);

                var activityCell = table.Cells[currentRow, 1];
                activityCell.TextString = act;
                activityCell.TextHeight = 10;
                activityCell.Alignment = CellAlignment.MiddleCenter;
                activityCell.ContentColor = ByBlockColor;
                SetCellBackgroundNone(activityCell);
                SetAllBorders(activityCell, true);

                var ownerCell = table.Cells[currentRow, 2];
                ownerCell.TextString = owner;
                ownerCell.TextHeight = 10;
                ownerCell.Alignment = CellAlignment.MiddleCenter;
                ownerCell.ContentColor = ByBlockColor;
                SetCellBackgroundNone(ownerCell);
                SetAllBorders(ownerCell, true);

                currentRow++;
            }
        }

        private static void ApplyDataBorders(Table table, int headerRow, int lastDataRow)
        {
            for (var r = 0; r < headerRow; r++)
            {
                for (var c = 0; c < 3; c++)
                {
                    HideCellBorders(table.Cells[r, c]);
                }
            }

            for (var r = headerRow; r <= lastDataRow; r++)
            {
                for (var c = 0; c < 3; c++)
                {
                    SetAllBorders(table.Cells[r, c], true);
                }
            }
        }

        private static void ApplySurfaceMerges(
            Table table,
            IReadOnlyList<SurfaceImpactActivityRecord> surface,
            int dataStartRow,
            int dataEndRow)
        {
            if (surface == null || surface.Count == 0)
            {
                return;
            }

            MergeRangeByValue(table, surface, dataStartRow, dataEndRow, r => r.LandLocation ?? string.Empty, 0);
            MergeActiveRtfTfa(table, surface, dataStartRow, dataEndRow);
        }

        private static void MergeRangeByValue(
            Table table,
            IReadOnlyList<SurfaceImpactActivityRecord> surface,
            int startRow,
            int endRow,
            Func<SurfaceImpactActivityRecord, string> selector,
            int columnIndex)
        {
            var rangeStart = startRow;
            var previous = selector(surface[0]);

            for (var i = 1; i < surface.Count; i++)
            {
                var current = selector(surface[i]);
                if (!string.Equals(previous, current, StringComparison.OrdinalIgnoreCase))
                {
                    MergeCellsIfNeeded(table, rangeStart, startRow + i - 1, columnIndex);
                    rangeStart = startRow + i;
                    previous = current;
                }
            }

            MergeCellsIfNeeded(table, rangeStart, endRow, columnIndex);
        }

        private static void MergeActiveRtfTfa(
            Table table,
            IReadOnlyList<SurfaceImpactActivityRecord> surface,
            int startRow,
            int endRow)
        {
            int? rangeStart = null;

            for (var i = 0; i < surface.Count; i++)
            {
                var record = surface[i];
                if (record != null && record.IsRtfTfa)
                {
                    if (rangeStart == null)
                    {
                        rangeStart = startRow + i;
                    }
                    else
                    {
                        var prevLand = surface[i - 1] != null ? (surface[i - 1].LandLocation ?? string.Empty) : string.Empty;
                        var currLand = record.LandLocation ?? string.Empty;

                        if (!string.Equals(prevLand, currLand, StringComparison.OrdinalIgnoreCase))
                        {
                            MergeCellsIfNeeded(table, rangeStart.Value, startRow + i - 1, 1);
                            rangeStart = startRow + i;
                        }
                    }
                }
                else if (rangeStart != null)
                {
                    MergeCellsIfNeeded(table, rangeStart.Value, startRow + i - 1, 1);
                    rangeStart = null;
                }
            }

            if (rangeStart != null)
            {
                MergeCellsIfNeeded(table, rangeStart.Value, endRow, 1);
            }
        }

        private static void MergeCellsIfNeeded(Table table, int startRow, int endRow, int col)
        {
            if (endRow <= startRow)
            {
                return;
            }

            table.MergeCells(CellRange.Create(table, startRow, col, endRow, col));
        }

        private static void HideCellBorders(Cell cell)
        {
            SetAllBorders(cell, false);
        }

        private static void SetAllBorders(Cell cell, bool visible)
        {
            cell.Borders.Top.IsVisible = visible;
            cell.Borders.Bottom.IsVisible = visible;
            cell.Borders.Left.IsVisible = visible;
            cell.Borders.Right.IsVisible = visible;

            cell.Borders.Top.Color = HeaderBorderColor;
            cell.Borders.Bottom.Color = HeaderBorderColor;
            cell.Borders.Left.Color = HeaderBorderColor;
            cell.Borders.Right.Color = HeaderBorderColor;
        }

        private static void SetCellBackground(Cell cell, Color color)
        {
            cell.IsBackgroundColorNone = false;
            cell.BackgroundColor = color;
        }

        private static void SetCellBackgroundNone(Cell cell)
        {
            cell.IsBackgroundColorNone = true;
        }
    }
}
