using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using DrawingImage = System.Drawing.Image;
using DrawingPropertyItem = System.Drawing.Imaging.PropertyItem;

namespace WildlifeSweeps
{
    internal sealed class SortBufferPhotosService
    {
        private const int GpsLatitudeRefId = 0x0001;
        private const int GpsLatitudeId = 0x0002;
        private const int GpsLongitudeRefId = 0x0003;
        private const int GpsLongitudeId = 0x0004;
        private const double BoundaryEdgeTolerance = 0.01;
        private const double BoundarySamplingStepMeters = 2.0;
        private const double BoundarySampleMergeTolerance = 0.01;
        private const string OutputFolderName = "within 100m";

        public void Execute(Document doc, Editor editor, PluginSettings settings)
        {
            using (doc.LockDocument())
            {
                var boundary = PromptForBuffer(editor, doc.Database);
                if (boundary == null)
                {
                    return;
                }

                var photoFolder = PromptForPhotoFolder();
                if (string.IsNullOrWhiteSpace(photoFolder))
                {
                    return;
                }

                if (!Directory.Exists(photoFolder))
                {
                    editor.WriteMessage($"\nSelected folder does not exist: {photoFolder}");
                    return;
                }

                var utmZone = PromptForUtmZone(editor, settings.UtmZone);
                settings.UtmZone = utmZone;
                if (!UtmCoordinateConverter.TryCreate(utmZone, out var utmConverter) || utmConverter == null)
                {
                    editor.WriteMessage("\n** Coordinate conversion failed - check UTM zone. **");
                    return;
                }

                var photos = LoadGpsPhotos(photoFolder, editor);
                if (photos.Count == 0)
                {
                    editor.WriteMessage("\nNo JPG photos with GPS metadata found in selected folder.");
                    return;
                }

                var outputFolder = Path.Combine(photoFolder, OutputFolderName);
                Directory.CreateDirectory(outputFolder);

                var copiedCount = 0;
                var overwrittenCount = 0;
                var outsideCount = 0;
                var projectionSkippedCount = 0;
                var copyFailedCount = 0;

                foreach (var photo in photos)
                {
                    if (!utmConverter.TryProjectLatLon(photo.Latitude, photo.Longitude, out var easting, out var northing))
                    {
                        projectionSkippedCount++;
                        continue;
                    }

                    if (!boundary.IsInside(new Point3d(easting, northing, 0.0)))
                    {
                        outsideCount++;
                        continue;
                    }

                    var destinationPath = Path.Combine(outputFolder, Path.GetFileName(photo.ImagePath));
                    try
                    {
                        var alreadyExists = File.Exists(destinationPath);
                        File.Copy(photo.ImagePath, destinationPath, overwrite: true);
                        copiedCount++;
                        if (alreadyExists)
                        {
                            overwrittenCount++;
                        }
                    }
                    catch (Exception ex)
                    {
                        copyFailedCount++;
                        editor.WriteMessage($"\nFailed to copy {Path.GetFileName(photo.ImagePath)}: {ex.Message}");
                    }
                }

                editor.WriteMessage(
                    $"\nSORT 100m buffer Photos complete. GPS photos={photos.Count}, copied={copiedCount}, outside={outsideCount}, projection skipped={projectionSkippedCount}, copy failed={copyFailedCount}, overwritten={overwrittenCount}. Output folder: {outputFolder}");
            }
        }

        private static string PromptForUtmZone(Editor editor, string current)
        {
            var options = new PromptKeywordOptions("\nPhoto UTM zone [11/12] <11>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("11");
            options.Keywords.Add("12");
            options.Keywords.Default = current == "12" ? "12" : "11";

            var result = editor.GetKeywords(options);
            return result.Status == PromptStatus.OK ? result.StringResult : options.Keywords.Default;
        }

        private static string? PromptForPhotoFolder()
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "Select photo folder to scan for JPG GPS coordinates.",
                ShowNewFolderButton = false
            };

            return dialog.ShowDialog() == DialogResult.OK
                ? dialog.SelectedPath
                : null;
        }

        private static BufferBoundary? PromptForBuffer(Editor editor, Database db)
        {
            var options = new PromptEntityOptions("\nSelect a closed polyline boundary for the 100m buffer:");
            options.SetRejectMessage("\nOnly closed LWPOLYLINE/POLYLINE boundaries are supported.");
            options.AddAllowedClass(typeof(Polyline), false);
            var result = editor.GetEntity(options);
            if (result.Status != PromptStatus.OK)
            {
                return null;
            }

            using var tr = db.TransactionManager.StartTransaction();
            if (tr.GetObject(result.ObjectId, OpenMode.ForRead) is not Polyline boundary)
            {
                editor.WriteMessage("\nSelection is not a polyline.");
                return null;
            }

            if (!boundary.Closed)
            {
                editor.WriteMessage("\nBoundary polyline must be closed.");
                return null;
            }

            if (boundary.NumberOfVertices < 3)
            {
                editor.WriteMessage("\nBoundary polyline must have at least 3 vertices.");
                return null;
            }

            var vertices = BuildBoundaryVertices(boundary);
            if (vertices.Count < 3)
            {
                editor.WriteMessage("\nBoundary polyline could not be sampled.");
                return null;
            }

            return new BufferBoundary(vertices);
        }

        private static IReadOnlyList<Point3d> BuildBoundaryVertices(Polyline boundary)
        {
            var vertices = new List<Point3d>(Math.Max(16, boundary.NumberOfVertices * 3));
            var totalLength = boundary.Length;
            if (totalLength <= 0.0)
            {
                for (var index = 0; index < boundary.NumberOfVertices; index++)
                {
                    AddBoundaryVertex(vertices, boundary.GetPoint3dAt(index));
                }

                return vertices;
            }

            for (var index = 0; index < boundary.NumberOfVertices; index++)
            {
                var segmentStartDistance = boundary.GetDistanceAtParameter(index);
                var segmentEndDistance = index == boundary.NumberOfVertices - 1
                    ? totalLength
                    : boundary.GetDistanceAtParameter(index + 1);

                AddBoundaryVertex(vertices, boundary.GetPoint3dAt(index));
                if (segmentEndDistance <= segmentStartDistance)
                {
                    continue;
                }

                var distance = segmentStartDistance + BoundarySamplingStepMeters;
                while (distance < segmentEndDistance - BoundarySampleMergeTolerance)
                {
                    AddBoundaryVertex(vertices, boundary.GetPointAtDist(distance));
                    distance += BoundarySamplingStepMeters;
                }
            }

            return vertices;
        }

        private static void AddBoundaryVertex(List<Point3d> vertices, Point3d candidate)
        {
            if (vertices.Count == 0 || vertices[vertices.Count - 1].DistanceTo(candidate) > BoundarySampleMergeTolerance)
            {
                vertices.Add(candidate);
            }
        }

        private static List<PhotoGpsRecord> LoadGpsPhotos(string folder, Editor editor)
        {
            var photos = new List<PhotoGpsRecord>();
            var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                .Where(path => path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                               || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase));

            var skipped = 0;
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
                catch (Exception ex)
                {
                    skipped++;
                    editor.WriteMessage($"\nFailed to read GPS from {Path.GetFileName(path)}: {ex.Message}");
                }
            }

            if (skipped > 0)
            {
                editor.WriteMessage($"\nSkipped {skipped} JPG(s) without readable GPS metadata.");
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

        private sealed record BufferBoundary(IReadOnlyList<Point3d> Vertices)
        {
            public bool IsInside(Point3d point)
            {
                if (Vertices.Count < 3)
                {
                    return false;
                }

                if (IsNearBoundary(point, BoundaryEdgeTolerance))
                {
                    return true;
                }

                return IsInsideByRayCasting(point) || IsInsideByWindingNumber(point);
            }

            private bool IsInsideByRayCasting(Point3d point)
            {
                var inside = false;
                var previous = Vertices[Vertices.Count - 1];
                for (var index = 0; index < Vertices.Count; index++)
                {
                    var current = Vertices[index];
                    var y1 = previous.Y;
                    var y2 = current.Y;
                    if ((y1 > point.Y) != (y2 > point.Y))
                    {
                        var xIntersection =
                            ((current.X - previous.X) * (point.Y - y1) / (y2 - y1)) + previous.X;
                        if (point.X < xIntersection)
                        {
                            inside = !inside;
                        }
                    }

                    previous = current;
                }

                return inside;
            }

            private bool IsInsideByWindingNumber(Point3d point)
            {
                var windingNumber = 0;
                for (var index = 0; index < Vertices.Count; index++)
                {
                    var start = Vertices[index];
                    var end = Vertices[(index + 1) % Vertices.Count];

                    if (start.Y <= point.Y)
                    {
                        if (end.Y > point.Y && IsLeft(start, end, point) > 0)
                        {
                            windingNumber++;
                        }
                    }
                    else if (end.Y <= point.Y && IsLeft(start, end, point) < 0)
                    {
                        windingNumber--;
                    }
                }

                return windingNumber != 0;
            }

            private static double IsLeft(Point3d start, Point3d end, Point3d point)
            {
                return ((end.X - start.X) * (point.Y - start.Y))
                       - ((point.X - start.X) * (end.Y - start.Y));
            }

            private bool IsNearBoundary(Point3d point, double tolerance)
            {
                if (Vertices.Count < 2)
                {
                    return false;
                }

                var toleranceSq = tolerance * tolerance;
                for (var index = 0; index < Vertices.Count; index++)
                {
                    var start = Vertices[index];
                    var end = Vertices[(index + 1) % Vertices.Count];
                    if (DistanceSqToSegment(point, start, end) <= toleranceSq)
                    {
                        return true;
                    }
                }

                return false;
            }

            private static double DistanceSqToSegment(Point3d point, Point3d start, Point3d end)
            {
                var segmentX = end.X - start.X;
                var segmentY = end.Y - start.Y;
                var segmentLenSq = (segmentX * segmentX) + (segmentY * segmentY);
                if (segmentLenSq <= 0.0)
                {
                    var dx = point.X - start.X;
                    var dy = point.Y - start.Y;
                    return (dx * dx) + (dy * dy);
                }

                var deltaX = point.X - start.X;
                var deltaY = point.Y - start.Y;
                var projection = (deltaX * segmentX) + (deltaY * segmentY);
                var clampedT = Math.Max(0.0, Math.Min(1.0, projection / segmentLenSq));

                var closestX = start.X + (clampedT * segmentX);
                var closestY = start.Y + (clampedT * segmentY);
                var distanceX = point.X - closestX;
                var distanceY = point.Y - closestY;
                return (distanceX * distanceX) + (distanceY * distanceY);
            }
        }
    }
}
