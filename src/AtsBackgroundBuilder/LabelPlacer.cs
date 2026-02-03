/////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Linq;
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
        private readonly Config _config;
        private readonly Logger _logger;

        public LabelPlacer(Database database, Editor editor, LayerManager layerManager, Config config, Logger logger)
        {
            _database = database;
            _editor = editor;
            _layerManager = layerManager;
            _config = config;
            _logger = logger;
        }

        public PlacementResult PlaceLabels(List<QuarterInfo> quarters, List<DispositionInfo> dispositions, string currentClient)
        {
            var result = new PlacementResult();
            var processedDispositionIds = new HashSet<ObjectId>();

            using (var transaction = _database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)transaction.GetObject(_database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                // Track extents of labels placed so far so we can avoid overlaps
                var placedLabelExtents = new List<Extents3d>();

                foreach (var quarter in quarters)
                {
                    using (var quarterClone = (Polyline)quarter.Polyline.Clone())
                    {
                        foreach (var disposition in dispositions)
                        {
                            if (!_config.AllowMultiQuarterDispositions && processedDispositionIds.Contains(disposition.ObjectId))
                                continue;

                            if (processedDispositionIds.Contains(disposition.ObjectId))
                                result.MultiQuarterProcessed++;

                            // Quick reject by extents
                            if (!GeometryUtils.ExtentsIntersect(quarter.Polyline.GeometricExtents, disposition.Polyline.GeometricExtents))
                                continue;

                            // Label layer mapping check
                            if (string.IsNullOrWhiteSpace(disposition.TextLayerName))
                            {
                                result.SkippedNoLayerMapping++;
                                continue;
                            }

                            // Ensure label layer exists
                            EnsureLayerInTransaction(_database, transaction, disposition.TextLayerName);

                            using (var dispClone = (Polyline)disposition.Polyline.Clone())
                            {
                                // Leader target point (inside quarter ∩ disposition whenever possible)
                                var target = GetTargetPoint(quarterClone, dispClone, disposition.SafePoint);

                                // Candidate label points around the target
                                var candidates = GetCandidateLabelPoints(
                                        quarterClone,
                                        dispClone,
                                        target,
                                        disposition.AllowLabelOutsideDisposition,
                                        _config.TextHeight,
                                        _config.MaxOverlapAttempts)
                                    .ToList();

                                if (candidates.Count == 0)
                                {
                                    // If we cannot find any point meeting the inside checks, fall back to target
                                    candidates.Add(target);
                                }

                                bool placed = false;
                                Point2d lastCandidate = candidates[candidates.Count - 1];

                                foreach (var pt in candidates)
                                {
                                    lastCandidate = pt;

                                    var labelEntity = CreateLabelEntity(transaction, modelSpace, target, pt, disposition);
                                    var bounds = labelEntity.GeometricExtents;
                                    bool overlaps = placedLabelExtents.Any(b => GeometryUtils.ExtentsIntersect(b, bounds));

                                    if (overlaps)
                                    {
                                        labelEntity.Erase();
                                        continue;
                                    }

                                    // Success
                                    placedLabelExtents.Add(bounds);
                                    placed = true;
                                    result.LabelsPlaced++;
                                    break;
                                }

                                if (!placed && _config.PlaceWhenOverlapFails && candidates.Count > 0)
                                {
                                    // Forced placement at last candidate
                                    var labelEntity = CreateLabelEntity(transaction, modelSpace, target, lastCandidate, disposition);

                                    placedLabelExtents.Add(labelEntity.GeometricExtents);
                                    result.LabelsPlaced++;
                                    result.OverlapForced++;

                                    placed = true;
                                }

                                if (!placed)
                                {
                                    // Not counted in legacy result, but keep debug output
                                    _logger.WriteLine($"Could not place label for disposition {disposition.ObjectId}");
                                }

                                processedDispositionIds.Add(disposition.ObjectId);
                            }
                        }
                    }
                }

                transaction.Commit();
            }

            return result;
        }

        private MText CreateLabel(Point2d point, string labelText, string layerName)
        {
            return new MText
            {
                Location = new Point3d(point.X, point.Y, 0),
                TextHeight = _config.TextHeight,
                Contents = labelText,
                Layer = layerName,
                ColorIndex = 256,
                Attachment = AttachmentPoint.MiddleCenter
            };
        }

        private Entity CreateLabelEntity(Transaction tr, BlockTableRecord modelSpace, Point2d target, Point2d labelPoint, DispositionInfo disposition)
        {
            if (_config.EnableLeaders && disposition.AddLeader)
            {
                return CreateLeader(tr, modelSpace, target, labelPoint, disposition.LabelText, disposition.TextLayerName);
            }

            var mtext = CreateLabel(labelPoint, disposition.LabelText, disposition.TextLayerName);
            modelSpace.AppendEntity(mtext);
            tr.AddNewlyCreatedDBObject(mtext, true);
            return mtext;
        }

        private MLeader CreateLeader(
            Transaction tr,
            BlockTableRecord modelSpace,
            Point2d target,
            Point2d labelPoint,
            string labelText,
            string layerName)
        {
            var mtext = new MText
            {
                Location = new Point3d(labelPoint.X, labelPoint.Y, 0),
                TextHeight = _config.TextHeight,
                Contents = labelText,
                Layer = layerName,
                ColorIndex = 256,
                Attachment = AttachmentPoint.MiddleCenter
            };

            var mleader = new MLeader();
            mleader.SetDatabaseDefaults();
            mleader.ContentType = ContentType.MTextContent;
            mleader.MText = mtext;

            // Create a leader cluster and line
            int leaderIndex = mleader.AddLeader();
            int lineIndex = mleader.AddLeaderLine(leaderIndex);

            // Set the start and end points of the leader line
            mleader.AddFirstVertex(lineIndex, new Point3d(target.X, target.Y, 0));
            mleader.AddLastVertex(lineIndex, new Point3d(labelPoint.X, labelPoint.Y, 0));

            mleader.LeaderLineType = LeaderType.StraightLeader;

            // Assign an arrow block (e.g. a dot) via ArrowSymbolId as needed; no HasArrowHead property exists
            var arrowId = GetClosedDotArrowId(tr);
            if (!arrowId.IsNull)
                mleader.ArrowSymbolId = arrowId;
            mleader.ArrowSize = 5.0;

            mleader.Layer = layerName;
            mleader.ColorIndex = 256;

            modelSpace.AppendEntity(mleader);
            tr.AddNewlyCreatedDBObject(mleader, true);
            return mleader;
        }

        private ObjectId GetClosedDotArrowId(Transaction tr)
        {
            var blockTable = (BlockTable)tr.GetObject(_database.BlockTableId, OpenMode.ForRead);
            var candidateNames = new[]
            {
                "_Dot",
                "_DOT",
                "DOT",
                "_DotSmall",
                "_DOTSMALL",
                "DOTSMALL"
            };

            foreach (var name in candidateNames)
            {
                if (blockTable.Has(name))
                {
                    return blockTable[name];
                }
            }

            return ObjectId.Null;
        }

        private static void EnsureLayerInTransaction(Database db, Transaction tr, string layerName)
        {
            var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (layerTable.Has(layerName))
                return;

            layerTable.UpgradeOpen();
            var layer = new LayerTableRecord
            {
                Name = layerName,
                IsPlottable = true
            };

            layerTable.Add(layer);
            tr.AddNewlyCreatedDBObject(layer, true);
        }


        private static IEnumerable<Point2d> GetCandidateLabelPoints(
            Polyline quarter,
            Polyline disposition,
            Point2d target,
            bool allowOutsideDisposition,
            double step,
            int maxPoints)
        {
            var spiral = GeometryUtils.GetSpiralOffsets(target, step, maxPoints).ToList();

            if (!allowOutsideDisposition)
            {
                foreach (var p in spiral)
                {
                    if (PointInPolyline(quarter, p) && PointInPolyline(disposition, p))
                        yield return p;
                }
                yield break;
            }

            // Prefer points in quarter but outside the disposition first (PDF-style callouts)
            var inside = new List<Point2d>();
            foreach (var p in spiral)
            {
                if (!PointInPolyline(quarter, p))
                    continue;

                if (PointInPolyline(disposition, p))
                    inside.Add(p);
                else
                    yield return p;
            }

            foreach (var p in inside)
                yield return p;
        }

        private Point2d GetTargetPoint(Polyline quarter, Polyline disposition, Point2d fallback)
        {
            // Best: centroid of intersection region(s)
            if (_config.UseRegionIntersection && GeometryUtils.TryIntersectRegions(disposition, quarter, out var regions))
            {
                foreach (var region in regions)
                {
                    using (region)
                    {
                        var c = GetRegionCentroidSafe(region);
                        if (PointInPolyline(quarter, c) && PointInPolyline(disposition, c))
                            return c;
                    }
                }
            }

            // If safe point lies in this quarter
            if (PointInPolyline(quarter, fallback) && PointInPolyline(disposition, fallback))
                return fallback;

            // Try extents overlap center
            var overlap = GetExtentsOverlapCenter(quarter.GeometricExtents, disposition.GeometricExtents);
            if (PointInPolyline(quarter, overlap) && PointInPolyline(disposition, overlap))
                return overlap;

            // Closest point on disposition to a known interior point of quarter
            var qInterior = GeometryUtils.GetSafeInteriorPoint(quarter);
            try
            {
                var closest = disposition.GetClosestPointTo(new Point3d(qInterior.X, qInterior.Y, 0), false);
                var cp = new Point2d(closest.X, closest.Y);
                if (PointInPolyline(quarter, cp))
                    return cp;
            }
            catch { }

            return fallback;
        }

        private static Point2d GetRegionCentroidSafe(Region region)
        {
            try
            {
                var centroid = Point3d.Origin;
                var normal = Vector3d.ZAxis;
                var axes = Vector3d.XAxis;
                region.AreaProperties(ref centroid, ref normal, ref axes);

                if (IsFinite(centroid.X) && IsFinite(centroid.Y))
                    return new Point2d(centroid.X, centroid.Y);
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                // ignore
            }
            catch
            {
                // ignore
            }

            try
            {
                var ext = region.GeometricExtents;
                return new Point2d(
                    (ext.MinPoint.X + ext.MaxPoint.X) / 2.0,
                    (ext.MinPoint.Y + ext.MaxPoint.Y) / 2.0
                );
            }
            catch
            {
                return new Point2d(0, 0);
            }
        }

        private static Point2d GetExtentsOverlapCenter(Extents3d a, Extents3d b)
        {
            double minX = Math.Max(a.MinPoint.X, b.MinPoint.X);
            double maxX = Math.Min(a.MaxPoint.X, b.MaxPoint.X);

            double minY = Math.Max(a.MinPoint.Y, b.MinPoint.Y);
            double maxY = Math.Min(a.MaxPoint.Y, b.MaxPoint.Y);

            return new Point2d((minX + maxX) / 2.0, (minY + maxY) / 2.0);
        }


        private static bool IsFinite(double v)
        {
            return !(double.IsNaN(v) || double.IsInfinity(v));
        }

        private static bool PointInPolyline(Polyline pl, Point2d pt)
        {
            bool inside = false;
            int n = pl.NumberOfVertices;
            for (int i = 0, j = n - 1; i < n; j = i++)
            {
                var pi = pl.GetPoint2dAt(i);
                var pj = pl.GetPoint2dAt(j);

                bool intersect = ((pi.Y > pt.Y) != (pj.Y > pt.Y)) &&
                                 (pt.X < (pj.X - pi.X) * (pt.Y - pi.Y) /
                                     ((pj.Y - pi.Y) == 0 ? 1e-12 : (pj.Y - pi.Y)) + pi.X);

                if (intersect) inside = !inside;
            }
            return inside;
        }
    }

    public sealed class QuarterInfo
    {
        public QuarterInfo(Polyline polyline)
        {
            Polyline = polyline;
            Bounds = polyline.GeometricExtents;
        }

        public Polyline Polyline { get; }
        public Extents3d Bounds { get; }
    }

    public sealed class DispositionInfo
    {
        public DispositionInfo(ObjectId objectId, Polyline polyline, string labelText, string layerName, string textLayerName, Point2d safePoint)
        {
            ObjectId = objectId;
            Polyline = polyline;
            Bounds = polyline.GeometricExtents;
            LabelText = labelText;
            LayerName = layerName;
            TextLayerName = textLayerName;
            SafePoint = safePoint;
        }

        public ObjectId ObjectId { get; }
        public Polyline Polyline { get; }
        public Extents3d Bounds { get; }

        public string LabelText { get; }
        public string LayerName { get; }
        public string TextLayerName { get; }
        public Point2d SafePoint { get; }

        // For width-required purposes, allow label to be placed in the quarter (not necessarily in the disposition)
        public bool AllowLabelOutsideDisposition { get; set; }

        // Draw leader entities (circle + line) from target to label
        public bool AddLeader { get; set; }
    }

    public sealed class PlacementResult
    {
        public int LabelsPlaced { get; set; }
        public int SkippedNoLayerMapping { get; set; }
        public int OverlapForced { get; set; }
        public int MultiQuarterProcessed { get; set; }
    }
}
