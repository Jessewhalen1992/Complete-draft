using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace AtsBackgroundBuilder
{
    public sealed class LabelPlacer
    {
        private readonly Database _database;
        private readonly Editor _editor;
        private readonly LayerManager _layerManager;
        private readonly Logger _logger;
        private readonly Config _config;

        public LabelPlacer(Database database, Editor editor, LayerManager layerManager, Config config, Logger logger)
        {
            _database = database;
            _editor = editor;
            _layerManager = layerManager;
            _config = config;
            _logger = logger;
        }

        public PlacementResult PlaceLabels(
            List<QuarterInfo> quarters,
            List<DispositionInfo> dispositions,
            string currentClient)
        {
            var result = new PlacementResult();

            using (var transaction = _database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)transaction.GetObject(_database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                var textStyleId = GetTextStyleId(transaction);

                foreach (var quarter in quarters)
                {
                    var quarterExtents = quarter.Polyline.GeometricExtents;
                    var placedExtents = new List<Extents2d>();

                    foreach (var disposition in dispositions)
                    {
                        if (!ExtentsIntersect(quarterExtents, disposition.Polyline.GeometricExtents))
                        {
                            continue;
                        }

                        if (string.IsNullOrWhiteSpace(disposition.LayerName) || string.IsNullOrWhiteSpace(disposition.TextLayerName))
                        {
                            result.SkippedNoLayerMapping++;
                            continue;
                        }

                        var anchorPoints = GetAnchorPoints(quarter.Polyline, disposition.Polyline, disposition.SafePoint, result);
                        var placed = false;
                        var attempts = 0;

                        foreach (var point in anchorPoints)
                        {
                            attempts++;
                            if (attempts > _config.MaxOverlapAttempts)
                            {
                                break;
                            }

                            if (!GeometryUtils.PointInPolyline(quarter.Polyline, point) || !GeometryUtils.PointInPolyline(disposition.Polyline, point))
                            {
                                continue;
                            }

                            var label = CreateLabel(disposition, point, textStyleId);
                            modelSpace.AppendEntity(label);
                            transaction.AddNewlyCreatedDBObject(label, true);

                            var extents = GetExtents2d(label);
                            if (HasOverlap(extents, placedExtents))
                            {
                                label.Erase(true);
                                result.OverlapForced++;
                                continue;
                            }

                            placedExtents.Add(extents);
                            placed = true;
                            result.LabelsPlaced++;
                            break;
                        }

                        if (!placed && _config.PlaceWhenOverlapFails)
                        {
                            var fallbackLabel = CreateLabel(disposition, disposition.SafePoint, textStyleId);
                            modelSpace.AppendEntity(fallbackLabel);
                            transaction.AddNewlyCreatedDBObject(fallbackLabel, true);
                            placedExtents.Add(GetExtents2d(fallbackLabel));
                            result.LabelsPlaced++;
                            result.OverlapForced++;
                        }
                    }
                }

                transaction.Commit();
            }

            return result;
        }

        private ObjectId GetTextStyleId(Transaction transaction)
        {
            var textStyleTable = (TextStyleTable)transaction.GetObject(_database.TextStyleTableId, OpenMode.ForRead);
            return textStyleTable.Has("80L") ? textStyleTable["80L"] : _database.Textstyle;
        }

        private IEnumerable<Point2d> GetAnchorPoints(Polyline quarter, Polyline disposition, Point2d fallback, PlacementResult result)
        {
            if (_config.UseRegionIntersection)
            {
                if (GeometryUtils.TryIntersectRegions(disposition, quarter, out var regions))
                {
                    foreach (var region in regions)
                    {
                        using (region)
                        {
                            var centroidComputed = false;
                            var point = Point2d.Origin;

                            try
                            {
                                var centroid = Point3d.Origin;
                                var normal = Vector3d.ZAxis;
                                var axes = Vector3d.XAxis;
                                region.AreaProperties(ref centroid, ref normal, ref axes);
                                point = new Point2d(centroid.X, centroid.Y);
                                centroidComputed = true;
                                result.MultiQuarterProcessed++;
                            }
                            catch (Autodesk.AutoCAD.Runtime.Exception ex)
                            {
                                _logger.WriteLine($"Region centroid failed ({ex.GetType().Name}). Falling back to safe point.");
                            }
                            catch (InvalidOperationException ex)
                            {
                                _logger.WriteLine($"Region centroid failed ({ex.GetType().Name}). Falling back to safe point.");
                            }

                            if (centroidComputed)
                            {
                                foreach (var candidate in GeometryUtils.GetSpiralOffsets(point, _config.TextHeight, _config.MaxOverlapAttempts))
                                {
                                    yield return candidate;
                                }
                            }
                        }
                    }
                    yield break;
                }
            }

            foreach (var candidate in GeometryUtils.GetSpiralOffsets(fallback, _config.TextHeight, _config.MaxOverlapAttempts))
            {
                yield return candidate;
            }
        }

        private MText CreateLabel(DispositionInfo disposition, Point2d location, ObjectId textStyleId)
        {
            var label = new MText
            {
                Location = new Point3d(location.X, location.Y, 0.0),
                Attachment = AttachmentPoint.MiddleCenter,
                TextHeight = _config.TextHeight,
                Contents = disposition.LabelText,
                Layer = disposition.TextLayerName,
                TextStyleId = textStyleId,
                BackgroundFill = true
            };

            return label;
        }

        private static Extents2d GetExtents2d(Entity entity)
        {
            var extents = entity.GeometricExtents;
            return new Extents2d(new Point2d(extents.MinPoint.X, extents.MinPoint.Y), new Point2d(extents.MaxPoint.X, extents.MaxPoint.Y));
        }

        private static bool HasOverlap(Extents2d extents, List<Extents2d> existing)
        {
            foreach (var existingExtents in existing)
            {
                if (GeometryUtils.ExtentsIntersect(extents, existingExtents))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ExtentsIntersect(Extents3d a, Extents3d b)
        {
            return !(a.MaxPoint.X < b.MinPoint.X || a.MinPoint.X > b.MaxPoint.X ||
                     a.MaxPoint.Y < b.MinPoint.Y || a.MinPoint.Y > b.MaxPoint.Y);
        }
    }

    public sealed class QuarterInfo
    {
        public QuarterInfo(Polyline polyline)
        {
            Polyline = polyline;
        }

        public Polyline Polyline { get; }
    }

    public sealed class DispositionInfo
    {
        public DispositionInfo(ObjectId objectId, Polyline polyline, string labelText, string layerName, string textLayerName, Point2d safePoint)
        {
            ObjectId = objectId;
            Polyline = polyline;
            LabelText = labelText;
            LayerName = layerName;
            TextLayerName = textLayerName;
            SafePoint = safePoint;
        }

        public ObjectId ObjectId { get; }
        public Polyline Polyline { get; }
        public string LabelText { get; }
        public string LayerName { get; }
        public string TextLayerName { get; }
        public Point2d SafePoint { get; }
    }

    public sealed class PlacementResult
    {
        public int LabelsPlaced { get; set; }
        public int SkippedNoLayerMapping { get; set; }
        public int OverlapForced { get; set; }
        public int MultiQuarterProcessed { get; set; }
    }
}
