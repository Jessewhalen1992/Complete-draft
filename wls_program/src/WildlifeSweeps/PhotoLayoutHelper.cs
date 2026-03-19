using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using DrawingImage = System.Drawing.Image;

namespace WildlifeSweeps
{
    internal static class PhotoLayoutHelper
    {
        public static void EnsureLayer(Database db, string layerName, Transaction tr)
        {
            EnsureLayer(db, layerName, Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 7), tr);
        }

        public static void EnsureLayer(Database db, string layerName, Autodesk.AutoCAD.Colors.Color color, Transaction tr)
        {
            var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (layerTable.Has(layerName))
            {
                return;
            }

            layerTable.UpgradeOpen();
            var layer = new LayerTableRecord
            {
                Name = layerName,
                Color = color
            };
            layerTable.Add(layer);
            tr.AddNewlyCreatedDBObject(layer, true);
        }

        public static bool PlacePhotoGroups(
            Database db,
            Editor editor,
            PluginSettings settings,
            IReadOnlyList<PhotoLayoutRecord> records,
            out List<string> report)
        {
            report = new List<string>();
            if (records.Count == 0)
            {
                return false;
            }

            using var trLayer = db.TransactionManager.StartTransaction();
            EnsureLayer(db, settings.PhotoLayer, trLayer);
            EnsureLayer(db, "DETAIL-T", Autodesk.AutoCAD.Colors.Color.FromRgb(0, 255, 0), trLayer);
            trLayer.Commit();

            var ordered = records.OrderBy(r => r.PhotoNumber).ToList();
            var groups = ChunkList(ordered, 4).ToList();
            if (groups.Count == 0)
            {
                return false;
            }

            var offsets = new List<Vector3d>
            {
                new Vector3d(0, 0, 0),
                new Vector3d(settings.GroupOffsetX, 0, 0),
                new Vector3d(0, -settings.GroupOffsetY, 0),
                new Vector3d(settings.GroupOffsetX, -settings.GroupOffsetY, 0)
            };

            var firstGroup = groups.First();
            var firstPhoto = firstGroup.First().PhotoNumber;
            var lastPhoto = firstGroup.Last().PhotoNumber;
            var startPointResult = editor.GetPoint($"\nPick insertion point for Group 1 (Photos {firstPhoto} - {lastPhoto}): ");
            if (startPointResult.Status != PromptStatus.OK)
            {
                return false;
            }

            return PlacePhotoGroupsAtPoint(db, settings, ordered, startPointResult.Value, out report);
        }

        public static PhotoLayoutUpdateResult RemoveAndReflowExistingPhotoGroups(
            Database db,
            PluginSettings settings,
            IReadOnlyCollection<int> removedPhotoNumbers,
            IReadOnlyDictionary<int, int> renumberMap)
        {
            if (removedPhotoNumbers == null || removedPhotoNumbers.Count == 0)
            {
                return PhotoLayoutUpdateResult.Empty;
            }

            var existingLayouts = ReadExistingPhotoLayouts(db, settings, out var unmatchedImageCount);
            if (existingLayouts.Count == 0)
            {
                return unmatchedImageCount > 0
                    ? new PhotoLayoutUpdateResult(false, 0, 0, 0, unmatchedImageCount, new List<string>())
                    : PhotoLayoutUpdateResult.Empty;
            }

            var firstLayout = existingLayouts
                .OrderBy(layout => layout.PhotoNumber)
                .FirstOrDefault();
            if (firstLayout == null)
            {
                return PhotoLayoutUpdateResult.Empty;
            }

            var orderedExistingLayouts = existingLayouts
                .OrderBy(layout => layout.PhotoNumber)
                .ToList();
            var survivingLayouts = orderedExistingLayouts
                .Select((layout, originalIndex) => new SurvivingPhotoLayout(
                    layout,
                    originalIndex,
                    renumberMap.TryGetValue(layout.PhotoNumber, out var renumbered) ? renumbered : layout.PhotoNumber))
                .Where(layout => !removedPhotoNumbers.Contains(layout.Layout.PhotoNumber))
                .OrderBy(layout => layout.NewPhotoNumber)
                .ToList();

            var removedLabels = 0;
            var removedImages = 0;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                for (var index = 0; index < orderedExistingLayouts.Count; index++)
                {
                    var layout = orderedExistingLayouts[index];
                    if (removedPhotoNumbers.Contains(layout.PhotoNumber))
                    {
                        removedLabels += TryEraseEntity(tr, layout.LabelId) ? 1 : 0;
                        removedImages += !layout.ImageId.IsNull && TryEraseEntity(tr, layout.ImageId) ? 1 : 0;
                    }
                }

                for (var newIndex = 0; newIndex < survivingLayouts.Count; newIndex++)
                {
                    var surviving = survivingLayouts[newIndex];
                    var oldSlotAnchor = BuildPhotoSlotAnchor(firstLayout.AnchorPoint, settings, surviving.OriginalSequenceIndex);
                    var newSlotAnchor = BuildPhotoSlotAnchor(firstLayout.AnchorPoint, settings, newIndex);
                    var displacement = newSlotAnchor - oldSlotAnchor;

                    if (!surviving.Layout.ImageId.IsNull)
                    {
                        TryMoveEntity(tr, surviving.Layout.ImageId, displacement);
                    }

                    if (tr.GetObject(surviving.Layout.LabelId, OpenMode.ForWrite, false) is MText label && !label.IsErased)
                    {
                        if (!displacement.IsZeroLength())
                        {
                            label.TransformBy(Matrix3d.Displacement(displacement));
                        }

                        label.Contents = BuildPhotoLabelContents(surviving.NewPhotoNumber, surviving.Layout.Caption);
                    }
                }

                tr.Commit();
            }

            if (survivingLayouts.Count == 0)
            {
                return new PhotoLayoutUpdateResult(true, removedLabels, removedImages, 0, unmatchedImageCount, new List<string>());
            }

            return new PhotoLayoutUpdateResult(
                true,
                removedLabels,
                removedImages,
                survivingLayouts.Count,
                unmatchedImageCount,
                new List<string>());
        }

        private static bool TryEraseEntity(Transaction tr, ObjectId entityId)
        {
            if (entityId.IsNull)
            {
                return false;
            }

            if (tr.GetObject(entityId, OpenMode.ForWrite, false) is Entity entity && !entity.IsErased)
            {
                entity.Erase();
                return true;
            }

            return false;
        }

        private static bool TryMoveEntity(Transaction tr, ObjectId entityId, Vector3d displacement)
        {
            if (entityId.IsNull)
            {
                return false;
            }

            if (tr.GetObject(entityId, OpenMode.ForWrite, false) is Entity entity && !entity.IsErased)
            {
                if (!displacement.IsZeroLength())
                {
                    entity.TransformBy(Matrix3d.Displacement(displacement));
                }

                return true;
            }

            return false;
        }

        private static bool PlacePhotoGroupsAtPoint(
            Database db,
            PluginSettings settings,
            IReadOnlyList<PhotoLayoutRecord> ordered,
            Point3d startPoint,
            out List<string> report)
        {
            report = new List<string>();
            if (ordered.Count == 0)
            {
                return false;
            }

            using var trLayer = db.TransactionManager.StartTransaction();
            EnsureLayer(db, settings.PhotoLayer, trLayer);
            EnsureLayer(db, "DETAIL-T", Autodesk.AutoCAD.Colors.Color.FromRgb(0, 255, 0), trLayer);
            trLayer.Commit();

            var groups = ChunkList(ordered, 4).ToList();
            if (groups.Count == 0)
            {
                return false;
            }

            var hadFailures = false;
            var offsets = new List<Vector3d>
            {
                new Vector3d(0, 0, 0),
                new Vector3d(settings.GroupOffsetX, 0, 0),
                new Vector3d(0, -settings.GroupOffsetY, 0),
                new Vector3d(settings.GroupOffsetX, -settings.GroupOffsetY, 0)
            };

            for (var groupIndex = 1; groupIndex <= groups.Count; groupIndex++)
            {
                var group = groups[groupIndex - 1];
                var basePoint = startPoint + new Vector3d(settings.GroupSpacingX * (groupIndex - 1), 0, 0);

                using var tr = db.TransactionManager.StartTransaction();
                var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);

                for (var i = 0; i < group.Count; i++)
                {
                    var rec = group[i];
                    var insertPoint = basePoint + offsets[i];

                    if (string.IsNullOrWhiteSpace(rec.ImagePath))
                    {
                        if (rec.ForceLabel)
                        {
                            AddPlaceholderLabel(space, tr, insertPoint, settings, rec.PhotoNumber, rec.Caption);
                        }
                    }
                    else if (!TryAttachImage(db, space, tr, rec.ImagePath, insertPoint, settings, rec.PhotoNumber, rec.Caption, out var message))
                    {
                        hadFailures = true;
                        report.Add(message ?? $"FAILED attach: Photo {rec.PhotoNumber}");
                    }
                }

                tr.Commit();
            }

            return !hadFailures;
        }

        private static bool TryAttachImage(
            Database db,
            BlockTableRecord space,
            Transaction tr,
            string imagePath,
            Point3d insertPoint,
            PluginSettings settings,
            int photoNumber,
            string? caption,
            out string? message)
        {
            message = null;

            if (!File.Exists(imagePath))
            {
                message = $"FAILED attach: File not found {imagePath}";
                return false;
            }

            if (!TryGetImageSize(imagePath, out var imageWidth, out var imageHeight, out message))
            {
                return false;
            }

            var imageDict = RasterImageDef.GetImageDictionary(db);
            if (imageDict.IsNull)
            {
                RasterImageDef.CreateImageDictionary(db);
                imageDict = RasterImageDef.GetImageDictionary(db);
            }

            var dict = (DBDictionary)tr.GetObject(imageDict, OpenMode.ForRead);
            var defName = BuildImageDefinitionKey(imagePath, dict);

            dict.UpgradeOpen();
            var imageDef = new RasterImageDef
            {
                SourceFileName = imagePath
            };

            imageDef.Load();
            var imageDefId = dict.SetAt(defName, imageDef);
            tr.AddNewlyCreatedDBObject(imageDef, true);

            var image = new RasterImage
            {
                ImageDefId = imageDefId,
                ShowImage = true,
                Layer = settings.PhotoLayer
            };

            var maxPixels = Math.Max(imageWidth, imageHeight);
            var maxDimension = 400.0 * Math.Min(1.0, settings.ImageScale);
            var scale = maxPixels > 0.0 ? maxDimension / maxPixels : settings.ImageScale;
            var uVector = Vector3d.XAxis.MultiplyBy(imageWidth * scale);
            var vVector = Vector3d.YAxis.MultiplyBy(imageHeight * scale);
            image.Orientation = new CoordinateSystem3d(insertPoint, uVector, vVector);

            GetTransformedCorners(
                insertPoint,
                uVector,
                vVector,
                settings.ImageRotationDegrees,
                out var rotation,
                out var displacement,
                out var hasRotation);

            if (hasRotation)
            {
                image.TransformBy(rotation);
            }

            if (!displacement.IsZeroLength())
            {
                image.TransformBy(Matrix3d.Displacement(displacement));
            }

            space.AppendEntity(image);
            tr.AddNewlyCreatedDBObject(image, true);

            image.AssociateRasterDef(imageDef);
            imageDef.Dispose();

            AddPhotoLabel(space, tr, insertPoint, uVector, vVector, settings, photoNumber, caption);

            return true;
        }

        private static List<ExistingPhotoLayoutRecord> ReadExistingPhotoLayouts(
            Database db,
            PluginSettings settings,
            out int unmatchedImageCount)
        {
            unmatchedImageCount = 0;
            using var tr = db.TransactionManager.StartTransaction();
            var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

            var labels = new List<ExistingPhotoLabel>();
            var images = new List<ExistingPhotoImage>();
            foreach (ObjectId id in space)
            {
                if (id.ObjectClass == RXObject.GetClass(typeof(MText)) &&
                    tr.GetObject(id, OpenMode.ForRead, false) is MText label &&
                    string.Equals(label.Layer, "DETAIL-T", StringComparison.OrdinalIgnoreCase) &&
                    TryParsePhotoLabel(label, out var photoNumber, out var caption))
                {
                    labels.Add(new ExistingPhotoLabel(id, photoNumber, caption, label.Location));
                    continue;
                }

                if (id.ObjectClass == RXObject.GetClass(typeof(RasterImage)) &&
                    tr.GetObject(id, OpenMode.ForRead, false) is RasterImage image &&
                    string.Equals(image.Layer, settings.PhotoLayer, StringComparison.OrdinalIgnoreCase) &&
                    TryReadExistingPhotoImage(image, tr, out var existingImage))
                {
                    images.Add(existingImage);
                }
            }

            var availableImages = new List<ExistingPhotoImage>(images);
            var records = new List<ExistingPhotoLayoutRecord>();
            foreach (var label in labels.OrderBy(item => item.PhotoNumber))
            {
                var bestIndex = -1;
                var bestScore = double.MaxValue;
                for (var i = 0; i < availableImages.Count; i++)
                {
                    var candidate = availableImages[i];
                    var expectedLabelPoint = new Point3d(candidate.CenterPoint.X, candidate.MinPoint.Y - 48.0, label.Location.Z);
                    var dx = label.Location.X - expectedLabelPoint.X;
                    var dy = label.Location.Y - expectedLabelPoint.Y;
                    if (Math.Abs(dx) > 250.0 || Math.Abs(dy) > 250.0)
                    {
                        continue;
                    }

                    var score = (dx * dx) + (dy * dy);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestIndex = i;
                    }
                }

                if (bestIndex >= 0)
                {
                    var image = availableImages[bestIndex];
                    availableImages.RemoveAt(bestIndex);
                    records.Add(new ExistingPhotoLayoutRecord(
                        label.PhotoNumber,
                        image.ImagePath,
                        label.Caption,
                        image.AnchorPoint,
                        label.LabelId,
                        image.ImageId,
                        new[] { label.LabelId, image.ImageId }));
                }
                else
                {
                    records.Add(new ExistingPhotoLayoutRecord(
                        label.PhotoNumber,
                        null,
                        label.Caption,
                        CreatePlaceholderAnchorPoint(label.Location, settings),
                        label.LabelId,
                        ObjectId.Null,
                        new[] { label.LabelId }));
                }
            }

            unmatchedImageCount = availableImages.Count;
            tr.Commit();
            return records;
        }

        private static Point3d BuildPhotoSlotAnchor(Point3d startPoint, PluginSettings settings, int sequenceIndex)
        {
            var groupIndex = sequenceIndex / 4;
            var slotIndex = sequenceIndex % 4;
            var offsets = GetPhotoSlotOffsets(settings);
            return startPoint + new Vector3d(settings.GroupSpacingX * groupIndex, 0, 0) + offsets[slotIndex];
        }

        private static IReadOnlyList<Vector3d> GetPhotoSlotOffsets(PluginSettings settings)
        {
            return new[]
            {
                new Vector3d(0, 0, 0),
                new Vector3d(settings.GroupOffsetX, 0, 0),
                new Vector3d(0, -settings.GroupOffsetY, 0),
                new Vector3d(settings.GroupOffsetX, -settings.GroupOffsetY, 0)
            };
        }

        private static bool TryReadExistingPhotoImage(
            RasterImage image,
            Transaction tr,
            out ExistingPhotoImage existingImage)
        {
            existingImage = default;
            try
            {
                var extents = image.GeometricExtents;
                var anchorPoint = image.Orientation.Origin;
                var centerPoint = new Point3d(
                    (extents.MinPoint.X + extents.MaxPoint.X) * 0.5,
                    (extents.MinPoint.Y + extents.MaxPoint.Y) * 0.5,
                    extents.MinPoint.Z);
                var imagePath = string.Empty;
                if (!image.ImageDefId.IsNull &&
                    tr.GetObject(image.ImageDefId, OpenMode.ForRead, false) is RasterImageDef imageDef)
                {
                    imagePath = imageDef.SourceFileName ?? string.Empty;
                }

                existingImage = new ExistingPhotoImage(image.ObjectId, imagePath, anchorPoint, centerPoint, extents.MinPoint);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryParsePhotoLabel(MText label, out int photoNumber, out string? caption)
        {
            photoNumber = 0;
            caption = null;

            var rawText = label.Text;
            if (string.IsNullOrWhiteSpace(rawText))
            {
                rawText = label.Contents;
            }

            if (string.IsNullOrWhiteSpace(rawText))
            {
                return false;
            }

            var normalized = rawText
                .Replace("\\P", "\n")
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');
            var lines = normalized
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToArray();
            if (lines.Length == 0 || !lines[0].StartsWith("PHOTO #", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var numberText = lines[0].Substring("PHOTO #".Length).Trim();
            if (!int.TryParse(numberText, NumberStyles.Integer, CultureInfo.InvariantCulture, out photoNumber))
            {
                return false;
            }

            caption = lines.Length > 1
                ? string.Join(" ", lines.Skip(1)).Trim()
                : null;
            return true;
        }

        private static Point3d CreatePlaceholderAnchorPoint(Point3d labelLocation, PluginSettings settings)
        {
            var placeholderDimension = 400.0 * Math.Min(1.0, settings.ImageScale);
            return new Point3d(
                labelLocation.X - (placeholderDimension * 0.5),
                labelLocation.Y + 48.0,
                labelLocation.Z);
        }

        private static string BuildImageDefinitionKey(string imagePath, DBDictionary dict)
        {
            var baseName = Path.GetFileNameWithoutExtension(imagePath);
            var sanitized = SanitizeDictionaryKey(baseName);
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                sanitized = $"image_{Guid.NewGuid():N}";
            }

            var key = sanitized;
            var suffix = 1;
            while (dict.Contains(key))
            {
                key = $"{sanitized}_{suffix}";
                suffix++;
            }

            return key;
        }

        private static string SanitizeDictionaryKey(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var chars = value
                .Select(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' ? ch : '_')
                .ToArray();
            var sanitized = new string(chars).Trim('_', '-');
            if (sanitized.Length == 0)
            {
                return string.Empty;
            }

            if (!char.IsLetter(sanitized[0]) && sanitized[0] != '_')
            {
                sanitized = $"_{sanitized}";
            }

            return sanitized;
        }

        private static void AddPhotoLabel(
            BlockTableRecord space,
            Transaction tr,
            Point3d insertPoint,
            Vector3d uVector,
            Vector3d vVector,
            PluginSettings settings,
            int photoNumber,
            string? caption)
        {
            var corners = GetTransformedCorners(
                insertPoint,
                uVector,
                vVector,
                settings.ImageRotationDegrees,
                out _,
                out _,
                out _);
            var minX = corners.Min(point => point.X);
            var maxX = corners.Max(point => point.X);
            var minY = corners.Min(point => point.Y);
            var labelPoint = new Point3d((minX + maxX) * 0.5, minY - 48.0, insertPoint.Z);

            var label = new MText
            {
                Contents = BuildPhotoLabelContents(photoNumber, caption),
                TextHeight = 20.0,
                Location = labelPoint,
                Attachment = AttachmentPoint.MiddleCenter,
                Layer = "DETAIL-T",
                Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, 3),
                Rotation = 0.0
            };

            space.AppendEntity(label);
            tr.AddNewlyCreatedDBObject(label, true);
        }

        private static void AddPlaceholderLabel(
            BlockTableRecord space,
            Transaction tr,
            Point3d insertPoint,
            PluginSettings settings,
            int photoNumber,
            string? caption)
        {
            var placeholderDimension = 400.0 * Math.Min(1.0, settings.ImageScale);
            var uVector = Vector3d.XAxis.MultiplyBy(placeholderDimension);
            var vVector = Vector3d.YAxis.MultiplyBy(placeholderDimension);
            AddPhotoLabel(space, tr, insertPoint, uVector, vVector, settings, photoNumber, caption);
        }

        private static string BuildPhotoLabelContents(int photoNumber, string? caption)
        {
            var numberText = photoNumber.ToString(CultureInfo.InvariantCulture);
            var labelText = "PHOTO #" + numberText;
            var escapedCaption = EscapeMText(caption);
            if (string.IsNullOrWhiteSpace(escapedCaption))
            {
                return labelText;
            }

            return labelText + "\\P" + escapedCaption;
        }

        private static string EscapeMText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return value
                .Trim()
                .ToUpperInvariant()
                .Replace("\\", "\\\\")
                .Replace("{", "\\{")
                .Replace("}", "\\}")
                .Replace("\r\n", " ")
                .Replace("\n", " ")
                .Replace("\r", " ");
        }

        private static IReadOnlyList<Point3d> GetTransformedCorners(
            Point3d insertPoint,
            Vector3d uVector,
            Vector3d vVector,
            double rotationDegrees,
            out Matrix3d rotation,
            out Vector3d displacement,
            out bool hasRotation)
        {
            hasRotation = Math.Abs(rotationDegrees) > 0.0001;
            var rotationMatrix = Matrix3d.Identity;
            var displacementVector = new Vector3d(0.0, 0.0, 0.0);

            var corners = new List<Point3d>
            {
                insertPoint,
                insertPoint + uVector,
                insertPoint + vVector,
                insertPoint + uVector + vVector
            };

            if (!hasRotation)
            {
                rotation = rotationMatrix;
                displacement = displacementVector;
                return corners;
            }

            rotationMatrix = Matrix3d.Rotation(rotationDegrees * (Math.PI / 180.0), Vector3d.ZAxis, insertPoint);
            corners = corners.Select(point => point.TransformBy(rotationMatrix)).ToList();
            var minX = corners.Min(point => point.X);
            var minY = corners.Min(point => point.Y);
            var minPoint = new Point3d(minX, minY, insertPoint.Z);
            displacementVector = insertPoint - minPoint;

            if (!displacementVector.IsZeroLength())
            {
                corners = corners.Select(point => point + displacementVector).ToList();
            }

            rotation = rotationMatrix;
            displacement = displacementVector;
            return corners;
        }

        private static bool TryGetImageSize(string imagePath, out double width, out double height, out string? message)
        {
            width = 0;
            height = 0;
            message = null;

            try
            {
                using var image = DrawingImage.FromFile(imagePath);
                width = image.Width;
                height = image.Height;
                return true;
            }
            catch (System.Exception ex)
            {
                message = $"FAILED attach: Unable to read image size for {imagePath}. {ex.Message}";
                return false;
            }
        }

        private static IEnumerable<List<T>> ChunkList<T>(IReadOnlyList<T> source, int size)
        {
            if (source is null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (size <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(size), "Chunk size must be greater than zero.");
            }

            for (var index = 0; index < source.Count; index += size)
            {
                var chunk = new List<T>(Math.Min(size, source.Count - index));
                for (var offset = index; offset < source.Count && offset < index + size; offset++)
                {
                    chunk.Add(source[offset]);
                }

                yield return chunk;
            }
        }

    }

    internal sealed record PhotoLayoutRecord(int PhotoNumber, string? ImagePath, bool ForceLabel = false, string? Caption = null);
    internal sealed record PhotoLayoutUpdateResult(
        bool Reflowed,
        int RemovedLabels,
        int RemovedImages,
        int ReflowedRecords,
        int UnmatchedImagesLeft,
        IReadOnlyList<string> Report)
    {
        public static PhotoLayoutUpdateResult Empty { get; } = new(false, 0, 0, 0, 0, Array.Empty<string>());
    }

    internal sealed record ExistingPhotoLayoutRecord(
        int PhotoNumber,
        string? ImagePath,
        string? Caption,
        Point3d AnchorPoint,
        ObjectId LabelId,
        ObjectId ImageId,
        IReadOnlyList<ObjectId> EntityIds);

    internal sealed record ExistingPhotoLabel(
        ObjectId LabelId,
        int PhotoNumber,
        string? Caption,
        Point3d Location);

    internal sealed record ExistingPhotoImage(
        ObjectId ImageId,
        string ImagePath,
        Point3d AnchorPoint,
        Point3d CenterPoint,
        Point3d MinPoint);

    internal sealed record SurvivingPhotoLayout(
        ExistingPhotoLayoutRecord Layout,
        int OriginalSequenceIndex,
        int NewPhotoNumber);
}
