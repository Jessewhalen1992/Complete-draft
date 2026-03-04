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

namespace WildlifeSweeps
{
    public class PhotoToTextCheckService
    {
        private const double MatchDistanceMeters = 3.0;

        public void Execute(Document doc, Editor editor, PluginSettings settings)
        {
            using (doc.LockDocument())
            {
                var utmZone = WildlifePromptHelper.PromptForUtmZone(editor, settings.UtmZone);
                settings.UtmZone = utmZone;

                var jpgPath = WildlifePromptHelper.PromptForJpg("Pick ANY JPG in the photo/location folder (we auto-load all JPGs): ");
                if (string.IsNullOrWhiteSpace(jpgPath))
                {
                    return;
                }

                var folder = Path.GetDirectoryName(jpgPath);
                if (string.IsNullOrWhiteSpace(folder))
                {
                    editor.WriteMessage("\nUnable to determine folder from JPG.");
                    return;
                }

                var photos = LoadGpsPhotos(folder, editor);
                if (photos.Count == 0)
                {
                    editor.WriteMessage("\nNo JPGs with GPS metadata found in that folder.");
                    return;
                }

                if (!UtmCoordinateConverter.TryCreate(utmZone, out var utmConverter) || utmConverter == null)
                {
                    editor.WriteMessage("\n** Coordinate conversion failed - check UTM zone. **");
                    return;
                }

                var projectedPhotos = ProjectPhotos(photos, utmConverter, editor);
                if (projectedPhotos.Count == 0)
                {
                    editor.WriteMessage("\nNo photos could be projected to drawing coordinates.");
                    return;
                }

                var textLocations = CollectTextLocations(doc.Database);
                if (textLocations.Count == 0)
                {
                    editor.WriteMessage("\nNo TEXT/MTEXT objects found in current space.");
                }

                var unmatched = FindUnmatchedPhotos(projectedPhotos, textLocations);
                var matchedCount = projectedPhotos.Count - unmatched.Count;

                editor.WriteMessage(
                    $"\nPhoto-to-text check complete. Photos: {projectedPhotos.Count}, Matched: {matchedCount}, Unmatched: {unmatched.Count} (threshold {MatchDistanceMeters:0.###}m, requires name match).");

                if (unmatched.Count == 0)
                {
                    editor.WriteMessage("\nAll projected photos have matching text within threshold.");
                    return;
                }

                var preview = unmatched
                    .OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
                    .Take(25)
                    .ToList();
                editor.WriteMessage("\nUnmatched photos (first 25):");
                foreach (var item in preview)
                {
                    editor.WriteMessage(
                        $"\n  {item.FileName} (N: {item.Northing:F3}, E: {item.Easting:F3}, nearest matching text: {item.NearestDistanceMeters:F3}m)");
                }

                if (unmatched.Count > preview.Count)
                {
                    editor.WriteMessage($"\n  ...and {unmatched.Count - preview.Count} more.");
                }

                var reportPath = WriteReport(doc, folder, projectedPhotos.Count, textLocations.Count, unmatched);
                if (!string.IsNullOrWhiteSpace(reportPath))
                {
                    editor.WriteMessage($"\nFull unmatched report written to: {reportPath}");
                }
            }
        }

        private static List<TextLocationRecord> CollectTextLocations(Database db)
        {
            var locations = new List<TextLocationRecord>();
            using var tr = db.TransactionManager.StartTransaction();
            var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

            foreach (ObjectId id in space)
            {
                var entity = tr.GetObject(id, OpenMode.ForRead) as Entity;
                if (entity == null)
                {
                    continue;
                }

                switch (entity)
                {
                    case DBText text when !string.IsNullOrWhiteSpace(text.TextString):
                        locations.Add(new TextLocationRecord(text.TextString.Trim(), text.Position));
                        break;
                    case MText mtext when !string.IsNullOrWhiteSpace(mtext.Text):
                        locations.Add(new TextLocationRecord(mtext.Text.Trim(), mtext.Location));
                        break;
                }
            }

            tr.Commit();
            return locations;
        }

        private static List<ProjectedPhotoRecord> ProjectPhotos(
            IReadOnlyList<PhotoGpsRecord> photos,
            UtmCoordinateConverter utmConverter,
            Editor editor)
        {
            var projected = new List<ProjectedPhotoRecord>();
            foreach (var photo in photos)
            {
                if (!utmConverter.TryProjectLatLon(photo.Latitude, photo.Longitude, out var easting, out var northing))
                {
                    editor.WriteMessage($"\nSkipped {photo.FileName}: unable to convert coordinates.");
                    continue;
                }

                projected.Add(new ProjectedPhotoRecord(photo.FileName, easting, northing));
            }

            return projected;
        }

        private static List<UnmatchedPhotoRecord> FindUnmatchedPhotos(
            IReadOnlyList<ProjectedPhotoRecord> photos,
            IReadOnlyList<TextLocationRecord> textLocations)
        {
            var unmatched = new List<UnmatchedPhotoRecord>();
            var thresholdSquared = MatchDistanceMeters * MatchDistanceMeters;

            foreach (var photo in photos)
            {
                var nearestMatchingDistanceSquared = double.MaxValue;
                foreach (var text in textLocations)
                {
                    if (!PhotoTextMatcher.IsTextMatchingPhotoName(text.Text, photo.FileName))
                    {
                        continue;
                    }

                    var dx = photo.Easting - text.Position.X;
                    var dy = photo.Northing - text.Position.Y;
                    var distanceSquared = (dx * dx) + (dy * dy);
                    if (distanceSquared < nearestMatchingDistanceSquared)
                    {
                        nearestMatchingDistanceSquared = distanceSquared;
                    }
                }

                if (nearestMatchingDistanceSquared > thresholdSquared)
                {
                    var nearestDistance = nearestMatchingDistanceSquared == double.MaxValue
                        ? double.PositiveInfinity
                        : Math.Sqrt(nearestMatchingDistanceSquared);
                    unmatched.Add(new UnmatchedPhotoRecord(photo.FileName, photo.Northing, photo.Easting, nearestDistance));
                }
            }

            return unmatched;
        }

        private static string? WriteReport(
            Document doc,
            string fallbackDirectory,
            int photoCount,
            int textCount,
            IReadOnlyList<UnmatchedPhotoRecord> unmatched)
        {
            try
            {
                var drawingPath = doc.Database.Filename;
                var targetDirectory = string.IsNullOrWhiteSpace(drawingPath)
                    ? fallbackDirectory
                    : (Path.GetDirectoryName(drawingPath) ?? fallbackDirectory);
                var baseName = string.IsNullOrWhiteSpace(drawingPath)
                    ? "unsaved_drawing"
                    : Path.GetFileNameWithoutExtension(drawingPath);
                var reportPath = Path.Combine(targetDirectory, $"{baseName}_photo_to_text_check.txt");

                var lines = new List<string>
                {
                    "Photo To Text Check Report",
                    $"Drawing: {doc.Name}",
                    $"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    $"Threshold (m): {MatchDistanceMeters.ToString("F3", CultureInfo.InvariantCulture)}",
                    "Match rule: distance + text value matches photo file name",
                    $"Photo count: {photoCount}",
                    $"Text count: {textCount}",
                    $"Unmatched count: {unmatched.Count}",
                    string.Empty,
                    "Unmatched photos:",
                    "FileName\tNorthing\tEasting\tNearestMatchingTextDistanceMeters"
                };

                lines.AddRange(unmatched
                    .OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase)
                    .Select(item => string.Join("\t",
                        item.FileName,
                        item.Northing.ToString("F3", CultureInfo.InvariantCulture),
                        item.Easting.ToString("F3", CultureInfo.InvariantCulture),
                        item.NearestDistanceMeters.ToString("F3", CultureInfo.InvariantCulture))));

                File.WriteAllLines(reportPath, lines, Encoding.UTF8);
                return reportPath;
            }
            catch
            {
                return null;
            }
        }

        private static List<PhotoGpsRecord> LoadGpsPhotos(string folder, Editor editor)
        {
            var gpsPhotos = PhotoGpsMetadataReader.LoadGpsPhotos(
                folder,
                editor,
                deduplicateByCoordinate: true,
                missingGpsSummaryFormat: "\nSkipped {0} JPG(s) without GPS metadata.");
            return gpsPhotos
                .Select(photo => new PhotoGpsRecord(
                    photo.FileName,
                    photo.Latitude,
                    photo.Longitude))
                .ToList();
        }

        private sealed record PhotoGpsRecord(string FileName, double Latitude, double Longitude);

        private sealed record ProjectedPhotoRecord(string FileName, double Easting, double Northing);

        private sealed record TextLocationRecord(string Text, Point3d Position);

        private sealed record UnmatchedPhotoRecord(
            string FileName,
            double Northing,
            double Easting,
            double NearestDistanceMeters);
    }
}
