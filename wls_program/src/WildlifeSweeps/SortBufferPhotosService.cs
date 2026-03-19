using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace WildlifeSweeps
{
    internal sealed class SortBufferPhotosService
    {
        private const double BoundaryEdgeTolerance = 0.01;
        private const double BoundaryExactRayYOffset = 0.01;
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

                var utmZone = WildlifePromptHelper.PromptForUtmZone(editor, settings.UtmZone);
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
            while (true)
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
                    continue;
                }

                if (boundary.NumberOfVertices < 3)
                {
                    editor.WriteMessage("\nBoundary polyline must have at least 3 vertices.");
                    continue;
                }

                var vertices = BoundarySamplingHelper.BuildBoundaryVertices(
                    boundary,
                    BoundarySamplingStepMeters,
                    BoundarySampleMergeTolerance);
                if (vertices.Count < 3)
                {
                    editor.WriteMessage("\nBoundary polyline could not be sampled.");
                    continue;
                }

                var bufferBoundary = new BufferBoundary(
                    vertices,
                    (Polyline)boundary.Clone(),
                    boundary.Layer,
                    boundary.Handle.ToString());

                if (RequiresBoundaryInteriorConfirmation(boundary) &&
                    !ConfirmBoundaryInteriorPoint(editor, bufferBoundary))
                {
                    continue;
                }

                return bufferBoundary;
            }
        }

        private static List<PhotoGpsRecord> LoadGpsPhotos(string folder, Editor editor)
        {
            var gpsPhotos = PhotoGpsMetadataReader.LoadGpsPhotos(
                folder,
                editor,
                deduplicateByCoordinate: false,
                missingGpsSummaryFormat: "\nSkipped {0} JPG(s) without readable GPS metadata.");
            return gpsPhotos
                .Select(photo => new PhotoGpsRecord(
                    photo.ImagePath,
                    photo.Latitude,
                    photo.Longitude))
                .ToList();
        }

        private sealed record PhotoGpsRecord(string ImagePath, double Latitude, double Longitude);

        private sealed class BufferBoundary
        {
            public BufferBoundary(
                IReadOnlyList<Point3d> vertices,
                Polyline exactBoundary,
                string sourceLayer,
                string sourceHandle)
            {
                Vertices = vertices ?? Array.Empty<Point3d>();
                ExactBoundary = exactBoundary;
                SourceLayer = sourceLayer ?? string.Empty;
                SourceHandle = sourceHandle ?? string.Empty;
            }

            public IReadOnlyList<Point3d> Vertices { get; }
            private Polyline? ExactBoundary { get; }
            public string SourceLayer { get; }
            public string SourceHandle { get; }

            public bool IsInside(Point3d point)
            {
                var exact = ExactBoundary != null
                    ? BoundaryContainmentHelper.EvaluateExactPolylineContainment(
                        ExactBoundary,
                        point,
                        BoundaryEdgeTolerance,
                        BoundaryExactRayYOffset)
                    : new BoundaryContainmentHelper.ExactContainmentEvaluation(
                        BoundaryValid: false,
                        CouldClassify: false,
                        IsInside: false,
                        IsOnBoundary: false,
                        BoundaryDistance: double.NaN,
                        SuccessfulRayCastCount: 0,
                        OddRayCastCount: 0,
                        EvenRayCastCount: 0,
                        RawIntersectionCount: 0,
                        UniqueIntersectionCount: 0,
                        WinningRayYOffset: double.NaN,
                        Error: "Exact boundary unavailable.");
                if (exact.CouldClassify)
                {
                    return exact.IsInside;
                }

                return BoundaryContainmentHelper.EvaluateSampledContainment(
                    Vertices,
                    point,
                    BoundaryEdgeTolerance,
                    use3dDistanceForDegenerateSegments: false).IsInside;
            }

            public double DistanceToBoundary(Point3d point)
            {
                if (ExactBoundary != null)
                {
                    var exactDistance = BoundaryContainmentHelper.DistanceToExactPolylineBoundary(ExactBoundary, point);
                    if (double.IsFinite(exactDistance))
                    {
                        return exactDistance;
                    }
                }

                return BoundaryContainmentHelper.DistanceToBoundary(
                    Vertices,
                    point,
                    use3dDistanceForDegenerateSegments: false);
            }
        }

        private static bool RequiresBoundaryInteriorConfirmation(Polyline boundary)
        {
            return boundary != null &&
                   string.Equals(boundary.Layer, "Defpoints", StringComparison.OrdinalIgnoreCase);
        }

        private static bool ConfirmBoundaryInteriorPoint(Editor editor, BufferBoundary boundary)
        {
            var options = new PromptPointOptions(
                $"\nPick a point that should be inside boundary handle {boundary.SourceHandle} on layer {boundary.SourceLayer}: ");
            var result = editor.GetPoint(options);
            if (result.Status != PromptStatus.OK)
            {
                return false;
            }

            var isInside = boundary.IsInside(result.Value);
            if (isInside)
            {
                return true;
            }

            editor.WriteMessage(
                $"\nThe picked interior-check point is outside boundary handle {boundary.SourceHandle} " +
                $"(distance={FormatExactDistance(boundary.DistanceToBoundary(result.Value))}m). This usually means AutoCAD selected a stacked/hidden polyline instead of the visible buffer. Select the boundary again.");
            return false;
        }

        private static string FormatExactDistance(double value)
        {
            return double.IsFinite(value)
                ? value.ToString("F3")
                : string.Empty;
        }
    }
}
