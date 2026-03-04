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

            var vertices = BoundarySamplingHelper.BuildBoundaryVertices(
                boundary,
                BoundarySamplingStepMeters,
                BoundarySampleMergeTolerance);
            if (vertices.Count < 3)
            {
                editor.WriteMessage("\nBoundary polyline could not be sampled.");
                return null;
            }

            return new BufferBoundary(vertices);
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

        private sealed record BufferBoundary(IReadOnlyList<Point3d> Vertices)
        {
            public bool IsInside(Point3d point)
            {
                return BoundaryContainmentHelper.IsInside(
                    Vertices,
                    point,
                    BoundaryEdgeTolerance,
                    use3dDistanceForDegenerateSegments: false);
            }
        }
    }
}
