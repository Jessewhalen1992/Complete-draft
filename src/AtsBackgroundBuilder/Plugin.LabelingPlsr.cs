using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AtsBackgroundBuilder
{
    public partial class Plugin
    {
        private static ObjectId InsertSectionLabelBlock(
            BlockTableRecord modelSpace,
            BlockTable blockTable,
            Transaction transaction,
            Editor editor,
            Point3d position,
            SectionKey key)
        {
            const string blockName = "L-SECLBL";

            if (!blockTable.Has(blockName))
            {
                editor?.WriteMessage($"\nBUILDSEC: Block '{blockName}' not found; skipped section label.");
                return ObjectId.Null;
            }

            var blockId = blockTable[blockName];
            var blockRef = new BlockReference(position, blockId)
            {
                ScaleFactors = new Scale3d(1.0)
            };
            var blockRefId = modelSpace.AppendEntity(blockRef);
            transaction.AddNewlyCreatedDBObject(blockRef, true);

            var blockDef = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);
            if (blockDef.HasAttributeDefinitions)
            {
                foreach (ObjectId id in blockDef)
                {
                    if (!(transaction.GetObject(id, OpenMode.ForRead) is AttributeDefinition definition))
                    {
                        continue;
                    }

                    if (definition.Constant)
                    {
                        continue;
                    }

                    var reference = new AttributeReference();
                    reference.SetAttributeFromBlock(definition, blockRef.BlockTransform);
                    blockRef.AttributeCollection.AppendAttribute(reference);
                    transaction.AddNewlyCreatedDBObject(reference, true);
                }
            }

            SetBlockAttribute(blockRef, transaction, "SEC", key.Section);
            SetBlockAttribute(blockRef, transaction, "TWP", key.Township);
            SetBlockAttribute(blockRef, transaction, "RGE", key.Range);
            SetBlockAttribute(blockRef, transaction, "MER", key.Meridian);

            return blockRefId;
        }

        private static void SetBlockAttribute(BlockReference blockRef, Transaction transaction, string tag, string value)
        {
            if (blockRef == null || string.IsNullOrWhiteSpace(tag))
            {
                return;
            }

            foreach (ObjectId id in blockRef.AttributeCollection)
            {
                if (!(transaction.GetObject(id, OpenMode.ForWrite) is AttributeReference attr))
                {
                    continue;
                }

                if (string.Equals(attr.Tag, tag, StringComparison.OrdinalIgnoreCase))
                {
                    attr.TextString = value ?? string.Empty;
                }
            }
        }

        private static Point2d ResolveSectionLabelPosition(
            Polyline sectionPolyline,
            Dictionary<QuarterSelection, Polyline> quarterMap,
            QuarterAnchors anchors,
            List<LineSegment2d> lineworkBarriers,
            BlockTable blockTable,
            Transaction transaction,
            bool drawLsds,
            Point2d sectionCenter)
        {
            if (sectionPolyline == null)
            {
                return sectionCenter;
            }

            if (!TryGetSectionLabelBlockFootprint(blockTable, transaction, out var labelWidth, out var labelHeight))
            {
                labelWidth = 130.0;
                labelHeight = 70.0;
            }

            var requiredClearance = Math.Sqrt((labelWidth * 0.5 * labelWidth * 0.5) + (labelHeight * 0.5 * labelHeight * 0.5)) + 2.0;

            bool Fits(Point2d p)
            {
                if (!IsLabelBoxInsideSection(sectionPolyline, p, labelWidth, labelHeight))
                    return false;

                return GetLineworkClearance(p, lineworkBarriers) >= requiredClearance;
            }

            if (!drawLsds)
            {
                if (Fits(sectionCenter))
                {
                    return sectionCenter;
                }

                if (TryFindNonOverlapSectionPosition(sectionPolyline, sectionCenter, labelWidth, labelHeight, requiredClearance, lineworkBarriers, out var candidate))
                {
                    return candidate;
                }

                return GetLeastCongestedQuarterCenter(quarterMap, sectionPolyline, lineworkBarriers, sectionCenter);
            }

            if (TryFindNonOverlapSectionPosition(sectionPolyline, sectionCenter, labelWidth, labelHeight, requiredClearance, lineworkBarriers, out var openArea))
            {
                return openArea;
            }

            return GetLeastCongestedLsdCenter(sectionPolyline, anchors, lineworkBarriers, sectionCenter);
        }

        private static bool TryGetSectionLabelBlockFootprint(
            BlockTable blockTable,
            Transaction transaction,
            out double width,
            out double height)
        {
            width = 0;
            height = 0;

            const string blockName = "L-SECLBL";
            if (blockTable == null || transaction == null || !blockTable.Has(blockName))
            {
                return false;
            }

            try
            {
                var blockId = blockTable[blockName];
                var blockDef = (BlockTableRecord)transaction.GetObject(blockId, OpenMode.ForRead);

                var found = false;
                var minX = 0.0;
                var minY = 0.0;
                var maxX = 0.0;
                var maxY = 0.0;

                foreach (ObjectId id in blockDef)
                {
                    if (!(transaction.GetObject(id, OpenMode.ForRead) is Entity entity))
                    {
                        continue;
                    }

                    Extents3d extents;
                    try
                    {
                        extents = entity.GeometricExtents;
                    }
                    catch
                    {
                        continue;
                    }

                    if (!found)
                    {
                        minX = extents.MinPoint.X;
                        minY = extents.MinPoint.Y;
                        maxX = extents.MaxPoint.X;
                        maxY = extents.MaxPoint.Y;
                        found = true;
                    }
                    else
                    {
                        minX = Math.Min(minX, extents.MinPoint.X);
                        minY = Math.Min(minY, extents.MinPoint.Y);
                        maxX = Math.Max(maxX, extents.MaxPoint.X);
                        maxY = Math.Max(maxY, extents.MaxPoint.Y);
                    }
                }

                if (!found)
                {
                    return false;
                }

                width = Math.Max(1.0, maxX - minX);
                height = Math.Max(1.0, maxY - minY);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryFindNonOverlapSectionPosition(
            Polyline sectionPolyline,
            Point2d preferredCenter,
            double labelWidth,
            double labelHeight,
            double requiredClearance,
            List<LineSegment2d> lineworkBarriers,
            out Point2d location)
        {
            location = preferredCenter;
            var step = Math.Max(5.0, Math.Min(labelWidth, labelHeight) * 0.6);
            foreach (var p in GeometryUtils.GetSpiralOffsets(preferredCenter, step, 400))
            {
                if (!IsLabelBoxInsideSection(sectionPolyline, p, labelWidth, labelHeight))
                {
                    continue;
                }

                if (GetLineworkClearance(p, lineworkBarriers) >= requiredClearance)
                {
                    location = p;
                    return true;
                }
            }

            return false;
        }

        private static Point2d GetLeastCongestedQuarterCenter(
            Dictionary<QuarterSelection, Polyline> quarterMap,
            Polyline sectionPolyline,
            List<LineSegment2d> lineworkBarriers,
            Point2d fallback)
        {
            if (quarterMap == null || quarterMap.Count == 0)
            {
                return fallback;
            }

            var bestPoint = fallback;
            var bestScore = double.NegativeInfinity;

            foreach (var quarter in quarterMap.Values)
            {
                if (quarter == null)
                {
                    continue;
                }

                var p = GeometryUtils.GetSafeInteriorPoint(quarter);
                if (!GeometryUtils.IsPointInsidePolyline(sectionPolyline, p))
                {
                    continue;
                }

                var score = GetLineworkClearance(p, lineworkBarriers);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPoint = p;
                }
            }

            return bestPoint;
        }

        private static Point2d GetLeastCongestedLsdCenter(
            Polyline sectionPolyline,
            QuarterAnchors anchors,
            List<LineSegment2d> lineworkBarriers,
            Point2d fallback)
        {
            var eastUnit = GetUnitVector(anchors.Left, anchors.Right, new Vector2d(1, 0));
            var northUnit = GetUnitVector(anchors.Bottom, anchors.Top, new Vector2d(0, 1));
            var width = anchors.Left.GetDistanceTo(anchors.Right);
            var height = anchors.Bottom.GetDistanceTo(anchors.Top);
            if (width <= 1e-6 || height <= 1e-6)
            {
                return fallback;
            }

            var southWest = fallback - (eastUnit * (width * 0.5)) - (northUnit * (height * 0.5));
            var bestPoint = fallback;
            var bestScore = double.NegativeInfinity;

            for (var row = 0; row < 4; row++)
            {
                for (var col = 0; col < 4; col++)
                {
                    var p = southWest +
                            (eastUnit * (width * ((col + 0.5) / 4.0))) +
                            (northUnit * (height * ((row + 0.5) / 4.0)));
                    if (!GeometryUtils.IsPointInsidePolyline(sectionPolyline, p))
                    {
                        continue;
                    }

                    var score = GetLineworkClearance(p, lineworkBarriers);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestPoint = p;
                    }
                }
            }

            return bestPoint;
        }

        private static Point2d GetLeastCongestedPointInBoundary(
            Polyline boundary,
            List<LineSegment2d> lineworkBarriers,
            Point2d fallback)
        {
            if (boundary == null)
            {
                return fallback;
            }

            var seed = GeometryUtils.GetSafeInteriorPoint(boundary);
            var bestPoint = GeometryUtils.IsPointInsidePolyline(boundary, seed) ? seed : fallback;
            var bestScore = GetLineworkClearance(bestPoint, lineworkBarriers);

            var extents = boundary.GeometricExtents;
            var diag = extents.MaxPoint.DistanceTo(extents.MinPoint);
            var step = Math.Max(5.0, diag / 16.0);

            foreach (var p in GeometryUtils.GetSpiralOffsets(bestPoint, step, 220))
            {
                if (!GeometryUtils.IsPointInsidePolyline(boundary, p))
                {
                    continue;
                }

                var score = GetLineworkClearance(p, lineworkBarriers);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPoint = p;
                }
            }

            return bestPoint;
        }

        private static bool TryGetBestQuarterLsdCellCenter(
            Polyline quarterPolyline,
            Vector2d eastUnit,
            Vector2d northUnit,
            List<LineSegment2d> lineworkBarriers,
            Func<Point2d, bool> fitsPredicate,
            out Point2d bestCellCenter,
            out Point2d? bestFitCellCenter)
        {
            bestCellCenter = default;
            bestFitCellCenter = null;
            if (quarterPolyline == null)
            {
                return false;
            }

            var anchors = GetLsdAnchorsForQuarter(quarterPolyline, eastUnit, northUnit);
            var width = anchors.Left.GetDistanceTo(anchors.Right);
            var height = anchors.Bottom.GetDistanceTo(anchors.Top);
            if (width <= 1e-6 || height <= 1e-6)
            {
                return false;
            }

            var quarterCenter = new Point2d(
                0.5 * (anchors.Top.X + anchors.Bottom.X),
                0.5 * (anchors.Left.Y + anchors.Right.Y));
            var southWest = quarterCenter - (eastUnit * (width * 0.5)) - (northUnit * (height * 0.5));

            var haveAny = false;
            var bestAnyScore = double.NegativeInfinity;
            var bestFitScore = double.NegativeInfinity;

            for (var row = 0; row < 2; row++)
            {
                for (var col = 0; col < 2; col++)
                {
                    var p = southWest +
                            (eastUnit * (width * ((col + 0.5) / 2.0))) +
                            (northUnit * (height * ((row + 0.5) / 2.0)));
                    if (!GeometryUtils.IsPointInsidePolyline(quarterPolyline, p))
                    {
                        continue;
                    }

                    var score = GetLineworkClearance(p, lineworkBarriers);
                    if (!haveAny || score > bestAnyScore)
                    {
                        bestAnyScore = score;
                        bestCellCenter = p;
                        haveAny = true;
                    }

                    if (fitsPredicate != null && fitsPredicate(p) && score > bestFitScore)
                    {
                        bestFitScore = score;
                        bestFitCellCenter = p;
                    }
                }
            }

            return haveAny;
        }

        private static bool IsLabelBoxInsideSection(Polyline sectionPolyline, Point2d center, double width, double height)
        {
            var halfW = width * 0.5;
            var halfH = height * 0.5;
            var points = new[]
            {
                center,
                new Point2d(center.X - halfW, center.Y - halfH),
                new Point2d(center.X + halfW, center.Y - halfH),
                new Point2d(center.X - halfW, center.Y + halfH),
                new Point2d(center.X + halfW, center.Y + halfH),
                new Point2d(center.X, center.Y - halfH),
                new Point2d(center.X, center.Y + halfH),
                new Point2d(center.X - halfW, center.Y),
                new Point2d(center.X + halfW, center.Y)
            };

            foreach (var p in points)
            {
                if (!GeometryUtils.IsPointInsidePolyline(sectionPolyline, p))
                {
                    return false;
                }
            }

            return true;
        }

        private static double GetLineworkClearance(Point2d point, List<LineSegment2d> lineworkBarriers)
        {
            if (lineworkBarriers == null || lineworkBarriers.Count == 0)
            {
                return double.MaxValue;
            }

            var min = double.MaxValue;
            foreach (var segment in lineworkBarriers)
            {
                var d = DistancePointToSegment(point, segment.StartPoint, segment.EndPoint);
                if (d < min)
                {
                    min = d;
                }
            }

            return min;
        }

        private static double DistancePointToSegment(Point2d point, Point2d a, Point2d b)
        {
            var ab = b - a;
            var len2 = ab.DotProduct(ab);
            if (len2 <= 1e-12)
            {
                return point.GetDistanceTo(a);
            }

            var ap = point - a;
            var t = ap.DotProduct(ab) / len2;
            if (t < 0.0) t = 0.0;
            if (t > 1.0) t = 1.0;
            var nearest = new Point2d(a.X + ab.X * t, a.Y + ab.Y * t);
            return point.GetDistanceTo(nearest);
        }

        private static void AddPolylineSegments(List<LineSegment2d> destination, Polyline polyline)
        {
            if (destination == null || polyline == null || polyline.NumberOfVertices < 2)
            {
                return;
            }

            for (var i = 0; i < polyline.NumberOfVertices; i++)
            {
                var j = i + 1;
                if (j >= polyline.NumberOfVertices)
                {
                    if (!polyline.Closed)
                    {
                        break;
                    }

                    j = 0;
                }

                var a = polyline.GetPoint2dAt(i);
                var b = polyline.GetPoint2dAt(j);
                if (a.GetDistanceTo(b) > 1e-9)
                {
                    destination.Add(new LineSegment2d(a, b));
                }
            }
        }

        private struct QuarterAnchors
        {
            public QuarterAnchors(Point2d top, Point2d bottom, Point2d left, Point2d right)
            {
                Top = top;
                Bottom = bottom;
                Left = left;
                Right = right;
            }

            public Point2d Top { get; }
            public Point2d Bottom { get; }
            public Point2d Left { get; }
            public Point2d Right { get; }
        }

        private struct EdgeInfo
        {
            public int Index;
            public Point3d A;
            public Point3d B;
            public Point3d Mid;
            public Vector3d U;
            public double Len;
        }

        private struct ChainInfo
        {
            public int Start;
            public int SegCount;
            public double Score;
            public double TotalLen;
        }

        // ------------------------------------------------------------------
        // PLSR XML check
        // ------------------------------------------------------------------

        private sealed class PlsrActivity
        {
            public string DispNum { get; set; } = string.Empty;
            public string Owner { get; set; } = string.Empty;
            public DateTime? ExpiryDate { get; set; }
        }

        private sealed class PlsrQuarterData
        {
            public DateTime ReportDate { get; set; }
            public List<PlsrActivity> Activities { get; } = new List<PlsrActivity>();
        }

        private sealed class PlsrLabelEntry
        {
            public ObjectId Id { get; set; }
            public bool IsLeader { get; set; }
            public bool IsDimension { get; set; }
            public string Owner { get; set; } = string.Empty;
            public string DispNum { get; set; } = string.Empty;
            public string RawContents { get; set; } = string.Empty;
            public Point2d Location { get; set; }
        }

        private static readonly HashSet<string> PlsrDispositionPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "LOC","PLA","MSL","MLL","DML","EZE","PIL","RME","RML","DLO","ROE","RRD","DPI","DPL","VCE","DRS","SML","SME"
        };

        private static void RunPlsrCheck(
            Database database,
            Editor editor,
            Logger logger,
            ExcelLookup companyLookup,
            AtsBuildInput input,
            List<QuarterInfo> quarters)
        {
            if (input.PlsrXmlPaths == null || input.PlsrXmlPaths.Count == 0)
            {
                editor.WriteMessage("\nPLSR check skipped: no XML files selected.");
                logger.WriteLine("PLSR check skipped: no XML files selected.");
                return;
            }

            var notIncludedPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var quarterData = LoadPlsrQuarterData(input.PlsrXmlPaths, logger, notIncludedPrefixes);

            var requestedQuarterKeys = BuildRequestedQuarterKeys(input.SectionRequests);
            var missingQuarterKeys = requestedQuarterKeys.Where(k => !quarterData.ContainsKey(k)).ToList();

            var labelByQuarter = CollectPlsrLabels(database, quarters, logger);

            var summary = new StringBuilder();
            summary.AppendLine("PLSR Check Summary");
            summary.AppendLine("-------------------");

            int missingLabels = 0;
            int ownerMismatches = 0;
            int extraLabels = 0;
            int expiredTagged = 0;

            using (var tr = database.TransactionManager.StartTransaction())
            {
                foreach (var quarterKey in requestedQuarterKeys)
                {
                    labelByQuarter.TryGetValue(quarterKey, out var labels);
                    quarterData.TryGetValue(quarterKey, out var expected);

                    labels ??= new List<PlsrLabelEntry>();
                    var expectedActivities = expected?.Activities ?? new List<PlsrActivity>();

                    var expectedByDisp = new Dictionary<string, PlsrActivity>(StringComparer.OrdinalIgnoreCase);
                    foreach (var act in expectedActivities)
                    {
                        var normDisp = NormalizeDispNum(act.DispNum);
                        if (string.IsNullOrWhiteSpace(normDisp))
                            continue;
                        if (!expectedByDisp.ContainsKey(normDisp))
                            expectedByDisp.Add(normDisp, act);
                    }

                    var labelByDisp = new Dictionary<string, PlsrLabelEntry>(StringComparer.OrdinalIgnoreCase);
                    foreach (var label in labels)
                    {
                        var normDisp = NormalizeDispNum(label.DispNum);
                        if (string.IsNullOrWhiteSpace(normDisp))
                            continue;

                        var prefix = GetDispositionPrefix(normDisp);
                        if (string.IsNullOrWhiteSpace(prefix))
                            continue;

                        if (!PlsrDispositionPrefixes.Contains(prefix))
                        {
                            notIncludedPrefixes.Add(prefix);
                            continue;
                        }

                        if (!labelByDisp.ContainsKey(normDisp))
                            labelByDisp.Add(normDisp, label);
                    }

                    foreach (var pair in expectedByDisp)
                    {
                        var dispNum = pair.Key;
                        var act = pair.Value;
                        var prefix = GetDispositionPrefix(dispNum);
                        if (string.IsNullOrWhiteSpace(prefix))
                            continue;

                        if (!PlsrDispositionPrefixes.Contains(prefix))
                        {
                            notIncludedPrefixes.Add(prefix);
                            continue;
                        }

                        if (!labelByDisp.TryGetValue(dispNum, out var label))
                        {
                            missingLabels++;
                            summary.AppendLine($"Missing label: {dispNum} in {quarterKey}");
                            continue;
                        }

                        var labelOwner = NormalizeOwner(label.Owner);
                        var expectedOwner = NormalizeOwner(MapClientNameForCompare(companyLookup, act.Owner));
                        if (!string.Equals(labelOwner, expectedOwner, StringComparison.OrdinalIgnoreCase))
                        {
                            ownerMismatches++;
                            summary.AppendLine($"Owner mismatch: {dispNum} in {quarterKey} (label='{label.Owner}' vs xml='{act.Owner}')");
                        }

                        if (expected != null && act.ExpiryDate.HasValue && act.ExpiryDate.Value < expected.ReportDate)
                        {
                            if (TryApplyExpiredMarker(tr, label, out var updated))
                            {
                                expiredTagged++;
                                if (updated)
                                {
                                    // already tagged
                                }
                            }
                        }
                    }

                    foreach (var labelPair in labelByDisp)
                    {
                        if (!expectedByDisp.ContainsKey(labelPair.Key))
                        {
                            extraLabels++;
                            summary.AppendLine($"Not in PLSR: {labelPair.Key} in {quarterKey}");
                        }
                    }
                }

                tr.Commit();
            }

            if (missingQuarterKeys.Count > 0)
            {
                summary.AppendLine();
                summary.AppendLine("Quarters requested but not found in XML:");
                foreach (var q in missingQuarterKeys)
                    summary.AppendLine($"- {q}");
            }

            if (notIncludedPrefixes.Count > 0)
            {
                summary.AppendLine();
                summary.AppendLine("Not Included in check: " + string.Join(", ", notIncludedPrefixes.OrderBy(p => p)));
            }

            summary.AppendLine();
            summary.AppendLine($"Missing labels: {missingLabels}");
            summary.AppendLine($"Owner mismatches: {ownerMismatches}");
            summary.AppendLine($"Extra labels not in PLSR: {extraLabels}");
            summary.AppendLine($"Expired tags added: {expiredTagged}");

            var summaryText = summary.ToString().TrimEnd();
            try
            {
                System.Windows.Forms.MessageBox.Show(summaryText, "PLSR Check", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Information);
            }
            catch
            {
                editor.WriteMessage("\n" + summaryText);
            }

            WritePlsrLog(database, summaryText, logger);
        }

        private static Dictionary<string, PlsrQuarterData> LoadPlsrQuarterData(
            IEnumerable<string> xmlPaths,
            Logger logger,
            HashSet<string> notIncludedPrefixes)
        {
            var result = new Dictionary<string, PlsrQuarterData>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in xmlPaths)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    logger.WriteLine("PLSR XML missing: " + path);
                    continue;
                }

                if (!TryParsePlsrXml(path, logger, out var reportDate, out var activities))
                {
                    logger.WriteLine("PLSR XML parse failed: " + path);
                    continue;
                }

                var reportQuarterActivities = new Dictionary<string, List<PlsrActivity>>(StringComparer.OrdinalIgnoreCase);
                foreach (var activity in activities)
                {
                    var prefix = GetDispositionPrefix(NormalizeDispNum(activity.Item1.DispNum));
                    if (!string.IsNullOrWhiteSpace(prefix) && !PlsrDispositionPrefixes.Contains(prefix))
                    {
                        notIncludedPrefixes.Add(prefix);
                        continue;
                    }

                    foreach (var landId in activity.Item2)
                    {
                        foreach (var quarterKey in BuildQuarterKeysFromLandId(landId))
                        {
                            if (!reportQuarterActivities.TryGetValue(quarterKey, out var list))
                            {
                                list = new List<PlsrActivity>();
                                reportQuarterActivities[quarterKey] = list;
                            }

                            list.Add(activity.Item1);
                        }
                    }
                }

                foreach (var pair in reportQuarterActivities)
                {
                    if (!result.TryGetValue(pair.Key, out var existing) || reportDate > existing.ReportDate)
                    {
                        var data = new PlsrQuarterData { ReportDate = reportDate };
                        data.Activities.AddRange(pair.Value);
                        result[pair.Key] = data;
                    }
                    else if (reportDate == existing.ReportDate)
                    {
                        existing.Activities.AddRange(pair.Value);
                    }
                }
            }

            return result;
        }

        private static bool TryParsePlsrXml(
            string path,
            Logger logger,
            out DateTime reportDate,
            out List<(PlsrActivity, List<string>)> activities)
        {
            reportDate = DateTime.MinValue;
            activities = new List<(PlsrActivity, List<string>)>();

            try
            {
                var doc = XDocument.Load(path);
                if (doc.Root == null)
                    return false;

                XNamespace ns = "urn:srd.gov.ab.ca:glimps:data:reports";

                var reportDateText = doc.Root.Element(ns + "ReportRunDate")?.Value;
                if (!DateTime.TryParse(reportDateText, out reportDate))
                    reportDate = DateTime.MinValue;

                var activitiesElement = doc.Root.Element(ns + "Activities");
                if (activitiesElement == null)
                    return true;

                foreach (var activity in activitiesElement.Elements(ns + "Activity"))
                {
                    var dispNum = activity.Element(ns + "ActivityNumber")?.Value?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(dispNum))
                        continue;

                    var owner = activity.Element(ns + "ServiceClientName")?.Value?.Trim();
                    if (string.IsNullOrWhiteSpace(owner))
                    {
                        owner = activity
                            .Element(ns + "Clients")?
                            .Elements(ns + "ActivityClient")
                            .Select(c => c.Element(ns + "ClientName")?.Value?.Trim())
                            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
                    }

                    owner ??= string.Empty;

                    DateTime? expiryDate = null;
                    var expiryText = activity.Element(ns + "ExpiryDate")?.Value?.Trim();
                    if (DateTime.TryParse(expiryText, out var expiryParsed))
                        expiryDate = expiryParsed;

                    var landIds = new List<string>();
                    var lands = activity.Element(ns + "Lands");
                    if (lands != null)
                    {
                        foreach (var land in lands.Elements(ns + "ActivityLand"))
                        {
                            var landId = land.Element(ns + "LandId")?.Value?.Trim();
                            if (!string.IsNullOrWhiteSpace(landId))
                                landIds.Add(landId);
                        }
                    }

                    if (landIds.Count == 0)
                        continue;

                    activities.Add((new PlsrActivity
                    {
                        DispNum = dispNum,
                        Owner = owner,
                        ExpiryDate = expiryDate
                    }, landIds));
                }

                return true;
            }
            catch (System.Exception ex)
            {
                logger.WriteLine("PLSR XML read failed: " + ex.Message);
                return false;
            }
        }

        private static List<string> BuildQuarterKeysFromLandId(string landId)
        {
            var keys = new List<string>();
            if (!TryParseLandId(landId, out var meridian, out var range, out var township, out var section, out var quarter))
                return keys;

            if (string.IsNullOrWhiteSpace(quarter))
            {
                keys.Add(BuildQuarterKey(meridian, range, township, section, "NW"));
                keys.Add(BuildQuarterKey(meridian, range, township, section, "NE"));
                keys.Add(BuildQuarterKey(meridian, range, township, section, "SW"));
                keys.Add(BuildQuarterKey(meridian, range, township, section, "SE"));
                return keys;
            }

            keys.Add(BuildQuarterKey(meridian, range, township, section, quarter));
            return keys;
        }

        private static bool TryParseLandId(
            string landId,
            out string meridian,
            out string range,
            out string township,
            out string section,
            out string? quarter)
        {
            meridian = string.Empty;
            range = string.Empty;
            township = string.Empty;
            section = string.Empty;
            quarter = null;

            if (string.IsNullOrWhiteSpace(landId))
                return false;

            var tokens = landId.Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 4)
                return false;

            meridian = NormalizeMeridianToken(tokens[0]);
            range = NormalizeNumberToken(tokens[1]);
            township = NormalizeNumberToken(tokens[2]);
            section = NormalizeNumberToken(tokens[3]);

            if (tokens.Length >= 5)
            {
                var last = tokens[tokens.Length - 1].Trim().ToUpperInvariant();
                if (IsQuarterToken(last))
                {
                    quarter = last;
                }
            }

            return true;
        }

        private static bool IsQuarterToken(string token)
        {
            return token == "NW" || token == "NE" || token == "SW" || token == "SE";
        }

        private static List<string> BuildRequestedQuarterKeys(IEnumerable<SectionRequest> requests)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var request in requests)
            {
                foreach (var quarter in ExpandQuarterSelections(request.Quarter))
                {
                    var key = BuildQuarterKey(request.Key, quarter);
                    if (!string.IsNullOrWhiteSpace(key))
                        keys.Add(key);
                }
            }

            return keys.ToList();
        }

        private static IEnumerable<QuarterSelection> ExpandQuarterSelections(QuarterSelection selection)
        {
            switch (selection)
            {
                case QuarterSelection.NorthHalf:
                    return new[] { QuarterSelection.NorthWest, QuarterSelection.NorthEast };
                case QuarterSelection.SouthHalf:
                    return new[] { QuarterSelection.SouthWest, QuarterSelection.SouthEast };
                case QuarterSelection.WestHalf:
                    return new[] { QuarterSelection.NorthWest, QuarterSelection.SouthWest };
                case QuarterSelection.EastHalf:
                    return new[] { QuarterSelection.NorthEast, QuarterSelection.SouthEast };
                case QuarterSelection.All:
                    return new[]
                    {
                        QuarterSelection.NorthWest,
                        QuarterSelection.NorthEast,
                        QuarterSelection.SouthWest,
                        QuarterSelection.SouthEast
                    };
                case QuarterSelection.NorthWest:
                case QuarterSelection.NorthEast:
                case QuarterSelection.SouthWest:
                case QuarterSelection.SouthEast:
                    return new[] { selection };
                default:
                    return Array.Empty<QuarterSelection>();
            }
        }

        private static string BuildQuarterKey(SectionKey key, QuarterSelection quarter)
        {
            var meridian = NormalizeMeridianToken(key.Meridian);
            var range = NormalizeNumberToken(key.Range);
            var township = NormalizeNumberToken(key.Township);
            var section = NormalizeNumberToken(key.Section);
            var q = QuarterSelectionToToken(quarter);
            if (string.IsNullOrWhiteSpace(q))
                return string.Empty;
            return BuildQuarterKey(meridian, range, township, section, q);
        }

        private static string BuildQuarterKey(string meridian, string range, string township, string section, string quarter)
        {
            return $"{meridian}|{range}|{township}|{section}|{quarter}";
        }

        private static string QuarterSelectionToToken(QuarterSelection quarter)
        {
            return quarter switch
            {
                QuarterSelection.NorthWest => "NW",
                QuarterSelection.NorthEast => "NE",
                QuarterSelection.SouthWest => "SW",
                QuarterSelection.SouthEast => "SE",
                _ => string.Empty
            };
        }

        private static string NormalizeMeridianToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return string.Empty;

            var digits = new string(token.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var num))
                return num.ToString();

            return token.Trim().ToUpperInvariant();
        }

        private static string NormalizeNumberToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                return string.Empty;

            if (int.TryParse(token.Trim(), out var num))
                return num.ToString();

            return token.Trim().TrimStart('0');
        }

        private static Dictionary<string, List<PlsrLabelEntry>> CollectPlsrLabels(Database database, List<QuarterInfo> quarters, Logger logger)
        {
            var byQuarter = new Dictionary<string, List<PlsrLabelEntry>>(StringComparer.OrdinalIgnoreCase);
            if (quarters == null || quarters.Count == 0)
                return byQuarter;

            var quarterMap = new Dictionary<string, QuarterInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (var q in quarters)
            {
                if (q.SectionKey == null || q.Quarter == QuarterSelection.None)
                    continue;
                var key = BuildQuarterKey(q.SectionKey.Value, q.Quarter);
                if (!quarterMap.ContainsKey(key))
                    quarterMap.Add(key, q);
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    PlsrLabelEntry? entry = null;

                    var dbObject = tr.GetObject(id, OpenMode.ForRead);
                    if (dbObject is MText mtext)
                    {
                        var contents = mtext.Contents ?? string.Empty;
                        entry = BuildLabelEntry(id, false, contents, new Point2d(mtext.Location.X, mtext.Location.Y));
                    }
                    else if (dbObject is MLeader mleader)
                    {
                        var leaderText = mleader.MText;
                        if (leaderText != null)
                        {
                            var contents = leaderText.Contents ?? string.Empty;
                            var anchor = GetLeaderAnchorPoint(mleader, leaderText, logger);
                            entry = BuildLabelEntry(id, true, contents, anchor);
                        }
                    }
                    else if (dbObject is AlignedDimension aligned)
                    {
                        var contents = aligned.DimensionText ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(contents) && !string.Equals(contents.Trim(), "<>", StringComparison.Ordinal))
                        {
                            var location = GetDimensionAnchorPoint(aligned);
                            entry = BuildLabelEntry(id, false, contents, location, isDimension: true);
                        }
                    }

                    if (entry == null)
                        continue;

                    if (string.IsNullOrWhiteSpace(entry.DispNum) || string.IsNullOrWhiteSpace(entry.Owner))
                        continue;

                    var prefix = GetDispositionPrefix(NormalizeDispNum(entry.DispNum));
                    if (string.IsNullOrWhiteSpace(prefix) || !PlsrDispositionPrefixes.Contains(prefix))
                        continue;

                    bool assigned = false;
                    foreach (var pair in quarterMap)
                    {
                        if (GeometryUtils.IsPointInsidePolyline(pair.Value.Polyline, entry.Location))
                        {
                            if (!byQuarter.TryGetValue(pair.Key, out var list))
                            {
                                list = new List<PlsrLabelEntry>();
                                byQuarter[pair.Key] = list;
                            }

                            list.Add(entry);
                            assigned = true;
                            break;
                        }
                    }

                    _ = assigned;
                }

                tr.Commit();
            }
            return byQuarter;
        }

        private static PlsrLabelEntry? BuildLabelEntry(ObjectId id, bool isLeader, string contents, Point2d location, bool isDimension = false)
        {
            var lines = SplitMTextLines(contents);
            if (lines.Count < 2)
                return null;

            var owner = lines.FirstOrDefault() ?? string.Empty;
            var dispNum = lines.LastOrDefault() ?? string.Empty;

            return new PlsrLabelEntry
            {
                Id = id,
                IsLeader = isLeader,
                IsDimension = isDimension,
                Owner = owner,
                DispNum = dispNum,
                RawContents = contents,
                Location = location
            };
        }

        private static Point2d GetDimensionAnchorPoint(AlignedDimension dimension)
        {
            try
            {
                var textPos = dimension.TextPosition;
                return new Point2d(textPos.X, textPos.Y);
            }
            catch
            {
                // fallback below
            }

            try
            {
                var a = dimension.XLine1Point;
                var b = dimension.XLine2Point;
                return new Point2d((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5);
            }
            catch
            {
                return new Point2d(0.0, 0.0);
            }
        }

        private static Point2d GetLeaderAnchorPoint(MLeader leader, MText leaderText, Logger logger)
        {
            try
            {
                var type = leader.GetType();
                var allVertices = new List<Point3d>();
                string? source = null;

                void AddVertices(Point3dCollection? pts, string src)
                {
                    if (pts == null || pts.Count == 0)
                        return;
                    if (source == null)
                        source = src;
                    foreach (Point3d p in pts)
                        allVertices.Add(p);
                }

                int? TryGetInt(string name)
                {
                    var prop = type.GetProperty(name);
                    if (prop != null && prop.PropertyType == typeof(int))
                        return (int?)prop.GetValue(leader);
                    var method = type.GetMethod(name, Type.EmptyTypes);
                    if (method != null && method.ReturnType == typeof(int))
                        return (int?)method.Invoke(leader, Array.Empty<object>());
                    return null;
                }

                int? leaderCount = TryGetInt("NumLeaders") ?? TryGetInt("LeaderCount") ?? TryGetInt("NumberOfLeaders");
                var maxLeaders = leaderCount.HasValue ? Math.Max(1, leaderCount.Value) : 4;

                var method2 = type.GetMethod("GetLeaderLineVertices", new[] { typeof(int), typeof(int) });
                var method1 = type.GetMethod("GetLeaderLineVertices", new[] { typeof(int) });
                var method3 = type.GetMethod("GetLeaderLineVertices", new[] { typeof(int), typeof(int), typeof(bool) });

                var getLeaderIndexes = type.GetMethod("GetLeaderIndexes", Type.EmptyTypes);
                var getLeaderLineIndexes = type.GetMethod("GetLeaderLineIndexes", new[] { typeof(int) });
                var getFirstVertex = type.GetMethods().FirstOrDefault(m => m.Name == "GetFirstVertex");
                var getLastVertex = type.GetMethods().FirstOrDefault(m => m.Name == "GetLastVertex");
                var getVertex = type.GetMethods().FirstOrDefault(m => m.Name == "GetVertex");

                int? TryGetLineCount(int leaderIndex)
                {
                    var method = type.GetMethod("GetLeaderLineCount", new[] { typeof(int) });
                    if (method != null && method.ReturnType == typeof(int))
                        return (int?)method.Invoke(leader, new object[] { leaderIndex });
                    var prop = type.GetProperty("LeaderLineCount");
                    if (prop != null && prop.PropertyType == typeof(int))
                        return (int?)prop.GetValue(leader);
                    return null;
                }

                IEnumerable<int> EnumerateLeaderIndexes()
                {
                    if (getLeaderIndexes != null)
                    {
                        var result = getLeaderIndexes.Invoke(leader, Array.Empty<object>());
                        if (result is IEnumerable<int> ints)
                            return ints;
                        if (result is System.Collections.IEnumerable enumerable)
                            return enumerable.Cast<object>().Select(o => Convert.ToInt32(o));
                    }
                    return Enumerable.Range(0, maxLeaders);
                }

                IEnumerable<int> EnumerateLeaderLineIndexes(int leaderIndex, int maxLines)
                {
                    if (getLeaderLineIndexes != null)
                    {
                        var result = getLeaderLineIndexes.Invoke(leader, new object[] { leaderIndex });
                        if (result is IEnumerable<int> ints)
                            return ints;
                        if (result is System.Collections.IEnumerable enumerable)
                            return enumerable.Cast<object>().Select(o => Convert.ToInt32(o));
                    }
                    return Enumerable.Range(0, maxLines);
                }

                Point3d? TryInvokePoint3d(MethodInfo? method, params object[] args)
                {
                    if (method == null)
                        return null;
                    try
                    {
                        var paramCount = method.GetParameters().Length;
                        if (paramCount != args.Length)
                            return null;
                        var result = method.Invoke(leader, args);
                        if (result is Point3d p)
                            return p;
                    }
                    catch
                    {
                        // ignore
                    }
                    return null;
                }

                foreach (int leaderIndex in EnumerateLeaderIndexes())
                {
                    int? lineCount = TryGetLineCount(leaderIndex);
                    var maxLines = lineCount.HasValue ? Math.Max(1, lineCount.Value) : 4;

                    foreach (int lineIndex in EnumerateLeaderLineIndexes(leaderIndex, maxLines))
                    {
                        if (method2 != null)
                        {
                            var result = method2.Invoke(leader, new object[] { leaderIndex, lineIndex }) as Point3dCollection;
                            AddVertices(result, "GetLeaderLineVertices(int,int)");
                        }

                        if (method3 != null)
                        {
                            var resultFalse = method3.Invoke(leader, new object[] { leaderIndex, lineIndex, false }) as Point3dCollection;
                            AddVertices(resultFalse, "GetLeaderLineVertices(int,int,bool=false)");
                            var resultTrue = method3.Invoke(leader, new object[] { leaderIndex, lineIndex, true }) as Point3dCollection;
                            AddVertices(resultTrue, "GetLeaderLineVertices(int,int,bool=true)");
                        }

                        var first = TryInvokePoint3d(getFirstVertex, leaderIndex, lineIndex)
                            ?? TryInvokePoint3d(getFirstVertex, leaderIndex)
                            ?? TryInvokePoint3d(getFirstVertex, lineIndex);
                        if (first.HasValue)
                            AddVertices(new Point3dCollection { first.Value }, "GetFirstVertex");

                        var last = TryInvokePoint3d(getLastVertex, leaderIndex, lineIndex)
                            ?? TryInvokePoint3d(getLastVertex, leaderIndex)
                            ?? TryInvokePoint3d(getLastVertex, lineIndex);
                        if (last.HasValue)
                            AddVertices(new Point3dCollection { last.Value }, "GetLastVertex");

                        if (getVertex != null)
                        {
                            for (int v = 0; v < 6; v++)
                            {
                                var vtx = TryInvokePoint3d(getVertex, leaderIndex, lineIndex, v)
                                    ?? TryInvokePoint3d(getVertex, leaderIndex, v)
                                    ?? TryInvokePoint3d(getVertex, lineIndex, v);
                                if (vtx.HasValue)
                                    AddVertices(new Point3dCollection { vtx.Value }, "GetVertex");
                            }
                        }
                    }

                    if (method1 != null)
                    {
                        var result = method1.Invoke(leader, new object[] { leaderIndex }) as Point3dCollection;
                        AddVertices(result, "GetLeaderLineVertices(int)");
                    }
                }

                if (allVertices.Count == 0 && method1 != null)
                {
                    var result = method1.Invoke(leader, new object[] { 0 }) as Point3dCollection;
                    AddVertices(result, "GetLeaderLineVertices(int)@0");
                }

                var prop = type.GetProperty("LeaderLineVertices");
                if (prop != null)
                {
                    var val = prop.GetValue(leader);
                    if (val is Point3dCollection pts)
                        AddVertices(pts, "LeaderLineVertices");
                    else if (val is IEnumerable<Point3d> enumerable)
                    {
                        var pts2 = new Point3dCollection();
                        foreach (var p in enumerable)
                            pts2.Add(p);
                        AddVertices(pts2, "LeaderLineVertices(IEnumerable)");
                    }
                }

                if (allVertices.Count > 0)
                    return SelectLeaderHeadPoint(allVertices, leaderText.Location);
            }
            catch
            {
                // fall back to text location
            }

            return new Point2d(leaderText.Location.X, leaderText.Location.Y);
        }

        private static Point2d SelectLeaderHeadPoint(Point3dCollection vertices, Point3d labelLocation)
        {
            double bestDistance = double.MinValue;
            Point3d best = labelLocation;
            foreach (Point3d p in vertices)
            {
                double d = p.DistanceTo(labelLocation);
                if (d > bestDistance)
                {
                    bestDistance = d;
                    best = p;
                }
            }

            return new Point2d(best.X, best.Y);
        }

        private static Point2d SelectLeaderHeadPoint(IEnumerable<Point3d> vertices, Point3d labelLocation)
        {
            double bestDistance = double.MinValue;
            Point3d best = labelLocation;
            foreach (Point3d p in vertices)
            {
                double d = p.DistanceTo(labelLocation);
                if (d > bestDistance)
                {
                    bestDistance = d;
                    best = p;
                }
            }

            return new Point2d(best.X, best.Y);
        }

        private static List<string> SplitMTextLines(string contents)
        {
            if (string.IsNullOrWhiteSpace(contents))
                return new List<string>();

            var normalized = contents
                .Replace("\\P", "\n")
                .Replace("\\X", "\n")
                .Replace("\r", "\n");
            var raw = normalized.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var lines = new List<string>();
            foreach (var line in raw)
            {
                var cleaned = line.Replace("{", string.Empty).Replace("}", string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(cleaned))
                    lines.Add(cleaned);
            }

            return lines;
        }

        private static string NormalizeDispNum(string dispNum)
        {
            if (string.IsNullOrWhiteSpace(dispNum))
                return string.Empty;
            return Regex.Replace(dispNum, "\\s+", string.Empty).ToUpperInvariant();
        }

        private static string GetDispositionPrefix(string dispNum)
        {
            if (string.IsNullOrWhiteSpace(dispNum))
                return string.Empty;

            var match = Regex.Match(dispNum, "^[A-Z]{3}");
            return match.Success ? match.Value.ToUpperInvariant() : string.Empty;
        }

        private static string NormalizeOwner(string owner)
        {
            if (string.IsNullOrWhiteSpace(owner))
                return string.Empty;

            var upper = owner.ToUpperInvariant();
            var normalized = Regex.Replace(upper, "[^A-Z0-9]+", string.Empty);
            return normalized;
        }

        private static string MapClientNameForCompare(ExcelLookup lookup, string rawName)
        {
            if (string.IsNullOrWhiteSpace(rawName))
                return string.Empty;

            var entry = lookup.Lookup(rawName);
            if (entry != null && !string.IsNullOrWhiteSpace(entry.Value))
                return entry.Value;

            if (lookup.Values.Count > 0)
            {
                var target = NormalizeOwner(rawName);
                foreach (var value in lookup.Values)
                {
                    if (NormalizeOwner(value) == target)
                        return value;
                }
            }

            return rawName;
        }

        private static bool TryApplyExpiredMarker(Transaction tr, PlsrLabelEntry label, out bool alreadyTagged)
        {
            alreadyTagged = false;
            if (label == null)
                return false;

            var contents = label.RawContents ?? string.Empty;
            if (contents.IndexOf("(Expired)", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                alreadyTagged = true;
                return true;
            }

            var delimiter = contents.IndexOf("\\X", StringComparison.OrdinalIgnoreCase) >= 0 ? "\\X" : "\\P";
            var updated = contents + delimiter + "(Expired)";

            if (label.IsDimension)
            {
                if (tr.GetObject(label.Id, OpenMode.ForWrite) is Dimension dimension)
                {
                    dimension.DimensionText = updated;
                    return true;
                }
            }
            else if (label.IsLeader)
            {
                if (tr.GetObject(label.Id, OpenMode.ForWrite) is MLeader mleader)
                {
                    var mt = mleader.MText;
                    mt.Contents = updated;
                    mleader.MText = mt;
                    return true;
                }
            }
            else
            {
                if (tr.GetObject(label.Id, OpenMode.ForWrite) is MText mtext)
                {
                    mtext.Contents = updated;
                    return true;
                }
            }

            return false;
        }

        private static void WritePlsrLog(Database database, string text, Logger logger)
        {
            try
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                var docPath = doc?.Name ?? string.Empty;
                var folder = Path.GetDirectoryName(docPath);
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                {
                    folder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Environment.CurrentDirectory;
                }

                var logPath = Path.Combine(folder, "PLSR_Check.txt");
                File.WriteAllText(logPath, text);
                logger.WriteLine("PLSR check log written: " + logPath);
            }
            catch (System.Exception ex)
            {
                logger.WriteLine("PLSR log write failed: " + ex.Message);
            }
        }

        private static string BuildSectionKeyId(SectionKey key)
        {
            return $"Z{key.Zone}_SEC{NormalizeNumberToken(key.Section)}_TWP{NormalizeNumberToken(key.Township)}_RGE{NormalizeNumberToken(key.Range)}_MER{NormalizeNumberToken(key.Meridian)}";
        }

        private static void PlaceQuarterSectionLabels(
            Database database,
            IEnumerable<QuarterLabelInfo> quarterInfos,
            bool includeLsds,
            Logger logger)
        {
            if (database == null || quarterInfos == null)
                return;

            var uniqueQuarterInfos = new Dictionary<ObjectId, QuarterLabelInfo>();
            foreach (var info in quarterInfos)
            {
                if (info == null || info.QuarterId.IsNull || info.QuarterId.IsErased)
                    continue;

                if (!uniqueQuarterInfos.ContainsKey(info.QuarterId))
                    uniqueQuarterInfos.Add(info.QuarterId, info);
            }

            if (uniqueQuarterInfos.Count == 0)
                return;

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                EnsureLayer(database, transaction, "C-SYMBOL");
                EnsureLayer(database, transaction, "C-UNS-T");

                var labelCount = 0;
                var grouped = uniqueQuarterInfos.Values
                    .GroupBy(info => BuildSectionKeyId(info.SectionKey))
                    .ToList();

                foreach (var group in grouped)
                {
                    var sectionInfo = group.FirstOrDefault(g => g != null && !g.SectionPolylineId.IsNull && !g.SectionPolylineId.IsErased);
                    if (sectionInfo == null)
                    {
                        continue;
                    }

                    var sectionPolyline = transaction.GetObject(sectionInfo.SectionPolylineId, OpenMode.ForRead) as Polyline;
                    if (sectionPolyline == null)
                    {
                        continue;
                    }

                    if (!TryGetQuarterAnchors(sectionPolyline, out var sectionAnchors))
                    {
                        sectionAnchors = GetFallbackAnchors(sectionPolyline);
                    }

                    var sectionCenter = new Point2d(
                        0.5 * (sectionAnchors.Top.X + sectionAnchors.Bottom.X),
                        0.5 * (sectionAnchors.Left.Y + sectionAnchors.Right.Y));
                    var eastUnit = GetUnitVector(sectionAnchors.Left, sectionAnchors.Right, new Vector2d(1, 0));
                    var northUnit = GetUnitVector(sectionAnchors.Bottom, sectionAnchors.Top, new Vector2d(0, 1));

                    var barriers = new List<LineSegment2d>();
                    AddPolylineSegments(barriers, sectionPolyline);
                    CollectSectionLineBarriers(transaction, modelSpace, sectionPolyline, includeLsds, barriers);

                    foreach (var info in group)
                    {
                        var quarterPolyline = transaction.GetObject(info.QuarterId, OpenMode.ForRead) as Polyline;
                        if (quarterPolyline == null)
                            continue;

                        var quarterToken = FormatQuarterForSectionLabel(info.Quarter);
                        if (string.IsNullOrWhiteSpace(quarterToken))
                            continue;

                        var sectionDescriptor = BuildSectionDescriptor(info.SectionKey);
                        var normalizedSecType = NormalizeSecType(info.SecType);
                        var isLsec = string.Equals(normalizedSecType, "L-SEC", StringComparison.OrdinalIgnoreCase);

                        var extents = quarterPolyline.GeometricExtents;
                        var quarterCenter = new Point2d(
                            (extents.MinPoint.X + extents.MaxPoint.X) * 0.5,
                            (extents.MinPoint.Y + extents.MaxPoint.Y) * 0.5);

                        var primaryContents = isLsec
                            ? $"{quarterToken} Sec. {sectionDescriptor}"
                            : $"Theor. {quarterToken}\\PSec. {sectionDescriptor}";

                        EstimateMTextFootprint(primaryContents, 20.0, out var labelWidth, out var labelHeight);
                        var requiredClearance = Math.Sqrt((labelWidth * 0.5 * labelWidth * 0.5) + (labelHeight * 0.5 * labelHeight * 0.5)) + 2.0;

                        Point2d labelLocation;
                        bool FitsInQuarter(Point2d p)
                        {
                            if (!IsLabelBoxInsideSection(quarterPolyline, p, labelWidth, labelHeight))
                            {
                                return false;
                            }

                            return GetLineworkClearance(p, barriers) >= requiredClearance;
                        }

                        if (!includeLsds)
                        {
                            if (FitsInQuarter(quarterCenter))
                            {
                                labelLocation = quarterCenter;
                            }
                            else if (TryFindNonOverlapSectionPosition(quarterPolyline, quarterCenter, labelWidth, labelHeight, requiredClearance, barriers, out var openSpot))
                            {
                                labelLocation = openSpot;
                            }
                            else
                            {
                                labelLocation = quarterCenter;
                            }
                        }
                        else
                        {
                            if (TryGetBestQuarterLsdCellCenter(
                                quarterPolyline,
                                eastUnit,
                                northUnit,
                                barriers,
                                FitsInQuarter,
                                out var bestCellCenter,
                                out var bestFitCellCenter))
                            {
                                labelLocation = bestFitCellCenter ?? bestCellCenter;
                            }
                            else if (TryFindNonOverlapSectionPosition(quarterPolyline, quarterCenter, labelWidth, labelHeight, requiredClearance, barriers, out var openSpot))
                            {
                                labelLocation = openSpot;
                            }
                            else
                            {
                                labelLocation = GetLeastCongestedPointInBoundary(quarterPolyline, barriers, quarterCenter);
                            }
                        }

                        var center = new Point3d(labelLocation.X, labelLocation.Y, 0.0);
                        var primary = new MText
                        {
                            Layer = "C-SYMBOL",
                            ColorIndex = 3,
                            TextHeight = 20.0,
                            Location = center,
                            Attachment = AttachmentPoint.MiddleCenter,
                            Contents = primaryContents
                        };
                        modelSpace.AppendEntity(primary);
                        transaction.AddNewlyCreatedDBObject(primary, true);
                        labelCount++;

                        if (!isLsec)
                        {
                            var unsurveyed = new MText
                            {
                                Layer = "C-UNS-T",
                                ColorIndex = 3,
                                TextHeight = 16.0,
                                Location = new Point3d(center.X, center.Y - 34.0, center.Z),
                                Attachment = AttachmentPoint.TopCenter,
                                Contents = "UNSURVEYED\\PTERRITORY"
                            };
                            modelSpace.AppendEntity(unsurveyed);
                            transaction.AddNewlyCreatedDBObject(unsurveyed, true);
                        }
                    }
                }

                transaction.Commit();
                logger?.WriteLine($"Placed {labelCount} quarter section label(s).");
            }
        }

        private static void CollectSectionLineBarriers(
            Transaction transaction,
            BlockTableRecord modelSpace,
            Polyline sectionPolyline,
            bool includeLsds,
            List<LineSegment2d> barriers)
        {
            if (transaction == null || modelSpace == null || sectionPolyline == null || barriers == null)
            {
                return;
            }

            Extents3d sectionExtents;
            try
            {
                sectionExtents = sectionPolyline.GeometricExtents;
            }
            catch
            {
                return;
            }

            foreach (ObjectId id in modelSpace)
            {
                if (!(transaction.GetObject(id, OpenMode.ForRead) is Entity entity) || entity.IsErased)
                {
                    continue;
                }

                var layer = entity.Layer ?? string.Empty;
                if (string.Equals(layer, "C-SYMBOL", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(layer, "C-UNS-T", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Restrict to simple geometry types to avoid unstable extents calls on proxy/custom entities.
                if (!(entity is Line) && !(entity is Polyline))
                {
                    continue;
                }

                try
                {
                    if (entity is Line line)
                    {
                        var lineExtents = line.GeometricExtents;
                        if (!GeometryUtils.ExtentsIntersect(sectionExtents, lineExtents))
                        {
                            continue;
                        }

                        var mid = new Point2d(
                            (line.StartPoint.X + line.EndPoint.X) * 0.5,
                            (line.StartPoint.Y + line.EndPoint.Y) * 0.5);
                        if (!GeometryUtils.IsPointInsidePolyline(sectionPolyline, mid))
                        {
                            continue;
                        }

                        barriers.Add(new LineSegment2d(
                            new Point2d(line.StartPoint.X, line.StartPoint.Y),
                            new Point2d(line.EndPoint.X, line.EndPoint.Y)));
                        continue;
                    }

                    if (entity is Polyline polyline)
                    {
                        var polyExtents = polyline.GeometricExtents;
                        if (!GeometryUtils.ExtentsIntersect(sectionExtents, polyExtents))
                        {
                            continue;
                        }

                        var polyCenter = new Point2d(
                            (polyExtents.MinPoint.X + polyExtents.MaxPoint.X) * 0.5,
                            (polyExtents.MinPoint.Y + polyExtents.MaxPoint.Y) * 0.5);
                        if (!GeometryUtils.IsPointInsidePolyline(sectionPolyline, polyCenter))
                        {
                            continue;
                        }

                        AddPolylineSegments(barriers, polyline);
                    }
                }
                catch
                {
                    // Ignore problematic entities during final label barrier collection.
                }
            }
        }

        private static void EstimateMTextFootprint(string contents, double textHeight, out double width, out double height)
        {
            if (textHeight <= 0)
            {
                textHeight = 1.0;
            }

            var normalized = string.IsNullOrWhiteSpace(contents) ? "X" : contents;
            var lines = normalized.Split(new[] { "\\P" }, StringSplitOptions.None);
            var maxChars = Math.Max(1, lines.Max(line => line?.Length ?? 0));
            var lineCount = Math.Max(1, lines.Length);

            width = Math.Max(10.0, maxChars * textHeight * 0.62);
            height = Math.Max(10.0, lineCount * textHeight * 1.25);
        }

        private static string FormatQuarterForSectionLabel(QuarterSelection quarter)
        {
            return quarter switch
            {
                QuarterSelection.NorthWest => "N.W.1/4",
                QuarterSelection.NorthEast => "N.E.1/4",
                QuarterSelection.SouthWest => "S.W.1/4",
                QuarterSelection.SouthEast => "S.E.1/4",
                _ => string.Empty
            };
        }

        private static string BuildSectionDescriptor(SectionKey key)
        {
            var section = NormalizeNumberToken(key.Section);
            var township = NormalizeNumberToken(key.Township);
            var range = NormalizeNumberToken(key.Range);
            var meridian = NormalizeNumberToken(key.Meridian);
            return $"{section}-{township}-{range}-W.{meridian}M.";
        }
    }
}
