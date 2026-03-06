/////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AtsBackgroundBuilder.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

namespace AtsBackgroundBuilder.Dispositions
{
    public sealed class LabelPlacer
    {
        private readonly Database _database;
        private readonly Editor _editor;
        private readonly LayerManager _layerManager;
        private readonly Config _config;
        private readonly Logger _logger;
        private readonly bool _useAlignedDimensions;

        public LabelPlacer(Database database, Editor editor, LayerManager layerManager, Config config, Logger logger, bool useAlignedDimensions = false)
        {
            _database = database;
            _editor = editor;
            _layerManager = layerManager;
            _config = config;
            _logger = logger;
            _useAlignedDimensions = useAlignedDimensions;
        }

        public PlacementResult PlaceLabels(
            List<QuarterInfo> quarters,
            List<DispositionInfo> dispositions,
            string currentClient,
            Dictionary<string, HashSet<string>>? existingDispNumsByQuarter = null)
        {
            var result = new PlacementResult();
            var processedDispositionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var countedDispositionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                    var quarterKey = BuildQuarterKey(quarter.SectionKey, quarter.Quarter);
                    HashSet<string>? existingQuarterDispNums = null;
                    if (!string.IsNullOrWhiteSpace(quarterKey) && existingDispNumsByQuarter != null)
                    {
                        existingDispNumsByQuarter.TryGetValue(quarterKey, out existingQuarterDispNums);
                    }

                    using (var quarterClone = (Polyline)quarter.Polyline.Clone())
                    {
                        foreach (var disposition in dispositions)
                        {
                            var normalizedDispNum = NormalizeDispNum(disposition.DispNumFormatted);
                            var normalizedReuseVariant = NormalizeDispositionReuseVariant(disposition.ReuseVariantKey);
                            var dispositionReuseKey = BuildDispositionReuseKey(normalizedDispNum, normalizedReuseVariant);
                            if (existingQuarterDispNums != null &&
                                !string.IsNullOrWhiteSpace(normalizedDispNum))
                            {
                                var shouldSkip = string.IsNullOrWhiteSpace(normalizedReuseVariant)
                                    ? existingQuarterDispNums.Contains(normalizedDispNum)
                                    : existingQuarterDispNums.Contains(dispositionReuseKey);
                                if (shouldSkip)
                                {
                                    continue;
                                }
                            }

                            var dispositionIdentity = BuildDispositionIdentity(disposition);
                            if (!_config.AllowMultiQuarterDispositions && processedDispositionIds.Contains(dispositionIdentity))
                                continue;

                            if (countedDispositionIds.Contains(dispositionIdentity))
                                result.MultiQuarterProcessed++;
                            else
                                countedDispositionIds.Add(dispositionIdentity);

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

                                // Mixed wellsite+access dispositions are a single boundary: bias the wellsite
                                // variant toward the most interior/high-clearance pad point.
                                if (string.Equals(
                                        NormalizeDispositionReuseVariant(disposition.ReuseVariantKey),
                                        DispositionInfo.ReuseVariantMixedWellsitePad,
                                        StringComparison.OrdinalIgnoreCase))
                                {
                                    if (TryFindMixedWellsitePadTarget(
                                            quarterClone,
                                            dispClone,
                                            intersectionPiece ?? dispClone,
                                            disposition.SafePoint,
                                            _config.TextHeight,
                                            out var padTarget))
                                    {
                                        searchTarget = padTarget;
                                        leaderTarget = padTarget;
                                    }
                                }

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
                                    bool usedOdFallbackWidth = false;

                                    // If the corridor measured as variable but its median is effectively on a standard width,
                                    // treat as fixed. (This prevents false "Variable Width" classifications.)
                                    if (isVariable && diffToSnapped <= _config.WidthSnapTolerance)
                                        isVariable = false;

                                    // Fallback: if measured as variable near complex corners, use OD DIMENSION width
                                    // when it clearly matches a standard width. Keep label green for awareness.
                                    if (isVariable &&
                                        TryParseOdDimensionWidth(disposition.OdDimension, out var odWidthMeters) &&
                                        _config.AcceptableRowWidths != null &&
                                        _config.AcceptableRowWidths.Length > 0)
                                    {
                                        var odSnapped = _config.AcceptableRowWidths
                                            .OrderBy(w => Math.Abs(odWidthMeters - w))
                                            .ThenBy(w => w)
                                            .First();
                                        var odDiff = Math.Abs(odWidthMeters - odSnapped);
                                        if (odDiff <= _config.WidthSnapTolerance)
                                        {
                                            snapped = odSnapped;
                                            isVariable = false;
                                            usedOdFallbackWidth = true;
                                        }
                                    }

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
                                        textColorIndex = (matches && !usedOdFallbackWidth) ? 256 : 3;
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

                                    leaderTarget = ChooseLeaderTargetAvoidingOtherDispositions(
                                        leaderCandidate,
                                        intersectionTarget,
                                        quarterClone,
                                        polyForWidth,
                                        disposition,
                                        dispositions);
                                }

                                if (disposition.ShouldAddExpiredMarker)
                                {
                                    labelText = AppendExpiredMarkerIfMissing(labelText);
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
                                int maxPoints = _config.MaxOverlapAttempts;
                                if (disposition.AddLeader)
                                    maxPoints = Math.Max(maxPoints, 160);

                                double maxLeaderLength = disposition.AddLeader ? 300.0 : double.PositiveInfinity;
                                if (_useAlignedDimensions && disposition.RequiresWidth)
                                {
                                    // Keep A-DIM local, but allow enough room to avoid forcing every label.
                                    maxLeaderLength = Math.Max(45.0, _config.TextHeight * 10.0);
                                }

                                var allowOutsideDisposition = disposition.AllowLabelOutsideDisposition;
                                if (_useAlignedDimensions && disposition.RequiresWidth)
                                {
                                    allowOutsideDisposition = false;
                                }

                                var candidates = GetCandidateLabelPoints(
                                        quarterClone,
                                        dispClone,
                                        searchTarget,
                                        allowOutsideDisposition,
                                        _config.TextHeight,
                                        maxPoints,
                                        measuredWidth,
                                        maxLeaderLength)
                                    .ToList();

                                if (candidates.Count == 0)
                                {
                                    // Do not place labels using an out-of-quarter/out-of-disposition fallback target
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
                                Point2d bestFallback = lastCandidate;
                                double bestFallbackScore = double.MaxValue;
                                var labelGap = _config.TextHeight * 1.6;
                                if (disposition.AddLeader)
                                {
                                    labelGap = Math.Max(labelGap, _config.TextHeight * 2.1);
                                }
                                else if (_useAlignedDimensions && disposition.RequiresWidth)
                                {
                                    labelGap = Math.Max(labelGap, _config.TextHeight * 1.9);
                                }

                                foreach (var pt in candidates)
                                {
                                    lastCandidate = pt;

                                    var predicted = InflateExtents(EstimateTextExtents(pt, labelText, _config.TextHeight), labelGap);
                                    int labelOverlapCount = placedLabelExtents.Count(b => GeometryUtils.ExtentsIntersect(b, predicted));
                                    int crowdednessCount = CountNearbyPlacedLabels(placedLabelExtents, predicted, _config.TextHeight * 3.0);
                                    int lineworkOverlapCount = CountIntersectingDispositionLinework(predicted, dispositions, disposition);
                                    bool overlaps = labelOverlapCount > 0 || lineworkOverlapCount > 0;

                                    if (overlaps)
                                    {
                                        var fallbackScore = (labelOverlapCount * 100000.0) +
                                                            (lineworkOverlapCount * 1000.0) +
                                                            (crowdednessCount * 200.0) +
                                                            pt.GetDistanceTo(searchTarget);
                                        if (fallbackScore < bestFallbackScore)
                                        {
                                            bestFallbackScore = fallbackScore;
                                            bestFallback = pt;
                                        }
                                        continue;
                                    }

                                    CreateLabelEntity(transaction, modelSpace, leaderTarget, pt, polyForWidth: intersectionPiece ?? dispClone, disposition, labelText, textColorIndex);

                                    // Success
                                    placedLabelExtents.Add(predicted);
                                    placed = true;
                                    result.LabelsPlaced++;
                                    if (existingDispNumsByQuarter != null &&
                                        !string.IsNullOrWhiteSpace(quarterKey) &&
                                        !string.IsNullOrWhiteSpace(normalizedDispNum))
                                    {
                                        if (existingQuarterDispNums == null)
                                        {
                                            existingQuarterDispNums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                            existingDispNumsByQuarter[quarterKey] = existingQuarterDispNums;
                                        }

                                        existingQuarterDispNums.Add(normalizedDispNum);
                                        if (!string.IsNullOrWhiteSpace(normalizedReuseVariant))
                                        {
                                            existingQuarterDispNums.Add(dispositionReuseKey);
                                        }
                                    }
                                    break;
                                }

                                if (!placed && _config.PlaceWhenOverlapFails && candidates.Count > 0)
                                {
                                    // Forced placement at last candidate
                                    var predicted = InflateExtents(EstimateTextExtents(bestFallback, labelText, _config.TextHeight), labelGap);
                                    CreateLabelEntity(
                                        transaction,
                                        modelSpace,
                                        leaderTarget,
                                        bestFallback,
                                        polyForWidth: intersectionPiece ?? dispClone,
                                        disposition,
                                        labelText,
                                        textColorIndex);

                                    placedLabelExtents.Add(predicted);
                                    result.LabelsPlaced++;
                                    result.OverlapForced++;
                                    if (existingDispNumsByQuarter != null &&
                                        !string.IsNullOrWhiteSpace(quarterKey) &&
                                        !string.IsNullOrWhiteSpace(normalizedDispNum))
                                    {
                                        if (existingQuarterDispNums == null)
                                        {
                                            existingQuarterDispNums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                            existingDispNumsByQuarter[quarterKey] = existingQuarterDispNums;
                                        }

                                        existingQuarterDispNums.Add(normalizedDispNum);
                                        if (!string.IsNullOrWhiteSpace(normalizedReuseVariant))
                                        {
                                            existingQuarterDispNums.Add(dispositionReuseKey);
                                        }
                                    }

                                    placed = true;
                                }

                                if (!placed)
                                {
                                    // Not counted in legacy result, but keep debug output
                                    _logger.WriteLine($"Could not place label for disposition {disposition.ObjectId}");
                                }

                                if (!_config.AllowMultiQuarterDispositions)
                                    processedDispositionIds.Add(dispositionIdentity);
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
            Polyline polyForWidth,
            DispositionInfo disposition,
            string labelText,
            int textColorIndex)
        {
            var resolvedTextColorIndex = DispositionLabelColorPolicy.ResolveTextColorIndex(labelText, textColorIndex);

            if (_useAlignedDimensions && disposition.RequiresWidth)
            {
                return CreateAlignedDimensionLabel(tr, modelSpace, target, labelPoint, polyForWidth, disposition, labelText, disposition.TextLayerName, resolvedTextColorIndex);
            }

            if (_config.EnableLeaders && disposition.AddLeader)
            {
                return CreateLeader(tr, modelSpace, target, labelPoint, labelText, disposition.TextLayerName, resolvedTextColorIndex);
            }

            var mtext = CreateLabel(tr, labelPoint, labelText, disposition.TextLayerName, resolvedTextColorIndex);
            modelSpace.AppendEntity(mtext);
            tr.AddNewlyCreatedDBObject(mtext, true);
            return mtext;
        }

        private AlignedDimension CreateAlignedDimensionLabel(
            Transaction tr,
            BlockTableRecord modelSpace,
            Point2d target,
            Point2d labelPoint,
            Polyline polyForWidth,
            DispositionInfo disposition,
            string labelText,
            string layerName,
            int colorIndex)
        {
            Point2d a2d;
            Point2d b2d;
            var widthSource = polyForWidth ?? disposition.Polyline;
            var spanTarget = target;
            if (GeometryUtils.TryGetCrossSectionMidpoint(widthSource, spanTarget, out var midTarget, out _))
            {
                spanTarget = midTarget;
            }

            if (!TryGetPerpendicularSpanAcrossDisposition(widthSource, spanTarget, out a2d, out b2d))
            {
                // Fallback to a short line near target if a robust cross-section can't be found.
                var fallbackDir = new Vector2d(labelPoint.X - target.X, labelPoint.Y - target.Y);
                if (fallbackDir.Length <= 1e-6) fallbackDir = new Vector2d(1.0, 0.0);
                fallbackDir = fallbackDir / fallbackDir.Length;
                var half = Math.Max(_config.TextHeight, 10.0);
                a2d = new Point2d(target.X - fallbackDir.X * half, target.Y - fallbackDir.Y * half);
                b2d = new Point2d(target.X + fallbackDir.X * half, target.Y + fallbackDir.Y * half);
            }

            var expectedWidth = TryExtractExpectedWidthFromLabelText(labelText);
            if (expectedWidth > 0.0)
            {
                var measuredSpan = a2d.GetDistanceTo(b2d);
                var snapTol = Math.Max(_config.WidthSnapTolerance * 2.0, 0.25);
                if (Math.Abs(measuredSpan - expectedWidth) <= snapTol && measuredSpan > 1e-6)
                {
                    var half = expectedWidth * 0.5;
                    var center = new Point2d((a2d.X + b2d.X) * 0.5, (a2d.Y + b2d.Y) * 0.5);
                    var axis = (b2d - a2d) / measuredSpan;
                    a2d = new Point2d(center.X - axis.X * half, center.Y - axis.Y * half);
                    b2d = new Point2d(center.X + axis.X * half, center.Y + axis.Y * half);
                }
            }

            var p1 = new Point3d(a2d.X, a2d.Y, 0.0);
            var p2 = new Point3d(b2d.X, b2d.Y, 0.0);
            // Keep the dimension line offset on the true normal of the measured span and clamp drift.
            var span = b2d - a2d;
            if (span.Length <= 1e-6) span = new Vector2d(1.0, 0.0);
            var spanUnit = span / span.Length;
            var normal = new Vector2d(-spanUnit.Y, spanUnit.X);
            var mid = new Point2d((a2d.X + b2d.X) * 0.5, (a2d.Y + b2d.Y) * 0.5);
            var requested = labelPoint - mid;
            var signedOffset = requested.DotProduct(normal);
            var maxOffset = Math.Max(_config.TextHeight * 6.0, 25.0);
            if (signedOffset > maxOffset) signedOffset = maxOffset;
            if (signedOffset < -maxOffset) signedOffset = -maxOffset;
            if (Math.Abs(signedOffset) < (_config.TextHeight * 0.75))
            {
                signedOffset = (_config.TextHeight * 1.5) * (signedOffset < 0 ? -1.0 : 1.0);
            }
            // Keep aligned-dimension text anchor inside the disposition so text doesn't drift
            // outside narrow/angled corridors.
            var insideMargin = Math.Max(_config.TextHeight * 0.9, 1.0);
            signedOffset = ClampSignedOffsetInsideDisposition(widthSource, mid, normal, signedOffset, insideMargin);
            var dimLinePoint = new Point3d(
                mid.X + normal.X * signedOffset,
                mid.Y + normal.Y * signedOffset,
                0.0);
            var dimText = ConvertLabelTextForDimension(labelText);
            var dimension = new AlignedDimension(p1, p2, dimLinePoint, dimText, ObjectId.Null)
            {
                Layer = layerName,
                ColorIndex = colorIndex
            };

            ApplyDimensionStyle(tr, dimension, out var dimStyleId);
            if (!dimStyleId.IsNull)
            {
                dimension.DimensionStyle = dimStyleId;
            }

            if (_config.TextHeight > 0)
            {
                dimension.Dimtxt = _config.TextHeight;
            }

            // Explicitly force override text; some styles can otherwise revert to measured text.
            dimension.DimensionText = dimText;

            modelSpace.AppendEntity(dimension);
            tr.AddNewlyCreatedDBObject(dimension, true);
            dimension.DimensionText = dimText;
            return dimension;
        }

        private static double ClampSignedOffsetInsideDisposition(
            Polyline disposition,
            Point2d origin,
            Vector2d axisUnit,
            double requestedOffset,
            double margin)
        {
            if (disposition == null || axisUnit.Length <= 1e-9)
            {
                return requestedOffset;
            }

            if (!TryGetSignedRangeAcrossDisposition(disposition, origin, axisUnit, out var minS, out var maxS))
            {
                return requestedOffset;
            }

            var minAllowed = minS + Math.Max(0.0, margin);
            var maxAllowed = maxS - Math.Max(0.0, margin);
            if (minAllowed > maxAllowed)
            {
                // Extremely tight corridor: place as centered as possible.
                return (minS + maxS) * 0.5;
            }

            if (requestedOffset < minAllowed) return minAllowed;
            if (requestedOffset > maxAllowed) return maxAllowed;
            return requestedOffset;
        }

        private static bool TryGetSignedRangeAcrossDisposition(
            Polyline disposition,
            Point2d origin,
            Vector2d axisUnit,
            out double minS,
            out double maxS)
        {
            minS = 0.0;
            maxS = 0.0;
            if (disposition == null || disposition.NumberOfVertices < 3 || axisUnit.Length <= 1e-9)
            {
                return false;
            }

            try
            {
                var unit = axisUnit.GetNormal();
                var ext = disposition.GeometricExtents;
                var halfLen = Math.Max(50.0, ext.MaxPoint.DistanceTo(ext.MinPoint) * 2.0);

                using (var probe = new Line(
                    new Point3d(origin.X - unit.X * halfLen, origin.Y - unit.Y * halfLen, disposition.Elevation),
                    new Point3d(origin.X + unit.X * halfLen, origin.Y + unit.Y * halfLen, disposition.Elevation)))
                {
                    var pts = new Point3dCollection();
                    disposition.IntersectWith(probe, Intersect.OnBothOperands, pts, IntPtr.Zero, IntPtr.Zero);
                    if (pts.Count < 2)
                    {
                        return false;
                    }

                    var signed = new List<double>(pts.Count);
                    for (var i = 0; i < pts.Count; i++)
                    {
                        var p = pts[i];
                        var s = (p.X - origin.X) * unit.X + (p.Y - origin.Y) * unit.Y;
                        if (!signed.Any(x => Math.Abs(x - s) < 1e-6))
                        {
                            signed.Add(s);
                        }
                    }

                    if (signed.Count < 2)
                    {
                        return false;
                    }

                    signed.Sort();
                    const double eps = 1e-6;
                    var lower = signed.First();
                    var upper = signed.Last();

                    // Prefer interval bracketing the origin.
                    var neg = double.NegativeInfinity;
                    var pos = double.PositiveInfinity;
                    foreach (var s in signed)
                    {
                        if (s < -eps && s > neg) neg = s;
                        if (s > eps && s < pos) pos = s;
                    }

                    if (neg > double.NegativeInfinity) lower = neg;
                    if (pos < double.PositiveInfinity) upper = pos;
                    if (upper - lower <= eps)
                    {
                        return false;
                    }

                    minS = lower;
                    maxS = upper;
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetPerpendicularSpanAcrossDisposition(
            Polyline disposition,
            Point2d insidePoint,
            out Point2d a,
            out Point2d b)
        {
            a = default;
            b = default;
            if (disposition == null || disposition.NumberOfVertices < 3)
                return false;

            try
            {
                var closest = disposition.GetClosestPointTo(new Point3d(insidePoint.X, insidePoint.Y, disposition.Elevation), false);
                var param = disposition.GetParameterAtPoint(closest);
                var deriv = disposition.GetFirstDerivative(param);
                var tangent = new Vector2d(deriv.X, deriv.Y);
                if (tangent.Length <= 1e-6)
                    return false;

                var normal = new Vector2d(-tangent.Y, tangent.X).GetNormal();
                var ext = disposition.GeometricExtents;
                var halfLen = Math.Max(50.0, ext.MaxPoint.DistanceTo(ext.MinPoint) * 2.0);

                using (var line = new Line(
                    new Point3d(insidePoint.X - normal.X * halfLen, insidePoint.Y - normal.Y * halfLen, disposition.Elevation),
                    new Point3d(insidePoint.X + normal.X * halfLen, insidePoint.Y + normal.Y * halfLen, disposition.Elevation)))
                {
                    var pts = new Point3dCollection();
                    disposition.IntersectWith(line, Intersect.OnBothOperands, pts, IntPtr.Zero, IntPtr.Zero);
                    if (pts.Count < 2)
                        return false;

                    var proj = new List<double>(pts.Count);
                    for (int i = 0; i < pts.Count; i++)
                    {
                        var p = pts[i];
                        var s = (p.X - insidePoint.X) * normal.X + (p.Y - insidePoint.Y) * normal.Y;
                        if (!proj.Any(x => Math.Abs(x - s) < 1e-6))
                            proj.Add(s);
                    }

                    if (proj.Count < 2)
                        return false;

                    proj.Sort();
                    const double eps = 1e-6;
                    bool haveNeg = false, havePos = false;
                    double sNeg = double.NegativeInfinity, sPos = double.PositiveInfinity;

                    foreach (var s in proj)
                    {
                        if (s < -eps && s > sNeg) { sNeg = s; haveNeg = true; }
                        if (s > eps && s < sPos) { sPos = s; havePos = true; }
                    }

                    if (!haveNeg || !havePos)
                        return false;

                    a = new Point2d(insidePoint.X + normal.X * sNeg, insidePoint.Y + normal.Y * sNeg);
                    b = new Point2d(insidePoint.X + normal.X * sPos, insidePoint.Y + normal.Y * sPos);
                    return true;
                }
            }
            catch
            {
                return false;
            }
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

        private void ApplyDimensionStyle(Transaction tr, Dimension dimension, out ObjectId dimStyleId)
        {
            dimStyleId = ObjectId.Null;
            if (tr == null || dimension == null) return;

            var dimStyleTable = (DimStyleTable)tr.GetObject(_database.DimStyleTableId, OpenMode.ForRead);
            if (!dimStyleTable.Has(_config.DimensionStyleName))
                return;

            dimStyleId = dimStyleTable[_config.DimensionStyleName];
        }

        internal static string AppendExpiredMarkerIfMissing(string labelText)
        {
            if (string.IsNullOrWhiteSpace(labelText))
            {
                return labelText ?? string.Empty;
            }

            var flattened = labelText
                .Replace("\\P", "\n")
                .Replace("\\X", "\n");
            if (Regex.IsMatch(flattened, @"\bEXPIRED\b", RegexOptions.IgnoreCase))
            {
                return labelText;
            }

            var delimiter = labelText.IndexOf("\\X", StringComparison.OrdinalIgnoreCase) >= 0 ? "\\X" : "\\P";
            return labelText + delimiter + "(Expired)";
        }

        private static string ConvertLabelTextForDimension(string labelText)
        {
            if (string.IsNullOrWhiteSpace(labelText))
            {
                return string.Empty;
            }

            var cleaned = labelText.Replace("\\P", "\n");
            cleaned = cleaned.Replace("{", string.Empty).Replace("}", string.Empty);
            return cleaned.Trim();
        }

        private static double TryExtractExpectedWidthFromLabelText(string labelText)
        {
            if (string.IsNullOrWhiteSpace(labelText))
                return 0.0;

            var firstLine = labelText.Split(new[] { "\\P" }, StringSplitOptions.None).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(firstLine))
                return 0.0;

            var match = Regex.Match(firstLine, @"(\d+(?:\.\d+)?)");
            if (!match.Success)
                return 0.0;

            if (double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
                return parsed;

            return 0.0;
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
            // AutoCAD 2025's TextAttachmentType enum does not define MiddleLeft/MiddleRight/MiddleCenter.
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

            // Conservative footprint estimate so collision checks better match actual MLeader text.
            double charWidth = textHeight * 0.92;
            double width = Math.Max(textHeight, maxChars * charWidth);
            double height = lineCount * textHeight * 1.55;

            double pad = textHeight * 0.9;
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
            double measuredWidth,
            double maxLeaderLength)
        {
            var spiral = GeometryUtils.GetSpiralOffsets(target, step, maxPoints).ToList();
            double minDistance = step * 0.5;
            double minHalfWidth = measuredWidth * 0.25;
            double minQuarterClearance = step * 0.5;

            if (!allowOutsideDisposition)
            {
                var candidates = new List<(Point2d pt, double distToTarget, double clearance)>();
                foreach (var p in spiral)
                {
                    if (GeometryUtils.IsPointInsidePolyline(quarter, p) && GeometryUtils.IsPointInsidePolyline(disposition, p))
                    {
                        var p3d = new Point3d(p.X, p.Y, 0);
                        var closest = disposition.GetClosestPointTo(p3d, false);
                        var quarterClearance = DistanceToPolyline(quarter, p3d);
                        var dispClearance = closest.DistanceTo(p3d);
                        if (closest.DistanceTo(p3d) >= Math.Max(minDistance, minHalfWidth) &&
                            quarterClearance >= minQuarterClearance &&
                            IsWithinLeaderLength(target, p, maxLeaderLength))
                        {
                            candidates.Add((p, p.GetDistanceTo(target), Math.Min(quarterClearance, dispClearance)));
                        }
                    }
                }
                foreach (var item in candidates.OrderBy(c => c.distToTarget).ThenByDescending(c => c.clearance))
                    yield return item.pt;
                yield break;
            }

            // For leader labels, allow both inside and outside white space.
            // We prioritize proximity first, then clearance; overlap checks later reject bad fits.
            var allCandidates = new List<(Point2d pt, double distToTarget, double clearance)>();
            foreach (var p in spiral)
            {
                if (!GeometryUtils.IsPointInsidePolyline(quarter, p))
                    continue;

                var p3d = new Point3d(p.X, p.Y, 0);
                var quarterClearance = DistanceToPolyline(quarter, p3d);
                var closest = disposition.GetClosestPointTo(p3d, false);
                var dispClearance = closest.DistanceTo(p3d);
                var insideDisposition = GeometryUtils.IsPointInsidePolyline(disposition, p);

                if (GeometryUtils.IsPointInsidePolyline(disposition, p))
                {
                    if (closest.DistanceTo(p3d) >= Math.Max(minDistance, minHalfWidth) &&
                        quarterClearance >= minQuarterClearance &&
                        IsWithinLeaderLength(target, p, maxLeaderLength))
                    {
                        allCandidates.Add((p, p.GetDistanceTo(target), Math.Min(quarterClearance, dispClearance)));
                    }
                }
                else
                {
                    if (closest.DistanceTo(p3d) >= Math.Max(minDistance, minHalfWidth) &&
                        quarterClearance >= minQuarterClearance &&
                        IsWithinLeaderLength(target, p, maxLeaderLength))
                    {
                        allCandidates.Add((p, p.GetDistanceTo(target), Math.Min(quarterClearance, dispClearance)));
                    }
                }
            }

            foreach (var item in allCandidates
                .OrderBy(c => c.distToTarget)
                .ThenByDescending(c => c.clearance))
                yield return item.pt;
        }

        private static double DistanceToPolyline(Polyline polyline, Point3d p3d)
        {
            try
            {
                var closest = polyline.GetClosestPointTo(p3d, false);
                return closest.DistanceTo(p3d);
            }
            catch
            {
                return 0;
            }
        }

        private static bool IsWithinLeaderLength(Point2d target, Point2d candidate, double maxLeaderLength)
        {
            if (double.IsInfinity(maxLeaderLength) || maxLeaderLength <= 0)
                return true;
            return target.GetDistanceTo(candidate) <= maxLeaderLength;
        }

        private static bool TryFindMixedWellsitePadTarget(
            Polyline quarter,
            Polyline disposition,
            Polyline padSource,
            Point2d safeFallback,
            double textHeight,
            out Point2d target)
        {
            target = safeFallback;
            if (quarter == null || disposition == null || padSource == null)
            {
                return false;
            }

            var hasBest = false;
            var bestPoint = target;
            var bestClearance = double.NegativeInfinity;

            void Consider(Point2d candidate)
            {
                if (!GeometryUtils.IsPointInsidePolyline(quarter, candidate) ||
                    !GeometryUtils.IsPointInsidePolyline(disposition, candidate))
                {
                    return;
                }

                var p3d = new Point3d(candidate.X, candidate.Y, 0.0);
                var quarterClearance = DistanceToPolyline(quarter, p3d);
                var dispClearance = DistanceToPolyline(disposition, p3d);
                var clearance = Math.Min(quarterClearance, dispClearance);
                if (!IsFinite(clearance) || clearance <= 0.0)
                {
                    return;
                }

                if (clearance > bestClearance)
                {
                    bestClearance = clearance;
                    bestPoint = candidate;
                    hasBest = true;
                }
            }

            try
            {
                var measurement = GeometryUtils.MeasureCorridorWidth(
                    padSource,
                    25,
                    Math.Max(0.20, textHeight * 0.05),
                    0.10);
                Consider(measurement.MaxCenter);
                Consider(measurement.MedianCenter);
            }
            catch
            {
                // Fall through to geometric candidates.
            }

            Consider(safeFallback);
            Consider(GeometryUtils.GetSafeInteriorPoint(padSource));
            try
            {
                Consider(GetExtentsOverlapCenter(quarter.GeometricExtents, disposition.GeometricExtents));
            }
            catch
            {
                // ignore extents failures
            }

            try
            {
                if (TryGetOverlapExtents(
                        quarter.GeometricExtents,
                        disposition.GeometricExtents,
                        out var minX,
                        out var minY,
                        out var maxX,
                        out var maxY))
                {
                    var xSpan = maxX - minX;
                    var ySpan = maxY - minY;
                    const int gridSteps = 22;
                    for (var ix = 0; ix <= gridSteps; ix++)
                    {
                        var x = minX + (xSpan * ix / gridSteps);
                        for (var iy = 0; iy <= gridSteps; iy++)
                        {
                            var y = minY + (ySpan * iy / gridSteps);
                            Consider(new Point2d(x, y));
                        }
                    }
                }
            }
            catch
            {
                // ignore grid fallback failures
            }

            if (!hasBest)
            {
                return false;
            }

            target = bestPoint;
            return true;
        }

        private static bool TryGetOverlapExtents(
            Extents3d a,
            Extents3d b,
            out double minX,
            out double minY,
            out double maxX,
            out double maxY)
        {
            minX = Math.Max(a.MinPoint.X, b.MinPoint.X);
            minY = Math.Max(a.MinPoint.Y, b.MinPoint.Y);
            maxX = Math.Min(a.MaxPoint.X, b.MaxPoint.X);
            maxY = Math.Min(a.MaxPoint.Y, b.MaxPoint.Y);

            return IsFinite(minX) &&
                   IsFinite(minY) &&
                   IsFinite(maxX) &&
                   IsFinite(maxY) &&
                   maxX > minX &&
                   maxY > minY;
        }
        private bool TryGetQuarterIntersectionTarget(
            Polyline quarter,
            Polyline disposition,
            out Polyline? intersectionPiece,
            out Point2d target)
        {
            intersectionPiece = null;
            target = default;

            // First try true polygon intersection: disposition intersection quarter
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

        private static Extents3d InflateExtents(Extents3d extents, double pad)
        {
            if (pad <= 0)
                return extents;

            return new Extents3d(
                new Point3d(extents.MinPoint.X - pad, extents.MinPoint.Y - pad, extents.MinPoint.Z),
                new Point3d(extents.MaxPoint.X + pad, extents.MaxPoint.Y + pad, extents.MaxPoint.Z));
        }

        private static int CountIntersectingDispositionLinework(
            Extents3d labelExtents,
            List<DispositionInfo> dispositions,
            DispositionInfo currentDisposition)
        {
            if (dispositions == null || dispositions.Count == 0)
                return 0;

            int count = 0;
            foreach (var disposition in dispositions)
            {
                if (disposition?.Polyline == null)
                    continue;

                if (currentDisposition != null)
                {
                    if (ReferenceEquals(disposition, currentDisposition))
                        continue;

                    if (!disposition.ObjectId.IsNull &&
                        !currentDisposition.ObjectId.IsNull &&
                        disposition.ObjectId == currentDisposition.ObjectId)
                    {
                        continue;
                    }
                }

                if (!GeometryUtils.ExtentsIntersect(labelExtents, disposition.Bounds))
                    continue;

                if (RectangleIntersectsPolyline(labelExtents, disposition.Polyline))
                    count++;
            }

            return count;
        }

        private static int CountNearbyPlacedLabels(List<Extents3d> placedLabelExtents, Extents3d candidate, double radius)
        {
            if (placedLabelExtents == null || placedLabelExtents.Count == 0 || radius <= 0)
                return 0;

            var expanded = InflateExtents(candidate, radius);
            int count = 0;
            foreach (var ext in placedLabelExtents)
            {
                if (GeometryUtils.ExtentsIntersect(expanded, ext))
                    count++;
            }

            return count;
        }

        private static bool IsPointInRectBounds(Point2d point, Point2d min, Point2d max)
        {
            return point.X >= min.X &&
                   point.X <= max.X &&
                   point.Y >= min.Y &&
                   point.Y <= max.Y;
        }

        private static bool RectangleIntersectsPolyline(Extents3d rect, Polyline polyline)
        {
            if (polyline == null || polyline.NumberOfVertices < 2)
                return false;

            var min = rect.MinPoint;
            var max = rect.MaxPoint;
            var rmin = new Point2d(min.X, min.Y);
            var rmax = new Point2d(max.X, max.Y);
            var rbl = new Point2d(rmin.X, rmin.Y);
            var rbr = new Point2d(rmax.X, rmin.Y);
            var rtr = new Point2d(rmax.X, rmax.Y);
            var rtl = new Point2d(rmin.X, rmax.Y);

            int last = polyline.Closed ? polyline.NumberOfVertices : polyline.NumberOfVertices - 1;
            for (int i = 0; i < last; i++)
            {
                int j = (i + 1) % polyline.NumberOfVertices;
                var a = polyline.GetPoint2dAt(i);
                var b = polyline.GetPoint2dAt(j);

                if (IsPointInRectBounds(a, rmin, rmax) || IsPointInRectBounds(b, rmin, rmax))
                    return true;

                if (SegmentsIntersect(a, b, rbl, rbr) ||
                    SegmentsIntersect(a, b, rbr, rtr) ||
                    SegmentsIntersect(a, b, rtr, rtl) ||
                    SegmentsIntersect(a, b, rtl, rbl))
                    return true;
            }

            // Rectangle center inside closed disposition still means the label sits on/in that polygon.
            if (polyline.Closed)
            {
                var center = new Point2d((rmin.X + rmax.X) * 0.5, (rmin.Y + rmax.Y) * 0.5);
                if (GeometryUtils.IsPointInsidePolyline(polyline, center))
                    return true;
            }

            return false;
        }

        private static bool SegmentsIntersect(Point2d p1, Point2d p2, Point2d q1, Point2d q2)
        {
            var d1 = CrossForSegmentIntersection(p1, p2, q1);
            var d2 = CrossForSegmentIntersection(p1, p2, q2);
            var d3 = CrossForSegmentIntersection(q1, q2, p1);
            var d4 = CrossForSegmentIntersection(q1, q2, p2);

            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
                ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0)))
                return true;

            if (Math.Abs(d1) <= 1e-9 && IsPointOnSegmentForIntersection(p1, p2, q1)) return true;
            if (Math.Abs(d2) <= 1e-9 && IsPointOnSegmentForIntersection(p1, p2, q2)) return true;
            if (Math.Abs(d3) <= 1e-9 && IsPointOnSegmentForIntersection(q1, q2, p1)) return true;
            if (Math.Abs(d4) <= 1e-9 && IsPointOnSegmentForIntersection(q1, q2, p2)) return true;

            return false;
        }

        private static Point2d ChooseLeaderTargetAvoidingOtherDispositions(
            Point2d preferred,
            Point2d intersectionTarget,
            Polyline quarter,
            Polyline targetCorridor,
            DispositionInfo current,
            List<DispositionInfo> allDispositions)
        {
            var candidates = new List<Point2d>();
            AddUniqueCandidatePoint(candidates, preferred);
            AddUniqueCandidatePoint(candidates, intersectionTarget);

            var safe = GeometryUtils.GetSafeInteriorPoint(targetCorridor);
            AddUniqueCandidatePoint(candidates, safe);

            if (GeometryUtils.TryGetCrossSectionMidpoint(targetCorridor, intersectionTarget, out var mid, out _))
                AddUniqueCandidatePoint(candidates, mid);

            if (GeometryUtils.TryFindPointInsideBoth(quarter, targetCorridor, out var overlapPoint))
                AddUniqueCandidatePoint(candidates, overlapPoint);

            foreach (var p in candidates)
            {
                if (!GeometryUtils.IsPointInsidePolyline(quarter, p) || !GeometryUtils.IsPointInsidePolyline(targetCorridor, p))
                    continue;
                if (!IsPointInsideAnyOtherDisposition(p, current, allDispositions))
                    return p;
            }

            foreach (var p in candidates)
            {
                if (GeometryUtils.IsPointInsidePolyline(quarter, p) && GeometryUtils.IsPointInsidePolyline(targetCorridor, p))
                    return p;
            }

            return preferred;
        }

        private static double CrossForSegmentIntersection(Point2d a, Point2d b, Point2d c)
        {
            return ((b.X - a.X) * (c.Y - a.Y)) - ((b.Y - a.Y) * (c.X - a.X));
        }

        private static bool IsPointOnSegmentForIntersection(Point2d a, Point2d b, Point2d p)
        {
            return p.X >= Math.Min(a.X, b.X) - 1e-9 &&
                   p.X <= Math.Max(a.X, b.X) + 1e-9 &&
                   p.Y >= Math.Min(a.Y, b.Y) - 1e-9 &&
                   p.Y <= Math.Max(a.Y, b.Y) + 1e-9;
        }

        private static void AddUniqueCandidatePoint(ICollection<Point2d> candidates, Point2d point)
        {
            foreach (var candidate in candidates)
            {
                if (Math.Abs(candidate.X - point.X) < 1e-6 &&
                    Math.Abs(candidate.Y - point.Y) < 1e-6)
                {
                    return;
                }
            }

            candidates.Add(point);
        }

        private static bool IsPointInsideAnyOtherDisposition(
            Point2d point,
            DispositionInfo current,
            List<DispositionInfo> allDispositions)
        {
            if (allDispositions == null || allDispositions.Count == 0)
                return false;

            foreach (var other in allDispositions)
            {
                if (other == null || ReferenceEquals(other, current))
                    continue;
                if (!other.ObjectId.IsNull && other.ObjectId == current.ObjectId)
                    continue;

                var b = other.Bounds;
                if (point.X < b.MinPoint.X || point.X > b.MaxPoint.X || point.Y < b.MinPoint.Y || point.Y > b.MaxPoint.Y)
                    continue;

                if (GeometryUtils.IsPointInsidePolyline(other.Polyline, point))
                    return true;
            }

            return false;
        }

        private static bool TryParseOdDimensionWidth(string raw, out double widthMeters)
        {
            widthMeters = 0.0;
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            // Examples: "15 M X 2.4", "15.24m", "20 x ..."
            var match = Regex.Match(raw, @"(?<!\d)(\d+(?:\.\d+)?)\s*(?:M|m)?");
            if (!match.Success)
                return false;

            if (!double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                return false;

            if (!IsFinite(parsed) || parsed <= 0)
                return false;

            widthMeters = parsed;
            return true;
        }

        private static string BuildQuarterKey(SectionKey? key, QuarterSelection quarter)
        {
            if (!key.HasValue || quarter == QuarterSelection.None)
            {
                return string.Empty;
            }

            var quarterToken = quarter switch
            {
                QuarterSelection.NorthWest => "NW",
                QuarterSelection.NorthEast => "NE",
                QuarterSelection.SouthWest => "SW",
                QuarterSelection.SouthEast => "SE",
                _ => string.Empty
            };

            if (string.IsNullOrWhiteSpace(quarterToken))
            {
                return string.Empty;
            }

            var sectionKey = key.Value;
            var meridian = NormalizeMeridianToken(sectionKey.Meridian);
            var range = NormalizeNumberToken(sectionKey.Range);
            var township = NormalizeNumberToken(sectionKey.Township);
            var section = NormalizeNumberToken(sectionKey.Section);
            return $"{meridian}|{range}|{township}|{section}|{quarterToken}";
        }

        private static string NormalizeMeridianToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return string.Empty;
            }

            var digits = new string(token.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var num))
            {
                return num.ToString();
            }

            return token.Trim().ToUpperInvariant();
        }

        private static string NormalizeNumberToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return string.Empty;
            }

            if (int.TryParse(token.Trim(), out var num))
            {
                return num.ToString();
            }

            return token.Trim().TrimStart('0');
        }

        private static string NormalizeDispNum(string dispNum)
        {
            if (string.IsNullOrWhiteSpace(dispNum))
            {
                return string.Empty;
            }

            var compact = Regex.Replace(dispNum.ToUpperInvariant(), "\\s+", string.Empty);
            compact = Regex.Replace(compact, "[^A-Z0-9]", string.Empty);
            if (string.IsNullOrWhiteSpace(compact))
            {
                return string.Empty;
            }

            var prefixMatch = Regex.Match(compact, "^[A-Z]{3}");
            if (!prefixMatch.Success)
            {
                return compact;
            }

            var prefix = prefixMatch.Value;
            var suffix = compact.Substring(prefix.Length);
            if (string.IsNullOrWhiteSpace(suffix))
            {
                return prefix;
            }

            var digits = new string(suffix.Where(char.IsDigit).ToArray());
            if (digits.Length > 0)
            {
                var trimmedDigits = digits.TrimStart('0');
                if (trimmedDigits.Length == 0)
                {
                    trimmedDigits = "0";
                }

                return prefix + trimmedDigits;
            }

            return prefix + suffix;
        }
        private static string NormalizeDispositionReuseVariant(string? variantKey)
        {
            return string.IsNullOrWhiteSpace(variantKey)
                ? string.Empty
                : variantKey.Trim().ToUpperInvariant();
        }

        private static string BuildDispositionReuseKey(string normalizedDispNum, string normalizedVariantKey)
        {
            if (string.IsNullOrWhiteSpace(normalizedDispNum))
            {
                return string.Empty;
            }

            if (string.IsNullOrWhiteSpace(normalizedVariantKey))
            {
                return normalizedDispNum;
            }

            return $"{normalizedDispNum}|{normalizedVariantKey}";
        }

        private static string BuildDispositionIdentity(DispositionInfo disposition)
        {
            if (disposition == null)
            {
                return string.Empty;
            }

            var objectToken = disposition.ObjectId.IsNull
                ? (disposition.DispNumFormatted ?? string.Empty)
                : disposition.ObjectId.Handle.ToString();
            var variantToken = NormalizeDispositionReuseVariant(disposition.ReuseVariantKey);

            return string.IsNullOrWhiteSpace(variantToken)
                ? objectToken
                : $"{objectToken}|{variantToken}";
        }

    }

    public sealed class QuarterInfo
    {
        public QuarterInfo(Polyline polyline, SectionKey? sectionKey = null, QuarterSelection quarter = QuarterSelection.None)
        {
            Polyline = polyline;
            Bounds = TryGetBounds(polyline, out var bounds)
                ? bounds
                : new Extents3d(Point3d.Origin, Point3d.Origin);
            SectionKey = sectionKey;
            Quarter = quarter;
        }

        public Polyline Polyline { get; }
        public Extents3d Bounds { get; }
        public SectionKey? SectionKey { get; }
        public QuarterSelection Quarter { get; }

        private static bool TryGetBounds(Polyline polyline, out Extents3d bounds)
        {
            bounds = new Extents3d(Point3d.Origin, Point3d.Origin);
            if (polyline == null)
            {
                return false;
            }

            try
            {
                bounds = polyline.GeometricExtents;
                return true;
            }
            catch
            {
                // fallback below
            }

            try
            {
                var count = polyline.NumberOfVertices;
                if (count <= 0)
                {
                    return false;
                }

                var minX = double.PositiveInfinity;
                var minY = double.PositiveInfinity;
                var maxX = double.NegativeInfinity;
                var maxY = double.NegativeInfinity;
                var found = false;
                for (var i = 0; i < count; i++)
                {
                    Point2d vertex;
                    try
                    {
                        vertex = polyline.GetPoint2dAt(i);
                    }
                    catch
                    {
                        continue;
                    }

                    if (vertex.X < minX) minX = vertex.X;
                    if (vertex.Y < minY) minY = vertex.Y;
                    if (vertex.X > maxX) maxX = vertex.X;
                    if (vertex.Y > maxY) maxY = vertex.Y;
                    found = true;
                }

                if (!found ||
                    double.IsNaN(minX) || double.IsInfinity(minX) ||
                    double.IsNaN(minY) || double.IsInfinity(minY) ||
                    double.IsNaN(maxX) || double.IsInfinity(maxX) ||
                    double.IsNaN(maxY) || double.IsInfinity(maxY))
                {
                    return false;
                }

                bounds = new Extents3d(
                    new Point3d(minX, minY, 0.0),
                    new Point3d(maxX, maxY, 0.0));
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public sealed class DispositionInfo
    {
        public const string ReuseVariantMixedAccessRoad = "MIXED_ACCESS_ROAD";
        public const string ReuseVariantMixedWellsitePad = "MIXED_WELLSITE_PAD";

        public DispositionInfo(ObjectId objectId, Polyline polyline, string labelText, string layerName, string textLayerName, Point2d safePoint)
        {
            ObjectId = objectId;
            Polyline = polyline;
            Bounds = TryGetBounds(polyline, out var bounds)
                ? bounds
                : new Extents3d(Point3d.Origin, Point3d.Origin);
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
        public string OdDimension { get; set; } = string.Empty;
        public string OdVerDateRaw { get; set; } = string.Empty;
        public string OdEffDateRaw { get; set; } = string.Empty;
        public string ReuseVariantKey { get; set; } = string.Empty;
        public bool ShouldAddExpiredMarker { get; set; }

        // For width-required purposes, allow label to be placed in the quarter (not necessarily in the disposition)
        public bool AllowLabelOutsideDisposition { get; set; }

        // Draw leader entities (circle + line) from target to label
        public bool AddLeader { get; set; }

        private static bool TryGetBounds(Polyline polyline, out Extents3d bounds)
        {
            bounds = new Extents3d(Point3d.Origin, Point3d.Origin);
            if (polyline == null)
            {
                return false;
            }

            try
            {
                bounds = polyline.GeometricExtents;
                return true;
            }
            catch
            {
                // fallback below
            }

            try
            {
                var count = polyline.NumberOfVertices;
                if (count <= 0)
                {
                    return false;
                }

                var minX = double.PositiveInfinity;
                var minY = double.PositiveInfinity;
                var maxX = double.NegativeInfinity;
                var maxY = double.NegativeInfinity;
                var found = false;
                for (var i = 0; i < count; i++)
                {
                    Point2d vertex;
                    try
                    {
                        vertex = polyline.GetPoint2dAt(i);
                    }
                    catch
                    {
                        continue;
                    }

                    if (vertex.X < minX) minX = vertex.X;
                    if (vertex.Y < minY) minY = vertex.Y;
                    if (vertex.X > maxX) maxX = vertex.X;
                    if (vertex.Y > maxY) maxY = vertex.Y;
                    found = true;
                }

                if (!found ||
                    double.IsNaN(minX) || double.IsInfinity(minX) ||
                    double.IsNaN(minY) || double.IsInfinity(minY) ||
                    double.IsNaN(maxX) || double.IsInfinity(maxX) ||
                    double.IsNaN(maxY) || double.IsInfinity(maxY))
                {
                    return false;
                }

                bounds = new Extents3d(
                    new Point3d(minX, minY, 0.0),
                    new Point3d(maxX, maxY, 0.0));
                return true;
            }
            catch
            {
                return false;
            }
        }
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










