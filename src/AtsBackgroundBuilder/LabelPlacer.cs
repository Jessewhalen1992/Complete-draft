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

                            // Confirm actual intersection and compute a per-quarter target.
                            // IMPORTANT: we MUST anchor and place the label per-quarter; using the disposition's
                            // global SafePoint will incorrectly bias labeling to the quarter containing that point.
                            Polyline? intersectionPiece;
                            Point2d intersectionTarget;
                            if (!TryGetQuarterIntersectionTarget(quarterClone, disposition.Polyline, out intersectionPiece, out intersectionTarget))
                            {
                                // Robust fallback (e.g. when the boolean intersection yields degenerate/open pieces):
                                // try to find ANY interior point that lies inside BOTH the quarter and the disposition.
                                if (!GeometryUtils.TryFindPointInsideBoth(quarterClone, disposition.Polyline, out intersectionTarget))
                                {
                                    _logger.WriteLine($"No quarter intersection: disp={disposition.ObjectId} quarterExt={quarterClone.GeometricExtents}");
                                    result.SkippedNoIntersection++;
                                    continue;
                                }
                            }

                            // Label layer mapping check
                            if (string.IsNullOrWhiteSpace(disposition.TextLayerName))
                            {
                                result.SkippedNoLayerMapping++;
                                if (intersectionPiece != null && !ReferenceEquals(intersectionPiece, disposition.Polyline))
                                    intersectionPiece.Dispose();
                                continue;
                            }

                            // Ensure label layer exists
                            EnsureLayerInTransaction(_database, transaction, disposition.TextLayerName);

                            using (var dispClone = (Polyline)disposition.Polyline.Clone())
                            {
                                var labelText = disposition.LabelText;
                                var textColorIndex = disposition.TextColorIndex;
                                double measuredWidth = 0.0;

                                // Default: per-quarter anchor/placement target.
                                // (Using disposition.SafePoint will bias multi-quarter dispositions to a single quarter.)
                                Point2d searchTarget = intersectionTarget;
                                Point2d leaderTarget = intersectionTarget;

                                if (disposition.RequiresWidth)
                                {
                                    var polyForWidth = intersectionPiece ?? dispClone;

                                    var measurement = GeometryUtils.MeasureCorridorWidth(
                                        polyForWidth,
                                        _config.WidthSampleCount,
                                        _config.VariableWidthAbsTolerance,
                                        _config.VariableWidthRelTolerance);

                                    measuredWidth = measurement.MedianWidth;

                                    // Always choose the closest acceptable width for the LABEL text.
                                    // Tolerance is only used for colour (match vs mismatch), not snapping choice.
                                    double snapped = measuredWidth;
                                    double diffToSnapped = double.MaxValue;
                                    if (_config.AcceptableRowWidths != null && _config.AcceptableRowWidths.Length > 0)
                                    {
                                        snapped = _config.AcceptableRowWidths
                                            .OrderBy(w => Math.Abs(measuredWidth - w))
                                            .ThenBy(w => w)
                                            .First();
                                        diffToSnapped = Math.Abs(measuredWidth - snapped);
                                    }

                                    bool isVariable = measurement.IsVariable;
                                    // If the corridor measured as variable but its median is effectively on a standard width,
                                    // treat as fixed. (This prevents false "Variable Width" classifications.)
                                    if (isVariable && diffToSnapped <= _config.WidthSnapTolerance)
                                        isVariable = false;

                                    if (isVariable)
                                    {
                                        labelText = disposition.MappedCompany + "\\P" + "Variable Width" + "\\P" + disposition.PurposeTitleCase + "\\P" + disposition.DispNumFormatted;
                                        textColorIndex = 3;
                                    }
                                    else
                                    {
                                        var widthText = snapped.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                                        labelText = disposition.MappedCompany + "\\P" + widthText + " " + disposition.MappedPurpose + "\\P" + disposition.DispNumFormatted;

                                        // Green = measured width does NOT match the snapped width within tolerance.
                                        bool matches = diffToSnapped <= _config.WidthSnapTolerance;
                                        textColorIndex = matches ? 256 : 3;
                                    }

                                    // Leader anchor: a centerline point in THIS quarter's corridor piece.
                                    // Prefer the measured median center sample if it's inside the quarter; otherwise use the per-quarter intersection target.
                                    Point2d leaderCandidate = measurement.MedianCenter;
                                    if (!GeometryUtils.IsPointInsidePolyline(quarterClone, leaderCandidate) ||
                                        !GeometryUtils.IsPointInsidePolyline(polyForWidth, leaderCandidate))
                                    {
                                        leaderCandidate = intersectionTarget;
                                    }

                                    // Refine to the mid-width point so the leader endpoint doesn't land on the corridor edge.
                                    if (GeometryUtils.TryGetCrossSectionMidpoint(polyForWidth, leaderCandidate, out var mid, out _))
                                        leaderCandidate = mid;

                                    // Final safety: keep leader target inside both the quarter and the corridor piece.
                                    if (!GeometryUtils.IsPointInsidePolyline(quarterClone, leaderCandidate) ||
                                        !GeometryUtils.IsPointInsidePolyline(polyForWidth, leaderCandidate))
                                    {
                                        leaderCandidate = intersectionTarget;
                                        if (GeometryUtils.TryGetCrossSectionMidpoint(polyForWidth, leaderCandidate, out var mid2, out _))
                                        {
                                            if (GeometryUtils.IsPointInsidePolyline(quarterClone, mid2) &&
                                                GeometryUtils.IsPointInsidePolyline(polyForWidth, mid2))
                                                leaderCandidate = mid2;
                                        }
                                    }

                                    leaderTarget = leaderCandidate;
                                }

                                // Defensive: if the intersection target landed just outside due to numerical issues,
                                // find a valid in-both point; do NOT fall back to SafePoint outside this quarter.
                                if (!GeometryUtils.IsPointInsidePolyline(quarterClone, searchTarget) ||
                                    !GeometryUtils.IsPointInsidePolyline(dispClone, searchTarget))
                                {
                                    if (GeometryUtils.TryFindPointInsideBoth(quarterClone, dispClone, out var altTarget))
                                        searchTarget = altTarget;
                                }

                                // If we still don't have a valid in-both target, do NOT attempt a quarter label.
                                // (This prevents "SE label" attempts from accidentally using an SW safe point / target.)
                                if (!GeometryUtils.IsPointInsidePolyline(quarterClone, searchTarget) ||
                                    !GeometryUtils.IsPointInsidePolyline(dispClone, searchTarget))
                                {
                                    _logger.WriteLine($"Skip label (no valid in-both target): disp={disposition.ObjectId}");
                                    continue;
                                }

                                // Candidate label points around the target
                                var candidates = GetCandidateLabelPoints(
                                        quarterClone,
                                        dispClone,
                                        searchTarget,
                                        disposition.AllowLabelOutsideDisposition,
                                        _config.TextHeight,
                                        _config.MaxOverlapAttempts,
                                        measuredWidth)
                                    .ToList();

                                if (candidates.Count == 0)
                                {
                                    // Don’t place labels using an out-of-quarter/out-of-disposition fallback target
                                    if (!GeometryUtils.IsPointInsidePolyline(quarterClone, searchTarget) ||
                                        !GeometryUtils.IsPointInsidePolyline(dispClone, searchTarget))
                                    {
                                        _logger.WriteLine($"Skip label (no valid in-shape target): disp={disposition.ObjectId}");
                                        continue;
                                    }

                                    candidates.Add(searchTarget);
                                }

                                bool placed = false;
                                Point2d lastCandidate = candidates[candidates.Count - 1];

                                foreach (var pt in candidates)
                                {
                                    lastCandidate = pt;

                                    var predicted = EstimateTextExtents(pt, labelText, _config.TextHeight);
                                    bool overlaps = placedLabelExtents.Any(b => GeometryUtils.ExtentsIntersect(b, predicted));

                                    if (overlaps)
                                    {
                                        continue;
                                    }

                                    CreateLabelEntity(transaction, modelSpace, leaderTarget, pt, disposition, labelText, textColorIndex);

                                    // Success
                                    placedLabelExtents.Add(predicted);
                                    placed = true;
                                    result.LabelsPlaced++;
                                    break;
                                }

                                if (!placed && _config.PlaceWhenOverlapFails && candidates.Count > 0)
                                {
                                    // Forced placement at last candidate
                                    var predicted = EstimateTextExtents(lastCandidate, labelText, _config.TextHeight);
                                    CreateLabelEntity(transaction, modelSpace, leaderTarget, lastCandidate, disposition, labelText, textColorIndex);

                                    placedLabelExtents.Add(predicted);
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

                            if (intersectionPiece != null && !ReferenceEquals(intersectionPiece, disposition.Polyline))
                            {
                                intersectionPiece.Dispose();
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

        private static Extents3d EstimateTextExtents(Point2d center, string labelText, double textHeight)
        {
            if (textHeight <= 0) textHeight = 10.0;
            var lines = (labelText ?? string.Empty).Split(new[] { "\\P" }, StringSplitOptions.None);
            int lineCount = Math.Max(1, lines.Length);

            int maxChars = 0;
            foreach (var ln in lines)
            {
                int c = 0;
                for (int i = 0; i < ln.Length; i++)
                {
                    char ch = ln[i];
                    if (ch == '\\')
                    {
                        if (i + 1 < ln.Length) i++;
                        continue;
                    }
                    if (ch == '{' || ch == '}') continue;
                    c++;
                }
                if (c > maxChars) maxChars = c;
            }

            double charWidth = textHeight * 0.6;
            double width = Math.Max(textHeight, maxChars * charWidth);
            double height = lineCount * textHeight * 1.2;

            double pad = textHeight * 0.3;
            width += pad * 2;
            height += pad * 2;

            var min = new Point3d(center.X - width / 2, center.Y - height / 2, 0);
            var max = new Point3d(center.X + width / 2, center.Y + height / 2, 0);
            return new Extents3d(min, max);
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

        private bool TryGetQuarterIntersectionTarget(
            Polyline quarter,
            Polyline disposition,
            out Polyline? intersectionPiece,
            out Point2d target)
        {
            intersectionPiece = null;
            target = default;

            // First try true polygon intersection: disposition ∩ quarter
            if (GeometryUtils.TryIntersectPolylines(disposition, quarter, out var pieces) && pieces.Count > 0)
            {
                // keep only closed pieces
                var closed = pieces.Where(p => p != null && p.Closed && p.NumberOfVertices >= 3).ToList();
                if (closed.Count > 0)
                {
                    // pick the piece with the largest area (fallback to extents area if Area throws)
                    Polyline best = closed[0];
                    double bestScore = -1;

                    foreach (var p in closed)
                    {
                        double score;
                        try { score = Math.Abs(p.Area); }
                        catch
                        {
                            var e = p.GeometricExtents;
                            score = Math.Abs((e.MaxPoint.X - e.MinPoint.X) * (e.MaxPoint.Y - e.MinPoint.Y));
                        }

                        if (score > bestScore)
                        {
                            bestScore = score;
                            best = p;
                        }
                    }

                    intersectionPiece = best;

                    // target = safe interior of intersection piece
                    target = GeometryUtils.GetSafeInteriorPoint(best);

                    // Dispose non-best pieces (TryIntersectPolylines returns DBObjects the caller owns).
                    // IMPORTANT: dispose BOTH the closed and any open/degenerate pieces we aren't returning,
                    // otherwise repeated quarter processing leaks DBObjects.
                    foreach (var p in pieces)
                    {
                        if (p != null && !ReferenceEquals(p, best))
                            p.Dispose();
                    }
                    return true;
                }

                // Dispose pieces if none were usable
                foreach (var p in pieces) p.Dispose();
            }

            // Fallback: spiral search inside BOTH polygons within overlap extents
            var overlap = GetExtentsOverlapCenter(quarter.GeometricExtents, disposition.GeometricExtents);
            double step = Math.Max(_config.TextHeight, 5.0);
            foreach (var p in GeometryUtils.GetSpiralOffsets(overlap, step, 200))
            {
                if (GeometryUtils.IsPointInsidePolyline(quarter, p) &&
                    GeometryUtils.IsPointInsidePolyline(disposition, p))
                {
                    target = p;
                    return true;
                }
            }

            return false;
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
        public int SkippedNoIntersection { get; set; }
        public int OverlapForced { get; set; }
        public int MultiQuarterProcessed { get; set; }
    }
}
