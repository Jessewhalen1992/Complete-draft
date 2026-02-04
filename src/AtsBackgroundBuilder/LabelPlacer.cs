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
            var countedDispositionIds = new HashSet<ObjectId>();

            using (var transaction = _database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)transaction.GetObject(_database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                // Track extents of labels placed so far so we can avoid overlaps
                var placedLabelExtents = new List<Extents3d>();
                _logger.WriteLine($"Quarter polylines (unique): {quarters.Count}");
                _logger.WriteLine($"Dispositions: {dispositions.Count}");

                foreach (var quarter in quarters)
                {
                    using (var quarterClone = (Polyline)quarter.Polyline.Clone())
                    {
                        foreach (var disposition in dispositions)
                        {
                            if (!_config.AllowMultiQuarterDispositions && processedDispositionIds.Contains(disposition.ObjectId))
                                continue;

                            if (countedDispositionIds.Contains(disposition.ObjectId))
                                result.MultiQuarterProcessed++;
                            else
                                countedDispositionIds.Add(disposition.ObjectId);

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
                                var labelText = disposition.LabelText;
                                var textColorIndex = disposition.TextColorIndex;
                                var safePoint = disposition.SafePoint;
                                double measuredWidth = 0.0;

                                if (disposition.RequiresWidth)
                                {
                                    // Clip the disposition clone to the quarter boundary
                                    var clipped = GeometryUtils.IntersectPolylineWithRect(
                                        dispClone,
                                        quarterClone.GeometricExtents.MinPoint,
                                        quarterClone.GeometricExtents.MaxPoint);
                                    // fall back to the full clone if clipping fails
                                    var polyForWidth = clipped ?? dispClone;

                                    var measurement = GeometryUtils.MeasureCorridorWidth(
                                        polyForWidth,
                                        _config.WidthSampleCount,
                                        _config.VariableWidthAbsTolerance,
                                        _config.VariableWidthRelTolerance);

                                    safePoint = measurement.MedianCenter;
                                    measuredWidth = measurement.MedianWidth;

                                    double median = measuredWidth;
                                    double nearestInt = Math.Round(measuredWidth, 0, MidpointRounding.AwayFromZero);

                                    double bestException = median;
                                    double diffException = double.MaxValue;
                                    if (_config.AcceptableRowWidths != null && _config.AcceptableRowWidths.Length > 0)
                                    {
                                        bestException = _config.AcceptableRowWidths
                                            .OrderBy(w => Math.Abs(measuredWidth - w))
                                            .First();
                                        diffException = Math.Abs(measuredWidth - bestException);
                                    }
                                    double diffInt = Math.Abs(measuredWidth - nearestInt);

                                    double snapped;
                                    bool snappedToAcceptable = diffException <= diffInt || diffException <= _config.WidthSnapTolerance;
                                    if (snappedToAcceptable)
                                        snapped = bestException;
                                    else
                                        snapped = nearestInt;

                                    bool isVariable = measurement.IsVariable;
                                    if (isVariable && Math.Abs(snapped - measuredWidth) <= _config.WidthSnapTolerance)
                                    {
                                        isVariable = false;
                                    }

                                    bool snappedIsInAcceptable = _config.AcceptableRowWidths != null
                                        && _config.AcceptableRowWidths.Any(w => Math.Abs(w - snapped) <= 1e-4);
                                    bool hasMatchingWidth = !isVariable && snappedIsInAcceptable;
                                    if (isVariable)
                                    {
                                        labelText = disposition.MappedCompany + "\\P" + "Variable Width" + "\\P" + disposition.PurposeTitleCase + "\\P" + disposition.DispNumFormatted;
                                    }
                                    else
                                    {
                                        var widthText = snapped.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                                        labelText = disposition.MappedCompany + "\\P" + widthText + " " + disposition.MappedPurpose + "\\P" + disposition.DispNumFormatted;
                                    }

                                    textColorIndex = hasMatchingWidth ? 256 : 3;
                                }

                                // Leader target point (inside quarter ∩ disposition whenever possible)
                                var target = disposition.RequiresWidth ? safePoint : GetTargetPoint(quarterClone, dispClone, safePoint);

                                // Candidate label points around the target
                                var candidates = GetCandidateLabelPoints(
                                        quarterClone,
                                        dispClone,
                                        target,
                                        disposition.AllowLabelOutsideDisposition,
                                        _config.TextHeight,
                                        _config.MaxOverlapAttempts,
                                        measuredWidth)
                                    .ToList();

                                if (candidates.Count == 0)
                                {
                                    // If we have no valid candidate inside this quarter, do not place a label for this quarter.
                                    // Falling back to an out-of-quarter target causes stacked/double labels in one quarter.
                                    if (!GeometryUtils.IsPointInsidePolyline(quarterClone, target))
                                        continue;

                                    candidates.Add(target);
                                }

                                bool placed = false;
                                Point2d lastCandidate = candidates[candidates.Count - 1];

                                foreach (var pt in candidates)
                                {
                                    lastCandidate = pt;

                                    var labelEntity = CreateLabelEntity(transaction, modelSpace, target, pt, disposition, labelText, textColorIndex);
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
                                    var labelEntity = CreateLabelEntity(transaction, modelSpace, target, lastCandidate, disposition, labelText, textColorIndex);

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

                                if (!_config.AllowMultiQuarterDispositions)
                                    processedDispositionIds.Add(disposition.ObjectId);
                            }
                        }
                    }
                }

                transaction.Commit();
            }

            return result;
        }

        private Entity CreateLabelEntity(
            Transaction tr,
            BlockTableRecord modelSpace,
            Point2d target,
            Point2d labelPoint,
            DispositionInfo disposition,
            string labelText,
            int textColorIndex)
        {
            if (_config.EnableLeaders && disposition.AddLeader)
            {
                return CreateLeader(tr, modelSpace, target, labelPoint, labelText, disposition.TextLayerName, textColorIndex);
            }

            var mtext = CreateLabel(tr, labelPoint, labelText, disposition.TextLayerName, textColorIndex);
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
            string layerName,
            int colorIndex)
        {
            var mtext = new MText
            {
                Location = new Point3d(labelPoint.X, labelPoint.Y, 0),
                TextHeight = _config.TextHeight,
                Contents = labelText,
                Layer = layerName,
                ColorIndex = colorIndex,
                Attachment = AttachmentPoint.MiddleCenter
            };
            ApplyDimensionStyle(tr, mtext, out _);

            var mleader = new MLeader();
            mleader.SetDatabaseDefaults();
            ApplyLeaderStyle(tr, mleader);
            mleader.ContentType = ContentType.MTextContent;
            mleader.MText = mtext;
            mleader.TextAttachmentType = TextAttachmentType.AttachmentMiddle;
            // Create a leader cluster and line
            int leaderIndex = mleader.AddLeader();
            int lineIndex = mleader.AddLeaderLine(leaderIndex);

            // Set the start and end points of the leader line
            mleader.AddFirstVertex(lineIndex, new Point3d(target.X, target.Y, 0));
            mleader.AddLastVertex(lineIndex, new Point3d(labelPoint.X, labelPoint.Y, 0));

            mleader.LeaderLineType = LeaderType.StraightLeader;
            mleader.EnableLanding = _config.LeaderHorizontalLanding;
            if (_config.LeaderHorizontalLanding)
            {
                mleader.DoglegLength = _config.LeaderLandingDistance;
                mleader.LandingGap = _config.LeaderLandingGap;
            }

            // Assign an arrow block (e.g. dot blank) via ArrowSymbolId as needed; no HasArrowHead property exists
            var arrowId = GetLeaderArrowId(tr);
            if (!arrowId.IsNull)
                mleader.ArrowSymbolId = arrowId;
            mleader.ArrowSize = 5.0;

            mleader.Layer = layerName;
            mleader.ColorIndex = colorIndex;

            modelSpace.AppendEntity(mleader);
            tr.AddNewlyCreatedDBObject(mleader, true);
            return mleader;
        }

        private MText CreateLabel(Transaction tr, Point2d labelPoint, string labelText, string layerName, int colorIndex)
        {
            var mtext = new MText
            {
                Location = new Point3d(labelPoint.X, labelPoint.Y, 0),
                TextHeight = _config.TextHeight,
                Contents = labelText,
                Layer = layerName,
                ColorIndex = colorIndex,
                Attachment = AttachmentPoint.MiddleCenter
            };

            ApplyDimensionStyle(tr, mtext, out _);
            return mtext;
        }

        private void ApplyDimensionStyle(Transaction tr, MText mtext, out ObjectId dimStyleId)
        {
            dimStyleId = ObjectId.Null;
            if (tr == null || mtext == null) return;

            var dimStyleTable = (DimStyleTable)tr.GetObject(_database.DimStyleTableId, OpenMode.ForRead);
            if (!dimStyleTable.Has(_config.DimensionStyleName))
                return;

            dimStyleId = dimStyleTable[_config.DimensionStyleName];
            var dimStyle = (DimStyleTableRecord)tr.GetObject(dimStyleId, OpenMode.ForRead);
            if (!dimStyle.Dimtxsty.IsNull)
                mtext.TextStyleId = dimStyle.Dimtxsty;
        }

        private void ApplyLeaderStyle(Transaction tr, MLeader mleader)
        {
            if (tr == null || mleader == null) return;

            var leaderStyleDictionary = (DBDictionary)tr.GetObject(_database.MLeaderStyleDictionaryId, OpenMode.ForRead);
            if (!leaderStyleDictionary.Contains("Dispo-Labels"))
                return;

            mleader.MLeaderStyle = leaderStyleDictionary.GetAt("Dispo-Labels");
        }

        private static AttachmentPoint GetLeaderAttachment(Point2d target, Point2d labelPoint)
        {
            var dx = labelPoint.X - target.X;
            if (Math.Abs(dx) < 1e-6)
                return AttachmentPoint.MiddleCenter;

            return dx < 0 ? AttachmentPoint.MiddleRight : AttachmentPoint.MiddleLeft;
        }

        private static TextAttachmentType GetLeaderTextAttachment(AttachmentPoint attachment)
        {
            // AutoCAD 2025's TextAttachmentType enum doesn’t define MiddleLeft/MiddleRight/MiddleCenter.
            // Use a neutral attachment type that aligns text to the middle for both left and right leaders.
            return TextAttachmentType.AttachmentMiddle;
        }

        private ObjectId GetLeaderArrowId(Transaction tr)
        {
            var blockTable = (BlockTable)tr.GetObject(_database.BlockTableId, OpenMode.ForRead);
            var preferred = _config.LeaderArrowBlockName?.Trim();
            var candidateNames = new List<string>();
            if (!string.IsNullOrWhiteSpace(preferred))
            {
                candidateNames.Add(preferred);
                candidateNames.Add("_" + preferred);
                candidateNames.Add(preferred.ToUpperInvariant());
                candidateNames.Add("_" + preferred.ToUpperInvariant());
            }

            candidateNames.AddRange(new[]
            {
                "DotBlank",
                "_DotBlank",
                "_DOTBLANK",
                "DOTBLANK",
                "_Dot",
                "_DOT",
                "DOT",
                "_DotSmall",
                "_DOTSMALL",
                "DOTSMALL"
            });

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
            int maxPoints,
            double measuredWidth)
        {
            var spiral = GeometryUtils.GetSpiralOffsets(target, step, maxPoints).ToList();
            double minDistance = step * 0.5;
            double minHalfWidth = measuredWidth * 0.25;

            if (!allowOutsideDisposition)
            {
                foreach (var p in spiral)
                {
                    if (GeometryUtils.IsPointInsidePolyline(quarter, p) && GeometryUtils.IsPointInsidePolyline(disposition, p))
                    {
                        var p3d = new Point3d(p.X, p.Y, 0);
                        var closest = disposition.GetClosestPointTo(p3d, false);
                        if (closest.DistanceTo(p3d) >= Math.Max(minDistance, minHalfWidth))
                            yield return p;
                    }
                }
                yield break;
            }

            // Prefer points in quarter but outside the disposition first (PDF-style callouts)
            var inside = new List<Point2d>();
            foreach (var p in spiral)
            {
                if (!GeometryUtils.IsPointInsidePolyline(quarter, p))
                    continue;

                if (GeometryUtils.IsPointInsidePolyline(disposition, p))
                {
                    var p3d = new Point3d(p.X, p.Y, 0);
                    var closest = disposition.GetClosestPointTo(p3d, false);
                    if (closest.DistanceTo(p3d) >= Math.Max(minDistance, minHalfWidth))
                        inside.Add(p);
                }
                else
                {
                    var p3d = new Point3d(p.X, p.Y, 0);
                    var closest = disposition.GetClosestPointTo(p3d, false);
                    if (closest.DistanceTo(p3d) >= Math.Max(minDistance, minHalfWidth))
                        yield return p;
                }
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
                        if (GeometryUtils.IsPointInsidePolyline(quarter, c) && GeometryUtils.IsPointInsidePolyline(disposition, c))
                            return c;
                    }
                }
            }

            // If safe point lies in this quarter
            if (GeometryUtils.IsPointInsidePolyline(quarter, fallback) && GeometryUtils.IsPointInsidePolyline(disposition, fallback))
                return fallback;

            // Try extents overlap center
            var overlap = GetExtentsOverlapCenter(quarter.GeometricExtents, disposition.GeometricExtents);
            if (GeometryUtils.IsPointInsidePolyline(quarter, overlap) && GeometryUtils.IsPointInsidePolyline(disposition, overlap))
                return overlap;

            // Closest point on disposition to a known interior point of quarter
            var qInterior = GeometryUtils.GetSafeInteriorPoint(quarter);
            try
            {
                var closest = disposition.GetClosestPointTo(new Point3d(qInterior.X, qInterior.Y, 0), false);
                var cp = new Point2d(closest.X, closest.Y);
                if (GeometryUtils.IsPointInsidePolyline(quarter, cp))
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
        public int TextColorIndex { get; set; } = 256;
        public bool RequiresWidth { get; set; }
        public string MappedCompany { get; set; } = string.Empty;
        public string MappedPurpose { get; set; } = string.Empty;
        public string PurposeTitleCase { get; set; } = string.Empty;
        public string DispNumFormatted { get; set; } = string.Empty;

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
