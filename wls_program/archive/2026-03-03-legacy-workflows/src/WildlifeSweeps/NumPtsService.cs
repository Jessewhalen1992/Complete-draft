using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Windows;

namespace WildlifeSweeps
{
    public class NumPtsService
    {
        public void Execute(Document doc, Editor editor, PluginSettings settings, bool promptForInputs = true)
        {
            try
            {
                using (doc.LockDocument())
                {
                    if (!TryGetSelection(editor, out var selection))
                    {
                        return;
                    }

                    if (!TryCollectOptions(editor, settings, promptForInputs, out var options))
                    {
                        return;
                    }

                    var projector = CoordinateProjector.Create(options.UtmZone);
                    WriteProjectionStatus(editor, projector);

                    var records = GatherRecords(doc.Database, selection);
                    if (records.Count == 0)
                    {
                        editor.WriteMessage("\n** Nothing selected **");
                        return;
                    }

                    if (!TryResolveDuplicateNorthingEasting(records, editor, out var uniqueRecords))
                    {
                        return;
                    }

                    var orderedRecords = SortRecords(uniqueRecords, options.Order).ToList();
                    var standardizer = new FindingsDescriptionStandardizer(
                        settings.FindingsLookupPath,
                        warning => editor.WriteMessage($"\n{warning}"));
                    var logBuilder = FindingsStandardizationHelper.BuildLogHeader(doc);

                    if (!TryWriteCsvAndLabels(doc, editor, orderedRecords, options, projector, standardizer, logBuilder, out var logPath, out var numberedCount))
                    {
                        return;
                    }

                    editor.WriteMessage($"\nDone! Numbered {numberedCount} objects.");
                    editor.WriteMessage($"\nCSV written to: {options.CsvPath}");
                    if (!string.IsNullOrWhiteSpace(logPath))
                    {
                        editor.WriteMessage($"\nDescription log written to: {logPath}");
                    }
                    if (!projector.HasProjection && !projector.HasFallback)
                    {
                        editor.WriteMessage("\nNote: Lat/Long columns are empty (requires Map 3D/Civil 3D).");
                    }
                }
            }
            catch (Exception ex)
            {
                var logPath = PluginLogger.TryLogException(doc, "NUMPTS", ex);
                editor.WriteMessage("\nUnexpected error during NUMPTS.");
                if (!string.IsNullOrWhiteSpace(logPath))
                {
                    editor.WriteMessage($"\nDetails logged to: {logPath}");
                }
            }
        }

        private static string PromptForOrder(Editor editor, string current)
        {
            var options = new PromptKeywordOptions("\nNumbering order [LeftToRight/RightToLeft/SouthToNorth/NorthToSouth/SEtoNW/SWtoNE/NEtoSW/NWtoSE] <LeftToRight>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("LeftToRight");
            options.Keywords.Add("RightToLeft");
            options.Keywords.Add("SouthToNorth");
            options.Keywords.Add("NorthToSouth");
            options.Keywords.Add("SEtoNW");
            options.Keywords.Add("SWtoNE");
            options.Keywords.Add("NEtoSW");
            options.Keywords.Add("NWtoSE");
            options.Keywords.Default = string.IsNullOrWhiteSpace(current) ? "LeftToRight" : current;

            var result = editor.GetKeywords(options);
            return result.Status == PromptStatus.OK ? result.StringResult : NormalizeOrder(current);
        }

        private static string NormalizeOrder(string? value)
        {
            return value switch
            {
                "LeftToRight" => "LeftToRight",
                "RightToLeft" => "RightToLeft",
                "SouthToNorth" => "SouthToNorth",
                "NorthToSouth" => "NorthToSouth",
                "SEtoNW" => "SEtoNW",
                "SWtoNE" => "SWtoNE",
                "NEtoSW" => "NEtoSW",
                "NWtoSE" => "NWtoSE",
                _ => "LeftToRight"
            };
        }

        private static string? PromptForCsvPath()
        {
            var defaultPath = Path.Combine(Path.GetTempPath(), "points.csv");
            var dialog = new SaveFileDialog("Save CSV file as", defaultPath, "csv", "csv", SaveFileDialog.SaveFileDialogFlags.DoNotTransferRemoteFiles);
            return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.Filename : null;
        }

        private static bool TryGetSelection(Editor editor, out SelectionSet selection)
        {
            var filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "POINT,INSERT,TEXT,MTEXT")
            });

            var selectResult = editor.GetSelection(filter);
            if (selectResult.Status != PromptStatus.OK || selectResult.Value == null)
            {
                editor.WriteMessage("\n** Nothing selected **");
                selection = null!;
                return false;
            }

            selection = selectResult.Value;
            return true;
        }

        private static bool TryCollectOptions(Editor editor, PluginSettings settings, bool promptForInputs, out NumPtsOptions options)
        {
            var order = promptForInputs
                ? PromptForOrder(editor, settings.NumberOrder)
                : NormalizeOrder(settings.NumberOrder);
            settings.NumberOrder = order;

            var utmZone = promptForInputs
                ? PromptHelper.PromptForUtmZone(editor, settings.UtmZone)
                : PromptHelper.NormalizeUtmZone(settings.UtmZone);
            settings.UtmZone = utmZone;

            var textHeight = promptForInputs
                ? PromptHelper.PromptForDouble(editor, "\nText height for numbers <2.5>: ", settings.TextHeight)
                : settings.TextHeight;
            settings.TextHeight = textHeight;

            var startNumber = promptForInputs
                ? PromptHelper.PromptForInt(editor, "\nStarting number <1>: ", settings.StartNumber)
                : settings.StartNumber;
            settings.StartNumber = startNumber;

            var csvPath = PromptForCsvPath();
            if (string.IsNullOrWhiteSpace(csvPath))
            {
                editor.WriteMessage("\n** Cancelled **");
                options = null!;
                return false;
            }

            options = new NumPtsOptions(order, utmZone, textHeight, startNumber, csvPath);
            return true;
        }

        private static void WriteProjectionStatus(Editor editor, CoordinateProjector projector)
        {
            if (projector.HasProjection)
            {
                editor.WriteMessage("\n** Using coordinate transformation (Map 3D/Civil 3D) **");
            }
            else if (projector.HasFallback)
            {
                editor.WriteMessage("\n** Map 3D/Civil 3D transform unavailable - using built-in NAD83 UTM conversion **");
            }
            else
            {
                editor.WriteMessage("\n** Coordinate transformation failed - using XY only **");
            }
        }

        private static List<Record> GatherRecords(Database db, SelectionSet selection)
        {
            var records = new List<Record>();
            using var tr = db.TransactionManager.StartTransaction();

            foreach (SelectedObject? selected in selection)
            {
                if (selected?.ObjectId == null)
                {
                    continue;
                }

                var entity = tr.GetObject(selected.ObjectId, OpenMode.ForRead) as Entity;
                if (entity == null)
                {
                    continue;
                }

                if (!TryGetPoint(entity, tr, out var point, out var originalText))
                {
                    continue;
                }

                var isTextEntity = entity is DBText || entity is MText;
                records.Add(new Record(selected.ObjectId, point, entity.Layer, GetTextStyleId(entity), originalText, isTextEntity));
            }

            tr.Commit();
            return records;
        }

        private static ObjectId GetTextStyleId(Entity entity)
        {
            return entity is DBText text ? text.TextStyleId
                : entity is MText mtext ? mtext.TextStyleId
                : ObjectId.Null;
        }

        private static bool TryGetPoint(Entity entity, Transaction tr, out Point3d point, out string? originalText)
        {
            originalText = string.Empty;
            switch (entity)
            {
                case DBPoint dbPoint:
                    point = dbPoint.Position;
                    return true;
                case BlockReference block:
                    point = block.Position;
                    return true;
                case DBText text:
                    point = text.Position;
                    originalText = text.TextString;
                    return true;
                case MText mtext:
                    point = mtext.Location;
                    originalText = mtext.Text;
                    return true;
                case RasterImage image:
                    point = image.Orientation.Origin;
                    originalText = GetImageBaseName(image, tr);
                    return true;
                default:
                    point = Point3d.Origin;
                    return false;
            }
        }

        private static string GetImageBaseName(RasterImage image, Transaction tr)
        {
            if (image.ImageDefId.IsNull)
            {
                return string.Empty;
            }

            if (tr.GetObject(image.ImageDefId, OpenMode.ForRead) is not RasterImageDef imageDef)
            {
                return string.Empty;
            }

            var sourceFile = imageDef.SourceFileName;
            return string.IsNullOrWhiteSpace(sourceFile)
                ? string.Empty
                : Path.GetFileNameWithoutExtension(sourceFile);
        }

        private static IEnumerable<Record> SortRecords(IEnumerable<Record> records, string order)
        {
            return order switch
            {
                "RightToLeft" => records.OrderByDescending(r => r.Point.X),
                "SouthToNorth" => records.OrderBy(r => r.Point.Y).ThenBy(r => r.Point.X),
                "NorthToSouth" => records.OrderByDescending(r => r.Point.Y).ThenBy(r => r.Point.X),
                "SEtoNW" => records.OrderBy(r => r.Point.Y).ThenByDescending(r => r.Point.X),
                "SWtoNE" => records.OrderBy(r => r.Point.Y).ThenBy(r => r.Point.X),
                "NEtoSW" => records.OrderByDescending(r => r.Point.Y).ThenByDescending(r => r.Point.X),
                "NWtoSE" => records.OrderByDescending(r => r.Point.Y).ThenBy(r => r.Point.X),
                _ => records.OrderBy(r => r.Point.X)
            };
        }

        private static bool TryResolveDuplicateNorthingEasting(IReadOnlyCollection<Record> records, Editor editor, out List<Record> uniqueRecords)
        {
            const int roundingPlaces = 3;
            const double tolerance = 0.0005;
            var byRounded = new Dictionary<(double Northing, double Easting), Record>();
            var conflicts = new HashSet<(double Northing, double Easting)>();
            uniqueRecords = new List<Record>();

            foreach (var record in records)
            {
                var northing = Math.Round(record.Point.Y, roundingPlaces);
                var easting = Math.Round(record.Point.X, roundingPlaces);
                var key = (northing, easting);

                if (!byRounded.TryGetValue(key, out var existing))
                {
                    byRounded[key] = record;
                    uniqueRecords.Add(record);
                    continue;
                }

                if (Math.Abs(existing.Point.X - record.Point.X) <= tolerance
                    && Math.Abs(existing.Point.Y - record.Point.Y) <= tolerance)
                {
                    continue;
                }

                conflicts.Add(key);
            }

            if (conflicts.Count > 0)
            {
                editor.WriteMessage("\nDuplicate northing/easting values detected with differing coordinates.");
                var preview = conflicts.Take(5).ToList();
                foreach (var conflict in preview)
                {
                    editor.WriteMessage($"\n  N {conflict.Northing:F3}, E {conflict.Easting:F3}");
                }

                if (conflicts.Count > preview.Count)
                {
                    editor.WriteMessage($"\n  ...and {conflicts.Count - preview.Count} more.");
                }

                var resolution = PromptDuplicateResolution(editor);
                if (resolution == DuplicateResolution.Cancel)
                {
                    uniqueRecords = new List<Record>();
                    return false;
                }

                if (resolution == DuplicateResolution.SkipAll)
                {
                    uniqueRecords = byRounded
                        .Where(item => !conflicts.Contains(item.Key))
                        .Select(item => item.Value)
                        .ToList();
                    return true;
                }
            }

            uniqueRecords = byRounded.Values.ToList();
            return true;
        }

        private static DuplicateResolution PromptDuplicateResolution(Editor editor)
        {
            var options = new PromptKeywordOptions("\nResolve duplicates [UseFirst/SkipAll/Cancel] <UseFirst>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("UseFirst");
            options.Keywords.Add("SkipAll");
            options.Keywords.Add("Cancel");
            options.Keywords.Default = "UseFirst";

            var result = editor.GetKeywords(options);
            if (result.Status != PromptStatus.OK)
            {
                return DuplicateResolution.UseFirst;
            }

            return result.StringResult switch
            {
                "SkipAll" => DuplicateResolution.SkipAll,
                "Cancel" => DuplicateResolution.Cancel,
                _ => DuplicateResolution.UseFirst
            };
        }

        private static bool TryWriteCsvAndLabels(
            Document doc,
            Editor editor,
            IReadOnlyList<Record> records,
            NumPtsOptions options,
            CoordinateProjector projector,
            FindingsDescriptionStandardizer standardizer,
            StringBuilder logBuilder,
            out string? logPath,
            out int numberedCount)
        {
            logPath = null;
            numberedCount = 0;

            try
            {
                using var writer = new StreamWriter(options.CsvPath);
                writer.WriteLine("FindingRef,OriginalText,CleanedOriginal,Species,FindingType,StandardDescription,PhotoRef,Lat,Long,LatDDMMSS,LongDDMMSS,Northing,Easting");

                using var tr = doc.Database.TransactionManager.StartTransaction();
                var space = (BlockTableRecord)tr.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForWrite);

                var number = options.StartNumber;
                var hasNonAutomaticLogEntries = false;
                for (var i = 0; i < records.Count; i++)
                {
                    var record = records[i];
                    var standardizations = standardizer.Standardize(
                        record.OriginalText,
                        context => FindingsStandardizationHelper.PromptForUnmappedFinding(context, standardizer));
                    var ignored = standardizations.Any(standardization =>
                        standardization.Source == FindingsDescriptionStandardizer.StandardizationSource.Ignored);
                    if (ignored)
                    {
                        ColorIgnoredTextEntity(record, tr);

                        if ((i + 1) % 25 == 0 || i == records.Count - 1)
                        {
                            editor.WriteMessage($"\nProcessed {i + 1}/{records.Count} points...");
                        }

                        continue;
                    }

                    var labelPoint = new Point3d(record.Point.X, record.Point.Y - (options.TextHeight * 1.2), record.Point.Z);

                    var label = new DBText
                    {
                        Position = labelPoint,
                        TextString = number.ToString(CultureInfo.InvariantCulture),
                        Height = options.TextHeight,
                        Layer = record.LayerName ?? string.Empty,
                        TextStyleId = record.TextStyleId
                    };

                    space.AppendEntity(label);
                    tr.AddNewlyCreatedDBObject(label, true);

                    var latText = string.Empty;
                    var lonText = string.Empty;
                    var latDms = string.Empty;
                    var lonDms = string.Empty;
                    if (projector.TryProject(record.Point, out var lat, out var lon))
                    {
                        latText = lat.ToString("F6", CultureInfo.InvariantCulture);
                        lonText = lon.ToString("F6", CultureInfo.InvariantCulture);
                        latDms = DmsFormatter.ToDmsString(lat, true);
                        lonDms = DmsFormatter.ToDmsString(lon, false);
                    }

                    foreach (var standardization in standardizations)
                    {
                        writer.WriteLine(string.Join(",",
                            number,
                            EscapeCsv(record.OriginalText),
                            EscapeCsv(standardization.CleanedOriginal),
                            EscapeCsv(standardization.Species),
                            EscapeCsv(standardization.FindingType),
                            EscapeCsv(standardization.StandardDescription),
                            EscapeCsv(standardization.PhotoRef),
                            latText,
                            lonText,
                            EscapeCsv(latDms),
                            EscapeCsv(lonDms),
                            record.Point.Y.ToString("F3", CultureInfo.InvariantCulture),
                            record.Point.X.ToString("F3", CultureInfo.InvariantCulture)));

                        if (FindingsStandardizationHelper.TryAppendNonAutomaticLogEntry(logBuilder, record.OriginalText, standardization))
                        {
                            hasNonAutomaticLogEntries = true;
                        }
                    }

                    number++;
                    numberedCount++;

                    if ((i + 1) % 25 == 0 || i == records.Count - 1)
                    {
                        editor.WriteMessage($"\nProcessed {i + 1}/{records.Count} points...");
                    }
                }

                tr.Commit();

                if (hasNonAutomaticLogEntries)
                {
                    logPath = FindingsStandardizationHelper.WriteLogFile(doc, logBuilder.ToString());
                }
                return true;
            }
            catch (Exception ex)
            {
                var errorLog = PluginLogger.TryLogException(doc, "NUMPTS CSV write", ex);
                editor.WriteMessage($"\nFailed to write CSV: {ex.Message}");
                if (!string.IsNullOrWhiteSpace(errorLog))
                {
                    editor.WriteMessage($"\nDetails logged to: {errorLog}");
                }
                numberedCount = 0;
                return false;
            }
        }

        private static string EscapeCsv(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var escaped = value.Replace("\"", "\"\"");
            return $"\"{escaped}\"";
        }

        private static void ColorIgnoredTextEntity(Record record, Transaction tr)
        {
            if (!record.IsTextEntity || record.SourceEntityId.IsNull)
            {
                return;
            }

            try
            {
                if (tr.GetObject(record.SourceEntityId, OpenMode.ForWrite, false) is Entity entity)
                {
                    entity.ColorIndex = 1;
                }
            }
            catch
            {
                // Ignore stale/deleted objects and continue.
            }
        }

        private record Record(
            ObjectId SourceEntityId,
            Point3d Point,
            string? LayerName,
            ObjectId TextStyleId,
            string? OriginalText,
            bool IsTextEntity);

        private sealed record NumPtsOptions(
            string Order,
            string UtmZone,
            double TextHeight,
            int StartNumber,
            string CsvPath);

        private enum DuplicateResolution
        {
            UseFirst,
            SkipAll,
            Cancel
        }

    }
}
