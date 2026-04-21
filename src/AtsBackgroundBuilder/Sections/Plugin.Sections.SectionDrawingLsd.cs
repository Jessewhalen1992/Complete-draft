using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AtsBackgroundBuilder.Core;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AtsBackgroundBuilder
{
    public partial class Plugin
    {
        private static SectionBuildResult DrawSectionFromIndex(
            Editor editor,
            Database database,
            SectionOutline outline,
            SectionKey key,
            bool drawLsds,
            bool drawQuarterView,
            string secType,
            IReadOnlyDictionary<QuarterSelection, string>? quarterSecTypes = null)
        {
            var quarterIds = new Dictionary<QuarterSelection, ObjectId>();
            var quarterHelperEntityIds = new List<ObjectId>();
            ObjectId sectionLabelId = ObjectId.Null;
            ObjectId sectionId;
            var normalizedSecType = NormalizeSecType(secType);
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                EnsureSecTypeLayers(database, transaction, normalizedSecType, quarterSecTypes);
                EnsureLayer(database, transaction, "L-QSEC");
                EnsureLayer(database, transaction, "L-QSEC-BOX");
                SetLayerVisibility(database, transaction, "L-QSEC-BOX", isOff: true, isPlottable: false);
                if (drawQuarterView)
                {
                    EnsureLayerWithColor(database, transaction, LayerQuarterView, QuarterViewLayerColorIndex);
                }

                var sectionPolyline = new Polyline(outline.Vertices.Count)
                {
                    Closed = outline.Closed,
                    Layer = "L-QSEC-BOX",
                    ColorIndex = 256
                };

                for (var i = 0; i < outline.Vertices.Count; i++)
                {
                    var vertex = outline.Vertices[i];
                    sectionPolyline.AddVertexAt(i, vertex, 0, 0, 0);
                }

                sectionId = modelSpace.AppendEntity(sectionPolyline);
                transaction.AddNewlyCreatedDBObject(sectionPolyline, true);

                if (TryBuildQuarterMap(sectionPolyline, out var quarterMap, out var anchors))
                {
                    var eastUnit = GetUnitVector(anchors.Left, anchors.Right, new Vector2d(1, 0));
                    var northUnit = GetUnitVector(anchors.Bottom, anchors.Top, new Vector2d(0, 1));
                    if (TryGetQuarterCorner(sectionPolyline, eastUnit, northUnit, QuarterCorner.NorthWest, out var nw) &&
                        TryGetQuarterCorner(sectionPolyline, eastUnit, northUnit, QuarterCorner.NorthEast, out var ne) &&
                        TryGetQuarterCorner(sectionPolyline, eastUnit, northUnit, QuarterCorner.SouthWest, out var sw) &&
                        TryGetQuarterCorner(sectionPolyline, eastUnit, northUnit, QuarterCorner.SouthEast, out var se))
                    {
                        foreach (var id in DrawSectionBoundaryQuarterSegmentPolylines(
                            modelSpace,
                            transaction,
                            nw,
                            ne,
                            sw,
                            se,
                            normalizedSecType,
                            quarterSecTypes,
                            clipToWindow: null,
                            anchors.Top,
                            anchors.Right,
                            anchors.Bottom,
                            anchors.Left))
                        {
                            quarterHelperEntityIds.Add(id);
                        }
                    }

                    foreach (var quarter in quarterMap)
                    {
                        quarter.Value.Layer = "L-QSEC-BOX";
                        quarter.Value.ColorIndex = 256;
                        var quarterId = modelSpace.AppendEntity(quarter.Value);
                        transaction.AddNewlyCreatedDBObject(quarter.Value, true);
                        quarterIds[quarter.Key] = quarterId;
                    }

                    var qv = new Line(new Point3d(anchors.Top.X, anchors.Top.Y, 0), new Point3d(anchors.Bottom.X, anchors.Bottom.Y, 0))
                    {
                        Layer = "L-QSEC",
                        ColorIndex = 256
                    };
                    var qh = new Line(new Point3d(anchors.Left.X, anchors.Left.Y, 0), new Point3d(anchors.Right.X, anchors.Right.Y, 0))
                    {
                        Layer = "L-QSEC",
                        ColorIndex = 256
                    };
                    var qvId = modelSpace.AppendEntity(qv);
                    transaction.AddNewlyCreatedDBObject(qv, true);
                    var qhId = modelSpace.AppendEntity(qh);
                    transaction.AddNewlyCreatedDBObject(qh, true);

                    quarterHelperEntityIds.Add(qvId);
                    quarterHelperEntityIds.Add(qhId);

                    if (drawLsds)
                    {
                        DrawLsdSubdivisionLines(
                            database,
                            transaction,
                            modelSpace,
                            editor,
                            quarterMap,
                            anchors,
                            key,
                            normalizedSecType);
                    }

                    var center = new Point3d(
                        0.5 * (anchors.Top.X + anchors.Bottom.X),
                        0.5 * (anchors.Left.Y + anchors.Right.Y),
                        0);
                    sectionLabelId = InsertSectionLabelBlock(modelSpace, blockTable, transaction, editor, center, key);
                }

                transaction.Commit();
            }

            return new SectionBuildResult(sectionId, quarterIds, quarterHelperEntityIds, sectionLabelId);
        }

        private readonly struct QuarterViewSectionFrame
        {
            public QuarterViewSectionFrame(
                ObjectId sectionId,
                int sectionNumber,
                Point2d origin,
                Vector2d eastUnit,
                Vector2d northUnit,
                double westEdgeU,
                double eastEdgeU,
                double southEdgeV,
                double northEdgeV,
                double midU,
                double midV,
                Point2d topAnchor,
                Point2d rightAnchor,
                Point2d bottomAnchor,
                Point2d leftAnchor,
                Extents3d cleanupWindow)
            {
                SectionId = sectionId;
                SectionNumber = sectionNumber;
                Origin = origin;
                EastUnit = eastUnit;
                NorthUnit = northUnit;
                WestEdgeU = westEdgeU;
                EastEdgeU = eastEdgeU;
                SouthEdgeV = southEdgeV;
                NorthEdgeV = northEdgeV;
                MidU = midU;
                MidV = midV;
                TopAnchor = topAnchor;
                RightAnchor = rightAnchor;
                BottomAnchor = bottomAnchor;
                LeftAnchor = leftAnchor;
                CleanupWindow = cleanupWindow;
            }

            public ObjectId SectionId { get; }
            public int SectionNumber { get; }
            public Point2d Origin { get; }
            public Vector2d EastUnit { get; }
            public Vector2d NorthUnit { get; }
            public double WestEdgeU { get; }
            public double EastEdgeU { get; }
            public double SouthEdgeV { get; }
            public double NorthEdgeV { get; }
            public double MidU { get; }
            public double MidV { get; }
            public Point2d TopAnchor { get; }
            public Point2d RightAnchor { get; }
            public Point2d BottomAnchor { get; }
            public Point2d LeftAnchor { get; }
            public Extents3d CleanupWindow { get; }
        }

        private static void DrawQuarterViewFromFinalRoadAllowanceGeometry(
            Database database,
            IReadOnlyCollection<ObjectId> sectionPolylineIds,
            IReadOnlyDictionary<ObjectId, int>? sectionNumberByPolylineId,
            Logger? logger)
        {
            if (database == null || sectionPolylineIds == null || sectionPolylineIds.Count == 0)
            {
                return;
            }

            const double cleanupWindowPadding = 80.0;
            const double minQuarterSpan = 2.0;

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                EnsureLayerWithColor(database, transaction, LayerQuarterView, QuarterViewLayerColorIndex);

                var frames = new List<QuarterViewSectionFrame>();
                foreach (var sectionId in sectionPolylineIds.Where(id => !id.IsNull).Distinct())
                {
                    if (!(transaction.GetObject(sectionId, OpenMode.ForRead, false) is Polyline section) || section.IsErased)
                    {
                        continue;
                    }

                    if (!TryGetQuarterAnchors(section, out var anchors))
                    {
                        anchors = GetFallbackAnchors(section);
                    }

                    var eastUnit = GetUnitVector(anchors.Left, anchors.Right, new Vector2d(1, 0));
                    var northRaw = GetUnitVector(anchors.Bottom, anchors.Top, new Vector2d(0, 1));
                    var northUnit = eastUnit.RotateBy(Math.PI / 2.0);
                    if (northUnit.DotProduct(northRaw) < 0.0)
                    {
                        northUnit = new Vector2d(-northUnit.X, -northUnit.Y);
                    }
                    if (!TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.SouthWest, out var origin))
                    {
                        Extents3d extents;
                        try
                        {
                            extents = section.GeometricExtents;
                        }
                        catch
                        {
                            continue;
                        }

                        origin = new Point2d(extents.MinPoint.X, extents.MinPoint.Y);
                    }

                    var swCorner = origin;
                    var seCorner = origin + (eastUnit * 1.0);
                    var nwCorner = origin + (northUnit * 1.0);
                    var neCorner = origin + (eastUnit * 1.0) + (northUnit * 1.0);
                    var haveCorners =
                        TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.NorthWest, out nwCorner) &&
                        TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.NorthEast, out neCorner) &&
                        TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.SouthWest, out swCorner) &&
                        TryGetQuarterCorner(section, eastUnit, northUnit, QuarterCorner.SouthEast, out seCorner);

                    var westEdgeU = 0.5 * (
                        ProjectPointToQuarterU(swCorner, origin, eastUnit) +
                        ProjectPointToQuarterU(nwCorner, origin, eastUnit));
                    var eastEdgeU = 0.5 * (
                        ProjectPointToQuarterU(seCorner, origin, eastUnit) +
                        ProjectPointToQuarterU(neCorner, origin, eastUnit));
                    var southEdgeV = 0.5 * (
                        ProjectPointToQuarterV(swCorner, origin, northUnit) +
                        ProjectPointToQuarterV(seCorner, origin, northUnit));
                    var northEdgeV = 0.5 * (
                        ProjectPointToQuarterV(nwCorner, origin, northUnit) +
                        ProjectPointToQuarterV(neCorner, origin, northUnit));

                    if (!haveCorners || westEdgeU >= eastEdgeU || southEdgeV >= northEdgeV)
                    {
                        westEdgeU = double.MaxValue;
                        eastEdgeU = double.MinValue;
                        southEdgeV = double.MaxValue;
                        northEdgeV = double.MinValue;
                        for (var vi = 0; vi < section.NumberOfVertices; vi++)
                        {
                            var p = section.GetPoint2dAt(vi);
                            var rel = p - origin;
                            var u = rel.DotProduct(eastUnit);
                            var v = rel.DotProduct(northUnit);
                            if (u < westEdgeU)
                            {
                                westEdgeU = u;
                            }

                            if (u > eastEdgeU)
                            {
                                eastEdgeU = u;
                            }

                            if (v < southEdgeV)
                            {
                                southEdgeV = v;
                            }

                            if (v > northEdgeV)
                            {
                                northEdgeV = v;
                            }
                        }
                    }

                    if (westEdgeU >= eastEdgeU || southEdgeV >= northEdgeV)
                    {
                        continue;
                    }

                    var midU = 0.5 * (
                        ProjectPointToQuarterU(anchors.Top, origin, eastUnit) +
                        ProjectPointToQuarterU(anchors.Bottom, origin, eastUnit));
                    var midV = 0.5 * (
                        ProjectPointToQuarterV(anchors.Left, origin, northUnit) +
                        ProjectPointToQuarterV(anchors.Right, origin, northUnit));
                    if ((midU - westEdgeU) <= minQuarterSpan || (midV - southEdgeV) <= minQuarterSpan)
                    {
                        continue;
                    }

                    Extents3d sectionExtents;
                    try
                    {
                        sectionExtents = section.GeometricExtents;
                    }
                    catch
                    {
                        continue;
                    }

                    var cleanupWindow = new Extents3d(
                        new Point3d(sectionExtents.MinPoint.X - cleanupWindowPadding, sectionExtents.MinPoint.Y - cleanupWindowPadding, 0.0),
                        new Point3d(sectionExtents.MaxPoint.X + cleanupWindowPadding, sectionExtents.MaxPoint.Y + cleanupWindowPadding, 0.0));

                    var sectionNumber = 0;
                    if (sectionNumberByPolylineId != null &&
                        sectionNumberByPolylineId.TryGetValue(sectionId, out var resolvedSectionNumber))
                    {
                        sectionNumber = resolvedSectionNumber;
                    }

                    frames.Add(new QuarterViewSectionFrame(
                        sectionId,
                        sectionNumber,
                        origin,
                        eastUnit,
                        northUnit,
                        westEdgeU,
                        eastEdgeU,
                        southEdgeV,
                        northEdgeV,
                        midU,
                        midV,
                        anchors.Top,
                        anchors.Right,
                        anchors.Bottom,
                        anchors.Left,
                        cleanupWindow));
                }

                if (frames.Count == 0)
                {
                    transaction.Commit();
                    return;
                }

                var boundarySegments = new List<(Point2d A, Point2d B, string Layer)>();
                var quarterDividerSegments = new List<(ObjectId Id, Point2d A, Point2d B)>();
                foreach (ObjectId id in modelSpace)
                {
                    if (!(transaction.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    if (!TryReadOpenLinearSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    if (!SegmentIntersectsAnyQuarterWindow(a, b, frames))
                    {
                        continue;
                    }

                    var normalizedLayer = (ent.Layer ?? string.Empty).Trim();
                    if (string.Equals(normalizedLayer, "L-QSEC", StringComparison.OrdinalIgnoreCase))
                    {
                        quarterDividerSegments.Add((id, a, b));
                        continue;
                    }

                    if (!IsQuarterViewBoundaryCandidateLayer(normalizedLayer))
                    {
                        continue;
                    }

                    boundarySegments.Add((a, b, normalizedLayer));
                }

                var correctionSouthBoundarySegments = boundarySegments
                    .Where(s => IsQuarterSouthCorrectionCandidateLayer(s.Layer))
                    .ToList();
                var correctionNorthBoundarySegments = boundarySegments
                    .Where(s => string.Equals(s.Layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(s.Layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                var hardBoundaryCornerClusters = new List<(Point2d Rep, int Count, bool HasHorizontal, bool HasVertical, int Priority)>();
                const double cornerClusterTolerance = 0.40;

                for (var i = 0; i < boundarySegments.Count; i++)
                {
                    var seg = boundarySegments[i];
                    var delta = seg.B - seg.A;
                    var absX = Math.Abs(delta.X);
                    var absY = Math.Abs(delta.Y);
                    if (absX <= 1e-6 && absY <= 1e-6)
                    {
                        continue;
                    }

                    var isHorizontal = absX >= absY;
                    var isVertical = absY >= absX;
                    AddBoundaryEndpointToCornerClusters(
                        hardBoundaryCornerClusters,
                        cornerClusterTolerance,
                        seg.A,
                        isHorizontal,
                        isVertical,
                        priority: 1);
                    AddBoundaryEndpointToCornerClusters(
                        hardBoundaryCornerClusters,
                        cornerClusterTolerance,
                        seg.B,
                        isHorizontal,
                        isVertical,
                        priority: 1);
                }

                for (var i = 0; i < boundarySegments.Count; i++)
                {
                    var first = boundarySegments[i];
                    var firstDelta = first.B - first.A;
                    var firstAbsX = Math.Abs(firstDelta.X);
                    var firstAbsY = Math.Abs(firstDelta.Y);
                    if (firstAbsX <= 1e-6 && firstAbsY <= 1e-6)
                    {
                        continue;
                    }

                    var firstLen = firstDelta.Length;
                    if (firstLen <= 1e-9)
                    {
                        continue;
                    }

                    var firstUnit = firstDelta / firstLen;
                    for (var j = i + 1; j < boundarySegments.Count; j++)
                    {
                        var second = boundarySegments[j];
                        var secondDelta = second.B - second.A;
                        var secondAbsX = Math.Abs(secondDelta.X);
                        var secondAbsY = Math.Abs(secondDelta.Y);
                        if (secondAbsX <= 1e-6 && secondAbsY <= 1e-6)
                        {
                            continue;
                        }

                        var secondLen = secondDelta.Length;
                        if (secondLen <= 1e-9)
                        {
                            continue;
                        }

                        var secondUnit = secondDelta / secondLen;
                        var absDot = Math.Abs(firstUnit.DotProduct(secondUnit));
                        // Robust orthogonality test for skewed township geometry.
                        if (absDot > 0.35)
                        {
                            continue;
                        }

                        if (!TryFindApparentCornerIntersection(
                                first.A,
                                first.B,
                                second.A,
                                second.B,
                                frames,
                                out var corner))
                        {
                            continue;
                        }

                        AddBoundaryEndpointToCornerClusters(
                            hardBoundaryCornerClusters,
                            cornerClusterTolerance,
                            corner,
                            isHorizontal: true,
                            isVertical: true,
                            priority: 0);
                    }
                }

                var hardBoundaryCornerEndpoints = hardBoundaryCornerClusters
                    .Where(c => c.HasHorizontal && c.HasVertical)
                    .Select(c => c.Rep)
                    .ToList();

                var erased = 0;
                foreach (ObjectId id in modelSpace)
                {
                    if (!(transaction.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    if (!string.Equals(ent.Layer, LayerQuarterView, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!(ent is Polyline poly) || !poly.Closed || poly.NumberOfVertices < 3)
                    {
                        continue;
                    }

                    if (!IsQuarterViewPolylineOwnedByAnyRebuiltSection(poly, frames))
                    {
                        continue;
                    }

                    try
                    {
                        ent.UpgradeOpen();
                        ent.Erase();
                        erased++;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        // Ignore locked/invalid entities and continue rebuilding quarter view.
                    }
                }

                var drawn = 0;
                var protectedSouthMidCorners = new List<Point2d>();
                var protectedNorthMidCorners = new List<Point2d>();
                var protectedWestBoundaryCorners = new List<Point2d>();
                var protectedEastBoundaryCorners = new List<Point2d>();
                var quarterBoxInfos = new List<(
                    ObjectId BoxId,
                    QuarterViewSectionFrame Frame,
                    QuarterSelection Quarter,
                    double WestExpectedInset,
                    double EastExpectedInset,
                    double SouthExpectedInset,
                    bool IsCorrectionSouthQuarter,
                    bool HasSouthDisplayOverride,
                    Point2d SouthDisplayOverrideSouthWestLocal,
                    Point2d SouthDisplayOverrideSouthMidLocal,
                    Point2d SouthDisplayOverrideSouthEastLocal)>();
                foreach (var frame in frames)
                {
                    var emitQuarterVerify = frame.SectionNumber >= 1 && frame.SectionNumber <= 36;
                    var dividerLineA = frame.BottomAnchor;
                    var dividerLineB = frame.TopAnchor;
                    var dividerSource = "anchors";
                    var dividerSegmentId = ObjectId.Null;
                    if (TryResolveQuarterViewVerticalDividerSegmentFromQsec(
                            frame,
                            quarterDividerSegments,
                            out dividerSegmentId,
                            out var qsecDividerA,
                            out var qsecDividerB))
                    {
                        dividerLineA = qsecDividerA;
                        dividerLineB = qsecDividerB;
                        dividerSource = "L-QSEC";
                    }

                    var dividerPreferredU = frame.MidU;
                    if (TryIntersectLocalInfiniteLines(
                            frame,
                            dividerLineA,
                            dividerLineB,
                            frame.LeftAnchor,
                            frame.RightAnchor,
                            out var dividerAxisU,
                            out _))
                    {
                        dividerPreferredU = dividerAxisU;
                    }
                    else
                    {
                        var dividerRelA = dividerLineA - frame.Origin;
                        var dividerRelB = dividerLineB - frame.Origin;
                        dividerPreferredU = 0.5 * (
                            dividerRelA.DotProduct(frame.EastUnit) +
                            dividerRelB.DotProduct(frame.EastUnit));
                    }

                    // East quarter ownership currently targets SEC-width inset.
                    var eastExpectedOffset = RoadAllowanceSecWidthMeters;
                    var isBlindSouthBoundarySection = IsBlindSouthBoundarySectionForQuarterView(frame.SectionNumber);
                    var usecZeroSegments = boundarySegments
                        .Where(s => string.Equals(s.Layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    var secOrUsecZeroSegments = boundarySegments
                        .Where(s =>
                            string.Equals(s.Layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(s.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(s.Layer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    var ordinarySouthResolutionSegments = boundarySegments
                        .Where(s => QuarterSouthBoundaryLayerFilter.IsOrdinaryResolutionLayer(s.Layer))
                        .ToList();
                    var westResolutionSegments = secOrUsecZeroSegments.Count > 0
                        ? (IReadOnlyList<(Point2d A, Point2d B, string Layer)>)secOrUsecZeroSegments
                        : boundarySegments;
                    var southResolutionSegments = ordinarySouthResolutionSegments.Count > 0
                        ? (IReadOnlyList<(Point2d A, Point2d B, string Layer)>)ordinarySouthResolutionSegments
                        : boundarySegments;
                    var hasWestUsecZeroOwnershipCandidate = TryResolveQuarterViewWestBoundaryU(
                        frame,
                        usecZeroSegments,
                        RoadAllowanceUsecWidthMeters,
                        dividerPreferredU,
                        out _,
                        out _,
                        out _,
                        out _);
                    var hasSouthUsecZeroOwnershipCandidate = !isBlindSouthBoundarySection &&
                                                             TryResolveQuarterViewSouthBoundaryV(
                                                                 frame,
                                                                 usecZeroSegments,
                                                                 RoadAllowanceUsecWidthMeters,
                                                                 dividerPreferredU,
                                                                 dividerLineA,
                                                                 dividerLineB,
                                                                 out _,
                                                                 out _,
                                                                 out _,
                                                                 out _);
                    // Side-specific ownership targets:
                    // - west: prefer USEC-width when a west L-USEC-0 ownership candidate exists, otherwise SEC-width.
                    // - south: same rule for surveyed south sections; blind south keeps legacy zero-offset behavior.
                    var ownershipPolicy = QuarterBoundaryOwnershipPolicy.Create(
                        isBlindSouthBoundarySection,
                        hasWestUsecZeroOwnershipCandidate,
                        hasSouthUsecZeroOwnershipCandidate,
                        RoadAllowanceSecWidthMeters,
                        RoadAllowanceUsecWidthMeters);
                    var westExpectedOffset = ownershipPolicy.WestExpectedOffset;
                    var westBoundaryU = frame.WestEdgeU - westExpectedOffset;
                    var southFallbackOffset = ownershipPolicy.SouthFallbackOffset;
                    var southExpectedOffset = Math.Max(southFallbackOffset, RoadAllowanceSecWidthMeters);
                    var southWestExpectedWestInset = westExpectedOffset;
                    var northWestExpectedWestInset = Math.Max(0.0, westExpectedOffset - RoadAllowanceSecWidthMeters);
                    const double northExpectedInset = 0.0;
                    var allowWestInsetDowngrade = ownershipPolicy.AllowWestInsetDowngrade;
                    var southBoundaryV = frame.SouthEdgeV - southFallbackOffset;
                    var westSource = ownershipPolicy.WestFallbackSource;
                    var southSource = ownershipPolicy.SouthFallbackSource;
                    var hasWestBoundarySegment = false;
                    var westBoundarySegmentA = default(Point2d);
                    var westBoundarySegmentB = default(Point2d);
                    var hasSouthBoundarySegment = false;
                    var southBoundarySegmentA = default(Point2d);
                    var southBoundarySegmentB = default(Point2d);
                    var northBoundaryV = frame.NorthEdgeV;
                    var northSource = "fallback-north";
                    var hasNorthBoundarySegment = false;
                    var northBoundarySegmentA = default(Point2d);
                    var northBoundarySegmentB = default(Point2d);
                    var eastBoundaryU = frame.EastEdgeU;
                    var eastSource = "fallback-east";
                    var hasEastBoundarySegment = false;
                    var eastBoundarySegmentA = default(Point2d);
                    var eastBoundarySegmentB = default(Point2d);

                    if (TryResolveQuarterViewWestBoundaryWithInsetFallbacks(
                            frame,
                            westResolutionSegments,
                            westExpectedOffset,
                            dividerPreferredU,
                            allowWestInsetDowngrade,
                            out var resolvedWestU,
                            out var resolvedWestLayer,
                            out var resolvedWestA,
                            out var resolvedWestB))
                    {
                        westBoundaryU = resolvedWestU;
                        westSource = resolvedWestLayer;
                        westBoundarySegmentA = resolvedWestA;
                        westBoundarySegmentB = resolvedWestB;
                        hasWestBoundarySegment = true;
                    }

                    if (hasWestBoundarySegment &&
                        TryResolveQuarterViewPreferredWestBoundaryFromSections(
                            frame,
                            boundarySegments,
                            westSource,
                            westExpectedOffset,
                            dividerPreferredU,
                            allowWestInsetDowngrade,
                            out var preferredWestU,
                            out var preferredWestLayer,
                            out var preferredWestA,
                            out var preferredWestB))
                    {
                        westBoundaryU = preferredWestU;
                        westSource = preferredWestLayer;
                        westBoundarySegmentA = preferredWestA;
                        westBoundarySegmentB = preferredWestB;
                        hasWestBoundarySegment = true;
                    }

                    if (TryResolvePreferredQuarterViewEastBoundary(
                            frame,
                            boundarySegments,
                            eastExpectedOffset,
                            out var preferredEastMidU,
                            out var resolvedEastA,
                            out var resolvedEastB,
                            out var resolvedEastLayer))
                    {
                        eastBoundaryU = preferredEastMidU;
                        eastSource = resolvedEastLayer;
                        eastBoundarySegmentA = resolvedEastA;
                        eastBoundarySegmentB = resolvedEastB;
                        hasEastBoundarySegment = true;
                    }

                    if (TryResolvePreferredQuarterViewSouthBoundaryWithCorrection(
                            frame,
                            southResolutionSegments,
                            correctionSouthBoundarySegments,
                            isBlindSouthBoundarySection,
                            southFallbackOffset,
                            dividerPreferredU,
                            dividerLineA,
                            dividerLineB,
                            out var resolvedSouthV,
                            out var resolvedSouthLayer,
                            out var resolvedSouthA,
                            out var resolvedSouthB))
                    {
                        southBoundaryV = resolvedSouthV;
                        southSource = resolvedSouthLayer;
                        southBoundarySegmentA = resolvedSouthA;
                        southBoundarySegmentB = resolvedSouthB;
                        hasSouthBoundarySegment = true;
                    }

                    if (hasSouthBoundarySegment &&
                        TryResolveQuarterViewPreferredSouthBoundaryFromSections(
                            frame,
                            boundarySegments,
                            southSource,
                            southFallbackOffset,
                            dividerPreferredU,
                            dividerLineA,
                            dividerLineB,
                            out var preferredSouthV,
                            out var preferredSouthLayer,
                            out var preferredSouthA,
                            out var preferredSouthB))
                    {
                        southBoundaryV = preferredSouthV;
                        southSource = preferredSouthLayer;
                        southBoundarySegmentA = preferredSouthA;
                        southBoundarySegmentB = preferredSouthB;
                        hasSouthBoundarySegment = true;
                    }

                    if (TryResolvePreferredQuarterViewNorthBoundary(
                            frame,
                            boundarySegments,
                            correctionNorthBoundarySegments,
                            dividerPreferredU,
                            southFallbackOffset <= 0.5 && hasWestBoundarySegment,
                            westBoundarySegmentA,
                            westBoundarySegmentB,
                            out var resolvedNorthV,
                            out var resolvedNorthLayer,
                            out var resolvedNorthA,
                            out var resolvedNorthB))
                    {
                        northBoundaryV = resolvedNorthV;
                        northSource = resolvedNorthLayer;
                        northBoundarySegmentA = resolvedNorthA;
                        northBoundarySegmentB = resolvedNorthB;
                        hasNorthBoundarySegment = true;
                    }

                    if (isBlindSouthBoundarySection &&
                        hasSouthBoundarySegment &&
                        hasNorthBoundarySegment &&
                        TryResolveQuarterViewBlindEastBoundaryFromNorthSouth(
                            frame,
                            boundarySegments,
                            eastBoundaryU,
                            southBoundarySegmentA,
                            southBoundarySegmentB,
                            northBoundarySegmentA,
                            northBoundarySegmentB,
                            out var refinedEastA,
                            out var refinedEastB,
                            out var refinedEastLayer,
                            out var resolvedEastMidU,
                            out var refinedEastOffset,
                            out var refinedSouthOffset,
                            out var refinedNorthOffset))
                    {
                        eastBoundarySegmentA = refinedEastA;
                        eastBoundarySegmentB = refinedEastB;
                        eastSource = refinedEastLayer;
                        hasEastBoundarySegment = true;
                        eastBoundaryU = resolvedEastMidU;

                        if (emitQuarterVerify)
                        {
                            logger?.WriteLine(
                                $"VERIFY-QTR-EAST-SELECT sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                $"eastOffset={refinedEastOffset:0.###} southOffset={refinedSouthOffset:0.###} northOffset={refinedNorthOffset:0.###} " +
                                $"seg={eastBoundarySegmentA.X:0.###},{eastBoundarySegmentA.Y:0.###}->{eastBoundarySegmentB.X:0.###},{eastBoundarySegmentB.Y:0.###} source={eastSource}");
                        }
                    }
                    else if (isBlindSouthBoundarySection &&
                             hasSouthBoundarySegment &&
                             hasNorthBoundarySegment &&
                             emitQuarterVerify)
                    {
                        logger?.WriteLine(
                            $"VERIFY-QTR-EAST-SELECT sec={frame.SectionNumber} handle={frame.SectionId.Handle}: found=False");
                    }

                    if (!isBlindSouthBoundarySection &&
                        hasEastBoundarySegment &&
                        hasSouthBoundarySegment &&
                        hasNorthBoundarySegment &&
                        TryResolveQuarterViewBlindEastBoundaryFromNorthSouth(
                            frame,
                            new[] { (eastBoundarySegmentA, eastBoundarySegmentB, eastSource) },
                            eastBoundaryU,
                            southBoundarySegmentA,
                            southBoundarySegmentB,
                            northBoundarySegmentA,
                            northBoundarySegmentB,
                            out _,
                            out _,
                            out _,
                            out _,
                            out var currentEastResidual,
                            out var currentSouthResidual,
                            out var currentNorthResidual) &&
                        TryResolveQuarterViewBlindEastBoundaryFromNorthSouth(
                            frame,
                            boundarySegments,
                            eastBoundaryU,
                            southBoundarySegmentA,
                            southBoundarySegmentB,
                            northBoundarySegmentA,
                            northBoundarySegmentB,
                            out var compatibilityEastA,
                            out var compatibilityEastB,
                            out var compatibilityEastLayer,
                            out var compatibilityEastMidU,
                            out var compatibilityEastResidual,
                            out var compatibilitySouthResidual,
                            out var compatibilityNorthResidual))
                    {
                        const double maxAcceptedEastCompatibilityResidual = 10.0;
                        const double minResidualImprovement = 0.5;
                        var currentMaxResidual = Math.Max(
                            Math.Abs(currentEastResidual),
                            Math.Max(Math.Abs(currentSouthResidual), Math.Abs(currentNorthResidual)));
                        var refinedMaxResidual = Math.Max(
                            Math.Abs(compatibilityEastResidual),
                            Math.Max(Math.Abs(compatibilitySouthResidual), Math.Abs(compatibilityNorthResidual)));
                        var eastSegmentChanged =
                            compatibilityEastA.GetDistanceTo(eastBoundarySegmentA) > 0.01 ||
                            compatibilityEastB.GetDistanceTo(eastBoundarySegmentB) > 0.01 ||
                            !string.Equals(compatibilityEastLayer, eastSource, StringComparison.OrdinalIgnoreCase);
                        if (eastSegmentChanged &&
                            refinedMaxResidual <= maxAcceptedEastCompatibilityResidual &&
                            (currentMaxResidual - refinedMaxResidual) >= minResidualImprovement)
                        {
                            eastBoundarySegmentA = compatibilityEastA;
                            eastBoundarySegmentB = compatibilityEastB;
                            eastSource = compatibilityEastLayer;
                            eastBoundaryU = compatibilityEastMidU;

                            if (emitQuarterVerify)
                            {
                                logger?.WriteLine(
                                    $"VERIFY-QTR-EAST-REFINE sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                    $"source={eastSource} currentMaxResidual={currentMaxResidual:0.###} refinedMaxResidual={refinedMaxResidual:0.###} " +
                                    $"eastResidual={compatibilityEastResidual:0.###} southResidual={compatibilitySouthResidual:0.###} northResidual={compatibilityNorthResidual:0.###}");
                            }
                        }
                    }

                    ResolveQuarterViewCenterCoordinates(frame, out var centerU, out var centerV);
                    var eastCornerMinBoundaryU = centerU + minQuarterSpan;
                    var southEastBoundarySegmentA = southBoundarySegmentA;
                    var southEastBoundarySegmentB = southBoundarySegmentB;
                    var allowSouthEastBoundaryRefine =
                        string.Equals(eastSource, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(eastSource, "L-SEC-2012", StringComparison.OrdinalIgnoreCase);
                    if (allowSouthEastBoundaryRefine &&
                        hasSouthBoundarySegment &&
                        TryResolveQuarterViewEastSideHorizontalBoundarySegment(
                            frame,
                            boundarySegments,
                            southSource,
                            northBand: false,
                            eastCornerMinBoundaryU,
                            out var refinedSouthEastA,
                            out var refinedSouthEastB))
                    {
                        southEastBoundarySegmentA = refinedSouthEastA;
                        southEastBoundarySegmentB = refinedSouthEastB;
                    }

                    var northEastBoundarySegmentA = northBoundarySegmentA;
                    var northEastBoundarySegmentB = northBoundarySegmentB;
                    var allowNorthEastBoundaryRefine =
                        !string.Equals(northSource, LayerUsecZero, StringComparison.OrdinalIgnoreCase);
                    if (allowNorthEastBoundaryRefine &&
                        hasNorthBoundarySegment &&
                        TryResolveQuarterViewEastSideHorizontalBoundarySegment(
                            frame,
                            boundarySegments,
                            northSource,
                            northBand: true,
                            eastCornerMinBoundaryU,
                            out var refinedNorthEastA,
                            out var refinedNorthEastB))
                    {
                        northEastBoundarySegmentA = refinedNorthEastA;
                        northEastBoundarySegmentB = refinedNorthEastB;
                    }

                    if (emitQuarterVerify)
                    {
                        WriteQuarterBoundarySelectionDiagnostics(
                            logger,
                            frame,
                            dividerLineA,
                            dividerLineB,
                            dividerPreferredU,
                            hasSouthBoundarySegment,
                            southBoundarySegmentA,
                            southBoundarySegmentB,
                            southSource,
                            hasNorthBoundarySegment,
                            northBoundarySegmentA,
                            northBoundarySegmentB,
                            northSource,
                            hasWestBoundarySegment,
                            westBoundarySegmentA,
                            westBoundarySegmentB,
                            westSource);
                    }

                    var promotedCorrectionSouthBoundary = TryPromoteQuarterViewSouthBoundaryFromCorrectionCandidate(
                        frame,
                        correctionSouthBoundarySegments,
                        centerU,
                        southFallbackOffset,
                        ref southSource,
                        ref hasSouthBoundarySegment,
                        ref southBoundarySegmentA,
                        ref southBoundarySegmentB,
                        ref southBoundaryV,
                        out var promotedCorrectionSouthSource,
                        out var promotedCurrentSouthError,
                        out var promotedCorrectionSouthError);

                    var westBoundaryLimitU = Math.Min(frame.WestEdgeU, centerU - minQuarterSpan);
                    var eastBoundaryLimitU = centerU + minQuarterSpan;
                    var southBoundaryLimitV = Math.Min(frame.SouthEdgeV, centerV - minQuarterSpan);
                    var northBoundaryLimitV = centerV + minQuarterSpan;
                    var isCorrectionSouthBoundary = IsQuarterSouthCorrectionCandidateLayer(southSource);
                    var isBlindNonCorrectionSouth = !isCorrectionSouthBoundary && southFallbackOffset <= 0.5;
                    var correctionSouthDividerU = dividerPreferredU;
                    westBoundaryU = ClampQuarterWestBoundaryUToLimit(westBoundaryU, westBoundaryLimitU);
                    eastBoundaryU = ClampQuarterEastBoundaryUToLimit(eastBoundaryU, eastBoundaryLimitU);
                    southBoundaryV = ClampQuarterSouthBoundaryVToLimit(southBoundaryV, southBoundaryLimitV);
                    northBoundaryV = ClampQuarterNorthBoundaryVToLimit(northBoundaryV, northBoundaryLimitV);

                    if ((centerU - westBoundaryU) <= minQuarterSpan)
                    {
                        westBoundaryU = westBoundaryLimitU;
                    }

                    if ((centerV - southBoundaryV) <= minQuarterSpan)
                    {
                        southBoundaryV = southBoundaryLimitV;
                    }

                    if (promotedCorrectionSouthBoundary)
                    {
                        logger?.WriteLine(
                            $"VERIFY-QTR-SOUTHMID-PROMOTE sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                            $"source={promotedCorrectionSouthSource} currentErr={promotedCurrentSouthError:0.###} " +
                            $"promotedErr={promotedCorrectionSouthError:0.###}");
                    }

                    var westAtMidU = ResolveQuarterWestBoundaryUAtV(
                        frame,
                        hasWestBoundarySegment,
                        westBoundarySegmentA,
                        westBoundarySegmentB,
                        centerV,
                        westBoundaryU,
                        westBoundaryLimitU);
                    var eastAtMidU = ResolveQuarterEastBoundaryUAtV(
                        frame,
                        hasEastBoundarySegment,
                        eastBoundarySegmentA,
                        eastBoundarySegmentB,
                        centerV,
                        eastBoundaryU,
                        eastBoundaryLimitU);
                    var eastAtMidV = centerV;
                    var southAtMidV = ResolveQuarterSouthBoundaryVAtU(
                        frame,
                        hasSouthBoundarySegment,
                        southBoundarySegmentA,
                        southBoundarySegmentB,
                        centerU,
                        southBoundaryV,
                        southBoundaryLimitV);
                    var westAtSouthU = ResolveQuarterWestBoundaryUAtV(
                        frame,
                        hasWestBoundarySegment,
                        westBoundarySegmentA,
                        westBoundarySegmentB,
                        southAtMidV,
                        westBoundaryU,
                        westBoundaryLimitU);
                    var southAtWestV = ResolveQuarterSouthBoundaryVAtU(
                        frame,
                        hasSouthBoundarySegment,
                        southBoundarySegmentA,
                        southBoundarySegmentB,
                        westAtSouthU,
                        southBoundaryV,
                        southBoundaryLimitV);
                    var southAtEastU = ResolveQuarterEastBoundaryUAtV(
                        frame,
                        hasEastBoundarySegment,
                        eastBoundarySegmentA,
                        eastBoundarySegmentB,
                        southAtMidV,
                        eastBoundaryU,
                        eastBoundaryLimitU);
                    var southAtEastV = ResolveQuarterSouthBoundaryVAtU(
                        frame,
                        hasSouthBoundarySegment,
                        southBoundarySegmentA,
                        southBoundarySegmentB,
                        southAtEastU,
                        southBoundaryV,
                        southBoundaryLimitV);
                    var northAtMidV = ResolveQuarterNorthBoundaryVAtU(
                        frame,
                        hasNorthBoundarySegment,
                        northBoundarySegmentA,
                        northBoundarySegmentB,
                        centerU,
                        northBoundaryV,
                        northBoundaryLimitV);
                    var westAtNorthU = ResolveQuarterWestBoundaryUAtV(
                        frame,
                        hasWestBoundarySegment,
                        westBoundarySegmentA,
                        westBoundarySegmentB,
                        northAtMidV,
                        westBoundaryU,
                        westBoundaryLimitU);
                    var northAtWestV = ResolveQuarterNorthBoundaryVAtU(
                        frame,
                        hasNorthBoundarySegment,
                        northBoundarySegmentA,
                        northBoundarySegmentB,
                        westAtNorthU,
                        northBoundaryV,
                        northBoundaryLimitV);
                    var northAtEastU = ResolveQuarterEastBoundaryUAtV(
                        frame,
                        hasEastBoundarySegment,
                        eastBoundarySegmentA,
                        eastBoundarySegmentB,
                        northAtMidV,
                        eastBoundaryU,
                        eastBoundaryLimitU);
                    var northAtEastV = ResolveQuarterNorthBoundaryVAtU(
                        frame,
                        hasNorthBoundarySegment,
                        northBoundarySegmentA,
                        northBoundarySegmentB,
                        northAtEastU,
                        northBoundaryV,
                        northBoundaryLimitV);
                    var westAtMidV = centerV;
                    var southAtMidU = centerU;
                    var northAtMidU = centerU;

                    const double dividerIntersectionDriftTolerance = 60.0;
                    if (hasWestBoundarySegment &&
                        TryIntersectBoundarySegmentsLocal(
                            frame,
                            frame.LeftAnchor,
                            frame.RightAnchor,
                            westBoundarySegmentA,
                            westBoundarySegmentB,
                            out var westMidU,
                            out var westMidV) &&
                        Math.Abs(westMidV - centerV) <= dividerIntersectionDriftTolerance)
                    {
                        westAtMidU = ClampQuarterWestBoundaryUToLimit(westMidU, westBoundaryLimitU);
                        westAtMidV = westMidV;
                    }

                    if (hasEastBoundarySegment &&
                        TryIntersectBoundarySegmentsLocal(
                            frame,
                            frame.LeftAnchor,
                            frame.RightAnchor,
                            eastBoundarySegmentA,
                            eastBoundarySegmentB,
                            out var eastMidU,
                            out var eastMidV) &&
                        Math.Abs(eastMidV - centerV) <= dividerIntersectionDriftTolerance)
                    {
                        eastAtMidU = ClampQuarterEastBoundaryUToLimit(eastMidU, eastBoundaryLimitU);
                        eastAtMidV = eastMidV;
                    }

                    if (hasSouthBoundarySegment && !isCorrectionSouthBoundary)
                    {
                        var resolvedSouthMid = false;
                        var resolvedSouthMidU = southAtMidU;
                        var resolvedSouthMidV = southAtMidV;

                        if (TryIntersectLocalInfiniteLineWithBoundedSegmentExtension(
                                frame,
                                dividerLineA,
                                dividerLineB,
                                southBoundarySegmentA,
                                southBoundarySegmentB,
                                maxSegmentExtension: 80.0,
                                out var apparentSouthMidU,
                                out var apparentSouthMidV) &&
                            Math.Abs(apparentSouthMidU - centerU) <= dividerIntersectionDriftTolerance)
                        {
                            resolvedSouthMid = true;
                            resolvedSouthMidU = apparentSouthMidU;
                            resolvedSouthMidV = apparentSouthMidV;
                        }
                        else if (TryIntersectBoundarySegmentsLocal(
                                     frame,
                                     dividerLineA,
                                     dividerLineB,
                                     southBoundarySegmentA,
                                     southBoundarySegmentB,
                                     out var strictSouthMidU,
                                     out var strictSouthMidV) &&
                                 Math.Abs(strictSouthMidU - centerU) <= dividerIntersectionDriftTolerance)
                        {
                            resolvedSouthMid = true;
                            resolvedSouthMidU = strictSouthMidU;
                            resolvedSouthMidV = strictSouthMidV;
                        }

                        if (resolvedSouthMid)
                        {
                            southAtMidU = resolvedSouthMidU;
                            southAtMidV = ClampQuarterSouthBoundaryV(
                                resolvedSouthMidV,
                                isCorrectionSouthBoundary,
                                southBoundaryLimitV,
                                centerV,
                                minQuarterSpan);
                        }
                    }

                    if (hasNorthBoundarySegment &&
                        TryIntersectBoundarySegmentsLocal(
                            frame,
                            dividerLineA,
                            dividerLineB,
                            northBoundarySegmentA,
                            northBoundarySegmentB,
                            out var northMidU,
                            out var northMidV) &&
                        Math.Abs(northMidU - centerU) <= dividerIntersectionDriftTolerance)
                    {
                        northAtMidU = northMidU;
                        northAtMidV = ClampQuarterNorthBoundaryVToLimit(northMidV, northBoundaryLimitV);
                    }

                    if (!hasNorthBoundarySegment &&
                        !dividerSegmentId.IsNull &&
                        dividerLineA.GetDistanceTo(dividerLineB) > 1e-6)
                    {
                        var divRelA = dividerLineA - frame.Origin;
                        var divRelB = dividerLineB - frame.Origin;
                        if (!TryConvertQuarterWorldToLocal(frame, dividerLineA, out var divAu, out var divAv) ||
                            !TryConvertQuarterWorldToLocal(frame, dividerLineB, out var divBu, out var divBv))
                        {
                            divAu = divRelA.DotProduct(frame.EastUnit);
                            divAv = divRelA.DotProduct(frame.NorthUnit);
                            divBu = divRelB.DotProduct(frame.EastUnit);
                            divBv = divRelB.DotProduct(frame.NorthUnit);
                        }

                        var divLocalA = new Point2d(divAu, divAv);
                        var divLocalB = new Point2d(divBu, divBv);
                        var dividerNorthEndpoint = divLocalA.Y >= divLocalB.Y ? divLocalA : divLocalB;
                        var currentNorthMid = new Point2d(northAtMidU, northAtMidV);
                        var northMidMove = currentNorthMid.GetDistanceTo(dividerNorthEndpoint);
                        const double maxDividerNorthMidMove = 30.0;
                        if (dividerNorthEndpoint.Y >= (centerV + minQuarterSpan) &&
                            dividerNorthEndpoint.Y <= (frame.NorthEdgeV + dividerIntersectionDriftTolerance) &&
                            northMidMove <= maxDividerNorthMidMove)
                        {
                            northAtMidU = dividerNorthEndpoint.X;
                            northAtMidV = ClampQuarterNorthBoundaryVToLimit(dividerNorthEndpoint.Y, northBoundaryLimitV);
                            logger?.WriteLine(
                                $"VERIFY-QTR-NORTHMID-DIVEND sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                $"u={northAtMidU:0.###} v={northAtMidV:0.###} move={northMidMove:0.###} dividerSource={dividerSource}");
                        }
                    }

                    if (IsOutsideRange(
                            westAtMidV,
                            frame.SouthEdgeV - dividerIntersectionDriftTolerance,
                            frame.NorthEdgeV + dividerIntersectionDriftTolerance))
                    {
                        westAtMidV = centerV;
                    }

                    if (IsOutsideRange(
                            southAtMidU,
                            frame.WestEdgeU - dividerIntersectionDriftTolerance,
                            frame.EastEdgeU + dividerIntersectionDriftTolerance))
                    {
                        southAtMidU = centerU;
                    }

                    if (IsOutsideRange(
                            northAtMidU,
                            frame.WestEdgeU - dividerIntersectionDriftTolerance,
                            frame.EastEdgeU + dividerIntersectionDriftTolerance))
                    {
                        northAtMidU = centerU;
                    }

                    var southWestLockedByApparentIntersection = false;
                    var southWestHasStrictSegmentIntersection = false;
                    var southWestEndpointAuthoritative = false;
                    var northWestLockedByApparentIntersection = false;
                    const double maxReliableApparentWestResidual = 8.0;
                    const double maxReliableSharedWestMove = 2.5;
                    var southWestHasReliableResolvedCorner = false;
                    bool DoesLocalSouthEndpointTouchSouthBandHardBoundary(Point2d localPoint)
                    {
                        var endpointWorld = QuarterViewLocalToWorld(frame, localPoint.X, localPoint.Y);
                        const double southBoundaryTouchTol = 2.0;
                        const double southBandPadding = 40.0;
                        for (var bi = 0; bi < boundarySegments.Count; bi++)
                        {
                            var candidate = boundarySegments[bi];
                            if (!TryConvertQuarterWorldToLocal(frame, candidate.A, out var segAu, out var segAv) ||
                                !TryConvertQuarterWorldToLocal(frame, candidate.B, out var segBu, out var segBv))
                            {
                                continue;
                            }

                            var du = Math.Abs(segBu - segAu);
                            var dv = Math.Abs(segBv - segAv);
                            if (du <= dv)
                            {
                                continue;
                            }

                            if (Math.Max(segAv, segBv) > (frame.MidV + southBandPadding))
                            {
                                continue;
                            }

                            if (DistancePointToSegment(endpointWorld, candidate.A, candidate.B) <= southBoundaryTouchTol)
                            {
                                return true;
                            }
                        }

                        return false;
                    }

                    // Prefer exact apparent intersections for west/south and west/north corners
                    // to eliminate small residual misses from iterative projection.
                    if (hasWestBoundarySegment &&
                        hasSouthBoundarySegment)
                    {
                        if (TryIntersectLocalInfiniteLines(
                                frame,
                                westBoundarySegmentA,
                                westBoundarySegmentB,
                                southBoundarySegmentA,
                                southBoundarySegmentB,
                                out var apparentSouthWestU,
                                out var apparentSouthWestV))
                        {
                            var westOffset = frame.WestEdgeU - apparentSouthWestU;
                            var southOffset = frame.SouthEdgeV - apparentSouthWestV;
                            const double minOffset = -6.0;
                            const double maxOffset = 90.0;
                            if (westOffset >= minOffset &&
                                westOffset <= maxOffset &&
                                southOffset >= minOffset &&
                                southOffset <= maxOffset)
                            {
                                westAtSouthU = apparentSouthWestU;
                                southAtWestV = apparentSouthWestV;
                                southWestLockedByApparentIntersection = true;
                                southWestHasReliableResolvedCorner =
                                    Math.Max(Math.Abs(westOffset), Math.Abs(southOffset)) <= maxReliableApparentWestResidual;
                                if (emitQuarterVerify)
                                {
                                    logger?.WriteLine(
                                        $"VERIFY-QTR-SW-SW-APP sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                        $"u={westAtSouthU:0.###} v={southAtWestV:0.###} westOffset={westOffset:0.###} southOffset={southOffset:0.###} " +
                                        $"southSource={southSource} dividerSource={dividerSource}");
                                }
                            }
                        }

                        if (TryIntersectBoundarySegmentsLocal(
                                frame,
                                westBoundarySegmentA,
                                westBoundarySegmentB,
                                southBoundarySegmentA,
                                southBoundarySegmentB,
                                out var southWestU,
                                out var southWestV))
                        {
                            southWestHasStrictSegmentIntersection = true;
                            if (!southWestLockedByApparentIntersection)
                            {
                                westAtSouthU = southWestU;
                                southAtWestV = southWestV;
                            }
                        }
                    }

                    if (!isCorrectionSouthBoundary &&
                        hasWestBoundarySegment &&
                        TryConvertQuarterWorldToLocal(frame, westBoundarySegmentA, out var westEndAu, out var westEndAv) &&
                        TryConvertQuarterWorldToLocal(frame, westBoundarySegmentB, out var westEndBu, out var westEndBv))
                    {
                        var westLocalA = new Point2d(westEndAu, westEndAv);
                        var westLocalB = new Point2d(westEndBu, westEndBv);
                        var southEndpoint = westLocalA.Y <= westLocalB.Y ? westLocalA : westLocalB;
                        var southBandMaxV = frame.SouthEdgeV + (2.0 * dividerIntersectionDriftTolerance);
                        var currentSouthWestLocal = new Point2d(westAtSouthU, southAtWestV);
                        var southEndpointCornerMove = currentSouthWestLocal.GetDistanceTo(southEndpoint);
                        var southEndpointTouchesSouthBandHardBoundary =
                            DoesLocalSouthEndpointTouchSouthBandHardBoundary(southEndpoint);
                        var southEndpointCanOverrideResolvedCorner =
                            !southWestLockedByApparentIntersection &&
                            !southWestHasStrictSegmentIntersection;
                        if (!southEndpointCanOverrideResolvedCorner)
                        {
                            // Once SW already resolves from the west/south boundary extensions,
                            // only let the raw west endpoint win when it is effectively the same corner.
                            southEndpointCanOverrideResolvedCorner = southEndpointCornerMove <= 5.0;
                        }
                        if (southEndpoint.X <= (centerU - minQuarterSpan) &&
                            southEndpoint.Y <= southBandMaxV &&
                            southEndpointTouchesSouthBandHardBoundary &&
                            southEndpointCanOverrideResolvedCorner)
                        {
                            westAtSouthU = southEndpoint.X;
                            southAtWestV = southEndpoint.Y;
                            southWestLockedByApparentIntersection = true;
                            southWestEndpointAuthoritative = true;
                            southWestHasReliableResolvedCorner = true;
                            if (emitQuarterVerify)
                            {
                                logger?.WriteLine(
                                    $"VERIFY-QTR-SW-SW-END sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                    $"u={westAtSouthU:0.###} v={southAtWestV:0.###} westSource={westSource}");
                            }
                        }
                    }

                    if (isBlindNonCorrectionSouth && hasWestBoundarySegment)
                    {
                        if (TryConvertQuarterWorldToLocal(frame, westBoundarySegmentA, out var westAu, out var westAv) &&
                            TryConvertQuarterWorldToLocal(frame, westBoundarySegmentB, out var westBu, out var westBv))
                        {
                            var westLocalA = new Point2d(westAu, westAv);
                            var westLocalB = new Point2d(westBu, westBv);
                            var southEndpoint = westLocalA.Y <= westLocalB.Y ? westLocalA : westLocalB;
                            // Only trust endpoint authority when the selected west segment actually
                            // reaches the south band; otherwise this can collapse SW to W-half.
                            var southBandMaxV = frame.SouthEdgeV + (2.0 * dividerIntersectionDriftTolerance);
                            if (southEndpoint.Y <= southBandMaxV &&
                                DoesLocalSouthEndpointTouchSouthBandHardBoundary(southEndpoint))
                            {
                                westAtSouthU = southEndpoint.X;
                                southAtWestV = southEndpoint.Y;
                                southWestLockedByApparentIntersection = true;
                                southWestEndpointAuthoritative = true;
                                if (emitQuarterVerify)
                                {
                                    logger?.WriteLine(
                                        $"VERIFY-QTR-SW-SW-END sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                        $"u={westAtSouthU:0.###} v={southAtWestV:0.###} westSource={westSource}");
                                }
                            }
                        }
                    }

                    if (hasWestBoundarySegment &&
                        hasNorthBoundarySegment)
                    {
                        if (TryIntersectLocalInfiniteLines(
                                frame,
                                westBoundarySegmentA,
                                westBoundarySegmentB,
                                northBoundarySegmentA,
                                northBoundarySegmentB,
                                out var apparentNorthWestU,
                                out var apparentNorthWestV))
                        {
                            var westOffset = frame.WestEdgeU - apparentNorthWestU;
                            var northOffset = apparentNorthWestV - frame.NorthEdgeV;
                            const double minOffset = -6.0;
                            var maxOffset = isBlindNonCorrectionSouth ? 120.0 : 90.0;
                            if (westOffset >= minOffset &&
                                westOffset <= maxOffset &&
                                northOffset >= minOffset &&
                                northOffset <= maxOffset)
                            {
                                westAtNorthU = apparentNorthWestU;
                                northAtWestV = apparentNorthWestV;
                                northWestLockedByApparentIntersection = true;
                                if (emitQuarterVerify)
                                {
                                    logger?.WriteLine(
                                        $"VERIFY-QTR-NW-NW-APP sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                        $"u={westAtNorthU:0.###} v={northAtWestV:0.###} westOffset={westOffset:0.###} northOffset={northOffset:0.###} " +
                                        $"westSource={westSource} northSource={northSource}");
                                }
                            }
                        }
                    }

                    var nwSnapPreferredU = (!hasNorthBoundarySegment && isBlindNonCorrectionSouth)
                        ? (westAtMidU - RoadAllowanceSecWidthMeters)
                        : westAtMidU;
                    var nwSnapCurrentLocal = new Point2d(westAtNorthU, northAtWestV);
                    var hasRawNorthWestIntersection = false;
                    if (isBlindNonCorrectionSouth &&
                        hasWestBoundarySegment &&
                        hasNorthBoundarySegment &&
                        TryIntersectLocalInfiniteLines(
                            frame,
                            westBoundarySegmentA,
                            westBoundarySegmentB,
                            northBoundarySegmentA,
                            northBoundarySegmentB,
                            out var nwRawU,
                            out var nwRawV))
                    {
                        hasRawNorthWestIntersection = true;
                        nwSnapPreferredU = nwRawU;
                        nwSnapCurrentLocal = new Point2d(nwRawU, nwRawV);
                        if (emitQuarterVerify)
                        {
                            logger?.WriteLine(
                                $"VERIFY-QTR-NW-NW-RAW sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                $"u={nwRawU:0.###} v={nwRawV:0.###}");
                        }

                        var rawWestOffset = frame.WestEdgeU - nwRawU;
                        var rawNorthOffset = nwRawV - frame.NorthEdgeV;
                        var rawWestMinOffset = isBlindNonCorrectionSouth ? -20.0 : -6.0;
                        var rawNorthMinOffset = isBlindNonCorrectionSouth ? -60.0 : -6.0;
                        var rawMaxOffset = isBlindNonCorrectionSouth ? 180.0 : 140.0;
                        if (rawWestOffset >= rawWestMinOffset &&
                            rawWestOffset <= rawMaxOffset &&
                            rawNorthOffset >= rawNorthMinOffset &&
                            rawNorthOffset <= rawMaxOffset)
                        {
                            westAtNorthU = nwRawU;
                            northAtWestV = nwRawV;
                            northWestLockedByApparentIntersection = true;
                            if (emitQuarterVerify)
                            {
                                logger?.WriteLine(
                                    $"VERIFY-QTR-NW-NW-RAWLOCK sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                    $"u={westAtNorthU:0.###} v={northAtWestV:0.###} westOffset={rawWestOffset:0.###} northOffset={rawNorthOffset:0.###}");
                            }
                        }
                    }

                    if (isBlindNonCorrectionSouth &&
                        TryResolveWestBandCornerFromHardBoundaries(
                            frame,
                            hardBoundaryCornerClusters,
                            nwSnapPreferredU,
                            westExpectedOffset,
                            northExpectedInset,
                            northBand: true,
                            new Point2d(westAtNorthU, northAtWestV),
                            maxMove: hasRawNorthWestIntersection ? 5.0 : 90.0,
                            boundarySegments,
                            requireEndpointNode: false,
                            out var snappedWestNorthLocal,
                            out var snappedWestNorthPriority))
                    {
                        var northSnapMove = new Point2d(westAtNorthU, northAtWestV).GetDistanceTo(snappedWestNorthLocal);
                        var allowBlindNorthSnap = snappedWestNorthPriority <= 0 &&
                                                  northSnapMove <= (hasRawNorthWestIntersection ? 5.0 : 90.0);
                        if (allowBlindNorthSnap)
                        {
                            westAtNorthU = snappedWestNorthLocal.X;
                            northAtWestV = snappedWestNorthLocal.Y;
                            northWestLockedByApparentIntersection = true;
                            if (emitQuarterVerify)
                            {
                                logger?.WriteLine(
                                    $"VERIFY-QTR-NW-NW-SNAP sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                    $"u={westAtNorthU:0.###} v={northAtWestV:0.###} priority={snappedWestNorthPriority} move={northSnapMove:0.###}");
                            }
                        }
                    }

                    if (!isBlindNonCorrectionSouth &&
                        TryResolveWestBandCornerFromHardBoundaries(
                            frame,
                            hardBoundaryCornerClusters,
                            westAtMidU,
                            southWestExpectedWestInset,
                            southExpectedOffset,
                            northBand: false,
                            new Point2d(westAtSouthU, southAtWestV),
                            maxMove: 35.0,
                            boundarySegments,
                            requireEndpointNode: true,
                            out var snappedWestSouthLocal,
                            out var snappedWestSouthPriority))
                    {
                        var southSnapMove = new Point2d(westAtSouthU, southAtWestV).GetDistanceTo(snappedWestSouthLocal);
                        if (snappedWestSouthPriority <= 0 && southSnapMove <= 15.0)
                        {
                            westAtSouthU = snappedWestSouthLocal.X;
                            southAtWestV = snappedWestSouthLocal.Y;
                            southWestLockedByApparentIntersection = true;
                            if (emitQuarterVerify)
                            {
                                logger?.WriteLine(
                                    $"VERIFY-QTR-SW-SW-SNAP sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                    $"u={westAtSouthU:0.###} v={southAtWestV:0.###} priority={snappedWestSouthPriority} move={southSnapMove:0.###}");
                            }
                        }
                    }

                    if (!southWestEndpointAuthoritative && !isCorrectionSouthBoundary)
                    {
                        var sharedWestSouthSource = string.Empty;
                        if (TryResolveBestWestCornerFromProtectedBoundaryCornerSets(
                                frame,
                                protectedEastBoundaryCorners,
                                "east",
                                protectedWestBoundaryCorners,
                                "west",
                                southWestExpectedWestInset,
                                southExpectedOffset,
                                northBand: false,
                                new Point2d(westAtSouthU, southAtWestV),
                                maxAllowedScoreRegression: 0.0,
                                out var sharedWestSouthLocal,
                                out var sharedWestSouthMove,
                                out sharedWestSouthSource))
                        {
                            westAtSouthU = sharedWestSouthLocal.X;
                            southAtWestV = sharedWestSouthLocal.Y;
                            southWestLockedByApparentIntersection = true;
                            if (sharedWestSouthMove <= maxReliableSharedWestMove)
                            {
                                southWestHasReliableResolvedCorner = true;
                            }
                        }

                        if (southWestLockedByApparentIntersection && emitQuarterVerify && !string.IsNullOrEmpty(sharedWestSouthSource))
                        {
                            logger?.WriteLine(
                                $"VERIFY-QTR-SW-SW-SHARED sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                $"u={westAtSouthU:0.###} v={southAtWestV:0.###} move={sharedWestSouthMove:0.###} source={sharedWestSouthSource}");
                        }
                    }

                    ApplyCorrectionSouthOverridesPreClamp(
                        frame,
                        boundarySegments,
                        isCorrectionSouthBoundary,
                        southBoundarySegmentA,
                        southBoundarySegmentB,
                        dividerLineA,
                        dividerLineB,
                        correctionSouthDividerU,
                        hasWestBoundarySegment,
                        westBoundarySegmentA,
                        westBoundarySegmentB,
                        ref westAtSouthU,
                        ref southAtWestV,
                        ref southAtMidU,
                        ref southAtMidV,
                        ref southAtEastV,
                        logger);

                    if (!northWestLockedByApparentIntersection &&
                        hasWestBoundarySegment &&
                        hasNorthBoundarySegment &&
                        TryIntersectBoundarySegmentsLocal(
                            frame,
                            westBoundarySegmentA,
                            westBoundarySegmentB,
                            northBoundarySegmentA,
                            northBoundarySegmentB,
                            out var northWestU,
                            out var northWestV))
                    {
                        westAtNorthU = northWestU;
                        northAtWestV = northWestV;
                    }

                    westAtMidU = ClampQuarterWestBoundaryUToLimit(westAtMidU, westBoundaryLimitU);
                    if (!southWestLockedByApparentIntersection)
                    {
                        westAtSouthU = ClampQuarterWestBoundaryUToLimit(westAtSouthU, westBoundaryLimitU);
                    }

                    if (!northWestLockedByApparentIntersection)
                    {
                        westAtNorthU = ClampQuarterWestBoundaryUToLimit(westAtNorthU, westBoundaryLimitU);
                    }
                    var westSouthV = southAtWestV;
                    var westNorthV = northAtWestV;
                    if (!southWestLockedByApparentIntersection)
                    {
                        westSouthV = ClampQuarterSouthBoundaryVToLimit(westSouthV, southBoundaryLimitV);
                    }
                    if (!northWestLockedByApparentIntersection)
                    {
                        westNorthV = ClampQuarterNorthBoundaryVToLimit(westNorthV, northBoundaryLimitV);
                    }
                    southAtMidV = ClampQuarterSouthBoundaryV(
                        southAtMidV,
                        isCorrectionSouthBoundary,
                        southBoundaryLimitV,
                        centerV,
                        minQuarterSpan);
                    northAtMidV = ClampQuarterNorthBoundaryVToLimit(northAtMidV, northBoundaryLimitV);

                    if (TryResolveSouthDividerCornerFromHardBoundaries(
                            frame,
                            hardBoundaryCornerClusters,
                            correctionSouthDividerU,
                            new Point2d(southAtMidU, southAtMidV),
                            out var snappedSouthMidLocal,
                            out var snappedSouthMidPriority))
                    {
                        southAtMidU = snappedSouthMidLocal.X;
                        southAtMidV = ClampQuarterSouthBoundaryV(
                            snappedSouthMidLocal.Y,
                            isCorrectionSouthBoundary,
                            southBoundaryLimitV,
                            centerV,
                            minQuarterSpan);
                        logger?.WriteLine(
                            $"VERIFY-QTR-SOUTHMID-SNAP sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                            $"u={southAtMidU:0.###} v={southAtMidV:0.###} priority={snappedSouthMidPriority} dividerU={correctionSouthDividerU:0.###} dividerSource={dividerSource}");
                    }

                    if (hasSouthBoundarySegment)
                    {
                        // Final south-mid authority: extend the active 1/4 divider line
                        // down to the south RA boundary.
                        // This preserves the apparent intersection the user expects for blind crossings.
                        var dividerStartWorld = dividerLineA;
                        var dividerEndWorld = dividerLineB;
                        var divextResolved = false;
                        var divextFound = false;
                        var divextMove = double.NaN;
                        var divendResolved = false;
                        if (dividerStartWorld.GetDistanceTo(dividerEndWorld) > 1e-6 &&
                            TryIntersectLocalInfiniteLineWithBoundedSegmentExtension(
                                frame,
                                dividerStartWorld,
                                dividerEndWorld,
                                southBoundarySegmentA,
                                southBoundarySegmentB,
                                maxSegmentExtension: 80.0,
                                out var dividerSouthMidU,
                                out var dividerSouthMidV))
                        {
                            divextFound = true;
                            const double maxDividerSouthMidMove = 20.0;
                            var currentSouthMid = new Point2d(southAtMidU, southAtMidV);
                            var dividerSouthMid = new Point2d(dividerSouthMidU, dividerSouthMidV);
                            divextMove = currentSouthMid.GetDistanceTo(dividerSouthMid);
                            if (currentSouthMid.GetDistanceTo(dividerSouthMid) <= maxDividerSouthMidMove)
                            {
                                southAtMidU = dividerSouthMidU;
                                southAtMidV = ClampQuarterSouthBoundaryV(
                                    dividerSouthMidV,
                                    isCorrectionSouthBoundary,
                                    southBoundaryLimitV,
                                    centerV,
                                    minQuarterSpan);
                                divextResolved = true;
                                logger?.WriteLine(
                                    $"VERIFY-QTR-SOUTHMID-DIVEXT sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                    $"u={southAtMidU:0.###} v={southAtMidV:0.###} dividerSource={dividerSource}");
                            }
                        }

                        if (!divextResolved &&
                            isBlindNonCorrectionSouth &&
                            dividerStartWorld.GetDistanceTo(dividerEndWorld) > 1e-6)
                        {
                            var divRelA = dividerStartWorld - frame.Origin;
                            var divRelB = dividerEndWorld - frame.Origin;
                            if (!TryConvertQuarterWorldToLocal(frame, dividerStartWorld, out var divAu, out var divAv) ||
                                !TryConvertQuarterWorldToLocal(frame, dividerEndWorld, out var divBu, out var divBv))
                            {
                                divAu = divRelA.DotProduct(frame.EastUnit);
                                divAv = divRelA.DotProduct(frame.NorthUnit);
                                divBu = divRelB.DotProduct(frame.EastUnit);
                                divBv = divRelB.DotProduct(frame.NorthUnit);
                            }

                            var divLocalA = new Point2d(divAu, divAv);
                            var divLocalB = new Point2d(divBu, divBv);
                            var dividerSouthEndpoint = divLocalA.Y <= divLocalB.Y ? divLocalA : divLocalB;
                            if (dividerSouthEndpoint.Y <= (centerV + (2.0 * dividerIntersectionDriftTolerance)))
                            {
                                var currentSouthMid = new Point2d(southAtMidU, southAtMidV);
                                divextMove = currentSouthMid.GetDistanceTo(dividerSouthEndpoint);
                                southAtMidU = dividerSouthEndpoint.X;
                                southAtMidV = dividerSouthEndpoint.Y;
                                divendResolved = true;
                                logger?.WriteLine(
                                    $"VERIFY-QTR-SOUTHMID-DIVEND sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                    $"u={southAtMidU:0.###} v={southAtMidV:0.###} dividerSource={dividerSource}");
                            }
                        }

                        if (emitQuarterVerify)
                        {
                            logger?.WriteLine(
                                $"VERIFY-QTR-SOUTHMID-DIVEXT-INPUT sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                $"divider={dividerLineA.X:0.###},{dividerLineA.Y:0.###}->{dividerLineB.X:0.###},{dividerLineB.Y:0.###} " +
                                $"south={southBoundarySegmentA.X:0.###},{southBoundarySegmentA.Y:0.###}->{southBoundarySegmentB.X:0.###},{southBoundarySegmentB.Y:0.###} " +
                                $"found={divextFound} resolved={divextResolved} divend={divendResolved} move={(double.IsNaN(divextMove) ? "NA" : divextMove.ToString("0.###"))} " +
                                $"dividerSource={dividerSource}");
                        }
                    }

                    ApplyCorrectionSouthEastProjection(
                        frame,
                        boundarySegments,
                        isCorrectionSouthBoundary,
                        centerU,
                        centerV,
                        minQuarterSpan,
                        southBoundaryLimitV,
                        westAtSouthU,
                        westSouthV,
                        southAtMidU,
                        southAtMidV,
                        ref southAtEastU,
                        ref southAtEastV);

                    var southEastLockedByApparentIntersection = false;
                    var northEastLockedByApparentIntersection = false;
                    const double maxReliableApparentEastResidual = 8.0;
                    const double maxQuarterBoundaryEndpointRescueMove = 40.0;
                    var southEastHasReliableApparentIntersection = false;
                    var northEastHasReliableApparentIntersection = false;
                    var southEastEndpointAuthoritative = false;
                    var northEastEndpointAuthoritative = false;
                    if (hasEastBoundarySegment)
                    {
                        ResolveQuarterEastMidAtCenter(
                            frame,
                            hasEastBoundarySegment,
                            eastBoundarySegmentA,
                            eastBoundarySegmentB,
                            centerV,
                            eastBoundaryU,
                            eastBoundaryLimitU,
                            dividerIntersectionDriftTolerance,
                            out eastAtMidU,
                            out eastAtMidV);

                        var resolvedLocalCorrectionWest = false;
                        var resolvedLocalCorrectionEast = false;
                        if (!isCorrectionSouthBoundary &&
                            !hasSouthBoundarySegment)
                        {
                            if (hasWestBoundarySegment &&
                                TryResolveQuarterSouthWestCorrectionCorner(
                                    frame,
                                    boundarySegments,
                                    westBoundarySegmentA,
                                    westBoundarySegmentB,
                                    centerU,
                                    minQuarterSpan,
                                    out var correctionSouthWestU,
                                    out var correctionSouthWestV,
                                    out var correctionWestOffset,
                                    out var correctionWestSouthOffset))
                            {
                                westAtSouthU = correctionSouthWestU;
                                southAtWestV = ClampQuarterSouthBoundaryV(
                                    correctionSouthWestV,
                                    isCorrectionSouthBoundary,
                                    southBoundaryLimitV,
                                    centerV,
                                    minQuarterSpan);
                                southWestLockedByApparentIntersection = true;
                                resolvedLocalCorrectionWest = true;
                                if (emitQuarterVerify)
                                {
                                    logger?.WriteLine(
                                        $"VERIFY-QTR-SW-SW-CORR-FALLBACK sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                        $"u={westAtSouthU:0.###} v={southAtWestV:0.###} westOffset={correctionWestOffset:0.###} southOffset={correctionWestSouthOffset:0.###} " +
                                        "source=local-correction-west-tie");
                                }
                            }

                            if (TryResolveQuarterSouthEastCorrectionCorner(
                                    frame,
                                    boundarySegments,
                                    eastBoundarySegmentA,
                                    eastBoundarySegmentB,
                                    centerU,
                                    minQuarterSpan,
                                    out var correctionSouthEastU,
                                    out var correctionSouthEastV,
                                    out var correctionEastOffset,
                                    out var correctionSouthOffset))
                            {
                                southAtEastU = correctionSouthEastU;
                                southAtEastV = ClampQuarterSouthBoundaryV(
                                    correctionSouthEastV,
                                    isCorrectionSouthBoundary,
                                    southBoundaryLimitV,
                                    centerV,
                                    minQuarterSpan);
                                southEastLockedByApparentIntersection = true;
                                resolvedLocalCorrectionEast = true;
                                if (emitQuarterVerify)
                                {
                                    logger?.WriteLine(
                                        $"VERIFY-QTR-SE-SE-CORR-FALLBACK sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                        $"u={southAtEastU:0.###} v={southAtEastV:0.###} eastOffset={correctionEastOffset:0.###} southOffset={correctionSouthOffset:0.###} " +
                                        "source=local-correction-east-tie");
                                }
                            }

                            if (resolvedLocalCorrectionWest &&
                                resolvedLocalCorrectionEast &&
                                TryIntersectBoundarySegmentWithLocalLine(
                                    frame,
                                    dividerLineA,
                                    dividerLineB,
                                    westAtSouthU,
                                    southAtWestV,
                                    southAtEastU,
                                    southAtEastV,
                                    out var correctionSouthMidU,
                                    out var correctionSouthMidV))
                            {
                                southAtMidU = correctionSouthMidU;
                                southAtMidV = ClampQuarterSouthBoundaryV(
                                    correctionSouthMidV,
                                    isCorrectionSouthBoundary,
                                    southBoundaryLimitV,
                                    centerV,
                                    minQuarterSpan);
                                if (emitQuarterVerify)
                                {
                                    logger?.WriteLine(
                                        $"VERIFY-QTR-SOUTHMID-CORR-FALLBACK sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                        $"u={southAtMidU:0.###} v={southAtMidV:0.###} note=local-correction-west-east-trend");
                                }
                            }
                        }

                        var southEastHasStrictSegmentIntersection = false;
                        if (hasSouthBoundarySegment)
                        {
                            if (TryResolveQuarterApparentEastCornerIntersection(
                                    frame,
                                    eastBoundarySegmentA,
                                    eastBoundarySegmentB,
                                    southBoundarySegmentA,
                                    southBoundarySegmentB,
                                    northBand: false,
                                    out var apparentSouthEastU,
                                    out var apparentSouthEastV,
                                    out var eastOffset,
                                    out var southOffset))
                            {
                                southAtEastU = apparentSouthEastU;
                                southAtEastV = apparentSouthEastV;
                                southEastLockedByApparentIntersection = true;
                                southEastHasReliableApparentIntersection =
                                    Math.Max(Math.Abs(eastOffset), Math.Abs(southOffset)) <= maxReliableApparentEastResidual;
                                if (isCorrectionSouthBoundary)
                                {
                                    southEastEndpointAuthoritative = true;
                                }
                                if (emitQuarterVerify)
                                {
                                    logger?.WriteLine(
                                        $"VERIFY-QTR-SE-SE-APP sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                        $"u={southAtEastU:0.###} v={southAtEastV:0.###} eastOffset={eastOffset:0.###} southOffset={southOffset:0.###} " +
                                        $"eastSource={eastSource} southSource={southSource}");
                                }
                            }

                            ResolveQuarterEastCornerFromBoundarySegments(
                                frame,
                                    eastBoundarySegmentA,
                                    eastBoundarySegmentB,
                                    southEastBoundarySegmentA,
                                    southEastBoundarySegmentB,
                                    southEastLockedByApparentIntersection,
                                    ref southAtEastU,
                                    ref southAtEastV);

                            if (TryIntersectBoundarySegmentsLocal(
                                    frame,
                                    eastBoundarySegmentA,
                                    eastBoundarySegmentB,
                                    southEastBoundarySegmentA,
                                    southEastBoundarySegmentB,
                                    out var strictSouthEastU,
                                    out var strictSouthEastV))
                            {
                                southEastHasStrictSegmentIntersection = true;
                                if (!southEastLockedByApparentIntersection)
                                {
                                    southAtEastU = strictSouthEastU;
                                    southAtEastV = strictSouthEastV;
                                }
                            }

                            if (!isCorrectionSouthBoundary &&
                                (string.Equals(eastSource, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(eastSource, "L-SEC-2012", StringComparison.OrdinalIgnoreCase)) &&
                                TryConvertQuarterWorldToLocal(frame, eastBoundarySegmentA, out var eastAu, out var eastAv) &&
                                TryConvertQuarterWorldToLocal(frame, eastBoundarySegmentB, out var eastBu, out var eastBv))
                            {
                                var eastLocalA = new Point2d(eastAu, eastAv);
                                var eastLocalB = new Point2d(eastBu, eastBv);
                                var eastEndpoint = eastLocalA.Y <= eastLocalB.Y ? eastLocalA : eastLocalB;
                                var southBandMaxV = frame.SouthEdgeV + (2.0 * dividerIntersectionDriftTolerance);
                                var currentSouthEastLocal = new Point2d(southAtEastU, southAtEastV);
                                var eastEndpointCornerMove = currentSouthEastLocal.GetDistanceTo(eastEndpoint);
                                var eastEndpointCanOverrideResolvedCorner =
                                    !southEastLockedByApparentIntersection &&
                                    !southEastHasStrictSegmentIntersection;
                                if (!eastEndpointCanOverrideResolvedCorner)
                                {
                                    // Once SE already resolves from the east/south boundary extensions,
                                    // only let the raw east endpoint win when it is effectively the same corner.
                                    eastEndpointCanOverrideResolvedCorner = eastEndpointCornerMove <= 5.0;
                                }
                                if (eastEndpoint.X >= (centerU + minQuarterSpan) &&
                                    eastEndpoint.Y <= southBandMaxV &&
                                    eastEndpointCanOverrideResolvedCorner)
                                {
                                    southAtEastU = eastEndpoint.X;
                                    southAtEastV = eastEndpoint.Y;
                                    southEastLockedByApparentIntersection = true;
                                    southEastEndpointAuthoritative = true;
                                    if (emitQuarterVerify)
                                    {
                                        logger?.WriteLine(
                                            $"VERIFY-QTR-SE-SE-END sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                            $"u={southAtEastU:0.###} v={southAtEastV:0.###} eastSource={eastSource}");
                                    }
                                }
                            }

                            if (TryConvertQuarterWorldToLocal(frame, southEastBoundarySegmentA, out var southAu, out var southAv) &&
                                TryConvertQuarterWorldToLocal(frame, southEastBoundarySegmentB, out var southBu, out var southBv))
                            {
                                var southLocalA = new Point2d(southAu, southAv);
                                var southLocalB = new Point2d(southBu, southBv);
                                var eastEndpoint = southLocalA.X >= southLocalB.X ? southLocalA : southLocalB;
                                var southBandMaxV = frame.SouthEdgeV + (2.0 * dividerIntersectionDriftTolerance);
                                var currentSouthEastLocal = new Point2d(southAtEastU, southAtEastV);
                                var maxSouthBoundaryEndpointMove = southEastHasStrictSegmentIntersection
                                    ? 2.5
                                    : maxQuarterBoundaryEndpointRescueMove;
                                if (eastEndpoint.X >= (centerU + minQuarterSpan) &&
                                    eastEndpoint.Y <= southBandMaxV &&
                                    currentSouthEastLocal.GetDistanceTo(eastEndpoint) <= maxSouthBoundaryEndpointMove)
                                {
                                    southAtEastU = eastEndpoint.X;
                                    southAtEastV = eastEndpoint.Y;
                                    southEastEndpointAuthoritative = true;
                                    if (emitQuarterVerify)
                                    {
                                        logger?.WriteLine(
                                            $"VERIFY-QTR-SE-SE-END sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                            $"u={southAtEastU:0.###} v={southAtEastV:0.###} southSource={southSource}");
                                    }
                                }
                            }

                            if (!southEastEndpointAuthoritative &&
                                !isCorrectionSouthBoundary &&
                                TryResolveSouthEastCornerFromEndpointCornerClusters(
                                    frame,
                                    boundarySegments,
                                    hardBoundaryCornerClusters,
                                    new Point2d(southAtEastU, southAtEastV),
                                    eastExpectedOffset,
                                    southExpectedOffset,
                                    requireEndpointNode: true,
                                    out var southEndpointLocal,
                                    out var southEndpointPriority,
                                    out var southEndpointMove,
                                    out var southEndpointDistance))
                            {
                                southAtEastU = southEndpointLocal.X;
                                southAtEastV = southEndpointLocal.Y;
                                southEastLockedByApparentIntersection = true;
                                southEastEndpointAuthoritative = true;
                                if (emitQuarterVerify)
                                {
                                    logger?.WriteLine(
                                        $"VERIFY-QTR-SE-SE-ENDPT sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                        $"u={southAtEastU:0.###} v={southAtEastV:0.###} priority={southEndpointPriority} move={southEndpointMove:0.###} " +
                                        $"endpointDist={southEndpointDistance:0.###}");
                                }
                            }
                        }

                    if (hasNorthBoundarySegment)
                    {
                        var correctionAdjoiningForNorthEast =
                            IsQuarterNorthEastCorrectionAdjoining(isCorrectionSouthBoundary, northSource);
                        var northEastHasStrictSegmentIntersection = false;

                        if (!correctionAdjoiningForNorthEast)
                        {
                            if (TryIntersectBoundarySegmentsLocal(
                                        frame,
                                        eastBoundarySegmentA,
                                        eastBoundarySegmentB,
                                        northEastBoundarySegmentA,
                                        northEastBoundarySegmentB,
                                        out var strictNorthEastU,
                                        out var strictNorthEastV))
                            {
                                northEastHasStrictSegmentIntersection = true;
                                northAtEastU = strictNorthEastU;
                                northAtEastV = strictNorthEastV;
                                northEastLockedByApparentIntersection = true;
                                if (emitQuarterVerify)
                                {
                                        logger?.WriteLine(
                                            $"VERIFY-QTR-NE-NE-STRICT sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                            $"u={northAtEastU:0.###} v={northAtEastV:0.###} eastSource={eastSource} northSource={northSource}");
                                    }
                                }
                                else if (emitQuarterVerify)
                                {
                                    logger?.WriteLine(
                                        $"VERIFY-QTR-NE-NE-STRICT sec={frame.SectionNumber} handle={frame.SectionId.Handle}: found=False eastSource={eastSource} northSource={northSource}");
                                }

                                if (TryConvertQuarterWorldToLocal(frame, northEastBoundarySegmentA, out var northAu, out var northAv) &&
                                    TryConvertQuarterWorldToLocal(frame, northEastBoundarySegmentB, out var northBu, out var northBv))
                                {
                                    var northLocalA = new Point2d(northAu, northAv);
                                    var northLocalB = new Point2d(northBu, northBv);
                                    var eastEndpoint = northLocalA.X >= northLocalB.X ? northLocalA : northLocalB;
                                    var northBandMinV = frame.NorthEdgeV - (2.0 * dividerIntersectionDriftTolerance);
                                    var currentNorthEastLocal = new Point2d(northAtEastU, northAtEastV);
                                    var maxNorthBoundaryEndpointMove = northEastHasStrictSegmentIntersection
                                        ? 2.5
                                        : maxQuarterBoundaryEndpointRescueMove;
                                    if (eastEndpoint.X >= (centerU + minQuarterSpan) &&
                                        eastEndpoint.Y >= northBandMinV &&
                                        currentNorthEastLocal.GetDistanceTo(eastEndpoint) <= maxNorthBoundaryEndpointMove)
                                    {
                                        northAtEastU = eastEndpoint.X;
                                        northAtEastV = eastEndpoint.Y;
                                        northEastEndpointAuthoritative = true;
                                        if (emitQuarterVerify)
                                        {
                                            logger?.WriteLine(
                                                $"VERIFY-QTR-NE-NE-END sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                                $"u={northAtEastU:0.###} v={northAtEastV:0.###} northSource={northSource}");
                                        }
                                    }
                                }

                                if (!northEastEndpointAuthoritative &&
                                    !isBlindNonCorrectionSouth &&
                                    TryResolveNorthEastCornerFromEndpointCornerClusters(
                                        frame,
                                        boundarySegments,
                                        hardBoundaryCornerClusters,
                                        new Point2d(northAtEastU, northAtEastV),
                                        eastExpectedOffset,
                                        northExpectedInset,
                                        requireEndpointNode: true,
                                        out var endpointClusterLocal,
                                        out var endpointClusterPriority,
                                        out var endpointClusterMove,
                                        out var endpointClusterDistance))
                                {
                                    northAtEastU = endpointClusterLocal.X;
                                    northAtEastV = endpointClusterLocal.Y;
                                    northEastLockedByApparentIntersection = true;
                                    northEastEndpointAuthoritative = true;
                                    if (emitQuarterVerify)
                                    {
                                        logger?.WriteLine(
                                            $"VERIFY-QTR-NE-NE-ENDPT sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                            $"u={northAtEastU:0.###} v={northAtEastV:0.###} priority={endpointClusterPriority} move={endpointClusterMove:0.###} " +
                                            $"endpointDist={endpointClusterDistance:0.###} correctionAdjoining=False");
                                    }
                                }
                            }
                            else
                            {
                                if (TryResolveQuarterApparentEastCornerIntersection(
                                        frame,
                                        eastBoundarySegmentA,
                                        eastBoundarySegmentB,
                                        northEastBoundarySegmentA,
                                        northEastBoundarySegmentB,
                                        northBand: true,
                                        out var apparentNorthEastU,
                                        out var apparentNorthEastV,
                                        out var eastOffset,
                                        out var northOffset))
                                {
                                    northAtEastU = apparentNorthEastU;
                                    northAtEastV = apparentNorthEastV;
                                    northEastLockedByApparentIntersection = true;
                                    northEastHasReliableApparentIntersection =
                                        Math.Max(Math.Abs(eastOffset), Math.Abs(northOffset)) <= maxReliableApparentEastResidual;
                                    if (emitQuarterVerify)
                                    {
                                        logger?.WriteLine(
                                            $"VERIFY-QTR-NE-NE-APP sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                            $"u={northAtEastU:0.###} v={northAtEastV:0.###} eastOffset={eastOffset:0.###} northOffset={northOffset:0.###} " +
                                            $"eastSource={eastSource} northSource={northSource}");
                                    }
                                }

                                ResolveQuarterEastCornerFromBoundarySegments(
                                    frame,
                                    eastBoundarySegmentA,
                                    eastBoundarySegmentB,
                                    northEastBoundarySegmentA,
                                    northEastBoundarySegmentB,
                                    northEastLockedByApparentIntersection,
                                    ref northAtEastU,
                                    ref northAtEastV);

                            }
                        }

                        if (isBlindNonCorrectionSouth && hasNorthBoundarySegment)
                        {
                            var isCorrectionAdjoiningSection =
                                IsQuarterNorthEastCorrectionAdjoining(isCorrectionSouthBoundary, northSource);
                            var neSnapPreferredU = northAtEastU;
                            var hasRawNorthEastIntersection = false;
                            var neRawU = northAtEastU;
                            var neRawV = northAtEastV;
                            if (TryIntersectLocalInfiniteLines(
                                    frame,
                                    eastBoundarySegmentA,
                                    eastBoundarySegmentB,
                                    northBoundarySegmentA,
                                    northBoundarySegmentB,
                                    out neRawU,
                                    out neRawV))
                            {
                                hasRawNorthEastIntersection = true;
                                neSnapPreferredU = neRawU;
                                if (emitQuarterVerify)
                                {
                                    logger?.WriteLine(
                                        $"VERIFY-QTR-NE-NE-RAW sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                        $"u={neRawU:0.###} v={neRawV:0.###}");
                                }
                            }

                            var resolvedByHardNode = false;
                            if (TryResolveNorthEastCornerFromEndpointCornerClusters(
                                    frame,
                                    boundarySegments,
                                    hardBoundaryCornerClusters,
                                    new Point2d(northAtEastU, northAtEastV),
                                    eastExpectedOffset,
                                    northExpectedInset,
                                    requireEndpointNode: !isCorrectionAdjoiningSection,
                                    out var endpointClusterLocal,
                                    out var endpointClusterPriority,
                                    out var endpointClusterMove,
                                    out var endpointClusterDistance))
                            {
                                northAtEastU = endpointClusterLocal.X;
                                northAtEastV = endpointClusterLocal.Y;
                                northEastLockedByApparentIntersection = true;
                                northEastEndpointAuthoritative = !isCorrectionAdjoiningSection;
                                resolvedByHardNode = true;
                                if (emitQuarterVerify)
                                {
                                    logger?.WriteLine(
                                        $"VERIFY-QTR-NE-NE-ENDPT sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                        $"u={northAtEastU:0.###} v={northAtEastV:0.###} priority={endpointClusterPriority} move={endpointClusterMove:0.###} " +
                                        $"endpointDist={endpointClusterDistance:0.###} correctionAdjoining={isCorrectionAdjoiningSection}");
                                }
                            }

                            if (!isCorrectionAdjoiningSection)
                            {
                                // Non-correction-adjoining NE corners must terminate at a real endpoint corner.
                                // Do not fall back to apparent/projection-only solutions that require extension-only intersections.
                                if (!resolvedByHardNode)
                                {
                                    if (TryResolveNonCorrectionNorthEastFromEastEndpoints(
                                            frame,
                                            boundarySegments,
                                            eastBoundarySegmentA,
                                            eastBoundarySegmentB,
                                            northBoundarySegmentA,
                                            northBoundarySegmentB,
                                            out var endpointFallbackLocal))
                                    {
                                        northAtEastU = endpointFallbackLocal.X;
                                        northAtEastV = endpointFallbackLocal.Y;
                                        northEastLockedByApparentIntersection = true;
                                        northEastEndpointAuthoritative = true;
                                        resolvedByHardNode = true;
                                        if (emitQuarterVerify)
                                        {
                                            logger?.WriteLine(
                                                $"VERIFY-QTR-NE-NE-ENDPT-FALLBACK sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                                $"u={northAtEastU:0.###} v={northAtEastV:0.###} note=east-endpoint-with-north-node");
                                        }
                                    }
                                    else if (emitQuarterVerify)
                                    {
                                        logger?.WriteLine(
                                            $"VERIFY-QTR-NE-NE-ENDPT-FALLBACK-SKIP sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                            "note=no-north-node-linked-east-endpoint-kept-prior-ne");
                                    }
                                }

                                // Stop here: non-correction sections must remain endpoint-authoritative.
                            }
                            else
                            {
                                if (!resolvedByHardNode &&
                                    TryResolveNorthEastCornerFromEastHardNode(
                                        frame,
                                        boundarySegments,
                                        hardBoundaryCornerClusters,
                                        eastBoundarySegmentA,
                                        eastBoundarySegmentB,
                                        new Point2d(northAtEastU, northAtEastV),
                                        out var hardNodeLocal,
                                        out var hardNodePriority,
                                        out var hardNodeLayer,
                                        out var hardNodeClusterDistance,
                                        out var hardNodeReachGap))
                                {
                                    northAtEastU = hardNodeLocal.X;
                                    northAtEastV = hardNodeLocal.Y;
                                    northEastLockedByApparentIntersection = true;
                                    resolvedByHardNode = true;
                                    if (emitQuarterVerify)
                                    {
                                        logger?.WriteLine(
                                            $"VERIFY-QTR-NE-NE-NODE sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                            $"u={northAtEastU:0.###} v={northAtEastV:0.###} priority={hardNodePriority} clusterDist={hardNodeClusterDistance:0.###} " +
                                            $"reachGap={hardNodeReachGap:0.###} source={hardNodeLayer}");
                                    }
                                }

                                if (!resolvedByHardNode &&
                                    TryResolveEastBandCornerFromHardBoundaries(
                                        frame,
                                        hardBoundaryCornerClusters,
                                        neSnapPreferredU,
                                        eastExpectedOffset,
                                        northExpectedInset,
                                        northBand: true,
                                        new Point2d(northAtEastU, northAtEastV),
                                        maxMove: hasRawNorthEastIntersection ? 65.0 : 85.0,
                                        boundarySegments,
                                        requireEndpointNode: false,
                                        out var snappedEastNorthLocal,
                                        out var snappedEastNorthPriority))
                                {
                                    var northEastSnapMove =
                                        new Point2d(northAtEastU, northAtEastV).GetDistanceTo(snappedEastNorthLocal);
                                    var allowBlindNorthEastSnap = snappedEastNorthPriority <= 0 &&
                                                                  northEastSnapMove <= (hasRawNorthEastIntersection ? 65.0 : 85.0);
                                    if (allowBlindNorthEastSnap)
                                    {
                                        northAtEastU = snappedEastNorthLocal.X;
                                        northAtEastV = snappedEastNorthLocal.Y;
                                        northEastLockedByApparentIntersection = true;
                                        if (emitQuarterVerify)
                                        {
                                            logger?.WriteLine(
                                                $"VERIFY-QTR-NE-NE-SNAP sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                                $"u={northAtEastU:0.###} v={northAtEastV:0.###} priority={snappedEastNorthPriority} move={northEastSnapMove:0.###}");
                                        }
                                    }
                                }
                            }
                        }
                    }

                    if (!hasNorthBoundarySegment &&
                        hasEastBoundarySegment &&
                        TryResolveEastBandCornerFromHardBoundaries(
                            frame,
                            hardBoundaryCornerClusters,
                            northAtEastU,
                            eastExpectedOffset,
                            northExpectedInset,
                            northBand: true,
                            new Point2d(northAtEastU, northAtEastV),
                            maxMove: 35.0,
                            boundarySegments,
                            requireEndpointNode: true,
                            out var snappedFallbackEastNorthLocal,
                            out var snappedFallbackEastNorthPriority))
                    {
                        var northEastSnapMove =
                            new Point2d(northAtEastU, northAtEastV).GetDistanceTo(snappedFallbackEastNorthLocal);
                        northAtEastU = snappedFallbackEastNorthLocal.X;
                        northAtEastV = snappedFallbackEastNorthLocal.Y;
                        northEastLockedByApparentIntersection = true;
                        if (emitQuarterVerify)
                        {
                            logger?.WriteLine(
                                $"VERIFY-QTR-NE-NE-SNAP sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                $"u={northAtEastU:0.###} v={northAtEastV:0.###} priority={snappedFallbackEastNorthPriority} move={northEastSnapMove:0.###}");
                        }
                    }

                    eastAtMidU = ClampQuarterEastBoundaryUToLimit(eastAtMidU, eastBoundaryLimitU);
                    southAtEastU = ClampQuarterEastBoundaryUToLimit(southAtEastU, eastBoundaryLimitU);
                    northAtEastU = ClampQuarterEastBoundaryUToLimit(northAtEastU, eastBoundaryLimitU);
                    var eastSouthV = southAtEastV;
                    var eastNorthV = northAtEastV;
                    var southEastSharedExpectedInset =
                        string.Equals(southSource, LayerUsecZero, StringComparison.OrdinalIgnoreCase) &&
                        !IsCorrectionSurveyedLayer(eastSource)
                            ? CorrectionLineInsetMeters
                            : southExpectedOffset;
                    if (!southEastLockedByApparentIntersection)
                    {
                        eastSouthV = ClampQuarterSouthBoundaryVToLimit(eastSouthV, southBoundaryLimitV);
                    }

                    if (!northEastLockedByApparentIntersection)
                    {
                        eastNorthV = ClampQuarterNorthBoundaryVToLimit(eastNorthV, northBoundaryLimitV);
                    }

                    if (!southWestEndpointAuthoritative &&
                        !southWestHasReliableResolvedCorner &&
                        TryResolveWestCornerFromProtectedBoundaryCorners(
                            frame,
                            hardBoundaryCornerEndpoints,
                            southWestExpectedWestInset,
                            southExpectedOffset,
                            northBand: false,
                            new Point2d(westAtSouthU, westSouthV),
                            out var reconciledSouthWestLocal,
                            out var reconciledSouthWestMove))
                    {
                        if (reconciledSouthWestMove <= 15.0)
                        {
                            westAtSouthU = reconciledSouthWestLocal.X;
                            westSouthV = reconciledSouthWestLocal.Y;
                            if (emitQuarterVerify)
                            {
                                logger?.WriteLine(
                                    $"VERIFY-QTR-SW-SW-HARD sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                    $"u={westAtSouthU:0.###} v={westSouthV:0.###} move={reconciledSouthWestMove:0.###}");
                            }
                        }
                    }

                    if (TryResolveWestCornerFromProtectedBoundaryCorners(
                            frame,
                            hardBoundaryCornerEndpoints,
                            northWestExpectedWestInset,
                            northExpectedInset,
                            northBand: true,
                            new Point2d(westAtNorthU, westNorthV),
                            out var reconciledNorthWestLocal,
                            out var reconciledNorthWestMove))
                    {
                        if (reconciledNorthWestMove <= 15.0)
                        {
                            westAtNorthU = reconciledNorthWestLocal.X;
                            westNorthV = reconciledNorthWestLocal.Y;
                            if (emitQuarterVerify)
                            {
                                logger?.WriteLine(
                                    $"VERIFY-QTR-NW-NW-HARD sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                    $"u={westAtNorthU:0.###} v={westNorthV:0.###} move={reconciledNorthWestMove:0.###}");
                            }
                        }
                    }

                    var sharedSouthEastSource = string.Empty;
                    if (!southEastEndpointAuthoritative &&
                        !southEastHasReliableApparentIntersection &&
                        TryResolveBestEastCornerFromProtectedBoundaryCornerSets(
                            frame,
                            protectedWestBoundaryCorners,
                            "west",
                            protectedEastBoundaryCorners,
                            "east",
                            eastExpectedOffset,
                            southEastSharedExpectedInset,
                            northBand: false,
                            hardBoundaryCornerEndpoints,
                            maxAllowedScoreRegression: 0.0,
                            new Point2d(southAtEastU, eastSouthV),
                            out var sharedSouthEastLocal,
                            out var sharedSouthEastMove,
                            out sharedSouthEastSource))
                    {
                        // Shared-corner borrowing is a local cleanup pass only; large
                        // moves start swapping quarter ownership onto adjacent inset rows.
                        const double maxSouthEastSharedCornerMove = 5.0;
                        if (sharedSouthEastMove <= maxSouthEastSharedCornerMove)
                        {
                            southAtEastU = sharedSouthEastLocal.X;
                            eastSouthV = sharedSouthEastLocal.Y;
                            southEastEndpointAuthoritative = true;
                            if (emitQuarterVerify)
                            {
                                logger?.WriteLine(
                                    $"VERIFY-QTR-SE-SE-SHARED sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                    $"u={southAtEastU:0.###} v={eastSouthV:0.###} move={sharedSouthEastMove:0.###} source={sharedSouthEastSource}");
                            }
                        }
                    }

                    if (!southEastEndpointAuthoritative &&
                        !southEastHasReliableApparentIntersection &&
                        TryResolveEastCornerFromProtectedBoundaryCorners(
                            frame,
                            hardBoundaryCornerEndpoints,
                            eastExpectedOffset,
                            southExpectedOffset,
                            northBand: false,
                            new Point2d(southAtEastU, eastSouthV),
                            out var reconciledSouthEastLocal,
                            out var reconciledSouthEastMove))
                    {
                        southAtEastU = reconciledSouthEastLocal.X;
                        eastSouthV = reconciledSouthEastLocal.Y;
                        if (emitQuarterVerify)
                        {
                            logger?.WriteLine(
                                $"VERIFY-QTR-SE-SE-HARD sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                $"u={southAtEastU:0.###} v={eastSouthV:0.###} move={reconciledSouthEastMove:0.###}");
                        }
                    }

                    var canReconcileNorthEastHard =
                        !northEastEndpointAuthoritative &&
                        !northEastHasReliableApparentIntersection &&
                        !string.Equals(northSource, "fallback-north", StringComparison.OrdinalIgnoreCase);
                    if (canReconcileNorthEastHard &&
                        TryResolveEastCornerFromProtectedBoundaryCorners(
                            frame,
                            hardBoundaryCornerEndpoints,
                            eastExpectedOffset,
                            northExpectedInset,
                            northBand: true,
                            new Point2d(northAtEastU, eastNorthV),
                            out var reconciledNorthEastLocal,
                            out var reconciledNorthEastMove))
                    {
                        northAtEastU = reconciledNorthEastLocal.X;
                        eastNorthV = reconciledNorthEastLocal.Y;
                        if (emitQuarterVerify)
                        {
                            logger?.WriteLine(
                                $"VERIFY-QTR-NE-NE-HARD sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                $"u={northAtEastU:0.###} v={eastNorthV:0.###} move={reconciledNorthEastMove:0.###}");
                        }
                    }

                    var quarterSouthWestLocal = new Point2d(westAtSouthU, westSouthV);
                    var quarterSouthMidLocal = new Point2d(southAtMidU, southAtMidV);
                    var quarterSouthEastLocal = new Point2d(southAtEastU, eastSouthV);
                    var hasQuarterSouthDisplayOverride = TryResolveQuarterDisplaySouthCorrectionTrendFromEastCompanion(
                            frame,
                            boundarySegments,
                            southSource,
                            southBoundarySegmentA,
                            southBoundarySegmentB,
                            hasWestBoundarySegment,
                            westBoundarySegmentA,
                            westBoundarySegmentB,
                            dividerLineA,
                            dividerLineB,
                            hasEastBoundarySegment,
                            eastBoundarySegmentA,
                            eastBoundarySegmentB,
                            quarterSouthMidLocal,
                            quarterSouthWestLocal,
                            quarterSouthEastLocal,
                            out var correctedQuarterSouthWestLocal,
                            out var correctedQuarterSouthMidLocal,
                            out var correctedQuarterSouthEastLocal,
                            out var correctedQuarterSouthDisplaySource);
                    if (hasQuarterSouthDisplayOverride)
                    {
                        logger?.WriteLine(
                            $"VERIFY-QTR-SOUTH-DISPLAY-ALT sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                            $"sw_sw={correctedQuarterSouthWestLocal.X:0.###},{correctedQuarterSouthWestLocal.Y:0.###} " +
                            $"sw_se={correctedQuarterSouthMidLocal.X:0.###},{correctedQuarterSouthMidLocal.Y:0.###} " +
                            $"se_se={correctedQuarterSouthEastLocal.X:0.###},{correctedQuarterSouthEastLocal.Y:0.###} " +
                            $"source={correctedQuarterSouthDisplaySource}");
                    }

                    var swNw = QuarterViewLocalToWorld(frame, westAtMidU, westAtMidV);
                    var swNe = QuarterViewLocalToWorld(frame, centerU, centerV);
                    var qsecSouthMid = QuarterViewLocalToWorld(frame, southAtMidU, southAtMidV);
                    var swSe = QuarterViewLocalToWorld(frame, quarterSouthMidLocal.X, quarterSouthMidLocal.Y);
                    var swSw = QuarterViewLocalToWorld(frame, quarterSouthWestLocal.X, quarterSouthWestLocal.Y);
                    var seSw = swSe;
                    var seSe = QuarterViewLocalToWorld(frame, quarterSouthEastLocal.X, quarterSouthEastLocal.Y);
                    var neNe = QuarterViewLocalToWorld(frame, northAtEastU, eastNorthV);
                    var northMid = QuarterViewLocalToWorld(frame, northAtMidU, northAtMidV);
                    var eastMid = QuarterViewLocalToWorld(frame, eastAtMidU, eastAtMidV);
                    var protectedSouthWestCorner = hasQuarterSouthDisplayOverride
                        ? QuarterViewLocalToWorld(frame, correctedQuarterSouthWestLocal.X, correctedQuarterSouthWestLocal.Y)
                        : swSw;
                    var protectedSouthEastCorner = hasQuarterSouthDisplayOverride
                        ? QuarterViewLocalToWorld(frame, correctedQuarterSouthEastLocal.X, correctedQuarterSouthEastLocal.Y)
                        : seSe;
                    if (IsQuarterSouthCorrectionCandidateLayer(southSource))
                    {
                        TryUpdateQuarterViewVerticalDividerSegment(
                            transaction,
                            frame,
                            dividerSegmentId,
                            dividerLineA,
                            dividerLineB,
                            qsecSouthMid,
                            northMid,
                            logger);
                    }
                    // Keep the protected shared south midpoint on the true divider node.
                    // The correction-corridor display override is for quarter box draw points
                    // only; feeding that alternate display point back into the protected
                    // midpoint pool lets later sections borrow it and rewrites real divider /
                    // LSD ownership outside the intended quarter-display family.
                    protectedSouthMidCorners.Add(qsecSouthMid);
                    protectedNorthMidCorners.Add(northMid);
                    var nwW = QuarterViewLocalToWorld(frame, westAtNorthU, westNorthV);
                    logger?.WriteLine(
                        $"Quarter view SW coords sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                        $"{swNw.X:0.###},{swNw.Y:0.###} > " +
                        $"{swNe.X:0.###},{swNe.Y:0.###} > " +
                        $"{swSe.X:0.###},{swSe.Y:0.###} > " +
                        $"{swSw.X:0.###},{swSw.Y:0.###}");
                    logger?.WriteLine(
                        $"Quarter view west corners sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                        $"SW={swSw.X:0.###},{swSw.Y:0.###} NW={nwW.X:0.###},{nwW.Y:0.###}");
                    if (isCorrectionSouthBoundary ||
                        promotedCorrectionSouthBoundary ||
                        frame.SectionNumber == 6 ||
                        frame.SectionNumber == 12 ||
                        frame.SectionNumber == 11 ||
                        frame.SectionNumber == 36)
                    {
                        logger?.WriteLine(
                            $"VERIFY-QTR-SOUTHMID sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                            $"sw_se={swSe.X:0.###},{swSe.Y:0.###} se_sw={seSw.X:0.###},{seSw.Y:0.###} " +
                            $"sw_sw={swSw.X:0.###},{swSw.Y:0.###} se_se={seSe.X:0.###},{seSe.Y:0.###} " +
                            $"dividerU={correctionSouthDividerU:0.###} southSource={southSource} dividerSource={dividerSource}");
                    }

                    if (emitQuarterVerify)
                    {
                        logger?.WriteLine(
                            $"VERIFY-QTR-EAST-CORNERS sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                            $"ne_ne={neNe.X:0.###},{neNe.Y:0.###} se_se={seSe.X:0.###},{seSe.Y:0.###} " +
                            $"east_mid={eastAtMidU:0.###},{eastAtMidV:0.###} eastSource={eastSource} southSource={southSource} northSource={northSource}");
                        logger?.WriteLine(
                            $"VERIFY-QTR-WEST-CORNERS sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                            $"nw_nw={nwW.X:0.###},{nwW.Y:0.###} sw_sw={swSw.X:0.###},{swSw.Y:0.###} w_half={swNw.X:0.###},{swNw.Y:0.###} " +
                            $"westSource={westSource} southSource={southSource} northSource={northSource}");
                    }

                    // SW: west + south RA.
                    drawn += DrawQuarterViewPolygonFromLocal(
                        modelSpace,
                        transaction,
                        frame,
                        out var swBoxId,
                        new Point2d(westAtMidU, westAtMidV),
                        new Point2d(centerU, centerV),
                        quarterSouthMidLocal,
                        quarterSouthWestLocal);
                    if (!swBoxId.IsNull)
                    {
                        quarterBoxInfos.Add((
                            swBoxId,
                            frame,
                            QuarterSelection.SouthWest,
                            southWestExpectedWestInset,
                            eastExpectedOffset,
                            southExpectedOffset,
                            isCorrectionSouthBoundary,
                            hasQuarterSouthDisplayOverride,
                            correctedQuarterSouthWestLocal,
                            correctedQuarterSouthMidLocal,
                            correctedQuarterSouthEastLocal));
                    }

                    // SE: south RA only.
                    drawn += DrawQuarterViewPolygonFromLocal(
                        modelSpace,
                        transaction,
                        frame,
                        out var seBoxId,
                        new Point2d(centerU, centerV),
                        new Point2d(eastAtMidU, eastAtMidV),
                        quarterSouthEastLocal,
                        quarterSouthMidLocal);
                    if (!seBoxId.IsNull)
                    {
                        quarterBoxInfos.Add((
                            seBoxId,
                            frame,
                            QuarterSelection.SouthEast,
                            westExpectedOffset,
                            eastExpectedOffset,
                            southEastSharedExpectedInset,
                            isCorrectionSouthBoundary,
                            hasQuarterSouthDisplayOverride,
                            correctedQuarterSouthWestLocal,
                            correctedQuarterSouthMidLocal,
                            correctedQuarterSouthEastLocal));
                    }

                    // NW: west RA only.
                    drawn += DrawQuarterViewPolygonFromLocal(
                        modelSpace,
                        transaction,
                        frame,
                        out var nwBoxId,
                        new Point2d(westAtNorthU, westNorthV),
                        new Point2d(northAtMidU, northAtMidV),
                        new Point2d(centerU, centerV),
                        new Point2d(westAtMidU, westAtMidV));
                    if (!nwBoxId.IsNull)
                    {
                        quarterBoxInfos.Add((
                            nwBoxId,
                            frame,
                            QuarterSelection.NorthWest,
                            northWestExpectedWestInset,
                            eastExpectedOffset,
                            northExpectedInset,
                            false,
                            false,
                            default,
                            default,
                            default));
                    }

                    // NE: east-side apparent boundary + north boundary.
                    drawn += DrawQuarterViewPolygonFromLocal(
                        modelSpace,
                        transaction,
                        frame,
                        out var neBoxId,
                        new Point2d(northAtMidU, northAtMidV),
                        new Point2d(northAtEastU, eastNorthV),
                        new Point2d(eastAtMidU, eastAtMidV),
                        new Point2d(centerU, centerV));
                    if (!neBoxId.IsNull)
                    {
                        quarterBoxInfos.Add((
                            neBoxId,
                            frame,
                            QuarterSelection.NorthEast,
                            westExpectedOffset,
                            eastExpectedOffset,
                            southExpectedOffset,
                            false,
                            false,
                            default,
                            default,
                            default));
                    }

                    logger?.WriteLine(
                        $"Quarter view section {frame.SectionId.Handle}: west={westSource} ({westAtMidU:0.###}), east={eastSource} ({eastAtMidU:0.###}), south={southSource} ({southAtMidV:0.###}), north={northSource} ({northAtMidV:0.###}).");

                    protectedWestBoundaryCorners.Add(protectedSouthWestCorner);
                    protectedWestBoundaryCorners.Add(swNw);
                    protectedWestBoundaryCorners.Add(nwW);
                    if (hasEastBoundarySegment && hasSouthBoundarySegment)
                    {
                        protectedEastBoundaryCorners.Add(protectedSouthEastCorner);
                    }

                    if (hasEastBoundarySegment &&
                        (hasNorthBoundarySegment || northEastLockedByApparentIntersection))
                    {
                        protectedEastBoundaryCorners.Add(neNe);
                    }
                }

                var appliedSouthDisplayOverrideVertices = 0;
                foreach (var quarterBoxInfo in quarterBoxInfos)
                {
                    if (!quarterBoxInfo.HasSouthDisplayOverride ||
                        quarterBoxInfo.BoxId.IsNull ||
                        quarterBoxInfo.Quarter == QuarterSelection.NorthWest ||
                        quarterBoxInfo.Quarter == QuarterSelection.NorthEast ||
                        transaction.GetObject(quarterBoxInfo.BoxId, OpenMode.ForRead, false) is not Polyline quarterBox ||
                        quarterBox.IsErased ||
                        !quarterBox.Closed ||
                        quarterBox.NumberOfVertices < 4)
                    {
                        continue;
                    }

                    try
                    {
                        quarterBox.UpgradeOpen();
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (quarterBoxInfo.Quarter == QuarterSelection.SouthWest)
                    {
                        var targetSouthMid = QuarterViewLocalToWorld(
                            quarterBoxInfo.Frame,
                            quarterBoxInfo.SouthDisplayOverrideSouthMidLocal.X,
                            quarterBoxInfo.SouthDisplayOverrideSouthMidLocal.Y);
                        var targetSouthWest = QuarterViewLocalToWorld(
                            quarterBoxInfo.Frame,
                            quarterBoxInfo.SouthDisplayOverrideSouthWestLocal.X,
                            quarterBoxInfo.SouthDisplayOverrideSouthWestLocal.Y);
                        if (quarterBox.GetPoint2dAt(2).GetDistanceTo(targetSouthMid) > 0.01)
                        {
                            quarterBox.SetPointAt(2, targetSouthMid);
                            appliedSouthDisplayOverrideVertices++;
                        }

                        if (quarterBox.GetPoint2dAt(3).GetDistanceTo(targetSouthWest) > 0.01)
                        {
                            quarterBox.SetPointAt(3, targetSouthWest);
                            appliedSouthDisplayOverrideVertices++;
                        }
                    }
                    else if (quarterBoxInfo.Quarter == QuarterSelection.SouthEast)
                    {
                        var targetSouthEast = QuarterViewLocalToWorld(
                            quarterBoxInfo.Frame,
                            quarterBoxInfo.SouthDisplayOverrideSouthEastLocal.X,
                            quarterBoxInfo.SouthDisplayOverrideSouthEastLocal.Y);
                        var targetSouthMid = QuarterViewLocalToWorld(
                            quarterBoxInfo.Frame,
                            quarterBoxInfo.SouthDisplayOverrideSouthMidLocal.X,
                            quarterBoxInfo.SouthDisplayOverrideSouthMidLocal.Y);
                        if (quarterBox.GetPoint2dAt(2).GetDistanceTo(targetSouthEast) > 0.01)
                        {
                            quarterBox.SetPointAt(2, targetSouthEast);
                            appliedSouthDisplayOverrideVertices++;
                        }

                        if (quarterBox.GetPoint2dAt(3).GetDistanceTo(targetSouthMid) > 0.01)
                        {
                            quarterBox.SetPointAt(3, targetSouthMid);
                            appliedSouthDisplayOverrideVertices++;
                        }
                    }
                }

                if (appliedSouthDisplayOverrideVertices > 0)
                {
                    logger?.WriteLine(
                        $"VERIFY-QTR-SOUTH-DISPLAY-POST appliedVertices={appliedSouthDisplayOverrideVertices}");
                }

                var reconciledWestSharedVertices = 0;
                var reconciledWestSharedBoxes = 0;
                var reconciledEastSharedVertices = 0;
                var reconciledEastSharedBoxes = 0;
                // Post-build shared-corner reconciliation should only settle sub-local
                // disagreements between adjoining quarter boxes, not re-own a corner by a
                // full road-allowance inset.
                const double maxSharedCornerReconcileMove = 5.0;
                foreach (var quarterBoxInfo in quarterBoxInfos)
                {
                    var quarter = quarterBoxInfo.Quarter;
                    if (quarterBoxInfo.BoxId.IsNull ||
                        !(transaction.GetObject(quarterBoxInfo.BoxId, OpenMode.ForRead, false) is Polyline quarterBox) ||
                        quarterBox.IsErased ||
                        !quarterBox.Closed)
                    {
                        continue;
                    }

                    try
                    {
                        quarterBox.UpgradeOpen();
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    var reconcileWestSharedCorner =
                        quarter == QuarterSelection.SouthWest ||
                        quarter == QuarterSelection.NorthWest;
                    var reconcileEastSharedCorner =
                        quarter == QuarterSelection.SouthEast ||
                        quarter == QuarterSelection.NorthEast;
                    if (quarter == QuarterSelection.SouthEast && quarterBoxInfo.HasSouthDisplayOverride)
                    {
                        reconcileEastSharedCorner = false;
                    }
                    if (!reconcileWestSharedCorner && !reconcileEastSharedCorner)
                    {
                        continue;
                    }

                    var cornerIndex = quarter switch
                    {
                        QuarterSelection.NorthWest => 0,
                        QuarterSelection.NorthEast => 1,
                        QuarterSelection.SouthEast => 2,
                        _ => 3,
                    };
                    if (quarterBox.NumberOfVertices <= cornerIndex)
                    {
                        continue;
                    }

                    var cornerWorld = quarterBox.GetPoint2dAt(cornerIndex);
                    if (!TryConvertQuarterWorldToLocal(quarterBoxInfo.Frame, cornerWorld, out var cornerU, out var cornerV))
                    {
                        continue;
                    }

                    var currentLocal = new Point2d(cornerU, cornerV);
                    if (reconcileWestSharedCorner)
                    {
                        var northBand = quarter == QuarterSelection.NorthWest;
                        var sharedSideExpectedInset = northBand
                            ? 0.0
                            : quarterBoxInfo.SouthExpectedInset;
                        var sharedSource = string.Empty;
                        if (TryResolveBestWestCornerFromProtectedBoundaryCornerSets(
                                quarterBoxInfo.Frame,
                                protectedEastBoundaryCorners,
                                "east",
                                protectedWestBoundaryCorners,
                                "west",
                                quarterBoxInfo.WestExpectedInset,
                                sharedSideExpectedInset,
                                northBand,
                                currentLocal,
                                maxAllowedScoreRegression: 2.5,
                                out var sharedLocal,
                                out var sharedMove,
                                out sharedSource))
                        {
                            if (sharedMove <= maxSharedCornerReconcileMove)
                            {
                                var sharedWorld = QuarterViewLocalToWorld(quarterBoxInfo.Frame, sharedLocal.X, sharedLocal.Y);
                                if (cornerWorld.GetDistanceTo(sharedWorld) > 0.01)
                                {
                                    quarterBox.SetPointAt(cornerIndex, sharedWorld);
                                    protectedWestBoundaryCorners.Add(sharedWorld);
                                    reconciledWestSharedVertices++;
                                    reconciledWestSharedBoxes++;
                                    logger?.WriteLine(
                                        $"VERIFY-QTR-WEST-SHARED-POST sec={quarterBoxInfo.Frame.SectionNumber} handle={quarterBoxInfo.Frame.SectionId.Handle}: " +
                                        $"quarter={quarter} source={sharedSource} move={sharedMove:0.###}");
                                }
                            }
                        }

                    }
                    else
                    {
                        var northBand = quarter == QuarterSelection.NorthEast;
                        var sharedSideExpectedInset = northBand
                            ? 0.0
                            : quarterBoxInfo.SouthExpectedInset;
                        var sharedSource = string.Empty;
                        if (TryResolveBestEastCornerFromProtectedBoundaryCornerSets(
                                quarterBoxInfo.Frame,
                                protectedWestBoundaryCorners,
                                "west",
                                protectedEastBoundaryCorners,
                                "east",
                                quarterBoxInfo.EastExpectedInset,
                                sharedSideExpectedInset,
                                northBand,
                                hardBoundaryCornerEndpoints,
                                maxAllowedScoreRegression: 2.5,
                                currentLocal,
                                out var sharedLocal,
                                out var sharedMove,
                                out sharedSource))
                        {
                            if (sharedMove <= maxSharedCornerReconcileMove)
                            {
                                var sharedWorld = QuarterViewLocalToWorld(quarterBoxInfo.Frame, sharedLocal.X, sharedLocal.Y);
                                if (cornerWorld.GetDistanceTo(sharedWorld) > 0.01)
                                {
                                    quarterBox.SetPointAt(cornerIndex, sharedWorld);
                                    protectedEastBoundaryCorners.Add(sharedWorld);
                                    reconciledEastSharedVertices++;
                                    reconciledEastSharedBoxes++;
                                    logger?.WriteLine(
                                        $"VERIFY-QTR-EAST-SHARED-POST sec={quarterBoxInfo.Frame.SectionNumber} handle={quarterBoxInfo.Frame.SectionId.Handle}: " +
                                        $"quarter={quarter} source={sharedSource} move={sharedMove:0.###}");
                                }
                            }
                        }

                    }
                }

                var reconciledCorrectionHardVertices = 0;
                foreach (var quarterBoxInfo in quarterBoxInfos)
                {
                    if (!quarterBoxInfo.IsCorrectionSouthQuarter ||
                        quarterBoxInfo.BoxId.IsNull ||
                        (quarterBoxInfo.Quarter != QuarterSelection.SouthWest &&
                         quarterBoxInfo.Quarter != QuarterSelection.SouthEast) ||
                        transaction.GetObject(quarterBoxInfo.BoxId, OpenMode.ForRead, false) is not Polyline quarterBox ||
                        quarterBox.IsErased ||
                        !quarterBox.Closed)
                    {
                        continue;
                    }

                    var cornerIndex = quarterBoxInfo.Quarter == QuarterSelection.SouthEast ? 2 : 3;
                    if (quarterBox.NumberOfVertices <= cornerIndex)
                    {
                        continue;
                    }

                    var cornerWorld = quarterBox.GetPoint2dAt(cornerIndex);
                    if (!TryConvertQuarterWorldToLocal(quarterBoxInfo.Frame, cornerWorld, out var cornerU, out var cornerV))
                    {
                        continue;
                    }

                    var currentLocal = new Point2d(cornerU, cornerV);
                    var resolvedLocal = currentLocal;
                    var resolvedMove = double.MaxValue;
                    var foundCorrectionHard = false;
                    if (quarterBoxInfo.Quarter == QuarterSelection.SouthWest)
                    {
                        foundCorrectionHard = TryResolveWestCornerFromProtectedBoundaryCorners(
                            quarterBoxInfo.Frame,
                            hardBoundaryCornerEndpoints,
                            quarterBoxInfo.WestExpectedInset,
                            quarterBoxInfo.SouthExpectedInset,
                            northBand: false,
                            currentLocal,
                            maxAllowedScoreRegression: 5.0,
                            out resolvedLocal,
                            out resolvedMove,
                            out _);
                    }
                    else
                    {
                        foundCorrectionHard = TryResolveEastCornerFromProtectedBoundaryCorners(
                            quarterBoxInfo.Frame,
                            hardBoundaryCornerEndpoints,
                            quarterBoxInfo.EastExpectedInset,
                            quarterBoxInfo.SouthExpectedInset,
                            northBand: false,
                            endpointNodes: hardBoundaryCornerEndpoints,
                            maxAllowedScoreRegression: 5.0,
                            currentLocal,
                            out resolvedLocal,
                            out resolvedMove,
                            out _);
                    }
                    if (!foundCorrectionHard || resolvedMove > 5.0)
                    {
                        continue;
                    }

                    var resolvedWorld = QuarterViewLocalToWorld(quarterBoxInfo.Frame, resolvedLocal.X, resolvedLocal.Y);
                    if (cornerWorld.GetDistanceTo(resolvedWorld) <= 0.01)
                    {
                        continue;
                    }

                    try
                    {
                        quarterBox.UpgradeOpen();
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    quarterBox.SetPointAt(cornerIndex, resolvedWorld);
                    if (quarterBoxInfo.Quarter == QuarterSelection.SouthWest)
                    {
                        protectedWestBoundaryCorners.Add(resolvedWorld);
                    }
                    else if (quarterBoxInfo.Quarter == QuarterSelection.SouthEast)
                    {
                        protectedEastBoundaryCorners.Add(resolvedWorld);
                    }

                    reconciledCorrectionHardVertices++;
                    logger?.WriteLine(
                        $"VERIFY-QTR-CORR-HARD-POST sec={quarterBoxInfo.Frame.SectionNumber} handle={quarterBoxInfo.Frame.SectionId.Handle}: " +
                        $"quarter={quarterBoxInfo.Quarter} move={resolvedMove:0.###}");
                }

                var reconciledCorrectionEastSharedVertices = 0;
                foreach (var quarterBoxInfo in quarterBoxInfos)
                {
                    if (!quarterBoxInfo.IsCorrectionSouthQuarter ||
                        quarterBoxInfo.HasSouthDisplayOverride ||
                        quarterBoxInfo.Quarter != QuarterSelection.SouthEast ||
                        quarterBoxInfo.BoxId.IsNull ||
                        transaction.GetObject(quarterBoxInfo.BoxId, OpenMode.ForRead, false) is not Polyline quarterBox ||
                        quarterBox.IsErased ||
                        !quarterBox.Closed ||
                        quarterBox.NumberOfVertices <= 2)
                    {
                        continue;
                    }

                    var cornerWorld = quarterBox.GetPoint2dAt(2);
                    if (!TryConvertQuarterWorldToLocal(quarterBoxInfo.Frame, cornerWorld, out var cornerU, out var cornerV))
                    {
                        continue;
                    }

                    var currentLocal = new Point2d(cornerU, cornerV);
                    var foundShared = TryResolveBestEastCornerFromProtectedBoundaryCornerSets(
                            quarterBoxInfo.Frame,
                            protectedWestBoundaryCorners,
                            "west",
                            protectedEastBoundaryCorners,
                            "east",
                            quarterBoxInfo.EastExpectedInset,
                            quarterBoxInfo.SouthExpectedInset,
                            northBand: false,
                            hardBoundaryCornerEndpoints,
                            maxAllowedScoreRegression: 5.0,
                            currentLocal,
                            out var sharedLocal,
                            out var sharedMove,
                            out var sharedSource);
                    if (!foundShared)
                    {
                        var currentSouthError =
                            Math.Abs((quarterBoxInfo.Frame.SouthEdgeV - currentLocal.Y) - RoadAllowanceSecWidthMeters);
                        var fallbackFound = false;
                        var fallbackScore = double.MaxValue;
                        foreach (var candidateWorld in protectedWestBoundaryCorners)
                        {
                            if (!TryConvertQuarterWorldToLocal(quarterBoxInfo.Frame, candidateWorld, out var candidateU, out var candidateV))
                            {
                                continue;
                            }

                            if (candidateU < (quarterBoxInfo.Frame.MidU - 60.0) ||
                                candidateU > (quarterBoxInfo.Frame.EastEdgeU + 60.0) ||
                                candidateV < (quarterBoxInfo.Frame.SouthEdgeV - 60.0) ||
                                candidateV > (quarterBoxInfo.Frame.MidV + 60.0))
                            {
                                continue;
                            }

                            var candidateLocal = new Point2d(candidateU, candidateV);
                            var candidateMove = candidateLocal.GetDistanceTo(currentLocal);
                            if (candidateMove <= 1e-3 || candidateMove > 25.0)
                            {
                                continue;
                            }

                            var candidateSouthOffset = quarterBoxInfo.Frame.SouthEdgeV - candidateV;
                            if (candidateSouthOffset < 12.0 || candidateSouthOffset > 35.0)
                            {
                                continue;
                            }

                            var candidateSouthError =
                                Math.Abs(candidateSouthOffset - RoadAllowanceSecWidthMeters);
                            if (candidateSouthError + 0.5 >= currentSouthError)
                            {
                                continue;
                            }

                            var candidateEastOffset = quarterBoxInfo.Frame.EastEdgeU - candidateU;
                            var candidateScore = candidateSouthError + (0.25 * Math.Abs(candidateEastOffset));
                            if (!fallbackFound || candidateScore < fallbackScore - 1e-6)
                            {
                                fallbackFound = true;
                                fallbackScore = candidateScore;
                                sharedLocal = candidateLocal;
                                sharedMove = candidateMove;
                                sharedSource = "west-nearby";
                            }
                        }

                        foundShared = fallbackFound;
                    }

                    if (!foundShared &&
                        TryResolveDeepSouthCorrectionEastSharedCornerFromWestProtectedCorners(
                            quarterBoxInfo.Frame,
                            protectedWestBoundaryCorners,
                            hardBoundaryCornerEndpoints,
                            quarterBoxInfo.EastExpectedInset,
                            quarterBoxInfo.SouthExpectedInset,
                            currentLocal,
                            out var deepSharedLocal,
                            out var deepSharedMove))
                    {
                        foundShared = true;
                        sharedLocal = deepSharedLocal;
                        sharedMove = deepSharedMove;
                        sharedSource = "west-deep-correction-shared";
                    }

                    if (!foundShared || sharedMove > 25.0)
                    {
                        continue;
                    }

                    var sharedWorld = QuarterViewLocalToWorld(quarterBoxInfo.Frame, sharedLocal.X, sharedLocal.Y);
                    if (cornerWorld.GetDistanceTo(sharedWorld) <= 0.01)
                    {
                        continue;
                    }

                    try
                    {
                        quarterBox.UpgradeOpen();
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    quarterBox.SetPointAt(2, sharedWorld);
                    protectedEastBoundaryCorners.Add(sharedWorld);
                    reconciledCorrectionEastSharedVertices++;
                    logger?.WriteLine(
                        $"VERIFY-QTR-CORR-EAST-SHARED-POST sec={quarterBoxInfo.Frame.SectionNumber} handle={quarterBoxInfo.Frame.SectionId.Handle}: " +
                        $"move={sharedMove:0.###} source={sharedSource}");
                }

                var snappedVertices = 0;
                var snappedBoxes = 0;
                foreach (ObjectId id in modelSpace)
                {
                    if (!(transaction.GetObject(id, OpenMode.ForRead, false) is Polyline box) || box.IsErased || !box.Closed)
                    {
                        continue;
                    }

                    if (!string.Equals(box.Layer, LayerQuarterView, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Extents3d extents;
                    try
                    {
                        extents = box.GeometricExtents;
                    }
                    catch
                    {
                        continue;
                    }

                    if (!ExtentsIntersectAnyQuarterWindow(extents, frames) || box.NumberOfVertices < 3)
                    {
                        continue;
                    }

                    try
                    {
                        box.UpgradeOpen();
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        // Ignore locked/invalid entities and continue rebuilding quarter view.
                        continue;
                    }

                    var movedAny = false;
                    for (var vi = 0; vi < box.NumberOfVertices; vi++)
                    {
                        var vertex = box.GetPoint2dAt(vi);
                        if (TryGetProtectedQuarterCorner(
                                protectedSouthMidCorners,
                                protectedNorthMidCorners,
                                protectedWestBoundaryCorners,
                                protectedEastBoundaryCorners,
                                vertex,
                                out var protectedCorner))
                        {
                            if (vertex.GetDistanceTo(protectedCorner) > 0.01)
                            {
                                box.SetPointAt(vi, protectedCorner);
                                snappedVertices++;
                                movedAny = true;
                            }

                            continue;
                        }

                        var prev = box.GetPoint2dAt((vi - 1 + box.NumberOfVertices) % box.NumberOfVertices);
                        var next = box.GetPoint2dAt((vi + 1) % box.NumberOfVertices);
                        if (!TryFindQuarterVertexSnapTarget(vertex, prev, next, hardBoundaryCornerEndpoints, out var snapped))
                        {
                            continue;
                        }

                        if (vertex.GetDistanceTo(snapped) <= 0.01)
                        {
                            continue;
                        }

                        box.SetPointAt(vi, snapped);
                        snappedVertices++;
                        movedAny = true;
                    }

                if (movedAny)
                {
                    snappedBoxes++;
                }
            }

            var reconciledCorrectionSouthDisplayMidVertices = 0;
            var correctionSouthQuarterPairBySection = new Dictionary<int, (ObjectId? SouthWestBoxId, ObjectId? SouthEastBoxId, QuarterViewSectionFrame Frame)>();
            foreach (var quarterBoxInfo in quarterBoxInfos)
            {
                if (!quarterBoxInfo.IsCorrectionSouthQuarter ||
                    quarterBoxInfo.BoxId.IsNull ||
                    (quarterBoxInfo.Quarter != QuarterSelection.SouthWest &&
                     quarterBoxInfo.Quarter != QuarterSelection.SouthEast))
                {
                    continue;
                }

                if (!correctionSouthQuarterPairBySection.TryGetValue(quarterBoxInfo.Frame.SectionNumber, out var pair))
                {
                    pair = (null, null, quarterBoxInfo.Frame);
                }

                if (quarterBoxInfo.Quarter == QuarterSelection.SouthWest)
                {
                    pair.SouthWestBoxId = quarterBoxInfo.BoxId;
                }
                else
                {
                    pair.SouthEastBoxId = quarterBoxInfo.BoxId;
                }

                correctionSouthQuarterPairBySection[quarterBoxInfo.Frame.SectionNumber] = pair;
            }

            foreach (var pairEntry in correctionSouthQuarterPairBySection.Values)
            {
                if (!pairEntry.SouthWestBoxId.HasValue ||
                    !pairEntry.SouthEastBoxId.HasValue ||
                    transaction.GetObject(pairEntry.SouthWestBoxId.Value, OpenMode.ForRead, false) is not Polyline southWestBox ||
                    transaction.GetObject(pairEntry.SouthEastBoxId.Value, OpenMode.ForRead, false) is not Polyline southEastBox ||
                    southWestBox.IsErased ||
                    southEastBox.IsErased ||
                    !southWestBox.Closed ||
                    !southEastBox.Closed ||
                    southWestBox.NumberOfVertices < 4 ||
                    southEastBox.NumberOfVertices < 4)
                {
                    continue;
                }

                var currentSouthMid = southWestBox.GetPoint2dAt(2);
                var sharedSouthMid = southEastBox.GetPoint2dAt(3);
                if (currentSouthMid.GetDistanceTo(sharedSouthMid) > 0.05)
                {
                    continue;
                }

                var southWestCorner = southWestBox.GetPoint2dAt(3);
                var southEastCorner = southEastBox.GetPoint2dAt(2);
                if (!TryConvertQuarterWorldToLocal(pairEntry.Frame, currentSouthMid, out var currentMidU, out var currentMidV) ||
                    !TryConvertQuarterWorldToLocal(pairEntry.Frame, southWestCorner, out _, out var southWestV) ||
                    !TryConvertQuarterWorldToLocal(pairEntry.Frame, southEastCorner, out _, out var southEastV))
                {
                    continue;
                }

                var currentSouthOffset = pairEntry.Frame.SouthEdgeV - currentMidV;
                var southWestOffset = pairEntry.Frame.SouthEdgeV - southWestV;
                var southEastOffset = pairEntry.Frame.SouthEdgeV - southEastV;
                if (currentSouthOffset > (CorrectionLineInsetMeters + 6.0) ||
                    southEastOffset < 12.0)
                {
                    continue;
                }

                var dividerLineA = pairEntry.Frame.BottomAnchor;
                var dividerLineB = pairEntry.Frame.TopAnchor;
                if (TryResolveQuarterViewVerticalDividerSegmentFromQsec(
                        pairEntry.Frame,
                        quarterDividerSegments,
                        out _,
                        out var qsecDividerA,
                        out var qsecDividerB))
                {
                    dividerLineA = qsecDividerA;
                    dividerLineB = qsecDividerB;
                }

                var foundTarget = false;
                var bestTargetSouthMid = currentSouthMid;
                var bestTargetMove = double.MaxValue;
                var bestTargetScore = double.MaxValue;
                foreach (ObjectId boundaryId in modelSpace)
                {
                    if (!(transaction.GetObject(boundaryId, OpenMode.ForRead, false) is Entity boundaryEntity) ||
                        boundaryEntity.IsErased ||
                        !string.Equals(boundaryEntity.Layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) ||
                        !TryReadOpenLinearSegment(boundaryEntity, out var segA, out var segB))
                    {
                        continue;
                    }

                    var delta = segB - segA;
                    var eastComp = Math.Abs(delta.DotProduct(pairEntry.Frame.EastUnit));
                    var northComp = Math.Abs(delta.DotProduct(pairEntry.Frame.NorthUnit));
                    if (eastComp <= northComp)
                    {
                        continue;
                    }

                    if (!TryConvertQuarterWorldToLocal(pairEntry.Frame, segA, out var segAu, out _) ||
                        !TryConvertQuarterWorldToLocal(pairEntry.Frame, segB, out var segBu, out _))
                    {
                        continue;
                    }

                    var segMinU = Math.Min(segAu, segBu);
                    var segMaxU = Math.Max(segAu, segBu);
                    var stationGap = DistanceToClosedInterval(currentMidU, segMinU, segMaxU);
                    if (stationGap > 5.0)
                    {
                        continue;
                    }

                    if (!TryIntersectLocalInfiniteLines(
                            pairEntry.Frame,
                            dividerLineA,
                            dividerLineB,
                            segA,
                            segB,
                            out var candidateMidU,
                            out var candidateMidV))
                    {
                        continue;
                    }

                    var candidateSouthOffset = pairEntry.Frame.SouthEdgeV - candidateMidV;
                    if (candidateSouthOffset < 12.0 ||
                        candidateSouthOffset > 35.0 ||
                        candidateSouthOffset <= currentSouthOffset + 5.0)
                    {
                        continue;
                    }

                    var candidateSouthMid = QuarterViewLocalToWorld(pairEntry.Frame, candidateMidU, candidateMidV);
                    var candidateMove = currentSouthMid.GetDistanceTo(candidateSouthMid);
                    if (candidateMove <= 0.01 || candidateMove > 35.0)
                    {
                        continue;
                    }

                    var overlapWidth = Math.Min(segMaxU, pairEntry.Frame.EastEdgeU) - Math.Max(segMinU, pairEntry.Frame.WestEdgeU);
                    var score =
                        Math.Abs(candidateSouthOffset - RoadAllowanceSecWidthMeters) +
                        stationGap +
                        (Math.Max(0.0, 200.0 - overlapWidth) * 0.01);
                    if (!foundTarget || score < bestTargetScore - 1e-6)
                    {
                        foundTarget = true;
                        bestTargetSouthMid = candidateSouthMid;
                        bestTargetMove = candidateMove;
                        bestTargetScore = score;
                    }
                }

                if (!foundTarget)
                {
                    continue;
                }

                try
                {
                    southWestBox.UpgradeOpen();
                    southEastBox.UpgradeOpen();
                }
                catch (Autodesk.AutoCAD.Runtime.Exception)
                {
                    continue;
                }

                southWestBox.SetPointAt(2, bestTargetSouthMid);
                southEastBox.SetPointAt(3, bestTargetSouthMid);
                reconciledCorrectionSouthDisplayMidVertices += 2;
                logger?.WriteLine(
                    $"VERIFY-QTR-CORR-SOUTHMID-POST sec={pairEntry.Frame.SectionNumber} handle={pairEntry.Frame.SectionId.Handle}: " +
                    $"move={bestTargetMove:0.###} score={bestTargetScore:0.###}");
            }

            var correctionSouthDeflectionVertices = 0;
            var correctionSouthDeflectionBoxes = 0;
            foreach (var quarterBoxInfo in quarterBoxInfos)
            {
                if (!quarterBoxInfo.IsCorrectionSouthQuarter ||
                    quarterBoxInfo.BoxId.IsNull ||
                    (quarterBoxInfo.Quarter != QuarterSelection.SouthWest &&
                     quarterBoxInfo.Quarter != QuarterSelection.SouthEast) ||
                    transaction.GetObject(quarterBoxInfo.BoxId, OpenMode.ForRead, false) is not Polyline quarterBox ||
                    quarterBox.IsErased ||
                    !quarterBox.Closed ||
                    quarterBox.NumberOfVertices < 4)
                {
                    continue;
                }

                if (!TryInsertCorrectionSouthQuarterBoundaryDeflectionVertices(
                        quarterBoxInfo.Frame,
                        quarterBoxInfo.Quarter,
                        quarterBox,
                        boundarySegments,
                        out var insertedWorldPoints))
                {
                    continue;
                }

                correctionSouthDeflectionVertices += insertedWorldPoints.Count;
                correctionSouthDeflectionBoxes++;
                logger?.WriteLine(
                    $"VERIFY-QTR-CORR-DEFLECT-POST sec={quarterBoxInfo.Frame.SectionNumber} handle={quarterBoxInfo.Frame.SectionId.Handle}: " +
                    $"quarter={quarterBoxInfo.Quarter} inserted={insertedWorldPoints.Count} " +
                    $"points={string.Join(" | ", insertedWorldPoints.Select(p => $"{p.X:0.###},{p.Y:0.###}"))}");
            }

            foreach (var quarterBoxInfo in quarterBoxInfos)
            {
                    if (quarterBoxInfo.BoxId.IsNull ||
                        (quarterBoxInfo.Frame.SectionNumber < 1 || quarterBoxInfo.Frame.SectionNumber > 6) ||
                        transaction.GetObject(quarterBoxInfo.BoxId, OpenMode.ForRead, false) is not Polyline finalQuarterBox ||
                        finalQuarterBox.IsErased ||
                        !finalQuarterBox.Closed ||
                        finalQuarterBox.NumberOfVertices < 4)
                    {
                        continue;
                    }

                    if (quarterBoxInfo.Quarter == QuarterSelection.SouthWest)
                    {
                        var finalSouthMid = finalQuarterBox.GetPoint2dAt(2);
                        var finalSouthWest = finalQuarterBox.GetPoint2dAt(finalQuarterBox.NumberOfVertices - 1);
                        logger?.WriteLine(
                            $"VERIFY-QTR-FINAL sec={quarterBoxInfo.Frame.SectionNumber} handle={quarterBoxInfo.Frame.SectionId.Handle}: " +
                            $"quarter=SW sw_se={finalSouthMid.X:0.###},{finalSouthMid.Y:0.###} " +
                            $"sw_sw={finalSouthWest.X:0.###},{finalSouthWest.Y:0.###}");
                    }
                    else if (quarterBoxInfo.Quarter == QuarterSelection.SouthEast)
                    {
                        var finalSouthEast = finalQuarterBox.GetPoint2dAt(2);
                        var finalSouthMid = finalQuarterBox.GetPoint2dAt(finalQuarterBox.NumberOfVertices - 1);
                        logger?.WriteLine(
                            $"VERIFY-QTR-FINAL sec={quarterBoxInfo.Frame.SectionNumber} handle={quarterBoxInfo.Frame.SectionId.Handle}: " +
                            $"quarter=SE se_se={finalSouthEast.X:0.###},{finalSouthEast.Y:0.###} " +
                            $"se_sw={finalSouthMid.X:0.###},{finalSouthMid.Y:0.###}");
                    }
                }

                logger?.WriteLine($"Quarter view: refreshed {drawn} quarter box(es) across {frames.Count} section(s); erased {erased} stale box(es).");
                logger?.WriteLine($"Quarter view: shared-corner reconciliation adjusted {reconciledWestSharedVertices} west-boundary corner(s) across {reconciledWestSharedBoxes} quarter box(es).");
                logger?.WriteLine($"Quarter view: south-correction hard reconcile adjusted {reconciledCorrectionHardVertices} corner(s).");
                logger?.WriteLine($"Quarter view: south-correction east shared reconcile adjusted {reconciledCorrectionEastSharedVertices} corner(s).");
                logger?.WriteLine($"Quarter view: south-correction display south-mid reconcile adjusted {reconciledCorrectionSouthDisplayMidVertices} vertex(s).");
                logger?.WriteLine($"Quarter view: south-correction deflection reconcile adjusted {correctionSouthDeflectionVertices} vertex/vertices across {correctionSouthDeflectionBoxes} quarter box(es).");
                logger?.WriteLine($"Quarter view: endpoint snap adjusted {snappedVertices} vertex/vertices across {snappedBoxes} quarter box(es).");
                transaction.Commit();
            }
        }

        private static bool TryInsertCorrectionSouthQuarterBoundaryDeflectionVertices(
            QuarterViewSectionFrame frame,
            QuarterSelection quarter,
            Polyline quarterBox,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> boundarySegments,
            out List<Point2d> insertedWorldPoints)
        {
            insertedWorldPoints = new List<Point2d>();
            if (quarterBox == null ||
                boundarySegments == null ||
                boundarySegments.Count == 0 ||
                quarterBox.NumberOfVertices < 4 ||
                (quarter != QuarterSelection.SouthWest && quarter != QuarterSelection.SouthEast))
            {
                return false;
            }

            const double maxCorrectionTouchDistance = 2.0;
            const double maxEdgeDistance = 12.0;
            const double minEndpointSeparation = 1.0;
            const double maxSouthBandPadding = 40.0;

            var edgeStartIndex = 2;
            var edgeEndIndex = quarterBox.NumberOfVertices - 1;
            if (edgeEndIndex <= edgeStartIndex)
            {
                return false;
            }

            var edgeStartWorld = quarterBox.GetPoint2dAt(edgeStartIndex);
            var edgeEndWorld = quarterBox.GetPoint2dAt(edgeEndIndex);
            if (!TryConvertQuarterWorldToLocal(frame, edgeStartWorld, out var edgeStartU, out var edgeStartV) ||
                !TryConvertQuarterWorldToLocal(frame, edgeEndWorld, out var edgeEndU, out var edgeEndV))
            {
                return false;
            }

            var edgeVector = edgeEndWorld - edgeStartWorld;
            var edgeLengthSquared = edgeVector.DotProduct(edgeVector);
            if (edgeLengthSquared <= 1e-6)
            {
                return false;
            }

            var edgeMinV = Math.Min(edgeStartV, edgeEndV) - maxSouthBandPadding;
            var edgeMaxV = Math.Max(edgeStartV, edgeEndV) + maxSouthBandPadding;
            var correctionHorizontalSegments = new List<(Point2d AWorld, Point2d BWorld)>();
            foreach (var boundarySegment in boundarySegments)
            {
                if (!IsQuarterSouthCorrectionCandidateLayer(boundarySegment.Layer) ||
                    !TryConvertQuarterWorldToLocal(frame, boundarySegment.A, out var segAu, out var segAv) ||
                    !TryConvertQuarterWorldToLocal(frame, boundarySegment.B, out var segBu, out var segBv))
                {
                    continue;
                }

                if (Math.Abs(segBu - segAu) <= Math.Abs(segBv - segAv))
                {
                    continue;
                }

                var segMinU = Math.Min(segAu, segBu);
                var segMaxU = Math.Max(segAu, segBu);
                if (segMaxU < (Math.Min(edgeStartU, edgeEndU) - 25.0) ||
                    segMinU > (Math.Max(edgeStartU, edgeEndU) + 25.0))
                {
                    continue;
                }

                var segMidV = 0.5 * (segAv + segBv);
                if (segMidV < edgeMinV || segMidV > edgeMaxV)
                {
                    continue;
                }

                correctionHorizontalSegments.Add((boundarySegment.A, boundarySegment.B));
            }

            if (correctionHorizontalSegments.Count == 0)
            {
                return false;
            }

            var rawCandidates = new List<(double Station, Point2d World)>();
            foreach (var boundarySegment in boundarySegments)
            {
                if (!IsQuarterCorrectionDeflectionVerticalCandidateLayer(boundarySegment.Layer) ||
                    !TryConvertQuarterWorldToLocal(frame, boundarySegment.A, out var segAu, out var segAv) ||
                    !TryConvertQuarterWorldToLocal(frame, boundarySegment.B, out var segBu, out var segBv))
                {
                    continue;
                }

                if (Math.Abs(segBv - segAv) <= Math.Abs(segBu - segAu))
                {
                    continue;
                }

                var endpoints = new[]
                {
                    (World: boundarySegment.A, U: segAu, V: segAv),
                    (World: boundarySegment.B, U: segBu, V: segBv),
                };
                for (var endpointIndex = 0; endpointIndex < endpoints.Length; endpointIndex++)
                {
                    var endpoint = endpoints[endpointIndex];
                    if (endpoint.World.GetDistanceTo(edgeStartWorld) <= minEndpointSeparation ||
                        endpoint.World.GetDistanceTo(edgeEndWorld) <= minEndpointSeparation)
                    {
                        continue;
                    }

                    if (endpoint.V < edgeMinV || endpoint.V > edgeMaxV)
                    {
                        continue;
                    }

                    if (IsQuarterCorrectionDeflectionSecLayer(boundarySegment.Layer) &&
                        endpoint.U > (frame.MidU + 1.0))
                    {
                        continue;
                    }

                    var edgeOffset = DistancePointToSegment(endpoint.World, edgeStartWorld, edgeEndWorld);
                    if (edgeOffset > maxEdgeDistance)
                    {
                        continue;
                    }

                    var station =
                        ((endpoint.World.X - edgeStartWorld.X) * edgeVector.X +
                         (endpoint.World.Y - edgeStartWorld.Y) * edgeVector.Y) / edgeLengthSquared;
                    if (station <= 0.01 || station >= 0.99)
                    {
                        continue;
                    }

                    var touchesCorrectionSouth = false;
                    for (var correctionIndex = 0; correctionIndex < correctionHorizontalSegments.Count; correctionIndex++)
                    {
                        var correctionSegment = correctionHorizontalSegments[correctionIndex];
                        if (DistancePointToSegment(endpoint.World, correctionSegment.AWorld, correctionSegment.BWorld) <= maxCorrectionTouchDistance)
                        {
                            touchesCorrectionSouth = true;
                            break;
                        }
                    }

                    if (!touchesCorrectionSouth)
                    {
                        continue;
                    }

                    rawCandidates.Add((station, endpoint.World));
                }
            }

            if (rawCandidates.Count == 0)
            {
                return false;
            }

            rawCandidates.Sort((left, right) => left.Station.CompareTo(right.Station));
            var orderedCandidates = new List<Point2d>();
            foreach (var rawCandidate in rawCandidates)
            {
                if (orderedCandidates.Count == 0 ||
                    orderedCandidates[orderedCandidates.Count - 1].GetDistanceTo(rawCandidate.World) > 0.25)
                {
                    orderedCandidates.Add(rawCandidate.World);
                }
            }

            var simplifiedCandidates = SimplifyQuarterCorrectionDeflectionCandidates(
                edgeStartWorld,
                orderedCandidates,
                edgeEndWorld);
            if (simplifiedCandidates.Count == 0)
            {
                return false;
            }

            try
            {
                quarterBox.UpgradeOpen();
            }
            catch (Autodesk.AutoCAD.Runtime.Exception)
            {
                return false;
            }

            var insertionIndex = edgeEndIndex;
            foreach (var candidateWorld in simplifiedCandidates)
            {
                quarterBox.AddVertexAt(insertionIndex++, candidateWorld, 0.0, 0.0, 0.0);
                insertedWorldPoints.Add(candidateWorld);
            }

            return insertedWorldPoints.Count > 0;
        }

        private static bool IsQuarterCorrectionDeflectionVerticalCandidateLayer(string? layerName)
        {
            if (string.IsNullOrWhiteSpace(layerName))
            {
                return false;
            }

            return string.Equals(layerName, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "L-SEC-2012", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsQuarterCorrectionDeflectionSecLayer(string? layerName)
        {
            if (string.IsNullOrWhiteSpace(layerName))
            {
                return false;
            }

            return string.Equals(layerName, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "L-SEC-2012", StringComparison.OrdinalIgnoreCase);
        }

        private static List<Point2d> SimplifyQuarterCorrectionDeflectionCandidates(
            Point2d edgeStartWorld,
            IReadOnlyList<Point2d> orderedCandidates,
            Point2d edgeEndWorld)
        {
            var chain = new List<Point2d> { edgeStartWorld };
            if (orderedCandidates != null)
            {
                for (var i = 0; i < orderedCandidates.Count; i++)
                {
                    var candidate = orderedCandidates[i];
                    if (chain[chain.Count - 1].GetDistanceTo(candidate) > 0.25)
                    {
                        chain.Add(candidate);
                    }
                }
            }

            if (chain[chain.Count - 1].GetDistanceTo(edgeEndWorld) > 0.25)
            {
                chain.Add(edgeEndWorld);
            }
            else
            {
                chain[chain.Count - 1] = edgeEndWorld;
            }

            const double simplifyDistanceTolerance = 0.10;
            const double simplifyNeighborSpacing = 35.0;
            var removed = true;
            while (removed && chain.Count > 2)
            {
                removed = false;
                for (var i = 1; i < chain.Count - 1; i++)
                {
                    var previous = chain[i - 1];
                    var current = chain[i];
                    var next = chain[i + 1];
                    if (DistancePointToSegment(current, previous, next) <= simplifyDistanceTolerance &&
                        (current.GetDistanceTo(previous) <= simplifyNeighborSpacing ||
                         current.GetDistanceTo(next) <= simplifyNeighborSpacing))
                    {
                        chain.RemoveAt(i);
                        removed = true;
                        break;
                    }
                }
            }

            var simplifiedCandidates = new List<Point2d>();
            for (var i = 1; i < chain.Count - 1; i++)
            {
                simplifiedCandidates.Add(chain[i]);
            }

            return simplifiedCandidates;
        }

        private static bool IsOutsideRange(double value, double min, double max)
        {
            return value < min || value > max;
        }

        private static bool IsPointWithinExpandedSegmentBounds(
            Point2d point,
            Point2d segmentA,
            Point2d segmentB,
            double padding)
        {
            var minX = Math.Min(segmentA.X, segmentB.X) - padding;
            var maxX = Math.Max(segmentA.X, segmentB.X) + padding;
            var minY = Math.Min(segmentA.Y, segmentB.Y) - padding;
            var maxY = Math.Max(segmentA.Y, segmentB.Y) + padding;
            return point.X >= minX && point.X <= maxX && point.Y >= minY && point.Y <= maxY;
        }

        private static double ProjectPointToQuarterU(
            Point2d point,
            Point2d origin,
            Vector2d eastUnit)
        {
            return (point - origin).DotProduct(eastUnit);
        }

        private static double ProjectPointToQuarterV(
            Point2d point,
            Point2d origin,
            Vector2d northUnit)
        {
            return (point - origin).DotProduct(northUnit);
        }

        private static bool TryPickQuarterVertexSnapTarget(
            Point2d vertex,
            Point2d prev,
            Point2d next,
            IReadOnlyList<Point2d> hardBoundaryCornerEndpoints,
            bool requireBothEdgeMatches,
            double lineMatchTolerance,
            double maxSnapDistance,
            double minMove,
            double relaxedSingleEdgeMissMaxDistance,
            out Point2d picked)
        {
            picked = vertex;
            var found = false;
            var bestDistance = double.MaxValue;
            for (var i = 0; i < hardBoundaryCornerEndpoints.Count; i++)
            {
                var candidate = hardBoundaryCornerEndpoints[i];
                var moveDistance = vertex.GetDistanceTo(candidate);
                if (moveDistance <= minMove || moveDistance > maxSnapDistance)
                {
                    continue;
                }

                if (prev.GetDistanceTo(vertex) <= 1e-6 || next.GetDistanceTo(vertex) <= 1e-6)
                {
                    continue;
                }

                var prevLineDistance = DistancePointToInfiniteLine(candidate, vertex, prev);
                var nextLineDistance = DistancePointToInfiniteLine(candidate, vertex, next);
                var prevMatch = prevLineDistance <= lineMatchTolerance;
                var nextMatch = nextLineDistance <= lineMatchTolerance;
                if (requireBothEdgeMatches)
                {
                    if (!prevMatch || !nextMatch)
                    {
                        continue;
                    }
                }
                else if (!prevMatch && !nextMatch)
                {
                    continue;
                }

                if (!requireBothEdgeMatches && prevMatch != nextMatch)
                {
                    var unmatchedDistance = prevMatch ? nextLineDistance : prevLineDistance;
                    if (unmatchedDistance > relaxedSingleEdgeMissMaxDistance)
                    {
                        continue;
                    }
                }

                if (!found || moveDistance < bestDistance)
                {
                    found = true;
                    bestDistance = moveDistance;
                    picked = candidate;
                }
            }

            return found;
        }

        private static bool SegmentIntersectsAnyQuarterWindow(
            Point2d a,
            Point2d b,
            IReadOnlyList<QuarterViewSectionFrame> frames)
        {
            for (var i = 0; i < frames.Count; i++)
            {
                if (TryClipSegmentToWindow(a, b, frames[i].CleanupWindow, out _, out _))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ExtentsIntersectAnyQuarterWindow(
            Extents3d extents,
            IReadOnlyList<QuarterViewSectionFrame> frames)
        {
            for (var i = 0; i < frames.Count; i++)
            {
                if (DoExtentsOverlapOrTouch(extents, frames[i].CleanupWindow, 0.01))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryFindQuarterVertexSnapTarget(
            Point2d vertex,
            Point2d prev,
            Point2d next,
            IReadOnlyList<Point2d> hardBoundaryCornerEndpoints,
            out Point2d target)
        {
            target = vertex;
            if (hardBoundaryCornerEndpoints == null || hardBoundaryCornerEndpoints.Count == 0)
            {
                return false;
            }

            const double strictMaxSnapDistance = 30.0;
            const double strictLineMatchTolerance = 1.25;
            const double relaxedMaxSnapDistance = 35.0;
            const double relaxedLineMatchTolerance = 8.0;
            const double relaxedSingleEdgeMissMaxDistance = 10.0;
            const double nearestEdgeMissMaxDistance = 10.0;
            const double minMove = 0.01;

            if (TryPickQuarterVertexSnapTarget(
                    vertex,
                    prev,
                    next,
                    hardBoundaryCornerEndpoints,
                    requireBothEdgeMatches: true,
                    strictLineMatchTolerance,
                    strictMaxSnapDistance,
                    minMove,
                    relaxedSingleEdgeMissMaxDistance,
                    out var strictTarget))
            {
                target = strictTarget;
                return true;
            }

            if (TryPickQuarterVertexSnapTarget(
                    vertex,
                    prev,
                    next,
                    hardBoundaryCornerEndpoints,
                    requireBothEdgeMatches: false,
                    relaxedLineMatchTolerance,
                    relaxedMaxSnapDistance,
                    minMove,
                    relaxedSingleEdgeMissMaxDistance,
                    out var relaxedTarget))
            {
                target = relaxedTarget;
                return true;
            }

            var foundNearest = false;
            var nearestDistance = double.MaxValue;
            for (var i = 0; i < hardBoundaryCornerEndpoints.Count; i++)
            {
                var candidate = hardBoundaryCornerEndpoints[i];
                var moveDistance = vertex.GetDistanceTo(candidate);
                if (moveDistance <= minMove || moveDistance > strictMaxSnapDistance)
                {
                    continue;
                }

                if (prev.GetDistanceTo(vertex) <= 1e-6 || next.GetDistanceTo(vertex) <= 1e-6)
                {
                    continue;
                }

                var prevLineDistance = DistancePointToInfiniteLine(candidate, vertex, prev);
                var nextLineDistance = DistancePointToInfiniteLine(candidate, vertex, next);
                var edgeMatched = prevLineDistance <= relaxedLineMatchTolerance || nextLineDistance <= relaxedLineMatchTolerance;
                if (!edgeMatched)
                {
                    continue;
                }

                if (Math.Max(prevLineDistance, nextLineDistance) > nearestEdgeMissMaxDistance)
                {
                    continue;
                }

                if (!foundNearest || moveDistance < nearestDistance)
                {
                    foundNearest = true;
                    nearestDistance = moveDistance;
                    target = candidate;
                }
            }

            return foundNearest;
        }

        private static void AddBoundaryEndpointToCornerClusters(
            List<(Point2d Rep, int Count, bool HasHorizontal, bool HasVertical, int Priority)> hardBoundaryCornerClusters,
            double cornerClusterTolerance,
            Point2d endpoint,
            bool isHorizontal,
            bool isVertical,
            int priority)
        {
            var bestIndex = -1;
            var bestDistance = double.MaxValue;
            for (var ci = 0; ci < hardBoundaryCornerClusters.Count; ci++)
            {
                var cluster = hardBoundaryCornerClusters[ci];
                var distance = endpoint.GetDistanceTo(cluster.Rep);
                if (distance > cornerClusterTolerance || distance >= bestDistance)
                {
                    continue;
                }

                bestIndex = ci;
                bestDistance = distance;
            }

            if (bestIndex < 0)
            {
                hardBoundaryCornerClusters.Add((endpoint, 1, isHorizontal, isVertical, priority));
                return;
            }

            var existing = hardBoundaryCornerClusters[bestIndex];
            var replaceRep = priority < existing.Priority;
            var newRep = replaceRep ? endpoint : existing.Rep;
            var newPriority = replaceRep ? priority : existing.Priority;
            hardBoundaryCornerClusters[bestIndex] = (
                newRep,
                existing.Count + 1,
                existing.HasHorizontal || isHorizontal,
                existing.HasVertical || isVertical,
                newPriority);
        }

        private static bool IsPointInAnyQuarterWindow(
            Point2d p,
            double tol,
            IReadOnlyList<QuarterViewSectionFrame> frames)
        {
            for (var i = 0; i < frames.Count; i++)
            {
                var w = frames[i].CleanupWindow;
                if (p.X >= (w.MinPoint.X - tol) && p.X <= (w.MaxPoint.X + tol) &&
                    p.Y >= (w.MinPoint.Y - tol) && p.Y <= (w.MaxPoint.Y + tol))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryFindApparentCornerIntersection(
            Point2d firstA,
            Point2d firstB,
            Point2d secondA,
            Point2d secondB,
            IReadOnlyList<QuarterViewSectionFrame> frames,
            out Point2d corner)
        {
            corner = default;
            if (firstA.GetDistanceTo(firstB) <= 1e-6 || secondA.GetDistanceTo(secondB) <= 1e-6)
            {
                return false;
            }

            var r = firstB - firstA;
            var s = secondB - secondA;
            var denom = (r.X * s.Y) - (r.Y * s.X);
            if (Math.Abs(denom) <= 1e-9)
            {
                return false;
            }

            var diff = secondA - firstA;
            var t = ((diff.X * s.Y) - (diff.Y * s.X)) / denom;
            var hit = new Point2d(firstA.X + (r.X * t), firstA.Y + (r.Y * t));

            const double apparentIntersectionPadding = 80.0;
            if (!IsPointWithinExpandedSegmentBounds(hit, firstA, firstB, apparentIntersectionPadding) ||
                !IsPointWithinExpandedSegmentBounds(hit, secondA, secondB, apparentIntersectionPadding))
            {
                return false;
            }

            if (!IsPointInAnyQuarterWindow(hit, 0.01, frames))
            {
                return false;
            }

            corner = hit;
            return true;
        }

        private static bool TryResolveSouthDividerCornerFromHardBoundaries(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d Rep, int Count, bool HasHorizontal, bool HasVertical, int Priority)> cornerClusters,
            double dividerU,
            Point2d currentLocal,
            out Point2d resolvedLocal,
            out int resolvedPriority)
        {
            resolvedLocal = currentLocal;
            resolvedPriority = int.MaxValue;
            if (cornerClusters == null || cornerClusters.Count == 0)
            {
                return false;
            }

            const double maxMove = 20.0;
            const double dividerUTol = 8.0;
            const double southBandPadding = 12.0;
            var found = false;
            var bestScore = double.MaxValue;
            for (var i = 0; i < cornerClusters.Count; i++)
            {
                var cluster = cornerClusters[i];
                if (!cluster.HasHorizontal || !cluster.HasVertical)
                {
                    continue;
                }

                if (!TryConvertQuarterWorldToLocal(frame, cluster.Rep, out var u, out var v))
                {
                    continue;
                }

                if (u < (frame.WestEdgeU - southBandPadding) || u > (frame.EastEdgeU + southBandPadding))
                {
                    continue;
                }

                if (v < (frame.SouthEdgeV - southBandPadding) || v > (frame.MidV + southBandPadding))
                {
                    continue;
                }

                var dividerGap = Math.Abs(u - dividerU);
                if (dividerGap > dividerUTol)
                {
                    continue;
                }

                var move = new Point2d(u, v).GetDistanceTo(currentLocal);
                if (move <= 1e-3 || move > maxMove)
                {
                    continue;
                }

                var score =
                    (cluster.Priority * 100.0) +
                    (dividerGap * 8.0) +
                    Math.Abs(v - currentLocal.Y) -
                    Math.Min(cluster.Count, 6);
                if (!found || score < bestScore)
                {
                    found = true;
                    bestScore = score;
                    resolvedLocal = new Point2d(u, v);
                    resolvedPriority = cluster.Priority;
                }
            }

            return found;
        }

        private static bool TryResolveWestBandCornerFromHardBoundaries(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d Rep, int Count, bool HasHorizontal, bool HasVertical, int Priority)> cornerClusters,
            double preferredWestU,
            double expectedWestInset,
            double expectedSideInset,
            bool northBand,
            Point2d currentLocal,
            double maxMove,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> segments,
            bool requireEndpointNode,
            out Point2d resolvedLocal,
            out int resolvedPriority)
        {
            resolvedLocal = currentLocal;
            resolvedPriority = int.MaxValue;
            if (cornerClusters == null || cornerClusters.Count == 0)
            {
                return false;
            }

            const double westWindowPadding = 20.0;
            const double verticalBandPadding = 20.0;
            const double maxWestGap = 70.0;
            const double endpointTol = 0.55;
            const double maxAllowedInsetRegression = 2.0;
            var currentInsetScore = ComputeQuarterWestCornerInsetScore(
                frame,
                northBand,
                currentLocal.X,
                currentLocal.Y,
                expectedWestInset,
                expectedSideInset);
            var found = false;
            var bestScore = double.MaxValue;
            for (var i = 0; i < cornerClusters.Count; i++)
            {
                var cluster = cornerClusters[i];
                if (!cluster.HasHorizontal || !cluster.HasVertical)
                {
                    continue;
                }

                if (!TryConvertQuarterWorldToLocal(frame, cluster.Rep, out var u, out var v))
                {
                    continue;
                }

                if (u < (frame.WestEdgeU - 90.0) || u > (frame.MidU + westWindowPadding))
                {
                    continue;
                }

                if (northBand)
                {
                    if (v < (frame.MidV - verticalBandPadding) || v > (frame.NorthEdgeV + verticalBandPadding))
                    {
                        continue;
                    }
                }
                else
                {
                    if (v < (frame.SouthEdgeV - verticalBandPadding) || v > (frame.MidV + verticalBandPadding))
                    {
                        continue;
                    }
                }

                var westGap = Math.Abs(u - preferredWestU);
                if (westGap > maxWestGap)
                {
                    continue;
                }

                var edgeGap = northBand
                    ? Math.Abs(v - frame.NorthEdgeV)
                    : Math.Abs(v - frame.SouthEdgeV);
                var move = new Point2d(u, v).GetDistanceTo(currentLocal);
                if (move <= 1e-3 || move > maxMove)
                {
                    continue;
                }

                var endpointDistance = double.MaxValue;
                var hasEndpointNode = segments != null &&
                                      segments.Count > 0 &&
                                      HasQuarterViewEndpointHorizontalAndVerticalEvidence(
                                          frame,
                                          segments,
                                          cluster.Rep,
                                          endpointTol,
                                          out endpointDistance);
                if (requireEndpointNode && !hasEndpointNode)
                {
                    continue;
                }

                double score;
                if (requireEndpointNode)
                {
                    var insetScore = ComputeQuarterWestCornerInsetScore(
                        frame,
                        northBand,
                        u,
                        v,
                        expectedWestInset,
                        expectedSideInset);
                    if (insetScore > currentInsetScore + maxAllowedInsetRegression)
                    {
                        continue;
                    }
                    score =
                        // When endpoint evidence is required, the ownership-class inset fit
                        // should dominate over apparent-intersection priority so shared quarter
                        // corners stay on the same hard node as the final section definition.
                        (cluster.Priority * 12.0) +
                        (endpointDistance * 10.0) +
                        (insetScore * 6.0) +
                        (move * 0.35);
                }
                else
                {
                    score =
                        (cluster.Priority * 100.0) +
                        (westGap * 4.0) +
                        (edgeGap * 3.0) +
                        (move * 0.5) -
                        Math.Min(cluster.Count, 6);
                }
                if (!found || score < bestScore)
                {
                    found = true;
                    bestScore = score;
                    resolvedLocal = new Point2d(u, v);
                    resolvedPriority = cluster.Priority;
                }
            }

            return found;
        }

        private static double ComputeQuarterWestCornerInsetScore(
            QuarterViewSectionFrame frame,
            bool northBand,
            double u,
            double v,
            double expectedWestInset,
            double expectedSideInset)
        {
            var westInset = frame.WestEdgeU - u;
            var sideInset = northBand
                ? frame.NorthEdgeV - v
                : frame.SouthEdgeV - v;
            return Math.Abs(westInset - expectedWestInset) + Math.Abs(sideInset - expectedSideInset);
        }

        private static bool TryResolveEastBandCornerFromHardBoundaries(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d Rep, int Count, bool HasHorizontal, bool HasVertical, int Priority)> cornerClusters,
            double preferredEastU,
            double expectedEastInset,
            double expectedSideInset,
            bool northBand,
            Point2d currentLocal,
            double maxMove,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> segments,
            bool requireEndpointNode,
            out Point2d resolvedLocal,
            out int resolvedPriority)
        {
            resolvedLocal = currentLocal;
            resolvedPriority = int.MaxValue;
            if (cornerClusters == null || cornerClusters.Count == 0)
            {
                return false;
            }

            const double eastWindowPadding = 20.0;
            const double verticalBandPadding = 20.0;
            const double maxEastGap = 120.0;
            const double endpointTol = 0.55;
            var found = false;
            var bestScore = double.MaxValue;
            for (var i = 0; i < cornerClusters.Count; i++)
            {
                var cluster = cornerClusters[i];
                if (!cluster.HasHorizontal || !cluster.HasVertical)
                {
                    continue;
                }

                if (!TryConvertQuarterWorldToLocal(frame, cluster.Rep, out var u, out var v))
                {
                    continue;
                }

                if (u < (frame.MidU - eastWindowPadding) || u > (frame.EastEdgeU + 220.0))
                {
                    continue;
                }

                if (northBand)
                {
                    if (v < (frame.MidV - verticalBandPadding) || v > (frame.NorthEdgeV + 220.0))
                    {
                        continue;
                    }
                }
                else
                {
                    if (v < (frame.SouthEdgeV - verticalBandPadding) || v > (frame.MidV + verticalBandPadding))
                    {
                        continue;
                    }
                }

                var eastGap = Math.Abs(u - preferredEastU);
                if (eastGap > maxEastGap)
                {
                    continue;
                }

                var edgeGap = northBand
                    ? Math.Abs(v - frame.NorthEdgeV)
                    : Math.Abs(v - frame.SouthEdgeV);
                var move = new Point2d(u, v).GetDistanceTo(currentLocal);
                if (move <= 1e-3 || move > maxMove)
                {
                    continue;
                }

                var endpointDistance = double.MaxValue;
                var hasEndpointNode = segments != null &&
                                      segments.Count > 0 &&
                                      HasQuarterViewEndpointHorizontalAndVerticalEvidence(
                                          frame,
                                          segments,
                                          cluster.Rep,
                                          endpointTol,
                                          out endpointDistance);
                if (requireEndpointNode && !hasEndpointNode)
                {
                    continue;
                }

                double score;
                if (requireEndpointNode)
                {
                    var eastInset = frame.EastEdgeU - u;
                    var sideInset = northBand
                        ? frame.NorthEdgeV - v
                        : frame.SouthEdgeV - v;
                    var insetScore = Math.Abs(eastInset - expectedEastInset) + Math.Abs(sideInset - expectedSideInset);
                    score =
                        (cluster.Priority * 80.0) +
                        (endpointDistance * 8.0) +
                        (insetScore * 2.0) +
                        (move * 0.5);
                }
                else
                {
                    score =
                        (cluster.Priority * 100.0) +
                        (eastGap * 4.0) +
                        (edgeGap * 3.0) +
                        (move * 0.5) -
                        Math.Min(cluster.Count, 6);
                }
                if (!found || score < bestScore)
                {
                    found = true;
                    bestScore = score;
                    resolvedLocal = new Point2d(u, v);
                    resolvedPriority = cluster.Priority;
                }
            }

            return found;
        }

        private static bool TryResolveWestCornerFromProtectedBoundaryCorners(
            QuarterViewSectionFrame frame,
            IReadOnlyList<Point2d> protectedBoundaryCorners,
            double expectedWestInset,
            double expectedSideInset,
            bool northBand,
            Point2d currentLocal,
            out Point2d resolvedLocal,
            out double resolvedMove)
        {
            return TryResolveWestCornerFromProtectedBoundaryCorners(
                frame,
                protectedBoundaryCorners,
                expectedWestInset,
                expectedSideInset,
                northBand,
                currentLocal,
                0.0,
                out resolvedLocal,
                out resolvedMove,
                out _);
        }

        private static bool TryResolveWestCornerFromProtectedBoundaryCorners(
            QuarterViewSectionFrame frame,
            IReadOnlyList<Point2d> protectedBoundaryCorners,
            double expectedWestInset,
            double expectedSideInset,
            bool northBand,
            Point2d currentLocal,
            double maxAllowedScoreRegression,
            out Point2d resolvedLocal,
            out double resolvedMove,
            out double resolvedScore)
        {
            resolvedLocal = currentLocal;
            resolvedMove = double.MaxValue;
            resolvedScore = double.MaxValue;
            if (protectedBoundaryCorners == null || protectedBoundaryCorners.Count == 0)
            {
                return false;
            }

            const double maxMove = 40.0;
            const double westWindowPadding = 60.0;
            const double verticalBandPadding = 40.0;
            var currentWestInset = frame.WestEdgeU - currentLocal.X;
            var currentSideInset = northBand
                ? frame.NorthEdgeV - currentLocal.Y
                : frame.SouthEdgeV - currentLocal.Y;
            var found = false;
            var bestScore =
                Math.Abs(currentWestInset - expectedWestInset) + Math.Abs(currentSideInset - expectedSideInset);
            var bestMove = double.MaxValue;
            for (var i = 0; i < protectedBoundaryCorners.Count; i++)
            {
                var candidateWorld = protectedBoundaryCorners[i];
                if (!TryConvertQuarterWorldToLocal(frame, candidateWorld, out var u, out var v))
                {
                    continue;
                }

                if (u < (frame.WestEdgeU - verticalBandPadding) || u > (frame.MidU + westWindowPadding))
                {
                    continue;
                }

                if (northBand)
                {
                    if (v < (frame.MidV - verticalBandPadding) || v > (frame.NorthEdgeV + verticalBandPadding))
                    {
                        continue;
                    }
                }
                else
                {
                    if (v < (frame.SouthEdgeV - verticalBandPadding) || v > (frame.MidV + verticalBandPadding))
                    {
                        continue;
                    }
                }

                var candidateLocal = new Point2d(u, v);
                var move = candidateLocal.GetDistanceTo(currentLocal);
                if (move <= 1e-3 || move > maxMove)
                {
                    continue;
                }

                var westInset = frame.WestEdgeU - u;
                var sideInset = northBand
                    ? frame.NorthEdgeV - v
                    : frame.SouthEdgeV - v;
                var insetScore = Math.Abs(westInset - expectedWestInset) + Math.Abs(sideInset - expectedSideInset);
                var score = insetScore;
                var improvesCurrent = score < bestScore - 1e-6;
                var isAllowedNearSharedRegression =
                    maxAllowedScoreRegression > 0.0 &&
                    score <= bestScore + maxAllowedScoreRegression + 1e-6;
                var improvesChosen = found &&
                                     Math.Abs(score - bestScore) <= 1e-6 &&
                                     move < bestMove;
                if (!improvesCurrent && !improvesChosen && !isAllowedNearSharedRegression)
                {
                    continue;
                }

                if (!found ||
                    score < bestScore - 1e-6 ||
                    (!improvesCurrent &&
                     isAllowedNearSharedRegression &&
                     (score < resolvedScore - 1e-6 ||
                      (Math.Abs(score - resolvedScore) <= 1e-6 && move < bestMove))) ||
                    (Math.Abs(score - bestScore) <= 1e-6 && move < bestMove))
                {
                    found = true;
                    bestMove = move;
                    if (score < bestScore - 1e-6)
                    {
                        bestScore = score;
                    }
                    resolvedLocal = candidateLocal;
                    resolvedMove = move;
                    resolvedScore = score;
                }
            }

            return found;
        }

        private static bool TryResolveEastCornerFromProtectedBoundaryCorners(
            QuarterViewSectionFrame frame,
            IReadOnlyList<Point2d> protectedBoundaryCorners,
            double expectedEastInset,
            double expectedSideInset,
            bool northBand,
            Point2d currentLocal,
            out Point2d resolvedLocal,
            out double resolvedMove)
        {
            return TryResolveEastCornerFromProtectedBoundaryCorners(
                frame,
                protectedBoundaryCorners,
                expectedEastInset,
                expectedSideInset,
                northBand,
                endpointNodes: null,
                maxAllowedScoreRegression: 0.0,
                currentLocal,
                out resolvedLocal,
                out resolvedMove,
                out _);
        }

        private static bool TryResolveEastCornerFromProtectedBoundaryCorners(
            QuarterViewSectionFrame frame,
            IReadOnlyList<Point2d> protectedBoundaryCorners,
            double expectedEastInset,
            double expectedSideInset,
            bool northBand,
            IReadOnlyList<Point2d>? endpointNodes,
            double maxAllowedScoreRegression,
            Point2d currentLocal,
            out Point2d resolvedLocal,
            out double resolvedMove,
            out double resolvedScore)
        {
            resolvedLocal = currentLocal;
            resolvedMove = double.MaxValue;
            resolvedScore = double.MaxValue;
            if (protectedBoundaryCorners == null || protectedBoundaryCorners.Count == 0)
            {
                return false;
            }

            const double maxMove = 45.0;
            const double eastWindowPadding = 60.0;
            const double verticalBandPadding = 40.0;
            var currentEastInset = frame.EastEdgeU - currentLocal.X;
            var currentSideInset = northBand
                ? frame.NorthEdgeV - currentLocal.Y
                : frame.SouthEdgeV - currentLocal.Y;
            var found = false;
            var bestScore =
                Math.Abs(currentEastInset - expectedEastInset) + Math.Abs(currentSideInset - expectedSideInset);
            var bestMove = double.MaxValue;
            for (var i = 0; i < protectedBoundaryCorners.Count; i++)
            {
                var candidateWorld = protectedBoundaryCorners[i];
                if (!TryConvertQuarterWorldToLocal(frame, candidateWorld, out var u, out var v))
                {
                    continue;
                }

                if (u < (frame.MidU - eastWindowPadding) || u > (frame.EastEdgeU + verticalBandPadding))
                {
                    continue;
                }

                if (northBand)
                {
                    if (v < (frame.MidV - verticalBandPadding) || v > (frame.NorthEdgeV + verticalBandPadding))
                    {
                        continue;
                    }
                }
                else
                {
                    if (v < (frame.SouthEdgeV - verticalBandPadding) || v > (frame.MidV + verticalBandPadding))
                    {
                        continue;
                    }
                }

                var candidateLocal = new Point2d(u, v);
                var move = candidateLocal.GetDistanceTo(currentLocal);
                if (move <= 1e-3 || move > maxMove)
                {
                    continue;
                }

                var eastInset = frame.EastEdgeU - u;
                var sideInset = northBand
                    ? frame.NorthEdgeV - v
                    : frame.SouthEdgeV - v;
                var insetScore = Math.Abs(eastInset - expectedEastInset) + Math.Abs(sideInset - expectedSideInset);
                var hasEndpointNode = HasNearbyQuarterEndpointNode(candidateWorld, endpointNodes, 0.55);
                var endpointNodeBonus = hasEndpointNode ? 24.0 : 0.0;
                var score = insetScore - endpointNodeBonus;
                var improvesCurrent = score < bestScore - 1e-6;
                var isAllowedNearSharedRegression =
                    maxAllowedScoreRegression > 0.0 &&
                    score <= bestScore + maxAllowedScoreRegression + 1e-6;
                var improvesChosen = found &&
                                     Math.Abs(score - bestScore) <= 1e-6 &&
                                     move < bestMove;
                if (!improvesCurrent && !improvesChosen && !isAllowedNearSharedRegression)
                {
                    continue;
                }

                if (!found ||
                    score < bestScore - 1e-6 ||
                    (!improvesCurrent &&
                     isAllowedNearSharedRegression &&
                     (score < resolvedScore - 1e-6 ||
                      (Math.Abs(score - resolvedScore) <= 1e-6 && move < bestMove))) ||
                    (Math.Abs(score - bestScore) <= 1e-6 && move < bestMove))
                {
                    found = true;
                    bestMove = move;
                    if (score < bestScore - 1e-6)
                    {
                        bestScore = score;
                    }
                    resolvedLocal = candidateLocal;
                    resolvedMove = move;
                    resolvedScore = score;
                }
            }

            return found;
        }

        private static bool TryResolveBestWestCornerFromProtectedBoundaryCornerSets(
            QuarterViewSectionFrame frame,
            IReadOnlyList<Point2d> primaryProtectedBoundaryCorners,
            string primarySource,
            IReadOnlyList<Point2d> secondaryProtectedBoundaryCorners,
            string secondarySource,
            double expectedWestInset,
            double expectedSideInset,
            bool northBand,
            Point2d currentLocal,
            double maxAllowedScoreRegression,
            out Point2d resolvedLocal,
            out double resolvedMove,
            out string resolvedSource)
        {
            resolvedLocal = currentLocal;
            resolvedMove = double.MaxValue;
            resolvedSource = string.Empty;
            var found = false;
            var bestScore = double.MaxValue;
            var bestMove = double.MaxValue;

            if (TryResolveWestCornerFromProtectedBoundaryCorners(
                    frame,
                    primaryProtectedBoundaryCorners,
                    expectedWestInset,
                    expectedSideInset,
                    northBand,
                    currentLocal,
                    maxAllowedScoreRegression,
                    out var primaryLocal,
                    out var primaryMove,
                    out var primaryScore))
            {
                found = true;
                bestScore = primaryScore;
                bestMove = primaryMove;
                resolvedLocal = primaryLocal;
                resolvedMove = primaryMove;
                resolvedSource = primarySource;
            }

            if (TryResolveWestCornerFromProtectedBoundaryCorners(
                    frame,
                    secondaryProtectedBoundaryCorners,
                    expectedWestInset,
                    expectedSideInset,
                    northBand,
                    currentLocal,
                    maxAllowedScoreRegression,
                    out var secondaryLocal,
                    out var secondaryMove,
                    out var secondaryScore) &&
                (!found ||
                 secondaryScore < bestScore - 1e-6 ||
                 (Math.Abs(secondaryScore - bestScore) <= 1e-6 && secondaryMove < bestMove)))
            {
                found = true;
                bestScore = secondaryScore;
                bestMove = secondaryMove;
                resolvedLocal = secondaryLocal;
                resolvedMove = secondaryMove;
                resolvedSource = secondarySource;
            }

            return found;
        }

        private static bool TryResolveBestEastCornerFromProtectedBoundaryCornerSets(
            QuarterViewSectionFrame frame,
            IReadOnlyList<Point2d> primaryProtectedBoundaryCorners,
            string primarySource,
            IReadOnlyList<Point2d> secondaryProtectedBoundaryCorners,
            string secondarySource,
            double expectedEastInset,
            double expectedSideInset,
            bool northBand,
            IReadOnlyList<Point2d>? endpointNodes,
            double maxAllowedScoreRegression,
            Point2d currentLocal,
            out Point2d resolvedLocal,
            out double resolvedMove,
            out string resolvedSource)
        {
            resolvedLocal = currentLocal;
            resolvedMove = double.MaxValue;
            resolvedSource = string.Empty;
            var found = false;
            var bestScore = double.MaxValue;
            var bestMove = double.MaxValue;

            if (TryResolveEastCornerFromProtectedBoundaryCorners(
                    frame,
                    primaryProtectedBoundaryCorners,
                    expectedEastInset,
                    expectedSideInset,
                    northBand,
                    endpointNodes,
                    maxAllowedScoreRegression,
                    currentLocal,
                    out var primaryLocal,
                    out var primaryMove,
                    out var primaryScore))
            {
                found = true;
                bestScore = primaryScore;
                bestMove = primaryMove;
                resolvedLocal = primaryLocal;
                resolvedMove = primaryMove;
                resolvedSource = primarySource;
            }

            if (TryResolveEastCornerFromProtectedBoundaryCorners(
                    frame,
                    secondaryProtectedBoundaryCorners,
                    expectedEastInset,
                    expectedSideInset,
                    northBand,
                    endpointNodes,
                    maxAllowedScoreRegression,
                    currentLocal,
                    out var secondaryLocal,
                    out var secondaryMove,
                    out var secondaryScore) &&
                (!found ||
                 secondaryScore < bestScore - 1e-6 ||
                 (Math.Abs(secondaryScore - bestScore) <= 1e-6 && secondaryMove < bestMove)))
            {
                found = true;
                bestScore = secondaryScore;
                bestMove = secondaryMove;
                resolvedLocal = secondaryLocal;
                resolvedMove = secondaryMove;
                resolvedSource = secondarySource;
            }

            return found;
        }

        private static bool TryResolveDeepSouthCorrectionEastSharedCornerFromWestProtectedCorners(
            QuarterViewSectionFrame frame,
            IReadOnlyList<Point2d> protectedWestBoundaryCorners,
            IReadOnlyList<Point2d>? endpointNodes,
            double expectedEastInset,
            double currentSouthExpectedInset,
            Point2d currentLocal,
            out Point2d resolvedLocal,
            out double resolvedMove)
        {
            resolvedLocal = currentLocal;
            resolvedMove = double.MaxValue;
            if (protectedWestBoundaryCorners == null || protectedWestBoundaryCorners.Count == 0)
            {
                return false;
            }

            const double maxMove = 25.0;
            const double eastWindowPadding = 60.0;
            const double maxEastInsetDrift = 70.0;
            var targetSouthInset = currentSouthExpectedInset + (RoadAllowanceSecWidthMeters * 2.0);
            var minSouthInset = currentSouthExpectedInset + (RoadAllowanceSecWidthMeters * 1.5);
            var maxSouthInset = currentSouthExpectedInset + (RoadAllowanceSecWidthMeters * 2.6);
            var found = false;
            var bestScore = double.MaxValue;
            var bestMove = double.MaxValue;

            for (var i = 0; i < protectedWestBoundaryCorners.Count; i++)
            {
                var candidateWorld = protectedWestBoundaryCorners[i];
                if (!TryConvertQuarterWorldToLocal(frame, candidateWorld, out var candidateU, out var candidateV))
                {
                    continue;
                }

                if (candidateU < (frame.MidU - eastWindowPadding) ||
                    candidateU > (frame.EastEdgeU + eastWindowPadding))
                {
                    continue;
                }

                var candidateLocal = new Point2d(candidateU, candidateV);
                var candidateMove = candidateLocal.GetDistanceTo(currentLocal);
                if (candidateMove <= 1e-3 || candidateMove > maxMove)
                {
                    continue;
                }

                var candidateEastInset = frame.EastEdgeU - candidateU;
                if (Math.Abs(candidateEastInset - expectedEastInset) > maxEastInsetDrift)
                {
                    continue;
                }

                var candidateSouthInset = frame.SouthEdgeV - candidateV;
                if (candidateSouthInset < minSouthInset || candidateSouthInset > maxSouthInset)
                {
                    continue;
                }

                var endpointBonus = HasNearbyQuarterEndpointNode(candidateWorld, endpointNodes, 0.75) ? 12.0 : 0.0;
                var score =
                    Math.Abs(candidateEastInset - expectedEastInset) +
                    Math.Abs(candidateSouthInset - targetSouthInset) -
                    endpointBonus;
                if (!found ||
                    score < bestScore - 1e-6 ||
                    (Math.Abs(score - bestScore) <= 1e-6 && candidateMove < bestMove))
                {
                    found = true;
                    bestScore = score;
                    bestMove = candidateMove;
                    resolvedLocal = candidateLocal;
                    resolvedMove = candidateMove;
                }
            }

            return found;
        }

        private static bool HasNearbyQuarterEndpointNode(
            Point2d candidateWorld,
            IReadOnlyList<Point2d>? endpointNodes,
            double tolerance)
        {
            if (endpointNodes == null || endpointNodes.Count == 0)
            {
                return false;
            }

            for (var i = 0; i < endpointNodes.Count; i++)
            {
                if (candidateWorld.GetDistanceTo(endpointNodes[i]) <= tolerance)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryResolveNorthEastCornerFromEastHardNode(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> segments,
            IReadOnlyList<(Point2d Rep, int Count, bool HasHorizontal, bool HasVertical, int Priority)> cornerClusters,
            Point2d eastBoundaryA,
            Point2d eastBoundaryB,
            Point2d currentLocal,
            out Point2d resolvedLocal,
            out int resolvedPriority,
            out string resolvedLayer,
            out double resolvedClusterDistance,
            out double resolvedHorizontalReachGap)
        {
            resolvedLocal = currentLocal;
            resolvedPriority = int.MaxValue;
            resolvedLayer = string.Empty;
            resolvedClusterDistance = double.MaxValue;
            resolvedHorizontalReachGap = double.MaxValue;
            if (segments == null || segments.Count == 0 || cornerClusters == null || cornerClusters.Count == 0)
            {
                return false;
            }

            if (!TryConvertQuarterWorldToLocal(frame, eastBoundaryA, out var eastAu, out var eastAv) ||
                !TryConvertQuarterWorldToLocal(frame, eastBoundaryB, out var eastBu, out var eastBv))
            {
                return false;
            }

            var eastMinV = Math.Min(eastAv, eastBv);
            var eastMaxV = Math.Max(eastAv, eastBv);
            const double maxHorizontalReachGap = 80.0;
            const double maxEastReachGap = 120.0;
            const double maxClusterDistance = 6.0;
            const double maxMove = 120.0;
            var found = false;
            var bestScore = double.MaxValue;
            for (var i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                if (!IsQuarterViewBoundaryCandidateLayer(seg.Layer))
                {
                    continue;
                }

                var delta = seg.B - seg.A;
                var eastComp = Math.Abs(delta.DotProduct(frame.EastUnit));
                var northComp = Math.Abs(delta.DotProduct(frame.NorthUnit));
                if (eastComp <= northComp)
                {
                    continue;
                }

                if (!TryIntersectLocalInfiniteLines(
                        frame,
                        eastBoundaryA,
                        eastBoundaryB,
                        seg.A,
                        seg.B,
                        out var u,
                        out var v))
                {
                    continue;
                }

                if (u < (frame.MidU - 40.0) || u > (frame.EastEdgeU + 220.0) ||
                    v < (frame.MidV - 40.0) || v > (frame.NorthEdgeV + 220.0))
                {
                    continue;
                }

                if (!TryConvertQuarterWorldToLocal(frame, seg.A, out var segAu, out var segAv) ||
                    !TryConvertQuarterWorldToLocal(frame, seg.B, out var segBu, out var segBv))
                {
                    continue;
                }

                var segMinU = Math.Min(segAu, segBu);
                var segMaxU = Math.Max(segAu, segBu);
                var horizontalReachGap = DistanceToClosedInterval(u, segMinU, segMaxU);
                if (horizontalReachGap > maxHorizontalReachGap)
                {
                    continue;
                }

                var eastReachGap = DistanceToClosedInterval(v, eastMinV, eastMaxV);
                if (eastReachGap > maxEastReachGap)
                {
                    continue;
                }

                var candidateLocal = new Point2d(u, v);
                var move = candidateLocal.GetDistanceTo(currentLocal);
                if (move <= 1e-3 || move > maxMove)
                {
                    continue;
                }

                var candidateWorld = QuarterViewLocalToWorld(frame, u, v);
                var bestClusterDistance = double.MaxValue;
                var clusterPriority = int.MaxValue;
                for (var ci = 0; ci < cornerClusters.Count; ci++)
                {
                    var cluster = cornerClusters[ci];
                    if (!cluster.HasHorizontal || !cluster.HasVertical)
                    {
                        continue;
                    }

                    var dist = candidateWorld.GetDistanceTo(cluster.Rep);
                    if (dist >= bestClusterDistance)
                    {
                        continue;
                    }

                    bestClusterDistance = dist;
                    clusterPriority = cluster.Priority;
                }

                if (bestClusterDistance > maxClusterDistance)
                {
                    continue;
                }

                var layerPriority = GetQuarterViewBoundaryLayerPriority(seg.Layer);
                var score =
                    (clusterPriority * 100.0) +
                    (layerPriority * 20.0) +
                    (bestClusterDistance * 12.0) +
                    (horizontalReachGap * 7.0) +
                    (eastReachGap * 3.0) +
                    (move * 0.5);
                if (!found || score < bestScore)
                {
                    found = true;
                    bestScore = score;
                    resolvedLocal = candidateLocal;
                    resolvedPriority = clusterPriority;
                    resolvedLayer = seg.Layer ?? string.Empty;
                    resolvedClusterDistance = bestClusterDistance;
                    resolvedHorizontalReachGap = horizontalReachGap;
                }
            }

            return found;
        }

        private static bool TryResolveNorthEastCornerFromEndpointCornerClusters(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> segments,
            IReadOnlyList<(Point2d Rep, int Count, bool HasHorizontal, bool HasVertical, int Priority)> cornerClusters,
            Point2d currentLocal,
            double expectedEastInset,
            double expectedSideInset,
            bool requireEndpointNode,
            out Point2d resolvedLocal,
            out int resolvedPriority,
            out double resolvedMove,
            out double resolvedEndpointDistance)
        {
            return TryResolveEastCornerFromEndpointCornerClusters(
                frame,
                segments,
                cornerClusters,
                currentLocal,
                expectedEastInset,
                expectedSideInset,
                northBand: true,
                requireEndpointNode,
                out resolvedLocal,
                out resolvedPriority,
                out resolvedMove,
                out resolvedEndpointDistance);
        }

        private static bool TryResolveSouthEastCornerFromEndpointCornerClusters(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> segments,
            IReadOnlyList<(Point2d Rep, int Count, bool HasHorizontal, bool HasVertical, int Priority)> cornerClusters,
            Point2d currentLocal,
            double expectedEastInset,
            double expectedSideInset,
            bool requireEndpointNode,
            out Point2d resolvedLocal,
            out int resolvedPriority,
            out double resolvedMove,
            out double resolvedEndpointDistance)
        {
            return TryResolveEastCornerFromEndpointCornerClusters(
                frame,
                segments,
                cornerClusters,
                currentLocal,
                expectedEastInset,
                expectedSideInset,
                northBand: false,
                requireEndpointNode,
                out resolvedLocal,
                out resolvedPriority,
                out resolvedMove,
                out resolvedEndpointDistance);
        }

        private static bool TryResolveEastCornerFromEndpointCornerClusters(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> segments,
            IReadOnlyList<(Point2d Rep, int Count, bool HasHorizontal, bool HasVertical, int Priority)> cornerClusters,
            Point2d currentLocal,
            double expectedEastInset,
            double expectedSideInset,
            bool northBand,
            bool requireEndpointNode,
            out Point2d resolvedLocal,
            out int resolvedPriority,
            out double resolvedMove,
            out double resolvedEndpointDistance)
        {
            resolvedLocal = currentLocal;
            resolvedPriority = int.MaxValue;
            resolvedMove = double.MaxValue;
            resolvedEndpointDistance = double.MaxValue;
            if (segments == null || segments.Count == 0 || cornerClusters == null || cornerClusters.Count == 0)
            {
                return false;
            }

            // East-corner endpoint promotion is only meant to catch nearby shared slanted junctions.
            // East-corner endpoint promotion is only meant to catch nearby shared slanted junctions.
            // Larger snaps tend to jump an entire surveyed road-allowance width onto the wrong node.
            const double maxMove = 12.0;
            const double endpointTol = 0.55;
            var found = false;
            var bestScore = double.MaxValue;
            for (var i = 0; i < cornerClusters.Count; i++)
            {
                var cluster = cornerClusters[i];
                if (!cluster.HasHorizontal || !cluster.HasVertical)
                {
                    continue;
                }

                if (!TryConvertQuarterWorldToLocal(frame, cluster.Rep, out var u, out var v))
                {
                    continue;
                }

                if (u < (frame.MidU - 30.0) || u > (frame.EastEdgeU + 140.0) ||
                    (northBand
                        ? (v < (frame.MidV - 30.0) || v > (frame.NorthEdgeV + 140.0))
                        : (v < (frame.SouthEdgeV - 140.0) || v > (frame.MidV + 30.0))))
                {
                    continue;
                }

                var candidateLocal = new Point2d(u, v);
                var move = candidateLocal.GetDistanceTo(currentLocal);
                if (move <= 1e-3 || move > maxMove)
                {
                    continue;
                }

                var hasEndpointNode = HasQuarterViewEndpointHorizontalAndVerticalEvidence(
                    frame,
                    segments,
                    cluster.Rep,
                    endpointTol,
                    out var endpointDistance);
                if (requireEndpointNode && !hasEndpointNode)
                {
                    continue;
                }

                var eastInset = frame.EastEdgeU - u;
                var sideInset = northBand
                    ? frame.NorthEdgeV - v
                    : frame.SouthEdgeV - v;
                var insetScore = Math.Abs(eastInset - expectedEastInset) + Math.Abs(sideInset - expectedSideInset);
                var endpointPenalty = hasEndpointNode ? endpointDistance * 8.0 : 120.0;
                var score =
                    (cluster.Priority * 80.0) +
                    endpointPenalty +
                    (insetScore * 2.0) +
                    (move * 0.5);
                if (!found || score < bestScore)
                {
                    found = true;
                    bestScore = score;
                    resolvedLocal = candidateLocal;
                    resolvedPriority = cluster.Priority;
                    resolvedMove = move;
                    resolvedEndpointDistance = endpointDistance;
                }
            }

            return found;
        }

        private static bool TryReadOpenSegmentForDeferredLsd(Entity ent, out Point2d a, out Point2d b)
        {
            a = default;
            b = default;
            if (ent == null)
            {
                return false;
            }

            if (ent is Line ln)
            {
                a = new Point2d(ln.StartPoint.X, ln.StartPoint.Y);
                b = new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                return a.GetDistanceTo(b) > 1e-4;
            }

            if (ent is Polyline pl)
            {
                if (pl.Closed || pl.NumberOfVertices < 2)
                {
                    return false;
                }

                a = pl.GetPoint2dAt(0);
                b = pl.GetPoint2dAt(pl.NumberOfVertices - 1);
                return a.GetDistanceTo(b) > 1e-4;
            }

            return false;
        }

        private static bool IsPointInAnyScopedQuarter(
            Point2d p,
            IReadOnlyList<(Polyline Polyline, Extents3d Extents)> scopedQuarterPolylines)
        {
            const double extPad = 0.50;
            for (var i = 0; i < scopedQuarterPolylines.Count; i++)
            {
                var candidate = scopedQuarterPolylines[i];
                if (p.X < (candidate.Extents.MinPoint.X - extPad) || p.X > (candidate.Extents.MaxPoint.X + extPad) ||
                    p.Y < (candidate.Extents.MinPoint.Y - extPad) || p.Y > (candidate.Extents.MaxPoint.Y + extPad))
                {
                    continue;
                }

                if (GeometryUtils.IsPointInsidePolyline(candidate.Polyline, p))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsSegmentOwnedByScopedQuarters(
            Point2d a,
            Point2d b,
            IReadOnlyList<(Polyline Polyline, Extents3d Extents)> scopedQuarterPolylines)
        {
            // Ownership for deferred redraw should follow the source quarter interior.
            // Midpoint containment avoids deleting adjoining boundary-touching LSD lines.
            var mid = Midpoint(a, b);
            if (IsPointInAnyScopedQuarter(mid, scopedQuarterPolylines))
            {
                return true;
            }

            return IsPointInAnyScopedQuarter(a, scopedQuarterPolylines) &&
                   IsPointInAnyScopedQuarter(b, scopedQuarterPolylines);
        }

        private static bool TryGetSectionAxes(
            ObjectId sectionId,
            Transaction transaction,
            IDictionary<ObjectId, (Vector2d East, Vector2d North)> sectionAxisCache,
            out Vector2d eastUnit,
            out Vector2d northUnit)
        {
            eastUnit = new Vector2d(1.0, 0.0);
            northUnit = new Vector2d(0.0, 1.0);
            if (sectionId.IsNull || sectionId.IsErased)
            {
                return false;
            }

            if (sectionAxisCache.TryGetValue(sectionId, out var cached))
            {
                eastUnit = cached.East;
                northUnit = cached.North;
                return true;
            }

            if (!(transaction.GetObject(sectionId, OpenMode.ForRead, false) is Polyline section))
            {
                return false;
            }

            if (!TryGetQuarterAnchors(section, out var anchors))
            {
                anchors = GetFallbackAnchors(section);
            }

            eastUnit = GetUnitVector(anchors.Left, anchors.Right, new Vector2d(1, 0));
            northUnit = GetUnitVector(anchors.Bottom, anchors.Top, new Vector2d(0, 1));
            sectionAxisCache[sectionId] = (eastUnit, northUnit);
            return true;
        }

        private static double ClampQuarterWestBoundaryUToLimit(double candidateU, double westBoundaryLimitU)
        {
            return Math.Min(candidateU, westBoundaryLimitU);
        }

        private static double ClampQuarterEastBoundaryUToLimit(double candidateU, double eastBoundaryLimitU)
        {
            return Math.Max(candidateU, eastBoundaryLimitU);
        }

        private static double ClampQuarterSouthBoundaryVToLimit(double candidateV, double southBoundaryLimitV)
        {
            return Math.Min(candidateV, southBoundaryLimitV);
        }

        private static double ClampQuarterNorthBoundaryVToLimit(double candidateV, double northBoundaryLimitV)
        {
            return Math.Max(candidateV, northBoundaryLimitV);
        }

        private static bool IsQuarterViewPolylineOwnedByAnyRebuiltSection(
            Polyline poly,
            IReadOnlyList<QuarterViewSectionFrame> frames)
        {
            if (poly == null || poly.NumberOfVertices < 3)
            {
                return false;
            }

            var sumX = 0.0;
            var sumY = 0.0;
            for (var vi = 0; vi < poly.NumberOfVertices; vi++)
            {
                var p = poly.GetPoint2dAt(vi);
                sumX += p.X;
                sumY += p.Y;
            }

            var centroid = new Point2d(sumX / poly.NumberOfVertices, sumY / poly.NumberOfVertices);
            const double centroidTolerance = 0.75;
            const double vertexTolerance = 0.35;
            const double cleanupWindowTolerance = 2.0;
            for (var i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                if (!IsPointInsideQuarterViewFrame(frame, centroid, centroidTolerance))
                {
                    if (!IsPointInsideQuarterViewCleanupWindow(frame, centroid, cleanupWindowTolerance))
                    {
                        continue;
                    }
                }

                var allVerticesInside = true;
                for (var vi = 0; vi < poly.NumberOfVertices; vi++)
                {
                    if (IsPointInsideQuarterViewFrame(frame, poly.GetPoint2dAt(vi), vertexTolerance))
                    {
                        continue;
                    }

                    allVerticesInside = false;
                    break;
                }

                if (allVerticesInside)
                {
                    return true;
                }

                // Stale preexisting quarter polygons can drift enough that one or more
                // corners miss the rebuilt frame even though their centroid still lies
                // inside the rebuilt section's cleanup window. Treat those as owned by
                // the rebuilt section so the fresh quarter view can replace them.
                if (IsPointInsideQuarterViewCleanupWindow(frame, centroid, cleanupWindowTolerance))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsPointInsideQuarterViewCleanupWindow(
            QuarterViewSectionFrame frame,
            Point2d worldPoint,
            double tolerance)
        {
            return worldPoint.X >= (frame.CleanupWindow.MinPoint.X - tolerance) &&
                   worldPoint.X <= (frame.CleanupWindow.MaxPoint.X + tolerance) &&
                   worldPoint.Y >= (frame.CleanupWindow.MinPoint.Y - tolerance) &&
                   worldPoint.Y <= (frame.CleanupWindow.MaxPoint.Y + tolerance);
        }

        private static bool IsPointInsideQuarterViewFrame(
            QuarterViewSectionFrame frame,
            Point2d worldPoint,
            double tolerance)
        {
            if (!TryConvertQuarterWorldToLocal(frame, worldPoint, out var u, out var v))
            {
                return false;
            }

            return u >= (frame.WestEdgeU - tolerance) && u <= (frame.EastEdgeU + tolerance) &&
                   v >= (frame.SouthEdgeV - tolerance) && v <= (frame.NorthEdgeV + tolerance);
        }

        private static bool TryConvertQuarterWorldToLocal(
            QuarterViewSectionFrame frame,
            Point2d worldPoint,
            out double u,
            out double v)
        {
            u = default;
            v = default;

            var rel = worldPoint - frame.Origin;
            var ex = frame.EastUnit.X;
            var ey = frame.EastUnit.Y;
            var nx = frame.NorthUnit.X;
            var ny = frame.NorthUnit.Y;
            var det = (ex * ny) - (ey * nx);
            if (Math.Abs(det) <= 1e-12)
            {
                return false;
            }

            u = ((rel.X * ny) - (rel.Y * nx)) / det;
            v = ((ex * rel.Y) - (ey * rel.X)) / det;
            return true;
        }

        private static double DistanceToClosedInterval(double value, double minInclusive, double maxInclusive)
        {
            if (value < minInclusive)
            {
                return minInclusive - value;
            }

            if (value > maxInclusive)
            {
                return value - maxInclusive;
            }

            return 0.0;
        }

        private static double GetQuarterSouthBoundaryCenterGapPenalty(double centerGap)
        {
            const double softGap = 40.0;
            if (centerGap <= softGap)
            {
                return centerGap * 0.02;
            }

            return (softGap * 0.02) + ((centerGap - softGap) * 2.0);
        }

        private static double GetQuarterDividerSpanPenalty(double dividerGap)
        {
            const double softGap = 6.0;
            if (dividerGap <= softGap)
            {
                return dividerGap * 0.05;
            }

            return (softGap * 0.05) + ((dividerGap - softGap) * 4.0);
        }

        private static double ClampQuarterSouthBoundaryV(
            double candidateV,
            bool isCorrectionSouthBoundary,
            double southBoundaryLimitV,
            double centerV,
            double minQuarterSpan)
        {
            if (isCorrectionSouthBoundary)
            {
                return Math.Min(candidateV, centerV - minQuarterSpan);
            }

            return Math.Min(candidateV, southBoundaryLimitV);
        }

        private static void ApplyCorrectionSouthOverridesPreClamp(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> boundarySegments,
            bool isCorrectionSouthBoundary,
            Point2d selectedSouthBoundarySegmentA,
            Point2d selectedSouthBoundarySegmentB,
            Point2d dividerLineA,
            Point2d dividerLineB,
            double dividerU,
            bool hasWestBoundarySegment,
            Point2d westBoundarySegmentA,
            Point2d westBoundarySegmentB,
            ref double westAtSouthU,
            ref double southAtWestV,
            ref double southAtMidU,
            ref double southAtMidV,
            ref double southAtEastV,
            Logger? logger)
        {
            if (!isCorrectionSouthBoundary)
            {
                return;
            }

            WriteQuarterCorrectionSouthDirectDiagnostics(
                frame,
                boundarySegments,
                dividerLineA,
                dividerLineB,
                dividerU,
                hasWestBoundarySegment,
                westBoundarySegmentA,
                westBoundarySegmentB,
                logger);

            var resolvedLocalCorrectionWest = false;
            if (hasWestBoundarySegment &&
                TryResolveQuarterSouthWestCorrectionCorner(
                    frame,
                    boundarySegments,
                    westBoundarySegmentA,
                    westBoundarySegmentB,
                    centerU: dividerU,
                    minQuarterSpan: 0.0,
                    out var localCorrectionWestU,
                    out var localCorrectionWestV,
                    out _,
                    out _))
            {
                westAtSouthU = localCorrectionWestU;
                southAtWestV = localCorrectionWestV;
                resolvedLocalCorrectionWest = true;
            }

            var resolvedLocalCorrectionSouthMid = TryResolveQuarterSouthDividerCorrectionIntersection(
                frame,
                boundarySegments,
                dividerLineA,
                dividerLineB,
                out var localCorrectionSouthMidU,
                out var localCorrectionSouthMidV,
                out _);
            if (resolvedLocalCorrectionSouthMid)
            {
                southAtMidU = localCorrectionSouthMidU;
                southAtMidV = localCorrectionSouthMidV;
            }

            var southCorrectionSouthA = selectedSouthBoundarySegmentA;
            var southCorrectionSouthB = selectedSouthBoundarySegmentB;
            if (southCorrectionSouthA.GetDistanceTo(southCorrectionSouthB) <= 1e-6)
            {
                return;
            }

            if (!resolvedLocalCorrectionWest &&
                TryResolveQuarterViewSouthWestCorrectionIntersection(
                    frame,
                    boundarySegments,
                    southCorrectionSouthA,
                    southCorrectionSouthB,
                    out var forcedSouthWestU,
                    out var forcedSouthWestV))
            {
                // Above correction lines, SW must terminate at the apparent
                // L-USEC-0 (vertical) x south L-USEC-C-0 intersection.
                westAtSouthU = forcedSouthWestU;
                southAtWestV = forcedSouthWestV;

                // Keep the SW-quarter SE corner on the same south correction boundary,
                // but preserve the mid blind-line perpendicular crossing using the
                // actual quarter-divider line (BottomAnchor -> TopAnchor).
                if (!resolvedLocalCorrectionSouthMid &&
                    TryIntersectLocalInfiniteLineWithBoundedSegmentExtension(
                        frame,
                        dividerLineA,
                        dividerLineB,
                        southCorrectionSouthA,
                        southCorrectionSouthB,
                        maxSegmentExtension: 80.0,
                        out var apparentSouthMidU,
                        out var apparentSouthMidV))
                {
                    southAtMidU = apparentSouthMidU;
                    southAtMidV = apparentSouthMidV;
                }
                else if (!resolvedLocalCorrectionSouthMid &&
                         TryProjectBoundarySegmentVAtU(
                         frame,
                         southCorrectionSouthA,
                         southCorrectionSouthB,
                         dividerU,
                         out var forcedSouthMidV))
                {
                    southAtMidU = dividerU;
                    southAtMidV = forcedSouthMidV;
                }
                else if (!resolvedLocalCorrectionSouthMid &&
                         TryIntersectBoundarySegmentsLocal(
                             frame,
                             dividerLineA,
                             dividerLineB,
                             southCorrectionSouthA,
                             southCorrectionSouthB,
                             out var fallbackSouthMidU,
                             out var fallbackSouthMidV))
                {
                    southAtMidU = fallbackSouthMidU;
                    southAtMidV = fallbackSouthMidV;
                }

                if (TryProjectBoundarySegmentVAtU(
                        frame,
                        southCorrectionSouthA,
                        southCorrectionSouthB,
                        frame.EastEdgeU,
                        out var forcedSouthEastV))
                {
                        southAtEastV = forcedSouthEastV;
                }
            }

            if (!resolvedLocalCorrectionWest &&
                !resolvedLocalCorrectionSouthMid &&
                hasWestBoundarySegment &&
                TryIntersectBoundarySegmentWithLocalLine(
                    frame,
                    westBoundarySegmentA,
                    westBoundarySegmentB,
                    southAtMidU,
                    southAtMidV,
                    frame.EastEdgeU,
                    southAtEastV,
                    out var correctionSouthWestU,
                    out var correctionSouthWestV))
            {
                // Keep SW on the same south trend used by mid/east correction endpoints.
                westAtSouthU = correctionSouthWestU;
                southAtWestV = correctionSouthWestV;
            }
        }

        private static bool TryIntersectLocalInfiniteLineWithBoundedSegmentExtension(
            QuarterViewSectionFrame frame,
            Point2d lineAWorld,
            Point2d lineBWorld,
            Point2d segAWorld,
            Point2d segBWorld,
            double maxSegmentExtension,
            out double intersectionU,
            out double intersectionV)
        {
            intersectionU = default;
            intersectionV = default;

            if (!TryConvertQuarterWorldToLocal(frame, lineAWorld, out var pU, out var pV) ||
                !TryConvertQuarterWorldToLocal(frame, lineBWorld, out var p2U, out var p2V) ||
                !TryConvertQuarterWorldToLocal(frame, segAWorld, out var qU, out var qV) ||
                !TryConvertQuarterWorldToLocal(frame, segBWorld, out var q2U, out var q2V))
            {
                return false;
            }

            var p = new Point2d(pU, pV);
            var p2 = new Point2d(p2U, p2V);
            var q = new Point2d(qU, qV);
            var q2 = new Point2d(q2U, q2V);

            var lineDir = p2 - p;
            var segDir = q2 - q;
            var lineLen = lineDir.Length;
            var segLen = segDir.Length;
            if (lineLen <= 1e-9 || segLen <= 1e-9)
            {
                return false;
            }

            var denom = (lineDir.X * segDir.Y) - (lineDir.Y * segDir.X);
            if (Math.Abs(denom) <= 1e-9)
            {
                return false;
            }

            var diff = q - p;
            var t = ((diff.X * segDir.Y) - (diff.Y * segDir.X)) / denom;
            var u = ((diff.X * lineDir.Y) - (diff.Y * lineDir.X)) / denom;

            var extension = 0.0;
            if (u < 0.0)
            {
                extension = -u * segLen;
            }
            else if (u > 1.0)
            {
                extension = (u - 1.0) * segLen;
            }

            if (extension > maxSegmentExtension)
            {
                return false;
            }

            intersectionU = p.X + (lineDir.X * t);
            intersectionV = p.Y + (lineDir.Y * t);
            return true;
        }

        private static void ApplyCorrectionSouthEastProjection(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> boundarySegments,
            bool isCorrectionSouthBoundary,
            double centerU,
            double centerV,
            double minQuarterSpan,
            double southBoundaryLimitV,
            double westAtSouthU,
            double westSouthV,
            double southAtMidU,
            double southAtMidV,
            ref double southAtEastU,
            ref double southAtEastV)
        {
            if (!isCorrectionSouthBoundary)
            {
                return;
            }

            if (TryResolveQuarterViewEastBoundarySegmentOnLayer(
                    frame,
                    boundarySegments,
                    LayerUsecZero,
                    0.0,
                    out var eastUsecZeroA,
                    out var eastUsecZeroB) &&
                TryIntersectBoundarySegmentWithLocalLine(
                    frame,
                    eastUsecZeroA,
                    eastUsecZeroB,
                    westAtSouthU,
                    westSouthV,
                    southAtMidU,
                    southAtMidV,
                    out var projectedSouthEastU,
                    out var projectedSouthEastV))
            {
                const double eastProjectionTolerance = 80.0;
                if (projectedSouthEastU >= (centerU + minQuarterSpan) &&
                    Math.Abs(projectedSouthEastU - frame.EastEdgeU) <= eastProjectionTolerance)
                {
                    southAtEastU = projectedSouthEastU;
                    southAtEastV = ClampQuarterSouthBoundaryV(
                        projectedSouthEastV,
                        isCorrectionSouthBoundary,
                        southBoundaryLimitV,
                        centerV,
                        minQuarterSpan);
                }
            }
        }

        private static bool IsQuarterViewBoundaryCandidateLayer(string? layerName)
        {
            if (string.IsNullOrWhiteSpace(layerName))
            {
                return false;
            }

            return string.Equals(layerName, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "L-SEC-2012", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryResolveNonCorrectionNorthEastFromEastEndpoints(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> boundarySegments,
            Point2d eastBoundarySegmentA,
            Point2d eastBoundarySegmentB,
            Point2d northBoundarySegmentA,
            Point2d northBoundarySegmentB,
            out Point2d resolvedLocal)
        {
            resolvedLocal = default;
            if (boundarySegments == null || boundarySegments.Count == 0)
            {
                return false;
            }

            var haveA = TryConvertQuarterWorldToLocal(frame, eastBoundarySegmentA, out var eastAu, out var eastAv);
            if (!haveA)
            {
                var eastRelA = eastBoundarySegmentA - frame.Origin;
                eastAu = eastRelA.DotProduct(frame.EastUnit);
                eastAv = eastRelA.DotProduct(frame.NorthUnit);
                haveA = true;
            }

            var haveB = TryConvertQuarterWorldToLocal(frame, eastBoundarySegmentB, out var eastBu, out var eastBv);
            if (!haveB)
            {
                var eastRelB = eastBoundarySegmentB - frame.Origin;
                eastBu = eastRelB.DotProduct(frame.EastUnit);
                eastBv = eastRelB.DotProduct(frame.NorthUnit);
                haveB = true;
            }

            if (!haveA || !haveB)
            {
                return false;
            }

            var haveScoredEndpoint = false;
            var bestUseA = false;
            var bestScore = double.MaxValue;
            if (TryScoreNonCorrectionNorthEastEndpointCandidate(
                    frame,
                    boundarySegments,
                    eastBoundarySegmentA,
                    northBoundarySegmentA,
                    northBoundarySegmentB,
                    eastAu,
                    eastAv,
                    out var scoreA))
            {
                haveScoredEndpoint = true;
                bestUseA = true;
                bestScore = scoreA;
            }

            if (TryScoreNonCorrectionNorthEastEndpointCandidate(
                    frame,
                    boundarySegments,
                    eastBoundarySegmentB,
                    northBoundarySegmentA,
                    northBoundarySegmentB,
                    eastBu,
                    eastBv,
                    out var scoreB) &&
                (!haveScoredEndpoint || scoreB < bestScore))
            {
                haveScoredEndpoint = true;
                bestUseA = false;
                bestScore = scoreB;
            }

            if (!haveScoredEndpoint)
            {
                return false;
            }

            resolvedLocal = bestUseA ? new Point2d(eastAu, eastAv) : new Point2d(eastBu, eastBv);
            return true;
        }

        private static bool TryScoreNonCorrectionNorthEastEndpointCandidate(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> boundarySegments,
            Point2d endpointWorld,
            Point2d northBoundarySegmentA,
            Point2d northBoundarySegmentB,
            double endpointU,
            double endpointV,
            out double score)
        {
            score = double.MaxValue;
            const double endpointNodeTol = 1.25;
            const double northBoundaryTouchTol = 1.25;
            var northBoundaryDistance =
                DistancePointToSegment(endpointWorld, northBoundarySegmentA, northBoundarySegmentB);
            var hasHorizontalNode = HasQuarterViewHorizontalEndpointNode(
                frame,
                boundarySegments,
                endpointWorld,
                endpointNodeTol);
            var nearNorthBoundary = northBoundaryDistance <= northBoundaryTouchTol;
            if (!hasHorizontalNode && !nearNorthBoundary)
            {
                return false;
            }

            var northInset = Math.Abs(frame.NorthEdgeV - endpointV);
            score = ((hasHorizontalNode ? 0.0 : 20.0) +
                     (nearNorthBoundary ? 0.0 : 10.0) +
                     (northBoundaryDistance * 6.0) +
                     (northInset * 0.35) +
                     (Math.Abs(frame.EastEdgeU - endpointU) * 0.25));
            return true;
        }

        private static bool HasQuarterViewHorizontalEndpointNode(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> boundarySegments,
            Point2d worldPoint,
            double endpointTol)
        {
            if (boundarySegments == null || boundarySegments.Count == 0)
            {
                return false;
            }

            for (var i = 0; i < boundarySegments.Count; i++)
            {
                var seg = boundarySegments[i];
                if (!IsQuarterViewBoundaryCandidateLayer(seg.Layer) ||
                    !IsQuarterViewHorizontalSegment(frame, seg.A, seg.B))
                {
                    continue;
                }

                if (worldPoint.GetDistanceTo(seg.A) <= endpointTol ||
                    worldPoint.GetDistanceTo(seg.B) <= endpointTol)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsQuarterViewHorizontalSegment(
            QuarterViewSectionFrame frame,
            Point2d a,
            Point2d b)
        {
            var delta = b - a;
            var eastComp = Math.Abs(delta.DotProduct(frame.EastUnit));
            var northComp = Math.Abs(delta.DotProduct(frame.NorthUnit));
            return eastComp > northComp;
        }

        private static bool IsQuarterViewVerticalSegment(
            QuarterViewSectionFrame frame,
            Point2d a,
            Point2d b)
        {
            var delta = b - a;
            var eastComp = Math.Abs(delta.DotProduct(frame.EastUnit));
            var northComp = Math.Abs(delta.DotProduct(frame.NorthUnit));
            return northComp > eastComp;
        }

        private static bool HasQuarterViewEndpointHorizontalAndVerticalEvidence(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> segments,
            Point2d worldPoint,
            double tolerance,
            out double bestEndpointDistance)
        {
            bestEndpointDistance = double.MaxValue;
            var hasHorizontal = false;
            var hasVertical = false;
            for (var i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                if (!IsQuarterViewBoundaryCandidateLayer(seg.Layer))
                {
                    continue;
                }

                var segmentIsHorizontal = IsQuarterViewHorizontalSegment(frame, seg.A, seg.B);
                var segmentIsVertical = IsQuarterViewVerticalSegment(frame, seg.A, seg.B);
                if (!segmentIsHorizontal && !segmentIsVertical)
                {
                    continue;
                }

                var endpointDistanceA = worldPoint.GetDistanceTo(seg.A);
                if (endpointDistanceA <= tolerance)
                {
                    bestEndpointDistance = Math.Min(bestEndpointDistance, endpointDistanceA);
                    if (segmentIsHorizontal)
                    {
                        hasHorizontal = true;
                    }

                    if (segmentIsVertical)
                    {
                        hasVertical = true;
                    }
                }

                var endpointDistanceB = worldPoint.GetDistanceTo(seg.B);
                if (endpointDistanceB <= tolerance)
                {
                    bestEndpointDistance = Math.Min(bestEndpointDistance, endpointDistanceB);
                    if (segmentIsHorizontal)
                    {
                        hasHorizontal = true;
                    }

                    if (segmentIsVertical)
                    {
                        hasVertical = true;
                    }
                }

                if (hasHorizontal && hasVertical)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryResolveQuarterViewVerticalDividerSegmentFromQsec(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(ObjectId Id, Point2d A, Point2d B)> dividerSegments,
            out ObjectId segmentId,
            out Point2d segmentA,
            out Point2d segmentB)
        {
            segmentId = ObjectId.Null;
            segmentA = default;
            segmentB = default;
            if (dividerSegments == null || dividerSegments.Count == 0)
            {
                return false;
            }

            const double overlapPadding = 16.0;
            const double minProjectedOverlap = 30.0;
            const double maxCenterGap = 60.0;
            var found = false;
            var bestScore = double.MaxValue;
            for (var i = 0; i < dividerSegments.Count; i++)
            {
                var seg = dividerSegments[i];
                var delta = seg.B - seg.A;
                var eastComp = Math.Abs(delta.DotProduct(frame.EastUnit));
                var northComp = Math.Abs(delta.DotProduct(frame.NorthUnit));
                if (northComp <= eastComp)
                {
                    continue;
                }

                var relA = seg.A - frame.Origin;
                var relB = seg.B - frame.Origin;
                var uA = relA.DotProduct(frame.EastUnit);
                var uB = relB.DotProduct(frame.EastUnit);
                var vA = relA.DotProduct(frame.NorthUnit);
                var vB = relB.DotProduct(frame.NorthUnit);
                var overlap = Math.Min(Math.Max(vA, vB), frame.NorthEdgeV + overlapPadding) -
                              Math.Max(Math.Min(vA, vB), frame.SouthEdgeV - overlapPadding);
                if (overlap < minProjectedOverlap)
                {
                    continue;
                }

                var uAtMidV = 0.5 * (uA + uB);
                if (TryProjectBoundarySegmentUAtV(frame, seg.A, seg.B, frame.MidV, out var projectedMidU))
                {
                    uAtMidV = projectedMidU;
                }

                var centerGap = Math.Abs(uAtMidV - frame.MidU);
                if (centerGap > maxCenterGap)
                {
                    continue;
                }

                if (!found || centerGap < bestScore)
                {
                    found = true;
                    bestScore = centerGap;
                    segmentId = seg.Id;
                    segmentA = seg.A;
                    segmentB = seg.B;
                }
            }

            return found;
        }

        private static void TryUpdateQuarterViewVerticalDividerSegment(
            Transaction transaction,
            QuarterViewSectionFrame frame,
            ObjectId dividerSegmentId,
            Point2d currentA,
            Point2d currentB,
            Point2d resolvedSouth,
            Point2d resolvedNorth,
            Logger? logger)
        {
            if (transaction == null ||
                dividerSegmentId.IsNull ||
                currentA.GetDistanceTo(currentB) <= 1e-6 ||
                resolvedSouth.GetDistanceTo(resolvedNorth) <= 1e-6)
            {
                return;
            }

            if (!TryConvertQuarterWorldToLocal(frame, currentA, out _, out var currentAv) ||
                !TryConvertQuarterWorldToLocal(frame, currentB, out _, out var currentBv) ||
                !TryConvertQuarterWorldToLocal(frame, resolvedSouth, out _, out var resolvedSouthV) ||
                !TryConvertQuarterWorldToLocal(frame, resolvedNorth, out _, out var resolvedNorthV))
            {
                return;
            }

            if (resolvedSouthV > resolvedNorthV)
            {
                var temp = resolvedSouth;
                resolvedSouth = resolvedNorth;
                resolvedNorth = temp;
            }

            var currentSouth = currentAv <= currentBv ? currentA : currentB;
            var currentNorth = currentAv <= currentBv ? currentB : currentA;
            if (transaction.GetObject(dividerSegmentId, OpenMode.ForWrite, false) is not Entity writable ||
                writable.IsErased ||
                !TryReadOpenLinearSegment(writable, out var writableA, out var writableB))
            {
                return;
            }

            if (TryResolveNearbyQuarterDividerCorrectionDrawingEndpoint(
                    transaction,
                    writable.OwnerId,
                    frame,
                    currentA,
                    currentB,
                    currentSouth,
                    maxEndpointMove: 2.0,
                    out var preferredCorrectionSouth,
                    out var preferredCorrectionSouthMove))
            {
                var resolvedSouthMove = currentSouth.GetDistanceTo(resolvedSouth);
                if (resolvedSouthMove > 10.0 &&
                    preferredCorrectionSouthMove + 2.0 < resolvedSouthMove)
                {
                    resolvedSouth = preferredCorrectionSouth;
                    logger?.WriteLine(
                        $"VERIFY-QTR-QSEC-V-PREF sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                        $"current={currentSouth.X:0.###},{currentSouth.Y:0.###} " +
                        $"resolved={preferredCorrectionSouth.X:0.###},{preferredCorrectionSouth.Y:0.###} move={preferredCorrectionSouthMove:0.###}");
                }
            }

            var southMove = currentSouth.GetDistanceTo(resolvedSouth);
            var northMove = currentNorth.GetDistanceTo(resolvedNorth);
            const double maxDividerEndpointMove = 40.0;
            if (southMove > maxDividerEndpointMove || northMove > maxDividerEndpointMove)
            {
                return;
            }

            var writableSouth = currentAv <= currentBv ? writableA : writableB;
            var southIsStart = writableSouth.GetDistanceTo(writableA) <= writableSouth.GetDistanceTo(writableB);
            if (writable is Line line)
            {
                if (southIsStart)
                {
                    line.StartPoint = new Point3d(resolvedSouth.X, resolvedSouth.Y, line.StartPoint.Z);
                    line.EndPoint = new Point3d(resolvedNorth.X, resolvedNorth.Y, line.EndPoint.Z);
                }
                else
                {
                    line.EndPoint = new Point3d(resolvedSouth.X, resolvedSouth.Y, line.EndPoint.Z);
                    line.StartPoint = new Point3d(resolvedNorth.X, resolvedNorth.Y, line.StartPoint.Z);
                }
            }
            else if (writable is Polyline poly && !poly.Closed && poly.NumberOfVertices == 2)
            {
                if (southIsStart)
                {
                    poly.SetPointAt(0, resolvedSouth);
                    poly.SetPointAt(1, resolvedNorth);
                }
                else
                {
                    poly.SetPointAt(1, resolvedSouth);
                    poly.SetPointAt(0, resolvedNorth);
                }
            }
            else
            {
                return;
            }

            logger?.WriteLine(
                $"VERIFY-QTR-QSEC-V-UPDATE sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                $"south={resolvedSouth.X:0.###},{resolvedSouth.Y:0.###} north={resolvedNorth.X:0.###},{resolvedNorth.Y:0.###} " +
                $"southMove={southMove:0.###} northMove={northMove:0.###}");
        }
        private static int GetQuarterViewBoundaryLayerPriority(string layerName)
        {
            if (string.Equals(layerName, LayerUsecZero, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (string.Equals(layerName, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layerName, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layerName, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layerName, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(layerName, "L-SEC-2012", StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            return 2;
        }

        private static bool IsBlindSouthBoundarySectionForQuarterView(int sectionNumber)
        {
            return (sectionNumber >= 7 && sectionNumber <= 12) ||
                   (sectionNumber >= 19 && sectionNumber <= 24) ||
                   (sectionNumber >= 31 && sectionNumber <= 36);
        }

        private static bool TryResolvePreferredQuarterViewWestBoundary(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> candidates,
            double expectedOffsetMeters,
            double preferredDividerU,
            bool allowInsetDowngrade,
            out double resolvedU,
            out string resolvedLayer,
            out Point2d resolvedA,
            out Point2d resolvedB)
        {
            resolvedU = default;
            resolvedLayer = string.Empty;
            resolvedA = default;
            resolvedB = default;
            if (candidates == null || candidates.Count == 0)
            {
                return false;
            }

            // First pass: keep prior expected inset behavior.
            if (TryResolveQuarterViewWestBoundaryU(
                    frame,
                    candidates,
                    expectedOffsetMeters,
                    preferredDividerU,
                    out resolvedU,
                    out resolvedLayer,
                    out resolvedA,
                    out resolvedB))
            {
                return true;
            }

            if (!allowInsetDowngrade)
            {
                return false;
            }

            // Regression guard: if geometry has already shifted section edge inward,
            // a zero expected offset still selects the intended 20.12-class boundary.
            return TryResolveQuarterViewWestBoundaryU(
                frame,
                candidates,
                0.0,
                preferredDividerU,
                out resolvedU,
                out resolvedLayer,
                out resolvedA,
                out resolvedB);
        }

        private static bool TryResolveQuarterViewWestBoundaryWithInsetFallbacks(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> westResolutionSegments,
            double expectedOffsetMeters,
            double preferredDividerU,
            bool allowInsetDowngrade,
            out double resolvedU,
            out string resolvedLayer,
            out Point2d resolvedA,
            out Point2d resolvedB)
        {
            resolvedU = default;
            resolvedLayer = string.Empty;
            resolvedA = default;
            resolvedB = default;

            var hasResolvedWest = TryResolveQuarterViewWestBoundaryU(
                frame,
                westResolutionSegments,
                expectedOffsetMeters,
                preferredDividerU,
                out resolvedU,
                out resolvedLayer,
                out resolvedA,
                out resolvedB);

            if (!hasResolvedWest &&
                expectedOffsetMeters >= (RoadAllowanceUsecWidthMeters - 0.5) &&
                allowInsetDowngrade)
            {
                // Fallback to SEC-width ownership when USEC-width candidates are absent.
                var secWidthInset = RoadAllowanceSecWidthMeters;
                hasResolvedWest = TryResolveQuarterViewWestBoundaryU(
                    frame,
                    westResolutionSegments,
                    secWidthInset,
                    preferredDividerU,
                    out resolvedU,
                    out resolvedLayer,
                    out resolvedA,
                    out resolvedB);
            }

            if (!hasResolvedWest && expectedOffsetMeters > 0.5 && allowInsetDowngrade)
            {
                // Final fallback keeps legacy near-edge behavior when no offset-class candidate survives filters.
                hasResolvedWest = TryResolveQuarterViewWestBoundaryU(
                    frame,
                    westResolutionSegments,
                    0.0,
                    preferredDividerU,
                    out resolvedU,
                    out resolvedLayer,
                    out resolvedA,
                    out resolvedB);
            }

            return hasResolvedWest;
        }

        private static bool TryResolvePreferredQuarterViewSouthBoundary(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> candidates,
            double fallbackOffsetMeters,
            double preferredDividerU,
            Point2d dividerLineA,
            Point2d dividerLineB,
            out double resolvedV,
            out string resolvedLayer,
            out Point2d resolvedA,
            out Point2d resolvedB)
        {
            resolvedV = default;
            resolvedLayer = string.Empty;
            resolvedA = default;
            resolvedB = default;
            if (candidates == null || candidates.Count == 0)
            {
                return false;
            }

            // First pass: keep prior expected inset behavior.
            if (TryResolveQuarterViewSouthBoundaryV(
                    frame,
                    candidates,
                    fallbackOffsetMeters,
                    preferredDividerU,
                    dividerLineA,
                    dividerLineB,
                    out resolvedV,
                    out resolvedLayer,
                    out resolvedA,
                    out resolvedB))
            {
                return true;
            }

            // Regression guard: allow near-edge fallback for SEC-width ownership.
            return TryResolveQuarterViewSouthBoundaryV(
                frame,
                candidates,
                0.0,
                preferredDividerU,
                dividerLineA,
                dividerLineB,
                out resolvedV,
                out resolvedLayer,
                out resolvedA,
                out resolvedB);
        }

        private static bool TryResolveQuarterViewPreferredWestBoundaryFromSections(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> boundarySegments,
            string westSource,
            double expectedOffsetMeters,
            double preferredDividerU,
            bool allowInsetDowngrade,
            out double resolvedU,
            out string resolvedLayer,
            out Point2d resolvedA,
            out Point2d resolvedB)
        {
            resolvedU = default;
            resolvedLayer = string.Empty;
            resolvedA = default;
            resolvedB = default;

            var canPromoteFromWestRoadAllowanceSource =
                string.Equals(westSource, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(westSource, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(westSource, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(westSource, "L-USEC2012", StringComparison.OrdinalIgnoreCase);
            if (!canPromoteFromWestRoadAllowanceSource)
            {
                return false;
            }

            var preferredWestSegments = boundarySegments
                .Where(s =>
                    string.Equals(s.Layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.Layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.Layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.Layer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase))
                .ToList();
            return TryResolvePreferredQuarterViewWestBoundary(
                frame,
                preferredWestSegments,
                expectedOffsetMeters,
                preferredDividerU,
                allowInsetDowngrade,
                out resolvedU,
                out resolvedLayer,
                out resolvedA,
                out resolvedB);
        }

        private static bool TryResolveQuarterViewPreferredSouthBoundaryFromSections(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> boundarySegments,
            string southSource,
            double fallbackOffsetMeters,
            double preferredDividerU,
            Point2d dividerLineA,
            Point2d dividerLineB,
            out double resolvedV,
            out string resolvedLayer,
            out Point2d resolvedA,
            out Point2d resolvedB)
        {
            resolvedV = default;
            resolvedLayer = string.Empty;
            resolvedA = default;
            resolvedB = default;

            if (IsBlindSouthBoundarySectionForQuarterView(frame.SectionNumber) ||
                !string.Equals(southSource, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                fallbackOffsetMeters > (RoadAllowanceSecWidthMeters + 0.5))
            {
                return false;
            }

            var preferredSouthSegments = boundarySegments
                .Where(s =>
                    string.Equals(s.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(s.Layer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase))
                .ToList();
            return TryResolvePreferredQuarterViewSouthBoundary(
                frame,
                preferredSouthSegments,
                fallbackOffsetMeters,
                preferredDividerU,
                dividerLineA,
                dividerLineB,
                out resolvedV,
                out resolvedLayer,
                out resolvedA,
                out resolvedB);
        }

        private static void WriteQuarterBoundarySelectionDiagnostics(
            Logger? logger,
            QuarterViewSectionFrame frame,
            Point2d dividerLineA,
            Point2d dividerLineB,
            double dividerPreferredU,
            bool hasSouthBoundarySegment,
            Point2d southBoundarySegmentA,
            Point2d southBoundarySegmentB,
            string southSource,
            bool hasNorthBoundarySegment,
            Point2d northBoundarySegmentA,
            Point2d northBoundarySegmentB,
            string northSource,
            bool hasWestBoundarySegment,
            Point2d westBoundarySegmentA,
            Point2d westBoundarySegmentB,
            string westSource)
        {
            static (double U, double V) ToLocal(QuarterViewSectionFrame sectionFrame, Point2d point)
            {
                var rel = point - sectionFrame.Origin;
                return (rel.DotProduct(sectionFrame.EastUnit), rel.DotProduct(sectionFrame.NorthUnit));
            }

            if (hasSouthBoundarySegment)
            {
                var sA = ToLocal(frame, southBoundarySegmentA);
                var sB = ToLocal(frame, southBoundarySegmentB);
                var sMinU = Math.Min(sA.U, sB.U);
                var sMaxU = Math.Max(sA.U, sB.U);
                var sDividerGap = DistanceToClosedInterval(dividerPreferredU, sMinU, sMaxU);
                var sDividerLinked = TryIntersectLocalInfiniteLineWithBoundedSegmentExtension(
                    frame,
                    dividerLineA,
                    dividerLineB,
                    southBoundarySegmentA,
                    southBoundarySegmentB,
                    maxSegmentExtension: 80.0,
                    out _,
                    out _);
                logger?.WriteLine(
                    $"VERIFY-QTR-SOUTH-SELECT sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                    $"uRange={sMinU:0.###}..{sMaxU:0.###} dividerU={dividerPreferredU:0.###} dividerGap={sDividerGap:0.###} " +
                    $"dividerLinked={sDividerLinked} seg={southBoundarySegmentA.X:0.###},{southBoundarySegmentA.Y:0.###}->{southBoundarySegmentB.X:0.###},{southBoundarySegmentB.Y:0.###} source={southSource}");
            }

            if (hasNorthBoundarySegment)
            {
                var nA = ToLocal(frame, northBoundarySegmentA);
                var nB = ToLocal(frame, northBoundarySegmentB);
                var nMinU = Math.Min(nA.U, nB.U);
                var nMaxU = Math.Max(nA.U, nB.U);
                var nDividerGap = DistanceToClosedInterval(dividerPreferredU, nMinU, nMaxU);
                var nWestLinked = hasWestBoundarySegment &&
                                  TryIntersectLocalInfiniteLineWithBoundedSegmentExtension(
                                      frame,
                                      westBoundarySegmentA,
                                      westBoundarySegmentB,
                                      northBoundarySegmentA,
                                      northBoundarySegmentB,
                                      maxSegmentExtension: 80.0,
                                      out _,
                                      out _);
                logger?.WriteLine(
                    $"VERIFY-QTR-NORTH-SELECT sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                    $"uRange={nMinU:0.###}..{nMaxU:0.###} dividerU={dividerPreferredU:0.###} dividerGap={nDividerGap:0.###} " +
                    $"westLinked={nWestLinked} seg={northBoundarySegmentA.X:0.###},{northBoundarySegmentA.Y:0.###}->{northBoundarySegmentB.X:0.###},{northBoundarySegmentB.Y:0.###} source={northSource}");
            }

            if (hasWestBoundarySegment)
            {
                var wA = ToLocal(frame, westBoundarySegmentA);
                var wB = ToLocal(frame, westBoundarySegmentB);
                var wMinV = Math.Min(wA.V, wB.V);
                var wMaxV = Math.Max(wA.V, wB.V);
                var wCenterGap = DistanceToClosedInterval(frame.MidV, wMinV, wMaxV);
                logger?.WriteLine(
                    $"VERIFY-QTR-WEST-SELECT sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                    $"vRange={wMinV:0.###}..{wMaxV:0.###} centerV={frame.MidV:0.###} centerGap={wCenterGap:0.###} dividerU={dividerPreferredU:0.###} " +
                    $"seg={westBoundarySegmentA.X:0.###},{westBoundarySegmentA.Y:0.###}->{westBoundarySegmentB.X:0.###},{westBoundarySegmentB.Y:0.###} source={westSource}");
            }
        }

        private static void ResolveQuarterViewCenterCoordinates(
            QuarterViewSectionFrame frame,
            out double centerU,
            out double centerV)
        {
            centerU = frame.MidU;
            centerV = frame.MidV;
            if (TryIntersectBoundarySegmentsLocal(
                    frame,
                    frame.LeftAnchor,
                    frame.RightAnchor,
                    frame.BottomAnchor,
                    frame.TopAnchor,
                    out var resolvedCenterU,
                    out var resolvedCenterV))
            {
                centerU = resolvedCenterU;
                centerV = resolvedCenterV;
            }
        }

        private static bool TryPromoteQuarterViewSouthBoundaryFromCorrectionCandidate(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> correctionSouthBoundarySegments,
            double centerU,
            double southFallbackOffset,
            ref string southSource,
            ref bool hasSouthBoundarySegment,
            ref Point2d southBoundarySegmentA,
            ref Point2d southBoundarySegmentB,
            ref double southBoundaryV,
            out string promotedFromSource,
            out double currentError,
            out double promotedError)
        {
            promotedFromSource = string.Empty;
            currentError = double.NaN;
            promotedError = double.NaN;

            if (!TryResolveQuarterViewSouthMostCorrectionBoundarySegment(
                    frame,
                    correctionSouthBoundarySegments,
                    out var promotedCorrectionSouthLayer,
                    out var promotedCorrectionSouthA,
                    out var promotedCorrectionSouthB))
            {
                return false;
            }

            const double minPromotionImprovement = 0.15;

            var candidateOutwardDistance = ResolveOutwardDistanceAtMidU(
                frame,
                promotedCorrectionSouthA,
                promotedCorrectionSouthB,
                centerU,
                southBoundaryV);
            var currentOutwardDistance = hasSouthBoundarySegment
                ? ResolveOutwardDistanceAtMidU(frame, southBoundarySegmentA, southBoundarySegmentB, centerU, southBoundaryV)
                : (frame.SouthEdgeV - southBoundaryV);
            var promoteTowardHardRoadAllowance =
                string.Equals(southSource, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(southSource, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase);
            var southPromotionTargetInset = promoteTowardHardRoadAllowance
                ? RoadAllowanceSecWidthMeters
                : (southFallbackOffset > 0.5 ? southFallbackOffset : RoadAllowanceSecWidthMeters);
            var candidateError = Math.Abs(candidateOutwardDistance - southPromotionTargetInset);
            var localCurrentError = Math.Abs(currentOutwardDistance - southPromotionTargetInset);
            if (candidateError + minPromotionImprovement >= localCurrentError)
            {
                return false;
            }

            promotedFromSource = southSource;
            currentError = localCurrentError;
            promotedError = candidateError;
            southBoundaryV = frame.SouthEdgeV - candidateOutwardDistance;
            southSource = promotedCorrectionSouthLayer;
            southBoundarySegmentA = promotedCorrectionSouthA;
            southBoundarySegmentB = promotedCorrectionSouthB;
            hasSouthBoundarySegment = true;
            return true;
        }

        private static double ResolveOutwardDistanceAtMidU(
            QuarterViewSectionFrame frame,
            Point2d a,
            Point2d b,
            double centerU,
            double fallbackV)
        {
            if (TryProjectBoundarySegmentVAtU(frame, a, b, centerU, out var projectedMidV))
            {
                return frame.SouthEdgeV - projectedMidV;
            }

            return frame.SouthEdgeV - fallbackV;
        }

        private static void ResolveQuarterEastMidAtCenter(
            QuarterViewSectionFrame frame,
            bool hasEastBoundarySegment,
            Point2d eastBoundarySegmentA,
            Point2d eastBoundarySegmentB,
            double centerV,
            double fallbackEastBoundaryU,
            double eastBoundaryLimitU,
            double dividerIntersectionDriftTolerance,
            out double eastMidU,
            out double eastMidV)
        {
            if (hasEastBoundarySegment &&
                TryIntersectBoundarySegmentsLocal(
                    frame,
                    frame.LeftAnchor,
                    frame.RightAnchor,
                    eastBoundarySegmentA,
                    eastBoundarySegmentB,
                    out var eastMidUFinal,
                    out var eastMidVFinal) &&
                Math.Abs(eastMidVFinal - centerV) <= dividerIntersectionDriftTolerance)
            {
                eastMidU = eastMidUFinal;
                eastMidV = eastMidVFinal;
                return;
            }

            eastMidU = ResolveQuarterEastBoundaryUAtV(
                frame,
                hasEastBoundarySegment,
                eastBoundarySegmentA,
                eastBoundarySegmentB,
                centerV,
                fallbackEastBoundaryU,
                eastBoundaryLimitU);
            eastMidV = centerV;
        }

        private static bool IsQuarterNorthEastCorrectionAdjoining(
            bool isCorrectionSouthBoundary,
            string northSource)
        {
            return isCorrectionSouthBoundary ||
                   string.Equals(northSource, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(northSource, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryResolveQuarterApparentEastCornerIntersection(
            QuarterViewSectionFrame frame,
            Point2d eastBoundarySegmentA,
            Point2d eastBoundarySegmentB,
            Point2d sideBoundarySegmentA,
            Point2d sideBoundarySegmentB,
            bool northBand,
            out double resolvedU,
            out double resolvedV,
            out double eastOffset,
            out double sideOffset)
        {
            resolvedU = default;
            resolvedV = default;
            eastOffset = default;
            sideOffset = default;

            if (!TryIntersectLocalInfiniteLines(
                    frame,
                    eastBoundarySegmentA,
                    eastBoundarySegmentB,
                    sideBoundarySegmentA,
                    sideBoundarySegmentB,
                    out resolvedU,
                    out resolvedV))
            {
                return false;
            }

            eastOffset = resolvedU - frame.EastEdgeU;
            sideOffset = northBand
                ? (resolvedV - frame.NorthEdgeV)
                : (frame.SouthEdgeV - resolvedV);

            const double minOffset = -6.0;
            const double maxOffset = 90.0;
            return eastOffset >= -maxOffset &&
                   eastOffset <= maxOffset &&
                   sideOffset >= minOffset &&
                   sideOffset <= maxOffset;
        }

        private static void ResolveQuarterEastCornerFromBoundarySegments(
            QuarterViewSectionFrame frame,
            Point2d eastBoundarySegmentA,
            Point2d eastBoundarySegmentB,
            Point2d sideBoundarySegmentA,
            Point2d sideBoundarySegmentB,
            bool cornerLockedByApparentIntersection,
            ref double cornerU,
            ref double cornerV)
        {
            if (cornerLockedByApparentIntersection)
            {
                return;
            }

            if (TryIntersectBoundarySegmentsLocal(
                    frame,
                    eastBoundarySegmentA,
                    eastBoundarySegmentB,
                    sideBoundarySegmentA,
                    sideBoundarySegmentB,
                    out var resolvedCornerU,
                    out var resolvedCornerV))
            {
                cornerU = resolvedCornerU;
                cornerV = resolvedCornerV;
            }
        }

        private static bool TryGetProtectedQuarterCorner(
            IReadOnlyList<Point2d> protectedSouthMidCorners,
            IReadOnlyList<Point2d> protectedNorthMidCorners,
            IReadOnlyList<Point2d> protectedWestBoundaryCorners,
            IReadOnlyList<Point2d> protectedEastBoundaryCorners,
            Point2d point,
            out Point2d corner)
        {
            corner = default;
            if (protectedSouthMidCorners.Count == 0 &&
                protectedNorthMidCorners.Count == 0 &&
                protectedWestBoundaryCorners.Count == 0 &&
                protectedEastBoundaryCorners.Count == 0)
            {
                return false;
            }

            const double tolerance = 0.05;
            return TryFindQuarterCornerWithinTolerance(protectedSouthMidCorners, point, tolerance, out corner) ||
                   TryFindQuarterCornerWithinTolerance(protectedNorthMidCorners, point, tolerance, out corner) ||
                   TryFindQuarterCornerWithinTolerance(protectedWestBoundaryCorners, point, tolerance, out corner) ||
                   TryFindQuarterCornerWithinTolerance(protectedEastBoundaryCorners, point, tolerance, out corner);
        }

        private static bool TryFindQuarterCornerWithinTolerance(
            IReadOnlyList<Point2d> corners,
            Point2d point,
            double tolerance,
            out Point2d corner)
        {
            corner = default;
            for (var i = 0; i < corners.Count; i++)
            {
                var candidate = corners[i];
                if (point.GetDistanceTo(candidate) > tolerance)
                {
                    continue;
                }

                corner = candidate;
                return true;
            }

            return false;
        }

        private static double ResolveQuarterSouthBoundaryVAtU(
            QuarterViewSectionFrame frame,
            bool hasSouthBoundarySegment,
            Point2d southBoundarySegmentA,
            Point2d southBoundarySegmentB,
            double targetU,
            double fallbackSouthBoundaryV,
            double southBoundaryLimitV)
        {
            if (hasSouthBoundarySegment &&
                TryProjectBoundarySegmentVAtU(frame, southBoundarySegmentA, southBoundarySegmentB, targetU, out var projectedV))
            {
                return Math.Min(projectedV, southBoundaryLimitV);
            }

            return Math.Min(fallbackSouthBoundaryV, southBoundaryLimitV);
        }

        private static double ResolveQuarterWestBoundaryUAtV(
            QuarterViewSectionFrame frame,
            bool hasWestBoundarySegment,
            Point2d westBoundarySegmentA,
            Point2d westBoundarySegmentB,
            double targetV,
            double fallbackWestBoundaryU,
            double westBoundaryLimitU)
        {
            if (hasWestBoundarySegment &&
                TryProjectBoundarySegmentUAtV(frame, westBoundarySegmentA, westBoundarySegmentB, targetV, out var projectedU))
            {
                return Math.Min(projectedU, westBoundaryLimitU);
            }

            return Math.Min(fallbackWestBoundaryU, westBoundaryLimitU);
        }

        private static double ResolveQuarterNorthBoundaryVAtU(
            QuarterViewSectionFrame frame,
            bool hasNorthBoundarySegment,
            Point2d northBoundarySegmentA,
            Point2d northBoundarySegmentB,
            double targetU,
            double fallbackNorthBoundaryV,
            double northBoundaryLimitV)
        {
            if (hasNorthBoundarySegment &&
                TryProjectBoundarySegmentVAtU(frame, northBoundarySegmentA, northBoundarySegmentB, targetU, out var projectedV))
            {
                return Math.Max(projectedV, northBoundaryLimitV);
            }

            return Math.Max(fallbackNorthBoundaryV, northBoundaryLimitV);
        }

        private static double ResolveQuarterEastBoundaryUAtV(
            QuarterViewSectionFrame frame,
            bool hasEastBoundarySegment,
            Point2d eastBoundarySegmentA,
            Point2d eastBoundarySegmentB,
            double targetV,
            double fallbackEastBoundaryU,
            double eastBoundaryLimitU)
        {
            if (hasEastBoundarySegment &&
                TryProjectBoundarySegmentUAtV(frame, eastBoundarySegmentA, eastBoundarySegmentB, targetV, out var projectedU))
            {
                return Math.Max(projectedU, eastBoundaryLimitU);
            }

            return Math.Max(fallbackEastBoundaryU, eastBoundaryLimitU);
        }

        private static bool TryResolveQuarterViewEastSideHorizontalBoundarySegment(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> segments,
            string targetLayer,
            bool northBand,
            double minEastU,
            out Point2d segmentA,
            out Point2d segmentB)
        {
            segmentA = default;
            segmentB = default;
            if (segments == null || segments.Count == 0 || string.IsNullOrWhiteSpace(targetLayer))
            {
                return false;
            }

            const double overlapPadding = 16.0;
            const double minProjectedOverlap = 20.0;
            const double maxEdgeGap = 80.0;
            const double maxEastEndpointGap = 90.0;
            var targetEdgeV = northBand ? frame.NorthEdgeV : frame.SouthEdgeV;
            var found = false;
            var bestScore = double.MaxValue;
            for (var i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                if (!string.Equals(seg.Layer, targetLayer, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var delta = seg.B - seg.A;
                var eastComp = Math.Abs(delta.DotProduct(frame.EastUnit));
                var northComp = Math.Abs(delta.DotProduct(frame.NorthUnit));
                if (eastComp <= northComp)
                {
                    continue;
                }

                if (!TryConvertQuarterWorldToLocal(frame, seg.A, out var uA, out var vA) ||
                    !TryConvertQuarterWorldToLocal(frame, seg.B, out var uB, out var vB))
                {
                    continue;
                }

                var overlap = Math.Min(Math.Max(uA, uB), frame.EastEdgeU + overlapPadding) -
                              Math.Max(Math.Min(uA, uB), frame.WestEdgeU - overlapPadding);
                if (overlap < minProjectedOverlap)
                {
                    continue;
                }

                var eastEndpointU = Math.Max(uA, uB);
                if (eastEndpointU < minEastU)
                {
                    continue;
                }

                var eastEndpointGap = Math.Abs(frame.EastEdgeU - eastEndpointU);
                if (eastEndpointGap > maxEastEndpointGap)
                {
                    continue;
                }

                var boundaryEdgeV = northBand ? Math.Max(vA, vB) : Math.Min(vA, vB);
                var edgeGap = Math.Abs(boundaryEdgeV - targetEdgeV);
                if (edgeGap > maxEdgeGap)
                {
                    continue;
                }

                var score = (edgeGap * 10.0) + eastEndpointGap;
                if (!found || score < bestScore)
                {
                    found = true;
                    bestScore = score;
                    segmentA = seg.A;
                    segmentB = seg.B;
                }
            }

            return found;
        }

        private static bool TryResolvePreferredQuarterViewEastBoundarySegment(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> boundarySegments,
            double expectedOffsetMeters,
            out Point2d resolvedEastA,
            out Point2d resolvedEastB,
            out string resolvedEastLayer)
        {
            resolvedEastA = default;
            resolvedEastB = default;
            resolvedEastLayer = string.Empty;

            // Prefer explicit quarter/section hard boundaries before broad fallback.
            var preferredEastLayers = new[]
            {
                LayerUsecTwenty,
                "L-USEC-2012",
                LayerUsecBase,
                "L-SEC",
                "L-SEC-2012",
                LayerUsecZero
            };

            // First pass: prefer a full-width inward road-allowance candidate.
            for (var li = 0; li < preferredEastLayers.Length; li++)
            {
                var layer = preferredEastLayers[li];
                if (!TryResolveQuarterViewEastBoundarySegmentOnLayer(
                        frame,
                        boundarySegments,
                        layer,
                        expectedOffsetMeters,
                        out resolvedEastA,
                        out resolvedEastB))
                {
                    continue;
                }

                resolvedEastLayer = layer;
                return true;
            }

            // Fallback: preserve legacy near-edge behavior when full-width geometry is unavailable.
            if (expectedOffsetMeters > 0.5)
            {
                for (var li = 0; li < preferredEastLayers.Length; li++)
                {
                    var layer = preferredEastLayers[li];
                    if (!TryResolveQuarterViewEastBoundarySegmentOnLayer(
                            frame,
                            boundarySegments,
                            layer,
                            0.0,
                            out resolvedEastA,
                            out resolvedEastB))
                    {
                        continue;
                    }

                    resolvedEastLayer = layer;
                    return true;
                }
            }

            return false;
        }

        private static bool TryResolvePreferredQuarterViewEastBoundary(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> boundarySegments,
            double expectedOffsetMeters,
            out double resolvedEastMidU,
            out Point2d resolvedEastA,
            out Point2d resolvedEastB,
            out string resolvedEastLayer)
        {
            resolvedEastMidU = default;
            resolvedEastA = default;
            resolvedEastB = default;
            resolvedEastLayer = string.Empty;

            if (!TryResolvePreferredQuarterViewEastBoundarySegment(
                    frame,
                    boundarySegments,
                    expectedOffsetMeters,
                    out resolvedEastA,
                    out resolvedEastB,
                    out resolvedEastLayer))
            {
                return false;
            }

            var relEastA = resolvedEastA - frame.Origin;
            var relEastB = resolvedEastB - frame.Origin;
            resolvedEastMidU = 0.5 * (
                relEastA.DotProduct(frame.EastUnit) +
                relEastB.DotProduct(frame.EastUnit));
            if (TryProjectBoundarySegmentUAtV(
                    frame,
                    resolvedEastA,
                    resolvedEastB,
                    frame.MidV,
                    out var projectedEastMidU))
            {
                resolvedEastMidU = projectedEastMidU;
            }

            return true;
        }

        private static bool TryResolveQuarterViewWestBoundaryU(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> segments,
            double expectedOffsetMeters,
            double preferredDividerU,
            out double boundaryU,
            out string sourceLayer,
            out Point2d boundarySegmentA,
            out Point2d boundarySegmentB)
        {
            boundaryU = default;
            sourceLayer = string.Empty;
            boundarySegmentA = default;
            boundarySegmentB = default;

            const double axisTolerance = 0.5;
            const double overlapPadding = 16.0;
            const double minProjectedOverlap = 20.0;
            const double maxOffset = 40.0;
            const double minRoadAllowanceOffset = 5.0;
            const double maxTargetOffsetError = 6.0;
            const double minDividerSeparation = 5.0;

            var bestPriority = int.MaxValue;
            var bestScore = double.MaxValue;
            var bestCenterGap = double.MaxValue;
            var bestOutwardDistance = double.MaxValue;
            foreach (var segment in segments)
            {
                if (string.Equals(segment.Layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var delta = segment.B - segment.A;
                var eastComp = Math.Abs(delta.DotProduct(frame.EastUnit));
                var northComp = Math.Abs(delta.DotProduct(frame.NorthUnit));
                if (northComp <= eastComp)
                {
                    continue;
                }

                var relA = segment.A - frame.Origin;
                var relB = segment.B - frame.Origin;
                var uA = relA.DotProduct(frame.EastUnit);
                var uB = relB.DotProduct(frame.EastUnit);
                var vA = relA.DotProduct(frame.NorthUnit);
                var vB = relB.DotProduct(frame.NorthUnit);
                var overlap = Math.Min(Math.Max(vA, vB), frame.NorthEdgeV + overlapPadding) -
                              Math.Max(Math.Min(vA, vB), frame.SouthEdgeV - overlapPadding);
                if (overlap < minProjectedOverlap)
                {
                    continue;
                }

                var uLine = 0.5 * (uA + uB);
                var uAtMidV = uLine;
                var dv = vB - vA;
                if (Math.Abs(dv) > 1e-6)
                {
                    var tMid = (frame.MidV - vA) / dv;
                    if (tMid >= -0.35 && tMid <= 1.35)
                    {
                        uAtMidV = uA + ((uB - uA) * tMid);
                    }
                }

                var outwardDistance = frame.WestEdgeU - uAtMidV;
                if (outwardDistance < -axisTolerance || outwardDistance > maxOffset)
                {
                    continue;
                }

                if (uAtMidV > (preferredDividerU - minDividerSeparation))
                {
                    continue;
                }

                var priority = GetQuarterViewBoundaryLayerPriority(segment.Layer);
                var isCorrectionBoundary = string.Equals(segment.Layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase);
                var targetOffset = isCorrectionBoundary
                    ? CorrectionLineInsetMeters
                    : Math.Max(0.0, expectedOffsetMeters);
                if (!isCorrectionBoundary &&
                    targetOffset >= minRoadAllowanceOffset &&
                    outwardDistance < minRoadAllowanceOffset)
                {
                    continue;
                }

                if (!isCorrectionBoundary &&
                    targetOffset >= minRoadAllowanceOffset &&
                    Math.Abs(outwardDistance - targetOffset) > maxTargetOffsetError)
                {
                    continue;
                }

                var segmentMinV = Math.Min(vA, vB);
                var segmentMaxV = Math.Max(vA, vB);
                var centerGap = DistanceToClosedInterval(frame.MidV, segmentMinV, segmentMaxV);
                var centerPenalty = GetQuarterSouthBoundaryCenterGapPenalty(centerGap);
                var score = Math.Abs(outwardDistance - targetOffset) + centerPenalty;
                if (score < bestScore ||
                    (Math.Abs(score - bestScore) <= 1e-6 && centerGap < bestCenterGap) ||
                    (Math.Abs(score - bestScore) <= 1e-6 && priority < bestPriority) ||
                    (Math.Abs(score - bestScore) <= 1e-6 &&
                     Math.Abs(centerGap - bestCenterGap) <= 1e-6 &&
                     priority == bestPriority &&
                     outwardDistance < bestOutwardDistance))
                {
                    bestPriority = priority;
                    bestScore = score;
                    bestCenterGap = centerGap;
                    bestOutwardDistance = outwardDistance;
                    boundaryU = uAtMidV;
                    sourceLayer = segment.Layer;
                    boundarySegmentA = segment.A;
                    boundarySegmentB = segment.B;
                }
            }

            if (bestPriority == int.MaxValue)
            {
                return false;
            }

            return true;
        }

        private static bool TryResolveQuarterViewSouthBoundaryV(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> segments,
            double expectedOffsetMeters,
            double preferredDividerU,
            Point2d dividerLineA,
            Point2d dividerLineB,
            out double boundaryV,
            out string sourceLayer,
            out Point2d boundarySegmentA,
            out Point2d boundarySegmentB)
        {
            boundaryV = default;
            sourceLayer = string.Empty;
            boundarySegmentA = default;
            boundarySegmentB = default;

            const double axisTolerance = 0.5;
            const double overlapPadding = 16.0;
            const double minProjectedOverlap = 20.0;
            const double maxOffset = 40.0;
            const double minRoadAllowanceOffset = 5.0;
            const double maxTargetOffsetError = 6.0;
            const double maxDividerIntersectionExtension = 80.0;
            var requireDividerLinkedCandidate = expectedOffsetMeters <= 0.5 &&
                                                dividerLineA.GetDistanceTo(dividerLineB) > 1e-6;

            var bestPriority = int.MaxValue;
            var bestScore = double.MaxValue;
            var bestCenterGap = double.MaxValue;
            var bestDividerGap = double.MaxValue;
            var bestOutwardDistance = double.MaxValue;
            var bestBoundaryV = default(double);
            var bestSourceLayer = string.Empty;
            var bestBoundaryA = default(Point2d);
            var bestBoundaryB = default(Point2d);

            var bestLinkedPriority = int.MaxValue;
            var bestLinkedScore = double.MaxValue;
            var bestLinkedCenterGap = double.MaxValue;
            var bestLinkedDividerGap = double.MaxValue;
            var bestLinkedOutwardDistance = double.MaxValue;
            var bestLinkedBoundaryV = default(double);
            var bestLinkedSourceLayer = string.Empty;
            var bestLinkedBoundaryA = default(Point2d);
            var bestLinkedBoundaryB = default(Point2d);
            foreach (var segment in segments)
            {
                if (string.Equals(segment.Layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var delta = segment.B - segment.A;
                var eastComp = Math.Abs(delta.DotProduct(frame.EastUnit));
                var northComp = Math.Abs(delta.DotProduct(frame.NorthUnit));
                if (eastComp <= northComp)
                {
                    continue;
                }

                var relA = segment.A - frame.Origin;
                var relB = segment.B - frame.Origin;
                var uA = relA.DotProduct(frame.EastUnit);
                var uB = relB.DotProduct(frame.EastUnit);
                var vA = relA.DotProduct(frame.NorthUnit);
                var vB = relB.DotProduct(frame.NorthUnit);
                var overlap = Math.Min(Math.Max(uA, uB), frame.EastEdgeU + overlapPadding) -
                              Math.Max(Math.Min(uA, uB), frame.WestEdgeU - overlapPadding);
                if (overlap < minProjectedOverlap)
                {
                    continue;
                }

                var vLine = 0.5 * (vA + vB);
                var vAtMidU = vLine;
                var du = uB - uA;
                if (Math.Abs(du) > 1e-6)
                {
                    var tMid = (frame.MidU - uA) / du;
                    if (tMid >= -0.35 && tMid <= 1.35)
                    {
                        vAtMidU = vA + ((vB - vA) * tMid);
                    }
                }

                var outwardDistance = frame.SouthEdgeV - vAtMidU;
                if (outwardDistance < -axisTolerance || outwardDistance > maxOffset)
                {
                    continue;
                }

                var priority = GetQuarterViewBoundaryLayerPriority(segment.Layer);
                var isCorrectionBoundary = string.Equals(segment.Layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase);
                var targetOffset = isCorrectionBoundary
                    ? CorrectionLineInsetMeters
                    : Math.Max(0.0, expectedOffsetMeters);
                if (!isCorrectionBoundary &&
                    targetOffset >= minRoadAllowanceOffset &&
                    outwardDistance < minRoadAllowanceOffset)
                {
                    continue;
                }

                if (!isCorrectionBoundary &&
                    targetOffset >= minRoadAllowanceOffset &&
                    Math.Abs(outwardDistance - targetOffset) > maxTargetOffsetError)
                {
                    continue;
                }

                var segmentMinU = Math.Min(uA, uB);
                var segmentMaxU = Math.Max(uA, uB);
                var centerGap = DistanceToClosedInterval(frame.MidU, segmentMinU, segmentMaxU);
                var centerPenalty = GetQuarterSouthBoundaryCenterGapPenalty(centerGap);
                var dividerGap = DistanceToClosedInterval(preferredDividerU, segmentMinU, segmentMaxU);
                var dividerPenalty = GetQuarterDividerSpanPenalty(dividerGap);
                var score = Math.Abs(outwardDistance - targetOffset) + centerPenalty + dividerPenalty;
                var dividerLinkedCandidate = !requireDividerLinkedCandidate ||
                                             TryIntersectLocalInfiniteLineWithBoundedSegmentExtension(
                                                 frame,
                                                 dividerLineA,
                                                 dividerLineB,
                                                 segment.A,
                                                 segment.B,
                                                 maxDividerIntersectionExtension,
                                                 out _,
                                                 out _);
                if (score < bestScore ||
                    (Math.Abs(score - bestScore) <= 1e-6 && dividerGap < bestDividerGap) ||
                    (Math.Abs(score - bestScore) <= 1e-6 && centerGap < bestCenterGap) ||
                    (Math.Abs(score - bestScore) <= 1e-6 && priority < bestPriority) ||
                    (Math.Abs(score - bestScore) <= 1e-6 &&
                     Math.Abs(dividerGap - bestDividerGap) <= 1e-6 &&
                     Math.Abs(centerGap - bestCenterGap) <= 1e-6 &&
                     priority == bestPriority &&
                     outwardDistance < bestOutwardDistance))
                {
                    bestPriority = priority;
                    bestScore = score;
                    bestCenterGap = centerGap;
                    bestDividerGap = dividerGap;
                    bestOutwardDistance = outwardDistance;
                    bestBoundaryV = vAtMidU;
                    bestSourceLayer = segment.Layer;
                    bestBoundaryA = segment.A;
                    bestBoundaryB = segment.B;
                }

                if (!dividerLinkedCandidate)
                {
                    continue;
                }

                if (score < bestLinkedScore ||
                    (Math.Abs(score - bestLinkedScore) <= 1e-6 && dividerGap < bestLinkedDividerGap) ||
                    (Math.Abs(score - bestLinkedScore) <= 1e-6 && centerGap < bestLinkedCenterGap) ||
                    (Math.Abs(score - bestLinkedScore) <= 1e-6 && priority < bestLinkedPriority) ||
                    (Math.Abs(score - bestLinkedScore) <= 1e-6 &&
                     Math.Abs(dividerGap - bestLinkedDividerGap) <= 1e-6 &&
                     Math.Abs(centerGap - bestLinkedCenterGap) <= 1e-6 &&
                     priority == bestLinkedPriority &&
                     outwardDistance < bestLinkedOutwardDistance))
                {
                    bestLinkedPriority = priority;
                    bestLinkedScore = score;
                    bestLinkedCenterGap = centerGap;
                    bestLinkedDividerGap = dividerGap;
                    bestLinkedOutwardDistance = outwardDistance;
                    bestLinkedBoundaryV = vAtMidU;
                    bestLinkedSourceLayer = segment.Layer;
                    bestLinkedBoundaryA = segment.A;
                    bestLinkedBoundaryB = segment.B;
                }
            }

            if (requireDividerLinkedCandidate && bestLinkedPriority != int.MaxValue)
            {
                boundaryV = bestLinkedBoundaryV;
                sourceLayer = bestLinkedSourceLayer;
                boundarySegmentA = bestLinkedBoundaryA;
                boundarySegmentB = bestLinkedBoundaryB;
                return true;
            }

            if (bestPriority != int.MaxValue)
            {
                boundaryV = bestBoundaryV;
                sourceLayer = bestSourceLayer;
                boundarySegmentA = bestBoundaryA;
                boundarySegmentB = bestBoundaryB;
                return true;
            }

            return false;
        }

        private static bool TryResolvePreferredQuarterViewSouthBoundaryWithCorrection(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> southResolutionSegments,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> correctionSouthBoundarySegments,
            bool isBlindSouthBoundarySection,
            double southFallbackOffset,
            double preferredDividerU,
            Point2d dividerLineA,
            Point2d dividerLineB,
            out double resolvedV,
            out string resolvedLayer,
            out Point2d resolvedA,
            out Point2d resolvedB)
        {
            resolvedV = default;
            resolvedLayer = string.Empty;
            resolvedA = default;
            resolvedB = default;

            if (TryResolveQuarterViewSouthCorrectionBoundaryV(
                    frame,
                    correctionSouthBoundarySegments,
                    preferredDividerU,
                    dividerLineA,
                    dividerLineB,
                    out var correctionSouthV,
                    out var correctionSouthLayer,
                    out var correctionSouthA,
                    out var correctionSouthB))
            {
                resolvedV = correctionSouthV;
                resolvedLayer = correctionSouthLayer;
                resolvedA = correctionSouthA;
                resolvedB = correctionSouthB;
                return true;
            }

            var hasResolvedSouth = TryResolveQuarterViewSouthBoundaryV(
                frame,
                southResolutionSegments,
                southFallbackOffset,
                preferredDividerU,
                dividerLineA,
                dividerLineB,
                out resolvedV,
                out resolvedLayer,
                out resolvedA,
                out resolvedB);

            if (!hasResolvedSouth &&
                !isBlindSouthBoundarySection &&
                southFallbackOffset <= (RoadAllowanceSecWidthMeters + 0.5))
            {
                // If SEC-width matching misses, allow a USEC-width retry for mixed/tight geometry.
                hasResolvedSouth = TryResolveQuarterViewSouthBoundaryV(
                    frame,
                    southResolutionSegments,
                    RoadAllowanceUsecWidthMeters,
                    preferredDividerU,
                    dividerLineA,
                    dividerLineB,
                    out resolvedV,
                    out resolvedLayer,
                    out resolvedA,
                    out resolvedB);
            }

            if (!hasResolvedSouth &&
                !isBlindSouthBoundarySection &&
                southFallbackOffset >= (RoadAllowanceUsecWidthMeters - 0.5))
            {
                // Symmetric retry back to SEC-width when USEC-width detection is noisy.
                hasResolvedSouth = TryResolveQuarterViewSouthBoundaryV(
                    frame,
                    southResolutionSegments,
                    RoadAllowanceSecWidthMeters,
                    preferredDividerU,
                    dividerLineA,
                    dividerLineB,
                    out resolvedV,
                    out resolvedLayer,
                    out resolvedA,
                    out resolvedB);
            }

            return hasResolvedSouth;
        }

        private static bool TryResolvePreferredQuarterViewNorthBoundary(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> boundarySegments,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> correctionNorthBoundarySegments,
            double preferredDividerU,
            bool preferWestLinkedCandidate,
            Point2d westBoundarySegmentA,
            Point2d westBoundarySegmentB,
            out double resolvedV,
            out string resolvedLayer,
            out Point2d resolvedA,
            out Point2d resolvedB)
        {
            resolvedV = default;
            resolvedLayer = string.Empty;
            resolvedA = default;
            resolvedB = default;

            if (TryResolveQuarterViewNorthCorrectionBoundaryV(
                    frame,
                    correctionNorthBoundarySegments,
                    out var correctionNorthV,
                    out var correctionNorthLayer,
                    out var correctionNorthA,
                    out var correctionNorthB))
            {
                resolvedV = correctionNorthV;
                resolvedLayer = correctionNorthLayer;
                resolvedA = correctionNorthA;
                resolvedB = correctionNorthB;
                return true;
            }

            return TryResolveQuarterViewNorthBoundaryV(
                frame,
                boundarySegments,
                preferredDividerU,
                preferWestLinkedCandidate,
                westBoundarySegmentA,
                westBoundarySegmentB,
                out resolvedV,
                out resolvedLayer,
                out resolvedA,
                out resolvedB);
        }

        private static bool TryResolveQuarterViewNorthBoundaryV(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> segments,
            double preferredDividerU,
            bool preferWestLinkedCandidate,
            Point2d westBoundarySegmentA,
            Point2d westBoundarySegmentB,
            out double boundaryV,
            out string sourceLayer,
            out Point2d boundarySegmentA,
            out Point2d boundarySegmentB)
        {
            boundaryV = default;
            sourceLayer = string.Empty;
            boundarySegmentA = default;
            boundarySegmentB = default;

            const double axisTolerance = 0.5;
            const double overlapPadding = 16.0;
            const double minProjectedOverlap = 20.0;
            const double maxOffset = 12.0;
            const double maxWestIntersectionExtension = 80.0;
            const double minWestOffset = -6.0;
            const double maxWestOffset = 90.0;
            const double minNorthOffset = -6.0;
            const double maxNorthOffset = 90.0;
            var requireWestLinkedCandidate = preferWestLinkedCandidate &&
                                             westBoundarySegmentA.GetDistanceTo(westBoundarySegmentB) > 1e-6;

            var bestPriority = int.MaxValue;
            var bestScore = double.MaxValue;
            var bestCenterGap = double.MaxValue;
            var bestDividerGap = double.MaxValue;
            var bestOutwardDistance = double.MaxValue;
            var bestBoundaryV = default(double);
            var bestSourceLayer = string.Empty;
            var bestBoundaryA = default(Point2d);
            var bestBoundaryB = default(Point2d);

            var bestLinkedPriority = int.MaxValue;
            var bestLinkedScore = double.MaxValue;
            var bestLinkedCenterGap = double.MaxValue;
            var bestLinkedDividerGap = double.MaxValue;
            var bestLinkedOutwardDistance = double.MaxValue;
            var bestLinkedBoundaryV = default(double);
            var bestLinkedSourceLayer = string.Empty;
            var bestLinkedBoundaryA = default(Point2d);
            var bestLinkedBoundaryB = default(Point2d);
            foreach (var segment in segments)
            {
                if (string.Equals(segment.Layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var delta = segment.B - segment.A;
                var eastComp = Math.Abs(delta.DotProduct(frame.EastUnit));
                var northComp = Math.Abs(delta.DotProduct(frame.NorthUnit));
                if (eastComp <= northComp)
                {
                    continue;
                }

                var relA = segment.A - frame.Origin;
                var relB = segment.B - frame.Origin;
                var uA = relA.DotProduct(frame.EastUnit);
                var uB = relB.DotProduct(frame.EastUnit);
                var vA = relA.DotProduct(frame.NorthUnit);
                var vB = relB.DotProduct(frame.NorthUnit);
                var overlap = Math.Min(Math.Max(uA, uB), frame.EastEdgeU + overlapPadding) -
                              Math.Max(Math.Min(uA, uB), frame.WestEdgeU - overlapPadding);
                if (overlap < minProjectedOverlap)
                {
                    continue;
                }

                var vLine = 0.5 * (vA + vB);
                var vAtMidU = vLine;
                var du = uB - uA;
                if (Math.Abs(du) > 1e-6)
                {
                    var tMid = (frame.MidU - uA) / du;
                    if (tMid >= -0.35 && tMid <= 1.35)
                    {
                        vAtMidU = vA + ((vB - vA) * tMid);
                    }
                }

                var outwardDistance = vAtMidU - frame.NorthEdgeV;
                if (outwardDistance < -axisTolerance || outwardDistance > maxOffset)
                {
                    continue;
                }

                var priority = GetQuarterViewBoundaryLayerPriority(segment.Layer);
                var segmentMinU = Math.Min(uA, uB);
                var segmentMaxU = Math.Max(uA, uB);
                var centerGap = DistanceToClosedInterval(frame.MidU, segmentMinU, segmentMaxU);
                var centerPenalty = GetQuarterSouthBoundaryCenterGapPenalty(centerGap);
                var dividerGap = DistanceToClosedInterval(preferredDividerU, segmentMinU, segmentMaxU);
                var dividerPenalty = GetQuarterDividerSpanPenalty(dividerGap);
                var score = Math.Abs(outwardDistance) + centerPenalty + dividerPenalty;
                var westLinkedCandidate = !requireWestLinkedCandidate;
                if (requireWestLinkedCandidate &&
                    TryIntersectLocalInfiniteLineWithBoundedSegmentExtension(
                        frame,
                        westBoundarySegmentA,
                        westBoundarySegmentB,
                        segment.A,
                        segment.B,
                        maxWestIntersectionExtension,
                        out var westIntersectionU,
                        out var westIntersectionV))
                {
                    var westOffset = frame.WestEdgeU - westIntersectionU;
                    var northOffset = westIntersectionV - frame.NorthEdgeV;
                    westLinkedCandidate = westOffset >= minWestOffset &&
                                          westOffset <= maxWestOffset &&
                                          northOffset >= minNorthOffset &&
                                          northOffset <= maxNorthOffset;
                }
                if (score < bestScore ||
                    (Math.Abs(score - bestScore) <= 1e-6 && dividerGap < bestDividerGap) ||
                    (Math.Abs(score - bestScore) <= 1e-6 && centerGap < bestCenterGap) ||
                    (Math.Abs(score - bestScore) <= 1e-6 && priority < bestPriority) ||
                    (Math.Abs(score - bestScore) <= 1e-6 &&
                     Math.Abs(dividerGap - bestDividerGap) <= 1e-6 &&
                     Math.Abs(centerGap - bestCenterGap) <= 1e-6 &&
                     priority == bestPriority &&
                     outwardDistance < bestOutwardDistance))
                {
                    bestPriority = priority;
                    bestScore = score;
                    bestCenterGap = centerGap;
                    bestDividerGap = dividerGap;
                    bestOutwardDistance = outwardDistance;
                    bestBoundaryV = vAtMidU;
                    bestSourceLayer = segment.Layer;
                    bestBoundaryA = segment.A;
                    bestBoundaryB = segment.B;
                }

                if (!westLinkedCandidate)
                {
                    continue;
                }

                if (score < bestLinkedScore ||
                    (Math.Abs(score - bestLinkedScore) <= 1e-6 && dividerGap < bestLinkedDividerGap) ||
                    (Math.Abs(score - bestLinkedScore) <= 1e-6 && centerGap < bestLinkedCenterGap) ||
                    (Math.Abs(score - bestLinkedScore) <= 1e-6 && priority < bestLinkedPriority) ||
                    (Math.Abs(score - bestLinkedScore) <= 1e-6 &&
                     Math.Abs(dividerGap - bestLinkedDividerGap) <= 1e-6 &&
                     Math.Abs(centerGap - bestLinkedCenterGap) <= 1e-6 &&
                     priority == bestLinkedPriority &&
                     outwardDistance < bestLinkedOutwardDistance))
                {
                    bestLinkedPriority = priority;
                    bestLinkedScore = score;
                    bestLinkedCenterGap = centerGap;
                    bestLinkedDividerGap = dividerGap;
                    bestLinkedOutwardDistance = outwardDistance;
                    bestLinkedBoundaryV = vAtMidU;
                    bestLinkedSourceLayer = segment.Layer;
                    bestLinkedBoundaryA = segment.A;
                    bestLinkedBoundaryB = segment.B;
                }
            }

            if (requireWestLinkedCandidate && bestLinkedPriority != int.MaxValue)
            {
                boundaryV = bestLinkedBoundaryV;
                sourceLayer = bestLinkedSourceLayer;
                boundarySegmentA = bestLinkedBoundaryA;
                boundarySegmentB = bestLinkedBoundaryB;
                return true;
            }

            if (bestPriority != int.MaxValue)
            {
                boundaryV = bestBoundaryV;
                sourceLayer = bestSourceLayer;
                boundarySegmentA = bestBoundaryA;
                boundarySegmentB = bestBoundaryB;
                return true;
            }

            return false;
        }

        private static bool TryResolveQuarterViewNorthCorrectionBoundaryV(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> correctionSegments,
            out double boundaryV,
            out string sourceLayer,
            out Point2d segmentA,
            out Point2d segmentB)
        {
            boundaryV = default;
            sourceLayer = string.Empty;
            segmentA = default;
            segmentB = default;
            if (correctionSegments == null || correctionSegments.Count == 0)
            {
                return false;
            }

            const double overlapPadding = 16.0;
            const double minProjectedOverlap = 20.0;
            const double minOffset = -0.75;
            const double maxOffset = 15.0;

            var found = false;
            var bestPriority = int.MaxValue;
            var bestScore = double.MaxValue;
            var bestOutwardDistance = double.MaxValue;
            for (var i = 0; i < correctionSegments.Count; i++)
            {
                var seg = correctionSegments[i];
                var layer = seg.Layer ?? string.Empty;
                var isCorrectionLine = string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase);
                var isCorrectionZero = string.Equals(layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase);
                if (!isCorrectionLine && !isCorrectionZero)
                {
                    continue;
                }

                var delta = seg.B - seg.A;
                var eastComp = Math.Abs(delta.DotProduct(frame.EastUnit));
                var northComp = Math.Abs(delta.DotProduct(frame.NorthUnit));
                if (eastComp <= northComp)
                {
                    continue;
                }

                var relA = seg.A - frame.Origin;
                var relB = seg.B - frame.Origin;
                var uA = relA.DotProduct(frame.EastUnit);
                var uB = relB.DotProduct(frame.EastUnit);
                var vA = relA.DotProduct(frame.NorthUnit);
                var vB = relB.DotProduct(frame.NorthUnit);
                var overlap = Math.Min(Math.Max(uA, uB), frame.EastEdgeU + overlapPadding) -
                              Math.Max(Math.Min(uA, uB), frame.WestEdgeU - overlapPadding);
                if (overlap < minProjectedOverlap)
                {
                    continue;
                }

                var vLine = 0.5 * (vA + vB);
                var vAtMidU = vLine;
                var du = uB - uA;
                if (Math.Abs(du) > 1e-6)
                {
                    var tMid = (frame.MidU - uA) / du;
                    if (tMid >= -0.35 && tMid <= 1.35)
                    {
                        vAtMidU = vA + ((vB - vA) * tMid);
                    }
                }

                var outwardDistance = vAtMidU - frame.NorthEdgeV;
                if (outwardDistance < minOffset || outwardDistance > maxOffset)
                {
                    continue;
                }

                var priority = isCorrectionLine ? 0 : 1;
                var targetOffset = isCorrectionLine ? 0.0 : CorrectionLineInsetMeters;
                var score = Math.Abs(outwardDistance - targetOffset);
                if (!found ||
                    score < bestScore ||
                    (Math.Abs(score - bestScore) <= 1e-6 && priority < bestPriority) ||
                    (Math.Abs(score - bestScore) <= 1e-6 && priority == bestPriority && outwardDistance < bestOutwardDistance))
                {
                    found = true;
                    bestPriority = priority;
                    bestScore = score;
                    bestOutwardDistance = outwardDistance;
                    boundaryV = vAtMidU;
                    sourceLayer = layer;
                    segmentA = seg.A;
                    segmentB = seg.B;
                }
            }

            return found;
        }

        private static bool TryResolveQuarterViewSouthCorrectionBoundaryV(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> correctionSegments,
            double preferredDividerU,
            Point2d dividerLineA,
            Point2d dividerLineB,
            out double boundaryV,
            out string sourceLayer,
            out Point2d segmentA,
            out Point2d segmentB)
        {
            boundaryV = default;
            sourceLayer = string.Empty;
            segmentA = default;
            segmentB = default;
            if (correctionSegments == null || correctionSegments.Count == 0)
            {
                return false;
            }

            const double overlapPadding = 16.0;
            const double minProjectedOverlap = 20.0;
            const double minOffset = 0.5;
            const double maxOffset = 40.0;
            const double maxDividerIntersectionExtension = 80.0;
            const double preferSouthDefinitionThreshold = 12.0;
            var maxUnlinkedDividerGap = RoadAllowanceSecWidthMeters * 0.6;
            var frameSpan = Math.Max(frame.EastEdgeU - frame.WestEdgeU, minProjectedOverlap);
            var foundHard = false;
            var bestHardScore = double.MaxValue;
            var bestHardTargetError = double.MaxValue;
            var bestHardCenterGap = double.MaxValue;
            var bestHardDividerGap = double.MaxValue;
            var bestHardOutwardDistance = double.MaxValue;
            var bestHardBoundaryV = default(double);
            var bestHardSourceLayer = string.Empty;
            var bestHardSegmentA = default(Point2d);
            var bestHardSegmentB = default(Point2d);
            var foundHardLinked = false;
            var bestHardLinkedScore = double.MaxValue;
            var bestHardLinkedTargetError = double.MaxValue;
            var bestHardLinkedCenterGap = double.MaxValue;
            var bestHardLinkedDividerGap = double.MaxValue;
            var bestHardLinkedOutwardDistance = double.MaxValue;
            var bestHardLinkedBoundaryV = default(double);
            var bestHardLinkedSourceLayer = string.Empty;
            var bestHardLinkedSegmentA = default(Point2d);
            var bestHardLinkedSegmentB = default(Point2d);
            var foundInset = false;
            var bestInsetScore = double.MaxValue;
            var bestInsetTargetError = double.MaxValue;
            var bestInsetCenterGap = double.MaxValue;
            var bestInsetDividerGap = double.MaxValue;
            var bestInsetOutwardDistance = double.MaxValue;
            var bestInsetBoundaryV = default(double);
            var bestInsetSourceLayer = string.Empty;
            var bestInsetSegmentA = default(Point2d);
            var bestInsetSegmentB = default(Point2d);
            var foundInsetLinked = false;
            var bestInsetLinkedScore = double.MaxValue;
            var bestInsetLinkedTargetError = double.MaxValue;
            var bestInsetLinkedCenterGap = double.MaxValue;
            var bestInsetLinkedDividerGap = double.MaxValue;
            var bestInsetLinkedOutwardDistance = double.MaxValue;
            var bestInsetLinkedBoundaryV = default(double);
            var bestInsetLinkedSourceLayer = string.Empty;
            var bestInsetLinkedSegmentA = default(Point2d);
            var bestInsetLinkedSegmentB = default(Point2d);

            static bool IsBetterCorrectionSouthCandidate(
                int layerPriority,
                int bestLayerPriority,
                double score,
                double bestScore,
                double dividerGap,
                double bestDividerGap,
                double centerGap,
                double bestCenterGap,
                double targetError,
                double bestTargetError,
                double outwardDistance,
                double bestOutwardDistance)
            {
                return layerPriority < bestLayerPriority ||
                       (layerPriority == bestLayerPriority && score < bestScore) ||
                       (layerPriority == bestLayerPriority &&
                        Math.Abs(score - bestScore) <= 1e-6 &&
                        dividerGap < bestDividerGap) ||
                       (layerPriority == bestLayerPriority &&
                        Math.Abs(score - bestScore) <= 1e-6 &&
                        centerGap < bestCenterGap) ||
                       (layerPriority == bestLayerPriority &&
                        Math.Abs(score - bestScore) <= 1e-6 &&
                        targetError < bestTargetError) ||
                       (layerPriority == bestLayerPriority &&
                        Math.Abs(score - bestScore) <= 1e-6 &&
                        Math.Abs(dividerGap - bestDividerGap) <= 1e-6 &&
                        Math.Abs(centerGap - bestCenterGap) <= 1e-6 &&
                        Math.Abs(targetError - bestTargetError) <= 1e-6 &&
                        outwardDistance < bestOutwardDistance);
            }
            for (var i = 0; i < correctionSegments.Count; i++)
            {
                var seg = correctionSegments[i];
                var layer = seg.Layer ?? string.Empty;
                var delta = seg.B - seg.A;
                var eastComp = Math.Abs(delta.DotProduct(frame.EastUnit));
                var northComp = Math.Abs(delta.DotProduct(frame.NorthUnit));
                if (eastComp <= northComp)
                {
                    continue;
                }

                var relA = seg.A - frame.Origin;
                var relB = seg.B - frame.Origin;
                var uA = relA.DotProduct(frame.EastUnit);
                var uB = relB.DotProduct(frame.EastUnit);
                var vA = relA.DotProduct(frame.NorthUnit);
                var vB = relB.DotProduct(frame.NorthUnit);
                var overlap = Math.Min(Math.Max(uA, uB), frame.EastEdgeU + overlapPadding) -
                              Math.Max(Math.Min(uA, uB), frame.WestEdgeU - overlapPadding);
                if (overlap < minProjectedOverlap)
                {
                    continue;
                }

                var vLine = 0.5 * (vA + vB);
                var vAtMidU = vLine;
                var vAtWestU = vLine;
                var vAtEastU = vLine;
                var hasWestProjection = false;
                var hasEastProjection = false;
                var du = uB - uA;
                if (Math.Abs(du) > 1e-6)
                {
                    var tMid = (frame.MidU - uA) / du;
                    if (tMid >= -0.35 && tMid <= 1.35)
                    {
                        vAtMidU = vA + ((vB - vA) * tMid);
                    }

                    var tWest = (frame.WestEdgeU - uA) / du;
                    if (tWest >= -0.35 && tWest <= 1.35)
                    {
                        vAtWestU = vA + ((vB - vA) * tWest);
                        hasWestProjection = true;
                    }

                    var tEast = (frame.EastEdgeU - uA) / du;
                    if (tEast >= -0.35 && tEast <= 1.35)
                    {
                        vAtEastU = vA + ((vB - vA) * tEast);
                        hasEastProjection = true;
                    }
                }

                var vForScore = hasWestProjection && hasEastProjection
                    ? 0.5 * (vAtWestU + vAtEastU)
                    : vAtMidU;
                var outwardDistance = frame.SouthEdgeV - vForScore;
                if (outwardDistance < minOffset || outwardDistance > maxOffset)
                {
                    continue;
                }

                var segmentMinU = Math.Min(uA, uB);
                var segmentMaxU = Math.Max(uA, uB);
                var centerGap = DistanceToClosedInterval(frame.MidU, segmentMinU, segmentMaxU);
                var centerPenalty = GetQuarterSouthBoundaryCenterGapPenalty(centerGap);
                var dividerGap = DistanceToClosedInterval(preferredDividerU, segmentMinU, segmentMaxU);
                var dividerPenalty = GetQuarterDividerSpanPenalty(dividerGap);
                var dividerLinkedCandidate = TryIntersectLocalInfiniteLineWithBoundedSegmentExtension(
                    frame,
                    dividerLineA,
                    dividerLineB,
                    seg.A,
                    seg.B,
                    maxDividerIntersectionExtension,
                    out _,
                    out _);
                if (!TryClassifyQuarterSouthCorrectionCandidate(
                        layer,
                        outwardDistance,
                        preferSouthDefinitionThreshold,
                        out var prefersInset,
                        out var targetOffset))
                {
                    continue;
                }
                if (!prefersInset &&
                    !dividerLinkedCandidate &&
                    !CorrectionSouthBoundaryPreference.IsHardBoundaryCoverageAcceptable(overlap, frameSpan))
                {
                    continue;
                }

                var candidateA = seg.A;
                var candidateB = seg.B;
                var candidateOutwardDistance = outwardDistance;
                var candidateBoundaryV = frame.SouthEdgeV - candidateOutwardDistance;
                var candidatePoint = new Point2d(
                    frame.Origin.X + (frame.EastUnit.X * preferredDividerU) + (frame.NorthUnit.X * candidateBoundaryV),
                    frame.Origin.Y + (frame.EastUnit.Y * preferredDividerU) + (frame.NorthUnit.Y * candidateBoundaryV));
                var targetError = Math.Abs(candidateOutwardDistance - targetOffset);
                var correctionCompanionPenalty = string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase)
                    ? GetQuarterSouthCorrectionCompanionGapPenalty(frame, correctionSegments, preferredDividerU, candidatePoint)
                    : 0.0;
                var score = correctionCompanionPenalty + targetError + centerPenalty + dividerPenalty;
                var layerPriority = GetQuarterSouthCorrectionLayerPriority(layer);
                dividerLinkedCandidate = TryIntersectLocalInfiniteLineWithBoundedSegmentExtension(
                    frame,
                    dividerLineA,
                    dividerLineB,
                    candidateA,
                    candidateB,
                    maxDividerIntersectionExtension,
                    out _,
                    out _);

                if (!prefersInset)
                {
                    if (!foundHard ||
                        IsBetterCorrectionSouthCandidate(
                            layerPriority,
                            GetQuarterSouthCorrectionLayerPriority(bestHardSourceLayer),
                            score,
                            bestHardScore,
                            dividerGap,
                            bestHardDividerGap,
                            centerGap,
                            bestHardCenterGap,
                            targetError,
                            bestHardTargetError,
                            candidateOutwardDistance,
                            bestHardOutwardDistance))
                    {
                        foundHard = true;
                        bestHardScore = score;
                        bestHardTargetError = targetError;
                        bestHardCenterGap = centerGap;
                        bestHardDividerGap = dividerGap;
                        bestHardOutwardDistance = candidateOutwardDistance;
                        bestHardBoundaryV = candidateBoundaryV;
                        bestHardSourceLayer = layer;
                        bestHardSegmentA = candidateA;
                        bestHardSegmentB = candidateB;
                    }

                    if (dividerLinkedCandidate &&
                        (!foundHardLinked ||
                         IsBetterCorrectionSouthCandidate(
                             layerPriority,
                             GetQuarterSouthCorrectionLayerPriority(bestHardLinkedSourceLayer),
                             score,
                             bestHardLinkedScore,
                             dividerGap,
                             bestHardLinkedDividerGap,
                             centerGap,
                             bestHardLinkedCenterGap,
                             targetError,
                             bestHardLinkedTargetError,
                             candidateOutwardDistance,
                             bestHardLinkedOutwardDistance)))
                    {
                        foundHardLinked = true;
                        bestHardLinkedScore = score;
                        bestHardLinkedTargetError = targetError;
                        bestHardLinkedCenterGap = centerGap;
                        bestHardLinkedDividerGap = dividerGap;
                        bestHardLinkedOutwardDistance = candidateOutwardDistance;
                        bestHardLinkedBoundaryV = candidateBoundaryV;
                        bestHardLinkedSourceLayer = layer;
                        bestHardLinkedSegmentA = candidateA;
                        bestHardLinkedSegmentB = candidateB;
                    }

                    continue;
                }

                if (!foundInset ||
                    IsBetterCorrectionSouthCandidate(
                        layerPriority,
                        GetQuarterSouthCorrectionLayerPriority(bestInsetSourceLayer),
                        score,
                        bestInsetScore,
                        dividerGap,
                        bestInsetDividerGap,
                        centerGap,
                        bestInsetCenterGap,
                        targetError,
                        bestInsetTargetError,
                        candidateOutwardDistance,
                        bestInsetOutwardDistance))
                {
                    foundInset = true;
                    bestInsetScore = score;
                    bestInsetTargetError = targetError;
                    bestInsetCenterGap = centerGap;
                    bestInsetDividerGap = dividerGap;
                    bestInsetOutwardDistance = candidateOutwardDistance;
                    bestInsetBoundaryV = candidateBoundaryV;
                    bestInsetSourceLayer = layer;
                    bestInsetSegmentA = candidateA;
                    bestInsetSegmentB = candidateB;
                }

                if (dividerLinkedCandidate &&
                    (!foundInsetLinked ||
                     IsBetterCorrectionSouthCandidate(
                         layerPriority,
                         GetQuarterSouthCorrectionLayerPriority(bestInsetLinkedSourceLayer),
                         score,
                         bestInsetLinkedScore,
                         dividerGap,
                         bestInsetLinkedDividerGap,
                         centerGap,
                         bestInsetLinkedCenterGap,
                         targetError,
                         bestInsetLinkedTargetError,
                         candidateOutwardDistance,
                         bestInsetLinkedOutwardDistance)))
                {
                    foundInsetLinked = true;
                    bestInsetLinkedScore = score;
                    bestInsetLinkedTargetError = targetError;
                    bestInsetLinkedCenterGap = centerGap;
                    bestInsetLinkedDividerGap = dividerGap;
                    bestInsetLinkedOutwardDistance = candidateOutwardDistance;
                    bestInsetLinkedBoundaryV = candidateBoundaryV;
                    bestInsetLinkedSourceLayer = layer;
                    bestInsetLinkedSegmentA = candidateA;
                    bestInsetLinkedSegmentB = candidateB;
                }
            }

            if (foundHardLinked)
            {
                if (foundInsetLinked &&
                    string.Equals(bestHardLinkedSourceLayer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(bestInsetLinkedSourceLayer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
                {
                    boundaryV = bestInsetLinkedBoundaryV;
                    sourceLayer = bestInsetLinkedSourceLayer;
                    segmentA = bestInsetLinkedSegmentA;
                    segmentB = bestInsetLinkedSegmentB;
                    return true;
                }

                boundaryV = bestHardLinkedBoundaryV;
                sourceLayer = bestHardLinkedSourceLayer;
                segmentA = bestHardLinkedSegmentA;
                segmentB = bestHardLinkedSegmentB;
                return true;
            }

            if (foundHard &&
                CorrectionSouthBoundaryPreference.IsUnlinkedDividerGapAcceptable(
                    bestHardDividerGap,
                    maxUnlinkedDividerGap))
            {
                if (foundInset &&
                    string.Equals(bestHardSourceLayer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(bestInsetSourceLayer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
                {
                    boundaryV = bestInsetBoundaryV;
                    sourceLayer = bestInsetSourceLayer;
                    segmentA = bestInsetSegmentA;
                    segmentB = bestInsetSegmentB;
                    return true;
                }

                // Quarter definitions on correction lines should include the road allowance when
                // a usable hard south boundary exists; inset candidates are fallback-only.
                boundaryV = bestHardBoundaryV;
                sourceLayer = bestHardSourceLayer;
                segmentA = bestHardSegmentA;
                segmentB = bestHardSegmentB;
                return true;
            }

            if (foundInsetLinked)
            {
                boundaryV = bestInsetLinkedBoundaryV;
                sourceLayer = bestInsetLinkedSourceLayer;
                segmentA = bestInsetLinkedSegmentA;
                segmentB = bestInsetLinkedSegmentB;
                return true;
            }

            if (foundInset &&
                CorrectionSouthBoundaryPreference.IsUnlinkedDividerGapAcceptable(
                    bestInsetDividerGap,
                    maxUnlinkedDividerGap))
            {
                boundaryV = bestInsetBoundaryV;
                sourceLayer = bestInsetSourceLayer;
                segmentA = bestInsetSegmentA;
                segmentB = bestInsetSegmentB;
                return true;
            }

            return false;
        }

        private static bool TryResolveQuarterViewEastBoundarySegmentOnLayer(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> segments,
            string targetLayer,
            double expectedInsetMeters,
            out Point2d segmentA,
            out Point2d segmentB)
        {
            segmentA = default;
            segmentB = default;
            if (segments == null || segments.Count == 0 || string.IsNullOrWhiteSpace(targetLayer))
            {
                return false;
            }

            const double axisTolerance = 0.5;
            const double overlapPadding = 16.0;
            const double minProjectedOverlap = 20.0;
            const double maxOffset = 60.0;
            const double minRoadAllowanceOffset = 5.0;
            const double maxTargetOffsetError = 6.0;
            var found = false;
            var bestScore = double.MaxValue;
            for (var i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                if (!string.Equals(seg.Layer, targetLayer, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var delta = seg.B - seg.A;
                var eastComp = Math.Abs(delta.DotProduct(frame.EastUnit));
                var northComp = Math.Abs(delta.DotProduct(frame.NorthUnit));
                if (northComp <= eastComp)
                {
                    continue;
                }

                var relA = seg.A - frame.Origin;
                var relB = seg.B - frame.Origin;
                var uA = relA.DotProduct(frame.EastUnit);
                var uB = relB.DotProduct(frame.EastUnit);
                var vA = relA.DotProduct(frame.NorthUnit);
                var vB = relB.DotProduct(frame.NorthUnit);
                var overlap = Math.Min(Math.Max(vA, vB), frame.NorthEdgeV + overlapPadding) -
                              Math.Max(Math.Min(vA, vB), frame.SouthEdgeV - overlapPadding);
                if (overlap < minProjectedOverlap)
                {
                    continue;
                }

                var uAtMidV = 0.5 * (uA + uB);
                var dv = vB - vA;
                if (Math.Abs(dv) > 1e-6)
                {
                    var tMid = (frame.MidV - vA) / dv;
                    if (tMid >= -0.35 && tMid <= 1.35)
                    {
                        uAtMidV = uA + ((uB - uA) * tMid);
                    }
                }

                // Positive inward distance means the candidate lies west of the east edge.
                var inwardDistance = frame.EastEdgeU - uAtMidV;
                if (inwardDistance < -axisTolerance || inwardDistance > maxOffset)
                {
                    continue;
                }

                var targetOffset = Math.Max(0.0, expectedInsetMeters);
                if (targetOffset >= minRoadAllowanceOffset &&
                    inwardDistance < minRoadAllowanceOffset)
                {
                    continue;
                }

                if (targetOffset >= minRoadAllowanceOffset &&
                    Math.Abs(inwardDistance - targetOffset) > maxTargetOffsetError)
                {
                    continue;
                }

                var score = Math.Abs(inwardDistance - targetOffset);
                if (!found || score < bestScore)
                {
                    found = true;
                    bestScore = score;
                    segmentA = seg.A;
                    segmentB = seg.B;
                }
            }

            return found;
        }

        private static bool TryResolveQuarterViewBlindEastBoundaryFromNorthSouth(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> segments,
            double fallbackEastBoundaryU,
            Point2d southBoundarySegmentA,
            Point2d southBoundarySegmentB,
            Point2d northBoundarySegmentA,
            Point2d northBoundarySegmentB,
            out Point2d segmentA,
            out Point2d segmentB,
            out string sourceLayer,
            out double resolvedEastMidU,
            out double eastOffset,
            out double southOffset,
            out double northOffset)
        {
            segmentA = default;
            segmentB = default;
            sourceLayer = string.Empty;
            resolvedEastMidU = fallbackEastBoundaryU;
            eastOffset = default;
            southOffset = default;
            northOffset = default;

            if (!TryResolveQuarterViewEastBoundarySegmentFromNorthSouth(
                    frame,
                    segments,
                    southBoundarySegmentA,
                    southBoundarySegmentB,
                    northBoundarySegmentA,
                    northBoundarySegmentB,
                    out segmentA,
                    out segmentB,
                    out sourceLayer,
                    out eastOffset,
                    out southOffset,
                    out northOffset))
            {
                return false;
            }

            if (TryProjectBoundarySegmentUAtV(
                    frame,
                    segmentA,
                    segmentB,
                    frame.MidV,
                    out var projectedEastMidU))
            {
                resolvedEastMidU = projectedEastMidU;
            }
            else if (TryConvertQuarterWorldToLocal(frame, segmentA, out var eastAu, out _) &&
                     TryConvertQuarterWorldToLocal(frame, segmentB, out var eastBu, out _))
            {
                resolvedEastMidU = 0.5 * (eastAu + eastBu);
            }

            return true;
        }

        private static bool TryResolveQuarterViewEastBoundarySegmentFromNorthSouth(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> segments,
            Point2d southBoundarySegmentA,
            Point2d southBoundarySegmentB,
            Point2d northBoundarySegmentA,
            Point2d northBoundarySegmentB,
            out Point2d segmentA,
            out Point2d segmentB,
            out string sourceLayer,
            out double eastOffset,
            out double southOffset,
            out double northOffset)
        {
            segmentA = default;
            segmentB = default;
            sourceLayer = string.Empty;
            eastOffset = default;
            southOffset = default;
            northOffset = default;
            if (segments == null || segments.Count == 0)
            {
                return false;
            }

            const double overlapPadding = 16.0;
            const double minProjectedOverlap = 20.0;
            static bool TryGetEastLayerPriority(string layer, out int priority)
            {
                priority = int.MaxValue;
                if (string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase))
                {
                    priority = 0;
                    return true;
                }

                if (string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase))
                {
                    priority = 1;
                    return true;
                }

                if (string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase))
                {
                    priority = 2;
                    return true;
                }

                if (string.Equals(layer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase))
                {
                    priority = 3;
                    return true;
                }

                if (string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase))
                {
                    priority = 4;
                    return true;
                }

                if (string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase))
                {
                    priority = 5;
                    return true;
                }

                return false;
            }

            var found = false;
            var bestScore = double.MaxValue;
            var bestMagnitude = double.MaxValue;
            for (var i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                if (!TryGetEastLayerPriority(seg.Layer, out var layerPriority))
                {
                    continue;
                }

                var delta = seg.B - seg.A;
                var eastComp = Math.Abs(delta.DotProduct(frame.EastUnit));
                var northComp = Math.Abs(delta.DotProduct(frame.NorthUnit));
                if (northComp <= eastComp)
                {
                    continue;
                }

                if (!TryConvertQuarterWorldToLocal(frame, seg.A, out var uA, out var vA) ||
                    !TryConvertQuarterWorldToLocal(frame, seg.B, out var uB, out var vB))
                {
                    continue;
                }

                var overlap = Math.Min(Math.Max(vA, vB), frame.NorthEdgeV + overlapPadding) -
                              Math.Max(Math.Min(vA, vB), frame.SouthEdgeV - overlapPadding);
                if (overlap < minProjectedOverlap)
                {
                    continue;
                }

                var uAtMidV = 0.5 * (uA + uB);
                if (TryProjectBoundarySegmentUAtV(frame, seg.A, seg.B, frame.MidV, out var projectedMidU))
                {
                    uAtMidV = projectedMidU;
                }

                var candidateEastOffset = uAtMidV - frame.EastEdgeU;

                if (!TryIntersectLocalInfiniteLines(
                        frame,
                        seg.A,
                        seg.B,
                        southBoundarySegmentA,
                        southBoundarySegmentB,
                        out _,
                        out var southV))
                {
                    continue;
                }

                if (!TryIntersectLocalInfiniteLines(
                        frame,
                        seg.A,
                        seg.B,
                        northBoundarySegmentA,
                        northBoundarySegmentB,
                        out _,
                        out var northV))
                {
                    continue;
                }

                var candidateSouthOffset = frame.SouthEdgeV - southV;
                var candidateNorthOffset = northV - frame.NorthEdgeV;
                var magnitude = Math.Abs(candidateEastOffset) +
                                Math.Abs(candidateSouthOffset) +
                                Math.Abs(candidateNorthOffset);
                // Geometry compatibility with the resolved north/south boundaries must dominate
                // east-boundary source preference; otherwise a poor L-USEC-0 candidate can beat
                // a near-perfect base/section candidate by layer order alone.
                var score = (layerPriority * 25.0) +
                            (Math.Abs(candidateEastOffset) * 1.0) +
                            (Math.Abs(candidateSouthOffset) * 2.0) +
                            (Math.Abs(candidateNorthOffset) * 4.0);
                if (!found ||
                    score < bestScore ||
                    (Math.Abs(score - bestScore) <= 1e-6 && magnitude < bestMagnitude))
                {
                    found = true;
                    bestScore = score;
                    bestMagnitude = magnitude;
                    segmentA = seg.A;
                    segmentB = seg.B;
                    sourceLayer = seg.Layer;
                    eastOffset = candidateEastOffset;
                    southOffset = candidateSouthOffset;
                    northOffset = candidateNorthOffset;
                }
            }

            return found;
        }

        private static bool TryResolveQuarterViewSouthMostCorrectionBoundarySegment(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> correctionSegments,
            out string sourceLayer,
            out Point2d segmentA,
            out Point2d segmentB)
        {
            sourceLayer = string.Empty;
            segmentA = default;
            segmentB = default;
            if (correctionSegments == null || correctionSegments.Count == 0)
            {
                return false;
            }

            const double overlapPadding = 16.0;
            const double minProjectedOverlap = 20.0;
            const double minOffset = 0.5;
            const double maxOffset = 40.0;
            var frameSpan = Math.Max(frame.EastEdgeU - frame.WestEdgeU, minProjectedOverlap);
            var foundInset = false;
            var bestInsetScore = double.MaxValue;
            var bestInsetOutwardDistance = double.MaxValue;
            var bestInsetLayer = string.Empty;
            var insetSegmentA = default(Point2d);
            var insetSegmentB = default(Point2d);
            var foundHard = false;
            var bestHardScore = double.MaxValue;
            var bestHardOffsetError = double.MaxValue;
            var bestHardOutwardDistance = double.MinValue;
            var bestHardLayer = string.Empty;
            for (var i = 0; i < correctionSegments.Count; i++)
            {
                var seg = correctionSegments[i];
                var layer = seg.Layer ?? string.Empty;
                var delta = seg.B - seg.A;
                var eastComp = Math.Abs(delta.DotProduct(frame.EastUnit));
                var northComp = Math.Abs(delta.DotProduct(frame.NorthUnit));
                if (eastComp <= northComp)
                {
                    continue;
                }

                var relA = seg.A - frame.Origin;
                var relB = seg.B - frame.Origin;
                var uA = relA.DotProduct(frame.EastUnit);
                var uB = relB.DotProduct(frame.EastUnit);
                var vA = relA.DotProduct(frame.NorthUnit);
                var vB = relB.DotProduct(frame.NorthUnit);
                var overlap = Math.Min(Math.Max(uA, uB), frame.EastEdgeU + overlapPadding) -
                              Math.Max(Math.Min(uA, uB), frame.WestEdgeU - overlapPadding);
                if (overlap < minProjectedOverlap)
                {
                    continue;
                }

                var vAtMidU = 0.5 * (vA + vB);
                var du = uB - uA;
                if (Math.Abs(du) > 1e-6)
                {
                    var tMid = (frame.MidU - uA) / du;
                    if (tMid >= -0.35 && tMid <= 1.35)
                    {
                        vAtMidU = vA + ((vB - vA) * tMid);
                    }
                }

                var outwardDistance = frame.SouthEdgeV - vAtMidU;
                if (outwardDistance < minOffset || outwardDistance > maxOffset)
                {
                    continue;
                }

                var segmentMinU = Math.Min(uA, uB);
                var segmentMaxU = Math.Max(uA, uB);
                var centerGap = DistanceToClosedInterval(frame.MidU, segmentMinU, segmentMaxU);
                var centerPenalty = GetQuarterSouthBoundaryCenterGapPenalty(centerGap);

                var candidateA = seg.A;
                var candidateB = seg.B;
                var candidateOutwardDistance = outwardDistance;
                if (!TryClassifyQuarterSouthCorrectionCandidate(
                        layer,
                        outwardDistance,
                        preferSouthDefinitionThreshold: double.MaxValue,
                        out var prefersInset,
                        out var targetOffset))
                {
                    continue;
                }
                if (prefersInset)
                {
                    var score = Math.Abs(candidateOutwardDistance - targetOffset) + centerPenalty;
                    var layerPriority = GetQuarterSouthCorrectionLayerPriority(layer);
                    if (!foundInset ||
                        layerPriority < GetQuarterSouthCorrectionLayerPriority(bestInsetLayer) ||
                        (layerPriority == GetQuarterSouthCorrectionLayerPriority(bestInsetLayer) && score < bestInsetScore) ||
                        (layerPriority == GetQuarterSouthCorrectionLayerPriority(bestInsetLayer) &&
                         Math.Abs(score - bestInsetScore) <= 1e-6 &&
                         candidateOutwardDistance < bestInsetOutwardDistance))
                    {
                        foundInset = true;
                        bestInsetScore = score;
                        bestInsetOutwardDistance = candidateOutwardDistance;
                        bestInsetLayer = layer;
                        insetSegmentA = candidateA;
                        insetSegmentB = candidateB;
                    }
                }
                else
                {
                    var centerLinkedCandidate = centerGap <= 5.0;
                    if (!centerLinkedCandidate &&
                        !CorrectionSouthBoundaryPreference.IsHardBoundaryCoverageAcceptable(overlap, frameSpan))
                    {
                        continue;
                    }

                    // Fall back to the far south correction definition only when there is no
                    // usable near correction boundary for the quarter/LSD stop.
                    var offsetError = Math.Abs(candidateOutwardDistance - targetOffset);
                    var southBias = Math.Max(0.0, candidateOutwardDistance - RoadAllowanceSecWidthMeters);
                    var candidateBoundaryV = frame.SouthEdgeV - candidateOutwardDistance;
                    var candidatePoint = new Point2d(
                        frame.Origin.X + (frame.EastUnit.X * frame.MidU) + (frame.NorthUnit.X * candidateBoundaryV),
                        frame.Origin.Y + (frame.EastUnit.Y * frame.MidU) + (frame.NorthUnit.Y * candidateBoundaryV));
                    var correctionCompanionPenalty = string.Equals(layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase)
                        ? GetQuarterSouthCorrectionCompanionGapPenalty(frame, correctionSegments, frame.MidU, candidatePoint)
                        : 0.0;
                    var score = correctionCompanionPenalty + offsetError + (0.20 * southBias) + centerPenalty;
                    var layerPriority = GetQuarterSouthCorrectionLayerPriority(layer);
                    if (!foundHard ||
                        layerPriority < GetQuarterSouthCorrectionLayerPriority(bestHardLayer) ||
                        (layerPriority == GetQuarterSouthCorrectionLayerPriority(bestHardLayer) && score < bestHardScore) ||
                        (layerPriority == GetQuarterSouthCorrectionLayerPriority(bestHardLayer) &&
                         Math.Abs(score - bestHardScore) <= 1e-6 &&
                         offsetError < bestHardOffsetError) ||
                        (layerPriority == GetQuarterSouthCorrectionLayerPriority(bestHardLayer) &&
                         Math.Abs(score - bestHardScore) <= 1e-6 &&
                         Math.Abs(offsetError - bestHardOffsetError) <= 1e-6 &&
                         candidateOutwardDistance > bestHardOutwardDistance))
                    {
                        foundHard = true;
                        bestHardScore = score;
                        bestHardOffsetError = offsetError;
                        bestHardOutwardDistance = candidateOutwardDistance;
                        bestHardLayer = layer;
                        segmentA = candidateA;
                        segmentB = candidateB;
                    }
                }
            }

            if (foundInset)
            {
                // Quarter-view correction promotion should restore the road-allowance edge when it exists;
                // inset candidates are only the fallback when no usable hard correction boundary is available.
                if (foundHard)
                {
                    if (string.Equals(bestHardLayer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(bestInsetLayer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
                    {
                        sourceLayer = bestInsetLayer;
                        segmentA = insetSegmentA;
                        segmentB = insetSegmentB;
                        return true;
                    }

                    sourceLayer = bestHardLayer;
                    return foundHard;
                }

                sourceLayer = bestInsetLayer;
                segmentA = insetSegmentA;
                segmentB = insetSegmentB;
                return true;
            }

            sourceLayer = bestHardLayer;
            return foundHard;
        }

        private static bool TryResolveQuarterSouthWestCorrectionCorner(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> boundarySegments,
            Point2d westBoundarySegmentA,
            Point2d westBoundarySegmentB,
            double centerU,
            double minQuarterSpan,
            out double resolvedU,
            out double resolvedV,
            out double westOffset,
            out double southOffset)
        {
            return TryResolveQuarterSouthCorrectionCornerOnBoundary(
                frame,
                boundarySegments,
                westBoundarySegmentA,
                westBoundarySegmentB,
                eastSide: false,
                centerU,
                minQuarterSpan,
                out resolvedU,
                out resolvedV,
                out westOffset,
                out southOffset);
        }

        private static bool TryResolveQuarterSouthEastCorrectionCorner(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> boundarySegments,
            Point2d eastBoundarySegmentA,
            Point2d eastBoundarySegmentB,
            double centerU,
            double minQuarterSpan,
            out double resolvedU,
            out double resolvedV,
            out double eastOffset,
            out double southOffset)
        {
            return TryResolveQuarterSouthCorrectionCornerOnBoundary(
                frame,
                boundarySegments,
                eastBoundarySegmentA,
                eastBoundarySegmentB,
                eastSide: true,
                centerU,
                minQuarterSpan,
                out resolvedU,
                out resolvedV,
                out eastOffset,
                out southOffset);
        }

        private static bool TryResolveQuarterSouthCorrectionCornerOnBoundary(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> boundarySegments,
            Point2d boundarySegmentA,
            Point2d boundarySegmentB,
            bool eastSide,
            double centerU,
            double minQuarterSpan,
            out double resolvedU,
            out double resolvedV,
            out double boundaryOffset,
            out double southOffset)
        {
            resolvedU = default;
            resolvedV = default;
            boundaryOffset = default;
            southOffset = default;
            if (boundarySegments == null || boundarySegments.Count == 0)
            {
                return false;
            }

            const double overlapPadding = 16.0;
            const double minProjectedOverlap = 20.0;
            var sideHalfLimitU = eastSide
                ? centerU + minQuarterSpan
                : centerU - minQuarterSpan;

            var foundInset = false;
            var bestInsetScore = double.MaxValue;
            var bestInsetBoundaryOffsetError = double.MaxValue;
            var insetResolvedU = default(double);
            var insetResolvedV = default(double);
            var insetBoundaryOffset = default(double);
            var insetSouthOffset = default(double);
            var bestInsetLayer = string.Empty;

            var foundHard = false;
            var bestHardScore = double.MaxValue;
            var bestHardBoundaryOffsetError = double.MaxValue;
            var hardResolvedU = default(double);
            var hardResolvedV = default(double);
            var hardBoundaryOffset = default(double);
            var hardSouthOffset = default(double);
            var bestHardLayer = string.Empty;

            for (var i = 0; i < boundarySegments.Count; i++)
            {
                var seg = boundarySegments[i];
                if (!IsQuarterSouthCorrectionCandidateLayer(seg.Layer))
                {
                    continue;
                }

                var delta = seg.B - seg.A;
                var eastComp = Math.Abs(delta.DotProduct(frame.EastUnit));
                var northComp = Math.Abs(delta.DotProduct(frame.NorthUnit));
                if (eastComp <= northComp)
                {
                    continue;
                }

                var relA = seg.A - frame.Origin;
                var relB = seg.B - frame.Origin;
                var uA = relA.DotProduct(frame.EastUnit);
                var uB = relB.DotProduct(frame.EastUnit);
                var sideOverlap = eastSide
                    ? Math.Min(Math.Max(uA, uB), frame.EastEdgeU + overlapPadding) -
                      Math.Max(Math.Min(uA, uB), sideHalfLimitU - overlapPadding)
                    : Math.Min(Math.Max(uA, uB), sideHalfLimitU + overlapPadding) -
                      Math.Max(Math.Min(uA, uB), frame.WestEdgeU - overlapPadding);
                if (sideOverlap < minProjectedOverlap)
                {
                    continue;
                }

                if (!TryIntersectLocalInfiniteLines(
                        frame,
                        boundarySegmentA,
                        boundarySegmentB,
                        seg.A,
                        seg.B,
                        out var candidateU,
                        out var candidateV))
                {
                    continue;
                }

                if ((eastSide && candidateU < sideHalfLimitU) ||
                    (!eastSide && candidateU > sideHalfLimitU))
                {
                    continue;
                }

                var candidateBoundaryOffset = eastSide
                    ? (candidateU - frame.EastEdgeU)
                    : (frame.WestEdgeU - candidateU);
                var candidateSouthOffset = frame.SouthEdgeV - candidateV;
                const double minOffset = -6.0;
                const double maxOffset = 90.0;
                if (candidateBoundaryOffset < -maxOffset ||
                    candidateBoundaryOffset > maxOffset ||
                    candidateSouthOffset < minOffset ||
                    candidateSouthOffset > maxOffset)
                {
                    continue;
                }

                if (!TryClassifyQuarterSouthCorrectionCandidate(
                        seg.Layer,
                        candidateSouthOffset,
                        preferSouthDefinitionThreshold: double.MaxValue,
                        out var prefersInset,
                        out var targetSouthOffset))
                {
                    continue;
                }
                var candidateResolvedU = candidateU;
                var candidateResolvedV = candidateV;
                var candidateResolvedBoundaryOffset = candidateBoundaryOffset;
                var candidateResolvedSouthOffset = candidateSouthOffset;
                var boundaryOffsetError = Math.Abs(candidateResolvedBoundaryOffset);
                var score = Math.Abs(candidateResolvedSouthOffset - targetSouthOffset) + (0.15 * boundaryOffsetError);
                var layerPriority = GetQuarterSouthCorrectionLayerPriority(seg.Layer);
                if (prefersInset)
                {
                    if (!foundInset ||
                        layerPriority < GetQuarterSouthCorrectionLayerPriority(bestInsetLayer) ||
                        (layerPriority == GetQuarterSouthCorrectionLayerPriority(bestInsetLayer) && score < bestInsetScore) ||
                        (layerPriority == GetQuarterSouthCorrectionLayerPriority(bestInsetLayer) &&
                         Math.Abs(score - bestInsetScore) <= 1e-6 &&
                         boundaryOffsetError < bestInsetBoundaryOffsetError))
                    {
                        foundInset = true;
                        bestInsetScore = score;
                        bestInsetBoundaryOffsetError = boundaryOffsetError;
                        insetResolvedU = candidateResolvedU;
                        insetResolvedV = candidateResolvedV;
                        insetBoundaryOffset = candidateResolvedBoundaryOffset;
                        insetSouthOffset = candidateResolvedSouthOffset;
                        bestInsetLayer = seg.Layer;
                    }
                }
                else
                {
                    if (!foundHard ||
                        layerPriority < GetQuarterSouthCorrectionLayerPriority(bestHardLayer) ||
                        (layerPriority == GetQuarterSouthCorrectionLayerPriority(bestHardLayer) && score < bestHardScore) ||
                        (layerPriority == GetQuarterSouthCorrectionLayerPriority(bestHardLayer) &&
                         Math.Abs(score - bestHardScore) <= 1e-6 &&
                         boundaryOffsetError < bestHardBoundaryOffsetError))
                    {
                        foundHard = true;
                        bestHardScore = score;
                        bestHardBoundaryOffsetError = boundaryOffsetError;
                        hardResolvedU = candidateResolvedU;
                        hardResolvedV = candidateResolvedV;
                        hardBoundaryOffset = candidateResolvedBoundaryOffset;
                        hardSouthOffset = candidateResolvedSouthOffset;
                        bestHardLayer = seg.Layer;
                    }
                }
            }

            if (foundHard)
            {
                if (foundInset &&
                    string.Equals(bestHardLayer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(bestInsetLayer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
                {
                    resolvedU = insetResolvedU;
                    resolvedV = insetResolvedV;
                    boundaryOffset = insetBoundaryOffset;
                    southOffset = insetSouthOffset;
                    return true;
                }

                resolvedU = hardResolvedU;
                resolvedV = hardResolvedV;
                boundaryOffset = hardBoundaryOffset;
                southOffset = hardSouthOffset;
                return true;
            }

            if (foundInset)
            {
                resolvedU = insetResolvedU;
                resolvedV = insetResolvedV;
                boundaryOffset = insetBoundaryOffset;
                southOffset = insetSouthOffset;
                return true;
            }

            return false;
        }

        private static void WriteQuarterCorrectionSouthDirectDiagnostics(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> boundarySegments,
            Point2d dividerLineA,
            Point2d dividerLineB,
            double dividerU,
            bool hasWestBoundarySegment,
            Point2d westBoundarySegmentA,
            Point2d westBoundarySegmentB,
            Logger? logger)
        {
            if (logger == null || boundarySegments == null || boundarySegments.Count == 0)
            {
                return;
            }

            var westHard = 0;
            var westInset = 0;
            var dividerHard = 0;
            var dividerInset = 0;
            for (var i = 0; i < boundarySegments.Count; i++)
            {
                var seg = boundarySegments[i];
                if (!IsQuarterSouthCorrectionCandidateLayer(seg.Layer))
                {
                    continue;
                }

                var delta = seg.B - seg.A;
                var eastComp = Math.Abs(delta.DotProduct(frame.EastUnit));
                var northComp = Math.Abs(delta.DotProduct(frame.NorthUnit));
                if (eastComp <= northComp)
                {
                    continue;
                }

                if (hasWestBoundarySegment &&
                    TryIntersectLocalInfiniteLines(
                        frame,
                        westBoundarySegmentA,
                        westBoundarySegmentB,
                        seg.A,
                        seg.B,
                        out var westCandidateU,
                        out var westCandidateV) &&
                    westCandidateU <= dividerU)
                {
                    var westSouthOffset = frame.SouthEdgeV - westCandidateV;
                    if (TryClassifyQuarterSouthCorrectionCandidate(
                            seg.Layer,
                            westSouthOffset,
                            preferSouthDefinitionThreshold: double.MaxValue,
                            out var westPrefersInset,
                            out _)
                        && westPrefersInset)
                    {
                        westInset++;
                    }
                    else
                    {
                        westHard++;
                    }
                }

                if (TryIntersectLocalInfiniteLineWithBoundedSegmentExtension(
                        frame,
                        dividerLineA,
                        dividerLineB,
                        seg.A,
                        seg.B,
                        maxSegmentExtension: 80.0,
                        out _,
                        out var dividerCandidateV))
                {
                    var dividerSouthOffset = frame.SouthEdgeV - dividerCandidateV;
                    if (TryClassifyQuarterSouthCorrectionCandidate(
                            seg.Layer,
                            dividerSouthOffset,
                            preferSouthDefinitionThreshold: double.MaxValue,
                            out var dividerPrefersInset,
                            out _)
                        && dividerPrefersInset)
                    {
                        dividerInset++;
                    }
                    else
                    {
                        dividerHard++;
                    }
                }
            }

            logger.WriteLine(
                $"VERIFY-QTR-CORR-DIRECT sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                $"westHard={westHard} westInset={westInset} dividerHard={dividerHard} dividerInset={dividerInset}");
        }

        private static bool TryResolveQuarterSouthDividerCorrectionIntersection(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> boundarySegments,
            Point2d dividerLineA,
            Point2d dividerLineB,
            out double resolvedU,
            out double resolvedV,
            out double southOffset)
        {
            resolvedU = default;
            resolvedV = default;
            southOffset = default;
            if (boundarySegments == null || boundarySegments.Count == 0)
            {
                return false;
            }

            const double minOffset = -6.0;
            const double maxOffset = 90.0;
            const double maxDividerIntersectionExtension = 80.0;
            var foundInset = false;
            var bestInsetScore = double.MaxValue;
            var insetResolvedU = default(double);
            var insetResolvedV = default(double);
            var insetSouthOffset = default(double);
            var bestInsetLayer = string.Empty;
            var foundHard = false;
            var bestHardScore = double.MaxValue;
            var hardResolvedU = default(double);
            var hardResolvedV = default(double);
            var hardSouthOffset = default(double);
            var bestHardLayer = string.Empty;

            for (var i = 0; i < boundarySegments.Count; i++)
            {
                var seg = boundarySegments[i];
                if (!IsQuarterSouthCorrectionCandidateLayer(seg.Layer))
                {
                    continue;
                }

                var delta = seg.B - seg.A;
                var eastComp = Math.Abs(delta.DotProduct(frame.EastUnit));
                var northComp = Math.Abs(delta.DotProduct(frame.NorthUnit));
                if (eastComp <= northComp)
                {
                    continue;
                }

                if (!TryIntersectLocalInfiniteLineWithBoundedSegmentExtension(
                        frame,
                        dividerLineA,
                        dividerLineB,
                        seg.A,
                        seg.B,
                        maxDividerIntersectionExtension,
                        out var candidateU,
                        out var candidateV))
                {
                    continue;
                }

                if (candidateU < (frame.WestEdgeU - maxDividerIntersectionExtension) ||
                    candidateU > (frame.EastEdgeU + maxDividerIntersectionExtension))
                {
                    continue;
                }

                var candidateSouthOffset = frame.SouthEdgeV - candidateV;
                if (candidateSouthOffset < minOffset || candidateSouthOffset > maxOffset)
                {
                    continue;
                }

                if (!TryClassifyQuarterSouthCorrectionCandidate(
                        seg.Layer,
                        candidateSouthOffset,
                        preferSouthDefinitionThreshold: double.MaxValue,
                        out var prefersInset,
                        out var targetSouthOffset))
                {
                    continue;
                }
                var score = Math.Abs(candidateSouthOffset - targetSouthOffset);
                var layerPriority = GetQuarterSouthCorrectionLayerPriority(seg.Layer);
                if (prefersInset)
                {
                    if (!foundInset ||
                        layerPriority < GetQuarterSouthCorrectionLayerPriority(bestInsetLayer) ||
                        (layerPriority == GetQuarterSouthCorrectionLayerPriority(bestInsetLayer) && score < bestInsetScore))
                    {
                        foundInset = true;
                        bestInsetScore = score;
                        insetResolvedU = candidateU;
                        insetResolvedV = candidateV;
                        insetSouthOffset = candidateSouthOffset;
                        bestInsetLayer = seg.Layer;
                    }
                }
                else
                {
                    if (!foundHard ||
                        layerPriority < GetQuarterSouthCorrectionLayerPriority(bestHardLayer) ||
                        (layerPriority == GetQuarterSouthCorrectionLayerPriority(bestHardLayer) && score < bestHardScore))
                    {
                        foundHard = true;
                        bestHardScore = score;
                        hardResolvedU = candidateU;
                        hardResolvedV = candidateV;
                        hardSouthOffset = candidateSouthOffset;
                        bestHardLayer = seg.Layer;
                    }
                }
            }

            if (foundHard)
            {
                if (foundInset &&
                    string.Equals(bestHardLayer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(bestInsetLayer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
                {
                    resolvedU = insetResolvedU;
                    resolvedV = insetResolvedV;
                    southOffset = insetSouthOffset;
                    return true;
                }

                resolvedU = hardResolvedU;
                resolvedV = hardResolvedV;
                southOffset = hardSouthOffset;
                return true;
            }

            if (foundInset)
            {
                resolvedU = insetResolvedU;
                resolvedV = insetResolvedV;
                southOffset = insetSouthOffset;
                return true;
            }

            return false;
        }

        private static bool TryResolveNearbyQuarterDividerCorrectionDrawingEndpoint(
            Transaction transaction,
            ObjectId ownerId,
            QuarterViewSectionFrame frame,
            Point2d dividerLineA,
            Point2d dividerLineB,
            Point2d nearWorld,
            double maxEndpointMove,
            out Point2d resolvedPoint,
            out double resolvedMove)
        {
            resolvedPoint = default;
            resolvedMove = double.MaxValue;
            if (transaction == null ||
                ownerId.IsNull ||
                maxEndpointMove <= 0.0 ||
                !TryConvertQuarterWorldToLocal(frame, dividerLineA, out var divStartU, out var divStartV) ||
                !TryConvertQuarterWorldToLocal(frame, dividerLineB, out var divEndU, out var divEndV))
            {
                return false;
            }

            var dirU = divEndU - divStartU;
            var dirV = divEndV - divStartV;
            var dirLength = Math.Sqrt((dirU * dirU) + (dirV * dirV));
            if (dirLength <= 1e-6)
            {
                return false;
            }

            var dirLengthSquared = (dirU * dirU) + (dirV * dirV);
            const double maxPerpendicularGap = 0.75;
            const double maxAlongExtension = 20.0;
            var found = false;

            if (transaction.GetObject(ownerId, OpenMode.ForRead, false) is not BlockTableRecord ownerRecord)
            {
                return false;
            }

            foreach (ObjectId entityId in ownerRecord)
            {
                if (transaction.GetObject(entityId, OpenMode.ForRead, false) is not Polyline poly ||
                    poly.IsErased ||
                    poly.Closed ||
                    poly.NumberOfVertices < 2 ||
                    !string.Equals(poly.Layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                for (var vi = 0; vi < poly.NumberOfVertices; vi++)
                {
                    TryResolveQuarterDividerCorrectionCandidatePoint(
                        frame,
                        divStartU,
                        divStartV,
                        dirU,
                        dirV,
                        dirLength,
                        dirLengthSquared,
                        maxAlongExtension,
                        maxPerpendicularGap,
                        nearWorld,
                        maxEndpointMove,
                        poly.GetPoint2dAt(vi),
                        ref found,
                        ref resolvedPoint,
                        ref resolvedMove);
                }
            }

            return found;
        }

        private static bool TryResolveQuarterDividerCorrectionCandidatePoint(
            QuarterViewSectionFrame frame,
            double divStartU,
            double divStartV,
            double dirU,
            double dirV,
            double dirLength,
            double dirLengthSquared,
            double maxAlongExtension,
            double maxPerpendicularGap,
            Point2d nearWorld,
            double maxEndpointMove,
            Point2d candidateWorld,
            ref bool found,
            ref Point2d resolvedPoint,
            ref double resolvedMove)
        {
            var move = nearWorld.GetDistanceTo(candidateWorld);
            if (move > maxEndpointMove)
            {
                return false;
            }

            if (!TryConvertQuarterWorldToLocal(frame, candidateWorld, out var candidateU, out var candidateV))
            {
                return false;
            }

            var relU = candidateU - divStartU;
            var relV = candidateV - divStartV;
            var along = ((relU * dirU) + (relV * dirV)) / dirLengthSquared;
            if (along < (-maxAlongExtension / dirLength) ||
                along > (1.0 + (maxAlongExtension / dirLength)))
            {
                return false;
            }

            var perp = Math.Abs((relU * dirV) - (relV * dirU)) / dirLength;
            if (perp > maxPerpendicularGap)
            {
                return false;
            }

            if (!found || move < resolvedMove)
            {
                found = true;
                resolvedPoint = candidateWorld;
                resolvedMove = move;
                return true;
            }

            return false;
        }

        private static bool TryResolveQuarterSouthInferredInsetCornerCandidate(
            QuarterViewSectionFrame frame,
            Point2d boundarySegmentA,
            Point2d boundarySegmentB,
            Point2d correctionSegmentA,
            Point2d correctionSegmentB,
            bool eastSide,
            double sideHalfLimitU,
            out double resolvedU,
            out double resolvedV,
            out double boundaryOffset,
            out double southOffset)
        {
            resolvedU = default;
            resolvedV = default;
            boundaryOffset = default;
            southOffset = default;
            const double minOffset = -6.0;
            const double maxOffset = 90.0;
            var inferredInsetShift = RoadAllowanceSecWidthMeters - CorrectionLineInsetMeters;
            var direction = correctionSegmentB - correctionSegmentA;
            var length = direction.Length;
            if (length <= 1e-6)
            {
                return false;
            }

            var normal = new Vector2d(-direction.Y / length, direction.X / length);
            var firstOffset = normal * inferredInsetShift;
            var secondOffset = new Vector2d(-firstOffset.X, -firstOffset.Y);
            var shiftedA = correctionSegmentA + firstOffset;
            var shiftedB = correctionSegmentB + firstOffset;
            var alternateShiftedA = correctionSegmentA + secondOffset;
            var alternateShiftedB = correctionSegmentB + secondOffset;
            if (TryConvertQuarterWorldToLocal(frame, Midpoint(alternateShiftedA, alternateShiftedB), out _, out var alternateMidV) &&
                TryConvertQuarterWorldToLocal(frame, Midpoint(shiftedA, shiftedB), out _, out var primaryMidV))
            {
                var primaryInsetError = Math.Abs((frame.SouthEdgeV - primaryMidV) - CorrectionLineInsetMeters);
                var alternateInsetError = Math.Abs((frame.SouthEdgeV - alternateMidV) - CorrectionLineInsetMeters);
                if (alternateInsetError < primaryInsetError)
                {
                    shiftedA = alternateShiftedA;
                    shiftedB = alternateShiftedB;
                }
            }

            if (!TryIntersectLocalInfiniteLines(
                    frame,
                    boundarySegmentA,
                    boundarySegmentB,
                    shiftedA,
                    shiftedB,
                    out resolvedU,
                    out resolvedV))
            {
                return false;
            }

            if ((eastSide && resolvedU < sideHalfLimitU) ||
                (!eastSide && resolvedU > sideHalfLimitU))
            {
                return false;
            }

            boundaryOffset = eastSide
                ? (resolvedU - frame.EastEdgeU)
                : (frame.WestEdgeU - resolvedU);
            southOffset = frame.SouthEdgeV - resolvedV;
            return boundaryOffset >= -maxOffset &&
                   boundaryOffset <= maxOffset &&
                   southOffset >= minOffset &&
                   southOffset <= maxOffset;
        }

        private static bool TryResolveQuarterSouthInferredInsetBoundaryCandidate(
            QuarterViewSectionFrame frame,
            Point2d correctionSegmentA,
            Point2d correctionSegmentB,
            out Point2d inferredSegmentA,
            out Point2d inferredSegmentB,
            out double inferredOutwardDistance)
        {
            inferredSegmentA = default;
            inferredSegmentB = default;
            inferredOutwardDistance = default;

            const double minOffset = 0.5;
            const double maxOffset = 40.0;
            var inferredInsetShift = RoadAllowanceSecWidthMeters - CorrectionLineInsetMeters;
            var direction = correctionSegmentB - correctionSegmentA;
            var length = direction.Length;
            if (length <= 1e-6)
            {
                return false;
            }

            var normal = new Vector2d(-direction.Y / length, direction.X / length);
            var firstOffset = normal * inferredInsetShift;
            var secondOffset = new Vector2d(-firstOffset.X, -firstOffset.Y);
            var shiftedA = correctionSegmentA + firstOffset;
            var shiftedB = correctionSegmentB + firstOffset;
            var alternateShiftedA = correctionSegmentA + secondOffset;
            var alternateShiftedB = correctionSegmentB + secondOffset;
            if (TryConvertQuarterWorldToLocal(frame, Midpoint(alternateShiftedA, alternateShiftedB), out _, out var alternateMidV) &&
                TryConvertQuarterWorldToLocal(frame, Midpoint(shiftedA, shiftedB), out _, out var primaryMidV))
            {
                var primaryInsetError = Math.Abs((frame.SouthEdgeV - primaryMidV) - CorrectionLineInsetMeters);
                var alternateInsetError = Math.Abs((frame.SouthEdgeV - alternateMidV) - CorrectionLineInsetMeters);
                if (alternateInsetError < primaryInsetError)
                {
                    shiftedA = alternateShiftedA;
                    shiftedB = alternateShiftedB;
                }
            }

            if (!TryConvertQuarterWorldToLocal(frame, Midpoint(shiftedA, shiftedB), out _, out var inferredMidV))
            {
                return false;
            }

            var outwardDistance = frame.SouthEdgeV - inferredMidV;
            if (outwardDistance < minOffset || outwardDistance > maxOffset)
            {
                return false;
            }

            inferredSegmentA = shiftedA;
            inferredSegmentB = shiftedB;
            inferredOutwardDistance = outwardDistance;
            return true;
        }

        private static bool IsQuarterSouthCorrectionCandidateLayer(string? layerName)
        {
            if (string.IsNullOrWhiteSpace(layerName))
            {
                return false;
            }

            var normalizedLayer = layerName.Trim();
            return string.Equals(normalizedLayer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalizedLayer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryClassifyQuarterSouthCorrectionCandidate(
            string? layerName,
            double outwardDistance,
            double preferSouthDefinitionThreshold,
            out bool prefersInset,
            out double targetOffset)
        {
            prefersInset = false;
            targetOffset = RoadAllowanceSecWidthMeters;

            if (string.Equals(layerName, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (string.Equals(layerName, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase))
            {
                prefersInset = true;
                targetOffset = CorrectionLineInsetMeters;
                return true;
            }

            var insetLike =
                outwardDistance < preferSouthDefinitionThreshold &&
                CorrectionSouthBoundaryPreference.IsCloserToInsetThanHardBoundary(
                    outwardDistance,
                    CorrectionLineInsetMeters,
                    RoadAllowanceSecWidthMeters);
            prefersInset =
                insetLike &&
                CorrectionSouthBoundaryPreference.IsPlausibleInsetOffset(
                    outwardDistance,
                    CorrectionLineInsetMeters);
            if (!prefersInset && insetLike)
            {
                return false;
            }

            targetOffset = prefersInset ? CorrectionLineInsetMeters : RoadAllowanceSecWidthMeters;
            return true;
        }

        private static double GetQuarterSouthCorrectionCompanionGapPenalty(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> correctionSegments,
            double stationU,
            Point2d candidatePoint)
        {
            if (correctionSegments == null || correctionSegments.Count == 0)
            {
                return 0.0;
            }

            var bestGap = double.MaxValue;
            for (var i = 0; i < correctionSegments.Count; i++)
            {
                var seg = correctionSegments[i];
                if (!string.Equals(seg.Layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TryProjectBoundarySegmentVAtU(frame, seg.A, seg.B, stationU, out var companionV))
                {
                    continue;
                }

                var companionPoint = new Point2d(
                    frame.Origin.X + (frame.EastUnit.X * stationU) + (frame.NorthUnit.X * companionV),
                    frame.Origin.Y + (frame.EastUnit.Y * stationU) + (frame.NorthUnit.Y * companionV));
                var gap = candidatePoint.GetDistanceTo(companionPoint);
                if (gap < bestGap)
                {
                    bestGap = gap;
                }
            }

            if (double.IsInfinity(bestGap) || double.IsNaN(bestGap) || bestGap == double.MaxValue)
            {
                return 0.0;
            }

            return bestGap * 1000.0;
        }

        private static int GetQuarterSouthCorrectionLayerPriority(string? layerName)
        {
            if (string.Equals(layerName, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (string.Equals(layerName, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            return 2;
        }

        private static bool TryResolveQuarterViewSouthWestCorrectionIntersection(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> boundarySegments,
            Point2d southSegmentA,
            Point2d southSegmentB,
            out double intersectionU,
            out double intersectionV)
        {
            intersectionU = default;
            intersectionV = default;
            if (boundarySegments == null || boundarySegments.Count == 0)
            {
                return false;
            }

            const double overlapPadding = 16.0;
            const double minProjectedOverlap = 20.0;
            const double minWestOffset = -0.75;
            const double maxWestOffset = 60.0;
            const double minSouthOffset = 0.5;
            const double maxSouthOffset = 60.0;
            var found = false;
            var bestScore = double.MaxValue;
            var bestWestOffsetError = double.MaxValue;
            for (var i = 0; i < boundarySegments.Count; i++)
            {
                var seg = boundarySegments[i];
                if (!string.Equals(seg.Layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var delta = seg.B - seg.A;
                var eastComp = Math.Abs(delta.DotProduct(frame.EastUnit));
                var northComp = Math.Abs(delta.DotProduct(frame.NorthUnit));
                if (northComp <= eastComp)
                {
                    continue;
                }

                var relA = seg.A - frame.Origin;
                var relB = seg.B - frame.Origin;
                var uA = relA.DotProduct(frame.EastUnit);
                var uB = relB.DotProduct(frame.EastUnit);
                var vA = relA.DotProduct(frame.NorthUnit);
                var vB = relB.DotProduct(frame.NorthUnit);
                var overlap = Math.Min(Math.Max(vA, vB), frame.NorthEdgeV + overlapPadding) -
                              Math.Max(Math.Min(vA, vB), frame.SouthEdgeV - overlapPadding);
                if (overlap < minProjectedOverlap)
                {
                    continue;
                }

                if (!TryIntersectBoundarySegmentsLocal(
                        frame,
                        seg.A,
                        seg.B,
                        southSegmentA,
                        southSegmentB,
                        out var candidateU,
                        out var candidateV))
                {
                    continue;
                }

                var westOffset = frame.WestEdgeU - candidateU;
                if (westOffset < minWestOffset || westOffset > maxWestOffset)
                {
                    continue;
                }

                var southOffset = frame.SouthEdgeV - candidateV;
                if (southOffset < minSouthOffset || southOffset > maxSouthOffset)
                {
                    continue;
                }

                var westOffsetError = Math.Abs(westOffset - RoadAllowanceUsecWidthMeters);
                var southOffsetError = Math.Abs(southOffset - RoadAllowanceSecWidthMeters);
                var score = westOffsetError + (0.35 * southOffsetError);
                if (!found ||
                    score < bestScore ||
                    (Math.Abs(score - bestScore) <= 1e-6 && westOffsetError < bestWestOffsetError))
                {
                    found = true;
                    bestScore = score;
                    bestWestOffsetError = westOffsetError;
                    intersectionU = candidateU;
                    intersectionV = candidateV;
                }
            }

            return found;
        }

        private static bool TryResolveQuarterDisplaySouthCorrectionTrendFromEastCompanion(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> boundarySegments,
            string? southSource,
            Point2d selectedSouthBoundarySegmentA,
            Point2d selectedSouthBoundarySegmentB,
            bool hasWestBoundarySegment,
            Point2d westBoundarySegmentA,
            Point2d westBoundarySegmentB,
            Point2d dividerLineA,
            Point2d dividerLineB,
            bool hasEastBoundarySegment,
            Point2d eastBoundarySegmentA,
            Point2d eastBoundarySegmentB,
            Point2d currentSouthMidLocal,
            Point2d currentSouthWestLocal,
            Point2d currentSouthEastLocal,
            out Point2d quarterSouthWestLocal,
            out Point2d quarterSouthMidLocal,
            out Point2d quarterSouthEastLocal,
            out string sourceLayer)
        {
            quarterSouthWestLocal = currentSouthWestLocal;
            quarterSouthMidLocal = currentSouthMidLocal;
            quarterSouthEastLocal = currentSouthEastLocal;
            sourceLayer = string.Empty;
            if (!string.Equals(southSource, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) ||
                !hasWestBoundarySegment ||
                !hasEastBoundarySegment ||
                boundarySegments == null ||
                boundarySegments.Count == 0)
            {
                return false;
            }

            var currentWestSouthOffset = frame.SouthEdgeV - currentSouthWestLocal.Y;
            var currentMidSouthOffset = frame.SouthEdgeV - currentSouthMidLocal.Y;
            var currentEastSouthOffset = frame.SouthEdgeV - currentSouthEastLocal.Y;
            var westInsetLike = currentWestSouthOffset <= (CorrectionLineInsetMeters + 2.5);
            var midInsetLike = currentMidSouthOffset <= (CorrectionLineInsetMeters + 2.5);
            var eastInsetLike = currentEastSouthOffset <= (CorrectionLineInsetMeters + 2.5);
            if (!westInsetLike && !midInsetLike && !eastInsetLike)
            {
                return false;
            }

            if (!TryConvertQuarterWorldToLocal(frame, selectedSouthBoundarySegmentA, out var selectedAu, out var selectedAv) ||
                !TryConvertQuarterWorldToLocal(frame, selectedSouthBoundarySegmentB, out var selectedBu, out var selectedBv))
            {
                return false;
            }

            var selectedDirU = selectedBu - selectedAu;
            var selectedDirV = selectedBv - selectedAv;
            var selectedDirLength = Math.Sqrt((selectedDirU * selectedDirU) + (selectedDirV * selectedDirV));
            if (selectedDirLength <= 1e-6)
            {
                return false;
            }

            var selectedMinU = Math.Min(selectedAu, selectedBu);
            var selectedMaxU = Math.Max(selectedAu, selectedBu);
            var maxAdjacentGap = (frame.EastEdgeU - frame.WestEdgeU) + 150.0;
            var found = false;
            var bestScore = double.MaxValue;
            var bestDirection = string.Empty;
            var bestWestLocal = currentSouthWestLocal;
            var bestMidLocal = currentSouthMidLocal;
            var bestEastLocal = currentSouthEastLocal;
            for (var i = 0; i < boundarySegments.Count; i++)
            {
                var seg = boundarySegments[i];
                if (!string.Equals(seg.Layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if ((seg.A.GetDistanceTo(selectedSouthBoundarySegmentA) <= 1e-3 &&
                     seg.B.GetDistanceTo(selectedSouthBoundarySegmentB) <= 1e-3) ||
                    (seg.A.GetDistanceTo(selectedSouthBoundarySegmentB) <= 1e-3 &&
                     seg.B.GetDistanceTo(selectedSouthBoundarySegmentA) <= 1e-3))
                {
                    continue;
                }

                var delta = seg.B - seg.A;
                var eastComp = Math.Abs(delta.DotProduct(frame.EastUnit));
                var northComp = Math.Abs(delta.DotProduct(frame.NorthUnit));
                if (eastComp <= northComp)
                {
                    continue;
                }

                if (!TryConvertQuarterWorldToLocal(frame, seg.A, out var segAu, out var segAv) ||
                    !TryConvertQuarterWorldToLocal(frame, seg.B, out var segBu, out var segBv))
                {
                    continue;
                }

                var segDirU = segBu - segAu;
                var segDirV = segBv - segAv;
                var segDirLength = Math.Sqrt((segDirU * segDirU) + (segDirV * segDirV));
                if (segDirLength <= 1e-6)
                {
                    continue;
                }

                var normalizedCross = Math.Abs((selectedDirU * segDirV) - (selectedDirV * segDirU)) / (selectedDirLength * segDirLength);
                if (normalizedCross > 0.02)
                {
                    continue;
                }

                var segMinU = Math.Min(segAu, segBu);
                var segMaxU = Math.Max(segAu, segBu);
                var eastGap = segMinU - selectedMaxU;
                var westGap = selectedMinU - segMaxU;
                var adjacentGap = double.MaxValue;
                var direction = string.Empty;
                if (eastGap >= -10.0 && eastGap <= maxAdjacentGap)
                {
                    adjacentGap = eastGap;
                    direction = "east";
                }

                if (westGap >= -10.0 &&
                    westGap <= maxAdjacentGap &&
                    (string.IsNullOrEmpty(direction) || westGap < adjacentGap - 1e-6))
                {
                    adjacentGap = westGap;
                    direction = "west";
                }

                if (string.IsNullOrEmpty(direction))
                {
                    continue;
                }

                if (!TryIntersectLocalInfiniteLines(
                        frame,
                        westBoundarySegmentA,
                        westBoundarySegmentB,
                        seg.A,
                        seg.B,
                        out var candidateWestU,
                        out var candidateWestV) ||
                    !TryIntersectLocalInfiniteLines(
                        frame,
                        dividerLineA,
                        dividerLineB,
                        seg.A,
                        seg.B,
                        out var candidateMidU,
                        out var candidateMidV) ||
                    !TryIntersectLocalInfiniteLines(
                        frame,
                        eastBoundarySegmentA,
                        eastBoundarySegmentB,
                        seg.A,
                        seg.B,
                        out var candidateEastU,
                        out var candidateEastV))
                {
                    continue;
                }

                var candidateWestBoundaryOffset = frame.WestEdgeU - candidateWestU;
                var candidateEastBoundaryOffset = candidateEastU - frame.EastEdgeU;
                if (candidateWestBoundaryOffset < -80.0 ||
                    candidateWestBoundaryOffset > 90.0 ||
                    candidateEastBoundaryOffset < -80.0 ||
                    candidateEastBoundaryOffset > 90.0)
                {
                    continue;
                }

                var candidateWestSouthOffset = frame.SouthEdgeV - candidateWestV;
                var candidateMidSouthOffset = frame.SouthEdgeV - candidateMidV;
                var candidateEastSouthOffset = frame.SouthEdgeV - candidateEastV;
                const double minHardSouthOffset = 12.0;
                const double maxHardSouthOffset = 35.0;
                if (candidateWestSouthOffset < minHardSouthOffset ||
                    candidateWestSouthOffset > maxHardSouthOffset ||
                    candidateMidSouthOffset < minHardSouthOffset ||
                    candidateMidSouthOffset > maxHardSouthOffset ||
                    candidateEastSouthOffset < minHardSouthOffset ||
                    candidateEastSouthOffset > maxHardSouthOffset)
                {
                    continue;
                }
                var hardOffsetError =
                    Math.Abs(candidateWestSouthOffset - RoadAllowanceSecWidthMeters) +
                    Math.Abs(candidateMidSouthOffset - RoadAllowanceSecWidthMeters) +
                    Math.Abs(candidateEastSouthOffset - RoadAllowanceSecWidthMeters);
                var boundaryOffsetError =
                    Math.Abs(candidateWestBoundaryOffset - RoadAllowanceUsecWidthMeters) +
                    Math.Abs(candidateEastBoundaryOffset);
                var score = adjacentGap + (hardOffsetError * 50.0) + (boundaryOffsetError * 2.0) + (normalizedCross * 5000.0);
                if (!found || score < bestScore)
                {
                    found = true;
                    bestScore = score;
                    bestDirection = direction;
                    bestWestLocal = new Point2d(candidateWestU, candidateWestV);
                    bestMidLocal = new Point2d(candidateMidU, candidateMidV);
                    bestEastLocal = new Point2d(candidateEastU, candidateEastV);
                    sourceLayer = $"{seg.Layer}-{direction}-companion";
                }
            }

            if (!found)
            {
                return false;
            }

            quarterSouthWestLocal = westInsetLike ? bestWestLocal : currentSouthWestLocal;
            quarterSouthMidLocal = midInsetLike ? bestMidLocal : currentSouthMidLocal;
            quarterSouthEastLocal = eastInsetLike ? bestEastLocal : currentSouthEastLocal;
            sourceLayer = $"{LayerUsecCorrectionZero}-{bestDirection}-companion";
            return found;
        }

        private static bool TryIntersectBoundarySegmentWithLocalLine(
            QuarterViewSectionFrame frame,
            Point2d segmentA,
            Point2d segmentB,
            double lineU1,
            double lineV1,
            double lineU2,
            double lineV2,
            out double intersectionU,
            out double intersectionV)
        {
            intersectionU = default;
            intersectionV = default;
            if (segmentA.GetDistanceTo(segmentB) <= 1e-6)
            {
                return false;
            }

            var relA = segmentA - frame.Origin;
            var relB = segmentB - frame.Origin;
            var p = new Point2d(
                relA.DotProduct(frame.EastUnit),
                relA.DotProduct(frame.NorthUnit));
            var p2 = new Point2d(
                relB.DotProduct(frame.EastUnit),
                relB.DotProduct(frame.NorthUnit));
            var q = new Point2d(lineU1, lineV1);
            var q2 = new Point2d(lineU2, lineV2);
            if (q.GetDistanceTo(q2) <= 1e-6)
            {
                return false;
            }

            var r = p2 - p;
            var s = q2 - q;
            var denom = (r.X * s.Y) - (r.Y * s.X);
            if (Math.Abs(denom) <= 1e-9)
            {
                return false;
            }

            var qp = q - p;
            var t = ((qp.X * s.Y) - (qp.Y * s.X)) / denom;
            intersectionU = p.X + (r.X * t);
            intersectionV = p.Y + (r.Y * t);
            return true;
        }

        private static bool TryProjectBoundarySegmentVAtU(
            QuarterViewSectionFrame frame,
            Point2d segmentA,
            Point2d segmentB,
            double targetU,
            out double projectedV)
        {
            projectedV = default;
            if (segmentA.GetDistanceTo(segmentB) <= 1e-6)
            {
                return false;
            }

            var relA = segmentA - frame.Origin;
            var relB = segmentB - frame.Origin;
            var uA = relA.DotProduct(frame.EastUnit);
            var uB = relB.DotProduct(frame.EastUnit);
            var vA = relA.DotProduct(frame.NorthUnit);
            var vB = relB.DotProduct(frame.NorthUnit);
            var du = uB - uA;
            if (Math.Abs(du) <= 1e-6)
            {
                return false;
            }

            const double apparentIntersectionPadding = 80.0;
            var minU = Math.Min(uA, uB) - apparentIntersectionPadding;
            var maxU = Math.Max(uA, uB) + apparentIntersectionPadding;
            if (targetU < minU || targetU > maxU)
            {
                return false;
            }

            var t = (targetU - uA) / du;
            projectedV = vA + ((vB - vA) * t);
            return true;
        }

        private static bool TryProjectBoundarySegmentUAtV(
            QuarterViewSectionFrame frame,
            Point2d segmentA,
            Point2d segmentB,
            double targetV,
            out double projectedU)
        {
            projectedU = default;
            if (segmentA.GetDistanceTo(segmentB) <= 1e-6)
            {
                return false;
            }

            var relA = segmentA - frame.Origin;
            var relB = segmentB - frame.Origin;
            var uA = relA.DotProduct(frame.EastUnit);
            var uB = relB.DotProduct(frame.EastUnit);
            var vA = relA.DotProduct(frame.NorthUnit);
            var vB = relB.DotProduct(frame.NorthUnit);
            var dv = vB - vA;
            if (Math.Abs(dv) <= 1e-6)
            {
                return false;
            }

            const double apparentIntersectionPadding = 80.0;
            var minV = Math.Min(vA, vB) - apparentIntersectionPadding;
            var maxV = Math.Max(vA, vB) + apparentIntersectionPadding;
            if (targetV < minV || targetV > maxV)
            {
                return false;
            }

            var t = (targetV - vA) / dv;
            projectedU = uA + ((uB - uA) * t);
            return true;
        }

        private static bool TryIntersectLocalInfiniteLines(
            QuarterViewSectionFrame frame,
            Point2d firstA,
            Point2d firstB,
            Point2d secondA,
            Point2d secondB,
            out double intersectionU,
            out double intersectionV)
        {
            intersectionU = default;
            intersectionV = default;

            if (firstA.GetDistanceTo(firstB) <= 1e-6 || secondA.GetDistanceTo(secondB) <= 1e-6)
            {
                return false;
            }

            if (!TryConvertQuarterWorldToLocal(frame, firstA, out var pU, out var pV) ||
                !TryConvertQuarterWorldToLocal(frame, firstB, out var p2U, out var p2V) ||
                !TryConvertQuarterWorldToLocal(frame, secondA, out var qU, out var qV) ||
                !TryConvertQuarterWorldToLocal(frame, secondB, out var q2U, out var q2V))
            {
                return false;
            }

            var p = new Point2d(pU, pV);
            var p2 = new Point2d(p2U, p2V);
            var q = new Point2d(qU, qV);
            var q2 = new Point2d(q2U, q2V);

            var r = p2 - p;
            var s = q2 - q;
            var denom = (r.X * s.Y) - (r.Y * s.X);
            if (Math.Abs(denom) <= 1e-9)
            {
                return false;
            }

            var qp = q - p;
            var t = ((qp.X * s.Y) - (qp.Y * s.X)) / denom;
            intersectionU = p.X + (r.X * t);
            intersectionV = p.Y + (r.Y * t);
            return true;
        }

        private static bool TryIntersectBoundarySegmentsLocal(
            QuarterViewSectionFrame frame,
            Point2d firstA,
            Point2d firstB,
            Point2d secondA,
            Point2d secondB,
            out double intersectionU,
            out double intersectionV)
        {
            intersectionU = default;
            intersectionV = default;

            if (firstA.GetDistanceTo(firstB) <= 1e-6 || secondA.GetDistanceTo(secondB) <= 1e-6)
            {
                return false;
            }

            if (!TryConvertQuarterWorldToLocal(frame, firstA, out var pU, out var pV) ||
                !TryConvertQuarterWorldToLocal(frame, firstB, out var p2U, out var p2V) ||
                !TryConvertQuarterWorldToLocal(frame, secondA, out var qU, out var qV) ||
                !TryConvertQuarterWorldToLocal(frame, secondB, out var q2U, out var q2V))
            {
                return false;
            }

            var p = new Point2d(pU, pV);
            var p2 = new Point2d(p2U, p2V);
            var q = new Point2d(qU, qV);
            var q2 = new Point2d(q2U, q2V);

            var r = p2 - p;
            var s = q2 - q;
            var denom = (r.X * s.Y) - (r.Y * s.X);
            if (Math.Abs(denom) <= 1e-9)
            {
                return false;
            }

            var qp = q - p;
            var t = ((qp.X * s.Y) - (qp.Y * s.X)) / denom;
            var uHit = p.X + (r.X * t);
            var vHit = p.Y + (r.Y * t);

            const double apparentIntersectionPadding = 80.0;
            var hit = new Point2d(uHit, vHit);
            if (!IsPointWithinExpandedSegmentBounds(hit, p, p2, apparentIntersectionPadding) ||
                !IsPointWithinExpandedSegmentBounds(hit, q, q2, apparentIntersectionPadding))
            {
                return false;
            }

            intersectionU = uHit;
            intersectionV = vHit;
            return true;
        }

        private static Point2d QuarterViewLocalToWorld(QuarterViewSectionFrame frame, double u, double v)
        {
            return frame.Origin + (frame.EastUnit * u) + (frame.NorthUnit * v);
        }

        private static int DrawQuarterViewPolygonFromLocal(
            BlockTableRecord modelSpace,
            Transaction transaction,
            QuarterViewSectionFrame frame,
            out ObjectId polyId,
            params Point2d[] localPoints)
        {
            polyId = ObjectId.Null;
            if (modelSpace == null || transaction == null)
            {
                return 0;
            }

            if (localPoints == null || localPoints.Length < 3)
            {
                return 0;
            }

            var worldPoints = new List<Point2d>(localPoints.Length);
            for (var i = 0; i < localPoints.Length; i++)
            {
                var world = QuarterViewLocalToWorld(frame, localPoints[i].X, localPoints[i].Y);
                if (worldPoints.Count == 0 || world.GetDistanceTo(worldPoints[worldPoints.Count - 1]) > 1e-4)
                {
                    worldPoints.Add(world);
                }
            }

            if (worldPoints.Count < 3)
            {
                return 0;
            }

            if (worldPoints[0].GetDistanceTo(worldPoints[worldPoints.Count - 1]) <= 1e-4)
            {
                worldPoints.RemoveAt(worldPoints.Count - 1);
            }

            if (worldPoints.Count < 3)
            {
                return 0;
            }

            var areaTwice = 0.0;
            for (var i = 0; i < worldPoints.Count; i++)
            {
                var a = worldPoints[i];
                var b = worldPoints[(i + 1) % worldPoints.Count];
                areaTwice += (a.X * b.Y) - (b.X * a.Y);
            }

            if (Math.Abs(areaTwice) <= 1e-2)
            {
                return 0;
            }

            var poly = new Polyline(worldPoints.Count)
            {
                Closed = true,
                Layer = LayerQuarterView,
                ColorIndex = 256
            };
            for (var i = 0; i < worldPoints.Count; i++)
            {
                poly.AddVertexAt(i, worldPoints[i], 0, 0, 0);
            }
            modelSpace.AppendEntity(poly);
            transaction.AddNewlyCreatedDBObject(poly, true);
            polyId = poly.ObjectId;
            return 1;
        }

        private static int DrawQuarterViewPolygonFromLocal(
            BlockTableRecord modelSpace,
            Transaction transaction,
            QuarterViewSectionFrame frame,
            params Point2d[] localPoints)
        {
            return DrawQuarterViewPolygonFromLocal(
                modelSpace,
                transaction,
                frame,
                out _,
                localPoints);
        }

        private static void EnsureSecTypeLayers(
            Database database,
            Transaction transaction,
            string fallbackSecType,
            IReadOnlyDictionary<QuarterSelection, string>? quarterSecTypes)
        {
            if (database == null || transaction == null)
            {
                return;
            }

            var layers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                NormalizeSecType(fallbackSecType)
            };

            if (quarterSecTypes != null)
            {
                foreach (var secType in quarterSecTypes.Values)
                {
                    layers.Add(NormalizeSecType(secType));
                }
            }

            foreach (var layer in layers)
            {
                EnsureLayer(database, transaction, layer);
            }
        }

        private static string ResolveQuarterSecTypeForQuarter(
            IReadOnlyDictionary<QuarterSelection, string>? quarterSecTypes,
            QuarterSelection quarter,
            string fallbackSecType)
        {
            if (quarterSecTypes != null &&
                quarterSecTypes.TryGetValue(quarter, out var secType) &&
                !string.IsNullOrWhiteSpace(secType))
            {
                return NormalizeSecType(secType);
            }

            return NormalizeSecType(fallbackSecType);
        }

        private static void DrawLsdSubdivisionLines(
            Database database,
            Transaction transaction,
            BlockTableRecord modelSpace,
            Editor editor,
            Dictionary<QuarterSelection, Polyline> quarterMap,
            QuarterAnchors sectionAnchors,
            SectionKey key,
            string secType)
        {
            if (quarterMap == null || quarterMap.Count == 0)
            {
                return;
            }

            var lsdLayer = "L-SECTION-LSD";
            EnsureLayer(database, transaction, lsdLayer);

            var eastUnit = GetUnitVector(sectionAnchors.Left, sectionAnchors.Right, new Vector2d(1, 0));
            var northUnit = GetUnitVector(sectionAnchors.Bottom, sectionAnchors.Top, new Vector2d(0, 1));

            foreach (var pair in quarterMap)
            {
                var quarterAnchors = GetLsdAnchorsForQuarter(pair.Value, eastUnit, northUnit);

                var verticalStart = quarterAnchors.Top;
                var verticalEnd = quarterAnchors.Bottom;
                var horizontalStart = quarterAnchors.Left;
                var horizontalEnd = quarterAnchors.Right;

                var vertical = new Line(
                    new Point3d(verticalStart.X, verticalStart.Y, 0),
                    new Point3d(verticalEnd.X, verticalEnd.Y, 0))
                {
                    Layer = lsdLayer,
                    ColorIndex = 256
                };
                var horizontal = new Line(
                    new Point3d(horizontalStart.X, horizontalStart.Y, 0),
                    new Point3d(horizontalEnd.X, horizontalEnd.Y, 0))
                {
                    Layer = lsdLayer,
                    ColorIndex = 256
                };

                modelSpace.AppendEntity(vertical);
                transaction.AddNewlyCreatedDBObject(vertical, true);
                modelSpace.AppendEntity(horizontal);
                transaction.AddNewlyCreatedDBObject(horizontal, true);

                var labelCenter = new Point3d(
                    0.25 * (verticalStart.X + verticalEnd.X + horizontalStart.X + horizontalEnd.X),
                    0.25 * (verticalStart.Y + verticalEnd.Y + horizontalStart.Y + horizontalEnd.Y),
                    0.0);
                InsertAndExplodeLsdLabelBlock(database, transaction, modelSpace, editor, pair.Key, labelCenter, lsdLayer);
            }
        }

        private static void DrawDeferredLsdSubdivisionLines(
            Database database,
            IEnumerable<QuarterLabelInfo> quarterInfos,
            Logger? logger)
        {
            if (database == null || quarterInfos == null)
            {
                return;
            }

            var uniqueQuarterInfos = new List<QuarterLabelInfo>();
            var seenQuarterIds = new HashSet<ObjectId>();
            foreach (var info in quarterInfos)
            {
                if (info == null || info.QuarterId.IsNull || info.QuarterId.IsErased)
                {
                    continue;
                }

                if (seenQuarterIds.Add(info.QuarterId))
                {
                    uniqueQuarterInfos.Add(info);
                }
            }

            if (uniqueQuarterInfos.Count == 0)
            {
                return;
            }

            var scopedQuarterIds = uniqueQuarterInfos
                .Select(info => info.QuarterId)
                .Where(id => !id.IsNull && !id.IsErased)
                .Distinct()
                .ToList();

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                EnsureLayer(database, transaction, "L-SECTION-LSD");
                var scopedQuarterPolylines = new List<(Polyline Polyline, Extents3d Extents)>();
                foreach (var info in uniqueQuarterInfos)
                {
                    if (!(transaction.GetObject(info.QuarterId, OpenMode.ForRead, false) is Polyline scopedQuarter) ||
                        scopedQuarter.IsErased ||
                        !scopedQuarter.Closed ||
                        scopedQuarter.NumberOfVertices < 3)
                    {
                        continue;
                    }

                    Extents3d scopedExtents;
                    try
                    {
                        scopedExtents = scopedQuarter.GeometricExtents;
                    }
                    catch
                    {
                        continue;
                    }

                    scopedQuarterPolylines.Add((scopedQuarter, scopedExtents));
                }

                var erased = 0;

                foreach (ObjectId id in modelSpace)
                {
                    if (!(transaction.GetObject(id, OpenMode.ForRead, false) is Entity existing) || existing.IsErased)
                    {
                        continue;
                    }

                    if (!string.Equals(existing.Layer, "L-SECTION-LSD", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!TryReadOpenSegmentForDeferredLsd(existing, out var existingA, out var existingB))
                    {
                        continue;
                    }

                    if (!IsAdjustableLsdLineSegment(existingA, existingB))
                    {
                        continue;
                    }

                    if (!IsSegmentOwnedByScopedQuarters(existingA, existingB, scopedQuarterPolylines))
                    {
                        continue;
                    }

                    if (transaction.GetObject(id, OpenMode.ForWrite, false) is Entity writable && !writable.IsErased)
                    {
                        writable.Erase();
                        erased++;
                    }
                }

                var sectionAxisCache = new Dictionary<ObjectId, (Vector2d East, Vector2d North)>();
                var skipped = 0;
                var drawn = 0;

                foreach (var info in uniqueQuarterInfos)
                {
                    if (!(transaction.GetObject(info.QuarterId, OpenMode.ForRead, false) is Polyline quarter))
                    {
                        skipped++;
                        continue;
                    }

                    Vector2d eastUnit;
                    Vector2d northUnit;
                    if (!TryGetSectionAxes(
                            info.SectionPolylineId,
                            transaction,
                            sectionAxisCache,
                            out eastUnit,
                            out northUnit))
                    {
                        if (!TryGetQuarterAnchors(quarter, out var quarterAnchors))
                        {
                            quarterAnchors = GetFallbackAnchors(quarter);
                        }

                        eastUnit = GetUnitVector(quarterAnchors.Left, quarterAnchors.Right, new Vector2d(1, 0));
                        northUnit = GetUnitVector(quarterAnchors.Bottom, quarterAnchors.Top, new Vector2d(0, 1));
                    }

                    var anchors = GetLsdAnchorsForQuarter(quarter, eastUnit, northUnit);
                    var verticalStart = new Point3d(anchors.Top.X, anchors.Top.Y, 0);
                    var verticalEnd = new Point3d(anchors.Bottom.X, anchors.Bottom.Y, 0);
                    var horizontalStart = new Point3d(anchors.Left.X, anchors.Left.Y, 0);
                    var horizontalEnd = new Point3d(anchors.Right.X, anchors.Right.Y, 0);

                    if (verticalStart.DistanceTo(verticalEnd) <= 1e-4 || horizontalStart.DistanceTo(horizontalEnd) <= 1e-4)
                    {
                        skipped++;
                        continue;
                    }

                    var vertical = new Line(verticalStart, verticalEnd)
                    {
                        Layer = "L-SECTION-LSD",
                        ColorIndex = 256
                    };
                    var horizontal = new Line(horizontalStart, horizontalEnd)
                    {
                        Layer = "L-SECTION-LSD",
                        ColorIndex = 256
                    };

                    modelSpace.AppendEntity(vertical);
                    transaction.AddNewlyCreatedDBObject(vertical, true);
                    modelSpace.AppendEntity(horizontal);
                    transaction.AddNewlyCreatedDBObject(horizontal, true);
                    drawn += 2;
                }

                transaction.Commit();
                logger?.WriteLine($"Cleanup: deferred LSD draw erased={erased}, redrawn={drawn}, skipped={skipped}.");
            }
        }

        private static List<ObjectId> DrawSectionBoundaryQuarterSegmentPolylines(
            BlockTableRecord modelSpace,
            Transaction transaction,
            Point2d nw,
            Point2d ne,
            Point2d sw,
            Point2d se,
            string secType,
            IReadOnlyDictionary<QuarterSelection, string>? quarterSecTypes,
            Extents3d? clipToWindow,
            Point2d? northQuarter = null,
            Point2d? eastQuarter = null,
            Point2d? southQuarter = null,
            Point2d? westQuarter = null)
        {
            var ids = new List<ObjectId>();
            if (modelSpace == null || transaction == null)
            {
                return ids;
            }

            var wMid = westQuarter ?? Midpoint(nw, sw);
            var eMid = eastQuarter ?? Midpoint(ne, se);
            var nMid = northQuarter ?? Midpoint(nw, ne);
            var sMid = southQuarter ?? Midpoint(sw, se);

            var swType = ResolveQuarterSecTypeForQuarter(quarterSecTypes, QuarterSelection.SouthWest, secType);
            var seType = ResolveQuarterSecTypeForQuarter(quarterSecTypes, QuarterSelection.SouthEast, secType);
            var neType = ResolveQuarterSecTypeForQuarter(quarterSecTypes, QuarterSelection.NorthEast, secType);
            var nwType = ResolveQuarterSecTypeForQuarter(quarterSecTypes, QuarterSelection.NorthWest, secType);

            var segments = new[]
            {
                // Assign each half-edge from its owning quarter to avoid cross-quarter bleed.
                (A: sw, B: sMid, Layer: swType),
                (A: sMid, B: se, Layer: seType),
                (A: se, B: eMid, Layer: seType),
                (A: eMid, B: ne, Layer: neType),
                (A: ne, B: nMid, Layer: neType),
                (A: nMid, B: nw, Layer: nwType),
                (A: nw, B: wMid, Layer: nwType),
                (A: wMid, B: sw, Layer: swType),
            };

            foreach (var seg in segments)
            {
                if (!TryClipSegmentToWindow(seg.A, seg.B, clipToWindow, out var a, out var b))
                {
                    continue;
                }

                var poly = new Polyline(2)
                {
                    Layer = seg.Layer,
                    ColorIndex = 256
                };
                poly.AddVertexAt(0, a, 0, 0, 0);
                poly.AddVertexAt(1, b, 0, 0, 0);
                var id = modelSpace.AppendEntity(poly);
                transaction.AddNewlyCreatedDBObject(poly, true);
                ids.Add(id);
            }

            return ids;
        }

        private static List<ObjectId> DrawSectionBoundaryQuarterSegmentPolylines(
            BlockTableRecord modelSpace,
            Transaction transaction,
            Point2d nw,
            Point2d ne,
            Point2d sw,
            Point2d se,
            string secType,
            IReadOnlyDictionary<QuarterSelection, string>? quarterSecTypes,
            IReadOnlyList<Extents3d> clipWindows,
            Point2d? northQuarter = null,
            Point2d? eastQuarter = null,
            Point2d? southQuarter = null,
            Point2d? westQuarter = null)
        {
            if (clipWindows == null || clipWindows.Count == 0)
            {
                return DrawSectionBoundaryQuarterSegmentPolylines(
                    modelSpace, transaction, nw, ne, sw, se, secType, quarterSecTypes, (Extents3d?)null,
                    northQuarter, eastQuarter, southQuarter, westQuarter);
            }

            var ids = new List<ObjectId>();
            var wMid = westQuarter ?? Midpoint(nw, sw);
            var eMid = eastQuarter ?? Midpoint(ne, se);
            var nMid = northQuarter ?? Midpoint(nw, ne);
            var sMid = southQuarter ?? Midpoint(sw, se);

            var swType = ResolveQuarterSecTypeForQuarter(quarterSecTypes, QuarterSelection.SouthWest, secType);
            var seType = ResolveQuarterSecTypeForQuarter(quarterSecTypes, QuarterSelection.SouthEast, secType);
            var neType = ResolveQuarterSecTypeForQuarter(quarterSecTypes, QuarterSelection.NorthEast, secType);
            var nwType = ResolveQuarterSecTypeForQuarter(quarterSecTypes, QuarterSelection.NorthWest, secType);

            var segments = new[]
            {
                // Assign each half-edge from its owning quarter to avoid cross-quarter bleed.
                (A: sw, B: sMid, Layer: swType),
                (A: sMid, B: se, Layer: seType),
                (A: se, B: eMid, Layer: seType),
                (A: eMid, B: ne, Layer: neType),
                (A: ne, B: nMid, Layer: neType),
                (A: nMid, B: nw, Layer: nwType),
                (A: nw, B: wMid, Layer: nwType),
                (A: wMid, B: sw, Layer: swType),
            };

            var dedupe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var seg in segments)
            {
                foreach (var win in clipWindows)
                {
                    if (!TryClipSegmentToWindow(seg.A, seg.B, win, out var a, out var b))
                    {
                        continue;
                    }

                    var p0 = a;
                    var p1 = b;
                    if (p1.X < p0.X || (Math.Abs(p1.X - p0.X) <= 1e-9 && p1.Y < p0.Y))
                    {
                        var tmp = p0;
                        p0 = p1;
                        p1 = tmp;
                    }

                    var key = string.Format(
                        CultureInfo.InvariantCulture,
                        "{0:0.###},{1:0.###},{2:0.###},{3:0.###}",
                        p0.X, p0.Y, p1.X, p1.Y);
                    if (!dedupe.Add(key))
                    {
                        continue;
                    }

                    var poly = new Polyline(2)
                    {
                        Layer = seg.Layer,
                        ColorIndex = 256
                    };
                    poly.AddVertexAt(0, a, 0, 0, 0);
                    poly.AddVertexAt(1, b, 0, 0, 0);
                    var id = modelSpace.AppendEntity(poly);
                    transaction.AddNewlyCreatedDBObject(poly, true);
                    ids.Add(id);
                }
            }

            return ids;
        }

        private static void InsertAndExplodeLsdLabelBlock(
            Database database,
            Transaction transaction,
            BlockTableRecord modelSpace,
            Editor? editor,
            QuarterSelection quarter,
            Point3d position,
            string layerName)
        {
            var preferredName = GetLsdLabelBlockName(quarter);
            if (!TryEnsureLsdBlockLoaded(database, transaction, preferredName, editor))
            {
                editor?.WriteMessage($"\nLSD label load failed: {preferredName}");
            }

            if (!TryResolveLsdLabelBlock(database, transaction, quarter, out var blockId))
            {
                editor?.WriteMessage($"\nLSD label block not found for {quarter}.");
                return;
            }

            var blockRef = new BlockReference(position, blockId)
            {
                ScaleFactors = new Scale3d(1.0),
                Layer = layerName,
                ColorIndex = 256
            };
            modelSpace.AppendEntity(blockRef);
            transaction.AddNewlyCreatedDBObject(blockRef, true);

            var exploded = new DBObjectCollection();
            blockRef.Explode(exploded);
            var explodedEntities = new List<Entity>();
            var haveExtents = false;
            var minX = 0.0;
            var minY = 0.0;
            var maxX = 0.0;
            var maxY = 0.0;
            foreach (DBObject dbObject in exploded)
            {
                if (dbObject is Entity entity)
                {
                    if (!string.IsNullOrWhiteSpace(layerName))
                    {
                        entity.Layer = layerName;
                    }

                    entity.ColorIndex = 256;
                    modelSpace.AppendEntity(entity);
                    transaction.AddNewlyCreatedDBObject(entity, true);
                    explodedEntities.Add(entity);

                    try
                    {
                        var ext = entity.GeometricExtents;
                        if (!haveExtents)
                        {
                            minX = ext.MinPoint.X;
                            minY = ext.MinPoint.Y;
                            maxX = ext.MaxPoint.X;
                            maxY = ext.MaxPoint.Y;
                            haveExtents = true;
                        }
                        else
                        {
                            minX = Math.Min(minX, ext.MinPoint.X);
                            minY = Math.Min(minY, ext.MinPoint.Y);
                            maxX = Math.Max(maxX, ext.MaxPoint.X);
                            maxY = Math.Max(maxY, ext.MaxPoint.Y);
                        }
                    }
                    catch
                    {
                    }
                }
                else
                {
                    dbObject.Dispose();
                }
            }

            if (haveExtents && explodedEntities.Count > 0)
            {
                var center = new Point3d(
                    0.5 * (minX + maxX),
                    0.5 * (minY + maxY),
                    position.Z);
                var displacement = position - center;
                if (displacement.Length > 1e-4)
                {
                    var transform = Matrix3d.Displacement(displacement);
                    for (var i = 0; i < explodedEntities.Count; i++)
                    {
                        try
                        {
                            explodedEntities[i].TransformBy(transform);
                        }
                        catch
                        {
                        }
                    }
                }
            }

            blockRef.Erase();
            editor?.WriteMessage($"\nLSD label inserted/exploded: {preferredName} ({quarter}).");
        }

        private static bool TryEnsureLsdBlockLoaded(Database database, Transaction transaction, string blockName, Editor? editor)
        {
            if (string.IsNullOrWhiteSpace(blockName))
            {
                return false;
            }

            var bt = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
            if (bt.Has(blockName))
            {
                return true;
            }

            const string blockFolder = @"C:\AUTOCAD-SETUP CG\BLOCKS\_CG BLOCKS";
            var blockPath = Path.Combine(blockFolder, blockName + ".dwg");
            if (!File.Exists(blockPath))
            {
                editor?.WriteMessage($"\nLSD block file missing: {blockPath}");
                return false;
            }

            try
            {
                using (var sourceDb = new Database(false, true))
                {
                    sourceDb.ReadDwgFile(blockPath, FileOpenMode.OpenForReadAndAllShare, true, string.Empty);
                    database.Insert(blockName, sourceDb, false);
                }

                editor?.WriteMessage($"\nLSD block loaded: {blockName}");
                return true;
            }
            catch (System.Exception ex)
            {
                editor?.WriteMessage($"\nLSD block load exception for {blockName}: {ex.Message}");
                return false;
            }
        }

        private static bool TryResolveLsdLabelBlock(
            Database database,
            Transaction transaction,
            QuarterSelection quarter,
            out ObjectId blockId)
        {
            blockId = ObjectId.Null;

            var targetSuffix = quarter switch
            {
                QuarterSelection.NorthWest => "NW",
                QuarterSelection.NorthEast => "NE",
                QuarterSelection.SouthWest => "SW",
                QuarterSelection.SouthEast => "SE",
                _ => string.Empty
            };

            if (string.IsNullOrWhiteSpace(targetSuffix))
            {
                return false;
            }

            var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);

            // Preferred explicit names first.
            var preferred = new[]
            {
                $"label_lsd_{targetSuffix.ToLowerInvariant()}",
                $"LABEL_LSD_{targetSuffix}",
                $"label-lsd-{targetSuffix.ToLowerInvariant()}",
                $"LABEL-LSD-{targetSuffix}"
            };

            foreach (var name in preferred)
            {
                if (blockTable.Has(name))
                {
                    blockId = blockTable[name];
                    return true;
                }
            }

            // Fallback: fuzzy lookup by normalized block name.
            var targetToken = $"LABELLSD{targetSuffix}";
            foreach (ObjectId id in blockTable)
            {
                var btr = transaction.GetObject(id, OpenMode.ForRead) as BlockTableRecord;
                if (btr == null || btr.IsAnonymous || btr.IsLayout || btr.IsFromExternalReference)
                {
                    continue;
                }

                var normalized = NormalizeBlockName(btr.Name);
                if (normalized == targetToken || normalized.EndsWith(targetToken, StringComparison.Ordinal))
                {
                    blockId = id;
                    return true;
                }
            }

            return false;
        }

        private static string NormalizeBlockName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var sb = new System.Text.StringBuilder(name.Length);
            foreach (var ch in name)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(char.ToUpperInvariant(ch));
                }
            }

            return sb.ToString();
        }

        private static string GetLsdLabelBlockName(QuarterSelection quarter)
        {
            switch (quarter)
            {
                case QuarterSelection.NorthWest:
                    return "label_lsd_nw";
                case QuarterSelection.NorthEast:
                    return "label_lsd_ne";
                case QuarterSelection.SouthWest:
                    return "label_lsd_sw";
                case QuarterSelection.SouthEast:
                    return "label_lsd_se";
                default:
                    return string.Empty;
            }
        }

        private static QuarterAnchors GetLsdAnchorsForQuarter(Polyline quarter, Vector2d eastUnit, Vector2d northUnit)
        {
            if (TryGetQuarterCorner(quarter, eastUnit, northUnit, QuarterCorner.NorthWest, out var nw) &&
                TryGetQuarterCorner(quarter, eastUnit, northUnit, QuarterCorner.NorthEast, out var ne) &&
                TryGetQuarterCorner(quarter, eastUnit, northUnit, QuarterCorner.SouthWest, out var sw) &&
                TryGetQuarterCorner(quarter, eastUnit, northUnit, QuarterCorner.SouthEast, out var se))
            {
                return new QuarterAnchors(
                    Midpoint(nw, ne), // top
                    Midpoint(sw, se), // bottom
                    Midpoint(nw, sw), // left
                    Midpoint(ne, se)); // right
            }

            // Fallback: quarter extents midpoint anchors.
            return GetFallbackAnchors(quarter);
        }

        private static void DrawBufferedQuarterWindowsOnDefpoints(
            Database database,
            IEnumerable<ObjectId> quarterIds,
            double buffer,
            Logger? logger)
        {
            if (database == null || quarterIds == null)
            {
                return;
            }

            var quarterIdList = quarterIds.Distinct().ToList();
            logger?.WriteLine($"DEFPOINTS BUFFER: requested quarter ids = {quarterIdList.Count}, buffer={buffer:0.###}m");
            var offsetPolylines = BuildBufferedQuarterOffsetPolylines(database, quarterIdList, buffer, logger);
            if (offsetPolylines.Count == 0)
            {
                logger?.WriteLine("DEFPOINTS BUFFER: no offset polylines created.");
                return;
            }

            List<Polyline> unionBoundaries;
            try
            {
                unionBoundaries = BuildUnionBoundaries(offsetPolylines, logger);
            }
            finally
            {
                foreach (var poly in offsetPolylines)
                {
                    poly.Dispose();
                }
            }

            if (unionBoundaries.Count == 0)
            {
                logger?.WriteLine("DEFPOINTS BUFFER: union produced 0 boundaries.");
                return;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                EnsureDefpointsLayer(database, tr);
                ClearPreviousBufferedDefpointsWindows(tr);

                var count = 0;
                var newWindowIds = new List<ObjectId>();
                foreach (var boundary in unionBoundaries)
                {
                    try
                    {
                        logger?.WriteLine($"DEFPOINTS BUFFER: draw boundary area={Math.Abs(boundary.Area):0.###} verts={boundary.NumberOfVertices}");
                    }
                    catch
                    {
                    }

                    boundary.Layer = "DEFPOINTS";
                    boundary.ColorIndex = 8;
                    var windowId = ms.AppendEntity(boundary);
                    tr.AddNewlyCreatedDBObject(boundary, true);
                    newWindowIds.Add(windowId);
                    count++;
                }

                tr.Commit();
                lock (BufferedDefpointsWindowLock)
                {
                    BufferedDefpointsWindowIds.Clear();
                    foreach (var id in newWindowIds)
                    {
                        if (!id.IsNull)
                        {
                            BufferedDefpointsWindowIds.Add(id);
                        }
                    }
                }
                logger?.WriteLine($"Buffered DEFPOINTS windows drawn: {count} outline(s).");
            }

            foreach (var boundary in unionBoundaries)
            {
                if (!boundary.IsDisposed && boundary.Database == null)
                {
                    boundary.Dispose();
                }
            }
        }

        private static void ClearPreviousBufferedDefpointsWindows(Transaction tr)
        {
            if (tr == null)
            {
                return;
            }

            List<ObjectId> previousIds;
            lock (BufferedDefpointsWindowLock)
            {
                previousIds = BufferedDefpointsWindowIds
                    .Where(id => !id.IsNull)
                    .Distinct()
                    .ToList();
                BufferedDefpointsWindowIds.Clear();
            }

            foreach (var id in previousIds)
            {
                try
                {
                    if (tr.GetObject(id, OpenMode.ForWrite, false) is Entity entity && !entity.IsErased)
                    {
                        entity.Erase();
                    }
                }
                catch
                {
                }
            }
        }
    }
}
