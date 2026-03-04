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

                    const double strictMaxSnapDistance = 30.0;
                    const double strictLineMatchTolerance = 1.25;
                    const double relaxedMaxSnapDistance = 35.0;
                    const double relaxedLineMatchTolerance = 8.0;
                    const double relaxedSingleEdgeMissMaxDistance = 10.0;
                    const double nearestEdgeMissMaxDistance = 10.0;
                    const double minMove = 0.01;

                    bool TryPick(bool requireBothEdgeMatches, double lineMatchTolerance, double maxSnapDistance, out Point2d picked)
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

                    if (TryPick(requireBothEdgeMatches: true, strictLineMatchTolerance, strictMaxSnapDistance, out var strictTarget))
                    {
                        target = strictTarget;
                        return true;
                    }

                    if (TryPick(requireBothEdgeMatches: false, relaxedLineMatchTolerance, relaxedMaxSnapDistance, out var relaxedTarget))
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

                bool TryResolveSouthDividerCornerFromHardBoundaries(
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

                bool TryResolveWestBandCornerFromHardBoundaries(
                    QuarterViewSectionFrame frame,
                    IReadOnlyList<(Point2d Rep, int Count, bool HasHorizontal, bool HasVertical, int Priority)> cornerClusters,
                    double preferredWestU,
                    bool northBand,
                    Point2d currentLocal,
                    double maxMove,
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

                        var score =
                            (cluster.Priority * 100.0) +
                            (westGap * 4.0) +
                            (edgeGap * 3.0) +
                            (move * 0.5) -
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

                bool TryResolveEastBandCornerFromHardBoundaries(
                    QuarterViewSectionFrame frame,
                    IReadOnlyList<(Point2d Rep, int Count, bool HasHorizontal, bool HasVertical, int Priority)> cornerClusters,
                    double preferredEastU,
                    bool northBand,
                    Point2d currentLocal,
                    double maxMove,
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

                        var score =
                            (cluster.Priority * 100.0) +
                            (eastGap * 4.0) +
                            (edgeGap * 3.0) +
                            (move * 0.5) -
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

                bool TryResolveNorthEastCornerFromEastHardNode(
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

                bool TryResolveNorthEastCornerFromEndpointCornerClusters(
                    QuarterViewSectionFrame frame,
                    IReadOnlyList<(Point2d A, Point2d B, string Layer)> segments,
                    IReadOnlyList<(Point2d Rep, int Count, bool HasHorizontal, bool HasVertical, int Priority)> cornerClusters,
                    Point2d currentLocal,
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

                    bool IsBoundaryCandidateLayer(string layer) => IsQuarterViewBoundaryCandidateLayer(layer);
                    bool IsHorizontal(Point2d a, Point2d b)
                    {
                        var d = b - a;
                        var eastComp = Math.Abs(d.DotProduct(frame.EastUnit));
                        var northComp = Math.Abs(d.DotProduct(frame.NorthUnit));
                        return eastComp > northComp;
                    }

                    bool IsVertical(Point2d a, Point2d b)
                    {
                        var d = b - a;
                        var eastComp = Math.Abs(d.DotProduct(frame.EastUnit));
                        var northComp = Math.Abs(d.DotProduct(frame.NorthUnit));
                        return northComp > eastComp;
                    }

                    bool HasEndpointHorizontalAndVerticalAt(Point2d worldPoint, double tol, out double bestEndpointDistance)
                    {
                        bestEndpointDistance = double.MaxValue;
                        var hasHorizontal = false;
                        var hasVertical = false;
                        for (var si = 0; si < segments.Count; si++)
                        {
                            var seg = segments[si];
                            if (!IsBoundaryCandidateLayer(seg.Layer))
                            {
                                continue;
                            }

                            var segmentIsHorizontal = IsHorizontal(seg.A, seg.B);
                            var segmentIsVertical = IsVertical(seg.A, seg.B);
                            if (!segmentIsHorizontal && !segmentIsVertical)
                            {
                                continue;
                            }

                            var dA = worldPoint.GetDistanceTo(seg.A);
                            if (dA <= tol)
                            {
                                bestEndpointDistance = Math.Min(bestEndpointDistance, dA);
                                if (segmentIsHorizontal)
                                {
                                    hasHorizontal = true;
                                }

                                if (segmentIsVertical)
                                {
                                    hasVertical = true;
                                }
                            }

                            var dB = worldPoint.GetDistanceTo(seg.B);
                            if (dB <= tol)
                            {
                                bestEndpointDistance = Math.Min(bestEndpointDistance, dB);
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

                    const double maxMove = 140.0;
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
                            v < (frame.MidV - 30.0) || v > (frame.NorthEdgeV + 140.0))
                        {
                            continue;
                        }

                        var candidateLocal = new Point2d(u, v);
                        var move = candidateLocal.GetDistanceTo(currentLocal);
                        if (move <= 1e-3 || move > maxMove)
                        {
                            continue;
                        }

                        var hasEndpointNode = HasEndpointHorizontalAndVerticalAt(cluster.Rep, endpointTol, out var endpointDistance);
                        if (requireEndpointNode && !hasEndpointNode)
                        {
                            continue;
                        }

                        var eastInset = frame.EastEdgeU - u;
                        var northInset = frame.NorthEdgeV - v;
                        var insetTarget = RoadAllowanceUsecWidthMeters - RoadAllowanceSecWidthMeters;
                        var insetScore = Math.Abs(eastInset - insetTarget) + Math.Abs(northInset - insetTarget);
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

                var boundarySegments = new List<(Point2d A, Point2d B, string Layer)>();
                var quarterDividerSegments = new List<(Point2d A, Point2d B)>();
                var correctionSouthBoundarySegments = new List<(Point2d A, Point2d B)>();
                var correctionNorthBoundarySegments = new List<(Point2d A, Point2d B, string Layer)>();
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

                    if (!SegmentIntersectsAnyQuarterWindow(a, b))
                    {
                        continue;
                    }

                    if (string.Equals(ent.Layer, "L-QSEC", StringComparison.OrdinalIgnoreCase))
                    {
                        quarterDividerSegments.Add((a, b));
                        continue;
                    }

                    if (!IsQuarterViewBoundaryCandidateLayer(ent.Layer))
                    {
                        continue;
                    }

                    if (string.Equals(ent.Layer, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase))
                    {
                        correctionSouthBoundarySegments.Add((a, b));
                        correctionNorthBoundarySegments.Add((a, b, LayerUsecCorrectionZero));
                    }
                    else if (string.Equals(ent.Layer, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase))
                    {
                        correctionNorthBoundarySegments.Add((a, b, LayerUsecCorrection));
                    }

                    boundarySegments.Add((a, b, ent.Layer ?? string.Empty));
                }

                var hardBoundaryCornerClusters = new List<(Point2d Rep, int Count, bool HasHorizontal, bool HasVertical, int Priority)>();
                const double cornerClusterTolerance = 0.40;
                void AddBoundaryEndpointToCornerClusters(Point2d endpoint, bool isHorizontal, bool isVertical, int priority)
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

                bool IsPointInAnyQuarterWindow(Point2d p, double tol)
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

                bool TryFindApparentCornerIntersection(
                    Point2d firstA,
                    Point2d firstB,
                    Point2d secondA,
                    Point2d secondB,
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
                    var u = ((diff.X * r.Y) - (diff.Y * r.X)) / denom;
                    var hit = new Point2d(firstA.X + (r.X * t), firstA.Y + (r.Y * t));

                    const double apparentIntersectionPadding = 80.0;
                    bool WithinExpandedBounds(Point2d a, Point2d b)
                    {
                        var minX = Math.Min(a.X, b.X) - apparentIntersectionPadding;
                        var maxX = Math.Max(a.X, b.X) + apparentIntersectionPadding;
                        var minY = Math.Min(a.Y, b.Y) - apparentIntersectionPadding;
                        var maxY = Math.Max(a.Y, b.Y) + apparentIntersectionPadding;
                        return hit.X >= minX && hit.X <= maxX && hit.Y >= minY && hit.Y <= maxY;
                    }

                    if (!WithinExpandedBounds(firstA, firstB) || !WithinExpandedBounds(secondA, secondB))
                    {
                        return false;
                    }

                    if (!IsPointInAnyQuarterWindow(hit, 0.01))
                    {
                        return false;
                    }

                    corner = hit;
                    return true;
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
                    AddBoundaryEndpointToCornerClusters(seg.A, isHorizontal, isVertical, priority: 1);
                    AddBoundaryEndpointToCornerClusters(seg.B, isHorizontal, isVertical, priority: 1);
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

                        if (!TryFindApparentCornerIntersection(first.A, first.B, second.A, second.B, out var corner))
                        {
                            continue;
                        }

                        AddBoundaryEndpointToCornerClusters(corner, isHorizontal: true, isVertical: true, priority: 0);
                    }
                }

                var hardBoundaryCornerEndpoints = hardBoundaryCornerClusters
                    .Where(c => c.HasHorizontal && c.HasVertical)
                    .Select(c => c.Rep)
                    .ToList();

                var erased = 0;
                bool IsPointInsideFrame(QuarterViewSectionFrame frame, Point2d worldPoint, double tol)
                {
                    if (!TryConvertQuarterWorldToLocal(frame, worldPoint, out var u, out var v))
                    {
                        return false;
                    }

                    return u >= (frame.WestEdgeU - tol) && u <= (frame.EastEdgeU + tol) &&
                           v >= (frame.SouthEdgeV - tol) && v <= (frame.NorthEdgeV + tol);
                }

                bool IsPolylineOwnedByAnyRebuiltSection(Polyline poly)
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
                    const double centroidTol = 0.75;
                    const double vertexTol = 0.35;
                    for (var i = 0; i < frames.Count; i++)
                    {
                        var frame = frames[i];
                        if (!IsPointInsideFrame(frame, centroid, centroidTol))
                        {
                            continue;
                        }

                        var allVerticesInside = true;
                        for (var vi = 0; vi < poly.NumberOfVertices; vi++)
                        {
                            if (!IsPointInsideFrame(frame, poly.GetPoint2dAt(vi), vertexTol))
                            {
                                allVerticesInside = false;
                                break;
                            }
                        }

                        if (allVerticesInside)
                        {
                            return true;
                        }
                    }

                    return false;
                }

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

                    if (!(ent is Polyline poly) || !poly.Closed || poly.NumberOfVertices < 3)
                    {
                        continue;
                    }

                    if (!IsPolylineOwnedByAnyRebuiltSection(poly))
                    {
                        continue;
                    }

                    ent.Erase();
                    erased++;
                }

                var drawn = 0;
                var protectedSouthMidCorners = new List<Point2d>();
                var protectedWestBoundaryCorners = new List<Point2d>();
                var protectedEastBoundaryCorners = new List<Point2d>();
                foreach (var frame in frames)
                {
                    var emitQuarterVerify = frame.SectionNumber == 6 ||
                                            frame.SectionNumber == 12 ||
                                            frame.SectionNumber == 11 ||
                                            frame.SectionNumber == 36;
                    var dividerLineA = frame.BottomAnchor;
                    var dividerLineB = frame.TopAnchor;
                    var dividerSource = "anchors";
                    if (TryResolveQuarterViewVerticalDividerSegmentFromQsec(
                            frame,
                            quarterDividerSegments,
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

                    var quarterDirectionInset = RoadAllowanceUsecWidthMeters - RoadAllowanceSecWidthMeters;
                    var westExpectedOffset = quarterDirectionInset;
                    var westBoundaryU = frame.WestEdgeU - westExpectedOffset;
                    var southFallbackOffset = IsBlindSouthBoundarySectionForQuarterView(frame.SectionNumber)
                        ? 0.0
                        : quarterDirectionInset;
                    var southBoundaryV = frame.SouthEdgeV - southFallbackOffset;
                    var westSource = "fallback-20.12";
                    var southSource = southFallbackOffset > 0.0 ? "fallback-20.12" : "fallback-blind";
                    var hasResolvedWest = false;
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

                    hasResolvedWest = TryResolveQuarterViewWestBoundaryU(
                        frame,
                        boundarySegments,
                        westExpectedOffset,
                        dividerPreferredU,
                        out var resolvedWestU,
                        out var resolvedWestLayer,
                        out var resolvedWestA,
                        out var resolvedWestB);
                    if (hasResolvedWest)
                    {
                        westBoundaryU = resolvedWestU;
                        westSource = resolvedWestLayer;
                        westBoundarySegmentA = resolvedWestA;
                        westBoundarySegmentB = resolvedWestB;
                        hasWestBoundarySegment = true;
                    }

                    bool TryResolvePreferredWestBoundary(
                        IReadOnlyList<(Point2d A, Point2d B, string Layer)> candidates,
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
                                westExpectedOffset,
                                dividerPreferredU,
                                out resolvedU,
                                out resolvedLayer,
                                out resolvedA,
                                out resolvedB))
                        {
                            return true;
                        }

                        // Regression guard: if geometry has already shifted section edge inward,
                        // a zero expected offset still selects the intended 20.12-class boundary.
                        return TryResolveQuarterViewWestBoundaryU(
                            frame,
                            candidates,
                            0.0,
                            dividerPreferredU,
                            out resolvedU,
                            out resolvedLayer,
                            out resolvedA,
                            out resolvedB);
                    }

                    if (hasWestBoundarySegment &&
                        string.Equals(westSource, LayerUsecZero, StringComparison.OrdinalIgnoreCase))
                    {
                        var preferredWestSegments = boundarySegments
                            .Where(s =>
                                string.Equals(s.Layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(s.Layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(s.Layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(s.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(s.Layer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        if (TryResolvePreferredWestBoundary(
                                preferredWestSegments,
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
                    }

                    bool TryResolvePreferredEastBoundarySegment(
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
                        for (var li = 0; li < preferredEastLayers.Length; li++)
                        {
                            var layer = preferredEastLayers[li];
                            if (!TryResolveQuarterViewEastBoundarySegmentOnLayer(
                                    frame,
                                    boundarySegments,
                                    layer,
                                    out resolvedEastA,
                                    out resolvedEastB))
                            {
                                continue;
                            }

                            resolvedEastLayer = layer;
                            return true;
                        }

                        return false;
                    }

                    if (TryResolvePreferredEastBoundarySegment(
                            out var resolvedEastA,
                            out var resolvedEastB,
                            out var resolvedEastLayer))
                    {
                        var relEastA = resolvedEastA - frame.Origin;
                        var relEastB = resolvedEastB - frame.Origin;
                        eastBoundaryU = 0.5 * (
                            relEastA.DotProduct(frame.EastUnit) +
                            relEastB.DotProduct(frame.EastUnit));
                        if (TryProjectBoundarySegmentUAtV(
                                frame,
                                resolvedEastA,
                                resolvedEastB,
                                frame.MidV,
                                out var projectedEastMidU))
                        {
                            eastBoundaryU = projectedEastMidU;
                        }

                        eastSource = resolvedEastLayer;
                        eastBoundarySegmentA = resolvedEastA;
                        eastBoundarySegmentB = resolvedEastB;
                        hasEastBoundarySegment = true;
                    }

                    if (TryResolveQuarterViewSouthCorrectionBoundaryV(
                        frame,
                        correctionSouthBoundarySegments,
                        out var correctionSouthV,
                        out var correctionSouthA,
                        out var correctionSouthB))
                    {
                        southBoundaryV = correctionSouthV;
                        southSource = LayerUsecCorrectionZero;
                        southBoundarySegmentA = correctionSouthA;
                        southBoundarySegmentB = correctionSouthB;
                        hasSouthBoundarySegment = true;
                    }
                    else if (TryResolveQuarterViewSouthBoundaryV(
                                 frame,
                                 boundarySegments,
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

                    if (!IsBlindSouthBoundarySectionForQuarterView(frame.SectionNumber) &&
                        hasSouthBoundarySegment &&
                        string.Equals(southSource, LayerUsecZero, StringComparison.OrdinalIgnoreCase))
                    {
                        bool TryResolvePreferredSouthBoundary(
                            IReadOnlyList<(Point2d A, Point2d B, string Layer)> candidates,
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
                                    quarterDirectionInset,
                                    dividerPreferredU,
                                    dividerLineA,
                                    dividerLineB,
                                    out resolvedV,
                                    out resolvedLayer,
                                    out resolvedA,
                                    out resolvedB))
                            {
                                return true;
                            }

                            // Regression guard: allow 20.12-class fallback even when section-edge
                            // normalization already consumed the expected inset.
                            return TryResolveQuarterViewSouthBoundaryV(
                                frame,
                                candidates,
                                0.0,
                                dividerPreferredU,
                                dividerLineA,
                                dividerLineB,
                                out resolvedV,
                                out resolvedLayer,
                                out resolvedA,
                                out resolvedB);
                        }

                        var preferredSouthSegments = boundarySegments
                            .Where(s =>
                                string.Equals(s.Layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(s.Layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(s.Layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(s.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(s.Layer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        if (TryResolvePreferredSouthBoundary(
                                preferredSouthSegments,
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
                    }

                    if (TryResolveQuarterViewNorthCorrectionBoundaryV(
                        frame,
                        correctionNorthBoundarySegments,
                        out var correctionNorthV,
                        out var correctionNorthLayer,
                        out var correctionNorthA,
                        out var correctionNorthB))
                    {
                        northBoundaryV = correctionNorthV;
                        northSource = correctionNorthLayer;
                        northBoundarySegmentA = correctionNorthA;
                        northBoundarySegmentB = correctionNorthB;
                        hasNorthBoundarySegment = true;
                    }
                    else if (TryResolveQuarterViewNorthBoundaryV(
                                 frame,
                                 boundarySegments,
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

                    if (IsBlindSouthBoundarySectionForQuarterView(frame.SectionNumber) &&
                        hasSouthBoundarySegment &&
                        hasNorthBoundarySegment &&
                        TryResolveQuarterViewEastBoundarySegmentFromNorthSouth(
                            frame,
                            boundarySegments,
                            southBoundarySegmentA,
                            southBoundarySegmentB,
                            northBoundarySegmentA,
                            northBoundarySegmentB,
                            out var refinedEastA,
                            out var refinedEastB,
                            out var refinedEastLayer,
                            out var refinedEastOffset,
                            out var refinedSouthOffset,
                            out var refinedNorthOffset))
                    {
                        eastBoundarySegmentA = refinedEastA;
                        eastBoundarySegmentB = refinedEastB;
                        eastSource = refinedEastLayer;
                        hasEastBoundarySegment = true;
                        if (TryProjectBoundarySegmentUAtV(
                                frame,
                                eastBoundarySegmentA,
                                eastBoundarySegmentB,
                                frame.MidV,
                                out var refinedEastMidU))
                        {
                            eastBoundaryU = refinedEastMidU;
                        }
                        else
                        {
                            if (TryConvertQuarterWorldToLocal(frame, eastBoundarySegmentA, out var eastAu, out _) &&
                                TryConvertQuarterWorldToLocal(frame, eastBoundarySegmentB, out var eastBu, out _))
                            {
                                eastBoundaryU = 0.5 * (eastAu + eastBu);
                            }
                        }

                        if (emitQuarterVerify)
                        {
                            logger?.WriteLine(
                                $"VERIFY-QTR-EAST-SELECT sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                $"eastOffset={refinedEastOffset:0.###} southOffset={refinedSouthOffset:0.###} northOffset={refinedNorthOffset:0.###} " +
                                $"seg={eastBoundarySegmentA.X:0.###},{eastBoundarySegmentA.Y:0.###}->{eastBoundarySegmentB.X:0.###},{eastBoundarySegmentB.Y:0.###} source={eastSource}");
                        }
                    }
                    else if (IsBlindSouthBoundarySectionForQuarterView(frame.SectionNumber) &&
                             hasSouthBoundarySegment &&
                             hasNorthBoundarySegment &&
                             emitQuarterVerify)
                    {
                        logger?.WriteLine(
                            $"VERIFY-QTR-EAST-SELECT sec={frame.SectionNumber} handle={frame.SectionId.Handle}: found=False");
                    }

                    var centerU = frame.MidU;
                    var centerV = frame.MidV;
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

                    if (emitQuarterVerify)
                    {
                        static (double U, double V) ToLocal(QuarterViewSectionFrame f, Point2d p)
                        {
                            var rel = p - f.Origin;
                            return (rel.DotProduct(f.EastUnit), rel.DotProduct(f.NorthUnit));
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

                    var promotedCorrectionSouthBoundary = false;
                    var promotedCorrectionSouthSource = string.Empty;
                    var promotedCurrentSouthError = double.NaN;
                    var promotedCorrectionSouthError = double.NaN;
                    if (!string.Equals(southSource, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase) &&
                        TryResolveQuarterViewSouthMostCorrectionBoundarySegment(
                            frame,
                            correctionSouthBoundarySegments,
                            out var promotedCorrectionSouthA,
                            out var promotedCorrectionSouthB))
                    {
                        const double minPromotionImprovement = 0.15;
                        double ResolveOutwardDistanceAtMidU(Point2d a, Point2d b, double fallbackV)
                        {
                            if (TryProjectBoundarySegmentVAtU(frame, a, b, centerU, out var projectedMidV))
                            {
                                return frame.SouthEdgeV - projectedMidV;
                            }

                            return frame.SouthEdgeV - fallbackV;
                        }

                        var candidateOutwardDistance = ResolveOutwardDistanceAtMidU(
                            promotedCorrectionSouthA,
                            promotedCorrectionSouthB,
                            southBoundaryV);
                        var currentOutwardDistance = hasSouthBoundarySegment
                            ? ResolveOutwardDistanceAtMidU(southBoundarySegmentA, southBoundarySegmentB, southBoundaryV)
                            : (frame.SouthEdgeV - southBoundaryV);
                        var candidateError = Math.Abs(candidateOutwardDistance - quarterDirectionInset);
                        var currentError = Math.Abs(currentOutwardDistance - quarterDirectionInset);
                        if (candidateError + minPromotionImprovement < currentError)
                        {
                            promotedCorrectionSouthBoundary = true;
                            promotedCorrectionSouthSource = southSource;
                            promotedCurrentSouthError = currentError;
                            promotedCorrectionSouthError = candidateError;
                            southBoundaryV = frame.SouthEdgeV - candidateOutwardDistance;
                            southSource = LayerUsecCorrectionZero;
                            southBoundarySegmentA = promotedCorrectionSouthA;
                            southBoundarySegmentB = promotedCorrectionSouthB;
                            hasSouthBoundarySegment = true;
                        }
                    }

                    var westBoundaryLimitU = Math.Min(frame.WestEdgeU, centerU - minQuarterSpan);
                    var eastBoundaryLimitU = centerU + minQuarterSpan;
                    var southBoundaryLimitV = Math.Min(frame.SouthEdgeV, centerV - minQuarterSpan);
                    var northBoundaryLimitV = centerV + minQuarterSpan;
                    var isCorrectionSouthBoundary = string.Equals(southSource, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase);
                    var isBlindNonCorrectionSouth = !isCorrectionSouthBoundary && southFallbackOffset <= 0.5;
                    var correctionSouthDividerU = dividerPreferredU;
                    double ClampWestBoundaryU(double candidateU) => Math.Min(candidateU, westBoundaryLimitU);
                    double ClampEastBoundaryU(double candidateU) => Math.Max(candidateU, eastBoundaryLimitU);
                    double ClampSouthBoundaryV(double candidateV) => Math.Min(candidateV, southBoundaryLimitV);
                    double ClampNorthBoundaryV(double candidateV) => Math.Max(candidateV, northBoundaryLimitV);

                    westBoundaryU = ClampWestBoundaryU(westBoundaryU);
                    eastBoundaryU = ClampEastBoundaryU(eastBoundaryU);
                    southBoundaryV = ClampSouthBoundaryV(southBoundaryV);
                    northBoundaryV = ClampNorthBoundaryV(northBoundaryV);

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

                    double ResolveSouthBoundaryVAtU(double targetU)
                    {
                        if (hasSouthBoundarySegment &&
                            TryProjectBoundarySegmentVAtU(frame, southBoundarySegmentA, southBoundarySegmentB, targetU, out var projectedV))
                        {
                            return ClampSouthBoundaryV(projectedV);
                        }

                        return ClampSouthBoundaryV(southBoundaryV);
                    }

                    double ResolveWestBoundaryUAtV(double targetV)
                    {
                        if (hasWestBoundarySegment &&
                            TryProjectBoundarySegmentUAtV(frame, westBoundarySegmentA, westBoundarySegmentB, targetV, out var projectedU))
                        {
                            return ClampWestBoundaryU(projectedU);
                        }

                        return ClampWestBoundaryU(westBoundaryU);
                    }

                    double ResolveNorthBoundaryVAtU(double targetU)
                    {
                        if (hasNorthBoundarySegment &&
                            TryProjectBoundarySegmentVAtU(frame, northBoundarySegmentA, northBoundarySegmentB, targetU, out var projectedV))
                        {
                            return ClampNorthBoundaryV(projectedV);
                        }

                        return ClampNorthBoundaryV(northBoundaryV);
                    }

                    double ResolveEastBoundaryUAtV(double targetV)
                    {
                        if (hasEastBoundarySegment &&
                            TryProjectBoundarySegmentUAtV(frame, eastBoundarySegmentA, eastBoundarySegmentB, targetV, out var projectedU))
                        {
                            return ClampEastBoundaryU(projectedU);
                        }

                        return ClampEastBoundaryU(eastBoundaryU);
                    }

                    var westAtMidU = ResolveWestBoundaryUAtV(centerV);
                    var eastAtMidU = ResolveEastBoundaryUAtV(centerV);
                    var eastAtMidV = centerV;
                    var southAtMidV = ResolveSouthBoundaryVAtU(centerU);
                    var westAtSouthU = ResolveWestBoundaryUAtV(southAtMidV);
                    var southAtWestV = ResolveSouthBoundaryVAtU(westAtSouthU);
                    var southAtEastU = ResolveEastBoundaryUAtV(southAtMidV);
                    var southAtEastV = ResolveSouthBoundaryVAtU(southAtEastU);
                    var northAtMidV = ResolveNorthBoundaryVAtU(centerU);
                    var westAtNorthU = ResolveWestBoundaryUAtV(northAtMidV);
                    var northAtWestV = ResolveNorthBoundaryVAtU(westAtNorthU);
                    var northAtEastU = ResolveEastBoundaryUAtV(northAtMidV);
                    var northAtEastV = ResolveNorthBoundaryVAtU(northAtEastU);
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
                        westAtMidU = ClampWestBoundaryU(westMidU);
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
                        eastAtMidU = ClampEastBoundaryU(eastMidU);
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
                        northAtMidV = ClampNorthBoundaryV(northMidV);
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
                    var northWestLockedByApparentIntersection = false;
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
                                if (emitQuarterVerify)
                                {
                                    logger?.WriteLine(
                                        $"VERIFY-QTR-SW-SW-APP sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                        $"u={westAtSouthU:0.###} v={southAtWestV:0.###} westOffset={westOffset:0.###} southOffset={southOffset:0.###} " +
                                        $"southSource={southSource} dividerSource={dividerSource}");
                                }
                            }
                        }

                        if (!southWestLockedByApparentIntersection &&
                            TryIntersectBoundarySegmentsLocal(
                                frame,
                                westBoundarySegmentA,
                                westBoundarySegmentB,
                                southBoundarySegmentA,
                                southBoundarySegmentB,
                                out var southWestU,
                                out var southWestV))
                        {
                            westAtSouthU = southWestU;
                            southAtWestV = southWestV;
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
                            if (southEndpoint.Y <= southBandMaxV)
                            {
                                westAtSouthU = southEndpoint.X;
                                southAtWestV = southEndpoint.Y;
                                southWestLockedByApparentIntersection = true;
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

                    var nwSnapPreferredU = westAtMidU;
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
                            northBand: true,
                            new Point2d(westAtNorthU, northAtWestV),
                            maxMove: hasRawNorthWestIntersection ? 5.0 : 90.0,
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
                            northBand: false,
                            new Point2d(westAtSouthU, southAtWestV),
                            maxMove: 5.0,
                            out var snappedWestSouthLocal,
                            out var snappedWestSouthPriority))
                    {
                        var southSnapMove = new Point2d(westAtSouthU, southAtWestV).GetDistanceTo(snappedWestSouthLocal);
                        if (snappedWestSouthPriority <= 0 && southSnapMove <= 5.0)
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

                    ApplyCorrectionSouthOverridesPreClamp(
                        frame,
                        boundarySegments,
                        correctionSouthBoundarySegments,
                        isCorrectionSouthBoundary,
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
                        ref southAtEastV);

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

                    westAtMidU = ClampWestBoundaryU(westAtMidU);
                    if (!southWestLockedByApparentIntersection)
                    {
                        westAtSouthU = ClampWestBoundaryU(westAtSouthU);
                    }

                    if (!northWestLockedByApparentIntersection)
                    {
                        westAtNorthU = ClampWestBoundaryU(westAtNorthU);
                    }
                    var westSouthV = southAtWestV;
                    var westNorthV = northAtWestV;
                    if (!southWestLockedByApparentIntersection)
                    {
                        westSouthV = ClampSouthBoundaryV(westSouthV);
                    }
                    if (!northWestLockedByApparentIntersection)
                    {
                        westNorthV = ClampNorthBoundaryV(westNorthV);
                    }
                    southAtMidV = ClampQuarterSouthBoundaryV(
                        southAtMidV,
                        isCorrectionSouthBoundary,
                        southBoundaryLimitV,
                        centerV,
                        minQuarterSpan);
                    northAtMidV = ClampNorthBoundaryV(northAtMidV);

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
                    if (hasEastBoundarySegment)
                    {
                        if (TryIntersectBoundarySegmentsLocal(
                                frame,
                                frame.LeftAnchor,
                                frame.RightAnchor,
                                eastBoundarySegmentA,
                                eastBoundarySegmentB,
                                out var eastMidUFinal,
                                out var eastMidVFinal) &&
                            Math.Abs(eastMidVFinal - centerV) <= dividerIntersectionDriftTolerance)
                        {
                            eastAtMidU = eastMidUFinal;
                            eastAtMidV = eastMidVFinal;
                        }
                        else
                        {
                            eastAtMidU = ResolveEastBoundaryUAtV(centerV);
                            eastAtMidV = centerV;
                        }

                        if (hasSouthBoundarySegment)
                        {
                            if (TryIntersectLocalInfiniteLines(
                                    frame,
                                    eastBoundarySegmentA,
                                    eastBoundarySegmentB,
                                    southBoundarySegmentA,
                                    southBoundarySegmentB,
                                    out var apparentSouthEastU,
                                    out var apparentSouthEastV))
                            {
                                var eastOffset = apparentSouthEastU - frame.EastEdgeU;
                                var southOffset = frame.SouthEdgeV - apparentSouthEastV;
                                const double minOffset = -6.0;
                                const double maxOffset = 90.0;
                                if (eastOffset >= -maxOffset &&
                                    eastOffset <= maxOffset &&
                                    southOffset >= minOffset &&
                                    southOffset <= maxOffset)
                                {
                                    southAtEastU = apparentSouthEastU;
                                    southAtEastV = apparentSouthEastV;
                                    southEastLockedByApparentIntersection = true;
                                    if (emitQuarterVerify)
                                    {
                                        logger?.WriteLine(
                                            $"VERIFY-QTR-SE-SE-APP sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                            $"u={southAtEastU:0.###} v={southAtEastV:0.###} eastOffset={eastOffset:0.###} southOffset={southOffset:0.###} " +
                                            $"eastSource={eastSource} southSource={southSource}");
                                    }
                                }
                            }

                            if (!southEastLockedByApparentIntersection &&
                                TryIntersectBoundarySegmentsLocal(
                                    frame,
                                    eastBoundarySegmentA,
                                    eastBoundarySegmentB,
                                    southBoundarySegmentA,
                                    southBoundarySegmentB,
                                    out var southEastU,
                                    out var southEastV))
                            {
                                southAtEastU = southEastU;
                                southAtEastV = southEastV;
                            }
                        }

                        if (hasNorthBoundarySegment)
                        {
                            var correctionAdjoiningForNorthEast =
                                isCorrectionSouthBoundary ||
                                string.Equals(northSource, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(northSource, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase);

                            if (!correctionAdjoiningForNorthEast)
                            {
                                if (TryIntersectBoundarySegmentsLocal(
                                        frame,
                                        eastBoundarySegmentA,
                                        eastBoundarySegmentB,
                                        northBoundarySegmentA,
                                        northBoundarySegmentB,
                                        out var strictNorthEastU,
                                        out var strictNorthEastV))
                                {
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
                            }
                            else
                            {
                                if (TryIntersectLocalInfiniteLines(
                                        frame,
                                        eastBoundarySegmentA,
                                        eastBoundarySegmentB,
                                        northBoundarySegmentA,
                                        northBoundarySegmentB,
                                        out var apparentNorthEastU,
                                        out var apparentNorthEastV))
                                {
                                    var eastOffset = apparentNorthEastU - frame.EastEdgeU;
                                    var northOffset = apparentNorthEastV - frame.NorthEdgeV;
                                    const double minOffset = -6.0;
                                    const double maxOffset = 90.0;
                                    if (eastOffset >= -maxOffset &&
                                        eastOffset <= maxOffset &&
                                        northOffset >= minOffset &&
                                        northOffset <= maxOffset)
                                    {
                                        northAtEastU = apparentNorthEastU;
                                        northAtEastV = apparentNorthEastV;
                                        northEastLockedByApparentIntersection = true;
                                        if (emitQuarterVerify)
                                        {
                                            logger?.WriteLine(
                                                $"VERIFY-QTR-NE-NE-APP sec={frame.SectionNumber} handle={frame.SectionId.Handle}: " +
                                                $"u={northAtEastU:0.###} v={northAtEastV:0.###} eastOffset={eastOffset:0.###} northOffset={northOffset:0.###} " +
                                                $"eastSource={eastSource} northSource={northSource}");
                                        }
                                    }
                                }

                                if (!northEastLockedByApparentIntersection &&
                                    TryIntersectBoundarySegmentsLocal(
                                        frame,
                                        eastBoundarySegmentA,
                                        eastBoundarySegmentB,
                                        northBoundarySegmentA,
                                        northBoundarySegmentB,
                                        out var northEastU,
                                        out var northEastV))
                                {
                                    northAtEastU = northEastU;
                                    northAtEastV = northEastV;
                                }
                            }
                        }

                        if (isBlindNonCorrectionSouth && hasNorthBoundarySegment)
                        {
                            var isCorrectionAdjoiningSection =
                                isCorrectionSouthBoundary ||
                                string.Equals(northSource, LayerUsecCorrection, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(northSource, LayerUsecCorrectionZero, StringComparison.OrdinalIgnoreCase);
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
                                    requireEndpointNode: !isCorrectionAdjoiningSection,
                                    out var endpointClusterLocal,
                                    out var endpointClusterPriority,
                                    out var endpointClusterMove,
                                    out var endpointClusterDistance))
                            {
                                northAtEastU = endpointClusterLocal.X;
                                northAtEastV = endpointClusterLocal.Y;
                                northEastLockedByApparentIntersection = true;
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
                                        northBand: true,
                                        new Point2d(northAtEastU, northAtEastV),
                                        maxMove: hasRawNorthEastIntersection ? 65.0 : 85.0,
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

                    eastAtMidU = ClampEastBoundaryU(eastAtMidU);
                    southAtEastU = ClampEastBoundaryU(southAtEastU);
                    northAtEastU = ClampEastBoundaryU(northAtEastU);
                    var eastSouthV = southAtEastV;
                    var eastNorthV = northAtEastV;
                    if (!southEastLockedByApparentIntersection)
                    {
                        eastSouthV = ClampSouthBoundaryV(eastSouthV);
                    }

                    if (!northEastLockedByApparentIntersection)
                    {
                        eastNorthV = ClampNorthBoundaryV(eastNorthV);
                    }

                    var swNw = QuarterViewLocalToWorld(frame, westAtMidU, westAtMidV);
                    var swNe = QuarterViewLocalToWorld(frame, centerU, centerV);
                    var swSe = QuarterViewLocalToWorld(frame, southAtMidU, southAtMidV);
                    var swSw = QuarterViewLocalToWorld(frame, westAtSouthU, westSouthV);
                    var seSw = swSe;
                    var seSe = QuarterViewLocalToWorld(frame, southAtEastU, eastSouthV);
                    var neNe = QuarterViewLocalToWorld(frame, northAtEastU, eastNorthV);
                    protectedSouthMidCorners.Add(swSe);
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
                        new Point2d(westAtMidU, westAtMidV),
                        new Point2d(centerU, centerV),
                        new Point2d(southAtMidU, southAtMidV),
                        new Point2d(westAtSouthU, westSouthV));

                    // SE: south RA only.
                    drawn += DrawQuarterViewPolygonFromLocal(
                        modelSpace,
                        transaction,
                        frame,
                        new Point2d(centerU, centerV),
                        new Point2d(eastAtMidU, eastAtMidV),
                        new Point2d(southAtEastU, eastSouthV),
                        new Point2d(southAtMidU, southAtMidV));

                    // NW: west RA only.
                    drawn += DrawQuarterViewPolygonFromLocal(
                        modelSpace,
                        transaction,
                        frame,
                        new Point2d(westAtNorthU, westNorthV),
                        new Point2d(northAtMidU, northAtMidV),
                        new Point2d(centerU, centerV),
                        new Point2d(westAtMidU, westAtMidV));

                    // NE: east-side apparent boundary + north boundary.
                    drawn += DrawQuarterViewPolygonFromLocal(
                        modelSpace,
                        transaction,
                        frame,
                        new Point2d(northAtMidU, northAtMidV),
                        new Point2d(northAtEastU, eastNorthV),
                        new Point2d(eastAtMidU, eastAtMidV),
                        new Point2d(centerU, centerV));

                    logger?.WriteLine(
                        $"Quarter view section {frame.SectionId.Handle}: west={westSource} ({westAtMidU:0.###}), east={eastSource} ({eastAtMidU:0.###}), south={southSource} ({southAtMidV:0.###}), north={northSource} ({northAtMidV:0.###}).");

                    protectedWestBoundaryCorners.Add(swSw);
                    protectedWestBoundaryCorners.Add(swNw);
                    protectedWestBoundaryCorners.Add(nwW);
                    if (hasEastBoundarySegment && hasSouthBoundarySegment)
                    {
                        protectedEastBoundaryCorners.Add(seSe);
                    }

                    if (hasEastBoundarySegment && hasNorthBoundarySegment)
                    {
                        protectedEastBoundaryCorners.Add(neNe);
                    }
                }

                var snappedVertices = 0;
                var snappedBoxes = 0;
                bool TryGetProtectedQuarterCorner(Point2d point, out Point2d corner)
                {
                    corner = default;
                    if (protectedSouthMidCorners.Count == 0 &&
                        protectedWestBoundaryCorners.Count == 0 &&
                        protectedEastBoundaryCorners.Count == 0)
                    {
                        return false;
                    }

                    const double tol = 0.05;
                    for (var i = 0; i < protectedSouthMidCorners.Count; i++)
                    {
                        var candidate = protectedSouthMidCorners[i];
                        if (point.GetDistanceTo(candidate) <= tol)
                        {
                            corner = candidate;
                            return true;
                        }
                    }

                    for (var i = 0; i < protectedWestBoundaryCorners.Count; i++)
                    {
                        var candidate = protectedWestBoundaryCorners[i];
                        if (point.GetDistanceTo(candidate) <= tol)
                        {
                            corner = candidate;
                            return true;
                        }
                    }

                    for (var i = 0; i < protectedEastBoundaryCorners.Count; i++)
                    {
                        var candidate = protectedEastBoundaryCorners[i];
                        if (point.GetDistanceTo(candidate) <= tol)
                        {
                            corner = candidate;
                            return true;
                        }
                    }

                    return false;
                }

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
                        if (TryGetProtectedQuarterCorner(vertex, out var protectedCorner))
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

                logger?.WriteLine($"Quarter view: refreshed {drawn} quarter box(es) across {frames.Count} section(s); erased {erased} stale box(es).");
                logger?.WriteLine($"Quarter view: endpoint snap adjusted {snappedVertices} vertex/vertices across {snappedBoxes} quarter box(es).");
                transaction.Commit();
            }
        }

        private static bool IsOutsideRange(double value, double min, double max)
        {
            return value < min || value > max;
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
            IReadOnlyList<(Point2d A, Point2d B)> correctionSouthBoundarySegments,
            bool isCorrectionSouthBoundary,
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
            ref double southAtEastV)
        {
            if (!isCorrectionSouthBoundary)
            {
                return;
            }

            if (TryResolveQuarterViewSouthMostCorrectionBoundarySegment(
                    frame,
                    correctionSouthBoundarySegments,
                    out var southCorrectionSouthA,
                    out var southCorrectionSouthB) &&
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
                if (TryIntersectLocalInfiniteLineWithBoundedSegmentExtension(
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
                else if (TryProjectBoundarySegmentVAtU(
                        frame,
                        southCorrectionSouthA,
                        southCorrectionSouthB,
                        dividerU,
                        out var forcedSouthMidV))
                {
                    southAtMidU = dividerU;
                    southAtMidV = forcedSouthMidV;
                }
                else if (TryIntersectBoundarySegmentsLocal(
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

            if (hasWestBoundarySegment &&
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

        private static bool TryResolveQuarterViewVerticalDividerSegmentFromQsec(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B)> dividerSegments,
            out Point2d segmentA,
            out Point2d segmentB)
        {
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
                    segmentA = seg.A;
                    segmentB = seg.B;
                }
            }

            return found;
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
            IReadOnlyList<(Point2d A, Point2d B)> correctionSegments,
            out double boundaryV,
            out Point2d segmentA,
            out Point2d segmentB)
        {
            boundaryV = default;
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
            const double preferSouthDefinitionThreshold = 12.0;
            var foundPreferredSouthDefinition = false;
            var bestPreferredScore = double.MaxValue;
            var bestPreferredOutwardDistance = double.MinValue;
            var preferredBoundaryV = default(double);
            var preferredSegmentA = default(Point2d);
            var preferredSegmentB = default(Point2d);
            var foundFallbackInset = false;
            var bestFallbackScore = double.MaxValue;
            var fallbackBoundaryV = default(double);
            var fallbackSegmentA = default(Point2d);
            var fallbackSegmentB = default(Point2d);
            for (var i = 0; i < correctionSegments.Count; i++)
            {
                var seg = correctionSegments[i];
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

                if (outwardDistance >= preferSouthDefinitionThreshold)
                {
                    // Quarter definitions at correction lines should follow the south hard boundary
                    // (L-USEC-C-0 south line) when both correction edges are present.
                    var score = Math.Abs(outwardDistance - RoadAllowanceSecWidthMeters) + centerPenalty;
                    if (!foundPreferredSouthDefinition ||
                        score < bestPreferredScore ||
                        (Math.Abs(score - bestPreferredScore) <= 1e-6 && outwardDistance > bestPreferredOutwardDistance))
                    {
                        foundPreferredSouthDefinition = true;
                        bestPreferredScore = score;
                        bestPreferredOutwardDistance = outwardDistance;
                        preferredBoundaryV = vAtMidU;
                        preferredSegmentA = seg.A;
                        preferredSegmentB = seg.B;
                    }
                }
                else
                {
                    var score = Math.Abs(outwardDistance - CorrectionLineInsetMeters) + centerPenalty;
                    if (!foundFallbackInset || score < bestFallbackScore)
                    {
                        foundFallbackInset = true;
                        bestFallbackScore = score;
                        fallbackBoundaryV = vAtMidU;
                        fallbackSegmentA = seg.A;
                        fallbackSegmentB = seg.B;
                    }
                }
            }

            if (foundPreferredSouthDefinition)
            {
                boundaryV = preferredBoundaryV;
                segmentA = preferredSegmentA;
                segmentB = preferredSegmentB;
                return true;
            }

            if (foundFallbackInset)
            {
                boundaryV = fallbackBoundaryV;
                segmentA = fallbackSegmentA;
                segmentB = fallbackSegmentB;
                return true;
            }

            return false;
        }

        private static bool TryResolveQuarterViewEastBoundarySegmentOnLayer(
            QuarterViewSectionFrame frame,
            IReadOnlyList<(Point2d A, Point2d B, string Layer)> segments,
            string targetLayer,
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
            const double minOffset = -60.0;
            const double maxOffset = 60.0;
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

                var offsetFromEast = uAtMidV - frame.EastEdgeU;
                if (offsetFromEast < minOffset || offsetFromEast > maxOffset)
                {
                    continue;
                }

                var score = Math.Abs(offsetFromEast);
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
                var score = (layerPriority * 100.0) +
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
            IReadOnlyList<(Point2d A, Point2d B)> correctionSegments,
            out Point2d segmentA,
            out Point2d segmentB)
        {
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
            var found = false;
            var bestScore = double.MaxValue;
            var bestOffsetError = double.MaxValue;
            var bestOutwardDistance = double.MinValue;
            for (var i = 0; i < correctionSegments.Count; i++)
            {
                var seg = correctionSegments[i];
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

                // Prefer the correction segment that best matches the intended south
                // road-allowance width; do not bias toward the absolute south-most line.
                var offsetError = Math.Abs(outwardDistance - RoadAllowanceSecWidthMeters);
                var southBias = Math.Max(0.0, outwardDistance - RoadAllowanceSecWidthMeters);
                var score = offsetError + (0.20 * southBias) + centerPenalty;
                if (!found ||
                    score < bestScore ||
                    (Math.Abs(score - bestScore) <= 1e-6 && offsetError < bestOffsetError) ||
                    (Math.Abs(score - bestScore) <= 1e-6 &&
                     Math.Abs(offsetError - bestOffsetError) <= 1e-6 &&
                     outwardDistance > bestOutwardDistance))
                {
                    found = true;
                    bestScore = score;
                    bestOffsetError = offsetError;
                    bestOutwardDistance = outwardDistance;
                    segmentA = seg.A;
                    segmentB = seg.B;
                }
            }

            return found;
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
            bool WithinExpandedBounds(Point2d a, Point2d b, double testU, double testV)
            {
                var minU = Math.Min(a.X, b.X) - apparentIntersectionPadding;
                var maxU = Math.Max(a.X, b.X) + apparentIntersectionPadding;
                var minV = Math.Min(a.Y, b.Y) - apparentIntersectionPadding;
                var maxV = Math.Max(a.Y, b.Y) + apparentIntersectionPadding;
                return testU >= minU && testU <= maxU && testV >= minV && testV <= maxV;
            }

            if (!WithinExpandedBounds(p, p2, uHit, vHit) || !WithinExpandedBounds(q, q2, uHit, vHit))
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
                bool IsPointInAnyScopedQuarter(Point2d p)
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

                bool IsSegmentOwnedByScopedQuarters(Point2d a, Point2d b)
                {
                    // Ownership for deferred redraw should follow the source quarter interior.
                    // Midpoint containment avoids deleting adjoining boundary-touching LSD lines.
                    var mid = Midpoint(a, b);
                    if (IsPointInAnyScopedQuarter(mid))
                    {
                        return true;
                    }

                    return IsPointInAnyScopedQuarter(a) && IsPointInAnyScopedQuarter(b);
                }

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

                    if (!IsSegmentOwnedByScopedQuarters(existingA, existingB))
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
