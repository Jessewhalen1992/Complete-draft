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
            var useCadenceFilter = selectedAnchorMatches > 0;
            logger?.WriteLine(
                $"CorrectionLine: cadence anchor evaluation [{string.Join(", ", anchorSummary)}], selected={selectedAnchorTownship}, useCadenceFilter={useCadenceFilter}, sectionsInScope={sectionMetaById.Count}, parseTownshipFailed={parseTownshipFailed}, parseRangeFailed={parseRangeFailed}, parseSectionFailed={parseSectionFailed}.");

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
                    if (useCadenceFilter)
                    {
                        isNorthSeamSection = isNorthSeamSection &&
                                             IsOnCorrectionCadence(townshipNumber - 1, selectedAnchorTownship);
                        isSouthSeamSection = isSouthSeamSection &&
                                             IsOnCorrectionCadence(townshipNumber, selectedAnchorTownship);
                    }
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
                    var horizontalCandidates = segments
                        .Where(s => s.IsHorizontalLike &&
                                    s.MaxX >= seam.MinX - 25.0 &&
                                    s.MinX <= seam.MaxX + 25.0 &&
                                    s.MidY >= seam.GetSouthYAt(s.MidX) - 12.0 &&
                                    s.MidY <= seam.GetNorthYAt(s.MidX) + 12.0 &&
                                    GetCorrectionHorizontalOverlap(s, seam.MinX, seam.MaxX) >= 2.0)
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

                    var uniqueOuters = northOuter
                        .Concat(southOuter)
                        .GroupBy(s => s.Id)
                        .Select(g => g.First())
                        .ToList();
                    var sortedOuterLengths = uniqueOuters
                        .Select(s => s.Length)
                        .OrderBy(v => v)
                        .ToList();
                    var medianOuterLength = sortedOuterLengths.Count == 0
                        ? 0.0
                        : sortedOuterLengths[sortedOuterLengths.Count / 2];
                    var minInnerCompanionOuterLength = Math.Max(120.0, medianOuterLength * 0.4);
                    var skippedShortOuterCompanions = 0;

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

                    foreach (var outer in northOuter)
                    {
                        if (outer.Length < minInnerCompanionOuterLength)
                        {
                            skippedShortOuterCompanions++;
                            continue;
                        }

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
                    }

                    foreach (var outer in southOuter)
                    {
                        if (outer.Length < minInnerCompanionOuterLength)
                        {
                            skippedShortOuterCompanions++;
                            continue;
                        }

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
                    }

                    logger?.WriteLine(
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "CorrectionLine: seam Z{0} M{1} R{2} T{3}/{4} companionMinOuterLen={5:0.###} skippedShortCompanions={6}.",
                            seam.Zone,
                            seam.Meridian,
                            seam.Range,
                            seam.NorthTownship,
                            seam.NorthTownship - 1,
                            minInnerCompanionOuterLength,
                            skippedShortOuterCompanions));
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
