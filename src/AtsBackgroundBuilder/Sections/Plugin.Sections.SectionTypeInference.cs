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
        private static string NormalizeSecType(string secType)
        {
            return string.Equals(secType?.Trim(), "L-SEC", StringComparison.OrdinalIgnoreCase)
                ? "L-SEC"
                : "L-USEC";
        }

        private static string ResolveSectionType(
            SectionKey key,
            string requestedSecType,
            IReadOnlyDictionary<string, string> inferredSecTypes)
        {
            var keyId = BuildSectionKeyId(key);
            if (inferredSecTypes != null &&
                inferredSecTypes.TryGetValue(keyId, out var inferred) &&
                !string.IsNullOrWhiteSpace(inferred))
            {
                return NormalizeSecType(inferred);
            }

            // "AUTO" or unknown values normalize to L-USEC.
            return NormalizeSecType(requestedSecType);
        }

        private static string BuildSectionQuarterKeyId(SectionKey key, QuarterSelection quarter)
        {
            return BuildSectionQuarterKeyId(BuildSectionKeyId(key), quarter);
        }

        private static string BuildSectionQuarterKeyId(string sectionKeyId, QuarterSelection quarter)
        {
            if (string.IsNullOrWhiteSpace(sectionKeyId))
            {
                return string.Empty;
            }

            var token = QuarterSelectionToToken(quarter);
            if (string.IsNullOrWhiteSpace(token))
            {
                return string.Empty;
            }

            return $"{sectionKeyId}|{token}";
        }

        private static Dictionary<QuarterSelection, string> ResolveQuarterSectionTypes(
            SectionKey key,
            string fallbackSecType,
            IReadOnlyDictionary<string, string> inferredQuarterSecTypes)
        {
            var resolved = new Dictionary<QuarterSelection, string>();
            var fallback = NormalizeSecType(fallbackSecType);
            var sectionQuarterKeys = new[]
            {
                QuarterSelection.NorthWest,
                QuarterSelection.NorthEast,
                QuarterSelection.SouthWest,
                QuarterSelection.SouthEast
            };

            foreach (var quarter in sectionQuarterKeys)
            {
                var quarterKeyId = BuildSectionQuarterKeyId(key, quarter);
                if (!string.IsNullOrWhiteSpace(quarterKeyId) &&
                    inferredQuarterSecTypes != null &&
                    inferredQuarterSecTypes.TryGetValue(quarterKeyId, out var inferred) &&
                    !string.IsNullOrWhiteSpace(inferred))
                {
                    resolved[quarter] = NormalizeSecType(inferred);
                }
                else
                {
                    resolved[quarter] = fallback;
                }
            }

            return resolved;
        }

        private static Dictionary<string, string> InferQuarterSectionTypes(
            IReadOnlyList<SectionRequest> requests,
            IReadOnlyList<string> searchFolders,
            Logger logger)
        {
            var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (requests == null || requests.Count == 0 || searchFolders == null || searchFolders.Count == 0)
            {
                return resolved;
            }

            var selectedSectionKeyIds = new HashSet<string>(
                requests.Select(r => BuildSectionKeyId(r.Key)),
                StringComparer.OrdinalIgnoreCase);
            var contextTownshipKeys = BuildContextTownshipKeys(requests);
            var geoms = new List<(
                string KeyId,
                string Label,
                Point2d SW,
                Point2d SE,
                Point2d NW,
                Point2d NE,
                Point2d Center,
                int Zone,
                string Meridian,
                int GlobalX,
                int GlobalY)>();

            foreach (var townshipKey in contextTownshipKeys)
            {
                if (!TryParseTownshipKey(townshipKey, out var zone, out var meridian, out var range, out var township))
                {
                    continue;
                }

                for (var section = 1; section <= 36; section++)
                {
                    var sectionKey = new SectionKey(zone, section.ToString(CultureInfo.InvariantCulture), township, range, meridian);
                    if (!TryLoadSectionOutline(searchFolders, sectionKey, logger, out var outline))
                    {
                        continue;
                    }

                    using (var poly = new Polyline(outline.Vertices.Count))
                    {
                        poly.Closed = outline.Closed;
                        for (var vi = 0; vi < outline.Vertices.Count; vi++)
                        {
                            poly.AddVertexAt(vi, outline.Vertices[vi], 0, 0, 0);
                        }

                        if (!TryGetQuarterAnchors(poly, out var anchors))
                        {
                            anchors = GetFallbackAnchors(poly);
                        }

                        var eastUnit = GetUnitVector(anchors.Left, anchors.Right, new Vector2d(1, 0));
                        var northUnit = GetUnitVector(anchors.Bottom, anchors.Top, new Vector2d(0, 1));
                        if (!TryGetQuarterCorner(poly, eastUnit, northUnit, QuarterCorner.SouthWest, out var sw) ||
                            !TryGetQuarterCorner(poly, eastUnit, northUnit, QuarterCorner.SouthEast, out var se) ||
                            !TryGetQuarterCorner(poly, eastUnit, northUnit, QuarterCorner.NorthWest, out var nw) ||
                            !TryGetQuarterCorner(poly, eastUnit, northUnit, QuarterCorner.NorthEast, out var ne))
                        {
                            continue;
                        }

                        var center = new Point2d(
                            0.25 * (sw.X + se.X + nw.X + ne.X),
                            0.25 * (sw.Y + se.Y + nw.Y + ne.Y));
                        var rangeNum = 0;
                        var townshipNum = 0;
                        var hasRange = TryParsePositiveToken(range, out rangeNum);
                        var hasTownship = TryParsePositiveToken(township, out townshipNum);
                        var hasGrid = TryGetAtsSectionGridPosition(section, out var row, out var col);
                        var globalX = (hasRange && hasGrid) ? ((-rangeNum * 6) + col) : int.MinValue;
                        var globalY = (hasTownship && hasGrid) ? ((townshipNum * 6) + (5 - row)) : int.MinValue;

                        geoms.Add((
                            BuildSectionKeyId(sectionKey),
                            BuildSectionDescriptor(sectionKey),
                            sw, se, nw, ne, center,
                            zone,
                            NormalizeNumberToken(meridian),
                            globalX,
                            globalY));
                    }
                }
            }

            if (geoms.Count == 0)
            {
                return resolved;
            }

            var euVec = geoms[0].SE - geoms[0].SW;
            var nuVec = geoms[0].NW - geoms[0].SW;
            var eu = euVec.Length > 1e-9 ? (euVec / euVec.Length) : new Vector2d(1, 0);
            var nu = nuVec.Length > 1e-9 ? (nuVec / nuVec.Length) : new Vector2d(0, 1);

            var localKeyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var selectedGeoms = geoms
                .Where(g => selectedSectionKeyIds.Contains(g.KeyId))
                .ToList();
            if (selectedGeoms.Count > 0)
            {
                foreach (var g in geoms)
                {
                    foreach (var s in selectedGeoms)
                    {
                        if (g.Zone != s.Zone ||
                            !string.Equals(g.Meridian, s.Meridian, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var dx = g.Center.X - s.Center.X;
                        var dy = g.Center.Y - s.Center.Y;
                        var centerDistance = Math.Sqrt((dx * dx) + (dy * dy));
                        var spanG = Math.Max((g.SE - g.SW).Length, (g.NW - g.SW).Length);
                        var spanS = Math.Max((s.SE - s.SW).Length, (s.NW - s.SW).Length);
                        var neighborThreshold = Math.Max(spanG, spanS) * 1.8;
                        if (centerDistance <= neighborThreshold)
                        {
                            localKeyIds.Add(g.KeyId);
                            break;
                        }
                    }
                }
            }

            var selectedOrLocalKeyIds = localKeyIds.Count > 0
                ? localKeyIds
                : selectedSectionKeyIds;
            var quarterGapSamples = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);

            var geomIndexByGrid = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < geoms.Count; i++)
            {
                var g = geoms[i];
                if (g.GlobalX == int.MinValue || g.GlobalY == int.MinValue)
                {
                    continue;
                }

                var key = BuildSectionGridLookupKey(g.Zone, g.Meridian, g.GlobalX, g.GlobalY);
                if (!geomIndexByGrid.ContainsKey(key))
                {
                    geomIndexByGrid[key] = i;
                }
            }

            for (var i = 0; i < geoms.Count; i++)
            {
                var a = geoms[i];
                if (a.GlobalX == int.MinValue || a.GlobalY == int.MinValue)
                {
                    continue;
                }

                if (TryGetNeighborGeometryIndex(
                        geomIndexByGrid,
                        a.Zone,
                        a.Meridian,
                        a.GlobalX,
                        a.GlobalY,
                        1,
                        0,
                        out var eastNeighborIndex))
                {
                    var b = geoms[eastNeighborIndex];
                    if (IsSectionNeighborPairEligible(selectedOrLocalKeyIds, a, b))
                    {
                        var aIsWest = a.Center.X <= b.Center.X;
                        var west = aIsWest ? a : b;
                        var east = aIsWest ? b : a;
                        TryAddQuarterEvidenceFromPair(
                            west.KeyId,
                            west.SE,
                            west.NE,
                            east.KeyId,
                            east.SW,
                            east.NW,
                            verticalMode: true,
                            eu,
                            nu,
                            quarterGapSamples);
                    }
                }

                if (TryGetNeighborGeometryIndex(
                        geomIndexByGrid,
                        a.Zone,
                        a.Meridian,
                        a.GlobalX,
                        a.GlobalY,
                        0,
                        1,
                        out var northNeighborIndex))
                {
                    var b = geoms[northNeighborIndex];
                    if (IsSectionNeighborPairEligible(selectedOrLocalKeyIds, a, b))
                    {
                        var aIsSouth = a.Center.Y <= b.Center.Y;
                        var south = aIsSouth ? a : b;
                        var north = aIsSouth ? b : a;
                        TryAddQuarterEvidenceFromPair(
                            south.KeyId,
                            south.NW,
                            south.NE,
                            north.KeyId,
                            north.SW,
                            north.SE,
                            verticalMode: false,
                            eu,
                            nu,
                            quarterGapSamples);
                    }
                }
            }

            foreach (var pair in quarterGapSamples)
            {
                resolved[pair.Key] = InferQuarterSecTypeFromRoadAllowance(pair.Value);
            }

            logger?.WriteLine($"Quarter SEC TYPE inferred: {resolved.Count} quarter(s).");
            return resolved;
        }

        private static void AddQuarterGapSample(
            IDictionary<string, List<double>> quarterGapSamples,
            string sectionKeyId,
            QuarterSelection quarter,
            double gapMeters)
        {
            if (gapMeters < 1.0)
            {
                return;
            }

            var quarterKeyId = BuildSectionQuarterKeyId(sectionKeyId, quarter);
            if (string.IsNullOrWhiteSpace(quarterKeyId))
            {
                return;
            }

            if (!quarterGapSamples.TryGetValue(quarterKeyId, out var samples))
            {
                samples = new List<double>();
                quarterGapSamples[quarterKeyId] = samples;
            }

            samples.Add(gapMeters);
        }

        private static double MeasureQuarterGapFromBaseSample(
            Point2d baseSample,
            Point2d otherEdgeStart,
            Vector2d otherEdgeDirection,
            double otherEdgeLengthSquared,
            Vector2d baseDirectionUnit)
        {
            var projection = (baseSample - otherEdgeStart).DotProduct(otherEdgeDirection) / otherEdgeLengthSquared;
            projection = Math.Max(0.0, Math.Min(1.0, projection));
            var otherSample = otherEdgeStart + (otherEdgeDirection * projection);

            var normal = new Vector2d(-baseDirectionUnit.Y, baseDirectionUnit.X);
            if ((otherSample - baseSample).DotProduct(normal) < 0.0)
            {
                normal = -normal;
            }

            return Math.Abs((otherSample - baseSample).DotProduct(normal));
        }

        private static void TryAddQuarterEvidenceFromPair(
            string keyA,
            Point2d a0,
            Point2d a1,
            string keyB,
            Point2d b0,
            Point2d b1,
            bool verticalMode,
            Vector2d eu,
            Vector2d nu,
            IDictionary<string, List<double>> quarterGapSamples)
        {
            var da = a1 - a0;
            var db = b1 - b0;
            var la = da.Length;
            var lb = db.Length;
            if (la <= 1e-6 || lb <= 1e-6)
            {
                return;
            }

            var ua = da / la;
            var ub = db / lb;
            if (Math.Abs(ua.DotProduct(ub)) < 0.99)
            {
                return;
            }

            var axis = ua;
            if (axis.DotProduct(ub) < 0.0)
            {
                ub = -ub;
            }

            if ((a1 - a0).DotProduct(axis) < 0.0)
            {
                var tmp = a0;
                a0 = a1;
                a1 = tmp;
                da = a1 - a0;
                la = da.Length;
                ua = da / la;
            }

            if ((b1 - b0).DotProduct(axis) < 0.0)
            {
                var tmp = b0;
                b0 = b1;
                b1 = tmp;
                db = b1 - b0;
                lb = db.Length;
                ub = db / lb;
            }

            var aMid = Midpoint(a0, a1);
            var bMid = Midpoint(b0, b1);
            var pa = verticalMode
                ? (aMid.X * eu.X + aMid.Y * eu.Y)
                : (aMid.X * nu.X + aMid.Y * nu.Y);
            var pb = verticalMode
                ? (bMid.X * eu.X + bMid.Y * eu.Y)
                : (bMid.X * nu.X + bMid.Y * nu.Y);

            var westOrSouth0 = pa <= pb ? a0 : b0;
            var westOrSouth1 = pa <= pb ? a1 : b1;
            var eastOrNorth0 = pa <= pb ? b0 : a0;
            var eastOrNorth1 = pa <= pb ? b1 : a1;
            var baseKey = pa <= pb ? keyA : keyB;
            var otherKey = pa <= pb ? keyB : keyA;

            var baseDir = westOrSouth1 - westOrSouth0;
            var baseLen = baseDir.Length;
            if (baseLen <= 1e-6)
            {
                return;
            }

            var baseU = baseDir / baseLen;
            var tBase0 = 0.0;
            var tBase1 = baseLen;
            var tOther0 = (eastOrNorth0 - westOrSouth0).DotProduct(baseU);
            var tOther1 = (eastOrNorth1 - westOrSouth0).DotProduct(baseU);
            var overlapMin = Math.Max(Math.Min(tBase0, tBase1), Math.Min(tOther0, tOther1));
            var overlapMax = Math.Min(Math.Max(tBase0, tBase1), Math.Max(tOther0, tOther1));
            const double endpointSnapToleranceMeters = 1.0;
            if (overlapMin > 0.0 && overlapMin < endpointSnapToleranceMeters)
            {
                overlapMin = 0.0;
            }

            if (overlapMax < baseLen && (baseLen - overlapMax) < endpointSnapToleranceMeters)
            {
                overlapMax = baseLen;
            }

            var overlapLength = overlapMax - overlapMin;
            var minEdgeLength = Math.Min(baseLen, lb);
            if (overlapLength < Math.Max(100.0, minEdgeLength * 0.75))
            {
                return;
            }

            var baseStart = westOrSouth0 + (baseU * overlapMin);
            var baseEnd = westOrSouth0 + (baseU * overlapMax);

            var otherDir = eastOrNorth1 - eastOrNorth0;
            var otherLen2 = otherDir.DotProduct(otherDir);
            if (otherLen2 <= 1e-9)
            {
                return;
            }

            var splitT = overlapMin + (0.5 * (overlapMax - overlapMin));
            splitT = Math.Max(overlapMin, Math.Min(overlapMax, splitT));
            var baseMid = westOrSouth0 + (baseU * splitT);
            var span = overlapMax - overlapMin;
            var sampleQ1 = westOrSouth0 + (baseU * (overlapMin + (0.25 * span)));
            var sampleQ3 = westOrSouth0 + (baseU * (overlapMin + (0.75 * span)));
            var gapQ1 = MeasureQuarterGapFromBaseSample(sampleQ1, eastOrNorth0, otherDir, otherLen2, baseU);
            var gapQ3 = MeasureQuarterGapFromBaseSample(sampleQ3, eastOrNorth0, otherDir, otherLen2, baseU);

            var segments = new[]
            {
                (
                    Gap: gapQ1,
                    BaseQuarter: verticalMode ? QuarterSelection.SouthEast : QuarterSelection.NorthWest,
                    OtherQuarter: verticalMode ? QuarterSelection.SouthWest : QuarterSelection.SouthWest),
                (
                    Gap: gapQ3,
                    BaseQuarter: verticalMode ? QuarterSelection.NorthEast : QuarterSelection.NorthEast,
                    OtherQuarter: verticalMode ? QuarterSelection.NorthWest : QuarterSelection.SouthEast)
            };

            foreach (var seg in segments)
            {
                AddQuarterGapSample(quarterGapSamples, baseKey, seg.BaseQuarter, seg.Gap);
                AddQuarterGapSample(quarterGapSamples, otherKey, seg.OtherQuarter, seg.Gap);
            }
        }

        private static string BuildSectionGridLookupKey(int zone, string meridian, int globalX, int globalY)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}|{1}|{2}|{3}",
                zone,
                meridian ?? string.Empty,
                globalX,
                globalY);
        }

        private static bool TryGetNeighborGeometryIndex(
            IReadOnlyDictionary<string, int> geomIndexByGrid,
            int zone,
            string meridian,
            int globalX,
            int globalY,
            int dx,
            int dy,
            out int index)
        {
            var key = BuildSectionGridLookupKey(zone, meridian, globalX + dx, globalY + dy);
            return geomIndexByGrid.TryGetValue(key, out index);
        }

        private static bool IsSectionNeighborPairEligible(
            ISet<string> selectedOrLocalKeyIds,
            (string KeyId, string Label, Point2d SW, Point2d SE, Point2d NW, Point2d NE, Point2d Center, int Zone, string Meridian, int GlobalX, int GlobalY) a,
            (string KeyId, string Label, Point2d SW, Point2d SE, Point2d NW, Point2d NE, Point2d Center, int Zone, string Meridian, int GlobalX, int GlobalY) b)
        {
            if (!selectedOrLocalKeyIds.Contains(a.KeyId) &&
                !selectedOrLocalKeyIds.Contains(b.KeyId))
            {
                return false;
            }

            var centerDx = b.Center.X - a.Center.X;
            var centerDy = b.Center.Y - a.Center.Y;
            var centerDistance = Math.Sqrt((centerDx * centerDx) + (centerDy * centerDy));
            var spanA = Math.Max((a.SE - a.SW).Length, (a.NW - a.SW).Length);
            var spanB = Math.Max((b.SE - b.SW).Length, (b.NW - b.SW).Length);
            return centerDistance <= (Math.Max(spanA, spanB) * 1.8);
        }

        private static Dictionary<string, string> InferSectionTypes(
            IReadOnlyList<SectionRequest> requests,
            IReadOnlyList<string> searchFolders,
            Logger logger)
        {
            var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (requests == null || requests.Count == 0)
            {
                return resolved;
            }

            var uniqueTownships = BuildContextTownshipKeys(requests).ToList();
            var entries = new List<(SectionKey Key, SectionSpatialInfo Info)>();
            try
            {
                foreach (var townshipKey in uniqueTownships)
                {
                    if (!TryParseTownshipKey(townshipKey, out var zone, out var meridian, out var range, out var township))
                    {
                        continue;
                    }

                    for (var section = 1; section <= 36; section++)
                    {
                        var key = new SectionKey(zone, section.ToString(CultureInfo.InvariantCulture), township, range, meridian);
                        if (!TryLoadSectionOutline(searchFolders, key, logger, out var outline))
                        {
                            continue;
                        }

                        if (TryCreateSectionSpatialInfo(outline, section, out var info))
                        {
                            entries.Add((key, info));
                        }
                    }
                }

                if (entries.Count == 0)
                {
                    return resolved;
                }

                foreach (var entry in entries)
                {
                    var peerInfos = entries
                        .Where(e => e.Info != null &&
                                    e.Key.Zone == entry.Key.Zone &&
                                    string.Equals(NormalizeNumberToken(e.Key.Meridian), NormalizeNumberToken(entry.Key.Meridian), StringComparison.OrdinalIgnoreCase))
                        .Select(e => e.Info)
                        .ToList();

                    var haveEastGap = TryMeasureRoadAllowanceGap(entry.Info, peerInfos, eastDirection: true, out var eastGap);
                    var haveSouthGap = TryMeasureRoadAllowanceGap(entry.Info, peerInfos, eastDirection: false, out var southGap);

                    var inferred = InferSecTypeFromRoadAllowance(haveEastGap ? eastGap : (double?)null, haveSouthGap ? southGap : (double?)null);
                    var keyId = BuildSectionKeyId(entry.Key);
                    resolved[keyId] = inferred;

                    if (EnableRoadAllowanceDiagnostics)
                    {
                        logger?.WriteLine(
                            $"SEC TYPE inferred: {keyId} -> {inferred} (eastGap={(haveEastGap ? eastGap.ToString("0.###", CultureInfo.InvariantCulture) : "n/a")}, southGap={(haveSouthGap ? southGap.ToString("0.###", CultureInfo.InvariantCulture) : "n/a")})");
                    }
                }
            }
            finally
            {
                DisposeSectionInfos(entries.Select(e => e.Info).ToList());
            }

            return resolved;
        }

        private static string InferSecTypeFromRoadAllowance(double? eastGap, double? southGap)
        {
            var gaps = new List<double>();
            if (eastGap.HasValue) gaps.Add(eastGap.Value);
            if (southGap.HasValue) gaps.Add(southGap.Value);

            // Blind/correction lines can produce near-zero or overlap gaps; treat as unknown.
            var measurableGaps = gaps.Where(g => g >= 1.0).ToList();
            if (measurableGaps.Count == 0)
            {
                return "L-USEC";
            }

            // Rule: width >= 25m is unsurveyed, otherwise surveyed.
            // Use the dominant (largest) measurable width so mixed evidence
            // near correction lines does not flip section type inconsistently.
            var representativeGap = measurableGaps.Max();
            if (representativeGap >= SurveyedUnsurveyedThresholdMeters)
            {
                return "L-USEC";
            }

            if (representativeGap >= (RoadAllowanceSecWidthMeters * 0.50))
            {
                return "L-SEC";
            }

            var hasUsec = measurableGaps.Any(g => Math.Abs(g - RoadAllowanceUsecWidthMeters) <= RoadAllowanceWidthToleranceMeters);
            var hasSec = measurableGaps.Any(g => Math.Abs(g - RoadAllowanceSecWidthMeters) <= RoadAllowanceWidthToleranceMeters);

            if (hasUsec && !hasSec)
            {
                return "L-USEC";
            }

            if (hasSec && !hasUsec)
            {
                return "L-SEC";
            }

            if (hasSec && hasUsec)
            {
                // Prefer surveyed when evidence is mixed so shared 20.11 boundaries
                // on adjacent sections resolve to the same section layer.
                return "L-SEC";
            }

            // Strict classification: if neither 20.11 nor 30.16 is observed inside tolerance,
            // keep unsurveyed instead of nearest-gap fallback.
            return "L-USEC";
        }

        private static string InferQuarterSecTypeFromRoadAllowance(IReadOnlyCollection<double> gaps)
        {
            if (gaps == null || gaps.Count == 0)
            {
                return "L-USEC";
            }

            var measurableGaps = gaps.Where(g => g >= 1.0).ToList();
            if (measurableGaps.Count == 0)
            {
                return "L-USEC";
            }

            // Rule: width >= 25m is unsurveyed, otherwise surveyed.
            var representativeGap = measurableGaps.Max();
            if (representativeGap >= SurveyedUnsurveyedThresholdMeters)
            {
                return "L-USEC";
            }

            if (representativeGap >= (RoadAllowanceSecWidthMeters * 0.50))
            {
                return "L-SEC";
            }

            var secMatches = measurableGaps
                .Where(g => Math.Abs(g - RoadAllowanceSecWidthMeters) <= RoadAllowanceWidthToleranceMeters)
                .ToList();
            var usecMatches = measurableGaps
                .Where(g => Math.Abs(g - RoadAllowanceUsecWidthMeters) <= RoadAllowanceGapOffsetToleranceMeters)
                .ToList();

            if (secMatches.Count > 0 && usecMatches.Count == 0)
            {
                return "L-SEC";
            }

            if (usecMatches.Count > 0 && secMatches.Count == 0)
            {
                return "L-USEC";
            }

            if (secMatches.Count > 0 && usecMatches.Count > 0)
            {
                if (secMatches.Count > usecMatches.Count)
                {
                    return "L-SEC";
                }

                if (usecMatches.Count > secMatches.Count)
                {
                    return "L-USEC";
                }

                var nearestSecMatched = secMatches.Min(g => Math.Abs(g - RoadAllowanceSecWidthMeters));
                var nearestUsecMatched = usecMatches.Min(g => Math.Abs(g - RoadAllowanceUsecWidthMeters));
                if (nearestSecMatched < nearestUsecMatched)
                {
                    return "L-SEC";
                }

                if (nearestUsecMatched < nearestSecMatched)
                {
                    return "L-USEC";
                }

                // When quarter evidence is evenly mixed, keep unsurveyed to avoid
                // promoting an entire 1/4 to surveyed from only one matching edge.
                return "L-USEC";
            }

            // Strict classification: require an explicit 20.11/30.16 match;
            // do not use nearest-gap fallback.
            return "L-USEC";
        }

        private static bool TryMeasureRoadAllowanceGap(
            SectionSpatialInfo source,
            IReadOnlyList<SectionSpatialInfo> townshipInfos,
            bool eastDirection,
            out double gapMeters)
        {
            gapMeters = 0.0;
            if (source == null || townshipInfos == null || townshipInfos.Count == 0)
            {
                return false;
            }

            var sourceCenter = GetSectionCenter(source);
            var bestGap = double.MaxValue;
            var found = false;

            foreach (var candidate in townshipInfos)
            {
                if (candidate == null || ReferenceEquals(candidate, source))
                {
                    continue;
                }

                var candidateCenter = GetSectionCenter(candidate);
                var delta = candidateCenter - sourceCenter;
                var eastDelta = delta.DotProduct(source.EastUnit);
                var northDelta = delta.DotProduct(source.NorthUnit);

                if (eastDirection)
                {
                    if (Math.Abs(northDelta) > Math.Max(source.Height, candidate.Height) * 0.60)
                        continue;

                    var projectedGap = Math.Abs(eastDelta) - (source.Width * 0.5) - (candidate.Width * 0.5);
                    if (projectedGap < -RoadAllowanceWidthToleranceMeters)
                        continue;
                    // Ignore near-zero/overlap artifacts from correction-line geometry.
                    if (projectedGap < 1.0)
                        continue;

                    if (!found || projectedGap < bestGap)
                    {
                        bestGap = projectedGap;
                        found = true;
                    }
                }
                else
                {
                    if (Math.Abs(eastDelta) > Math.Max(source.Width, candidate.Width) * 0.60)
                        continue;

                    var projectedGap = Math.Abs(northDelta) - (source.Height * 0.5) - (candidate.Height * 0.5);
                    if (projectedGap < -RoadAllowanceWidthToleranceMeters)
                        continue;
                    // Ignore near-zero/overlap artifacts from correction-line geometry.
                    if (projectedGap < 1.0)
                        continue;

                    if (!found || projectedGap < bestGap)
                    {
                        bestGap = projectedGap;
                        found = true;
                    }
                }
            }

            if (!found)
            {
                return false;
            }

            gapMeters = Math.Max(0.0, bestGap);
            return true;
        }

        private static Point2d GetSectionCenter(SectionSpatialInfo section)
        {
            return section.SouthWest +
                   (section.EastUnit * (section.Width * 0.5)) +
                   (section.NorthUnit * (section.Height * 0.5));
        }
    }
}
