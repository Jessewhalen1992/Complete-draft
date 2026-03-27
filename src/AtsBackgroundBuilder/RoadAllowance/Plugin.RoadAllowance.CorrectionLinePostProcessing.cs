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
                    if (logger == null || !IsTargetLayerTraceSegment(segment.A, segment.B))
                    {
                        return;
                    }

                    var centerRange = seam.GetCenterSignedOffsetRange(segment);
                    var southRange = seam.GetSouthSignedOffsetRange(segment);
                    var northRange = seam.GetNorthSignedOffsetRange(segment);
                    logger.WriteLine(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "LAYER-TARGET corr stage={0} seam=Z{1} M{2} R{3} T{4}/{5} id={6} layer={7} centerMid={8:0.###} centerRange=[{9:0.###},{10:0.###}] southRange=[{11:0.###},{12:0.###}] northRange=[{13:0.###},{14:0.###}] note={15}",
                            stage,
                            seam.Zone,
                            seam.Meridian,
                            seam.Range,
                            seam.NorthTownship,
                            seam.NorthTownship - 1,
                            segment.Id.Handle.ToString(),
                            segment.Layer,
                            seam.GetCenterSignedOffset(segment.Mid),
                            centerRange.Min,
                            centerRange.Max,
                            southRange.Min,
                            southRange.Max,
                            northRange.Min,
                            northRange.Max,
                            note ?? string.Empty));
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
                    var nearestNorthEdgeDelta = double.MaxValue;
                    var nearestSouthEdgeDelta = double.MaxValue;
                    for (var hi = 0; hi < surveyedHorizontalCandidates.Count; hi++)
                    {
                        var h = surveyedHorizontalCandidates[hi];
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
                        }

                        if (southDelta <= surveyedHorizontalEdgeTol)
                        {
                            surveyedSouthEdgeHits++;
                        }
                    }
                    if (seam.IsOneSidedSynthesized && surveyedNorthEdgeHits == 0 && surveyedSouthEdgeHits == 0)
                    {
                        for (var hi = 0; hi < surveyedHorizontalXOnlyCandidates.Count; hi++)
                        {
                            var h = surveyedHorizontalXOnlyCandidates[hi];
                            var northDelta = Math.Abs(seam.GetNorthSignedOffset(h.Mid));
                            var southDelta = Math.Abs(seam.GetSouthSignedOffset(h.Mid));
                            if (northDelta <= surveyedHorizontalEdgeTol || southDelta <= surveyedHorizontalEdgeTol)
                            {
                                surveyedRelaxedEdgeHits++;
                            }
                        }
                    }

                    var hasSurveyedHorizontalBoundary = surveyedNorthEdgeHits > 0 ||
                                                       surveyedSouthEdgeHits > 0 ||
                                                       surveyedRelaxedEdgeHits > 0;
                    var nearestNorthText = nearestNorthEdgeDelta == double.MaxValue
                        ? "n/a"
                        : nearestNorthEdgeDelta.ToString("0.###", CultureInfo.InvariantCulture);
                    var nearestSouthText = nearestSouthEdgeDelta == double.MaxValue
                        ? "n/a"
                        : nearestSouthEdgeDelta.ToString("0.###", CultureInfo.InvariantCulture);
                    logger?.WriteLine(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "CorrectionLine: seam evidence Z{0} M{1} R{2} T{3}/{4} oneSided={5} vertical(total={6}, surveyed={7}, usec={8}), horizontalSurveyed(xOnly={9}, seamBand={10}, northHits={11}, southHits={12}, relaxedHits={13}, bandTol={14:0.#}, edgeTol={15:0.#}, nearestNorthDelta={16}, nearestSouthDelta={17}).",
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
                            surveyedHorizontalBandTol,
                            surveyedHorizontalEdgeTol,
                            nearestNorthText,
                            nearestSouthText));

                    if (hasSurveyedVertical || hasSurveyedHorizontalBoundary)
                    {
                        surveyedCount++;
                        logger?.WriteLine(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "CorrectionLine: seam Z{0} M{1} R{2} T{3}/{4} classified as L-SEC (surveyed evidence: vertical={5}, northHorizontal={6}, southHorizontal={7}).",
                                seam.Zone,
                                seam.Meridian,
                                seam.Range,
                                seam.NorthTownship,
                                seam.NorthTownship - 1,
                                surveyedVerticalCandidates.Count,
                                surveyedNorthEdgeHits,
                                surveyedSouthEdgeHits));
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

                    foreach (var outer in uniqueOuters)
                    {
                        WriteTargetCorrectionTrace("outer-select", seam, outer, note: "selected");
                        if (TryRelayerCorrectionSegment(tr, outer.Id, LayerUsecCorrection))
                        {
                            relayeredOuter++;
                            correctionGeometryChanged = true;
                            if (outerChangeSamples.Count < 16)
                            {
                                outerChangeSamples.Add(
                                    string.Format(
                                        CultureInfo.InvariantCulture,
                                        "id={0} A=({1:0.###},{2:0.###}) B=({3:0.###},{4:0.###}) -> {5}",
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
                    foreach (var outer in uniqueOuters)
                    {
                        if (TryFindCorrectionInnerCompanion(
                            outer,
                            horizontalCandidates,
                            CorrectionLinePostInsetMeters,
                            point => seam.GetCenterSignedOffset(point),
                            out var existingInner))
                        {
                            if (TryRelayerCorrectionSegment(tr, existingInner.Id, LayerUsecCorrectionZero))
                            {
                                relayeredInner++;
                                correctionGeometryChanged = true;
                                if (innerChangeSamples.Count < 16)
                                {
                                    innerChangeSamples.Add(
                                        string.Format(
                                            CultureInfo.InvariantCulture,
                                            "id={0} A=({1:0.###},{2:0.###}) B=({3:0.###},{4:0.###}) -> {5}",
                                            existingInner.Id.Handle.ToString(),
                                            existingInner.A.X,
                                            existingInner.A.Y,
                                            existingInner.B.X,
                                            existingInner.B.Y,
                                            LayerUsecCorrectionZero));
                                }
                            }

                            continue;
                        }

                        if (TryCreateCorrectionInnerCompanion(
                            outer,
                            point => seam.GetCenterSignedOffset(point),
                            CorrectionLinePostInsetMeters,
                            segments,
                            ms,
                            tr,
                            out var newId,
                            out var newA,
                            out var newB))
                        {
                            createdInner++;
                            correctionGeometryChanged = true;
                            segments.Add(new CorrectionSegment(newId, LayerUsecCorrectionZero, newA, newB));
                            if (createdSamples.Count < 16)
                            {
                                createdSamples.Add(
                                    string.Format(
                                        CultureInfo.InvariantCulture,
                                        "id={0} A=({1:0.###},{2:0.###}) B=({3:0.###},{4:0.###}) layer={5}",
                                        newId.Handle.ToString(),
                                        newA.X,
                                        newA.Y,
                                        newB.X,
                                        newB.Y,
                                        LayerUsecCorrectionZero));
                            }
                        }
                        else
                        {
                            companionNoOp++;
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

                // Deterministic correction cleanup: any horizontal 20.12 segment that sits on a
                // resolved correction seam outer band is reclassified to correction.
                var postRelayeredTwenty = 0;
                var postRelayerSamples = new List<string>();
                var postSeen = new HashSet<ObjectId>();
                var lateOuterCompanionCandidates = new List<(CorrectionSegment Outer, CorrectionSeam Seam)>();
                for (var si = 0; si < seams.Count; si++)
                {
                    var seam = seams[si];
                    for (var i = 0; i < segments.Count; i++)
                    {
                        var seg = segments[i];
                        var centerOffset = seam.GetCenterSignedOffset(seg.Mid);
                        var preferSouthSide = centerOffset < 0.0;
                        var isFallbackLayer = IsCorrectionOuterFallbackLayerName(seg.Layer, preferSouthSide);
                        WriteTargetCorrectionTrace(
                            "late-post-candidate",
                            seam,
                            seg,
                            note: string.Format(
                                CultureInfo.InvariantCulture,
                                "horizontal={0} southSide={1} fallbackLayer={2}",
                                seg.IsHorizontalLike,
                                preferSouthSide,
                                isFallbackLayer));
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

                        if (!postSeen.Add(seg.Id))
                        {
                            continue;
                        }

                        if (!TryRelayerCorrectionSegment(tr, seg.Id, LayerUsecCorrection))
                        {
                            continue;
                        }

                        WriteTargetCorrectionTrace("late-post-relayer", seam, seg, note: "relayered-to-correction");
                        postRelayeredTwenty++;
                        correctionGeometryChanged = true;
                        lateOuterCompanionCandidates.Add((new CorrectionSegment(seg.Id, LayerUsecCorrection, seg.A, seg.B), seam));
                        if (postRelayerSamples.Count < 12)
                        {
                            postRelayerSamples.Add(
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "id={0} A=({1:0.###},{2:0.###}) B=({3:0.###},{4:0.###}) -> {5}",
                                    seg.Id.Handle.ToString(),
                                    seg.A.X,
                                    seg.A.Y,
                                    seg.B.X,
                                    seg.B.Y,
                                    LayerUsecCorrection));
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

                        if (!TryRelayerCorrectionSegment(tr, id, LayerUsecCorrection))
                        {
                            continue;
                        }

                        correctionOuterSegments.Add((id, a, b, u));
                        bridgeChanged = true;
                        bridgeRelayered++;
                        correctionGeometryChanged = true;
                        if (bridgeSamples.Count < 12)
                        {
                            bridgeSamples.Add(
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "id={0} A=({1:0.###},{2:0.###}) B=({3:0.###},{4:0.###}) -> {5}",
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
                    if (TryFindCorrectionInnerCompanion(
                        outer,
                        segments,
                        CorrectionLinePostInsetMeters,
                        point => seam.GetCenterSignedOffset(point),
                        out var existingInner))
                    {
                        if (TryRelayerCorrectionSegment(tr, existingInner.Id, LayerUsecCorrectionZero))
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
                                        existingInner.Id.Handle.ToString(),
                                        existingInner.A.X,
                                        existingInner.A.Y,
                                        existingInner.B.X,
                                        existingInner.B.Y,
                                        LayerUsecCorrectionZero));
                            }
                        }

                        continue;
                    }

                    if (TryCreateCorrectionInnerCompanion(
                        outer,
                        point => seam.GetCenterSignedOffset(point),
                        CorrectionLinePostInsetMeters,
                        segments,
                        ms,
                        tr,
                        out var newId,
                        out var newA,
                        out var newB))
                    {
                        createdInner++;
                        lateCompanionCreated++;
                        correctionGeometryChanged = true;
                        segments.Add(new CorrectionSegment(newId, LayerUsecCorrectionZero, newA, newB));
                        if (lateCompanionSamples.Count < 10)
                        {
                            lateCompanionSamples.Add(
                                string.Format(
                                    CultureInfo.InvariantCulture,
                                    "create id={0} A=({1:0.###},{2:0.###}) B=({3:0.###},{4:0.###}) layer={5}",
                                    newId.Handle.ToString(),
                                    newA.X,
                                    newA.Y,
                                    newB.X,
                                    newB.Y,
                                    LayerUsecCorrectionZero));
                        }
                    }
                    else
                    {
                        lateCompanionNoOp++;
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
                var liveCorrectionZeroSegments = new List<CorrectionSegment>();
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
                        !string.Equals(ent.Layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!TryReadOpenLinearSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    var candidate = new CorrectionSegment(id, LayerUsecCorrectionZero, a, b);
                    if (!candidate.IsHorizontalLike)
                    {
                        continue;
                    }

                    liveCorrectionZeroSegments.Add(candidate);
                }

                var normalizedOuterZero = 0;
                var normalizedOuterZeroCandidates = 0;
                var normalizedOuterZeroCompanionCreated = 0;
                var normalizedOuterZeroCompanionNoOp = 0;
                var normalizedOuterZeroSamples = new List<string>();
                for (var si = 0; si < seams.Count; si++)
                {
                    var seam = seams[si];
                    for (var i = 0; i < liveCorrectionZeroSegments.Count; i++)
                    {
                        var seg = liveCorrectionZeroSegments[i];
                        if (seg.MaxX < seam.MinX - 25.0 || seg.MinX > seam.MaxX + 25.0)
                        {
                            continue;
                        }

                        if (GetCorrectionHorizontalOverlap(seg, seam.MinX, seam.MaxX) < -30.0)
                        {
                            continue;
                        }

                        if (!seam.IntersectsExpandedStrip(seg, 14.0))
                        {
                            continue;
                        }

                        var centerDistance = Math.Abs(seam.GetCenterSignedOffset(seg.Mid));
                        normalizedOuterZeroCandidates++;
                        if (!CorrectionBandAxisClassifier.IsCloserToOuterBand(
                                centerDistance,
                                CorrectionLinePostExpectedUsecWidthMeters,
                                CorrectionLinePostInsetMeters))
                        {
                            continue;
                        }

                        if (!TryRelayerCorrectionSegment(tr, seg.Id, LayerUsecCorrection))
                        {
                            continue;
                        }

                        var normalizedOuter = new CorrectionSegment(seg.Id, LayerUsecCorrection, seg.A, seg.B);
                        for (var existingIndex = 0; existingIndex < segments.Count; existingIndex++)
                        {
                            if (segments[existingIndex].Id != seg.Id)
                            {
                                continue;
                            }

                            segments[existingIndex] = normalizedOuter;
                            break;
                        }

                        if (TryFindCorrectionInnerCompanion(
                                normalizedOuter,
                                segments,
                                CorrectionLinePostInsetMeters,
                                point => seam.GetCenterSignedOffset(point),
                                out var existingInner))
                        {
                            if (TryRelayerCorrectionSegment(tr, existingInner.Id, LayerUsecCorrectionZero))
                            {
                                correctionGeometryChanged = true;
                            }

                            normalizedOuterZeroCompanionNoOp++;
                        }
                        else if (TryCreateCorrectionInnerCompanion(
                                     normalizedOuter,
                                     point => seam.GetCenterSignedOffset(point),
                                     CorrectionLinePostInsetMeters,
                                     segments,
                                     ms,
                                     tr,
                                     out var companionId,
                                     out var companionA,
                                     out var companionB))
                        {
                            normalizedOuterZeroCompanionCreated++;
                            correctionGeometryChanged = true;
                            segments.Add(new CorrectionSegment(companionId, LayerUsecCorrectionZero, companionA, companionB));
                        }
                        else
                        {
                            normalizedOuterZeroCompanionNoOp++;
                        }

                        normalizedOuterZero++;
                        correctionGeometryChanged = true;
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
                    $"CorrectionLine: outer-axis correction-zero normalization scanned {liveCorrectionZeroSegments.Count} live C-0 horizontal(s), seamCandidates={normalizedOuterZeroCandidates}, relayered={normalizedOuterZero}.");
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

                var liveCorrectionOuterSegments = new List<CorrectionSegment>();
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

                    if (!TryReadOpenLinearSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    var candidate = new CorrectionSegment(id, LayerUsecCorrection, a, b);
                    if (!candidate.IsHorizontalLike)
                    {
                        continue;
                    }

                    liveCorrectionOuterSegments.Add(candidate);
                }

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

                        if (seg.MaxX < seam.MinX - 25.0 || seg.MinX > seam.MaxX + 25.0)
                        {
                            continue;
                        }

                        if (GetCorrectionHorizontalOverlap(seg, seam.MinX, seam.MaxX) < -30.0)
                        {
                            continue;
                        }

                        if (!seam.IntersectsExpandedStrip(seg, 14.0))
                        {
                            continue;
                        }

                        var centerDistance = Math.Abs(seam.GetCenterSignedOffset(seg.Mid));
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

                        if (!TryRelayerCorrectionSegment(tr, seg.Id, LayerUsecCorrectionZero))
                        {
                            continue;
                        }

                        normalizedInsetOuterIds.Add(seg.Id);
                        correctionGeometryChanged = true;
                        normalizedInsetOuter++;
                        var normalizedInner = new CorrectionSegment(seg.Id, LayerUsecCorrectionZero, seg.A, seg.B);
                        for (var existingIndex = 0; existingIndex < segments.Count; existingIndex++)
                        {
                            if (segments[existingIndex].Id != seg.Id)
                            {
                                continue;
                            }

                            segments[existingIndex] = normalizedInner;
                            break;
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
                    $"CorrectionLine: seams={seamCount}, surveyed={surveyedCount}, unsurveyed={unsurveyedCount}, relayerOuter={relayeredOuter}, relayerInner={relayeredInner}, createdInner={createdInner}.");
                if (outerChangeSamples.Count > 0)
                {
                    logger?.WriteLine($"CorrectionLine: outer relayer samples ({outerChangeSamples.Count})");
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

            bool IsHardVerticalTieInTargetLayer(string layer)
            {
                return IsOrdinaryUsecTieInLayer(layer) ||
                       string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-SEC-0", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase);
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
                var verticalTargets = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, double MinY, double MaxY, double AxisX)>();
                var horizontalTargets = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, double MinX, double MaxX, double AxisY)>();
                var horizontalAnchors = new List<(ObjectId Id, Point2d A, Point2d B)>();
                var verticalAnchors = new List<(ObjectId Id, Point2d A, Point2d B)>();
                var correctionZeroHorizontalTargets = new List<(LineDistancePoint A, LineDistancePoint B)>();
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
                    if (!IsOrdinaryUsecTieInLayer(layer) &&
                        !IsHardVerticalTieInTargetLayer(layer) &&
                        !IsOrdinaryUsecOuterLayer(layer))
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b) || !DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    if (IsOrdinaryUsecTieInLayer(layer) && IsHorizontalLike(a, b))
                    {
                        horizontalSources.Add((id, layer, a, b));
                    }
                    else if (IsOrdinaryUsecTieInLayer(layer) && IsVerticalLike(a, b))
                    {
                        verticalSources.Add((id, layer, a, b));
                    }
                    else if (IsHardVerticalTieInTargetLayer(layer) && IsVerticalLike(a, b))
                    {
                        verticalTargets.Add((id, layer, a, b, Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y), 0.5 * (a.X + b.X)));
                    }
                    else if (IsHardVerticalTieInTargetLayer(layer) && IsHorizontalLike(a, b))
                    {
                        if (string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase))
                        {
                            correctionZeroHorizontalTargets.Add((
                                new LineDistancePoint(a.X, a.Y),
                                new LineDistancePoint(b.X, b.Y)));
                        }

                        horizontalTargets.Add((id, layer, a, b, Math.Min(a.X, b.X), Math.Max(a.X, b.X), 0.5 * (a.Y + b.Y)));
                    }
                    else if (IsOrdinaryUsecOuterLayer(layer) && IsHorizontalLike(a, b))
                    {
                        horizontalAnchors.Add((id, a, b));
                    }
                    else if (IsOrdinaryUsecOuterLayer(layer) && IsVerticalLike(a, b))
                    {
                        verticalAnchors.Add((id, a, b));
                    }
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

                var ghostHorizontalRowsIgnored = 0;
                if (correctionZeroHorizontalTargets.Count > 0)
                {
                    var ghostCandidates = new List<(ObjectId Id, LineDistancePoint A, LineDistancePoint B)>();
                    for (var i = 0; i < horizontalSources.Count; i++)
                    {
                        var source = horizontalSources[i];
                        ghostCandidates.Add((
                            source.Id,
                            new LineDistancePoint(source.A.X, source.A.Y),
                            new LineDistancePoint(source.B.X, source.B.Y)));
                    }

                    for (var i = 0; i < horizontalAnchors.Count; i++)
                    {
                        var anchor = horizontalAnchors[i];
                        ghostCandidates.Add((
                            anchor.Id,
                            new LineDistancePoint(anchor.A.X, anchor.A.Y),
                            new LineDistancePoint(anchor.B.X, anchor.B.Y)));
                    }

                    for (var i = 0; i < horizontalTargets.Count; i++)
                    {
                        var target = horizontalTargets[i];
                        if (!IsOrdinaryUsecTieInLayer(target.Layer) && !IsOrdinaryUsecOuterLayer(target.Layer))
                        {
                            continue;
                        }

                        ghostCandidates.Add((
                            target.Id,
                            new LineDistancePoint(target.A.X, target.A.Y),
                            new LineDistancePoint(target.B.X, target.B.Y)));
                    }

                    var ghostCandidateIndices = CorrectionInsetGhostRowClassifier.FindGhostChainIndices(
                        ghostCandidates.Select(candidate => (candidate.A, candidate.B)).ToList(),
                        correctionZeroHorizontalTargets,
                        CorrectionLinePostInsetMeters);
                    if (ghostCandidateIndices.Count > 0)
                    {
                        var ghostHorizontalIds = new HashSet<ObjectId>();
                        foreach (var ghostCandidateIndex in ghostCandidateIndices)
                        {
                            ghostHorizontalIds.Add(ghostCandidates[ghostCandidateIndex].Id);
                        }

                        ghostHorizontalRowsIgnored += ghostHorizontalIds.Count;
                        horizontalSources = horizontalSources
                            .Where(source => !ghostHorizontalIds.Contains(source.Id))
                            .ToList();
                        horizontalAnchors = horizontalAnchors
                            .Where(anchor => !ghostHorizontalIds.Contains(anchor.Id))
                            .ToList();
                        horizontalTargets = horizontalTargets
                            .Where(target => !ghostHorizontalIds.Contains(target.Id))
                            .ToList();
                    }
                }

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
                const double axisTol = 0.35;
                const double spanTol = 0.50;
                const double outerBoundaryTol = 0.40;
                var trimmed = 0;
                var scanned = 0;

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

                    if (string.Equals(targetLayer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(targetLayer, "L-SEC-0", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(targetLayer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase))
                    {
                        return 2;
                    }

                    if (AreMatchingTieInBands(sourceLayer, targetLayer))
                    {
                        return 3;
                    }

                    return 4;
                }

                bool EndpointTouchesHardAnchorLayer(string layer)
                {
                    return string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(layer, "L-SEC-0", StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(layer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase);
                }

                (bool Connected, bool HardConnected) GetHorizontalEndpointAnchorState(Point2d endpoint, string sourceLayer, ObjectId sourceId)
                {
                    for (var i = 0; i < horizontalSources.Count; i++)
                    {
                        var other = horizontalSources[i];
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

                    for (var i = 0; i < verticalTargets.Count; i++)
                    {
                        var target = verticalTargets[i];
                        if (!AreMatchingTieInBands(sourceLayer, target.Layer) &&
                            !string.Equals(target.Layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(target.Layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(target.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(target.Layer, "L-SEC-0", StringComparison.OrdinalIgnoreCase) &&
                            !string.Equals(target.Layer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (DistancePointToSegment(endpoint, target.A, target.B) <= endpointTouchTol)
                        {
                            return (true, EndpointTouchesHardAnchorLayer(target.Layer));
                        }
                    }

                    for (var i = 0; i < verticalAnchors.Count; i++)
                    {
                        var anchor = verticalAnchors[i];
                        if (DistancePointToSegment(endpoint, anchor.A, anchor.B) <= endpointTouchTol)
                        {
                            return (true, false);
                        }
                    }

                    return (false, false);
                }

                (bool Connected, bool HardConnected) GetVerticalEndpointAnchorState(Point2d endpoint, string sourceLayer, ObjectId sourceId)
                {
                    for (var i = 0; i < verticalSources.Count; i++)
                    {
                        var other = verticalSources[i];
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

                    for (var i = 0; i < horizontalTargets.Count; i++)
                    {
                        var target = horizontalTargets[i];
                        if (!AreMatchingTieInBands(sourceLayer, target.Layer) &&
                            !EndpointTouchesHardAnchorLayer(target.Layer))
                        {
                            continue;
                        }

                        if (DistancePointToSegment(endpoint, target.A, target.B) <= endpointTouchTol)
                        {
                            return (true, EndpointTouchesHardAnchorLayer(target.Layer));
                        }
                    }

                    for (var i = 0; i < horizontalAnchors.Count; i++)
                    {
                        var anchor = horizontalAnchors[i];
                        if (DistancePointToSegment(endpoint, anchor.A, anchor.B) <= endpointTouchTol)
                        {
                            return (true, false);
                        }
                    }

                    return (false, false);
                }

                for (var iteration = 0; iteration < 4; iteration++)
                {
                    var movedAny = false;
                    for (var si = 0; si < horizontalSources.Count; si++)
                    {
                        var source = horizontalSources[si];
                        if (!(tr.GetObject(source.Id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(writable, out var a, out var b) || !IsHorizontalLike(a, b))
                        {
                            continue;
                        }

                        horizontalSources[si] = (source.Id, source.Layer, a, b);
                        var startAnchorState = GetHorizontalEndpointAnchorState(a, source.Layer, source.Id);
                        var endAnchorState = GetHorizontalEndpointAnchorState(b, source.Layer, source.Id);
                        var startOnBoundary = IsPointOnAnyWindowBoundaryForPlugin(a, outerBoundaryTol, clipWindows);
                        var endOnBoundary = IsPointOnAnyWindowBoundaryForPlugin(b, outerBoundaryTol, clipWindows);
                        var canTrimStart = !startAnchorState.HardConnected && !startOnBoundary && (endAnchorState.Connected || endOnBoundary);
                        var canTrimEnd = !endAnchorState.HardConnected && !endOnBoundary && (startAnchorState.Connected || startOnBoundary);
                        if (!canTrimStart && !canTrimEnd)
                        {
                            continue;
                        }

                        if (canTrimStart)
                        {
                            scanned++;
                        }

                        if (canTrimEnd)
                        {
                            scanned++;
                        }

                        var bestFound = false;
                        var bestMoveStart = false;
                        var bestMoveDistance = double.MaxValue;
                        var bestTargetPriority = int.MaxValue;
                        var bestTarget = default(Point2d);
                        for (var ti = 0; ti < verticalTargets.Count; ti++)
                        {
                            var target = verticalTargets[ti];
                            var isMatchingBand = AreMatchingTieInBands(source.Layer, target.Layer);
                            var isOrdinaryTieInTarget = IsOrdinaryUsecTieInLayer(target.Layer);
                            var isHardBoundaryTarget =
                                string.Equals(target.Layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(target.Layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(target.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(target.Layer, "L-SEC-0", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(target.Layer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase);
                            if (!isMatchingBand && !isHardBoundaryTarget && !isOrdinaryTieInTarget)
                            {
                                continue;
                            }

                            if (!TryIntersectInfiniteLines(a, b, target.A, target.B, out var intersection))
                            {
                                continue;
                            }

                            if (DistancePointToSegment(intersection, target.A, target.B) > spanTol)
                            {
                                continue;
                            }

                            // Tie-in overhang trim should only shorten the live source span.
                            if (DistancePointToSegment(intersection, a, b) > spanTol)
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

                            var targetPriority = GetTieInTargetPriority(source.Layer, target.Layer);
                            if (!bestFound ||
                                targetPriority < bestTargetPriority ||
                                (targetPriority == bestTargetPriority && moveDistance < bestMoveDistance - 1e-6))
                            {
                                bestFound = true;
                                bestMoveStart = moveStart;
                                bestMoveDistance = moveDistance;
                                bestTargetPriority = targetPriority;
                                bestTarget = intersection;
                            }
                        }

                        if (!bestFound || !TryMoveEndpoint(writable, bestMoveStart, bestTarget, endpointMoveTol))
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(writable, out var newA, out var newB))
                        {
                            continue;
                        }

                        horizontalSources[si] = (source.Id, source.Layer, newA, newB);
                        trimmed++;
                        movedAny = true;
                    }

                    for (var si = 0; si < verticalSources.Count; si++)
                    {
                        var source = verticalSources[si];
                        if (!(tr.GetObject(source.Id, OpenMode.ForWrite, false) is Entity writable) || writable.IsErased)
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(writable, out var a, out var b) || !IsVerticalLike(a, b))
                        {
                            continue;
                        }

                        verticalSources[si] = (source.Id, source.Layer, a, b);
                        var startAnchorState = GetVerticalEndpointAnchorState(a, source.Layer, source.Id);
                        var endAnchorState = GetVerticalEndpointAnchorState(b, source.Layer, source.Id);
                        var startOnBoundary = IsPointOnAnyWindowBoundaryForPlugin(a, outerBoundaryTol, clipWindows);
                        var endOnBoundary = IsPointOnAnyWindowBoundaryForPlugin(b, outerBoundaryTol, clipWindows);
                        var canTrimStart = !startAnchorState.HardConnected && !startOnBoundary && (endAnchorState.Connected || endOnBoundary);
                        var canTrimEnd = !endAnchorState.HardConnected && !endOnBoundary && (startAnchorState.Connected || startOnBoundary);
                        if (!canTrimStart && !canTrimEnd)
                        {
                            continue;
                        }

                        if (canTrimStart)
                        {
                            scanned++;
                        }

                        if (canTrimEnd)
                        {
                            scanned++;
                        }

                        var bestFound = false;
                        var bestMoveStart = false;
                        var bestMoveDistance = double.MaxValue;
                        var bestTargetPriority = int.MaxValue;
                        var bestTarget = default(Point2d);
                        for (var ti = 0; ti < horizontalTargets.Count; ti++)
                        {
                            var target = horizontalTargets[ti];
                            var isMatchingBand = AreMatchingTieInBands(source.Layer, target.Layer);
                            var isOrdinaryTieInTarget = IsOrdinaryUsecTieInLayer(target.Layer);
                            var isHardBoundaryTarget = EndpointTouchesHardAnchorLayer(target.Layer);
                            if (!isMatchingBand && !isHardBoundaryTarget && !isOrdinaryTieInTarget)
                            {
                                continue;
                            }

                            if (!TryIntersectInfiniteLines(a, b, target.A, target.B, out var intersection))
                            {
                                continue;
                            }

                            if (DistancePointToSegment(intersection, target.A, target.B) > spanTol)
                            {
                                continue;
                            }

                            // Tie-in overhang trim should only shorten the live source span.
                            if (DistancePointToSegment(intersection, a, b) > spanTol)
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

                            var targetPriority = GetTieInTargetPriority(source.Layer, target.Layer);
                            if (!bestFound ||
                                targetPriority < bestTargetPriority ||
                                (targetPriority == bestTargetPriority && moveDistance < bestMoveDistance - 1e-6))
                            {
                                bestFound = true;
                                bestMoveStart = moveStart;
                                bestMoveDistance = moveDistance;
                                bestTargetPriority = targetPriority;
                                bestTarget = intersection;
                            }
                        }

                        if (!bestFound || !TryMoveEndpoint(writable, bestMoveStart, bestTarget, endpointMoveTol))
                        {
                            continue;
                        }

                        if (!TryReadOpenSegment(writable, out var newA, out var newB))
                        {
                            continue;
                        }

                        verticalSources[si] = (source.Id, source.Layer, newA, newB);
                        trimmed++;
                        movedAny = true;
                    }

                    if (!movedAny)
                    {
                        break;
                    }
                }

                tr.Commit();
                logger?.WriteLine($"CorrectionLine: ordinary USEC tie-in overhang trim scanned={scanned}, trimmed={trimmed}, horizontalSources={horizontalSources.Count}, verticalSources={verticalSources.Count}, verticalTargets={verticalTargets.Count}, horizontalTargets={horizontalTargets.Count}, horizontalAnchors={horizontalAnchors.Count}, verticalAnchors={verticalAnchors.Count}, ghostHorizontalRowsIgnored={ghostHorizontalRowsIgnored}.");
                return trimmed > 0;
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
                    var isCorrectionLayer =
                        string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase);
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
                var liveSegments = new List<CorrectionSegment>();
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
                    var isCorrectionLayer =
                        string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase);
                    if (!isCorrectionLayer || !TryReadOpenLinearSegment(ent, out var a, out var b))
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

                var erasedIds = new HashSet<ObjectId>();
                void UpdateLiveSegmentLayer(ObjectId id, string layer)
                {
                    for (var i = 0; i < liveSegments.Count; i++)
                    {
                        if (liveSegments[i].Id == id)
                        {
                            liveSegments[i] = new CorrectionSegment(id, layer, liveSegments[i].A, liveSegments[i].B);
                            return;
                        }
                    }
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
                            for (var ci = 0; ci < cluster.Count; ci++)
                            {
                                var idx = cluster[ci];
                                var seg = seamCandidates[idx];
                                var centerDistance = Math.Abs(seam.GetCenterSignedOffset(seg.Mid));
                                var outerScore = Math.Abs(centerDistance - outerExpected);
                                var innerScore = Math.Abs(centerDistance - innerExpected);
                                if (outerScore <= bandTol && outerScore < bestOuterScore - 1e-6)
                                {
                                    bestOuter = idx;
                                    bestOuterScore = outerScore;
                                }

                                if (innerScore <= bandTol && innerScore < bestInnerScore - 1e-6)
                                {
                                    bestInner = idx;
                                    bestInnerScore = innerScore;
                                }
                            }

                            if (bestOuter < 0)
                            {
                                for (var ci = 0; ci < cluster.Count; ci++)
                                {
                                    var idx = cluster[ci];
                                    var seg = seamCandidates[idx];
                                    var outerScore = Math.Abs(Math.Abs(seam.GetCenterSignedOffset(seg.Mid)) - outerExpected);
                                    if (outerScore < bestOuterScore - 1e-6)
                                    {
                                        bestOuter = idx;
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
                                    var innerScore = Math.Abs(Math.Abs(seam.GetCenterSignedOffset(seg.Mid)) - innerExpected);
                                    if (innerScore < bestInnerScore - 1e-6)
                                    {
                                        bestInner = idx;
                                        bestInnerScore = innerScore;
                                    }
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

            var clipWindows = MergeOverlappingClipWindows(BuildBufferedQuarterWindows(database, requestedQuarterIds, 100.0));
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
                var twentyCandidates = new List<(ObjectId Id, Point2d A, Point2d B)>();
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

                    if (!TryReadOpenLinearSegment(ent, out var a, out var b) || !IsHorizontalLike(a, b))
                    {
                        continue;
                    }

                    if (!DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
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
                        string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, "L-SEC-0", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(layer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase);
                    if (isCorrectionPromotableLayer)
                    {
                        twentyCandidates.Add((id, a, b));
                    }
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

                    var offsetDelta = Math.Abs(Math.Abs(signedOffset) - CorrectionLinePostInsetMeters);
                    if (offsetDelta > duplicateOffsetTol)
                    {
                        return false;
                    }

                    var reverseOffset = Math.Abs(DistancePointToInfiniteLine(inner.Mid, a, b));
                    return Math.Abs(reverseOffset - CorrectionLinePostInsetMeters) <= duplicateOffsetTol;
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
                        if (!TryFindInsetDuplicateInnerAnchor(
                                candidate.A,
                                candidate.B,
                                requireProjectedOverlap: true,
                                out var innerIndex,
                                out var offsetSign))
                        {
                            continue;
                        }

                        var startTouchesChain = EndpointTouchesCorrectionChain(candidate.A, correctionChain);
                        var endTouchesChain = EndpointTouchesCorrectionChain(candidate.B, correctionChain);
                        if (CorrectionOuterConsistencyPromotionPolicy.ShouldPromoteSegment(
                                startTouchesChain,
                                endTouchesChain,
                                hasParallelInsetCompanion: true))
                        {
                            continue;
                        }

                        if (suppressedIds.Add(candidate.Id))
                        {
                            ordinaryInsetDuplicateSeeded++;
                            suppressedCandidates.Enqueue((ci, innerIndex, offsetSign));
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
                        }
                    }
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

                            writable.Layer = LayerUsecCorrection;
                            writable.ColorIndex = 256;
                            converted++;
                            correctionChain.Add((candidate.A, candidate.B));
                            twentyCandidates.RemoveAt(ci);
                            progress = true;
                        }
                    }
                }

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
                       string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-SEC-0", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase);
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

                if (correctionInnerSources.Count == 0 || (verticalTargets.Count == 0 && correctionOuterSegments.Count == 0))
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
                var sampleMoves = new List<string>();
                var verticalTargetBestSnapByEndpoint = new Dictionary<(ObjectId TargetId, bool MoveStart), (Point2d OriginalEndpoint, double BestDistanceFromOriginal)>();

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

                bool TryAdjustVerticalTargetEndpointToPoint(
                    int targetIndex,
                    Point2d connectionPoint,
                    int preferredDirectionSign)
                {
                    if (targetIndex < 0 || targetIndex >= verticalTargets.Count)
                    {
                        return false;
                    }

                    var target = verticalTargets[targetIndex];
                    if (DistancePointToSegment(connectionPoint, target.A, target.B) <= endpointTouchTol)
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

                    if (DistancePointToSegment(connectionPoint, currentA, currentB) <= endpointTouchTol)
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
                        (string.Equals(targetLayer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(targetLayer, "L-SEC-0", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(targetLayer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase)) &&
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

                    if (!TryMoveEndpoint(targetWritable, moveStart, connectionPoint, endpointMoveTol))
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

                    if (TryAdjustVerticalTargetEndpointToPoint(targetIndex, connectionPoint, directionSign))
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
                            string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
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

                tr.Commit();
                logger?.WriteLine(
                    $"CorrectionLine: C-0 endpoint snap scanned={scannedEndpoints}, movedEndpoints={movedEndpoints}, movedHorizontal={movedHorizontalEndpoints}, movedVertical={movedVerticalEndpoints}, movedLines={movedLines}, alreadyConnected={alreadyConnected}, sharedSkipped={sharedEndpointSkipped}, noDirectionResolved={noDirectionResolved}, noTargetFound={noTargetFound}, maxMove={maxMove:0.##}.");
                logger?.WriteLine(
                    $"CorrectionLine: C-0 split at boundaries sources={splitInnerSources}, created={splitInnerCreated}, outerAnchors={splitInnerFromOuterAnchors}.");
                logger?.WriteLine(
                    $"CorrectionLine: C split at vertical boundaries sources={splitOuterSources}, created={splitOuterCreated}.");
                logger?.WriteLine(
                    $"CorrectionLine: forced correction outer relayer converted {forcedOuterRelayer} seam-overlap L-USEC20/3018 segment(s) to {LayerUsecCorrection} (anchors={correctionOuterAnchors.Count}).");
                if (sampleMoves.Count > 0)
                {
                    logger?.WriteLine($"CorrectionLine: C-0 endpoint snap samples ({sampleMoves.Count})");
                    for (var i = 0; i < sampleMoves.Count; i++)
                    {
                        logger?.WriteLine("CorrectionLine:   " + sampleMoves[i]);
                    }
                }

                return movedEndpoints > 0 || splitInnerCreated > 0 || splitOuterCreated > 0;
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
                   string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase);
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
                   string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase);
        }

        private static double GetCorrectionHorizontalOverlap(CorrectionSegment segment, double minX, double maxX)
        {
            var overlapMin = Math.Max(segment.MinX, minX);
            var overlapMax = Math.Min(segment.MaxX, maxX);
            return overlapMax - overlapMin;
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

            var inStrictBand = candidates
                .Where(c => IsOnExpectedSide(c) && Math.Abs(targetSignedOffset(c.Mid)) <= strictTol)
                .ToList();
            if (inStrictBand.Count > 0)
            {
                return inStrictBand;
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
            return candidates
                .Where(c => IsOnExpectedSide(c) &&
                            Math.Abs(Math.Abs(targetSignedOffset(c.Mid)) - bestResidual) <= fallbackBandTol)
                .ToList();
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
            }

            return false;
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
