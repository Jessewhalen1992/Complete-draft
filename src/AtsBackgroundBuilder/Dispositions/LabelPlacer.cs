/////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
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
        private readonly struct Aabb2d
        {
            public Aabb2d(double minX, double minY, double maxX, double maxY)
            {
                MinX = Math.Min(minX, maxX);
                MinY = Math.Min(minY, maxY);
                MaxX = Math.Max(minX, maxX);
                MaxY = Math.Max(minY, maxY);
            }

            public double MinX { get; }
            public double MinY { get; }
            public double MaxX { get; }
            public double MaxY { get; }

            public Extents3d ToExtents3d()
            {
                return new Extents3d(
                    new Point3d(MinX, MinY, 0.0),
                    new Point3d(MaxX, MaxY, 0.0));
            }

            public Aabb2d Inflate(double pad)
            {
                if (pad <= 0.0)
                {
                    return this;
                }

                return new Aabb2d(MinX - pad, MinY - pad, MaxX + pad, MaxY + pad);
            }
        }

        private readonly struct DimensionTextCandidate
        {
            public DimensionTextCandidate(Point2d textPoint, double dimLineOffset, double score)
            {
                TextPoint = textPoint;
                DimLineOffset = dimLineOffset;
                Score = score;
            }

            public Point2d TextPoint { get; }
            public double DimLineOffset { get; }
            public double Score { get; }
        }
        private sealed class LabelCollisionIndex
        {
            private readonly List<Aabb2d> _boxes = new List<Aabb2d>();

            public void Add(Aabb2d box)
            {
                _boxes.Add(box);
            }

            public bool Intersects(Aabb2d box)
            {
                foreach (var other in _boxes)
                {
                    if (!(box.MaxX < other.MinX ||
                          other.MaxX < box.MinX ||
                          box.MaxY < other.MinY ||
                          other.MaxY < box.MinY))
                    {
                        return true;
                    }
                }

                return false;
            }

            public double OverlapArea(Aabb2d box)
            {
                double area = 0.0;
                foreach (var other in _boxes)
                {
                    var dx = Math.Max(0.0, Math.Min(box.MaxX, other.MaxX) - Math.Max(box.MinX, other.MinX));
                    var dy = Math.Max(0.0, Math.Min(box.MaxY, other.MaxY) - Math.Max(box.MinY, other.MinY));
                    area += dx * dy;
                }

                return area;
            }

            public int CountNearby(Aabb2d box, double radius)
            {
                var expanded = box.Inflate(radius);
                var count = 0;
                foreach (var other in _boxes)
                {
                    if (!(expanded.MaxX < other.MinX ||
                          other.MaxX < expanded.MinX ||
                          expanded.MaxY < other.MinY ||
                          other.MaxY < expanded.MinY))
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        private sealed class PlacementRequest : IDisposable
        {
            public PlacementRequest(
                QuarterInfo quarter,
                string quarterKey,
                DispositionInfo disposition,
                Polyline? intersectionPiece,
                Point2d measurementTarget,
                Point2d searchTarget,
                Point2d leaderTarget,
                string labelText,
                int textColorIndex,
                double measuredWidth,
                int maxPoints,
                double maxLeaderLength,
                bool allowOutsideDisposition,
                bool isLeaderOrPlainText,
                int candidateCount,
                double quarterFootprintArea,
                string dispositionIdentity,
                string normalizedDispNum,
                string normalizedReuseVariant,
                string dispositionReuseKey)
            {
                Quarter = quarter;
                QuarterKey = quarterKey;
                Disposition = disposition;
                IntersectionPiece = intersectionPiece;
                MeasurementTarget = measurementTarget;
                SearchTarget = searchTarget;
                LeaderTarget = leaderTarget;
                LabelText = labelText;
                TextColorIndex = textColorIndex;
                MeasuredWidth = measuredWidth;
                MaxPoints = maxPoints;
                MaxLeaderLength = maxLeaderLength;
                AllowOutsideDisposition = allowOutsideDisposition;
                IsLeaderOrPlainText = isLeaderOrPlainText;
                CandidateCount = candidateCount;
                QuarterFootprintArea = quarterFootprintArea;
                DispositionIdentity = dispositionIdentity;
                NormalizedDispNum = normalizedDispNum;
                NormalizedReuseVariant = normalizedReuseVariant;
                DispositionReuseKey = dispositionReuseKey;
            }

            public QuarterInfo Quarter { get; }
            public string QuarterKey { get; }
            public DispositionInfo Disposition { get; }
            public Polyline? IntersectionPiece { get; }
            public Point2d MeasurementTarget { get; }
            public Point2d SearchTarget { get; }
            public Point2d LeaderTarget { get; }
            public string LabelText { get; }
            public int TextColorIndex { get; }
            public double MeasuredWidth { get; }
            public int MaxPoints { get; }
            public double MaxLeaderLength { get; }
            public bool AllowOutsideDisposition { get; }
            public bool IsLeaderOrPlainText { get; }
            public int CandidateCount { get; }
            public double QuarterFootprintArea { get; }
            public string DispositionIdentity { get; }
            public string NormalizedDispNum { get; }
            public string NormalizedReuseVariant { get; }
            public string DispositionReuseKey { get; }

            public void Dispose()
            {
                if (IntersectionPiece == null)
                {
                    return;
                }

                try
                {
                    IntersectionPiece.Dispose();
                }
                catch
                {
                    // ignore transient geometry disposal failures
                }
            }
        }

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
            var requests = new List<PlacementRequest>();

            try
            {
                using (var transaction = _database.TransactionManager.StartTransaction())
                {
                    var blockTable = (BlockTable)transaction.GetObject(_database.BlockTableId, OpenMode.ForRead);
                    var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    var collisionIndex = BuildCollisionIndex(transaction, modelSpace, dispositions);

                    _logger.WriteLine($"Quarter polylines (unique): {quarters.Count}");
                    _logger.WriteLine($"Dispositions: {dispositions.Count}");

                    foreach (var quarter in quarters)
                    {
                        var quarterKey = BuildQuarterKey(quarter.SectionKey, quarter.Quarter);

                        foreach (var disposition in dispositions)
                        {
                            Polyline? intersectionPiece = null;
                            try
                            {
                                var normalizedDispNum = NormalizeDispNum(disposition.DispNumFormatted);
                                var normalizedReuseVariant = NormalizeDispositionReuseVariant(disposition.ReuseVariantKey);
                                var dispositionReuseKey = BuildDispositionReuseKey(normalizedDispNum, normalizedReuseVariant);
                                if (ShouldSkipExistingQuarterLabel(
                                        existingDispNumsByQuarter,
                                        quarterKey,
                                        normalizedDispNum,
                                        normalizedReuseVariant,
                                        dispositionReuseKey,
                                        out _))
                                {
                                    continue;
                                }

                                var dispositionIdentity = BuildDispositionIdentity(disposition);
                                if (!_config.AllowMultiQuarterDispositions && processedDispositionIds.Contains(dispositionIdentity))
                                {
                                    continue;
                                }

                                if (countedDispositionIds.Contains(dispositionIdentity))
                                {
                                    result.MultiQuarterProcessed++;
                                }
                                else
                                {
                                    countedDispositionIds.Add(dispositionIdentity);
                                }

                                if (!GeometryUtils.ExtentsIntersect(quarter.Polyline.GeometricExtents, disposition.Polyline.GeometricExtents))
                                {
                                    continue;
                                }

                                Point2d intersectionTarget;
                                if (!TryGetQuarterIntersectionTarget(quarter.Polyline, disposition.Polyline, out intersectionPiece, out intersectionTarget))
                                {
                                    if (!GeometryUtils.TryFindPointInsideBoth(quarter.Polyline, disposition.Polyline, out intersectionTarget))
                                    {
                                        _logger.WriteLine($"No quarter intersection: disp={disposition.ObjectId} quarterExt={quarter.Polyline.GeometricExtents}");
                                        result.SkippedNoIntersection++;
                                        continue;
                                    }
                                }

                                if (string.IsNullOrWhiteSpace(disposition.TextLayerName))
                                {
                                    result.SkippedNoLayerMapping++;
                                    continue;
                                }

                                EnsureLayerInTransaction(_database, transaction, disposition.TextLayerName);

                                var labelText = disposition.LabelText;
                                var textColorIndex = disposition.TextColorIndex;
                                var measuredWidth = 0.0;
                                var measurementTarget = intersectionTarget;
                                var searchTarget = intersectionTarget;
                                var leaderTarget = intersectionTarget;

                                if (string.Equals(
                                        NormalizeDispositionReuseVariant(disposition.ReuseVariantKey),
                                        DispositionInfo.ReuseVariantMixedWellsitePad,
                                        StringComparison.OrdinalIgnoreCase))
                                {
                                    if (TryFindMixedWellsitePadTarget(
                                            quarter.Polyline,
                                            disposition.Polyline,
                                            intersectionPiece ?? disposition.Polyline,
                                            disposition.SafePoint,
                                            _config.TextHeight,
                                            out var padTarget))
                                    {
                                        measurementTarget = padTarget;
                                        searchTarget = padTarget;
                                        leaderTarget = padTarget;
                                    }
                                }

                                if (disposition.RequiresWidth)
                                {
                                    var polyForWidth = intersectionPiece ?? disposition.Polyline;
                                    var measurement = GeometryUtils.MeasureCorridorWidth(
                                        polyForWidth,
                                        _config.WidthSampleCount,
                                        _config.VariableWidthAbsTolerance,
                                        _config.VariableWidthRelTolerance);

                                    measuredWidth = measurement.MedianWidth;

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

                                    var isVariable = measurement.IsVariable;
                                    var usedOdFallbackWidth = false;
                                    if (isVariable && diffToSnapped <= _config.WidthSnapTolerance)
                                    {
                                        isVariable = false;
                                    }

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
                                        var matches = diffToSnapped <= _config.WidthSnapTolerance;
                                        textColorIndex = (matches && !usedOdFallbackWidth) ? 256 : 3;
                                    }

                                    measurementTarget = measurement.MedianCenter;
                                    if (!GeometryUtils.IsPointInsidePolyline(quarter.Polyline, measurementTarget) ||
                                        !GeometryUtils.IsPointInsidePolyline(polyForWidth, measurementTarget))
                                    {
                                        measurementTarget = intersectionTarget;
                                    }

                                    if (GeometryUtils.TryGetCrossSectionMidpoint(polyForWidth, measurementTarget, out var primaryMid, out _) &&
                                        GeometryUtils.IsPointInsidePolyline(quarter.Polyline, primaryMid) &&
                                        GeometryUtils.IsPointInsidePolyline(polyForWidth, primaryMid))
                                    {
                                        measurementTarget = primaryMid;
                                    }

                                    if (!GeometryUtils.IsPointInsidePolyline(quarter.Polyline, measurementTarget) ||
                                             !GeometryUtils.IsPointInsidePolyline(polyForWidth, measurementTarget))
                                    {
                                        if (!TryResolveLocalWidthMeasurementTarget(
                                                quarter.Polyline,
                                                polyForWidth,
                                                intersectionTarget,
                                                measurement.MedianCenter,
                                                measuredWidth,
                                                out measurementTarget))
                                        {
                                            measurementTarget = intersectionTarget;
                                            if (GeometryUtils.TryGetCrossSectionMidpoint(polyForWidth, measurementTarget, out var localMid, out _) &&
                                                GeometryUtils.IsPointInsidePolyline(quarter.Polyline, localMid) &&
                                                GeometryUtils.IsPointInsidePolyline(polyForWidth, localMid))
                                            {
                                                measurementTarget = localMid;
                                            }
                                        }
                                    }

                                    leaderTarget = ChooseLeaderTargetAvoidingOtherDispositions(
                                        measurementTarget,
                                        intersectionTarget,
                                        quarter.Polyline,
                                        polyForWidth,
                                        disposition,
                                        dispositions);
                                    searchTarget = leaderTarget;
                                }

                                if (disposition.ShouldAddExpiredMarker)
                                {
                                    labelText = AppendExpiredMarkerIfMissing(labelText);
                                }

                                if (!GeometryUtils.IsPointInsidePolyline(quarter.Polyline, searchTarget) ||
                                    !GeometryUtils.IsPointInsidePolyline(disposition.Polyline, searchTarget))
                                {
                                    if (GeometryUtils.TryFindPointInsideBoth(quarter.Polyline, disposition.Polyline, out var altTarget))
                                    {
                                        searchTarget = altTarget;
                                    }
                                }

                                if (!GeometryUtils.IsPointInsidePolyline(quarter.Polyline, searchTarget) ||
                                    !GeometryUtils.IsPointInsidePolyline(disposition.Polyline, searchTarget))
                                {
                                    _logger.WriteLine($"Skip label (no valid in-both target): disp={disposition.ObjectId}");
                                    continue;
                                }

                                var targetCorridor = intersectionPiece ?? disposition.Polyline;
                                var maxPoints = _config.MaxOverlapAttempts;
                                if (disposition.AddLeader)
                                {
                                    maxPoints = Math.Max(maxPoints, 160);
                                }

                                var maxLeaderLength = disposition.AddLeader ? 300.0 : double.PositiveInfinity;
                                if (_useAlignedDimensions && disposition.RequiresWidth)
                                {
                                    maxLeaderLength = Math.Max(120.0, _config.TextHeight * 20.0);
                                }

                                var allowOutsideDisposition = disposition.AllowLabelOutsideDisposition;
                                if (_useAlignedDimensions && disposition.RequiresWidth)
                                {
                                    allowOutsideDisposition = true;
                                }

                                var isWidthAligned = _useAlignedDimensions && disposition.RequiresWidth;
                                var isLeaderOrPlainText = !isWidthAligned;
                                int candidateCount;
                                if (isWidthAligned)
                                {
                                    candidateCount = GetCandidateDimensionTextPoints(
                                            quarter.Polyline,
                                            targetCorridor,
                                            measurementTarget,
                                            searchTarget,
                                            labelText,
                                            collisions: null,
                                            allDispositions: dispositions,
                                            currentDisposition: disposition,
                                            maxDistance: maxLeaderLength)
                                        .Count();
                                }
                                else
                                {
                                    candidateCount = GetCandidateLabelPoints(
                                            quarter.Polyline,
                                            disposition.Polyline,
                                            searchTarget,
                                            allowOutsideDisposition,
                                            _config.TextHeight,
                                            maxPoints,
                                            measuredWidth,
                                            maxLeaderLength,
                                            labelText,
                                            collisions: null,
                                            isLeaderOrPlainText: false)
                                        .Count();
                                }

                                var effectiveMaxPoints = maxPoints;
                                var allowQuarterOnlyLeaderPlacement =
                                    QuarterLabelFallbackPolicy.ShouldAllowQuarterOnlyLeaderPlacement(
                                        allowOutsideDisposition,
                                        isWidthAligned,
                                        disposition.AddLeader,
                                        intersectionPiece != null,
                                        candidateCount);
                                if (allowQuarterOnlyLeaderPlacement)
                                {
                                    effectiveMaxPoints = QuarterLabelFallbackPolicy.ExpandSearchPointsForQuarterOnlyLeaderPlacement(maxPoints);
                                    candidateCount = GetCandidateLabelPoints(
                                            quarter.Polyline,
                                            disposition.Polyline,
                                            searchTarget,
                                            allowOutsideDisposition: true,
                                            _config.TextHeight,
                                            effectiveMaxPoints,
                                            measuredWidth,
                                            maxLeaderLength,
                                            labelText,
                                            collisions: null,
                                            isLeaderOrPlainText)
                                        .Count();
                                }

                                if (candidateCount == 0)
                                {
                                    var fallbackCandidates = BuildQuarterFallbackCandidates(
                                        quarter.Polyline,
                                        targetCorridor,
                                        disposition.Polyline,
                                        searchTarget,
                                        measurementTarget,
                                        leaderTarget,
                                        intersectionPiece);
                                    candidateCount = fallbackCandidates.Count;
                                }

                                requests.Add(new PlacementRequest(
                                    quarter,
                                    quarterKey,
                                    disposition,
                                    intersectionPiece,
                                    measurementTarget,
                                    searchTarget,
                                    leaderTarget,
                                    labelText,
                                    textColorIndex,
                                    measuredWidth,
                                    effectiveMaxPoints,
                                    maxLeaderLength,
                                    allowOutsideDisposition || allowQuarterOnlyLeaderPlacement,
                                    isLeaderOrPlainText,
                                    candidateCount,
                                    EstimateQuarterFootprintArea(quarter, disposition, intersectionPiece),
                                    dispositionIdentity,
                                    normalizedDispNum,
                                    normalizedReuseVariant,
                                    dispositionReuseKey));

                                intersectionPiece = null;
                                if (!_config.AllowMultiQuarterDispositions)
                                {
                                    processedDispositionIds.Add(dispositionIdentity);
                                }
                            }
                            finally
                            {
                                if (intersectionPiece != null)
                                {
                                    intersectionPiece.Dispose();
                                }
                            }
                        }
                    }

                    foreach (var request in requests
                        .OrderByDescending(r => r.Disposition.RequiresWidth)
                        .ThenBy(r => r.CandidateCount)
                        .ThenBy(r => r.QuarterFootprintArea))
                    {
                        if (ShouldSkipExistingQuarterLabel(
                                existingDispNumsByQuarter,
                                request.QuarterKey,
                                request.NormalizedDispNum,
                                request.NormalizedReuseVariant,
                                request.DispositionReuseKey,
                                out _))
                        {
                            continue;
                        }

                        var isWidthAligned = _useAlignedDimensions && request.Disposition.RequiresWidth;
                        List<Point2d> candidates;
                        List<DimensionTextCandidate>? dimCandidates = null;
                        var widthSource = request.IntersectionPiece ?? request.Disposition.Polyline;
                        var targetCorridor = request.IntersectionPiece ?? request.Disposition.Polyline;
                        var dimText = string.Empty;
                        var haveDimSpan = false;
                        var dimSpanUnit = default(Vector2d);

                        if (isWidthAligned)
                        {
                            dimCandidates = GetCandidateDimensionTextPoints(
                                    request.Quarter.Polyline,
                                    widthSource,
                                    request.MeasurementTarget,
                                    request.SearchTarget,
                                    request.LabelText,
                                    collisionIndex,
                                    dispositions,
                                    request.Disposition,
                                    request.MaxLeaderLength)
                                .ToList();
                            candidates = dimCandidates.Select(x => x.TextPoint).ToList();
                            dimText = ConvertLabelTextForDimension(request.LabelText);
                            haveDimSpan = TryGetDimensionSpanGeometry(
                                widthSource,
                                request.MeasurementTarget,
                                out _,
                                out _,
                                out _,
                                out dimSpanUnit,
                                out _);
                        }
                        else
                        {
                            candidates = GetCandidateLabelPoints(
                                    request.Quarter.Polyline,
                                    request.Disposition.Polyline,
                                    request.SearchTarget,
                                    request.AllowOutsideDisposition,
                                    _config.TextHeight,
                                    request.MaxPoints,
                                    request.MeasuredWidth,
                                    request.MaxLeaderLength,
                                    request.LabelText,
                                    collisionIndex,
                                    request.IsLeaderOrPlainText)
                                .ToList();
                        }

                        if (candidates.Count == 0)
                        {
                            var fallbackCandidates = BuildQuarterFallbackCandidates(
                                request.Quarter.Polyline,
                                targetCorridor,
                                request.Disposition.Polyline,
                                request.SearchTarget,
                                request.MeasurementTarget,
                                request.LeaderTarget,
                                request.IntersectionPiece);
                            if (fallbackCandidates.Count == 0)
                            {
                                _logger.WriteLine($"Skip label (no valid in-shape target): disp={request.Disposition.ObjectId}");
                                continue;
                            }

                            candidates.AddRange(fallbackCandidates);
                        }

                        var placed = false;
                        var bestFallback = candidates[candidates.Count - 1];
                        var bestFallbackIndex = candidates.Count - 1;
                        var bestFallbackScore = double.MaxValue;
                        var scoreTextHeight = ResolveLabelTextHeight(0.0);

                        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
                        {
                            var pt = candidates[candidateIndex];
                            Aabb2d scoreBox;
                            double overlapArea;
                            int crowdednessCount;
                            int lineworkOverlapCount;
                            double fallbackScore;

                            if (isWidthAligned && haveDimSpan)
                            {
                                scoreBox = EstimateDimensionTextBox(
                                    pt,
                                    dimText,
                                    scoreTextHeight,
                                    dimSpanUnit,
                                    scoreTextHeight * 0.35);
                                overlapArea = collisionIndex.OverlapArea(scoreBox);
                                crowdednessCount = collisionIndex.CountNearby(scoreBox, scoreTextHeight * 3.0);
                                lineworkOverlapCount = CountIntersectingDispositionLinework(
                                    scoreBox.ToExtents3d(),
                                    dispositions,
                                    request.Disposition);
                                fallbackScore = dimCandidates != null && candidateIndex < dimCandidates.Count
                                    ? dimCandidates[candidateIndex].Score
                                    : (overlapArea * 1000000.0) +
                                      (lineworkOverlapCount * 10000.0) +
                                      (crowdednessCount * 500.0) +
                                      pt.GetDistanceTo(request.SearchTarget);
                            }
                            else
                            {
                                scoreBox = EstimateCenteredTextBox(
                                    pt,
                                    request.LabelText,
                                    scoreTextHeight,
                                    scoreTextHeight * 0.30);
                                overlapArea = collisionIndex.OverlapArea(scoreBox);
                                crowdednessCount = collisionIndex.CountNearby(scoreBox, scoreTextHeight * 3.0);
                                lineworkOverlapCount = CountIntersectingDispositionLinework(
                                    scoreBox.ToExtents3d(),
                                    dispositions,
                                    request.Disposition);
                                fallbackScore = (overlapArea * 1000000.0) +
                                                (lineworkOverlapCount * 1000.0) +
                                                (crowdednessCount * 200.0) +
                                                pt.GetDistanceTo(request.SearchTarget);
                            }

                            if (fallbackScore < bestFallbackScore)
                            {
                                bestFallbackScore = fallbackScore;
                                bestFallback = pt;
                                bestFallbackIndex = candidateIndex;
                            }

                            if (overlapArea > 0.0 || lineworkOverlapCount > 0)
                            {
                                continue;
                            }

                            var creationTarget = isWidthAligned ? request.MeasurementTarget : request.LeaderTarget;
                            var requestedDimLineOffset = isWidthAligned && dimCandidates != null && candidateIndex < dimCandidates.Count
                                ? dimCandidates[candidateIndex].DimLineOffset
                                : (double?)null;
                            var created = CreateLabelEntity(
                                transaction,
                                modelSpace,
                                request.Quarter.Polyline,
                                dispositions,
                                creationTarget,
                                pt,
                                widthSource,
                                request.Disposition,
                                request.LabelText,
                                request.TextColorIndex,
                                requestedDimLineOffset,
                                collisionIndex);
                            if (created == null)
                            {
                                continue;
                            }

                            placed = true;
                            result.LabelsPlaced++;
                            RegisterPlacedQuarterLabel(
                                existingDispNumsByQuarter,
                                request.QuarterKey,
                                request.NormalizedDispNum,
                                request.NormalizedReuseVariant,
                                request.DispositionReuseKey);
                            break;
                        }

                        if (!placed && _config.PlaceWhenOverlapFails && candidates.Count > 0)
                        {
                            var creationTarget = isWidthAligned ? request.MeasurementTarget : request.LeaderTarget;
                            var requestedDimLineOffset = isWidthAligned && dimCandidates != null && bestFallbackIndex < dimCandidates.Count
                                ? dimCandidates[bestFallbackIndex].DimLineOffset
                                : (double?)null;
                            var forced = CreateLabelEntity(
                                transaction,
                                modelSpace,
                                request.Quarter.Polyline,
                                dispositions,
                                creationTarget,
                                bestFallback,
                                widthSource,
                                request.Disposition,
                                request.LabelText,
                                request.TextColorIndex,
                                requestedDimLineOffset,
                                collisionIndex,
                                allowOverlap: true);
                            if (forced != null)
                            {
                                placed = true;
                                result.LabelsPlaced++;
                                result.OverlapForced++;
                                RegisterPlacedQuarterLabel(
                                    existingDispNumsByQuarter,
                                    request.QuarterKey,
                                    request.NormalizedDispNum,
                                    request.NormalizedReuseVariant,
                                    request.DispositionReuseKey);
                            }
                        }

                        if (!placed)
                        {
                            _logger.WriteLine($"Could not place label for disposition {request.Disposition.ObjectId}");
                        }
                    }

                    transaction.Commit();
                }
            }
            finally
            {
                foreach (var request in requests)
                {
                    request.Dispose();
                }
            }

            return result;
        }
        private Entity? CreateLabelEntity(
            Transaction tr,
            BlockTableRecord modelSpace,
            Polyline quarterPolyline,
            IReadOnlyCollection<DispositionInfo> allDispositions,
            Point2d target,
            Point2d labelPoint,
            Polyline polyForWidth,
            DispositionInfo disposition,
            string labelText,
            int textColorIndex,
            double? requestedDimLineOffset,
            LabelCollisionIndex? collisions,
            bool allowOverlap = false)
        {
            var resolvedTextColorIndex = DispositionLabelColorPolicy.ResolveTextColorIndex(labelText, textColorIndex);

            if (_useAlignedDimensions && disposition.RequiresWidth)
            {
                var dimension = CreateAlignedDimensionLabel(
                    tr,
                    modelSpace,
                    quarterPolyline,
                    allDispositions,
                    target,
                    labelPoint,
                    polyForWidth,
                    disposition,
                    labelText,
                    disposition.TextLayerName,
                    resolvedTextColorIndex,
                    requestedDimLineOffset,
                    collisions,
                    allowOverlap);
                if (dimension != null)
                {
                    return dimension;
                }

                return CreateLabel(
                    tr,
                    modelSpace,
                    labelPoint,
                    labelText,
                    disposition.TextLayerName,
                    resolvedTextColorIndex,
                    collisions,
                    allowOverlap);
            }

            if (_config.EnableLeaders && disposition.AddLeader)
            {
                return CreateLeader(
                    tr,
                    modelSpace,
                    target,
                    labelPoint,
                    labelText,
                    disposition.TextLayerName,
                    resolvedTextColorIndex,
                    collisions,
                    allowOverlap);
            }

            return CreateLabel(
                tr,
                modelSpace,
                labelPoint,
                labelText,
                disposition.TextLayerName,
                resolvedTextColorIndex,
                collisions,
                allowOverlap);
        }
        private AlignedDimension? CreateAlignedDimensionLabel(
            Transaction tr,
            BlockTableRecord modelSpace,
            Polyline quarterPolyline,
            IReadOnlyCollection<DispositionInfo> allDispositions,
            Point2d target,
            Point2d labelPoint,
            Polyline polyForWidth,
            DispositionInfo disposition,
            string labelText,
            string layerName,
            int colorIndex,
            double? requestedDimLineOffset,
            LabelCollisionIndex? collisions,
            bool allowOverlap = false)
        {
            var widthSource = polyForWidth ?? disposition.Polyline;
            if (!TryGetDimensionSpanGeometry(
                    widthSource,
                    target,
                    out var a2d,
                    out var b2d,
                    out var mid,
                    out var spanUnit,
                    out var normal))
            {
                return null;
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
            var span = b2d - a2d;
            if (span.Length <= 1e-6)
            {
                return null;
            }

            spanUnit = span / span.Length;
            normal = new Vector2d(-spanUnit.Y, spanUnit.X);
            mid = new Point2d((a2d.X + b2d.X) * 0.5, (a2d.Y + b2d.Y) * 0.5);

            var textHeight = ResolveLabelTextHeight(0.0);
            var dimText = ConvertLabelTextForDimension(labelText);
            var dimLineOffset = ResolveWidthAlignedDimensionLineOffset(
                widthSource,
                mid,
                normal,
                textHeight,
                requestedDimLineOffset ?? 0.0);
            var placement = WidthAlignedDimensionPlacementPolicy.Resolve(
                new WidthDimensionPoint(a2d.X, a2d.Y),
                new WidthDimensionPoint(b2d.X, b2d.Y),
                new WidthDimensionPoint(labelPoint.X, labelPoint.Y),
                dimLineOffset);
            var textPoint = new Point2d(placement.TextPoint.X, placement.TextPoint.Y);
            if (!GeometryUtils.IsPointInsidePolyline(quarterPolyline, textPoint))
            {
                return null;
            }

            var textBox = EstimateDimensionTextBox(
                textPoint,
                dimText,
                textHeight,
                spanUnit,
                textHeight * 0.35);

            if (!allowOverlap)
            {
                if (collisions != null && collisions.Intersects(textBox))
                {
                    return null;
                }

                if (CountIntersectingDispositionLinework(textBox.ToExtents3d(), allDispositions, disposition) > 0)
                {
                    return null;
                }
            }

            var dimLinePoint = new Point3d(
                placement.DimLinePoint.X,
                placement.DimLinePoint.Y,
                0.0);
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

            dimension.DimensionText = dimText;

            modelSpace.AppendEntity(dimension);
            tr.AddNewlyCreatedDBObject(dimension, true);
            TrySetDimensionTextMovementMode(dimension, 0);

            try
            {
                dimension.TextPosition = new Point3d(textPoint.X, textPoint.Y, 0.0);
                TrySetUsingDefaultTextPosition(dimension, false);
            }
            catch
            {
                // Leave native placement if explicit text override is unavailable on this build.
            }

            TryRecomputeAlignedDimensionBlock(dimension);
            TryNormalizeAlignedDimensionTextOrientation(dimension);

            collisions?.Add(textBox);
            return dimension;
        }

        private static double ResolveWidthAlignedDimensionLineOffset(
            Polyline widthSource,
            Point2d origin,
            Vector2d normal,
            double textHeight,
            double requestedDimLineOffset)
        {
            if (normal.Length <= 1e-9)
            {
                return 0.0;
            }

            var limitedRequestedOffset = 0.0;
            if (Math.Abs(requestedDimLineOffset) > 1e-6)
            {
                var maxPreferredOffset = Math.Max(0.25, Math.Min(textHeight * 0.25, 1.0));
                limitedRequestedOffset = Math.Max(-maxPreferredOffset, Math.Min(maxPreferredOffset, requestedDimLineOffset));
            }

            return ClampSignedOffsetInsideDisposition(widthSource, origin, normal, limitedRequestedOffset, 0.0);
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

        private MLeader? CreateLeader(
            Transaction tr,
            BlockTableRecord modelSpace,
            Point2d target,
            Point2d labelPoint,
            string labelText,
            string layerName,
            int colorIndex,
            LabelCollisionIndex? collisions,
            bool allowOverlap = false)
        {
            var textHeight = ResolveLabelTextHeight(0.0);
            var textBox = EstimateCenteredTextBox(labelPoint, labelText, textHeight, textHeight * 0.30);
            if (collisions != null && !allowOverlap && collisions.Intersects(textBox))
            {
                return null;
            }

            var attachment = GetLeaderAttachment(target, labelPoint);
            var mtext = new MText
            {
                Location = new Point3d(labelPoint.X, labelPoint.Y, 0),
                TextHeight = _config.TextHeight,
                Contents = labelText,
                Layer = layerName,
                ColorIndex = colorIndex,
                Attachment = attachment
            };
            ApplyDimensionStyle(tr, mtext, out _);

            var mleader = new MLeader();
            mleader.SetDatabaseDefaults();
            ApplyLeaderStyle(tr, mleader);
            mleader.ContentType = ContentType.MTextContent;
            mleader.MText = mtext;
            mleader.TextAttachmentType = GetLeaderTextAttachment(attachment);
            int leaderIndex = mleader.AddLeader();
            int lineIndex = mleader.AddLeaderLine(leaderIndex);

            mleader.AddFirstVertex(lineIndex, new Point3d(target.X, target.Y, 0));
            mleader.AddLastVertex(lineIndex, new Point3d(labelPoint.X, labelPoint.Y, 0));

            mleader.LeaderLineType = LeaderType.StraightLeader;
            mleader.EnableLanding = _config.LeaderHorizontalLanding;
            if (_config.LeaderHorizontalLanding)
            {
                mleader.DoglegLength = _config.LeaderLandingDistance;
                mleader.LandingGap = _config.LeaderLandingGap;
            }

            var arrowId = GetLeaderArrowId(tr);
            if (!arrowId.IsNull)
                mleader.ArrowSymbolId = arrowId;
            mleader.ArrowSize = 5.0;

            mleader.Layer = layerName;
            mleader.ColorIndex = colorIndex;

            modelSpace.AppendEntity(mleader);
            tr.AddNewlyCreatedDBObject(mleader, true);
            collisions?.Add(textBox);
            return mleader;
        }
        private MText? CreateLabel(
            Transaction tr,
            BlockTableRecord modelSpace,
            Point2d labelPoint,
            string labelText,
            string layerName,
            int colorIndex,
            LabelCollisionIndex? collisions,
            bool allowOverlap = false)
        {
            var textHeight = ResolveLabelTextHeight(0.0);
            var textBox = EstimateCenteredTextBox(labelPoint, labelText, textHeight, textHeight * 0.30);
            if (collisions != null && !allowOverlap && collisions.Intersects(textBox))
            {
                return null;
            }

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
            modelSpace.AppendEntity(mtext);
            tr.AddNewlyCreatedDBObject(mtext, true);
            collisions?.Add(textBox);
            return mtext;
        }

        private LabelCollisionIndex BuildCollisionIndex(
            Transaction tr,
            BlockTableRecord modelSpace,
            IReadOnlyCollection<DispositionInfo> dispositions)
        {
            var collisions = new LabelCollisionIndex();
            var dispositionTextLayers = new HashSet<string>(
                dispositions
                    .Where(d => !string.IsNullOrWhiteSpace(d.TextLayerName))
                    .Select(d => d.TextLayerName),
                StringComparer.OrdinalIgnoreCase);

            foreach (ObjectId id in modelSpace)
            {
                Entity? entity = null;
                try
                {
                    entity = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                }
                catch
                {
                    continue;
                }

                if (entity == null || entity.IsErased)
                {
                    continue;
                }

                if (!(entity is MText || entity is MLeader || entity is AlignedDimension))
                {
                    continue;
                }

                if (!TryGetEntityCollisionBox(entity, out var box, out var labelText))
                {
                    continue;
                }

                var layerName = entity.Layer ?? string.Empty;
                if (dispositionTextLayers.Count > 0 &&
                    !dispositionTextLayers.Contains(layerName) &&
                    !LooksLikeDispositionLabel(labelText))
                {
                    continue;
                }

                collisions.Add(box);
            }

            return collisions;
        }

        private bool TryGetEntityCollisionBox(Entity entity, out Aabb2d box, out string labelText)
        {
            box = default;
            labelText = string.Empty;

            switch (entity)
            {
                case MText mtext:
                {
                    labelText = mtext.Contents ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(labelText))
                    {
                        return false;
                    }

                    var textHeight = ResolveLabelTextHeight(mtext.TextHeight);
                    box = EstimateCenteredTextBox(
                        new Point2d(mtext.Location.X, mtext.Location.Y),
                        labelText,
                        textHeight,
                        textHeight * 0.30);
                    return true;
                }
                case MLeader mleader:
                {
                    var leaderText = mleader.MText;
                    if (leaderText == null)
                    {
                        return false;
                    }

                    labelText = leaderText.Contents ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(labelText))
                    {
                        return false;
                    }

                    var textHeight = ResolveLabelTextHeight(leaderText.TextHeight);
                    box = EstimateCenteredTextBox(
                        new Point2d(leaderText.Location.X, leaderText.Location.Y),
                        labelText,
                        textHeight,
                        textHeight * 0.30);
                    return true;
                }
                case AlignedDimension aligned:
                {
                    labelText = aligned.DimensionText ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(labelText) || string.Equals(labelText.Trim(), "<>", StringComparison.Ordinal))
                    {
                        return false;
                    }

                    if (!TryGetDimensionTextPoint(aligned, out var textPoint))
                    {
                        return false;
                    }

                    if (!TryGetDimensionSpanUnit(aligned, out var spanUnit))
                    {
                        spanUnit = new Vector2d(1.0, 0.0);
                    }

                    var dimText = ConvertLabelTextForDimension(labelText);
                    var textHeight = ResolveLabelTextHeight(aligned.Dimtxt);
                    box = EstimateDimensionTextBox(
                        textPoint,
                        dimText,
                        textHeight,
                        spanUnit,
                        textHeight * 0.35);
                    return true;
                }
                default:
                    return false;
            }
        }

        private double ResolveLabelTextHeight(double entityTextHeight)
        {
            if (entityTextHeight > 0.0)
            {
                return entityTextHeight;
            }

            return _config.TextHeight > 0.0 ? _config.TextHeight : 10.0;
        }

        private static bool TryGetDimensionTextPoint(AlignedDimension dimension, out Point2d textPoint)
        {
            textPoint = default;
            try
            {
                var textPosition = dimension.TextPosition;
                textPoint = new Point2d(textPosition.X, textPosition.Y);
                return true;
            }
            catch
            {
                // fallback below
            }

            try
            {
                var a = dimension.XLine1Point;
                var b = dimension.XLine2Point;
                textPoint = new Point2d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetDimensionSpanUnit(AlignedDimension dimension, out Vector2d spanUnit)
        {
            spanUnit = new Vector2d(1.0, 0.0);
            try
            {
                var a = dimension.XLine1Point;
                var b = dimension.XLine2Point;
                var span = new Vector2d(b.X - a.X, b.Y - a.Y);
                if (span.Length <= 1e-6)
                {
                    return false;
                }

                spanUnit = span / span.Length;
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static void ReapplyAlignedDimensionTextPlacement(AlignedDimension dimension)
        {
            if (dimension == null)
            {
                return;
            }

            try
            {
                var a = dimension.XLine1Point;
                var b = dimension.XLine2Point;
                var span = new Vector2d(b.X - a.X, b.Y - a.Y);
                var spanLength = span.Length;
                if (spanLength <= 1e-6)
                {
                    return;
                }

                var spanUnit = span / spanLength;
                var normal = new Vector2d(-spanUnit.Y, spanUnit.X);
                var mid = new Point2d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);
                var dimText = dimension.DimensionText ?? string.Empty;
                var textHeight = ResolveDimensionTextHeight(dimension);
                var pad = textHeight * 0.35;

                if (!TryGetDimensionTextPoint(dimension, out var textPoint))
                {
                    textPoint = mid;
                }

                var requested = textPoint - mid;
                var textAlong = ClampDimensionTextAlongOffset(
                    requested.DotProduct(spanUnit),
                    spanLength,
                    dimText,
                    textHeight,
                    pad);

                if (TryGetAlignedDimensionLinePoint(dimension, out var currentDimLinePoint))
                {
                    var dimRequest = new Point2d(currentDimLinePoint.X, currentDimLinePoint.Y) - mid;
                    var dimOffset = dimRequest.DotProduct(normal);
                    var clampedDimLinePoint = new Point3d(
                        mid.X + normal.X * dimOffset,
                        mid.Y + normal.Y * dimOffset,
                        0.0);
                    TrySetAlignedDimensionLinePoint(dimension, clampedDimLinePoint);
                }

                var clampedTextPoint = new Point3d(
                    mid.X + spanUnit.X * textAlong,
                    mid.Y + spanUnit.Y * textAlong,
                    0.0);
                dimension.TextPosition = clampedTextPoint;
                TrySetUsingDefaultTextPosition(dimension, false);
                TryRecomputeAlignedDimensionBlock(dimension);
                dimension.RecordGraphicsModified(true);
            }
            catch
            {
                // best effort only
            }
        }

        private static void TryProjectAlignedDimensionLinePointUnderText(AlignedDimension dimension)
        {
            if (dimension == null)
            {
                return;
            }

            try
            {
                var a = dimension.XLine1Point;
                var b = dimension.XLine2Point;
                var span = new Vector2d(b.X - a.X, b.Y - a.Y);
                var spanLength = span.Length;
                if (spanLength <= 1e-6 || !TryGetAlignedDimensionLinePoint(dimension, out var currentDimLinePoint))
                {
                    return;
                }

                if (!TryGetDimensionTextPoint(dimension, out var textPoint))
                {
                    return;
                }

                var spanUnit = span / spanLength;
                var normal = new Vector2d(-spanUnit.Y, spanUnit.X);
                var mid = new Point2d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);
                var requested = textPoint - mid;
                var textAlong = requested.DotProduct(spanUnit);
                var currentRequest = new Point2d(currentDimLinePoint.X, currentDimLinePoint.Y) - mid;
                var dimOffset = currentRequest.DotProduct(normal);
                var projectedAlong = ClampDimensionLineAlongOffset(
                    textAlong,
                    spanLength,
                    Math.Max(ResolveDimensionTextHeight(dimension) * 0.25, 1.0));

                var projectedDimLinePoint = new Point3d(
                    mid.X + spanUnit.X * projectedAlong + normal.X * dimOffset,
                    mid.Y + spanUnit.Y * projectedAlong + normal.Y * dimOffset,
                    0.0);
                TrySetAlignedDimensionLinePoint(dimension, projectedDimLinePoint);
                TryRecomputeAlignedDimensionBlock(dimension);
                dimension.RecordGraphicsModified(true);
            }
            catch
            {
                // best effort only
            }
        }

        private static double ResolveDimensionTextHeight(Dimension dimension)
        {
            if (dimension == null)
            {
                return 10.0;
            }

            try
            {
                if (dimension.Dimtxt > 0.0)
                {
                    return dimension.Dimtxt;
                }
            }
            catch
            {
                // fall through
            }

            return 10.0;
        }

        private static bool TryGetAlignedDimensionLinePoint(AlignedDimension dimension, out Point3d dimLinePoint)
        {
            dimLinePoint = default;
            if (dimension == null)
            {
                return false;
            }

            try
            {
                var property = dimension.GetType().GetProperty("DimLinePoint", BindingFlags.Instance | BindingFlags.Public);
                if (property != null && property.CanRead)
                {
                    var value = property.GetValue(dimension, null);
                    if (value is Point3d point)
                    {
                        dimLinePoint = point;
                        return true;
                    }
                }
            }
            catch
            {
                // reflection fallback below
            }

            return false;
        }

        private static bool TrySetAlignedDimensionLinePoint(AlignedDimension dimension, Point3d dimLinePoint)
        {
            if (dimension == null)
            {
                return false;
            }

            try
            {
                var property = dimension.GetType().GetProperty("DimLinePoint", BindingFlags.Instance | BindingFlags.Public);
                if (property != null && property.CanWrite)
                {
                    property.SetValue(dimension, dimLinePoint, null);
                    return true;
                }
            }
            catch
            {
                // best effort only
            }

            return false;
        }

        private static void TrySetUsingDefaultTextPosition(Dimension dimension, bool value)
        {
            if (dimension == null)
            {
                return;
            }

            try
            {
                var property = dimension.GetType().GetProperty("UsingDefaultTextPosition", BindingFlags.Instance | BindingFlags.Public);
                if (property != null && property.CanWrite)
                {
                    property.SetValue(dimension, value, null);
                }
            }
            catch
            {
                // best effort only
            }
        }

        private static void TryRecomputeAlignedDimensionBlock(AlignedDimension dimension)
        {
            if (dimension == null)
            {
                return;
            }

            try
            {
                var generateLayout = dimension.GetType().GetMethod(
                    "GenerateLayout",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    Type.EmptyTypes,
                    null);
                if (generateLayout != null)
                {
                    generateLayout.Invoke(dimension, null);
                }

                var method = dimension.GetType().GetMethod(
                    "RecomputeDimensionBlock",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(bool) },
                    null);
                if (method != null)
                {
                    method.Invoke(dimension, new object[] { true });
                }
            }
            catch
            {
                // best effort only
            }
        }

        internal static int AlignRenderedAlignedDimensionTextsToLeaders(Database database, Logger logger)
        {
            if (database == null)
            {
                return 0;
            }

            try
            {
                using (var tr = database.TransactionManager.StartTransaction())
                {
                    var blockTable = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                    var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                    var inspected = 0;
                    var movementAdjusted = 0;
                    var placementNormalized = 0;
                    var rotationAdjusted = 0;

                    foreach (ObjectId entityId in modelSpace)
                    {
                        if (!(tr.GetObject(entityId, OpenMode.ForWrite, false) is AlignedDimension dimension) || dimension.IsErased)
                        {
                            continue;
                        }

                        inspected++;
                        if (TrySetDimensionTextMovementMode(dimension, 0))
                        {
                            movementAdjusted++;
                        }

                        if (TryNormalizeWidthAlignedDimensionPlacement(tr, dimension))
                        {
                            placementNormalized++;
                        }

                        if (TryNormalizeAlignedDimensionTextOrientation(dimension))
                        {
                            rotationAdjusted++;
                        }
                    }

                    tr.Commit();
                    logger?.WriteLine(
                        $"Aligned dimension finalize pass: inspected={inspected}, movementAdjusted={movementAdjusted}, helperLinesAdded=0, placementNormalized={placementNormalized}, rotationAdjusted={rotationAdjusted}.");
                    return movementAdjusted + placementNormalized + rotationAdjusted;
                }
            }
            catch (System.Exception ex)
            {
                logger?.WriteLine("Aligned dimension finalize pass failed: " + ex.Message);
                return 0;
            }
        }

        private static bool TryNormalizeWidthAlignedDimensionPlacement(
            Transaction tr,
            AlignedDimension dimension)
        {
            if (tr == null || dimension == null)
            {
                return false;
            }

            try
            {
                var a = dimension.XLine1Point;
                var b = dimension.XLine2Point;
                var span = new Vector2d(b.X - a.X, b.Y - a.Y);
                var spanLength = span.Length;
                if (spanLength <= 1e-6 ||
                    !TryGetDimensionTextPoint(dimension, out var textPoint))
                {
                    return false;
                }

                var spanUnit = span / spanLength;
                var normal = new Vector2d(-spanUnit.Y, spanUnit.X);
                var mid = new Point2d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);

                var textHeight = ResolveDimensionTextHeight(dimension);
                var dimText = dimension.DimensionText ?? string.Empty;
                var pad = textHeight * 0.35;
                var requested = textPoint - mid;
                var requestedAlong = requested.DotProduct(spanUnit);
                var requestedNormal = requested.DotProduct(normal);
                var normalizedAlong = ClampDimensionTextAlongOffset(
                    requestedAlong,
                    spanLength,
                    dimText,
                    textHeight,
                    pad);
                var changed = Math.Abs(normalizedAlong - requestedAlong) > 1e-6 ||
                              Math.Abs(requestedNormal) > 1e-6;

                if (TryGetAlignedDimensionLinePoint(dimension, out var currentDimLinePoint))
                {
                    var dimRequest = new Point2d(currentDimLinePoint.X, currentDimLinePoint.Y) - mid;
                    var dimOffset = dimRequest.DotProduct(normal);
                    var normalizedDimLinePoint = new Point3d(
                        mid.X + normal.X * dimOffset,
                        mid.Y + normal.Y * dimOffset,
                        0.0);
                    changed |= currentDimLinePoint.DistanceTo(normalizedDimLinePoint) > 1e-6;
                    TrySetAlignedDimensionLinePoint(dimension, normalizedDimLinePoint);
                }

                if (!changed)
                {
                    return false;
                }

                dimension.TextPosition = new Point3d(
                    mid.X + spanUnit.X * normalizedAlong,
                    mid.Y + spanUnit.Y * normalizedAlong,
                    0.0);
                TrySetUsingDefaultTextPosition(dimension, false);
                TryRecomputeAlignedDimensionBlock(dimension);
                dimension.RecordGraphicsModified(true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetRenderedAlignedDimensionMaxLineLength(
            Transaction tr,
            AlignedDimension dimension,
            out double maxLineLength)
        {
            maxLineLength = 0.0;
            if (tr == null || dimension == null)
            {
                return false;
            }

            try
            {
                var dimBlockId = dimension.DimBlockId;
                if (dimBlockId.IsNull || !dimBlockId.IsValid)
                {
                    return false;
                }

                if (!(tr.GetObject(dimBlockId, OpenMode.ForRead, false) is BlockTableRecord dimBlock))
                {
                    return false;
                }

                var foundLine = false;
                foreach (ObjectId entityId in dimBlock)
                {
                    if (!(tr.GetObject(entityId, OpenMode.ForRead, false) is Line line))
                    {
                        continue;
                    }

                    foundLine = true;
                    var length = line.StartPoint.DistanceTo(line.EndPoint);
                    if (length > maxLineLength)
                    {
                        maxLineLength = length;
                    }
                }

                return foundLine;
            }
            catch
            {
                return false;
            }
        }

        private static bool TrySetDimensionTextMovementMode(AlignedDimension dimension, int expectedMode)
        {
            if (dimension == null)
            {
                return false;
            }

            try
            {
                var dimensionType = dimension.GetType();
                var property =
                    dimensionType.GetProperty("TextMovement", BindingFlags.Instance | BindingFlags.Public) ??
                    dimensionType.GetProperty("Dimtmove", BindingFlags.Instance | BindingFlags.Public);
                if (property == null || !property.CanWrite)
                {
                    return false;
                }

                var currentValue = property.CanRead
                    ? property.GetValue(dimension, null)
                    : null;
                var currentNumeric = currentValue == null
                    ? int.MinValue
                    : Convert.ToInt32(currentValue);
                if (currentNumeric == expectedMode)
                {
                    return false;
                }

                var propertyValue = property.PropertyType.IsEnum
                    ? Enum.ToObject(property.PropertyType, expectedMode)
                    : Convert.ChangeType(expectedMode, property.PropertyType);
                property.SetValue(dimension, propertyValue, null);
                TryRecomputeAlignedDimensionBlock(dimension);
                dimension.RecordGraphicsModified(true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryNormalizeAlignedDimensionTextOrientation(AlignedDimension dimension)
        {
            if (dimension == null)
            {
                return false;
            }

            try
            {
                var currentRotation = NormalizeAlignedDimensionTextRotation(dimension.TextRotation);
                if (Math.Abs(currentRotation) <= 1e-4)
                {
                    return false;
                }

                dimension.TextRotation = 0.0;
                dimension.RecordGraphicsModified(true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static double NormalizeAlignedDimensionTextRotation(double angle)
        {
            while (angle <= -Math.PI * 0.5)
            {
                angle += Math.PI;
            }

            while (angle > Math.PI * 0.5)
            {
                angle -= Math.PI;
            }

            return angle;
        }

        private static bool LooksLikeDispositionLabel(string labelText)
        {
            var flattened = ConvertLabelTextForDimension(labelText);
            if (string.IsNullOrWhiteSpace(flattened))
            {
                return false;
            }

            return Regex.IsMatch(flattened, @"\b[A-Z]{3}\s*[-/]?\s*0*\d+\b", RegexOptions.IgnoreCase);
        }

        private static List<string> SplitLabelLines(string labelText)
        {
            var normalized = (labelText ?? string.Empty)
                .Replace("\\P", "\n")
                .Replace("\\X", "\n")
                .Replace("\r\n", "\n")
                .Replace('\r', '\n');
            var rawLines = normalized.Split(new[] { '\n' }, StringSplitOptions.None);
            var lines = new List<string>(rawLines.Length);
            foreach (var rawLine in rawLines)
            {
                lines.Add(StripLabelFormatting(rawLine).Trim());
            }

            if (lines.Count == 0)
            {
                lines.Add(string.Empty);
            }

            return lines;
        }

        private static string StripLabelFormatting(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var builder = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                var ch = text[i];
                if (ch == '\\')
                {
                    if (i + 1 < text.Length)
                    {
                        i++;
                    }

                    continue;
                }

                if (ch == '{' || ch == '}')
                {
                    continue;
                }

                builder.Append(ch);
            }

            return builder.ToString();
        }

        private static Aabb2d EstimateCenteredTextBox(
            Point2d center,
            string text,
            double textHeight,
            double pad)
        {
            if (textHeight <= 0.0)
            {
                textHeight = 10.0;
            }

            var lines = SplitLabelLines(text);
            var lineCount = Math.Max(1, lines.Count);
            var maxChars = Math.Max(1, lines.Max(s => Math.Max(1, s.Length)));
            var width = Math.Max(textHeight * 2.0, maxChars * textHeight * 0.62);
            var height = lineCount * textHeight * 1.35;

            return new Aabb2d(
                center.X - width * 0.5 - pad,
                center.Y - height * 0.5 - pad,
                center.X + width * 0.5 + pad,
                center.Y + height * 0.5 + pad);
        }

        private static Aabb2d EstimateDimensionTextBox(
            Point2d center,
            string text,
            double textHeight,
            Vector2d spanUnit,
            double pad)
        {
            return EstimateCenteredTextBox(center, text, textHeight, pad);
        }

        private static double EstimateDimensionTextNormalHalfExtent(
            string text,
            double textHeight,
            double pad)
        {
            return EstimateDimensionTextNormalHalfExtent(text, textHeight, pad, Vector2d.XAxis);
        }

        private static double EstimateDimensionTextNormalHalfExtent(
            string text,
            double textHeight,
            double pad,
            Vector2d spanUnit)
        {
            EstimateDimensionTextProjectedHalfExtents(text, textHeight, pad, spanUnit, out _, out var halfNormal);
            return halfNormal;
        }

        private static double EstimateDimensionTextAlongHalfExtent(
            string text,
            double textHeight,
            double pad)
        {
            return EstimateDimensionTextAlongHalfExtent(text, textHeight, pad, Vector2d.XAxis);
        }

        private static double EstimateDimensionTextAlongHalfExtent(
            string text,
            double textHeight,
            double pad,
            Vector2d spanUnit)
        {
            EstimateDimensionTextProjectedHalfExtents(text, textHeight, pad, spanUnit, out var halfAlong, out _);
            return halfAlong;
        }

        private static void EstimateDimensionTextProjectedHalfExtents(
            string text,
            double textHeight,
            double pad,
            Vector2d spanUnit,
            out double halfAlong,
            out double halfNormal)
        {
            if (textHeight <= 0.0)
            {
                textHeight = 10.0;
            }

            var lines = SplitLabelLines(text);
            var lineCount = Math.Max(1, lines.Count);
            var maxChars = Math.Max(1, lines.Max(s => Math.Max(1, s.Length)));
            var width = Math.Max(textHeight * 2.0, maxChars * textHeight * 0.62) + (pad * 2.0);
            var height = (lineCount * textHeight * 1.35) + (pad * 2.0);

            if (spanUnit.Length <= 1e-6)
            {
                spanUnit = Vector2d.XAxis;
            }
            else
            {
                spanUnit = spanUnit.GetNormal();
            }

            var c = Math.Abs(spanUnit.X);
            var s = Math.Abs(spanUnit.Y);
            halfAlong = (width * 0.5 * c) + (height * 0.5 * s);
            halfNormal = (width * 0.5 * s) + (height * 0.5 * c);
        }

        private static double ClampDimensionTextAlongOffset(
            double requestedAlong,
            double spanLength,
            string text,
            double textHeight,
            double pad)
        {
            if (spanLength <= 1e-6)
            {
                return 0.0;
            }

            var halfTextAlong = EstimateDimensionTextAlongHalfExtent(text, textHeight, pad);
            var edgeMargin = Math.Max(textHeight * 0.25, 1.0);
            var maxAlong = Math.Max(0.0, (spanLength * 0.5) - halfTextAlong - edgeMargin);
            if (maxAlong <= 1e-6)
            {
                var preferredOutsideAlong = WidthAlignedDimensionPlacementPolicy.GetPreferredOutsideAlongOffset(
                    spanLength,
                    halfTextAlong,
                    edgeMargin);
                if (preferredOutsideAlong <= 1e-6)
                {
                    return 0.0;
                }

                var sign = requestedAlong < 0.0 ? -1.0 : 1.0;
                if (Math.Abs(requestedAlong) > preferredOutsideAlong)
                {
                    return requestedAlong;
                }

                return sign * preferredOutsideAlong;
            }

            if (requestedAlong > maxAlong)
            {
                return maxAlong;
            }

            if (requestedAlong < -maxAlong)
            {
                return -maxAlong;
            }

            return requestedAlong;
        }

        private static double ClampDimensionLineAlongOffset(
            double requestedAlong,
            double spanLength,
            double margin)
        {
            if (spanLength <= 1e-6)
            {
                return 0.0;
            }

            var maxAlong = Math.Max(0.0, (spanLength * 0.5) - Math.Max(margin, 0.0));
            if (maxAlong <= 1e-6)
            {
                return 0.0;
            }

            if (requestedAlong > maxAlong)
            {
                return maxAlong;
            }

            if (requestedAlong < -maxAlong)
            {
                return -maxAlong;
            }

            return requestedAlong;
        }

        private static bool HasDimensionTextClearanceFromMeasuredLine(
            Polyline widthSource,
            Point2d textPoint,
            string dimText,
            double textHeight,
            double extraGap,
            Vector2d spanUnit)
        {
            if (widthSource == null)
            {
                return false;
            }

            var requiredCenterClearance =
                EstimateDimensionTextNormalHalfExtent(dimText, textHeight, textHeight * 0.35, spanUnit) + extraGap;
            var actualClearance = DistanceToPolyline(widthSource, new Point3d(textPoint.X, textPoint.Y, 0.0));
            return actualClearance >= requiredCenterClearance;
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


        private static bool TryGetDimensionSpanGeometry(
            Polyline widthSource,
            Point2d spanTarget,
            out Point2d a2d,
            out Point2d b2d,
            out Point2d mid,
            out Vector2d spanUnit,
            out Vector2d normal)
        {
            a2d = default;
            b2d = default;
            mid = default;
            spanUnit = default;
            normal = default;

            if (widthSource == null)
            {
                return false;
            }

            var refinedTarget = spanTarget;
            if (GeometryUtils.TryGetCrossSectionMidpoint(widthSource, refinedTarget, out var midTarget, out _))
            {
                refinedTarget = midTarget;
            }

            if (!TryGetPerpendicularSpanAcrossDisposition(widthSource, refinedTarget, out a2d, out b2d))
            {
                return false;
            }

            var span = b2d - a2d;
            if (span.Length <= 1e-6)
            {
                return false;
            }

            spanUnit = span / span.Length;
            normal = new Vector2d(-spanUnit.Y, spanUnit.X);
            mid = new Point2d((a2d.X + b2d.X) * 0.5, (a2d.Y + b2d.Y) * 0.5);
            return true;
        }

        private IEnumerable<DimensionTextCandidate> GetCandidateDimensionTextPoints(
            Polyline quarter,
            Polyline widthSource,
            Point2d spanTarget,
            Point2d seedPoint,
            string labelText,
            LabelCollisionIndex? collisions,
            IReadOnlyCollection<DispositionInfo> allDispositions,
            DispositionInfo currentDisposition,
            double maxDistance)
        {
            if (!TryGetDimensionSpanGeometry(widthSource, spanTarget, out var a2d, out var b2d, out var mid, out var spanUnit, out var normal))
            {
                yield break;
            }

            var textHeight = ResolveLabelTextHeight(0.0);
            var dimText = ConvertLabelTextForDimension(labelText);
            var spanLength = a2d.GetDistanceTo(b2d);
            var pad = textHeight * 0.35;
            var edgeMargin = Math.Max(textHeight * 0.25, 1.0);
            var halfTextAlong = EstimateDimensionTextAlongHalfExtent(dimText, textHeight, pad, spanUnit);
            var alongStep = Math.Max(spanLength * 0.25, textHeight);
            var preferredAlong = (seedPoint - mid).DotProduct(spanUnit);
            var preferredOutsideAlong = WidthAlignedDimensionPlacementPolicy.GetPreferredOutsideAlongOffset(
                spanLength,
                halfTextAlong,
                edgeMargin);
            var alongOffsets = WidthAlignedDimensionPlacementPolicy.BuildSameLineAlongOffsets(
                spanLength,
                preferredAlong,
                halfTextAlong,
                edgeMargin,
                alongStep,
                expansionCount: 7);
            var normalOffsets = new[] { 0.0 };

            var ranked = new List<DimensionTextCandidate>();

            foreach (var normalOffset in normalOffsets)
            {
                foreach (var along in alongOffsets)
                {
                    var textPoint = new Point2d(
                        mid.X + spanUnit.X * along + normal.X * normalOffset,
                        mid.Y + spanUnit.Y * along + normal.Y * normalOffset);

                    if (!GeometryUtils.IsPointInsidePolyline(quarter, textPoint))
                    {
                        continue;
                    }

                    if (!IsWithinLeaderLength(mid, textPoint, maxDistance))
                    {
                        continue;
                    }

                    var quarterClearance = DistanceToPolyline(quarter, new Point3d(textPoint.X, textPoint.Y, 0.0));
                    if (quarterClearance < textHeight * 0.35)
                    {
                        continue;
                    }

                    var textBox = EstimateDimensionTextBox(
                        textPoint,
                        dimText,
                        textHeight,
                        spanUnit,
                        pad);

                    var overlapArea = collisions?.OverlapArea(textBox) ?? 0.0;
                    var crowdedness = collisions?.CountNearby(textBox, textHeight * 3.0) ?? 0;
                    var lineworkOverlap = CountIntersectingDispositionLinework(
                        textBox.ToExtents3d(),
                        allDispositions,
                        currentDisposition);
                    var sameLinePenalty = Math.Abs(normalOffset) > 1e-6 ? 1000000000.0 : 0.0;
                    var outsideAlong = Math.Abs(along);
                    var betweenArrowOverflow = Math.Abs(along) < (spanLength * 0.5)
                        ? Math.Max(0.0, halfTextAlong + edgeMargin - ((spanLength * 0.5) - Math.Abs(along)))
                        : 0.0;
                    var outsideGap = EstimateOutsideDimensionTextGap(along, spanLength, halfTextAlong);
                    var widthMatchPenalty = outsideGap > 0.0
                        ? Math.Abs(outsideGap - spanLength) * 5000.0
                        : 0.0;
                    var farOutsidePenalty = outsideGap > 0.0
                        ? Math.Max(0.0, outsideGap - (spanLength * 1.25)) * 15000.0
                        : 0.0;
                    var preferredOutsidePenalty = outsideGap > 0.0
                        ? Math.Abs(outsideAlong - preferredOutsideAlong) * 5000.0
                        : 0.0;
                    var alongShift = Math.Abs(along - preferredAlong);
                    var score =
                        sameLinePenalty +
                        overlapArea * 1000000.0 +
                        lineworkOverlap * 10000.0 +
                        crowdedness * 500.0 +
                        (betweenArrowOverflow * 100000.0) +
                        widthMatchPenalty +
                        preferredOutsidePenalty +
                        farOutsidePenalty +
                        alongShift;

                    ranked.Add(new DimensionTextCandidate(textPoint, 0.0, score));
                }
            }

            foreach (var item in ranked.OrderBy(x => x.Score))
            {
                yield return item;
            }
        }

        private static IEnumerable<Point2d> GetCandidateLabelPoints(
            Polyline quarter,
            Polyline disposition,
            Point2d target,
            bool allowOutsideDisposition,
            double step,
            int maxPoints,
            double measuredWidth,
            double maxLeaderLength,
            string labelText,
            LabelCollisionIndex? collisions,
            bool isLeaderOrPlainText)
        {
            var spiral = GeometryUtils.GetSpiralOffsets(target, step, maxPoints).ToList();
            double minDistance = step * 0.5;
            double minHalfWidth = measuredWidth * 0.25;
            double minQuarterClearance = step * 0.5;
            var effectiveTextHeight = step > 0.0 ? step : 10.0;
            var ranked = new List<(Point2d pt, double score, double overlap)>();

            foreach (var p in spiral)
            {
                if (!GeometryUtils.IsPointInsidePolyline(quarter, p))
                {
                    continue;
                }

                var p3d = new Point3d(p.X, p.Y, 0);
                var quarterClearance = DistanceToPolyline(quarter, p3d);
                var closest = disposition.GetClosestPointTo(p3d, false);
                var dispClearance = closest.DistanceTo(p3d);
                var insideDisposition = GeometryUtils.IsPointInsidePolyline(disposition, p);
                if (!insideDisposition && !allowOutsideDisposition)
                {
                    continue;
                }

                if (closest.DistanceTo(p3d) < Math.Max(minDistance, minHalfWidth) ||
                    quarterClearance < minQuarterClearance ||
                    !IsWithinLeaderLength(target, p, maxLeaderLength))
                {
                    continue;
                }

                var score = p.GetDistanceTo(target);
                score -= Math.Min(quarterClearance, dispClearance) * 0.10;
                if (!insideDisposition)
                {
                    score += effectiveTextHeight * 0.5;
                }

                var overlap = 0.0;
                if (isLeaderOrPlainText && collisions != null)
                {
                    var box = EstimateCenteredTextBox(
                        p,
                        labelText,
                        effectiveTextHeight,
                        effectiveTextHeight * 0.30);
                    overlap = collisions.OverlapArea(box);
                    score += overlap * 1000000.0;
                }

                ranked.Add((p, score, overlap));
            }

            var hasCleanCandidate = isLeaderOrPlainText &&
                                    collisions != null &&
                                    ranked.Any(c => c.overlap <= 1e-9);

            foreach (var item in ranked
                .OrderBy(c => hasCleanCandidate && c.overlap > 1e-9 ? 1 : 0)
                .ThenBy(c => c.overlap)
                .ThenBy(c => c.score))
            {
                yield return item.pt;
            }
        }

        private static List<Point2d> BuildQuarterFallbackCandidates(
            Polyline quarter,
            Polyline targetCorridor,
            Polyline disposition,
            Point2d searchTarget,
            Point2d measurementTarget,
            Point2d leaderTarget,
            Polyline? intersectionPiece)
        {
            var seeds = new List<Point2d>
            {
                searchTarget,
                measurementTarget,
                leaderTarget
            };

            if (intersectionPiece != null)
            {
                try
                {
                    seeds.Add(GeometryUtils.GetSafeInteriorPoint(intersectionPiece));
                }
                catch
                {
                    // best effort only
                }
            }

            try
            {
                if (GeometryUtils.TryFindPointInsideBoth(quarter, targetCorridor, out var overlapTarget))
                {
                    seeds.Add(overlapTarget);
                }
            }
            catch
            {
                // best effort only
            }

            if (!ReferenceEquals(targetCorridor, disposition))
            {
                // Tiny clipped intersection pieces can reject every fallback seed even
                // though the full disposition still has a real overlap inside the quarter.
                try
                {
                    if (GeometryUtils.TryFindPointInsideBoth(quarter, disposition, out var dispositionOverlapTarget))
                    {
                        seeds.Add(dispositionOverlapTarget);
                    }
                }
                catch
                {
                    // best effort only
                }
            }

            return BuildOrderedFallbackCandidates(
                seeds,
                candidate => GeometryUtils.IsPointInsidePolyline(quarter, candidate) &&
                             GeometryUtils.IsPointInsidePolyline(targetCorridor, candidate),
                !ReferenceEquals(targetCorridor, disposition)
                    ? candidate => GeometryUtils.IsPointInsidePolyline(quarter, candidate) &&
                                   GeometryUtils.IsPointInsidePolyline(disposition, candidate)
                    : null);
        }

        private static List<Point2d> BuildOrderedFallbackCandidates(
            IEnumerable<Point2d>? seedCandidates,
            Func<Point2d, bool> isUsable,
            Func<Point2d, bool>? isSecondaryUsable = null)
        {
            if (seedCandidates == null || isUsable == null)
            {
                return new List<Point2d>();
            }

            var selected = QuarterFallbackCandidateSelector.Select(
                seedCandidates.Select(candidate => new PlsrQuarterMatchPoint(candidate.X, candidate.Y)),
                candidate => isUsable(new Point2d(candidate.X, candidate.Y)),
                isSecondaryUsable == null
                    ? null
                    : candidate => isSecondaryUsable(new Point2d(candidate.X, candidate.Y)));

            return selected
                .Select(candidate => new Point2d(candidate.X, candidate.Y))
                .ToList();
        }

        private static double EstimateOutsideDimensionTextGap(
            double along,
            double spanLength,
            double halfTextAlong)
        {
            if (spanLength <= 1e-6)
            {
                return 0.0;
            }

            var gap = Math.Abs(along) - (spanLength * 0.5) - Math.Max(0.0, halfTextAlong);
            return gap > 0.0 ? gap : 0.0;
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
        private static double GetNearestVertexDistance(Polyline polyline, Point2d point)
        {
            if (polyline == null || polyline.NumberOfVertices <= 0)
            {
                return double.PositiveInfinity;
            }

            var best = double.PositiveInfinity;
            for (var i = 0; i < polyline.NumberOfVertices; i++)
            {
                var vertex = polyline.GetPoint2dAt(i);
                var distance = vertex.GetDistanceTo(point);
                if (distance < best)
                {
                    best = distance;
                }
            }

            return best;
        }

        private bool TryResolveLocalWidthMeasurementTarget(
            Polyline quarter,
            Polyline corridor,
            Point2d preferredInsidePoint,
            Point2d fallbackInsidePoint,
            double expectedWidth,
            out Point2d measurementTarget)
        {
            measurementTarget = preferredInsidePoint;
            if (quarter == null || corridor == null)
            {
                return false;
            }

            var bestPoint = measurementTarget;
            var bestScore = double.MaxValue;
            var haveBest = false;
            var preferredVertexClearance = Math.Max(
                expectedWidth > 1e-6 ? expectedWidth * 0.75 : 0.0,
                Math.Max(_config.TextHeight * 2.0, 8.0));

            void Consider(Point2d seed, double seedPenalty)
            {
                if (!GeometryUtils.IsPointInsidePolyline(quarter, seed) ||
                    !GeometryUtils.IsPointInsidePolyline(corridor, seed))
                {
                    return;
                }

                if (!GeometryUtils.TryGetCrossSectionMidpoint(corridor, seed, out var mid, out var width))
                {
                    return;
                }

                if (!GeometryUtils.IsPointInsidePolyline(quarter, mid) ||
                    !GeometryUtils.IsPointInsidePolyline(corridor, mid))
                {
                    return;
                }

                var widthPenalty = expectedWidth > 1e-6
                    ? Math.Abs(width - expectedWidth) * 20.0
                    : 0.0;
                var vertexClearance = GetNearestVertexDistance(corridor, mid);
                var bendPenalty = vertexClearance < preferredVertexClearance
                    ? (preferredVertexClearance - vertexClearance) * 60.0
                    : 0.0;
                var score = mid.GetDistanceTo(preferredInsidePoint) + widthPenalty + bendPenalty + seedPenalty;
                if (!haveBest || score < bestScore)
                {
                    bestScore = score;
                    bestPoint = mid;
                    haveBest = true;
                }
            }

            Consider(preferredInsidePoint, 0.0);
            Consider(fallbackInsidePoint, 5.0);

            try
            {
                var safeInterior = GeometryUtils.GetSafeInteriorPoint(corridor);
                Consider(safeInterior, 20.0);
            }
            catch
            {
                // ignore safe-point failures
            }

            double length;
            try
            {
                length = corridor.Length;
            }
            catch
            {
                length = 0.0;
            }

            if (length > 1e-6)
            {
                var sampleCount = Math.Max(_config.WidthSampleCount * 4, 48);
                const double probe = 0.5;
                for (var i = 1; i <= sampleCount; i++)
                {
                    var frac = (double)i / (sampleCount + 1);
                    var dist = frac * length;

                    double param;
                    Point3d onEdge;
                    try
                    {
                        param = corridor.GetParameterAtDistance(dist);
                        onEdge = corridor.GetPointAtParameter(param);
                    }
                    catch
                    {
                        continue;
                    }

                    Vector3d derivative;
                    try
                    {
                        derivative = corridor.GetFirstDerivative(param);
                    }
                    catch
                    {
                        continue;
                    }

                    var tangent = new Vector2d(derivative.X, derivative.Y);
                    if (tangent.Length <= 1e-6)
                    {
                        continue;
                    }

                    var normal = new Vector2d(-tangent.Y, tangent.X).GetNormal();
                    var plus = new Point2d(onEdge.X + normal.X * probe, onEdge.Y + normal.Y * probe);
                    var minus = new Point2d(onEdge.X - normal.X * probe, onEdge.Y - normal.Y * probe);

                    if (GeometryUtils.IsPointInsidePolyline(corridor, plus))
                    {
                        Consider(plus, 1.0);
                    }

                    if (GeometryUtils.IsPointInsidePolyline(corridor, minus))
                    {
                        Consider(minus, 1.0);
                    }
                }
            }

            if (!haveBest)
            {
                return false;
            }

            measurementTarget = bestPoint;
            return true;
        }
        private static bool ShouldSkipExistingQuarterLabel(
            Dictionary<string, HashSet<string>>? existingDispNumsByQuarter,
            string quarterKey,
            string normalizedDispNum,
            string normalizedReuseVariant,
            string dispositionReuseKey,
            out HashSet<string>? existingQuarterDispNums)
        {
            existingQuarterDispNums = null;
            if (existingDispNumsByQuarter == null ||
                string.IsNullOrWhiteSpace(quarterKey) ||
                string.IsNullOrWhiteSpace(normalizedDispNum))
            {
                return false;
            }

            existingDispNumsByQuarter.TryGetValue(quarterKey, out existingQuarterDispNums);
            if (existingQuarterDispNums == null)
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(normalizedReuseVariant)
                ? existingQuarterDispNums.Contains(normalizedDispNum)
                : existingQuarterDispNums.Contains(dispositionReuseKey);
        }

        private static void RegisterPlacedQuarterLabel(
            Dictionary<string, HashSet<string>>? existingDispNumsByQuarter,
            string quarterKey,
            string normalizedDispNum,
            string normalizedReuseVariant,
            string dispositionReuseKey)
        {
            if (existingDispNumsByQuarter == null ||
                string.IsNullOrWhiteSpace(quarterKey) ||
                string.IsNullOrWhiteSpace(normalizedDispNum))
            {
                return;
            }

            if (!existingDispNumsByQuarter.TryGetValue(quarterKey, out var existingQuarterDispNums) ||
                existingQuarterDispNums == null)
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

        private static double EstimateQuarterFootprintArea(
            QuarterInfo quarter,
            DispositionInfo disposition,
            Polyline? intersectionPiece)
        {
            if (intersectionPiece != null)
            {
                try
                {
                    return Math.Abs(intersectionPiece.Area);
                }
                catch
                {
                    // fall through
                }
            }

            if (TryGetOverlapExtents(
                    quarter.Bounds,
                    disposition.Bounds,
                    out var minX,
                    out var minY,
                    out var maxX,
                    out var maxY))
            {
                return Math.Abs((maxX - minX) * (maxY - minY));
            }

            return double.PositiveInfinity;
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
            IReadOnlyCollection<DispositionInfo> dispositions,
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








