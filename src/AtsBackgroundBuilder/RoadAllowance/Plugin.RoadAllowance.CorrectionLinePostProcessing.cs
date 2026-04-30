/////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AtsBackgroundBuilder.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AtsBackgroundBuilder
{
    public partial class Plugin
    {
        private const double CorrectionLinePostInsetMeters = 5.02;
        private const double CorrectionLinePostExpectedUsecWidthMeters = 30.18;

        private static void ApplyCorrectionLinePostBuildRules(
            Database database,
            IReadOnlyCollection<QuarterLabelInfo> sectionInfos,
            IEnumerable<ObjectId> requestedQuarterIds,
            bool drawLsds,
            Logger? logger)
        {
            if (database == null || sectionInfos == null || requestedQuarterIds == null)
            {
                return;
            }

            var requestedScopeIds = requestedQuarterIds
                .Where(id => !id.IsNull)
                .Distinct()
                .ToList();
            if (requestedScopeIds.Count == 0)
            {
                return;
            }
            var requestedScopeSet = new HashSet<ObjectId>(requestedScopeIds);

            var sectionById = new Dictionary<ObjectId, SectionKey>();
            foreach (var info in sectionInfos)
            {
                if (info == null || info.SectionPolylineId.IsNull)
                {
                    continue;
                }

                if (!sectionById.ContainsKey(info.SectionPolylineId))
                {
                    sectionById[info.SectionPolylineId] = info.SectionKey;
                }
            }

            if (sectionById.Count == 0)
            {
                logger?.WriteLine("CorrectionLine: skipped (no section ids available from section infos).");
                return;
            }

            var seamCount = 0;
            var surveyedCount = 0;
            var unsurveyedCount = 0;
            var relayeredOuter = 0;
            var createdOuter = 0;
            var relayeredInner = 0;
            var createdInner = 0;
            var correctionGeometryChanged = false;
            var matchedCorrectionSections = 0;
            var parseTownshipFailed = 0;
            var parseRangeFailed = 0;
            var parseSectionFailed = 0;
            var outerChangeSamples = new List<string>();
            var innerChangeSamples = new List<string>();
            var createdSamples = new List<string>();
            var resolvedSeams = new List<CorrectionSeam>();

            bool IsOnCorrectionCadence(int township, int anchorTownship)
            {
                var mod = (township - anchorTownship) % 4;
                if (mod < 0)
                {
                    mod += 4;
                }

                return mod == 0;
            }

            bool IsCorrectionOuterFallbackLayerName(string layer, bool preferSouthSide)
            {
                return CorrectionOuterFallbackLayerClassifier.IsCorrectionOuterFallbackLayer(layer, preferSouthSide);
            }

            bool IsCorrectionOuterBridgeLayerName(string layer)
            {
                return CorrectionOuterFallbackLayerClassifier.IsCorrectionOuterBridgeLayer(layer);
            }

            var sectionMetaById = new Dictionary<ObjectId, (SectionKey Key, int Township, int Range, int SectionNumber)>();
            foreach (var pair in sectionById)
            {
                var key = pair.Value;
                if (!TryParsePositiveToken(key.Township, out var townshipNumber))
                {
                    parseTownshipFailed++;
                    continue;
                }

                if (!TryParsePositiveToken(key.Range, out var rangeNumber))
                {
                    parseRangeFailed++;
                    continue;
                }

                var sectionNumber = ParseSectionNumber(key.Section);
                if (sectionNumber < 1 || sectionNumber > 36)
                {
                    parseSectionFailed++;
                    continue;
                }

                sectionMetaById[pair.Key] = (key, townshipNumber, rangeNumber, sectionNumber);
            }

            if (sectionMetaById.Count == 0)
            {
                logger?.WriteLine(
                    $"CorrectionLine: skipped (no parsable section metadata; townshipParseFailed={parseTownshipFailed}, rangeParseFailed={parseRangeFailed}, sectionParseFailed={parseSectionFailed}).");
                return;
            }

            // Correction cadence anchor is fixed at township 58 per section-building rules.
            // This yields correction lines every four townships (58, 62, 66 ... and 54, 50, 46 ...).
            const int selectedAnchorTownship = 58;
            var selectedAnchorMatches = 0;
            foreach (var meta in sectionMetaById.Values)
            {
                var isNorth = meta.SectionNumber >= 1 && meta.SectionNumber <= 6 &&
                              IsOnCorrectionCadence(meta.Township - 1, selectedAnchorTownship);
                var isSouth = meta.SectionNumber >= 31 && meta.SectionNumber <= 36 &&
                              IsOnCorrectionCadence(meta.Township, selectedAnchorTownship);
                if (isNorth || isSouth)
                {
                    selectedAnchorMatches++;
                }
            }

            var anchorSummary = new List<string> { $"{selectedAnchorTownship}:{selectedAnchorMatches}" };
            const bool useCadenceFilter = true;
            logger?.WriteLine(
                $"CorrectionLine: cadence anchor evaluation [{string.Join(", ", anchorSummary)}], selected={selectedAnchorTownship}, useCadenceFilter={useCadenceFilter}, sectionsInScope={sectionMetaById.Count}, parseTownshipFailed={parseTownshipFailed}, parseRangeFailed={parseRangeFailed}, parseSectionFailed={parseSectionFailed}.");
            if (selectedAnchorMatches == 0)
            {
                logger?.WriteLine("CorrectionLine: skipped (no cadence-aligned seam sections in scope).");
                return;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                const short correctionLayerColorIndex = 6; // magenta
                EnsureLayerWithColor(
                    database,
                    tr,
                    LayerUsecCorrection,
                    correctionLayerColorIndex);
                EnsureLayerWithColor(
                    database,
                    tr,
                    LayerUsecCorrectionZero,
                    correctionLayerColorIndex);
                logger?.WriteLine(
                    $"CorrectionLine: ensured layer colors {LayerUsecCorrection}/{LayerUsecCorrectionZero}=ACI {correctionLayerColorIndex} (magenta).");

                var seamAccumulators = new Dictionary<string, CorrectionSeamAccumulator>(StringComparer.OrdinalIgnoreCase);
                var seamInputSamples = new List<string>();
                foreach (var pair in sectionMetaById)
                {
                    if (!(tr.GetObject(pair.Key, OpenMode.ForRead, false) is Polyline section) || section.IsErased)
                    {
                        continue;
                    }

                    var sectionMeta = pair.Value;
                    var sectionKey = sectionMeta.Key;
                    var rangeNumber = sectionMeta.Range;
                    var townshipNumber = sectionMeta.Township;
                    var sectionNumber = sectionMeta.SectionNumber;
                    var isNorthSeamSection = sectionNumber >= 1 && sectionNumber <= 6;
                    var isSouthSeamSection = sectionNumber >= 31 && sectionNumber <= 36;
                    isNorthSeamSection = isNorthSeamSection &&
                                         IsOnCorrectionCadence(townshipNumber - 1, selectedAnchorTownship);
                    isSouthSeamSection = isSouthSeamSection &&
                                         IsOnCorrectionCadence(townshipNumber, selectedAnchorTownship);
                    if (!isNorthSeamSection && !isSouthSeamSection)
                    {
                        continue;
                    }
                    matchedCorrectionSections++;

                    var northTownship = isNorthSeamSection ? townshipNumber : townshipNumber + 1;
                    var seamKey = BuildCorrectionSeamKey(sectionKey.Zone, sectionKey.Meridian, rangeNumber, northTownship);
                    if (!seamAccumulators.TryGetValue(seamKey, out var accumulator))
                    {
                        accumulator = new CorrectionSeamAccumulator(
                            sectionKey.Zone,
                            NormalizeNumberToken(sectionKey.Meridian),
                            rangeNumber,
                            northTownship);
                        seamAccumulators.Add(seamKey, accumulator);
                    }

                    QuarterAnchors anchors;
                    if (!TryGetQuarterAnchors(section, out anchors))
                    {
                        anchors = GetFallbackAnchors(section);
                    }

                    Extents3d extents;
                    try
                    {
                        extents = section.GeometricExtents;
                    }
                    catch
                    {
                        continue;
                    }

                    var minX = Math.Min(extents.MinPoint.X, extents.MaxPoint.X);
                    var maxX = Math.Max(extents.MinPoint.X, extents.MaxPoint.X);
                    var boundaryAnchor = isNorthSeamSection ? anchors.Bottom : anchors.Top;
                    var usedEdgeTrendSample =
                        CorrectionBoundaryTrendSampling.TryBuildBoundarySampleAcrossXSpan(
                            new LineDistancePoint(boundaryAnchor.X, boundaryAnchor.Y),
                            new LineDistancePoint(anchors.Left.X, anchors.Left.Y),
                            new LineDistancePoint(anchors.Right.X, anchors.Right.Y),
                            minX,
                            maxX,
                            out var boundarySampleA,
                            out var boundarySampleB);
                    if (isNorthSeamSection)
                    {
                        if (usedEdgeTrendSample)
                        {
                            accumulator.AddNorthBoundary(
                                new Point2d(boundarySampleA.X, boundarySampleA.Y),
                                new Point2d(boundarySampleB.X, boundarySampleB.Y));
                        }
                        else
                        {
                            accumulator.AddNorthBoundary(boundaryAnchor, boundaryAnchor);
                        }
                    }
                    else
                    {
                        if (usedEdgeTrendSample)
                        {
                            accumulator.AddSouthBoundary(
                                new Point2d(boundarySampleA.X, boundarySampleA.Y),
                                new Point2d(boundarySampleB.X, boundarySampleB.Y));
                        }
                        else
                        {
                            accumulator.AddSouthBoundary(boundaryAnchor, boundaryAnchor);
                        }
                    }

                    if (seamInputSamples.Count < 64)
                    {
                        seamInputSamples.Add(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "seamKey={0} src={1}-{2}-{3}-{4}-{5} section={6} side={7} y={8:0.###} x=[{9:0.###},{10:0.###}]",
                                seamKey,
                                sectionKey.Zone,
                                NormalizeNumberToken(sectionKey.Meridian),
                                NormalizeNumberToken(sectionKey.Range),
                                NormalizeNumberToken(sectionKey.Township),
                                NormalizeNumberToken(sectionKey.Section),
                                sectionNumber,
                                isNorthSeamSection ? "NORTH" : "SOUTH",
                                boundaryAnchor.Y,
                                minX,
                                maxX) +
                            string.Format(
                                CultureInfo.InvariantCulture,
                                " anchor=({0:0.###},{1:0.###}) left=({2:0.###},{3:0.###}) right=({4:0.###},{5:0.###}) trendSample={6}",
                                boundaryAnchor.X,
                                boundaryAnchor.Y,
                                anchors.Left.X,
                                anchors.Left.Y,
                                anchors.Right.X,
                                anchors.Right.Y,
                                usedEdgeTrendSample));
                    }
                }

                var seams = new List<CorrectionSeam>();
                logger?.WriteLine(
                    $"CorrectionLine: seam accumulators={seamAccumulators.Count}, scopeQuarterIds={requestedScopeIds.Count}, sectionsInScope={sectionMetaById.Count}, correctionBandSections={matchedCorrectionSections}.");
                if (seamInputSamples.Count > 0)
                {
                    logger?.WriteLine($"CorrectionLine: seam input samples ({seamInputSamples.Count})");
                    for (var i = 0; i < seamInputSamples.Count; i++)
                    {
                        logger?.WriteLine("CorrectionLine:   " + seamInputSamples[i]);
                    }
                }

                foreach (var accumulator in seamAccumulators.Values)
                {
                    logger?.WriteLine(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "CorrectionLine: seam accumulator Z{0} M{1} R{2} T{3}/{4} northSamples={5}, southSamples={6}, x=[{7:0.###},{8:0.###}]",
                            accumulator.Zone,
                            accumulator.Meridian,
                            accumulator.Range,
                            accumulator.NorthTownship,
                            accumulator.NorthTownship - 1,
                            accumulator.NorthSampleCount,
                            accumulator.SouthSampleCount,
                            accumulator.MinX,
                            accumulator.MaxX));
                    if (accumulator.NorthSampleCount == 0 || accumulator.SouthSampleCount == 0)
                    {
                        logger?.WriteLine(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "CorrectionLine: seam accumulator Z{0} M{1} R{2} T{3}/{4} is one-sided (northSamples={5}, southSamples={6}); opposite side synthesized at width={7:0.##}m.",
                                accumulator.Zone,
                                accumulator.Meridian,
                                accumulator.Range,
                                accumulator.NorthTownship,
                                accumulator.NorthTownship - 1,
                                accumulator.NorthSampleCount,
                                accumulator.SouthSampleCount,
                                CorrectionLinePostExpectedUsecWidthMeters));
                    }

                    if (accumulator.TryBuild(out var seam))
                    {
                        seams.Add(seam);
                    }
                }

                if (seams.Count == 0)
                {
                    logger?.WriteLine(
                        $"CorrectionLine: no seam candidates resolved (sectionsInScope={sectionMetaById.Count}, correctionBandSections={matchedCorrectionSections}).");
                    tr.Commit();
                    return;
                }
                logger?.WriteLine(
                    $"CorrectionLine: resolved {seams.Count} seam candidate(s) from sectionsInScope={sectionMetaById.Count}, correctionBandSections={matchedCorrectionSections}.");
                resolvedSeams.AddRange(seams);

                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var segments = new List<CorrectionSegment>();
                var candidateLayerEntities = 0;
                var readableCandidateSegments = 0;
                var seamWindowHits = 0;
                foreach (ObjectId id in ms)
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    if (!IsCorrectionCandidateLayer(layer))
                    {
                        continue;
                    }

                    candidateLayerEntities++;

                    if (!TryReadOpenLinearSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    readableCandidateSegments++;

                    var segment = new CorrectionSegment(id, layer, a, b);
                    if (!IntersectsAnyCorrectionSeamWindow(segment, seams))
                    {
                        continue;
                    }

                    seamWindowHits++;
                    segments.Add(segment);
                }

                if (segments.Count == 0)
                {
                    logger?.WriteLine(
                        $"CorrectionLine: no candidate segments intersect resolved seam windows (candidateLayerEntities={candidateLayerEntities}, readableSegments={readableCandidateSegments}, seamWindowHits={seamWindowHits}, seams={seams.Count}).");
                    tr.Commit();
                    return;
                }

                void WriteTargetCorrectionTrace(string stage, CorrectionSeam seam, CorrectionSegment segment, string? note = null)
                {
                    static bool IntersectsTraceWindow(Point2d a, Point2d b, double minX, double minY, double maxX, double maxY)
                    {
                        var segMinX = Math.Min(a.X, b.X);
                        var segMaxX = Math.Max(a.X, b.X);
                        var segMinY = Math.Min(a.Y, b.Y);
                        var segMaxY = Math.Max(a.Y, b.Y);
                        return !(segMaxX < minX || segMinX > maxX || segMaxY < minY || segMinY > maxY);
                    }

                    var traceThisSegment =
                        IsTargetLayerTraceSegment(segment.A, segment.B) ||
                        IntersectsTraceWindow(segment.A, segment.B, 624100.0, 5836950.0, 625150.0, 5837105.0) ||
                        IntersectsTraceWindow(segment.A, segment.B, 632450.0, 5837240.0, 634250.0, 5837320.0);
                    if (logger == null || !traceThisSegment)
                    {
                        return;
                    }

                    var centerRange = seam.GetCenterSignedOffsetRange(segment);
                    var southRange = seam.GetSouthSignedOffsetRange(segment);
                    var northRange = seam.GetNorthSignedOffsetRange(segment);
                    logger.WriteLine(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "LAYER-TARGET corr stage={0} seam=Z{1} M{2} R{3} T{4}/{5} id={6} layer={7} A=({8:0.###},{9:0.###}) B=({10:0.###},{11:0.###}) centerMid={12:0.###} centerRange=[{13:0.###},{14:0.###}] southRange=[{15:0.###},{16:0.###}] northRange=[{17:0.###},{18:0.###}] note={19}",
                            stage,
                            seam.Zone,
                            seam.Meridian,
                            seam.Range,
                            seam.NorthTownship,
                            seam.NorthTownship - 1,
                            segment.Id.Handle.ToString(),
                            segment.Layer,
                            segment.A.X,
                            segment.A.Y,
                            segment.B.X,
                            segment.B.Y,
                            seam.GetCenterSignedOffset(segment.Mid),
                            centerRange.Min,
                            centerRange.Max,
                            southRange.Min,
                            southRange.Max,
                            northRange.Min,
                            northRange.Max,
                            note ?? string.Empty));
                }

                (bool FoundExisting, bool RelayeredExisting, bool CreatedNew) TryEnsureCorrectionInnerCompanion(
                    CorrectionSegment outer,
                    IReadOnlyList<CorrectionSegment> searchSegments,
                    Func<Point2d, double> getCenterSignedOffset,
                    bool allowCreateNew,
                    out CorrectionSegment companion,
                    IReadOnlyList<CorrectionSegment>? sideSearchSegments = null)
                {
                    companion = default;
                    bool TryResolveNearbyCorridorSide(
                        out int preferredOffsetSign)
                    {
                        preferredOffsetSign = 0;
                        var dir = outer.B - outer.A;
                        var len = dir.Length;
                        var corridorSideSegments = sideSearchSegments ?? searchSegments;
                        if (len <= 1e-6 || corridorSideSegments == null || corridorSideSegments.Count == 0)
                        {
                            return false;
                        }

                        var u = dir / len;
                        var positiveIntervals = new List<(double Min, double Max)>();
                        var negativeIntervals = new List<(double Min, double Max)>();
                        for (var si = 0; si < corridorSideSegments.Count; si++)
                        {
                            var candidate = corridorSideSegments[si];
                            if (candidate.Id == outer.Id || !candidate.IsHorizontalLike)
                            {
                                continue;
                            }

                            var candidateDir = candidate.B - candidate.A;
                            var candidateLen = candidateDir.Length;
                            if (candidateLen <= 1e-6 || Math.Abs((candidateDir / candidateLen).DotProduct(u)) < 0.985)
                            {
                                continue;
                            }

                            var overlap = GetCorrectionHorizontalOverlap(candidate, outer.MinX, outer.MaxX);
                            if (overlap < Math.Max(20.0, outer.Length * 0.20))
                            {
                                continue;
                            }

                            var signedOffset =
                                ((candidate.Mid.X - outer.A.X) * (outer.B.Y - outer.A.Y)) -
                                ((candidate.Mid.Y - outer.A.Y) * (outer.B.X - outer.A.X));
                            var sign = Math.Sign(signedOffset);
                            if (sign == 0)
                            {
                                continue;
                            }

                            var s0 = (candidate.A - outer.A).DotProduct(u);
                            var s1 = (candidate.B - outer.A).DotProduct(u);
                            var minS = Math.Max(0.0, Math.Min(s0, s1));
                            var maxS = Math.Min(len, Math.Max(s0, s1));
                            if (maxS <= minS)
                            {
                                continue;
                            }

                            if (sign > 0)
                            {
                                positiveIntervals.Add((minS, maxS));
                            }
                            else
                            {
                                negativeIntervals.Add((minS, maxS));
                            }
                        }

                        var positiveCoverage = GetMergedCoverageLength(positiveIntervals);
                        var negativeCoverage = GetMergedCoverageLength(negativeIntervals);
                        if (positiveCoverage <= 0.0 && negativeCoverage <= 0.0)
                        {
                            return false;
                        }

                        preferredOffsetSign = positiveCoverage >= negativeCoverage ? 1 : -1;
                        return true;
                    }

                    if (TryFindCorrectionInnerCompanion(
                        outer,
                        searchSegments,
                        CorrectionLinePostInsetMeters,
                        getCenterSignedOffset,
                        out var existingInner))
                    {
                        bool HasWrongSideExistingInner(
                            CorrectionSegment existing,
                            out int preferredOffsetSign)
                        {
                            preferredOffsetSign = 0;
                            if (!allowCreateNew ||
                                !string.Equals(existing.Layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) ||
                                !TryResolveNearbyCorridorSide(out preferredOffsetSign))
                            {
                                return false;
                            }

                            var cross =
                                ((existing.Mid.X - outer.A.X) * (outer.B.Y - outer.A.Y)) -
                                ((existing.Mid.Y - outer.A.Y) * (outer.B.X - outer.A.X));
                            var existingOffsetSign = Math.Sign(cross);
                            if (existingOffsetSign == 0 || existingOffsetSign == preferredOffsetSign)
                            {
                                return false;
                            }

                            var existingSignedOffset = DistancePointToInfiniteLine(existing.Mid, outer.A, outer.B);
                            return Math.Abs(existingSignedOffset - CorrectionLinePostInsetMeters) <= 1.25;
                        }

                        bool TryEraseWrongSideSyntheticInner(CorrectionSegment existing)
                        {
                            Entity? writable = null;
                            try
                            {
                                writable = tr.GetObject(existing.Id, OpenMode.ForWrite, false) as Entity;
                            }
                            catch (Autodesk.AutoCAD.Runtime.Exception)
                            {
                                return false;
                            }

                            if (writable == null || writable.IsErased)
                            {
                                return false;
                            }

                            if (!TryReadOpenLinearSegment(writable, out var existingA, out var existingB))
                            {
                                return false;
                            }

                            if (!existingA.IsEqualTo(existing.A, new Tolerance(0.25, 0.25)) ||
                                !existingB.IsEqualTo(existing.B, new Tolerance(0.25, 0.25)))
                            {
                                // Polyline order can be reversed; allow reversed match too.
                                if (!existingA.IsEqualTo(existing.B, new Tolerance(0.25, 0.25)) ||
                                    !existingB.IsEqualTo(existing.A, new Tolerance(0.25, 0.25)))
                                {
                                    return false;
                                }
                            }

                            writable.Erase();
                            segments.RemoveAll(s => s.Id == existing.Id);
                            return true;
                        }

                        if (HasWrongSideExistingInner(existingInner, out var preferredOffsetSign))
                        {
                            var direction = outer.B - outer.A;
                            var length = direction.Length;
                            if (length > 1e-6)
                            {
                                var normal = new Vector2d(direction.Y / length, -direction.X / length);
                                var offset = normal * (preferredOffsetSign > 0 ? CorrectionLinePostInsetMeters : -CorrectionLinePostInsetMeters);
                                var preferredA = outer.A + offset;
                                var preferredB = outer.B + offset;
                                if (!HasMatchingCorrectionSegment(segments, preferredA, preferredB))
                                {
                                    var line = new Line(
                                        new Point3d(preferredA.X, preferredA.Y, 0.0),
                                        new Point3d(preferredB.X, preferredB.Y, 0.0))
                                    {
                                        Layer = LayerUsecCorrectionZero,
                                        ColorIndex = 256
                                    };

                                    var preferredId = ms.AppendEntity(line);
                                    tr.AddNewlyCreatedDBObject(line, true);
                                    if (!preferredId.IsNull)
                                    {
                                        companion = new CorrectionSegment(preferredId, LayerUsecCorrectionZero, preferredA, preferredB);
                                        segments.Add(companion);
                                        TryEraseWrongSideSyntheticInner(existingInner);
                                        return (FoundExisting: false, RelayeredExisting: false, CreatedNew: true);
                                    }
                                }
                            }
                        }

                        if (string.Equals(existingInner.Layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase))
                        {
                            companion = existingInner;
                            return (FoundExisting: true, RelayeredExisting: false, CreatedNew: false);
                        }

                        if (TryRelayerAndTrackCorrectionSegment(
                                existingInner,
                                LayerUsecCorrectionZero,
                                out var relayeredInner))
                        {
                            companion = relayeredInner;
                            return (FoundExisting: true, RelayeredExisting: true, CreatedNew: false);
                        }

                        companion = existingInner;
                        return (FoundExisting: true, RelayeredExisting: false, CreatedNew: false);
                    }

                    if (!allowCreateNew)
                    {
                        return (FoundExisting: false, RelayeredExisting: false, CreatedNew: false);
                    }

                    if (TryResolveNearbyCorridorSide(out var preferredCreateOffsetSign))
                    {
                        var direction = outer.B - outer.A;
                        var length = direction.Length;
                        if (length > 1e-6)
                        {
                            var normal = new Vector2d(direction.Y / length, -direction.X / length);
                            var offset = normal * (preferredCreateOffsetSign > 0 ? CorrectionLinePostInsetMeters : -CorrectionLinePostInsetMeters);
                            var preferredA = outer.A + offset;
                            var preferredB = outer.B + offset;
                            if (!HasMatchingCorrectionSegment(segments, preferredA, preferredB))
                            {
                                var line = new Line(
                                    new Point3d(preferredA.X, preferredA.Y, 0.0),
                                    new Point3d(preferredB.X, preferredB.Y, 0.0))
                                {
                                    Layer = LayerUsecCorrectionZero,
                                    ColorIndex = 256
                                };

                                var preferredId = ms.AppendEntity(line);
                                tr.AddNewlyCreatedDBObject(line, true);
                                if (!preferredId.IsNull)
                                {
                                    companion = new CorrectionSegment(preferredId, LayerUsecCorrectionZero, preferredA, preferredB);
                                    segments.Add(companion);
                                    return (FoundExisting: false, RelayeredExisting: false, CreatedNew: true);
                                }
                            }
                        }
                    }

                    if (!TryCreateCorrectionInnerCompanion(
                        outer,
                        getCenterSignedOffset,
                        CorrectionLinePostInsetMeters,
                        segments,
                        ms,
                        tr,
                        out var newId,
                        out var newA,
                        out var newB))
                    {
                        return (FoundExisting: false, RelayeredExisting: false, CreatedNew: false);
                    }

                    companion = new CorrectionSegment(newId, LayerUsecCorrectionZero, newA, newB);
                    segments.Add(companion);
                    return (FoundExisting: false, RelayeredExisting: false, CreatedNew: true);
                }

                List<CorrectionSegment> CollectLiveHorizontalCorrectionSegments(string layerName)
                {
                    return CollectHorizontalCorrectionSegments(
                        tr,
                        ms,
                        layer => string.Equals(layer, layerName, StringComparison.OrdinalIgnoreCase));
                }

                void UpdateTrackedCorrectionSegment(CorrectionSegment updatedSegment)
                {
                    UpdateTrackedCorrectionSegmentLayer(segments, updatedSegment.Id, updatedSegment.Layer);
                }

                bool TryRelayerAndTrackCorrectionSegment(
                    CorrectionSegment segment,
                    string layerName,
                    out CorrectionSegment updatedSegment)
                {
                    updatedSegment = default;
                    if (!TryRelayerCorrectionSegment(tr, segment.Id, layerName))
                    {
                        return false;
                    }

                    updatedSegment = new CorrectionSegment(segment.Id, layerName, segment.A, segment.B);
                    UpdateTrackedCorrectionSegment(updatedSegment);
                    return true;
                }

                bool TryGetNormalizationCenterDistance(
                    CorrectionSegment segment,
                    CorrectionSeam seam,
                    out double centerDistance)
                {
                    centerDistance = double.NaN;
                    if (segment.MaxX < seam.MinX - 25.0 || segment.MinX > seam.MaxX + 25.0)
                    {
                        return false;
                    }

                    if (GetCorrectionHorizontalOverlap(segment, seam.MinX, seam.MaxX) < -30.0)
                    {
                        return false;
                    }

                    if (!seam.IntersectsExpandedStrip(segment, 14.0))
                    {
                        return false;
                    }

                    centerDistance = Math.Abs(seam.GetCenterSignedOffset(segment.Mid));
                    return true;
                }

                double GetMergedCoverageLength(List<(double Min, double Max)> intervals)
                {
                    if (intervals == null || intervals.Count == 0)
                    {
                        return 0.0;
                    }

                    var ordered = intervals
                        .Where(interval => interval.Max > interval.Min)
                        .OrderBy(interval => interval.Min)
                        .ToList();
                    if (ordered.Count == 0)
                    {
                        return 0.0;
                    }

                    var total = 0.0;
                    var currentMin = ordered[0].Min;
                    var currentMax = ordered[0].Max;
                    for (var oi = 1; oi < ordered.Count; oi++)
                    {
                        var interval = ordered[oi];
                        if (interval.Min <= currentMax + 0.50)
                        {
                            currentMax = Math.Max(currentMax, interval.Max);
                            continue;
                        }

                        total += Math.Max(0.0, currentMax - currentMin);
                        currentMin = interval.Min;
                        currentMax = interval.Max;
                    }

                    total += Math.Max(0.0, currentMax - currentMin);
                    return total;
                }

                foreach (var seam in seams)
                {
                    logger?.WriteLine(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "CorrectionLine: seam candidate Z{0} M{1} R{2} T{3}/{4} X=[{5:0.###},{6:0.###}] Y=[{7:0.###},{8:0.###}]",
                            seam.Zone,
                            seam.Meridian,
                            seam.Range,
                            seam.NorthTownship,
                            seam.NorthTownship - 1,
                            seam.MinX,
                            seam.MaxX,
                            seam.SouthY,
                            seam.NorthY));
                    seamCount++;

                    for (var ti = 0; ti < segments.Count; ti++)
                    {
                        var target = segments[ti];
                        WriteTargetCorrectionTrace("seam-scan", seam, target);
                    }

                    var verticalCandidates = segments
                        .Where(s => s.IsVerticalLike &&
                                    s.MidX >= seam.MinX - 10.0 &&
                                    s.MidX <= seam.MaxX + 10.0 &&
                                    s.MaxY >= seam.SouthY - 1.5 &&
                                    s.MinY <= seam.NorthY + 1.5)
                        .ToList();
                    var surveyedVerticalCandidates = verticalCandidates
                        .Where(s => IsCorrectionSurveyedLayer(s.Layer))
                        .ToList();
                    var usecVerticalCandidates = verticalCandidates
                        .Where(s => IsCorrectionUsecLayer(s.Layer))
                        .ToList();
                    var hasSurveyedVertical = surveyedVerticalCandidates.Count > 0;
                    var hasUsecVertical = usecVerticalCandidates.Count > 0;
                    var surveyedHorizontalBandTol = seam.IsOneSidedSynthesized ? 30.0 : 20.0;
                    var surveyedHorizontalEdgeTol = seam.IsOneSidedSynthesized ? 8.0 : 2.6;
                    var surveyedHorizontalXOnlyCandidates = segments
                        .Where(s => s.IsHorizontalLike &&
                                    IsCorrectionSurveyedLayer(s.Layer) &&
                                    s.MaxX >= seam.MinX - 25.0 &&
                                    s.MinX <= seam.MaxX + 25.0)
                        .ToList();
                    var surveyedHorizontalCandidates = segments
                        .Where(s => s.IsHorizontalLike &&
                                    IsCorrectionSurveyedLayer(s.Layer) &&
                                    s.MaxX >= seam.MinX - 25.0 &&
                                    s.MinX <= seam.MaxX + 25.0 &&
                                    seam.IntersectsExpandedStrip(s, surveyedHorizontalBandTol) &&
                                    GetCorrectionHorizontalOverlap(s, seam.MinX, seam.MaxX) >= 8.0)
                        .ToList();
                    var surveyedNorthEdgeHits = 0;
                    var surveyedSouthEdgeHits = 0;
                    var surveyedRelaxedEdgeHits = 0;
                    var surveyedNorthCoverageIntervals = new List<(double Min, double Max)>();
                    var surveyedSouthCoverageIntervals = new List<(double Min, double Max)>();
                    var surveyedRelaxedCoverageIntervals = new List<(double Min, double Max)>();
                    var nearestNorthEdgeDelta = double.MaxValue;
                    var nearestSouthEdgeDelta = double.MaxValue;
                    for (var hi = 0; hi < surveyedHorizontalCandidates.Count; hi++)
                    {
                        var h = surveyedHorizontalCandidates[hi];
                        var overlapMin = Math.Max(h.MinX, seam.MinX);
                        var overlapMax = Math.Min(h.MaxX, seam.MaxX);
                        var northDelta = Math.Abs(seam.GetNorthSignedOffset(h.Mid));
                        var southDelta = Math.Abs(seam.GetSouthSignedOffset(h.Mid));
                        if (northDelta < nearestNorthEdgeDelta)
                        {
                            nearestNorthEdgeDelta = northDelta;
                        }

                        if (southDelta < nearestSouthEdgeDelta)
                        {
                            nearestSouthEdgeDelta = southDelta;
                        }

                        if (northDelta <= surveyedHorizontalEdgeTol)
                        {
                            surveyedNorthEdgeHits++;
                            surveyedNorthCoverageIntervals.Add((overlapMin, overlapMax));
                        }

                        if (southDelta <= surveyedHorizontalEdgeTol)
                        {
                            surveyedSouthEdgeHits++;
                            surveyedSouthCoverageIntervals.Add((overlapMin, overlapMax));
                        }
                    }
                    if (seam.IsOneSidedSynthesized && surveyedNorthEdgeHits == 0 && surveyedSouthEdgeHits == 0)
                    {
                        for (var hi = 0; hi < surveyedHorizontalXOnlyCandidates.Count; hi++)
                        {
                            var h = surveyedHorizontalXOnlyCandidates[hi];
                            var overlapMin = Math.Max(h.MinX, seam.MinX);
                            var overlapMax = Math.Min(h.MaxX, seam.MaxX);
                            var northDelta = Math.Abs(seam.GetNorthSignedOffset(h.Mid));
                            var southDelta = Math.Abs(seam.GetSouthSignedOffset(h.Mid));
                            if (northDelta <= surveyedHorizontalEdgeTol || southDelta <= surveyedHorizontalEdgeTol)
                            {
                                surveyedRelaxedEdgeHits++;
                                surveyedRelaxedCoverageIntervals.Add((overlapMin, overlapMax));
                            }
                        }
                    }

                    var seamWidth = Math.Max(0.0, seam.MaxX - seam.MinX);
                    var surveyedNorthCoverage = GetMergedCoverageLength(surveyedNorthCoverageIntervals);
                    var surveyedSouthCoverage = GetMergedCoverageLength(surveyedSouthCoverageIntervals);
                    var surveyedRelaxedCoverage = GetMergedCoverageLength(surveyedRelaxedCoverageIntervals);
                    var requiredSurveyedCoverage = seamWidth * (seam.IsOneSidedSynthesized ? 0.75 : 0.80);
                    var hasSurveyedHorizontalBoundary = surveyedNorthCoverage >= requiredSurveyedCoverage ||
                                                       surveyedSouthCoverage >= requiredSurveyedCoverage ||
                                                       surveyedRelaxedCoverage >= requiredSurveyedCoverage;
                    var nearestNorthText = nearestNorthEdgeDelta == double.MaxValue
                        ? "n/a"
                        : nearestNorthEdgeDelta.ToString("0.###", CultureInfo.InvariantCulture);
                    var nearestSouthText = nearestSouthEdgeDelta == double.MaxValue
                        ? "n/a"
                        : nearestSouthEdgeDelta.ToString("0.###", CultureInfo.InvariantCulture);
                    logger?.WriteLine(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "CorrectionLine: seam evidence Z{0} M{1} R{2} T{3}/{4} oneSided={5} vertical(total={6}, surveyed={7}, usec={8}), horizontalSurveyed(xOnly={9}, seamBand={10}, northHits={11}, southHits={12}, relaxedHits={13}, northCoverage={14:0.#}, southCoverage={15:0.#}, relaxedCoverage={16:0.#}, requiredCoverage={17:0.#}, bandTol={18:0.#}, edgeTol={19:0.#}, nearestNorthDelta={20}, nearestSouthDelta={21}).",
                            seam.Zone,
                            seam.Meridian,
                            seam.Range,
                            seam.NorthTownship,
                            seam.NorthTownship - 1,
                            seam.IsOneSidedSynthesized,
                            verticalCandidates.Count,
                            surveyedVerticalCandidates.Count,
                            usecVerticalCandidates.Count,
                            surveyedHorizontalXOnlyCandidates.Count,
                            surveyedHorizontalCandidates.Count,
                            surveyedNorthEdgeHits,
                            surveyedSouthEdgeHits,
                            surveyedRelaxedEdgeHits,
                            surveyedNorthCoverage,
                            surveyedSouthCoverage,
                            surveyedRelaxedCoverage,
                            requiredSurveyedCoverage,
                            surveyedHorizontalBandTol,
                            surveyedHorizontalEdgeTol,
                            nearestNorthText,
                            nearestSouthText));

                    if (hasSurveyedHorizontalBoundary)
                    {
                        surveyedCount++;
                        logger?.WriteLine(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "CorrectionLine: seam Z{0} M{1} R{2} T{3}/{4} classified as L-SEC (surveyed evidence: vertical={5}, northHorizontal={6}, southHorizontal={7}, northCoverage={8:0.#}, southCoverage={9:0.#}, relaxedCoverage={10:0.#}, requiredCoverage={11:0.#}).",
                                seam.Zone,
                                seam.Meridian,
                                seam.Range,
                                seam.NorthTownship,
                                seam.NorthTownship - 1,
                                surveyedVerticalCandidates.Count,
                                surveyedNorthEdgeHits,
                                surveyedSouthEdgeHits,
                                surveyedNorthCoverage,
                                surveyedSouthCoverage,
                                surveyedRelaxedCoverage,
                                requiredSurveyedCoverage));
                        continue;
                    }

                    if (!hasUsecVertical)
                    {
                        logger?.WriteLine(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "CorrectionLine: seam Z{0} M{1} R{2} T{3}/{4} using no-vertical fallback (seam-band evidence only).",
                                seam.Zone,
                                seam.Meridian,
                                seam.Range,
                                seam.NorthTownship,
                                seam.NorthTownship - 1));
                    }

                    unsurveyedCount++;
                    const double seamEdgeExtensionTolerance = 30.0;
                    var horizontalCandidates = segments
                        .Where(s => s.IsHorizontalLike &&
                                    s.MaxX >= seam.MinX - 25.0 &&
                                    s.MinX <= seam.MaxX + 25.0 &&
                                    seam.IntersectsExpandedStrip(s, 12.0) &&
                                    // Keep seam-edge stubs that are just outside [MinX, MaxX].
                                    GetCorrectionHorizontalOverlap(s, seam.MinX, seam.MaxX) >= -seamEdgeExtensionTolerance)
                        .ToList();
                    if (horizontalCandidates.Count == 0)
                    {
                        logger?.WriteLine(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "CorrectionLine: seam Z{0} M{1} R{2} T{3}/{4} has no horizontal candidates in seam band.",
                                seam.Zone,
                                seam.Meridian,
                                seam.Range,
                                seam.NorthTownship,
                                seam.NorthTownship - 1));
                        continue;
                    }

                    var northOuter = SelectCorrectionHorizontalBand(
                        horizontalCandidates,
                        point => seam.GetNorthSignedOffset(point),
                        point => seam.GetCenterSignedOffset(point),
                        preferAboveCenter: true,
                        strictTol: 2.4);
                    var southOuter = SelectCorrectionHorizontalBand(
                        horizontalCandidates,
                        point => seam.GetSouthSignedOffset(point),
                        point => seam.GetCenterSignedOffset(point),
                        preferAboveCenter: false,
                        strictTol: 2.4);
                    if (northOuter.Count == 0 && southOuter.Count == 0)
                    {
                        logger?.WriteLine(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "CorrectionLine: seam Z{0} M{1} R{2} T{3}/{4} missing outer band candidate(s), north={5}, south={6}.",
                                seam.Zone,
                                seam.Meridian,
                                seam.Range,
                                seam.NorthTownship,
                                seam.NorthTownship - 1,
                                northOuter.Count,
                                southOuter.Count));
                        continue;
                    }

                    if (northOuter.Count == 0 || southOuter.Count == 0)
                    {
                        logger?.WriteLine(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "CorrectionLine: seam Z{0} M{1} R{2} T{3}/{4} one-sided outer band fallback, north={5}, south={6}.",
                                seam.Zone,
                                seam.Meridian,
                                seam.Range,
                                seam.NorthTownship,
                                seam.NorthTownship - 1,
                                northOuter.Count,
                                southOuter.Count));
                    }

                    var skippedInnerLayerOuter = 0;
                    var selectedOuters = northOuter.Count > 0 && southOuter.Count > 0
                        ? northOuter.Concat(southOuter)
                        : (northOuter.Count > 0 ? northOuter : southOuter);
                    var uniqueOuters = selectedOuters
                        .Where(s =>
                        {
                            var isInnerLayer = string.Equals(s.Layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase);
                            if (isInnerLayer)
                            {
                                skippedInnerLayerOuter++;
                            }

                            return !isInnerLayer;
                        })
                        .GroupBy(s => s.Id)
                        .Select(g => g.First())
                        .ToList();
                    if (uniqueOuters.Count == 0)
                    {
                        logger?.WriteLine(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "CorrectionLine: seam Z{0} M{1} R{2} T{3}/{4} no outer candidates after filtering (skippedInnerLayerOuter={5}).",
                                seam.Zone,
                                seam.Meridian,
                                seam.Range,
                                seam.NorthTownship,
                                seam.NorthTownship - 1,
                                skippedInnerLayerOuter));
                        continue;
                    }

                    var effectiveOuters = new List<(CorrectionSegment Outer, bool AllowSyntheticCompanion)>(uniqueOuters.Count);
                    foreach (var outer in uniqueOuters)
                    {
                        WriteTargetCorrectionTrace("outer-select", seam, outer, note: "selected");
                        if (!TryEnsureCorrectionOuterSegment(outer, segments, ms, tr, out var effectiveOuter, out var createdOuterClone))
                        {
                            continue;
                        }

                        effectiveOuters.Add((
                            effectiveOuter,
                            !string.Equals(outer.Layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase)));
                        if (createdOuterClone)
                        {
                            createdOuter++;
                            correctionGeometryChanged = true;
                            if (outerChangeSamples.Count < 16)
                            {
                                outerChangeSamples.Add(
                                    string.Format(
                                        CultureInfo.InvariantCulture,
                                        "clone id={0} A=({1:0.###},{2:0.###}) B=({3:0.###},{4:0.###}) -> {5}",
                                        outer.Id.Handle.ToString(),
                                        outer.A.X,
                                        outer.A.Y,
                                        outer.B.X,
                                        outer.B.Y,
                                        LayerUsecCorrection));
                            }
                        }
                    }

                    var companionNoOp = 0;
                    foreach (var outerEntry in effectiveOuters)
                    {
                        var outer = outerEntry.Outer;
                        var traceOuter = ShouldTraceCorrectionCandidate(outer.A, outer.B);
                        var companionResult = TryEnsureCorrectionInnerCompanion(
                            outer,
                            horizontalCandidates,
                            point => seam.GetCenterSignedOffset(point),
                            outerEntry.AllowSyntheticCompanion,
                            out var companion,
                            segments);
                        if (companionResult.FoundExisting)
                        {
                            if (companionResult.RelayeredExisting)
                            {
                                relayeredInner++;
                                correctionGeometryChanged = true;
                                if (innerChangeSamples.Count < 16)
                                {
                                    innerChangeSamples.Add(
                                        string.Format(
                                            CultureInfo.InvariantCulture,
                                            "id={0} A=({1:0.###},{2:0.###}) B=({3:0.###},{4:0.###}) -> {5}",
                                            companion.Id.Handle.ToString(),
                                            companion.A.X,
                                            companion.A.Y,
                                            companion.B.X,
                                            companion.B.Y,
                                            LayerUsecCorrectionZero));
                                }
                            }

                            if (traceOuter)
                            {
                                logger?.WriteLine(
                                    $"Cleanup: corr-companion trace outer id={outer.Id.Handle} seam=R{seam.Range} T{seam.NorthTownship}/{seam.NorthTownship - 1} " +
                                    $"result={(companionResult.RelayeredExisting ? "relayered-existing" : "existing")} " +
                                    $"companion={companion.Id.Handle} layer={companion.Layer} A=({companion.A.X:0.###},{companion.A.Y:0.###}) B=({companion.B.X:0.###},{companion.B.Y:0.###}).");
                            }

                            continue;
                        }

                        if (companionResult.CreatedNew)
                        {
                            createdInner++;
                            correctionGeometryChanged = true;
                            if (createdSamples.Count < 16)
                            {
                                createdSamples.Add(
                                    string.Format(
                                        CultureInfo.InvariantCulture,
                                        "id={0} A=({1:0.###},{2:0.###}) B=({3:0.###},{4:0.###}) layer={5}",
                                        companion.Id.Handle.ToString(),
                                        companion.A.X,
                                        companion.A.Y,
                                        companion.B.X,
                                        companion.B.Y,
                                        LayerUsecCorrectionZero));
                            }

                            if (traceOuter)
                            {
                                logger?.WriteLine(
                                    $"Cleanup: corr-companion trace outer id={outer.Id.Handle} seam=R{seam.Range} T{seam.NorthTownship}/{seam.NorthTownship - 1} " +
                                    $"result=created companion={companion.Id.Handle} layer={companion.Layer} " +
                                    $"A=({companion.A.X:0.###},{companion.A.Y:0.###}) B=({companion.B.X:0.###},{companion.B.Y:0.###}).");
                            }
                        }
                        else
                        {
                            companionNoOp++;
                            if (traceOuter)
                            {
                                logger?.WriteLine(
                                    $"Cleanup: corr-companion trace outer id={outer.Id.Handle} seam=R{seam.Range} T{seam.NorthTownship}/{seam.NorthTownship - 1} result=no-op.");
                            }
                        }
                    }

                    logger?.WriteLine(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "CorrectionLine: seam Z{0} M{1} R{2} T{3}/{4} outerCandidates={5} skippedInnerLayerOuter={6} companionNoOp={7}.",
                            seam.Zone,
                            seam.Meridian,
                            seam.Range,
                            seam.NorthTownship,
                            seam.NorthTownship - 1,
                            uniqueOuters.Count,
                            skippedInnerLayerOuter,
                            companionNoOp));
                }

                bool IsInnerBandForAnySeam(CorrectionSegment segment, CorrectionSeam excludedSeam, bool hasExcludedSeam)
                {
                    for (var si = 0; si < seams.Count; si++)
                    {
                        var candidateSeam = seams[si];
                        if (hasExcludedSeam &&
                            candidateSeam.Range == excludedSeam.Range &&
                            candidateSeam.NorthTownship == excludedSeam.NorthTownship &&
                            candidateSeam.Zone == excludedSeam.Zone &&
                            string.Equals(candidateSeam.Meridian, excludedSeam.Meridian, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!TryGetNormalizationCenterDistance(segment, candidateSeam, out var candidateCenterDistance))
                        {
                            continue;
                        }

                        if (CorrectionBandAxisClassifier.IsCloserToInnerBand(
                                candidateCenterDistance,
                                CorrectionLinePostExpectedUsecWidthMeters,
                                CorrectionLinePostInsetMeters))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                // Deterministic correction cleanup: any horizontal 20.12 segment that sits on a
                // resolved correction seam outer band is reclassified to correction.
                var postRelayeredTwenty = 0;
                var postRelayerSamples = new List<string>();
                var postSeen = new HashSet<ObjectId>();
                var pinnedLateCorrectionZeroIds = new HashSet<ObjectId>();
                var lateOuterCompanionCandidates = new List<(CorrectionSegment Outer, CorrectionSeam Seam)>();
                var latePinnedCompanionIds = new HashSet<ObjectId>();
                const double latePostOuterBandTol = 2.4;
                var latePostOuterExpected = CorrectionLinePostExpectedUsecWidthMeters * 0.5;
                for (var si = 0; si < seams.Count; si++)
                {
                    var seam = seams[si];
                    for (var i = 0; i < segments.Count; i++)
                    {
                        var seg = segments[i];
                        var centerOffset = seam.GetCenterSignedOffset(seg.Mid);
                        var centerDistance = Math.Abs(centerOffset);
                        var outerBandDelta = Math.Abs(centerDistance - latePostOuterExpected);
                        var preferSouthSide = centerOffset < 0.0;
                        var isFallbackLayer = IsCorrectionOuterFallbackLayerName(seg.Layer, preferSouthSide);
                        var preferCorrectionZeroFallback =
                            string.Equals(seg.Layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase);
                        var preferShortThirtyCorrectionZero =
                            string.Equals(seg.Layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) &&
                            seg.Length < 150.0;
                        WriteTargetCorrectionTrace(
                            "late-post-candidate",
                            seam,
                            seg,
                            note: string.Format(
                                CultureInfo.InvariantCulture,
                                "horizontal={0} southSide={1} fallbackLayer={2} preferCorrectionZero={3} preferShortThirtyCorrectionZero={4} centerDistance={5:0.###} outerBandDelta={6:0.###}",
                                seg.IsHorizontalLike,
                                preferSouthSide,
                                isFallbackLayer,
                                preferCorrectionZeroFallback,
                                preferShortThirtyCorrectionZero,
                                centerDistance,
                                outerBandDelta));
                        if (!seg.IsHorizontalLike || !isFallbackLayer)
                        {
                            continue;
                        }

                        if (seg.MaxX < seam.MinX - 25.0 || seg.MinX > seam.MaxX + 25.0)
                        {
                            continue;
                        }

                        if (GetCorrectionHorizontalOverlap(seg, seam.MinX, seam.MaxX) < -30.0)
                        {
                            continue;
                        }

                        if (!seam.IntersectsExpandedStrip(seg, 12.0))
                        {
                            continue;
                        }

                        if (outerBandDelta > latePostOuterBandTol)
                        {
                            continue;
                        }

                        if (!postSeen.Add(seg.Id))
                        {
                            continue;
                        }

                        var lateTargetLayer =
                            preferCorrectionZeroFallback || preferShortThirtyCorrectionZero
                                ? LayerUsecCorrectionZero
                                : LayerUsecCorrection;
                        var createdOuterClone = false;
                        CorrectionSegment effectiveOuter;
                        if (string.Equals(lateTargetLayer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!TryEnsureCorrectionOuterSegment(seg, segments, ms, tr, out effectiveOuter, out createdOuterClone))
                            {
                                continue;
                            }
                        }
                        else if (string.Equals(seg.Layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase))
                        {
                            effectiveOuter = seg;
                        }
                        else if (!TryRelayerAndTrackCorrectionSegment(
                                     seg,
                                     LayerUsecCorrectionZero,
                                     out effectiveOuter))
                        {
                            continue;
                        }

                        WriteTargetCorrectionTrace(
                            "late-post-relayer",
                            seam,
                            effectiveOuter,
                            note: string.Equals(lateTargetLayer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase)
                                ? "relayered-to-correction-zero"
                                : createdOuterClone
                                    ? "cloned-to-correction"
                                    : "already-correction");
                        var lateTargetIsCorrectionZero =
                            string.Equals(lateTargetLayer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase);
                        var lateTargetHasExistingZeroCompanion =
                            lateTargetIsCorrectionZero &&
                            HasTrackedInsetCompanion(
                                segments,
                                effectiveOuter.A,
                                effectiveOuter.B,
                                layer => string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase));
                        if (lateTargetIsCorrectionZero && !lateTargetHasExistingZeroCompanion)
                        {
                            pinnedLateCorrectionZeroIds.Add(effectiveOuter.Id);
                        }
                        postRelayeredTwenty++;
                        correctionGeometryChanged = true;
                        if (createdOuterClone)
                        {
                            createdOuter++;
                        }

                        if (!lateTargetIsCorrectionZero)
                        {
                            lateOuterCompanionCandidates.Add((effectiveOuter, seam));
                        }
                        if (postRelayerSamples.Count < 12)
                        {
                            postRelayerSamples.Add(
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "{0} id={1} A=({2:0.###},{3:0.###}) B=({4:0.###},{5:0.###}) -> {6}",
                                    createdOuterClone ? "clone" : "reuse",
                                    seg.Id.Handle.ToString(),
                                    seg.A.X,
                                    seg.A.Y,
                                    seg.B.X,
                                    seg.B.Y,
                                    lateTargetLayer));
                        }
                    }
                }

                if (postRelayeredTwenty > 0)
                {
                    logger?.WriteLine(
                        $"CorrectionLine: post-relayer converted {postRelayeredTwenty} seam-band ordinary usec segment(s) to {LayerUsecCorrection}.");
                    for (var i = 0; i < postRelayerSamples.Count; i++)
                    {
                        logger?.WriteLine("CorrectionLine:   " + postRelayerSamples[i]);
                    }
                }

                // Bridge cleanup for seam-edge misses: if a horizontal 20.12 segment is
                // connected on both endpoints to collinear correction outers, classify it as
                // correction as well.
                bool IsBridgeHorizontalLike(Point2d p0, Point2d p1)
                {
                    var dx = Math.Abs(p1.X - p0.X);
                    var dy = Math.Abs(p1.Y - p0.Y);
                    return dx >= (dy * 1.2);
                }

                var correctionOuterSegments = new List<(ObjectId Id, Point2d A, Point2d B, Vector2d U)>();
                foreach (ObjectId id in ms)
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased ||
                        !string.Equals(ent.Layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!TryReadOpenLinearSegment(ent, out var a, out var b) || !IsBridgeHorizontalLike(a, b))
                    {
                        continue;
                    }

                    var dir = b - a;
                    var len = dir.Length;
                    if (len <= 1e-6)
                    {
                        continue;
                    }

                    correctionOuterSegments.Add((id, a, b, dir / len));
                }

                bool EndpointTouchesCollinearCorrection(
                    Point2d endpoint,
                    Vector2d candidateU,
                    out ObjectId matchedId)
                {
                    matchedId = ObjectId.Null;
                    const double endpointTol = 4.0;
                    const double collinearDotTol = 0.985;
                    for (var i = 0; i < correctionOuterSegments.Count; i++)
                    {
                        var corr = correctionOuterSegments[i];
                        if (Math.Abs(candidateU.DotProduct(corr.U)) < collinearDotTol)
                        {
                            continue;
                        }

                        if (endpoint.GetDistanceTo(corr.A) <= endpointTol ||
                            endpoint.GetDistanceTo(corr.B) <= endpointTol)
                        {
                            matchedId = corr.Id;
                            return true;
                        }
                    }

                    return false;
                }

                static double DistancePointToSegment(Point2d p, Point2d a, Point2d b)
                {
                    var ab = b - a;
                    var abLen2 = ab.DotProduct(ab);
                    if (abLen2 <= 1e-9)
                    {
                        return p.GetDistanceTo(a);
                    }

                    var t = (p - a).DotProduct(ab) / abLen2;
                    t = Math.Max(0.0, Math.Min(1.0, t));
                    var proj = a + (ab * t);
                    return p.GetDistanceTo(proj);
                }

                var bridgeRelayered = 0;
                var bridgeSamples = new List<string>();
                var bridgeLoopGuard = 0;
                var bridgeChanged = true;
                while (bridgeChanged && bridgeLoopGuard++ < 4)
                {
                    bridgeChanged = false;
                    foreach (ObjectId id in ms)
                    {
                        Entity? ent = null;
                        try
                        {
                            ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception)
                        {
                            continue;
                        }

                        if (ent == null || ent.IsErased || !IsCorrectionOuterBridgeLayerName(ent.Layer))
                        {
                            continue;
                        }

                        if (!TryReadOpenLinearSegment(ent, out var a, out var b) || !IsBridgeHorizontalLike(a, b))
                        {
                            continue;
                        }

                        var dir = b - a;
                        var len = dir.Length;
                        if (len <= 1e-6)
                        {
                            continue;
                        }

                        var u = dir / len;
                        var startTouchesCorrection = EndpointTouchesCollinearCorrection(a, u, out var corrIdA);
                        var endTouchesCorrection = EndpointTouchesCollinearCorrection(b, u, out var corrIdB);
                        if (!CorrectionOuterBridgePropagationPolicy.ShouldRelayerBridgeSegment(
                                startTouchesCorrection,
                                endTouchesCorrection))
                        {
                            continue;
                        }

                        const double lateralTol = 2.5;
                        var mid = new Point2d(0.5 * (a.X + b.X), 0.5 * (a.Y + b.Y));
                        var midpointAligned = false;
                        if (startTouchesCorrection)
                        {
                            var corr = correctionOuterSegments.FirstOrDefault(s => s.Id == corrIdA);
                            if (!corr.Id.IsNull && DistancePointToSegment(mid, corr.A, corr.B) <= lateralTol)
                            {
                                midpointAligned = true;
                            }
                        }

                        if (!midpointAligned && endTouchesCorrection)
                        {
                            var corr = correctionOuterSegments.FirstOrDefault(s => s.Id == corrIdB);
                            if (!corr.Id.IsNull && DistancePointToSegment(mid, corr.A, corr.B) <= lateralTol)
                            {
                                midpointAligned = true;
                            }
                        }

                        if (!midpointAligned)
                        {
                            continue;
                        }

                        var bridgeSource = new CorrectionSegment(id, ent.Layer ?? string.Empty, a, b);
                        if (!TryEnsureCorrectionOuterSegment(bridgeSource, segments, ms, tr, out var effectiveOuter, out var createdOuterClone))
                        {
                            continue;
                        }

                        var effectiveDir = effectiveOuter.B - effectiveOuter.A;
                        var effectiveLen = effectiveDir.Length;
                        if (effectiveLen <= 1e-6)
                        {
                            continue;
                        }

                        correctionOuterSegments.Add((effectiveOuter.Id, effectiveOuter.A, effectiveOuter.B, effectiveDir / effectiveLen));
                        bridgeChanged = true;
                        bridgeRelayered++;
                        correctionGeometryChanged = true;
                        if (createdOuterClone)
                        {
                            createdOuter++;
                        }
                        if (bridgeSamples.Count < 12)
                        {
                            bridgeSamples.Add(
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "{0} id={1} A=({2:0.###},{3:0.###}) B=({4:0.###},{5:0.###}) -> {6}",
                                    createdOuterClone ? "clone" : "reuse",
                                    id.Handle.ToString(),
                                    a.X,
                                    a.Y,
                                    b.X,
                                    b.Y,
                                    LayerUsecCorrection));
                        }
                    }
                }

                if (bridgeRelayered > 0)
                {
                    logger?.WriteLine(
                        $"CorrectionLine: bridge-relayer converted {bridgeRelayered} collinear seam-link ordinary usec segment(s) to {LayerUsecCorrection}.");
                    for (var i = 0; i < bridgeSamples.Count; i++)
                    {
                        logger?.WriteLine("CorrectionLine:   " + bridgeSamples[i]);
                    }
                }

                // Late relayer companion pass: post-relayered seam segments were converted after
                // the main outer/inner pairing pass, so create/relayer their C-0 companions here.
                var lateCompanionRelayered = 0;
                var lateCompanionCreated = 0;
                var lateCompanionNoOp = 0;
                var lateCompanionSamples = new List<string>();
                for (var i = 0; i < lateOuterCompanionCandidates.Count; i++)
                {
                    var candidate = lateOuterCompanionCandidates[i];
                    var outer = candidate.Outer;
                    var seam = candidate.Seam;
                    var traceOuter = ShouldTraceCorrectionCandidate(outer.A, outer.B);
                    var companionResult = TryEnsureCorrectionInnerCompanion(
                        outer,
                        segments,
                        point => seam.GetCenterSignedOffset(point),
                        allowCreateNew: true,
                        out var companion);
                    if (companionResult.FoundExisting)
                    {
                        if (companionResult.RelayeredExisting)
                        {
                            relayeredInner++;
                            lateCompanionRelayered++;
                            correctionGeometryChanged = true;
                            if (lateCompanionSamples.Count < 10)
                            {
                                lateCompanionSamples.Add(
                                    string.Format(
                                        CultureInfo.InvariantCulture,
                                        "relayer id={0} A=({1:0.###},{2:0.###}) B=({3:0.###},{4:0.###}) -> {5}",
                                        companion.Id.Handle.ToString(),
                                        companion.A.X,
                                        companion.A.Y,
                                        companion.B.X,
                                        companion.B.Y,
                                        LayerUsecCorrectionZero));
                                }
                            }

                            if (traceOuter)
                            {
                                logger?.WriteLine(
                                    $"Cleanup: late-corr-companion trace outer id={outer.Id.Handle} seam=R{seam.Range} T{seam.NorthTownship}/{seam.NorthTownship - 1} " +
                                    $"result={(companionResult.RelayeredExisting ? "relayered-existing" : "existing")} " +
                                    $"companion={companion.Id.Handle} layer={companion.Layer} A=({companion.A.X:0.###},{companion.A.Y:0.###}) B=({companion.B.X:0.###},{companion.B.Y:0.###}).");
                            }

                            continue;
                        }

                    if (companionResult.CreatedNew)
                    {
                        createdInner++;
                        lateCompanionCreated++;
                        correctionGeometryChanged = true;
                        if (pinnedLateCorrectionZeroIds.Contains(outer.Id))
                        {
                            latePinnedCompanionIds.Add(companion.Id);
                        }
                        if (lateCompanionSamples.Count < 10)
                        {
                            lateCompanionSamples.Add(
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "create id={0} A=({1:0.###},{2:0.###}) B=({3:0.###},{4:0.###}) layer={5}",
                                    companion.Id.Handle.ToString(),
                                    companion.A.X,
                                    companion.A.Y,
                                    companion.B.X,
                                    companion.B.Y,
                                    LayerUsecCorrectionZero));
                        }

                        if (traceOuter)
                        {
                            logger?.WriteLine(
                                $"Cleanup: late-corr-companion trace outer id={outer.Id.Handle} seam=R{seam.Range} T{seam.NorthTownship}/{seam.NorthTownship - 1} " +
                                $"result=created companion={companion.Id.Handle} layer={companion.Layer} " +
                                $"A=({companion.A.X:0.###},{companion.A.Y:0.###}) B=({companion.B.X:0.###},{companion.B.Y:0.###}).");
                        }
                    }
                    else
                    {
                        lateCompanionNoOp++;
                        if (traceOuter)
                        {
                            logger?.WriteLine(
                                $"Cleanup: late-corr-companion trace outer id={outer.Id.Handle} seam=R{seam.Range} T{seam.NorthTownship}/{seam.NorthTownship - 1} result=no-op.");
                        }
                    }
                }
                if (lateCompanionRelayered > 0 || lateCompanionCreated > 0 || lateCompanionNoOp > 0)
                {
                    logger?.WriteLine(
                        $"CorrectionLine: late companion pass relayered={lateCompanionRelayered}, created={lateCompanionCreated}, noOp={lateCompanionNoOp}.");
                    for (var i = 0; i < lateCompanionSamples.Count; i++)
                    {
                        logger?.WriteLine("CorrectionLine:   " + lateCompanionSamples[i]);
                    }
                }

                // Guardrail: a seam can accumulate both inner and outer correction-zero bands after
                // multiple relayer/companion passes. Quarter/LSD selection must only see the inner
                // C-0 band; any outer-axis survivor is normalized back to correction outer here.
                var liveCorrectionZeroSegments = CollectLiveHorizontalCorrectionSegments(LayerUsecCorrectionZero);

                var normalizedOuterZero = 0;
                var normalizedOuterZeroCandidates = 0;
                var normalizedOuterZeroCompanionCreated = 0;
                var normalizedOuterZeroCompanionNoOp = 0;
                var erasedLatePinnedOuterZero = 0;
                var normalizedOuterZeroSamples = new List<string>();
                for (var si = 0; si < seams.Count; si++)
                {
                    var seam = seams[si];
                    for (var i = 0; i < liveCorrectionZeroSegments.Count; i++)
                    {
                        var seg = liveCorrectionZeroSegments[i];
                        if (pinnedLateCorrectionZeroIds.Contains(seg.Id))
                        {
                            if (ShouldTraceCorrectionCandidate(seg.A, seg.B))
                            {
                                logger?.WriteLine(
                                    $"Cleanup: corr-normalize outer-axis trace id={seg.Id.Handle} skipped=pinned-late-zero.");
                            }

                            continue;
                        }

                        if (!TryGetNormalizationCenterDistance(seg, seam, out var centerDistance))
                        {
                            continue;
                        }

                        normalizedOuterZeroCandidates++;
                        if (!CorrectionBandAxisClassifier.IsCloserToOuterBand(
                                centerDistance,
                                CorrectionLinePostExpectedUsecWidthMeters,
                                CorrectionLinePostInsetMeters))
                        {
                            continue;
                        }

                        if (IsInnerBandForAnySeam(seg, seam, hasExcludedSeam: true))
                        {
                            if (ShouldTraceCorrectionCandidate(seg.A, seg.B))
                            {
                                logger?.WriteLine(
                                    $"Cleanup: corr-normalize outer-axis trace id={seg.Id.Handle} seam=R{seam.Range} T{seam.NorthTownship}/{seam.NorthTownship - 1} " +
                                    $"centerDist={centerDistance:0.###} skipped=inner-band-on-other-seam A=({seg.A.X:0.###},{seg.A.Y:0.###}) B=({seg.B.X:0.###},{seg.B.Y:0.###}).");
                            }

                            continue;
                        }

                        if (latePinnedCompanionIds.Contains(seg.Id))
                        {
                            Entity? writablePinned = null;
                            try
                            {
                                writablePinned = tr.GetObject(seg.Id, OpenMode.ForWrite, false) as Entity;
                            }
                            catch (Autodesk.AutoCAD.Runtime.Exception)
                            {
                                continue;
                            }

                            if (writablePinned == null || writablePinned.IsErased)
                            {
                                continue;
                            }

                            writablePinned.Erase();
                            UpdateTrackedCorrectionSegmentLayer(segments, seg.Id, string.Empty);
                            erasedLatePinnedOuterZero++;
                            correctionGeometryChanged = true;
                            if (ShouldTraceCorrectionCandidate(seg.A, seg.B))
                            {
                                logger?.WriteLine(
                                    $"Cleanup: corr-normalize outer-axis trace id={seg.Id.Handle} seam=R{seam.Range} T{seam.NorthTownship}/{seam.NorthTownship - 1} " +
                                    $"centerDist={centerDistance:0.###} action=erase-late-pinned-companion A=({seg.A.X:0.###},{seg.A.Y:0.###}) B=({seg.B.X:0.###},{seg.B.Y:0.###}).");
                            }
                            continue;
                        }

                        if (HasTrackedInsetCompanion(
                                segments,
                                seg.A,
                                seg.B,
                                layer => IsCorrectionUsecLayer(layer) && !IsCorrectionLayer(layer)))
                        {
                            if (ShouldTraceCorrectionCandidate(seg.A, seg.B))
                            {
                                logger?.WriteLine(
                                    $"Cleanup: corr-normalize outer-axis trace id={seg.Id.Handle} seam=R{seam.Range} T{seam.NorthTownship}/{seam.NorthTownship - 1} " +
                                    $"centerDist={centerDistance:0.###} skipped=ordinary-inset-source A=({seg.A.X:0.###},{seg.A.Y:0.###}) B=({seg.B.X:0.###},{seg.B.Y:0.###}).");
                            }

                            continue;
                        }

                        if (!TryRelayerAndTrackCorrectionSegment(
                                seg,
                                LayerUsecCorrection,
                                out var normalizedOuter))
                        {
                            continue;
                        }

                        var companionResult = TryEnsureCorrectionInnerCompanion(
                            normalizedOuter,
                            segments,
                            point => seam.GetCenterSignedOffset(point),
                            allowCreateNew: true,
                            out _);
                        if (companionResult.FoundExisting)
                        {
                            if (companionResult.RelayeredExisting)
                            {
                                correctionGeometryChanged = true;
                            }

                            normalizedOuterZeroCompanionNoOp++;
                        }
                        else if (companionResult.CreatedNew)
                        {
                            normalizedOuterZeroCompanionCreated++;
                            correctionGeometryChanged = true;
                        }
                        else
                        {
                            normalizedOuterZeroCompanionNoOp++;
                        }

                        normalizedOuterZero++;
                        correctionGeometryChanged = true;
                        if (ShouldTraceCorrectionCandidate(seg.A, seg.B))
                        {
                            logger?.WriteLine(
                                $"Cleanup: corr-normalize outer-axis trace id={seg.Id.Handle} seam=R{seam.Range} T{seam.NorthTownship}/{seam.NorthTownship - 1} " +
                                $"centerDist={centerDistance:0.###} target={LayerUsecCorrection} A=({seg.A.X:0.###},{seg.A.Y:0.###}) B=({seg.B.X:0.###},{seg.B.Y:0.###}).");
                        }
                        if (normalizedOuterZeroSamples.Count < 12)
                        {
                            normalizedOuterZeroSamples.Add(
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "id={0} centerDist={1:0.###} A=({2:0.###},{3:0.###}) B=({4:0.###},{5:0.###}) -> {6}",
                                    seg.Id.Handle.ToString(),
                                    centerDistance,
                                    seg.A.X,
                                    seg.A.Y,
                                    seg.B.X,
                                    seg.B.Y,
                                    LayerUsecCorrection));
                        }
                    }
                }
                logger?.WriteLine(
                    $"CorrectionLine: outer-axis correction-zero normalization scanned {liveCorrectionZeroSegments.Count} live C-0 horizontal(s), seamCandidates={normalizedOuterZeroCandidates}, relayered={normalizedOuterZero}, erasedLatePinnedCompanions={erasedLatePinnedOuterZero}.");
                if (normalizedOuterZero > 0)
                {
                    logger?.WriteLine(
                        $"CorrectionLine: normalized {normalizedOuterZero} outer-axis correction-zero segment(s) back to {LayerUsecCorrection}.");
                    logger?.WriteLine(
                        $"CorrectionLine: outer-axis normalization companion pass created={normalizedOuterZeroCompanionCreated}, noOp={normalizedOuterZeroCompanionNoOp}.");
                    for (var i = 0; i < normalizedOuterZeroSamples.Count; i++)
                    {
                        logger?.WriteLine("CorrectionLine:   " + normalizedOuterZeroSamples[i]);
                    }
                }

                var liveCorrectionOuterSegments = CollectLiveHorizontalCorrectionSegments(LayerUsecCorrection);

                var normalizedInsetOuterCandidates = 0;
                var normalizedInsetOuter = 0;
                var normalizedInsetOuterSamples = new List<string>();
                var normalizedInsetOuterIds = new HashSet<ObjectId>();
                for (var si = 0; si < seams.Count; si++)
                {
                    var seam = seams[si];
                    for (var i = 0; i < liveCorrectionOuterSegments.Count; i++)
                    {
                        var seg = liveCorrectionOuterSegments[i];
                        if (normalizedInsetOuterIds.Contains(seg.Id))
                        {
                            continue;
                        }

                        if (!TryGetNormalizationCenterDistance(seg, seam, out var centerDistance))
                        {
                            continue;
                        }

                        normalizedInsetOuterCandidates++;
                        if (!CorrectionBandAxisClassifier.IsCloserToInnerBand(
                                centerDistance,
                                CorrectionLinePostExpectedUsecWidthMeters,
                                CorrectionLinePostInsetMeters))
                        {
                            continue;
                        }

                        if (!TryFindCorrectionOuterCompanion(
                                seg,
                                segments,
                                CorrectionLinePostInsetMeters,
                                point => seam.GetCenterSignedOffset(point),
                                out var outerCompanion))
                        {
                            continue;
                        }

                        if (!TryRelayerAndTrackCorrectionSegment(
                                seg,
                                LayerUsecCorrectionZero,
                                out var normalizedInner))
                        {
                            continue;
                        }

                        normalizedInsetOuterIds.Add(seg.Id);
                        correctionGeometryChanged = true;
                        normalizedInsetOuter++;
                        if (ShouldTraceCorrectionCandidate(seg.A, seg.B))
                        {
                            logger?.WriteLine(
                                $"Cleanup: corr-normalize inset-axis trace id={seg.Id.Handle} seam=R{seam.Range} T{seam.NorthTownship}/{seam.NorthTownship - 1} " +
                                $"centerDist={centerDistance:0.###} target={LayerUsecCorrectionZero} outer={outerCompanion.Id.Handle} " +
                                $"A=({seg.A.X:0.###},{seg.A.Y:0.###}) B=({seg.B.X:0.###},{seg.B.Y:0.###}).");
                        }

                        if (normalizedInsetOuterSamples.Count < 12)
                        {
                            normalizedInsetOuterSamples.Add(
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "id={0} centerDist={1:0.###} A=({2:0.###},{3:0.###}) B=({4:0.###},{5:0.###}) outer={6}",
                                    seg.Id.Handle.ToString(),
                                    centerDistance,
                                    seg.A.X,
                                    seg.A.Y,
                                    seg.B.X,
                                    seg.B.Y,
                                    outerCompanion.Id.Handle.ToString()));
                        }
                    }
                }

                logger?.WriteLine(
                    $"CorrectionLine: inset-axis correction-outer normalization scanned {liveCorrectionOuterSegments.Count} live C horizontal(s), seamCandidates={normalizedInsetOuterCandidates}, relayered={normalizedInsetOuter}.");
                if (normalizedInsetOuter > 0)
                {
                    logger?.WriteLine(
                        $"CorrectionLine: normalized {normalizedInsetOuter} inset-axis correction segment(s) to {LayerUsecCorrectionZero}.");
                    for (var i = 0; i < normalizedInsetOuterSamples.Count; i++)
                    {
                        logger?.WriteLine("CorrectionLine:   " + normalizedInsetOuterSamples[i]);
                    }
                }

                tr.Commit();
            }

            if (seamCount > 0)
            {
                logger?.WriteLine(
                    $"CorrectionLine: seams={seamCount}, surveyed={surveyedCount}, unsurveyed={unsurveyedCount}, relayerOuter={relayeredOuter}, createdOuter={createdOuter}, relayerInner={relayeredInner}, createdInner={createdInner}.");
                if (outerChangeSamples.Count > 0)
                {
                    logger?.WriteLine($"CorrectionLine: outer change samples ({outerChangeSamples.Count})");
                    for (var i = 0; i < outerChangeSamples.Count; i++)
                    {
                        logger?.WriteLine("CorrectionLine:   " + outerChangeSamples[i]);
                    }
                }

                if (innerChangeSamples.Count > 0)
                {
                    logger?.WriteLine($"CorrectionLine: inner relayer samples ({innerChangeSamples.Count})");
                    for (var i = 0; i < innerChangeSamples.Count; i++)
                    {
                        logger?.WriteLine("CorrectionLine:   " + innerChangeSamples[i]);
                    }
                }

                if (createdSamples.Count > 0)
                {
                    logger?.WriteLine($"CorrectionLine: created inner samples ({createdSamples.Count})");
                    for (var i = 0; i < createdSamples.Count; i++)
                    {
                        logger?.WriteLine("CorrectionLine:   " + createdSamples[i]);
                    }
                }
            }

            if (seamCount > 0)
            {
                TraceTargetLayerSegmentState(database, requestedScopeIds, "corr-post-before-inner-endpoint-snap", logger);
                var correctionEndpointAdjusted = ConnectCorrectionInnerEndpointsToVerticalUsecBoundaries(
                    database,
                    requestedScopeIds,
                    logger);
                TraceTargetLayerSegmentState(database, requestedScopeIds, "corr-post-after-inner-endpoint-snap", logger);
                if (correctionEndpointAdjusted)
                {
                    correctionGeometryChanged = true;
                }

                var correctionBandPruned = PruneRedundantCorrectionBandRows(
                    database,
                    resolvedSeams,
                    logger);
                TraceTargetLayerSegmentState(database, requestedScopeIds, "corr-post-after-redundant-band-prune", logger);
                if (correctionBandPruned)
                {
                    correctionGeometryChanged = true;
                }

            }

            if (!correctionGeometryChanged)
            {
                return;
            }

            // Rule 7/8: correction hard boundaries behave like USEC-0/2012 for 1/4 + LSD endpoints.
            EnforceQuarterLineEndpointsOnSectionBoundaries(database, requestedScopeIds, logger);
            TraceTargetLayerSegmentState(database, requestedScopeIds, "corr-post-after-quarter-hard", logger);
            EnforceBlindLineEndpointsOnSectionBoundaries(database, requestedScopeIds, logger);
            TraceTargetLayerSegmentState(database, requestedScopeIds, "corr-post-after-blind-hard", logger);
        }


        private static bool TrimOrdinaryUsecTieInOverhangsToVerticalBoundaries(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null)
            {
                return false;
            }

            var clipWindows = MergeOverlappingClipWindows(BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0));
            if (clipWindows.Count == 0)
            {
                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b) => DoesSegmentIntersectAnyWindowForCorrectionLinePost(a, b, clipWindows);

            bool TryReadOpenSegment(Entity ent, out Point2d a, out Point2d b) => TryReadOpenLinearSegment(ent, out a, out b);

            bool TryMoveEndpoint(Entity writable, bool moveStart, Point2d target, double moveTol) =>
                TryMoveEndpointForCorrectionLinePost(writable, moveStart, target, moveTol);

            bool TryIntersectInfiniteLines(Point2d a0, Point2d a1, Point2d b0, Point2d b1, out Point2d intersection) =>
                TryIntersectInfiniteLinesForPluginGeometry(a0, a1, b0, b1, out intersection);

            bool IsHorizontalLike(Point2d a, Point2d b) => IsHorizontalLikeForCorrectionLinePost(a, b);

            bool IsVerticalLike(Point2d a, Point2d b) => IsVerticalLikeForCorrectionLinePost(a, b);

            bool IsUsecZeroLikeLayer(string layer)
            {
                return string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-0", StringComparison.OrdinalIgnoreCase);
            }

            bool IsUsecTwentyLikeLayer(string layer)
            {
                return string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC2012", StringComparison.OrdinalIgnoreCase);
            }

            bool IsOrdinaryUsecTieInLayer(string layer) => IsUsecZeroLikeLayer(layer) || IsUsecTwentyLikeLayer(layer);

            bool IsOrdinaryUsecOuterLayer(string layer) => OrdinaryUsecTieInAnchorLayerClassifier.IsOuterUsecAnchorLayer(layer);

            bool IsOuterToOuterTieInTargetLayer(string sourceLayer, string targetLayer)
            {
                return IsOrdinaryUsecOuterLayer(sourceLayer) && IsOrdinaryUsecOuterLayer(targetLayer);
            }

            bool IsHardTieInBoundaryLayer(string layer)
            {
                return IsCorrectionLayer(layer) ||
                       string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-SEC-0", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase);
            }

            bool IsHardVerticalTieInTargetLayer(string layer)
            {
                return IsOrdinaryUsecTieInLayer(layer) || IsHardTieInBoundaryLayer(layer);
            }

            bool AreMatchingTieInBands(string sourceLayer, string targetLayer)
            {
                return (IsUsecZeroLikeLayer(sourceLayer) && IsUsecZeroLikeLayer(targetLayer)) ||
                       (IsUsecTwentyLikeLayer(sourceLayer) && IsUsecTwentyLikeLayer(targetLayer));
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var horizontalSources = new List<(ObjectId Id, string Layer, Point2d A, Point2d B)>();
                var verticalSources = new List<(ObjectId Id, string Layer, Point2d A, Point2d B)>();
                var thirtyHorizontalAnchors = new List<(ObjectId Id, string Layer, Point2d A, Point2d B)>();
                var thirtyVerticalAnchors = new List<(ObjectId Id, string Layer, Point2d A, Point2d B)>();
                var verticalTargets = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, double MinY, double MaxY, double AxisX)>();
                var horizontalTargets = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, double MinX, double MaxX, double AxisY)>();
                var horizontalAnchors = new List<(ObjectId Id, Point2d A, Point2d B)>();
                var verticalAnchors = new List<(ObjectId Id, Point2d A, Point2d B)>();
                var correctionZeroHorizontalTargets = new List<(LineDistancePoint A, LineDistancePoint B)>();

                void AddRelevantTieInSegment(ObjectId id, Entity ent)
                {
                    var layer = ent.Layer ?? string.Empty;
                    var isOuterUsecLayer = IsOrdinaryUsecOuterLayer(layer);
                    var isThirtyUsecLayer =
                        string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, "L-USEC3018", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, "L-USEC-3018", StringComparison.OrdinalIgnoreCase);
                    var isTrimSourceLayer = IsOrdinaryUsecTieInLayer(layer) || isOuterUsecLayer;

                    if (!TryReadOpenSegment(ent, out var a, out var b) || !DoesSegmentIntersectAnyWindow(a, b))
                    {
                        return;
                    }

                    if (isThirtyUsecLayer && IsHorizontalLike(a, b))
                    {
                        thirtyHorizontalAnchors.Add((id, layer, a, b));
                    }

                    if (isThirtyUsecLayer && IsVerticalLike(a, b))
                    {
                        thirtyVerticalAnchors.Add((id, layer, a, b));
                    }

                    if (!IsOrdinaryUsecTieInLayer(layer) &&
                        !IsHardVerticalTieInTargetLayer(layer) &&
                        !isOuterUsecLayer)
                    {
                        return;
                    }

                    // After correction rows exist, the visible outer L-USEC row must obey the
                    // same hard-boundary stop rules as its 0/20 companions. Keep treating it as
                    // an anchor too so existing connectivity checks stay intact.
                    if (isTrimSourceLayer && IsHorizontalLike(a, b))
                    {
                        horizontalSources.Add((id, layer, a, b));
                    }

                    if (isTrimSourceLayer && IsVerticalLike(a, b))
                    {
                        verticalSources.Add((id, layer, a, b));
                    }

                    if ((IsHardVerticalTieInTargetLayer(layer) || isOuterUsecLayer) && IsVerticalLike(a, b))
                    {
                        verticalTargets.Add((id, layer, a, b, Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y), 0.5 * (a.X + b.X)));
                    }

                    if ((IsHardVerticalTieInTargetLayer(layer) || isOuterUsecLayer) && IsHorizontalLike(a, b))
                    {
                        if (string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase))
                        {
                            correctionZeroHorizontalTargets.Add((
                                new LineDistancePoint(a.X, a.Y),
                                new LineDistancePoint(b.X, b.Y)));
                        }

                        horizontalTargets.Add((id, layer, a, b, Math.Min(a.X, b.X), Math.Max(a.X, b.X), 0.5 * (a.Y + b.Y)));
                    }

                    if (isOuterUsecLayer && IsHorizontalLike(a, b))
                    {
                        horizontalAnchors.Add((id, a, b));
                    }

                    if (isOuterUsecLayer && IsVerticalLike(a, b))
                    {
                        verticalAnchors.Add((id, a, b));
                    }
                }

                foreach (ObjectId id in ms)
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    AddRelevantTieInSegment(id, ent);
                }

                var hasHardTrimTargets =
                    verticalTargets.Exists(target => EndpointTouchesHardAnchorLayer(target.Layer)) ||
                    horizontalTargets.Exists(target => EndpointTouchesHardAnchorLayer(target.Layer));
                if (!hasHardTrimTargets)
                {
                    tr.Commit();
                    logger?.WriteLine("CorrectionLine: ordinary USEC tie-in overhang trim skipped (no live hard-target rows in buffered windows).");
                    return false;
                }

                List<(ObjectId Id, LineDistancePoint A, LineDistancePoint B)> CollectHorizontalGhostCandidates()
                {
                    var ghostCandidates = new List<(ObjectId Id, LineDistancePoint A, LineDistancePoint B)>();

                    void AddGhostCandidate(ObjectId id, Point2d a, Point2d b)
                    {
                        ghostCandidates.Add((
                            id,
                            new LineDistancePoint(a.X, a.Y),
                            new LineDistancePoint(b.X, b.Y)));
                    }

                    for (var i = 0; i < horizontalSources.Count; i++)
                    {
                        var source = horizontalSources[i];
                        AddGhostCandidate(source.Id, source.A, source.B);
                    }

                    for (var i = 0; i < horizontalAnchors.Count; i++)
                    {
                        var anchor = horizontalAnchors[i];
                        AddGhostCandidate(anchor.Id, anchor.A, anchor.B);
                    }

                    for (var i = 0; i < horizontalTargets.Count; i++)
                    {
                        var target = horizontalTargets[i];
                        if (!IsOrdinaryUsecTieInLayer(target.Layer) && !IsOrdinaryUsecOuterLayer(target.Layer))
                        {
                            continue;
                        }

                        AddGhostCandidate(target.Id, target.A, target.B);
                    }

                    return ghostCandidates;
                }

                void RemoveGhostHorizontalRows(IReadOnlyCollection<ObjectId> ghostHorizontalIds)
                {
                    var ghostIdSet = ghostHorizontalIds as HashSet<ObjectId> ?? new HashSet<ObjectId>(ghostHorizontalIds);
                    horizontalSources = horizontalSources
                        .Where(source => !ghostIdSet.Contains(source.Id))
                        .ToList();
                    horizontalAnchors = horizontalAnchors
                        .Where(anchor => !ghostIdSet.Contains(anchor.Id))
                        .ToList();
                    horizontalTargets = horizontalTargets
                        .Where(target => !ghostIdSet.Contains(target.Id))
                        .ToList();
                }

                int IgnoreGhostHorizontalRows()
                {
                    if (correctionZeroHorizontalTargets.Count == 0)
                    {
                        return 0;
                    }

                    var ghostCandidates = CollectHorizontalGhostCandidates();
                    var ghostHorizontalIds = CorrectionInsetGhostRowClassifier.FindGhostChainIds(
                        ghostCandidates,
                        correctionZeroHorizontalTargets,
                        CorrectionLinePostInsetMeters);
                    if (ghostHorizontalIds.Count == 0)
                    {
                        return 0;
                    }

                    RemoveGhostHorizontalRows(ghostHorizontalIds);
                    return ghostHorizontalIds.Count;
                }

                var rawHorizontalTargets = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, double MinX, double MaxX, double AxisY)>(horizontalTargets);
                var ghostHorizontalRowsIgnored = IgnoreGhostHorizontalRows();

                if ((horizontalSources.Count == 0 || verticalTargets.Count == 0) &&
                    (verticalSources.Count == 0 || horizontalTargets.Count == 0))
                {
                    tr.Commit();
                    logger?.WriteLine(
                        $"CorrectionLine: ordinary USEC tie-in overhang trim skipped (horizontalSources={horizontalSources.Count}, verticalSources={verticalSources.Count}, verticalTargets={verticalTargets.Count}, horizontalTargets={horizontalTargets.Count}).");
                    return false;
                }

                const double endpointMoveTol = 0.05;
                const double endpointTouchTol = 0.50;
                const double minMove = 0.05;
                const double maxTrim = 80.0;
                const double spanTol = 0.50;
                const double apparentEndpointGapTol = 40.0;
                const double outerBoundaryTol = 0.40;
                var trimmed = 0;
                var scanned = 0;
                var openTPairsAdjusted = 0;

                int GetTieInTargetPriority(string sourceLayer, string targetLayer)
                {
                    if (string.Equals(targetLayer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase))
                    {
                        return 0;
                    }

                    if (string.Equals(targetLayer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
                    {
                        return 1;
                    }

                    // Real road-allowance rows should stop on other road-allowance rows before
                    // falling back to generic section lines, or visible tie-ins can overshoot
                    // past the actual corridor stop.
                    if (AreMatchingTieInBands(sourceLayer, targetLayer) ||
                        IsOuterToOuterTieInTargetLayer(sourceLayer, targetLayer) ||
                        (IsOrdinaryUsecOuterLayer(sourceLayer) && IsOrdinaryUsecTieInLayer(targetLayer)))
                    {
                        return 2;
                    }

                    if (string.Equals(targetLayer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(targetLayer, "L-SEC-0", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(targetLayer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase))
                    {
                        return 3;
                    }

                    return 4;
                }

                bool EndpointTouchesHardAnchorLayer(string layer)
                {
                    return IsHardTieInBoundaryLayer(layer);
                }

                bool IsCompatibleEndpointTargetLayer(string sourceLayer, string targetLayer)
                {
                    return AreMatchingTieInBands(sourceLayer, targetLayer) ||
                           IsOuterToOuterTieInTargetLayer(sourceLayer, targetLayer) ||
                           EndpointTouchesHardAnchorLayer(targetLayer);
                }

                bool IsEligibleTieInTrimTargetLayer(string sourceLayer, string targetLayer)
                {
                    return IsCompatibleEndpointTargetLayer(sourceLayer, targetLayer) ||
                           IsOrdinaryUsecTieInLayer(targetLayer);
                }

                bool TryFindBestTieInTrimTarget<TTarget>(
                    Point2d a,
                    Point2d b,
                    string sourceLayer,
                    IReadOnlyList<TTarget> targets,
                    Func<TTarget, string> layerSelector,
                    Func<TTarget, Point2d> aSelector,
                    Func<TTarget, Point2d> bSelector,
                    bool canTrimStart,
                    bool canTrimEnd,
                    out bool bestMoveStart,
                    out Point2d bestTarget,
                    out string bestTargetLayer,
                    out Point2d bestTargetA,
                    out Point2d bestTargetB)
                {
                    var bestFound = false;
                    bestMoveStart = false;
                    bestTarget = default;
                    bestTargetLayer = string.Empty;
                    bestTargetA = default;
                    bestTargetB = default;
                    var bestMoveDistance = double.MaxValue;
                    var bestTargetPriority = int.MaxValue;
                    for (var ti = 0; ti < targets.Count; ti++)
                    {
                        var target = targets[ti];
                        var targetLayer = layerSelector(target);
                        if (!IsEligibleTieInTrimTargetLayer(sourceLayer, targetLayer))
                        {
                            continue;
                        }

                        var targetA = aSelector(target);
                        var targetB = bSelector(target);
                        if (!TryIntersectInfiniteLines(a, b, targetA, targetB, out var intersection))
                        {
                            continue;
                        }

                            var targetDistance = DistancePointToSegment(intersection, targetA, targetB);
                            var targetEndpointGap = Math.Min(
                                intersection.GetDistanceTo(targetA),
                                intersection.GetDistanceTo(targetB));
                            var targetApparent =
                                targetDistance > spanTol &&
                                targetEndpointGap <= apparentEndpointGapTol;
                            if (targetDistance > spanTol && !targetApparent)
                            {
                                continue;
                            }

                            var sourceDistance = DistancePointToSegment(intersection, a, b);
                            var sourceEndpointGap = Math.Min(
                                intersection.GetDistanceTo(a),
                                intersection.GetDistanceTo(b));
                            var sourceApparent =
                                sourceDistance > spanTol &&
                                sourceEndpointGap <= apparentEndpointGapTol;
                            // Allow short apparent endpoint intersections so a local open-T can
                            // close at the true corridor junction instead of stopping on the
                            // neighboring parallel row.
                            if (sourceDistance > spanTol && !sourceApparent)
                            {
                                continue;
                            }

                        var dStart = a.GetDistanceTo(intersection);
                        var dEnd = b.GetDistanceTo(intersection);
                        var moveStart = dStart <= dEnd;
                        if ((moveStart && !canTrimStart) || (!moveStart && !canTrimEnd))
                        {
                            continue;
                        }

                        var moveDistance = moveStart ? dStart : dEnd;
                        if (moveDistance <= minMove || moveDistance > maxTrim)
                        {
                            continue;
                        }

                        var targetPriority = GetTieInTargetPriority(sourceLayer, targetLayer);
                            var candidateScore = moveDistance +
                                (4.0 * sourceDistance) +
                                (4.0 * targetDistance) +
                                (sourceApparent ? 12.0 + sourceEndpointGap : 0.0) +
                                (targetApparent ? 12.0 + targetEndpointGap : 0.0);
                            if (!bestFound ||
                                targetPriority < bestTargetPriority ||
                                (targetPriority == bestTargetPriority && candidateScore < bestMoveDistance - 1e-6))
                        {
                            bestFound = true;
                            bestMoveStart = moveStart;
                            bestMoveDistance = candidateScore;
                            bestTargetPriority = targetPriority;
                            bestTarget = intersection;
                            bestTargetLayer = targetLayer;
                            bestTargetA = targetA;
                            bestTargetB = targetB;
                        }
                    }

                    return bestFound;
                }

                (bool Connected, bool HardConnected) GetEndpointAnchorState<TTarget, TAnchor>(
                    Point2d endpoint,
                    string sourceLayer,
                    ObjectId sourceId,
                    IReadOnlyList<(ObjectId Id, string Layer, Point2d A, Point2d B)> parallelSources,
                    IReadOnlyList<TTarget> perpendicularTargets,
                    Func<TTarget, string> targetLayerSelector,
                    Func<TTarget, Point2d> targetASelector,
                    Func<TTarget, Point2d> targetBSelector,
                    IReadOnlyList<TAnchor> perpendicularAnchors,
                    Func<TAnchor, Point2d> anchorASelector,
                    Func<TAnchor, Point2d> anchorBSelector)
                {
                    for (var i = 0; i < parallelSources.Count; i++)
                    {
                        var other = parallelSources[i];
                        if (other.Id == sourceId || !AreMatchingTieInBands(sourceLayer, other.Layer))
                        {
                            continue;
                        }

                        if (endpoint.GetDistanceTo(other.A) <= endpointTouchTol ||
                            endpoint.GetDistanceTo(other.B) <= endpointTouchTol)
                        {
                            return (true, false);
                        }
                    }

                    for (var i = 0; i < perpendicularTargets.Count; i++)
                    {
                        var target = perpendicularTargets[i];
                        var targetLayer = targetLayerSelector(target);
                        if (!IsCompatibleEndpointTargetLayer(sourceLayer, targetLayer))
                        {
                            continue;
                        }

                        if (DistancePointToSegment(endpoint, targetASelector(target), targetBSelector(target)) <= endpointTouchTol)
                        {
                            return (true, EndpointTouchesHardAnchorLayer(targetLayer));
                        }
                    }

                    for (var i = 0; i < perpendicularAnchors.Count; i++)
                    {
                        var anchor = perpendicularAnchors[i];
                        if (DistancePointToSegment(endpoint, anchorASelector(anchor), anchorBSelector(anchor)) <= endpointTouchTol)
                        {
                            return (true, false);
                        }
                    }

                    return (false, false);
                }

                (bool Connected, bool HardConnected) GetHorizontalEndpointAnchorState(Point2d endpoint, string sourceLayer, ObjectId sourceId)
                {
                    return GetEndpointAnchorState(
                        endpoint,
                        sourceLayer,
                        sourceId,
                        horizontalSources,
                        verticalTargets,
                        target => target.Layer,
                        target => target.A,
                        target => target.B,
                        verticalAnchors,
                        anchor => anchor.A,
                        anchor => anchor.B);
                }

                (bool Connected, bool HardConnected) GetVerticalEndpointAnchorState(Point2d endpoint, string sourceLayer, ObjectId sourceId)
                {
                    return GetEndpointAnchorState(
                        endpoint,
                        sourceLayer,
                        sourceId,
                        verticalSources,
                        horizontalTargets,
                        target => target.Layer,
                        target => target.A,
                        target => target.B,
                        horizontalAnchors,
                        anchor => anchor.A,
                        anchor => anchor.B);
                }

                bool TryGetRawNonHardHorizontalTieConnection(
                    Point2d endpoint,
                    string sourceLayer,
                    ObjectId sourceId,
                    out (ObjectId Id, string Layer, Point2d A, Point2d B, double MinX, double MaxX, double AxisY) rawTie)
                {
                    rawTie = default;
                    var found = false;
                    var bestAxisGap = double.MaxValue;
                    var bestEndpointGap = double.MaxValue;
                    for (var i = 0; i < rawHorizontalTargets.Count; i++)
                    {
                        var target = rawHorizontalTargets[i];
                        if (target.Id == sourceId || EndpointTouchesHardAnchorLayer(target.Layer))
                        {
                            continue;
                        }

                        if (!AreMatchingTieInBands(sourceLayer, target.Layer) &&
                            !IsOuterToOuterTieInTargetLayer(sourceLayer, target.Layer) &&
                            !(IsOrdinaryUsecOuterLayer(sourceLayer) && IsOrdinaryUsecTieInLayer(target.Layer)))
                        {
                            continue;
                        }

                        if (DistancePointToSegment(endpoint, target.A, target.B) <= endpointTouchTol)
                        {
                            var axisGap = Math.Abs(endpoint.Y - target.AxisY);
                            var endpointGap = Math.Min(
                                endpoint.GetDistanceTo(target.A),
                                endpoint.GetDistanceTo(target.B));
                            if (!found ||
                                axisGap < bestAxisGap - 1e-6 ||
                                (Math.Abs(axisGap - bestAxisGap) <= 1e-6 && endpointGap < bestEndpointGap - 1e-6))
                            {
                                rawTie = target;
                                found = true;
                                bestAxisGap = axisGap;
                                bestEndpointGap = endpointGap;
                            }
                        }
                    }

                    return found;
                }

                bool TryTrimTieInSource<TTarget>(
                    List<(ObjectId Id, string Layer, Point2d A, Point2d B)> sources,
                    int sourceIndex,
                    IReadOnlyList<TTarget> targets,
                    Func<Point2d, string, ObjectId, (bool Connected, bool HardConnected)> getEndpointAnchorState,
                    Func<Point2d, Point2d, bool> expectedOrientation,
                    Func<TTarget, string> layerSelector,
                    Func<TTarget, Point2d> aSelector,
                    Func<TTarget, Point2d> bSelector)
                {
                    var source = sources[sourceIndex];
                    if (!(tr.GetObject(source.Id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        return false;
                    }

                    if (!TryReadOpenSegment(writable, out var a, out var b) || !expectedOrientation(a, b))
                    {
                        return false;
                    }

                    sources[sourceIndex] = (source.Id, source.Layer, a, b);
                    var startAnchorState = getEndpointAnchorState(a, source.Layer, source.Id);
                    var endAnchorState = getEndpointAnchorState(b, source.Layer, source.Id);
                    var startHasRawHorizontalTie = false;
                    var endHasRawHorizontalTie = false;
                    var startRawHorizontalTie = default((ObjectId Id, string Layer, Point2d A, Point2d B, double MinX, double MaxX, double AxisY));
                    var endRawHorizontalTie = default((ObjectId Id, string Layer, Point2d A, Point2d B, double MinX, double MaxX, double AxisY));
                    if (IsVerticalLike(a, b))
                    {
                        startHasRawHorizontalTie = TryGetRawNonHardHorizontalTieConnection(a, source.Layer, source.Id, out startRawHorizontalTie);
                        endHasRawHorizontalTie = TryGetRawNonHardHorizontalTieConnection(b, source.Layer, source.Id, out endRawHorizontalTie);
                        if (!startAnchorState.Connected && startHasRawHorizontalTie)
                        {
                            startAnchorState = (true, false);
                        }

                        if (!endAnchorState.Connected && endHasRawHorizontalTie)
                        {
                            endAnchorState = (true, false);
                        }
                    }
                    var startOnBoundary = IsPointOnAnyWindowBoundaryForPlugin(a, outerBoundaryTol, clipWindows);
                    var endOnBoundary = IsPointOnAnyWindowBoundaryForPlugin(b, outerBoundaryTol, clipWindows);

                    bool HasInteriorCorrectionZeroTrimCandidate(Point2d endpoint)
                    {
                        if (!IsOrdinaryUsecOuterLayer(source.Layer))
                        {
                            return false;
                        }

                        for (var ti = 0; ti < targets.Count; ti++)
                        {
                            var targetLayer = layerSelector(targets[ti]);
                            if (!string.Equals(targetLayer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            var targetA = aSelector(targets[ti]);
                            var targetB = bSelector(targets[ti]);
                            if (!TryIntersectInfiniteLines(a, b, targetA, targetB, out var intersection))
                            {
                                continue;
                            }

                            var sourceDistance = DistancePointToSegment(intersection, a, b);
                            var sourceEndpointGap = Math.Min(
                                intersection.GetDistanceTo(a),
                                intersection.GetDistanceTo(b));
                            var sourceApparent =
                                sourceDistance > spanTol &&
                                sourceEndpointGap <= apparentEndpointGapTol;
                            if (sourceDistance > spanTol && !sourceApparent)
                            {
                                continue;
                            }

                            var targetDistance = DistancePointToSegment(intersection, targetA, targetB);
                            var targetEndpointGap = Math.Min(
                                intersection.GetDistanceTo(targetA),
                                intersection.GetDistanceTo(targetB));
                            var targetApparent =
                                targetDistance > spanTol &&
                                targetEndpointGap <= apparentEndpointGapTol;
                            if (targetDistance > spanTol && !targetApparent)
                            {
                                continue;
                            }

                            var moveDistance = endpoint.GetDistanceTo(intersection);
                            if (moveDistance <= minMove || moveDistance > maxTrim)
                            {
                                continue;
                            }

                            return true;
                        }

                        return false;
                    }

                    var startCorrectionZeroTrimCandidate =
                        !startOnBoundary &&
                        (endAnchorState.Connected || endOnBoundary) &&
                        HasInteriorCorrectionZeroTrimCandidate(a);
                    var endCorrectionZeroTrimCandidate =
                        !endOnBoundary &&
                        (startAnchorState.Connected || startOnBoundary) &&
                        HasInteriorCorrectionZeroTrimCandidate(b);
                    var canTrimStart =
                        startCorrectionZeroTrimCandidate ||
                        (!startAnchorState.HardConnected &&
                         !startOnBoundary &&
                         (endAnchorState.Connected || endOnBoundary));
                    var canTrimEnd =
                        endCorrectionZeroTrimCandidate ||
                        (!endAnchorState.HardConnected &&
                         !endOnBoundary &&
                         (startAnchorState.Connected || startOnBoundary));
                    if (!canTrimStart && !canTrimEnd)
                    {
                        return false;
                    }

                    if (canTrimStart)
                    {
                        scanned++;
                    }

                    if (canTrimEnd)
                    {
                        scanned++;
                    }

                    if (!TryFindBestTieInTrimTarget(
                            a,
                            b,
                            source.Layer,
                            targets,
                            layerSelector,
                            aSelector,
                            bSelector,
                            canTrimStart,
                            canTrimEnd,
                            out var bestMoveStart,
                            out var bestTarget,
                            out var bestTargetLayer,
                            out var bestTargetA,
                            out var bestTargetB))
                    {
                        return false;
                    }

                    bool IsSecTieInBoundaryLayer(string layer)
                    {
                        return string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(layer, "L-SEC-0", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(layer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase);
                    }

                    bool ShouldPreserveRawHorizontalTieInsteadOfSecPromotion(
                        Point2d movingEndpoint,
                        (ObjectId Id, string Layer, Point2d A, Point2d B, double MinX, double MaxX, double AxisY) rawTie)
                    {
                        if (!IsVerticalLike(a, b) ||
                            !IsOrdinaryUsecTieInLayer(source.Layer) ||
                            rawTie.Id.IsNull ||
                            !IsSecTieInBoundaryLayer(bestTargetLayer))
                        {
                            return false;
                        }

                        var moveDistance = movingEndpoint.GetDistanceTo(bestTarget);
                        if (Math.Abs(moveDistance - RoadAllowanceSecWidthMeters) > 2.0)
                        {
                            return false;
                        }

                        var targetGapFromRawTie = Math.Abs(DistancePointToInfiniteLine(bestTarget, rawTie.A, rawTie.B));
                        if (Math.Abs(targetGapFromRawTie - RoadAllowanceSecWidthMeters) > 2.0)
                        {
                            return false;
                        }

                        var endpointGapFromTarget = Math.Abs(DistancePointToInfiniteLine(movingEndpoint, bestTargetA, bestTargetB));
                        if (Math.Abs(endpointGapFromTarget - RoadAllowanceSecWidthMeters) > 2.0)
                        {
                            return false;
                        }

                        return true;
                    }

                    if (bestMoveStart &&
                        startHasRawHorizontalTie &&
                        ShouldPreserveRawHorizontalTieInsteadOfSecPromotion(a, startRawHorizontalTie))
                    {
                        return false;
                    }

                    if (!bestMoveStart &&
                        endHasRawHorizontalTie &&
                        ShouldPreserveRawHorizontalTieInsteadOfSecPromotion(b, endRawHorizontalTie))
                    {
                        return false;
                    }

                    if (!TryMoveEndpoint(writable, bestMoveStart, bestTarget, endpointMoveTol))
                    {
                        return false;
                    }

                    if (!TryReadOpenSegment(writable, out var newA, out var newB))
                    {
                        return false;
                    }

                    sources[sourceIndex] = (source.Id, source.Layer, newA, newB);
                    trimmed++;
                    return true;
                }

                bool TryResolveLocalTwentyOpenTPairs()
                {
                    const double localEndpointSeparationMax = 25.0;
                    var openTPairSamples = new List<string>();

                    bool EndpointTouchesAlternatePerpendicularLocalSource(
                        Point2d endpoint,
                        bool sourceIsHorizontal,
                        ObjectId sourceId,
                        ObjectId pairedSourceId)
                    {
                        var perpendicularSources = sourceIsHorizontal ? verticalSources : horizontalSources;
                        for (var pi = 0; pi < perpendicularSources.Count; pi++)
                        {
                            var other = perpendicularSources[pi];
                            if (other.Id == sourceId || other.Id == pairedSourceId)
                            {
                                continue;
                            }

                            if (!IsOrdinaryUsecTieInLayer(other.Layer) && !IsOrdinaryUsecOuterLayer(other.Layer))
                            {
                                continue;
                            }

                            if (endpoint.GetDistanceTo(other.A) <= endpointTouchTol ||
                                endpoint.GetDistanceTo(other.B) <= endpointTouchTol)
                            {
                                return true;
                            }
                        }

                        return false;
                    }

                    bool EndpointHasNearbyAlternatePerpendicularLocalEndpoint(
                        Point2d endpoint,
                        bool sourceIsHorizontal,
                        ObjectId sourceId,
                        ObjectId pairedSourceId)
                    {
                        var alternatePerpendicularEndpointProximityMax =
                            localEndpointSeparationMax + (RoadAllowanceSecWidthMeters * 0.5);
                        var perpendicularSources = sourceIsHorizontal ? verticalSources : horizontalSources;
                        for (var pi = 0; pi < perpendicularSources.Count; pi++)
                        {
                            var other = perpendicularSources[pi];
                            if (other.Id == sourceId || other.Id == pairedSourceId)
                            {
                                continue;
                            }

                            if (!IsOrdinaryUsecTieInLayer(other.Layer) && !IsOrdinaryUsecOuterLayer(other.Layer))
                            {
                                continue;
                            }

                            if (endpoint.GetDistanceTo(other.A) <= alternatePerpendicularEndpointProximityMax ||
                                endpoint.GetDistanceTo(other.B) <= alternatePerpendicularEndpointProximityMax)
                            {
                                return true;
                            }
                        }

                        return false;
                    }

                    bool EndpointTouchesAlternateOuterPerpendicularLocalSource(
                        Point2d endpoint,
                        bool sourceIsHorizontal,
                        ObjectId sourceId,
                        ObjectId pairedSourceId)
                    {
                        var perpendicularSources = sourceIsHorizontal ? verticalSources : horizontalSources;
                        for (var pi = 0; pi < perpendicularSources.Count; pi++)
                        {
                            var other = perpendicularSources[pi];
                            if (other.Id == sourceId || other.Id == pairedSourceId)
                            {
                                continue;
                            }

                            if (!IsOrdinaryUsecOuterLayer(other.Layer))
                            {
                                continue;
                            }

                            if (endpoint.GetDistanceTo(other.A) <= endpointTouchTol ||
                                endpoint.GetDistanceTo(other.B) <= endpointTouchTol)
                            {
                                return true;
                            }
                        }

                        return false;
                    }

                    bool EndpointTouchesAlternateThirtyPerpendicularLocalSource(
                        Point2d endpoint,
                        bool sourceIsHorizontal,
                        ObjectId sourceId,
                        ObjectId pairedSourceId)
                    {
                        var perpendicularSources = sourceIsHorizontal ? thirtyVerticalAnchors : thirtyHorizontalAnchors;
                        for (var pi = 0; pi < perpendicularSources.Count; pi++)
                        {
                            var other = perpendicularSources[pi];
                            if (other.Id == sourceId || other.Id == pairedSourceId)
                            {
                                continue;
                            }

                            if (DistancePointToSegment(endpoint, other.A, other.B) <= endpointTouchTol)
                            {
                                return true;
                            }
                        }

                        return false;
                    }

                    var pairCandidates = new List<(
                        int HorizontalIndex,
                        bool HorizontalMoveStart,
                        Point2d HorizontalEndpoint,
                        bool MoveHorizontal,
                        int VerticalIndex,
                        bool VerticalMoveStart,
                        Point2d VerticalEndpoint,
                        bool MoveVertical,
                        Point2d Target,
                        double Score)>();

                    for (var hi = 0; hi < horizontalSources.Count; hi++)
                    {
                        var horizontal = horizontalSources[hi];
                        if (!IsUsecTwentyLikeLayer(horizontal.Layer))
                        {
                            continue;
                        }

                        for (var vi = 0; vi < verticalSources.Count; vi++)
                        {
                            var vertical = verticalSources[vi];
                            if (!IsUsecTwentyLikeLayer(vertical.Layer) ||
                                !AreMatchingTieInBands(horizontal.Layer, vertical.Layer))
                            {
                                continue;
                            }

                            if (!TryIntersectInfiniteLines(horizontal.A, horizontal.B, vertical.A, vertical.B, out var intersection))
                            {
                                continue;
                            }

                            var horizontalDistance = DistancePointToSegment(intersection, horizontal.A, horizontal.B);
                            var horizontalEndpointGap = Math.Min(
                                intersection.GetDistanceTo(horizontal.A),
                                intersection.GetDistanceTo(horizontal.B));
                            var horizontalApparent =
                                horizontalDistance > spanTol &&
                                horizontalEndpointGap <= apparentEndpointGapTol;
                            if (horizontalDistance > spanTol && !horizontalApparent)
                            {
                                continue;
                            }

                            var verticalDistance = DistancePointToSegment(intersection, vertical.A, vertical.B);
                            var verticalEndpointGap = Math.Min(
                                intersection.GetDistanceTo(vertical.A),
                                intersection.GetDistanceTo(vertical.B));
                            var verticalApparent =
                                verticalDistance > spanTol &&
                                verticalEndpointGap <= apparentEndpointGapTol;
                            if (verticalDistance > spanTol && !verticalApparent)
                            {
                                continue;
                            }

                            if (!horizontalApparent && !verticalApparent)
                            {
                                continue;
                            }

                            var horizontalMoveStart = intersection.GetDistanceTo(horizontal.A) <= intersection.GetDistanceTo(horizontal.B);
                            var horizontalEndpoint = horizontalMoveStart ? horizontal.A : horizontal.B;
                            var horizontalMoveDistance = horizontalEndpoint.GetDistanceTo(intersection);
                            var horizontalNeedsMove = horizontalMoveDistance > minMove;
                            if (horizontalNeedsMove && horizontalMoveDistance > maxTrim)
                            {
                                continue;
                            }

                            var verticalMoveStart = intersection.GetDistanceTo(vertical.A) <= intersection.GetDistanceTo(vertical.B);
                            var verticalEndpoint = verticalMoveStart ? vertical.A : vertical.B;
                            var verticalMoveDistance = verticalEndpoint.GetDistanceTo(intersection);
                            var verticalNeedsMove = verticalMoveDistance > minMove;
                            if (verticalNeedsMove && verticalMoveDistance > maxTrim)
                            {
                                continue;
                            }

                            if (!horizontalNeedsMove && !verticalNeedsMove)
                            {
                                continue;
                            }

                            if (horizontalEndpoint.GetDistanceTo(verticalEndpoint) > localEndpointSeparationMax)
                            {
                                continue;
                            }

                            var horizontalAnchorState = GetHorizontalEndpointAnchorState(horizontalEndpoint, horizontal.Layer, horizontal.Id);
                            var verticalAnchorState = GetVerticalEndpointAnchorState(verticalEndpoint, vertical.Layer, vertical.Id);
                            if (horizontalAnchorState.HardConnected || verticalAnchorState.HardConnected)
                            {
                                continue;
                            }

                            if (IsPointOnAnyWindowBoundaryForPlugin(horizontalEndpoint, outerBoundaryTol, clipWindows) ||
                                IsPointOnAnyWindowBoundaryForPlugin(verticalEndpoint, outerBoundaryTol, clipWindows))
                            {
                                continue;
                            }

                            var horizontalHasAlternatePerpendicular =
                                EndpointTouchesAlternatePerpendicularLocalSource(horizontalEndpoint, sourceIsHorizontal: true, horizontal.Id, vertical.Id);
                            var verticalHasAlternatePerpendicular =
                                EndpointTouchesAlternatePerpendicularLocalSource(verticalEndpoint, sourceIsHorizontal: false, vertical.Id, horizontal.Id);
                            var horizontalTouchesOuterPerpendicular =
                                EndpointTouchesAlternateOuterPerpendicularLocalSource(horizontalEndpoint, sourceIsHorizontal: true, horizontal.Id, vertical.Id);
                            var verticalTouchesOuterPerpendicular =
                                EndpointTouchesAlternateOuterPerpendicularLocalSource(verticalEndpoint, sourceIsHorizontal: false, vertical.Id, horizontal.Id);
                            if (horizontalTouchesOuterPerpendicular || verticalTouchesOuterPerpendicular)
                            {
                                continue;
                            }

                            var horizontalTouchesThirtyPerpendicular =
                                EndpointTouchesAlternateThirtyPerpendicularLocalSource(horizontalEndpoint, sourceIsHorizontal: true, horizontal.Id, vertical.Id);
                            var verticalTouchesThirtyPerpendicular =
                                EndpointTouchesAlternateThirtyPerpendicularLocalSource(verticalEndpoint, sourceIsHorizontal: false, vertical.Id, horizontal.Id);
                            var oneSidedThirtyBridge =
                                horizontalNeedsMove != verticalNeedsMove &&
                                (horizontalTouchesThirtyPerpendicular || verticalTouchesThirtyPerpendicular) &&
                                !(horizontalNeedsMove && horizontalHasAlternatePerpendicular) &&
                                !(verticalNeedsMove && verticalHasAlternatePerpendicular) &&
                                ((horizontalNeedsMove && verticalMoveDistance <= endpointTouchTol) ||
                                 (verticalNeedsMove && horizontalMoveDistance <= endpointTouchTol));
                            if (horizontalNeedsMove != verticalNeedsMove && !oneSidedThirtyBridge)
                            {
                                continue;
                            }

                            if ((horizontalTouchesThirtyPerpendicular || verticalTouchesThirtyPerpendicular) &&
                                !oneSidedThirtyBridge)
                            {
                                continue;
                            }

                            if (horizontalHasAlternatePerpendicular && verticalHasAlternatePerpendicular)
                            {
                                continue;
                            }

                            var horizontalHasNearbyAlternatePerpendicular =
                                EndpointHasNearbyAlternatePerpendicularLocalEndpoint(horizontalEndpoint, sourceIsHorizontal: true, horizontal.Id, vertical.Id);
                            var verticalHasNearbyAlternatePerpendicular =
                                EndpointHasNearbyAlternatePerpendicularLocalEndpoint(verticalEndpoint, sourceIsHorizontal: false, vertical.Id, horizontal.Id);
                            var intersectionHorizontalAnchorState =
                                GetHorizontalEndpointAnchorState(intersection, horizontal.Layer, horizontal.Id);
                            var intersectionVerticalAnchorState =
                                GetVerticalEndpointAnchorState(intersection, vertical.Layer, vertical.Id);
                            if (horizontalHasNearbyAlternatePerpendicular && verticalHasNearbyAlternatePerpendicular)
                            {
                                var intersectionIsAnchored =
                                    intersectionHorizontalAnchorState.Connected ||
                                    intersectionHorizontalAnchorState.HardConnected ||
                                    intersectionVerticalAnchorState.Connected ||
                                    intersectionVerticalAnchorState.HardConnected;
                                if (!intersectionIsAnchored)
                                {
                                    continue;
                                }
                            }

                            // If a candidate target is only justified by the same side that already
                            // has another nearby perpendicular continuation, treat it as a false
                            // local open-T collapse and keep the split node instead.
                            var intersectionAnchoredByHorizontalOnly =
                                (intersectionHorizontalAnchorState.Connected ||
                                 intersectionHorizontalAnchorState.HardConnected) &&
                                !(intersectionVerticalAnchorState.Connected ||
                                  intersectionVerticalAnchorState.HardConnected);
                            var intersectionAnchoredByVerticalOnly =
                                (intersectionVerticalAnchorState.Connected ||
                                 intersectionVerticalAnchorState.HardConnected) &&
                                !(intersectionHorizontalAnchorState.Connected ||
                                  intersectionHorizontalAnchorState.HardConnected);
                            if (horizontalHasNearbyAlternatePerpendicular &&
                                !verticalHasNearbyAlternatePerpendicular &&
                                intersectionAnchoredByHorizontalOnly)
                            {
                                continue;
                            }

                            if (verticalHasNearbyAlternatePerpendicular &&
                                !horizontalHasNearbyAlternatePerpendicular &&
                                intersectionAnchoredByVerticalOnly)
                            {
                                continue;
                            }

                            var score =
                                horizontalEndpoint.GetDistanceTo(verticalEndpoint) +
                                horizontalMoveDistance +
                                verticalMoveDistance +
                                (horizontalApparent ? horizontalEndpointGap : 0.0) +
                                (verticalApparent ? verticalEndpointGap : 0.0);
                            pairCandidates.Add((
                                hi,
                                horizontalMoveStart,
                                horizontalEndpoint,
                                horizontalNeedsMove,
                                vi,
                                verticalMoveStart,
                                verticalEndpoint,
                                verticalNeedsMove,
                                intersection,
                                score));
                        }
                    }

                    if (pairCandidates.Count == 0)
                    {
                        return false;
                    }

                    var movedAny = false;
                    var usedHorizontal = new HashSet<int>();
                    var usedVertical = new HashSet<int>();
                    foreach (var pair in pairCandidates.OrderBy(candidate => candidate.Score))
                    {
                        if (!usedHorizontal.Add(pair.HorizontalIndex) || !usedVertical.Add(pair.VerticalIndex))
                        {
                            continue;
                        }

                        var horizontalSource = horizontalSources[pair.HorizontalIndex];
                        var verticalSource = verticalSources[pair.VerticalIndex];
                        if (!(tr.GetObject(horizontalSource.Id, OpenMode.ForWrite, false) is Entity horizontalWritable) ||
                            horizontalWritable.IsErased ||
                            !(tr.GetObject(verticalSource.Id, OpenMode.ForWrite, false) is Entity verticalWritable) ||
                            verticalWritable.IsErased)
                        {
                            continue;
                        }

                        var movedHorizontal =
                            !pair.MoveHorizontal ||
                            TryMoveEndpoint(horizontalWritable, pair.HorizontalMoveStart, pair.Target, endpointMoveTol);
                        var movedVertical =
                            !pair.MoveVertical ||
                            TryMoveEndpoint(verticalWritable, pair.VerticalMoveStart, pair.Target, endpointMoveTol);
                        if (!movedHorizontal || !movedVertical)
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(horizontalWritable, out var newHorizontalA, out var newHorizontalB) ||
                            !TryReadOpenSegment(verticalWritable, out var newVerticalA, out var newVerticalB))
                        {
                            continue;
                        }

                        horizontalSources[pair.HorizontalIndex] = (horizontalSource.Id, horizontalSource.Layer, newHorizontalA, newHorizontalB);
                        verticalSources[pair.VerticalIndex] = (verticalSource.Id, verticalSource.Layer, newVerticalA, newVerticalB);
                        trimmed += (pair.MoveHorizontal ? 1 : 0) + (pair.MoveVertical ? 1 : 0);
                        openTPairsAdjusted++;
                        movedAny = true;
                        if (openTPairSamples.Count < 80)
                        {
                            var movedHorizontalHasAlternatePerpendicular =
                                EndpointTouchesAlternatePerpendicularLocalSource(pair.HorizontalEndpoint, sourceIsHorizontal: true, horizontalSource.Id, verticalSource.Id);
                            var movedVerticalHasAlternatePerpendicular =
                                EndpointTouchesAlternatePerpendicularLocalSource(pair.VerticalEndpoint, sourceIsHorizontal: false, verticalSource.Id, horizontalSource.Id);
                            var movedHorizontalHasNearbyAlternatePerpendicular =
                                EndpointHasNearbyAlternatePerpendicularLocalEndpoint(pair.HorizontalEndpoint, sourceIsHorizontal: true, horizontalSource.Id, verticalSource.Id);
                            var movedVerticalHasNearbyAlternatePerpendicular =
                                EndpointHasNearbyAlternatePerpendicularLocalEndpoint(pair.VerticalEndpoint, sourceIsHorizontal: false, verticalSource.Id, horizontalSource.Id);
                            var movedIntersectionHorizontalAnchorState =
                                GetHorizontalEndpointAnchorState(pair.Target, horizontalSource.Layer, horizontalSource.Id);
                            var movedIntersectionVerticalAnchorState =
                                GetVerticalEndpointAnchorState(pair.Target, verticalSource.Layer, verticalSource.Id);
                            openTPairSamples.Add(
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "h={0}/{1} ({2:0.###},{3:0.###})->({4:0.###},{5:0.###}) v={6}/{7} ({8:0.###},{9:0.###})->({10:0.###},{11:0.###}) target=({12:0.###},{13:0.###})",
                                    horizontalSource.Id.Handle.ToString(),
                                    horizontalSource.Layer,
                                    horizontalSource.A.X,
                                    horizontalSource.A.Y,
                                    horizontalSource.B.X,
                                    horizontalSource.B.Y,
                                    verticalSource.Id.Handle.ToString(),
                                    verticalSource.Layer,
                                    verticalSource.A.X,
                                    verticalSource.A.Y,
                                    verticalSource.B.X,
                                    verticalSource.B.Y,
                                    pair.Target.X,
                                    pair.Target.Y) +
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    " hexact={0} vexact={1} hnear={2} vnear={3} htargetConnected={4}/{5} vtargetConnected={6}/{7}",
                                    movedHorizontalHasAlternatePerpendicular,
                                    movedVerticalHasAlternatePerpendicular,
                                    movedHorizontalHasNearbyAlternatePerpendicular,
                                    movedVerticalHasNearbyAlternatePerpendicular,
                                    movedIntersectionHorizontalAnchorState.Connected,
                                    movedIntersectionHorizontalAnchorState.HardConnected,
                                    movedIntersectionVerticalAnchorState.Connected,
                                    movedIntersectionVerticalAnchorState.HardConnected));
                        }
                    }

                    if (openTPairSamples.Count > 0)
                    {
                        logger?.WriteLine($"Cleanup: local 20 open-T adjusted {openTPairSamples.Count} sampled pair(s) (totalMoves={openTPairsAdjusted}).");
                        for (var sampleIndex = 0; sampleIndex < openTPairSamples.Count; sampleIndex++)
                        {
                            logger?.WriteLine("Cleanup:   open-T " + openTPairSamples[sampleIndex]);
                        }
                    }

                    return movedAny;
                }

                for (var iteration = 0; iteration < 4; iteration++)
                {
                    var movedAny = false;
                    for (var si = 0; si < horizontalSources.Count; si++)
                    {
                        if (TryTrimTieInSource(
                                horizontalSources,
                                si,
                                verticalTargets,
                                GetHorizontalEndpointAnchorState,
                                IsHorizontalLike,
                                target => target.Layer,
                                target => target.A,
                                target => target.B))
                        {
                            movedAny = true;
                        }
                    }

                    for (var si = 0; si < verticalSources.Count; si++)
                    {
                        if (TryTrimTieInSource(
                                verticalSources,
                                si,
                                horizontalTargets,
                                GetVerticalEndpointAnchorState,
                                IsVerticalLike,
                                target => target.Layer,
                                target => target.A,
                                target => target.B))
                        {
                            movedAny = true;
                        }
                    }

                    if (TryResolveLocalTwentyOpenTPairs())
                    {
                        movedAny = true;
                    }

                    if (!movedAny)
                    {
                        break;
                    }
                }

                tr.Commit();
                logger?.WriteLine($"CorrectionLine: ordinary USEC tie-in overhang trim scanned={scanned}, trimmed={trimmed}, horizontalSources={horizontalSources.Count}, verticalSources={verticalSources.Count}, verticalTargets={verticalTargets.Count}, horizontalTargets={horizontalTargets.Count}, horizontalAnchors={horizontalAnchors.Count}, verticalAnchors={verticalAnchors.Count}, ghostHorizontalRowsIgnored={ghostHorizontalRowsIgnored}, openTPairsAdjusted={openTPairsAdjusted}.");
                return trimmed > 0;
            }
        }

        private static bool TrimOuterUsecOvershootToCorrectionZeroBoundaries(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null)
            {
                return false;
            }

            var coreClipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 0.0);
            var bufferedClipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0);
            var clipWindows = MergeOverlappingClipWindows(bufferedClipWindows);
            if (clipWindows.Count == 0)
            {
                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b) => DoesSegmentIntersectAnyWindowForCorrectionLinePost(a, b, clipWindows);
            bool TryReadOpenSegment(Entity ent, out Point2d a, out Point2d b) => TryReadOpenLinearSegment(ent, out a, out b);
            bool TryMoveEndpoint(Entity writable, bool moveStart, Point2d target, double moveTol) =>
                TryMoveEndpointForCorrectionLinePost(writable, moveStart, target, moveTol);
            bool TryIntersectInfiniteLines(Point2d a0, Point2d a1, Point2d b0, Point2d b1, out Point2d intersection) =>
                TryIntersectInfiniteLinesForPluginGeometry(a0, a1, b0, b1, out intersection);
            bool IsBaseUsecLayer(string layer) =>
                string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layer, "L-USEC", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layer, "L-USEC-0", StringComparison.OrdinalIgnoreCase);

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var sourceIds = new List<ObjectId>();
                var correctionOuterHorizontalTargets = new List<(Point2d A, Point2d B)>();
                var correctionOuterVerticalTargets = new List<(Point2d A, Point2d B)>();
                var correctionZeroHorizontalTargets = new List<(Point2d A, Point2d B)>();
                var correctionZeroVerticalTargets = new List<(Point2d A, Point2d B)>();

                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b) || !DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    if (IsBaseUsecLayer(layer))
                    {
                        sourceIds.Add(id);
                        continue;
                    }

                    if (string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsHorizontalLikeForCorrectionLinePost(a, b))
                        {
                            correctionOuterHorizontalTargets.Add((a, b));
                        }
                        else if (IsVerticalLikeForCorrectionLinePost(a, b))
                        {
                            correctionOuterVerticalTargets.Add((a, b));
                        }

                        continue;
                    }

                    if (!string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (IsHorizontalLikeForCorrectionLinePost(a, b))
                    {
                        correctionZeroHorizontalTargets.Add((a, b));
                    }
                    else if (IsVerticalLikeForCorrectionLinePost(a, b))
                    {
                        correctionZeroVerticalTargets.Add((a, b));
                    }
                }

                if (sourceIds.Count == 0 ||
                    (correctionOuterHorizontalTargets.Count == 0 && correctionOuterVerticalTargets.Count == 0) ||
                    (correctionZeroHorizontalTargets.Count == 0 && correctionZeroVerticalTargets.Count == 0))
                {
                    tr.Commit();
                    return false;
                }

                const double endpointTouchTol = 0.50;
                const double endpointMoveTol = 0.05;
                const double minTrim = 0.05;
                const double maxTrim = 80.0;
                const double spanTol = 0.50;
                const double apparentEndpointGapTol = 40.0;
                const double outerBoundaryTol = 0.40;
                var scannedEndpoints = 0;
                var onCorrectionOuter = 0;
                var alreadyOnCorrectionZero = 0;
                var boundarySkipped = 0;
                var noTarget = 0;
                var trimmed = 0;

                foreach (var sourceId in sourceIds)
                {
                    if (!(tr.GetObject(sourceId, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(writable, out var a, out var b))
                    {
                        continue;
                    }

                    var sourceIsHorizontal = IsHorizontalLikeForCorrectionLinePost(a, b);
                    var sourceIsVertical = IsVerticalLikeForCorrectionLinePost(a, b);
                    if (!sourceIsHorizontal && !sourceIsVertical)
                    {
                        continue;
                    }

                    var correctionOuterTargets = sourceIsVertical
                        ? correctionOuterHorizontalTargets
                        : correctionOuterVerticalTargets;
                    var correctionZeroTargets = sourceIsVertical
                        ? correctionZeroHorizontalTargets
                        : correctionZeroVerticalTargets;
                    if (correctionOuterTargets.Count == 0 || correctionZeroTargets.Count == 0)
                    {
                        continue;
                    }

                    var movedSource = false;
                    for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                    {
                        var endpoint = endpointIndex == 0 ? a : b;
                        scannedEndpoints++;

                        if (IsPointOnAnyWindowBoundaryForPlugin(endpoint, outerBoundaryTol, clipWindows))
                        {
                            boundarySkipped++;
                            continue;
                        }

                        var endpointOnCorrectionZero = false;
                        for (var zi = 0; zi < correctionZeroTargets.Count; zi++)
                        {
                            var target = correctionZeroTargets[zi];
                            if (DistancePointToSegment(endpoint, target.A, target.B) <= endpointTouchTol)
                            {
                                endpointOnCorrectionZero = true;
                                break;
                            }
                        }

                        if (endpointOnCorrectionZero)
                        {
                            alreadyOnCorrectionZero++;
                            continue;
                        }

                        var endpointOnCorrectionOuter = false;
                        var endpointAtCorrectionOuterEndpoint = false;
                        for (var oi = 0; oi < correctionOuterTargets.Count; oi++)
                        {
                            var target = correctionOuterTargets[oi];
                            if (DistancePointToSegment(endpoint, target.A, target.B) <= endpointTouchTol)
                            {
                                endpointOnCorrectionOuter = true;
                                if (endpoint.GetDistanceTo(target.A) <= endpointTouchTol ||
                                    endpoint.GetDistanceTo(target.B) <= endpointTouchTol)
                                {
                                    endpointAtCorrectionOuterEndpoint = true;
                                    break;
                                }
                            }

                            if (endpoint.GetDistanceTo(target.A) <= endpointTouchTol ||
                                endpoint.GetDistanceTo(target.B) <= endpointTouchTol)
                            {
                                endpointAtCorrectionOuterEndpoint = true;
                                endpointOnCorrectionOuter = true;
                                break;
                            }
                        }

                        if (!endpointOnCorrectionOuter)
                        {
                            continue;
                        }

                        if (endpointAtCorrectionOuterEndpoint)
                        {
                            continue;
                        }

                        onCorrectionOuter++;
                        var bestFound = false;
                        var bestTarget = endpoint;
                        var bestMoveDistance = double.MaxValue;
                        var bestTargetEndpointDistance = double.MaxValue;
                        var bestHitsBoundaryEndpoint = false;
                        for (var zi = 0; zi < correctionZeroTargets.Count; zi++)
                        {
                            var target = correctionZeroTargets[zi];
                            if (!TryIntersectInfiniteLines(a, b, target.A, target.B, out var intersection))
                            {
                                continue;
                            }

                            var sourceDistance = DistancePointToSegment(intersection, a, b);
                            var sourceEndpointGap = Math.Min(
                                intersection.GetDistanceTo(a),
                                intersection.GetDistanceTo(b));
                            var sourceApparent =
                                sourceDistance > spanTol &&
                                sourceEndpointGap <= apparentEndpointGapTol;
                            if (sourceDistance > spanTol && !sourceApparent)
                            {
                                continue;
                            }

                            var targetDistance = DistancePointToSegment(intersection, target.A, target.B);
                            var targetEndpointGap = Math.Min(
                                intersection.GetDistanceTo(target.A),
                                intersection.GetDistanceTo(target.B));
                            var targetApparent =
                                targetDistance > spanTol &&
                                targetEndpointGap <= apparentEndpointGapTol;
                            if (targetDistance > spanTol && !targetApparent)
                            {
                                continue;
                            }

                            var moveDistance = endpoint.GetDistanceTo(intersection);
                            if (moveDistance <= minTrim || moveDistance > maxTrim)
                            {
                                continue;
                            }

                            var targetEndpointDistance = Math.Min(
                                intersection.GetDistanceTo(target.A),
                                intersection.GetDistanceTo(target.B));
                            var hitsBoundaryEndpoint = targetEndpointDistance <= endpointTouchTol;
                            if (!bestFound ||
                                (hitsBoundaryEndpoint && !bestHitsBoundaryEndpoint) ||
                                (hitsBoundaryEndpoint == bestHitsBoundaryEndpoint &&
                                 (moveDistance < bestMoveDistance - 1e-6 ||
                                  (Math.Abs(moveDistance - bestMoveDistance) <= 1e-6 &&
                                   targetEndpointDistance < bestTargetEndpointDistance - 1e-6))))
                            {
                                bestFound = true;
                                bestTarget = intersection;
                                bestMoveDistance = moveDistance;
                                bestTargetEndpointDistance = targetEndpointDistance;
                                bestHitsBoundaryEndpoint = hitsBoundaryEndpoint;
                            }
                        }

                        if (!bestFound)
                        {
                            noTarget++;
                            continue;
                        }

                        if (!TryMoveEndpoint(writable, endpointIndex == 0, bestTarget, endpointMoveTol))
                        {
                            noTarget++;
                            continue;
                        }

                        trimmed++;
                        movedSource = true;
                        break;
                    }

                    if (!movedSource)
                    {
                        continue;
                    }
                }

                tr.Commit();
                logger?.WriteLine(
                    $"CorrectionLine: outer USEC correction-zero trim scannedEndpoints={scannedEndpoints}, onCorrectionOuter={onCorrectionOuter}, alreadyOnCorrectionZero={alreadyOnCorrectionZero}, boundarySkipped={boundarySkipped}, noTarget={noTarget}, trimmed={trimmed}.");
                return trimmed > 0;
            }
        }

        private static bool TrimCorrectionOuterBlindJoinEndpointsToOrdinaryVerticalBoundaries(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null)
            {
                return false;
            }

            var rawClipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0);
            var clipWindows = MergeOverlappingClipWindows(rawClipWindows);
            if (clipWindows.Count == 0)
            {
                logger?.WriteLine("CorrectionLine: mixed 30.16/20.11 ordinary normalize skipped (no clip windows).");
                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b) => DoesSegmentIntersectAnyWindowForCorrectionLinePost(a, b, clipWindows);
            bool TryReadOpenSegment(Entity ent, out Point2d a, out Point2d b) => TryReadOpenLinearSegment(ent, out a, out b);
            bool TryMoveEndpoint(Entity writable, bool moveStart, Point2d target, double moveTol) =>
                TryMoveEndpointForCorrectionLinePost(writable, moveStart, target, moveTol);
            bool TryMoveEndpointAnyDirection(Entity writable, bool moveStart, Point2d target, double moveTol)
            {
                if (writable == null || writable.IsErased)
                {
                    return false;
                }

                if (writable is Line line)
                {
                    var existing = moveStart
                        ? new Point2d(line.StartPoint.X, line.StartPoint.Y)
                        : new Point2d(line.EndPoint.X, line.EndPoint.Y);
                    if (existing.GetDistanceTo(target) <= moveTol)
                    {
                        return false;
                    }

                    if (moveStart)
                    {
                        line.StartPoint = new Point3d(target.X, target.Y, line.StartPoint.Z);
                    }
                    else
                    {
                        line.EndPoint = new Point3d(target.X, target.Y, line.EndPoint.Z);
                    }

                    return true;
                }

                if (writable is Polyline polyline && polyline.NumberOfVertices >= 2)
                {
                    var index = moveStart ? 0 : (polyline.NumberOfVertices - 1);
                    var existing = polyline.GetPoint2dAt(index);
                    if (existing.GetDistanceTo(target) <= moveTol)
                    {
                        return false;
                    }

                    polyline.SetPointAt(index, target);
                    return true;
                }

                return false;
            }
            bool TryIntersectInfiniteLines(Point2d a0, Point2d a1, Point2d b0, Point2d b1, out Point2d intersection) =>
                TryIntersectInfiniteLinesForPluginGeometry(a0, a1, b0, b1, out intersection);

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var sourceIds = new List<ObjectId>();
                var correctionZeroHorizontalTargets = new List<(Point2d A, Point2d B)>();
                var ordinaryVerticalTargets = new List<(Point2d A, Point2d B, string Layer)>();

                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b) || !DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    if (string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase) &&
                        IsHorizontalLikeForCorrectionLinePost(a, b))
                    {
                        sourceIds.Add(id);
                        continue;
                    }

                    if (string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) &&
                        IsHorizontalLikeForCorrectionLinePost(a, b))
                    {
                        correctionZeroHorizontalTargets.Add((a, b));
                        continue;
                    }

                    var isOrdinaryVerticalLayer =
                        string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, "L-USEC", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, "L-USEC3018", StringComparison.OrdinalIgnoreCase);
                    if (isOrdinaryVerticalLayer && IsVerticalLikeForCorrectionLinePost(a, b))
                    {
                        ordinaryVerticalTargets.Add((a, b, layer));
                    }
                }

                if (sourceIds.Count == 0 ||
                    correctionZeroHorizontalTargets.Count == 0 ||
                    ordinaryVerticalTargets.Count == 0)
                {
                    tr.Commit();
                    return false;
                }

                const double endpointMoveTol = 0.05;
                const double zeroDirectionDotMin = 0.985;
                const double zeroStationTol = 0.80;
                const double zeroOffsetTol = 0.40;
                const double verticalEndpointTol = 0.80;
                const double trimGapTol = 1.00;
                const double minTrim = 0.05;
                var expectedBlindJoinTrim = RoadAllowanceUsecWidthMeters - RoadAllowanceSecWidthMeters;
                var maxTrim = expectedBlindJoinTrim + 2.0;

                var scannedEndpoints = 0;
                var zeroMatchedEndpoints = 0;
                var noTarget = 0;
                var trimmedEndpoints = 0;
                var trimmedLines = 0;
                var moveSamples = new List<string>();

                bool TryFindSameStationCorrectionZeroEndpoint(Point2d endpoint, Point2d other, out double score)
                {
                    score = double.MaxValue;
                    var sourceDir = other - endpoint;
                    var sourceLen = sourceDir.Length;
                    if (sourceLen <= 1e-6)
                    {
                        return false;
                    }

                    var sourceUnit = sourceDir / sourceLen;
                    var found = false;
                    for (var i = 0; i < correctionZeroHorizontalTargets.Count; i++)
                    {
                        var target = correctionZeroHorizontalTargets[i];
                        var targetDir = target.B - target.A;
                        var targetLen = targetDir.Length;
                        if (targetLen <= 1e-6)
                        {
                            continue;
                        }

                        var targetUnit = targetDir / targetLen;
                        if (Math.Abs(sourceUnit.DotProduct(targetUnit)) < zeroDirectionDotMin)
                        {
                            continue;
                        }

                        var candidates = new[] { target.A, target.B };
                        for (var ci = 0; ci < candidates.Length; ci++)
                        {
                            var candidate = candidates[ci];
                            var along = Math.Abs((candidate - endpoint).DotProduct(sourceUnit));
                            if (along > zeroStationTol)
                            {
                                continue;
                            }

                            var offsetDelta = Math.Abs(
                                Math.Abs(DistancePointToInfiniteLine(candidate, endpoint, other)) -
                                CorrectionLinePostInsetMeters);
                            if (offsetDelta > zeroOffsetTol)
                            {
                                continue;
                            }

                            var candidateScore = along + (offsetDelta * 10.0);
                            if (!found || candidateScore < score - 1e-6)
                            {
                                found = true;
                                score = candidateScore;
                            }
                        }
                    }

                    return found;
                }

                foreach (var sourceId in sourceIds)
                {
                    if (!(tr.GetObject(sourceId, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(writable, out var a, out var b) || !IsHorizontalLikeForCorrectionLinePost(a, b))
                    {
                        continue;
                    }

                    var movedSource = false;
                    for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                    {
                        var endpoint = endpointIndex == 0 ? a : b;
                        var other = endpointIndex == 0 ? b : a;
                        scannedEndpoints++;

                        if (!TryFindSameStationCorrectionZeroEndpoint(endpoint, other, out var zeroScore))
                        {
                            continue;
                        }

                        zeroMatchedEndpoints++;
                        var inward = other - endpoint;
                        var inwardLen = inward.Length;
                        if (inwardLen <= 1e-6)
                        {
                            continue;
                        }

                        var inwardUnit = inward / inwardLen;
                        var foundTarget = false;
                        var bestTarget = endpoint;
                        var bestScore = double.MaxValue;
                        for (var i = 0; i < ordinaryVerticalTargets.Count; i++)
                        {
                            var target = ordinaryVerticalTargets[i];
                            if (!TryIntersectInfiniteLines(endpoint, other, target.A, target.B, out var intersection))
                            {
                                continue;
                            }

                            var endpointGap = Math.Min(
                                intersection.GetDistanceTo(target.A),
                                intersection.GetDistanceTo(target.B));
                            if (endpointGap > verticalEndpointTol)
                            {
                                continue;
                            }

                            var trimDistance = (intersection - endpoint).DotProduct(inwardUnit);
                            if (trimDistance <= minTrim || trimDistance > maxTrim)
                            {
                                continue;
                            }

                            var trimGapDelta = Math.Abs(trimDistance - expectedBlindJoinTrim);
                            if (trimGapDelta > trimGapTol)
                            {
                                continue;
                            }

                            var score = trimGapDelta + zeroScore + endpointGap;
                            if (!foundTarget || score < bestScore - 1e-6)
                            {
                                foundTarget = true;
                                bestScore = score;
                                bestTarget = intersection;
                            }
                        }

                        if (!foundTarget)
                        {
                            noTarget++;
                            continue;
                        }

                        if (!TryMoveEndpoint(writable, endpointIndex == 0, bestTarget, endpointMoveTol))
                        {
                            noTarget++;
                            continue;
                        }

                        trimmedEndpoints++;
                        movedSource = true;
                        if (moveSamples.Count < 20)
                        {
                            moveSamples.Add(
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "id={0} {1} ({2:0.###},{3:0.###})->({4:0.###},{5:0.###})",
                                    sourceId.Handle.ToString(),
                                    endpointIndex == 0 ? "start" : "end",
                                    endpoint.X,
                                    endpoint.Y,
                                    bestTarget.X,
                                    bestTarget.Y));
                        }

                        if (!TryReadOpenSegment(writable, out a, out b))
                        {
                            break;
                        }
                    }

                    if (movedSource)
                    {
                        trimmedLines++;
                    }
                }

                bool IsOrdinaryUsecLayer(string layer)
                {
                    return string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(layer, "L-USEC", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(layer, "L-USEC2012", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(layer, "L-USEC3018", StringComparison.OrdinalIgnoreCase);
                }

                var mixedAdjustedVerticals = 0;
                var mixedAdjustedHorizontals = 0;
                var mixedSamples = new List<string>();

                List<(ObjectId Id, string Layer, Point2d A, Point2d B, double Length, double Axis, double MajorMin, double MajorMax, Vector2d Unit)> CollectMixedHorizontals()
                {
                    var results = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, double Length, double Axis, double MajorMin, double MajorMax, Vector2d Unit)>();
                    foreach (ObjectId id in ms)
                    {
                        Entity? ent = null;
                        try
                        {
                            ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception)
                        {
                            continue;
                        }

                        if (ent == null || ent.IsErased || !IsOrdinaryUsecLayer(ent.Layer ?? string.Empty))
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(ent, out var a, out var b) ||
                            !DoesSegmentIntersectAnyWindow(a, b) ||
                            !IsHorizontalLikeForCorrectionLinePost(a, b))
                        {
                            continue;
                        }

                        var dir = b - a;
                        var len = dir.Length;
                        if (len <= 1e-6)
                        {
                            continue;
                        }

                        results.Add((id, ent.Layer ?? string.Empty, a, b, len, 0.5 * (a.Y + b.Y), Math.Min(a.X, b.X), Math.Max(a.X, b.X), dir / len));
                    }

                    return results;
                }

                List<(ObjectId Id, string Layer, Point2d A, Point2d B, double Length, Vector2d Unit)> CollectMixedVerticals(bool ordinaryOnly)
                {
                    var results = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, double Length, Vector2d Unit)>();
                    foreach (ObjectId id in ms)
                    {
                        Entity? ent = null;
                        try
                        {
                            ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception)
                        {
                            continue;
                        }

                        var layer = ent?.Layer ?? string.Empty;
                        if (ent == null || ent.IsErased)
                        {
                            continue;
                        }

                        var isSupportedLayer =
                            IsOrdinaryUsecLayer(layer) ||
                            string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(layer, "L-QSEC", StringComparison.OrdinalIgnoreCase);
                        if (!isSupportedLayer || (ordinaryOnly && !IsOrdinaryUsecLayer(layer)))
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(ent, out var a, out var b) ||
                            !DoesSegmentIntersectAnyWindow(a, b) ||
                            !IsVerticalLikeForCorrectionLinePost(a, b))
                        {
                            continue;
                        }

                        var dir = b - a;
                        var len = dir.Length;
                        if (len <= 1e-6)
                        {
                            continue;
                        }

                        results.Add((id, layer, a, b, len, dir / len));
                    }

                    return results;
                }

                bool HasParallelHorizontalCompanion(
                    (ObjectId Id, string Layer, Point2d A, Point2d B, double Length, double Axis, double MajorMin, double MajorMax, Vector2d Unit) row,
                    IReadOnlyList<(ObjectId Id, string Layer, Point2d A, Point2d B, double Length, double Axis, double MajorMin, double MajorMax, Vector2d Unit)> allRows,
                    double expectedGap,
                    double gapTol)
                {
                    const double parallelDotMin = 0.985;
                    for (var i = 0; i < allRows.Count; i++)
                    {
                        var other = allRows[i];
                        if (other.Id == row.Id)
                        {
                            continue;
                        }

                        if (Math.Abs(row.Unit.DotProduct(other.Unit)) < parallelDotMin)
                        {
                            continue;
                        }

                        var overlap = Math.Min(row.MajorMax, other.MajorMax) - Math.Max(row.MajorMin, other.MajorMin);
                        if (overlap < 8.0)
                        {
                            continue;
                        }

                        var rowMid = Midpoint(row.A, row.B);
                        var gap = Math.Abs(DistancePointToInfiniteLine(rowMid, other.A, other.B));
                        if (Math.Abs(gap - expectedGap) <= gapTol)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                const double mixedEndpointTol = 0.80;
                const double mixedParallelDotMin = 0.985;
                const double mixedMinMove = 0.05;
                const double mixedMoveTol = 0.05;
                const double mixedRetreatTol = 1.60;
                const double mixedRetreatStationTol = 25.0;
                const double mixedCompanionTol = 1.80;
                const double mixedPivotTol = 1.80;
                const double mixedAnchorOffsetTol = 2.00;
                const double mixedCurrentOffsetTol = 2.00;
                const double mixedOverlapMin = 60.0;

                var mixedHorizontals = CollectMixedHorizontals();
                var mixedOrdinaryVerticals = CollectMixedVerticals(ordinaryOnly: true);
                logger?.WriteLine(
                    $"Cleanup: mixed ordinary live counts h={mixedHorizontals.Count}, v={mixedOrdinaryVerticals.Count}.");
                for (var vi = 0; vi < mixedOrdinaryVerticals.Count; vi++)
                {
                    var source = mixedOrdinaryVerticals[vi];
                    if (!(tr.GetObject(source.Id, OpenMode.ForWrite, false) is Entity writableVertical) || writableVertical.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(writableVertical, out var sourceA, out var sourceB) || !IsVerticalLikeForCorrectionLinePost(sourceA, sourceB))
                    {
                        continue;
                    }

                    for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                    {
                        var endpoint = endpointIndex == 0 ? sourceA : sourceB;
                        var other = endpointIndex == 0 ? sourceB : sourceA;
                        var inward = other - endpoint;
                        var inwardLen = inward.Length;
                        if (inwardLen <= 1e-6)
                        {
                            continue;
                        }

                        var inwardUnit = inward / inwardLen;
                        var foundTarget = false;
                        var bestTarget = endpoint;
                        var bestScore = double.MaxValue;
                        for (var ci = 0; ci < mixedHorizontals.Count; ci++)
                        {
                            var candidate = mixedHorizontals[ci];
                            if (candidate.Id == source.Id)
                            {
                                continue;
                            }

                            if (!HasParallelHorizontalCompanion(candidate, mixedHorizontals, RoadAllowanceSecWidthMeters, mixedCompanionTol) &&
                                !HasParallelHorizontalCompanion(candidate, mixedHorizontals, RoadAllowanceUsecWidthMeters, mixedCompanionTol))
                            {
                                continue;
                            }

                            if (!TryIntersectInfiniteLines(sourceA, sourceB, candidate.A, candidate.B, out var target))
                            {
                                continue;
                            }

                            if (DistancePointToSegment(target, candidate.A, candidate.B) > mixedEndpointTol)
                            {
                                continue;
                            }

                            var along = (target - endpoint).DotProduct(inwardUnit);
                            if (Math.Abs(along - RoadAllowanceSecWidthMeters) > mixedRetreatTol)
                            {
                                continue;
                            }

                            var stationGap = Math.Min(
                                Math.Min(Math.Abs(endpoint.X - candidate.A.X), Math.Abs(endpoint.X - candidate.B.X)),
                                endpoint.X < candidate.MajorMin
                                    ? (candidate.MajorMin - endpoint.X)
                                    : endpoint.X > candidate.MajorMax
                                        ? (endpoint.X - candidate.MajorMax)
                                        : 0.0);
                            if (stationGap > mixedRetreatStationTol)
                            {
                                continue;
                            }

                            var score =
                                Math.Abs(along - RoadAllowanceSecWidthMeters) +
                                (0.04 * stationGap) -
                                (0.0005 * candidate.Length);
                            if (!foundTarget || score < bestScore - 1e-6)
                            {
                                foundTarget = true;
                                bestScore = score;
                                bestTarget = target;
                            }
                        }

                        if (!foundTarget || endpoint.GetDistanceTo(bestTarget) <= mixedMinMove)
                        {
                            continue;
                        }

                        if (!TryMoveEndpoint(writableVertical, endpointIndex == 0, bestTarget, mixedMoveTol))
                        {
                            continue;
                        }

                        mixedAdjustedVerticals++;
                        if (mixedSamples.Count < 16)
                        {
                            mixedSamples.Add(
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "v id={0} ({1:0.###},{2:0.###})->({3:0.###},{4:0.###})",
                                    source.Id.Handle.ToString(),
                                    endpoint.X,
                                    endpoint.Y,
                                    bestTarget.X,
                                    bestTarget.Y));
                        }

                        if (!TryReadOpenSegment(writableVertical, out sourceA, out sourceB))
                        {
                            break;
                        }
                    }
                }

                mixedHorizontals = CollectMixedHorizontals();
                var mixedSupportVerticals = CollectMixedVerticals(ordinaryOnly: false);
                logger?.WriteLine(
                    $"Cleanup: mixed ordinary support counts h={mixedHorizontals.Count}, supportV={mixedSupportVerticals.Count}.");
                for (var hi = 0; hi < mixedHorizontals.Count; hi++)
                {
                    var source = mixedHorizontals[hi];
                    if (!(tr.GetObject(source.Id, OpenMode.ForWrite, false) is Entity writableHorizontal) || writableHorizontal.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(writableHorizontal, out var sourceA, out var sourceB) || !IsHorizontalLikeForCorrectionLinePost(sourceA, sourceB))
                    {
                        continue;
                    }

                    for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                    {
                        var endpoint = endpointIndex == 0 ? sourceA : sourceB;
                        var anchor = endpointIndex == 0 ? sourceB : sourceA;
                        var traceMixedSec31Horizontal =
                            Math.Abs(endpoint.X - 615061.122) <= 1.0 &&
                            Math.Abs(endpoint.Y - 5846513.752) <= 1.0;
                        var foundTarget = false;
                        var bestTarget = endpoint;
                        var bestScore = double.MaxValue;

                        for (var si = 0; si < mixedSupportVerticals.Count; si++)
                        {
                            var support = mixedSupportVerticals[si];
                            if (support.Id == source.Id || DistancePointToSegment(endpoint, support.A, support.B) > mixedEndpointTol)
                            {
                                continue;
                            }

                            for (var ci = 0; ci < mixedHorizontals.Count; ci++)
                            {
                                var candidate = mixedHorizontals[ci];
                                if (candidate.Id == source.Id)
                                {
                                    continue;
                                }

                                if (Math.Abs(candidate.Unit.DotProduct(source.Unit)) < mixedParallelDotMin)
                                {
                                    continue;
                                }

                                var overlap = Math.Min(source.MajorMax, candidate.MajorMax) - Math.Max(source.MajorMin, candidate.MajorMin);
                                if (overlap < mixedOverlapMin)
                                {
                                    continue;
                                }

                                var anchorOffset = Math.Abs(DistancePointToInfiniteLine(anchor, candidate.A, candidate.B));
                                if (Math.Abs(anchorOffset - RoadAllowanceUsecWidthMeters) > mixedAnchorOffsetTol)
                                {
                                    continue;
                                }

                                var currentOffset = Math.Abs(DistancePointToInfiniteLine(endpoint, candidate.A, candidate.B));
                                if (Math.Abs(currentOffset - RoadAllowanceSecWidthMeters) > mixedCurrentOffsetTol)
                                {
                                    if (traceMixedSec31Horizontal)
                                    {
                                        logger?.WriteLine(
                                            $"Cleanup: mixed ordinary sec31 horizontal reject offset cand={candidate.A.X:0.###},{candidate.A.Y:0.###}->{candidate.B.X:0.###},{candidate.B.Y:0.###} anchorOff={anchorOffset:0.###} currentOff={currentOffset:0.###}");
                                    }
                                    continue;
                                }

                                var candidateParallelPoint = anchor + candidate.Unit;
                                if (!TryIntersectInfiniteLines(anchor, candidateParallelPoint, support.A, support.B, out var target))
                                {
                                    continue;
                                }

                                if (DistancePointToSegment(target, support.A, support.B) > mixedEndpointTol)
                                {
                                    continue;
                                }

                                var moveDistance = endpoint.GetDistanceTo(target);
                                if (moveDistance <= mixedMinMove || Math.Abs(moveDistance - CorrectionLinePairGapMeters) > mixedPivotTol)
                                {
                                    if (traceMixedSec31Horizontal)
                                    {
                                        logger?.WriteLine(
                                            $"Cleanup: mixed ordinary sec31 horizontal reject move cand={candidate.A.X:0.###},{candidate.A.Y:0.###}->{candidate.B.X:0.###},{candidate.B.Y:0.###} move={moveDistance:0.###}");
                                    }
                                    continue;
                                }

                                var score =
                                    Math.Abs(anchorOffset - RoadAllowanceUsecWidthMeters) +
                                    Math.Abs(currentOffset - RoadAllowanceSecWidthMeters) +
                                    Math.Abs(moveDistance - CorrectionLinePairGapMeters);
                                if (traceMixedSec31Horizontal)
                                {
                                    logger?.WriteLine(
                                        $"Cleanup: mixed ordinary sec31 horizontal cand={candidate.A.X:0.###},{candidate.A.Y:0.###}->{candidate.B.X:0.###},{candidate.B.Y:0.###} anchorOff={anchorOffset:0.###} currentOff={currentOffset:0.###} move={moveDistance:0.###} score={score:0.###}");
                                }
                                if (!foundTarget || score < bestScore - 1e-6)
                                {
                                    foundTarget = true;
                                    bestScore = score;
                                    bestTarget = target;
                                }
                            }
                        }

                        if (!foundTarget || endpoint.GetDistanceTo(bestTarget) <= mixedMinMove)
                        {
                            continue;
                        }

                        if (!TryMoveEndpointAnyDirection(writableHorizontal, endpointIndex == 0, bestTarget, mixedMoveTol))
                        {
                            continue;
                        }

                        mixedAdjustedHorizontals++;
                        if (mixedSamples.Count < 16)
                        {
                            mixedSamples.Add(
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "h id={0} ({1:0.###},{2:0.###})->({3:0.###},{4:0.###})",
                                    source.Id.Handle.ToString(),
                                    endpoint.X,
                                    endpoint.Y,
                                    bestTarget.X,
                                    bestTarget.Y));
                        }

                        if (!TryReadOpenSegment(writableHorizontal, out sourceA, out sourceB))
                        {
                            break;
                        }
                    }
                }

                tr.Commit();
                logger?.WriteLine(
                    $"Cleanup: mixed ordinary adjusted v={mixedAdjustedVerticals}, h={mixedAdjustedHorizontals}.");
                logger?.WriteLine(
                    $"CorrectionLine: blind-30 correction-outer trim scannedEndpoints={scannedEndpoints}, zeroMatched={zeroMatchedEndpoints}, noTarget={noTarget}, trimmedEndpoints={trimmedEndpoints}, trimmedLines={trimmedLines}, mixedVerticalAdjusted={mixedAdjustedVerticals}, mixedHorizontalAdjusted={mixedAdjustedHorizontals}.");
                for (var i = 0; i < moveSamples.Count; i++)
                {
                    logger?.WriteLine("CorrectionLine:   blind-30-trim " + moveSamples[i]);
                }
                for (var i = 0; i < mixedSamples.Count; i++)
                {
                    logger?.WriteLine("CorrectionLine:   mixed-30-20 " + mixedSamples[i]);
                }

                return trimmedEndpoints > 0 || mixedAdjustedVerticals > 0 || mixedAdjustedHorizontals > 0;
            }
        }

        private static bool NormalizeMixedThirtyTwentyOrdinaryEndpoints(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null)
            {
                return false;
            }

            var rawClipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0);
            var clipWindows = MergeOverlappingClipWindows(rawClipWindows);
            if (clipWindows.Count == 0)
            {
                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b) => DoesSegmentIntersectAnyWindowForCorrectionLinePost(a, b, clipWindows);
            bool TryReadOpenSegment(Entity ent, out Point2d a, out Point2d b) => TryReadOpenLinearSegment(ent, out a, out b);
            bool TryMoveEndpoint(Entity writable, bool moveStart, Point2d target, double moveTol) =>
                TryMoveEndpointForCorrectionLinePost(writable, moveStart, target, moveTol);
            bool TryIntersectInfiniteLines(Point2d a0, Point2d a1, Point2d b0, Point2d b1, out Point2d intersection) =>
                TryIntersectInfiniteLinesForPluginGeometry(a0, a1, b0, b1, out intersection);

            try
            {
                using (var tr = database.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                    bool IsOrdinaryUsecLayer(string layer)
                    {
                        return string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(layer, "L-USEC", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(layer, "L-USEC2012", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(layer, "L-USEC3018", StringComparison.OrdinalIgnoreCase);
                    }

                    var ordinaryHorizontals = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, double Length, double Axis, double MajorMin, double MajorMax, Vector2d Unit)>();
                    var ordinaryVerticals = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, double Length, double Axis, double MajorMin, double MajorMax, Vector2d Unit)>();
                    var supportVerticals = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, double Length)>();

                    void CollectSegments()
                    {
                        ordinaryHorizontals.Clear();
                        ordinaryVerticals.Clear();
                        supportVerticals.Clear();
                        foreach (ObjectId id in ms)
                        {
                            Entity? ent = null;
                            try
                            {
                                ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                            }
                            catch (Autodesk.AutoCAD.Runtime.Exception)
                            {
                                continue;
                            }
                            if (ent == null || ent.IsErased)
                            {
                                continue;
                            }

                            if (!TryReadOpenSegment(ent, out var a, out var b) || !DoesSegmentIntersectAnyWindow(a, b))
                            {
                                continue;
                            }

                            var layer = ent.Layer ?? string.Empty;
                            var horizontal = IsHorizontalLikeForCorrectionLinePost(a, b);
                            var vertical = IsVerticalLikeForCorrectionLinePost(a, b);
                            if (!horizontal && !vertical)
                            {
                                continue;
                            }

                            var dir = b - a;
                            var len = dir.Length;
                            if (len <= 1e-6)
                            {
                                continue;
                            }

                            var unit = dir / len;
                            if (IsOrdinaryUsecLayer(layer))
                            {
                                if (horizontal)
                                {
                                    ordinaryHorizontals.Add((id, layer, a, b, len, 0.5 * (a.Y + b.Y), Math.Min(a.X, b.X), Math.Max(a.X, b.X), unit));
                                }
                                else if (vertical)
                                {
                                    ordinaryVerticals.Add((id, layer, a, b, len, 0.5 * (a.X + b.X), Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y), unit));
                                }
                            }

                            var isSupportVertical =
                                vertical &&
                                (IsOrdinaryUsecLayer(layer) ||
                                 string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(layer, "L-QSEC", StringComparison.OrdinalIgnoreCase));
                            if (isSupportVertical)
                            {
                                supportVerticals.Add((id, layer, a, b, len));
                            }
                        }
                    }

                    CollectSegments();
                    if (ordinaryHorizontals.Count == 0 || (ordinaryVerticals.Count == 0 && supportVerticals.Count == 0))
                    {
                        logger?.WriteLine(
                            $"CorrectionLine: mixed 30.16/20.11 ordinary normalize skipped (h={ordinaryHorizontals.Count}, v={ordinaryVerticals.Count}, supportV={supportVerticals.Count}).");
                        tr.Commit();
                        return false;
                    }

                    logger?.WriteLine(
                        $"CorrectionLine: mixed 30.16/20.11 ordinary normalize candidates h={ordinaryHorizontals.Count}, v={ordinaryVerticals.Count}, supportV={supportVerticals.Count}.");

                    const double endpointTouchTol = 0.80;
                    const double supportTouchTol = 0.80;
                    const double parallelDotMin = 0.985;
                    const double minMove = 1.0;
                    const double endpointMoveTol = 0.05;
                    const double shortTouchedLengthMax = 220.0;
                    const double longerCandidateMargin = 40.0;
                    const double retreatTarget = RoadAllowanceSecWidthMeters;
                    const double retreatTol = 1.60;
                    const double retreatStationTol = 25.0;
                    const double retreatCompanionTol = 1.80;
                    const double pivotTarget = CorrectionLinePairGapMeters;
                    const double pivotTol = 1.80;
                    const double pivotAnchorOffsetTol = 2.00;
                    const double pivotCurrentOffsetTol = 2.00;
                    const double overlapMin = 60.0;

                bool HasParallelHorizontalCompanion(
                    (ObjectId Id, string Layer, Point2d A, Point2d B, double Length, double Axis, double MajorMin, double MajorMax, Vector2d Unit) row,
                    double expectedGap,
                    double gapTol)
                {
                    for (var i = 0; i < ordinaryHorizontals.Count; i++)
                    {
                        var other = ordinaryHorizontals[i];
                        if (other.Id == row.Id)
                        {
                            continue;
                        }

                        if (Math.Abs(row.Unit.DotProduct(other.Unit)) < parallelDotMin)
                        {
                            continue;
                        }

                        var overlap = Math.Min(row.MajorMax, other.MajorMax) - Math.Max(row.MajorMin, other.MajorMin);
                        if (overlap < 8.0)
                        {
                            continue;
                        }

                        var rowMid = Midpoint(row.A, row.B);
                        var gap = Math.Abs(DistancePointToInfiniteLine(rowMid, other.A, other.B));
                        if (Math.Abs(gap - expectedGap) <= gapTol)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                var adjustedVerticalEndpoints = 0;
                var adjustedHorizontalEndpoints = 0;
                var mixedSamples = new List<string>();

                for (var i = 0; i < ordinaryVerticals.Count; i++)
                {
                    var source = ordinaryVerticals[i];
                    if (!(tr.GetObject(source.Id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(writable, out var a, out var b) || !IsVerticalLikeForCorrectionLinePost(a, b))
                    {
                        continue;
                    }

                    for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                    {
                        var endpoint = endpointIndex == 0 ? a : b;
                        var other = endpointIndex == 0 ? b : a;
                        var inward = other - endpoint;
                        var inwardLen = inward.Length;
                        if (inwardLen <= 1e-6)
                        {
                            continue;
                        }

                        var inwardUnit = inward / inwardLen;
                        var foundTarget = false;
                        var bestTarget = endpoint;
                        var bestScore = double.MaxValue;
                        for (var hi = 0; hi < ordinaryHorizontals.Count; hi++)
                        {
                            var touched = ordinaryHorizontals[hi];
                            if (DistancePointToSegment(endpoint, touched.A, touched.B) > endpointTouchTol)
                            {
                                continue;
                            }

                            var touchIsEndpoint =
                                endpoint.GetDistanceTo(touched.A) <= endpointTouchTol ||
                                endpoint.GetDistanceTo(touched.B) <= endpointTouchTol;
                            if (!touchIsEndpoint || touched.Length > shortTouchedLengthMax)
                            {
                                continue;
                            }

                            if (!HasParallelHorizontalCompanion(touched, CorrectionLinePairGapMeters, retreatCompanionTol) &&
                                !HasParallelHorizontalCompanion(touched, RoadAllowanceSecWidthMeters, retreatCompanionTol))
                            {
                                continue;
                            }

                            for (var ci = 0; ci < ordinaryHorizontals.Count; ci++)
                            {
                                var candidate = ordinaryHorizontals[ci];
                                if (candidate.Id == touched.Id || candidate.Id == source.Id)
                                {
                                    continue;
                                }

                                if (Math.Abs(candidate.Unit.DotProduct(touched.Unit)) < parallelDotMin)
                                {
                                    continue;
                                }

                                if (candidate.Length <= (touched.Length + longerCandidateMargin))
                                {
                                    continue;
                                }

                                var touchedMid = Midpoint(touched.A, touched.B);
                                var gap = Math.Abs(DistancePointToInfiniteLine(touchedMid, candidate.A, candidate.B));
                                if (Math.Abs(gap - retreatTarget) > retreatTol)
                                {
                                    continue;
                                }

                                if (!TryIntersectInfiniteLines(a, b, candidate.A, candidate.B, out var target))
                                {
                                    continue;
                                }

                                if (DistancePointToSegment(target, candidate.A, candidate.B) > endpointTouchTol)
                                {
                                    continue;
                                }

                                var along = (target - endpoint).DotProduct(inwardUnit);
                                if (Math.Abs(along - retreatTarget) > retreatTol)
                                {
                                    continue;
                                }

                                var stationGap = Math.Min(
                                    Math.Min(Math.Abs(endpoint.X - candidate.A.X), Math.Abs(endpoint.X - candidate.B.X)),
                                    endpoint.X < candidate.MajorMin
                                        ? (candidate.MajorMin - endpoint.X)
                                        : endpoint.X > candidate.MajorMax
                                            ? (endpoint.X - candidate.MajorMax)
                                            : 0.0);
                                if (stationGap > retreatStationTol)
                                {
                                    continue;
                                }

                                var score =
                                    Math.Abs(gap - retreatTarget) +
                                    Math.Abs(along - retreatTarget) +
                                    (0.04 * stationGap) +
                                    (0.001 * Math.Max(0.0, touched.Length - candidate.Length));
                                if (!foundTarget || score < bestScore - 1e-6)
                                {
                                    foundTarget = true;
                                    bestScore = score;
                                    bestTarget = target;
                                }
                            }
                        }

                        if (!foundTarget || endpoint.GetDistanceTo(bestTarget) <= minMove)
                        {
                            continue;
                        }

                        if (!TryMoveEndpoint(writable, endpointIndex == 0, bestTarget, endpointMoveTol))
                        {
                            continue;
                        }

                        adjustedVerticalEndpoints++;
                        if (mixedSamples.Count < 16)
                        {
                            mixedSamples.Add(
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "vertical id={0} ({1:0.###},{2:0.###})->({3:0.###},{4:0.###})",
                                    source.Id.Handle.ToString(),
                                    endpoint.X,
                                    endpoint.Y,
                                    bestTarget.X,
                                    bestTarget.Y));
                        }

                        if (!TryReadOpenSegment(writable, out a, out b))
                        {
                            break;
                        }
                    }
                }

                CollectSegments();
                for (var i = 0; i < ordinaryHorizontals.Count; i++)
                {
                    var source = ordinaryHorizontals[i];
                    if (!(tr.GetObject(source.Id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(writable, out var a, out var b) || !IsHorizontalLikeForCorrectionLinePost(a, b))
                    {
                        continue;
                    }

                    for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                    {
                        var endpoint = endpointIndex == 0 ? a : b;
                        var anchor = endpointIndex == 0 ? b : a;
                        var foundTarget = false;
                        var bestTarget = endpoint;
                        var bestScore = double.MaxValue;

                        for (var si = 0; si < supportVerticals.Count; si++)
                        {
                            var support = supportVerticals[si];
                            if (support.Id == source.Id || DistancePointToSegment(endpoint, support.A, support.B) > supportTouchTol)
                            {
                                continue;
                            }

                            for (var ci = 0; ci < ordinaryHorizontals.Count; ci++)
                            {
                                var candidate = ordinaryHorizontals[ci];
                                if (candidate.Id == source.Id)
                                {
                                    continue;
                                }

                                if (Math.Abs(candidate.Unit.DotProduct(source.Unit)) < parallelDotMin)
                                {
                                    continue;
                                }

                                var overlap = Math.Min(source.MajorMax, candidate.MajorMax) - Math.Max(source.MajorMin, candidate.MajorMin);
                                if (overlap < overlapMin)
                                {
                                    continue;
                                }

                                var anchorOffset = Math.Abs(DistancePointToInfiniteLine(anchor, candidate.A, candidate.B));
                                if (Math.Abs(anchorOffset - RoadAllowanceUsecWidthMeters) > pivotAnchorOffsetTol)
                                {
                                    continue;
                                }

                                var currentOffset = Math.Abs(DistancePointToInfiniteLine(endpoint, candidate.A, candidate.B));
                                if (Math.Abs(currentOffset - RoadAllowanceSecWidthMeters) > pivotCurrentOffsetTol)
                                {
                                    continue;
                                }

                                var candidateParallelPoint = anchor + candidate.Unit;
                                if (!TryIntersectInfiniteLines(anchor, candidateParallelPoint, support.A, support.B, out var target))
                                {
                                    continue;
                                }

                                if (DistancePointToSegment(target, support.A, support.B) > supportTouchTol)
                                {
                                    continue;
                                }

                                var moveDistance = endpoint.GetDistanceTo(target);
                                if (moveDistance <= minMove || Math.Abs(moveDistance - pivotTarget) > pivotTol)
                                {
                                    continue;
                                }

                                var score =
                                    Math.Abs(anchorOffset - RoadAllowanceUsecWidthMeters) +
                                    Math.Abs(currentOffset - RoadAllowanceSecWidthMeters) +
                                    Math.Abs(moveDistance - pivotTarget);
                                if (!foundTarget || score < bestScore - 1e-6)
                                {
                                    foundTarget = true;
                                    bestScore = score;
                                    bestTarget = target;
                                }
                            }
                        }

                        if (!foundTarget || endpoint.GetDistanceTo(bestTarget) <= minMove)
                        {
                            continue;
                        }

                        if (!TryMoveEndpoint(writable, endpointIndex == 0, bestTarget, endpointMoveTol))
                        {
                            continue;
                        }

                        adjustedHorizontalEndpoints++;
                        if (mixedSamples.Count < 16)
                        {
                            mixedSamples.Add(
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "horizontal id={0} ({1:0.###},{2:0.###})->({3:0.###},{4:0.###})",
                                    source.Id.Handle.ToString(),
                                    endpoint.X,
                                    endpoint.Y,
                                    bestTarget.X,
                                    bestTarget.Y));
                        }

                        if (!TryReadOpenSegment(writable, out a, out b))
                        {
                            break;
                        }
                    }
                }

                    tr.Commit();
                    logger?.WriteLine(
                        $"CorrectionLine: mixed 30.16/20.11 ordinary normalize verticalAdjusted={adjustedVerticalEndpoints}, horizontalAdjusted={adjustedHorizontalEndpoints}.");
                    for (var i = 0; i < mixedSamples.Count; i++)
                    {
                        logger?.WriteLine("CorrectionLine:   mixed-30-20 " + mixedSamples[i]);
                    }

                    return adjustedVerticalEndpoints > 0 || adjustedHorizontalEndpoints > 0;
                }
            }
            catch (System.Exception ex)
            {
                logger?.WriteLine("CorrectionLine: mixed 30.16/20.11 ordinary normalize exception: " + ex.Message);
                return false;
            }
        }

        private static bool NormalizeFinalMixedThirtyTwentyOrdinaryEndpoints(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null)
            {
                return false;
            }

            var rawClipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0);
            var clipWindows = MergeOverlappingClipWindows(rawClipWindows);
            if (clipWindows.Count == 0)
            {
                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b) => DoesSegmentIntersectAnyWindowForCorrectionLinePost(a, b, clipWindows);
            bool TryReadOpenSegment(Entity ent, out Point2d a, out Point2d b) => TryReadOpenLinearSegment(ent, out a, out b);
            bool TryMoveEndpoint(Entity writable, bool moveStart, Point2d target, double moveTol) =>
                TryMoveEndpointForCorrectionLinePost(writable, moveStart, target, moveTol);
            bool TryMoveEndpointAnyDirection(Entity writable, bool moveStart, Point2d target, double moveTol)
            {
                if (writable == null || writable.IsErased)
                {
                    return false;
                }

                if (writable is Line line)
                {
                    var existing = moveStart
                        ? new Point2d(line.StartPoint.X, line.StartPoint.Y)
                        : new Point2d(line.EndPoint.X, line.EndPoint.Y);
                    if (existing.GetDistanceTo(target) <= moveTol)
                    {
                        return false;
                    }

                    if (moveStart)
                    {
                        line.StartPoint = new Point3d(target.X, target.Y, line.StartPoint.Z);
                    }
                    else
                    {
                        line.EndPoint = new Point3d(target.X, target.Y, line.EndPoint.Z);
                    }

                    return true;
                }

                if (writable is Polyline polyline && polyline.NumberOfVertices >= 2)
                {
                    var index = moveStart ? 0 : (polyline.NumberOfVertices - 1);
                    var existing = polyline.GetPoint2dAt(index);
                    if (existing.GetDistanceTo(target) <= moveTol)
                    {
                        return false;
                    }

                    polyline.SetPointAt(index, target);
                    return true;
                }

                return false;
            }
            bool TryIntersectInfiniteLines(Point2d a0, Point2d a1, Point2d b0, Point2d b1, out Point2d intersection) =>
                TryIntersectInfiniteLinesForPluginGeometry(a0, a1, b0, b1, out intersection);

            try
            {
                using (var tr = database.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                    bool IsOrdinaryUsecLayer(string layer)
                    {
                        return string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(layer, "L-USEC", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(layer, "L-USEC2012", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(layer, "L-USEC3018", StringComparison.OrdinalIgnoreCase);
                    }

                    var ordinaryHorizontals =
                        new List<(ObjectId Id, string Layer, Point2d A, Point2d B, double Length, double MajorMin, double MajorMax, Vector2d Unit)>();
                    var ordinaryVerticals =
                        new List<(ObjectId Id, string Layer, Point2d A, Point2d B, double Length, Vector2d Unit)>();
                    var supportVerticals =
                        new List<(ObjectId Id, string Layer, Point2d A, Point2d B, double Length, Vector2d Unit)>();

                    void CollectSegments()
                    {
                        ordinaryHorizontals.Clear();
                        ordinaryVerticals.Clear();
                        supportVerticals.Clear();
                        foreach (ObjectId id in ms)
                        {
                            Entity? ent = null;
                            try
                            {
                                ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                            }
                            catch (Autodesk.AutoCAD.Runtime.Exception)
                            {
                                continue;
                            }

                            if (ent == null || ent.IsErased)
                            {
                                continue;
                            }

                            if (!TryReadOpenSegment(ent, out var a, out var b) || !DoesSegmentIntersectAnyWindow(a, b))
                            {
                                continue;
                            }

                            var layer = ent.Layer ?? string.Empty;
                            var horizontal = IsHorizontalLikeForCorrectionLinePost(a, b);
                            var vertical = IsVerticalLikeForCorrectionLinePost(a, b);
                            if (!horizontal && !vertical)
                            {
                                continue;
                            }

                            var dir = b - a;
                            var len = dir.Length;
                            if (len <= 1e-6)
                            {
                                continue;
                            }

                            var unit = dir / len;
                            if (IsOrdinaryUsecLayer(layer))
                            {
                                if (horizontal)
                                {
                                    ordinaryHorizontals.Add((id, layer, a, b, len, Math.Min(a.X, b.X), Math.Max(a.X, b.X), unit));
                                }
                                else if (vertical)
                                {
                                    ordinaryVerticals.Add((id, layer, a, b, len, unit));
                                }
                            }

                            if (vertical &&
                                (IsOrdinaryUsecLayer(layer) ||
                                 string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(layer, "L-QSEC", StringComparison.OrdinalIgnoreCase)))
                            {
                                supportVerticals.Add((id, layer, a, b, len, unit));
                            }
                        }
                    }

                    const double endpointTol = 0.80;
                    const double parallelDotMin = 0.985;
                    const double minMove = 1.0;
                    const double moveTol = 0.05;
                    const double retreatTol = 1.80;
                    const double stationTol = 25.0;
                    const double pivotTol = 1.80;
                    const double anchorOffsetTol = 2.00;
                    const double currentOffsetTol = 2.00;
                    const double overlapMin = 60.0;

                    double HorizontalGapToPoint(
                        (ObjectId Id, string Layer, Point2d A, Point2d B, double Length, double MajorMin, double MajorMax, Vector2d Unit) row,
                        Point2d point)
                    {
                        return Math.Abs(DistancePointToInfiniteLine(point, row.A, row.B));
                    }

                    CollectSegments();
                    logger?.WriteLine(
                        $"CorrectionLine: final mixed 30.16/20.11 normalize candidates h={ordinaryHorizontals.Count}, v={ordinaryVerticals.Count}, supportV={supportVerticals.Count}.");

                    var adjustedVerticalEndpoints = 0;
                    var adjustedHorizontalEndpoints = 0;
                    var samples = new List<string>();

                    for (var i = 0; i < ordinaryVerticals.Count; i++)
                    {
                        var source = ordinaryVerticals[i];
                        if (!(tr.GetObject(source.Id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(writable, out var sourceA, out var sourceB) || !IsVerticalLikeForCorrectionLinePost(sourceA, sourceB))
                        {
                            continue;
                        }

                        for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                        {
                            var endpoint = endpointIndex == 0 ? sourceA : sourceB;
                            var other = endpointIndex == 0 ? sourceB : sourceA;
                            var inward = other - endpoint;
                            var inwardLen = inward.Length;
                            if (inwardLen <= 1e-6)
                            {
                                continue;
                            }

                            var inwardUnit = inward / inwardLen;
                            var foundTarget = false;
                            var bestTarget = endpoint;
                            var bestScore = double.MaxValue;

                            for (var ci = 0; ci < ordinaryHorizontals.Count; ci++)
                            {
                                var candidate = ordinaryHorizontals[ci];
                                if (!TryIntersectInfiniteLines(sourceA, sourceB, candidate.A, candidate.B, out var target))
                                {
                                    continue;
                                }

                                if (DistancePointToSegment(target, candidate.A, candidate.B) > endpointTol)
                                {
                                    continue;
                                }

                                var along = (target - endpoint).DotProduct(inwardUnit);
                                if (Math.Abs(along - RoadAllowanceSecWidthMeters) > retreatTol)
                                {
                                    continue;
                                }

                                var stationGap = Math.Min(
                                    Math.Min(Math.Abs(endpoint.X - candidate.A.X), Math.Abs(endpoint.X - candidate.B.X)),
                                    endpoint.X < candidate.MajorMin
                                        ? (candidate.MajorMin - endpoint.X)
                                        : endpoint.X > candidate.MajorMax
                                            ? (endpoint.X - candidate.MajorMax)
                                            : 0.0);
                                if (stationGap > stationTol)
                                {
                                    continue;
                                }

                                var foundNearRow = false;
                                var bestNearGap = double.MaxValue;
                                for (var ni = 0; ni < ordinaryHorizontals.Count; ni++)
                                {
                                    var near = ordinaryHorizontals[ni];
                                    if (near.Id == candidate.Id)
                                    {
                                        continue;
                                    }

                                    if (Math.Abs(near.Unit.DotProduct(candidate.Unit)) < parallelDotMin)
                                    {
                                        continue;
                                    }

                                    var overlap = Math.Min(near.MajorMax, candidate.MajorMax) - Math.Max(near.MajorMin, candidate.MajorMin);
                                    if (overlap < overlapMin)
                                    {
                                        continue;
                                    }

                                    var nearGap = HorizontalGapToPoint(near, endpoint);
                                    if (Math.Abs(nearGap - CorrectionLinePairGapMeters) > pivotTol)
                                    {
                                        continue;
                                    }

                                    foundNearRow = true;
                                    bestNearGap = Math.Min(bestNearGap, nearGap);
                                }

                                if (!foundNearRow)
                                {
                                    continue;
                                }

                                var score =
                                    Math.Abs(along - RoadAllowanceSecWidthMeters) +
                                    (0.04 * stationGap) +
                                    Math.Abs(bestNearGap - CorrectionLinePairGapMeters);
                                if (!foundTarget || score < bestScore - 1e-6)
                                {
                                    foundTarget = true;
                                    bestScore = score;
                                    bestTarget = target;
                                }
                            }

                            if (!foundTarget || endpoint.GetDistanceTo(bestTarget) <= minMove)
                            {
                                continue;
                            }

                            if (!TryMoveEndpoint(writable, endpointIndex == 0, bestTarget, moveTol))
                            {
                                continue;
                            }

                            adjustedVerticalEndpoints++;
                            if (samples.Count < 16)
                            {
                                samples.Add(
                                    string.Format(
                                        CultureInfo.InvariantCulture,
                                        "final-mixed-v id={0} ({1:0.###},{2:0.###})->({3:0.###},{4:0.###})",
                                        source.Id.Handle.ToString(),
                                        endpoint.X,
                                        endpoint.Y,
                                        bestTarget.X,
                                        bestTarget.Y));
                            }

                            if (!TryReadOpenSegment(writable, out sourceA, out sourceB))
                            {
                                break;
                            }
                        }
                    }

                    CollectSegments();
                    for (var i = 0; i < ordinaryHorizontals.Count; i++)
                    {
                        var source = ordinaryHorizontals[i];
                        if (!(tr.GetObject(source.Id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(writable, out var sourceA, out var sourceB) || !IsHorizontalLikeForCorrectionLinePost(sourceA, sourceB))
                        {
                            continue;
                        }

                        for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                        {
                            var endpoint = endpointIndex == 0 ? sourceA : sourceB;
                            var anchor = endpointIndex == 0 ? sourceB : sourceA;
                            var foundTarget = false;
                            var bestTarget = endpoint;
                            var bestScore = double.MaxValue;

                            for (var si = 0; si < supportVerticals.Count; si++)
                            {
                                var support = supportVerticals[si];
                                if (support.Id == source.Id || DistancePointToSegment(endpoint, support.A, support.B) > endpointTol)
                                {
                                    continue;
                                }

                                for (var ci = 0; ci < ordinaryHorizontals.Count; ci++)
                                {
                                    var candidate = ordinaryHorizontals[ci];
                                    if (candidate.Id == source.Id)
                                    {
                                        continue;
                                    }

                                    if (Math.Abs(candidate.Unit.DotProduct(source.Unit)) < parallelDotMin)
                                    {
                                        continue;
                                    }

                                    var overlap = Math.Min(source.MajorMax, candidate.MajorMax) - Math.Max(source.MajorMin, candidate.MajorMin);
                                    if (overlap < overlapMin)
                                    {
                                        continue;
                                    }

                                    var anchorOffset = HorizontalGapToPoint(candidate, anchor);
                                    if (Math.Abs(anchorOffset - RoadAllowanceUsecWidthMeters) > anchorOffsetTol)
                                    {
                                        continue;
                                    }

                                    var currentOffset = HorizontalGapToPoint(candidate, endpoint);
                                    if (Math.Abs(currentOffset - RoadAllowanceSecWidthMeters) > currentOffsetTol)
                                    {
                                        continue;
                                    }

                                    var candidateParallelPoint = anchor + candidate.Unit;
                                    if (!TryIntersectInfiniteLines(anchor, candidateParallelPoint, support.A, support.B, out var target))
                                    {
                                        continue;
                                    }

                                    if (DistancePointToSegment(target, support.A, support.B) > endpointTol)
                                    {
                                        continue;
                                    }

                                    var moveDistance = endpoint.GetDistanceTo(target);
                                    if (moveDistance <= minMove || Math.Abs(moveDistance - CorrectionLinePairGapMeters) > pivotTol)
                                    {
                                        continue;
                                    }

                                    var score =
                                        Math.Abs(anchorOffset - RoadAllowanceUsecWidthMeters) +
                                        Math.Abs(currentOffset - RoadAllowanceSecWidthMeters) +
                                        Math.Abs(moveDistance - CorrectionLinePairGapMeters);
                                    if (!foundTarget || score < bestScore - 1e-6)
                                    {
                                        foundTarget = true;
                                        bestScore = score;
                                        bestTarget = target;
                                    }
                                }
                            }

                            if (!foundTarget || endpoint.GetDistanceTo(bestTarget) <= minMove)
                            {
                                continue;
                            }

                            if (!TryMoveEndpointAnyDirection(writable, endpointIndex == 0, bestTarget, moveTol))
                            {
                                continue;
                            }

                            adjustedHorizontalEndpoints++;
                            if (samples.Count < 16)
                            {
                                samples.Add(
                                    string.Format(
                                        CultureInfo.InvariantCulture,
                                        "final-mixed-h id={0} ({1:0.###},{2:0.###})->({3:0.###},{4:0.###})",
                                        source.Id.Handle.ToString(),
                                        endpoint.X,
                                        endpoint.Y,
                                        bestTarget.X,
                                        bestTarget.Y));
                            }

                            if (!TryReadOpenSegment(writable, out sourceA, out sourceB))
                            {
                                break;
                            }
                        }
                    }

                    tr.Commit();
                    logger?.WriteLine(
                        $"CorrectionLine: final mixed 30.16/20.11 normalize verticalAdjusted={adjustedVerticalEndpoints}, horizontalAdjusted={adjustedHorizontalEndpoints}.");
                    for (var i = 0; i < samples.Count; i++)
                    {
                        logger?.WriteLine("CorrectionLine:   final-mixed-30-20 " + samples[i]);
                    }

                    return adjustedVerticalEndpoints > 0 || adjustedHorizontalEndpoints > 0;
                }
            }
            catch (System.Exception ex)
            {
                logger?.WriteLine("CorrectionLine: final mixed 30.16/20.11 normalize exception: " + ex.Message);
                return false;
            }
        }

        private static bool RetargetOrdinaryRoadAllowanceEndpointsToInnerBand(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger,
            ICollection<Point2d> adjustedTargets,
            ICollection<Point2d> adjustedCapTargets,
            ICollection<(Point2d Original, Point2d Target)> adjustedEndpointPairs)
        {
            if (database == null || requestedQuarterIds == null)
            {
                return false;
            }

            var clipWindows = MergeOverlappingClipWindows(BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0));
            if (clipWindows.Count == 0)
            {
                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b) => DoesSegmentIntersectAnyWindowForCorrectionLinePost(a, b, clipWindows);
            bool TryReadOpenSegment(Entity ent, out Point2d a, out Point2d b) => TryReadOpenLinearSegment(ent, out a, out b);
            bool TryMoveEndpoint(Entity writable, bool moveStart, Point2d target, double moveTol) =>
                TryMoveEndpointForCorrectionLinePost(writable, moveStart, target, moveTol);

            bool IsOrdinaryRoadAllowanceLayer(string layer)
            {
                return string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-0", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC2012", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC3018", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-3018", StringComparison.OrdinalIgnoreCase);
            }

                bool IsTwentyRoadAllowanceLayer(string layer)
                {
                    return string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(layer, "L-USEC2012", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase);
                }

                bool IsZeroRoadAllowanceLayer(string layer)
                {
                    return string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(layer, "L-USEC-0", StringComparison.OrdinalIgnoreCase);
                }

            bool IsQuarterDefinitionLayer(string layer)
            {
                return string.Equals(layer, "L-QUATER", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-QUARTER", StringComparison.OrdinalIgnoreCase);
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                var segments = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, bool Horizontal, bool Vertical, double Length, Vector2d Unit)>();

                foreach (ObjectId id in ms)
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    if (!IsOrdinaryRoadAllowanceLayer(layer) ||
                        !TryReadOpenSegment(ent, out var a, out var b) ||
                        !DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    var horizontal = IsHorizontalLikeForCorrectionLinePost(a, b);
                    var vertical = IsVerticalLikeForCorrectionLinePost(a, b);
                    if (!horizontal && !vertical)
                    {
                        continue;
                    }

                    var direction = b - a;
                    var length = direction.Length;
                    if (length <= 1e-6)
                    {
                        continue;
                    }

                    segments.Add((id, layer, a, b, horizontal, vertical, length, direction / length));
                }

                if (segments.Count == 0)
                {
                    tr.Commit();
                    return false;
                }

                const double endpointTol = 0.80;
                const double axisTol = 0.80;
                const double sourceSpanTol = 0.80;
                const double parallelDotMin = 0.985;
                const double perpendicularDotMax = 0.25;
                const double minSupportLength = 40.0;
                const double moveTol = 0.05;
                const double minMove = 1.0;
                const double bandTol = 1.80;
                const double targetBand = RoadAllowanceSecWidthMeters;

                bool IsPerpendicular(int sourceIndex, int targetIndex)
                {
                    return Math.Abs(segments[sourceIndex].Unit.DotProduct(segments[targetIndex].Unit)) <= perpendicularDotMax;
                }

                bool EndpointTouchesSegmentEndpoint(Point2d endpoint, int segmentIndex)
                {
                    var segment = segments[segmentIndex];
                    return endpoint.GetDistanceTo(segment.A) <= endpointTol ||
                           endpoint.GetDistanceTo(segment.B) <= endpointTol;
                }

                bool TryFindInnerBandStop(int sourceIndex, Point2d endpoint, Point2d other, out Point2d target, out double score)
                {
                    target = endpoint;
                    score = double.MaxValue;
                    var source = segments[sourceIndex];
                    var inward = other - endpoint;
                    var inwardLength = inward.Length;
                    if (inwardLength <= 1e-6)
                    {
                        return false;
                    }

                    var inwardUnit = inward / inwardLength;
                    var currentSupports = new List<int>();
                    for (var i = 0; i < segments.Count; i++)
                    {
                        if (i == sourceIndex ||
                            segments[i].Length < minSupportLength ||
                            !IsPerpendicular(sourceIndex, i) ||
                            !EndpointTouchesSegmentEndpoint(endpoint, i))
                        {
                            continue;
                        }

                        currentSupports.Add(i);
                    }

                    if (currentSupports.Count == 0)
                    {
                        return false;
                    }

                    var found = false;
                    for (var i = 0; i < segments.Count; i++)
                    {
                        if (i == sourceIndex || segments[i].Length < minSupportLength || !IsPerpendicular(sourceIndex, i))
                        {
                            continue;
                        }

                        var candidateSupport = segments[i];
                        var hasParallelCurrentSupport = currentSupports.Any(currentIndex =>
                            Math.Abs(segments[currentIndex].Unit.DotProduct(candidateSupport.Unit)) >= parallelDotMin);
                        if (!hasParallelCurrentSupport)
                        {
                            continue;
                        }

                        var candidates = new[] { candidateSupport.A, candidateSupport.B };
                        for (var candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++)
                        {
                            var candidate = candidates[candidateIndex];
                            if (candidate.GetDistanceTo(endpoint) <= endpointTol)
                            {
                                continue;
                            }

                            var along = (candidate - endpoint).DotProduct(inwardUnit);
                            if (Math.Abs(along - targetBand) > bandTol)
                            {
                                continue;
                            }

                            if (DistancePointToInfiniteLine(candidate, source.A, source.B) > axisTol ||
                                DistancePointToSegment(candidate, source.A, source.B) > sourceSpanTol)
                            {
                                continue;
                            }

                            var candidateScore =
                                Math.Abs(along - targetBand) +
                                DistancePointToInfiniteLine(candidate, source.A, source.B);
                            if (candidateScore >= score)
                            {
                                continue;
                            }

                            found = true;
                            target = candidate;
                            score = candidateScore;
                        }
                    }

                    return found;
                }

                var plans = new List<(ObjectId Id, bool MoveStart, Point2d Original, Point2d Target, string Layer)>();
                for (var i = 0; i < segments.Count; i++)
                {
                    var source = segments[i];
                    for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                    {
                        var endpoint = endpointIndex == 0 ? source.A : source.B;
                        var other = endpointIndex == 0 ? source.B : source.A;
                        if (!TryFindInnerBandStop(i, endpoint, other, out var target, out _) ||
                            endpoint.GetDistanceTo(target) <= minMove)
                        {
                            continue;
                        }

                        plans.Add((source.Id, endpointIndex == 0, endpoint, target, source.Layer));
                    }
                }

                if (plans.Count == 0)
                {
                    tr.Commit();
                    return false;
                }

                var hasTwentyBandPlan = plans.Any(plan => IsTwentyRoadAllowanceLayer(plan.Layer));
                var adjusted = 0;
                var quarterAdjusted = 0;
                var samples = new List<string>();
                var appliedPlans = new List<(Point2d Original, Point2d Target)>();
                foreach (var plan in plans
                    .OrderBy(plan => plan.Original.GetDistanceTo(plan.Target))
                    .ThenBy(plan => plan.Id.Handle.ToString()))
                {
                    if (IsZeroRoadAllowanceLayer(plan.Layer) && !hasTwentyBandPlan)
                    {
                        continue;
                    }

                    if (!(tr.GetObject(plan.Id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        continue;
                    }

                    if (!TryMoveEndpoint(writable, plan.MoveStart, plan.Target, moveTol))
                    {
                        continue;
                    }

                    adjusted++;
                    adjustedTargets.Add(plan.Target);
                    if (IsTwentyRoadAllowanceLayer(plan.Layer))
                    {
                        adjustedCapTargets.Add(plan.Target);
                    }
                    adjustedEndpointPairs.Add((plan.Original, plan.Target));
                    appliedPlans.Add((plan.Original, plan.Target));
                    if (samples.Count < 24)
                    {
                        samples.Add(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "{0}/{1} {2:0.###},{3:0.###}->{4:0.###},{5:0.###}",
                                plan.Id.Handle.ToString(),
                                plan.Layer,
                                plan.Original.X,
                                plan.Original.Y,
                                plan.Target.X,
                                plan.Target.Y));
                    }
                }

                if (appliedPlans.Count > 0)
                {
                    foreach (ObjectId id in ms)
                    {
                        if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) ||
                            ent.IsErased ||
                            !IsQuarterDefinitionLayer(ent.Layer ?? string.Empty) ||
                            !TryReadOpenSegment(ent, out var a, out var b) ||
                            !DoesSegmentIntersectAnyWindow(a, b))
                        {
                            continue;
                        }

                        var moveStart = false;
                        var moveEnd = false;
                        var startTarget = a;
                        var endTarget = b;
                        for (var planIndex = 0; planIndex < appliedPlans.Count; planIndex++)
                        {
                            var plan = appliedPlans[planIndex];
                            if (!moveStart &&
                                a.GetDistanceTo(plan.Original) <= endpointTol &&
                                a.GetDistanceTo(plan.Target) > moveTol)
                            {
                                startTarget = plan.Target;
                                moveStart = true;
                            }

                            if (!moveEnd &&
                                b.GetDistanceTo(plan.Original) <= endpointTol &&
                                b.GetDistanceTo(plan.Target) > moveTol)
                            {
                                endTarget = plan.Target;
                                moveEnd = true;
                            }

                            if (moveStart && moveEnd)
                            {
                                break;
                            }
                        }

                        if (!moveStart && !moveEnd)
                        {
                            continue;
                        }

                        if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity writableQuarter) ||
                            writableQuarter.IsErased)
                        {
                            continue;
                        }

                        if (moveStart && TryMoveEndpoint(writableQuarter, moveStart: true, startTarget, moveTol))
                        {
                            quarterAdjusted++;
                        }

                        if (moveEnd && TryMoveEndpoint(writableQuarter, moveStart: false, endTarget, moveTol))
                        {
                            quarterAdjusted++;
                        }
                    }
                }

                tr.Commit();
                if (adjusted > 0 || quarterAdjusted > 0)
                {
                    logger?.WriteLine($"Cleanup: inner-band endpoint retarget adjusted {adjusted} ordinary road endpoint(s) and {quarterAdjusted} quarter endpoint(s).");
                    for (var i = 0; i < samples.Count; i++)
                    {
                        logger?.WriteLine("Cleanup:   inner-band-retarget " + samples[i]);
                    }
                }

                return adjusted > 0 || quarterAdjusted > 0;
            }
        }

        private static bool RetargetQuarterDefinitionEndpointsToAdjustedInnerBand(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger,
            IReadOnlyCollection<(Point2d Original, Point2d Target)> adjustedEndpointPairs)
        {
            if (database == null || requestedQuarterIds == null || adjustedEndpointPairs == null || adjustedEndpointPairs.Count == 0)
            {
                return false;
            }

            var clipWindows = MergeOverlappingClipWindows(BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0));
            if (clipWindows.Count == 0)
            {
                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b) => DoesSegmentIntersectAnyWindowForCorrectionLinePost(a, b, clipWindows);
            bool TryReadOpenSegment(Entity ent, out Point2d a, out Point2d b) => TryReadOpenLinearSegment(ent, out a, out b);
            bool TryMoveEndpoint(Entity writable, bool moveStart, Point2d target, double moveTol) =>
                TryMoveEndpointForCorrectionLinePost(writable, moveStart, target, moveTol);

            bool IsQuarterDefinitionLayer(string layer)
            {
                return string.Equals(layer, "L-QUATER", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-QUARTER", StringComparison.OrdinalIgnoreCase);
            }

            bool DoesQuarterPolylineIntersectAnyWindow(Polyline polyline)
            {
                if (polyline == null || polyline.NumberOfVertices < 2)
                {
                    return false;
                }

                var segmentCount = polyline.Closed
                    ? polyline.NumberOfVertices
                    : polyline.NumberOfVertices - 1;
                for (var vertexIndex = 0; vertexIndex < segmentCount; vertexIndex++)
                {
                    var a = polyline.GetPoint2dAt(vertexIndex);
                    var b = polyline.GetPoint2dAt((vertexIndex + 1) % polyline.NumberOfVertices);
                    if (DoesSegmentIntersectAnyWindow(a, b))
                    {
                        return true;
                    }
                }

                return false;
            }

            const double endpointTol = 0.80;
            const double moveTol = 0.05;
            var adjusted = 0;
            var samples = new List<string>();

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) ||
                        ent.IsErased ||
                        !IsQuarterDefinitionLayer(ent.Layer ?? string.Empty))
                    {
                        continue;
                    }

                    if (ent is Polyline polyline)
                    {
                        if (!DoesQuarterPolylineIntersectAnyWindow(polyline))
                        {
                            continue;
                        }

                        var vertexTargets = new Dictionary<int, Point2d>();
                        for (var vertexIndex = 0; vertexIndex < polyline.NumberOfVertices; vertexIndex++)
                        {
                            var vertex = polyline.GetPoint2dAt(vertexIndex);
                            foreach (var pair in adjustedEndpointPairs)
                            {
                                if (vertex.GetDistanceTo(pair.Original) <= endpointTol &&
                                    vertex.GetDistanceTo(pair.Target) > moveTol)
                                {
                                    vertexTargets[vertexIndex] = pair.Target;
                                    break;
                                }
                            }
                        }

                        if (vertexTargets.Count == 0)
                        {
                            continue;
                        }

                        if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Polyline writablePolyline) ||
                            writablePolyline.IsErased)
                        {
                            continue;
                        }

                        foreach (var target in vertexTargets)
                        {
                            var oldVertex = writablePolyline.GetPoint2dAt(target.Key);
                            writablePolyline.SetPointAt(target.Key, target.Value);
                            adjusted++;
                            if (samples.Count < 16)
                            {
                                samples.Add(
                                    string.Format(
                                        CultureInfo.InvariantCulture,
                                        "{0} vertex={1} {2:0.###},{3:0.###}->{4:0.###},{5:0.###}",
                                        id.Handle.ToString(),
                                        target.Key,
                                        oldVertex.X,
                                        oldVertex.Y,
                                        target.Value.X,
                                        target.Value.Y));
                            }
                        }

                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b) ||
                        !DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    var moveStart = false;
                    var moveEnd = false;
                    var startTarget = a;
                    var endTarget = b;
                    foreach (var pair in adjustedEndpointPairs)
                    {
                        if (!moveStart &&
                            a.GetDistanceTo(pair.Original) <= endpointTol &&
                            a.GetDistanceTo(pair.Target) > moveTol)
                        {
                            startTarget = pair.Target;
                            moveStart = true;
                        }

                        if (!moveEnd &&
                            b.GetDistanceTo(pair.Original) <= endpointTol &&
                            b.GetDistanceTo(pair.Target) > moveTol)
                        {
                            endTarget = pair.Target;
                            moveEnd = true;
                        }

                        if (moveStart && moveEnd)
                        {
                            break;
                        }
                    }

                    if (!moveStart && !moveEnd)
                    {
                        continue;
                    }

                    if (!(tr.GetObject(id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        continue;
                    }

                    if (moveStart && TryMoveEndpoint(writable, moveStart: true, startTarget, moveTol))
                    {
                        adjusted++;
                    }

                    if (moveEnd && TryMoveEndpoint(writable, moveStart: false, endTarget, moveTol))
                    {
                        adjusted++;
                    }

                    if (samples.Count < 16)
                    {
                        samples.Add(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "{0} {1:0.###},{2:0.###}->{3:0.###},{4:0.###} / {5:0.###},{6:0.###}->{7:0.###},{8:0.###}",
                                id.Handle.ToString(),
                                a.X,
                                a.Y,
                                startTarget.X,
                                startTarget.Y,
                                b.X,
                                b.Y,
                                endTarget.X,
                                endTarget.Y));
                    }
                }

                tr.Commit();
            }

            if (adjusted > 0)
            {
                logger?.WriteLine($"Cleanup: inner-band quarter endpoint retarget adjusted {adjusted} quarter endpoint(s).");
                for (var i = 0; i < samples.Count; i++)
                {
                    logger?.WriteLine("Cleanup:   inner-band-quarter-retarget " + samples[i]);
                }
            }

            return adjusted > 0;
        }

        private static bool RepairMixedBandVerticalRoadAllowanceCompanions(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null)
            {
                return false;
            }

            var clipWindows = MergeOverlappingClipWindows(BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0));
            if (clipWindows.Count == 0)
            {
                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b) => DoesSegmentIntersectAnyWindowForCorrectionLinePost(a, b, clipWindows);
            bool TryReadOpenSegment(Entity ent, out Point2d a, out Point2d b) => TryReadOpenLinearSegment(ent, out a, out b);

            bool IsZeroLayer(string layer)
            {
                return string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-0", StringComparison.OrdinalIgnoreCase);
            }

            bool IsTwentyLayer(string layer)
            {
                return string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC2012", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase);
            }

            bool IsThirtyLayer(string layer)
            {
                return string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC3018", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-3018", StringComparison.OrdinalIgnoreCase);
            }

            bool IsOrdinaryLayer(string layer)
            {
                return string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC", StringComparison.OrdinalIgnoreCase) ||
                       IsZeroLayer(layer) ||
                       IsTwentyLayer(layer) ||
                       IsThirtyLayer(layer);
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var verticals = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, double Length)>();
                var shortHorizontalCaps = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, double Length)>();

                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) ||
                        ent.IsErased ||
                        !TryReadOpenSegment(ent, out var a, out var b) ||
                        !DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    if (!IsOrdinaryLayer(layer))
                    {
                        continue;
                    }

                    var length = a.GetDistanceTo(b);
                    if (IsHorizontalLikeForCorrectionLinePost(a, b) &&
                        length >= 8.0 &&
                        length <= 35.0)
                    {
                        shortHorizontalCaps.Add((id, layer, a, b, length));
                    }

                    if (!IsVerticalLikeForCorrectionLinePost(a, b))
                    {
                        continue;
                    }

                    if (length < 200.0)
                    {
                        continue;
                    }

                    verticals.Add((id, layer, a, b, length));
                }

                if (verticals.Count == 0)
                {
                    tr.Commit();
                    return false;
                }

                const double parallelDotMin = 0.995;
                const double stationTol = 35.0;
                const double axisTol = 1.25;
                const double twentyMin = 18.0;
                const double twentyMax = 22.5;
                const double thirtyMin = 28.0;
                const double thirtyMax = 32.5;
                const double bandGapTarget = CorrectionLinePairGapMeters;
                const double bandGapTol = 2.0;
                const double duplicateTol = 0.75;
                const double moveTol = 0.05;

                bool HasNearOrdinarySegment(Point2d a, Point2d b)
                {
                    return verticals.Any(segment =>
                        Math.Min(
                            segment.A.GetDistanceTo(a) + segment.B.GetDistanceTo(b),
                            segment.A.GetDistanceTo(b) + segment.B.GetDistanceTo(a)) <= duplicateTol);
                }

                bool HasShortCapAtEndpoint(Point2d point)
                {
                    const double capEndpointTol = 0.80;
                    return shortHorizontalCaps.Any(cap =>
                        cap.A.GetDistanceTo(point) <= capEndpointTol ||
                        cap.B.GetDistanceTo(point) <= capEndpointTol);
                }

                var repairs = new List<(ObjectId Id, bool MoveStart, Point2d Original, Point2d Target, Point2d TwentyA, Point2d TwentyB)>();
                foreach (var baseSegment in verticals.Where(v => IsZeroLayer(v.Layer) || HasShortCapAtEndpoint(v.A) || HasShortCapAtEndpoint(v.B)))
                {
                    var baseIsExplicitZero = IsZeroLayer(baseSegment.Layer);
                    var baseHasShortCap = HasShortCapAtEndpoint(baseSegment.A) || HasShortCapAtEndpoint(baseSegment.B);
                    var baseDir = baseSegment.B - baseSegment.A;
                    var baseLen = baseDir.Length;
                    if (baseLen <= 1e-6)
                    {
                        continue;
                    }

                    var baseUnit = baseDir / baseLen;
                    var normal = new Vector2d(-baseUnit.Y, baseUnit.X);
                    foreach (var candidate in verticals.Where(v =>
                        v.Id != baseSegment.Id &&
                        (!IsZeroLayer(v.Layer) || baseHasShortCap) &&
                        (baseIsExplicitZero && !baseHasShortCap || (!HasShortCapAtEndpoint(v.A) && !HasShortCapAtEndpoint(v.B)))))
                    {
                        var candidateDir = candidate.B - candidate.A;
                        var candidateLen = candidateDir.Length;
                        if (candidateLen <= 1e-6 ||
                            Math.Abs((candidateDir / candidateLen).DotProduct(baseUnit)) < parallelDotMin)
                        {
                            continue;
                        }

                        var endpoints = new[]
                        {
                            (MoveStart: true, Point: candidate.A),
                            (MoveStart: false, Point: candidate.B)
                        };

                        var resolved = endpoints
                            .Select(endpoint =>
                            {
                                var along = (endpoint.Point - baseSegment.A).DotProduct(baseUnit);
                                var basePoint = baseSegment.A + (baseUnit * along);
                                var offset = (endpoint.Point - basePoint).DotProduct(normal);
                                return new
                                {
                                    endpoint.MoveStart,
                                    endpoint.Point,
                                    Along = along,
                                    BasePoint = basePoint,
                                    Offset = offset,
                                    AbsOffset = Math.Abs(offset),
                                    AxisGap = endpoint.Point.GetDistanceTo(basePoint)
                                };
                            })
                            .ToList();

                        if (resolved.Count != 2 ||
                            resolved.Any(r => r.Along < -stationTol || r.Along > baseLen + stationTol) ||
                            resolved.Any(r => Math.Abs(r.AxisGap - r.AbsOffset) > axisTol) ||
                            Math.Sign(resolved[0].Offset) != Math.Sign(resolved[1].Offset))
                        {
                            continue;
                        }

                        var twentyEndpoint = resolved.OrderBy(r => r.AbsOffset).First();
                        var thirtyEndpoint = resolved.OrderByDescending(r => r.AbsOffset).First();
                        if (twentyEndpoint.AbsOffset < twentyMin ||
                            twentyEndpoint.AbsOffset > twentyMax ||
                            thirtyEndpoint.AbsOffset < thirtyMin ||
                            thirtyEndpoint.AbsOffset > thirtyMax ||
                            Math.Abs((thirtyEndpoint.AbsOffset - twentyEndpoint.AbsOffset) - bandGapTarget) > bandGapTol)
                        {
                            continue;
                        }

                        var sign = Math.Sign(thirtyEndpoint.Offset);
                        var thirtyTarget = twentyEndpoint.BasePoint + (normal * (sign * thirtyEndpoint.AbsOffset));
                        var twentyAtThirtyStation = thirtyEndpoint.BasePoint + (normal * (sign * twentyEndpoint.AbsOffset));
                        var twentyAtTwentyStation = twentyEndpoint.BasePoint + (normal * (sign * twentyEndpoint.AbsOffset));
                        if (twentyEndpoint.Point.GetDistanceTo(twentyAtTwentyStation) > axisTol ||
                            thirtyEndpoint.Point.GetDistanceTo(thirtyEndpoint.BasePoint + (normal * thirtyEndpoint.Offset)) > axisTol ||
                            HasNearOrdinarySegment(twentyAtThirtyStation, twentyAtTwentyStation) ||
                            repairs.Any(repair =>
                                Math.Min(
                                    repair.TwentyA.GetDistanceTo(twentyAtThirtyStation) + repair.TwentyB.GetDistanceTo(twentyAtTwentyStation),
                                    repair.TwentyA.GetDistanceTo(twentyAtTwentyStation) + repair.TwentyB.GetDistanceTo(twentyAtThirtyStation)) <= duplicateTol))
                        {
                            continue;
                        }

                        repairs.Add((candidate.Id, twentyEndpoint.MoveStart, twentyEndpoint.Point, thirtyTarget, twentyAtThirtyStation, twentyAtTwentyStation));
                    }
                }

                if (repairs.Count == 0)
                {
                    tr.Commit();
                    return false;
                }

                EnsureLayerWithColor(database, tr, LayerUsecTwenty, ResolveUsecLayerColorIndex(LayerUsecTwenty));
                var adjusted = 0;
                var created = 0;
                var samples = new List<string>();
                foreach (var repair in repairs.OrderBy(r => r.Original.GetDistanceTo(r.Target)))
                {
                    if (!(tr.GetObject(repair.Id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        continue;
                    }

                    if (TryMoveEndpointForCorrectionLinePost(writable, repair.MoveStart, repair.Target, moveTol))
                    {
                        adjusted++;
                    }

                    var connector = new Line(
                        new Point3d(repair.TwentyA.X, repair.TwentyA.Y, 0.0),
                        new Point3d(repair.TwentyB.X, repair.TwentyB.Y, 0.0))
                    {
                        Layer = LayerUsecTwenty,
                        ColorIndex = 256
                    };
                    ms.AppendEntity(connector);
                    tr.AddNewlyCreatedDBObject(connector, true);
                    created++;

                    if (samples.Count < 16)
                    {
                        samples.Add(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "thirty {0:0.###},{1:0.###}->{2:0.###},{3:0.###}; twenty {4:0.###},{5:0.###}->{6:0.###},{7:0.###}",
                                repair.Original.X,
                                repair.Original.Y,
                                repair.Target.X,
                                repair.Target.Y,
                                repair.TwentyA.X,
                                repair.TwentyA.Y,
                                repair.TwentyB.X,
                                repair.TwentyB.Y));
                    }
                }

                tr.Commit();

                if (adjusted > 0 || created > 0)
                {
                    logger?.WriteLine($"Cleanup: mixed-band vertical road companion repair adjusted={adjusted}, createdTwenty={created}.");
                    for (var i = 0; i < samples.Count; i++)
                    {
                        logger?.WriteLine("Cleanup:   mixed-band-vertical " + samples[i]);
                    }
                }

                return adjusted > 0 || created > 0;
            }
        }

        private static bool TrimOrdinaryVerticalEndpointsToCorrectionOuterRows(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null)
            {
                return false;
            }

            var clipWindows = MergeOverlappingClipWindows(BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0));
            if (clipWindows.Count == 0)
            {
                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b) => DoesSegmentIntersectAnyWindowForCorrectionLinePost(a, b, clipWindows);
            bool TryReadOpenSegment(Entity ent, out Point2d a, out Point2d b) => TryReadOpenLinearSegment(ent, out a, out b);

            bool IsOrdinaryLayer(string layer)
            {
                return string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-0", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC2012", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC3018", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-3018", StringComparison.OrdinalIgnoreCase);
            }

            bool IsZeroLayer(string layer)
            {
                return string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-0", StringComparison.OrdinalIgnoreCase);
            }

            Point2d ClosestPointOnSegment(Point2d p, Point2d a, Point2d b)
            {
                var ab = b - a;
                var abLen2 = ab.DotProduct(ab);
                if (abLen2 <= 1e-9)
                {
                    return a;
                }

                var t = (p - a).DotProduct(ab) / abLen2;
                t = Math.Max(0.0, Math.Min(1.0, t));
                return a + (ab * t);
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                var ordinaryVerticals = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, double Length)>();
                var correctionOuterRows = new List<(Point2d A, Point2d B)>();
                var correctionZeroRows = new List<(Point2d A, Point2d B)>();

                foreach (ObjectId id in ms)
                {
                    if (!(tr.GetObject(id, OpenMode.ForRead, false) is Entity ent) ||
                        ent.IsErased ||
                        !TryReadOpenSegment(ent, out var a, out var b) ||
                        !DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    if (string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase) &&
                        IsHorizontalLikeForCorrectionLinePost(a, b))
                    {
                        correctionOuterRows.Add((a, b));
                        continue;
                    }

                    if (string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) &&
                        IsHorizontalLikeForCorrectionLinePost(a, b))
                    {
                        correctionZeroRows.Add((a, b));
                        continue;
                    }

                    if (IsOrdinaryLayer(layer) && IsVerticalLikeForCorrectionLinePost(a, b))
                    {
                        var length = a.GetDistanceTo(b);
                        if (length >= 25.0)
                        {
                            ordinaryVerticals.Add((id, layer, a, b, length));
                        }
                    }
                }

                if (ordinaryVerticals.Count == 0 || correctionOuterRows.Count == 0 || correctionZeroRows.Count == 0)
                {
                    tr.Commit();
                    return false;
                }

                const double maxMove = 40.0;
                const double minMove = 0.10;
                const double axisTol = 1.0;
                const double correctionZeroNearbyTol = 12.0;
                const double minRemainingLength = 20.0;
                const double zeroRowOvershootMoveMin = 10.0;
                const double moveTol = 0.05;

                bool EndpointNearCorrectionZero(Point2d endpoint)
                {
                    return correctionZeroRows.Any(row => DistancePointToSegment(endpoint, row.A, row.B) <= correctionZeroNearbyTol);
                }

                var plans = new List<(ObjectId Id, bool MoveStart, Point2d Original, Point2d Target)>();
                foreach (var vertical in ordinaryVerticals)
                {
                    var endpoints = new[] { (MoveStart: true, Point: vertical.A, Other: vertical.B), (MoveStart: false, Point: vertical.B, Other: vertical.A) };
                    foreach (var endpoint in endpoints)
                    {
                        if (!EndpointNearCorrectionZero(endpoint.Point))
                        {
                            continue;
                        }

                        var direction = endpoint.Other - endpoint.Point;
                        var directionLen = direction.Length;
                        if (directionLen <= 1e-6)
                        {
                            continue;
                        }

                        var unit = direction / directionLen;
                        var bestTarget = endpoint.Point;
                        var bestMove = double.MaxValue;
                        foreach (var row in correctionOuterRows)
                        {
                            var candidate = ClosestPointOnSegment(endpoint.Point, row.A, row.B);
                            var move = endpoint.Point.GetDistanceTo(candidate);
                            if (move < minMove || move > maxMove)
                            {
                                continue;
                            }

                            var along = (candidate - endpoint.Point).DotProduct(unit);
                            if (along <= minMove || along >= directionLen - minRemainingLength)
                            {
                                continue;
                            }

                            if (DistancePointToInfiniteLine(candidate, endpoint.Point, endpoint.Other) > axisTol)
                            {
                                continue;
                            }

                            if (move < bestMove)
                            {
                                bestMove = move;
                                bestTarget = candidate;
                            }
                        }

                        if (bestMove < double.MaxValue)
                        {
                            if (IsZeroLayer(vertical.Layer) && bestMove < zeroRowOvershootMoveMin)
                            {
                                continue;
                            }

                            plans.Add((vertical.Id, endpoint.MoveStart, endpoint.Point, bestTarget));
                        }
                    }
                }

                if (plans.Count == 0)
                {
                    tr.Commit();
                    return false;
                }

                var adjusted = 0;
                var samples = new List<string>();
                foreach (var plan in plans.OrderBy(p => p.Original.GetDistanceTo(p.Target)))
                {
                    if (!(tr.GetObject(plan.Id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        continue;
                    }

                    if (!TryMoveEndpointForCorrectionLinePost(writable, plan.MoveStart, plan.Target, moveTol))
                    {
                        continue;
                    }

                    adjusted++;
                    if (samples.Count < 16)
                    {
                        samples.Add(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "{0:0.###},{1:0.###}->{2:0.###},{3:0.###}",
                                plan.Original.X,
                                plan.Original.Y,
                                plan.Target.X,
                                plan.Target.Y));
                    }
                }

                tr.Commit();

                if (adjusted > 0)
                {
                    logger?.WriteLine($"Cleanup: correction outer vertical endpoint trim adjusted {adjusted} endpoint(s).");
                    for (var i = 0; i < samples.Count; i++)
                    {
                        logger?.WriteLine("Cleanup:   correction-outer-vertical-trim " + samples[i]);
                    }
                }

                return adjusted > 0;
            }
        }

        private static bool ExtendShortOrdinaryRoadBoundaryStubsToAnchoredContinuation(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null)
            {
                return false;
            }

            var clipWindows = MergeOverlappingClipWindows(BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0));
            if (clipWindows.Count == 0)
            {
                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b) => DoesSegmentIntersectAnyWindowForCorrectionLinePost(a, b, clipWindows);
            bool TryReadOpenSegment(Entity ent, out Point2d a, out Point2d b) => TryReadOpenLinearSegment(ent, out a, out b);
            bool TryMoveEndpoint(Entity writable, bool moveStart, Point2d target, double moveTol) =>
                TryMoveEndpointForCorrectionLinePost(writable, moveStart, target, moveTol);

            bool IsOrdinaryRoadAllowanceLayer(string layer)
            {
                return string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-0", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC2012", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC3018", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-3018", StringComparison.OrdinalIgnoreCase);
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                var roadSegments = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, bool Horizontal, bool Vertical, double Length, Vector2d Unit)>();
                var anchoredEndpoints = new List<Point2d>();

                foreach (ObjectId id in ms)
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased ||
                        !TryReadOpenSegment(ent, out var a, out var b) ||
                        !DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    if (string.Equals(layer, "L-QSEC", StringComparison.OrdinalIgnoreCase))
                    {
                        anchoredEndpoints.Add(a);
                        anchoredEndpoints.Add(b);
                    }

                    if (!IsOrdinaryRoadAllowanceLayer(layer))
                    {
                        continue;
                    }

                    var horizontal = IsHorizontalLikeForCorrectionLinePost(a, b);
                    var vertical = IsVerticalLikeForCorrectionLinePost(a, b);
                    if (!horizontal && !vertical)
                    {
                        continue;
                    }

                    var direction = b - a;
                    var length = direction.Length;
                    if (length <= 1e-6)
                    {
                        continue;
                    }

                    roadSegments.Add((id, layer, a, b, horizontal, vertical, length, direction / length));
                }

                if (roadSegments.Count == 0 || anchoredEndpoints.Count == 0)
                {
                    tr.Commit();
                    return false;
                }

                const double shortStubMax = 35.0;
                const double continuationMin = 80.0;
                const double maxExtend = 1200.0;
                const double minExtend = 40.0;
                const double axisTol = 0.75;
                const double endpointTol = 0.85;
                const double parallelDotMin = 0.985;
                const double moveTol = 0.05;

                bool EndpointIsAnchored(Point2d point)
                {
                    return anchoredEndpoints.Any(anchor => anchor.GetDistanceTo(point) <= endpointTol);
                }

                bool TryFindContinuationStop(
                    int sourceIndex,
                    Point2d anchor,
                    Point2d endpoint,
                    out Point2d target,
                    out double score)
                {
                    target = endpoint;
                    score = double.MaxValue;
                    var source = roadSegments[sourceIndex];
                    var direction = endpoint - anchor;
                    var directionLength = direction.Length;
                    if (directionLength <= 1e-6)
                    {
                        return false;
                    }

                    var unit = direction / directionLength;
                    var found = false;
                    for (var i = 0; i < roadSegments.Count; i++)
                    {
                        if (i == sourceIndex)
                        {
                            continue;
                        }

                        var candidate = roadSegments[i];
                        if (candidate.Length < continuationMin ||
                            candidate.Horizontal != source.Horizontal ||
                            candidate.Vertical != source.Vertical ||
                            Math.Abs(candidate.Unit.DotProduct(source.Unit)) < parallelDotMin)
                        {
                            continue;
                        }

                        var candidateEndpoints = new[] { candidate.A, candidate.B };
                        for (var endpointIndex = 0; endpointIndex < candidateEndpoints.Length; endpointIndex++)
                        {
                            var candidatePoint = candidateEndpoints[endpointIndex];
                            var along = (candidatePoint - endpoint).DotProduct(unit);
                            if (along < minExtend || along > maxExtend)
                            {
                                continue;
                            }

                            if (DistancePointToInfiniteLine(candidatePoint, source.A, source.B) > axisTol ||
                                !EndpointIsAnchored(candidatePoint))
                            {
                                continue;
                            }

                            var candidateScore =
                                DistancePointToInfiniteLine(candidatePoint, source.A, source.B) +
                                (0.001 * along);
                            if (candidateScore >= score)
                            {
                                continue;
                            }

                            found = true;
                            target = candidatePoint;
                            score = candidateScore;
                        }
                    }

                    return found;
                }

                var plans = new List<(ObjectId Id, bool MoveStart, Point2d Original, Point2d Target, string Layer)>();
                for (var i = 0; i < roadSegments.Count; i++)
                {
                    var source = roadSegments[i];
                    if (source.Length > shortStubMax)
                    {
                        continue;
                    }

                    if (TryFindContinuationStop(i, source.B, source.A, out var startTarget, out _))
                    {
                        plans.Add((source.Id, true, source.A, startTarget, source.Layer));
                    }

                    if (TryFindContinuationStop(i, source.A, source.B, out var endTarget, out _))
                    {
                        plans.Add((source.Id, false, source.B, endTarget, source.Layer));
                    }
                }

                if (plans.Count == 0)
                {
                    tr.Commit();
                    return false;
                }

                var adjusted = 0;
                var samples = new List<string>();
                foreach (var plan in plans
                    .OrderBy(plan => plan.Original.GetDistanceTo(plan.Target))
                    .ThenBy(plan => plan.Id.Handle.ToString()))
                {
                    if (!(tr.GetObject(plan.Id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                    {
                        continue;
                    }

                    if (!TryMoveEndpoint(writable, plan.MoveStart, plan.Target, moveTol))
                    {
                        continue;
                    }

                    adjusted++;
                    if (samples.Count < 20)
                    {
                        samples.Add(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "{0}/{1} {2:0.###},{3:0.###}->{4:0.###},{5:0.###}",
                                plan.Id.Handle.ToString(),
                                plan.Layer,
                                plan.Original.X,
                                plan.Original.Y,
                                plan.Target.X,
                                plan.Target.Y));
                    }
                }

                tr.Commit();
                if (adjusted > 0)
                {
                    logger?.WriteLine($"Cleanup: short road-boundary stub extension adjusted {adjusted} endpoint(s).");
                    for (var i = 0; i < samples.Count; i++)
                    {
                        logger?.WriteLine("Cleanup:   short-stub-extension " + samples[i]);
                    }
                }

                return adjusted > 0;
            }
        }

        private static bool AddMissingShortTwentyBandCapsAtRoadCorners(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger,
            IReadOnlyCollection<Point2d> innerBandRetargets)
        {
            if (database == null ||
                requestedQuarterIds == null ||
                innerBandRetargets == null ||
                innerBandRetargets.Count == 0)
            {
                return false;
            }

            var clipWindows = MergeOverlappingClipWindows(BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0));
            if (clipWindows.Count == 0)
            {
                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b) => DoesSegmentIntersectAnyWindowForCorrectionLinePost(a, b, clipWindows);
            bool TryReadOpenSegment(Entity ent, out Point2d a, out Point2d b) => TryReadOpenLinearSegment(ent, out a, out b);

            bool IsOrdinaryRoadAllowanceLayer(string layer)
            {
                return string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-0", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC2012", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC3018", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-3018", StringComparison.OrdinalIgnoreCase);
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var roadSegments = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, bool Horizontal, bool Vertical, double Length)>();
                var qsecEndpoints = new List<Point2d>();

                foreach (ObjectId id in ms)
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased ||
                        !TryReadOpenSegment(ent, out var a, out var b) ||
                        !DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    if (string.Equals(layer, "L-QSEC", StringComparison.OrdinalIgnoreCase))
                    {
                        qsecEndpoints.Add(a);
                        qsecEndpoints.Add(b);
                    }

                    if (!IsOrdinaryRoadAllowanceLayer(layer))
                    {
                        continue;
                    }

                    var horizontal = IsHorizontalLikeForCorrectionLinePost(a, b);
                    var vertical = IsVerticalLikeForCorrectionLinePost(a, b);
                    if (!horizontal && !vertical)
                    {
                        continue;
                    }

                    roadSegments.Add((id, layer, a, b, horizontal, vertical, a.GetDistanceTo(b)));
                }

                if (roadSegments.Count == 0 || qsecEndpoints.Count == 0)
                {
                    tr.Commit();
                    return false;
                }

                const double qsecEndpointTol = 0.80;
                const double existingCapTol = 0.80;
                const double capMin = 18.0;
                const double capMax = 22.5;
                const double verticalLengthMin = 200.0;
                const double duplicateTol = 0.75;

                bool EndpointTouchesQsec(Point2d point)
                {
                    return qsecEndpoints.Any(endpoint => endpoint.GetDistanceTo(point) <= qsecEndpointTol);
                }

                bool HasHorizontalRoadCapAt(Point2d point)
                {
                    return roadSegments.Any(segment =>
                        segment.Horizontal &&
                        DistancePointToSegment(point, segment.A, segment.B) <= existingCapTol);
                }

                bool HasNearRoadSegment(Point2d a, Point2d b)
                {
                    return roadSegments.Any(segment =>
                        Math.Min(
                            segment.A.GetDistanceTo(a) + segment.B.GetDistanceTo(b),
                            segment.A.GetDistanceTo(b) + segment.B.GetDistanceTo(a)) <= duplicateTol);
                }

                bool IsUsecZeroLayer(string layer)
                {
                    return string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(layer, "L-USEC-0", StringComparison.OrdinalIgnoreCase);
                }

                bool HasLongSameZeroBoundaryAt(Point2d point, string layer)
                {
                    const double horizontalBoundaryMin = 200.0;
                    return IsUsecZeroLayer(layer) &&
                           roadSegments.Any(segment =>
                               segment.Horizontal &&
                               segment.Length >= horizontalBoundaryMin &&
                               IsUsecZeroLayer(segment.Layer) &&
                               DistancePointToSegment(point, segment.A, segment.B) <= existingCapTol);
                }

                bool IsNearInnerBandRetarget(Point2d point)
                {
                    const double retargetTieTol = RoadAllowanceSecWidthMeters + 2.0;
                    return innerBandRetargets.Any(target => target.GetDistanceTo(point) <= retargetTieTol);
                }

                var plannedCaps = new List<(Point2d Endpoint, Point2d Target, double Length)>();
                var samples = new List<string>();
                foreach (var segment in roadSegments)
                {
                    if (!segment.Vertical ||
                        segment.Length < verticalLengthMin ||
                        !IsUsecZeroLayer(segment.Layer))
                    {
                        continue;
                    }

                    var dx = Math.Abs(segment.A.X - segment.B.X);
                    if (dx < capMin || dx > capMax)
                    {
                        continue;
                    }

                    var endpoints = new[] { (Point: segment.A, Other: segment.B), (Point: segment.B, Other: segment.A) };
                    for (var endpointIndex = 0; endpointIndex < endpoints.Length; endpointIndex++)
                    {
                        var endpoint = endpoints[endpointIndex].Point;
                        var other = endpoints[endpointIndex].Other;
                        if (!EndpointTouchesQsec(endpoint) ||
                            HasHorizontalRoadCapAt(endpoint) ||
                            !IsNearInnerBandRetarget(other) ||
                            !HasLongSameZeroBoundaryAt(other, segment.Layer))
                        {
                            continue;
                        }

                        var target = new Point2d(other.X, endpoint.Y);
                        var capLength = endpoint.GetDistanceTo(target);
                        if (capLength < capMin ||
                            capLength > capMax ||
                            HasNearRoadSegment(endpoint, target) ||
                            plannedCaps.Any(cap =>
                                Math.Min(
                                    cap.Endpoint.GetDistanceTo(endpoint) + cap.Target.GetDistanceTo(target),
                                    cap.Endpoint.GetDistanceTo(target) + cap.Target.GetDistanceTo(endpoint)) <= duplicateTol))
                        {
                            continue;
                        }

                        plannedCaps.Add((endpoint, target, capLength));

                        if (samples.Count < 16)
                        {
                            samples.Add(
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "{0:0.###},{1:0.###}->{2:0.###},{3:0.###}",
                                    endpoint.X,
                                    endpoint.Y,
                                    target.X,
                                    target.Y));
                        }
                    }
                }

                var added = 0;
                if (plannedCaps.Count > 0)
                {
                    EnsureLayerWithColor(database, tr, LayerUsecTwenty, ResolveUsecLayerColorIndex(LayerUsecTwenty));
                    foreach (var cap in plannedCaps)
                    {
                        var connector = new Line(
                            new Point3d(cap.Endpoint.X, cap.Endpoint.Y, 0.0),
                            new Point3d(cap.Target.X, cap.Target.Y, 0.0))
                        {
                            Layer = LayerUsecTwenty,
                            ColorIndex = 256
                        };
                        ms.AppendEntity(connector);
                        tr.AddNewlyCreatedDBObject(connector, true);
                        added++;
                    }
                }

                tr.Commit();
                if (added > 0)
                {
                    logger?.WriteLine($"Cleanup: road-corner 20.11 cap restore added {added} connector(s).");
                    for (var i = 0; i < samples.Count; i++)
                    {
                        logger?.WriteLine("Cleanup:   road-corner-cap " + samples[i]);
                    }
                }

                return added > 0;
            }
        }

        private static bool TrimUsecTwentyEndpointsToZeroTerminators(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            IReadOnlyCollection<ObjectId> generatedRoadAllowanceIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null || generatedRoadAllowanceIds == null || generatedRoadAllowanceIds.Count == 0)
            {
                return false;
            }

            var rawClipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0);
            var clipWindows = MergeOverlappingClipWindows(rawClipWindows);
            if (clipWindows.Count == 0)
            {
                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b) => DoesSegmentIntersectAnyWindowForCorrectionLinePost(a, b, clipWindows);
            bool TryReadOpenSegment(Entity ent, out Point2d a, out Point2d b) => TryReadOpenLinearSegment(ent, out a, out b);
            bool TryMoveEndpoint(Entity writable, bool moveStart, Point2d target, double moveTol) =>
                TryMoveEndpointForCorrectionLinePost(writable, moveStart, target, moveTol);

            static bool IsUsecTwentyLayerName(string layer)
            {
                return string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase);
            }

            static bool IsZeroOrSurveyedTerminatorLayer(string layer)
            {
                return string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-SEC-0", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase);
            }

            try
            {
                using (var tr = database.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    var generatedSet = new HashSet<ObjectId>(generatedRoadAllowanceIds.Where(id => !id.IsNull));
                    var sources = new List<(ObjectId Id, string Layer, Point2d A, Point2d B)>();
                    var terminators = new List<(ObjectId Id, string Layer, Point2d A, Point2d B)>();

                    foreach (ObjectId id in ms)
                    {
                        Entity? ent = null;
                        try
                        {
                            ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception)
                        {
                            continue;
                        }

                        if (ent == null || ent.IsErased)
                        {
                            continue;
                        }

                        var layer = ent.Layer ?? string.Empty;
                        if (!IsUsecTwentyLayerName(layer) && !IsZeroOrSurveyedTerminatorLayer(layer))
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(ent, out var a, out var b) || !DoesSegmentIntersectAnyWindow(a, b))
                        {
                            continue;
                        }

                        if (IsUsecTwentyLayerName(layer) && generatedSet.Contains(id))
                        {
                            sources.Add((id, layer, a, b));
                        }

                        if (IsZeroOrSurveyedTerminatorLayer(layer))
                        {
                            terminators.Add((id, layer, a, b));
                        }
                    }

                    if (sources.Count == 0 || terminators.Count == 0)
                    {
                        tr.Commit();
                        return false;
                    }

                    const double pointOnSourceTol = 0.65;
                    const double offAxisTerminatorTol = 0.80;
                    const double minMove = 1.0;
                    const double moveTol = 0.05;
                    const double minRemainingLength = 2.0;
                    var maxMove = RoadAllowanceUsecWidthMeters + 2.0;
                    var adjusted = 0;
                    var samples = new List<string>();

                    bool TryFindTerminator(
                        ObjectId sourceId,
                        Point2d sourceA,
                        Point2d sourceB,
                        bool moveStart,
                        out Point2d bestTarget,
                        out string bestLayer,
                        out double bestMove)
                    {
                        bestTarget = moveStart ? sourceA : sourceB;
                        bestLayer = string.Empty;
                        bestMove = double.MaxValue;
                        var candidateBestTarget = bestTarget;
                        var candidateBestLayer = string.Empty;
                        var candidateBestMove = double.MaxValue;
                        var endpoint = moveStart ? sourceA : sourceB;
                        var otherEndpoint = moveStart ? sourceB : sourceA;
                        var sourceLength = sourceA.GetDistanceTo(sourceB);
                        if (sourceLength <= minRemainingLength)
                        {
                            return false;
                        }

                        for (var ti = 0; ti < terminators.Count; ti++)
                        {
                            var terminator = terminators[ti];
                            if (terminator.Id == sourceId)
                            {
                                continue;
                            }

                            void Consider(Point2d candidate, Point2d terminatorOther)
                            {
                                if (DistancePointToSegment(candidate, sourceA, sourceB) > pointOnSourceTol)
                                {
                                    return;
                                }

                                var moveDistance = endpoint.GetDistanceTo(candidate);
                                if (moveDistance <= minMove || moveDistance > maxMove)
                                {
                                    return;
                                }

                                if (otherEndpoint.GetDistanceTo(candidate) <= minRemainingLength)
                                {
                                    return;
                                }

                                if (DistancePointToInfiniteLine(terminatorOther, sourceA, sourceB) <= offAxisTerminatorTol)
                                {
                                    return;
                                }

                                var toCandidate = candidate - endpoint;
                                var toOther = otherEndpoint - endpoint;
                                if (toCandidate.DotProduct(toOther) <= 0.0)
                                {
                                    return;
                                }

                                if (moveDistance >= candidateBestMove)
                                {
                                    return;
                                }

                                candidateBestMove = moveDistance;
                                candidateBestTarget = candidate;
                                candidateBestLayer = terminator.Layer;
                            }

                            Consider(terminator.A, terminator.B);
                            Consider(terminator.B, terminator.A);
                        }

                        if (candidateBestMove >= double.MaxValue)
                        {
                            return false;
                        }

                        bestTarget = candidateBestTarget;
                        bestLayer = candidateBestLayer;
                        bestMove = candidateBestMove;
                        return true;
                    }

                    for (var i = 0; i < sources.Count; i++)
                    {
                        var source = sources[i];
                        if (!(tr.GetObject(source.Id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(writable, out var sourceA, out var sourceB))
                        {
                            continue;
                        }

                        for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                        {
                            var moveStart = endpointIndex == 0;
                            if (!TryFindTerminator(source.Id, sourceA, sourceB, moveStart, out var target, out var targetLayer, out var moveDistance))
                            {
                                continue;
                            }

                            if (!TryMoveEndpoint(writable, moveStart, target, moveTol))
                            {
                                continue;
                            }

                            adjusted++;
                            if (samples.Count < 16)
                            {
                                var endpoint = moveStart ? sourceA : sourceB;
                                samples.Add(
                                    string.Format(
                                        CultureInfo.InvariantCulture,
                                        "id={0} {1} endpoint=({2:0.###},{3:0.###}) target=({4:0.###},{5:0.###}) via={6} move={7:0.###}",
                                        source.Id.Handle.ToString(),
                                        source.Layer,
                                        endpoint.X,
                                        endpoint.Y,
                                        target.X,
                                        target.Y,
                                        targetLayer,
                                        moveDistance));
                            }

                            if (!TryReadOpenSegment(writable, out sourceA, out sourceB))
                            {
                                break;
                            }
                        }
                    }

                    tr.Commit();
                    if (adjusted > 0)
                    {
                        logger?.WriteLine($"CorrectionLine: trimmed {adjusted} L-USEC2012 endpoint(s) to zero/surveyed terminators.");
                        for (var i = 0; i < samples.Count; i++)
                        {
                            logger?.WriteLine("CorrectionLine:   trim-20-to-zero " + samples[i]);
                        }
                    }

                    return adjusted > 0;
                }
            }
            catch (System.Exception ex)
            {
                logger?.WriteLine("CorrectionLine: trim L-USEC2012 endpoints to zero/surveyed terminators exception: " + ex.Message);
                return false;
            }
        }

        private static bool ProjectMisbandedTwentyRowsToTwentyOffset(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null)
            {
                return false;
            }

            var clipWindows = MergeOverlappingClipWindows(BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0));
            if (clipWindows.Count == 0)
            {
                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b) => DoesSegmentIntersectAnyWindowForCorrectionLinePost(a, b, clipWindows);
            bool TryReadOpenSegment(Entity ent, out Point2d a, out Point2d b) => TryReadOpenLinearSegment(ent, out a, out b);
            bool TryMoveEndpoint(Entity writable, bool moveStart, Point2d target, double moveTol) =>
                TryMoveEndpointForCorrectionLinePost(writable, moveStart, target, moveTol);

            static bool IsUsecTwentyLayerName(string layer)
            {
                return string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase);
            }

            static bool IsZeroBaseLayerName(string layer)
            {
                return string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-SEC-0", StringComparison.OrdinalIgnoreCase);
            }

            static double SegmentOverlapAlongAxis(Point2d a0, Point2d a1, Point2d b0, Point2d b1)
            {
                var axis = a1 - a0;
                var len = axis.Length;
                if (len <= 1e-6)
                {
                    return 0.0;
                }

                var u = axis / len;
                var aMin = 0.0;
                var aMax = len;
                var bT0 = (b0 - a0).DotProduct(u);
                var bT1 = (b1 - a0).DotProduct(u);
                var bMin = Math.Min(bT0, bT1);
                var bMax = Math.Max(bT0, bT1);
                return Math.Min(aMax, bMax) - Math.Max(aMin, bMin);
            }

            try
            {
                using (var tr = database.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    var sources = new List<(ObjectId Id, string Layer, Point2d A, Point2d B)>();
                    var zeroRows = new List<(ObjectId Id, Point2d A, Point2d B)>();

                    foreach (ObjectId id in ms)
                    {
                        Entity? ent = null;
                        try
                        {
                            ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception)
                        {
                            continue;
                        }

                        if (ent == null || ent.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(ent, out var a, out var b) ||
                            !DoesSegmentIntersectAnyWindow(a, b) ||
                            !IsHorizontalLikeForCorrectionLinePost(a, b))
                        {
                            continue;
                        }

                        var layer = ent.Layer ?? string.Empty;
                        if (IsUsecTwentyLayerName(layer))
                        {
                            sources.Add((id, layer, a, b));
                        }
                        else if (IsZeroBaseLayerName(layer))
                        {
                            zeroRows.Add((id, a, b));
                        }
                    }

                    if (sources.Count == 0 || zeroRows.Count == 0)
                    {
                        tr.Commit();
                        return false;
                    }

                    const double parallelDotMin = 0.985;
                    const double overlapMin = 60.0;
                    const double currentThirtyOffsetTol = 1.80;
                    const double endpointMoveMin = 0.05;
                    const double endpointMoveMax = 15.0;
                    const double endpointMoveTol = 0.05;
                    var adjusted = 0;
                    var samples = new List<string>();

                    for (var si = 0; si < sources.Count; si++)
                    {
                        var source = sources[si];
                        var sourceVector = source.B - source.A;
                        var sourceLength = sourceVector.Length;
                        if (sourceLength <= 1e-6)
                        {
                            continue;
                        }

                        var sourceUnit = sourceVector / sourceLength;
                        var sourceMid = Midpoint(source.A, source.B);
                        var bestScore = double.MaxValue;
                        var bestStart = source.A;
                        var bestEnd = source.B;
                        var bestBase = default((ObjectId Id, Point2d A, Point2d B));
                        var found = false;

                        for (var zi = 0; zi < zeroRows.Count; zi++)
                        {
                            var zero = zeroRows[zi];
                            if (zero.Id == source.Id)
                            {
                                continue;
                            }

                            var zeroVector = zero.B - zero.A;
                            var zeroLength = zeroVector.Length;
                            if (zeroLength <= 1e-6)
                            {
                                continue;
                            }

                            var zeroUnit = zeroVector / zeroLength;
                            if (Math.Abs(sourceUnit.DotProduct(zeroUnit)) < parallelDotMin)
                            {
                                continue;
                            }

                            var overlap = Math.Max(
                                SegmentOverlapAlongAxis(zero.A, zero.B, source.A, source.B),
                                SegmentOverlapAlongAxis(source.A, source.B, zero.A, zero.B));
                            if (overlap < overlapMin)
                            {
                                continue;
                            }

                            var offset = DistancePointToInfiniteLine(sourceMid, zero.A, zero.B);
                            if (Math.Abs(offset - RoadAllowanceUsecWidthMeters) > currentThirtyOffsetTol)
                            {
                                continue;
                            }

                            var normal = new Vector2d(-zeroUnit.Y, zeroUnit.X);
                            var positiveA = zero.A + normal * RoadAllowanceSecWidthMeters;
                            var positiveB = zero.B + normal * RoadAllowanceSecWidthMeters;
                            var negativeA = zero.A - normal * RoadAllowanceSecWidthMeters;
                            var negativeB = zero.B - normal * RoadAllowanceSecWidthMeters;
                            var positiveMid = Midpoint(positiveA, positiveB);
                            var negativeMid = Midpoint(negativeA, negativeB);
                            var usePositive = sourceMid.GetDistanceTo(positiveMid) <= sourceMid.GetDistanceTo(negativeMid);
                            var candidateA = usePositive ? positiveA : negativeA;
                            var candidateB = usePositive ? positiveB : negativeB;
                            var directScore = source.A.GetDistanceTo(candidateA) + source.B.GetDistanceTo(candidateB);
                            var reverseScore = source.A.GetDistanceTo(candidateB) + source.B.GetDistanceTo(candidateA);
                            if (reverseScore < directScore)
                            {
                                (candidateA, candidateB) = (candidateB, candidateA);
                            }

                            var moveA = source.A.GetDistanceTo(candidateA);
                            var moveB = source.B.GetDistanceTo(candidateB);
                            if ((moveA <= endpointMoveMin && moveB <= endpointMoveMin) ||
                                moveA > endpointMoveMax ||
                                moveB > endpointMoveMax)
                            {
                                continue;
                            }

                            var score = Math.Abs(offset - RoadAllowanceUsecWidthMeters) + moveA + moveB - (0.001 * overlap);
                            if (score >= bestScore)
                            {
                                continue;
                            }

                            found = true;
                            bestScore = score;
                            bestStart = candidateA;
                            bestEnd = candidateB;
                            bestBase = zero;
                        }

                        if (!found)
                        {
                            continue;
                        }

                        if (!(tr.GetObject(source.Id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(writable, out var currentA, out var currentB))
                        {
                            continue;
                        }

                        var movedStart = currentA.GetDistanceTo(bestStart) > endpointMoveMin &&
                                         TryMoveEndpoint(writable, moveStart: true, bestStart, endpointMoveTol);
                        var movedEnd = currentB.GetDistanceTo(bestEnd) > endpointMoveMin &&
                                       TryMoveEndpoint(writable, moveStart: false, bestEnd, endpointMoveTol);
                        if (!movedStart && !movedEnd)
                        {
                            continue;
                        }

                        adjusted++;
                        if (samples.Count < 16)
                        {
                            samples.Add(
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "id={0} {1} base={2} from=({3:0.###},{4:0.###})->({5:0.###},{6:0.###}) to=({7:0.###},{8:0.###})->({9:0.###},{10:0.###})",
                                    source.Id.Handle.ToString(),
                                    source.Layer,
                                    bestBase.Id.Handle.ToString(),
                                    source.A.X,
                                    source.A.Y,
                                    source.B.X,
                                    source.B.Y,
                                    bestStart.X,
                                    bestStart.Y,
                                    bestEnd.X,
                                    bestEnd.Y));
                        }
                    }

                    tr.Commit();
                    if (adjusted > 0)
                    {
                        logger?.WriteLine($"CorrectionLine: projected {adjusted} L-USEC2012 row(s) from 30.16 band back to 20.11 band.");
                        for (var i = 0; i < samples.Count; i++)
                        {
                            logger?.WriteLine("CorrectionLine:   project-20-row " + samples[i]);
                        }
                    }

                    return adjusted > 0;
                }
            }
            catch (System.Exception ex)
            {
                logger?.WriteLine("CorrectionLine: project L-USEC2012 rows to 20.11 offset exception: " + ex.Message);
                return false;
            }
        }

        private static void NormalizeCorrectionLayerEntityColorByLayer(Database database, Logger? logger)
        {
            if (database == null)
            {
                return;
            }

            var adjusted = 0;
            using (var tr = database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                foreach (ObjectId id in ms)
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    var isCorrectionLayer = IsCorrectionLayer(layer);
                    if (!isCorrectionLayer || ent.ColorIndex == 256)
                    {
                        continue;
                    }

                    try
                    {
                        var writable = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                        if (writable == null || writable.IsErased)
                        {
                            continue;
                        }

                        writable.ColorIndex = 256;
                        adjusted++;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        // Skip invalid or erased entities.
                    }
                }

                tr.Commit();
            }

            logger?.WriteLine($"CorrectionLine: normalized correction-layer entity colors to ByLayer count={adjusted}.");
        }

        private static bool PruneRedundantCorrectionBandRows(
            Database database,
            IReadOnlyList<CorrectionSeam> seams,
            Logger? logger)
        {
            if (database == null || seams == null || seams.Count == 0)
            {
                return false;
            }

            const double directionDotMin = 0.985;
            const double overlapMin = 35.0;
            const double bandTol = 2.4;
            var outerExpected = CorrectionLinePostExpectedUsecWidthMeters * 0.5;
            var innerExpected = outerExpected - CorrectionLinePostInsetMeters;
            var relayered = 0;
            var erased = 0;
            using (var tr = database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var liveSegments = CollectHorizontalCorrectionSegments(tr, ms, IsCorrectionLayer);
                var endpointAnchorSegments = new List<(Point2d A, Point2d B)>();

                foreach (ObjectId id in ms)
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased || IsCorrectionLayer(ent.Layer ?? string.Empty))
                    {
                        continue;
                    }

                    if (!TryReadOpenLinearSegment(ent, out var anchorA, out var anchorB))
                    {
                        continue;
                    }

                    var dx = Math.Abs(anchorB.X - anchorA.X);
                    var dy = Math.Abs(anchorB.Y - anchorA.Y);
                    if (dx >= (dy * 1.2))
                    {
                        continue;
                    }

                    endpointAnchorSegments.Add((anchorA, anchorB));
                }

                bool AreClusterNeighbors(CorrectionSegment a, CorrectionSegment b)
                {
                    var dirA = a.B - a.A;
                    var dirB = b.B - b.A;
                    var lenA = dirA.Length;
                    var lenB = dirB.Length;
                    if (lenA <= 1e-6 || lenB <= 1e-6)
                    {
                        return false;
                    }

                    if (Math.Abs((dirA / lenA).DotProduct(dirB / lenB)) < directionDotMin)
                    {
                        return false;
                    }

                    var overlap = GetCorrectionHorizontalOverlap(a, b.MinX, b.MaxX);
                    var minRequiredOverlap = Math.Max(overlapMin, Math.Min(Math.Min(a.Length, b.Length) * 0.55, 140.0));
                    return overlap >= minRequiredOverlap;
                }

                int CountEndpointAnchorTouches(CorrectionSegment segment)
                {
                    const double endpointAnchorTol = 0.75;
                    var touches = 0;
                    for (var ai = 0; ai < endpointAnchorSegments.Count; ai++)
                    {
                        var anchor = endpointAnchorSegments[ai];
                        if (DistancePointToSegment(segment.A, anchor.A, anchor.B) <= endpointAnchorTol)
                        {
                            touches++;
                        }

                        if (DistancePointToSegment(segment.B, anchor.A, anchor.B) <= endpointAnchorTol)
                        {
                            touches++;
                        }
                    }

                    return touches;
                }

                var erasedIds = new HashSet<ObjectId>();
                void UpdateLiveSegmentLayer(ObjectId id, string layer)
                {
                    UpdateTrackedCorrectionSegmentLayer(liveSegments, id, layer);
                }

                for (var si = 0; si < seams.Count; si++)
                {
                    var seam = seams[si];
                    for (var signIndex = 0; signIndex < 2; signIndex++)
                    {
                        var expectedSign = signIndex == 0 ? -1 : 1;
                        var seamCandidates = new List<CorrectionSegment>();
                        for (var i = 0; i < liveSegments.Count; i++)
                        {
                            var seg = liveSegments[i];
                            if (erasedIds.Contains(seg.Id))
                            {
                                continue;
                            }

                            if (seg.MaxX < seam.MinX - 25.0 || seg.MinX > seam.MaxX + 25.0)
                            {
                                continue;
                            }

                            if (GetCorrectionHorizontalOverlap(seg, seam.MinX, seam.MaxX) < overlapMin)
                            {
                                continue;
                            }

                            if (!seam.IntersectsExpandedStrip(seg, 18.0))
                            {
                                continue;
                            }

                            var signedOffset = seam.GetCenterSignedOffset(seg.Mid);
                            if (Math.Sign(signedOffset) != expectedSign)
                            {
                                continue;
                            }

                            seamCandidates.Add(seg);
                        }

                        if (seamCandidates.Count <= 2)
                        {
                            continue;
                        }

                        var visited = new bool[seamCandidates.Count];
                        for (var i = 0; i < seamCandidates.Count; i++)
                        {
                            if (visited[i])
                            {
                                continue;
                            }

                            var cluster = new List<int>();
                            var queue = new Queue<int>();
                            queue.Enqueue(i);
                            visited[i] = true;
                            while (queue.Count > 0)
                            {
                                var current = queue.Dequeue();
                                cluster.Add(current);
                                for (var j = 0; j < seamCandidates.Count; j++)
                                {
                                    if (visited[j] || j == current)
                                    {
                                        continue;
                                    }

                                    if (!AreClusterNeighbors(seamCandidates[current], seamCandidates[j]))
                                    {
                                        continue;
                                    }

                                    visited[j] = true;
                                    queue.Enqueue(j);
                                }
                            }

                            if (cluster.Count <= 2)
                            {
                                continue;
                            }

                            var bestOuter = -1;
                            var bestInner = -1;
                            var bestOuterScore = double.MaxValue;
                            var bestInnerScore = double.MaxValue;
                            var bestOuterCoverage = double.NegativeInfinity;
                            var bestInnerCoverage = double.NegativeInfinity;
                            var bestOuterAnchorTouches = int.MinValue;
                            for (var ci = 0; ci < cluster.Count; ci++)
                            {
                                var idx = cluster[ci];
                                var seg = seamCandidates[idx];
                                var centerDistance = Math.Abs(seam.GetCenterSignedOffset(seg.Mid));
                                var coverage = GetCorrectionHorizontalOverlap(seg, seam.MinX, seam.MaxX);
                                var anchorTouches = CountEndpointAnchorTouches(seg);
                                var outerScore = Math.Abs(centerDistance - outerExpected);
                                var innerScore = Math.Abs(centerDistance - innerExpected);
                                if (outerScore <= bandTol &&
                                    (coverage > bestOuterCoverage + 1e-6 ||
                                     (Math.Abs(coverage - bestOuterCoverage) <= 1e-6 &&
                                      (anchorTouches > bestOuterAnchorTouches ||
                                       (anchorTouches == bestOuterAnchorTouches && outerScore < bestOuterScore - 1e-6)))))
                                {
                                    bestOuter = idx;
                                    bestOuterCoverage = coverage;
                                    bestOuterAnchorTouches = anchorTouches;
                                    bestOuterScore = outerScore;
                                }

                                if (innerScore <= bandTol &&
                                    (coverage > bestInnerCoverage + 1e-6 ||
                                     (Math.Abs(coverage - bestInnerCoverage) <= 1e-6 && innerScore < bestInnerScore - 1e-6)))
                                {
                                    bestInner = idx;
                                    bestInnerCoverage = coverage;
                                    bestInnerScore = innerScore;
                                }
                            }

                            if (bestOuter < 0)
                            {
                                for (var ci = 0; ci < cluster.Count; ci++)
                                {
                                    var idx = cluster[ci];
                                    var seg = seamCandidates[idx];
                                    var coverage = GetCorrectionHorizontalOverlap(seg, seam.MinX, seam.MaxX);
                                    var anchorTouches = CountEndpointAnchorTouches(seg);
                                    var outerScore = Math.Abs(Math.Abs(seam.GetCenterSignedOffset(seg.Mid)) - outerExpected);
                                    if (coverage > bestOuterCoverage + 1e-6 ||
                                        (Math.Abs(coverage - bestOuterCoverage) <= 1e-6 &&
                                         (anchorTouches > bestOuterAnchorTouches ||
                                          (anchorTouches == bestOuterAnchorTouches && outerScore < bestOuterScore - 1e-6))))
                                    {
                                        bestOuter = idx;
                                        bestOuterCoverage = coverage;
                                        bestOuterAnchorTouches = anchorTouches;
                                        bestOuterScore = outerScore;
                                    }
                                }
                            }

                            if (bestInner < 0)
                            {
                                for (var ci = 0; ci < cluster.Count; ci++)
                                {
                                    var idx = cluster[ci];
                                    if (idx == bestOuter)
                                    {
                                        continue;
                                    }

                                    var seg = seamCandidates[idx];
                                    var coverage = GetCorrectionHorizontalOverlap(seg, seam.MinX, seam.MaxX);
                                    var innerScore = Math.Abs(Math.Abs(seam.GetCenterSignedOffset(seg.Mid)) - innerExpected);
                                    if (coverage > bestInnerCoverage + 1e-6 ||
                                        (Math.Abs(coverage - bestInnerCoverage) <= 1e-6 && innerScore < bestInnerScore - 1e-6))
                                    {
                                        bestInner = idx;
                                        bestInnerCoverage = coverage;
                                        bestInnerScore = innerScore;
                                    }
                                }
                            }

                            if (bestOuter >= 0 && bestInner >= 0)
                            {
                                var outerAnchors = CountEndpointAnchorTouches(seamCandidates[bestOuter]);
                                var innerAnchors = CountEndpointAnchorTouches(seamCandidates[bestInner]);
                                var coverageIsComparable =
                                    bestInnerCoverage + 20.0 >= bestOuterCoverage &&
                                    bestOuterCoverage + 20.0 >= bestInnerCoverage;
                                if (innerAnchors > outerAnchors && coverageIsComparable)
                                {
                                    (bestOuter, bestInner) = (bestInner, bestOuter);
                                    (bestOuterScore, bestInnerScore) = (bestInnerScore, bestOuterScore);
                                    (bestOuterCoverage, bestInnerCoverage) = (bestInnerCoverage, bestOuterCoverage);
                                    bestOuterAnchorTouches = innerAnchors;
                                }
                            }

                            if (bestOuter == bestInner && bestOuter >= 0)
                            {
                                if (bestOuterScore <= bestInnerScore)
                                {
                                    bestInner = -1;
                                }
                                else
                                {
                                    bestOuter = -1;
                                }
                            }

                            for (var ci = 0; ci < cluster.Count; ci++)
                            {
                                var idx = cluster[ci];
                                var seg = seamCandidates[idx];
                                var desiredLayer =
                                    idx == bestOuter ? LayerUsecCorrection :
                                    idx == bestInner ? LayerUsecCorrectionZero :
                                    null;
                                if (desiredLayer == null)
                                {
                                    Entity? writable = null;
                                    try
                                    {
                                        writable = tr.GetObject(seg.Id, OpenMode.ForWrite, false) as Entity;
                                    }
                                    catch (Autodesk.AutoCAD.Runtime.Exception)
                                    {
                                        continue;
                                    }

                                    if (writable == null || writable.IsErased)
                                    {
                                        continue;
                                    }

                                    writable.Erase();
                                    erasedIds.Add(seg.Id);
                                    erased++;
                                    continue;
                                }

                                if (!string.Equals(seg.Layer, desiredLayer, StringComparison.OrdinalIgnoreCase) &&
                                    TryRelayerCorrectionSegment(tr, seg.Id, desiredLayer))
                                {
                                    UpdateLiveSegmentLayer(seg.Id, desiredLayer);
                                    relayered++;
                                }
                            }
                        }
                    }
                }

                tr.Commit();
            }

            logger?.WriteLine(
                $"CorrectionLine: redundant band prune relayered={relayered}, erased={erased}.");
            return relayered > 0 || erased > 0;
        }

        private static bool EnforceFinalCorrectionOuterLayerConsistency(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null)
            {
                return false;
            }

            var coreClipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 0.0);
            var bufferedClipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0);
            var clipWindows = MergeOverlappingClipWindows(bufferedClipWindows);
            if (clipWindows.Count == 0)
            {
                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b) => DoesSegmentIntersectAnyWindowForCorrectionLinePost(a, b, clipWindows);

            bool IsHorizontalLike(Point2d a, Point2d b) => IsHorizontalLikeForCorrectionLinePost(a, b);

            const double endpointTouchTol = 1.6;
            const double collinearTol = 0.90;
            const double directionDotMin = 0.985;
            const double overlapMin = 10.0;
            const double companionOffsetTol = 1.25;

            bool IsCollinearAndAligned(Point2d a0, Point2d a1, Point2d b0, Point2d b1)
            {
                var da = a1 - a0;
                var db = b1 - b0;
                var lenA = da.Length;
                var lenB = db.Length;
                if (lenA <= 1e-6 || lenB <= 1e-6)
                {
                    return false;
                }

                var ua = da / lenA;
                var ub = db / lenB;
                if (Math.Abs(ua.DotProduct(ub)) < directionDotMin)
                {
                    return false;
                }

                var midA = Midpoint(a0, a1);
                var midB = Midpoint(b0, b1);
                var d1 = Math.Abs(DistancePointToInfiniteLine(midA, b0, b1));
                if (d1 > collinearTol)
                {
                    return false;
                }

                var d2 = Math.Abs(DistancePointToInfiniteLine(midB, a0, a1));
                return d2 <= collinearTol;
            }

            bool HasProjectedOverlap(Point2d a0, Point2d a1, Point2d b0, Point2d b1)
            {
                var da = a1 - a0;
                var lenA = da.Length;
                if (lenA <= 1e-6)
                {
                    return false;
                }

                var ua = da / lenA;
                var aMin = 0.0;
                var aMax = lenA;
                var bS0 = (b0 - a0).DotProduct(ua);
                var bS1 = (b1 - a0).DotProduct(ua);
                var bMin = Math.Min(bS0, bS1);
                var bMax = Math.Max(bS0, bS1);
                var overlap = Math.Min(aMax, bMax) - Math.Max(aMin, bMin);
                return overlap >= overlapMin;
            }

            double GetProjectedOverlap(Point2d a0, Point2d a1, Point2d b0, Point2d b1)
            {
                var da = a1 - a0;
                var lenA = da.Length;
                if (lenA <= 1e-6)
                {
                    return double.NegativeInfinity;
                }

                var ua = da / lenA;
                var aMin = 0.0;
                var aMax = lenA;
                var bS0 = (b0 - a0).DotProduct(ua);
                var bS1 = (b1 - a0).DotProduct(ua);
                var bMin = Math.Min(bS0, bS1);
                var bMax = Math.Max(bS0, bS1);
                return Math.Min(aMax, bMax) - Math.Max(aMin, bMin);
            }

            double MinEndpointDistance(Point2d a0, Point2d a1, Point2d b0, Point2d b1)
            {
                var d00 = a0.GetDistanceTo(b0);
                var d01 = a0.GetDistanceTo(b1);
                var d10 = a1.GetDistanceTo(b0);
                var d11 = a1.GetDistanceTo(b1);
                return Math.Min(Math.Min(d00, d01), Math.Min(d10, d11));
            }

            var converted = 0;
            var anchors = 0;
            var insetCompanionSuppressed = 0;
            var ordinaryInsetDuplicateErased = 0;
            var ordinaryInsetDuplicateSeeded = 0;
            var ordinaryInsetDuplicateExtended = 0;
            using (var tr = database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                var correctionOuterAnchors = new List<(Point2d A, Point2d B, Point2d Mid, double MinX, double MaxX)>();
                var correctionInnerAnchors = new List<(Point2d A, Point2d B, Point2d Mid, Vector2d U, double Length)>();
                var hardHorizontalOwnerSegments = new List<(Point2d A, Point2d B)>();
                var sectionVerticalSplitSegments = new List<(Point2d A, Point2d B, double MinY, double MaxY)>();
                var outerSectionVerticalSplitSegments = new List<(Point2d A, Point2d B, double MinY, double MaxY)>();
                var ordinaryVerticalCorridorAnchors = new List<(Point2d A, Point2d B, string Layer)>();
                var twentyCandidates = new List<(ObjectId Id, Point2d A, Point2d B, string Layer)>();

                static bool IntersectsTraceWindow(Point2d a, Point2d b, double minX, double minY, double maxX, double maxY)
                {
                    var segMinX = Math.Min(a.X, b.X);
                    var segMaxX = Math.Max(a.X, b.X);
                    var segMinY = Math.Min(a.Y, b.Y);
                    var segMaxY = Math.Max(a.Y, b.Y);
                    return !(segMaxX < minX || segMinX > maxX || segMaxY < minY || segMinY > maxY);
                }

                bool ShouldTraceFinalOuterCandidate(Point2d a, Point2d b)
                {
                    return IntersectsTraceWindow(a, b, 624100.0, 5836950.0, 625150.0, 5837105.0) ||
                           IntersectsTraceWindow(a, b, 632450.0, 5837240.0, 634250.0, 5837320.0);
                }

                bool IsHardHorizontalOwnerLayer(string layer)
                {
                    if (string.IsNullOrWhiteSpace(layer))
                    {
                        return false;
                    }

                    return layer.StartsWith("AB_", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(layer, "L-SEC-0", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(layer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(layer, "L-QSEC", StringComparison.OrdinalIgnoreCase);
                }

                bool IsSectionVerticalSplitLayer(string layer)
                {
                    return string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(layer, "L-QSEC", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase);
                }

                foreach (ObjectId id in ms)
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenLinearSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    if (!DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    if (IsHardHorizontalOwnerLayer(layer))
                    {
                        hardHorizontalOwnerSegments.Add((a, b));
                    }

                    if (IsSectionVerticalSplitLayer(layer) && IsVerticalLikeForCorrectionLinePost(a, b))
                    {
                        sectionVerticalSplitSegments.Add((a, b, Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y)));
                    }

                    var isCorrectionPromotableLayer =
                        string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, "L-USEC", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, "L-USEC2012", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, "L-USEC-3018", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, "L-USEC3018", StringComparison.OrdinalIgnoreCase) ||
                        IsCorrectionSurveyedLayer(layer);
                    if (isCorrectionPromotableLayer &&
                        !IsCorrectionLayer(layer) &&
                        IsVerticalLikeForCorrectionLinePost(a, b))
                    {
                        ordinaryVerticalCorridorAnchors.Add((a, b, layer));
                    }

                    if (!IsHorizontalLike(a, b))
                    {
                        continue;
                    }

                    if (string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
                    {
                        correctionOuterAnchors.Add((a, b, Midpoint(a, b), Math.Min(a.X, b.X), Math.Max(a.X, b.X)));
                        continue;
                    }

                    if (string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase))
                    {
                        var dir = b - a;
                        var len = dir.Length;
                        if (len <= 1e-6)
                        {
                            continue;
                        }

                        correctionInnerAnchors.Add((a, b, Midpoint(a, b), dir / len, len));
                        continue;
                    }

                    if (isCorrectionPromotableLayer)
                    {
                        twentyCandidates.Add((id, a, b, layer));
                    }
                }

                logger?.WriteLine(
                    $"Cleanup: corr-final collected anchors={correctionOuterAnchors.Count} inners={correctionInnerAnchors.Count} candidates={twentyCandidates.Count}.");

                bool MatchesExistingCorrectionOuterAnchor(Point2d a, Point2d b)
                {
                    for (var ai = 0; ai < correctionOuterAnchors.Count; ai++)
                    {
                        var anchor = correctionOuterAnchors[ai];
                        if (!IsCollinearAndAligned(a, b, anchor.A, anchor.B))
                        {
                            continue;
                        }

                        var hasOverlap = HasProjectedOverlap(a, b, anchor.A, anchor.B);
                        if (hasOverlap)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool TryFindNearbyCorrectionCorridorSide(
                    Point2d a,
                    Point2d b,
                    out int preferredOffsetSign)
                {
                    preferredOffsetSign = 0;
                    var dir = b - a;
                    var len = dir.Length;
                    if (len <= 1e-6)
                    {
                        return false;
                    }

                    var u = dir / len;
                    const double minCorridorOffset = 2.0;
                    const double maxCorridorOffset = 40.0;
                    var positiveIntervals = new List<(double Min, double Max)>();
                    var negativeIntervals = new List<(double Min, double Max)>();

                    void AddCoverageInterval(Point2d segA, Point2d segB, Point2d segMid, Vector2d segU)
                    {
                        if (Math.Abs(u.DotProduct(segU)) < directionDotMin)
                        {
                            return;
                        }

                        var signedOffset = GetSignedOffsetToLine(a, b, segMid);
                        var absOffset = Math.Abs(signedOffset);
                        if (absOffset < minCorridorOffset || absOffset > maxCorridorOffset)
                        {
                            return;
                        }

                        var s0 = (segA - a).DotProduct(u);
                        var s1 = (segB - a).DotProduct(u);
                        var minS = Math.Max(0.0, Math.Min(s0, s1));
                        var maxS = Math.Min(len, Math.Max(s0, s1));
                        if (maxS <= minS)
                        {
                            return;
                        }

                        if (Math.Sign(signedOffset) > 0)
                        {
                            positiveIntervals.Add((minS, maxS));
                        }
                        else if (Math.Sign(signedOffset) < 0)
                        {
                            negativeIntervals.Add((minS, maxS));
                        }
                    }

                    for (var ai = 0; ai < correctionOuterAnchors.Count; ai++)
                    {
                        var anchor = correctionOuterAnchors[ai];
                        var anchorDir = anchor.B - anchor.A;
                        var anchorLen = anchorDir.Length;
                        if (anchorLen <= 1e-6)
                        {
                            continue;
                        }

                        AddCoverageInterval(anchor.A, anchor.B, anchor.Mid, anchorDir / anchorLen);
                    }

                    for (var ii = 0; ii < correctionInnerAnchors.Count; ii++)
                    {
                        var inner = correctionInnerAnchors[ii];
                        AddCoverageInterval(inner.A, inner.B, inner.Mid, inner.U);
                    }

                    var positiveCoverage = GetMergedCoverageLength(positiveIntervals);
                    var negativeCoverage = GetMergedCoverageLength(negativeIntervals);
                    var positiveAcceptable = CorrectionSouthBoundaryPreference.IsCompanionCoverageAcceptable(positiveCoverage, len);
                    var negativeAcceptable = CorrectionSouthBoundaryPreference.IsCompanionCoverageAcceptable(negativeCoverage, len);
                    if (!positiveAcceptable && !negativeAcceptable)
                    {
                        const double connectedInnerEndpointTol = 1.20;
                        const double connectedInnerOffsetTol = 1.25;
                        const double connectedVerticalBridgeTol = 0.85;
                        var foundConnectedInnerHint = false;
                        var bestConnectedInnerEndpointGap = double.MaxValue;
                        var bestConnectedInnerOffsetDelta = double.MaxValue;
                        var bestConnectedInnerOverlap = double.NegativeInfinity;
                        for (var ii = 0; ii < correctionInnerAnchors.Count; ii++)
                        {
                            var inner = correctionInnerAnchors[ii];
                            if (Math.Abs(u.DotProduct(inner.U)) < directionDotMin)
                            {
                                continue;
                            }

                            var signedOffset = GetSignedOffsetToLine(a, b, inner.Mid);
                            var sign = Math.Sign(signedOffset);
                            if (sign == 0)
                            {
                                continue;
                            }

                            var offsetDelta = Math.Abs(Math.Abs(signedOffset) - CorrectionLinePostInsetMeters);
                            if (offsetDelta > connectedInnerOffsetTol)
                            {
                                continue;
                            }

                            var endpointPairs = new[]
                            {
                                (Candidate: a, Inner: inner.A),
                                (Candidate: a, Inner: inner.B),
                                (Candidate: b, Inner: inner.A),
                                (Candidate: b, Inner: inner.B),
                            };

                            for (var pi = 0; pi < endpointPairs.Length; pi++)
                            {
                                var pair = endpointPairs[pi];
                                var gapVector = pair.Inner - pair.Candidate;
                                var alongGap = Math.Abs(gapVector.DotProduct(u));
                                if (alongGap > connectedInnerOffsetTol)
                                {
                                    continue;
                                }

                                var endpointGap = pair.Candidate.GetDistanceTo(pair.Inner);
                                var perpendicularGap = Math.Sqrt(Math.Max(0.0, (endpointGap * endpointGap) - (alongGap * alongGap)));
                                var endpointGapIsExactTouch = endpointGap <= connectedInnerEndpointTol;
                                var endpointGapIsInsetBand =
                                    Math.Abs(perpendicularGap - CorrectionLinePostInsetMeters) <= connectedInnerOffsetTol;
                                if (!endpointGapIsExactTouch && !endpointGapIsInsetBand)
                                {
                                    continue;
                                }

                                var overlap = GetProjectedOverlap(a, b, inner.A, inner.B);
                                if (!foundConnectedInnerHint ||
                                    endpointGap < bestConnectedInnerEndpointGap - 1e-6 ||
                                    (Math.Abs(endpointGap - bestConnectedInnerEndpointGap) <= 1e-6 &&
                                     offsetDelta < bestConnectedInnerOffsetDelta - 1e-6) ||
                                    (Math.Abs(endpointGap - bestConnectedInnerEndpointGap) <= 1e-6 &&
                                     Math.Abs(offsetDelta - bestConnectedInnerOffsetDelta) <= 1e-6 &&
                                     overlap > bestConnectedInnerOverlap + 1e-6))
                                {
                                    foundConnectedInnerHint = true;
                                    bestConnectedInnerEndpointGap = endpointGap;
                                    bestConnectedInnerOffsetDelta = offsetDelta;
                                    bestConnectedInnerOverlap = overlap;
                                    preferredOffsetSign = sign;
                                }
                            }
                        }

                        if (!foundConnectedInnerHint)
                        {
                            for (var ii = 0; ii < correctionInnerAnchors.Count; ii++)
                            {
                                var inner = correctionInnerAnchors[ii];
                                if (Math.Abs(u.DotProduct(inner.U)) < directionDotMin)
                                {
                                    continue;
                                }

                                var signedOffset = GetSignedOffsetToLine(a, b, inner.Mid);
                                var sign = Math.Sign(signedOffset);
                                if (sign == 0)
                                {
                                    continue;
                                }

                                var offsetDelta = Math.Abs(Math.Abs(signedOffset) - CorrectionLinePostInsetMeters);
                                if (offsetDelta > connectedInnerOffsetTol)
                                {
                                    continue;
                                }

                                var endpointPairs = new[]
                                {
                                    (Candidate: a, Inner: inner.A),
                                    (Candidate: a, Inner: inner.B),
                                    (Candidate: b, Inner: inner.A),
                                    (Candidate: b, Inner: inner.B),
                                };

                                for (var pi = 0; pi < endpointPairs.Length; pi++)
                                {
                                    var pair = endpointPairs[pi];
                                    var bridged = false;
                                    for (var vi = 0; vi < ordinaryVerticalCorridorAnchors.Count; vi++)
                                    {
                                        var verticalAnchor = ordinaryVerticalCorridorAnchors[vi];
                                        if (DistancePointToSegment(pair.Candidate, verticalAnchor.A, verticalAnchor.B) > connectedVerticalBridgeTol)
                                        {
                                            continue;
                                        }

                                        if (DistancePointToSegment(pair.Inner, verticalAnchor.A, verticalAnchor.B) > connectedVerticalBridgeTol)
                                        {
                                            continue;
                                        }

                                        bridged = true;
                                        break;
                                    }

                                    if (!bridged)
                                    {
                                        for (var vi = 0; vi < sectionVerticalSplitSegments.Count; vi++)
                                        {
                                            var verticalSplit = sectionVerticalSplitSegments[vi];
                                            if (DistancePointToSegment(pair.Candidate, verticalSplit.A, verticalSplit.B) > connectedVerticalBridgeTol)
                                            {
                                                continue;
                                            }

                                            if (DistancePointToSegment(pair.Inner, verticalSplit.A, verticalSplit.B) > connectedVerticalBridgeTol)
                                            {
                                                continue;
                                            }

                                            bridged = true;
                                            break;
                                        }
                                    }

                                    if (!bridged)
                                    {
                                        continue;
                                    }

                                    var endpointGap = pair.Candidate.GetDistanceTo(pair.Inner);
                                    var overlap = GetProjectedOverlap(a, b, inner.A, inner.B);
                                    if (!foundConnectedInnerHint ||
                                        endpointGap < bestConnectedInnerEndpointGap - 1e-6 ||
                                        (Math.Abs(endpointGap - bestConnectedInnerEndpointGap) <= 1e-6 &&
                                         offsetDelta < bestConnectedInnerOffsetDelta - 1e-6) ||
                                        (Math.Abs(endpointGap - bestConnectedInnerEndpointGap) <= 1e-6 &&
                                         Math.Abs(offsetDelta - bestConnectedInnerOffsetDelta) <= 1e-6 &&
                                         overlap > bestConnectedInnerOverlap + 1e-6))
                                    {
                                        foundConnectedInnerHint = true;
                                        bestConnectedInnerEndpointGap = endpointGap;
                                        bestConnectedInnerOffsetDelta = offsetDelta;
                                        bestConnectedInnerOverlap = overlap;
                                        preferredOffsetSign = sign;
                                    }
                                }
                            }
                        }

                        if (!foundConnectedInnerHint)
                        {
                            return false;
                        }

                        if (ShouldTraceFinalOuterCandidate(a, b))
                        {
                            logger?.WriteLine(
                                $"Cleanup: corr-final trace connected-inner-side-hint A=({a.X:0.###},{a.Y:0.###}) B=({b.X:0.###},{b.Y:0.###}) sign={preferredOffsetSign} endpointGap={bestConnectedInnerEndpointGap:0.###} offsetDelta={bestConnectedInnerOffsetDelta:0.###} overlap={bestConnectedInnerOverlap:0.###}.");
                        }

                        return preferredOffsetSign != 0;
                    }

                    preferredOffsetSign = positiveCoverage >= negativeCoverage ? 1 : -1;
                    return true;
                }

                bool TryCreatePreferredOffsetCompanionSegment(
                    CorrectionSegment source,
                    int preferredOffsetSign,
                    IReadOnlyList<CorrectionSegment> existingSegments,
                    string targetLayer,
                    bool createPolyline,
                    out CorrectionSegment companion)
                {
                    companion = default;
                    if (preferredOffsetSign == 0 ||
                        !source.IsHorizontalLike ||
                        ms == null ||
                        tr == null ||
                        string.IsNullOrWhiteSpace(targetLayer))
                    {
                        return false;
                    }

                    var direction = source.B - source.A;
                    var length = direction.Length;
                    if (length <= 1e-6)
                    {
                        return false;
                    }

                    var normal = new Vector2d(direction.Y / length, -direction.X / length);
                    var offset = normal * (preferredOffsetSign > 0 ? CorrectionLinePostInsetMeters : -CorrectionLinePostInsetMeters);
                    var newA = source.A + offset;
                    var newB = source.B + offset;
                    if (newA.GetDistanceTo(newB) <= 1e-4)
                    {
                        return false;
                    }

                    if (HasMatchingCorrectionSegment(existingSegments, newA, newB))
                    {
                        return false;
                    }

                    Entity entity;
                    if (createPolyline)
                    {
                        var polyline = new Polyline();
                        polyline.AddVertexAt(0, newA, 0.0, 0.0, 0.0);
                        polyline.AddVertexAt(1, newB, 0.0, 0.0, 0.0);
                        polyline.Layer = targetLayer;
                        polyline.ColorIndex = 256;
                        entity = polyline;
                    }
                    else
                    {
                        entity = new Line(
                            new Point3d(newA.X, newA.Y, 0.0),
                            new Point3d(newB.X, newB.Y, 0.0))
                        {
                            Layer = targetLayer,
                            ColorIndex = 256
                        };
                    }

                    var newId = ms.AppendEntity(entity);
                    tr.AddNewlyCreatedDBObject(entity, true);
                    if (newId.IsNull)
                    {
                        return false;
                    }

                    companion = new CorrectionSegment(newId, targetLayer, newA, newB);
                    return true;
                }

                bool TryEnsurePreferredSurveyedCorrectionOuterCompanion(
                    CorrectionSegment outer,
                    int preferredOffsetSign,
                    out CorrectionSegment companionOuter)
                {
                    companionOuter = default;
                    var existingOuterSegments = CollectHorizontalCorrectionSegments(
                            tr,
                            ms,
                            layer => string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
                        .Where(segment => segment.Id != outer.Id)
                        .ToList();
                    existingOuterSegments.AddRange(
                        correctionOuterAnchors.Select(anchor => new CorrectionSegment(ObjectId.Null, LayerUsecCorrection, anchor.A, anchor.B)));
                    if (!TryCreatePreferredOffsetCompanionSegment(
                            outer,
                            preferredOffsetSign,
                            existingOuterSegments,
                            LayerUsecCorrection,
                            createPolyline: true,
                            out companionOuter))
                    {
                        return false;
                    }

                    correctionOuterAnchors.Add((
                        companionOuter.A,
                        companionOuter.B,
                        Midpoint(companionOuter.A, companionOuter.B),
                        Math.Min(companionOuter.A.X, companionOuter.B.X),
                        Math.Max(companionOuter.A.X, companionOuter.B.X)));
                    return true;
                }

                bool TryReplaceOrdinaryCandidateWithCorrectionZeroCompanion(
                    CorrectionSegment ordinaryCandidate,
                    int preferredOffsetSign,
                    bool preserveOriginalAsCorrectionOuter,
                    out CorrectionSegment companionInner,
                    out CorrectionSegment preservedOuter)
                {
                    companionInner = default;
                    preservedOuter = default;
                    if (preferredOffsetSign == 0 || tr == null)
                    {
                        return false;
                    }

                    Entity? writable = null;
                    try
                    {
                        writable = tr.GetObject(ordinaryCandidate.Id, OpenMode.ForWrite, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        return false;
                    }

                    if (writable == null || writable.IsErased)
                    {
                        return false;
                    }

                    var existingInnerSegments = CollectHorizontalCorrectionSegments(
                            tr,
                            ms,
                            layer => string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase))
                        .Where(segment => segment.Id != ordinaryCandidate.Id)
                        .ToList();
                    existingInnerSegments.AddRange(
                        correctionInnerAnchors.Select(inner => new CorrectionSegment(ObjectId.Null, LayerUsecCorrectionZero, inner.A, inner.B)));
                    if (!TryCreatePreferredOffsetCompanionSegment(
                            ordinaryCandidate,
                            preferredOffsetSign,
                            existingInnerSegments,
                            LayerUsecCorrectionZero,
                            createPolyline: false,
                            out companionInner))
                    {
                        return false;
                    }

                    if (preserveOriginalAsCorrectionOuter)
                    {
                        if (!TryRelayerCorrectionSegment(tr, ordinaryCandidate.Id, LayerUsecCorrection))
                        {
                            try
                            {
                                if (!companionInner.Id.IsNull &&
                                    tr.GetObject(companionInner.Id, OpenMode.ForWrite, false) is Entity companionWritable &&
                                    companionWritable != null &&
                                    !companionWritable.IsErased)
                                {
                                    companionWritable.Erase();
                                }
                            }
                            catch (Autodesk.AutoCAD.Runtime.Exception)
                            {
                            }

                            companionInner = default;
                            return false;
                        }

                        preservedOuter = new CorrectionSegment(
                            ordinaryCandidate.Id,
                            LayerUsecCorrection,
                            ordinaryCandidate.A,
                            ordinaryCandidate.B);
                        return true;
                    }

                    writable.Erase();
                    return true;
                }

                bool EndpointTouchesCorrectionChain(Point2d endpoint, IReadOnlyList<(Point2d A, Point2d B)> chain)
                {
                    if (chain == null)
                    {
                        return false;
                    }

                    for (var i = 0; i < chain.Count; i++)
                    {
                        var anchor = chain[i];
                        if (endpoint.GetDistanceTo(anchor.A) <= endpointTouchTol ||
                            endpoint.GetDistanceTo(anchor.B) <= endpointTouchTol)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool HasParallelInsetCompanion(Point2d a, Point2d b)
                {
                    var dir = b - a;
                    var len = dir.Length;
                    if (len <= 1e-6)
                    {
                        return false;
                    }

                    var u = dir / len;
                    var mid = Midpoint(a, b);
                    for (var i = 0; i < correctionInnerAnchors.Count; i++)
                    {
                        var inner = correctionInnerAnchors[i];
                        if (Math.Abs(u.DotProduct(inner.U)) < directionDotMin)
                        {
                            continue;
                        }

                        var overlap = GetProjectedOverlap(a, b, inner.A, inner.B);
                        if (!CorrectionSouthBoundaryPreference.IsCompanionCoverageAcceptable(overlap, len))
                        {
                            continue;
                        }

                        var candidateToInner = Math.Abs(DistancePointToInfiniteLine(mid, inner.A, inner.B));
                        if (Math.Abs(candidateToInner - CorrectionLinePostInsetMeters) > companionOffsetTol)
                        {
                            continue;
                        }

                        var innerToCandidate = Math.Abs(DistancePointToInfiniteLine(inner.Mid, a, b));
                        if (Math.Abs(innerToCandidate - CorrectionLinePostInsetMeters) > companionOffsetTol)
                        {
                            continue;
                        }

                        return true;
                    }

                    return false;
                }

                bool HasExistingCorrectionZeroCompanionCoverage(Point2d a, Point2d b)
                {
                    var liveInnerSegments = CollectHorizontalCorrectionSegments(
                        tr,
                        ms,
                        layer => string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase));
                    if (correctionInnerAnchors.Count > 0)
                    {
                        liveInnerSegments.AddRange(
                            correctionInnerAnchors.Select(inner => new CorrectionSegment(ObjectId.Null, LayerUsecCorrectionZero, inner.A, inner.B)));
                    }

                    if (liveInnerSegments.Count == 0)
                    {
                        return false;
                    }

                    var hasCoverage = HasTrackedInsetCompanion(
                        liveInnerSegments,
                        a,
                        b,
                        layer => string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase));
                    if (ShouldTraceFinalOuterCandidate(a, b))
                    {
                        logger?.WriteLine(
                            $"Cleanup: corr-final trace existing-inner-check A=({a.X:0.###},{a.Y:0.###}) B=({b.X:0.###},{b.Y:0.###}) liveInnerCount={liveInnerSegments.Count} hasCoverage={hasCoverage}.");
                    }
                    return hasCoverage;
                }

                bool HasExistingCorrectionOuterCoverage(Point2d a, Point2d b)
                {
                    var liveOuterSegments = CollectHorizontalCorrectionSegments(
                        tr,
                        ms,
                        layer => string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase));
                    if (correctionOuterAnchors.Count > 0)
                    {
                        liveOuterSegments.AddRange(
                            correctionOuterAnchors.Select(anchor => new CorrectionSegment(ObjectId.Null, LayerUsecCorrection, anchor.A, anchor.B)));
                    }

                    if (liveOuterSegments.Count == 0)
                    {
                        return false;
                    }

                    var hasCoverage = HasTrackedInsetCompanion(
                        liveOuterSegments,
                        a,
                        b,
                        layer => string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase));
                    if (ShouldTraceFinalOuterCandidate(a, b))
                    {
                        logger?.WriteLine(
                            $"Cleanup: corr-final trace existing-outer-check A=({a.X:0.###},{a.Y:0.###}) B=({b.X:0.###},{b.Y:0.###}) liveOuterCount={liveOuterSegments.Count} hasCoverage={hasCoverage}.");
                    }
                    return hasCoverage;
                }

                double GetSignedOffsetToLine(Point2d lineA, Point2d lineB, Point2d point)
                {
                    var dir = lineB - lineA;
                    var len = dir.Length;
                    if (len <= 1e-6)
                    {
                        return 0.0;
                    }

                    return ((point.X - lineA.X) * dir.Y - (point.Y - lineA.Y) * dir.X) / len;
                }

                bool TryFindInsetDuplicateInnerAnchor(
                    Point2d a,
                    Point2d b,
                    bool requireProjectedOverlap,
                    out int innerIndex,
                    out int offsetSign)
                {
                    innerIndex = -1;
                    offsetSign = 0;
                    var dir = b - a;
                    var len = dir.Length;
                    if (len <= 1e-6)
                    {
                        return false;
                    }

                    var u = dir / len;
                    var mid = Midpoint(a, b);
                    var found = false;
                    var bestOffsetDelta = double.MaxValue;
                    var bestOverlap = double.NegativeInfinity;
                    const double duplicateOffsetTol = 0.90;
                    for (var i = 0; i < correctionInnerAnchors.Count; i++)
                    {
                        var inner = correctionInnerAnchors[i];
                        if (Math.Abs(u.DotProduct(inner.U)) < directionDotMin)
                        {
                            continue;
                        }

                        var overlap = GetProjectedOverlap(a, b, inner.A, inner.B);
                        if (requireProjectedOverlap && overlap < overlapMin)
                        {
                            continue;
                        }

                        var signedOffset = GetSignedOffsetToLine(inner.A, inner.B, mid);
                        if (TryGetExistingCorrectionOuterSideHint(a, b, i, out var preferredOffsetSign) &&
                            preferredOffsetSign != Math.Sign(signedOffset))
                        {
                            continue;
                        }

                        var offsetDelta = Math.Abs(Math.Abs(signedOffset) - CorrectionLinePostInsetMeters);
                        if (offsetDelta > duplicateOffsetTol)
                        {
                            continue;
                        }

                        var reverseOffset = Math.Abs(DistancePointToInfiniteLine(inner.Mid, a, b));
                        if (Math.Abs(reverseOffset - CorrectionLinePostInsetMeters) > duplicateOffsetTol)
                        {
                            continue;
                        }

                        if (!found ||
                            offsetDelta < bestOffsetDelta - 1e-6 ||
                            (Math.Abs(offsetDelta - bestOffsetDelta) <= 1e-6 && overlap > bestOverlap + 1e-6))
                        {
                            found = true;
                            bestOffsetDelta = offsetDelta;
                            bestOverlap = overlap;
                            innerIndex = i;
                            offsetSign = Math.Sign(signedOffset);
                        }
                    }

                    return found && offsetSign != 0;
                }

                bool TryGetExistingCorrectionOuterSideHint(
                    Point2d a,
                    Point2d b,
                    int innerIndex,
                    out int preferredOffsetSign)
                {
                    preferredOffsetSign = 0;
                    if (innerIndex < 0 || innerIndex >= correctionInnerAnchors.Count)
                    {
                        return false;
                    }

                    var inner = correctionInnerAnchors[innerIndex];
                    const double duplicateOffsetTol = 0.90;
                    var found = false;
                    var bestOverlap = double.NegativeInfinity;
                    var bestEndpointDistance = double.MaxValue;
                    for (var ai = 0; ai < correctionOuterAnchors.Count; ai++)
                    {
                        var anchor = correctionOuterAnchors[ai];
                        if (!IsCollinearAndAligned(a, b, anchor.A, anchor.B))
                        {
                            continue;
                        }

                        var overlap = GetProjectedOverlap(a, b, anchor.A, anchor.B);
                        if (overlap < overlapMin)
                        {
                            continue;
                        }

                        var signedOffset = GetSignedOffsetToLine(inner.A, inner.B, anchor.Mid);
                        var sign = Math.Sign(signedOffset);
                        if (sign == 0)
                        {
                            continue;
                        }

                        var offsetDelta = Math.Abs(Math.Abs(signedOffset) - CorrectionLinePostInsetMeters);
                        if (offsetDelta > duplicateOffsetTol)
                        {
                            continue;
                        }

                        var reverseOffset = Math.Abs(DistancePointToInfiniteLine(inner.Mid, anchor.A, anchor.B));
                        if (Math.Abs(reverseOffset - CorrectionLinePostInsetMeters) > duplicateOffsetTol)
                        {
                            continue;
                        }

                        var endpointDistance = MinEndpointDistance(a, b, anchor.A, anchor.B);
                        if (!found ||
                            overlap > bestOverlap + 1e-6 ||
                            (Math.Abs(overlap - bestOverlap) <= 1e-6 &&
                             endpointDistance < bestEndpointDistance - 1e-6))
                        {
                            found = true;
                            bestOverlap = overlap;
                            bestEndpointDistance = endpointDistance;
                            preferredOffsetSign = sign;
                        }
                    }

                    return found && preferredOffsetSign != 0;
                }

                bool MatchesInsetDuplicateInnerAnchor(
                    Point2d a,
                    Point2d b,
                    int innerIndex,
                    int expectedOffsetSign)
                {
                    if (innerIndex < 0 || innerIndex >= correctionInnerAnchors.Count || expectedOffsetSign == 0)
                    {
                        return false;
                    }

                    var dir = b - a;
                    var len = dir.Length;
                    if (len <= 1e-6)
                    {
                        return false;
                    }

                    var inner = correctionInnerAnchors[innerIndex];
                    var u = dir / len;
                    if (Math.Abs(u.DotProduct(inner.U)) < directionDotMin)
                    {
                        return false;
                    }

                    const double duplicateOffsetTol = 0.90;
                    var mid = Midpoint(a, b);
                    var signedOffset = GetSignedOffsetToLine(inner.A, inner.B, mid);
                    if (Math.Sign(signedOffset) != expectedOffsetSign)
                    {
                        return false;
                    }

                    if (TryGetExistingCorrectionOuterSideHint(a, b, innerIndex, out var preferredOffsetSign) &&
                        preferredOffsetSign != expectedOffsetSign)
                    {
                        return false;
                    }

                    var offsetDelta = Math.Abs(Math.Abs(signedOffset) - CorrectionLinePostInsetMeters);
                    if (offsetDelta > duplicateOffsetTol)
                    {
                        return false;
                    }

                    var reverseOffset = Math.Abs(DistancePointToInfiniteLine(inner.Mid, a, b));
                    return Math.Abs(reverseOffset - CorrectionLinePostInsetMeters) <= duplicateOffsetTol;
                }

                double GetMergedCoverageLength(List<(double Min, double Max)> intervals)
                {
                    if (intervals == null || intervals.Count == 0)
                    {
                        return 0.0;
                    }

                    var ordered = intervals
                        .Where(interval => interval.Max > interval.Min)
                        .OrderBy(interval => interval.Min)
                        .ToList();
                    if (ordered.Count == 0)
                    {
                        return 0.0;
                    }

                    var total = 0.0;
                    var currentMin = ordered[0].Min;
                    var currentMax = ordered[0].Max;
                    for (var oi = 1; oi < ordered.Count; oi++)
                    {
                        var interval = ordered[oi];
                        if (interval.Min <= currentMax + 0.50)
                        {
                            currentMax = Math.Max(currentMax, interval.Max);
                            continue;
                        }

                        total += Math.Max(0.0, currentMax - currentMin);
                        currentMin = interval.Min;
                        currentMax = interval.Max;
                    }

                    total += Math.Max(0.0, currentMax - currentMin);
                    return total;
                }

                List<(double Min, double Max)> GetMergedCoverageIntervals(List<(double Min, double Max)> intervals)
                {
                    var merged = new List<(double Min, double Max)>();
                    if (intervals == null || intervals.Count == 0)
                    {
                        return merged;
                    }

                    var ordered = intervals
                        .Where(interval => interval.Max > interval.Min)
                        .OrderBy(interval => interval.Min)
                        .ToList();
                    if (ordered.Count == 0)
                    {
                        return merged;
                    }

                    var currentMin = ordered[0].Min;
                    var currentMax = ordered[0].Max;
                    for (var i = 1; i < ordered.Count; i++)
                    {
                        var interval = ordered[i];
                        if (interval.Min <= currentMax + 1e-6)
                        {
                            currentMax = Math.Max(currentMax, interval.Max);
                            continue;
                        }

                        merged.Add((currentMin, currentMax));
                        currentMin = interval.Min;
                        currentMax = interval.Max;
                    }

                    merged.Add((currentMin, currentMax));
                    return merged;
                }

                bool TryPromoteCorrectionOuterRespectingHardOwners(
                    CorrectionSegment source,
                    IList<CorrectionSegment> liveSegments,
                    out List<CorrectionSegment> effectiveOuters,
                    bool requireCarvedCoverage = false)
                {
                    effectiveOuters = new List<CorrectionSegment>();

                    var dir = source.B - source.A;
                    var len = dir.Length;
                    if (len <= 1e-6)
                    {
                        return false;
                    }

                    var u = dir / len;
                    var ownedIntervals = new List<(double Min, double Max)>();
                    for (var hi = 0; hi < hardHorizontalOwnerSegments.Count; hi++)
                    {
                        var owner = hardHorizontalOwnerSegments[hi];
                        if (!IsCollinearAndAligned(source.A, source.B, owner.A, owner.B))
                        {
                            continue;
                        }

                        var s0 = (owner.A - source.A).DotProduct(u);
                        var s1 = (owner.B - source.A).DotProduct(u);
                        var minS = Math.Max(0.0, Math.Min(s0, s1));
                        var maxS = Math.Min(len, Math.Max(s0, s1));
                        if (maxS - minS <= overlapMin)
                        {
                            continue;
                        }

                        ownedIntervals.Add((minS, maxS));
                    }

                    var mergedOwnedIntervals = GetMergedCoverageIntervals(ownedIntervals);
                    const double hardOwnerTrimTol = 0.50;
                    const double minPromotedSpanLength = 8.0;
                    var remainingIntervals = new List<(double Min, double Max)>();
                    var usedWindowCoverageFallback = false;
                    if (mergedOwnedIntervals.Count == 0)
                    {
                        if (requireCarvedCoverage)
                        {
                            var windowCoverageIntervals = new List<(double Min, double Max)>();
                            for (var wi = 0; wi < coreClipWindows.Count; wi++)
                            {
                                if (!TryClipSegmentToWindow(source.A, source.B, coreClipWindows[wi], out var clipStart, out var clipEnd))
                                {
                                    continue;
                                }

                                var s0 = (clipStart - source.A).DotProduct(u);
                                var s1 = (clipEnd - source.A).DotProduct(u);
                                var minS = Math.Max(0.0, Math.Min(s0, s1));
                                var maxS = Math.Min(len, Math.Max(s0, s1));
                                if (maxS - minS <= minPromotedSpanLength)
                                {
                                    continue;
                                }

                                windowCoverageIntervals.Add((minS, maxS));
                            }

                            var mergedWindowCoverageIntervals = GetMergedCoverageIntervals(windowCoverageIntervals);
                            var windowCoveredLength = GetMergedCoverageLength(mergedWindowCoverageIntervals);
                            if (mergedWindowCoverageIntervals.Count == 0 ||
                                windowCoveredLength >= len - hardOwnerTrimTol)
                            {
                                return false;
                            }

                            remainingIntervals.AddRange(mergedWindowCoverageIntervals);
                            usedWindowCoverageFallback = true;
                        }
                        else
                        {
                            if (TryEnsureCorrectionOuterSegment(source, liveSegments, ms, tr, out var effectiveOuter, out _))
                            {
                                effectiveOuters.Add(effectiveOuter);
                                return true;
                            }

                            return false;
                        }
                    }
                    else
                    {
                        var cursor = 0.0;
                        for (var i = 0; i < mergedOwnedIntervals.Count; i++)
                        {
                            var owned = mergedOwnedIntervals[i];
                            if (owned.Min > cursor + hardOwnerTrimTol)
                            {
                                remainingIntervals.Add((cursor, owned.Min));
                            }

                            cursor = Math.Max(cursor, owned.Max);
                        }

                        if (cursor < len - hardOwnerTrimTol)
                        {
                            remainingIntervals.Add((cursor, len));
                        }
                    }

                    if (remainingIntervals.Count == 0)
                    {
                        return false;
                    }

                    const double splitTargetTouchTol = 1.2;
                    const double splitTargetSpanTol = 0.6;
                    const double splitTargetCrossMargin = 2.5;
                    const double splitEndpointParamTol = 0.02;
                    const double splitParamMergeTol = 0.01;
                    if (sectionVerticalSplitSegments.Count > 0)
                    {
                        var splitTs = new List<double>();
                        var abLen2 = dir.DotProduct(dir);
                        if (abLen2 > 1e-9)
                        {
                            for (var i = 0; i < sectionVerticalSplitSegments.Count; i++)
                            {
                                var target = sectionVerticalSplitSegments[i];
                                if (!TryIntersectInfiniteLinesForPluginGeometry(source.A, source.B, target.A, target.B, out var ip))
                                {
                                    continue;
                                }

                                if (DistancePointToSegment(ip, target.A, target.B) > splitTargetTouchTol)
                                {
                                    continue;
                                }

                                if (ip.Y < target.MinY - splitTargetSpanTol || ip.Y > target.MaxY + splitTargetSpanTol)
                                {
                                    continue;
                                }

                                // Only split a correction corridor when the vertical materially
                                // crosses through the corridor. Verticals that merely terminate
                                // on the seam should not manufacture extra correction row pieces.
                                if (ip.Y <= target.MinY + splitTargetCrossMargin ||
                                    ip.Y >= target.MaxY - splitTargetCrossMargin)
                                {
                                    continue;
                                }

                                var t = (ip - source.A).DotProduct(dir) / abLen2;
                                if (t <= splitEndpointParamTol || t >= 1.0 - splitEndpointParamTol)
                                {
                                    continue;
                                }

                                splitTs.Add(t);
                            }
                        }

                        if (splitTs.Count > 0)
                        {
                            splitTs.Sort();
                            var uniqueTs = new List<double>();
                            for (var i = 0; i < splitTs.Count; i++)
                            {
                                var t = splitTs[i];
                                if (uniqueTs.Count == 0 || Math.Abs(t - uniqueTs[uniqueTs.Count - 1]) > splitParamMergeTol)
                                {
                                    uniqueTs.Add(t);
                                }
                            }

                            if (uniqueTs.Count > 0)
                            {
                                var splitRemaining = new List<(double Min, double Max)>();
                                for (var i = 0; i < remainingIntervals.Count; i++)
                                {
                                    var interval = remainingIntervals[i];
                                    var cursorInterval = interval.Min;
                                    for (var ti = 0; ti < uniqueTs.Count; ti++)
                                    {
                                        var t = uniqueTs[ti] * len;
                                        if (t <= interval.Min + hardOwnerTrimTol || t >= interval.Max - hardOwnerTrimTol)
                                        {
                                            continue;
                                        }

                                        if (t > cursorInterval + hardOwnerTrimTol)
                                        {
                                            splitRemaining.Add((cursorInterval, t));
                                        }

                                        cursorInterval = t;
                                    }

                                    if (cursorInterval < interval.Max - hardOwnerTrimTol)
                                    {
                                        splitRemaining.Add((cursorInterval, interval.Max));
                                    }
                                }

                                if (splitRemaining.Count > 0)
                                {
                                    remainingIntervals = splitRemaining;
                                }
                            }
                        }
                    }

                    if (!usedWindowCoverageFallback &&
                        remainingIntervals.Count == 1 &&
                        remainingIntervals[0].Min <= hardOwnerTrimTol &&
                        remainingIntervals[0].Max >= len - hardOwnerTrimTol)
                    {
                        if (requireCarvedCoverage)
                        {
                            return false;
                        }

                        if (TryEnsureCorrectionOuterSegment(source, liveSegments, ms, tr, out var effectiveOuter, out _))
                        {
                            effectiveOuters.Add(effectiveOuter);
                            return true;
                        }

                        return false;
                    }

                    for (var i = 0; i < remainingIntervals.Count; i++)
                    {
                        var interval = remainingIntervals[i];
                        if (interval.Max - interval.Min <= minPromotedSpanLength)
                        {
                            continue;
                        }

                        var spanA = source.A + (u * interval.Min);
                        var spanB = source.A + (u * interval.Max);
                        var spanSource = new CorrectionSegment(ObjectId.Null, LayerUsecCorrection, spanA, spanB);
                        if (!TryCloneCorrectionSegment(
                                spanSource,
                                LayerUsecCorrection,
                                liveSegments,
                                ms,
                                tr,
                                out var clonedOuter))
                        {
                            continue;
                        }

                        effectiveOuters.Add(clonedOuter);
                    }

                    if (effectiveOuters.Count == 0)
                    {
                        return false;
                    }

                    if (!source.Id.IsNull)
                    {
                        try
                        {
                            if (tr.GetObject(source.Id, OpenMode.ForWrite, false) is Entity writable &&
                                writable != null &&
                                !writable.IsErased)
                            {
                                writable.Erase();
                            }
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception)
                        {
                        }
                    }

                    return true;
                }

                bool TrySeedOrdinaryCarvedCorrectionOuter(
                    CorrectionSegment ordinaryCandidate,
                    out List<CorrectionSegment> promotedOuters)
                {
                    promotedOuters = new List<CorrectionSegment>();
                    if (!IsCorrectionUsecLayer(ordinaryCandidate.Layer) ||
                        IsCorrectionLayer(ordinaryCandidate.Layer))
                    {
                        return false;
                    }

                    var liveOuterSegments =
                        twentyCandidates.Select(c => new CorrectionSegment(c.Id, c.Layer, c.A, c.B)).ToList();
                    liveOuterSegments.AddRange(
                        correctionOuterAnchors.Select(anchor => new CorrectionSegment(ObjectId.Null, LayerUsecCorrection, anchor.A, anchor.B)));

                    return TryPromoteCorrectionOuterRespectingHardOwners(
                        new CorrectionSegment(ObjectId.Null, LayerUsecCorrection, ordinaryCandidate.A, ordinaryCandidate.B),
                        liveOuterSegments,
                        out promotedOuters,
                        requireCarvedCoverage: true);
                }

                bool HasAdequateExistingCorrectionOuterCoverage(
                    Point2d a,
                    Point2d b,
                    int innerIndex,
                    int expectedOffsetSign)
                {
                    if (innerIndex < 0 || innerIndex >= correctionInnerAnchors.Count || expectedOffsetSign == 0)
                    {
                        return false;
                    }

                    var dir = b - a;
                    var len = dir.Length;
                    if (len <= 1e-6)
                    {
                        return false;
                    }

                    var inner = correctionInnerAnchors[innerIndex];
                    var u = dir / len;
                    const double duplicateOffsetTol = 0.90;
                    var coverageIntervals = new List<(double Min, double Max)>();
                    for (var ai = 0; ai < correctionOuterAnchors.Count; ai++)
                    {
                        var anchor = correctionOuterAnchors[ai];
                        if (!IsCollinearAndAligned(a, b, anchor.A, anchor.B))
                        {
                            continue;
                        }

                        var signedOffset = GetSignedOffsetToLine(inner.A, inner.B, anchor.Mid);
                        if (Math.Sign(signedOffset) != expectedOffsetSign)
                        {
                            continue;
                        }

                        var offsetDelta = Math.Abs(Math.Abs(signedOffset) - CorrectionLinePostInsetMeters);
                        if (offsetDelta > duplicateOffsetTol)
                        {
                            continue;
                        }

                        var s0 = (anchor.A - a).DotProduct(u);
                        var s1 = (anchor.B - a).DotProduct(u);
                        var minS = Math.Max(0.0, Math.Min(s0, s1));
                        var maxS = Math.Min(len, Math.Max(s0, s1));
                        if (maxS <= minS)
                        {
                            continue;
                        }

                        coverageIntervals.Add((minS, maxS));
                    }

                    for (var hi = 0; hi < hardHorizontalOwnerSegments.Count; hi++)
                    {
                        var owner = hardHorizontalOwnerSegments[hi];
                        if (!IsCollinearAndAligned(a, b, owner.A, owner.B))
                        {
                            continue;
                        }

                        var ownerMid = Midpoint(owner.A, owner.B);
                        if (Math.Abs(DistancePointToInfiniteLine(ownerMid, a, b)) > duplicateOffsetTol)
                        {
                            continue;
                        }

                        var s0 = (owner.A - a).DotProduct(u);
                        var s1 = (owner.B - a).DotProduct(u);
                        var minS = Math.Max(0.0, Math.Min(s0, s1));
                        var maxS = Math.Min(len, Math.Max(s0, s1));
                        if (maxS <= minS)
                        {
                            continue;
                        }

                        coverageIntervals.Add((minS, maxS));
                    }

                    var coveredLength = GetMergedCoverageLength(coverageIntervals);
                    return CorrectionSouthBoundaryPreference.IsCompanionCoverageAcceptable(coveredLength, len);
                }

                bool EndpointTouchesOrdinaryVerticalCorridorAnchor(Point2d endpoint)
                {
                    const double verticalEndpointTouchTol = 0.60;
                    for (var i = 0; i < ordinaryVerticalCorridorAnchors.Count; i++)
                    {
                        var anchor = ordinaryVerticalCorridorAnchors[i];
                        if (endpoint.GetDistanceTo(anchor.A) <= verticalEndpointTouchTol ||
                            endpoint.GetDistanceTo(anchor.B) <= verticalEndpointTouchTol)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool EndpointTouchesOrdinaryVerticalCorridorInsetAnchor(Point2d endpoint, Point2d lineA, Point2d lineB)
                {
                    if (EndpointTouchesOrdinaryVerticalCorridorAnchor(endpoint))
                    {
                        return true;
                    }

                    var dir = lineB - lineA;
                    var len = dir.Length;
                    if (len <= 1e-6)
                    {
                        return false;
                    }

                    var u = dir / len;
                    var perp = new Vector2d(-u.Y, u.X);
                    for (var sign = -1; sign <= 1; sign += 2)
                    {
                        var insetProbe = endpoint + (perp * (sign * CorrectionLinePostInsetMeters));
                        if (EndpointTouchesOrdinaryVerticalCorridorAnchor(insetProbe))
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool IsBufferedOnlyQuarterCoverage(Point2d a, Point2d b)
                {
                    const double bufferedCoverageTol = 0.50;
                    var dir = b - a;
                    var len = dir.Length;
                    if (len <= 1e-6)
                    {
                        return false;
                    }

                    var u = dir / len;
                    var bufferedCoverageIntervals = new List<(double Min, double Max)>();
                    for (var wi = 0; wi < bufferedClipWindows.Count; wi++)
                    {
                        if (!TryClipSegmentToWindow(a, b, bufferedClipWindows[wi], out var clipStart, out var clipEnd))
                        {
                            continue;
                        }

                        var s0 = (clipStart - a).DotProduct(u);
                        var s1 = (clipEnd - a).DotProduct(u);
                        var minS = Math.Max(0.0, Math.Min(s0, s1));
                        var maxS = Math.Min(len, Math.Max(s0, s1));
                        if (maxS - minS <= bufferedCoverageTol)
                        {
                            continue;
                        }

                        bufferedCoverageIntervals.Add((minS, maxS));
                    }

                    var bufferedCoveredLength = GetMergedCoverageLength(GetMergedCoverageIntervals(bufferedCoverageIntervals));
                    var coreCoverageIntervals = new List<(double Min, double Max)>();
                    for (var wi = 0; wi < coreClipWindows.Count; wi++)
                    {
                        if (!TryClipSegmentToWindow(a, b, coreClipWindows[wi], out var clipStart, out var clipEnd))
                        {
                            continue;
                        }

                        var s0 = (clipStart - a).DotProduct(u);
                        var s1 = (clipEnd - a).DotProduct(u);
                        var minS = Math.Max(0.0, Math.Min(s0, s1));
                        var maxS = Math.Min(len, Math.Max(s0, s1));
                        if (maxS - minS <= bufferedCoverageTol)
                        {
                            continue;
                        }

                        coreCoverageIntervals.Add((minS, maxS));
                    }

                    var coreCoveredLength = GetMergedCoverageLength(GetMergedCoverageIntervals(coreCoverageIntervals));
                    return bufferedCoveredLength >= len - bufferedCoverageTol &&
                           coreCoveredLength <= bufferedCoverageTol;
                }

                bool HasConnectedInsetInnerEndpointHint(Point2d a, Point2d b)
                {
                    var dir = b - a;
                    var len = dir.Length;
                    if (len <= 1e-6)
                    {
                        return false;
                    }

                    var u = dir / len;
                    const double connectedInnerEndpointTol = 1.25;
                    const double connectedInnerInsetTol = 1.25;
                    for (var ii = 0; ii < correctionInnerAnchors.Count; ii++)
                    {
                        var inner = correctionInnerAnchors[ii];
                        if (Math.Abs(u.DotProduct(inner.U)) < directionDotMin)
                        {
                            continue;
                        }

                        var signedOffset = GetSignedOffsetToLine(a, b, inner.Mid);
                        if (Math.Abs(Math.Abs(signedOffset) - CorrectionLinePostInsetMeters) > connectedInnerInsetTol)
                        {
                            continue;
                        }

                        var endpointPairs = new[]
                        {
                            (Candidate: a, Inner: inner.A),
                            (Candidate: a, Inner: inner.B),
                            (Candidate: b, Inner: inner.A),
                            (Candidate: b, Inner: inner.B),
                        };

                        for (var pi = 0; pi < endpointPairs.Length; pi++)
                        {
                            var pair = endpointPairs[pi];
                            var gapVector = pair.Inner - pair.Candidate;
                            var alongGap = Math.Abs(gapVector.DotProduct(u));
                            if (alongGap > connectedInnerEndpointTol)
                            {
                                continue;
                            }

                            var endpointGap = pair.Candidate.GetDistanceTo(pair.Inner);
                            var perpendicularGap = Math.Sqrt(Math.Max(0.0, (endpointGap * endpointGap) - (alongGap * alongGap)));
                            if (Math.Abs(perpendicularGap - CorrectionLinePostInsetMeters) <= connectedInnerInsetTol)
                            {
                                return true;
                            }
                        }
                    }

                    return false;
                }

                bool ShouldPreserveOriginalOrdinaryCorridorAsCorrectionOuter(Point2d a, Point2d b)
                {
                    const double minimumOuterGap = 10.0;
                    const double maximumOuterGap = 35.0;
                    if (IsBufferedOnlyQuarterCoverage(a, b))
                    {
                        return true;
                    }

                    if (HasConnectedInsetInnerEndpointHint(a, b))
                    {
                        return true;
                    }

                    var bestGap = double.MaxValue;
                    var bestEndpoint = default(Point2d);
                    var found = false;
                    for (var i = 0; i < correctionOuterAnchors.Count; i++)
                    {
                        var outer = correctionOuterAnchors[i];
                        if (!IsCollinearAndAligned(a, b, outer.A, outer.B))
                        {
                            continue;
                        }

                        if (HasProjectedOverlap(a, b, outer.A, outer.B))
                        {
                            continue;
                        }

                        var endpointPairs = new[]
                        {
                            (Candidate: a, Outer: outer.A),
                            (Candidate: a, Outer: outer.B),
                            (Candidate: b, Outer: outer.A),
                            (Candidate: b, Outer: outer.B),
                        };

                        for (var pi = 0; pi < endpointPairs.Length; pi++)
                        {
                            var pair = endpointPairs[pi];
                            var gap = pair.Candidate.GetDistanceTo(pair.Outer);
                            if (gap < minimumOuterGap || gap > maximumOuterGap)
                            {
                                continue;
                            }

                            if (!found || gap < bestGap - 1e-6)
                            {
                                found = true;
                                bestGap = gap;
                                bestEndpoint = pair.Candidate;
                            }
                        }
                    }

                    if (!found)
                    {
                        return false;
                    }

                    var preserve =
                        ordinaryVerticalCorridorAnchors.Count == 0 ||
                        EndpointTouchesOrdinaryVerticalCorridorInsetAnchor(bestEndpoint, a, b);
                    return preserve;
                }

                bool TryFindInsetDuplicateOuterCoverage(
                    Point2d a,
                    Point2d b,
                    out int preferredOffsetSign)
                {
                    preferredOffsetSign = 0;

                    var dir = b - a;
                    var len = dir.Length;
                    if (len <= 1e-6)
                    {
                        return false;
                    }

                    var u = dir / len;
                    const double duplicateOffsetTol = 0.90;
                    var positiveIntervals = new List<(double Min, double Max)>();
                    var negativeIntervals = new List<(double Min, double Max)>();
                    for (var ai = 0; ai < correctionOuterAnchors.Count; ai++)
                    {
                        var anchor = correctionOuterAnchors[ai];
                        if (!IsCollinearAndAligned(a, b, anchor.A, anchor.B))
                        {
                            continue;
                        }

                        var signedOffset = GetSignedOffsetToLine(a, b, anchor.Mid);
                        var sign = Math.Sign(signedOffset);
                        if (sign == 0)
                        {
                            continue;
                        }

                        var offsetDelta = Math.Abs(Math.Abs(signedOffset) - CorrectionLinePostInsetMeters);
                        if (offsetDelta > duplicateOffsetTol)
                        {
                            continue;
                        }

                        var reverseOffset = Math.Abs(DistancePointToInfiniteLine(anchor.Mid, a, b));
                        if (Math.Abs(reverseOffset - CorrectionLinePostInsetMeters) > duplicateOffsetTol)
                        {
                            continue;
                        }

                        var s0 = (anchor.A - a).DotProduct(u);
                        var s1 = (anchor.B - a).DotProduct(u);
                        var minS = Math.Max(0.0, Math.Min(s0, s1));
                        var maxS = Math.Min(len, Math.Max(s0, s1));
                        if (maxS <= minS)
                        {
                            continue;
                        }

                        if (sign > 0)
                        {
                            positiveIntervals.Add((minS, maxS));
                        }
                        else
                        {
                            negativeIntervals.Add((minS, maxS));
                        }
                    }

                    var positiveCoverage = GetMergedCoverageLength(positiveIntervals);
                    var negativeCoverage = GetMergedCoverageLength(negativeIntervals);
                    var positiveAcceptable = CorrectionSouthBoundaryPreference.IsCompanionCoverageAcceptable(positiveCoverage, len);
                    var negativeAcceptable = CorrectionSouthBoundaryPreference.IsCompanionCoverageAcceptable(negativeCoverage, len);
                    if (ShouldTraceFinalOuterCandidate(a, b))
                    {
                        logger?.WriteLine(
                            $"Cleanup: corr-final trace coverage A=({a.X:0.###},{a.Y:0.###}) B=({b.X:0.###},{b.Y:0.###}) len={len:0.###} pos={positiveCoverage:0.###} neg={negativeCoverage:0.###} posOk={positiveAcceptable} negOk={negativeAcceptable}.");
                    }
                    if (!positiveAcceptable && !negativeAcceptable)
                    {
                        return false;
                    }

                    preferredOffsetSign = positiveCoverage >= negativeCoverage ? 1 : -1;
                    return true;
                }

                anchors = correctionOuterAnchors.Count;
                var correctionChain = new List<(Point2d A, Point2d B)>(anchors);
                for (var i = 0; i < correctionOuterAnchors.Count; i++)
                {
                    correctionChain.Add((correctionOuterAnchors[i].A, correctionOuterAnchors[i].B));
                }

                if (correctionInnerAnchors.Count > 0 && twentyCandidates.Count > 0)
                {
                    var suppressedIds = new HashSet<ObjectId>();
                    var suppressedCandidates = new Queue<(int CandidateIndex, int InnerIndex, int OffsetSign)>();
                    for (var ci = 0; ci < twentyCandidates.Count; ci++)
                    {
                        var candidate = twentyCandidates[ci];
                        var traceCandidate = ShouldTraceFinalOuterCandidate(candidate.A, candidate.B);
                        if (MatchesExistingCorrectionOuterAnchor(candidate.A, candidate.B))
                        {
                            if (traceCandidate)
                            {
                                logger?.WriteLine(
                                    $"Cleanup: corr-final trace candidate id={candidate.Id.Handle} layer={candidate.Layer} skipped=existing-outer A=({candidate.A.X:0.###},{candidate.A.Y:0.###}) B=({candidate.B.X:0.###},{candidate.B.Y:0.###}).");
                            }
                            continue;
                        }

                        if (!TryFindInsetDuplicateInnerAnchor(
                                candidate.A,
                                candidate.B,
                                requireProjectedOverlap: true,
                                out var innerIndex,
                                out var offsetSign))
                        {
                            // Only surveyed corridor rows are allowed to seed a missing correction
                            // outer when there is no inner duplicate. Letting ordinary USEC/0 rows
                            // take this path can relayer the original seam boundary into the
                            // correction corridor and create the extra parallel line users flagged
                            // at the township bend.
                            if (IsCorrectionSurveyedLayer(candidate.Layer) &&
                                TryFindNearbyCorrectionCorridorSide(candidate.A, candidate.B, out var corridorOffsetSign))
                            {
                                var promotedCorridorSegment = new CorrectionSegment(candidate.Id, candidate.Layer, candidate.A, candidate.B);
                                if (TryEnsureCorrectionOuterSegment(
                                        promotedCorridorSegment,
                                        twentyCandidates.Select(c => new CorrectionSegment(c.Id, c.Layer, c.A, c.B)).ToList(),
                                        ms,
                                        tr,
                                        out var effectiveOuter,
                                        out _))
                                {
                                    converted++;
                                    correctionOuterAnchors.Add((
                                        effectiveOuter.A,
                                        effectiveOuter.B,
                                        Midpoint(effectiveOuter.A, effectiveOuter.B),
                                        Math.Min(effectiveOuter.A.X, effectiveOuter.B.X),
                                        Math.Max(effectiveOuter.A.X, effectiveOuter.B.X)));
                                    correctionChain.Add((effectiveOuter.A, effectiveOuter.B));
                                    if (TryEnsurePreferredSurveyedCorrectionOuterCompanion(
                                            effectiveOuter,
                                            corridorOffsetSign,
                                            out var companionOuter))
                                    {
                                        correctionChain.Add((companionOuter.A, companionOuter.B));
                                    }
                                    if (traceCandidate)
                                    {
                                        logger?.WriteLine(
                                            $"Cleanup: corr-final trace candidate id={candidate.Id.Handle} action=seed-surveyed-corridor side={corridorOffsetSign} effectiveA=({effectiveOuter.A.X:0.###},{effectiveOuter.A.Y:0.###}) effectiveB=({effectiveOuter.B.X:0.###},{effectiveOuter.B.Y:0.###}).");
                                    }
                                    continue;
                                }
                            }

                            if (IsCorrectionUsecLayer(candidate.Layer) &&
                                !IsCorrectionLayer(candidate.Layer) &&
                                (HasParallelInsetCompanion(candidate.A, candidate.B) ||
                                 HasTrackedInsetCompanion(
                                     correctionOuterAnchors
                                         .Select(anchor => new CorrectionSegment(ObjectId.Null, LayerUsecCorrection, anchor.A, anchor.B))
                                         .Concat(correctionInnerAnchors.Select(inner => new CorrectionSegment(ObjectId.Null, LayerUsecCorrectionZero, inner.A, inner.B)))
                                         .ToList(),
                                     candidate.A,
                                     candidate.B,
                                     IsCorrectionLayer)))
                            {
                                var preservedOuterCandidate = new CorrectionSegment(candidate.Id, candidate.Layer, candidate.A, candidate.B);
                                if (TryEnsureCorrectionOuterSegment(
                                        preservedOuterCandidate,
                                        twentyCandidates.Select(c => new CorrectionSegment(c.Id, c.Layer, c.A, c.B)).ToList(),
                                        ms,
                                        tr,
                                        out var preservedOuter,
                                        out _))
                                {
                                    converted++;
                                    correctionOuterAnchors.Add((
                                        preservedOuter.A,
                                        preservedOuter.B,
                                        Midpoint(preservedOuter.A, preservedOuter.B),
                                        Math.Min(preservedOuter.A.X, preservedOuter.B.X),
                                        Math.Max(preservedOuter.A.X, preservedOuter.B.X)));
                                    correctionChain.Add((preservedOuter.A, preservedOuter.B));
                                    if (traceCandidate)
                                    {
                                        logger?.WriteLine(
                                            $"Cleanup: corr-final trace candidate id={candidate.Id.Handle} action=preserve-ordinary-corridor-as-outer " +
                                            $"effectiveA=({preservedOuter.A.X:0.###},{preservedOuter.A.Y:0.###}) effectiveB=({preservedOuter.B.X:0.###},{preservedOuter.B.Y:0.###}).");
                                    }
                                    continue;
                                }
                            }

                            if (IsCorrectionUsecLayer(candidate.Layer) &&
                                !IsCorrectionLayer(candidate.Layer) &&
                                TryFindInsetDuplicateOuterCoverage(candidate.A, candidate.B, out var insetOffsetSign))
                            {
                                var insetCandidate = new CorrectionSegment(candidate.Id, candidate.Layer, candidate.A, candidate.B);
                                if (TryRelayerCorrectionSegment(tr, insetCandidate.Id, LayerUsecCorrectionZero))
                                {
                                    var effectiveInner = new CorrectionSegment(
                                        insetCandidate.Id,
                                        LayerUsecCorrectionZero,
                                        insetCandidate.A,
                                        insetCandidate.B);
                                    var innerDir = effectiveInner.B - effectiveInner.A;
                                    var innerLen = innerDir.Length;
                                    if (innerLen > 1e-6)
                                    {
                                        correctionInnerAnchors.Add((
                                            effectiveInner.A,
                                            effectiveInner.B,
                                            Midpoint(effectiveInner.A, effectiveInner.B),
                                            innerDir / innerLen,
                                            innerLen));
                                    }

                                    twentyCandidates.RemoveAt(ci);
                                    ci--;
                                    converted++;
                                    if (traceCandidate)
                                    {
                                        logger?.WriteLine(
                                            $"Cleanup: corr-final trace candidate id={candidate.Id.Handle} action=relayer-no-inner-inset side={insetOffsetSign} effectiveA=({effectiveInner.A.X:0.###},{effectiveInner.A.Y:0.###}) effectiveB=({effectiveInner.B.X:0.###},{effectiveInner.B.Y:0.###}).");
                                    }
                                    continue;
                                }
                            }

                            if (IsCorrectionUsecLayer(candidate.Layer) &&
                                !IsCorrectionLayer(candidate.Layer) &&
                                TryFindNearbyCorrectionCorridorSide(candidate.A, candidate.B, out var ordinaryCorridorOffsetSign) &&
                                !HasExistingCorrectionZeroCompanionCoverage(candidate.A, candidate.B) &&
                                TryReplaceOrdinaryCandidateWithCorrectionZeroCompanion(
                                    new CorrectionSegment(candidate.Id, candidate.Layer, candidate.A, candidate.B),
                                    ordinaryCorridorOffsetSign,
                                    ShouldPreserveOriginalOrdinaryCorridorAsCorrectionOuter(candidate.A, candidate.B),
                                    out var corridorInner,
                                    out var preservedOrdinaryOuter))
                            {
                                var ordinaryCandidate = new CorrectionSegment(candidate.Id, candidate.Layer, candidate.A, candidate.B);
                                if (TrySeedOrdinaryCarvedCorrectionOuter(ordinaryCandidate, out var promotedOrdinaryOuters))
                                {
                                    for (var po = 0; po < promotedOrdinaryOuters.Count; po++)
                                    {
                                        var effectiveOuter = promotedOrdinaryOuters[po];
                                        correctionOuterAnchors.Add((
                                            effectiveOuter.A,
                                            effectiveOuter.B,
                                            Midpoint(effectiveOuter.A, effectiveOuter.B),
                                            Math.Min(effectiveOuter.A.X, effectiveOuter.B.X),
                                            Math.Max(effectiveOuter.A.X, effectiveOuter.B.X)));
                                        correctionChain.Add((effectiveOuter.A, effectiveOuter.B));
                                        if (traceCandidate)
                                        {
                                            logger?.WriteLine(
                                                $"Cleanup: corr-final trace candidate id={candidate.Id.Handle} action=seed-ordinary-carved-corridor " +
                                                $"effectiveA=({effectiveOuter.A.X:0.###},{effectiveOuter.A.Y:0.###}) effectiveB=({effectiveOuter.B.X:0.###},{effectiveOuter.B.Y:0.###}).");
                                        }
                                    }
                                }
                                else if (!preservedOrdinaryOuter.Id.IsNull)
                                {
                                    correctionOuterAnchors.Add((
                                        preservedOrdinaryOuter.A,
                                        preservedOrdinaryOuter.B,
                                        Midpoint(preservedOrdinaryOuter.A, preservedOrdinaryOuter.B),
                                        Math.Min(preservedOrdinaryOuter.A.X, preservedOrdinaryOuter.B.X),
                                        Math.Max(preservedOrdinaryOuter.A.X, preservedOrdinaryOuter.B.X)));
                                    correctionChain.Add((preservedOrdinaryOuter.A, preservedOrdinaryOuter.B));
                                    if (traceCandidate)
                                    {
                                        logger?.WriteLine(
                                            $"Cleanup: corr-final trace candidate id={candidate.Id.Handle} action=preserve-original-outer-with-zero " +
                                            $"effectiveA=({preservedOrdinaryOuter.A.X:0.###},{preservedOrdinaryOuter.A.Y:0.###}) effectiveB=({preservedOrdinaryOuter.B.X:0.###},{preservedOrdinaryOuter.B.Y:0.###}).");
                                    }
                                }

                                var innerDir = corridorInner.B - corridorInner.A;
                                var innerLen = innerDir.Length;
                                if (innerLen > 1e-6)
                                {
                                    correctionInnerAnchors.Add((
                                        corridorInner.A,
                                        corridorInner.B,
                                        Midpoint(corridorInner.A, corridorInner.B),
                                        innerDir / innerLen,
                                        innerLen));
                                }

                                twentyCandidates.RemoveAt(ci);
                                ci--;
                                ordinaryInsetDuplicateErased++;
                                if (traceCandidate)
                                {
                                    logger?.WriteLine(
                                        $"Cleanup: corr-final trace candidate id={candidate.Id.Handle} action=replace-ordinary-corridor-with-zero side={ordinaryCorridorOffsetSign} effectiveA=({corridorInner.A.X:0.###},{corridorInner.A.Y:0.###}) effectiveB=({corridorInner.B.X:0.###},{corridorInner.B.Y:0.###}).");
                                }
                                continue;
                            }

                            if (IsCorrectionUsecLayer(candidate.Layer) &&
                                !IsCorrectionLayer(candidate.Layer) &&
                                HasExistingCorrectionZeroCompanionCoverage(candidate.A, candidate.B))
                            {
                                if (traceCandidate)
                                {
                                    logger?.WriteLine(
                                        $"Cleanup: corr-final trace candidate id={candidate.Id.Handle} layer={candidate.Layer} skipped=existing-inner-corridor A=({candidate.A.X:0.###},{candidate.A.Y:0.###}) B=({candidate.B.X:0.###},{candidate.B.Y:0.###}).");
                                }
                                continue;
                            }

                            if (IsCorrectionUsecLayer(candidate.Layer) &&
                                !IsCorrectionLayer(candidate.Layer) &&
                                HasExistingCorrectionOuterCoverage(candidate.A, candidate.B))
                            {
                                if (traceCandidate)
                                {
                                    logger?.WriteLine(
                                        $"Cleanup: corr-final trace candidate id={candidate.Id.Handle} layer={candidate.Layer} skipped=existing-outer-corridor A=({candidate.A.X:0.###},{candidate.A.Y:0.###}) B=({candidate.B.X:0.###},{candidate.B.Y:0.###}).");
                                }
                                continue;
                            }

                            if (traceCandidate)
                            {
                                logger?.WriteLine(
                                    $"Cleanup: corr-final trace candidate id={candidate.Id.Handle} layer={candidate.Layer} skipped=no-inner A=({candidate.A.X:0.###},{candidate.A.Y:0.###}) B=({candidate.B.X:0.###},{candidate.B.Y:0.###}).");
                            }
                            continue;
                        }

                        var hasCoverage = HasAdequateExistingCorrectionOuterCoverage(candidate.A, candidate.B, innerIndex, offsetSign);
                        if (traceCandidate)
                        {
                            logger?.WriteLine(
                                $"Cleanup: corr-final trace candidate id={candidate.Id.Handle} layer={candidate.Layer} innerIndex={innerIndex} offsetSign={offsetSign} coverage={hasCoverage} A=({candidate.A.X:0.###},{candidate.A.Y:0.###}) B=({candidate.B.X:0.###},{candidate.B.Y:0.###}).");
                        }

                        if (!hasCoverage)
                        {
                            var promotedSegment = new CorrectionSegment(candidate.Id, candidate.Layer, candidate.A, candidate.B);
                            if (TryPromoteCorrectionOuterRespectingHardOwners(
                                    promotedSegment,
                                    twentyCandidates.Select(c => new CorrectionSegment(c.Id, c.Layer, c.A, c.B)).ToList(),
                                    out var promotedOuters))
                            {
                                for (var po = 0; po < promotedOuters.Count; po++)
                                {
                                    var effectiveOuter = promotedOuters[po];
                                    if (traceCandidate)
                                    {
                                        logger?.WriteLine(
                                            $"Cleanup: corr-final trace candidate id={candidate.Id.Handle} action=promote-no-coverage effectiveA=({effectiveOuter.A.X:0.###},{effectiveOuter.A.Y:0.###}) effectiveB=({effectiveOuter.B.X:0.###},{effectiveOuter.B.Y:0.###}).");
                                    }

                                    correctionOuterAnchors.Add((
                                        effectiveOuter.A,
                                        effectiveOuter.B,
                                        Midpoint(effectiveOuter.A, effectiveOuter.B),
                                        Math.Min(effectiveOuter.A.X, effectiveOuter.B.X),
                                        Math.Max(effectiveOuter.A.X, effectiveOuter.B.X)));
                                    correctionChain.Add((effectiveOuter.A, effectiveOuter.B));
                                }

                                converted++;
                            }

                            continue;
                        }

                        var startTouchesChain = EndpointTouchesCorrectionChain(candidate.A, correctionChain);
                        var endTouchesChain = EndpointTouchesCorrectionChain(candidate.B, correctionChain);
                        if (CorrectionOuterConsistencyPromotionPolicy.ShouldPromoteSegment(
                                startTouchesChain,
                                endTouchesChain,
                                hasParallelInsetCompanion: true))
                        {
                            if (traceCandidate)
                            {
                                logger?.WriteLine(
                                    $"Cleanup: corr-final trace candidate id={candidate.Id.Handle} retained=needs-chain startTouch={startTouchesChain} endTouch={endTouchesChain}.");
                            }
                            continue;
                        }

                        if (suppressedIds.Add(candidate.Id))
                        {
                            ordinaryInsetDuplicateSeeded++;
                            suppressedCandidates.Enqueue((ci, innerIndex, offsetSign));
                            if (traceCandidate)
                            {
                                logger?.WriteLine(
                                    $"Cleanup: corr-final trace candidate id={candidate.Id.Handle} action=suppress-ordinary-duplicate innerIndex={innerIndex} offsetSign={offsetSign}.");
                            }
                        }
                    }

                    while (suppressedCandidates.Count > 0)
                    {
                        var current = suppressedCandidates.Dequeue();
                        var source = twentyCandidates[current.CandidateIndex];
                        for (var ci = 0; ci < twentyCandidates.Count; ci++)
                        {
                            var candidate = twentyCandidates[ci];
                            if (candidate.Id == source.Id || suppressedIds.Contains(candidate.Id))
                            {
                                continue;
                            }

                            if (!IsCollinearAndAligned(source.A, source.B, candidate.A, candidate.B))
                            {
                                continue;
                            }

                            var endpointDistance = MinEndpointDistance(source.A, source.B, candidate.A, candidate.B);
                            var hasEndpointTouch = endpointDistance <= endpointTouchTol;
                            var hasOverlap = HasProjectedOverlap(source.A, source.B, candidate.A, candidate.B);
                            if (!hasEndpointTouch && !hasOverlap)
                            {
                                continue;
                            }

                            if (!MatchesInsetDuplicateInnerAnchor(
                                    candidate.A,
                                    candidate.B,
                                    current.InnerIndex,
                                    current.OffsetSign))
                            {
                                continue;
                            }

                            suppressedIds.Add(candidate.Id);
                            ordinaryInsetDuplicateExtended++;
                            suppressedCandidates.Enqueue((ci, current.InnerIndex, current.OffsetSign));
                        }
                    }

                    if (suppressedIds.Count > 0)
                    {
                        for (var ci = twentyCandidates.Count - 1; ci >= 0; ci--)
                        {
                            var candidate = twentyCandidates[ci];
                            var traceCandidate = ShouldTraceFinalOuterCandidate(candidate.A, candidate.B);
                            if (!suppressedIds.Contains(candidate.Id))
                            {
                                continue;
                            }

                            Entity? writable = null;
                            try
                            {
                                writable = tr.GetObject(candidate.Id, OpenMode.ForWrite, false) as Entity;
                            }
                            catch (Autodesk.AutoCAD.Runtime.Exception)
                            {
                                continue;
                            }

                            if (writable == null || writable.IsErased)
                            {
                                twentyCandidates.RemoveAt(ci);
                                continue;
                            }

                            writable.Erase();
                            ordinaryInsetDuplicateErased++;
                            twentyCandidates.RemoveAt(ci);
                            if (traceCandidate)
                            {
                                logger?.WriteLine(
                                    $"Cleanup: corr-final trace candidate id={candidate.Id.Handle} action=erase-suppressed.");
                            }
                        }
                    }
                }

                if (correctionOuterAnchors.Count > 0 && twentyCandidates.Count > 0)
                {
                    twentyCandidates = twentyCandidates
                        .Where(candidate => !MatchesExistingCorrectionOuterAnchor(candidate.A, candidate.B))
                        .ToList();
                }

                if (anchors > 0 && twentyCandidates.Count > 0)
                {
                    var progress = true;
                    while (progress && twentyCandidates.Count > 0)
                    {
                        progress = false;
                        for (var ci = twentyCandidates.Count - 1; ci >= 0; ci--)
                        {
                            var candidate = twentyCandidates[ci];
                            var traceCandidate = ShouldTraceFinalOuterCandidate(candidate.A, candidate.B);
                            var attachToChain = false;
                            var hasParallelInsetCompanion = HasParallelInsetCompanion(candidate.A, candidate.B);
                            var startTouchesChain = EndpointTouchesCorrectionChain(candidate.A, correctionChain);
                            var endTouchesChain = EndpointTouchesCorrectionChain(candidate.B, correctionChain);
                            for (var ai = 0; ai < correctionChain.Count; ai++)
                            {
                                var anchor = correctionChain[ai];
                                if (!IsCollinearAndAligned(candidate.A, candidate.B, anchor.A, anchor.B))
                                {
                                    continue;
                                }

                                var endpointDistance = MinEndpointDistance(candidate.A, candidate.B, anchor.A, anchor.B);
                                var hasEndpointTouch = endpointDistance <= endpointTouchTol;
                                var hasOverlap = HasProjectedOverlap(candidate.A, candidate.B, anchor.A, anchor.B);
                                if (!hasEndpointTouch && !hasOverlap)
                                {
                                    continue;
                                }

                                if (!CorrectionOuterConsistencyPromotionPolicy.ShouldPromoteSegment(
                                        startTouchesChain,
                                        endTouchesChain,
                                        hasParallelInsetCompanion))
                                {
                                    insetCompanionSuppressed++;
                                    continue;
                                }

                                attachToChain = true;
                                break;
                            }

                            if (!attachToChain)
                            {
                                if (traceCandidate)
                                {
                                    logger?.WriteLine(
                                        $"Cleanup: corr-final trace candidate id={candidate.Id.Handle} skipped=no-chain startTouch={startTouchesChain} endTouch={endTouchesChain} hasInset={hasParallelInsetCompanion}.");
                                }
                                continue;
                            }

                            Entity? writable = null;
                            try
                            {
                                writable = tr.GetObject(candidate.Id, OpenMode.ForWrite, false) as Entity;
                            }
                            catch (Autodesk.AutoCAD.Runtime.Exception)
                            {
                                continue;
                            }

                            if (writable == null || writable.IsErased)
                            {
                                continue;
                            }

                            var candidateSegment = new CorrectionSegment(candidate.Id, writable.Layer ?? string.Empty, candidate.A, candidate.B);
                            if (!TryEnsureCorrectionOuterSegment(candidateSegment, twentyCandidates.Select(c => new CorrectionSegment(c.Id, string.Empty, c.A, c.B)).ToList(), ms, tr, out _, out var createdOuterClone))
                            {
                                if (traceCandidate)
                                {
                                    logger?.WriteLine(
                                        $"Cleanup: corr-final trace candidate id={candidate.Id.Handle} failed=ensure-outer.");
                                }
                                continue;
                            }

                            if (!createdOuterClone)
                            {
                                twentyCandidates.RemoveAt(ci);
                                progress = true;
                                if (traceCandidate)
                                {
                                    logger?.WriteLine(
                                        $"Cleanup: corr-final trace candidate id={candidate.Id.Handle} action=relayer-chain.");
                                }
                                continue;
                            }

                            converted++;
                            correctionChain.Add((candidate.A, candidate.B));
                            twentyCandidates.RemoveAt(ci);
                            progress = true;
                            if (traceCandidate)
                            {
                                logger?.WriteLine(
                                    $"Cleanup: corr-final trace candidate id={candidate.Id.Handle} action=clone-chain.");
                            }
                        }
                    }
                }

                int RestoreDeflectedCorrectionOuterAcrossVerticalGaps()
                {
                    if (correctionOuterAnchors.Count == 0 ||
                        ordinaryVerticalCorridorAnchors.Count == 0 ||
                        sectionVerticalSplitSegments.Count == 0)
                    {
                        return 0;
                    }

                    bool IsThirtyVerticalLayer(string layer)
                    {
                        return string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(layer, "L-USEC3018", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(layer, "L-USEC-3018", StringComparison.OrdinalIgnoreCase);
                    }

                    bool IsZeroVerticalLayer(string layer)
                    {
                        return string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase);
                    }

                    var liveOuterSegments = CollectHorizontalCorrectionSegments(
                        tr,
                        ms,
                        layer => string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase));
                    liveOuterSegments.AddRange(
                        correctionOuterAnchors.Select(anchor => new CorrectionSegment(ObjectId.Null, LayerUsecCorrection, anchor.A, anchor.B)));

                    bool HasOuter(Point2d a, Point2d b) => HasMatchingCorrectionSegment(liveOuterSegments, a, b);

                    bool TryAddOuter(Point2d a, Point2d b, string reason)
                    {
                        if (a.GetDistanceTo(b) <= 1e-4 || HasOuter(a, b))
                        {
                            return false;
                        }

                        var line = new Line(
                            new Point3d(a.X, a.Y, 0.0),
                            new Point3d(b.X, b.Y, 0.0))
                        {
                            Layer = LayerUsecCorrection,
                            ColorIndex = 256
                        };

                        var id = ms.AppendEntity(line);
                        tr.AddNewlyCreatedDBObject(line, true);
                        if (id.IsNull)
                        {
                            return false;
                        }

                        var restored = new CorrectionSegment(id, LayerUsecCorrection, a, b);
                        liveOuterSegments.Add(restored);
                        correctionOuterAnchors.Add((a, b, Midpoint(a, b), Math.Min(a.X, b.X), Math.Max(a.X, b.X)));
                        correctionChain.Add((a, b));
                        logger?.WriteLine(
                            $"Cleanup: restored deflected correction outer {reason} A=({a.X:0.###},{a.Y:0.###}) B=({b.X:0.###},{b.Y:0.###}).");
                        return true;
                    }

                    bool TryFindThirtyGapEndpoint(
                        Point2d anchorEndpoint,
                        Vector2d outwardUnit,
                        Point2d lineA,
                        Point2d lineB,
                        out Point2d gapEndpoint)
                    {
                        gapEndpoint = default;
                        var found = false;
                        var bestAlong = double.MaxValue;
                        const double gapMin = 18.0;
                        const double gapMax = 35.5;
                        const double collinearEndpointTol = 0.90;
                        foreach (var vertical in ordinaryVerticalCorridorAnchors)
                        {
                            if (!IsThirtyVerticalLayer(vertical.Layer))
                            {
                                continue;
                            }

                            var endpoints = new[] { vertical.A, vertical.B };
                            for (var ei = 0; ei < endpoints.Length; ei++)
                            {
                                var candidate = endpoints[ei];
                                var delta = candidate - anchorEndpoint;
                                var along = delta.DotProduct(outwardUnit);
                                if (along < gapMin || along > gapMax)
                                {
                                    continue;
                                }

                                if (DistancePointToInfiniteLine(candidate, lineA, lineB) > collinearEndpointTol)
                                {
                                    continue;
                                }

                                if (!found || along < bestAlong)
                                {
                                    found = true;
                                    bestAlong = along;
                                    gapEndpoint = candidate;
                                }
                            }
                        }

                        return found;
                    }

                    bool TryFindNextSectionSplit(
                        Point2d from,
                        Vector2d outwardUnit,
                        Point2d lineA,
                        Point2d lineB,
                        out Point2d splitPoint)
                    {
                        splitPoint = default;
                        var found = false;
                        var bestAlong = double.MaxValue;
                        const double minAlong = 80.0;
                        const double maxAlong = 1200.0;
                        const double splitTouchTol = 0.90;
                        foreach (var split in sectionVerticalSplitSegments)
                        {
                            if (!TryIntersectInfiniteLinesForPluginGeometry(lineA, lineB, split.A, split.B, out var intersection))
                            {
                                continue;
                            }

                            var along = (intersection - from).DotProduct(outwardUnit);
                            if (along < minAlong || along > maxAlong)
                            {
                                continue;
                            }

                            if (DistancePointToSegment(intersection, split.A, split.B) > splitTouchTol)
                            {
                                continue;
                            }

                            if (!found || along < bestAlong)
                            {
                                found = true;
                                bestAlong = along;
                                splitPoint = intersection;
                            }
                        }

                        return found;
                    }

                    bool TryRetargetCorrectionOuterAsZeroCompanion(
                        CorrectionSegment source,
                        Point2d near,
                        Point2d far)
                    {
                        if (source.Id.IsNull || near.GetDistanceTo(far) <= 1e-4)
                        {
                            return false;
                        }

                        Entity? writable = null;
                        try
                        {
                            writable = tr.GetObject(source.Id, OpenMode.ForWrite, false) as Entity;
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception)
                        {
                            return false;
                        }

                        if (writable == null || writable.IsErased)
                        {
                            return false;
                        }

                        if (writable is Line line)
                        {
                            var start = new Point2d(line.StartPoint.X, line.StartPoint.Y);
                            var end = new Point2d(line.EndPoint.X, line.EndPoint.Y);
                            var keepForward =
                                start.GetDistanceTo(source.A) + end.GetDistanceTo(source.B) <=
                                start.GetDistanceTo(source.B) + end.GetDistanceTo(source.A);
                            line.StartPoint = keepForward
                                ? new Point3d(near.X, near.Y, line.StartPoint.Z)
                                : new Point3d(far.X, far.Y, line.StartPoint.Z);
                            line.EndPoint = keepForward
                                ? new Point3d(far.X, far.Y, line.EndPoint.Z)
                                : new Point3d(near.X, near.Y, line.EndPoint.Z);
                            line.Layer = LayerUsecCorrectionZero;
                            line.ColorIndex = 256;
                            return true;
                        }

                        if (writable is Polyline polyline && !polyline.Closed && polyline.NumberOfVertices >= 2)
                        {
                            var startIndex = 0;
                            var endIndex = polyline.NumberOfVertices - 1;
                            var start = polyline.GetPoint2dAt(startIndex);
                            var end = polyline.GetPoint2dAt(endIndex);
                            var keepForward =
                                start.GetDistanceTo(source.A) + end.GetDistanceTo(source.B) <=
                                start.GetDistanceTo(source.B) + end.GetDistanceTo(source.A);
                            polyline.SetPointAt(startIndex, keepForward ? near : far);
                            polyline.SetPointAt(endIndex, keepForward ? far : near);
                            polyline.Layer = LayerUsecCorrectionZero;
                            polyline.ColorIndex = 256;
                            return true;
                        }

                        return false;
                    }

                    bool TryFindZeroEndpointForOffsetDiagonal(
                        Point2d splitPoint,
                        Vector2d outwardUnit,
                        out Point2d zeroEndpoint,
                        out CorrectionSegment diagonalOuter,
                        out Point2d correctedOffsetNear,
                        out Point2d correctedOffsetFar)
                    {
                        zeroEndpoint = default;
                        diagonalOuter = default;
                        correctedOffsetNear = default;
                        correctedOffsetFar = default;
                        var found = false;
                        var bestScore = double.MaxValue;
                        const double splitOffsetMin = 3.0;
                        const double splitOffsetMax = 7.5;
                        const double zeroEndpointTouchTol = 1.50;
                        const double minAlong = 100.0;
                        const double maxAlong = 1200.0;

                        foreach (var outer in liveOuterSegments)
                        {
                            if (outer.Id.IsNull ||
                                !string.Equals(outer.Layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            var endpoints = new[] { outer.A, outer.B };
                            for (var nearIndex = 0; nearIndex <= 1; nearIndex++)
                            {
                                var near = endpoints[nearIndex];
                                var far = endpoints[1 - nearIndex];
                                var splitGap = near.GetDistanceTo(splitPoint);
                                if (splitGap < splitOffsetMin || splitGap > splitOffsetMax)
                                {
                                    continue;
                                }

                                var along = (far - splitPoint).DotProduct(outwardUnit);
                                if (along < minAlong || along > maxAlong)
                                {
                                    continue;
                                }

                                foreach (var vertical in ordinaryVerticalCorridorAnchors)
                                {
                                    if (!IsZeroVerticalLayer(vertical.Layer))
                                    {
                                        continue;
                                    }

                                    var verticalEndpoints = new[] { vertical.A, vertical.B };
                                    for (var vi = 0; vi < verticalEndpoints.Length; vi++)
                                    {
                                        var candidate = verticalEndpoints[vi];
                                        var endpointGap = candidate.GetDistanceTo(far);
                                        if (endpointGap > zeroEndpointTouchTol)
                                        {
                                            continue;
                                        }

                                        var axisGap = Math.Abs(candidate.Y - splitPoint.Y);
                                        if (axisGap < 18.0 || axisGap > 35.5)
                                        {
                                            continue;
                                        }

                                        var insetVector = near - splitPoint;
                                        var recoveredOriginalFar = far - insetVector;
                                        var score = splitGap + endpointGap + Math.Abs(along - 800.0) * 0.001;
                                        if (!found || score < bestScore)
                                        {
                                            found = true;
                                            bestScore = score;
                                            zeroEndpoint = recoveredOriginalFar;
                                            diagonalOuter = outer;
                                            correctedOffsetNear = near;
                                            correctedOffsetFar = far;
                                        }
                                    }
                                }
                            }
                        }

                        return found;
                    }

                    bool TryFindThirtyEndpointForLocalZeroEndpoint(
                        Point2d zeroEndpoint,
                        Point2d lineA,
                        Point2d lineB,
                        out Point2d thirtyEndpoint)
                    {
                        thirtyEndpoint = default;
                        var dir = lineB - lineA;
                        var len = dir.Length;
                        if (len <= 1e-6)
                        {
                            return false;
                        }

                        var u = dir / len;
                        var found = false;
                        var bestScore = double.MaxValue;
                        const double alongMin = -2.0;
                        const double alongMax = 16.0;
                        const double endpointDistanceMax = 18.0;
                        const double offsetTol = 1.40;
                        foreach (var vertical in ordinaryVerticalCorridorAnchors)
                        {
                            if (!IsThirtyVerticalLayer(vertical.Layer))
                            {
                                continue;
                            }

                            var endpoints = new[] { vertical.A, vertical.B };
                            for (var ei = 0; ei < endpoints.Length; ei++)
                            {
                                var candidate = endpoints[ei];
                                var delta = candidate - zeroEndpoint;
                                var along = delta.DotProduct(u);
                                if (along < alongMin || along > alongMax)
                                {
                                    continue;
                                }

                                var endpointDistance = zeroEndpoint.GetDistanceTo(candidate);
                                if (endpointDistance > endpointDistanceMax)
                                {
                                    continue;
                                }

                                var offsetDistance = Math.Abs(DistancePointToInfiniteLine(candidate, lineA, lineB));
                                var offsetDelta = Math.Abs(offsetDistance - CorrectionLinePostInsetMeters);
                                if (offsetDelta > offsetTol)
                                {
                                    continue;
                                }

                                var score = offsetDelta + (endpointDistance * 0.01);
                                if (!found || score < bestScore)
                                {
                                    found = true;
                                    bestScore = score;
                                    thirtyEndpoint = candidate;
                                }
                            }
                        }

                        return found;
                    }

                    bool TryFindOuterEndpointForLocalZeroEndpoint(
                        Point2d zeroEndpoint,
                        Point2d lineA,
                        Point2d lineB,
                        out Point2d outerEndpoint)
                    {
                        outerEndpoint = default;
                        var dir = lineB - lineA;
                        var len = dir.Length;
                        if (len <= 1e-6)
                        {
                            return false;
                        }

                        var u = dir / len;
                        var found = false;
                        var bestScore = double.MaxValue;
                        const double endpointDistanceMax = 7.0;
                        const double alongTol = 1.60;
                        const double offsetTol = 1.25;
                        foreach (var outer in liveOuterSegments)
                        {
                            if (!string.Equals(outer.Layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            var endpoints = new[] { outer.A, outer.B };
                            for (var ei = 0; ei < endpoints.Length; ei++)
                            {
                                var candidate = endpoints[ei];
                                var delta = candidate - zeroEndpoint;
                                var endpointDistance = zeroEndpoint.GetDistanceTo(candidate);
                                if (endpointDistance > endpointDistanceMax)
                                {
                                    continue;
                                }

                                var alongGap = Math.Abs(delta.DotProduct(u));
                                if (alongGap > alongTol)
                                {
                                    continue;
                                }

                                var perpendicularGap = Math.Sqrt(Math.Max(0.0, (endpointDistance * endpointDistance) - (alongGap * alongGap)));
                                var offsetDelta = Math.Abs(perpendicularGap - CorrectionLinePostInsetMeters);
                                if (offsetDelta > offsetTol)
                                {
                                    continue;
                                }

                                var score = offsetDelta + alongGap;
                                if (!found || score < bestScore)
                                {
                                    found = true;
                                    bestScore = score;
                                    outerEndpoint = candidate;
                                }
                            }
                        }

                        return found;
                    }

                    int RestoreOuterSpansFromLocalZeroCompanions()
                    {
                        var liveInnerSegments = CollectHorizontalCorrectionSegments(
                            tr,
                            ms,
                            layer => string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase));
                        var restored = 0;
                        const double minRestoreLength = 80.0;
                        const double maxLocalCompanionLength = 1200.0;
                        foreach (var inner in liveInnerSegments)
                        {
                            if (!inner.IsHorizontalLike ||
                                inner.Length < minRestoreLength ||
                                inner.Length > maxLocalCompanionLength)
                            {
                                continue;
                            }

                            var endpointPairs = new[]
                            {
                                (ZeroStart: inner.A, ZeroEnd: inner.B),
                                (ZeroStart: inner.B, ZeroEnd: inner.A),
                            };
                            for (var pi = 0; pi < endpointPairs.Length; pi++)
                            {
                                var pair = endpointPairs[pi];
                                var hasThirty = TryFindThirtyEndpointForLocalZeroEndpoint(pair.ZeroStart, inner.A, inner.B, out var thirtyEndpoint);
                                var hasOuter = TryFindOuterEndpointForLocalZeroEndpoint(pair.ZeroEnd, inner.A, inner.B, out var outerEndpoint);

                                if (!hasThirty ||
                                    !hasOuter ||
                                    thirtyEndpoint.GetDistanceTo(outerEndpoint) < minRestoreLength)
                                {
                                    continue;
                                }

                                var innerDir = inner.B - inner.A;
                                var restoreDir = outerEndpoint - thirtyEndpoint;
                                if (innerDir.Length <= 1e-6 ||
                                    restoreDir.Length <= 1e-6 ||
                                    Math.Abs((innerDir / innerDir.Length).DotProduct(restoreDir / restoreDir.Length)) < 0.985)
                                {
                                    continue;
                                }

                                if (HasMatchingCorrectionSegment(liveInnerSegments, thirtyEndpoint, outerEndpoint))
                                {
                                    continue;
                                }

                                if (TryAddOuter(thirtyEndpoint, outerEndpoint, "from local zero companion to 30.18 vertical endpoint"))
                                {
                                    restored++;
                                }
                            }
                        }

                        return restored;
                    }

                    double GetProjectedParameter(Point2d point, Point2d a, Point2d b)
                    {
                        var dir = b - a;
                        var lenSquared = dir.DotProduct(dir);
                        if (lenSquared <= 1e-9)
                        {
                            return 0.0;
                        }

                        return ((point - a).DotProduct(dir)) / lenSquared;
                    }

                    int CountInteriorVerticalBreakpoints(CorrectionSegment segment)
                    {
                        var breakpoints = new List<Point2d>();
                        const double verticalTouchTol = 16.0;
                        foreach (var vertical in ordinaryVerticalCorridorAnchors)
                        {
                            var endpoints = new[] { vertical.A, vertical.B };
                            for (var ei = 0; ei < endpoints.Length; ei++)
                            {
                                var endpoint = endpoints[ei];
                                var parameter = GetProjectedParameter(endpoint, segment.A, segment.B);
                                if (parameter <= 0.05 || parameter >= 0.95)
                                {
                                    continue;
                                }

                                if (DistancePointToSegment(endpoint, segment.A, segment.B) > verticalTouchTol)
                                {
                                    continue;
                                }

                                if (breakpoints.Any(existing => existing.GetDistanceTo(endpoint) <= 18.0))
                                {
                                    continue;
                                }

                                breakpoints.Add(endpoint);
                            }
                        }

                        return breakpoints.Count;
                    }

                    int EraseOverlongCorrectionZeroBridges()
                    {
                        var liveInnerSegments = CollectHorizontalCorrectionSegments(
                            tr,
                            ms,
                            layer => string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase));
                        var erased = 0;
                        const double overlongBridgeMinLength = 1600.0;
                        foreach (var inner in liveInnerSegments)
                        {
                            var breakpointCount = CountInteriorVerticalBreakpoints(inner);

                            if (inner.Id.IsNull ||
                                !inner.IsHorizontalLike ||
                                inner.Length < overlongBridgeMinLength ||
                                breakpointCount < 2)
                            {
                                continue;
                            }

                            Entity? writable = null;
                            try
                            {
                                writable = tr.GetObject(inner.Id, OpenMode.ForWrite, false) as Entity;
                            }
                            catch (Autodesk.AutoCAD.Runtime.Exception)
                            {
                                continue;
                            }

                            if (writable == null || writable.IsErased)
                            {
                                continue;
                            }

                            writable.Erase();
                            erased++;
                            logger?.WriteLine(
                                $"Cleanup: erased overlong correction zero bridge A=({inner.A.X:0.###},{inner.A.Y:0.###}) B=({inner.B.X:0.###},{inner.B.Y:0.###}).");
                        }

                        return erased;
                    }

                    var restoredCount = 0;
                    var initialOuterCount = correctionOuterAnchors.Count;
                    for (var ai = 0; ai < initialOuterCount; ai++)
                    {
                        var anchor = correctionOuterAnchors[ai];
                        var endpoints = new[] { anchor.A, anchor.B };
                        for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                        {
                            var anchorEndpoint = endpoints[endpointIndex];
                            var anchorOther = endpoints[1 - endpointIndex];
                            var outward = anchorEndpoint - anchorOther;
                            var outwardLen = outward.Length;
                            if (outwardLen <= 1e-6)
                            {
                                continue;
                            }

                            var outwardUnit = outward / outwardLen;
                            if (!TryFindThirtyGapEndpoint(anchorEndpoint, outwardUnit, anchor.A, anchor.B, out var gapEndpoint))
                            {
                                continue;
                            }

                            if (!TryFindNextSectionSplit(gapEndpoint, outwardUnit, anchor.A, anchor.B, out var splitPoint))
                            {
                                continue;
                            }

                            if (!TryFindZeroEndpointForOffsetDiagonal(
                                    splitPoint,
                                    outwardUnit,
                                    out var zeroEndpoint,
                                    out var diagonalOuter,
                                    out var correctedOffsetNear,
                                    out var correctedOffsetFar) ||
                                zeroEndpoint.GetDistanceTo(splitPoint) <= 1e-4)
                            {
                                continue;
                            }

                            var splitToOffsetNearGap = splitPoint.GetDistanceTo(correctedOffsetNear);
                            var splitHasStandardCorrectionInset =
                                Math.Abs(splitToOffsetNearGap - CorrectionLinePostInsetMeters) <= 0.75;
                            var firstOuterEnd = splitHasStandardCorrectionInset ? splitPoint : correctedOffsetNear;

                            if (TryAddOuter(
                                    gapEndpoint,
                                    firstOuterEnd,
                                    splitHasStandardCorrectionInset
                                        ? "across 30.18 gap to split"
                                        : "across 30.18 gap to clipped correction-zero buffer endpoint"))
                            {
                                restoredCount++;
                            }

                            if (splitHasStandardCorrectionInset &&
                                TryAddOuter(splitPoint, zeroEndpoint, "from split to same-side zero boundary"))
                            {
                                restoredCount++;
                            }

                            if (TryRetargetCorrectionOuterAsZeroCompanion(diagonalOuter, correctedOffsetNear, correctedOffsetFar))
                            {
                                restoredCount++;
                                logger?.WriteLine(
                                    $"Cleanup: retargeted deflected correction offset companion A=({correctedOffsetNear.X:0.###},{correctedOffsetNear.Y:0.###}) B=({correctedOffsetFar.X:0.###},{correctedOffsetFar.Y:0.###}).");
                            }
                        }
                    }

                    restoredCount += RestoreOuterSpansFromLocalZeroCompanions();
                    restoredCount += EraseOverlongCorrectionZeroBridges();

                    return restoredCount;
                }

                var restoredDeflectedCorrectionOuters = RestoreDeflectedCorrectionOuterAcrossVerticalGaps();
                converted += restoredDeflectedCorrectionOuters;

                tr.Commit();
            }

            logger?.WriteLine(
                $"CorrectionLine: final outer layer consistency converted {converted} segment(s) to {LayerUsecCorrection} " +
                $"(anchors={anchors}, insetCompanionSuppressed={insetCompanionSuppressed}, duplicateInsetErased={ordinaryInsetDuplicateErased}, " +
                $"duplicateSeeds={ordinaryInsetDuplicateSeeded}, duplicateExtended={ordinaryInsetDuplicateExtended}).");
            return converted > 0 || ordinaryInsetDuplicateErased > 0;
        }

        private static bool ConnectCorrectionInnerEndpointsToVerticalUsecBoundaries(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null)
            {
                return false;
            }

            var clipWindows = MergeOverlappingClipWindows(BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0));
            var coreClipWindows = MergeOverlappingClipWindows(BuildBufferedQuarterWindows(database, requestedQuarterIds, 0.0));
            if (clipWindows.Count == 0)
            {
                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b) => DoesSegmentIntersectAnyWindowForCorrectionLinePost(a, b, clipWindows);

            bool IsHorizontalLike(Point2d a, Point2d b) => IsHorizontalLikeForCorrectionLinePost(a, b);

            bool IsVerticalLike(Point2d a, Point2d b) => IsVerticalLikeForCorrectionLinePost(a, b);

            bool IsVerticalHardTargetLayer(string layer)
            {
                return string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC2012", StringComparison.OrdinalIgnoreCase) ||
                       IsCorrectionSurveyedLayer(layer);
            }

            bool IsVerticalHardAnchorLayer(string layer)
            {
                return IsVerticalHardTargetLayer(layer) ||
                       string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-3018", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC3018", StringComparison.OrdinalIgnoreCase);
            }

            bool IsVerticalProtectedRestoreLayer(string layer)
            {
                return IsVerticalHardTargetLayer(layer) ||
                       string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
            }

            bool IsSectionVerticalSplitLayer(string layer)
            {
                return string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-QSEC", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase);
            }

            bool TryMoveEndpoint(Entity writable, bool moveStart, Point2d target, double moveTol) => TryMoveEndpointForCorrectionLinePost(writable, moveStart, target, moveTol);

            bool TryIntersectInfiniteLines(Point2d a0, Point2d a1, Point2d b0, Point2d b1, out Point2d intersection) =>
                TryIntersectInfiniteLinesForPluginGeometry(a0, a1, b0, b1, out intersection);

            bool TryResolveInnerDirectionSign(
                Point2d a,
                Point2d b,
                IReadOnlyList<(Point2d A, Point2d B, Point2d Mid, double MinX, double MaxX)> outerCandidates,
                out int sign)
            {
                sign = 0;
                if (outerCandidates == null || outerCandidates.Count == 0)
                {
                    return false;
                }

                var sourceMinX = Math.Min(a.X, b.X);
                var sourceMaxX = Math.Max(a.X, b.X);
                var sourceMid = Midpoint(a, b);
                var sourceLength = a.GetDistanceTo(b);
                var minRequiredOverlap = Math.Max(16.0, Math.Min(sourceLength * 0.2, 55.0));

                var found = false;
                var bestOverlap = double.MinValue;
                var bestOffsetDelta = double.MaxValue;
                var bestSign = 0;
                for (var i = 0; i < outerCandidates.Count; i++)
                {
                    var outer = outerCandidates[i];
                    var overlapMin = Math.Max(sourceMinX, outer.MinX);
                    var overlapMax = Math.Min(sourceMaxX, outer.MaxX);
                    var overlap = overlapMax - overlapMin;
                    if (overlap < minRequiredOverlap)
                    {
                        continue;
                    }

                    var offset = Math.Abs(outer.Mid.Y - sourceMid.Y);
                    var offsetDelta = Math.Abs(offset - CorrectionLinePostInsetMeters);
                    if (offsetDelta > 2.2)
                    {
                        continue;
                    }

                    var candidateSign = outer.Mid.Y >= sourceMid.Y ? 1 : -1;
                    if (!found ||
                        overlap > bestOverlap + 1e-6 ||
                        (Math.Abs(overlap - bestOverlap) <= 1e-6 && offsetDelta < bestOffsetDelta))
                    {
                        found = true;
                        bestOverlap = overlap;
                        bestOffsetDelta = offsetDelta;
                        bestSign = candidateSign;
                    }
                }

                if (found)
                {
                    sign = bestSign;
                    return true;
                }

                // Fallback: if companion pairing is imperfect, infer north/south from nearest
                // correction outer by vertical proximity over any overlap with the source span.
                var bestDistance = double.MaxValue;
                var fallbackSign = 0;
                for (var i = 0; i < outerCandidates.Count; i++)
                {
                    var outer = outerCandidates[i];
                    var overlapMin = Math.Max(sourceMinX, outer.MinX);
                    var overlapMax = Math.Min(sourceMaxX, outer.MaxX);
                    if (overlapMax < overlapMin - 6.0)
                    {
                        continue;
                    }

                    var distance = Math.Abs(outer.Mid.Y - sourceMid.Y);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        fallbackSign = outer.Mid.Y >= sourceMid.Y ? 1 : -1;
                    }
                }

                if (fallbackSign == 0)
                {
                    return false;
                }

                sign = fallbackSign;
                return true;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                var correctionInnerSources = new List<(ObjectId Id, Point2d A, Point2d B)>();
                var correctionOuterSources = new List<(ObjectId Id, Point2d A, Point2d B)>();
                var correctionOuterSegments = new List<(Point2d A, Point2d B, Point2d Mid, double MinX, double MaxX)>();
                var verticalTargets = new List<(ObjectId Id, Point2d A, Point2d B, double MinY, double MaxY, double AxisX)>();
                var verticalProtectedRestoreTargets = new List<(ObjectId Id, Point2d A, Point2d B, double MinY, double MaxY, double AxisX)>();
                var verticalTwentyTargets = new List<(ObjectId Id, Point2d A, Point2d B, double MinY, double MaxY, double AxisX)>();
                var verticalThirtyTargets = new List<(ObjectId Id, Point2d A, Point2d B, double MinY, double MaxY, double AxisX)>();
                var verticalEndpointAnchors = new List<(Point2d A, Point2d B)>();
                var horizontalEndpointAnchors = new List<(ObjectId Id, Point2d A, Point2d B)>();
                var sectionVerticalSplitSegments = new List<(Point2d A, Point2d B, double MinY, double MaxY)>();
                var outerSectionVerticalSplitSegments = new List<(Point2d A, Point2d B, double MinY, double MaxY)>();

                foreach (ObjectId id in ms)
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenLinearSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    if (!DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    if (IsHorizontalLike(a, b))
                    {
                        horizontalEndpointAnchors.Add((id, a, b));
                    }

                    if (IsVerticalLike(a, b) && IsVerticalHardAnchorLayer(ent.Layer ?? string.Empty))
                    {
                        verticalEndpointAnchors.Add((a, b));
                    }

                    var layer = ent.Layer ?? string.Empty;
                    if (IsVerticalLike(a, b) && IsVerticalProtectedRestoreLayer(layer))
                    {
                        verticalProtectedRestoreTargets.Add((id, a, b, Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y), 0.5 * (a.X + b.X)));
                    }

                    if (IsVerticalLike(a, b) &&
                        (string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(layer, "L-USEC2012", StringComparison.OrdinalIgnoreCase)))
                    {
                        verticalTwentyTargets.Add((id, a, b, Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y), 0.5 * (a.X + b.X)));
                    }

                    if (IsVerticalLike(a, b) &&
                        (string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(layer, "L-USEC-3018", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(layer, "L-USEC3018", StringComparison.OrdinalIgnoreCase)))
                    {
                        verticalThirtyTargets.Add((id, a, b, Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y), 0.5 * (a.X + b.X)));
                    }

                    if (IsVerticalLike(a, b) && IsSectionVerticalSplitLayer(layer))
                    {
                        sectionVerticalSplitSegments.Add((a, b, Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y)));
                        if (!string.Equals(layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase))
                        {
                            outerSectionVerticalSplitSegments.Add((a, b, Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y)));
                        }
                    }

                    if (string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) &&
                        IsHorizontalLike(a, b))
                    {
                        correctionInnerSources.Add((id, a, b));
                        continue;
                    }

                    if (string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase) &&
                        IsHorizontalLike(a, b))
                    {
                        correctionOuterSources.Add((id, a, b));
                        correctionOuterSegments.Add((a, b, Midpoint(a, b), Math.Min(a.X, b.X), Math.Max(a.X, b.X)));
                        continue;
                    }

                    if (IsVerticalHardTargetLayer(layer) && IsVerticalLike(a, b))
                    {
                        verticalTargets.Add((id, a, b, Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y), 0.5 * (a.X + b.X)));
                    }
                }

                if ((correctionInnerSources.Count == 0 && correctionOuterSources.Count == 0) ||
                    (verticalTargets.Count == 0 && verticalProtectedRestoreTargets.Count == 0 && correctionOuterSegments.Count == 0 && sectionVerticalSplitSegments.Count == 0))
                {
                    tr.Commit();
                    return false;
                }

                const double endpointTouchTol = 0.35;
                const double endpointMoveTol = 0.05;
                const double minMove = 0.05;
                const double maxMove = 1200.0;
                // Keep correction inner-to-vertical cleanup local; large seam-facing pulls can create
                // false south spikes on correction-line townships when a distant vertical target wins.
                const double maxVerticalTargetGap = CorrectionLinePostExpectedUsecWidthMeters * 6.0;
                const double maxVerticalTargetLength = 2200.0;
                const double maxVerticalEndpointMove = CorrectionLinePostExpectedUsecWidthMeters * 6.0;
                const double minRemainingLength = 2.0;
                const double directionAxisTol = 0.05;
                const double inlineVerticalTol = 0.80;
                const double protectedCompanionOffsetTol = 0.12;
                const double protectedCompanionProjectionTol = 0.20;
                const double protectedCompanionReverseOffsetTol = 0.35;
                const double protectedCompanionDirectionDotMin = 0.985;
                const double protectedCompanionRestoreMax = 0.08;
                const double protectedTouchingVerticalRestoreMax = CorrectionLinePostInsetMeters * 6.0;
                const double protectedOuterCompanionOverhangKeepMin = 12.0;
                const double exactEndpointMoveTol = 0.001;

                var movedLines = 0;
                var movedEndpoints = 0;
                var movedHorizontalEndpoints = 0;
                var movedVerticalEndpoints = 0;
                var scannedEndpoints = 0;
                var noDirectionResolved = 0;
                var noTargetFound = 0;
                var alreadyConnected = 0;
                var sharedEndpointSkipped = 0;
                var splitInnerSources = 0;
                var splitInnerCreated = 0;
                var splitInnerFromOuterAnchors = 0;
                var splitOuterSources = 0;
                var splitOuterCreated = 0;
                var erasedOverlongCorrectionZeroBridges = 0;
                var sampleMoves = new List<string>();
                var verticalTargetBestSnapByEndpoint = new Dictionary<(ObjectId TargetId, bool MoveStart), (Point2d OriginalEndpoint, double BestDistanceFromOriginal)>();

                double SignedDistanceToInfiniteLine(Point2d point, Point2d lineA, Point2d lineB)
                {
                    var dir = lineB - lineA;
                    var len = dir.Length;
                    if (len <= 1e-6)
                    {
                        return 0.0;
                    }

                    return (((point.X - lineA.X) * dir.Y) - ((point.Y - lineA.Y) * dir.X)) / len;
                }

                bool TryResolveInnerOffsetSignFromExistingCoverage(Point2d outerA, Point2d outerB, out int sign)
                {
                    sign = 0;
                    var dir = outerB - outerA;
                    var len = dir.Length;
                    if (len <= 1e-6 || correctionInnerSources.Count == 0)
                    {
                        return false;
                    }

                    var u = dir / len;
                    var outerMinX = Math.Min(outerA.X, outerB.X);
                    var outerMaxX = Math.Max(outerA.X, outerB.X);
                    var found = false;
                    var bestOverlap = double.MinValue;
                    var bestOffsetDelta = double.MaxValue;
                    var bestSign = 0;
                    for (var i = 0; i < correctionInnerSources.Count; i++)
                    {
                        var inner = correctionInnerSources[i];
                        var innerDir = inner.B - inner.A;
                        var innerLen = innerDir.Length;
                        if (innerLen <= 1e-6)
                        {
                            continue;
                        }

                        if (Math.Abs(u.DotProduct(innerDir / innerLen)) < 0.985)
                        {
                            continue;
                        }

                        var overlapMin = Math.Max(outerMinX, Math.Min(inner.A.X, inner.B.X));
                        var overlapMax = Math.Min(outerMaxX, Math.Max(inner.A.X, inner.B.X));
                        var overlap = overlapMax - overlapMin;
                        if (overlap < 12.0)
                        {
                            continue;
                        }

                        var signedOffset = SignedDistanceToInfiniteLine(Midpoint(inner.A, inner.B), outerA, outerB);
                        var offsetDelta = Math.Abs(Math.Abs(signedOffset) - CorrectionLinePostInsetMeters);
                        if (offsetDelta > 2.4)
                        {
                            continue;
                        }

                        var candidateSign = signedOffset >= 0.0 ? 1 : -1;
                        if (!found ||
                            overlap > bestOverlap + 1e-6 ||
                            (Math.Abs(overlap - bestOverlap) <= 1e-6 && offsetDelta < bestOffsetDelta))
                        {
                            found = true;
                            bestOverlap = overlap;
                            bestOffsetDelta = offsetDelta;
                            bestSign = candidateSign;
                        }
                    }

                    if (!found || bestSign == 0)
                    {
                        return false;
                    }

                    sign = bestSign;
                    return true;
                }

                void AppendSectionVerticalSplitTs(
                    IReadOnlyList<(Point2d A, Point2d B, double MinY, double MaxY)> splitSegments,
                    Point2d a,
                    Point2d b,
                    Vector2d ab,
                    double abLen2,
                    List<double> splitTs)
                {
                    const double sectionSplitTargetTouchTol = 1.2;
                    const double sectionSplitTargetSpanTol = 0.6;
                    const double sectionSplitTargetCrossMargin = 2.5;
                    const double sectionSplitEndpointParamTol = 0.02;
                    if (splitSegments == null || splitSegments.Count == 0 || splitTs == null || abLen2 <= 1e-9)
                    {
                        return;
                    }

                    for (var i = 0; i < splitSegments.Count; i++)
                    {
                        var target = splitSegments[i];
                        if (!TryIntersectInfiniteLines(a, b, target.A, target.B, out var ip))
                        {
                            continue;
                        }

                        if (DistancePointToSegment(ip, target.A, target.B) > sectionSplitTargetTouchTol)
                        {
                            continue;
                        }

                        if (ip.Y < target.MinY - sectionSplitTargetSpanTol || ip.Y > target.MaxY + sectionSplitTargetSpanTol)
                        {
                            continue;
                        }

                        if (ip.Y <= target.MinY + sectionSplitTargetCrossMargin ||
                            ip.Y >= target.MaxY - sectionSplitTargetCrossMargin)
                        {
                            continue;
                        }

                        var t = (ip - a).DotProduct(ab) / abLen2;
                        if (t <= sectionSplitEndpointParamTol || t >= 1.0 - sectionSplitEndpointParamTol)
                        {
                            continue;
                        }

                        splitTs.Add(t);
                    }
                }

                bool IsEndpointAlreadyConnected(Point2d endpoint, int preferredDirectionSign)
                {
                    for (var i = 0; i < verticalTargets.Count; i++)
                    {
                        var target = verticalTargets[i];
                        if (DistancePointToSegment(endpoint, target.A, target.B) > endpointTouchTol)
                        {
                            continue;
                        }

                        if (preferredDirectionSign > 0)
                        {
                            if (target.MaxY >= endpoint.Y + directionAxisTol)
                            {
                                return true;
                            }

                            continue;
                        }

                        if (preferredDirectionSign < 0)
                        {
                            if (target.MinY <= endpoint.Y - directionAxisTol)
                            {
                                return true;
                            }

                            continue;
                        }

                        return true;
                    }

                    return false;
                }

                bool IsEndpointAnchoredToVerticalTargetEndpoint(Point2d endpoint)
                {
                    for (var i = 0; i < verticalEndpointAnchors.Count; i++)
                    {
                        var target = verticalEndpointAnchors[i];
                        if (endpoint.GetDistanceTo(target.A) <= endpointTouchTol ||
                            endpoint.GetDistanceTo(target.B) <= endpointTouchTol)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool IsEndpointAnchoredToCorrectionOuterEndpoint(Point2d endpoint)
                {
                    for (var i = 0; i < correctionOuterSources.Count; i++)
                    {
                        var outer = correctionOuterSources[i];
                        if (endpoint.GetDistanceTo(outer.A) <= endpointTouchTol ||
                            endpoint.GetDistanceTo(outer.B) <= endpointTouchTol)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool TryResolveProtectedHorizontalCompanionEndpoint(
                    ObjectId sourceId,
                    Point2d endpoint,
                    Point2d other,
                    out Point2d protectedPoint)
                {
                    protectedPoint = endpoint;
                    var dir = other - endpoint;
                    var len = dir.Length;
                    if (len <= 1e-6)
                    {
                        return false;
                    }

                    var u = dir / len;
                    var found = false;
                    var bestProjectionDistance = double.MaxValue;
                    var bestEndpointDistance = double.MaxValue;
                    for (var hi = 0; hi < horizontalEndpointAnchors.Count; hi++)
                    {
                        var anchor = horizontalEndpointAnchors[hi];
                        if (anchor.Id == sourceId)
                        {
                            continue;
                        }

                        var anchorDir = anchor.B - anchor.A;
                        var anchorLen = anchorDir.Length;
                        var anchorLen2 = anchorDir.DotProduct(anchorDir);
                        if (anchorLen <= 1e-6 || anchorLen2 <= 1e-9)
                        {
                            continue;
                        }

                        if (Math.Abs(u.DotProduct(anchorDir / anchorLen)) < protectedCompanionDirectionDotMin)
                        {
                            continue;
                        }

                        var signedOffset = SignedDistanceToInfiniteLine(endpoint, anchor.A, anchor.B);
                        var offsetSign = Math.Sign(signedOffset);
                        if (offsetSign == 0 ||
                            Math.Abs(Math.Abs(signedOffset) - CorrectionLinePostInsetMeters) > protectedCompanionOffsetTol)
                        {
                            continue;
                        }

                        var reverseOffset = Math.Abs(DistancePointToInfiniteLine(Midpoint(anchor.A, anchor.B), endpoint, other));
                        if (Math.Abs(reverseOffset - CorrectionLinePostInsetMeters) > protectedCompanionReverseOffsetTol)
                        {
                            continue;
                        }

                        var projectionT = ((endpoint - anchor.A).DotProduct(anchorDir)) / anchorLen2;
                        var projected = anchor.A + (anchorDir * projectionT);
                        var normal = new Vector2d(anchorDir.Y / anchorLen, -anchorDir.X / anchorLen);

                        var anchorEndpoints = new[] { anchor.A, anchor.B };
                        for (var ei = 0; ei < anchorEndpoints.Length; ei++)
                        {
                            var anchorEndpoint = anchorEndpoints[ei];
                            var projectionDistance = projected.GetDistanceTo(anchorEndpoint);
                            if (projectionDistance > protectedCompanionProjectionTol)
                            {
                                continue;
                            }

                            var exactCompanion = anchorEndpoint + (normal * (offsetSign * CorrectionLinePostInsetMeters));
                            var endpointDistance = endpoint.GetDistanceTo(exactCompanion);
                            if (endpointDistance > protectedCompanionRestoreMax)
                            {
                                continue;
                            }

                            if (!found ||
                                projectionDistance < bestProjectionDistance - 1e-6 ||
                                (Math.Abs(projectionDistance - bestProjectionDistance) <= 1e-6 &&
                                 endpointDistance < bestEndpointDistance - 1e-6))
                            {
                                found = true;
                                bestProjectionDistance = projectionDistance;
                                bestEndpointDistance = endpointDistance;
                                protectedPoint = exactCompanion;
                            }
                        }
                    }

                    return found;
                }

                bool TryResolveEndpointTarget(
                    Point2d endpoint,
                    Point2d other,
                    int preferredDirectionSign,
                    out Point2d targetPoint,
                    out int targetIndex,
                    out bool keepHorizontalEndpoint)
                {
                    targetPoint = endpoint;
                    targetIndex = -1;
                    keepHorizontalEndpoint = false;
                    var outward = endpoint - other;
                    var outwardLength = outward.Length;
                    if (outwardLength <= 1e-6)
                    {
                        return false;
                    }

                    var outwardUnit = outward / outwardLength;
                    var found = false;
                    var bestHorizontalMoveDistance = double.MaxValue;
                    var bestExtensionGap = double.MaxValue;
                    var bestPoint = endpoint;
                    var bestIndex = -1;
                    var bestKeepHorizontal = false;
                    for (var i = 0; i < verticalTargets.Count; i++)
                    {
                        var target = verticalTargets[i];
                        if (target.A.GetDistanceTo(target.B) > maxVerticalTargetLength)
                        {
                            continue;
                        }

                        if (preferredDirectionSign > 0 && target.MaxY < endpoint.Y + directionAxisTol)
                        {
                            continue;
                        }

                        if (preferredDirectionSign < 0 && target.MinY > endpoint.Y - directionAxisTol)
                        {
                            continue;
                        }

                        if (!TryIntersectInfiniteLines(endpoint, other, target.A, target.B, out var intersection))
                        {
                            continue;
                        }

                        var inlineWithVertical = DistancePointToInfiniteLine(endpoint, target.A, target.B) <= inlineVerticalTol;
                        var horizontalMoveDistance = 0.0;
                        if (!inlineWithVertical)
                        {
                            var delta = intersection - endpoint;
                            horizontalMoveDistance = delta.Length;
                            if (horizontalMoveDistance < minMove || horizontalMoveDistance > maxMove)
                            {
                                continue;
                            }

                            var alongOutward = delta.DotProduct(outwardUnit);
                            if (alongOutward <= directionAxisTol)
                            {
                                continue;
                            }
                        }

                        var connectionPoint = inlineWithVertical ? endpoint : intersection;
                        var extensionGap = DistancePointToSegment(connectionPoint, target.A, target.B);
                        if (extensionGap > maxVerticalTargetGap)
                        {
                            continue;
                        }

                        if (!found ||
                            horizontalMoveDistance < bestHorizontalMoveDistance - 1e-6 ||
                            (Math.Abs(horizontalMoveDistance - bestHorizontalMoveDistance) <= 1e-6 &&
                             extensionGap < bestExtensionGap - 1e-6))
                        {
                            found = true;
                            bestHorizontalMoveDistance = horizontalMoveDistance;
                            bestExtensionGap = extensionGap;
                            bestPoint = connectionPoint;
                            bestIndex = i;
                            bestKeepHorizontal = inlineWithVertical;
                        }
                    }

                    if (!found)
                    {
                        return false;
                    }

                    targetPoint = bestPoint;
                    targetIndex = bestIndex;
                    keepHorizontalEndpoint = bestKeepHorizontal;
                    return true;
                }

                bool TryFindVerticalTargetTouchingEndpoint(Point2d endpoint, out int targetIndex)
                {
                    targetIndex = -1;
                    var found = false;
                    var foundExactTouch = false;
                    var bestDistance = double.MaxValue;
                    var touchTol = Math.Max(endpointTouchTol * 2.0, 0.80);
                    var axisTol = Math.Max(endpointTouchTol * 2.0, 0.80);
                    const double endpointExtensionParamTol = 0.05;
                    for (var i = 0; i < verticalProtectedRestoreTargets.Count; i++)
                    {
                        var target = verticalProtectedRestoreTargets[i];
                        var segmentDistance = Math.Min(
                            Math.Min(endpoint.GetDistanceTo(target.A), endpoint.GetDistanceTo(target.B)),
                            DistancePointToSegment(endpoint, target.A, target.B));
                        var isExactTouch = segmentDistance <= touchTol;
                        var candidateDistance = segmentDistance;
                        if (!isExactTouch)
                        {
                            var lineDistance = Math.Abs(DistancePointToInfiniteLine(endpoint, target.A, target.B));
                            if (lineDistance > axisTol)
                            {
                                continue;
                            }

                            var endpointDistance = Math.Min(endpoint.GetDistanceTo(target.A), endpoint.GetDistanceTo(target.B));
                            if (endpointDistance > protectedTouchingVerticalRestoreMax)
                            {
                                continue;
                            }

                            var targetDir = target.B - target.A;
                            var targetLen2 = targetDir.DotProduct(targetDir);
                            if (targetLen2 <= 1e-9)
                            {
                                continue;
                            }

                            var projectionT = ((endpoint - target.A).DotProduct(targetDir)) / targetLen2;
                            if (projectionT > endpointExtensionParamTol && projectionT < 1.0 - endpointExtensionParamTol)
                            {
                                continue;
                            }

                            candidateDistance = endpointDistance;
                        }

                        if (!found ||
                            (foundExactTouch != isExactTouch && isExactTouch) ||
                            (foundExactTouch == isExactTouch && candidateDistance < bestDistance - 1e-6))
                        {
                            found = true;
                            foundExactTouch = isExactTouch;
                            bestDistance = candidateDistance;
                            targetIndex = i;
                        }
                    }

                    return found;
                }

                bool TryForceTouchingVerticalTargetEndpointToPoint(
                    Point2d touchPoint,
                    Point2d connectionPoint)
                {
                    if (!(TryFindVerticalTargetTouchingEndpoint(touchPoint, out var targetIndex) ||
                          TryFindVerticalTargetTouchingEndpoint(connectionPoint, out targetIndex)))
                    {
                        return false;
                    }

                    var target = verticalProtectedRestoreTargets[targetIndex];
                    Entity? targetWritable = null;
                    try
                    {
                        targetWritable = tr.GetObject(target.Id, OpenMode.ForWrite, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        return false;
                    }

                    if (targetWritable == null || targetWritable.IsErased)
                    {
                        return false;
                    }

                    if (IsCorrectionSurveyedLayer(targetWritable.Layer ?? string.Empty))
                    {
                        return false;
                    }

                    if (!TryReadOpenLinearSegment(targetWritable, out var currentA, out var currentB))
                    {
                        return false;
                    }

                    var startDistance = Math.Min(currentA.GetDistanceTo(touchPoint), currentA.GetDistanceTo(connectionPoint));
                    var endDistance = Math.Min(currentB.GetDistanceTo(touchPoint), currentB.GetDistanceTo(connectionPoint));
                    var moveStart = startDistance <= endDistance;
                    var moveEndpoint = moveStart ? currentA : currentB;
                    var endpointMoveDistance = moveEndpoint.GetDistanceTo(connectionPoint);
                    if (endpointMoveDistance > protectedTouchingVerticalRestoreMax)
                    {
                        return false;
                    }

                    if (endpointMoveDistance <= exactEndpointMoveTol)
                    {
                        return false;
                    }

                    var fixedEndpoint = moveStart ? currentB : currentA;
                    if (fixedEndpoint.GetDistanceTo(connectionPoint) < minRemainingLength)
                    {
                        return false;
                    }

                    if (!TryMoveEndpoint(targetWritable, moveStart, connectionPoint, exactEndpointMoveTol))
                    {
                        return false;
                    }

                    if (!TryReadOpenLinearSegment(targetWritable, out var newA, out var newB))
                    {
                        return true;
                    }

                    verticalProtectedRestoreTargets[targetIndex] = (
                        target.Id,
                        newA,
                        newB,
                        Math.Min(newA.Y, newB.Y),
                        Math.Max(newA.Y, newB.Y),
                        0.5 * (newA.X + newB.X));
                    for (var i = 0; i < verticalTargets.Count; i++)
                    {
                        if (verticalTargets[i].Id != target.Id)
                        {
                            continue;
                        }

                        verticalTargets[i] = (
                            target.Id,
                            newA,
                            newB,
                            Math.Min(newA.Y, newB.Y),
                            Math.Max(newA.Y, newB.Y),
                            0.5 * (newA.X + newB.X));
                        break;
                    }
                    return true;
                }

                bool TryForceSpecificVerticalEndpointToPoint(
                    ObjectId targetId,
                    Point2d touchPoint,
                    Point2d connectionPoint)
                {
                    if (targetId.IsNull)
                    {
                        return false;
                    }

                    Entity? targetWritable = null;
                    try
                    {
                        targetWritable = tr.GetObject(targetId, OpenMode.ForWrite, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        return false;
                    }

                    if (targetWritable == null || targetWritable.IsErased)
                    {
                        return false;
                    }

                    if (!TryReadOpenLinearSegment(targetWritable, out var currentA, out var currentB))
                    {
                        return false;
                    }

                    var startDistance = Math.Min(currentA.GetDistanceTo(touchPoint), currentA.GetDistanceTo(connectionPoint));
                    var endDistance = Math.Min(currentB.GetDistanceTo(touchPoint), currentB.GetDistanceTo(connectionPoint));
                    var moveStart = startDistance <= endDistance;
                    var moveEndpoint = moveStart ? currentA : currentB;
                    var endpointMoveDistance = moveEndpoint.GetDistanceTo(connectionPoint);
                    if (endpointMoveDistance > protectedTouchingVerticalRestoreMax ||
                        endpointMoveDistance <= exactEndpointMoveTol)
                    {
                        return false;
                    }

                    var fixedEndpoint = moveStart ? currentB : currentA;
                    if (fixedEndpoint.GetDistanceTo(connectionPoint) < minRemainingLength)
                    {
                        return false;
                    }

                    if (!TryMoveEndpoint(targetWritable, moveStart, connectionPoint, exactEndpointMoveTol))
                    {
                        return false;
                    }

                    if (!TryReadOpenLinearSegment(targetWritable, out var newA, out var newB))
                    {
                        return true;
                    }

                    for (var i = 0; i < verticalTargets.Count; i++)
                    {
                        if (verticalTargets[i].Id != targetId)
                        {
                            continue;
                        }

                        verticalTargets[i] = (
                            targetId,
                            newA,
                            newB,
                            Math.Min(newA.Y, newB.Y),
                            Math.Max(newA.Y, newB.Y),
                            0.5 * (newA.X + newB.X));
                        break;
                    }

                    for (var i = 0; i < verticalProtectedRestoreTargets.Count; i++)
                    {
                        if (verticalProtectedRestoreTargets[i].Id != targetId)
                        {
                            continue;
                        }

                        verticalProtectedRestoreTargets[i] = (
                            targetId,
                            newA,
                            newB,
                            Math.Min(newA.Y, newB.Y),
                            Math.Max(newA.Y, newB.Y),
                            0.5 * (newA.X + newB.X));
                        break;
                    }

                    for (var i = 0; i < verticalTwentyTargets.Count; i++)
                    {
                        if (verticalTwentyTargets[i].Id != targetId)
                        {
                            continue;
                        }

                        verticalTwentyTargets[i] = (
                            targetId,
                            newA,
                            newB,
                            Math.Min(newA.Y, newB.Y),
                            Math.Max(newA.Y, newB.Y),
                            0.5 * (newA.X + newB.X));
                        break;
                    }

                    for (var i = 0; i < verticalThirtyTargets.Count; i++)
                    {
                        if (verticalThirtyTargets[i].Id != targetId)
                        {
                            continue;
                        }

                        verticalThirtyTargets[i] = (
                            targetId,
                            newA,
                            newB,
                            Math.Min(newA.Y, newB.Y),
                            Math.Max(newA.Y, newB.Y),
                            0.5 * (newA.X + newB.X));
                        break;
                    }

                    return true;
                }

                bool TryResolveMixedTwentyThirtyOuterEndpointTarget(
                    Point2d endpoint,
                    Point2d other,
                    out Point2d targetPoint,
                    out ObjectId touchingThirtyId)
                {
                    targetPoint = endpoint;
                    touchingThirtyId = ObjectId.Null;

                    const double endpointTouchTol = 0.80;
                    const double axisGapTarget = CorrectionLinePostInsetMeters * 2.0;
                    const double axisGapTol = 1.40;
                    const double outerToTwentyGapTarget = CorrectionLinePostInsetMeters;
                    const double outerToTwentyGapTol = 1.40;
                    const double targetMoveMax = 2.5;

                    var found = false;
                    var bestScore = double.MaxValue;
                    var bestMove = double.MaxValue;
                    for (var ti = 0; ti < verticalThirtyTargets.Count; ti++)
                    {
                        var thirty = verticalThirtyTargets[ti];
                        var thirtyEndpointA = endpoint.GetDistanceTo(thirty.A);
                        var thirtyEndpointB = endpoint.GetDistanceTo(thirty.B);
                        if (thirtyEndpointA > endpointTouchTol && thirtyEndpointB > endpointTouchTol)
                        {
                            continue;
                        }

                        for (var vi = 0; vi < verticalTwentyTargets.Count; vi++)
                        {
                            var twenty = verticalTwentyTargets[vi];
                            var touchingThirtyPoint = thirtyEndpointA <= thirtyEndpointB
                                ? thirty.A
                                : thirty.B;
                            var twentyEndpoints = new[] { twenty.A, twenty.B };
                            for (var ei = 0; ei < twentyEndpoints.Length; ei++)
                            {
                                var twentyEndpoint = twentyEndpoints[ei];
                                var axisGap = Math.Abs(touchingThirtyPoint.X - twentyEndpoint.X);
                                if (Math.Abs(axisGap - axisGapTarget) > axisGapTol)
                                {
                                    continue;
                                }

                                var currentGap = endpoint.Y - twentyEndpoint.Y;
                                var gapSign = Math.Sign(currentGap);
                                if (gapSign == 0)
                                {
                                    continue;
                                }

                                if (Math.Abs(Math.Abs(currentGap) - outerToTwentyGapTarget) > outerToTwentyGapTol)
                                {
                                    continue;
                                }

                                var candidateTarget = new Point2d(
                                    touchingThirtyPoint.X,
                                    twentyEndpoint.Y + (gapSign * CorrectionLinePostInsetMeters));
                                if (candidateTarget.Y < thirty.MinY - endpointTouchTol ||
                                    candidateTarget.Y > thirty.MaxY + endpointTouchTol)
                                {
                                    continue;
                                }

                                if (other.GetDistanceTo(candidateTarget) < minRemainingLength)
                                {
                                    continue;
                                }

                                var move = endpoint.GetDistanceTo(candidateTarget);
                                if (move <= exactEndpointMoveTol || move > targetMoveMax)
                                {
                                    continue;
                                }

                                var score =
                                    Math.Abs(axisGap - axisGapTarget) +
                                    Math.Abs(Math.Abs(currentGap) - outerToTwentyGapTarget) +
                                    (move * 3.0);
                                if (!found ||
                                    score < bestScore - 1e-6 ||
                                    (Math.Abs(score - bestScore) <= 1e-6 && move < bestMove - 1e-6))
                                {
                                    found = true;
                                    bestScore = score;
                                    bestMove = move;
                                    targetPoint = candidateTarget;
                                    touchingThirtyId = thirty.Id;
                                }
                            }
                        }
                    }

                    return found;
                }

                bool TryAdjustVerticalTargetEndpointToPoint(
                    int targetIndex,
                    Point2d connectionPoint,
                    int preferredDirectionSign,
                    ObjectId sourceId,
                    double moveTol,
                    bool allowHorizontalAnchorBypass = false)
                {
                    if (targetIndex < 0 || targetIndex >= verticalTargets.Count)
                    {
                        return false;
                    }

                    var target = verticalTargets[targetIndex];
                    if (Math.Min(
                            connectionPoint.GetDistanceTo(target.A),
                            connectionPoint.GetDistanceTo(target.B)) <= moveTol)
                    {
                        return false;
                    }

                    Entity? targetWritable = null;
                    try
                    {
                        targetWritable = tr.GetObject(target.Id, OpenMode.ForWrite, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        return false;
                    }

                    if (targetWritable == null || targetWritable.IsErased)
                    {
                        return false;
                    }

                    if (!TryReadOpenLinearSegment(targetWritable, out var currentA, out var currentB))
                    {
                        return false;
                    }

                    if (Math.Min(
                            connectionPoint.GetDistanceTo(currentA),
                            connectionPoint.GetDistanceTo(currentB)) <= moveTol)
                    {
                        return false;
                    }

                    if (currentA.GetDistanceTo(currentB) > maxVerticalTargetLength)
                    {
                        return false;
                    }

                    var moveStart = false;
                    if (preferredDirectionSign > 0)
                    {
                        // North-side correction line: target segment is the north-going vertical;
                        // extend its seam-facing (south/lower) endpoint to meet C-0.
                        moveStart = currentA.Y <= currentB.Y;
                    }
                    else if (preferredDirectionSign < 0)
                    {
                        // South-side correction line: target segment is the south-going vertical;
                        // extend its seam-facing (north/upper) endpoint to meet C-0.
                        moveStart = currentA.Y >= currentB.Y;
                    }
                    else
                    {
                        var startDistance = currentA.GetDistanceTo(connectionPoint);
                        var endDistance = currentB.GetDistanceTo(connectionPoint);
                        moveStart = startDistance <= endDistance;
                    }

                    var moveEndpoint = moveStart ? currentA : currentB;
                    if (moveEndpoint.GetDistanceTo(connectionPoint) > maxVerticalEndpointMove)
                    {
                        return false;
                    }

                    if (!allowHorizontalAnchorBypass)
                    {
                        for (var hi = 0; hi < horizontalEndpointAnchors.Count; hi++)
                        {
                            var anchor = horizontalEndpointAnchors[hi];
                            if (anchor.Id == sourceId)
                            {
                                continue;
                            }

                            if (moveEndpoint.GetDistanceTo(anchor.A) <= endpointTouchTol ||
                                moveEndpoint.GetDistanceTo(anchor.B) <= endpointTouchTol)
                            {
                                return false;
                            }
                        }
                    }

                    var snapKey = (target.Id, moveStart);
                    if (!verticalTargetBestSnapByEndpoint.TryGetValue(snapKey, out var snapState))
                    {
                        snapState = (moveEndpoint, double.MaxValue);
                    }

                    var candidateDistanceFromOriginal = snapState.OriginalEndpoint.GetDistanceTo(connectionPoint);
                    var hasExistingCandidate = snapState.BestDistanceFromOriginal < double.MaxValue * 0.5;
                    if (!CorrectionZeroTargetPreference.IsBetterEndpointAdjustmentCandidate(
                            hasExistingCandidate,
                            candidateDistanceFromOriginal,
                            snapState.BestDistanceFromOriginal))
                    {
                        return false;
                    }

                    var candidateA = moveStart ? connectionPoint : currentA;
                    var candidateB = moveStart ? currentB : connectionPoint;
                    if (candidateA.GetDistanceTo(candidateB) > maxVerticalTargetLength)
                    {
                        return false;
                    }

                    var targetLayer = targetWritable.Layer ?? string.Empty;
                    var allowSecInsetDirectionBypass =
                        IsCorrectionSurveyedLayer(targetLayer) &&
                        moveEndpoint.GetDistanceTo(connectionPoint) <= CorrectionLinePostInsetMeters + 1.0;

                    if (!allowSecInsetDirectionBypass &&
                        preferredDirectionSign > 0 &&
                        connectionPoint.Y < moveEndpoint.Y - directionAxisTol)
                    {
                        return false;
                    }

                    if (!allowSecInsetDirectionBypass &&
                        preferredDirectionSign < 0 &&
                        connectionPoint.Y > moveEndpoint.Y + directionAxisTol)
                    {
                        return false;
                    }

                    var fixedEndpoint = moveStart ? currentB : currentA;
                    if (fixedEndpoint.GetDistanceTo(connectionPoint) < minRemainingLength)
                    {
                        return false;
                    }

                    if (!TryMoveEndpoint(targetWritable, moveStart, connectionPoint, moveTol))
                    {
                        return false;
                    }

                    WriteTargetLayerTraceMove(
                        logger,
                        "corr-post-c0-target-adjust",
                        target.Id,
                        targetLayer,
                        currentA,
                        currentB,
                        candidateA,
                        candidateB,
                        note: string.Format(
                            CultureInfo.InvariantCulture,
                            "direction={0} targetIndex={1} keepHorizontal=false",
                            preferredDirectionSign,
                            targetIndex));

                    if (!TryReadOpenLinearSegment(targetWritable, out var newA, out var newB))
                    {
                        verticalTargetBestSnapByEndpoint[snapKey] = (snapState.OriginalEndpoint, candidateDistanceFromOriginal);
                        return true;
                    }

                    verticalTargetBestSnapByEndpoint[snapKey] = (snapState.OriginalEndpoint, candidateDistanceFromOriginal);
                    verticalTargets[targetIndex] = (
                        target.Id,
                        newA,
                        newB,
                        Math.Min(newA.Y, newB.Y),
                        Math.Max(newA.Y, newB.Y),
                        0.5 * (newA.X + newB.X));
                    return true;
                }

                bool IsEndpointSharedWithOtherInnerSegments(ObjectId sourceId, Point2d endpoint)
                {
                    for (var i = 0; i < correctionInnerSources.Count; i++)
                    {
                        var other = correctionInnerSources[i];
                        if (other.Id == sourceId)
                        {
                            continue;
                        }

                        if (endpoint.GetDistanceTo(other.A) <= endpointTouchTol ||
                            endpoint.GetDistanceTo(other.B) <= endpointTouchTol)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool ExtensionWouldRunIntoOtherInnerSpan(
                    ObjectId sourceId,
                    Point2d endpoint,
                    Point2d other,
                    Point2d targetPoint)
                {
                    var move = targetPoint - endpoint;
                    var moveLength = move.Length;
                    if (moveLength <= minMove)
                    {
                        return false;
                    }

                    var moveUnit = move / moveLength;
                    var candidateA = other;
                    var candidateB = targetPoint;
                    const double blockerOverlapMin = 8.0;
                    const double blockerEndpointTol = 0.60;
                    const double blockerDirectionDotMin = 0.985;
                    const double blockerLineTol = 2.50;

                    for (var i = 0; i < correctionInnerSources.Count; i++)
                    {
                        var otherInner = correctionInnerSources[i];
                        if (otherInner.Id == sourceId)
                        {
                            continue;
                        }

                        var otherDir = otherInner.B - otherInner.A;
                        var otherLength = otherDir.Length;
                        if (otherLength <= 1e-6)
                        {
                            continue;
                        }

                        if (Math.Abs((otherDir / otherLength).DotProduct(moveUnit)) < blockerDirectionDotMin)
                        {
                            continue;
                        }

                        var minEndpointDistance = Math.Min(
                            Math.Abs(DistancePointToInfiniteLine(otherInner.A, candidateA, candidateB)),
                            Math.Abs(DistancePointToInfiniteLine(otherInner.B, candidateA, candidateB)));
                        var midpointDistance = Math.Abs(DistancePointToInfiniteLine(Midpoint(otherInner.A, otherInner.B), candidateA, candidateB));
                        if (minEndpointDistance > blockerLineTol && midpointDistance > blockerLineTol)
                        {
                            continue;
                        }

                        if (DistancePointToSegment(targetPoint, otherInner.A, otherInner.B) <= blockerEndpointTol)
                        {
                            return true;
                        }

                        var t0 = (otherInner.A - endpoint).DotProduct(moveUnit);
                        var t1 = (otherInner.B - endpoint).DotProduct(moveUnit);
                        var minT = Math.Min(t0, t1);
                        var maxT = Math.Max(t0, t1);
                        if (maxT <= blockerEndpointTol)
                        {
                            continue;
                        }

                        var clippedMin = Math.Max(0.0, minT);
                        var clippedMax = Math.Min(moveLength, maxT);
                        if (clippedMax - clippedMin >= blockerOverlapMin)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool TryProcessInnerEndpoint(
                    ObjectId sourceId,
                    Entity sourceWritable,
                    bool moveStart,
                    Point2d endpoint,
                    Point2d other,
                    int directionSign,
                    out Point2d updatedEndpoint)
                {
                    updatedEndpoint = endpoint;
                    scannedEndpoints++;

                    if (IsEndpointSharedWithOtherInnerSegments(sourceId, endpoint))
                    {
                        sharedEndpointSkipped++;
                        return false;
                    }

                    if (IsEndpointAnchoredToVerticalTargetEndpoint(endpoint) ||
                        IsEndpointAnchoredToCorrectionOuterEndpoint(endpoint))
                    {
                        alreadyConnected++;
                        return false;
                    }

                    if (IsEndpointAlreadyConnected(endpoint, directionSign))
                    {
                        alreadyConnected++;
                        return false;
                    }

                    if (!TryResolveEndpointTarget(
                            endpoint,
                            other,
                            directionSign,
                            out var snappedTarget,
                            out var targetIndex,
                            out var keepHorizontalEndpoint))
                    {
                        noTargetFound++;
                        return false;
                    }

                    var changed = false;
                    var connectionPoint = endpoint;
                    if (!keepHorizontalEndpoint)
                    {
                        if (snappedTarget.GetDistanceTo(other) < minRemainingLength)
                        {
                            noTargetFound++;
                            return false;
                        }

                        if (ExtensionWouldRunIntoOtherInnerSpan(sourceId, endpoint, other, snappedTarget))
                        {
                            noTargetFound++;
                            return false;
                        }

                        var oldEndpoint = endpoint;
                        if (TryMoveEndpoint(sourceWritable, moveStart, snappedTarget, endpointMoveTol))
                        {
                            changed = true;
                            movedEndpoints++;
                            movedHorizontalEndpoints++;
                            updatedEndpoint = snappedTarget;
                            connectionPoint = snappedTarget;
                            if (sampleMoves.Count < 24)
                            {
                                sampleMoves.Add(
                                    string.Format(
                                        CultureInfo.InvariantCulture,
                                        "id={0} {1} H ({2:0.###},{3:0.###})->({4:0.###},{5:0.###})",
                                        sourceId.Handle.ToString(),
                                        moveStart ? "start" : "end",
                                        oldEndpoint.X,
                                        oldEndpoint.Y,
                                        snappedTarget.X,
                                        snappedTarget.Y));
                            }
                        }
                        else
                        {
                            noTargetFound++;
                            return false;
                        }
                    }
                    else
                    {
                        connectionPoint = endpoint;
                    }

                    if (TryAdjustVerticalTargetEndpointToPoint(targetIndex, connectionPoint, directionSign, sourceId, endpointMoveTol))
                    {
                        changed = true;
                        movedEndpoints++;
                        movedVerticalEndpoints++;
                        if (sampleMoves.Count < 24)
                        {
                            sampleMoves.Add(
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "id={0} {1} V ->({2:0.###},{3:0.###})",
                                    sourceId.Handle.ToString(),
                                    moveStart ? "start" : "end",
                                    connectionPoint.X,
                                    connectionPoint.Y));
                        }
                    }

                    if (!changed)
                    {
                        noTargetFound++;
                    }

                    return changed;
                }

                bool IsEndpointSharedWithOtherOuterSegments(ObjectId sourceId, Point2d endpoint)
                {
                    for (var i = 0; i < correctionOuterSources.Count; i++)
                    {
                        var other = correctionOuterSources[i];
                        if (other.Id == sourceId)
                        {
                            continue;
                        }

                        if (endpoint.GetDistanceTo(other.A) <= endpointTouchTol ||
                            endpoint.GetDistanceTo(other.B) <= endpointTouchTol)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool TryResolveOuterEndpointTarget(
                    Point2d endpoint,
                    Point2d other,
                    out Point2d targetPoint)
                {
                    targetPoint = endpoint;
                    var outward = endpoint - other;
                    var outwardLength = outward.Length;
                    if (outwardLength <= 1e-6)
                    {
                        return false;
                    }

                    var outwardUnit = outward / outwardLength;
                    const double outerEndpointMoveMax = CorrectionLinePostExpectedUsecWidthMeters * 6.0;
                    const double outerEndpointSpanTol = 1.2;
                    const double outerEndpointInwardTol = 0.25;
                    var found = false;
                    var bestMove = double.MaxValue;
                    var bestEndpointDistance = double.MaxValue;
                    for (var i = 0; i < verticalProtectedRestoreTargets.Count; i++)
                    {
                        var target = verticalProtectedRestoreTargets[i];
                        if (!TryIntersectInfiniteLines(endpoint, other, target.A, target.B, out var intersection))
                        {
                            continue;
                        }

                        if (intersection.Y < target.MinY - outerEndpointSpanTol ||
                            intersection.Y > target.MaxY + outerEndpointSpanTol)
                        {
                            continue;
                        }

                        var delta = intersection - endpoint;
                        var move = delta.Length;
                        if (move > outerEndpointMoveMax)
                        {
                            continue;
                        }

                        if (delta.DotProduct(outwardUnit) < -outerEndpointInwardTol)
                        {
                            continue;
                        }

                        if (other.GetDistanceTo(intersection) < minRemainingLength)
                        {
                            continue;
                        }

                        var endpointDistance = Math.Min(
                            intersection.GetDistanceTo(target.A),
                            intersection.GetDistanceTo(target.B));
                        if (!found ||
                            move < bestMove - 1e-6 ||
                            (Math.Abs(move - bestMove) <= 1e-6 &&
                             endpointDistance < bestEndpointDistance - 1e-6))
                        {
                            found = true;
                            bestMove = move;
                            bestEndpointDistance = endpointDistance;
                            targetPoint = intersection;
                        }
                    }

                    return found;
                }

                if (correctionOuterSources.Count > 0 &&
                    correctionOuterSegments.Count > 0 &&
                    correctionInnerSources.Count > 0 &&
                    (verticalTargets.Count > 0 || sectionVerticalSplitSegments.Count > 0))
                {
                    for (var i = 0; i < correctionOuterSources.Count; i++)
                    {
                        var source = correctionOuterSources[i];
                        Entity? writable = null;
                        try
                        {
                            writable = tr.GetObject(source.Id, OpenMode.ForRead, false) as Entity;
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception)
                        {
                            continue;
                        }

                        if (writable == null || writable.IsErased ||
                            !string.Equals(writable.Layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!TryReadOpenLinearSegment(writable, out var a, out var b) || !IsHorizontalLike(a, b))
                        {
                            continue;
                        }

                        var ab = b - a;
                        var len = ab.Length;
                        var abLen2 = ab.DotProduct(ab);
                        if (len <= 1e-6 || abLen2 <= 1e-9)
                        {
                            continue;
                        }

                        var splitTs = new List<double>();
                        for (var vi = 0; vi < verticalTargets.Count; vi++)
                        {
                            var target = verticalTargets[vi];
                            if (!TryIntersectInfiniteLines(a, b, target.A, target.B, out var ip))
                            {
                                continue;
                            }

                            if (Math.Abs(ip.X - target.AxisX) > 1.2)
                            {
                                continue;
                            }

                            if (ip.Y < target.MinY - 0.6 || ip.Y > target.MaxY + 0.6)
                            {
                                continue;
                            }

                            var t = (ip - a).DotProduct(ab) / abLen2;
                            if (t <= 0.02 || t >= 0.98)
                            {
                                continue;
                            }

                            splitTs.Add(t);
                        }

                        AppendSectionVerticalSplitTs(sectionVerticalSplitSegments, a, b, ab, abLen2, splitTs);
                        if (splitTs.Count == 0)
                        {
                            continue;
                        }

                        splitTs.Sort();
                        var uniqueTs = new List<double>();
                        for (var ti = 0; ti < splitTs.Count; ti++)
                        {
                            var t = splitTs[ti];
                            if (uniqueTs.Count == 0 || Math.Abs(t - uniqueTs[uniqueTs.Count - 1]) > 0.01)
                            {
                                uniqueTs.Add(t);
                            }
                        }

                        if (uniqueTs.Count == 0)
                        {
                            continue;
                        }

                        if (!TryResolveInnerOffsetSignFromExistingCoverage(a, b, out var offsetSign))
                        {
                            continue;
                        }

                        var normal = new Vector2d(-ab.Y / len, ab.X / len);
                        var offsetVector = normal * (offsetSign * CorrectionLinePostInsetMeters);
                        var points = new List<Point2d> { a };
                        for (var ti = 0; ti < uniqueTs.Count; ti++)
                        {
                            points.Add(a + (ab * uniqueTs[ti]));
                        }

                        points.Add(b);
                        if (points.Count < 3)
                        {
                            continue;
                        }

                        var liveInnerSegments = correctionInnerSources
                            .Select(inner => new CorrectionSegment(inner.Id, LayerUsecCorrectionZero, inner.A, inner.B))
                            .ToList();
                        for (var pi = 0; pi < points.Count - 1; pi++)
                        {
                            if (pi != 0 && pi != points.Count - 2)
                            {
                                continue;
                            }

                            var candidateA = points[pi] + offsetVector;
                            var candidateB = points[pi + 1] + offsetVector;
                            if (candidateA.GetDistanceTo(candidateB) <= 0.20 ||
                                HasMatchingCorrectionSegment(liveInnerSegments, candidateA, candidateB))
                            {
                                continue;
                            }

                            var line = new Line(
                                new Point3d(candidateA.X, candidateA.Y, 0.0),
                                new Point3d(candidateB.X, candidateB.Y, 0.0))
                            {
                                Layer = LayerUsecCorrectionZero,
                                ColorIndex = 256
                            };
                            var newId = ms.AppendEntity(line);
                            tr.AddNewlyCreatedDBObject(line, true);
                            correctionInnerSources.Add((newId, candidateA, candidateB));
                            liveInnerSegments.Add(new CorrectionSegment(newId, LayerUsecCorrectionZero, candidateA, candidateB));
                        }
                    }
                }

                if (verticalTargets.Count > 0)
                {
                    for (var i = 0; i < correctionInnerSources.Count; i++)
                    {
                        var source = correctionInnerSources[i];
                        var id = source.Id;
                        Entity? writable = null;
                        try
                        {
                            writable = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception)
                        {
                            continue;
                        }

                        if (writable == null || writable.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenLinearSegment(writable, out var a, out var b))
                        {
                            continue;
                        }

                        if (!IsHorizontalLike(a, b))
                        {
                            continue;
                        }

                        if (!TryResolveInnerDirectionSign(a, b, correctionOuterSegments, out var directionSign))
                        {
                            noDirectionResolved += 2;
                            directionSign = 0;
                        }

                        var lineMoved = false;
                        if (TryProcessInnerEndpoint(id, writable, moveStart: true, a, b, directionSign, out var updatedStart))
                        {
                            lineMoved = true;
                            a = updatedStart;
                        }

                        if (TryProcessInnerEndpoint(id, writable, moveStart: false, b, a, directionSign, out var updatedEnd))
                        {
                            lineMoved = true;
                            b = updatedEnd;
                        }

                        if (lineMoved)
                        {
                            movedLines++;
                        }
                    }

                    for (var i = 0; i < correctionInnerSources.Count; i++)
                    {
                        var source = correctionInnerSources[i];
                        Entity? writable = null;
                        try
                        {
                            writable = tr.GetObject(source.Id, OpenMode.ForWrite, false) as Entity;
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception)
                        {
                            continue;
                        }

                        if (writable == null || writable.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenLinearSegment(writable, out var a, out var b) || !IsHorizontalLike(a, b))
                        {
                            continue;
                        }

                        if (!TryResolveInnerDirectionSign(a, b, correctionOuterSegments, out var directionSign))
                        {
                            directionSign = 0;
                        }

                        var lineMoved = false;

                        bool TryRestoreProtectedCompanionEndpoint(bool moveStart, Point2d endpoint, Point2d other, out Point2d updatedEndpoint)
                        {
                            updatedEndpoint = endpoint;
                            if (!TryResolveProtectedHorizontalCompanionEndpoint(source.Id, endpoint, other, out var protectedPoint))
                            {
                                return false;
                            }

                            var moveDistance = endpoint.GetDistanceTo(protectedPoint);
                            if (moveDistance > protectedCompanionRestoreMax)
                            {
                                return false;
                            }

                            var sourceMoved = false;
                            if (moveDistance > exactEndpointMoveTol)
                            {
                                if (!TryMoveEndpoint(writable, moveStart, protectedPoint, exactEndpointMoveTol))
                                {
                                    return false;
                                }

                                sourceMoved = true;
                                updatedEndpoint = protectedPoint;
                            }

                            var adjustedVertical = false;
                            adjustedVertical = TryForceTouchingVerticalTargetEndpointToPoint(endpoint, protectedPoint);

                            if (!sourceMoved && !adjustedVertical)
                            {
                                return false;
                            }

                            if (sourceMoved)
                            {
                                movedEndpoints++;
                                movedHorizontalEndpoints++;
                            }

                            if (adjustedVertical)
                            {
                                movedEndpoints++;
                                movedVerticalEndpoints++;
                            }

                            return true;
                        }

                        if (TryRestoreProtectedCompanionEndpoint(moveStart: true, a, b, out var updatedStart))
                        {
                            a = updatedStart;
                            lineMoved = true;
                        }

                        if (TryRestoreProtectedCompanionEndpoint(moveStart: false, b, a, out var updatedEnd))
                        {
                            b = updatedEnd;
                            lineMoved = true;
                        }

                        if (lineMoved)
                        {
                            correctionInnerSources[i] = (source.Id, a, b);
                            movedLines++;
                        }
                    }

                    for (var i = 0; i < correctionInnerSources.Count; i++)
                    {
                        var source = correctionInnerSources[i];
                        Entity? readable = null;
                        try
                        {
                            readable = tr.GetObject(source.Id, OpenMode.ForRead, false) as Entity;
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception)
                        {
                            continue;
                        }

                        if (readable == null || readable.IsErased ||
                            !string.Equals(readable.Layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!TryReadOpenLinearSegment(readable, out var a, out var b) || !IsHorizontalLike(a, b))
                        {
                            continue;
                        }

                        if (TryForceTouchingVerticalTargetEndpointToPoint(a, a))
                        {
                            movedEndpoints++;
                            movedVerticalEndpoints++;
                        }

                        if (TryForceTouchingVerticalTargetEndpointToPoint(b, b))
                        {
                            movedEndpoints++;
                            movedVerticalEndpoints++;
                        }
                    }

                }

                if (correctionOuterSources.Count > 0 &&
                    verticalProtectedRestoreTargets.Count > 0)
                {
                    for (var i = 0; i < correctionOuterSources.Count; i++)
                    {
                        var source = correctionOuterSources[i];
                        Entity? writable = null;
                        try
                        {
                            writable = tr.GetObject(source.Id, OpenMode.ForWrite, false) as Entity;
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception)
                        {
                            continue;
                        }

                        if (writable == null || writable.IsErased ||
                            !string.Equals(writable.Layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (!TryReadOpenLinearSegment(writable, out var a, out var b) || !IsHorizontalLike(a, b))
                        {
                            continue;
                        }

                        var lineMoved = false;

                        bool TryProcessOuterEndpoint(bool moveStart, Point2d endpoint, Point2d other, out Point2d updatedEndpoint)
                        {
                            updatedEndpoint = endpoint;
                            var changed = false;
                            if (TryForceTouchingVerticalTargetEndpointToPoint(endpoint, endpoint))
                            {
                                movedEndpoints++;
                                movedVerticalEndpoints++;
                                changed = true;
                            }

                            if (TryResolveMixedTwentyThirtyOuterEndpointTarget(endpoint, other, out var mixedThirtyTarget, out var touchingThirtyId))
                            {
                                if (TryMoveEndpoint(writable, moveStart, mixedThirtyTarget, endpointMoveTol))
                                {
                                    movedEndpoints++;
                                    movedHorizontalEndpoints++;
                                    updatedEndpoint = mixedThirtyTarget;
                                    changed = true;
                                    if (sampleMoves.Count < 24)
                                    {
                                        sampleMoves.Add(
                                            string.Format(
                                                CultureInfo.InvariantCulture,
                                                "id={0} {1} C-outer mixed30 ({2:0.###},{3:0.###})->({4:0.###},{5:0.###})",
                                                source.Id.Handle.ToString(),
                                                moveStart ? "start" : "end",
                                                endpoint.X,
                                                endpoint.Y,
                                                mixedThirtyTarget.X,
                                                mixedThirtyTarget.Y));
                                    }
                                }

                                if (TryForceSpecificVerticalEndpointToPoint(touchingThirtyId, endpoint, mixedThirtyTarget))
                                {
                                    movedEndpoints++;
                                    movedVerticalEndpoints++;
                                    changed = true;
                                }

                                if (changed)
                                {
                                    return true;
                                }
                            }

                            if (IsEndpointSharedWithOtherOuterSegments(source.Id, endpoint) ||
                                IsEndpointAnchoredToVerticalTargetEndpoint(endpoint))
                            {
                                return changed;
                            }

                            if (!TryResolveOuterEndpointTarget(endpoint, other, out var snappedTarget))
                            {
                                return changed;
                            }

                            // Preserve exact 5.02 m outer companions only when the alternative
                            // snap would create a materially longer bridge past the companion's
                            // hard stop. Smaller seam-connection pulls are still allowed.
                            if (TryResolveProtectedHorizontalCompanionEndpoint(source.Id, endpoint, other, out var protectedPoint) &&
                                endpoint.GetDistanceTo(protectedPoint) <= protectedCompanionRestoreMax &&
                                snappedTarget.GetDistanceTo(protectedPoint) >= protectedOuterCompanionOverhangKeepMin)
                            {
                                return changed;
                            }

                            if (snappedTarget.GetDistanceTo(endpoint) <= exactEndpointMoveTol)
                            {
                                return changed;
                            }

                            if (!TryMoveEndpoint(writable, moveStart, snappedTarget, endpointMoveTol))
                            {
                                return changed;
                            }

                            movedEndpoints++;
                            movedHorizontalEndpoints++;
                            updatedEndpoint = snappedTarget;
                            changed = true;
                            if (sampleMoves.Count < 24)
                            {
                                sampleMoves.Add(
                                    string.Format(
                                        CultureInfo.InvariantCulture,
                                        "id={0} {1} C-outer ({2:0.###},{3:0.###})->({4:0.###},{5:0.###})",
                                        source.Id.Handle.ToString(),
                                        moveStart ? "start" : "end",
                                        endpoint.X,
                                        endpoint.Y,
                                        snappedTarget.X,
                                        snappedTarget.Y));
                            }

                            if (TryForceTouchingVerticalTargetEndpointToPoint(endpoint, snappedTarget))
                            {
                                movedEndpoints++;
                                movedVerticalEndpoints++;
                            }

                            return true;
                        }

                        if (TryProcessOuterEndpoint(moveStart: true, a, b, out var updatedStart))
                        {
                            a = updatedStart;
                            lineMoved = true;
                        }

                        if (TryProcessOuterEndpoint(moveStart: false, b, a, out var updatedEnd))
                        {
                            b = updatedEnd;
                            lineMoved = true;
                        }

                        if (lineMoved)
                        {
                            correctionOuterSources[i] = (source.Id, a, b);
                            movedLines++;
                        }
                    }

                    correctionOuterSegments.Clear();
                    for (var i = 0; i < correctionOuterSources.Count; i++)
                    {
                        var source = correctionOuterSources[i];
                        correctionOuterSegments.Add((
                            source.A,
                            source.B,
                            Midpoint(source.A, source.B),
                            Math.Min(source.A.X, source.B.X),
                            Math.Max(source.A.X, source.B.X)));
                    }

                }

                // Keep correction inner boundaries local to each quarter span: split any
                // horizontal L-USEC-C-0 crossing interior vertical hard targets.
                const double splitTargetTouchTol = 1.2;
                const double splitTargetSpanTol = 0.6;
                const double splitEndpointParamTol = 0.02;
                const double splitParamMergeTol = 0.01;
                const double splitOuterOffsetTol = 2.4;
                const double splitOuterOverlapTol = 6.0;
                const double forceOuterOffsetTol = 0.85;
                const double forceOuterOverlapMin = 35.0;
                const double splitOuterEndpointVerticalTol = 3.5;
                const double splitOuterEndpointSpanTol = 2.0;

                bool IsNearVerticalSplitTarget(Point2d point)
                {
                    for (var vi = 0; vi < verticalTargets.Count; vi++)
                    {
                        var target = verticalTargets[vi];
                        if (Math.Abs(point.X - target.AxisX) <= splitOuterEndpointVerticalTol &&
                            point.Y >= target.MinY - splitOuterEndpointSpanTol &&
                            point.Y <= target.MaxY + splitOuterEndpointSpanTol)
                        {
                            return true;
                        }

                        if (DistancePointToSegment(point, target.A, target.B) <= splitOuterEndpointVerticalTol)
                        {
                            return true;
                        }
                    }

                    for (var si = 0; si < sectionVerticalSplitSegments.Count; si++)
                    {
                        var target = sectionVerticalSplitSegments[si];
                        if (DistancePointToSegment(point, target.A, target.B) <= splitOuterEndpointVerticalTol &&
                            point.Y >= target.MinY - splitOuterEndpointSpanTol &&
                            point.Y <= target.MaxY + splitOuterEndpointSpanTol)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                double GetCorrectionZeroBridgeParameter(Point2d point, Point2d a, Point2d b)
                {
                    var dir = b - a;
                    var lenSquared = dir.DotProduct(dir);
                    if (lenSquared <= 1e-9)
                    {
                        return 0.0;
                    }

                    return ((point - a).DotProduct(dir)) / lenSquared;
                }

                int CountInteriorCorrectionZeroBridgeBreakpoints(CorrectionSegment segment)
                {
                    var breakpoints = new List<Point2d>();
                    const double verticalTouchTol = 24.0;
                    const double uniqueBreakpointTol = 18.0;

                    void TryAddBreakpoint(Point2d endpoint)
                    {
                        var parameter = GetCorrectionZeroBridgeParameter(endpoint, segment.A, segment.B);
                        if (parameter <= 0.05 || parameter >= 0.95)
                        {
                            return;
                        }

                        if (DistancePointToSegment(endpoint, segment.A, segment.B) > verticalTouchTol)
                        {
                            return;
                        }

                        if (breakpoints.Any(existing => existing.GetDistanceTo(endpoint) <= uniqueBreakpointTol))
                        {
                            return;
                        }

                        breakpoints.Add(endpoint);
                    }

                    for (var vi = 0; vi < verticalEndpointAnchors.Count; vi++)
                    {
                        var vertical = verticalEndpointAnchors[vi];
                        TryAddBreakpoint(vertical.A);
                        TryAddBreakpoint(vertical.B);
                    }

                    for (var si = 0; si < sectionVerticalSplitSegments.Count; si++)
                    {
                        var split = sectionVerticalSplitSegments[si];
                        TryAddBreakpoint(split.A);
                        TryAddBreakpoint(split.B);
                    }

                    return breakpoints.Count;
                }

                int EraseOverlongCorrectionZeroBridgesAfterSplit()
                {
                    var liveInnerSegments = CollectHorizontalCorrectionSegments(
                        tr,
                        ms,
                        layer => string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase));
                    var erased = 0;
                    const double overlongBridgeMinLength = 1600.0;
                    foreach (var inner in liveInnerSegments)
                    {
                        var breakpointCount = CountInteriorCorrectionZeroBridgeBreakpoints(inner);
                        if (inner.Id.IsNull ||
                            !inner.IsHorizontalLike ||
                            inner.Length < overlongBridgeMinLength ||
                            breakpointCount < 2)
                        {
                            continue;
                        }

                        Entity? writable = null;
                        try
                        {
                            writable = tr.GetObject(inner.Id, OpenMode.ForWrite, false) as Entity;
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception)
                        {
                            continue;
                        }

                        if (writable == null || writable.IsErased)
                        {
                            continue;
                        }

                        writable.Erase();
                        erased++;
                        logger?.WriteLine(
                            $"CorrectionLine: erased overlong correction-zero bridge after split A=({inner.A.X:0.###},{inner.A.Y:0.###}) B=({inner.B.X:0.###},{inner.B.Y:0.###}) breakpoints={breakpointCount}.");
                    }

                    return erased;
                }

                for (var i = 0; i < correctionInnerSources.Count; i++)
                {
                    var source = correctionInnerSources[i];
                    Entity? writable = null;
                    try
                    {
                        writable = tr.GetObject(source.Id, OpenMode.ForWrite, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (writable == null || writable.IsErased ||
                        !string.Equals(writable.Layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!TryReadOpenLinearSegment(writable, out var a, out var b) || !IsHorizontalLike(a, b))
                    {
                        continue;
                    }

                    var ab = b - a;
                    var abLen2 = ab.DotProduct(ab);
                    if (abLen2 <= 1e-9)
                    {
                        continue;
                    }

                    var splitTs = new List<double>();
                    for (var vi = 0; vi < verticalTargets.Count; vi++)
                    {
                        var target = verticalTargets[vi];
                        if (!TryIntersectInfiniteLines(a, b, target.A, target.B, out var ip))
                        {
                            continue;
                        }

                        if (Math.Abs(ip.X - target.AxisX) > splitTargetTouchTol)
                        {
                            continue;
                        }

                        if (ip.Y < target.MinY - splitTargetSpanTol || ip.Y > target.MaxY + splitTargetSpanTol)
                        {
                            continue;
                        }

                        var t = (ip - a).DotProduct(ab) / abLen2;
                        if (t <= splitEndpointParamTol || t >= 1.0 - splitEndpointParamTol)
                        {
                            continue;
                        }

                        splitTs.Add(t);
                    }

                    AppendSectionVerticalSplitTs(sectionVerticalSplitSegments, a, b, ab, abLen2, splitTs);

                    // Also split by aligned correction outer endpoints so C-0 breaks at 1/4 seams
                    // even when hard vertical intersections are imperfect/missing.
                    var sourceMid = Midpoint(a, b);
                    var sourceMinX = Math.Min(a.X, b.X);
                    var sourceMaxX = Math.Max(a.X, b.X);
                    for (var oi = 0; oi < correctionOuterSegments.Count; oi++)
                    {
                        var outer = correctionOuterSegments[oi];
                        var overlapMin = Math.Max(sourceMinX, outer.MinX);
                        var overlapMax = Math.Min(sourceMaxX, outer.MaxX);
                        if (overlapMax - overlapMin < splitOuterOverlapTol)
                        {
                            continue;
                        }

                        var offset = Math.Abs(DistancePointToInfiniteLine(outer.Mid, a, b));
                        if (Math.Abs(offset - CorrectionLinePostInsetMeters) > splitOuterOffsetTol)
                        {
                            continue;
                        }

                        void TryAddOuterEndpointSplit(Point2d p)
                        {
                            if (!IsNearVerticalSplitTarget(p))
                            {
                                return;
                            }

                            var tOuter = (p - a).DotProduct(ab) / abLen2;
                            if (tOuter <= splitEndpointParamTol || tOuter >= 1.0 - splitEndpointParamTol)
                            {
                                return;
                            }

                            var projected = a + (ab * tOuter);
                            var lateral = projected.GetDistanceTo(p);
                            if (Math.Abs(lateral - CorrectionLinePostInsetMeters) > splitOuterOffsetTol)
                            {
                                return;
                            }

                            splitTs.Add(tOuter);
                            splitInnerFromOuterAnchors++;
                        }

                        TryAddOuterEndpointSplit(outer.A);
                        TryAddOuterEndpointSplit(outer.B);
                    }

                    if (splitTs.Count == 0)
                    {
                        continue;
                    }

                    splitTs.Sort();
                    var uniqueTs = new List<double>();
                    for (var ti = 0; ti < splitTs.Count; ti++)
                    {
                        var t = splitTs[ti];
                        if (uniqueTs.Count == 0 || Math.Abs(t - uniqueTs[uniqueTs.Count - 1]) > splitParamMergeTol)
                        {
                            uniqueTs.Add(t);
                        }
                    }

                    if (uniqueTs.Count == 0)
                    {
                        continue;
                    }

                    var points = new List<Point2d> { a };
                    for (var ti = 0; ti < uniqueTs.Count; ti++)
                    {
                        points.Add(a + (ab * uniqueTs[ti]));
                    }
                    points.Add(b);

                    if (points.Count < 3)
                    {
                        continue;
                    }

                    writable.Erase();
                    splitInnerSources++;
                    for (var pi = 0; pi < points.Count - 1; pi++)
                    {
                        var p0 = points[pi];
                        var p1 = points[pi + 1];
                        if (p0.GetDistanceTo(p1) <= 0.20)
                        {
                            continue;
                        }

                        var ln = new Line(
                            new Point3d(p0.X, p0.Y, 0.0),
                            new Point3d(p1.X, p1.Y, 0.0))
                        {
                            Layer = LayerUsecCorrectionZero,
                            ColorIndex = 256
                        };
                        ms.AppendEntity(ln);
                        tr.AddNewlyCreatedDBObject(ln, true);
                        splitInnerCreated++;
                    }
                }

                erasedOverlongCorrectionZeroBridges += EraseOverlongCorrectionZeroBridgesAfterSplit();

                // Keep correction outer boundaries local to quarter spans too. Without matching
                // splits on L-USEC-C, a single survivor can visually run across a north/south
                // road allowance even when the inset companion already breaks correctly.
                for (var i = 0; i < correctionOuterSources.Count; i++)
                {
                    var source = correctionOuterSources[i];
                    Entity? writable = null;
                    try
                    {
                        writable = tr.GetObject(source.Id, OpenMode.ForWrite, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (writable == null || writable.IsErased ||
                        !string.Equals(writable.Layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!TryReadOpenLinearSegment(writable, out var a, out var b) || !IsHorizontalLike(a, b))
                    {
                        continue;
                    }

                    var ab = b - a;
                    var abLen2 = ab.DotProduct(ab);
                    if (abLen2 <= 1e-9)
                    {
                        continue;
                    }

                    var splitTs = new List<double>();
                    AppendSectionVerticalSplitTs(outerSectionVerticalSplitSegments, a, b, ab, abLen2, splitTs);

                    if (splitTs.Count == 0)
                    {
                        continue;
                    }

                    splitTs.Sort();
                    var uniqueTs = new List<double>();
                    for (var ti = 0; ti < splitTs.Count; ti++)
                    {
                        var t = splitTs[ti];
                        if (uniqueTs.Count == 0 || Math.Abs(t - uniqueTs[uniqueTs.Count - 1]) > splitParamMergeTol)
                        {
                            uniqueTs.Add(t);
                        }
                    }

                    if (uniqueTs.Count == 0)
                    {
                        continue;
                    }

                    var points = new List<Point2d> { a };
                    for (var ti = 0; ti < uniqueTs.Count; ti++)
                    {
                        points.Add(a + (ab * uniqueTs[ti]));
                    }

                    points.Add(b);
                    if (points.Count < 3)
                    {
                        continue;
                    }

                    writable.Erase();
                    splitOuterSources++;
                    for (var pi = 0; pi < points.Count - 1; pi++)
                    {
                        var p0 = points[pi];
                        var p1 = points[pi + 1];
                        if (p0.GetDistanceTo(p1) <= 0.20)
                        {
                            continue;
                        }

                        var ln = new Line(
                            new Point3d(p0.X, p0.Y, 0.0),
                            new Point3d(p1.X, p1.Y, 0.0))
                        {
                            Layer = LayerUsecCorrection,
                            ColorIndex = 256
                        };
                        ms.AppendEntity(ln);
                        tr.AddNewlyCreatedDBObject(ln, true);
                        splitOuterCreated++;
                    }
                }

                // Final layer guard: if any seam-overlap horizontal L-USEC-0 / L-USEC-2012 survives,
                // force it to L-USEC-C. This is layer-only and does not modify geometry.
                var forcedOuterRelayer = 0;
                var correctionOuterAnchors = new List<(Point2d A, Point2d B, Point2d Mid, double MinX, double MaxX)>();
                foreach (ObjectId id in ms)
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    if (!string.Equals(ent.Layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!TryReadOpenLinearSegment(ent, out var a, out var b) || !IsHorizontalLike(a, b))
                    {
                        continue;
                    }

                    if (!DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    correctionOuterAnchors.Add((a, b, Midpoint(a, b), Math.Min(a.X, b.X), Math.Max(a.X, b.X)));
                }

                if (correctionOuterAnchors.Count > 0)
                {
                    foreach (ObjectId id in ms)
                    {
                        Entity? writable = null;
                        try
                        {
                            writable = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception)
                        {
                            continue;
                        }

                        if (writable == null || writable.IsErased)
                        {
                            continue;
                        }

                        var layer = writable.Layer ?? string.Empty;
                        var isTwentyLikeLayer =
                            string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(layer, "L-USEC2012", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(layer, "L-USEC-3018", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(layer, "L-USEC3018", StringComparison.OrdinalIgnoreCase);
                        if (!isTwentyLikeLayer)
                        {
                            continue;
                        }

                        if (!TryReadOpenLinearSegment(writable, out var a, out var b) || !IsHorizontalLike(a, b))
                        {
                            continue;
                        }

                        if (!DoesSegmentIntersectAnyWindow(a, b))
                        {
                            continue;
                        }

                        var minX = Math.Min(a.X, b.X);
                        var maxX = Math.Max(a.X, b.X);
                        var mid = Midpoint(a, b);
                        var shouldRelayer = false;
                        for (var oi = 0; oi < correctionOuterAnchors.Count; oi++)
                        {
                            var outer = correctionOuterAnchors[oi];
                            var overlapMin = Math.Max(minX, outer.MinX);
                            var overlapMax = Math.Min(maxX, outer.MaxX);
                            var overlap = overlapMax - overlapMin;
                            if (overlap < forceOuterOverlapMin)
                            {
                                continue;
                            }

                            var d1 = Math.Abs(DistancePointToInfiniteLine(mid, outer.A, outer.B));
                            if (d1 > forceOuterOffsetTol)
                            {
                                continue;
                            }

                            var d2 = Math.Abs(DistancePointToInfiniteLine(outer.Mid, a, b));
                            if (d2 > forceOuterOffsetTol)
                            {
                                continue;
                            }

                            shouldRelayer = true;
                            break;
                        }

                        if (!shouldRelayer)
                        {
                            continue;
                        }

                        writable.Layer = LayerUsecCorrection;
                        writable.ColorIndex = 256;
                        forcedOuterRelayer++;
                    }
                }

                var addedInwardShortCorrectionZeroCompanions = 0;
                var retargetedInwardShortCorrectionZeroVerticals = 0;
                var inwardShortCorrectionZeroSamples = new List<string>();

                // Some seam-edge short 30.18 rows are intentionally relayered to L-USEC-C-0 to
                // preserve the traced outer correction fabric. When one of those short rows is
                // still acting as the terminal stop for an ordinary vertical USEC, the drafting
                // result needs the one-band-inward short companion as well so the visible
                // ordinary vertical lands on the inner short row instead of the outer short row.
                // Keep this narrowly scoped to the unique live pattern:
                // - short horizontal C-0 row
                // - touched by an ordinary vertical USEC endpoint
                // - has a parallel ordinary vertical companion about one corridor width away
                // - has a longer parallel C-0 row on the inward side about four inset widths away
                // This stays reusable and avoids broad corridor cloning elsewhere.
                var liveCorrectionZeroSegments = new List<CorrectionSegment>();
                var liveCorrectionOuterSegments = new List<CorrectionSegment>();
                var liveOrdinaryVerticals = new List<(ObjectId Id, string Layer, Point2d A, Point2d B)>();
                foreach (ObjectId id in ms)
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenLinearSegment(ent, out var a, out var b) || !DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    if (string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) &&
                        IsHorizontalLike(a, b))
                    {
                        liveCorrectionZeroSegments.Add(new CorrectionSegment(id, layer, a, b));
                        continue;
                    }

                    if (string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase) &&
                        IsHorizontalLike(a, b))
                    {
                        liveCorrectionOuterSegments.Add(new CorrectionSegment(id, layer, a, b));
                        continue;
                    }

                    if (IsCorrectionUsecLayer(layer) &&
                        !IsCorrectionLayer(layer) &&
                        IsVerticalLike(a, b))
                    {
                        liveOrdinaryVerticals.Add((id, layer, a, b));
                    }
                }

                const double shortCorrectionZeroMinLength = 60.0;
                const double shortCorrectionZeroMaxLength = 180.0;
                const double longCorrectionZeroMinLength = 400.0;
                const double shortCorrectionZeroTouchTol = 0.60;
                const double shortCorrectionZeroParallelDotMin = 0.995;
                const double shortCorrectionZeroParallelOffsetMin = 19.5;
                const double shortCorrectionZeroParallelOffsetMax = 20.7;
                const double shortCorrectionZeroInwardOffsetMin = 35.0;
                const double shortCorrectionZeroInwardOffsetMax = 45.0;
                const double shortCorrectionZeroOverlapMin = 50.0;
                const double shortCorrectionZeroCompanionStep = CorrectionLinePostInsetMeters * 2.0;
                const double shortCorrectionZeroCreateTol = 0.05;
                const double ordinaryVerticalPairLengthMin = 300.0;

                bool ShouldCreateShortCompanionAsCorrectionOuter(
                    CorrectionSegment inwardLongRow,
                    Point2d candidateA,
                    Point2d candidateB)
                {
                    var candidateDir = candidateB - candidateA;
                    var candidateLen = candidateDir.Length;
                    if (candidateLen <= 1e-6)
                    {
                        return false;
                    }

                    var candidateU = candidateDir / candidateLen;
                    var candidateMid = Midpoint(candidateA, candidateB);
                    const double companionOffsetTol = 0.35;
                    for (var oi = 0; oi < liveCorrectionOuterSegments.Count; oi++)
                    {
                        var outer = liveCorrectionOuterSegments[oi];
                        if (!outer.IsHorizontalLike)
                        {
                            continue;
                        }

                        var outerDir = outer.B - outer.A;
                        var outerLen = outerDir.Length;
                        if (outerLen <= 1e-6)
                        {
                            continue;
                        }

                        var outerU = outerDir / outerLen;
                        if (Math.Abs(outerU.DotProduct(candidateU)) < shortCorrectionZeroParallelDotMin)
                        {
                            continue;
                        }

                        var overlapMin = Math.Max(Math.Min(candidateA.X, candidateB.X), outer.MinX);
                        var overlapMax = Math.Min(Math.Max(candidateA.X, candidateB.X), outer.MaxX);
                        if (overlapMax - overlapMin < shortCorrectionZeroOverlapMin)
                        {
                            continue;
                        }

                        var inwardToOuter = Math.Abs(DistancePointToInfiniteLine(inwardLongRow.Mid, outer.A, outer.B));
                        if (Math.Abs(inwardToOuter - CorrectionLinePostInsetMeters) > companionOffsetTol)
                        {
                            continue;
                        }

                        var outerToInward = Math.Abs(DistancePointToInfiniteLine(outer.Mid, inwardLongRow.A, inwardLongRow.B));
                        if (Math.Abs(outerToInward - CorrectionLinePostInsetMeters) > companionOffsetTol)
                        {
                            continue;
                        }

                        if (DistancePointToInfiniteLine(candidateMid, outer.A, outer.B) <= companionOffsetTol)
                        {
                            return true;
                        }

                        return true;
                    }

                    return false;
                }

                var candidateShortCorrectionZeroRows = liveCorrectionZeroSegments.ToList();
                foreach (var shortRow in candidateShortCorrectionZeroRows)
                {
                    if (!shortRow.IsHorizontalLike)
                    {
                        continue;
                    }

                    var shortLen = shortRow.Length;
                    if (shortLen < shortCorrectionZeroMinLength || shortLen > shortCorrectionZeroMaxLength)
                    {
                        continue;
                    }

                    var touchPoint = default(Point2d);
                    var touchMoveStart = false;
                    var touchingVertical = default((ObjectId Id, string Layer, Point2d A, Point2d B));
                    var foundTouchingVertical = false;
                    for (var vi = 0; vi < liveOrdinaryVerticals.Count; vi++)
                    {
                        var vertical = liveOrdinaryVerticals[vi];
                        var moveStart =
                            vertical.A.GetDistanceTo(shortRow.A) <= shortCorrectionZeroTouchTol ||
                            vertical.A.GetDistanceTo(shortRow.B) <= shortCorrectionZeroTouchTol;
                        var moveEnd =
                            vertical.B.GetDistanceTo(shortRow.A) <= shortCorrectionZeroTouchTol ||
                            vertical.B.GetDistanceTo(shortRow.B) <= shortCorrectionZeroTouchTol;
                        if (moveStart == moveEnd)
                        {
                            continue;
                        }

                        touchingVertical = vertical;
                        touchMoveStart = moveStart;
                        touchPoint = moveStart ? vertical.A : vertical.B;
                        foundTouchingVertical = true;
                        break;
                    }

                    if (!foundTouchingVertical)
                    {
                        continue;
                    }

                    var touchingDir = touchingVertical.B - touchingVertical.A;
                    var touchingLen = touchingDir.Length;
                    if (touchingLen < ordinaryVerticalPairLengthMin)
                    {
                        continue;
                    }

                    var touchingU = touchingDir / touchingLen;
                    var foundParallelVertical = false;
                    for (var vi = 0; vi < liveOrdinaryVerticals.Count; vi++)
                    {
                        var otherVertical = liveOrdinaryVerticals[vi];
                        if (otherVertical.Id == touchingVertical.Id)
                        {
                            continue;
                        }

                        var otherDir = otherVertical.B - otherVertical.A;
                        var otherLen = otherDir.Length;
                        if (otherLen < ordinaryVerticalPairLengthMin)
                        {
                            continue;
                        }

                        var otherU = otherDir / otherLen;
                        if (Math.Abs(otherU.DotProduct(touchingU)) < shortCorrectionZeroParallelDotMin)
                        {
                            continue;
                        }

                        var offset = Math.Abs(SignedDistanceToInfiniteLine(touchPoint, otherVertical.A, otherVertical.B));
                        if (offset < shortCorrectionZeroParallelOffsetMin || offset > shortCorrectionZeroParallelOffsetMax)
                        {
                            continue;
                        }

                        foundParallelVertical = true;
                        break;
                    }

                    if (!foundParallelVertical)
                    {
                        continue;
                    }

                    CorrectionSegment? inwardLongRow = null;
                    Point2d inwardProjection = default;
                    var bestInwardOffset = double.MaxValue;
                    for (var li = 0; li < liveCorrectionZeroSegments.Count; li++)
                    {
                        var longRow = liveCorrectionZeroSegments[li];
                        if (longRow.Id == shortRow.Id || !longRow.IsHorizontalLike)
                        {
                            continue;
                        }

                        if (longRow.Length < longCorrectionZeroMinLength)
                        {
                            continue;
                        }

                        var longDir = longRow.B - longRow.A;
                        var longLen = longDir.Length;
                        if (longLen <= 1e-6)
                        {
                            continue;
                        }

                        var longU = longDir / longLen;
                        var shortU = (shortRow.B - shortRow.A) / shortLen;
                        if (Math.Abs(longU.DotProduct(shortU)) < shortCorrectionZeroParallelDotMin)
                        {
                            continue;
                        }

                        var overlapMin = Math.Max(shortRow.MinX, longRow.MinX);
                        var overlapMax = Math.Min(shortRow.MaxX, longRow.MaxX);
                        if (overlapMax - overlapMin < shortCorrectionZeroOverlapMin)
                        {
                            continue;
                        }

                        var shortMidpoint = Midpoint(shortRow.A, shortRow.B);
                        var projected =
                            longRow.A +
                            (longU * ((shortMidpoint - longRow.A).DotProduct(longU)));
                        var inwardOffset = projected.GetDistanceTo(Midpoint(shortRow.A, shortRow.B));
                        if (inwardOffset < shortCorrectionZeroInwardOffsetMin ||
                            inwardOffset > shortCorrectionZeroInwardOffsetMax)
                        {
                            continue;
                        }

                        if (inwardOffset < bestInwardOffset)
                        {
                            bestInwardOffset = inwardOffset;
                            inwardLongRow = longRow;
                            inwardProjection = projected;
                        }
                    }

                    if (inwardLongRow == null)
                    {
                        continue;
                    }

                    var shortMid = Midpoint(shortRow.A, shortRow.B);
                    var inwardVector = inwardProjection - shortMid;
                    var inwardLength = inwardVector.Length;
                    if (inwardLength <= 1e-6)
                    {
                        continue;
                    }

                    var inwardU = inwardVector / inwardLength;
                    var candidateA = shortRow.A + (inwardU * shortCorrectionZeroCompanionStep);
                    var candidateB = shortRow.B + (inwardU * shortCorrectionZeroCompanionStep);
                    var candidateIsBufferedOnlyOuter = ShouldCreateShortCompanionAsCorrectionOuter(
                        inwardLongRow.Value,
                        candidateA,
                        candidateB);
                    if ((candidateIsBufferedOnlyOuter &&
                         HasMatchingCorrectionSegment(liveCorrectionOuterSegments, candidateA, candidateB)) ||
                        (!candidateIsBufferedOnlyOuter &&
                         HasMatchingCorrectionSegment(liveCorrectionZeroSegments, candidateA, candidateB)))
                    {
                        continue;
                    }

                    var targetLayer = candidateIsBufferedOnlyOuter
                        ? LayerUsecCorrection
                        : LayerUsecCorrectionZero;
                    var createdCompanion = new Line(
                        new Point3d(candidateA.X, candidateA.Y, 0.0),
                        new Point3d(candidateB.X, candidateB.Y, 0.0))
                    {
                        Layer = targetLayer,
                        ColorIndex = 256
                    };
                    var createdId = ms.AppendEntity(createdCompanion);
                    tr.AddNewlyCreatedDBObject(createdCompanion, true);
                    if (candidateIsBufferedOnlyOuter)
                    {
                        liveCorrectionOuterSegments.Add(new CorrectionSegment(createdId, targetLayer, candidateA, candidateB));
                    }
                    else
                    {
                        liveCorrectionZeroSegments.Add(new CorrectionSegment(createdId, targetLayer, candidateA, candidateB));
                    }
                    addedInwardShortCorrectionZeroCompanions++;

                    try
                    {
                        if (tr.GetObject(touchingVertical.Id, OpenMode.ForWrite, false) is Entity verticalWritable &&
                            verticalWritable != null &&
                            !verticalWritable.IsErased)
                        {
                            var touchTarget =
                                touchPoint.GetDistanceTo(shortRow.A) <= touchPoint.GetDistanceTo(shortRow.B)
                                    ? candidateA
                                    : candidateB;
                            if (TryIntersectInfiniteLinesForPluginGeometry(
                                    touchingVertical.A,
                                    touchingVertical.B,
                                    candidateA,
                                    candidateB,
                                    out var candidateIntersection) &&
                                DistancePointToSegment(candidateIntersection, candidateA, candidateB) <= 0.60)
                            {
                                touchTarget = candidateIntersection;
                            }
                            if (TryMoveEndpointForCorrectionLinePost(
                                    verticalWritable,
                                    touchMoveStart,
                                    touchTarget,
                                    shortCorrectionZeroCreateTol) &&
                                TryReadOpenLinearSegment(verticalWritable, out var newVerticalA, out var newVerticalB))
                            {
                                retargetedInwardShortCorrectionZeroVerticals++;
                                for (var vi = 0; vi < liveOrdinaryVerticals.Count; vi++)
                                {
                                    if (liveOrdinaryVerticals[vi].Id != touchingVertical.Id)
                                    {
                                        continue;
                                    }

                                    liveOrdinaryVerticals[vi] = (
                                        touchingVertical.Id,
                                        touchingVertical.Layer ?? string.Empty,
                                        newVerticalA,
                                        newVerticalB);
                                    break;
                                }
                            }
                        }
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                    }

                if (inwardShortCorrectionZeroSamples.Count < 8)
                {
                    inwardShortCorrectionZeroSamples.Add(
                        string.Format(
                            CultureInfo.InvariantCulture,
                                "short={0} A=({1:0.###},{2:0.###}) B=({3:0.###},{4:0.###}) inwardA=({5:0.###},{6:0.###}) inwardB=({7:0.###},{8:0.###}) vertical={9} layer={10}",
                                shortRow.Id.Handle.ToString(),
                                shortRow.A.X,
                                shortRow.A.Y,
                                shortRow.B.X,
                                shortRow.B.Y,
                                candidateA.X,
                                candidateA.Y,
                                candidateB.X,
                                candidateB.Y,
                                touchingVertical.Id.Handle.ToString(),
                                targetLayer));
                    }
                }

                // Restore longer split C-0 overlap pieces that should continue from a split-node
                // anchor to an ordinary vertical endpoint when the full parallel correction outer
                // still covers that missing span one band away.
                var longSplitCorrectionZeroOverlapCreated = 0;
                var longSplitCorrectionZeroOverlapSamples = new List<string>();
                var verticalQsecCorrectionZeroRetargeted = 0;
                var verticalQsecCorrectionZeroSamples = new List<string>();
                var liveSplitCorrectionZeroSegments = liveCorrectionZeroSegments.ToList();
                var liveSplitOrdinaryVerticals = liveOrdinaryVerticals.ToList();
                var liveSplitCorrectionOuterSegments = correctionOuterSources
                    .Where(s => IsHorizontalLike(s.A, s.B))
                    .Select(s => new CorrectionSegment(s.Id, LayerUsecCorrection, s.A, s.B))
                    .ToList();

                const double longSplitCorrectionZeroGapMin = 120.0;
                const double longSplitCorrectionZeroGapMax = 420.0;
                const double longSplitCorrectionZeroEndpointTol = 0.75;
                const double longSplitCorrectionZeroParamTol = 0.02;
                const double longSplitCorrectionZeroParallelDotMin = 0.995;
                const double longSplitCorrectionZeroOffsetTol = 0.25;
                const double longSplitCorrectionZeroCoverageSlack = 1.0;

                bool HasFullCorrectionOuterCoverageForSplitGap(Point2d gapA, Point2d gapB)
                {
                    var gapDir = gapB - gapA;
                    var gapLen = gapDir.Length;
                    if (gapLen <= 1e-6)
                    {
                        return false;
                    }

                    var gapU = gapDir / gapLen;
                    var gapMid = Midpoint(gapA, gapB);
                    for (var oi = 0; oi < liveSplitCorrectionOuterSegments.Count; oi++)
                    {
                        var outer = liveSplitCorrectionOuterSegments[oi];
                        if (!outer.IsHorizontalLike)
                        {
                            continue;
                        }

                        var outerDir = outer.B - outer.A;
                        var outerLen = outerDir.Length;
                        if (outerLen <= 1e-6)
                        {
                            continue;
                        }

                        var outerU = outerDir / outerLen;
                        if (Math.Abs(outerU.DotProduct(gapU)) < longSplitCorrectionZeroParallelDotMin)
                        {
                            continue;
                        }

                        var offset = Math.Abs(DistancePointToInfiniteLine(gapMid, outer.A, outer.B));
                        if (Math.Abs(offset - CorrectionLinePostInsetMeters) > longSplitCorrectionZeroOffsetTol)
                        {
                            continue;
                        }

                        var gapMinX = Math.Min(gapA.X, gapB.X);
                        var gapMaxX = Math.Max(gapA.X, gapB.X);
                        var overlapMin = Math.Max(gapMinX, outer.MinX);
                        var overlapMax = Math.Min(gapMaxX, outer.MaxX);
                        if (overlapMax - overlapMin < gapLen - longSplitCorrectionZeroCoverageSlack)
                        {
                            continue;
                        }

                        return true;
                    }

                    return false;
                }

                bool TryGetLongSplitCorrectionZeroEndpointScore(double endpointDistance, out double score)
                {
                    score = Math.Min(
                        endpointDistance,
                        Math.Abs(endpointDistance - CorrectionLinePostInsetMeters));

                    return score <= longSplitCorrectionZeroEndpointTol;
                }

                int RetargetVerticalQsecEndpointsToCorrectionZeroIntersections()
                {
                    var zeroRows = new List<CorrectionSegment>();
                    var outerRows = new List<CorrectionSegment>();
                    var qsecRows = new List<(ObjectId Id, Point2d A, Point2d B)>();
                    foreach (ObjectId id in ms)
                    {
                        Entity? ent = null;
                        try
                        {
                            ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception)
                        {
                            continue;
                        }

                        if (ent == null || ent.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenLinearSegment(ent, out var a, out var b) ||
                            !DoesSegmentIntersectAnyWindow(a, b))
                        {
                            continue;
                        }

                        var layer = ent.Layer ?? string.Empty;
                        if (string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) &&
                            IsHorizontalLike(a, b))
                        {
                            zeroRows.Add(new CorrectionSegment(id, layer, a, b));
                            continue;
                        }

                        if (string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase) &&
                            IsHorizontalLike(a, b))
                        {
                            outerRows.Add(new CorrectionSegment(id, layer, a, b));
                            continue;
                        }

                        if (string.Equals(layer, "L-QSEC", StringComparison.OrdinalIgnoreCase) &&
                            IsVerticalLike(a, b))
                        {
                            qsecRows.Add((id, a, b));
                        }
                    }

                    if (zeroRows.Count == 0 || qsecRows.Count == 0)
                    {
                        return 0;
                    }

                    double SegmentParameter(Point2d point, Point2d a, Point2d b)
                    {
                        var ab = b - a;
                        var len2 = ab.DotProduct(ab);
                        if (len2 <= 1e-9)
                        {
                            return 0.0;
                        }

                        return (point - a).DotProduct(ab) / len2;
                    }

                    bool TouchesCorrectionOuter(Point2d point)
                    {
                        for (var i = 0; i < outerRows.Count; i++)
                        {
                            var row = outerRows[i];
                            if (DistancePointToSegment(point, row.A, row.B) <= 0.90)
                            {
                                return true;
                            }
                        }

                        return false;
                    }

                    bool TouchesCorrectionZeroEndpoint(Point2d point)
                    {
                        for (var i = 0; i < zeroRows.Count; i++)
                        {
                            var row = zeroRows[i];
                            if (point.GetDistanceTo(row.A) <= 1.20 ||
                                point.GetDistanceTo(row.B) <= 1.20)
                            {
                                return true;
                            }
                        }

                        return false;
                    }

                    var retargeted = 0;
                    const double qsecZeroSegmentTouchTol = 0.75;
                    const double qsecZeroMinMove = 0.60;
                    const double qsecZeroMaxMove = 45.0;
                    const double qsecZeroShortMoveMax = CorrectionLinePostInsetMeters + 1.50;
                    const double qsecZeroDeepMoveMin = 24.0;
                    const double qsecZeroDeepMoveMax = 42.0;
                    const double qsecZeroEndpointSnapTol = 0.35;
                    const double qsecZeroInteriorParamTol = 0.05;

                    foreach (var qsec in qsecRows)
                    {
                        Entity? writable = null;
                        try
                        {
                            writable = tr.GetObject(qsec.Id, OpenMode.ForWrite, false) as Entity;
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception)
                        {
                            continue;
                        }

                        if (writable == null || writable.IsErased ||
                            !TryReadOpenLinearSegment(writable, out var currentA, out var currentB) ||
                            !IsVerticalLike(currentA, currentB))
                        {
                            continue;
                        }

                        for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                        {
                            var endpoint = endpointIndex == 0 ? currentA : currentB;
                            var other = endpointIndex == 0 ? currentB : currentA;
                            var endpointTouchesOuter = TouchesCorrectionOuter(endpoint);
                            var endpointTouchesZeroEndpoint = TouchesCorrectionZeroEndpoint(endpoint);
                            var bestFound = false;
                            var bestTarget = endpoint;
                            var bestScore = double.MaxValue;
                            var bestKind = string.Empty;

                            for (var zi = 0; zi < zeroRows.Count; zi++)
                            {
                                var zero = zeroRows[zi];
                                if (!TryIntersectInfiniteLines(endpoint, other, zero.A, zero.B, out var intersection))
                                {
                                    continue;
                                }

                                if (DistancePointToSegment(intersection, zero.A, zero.B) > qsecZeroSegmentTouchTol)
                                {
                                    continue;
                                }

                                var target = intersection;
                                var endpointA = intersection.GetDistanceTo(zero.A);
                                var endpointB = intersection.GetDistanceTo(zero.B);
                                if (endpointA <= qsecZeroEndpointSnapTol || endpointB <= qsecZeroEndpointSnapTol)
                                {
                                    target = endpointA <= endpointB ? zero.A : zero.B;
                                }

                                var move = endpoint.GetDistanceTo(target);
                                if (move <= qsecZeroMinMove || move > qsecZeroMaxMove)
                                {
                                    continue;
                                }

                                var parameter = SegmentParameter(intersection, zero.A, zero.B);
                                var isShortInsetRetarget =
                                    move <= qsecZeroShortMoveMax &&
                                    endpointTouchesOuter;
                                var isDeepJunctionRetarget =
                                    endpointTouchesZeroEndpoint &&
                                    move >= qsecZeroDeepMoveMin &&
                                    move <= qsecZeroDeepMoveMax &&
                                    parameter > qsecZeroInteriorParamTol &&
                                    parameter < 1.0 - qsecZeroInteriorParamTol;

                                if (!isShortInsetRetarget && !isDeepJunctionRetarget)
                                {
                                    continue;
                                }

                                var score = isShortInsetRetarget
                                    ? move
                                    : 100.0 - move;
                                if (!bestFound || score < bestScore)
                                {
                                    bestFound = true;
                                    bestScore = score;
                                    bestTarget = target;
                                    bestKind = isShortInsetRetarget ? "short-inset" : "deep-junction";
                                }
                            }

                            if (!bestFound)
                            {
                                continue;
                            }

                            if (TryMoveEndpoint(writable, endpointIndex == 0, bestTarget, endpointMoveTol) &&
                                TryReadOpenLinearSegment(writable, out currentA, out currentB))
                            {
                                retargeted++;
                                if (verticalQsecCorrectionZeroSamples.Count < 8)
                                {
                                    verticalQsecCorrectionZeroSamples.Add(
                                        string.Format(
                                            CultureInfo.InvariantCulture,
                                            "qsec={0} kind={1} from=({2:0.###},{3:0.###}) to=({4:0.###},{5:0.###})",
                                            qsec.Id.Handle.ToString(),
                                            bestKind,
                                            endpoint.X,
                                            endpoint.Y,
                                            bestTarget.X,
                                            bestTarget.Y));
                                }
                            }
                        }
                    }

                    return retargeted;
                }

                foreach (var zeroRow in liveSplitCorrectionZeroSegments.ToList())
                {
                    if (!zeroRow.IsHorizontalLike)
                    {
                        continue;
                    }

                    var zeroDir = zeroRow.B - zeroRow.A;
                    var zeroLen = zeroDir.Length;
                    var zeroLen2 = zeroDir.DotProduct(zeroDir);
                    if (zeroLen <= 1e-6 || zeroLen2 <= 1e-9)
                    {
                        continue;
                    }

                    var rowCandidates = new List<(ObjectId VerticalId, Point2d ProjectedPoint, Point2d Anchor, double Gap, double EndpointDistance, double EndpointScore)>();
                    for (var vi = 0; vi < liveSplitOrdinaryVerticals.Count; vi++)
                    {
                        var vertical = liveSplitOrdinaryVerticals[vi];
                        if (!TryIntersectInfiniteLinesForPluginGeometry(
                                vertical.A,
                                vertical.B,
                                zeroRow.A,
                                zeroRow.B,
                                out var projectedPoint))
                        {
                            continue;
                        }

                        var endpointDistance = Math.Min(
                            vertical.A.GetDistanceTo(projectedPoint),
                            vertical.B.GetDistanceTo(projectedPoint));
                        if (!TryGetLongSplitCorrectionZeroEndpointScore(endpointDistance, out var endpointScore))
                        {
                            continue;
                        }

                        var t = (projectedPoint - zeroRow.A).DotProduct(zeroDir) / zeroLen2;
                        if (t > longSplitCorrectionZeroParamTol &&
                            t < 1.0 - longSplitCorrectionZeroParamTol)
                        {
                            continue;
                        }

                        var anchor = t <= longSplitCorrectionZeroParamTol ? zeroRow.A : zeroRow.B;
                        var gap = anchor.GetDistanceTo(projectedPoint);
                        if (gap < longSplitCorrectionZeroGapMin ||
                            gap > longSplitCorrectionZeroGapMax)
                        {
                            continue;
                        }

                        if (!HasFullCorrectionOuterCoverageForSplitGap(projectedPoint, anchor))
                        {
                            continue;
                        }

                        rowCandidates.Add((vertical.Id, projectedPoint, anchor, gap, endpointDistance, endpointScore));
                    }

                    if (rowCandidates.Count == 0)
                    {
                        continue;
                    }

                    var bestCandidate = rowCandidates
                        .OrderByDescending(c => c.Gap)
                        .ThenBy(c => c.EndpointScore)
                        .ThenBy(c => c.ProjectedPoint.X)
                        .First();

                    if (HasMatchingCorrectionSegment(
                            liveSplitCorrectionZeroSegments,
                            bestCandidate.ProjectedPoint,
                            bestCandidate.Anchor))
                    {
                        continue;
                    }

                    var createdLine = new Line(
                        new Point3d(bestCandidate.ProjectedPoint.X, bestCandidate.ProjectedPoint.Y, 0.0),
                        new Point3d(bestCandidate.Anchor.X, bestCandidate.Anchor.Y, 0.0))
                    {
                        Layer = LayerUsecCorrectionZero,
                        ColorIndex = 256
                    };
                    var createdId = ms.AppendEntity(createdLine);
                    tr.AddNewlyCreatedDBObject(createdLine, true);
                    liveSplitCorrectionZeroSegments.Add(new CorrectionSegment(
                        createdId,
                        LayerUsecCorrectionZero,
                        bestCandidate.ProjectedPoint,
                        bestCandidate.Anchor));
                    longSplitCorrectionZeroOverlapCreated++;

                    if (longSplitCorrectionZeroOverlapSamples.Count < 8)
                    {
                        longSplitCorrectionZeroOverlapSamples.Add(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "zero={0} vertical={1} gapA=({2:0.###},{3:0.###}) gapB=({4:0.###},{5:0.###}) endpointDist={6:0.###}",
                                zeroRow.Id.Handle.ToString(),
                                bestCandidate.VerticalId.Handle.ToString(),
                                bestCandidate.ProjectedPoint.X,
                                bestCandidate.ProjectedPoint.Y,
                                bestCandidate.Anchor.X,
                                bestCandidate.Anchor.Y,
                                bestCandidate.EndpointDistance));
                    }
                }

                verticalQsecCorrectionZeroRetargeted += RetargetVerticalQsecEndpointsToCorrectionZeroIntersections();

                if (longSplitCorrectionZeroOverlapCreated > 0)
                {
                    logger?.WriteLine(
                        $"CorrectionLine: long split C-0 overlap pass created={longSplitCorrectionZeroOverlapCreated}.");
                    for (var i = 0; i < longSplitCorrectionZeroOverlapSamples.Count; i++)
                    {
                        logger?.WriteLine("CorrectionLine:   long-split-zero " + longSplitCorrectionZeroOverlapSamples[i]);
                    }
                }

                if (verticalQsecCorrectionZeroRetargeted > 0)
                {
                    logger?.WriteLine(
                        $"CorrectionLine: vertical QSEC correction-zero retarget moved={verticalQsecCorrectionZeroRetargeted}.");
                    for (var i = 0; i < verticalQsecCorrectionZeroSamples.Count; i++)
                    {
                        logger?.WriteLine("CorrectionLine:   qsec-zero " + verticalQsecCorrectionZeroSamples[i]);
                    }
                }

                tr.Commit();
                logger?.WriteLine(
                    $"CorrectionLine: C-0 endpoint snap scanned={scannedEndpoints}, movedEndpoints={movedEndpoints}, movedHorizontal={movedHorizontalEndpoints}, movedVertical={movedVerticalEndpoints}, movedLines={movedLines}, alreadyConnected={alreadyConnected}, sharedSkipped={sharedEndpointSkipped}, noDirectionResolved={noDirectionResolved}, noTargetFound={noTargetFound}, maxMove={maxMove:0.##}.");
                logger?.WriteLine(
                    $"CorrectionLine: C-0 split at boundaries sources={splitInnerSources}, created={splitInnerCreated}, outerAnchors={splitInnerFromOuterAnchors}.");
                logger?.WriteLine(
                    $"CorrectionLine: C split at vertical boundaries sources={splitOuterSources}, created={splitOuterCreated}.");
                if (erasedOverlongCorrectionZeroBridges > 0)
                {
                    logger?.WriteLine(
                        $"CorrectionLine: overlong correction-zero bridge cleanup erased={erasedOverlongCorrectionZeroBridges}.");
                }
                logger?.WriteLine(
                    $"CorrectionLine: forced correction outer relayer converted {forcedOuterRelayer} seam-overlap L-USEC20/3018 segment(s) to {LayerUsecCorrection} (anchors={correctionOuterAnchors.Count}).");
                if (addedInwardShortCorrectionZeroCompanions > 0 || retargetedInwardShortCorrectionZeroVerticals > 0)
                {
                    logger?.WriteLine(
                        $"CorrectionLine: inward short C-0 companion pass created={addedInwardShortCorrectionZeroCompanions}, retargetedVerticals={retargetedInwardShortCorrectionZeroVerticals}.");
                    for (var i = 0; i < inwardShortCorrectionZeroSamples.Count; i++)
                    {
                        logger?.WriteLine("CorrectionLine:   inward-short " + inwardShortCorrectionZeroSamples[i]);
                    }
                }

                if (sampleMoves.Count > 0)
                {
                    logger?.WriteLine($"CorrectionLine: C-0 endpoint snap samples ({sampleMoves.Count})");
                    for (var i = 0; i < sampleMoves.Count; i++)
                    {
                        logger?.WriteLine("CorrectionLine:   " + sampleMoves[i]);
                    }
                }

                return movedEndpoints > 0 ||
                       splitInnerCreated > 0 ||
                       splitOuterCreated > 0 ||
                       erasedOverlongCorrectionZeroBridges > 0 ||
                       addedInwardShortCorrectionZeroCompanions > 0 ||
                       longSplitCorrectionZeroOverlapCreated > 0 ||
                       verticalQsecCorrectionZeroRetargeted > 0 ||
                       retargetedInwardShortCorrectionZeroVerticals > 0;
            }
        }

        private static bool RetargetVerticalQsecEndpointsToCorrectionZeroIntersections(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            Logger? logger,
            bool allowDeepJunctionRetargets = true)
        {
            if (database == null || requestedQuarterIds == null)
            {
                return false;
            }

            var clipWindows = MergeOverlappingClipWindows(BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0));
            if (clipWindows.Count == 0)
            {
                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b) => DoesSegmentIntersectAnyWindowForCorrectionLinePost(a, b, clipWindows);
            bool IsHorizontalLike(Point2d a, Point2d b) => IsHorizontalLikeForCorrectionLinePost(a, b);
            bool IsVerticalLike(Point2d a, Point2d b) => IsVerticalLikeForCorrectionLinePost(a, b);

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var zeroRows = new List<CorrectionSegment>();
                var outerRows = new List<CorrectionSegment>();
                var qsecCandidateRows = new List<(ObjectId Id, Point2d A, Point2d B, bool IntersectsWindow)>();

                foreach (ObjectId id in ms)
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenLinearSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    var intersectsWindow = DoesSegmentIntersectAnyWindow(a, b);
                    if (string.Equals(layer, "L-QSEC", StringComparison.OrdinalIgnoreCase) &&
                        IsVerticalLike(a, b))
                    {
                        qsecCandidateRows.Add((id, a, b, intersectsWindow));
                        continue;
                    }

                    if (!intersectsWindow)
                    {
                        continue;
                    }

                    if (string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) &&
                        IsHorizontalLike(a, b))
                    {
                        zeroRows.Add(new CorrectionSegment(id, layer, a, b));
                        continue;
                    }

                    if (string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase) &&
                        IsHorizontalLike(a, b))
                    {
                        outerRows.Add(new CorrectionSegment(id, layer, a, b));
                        continue;
                    }
                }

                if (zeroRows.Count == 0 || qsecCandidateRows.Count == 0)
                {
                    tr.Commit();
                    return false;
                }

                double SegmentParameter(Point2d point, Point2d a, Point2d b)
                {
                    var ab = b - a;
                    var len2 = ab.DotProduct(ab);
                    if (len2 <= 1e-9)
                    {
                        return 0.0;
                    }

                    return (point - a).DotProduct(ab) / len2;
                }

                bool TouchesCorrectionOuter(Point2d point)
                {
                    for (var i = 0; i < outerRows.Count; i++)
                    {
                        var row = outerRows[i];
                        if (DistancePointToSegment(point, row.A, row.B) <= 0.90)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                bool TouchesCorrectionZeroEndpoint(Point2d point)
                {
                    for (var i = 0; i < zeroRows.Count; i++)
                    {
                        var row = zeroRows[i];
                        if (point.GetDistanceTo(row.A) <= 1.20 ||
                            point.GetDistanceTo(row.B) <= 1.20)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                var qsecRows = new List<(ObjectId Id, Point2d A, Point2d B)>();
                for (var i = 0; i < qsecCandidateRows.Count; i++)
                {
                    var qsec = qsecCandidateRows[i];
                    if (qsec.IntersectsWindow ||
                        TouchesCorrectionOuter(qsec.A) ||
                        TouchesCorrectionOuter(qsec.B) ||
                        TouchesCorrectionZeroEndpoint(qsec.A) ||
                        TouchesCorrectionZeroEndpoint(qsec.B))
                    {
                        qsecRows.Add((qsec.Id, qsec.A, qsec.B));
                    }
                }

                if (qsecRows.Count == 0)
                {
                    tr.Commit();
                    return false;
                }

                var retargeted = 0;
                var samples = new List<string>();
                const double qsecZeroSegmentTouchTol = 0.75;
                const double qsecZeroMinMove = 0.60;
                const double qsecZeroMaxMove = 45.0;
                const double qsecZeroShortMoveMax = CorrectionLinePostInsetMeters + 1.50;
                const double qsecZeroDeepMoveMin = 24.0;
                const double qsecZeroDeepMoveMax = 42.0;
                const double qsecZeroEndpointSnapTol = 0.35;
                const double qsecZeroInteriorParamTol = 0.05;

                foreach (var qsec in qsecRows)
                {
                    Entity? writable = null;
                    try
                    {
                        writable = tr.GetObject(qsec.Id, OpenMode.ForWrite, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (writable == null || writable.IsErased ||
                        !TryReadOpenLinearSegment(writable, out var currentA, out var currentB) ||
                        !IsVerticalLike(currentA, currentB))
                    {
                        continue;
                    }

                    for (var endpointIndex = 0; endpointIndex <= 1; endpointIndex++)
                    {
                        var endpoint = endpointIndex == 0 ? currentA : currentB;
                        var other = endpointIndex == 0 ? currentB : currentA;
                        var endpointTouchesOuter = TouchesCorrectionOuter(endpoint);
                        var endpointTouchesZeroEndpoint = TouchesCorrectionZeroEndpoint(endpoint);
                        var bestFound = false;
                        var bestTarget = endpoint;
                        var bestScore = double.MaxValue;
                        var bestKind = string.Empty;

                        for (var zi = 0; zi < zeroRows.Count; zi++)
                        {
                            var zero = zeroRows[zi];
                            if (!TryIntersectInfiniteLinesForPluginGeometry(endpoint, other, zero.A, zero.B, out var intersection))
                            {
                                continue;
                            }

                            if (DistancePointToSegment(intersection, zero.A, zero.B) > qsecZeroSegmentTouchTol)
                            {
                                continue;
                            }

                            var target = intersection;
                            var endpointA = intersection.GetDistanceTo(zero.A);
                            var endpointB = intersection.GetDistanceTo(zero.B);
                            if (endpointA <= qsecZeroEndpointSnapTol || endpointB <= qsecZeroEndpointSnapTol)
                            {
                                target = endpointA <= endpointB ? zero.A : zero.B;
                            }

                            var move = endpoint.GetDistanceTo(target);
                            if (move <= qsecZeroMinMove || move > qsecZeroMaxMove)
                            {
                                continue;
                            }

                            var parameter = SegmentParameter(intersection, zero.A, zero.B);
                            var isShortInsetRetarget =
                                move <= qsecZeroShortMoveMax &&
                                endpointTouchesOuter;
                            var isDeepJunctionRetarget =
                                allowDeepJunctionRetargets &&
                                endpointTouchesZeroEndpoint &&
                                move >= qsecZeroDeepMoveMin &&
                                move <= qsecZeroDeepMoveMax &&
                                parameter > qsecZeroInteriorParamTol &&
                                parameter < 1.0 - qsecZeroInteriorParamTol;

                            if (!isShortInsetRetarget && !isDeepJunctionRetarget)
                            {
                                continue;
                            }

                            var score = isShortInsetRetarget
                                ? move
                                : 100.0 - move;
                            if (!bestFound || score < bestScore)
                            {
                                bestFound = true;
                                bestScore = score;
                                bestTarget = target;
                                bestKind = isShortInsetRetarget ? "short-inset" : "deep-junction";
                            }
                        }

                        if (!bestFound)
                        {
                            continue;
                        }

                        if (TryMoveEndpointForCorrectionLinePost(writable, endpointIndex == 0, bestTarget, 0.05) &&
                            TryReadOpenLinearSegment(writable, out currentA, out currentB))
                        {
                            retargeted++;
                            if (samples.Count < 8)
                            {
                                samples.Add(
                                    string.Format(
                                        CultureInfo.InvariantCulture,
                                        "qsec={0} kind={1} from=({2:0.###},{3:0.###}) to=({4:0.###},{5:0.###})",
                                        qsec.Id.Handle.ToString(),
                                        bestKind,
                                        endpoint.X,
                                        endpoint.Y,
                                        bestTarget.X,
                                        bestTarget.Y));
                            }
                        }
                    }
                }

                tr.Commit();
                if (retargeted > 0)
                {
                    logger?.WriteLine(
                        $"Cleanup: vertical QSEC correction-zero retarget moved={retargeted}.");
                    for (var i = 0; i < samples.Count; i++)
                    {
                        logger?.WriteLine("Cleanup:   qsec-zero " + samples[i]);
                    }
                }

                return retargeted > 0;
            }
        }

        private static bool IsPointInAnyWindowForCorrectionLinePost(Point2d point, IReadOnlyList<Extents3d> clipWindows)
        {
            if (clipWindows == null || clipWindows.Count == 0)
            {
                return false;
            }

            for (var i = 0; i < clipWindows.Count; i++)
            {
                var window = clipWindows[i];
                if (point.X >= window.MinPoint.X && point.X <= window.MaxPoint.X &&
                    point.Y >= window.MinPoint.Y && point.Y <= window.MaxPoint.Y)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool DoesSegmentIntersectAnyWindowForCorrectionLinePost(Point2d a, Point2d b, IReadOnlyList<Extents3d> clipWindows)
        {
            if (IsPointInAnyWindowForCorrectionLinePost(a, clipWindows) ||
                IsPointInAnyWindowForCorrectionLinePost(b, clipWindows))
            {
                return true;
            }

            for (var i = 0; i < clipWindows.Count; i++)
            {
                if (TryClipSegmentToWindow(a, b, clipWindows[i], out _, out _))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsHorizontalLikeForCorrectionLinePost(Point2d a, Point2d b)
        {
            var dx = Math.Abs(b.X - a.X);
            var dy = Math.Abs(b.Y - a.Y);
            return dx >= (dy * 1.2);
        }

        private static bool IsVerticalLikeForCorrectionLinePost(Point2d a, Point2d b)
        {
            var dx = Math.Abs(b.X - a.X);
            var dy = Math.Abs(b.Y - a.Y);
            return dy >= (dx * 1.2);
        }

        private static bool TryMoveEndpointForCorrectionLinePost(Entity writable, bool moveStart, Point2d target, double moveTol)
        {
            if (writable is Line line)
            {
                var old = moveStart
                    ? new Point2d(line.StartPoint.X, line.StartPoint.Y)
                    : new Point2d(line.EndPoint.X, line.EndPoint.Y);
                if (old.GetDistanceTo(target) <= moveTol)
                {
                    return false;
                }

                if (moveStart)
                {
                    line.StartPoint = new Point3d(target.X, target.Y, line.StartPoint.Z);
                }
                else
                {
                    line.EndPoint = new Point3d(target.X, target.Y, line.EndPoint.Z);
                }

                return true;
            }

            if (writable is Polyline polyline && !polyline.Closed && polyline.NumberOfVertices >= 2)
            {
                var index = moveStart ? 0 : polyline.NumberOfVertices - 1;
                var old = polyline.GetPoint2dAt(index);
                if (old.GetDistanceTo(target) <= moveTol)
                {
                    return false;
                }

                polyline.SetPointAt(index, target);
                return true;
            }

            return false;
        }

        private static bool IntersectsAnyCorrectionSeamWindow(CorrectionSegment segment, IReadOnlyList<CorrectionSeam> seams)
        {
            for (var i = 0; i < seams.Count; i++)
            {
                var seam = seams[i];
                if (segment.MaxX < seam.MinX - 40.0 || segment.MinX > seam.MaxX + 40.0)
                {
                    continue;
                }

                if (!seam.IntersectsExpandedStrip(segment, 40.0))
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        private static List<CorrectionSegment> CollectHorizontalCorrectionSegments(
            Transaction tr,
            BlockTableRecord modelSpace,
            Func<string, bool> layerPredicate)
        {
            var liveSegments = new List<CorrectionSegment>();
            if (tr == null || modelSpace == null || layerPredicate == null)
            {
                return liveSegments;
            }

            foreach (ObjectId id in modelSpace)
            {
                Entity? ent = null;
                try
                {
                    ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    continue;
                }

                if (ent == null || ent.IsErased)
                {
                    continue;
                }

                var layer = ent.Layer ?? string.Empty;
                if (!layerPredicate(layer) || !TryReadOpenLinearSegment(ent, out var a, out var b))
                {
                    continue;
                }

                var seg = new CorrectionSegment(id, layer, a, b);
                if (!seg.IsHorizontalLike)
                {
                    continue;
                }

                liveSegments.Add(seg);
            }

            return liveSegments;
        }

        private static void UpdateTrackedCorrectionSegmentLayer(
            IList<CorrectionSegment> segments,
            ObjectId id,
            string layer)
        {
            if (segments == null || id.IsNull)
            {
                return;
            }

            for (var i = 0; i < segments.Count; i++)
            {
                if (segments[i].Id != id)
                {
                    continue;
                }

                segments[i] = new CorrectionSegment(id, layer, segments[i].A, segments[i].B);
                return;
            }
        }

        private static string BuildCorrectionSeamKey(int zone, string meridian, int range, int northTownship)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2}|{3}",
                zone,
                NormalizeNumberToken(meridian),
                range,
                northTownship);
        }

        private static bool IsCorrectionLayer(string layer)
        {
            if (string.IsNullOrWhiteSpace(layer))
            {
                return false;
            }

            return string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCorrectionCandidateLayer(string layer)
        {
            if (string.IsNullOrWhiteSpace(layer))
            {
                return false;
            }

            return string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layer, "L-SEC-0", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layer, "L-USEC-3018", StringComparison.OrdinalIgnoreCase) ||
                   IsCorrectionLayer(layer);
        }

        private static bool IsCorrectionSurveyedLayer(string layer)
        {
            if (string.IsNullOrWhiteSpace(layer))
            {
                return false;
            }

            return string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layer, "L-SEC-0", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCorrectionUsecLayer(string layer)
        {
            if (string.IsNullOrWhiteSpace(layer))
            {
                return false;
            }

            return string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layer, "L-USEC-3018", StringComparison.OrdinalIgnoreCase) ||
                   IsCorrectionLayer(layer);
        }

        private static double GetCorrectionHorizontalOverlap(CorrectionSegment segment, double minX, double maxX)
        {
            var overlapMin = Math.Max(segment.MinX, minX);
            var overlapMax = Math.Min(segment.MaxX, maxX);
            return overlapMax - overlapMin;
        }

        private static bool ShouldTraceCorrectionCandidate(Point2d a, Point2d b)
        {
            return IntersectsCorrectionTraceWindow(a, b, 624100.0, 5836950.0, 625150.0, 5837105.0) ||
                   IntersectsCorrectionTraceWindow(a, b, 632450.0, 5837240.0, 634250.0, 5837320.0) ||
                   IntersectsCorrectionTraceWindow(a, b, 614200.0, 5844850.0, 615950.0, 5846555.0);
        }

        private static bool IntersectsCorrectionTraceWindow(
            Point2d a,
            Point2d b,
            double minX,
            double minY,
            double maxX,
            double maxY)
        {
            var segMinX = Math.Min(a.X, b.X);
            var segMaxX = Math.Max(a.X, b.X);
            var segMinY = Math.Min(a.Y, b.Y);
            var segMaxY = Math.Max(a.Y, b.Y);
            return !(segMaxX < minX || segMinX > maxX || segMaxY < minY || segMinY > maxY);
        }

        private static List<CorrectionSegment> SelectCorrectionHorizontalBand(
            IReadOnlyList<CorrectionSegment> candidates,
            Func<Point2d, double> targetSignedOffset,
            Func<Point2d, double> centerSignedOffset,
            bool preferAboveCenter,
            double strictTol)
        {
            if (candidates == null || candidates.Count == 0 || targetSignedOffset == null || centerSignedOffset == null)
            {
                return new List<CorrectionSegment>();
            }

            bool IsOnExpectedSide(CorrectionSegment c)
            {
                var centerY = centerSignedOffset(c.Mid);
                if (preferAboveCenter)
                {
                    return centerY >= -0.1;
                }

                return centerY <= 0.1;
            }

            double GetCenterOffsetDrift(CorrectionSegment c)
            {
                return Math.Abs(centerSignedOffset(c.A) - centerSignedOffset(c.B));
            }

            bool HasSameSideCompanionAtGap(CorrectionSegment source, string companionLayer, double expectedGap, double tolerance)
            {
                var sourceSignedOffset = centerSignedOffset(source.Mid);
                var sourceDistance = Math.Abs(sourceSignedOffset);
                for (var i = 0; i < candidates.Count; i++)
                {
                    var candidate = candidates[i];
                    if (candidate.Id == source.Id ||
                        !candidate.IsHorizontalLike ||
                        !string.Equals(candidate.Layer, companionLayer, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var overlap = GetCorrectionHorizontalOverlap(candidate, source.MinX, source.MaxX);
                    if (!CorrectionSouthBoundaryPreference.IsCompanionCoverageAcceptable(overlap, source.Length))
                    {
                        continue;
                    }

                    var candidateSignedOffset = centerSignedOffset(candidate.Mid);
                    if (Math.Sign(candidateSignedOffset) != Math.Sign(sourceSignedOffset))
                    {
                        continue;
                    }

                    var candidateDistance = Math.Abs(candidateSignedOffset);
                    if (Math.Abs(candidateDistance - sourceDistance - expectedGap) <= tolerance ||
                        Math.Abs(sourceDistance - candidateDistance - expectedGap) <= tolerance)
                    {
                        return true;
                    }
                }

                return false;
            }

            List<CorrectionSegment> FilterPreferredOuterCandidates(List<CorrectionSegment> band)
            {
                if (band == null || band.Count <= 1)
                {
                    return band ?? new List<CorrectionSegment>();
                }

                const double companionGapTolerance = 2.50;
                const double shortOuterLengthFloor = 150.0;
                const double shortOuterLengthFraction = 0.35;
                var maxLength = band.Max(c => c.Length);
                var minPreferredLength = Math.Max(shortOuterLengthFloor, maxLength * shortOuterLengthFraction);
                var hasNonTwentyCandidate = band.Any(
                    c => !string.Equals(c.Layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase));
                var filtered = new List<CorrectionSegment>(band.Count);
                for (var i = 0; i < band.Count; i++)
                {
                    var candidate = band[i];
                    if (hasNonTwentyCandidate &&
                        string.Equals(candidate.Layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var hasThirtyCompanion = HasSameSideCompanionAtGap(
                        candidate,
                        LayerUsecThirty,
                        CorrectionLinePairGapMeters,
                        companionGapTolerance);
                    if (string.Equals(candidate.Layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) &&
                        hasThirtyCompanion)
                    {
                        continue;
                    }

                    var hasTwentyCompanion = HasSameSideCompanionAtGap(
                        candidate,
                        LayerUsecTwenty,
                        CorrectionLinePairGapMeters,
                        companionGapTolerance);
                    if (string.Equals(candidate.Layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) &&
                        !hasTwentyCompanion &&
                        candidate.Length < minPreferredLength)
                    {
                        continue;
                    }

                    filtered.Add(candidate);
                }

                return filtered.Count > 0 ? filtered : band;
            }

            var inStrictBand = candidates
                .Where(c => IsOnExpectedSide(c) && Math.Abs(targetSignedOffset(c.Mid)) <= strictTol)
                .ToList();
            if (inStrictBand.Count > 0)
            {
                return FilterPreferredOuterCandidates(inStrictBand);
            }

            var bestDistance = double.MaxValue;
            var bestResidual = double.NaN;
            for (var i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (!IsOnExpectedSide(c))
                {
                    continue;
                }

                var distance = Math.Abs(targetSignedOffset(c.Mid));
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestResidual = distance;
                }
            }

            if (double.IsNaN(bestResidual))
            {
                return new List<CorrectionSegment>();
            }

            var fallbackBandTol = Math.Max(0.9, strictTol * 0.55);
            var maxFallbackCenterDrift = Math.Max(8.0, strictTol * 3.5);
            var fallbackBand = candidates
                .Where(c => IsOnExpectedSide(c) &&
                            GetCenterOffsetDrift(c) <= maxFallbackCenterDrift &&
                            Math.Abs(Math.Abs(targetSignedOffset(c.Mid)) - bestResidual) <= fallbackBandTol)
                .ToList();
            return FilterPreferredOuterCandidates(fallbackBand);
        }

        private static bool TryFindCorrectionInnerCompanion(
            CorrectionSegment outer,
            IReadOnlyList<CorrectionSegment> horizontalCandidates,
            double expectedInset,
            Func<Point2d, double> centerSignedOffset,
            out CorrectionSegment companion)
        {
            companion = default;
            if (horizontalCandidates == null || horizontalCandidates.Count == 0 || centerSignedOffset == null)
            {
                return false;
            }

            var outerSignedOffset = centerSignedOffset(outer.Mid);
            var outerCenterDistance = Math.Abs(outerSignedOffset);
            var found = false;
            var bestOverlap = double.MinValue;
            var bestOffsetDelta = double.MaxValue;
            for (var i = 0; i < horizontalCandidates.Count; i++)
            {
                var candidate = horizontalCandidates[i];
                if (candidate.Id == outer.Id)
                {
                    continue;
                }

                if (!candidate.IsHorizontalLike)
                {
                    continue;
                }

                var overlap = GetCorrectionHorizontalOverlap(candidate, outer.MinX, outer.MaxX);
                if (!CorrectionSouthBoundaryPreference.IsCompanionCoverageAcceptable(
                        overlap,
                        outer.Length))
                {
                    continue;
                }

                var candidateSignedOffset = centerSignedOffset(candidate.Mid);
                var candidateCenterDistance = Math.Abs(candidateSignedOffset);
                if (!CorrectionSouthBoundaryPreference.IsSameSideInsetCompanionCandidate(
                        outerSignedOffset,
                        candidateSignedOffset,
                        expectedInset,
                        toleranceMeters: 1.25))
                {
                    continue;
                }

                var offsetDelta = Math.Abs((outerCenterDistance - candidateCenterDistance) - expectedInset);

                if (!found ||
                    overlap > bestOverlap + 1e-6 ||
                    (Math.Abs(overlap - bestOverlap) <= 1e-6 && offsetDelta < bestOffsetDelta))
                {
                    companion = candidate;
                    found = true;
                    bestOverlap = overlap;
                    bestOffsetDelta = offsetDelta;
                }
            }

            return found;
        }

        private static bool TryFindCorrectionOuterCompanion(
            CorrectionSegment inner,
            IReadOnlyList<CorrectionSegment> horizontalCandidates,
            double expectedInset,
            Func<Point2d, double> centerSignedOffset,
            out CorrectionSegment companion)
        {
            companion = default;
            if (horizontalCandidates == null || horizontalCandidates.Count == 0 || centerSignedOffset == null)
            {
                return false;
            }

            var innerSignedOffset = centerSignedOffset(inner.Mid);
            var innerCenterDistance = Math.Abs(innerSignedOffset);
            var found = false;
            var bestOverlap = double.MinValue;
            var bestOffsetDelta = double.MaxValue;
            for (var i = 0; i < horizontalCandidates.Count; i++)
            {
                var candidate = horizontalCandidates[i];
                if (candidate.Id == inner.Id ||
                    !candidate.IsHorizontalLike ||
                    !string.Equals(candidate.Layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var overlap = GetCorrectionHorizontalOverlap(candidate, inner.MinX, inner.MaxX);
                if (!CorrectionSouthBoundaryPreference.IsCompanionCoverageAcceptable(
                        overlap,
                        inner.Length))
                {
                    continue;
                }

                var candidateSignedOffset = centerSignedOffset(candidate.Mid);
                if (!CorrectionSouthBoundaryPreference.IsSameSideInsetCompanionCandidate(
                        candidateSignedOffset,
                        innerSignedOffset,
                        expectedInset,
                        toleranceMeters: 1.25))
                {
                    continue;
                }

                var candidateCenterDistance = Math.Abs(candidateSignedOffset);
                var offsetDelta = Math.Abs((candidateCenterDistance - innerCenterDistance) - expectedInset);
                if (!found ||
                    overlap > bestOverlap + 1e-6 ||
                    (Math.Abs(overlap - bestOverlap) <= 1e-6 && offsetDelta < bestOffsetDelta))
                {
                    companion = candidate;
                    found = true;
                    bestOverlap = overlap;
                    bestOffsetDelta = offsetDelta;
                }
            }

            return found;
        }

        private static bool TryFindMatchingCorrectionOuterSegment(
            IEnumerable<CorrectionSegment> segments,
            Point2d a,
            Point2d b,
            out CorrectionSegment match)
        {
            match = default;
            if (segments == null)
            {
                return false;
            }

            const double endpointTolerance = 0.20;
            foreach (var candidate in segments)
            {
                if (!candidate.IsHorizontalLike ||
                    !string.Equals(candidate.Layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var directMatch =
                    candidate.A.GetDistanceTo(a) <= endpointTolerance &&
                    candidate.B.GetDistanceTo(b) <= endpointTolerance;
                if (directMatch)
                {
                    match = candidate;
                    return true;
                }

                var reverseMatch =
                    candidate.A.GetDistanceTo(b) <= endpointTolerance &&
                    candidate.B.GetDistanceTo(a) <= endpointTolerance;
                if (reverseMatch)
                {
                    match = candidate;
                    return true;
                }
            }

            return false;
        }

        private static bool TryCloneCorrectionSegment(
            CorrectionSegment source,
            string targetLayer,
            IList<CorrectionSegment> liveSegments,
            BlockTableRecord modelSpace,
            Transaction tr,
            out CorrectionSegment clone)
        {
            clone = default;
            if (modelSpace == null || tr == null || string.IsNullOrWhiteSpace(targetLayer))
            {
                return false;
            }

            if (liveSegments != null && HasMatchingCorrectionSegment(liveSegments.ToList(), source.A, source.B))
            {
                for (var i = 0; i < liveSegments.Count; i++)
                {
                    var candidate = liveSegments[i];
                    if (!candidate.IsHorizontalLike ||
                        !string.Equals(candidate.Layer, targetLayer, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var directMatch =
                        candidate.A.GetDistanceTo(source.A) <= 0.20 &&
                        candidate.B.GetDistanceTo(source.B) <= 0.20;
                    var reverseMatch =
                        candidate.A.GetDistanceTo(source.B) <= 0.20 &&
                        candidate.B.GetDistanceTo(source.A) <= 0.20;
                    if (directMatch || reverseMatch)
                    {
                        clone = candidate;
                        return true;
                    }
                }
            }

            var line = new Line(
                new Point3d(source.A.X, source.A.Y, 0.0),
                new Point3d(source.B.X, source.B.Y, 0.0))
            {
                Layer = targetLayer,
                ColorIndex = 256
            };

            var newId = modelSpace.AppendEntity(line);
            tr.AddNewlyCreatedDBObject(line, true);
            if (newId.IsNull)
            {
                return false;
            }

            clone = new CorrectionSegment(newId, targetLayer, source.A, source.B);
            liveSegments?.Add(clone);
            return true;
        }

        private static bool TryEnsureCorrectionOuterSegment(
            CorrectionSegment source,
            IList<CorrectionSegment> liveSegments,
            BlockTableRecord modelSpace,
            Transaction tr,
            out CorrectionSegment correctionOuter,
            out bool createdNew)
        {
            correctionOuter = source;
            createdNew = false;
            if (!source.IsHorizontalLike)
            {
                return false;
            }

            if (string.Equals(source.Layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (TryFindMatchingCorrectionOuterSegment(liveSegments, source.A, source.B, out var existing))
            {
                correctionOuter = existing;
                return true;
            }

            if (tr == null)
            {
                return false;
            }

            if (!TryRelayerCorrectionSegment(tr, source.Id, LayerUsecCorrection))
            {
                return false;
            }

            correctionOuter = new CorrectionSegment(source.Id, LayerUsecCorrection, source.A, source.B);
            UpdateTrackedCorrectionSegmentLayer(liveSegments, source.Id, LayerUsecCorrection);

            return true;
        }

        private static bool TryCreateCorrectionInnerCompanion(
            CorrectionSegment outer,
            Func<Point2d, double> centerSignedOffset,
            double inset,
            IReadOnlyList<CorrectionSegment> existingSegments,
            BlockTableRecord modelSpace,
            Transaction tr,
            out ObjectId newId,
            out Point2d newA,
            out Point2d newB)
        {
            newId = ObjectId.Null;
            newA = default;
            newB = default;
            if (!outer.IsHorizontalLike || modelSpace == null || tr == null || centerSignedOffset == null)
            {
                return false;
            }

            var direction = outer.B - outer.A;
            var length = direction.Length;
            if (length <= 1e-6)
            {
                return false;
            }

            var normal = new Vector2d(-direction.Y / length, direction.X / length);
            var oppositeNormal = new Vector2d(-normal.X, -normal.Y);

            var mid = outer.Mid;
            var candidateMidA = mid + (normal * inset);
            var candidateMidB = mid + (oppositeNormal * inset);
            var chosenOffset = Math.Abs(centerSignedOffset(candidateMidA)) <= Math.Abs(centerSignedOffset(candidateMidB))
                ? (normal * inset)
                : (oppositeNormal * inset);

            const double parallelDirectionDotMin = 0.985;
            const double nearbyOuterMinOffset = 7.5;
            const double nearbyOuterMaxOffset = 25.0;
            var nearbyOuterFound = false;
            var nearbyOuterDistance = double.MaxValue;
            CorrectionSegment nearbyOuter = default;
            for (var i = 0; i < existingSegments.Count; i++)
            {
                var candidate = existingSegments[i];
                if (candidate.Id == outer.Id ||
                    !candidate.IsHorizontalLike ||
                    !string.Equals(candidate.Layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var candidateDirection = candidate.B - candidate.A;
                var candidateLength = candidateDirection.Length;
                if (candidateLength <= 1e-6)
                {
                    continue;
                }

                var candidateUnit = candidateDirection / candidateLength;
                var directionDot = Math.Abs(((direction / length).DotProduct(candidateUnit)));
                if (directionDot < parallelDirectionDotMin)
                {
                    continue;
                }

                var overlap = GetCorrectionHorizontalOverlap(candidate, outer.MinX, outer.MaxX);
                if (overlap < Math.Max(20.0, outer.Length * 0.20))
                {
                    continue;
                }

                var offsetFromOuter = Math.Abs(DistancePointToInfiniteLine(candidate.Mid, outer.A, outer.B));
                if (offsetFromOuter < nearbyOuterMinOffset || offsetFromOuter > nearbyOuterMaxOffset)
                {
                    continue;
                }

                if (offsetFromOuter >= nearbyOuterDistance - 1e-6)
                {
                    continue;
                }

                nearbyOuterFound = true;
                nearbyOuterDistance = offsetFromOuter;
                nearbyOuter = candidate;
            }

            if (nearbyOuterFound)
            {
                var candidateADistance = Math.Abs(DistancePointToInfiniteLine(candidateMidA, nearbyOuter.A, nearbyOuter.B));
                var candidateBDistance = Math.Abs(DistancePointToInfiniteLine(candidateMidB, nearbyOuter.A, nearbyOuter.B));
                chosenOffset = candidateADistance >= candidateBDistance
                    ? (normal * inset)
                    : (oppositeNormal * inset);
            }

            newA = outer.A + chosenOffset;
            newB = outer.B + chosenOffset;
            if (newA.GetDistanceTo(newB) <= 1e-4)
            {
                return false;
            }

            if (HasMatchingCorrectionSegment(existingSegments, newA, newB))
            {
                return false;
            }

            var line = new Line(
                new Point3d(newA.X, newA.Y, 0.0),
                new Point3d(newB.X, newB.Y, 0.0))
            {
                Layer = LayerUsecCorrectionZero,
                ColorIndex = 256
            };

            newId = modelSpace.AppendEntity(line);
            tr.AddNewlyCreatedDBObject(line, true);
            return !newId.IsNull;
        }

        private static bool HasMatchingCorrectionSegment(
            IReadOnlyList<CorrectionSegment> segments,
            Point2d a,
            Point2d b)
        {
            if (segments == null || segments.Count == 0)
            {
                return false;
            }

            const double endpointTolerance = 0.20;
            const double directionDotMin = 0.985;
            const double nearDuplicateLineTol = 0.40;
            const double nearDuplicateOverlapFraction = 0.85;
            const double nearDuplicateLengthRatioMax = 1.35;
            const double coveredSpanTolerance = 0.40;
            var targetDir = b - a;
            var targetLen = targetDir.Length;
            if (targetLen <= 1e-6)
            {
                return false;
            }

            var targetU = targetDir / targetLen;
            var targetMid = new Point2d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);

            static double GetProjectedOverlap(Point2d a0, Point2d a1, Point2d b0, Point2d b1)
            {
                var dir = a1 - a0;
                var len = dir.Length;
                if (len <= 1e-6)
                {
                    return 0.0;
                }

                var u = dir / len;
                var bS0 = (b0 - a0).DotProduct(u);
                var bS1 = (b1 - a0).DotProduct(u);
                var bMin = Math.Min(bS0, bS1);
                var bMax = Math.Max(bS0, bS1);
                return Math.Max(0.0, Math.Min(len, bMax) - Math.Max(0.0, bMin));
            }

            for (var i = 0; i < segments.Count; i++)
            {
                var candidate = segments[i];
                if (!candidate.IsHorizontalLike)
                {
                    continue;
                }

                var directMatch =
                    candidate.A.GetDistanceTo(a) <= endpointTolerance &&
                    candidate.B.GetDistanceTo(b) <= endpointTolerance;
                if (directMatch)
                {
                    return true;
                }

                var reverseMatch =
                    candidate.A.GetDistanceTo(b) <= endpointTolerance &&
                    candidate.B.GetDistanceTo(a) <= endpointTolerance;
                if (reverseMatch)
                {
                    return true;
                }

                var candidateDir = candidate.B - candidate.A;
                var candidateLen = candidateDir.Length;
                if (candidateLen <= 1e-6)
                {
                    continue;
                }

                var shorterLength = Math.Min(candidateLen, targetLen);
                var longerLength = Math.Max(candidateLen, targetLen);
                if (longerLength > shorterLength * nearDuplicateLengthRatioMax)
                {
                    continue;
                }

                var candidateU = candidateDir / candidateLen;
                if (Math.Abs(candidateU.DotProduct(targetU)) < directionDotMin)
                {
                    continue;
                }

                var overlap = GetProjectedOverlap(a, b, candidate.A, candidate.B);
                if (overlap < shorterLength * nearDuplicateOverlapFraction)
                {
                    continue;
                }

                var candidateMid = candidate.Mid;
                if (Math.Abs(DistancePointToInfiniteLine(candidateMid, a, b)) > nearDuplicateLineTol ||
                    Math.Abs(DistancePointToInfiniteLine(targetMid, candidate.A, candidate.B)) > nearDuplicateLineTol)
                {
                    continue;
                }

                return true;
            }

            for (var i = 0; i < segments.Count; i++)
            {
                var candidate = segments[i];
                if (!candidate.IsHorizontalLike)
                {
                    continue;
                }

                var candidateDir = candidate.B - candidate.A;
                var candidateLen = candidateDir.Length;
                if (candidateLen <= 1e-6)
                {
                    continue;
                }

                var candidateU = candidateDir / candidateLen;
                if (Math.Abs(candidateU.DotProduct(targetU)) < directionDotMin)
                {
                    continue;
                }

                if (Math.Abs(DistancePointToInfiniteLine(a, candidate.A, candidate.B)) > nearDuplicateLineTol ||
                    Math.Abs(DistancePointToInfiniteLine(b, candidate.A, candidate.B)) > nearDuplicateLineTol)
                {
                    continue;
                }

                var stationA = (a - candidate.A).DotProduct(candidateU);
                var stationB = (b - candidate.A).DotProduct(candidateU);
                var minStation = Math.Min(stationA, stationB);
                var maxStation = Math.Max(stationA, stationB);
                if (minStation < -coveredSpanTolerance || maxStation > candidateLen + coveredSpanTolerance)
                {
                    continue;
                }

                return true;
            }

            return false;
        }

        private static bool HasTrackedInsetCompanion(
            IReadOnlyList<CorrectionSegment> segments,
            Point2d a,
            Point2d b,
            Func<string, bool> layerPredicate)
        {
            if (segments == null || segments.Count == 0 || layerPredicate == null)
            {
                return false;
            }

            var dir = b - a;
            var len = dir.Length;
            if (len <= 1e-6)
            {
                return false;
            }

            const double directionDotMin = 0.985;
            const double companionOffsetTol = 1.25;
            var u = dir / len;
            var mid = new Point2d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);
            var positiveCoverageIntervals = new List<(double Min, double Max)>();
            var negativeCoverageIntervals = new List<(double Min, double Max)>();

            static double GetProjectedOverlapLength(Point2d a0, Point2d a1, Point2d b0, Point2d b1)
            {
                var da = a1 - a0;
                var lenA = da.Length;
                if (lenA <= 1e-6)
                {
                    return double.NegativeInfinity;
                }

                var ua = da / lenA;
                var bS0 = (b0 - a0).DotProduct(ua);
                var bS1 = (b1 - a0).DotProduct(ua);
                var bMin = Math.Min(bS0, bS1);
                var bMax = Math.Max(bS0, bS1);
                return Math.Min(lenA, bMax) - Math.Max(0.0, bMin);
            }

            static double DistanceToInfiniteLine(Point2d point, Point2d lineA, Point2d lineB)
            {
                var dir = lineB - lineA;
                var len = dir.Length;
                if (len <= 1e-6)
                {
                    return point.GetDistanceTo(lineA);
                }

                return Math.Abs(((point.X - lineA.X) * dir.Y - (point.Y - lineA.Y) * dir.X) / len);
            }

            for (var i = 0; i < segments.Count; i++)
            {
                var other = segments[i];
                if (!other.IsHorizontalLike || !layerPredicate(other.Layer))
                {
                    continue;
                }

                var otherDir = other.B - other.A;
                var otherLen = otherDir.Length;
                if (otherLen <= 1e-6)
                {
                    continue;
                }

                if (Math.Abs(u.DotProduct(otherDir / otherLen)) < directionDotMin)
                {
                    continue;
                }

                var overlap = GetProjectedOverlapLength(a, b, other.A, other.B);
                if (!CorrectionSouthBoundaryPreference.IsCompanionCoverageAcceptable(overlap, len))
                {
                    continue;
                }

                var signedOffset = ((mid.X - other.A.X) * otherDir.Y - (mid.Y - other.A.Y) * otherDir.X) / otherLen;
                if (Math.Sign(signedOffset) == 0)
                {
                    continue;
                }

                if (Math.Abs(Math.Abs(signedOffset) - CorrectionLinePostInsetMeters) > companionOffsetTol)
                {
                    continue;
                }

                var otherToCandidate = DistanceToInfiniteLine(other.Mid, a, b);
                if (Math.Abs(otherToCandidate - CorrectionLinePostInsetMeters) > companionOffsetTol)
                {
                    continue;
                }

                var s0 = (other.A - a).DotProduct(u);
                var s1 = (other.B - a).DotProduct(u);
                var minS = Math.Max(0.0, Math.Min(s0, s1));
                var maxS = Math.Min(len, Math.Max(s0, s1));
                if (maxS <= minS)
                {
                    continue;
                }

                if (Math.Sign(signedOffset) > 0)
                {
                    positiveCoverageIntervals.Add((minS, maxS));
                }
                else
                {
                    negativeCoverageIntervals.Add((minS, maxS));
                }
            }

            double GetMergedCoverageLength(List<(double Min, double Max)> intervals)
            {
                if (intervals == null || intervals.Count == 0)
                {
                    return 0.0;
                }

                var ordered = intervals
                    .Where(interval => interval.Max > interval.Min)
                    .OrderBy(interval => interval.Min)
                    .ToList();
                if (ordered.Count == 0)
                {
                    return 0.0;
                }

                var total = 0.0;
                var currentMin = ordered[0].Min;
                var currentMax = ordered[0].Max;
                for (var oi = 1; oi < ordered.Count; oi++)
                {
                    var interval = ordered[oi];
                    if (interval.Min <= currentMax + 0.50)
                    {
                        currentMax = Math.Max(currentMax, interval.Max);
                        continue;
                    }

                    total += Math.Max(0.0, currentMax - currentMin);
                    currentMin = interval.Min;
                    currentMax = interval.Max;
                }

                total += Math.Max(0.0, currentMax - currentMin);
                return total;
            }

            var positiveCoverage = GetMergedCoverageLength(positiveCoverageIntervals);
            if (CorrectionSouthBoundaryPreference.IsCompanionCoverageAcceptable(positiveCoverage, len))
            {
                return true;
            }

            var negativeCoverage = GetMergedCoverageLength(negativeCoverageIntervals);
            return CorrectionSouthBoundaryPreference.IsCompanionCoverageAcceptable(negativeCoverage, len);
        }

        private static bool TryRelayerCorrectionSegment(Transaction tr, ObjectId id, string targetLayer)
        {
            if (tr == null || id.IsNull || string.IsNullOrWhiteSpace(targetLayer))
            {
                return false;
            }

            Entity? writable = null;
            try
            {
                writable = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return false;
            }

            if (writable == null || writable.IsErased ||
                string.Equals(writable.Layer, targetLayer, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            writable.Layer = targetLayer;
            writable.ColorIndex = 256;
            return true;
        }

        private sealed class CorrectionSeamAccumulator
        {
            private readonly List<Point2d> _northBoundarySamples = new List<Point2d>();
            private readonly List<Point2d> _southBoundarySamples = new List<Point2d>();

            public CorrectionSeamAccumulator(int zone, string meridian, int range, int northTownship)
            {
                Zone = zone;
                Meridian = meridian;
                Range = range;
                NorthTownship = northTownship;
                MinX = double.MaxValue;
                MaxX = double.MinValue;
            }

            public int Zone { get; }
            public string Meridian { get; }
            public int Range { get; }
            public int NorthTownship { get; }
            public double MinX { get; private set; }
            public double MaxX { get; private set; }
            public int NorthSampleCount => _northBoundarySamples.Count;
            public int SouthSampleCount => _southBoundarySamples.Count;

            public void AddNorthBoundary(Point2d sampleA, Point2d sampleB)
            {
                _northBoundarySamples.Add(sampleA);
                if (sampleB.GetDistanceTo(sampleA) > 1e-6)
                {
                    _northBoundarySamples.Add(sampleB);
                }

                UpdateXRange(
                    System.Math.Min(sampleA.X, sampleB.X),
                    System.Math.Max(sampleA.X, sampleB.X));
            }

            public void AddSouthBoundary(Point2d sampleA, Point2d sampleB)
            {
                _southBoundarySamples.Add(sampleA);
                if (sampleB.GetDistanceTo(sampleA) > 1e-6)
                {
                    _southBoundarySamples.Add(sampleB);
                }

                UpdateXRange(
                    System.Math.Min(sampleA.X, sampleB.X),
                    System.Math.Max(sampleA.X, sampleB.X));
            }

            private void UpdateXRange(double minX, double maxX)
            {
                if (minX < MinX)
                {
                    MinX = minX;
                }

                if (maxX > MaxX)
                {
                    MaxX = maxX;
                }
            }

            public bool TryBuild(out CorrectionSeam seam)
            {
                seam = default;
                if (MinX == double.MaxValue || MaxX == double.MinValue || MaxX - MinX < 4.0)
                {
                    return false;
                }

                var hasNorth = _northBoundarySamples.Count > 0;
                var hasSouth = _southBoundarySamples.Count > 0;
                if (!hasNorth && !hasSouth)
                {
                    return false;
                }

                (double Slope, double Intercept) northFit;
                (double Slope, double Intercept) southFit;
                if (hasNorth && hasSouth)
                {
                    northFit = FitLinearTrend(_northBoundarySamples);
                    southFit = FitLinearTrend(_southBoundarySamples);
                }
                else if (hasNorth)
                {
                    northFit = FitLinearTrend(_northBoundarySamples);
                    southFit = (
                        northFit.Slope,
                        northFit.Intercept - CorrectionLinePostExpectedUsecWidthMeters);
                }
                else
                {
                    southFit = FitLinearTrend(_southBoundarySamples);
                    northFit = (
                        southFit.Slope,
                        southFit.Intercept + CorrectionLinePostExpectedUsecWidthMeters);
                }

                var centerX = 0.5 * (MinX + MaxX);
                var northY = (northFit.Slope * centerX) + northFit.Intercept;
                var southY = (southFit.Slope * centerX) + southFit.Intercept;
                if (northY < southY)
                {
                    var tmp = northY;
                    northY = southY;
                    southY = tmp;

                    var northFitTmp = northFit;
                    northFit = southFit;
                    southFit = northFitTmp;
                }

                var width = northY - southY;
                if (width < 8.0)
                {
                    return false;
                }

                seam = new CorrectionSeam(
                    Zone,
                    Meridian,
                    Range,
                    NorthTownship,
                    !hasNorth || !hasSouth,
                    MinX,
                    MaxX,
                    northY,
                    southY,
                    northFit.Slope,
                    northFit.Intercept,
                    southFit.Slope,
                    southFit.Intercept);
                return true;
            }

            private static (double Slope, double Intercept) FitLinearTrend(IReadOnlyList<Point2d> samples)
            {
                if (samples == null || samples.Count == 0)
                {
                    return (0.0, 0.0);
                }

                if (samples.Count == 1)
                {
                    return (0.0, samples[0].Y);
                }

                var sumX = 0.0;
                var sumY = 0.0;
                for (var i = 0; i < samples.Count; i++)
                {
                    sumX += samples[i].X;
                    sumY += samples[i].Y;
                }

                var meanX = sumX / samples.Count;
                var meanY = sumY / samples.Count;
                var covariance = 0.0;
                var variance = 0.0;
                for (var i = 0; i < samples.Count; i++)
                {
                    var dx = samples[i].X - meanX;
                    covariance += dx * (samples[i].Y - meanY);
                    variance += dx * dx;
                }

                if (variance <= 1e-6)
                {
                    return (0.0, meanY);
                }

                var slope = covariance / variance;
                var intercept = meanY - (slope * meanX);
                return (slope, intercept);
            }
        }

        private readonly struct CorrectionSeam
        {
            public CorrectionSeam(
                int zone,
                string meridian,
                int range,
                int northTownship,
                bool isOneSidedSynthesized,
                double minX,
                double maxX,
                double northY,
                double southY,
                double northSlope,
                double northIntercept,
                double southSlope,
                double southIntercept)
            {
                Zone = zone;
                Meridian = meridian;
                Range = range;
                NorthTownship = northTownship;
                IsOneSidedSynthesized = isOneSidedSynthesized;
                MinX = minX;
                MaxX = maxX;
                NorthY = northY;
                SouthY = southY;
                NorthSlope = northSlope;
                NorthIntercept = northIntercept;
                SouthSlope = southSlope;
                SouthIntercept = southIntercept;
                CenterSlope = 0.5 * (northSlope + southSlope);
                CenterIntercept = 0.5 * (northIntercept + southIntercept);
                CenterY = 0.5 * (northY + southY);
            }

            public int Zone { get; }
            public string Meridian { get; }
            public int Range { get; }
            public int NorthTownship { get; }
            public bool IsOneSidedSynthesized { get; }
            public double MinX { get; }
            public double MaxX { get; }
            public double NorthY { get; }
            public double SouthY { get; }
            public double NorthSlope { get; }
            public double NorthIntercept { get; }
            public double SouthSlope { get; }
            public double SouthIntercept { get; }
            public double CenterSlope { get; }
            public double CenterIntercept { get; }
            public double CenterY { get; }

            public double GetNorthYAt(double x) => (NorthSlope * x) + NorthIntercept;
            public double GetSouthYAt(double x) => (SouthSlope * x) + SouthIntercept;
            public double GetCenterYAt(double x) => 0.5 * (GetNorthYAt(x) + GetSouthYAt(x));
            public double GetNorthSignedOffset(Point2d point) => GetSignedOffset(point, NorthSlope, NorthIntercept);
            public double GetSouthSignedOffset(Point2d point) => GetSignedOffset(point, SouthSlope, SouthIntercept);
            public double GetCenterSignedOffset(Point2d point) => GetSignedOffset(point, CenterSlope, CenterIntercept);
            public (double Min, double Max) GetNorthSignedOffsetRange(CorrectionSegment segment) => GetSignedOffsetRange(segment, NorthSlope, NorthIntercept);
            public (double Min, double Max) GetSouthSignedOffsetRange(CorrectionSegment segment) => GetSignedOffsetRange(segment, SouthSlope, SouthIntercept);
            public (double Min, double Max) GetCenterSignedOffsetRange(CorrectionSegment segment) => GetSignedOffsetRange(segment, CenterSlope, CenterIntercept);

            public bool IntersectsExpandedStrip(CorrectionSegment segment, double tolerance)
            {
                return PerpendicularLineBandMeasurement.IntersectsStrip(
                    new LineDistancePoint(segment.A.X, segment.A.Y),
                    new LineDistancePoint(segment.B.X, segment.B.Y),
                    SouthSlope,
                    SouthIntercept,
                    NorthSlope,
                    NorthIntercept,
                    tolerance);
            }

            private static double GetSignedOffset(Point2d point, double slope, double intercept)
            {
                return PerpendicularLineDistanceMeasurement.SignedDistanceToLine(
                    new LineDistancePoint(point.X, point.Y),
                    slope,
                    intercept);
            }

            private static (double Min, double Max) GetSignedOffsetRange(CorrectionSegment segment, double slope, double intercept)
            {
                return PerpendicularLineBandMeasurement.SignedDistanceRangeToLine(
                    new LineDistancePoint(segment.A.X, segment.A.Y),
                    new LineDistancePoint(segment.B.X, segment.B.Y),
                    slope,
                    intercept);
            }
        }

        private readonly struct CorrectionSegment
        {
            public CorrectionSegment(ObjectId id, string layer, Point2d a, Point2d b)
            {
                Id = id;
                Layer = layer ?? string.Empty;
                A = a;
                B = b;
                Mid = Midpoint(a, b);
                MinX = Math.Min(a.X, b.X);
                MaxX = Math.Max(a.X, b.X);
                MinY = Math.Min(a.Y, b.Y);
                MaxY = Math.Max(a.Y, b.Y);
                Length = a.GetDistanceTo(b);

                var dx = Math.Abs(b.X - a.X);
                var dy = Math.Abs(b.Y - a.Y);
                IsHorizontalLike = dx >= (dy * 1.2);
                IsVerticalLike = dy >= (dx * 1.2);
            }

            public ObjectId Id { get; }
            public string Layer { get; }
            public Point2d A { get; }
            public Point2d B { get; }
            public Point2d Mid { get; }
            public double MinX { get; }
            public double MaxX { get; }
            public double MinY { get; }
            public double MaxY { get; }
            public double Length { get; }
            public bool IsHorizontalLike { get; }
            public bool IsVerticalLike { get; }
            public double MidX => Mid.X;
            public double MidY => Mid.Y;
        }
    }
}
