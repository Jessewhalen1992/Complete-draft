using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Windows;
using DrawingImage = System.Drawing.Image;
using DrawingPropertyItem = System.Drawing.Imaging.PropertyItem;

namespace WildlifeSweeps
{
    public class PhotoDdmmssService
    {
        private const double MatchToleranceSeconds = 1.0;
        private const int GpsLatitudeRefId = 0x0001;
        private const int GpsLatitudeId = 0x0002;
        private const int GpsLongitudeRefId = 0x0003;
        private const int GpsLongitudeId = 0x0004;

        public void Execute(Document doc, Editor editor, PluginSettings settings)
        {
            try
            {
                using (doc.LockDocument())
                {
                    if (!TryGetSampleBlockName(doc, editor, out var blockName))
                    {
                        return;
                    }

                    if (!TryGetPhotoFolder(editor, out var folder))
                    {
                        return;
                    }

                    var gpsPhotos = LoadGpsPhotos(folder, editor);
                    if (gpsPhotos.Count == 0)
                    {
                        editor.WriteMessage("\nNo JPGs with GPS metadata found in that folder.");
                        return;
                    }

                    var utmZone = PromptHelper.PromptForUtmZone(editor, settings.UtmZone);
                    settings.UtmZone = utmZone;

                    var projector = CoordinateProjector.Create(utmZone);
                    if (!projector.HasProjection && !projector.HasFallback)
                    {
                        editor.WriteMessage("\n** Coordinate transformation failed - cannot match photo GPS to points. **");
                        return;
                    }

                    var blocks = BlockSelectionHelper.PromptForBlocks(doc, editor, blockName);
                    if (blocks.Count == 0)
                    {
                        editor.WriteMessage($"\nNo INSERTs found for block: {blockName}.");
                        return;
                    }

                    var records = BuildPhotoRecords(doc, blocks, gpsPhotos, projector, editor, out var unmatched);
                    if (records.Count == 0)
                    {
                        editor.WriteMessage("\nNo photo matches found for the selected blocks.");
                        return;
                    }

                    if (!PhotoLayoutHelper.PlacePhotoGroups(doc.Database, editor, settings, records, out var report))
                    {
                        return;
                    }

                    WriteReport(editor, unmatched, report);
                    editor.WriteMessage($"\nDone. Processed {records.Count} photo locations.");
                }
            }
            catch (Exception ex)
            {
                var logPath = PluginLogger.TryLogException(doc, "PhotoDdmmssService", ex);
                editor.WriteMessage("\nUnexpected error during PHOTO DDMMSS.");
                if (!string.IsNullOrWhiteSpace(logPath))
                {
                    editor.WriteMessage($"\nDetails logged to: {logPath}");
                }
            }
        }

        private static string? PromptForJpg(Editor editor)
        {
            var dialog = new OpenFileDialog("Pick ANY JPG in the folder (we auto-load all JPGs): ", "", "jpg;jpeg", "jpg", OpenFileDialog.OpenFileDialogFlags.DoNotTransferRemoteFiles);
            return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.Filename : null;
        }

        private static PhotoGpsRecord? FindBestMatch(
            double lat,
            double lon,
            IReadOnlyList<PhotoGpsRecord> photos,
            HashSet<string> used,
            double toleranceSeconds)
        {
            PhotoGpsRecord? best = null;
            double? bestDistance = null;

            foreach (var photo in photos)
            {
                if (used.Contains(photo.ImagePath))
                {
                    continue;
                }

                var latDelta = DmsFormatter.ToSecondsDifference(lat, photo.Latitude);
                var lonDelta = DmsFormatter.ToSecondsDifference(lon, photo.Longitude);
                if (latDelta > toleranceSeconds || lonDelta > toleranceSeconds)
                {
                    continue;
                }

                var distance = Math.Sqrt(latDelta * latDelta + lonDelta * lonDelta);
                if (!bestDistance.HasValue || distance < bestDistance.Value)
                {
                    bestDistance = distance;
                    best = photo;
                }
            }

            return best;
        }

        private static bool TryGetSampleBlockName(Document doc, Editor editor, out string blockName)
        {
            blockName = string.Empty;
            var sampleResult = editor.GetEntity("\nSelect ONE Photo_Location block (sample): ");
            if (sampleResult.Status != PromptStatus.OK)
            {
                return false;
            }

            using var tr = doc.Database.TransactionManager.StartTransaction();
            var sampleRef = tr.GetObject(sampleResult.ObjectId, OpenMode.ForRead) as BlockReference;
            if (sampleRef == null)
            {
                editor.WriteMessage("\nSelection is not a block reference.");
                return false;
            }

            blockName = BlockSelectionHelper.GetEffectiveName(sampleRef, tr);
            tr.Commit();
            return true;
        }

        private static bool TryGetPhotoFolder(Editor editor, out string folder)
        {
            var jpgPath = PromptForJpg(editor);
            if (string.IsNullOrWhiteSpace(jpgPath))
            {
                folder = string.Empty;
                return false;
            }

            folder = Path.GetDirectoryName(jpgPath) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(folder))
            {
                editor.WriteMessage("\nUnable to determine folder from JPG.");
                return false;
            }

            return true;
        }

        private static List<PhotoLayoutRecord> BuildPhotoRecords(
            Document doc,
            IReadOnlyList<ObjectId> blocks,
            IReadOnlyList<PhotoGpsRecord> gpsPhotos,
            CoordinateProjector projector,
            Editor editor,
            out List<string> unmatched)
        {
            var records = new List<PhotoLayoutRecord>();
            unmatched = new List<string>();
            var usedPhotos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using var tr = doc.Database.TransactionManager.StartTransaction();
            for (var i = 0; i < blocks.Count; i++)
            {
                var blockId = blocks[i];
                var block = tr.GetObject(blockId, OpenMode.ForRead) as BlockReference;
                if (block == null)
                {
                    continue;
                }

                var photoNumber = BlockSelectionHelper.TryGetAttribute(block, "#", tr);
                if (!photoNumber.HasValue || photoNumber.Value <= 0)
                {
                    continue;
                }

                if (!projector.TryProject(block.Position, out var lat, out var lon))
                {
                    unmatched.Add($"Photo {photoNumber}: no lat/long conversion available.");
                    records.Add(new PhotoLayoutRecord(photoNumber.Value, null, true));
                    continue;
                }

                var match = FindBestMatch(lat, lon, gpsPhotos, usedPhotos, MatchToleranceSeconds);
                if (match == null)
                {
                    unmatched.Add($"Photo {photoNumber}: no GPS match within {MatchToleranceSeconds:0.##}\".");
                    records.Add(new PhotoLayoutRecord(photoNumber.Value, null, true));
                    continue;
                }

                usedPhotos.Add(match.ImagePath);
                records.Add(new PhotoLayoutRecord(photoNumber.Value, match.ImagePath));

                if ((i + 1) % 25 == 0 || i == blocks.Count - 1)
                {
                    editor.WriteMessage($"\nMatched {i + 1}/{blocks.Count} photo locations...");
                }
            }

            tr.Commit();
            return records;
        }

        private static void WriteReport(Editor editor, IReadOnlyList<string> unmatched, IReadOnlyList<string> report)
        {
            if (unmatched.Count == 0 && report.Count == 0)
            {
                return;
            }

            editor.WriteMessage("\n--- Report ---");
            foreach (var entry in unmatched)
            {
                editor.WriteMessage($"\n{entry}");
            }

            foreach (var entry in report)
            {
                editor.WriteMessage($"\n{entry}");
            }

            editor.WriteMessage("\n-------------");
        }

        private static List<PhotoGpsRecord> LoadGpsPhotos(string folder, Editor editor)
        {
            var photos = new List<PhotoGpsRecord>();
            var files = Directory.EnumerateFiles(folder, "*.*")
                .Where(path => path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                               || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase));

            var skipped = 0;
            var processed = 0;
            foreach (var path in files)
            {
                try
                {
                    using var image = DrawingImage.FromFile(path);
                    if (TryReadGpsCoordinate(image, GpsLatitudeRefId, GpsLatitudeId, out var lat)
                        && TryReadGpsCoordinate(image, GpsLongitudeRefId, GpsLongitudeId, out var lon))
                    {
                        photos.Add(new PhotoGpsRecord(path, lat, lon));
                    }
                    else
                    {
                        skipped++;
                    }
                }
                catch (System.Exception ex)
                {
                    skipped++;
                    editor.WriteMessage($"\nFailed to read GPS from {Path.GetFileName(path)}: {ex.Message}");
                }

                processed++;
                if (processed % 25 == 0)
                {
                    editor.WriteMessage($"\nLoaded {processed} photo(s)...");
                }
            }

            if (skipped > 0)
            {
                editor.WriteMessage($"\nSkipped {skipped} JPG(s) without GPS metadata.");
            }

            return photos;
        }

        private static bool TryReadGpsCoordinate(DrawingImage image, int refId, int valueId, out double coordinate)
        {
            coordinate = 0.0;

            if (!TryGetPropertyItem(image, refId, out var refItem))
            {
                return false;
            }

            if (!TryGetPropertyItem(image, valueId, out var valueItem))
            {
                return false;
            }

            var hemisphere = ReadAscii(refItem);
            var values = ReadRationals(valueItem);
            if (values.Count < 3)
            {
                return false;
            }

            var decimalValue = values[0] + (values[1] / 60.0) + (values[2] / 3600.0);
            if (string.Equals(hemisphere, "S", StringComparison.OrdinalIgnoreCase)
                || string.Equals(hemisphere, "W", StringComparison.OrdinalIgnoreCase))
            {
                decimalValue = -decimalValue;
            }

            coordinate = decimalValue;
            return true;
        }

        private static bool TryGetPropertyItem(DrawingImage image, int id, out DrawingPropertyItem? item)
        {
            try
            {
                item = image.GetPropertyItem(id);
                return true;
            }
            catch (ArgumentException)
            {
                item = null;
                return false;
            }
        }

        private static string ReadAscii(DrawingPropertyItem? item)
        {
            if (item == null || item.Value == null)
            {
                return string.Empty;
            }

            return Encoding.ASCII.GetString(item.Value).Trim('\0', ' ');
        }

        private static List<double> ReadRationals(DrawingPropertyItem? item)
        {
            var values = new List<double>();
            if (item?.Value == null)
            {
                return values;
            }

            var bytes = item.Value;
            for (var offset = 0; offset + 7 < bytes.Length; offset += 8)
            {
                var numerator = BitConverter.ToUInt32(bytes, offset);
                var denominator = BitConverter.ToUInt32(bytes, offset + 4);
                if (denominator == 0)
                {
                    values.Add(0.0);
                }
                else
                {
                    values.Add(numerator / (double)denominator);
                }
            }

            return values;
        }

        private sealed record PhotoGpsRecord(string ImagePath, double Latitude, double Longitude);
    }
}
