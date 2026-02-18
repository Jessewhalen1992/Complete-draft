using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
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

                    double ProjectU(Point2d p) => (p - origin).DotProduct(eastUnit);
                    double ProjectV(Point2d p) => (p - origin).DotProduct(northUnit);

                    var westEdgeU = 0.5 * (ProjectU(swCorner) + ProjectU(nwCorner));
                    var eastEdgeU = 0.5 * (ProjectU(seCorner) + ProjectU(neCorner));
                    var southEdgeV = 0.5 * (ProjectV(swCorner) + ProjectV(seCorner));
                    var northEdgeV = 0.5 * (ProjectV(nwCorner) + ProjectV(neCorner));

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

                    var midU = 0.5 * (ProjectU(anchors.Top) + ProjectU(anchors.Bottom));
                    var midV = 0.5 * (ProjectV(anchors.Left) + ProjectV(anchors.Right));
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
                        cleanupWindow));
                }

                if (frames.Count == 0)
                {
                    transaction.Commit();
                    return;
                }

                bool SegmentIntersectsAnyQuarterWindow(Point2d a, Point2d b)
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

                bool ExtentsIntersectAnyQuarterWindow(Extents3d extents)
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

                bool TryFindQuarterVertexSnapTarget(
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

                    const double maxSnapDistance = 3.0;
                    const double lineMatchTolerance = 1.25;
                    const double minMove = 0.01;
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
                        if (prevLineDistance > lineMatchTolerance)
                        {
                            continue;
                        }

                        var nextLineDistance = DistancePointToInfiniteLine(candidate, vertex, next);
                        if (nextLineDistance > lineMatchTolerance)
                        {
                            continue;
                        }

                        if (!found || moveDistance < bestDistance)
                        {
                            found = true;
                            bestDistance = moveDistance;
                            target = candidate;
                        }
                    }

                    return found;
                }

                var boundarySegments = new List<(Point2d A, Point2d B, string Layer)>();
                foreach (ObjectId id in modelSpace)
                {
                    if (!(transaction.GetObject(id, OpenMode.ForRead, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    if (!IsQuarterViewBoundaryCandidateLayer(ent.Layer))
                    {
                        continue;
                    }

                    if (!TryReadOpenLinearSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    if (!SegmentIntersectsAnyQuarterWindow(a, b))
                    {
                        continue;
                    }

                    boundarySegments.Add((a, b, ent.Layer ?? string.Empty));
                }

                var hardBoundaryCornerClusters = new List<(Point2d Rep, int Count, bool HasHorizontal, bool HasVertical)>();
                const double cornerClusterTolerance = 0.40;
                void AddBoundaryEndpointToCornerClusters(Point2d endpoint, bool isHorizontal, bool isVertical)
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
                        hardBoundaryCornerClusters.Add((endpoint, 1, isHorizontal, isVertical));
                        return;
                    }

                    var existing = hardBoundaryCornerClusters[bestIndex];
                    var newCount = existing.Count + 1;
                    var newRep = new Point2d(
                        ((existing.Rep.X * existing.Count) + endpoint.X) / newCount,
                        ((existing.Rep.Y * existing.Count) + endpoint.Y) / newCount);
                    hardBoundaryCornerClusters[bestIndex] = (
                        newRep,
                        newCount,
                        existing.HasHorizontal || isHorizontal,
                        existing.HasVertical || isVertical);
                }

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
                    AddBoundaryEndpointToCornerClusters(seg.A, isHorizontal, isVertical);
                    AddBoundaryEndpointToCornerClusters(seg.B, isHorizontal, isVertical);
                }
                var hardBoundaryCornerEndpoints = hardBoundaryCornerClusters
                    .Where(c => c.HasHorizontal && c.HasVertical)
                    .Select(c => c.Rep)
                    .ToList();

                var erased = 0;
                foreach (ObjectId id in modelSpace)
                {
                    if (!(transaction.GetObject(id, OpenMode.ForWrite, false) is Entity ent) || ent.IsErased)
                    {
                        continue;
                    }

                    if (!string.Equals(ent.Layer, LayerQuarterView, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!(ent is Polyline poly) || !poly.Closed)
                    {
                        continue;
                    }

                    Extents3d extents;
                    try
                    {
                        extents = poly.GeometricExtents;
                    }
                    catch
                    {
                        continue;
                    }

                    if (!ExtentsIntersectAnyQuarterWindow(extents))
                    {
                        continue;
                    }

                    ent.Erase();
                    erased++;
                }

                var drawn = 0;
                foreach (var frame in frames)
                {
                    var westExpectedOffset = RoadAllowanceUsecWidthMeters;
                    var westBoundaryU = frame.WestEdgeU - westExpectedOffset;
                    var southFallbackOffset = IsBlindSouthBoundarySectionForQuarterView(frame.SectionNumber)
                        ? 0.0
                        : RoadAllowanceUsecWidthMeters;
                    var southBoundaryV = frame.SouthEdgeV - southFallbackOffset;
                    var westSource = "fallback-30.16";
                    var southSource = southFallbackOffset > 0.0 ? "fallback-30.16" : "fallback-blind";
                    var hasResolvedWest = false;

                    hasResolvedWest = TryResolveQuarterViewWestBoundaryU(
                        frame,
                        boundarySegments,
                        westExpectedOffset,
                        out var resolvedWestU,
                        out var resolvedWestLayer);
                    if (hasResolvedWest)
                    {
                        westBoundaryU = resolvedWestU;
                        westSource = resolvedWestLayer;
                    }

                    if (TryResolveQuarterViewSouthBoundaryV(
                        frame,
                        boundarySegments,
                        southFallbackOffset,
                        out var resolvedSouthV,
                        out var resolvedSouthLayer))
                    {
                        southBoundaryV = resolvedSouthV;
                        southSource = resolvedSouthLayer;
                    }

                    westBoundaryU = Math.Min(westBoundaryU, frame.WestEdgeU);
                    southBoundaryV = Math.Min(southBoundaryV, frame.SouthEdgeV);

                    if ((frame.MidU - westBoundaryU) <= minQuarterSpan)
                    {
                        westBoundaryU = Math.Min(frame.WestEdgeU, frame.MidU - minQuarterSpan);
                    }

                    if ((frame.MidV - southBoundaryV) <= minQuarterSpan)
                    {
                        southBoundaryV = Math.Min(frame.SouthEdgeV, frame.MidV - minQuarterSpan);
                    }

                    var westSouthV = southBoundaryV;
                    var westNorthV = frame.NorthEdgeV;
                    westSouthV = Math.Min(westSouthV, frame.MidV - minQuarterSpan);
                    westNorthV = Math.Max(westNorthV, frame.MidV + minQuarterSpan);

                    var swNw = QuarterViewLocalToWorld(frame, westBoundaryU, frame.MidV);
                    var swNe = QuarterViewLocalToWorld(frame, frame.MidU, frame.MidV);
                    var swSe = QuarterViewLocalToWorld(frame, frame.MidU, southBoundaryV);
                    var swSw = QuarterViewLocalToWorld(frame, westBoundaryU, westSouthV);
                    var nwW = QuarterViewLocalToWorld(frame, westBoundaryU, westNorthV);
                    logger?.WriteLine(
                        $"Quarter view SW coords sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                        $"{swNw.X:0.###},{swNw.Y:0.###} > " +
                        $"{swNe.X:0.###},{swNe.Y:0.###} > " +
                        $"{swSe.X:0.###},{swSe.Y:0.###} > " +
                        $"{swSw.X:0.###},{swSw.Y:0.###}");
                    logger?.WriteLine(
                        $"Quarter view west corners sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                        $"SW={swSw.X:0.###},{swSw.Y:0.###} NW={nwW.X:0.###},{nwW.Y:0.###}");

                    // SW: west + south RA.
                    drawn += DrawQuarterViewPolygonFromLocal(
                        modelSpace,
                        transaction,
                        frame,
                        new Point2d(westBoundaryU, frame.MidV),
                        new Point2d(frame.MidU, frame.MidV),
                        new Point2d(frame.MidU, southBoundaryV),
                        new Point2d(westBoundaryU, westSouthV));

                    // SE: south RA only.
                    drawn += DrawQuarterViewPolygonFromLocal(
                        modelSpace,
                        transaction,
                        frame,
                        new Point2d(frame.MidU, frame.MidV),
                        new Point2d(frame.EastEdgeU, frame.MidV),
                        new Point2d(frame.EastEdgeU, southBoundaryV),
                        new Point2d(frame.MidU, southBoundaryV));

                    // NW: west RA only.
                    drawn += DrawQuarterViewPolygonFromLocal(
                        modelSpace,
                        transaction,
                        frame,
                        new Point2d(westBoundaryU, westNorthV),
                        new Point2d(frame.MidU, frame.NorthEdgeV),
                        new Point2d(frame.MidU, frame.MidV),
                        new Point2d(westBoundaryU, frame.MidV));

                    // NE: no RA.
                    drawn += DrawQuarterViewPolygonFromLocal(
                        modelSpace,
                        transaction,
                        frame,
                        new Point2d(frame.MidU, frame.NorthEdgeV),
                        new Point2d(frame.EastEdgeU, frame.NorthEdgeV),
                        new Point2d(frame.EastEdgeU, frame.MidV),
                        new Point2d(frame.MidU, frame.MidV));

                    logger?.WriteLine(
                        $"Quarter view section {frame.SectionId.Handle}: west={westSource} ({westBoundaryU:0.###}), south={southSource} ({southBoundaryV:0.###}).");
                }

                var snappedVertices = 0;
                var snappedBoxes = 0;
                foreach (ObjectId id in modelSpace)
                {
                    if (!(transaction.GetObject(id, OpenMode.ForWrite, false) is Polyline box) || box.IsErased || !box.Closed)
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

                    if (!ExtentsIntersectAnyQuarterWindow(extents) || box.NumberOfVertices < 3)
                    {
                        continue;
                    }

                    var movedAny = false;
                    for (var vi = 0; vi < box.NumberOfVertices; vi++)
                    {
                        var vertex = box.GetPoint2dAt(vi);
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

                logger?.WriteLine($"Quarter view: refreshed {drawn} quarter box(es) across {frames.Count} section(s); erased {erased} stale box(es).");
                logger?.WriteLine($"Quarter view: endpoint snap adjusted {snappedVertices} vertex/vertices across {snappedBoxes} quarter box(es).");
                transaction.Commit();
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
                   string.Equals(layerName, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(layerName, "L-SEC-2012", StringComparison.OrdinalIgnoreCase);
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

        private static bool TryResolveQuarterViewWestBoundaryU(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> segments,
            double expectedOffsetMeters,
            out double boundaryU,
            out string sourceLayer)
        {
            boundaryU = default;
            sourceLayer = string.Empty;

            const double axisTolerance = 0.5;
            const double overlapPadding = 16.0;
            const double minProjectedOverlap = 20.0;
            const double maxOffset = 40.0;
            const double minRoadAllowanceOffset = 5.0;

            var bestPriority = int.MaxValue;
            var bestScore = double.MaxValue;
            var bestOutwardDistance = double.MaxValue;
            foreach (var segment in segments)
            {
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
                var outwardDistance = frame.WestEdgeU - uLine;
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

                var score = Math.Abs(outwardDistance - targetOffset);
                if (score < bestScore ||
                    (Math.Abs(score - bestScore) <= 1e-6 && priority < bestPriority) ||
                    (Math.Abs(score - bestScore) <= 1e-6 && priority == bestPriority && outwardDistance < bestOutwardDistance))
                {
                    bestPriority = priority;
                    bestScore = score;
                    bestOutwardDistance = outwardDistance;
                    boundaryU = uLine;
                    sourceLayer = segment.Layer;
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
            out double boundaryV,
            out string sourceLayer)
        {
            boundaryV = default;
            sourceLayer = string.Empty;

            const double axisTolerance = 0.5;
            const double overlapPadding = 16.0;
            const double minProjectedOverlap = 20.0;
            const double maxOffset = 40.0;
            const double minRoadAllowanceOffset = 5.0;

            var bestPriority = int.MaxValue;
            var bestScore = double.MaxValue;
            var bestOutwardDistance = double.MaxValue;
            foreach (var segment in segments)
            {
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
                var outwardDistance = frame.SouthEdgeV - vLine;
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

                var score = Math.Abs(outwardDistance - targetOffset);
                if (score < bestScore ||
                    (Math.Abs(score - bestScore) <= 1e-6 && priority < bestPriority) ||
                    (Math.Abs(score - bestScore) <= 1e-6 && priority == bestPriority && outwardDistance < bestOutwardDistance))
                {
                    bestPriority = priority;
                    bestScore = score;
                    bestOutwardDistance = outwardDistance;
                    boundaryV = vLine;
                    sourceLayer = segment.Layer;
                }
            }

            return bestPriority != int.MaxValue;
        }

        private static Point2d QuarterViewLocalToWorld(QuarterViewSectionFrame frame, double u, double v)
        {
            return frame.Origin + (frame.EastUnit * u) + (frame.NorthUnit * v);
        }

        private static int DrawQuarterViewPolygonFromLocal(
            BlockTableRecord modelSpace,
            Transaction transaction,
            QuarterViewSectionFrame frame,
            params Point2d[] localPoints)
        {
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
            return 1;
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
            // Redraw only within the exact requested quarter extents so adjacent
            // pre-existing LSD lines are never erased during adjoining builds.
            var clipWindows = BuildBufferedQuarterWindows(database, scopedQuarterIds, 0.0);

            bool TryReadOpenSegment(Entity ent, out Point2d a, out Point2d b)
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

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b)
            {
                if (clipWindows.Count == 0)
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

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                EnsureLayer(database, transaction, "L-SECTION-LSD");

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

                    if (!TryReadOpenSegment(existing, out var existingA, out var existingB))
                    {
                        continue;
                    }

                    if (!IsAdjustableLsdLineSegment(existingA, existingB))
                    {
                        continue;
                    }

                    if (!DoesSegmentIntersectAnyWindow(existingA, existingB))
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

                bool TryGetSectionAxes(ObjectId sectionId, out Vector2d eastUnit, out Vector2d northUnit)
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

                foreach (var info in uniqueQuarterInfos)
                {
                    if (!(transaction.GetObject(info.QuarterId, OpenMode.ForRead, false) is Polyline quarter))
                    {
                        skipped++;
                        continue;
                    }

                    Vector2d eastUnit;
                    Vector2d northUnit;
                    if (!TryGetSectionAxes(info.SectionPolylineId, out eastUnit, out northUnit))
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
