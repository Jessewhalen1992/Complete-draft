/////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

            bool IsOnCorrectionCadence(int township, int anchorTownship)
            {
                var mod = (township - anchorTownship) % 4;
                if (mod < 0)
                {
                    mod += 4;
                }

                return mod == 0;
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
                    var seamY = isNorthSeamSection ? anchors.Bottom.Y : anchors.Top.Y;
                    if (isNorthSeamSection)
                    {
                        accumulator.AddNorthBoundary(seamY, minX, maxX);
                    }
                    else
                    {
                        accumulator.AddSouthBoundary(seamY, minX, maxX);
                    }
                }

                var seams = new List<CorrectionSeam>();
                foreach (var accumulator in seamAccumulators.Values)
                {
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

                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var segments = new List<CorrectionSegment>();
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

                    if (!TryReadOpenLinearSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    var segment = new CorrectionSegment(id, layer, a, b);
                    if (!IntersectsAnyCorrectionSeamWindow(segment, seams))
                    {
                        continue;
                    }

                    segments.Add(segment);
                }

                if (segments.Count == 0)
                {
                    tr.Commit();
                    return;
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
                    logger?.WriteLine(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "CorrectionLine: seam vertical evidence Z{0} M{1} R{2} T{3}/{4} total={5}, surveyed={6}, usec={7}.",
                            seam.Zone,
                            seam.Meridian,
                            seam.Range,
                            seam.NorthTownship,
                            seam.NorthTownship - 1,
                            verticalCandidates.Count,
                            surveyedVerticalCandidates.Count,
                            usecVerticalCandidates.Count));

                    if (hasSurveyedVertical)
                    {
                        surveyedCount++;
                        logger?.WriteLine(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "CorrectionLine: seam Z{0} M{1} R{2} T{3}/{4} classified as L-SEC (surveyed vertical RA found).",
                                seam.Zone,
                                seam.Meridian,
                                seam.Range,
                                seam.NorthTownship,
                                seam.NorthTownship - 1));
                        continue;
                    }

                    if (!hasUsecVertical)
                    {
                        logger?.WriteLine(
                            string.Format(
                                CultureInfo.InvariantCulture,
                                "CorrectionLine: seam Z{0} M{1} R{2} T{3}/{4} skipped (no vertical RA evidence in seam band).",
                                seam.Zone,
                                seam.Meridian,
                                seam.Range,
                                seam.NorthTownship,
                                seam.NorthTownship - 1));
                        continue;
                    }

                    unsurveyedCount++;
                    const double seamEdgeExtensionTolerance = 30.0;
                    var horizontalCandidates = segments
                        .Where(s => s.IsHorizontalLike &&
                                    s.MaxX >= seam.MinX - 25.0 &&
                                    s.MinX <= seam.MaxX + 25.0 &&
                                    s.MidY >= seam.GetSouthYAt(s.MidX) - 12.0 &&
                                    s.MidY <= seam.GetNorthYAt(s.MidX) + 12.0 &&
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
                        x => seam.GetNorthYAt(x),
                        x => seam.GetCenterYAt(x),
                        preferAboveCenter: true,
                        strictTol: 2.4);
                    var southOuter = SelectCorrectionHorizontalBand(
                        horizontalCandidates,
                        x => seam.GetSouthYAt(x),
                        x => seam.GetCenterYAt(x),
                        preferAboveCenter: false,
                        strictTol: 2.4);
                    if (northOuter.Count == 0 || southOuter.Count == 0)
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

                    var skippedInnerLayerOuter = 0;
                    var uniqueOuters = northOuter
                        .Concat(southOuter)
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
                            x => seam.GetCenterYAt(x),
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
                            x => seam.GetCenterYAt(x),
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
                var correctionEndpointAdjusted = ConnectCorrectionInnerEndpointsToVerticalUsecBoundaries(
                    database,
                    requestedScopeIds,
                    logger);
                if (correctionEndpointAdjusted)
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
            EnforceBlindLineEndpointsOnSectionBoundaries(database, requestedScopeIds, logger);
            if (drawLsds)
            {
                EnforceLsdLineEndpointsOnHardSectionBoundaries(database, requestedScopeIds, logger);
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
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
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

            bool IsPointInAnyWindow(Point2d p)
            {
                for (var i = 0; i < clipWindows.Count; i++)
                {
                    var w = clipWindows[i];
                    if (p.X >= w.MinPoint.X && p.X <= w.MaxPoint.X &&
                        p.Y >= w.MinPoint.Y && p.Y <= w.MaxPoint.Y)
                    {
                        return true;
                    }
                }

                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b)
            {
                if (IsPointInAnyWindow(a) || IsPointInAnyWindow(b))
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

            bool IsHorizontalLike(Point2d a, Point2d b)
            {
                var dx = Math.Abs(b.X - a.X);
                var dy = Math.Abs(b.Y - a.Y);
                return dx >= (dy * 1.2);
            }

            bool IsVerticalLike(Point2d a, Point2d b)
            {
                var dx = Math.Abs(b.X - a.X);
                var dy = Math.Abs(b.Y - a.Y);
                return dy >= (dx * 1.2);
            }

            bool IsVerticalHardTargetLayer(string layer)
            {
                return string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase);
            }

            bool TryMoveEndpoint(Entity writable, bool moveStart, Point2d target, double moveTol)
            {
                if (writable is Line ln)
                {
                    var old = moveStart
                        ? new Point2d(ln.StartPoint.X, ln.StartPoint.Y)
                        : new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                    if (old.GetDistanceTo(target) <= moveTol)
                    {
                        return false;
                    }

                    if (moveStart)
                    {
                        ln.StartPoint = new Point3d(target.X, target.Y, ln.StartPoint.Z);
                    }
                    else
                    {
                        ln.EndPoint = new Point3d(target.X, target.Y, ln.EndPoint.Z);
                    }

                    return true;
                }

                if (writable is Polyline pl && !pl.Closed && pl.NumberOfVertices >= 2)
                {
                    var index = moveStart ? 0 : pl.NumberOfVertices - 1;
                    var old = pl.GetPoint2dAt(index);
                    if (old.GetDistanceTo(target) <= moveTol)
                    {
                        return false;
                    }

                    pl.SetPointAt(index, target);
                    return true;
                }

                return false;
            }

            bool TryIntersectInfiniteLines(Point2d a0, Point2d a1, Point2d b0, Point2d b1, out Point2d intersection)
            {
                intersection = default;
                var da = a1 - a0;
                var db = b1 - b0;
                var denom = Cross2d(da, db);
                if (Math.Abs(denom) <= 1e-9)
                {
                    return false;
                }

                var diff = b0 - a0;
                var t = Cross2d(diff, db) / denom;
                intersection = a0 + (da * t);
                return true;
            }

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
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                var correctionInnerSources = new List<(ObjectId Id, Point2d A, Point2d B)>();
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
                        correctionOuterSegments.Add((a, b, Midpoint(a, b), Math.Min(a.X, b.X), Math.Max(a.X, b.X)));
                        continue;
                    }

                    if (IsVerticalHardTargetLayer(layer) && IsVerticalLike(a, b))
                    {
                        verticalTargets.Add((id, a, b, Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y), 0.5 * (a.X + b.X)));
                    }
                }

                if (correctionInnerSources.Count == 0 || verticalTargets.Count == 0)
                {
                    tr.Commit();
                    return false;
                }

                const double endpointTouchTol = 0.35;
                const double endpointMoveTol = 0.05;
                const double minMove = 0.05;
                const double maxMove = 1200.0;
                const double maxVerticalTargetGap = 520.0;
                const double maxVerticalTargetLength = 2200.0;
                const double maxVerticalEndpointMove = 520.0;
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
                var sampleMoves = new List<string>();

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

                    var candidateA = moveStart ? connectionPoint : currentA;
                    var candidateB = moveStart ? currentB : connectionPoint;
                    if (candidateA.GetDistanceTo(candidateB) > maxVerticalTargetLength)
                    {
                        return false;
                    }

                    if (preferredDirectionSign > 0 && connectionPoint.Y > moveEndpoint.Y + directionAxisTol)
                    {
                        return false;
                    }

                    if (preferredDirectionSign < 0 && connectionPoint.Y < moveEndpoint.Y - directionAxisTol)
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

                    if (!TryReadOpenLinearSegment(targetWritable, out var newA, out var newB))
                    {
                        return true;
                    }

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

                tr.Commit();
                logger?.WriteLine(
                    $"CorrectionLine: C-0 endpoint snap scanned={scannedEndpoints}, movedEndpoints={movedEndpoints}, movedHorizontal={movedHorizontalEndpoints}, movedVertical={movedVerticalEndpoints}, movedLines={movedLines}, alreadyConnected={alreadyConnected}, sharedSkipped={sharedEndpointSkipped}, noDirectionResolved={noDirectionResolved}, noTargetFound={noTargetFound}, maxMove={maxMove:0.##}.");
                if (sampleMoves.Count > 0)
                {
                    logger?.WriteLine($"CorrectionLine: C-0 endpoint snap samples ({sampleMoves.Count})");
                    for (var i = 0; i < sampleMoves.Count; i++)
                    {
                        logger?.WriteLine("CorrectionLine:   " + sampleMoves[i]);
                    }
                }

                return movedEndpoints > 0;
            }
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

                if (segment.MaxY < seam.SouthY - 40.0 || segment.MinY > seam.NorthY + 40.0)
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
            Func<double, double> targetYAtX,
            Func<double, double> centerYAtX,
            bool preferAboveCenter,
            double strictTol)
        {
            if (candidates == null || candidates.Count == 0 || targetYAtX == null || centerYAtX == null)
            {
                return new List<CorrectionSegment>();
            }

            bool IsOnExpectedSide(CorrectionSegment c)
            {
                var centerY = centerYAtX(c.MidX);
                if (preferAboveCenter)
                {
                    return c.MidY >= centerY - 0.1;
                }

                return c.MidY <= centerY + 0.1;
            }

            var inStrictBand = candidates
                .Where(c => IsOnExpectedSide(c) && Math.Abs(c.MidY - targetYAtX(c.MidX)) <= strictTol)
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

                var distance = Math.Abs(c.MidY - targetYAtX(c.MidX));
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
                            Math.Abs(Math.Abs(c.MidY - targetYAtX(c.MidX)) - bestResidual) <= fallbackBandTol)
                .ToList();
        }

        private static bool TryFindCorrectionInnerCompanion(
            CorrectionSegment outer,
            IReadOnlyList<CorrectionSegment> horizontalCandidates,
            double expectedInset,
            Func<double, double> centerYAtX,
            out CorrectionSegment companion)
        {
            companion = default;
            if (horizontalCandidates == null || horizontalCandidates.Count == 0 || centerYAtX == null)
            {
                return false;
            }

            var minRequiredOverlap = Math.Max(20.0, Math.Min(outer.Length * 0.2, 60.0));
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
                if (overlap < minRequiredOverlap)
                {
                    continue;
                }

                var offset = Math.Abs(candidate.MidY - outer.MidY);
                var offsetDelta = Math.Abs(offset - expectedInset);
                if (offsetDelta > 1.25)
                {
                    continue;
                }

                // Companion must be inward (closer to seam center than the outer line).
                var outerCenterY = centerYAtX(outer.MidX);
                var candidateCenterY = centerYAtX(candidate.MidX);
                if (Math.Abs(candidate.MidY - candidateCenterY) >= Math.Abs(outer.MidY - outerCenterY))
                {
                    continue;
                }

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
            Func<double, double> centerYAtX,
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
            if (!outer.IsHorizontalLike || modelSpace == null || tr == null || centerYAtX == null)
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
            var centerY = centerYAtX(mid.X);
            var candidateMidA = mid + (normal * inset);
            var candidateMidB = mid + (oppositeNormal * inset);
            var chosenOffset = Math.Abs(candidateMidA.Y - centerY) <= Math.Abs(candidateMidB.Y - centerY)
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

            public void AddNorthBoundary(double y, double minX, double maxX)
            {
                var sampleX = 0.5 * (minX + maxX);
                _northBoundarySamples.Add(new Point2d(sampleX, y));
                UpdateXRange(minX, maxX);
            }

            public void AddSouthBoundary(double y, double minX, double maxX)
            {
                var sampleX = 0.5 * (minX + maxX);
                _southBoundarySamples.Add(new Point2d(sampleX, y));
                UpdateXRange(minX, maxX);
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
                MinX = minX;
                MaxX = maxX;
                NorthY = northY;
                SouthY = southY;
                NorthSlope = northSlope;
                NorthIntercept = northIntercept;
                SouthSlope = southSlope;
                SouthIntercept = southIntercept;
                CenterY = 0.5 * (northY + southY);
            }

            public int Zone { get; }
            public string Meridian { get; }
            public int Range { get; }
            public int NorthTownship { get; }
            public double MinX { get; }
            public double MaxX { get; }
            public double NorthY { get; }
            public double SouthY { get; }
            public double NorthSlope { get; }
            public double NorthIntercept { get; }
            public double SouthSlope { get; }
            public double SouthIntercept { get; }
            public double CenterY { get; }

            public double GetNorthYAt(double x) => (NorthSlope * x) + NorthIntercept;
            public double GetSouthYAt(double x) => (SouthSlope * x) + SouthIntercept;
            public double GetCenterYAt(double x) => 0.5 * (GetNorthYAt(x) + GetSouthYAt(x));
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
