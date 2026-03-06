using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AtsBackgroundBuilder.Core;
using AtsBackgroundBuilder.Dispositions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using WinForms = System.Windows.Forms;

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
            public DateTime? VersionDate { get; set; }
            public DateTime? ActivityDate { get; set; }
            public List<DateTime> VersionDates { get; } = new List<DateTime>();
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

        private sealed class PlsrDispositionLabelOverride
        {
            public string Owner { get; set; } = string.Empty;
            public bool ShouldAddExpiredMarker { get; set; }
        }

        private enum PlsrIssueChangeType
        {
            None,
            TagExpired,
            UpdateOwner,
            CreateMissingLabel,
            CreateMissingLabelFromTemplate,
            CreateMissingLabelFromXml
        }

        private sealed class PlsrCheckIssue
        {
            public Guid Id { get; } = Guid.NewGuid();
            public string Type { get; set; } = string.Empty;
            public string QuarterKey { get; set; } = string.Empty;
            public string DispNum { get; set; } = string.Empty;
            public string CurrentValue { get; set; } = string.Empty;
            public string ExpectedValue { get; set; } = string.Empty;
            public string Detail { get; set; } = string.Empty;
            public string SummaryLine { get; set; } = string.Empty;
            public PlsrIssueChangeType ChangeType { get; set; } = PlsrIssueChangeType.None;
            public PlsrLabelEntry? Label { get; set; }
            public DispositionInfo? Disposition { get; set; }
            public QuarterInfo? Quarter { get; set; }
            public string ProposedOwner { get; set; } = string.Empty;
            public string VersionDateStatus { get; set; } = "N/A";
            public bool ShouldAddExpiredMarker { get; set; }

            public bool IsActionable => ChangeType switch
            {
                PlsrIssueChangeType.UpdateOwner => Label != null,
                PlsrIssueChangeType.TagExpired => Label != null,
                PlsrIssueChangeType.CreateMissingLabel => Disposition != null && Quarter != null,
                PlsrIssueChangeType.CreateMissingLabelFromTemplate => Label != null && Quarter != null,
                PlsrIssueChangeType.CreateMissingLabelFromXml => Quarter != null,
                _ => false
            };
        }

        private sealed class PlsrScanResult
        {
            public HashSet<string> NotIncludedPrefixes { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, List<PlsrLabelEntry>> LabelByQuarter { get; set; } = new Dictionary<string, List<PlsrLabelEntry>>(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, List<DispositionInfo>> DispositionsByDispNum { get; set; } = new Dictionary<string, List<DispositionInfo>>(StringComparer.OrdinalIgnoreCase);
            public List<PlsrCheckIssue> Issues { get; } = new List<PlsrCheckIssue>();
            public bool AllowTextOnlyFallbackLabels { get; set; }
            public int MissingLabels { get; set; }
            public int OwnerMismatches { get; set; }
            public int ExtraLabels { get; set; }
            public int ExpiredCandidates { get; set; }
            public int SkippedTextOnlyFallbackLabels { get; set; }
            public List<string> SkippedTextOnlyFallbackExamples { get; } = new List<string>();
        }

        private sealed class PlsrApplyResult
        {
            public int OwnerUpdated { get; set; }
            public int ExpiredTagged { get; set; }
            public int MissingCreated { get; set; }
            public int AcceptedActionable { get; set; }
            public int IgnoredActionable { get; set; }
            public int ApplyErrors { get; set; }
        }

        private sealed class PlsrSummaryResult
        {
            public string SummaryText { get; set; } = string.Empty;
            public string WarningText { get; set; } = string.Empty;
            public bool ShouldShowWarning => !string.IsNullOrWhiteSpace(WarningText);
        }

        private sealed class PlsrAcceptedActionBuckets
        {
            public List<PlsrCheckIssue> CreateMissingIssues { get; } = new List<PlsrCheckIssue>();
            public List<PlsrCheckIssue> CreateMissingTemplateIssues { get; } = new List<PlsrCheckIssue>();
            public List<PlsrCheckIssue> CreateMissingXmlIssues { get; } = new List<PlsrCheckIssue>();

            public bool HasMissingCreateActions =>
                CreateMissingIssues.Count > 0 ||
                CreateMissingTemplateIssues.Count > 0 ||
                CreateMissingXmlIssues.Count > 0;
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
            List<QuarterInfo> quarters,
            List<DispositionInfo> dispositions,
            Config config)
        {
            var stage = "start";
            void SetStage(string nextStage)
            {
                stage = nextStage;
                logger.WriteLine("PLSR stage: " + stage);
            }

            try
            {
            SetStage("validate_inputs");
            if (input.PlsrXmlPaths == null || input.PlsrXmlPaths.Count == 0)
            {
                editor.WriteMessage("\nPLSR check skipped: no XML files selected.");
                logger.WriteLine("PLSR check skipped: no XML files selected.");
                return;
            }

            var scanResult = RunPlsrScan(database, logger, companyLookup, input, quarters, dispositions, SetStage);
            var labelByQuarter = scanResult.LabelByQuarter;
            var dispositionsByDispNum = scanResult.DispositionsByDispNum;
            var issues = scanResult.Issues;

            SetStage("review_dialog");
            var acceptedIssueIds = ShowPlsrReviewDialog(issues, logger);

            var applyResult = RunPlsrApply(
                database,
                editor,
                logger,
                config,
                input,
                issues,
                labelByQuarter,
                dispositionsByDispNum,
                acceptedIssueIds,
                SetStage);
            SetStage("summary");
            var summaryResult = BuildPlsrSummary(scanResult, applyResult);
            if (summaryResult.ShouldShowWarning)
            {
                try
                {
                    WinForms.MessageBox.Show(
                        summaryResult.WarningText,
                        "PLSR Label Warning",
                        WinForms.MessageBoxButtons.OK,
                        WinForms.MessageBoxIcon.Warning);
                }
                catch
                {
                    editor.WriteMessage("\n" + summaryResult.WarningText);
                }
            }

            SetStage("show_summary");
            logger.WriteLine("PLSR summary popup suppressed; review was already shown in PLSR Review grid.");
            editor.WriteMessage("\nPLSR check complete. Summary written to PLSR_Check.txt.");

            SetStage("write_log");
            WritePlsrLog(database, summaryResult.SummaryText, logger);
            }
            catch (System.Exception ex)
            {
                var failureText = $"PLSR check failed at stage '{stage}': {ex.Message}";
                logger.WriteLine(failureText);
                logger.WriteLine(ex.ToString());
                try
                {
                    editor.WriteMessage("\n" + failureText);
                }
                catch
                {
                    // best-effort message
                }

                WritePlsrLog(database, failureText, logger);
            }
        }

        private static PlsrScanResult RunPlsrScan(
            Database database,
            Logger logger,
            ExcelLookup companyLookup,
            AtsBuildInput input,
            List<QuarterInfo> quarters,
            List<DispositionInfo> dispositions,
            Action<string> setStage)
        {
            var result = new PlsrScanResult();

            setStage("load_xml");
            var quarterData = LoadPlsrQuarterData(input.PlsrXmlPaths, logger, result.NotIncludedPrefixes);

            setStage("build_quarter_keys");
            var requestedQuarterKeys = BuildRequestedQuarterKeys(input.SectionRequests);
            var missingQuarterKeys = requestedQuarterKeys.Where(k => !quarterData.ContainsKey(k)).ToList();

            setStage("collect_labels");
            result.LabelByQuarter = CollectPlsrLabels(database, quarters, logger);
            var labelTemplatesByDispNum = BuildLabelTemplatesByDispNum(result.LabelByQuarter);
            var defaultTemplateLabel = labelTemplatesByDispNum.Values.FirstOrDefault();
            result.AllowTextOnlyFallbackLabels = IsPlsrTextOnlyFallbackLabelsEnabled();
            var quarterByKey = BuildQuarterInfoByKey(quarters);
            result.DispositionsByDispNum = IndexDispositionsByDispNum(dispositions);
            var dispositionSourceCache = new Dictionary<string, DispositionInfo?>(StringComparer.OrdinalIgnoreCase);

            foreach (var quarterKey in requestedQuarterKeys)
            {
                result.LabelByQuarter.TryGetValue(quarterKey, out var labels);
                quarterData.TryGetValue(quarterKey, out var expected);

                var labelsForQuarter = labels ?? new List<PlsrLabelEntry>();
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
                foreach (var label in labelsForQuarter)
                {
                    var normDisp = NormalizeDispNum(label.DispNum);
                    if (string.IsNullOrWhiteSpace(normDisp))
                        continue;

                    var prefix = GetDispositionPrefix(normDisp);
                    if (string.IsNullOrWhiteSpace(prefix))
                        continue;

                    if (!PlsrDispositionPrefixes.Contains(prefix))
                    {
                        result.NotIncludedPrefixes.Add(prefix);
                        continue;
                    }

                    if (!labelByDisp.ContainsKey(normDisp))
                        labelByDisp.Add(normDisp, label);
                }

                foreach (var pair in expectedByDisp)
                {
                    var dispNum = pair.Key;
                    var act = pair.Value;
                    if (act == null)
                    {
                        continue;
                    }

                    var displayDispNum = ResolveIssueDisplayDispNum(act.DispNum, dispNum);
                    var prefix = GetDispositionPrefix(dispNum);
                    if (string.IsNullOrWhiteSpace(prefix))
                        continue;

                    if (!PlsrDispositionPrefixes.Contains(prefix))
                    {
                        result.NotIncludedPrefixes.Add(prefix);
                        continue;
                    }

                    DispositionInfo? versionDateSourceDisposition = null;
                    if (quarterByKey.TryGetValue(quarterKey, out var versionDateQuarter) &&
                        versionDateQuarter != null &&
                        TryFindDispositionSourceForQuarterDisp(
                            quarterKey,
                            versionDateQuarter,
                            dispNum,
                            result.DispositionsByDispNum,
                            dispositionSourceCache,
                            logger,
                            out var versionDateDispositionMatch) &&
                        versionDateDispositionMatch != null)
                    {
                        versionDateSourceDisposition = versionDateDispositionMatch;
                    }
                    var versionDateStatus = ResolvePlsrVersionDateStatus(versionDateSourceDisposition, act);
                    var odVersionDateDisplay = FormatDispositionDateFieldsForDisplay(versionDateSourceDisposition);
                    var xmlVersionDateDisplay = FormatPlsrExpectedVersionDateForDisplay(act);
                    var versionDateMismatchDetail = ResolvePlsrVersionDateMismatchDetail(act);
                    var shouldAddExpiredMarker = ShouldPlsrActivityRequireExpiredMarker(expected?.ReportDate, act);

                    if (!labelByDisp.TryGetValue(dispNum, out var label))
                    {
                        result.MissingLabels++;
                        var mappedOwnerForMissing = MapClientNameForCompare(companyLookup, act.Owner);
                        DispositionInfo? sourceDisposition = null;
                        QuarterInfo? sourceQuarter = null;
                        var hasDispositionSource = false;
                        var usedFallbackDispositionCandidate = false;
                        var hasTemplateSource = false;
                        var hasXmlFallbackSource = false;
                        var hasTextOnlyFallbackCandidate = false;
                        PlsrLabelEntry? templateLabel = null;
                        PlsrLabelEntry? xmlFallbackTemplateLabel = null;
                        DispositionInfo? fallbackDispositionCandidate = null;
                        if (result.DispositionsByDispNum.TryGetValue(dispNum, out var fallbackCandidates) &&
                            fallbackCandidates != null &&
                            fallbackCandidates.Count > 0)
                        {
                            fallbackDispositionCandidate = fallbackCandidates[0];
                        }

                        if (quarterByKey.TryGetValue(quarterKey, out var quarterMatch) && quarterMatch != null)
                        {
                            sourceQuarter = quarterMatch;
                            if (TryFindDispositionSourceForQuarterDisp(
                                quarterKey,
                                quarterMatch,
                                dispNum,
                                result.DispositionsByDispNum,
                                dispositionSourceCache,
                                logger,
                                out var dispositionMatch) &&
                                dispositionMatch != null)
                            {
                                sourceDisposition = dispositionMatch;
                                hasDispositionSource = true;
                            }
                            else if (fallbackDispositionCandidate != null)
                            {
                                sourceDisposition = fallbackDispositionCandidate;
                                hasDispositionSource = true;
                                usedFallbackDispositionCandidate = true;
                            }
                        }

                        if (!hasDispositionSource &&
                            result.AllowTextOnlyFallbackLabels &&
                            labelTemplatesByDispNum.TryGetValue(dispNum, out var templateCandidate) &&
                            templateCandidate != null)
                        {
                            templateLabel = templateCandidate;
                            hasTemplateSource = sourceQuarter != null;
                        }

                        if (!hasDispositionSource && !hasTemplateSource && sourceQuarter != null)
                        {
                            if (result.AllowTextOnlyFallbackLabels)
                            {
                                xmlFallbackTemplateLabel = labelsForQuarter.FirstOrDefault(l => l != null && !l.Id.IsNull && !l.Id.IsErased) ??
                                                           defaultTemplateLabel;
                                hasXmlFallbackSource = true;
                            }
                            else
                            {
                                hasTextOnlyFallbackCandidate =
                                    (labelTemplatesByDispNum.TryGetValue(dispNum, out var textTemplate) && textTemplate != null) ||
                                    defaultTemplateLabel != null ||
                                    labelsForQuarter.Any(l => l != null && !l.Id.IsNull && !l.Id.IsErased);
                            }
                        }

                        if (!hasDispositionSource && !result.AllowTextOnlyFallbackLabels && hasTextOnlyFallbackCandidate)
                        {
                            result.SkippedTextOnlyFallbackLabels++;
                            result.SkippedTextOnlyFallbackExamples.Add($"{displayDispNum} in {quarterKey}");
                        }

                        var changeType = PlsrIssueChangeType.None;
                        if (hasDispositionSource)
                        {
                            changeType = PlsrIssueChangeType.CreateMissingLabel;
                        }
                        else if (hasTemplateSource)
                        {
                            changeType = PlsrIssueChangeType.CreateMissingLabelFromTemplate;
                        }
                        else if (hasXmlFallbackSource)
                        {
                            changeType = PlsrIssueChangeType.CreateMissingLabelFromXml;
                        }

                        var missingLabelVersionDateStatus = hasDispositionSource
                            ? ResolvePlsrVersionDateStatus(sourceDisposition, act)
                            : versionDateStatus;
                        var missingLabelDetail = hasDispositionSource
                            ? (usedFallbackDispositionCandidate
                                ? "No label found in this quarter. Create missing label if accepted (using fallback source candidate)."
                                : "No label found in this quarter. Create missing label if accepted.")
                            : (hasTemplateSource
                                ? "No matching source disposition geometry in this quarter. Create missing label from an existing disposition label template."
                                : (hasXmlFallbackSource
                                    ? "No source disposition geometry was found. Create missing XML-based label in this quarter."
                                    : (!result.AllowTextOnlyFallbackLabels && hasTextOnlyFallbackCandidate
                                        ? "No source disposition geometry was found. Text-only fallback label creation is disabled to prevent floating MText labels."
                                        : "No label found in this quarter and no source disposition geometry was found.")));
                        if (shouldAddExpiredMarker && changeType != PlsrIssueChangeType.None)
                        {
                            missingLabelDetail += " Created label will include '(Expired)'.";
                        }

                        result.Issues.Add(new PlsrCheckIssue
                        {
                            Type = "Missing label",
                            QuarterKey = quarterKey,
                            DispNum = displayDispNum,
                            CurrentValue = "(none)",
                            ExpectedValue = mappedOwnerForMissing ?? string.Empty,
                            Detail = missingLabelDetail,
                            SummaryLine = $"Missing label: {displayDispNum} in {quarterKey}",
                            ChangeType = changeType,
                            Label = hasTemplateSource ? templateLabel : (hasXmlFallbackSource ? xmlFallbackTemplateLabel : null),
                            Disposition = hasDispositionSource ? sourceDisposition : null,
                            Quarter = (hasDispositionSource || hasTemplateSource || hasXmlFallbackSource) ? sourceQuarter : null,
                            VersionDateStatus = missingLabelVersionDateStatus,
                            ShouldAddExpiredMarker = shouldAddExpiredMarker
                        });
                        continue;
                    }

                    var hasBehaviorTriggerIssue = false;
                    var mappedOwner = MapClientNameForCompare(companyLookup, act.Owner);
                    var labelOwner = NormalizeOwner(label.Owner);
                    var expectedOwner = NormalizeOwner(mappedOwner);
                    if (!string.Equals(labelOwner, expectedOwner, StringComparison.OrdinalIgnoreCase))
                    {
                        hasBehaviorTriggerIssue = true;
                        result.OwnerMismatches++;
                        result.Issues.Add(new PlsrCheckIssue
                        {
                            Type = "Owner mismatch",
                            QuarterKey = quarterKey,
                            DispNum = displayDispNum,
                            CurrentValue = label.Owner ?? string.Empty,
                            ExpectedValue = mappedOwner ?? string.Empty,
                            Detail = "Label owner differs from PLSR XML owner.",
                            SummaryLine = $"Owner mismatch: {displayDispNum} in {quarterKey} (label='{label.Owner}' vs xml='{act.Owner}')",
                            ChangeType = PlsrIssueChangeType.UpdateOwner,
                            Label = label,
                            ProposedOwner = mappedOwner ?? string.Empty,
                            VersionDateStatus = versionDateStatus
                        });
                    }

                    if (shouldAddExpiredMarker)
                    {
                        if (!HasExpiredMarker(label.RawContents))
                        {
                            hasBehaviorTriggerIssue = true;
                            result.ExpiredCandidates++;
                            var expiredExpectedValue = act.ExpiryDate.HasValue && expected != null
                                ? $"Expiry {act.ExpiryDate.Value:yyyy-MM-dd} < report {expected.ReportDate:yyyy-MM-dd}"
                                : "Expired in current PLSR report.";
                            result.Issues.Add(new PlsrCheckIssue
                            {
                                Type = "Expired in PLSR",
                                QuarterKey = quarterKey,
                                DispNum = displayDispNum,
                                CurrentValue = FlattenMTextForDisplay(label.RawContents ?? string.Empty),
                                ExpectedValue = expiredExpectedValue,
                                Detail = "Append '(Expired)' to this label.",
                                SummaryLine = $"Expired in PLSR: {displayDispNum} in {quarterKey}",
                                ChangeType = PlsrIssueChangeType.TagExpired,
                                Label = label,
                                VersionDateStatus = versionDateStatus
                            });
                        }
                    }
                    if (!hasBehaviorTriggerIssue &&
                        string.Equals(versionDateStatus, "NON-MATCH", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Issues.Add(new PlsrCheckIssue
                        {
                            Type = "Version date mismatch",
                            QuarterKey = quarterKey,
                            DispNum = displayDispNum,
                            CurrentValue = odVersionDateDisplay,
                            ExpectedValue = xmlVersionDateDisplay,
                            Detail = versionDateMismatchDetail,
                            SummaryLine = $"Version date mismatch: {displayDispNum} in {quarterKey} (od='{odVersionDateDisplay}' vs xml='{xmlVersionDateDisplay}')",
                            ChangeType = PlsrIssueChangeType.None,
                            Label = null,
                            VersionDateStatus = versionDateStatus
                        });
                    }

                }

                foreach (var labelPair in labelByDisp)
                {
                    var extraLabel = labelPair.Value;
                    if (extraLabel == null)
                    {
                        continue;
                    }

                    if (!expectedByDisp.ContainsKey(labelPair.Key))
                    {
                        var displayDispNum = ResolveIssueDisplayDispNum(extraLabel.DispNum, labelPair.Key);
                        result.ExtraLabels++;
                        result.Issues.Add(new PlsrCheckIssue
                        {
                            Type = "Not in PLSR",
                            QuarterKey = quarterKey,
                            DispNum = displayDispNum,
                            CurrentValue = extraLabel.Owner ?? string.Empty,
                            ExpectedValue = "(none)",
                            Detail = "Label exists in drawing but not in current PLSR data.",
                            SummaryLine = $"Not in PLSR: {displayDispNum} in {quarterKey}",
                            ChangeType = PlsrIssueChangeType.None,
                            Label = null
                        });
                    }
                }
            }

            foreach (var missingQuarter in missingQuarterKeys)
            {
                result.Issues.Add(new PlsrCheckIssue
                {
                    Type = "Missing quarter in XML",
                    QuarterKey = missingQuarter,
                    DispNum = "N/A",
                    CurrentValue = string.Empty,
                    ExpectedValue = string.Empty,
                    Detail = "Requested quarter not found in selected PLSR XML files.",
                    SummaryLine = $"Quarter missing in XML: {missingQuarter}",
                    ChangeType = PlsrIssueChangeType.None,
                    Label = null
                });
            }

            ConsolidateRepeatedVersionDateMismatchIssues(result.Issues);
            return result;
        }

        private static PlsrApplyResult RunPlsrApply(
            Database database,
            Editor editor,
            Logger logger,
            Config config,
            AtsBuildInput input,
            List<PlsrCheckIssue> issues,
            Dictionary<string, List<PlsrLabelEntry>> labelByQuarter,
            Dictionary<string, List<DispositionInfo>> dispositionsByDispNum,
            HashSet<Guid> acceptedIssueIds,
            Action<string> setStage)
        {
            var result = new PlsrApplyResult();
            var actionBuckets = new PlsrAcceptedActionBuckets();

            setStage("apply_actions");
            ApplyAcceptedPlsrActions(database, logger, issues, acceptedIssueIds, result, actionBuckets);

            setStage("create_missing_labels");
            if (!actionBuckets.HasMissingCreateActions)
            {
                return result;
            }

            ApplyAcceptedPlsrMissingLabelCreates(
                database,
                editor,
                logger,
                config,
                input,
                labelByQuarter,
                dispositionsByDispNum,
                result,
                actionBuckets);

            return result;
        }

        private static void ApplyAcceptedPlsrActions(
            Database database,
            Logger logger,
            List<PlsrCheckIssue> issues,
            HashSet<Guid> acceptedIssueIds,
            PlsrApplyResult result,
            PlsrAcceptedActionBuckets actionBuckets)
        {
            var issueById = issues.ToDictionary(i => i.Id);
            var effectiveAcceptedIssueIds = acceptedIssueIds != null
                ? new HashSet<Guid>(acceptedIssueIds)
                : new HashSet<Guid>();
            var forcedOwnerUpdateCount = 0;
            foreach (var issue in issues)
            {
                if (issue == null ||
                    issue.ChangeType != PlsrIssueChangeType.UpdateOwner ||
                    !issue.IsActionable)
                {
                    continue;
                }

                if (effectiveAcceptedIssueIds.Add(issue.Id))
                {
                    forcedOwnerUpdateCount++;
                }
            }

            if (forcedOwnerUpdateCount > 0)
            {
                logger.WriteLine(
                    $"PLSR owner enforcement: applying {forcedOwnerUpdateCount} owner update issue(s) from XML regardless of review selection.");
            }

            var decision = PlsrApplyDecisionEngine.Route(
                issues.Select(issue => new PlsrApplyDecisionItem
                {
                    IssueId = issue.Id,
                    IsActionable = issue.IsActionable,
                    ActionType = MapPlsrApplyDecisionActionType(issue.ChangeType)
                }),
                effectiveAcceptedIssueIds);
            result.AcceptedActionable = decision.AcceptedActionable;
            result.IgnoredActionable = decision.IgnoredActionable;

            using (var tr = database.TransactionManager.StartTransaction())
            {
                foreach (var routed in decision.AcceptedRoutedIssues)
                {
                    if (!issueById.TryGetValue(routed.IssueId, out var issue))
                    {
                        continue;
                    }

                    try
                    {
                        ApplyAcceptedPlsrAction(tr, issue, routed.ActionType, result, actionBuckets);
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex)
                    {
                        result.ApplyErrors++;
                        logger.WriteLine($"PLSR apply skipped ({issue.ChangeType}) for {issue.DispNum} in {issue.QuarterKey}: {ex.Message}");
                    }
                    catch (System.Exception ex)
                    {
                        result.ApplyErrors++;
                        logger.WriteLine($"PLSR apply skipped ({issue.ChangeType}) for {issue.DispNum} in {issue.QuarterKey}: {ex.Message}");
                    }
                }

                tr.Commit();
            }
        }

        private static void ApplyAcceptedPlsrAction(
            Transaction tr,
            PlsrCheckIssue issue,
            PlsrApplyDecisionActionType actionType,
            PlsrApplyResult result,
            PlsrAcceptedActionBuckets actionBuckets)
        {
            switch (actionType)
            {
                case PlsrApplyDecisionActionType.UpdateOwner:
                {
                    if (issue.Label != null && TryApplyOwnerUpdate(tr, issue.Label, issue.ProposedOwner, out _))
                    {
                        result.OwnerUpdated++;
                    }

                    break;
                }
                case PlsrApplyDecisionActionType.TagExpired:
                {
                    if (issue.Label != null && TryApplyExpiredMarker(tr, issue.Label, out _))
                    {
                        result.ExpiredTagged++;
                    }

                    break;
                }
                case PlsrApplyDecisionActionType.CreateMissingLabel:
                {
                    actionBuckets.CreateMissingIssues.Add(issue);
                    break;
                }
                case PlsrApplyDecisionActionType.CreateMissingLabelFromTemplate:
                {
                    actionBuckets.CreateMissingTemplateIssues.Add(issue);
                    break;
                }
                case PlsrApplyDecisionActionType.CreateMissingLabelFromXml:
                {
                    actionBuckets.CreateMissingXmlIssues.Add(issue);
                    break;
                }
            }
        }

        private static PlsrApplyDecisionActionType MapPlsrApplyDecisionActionType(PlsrIssueChangeType changeType)
        {
            switch (changeType)
            {
                case PlsrIssueChangeType.UpdateOwner:
                    return PlsrApplyDecisionActionType.UpdateOwner;
                case PlsrIssueChangeType.TagExpired:
                    return PlsrApplyDecisionActionType.TagExpired;
                case PlsrIssueChangeType.CreateMissingLabel:
                    return PlsrApplyDecisionActionType.CreateMissingLabel;
                case PlsrIssueChangeType.CreateMissingLabelFromTemplate:
                    return PlsrApplyDecisionActionType.CreateMissingLabelFromTemplate;
                case PlsrIssueChangeType.CreateMissingLabelFromXml:
                    return PlsrApplyDecisionActionType.CreateMissingLabelFromXml;
                default:
                    return PlsrApplyDecisionActionType.None;
            }
        }

        private static void ApplyAcceptedPlsrMissingLabelCreates(
            Database database,
            Editor editor,
            Logger logger,
            Config config,
            AtsBuildInput input,
            Dictionary<string, List<PlsrLabelEntry>> labelByQuarter,
            Dictionary<string, List<DispositionInfo>> dispositionsByDispNum,
            PlsrApplyResult result,
            PlsrAcceptedActionBuckets actionBuckets)
        {
            var existingDispNumsByQuarter = BuildExistingDispNumsByQuarter(labelByQuarter);
            var layerManager = new LayerManager(database);
            var placer = new LabelPlacer(database, editor, layerManager, config, logger, useAlignedDimensions: true);

            CreatePlsrMissingLabelsFromDispositions(
                logger,
                input,
                placer,
                dispositionsByDispNum,
                existingDispNumsByQuarter,
                actionBuckets.CreateMissingIssues,
                result);
            CreatePlsrMissingLabelsFromTemplates(
                database,
                logger,
                existingDispNumsByQuarter,
                actionBuckets.CreateMissingTemplateIssues,
                result);
            CreatePlsrMissingLabelsFromXml(
                database,
                logger,
                existingDispNumsByQuarter,
                actionBuckets.CreateMissingXmlIssues,
                result);
        }

        private static void CreatePlsrMissingLabelsFromDispositions(
            Logger logger,
            AtsBuildInput input,
            LabelPlacer placer,
            Dictionary<string, List<DispositionInfo>> dispositionsByDispNum,
            Dictionary<string, HashSet<string>> existingDispNumsByQuarter,
            List<PlsrCheckIssue> issues,
            PlsrApplyResult result)
        {
            foreach (var issue in issues)
            {
                if (issue.Quarter == null)
                {
                    continue;
                }

                try
                {
                    var normalizedIssueDispNum = NormalizeDispNum(issue.DispNum);
                    if (string.IsNullOrWhiteSpace(normalizedIssueDispNum) ||
                        !dispositionsByDispNum.TryGetValue(normalizedIssueDispNum, out var indexedCandidates) ||
                        indexedCandidates == null ||
                        indexedCandidates.Count == 0)
                    {
                        logger.WriteLine(
                            $"PLSR missing-label create skipped for {issue.DispNum} in {issue.QuarterKey}: no disposition candidates indexed.");
                        continue;
                    }

                    var indexedCandidateById = new Dictionary<string, DispositionInfo>(StringComparer.OrdinalIgnoreCase);
                    var orderedIndexedCandidateIds = new List<string>();
                    foreach (var candidate in indexedCandidates)
                    {
                        if (candidate == null || candidate.ObjectId.IsNull)
                        {
                            continue;
                        }

                        var candidateId = candidate.ObjectId.ToString();
                        if (string.IsNullOrWhiteSpace(candidateId))
                        {
                            continue;
                        }

                        if (!indexedCandidateById.ContainsKey(candidateId))
                        {
                            indexedCandidateById[candidateId] = candidate;
                            orderedIndexedCandidateIds.Add(candidateId);
                        }
                    }

                    var preferredCandidateId = issue.Disposition != null && !issue.Disposition.ObjectId.IsNull
                        ? issue.Disposition.ObjectId.ToString()
                        : null;
                    var selection = PlsrMissingLabelCandidateSelector.Select(
                        new PlsrMissingLabelCandidateSelectionInput
                        {
                            PreferredCandidateId = preferredCandidateId,
                            IndexedCandidateIds = orderedIndexedCandidateIds
                        });
                    if (!selection.HasCandidates)
                    {
                        logger.WriteLine(
                            $"PLSR missing-label create skipped for {issue.DispNum} in {issue.QuarterKey}: candidate list empty after dedupe.");
                        continue;
                    }

                    var candidateDispositions = new List<DispositionInfo>();
                    foreach (var candidateId in selection.OrderedCandidateIds)
                    {
                        if (indexedCandidateById.TryGetValue(candidateId, out var candidate))
                        {
                            candidateDispositions.Add(candidate);
                        }
                        else if (issue.Disposition != null && !issue.Disposition.ObjectId.IsNull &&
                                 string.Equals(candidateId, issue.Disposition.ObjectId.ToString(), StringComparison.OrdinalIgnoreCase))
                        {
                            candidateDispositions.Add(issue.Disposition);
                        }
                    }

                    if (candidateDispositions.Count == 0)
                    {
                        logger.WriteLine(
                            $"PLSR missing-label create skipped for {issue.DispNum} in {issue.QuarterKey}: candidate list empty after dedupe.");
                        continue;
                    }

                    var placement = placer.PlaceLabels(
                        new List<QuarterInfo> { issue.Quarter },
                        candidateDispositions,
                        input.CurrentClient,
                        existingDispNumsByQuarter);
                    result.MissingCreated += placement.LabelsPlaced;
                    if (placement.LabelsPlaced == 0)
                    {
                        logger.WriteLine(
                            $"PLSR missing-label create produced no label for {issue.DispNum} in {issue.QuarterKey} after trying {candidateDispositions.Count} candidate(s).");
                    }
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    result.ApplyErrors++;
                    logger.WriteLine($"PLSR missing-label create failed for {issue.DispNum} in {issue.QuarterKey}: {ex.Message}");
                }
                catch (System.Exception ex)
                {
                    result.ApplyErrors++;
                    logger.WriteLine($"PLSR missing-label create failed for {issue.DispNum} in {issue.QuarterKey}: {ex.Message}");
                }
            }
        }

        private static void CreatePlsrMissingLabelsFromTemplates(
            Database database,
            Logger logger,
            Dictionary<string, HashSet<string>> existingDispNumsByQuarter,
            List<PlsrCheckIssue> issues,
            PlsrApplyResult result)
        {
            foreach (var issue in issues)
            {
                try
                {
                    if (TryCreateMissingLabelFromTemplate(database, issue, existingDispNumsByQuarter, out var templateReason))
                    {
                        result.MissingCreated++;
                    }
                    else if (!string.IsNullOrWhiteSpace(templateReason))
                    {
                        logger.WriteLine(
                            $"PLSR missing-label template create skipped for {issue.DispNum} in {issue.QuarterKey}: {templateReason}");
                    }
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    result.ApplyErrors++;
                    logger.WriteLine($"PLSR missing-label template create failed for {issue.DispNum} in {issue.QuarterKey}: {ex.Message}");
                }
                catch (System.Exception ex)
                {
                    result.ApplyErrors++;
                    logger.WriteLine($"PLSR missing-label template create failed for {issue.DispNum} in {issue.QuarterKey}: {ex.Message}");
                }
            }
        }

        private static void CreatePlsrMissingLabelsFromXml(
            Database database,
            Logger logger,
            Dictionary<string, HashSet<string>> existingDispNumsByQuarter,
            List<PlsrCheckIssue> issues,
            PlsrApplyResult result)
        {
            foreach (var issue in issues)
            {
                try
                {
                    if (TryCreateMissingLabelFromXml(database, issue, existingDispNumsByQuarter, out var xmlReason))
                    {
                        result.MissingCreated++;
                    }
                    else if (!string.IsNullOrWhiteSpace(xmlReason))
                    {
                        logger.WriteLine(
                            $"PLSR missing-label XML create skipped for {issue.DispNum} in {issue.QuarterKey}: {xmlReason}");
                    }
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    result.ApplyErrors++;
                    logger.WriteLine($"PLSR missing-label XML create failed for {issue.DispNum} in {issue.QuarterKey}: {ex.Message}");
                }
                catch (System.Exception ex)
                {
                    result.ApplyErrors++;
                    logger.WriteLine($"PLSR missing-label XML create failed for {issue.DispNum} in {issue.QuarterKey}: {ex.Message}");
                }
            }
        }

        private static PlsrSummaryResult BuildPlsrSummary(PlsrScanResult scanResult, PlsrApplyResult applyResult)
        {
            var composeResult = PlsrSummaryComposer.Compose(
                new PlsrSummaryComposeInput
                {
                    IssueSummaryLines = scanResult.Issues.Select(i => i.SummaryLine),
                    NotIncludedPrefixes = scanResult.NotIncludedPrefixes,
                    MissingLabels = scanResult.MissingLabels,
                    OwnerMismatches = scanResult.OwnerMismatches,
                    ExtraLabels = scanResult.ExtraLabels,
                    ExpiredCandidates = scanResult.ExpiredCandidates,
                    MissingCreated = applyResult.MissingCreated,
                    SkippedTextOnlyFallbackLabels = scanResult.SkippedTextOnlyFallbackLabels,
                    OwnerUpdated = applyResult.OwnerUpdated,
                    ExpiredTagged = applyResult.ExpiredTagged,
                    AcceptedActionable = applyResult.AcceptedActionable,
                    IgnoredActionable = applyResult.IgnoredActionable,
                    ApplyErrors = applyResult.ApplyErrors,
                    AllowTextOnlyFallbackLabels = scanResult.AllowTextOnlyFallbackLabels,
                    SkippedTextOnlyFallbackExamples = scanResult.SkippedTextOnlyFallbackExamples
                });

            return new PlsrSummaryResult
            {
                SummaryText = composeResult.SummaryText,
                WarningText = composeResult.WarningText
            };
        }

        private static bool ShouldPlsrActivityRequireExpiredMarker(DateTime? reportDate, PlsrActivity? activity)
        {
            if (!reportDate.HasValue || reportDate.Value == DateTime.MinValue || activity == null || !activity.ExpiryDate.HasValue)
            {
                return false;
            }

            return activity.ExpiryDate.Value.Date < reportDate.Value.Date;
        }

        private static void ConsolidateRepeatedVersionDateMismatchIssues(List<PlsrCheckIssue> issues)
        {
            if (issues == null || issues.Count == 0)
            {
                return;
            }

            var groupedMismatches = issues
                .Where(issue => issue != null && string.Equals(issue.Type, "Version date mismatch", StringComparison.OrdinalIgnoreCase))
                .GroupBy(issue => NormalizeDispNum(issue.DispNum), StringComparer.OrdinalIgnoreCase)
                .Where(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1)
                .ToList();
            if (groupedMismatches.Count == 0)
            {
                return;
            }

            var replacementByOriginalId = new Dictionary<Guid, PlsrCheckIssue>();
            var skippedIds = new HashSet<Guid>();
            foreach (var mismatchGroup in groupedMismatches)
            {
                var mismatches = mismatchGroup.ToList();
                var representative = mismatches[0];
                var quarterKeys = mismatches
                    .Select(issue => issue.QuarterKey ?? string.Empty)
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var currentValues = mismatches
                    .Select(issue => issue.CurrentValue ?? string.Empty)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var expectedValues = mismatches
                    .Select(issue => issue.ExpectedValue ?? string.Empty)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                var mergedCurrentValue = currentValues.Count switch
                {
                    0 => representative.CurrentValue ?? string.Empty,
                    1 => currentValues[0],
                    _ => string.Join("; ", currentValues)
                };
                var mergedExpectedValue = expectedValues.Count switch
                {
                    0 => representative.ExpectedValue ?? string.Empty,
                    1 => expectedValues[0],
                    _ => string.Join("; ", expectedValues)
                };
                var mergedDetail = string.IsNullOrWhiteSpace(representative.Detail)
                    ? "Disposition OD VER_DATE differs from PLSR XML VersionDate."
                    : representative.Detail.Trim();
                if (quarterKeys.Count > 0)
                {
                    mergedDetail += " Quarters: " + string.Join(", ", quarterKeys) + ".";
                }

                replacementByOriginalId[representative.Id] = new PlsrCheckIssue
                {
                    Type = representative.Type,
                    QuarterKey = "MULTIPLE",
                    DispNum = representative.DispNum,
                    CurrentValue = mergedCurrentValue,
                    ExpectedValue = mergedExpectedValue,
                    Detail = mergedDetail,
                    SummaryLine = $"Version date mismatch: {representative.DispNum} in MULTIPLE (od='{mergedCurrentValue}' vs xml='{mergedExpectedValue}')",
                    ChangeType = PlsrIssueChangeType.None,
                    Label = null,
                    VersionDateStatus = representative.VersionDateStatus
                };

                foreach (var duplicate in mismatches.Skip(1))
                {
                    skippedIds.Add(duplicate.Id);
                }
            }

            var consolidated = new List<PlsrCheckIssue>(issues.Count);
            foreach (var issue in issues)
            {
                if (skippedIds.Contains(issue.Id))
                {
                    continue;
                }

                if (replacementByOriginalId.TryGetValue(issue.Id, out var replacement))
                {
                    consolidated.Add(replacement);
                    continue;
                }

                consolidated.Add(issue);
            }

            issues.Clear();
            issues.AddRange(consolidated);
        }

        private static Dictionary<string, QuarterInfo> BuildQuarterInfoByKey(List<QuarterInfo> quarters)
        {
            var byKey = new Dictionary<string, QuarterInfo>(StringComparer.OrdinalIgnoreCase);
            if (quarters == null || quarters.Count == 0)
            {
                return byKey;
            }

            foreach (var quarter in quarters)
            {
                if (quarter == null || quarter.SectionKey == null || quarter.Quarter == QuarterSelection.None)
                {
                    continue;
                }

                var key = BuildQuarterKey(quarter.SectionKey.Value, quarter.Quarter);
                if (!byKey.ContainsKey(key))
                {
                    byKey[key] = quarter;
                }
            }

            return byKey;
        }

        private static Dictionary<string, List<DispositionInfo>> IndexDispositionsByDispNum(List<DispositionInfo> dispositions)
        {
            var indexed = new Dictionary<string, List<DispositionInfo>>(StringComparer.OrdinalIgnoreCase);
            if (dispositions == null || dispositions.Count == 0)
            {
                return indexed;
            }

            foreach (var disposition in dispositions)
            {
                if (disposition == null || disposition.Polyline == null)
                {
                    continue;
                }

                var dispNum = NormalizeDispNum(disposition.DispNumFormatted);
                if (string.IsNullOrWhiteSpace(dispNum))
                {
                    continue;
                }

                if (!indexed.TryGetValue(dispNum, out var list))
                {
                    list = new List<DispositionInfo>();
                    indexed[dispNum] = list;
                }

                list.Add(disposition);
            }

            return indexed;
        }

        private static bool TryFindDispositionSourceForQuarterDisp(
            string quarterKey,
            QuarterInfo quarter,
            string dispNum,
            Dictionary<string, List<DispositionInfo>> dispositionsByDispNum,
            Dictionary<string, DispositionInfo?> dispositionSourceCache,
            Logger logger,
            out DispositionInfo? matchedDisposition)
        {
            matchedDisposition = null;

            if (quarter == null ||
                quarter.Polyline == null ||
                string.IsNullOrWhiteSpace(quarterKey))
            {
                return false;
            }

            var normalizedDispNum = NormalizeDispNum(dispNum);
            if (string.IsNullOrWhiteSpace(normalizedDispNum))
            {
                return false;
            }

            var cacheKey = quarterKey + "|" + normalizedDispNum;
            if (dispositionSourceCache.TryGetValue(cacheKey, out var cached))
            {
                matchedDisposition = cached;
                return cached != null;
            }

            if (!dispositionsByDispNum.TryGetValue(normalizedDispNum, out var candidates) || candidates == null || candidates.Count == 0)
            {
                dispositionSourceCache[cacheKey] = null;
                return false;
            }

            var quarterBounds = quarter.Bounds;
            foreach (var candidate in candidates)
            {
                if (candidate == null || candidate.Polyline == null)
                {
                    continue;
                }

                if (!GeometryUtils.ExtentsIntersect(quarterBounds, candidate.Bounds))
                {
                    continue;
                }

                bool safePointInsideQuarter;
                try
                {
                    safePointInsideQuarter = GeometryUtils.IsPointInsidePolyline(quarter.Polyline, candidate.SafePoint);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    logger.WriteLine(
                        $"PLSR source-scan skipped candidate {normalizedDispNum} in {quarterKey} (safe-point test): {ex.Message}");
                    continue;
                }
                catch (System.Exception ex)
                {
                    logger.WriteLine(
                        $"PLSR source-scan skipped candidate {normalizedDispNum} in {quarterKey} (safe-point test): {ex.Message}");
                    continue;
                }

                if (safePointInsideQuarter)
                {
                    matchedDisposition = candidate;
                    dispositionSourceCache[cacheKey] = candidate;
                    return true;
                }

                bool overlapsQuarter;
                try
                {
                    overlapsQuarter = GeometryUtils.TryFindPointInsideBoth(quarter.Polyline, candidate.Polyline, out _);
                }
                catch (Autodesk.AutoCAD.Runtime.Exception ex)
                {
                    logger.WriteLine(
                        $"PLSR source-scan skipped candidate {normalizedDispNum} in {quarterKey} (overlap test): {ex.Message}");
                    continue;
                }
                catch (System.Exception ex)
                {
                    logger.WriteLine(
                        $"PLSR source-scan skipped candidate {normalizedDispNum} in {quarterKey} (overlap test): {ex.Message}");
                    continue;
                }

                if (!overlapsQuarter)
                {
                    continue;
                }

                matchedDisposition = candidate;
                dispositionSourceCache[cacheKey] = candidate;
                return true;
            }

            dispositionSourceCache[cacheKey] = null;
            return false;
        }

        private static string ResolvePlsrVersionDateStatus(DispositionInfo? sourceDisposition, PlsrActivity? activity)
        {
            if (sourceDisposition == null || activity == null)
            {
                return "N/A";
            }

            if (!TryParseDispositionVersionDate(sourceDisposition.OdVerDateRaw, out var odVersionDate))
            {
                return "N/A";
            }

            var candidateDates = new HashSet<DateTime>();
            if (activity.VersionDates != null && activity.VersionDates.Count > 0)
            {
                foreach (var versionDate in activity.VersionDates)
                {
                    candidateDates.Add(versionDate.Date);
                }
            }

            if (activity.VersionDate.HasValue)
            {
                candidateDates.Add(activity.VersionDate.Value.Date);
            }

            if (candidateDates.Count == 0)
            {
                return "N/A";
            }

            return candidateDates.Contains(odVersionDate.Date)
                ? "MATCH"
                : "NON-MATCH";
        }

        private static string FormatPlsrExpectedVersionDateForDisplay(PlsrActivity? activity)
        {
            if (activity == null)
            {
                return "N/A";
            }

            if (activity.VersionDates != null && activity.VersionDates.Count > 0)
            {
                var preferredVersionDate = activity.VersionDates.Max();
                return FormatPlsrVersionDateForDisplay(preferredVersionDate);
            }

            if (activity.VersionDate.HasValue)
            {
                return FormatPlsrVersionDateForDisplay(activity.VersionDate);
            }

            return "N/A";
        }

        private static string ResolvePlsrVersionDateMismatchDetail(PlsrActivity? activity)
        {
            _ = activity;
            return "Disposition OD VER_DATE differs from PLSR XML VersionDate.";
        }

        private static string FormatDispositionDateFieldsForDisplay(DispositionInfo? sourceDisposition)
        {
            if (sourceDisposition == null)
            {
                return "N/A";
            }

            var verDateDisplay = FormatDispositionVersionDateForDisplay(sourceDisposition.OdVerDateRaw);
            return string.Equals(verDateDisplay, "N/A", StringComparison.OrdinalIgnoreCase)
                ? "N/A"
                : $"VER_DATE={verDateDisplay}";
        }

        private static string FormatDispositionVersionDateForDisplay(string? rawValue)
        {
            if (TryParseDispositionVersionDate(rawValue, out var parsed))
            {
                return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }

            return "N/A";
        }

        private static string FormatPlsrVersionDateForDisplay(DateTime? value)
        {
            if (!value.HasValue)
            {
                return "N/A";
            }

            return value.Value.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private static bool TryParseDispositionVersionDate(string? rawValue, out DateTime versionDate)
        {
            versionDate = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return false;
            }

            var trimmed = rawValue.Trim();
            if (DateTime.TryParseExact(trimmed, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exactDate))
            {
                versionDate = exactDate.Date;
                return true;
            }

            var digitsOnly = new string(trimmed.Where(char.IsDigit).ToArray());
            if (digitsOnly.Length >= 8 &&
                DateTime.TryParseExact(digitsOnly.Substring(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var digitDate))
            {
                versionDate = digitDate.Date;
                return true;
            }

            if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var numericDate))
            {
                var rounded = Math.Round(numericDate).ToString("0", CultureInfo.InvariantCulture);
                if (rounded.Length >= 8 &&
                    DateTime.TryParseExact(rounded.Substring(0, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var numericParsedDate))
                {
                    versionDate = numericParsedDate.Date;
                    return true;
                }
            }

            DateTime parsedDate;
            if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsedDate) ||
                DateTime.TryParse(trimmed, out parsedDate))
            {
                versionDate = parsedDate.Date;
                return true;
            }

            return false;
        }

        private static bool TryParsePlsrXmlVersionDate(string? rawValue, out DateTime versionDate)
        {
            versionDate = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(rawValue))
            {
                return false;
            }

            var trimmed = rawValue.Trim();
            if (trimmed.Length >= 10 &&
                DateTime.TryParseExact(trimmed.Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var isoDate))
            {
                versionDate = isoDate.Date;
                return true;
            }

            DateTime parsedDate;
            if (DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsedDate) ||
                DateTime.TryParse(trimmed, out parsedDate))
            {
                versionDate = parsedDate.Date;
                return true;
            }

            return false;
        }

        private static Dictionary<string, HashSet<string>> BuildExistingDispNumsByQuarter(
            Dictionary<string, List<PlsrLabelEntry>> labelByQuarter)
        {
            var indexed = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            if (labelByQuarter == null || labelByQuarter.Count == 0)
            {
                return indexed;
            }

            foreach (var pair in labelByQuarter)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || pair.Value == null || pair.Value.Count == 0)
                {
                    continue;
                }

                if (!indexed.TryGetValue(pair.Key, out var dispNums))
                {
                    dispNums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    indexed[pair.Key] = dispNums;
                }

                foreach (var label in pair.Value)
                {
                    var normalized = NormalizeDispNum(label?.DispNum ?? string.Empty);
                    if (!string.IsNullOrWhiteSpace(normalized))
                    {
                        dispNums.Add(normalized);
                    }
                }
            }

            return indexed;
        }

        private static Dictionary<string, PlsrLabelEntry> BuildLabelTemplatesByDispNum(
            Dictionary<string, List<PlsrLabelEntry>> labelByQuarter)
        {
            var templates = new Dictionary<string, PlsrLabelEntry>(StringComparer.OrdinalIgnoreCase);
            if (labelByQuarter == null || labelByQuarter.Count == 0)
            {
                return templates;
            }

            foreach (var pair in labelByQuarter)
            {
                if (pair.Value == null || pair.Value.Count == 0)
                {
                    continue;
                }

                foreach (var label in pair.Value)
                {
                    if (label == null || label.Id.IsNull || label.Id.IsErased)
                    {
                        continue;
                    }

                    var normalizedDispNum = NormalizeDispNum(label.DispNum);
                    if (string.IsNullOrWhiteSpace(normalizedDispNum) || templates.ContainsKey(normalizedDispNum))
                    {
                        continue;
                    }

                    templates[normalizedDispNum] = label;
                }
            }

            return templates;
        }

        private static bool TryCreateMissingLabelFromTemplate(
            Database database,
            PlsrCheckIssue issue,
            Dictionary<string, HashSet<string>> existingDispNumsByQuarter,
            out string reason)
        {
            reason = string.Empty;

            if (database == null || issue == null || issue.Quarter == null || issue.Label == null)
            {
                reason = "required template or quarter data missing";
                return false;
            }

            var quarter = issue.Quarter;
            if (quarter.Polyline == null)
            {
                reason = "quarter geometry unavailable";
                return false;
            }

            var normalizedDispNum = NormalizeDispNum(issue.DispNum);
            var quarterKey = issue.QuarterKey;
            if (string.IsNullOrWhiteSpace(quarterKey) && quarter.SectionKey.HasValue && quarter.Quarter != QuarterSelection.None)
            {
                quarterKey = BuildQuarterKey(quarter.SectionKey.Value, quarter.Quarter);
            }

            if (!string.IsNullOrWhiteSpace(quarterKey) &&
                !string.IsNullOrWhiteSpace(normalizedDispNum) &&
                existingDispNumsByQuarter.TryGetValue(quarterKey, out var existingDispNums) &&
                existingDispNums != null &&
                existingDispNums.Contains(normalizedDispNum))
            {
                reason = "label already exists in quarter";
                return false;
            }

            var baseContents = issue.Label.RawContents ?? string.Empty;
            if (string.IsNullOrWhiteSpace(baseContents))
            {
                reason = "template label contents empty";
                return false;
            }

            var contents = ReplaceLabelOwnerInContents(baseContents, issue.ExpectedValue);
            contents = ReplaceLabelDispNumInContents(contents, ResolveIssueDisplayDispNum(issue.DispNum, normalizedDispNum));
            if (issue.ShouldAddExpiredMarker)
            {
                contents = LabelPlacer.AppendExpiredMarkerIfMissing(contents);
            }
            if (string.IsNullOrWhiteSpace(contents))
            {
                reason = "template label contents invalid";
                return false;
            }

            Point2d anchor;
            try
            {
                anchor = GeometryUtils.GetSafeInteriorPoint(quarter.Polyline);
            }
            catch
            {
                reason = "could not resolve quarter interior point";
                return false;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead, false);
                var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite, false);
                if (!(tr.GetObject(issue.Label.Id, OpenMode.ForRead, false) is Entity templateEntity) || templateEntity.IsErased)
                {
                    reason = "template entity not found";
                    tr.Commit();
                    return false;
                }

                var mtext = new MText();
                mtext.SetDatabaseDefaults(database);
                mtext.Contents = contents;
                mtext.Location = new Point3d(anchor.X, anchor.Y, 0.0);
                mtext.Layer = templateEntity.Layer;
                mtext.ColorIndex = templateEntity.ColorIndex > 0 ? templateEntity.ColorIndex : 256;

                ApplyTemplateTextFormatting(templateEntity, mtext);
                if (mtext.TextHeight <= 0.0)
                {
                    mtext.TextHeight = 7.0;
                }

                modelSpace.AppendEntity(mtext);
                tr.AddNewlyCreatedDBObject(mtext, true);
                tr.Commit();
            }

            if (!string.IsNullOrWhiteSpace(quarterKey) && !string.IsNullOrWhiteSpace(normalizedDispNum))
            {
                if (!existingDispNumsByQuarter.TryGetValue(quarterKey, out var dispNums) || dispNums == null)
                {
                    dispNums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    existingDispNumsByQuarter[quarterKey] = dispNums;
                }

                dispNums.Add(normalizedDispNum);
            }

            return true;
        }

        private static bool IsPlsrTextOnlyFallbackLabelsEnabled()
        {
            // Intentionally hard-disabled: text-only fallback labels create floating MText
            // (owner/disp only, no geometry/dimension anchor), which users have rejected.
            // Missing-label rows without source geometry should stay skipped and be warned.
            return false;
        }

        private static bool TryCreateMissingLabelFromXml(
            Database database,
            PlsrCheckIssue issue,
            Dictionary<string, HashSet<string>> existingDispNumsByQuarter,
            out string reason)
        {
            reason = string.Empty;

            if (database == null || issue == null || issue.Quarter == null)
            {
                reason = "required quarter data missing";
                return false;
            }

            var quarter = issue.Quarter;
            if (quarter.Polyline == null)
            {
                reason = "quarter geometry unavailable";
                return false;
            }

            var normalizedDispNum = NormalizeDispNum(issue.DispNum);
            var quarterKey = issue.QuarterKey;
            if (string.IsNullOrWhiteSpace(quarterKey) && quarter.SectionKey.HasValue && quarter.Quarter != QuarterSelection.None)
            {
                quarterKey = BuildQuarterKey(quarter.SectionKey.Value, quarter.Quarter);
            }

            if (!string.IsNullOrWhiteSpace(quarterKey) &&
                !string.IsNullOrWhiteSpace(normalizedDispNum) &&
                existingDispNumsByQuarter.TryGetValue(quarterKey, out var existingDispNums) &&
                existingDispNums != null &&
                existingDispNums.Contains(normalizedDispNum))
            {
                reason = "label already exists in quarter";
                return false;
            }

            var owner = issue.ExpectedValue?.Trim();
            if (string.IsNullOrWhiteSpace(owner))
            {
                owner = "UNKNOWN";
            }

            var dispLine = ResolveIssueDisplayDispNum(issue.DispNum, normalizedDispNum);
            if (string.IsNullOrWhiteSpace(dispLine))
            {
                reason = "disposition number missing";
                return false;
            }

            var contents = owner + "\\P" + dispLine;
            if (issue.ShouldAddExpiredMarker)
            {
                contents = LabelPlacer.AppendExpiredMarkerIfMissing(contents);
            }
            Point2d anchor;
            try
            {
                anchor = ResolveMissingLabelAnchor(quarter.Polyline, dispLine);
            }
            catch
            {
                reason = "could not resolve quarter interior point";
                return false;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead, false);
                var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite, false);

                Entity? templateEntity = null;
                if (issue.Label != null && !issue.Label.Id.IsNull)
                {
                    templateEntity = tr.GetObject(issue.Label.Id, OpenMode.ForRead, false) as Entity;
                    if (templateEntity != null && templateEntity.IsErased)
                    {
                        templateEntity = null;
                    }
                }

                var layerName = (templateEntity?.Layer ?? string.Empty).Trim();
                if (!IsDispositionTextLayer(layerName))
                {
                    layerName = "C-PLSR-T";
                }

                EnsureLayer(database, tr, layerName);

                var mtext = new MText();
                mtext.SetDatabaseDefaults(database);
                mtext.Contents = contents;
                mtext.Location = new Point3d(anchor.X, anchor.Y, 0.0);
                mtext.Layer = layerName;
                mtext.ColorIndex = templateEntity != null && templateEntity.ColorIndex > 0
                    ? templateEntity.ColorIndex
                    : 256;

                if (templateEntity != null)
                {
                    ApplyTemplateTextFormatting(templateEntity, mtext);
                }

                if (mtext.TextHeight <= 0.0)
                {
                    mtext.TextHeight = 7.0;
                }

                modelSpace.AppendEntity(mtext);
                tr.AddNewlyCreatedDBObject(mtext, true);
                tr.Commit();
            }

            if (!string.IsNullOrWhiteSpace(quarterKey) && !string.IsNullOrWhiteSpace(normalizedDispNum))
            {
                if (!existingDispNumsByQuarter.TryGetValue(quarterKey, out var dispNums) || dispNums == null)
                {
                    dispNums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    existingDispNumsByQuarter[quarterKey] = dispNums;
                }

                dispNums.Add(normalizedDispNum);
            }

            return true;
        }

        private static Point2d ResolveMissingLabelAnchor(Polyline quarterPolyline, string dispNum)
        {
            var center = GeometryUtils.GetSafeInteriorPoint(quarterPolyline);
            var hash = GetStableDispHash(dispNum);
            for (var i = 0; i < 40; i++)
            {
                var angleDegrees = (hash + (i * 59)) % 360;
                var angle = angleDegrees * Math.PI / 180.0;
                var radius = 5.0 + (((hash / 37) + i) % 10) * 4.5;
                var candidate = new Point2d(
                    center.X + (Math.Cos(angle) * radius),
                    center.Y + (Math.Sin(angle) * radius));
                if (GeometryUtils.IsPointInsidePolyline(quarterPolyline, candidate))
                {
                    return candidate;
                }
            }

            return center;
        }

        private static int GetStableDispHash(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return 0;
            }

            unchecked
            {
                var hash = 17;
                foreach (var ch in text.ToUpperInvariant())
                {
                    hash = (hash * 31) + ch;
                }

                return hash & int.MaxValue;
            }
        }

        private static string ReplaceLabelOwnerInContents(string contents, string expectedOwner)
        {
            if (string.IsNullOrWhiteSpace(contents))
            {
                return string.Empty;
            }

            if (string.IsNullOrWhiteSpace(expectedOwner))
            {
                return contents;
            }

            var delimiter = contents.IndexOf("\\X", StringComparison.OrdinalIgnoreCase) >= 0 ? "\\X" : "\\P";
            var parts = contents.Split(new[] { delimiter }, StringSplitOptions.None);
            if (parts.Length == 0)
            {
                return contents;
            }

            parts[0] = expectedOwner.Trim();
            return string.Join(delimiter, parts);
        }

        private static string ReplaceLabelDispNumInContents(string contents, string dispNum)
        {
            if (string.IsNullOrWhiteSpace(contents))
            {
                return string.Empty;
            }

            var normalizedDisplayDispNum = ResolveIssueDisplayDispNum(dispNum, string.Empty);
            if (string.IsNullOrWhiteSpace(normalizedDisplayDispNum))
            {
                return contents;
            }

            var delimiter = contents.IndexOf("\\X", StringComparison.OrdinalIgnoreCase) >= 0 ? "\\X" : "\\P";
            var parts = contents.Split(new[] { delimiter }, StringSplitOptions.None);
            if (parts.Length == 0)
            {
                return contents;
            }

            for (var i = parts.Length - 1; i >= 0; i--)
            {
                if (TryParseDispositionNumberFromText(parts[i], out _))
                {
                    parts[i] = normalizedDisplayDispNum;
                    return string.Join(delimiter, parts);
                }
            }

            return contents;
        }

        private static string ResolveIssueDisplayDispNum(string candidateDispNum, string normalizedFallback)
        {
            if (TryParseDispositionNumberFromText(candidateDispNum, out var parsedDispNum))
            {
                return parsedDispNum;
            }

            if (!string.IsNullOrWhiteSpace(normalizedFallback))
            {
                return normalizedFallback.Trim();
            }

            return (candidateDispNum ?? string.Empty).Trim();
        }

        private static void ApplyTemplateTextFormatting(Entity templateEntity, MText target)
        {
            if (templateEntity == null || target == null)
            {
                return;
            }

            if (templateEntity is MText templateMText)
            {
                if (!templateMText.TextStyleId.IsNull)
                {
                    target.TextStyleId = templateMText.TextStyleId;
                }

                if (templateMText.TextHeight > 0.0)
                {
                    target.TextHeight = templateMText.TextHeight;
                }

                target.Attachment = templateMText.Attachment;
                target.Rotation = templateMText.Rotation;
                if (templateMText.Width > 0.0)
                {
                    target.Width = templateMText.Width;
                }

                return;
            }

            if (templateEntity is MLeader templateLeader)
            {
                try
                {
                    var leaderText = templateLeader.MText;
                    if (leaderText != null)
                    {
                        if (!leaderText.TextStyleId.IsNull)
                        {
                            target.TextStyleId = leaderText.TextStyleId;
                        }

                        if (leaderText.TextHeight > 0.0)
                        {
                            target.TextHeight = leaderText.TextHeight;
                        }

                        target.Attachment = leaderText.Attachment;
                        target.Rotation = leaderText.Rotation;
                        if (leaderText.Width > 0.0)
                        {
                            target.Width = leaderText.Width;
                        }
                    }
                }
                catch
                {
                    // Best-effort fallback.
                }

                return;
            }

            if (templateEntity is AlignedDimension templateDimension && templateDimension.Dimtxt > 0.0)
            {
                target.TextHeight = templateDimension.Dimtxt;
            }
        }

        private static bool HasPotentialMissingPlsrLabels(
            Database database,
            Logger logger,
            AtsBuildInput input,
            List<QuarterInfo> plsrQuarters)
        {
            if (database == null ||
                input == null ||
                input.PlsrXmlPaths == null ||
                input.PlsrXmlPaths.Count == 0 ||
                input.SectionRequests == null ||
                input.SectionRequests.Count == 0 ||
                plsrQuarters == null ||
                plsrQuarters.Count == 0)
            {
                return false;
            }

            try
            {
                var requestedQuarterKeys = BuildRequestedQuarterKeys(input.SectionRequests);
                if (requestedQuarterKeys.Count == 0)
                {
                    return false;
                }

                var notIncludedPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var quarterData = LoadPlsrQuarterData(input.PlsrXmlPaths, logger, notIncludedPrefixes);
                if (quarterData.Count == 0)
                {
                    return false;
                }

                var labelByQuarter = CollectPlsrLabels(database, plsrQuarters, logger);
                var existingDispNumsByQuarter = BuildExistingDispNumsByQuarter(labelByQuarter);
                var emptySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var quarterKey in requestedQuarterKeys)
                {
                    if (!quarterData.TryGetValue(quarterKey, out var expected) || expected == null)
                    {
                        continue;
                    }

                    if (!existingDispNumsByQuarter.TryGetValue(quarterKey, out var existingDispNums) || existingDispNums == null)
                    {
                        existingDispNums = emptySet;
                    }

                    foreach (var act in expected.Activities)
                    {
                        var dispNum = NormalizeDispNum(act?.DispNum ?? string.Empty);
                        if (string.IsNullOrWhiteSpace(dispNum))
                        {
                            continue;
                        }

                        var prefix = GetDispositionPrefix(dispNum);
                        if (!string.IsNullOrWhiteSpace(prefix) && !PlsrDispositionPrefixes.Contains(prefix))
                        {
                            continue;
                        }

                        if (!existingDispNums.Contains(dispNum))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
            catch (System.Exception ex)
            {
                logger.WriteLine("PLSR precheck failed; falling back to conservative import behavior: " + ex.Message);
                return true;
            }
        }

        private static Dictionary<string, PlsrQuarterData> LoadPlsrQuarterData(
            IEnumerable<string>? xmlPaths,
            Logger logger,
            HashSet<string> notIncludedPrefixes)
        {
            var result = new Dictionary<string, PlsrQuarterData>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in xmlPaths ?? Array.Empty<string>())
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

        private static Dictionary<string, PlsrDispositionLabelOverride> BuildPlsrLabelOverridesByDispNum(
            IEnumerable<string>? xmlPaths,
            ExcelLookup companyLookup,
            Logger logger)
        {
            var overrideByDispNum = new Dictionary<string, PlsrDispositionLabelOverride>(StringComparer.OrdinalIgnoreCase);
            var reportDateByDispNum = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            var ownerConflictCount = 0;

            foreach (var path in xmlPaths ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                {
                    continue;
                }

                if (!TryParsePlsrXml(path, logger, out var reportDate, out var activities))
                {
                    continue;
                }

                var effectiveReportDate = reportDate.Date;
                foreach (var activity in activities)
                {
                    var dispNum = NormalizeDispNum(activity.Item1.DispNum);
                    if (string.IsNullOrWhiteSpace(dispNum))
                    {
                        continue;
                    }

                    var prefix = GetDispositionPrefix(dispNum);
                    if (string.IsNullOrWhiteSpace(prefix) || !PlsrDispositionPrefixes.Contains(prefix))
                    {
                        continue;
                    }

                    var mappedOwner = MapClientNameForCompare(companyLookup, activity.Item1.Owner);
                    var shouldAddExpiredMarker = ShouldPlsrActivityRequireExpiredMarker(effectiveReportDate, activity.Item1);
                    if (string.IsNullOrWhiteSpace(mappedOwner) && !shouldAddExpiredMarker)
                    {
                        continue;
                    }

                    if (!overrideByDispNum.TryGetValue(dispNum, out var existingOverride))
                    {
                        overrideByDispNum[dispNum] = new PlsrDispositionLabelOverride
                        {
                            Owner = mappedOwner ?? string.Empty,
                            ShouldAddExpiredMarker = shouldAddExpiredMarker
                        };
                        reportDateByDispNum[dispNum] = effectiveReportDate;
                        continue;
                    }

                    var existingReportDate = reportDateByDispNum[dispNum];
                    if (effectiveReportDate > existingReportDate)
                    {
                        existingOverride.Owner = mappedOwner ?? string.Empty;
                        existingOverride.ShouldAddExpiredMarker = shouldAddExpiredMarker;
                        reportDateByDispNum[dispNum] = effectiveReportDate;
                        continue;
                    }

                    if (effectiveReportDate == existingReportDate)
                    {
                        if (!string.IsNullOrWhiteSpace(mappedOwner) &&
                            !string.IsNullOrWhiteSpace(existingOverride.Owner) &&
                            !string.Equals(
                                NormalizeOwner(existingOverride.Owner),
                                NormalizeOwner(mappedOwner),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            ownerConflictCount++;
                            logger.WriteLine($"PLSR label override owner conflict for {dispNum}: keeping '{existingOverride.Owner}', ignoring '{mappedOwner}' from {Path.GetFileName(path)}.");
                        }

                        if (string.IsNullOrWhiteSpace(existingOverride.Owner) && !string.IsNullOrWhiteSpace(mappedOwner))
                        {
                            existingOverride.Owner = mappedOwner;
                        }

                        if (shouldAddExpiredMarker)
                        {
                            existingOverride.ShouldAddExpiredMarker = true;
                        }
                    }
                }
            }

            logger.WriteLine($"PLSR label overrides loaded: {overrideByDispNum.Count} disposition(s).");
            logger.WriteLine($"PLSR expired marker overrides loaded: {overrideByDispNum.Values.Count(v => v.ShouldAddExpiredMarker)} disposition(s).");
            if (ownerConflictCount > 0)
            {
                logger.WriteLine($"PLSR label override owner conflicts ignored: {ownerConflictCount}.");
            }

            return overrideByDispNum;
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

                    DateTime? versionDate = null;
                    var versionDates = new List<DateTime>();
                    foreach (var versionElement in activity.Descendants(ns + "VersionDate"))
                    {
                        var versionDateText = versionElement?.Value?.Trim();
                        if (!TryParsePlsrXmlVersionDate(versionDateText, out var versionParsed))
                        {
                            continue;
                        }

                        var parsedDate = versionParsed.Date;
                        if (!versionDates.Contains(parsedDate))
                        {
                            versionDates.Add(parsedDate);
                        }
                    }

                    if (versionDates.Count > 0)
                    {
                        versionDate = versionDates.Max();
                    }

                    DateTime? activityDate = null;
                    var activityDateText = activity.Element(ns + "ActivityDate")?.Value?.Trim();
                    if (TryParsePlsrXmlVersionDate(activityDateText, out var activityParsed))
                    {
                        activityDate = activityParsed;
                    }

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

                    var parsedActivity = new PlsrActivity
                    {
                        DispNum = dispNum,
                        Owner = owner,
                        ExpiryDate = expiryDate,
                        VersionDate = versionDate,
                        ActivityDate = activityDate
                    };

                    foreach (var parsedVersionDate in versionDates)
                    {
                        parsedActivity.VersionDates.Add(parsedVersionDate);
                    }

                    activities.Add((parsedActivity, landIds));
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

            var quarterEntries = quarterMap
                .Select(pair => (
                    Key: pair.Key,
                    Polyline: pair.Value.Polyline,
                    Bounds: pair.Value.Bounds))
                .ToList();
            var layerMatchCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var scanErrors = 0;
            const int maxLoggedScanErrors = 8;

            try
            {
                using (var tr = database.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead, false);
                    var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead, false);

                    foreach (ObjectId id in ms)
                    {
                        try
                        {
                            PlsrLabelEntry? entry = null;

                            var dbObject = tr.GetObject(id, OpenMode.ForRead, false);
                            if (!(dbObject is Entity labelEntity) || labelEntity.IsErased)
                            {
                                continue;
                            }

                            if (!(dbObject is MText || dbObject is MLeader || dbObject is AlignedDimension))
                            {
                                continue;
                            }

                            var layerName = labelEntity.Layer ?? string.Empty;
                            if (!layerMatchCache.TryGetValue(layerName, out var isDispositionTextLayer))
                            {
                                isDispositionTextLayer = IsDispositionTextLayer(layerName);
                                layerMatchCache[layerName] = isDispositionTextLayer;
                            }

                            if (!isDispositionTextLayer)
                            {
                                continue;
                            }

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

                            foreach (var quarterEntry in quarterEntries)
                            {
                                if (entry.Location.X < quarterEntry.Bounds.MinPoint.X ||
                                    entry.Location.X > quarterEntry.Bounds.MaxPoint.X ||
                                    entry.Location.Y < quarterEntry.Bounds.MinPoint.Y ||
                                    entry.Location.Y > quarterEntry.Bounds.MaxPoint.Y)
                                {
                                    continue;
                                }

                                if (GeometryUtils.IsPointInsidePolyline(quarterEntry.Polyline, entry.Location))
                                {
                                    if (!byQuarter.TryGetValue(quarterEntry.Key, out var list))
                                    {
                                        list = new List<PlsrLabelEntry>();
                                        byQuarter[quarterEntry.Key] = list;
                                    }

                                    list.Add(entry);
                                    break;
                                }
                            }
                        }
                        catch (Autodesk.AutoCAD.Runtime.Exception ex)
                        {
                            scanErrors++;
                            if (scanErrors <= maxLoggedScanErrors)
                            {
                                logger.WriteLine($"PLSR label scan skipped entity {id}: {ex.Message}");
                            }
                        }
                        catch (System.Exception ex)
                        {
                            scanErrors++;
                            if (scanErrors <= maxLoggedScanErrors)
                            {
                                logger.WriteLine($"PLSR label scan skipped entity {id}: {ex.Message}");
                            }
                        }
                    }

                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                logger.WriteLine("PLSR label scan failed: " + ex.Message);
            }

            if (scanErrors > maxLoggedScanErrors)
            {
                logger.WriteLine($"PLSR label scan skipped {scanErrors - maxLoggedScanErrors} additional entity error(s).");
            }
            if (scanErrors > 0)
            {
                logger.WriteLine($"PLSR label scan completed with {scanErrors} recoverable entity error(s).");
            }

            return byQuarter;
        }

        private static PlsrLabelEntry? BuildLabelEntry(ObjectId id, bool isLeader, string contents, Point2d location, bool isDimension = false)
        {
            var lines = SplitMTextLines(contents);
            if (lines.Count < 2)
                return null;

            var owner = lines.FirstOrDefault() ?? string.Empty;
            var dispNum = ExtractDispositionNumber(lines, contents);
            if (string.IsNullOrWhiteSpace(dispNum))
            {
                return null;
            }

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

        private static string ExtractDispositionNumber(IReadOnlyList<string> lines, string rawContents)
        {
            if (lines != null)
            {
                for (int i = lines.Count - 1; i >= 0; i--)
                {
                    if (TryParseDispositionNumberFromText(lines[i], out var parsed))
                    {
                        return parsed;
                    }
                }
            }

            if (TryParseDispositionNumberFromText(rawContents, out var fallback))
            {
                return fallback;
            }

            return string.Empty;
        }

        private static bool TryParseDispositionNumberFromText(string text, out string dispNum)
        {
            dispNum = string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var upper = StripMTextControlCodes(text).ToUpperInvariant();
            foreach (var prefix in PlsrDispositionPrefixes)
            {
                var spaced = Regex.Match(upper, $@"\b{Regex.Escape(prefix)}\s*[-]?\s*(\d{{2,}})\b", RegexOptions.IgnoreCase);
                if (spaced.Success)
                {
                    dispNum = prefix + spaced.Groups[1].Value;
                    return true;
                }
            }

            var normalized = Regex.Replace(upper, "[^A-Z0-9]+", string.Empty);
            if (normalized.Length < 4)
            {
                return false;
            }

            foreach (var prefix in PlsrDispositionPrefixes)
            {
                if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (normalized.Length <= prefix.Length || !char.IsDigit(normalized[prefix.Length]))
                {
                    continue;
                }

                int end = prefix.Length;
                while (end < normalized.Length && char.IsDigit(normalized[end]))
                {
                    end++;
                }

                if (end > prefix.Length)
                {
                    dispNum = prefix + normalized.Substring(prefix.Length, end - prefix.Length);
                    return true;
                }
            }

            return false;
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
                var cleaned = StripMTextControlCodes(line).Trim();
                if (!string.IsNullOrWhiteSpace(cleaned))
                    lines.Add(cleaned);
            }

            return lines;
        }

        private static string StripMTextControlCodes(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var cleaned = text
                .Replace("{", string.Empty)
                .Replace("}", string.Empty)
                .Replace("\\~", " ")
                .Replace("\\P", " ")
                .Replace("\\X", " ");

            // Strip semicolon-terminated control groups (e.g. \A1; \pxqc; \C1; \F...;).
            cleaned = Regex.Replace(cleaned, @"\\[A-Za-z][^;\\]*;", string.Empty, RegexOptions.IgnoreCase);
            // Strip stacked fraction/text groups.
            cleaned = Regex.Replace(cleaned, @"\\S[^;]*;", " ", RegexOptions.IgnoreCase);
            // Strip one-char on/off controls (e.g. \L \l \O \o \K \k).
            cleaned = Regex.Replace(cleaned, @"\\[LOKlok]", string.Empty);
            // Strip any remaining generic one-letter controls.
            cleaned = Regex.Replace(cleaned, @"\\[A-Za-z]", string.Empty);

            return cleaned;
        }

        private static string FlattenMTextForDisplay(string contents)
        {
            if (string.IsNullOrWhiteSpace(contents))
            {
                return string.Empty;
            }

            var lines = SplitMTextLines(contents);
            if (lines.Count == 0)
            {
                return StripMTextControlCodes(contents).Trim();
            }

            return string.Join(" | ", lines);
        }

        private static bool HasExpiredMarker(string? contents)
        {
            if (string.IsNullOrWhiteSpace(contents))
            {
                return false;
            }

            var flattened = FlattenMTextForDisplay(contents);
            return Regex.IsMatch(flattened, @"\bEXPIRED\b", RegexOptions.IgnoreCase);
        }

        private static bool IsDispositionTextLayer(string layerName)
        {
            if (string.IsNullOrWhiteSpace(layerName))
            {
                return false;
            }

            // Keep this aligned with disposition layering logic:
            // text layers are generated as C-<suffix>-T or F-<suffix>-T.
            var normalized = layerName.Trim();
            if (normalized.Length < 5)
            {
                return false;
            }

            var prefix = char.ToUpperInvariant(normalized[0]);
            if ((prefix != 'C' && prefix != 'F') || normalized[1] != '-')
            {
                return false;
            }

            return normalized.EndsWith("-T", StringComparison.OrdinalIgnoreCase);
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

            var upper = StripMTextControlCodes(owner).ToUpperInvariant();
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

        private static bool TryApplyOwnerUpdate(
            Transaction tr,
            PlsrLabelEntry label,
            string expectedOwner,
            out bool alreadyMatched)
        {
            alreadyMatched = false;
            if (label == null || string.IsNullOrWhiteSpace(expectedOwner))
            {
                return false;
            }

            var contents = label.RawContents ?? string.Empty;
            if (string.IsNullOrWhiteSpace(contents))
            {
                return false;
            }

            var delimiter = contents.IndexOf("\\X", StringComparison.OrdinalIgnoreCase) >= 0 ? "\\X" : "\\P";
            var parts = contents.Split(new[] { delimiter }, StringSplitOptions.None);
            if (parts.Length == 0)
            {
                return false;
            }

            var existingOwner = SplitMTextLines(parts[0]).FirstOrDefault() ?? parts[0];
            if (string.Equals(NormalizeOwner(existingOwner), NormalizeOwner(expectedOwner), StringComparison.OrdinalIgnoreCase))
            {
                alreadyMatched = true;
                return true;
            }

            parts[0] = expectedOwner.Trim();
            var updated = string.Join(delimiter, parts);

            if (label.IsDimension)
            {
                if (tr.GetObject(label.Id, OpenMode.ForWrite) is Dimension dimension)
                {
                    dimension.DimensionText = updated;
                    label.RawContents = updated;
                    label.Owner = expectedOwner.Trim();
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
                    label.RawContents = updated;
                    label.Owner = expectedOwner.Trim();
                    return true;
                }
            }
            else
            {
                if (tr.GetObject(label.Id, OpenMode.ForWrite) is MText mtext)
                {
                    mtext.Contents = updated;
                    label.RawContents = updated;
                    label.Owner = expectedOwner.Trim();
                    return true;
                }
            }

            return false;
        }

        private static bool TryApplyExpiredMarker(Transaction tr, PlsrLabelEntry label, out bool alreadyTagged)
        {
            alreadyTagged = false;
            if (label == null)
                return false;

            var contents = label.RawContents ?? string.Empty;
            if (HasExpiredMarker(contents))
            {
                alreadyTagged = true;
                return true;
            }

            var updated = LabelPlacer.AppendExpiredMarkerIfMissing(contents);

            if (label.IsDimension)
            {
                if (tr.GetObject(label.Id, OpenMode.ForWrite) is Dimension dimension)
                {
                    dimension.DimensionText = updated;
                    label.RawContents = updated;
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
                    label.RawContents = updated;
                    return true;
                }
            }
            else
            {
                if (tr.GetObject(label.Id, OpenMode.ForWrite) is MText mtext)
                {
                    mtext.Contents = updated;
                    label.RawContents = updated;
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
